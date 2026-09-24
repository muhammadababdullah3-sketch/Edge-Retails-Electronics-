using System.Security.Cryptography;
using System.Text;

namespace EdgeRetails.Infrastructure.Production.Backup;

public static class PostgresRecoveryDatabaseName
{
    public static string Create(string purpose, string targetDatabase, Guid restoreId)
    {
        if (string.IsNullOrWhiteSpace(purpose) || purpose.Length > 8 || purpose.Any(ch => !char.IsAsciiLetterOrDigit(ch)))
        {
            throw new ArgumentException("Recovery database name purpose must be 1-8 ASCII letters/digits.", nameof(purpose));
        }

        if (string.IsNullOrWhiteSpace(targetDatabase))
        {
            throw new ArgumentException("Target database name is required.", nameof(targetDatabase));
        }

        if (restoreId == Guid.Empty)
        {
            throw new ArgumentException("Restore ID is required.", nameof(restoreId));
        }

        var targetHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(targetDatabase))).ToLowerInvariant()[..8];
        var id = restoreId.ToString("N")[..16];
        var name = $"er_{purpose.ToLowerInvariant()}_{targetHash}_{id}";
        if (Encoding.UTF8.GetByteCount(name) > 63)
        {
            throw new InvalidOperationException("Generated PostgreSQL recovery database name exceeds the safe identifier limit.");
        }

        if (string.Equals(name, targetDatabase, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Generated recovery database name collides with production database name.");
        }

        return name;
    }
}
