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

    public async Task<IReadOnlyList<PosCatalogProductDto>> GetSellableCatalogAsync(
        string? search,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var take = Math.Clamp(pageSize, 1, 200);
        var term = search?.Trim() ?? string.Empty;
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
            from category in categoryGroup.DefaultIfEmpty()
            where product.IsActive &&
                  productUnit.IsActive &&
                  productUnit.CanSell &&
                  productUnit.IsDefaultSaleUnit &&
                  (string.IsNullOrWhiteSpace(term) ||
                   product.Name.Contains(term) ||
                   ((product.Sku ?? string.Empty).Contains(term)) ||
                   _db.ProductUnitBarcodes.Any(barcode =>
                       barcode.ProductUnitId == productUnit.Id &&
                       barcode.Barcode.Contains(term)))
            orderby product.Name, product.Id
            select new PosCatalogProductDto(
                product.Id,
                productUnit.Id,
                product.Name,
                product.Sku,
                category == null ? "Uncategorized" : category.Name!,
                unit.Symbol,
                stock == null ? 0m : stock.SellableQty,
                decimal.Round(
                    product.DefaultSalePrice * productUnit.FactorToBaseUnit,
                    2,
                    MidpointRounding.AwayFromZero),
                product.ReferencePurchaseCost ?? 0m,
                product.TrackingMode == TrackingMode.Serialized))
            .Take(take);

        return await query.ToListAsync(cancellationToken);
    }
}
