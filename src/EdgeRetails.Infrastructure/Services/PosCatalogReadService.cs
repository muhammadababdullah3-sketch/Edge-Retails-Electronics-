using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Infrastructure.Services;

public sealed class PosCatalogReadService : IPosCatalogReadService
{
    private readonly EdgeRetailsDbContext _db;

    public PosCatalogReadService(EdgeRetailsDbContext db)
    {
        _db = db;
    }

    public Task<IReadOnlyList<PosCatalogProductDto>> GetSellableCatalogAsync(
        string? search,
        int pageSize,
        CancellationToken cancellationToken) =>
        GetSellableCatalogAsync(search, null, null, pageSize, null, null, cancellationToken);

    public async Task<IReadOnlyList<PosCatalogProductDto>> GetSellableCatalogAsync(
        string? search,
        string? category,
        string? brand,
        int pageSize,
        string? afterName,
        Guid? afterId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(afterName) != !afterId.HasValue)
        {
            throw new ArgumentException("Both afterName and afterId are required for a catalog cursor.");
        }

        var take = Math.Clamp(pageSize, 1, 500);
        var term = search?.Trim() ?? string.Empty;
        var catFilter = category?.Trim();
        var brandFilter = brand?.Trim();

        var query = (
            from product in _db.Products.AsNoTracking()
            join productUnit in _db.ProductUnits.AsNoTracking()
                on product.Id equals productUnit.ProductId
            join unit in _db.Units.AsNoTracking()
                on productUnit.UnitId equals unit.Id
            join stockJoin in _db.StockBalances.AsNoTracking()
                on product.Id equals stockJoin.ProductId into stockGroup
            from stock in stockGroup.DefaultIfEmpty()
            join categoryJoin in _db.Categories.AsNoTracking()
                on product.CategoryId equals categoryJoin.Id into categoryGroup
            from cat in categoryGroup.DefaultIfEmpty()
            where product.IsActive &&
                  productUnit.IsActive &&
                  productUnit.CanSell &&
                  productUnit.IsDefaultSaleUnit &&
                  (string.IsNullOrWhiteSpace(term) ||
                   product.Name.Contains(term) ||
                   ((product.Sku ?? string.Empty).Contains(term)) ||
                   _db.ProductUnitBarcodes.Any(barcode =>
                       barcode.ProductUnitId == productUnit.Id &&
                       barcode.Barcode.Contains(term))) &&
                  (string.IsNullOrWhiteSpace(catFilter) ||
                   catFilter == "All" ||
                   (catFilter == "Uncategorized" ? cat == null : cat != null && cat.Name == catFilter)) &&
                  (string.IsNullOrWhiteSpace(brandFilter) ||
                   brandFilter == "All" ||
                   (brandFilter == "Unbranded" ? (product.Brand == null || product.Brand == "") : product.Brand == brandFilter))
            select new { product, productUnit, unit, stock, cat });

        if (!string.IsNullOrEmpty(afterName) && afterId.HasValue)
        {
            query = query.Where(x =>
                string.Compare(x.product.Name, afterName) > 0 ||
                (x.product.Name == afterName && x.product.Id.CompareTo(afterId.Value) > 0));
        }

        var resultQuery = query
            .OrderBy(x => x.product.Name)
            .ThenBy(x => x.product.Id)
            .Select(x => new PosCatalogProductDto(
                x.product.Id,
                x.productUnit.Id,
                x.product.Name,
                x.product.Sku,
                x.cat == null ? "Uncategorized" : x.cat.Name!,
                x.unit.Symbol,
                x.stock == null ? 0m : x.stock.SellableQty,
                decimal.Round(
                    x.product.DefaultSalePrice * x.productUnit.FactorToBaseUnit,
                    2,
                    MidpointRounding.AwayFromZero),
                x.product.ReferencePurchaseCost ?? 0m,
                (x.product.TrackingMode == TrackingMode.Serialized || x.product.TrackingMode == TrackingMode.IndividualPiece || x.product.TrackingMode == TrackingMode.Container),
                string.IsNullOrWhiteSpace(x.product.Brand) ? null : x.product.Brand))
            .Take(take);

        return await resultQuery.ToListAsync(cancellationToken);
    }
}
