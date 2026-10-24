using EdgeRetails.Application.Common;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;

namespace EdgeRetails.Application.Features.Catalog;

public static class ProductUnitValidator
{
    public static Error? ValidateUnitInputs(
        TrackingMode trackingMode,
        Guid baseUnitId,
        IReadOnlyList<ProductUnitInput> units,
        IReadOnlyDictionary<Guid, Unit> unitLookup)
    {
        if (units == null || units.Count == 0)
        {
            return new Error("catalog.units_required", "At least one unit must be specified.");
        }

        var baseInput = units.SingleOrDefault(x => x.UnitId == baseUnitId);
        if (baseInput is null || QuantityMath.RoundFactor(baseInput.FactorToBaseUnit) != 1m)
        {
            return new Error(
                "catalog.base_unit_required",
                "Base unit must exist with conversion factor 1.");
        }

        if (units.Count(x => x.CanPurchase && x.IsDefaultPurchaseUnit) > 1)
        {
            return new Error(
                "catalog.multiple_default_purchase_units",
                "Only one default purchase unit is allowed.");
        }

        if (units.Count(x => x.CanSell && x.IsDefaultSaleUnit) > 1)
        {
            return new Error(
                "catalog.multiple_default_sale_units",
                "Only one default sale unit is allowed.");
        }

        if (units.Any(x => x.IsDefaultPurchaseUnit && !x.CanPurchase) ||
            units.Any(x => x.IsDefaultSaleUnit && !x.CanSell))
        {
            return new Error(
                "catalog.invalid_default_unit",
                "A default unit must be enabled for the corresponding operation.");
        }

        if (!unitLookup.TryGetValue(baseUnitId, out var baseUnit) || !baseUnit.IsActive)
        {
            return new Error("catalog.base_unit_unavailable", "Base unit is unavailable.");
        }

        var baseDef = ResolveDefinition(baseUnit);
        var baseDimension = baseDef?.Dimension ?? baseUnit.Dimension;

        foreach (var input in units)
        {
            if (input.FactorToBaseUnit <= 0)
            {
                return new Error(
                    "catalog.conversion_factor_positive",
                    "Conversion factor must be greater than zero.");
            }

            if (trackingMode == TrackingMode.Container &&
                !QuantityMath.IsWhole(input.FactorToBaseUnit))
            {
                return new Error(
                    "catalog.container_conversion_whole",
                    "Container conversion factor must be an exact whole integer count.");
            }

            if (trackingMode == TrackingMode.Serialized &&
                !QuantityMath.IsWhole(input.FactorToBaseUnit))
            {
                return new Error(
                    "catalog.serialized_conversion_whole",
                    "Serialized product unit factors must resolve to whole units.");
            }

            if (!unitLookup.TryGetValue(input.UnitId, out var unit) || !unit.IsActive)
            {
                return new Error(
                    "catalog.unit_unavailable",
                    "One of the selected units is unavailable.");
            }

            var unitDef = ResolveDefinition(unit);
            var unitDimension = unitDef?.Dimension ?? unit.Dimension;

            // Gate D validation for mass / volume / area stock usage
            if (unitDef != null && !unitDef.IsActiveSupported)
            {
                if (DimensionGatePolicy.GatedDimensions.Contains(unitDimension))
                {
                    return new Error(
                        "catalog.dimension_not_supported_gate_d",
                        $"Physical dimension '{unitDimension}' is gated under Gate D policy until live precision and valuation authority is certified.");
                }
            }

            // Cross-dimension check
            if (unitDimension != PhysicalDimension.Any &&
                baseDimension != PhysicalDimension.Any &&
                unitDimension != baseDimension)
            {
                return new Error(
                    "catalog.cross_dimension_conversion_forbidden",
                    $"Cross-dimension conversion from '{unitDimension}' to '{baseDimension}' is prohibited. Conversions must occur strictly within the same physical dimension.");
            }

            // Fixed conversion factor enforcement
            if (unitDef != null && baseDef != null)
            {
                if (IsFixedConversionKind(unitDef.ConversionKind) && IsFixedConversionKind(baseDef.ConversionKind))
                {
                    if (unitDef.Dimension == baseDef.Dimension && baseDef.FactorToReference > 0m)
                    {
                        var expectedFactor = QuantityMath.RoundFactor(unitDef.FactorToReference / baseDef.FactorToReference);
                        var actualFactor = QuantityMath.RoundFactor(input.FactorToBaseUnit);

                        if (Math.Abs(actualFactor - expectedFactor) > 0.000001m)
                        {
                            return new Error(
                                "catalog.fixed_conversion_factor_mismatch",
                                $"Fixed conversion factor for '{unitDef.LocalCode}' against '{baseDef.LocalCode}' must be {expectedFactor}. (Provided: {input.FactorToBaseUnit})");
                        }
                    }
                }
            }
        }

        return null;
    }

    private static bool IsFixedConversionKind(string? kind) =>
        kind is "FIXED" or "FIXED_TO_M" or "FIXED_TO_G" or "FIXED_TO_M2";

    public static UnitDefinition? ResolveDefinition(Unit unit)
    {
        var match = TaxonomyV3Store.UnitDefinitions.FirstOrDefault(u =>
            string.Equals(u.LocalCode, unit.Symbol, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(u.Name, unit.Name, StringComparison.OrdinalIgnoreCase));

        return match;
    }
}
