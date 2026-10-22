using System.Security.Cryptography;
using System.Text;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Application.Services;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Common;
using EdgeRetails.Central.Domain.Licensing;
using EdgeRetails.Central.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EdgeRetails.Central.UnitTests.Activation;

public class ActivationUnitTests : IDisposable
{
    private readonly CentralDbContext _dbContext;
    private readonly CentralAuditService _auditService;
    private readonly TestProtectedSignerClient _signerClient;
    private readonly ActivationService _activationService;

    public ActivationUnitTests()
    {
        var options = new DbContextOptionsBuilder<CentralDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _dbContext = new CentralDbContext(options);
        _auditService = new CentralAuditService(_dbContext);
        _signerClient = new TestProtectedSignerClient();
        _activationService = new ActivationService(_dbContext, _signerClient, _auditService, bootstrapChallengeTtlSeconds: 300);
    }

    [Fact]
    public void PendingDeliverySecret_IsRemovedAndTokenExpiredOnlyAfterRedemptionWindow()
    {
        var now = DateTimeOffset.UtcNow;
        var token = ActivationToken.Issue(Guid.NewGuid(), Guid.NewGuid(), new string('a', 64), now.AddMinutes(1), Guid.NewGuid(), now);
        token.BindPendingDelivery(Guid.NewGuid(), Guid.NewGuid(), $"activation-deliveries/{token.Id:D}", "dev-key-v1", "nonce.tag.ciphertext");

        Assert.False(token.ExpireDeliverySecretIfWindowElapsed(now.AddSeconds(60)));
        Assert.Equal(ActivationToken.StatusIssued, token.Status);
        Assert.NotNull(token.ProtectedDeliverySecret);

        Assert.True(token.ExpireDeliverySecretIfWindowElapsed(now.AddSeconds(61)));
        Assert.Equal(ActivationToken.StatusExpired, token.Status);
        Assert.Equal("EXPIRED", token.DeliveryState);
        Assert.Null(token.ProtectedDeliverySecret);
        Assert.Null(token.DeliverySecretKeyId);
        Assert.False(token.ExpireDeliverySecretIfWindowElapsed(now.AddMinutes(2)));
    }

    [Fact]
    public async Task Registration_SeparatesStableInstallationIdentity_FromHardwareFingerprint()
    {
        using var rsa = RSA.Create(2048);
        var pubPem = rsa.ExportSubjectPublicKeyInfoPem();

        var now = DateTimeOffset.UtcNow;
        var shop = Shop.Create(Guid.NewGuid(), "SHOP-ACT-01", "Activation Shop", now);
        var item = PlanCatalogItem.Create("PLAN-ACT", "Plan Act", PlanCatalogItem.TargetBaseLicense, "LIFETIME", null, 500m, "USD");
        var snapshot = PricingSnapshot.Create(shop.Id, item, 1, now);
        var order = Order.CreateFromSnapshot(snapshot, Guid.NewGuid(), now);
        order.ApprovePayment(Guid.NewGuid(), Guid.NewGuid(), now);
        var license = CommercialLicense.Issue(order, now, null, now);

        _dbContext.Shops.Add(shop);
        _dbContext.CommercialLicenses.Add(license);
        await _dbContext.SaveChangesAsync();

        var install = await _activationService.RegisterInstallationAsync(new RegisterInstallationCommand
        {
            ShopId = shop.Id,
            LicenseId = license.Id,
            MachineFingerprint = "HW-DISK-CPU-992182",
            PublicKeyPem = pubPem,
            Hostname = "POS-TERMINAL-1",
            OsPlatform = "Windows 11",
            CommandMeta = new CommandMetaDto()
        });

        Assert.NotNull(install);
        Assert.Equal(Installation.StatusRegistered, install.Status);
        Assert.Equal(pubPem, install.PublicKeyPem);
        Assert.NotEmpty(install.PublicKeyFingerprintSha256);
        Assert.Equal("HW-DISK-CPU-992182", install.MachineFingerprint);
    }

    [Fact]
    public async Task BootstrapRegistration_BindsChallengeTokenKeyAndPayload_AndReplaysIdempotently()
    {
        using var clientRsa = RSA.Create(2048);
        var publicKey = clientRsa.ExportSubjectPublicKeyInfoPem();
        var now = DateTimeOffset.UtcNow;
        var shop = Shop.Create(Guid.NewGuid(), "SHOP-BOOT-01", "Bootstrap Shop", now);
        var item = PlanCatalogItem.Create("PLAN-BOOT", "Plan Bootstrap", PlanCatalogItem.TargetBaseLicense, "LIFETIME", null, 500m, "USD");
        var snapshot = PricingSnapshot.Create(shop.Id, item, 1, now);
        var order = Order.CreateFromSnapshot(snapshot, Guid.NewGuid(), now);
        order.ApprovePayment(Guid.NewGuid(), Guid.NewGuid(), now);
        var license = CommercialLicense.Issue(order, now, null, now);
        _dbContext.Shops.Add(shop);
        _dbContext.CommercialLicenses.Add(license);
        await _dbContext.SaveChangesAsync();

        var token = await _activationService.IssueActivationTokenAsync(new IssueActivationTokenCommand
        {
            ShopId = shop.Id,
            LicenseId = license.Id,
            FirstRedemptionTtlSeconds = 3600,
            CommandMeta = new CommandMetaDto()
        }, Guid.NewGuid());

        var installationId = Guid.NewGuid();
        var challenge = await _activationService.GenerateBootstrapChallengeAsync(new BootstrapChallengeRequestDto
        {
            InstallationId = installationId,
            InstallationPublicKey = publicKey,
            ActivationTokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token.TokenSecret.Trim()))),
            RequestNonce = Guid.NewGuid()
        });
        Assert.Contains("installation.register", challenge.AllowedOperations);

        var payload = new InstallRegistrationPayloadDto
        {
            ShopId = shop.Id,
            LicenseId = license.Id,
            InstallationId = installationId,
            InstallationPublicKey = publicKey,
            InstallationPublicKeyFingerprint = Installation.ComputePublicKeyFingerprintSha256(publicKey),
            BootstrapChallengeId = challenge.ChallengeId,
            DeviceIdentityVersion = 1,
            DeviceIdentityHash = new string('a', 64),
            HardwareFingerprintVersion = 1,
            HardwareFingerprintHash = new string('b', 64),
            SignalQuality = "STRONG",
            ApplicationVersion = "0.0.0-dev-fixture"
        };
        string CreateProof()
        {
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
            var serializer = new EdgeRetails.Central.Application.Common.CanonicalJsonSerializer();
            var (_, payloadDigest) = serializer.SerializeAndHash(unsignedPayload);
            var envelope = serializer.SerializeCanonical(new
            {
                profile = "CENTRAL_INSTALLATION_BOOTSTRAP_V1",
                purpose = "installation.register",
                httpMethod = "POST",
                httpPath = "/api/licensing/v1/primary/register",
                challengeId = challenge.ChallengeId,
                challengeNonce = challenge.ChallengeNonce,
                requestNonce = _dbContext.BootstrapChallenges.Single(x => x.Id == challenge.ChallengeId).RequestNonce,
                activationTokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token.TokenSecret.Trim()))),
                payloadDigest
            });
            return Convert.ToHexString(clientRsa.SignData(Encoding.UTF8.GetBytes(envelope), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)).ToLowerInvariant();
        }

        payload.ProofOfPossession = CreateProof();
        var meta = new CommandMetaDto
        {
            ClientOperationId = Guid.NewGuid(),
            OperationType = "installation.register",
            PayloadSha256 = new EdgeRetails.Central.Application.Common.CanonicalJsonSerializer().SerializeAndHash(payload).Sha256Hex,
            Reason = "Synthetic development bootstrap registration"
        };
        var outcome = await _activationService.RegisterBootstrapInstallationAsync(meta, payload);
        Assert.Equal("SUCCEEDED", outcome.State);
        var registered = await _dbContext.Installations.SingleAsync(x => x.Id == installationId);
        Assert.Equal(shop.Id, registered.ShopId);
        Assert.Equal(license.Id, registered.LicenseId);
        Assert.Equal(new string('b', 64), registered.HardwareFingerprintHash);
        Assert.Equal(1, registered.DeviceIdentityVersion);
        Assert.NotNull((await _dbContext.BootstrapChallenges.SingleAsync(x => x.Id == challenge.ChallengeId)).RegisteredAtUtc);

        var replay = await _activationService.RegisterBootstrapInstallationAsync(meta, payload);
        Assert.Equal(outcome.ClientOperationId, replay.ClientOperationId);
        Assert.Equal(1, await _dbContext.Installations.CountAsync(x => x.Id == installationId));
    }

    [Fact]
    public async Task ActivationToken_IssuesHashedSecret_PersistsHashOnly()
    {
        var adminId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var shop = Shop.Create(Guid.NewGuid(), "SHOP-ACT-02", "Activation Shop 2", now);
        var item = PlanCatalogItem.Create("PLAN-ACT2", "Plan Act 2", PlanCatalogItem.TargetBaseLicense, "LIFETIME", null, 500m, "USD");
        var snapshot = PricingSnapshot.Create(shop.Id, item, 1, now);
        var order = Order.CreateFromSnapshot(snapshot, Guid.NewGuid(), now);
        order.ApprovePayment(Guid.NewGuid(), adminId, now);
        var license = CommercialLicense.Issue(order, now, null, now);

        _dbContext.Shops.Add(shop);
        _dbContext.CommercialLicenses.Add(license);
        await _dbContext.SaveChangesAsync();

        var tokenDto = await _activationService.IssueActivationTokenAsync(new IssueActivationTokenCommand
        {
            ShopId = shop.Id,
            LicenseId = license.Id,
            FirstRedemptionTtlSeconds = 3600,
            CommandMeta = new CommandMetaDto()
        }, adminId);

        Assert.NotNull(tokenDto);
        Assert.NotEmpty(tokenDto.TokenSecret);

        // Verify token in database has SHA256 hash, NOT raw secret
        var tokenInDb = await _dbContext.ActivationTokens.FindAsync(tokenDto.TokenId);
        Assert.NotNull(tokenInDb);
        Assert.Equal(ActivationToken.StatusIssued, tokenInDb.Status);

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(tokenDto.TokenSecret));
        string expectedHash = Convert.ToHexString(hash).ToLowerInvariant();
        Assert.Equal(expectedHash, tokenInDb.TokenHashSha256);
    }

    [Fact]
    public async Task Redemption_VerifiesRsaSignature_RejectsInvalidSignature()
    {
        using var clientRsa = RSA.Create(2048);
        var clientPubPem = clientRsa.ExportSubjectPublicKeyInfoPem();

        var now = DateTimeOffset.UtcNow;
        var shop = Shop.Create(Guid.NewGuid(), "SHOP-ACT-03", "Activation Shop 3", now);
        var item = PlanCatalogItem.Create("PLAN-ACT3", "Plan Act 3", PlanCatalogItem.TargetBaseLicense, "LIFETIME", null, 500m, "USD");
        var snapshot = PricingSnapshot.Create(shop.Id, item, 1, now);
        var order = Order.CreateFromSnapshot(snapshot, Guid.NewGuid(), now);
        order.ApprovePayment(Guid.NewGuid(), Guid.NewGuid(), now);
        var license = CommercialLicense.Issue(order, now, null, now);

        _dbContext.Shops.Add(shop);
        _dbContext.CommercialLicenses.Add(license);
        await _dbContext.SaveChangesAsync();

        var install = await _activationService.RegisterInstallationAsync(new RegisterInstallationCommand
        {
            ShopId = shop.Id,
            LicenseId = license.Id,
            MachineFingerprint = "HW-CLIENT-03",
            PublicKeyPem = clientPubPem,
            Hostname = "POS-03",
            OsPlatform = "Windows 11",
            CommandMeta = new CommandMetaDto()
        });

        var tokenDto = await _activationService.IssueActivationTokenAsync(new IssueActivationTokenCommand
        {
            ShopId = shop.Id,
            LicenseId = license.Id,
            FirstRedemptionTtlSeconds = 3600,
            CommandMeta = new CommandMetaDto()
        }, Guid.NewGuid());

        var challengeRequest = new BootstrapChallengeRequestDto
        {
            ShopId = shop.Id,
            InstallationId = install.Id,
            InstallationPublicKey = clientPubPem,
            ActivationTokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(tokenDto.TokenSecret.Trim()))),
            RequestNonce = Guid.NewGuid()
        };
        var challenge = await _activationService.GenerateBootstrapChallengeAsync(challengeRequest);
        Assert.NotEqual(Guid.Empty, challenge.ChallengeId);
        Assert.Contains("activation.redeem", challenge.AllowedOperations);
        Assert.Equal(1, await _dbContext.BootstrapChallenges.CountAsync());
        Assert.DoesNotContain(challenge.ChallengeNonce, (await _dbContext.BootstrapChallenges.SingleAsync()).ChallengeNonceHashSha256);
        var reusedRequestNonce = await Assert.ThrowsAsync<CentralDomainException>(() =>
            _activationService.GenerateBootstrapChallengeAsync(challengeRequest));
        Assert.Equal("BOOTSTRAP_REQUEST_REPLAYED", reusedRequestNonce.Code);

        // Sign challenge nonce using client's private key
        byte[] sigBytes = clientRsa.SignData(
            Encoding.UTF8.GetBytes(challenge.ChallengeNonce),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        string clientSigHex = Convert.ToHexString(sigBytes).ToLowerInvariant();

        // 1. Valid signature succeeds
        var redemption = await _activationService.RedeemActivationTokenAsync(new RedeemActivationTokenCommand
        {
            TokenId = tokenDto.TokenId,
            InstallationId = install.Id,
            TokenSecret = tokenDto.TokenSecret,
            ChallengeNonce = challenge.ChallengeNonce,
            ClientSignatureHex = clientSigHex,
            CommandMeta = new CommandMetaDto()
        });

        Assert.NotNull(redemption);
        Assert.Equal(ActivationRedemption.StatusPending, redemption.Status);

        // 2. Invalid signature fails
        var fakeSig = new string('0', clientSigHex.Length);
        var ex = await Assert.ThrowsAsync<CentralDomainException>(() =>
            _activationService.RedeemActivationTokenAsync(new RedeemActivationTokenCommand
            {
                TokenId = tokenDto.TokenId,
                InstallationId = install.Id,
                TokenSecret = tokenDto.TokenSecret,
                ChallengeNonce = challenge.ChallengeNonce,
                ClientSignatureHex = fakeSig,
                CommandMeta = new CommandMetaDto()
            }));

        Assert.Equal("INVALID_CLIENT_SIGNATURE", ex.Code);
    }

    [Fact]
    public async Task CertificateIssuance_EnforcesStrictlyAtMostOneActiveCertificate_SupersedesPrior()
    {
        var adminId = Guid.NewGuid();
        using var clientRsa = RSA.Create(2048);
        var clientPubPem = clientRsa.ExportSubjectPublicKeyInfoPem();

        var now = DateTimeOffset.UtcNow;
        var shop = Shop.Create(Guid.NewGuid(), "SHOP-CERT-01", "Cert Shop", now);
        var item = PlanCatalogItem.Create("PLAN-CERT", "Plan Cert", PlanCatalogItem.TargetBaseLicense, "LIFETIME", null, 500m, "USD");
        var snapshot = PricingSnapshot.Create(shop.Id, item, 1, now);
        var order = Order.CreateFromSnapshot(snapshot, Guid.NewGuid(), now);
        order.ApprovePayment(Guid.NewGuid(), adminId, now);
        var license = CommercialLicense.Issue(order, now, null, now);

        var install = Installation.Register(shop.Id, license.Id, "HW-01", clientPubPem, "POS", "Win", now);
        install.Approve(now); // Make ACTIVE

        _dbContext.Shops.Add(shop);
        _dbContext.CommercialLicenses.Add(license);
        _dbContext.Installations.Add(install);
        await _dbContext.SaveChangesAsync();

        // Issue 1st certificate
        var cert1 = await _activationService.IssueCertificateAsync(install.Id, adminId);
        Assert.NotNull(cert1);
        Assert.Equal(LicenseCertificate.StatusActive, cert1.Status);
        Assert.Equal(1, cert1.LineageRevision);

        // Issue 2nd certificate for same installation
        var cert2 = await _activationService.IssueCertificateAsync(install.Id, adminId);
        Assert.NotNull(cert2);
        Assert.Equal(LicenseCertificate.StatusActive, cert2.Status);
        Assert.Equal(2, cert2.LineageRevision);

        // Invariant: cert1 must now be SUPERSEDED
        var reloadedCert1 = await _dbContext.LicenseCertificates.FindAsync(cert1.Id);
        Assert.NotNull(reloadedCert1);
        Assert.Equal(LicenseCertificate.StatusSuperseded, reloadedCert1.Status);

        // Exactly 1 active certificate exists for installation
        var activeCerts = await _dbContext.LicenseCertificates
            .Where(c => c.InstallationId == install.Id && c.Status == LicenseCertificate.StatusActive)
            .ToListAsync();
        Assert.Single(activeCerts);
    }

    [Fact]
    public async Task PrimaryTransfer_DeactivatesSource_RevokesActiveCerts_AndCompletesTransfer()
    {
        var adminId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var shop = Shop.Create(Guid.NewGuid(), "SHOP-XFER-01", "Transfer Shop", now);
        var item = PlanCatalogItem.Create("PLAN-XFER", "Plan Xfer", PlanCatalogItem.TargetBaseLicense, "LIFETIME", null, 500m, "USD");
        var snapshot = PricingSnapshot.Create(shop.Id, item, 1, now);
        var order = Order.CreateFromSnapshot(snapshot, Guid.NewGuid(), now);
        order.ApprovePayment(Guid.NewGuid(), adminId, now);
        var license = CommercialLicense.Issue(order, now, null, now);

        using var rsa1 = RSA.Create(2048);
        using var rsa2 = RSA.Create(2048);
        var sourceInstall = Installation.Register(shop.Id, license.Id, "HW-SOURCE", rsa1.ExportSubjectPublicKeyInfoPem(), "OLD-POS", "Win", now);
        sourceInstall.Approve(now);

        var targetInstall = Installation.Register(shop.Id, license.Id, "HW-TARGET", rsa2.ExportSubjectPublicKeyInfoPem(), "NEW-POS", "Win", now);
        targetInstall.Approve(now);

        _dbContext.Shops.Add(shop);
        _dbContext.CommercialLicenses.Add(license);
        _dbContext.Installations.Add(sourceInstall);
        _dbContext.Installations.Add(targetInstall);
        await _dbContext.SaveChangesAsync();

        // Source installation has active certificate
        var sourceCert = await _activationService.IssueCertificateAsync(sourceInstall.Id, adminId);
        Assert.Equal(LicenseCertificate.StatusActive, sourceCert.Status);

        // Initiate Primary Transfer
        var transfer = await _activationService.InitiatePrimaryTransferAsync(new InitiatePrimaryTransferCommand
        {
            ShopId = shop.Id,
            SourceInstallationId = sourceInstall.Id,
            TargetInstallationId = targetInstall.Id,
            TransferReason = "Upgraded to new POS hardware",
            CommandMeta = new CommandMetaDto()
        }, adminId);

        Assert.NotNull(transfer);
        Assert.Equal(PrimaryTransfer.StatusPendingApproval, transfer.Status);

        // Dual-Control Approval: Approver must be distinct from initiator
        var distinctApproverAdminId = Guid.NewGuid();
        var approval = SensitiveApprovalRequest.Request(
            "PRIMARY_TRANSFER",
            transfer.Id,
            new string('a', 64),
            adminId,
            "shop.transfer",
            Guid.NewGuid(),
            now,
            now.AddHours(2));
        approval.Approve(distinctApproverAdminId, new string('a', 64), now);
        _dbContext.SensitiveApprovalRequests.Add(approval);
        await _dbContext.SaveChangesAsync();

        await _activationService.ApprovePrimaryTransferAsync(transfer.Id, distinctApproverAdminId, approval.Id);

        // Complete Transfer
        await _activationService.CompletePrimaryTransferAsync(transfer.Id, distinctApproverAdminId);

        // Check source installation: replaced/deactivated
        var reloadedSource = await _dbContext.Installations.FindAsync(sourceInstall.Id);
        Assert.NotNull(reloadedSource);
        Assert.Equal(Installation.StatusReplaced, reloadedSource.Status);

        // Check source cert: revoked
        var reloadedCert = await _dbContext.LicenseCertificates.FindAsync(sourceCert.Id);
        Assert.NotNull(reloadedCert);
        Assert.Equal(LicenseCertificate.StatusRevoked, reloadedCert.Status);

        // Check transfer status: completed
        var reloadedTransfer = await _dbContext.PrimaryTransfers.FindAsync(transfer.Id);
        Assert.NotNull(reloadedTransfer);
        Assert.Equal(PrimaryTransfer.StatusCompleted, reloadedTransfer.Status);
    }

    [Fact]
    public async Task ReconcileInstallation_Success_ReturnsValidSignedEntitlement()
    {
        using var rsa = RSA.Create(2048);
        var pubPem = rsa.ExportSubjectPublicKeyInfoPem();
        var adminId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var shop = Shop.Create(Guid.NewGuid(), "SHOP-REC-01", "Reconcile Shop", now);
        var item = PlanCatalogItem.Create("PLAN-REC", "Plan Rec", PlanCatalogItem.TargetBaseLicense, "LIFETIME", null, 500m, "USD");
        var snapshot = PricingSnapshot.Create(shop.Id, item, 1, now);
        var order = Order.CreateFromSnapshot(snapshot, Guid.NewGuid(), now);
        order.ApprovePayment(Guid.NewGuid(), adminId, now);
        var license = CommercialLicense.Issue(order, now, null, now);
        var install = Installation.Register(shop.Id, license.Id, "HW-REC-001", pubPem, "POS-1", "Win", now);
        install.Approve(now);

        _dbContext.Shops.Add(shop);
        _dbContext.CommercialLicenses.Add(license);
        _dbContext.Installations.Add(install);
        await _dbContext.SaveChangesAsync();

        var cert = await _activationService.IssueCertificateAsync(install.Id, adminId);
        Assert.NotNull(cert);

        var signedEntitlement = await _activationService.ReconcileInstallationAsync(new ReconcileCommandDto
        {
            InstallationId = install.Id,
            CertificateId = cert.Id,
            AuthorityRevision = license.AuthorityRevision,
            HardwareFingerprintHash = "HW-REC-001",
            ApplicationVersion = "1.0.0-phase12",
            LastVerifiedVendorTimeUtc = now
        });

        Assert.NotNull(signedEntitlement);
        Assert.Equal(1, signedEntitlement.EnvelopeVersion);
        Assert.Equal("RS256", signedEntitlement.AlgorithmProfileId);
        Assert.Equal("LICENSE", signedEntitlement.Purpose);
        Assert.NotNull(signedEntitlement.Payload);
        Assert.Equal(install.Id, signedEntitlement.Payload.InstallationId);
        Assert.Equal(shop.Id, signedEntitlement.Payload.ShopId);
        Assert.NotEmpty(signedEntitlement.Signature);
        Assert.NotEmpty(signedEntitlement.PayloadSha256);
    }

    [Fact]
    public async Task ReconcileInstallation_HardwareMismatch_WithoutReview_ThrowsException()
    {
        using var rsa = RSA.Create(2048);
        var pubPem = rsa.ExportSubjectPublicKeyInfoPem();
        var adminId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var shop = Shop.Create(Guid.NewGuid(), "SHOP-REC-02", "Reconcile Shop 2", now);
        var item = PlanCatalogItem.Create("PLAN-REC2", "Plan Rec 2", PlanCatalogItem.TargetBaseLicense, "LIFETIME", null, 500m, "USD");
        var snapshot = PricingSnapshot.Create(shop.Id, item, 1, now);
        var order = Order.CreateFromSnapshot(snapshot, Guid.NewGuid(), now);
        order.ApprovePayment(Guid.NewGuid(), adminId, now);
        var license = CommercialLicense.Issue(order, now, null, now);
        var install = Installation.Register(shop.Id, license.Id, "HW-ORIGINAL", pubPem, "POS-1", "Win", now);
        install.Approve(now);

        _dbContext.Shops.Add(shop);
        _dbContext.CommercialLicenses.Add(license);
        _dbContext.Installations.Add(install);
        await _dbContext.SaveChangesAsync();

        var cert = await _activationService.IssueCertificateAsync(install.Id, adminId);

        var ex = await Assert.ThrowsAsync<CentralDomainException>(() =>
            _activationService.ReconcileInstallationAsync(new ReconcileCommandDto
            {
                InstallationId = install.Id,
                CertificateId = cert.Id,
                AuthorityRevision = license.AuthorityRevision,
                HardwareFingerprintHash = "HW-TAMPERED-DIFF",
                ApplicationVersion = "1.0.0",
                LastVerifiedVendorTimeUtc = now
            }));

        Assert.Equal("HARDWARE_FINGERPRINT_MISMATCH", ex.Code);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _signerClient.Dispose();
        GC.SuppressFinalize(this);
    }
}

public class TestProtectedSignerClient : IProtectedSignerClient, IDisposable
{
    private readonly RSA _caRsa = RSA.Create(2048);

    public Task<SigningResponseDto> SignAsync(SigningRequestDto request, CancellationToken cancellationToken = default)
    {
        byte[] payloadBytes = Encoding.UTF8.GetBytes(request.CanonicalPayload);
        byte[] sigBytes = _caRsa.SignData(payloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        string sigHex = Convert.ToHexString(sigBytes).ToLowerInvariant();

        return Task.FromResult(new SigningResponseDto
        {
            SigningRequestId = request.SigningRequestId,
            Signature = sigHex,
            AlgorithmProfileId = "RS256",
            KeyId = request.KeyId,
            KeyVersion = request.KeyVersion
        });
    }

    public Task<TrustSetDto> GetTrustSetAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new TrustSetDto
        {
            TrustSetVersion = 1,
            SignedByTrustedRootKeyId = "root",
            Keys = new List<KeyDescriptorDto>
            {
                new KeyDescriptorDto
                {
                    KeyId = "central-ca-v1",
                    KeyVersion = 1,
                    PublicKey = _caRsa.ExportSubjectPublicKeyInfoPem(),
                    Status = "ACTIVE",
                    Purpose = "LICENSE_CERTIFICATE"
                }
            }
        });
    }

    public Task<KeyDescriptorDto> EnsureActiveKeyAsync(string purpose, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new KeyDescriptorDto
        {
            KeyId = "central-ca-v1",
            KeyVersion = 1,
            PublicKey = _caRsa.ExportSubjectPublicKeyInfoPem(),
            Status = "ACTIVE",
            Purpose = purpose
        });
    }

    public bool VerifySignature(string publicKeyPem, string canonicalPayload, string signatureHexOrBase64)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem);

            byte[] sigBytes;
            if (signatureHexOrBase64.Length % 2 == 0 && signatureHexOrBase64.All(c => "0123456789abcdefABCDEF".Contains(c)))
            {
                sigBytes = Convert.FromHexString(signatureHexOrBase64);
            }
            else
            {
                sigBytes = Convert.FromBase64String(signatureHexOrBase64);
            }

            return rsa.VerifyData(
                Encoding.UTF8.GetBytes(canonicalPayload),
                sigBytes,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _caRsa.Dispose();
    }
}
