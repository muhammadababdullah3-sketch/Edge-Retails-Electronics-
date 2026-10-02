using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using EdgeRetails.Application.Production.Backup;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace EdgeRetails.Infrastructure.Production.Backup;

/// <summary>Reads the portable recovery key from the Server process environment.</summary>
public sealed class EnvironmentBackupEncryptionKeyProvider : IBackupEncryptionKeyProvider
{
    private readonly Func<string?> _readKey;

    public EnvironmentBackupEncryptionKeyProvider(Func<string?>? readKey = null)
        => _readKey = readKey ?? (() => Environment.GetEnvironmentVariable("EDGE_RETAILS_BACKUP_KEY"));

    public Task<byte[]> GetKeyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configured = _readKey();
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException("Backup protection is not configured. Set EDGE_RETAILS_BACKUP_KEY in the Server environment.");
        }

        try
        {
            var key = Convert.FromHexString(configured.Trim());
            if (key.Length != 32)
            {
                CryptographicOperations.ZeroMemory(key);
                throw new InvalidOperationException("Backup protection key must be exactly 64 hexadecimal characters.");
            }

            return Task.FromResult(key);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Backup protection key must be exactly 64 hexadecimal characters.", ex);
        }
    }
}

/// <summary>
/// Separate local integrity key for the restore journal. It is stored beside protected production
/// state with a restrictive Windows ACL and is independent of both backup and maintenance keys.
/// </summary>
public sealed class FileRestoreJournalIntegrityKeyProvider : IRestoreJournalIntegrityKeyProvider
{
    private const int KeySize = 32;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileRestoreJournalIntegrityKeyProvider(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public async Task<byte[]> GetIntegrityKeyAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(_path)
                ?? throw new InvalidOperationException("Restore journal key path has no parent directory.");
            Directory.CreateDirectory(directory);
            HardenWindowsDirectoryAcl(directory);

            if (File.Exists(_path))
            {
                HardenWindowsFileAcl(_path);
                return await ReadKeyAsync(_path, cancellationToken);
            }

            var generated = RandomNumberGenerator.GetBytes(KeySize);
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(generated, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    stream.Flush(flushToDisk: true);
                }

                HardenWindowsFileAcl(temporary);
                try
                {
                    File.Move(temporary, _path, overwrite: false);
                    return generated.ToArray();
                }
                catch (IOException) when (File.Exists(_path))
                {
                    HardenWindowsFileAcl(_path);
                    return await ReadKeyAsync(_path, cancellationToken);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(generated);
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<byte[]> ReadKeyAsync(string path, CancellationToken cancellationToken)
    {
        var key = await File.ReadAllBytesAsync(path, cancellationToken);
        if (key.Length != KeySize)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new InvalidDataException("Restore-journal integrity key has an invalid length.");
        }

        return key;
    }

    private static void HardenWindowsDirectoryAcl(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var identity = WindowsIdentity.GetCurrent();
        var userSid = identity.User
            ?? throw new InvalidOperationException("Current Windows identity does not expose a user SID.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(userSid);
        const InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[]
        {
            userSid,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
        })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        }

        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static void HardenWindowsFileAcl(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var identity = WindowsIdentity.GetCurrent();
        var userSid = identity.User
            ?? throw new InvalidOperationException("Current Windows identity does not expose a user SID.");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(userSid);
        foreach (var sid in new[]
        {
            userSid,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
        })
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        }

        new FileInfo(path).SetAccessControl(security);
    }
}

/// <summary>
/// Restore-only PostgreSQL administrator credentials. The deployment trust boundary supplies all
/// connection values; no credential is accepted from a Desktop request or normal DB connection.
/// </summary>
public sealed class EnvironmentPostgresMaintenanceConnectionProvider : IPostgresMaintenanceConnectionProvider
{
    private readonly Func<string, string?> _readValue;

    public EnvironmentPostgresMaintenanceConnectionProvider(
        IConfiguration configuration,
        Func<string, string?>? readValue = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _readValue = readValue ?? (key => Environment.GetEnvironmentVariable(key) ?? configuration[key]);
    }

    public Task<PostgresMaintenanceDescriptor> GetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var host = Required("EDGE_RETAILS_PG_MAINTENANCE_HOST");
        var user = Required("EDGE_RETAILS_PG_MAINTENANCE_USERNAME");
        var password = Required("EDGE_RETAILS_PG_MAINTENANCE_PASSWORD");
        var portText = Required("EDGE_RETAILS_PG_MAINTENANCE_PORT");
        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
        {
            throw new InvalidOperationException("EDGE_RETAILS_PG_MAINTENANCE_PORT must be a valid TCP port.");
        }

        var database = _readValue("EDGE_RETAILS_PG_MAINTENANCE_DATABASE");
        if (string.IsNullOrWhiteSpace(database))
        {
            database = "postgres";
        }

        return Task.FromResult(new PostgresMaintenanceDescriptor(host, port, user, new SensitiveString(password), database));
    }

    private string Required(string key)
    {
        var value = _readValue(key);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Restore administrator configuration is missing {key}.");
        }

        return value.Trim();
    }
}

public static class ProductionBackupConfiguration
{
    public static string ResolveBackupDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("EDGE_RETAILS_BACKUP_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured.Trim());
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("Local application-data directory is unavailable for backups.");
        }

        return Path.Combine(localAppData, "EdgeRetails", "Production", "backups");
    }

    public static string ResolvePostgresTool(string executableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);
        var configuredBin = Environment.GetEnvironmentVariable("EDGE_RETAILS_PG_BIN");
        var bin = string.IsNullOrWhiteSpace(configuredBin)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL", "18", "bin")
            : Path.GetFullPath(configuredBin.Trim());
        var path = Path.Combine(bin, executableName + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"PostgreSQL 18 client tool '{executableName}' was not found in the configured tool directory.", path);
        }

        return path;
    }

    public static PostgresConnectionDescriptor FromConnectionString(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var parsed = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(parsed.Host) || string.IsNullOrWhiteSpace(parsed.Database) ||
            string.IsNullOrWhiteSpace(parsed.Username))
        {
            throw new InvalidOperationException("Backup requires a configured PostgreSQL host, database, and username.");
        }

        return new PostgresConnectionDescriptor(
            parsed.Host,
            parsed.Port,
            parsed.Database,
            parsed.Username,
            new SensitiveString(parsed.Password ?? string.Empty),
            "postgres");
    }
}
