namespace EdgeRetails.Desktop.Services;

public interface ISessionContext
{
    string DisplayName { get; }

    string RoleName { get; }

    string Initials { get; }

    bool IsOnline { get; }
}
