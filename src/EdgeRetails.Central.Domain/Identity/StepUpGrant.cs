using EdgeRetails.Central.Domain.Common;

namespace EdgeRetails.Central.Domain.Identity;

public class StepUpGrant : Entity
{
    public Guid SessionId { get; private set; }
    public string ActionScope { get; private set; } = string.Empty;
    public string TargetPayloadSha256 { get; private set; } = string.Empty;
    public Guid ChallengeId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset ExpiresAtUtc { get; private set; }
    public bool IsConsumed { get; private set; }
    public DateTimeOffset? ConsumedAtUtc { get; private set; }

    private StepUpGrant() { }

    public static StepUpGrant Create(
        Guid sessionId,
        string actionScope,
        string targetPayloadSha256,
        Guid challengeId,
        DateTimeOffset createdAtUtc,
        TimeSpan validity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionScope);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPayloadSha256);

        return new StepUpGrant
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            ActionScope = actionScope,
            TargetPayloadSha256 = targetPayloadSha256,
            ChallengeId = challengeId,
            CreatedAtUtc = createdAtUtc,
            ExpiresAtUtc = createdAtUtc.Add(validity),
            IsConsumed = false
        };
    }

    public bool IsValid(Guid sessionId, string actionScope, string targetPayloadSha256, DateTimeOffset now)
    {
        return !IsConsumed
            && SessionId == sessionId
            && string.Equals(ActionScope, actionScope, StringComparison.Ordinal)
            && string.Equals(TargetPayloadSha256, targetPayloadSha256, StringComparison.OrdinalIgnoreCase)
            && now < ExpiresAtUtc;
    }

    public void Consume(DateTimeOffset now)
    {
        IsConsumed = true;
        ConsumedAtUtc = now;
    }
}
