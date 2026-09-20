using EdgeRetails.Domain.Common;

namespace EdgeRetails.Domain.Catalog;

public enum TrackingMode
{
    Quantity = 1,
    Length = 2,
    Serialized = 3
}

public sealed class Unit : Entity
{
    public string Name { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public int DisplayDecimalPlaces { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class Category : Entity
{
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}

public sealed class Product : Entity
{
    public string Name { get; set; } = string.Empty;
    public string? Sku { get; set; }
    public Guid BaseUnitId { get; set; }
    public Guid? CategoryId { get; set; }
    public TrackingMode TrackingMode { get; set; } = TrackingMode.Quantity;
    public bool SerialTrackingEnabled { get; set; }
    public bool ImeiTrackingEnabled { get; set; }
    public decimal? ReferencePurchaseCost { get; set; }
    public decimal DefaultSalePrice { get; set; }
    public bool IsActive { get; set; } = true;
    public long Version { get; set; }

    public void ValidateTrackingPolicy()
    {
        if (TrackingMode == TrackingMode.Serialized && !SerialTrackingEnabled && !ImeiTrackingEnabled)
        {
            throw new BusinessRuleException(
                "catalog.serialized_identity_required",
                "A serialized product must enable serial or IMEI tracking.");
        }
    }
}

public sealed class ProductUnit : Entity
{
    public Guid ProductId { get; set; }
    public Guid UnitId { get; set; }
    public decimal FactorToBaseUnit { get; set; } = 1m;
    public bool CanPurchase { get; set; }
    public bool CanSell { get; set; }
    public bool CanUseInThaka { get; set; }
    public bool IsDefaultPurchaseUnit { get; set; }
    public bool IsDefaultSaleUnit { get; set; }
    public bool IsActive { get; set; } = true;

    public decimal ToBaseQuantity(decimal enteredQuantity, TrackingMode trackingMode)
    {
        if (enteredQuantity <= 0)
        {
            throw new BusinessRuleException(
                "catalog.quantity_positive",
                "Entered quantity must be greater than zero.");
        }

        if (FactorToBaseUnit <= 0)
        {
            throw new BusinessRuleException(
                "catalog.conversion_factor_positive",
                "Unit conversion factor must be greater than zero.");
        }

        var baseQuantity = QuantityMath.RoundQuantity(enteredQuantity * FactorToBaseUnit);

        if (trackingMode == TrackingMode.Serialized && !QuantityMath.IsWhole(baseQuantity))
        {
            throw new BusinessRuleException(
                "catalog.serialized_whole_quantity",
                "Serialized product quantity must resolve to a whole base quantity.");
        }

        return baseQuantity;
    }
}

public sealed class ProductUnitBarcode : Entity
{
    public Guid ProductUnitId { get; set; }
    public string Barcode { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;

    public void Normalize()
    {
        Barcode = Barcode.Trim();
        if (Barcode.Length == 0)
        {
            throw new BusinessRuleException("catalog.barcode_required", "Barcode is required.");
        }
    }
}

public sealed record TransactionQuantitySnapshot(
    Guid ProductUnitId,
    decimal EnteredQuantity,
    decimal FactorToBaseSnapshot,
    decimal BaseQuantity)
{
    public static TransactionQuantitySnapshot Create(
        ProductUnit unit,
        decimal enteredQuantity,
        TrackingMode trackingMode)
    {
        return new TransactionQuantitySnapshot(
            unit.Id,
            QuantityMath.RoundQuantity(enteredQuantity),
            QuantityMath.RoundFactor(unit.FactorToBaseUnit),
            unit.ToBaseQuantity(enteredQuantity, trackingMode));
    }
}
