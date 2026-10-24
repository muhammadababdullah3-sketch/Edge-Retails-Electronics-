using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Domain.Common;
using System.Security.Cryptography;

namespace EdgeRetails.Central.Infrastructure.Storage;

public class DiskObjectStorageProvider : IObjectStorageProvider, IDisposable
{
    private readonly string _baseStorageDirectory;
    private readonly IAnchoredStorageRoot _storageRoot;
    private readonly object _cleanupGate = new();
    private string? _cleanupCursor;

    public DiskObjectStorageProvider(string? baseStorageDirectory = null)
    {
        try
        {
            baseStorageDirectory ??= Environment.GetEnvironmentVariable("CENTRAL_OBJECT_STORAGE_ROOT");
            if (string.IsNullOrWhiteSpace(baseStorageDirectory))
            {
                throw new CentralDomainException("STORAGE_ROOT_NOT_CONFIGURED", "storage.root_unavailable", "An explicit owned Central object-storage root is required.");
            }
            var configuredRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
                baseStorageDirectory,
                AppContext.BaseDirectory));
            _storageRoot = AnchoredStorageRootFactory.OpenOrCreate(configuredRoot);
            _baseStorageDirectory = _storageRoot.AbsoluteRoot;
        }
        catch (Exception exception) when (IsPathFailure(exception))
        {
            throw MapStorageFailure(exception);
        }
    }

    public async Task<string> SaveCiphertextAsync(Guid shopId, Guid objectId, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (shopId == Guid.Empty || objectId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_STORAGE_TARGET", "storage.invalid_target", "ShopId and ObjectId are required for object storage.");
        }

        var targetComponents = new[] { shopId.ToString("N"), $"{objectId:N}.bin" };
        var pendingComponents = new[] { shopId.ToString("N"), $"{objectId:N}.{Guid.NewGuid():N}.pending" };
        var fullTarget = ToAbsolutePath(targetComponents);

        try
        {
            _storageRoot.EnsureDirectory(new[] { shopId.ToString("N") });
            // Validate the current immutable identity before consuming the caller's stream.
            // In particular, a hard-link alias must fail before a pending upload is written.
            _storageRoot.FileExists(targetComponents);
            await using (var fileStream = _storageRoot.OpenFile(pendingComponents, FileMode.CreateNew, FileAccess.Write, FileShare.None, FileOptions.Asynchronous))
            {
                await content.CopyToAsync(fileStream, cancellationToken);
                await fileStream.FlushAsync(cancellationToken);
                _storageRoot.FlushToDisk(fileStream);
            }
            try
            {
                _storageRoot.MoveFileNoReplace(pendingComponents, targetComponents);
            }
            catch (IOException)
            {
                if (!_storageRoot.FileExists(targetComponents))
                {
                    throw;
                }

                // A retry after file publication but before database commit may find this object.
                // Only identical ciphertext may reuse its immutable identity.
                await using var existing = _storageRoot.OpenFile(targetComponents, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);
                await using var pending = _storageRoot.OpenFile(pendingComponents, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);
                var existingDigest = await SHA256.HashDataAsync(existing, cancellationToken);
                var pendingDigest = await SHA256.HashDataAsync(pending, cancellationToken);
                if (existing.Length != pending.Length ||
                    !CryptographicOperations.FixedTimeEquals(existingDigest, pendingDigest))
                {
                    throw new CentralDomainException("STORAGE_OBJECT_CONFLICT", "storage.object_conflict", "Object identity already contains different immutable ciphertext.");
                }
            }
            return fullTarget;
        }
        catch (Exception exception) when (IsPathFailure(exception))
        {
            throw MapStorageFailure(exception);
        }
        finally
        {
            try
            {
                _storageRoot.DeleteFile(pendingComponents);
            }
            catch (Exception exception) when (IsPathFailure(exception))
            {
                // A later configured reconciliation may remove this private publication temporary.
            }
        }
    }

    public Task<Stream> GetCiphertextAsync(string storagePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(storagePath))
        {
            throw new CentralDomainException("STORAGE_PATH_REQUIRED", "storage.path_required", "Storage path cannot be empty.");
        }

        var components = ResolveStorageComponents(storagePath);

        try
        {
            Stream fileStream = _storageRoot.OpenFile(components, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);
            return Task.FromResult(fileStream);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new CentralDomainException("OBJECT_NOT_FOUND", "storage.object_not_found", "Object ciphertext was not found.");
        }
        catch (Exception exception) when (IsPathFailure(exception))
        {
            throw MapStorageFailure(exception);
        }
    }

    public Task DeleteCiphertextAsync(string storagePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(storagePath))
        {
            return Task.CompletedTask;
        }

        var components = ResolveStorageComponents(storagePath);

        try
        {
            _storageRoot.DeleteFile(components);
        }
        catch (FileNotFoundException)
        {
            // Deleting an already absent object remains idempotent.
        }
        catch (DirectoryNotFoundException)
        {
            // Deleting an already absent object remains idempotent.
        }
        catch (Exception exception) when (IsPathFailure(exception))
        {
            throw MapStorageFailure(exception);
        }

        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string storagePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(storagePath))
        {
            return Task.FromResult(false);
        }

        var components = ResolveStorageComponents(storagePath);
        try
        {
            return Task.FromResult(_storageRoot.FileExists(components));
        }
        catch (Exception exception) when (IsPathFailure(exception))
        {
            throw MapStorageFailure(exception);
        }
    }

    public async Task<bool> AppendUploadChunkAsync(
        Guid shopId,
        Guid uploadId,
        long acceptedOffset,
        long offset,
        byte[] content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (shopId == Guid.Empty || uploadId == Guid.Empty || acceptedOffset < 0 || offset < 0 || content.Length == 0)
        {
            throw new CentralDomainException("INVALID_UPLOAD_CHUNK", "upload.chunk_invalid", "Upload chunk identity, offset, and content are required.");
        }

        var components = new[] { ".uploads", shopId.ToString("N"), $"{uploadId:N}.part" };
        try
        {
            _storageRoot.EnsureDirectory(components[..^1]);
            await using var stream = _storageRoot.OpenFile(components, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, FileOptions.Asynchronous);
            if (offset < acceptedOffset)
            {
                if (offset > acceptedOffset - content.LongLength || stream.Length < acceptedOffset)
                {
                    throw new CentralDomainException("UPLOAD_CHUNK_CONFLICT", "upload.chunk_conflict", "Retry does not match a complete previously accepted chunk.");
                }
                stream.Position = offset;
                var existing = new byte[content.Length];
                var totalRead = 0;
                while (totalRead < existing.Length)
                {
                    var read = await stream.ReadAsync(existing.AsMemory(totalRead), cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }
                    totalRead += read;
                }
                if (totalRead != content.Length || !CryptographicOperations.FixedTimeEquals(existing, content))
                {
                    throw new CentralDomainException("UPLOAD_CHUNK_CONFLICT", "upload.chunk_conflict", "Retry bytes differ from the previously accepted chunk.");
                }
                return true;
            }

            if (offset != acceptedOffset || stream.Length < acceptedOffset)
            {
                throw new CentralDomainException("UPLOAD_OFFSET_CONFLICT", "upload.offset_conflict", "Upload chunk offset does not match the persisted accepted offset.");
            }

            // A process may have died after writing the chunk but before persisting its offset.
            // The database offset is authoritative, so discard that uncommitted tail before retry.
            if (stream.Length > acceptedOffset)
            {
                stream.SetLength(acceptedOffset);
            }
            stream.Position = acceptedOffset;
            await stream.WriteAsync(content, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            _storageRoot.FlushToDisk(stream);
            return false;
        }
        catch (CentralDomainException)
        {
            throw;
        }
        catch (Exception exception) when (IsPathFailure(exception))
        {
            throw MapStorageFailure(exception);
        }
    }

    public Task<Stream> OpenUploadContentAsync(Guid shopId, Guid uploadId, CancellationToken cancellationToken = default)
    {
        if (shopId == Guid.Empty || uploadId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_UPLOAD_TARGET", "upload.target_invalid", "Shop and upload identities are required.");
        }
        var components = new[] { ".uploads", shopId.ToString("N"), $"{uploadId:N}.part" };
        try
        {
            Stream stream = _storageRoot.OpenFile(components, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);
            return Task.FromResult(stream);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new CentralDomainException("UPLOAD_CONTENT_NOT_FOUND", "upload.content_not_found", "Persisted upload content was not found.");
        }
        catch (Exception exception) when (IsPathFailure(exception))
        {
            throw MapStorageFailure(exception);
        }
    }

    public Task TruncateUploadContentAsync(Guid shopId, Guid uploadId, long acceptedOffset, CancellationToken cancellationToken = default)
    {
        if (shopId == Guid.Empty || uploadId == Guid.Empty || acceptedOffset < 0)
        {
            throw new CentralDomainException("INVALID_UPLOAD_TARGET", "upload.target_invalid", "Shop, upload, and accepted offset are required.");
        }
        var components = new[] { ".uploads", shopId.ToString("N"), $"{uploadId:N}.part" };
        try
        {
            if (!_storageRoot.FileExists(components))
            {
                if (acceptedOffset == 0)
                {
                    return Task.CompletedTask;
                }
                throw new CentralDomainException("UPLOAD_STORAGE_INCONSISTENT", "upload.storage_inconsistent", "Persisted upload bytes are missing.");
            }
            using var stream = _storageRoot.OpenFile(components, FileMode.Open, FileAccess.Write, FileShare.None);
            if (stream.Length < acceptedOffset)
            {
                throw new CentralDomainException("UPLOAD_STORAGE_INCONSISTENT", "upload.storage_inconsistent", "Persisted upload bytes are shorter than the accepted offset.");
            }
            if (stream.Length > acceptedOffset)
            {
                stream.SetLength(acceptedOffset);
                _storageRoot.FlushToDisk(stream);
            }
            return Task.CompletedTask;
        }
        catch (CentralDomainException)
        {
            throw;
        }
        catch (Exception exception) when (IsPathFailure(exception))
        {
            throw MapStorageFailure(exception);
        }
    }

    public Task DeleteUploadContentAsync(Guid shopId, Guid uploadId, CancellationToken cancellationToken = default)
    {
        if (shopId == Guid.Empty || uploadId == Guid.Empty)
        {
            return Task.CompletedTask;
        }
        var components = new[] { ".uploads", shopId.ToString("N"), $"{uploadId:N}.part" };
        try
        {
            _storageRoot.DeleteFile(components);
            _storageRoot.DeleteDirectoryIfEmpty(components[..^1]);
            return Task.CompletedTask;
        }
        catch (DirectoryNotFoundException)
        {
            return Task.CompletedTask;
        }
        catch (FileNotFoundException)
        {
            return Task.CompletedTask;
        }
        catch (Exception exception) when (IsPathFailure(exception))
        {
            throw MapStorageFailure(exception);
        }
    }

    public Task<(int Removed, int Deferred)> CleanupPendingPublicationsAsync(DateTimeOffset beforeUtc, int maximumFiles, CancellationToken cancellationToken = default)
    {
        if (maximumFiles < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFiles));
        }
        lock (_cleanupGate)
        {
            return Task.FromResult(CleanupPendingPublications(beforeUtc, maximumFiles, cancellationToken));
        }
    }

    private (int Removed, int Deferred) CleanupPendingPublications(DateTimeOffset beforeUtc, int maximumFiles, CancellationToken cancellationToken)
    {
        var removed = 0;
        var deferred = 0;
        var candidates = new List<string[]>();
        foreach (var directory in _storageRoot.EnumerateEntries(Array.Empty<string>(), "*"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!directory.IsDirectory || !Guid.TryParseExact(directory.Name, "N", out _))
            {
                continue;
            }
            var directoryComponents = new[] { directory.Name };
            foreach (var entry in _storageRoot.EnumerateEntries(directoryComponents, "*.pending"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.IsDirectory)
                {
                    continue;
                }
                var parts = entry.Name.Split('.');
                if (parts.Length != 3 || !Guid.TryParseExact(parts[0], "N", out _) || !Guid.TryParseExact(parts[1], "N", out _))
                {
                    continue;
                }
                candidates.Add(new[] { directory.Name, entry.Name });
            }
        }
        candidates.Sort((left, right) => string.CompareOrdinal(ToAbsolutePath(left), ToAbsolutePath(right)));
        var remaining = candidates.Where(path => _cleanupCursor is null || string.CompareOrdinal(ToAbsolutePath(path), _cleanupCursor) > 0).ToArray();
        if (remaining.Length == 0)
        {
            _cleanupCursor = null;
            remaining = candidates.ToArray();
        }
        foreach (var components in remaining.Take(maximumFiles))
        {
                cancellationToken.ThrowIfCancellationRequested();
                _cleanupCursor = ToAbsolutePath(components);
                try
                {
                    // An active writer's exclusive handle prevents this claim on Windows.
                    using var claimed = _storageRoot.OpenFile(components, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    if (_storageRoot.GetLastWriteTimeUtc(claimed) < beforeUtc.UtcDateTime)
                    {
                        _storageRoot.DeleteOpenFile(claimed);
                        removed++;
                    }
                }
                catch (IOException)
                {
                    deferred++;
                }
        }
        return (removed, deferred);
    }

    private IReadOnlyList<string> ResolveStorageComponents(string storagePath)
    {
        try
        {
            // Relative object paths are rooted here, never at the process working directory.
            // Partially rooted Windows paths (for example C:object.bin or \object.bin) are ambiguous.
            if (Path.IsPathRooted(storagePath) && !Path.IsPathFullyQualified(storagePath))
            {
                throw StoragePathRejected();
            }

            RejectWindowsPathAliases(storagePath);
            var fullPath = Path.GetFullPath(storagePath, _baseStorageDirectory);
            var relativePath = Path.GetRelativePath(_baseStorageDirectory, fullPath);
            if (relativePath == "." || relativePath == ".." || Path.IsPathRooted(relativePath)
                || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                throw StoragePathRejected();
            }

            var components = relativePath.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            if (components.Length == 0 || components.Any(component => component is "." or ".."))
            {
                throw StoragePathRejected();
            }
            return components;
        }
        catch (Exception exception) when (IsPathFailure(exception))
        {
            throw StoragePathRejected();
        }
    }

    private static void RejectWindowsPathAliases(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var rootLength = Path.GetPathRoot(path)?.Length ?? 0;
        var components = path[rootLength..].Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        foreach (var component in components)
        {
            if (component is "." or "..")
            {
                continue;
            }

            if (component.EndsWith('.') || component.EndsWith(' ') || component.Contains(':'))
            {
                // Reject aliases/alternate streams before normalization can hide their spelling.
                throw StoragePathRejected();
            }
        }
    }

    private string ToAbsolutePath(IReadOnlyList<string> components) =>
        components.Aggregate(_baseStorageDirectory, (path, component) => Path.Combine(path, component));

    public void Dispose() => _storageRoot.Dispose();

    private static bool IsPathFailure(Exception exception) =>
        exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException;

    private static CentralDomainException StoragePathRejected() =>
        new("PATH_TRAVERSAL_DETECTED", "storage.path_traversal", "Storage path is not an allowed object location.");

    private static CentralDomainException MapStorageFailure(Exception exception) =>
        exception is UnsafeStorageEntryException ? StoragePathRejected() : StorageOperationFailed();

    private static CentralDomainException StorageOperationFailed() =>
        new("STORAGE_IO_FAILED", "storage.io_failed", "Object storage operation could not be completed.");
}
