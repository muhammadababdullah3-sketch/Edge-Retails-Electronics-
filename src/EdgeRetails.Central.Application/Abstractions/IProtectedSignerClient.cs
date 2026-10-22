using EdgeRetails.Central.Application.DTOs;

namespace EdgeRetails.Central.Application.Abstractions;

public interface IProtectedSignerClient
{
    Task<SigningResponseDto> SignAsync(SigningRequestDto request, CancellationToken cancellationToken = default);
    Task<TrustSetDto> GetTrustSetAsync(CancellationToken cancellationToken = default);
    Task<KeyDescriptorDto> EnsureActiveKeyAsync(string purpose, CancellationToken cancellationToken = default);
    bool VerifySignature(string publicKeyPem, string canonicalPayload, string signatureHex);
}

