using EdgeRetails.Domain.Common;

namespace EdgeRetails.Domain.Warranty;

public sealed class ShopWarrantySendAllocation : Entity
{
    public Guid CaseId { get; set; }
    public Guid OriginalInventoryLotId { get; set; }
    public Guid SendMovementId { get; set; }
    public decimal BaseQuantity { get; set; }
    public decimal SourceUnitCostSnapshot { get; set; }
    public decimal SendTimeMwaUnitCostSnapshot { get; set; }
    // Forensic send-time evidence; future disposition uses the canonical current MWA.
    public decimal SendTimeCarryingValueSnapshot { get; set; }
    public Guid ClientOperationId { get; set; }
    public Guid ActorId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class ShopWarrantyResolutionAllocation : Entity
{
    public Guid SendAllocationId { get; set; }
    public Guid ResolutionMovementId { get; set; }
    public decimal ResolvedBaseQuantity { get; set; }
    public WarrantyResolutionType ResolutionOutcome { get; set; }
    public decimal? ResolutionTimeMwaUnitCostSnapshot { get; set; }
    public decimal ActualResolvedCarryingValue { get; set; }
    public decimal? SupplierCreditAmount { get; set; }
    public Guid? ReplacementInventoryLotId { get; set; }
    public Guid ClientOperationId { get; set; }
    public Guid ActorId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class WarrantyClaimSourceAllocation : Entity
{
    public Guid ClaimItemId { get; set; }
    public Guid SaleConsumptionId { get; set; }
    public decimal BaseQuantity { get; set; }
    public Guid ClientOperationId { get; set; }
    public Guid ActorId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class SaleReturnSourceAllocation : Entity
{
    public Guid SaleReturnItemId { get; set; }
    public Guid SaleConsumptionId { get; set; }
    public Guid ReturnMovementId { get; set; }
    public Guid? RestoredInventoryLotId { get; set; }
    public decimal BaseQuantity { get; set; }
    public Guid ClientOperationId { get; set; }
    public Guid ActorId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
