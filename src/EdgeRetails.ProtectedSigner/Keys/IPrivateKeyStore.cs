using System.Security.Cryptography;

namespace EdgeRetails.ProtectedSigner.Keys;

public interface IPrivateKeyStore
{
    void StoreKey(string keyId, RSA rsa);
    RSA? GetKey(string keyId);
    bool HasKey(string keyId);
    string? GetPublicKeyPem(string keyId);
}
