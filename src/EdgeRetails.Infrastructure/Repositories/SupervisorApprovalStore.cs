using System.Security.Cryptography;
using System.Text;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Domain.Posting;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Infrastructure.Repositories;

public sealed class EfSupervisorApprovalStore(EdgeRetailsDbContext db) : ISupervisorApprovalStore
{
    public async Task<string> IssueAsync(
        SupervisorApprovalGrantIssueRequest request,
        CancellationToken cancellationToken = default)
    {
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(tokenBytes);
        var tokenHash = HashToken(token);

        var grant = new SupervisorApprovalGrant
        {
            Id = Guid.CreateVersion7(),
            TokenHash = tokenHash,
            SupervisorId = request.SupervisorId,
            PostingDate = request.PostingDate,
            OperationType = request.OperationType.Trim(),
            Reason = request.Reason.Trim(),
            ExpiresAtUtc = request.ExpiresAtUtc,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Consumed = false
        };

        db.SupervisorApprovalGrants.Add(grant);
        await db.SaveChangesAsync(cancellationToken);
        return token;
    }

    public async Task<Result<SupervisorApprovalGrantSnapshot>> TryConsumeAsync(
        string approvalToken,
        string operationType,
        DateOnly postingDate,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(approvalToken))
        {
            return Result<SupervisorApprovalGrantSnapshot>.Failure(
                "authorization.approval_token_required",
                "A supervisor approval token is required for this historical posting.");
        }

        var tokenHash = HashToken(approvalToken.Trim());
        var grant = await db.SupervisorApprovalGrants
            .FromSqlInterpolated(
                $"SELECT * FROM posting.supervisor_approval_grants WHERE token_hash = {tokenHash} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

        if (grant is null)
        {
            return Result<SupervisorApprovalGrantSnapshot>.Failure(
                "authorization.approval_token_invalid",
                "Supervisor approval token is invalid or expired.");
        }

        if (grant.Consumed)
        {
            return Result<SupervisorApprovalGrantSnapshot>.Failure(
                "authorization.approval_token_consumed",
                "Supervisor approval token was already used.");
        }

        if (grant.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            return Result<SupervisorApprovalGrantSnapshot>.Failure(
                "authorization.approval_token_expired",
                "Supervisor approval token has expired.");
        }

        if (!string.Equals(grant.OperationType, operationType.Trim(), StringComparison.Ordinal))
        {
            return Result<SupervisorApprovalGrantSnapshot>.Failure(
                "authorization.approval_token_operation_mismatch",
                "Supervisor approval token does not match this operation type.");
        }

        if (grant.PostingDate != postingDate)
        {
            return Result<SupervisorApprovalGrantSnapshot>.Failure(
                "authorization.approval_token_posting_date_mismatch",
                "Supervisor approval token does not match the posting date.");
        }

        grant.Consumed = true;
        grant.ConsumedAtUtc = DateTimeOffset.UtcNow;

        return Result<SupervisorApprovalGrantSnapshot>.Success(
            new SupervisorApprovalGrantSnapshot(
                grant.SupervisorId,
                grant.PostingDate,
                grant.OperationType,
                grant.Reason));
    }

    private static string HashToken(string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hash);
    }
}
