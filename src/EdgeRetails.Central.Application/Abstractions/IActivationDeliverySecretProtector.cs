namespace EdgeRetails.Central.Application.Abstractions;

public interface IActivationDeliverySecretProtector
{
    (string KeyId, string Ciphertext) Protect(string secret, string context);
    string Unprotect(string keyId, string ciphertext, string context);
}
