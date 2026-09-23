namespace EdgeRetails.Worker.Jobs;

public interface IWorkerJobLock
{
    Task<IAsyncDisposable?> TryAcquireJobLockAsync(string jobName, CancellationToken cancellationToken = default);
}

public sealed class FileWorkerJobLock : IWorkerJobLock
{
    private readonly string _lockDirectory;

    public FileWorkerJobLock(string lockDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockDirectory);
        _lockDirectory = Path.GetFullPath(lockDirectory);
    }

    public Task<IAsyncDisposable?> TryAcquireJobLockAsync(string jobName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_lockDirectory);

        var safeName = string.Join("_", jobName.Split(Path.GetInvalidFileNameChars()));
        var lockFilePath = Path.Combine(_lockDirectory, $"worker-job-{safeName}.lock");

        try
        {
            var stream = new FileStream(
                lockFilePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                1,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose);

            return Task.FromResult<IAsyncDisposable?>(new JobLockLease(stream));
        }
        catch (IOException)
        {
            return Task.FromResult<IAsyncDisposable?>(null);
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult<IAsyncDisposable?>(null);
        }
    }

    private sealed class JobLockLease : IAsyncDisposable
    {
        private FileStream? _stream;

        public JobLockLease(FileStream stream)
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
