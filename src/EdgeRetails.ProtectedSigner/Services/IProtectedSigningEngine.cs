using EdgeRetails.Central.Application.DTOs;

namespace EdgeRetails.ProtectedSigner.Services;

public interface IProtectedSigningEngine
{
    Task AuthorizeTestRequestAsync(SigningRequestDto request, CancellationToken cancellationToken = default);
    Task<SigningResponseDto> SignAsync(SigningRequestDto request, CancellationToken cancellationToken = default);
    Task<TrustSetDto> GetTrustSetAsync(CancellationToken cancellationToken = default);
    Task<KeyDescriptorDto> GenerateAndRegisterKeyAsync(string keyId, int keyVersion, string purpose, TimeSpan validity, CancellationToken cancellationToken = default);
    Task RotateKeyAsync(string keyId, CancellationToken cancellationToken = default);
    Task RevokeKeyAsync(string keyId, CancellationToken cancellationToken = default);
    bool VerifySignature(string publicKeyPem, string canonicalPayload, string signatureBase64);
}
