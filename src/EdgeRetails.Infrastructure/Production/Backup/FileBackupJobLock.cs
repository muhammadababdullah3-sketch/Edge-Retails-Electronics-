using EdgeRetails.Application.Production.Backup;

namespace EdgeRetails.Infrastructure.Production.Backup;

public sealed class FileBackupJobLock : IBackupJobLock
{
    private readonly string _lockFilePath;

    public FileBackupJobLock(string lockFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockFilePath);
        _lockFilePath = Path.GetFullPath(lockFilePath);
    }

    public Task<IAsyncDisposable?> TryAcquireLockAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var dir = Path.GetDirectoryName(_lockFilePath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        try
        {
            var stream = new FileStream(
                _lockFilePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                1,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose);

            return Task.FromResult<IAsyncDisposable?>(new LockLease(stream));
        }
        catch (IOException)
        {
            // Another process or thread is holding the exclusive backup lock
            return Task.FromResult<IAsyncDisposable?>(null);
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult<IAsyncDisposable?>(null);
        }
    }

    private sealed class LockLease : IAsyncDisposable
    {
        private FileStream? _stream;

        public LockLease(FileStream stream)
        {
            _stream = stream;
        }

        public async ValueTask DisposeAsync()
        {
            var stream = Interlocked.Exchange(ref _stream, null);
            if (stream is not null)
            {
                await stream.DisposeAsync();
            }
        }
    }
}
