using EdgeRetails.Domain.Common;

namespace EdgeRetails.Domain.Identity;

public enum UserStatus
{
    Active = 1,
    Disabled = 2
}

public sealed class User : Entity
{
    public string DisplayName { get; set; } = string.Empty;
    public Guid RoleId { get; set; }
    public byte[] PinHash { get; set; } = Array.Empty<byte>();
    public byte[] PinSalt { get; set; } = Array.Empty<byte>();
    public int PinIterations { get; set; }
    public string PinAlgorithm { get; set; } = "PBKDF2-SHA256";
    public UserStatus Status { get; set; } = UserStatus.Active;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; }
}

public sealed class Role : Entity
{
    public string Name { get; set; } = string.Empty; public bool IsSystem { get; set; }
    public bool IsActive { get; set; } = true;
    public long Version { get; set; }
}

public sealed class Permission : Entity
{
    public string Key { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class RolePermission : Entity
{
    public Guid RoleId { get; set; }
    public Guid PermissionId { get; set; }
}

public sealed class UserPermissionOverride : Entity
{
    public Guid UserId { get; set; }
    public Guid PermissionId { get; set; }
    public bool IsAllowed { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; }
}
public sealed class UserSession : Entity
{
    public Guid UserId { get; set; }
    public Guid ClientSessionId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public bool IsRevoked { get; set; }
}
