using EdgeRetails.Domain.Production;

namespace EdgeRetails.Application.Production.Printing;

public interface IPhysicalLabelPrintIntentWriter
{
    void AddOriginalIntents(
        Guid sourceClientOperationId,
        Guid? inventoryMovementId,
        IReadOnlyList<Guid> inventoryUnitIds,
        DateTimeOffset createdAt);
}
