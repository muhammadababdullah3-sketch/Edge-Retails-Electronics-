using System.Runtime.CompilerServices;
using EdgeRetails.Central.Application.Common;

namespace EdgeRetails.Central.IntegrationTests;

internal static class CentralIntegrationTestEnvironment
{
    [ModuleInitializer]
    internal static void ConfigureSyntheticLocalAdapters()
    {
        Environment.SetEnvironmentVariable(CentralOperationalEnvironment.IntegrationTestHostFlag, "1");
        var root = Environment.GetEnvironmentVariable("CENTRAL_LOCAL_ADAPTERS_ROOT");
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(AppContext.BaseDirectory, "test-artifacts", "central-local-adapters");
        }

        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        var signerRoot = Path.Combine(root, "signer-keys");
        var objectRoot = Path.Combine(root, "objects");
        var paymentRoot = Path.Combine(root, "payment-files");
        Directory.CreateDirectory(signerRoot);
        Directory.CreateDirectory(objectRoot);
        Directory.CreateDirectory(paymentRoot);
        Environment.SetEnvironmentVariable("CENTRAL_SIGNER_KEY_STORE_ROOT", signerRoot);
        Environment.SetEnvironmentVariable("CENTRAL_OBJECT_STORAGE_ROOT", objectRoot);
        Environment.SetEnvironmentVariable("CENTRAL_PAYMENT_STORAGE_ROOT", paymentRoot);
        Environment.SetEnvironmentVariable("CENTRAL_STORAGE_RETENTION_POLICY_ID", "d0d8959b-278f-4cda-a81c-3eb88732f071");
        Environment.SetEnvironmentVariable("CENTRAL_STORAGE_RETENTION_DAYS", "30");
        Environment.SetEnvironmentVariable("CENTRAL_STORAGE_LEGAL_HOLD_ENABLED", "false");
        Environment.SetEnvironmentVariable("CENTRAL_UPLOAD_SESSION_TTL_SECONDS", "600");
        Environment.SetEnvironmentVariable("CENTRAL_BACKUP_DOWNLOAD_GRANT_TTL_SECONDS", "120"); // labelled synthetic integration fixture
        // Labelled isolated compatibility fixtures; operational upload limits have no fallback.
        Environment.SetEnvironmentVariable("CENTRAL_UPLOAD_MAX_OBJECT_BYTES", "104857600");
        Environment.SetEnvironmentVariable("CENTRAL_UPLOAD_MAX_CHUNK_BYTES", "10485760");
        Environment.SetEnvironmentVariable("CENTRAL_BOOTSTRAP_CHALLENGE_TTL_SECONDS", "300");
        Environment.SetEnvironmentVariable("CENTRAL_INSTALLATION_PROOF_WINDOW_SECONDS", "120");
        Environment.SetEnvironmentVariable("CENTRAL_SENSITIVE_APPROVAL_TTL_SECONDS", "3600"); // labelled synthetic integration fixture
        Environment.SetEnvironmentVariable("CENTRAL_DUAL_APPROVAL_ACTIONS", "NONE");
        // Existing implementation values are labelled compatibility-test fixtures, never runtime defaults.
        SetSyntheticFixtureIfAbsent("CENTRAL_AUTH_RATE_LIMIT_MAX_ATTEMPTS", "5");
        SetSyntheticFixtureIfAbsent("CENTRAL_AUTH_RATE_LIMIT_WINDOW_SECONDS", "60");
        // Explicit synthetic compatibility fixtures; no application defaults are implied.
        Environment.SetEnvironmentVariable("CENTRAL_ADMIN_AUTH_CHALLENGE_TTL_SECONDS", "300");
        Environment.SetEnvironmentVariable("CENTRAL_ADMIN_AUTH_SESSION_IDLE_TIMEOUT_SECONDS", "900");
        Environment.SetEnvironmentVariable("CENTRAL_ADMIN_AUTH_SESSION_ABSOLUTE_LIFETIME_SECONDS", "28800");
        Environment.SetEnvironmentVariable("CENTRAL_ADMIN_STEP_UP_TTL_SECONDS", "300");
        Environment.SetEnvironmentVariable("CENTRAL_INTERNAL_SIGNER_TOKEN", "synthetic-central-integration-secret-20261022");
    }

    private static void SetSyntheticFixtureIfAbsent(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }
}
