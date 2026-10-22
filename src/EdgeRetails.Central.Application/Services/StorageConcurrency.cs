using EdgeRetails.Central.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Central.Application.Services;

internal static class StorageConcurrency
{
    public static Task LockObjectAsync(ICentralDbContext db, Guid objectId, CancellationToken cancellationToken)
    {
        if (db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true)
        {
            return Task.CompletedTask;
        }
        var key = $"storage-object:{objectId:D}";
        return db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 2))", cancellationToken);
    }
}
