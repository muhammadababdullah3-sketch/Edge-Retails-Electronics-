using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using EdgeRetails.Application.Production.Backup;

namespace EdgeRetails.Infrastructure.Production.Backup;

public interface IBackupLocalKeyProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}

public sealed class WindowsDpapiBackupLocalKeyProtector : IBackupLocalKeyProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("EdgeRetails.Backup.BMK.Local.V1");

    public byte[] Protect(byte[] plaintext)
    {
        if (!OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException("Backup key protection requires Windows."); }
        return ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.LocalMachine);
    }

    public byte[] Unprotect(byte[] ciphertext)
    {
        if (!OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException("Backup key protection requires Windows."); }
        return ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.LocalMachine);
    }
}

/// <summary>
/// Explicit, persistent BMK authority. The Owner RK is supplied only to provisioning/recovery/
/// rotation and is never persisted. Every local record is OS protected; wrapped public envelopes
/// can accompany backups. Absence never triggers implicit generation.
/// </summary>
public sealed class WindowsProtectedBackupKeyRing : IVersionedBackupEncryptionKeyProvider
{
    private const int MaximumFileBytes = 256 * 1024;
    private readonly string _path;
    private readonly IBackupLocalKeyProtector _localProtection;
    private readonly Action<string> _securePath;
    private readonly EnvironmentBackupEncryptionKeyProvider _legacy;
    private readonly Func<string?> _readLegacy;
    private readonly bool _enforceAuthoritySecurity;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public WindowsProtectedBackupKeyRing(string path, IBackupLocalKeyProtector? localProtection = null,
        Action<string>? securePath = null, Func<string?>? legacyReader = null)
    {
        _path = Path.GetFullPath(path);
        _localProtection = localProtection ?? new WindowsDpapiBackupLocalKeyProtector();
        _securePath = securePath ?? HardenWindowsPath;
        _enforceAuthoritySecurity = securePath is null;
        _readLegacy = legacyReader ?? (() => Environment.GetEnvironmentVariable("EDGE_RETAILS_BACKUP_KEY"));
        _legacy = new EnvironmentBackupEncryptionKeyProvider(_readLegacy);
    }

    public async Task<BackupKeyMetadata> ProvisionAsync(byte[] ownerRecoveryKey, byte[]? existingLegacyKey = null,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(ownerRecoveryKey);
        if (existingLegacyKey is not null) { ValidateKey(existingLegacyKey); }
        await using var lease = await AcquireWriteLeaseAsync(cancellationToken);
        if (HasAuthorityFile()) { throw new InvalidOperationException("Backup protection is already provisioned."); }
        byte[]? configuredLegacy = null;
        if (!string.IsNullOrWhiteSpace(_readLegacy()))
        {
            configuredLegacy = await _legacy.GetKeyAsync(cancellationToken);
            if (existingLegacyKey is not null && !CryptographicOperations.FixedTimeEquals(existingLegacyKey, configuredLegacy))
            {
                CryptographicOperations.ZeroMemory(configuredLegacy);
                throw new InvalidOperationException("Provisioning cannot replace the established legacy backup key.");
            }
        }
        var importedLegacy = existingLegacyKey is not null || configuredLegacy is not null;
        var master = existingLegacyKey?.ToArray() ?? configuredLegacy?.ToArray() ?? RandomNumberGenerator.GetBytes(32);
        try
        {
            if (CryptographicOperations.FixedTimeEquals(master, ownerRecoveryKey))
            {
                throw new ArgumentException("Backup master and Owner recovery keys must be distinct.");
            }
            var entry = CreateEntry(master, ownerRecoveryKey, importedLegacy);
            await SaveAsync(new LocalRing(1, entry.Metadata.KeyVersion,
                importedLegacy ? entry.Metadata.KeyVersion : null, [entry]), cancellationToken);
            return entry.Metadata;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(master);
            if (configuredLegacy is not null) { CryptographicOperations.ZeroMemory(configuredLegacy); }
        }
    }

    public async Task<BackupKeyMetadata> RotateAsync(byte[] ownerRecoveryKey, CancellationToken cancellationToken = default)
    {
        ValidateKey(ownerRecoveryKey);
        await using var lease = await AcquireWriteLeaseAsync(cancellationToken);
        var ring = await ReadAsync(cancellationToken);
        var current = ring.Entries.Single(entry => entry.Metadata.KeyVersion == ring.ActiveVersion);
        var verified = Unwrap(current.Metadata, ownerRecoveryKey);
        CryptographicOperations.ZeroMemory(verified);
        var master = RandomNumberGenerator.GetBytes(32);
        try
        {
            var entry = CreateEntry(master, ownerRecoveryKey);
            await SaveAsync(ring with { ActiveVersion = entry.Metadata.KeyVersion, Entries = [.. ring.Entries, entry] }, cancellationToken, replaceExisting: true);
            return entry.Metadata;
        }
        finally { CryptographicOperations.ZeroMemory(master); }
    }

    public async Task RecoverAsync(BackupKeyMetadata metadata, byte[] ownerRecoveryKey, CancellationToken cancellationToken = default)
    {
        var master = Unwrap(metadata, ownerRecoveryKey);
        try
        {
            await using var lease = await AcquireWriteLeaseAsync(cancellationToken);
            if (HasAuthorityFile()) { throw new InvalidOperationException("Recovery cannot overwrite established backup protection."); }
            var entry = new LocalEntry(metadata, Convert.ToBase64String(master));
            await SaveAsync(new LocalRing(1, metadata.KeyVersion, metadata.LegacyCompatible ? metadata.KeyVersion : null, [entry]), cancellationToken);
        }
        finally { CryptographicOperations.ZeroMemory(master); }
    }

    public async Task<BackupKeyMetadata?> GetCurrentMetadataAsync(CancellationToken cancellationToken = default)
    {
        if (!HasAuthorityFile())
        {
            var legacy = await _legacy.GetKeyAsync(cancellationToken);
            CryptographicOperations.ZeroMemory(legacy);
            return null; // Explicit legacy authority remains usable, never silently replaced.
        }
        var ring = await ReadAsync(cancellationToken);
        return ring.Entries.Single(entry => entry.Metadata.KeyVersion == ring.ActiveVersion).Metadata;
    }

    public async Task<byte[]> GetKeyAsync(CancellationToken cancellationToken = default)
    {
        if (!HasAuthorityFile()) { return await _legacy.GetKeyAsync(cancellationToken); }
        var ring = await ReadAsync(cancellationToken);
        return DecodeMaster(ring.Entries.Single(entry => entry.Metadata.KeyVersion == ring.ActiveVersion));
    }

    public async Task<byte[]> GetLegacyKeyAsync(CancellationToken cancellationToken = default)
    {
        if (!HasAuthorityFile()) { return await _legacy.GetKeyAsync(cancellationToken); }
        var ring = await ReadAsync(cancellationToken);
        if (ring.LegacyVersion is null) { throw new InvalidOperationException("The retained legacy backup key is unavailable."); }
        return DecodeMaster(ring.Entries.Single(entry => entry.Metadata.KeyVersion == ring.LegacyVersion));
    }

    public async Task<byte[]> GetVersionKeyAsync(string keyVersion, CancellationToken cancellationToken = default)
    {
        ValidateVersion(keyVersion);
        var ring = await ReadAsync(cancellationToken);
        var entry = ring.Entries.SingleOrDefault(entry => entry.Metadata.KeyVersion == keyVersion)
            ?? throw new InvalidOperationException("The requested retained backup key is unavailable.");
        return DecodeMaster(entry);
    }

    public async Task ImportRecoveryVersionAsync(BackupKeyMetadata metadata, byte[] ownerRecoveryKey,
        CancellationToken cancellationToken = default)
    {
        var master = Unwrap(metadata, ownerRecoveryKey);
        try
        {
            await using var lease = await AcquireWriteLeaseAsync(cancellationToken);
            var ring = await ReadAsync(cancellationToken);
            var existing = ring.Entries.SingleOrDefault(entry => entry.Metadata.KeyVersion == metadata.KeyVersion);
            if (existing is not null)
            {
                var retained = DecodeMaster(existing);
                try
                {
                    if (existing.Metadata != metadata || !CryptographicOperations.FixedTimeEquals(retained, master))
                    {
                        throw new InvalidOperationException("Recovery cannot replace a retained backup version.");
                    }
                    return;
                }
                finally { CryptographicOperations.ZeroMemory(retained); }
            }
            if (metadata.LegacyCompatible && ring.LegacyVersion is not null)
            {
                throw new InvalidOperationException("Recovery cannot replace the established legacy backup version.");
            }
            await SaveAsync(ring with
            {
                LegacyVersion = metadata.LegacyCompatible ? metadata.KeyVersion : ring.LegacyVersion,
                Entries = [.. ring.Entries, new LocalEntry(metadata, Convert.ToBase64String(master))]
            }, cancellationToken, replaceExisting: true);
        }
        finally { CryptographicOperations.ZeroMemory(master); }
    }

    private static LocalEntry CreateEntry(byte[] master, byte[] recovery, bool legacyCompatible = false)
    {
        var version = Guid.NewGuid().ToString("N");
        var recoveryId = Convert.ToHexString(SHA256.HashData(recovery));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[32];
        var tag = new byte[16];
        using var aes = new AesGcm(recovery, 16);
        aes.Encrypt(nonce, master, ciphertext, tag, WrapAad(version, recoveryId, legacyCompatible));
        return new LocalEntry(new BackupKeyMetadata(2, version, recoveryId,
            Convert.ToBase64String(ciphertext), Convert.ToBase64String(nonce), Convert.ToBase64String(tag), legacyCompatible),
            Convert.ToBase64String(master));
    }

    private static byte[] Unwrap(BackupKeyMetadata metadata, byte[] recovery)
    {
        ValidateKey(recovery);
        ValidateMetadata(metadata);
        var recoveryId = Convert.ToHexString(SHA256.HashData(recovery));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(recoveryId), Encoding.ASCII.GetBytes(metadata.RecoveryKeyId)))
        {
            throw new CryptographicException("Backup recovery authorization does not match this envelope.");
        }
        var master = new byte[32];
        try
        {
            using var aes = new AesGcm(recovery, 16);
            aes.Decrypt(Convert.FromBase64String(metadata.WrapNonce), Convert.FromBase64String(metadata.WrappedBackupMasterKey),
                Convert.FromBase64String(metadata.WrapTag), master, WrapAad(metadata.KeyVersion, metadata.RecoveryKeyId, metadata.LegacyCompatible));
            return master;
        }
        catch { CryptographicOperations.ZeroMemory(master); throw; }
    }

    private static byte[] WrapAad(string version, string recoveryId, bool legacyCompatible)
        => Encoding.UTF8.GetBytes($"EdgeRetails.Backup.BMK.Wrap.V1|2|{version}|{recoveryId}|{legacyCompatible}");

    private async Task<LocalRing> ReadAsync(CancellationToken cancellationToken)
    {
        if (!HasAuthorityFile()) { throw new InvalidDataException("Backup key authority is missing."); }
        if (_enforceAuthoritySecurity) { ValidateTrustedAuthority(_path); }
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length is <= 0 or > MaximumFileBytes || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("Backup key authority is missing or invalid.");
        }
        var encrypted = await File.ReadAllBytesAsync(_path, cancellationToken);
        var plaintext = _localProtection.Unprotect(encrypted);
        try
        {
            var ring = JsonSerializer.Deserialize<LocalRing>(plaintext, JsonOptions)
                ?? throw new InvalidDataException("Backup key authority is invalid.");
            if (ring.FormatVersion != 1 || ring.Entries is null || ring.Entries.Count == 0 ||
                ring.Entries.Select(entry => entry.Metadata.KeyVersion).Distinct(StringComparer.Ordinal).Count() != ring.Entries.Count ||
                !ring.Entries.Any(entry => entry.Metadata.KeyVersion == ring.ActiveVersion) ||
                (ring.LegacyVersion is not null && !ring.Entries.Any(entry => entry.Metadata.KeyVersion == ring.LegacyVersion)))
            {
                throw new InvalidDataException("Backup key authority is invalid.");
            }
            foreach (var entry in ring.Entries)
            {
                ValidateMetadata(entry.Metadata);
                var key = DecodeMaster(entry);
                CryptographicOperations.ZeroMemory(key);
            }
            return ring;
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private async Task SaveAsync(LocalRing ring, CancellationToken cancellationToken, bool replaceExisting = false)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(ring, JsonOptions);
        byte[] encrypted;
        try { encrypted = _localProtection.Protect(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        if (encrypted.Length is <= 0 or > MaximumFileBytes)
        {
            throw new InvalidDataException("Backup key authority exceeds its supported size; existing versions are unchanged.");
        }
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                _securePath(temporary);
                await output.WriteAsync(encrypted, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(true);
            }
            if (HasAuthorityFile() && !replaceExisting) { throw new InvalidOperationException("Backup protection is already established."); }
            File.Move(temporary, _path, overwrite: replaceExisting);
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }

    private async Task<FileStream> AcquireWriteLeaseAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path)!;
        if (_enforceAuthoritySecurity)
        {
            RejectReparseAncestors(_path);
            var existingParent = directory;
            while (!Directory.Exists(existingParent))
            {
                existingParent = Path.GetDirectoryName(existingParent)
                    ?? throw new UnauthorizedAccessException("Backup protection parent authority is unavailable.");
            }
            ValidateTrustedAuthority(existingParent);
        }
        Directory.CreateDirectory(directory);
        _securePath(directory);
        var lockPath = _path + ".lock";
        if (_enforceAuthoritySecurity && File.Exists(lockPath)) { ValidateTrustedAuthority(lockPath); }
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                try { _securePath(lockPath); return stream; }
                catch { stream.Dispose(); throw; }
            }
            catch (IOException) when (attempt < 100) { await Task.Delay(25, cancellationToken); }
        }
    }

    private static byte[] DecodeMaster(LocalEntry entry)
    {
        var master = Convert.FromBase64String(entry.MasterKey);
        if (master.Length != 32) { CryptographicOperations.ZeroMemory(master); throw new InvalidDataException("Backup key authority is invalid."); }
        return master;
    }

    private static void ValidateKey(byte[] key)
    {
        if (key is null || key.Length != 32) { throw new ArgumentException("Backup key material must contain exactly 32 bytes."); }
    }

    private static void ValidateVersion(string version)
    {
        if (!Guid.TryParseExact(version, "N", out _)) { throw new InvalidDataException("Backup key version is invalid."); }
    }

    private static void ValidateMetadata(BackupKeyMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ValidateVersion(metadata.KeyVersion);
        if (metadata.EncryptionVersion != 2 || metadata.RecoveryKeyId.Length != 64 || !metadata.RecoveryKeyId.All(Uri.IsHexDigit) ||
            Convert.FromBase64String(metadata.WrappedBackupMasterKey).Length != 32 ||
            Convert.FromBase64String(metadata.WrapNonce).Length != 12 || Convert.FromBase64String(metadata.WrapTag).Length != 16)
        {
            throw new InvalidDataException("Backup recovery envelope is invalid.");
        }
    }

    private static void HardenWindowsPath(string path)
    {
        if (!OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException("Backup key protection requires Windows."); }
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        RejectReparseAncestors(path);
        if (Directory.Exists(path))
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(admins);
            foreach (var sid in new[] { system, admins })
            {
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            }
            new DirectoryInfo(path).SetAccessControl(security);
        }
        else
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(admins);
            foreach (var sid in new[] { system, admins })
            {
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
            }
            new FileInfo(path).SetAccessControl(security);
        }
    }

    private bool HasAuthorityFile()
    {
        if (_enforceAuthoritySecurity) { RejectReparseAncestors(_path); }
        try
        {
            var attributes = File.GetAttributes(_path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new InvalidDataException("Backup key authority path is invalid.");
            }
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        // Access denied is NOT absence and must never enable legacy fallback.
    }

    private static void RejectReparseAncestors(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new UnauthorizedAccessException("Backup authority cannot use redirected filesystem paths.");
                }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static void ValidateTrustedAuthority(string path)
    {
        if (!OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException("Backup key protection requires Windows."); }
        RejectReparseAncestors(path);
        var trustedInstaller = (SecurityIdentifier)new NTAccount("NT SERVICE", "TrustedInstaller").Translate(typeof(SecurityIdentifier));
        bool Trusted(SecurityIdentifier sid) => sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
            sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || sid.Equals(trustedInstaller);
        var first = true;
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            var directory = Directory.Exists(current);
            FileSystemSecurity security = directory
                ? new DirectoryInfo(current).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
                : new FileInfo(current).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier
                ?? throw new UnauthorizedAccessException("Backup authority ownership is unavailable.");
            if (!Trusted(owner)) { throw new UnauthorizedAccessException("Backup authority requires trusted filesystem ownership."); }
            var dangerous = FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
                FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
            if (first)
            {
                dangerous |= FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes;
                if (!directory) { dangerous |= FileSystemRights.ReadData; }
            }
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType == AccessControlType.Allow && !Trusted((SecurityIdentifier)rule.IdentityReference) &&
                    (rule.FileSystemRights & dangerous) != 0)
                {
                    throw new UnauthorizedAccessException("Backup authority permissions allow an untrusted writer.");
                }
            }
            first = false;
        }
    }

    private sealed record LocalEntry(BackupKeyMetadata Metadata, string MasterKey);
    private sealed record LocalRing(int FormatVersion, string ActiveVersion, string? LegacyVersion, IReadOnlyList<LocalEntry> Entries);
}
