using EdgeRetails.Application.Production.Printing;
using EdgeRetails.Domain.Production;
using EdgeRetails.Infrastructure.Persistence;

namespace EdgeRetails.Infrastructure.Production.Printing;

public sealed class EfPhysicalLabelPrintIntentWriter(EdgeRetailsDbContext db) : IPhysicalLabelPrintIntentWriter
{
    public void AddOriginalIntents(
        Guid sourceClientOperationId,
        Guid? inventoryMovementId,
        IReadOnlyList<Guid> inventoryUnitIds,
        DateTimeOffset createdAt)
    {
        if (inventoryUnitIds.Count == 0)
        {
            return;
        }

        foreach (var unitId in inventoryUnitIds)
        {
            db.PhysicalLabelPrintIntents.Add(new PhysicalLabelPrintIntent
            {
                Id = Guid.CreateVersion7(),
                InventoryUnitId = unitId,
                InventoryMovementId = inventoryMovementId,
                SourceClientOperationId = sourceClientOperationId,
                Kind = PhysicalLabelPrintIntentKind.Original,
                Status = PhysicalLabelPrintIntentStatus.PendingDispatch,
                AttemptCount = 0,
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
                Version = 1
            });
        }
    }
}
