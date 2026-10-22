namespace EdgeRetails.Central.Application.Abstractions;

public interface IObjectStorageProvider
{
    Task<string> SaveCiphertextAsync(Guid shopId, Guid objectId, Stream content, CancellationToken cancellationToken = default);
    Task<Stream> GetCiphertextAsync(string storagePath, CancellationToken cancellationToken = default);
    Task DeleteCiphertextAsync(string storagePath, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(string storagePath, CancellationToken cancellationToken = default);
    Task<bool> AppendUploadChunkAsync(Guid shopId, Guid uploadId, long acceptedOffset, long offset, byte[] content, CancellationToken cancellationToken = default);
    Task<Stream> OpenUploadContentAsync(Guid shopId, Guid uploadId, CancellationToken cancellationToken = default);
    Task TruncateUploadContentAsync(Guid shopId, Guid uploadId, long acceptedOffset, CancellationToken cancellationToken = default);
    Task DeleteUploadContentAsync(Guid shopId, Guid uploadId, CancellationToken cancellationToken = default);
    Task<(int Removed, int Deferred)> CleanupPendingPublicationsAsync(DateTimeOffset beforeUtc, int maximumFiles, CancellationToken cancellationToken = default);
}
