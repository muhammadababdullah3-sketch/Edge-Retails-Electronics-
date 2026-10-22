using System.Runtime.CompilerServices;

namespace EdgeRetails.Central.IntegrationTests;

internal static class CentralIntegrationTestEnvironment
{
    [ModuleInitializer]
    internal static void ConfigureSyntheticLocalAdapters()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-artifacts", "central-local-adapters");
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("CENTRAL_SIGNER_KEY_STORE_ROOT", Path.Combine(root, "signer-keys"));
        Environment.SetEnvironmentVariable("CENTRAL_OBJECT_STORAGE_ROOT", Path.Combine(root, "objects"));
        Environment.SetEnvironmentVariable("CENTRAL_PAYMENT_STORAGE_ROOT", Path.Combine(root, "payment-files"));
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
        Environment.SetEnvironmentVariable("CENTRAL_DUAL_APPROVAL_ACTIONS", "NONE");
        Environment.SetEnvironmentVariable("CENTRAL_INTERNAL_SIGNER_TOKEN", "synthetic-central-integration-secret-20261022");
    }
}
