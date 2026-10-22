using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Domain.Signing;
using EdgeRetails.ProtectedSigner.Keys;
using EdgeRetails.ProtectedSigner.Services;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Central.Api.Services;

public class ProtectedSignerClient : IProtectedSignerClient
{
    private readonly IProtectedSigningEngine _signingEngine;
    private readonly ICentralDbContext _dbContext;
    private readonly IPrivateKeyStore _keyStore;

    public ProtectedSignerClient(
        IProtectedSigningEngine signingEngine,
        ICentralDbContext dbContext,
        IPrivateKeyStore keyStore)
    {
        _signingEngine = signingEngine ?? throw new ArgumentNullException(nameof(signingEngine));
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
    }

    public Task<SigningResponseDto> SignAsync(SigningRequestDto request, CancellationToken cancellationToken = default)
    {
        return _signingEngine.SignAsync(request, cancellationToken);
    }

    public Task<TrustSetDto> GetTrustSetAsync(CancellationToken cancellationToken = default)
    {
        return _signingEngine.GetTrustSetAsync(cancellationToken);
    }

    public async Task<KeyDescriptorDto> EnsureActiveKeyAsync(string purpose, CancellationToken cancellationToken = default)
    {
        var targetPurpose = purpose == "LICENSE_CERTIFICATE" ? SigningPurpose.License : purpose;
        var activeKeys = await _dbContext.CentralSigningKeyRecords
            .Where(k => (k.Purpose == targetPurpose || k.Purpose == purpose) && k.Status == KeyStatus.Active)
            .OrderByDescending(k => k.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        var usableKey = activeKeys.FirstOrDefault(k => _keyStore.HasKey(k.KeyId));
        if (usableKey != null)
        {
            return new KeyDescriptorDto
            {
                KeyId = usableKey.KeyId,
                KeyVersion = usableKey.KeyVersion,
                AlgorithmProfileId = usableKey.AlgorithmProfileId,
                Purpose = usableKey.Purpose,
                PublicKey = usableKey.PublicKeyPem,
                ValidFromUtc = usableKey.ValidFromUtc,
                ValidUntilUtc = usableKey.ValidUntilUtc,
                Status = usableKey.Status
            };
        }

        var newKeyId = $"central-{purpose.ToLowerInvariant().Replace('_', '-')}-{Guid.NewGuid():N}";
        return await _signingEngine.GenerateAndRegisterKeyAsync(newKeyId, 1, targetPurpose, TimeSpan.FromDays(365), cancellationToken);
    }

    public bool VerifySignature(string publicKeyPem, string canonicalPayload, string signatureHexOrBase64)
    {
        if (_signingEngine.VerifySignature(publicKeyPem, canonicalPayload, signatureHexOrBase64))
        {
            return true;
        }

        try
        {
            var bytes = Convert.FromHexString(signatureHexOrBase64);
            var b64 = Convert.ToBase64String(bytes);
            return _signingEngine.VerifySignature(publicKeyPem, canonicalPayload, b64);
        }
        catch
        {
            return false;
        }
    }
}
