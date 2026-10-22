using EdgeRetails.Central.Domain.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EdgeRetails.Central.Infrastructure.Persistence.Interceptors;

public class AppendOnlyAuditInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ValidateAuditEntries(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ValidateAuditEntries(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void ValidateAuditEntries(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries<CentralAuditRecord>())
        {
            if (entry.State == EntityState.Modified)
            {
                throw new InvalidOperationException("CentralAuditRecord is strictly append-only. Modification of audit logs is forbidden.");
            }

            if (entry.State == EntityState.Deleted)
            {
                throw new InvalidOperationException("CentralAuditRecord is strictly append-only. Deletion of audit logs is forbidden.");
            }
        }
    }
}
