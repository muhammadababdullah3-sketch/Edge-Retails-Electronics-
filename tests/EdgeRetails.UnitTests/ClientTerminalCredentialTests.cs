using EdgeRetails.Infrastructure.Production.Client;

namespace EdgeRetails.UnitTests;

public sealed class ClientTerminalCredentialTests
{
    [Fact]
    public void ResolveTerminalCode_DefaultsWhenManifestMissing()
    {
        var code = ClientTerminalCredential.ResolveTerminalCode();
        Assert.Equal(ClientTerminalCredential.DefaultTerminalCode, code);
    }

    [Fact]
    public void TryEnsureProvisioned_CreatesReadableSecret()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "er-client-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousStateRoot = Environment.GetEnvironmentVariable("EDGE_RETAILS_PRODUCTION_STATE_DIR");
        Environment.SetEnvironmentVariable("EDGE_RETAILS_PRODUCTION_STATE_DIR", tempRoot);

        var credential = ClientTerminalCredential.ProtectedCredentialPath;
        string? previousSecret = Environment.GetEnvironmentVariable("EDGE_RETAILS_TERMINAL_SECRET");

        try
        {
            Environment.SetEnvironmentVariable("EDGE_RETAILS_TERMINAL_SECRET", null);

            var ok = ClientTerminalCredential.TryEnsureProvisioned(out var error);
            Assert.True(ok, error);
            var secret = ClientTerminalCredential.TryResolveSecret();
            Assert.False(string.IsNullOrWhiteSpace(secret));
            Assert.True(File.Exists(credential));
        }
        finally
        {
            Environment.SetEnvironmentVariable("EDGE_RETAILS_TERMINAL_SECRET", previousSecret);
            Environment.SetEnvironmentVariable("EDGE_RETAILS_PRODUCTION_STATE_DIR", previousStateRoot);
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
                // Best-effort test cleanup.
            }
        }
    }
}
