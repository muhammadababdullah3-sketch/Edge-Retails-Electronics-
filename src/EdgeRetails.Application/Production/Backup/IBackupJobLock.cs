namespace EdgeRetails.Application.Production.Backup;

public interface IBackupJobLock
{
    Task<IAsyncDisposable?> TryAcquireLockAsync(CancellationToken cancellationToken = default);
}
