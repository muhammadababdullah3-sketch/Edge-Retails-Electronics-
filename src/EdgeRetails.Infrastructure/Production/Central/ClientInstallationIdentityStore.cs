using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EdgeRetails.Infrastructure.Production.Central;

[SupportedOSPlatform("windows")]
internal sealed class ClientInstallationIdentityStore
{
    private static readonly byte[] KeyEntropy = Encoding.UTF8.GetBytes("EdgeRetails.Client.InstallationKey.V1");

    private readonly string _directory;

    public ClientInstallationIdentityStore(string installationDirectory) =>
        _directory = installationDirectory;

    public ClientInstallationIdentity LoadOrCreate()
    {
        Directory.CreateDirectory(_directory);
        var statePath = Path.Combine(_directory, "activation-identity.json");
        var keyPath = Path.Combine(_directory, "installation-private-key.dpapi");

        if (File.Exists(statePath) && File.Exists(keyPath))
        {
            var state = JsonSerializer.Deserialize<IdentityState>(File.ReadAllText(statePath))
                ?? throw new InvalidOperationException("Installation identity state is corrupt.");
            var keyBytes = ProtectedData.Unprotect(File.ReadAllBytes(keyPath), KeyEntropy, DataProtectionScope.LocalMachine);
            var loadedKey = RSA.Create();
            loadedKey.ImportPkcs8PrivateKey(keyBytes, out _);
            CryptographicOperations.ZeroMemory(keyBytes);
            var loaded = new ClientInstallationIdentity(
                state.InstallationId,
                state.ShopId,
                state.LicenseId,
                state.PublicKeyPem,
                state.PublicKeyFingerprint,
                loadedKey);
            loaded.Bind(state.ShopId, state.LicenseId);
            return loaded;
        }

        var installationId = Guid.CreateVersion7();
        var rsa = RSA.Create(2048);
        var publicPem = rsa.ExportSubjectPublicKeyInfoPem();
        var fingerprint = ComputeFingerprint(publicPem);
        var privateBytes = rsa.ExportPkcs8PrivateKey();
        var protectedKey = ProtectedData.Protect(privateBytes, KeyEntropy, DataProtectionScope.LocalMachine);
        CryptographicOperations.ZeroMemory(privateBytes);

        var temp = keyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(temp, protectedKey);
        File.Move(temp, keyPath, overwrite: true);

        var identity = new IdentityState(
            installationId,
            Guid.Empty,
            Guid.Empty,
            publicPem,
            fingerprint);
        File.WriteAllText(statePath, JsonSerializer.Serialize(identity, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

        return new ClientInstallationIdentity(installationId, Guid.Empty, Guid.Empty, publicPem, fingerprint, rsa);
    }

    public void SaveBinding(Guid shopId, Guid licenseId, ClientInstallationIdentity identity)
    {
        var statePath = Path.Combine(_directory, "activation-identity.json");
        var state = new IdentityState(identity.InstallationId, shopId, licenseId, identity.PublicKeyPem, identity.PublicKeyFingerprint);
        File.WriteAllText(statePath, JsonSerializer.Serialize(state, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }

    public static string ComputeFingerprint(string publicKeyPem) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(publicKeyPem.Trim()))).ToLowerInvariant();

    private sealed record IdentityState(
        Guid InstallationId,
        Guid ShopId,
        Guid LicenseId,
        string PublicKeyPem,
        string PublicKeyFingerprint);
}

internal sealed class ClientInstallationIdentity(
    Guid installationId,
    Guid shopId,
    Guid licenseId,
    string publicKeyPem,
    string publicKeyFingerprint,
    RSA privateKey) : IDisposable
{
    public Guid InstallationId { get; } = installationId;
    public Guid ShopId { get; private set; } = shopId;
    public Guid LicenseId { get; private set; } = licenseId;
    public string PublicKeyPem { get; } = publicKeyPem;
    public string PublicKeyFingerprint { get; } = publicKeyFingerprint;
    public RSA PrivateKey { get; } = privateKey;

    public void Bind(Guid shopId, Guid licenseId)
    {
        ShopId = shopId;
        LicenseId = licenseId;
    }

    public void Dispose() => PrivateKey.Dispose();
}
