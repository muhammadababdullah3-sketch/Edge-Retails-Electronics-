using EdgeRetails.Central.Domain.Common;

namespace EdgeRetails.Central.Domain.Identity;

public class LoginChallenge : Entity
{
    public Guid AdminUserId { get; private set; }
    public AdminUser AdminUser { get; private set; } = null!;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset ExpiresAtUtc { get; private set; }
    public bool IsConsumed { get; private set; }
    public DateTimeOffset? ConsumedAtUtc { get; private set; }
    public int FailedAttempts { get; private set; }

    private LoginChallenge() { }

    public static LoginChallenge Create(Guid adminUserId, DateTimeOffset createdAtUtc, TimeSpan validity)
    {
        return new LoginChallenge
        {
            Id = Guid.NewGuid(),
            AdminUserId = adminUserId,
            CreatedAtUtc = createdAtUtc,
            ExpiresAtUtc = createdAtUtc.Add(validity),
            IsConsumed = false,
            FailedAttempts = 0
        };
    }

    public bool IsValid(DateTimeOffset now)
    {
        return !IsConsumed && now < ExpiresAtUtc && FailedAttempts < 5;
    }

    public void Consume(DateTimeOffset consumedAtUtc)
    {
        IsConsumed = true;
        ConsumedAtUtc = consumedAtUtc;
    }

    public void RecordFailure()
    {
        FailedAttempts++;
    }
}
