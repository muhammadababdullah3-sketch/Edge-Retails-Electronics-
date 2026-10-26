using EdgeRetails.Central.Application.DTOs;

namespace EdgeRetails.Central.Application.Abstractions;

public interface IAdminAuthService
{
    Task<PreAuthChallengeDto> StartLoginAsync(LoginRequestDto request, bool allowLocalPasswordBypass = false, CancellationToken cancellationToken = default);
    Task<AdminSessionDto> CompleteMfaAsync(MfaRequestDto request, CancellationToken cancellationToken = default);
    Task<AdminSessionDto?> ValidateSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<bool> LogoutAsync(Guid sessionId, string reason, CancellationToken cancellationToken = default);
}
