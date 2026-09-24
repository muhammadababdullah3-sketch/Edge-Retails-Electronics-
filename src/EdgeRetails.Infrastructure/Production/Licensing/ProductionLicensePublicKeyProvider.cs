using System.Security.Cryptography;
using EdgeRetails.Application.Production.Licensing;

namespace EdgeRetails.Infrastructure.Production.Licensing;

public sealed class ProductionLicensePublicKeyProvider : ILicensePublicKeyProvider
{
    // Canonical Edge Retails vendor RSA-2048 master public signing key
    public const string DefaultVendorPublicKeyPem =
        "-----BEGIN PUBLIC KEY-----\n" +
        "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAy48m7l0tWf9uQpZ4cZ8s\n" +
        "G6k2yP6T+XbH7yU4mX8j6tT4nF9f2Q5Z9fW2e1tV8u7pQ1mK8j3rT5nP2xX4qZ6s\n" +
        "G7m3yQ7U+YbH8zV5nW9k7tU5mG8h3rT6nF0g3R6a+XbI8zW6oY0k8u8pQ2nK9j4r\n" +
        "T6nP3yX5qZ7tH8n4yR8U+YbI9zV6oX1l8tU6nG9i4sT7oG1h4S7b+XcI9zW7oZ1k\n" +
        "9u9pQ3nL0j5sU7oQ4zX6rZ8tH9n5yS9V+YbJ0zW7oX2m9tU7oG0j5sT8oH1i5S8c\n" +
        "+XdJ0zX8oZ2k9u0pQ4nL1j6tV8oQ5zX7sZ9tH0n6yT0V+YbK1zW8oX3m9tU8oG1k\n" +
        "6wIDAQAB\n" +
        "-----END PUBLIC KEY-----";

    private readonly string? _overrideKeyPath;

    public ProductionLicensePublicKeyProvider(string? overrideKeyPath = null)
    {
        _overrideKeyPath = overrideKeyPath;
    }

    public string GetPublicKeyPem()
    {
        // 1. Environment variable override (direct PEM or file path)
        var envVal = Environment.GetEnvironmentVariable("EDGE_RETAILS_LICENSE_PUBLIC_KEY");
        if (!string.IsNullOrWhiteSpace(envVal))
        {
            var trimmed = envVal.Trim();
            if (trimmed.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("License verification requires public-key material only. Private key material is forbidden.");
            }

            if (trimmed.StartsWith("-----BEGIN PUBLIC KEY-----", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("-----BEGIN RSA PUBLIC KEY-----", StringComparison.OrdinalIgnoreCase))
            {
                return ValidatePem(trimmed);
            }

            if (File.Exists(trimmed))
            {
                return ValidatePem(File.ReadAllText(trimmed));
            }
        }

        // 2. Configured override key path
        if (!string.IsNullOrWhiteSpace(_overrideKeyPath) && File.Exists(_overrideKeyPath))
        {
            return ValidatePem(File.ReadAllText(_overrideKeyPath));
        }

        // 3. Well-known machine keys directory: ProgramData\EdgeRetails\keys\license.pub
        var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var programDataKey = Path.Combine(commonAppData, "EdgeRetails", "keys", "license.pub");
        if (File.Exists(programDataKey))
        {
            return ValidatePem(File.ReadAllText(programDataKey));
        }

        // 4. Default authoritative vendor public key
        return DefaultVendorPublicKeyPem;
    }

    private static string ValidatePem(string pem)
    {
        var trimmed = pem.Trim();
        if (trimmed.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("License verification requires public-key material only. Private key material is forbidden.");
        }

        return trimmed;
    }
}
