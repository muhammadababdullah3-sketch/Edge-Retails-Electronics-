using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace EdgeRetails.ProtectedSigner.Keys;

public class InMemoryPrivateKeyStore : IPrivateKeyStore, IDisposable
{
    public const string RootAuthorityKeyId = "central-root-authority-v1";
    private readonly ConcurrentDictionary<string, RSA> _keys = new(StringComparer.Ordinal);

    public InMemoryPrivateKeyStore()
    {
        // Pre-initialize isolated test root authority key
        var rootRsa = RSA.Create(2048);
        _keys[RootAuthorityKeyId] = rootRsa;
    }

    public void StoreKey(string keyId, RSA rsa)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        ArgumentNullException.ThrowIfNull(rsa);

        _keys[keyId] = rsa;
    }

    public RSA? GetKey(string keyId)
    {
        return _keys.TryGetValue(keyId, out var rsa) ? rsa : null;
    }

    public bool HasKey(string keyId)
    {
        return _keys.ContainsKey(keyId);
    }

    public string? GetPublicKeyPem(string keyId)
    {
        return _keys.TryGetValue(keyId, out var rsa) ? rsa.ExportSubjectPublicKeyInfoPem() : null;
    }

    public void Dispose()
    {
        foreach (var rsa in _keys.Values)
        {
            rsa.Dispose();
        }
        _keys.Clear();
        GC.SuppressFinalize(this);
    }
}
