using EdgeRetails.Application.Common;
using EdgeRetails.Domain.Inventory;

namespace EdgeRetails.Application.Abstractions;

public sealed record PhysicalUnitCreationEntry(
    string? SerialNumber,
    string? Imei1,
    string? Imei2,
    InventoryUnitStatus Status,
    decimal AcquisitionCost,
    Guid? InventoryLotId,
    InventoryUnitOriginType OriginType,
    Guid? SourcePurchaseItemId = null,
    Guid? SourceWarrantyClaimItemId = null,
    Guid? SourceWarrantyCaseId = null,
    Guid? SourceStockAdjustmentItemId = null);

public interface IPhysicalUnitCreationAuthority
{
    Task<Result<IReadOnlyList<InventoryUnit>>> CreateAsync(
        Guid supplierId,
        Guid productId,
        IReadOnlyList<PhysicalUnitCreationEntry> entries,
        CancellationToken cancellationToken);
}
