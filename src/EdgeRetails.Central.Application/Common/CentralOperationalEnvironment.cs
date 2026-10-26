namespace EdgeRetails.Central.Application.Common;

/// <summary>
/// Fail-fast validation for variables required before the Central host can serve auth and database-backed API work.
/// Values are never invented here; missing configuration must be supplied explicitly.
/// </summary>
public static class CentralOperationalEnvironment
{
    public static readonly string[] MinimumHostVariables =
    [
        "CENTRAL_SIGNER_KEY_STORE_ROOT",
        "CENTRAL_DATABASE_CONNECTION_STRING",
        "CENTRAL_PG_PORT",
        "CENTRAL_PG_DATABASE",
        "CENTRAL_PG_ROLE",
        "CENTRAL_PG_MIGRATION_ROLE",
        "CENTRAL_PG_PID",
        "CENTRAL_PG_BINARY",
        "CENTRAL_PG_OWNERSHIP_MARKER",
        "CENTRAL_PG_DATA_DIR",
        "CENTRAL_ADMIN_AUTH_CHALLENGE_TTL_SECONDS",
        "CENTRAL_ADMIN_AUTH_SESSION_IDLE_TIMEOUT_SECONDS",
        "CENTRAL_ADMIN_AUTH_SESSION_ABSOLUTE_LIFETIME_SECONDS",
        "CENTRAL_ADMIN_STEP_UP_TTL_SECONDS",
        "CENTRAL_AUTH_RATE_LIMIT_MAX_ATTEMPTS",
        "CENTRAL_AUTH_RATE_LIMIT_WINDOW_SECONDS"
    ];

    public static IReadOnlyList<string> GetMissingMinimumVariables()
    {
        var missing = new List<string>();
        foreach (var name in MinimumHostVariables)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
            {
                missing.Add(name);
            }
        }

        return missing;
    }

    public const string IntegrationTestHostFlag = "CENTRAL_INTEGRATION_TEST_HOST";

    public static bool IsTestHostEnvironment() =>
        string.Equals(Environment.GetEnvironmentVariable(IntegrationTestHostFlag), "1", StringComparison.Ordinal);

    public static void EnsureMinimumHostConfiguration()
    {
        if (IsTestHostEnvironment())
        {
            return;
        }

        var missing = GetMissingMinimumVariables();
        if (missing.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            "Central host configuration is incomplete. Missing: " + string.Join(", ", missing) +
            ". Copy local/central-dev.env.example to local/central-dev.env (gitignored), fill attested values, " +
            "or dot-source local/central-dev.env.ps1 via scripts/central/Start-CentralLocalDev.ps1.");
    }
}
