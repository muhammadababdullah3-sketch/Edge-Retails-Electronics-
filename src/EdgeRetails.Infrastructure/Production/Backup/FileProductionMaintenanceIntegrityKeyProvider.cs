using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using EdgeRetails.Application.Production;

namespace EdgeRetails.Infrastructure.Production.Backup;

/// <summary>
/// Machine-local integrity key for the cooperative maintenance barrier. The key is generated once,
/// stored separately from the authenticated state file and returned as a fresh buffer on each read.
/// Deployment may replace this provider with a stronger OS secret-store adapter without changing
/// Application or restore semantics.
/// </summary>
public sealed class FileProductionMaintenanceIntegrityKeyProvider : IProductionMaintenanceIntegrityKeyProvider
{
    private const int KeySize = 32;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileProductionMaintenanceIntegrityKeyProvider(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Maintenance integrity-key path is required.", nameof(path));
        }

        _path = Path.GetFullPath(path);
    }

    public async Task<byte[]> GetIntegrityKeyAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
                HardenWindowsDirectoryAcl(directory);
            }

            if (File.Exists(_path))
            {
                HardenWindowsAcl(_path);
                var existing = await File.ReadAllBytesAsync(_path, cancellationToken);
                if (existing.Length != KeySize)
                {
                    CryptographicOperations.ZeroMemory(existing);
                    throw new InvalidDataException("Production-maintenance integrity key has an invalid length.");
                }

                return existing;
            }

            var generated = RandomNumberGenerator.GetBytes(KeySize);
            var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(
                    temp,
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

                HardenWindowsAcl(temp);

                try
                {
                    File.Move(temp, _path, overwrite: false);
                    return generated.ToArray();
                }
                catch (IOException) when (File.Exists(_path))
                {
                    HardenWindowsAcl(_path);
                    var winner = await File.ReadAllBytesAsync(_path, cancellationToken);
                    if (winner.Length != KeySize)
                    {
                        CryptographicOperations.ZeroMemory(winner);
                        throw new InvalidDataException("Production-maintenance integrity key has an invalid length.");
                    }

                    return winner;
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(temp))
                    {
                        File.Delete(temp);
                    }
                }
                catch
                {
                }

                CryptographicOperations.ZeroMemory(generated);
            }
        }
        finally
        {
            _gate.Release();
        }
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

        const InheritanceFlags inheritance =
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        const PropagationFlags propagation = PropagationFlags.None;
        var fullControl = FileSystemRights.FullControl;

        foreach (var sid in new[]
        {
            userSid,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
        })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                fullControl,
                inheritance,
                propagation,
                AccessControlType.Allow));
        }

        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static void HardenWindowsAcl(string path)
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

        var fullControl = FileSystemRights.FullControl;
        security.AddAccessRule(new FileSystemAccessRule(
            userSid,
            fullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            fullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            fullControl,
            AccessControlType.Allow));

        new FileInfo(path).SetAccessControl(security);
    }
}
