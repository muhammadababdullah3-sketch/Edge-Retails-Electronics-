namespace EdgeRetails.Desktop.Services;

public interface ISessionContext
{
    Guid? UserId { get; }

    Guid? SessionId { get; }

    string DisplayName { get; }

    string RoleName { get; }

    string Initials { get; }

    bool IsOnline { get; }

    IReadOnlySet<string> PermissionKeys { get; }
}
