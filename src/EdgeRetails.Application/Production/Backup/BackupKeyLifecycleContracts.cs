namespace EdgeRetails.Application.Production.Backup;

/// <summary>Public recovery envelope only: no plaintext BMK or Owner RK.</summary>
public sealed record BackupKeyMetadata(
    int EncryptionVersion,
    string KeyVersion,
    string RecoveryKeyId,
    string WrappedBackupMasterKey,
    string WrapNonce,
    string WrapTag,
    bool LegacyCompatible = false);

public interface IVersionedBackupEncryptionKeyProvider : IBackupEncryptionKeyProvider
{
    Task<BackupKeyMetadata?> GetCurrentMetadataAsync(CancellationToken cancellationToken = default);
    Task<byte[]> GetVersionKeyAsync(string keyVersion, CancellationToken cancellationToken = default);
    Task<byte[]> GetLegacyKeyAsync(CancellationToken cancellationToken = default);
}

public interface IVersionedBackupProtector : IBackupProtector
{
    Task<BackupKeyMetadata?> GetCurrentMetadataAsync(CancellationToken cancellationToken = default);
    Task ProtectVersionAsync(string plainDumpPath, string protectedBackupPath, BackupKeyMetadata? metadata,
        CancellationToken cancellationToken = default);
    Task UnprotectVersionAsync(string protectedBackupPath, string plainDumpPath, BackupKeyMetadata? metadata,
        CancellationToken cancellationToken = default);
}
