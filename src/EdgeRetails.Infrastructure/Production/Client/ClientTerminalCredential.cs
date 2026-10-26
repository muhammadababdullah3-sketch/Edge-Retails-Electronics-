using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EdgeRetails.Infrastructure.Production.Client;

/// <summary>
/// Machine-local terminal pairing secret for the client shop service API.
/// Not a license signing key. Prefer DPAPI-protected store over plaintext environment variables.
/// </summary>
public static class ClientTerminalCredential
{
    public const string DefaultTerminalCode = "LOCAL";

    private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("EdgeRetails.Client.TerminalCredential.V1");
    private const int SecretByteLength = 32;
    private const int MaximumCredentialFileBytes = 16 * 1024;

    public static string InstallationDirectory => ResolveInstallationDirectory();

    private static string ResolveInstallationDirectory()
    {
        var stateRoot = Environment.GetEnvironmentVariable("EDGE_RETAILS_PRODUCTION_STATE_DIR");
        if (!string.IsNullOrWhiteSpace(stateRoot))
        {
            return Path.Combine(Path.GetFullPath(stateRoot.Trim()), "installation");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "EdgeRetails",
            "installation");
    }

    public static string ProtectedCredentialPath =>
        Path.Combine(InstallationDirectory, "terminal-credential.dpapi");

    public static string InstallationManifestPath =>
        Path.Combine(InstallationDirectory, "installation.json");

    /// <summary>
    /// Resolves the terminal auth secret: explicit env (rehearsal/legacy) then DPAPI store.
    /// </summary>
    public static string? TryResolveSecret()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("EDGE_RETAILS_TERMINAL_SECRET");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment.Trim();
        }

        return TryReadProtectedSecret();
    }

    public static string ResolveTerminalCode()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("EDGE_RETAILS_TERMINAL_CODE");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment.Trim().ToUpperInvariant();
        }

        if (!File.Exists(InstallationManifestPath))
        {
            return DefaultTerminalCode;
        }

        try
        {
            var json = File.ReadAllText(InstallationManifestPath);
            var manifest = JsonSerializer.Deserialize<InstallationManifest>(json);
            if (!string.IsNullOrWhiteSpace(manifest?.TerminalCode))
            {
                return manifest.TerminalCode.Trim().ToUpperInvariant();
            }
        }
        catch
        {
            // Fall back to default code when manifest is unreadable.
        }

        return DefaultTerminalCode;
    }

    /// <summary>
    /// Provisions a new terminal secret when none exists. Idempotent.
    /// </summary>
    public static bool TryEnsureProvisioned(out string? error)
    {
        error = null;
        if (!OperatingSystem.IsWindows())
        {
            error = "Client terminal credential provisioning requires Windows.";
            return false;
        }

        if (TryResolveSecret() is not null && File.Exists(ProtectedCredentialPath))
        {
            return true;
        }

        if (TryResolveSecret() is not null)
        {
            // Legacy env-only install without persisted store — do not overwrite.
            return true;
        }

        try
        {
            Directory.CreateDirectory(InstallationDirectory);
            var secretBytes = RandomNumberGenerator.GetBytes(SecretByteLength);
            var secret = Convert.ToBase64String(secretBytes);
            CryptographicOperations.ZeroMemory(secretBytes);

            var protectedBytes = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(secret),
                DpapiEntropy,
                DataProtectionScope.LocalMachine);

            var temp = ProtectedCredentialPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temp, protectedBytes);
            File.Move(temp, ProtectedCredentialPath, overwrite: true);

            if (!File.Exists(InstallationManifestPath))
            {
                var manifest = new InstallationManifest(DefaultTerminalCode, DateTimeOffset.UtcNow);
                File.WriteAllText(
                    InstallationManifestPath,
                    JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            }

            CryptographicOperations.ZeroMemory(protectedBytes);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string? TryReadProtectedSecret()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(ProtectedCredentialPath))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(ProtectedCredentialPath);
            if (info.Length <= 0 || info.Length > MaximumCredentialFileBytes)
            {
                return null;
            }

            var protectedBytes = File.ReadAllBytes(ProtectedCredentialPath);
            var plain = ProtectedData.Unprotect(protectedBytes, DpapiEntropy, DataProtectionScope.LocalMachine);
            var secret = Encoding.UTF8.GetString(plain);
            CryptographicOperations.ZeroMemory(plain);
            return string.IsNullOrWhiteSpace(secret) ? null : secret.Trim();
        }
        catch
        {
            return null;
        }
    }

    private sealed record InstallationManifest(string TerminalCode, DateTimeOffset ProvisionedAtUtc);
}
