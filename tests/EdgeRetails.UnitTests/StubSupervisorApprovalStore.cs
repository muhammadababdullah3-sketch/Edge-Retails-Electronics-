using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;

namespace EdgeRetails.UnitTests;

internal sealed class InMemorySupervisorApprovalStore : ISupervisorApprovalStore
{
    private readonly Dictionary<string, SupervisorApprovalGrantSnapshot> _active = new(StringComparer.Ordinal);

    public Task<string> IssueAsync(SupervisorApprovalGrantIssueRequest request, CancellationToken cancellationToken = default)
    {
        var token = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        _active[token] = new SupervisorApprovalGrantSnapshot(
            request.SupervisorId,
            request.PostingDate,
            request.OperationType,
            request.Reason);
        return Task.FromResult(token);
    }

    public Task<Result<SupervisorApprovalGrantSnapshot>> TryConsumeAsync(
        string approvalToken,
        string operationType,
        DateOnly postingDate,
        CancellationToken cancellationToken = default)
    {
        if (!_active.TryGetValue(approvalToken, out var snapshot))
        {
            return Task.FromResult(Result<SupervisorApprovalGrantSnapshot>.Failure(
                "authorization.approval_token_invalid",
                "Supervisor approval token is invalid or expired."));
        }

        if (!string.Equals(snapshot.OperationType, operationType, StringComparison.Ordinal) ||
            snapshot.PostingDate != postingDate)
        {
            return Task.FromResult(Result<SupervisorApprovalGrantSnapshot>.Failure(
                "authorization.approval_token_operation_mismatch",
                "Supervisor approval token does not match this operation."));
        }

        _active.Remove(approvalToken);
        return Task.FromResult(Result<SupervisorApprovalGrantSnapshot>.Success(snapshot));
    }
}

internal sealed class StubSupervisorApprovalStore : ISupervisorApprovalStore
{
    public Task<string> IssueAsync(SupervisorApprovalGrantIssueRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult("stub-approval-token");

    public Task<Result<SupervisorApprovalGrantSnapshot>> TryConsumeAsync(
        string approvalToken,
        string operationType,
        DateOnly postingDate,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<SupervisorApprovalGrantSnapshot>.Failure(
            "authorization.approval_token_invalid",
            "Stub supervisor approval store does not validate tokens."));
}
