using EdgeRetails.Domain.Common;

namespace EdgeRetails.Domain.Inventory;

public enum InventoryBucket
{
    Sellable = 1,
    Damaged = 2,
    Defective = 3,
    WithSupplier = 4,
    Scrap = 5
}

public enum InventoryUnitStatus
{
    InStock = 1,
    Sold = 2,
    IssuedThaka = 3,
    Damaged = 4,
    Defective = 5,
    WithSupplier = 6,
    SupplierReturned = 7,
    Scrapped = 8,
    ReceiptVoided = 9,
    WarrantyCustomerHeld = 10,
    WarrantyCustomerHandedOver = 11
}

public enum InventoryUnitOriginType
{
    Purchase = 1,
    WarrantyReplacement = 2,
    StockAdjustment = 3
}

public enum InventoryMovementType
{
    OpeningStock = 1,
    PurchaseIn = 2,
    SaleOut = 3,
    SaleReturn = 4,
    ThakaOut = 5,
    ThakaReturn = 6,
    PurchaseReturn = 7,
    StockAdjustment = 8,
    MarkDamaged = 9,
    MarkDefective = 10,
    RestoreToSellable = 11,
    SendToSupplierWarranty = 12,
    ReceiveRepairedFromSupplier = 13,
    ReceiveReplacementFromSupplier = 14,
    WarrantyRejectedReturn = 15,
    WriteOffToScrap = 16,
    PhysicalCountCorrection = 17,
    PurchaseVoid = 18,
    WarrantyCreditResolution = 19
}

public sealed class StockBalance : Entity
{
    public Guid ProductId { get; set; }
    public decimal SellableQty { get; set; }
    public decimal DamagedQty { get; set; }
    public decimal DefectiveQty { get; set; }
    public decimal WithSupplierQty { get; set; }
    public decimal ScrapQty { get; set; }
    public long Version { get; set; }

    public decimal Get(InventoryBucket bucket) => bucket switch
    {
        InventoryBucket.Sellable => SellableQty,
        InventoryBucket.Damaged => DamagedQty,
        InventoryBucket.Defective => DefectiveQty,
        InventoryBucket.WithSupplier => WithSupplierQty,
        InventoryBucket.Scrap => ScrapQty,
        _ => throw new ArgumentOutOfRangeException(nameof(bucket))
    };

    public void ApplyDelta(InventoryBucket bucket, decimal delta)
    {
        var next = QuantityMath.RoundQuantity(Get(bucket) + delta);
        if (next < 0)
        {
            throw new BusinessRuleException(
                "inventory.negative_bucket",
                $"Inventory bucket {bucket} cannot become negative.");
        }

        Set(bucket, next);
        Version++;
    }

    public void Transfer(InventoryBucket from, InventoryBucket to, decimal baseQuantity)
    {
        if (from == to)
        {
            throw new BusinessRuleException(
                "inventory.same_bucket_transfer",
                "Source and destination inventory buckets must differ.");
        }

        if (baseQuantity <= 0)
        {
            throw new BusinessRuleException(
                "inventory.quantity_positive",
                "Inventory transfer quantity must be greater than zero.");
        }

        var rounded = QuantityMath.RoundQuantity(baseQuantity);
        if (Get(from) < rounded)
        {
            throw new BusinessRuleException(
                "inventory.insufficient_bucket_quantity",
                $"Insufficient quantity in {from}.");
        }

        ApplyDelta(from, -rounded);
        ApplyDelta(to, rounded);
    }

    private void Set(InventoryBucket bucket, decimal value)
    {
        switch (bucket)
        {
            case InventoryBucket.Sellable:
                SellableQty = value;
                break;
            case InventoryBucket.Damaged:
                DamagedQty = value;
                break;
            case InventoryBucket.Defective:
                DefectiveQty = value;
                break;
            case InventoryBucket.WithSupplier:
                WithSupplierQty = value;
                break;
            case InventoryBucket.Scrap:
                ScrapQty = value;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(bucket));
        }
    }
}

public sealed class InventoryMovement : Entity
{
    public Guid ProductId { get; set; }
    public InventoryMovementType MovementType { get; set; }
    public string ReferenceType { get; set; } = string.Empty;
    public Guid? ReferenceId { get; set; }
    public decimal? UnitCostSnapshot { get; set; }
    public decimal RecognizedLossAmount { get; set; }
    public Guid ActorId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public Guid CorrelationId { get; set; }
    public string? Reason { get; set; }
    public string? Note { get; set; }
}

public sealed class InventoryMovementEffect : Entity
{
    public Guid MovementId { get; set; }
    public InventoryBucket StockBucket { get; set; }
    public decimal QuantityDelta { get; set; }
    public decimal QuantityBefore { get; set; }
    public decimal QuantityAfter { get; set; }
}

public sealed class InventoryUnit : Entity
{
    public Guid ProductId { get; set; }
    public Guid? SupplierProductId { get; set; }
    public InventoryUnitOriginType OriginType { get; set; } = InventoryUnitOriginType.Purchase;
    public Guid? SourceWarrantyClaimItemId { get; set; }
    public long? ItemSequence { get; set; }
    public string? TrackingCode { get; set; }
    public string? SupplierCodeSnapshot { get; set; }
    public string? ProductSkuSnapshot { get; set; }
    public string? SerialNumber { get; set; }
    public string? Imei1 { get; set; }
    public string? Imei2 { get; set; }
    public InventoryUnitStatus Status { get; set; } = InventoryUnitStatus.InStock;
    public decimal AcquisitionCost { get; set; }
    public Guid? InventoryLotId { get; set; }
    public Guid? SourcePurchaseItemId { get; set; }
    public Guid? SourceWarrantyCaseId { get; set; }
    public Guid? SourceStockAdjustmentItemId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public long Version { get; set; }

    public void ValidateOriginInvariants()
    {
        switch (OriginType)
        {
            case InventoryUnitOriginType.Purchase:
                if (SourcePurchaseItemId is null || SourcePurchaseItemId == Guid.Empty)
                {
                    throw new BusinessRuleException(
                        "inventory.purchase_origin_missing_source",
                        "Purchase-origin inventory unit requires SourcePurchaseItemId.");
                }

                if (SourceStockAdjustmentItemId is not null || SourceWarrantyClaimItemId is not null || SourceWarrantyCaseId is not null)
                {
                    throw new BusinessRuleException(
                        "inventory.purchase_origin_invalid_sources",
                        "Purchase-origin inventory unit cannot have warranty or stock adjustment source references.");
                }
                break;

            case InventoryUnitOriginType.WarrantyReplacement:
                if (SourcePurchaseItemId is not null || SourceStockAdjustmentItemId is not null)
                {
                    throw new BusinessRuleException(
                        "inventory.warranty_origin_invalid_sources",
                        "Warranty-origin inventory unit cannot have purchase or stock adjustment source references.");
                }

                if ((SourceWarrantyClaimItemId is null && SourceWarrantyCaseId is null) ||
                    (SourceWarrantyClaimItemId is not null && SourceWarrantyCaseId is not null))
                {
                    throw new BusinessRuleException(
                        "inventory.warranty_origin_missing_source",
                        "Warranty-origin inventory unit requires exactly one warranty source reference.");
                }
                break;

            case InventoryUnitOriginType.StockAdjustment:
                if (SourceStockAdjustmentItemId is null || SourceStockAdjustmentItemId == Guid.Empty)
                {
                    throw new BusinessRuleException(
                        "inventory.stock_adjustment_origin_missing_source",
                        "StockAdjustment-origin inventory unit requires SourceStockAdjustmentItemId.");
                }

                if (SourcePurchaseItemId is not null || SourceWarrantyClaimItemId is not null || SourceWarrantyCaseId is not null)
                {
                    throw new BusinessRuleException(
                        "inventory.stock_adjustment_origin_invalid_sources",
                        "StockAdjustment-origin inventory unit cannot have purchase or warranty source references.");
                }
                break;

            default:
                throw new BusinessRuleException(
                    "inventory.origin_type_invalid",
                    "Invalid inventory unit origin type.");
        }
    }
}

public enum StocktakeStatus
{
    Draft = 1,
    Counting = 2,
    Review = 3,
    Posted = 4,
    Cancelled = 5
}

public enum StocktakeScope
{
    FullShop = 1,
    Category = 2
}

public sealed class Stocktake : Entity
{
    public StocktakeScope Scope { get; set; }
    public Guid? CategoryId { get; set; }
    public StocktakeStatus Status { get; set; } = StocktakeStatus.Draft;
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? ReviewAt { get; set; }
    public DateTimeOffset? PostedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? Note { get; set; }
    public long Version { get; set; }

    public void Start(DateTimeOffset now)
    {
        RequireStatus(StocktakeStatus.Draft);
        Status = StocktakeStatus.Counting;
        StartedAt = now;
        Version++;
    }

    public void MoveToReview(DateTimeOffset now)
    {
        RequireStatus(StocktakeStatus.Counting);
        Status = StocktakeStatus.Review;
        ReviewAt = now;
        Version++;
    }

    public void MarkPosted(DateTimeOffset now)
    {
        RequireStatus(StocktakeStatus.Review);
        Status = StocktakeStatus.Posted;
        PostedAt = now;
        Version++;
    }

    public void Cancel(DateTimeOffset now)
    {
        if (Status is StocktakeStatus.Posted or StocktakeStatus.Cancelled)
        {
            throw new BusinessRuleException(
                "inventory.stocktake_not_cancellable",
                "Posted or cancelled stocktakes cannot be cancelled.");
        }

        Status = StocktakeStatus.Cancelled;
        CancelledAt = now;
        Version++;
    }

    private void RequireStatus(StocktakeStatus required)
    {
        if (Status != required)
        {
            throw new BusinessRuleException(
                "inventory.stocktake_invalid_state",
                $"Stocktake must be {required} for this operation.");
        }
    }
}

public sealed class StocktakeItem : Entity
{
    public Guid StocktakeId { get; set; }
    public Guid ProductId { get; set; }
    public decimal ExpectedSellableQty { get; set; }
    public decimal? CountedSellableQty { get; set; }
    public Guid? CountedBy { get; set; }
    public DateTimeOffset? CountedAt { get; set; }
    public string? ReviewNote { get; set; }

    public decimal VarianceQty =>
        CountedSellableQty is null
            ? 0m
            : QuantityMath.RoundQuantity(CountedSellableQty.Value - ExpectedSellableQty);
}

public enum StocktakeUnitCheckResult
{
    ExpectedAndFound = 1,
    Missing = 2,
    Unexpected = 3,
    WrongStatus = 4
}

public sealed class StocktakeUnitCheck : Entity
{
    public Guid StocktakeItemId { get; set; }
    public Guid? InventoryUnitId { get; set; }
    public string IdentitySnapshot { get; set; } = string.Empty;
    public StocktakeUnitCheckResult Result { get; set; }
    public string? Note { get; set; }
}


public sealed class ProductCostState : Entity
{
    public Guid ProductId { get; set; }
    public decimal CostedQty { get; set; }
    public decimal TotalInventoryCost { get; set; }
    public decimal MovingAverageCost { get; set; }
    public decimal? LastPurchaseCost { get; set; }
    public DateTimeOffset? LastPurchaseAt { get; set; }
    public long Version { get; set; }

    public decimal CalculateRemovalCost(decimal baseQuantity)
    {
        if (baseQuantity <= 0)
        {
            throw new BusinessRuleException(
                "inventory.removal_quantity_positive",
                "Removal quantity must be greater than zero.");
        }

        if (baseQuantity > CostedQty)
        {
            throw new BusinessRuleException(
                "inventory.cost_pool_insufficient",
                "Inventory cost pool does not contain enough quantity.");
        }

        return decimal.Round(baseQuantity * MovingAverageCost, 6, MidpointRounding.AwayFromZero);
    }

    public void RemoveCarryingValue(decimal baseQuantity)
    {
        var cost = CalculateRemovalCost(baseQuantity);
        CostedQty = QuantityMath.RoundQuantity(CostedQty - baseQuantity);
        TotalInventoryCost = decimal.Round(TotalInventoryCost - cost, 6, MidpointRounding.AwayFromZero);
        MovingAverageCost = CostedQty == 0
            ? 0
            : decimal.Round(TotalInventoryCost / CostedQty, 6, MidpointRounding.AwayFromZero);
        Version++;
    }
}

public sealed class InventoryLot : Entity
{
    public Guid ProductId { get; set; }
    public Guid SourceMovementId { get; set; }
    public Guid? PurchaseItemId { get; set; }
    public decimal ReceivedQuantity { get; set; }
    public decimal OriginalUnitCost { get; set; }
    public decimal EffectiveUnitCost { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class InventoryLotBucketBalance : Entity
{
    public Guid LotId { get; set; }
    public InventoryBucket StockBucket { get; set; }
    public decimal Quantity { get; set; }
}

public sealed class InventoryLotConsumption : Entity
{
    public Guid LotId { get; set; }
    public Guid MovementId { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitCostSnapshot { get; set; }
    public decimal TotalCostSnapshot { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class InventoryMovementUnit : Entity
{
    public Guid MovementId { get; set; }
    public Guid InventoryUnitId { get; set; }
    public InventoryUnitStatus? FromStatus { get; set; }
    public InventoryUnitStatus ToStatus { get; set; }
}

public enum StockAdjustmentMode
{
    Delta = 1,
    SetPhysicalCount = 2
}

public enum StockAdjustmentReason
{
    OpeningStock = 1,
    Damaged = 2,
    Lost = 3,
    PhysicalCountCorrection = 4,
    Other = 5
}

public enum StockAdjustmentStatus
{
    Draft = 1,
    Posted = 2,
    Cancelled = 3
}

public enum StockAdjustmentDirection
{
    Increase = 1,
    Decrease = 2
}

public sealed class StockAdjustment : Entity
{
    public string AdjustmentNumber { get; set; } = string.Empty;
    public StockAdjustmentMode Mode { get; set; } = StockAdjustmentMode.Delta;
    public StockAdjustmentReason Reason { get; set; } = StockAdjustmentReason.Other;
    public StockAdjustmentStatus Status { get; set; } = StockAdjustmentStatus.Posted;
    public Guid ActorId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public Guid CorrelationId { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public long Version { get; set; }
}

public sealed class StockAdjustmentItem : Entity
{
    public Guid StockAdjustmentId { get; set; }
    public Guid ProductId { get; set; }
    public Guid? ProductUnitId { get; set; }
    public StockAdjustmentDirection Direction { get; set; } = StockAdjustmentDirection.Increase;
    public InventoryBucket TargetBucket { get; set; } = InventoryBucket.Sellable;
    public decimal BaseQuantity { get; set; }
    public decimal? UnitCostSnapshot { get; set; }
    public decimal? TotalCostSnapshot { get; set; }
    public Guid? SupplierId { get; set; }
    public Guid? SupplierProductId { get; set; }
    public string? ReasonDetails { get; set; }
}

public enum BusinessOwner
{
    Shop = 1,
    Customer = 2,
    ExternalOrHistorical = 3
}

public sealed record InventoryUnitAccountingRule(
    InventoryUnitStatus Status,
    bool ContributesToStockBalance,
    InventoryBucket? AuthoritativeBucket,
    bool ContributesToProductCostState,
    BusinessOwner BusinessOwner,
    bool MayBeSold,
    bool MayBeReturned,
    bool IsTerminal);

public static class InventoryUnitAccountingPolicy
{
    private static readonly IReadOnlyDictionary<InventoryUnitStatus, InventoryUnitAccountingRule> Rules =
        new Dictionary<InventoryUnitStatus, InventoryUnitAccountingRule>
        {
            [InventoryUnitStatus.InStock] = new(
                InventoryUnitStatus.InStock,
                ContributesToStockBalance: true,
                AuthoritativeBucket: InventoryBucket.Sellable,
                ContributesToProductCostState: true,
                BusinessOwner: BusinessOwner.Shop,
                MayBeSold: true,
                MayBeReturned: false,
                IsTerminal: false),

            [InventoryUnitStatus.Sold] = new(
                InventoryUnitStatus.Sold,
                ContributesToStockBalance: false,
                AuthoritativeBucket: null,
                ContributesToProductCostState: false,
                BusinessOwner: BusinessOwner.Customer,
                MayBeSold: false,
                MayBeReturned: true,
                IsTerminal: false),

            [InventoryUnitStatus.IssuedThaka] = new(
                InventoryUnitStatus.IssuedThaka,
                ContributesToStockBalance: false,
                AuthoritativeBucket: null,
                ContributesToProductCostState: true,
                BusinessOwner: BusinessOwner.Shop,
                MayBeSold: false,
                MayBeReturned: true,
                IsTerminal: false),

            [InventoryUnitStatus.Damaged] = new(
                InventoryUnitStatus.Damaged,
                ContributesToStockBalance: true,
                AuthoritativeBucket: InventoryBucket.Damaged,
                ContributesToProductCostState: true,
                BusinessOwner: BusinessOwner.Shop,
                MayBeSold: false,
                MayBeReturned: false,
                IsTerminal: false),

            [InventoryUnitStatus.Defective] = new(
                InventoryUnitStatus.Defective,
                ContributesToStockBalance: true,
                AuthoritativeBucket: InventoryBucket.Defective,
                ContributesToProductCostState: true,
                BusinessOwner: BusinessOwner.Shop,
                MayBeSold: false,
                MayBeReturned: false,
                IsTerminal: false),

            [InventoryUnitStatus.WithSupplier] = new(
                InventoryUnitStatus.WithSupplier,
                ContributesToStockBalance: true,
                AuthoritativeBucket: InventoryBucket.WithSupplier,
                ContributesToProductCostState: true,
                BusinessOwner: BusinessOwner.Shop,
                MayBeSold: false,
                MayBeReturned: false,
                IsTerminal: false),

            [InventoryUnitStatus.SupplierReturned] = new(
                InventoryUnitStatus.SupplierReturned,
                ContributesToStockBalance: false,
                AuthoritativeBucket: null,
                ContributesToProductCostState: false,
                BusinessOwner: BusinessOwner.ExternalOrHistorical,
                MayBeSold: false,
                MayBeReturned: false,
                IsTerminal: true),

            [InventoryUnitStatus.Scrapped] = new(
                InventoryUnitStatus.Scrapped,
                ContributesToStockBalance: true,
                AuthoritativeBucket: InventoryBucket.Scrap,
                ContributesToProductCostState: true,
                BusinessOwner: BusinessOwner.Shop,
                MayBeSold: false,
                MayBeReturned: false,
                IsTerminal: true),

            [InventoryUnitStatus.ReceiptVoided] = new(
                InventoryUnitStatus.ReceiptVoided,
                ContributesToStockBalance: false,
                AuthoritativeBucket: null,
                ContributesToProductCostState: false,
                BusinessOwner: BusinessOwner.ExternalOrHistorical,
                MayBeSold: false,
                MayBeReturned: false,
                IsTerminal: true),

            [InventoryUnitStatus.WarrantyCustomerHeld] = new(
                InventoryUnitStatus.WarrantyCustomerHeld,
                ContributesToStockBalance: false,
                AuthoritativeBucket: null,
                ContributesToProductCostState: false,
                BusinessOwner: BusinessOwner.Customer,
                MayBeSold: false,
                MayBeReturned: false,
                IsTerminal: false),

            [InventoryUnitStatus.WarrantyCustomerHandedOver] = new(
                InventoryUnitStatus.WarrantyCustomerHandedOver,
                ContributesToStockBalance: false,
                AuthoritativeBucket: null,
                ContributesToProductCostState: false,
                BusinessOwner: BusinessOwner.Customer,
                MayBeSold: false,
                MayBeReturned: false,
                IsTerminal: false),
        };

    public static InventoryUnitAccountingRule GetRule(InventoryUnitStatus status)
    {
        if (Rules.TryGetValue(status, out var rule))
        {
            return rule;
        }

        throw new BusinessRuleException(
            "inventory.unknown_unit_status",
            $"No accounting rule defined for InventoryUnitStatus {status}.");
    }

    public static void ValidateAllStatusesCovered()
    {
        foreach (InventoryUnitStatus status in Enum.GetValues<InventoryUnitStatus>())
        {
            if (!Rules.ContainsKey(status))
            {
                throw new InvalidOperationException($"InventoryUnitStatus {status} is missing an explicit accounting rule definition.");
            }
        }
    }
}


