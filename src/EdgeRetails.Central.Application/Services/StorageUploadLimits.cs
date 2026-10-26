namespace EdgeRetails.Central.Application.Services;

public sealed record StorageUploadLimits(int MaxObjectBytes, int MaxChunkBytes)
{
    public static bool TryRead(out StorageUploadLimits limits) => TryParse(
        Environment.GetEnvironmentVariable("CENTRAL_UPLOAD_MAX_OBJECT_BYTES"),
        Environment.GetEnvironmentVariable("CENTRAL_UPLOAD_MAX_CHUNK_BYTES"), out limits);

    public static bool TryParse(string? objectValue, string? chunkValue, out StorageUploadLimits limits)
    {
        limits = new(0, 0);
        if (!int.TryParse(objectValue, out var objectBytes) ||
            objectBytes is < 1 or > 100 * 1024 * 1024 ||
            !int.TryParse(chunkValue, out var chunkBytes) ||
            chunkBytes < 1 || chunkBytes > objectBytes)
        {
            return false;
        }
        limits = new(objectBytes, chunkBytes);
        return true;
    }
}
