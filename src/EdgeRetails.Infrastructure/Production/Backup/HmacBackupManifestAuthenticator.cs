using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdgeRetails.Application.Production.Backup;

namespace EdgeRetails.Infrastructure.Production.Backup;

public sealed class HmacBackupManifestAuthenticator : IBackupManifestAuthenticator
{
    private static readonly byte[] DerivationLabel = Encoding.UTF8.GetBytes("EdgeRetails.BackupManifest.HMAC.V1");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IBackupEncryptionKeyProvider _keyProvider;

    public HmacBackupManifestAuthenticator(IBackupEncryptionKeyProvider keyProvider)
        => _keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));

    public async Task<string> ComputeAuthenticationAsync(BackupManifest manifest, CancellationToken cancellationToken = default)
    {
        var raw = await _keyProvider.GetKeyAsync(cancellationToken);
        if (raw is null || raw.Length != 32)
        {
            throw new InvalidOperationException("Backup encryption key provider must return exactly 32 bytes.");
        }

        var baseKey = raw.ToArray();
        byte[]? macKey = null;
        try
        {
            using (var derivation = new HMACSHA256(baseKey))
            {
                macKey = derivation.ComputeHash(DerivationLabel);
            }

            var payload = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
            using var hmac = new HMACSHA256(macKey);
            return Convert.ToHexString(hmac.ComputeHash(payload));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(baseKey);
            if (macKey is not null)
            {
                CryptographicOperations.ZeroMemory(macKey);
            }
        }
    }

    public async Task<bool> VerifyAuthenticationAsync(BackupManifest manifest, string authentication, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(authentication) || authentication.Length != 64 || !authentication.All(Uri.IsHexDigit))
        {
            return false;
        }

        var expected = await ComputeAuthenticationAsync(manifest, cancellationToken);
        try
        {
            var left = Convert.FromHexString(expected);
            var right = Convert.FromHexString(authentication);
            return CryptographicOperations.FixedTimeEquals(left, right);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
