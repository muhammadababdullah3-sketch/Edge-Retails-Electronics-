using EdgeRetails.Application.Abstractions;
using EdgeRetails.Domain.Catalog;

namespace EdgeRetails.Application.Features.Catalog;

/// <summary>
/// Idempotent preload of Taxonomy V3 operational units and department-backed shop categories.
/// Used at first setup and during explicit catalog reference upgrades for existing shops.
/// </summary>
public static class CatalogTaxonomyBootstrap
{
    public sealed record SeedSummary(int UnitsAdded, int CategoriesAdded);

    public static async Task<SeedSummary> SeedMissingReferenceDataAsync(
        ICatalogRepository catalog,
        CancellationToken cancellationToken)
    {
        var unitsAdded = await SeedMissingUnitsAsync(catalog, cancellationToken);
        var categoriesAdded = await SeedMissingDepartmentCategoriesAsync(catalog, cancellationToken);
        return new SeedSummary(unitsAdded, categoriesAdded);
    }

    public static async Task<int> SeedMissingUnitsAsync(
        ICatalogRepository catalog,
        CancellationToken cancellationToken)
    {
        var existingUnits = await catalog.GetUnitsAsync(includeInactive: true, cancellationToken);
        var existingUnitSymbols = new HashSet<string>(
            existingUnits.Select(u => u.Symbol),
            StringComparer.OrdinalIgnoreCase);
        var existingUnitNames = new HashSet<string>(
            existingUnits.Select(u => u.Name),
            StringComparer.OrdinalIgnoreCase);
        var added = 0;

        foreach (var def in TaxonomyV3Store.UnitDefinitions)
        {
            if (!existingUnitSymbols.Contains(def.LocalCode) && !existingUnitNames.Contains(def.Name))
            {
                catalog.AddUnit(new Unit
                {
                    Name = def.Name,
                    Symbol = def.LocalCode,
                    Dimension = def.Dimension,
                    FactorToReference = def.FactorToReference,
                    ConversionKind = def.ConversionKind,
                    DisplayDecimalPlaces = def.Dimension == PhysicalDimension.Count ? 0 : 3,
                    IsActive = def.IsActiveSupported
                });
                existingUnitSymbols.Add(def.LocalCode);
                existingUnitNames.Add(def.Name);
                added++;
            }
        }

        return added;
    }

    public static async Task<int> SeedMissingDepartmentCategoriesAsync(
        ICatalogRepository catalog,
        CancellationToken cancellationToken)
    {
        var existingCategories = await catalog.GetCategoriesAsync(includeInactive: true, cancellationToken);
        var existingCategorySymbols = new HashSet<string>(
            existingCategories.Select(c => c.IdentitySymbol),
            StringComparer.OrdinalIgnoreCase);
        var existingCategoryNames = new HashSet<string>(
            existingCategories.Select(c => c.Name),
            StringComparer.OrdinalIgnoreCase);
        var added = 0;

        foreach (var dept in TaxonomyV3Store.Departments)
        {
            var symbol = dept.Code.ToUpperInvariant();
            if (!existingCategorySymbols.Contains(symbol) && !existingCategoryNames.Contains(dept.Name))
            {
                catalog.AddCategory(new Category
                {
                    Name = dept.Name,
                    IdentitySymbol = symbol,
                    IsActive = true,
                    Version = 1
                });
                existingCategorySymbols.Add(symbol);
                existingCategoryNames.Add(dept.Name);
                added++;
            }
        }

        return added;
    }
}
