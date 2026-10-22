namespace EdgeRetails.Central.Application.Services;

public static class StorageDownloadGrantPolicy
{
    public static bool TryReadTtlSeconds(out int ttlSeconds) =>
        int.TryParse(Environment.GetEnvironmentVariable("CENTRAL_BACKUP_DOWNLOAD_GRANT_TTL_SECONDS"), out ttlSeconds) &&
        ttlSeconds is >= 1 and <= 300;

    public static bool TryParseTtlSeconds(string? value, out int ttlSeconds) =>
        int.TryParse(value, out ttlSeconds) && ttlSeconds is >= 1 and <= 300;
}
