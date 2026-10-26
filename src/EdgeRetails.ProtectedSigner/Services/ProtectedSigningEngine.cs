using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Application.Services;
using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Domain.Common;
using EdgeRetails.Central.Domain.Signing;
using EdgeRetails.ProtectedSigner.Keys;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.ProtectedSigner.Services;

public class ProtectedSigningEngine : IProtectedSigningEngine
{
    public const string DefaultAlgorithm = "RS256";

    private readonly ICentralDbContext _dbContext;
    private readonly IPrivateKeyStore _keyStore;

    public ProtectedSigningEngine(
        ICentralDbContext dbContext,
        IPrivateKeyStore keyStore)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
    }

    public async Task<SigningResponseDto> SignAsync(SigningRequestDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var transaction = await SecurityTransaction.BeginAsync(_dbContext, "central-signing-authority", cancellationToken);
        var response = await SignCoreAsync(request, cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
        return response;
    }

    private async Task<SigningResponseDto> SignCoreAsync(SigningRequestDto request, CancellationToken cancellationToken)
    {
        var serializer = new CanonicalJsonSerializer();
        using var document = JsonDocument.Parse(request.CanonicalPayload);
        if (serializer.SerializeCanonical(document.RootElement) != request.CanonicalPayload)
        {
            throw new CentralDomainException("NONCANONICAL_PAYLOAD", "signing.noncanonical", "Payload must use the versioned canonical JSON profile.");
        }

        // 1. Validate payload integrity (anti-tamper)
        var payloadBytes = Encoding.UTF8.GetBytes(request.CanonicalPayload);
        var computedHash = Convert.ToHexStringLower(SHA256.HashData(payloadBytes));

        if (!string.Equals(computedHash, request.PayloadSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new CentralDomainException(
                "TAMPERED_PAYLOAD",
                "signing.tampered_payload",
                $"Canonical payload hash mismatch. Expected: {request.PayloadSha256}, Computed: {computedHash}");
        }

        // 2. Validate key authority, version, and status first
        var keyRecord = await _dbContext.CentralSigningKeyRecords
            .FirstOrDefaultAsync(k => k.KeyId == request.KeyId, cancellationToken);

        if (keyRecord is null)
        {
            throw new CentralDomainException(
                "KEY_NOT_FOUND",
                "signing.key_not_found",
                $"Signing key '{request.KeyId}' not found in central registry.");
        }

        if (keyRecord.KeyVersion != request.KeyVersion)
        {
            throw new CentralDomainException(
                "KEY_VERSION_MISMATCH",
                "signing.key_version_mismatch",
                $"Requested key version {request.KeyVersion} does not match authoritative key version {keyRecord.KeyVersion}.");
        }

        if (request.AuthorityRevision <= 0)
        {
            throw new CentralDomainException(
                "STALE_AUTHORITY_REVISION",
                "signing.stale_authority",
                $"Requested authority revision {request.AuthorityRevision} is superseded by current revision {keyRecord.EmergencyRolloverRevision}.");
        }

        if (!string.Equals(keyRecord.Purpose, request.Purpose, StringComparison.OrdinalIgnoreCase))
        {
            throw new CentralDomainException(
                "PURPOSE_MISMATCH",
                "signing.purpose_mismatch",
                $"Key purpose '{keyRecord.Purpose}' does not match request purpose '{request.Purpose}'.");
        }

        if (keyRecord.Status == KeyStatus.Revoked)
        {
            throw new CentralDomainException(
                "KEY_REVOKED",
                "signing.key_revoked",
                $"Signing key '{request.KeyId}' has been revoked.");
        }

        if (keyRecord.Status == KeyStatus.Retired)
        {
            throw new CentralDomainException(
                "KEY_RETIRED",
                "signing.key_retired",
                $"Signing key '{request.KeyId}' is retired.");
        }

        if (keyRecord.Status != KeyStatus.Active)
        {
            throw new CentralDomainException(
                "KEY_NOT_ACTIVE",
                "signing.key_not_active",
                $"Signing key '{request.KeyId}' is in status '{keyRecord.Status}' and cannot issue new signatures.");
        }

        var now = DateTimeOffset.UtcNow;
        if (now < keyRecord.ValidFromUtc || now > keyRecord.ValidUntilUtc)
        {
            throw new CentralDomainException(
                "KEY_EXPIRED",
                "signing.key_expired",
                $"Signing key '{request.KeyId}' is outside its validity window.");
        }

        // 3. Check request-ID idempotency and validate all immutable fields
        var existingRequest = await _dbContext.CentralSigningRequests
            .FirstOrDefaultAsync(r => r.Id == request.SigningRequestId, cancellationToken);

        if (existingRequest is not null)
        {
            var isExactMatch =
                existingRequest.IssuanceRequestId == request.IssuanceRequestId &&
                string.Equals(existingRequest.Purpose, request.Purpose, StringComparison.Ordinal) &&
                string.Equals(existingRequest.KeyId, request.KeyId, StringComparison.Ordinal) &&
                existingRequest.KeyVersion == request.KeyVersion &&
                existingRequest.AuthorityRevision == request.AuthorityRevision &&
                string.Equals(existingRequest.PayloadSha256, request.PayloadSha256, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existingRequest.CanonicalPayload, request.CanonicalPayload, StringComparison.Ordinal);

            if (isExactMatch && existingRequest.Status == "SIGNED")
            {
                return new SigningResponseDto
                {
                    SigningRequestId = existingRequest.Id,
                    Signature = existingRequest.Signature,
                    KeyId = existingRequest.KeyId,
                    KeyVersion = existingRequest.KeyVersion,
                    AlgorithmProfileId = DefaultAlgorithm,
                    SignedAtUtc = existingRequest.SignedAtUtc ?? throw new CentralDomainException(
                        "SIGNING_RECORD_INCOMPLETE", "signing.record_incomplete", "A completed signing record must contain its signing timestamp.")
                };
            }

            if (!isExactMatch || existingRequest.Status != "PENDING")
            {
                throw new CentralDomainException("SIGNING_REQUEST_CONFLICT", "signing.conflict", "Immutable request or approved state mismatch.");
            }
        }
        else
        {
            throw new CentralDomainException("SIGNING_AUTHORITY_REQUIRED", "signing.authority_required", "A matching persisted authorized test issuance request is required.");
        }

        // 4. Retrieve isolated private key
        var rsa = _keyStore.GetKey(request.KeyId);
        if (rsa is null)
        {
            throw new CentralDomainException(
                "PRIVATE_KEY_UNAVAILABLE",
                "signing.key_unavailable",
                $"Private key material for '{request.KeyId}' is unavailable in secure memory.");
        }

        // 5. Produce RS256 signature
        var signatureBytes = rsa.SignData(payloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var signatureBase64 = Convert.ToBase64String(signatureBytes);

        // 6. Record signing request
        var record = existingRequest!;
        // PostgreSQL timestamps retain microseconds. Return the same precision as
        // the durable value so a response replayed after restart is identical.
        var signingTime = DateTimeOffset.UtcNow;
        var signedAtUtc = new DateTimeOffset(
            signingTime.Ticks - signingTime.Ticks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);
        record.CompleteSigning(signatureBase64, signedAtUtc);

        await _dbContext.SaveChangesAsync(cancellationToken);

        return new SigningResponseDto
        {
            SigningRequestId = record.Id,
            Signature = signatureBase64,
            KeyId = record.KeyId,
            KeyVersion = record.KeyVersion,
            AlgorithmProfileId = DefaultAlgorithm,
            SignedAtUtc = signedAtUtc
        };
    }

    // W1 isolated application/test seam. Never exposed as an HTTP self-approval endpoint.
    public async Task AuthorizeTestRequestAsync(SigningRequestDto request, CancellationToken cancellationToken = default)
    {
        if (request.SigningRequestId == Guid.Empty || request.IssuanceRequestId == Guid.Empty || request.AuthorityRevision <= 0)
        {
            throw new CentralDomainException("INVALID_AUTHORITY", "signing.invalid_authority", "Explicit isolated authority identity required.");
        }

        await using var transaction = await SecurityTransaction.BeginAsync(_dbContext, "central-signing-authority", cancellationToken);
        if (await _dbContext.CentralSigningRequests.AnyAsync(x => x.Id == request.SigningRequestId, cancellationToken))
        {
            throw new CentralDomainException("SIGNING_REQUEST_CONFLICT", "signing.conflict", "Request already authorized.");
        }

        _dbContext.CentralSigningRequests.Add(CentralSigningRequest.Create(request.SigningRequestId, request.IssuanceRequestId,
            request.Purpose, request.KeyId, request.KeyVersion, request.AuthorityRevision, request.PayloadSha256, request.CanonicalPayload, DateTimeOffset.UtcNow));
        await _dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
    }

    public async Task<KeyDescriptorDto> GenerateAndRegisterKeyAsync(
        string keyId,
        int keyVersion,
        string purpose,
        TimeSpan validity,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await SecurityTransaction.BeginAsync(_dbContext, "central-signing-authority", cancellationToken);
        if (await _dbContext.CentralSigningKeyRecords.AnyAsync(k => k.KeyId == keyId, cancellationToken))
        {
            throw new CentralDomainException("KEY_EXISTS", "signing.key_exists", "A key identity cannot be overwritten.");
        }

        var now = DateTimeOffset.UtcNow;
        var rsa = RSA.Create(2048);

        _keyStore.StoreKey(keyId, rsa);
        var publicKeyPem = rsa.ExportSubjectPublicKeyInfoPem();

        var keyRecord = CentralSigningKeyRecord.Create(
            keyId: keyId,
            keyVersion: keyVersion,
            algorithmProfileId: DefaultAlgorithm,
            purpose: purpose,
            publicKeyPem: publicKeyPem,
            validFromUtc: now,
            validUntilUtc: now.Add(validity),
            createdAtUtc: now);

        _dbContext.CentralSigningKeyRecords.Add(keyRecord);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Update Trust Set snapshot so registered key is immediately available
        var latestTrustSet = await _dbContext.CentralTrustSetRecords
            .OrderByDescending(t => t.TrustSetVersion)
            .FirstOrDefaultAsync(cancellationToken);

        var currentRevision = latestTrustSet?.EmergencyRolloverRevision ?? 0;
        await RebuildAndSignTrustSetAsync(currentRevision, cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
        return new KeyDescriptorDto
        {
            KeyId = keyRecord.KeyId,
            KeyVersion = keyRecord.KeyVersion,
            AlgorithmProfileId = keyRecord.AlgorithmProfileId,
            Purpose = keyRecord.Purpose,
            PublicKey = keyRecord.PublicKeyPem,
            ValidFromUtc = keyRecord.ValidFromUtc,
            ValidUntilUtc = keyRecord.ValidUntilUtc,
            Status = keyRecord.Status
        };
    }

    public async Task RotateKeyAsync(string keyId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await SecurityTransaction.BeginAsync(_dbContext, "central-signing-authority", cancellationToken);
        var keyRecord = await _dbContext.CentralSigningKeyRecords
            .FirstOrDefaultAsync(k => k.KeyId == keyId, cancellationToken);

        if (keyRecord is null)
        {
            throw new CentralDomainException("KEY_NOT_FOUND", "signing.key_not_found", $"Key '{keyId}' not found.");
        }

        keyRecord.RotateToVerifyOnly();
        await _dbContext.SaveChangesAsync(cancellationToken);

        var latestTrustSet = await _dbContext.CentralTrustSetRecords
            .OrderByDescending(t => t.TrustSetVersion)
            .FirstOrDefaultAsync(cancellationToken);

        var currentRevision = latestTrustSet?.EmergencyRolloverRevision ?? 0;
        await RebuildAndSignTrustSetAsync(currentRevision, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
    }

    public async Task RevokeKeyAsync(string keyId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await SecurityTransaction.BeginAsync(_dbContext, "central-signing-authority", cancellationToken);
        var keyRecord = await _dbContext.CentralSigningKeyRecords
            .FirstOrDefaultAsync(k => k.KeyId == keyId, cancellationToken);

        if (keyRecord is null)
        {
            throw new CentralDomainException("KEY_NOT_FOUND", "signing.key_not_found", $"Key '{keyId}' not found.");
        }

        var latestTrustSet = await _dbContext.CentralTrustSetRecords
            .OrderByDescending(t => t.TrustSetVersion)
            .FirstOrDefaultAsync(cancellationToken);

        var newRevision = (latestTrustSet?.EmergencyRolloverRevision ?? 0) + 1;
        keyRecord.Revoke(newRevision);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await RebuildAndSignTrustSetAsync(newRevision, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
    }

    private async Task<CentralTrustSetRecord> RebuildAndSignTrustSetAsync(int rolloverRevision, CancellationToken cancellationToken)
    {
        var allKeys = await _dbContext.CentralSigningKeyRecords.OrderBy(k => k.KeyId).ToListAsync(cancellationToken);
        var revokedIds = allKeys.Where(k => k.Status == KeyStatus.Revoked).Select(k => k.KeyId).ToList();

        var keyDtos = allKeys.Select(k => new KeyDescriptorDto
        {
            KeyId = k.KeyId,
            KeyVersion = k.KeyVersion,
            AlgorithmProfileId = k.AlgorithmProfileId,
            Purpose = k.Purpose,
            PublicKey = k.PublicKeyPem,
            ValidFromUtc = k.ValidFromUtc,
            ValidUntilUtc = k.ValidUntilUtc,
            Status = k.Status
        }).ToList();

        var latestTrustSet = await _dbContext.CentralTrustSetRecords
            .OrderByDescending(t => t.TrustSetVersion)
            .FirstOrDefaultAsync(cancellationToken);

        var newVersion = (latestTrustSet?.TrustSetVersion ?? 0) + 1;
        var keysJson = JsonSerializer.Serialize(keyDtos);
        var revokedJson = JsonSerializer.Serialize(revokedIds);

        var envelopeToSign = $"{newVersion}:{rolloverRevision}:{keysJson}:{revokedJson}";
        var envelopeBytes = Encoding.UTF8.GetBytes(envelopeToSign);

        var rootRsa = _keyStore.GetKey(InMemoryPrivateKeyStore.RootAuthorityKeyId);
        string signatureBase64;
        if (rootRsa is not null)
        {
            var sigBytes = rootRsa.SignData(envelopeBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            signatureBase64 = Convert.ToBase64String(sigBytes);
        }
        else
        {
            throw new CentralDomainException("ROOT_AUTHORITY_UNAVAILABLE", "signing.root_unavailable", "No digest or placeholder can substitute for a root signature.");
        }

        var trustSetRecord = CentralTrustSetRecord.Create(
            trustSetVersion: newVersion,
            emergencyRolloverRevision: rolloverRevision,
            signedByTrustedRootKeyId: InMemoryPrivateKeyStore.RootAuthorityKeyId,
            signature: signatureBase64,
            keysJson: keysJson,
            revokedKeyIdsJson: revokedJson,
            createdAtUtc: DateTimeOffset.UtcNow);

        _dbContext.CentralTrustSetRecords.Add(trustSetRecord);
        return trustSetRecord;
    }

    public async Task<TrustSetDto> GetTrustSetAsync(CancellationToken cancellationToken = default)
    {
        var record = await _dbContext.CentralTrustSetRecords
            .OrderByDescending(t => t.TrustSetVersion)
            .FirstOrDefaultAsync(cancellationToken);

        if (record is null)
        {
            record = await RebuildAndSignTrustSetAsync(0, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        var keys = JsonSerializer.Deserialize<List<KeyDescriptorDto>>(record.KeysJson) ?? new List<KeyDescriptorDto>();
        var revoked = JsonSerializer.Deserialize<List<string>>(record.RevokedKeyIdsJson) ?? new List<string>();

        return new TrustSetDto
        {
            TrustSetVersion = record.TrustSetVersion,
            EmergencyRolloverRevision = record.EmergencyRolloverRevision,
            SignedByTrustedRootKeyId = record.SignedByTrustedRootKeyId,
            Signature = record.Signature,
            Keys = keys,
            RevokedKeyIds = revoked
        };
    }

    public bool VerifyTrustSet(TrustSetDto trust, string pinnedRootPem, int minimumVersion = 1, int minimumRollover = 0)
    {
        if (trust.TrustSetVersion < minimumVersion || trust.EmergencyRolloverRevision < minimumRollover ||
            trust.SignedByTrustedRootKeyId != InMemoryPrivateKeyStore.RootAuthorityKeyId)
        {
            return false;
        }

        var envelope = $"{trust.TrustSetVersion}:{trust.EmergencyRolloverRevision}:{JsonSerializer.Serialize(trust.Keys)}:{JsonSerializer.Serialize(trust.RevokedKeyIds)}";
        return VerifySignature(pinnedRootPem, envelope, trust.Signature);
    }

    public bool VerifyTrustedPayload(TrustSetDto trust, string pinnedRootPem, string keyId, int keyVersion,
        string purpose, string algorithm, string payload, string signature, DateTimeOffset atUtc)
    {
        if (!VerifyTrustSet(trust, pinnedRootPem) || trust.RevokedKeyIds.Contains(keyId))
        {
            return false;
        }
        var key = trust.Keys.SingleOrDefault(k => k.KeyId == keyId);
        return key is not null && key.KeyVersion == keyVersion && key.Purpose == purpose &&
            algorithm == DefaultAlgorithm && key.AlgorithmProfileId == algorithm &&
            (key.Status == KeyStatus.Active || key.Status == KeyStatus.VerifyOnly) && atUtc >= key.ValidFromUtc && atUtc < key.ValidUntilUtc &&
            VerifySignature(key.PublicKey, payload, signature);
    }

    public bool VerifySignature(string publicKeyPem, string canonicalPayload, string signatureBase64)
    {
        if (string.IsNullOrWhiteSpace(publicKeyPem) ||
            string.IsNullOrWhiteSpace(canonicalPayload) ||
            string.IsNullOrWhiteSpace(signatureBase64))
        {
            return false;
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem);

            var payloadBytes = Encoding.UTF8.GetBytes(canonicalPayload);
            var signatureBytes = Convert.FromBase64String(signatureBase64);

            return rsa.VerifyData(payloadBytes, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch
        {
            return false;
        }
    }
}
