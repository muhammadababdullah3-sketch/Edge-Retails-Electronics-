namespace EdgeRetails.Central.Application.Common;

/// <summary>
/// Disposable local-development portal sign-in defaults. Not used in production hosts.
/// Override with CENTRAL_DEV_PORTAL_USERNAME / CENTRAL_DEV_PORTAL_PASSWORD in local/central-dev.env.
/// </summary>
public static class CentralLocalPortalCredentials
{
    public const string UsernameVariable = "CENTRAL_DEV_PORTAL_USERNAME";
    public const string PasswordVariable = "CENTRAL_DEV_PORTAL_PASSWORD";

    public const string DefaultUsername = "central-dev@edgeretails.local";
    public const string DefaultPassword = "CentralDevPortal!2026";

    public const string PortalRecordRelativePath = "local/central-dev.portal.json";

    public static string ResolveUsername()
    {
        var fromEnv = Environment.GetEnvironmentVariable(UsernameVariable)?.Trim();
        return string.IsNullOrEmpty(fromEnv) ? DefaultUsername : fromEnv.ToLowerInvariant();
    }

    public static string ResolvePassword()
    {
        var fromEnv = Environment.GetEnvironmentVariable(PasswordVariable);
        return string.IsNullOrEmpty(fromEnv) ? DefaultPassword : fromEnv;
    }
}
