using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Domain.Common;
using EdgeRetails.Central.Domain.Signing;
using EdgeRetails.Central.Infrastructure.Persistence;
using EdgeRetails.ProtectedSigner.Keys;
using EdgeRetails.ProtectedSigner.Services;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Central.UnitTests.Signing;

public class ProtectedSigningUnitTests : IDisposable
{
    private readonly CentralDbContext _dbContext;
    private readonly InMemoryPrivateKeyStore _keyStore = new();
    private readonly ProtectedSigningEngine _engine;

    public ProtectedSigningUnitTests()
    {
        var options = new DbContextOptionsBuilder<CentralDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new CentralDbContext(options);
        _engine = new ProtectedSigningEngine(_dbContext, _keyStore);
    }

    [Fact]
    public async Task SignAsync_ProducesValidRs256Signature_VerifiableByPublicKey()
    {
        const string keyId = "test-key-2026-v1";
        var keyDesc = await _engine.GenerateAndRegisterKeyAsync(
            keyId, 1, SigningPurpose.License, TimeSpan.FromDays(365));

        const string canonicalPayload = """{"licenseId":"lic-123","shopId":"shop-456"}""";
        var payloadBytes = System.Text.Encoding.UTF8.GetBytes(canonicalPayload);
        var sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payloadBytes));

        var reqId = Guid.NewGuid();
        var request = new SigningRequestDto
        {
            SigningRequestId = reqId,
            IssuanceRequestId = Guid.NewGuid(),
            PayloadSha256 = sha256,
            AuthorityRevision = 1,
            KeyId = keyId,
            KeyVersion = 1,
            Purpose = SigningPurpose.License,
            CanonicalPayload = canonicalPayload
        };

        await _engine.AuthorizeTestRequestAsync(request);
        var response = await _engine.SignAsync(request);

        Assert.NotNull(response);
        Assert.Equal(reqId, response.SigningRequestId);
        Assert.Equal("RS256", response.AlgorithmProfileId);
        Assert.NotEmpty(response.Signature);

        // Cross-verify with public key PEM
        var isValid = _engine.VerifySignature(keyDesc.PublicKey, canonicalPayload, response.Signature);
        Assert.True(isValid);
    }

    [Fact]
    public async Task SignAsync_TamperedPayload_ThrowsTamperedPayloadException()
    {
        const string keyId = "test-key-tamper";
        await _engine.GenerateAndRegisterKeyAsync(keyId, 1, SigningPurpose.License, TimeSpan.FromDays(365));

        const string originalPayload = """{"amount":100}""";
        var originalBytes = System.Text.Encoding.UTF8.GetBytes(originalPayload);
        var sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(originalBytes));

        // Client supplies tampered payload but claims original hash
        var request = new SigningRequestDto
        {
            SigningRequestId = Guid.NewGuid(),
            IssuanceRequestId = Guid.NewGuid(),
            PayloadSha256 = sha256,
            AuthorityRevision = 1,
            KeyId = keyId,
            KeyVersion = 1,
            Purpose = SigningPurpose.License,
            CanonicalPayload = """{"amount":1000000}""" // Tampered!
        };

        var ex = await Assert.ThrowsAsync<CentralDomainException>(() => _engine.SignAsync(request));
        Assert.Equal("TAMPERED_PAYLOAD", ex.Code);
    }

    [Fact]
    public async Task SignAsync_IdempotentReplay_ReturnsExistingSignature()
    {
        const string keyId = "test-key-idempotent";
        await _engine.GenerateAndRegisterKeyAsync(keyId, 1, SigningPurpose.License, TimeSpan.FromDays(365));

        const string canonicalPayload = """{"test":true}""";
        var payloadBytes = System.Text.Encoding.UTF8.GetBytes(canonicalPayload);
        var sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payloadBytes));

        var reqId = Guid.NewGuid();
        var request = new SigningRequestDto
        {
            SigningRequestId = reqId,
            IssuanceRequestId = Guid.NewGuid(),
            PayloadSha256 = sha256,
            AuthorityRevision = 1,
            KeyId = keyId,
            KeyVersion = 1,
            Purpose = SigningPurpose.License,
            CanonicalPayload = canonicalPayload
        };

        await _engine.AuthorizeTestRequestAsync(request);
        var response1 = await _engine.SignAsync(request);
        var response2 = await _engine.SignAsync(request);

        Assert.Equal(response1.Signature, response2.Signature);
        Assert.Equal(response1.SigningRequestId, response2.SigningRequestId);
    }

    [Fact]
    public async Task KeyLifecycle_RotationAndEmergencyRevocation_EnforcesStatusGuards()
    {
        const string keyId = "test-key-lifecycle";
        await _engine.GenerateAndRegisterKeyAsync(keyId, 1, SigningPurpose.License, TimeSpan.FromDays(365));

        // Rotate to VERIFY_ONLY
        await _engine.RotateKeyAsync(keyId);
        var keyRecord = await _dbContext.CentralSigningKeyRecords.FindAsync(keyId);
        Assert.NotNull(keyRecord);
        Assert.Equal(KeyStatus.VerifyOnly, keyRecord.Status);

        // Emergency Revoke
        await _engine.RevokeKeyAsync(keyId);
        var revokedRecord = await _dbContext.CentralSigningKeyRecords.FindAsync(keyId);
        Assert.NotNull(revokedRecord);
        Assert.Equal(KeyStatus.Revoked, revokedRecord.Status);

        // Signing with revoked key must fail
        const string canonicalPayload = """{"data":"secure"}""";
        var payloadBytes = System.Text.Encoding.UTF8.GetBytes(canonicalPayload);
        var sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payloadBytes));

        var request = new SigningRequestDto
        {
            SigningRequestId = Guid.NewGuid(),
            IssuanceRequestId = Guid.NewGuid(),
            PayloadSha256 = sha256,
            AuthorityRevision = 1,
            KeyId = keyId,
            KeyVersion = 1,
            Purpose = SigningPurpose.License,
            CanonicalPayload = canonicalPayload
        };

        var ex = await Assert.ThrowsAsync<CentralDomainException>(() => _engine.SignAsync(request));
        Assert.Equal("KEY_REVOKED", ex.Code);

        // Trust set must include revoked key ID
        var trustSet = await _engine.GetTrustSetAsync();
        Assert.Contains(keyId, trustSet.RevokedKeyIds);
        Assert.True(trustSet.EmergencyRolloverRevision > 0);
    }

    [Fact]
    public async Task PersistedAuthority_AndImmutableReplay_AreRequired()
    {
        await _engine.GenerateAndRegisterKeyAsync("bound-key", 1, SigningPurpose.License, TimeSpan.FromDays(1));
        var request = NewRequest("bound-key");
        var missing = await Assert.ThrowsAsync<CentralDomainException>(() => _engine.SignAsync(request));
        Assert.Equal("SIGNING_AUTHORITY_REQUIRED", missing.Code);
        await _engine.AuthorizeTestRequestAsync(request);
        var result = await _engine.SignAsync(request);
        request.AuthorityRevision++;
        var conflict = await Assert.ThrowsAsync<CentralDomainException>(() => _engine.SignAsync(request));
        Assert.Equal("SIGNING_REQUEST_CONFLICT", conflict.Code);
        request.AuthorityRevision--;
        await _engine.RevokeKeyAsync("bound-key");
        var revoked = await Assert.ThrowsAsync<CentralDomainException>(() => _engine.SignAsync(request));
        Assert.Equal("KEY_REVOKED", revoked.Code);
    }

    [Fact]
    public async Task TrustEnvelope_AuthenticatesCurrentKeysAndRevocationAgainstPinnedRoot()
    {
        await _engine.GenerateAndRegisterKeyAsync("trust-v1", 1, SigningPurpose.License, TimeSpan.FromDays(1));
        var request = NewRequest("trust-v1");
        await _engine.AuthorizeTestRequestAsync(request);
        var signed = await _engine.SignAsync(request);
        var root = _keyStore.GetPublicKeyPem(InMemoryPrivateKeyStore.RootAuthorityKeyId)!;
        var trust = await _engine.GetTrustSetAsync();
        Assert.True(_engine.VerifyTrustSet(trust, root));
        Assert.True(_engine.VerifyTrustedPayload(trust, root, "trust-v1", 1, SigningPurpose.License, "RS256", request.CanonicalPayload, signed.Signature, DateTimeOffset.UtcNow));
        Assert.False(_engine.VerifyTrustedPayload(trust, root, "trust-v1", 2, SigningPurpose.License, "RS256", request.CanonicalPayload, signed.Signature, DateTimeOffset.UtcNow));
        Assert.False(_engine.VerifyTrustedPayload(trust, root, "trust-v1", 1, "UPDATE", "RS256", request.CanonicalPayload, signed.Signature, DateTimeOffset.UtcNow));
        using var wrongRoot = System.Security.Cryptography.RSA.Create(2048);
        Assert.False(_engine.VerifyTrustSet(trust, wrongRoot.ExportSubjectPublicKeyInfoPem()));
        trust.EmergencyRolloverRevision++;
        Assert.False(_engine.VerifyTrustSet(trust, root));
        await _engine.RotateKeyAsync("trust-v1");
        await _engine.GenerateAndRegisterKeyAsync("trust-v2", 2, SigningPurpose.License, TimeSpan.FromDays(1));
        var overlap = await _engine.GetTrustSetAsync();
        Assert.Contains(overlap.Keys, k => k.KeyId == "trust-v2");
        Assert.True(_engine.VerifyTrustedPayload(overlap, root, "trust-v1", 1, SigningPurpose.License, "RS256", request.CanonicalPayload, signed.Signature, DateTimeOffset.UtcNow));
        await _engine.RevokeKeyAsync("trust-v1");
        var revoked = await _engine.GetTrustSetAsync();
        Assert.False(_engine.VerifyTrustedPayload(revoked, root, "trust-v1", 1, SigningPurpose.License, "RS256", request.CanonicalPayload, signed.Signature, DateTimeOffset.UtcNow));
        Assert.False(_engine.VerifyTrustSet(overlap, root, revoked.TrustSetVersion, revoked.EmergencyRolloverRevision));
    }

    [Fact]
    public async Task NonCanonicalOrWrongVersion_CannotAcquireSignature()
    {
        await _engine.GenerateAndRegisterKeyAsync("profile-key", 1, SigningPurpose.License, TimeSpan.FromDays(1));
        var request = NewRequest("profile-key");
        request.KeyVersion = 2;
        Assert.Equal("KEY_VERSION_MISMATCH", (await Assert.ThrowsAsync<CentralDomainException>(() => _engine.SignAsync(request))).Code);
        request.KeyVersion = 1;
        request.CanonicalPayload = "{ \"test\":true }";
        Assert.Equal("NONCANONICAL_PAYLOAD", (await Assert.ThrowsAsync<CentralDomainException>(() => _engine.SignAsync(request))).Code);
    }

    private static SigningRequestDto NewRequest(string keyId) => new()
    {
        SigningRequestId = Guid.NewGuid(), IssuanceRequestId = Guid.NewGuid(), KeyId = keyId, KeyVersion = 1,
        AuthorityRevision = 1, Purpose = SigningPurpose.License, CanonicalPayload = "{\"test\":true}",
        PayloadSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("{\"test\":true}")))
    };

    public void Dispose()
    {
        _keyStore.Dispose();
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }
}
