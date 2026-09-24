namespace EdgeRetails.Application.Production.Backup;

public interface IBackupEncryptionKeyProvider
{
    /// <summary>Returns exactly 32 bytes of key material from the existing portable recovery-key trust domain.</summary>
    Task<byte[]> GetKeyAsync(CancellationToken cancellationToken = default);
}

public interface IBackupProtector
{
    string ProtectionName { get; }
    Task ProtectAsync(string plainDumpPath, string protectedBackupPath, CancellationToken cancellationToken = default);
    Task UnprotectAsync(string protectedBackupPath, string plainDumpPath, CancellationToken cancellationToken = default);
}

/// <summary>
/// Authenticates backup manifest metadata. The implementation should derive a distinct MAC sub-key
/// from the backup recovery-key domain rather than reuse raw AES material directly.
/// </summary>
public interface IBackupManifestAuthenticator
{
    Task<string> ComputeAuthenticationAsync(BackupManifest manifest, CancellationToken cancellationToken = default);
    Task<bool> VerifyAuthenticationAsync(BackupManifest manifest, string authentication, CancellationToken cancellationToken = default);
}

/// <summary>
/// Separate integrity trust domain for the local restore journal. Do not reuse license-signing,
/// update-signing, recovery-signing, or backup-encryption key material.
/// </summary>
public interface IRestoreJournalIntegrityKeyProvider
{
    Task<byte[]> GetIntegrityKeyAsync(CancellationToken cancellationToken = default);
}
