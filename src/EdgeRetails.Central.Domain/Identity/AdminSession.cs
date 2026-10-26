using EdgeRetails.Central.Domain.Common;

namespace EdgeRetails.Central.Domain.Identity;

public class AdminSession : Entity
{
    public Guid AdminUserId { get; private set; }
    public AdminUser AdminUser { get; private set; } = null!;
    public string TokenHash { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset ExpiresAtUtc { get; private set; }
    public DateTimeOffset AbsoluteExpiresAtUtc { get; private set; }
    public DateTimeOffset LastActivityAtUtc { get; private set; }
    public bool MfaSatisfied { get; private set; }
    public int SecurityRevisionAtCreation { get; private set; }
    public bool IsRevoked { get; private set; }
    public DateTimeOffset? RevokedAtUtc { get; private set; }
    public string? RevocationReason { get; private set; }
    public string CapabilitiesJson { get; private set; } = "[]";

    private AdminSession() { }

    public static AdminSession Create(
        Guid adminUserId,
        string tokenHash,
        DateTimeOffset createdAtUtc,
        TimeSpan idleTimeout,
        TimeSpan absoluteLifetime,
        bool mfaSatisfied,
        int securityRevision,
        IEnumerable<string> capabilities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);

        var caps = capabilities.Distinct(StringComparer.Ordinal).ToList();
        return new AdminSession
        {
            Id = Guid.NewGuid(),
            AdminUserId = adminUserId,
            TokenHash = tokenHash,
            CreatedAtUtc = createdAtUtc,
            ExpiresAtUtc = createdAtUtc.Add(idleTimeout),
            AbsoluteExpiresAtUtc = createdAtUtc.Add(absoluteLifetime),
            LastActivityAtUtc = createdAtUtc,
            MfaSatisfied = mfaSatisfied,
            SecurityRevisionAtCreation = securityRevision,
            IsRevoked = false,
            CapabilitiesJson = System.Text.Json.JsonSerializer.Serialize(caps)
        };
    }

    public bool IsValid(DateTimeOffset now, int currentUserSecurityRevision)
    {
        if (IsRevoked)
        {
            return false;
        }
        if (now >= ExpiresAtUtc)
        {
            return false;
        }
        if (now >= AbsoluteExpiresAtUtc)
        {
            return false;
        }
        if (SecurityRevisionAtCreation != currentUserSecurityRevision)
        {
            return false;
        }
        return true;
    }

    public void Touch(DateTimeOffset now, TimeSpan idleTimeout)
    {
        LastActivityAtUtc = now;
        var newIdle = now.Add(idleTimeout);
        ExpiresAtUtc = newIdle < AbsoluteExpiresAtUtc ? newIdle : AbsoluteExpiresAtUtc;
    }

    public void Revoke(string reason, DateTimeOffset revokedAtUtc)
    {
        IsRevoked = true;
        RevokedAtUtc = revokedAtUtc;
        RevocationReason = reason;
    }

    public IReadOnlyList<string> GetCapabilities()
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(CapabilitiesJson) ?? new List<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
