using EdgeRetails.Domain.Catalog;

namespace EdgeRetails.Application.Features.Catalog;

/// <summary>
/// Data-approved fixed operational measurement units. This is deliberately distinct from
/// reference-only/product-defined/package/service unit rows in TaxonomyV3Store.UnitDefinitions.
/// Operational categories are intentionally omitted until the owner approves a symbol/name map.
/// </summary>
public static class CatalogOperationalReferenceSeed
{
    public sealed record FixedUnit(string Symbol, string Name, PhysicalDimension Dimension, decimal FactorToReference, string ConversionKind, int DecimalPlaces, bool IsActiveSupported);

    public static IReadOnlyList<FixedUnit> FixedUnits { get; } = Array.AsReadOnly(new[]
    {
        new FixedUnit("PCS", "Piece", PhysicalDimension.Count, 1m, "FIXED", 0, true),
        new FixedUnit("DOZ", "Dozen", PhysicalDimension.Count, 12m, "FIXED", 0, true),
        new FixedUnit("HDOZ", "Half Dozen", PhysicalDimension.Count, 6m, "FIXED", 0, true),
        new FixedUnit("MM", "Millimeter", PhysicalDimension.Length, 0.001m, "FIXED_TO_M", 3, true),
        new FixedUnit("CM", "Centimeter", PhysicalDimension.Length, 0.01m, "FIXED_TO_M", 3, true),
        new FixedUnit("M", "Meter", PhysicalDimension.Length, 1m, "FIXED_TO_M", 3, true),
        new FixedUnit("KM", "Kilometer", PhysicalDimension.Length, 1000m, "FIXED_TO_M", 3, true),
        new FixedUnit("IN", "Inch", PhysicalDimension.Length, 0.0254m, "FIXED_TO_M", 3, true),
        new FixedUnit("FT", "Foot", PhysicalDimension.Length, 0.3048m, "FIXED_TO_M", 3, true),
        new FixedUnit("YD", "Yard", PhysicalDimension.Length, 0.9144m, "FIXED_TO_M", 3, true),
        new FixedUnit("PAIR", "Pair", PhysicalDimension.Count, 1m, "PRODUCT_DEFINED", 0, false),
        new FixedUnit("SET", "Set", PhysicalDimension.Count, 1m, "PRODUCT_DEFINED", 0, false),
        new FixedUnit("KIT", "Kit", PhysicalDimension.Count, 1m, "NOT_AUTO", 0, false)
    });
}
