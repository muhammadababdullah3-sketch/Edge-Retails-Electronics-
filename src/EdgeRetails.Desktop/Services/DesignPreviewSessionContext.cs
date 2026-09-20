namespace EdgeRetails.Desktop.Services;

/// <summary>
/// Sprint 1 design-preview session only. Authentication replaces this in Sprint 2.
/// </summary>
public sealed class DesignPreviewSessionContext : ISessionContext
{
    public string DisplayName => "Abdullah";

    public string RoleName => "Owner";

    public string Initials => "A";

    public bool IsOnline => true;
}
