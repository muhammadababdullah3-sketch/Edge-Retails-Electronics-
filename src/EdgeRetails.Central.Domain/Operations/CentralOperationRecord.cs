using EdgeRetails.Central.Domain.Common;

namespace EdgeRetails.Central.Domain.Operations;

public class CentralOperationRecord : Entity
{
    public Guid ClientOperationId { get; private set; }
    public string OperationType { get; private set; } = string.Empty;
    public string ActorScope { get; private set; } = string.Empty;
    public Guid? ShopId { get; private set; }
    public string? TargetResource { get; private set; }
    public int FingerprintVersion { get; private set; }
    public string PayloadSha256 { get; private set; } = string.Empty;
    public string CanonicalPayload { get; private set; } = string.Empty;
    public int ExpectedVersion { get; private set; }
    public int PolicyVersion { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public Guid? SensitiveApprovalId { get; private set; }

    public OperationState State { get; private set; }
    public Guid OutcomeId { get; private set; }
    public string? ResultReference { get; private set; }
    public int ResourceVersion { get; private set; }
    public int AuthorityRevision { get; private set; }
    public string? ErrorCode { get; private set; }
    public string? ErrorMessageKey { get; private set; }
    public bool Retryable { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }

    private CentralOperationRecord() { }

    public static CentralOperationRecord CreatePending(
        Guid clientOperationId,
        string operationType,
        string actorScope,
        Guid? shopId,
        int fingerprintVersion,
        string payloadSha256,
        string canonicalPayload,
        int expectedVersion,
        int policyVersion,
        string reason,
        Guid? sensitiveApprovalId,
        DateTimeOffset createdAtUtc,
        string? targetResource = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationType);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorScope);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return new CentralOperationRecord
        {
            Id = Guid.NewGuid(),
            ClientOperationId = clientOperationId,
            OperationType = operationType,
            ActorScope = actorScope,
            ShopId = shopId,
            FingerprintVersion = fingerprintVersion,
            PayloadSha256 = payloadSha256,
            CanonicalPayload = canonicalPayload,
            ExpectedVersion = expectedVersion,
            PolicyVersion = policyVersion,
            Reason = reason,
            SensitiveApprovalId = sensitiveApprovalId,
            State = OperationState.PENDING,
            OutcomeId = Guid.NewGuid(),
            TargetResource = targetResource,
            ResourceVersion = expectedVersion,
            AuthorityRevision = 0,
            CreatedAtUtc = createdAtUtc
        };
    }

    public void MarkSucceeded(
        string? resultReference,
        int resourceVersion,
        int authorityRevision,
        DateTimeOffset completedAtUtc)
    {
        State = OperationState.SUCCEEDED;
        ResultReference = resultReference ?? ResultReference;
        ResourceVersion = resourceVersion;
        AuthorityRevision = authorityRevision;
        CompletedAtUtc = completedAtUtc;
    }

    public void MarkFailed(
        string errorCode,
        string errorMessageKey,
        bool retryable,
        int currentVersion,
        DateTimeOffset completedAtUtc)
    {
        State = OperationState.FAILED_FINAL;
        ErrorCode = errorCode;
        ErrorMessageKey = errorMessageKey;
        Retryable = retryable;
        ResourceVersion = currentVersion;
        CompletedAtUtc = completedAtUtc;
    }

    public bool MatchesIdempotency(string actorScope, string operationType, string payloadSha256)
    {
        return MatchesIdempotency(actorScope, ShopId, operationType, FingerprintVersion, payloadSha256, TargetResource);
    }

    public bool MatchesIdempotency(
        string actorScope,
        Guid? shopId,
        string operationType,
        int fingerprintVersion,
        string payloadSha256,
        string? targetResource = null)
    {
        if (!string.Equals(ActorScope, actorScope, StringComparison.Ordinal))
        {
            return false;
        }

        if (!Nullable.Equals(ShopId, shopId))
        {
            return false;
        }

        if (!string.Equals(OperationType, operationType, StringComparison.Ordinal))
        {
            return false;
        }

        if (FingerprintVersion != fingerprintVersion)
        {
            return false;
        }

        if (!string.Equals(PayloadSha256, payloadSha256, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(TargetResource, targetResource, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }
}
