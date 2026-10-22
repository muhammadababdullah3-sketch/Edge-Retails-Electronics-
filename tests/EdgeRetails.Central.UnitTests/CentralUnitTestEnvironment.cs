using System.Runtime.CompilerServices;

namespace EdgeRetails.Central.UnitTests;

internal static class CentralUnitTestEnvironment
{
    [ModuleInitializer]
    internal static void ConfigureSyntheticPaymentStorage()
    {
        Environment.SetEnvironmentVariable("CENTRAL_PAYMENT_STORAGE_ROOT",
            Path.Combine(AppContext.BaseDirectory, "test-artifacts", "synthetic-unit-payment-store"));
        Environment.SetEnvironmentVariable("CENTRAL_UPLOAD_MAX_OBJECT_BYTES", "104857600");
        Environment.SetEnvironmentVariable("CENTRAL_UPLOAD_MAX_CHUNK_BYTES", "10485760");
        Environment.SetEnvironmentVariable("CENTRAL_INSTALLATION_PROOF_WINDOW_SECONDS", "120");
    }
}
