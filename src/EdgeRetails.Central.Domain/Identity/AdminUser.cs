using EdgeRetails.Central.Domain.Common;

namespace EdgeRetails.Central.Domain.Identity;

public class AdminUser : Entity
{
    public string Username { get; private set; } = string.Empty;
    public string PasswordHash { get; private set; } = string.Empty;
    public string Salt { get; private set; } = string.Empty;
    public string? MfaSecret { get; private set; }
    public bool MfaEnabled { get; private set; }
    public bool IsActive { get; private set; }
    public int SecurityRevision { get; private set; }
    public int RowVersion { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? LastLoginAtUtc { get; private set; }

    public List<AdminUserRole> UserRoles { get; private set; } = new();

    private AdminUser() { }

    public static AdminUser Create(
        string username,
        string passwordHash,
        string salt,
        string? mfaSecret,
        bool mfaEnabled,
        DateTimeOffset createdAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(salt);

        return new AdminUser
        {
            Id = Guid.NewGuid(),
            Username = username.Trim().ToLowerInvariant(),
            PasswordHash = passwordHash,
            Salt = salt,
            MfaSecret = mfaSecret,
            MfaEnabled = mfaEnabled,
            IsActive = true,
            SecurityRevision = 1,
            RowVersion = 1,
            CreatedAtUtc = createdAtUtc
        };
    }

    public void ChangeUsername(string username)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        Username = username.Trim().ToLowerInvariant();
        BumpSecurityRevision();
    }

    public void UpdatePassword(string newHash, string newSalt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(newSalt);

        PasswordHash = newHash;
        Salt = newSalt;
        BumpSecurityRevision();
    }

    public void SetMfa(bool enabled, string? secret)
    {
        MfaEnabled = enabled;
        MfaSecret = secret;
        BumpSecurityRevision();
    }

    public void SetActive(bool active)
    {
        IsActive = active;
        BumpSecurityRevision();
    }

    public void BumpSecurityRevision()
    {
        SecurityRevision++;
        RowVersion++;
    }

    public void RecordLogin(DateTimeOffset loginTimeUtc)
    {
        LastLoginAtUtc = loginTimeUtc;
    }
}
