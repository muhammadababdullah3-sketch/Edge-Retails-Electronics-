using EdgeRetails.Application.Common;

namespace EdgeRetails.Application.Abstractions;

public sealed record SupervisorApprovalGrantIssueRequest(
    Guid SupervisorId,
    DateOnly PostingDate,
    string OperationType,
    string Reason,
    DateTimeOffset ExpiresAtUtc);

public sealed record SupervisorApprovalGrantSnapshot(
    Guid SupervisorId,
    DateOnly PostingDate,
    string OperationType,
    string Reason);

public interface ISupervisorApprovalStore
{
    Task<string> IssueAsync(SupervisorApprovalGrantIssueRequest request, CancellationToken cancellationToken = default);

    Task<Result<SupervisorApprovalGrantSnapshot>> TryConsumeAsync(
        string approvalToken,
        string operationType,
        DateOnly postingDate,
        CancellationToken cancellationToken = default);
}
