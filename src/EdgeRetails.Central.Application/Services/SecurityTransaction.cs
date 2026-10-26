using EdgeRetails.Central.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
namespace EdgeRetails.Central.Application.Services;

// A transaction-scoped database lock protects read/validate/write security decisions
// across processes. In-memory unit fixtures do not claim database concurrency proof.
public static class SecurityTransaction
{
    public static async Task<IDbContextTransaction?> BeginAsync(ICentralDbContext db, string resource, CancellationToken ct)
    {
        if (!db.Database.IsRelational())
        {
            return null;
        }

        var owns = db.Database.CurrentTransaction is null;
        var transaction = owns ? await db.Database.BeginTransactionAsync(ct) : null;
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({resource}, 0))", ct);
        return transaction;
    }
}
