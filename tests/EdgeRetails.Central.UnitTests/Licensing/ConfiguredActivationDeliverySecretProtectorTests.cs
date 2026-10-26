using System.Security.Cryptography;
using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Domain.Common;

namespace EdgeRetails.Central.UnitTests.Licensing;

public sealed class ConfiguredActivationDeliverySecretProtectorTests
{
    [Fact]
    public void ExplicitAesGcmKey_ProtectsAndRecoversSecretOnlyWithExactContext()
    {
        const string activeId = "dev-key-v1";
        var key = RandomNumberGenerator.GetBytes(32);
        var oldKeys = Environment.GetEnvironmentVariable("CENTRAL_ACTIVATION_DELIVERY_KEYS_JSON");
        var oldActiveId = Environment.GetEnvironmentVariable("CENTRAL_ACTIVATION_DELIVERY_ACTIVE_KEY_ID");
        Environment.SetEnvironmentVariable("CENTRAL_ACTIVATION_DELIVERY_KEYS_JSON",
            System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { [activeId] = Convert.ToBase64String(key) }));
        Environment.SetEnvironmentVariable("CENTRAL_ACTIVATION_DELIVERY_ACTIVE_KEY_ID", activeId);
        try
        {
            var protector = new ConfiguredActivationDeliverySecretProtector();
            var secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            const string context = "activation:synthetic-token:shop:installation:channel";
            var (keyId, ciphertext) = protector.Protect(secret, context);

            Assert.Equal(activeId, keyId);
            Assert.DoesNotContain(secret, ciphertext, StringComparison.Ordinal);
            Assert.Equal(secret, protector.Unprotect(keyId, ciphertext, context));
            Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(keyId, ciphertext, context + ":substituted"));

            var parts = ciphertext.Split('.');
            var encrypted = Convert.FromBase64String(parts[2]);
            encrypted[0] ^= 1;
            var tampered = string.Join('.', parts[0], parts[1], Convert.ToBase64String(encrypted));
            Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(keyId, tampered, context));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            Environment.SetEnvironmentVariable("CENTRAL_ACTIVATION_DELIVERY_KEYS_JSON", oldKeys);
            Environment.SetEnvironmentVariable("CENTRAL_ACTIVATION_DELIVERY_ACTIVE_KEY_ID", oldActiveId);
        }
    }

    [Fact]
    public void MissingKeyConfiguration_FailsClosed()
    {
        var oldKeys = Environment.GetEnvironmentVariable("CENTRAL_ACTIVATION_DELIVERY_KEYS_JSON");
        var oldActiveId = Environment.GetEnvironmentVariable("CENTRAL_ACTIVATION_DELIVERY_ACTIVE_KEY_ID");
        Environment.SetEnvironmentVariable("CENTRAL_ACTIVATION_DELIVERY_KEYS_JSON", null);
        Environment.SetEnvironmentVariable("CENTRAL_ACTIVATION_DELIVERY_ACTIVE_KEY_ID", null);
        try
        {
            var error = Assert.Throws<CentralDomainException>(() => new ConfiguredActivationDeliverySecretProtector().Protect("synthetic-token", "synthetic-context"));
            Assert.Equal("ACTIVATION_DELIVERY_KEY_NOT_CONFIGURED", error.Code);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CENTRAL_ACTIVATION_DELIVERY_KEYS_JSON", oldKeys);
            Environment.SetEnvironmentVariable("CENTRAL_ACTIVATION_DELIVERY_ACTIVE_KEY_ID", oldActiveId);
        }
    }

    [Fact]
    public void RetainedDevelopmentKey_CanDecryptPriorDeliveryAfterActiveKeyRotation()
    {
        var oldKeys = Environment.GetEnvironmentVariable("CENTRAL_ACTIVATION_DELIVERY_KEYS_JSON");
        var oldActiveId = Environment.GetEnvironmentVariable("CENTRAL_ACTIVATION_DELIVERY_ACTIVE_KEY_ID");
        var firstKey = RandomNumberGenerator.GetBytes(32);
        var secondKey = RandomNumberGenerator.GetBytes(32);
        const string firstId = "dev-key-before-rotation";
        const string secondId = "dev-key-after-rotation";
        const string secret = "synthetic-activation-secret";
        const string context = "activation:token:shop:installation:channel";
        try
        {
            Environment.SetEnvironmentVariable("CENTRAL_ACTIVATION_DELIVERY_KEYS_JSON",
                System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { [firstId] = Convert.ToBase64String(firstKey) }));
            Environment.SetEnvironmentVariable("CENTRAL_ACTIVATION_DELIVERY_ACTIVE_KEY_ID", firstId);
            var protector = new ConfiguredActivationDeliverySecretProtector();
            var (oldKeyId, oldCiphertext) = protector.Protect(secret, context);

            Environment.SetEnvironmentVariable("CENTRAL_ACTIVATION_DELIVERY_KEYS_JSON",
                System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    [firstId] = Convert.ToBase64String(firstKey),
                    [secondId] = Convert.ToBase64String(secondKey)
                }));
            Environment.SetEnvironmentVariable("CENTRAL_ACTIVATION_DELIVERY_ACTIVE_KEY_ID", secondId);
            var (newKeyId, newCiphertext) = protector.Protect(secret, context);

            Assert.Equal(firstId, oldKeyId);
            Assert.Equal(secondId, newKeyId);
            Assert.Equal(secret, protector.Unprotect(oldKeyId, oldCiphertext, context));
            Assert.Equal(secret, protector.Unprotect(newKeyId, newCiphertext, context));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(firstKey);
            CryptographicOperations.ZeroMemory(secondKey);
            Environment.SetEnvironmentVariable("CENTRAL_ACTIVATION_DELIVERY_KEYS_JSON", oldKeys);
            Environment.SetEnvironmentVariable("CENTRAL_ACTIVATION_DELIVERY_ACTIVE_KEY_ID", oldActiveId);
        }
    }
}
