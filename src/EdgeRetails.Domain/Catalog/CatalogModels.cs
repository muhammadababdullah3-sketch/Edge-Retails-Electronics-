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
    public string? Brand { get; set; }
    public string? Model { get; set; }
    public Guid BaseUnitId { get; set; }
    public Guid? CategoryId { get; set; }
    public TrackingMode TrackingMode { get; set; } = TrackingMode.Quantity;
    public bool SerialTrackingEnabled { get; set; }
    public bool ImeiTrackingEnabled { get; set; }
    public decimal? ReferencePurchaseCost { get; set; }
    public decimal DefaultSalePrice { get; set; }
    public decimal MinimumStockLevel { get; set; }
    public int DefaultWarrantyMonths { get; set; }
    public string? AttributesJson { get; set; }
    public int AttributesSchemaVersion { get; set; } = 1;
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

    public void ValidateAttributes()
    {
        AttributesPolicy.Validate(AttributesJson, AttributesSchemaVersion);
    }
}

public static class AttributesPolicy
{
    public const int DefaultSchemaVersion = 1;

    public static void Validate(string? attributesJson, int schemaVersion)
    {
        if (schemaVersion < 1)
        {
            throw new BusinessRuleException(
                "catalog.attributes_schema_version_invalid",
                "Attributes schema version must be greater than or equal to 1.");
        }

        if (string.IsNullOrWhiteSpace(attributesJson))
        {
            return;
        }

        var trimmed = attributesJson.Trim();
        if (!trimmed.StartsWith('{') || !trimmed.EndsWith('}'))
        {
            throw new BusinessRuleException(
                "catalog.attributes_json_invalid",
                "Attributes JSON must be a valid JSON object structure.");
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                throw new BusinessRuleException(
                    "catalog.attributes_json_invalid",
                    "Attributes JSON root element must be an object.");
            }
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new BusinessRuleException(
                "catalog.attributes_json_malformed",
                $"Attributes JSON is malformed: {ex.Message}");
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

        var exactBaseQuantity = enteredQuantity * FactorToBaseUnit;

        if (trackingMode == TrackingMode.Serialized && !QuantityMath.IsWhole(exactBaseQuantity))
        {
            throw new BusinessRuleException(
                "catalog.serialized_whole_quantity",
                "Serialized product quantity must resolve to an exact whole base quantity before rounding.");
        }

        return trackingMode == TrackingMode.Serialized
            ? exactBaseQuantity
            : QuantityMath.RoundQuantity(exactBaseQuantity);
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
