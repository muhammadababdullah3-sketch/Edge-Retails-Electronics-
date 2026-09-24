using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using EdgeRetails.Application.Production;

namespace EdgeRetails.Infrastructure.Production.Backup;

/// <summary>
/// Cooperative cross-process maintenance barrier for Desktop/Worker. The durable state is
/// HMAC-authenticated so a valid-looking Normal JSON file cannot bypass RecoveryRequired.
/// </summary>
public sealed class FileProductionMaintenanceBarrier : IProductionMaintenanceBarrier
{
    private const long MaximumStateBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _lockPath;
    private readonly string _statePath;
    private readonly IProductionMaintenanceIntegrityKeyProvider _integrityKeyProvider;

    public FileProductionMaintenanceBarrier(string directory, IProductionMaintenanceIntegrityKeyProvider integrityKeyProvider)
    {
        var root = Path.GetFullPath(directory ?? throw new ArgumentNullException(nameof(directory)));
        Directory.CreateDirectory(root);
        HardenWindowsDirectoryAcl(root);
        _lockPath = Path.Combine(root, "production-maintenance.lock");
        _statePath = Path.Combine(root, "production-maintenance.state.json");
        _integrityKeyProvider = integrityKeyProvider ?? throw new ArgumentNullException(nameof(integrityKeyProvider));
    }

    public async Task<IProductionMaintenanceLease> EnterExclusiveAsync(
        ProductionMaintenanceState state,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileStream stream;
        try
        {
            stream = new FileStream(
                _lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                1,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("Another production maintenance operation is already active.", ex);
        }

        try
        {
            await WriteStateAsync(state, cancellationToken);
            return new Lease(this, stream);
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    public async Task<ProductionMaintenanceState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var lockHeld = IsLockHeld();
        if (!File.Exists(_statePath))
        {
            return lockHeld ? ProductionMaintenanceState.RestoreCutover : ProductionMaintenanceState.Normal;
        }

        try
        {
            var info = new FileInfo(_statePath);
            if (info.Length <= 0 || info.Length > MaximumStateBytes)
            {
                return ProductionMaintenanceState.RecoveryRequired;
            }

            var envelope = JsonSerializer.Deserialize<StateEnvelope>(
                await File.ReadAllTextAsync(_statePath, cancellationToken),
                JsonOptions);
            if (envelope is null ||
                string.IsNullOrWhiteSpace(envelope.PayloadBase64) ||
                string.IsNullOrWhiteSpace(envelope.Authentication))
            {
                return ProductionMaintenanceState.RecoveryRequired;
            }

            byte[] payload;
            byte[] suppliedAuthentication;
            try
            {
                payload = Convert.FromBase64String(envelope.PayloadBase64);
                suppliedAuthentication = Convert.FromBase64String(envelope.Authentication);
            }
            catch (FormatException)
            {
                return ProductionMaintenanceState.RecoveryRequired;
            }

            try
            {
                var key = await _integrityKeyProvider.GetIntegrityKeyAsync(cancellationToken);
                try
                {
                    if (key.Length < 32)
                    {
                        return ProductionMaintenanceState.RecoveryRequired;
                    }

                    var expected = HMACSHA256.HashData(key, payload);
                    try
                    {
                        if (suppliedAuthentication.Length != expected.Length ||
                            !CryptographicOperations.FixedTimeEquals(suppliedAuthentication, expected))
                        {
                            return ProductionMaintenanceState.RecoveryRequired;
                        }
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(expected);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(key);
                }

                var state = JsonSerializer.Deserialize<StateFile>(payload, JsonOptions);
                var value = state?.State ?? ProductionMaintenanceState.RecoveryRequired;
                return lockHeld && value == ProductionMaintenanceState.Normal
                    ? ProductionMaintenanceState.RestoreCutover
                    : value;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
                CryptographicOperations.ZeroMemory(suppliedAuthentication);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return ProductionMaintenanceState.RecoveryRequired;
        }
    }

    private bool IsLockHeld()
    {
        if (!File.Exists(_lockPath))
        {
            return false;
        }

        try
        {
            using var probe = new FileStream(_lockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private async Task WriteStateAsync(ProductionMaintenanceState state, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new StateFile(state, DateTimeOffset.UtcNow), JsonOptions);
        byte[]? key = null;
        byte[]? authentication = null;
        try
        {
            key = await _integrityKeyProvider.GetIntegrityKeyAsync(cancellationToken);
            if (key.Length < 32)
            {
                throw new InvalidOperationException("Production-maintenance integrity key must be at least 32 bytes.");
            }

            authentication = HMACSHA256.HashData(key, payload);
            var envelope = new StateEnvelope(
                Convert.ToBase64String(payload),
                Convert.ToBase64String(authentication));
            var body = JsonSerializer.Serialize(envelope, JsonOptions);
            var bytes = Encoding.UTF8.GetBytes(body);
            if (bytes.LongLength > MaximumStateBytes)
            {
                throw new InvalidDataException("Production-maintenance state exceeded its maximum size.");
            }

            var temp = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
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
                    await stream.WriteAsync(bytes, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temp, _statePath, overwrite: true);
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

                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            if (key is not null)
            {
                CryptographicOperations.ZeroMemory(key);
            }

            if (authentication is not null)
            {
                CryptographicOperations.ZeroMemory(authentication);
            }
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

        foreach (var sid in new[]
        {
            userSid,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
        })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                inheritance,
                propagation,
                AccessControlType.Allow));
        }

        new DirectoryInfo(path).SetAccessControl(security);
    }

    private sealed record StateFile(ProductionMaintenanceState State, DateTimeOffset ChangedAtUtc);
    private sealed record StateEnvelope(string PayloadBase64, string Authentication);

    private sealed class Lease : IProductionMaintenanceLease
    {
        private readonly FileProductionMaintenanceBarrier _owner;
        private FileStream? _stream;
        private ProductionMaintenanceState _exitState = ProductionMaintenanceState.Normal;

        public Lease(FileProductionMaintenanceBarrier owner, FileStream stream)
        {
            _owner = owner;
            _stream = stream;
        }

        public Task SetExitStateAsync(
            ProductionMaintenanceState state,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _exitState = state;
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            var stream = Interlocked.Exchange(ref _stream, null);
            if (stream is null)
            {
                return;
            }

            try
            {
                await _owner.WriteStateAsync(_exitState, CancellationToken.None);
            }
            finally
            {
                await stream.DisposeAsync();
            }
        }
    }
}
