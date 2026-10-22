using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Domain.Common;

namespace EdgeRetails.Central.Application.Common;

/// <summary>Explicitly configured local/dev secret envelope. No key or algorithm fallback is provided.</summary>
public sealed class ConfiguredActivationDeliverySecretProtector : IActivationDeliverySecretProtector
{
    private sealed record KeySet(string ActiveKeyId, Dictionary<string, string> Keys);

    public (string KeyId, string Ciphertext) Protect(string secret, string context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        var keySet = ReadKeySet();
        var key = ReadKey(keySet, keySet.ActiveKeyId);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintext = Encoding.UTF8.GetBytes(secret);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(context));
            return (keySet.ActiveKeyId, string.Join('.', Convert.ToBase64String(nonce), Convert.ToBase64String(tag), Convert.ToBase64String(ciphertext)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public string Unprotect(string keyId, string ciphertext, string context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ciphertext);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        var parts = ciphertext.Split('.');
        if (parts.Length != 3)
        {
            throw new CentralDomainException("ACTIVATION_DELIVERY_CIPHERTEXT_INVALID", "activation.delivery_ciphertext_invalid", "The protected activation delivery envelope is malformed.");
        }

        var key = ReadKey(ReadKeySet(), keyId);
        var nonce = Convert.FromBase64String(parts[0]);
        var tag = Convert.FromBase64String(parts[1]);
        var encrypted = Convert.FromBase64String(parts[2]);
        var plaintext = new byte[encrypted.Length];
        try
        {
            if (nonce.Length != 12 || tag.Length != 16)
            {
                throw new CryptographicException("Activation delivery envelope dimensions are invalid.");
            }
            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(nonce, encrypted, tag, plaintext, Encoding.UTF8.GetBytes(context));
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static KeySet ReadKeySet()
    {
        var json = Environment.GetEnvironmentVariable("CENTRAL_ACTIVATION_DELIVERY_KEYS_JSON");
        var activeId = Environment.GetEnvironmentVariable("CENTRAL_ACTIVATION_DELIVERY_ACTIVE_KEY_ID");
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(activeId))
        {
            throw new CentralDomainException("ACTIVATION_DELIVERY_KEY_NOT_CONFIGURED", "activation.delivery_key_not_configured", "Explicit activation-delivery encryption keys are required before issuing an activation token.");
        }
        try
        {
            var keys = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (keys is null || !keys.ContainsKey(activeId))
            {
                throw new JsonException("The active key ID is absent from the configured key set.");
            }
            return new KeySet(activeId, keys);
        }
        catch (JsonException ex)
        {
            throw new CentralDomainException("ACTIVATION_DELIVERY_KEY_CONFIGURATION_INVALID", "activation.delivery_key_configuration_invalid", $"Activation-delivery key configuration is invalid: {ex.Message}");
        }
    }

    private static byte[] ReadKey(KeySet keySet, string keyId)
    {
        if (!keySet.Keys.TryGetValue(keyId, out var value))
        {
            throw new CentralDomainException("ACTIVATION_DELIVERY_KEY_UNAVAILABLE", "activation.delivery_key_unavailable", $"Activation-delivery key '{keyId}' is not configured.");
        }
        try
        {
            var key = Convert.FromBase64String(value);
            if (key.Length != 32)
            {
                CryptographicOperations.ZeroMemory(key);
                throw new FormatException("AES-256-GCM requires a 32-byte key.");
            }
            return key;
        }
        catch (FormatException ex)
        {
            throw new CentralDomainException("ACTIVATION_DELIVERY_KEY_CONFIGURATION_INVALID", "activation.delivery_key_configuration_invalid", $"Configured activation-delivery key is invalid: {ex.Message}");
        }
    }
}
