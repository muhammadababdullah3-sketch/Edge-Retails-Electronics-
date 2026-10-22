using EdgeRetails.Central.Application.DTOs;

namespace EdgeRetails.Central.Application.Abstractions;

public interface IStepUpService
{
    Task<StepUpGrantDto> VerifyAndGrantStepUpAsync(StepUpRequestDto request, CancellationToken cancellationToken = default);
    Task<bool> ValidateStepUpGrantAsync(Guid sessionId, string actionScope, string targetPayloadSha256, CancellationToken cancellationToken = default);
    Task<bool> ValidateAndConsumeStepUpGrantAsync(Guid sessionId, string actionScope, string targetPayloadSha256, Guid? grantId = null, CancellationToken cancellationToken = default);
}
