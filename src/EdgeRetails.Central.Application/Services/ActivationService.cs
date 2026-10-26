using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Common;
using EdgeRetails.Central.Domain.Licensing;
using EdgeRetails.Central.Domain.Outbox;
using EdgeRetails.Central.Domain.Signing;
using EdgeRetails.Central.Domain.Storage;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Central.Application.Services;

public class ActivationService : IActivationService
{
    private static readonly SemaphoreSlim BootstrapChallengeIssueLock = new(1, 1);
    private readonly ICentralDbContext _dbContext;
    private readonly IProtectedSignerClient _signingEngine;
    private readonly ICentralAuditService _auditService;
    private readonly ICanonicalJsonSerializer _serializer;
    private readonly IOperationLedgerService _operationLedger;
    private readonly IActivationDeliverySecretProtector? _activationSecretProtector;
    private readonly int? _bootstrapChallengeTtlSeconds;

    public ActivationService(
        ICentralDbContext dbContext,
        IProtectedSignerClient signingEngine,
        ICentralAuditService auditService,
        IOperationLedgerService? operationLedger = null,
        ICanonicalJsonSerializer? serializer = null,
        int? bootstrapChallengeTtlSeconds = null,
        IActivationDeliverySecretProtector? activationSecretProtector = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _signingEngine = signingEngine ?? throw new ArgumentNullException(nameof(signingEngine));
        _auditService = auditService ?? throw new ArgumentNullException(nameof(auditService));
        _serializer = serializer ?? new CanonicalJsonSerializer();
        _operationLedger = operationLedger ?? new OperationLedgerService(dbContext, _serializer);
        _bootstrapChallengeTtlSeconds = bootstrapChallengeTtlSeconds;
        _activationSecretProtector = activationSecretProtector;
    }

    public async Task<Installation> RegisterInstallationAsync(
        RegisterInstallationCommand command,
        CancellationToken cancellationToken = default)
    {
        var shop = await _dbContext.Shops.FindAsync(new object[] { command.ShopId }, cancellationToken)
            ?? throw new CentralDomainException("SHOP_NOT_FOUND", "install.shop_not_found", $"Shop {command.ShopId} does not exist.");

        var license = await _dbContext.CommercialLicenses.FindAsync(new object[] { command.LicenseId }, cancellationToken)
            ?? throw new CentralDomainException("LICENSE_NOT_FOUND", "install.license_not_found", $"License {command.LicenseId} does not exist.");

        if (command.CommandMeta is not null && command.CommandMeta.ClientOperationId != Guid.Empty)
        {
            var actorScope = $"shop:{command.ShopId}";
            var targetResource = $"installations/{command.MachineFingerprint}";

            var (_, installation) = await _operationLedger.ExecuteAndCaptureOrReplayAsync(
                actorScope: actorScope,
                shopId: command.ShopId,
                meta: command.CommandMeta,
                payload: command,
                executeAsync: async cmd =>
                {
                    var now = DateTimeOffset.UtcNow;
                    var inst = Installation.Register(
                        shop.Id,
                        license.Id,
                        cmd.MachineFingerprint,
                        cmd.PublicKeyPem,
                        cmd.Hostname,
                        cmd.OsPlatform,
                        now);

                    _dbContext.Installations.Add(inst);
                    await _dbContext.SaveChangesAsync(cancellationToken);

                    _dbContext.CentralOutboxMessages.Add(CentralOutboxMessage.Create(
                        command.CommandMeta.ClientOperationId,
                        "activation.installation_registered",
                        nameof(Installation),
                        inst.Id.ToString(),
                        _serializer.SerializeCanonical(new { inst.Id, inst.ShopId, inst.LicenseId, inst.PublicKeyFingerprintSha256, clientOperationId = command.CommandMeta.ClientOperationId }),
                        now));

                    await _auditService.RecordAuditAsync(
                        "INSTALLATION_REGISTERED",
                        "MACHINE",
                        null,
                        null,
                        cmd.CommandMeta?.Reason ?? "Installation registered",
                        command.CommandMeta.ClientOperationId,
                        command.CommandMeta.ClientOperationId,
                        nameof(Installation),
                        inst.Id.ToString(),
                        $"{{\"shopId\":\"{inst.ShopId}\",\"keyFingerprint\":\"{inst.PublicKeyFingerprintSha256}\"}}",
                        true,
                        command.CommandMeta.PayloadSha256,
                        cancellationToken);

                    return (inst, $"installations/{inst.Id}", 1, 1);
                },
                recoverResultAsync: async record =>
                {
                    var instId = Guid.Parse(record.ResultReference!.Split('/').Last());
                    return await _dbContext.Installations.FindAsync(new object[] { instId }, cancellationToken)
                        ?? throw new CentralDomainException("INSTALLATION_NOT_FOUND", "install.not_found", $"Installation {instId} not found during replay.");
                },
                targetResource: targetResource,
                cancellationToken: cancellationToken);

            return installation;
        }

        await using var securityTransaction = await SecurityTransaction.BeginAsync(_dbContext, command.ShopId.ToString(), cancellationToken);

        var nowDirect = DateTimeOffset.UtcNow;
        var installationDirect = Installation.Register(
            shop.Id,
            license.Id,
            command.MachineFingerprint,
            command.PublicKeyPem,
            command.Hostname,
            command.OsPlatform,
            nowDirect);

        _dbContext.Installations.Add(installationDirect);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.RecordAuditAsync(
            "INSTALLATION_REGISTERED",
            "MACHINE",
            null,
            null,
            command.CommandMeta?.Reason ?? "Installation registered",
            null,
            command.CommandMeta?.ClientOperationId,
            nameof(Installation),
            installationDirect.Id.ToString(),
            $"{{\"shopId\":\"{installationDirect.ShopId}\",\"keyFingerprint\":\"{installationDirect.PublicKeyFingerprintSha256}\"}}",
            true,
            null,
            cancellationToken);

        if (securityTransaction is not null)
        {
            await securityTransaction.CommitAsync(cancellationToken);
        }

        return installationDirect;
    }

    public async Task<OutcomeDto> RegisterBootstrapInstallationAsync(
        CommandMetaDto meta,
        InstallRegistrationPayloadDto payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(payload);
        if (!string.Equals(meta.OperationType, "installation.register", StringComparison.Ordinal) || meta.ClientOperationId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_OPERATION_META", "operation.invalid_meta", "Bootstrap installation registration requires operation type installation.register and a client operation ID.");
        }

        var challenge = await _dbContext.BootstrapChallenges.SingleOrDefaultAsync(x => x.Id == payload.BootstrapChallengeId, cancellationToken)
            ?? throw new CentralDomainException("INVALID_CHALLENGE", "bootstrap.challenge_invalid", "Bootstrap challenge does not exist.");
        var now = DateTimeOffset.UtcNow;
        if (challenge.ExpiresAtUtc <= now)
        {
            throw new CentralDomainException("CHALLENGE_EXPIRED", "bootstrap.challenge_expired", "Bootstrap challenge has expired.");
        }
        if (challenge.InstallationId != payload.InstallationId || challenge.ShopId != payload.ShopId || challenge.LicenseId != payload.LicenseId)
        {
            throw new CentralDomainException("BOOTSTRAP_BINDING_MISMATCH", "bootstrap.binding_mismatch", "Registration identity does not match the persisted challenge.");
        }

        var fingerprint = Installation.ComputePublicKeyFingerprintSha256(payload.InstallationPublicKey);
        if (!string.Equals(fingerprint, payload.InstallationPublicKeyFingerprint, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(fingerprint, challenge.InstallationPublicKeyFingerprintSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(payload.InstallationPublicKey.Trim(), challenge.InstallationPublicKeyPem, StringComparison.Ordinal))
        {
            throw new CentralDomainException("INSTALLATION_KEY_MISMATCH", "bootstrap.key_mismatch", "Registration key and fingerprint must match the challenge-bound key.");
        }

        var unsignedPayload = new
        {
            payload.ShopId,
            payload.LicenseId,
            payload.InstallationId,
            payload.InstallationPublicKey,
            payload.InstallationPublicKeyFingerprint,
            payload.BootstrapChallengeId,
            payload.DeviceIdentityVersion,
            payload.DeviceIdentityHash,
            payload.HardwareFingerprintVersion,
            payload.HardwareFingerprintHash,
            payload.SignalQuality,
            payload.ApplicationVersion
        };
        var (_, registrationPayloadDigest) = _serializer.SerializeAndHash(unsignedPayload);
        var proofEnvelope = _serializer.SerializeCanonical(new
        {
            profile = "CENTRAL_INSTALLATION_BOOTSTRAP_V1",
            purpose = "installation.register",
            httpMethod = "POST",
            httpPath = "/api/licensing/v1/primary/register",
            challengeId = challenge.Id,
            challengeNonce = challenge.ChallengeNonce,
            requestNonce = challenge.RequestNonce,
            activationTokenHash = challenge.ActivationTokenHashSha256,
            payloadDigest = registrationPayloadDigest
        });
        if (!_signingEngine.VerifySignature(challenge.InstallationPublicKeyPem, proofEnvelope, payload.ProofOfPossession))
        {
            throw new CentralDomainException("INVALID_PROOF_OF_POSSESSION", "bootstrap.proof_invalid", "Installation registration proof of possession is invalid.");
        }

        var targetResource = $"installation:{payload.InstallationId:D}";
        return await _operationLedger.ExecuteOrReplayAsync<InstallRegistrationPayloadDto, Guid>(
            actorScope: $"bootstrap:{payload.InstallationId:D}",
            shopId: challenge.ShopId,
            meta: meta,
            payload: payload,
            executeAsync: async cmd =>
            {
                var currentChallenge = await _dbContext.BootstrapChallenges.SingleAsync(x => x.Id == cmd.BootstrapChallengeId, cancellationToken);
                var token = await _dbContext.ActivationTokens.SingleOrDefaultAsync(
                    x => x.TokenHashSha256 == currentChallenge.ActivationTokenHashSha256,
                    cancellationToken)
                    ?? throw new CentralDomainException("BOOTSTRAP_NOT_ELIGIBLE", "bootstrap.not_eligible", "Activation token or commercial license is not eligible for bootstrap.");
                var license = await _dbContext.CommercialLicenses.FindAsync(new object[] { token.LicenseId }, cancellationToken)
                    ?? throw new CentralDomainException("BOOTSTRAP_NOT_ELIGIBLE", "bootstrap.not_eligible", "Activation token or commercial license is not eligible for bootstrap.");
                if (token.Status != ActivationToken.StatusIssued || token.FirstRedemptionExpiresAtUtc <= DateTimeOffset.UtcNow ||
                    token.ShopId != cmd.ShopId || token.LicenseId != cmd.LicenseId || license.ShopId != cmd.ShopId ||
                    license.Status != CommercialLicense.StatusActive || currentChallenge.ShopId != cmd.ShopId || currentChallenge.LicenseId != cmd.LicenseId)
                {
                    throw new CentralDomainException("BOOTSTRAP_NOT_ELIGIBLE", "bootstrap.not_eligible", "Activation token or commercial license is not eligible for bootstrap.");
                }

                if (await _dbContext.Installations.AnyAsync(x => x.Id == cmd.InstallationId, cancellationToken))
                {
                    throw new CentralDomainException("INSTALLATION_ALREADY_REGISTERED", "bootstrap.installation_exists", "Installation identity is already registered.");
                }

                currentChallenge.ConsumeForRegistration(
                    cmd.InstallationId,
                    cmd.ShopId,
                    cmd.LicenseId,
                    token.TokenHashSha256,
                    fingerprint,
                    DateTimeOffset.UtcNow);
                var installation = Installation.RegisterBootstrap(
                    cmd.InstallationId,
                    cmd.ShopId,
                    cmd.LicenseId,
                    cmd.HardwareFingerprintHash,
                    currentChallenge.InstallationPublicKeyPem,
                    cmd.DeviceIdentityVersion,
                    cmd.DeviceIdentityHash,
                    cmd.HardwareFingerprintVersion,
                    cmd.SignalQuality,
                    cmd.ApplicationVersion,
                    DateTimeOffset.UtcNow);
                _dbContext.Installations.Add(installation);
                await _dbContext.SaveChangesAsync(cancellationToken);

                _dbContext.CentralOutboxMessages.Add(CentralOutboxMessage.Create(
                    meta.ClientOperationId,
                    "activation.installation_registered",
                    nameof(Installation),
                    installation.Id.ToString("D"),
                    _serializer.SerializeCanonical(new
                    {
                        installation.Id,
                        installation.ShopId,
                        installation.LicenseId,
                        installation.PublicKeyFingerprintSha256,
                        installation.HardwareFingerprintHash,
                        meta.ClientOperationId
                    }),
                    DateTimeOffset.UtcNow));
                await _auditService.RecordAuditAsync(
                    "INSTALLATION_REGISTERED",
                    "MACHINE_BOOTSTRAP",
                    null,
                    null,
                    meta.Reason ?? "Challenge-bound installation registration",
                    meta.ClientOperationId,
                    meta.ClientOperationId,
                    nameof(Installation),
                    installation.Id.ToString("D"),
                    _serializer.SerializeCanonical(new { installation.ShopId, installation.LicenseId, installation.PublicKeyFingerprintSha256 }),
                    true,
                    meta.PayloadSha256,
                    cancellationToken);

                return (installation.Id, installation.Id.ToString("D"), installation.RowVersion, 1);
            },
            targetResource: targetResource,
            cancellationToken: cancellationToken);
    }

    public async Task<OutcomeDto> RedeemBootstrapActivationAsync(
        CommandMetaDto meta,
        BootstrapActivationRedeemPayloadDto payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(payload);
        if (!string.Equals(meta.OperationType, "activation.redeem", StringComparison.Ordinal) || meta.ClientOperationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(payload.ActivationToken) || payload.ActivationToken.Length > 512 ||
            payload.InstallationId == Guid.Empty || payload.ValidationNonce == Guid.Empty || payload.BootstrapChallengeId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_BOOTSTRAP_REDEMPTION", "bootstrap.redemption_invalid", "Bootstrap activation redemption payload or operation identity is invalid.");
        }

        var tokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload.ActivationToken.Trim())));
        var challenge = await _dbContext.BootstrapChallenges.SingleOrDefaultAsync(x => x.Id == payload.BootstrapChallengeId, cancellationToken)
            ?? throw new CentralDomainException("INVALID_CHALLENGE", "bootstrap.challenge_invalid", "Bootstrap challenge does not exist.");
        var installation = await _dbContext.Installations.FindAsync(new object[] { payload.InstallationId }, cancellationToken)
            ?? throw new CentralDomainException("INSTALLATION_NOT_FOUND", "redemption.install_not_found", "Installation is not registered.");
        var token = await _dbContext.ActivationTokens.SingleOrDefaultAsync(x => x.TokenHashSha256 == tokenHash, cancellationToken)
            ?? throw new CentralDomainException("BOOTSTRAP_NOT_ELIGIBLE", "bootstrap.not_eligible", "Activation token or installation is not eligible.");
        if (payload.HardwareFingerprintHash != installation.HardwareFingerprintHash ||
            !string.Equals(payload.InstallationPublicKeyFingerprint, installation.PublicKeyFingerprintSha256, StringComparison.OrdinalIgnoreCase) ||
            challenge.InstallationId != installation.Id || challenge.ShopId != installation.ShopId || challenge.LicenseId != installation.LicenseId ||
            challenge.ActivationTokenHashSha256 != tokenHash || challenge.InstallationPublicKeyFingerprintSha256 != installation.PublicKeyFingerprintSha256)
        {
            throw new CentralDomainException("BOOTSTRAP_BINDING_MISMATCH", "bootstrap.binding_mismatch", "Activation redemption does not match the challenge-bound token, installation, shop, key, or hardware fingerprint.");
        }

        var unsignedPayload = new
        {
            payload.ActivationToken,
            payload.InstallationId,
            payload.HardwareFingerprintHash,
            payload.InstallationPublicKeyFingerprint,
            payload.ValidationNonce,
            payload.BootstrapChallengeId
        };
        var (_, payloadDigest) = _serializer.SerializeAndHash(unsignedPayload);
        var proofEnvelope = _serializer.SerializeCanonical(new
        {
            profile = "CENTRAL_INSTALLATION_BOOTSTRAP_V1",
            purpose = "activation.redeem",
            httpMethod = "POST",
            httpPath = "/api/licensing/v1/activation/redeem",
            challengeId = challenge.Id,
            challengeNonce = challenge.ChallengeNonce,
            requestNonce = challenge.RequestNonce,
            validationNonce = payload.ValidationNonce,
            activationTokenHash = challenge.ActivationTokenHashSha256,
            payloadDigest
        });
        if (!_signingEngine.VerifySignature(challenge.InstallationPublicKeyPem, proofEnvelope, payload.ProofOfPossession))
        {
            throw new CentralDomainException("INVALID_PROOF_OF_POSSESSION", "bootstrap.proof_invalid", "Activation redemption proof of possession is invalid.");
        }

        return await _operationLedger.ExecuteOrReplayAsync<BootstrapActivationRedeemPayloadDto, Guid>(
            actorScope: $"installation:{installation.Id:D}",
            shopId: installation.ShopId,
            meta: meta,
            payload: payload,
            executeAsync: async command =>
            {
                var currentChallenge = await _dbContext.BootstrapChallenges.SingleAsync(x => x.Id == command.BootstrapChallengeId, cancellationToken);
                var currentToken = await _dbContext.ActivationTokens.SingleAsync(x => x.TokenHashSha256 == tokenHash, cancellationToken);
                var currentInstallation = await _dbContext.Installations.SingleAsync(x => x.Id == command.InstallationId, cancellationToken);
                var license = await _dbContext.CommercialLicenses.FindAsync(new object[] { currentToken.LicenseId }, cancellationToken)
                    ?? throw new CentralDomainException("BOOTSTRAP_NOT_ELIGIBLE", "bootstrap.not_eligible", "Activation token or installation is not eligible.");
                var now = DateTimeOffset.UtcNow;
                if (currentToken.Status != ActivationToken.StatusIssued || currentToken.FirstRedemptionExpiresAtUtc <= now ||
                    license.Status != CommercialLicense.StatusActive || currentToken.ShopId != currentInstallation.ShopId ||
                    currentToken.LicenseId != currentInstallation.LicenseId || currentInstallation.Status != Installation.StatusRegistered ||
                    currentInstallation.HardwareFingerprintHash != command.HardwareFingerprintHash)
                {
                    throw new CentralDomainException("BOOTSTRAP_NOT_ELIGIBLE", "bootstrap.not_eligible", "Activation token or installation is not eligible.");
                }

                currentChallenge.ConsumeForRedemption(
                    currentInstallation.Id,
                    currentInstallation.ShopId,
                    currentInstallation.LicenseId,
                    currentToken.TokenHashSha256,
                    currentInstallation.PublicKeyFingerprintSha256,
                    currentChallenge.ChallengeNonce,
                    now);
                currentToken.MarkRedeemed(now);
                currentToken.MoveToPendingApproval();
                currentInstallation.MoveToPendingApproval();
                var validationSnapshot = CreateActivationValidationSnapshot(currentToken, currentInstallation, license);
                var redemption = ActivationRedemption.Create(
                    currentToken.Id,
                    currentInstallation.Id,
                    currentChallenge.ChallengeNonce,
                    command.ProofOfPossession,
                    now,
                    validationSnapshot,
                    ActivationValidationSnapshotVersion);
                _dbContext.ActivationRedemptions.Add(redemption);
                await _dbContext.SaveChangesAsync(cancellationToken);
                _dbContext.CentralOutboxMessages.Add(CentralOutboxMessage.Create(
                    meta.ClientOperationId,
                    "activation.token_redeemed",
                    nameof(ActivationRedemption),
                    redemption.Id.ToString("D"),
                    _serializer.SerializeCanonical(new { redemption.Id, redemption.TokenId, redemption.InstallationId, meta.ClientOperationId }),
                    now));
                await _auditService.RecordAuditAsync(
                    "ACTIVATION_TOKEN_REDEEMED",
                    "MACHINE_BOOTSTRAP",
                    null,
                    null,
                    meta.Reason ?? "Challenge-bound activation redemption",
                    meta.ClientOperationId,
                    meta.ClientOperationId,
                    nameof(ActivationRedemption),
                    redemption.Id.ToString("D"),
                    _serializer.SerializeCanonical(new { redemption.TokenId, redemption.InstallationId, currentToken.ShopId }),
                    true,
                    meta.PayloadSha256,
                    cancellationToken);
                return (redemption.Id, $"redemptions/{redemption.Id:D}", 1, 1);
            },
            targetResource: $"activation-token:{token.Id:D}",
            cancellationToken: cancellationToken);
    }

    public async Task<ActivationIssuedDto> IssueActivationTokenAsync(
        IssueActivationTokenCommand command,
        Guid adminId,
        CancellationToken cancellationToken = default)
    {
        var license = await _dbContext.CommercialLicenses.FindAsync(new object[] { command.LicenseId }, cancellationToken)
            ?? throw new CentralDomainException("LICENSE_NOT_FOUND", "token.license_not_found", $"License {command.LicenseId} does not exist.");

        var shop = await _dbContext.Shops.FindAsync(new object[] { command.ShopId }, cancellationToken)
            ?? throw new CentralDomainException("SHOP_NOT_FOUND", "token.shop_not_found", $"Shop {command.ShopId} does not exist.");

        await EnsureActivationTokenIssuanceEligibilityAsync(license.Id, shop.Id, cancellationToken);

        if (command.CommandMeta is not null && command.CommandMeta.ClientOperationId != Guid.Empty)
        {
            var actorScope = $"admin:{adminId}";
            var targetResource = $"licenses/{command.LicenseId}/tokens";

            var (_, tokenDto) = await _operationLedger.ExecuteAndCaptureOrReplayAsync(
                actorScope: actorScope,
                shopId: command.ShopId,
                meta: command.CommandMeta,
                payload: command,
                executeAsync: async cmd =>
                {
                    await EnsureActivationTokenIssuanceEligibilityAsync(license.Id, shop.Id, cancellationToken);

                    byte[] secretBytes = RandomNumberGenerator.GetBytes(32);
                    string tokenSecret = Convert.ToHexString(secretBytes).ToLowerInvariant();

                    byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(tokenSecret));
                    string tokenHash = Convert.ToHexString(hashBytes).ToLowerInvariant();

                    var now = DateTimeOffset.UtcNow;
                    var ttl = cmd.FirstRedemptionTtlSeconds > 0 ? cmd.FirstRedemptionTtlSeconds : 86400;
                    var expiresAt = now.AddSeconds(ttl);

                    var token = ActivationToken.Issue(
                        license.Id,
                        shop.Id,
                        tokenHash,
                        expiresAt,
                        adminId,
                        now);

                    _dbContext.ActivationTokens.Add(token);
                    await _dbContext.SaveChangesAsync(cancellationToken);

                    _dbContext.CentralOutboxMessages.Add(CentralOutboxMessage.Create(
                        command.CommandMeta.ClientOperationId,
                        "activation.token_issued",
                        nameof(ActivationToken),
                        token.Id.ToString(),
                        _serializer.SerializeCanonical(new { token.Id, token.LicenseId, token.ShopId, clientOperationId = command.CommandMeta.ClientOperationId }),
                        now));

                    await _auditService.RecordAuditAsync(
                        "ACTIVATION_TOKEN_ISSUED",
                        "ADMIN",
                        adminId,
                        null,
                        cmd.CommandMeta?.Reason ?? "Activation token issued",
                        command.CommandMeta.ClientOperationId,
                        command.CommandMeta.ClientOperationId,
                        nameof(ActivationToken),
                        token.Id.ToString(),
                        $"{{\"shopId\":\"{token.ShopId}\",\"expiresAt\":\"{token.FirstRedemptionExpiresAtUtc}\"}}",
                        true,
                        command.CommandMeta.PayloadSha256,
                        cancellationToken);

                    var result = new ActivationIssuedDto
                    {
                        TokenId = token.Id,
                        TokenSecret = tokenSecret,
                        FirstRedemptionExpiresAtUtc = token.FirstRedemptionExpiresAtUtc
                    };

                    return (result, $"tokens/{token.Id}", 1, 1);
                },
                recoverResultAsync: async record =>
                {
                    var tokenId = Guid.Parse(record.ResultReference!.Split('/').Last());
                    var tok = await _dbContext.ActivationTokens.FindAsync(new object[] { tokenId }, cancellationToken)
                        ?? throw new CentralDomainException("TOKEN_NOT_FOUND", "token.not_found", $"Token {tokenId} not found during replay.");
                    return new ActivationIssuedDto
                    {
                        TokenId = tok.Id,
                        TokenSecret = string.Empty,
                        FirstRedemptionExpiresAtUtc = tok.FirstRedemptionExpiresAtUtc
                    };
                },
                targetResource: targetResource,
                cancellationToken: cancellationToken);

            return tokenDto;
        }

        await using var securityTransaction = await SecurityTransaction.BeginAsync(_dbContext, command.LicenseId.ToString(), cancellationToken);
        await EnsureActivationTokenIssuanceEligibilityAsync(license.Id, shop.Id, cancellationToken);

        // Generate unguessable 256-bit token secret
        byte[] secretBytesDirect = RandomNumberGenerator.GetBytes(32);
        string tokenSecretDirect = Convert.ToHexString(secretBytesDirect).ToLowerInvariant();

        byte[] hashBytesDirect = SHA256.HashData(Encoding.UTF8.GetBytes(tokenSecretDirect));
        string tokenHashDirect = Convert.ToHexString(hashBytesDirect).ToLowerInvariant();

        var nowDirectToken = DateTimeOffset.UtcNow;
        var ttlDirect = command.FirstRedemptionTtlSeconds > 0 ? command.FirstRedemptionTtlSeconds : 86400;
        var expiresAtDirect = nowDirectToken.AddSeconds(ttlDirect);

        var tokenDirect = ActivationToken.Issue(
            license.Id,
            shop.Id,
            tokenHashDirect,
            expiresAtDirect,
            adminId,
            nowDirectToken);

        _dbContext.ActivationTokens.Add(tokenDirect);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.RecordAuditAsync(
            "ACTIVATION_TOKEN_ISSUED",
            "ADMIN",
            adminId,
            null,
            command.CommandMeta?.Reason ?? "Activation token issued",
            null,
            command.CommandMeta?.ClientOperationId,
            nameof(ActivationToken),
            tokenDirect.Id.ToString(),
            $"{{\"shopId\":\"{tokenDirect.ShopId}\",\"expiresAt\":\"{tokenDirect.FirstRedemptionExpiresAtUtc}\"}}",
            true,
            null,
            cancellationToken);

        if (securityTransaction is not null)
        {
            await securityTransaction.CommitAsync(cancellationToken);
        }

        return new ActivationIssuedDto
        {
            TokenId = tokenDirect.Id,
            TokenSecret = tokenSecretDirect,
            FirstRedemptionExpiresAtUtc = tokenDirect.FirstRedemptionExpiresAtUtc
        };
    }

    public async Task<ActivationIssuedContractDto> IssueCanonicalActivationTokenAsync(
        JsonElement payload,
        CanonicalDirectoryMeta meta,
        Guid adminId,
        Guid? adminSessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(meta);
        if (_activationSecretProtector is null)
        {
            throw new CentralDomainException("ACTIVATION_DELIVERY_NOT_CONFIGURED", "activation.delivery_not_configured", "A protected activation-delivery adapter is required.");
        }
        if (payload.ValueKind != JsonValueKind.Object || adminId == Guid.Empty || meta.OperationType != "activation.issue" ||
            meta.ClientOperationId == Guid.Empty || meta.FingerprintVersion != 1 || meta.ExpectedVersion < 1 || meta.PolicyVersion < 1 ||
            string.IsNullOrWhiteSpace(meta.Reason))
        {
            throw new CentralDomainException("INVALID_ACTIVATION_ISSUE", "activation.issue_invalid", "Canonical activation issuance metadata is invalid.");
        }

        if (!TryReadGuid(payload, "licenseId", out var licenseId) || !TryReadGuid(payload, "installationId", out var installationId) ||
            !TryReadGuid(payload, "tokenPolicyId", out var tokenPolicyId) || !TryReadGuid(payload, "deliveryChannelId", out var deliveryChannelId) ||
            !TryReadString(payload, "installationPublicKeyFingerprint", out var keyFingerprint) ||
            !TryReadString(payload, "hardwareFingerprintHash", out var hardwareFingerprint))
        {
            throw new CentralDomainException("INVALID_ACTIVATION_ISSUE", "activation.issue_invalid", "Canonical activation issuance payload is incomplete.");
        }

        var configuredPolicyId = ReadRequiredGuidConfiguration("CENTRAL_ACTIVATION_TOKEN_POLICY_ID");
        var configuredChannelId = ReadRequiredGuidConfiguration("CENTRAL_ACTIVATION_DELIVERY_CHANNEL_ID");
        var configuredPolicyVersion = ReadRequiredIntConfiguration("CENTRAL_ACTIVATION_TOKEN_POLICY_VERSION", minimum: 1);
        var ttlSeconds = ReadRequiredIntConfiguration("CENTRAL_ACTIVATION_FIRST_REDEMPTION_TTL_SECONDS", minimum: 1);
        if (tokenPolicyId != configuredPolicyId || deliveryChannelId != configuredChannelId || meta.PolicyVersion != configuredPolicyVersion)
        {
            throw new CentralDomainException("ACTIVATION_POLICY_NOT_CONFIGURED", "activation.policy_not_configured", "The request must match the explicitly configured activation policy and delivery channel.");
        }

        var initialLicense = await _dbContext.CommercialLicenses.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == licenseId, cancellationToken)
            ?? throw new CentralDomainException("LICENSE_NOT_FOUND", "token.license_not_found", "The activation license does not exist.");
        var initialInstallation = await _dbContext.Installations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == installationId, cancellationToken)
            ?? throw new CentralDomainException("INSTALLATION_NOT_FOUND", "install.not_found", "The activation installation does not exist.");
        ValidateActivationIssueBinding(initialLicense, initialInstallation, keyFingerprint, hardwareFingerprint);
        if (initialLicense.RowVersion != meta.ExpectedVersion)
        {
            throw new CentralDomainException("CONCURRENCY_CONFLICT", "operation.concurrency_conflict", "The license changed before activation issuance.");
        }

        return (await _operationLedger.ExecuteAndCaptureOrReplayAsync<JsonElement, ActivationIssuedContractDto>(
            $"admin:{adminId:D}", initialLicense.ShopId, meta.ToLedgerMeta(), payload,
            async _ =>
            {
                var license = await _dbContext.CommercialLicenses.SingleOrDefaultAsync(x => x.Id == licenseId && x.ShopId == initialLicense.ShopId, cancellationToken)
                    ?? throw new CentralDomainException("LICENSE_NOT_FOUND", "token.license_not_found", "The license no longer matches the original shop scope.");
                var installation = await _dbContext.Installations.SingleOrDefaultAsync(x => x.Id == installationId && x.ShopId == license.ShopId && x.LicenseId == license.Id, cancellationToken)
                    ?? throw new CentralDomainException("INSTALLATION_SCOPE_MISMATCH", "install.scope_mismatch", "The installation does not belong to the persisted license and shop.");
                ValidateActivationIssueBinding(license, installation, keyFingerprint, hardwareFingerprint);
                if (license.RowVersion != meta.ExpectedVersion)
                {
                    throw new CentralDomainException("CONCURRENCY_CONFLICT", "operation.concurrency_conflict", "The license changed before activation issuance.");
                }
                var paidOrder = await _dbContext.Orders.AsNoTracking().SingleOrDefaultAsync(x => x.Id == license.InitialOrderId && x.ShopId == license.ShopId, cancellationToken);
                if (paidOrder is null || paidOrder.Status != Order.StatusPaidApproved)
                {
                    throw new CentralDomainException("COMMERCIAL_ELIGIBILITY_REQUIRED", "token.commercial_eligibility_required", "Activation issuance requires a license bound to an approved paid order.");
                }

                var now = DateTimeOffset.UtcNow;
                var expiresAt = now.AddSeconds(ttlSeconds);
                var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
                var secretHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();
                var token = ActivationToken.Issue(license.Id, license.ShopId, secretHash, expiresAt, adminId, now);
                var deliveryReference = $"activation-deliveries/{token.Id:D}";
                var context = $"activation:{token.Id:D}:{license.ShopId:D}:{installation.Id:D}:{deliveryChannelId:D}";
                var protectedSecret = _activationSecretProtector.Protect(secret, context);
                token.BindPendingDelivery(tokenPolicyId, deliveryChannelId, deliveryReference, protectedSecret.KeyId, protectedSecret.Ciphertext);
                _dbContext.ActivationTokens.Add(token);
                await _dbContext.SaveChangesAsync(cancellationToken);

                _dbContext.CentralOutboxMessages.Add(CentralOutboxMessage.Create(meta.ClientOperationId,
                    "activation.delivery_pending", nameof(ActivationToken), token.Id.ToString("D"),
                    _serializer.SerializeCanonical(new { activationTokenId = token.Id, installationId, shopId = license.ShopId, tokenPolicyId, deliveryChannelId, deliveryReference, expiresAtUtc = expiresAt, clientOperationId = meta.ClientOperationId }), now));
                await _auditService.RecordAuditAsync("ACTIVATION_TOKEN_ISSUED", "ADMIN", adminId, adminSessionId,
                    meta.Reason, meta.ClientOperationId, meta.ClientOperationId, nameof(ActivationToken), token.Id.ToString("D"),
                    _serializer.SerializeCanonical(new { token.Id, token.ShopId, installationId, tokenPolicyId, deliveryChannelId, deliveryReference, token.DeliveryState, token.FirstRedemptionExpiresAtUtc }),
                    true, meta.PayloadSha256, cancellationToken);

                return (new ActivationIssuedContractDto
                {
                    ClientOperationId = meta.ClientOperationId,
                    ActivationTokenId = token.Id,
                    DeliveryReference = deliveryReference,
                    ExpiresAtUtc = expiresAt,
                    State = token.DeliveryState
                }, $"tokens/{token.Id:D}", token.RowVersion, configuredPolicyVersion);
            },
            record => RecoverCanonicalActivationIssueAsync(record, meta.ClientOperationId, cancellationToken),
            targetResource: $"licenses/{licenseId:D}/activation-issue/{installationId:D}", cancellationToken: cancellationToken)).Result;
    }

    public async Task<int> ExpirePendingActivationDeliverySecretsAsync(int batchSize, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        if (batchSize is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        var expired = await _dbContext.ActivationTokens
            .Where(token => token.Status == ActivationToken.StatusIssued && token.FirstRedemptionExpiresAtUtc < nowUtc && token.ProtectedDeliverySecret != null)
            .OrderBy(token => token.FirstRedemptionExpiresAtUtc)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
        foreach (var token in expired)
        {
            token.ExpireDeliverySecretIfWindowElapsed(nowUtc);
        }

        return expired.Count == 0 ? 0 : await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<ActivationIssuedContractDto> RecoverCanonicalActivationIssueAsync(
        EdgeRetails.Central.Domain.Operations.CentralOperationRecord record,
        Guid clientOperationId,
        CancellationToken cancellationToken)
    {
        var tokenIdText = record.ResultReference?.Split('/').LastOrDefault();
        if (!Guid.TryParse(tokenIdText, out var tokenId))
        {
            throw new CentralDomainException("ACTIVATION_DELIVERY_REFERENCE_INVALID", "activation.delivery_reference_invalid", "The persisted activation delivery reference is invalid.");
        }
        var token = await _dbContext.ActivationTokens.AsNoTracking().SingleOrDefaultAsync(x => x.Id == tokenId, cancellationToken)
            ?? throw new CentralDomainException("ACTIVATION_TOKEN_NOT_FOUND", "token.not_found", "The persisted activation token is missing.");
        return new ActivationIssuedContractDto
        {
            ClientOperationId = clientOperationId,
            ActivationTokenId = token.Id,
            DeliveryReference = token.DeliveryReference ?? $"activation-deliveries/{token.Id:D}",
            ExpiresAtUtc = token.FirstRedemptionExpiresAtUtc,
            State = token.DeliveryState
        };
    }

    private static void ValidateActivationIssueBinding(CommercialLicense license, Installation installation, string keyFingerprint, string hardwareFingerprint)
    {
        var now = DateTimeOffset.UtcNow;
        if (license.Status != CommercialLicense.StatusActive || license.ValidFromUtc > now || (license.ValidUntilUtc.HasValue && license.ValidUntilUtc.Value <= now))
        {
            throw new CentralDomainException("LICENSE_NOT_ELIGIBLE", "token.license_not_eligible", "Activation requires an active commercially valid license.");
        }
        if (installation.LicenseId != license.Id || installation.ShopId != license.ShopId ||
            installation.Status is Installation.StatusRevoked or Installation.StatusReplaced ||
            !string.Equals(installation.PublicKeyFingerprintSha256, keyFingerprint, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(installation.HardwareFingerprintHash, hardwareFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new CentralDomainException("INSTALLATION_BINDING_MISMATCH", "token.installation_binding_mismatch", "Installation, shop, license, public-key and hardware identities must match persisted server records.");
        }
    }

    private static bool TryReadGuid(JsonElement payload, string name, out Guid value)
    {
        value = Guid.Empty;
        return payload.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && Guid.TryParse(property.GetString(), out value) && value != Guid.Empty;
    }

    private static bool TryReadString(JsonElement payload, string name, out string value)
    {
        value = string.Empty;
        if (!payload.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
        {
            return false;
        }
        value = property.GetString()!;
        return true;
    }

    private static Guid ReadRequiredGuidConfiguration(string key)
    {
        if (!Guid.TryParse(Environment.GetEnvironmentVariable(key), out var value) || value == Guid.Empty)
        {
            throw new CentralDomainException("ACTIVATION_POLICY_NOT_CONFIGURED", "activation.policy_not_configured", $"Explicit {key} configuration is required.");
        }
        return value;
    }

    private async Task EnsureActivationTokenIssuanceEligibilityAsync(
        Guid licenseId,
        Guid requestedShopId,
        CancellationToken cancellationToken)
    {
        var license = await _dbContext.CommercialLicenses.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == licenseId, cancellationToken)
            ?? throw new CentralDomainException("LICENSE_NOT_FOUND", "token.license_not_found", "Activation license does not exist.");
        var shop = await _dbContext.Shops.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == requestedShopId, cancellationToken)
            ?? throw new CentralDomainException("SHOP_NOT_FOUND", "token.shop_not_found", "Activation shop does not exist.");
        if (license.ShopId != shop.Id || license.CustomerId != shop.CustomerId)
        {
            throw new CentralDomainException("SHOP_MISMATCH", "token.shop_mismatch", "Activation shop does not match the persisted license owner.");
        }

        var now = DateTimeOffset.UtcNow;
        if (license.Status != CommercialLicense.StatusActive || license.ValidFromUtc > now ||
            (license.ValidUntilUtc.HasValue && license.ValidUntilUtc.Value <= now))
        {
            throw new CentralDomainException("LICENSE_NOT_ELIGIBLE", "token.license_not_eligible", "Activation requires an active commercially valid license.");
        }

        var paidOrder = await _dbContext.Orders.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == license.InitialOrderId && x.ShopId == license.ShopId && x.CustomerId == license.CustomerId,
            cancellationToken);
        if (paidOrder is null || paidOrder.Status != Order.StatusPaidApproved)
        {
            throw new CentralDomainException("COMMERCIAL_ELIGIBILITY_REQUIRED", "token.commercial_eligibility_required", "Activation issuance requires a license bound to an approved paid order.");
        }
    }

    private static int ReadRequiredIntConfiguration(string key, int minimum)
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable(key), out var value) || value < minimum)
        {
            throw new CentralDomainException("ACTIVATION_POLICY_NOT_CONFIGURED", "activation.policy_not_configured", $"Explicit supported {key} configuration is required.");
        }
        return value;
    }

    public async Task<BootstrapChallengeDto> GenerateBootstrapChallengeAsync(
        BootstrapChallengeRequestDto request,
        CancellationToken cancellationToken = default)
    {
        if (request.InstallationId == Guid.Empty || request.RequestNonce == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_BOOTSTRAP_BINDING", "bootstrap.binding_invalid", "Installation ID and client request nonce are required.");
        }

        if (string.IsNullOrWhiteSpace(request.InstallationPublicKey) ||
            request.ActivationTokenHash is not { Length: 64 } || !request.ActivationTokenHash.All(Uri.IsHexDigit))
        {
            throw new CentralDomainException("INVALID_BOOTSTRAP_BINDING", "bootstrap.binding_invalid", "A public key and SHA-256 activation-token hash are required.");
        }

        var lifetimeSeconds = _bootstrapChallengeTtlSeconds;
        if (lifetimeSeconds is null && int.TryParse(Environment.GetEnvironmentVariable("CENTRAL_BOOTSTRAP_CHALLENGE_TTL_SECONDS"), out var configuredSeconds))
        {
            lifetimeSeconds = configuredSeconds;
        }
        if (lifetimeSeconds is null or < 1 or > 3600)
        {
            throw new CentralDomainException("BOOTSTRAP_POLICY_NOT_CONFIGURED", "bootstrap.policy_unavailable", "An explicit supported bootstrap challenge lifetime is required.");
        }

        var submittedFingerprint = Installation.ComputePublicKeyFingerprintSha256(request.InstallationPublicKey);
        using (var rsa = RSA.Create())
        {
            try
            {
                rsa.ImportFromPem(request.InstallationPublicKey);
                if (rsa.KeySize < 2048)
                {
                    throw new CentralDomainException("INVALID_PUBLIC_KEY", "bootstrap.key_invalid", "Bootstrap public key must be at least 2048 bits.");
                }
            }
            catch (CentralDomainException)
            {
                throw;
            }
            catch (CryptographicException ex)
            {
                throw new CentralDomainException("INVALID_PUBLIC_KEY", "bootstrap.key_invalid", "Bootstrap public key is malformed.", ex);
            }
        }

        var activationTokenHash = request.ActivationTokenHash.ToLowerInvariant();
        var token = await _dbContext.ActivationTokens.SingleOrDefaultAsync(
            x => x.TokenHashSha256 == activationTokenHash,
            cancellationToken)
            ?? throw new CentralDomainException("BOOTSTRAP_NOT_ELIGIBLE", "bootstrap.not_eligible", "Activation token or commercial license is not eligible for bootstrap.");
        var now = DateTimeOffset.UtcNow;
        var license = await _dbContext.CommercialLicenses.FindAsync(new object[] { token.LicenseId }, cancellationToken)
            ?? throw new CentralDomainException("LICENSE_NOT_FOUND", "bootstrap.license_not_found", "Activation license does not exist.");
        if (token.Status != ActivationToken.StatusIssued || token.FirstRedemptionExpiresAtUtc <= now || license.Status != CommercialLicense.StatusActive ||
            license.ShopId != token.ShopId || (request.ShopId != Guid.Empty && request.ShopId != token.ShopId))
        {
            throw new CentralDomainException("BOOTSTRAP_NOT_ELIGIBLE", "bootstrap.not_eligible", "Activation token or commercial license is not eligible for bootstrap.");
        }

        var install = await _dbContext.Installations.FindAsync(new object[] { request.InstallationId }, cancellationToken);
        var installationAlreadyRegistered = install is not null;
        if (install is not null &&
            (install.ShopId != token.ShopId || install.LicenseId != token.LicenseId ||
             !string.Equals(install.PublicKeyFingerprintSha256, submittedFingerprint, StringComparison.OrdinalIgnoreCase) ||
             install.Status is Installation.StatusRevoked or Installation.StatusReplaced))
        {
            throw new CentralDomainException("BOOTSTRAP_BINDING_MISMATCH", "bootstrap.binding_mismatch", "Existing installation identity does not match the eligible token and submitted key.");
        }

        // The database uniqueness constraint remains authoritative across API
        // instances; this short gate serializes same-process issuance races.
        await BootstrapChallengeIssueLock.WaitAsync(cancellationToken);
        try
        {
            if (await _dbContext.BootstrapChallenges.AnyAsync(x => x.RequestNonce == request.RequestNonce, cancellationToken))
            {
                throw new CentralDomainException("BOOTSTRAP_REQUEST_REPLAYED", "bootstrap.request_replayed", "Bootstrap request nonce has already been used.");
            }

            var nonceBytes = RandomNumberGenerator.GetBytes(32);
            var challengeNonce = Convert.ToBase64String(nonceBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var expiresAt = now.AddSeconds(lifetimeSeconds.Value);
            var challenge = BootstrapChallengeRecord.Issue(
                request.InstallationId,
                token.ShopId,
                token.LicenseId,
                token.TokenHashSha256,
                request.InstallationPublicKey,
                submittedFingerprint,
                request.RequestNonce,
                challengeNonce,
                now,
                expiresAt,
                installationAlreadyRegistered);
            _dbContext.BootstrapChallenges.Add(challenge);
            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // The preflight lookup above improves the common replay response, but two
                // concurrent requests can both pass it. The unique RequestNonce index is
                // the authority in that race; translate its winner/loser outcome into the
                // same safe contract response as a sequential replay.
                if (await _dbContext.BootstrapChallenges.AsNoTracking()
                        .AnyAsync(x => x.RequestNonce == request.RequestNonce, cancellationToken))
                {
                    throw new CentralDomainException(
                        "BOOTSTRAP_REQUEST_REPLAYED",
                        "bootstrap.request_replayed",
                        "Bootstrap request nonce has already been used.");
                }

                throw;
            }

            return new BootstrapChallengeDto
            {
                ChallengeId = challenge.Id,
                InstallationId = request.InstallationId,
                ChallengeNonce = challengeNonce,
                ExpiresAtUtc = expiresAt,
                AllowedOperations = new[] { "installation.register", "activation.redeem", "activation.status" }
            };
        }
        finally
        {
            BootstrapChallengeIssueLock.Release();
        }
    }

    public async Task<ActivationRedemption> RedeemActivationTokenAsync(
        RedeemActivationTokenCommand command,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.ChallengeNonce) || command.ChallengeNonce.Length > 128)
        {
            throw new CentralDomainException("INVALID_CHALLENGE", "redemption.invalid_challenge", "Bootstrap challenge nonce is missing or exceeds the supported bound.");
        }

        // The nonce hash and bindings are looked up in persisted Central state below;
        // no embedded claim or process-local HMAC secret is trusted as authority.
        var now = DateTimeOffset.UtcNow;

        // 2. Validate token and installation
        await using var securityTransaction = await SecurityTransaction.BeginAsync(_dbContext, command.InstallationId.ToString(), cancellationToken);

        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(command.TokenSecret.Trim()));
        string tokenHash = Convert.ToHexString(hashBytes).ToLowerInvariant();

        var token = await _dbContext.ActivationTokens
            .FirstOrDefaultAsync(t => t.Id == command.TokenId && t.TokenHashSha256 == tokenHash, cancellationToken)
            ?? throw new CentralDomainException("INVALID_TOKEN", "redemption.invalid_token", "Activation token secret is invalid or does not match.");

        var installation = await _dbContext.Installations.FindAsync(new object[] { command.InstallationId }, cancellationToken)
            ?? throw new CentralDomainException("INSTALLATION_NOT_FOUND", "redemption.install_not_found", $"Installation {command.InstallationId} does not exist.");

        // Verify client signature of challenge nonce using installation's public key
        bool sigValid = _signingEngine.VerifySignature(
            installation.PublicKeyPem,
            command.ChallengeNonce,
            command.ClientSignatureHex);

        if (!sigValid)
        {
            throw new CentralDomainException("INVALID_CLIENT_SIGNATURE", "redemption.invalid_signature", "Client challenge response signature is invalid.");
        }

        if (token.ShopId != installation.ShopId)
        {
            throw new CentralDomainException("SHOP_MISMATCH", "redemption.shop_mismatch", "Token shop does not match installation shop.");
        }

        if (token.LicenseId != installation.LicenseId)
        {
            throw new CentralDomainException("LICENSE_MISMATCH", "redemption.license_mismatch", "Token license does not match installation license.");
        }

        var challengeNonceHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(command.ChallengeNonce)));
        var bootstrapChallenge = await _dbContext.BootstrapChallenges.SingleOrDefaultAsync(
            x => x.ChallengeNonceHashSha256 == challengeNonceHash,
            cancellationToken)
            ?? throw new CentralDomainException("INVALID_CHALLENGE", "redemption.invalid_challenge", "Bootstrap challenge is unknown or invalid.");

        var license = await _dbContext.CommercialLicenses.FindAsync(new object[] { token.LicenseId }, cancellationToken)
            ?? throw new CentralDomainException("LICENSE_NOT_FOUND", "redemption.license_not_found", "License not found.");

        if (license.Status == CommercialLicense.StatusRevoked)
        {
            throw new CentralDomainException("LICENSE_REVOKED", "redemption.license_revoked", "License is revoked.");
        }

        if (license.Status != CommercialLicense.StatusActive)
        {
            throw new CentralDomainException("LICENSE_NOT_ACTIVE", "redemption.license_not_active", $"License status {license.Status} is not eligible for redemption.");
        }

        if (now > token.FirstRedemptionExpiresAtUtc)
        {
            throw new CentralDomainException("TOKEN_EXPIRED", "redemption.token_expired", "Activation token has expired.");
        }

        if (token.Status != ActivationToken.StatusIssued)
        {
            throw new CentralDomainException("TOKEN_ALREADY_REDEEMED", "redemption.token_already_redeemed", "Activation token has already been redeemed or consumed.");
        }

        bootstrapChallenge.ConsumeForRedemption(
            installation.Id,
            installation.ShopId,
            installation.LicenseId,
            tokenHash,
            installation.PublicKeyFingerprintSha256,
            command.ChallengeNonce,
            now);

        if (command.CommandMeta is not null && command.CommandMeta.ClientOperationId != Guid.Empty)
        {
            var actorScope = $"machine:{command.InstallationId}";
            var targetResource = $"tokens/{command.TokenId}";

            var (_, redemptionRecord) = await _operationLedger.ExecuteAndCaptureOrReplayAsync(
                actorScope: actorScope,
                shopId: installation.ShopId,
                meta: command.CommandMeta,
                payload: command,
                executeAsync: async cmd =>
                {
                    token.MarkRedeemed(now);
                    token.MoveToPendingApproval();
                    installation.MoveToPendingApproval();
                    var validationSnapshot = CreateActivationValidationSnapshot(token, installation, license);

                    var red = ActivationRedemption.Create(
                        token.Id,
                        installation.Id,
                        cmd.ChallengeNonce,
                        cmd.ClientSignatureHex,
                        now,
                        validationSnapshot,
                        ActivationValidationSnapshotVersion);

                    _dbContext.ActivationRedemptions.Add(red);
                    await _dbContext.SaveChangesAsync(cancellationToken);

                    _dbContext.CentralOutboxMessages.Add(CentralOutboxMessage.Create(
                        command.CommandMeta.ClientOperationId,
                        "activation.token_redeemed",
                        nameof(ActivationRedemption),
                        red.Id.ToString(),
                        _serializer.SerializeCanonical(new { red.Id, red.TokenId, red.InstallationId, clientOperationId = command.CommandMeta.ClientOperationId }),
                        now));

                    await _auditService.RecordAuditAsync(
                        "ACTIVATION_TOKEN_REDEEMED",
                        "MACHINE",
                        null,
                        null,
                        cmd.CommandMeta?.Reason ?? "Token redeemed with challenge signature",
                        command.CommandMeta.ClientOperationId,
                        command.CommandMeta.ClientOperationId,
                        nameof(ActivationRedemption),
                        red.Id.ToString(),
                        $"{{\"tokenId\":\"{token.Id}\",\"installationId\":\"{installation.Id}\"}}",
                        true,
                        command.CommandMeta.PayloadSha256,
                        cancellationToken);

                    return (red, $"redemptions/{red.Id}", 1, 1);
                },
                recoverResultAsync: async record =>
                {
                    var redId = Guid.Parse(record.ResultReference!.Split('/').Last());
                    return await _dbContext.ActivationRedemptions.FindAsync(new object[] { redId }, cancellationToken)
                        ?? throw new CentralDomainException("REDEMPTION_NOT_FOUND", "redemption.not_found", $"Redemption {redId} not found during replay.");
                },
                targetResource: targetResource,
                cancellationToken: cancellationToken);

            if (securityTransaction is not null)
            {
                await securityTransaction.CommitAsync(cancellationToken);
            }

            return redemptionRecord;
        }

        token.MarkRedeemed(now);
        token.MoveToPendingApproval();
        installation.MoveToPendingApproval();
        var directValidationSnapshot = CreateActivationValidationSnapshot(token, installation, license);

        var redemption = ActivationRedemption.Create(
            token.Id,
            installation.Id,
            command.ChallengeNonce,
            command.ClientSignatureHex,
            now,
            directValidationSnapshot,
            ActivationValidationSnapshotVersion);

        _dbContext.ActivationRedemptions.Add(redemption);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.RecordAuditAsync(
            "ACTIVATION_TOKEN_REDEEMED",
            "MACHINE",
            null,
            null,
            command.CommandMeta?.Reason ?? "Token redeemed with challenge signature",
            null,
            command.CommandMeta?.ClientOperationId,
            nameof(ActivationRedemption),
            redemption.Id.ToString(),
            $"{{\"tokenId\":\"{token.Id}\",\"installationId\":\"{installation.Id}\"}}",
            true,
            null,
            cancellationToken);

        if (securityTransaction is not null)
        {
            await securityTransaction.CommitAsync(cancellationToken);
        }

        return redemption;
    }

    public async Task ReviewActivationAsync(
        Guid redemptionId,
        ReviewActivationCommand command,
        Guid adminId,
        CancellationToken cancellationToken = default)
    {
        var redemption = await _dbContext.ActivationRedemptions.FindAsync(new object[] { redemptionId }, cancellationToken)
            ?? throw new CentralDomainException("REDEMPTION_NOT_FOUND", "review.redemption_not_found", $"Redemption {redemptionId} does not exist.");

        var token = await _dbContext.ActivationTokens.FindAsync(new object[] { redemption.TokenId }, cancellationToken)
            ?? throw new CentralDomainException("TOKEN_NOT_FOUND", "review.token_not_found", "Associated token was not found.");

        var installation = await _dbContext.Installations.FindAsync(new object[] { redemption.InstallationId }, cancellationToken)
            ?? throw new CentralDomainException("INSTALLATION_NOT_FOUND", "review.install_not_found", "Associated installation was not found.");

        await using var securityTransaction = await SecurityTransaction.BeginAsync(_dbContext, redemptionId.ToString(), cancellationToken);

        var now = DateTimeOffset.UtcNow;

        if (command.CommandMeta is not null && command.CommandMeta.ClientOperationId != Guid.Empty)
        {
            var actorScope = $"admin:{adminId}";
            var targetResource = $"redemptions/{redemptionId}/review";

            await _operationLedger.ExecuteOrReplayAsync(
                actorScope: actorScope,
                shopId: installation.ShopId,
                meta: command.CommandMeta,
                payload: command,
                executeAsync: async cmd =>
                {
                    if (string.Equals(cmd.Decision, "APPROVE", StringComparison.OrdinalIgnoreCase))
                    {
                        redemption.Approve();
                        token.Consume(now);
                        installation.Approve(now);
                    }
                    else
                    {
                        redemption.Reject();
                        token.Reject();
                    }

                    await _dbContext.SaveChangesAsync(cancellationToken);

                    _dbContext.CentralOutboxMessages.Add(CentralOutboxMessage.Create(
                        command.CommandMeta.ClientOperationId,
                        "activation.review_decided",
                        nameof(ActivationRedemption),
                        redemption.Id.ToString(),
                        _serializer.SerializeCanonical(new { redemption.Id, decision = cmd.Decision, clientOperationId = command.CommandMeta.ClientOperationId }),
                        now));

                    await _auditService.RecordAuditAsync(
                        "ACTIVATION_REVIEW_DECIDED",
                        "ADMIN",
                        adminId,
                        null,
                        cmd.CommandMeta?.Reason ?? $"Activation review decided: {cmd.Decision}",
                        command.CommandMeta.ClientOperationId,
                        command.CommandMeta.ClientOperationId,
                        nameof(ActivationRedemption),
                        redemption.Id.ToString(),
                        $"{{\"decision\":\"{cmd.Decision}\",\"installationId\":\"{installation.Id}\"}}",
                        true,
                        command.CommandMeta.PayloadSha256,
                        cancellationToken);

                    return (true, $"redemptions/{redemption.Id}", 1, 1);
                },
                targetResource: targetResource,
                cancellationToken: cancellationToken);

            if (securityTransaction is not null)
            {
                await securityTransaction.CommitAsync(cancellationToken);
            }

            return;
        }

        if (string.Equals(command.Decision, "APPROVE", StringComparison.OrdinalIgnoreCase))
        {
            redemption.Approve();
            token.Consume(now);
            installation.Approve(now);
        }
        else
        {
            redemption.Reject();
            token.Reject();
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.RecordAuditAsync(
            "ACTIVATION_REVIEW_DECIDED",
            "ADMIN",
            adminId,
            null,
            command.CommandMeta?.Reason ?? $"Activation review decided: {command.Decision}",
            null,
            command.CommandMeta?.ClientOperationId,
            nameof(ActivationRedemption),
            redemption.Id.ToString(),
            $"{{\"decision\":\"{command.Decision}\",\"installationId\":\"{installation.Id}\"}}",
            true,
            null,
            cancellationToken);

        if (securityTransaction is not null)
        {
            await securityTransaction.CommitAsync(cancellationToken);
        }
    }

    private const int ActivationValidationSnapshotVersion = 1;

    public async Task<OutcomeDto> ReviewCanonicalActivationAsync(
        Guid redemptionId,
        CanonicalDirectoryMeta meta,
        CanonicalActivationDecisionDto decision,
        Guid adminId,
        Guid? adminSessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(decision);
        if (redemptionId == Guid.Empty || decision.ActivationRequestId != redemptionId || adminId == Guid.Empty ||
            meta.OperationType != "activation.review" || meta.ClientOperationId == Guid.Empty || meta.FingerprintVersion != 1 ||
            meta.ExpectedVersion < 1 || meta.PolicyVersion < 0 || string.IsNullOrWhiteSpace(meta.Reason) ||
            decision.ValidationSnapshotVersion < 1 || string.IsNullOrWhiteSpace(decision.ReasonCode))
        {
            throw new CentralDomainException("INVALID_ACTIVATION_REVIEW", "activation.review_invalid", "Activation review identity or command metadata is invalid.");
        }

        var redemption = await _dbContext.ActivationRedemptions.SingleOrDefaultAsync(x => x.Id == redemptionId, cancellationToken)
            ?? throw new CentralDomainException("ACTIVATION_REQUEST_NOT_FOUND", "activation.request_not_found", "The activation request does not exist.");
        var token = await _dbContext.ActivationTokens.SingleOrDefaultAsync(x => x.Id == redemption.TokenId, cancellationToken)
            ?? throw new CentralDomainException("TOKEN_NOT_FOUND", "review.token_not_found", "The activation token does not exist.");
        var installation = await _dbContext.Installations.SingleOrDefaultAsync(x => x.Id == redemption.InstallationId, cancellationToken)
            ?? throw new CentralDomainException("INSTALLATION_NOT_FOUND", "review.install_not_found", "The installation does not exist.");
        var license = await _dbContext.CommercialLicenses.SingleOrDefaultAsync(x => x.Id == token.LicenseId, cancellationToken)
            ?? throw new CentralDomainException("LICENSE_NOT_FOUND", "review.license_not_found", "The commercial license does not exist.");

        if (token.ShopId != license.ShopId || installation.ShopId != license.ShopId || installation.LicenseId != license.Id)
        {
            throw new CentralDomainException("ACTIVATION_SCOPE_MISMATCH", "activation.scope_mismatch", "Activation request ownership does not match its persisted license and installation.");
        }
        if (redemption.ValidationSnapshotJson is null || redemption.ValidationSnapshotVersion is null ||
            decision.ValidationSnapshotVersion != redemption.ValidationSnapshotVersion.Value)
        {
            throw new CentralDomainException("VALIDATION_SNAPSHOT_MISMATCH", "activation.snapshot_mismatch", "The submitted validation snapshot version does not match persisted activation evidence.");
        }

        var actorScope = $"admin:{adminId:D}";
        var targetResource = $"activation_requests/{redemptionId:D}/review";
        return await _operationLedger.ExecuteOrReplayAsync<CanonicalActivationDecisionDto, Guid>(
            actorScope,
            license.ShopId,
            meta.ToLedgerMeta(),
            decision,
            async cmd =>
            {
                if (meta.ExpectedVersion != redemption.RowVersion)
                {
                    throw new CentralDomainException("CONCURRENCY_CONFLICT", "operation.concurrency_conflict", "The activation request changed before review.");
                }
                if (redemption.Status != ActivationRedemption.StatusPending)
                {
                    throw new CentralDomainException("ACTIVATION_NOT_PENDING", "activation.not_pending", "Only a pending activation request can be reviewed.");
                }

                // The immutable persisted snapshot is compared against the current Central facts before approval.
                var currentSnapshot = CreateActivationValidationSnapshot(token, installation, license);
                if (!string.Equals(currentSnapshot, redemption.ValidationSnapshotJson, StringComparison.Ordinal))
                {
                    throw new CentralDomainException("ACTIVATION_VALIDATION_CHANGED", "activation.validation_changed", "Activation eligibility changed after the request was submitted; refresh the validation evidence before deciding.");
                }

                var submittedDecision = cmd.Decision?.Trim().ToUpperInvariant();
                var normalizedDecision = submittedDecision switch
                {
                    "APPROVE" => ActivationRedemption.StatusApproved,
                    "REJECT" => ActivationRedemption.StatusRejected,
                    ActivationRedemption.StatusRequestInformation => ActivationRedemption.StatusRequestInformation,
                    ActivationRedemption.StatusOpenHardwareReview => ActivationRedemption.StatusOpenHardwareReview,
                    _ => string.Empty
                };
                if (normalizedDecision is not (ActivationRedemption.StatusApproved or ActivationRedemption.StatusRejected or
                    ActivationRedemption.StatusRequestInformation or ActivationRedemption.StatusOpenHardwareReview))
                {
                    throw new CentralDomainException("INVALID_ACTIVATION_DECISION", "activation.invalid_decision", "The activation decision is not supported.");
                }

                var now = DateTimeOffset.UtcNow;
                if (normalizedDecision == ActivationRedemption.StatusApproved)
                {
                    if (license.Status != CommercialLicense.StatusActive || token.Status != ActivationToken.StatusPendingApproval ||
                        token.FirstRedemptionExpiresAtUtc <= redemption.RedeemedAtUtc || installation.Status != Installation.StatusPendingApproval)
                    {
                        throw new CentralDomainException("ACTIVATION_NOT_ELIGIBLE", "activation.not_eligible", "The activation is no longer commercially or technically eligible for approval.");
                    }
                    token.Consume(now);
                    installation.Approve(now);
                }
                else if (normalizedDecision == ActivationRedemption.StatusRejected)
                {
                    token.Reject();
                }

                HardwareReview? hardwareReview = null;
                if (normalizedDecision == ActivationRedemption.StatusOpenHardwareReview)
                {
                    using var snapshotDocument = JsonDocument.Parse(redemption.ValidationSnapshotJson);
                    if (!snapshotDocument.RootElement.TryGetProperty("reportedHardwareFingerprintHash", out var reportedElement) ||
                        reportedElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(reportedElement.GetString()) ||
                        !snapshotDocument.RootElement.TryGetProperty("hardwareFingerprintHash", out var storedElement) ||
                        storedElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(storedElement.GetString()) ||
                        string.Equals(reportedElement.GetString(), storedElement.GetString(), StringComparison.OrdinalIgnoreCase))
                    {
                        throw new CentralDomainException("HARDWARE_REVIEW_EVIDENCE_REQUIRED", "activation.hardware_evidence_required", "A distinct persisted reported fingerprint is required before opening a hardware review.");
                    }
                    hardwareReview = HardwareReview.Create(license.ShopId, installation.Id,
                        reportedElement.GetString()!, storedElement.GetString()!, cmd.ReasonCode, now);
                    _dbContext.HardwareReviews.Add(hardwareReview);
                }

                redemption.Decide(normalizedDecision, cmd.ReasonCode, adminId, now,
                    redemption.ValidationSnapshotJson, redemption.ValidationSnapshotVersion.Value);

                await _dbContext.SaveChangesAsync(cancellationToken);
                var eventType = hardwareReview is not null ? "activation.hardware_review_requested" : "activation.review_decided";
                _dbContext.CentralOutboxMessages.Add(CentralOutboxMessage.Create(
                    meta.ClientOperationId,
                    eventType,
                    hardwareReview is not null ? nameof(HardwareReview) : nameof(ActivationRedemption),
                    hardwareReview?.Id.ToString("D") ?? redemption.Id.ToString("D"),
                    _serializer.SerializeCanonical(new
                    {
                        activationRequestId = redemption.Id,
                        hardwareReviewId = hardwareReview?.Id,
                        redemption.TokenId,
                        redemption.InstallationId,
                        shopId = license.ShopId,
                        decision = normalizedDecision,
                        reasonCode = cmd.ReasonCode,
                        validationSnapshotVersion = redemption.ValidationSnapshotVersion,
                        clientOperationId = meta.ClientOperationId
                    }),
                    now));
                await _auditService.RecordAuditAsync(
                    "ACTIVATION_REVIEW_DECIDED",
                    "ADMIN",
                    adminId,
                    adminSessionId,
                    meta.Reason,
                    meta.ClientOperationId,
                    meta.ClientOperationId,
                    nameof(ActivationRedemption),
                    redemption.Id.ToString("D"),
                    _serializer.SerializeCanonical(new
                    {
                        decision = normalizedDecision,
                        reasonCode = cmd.ReasonCode,
                        redemption.InstallationId,
                        validationSnapshotVersion = redemption.ValidationSnapshotVersion
                    }),
                    true,
                    meta.PayloadSha256,
                    cancellationToken);
                return (redemption.Id, $"activation_requests/{redemption.Id:D}", redemption.RowVersion, license.AuthorityRevision);
            },
            targetResource,
            cancellationToken);
    }

    private string CreateActivationValidationSnapshot(ActivationToken token, Installation installation, CommercialLicense license) =>
        _serializer.SerializeCanonical(new
        {
            version = ActivationValidationSnapshotVersion,
            tokenId = token.Id,
            tokenStatus = token.Status,
            tokenRowVersion = token.RowVersion,
            tokenExpiresAtUtc = NormalizeSnapshotTimestamp(token.FirstRedemptionExpiresAtUtc),
            installationId = installation.Id,
            installationStatus = installation.Status,
            installationRowVersion = installation.RowVersion,
            installationShopId = installation.ShopId,
            installationLicenseId = installation.LicenseId,
            installationPublicKeyFingerprint = installation.PublicKeyFingerprintSha256,
            hardwareFingerprintVersion = installation.HardwareFingerprintVersion,
            reportedHardwareFingerprintHash = installation.HardwareFingerprintHash,
            hardwareFingerprintHash = installation.HardwareFingerprintHash,
            licenseId = license.Id,
            licenseShopId = license.ShopId,
            licenseStatus = license.Status,
            licenseAuthorityRevision = license.AuthorityRevision,
            licenseRowVersion = license.RowVersion
        });

    private static DateTimeOffset NormalizeSnapshotTimestamp(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % 10), TimeSpan.Zero);
    }
    public async Task<LicenseCertificate> IssueCertificateAsync(
        Guid installationId,
        Guid adminId,
        CancellationToken cancellationToken = default)
    {
        var installationIdentity = await _dbContext.Installations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == installationId, cancellationToken)
            ?? throw new CentralDomainException("INSTALLATION_NOT_FOUND", "cert.install_not_found", $"Installation {installationId} does not exist.");

        // Lifecycle mutations serialize on the license ID while installation changes serialize on
        // the installation ID. Acquire both in a stable order so revocation or replacement cannot
        // race certificate signing and persistence.
        var securityTransaction = await SecurityTransaction.BeginAsync(_dbContext, installationIdentity.LicenseId.ToString(), cancellationToken);
        await SecurityTransaction.BeginAsync(_dbContext, installationId.ToString(), cancellationToken);

        var installation = await _dbContext.Installations.FindAsync(new object[] { installationId }, cancellationToken)
            ?? throw new CentralDomainException("INSTALLATION_NOT_FOUND", "cert.install_not_found", $"Installation {installationId} does not exist.");

        if (installation.Status != Installation.StatusActive)
        {
            throw new CentralDomainException("INSTALLATION_NOT_ACTIVE", "cert.install_not_active", $"Installation is in status {installation.Status}; must be ACTIVE to issue certificate.");
        }

        var license = await _dbContext.CommercialLicenses.FindAsync(new object[] { installation.LicenseId }, cancellationToken)
            ?? throw new CentralDomainException("LICENSE_NOT_FOUND", "cert.license_not_found", $"License {installation.LicenseId} does not exist.");

        if (license.Status != CommercialLicense.StatusActive)
        {
            throw new CentralDomainException("LICENSE_NOT_ACTIVE", "cert.license_not_active", "An active commercial license is required to issue a certificate.");
        }

        var eligibilityTimeUtc = DateTimeOffset.UtcNow;
        if (license.ValidFromUtc > eligibilityTimeUtc || (license.ValidUntilUtc.HasValue && license.ValidUntilUtc.Value <= eligibilityTimeUtc))
        {
            throw new CentralDomainException("LICENSE_NOT_ELIGIBLE", "cert.license_not_eligible", "A commercially valid license is required to issue a certificate.");
        }

        var policy = await GetVerifiedLicensePolicyAsync(installation.ShopId, license.Id, cancellationToken);

        // Invariant: Strictly at most one active certificate per installation
        var existingCerts = await _dbContext.LicenseCertificates
            .Where(c => c.InstallationId == installation.Id && c.Status == LicenseCertificate.StatusActive)
            .ToListAsync(cancellationToken);

        int lineageRevision = 1;
        foreach (var oldCert in existingCerts)
        {
            oldCert.Supersede();
            lineageRevision = Math.Max(lineageRevision, oldCert.LineageRevision + 1);
        }

        var now = DateTimeOffset.UtcNow;
        var validFrom = now;
        var secondsAvailable = (DateTimeOffset.MaxValue - now).TotalSeconds;
        if (policy.LeaseDurationSeconds > secondsAvailable ||
            policy.OfflineGraceSeconds > secondsAvailable - policy.LeaseDurationSeconds)
        {
            throw new CentralDomainException("POLICY_INVALID", "policy.invalid", "The persisted lease and grace durations exceed the supported timestamp range.");
        }
        var leaseExpires = now.AddSeconds(policy.LeaseDurationSeconds);
        var offlineGraceUntil = leaseExpires.AddSeconds(policy.OfflineGraceSeconds);

        var certSerial = $"CERT-{installation.ShopId.ToString("N")[..8].ToUpperInvariant()}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";

        var activeKey = await _signingEngine.EnsureActiveKeyAsync(SigningPurpose.License, cancellationToken);

        string keyId = activeKey.KeyId;
        int keyVersion = activeKey.KeyVersion;
        string purpose = activeKey.Purpose;

        var activeAddons = await _dbContext.CommercialEntitlements
            .Where(a => a.LicenseId == license.Id && a.Status == CommercialEntitlement.StatusActive &&
                (!a.ValidUntilUtc.HasValue || a.ValidUntilUtc.Value > now))
            .OrderBy(a => a.Kind)
            .Select(a => new
            {
                addonKind = a.Kind,
                validUntilUtc = a.ValidUntilUtc
            })
            .ToListAsync(cancellationToken);

        // Capture the exact persisted authority represented by the payload before the signer call.
        // EF-tracked entities can be updated in the same scope while signing is awaited, so comparing
        // against the mutable entity afterward would not detect every authority change.
        var licenseAuthorityRevisionForSigning = license.AuthorityRevision;
        var licenseTermForSigning = license.TermKind;
        var licenseValidUntilForSigning = license.ValidUntilUtc;
        var installationRowVersionForSigning = installation.RowVersion;
        var installationShopIdForSigning = installation.ShopId;
        var installationLicenseIdForSigning = installation.LicenseId;
        var installationPublicKeyFingerprintForSigning = installation.PublicKeyFingerprintSha256;

        var certPayload = new
        {
            serial = certSerial,
            installationId = installation.Id,
            shopId = installation.ShopId,
            licenseId = license.Id,
            termKind = license.TermKind,
            baseValidUntilUtc = license.ValidUntilUtc,
            installationPublicKeyFingerprint = installation.PublicKeyFingerprintSha256,
            addonEntitlements = activeAddons,
            lineageRevision = lineageRevision,
            authorityRevision = license.AuthorityRevision,
            validFromUtc = validFrom,
            leaseExpiresUtc = leaseExpires,
            offlineGraceUntilUtc = offlineGraceUntil
        };

        var (canonicalJson, digestSha256) = _serializer.SerializeAndHash(certPayload);
        var signingRequestId = Guid.NewGuid();

        // CAS check against concurrent revocation, renewal, or transfer
        var currentLicense = await _dbContext.CommercialLicenses.AsNoTracking().FirstOrDefaultAsync(l => l.Id == license.Id, cancellationToken);
        if (currentLicense == null || currentLicense.Status != CommercialLicense.StatusActive || currentLicense.AuthorityRevision != licenseAuthorityRevisionForSigning ||
            currentLicense.TermKind != licenseTermForSigning || currentLicense.ValidUntilUtc != licenseValidUntilForSigning)
        {
            throw new CentralDomainException("CONCURRENT_MODIFICATION", "cert.concurrent_modification", "License was modified or revoked during certificate issuance.");
        }

        var currentInstall = await _dbContext.Installations.AsNoTracking().FirstOrDefaultAsync(i => i.Id == installation.Id, cancellationToken);
        if (currentInstall == null || currentInstall.Status != Installation.StatusActive)
        {
            throw new CentralDomainException("CONCURRENT_MODIFICATION", "cert.concurrent_modification", "Installation was modified or deactivated during certificate issuance.");
        }

        var signingRequest = CentralSigningRequest.Create(
            signingRequestId,
            installation.Id,
            purpose,
            keyId,
            keyVersion,
            licenseAuthorityRevisionForSigning,
            digestSha256,
            canonicalJson,
            now);

        _dbContext.CentralSigningRequests.Add(signingRequest);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var signingResponse = await _signingEngine.SignAsync(new SigningRequestDto
        {
            SigningRequestId = signingRequestId,
            IssuanceRequestId = installation.Id,
            PayloadSha256 = digestSha256,
            AuthorityRevision = license.AuthorityRevision,
            KeyId = keyId,
            KeyVersion = keyVersion,
            Purpose = purpose,
            CanonicalPayload = canonicalJson
        }, cancellationToken);

        // Signing may be backed by another process or hardware device. Recheck the same persisted
        // authority after that call so a stale signature is never stored as a usable certificate.
        var authorityCheckTimeUtc = DateTimeOffset.UtcNow;
        var signedLicense = await _dbContext.CommercialLicenses.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == license.Id, cancellationToken);
        if (signedLicense == null || signedLicense.Status != CommercialLicense.StatusActive ||
            signedLicense.AuthorityRevision != licenseAuthorityRevisionForSigning ||
            signedLicense.TermKind != licenseTermForSigning || signedLicense.ValidUntilUtc != licenseValidUntilForSigning ||
            signedLicense.ValidFromUtc > authorityCheckTimeUtc ||
            (signedLicense.ValidUntilUtc.HasValue && signedLicense.ValidUntilUtc.Value <= authorityCheckTimeUtc))
        {
            throw new CentralDomainException("CONCURRENT_MODIFICATION", "cert.concurrent_modification", "License authority changed or expired while certificate signing was in progress.");
        }

        var signedInstallation = await _dbContext.Installations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == installation.Id, cancellationToken);
        if (signedInstallation == null || signedInstallation.Status != Installation.StatusActive ||
            signedInstallation.RowVersion != installationRowVersionForSigning ||
            signedInstallation.LicenseId != installationLicenseIdForSigning || signedInstallation.ShopId != installationShopIdForSigning ||
            signedInstallation.PublicKeyFingerprintSha256 != installationPublicKeyFingerprintForSigning)
        {
            throw new CentralDomainException("CONCURRENT_MODIFICATION", "cert.concurrent_modification", "Installation authority changed while certificate signing was in progress.");
        }

        signingRequest.CompleteSigning(signingResponse.Signature, DateTimeOffset.UtcNow);

        var cert = LicenseCertificate.Create(
            installation.Id,
            license.Id,
            installation.ShopId,
            lineageRevision,
            certSerial,
            canonicalJson,
            signingResponse.Signature,
            digestSha256,
            validFrom,
            leaseExpires,
            offlineGraceUntil,
            now);

        _dbContext.LicenseCertificates.Add(cert);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.RecordAuditAsync(
            "LICENSE_CERTIFICATE_ISSUED",
            "ADMIN",
            adminId,
            null,
            "License certificate issued",
            null,
            null,
            nameof(LicenseCertificate),
            cert.Id.ToString(),
            $"{{\"serial\":\"{cert.CertificateSerial}\",\"lineageRevision\":{cert.LineageRevision}}}",
            true,
            digestSha256,
            cancellationToken);

        if (securityTransaction is not null)
        {
            await securityTransaction.CommitAsync(cancellationToken);
        }

        return cert;
    }

    public async Task<HardwareReview> ReportHardwareDiscrepancyAsync(
        Guid installationId,
        string reportedFingerprint,
        string reason,
        CancellationToken cancellationToken = default)
    {
        await using var securityTransaction = await SecurityTransaction.BeginAsync(_dbContext, installationId.ToString(), cancellationToken);

        var installation = await _dbContext.Installations.FindAsync(new object[] { installationId }, cancellationToken)
            ?? throw new CentralDomainException("INSTALLATION_NOT_FOUND", "hw.installation_not_found", $"Installation {installationId} does not exist.");

        var now = DateTimeOffset.UtcNow;
        var persistedFingerprint = string.IsNullOrWhiteSpace(installation.HardwareFingerprintHash)
            ? installation.MachineFingerprint
            : installation.HardwareFingerprintHash;
        var review = HardwareReview.Create(
            installation.ShopId,
            installationId,
            reportedFingerprint,
            persistedFingerprint,
            reason,
            now);

        _dbContext.HardwareReviews.Add(review);
        _dbContext.CentralOutboxMessages.Add(CentralOutboxMessage.Create(
            review.Id, "activation.hardware_review_requested", nameof(HardwareReview), review.Id.ToString("D"),
            _serializer.SerializeCanonical(new { review.Id, review.ShopId, review.InstallationId }), now));
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (securityTransaction is not null)
        {
            await securityTransaction.CommitAsync(cancellationToken);
        }

        return review;
    }

    public async Task DecideHardwareReviewAsync(
        Guid reviewId,
        DecideHardwareReviewCommand command,
        Guid adminId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var meta = command.CommandMeta;
        if (reviewId == Guid.Empty || adminId == Guid.Empty || meta is null ||
            meta.ClientOperationId == Guid.Empty || meta.OperationType != "hardware.review" ||
            meta.FingerprintVersion != 1 || meta.ExpectedVersion < 1 || meta.PolicyVersion < 0 ||
            string.IsNullOrWhiteSpace(meta.Reason) || string.IsNullOrWhiteSpace(meta.PayloadSha256))
        {
            throw new CentralDomainException("INVALID_HARDWARE_REVIEW_COMMAND", "hw.command_invalid", "Hardware review command identity or metadata is invalid.");
        }

        var review = await _dbContext.HardwareReviews.FindAsync(new object[] { reviewId }, cancellationToken)
            ?? throw new CentralDomainException("HARDWARE_REVIEW_NOT_FOUND", "hw.not_found", $"Review {reviewId} does not exist.");

        await using var securityTransaction = await SecurityTransaction.BeginAsync(_dbContext, reviewId.ToString(), cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var actorScope = $"admin:{adminId:D}";
        var targetResource = $"hardware_reviews/{reviewId:D}";

        await _operationLedger.ExecuteOrReplayAsync(
            actorScope: actorScope,
            shopId: review.ShopId,
            meta: meta,
            payload: command,
            executeAsync: async cmd =>
            {
                if (meta.ExpectedVersion != review.RowVersion)
                {
                    throw new CentralDomainException("CONCURRENCY_CONFLICT", "operation.concurrency_conflict", "The hardware review changed before the decision was applied.");
                }

                review.Decide(cmd.Approve, adminId, now);
                await _dbContext.SaveChangesAsync(cancellationToken);

                _dbContext.CentralOutboxMessages.Add(CentralOutboxMessage.Create(
                    meta.ClientOperationId,
                    "activation.hardware_review_decided",
                    nameof(HardwareReview),
                    review.Id.ToString(),
                    _serializer.SerializeCanonical(new { review.Id, cmd.Approve, review.InstallationId, clientOperationId = meta.ClientOperationId }),
                    now));

                await _auditService.RecordAuditAsync(
                    "HARDWARE_REVIEW_DECIDED",
                    "ADMIN",
                    adminId,
                    null,
                    meta.Reason,
                    meta.ClientOperationId,
                    meta.ClientOperationId,
                    nameof(HardwareReview),
                    review.Id.ToString(),
                    $"{{\"approve\":{cmd.Approve},\"installationId\":\"{review.InstallationId}\"}}",
                    true,
                    meta.PayloadSha256,
                    cancellationToken);

                return (true, targetResource, review.RowVersion, 1);
            },
            targetResource: targetResource,
            cancellationToken: cancellationToken);

        if (securityTransaction is not null)
        {
            await securityTransaction.CommitAsync(cancellationToken);
        }
    }

    public async Task<PrimaryTransfer> InitiatePrimaryTransferAsync(
        InitiatePrimaryTransferCommand command,
        Guid adminId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var meta = command.CommandMeta;
        if (adminId == Guid.Empty || command.ShopId == Guid.Empty || command.SourceInstallationId == Guid.Empty ||
            command.TargetInstallationId == Guid.Empty || meta is null || meta.ClientOperationId == Guid.Empty ||
            meta.OperationType != "primary.transfer" || meta.FingerprintVersion != 1 || meta.ExpectedVersion < 0 ||
            meta.PolicyVersion < 0 || string.IsNullOrWhiteSpace(meta.PayloadSha256) || string.IsNullOrWhiteSpace(meta.Reason))
        {
            throw new CentralDomainException("INVALID_PRIMARY_TRANSFER_COMMAND", "transfer.command_invalid", "Primary transfer request identity or metadata is invalid.");
        }

        await using var securityTransaction = await SecurityTransaction.BeginAsync(_dbContext, command.ShopId.ToString(), cancellationToken);

        var source = await _dbContext.Installations.FindAsync(new object[] { command.SourceInstallationId }, cancellationToken)
            ?? throw new CentralDomainException("SOURCE_NOT_FOUND", "transfer.source_not_found", $"Source installation {command.SourceInstallationId} does not exist.");

        var target = await _dbContext.Installations.FindAsync(new object[] { command.TargetInstallationId }, cancellationToken)
            ?? throw new CentralDomainException("TARGET_NOT_FOUND", "transfer.target_not_found", $"Target installation {command.TargetInstallationId} does not exist.");

        if (source.ShopId != command.ShopId || target.ShopId != command.ShopId)
        {
            throw new CentralDomainException("SHOP_MISMATCH", "transfer.shop_mismatch", "Both installations must belong to the specified shop.");
        }

        var now = DateTimeOffset.UtcNow;

        {
            var actorScope = $"admin:{adminId}";
            var targetResource = $"shops/{command.ShopId}/transfers";

            var (_, transferRecord) = await _operationLedger.ExecuteAndCaptureOrReplayAsync(
                actorScope: actorScope,
                shopId: command.ShopId,
                meta: meta,
                payload: command,
                executeAsync: async cmd =>
                {
                    var tr = PrimaryTransfer.Initiate(
                        cmd.ShopId,
                        source.Id,
                        target.Id,
                        cmd.TransferReason,
                        adminId,
                        now);

                    _dbContext.PrimaryTransfers.Add(tr);
                    await _dbContext.SaveChangesAsync(cancellationToken);

                    _dbContext.CentralOutboxMessages.Add(CentralOutboxMessage.Create(
                        meta.ClientOperationId,
                        "activation.primary_transfer_initiated",
                        nameof(PrimaryTransfer),
                        tr.Id.ToString(),
                        _serializer.SerializeCanonical(new { tr.Id, tr.ShopId, tr.SourceInstallationId, tr.TargetInstallationId, clientOperationId = meta.ClientOperationId }),
                        now));

                    await _auditService.RecordAuditAsync(
                        "PRIMARY_TRANSFER_INITIATED",
                        "ADMIN",
                        adminId,
                        null,
                        meta.Reason,
                        meta.ClientOperationId,
                        meta.ClientOperationId,
                        nameof(PrimaryTransfer),
                        tr.Id.ToString(),
                        $"{{\"source\":\"{source.Id}\",\"target\":\"{target.Id}\"}}",
                        true,
                        meta.PayloadSha256,
                        cancellationToken);

                    return (tr, $"transfers/{tr.Id}", 1, 1);
                },
                recoverResultAsync: async record =>
                {
                    var trId = Guid.Parse(record.ResultReference!.Split('/').Last());
                    return await _dbContext.PrimaryTransfers.FindAsync(new object[] { trId }, cancellationToken)
                        ?? throw new CentralDomainException("TRANSFER_NOT_FOUND", "transfer.not_found", $"Transfer {trId} not found during replay.");
                },
                targetResource: targetResource,
                cancellationToken: cancellationToken);

            if (securityTransaction is not null)
            {
                await securityTransaction.CommitAsync(cancellationToken);
            }

            return transferRecord;
        }
    }

    public Task ApprovePrimaryTransferAsync(
        Guid transferId,
        Guid approverAdminId,
        Guid sensitiveApprovalId,
        CancellationToken cancellationToken = default)
    {
        return ApprovePrimaryTransferAsync(transferId, approverAdminId, sensitiveApprovalId, null, cancellationToken);
    }

    public async Task ApprovePrimaryTransferAsync(
        Guid transferId,
        Guid approverAdminId,
        Guid sensitiveApprovalId,
        ApprovePrimaryTransferCommand? command,
        CancellationToken cancellationToken = default)
    {
        var meta = command?.CommandMeta;
        if (command is not null && (transferId == Guid.Empty || approverAdminId == Guid.Empty || meta is null ||
            meta.ClientOperationId == Guid.Empty || meta.OperationType != "primary.transfer" || meta.FingerprintVersion != 1 ||
            meta.ExpectedVersion < 1 || meta.PolicyVersion < 0 || string.IsNullOrWhiteSpace(meta.PayloadSha256) ||
            string.IsNullOrWhiteSpace(meta.Reason) || sensitiveApprovalId == Guid.Empty || command.SensitiveApprovalId != sensitiveApprovalId ||
            (command.ContractRecoveryType is null) != (command.ContractEvidenceId is null) ||
            (command.ContractRecoveryType is not null && command.ContractRecoveryType is not ("STANDARD_TRANSFER" or "LOST_HARDWARE_RECOVERY")) ||
            (command.ContractEvidenceId == Guid.Empty)))
        {
            throw new CentralDomainException("INVALID_PRIMARY_TRANSFER_COMMAND", "transfer.command_invalid", "Primary transfer approval command identity or metadata is invalid.");
        }

        await using var securityTransaction = await SecurityTransaction.BeginAsync(_dbContext, transferId.ToString(), cancellationToken);

        var transfer = await _dbContext.PrimaryTransfers.FindAsync(new object[] { transferId }, cancellationToken)
            ?? throw new CentralDomainException("TRANSFER_NOT_FOUND", "transfer.not_found", $"Transfer {transferId} does not exist.");

        if (transfer.InitiatedByAdminId == approverAdminId)
        {
            throw new CentralDomainException("SELF_APPROVAL_PROHIBITED", "transfer.self_approval_prohibited", "Approver must be distinct from requester.");
        }

        SensitiveApprovalRequest? sensitiveApproval = null;
        if (sensitiveApprovalId != Guid.Empty)
        {
            sensitiveApproval = await _dbContext.SensitiveApprovalRequests.FindAsync(new object[] { sensitiveApprovalId }, cancellationToken)
                ?? throw new CentralDomainException("APPROVAL_NOT_FOUND", "transfer.approval_not_found", $"Approval request {sensitiveApprovalId} does not exist.");
        }
        else
        {
            sensitiveApproval = await _dbContext.SensitiveApprovalRequests
                .Where(r => r.TargetEntityId == transfer.Id && (r.OperationType == "PRIMARY_TRANSFER" || r.OperationType == "primary.transfer") && r.Status == SensitiveApprovalRequest.StatusApproved)
                .OrderByDescending(r => r.RequestedAtUtc)
                .FirstOrDefaultAsync(cancellationToken)
                ?? throw new CentralDomainException("APPROVAL_REQUIRED", "transfer.approval_required", "Primary transfer approval requires an approved sensitive approval request.");
        }

        if (!string.Equals(sensitiveApproval.OperationType, "PRIMARY_TRANSFER", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(sensitiveApproval.OperationType, "primary.transfer", StringComparison.OrdinalIgnoreCase))
        {
            throw new CentralDomainException("OPERATION_TYPE_MISMATCH", "transfer.operation_type_mismatch", $"Approval request operation {sensitiveApproval.OperationType} is not PRIMARY_TRANSFER.");
        }

        if (sensitiveApproval.TargetEntityId != transfer.Id)
        {
            throw new CentralDomainException("TARGET_MISMATCH", "transfer.target_mismatch", "Approval target does not match target transfer.");
        }

        if (command?.CommandMeta is CommandMetaDto commandMeta &&
            !string.Equals(sensitiveApproval.TargetPayloadSha256, commandMeta.PayloadSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new CentralDomainException("APPROVAL_PAYLOAD_MISMATCH", "approval.payload_mismatch", "The approved payload digest does not match this canonical transfer command.");
        }

        if (sensitiveApproval.RequesterAdminId == approverAdminId)
        {
            throw new CentralDomainException("SELF_APPROVAL_PROHIBITED", "transfer.self_approval_prohibited", "Approver must be distinct from requester.");
        }

        if (sensitiveApproval.RequesterAdminId == sensitiveApproval.ApproverAdminId)
        {
            throw new CentralDomainException("SELF_APPROVAL_PROHIBITED", "transfer.self_approval_prohibited", "Approver must be distinct from requester.");
        }

        var now = DateTimeOffset.UtcNow;

        if (command is not null && meta is not null)
        {
            var actorScope = $"admin:{approverAdminId}";
            var targetResource = $"transfers/{transferId}/approve";

            await _operationLedger.ExecuteOrReplayAsync<object, bool>(
                actorScope: actorScope,
                shopId: transfer.ShopId,
                meta: command.CommandMeta,
                payload: command.CanonicalPayload is JsonElement canonicalPayload ? canonicalPayload : command,
                executeAsync: async _ =>
                {
                    if (meta.ExpectedVersion != transfer.RowVersion)
                    {
                        throw new CentralDomainException("CONCURRENCY_CONFLICT", "operation.concurrency_conflict", "The primary transfer changed before approval was applied.");
                    }
                    if (sensitiveApproval.Status != SensitiveApprovalRequest.StatusApproved)
                    {
                        throw new CentralDomainException("APPROVAL_NOT_APPROVED", "transfer.approval_not_approved", $"Approval request is in status {sensitiveApproval.Status}; must be APPROVED.");
                    }
                    if (sensitiveApproval.ExpiresAtUtc <= now)
                    {
                        throw new CentralDomainException("APPROVAL_EXPIRED", "transfer.approval_expired", "Approval request has expired.");
                    }
                    if (command.ContractEvidenceId is Guid contractEvidenceId)
                    {
                        var contractEvidence = await _dbContext.EncryptedObjects.SingleOrDefaultAsync(x => x.Id == contractEvidenceId, cancellationToken)
                            ?? throw new CentralDomainException("EVIDENCE_NOT_FOUND", "support.evidence_not_found", "Verification evidence was not found.");
                        if (contractEvidence.ShopId != transfer.ShopId || contractEvidence.Purpose != "SUPPORT_ATTACHMENT" || contractEvidence.IsTombstoned)
                        {
                            throw new CentralDomainException("EVIDENCE_SCOPE_INVALID", "support.evidence_scope_invalid", "Evidence must be an active support attachment belonging to the same shop.");
                        }
                    }

                    sensitiveApproval.Consume(now);
                    transfer.Approve(approverAdminId, sensitiveApproval.Id);
                    if (command.ContractRecoveryType is not null && command.ContractEvidenceId is Guid evidenceId)
                    {
                        transfer.ApplyContractDecision("APPROVE", command.ContractRecoveryType, evidenceId, approverAdminId);
                    }
                    await _dbContext.SaveChangesAsync(cancellationToken);

                    _dbContext.CentralOutboxMessages.Add(CentralOutboxMessage.Create(
                        meta.ClientOperationId,
                        "activation.primary_transfer_approved",
                        nameof(PrimaryTransfer),
                        transfer.Id.ToString(),
                        _serializer.SerializeCanonical(new { transfer.Id, transfer.ShopId, approverAdminId, sensitiveApprovalId = sensitiveApproval.Id, clientOperationId = meta.ClientOperationId }),
                        now));

                    await _auditService.RecordAuditAsync(
                        "PRIMARY_TRANSFER_APPROVED",
                        "ADMIN",
                        approverAdminId,
                        null,
                        meta.Reason,
                        meta.ClientOperationId,
                        meta.ClientOperationId,
                        nameof(PrimaryTransfer),
                        transfer.Id.ToString(),
                        $"{{\"transferId\":\"{transfer.Id}\",\"sensitiveApprovalId\":\"{sensitiveApproval.Id}\"}}",
                        true,
                        meta.PayloadSha256,
                        cancellationToken);

                    return (true, $"transfers/{transfer.Id}", transfer.RowVersion, 1);
                },
                targetResource: targetResource,
                cancellationToken: cancellationToken);

            if (securityTransaction is not null)
            {
                await securityTransaction.CommitAsync(cancellationToken);
            }

            return;
        }

        sensitiveApproval.Consume(now);

        transfer.Approve(approverAdminId, sensitiveApproval.Id);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.RecordAuditAsync(
            "PRIMARY_TRANSFER_APPROVED",
            "ADMIN",
            approverAdminId,
            null,
            "Primary transfer approved with consumed dual approval",
            null,
            null,
            nameof(PrimaryTransfer),
            transfer.Id.ToString(),
            $"{{\"transferId\":\"{transfer.Id}\",\"sensitiveApprovalId\":\"{sensitiveApproval.Id}\"}}",
            true,
            null,
            cancellationToken);

        if (securityTransaction is not null)
        {
            await securityTransaction.CommitAsync(cancellationToken);
        }
    }

    public async Task CompletePrimaryTransferAsync(
        Guid transferId,
        Guid adminId,
        CompletePrimaryTransferCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var meta = command.CommandMeta;
        if (adminId == Guid.Empty || meta is null || meta.ClientOperationId == Guid.Empty ||
            meta.OperationType != "primary.transfer" || meta.FingerprintVersion != 1 || meta.ExpectedVersion < 1 ||
            meta.PolicyVersion < 0 || string.IsNullOrWhiteSpace(meta.PayloadSha256) || string.IsNullOrWhiteSpace(meta.Reason))
        {
            throw new CentralDomainException("INVALID_PRIMARY_TRANSFER_COMMAND", "transfer.command_invalid", "Primary transfer completion requires valid operation metadata.");
        }

        await using var securityTransaction = await SecurityTransaction.BeginAsync(_dbContext, transferId.ToString(), cancellationToken);

        var transfer = await _dbContext.PrimaryTransfers.FindAsync(new object[] { transferId }, cancellationToken)
            ?? throw new CentralDomainException("TRANSFER_NOT_FOUND", "transfer.not_found", $"Transfer {transferId} does not exist.");

        var outcome = await _operationLedger.ExecuteOrReplayAsync(
            actorScope: $"admin:{adminId}",
            shopId: transfer.ShopId,
            meta: meta,
            payload: command,
            executeAsync: async _ =>
            {
                if (meta.ExpectedVersion != transfer.RowVersion)
                {
                    throw new CentralDomainException("CONCURRENCY_CONFLICT", "operation.concurrency_conflict", "The primary transfer changed before completion was applied.");
                }
                if (transfer.Status != PrimaryTransfer.StatusApproved)
                {
                    throw new CentralDomainException("TRANSFER_NOT_APPROVED", "transfer.not_approved", $"Transfer must be in APPROVED status; current status: {transfer.Status}");
                }
                if (transfer.ContractDecision != "APPROVE" ||
                    transfer.RecoveryType is not ("STANDARD_TRANSFER" or "LOST_HARDWARE_RECOVERY") ||
                    transfer.VerificationEvidenceId is not Guid evidenceId || evidenceId == Guid.Empty)
                {
                    throw new CentralDomainException("TRANSFER_CONTRACT_EVIDENCE_REQUIRED", "transfer.evidence_required", "Completion requires a contract APPROVE decision with persisted verification evidence.");
                }
                var evidence = await _dbContext.EncryptedObjects.SingleOrDefaultAsync(x => x.Id == evidenceId, cancellationToken)
                    ?? throw new CentralDomainException("EVIDENCE_NOT_FOUND", "support.evidence_not_found", "Primary transfer verification evidence was not found.");
                if (evidence.ShopId != transfer.ShopId || evidence.Purpose != EncryptedObjectRecord.PurposeSupportAttachment || evidence.IsTombstoned)
                {
                    throw new CentralDomainException("EVIDENCE_SCOPE_INVALID", "support.evidence_scope_invalid", "Primary transfer evidence must be an active support attachment for the same shop.");
                }

                var source = await _dbContext.Installations.FindAsync(new object[] { transfer.SourceInstallationId }, cancellationToken)
                    ?? throw new CentralDomainException("SOURCE_NOT_FOUND", "transfer.source_not_found", "Source installation not found.");
                var target = await _dbContext.Installations.FindAsync(new object[] { transfer.TargetInstallationId }, cancellationToken)
                    ?? throw new CentralDomainException("TARGET_NOT_FOUND", "transfer.target_not_found", "Target installation not found.");

                if (source.ShopId != transfer.ShopId || target.ShopId != transfer.ShopId)
                {
                    throw new CentralDomainException("SHOP_MISMATCH", "transfer.shop_mismatch", "Source and target installations must belong to the transfer shop.");
                }
                if (source.LicenseId != target.LicenseId)
                {
                    throw new CentralDomainException("LICENSE_MISMATCH", "transfer.license_mismatch", "Source and target installations must be bound to the same license.");
                }
                // Serialize completion against commercial lifecycle and installation changes.
                await SecurityTransaction.BeginAsync(_dbContext, source.LicenseId.ToString(), cancellationToken);
                foreach (var installationId in new[] { source.Id, target.Id }.Order())
                {
                    await SecurityTransaction.BeginAsync(_dbContext, installationId.ToString(), cancellationToken);
                }
                if (_dbContext is not DbContext ef)
                {
                    throw new InvalidOperationException("Transfer completion requires a reload-capable EF context after acquiring persisted identity locks.");
                }
                await ef.Entry(source).ReloadAsync(cancellationToken);
                await ef.Entry(target).ReloadAsync(cancellationToken);

                if (source.ShopId != transfer.ShopId || target.ShopId != transfer.ShopId)
                {
                    throw new CentralDomainException("SHOP_MISMATCH", "transfer.shop_mismatch", "Both current installations must belong to the persisted transfer shop.");
                }
                if (source.LicenseId != target.LicenseId)
                {
                    throw new CentralDomainException("LICENSE_MISMATCH", "transfer.license_mismatch", "Current source and target installations must be bound to the same license.");
                }
                if (source.Id == target.Id ||
                    source.Status is Installation.StatusRevoked or Installation.StatusReplaced ||
                    target.Status is Installation.StatusRevoked or Installation.StatusReplaced)
                {
                    throw new CentralDomainException("TRANSFER_INSTALLATION_UNAVAILABLE", "transfer.installation_unavailable", "A retired or revoked installation cannot participate in this transfer.");
                }

                // Re-read commercial authority under the license lock before changing
                // installation or certificate state.
                var license = await _dbContext.CommercialLicenses.FindAsync(new object[] { source.LicenseId }, cancellationToken)
                    ?? throw new CentralDomainException("LICENSE_NOT_FOUND", "transfer.license_not_found", "The transfer commercial license does not exist.");
                var now = DateTimeOffset.UtcNow;
                if (license.ShopId != transfer.ShopId)
                {
                    throw new CentralDomainException("SHOP_MISMATCH", "transfer.shop_mismatch", "The transfer license must belong to the persisted transfer shop.");
                }
                if (license.Status != CommercialLicense.StatusActive || license.ValidFromUtc > now ||
                    (license.ValidUntilUtc.HasValue && license.ValidUntilUtc.Value <= now))
                {
                    throw new CentralDomainException("LICENSE_NOT_ELIGIBLE", "transfer.license_not_eligible", "Primary transfer completion requires a currently active, commercially valid license.");
                }

                var sourceCerts = await _dbContext.LicenseCertificates
                    .Where(c => c.InstallationId == source.Id && c.Status == LicenseCertificate.StatusActive)
                    .ToListAsync(cancellationToken);
                foreach (var cert in sourceCerts)
                {
                    cert.Revoke();
                }

                source.Replace();
                var otherActiveInstalls = await _dbContext.Installations
                    .Where(i => i.ShopId == source.ShopId && i.Id != target.Id && i.Status == Installation.StatusActive)
                    .ToListAsync(cancellationToken);
                foreach (var other in otherActiveInstalls)
                {
                    other.Replace();
                }
                if (target.Status != Installation.StatusActive)
                {
                    target.Approve(now);
                }
                transfer.Complete(now);

                await _dbContext.SaveChangesAsync(cancellationToken);
                _dbContext.CentralOutboxMessages.Add(CentralOutboxMessage.Create(
                    meta.ClientOperationId,
                    "activation.primary_transfer_completed",
                    nameof(PrimaryTransfer),
                    transfer.Id.ToString(),
                    _serializer.SerializeCanonical(new { transfer.Id, transfer.ShopId, sourceInstallationId = source.Id, targetInstallationId = target.Id, clientOperationId = meta.ClientOperationId }),
                    now));
                await _auditService.RecordAuditAsync(
                    "PRIMARY_TRANSFER_COMPLETED",
                    "ADMIN",
                    adminId,
                    null,
                    meta.Reason,
                    meta.ClientOperationId,
                    meta.ClientOperationId,
                    nameof(PrimaryTransfer),
                    transfer.Id.ToString(),
                    _serializer.SerializeCanonical(new { sourceDeactivated = source.Id, targetActive = target.Id }),
                    true,
                    meta.PayloadSha256,
                    cancellationToken);

                return (true, $"transfers/{transfer.Id}", transfer.RowVersion, 1);
            },
            targetResource: $"transfers/{transferId}/complete",
            cancellationToken: cancellationToken);

        if (securityTransaction is not null)
        {
            await securityTransaction.CommitAsync(cancellationToken);
        }
    }

    public Task<SignedEntitlementDto> ReconcileInstallationAsync(
        ReconcileCommandDto command,
        CancellationToken cancellationToken = default)
        => ReconcileInstallationCoreAsync(command, null, cancellationToken);

    public async Task<SignedEntitlementDto> ReconcileCanonicalInstallationAsync(
        CommandMetaDto meta,
        ReconcileCommandDto command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(command);
        if (meta.OperationType != "installation.reconcile" || meta.ClientOperationId == Guid.Empty ||
            meta.FingerprintVersion != 1 || meta.ExpectedVersion < 0 || meta.PolicyVersion < 0 ||
            string.IsNullOrWhiteSpace(meta.Reason))
        {
            throw new CentralDomainException("INVALID_OPERATION_META", "operation.invalid_meta", "Invalid reconcile operation metadata.");
        }

        var installation = await _dbContext.Installations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == command.InstallationId, cancellationToken)
            ?? throw new CentralDomainException("INSTALLATION_NOT_FOUND", "reconcile.installation_not_found", "Installation does not exist.");
        var target = $"installations/{installation.Id:D}/reconcile";
        var (_, entitlement) = await _operationLedger.ExecuteAndCaptureOrReplayAsync<ReconcileCommandDto, SignedEntitlementDto>(
            $"installation:{installation.Id:D}", installation.ShopId, meta, command,
            async payload =>
            {
                var current = await _dbContext.Installations.SingleAsync(x => x.Id == payload.InstallationId, cancellationToken);
                if (current.RowVersion != meta.ExpectedVersion)
                {
                    throw new CentralDomainException("CONCURRENCY_CONFLICT", "operation.version_conflict", "Installation version changed; refresh and retry.");
                }
                var result = await ReconcileInstallationCoreAsync(payload, meta.ClientOperationId, cancellationToken);
                return (result, $"signingRequests/{meta.ClientOperationId:D}", current.RowVersion, result.Payload.AuthorityRevision);
            },
            recoverResultAsync: async record =>
            {
                if (!Guid.TryParse(record.ResultReference?.Split('/').LastOrDefault(), out var requestId))
                {
                    throw new CentralDomainException("RECONCILE_RESULT_UNAVAILABLE", "reconcile.result_unavailable", "Saved reconcile result is unavailable.");
                }
                var request = await _dbContext.CentralSigningRequests.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == requestId && x.Purpose == SigningPurpose.License && x.Status == "SIGNED", cancellationToken)
                    ?? throw new CentralDomainException("RECONCILE_RESULT_UNAVAILABLE", "reconcile.result_unavailable", "Saved signed reconcile result is unavailable.");
                var payload = System.Text.Json.JsonSerializer.Deserialize<EntitlementPayloadDto>(request.CanonicalPayload)
                    ?? throw new CentralDomainException("RECONCILE_RESULT_UNAVAILABLE", "reconcile.result_unavailable", "Saved reconcile payload is invalid.");
                var trustSet = await _signingEngine.GetTrustSetAsync(cancellationToken);
                return new SignedEntitlementDto
                {
                    EnvelopeVersion = 1, CanonicalizationProfileId = "CANONICAL_JSON_V1", AlgorithmProfileId = "RS256",
                    Payload = payload, PayloadSha256 = request.PayloadSha256, Signature = request.Signature,
                    TrustSetVersion = trustSet.TrustSetVersion
                };
            },
            targetResource: target,
            cancellationToken: cancellationToken);
        return entitlement;
    }

    private async Task<SignedEntitlementDto> ReconcileInstallationCoreAsync(
        ReconcileCommandDto command,
        Guid? stableSigningRequestId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var securityTransaction = await SecurityTransaction.BeginAsync(_dbContext, command.InstallationId.ToString(), cancellationToken);

        var installation = await _dbContext.Installations.FindAsync(new object[] { command.InstallationId }, cancellationToken)
            ?? throw new CentralDomainException("INSTALLATION_NOT_FOUND", "reconcile.installation_not_found", $"Installation {command.InstallationId} does not exist.");

        if (installation.Status != Installation.StatusActive)
        {
            throw new CentralDomainException("INSTALLATION_NOT_ACTIVE", "reconcile.installation_not_active", $"Installation is in status {installation.Status}; must be ACTIVE to reconcile.");
        }

        // Hardware discrepancy check
        if (!string.Equals(installation.MachineFingerprint, command.HardwareFingerprintHash, StringComparison.OrdinalIgnoreCase))
        {
            var approvedReview = await _dbContext.HardwareReviews
                .Where(r => r.InstallationId == installation.Id && r.ReportedMachineFingerprint == command.HardwareFingerprintHash && r.Status == HardwareReview.StatusApproved)
                .FirstOrDefaultAsync(cancellationToken);

            if (approvedReview == null)
            {
                throw new CentralDomainException("HARDWARE_FINGERPRINT_MISMATCH", "reconcile.hardware_mismatch", "Reported hardware fingerprint does not match active registration.");
            }
        }

        var license = await _dbContext.CommercialLicenses.FindAsync(new object[] { installation.LicenseId }, cancellationToken)
            ?? throw new CentralDomainException("LICENSE_NOT_FOUND", "reconcile.license_not_found", $"License {installation.LicenseId} does not exist.");

        if (license.Status != CommercialLicense.StatusActive)
        {
            throw new CentralDomainException("LICENSE_NOT_ACTIVE", "reconcile.license_not_active", $"License is in status {license.Status}; must be ACTIVE to reconcile.");
        }

        // Certificate verification
        if (command.CertificateId == Guid.Empty)
        {
            throw new CentralDomainException("CERTIFICATE_NOT_FOUND", "reconcile.cert_not_found", "The certificate does not belong to this installation.");
        }

        var certificate = await _dbContext.LicenseCertificates
            .FirstOrDefaultAsync(c => c.Id == command.CertificateId && c.InstallationId == installation.Id, cancellationToken);
        if (certificate is null)
        {
            // Never substitute a different active certificate when the caller supplied an
            // unknown or another installation's certificate ID. The certificate identity
            // is part of the installation proof, especially across primary transfer.
            throw new CentralDomainException("CERTIFICATE_NOT_FOUND", "reconcile.cert_not_found", "The certificate does not belong to this installation.");
        }

        if (certificate.Status == LicenseCertificate.StatusRevoked)
        {
            throw new CentralDomainException("CERTIFICATE_REVOKED", "reconcile.cert_revoked", "License certificate has been revoked.");
        }

        var policy = await GetVerifiedLicensePolicyAsync(installation.ShopId, license.Id, cancellationToken);

        var policyDto = new PolicyDto
        {
            ShopId = installation.ShopId,
            LicenseId = license.Id,
            PolicyVersion = policy.PolicyVersion,
            EnforcementMode = policy.EnforcementMode,
            ExtensionEndUtc = policy.ExtensionEndUtc,
            AfterExtensionPolicyId = policy.AfterExtensionPolicyId,
            LeaseDurationSeconds = policy.LeaseDurationSeconds,
            OfflineGraceSeconds = policy.OfflineGraceSeconds,
            RollbackToleranceSeconds = policy.RollbackToleranceSeconds,
            CapabilityPolicyId = policy.CapabilityPolicyId,
            ApprovedByAdminId = policy.ApprovedByAdminId,
            ApprovedAtUtc = policy.CreatedAtUtc
        };

        var commercialTermDto = new CommercialTermDto
        {
            TermKind = license.TermKind,
            ValidFromUtc = license.ValidFromUtc,
            ValidUntilUtc = license.ValidUntilUtc
        };

        var addons = await _dbContext.CommercialEntitlements
            .Where(e => e.LicenseId == license.Id && e.Status == CommercialEntitlement.StatusActive)
            .OrderBy(e => e.Kind)
            .Select(e => new AddonEntitlementDto
            {
                EntitlementId = e.Id,
                Kind = e.Kind,
                Status = e.Status,
                ValidUntilUtc = e.ValidUntilUtc
            })
            .ToListAsync(cancellationToken);

        var activeKey = await _signingEngine.EnsureActiveKeyAsync(SigningPurpose.License, cancellationToken);
        var trustSet = await _signingEngine.GetTrustSetAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var validFrom = certificate.ValidFromUtc;
        var secondsAvailable = (DateTimeOffset.MaxValue - now).TotalSeconds;
        if (policy.LeaseDurationSeconds > secondsAvailable ||
            policy.OfflineGraceSeconds > secondsAvailable - policy.LeaseDurationSeconds)
        {
            throw new CentralDomainException("POLICY_INVALID", "policy.invalid", "The persisted lease and grace durations exceed the supported timestamp range.");
        }
        var leaseExpires = now.AddSeconds(policyDto.LeaseDurationSeconds);
        var offlineGraceUntil = leaseExpires.AddSeconds(policyDto.OfflineGraceSeconds);

        var payload = new EntitlementPayloadDto
        {
            CertificateId = certificate.Id,
            CertificateVersion = certificate.LineageRevision,
            ShopId = installation.ShopId,
            LicenseId = license.Id,
            InstallationId = installation.Id,
            InstallationPublicKeyFingerprint = installation.PublicKeyFingerprintSha256,
            HardwareFingerprintVersion = 1,
            HardwareFingerprintHash = command.HardwareFingerprintHash,
            AuthorityRevision = license.AuthorityRevision,
            Policy = policyDto,
            IssuedAtUtc = certificate.IssuedAtUtc,
            ValidFromUtc = validFrom,
            LeaseExpiresAtUtc = leaseExpires,
            OfflineGraceUntilUtc = offlineGraceUntil,
            TrustedVendorTimeUtc = now,
            CapabilityClaims = new List<string> { "pos.checkout", "inventory.manage", "reports.view" },
            SigningKeyId = activeKey.KeyId,
            SigningKeyVersion = activeKey.KeyVersion,
            CommercialTerm = commercialTermDto,
            AddonEntitlements = addons
        };

        var (canonicalJson, digestSha256) = _serializer.SerializeAndHash(payload);

        var signingRequestId = stableSigningRequestId ?? Guid.NewGuid();
        var signingRequest = CentralSigningRequest.Create(
            signingRequestId,
            installation.Id,
            SigningPurpose.License,
            activeKey.KeyId,
            activeKey.KeyVersion,
            license.AuthorityRevision,
            digestSha256,
            canonicalJson,
            now);

        _dbContext.CentralSigningRequests.Add(signingRequest);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var signingResponse = await _signingEngine.SignAsync(new SigningRequestDto
        {
            SigningRequestId = signingRequestId,
            IssuanceRequestId = installation.Id,
            PayloadSha256 = digestSha256,
            AuthorityRevision = license.AuthorityRevision,
            KeyId = activeKey.KeyId,
            KeyVersion = activeKey.KeyVersion,
            Purpose = SigningPurpose.License,
            CanonicalPayload = canonicalJson
        }, cancellationToken);
        signingRequest.CompleteSigning(signingResponse.Signature, DateTimeOffset.UtcNow);

        var signedEntitlement = new SignedEntitlementDto
        {
            EnvelopeVersion = 1,
            CanonicalizationProfileId = "CANONICAL_JSON_V1",
            AlgorithmProfileId = "RS256",
            Payload = payload,
            PayloadSha256 = digestSha256,
            Signature = signingResponse.Signature,
            TrustSetVersion = trustSet.TrustSetVersion
        };

        // Outbox event and audit trail
        _dbContext.CentralOutboxMessages.Add(CentralOutboxMessage.Create(
            Guid.NewGuid(),
            "licensing.installation_reconciled",
            nameof(Installation),
            installation.Id.ToString(),
            _serializer.SerializeCanonical(new
            {
                installationId = installation.Id,
                certificateId = certificate.Id,
                authorityRevision = license.AuthorityRevision,
                reconciledAtUtc = now
            }),
            now));

        await _auditService.RecordAuditAsync(
            "INSTALLATION_RECONCILED",
            "INSTALLATION",
            null,
            installation.Id,
            $"Installation reconciled authority revision {license.AuthorityRevision}",
            null,
            null,
            nameof(Installation),
            installation.Id.ToString(),
            $"{{\"certificateId\":\"{certificate.Id}\",\"authorityRevision\":{license.AuthorityRevision}}}",
            true,
            digestSha256,
            cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);

        if (securityTransaction is not null)
        {
            await securityTransaction.CommitAsync(cancellationToken);
        }

        return signedEntitlement;
    }

    private async Task<ShopEnforcementPolicy> GetVerifiedLicensePolicyAsync(
        Guid shopId,
        Guid licenseId,
        CancellationToken cancellationToken)
    {
        var policy = await _dbContext.ShopEnforcementPolicies
            .Where(x => x.ShopId == shopId && x.LicenseId == licenseId)
            .OrderByDescending(x => x.PolicyVersion)
            .ThenByDescending(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new CentralDomainException("POLICY_CONFIGURATION_REQUIRED", "policy.configuration_required", "A persisted signed license policy is required before certificate issuance or reconciliation.");

        if (policy.PolicyVersion < 1 || policy.ApprovedByAdminId == Guid.Empty ||
            policy.AfterExtensionPolicyId == Guid.Empty || policy.CapabilityPolicyId == Guid.Empty ||
            policy.LeaseDurationSeconds < 1 || policy.OfflineGraceSeconds < 86400 ||
            policy.RollbackToleranceSeconds < 0 || string.IsNullOrWhiteSpace(policy.KeyId) ||
            policy.EnforcementMode is not (ShopEnforcementPolicy.FullStop or ShopEnforcementPolicy.ConfigurableExtension) ||
            (policy.EnforcementMode == ShopEnforcementPolicy.ConfigurableExtension && !policy.ExtensionEndUtc.HasValue) ||
            (policy.EnforcementMode == ShopEnforcementPolicy.FullStop && policy.ExtensionEndUtc.HasValue))
        {
            throw new CentralDomainException("POLICY_INVALID", "policy.invalid", "The persisted license policy is incomplete or invalid.");
        }

        var policyEnvelope = new
        {
            shopId = policy.ShopId,
            licenseId = policy.LicenseId,
            policyVersion = policy.PolicyVersion,
            enforcementMode = policy.EnforcementMode,
            extensionEndUtc = policy.ExtensionEndUtc,
            afterExtensionPolicyId = policy.AfterExtensionPolicyId,
            leaseDurationSeconds = policy.LeaseDurationSeconds,
            offlineGraceSeconds = policy.OfflineGraceSeconds,
            rollbackToleranceSeconds = policy.RollbackToleranceSeconds,
            capabilityPolicyId = policy.CapabilityPolicyId
        };
        var (canonicalPolicyJson, _) = _serializer.SerializeAndHash(policyEnvelope);
        var policyTrust = await _signingEngine.GetTrustSetAsync(cancellationToken);
        var policyKey = policyTrust.Keys.SingleOrDefault(x => x.KeyId == policy.KeyId);
        if (!string.Equals(canonicalPolicyJson, policy.SignedPolicyEnvelopeJson, StringComparison.Ordinal) ||
            policyKey is null || policyKey.Purpose != SigningPurpose.License ||
            policyKey.AlgorithmProfileId != "RS256" ||
            policyKey.Status is not ("ACTIVE" or "VERIFY_ONLY") ||
            policyTrust.RevokedKeyIds.Contains(policy.KeyId, StringComparer.Ordinal) ||
            !_signingEngine.VerifySignature(policyKey.PublicKey, policy.SignedPolicyEnvelopeJson, policy.RS256Signature))
        {
            throw new CentralDomainException("POLICY_SIGNATURE_INVALID", "policy.signature_invalid", "The persisted license policy failed integrity or trusted signer verification.");
        }

        return policy;
    }
}
