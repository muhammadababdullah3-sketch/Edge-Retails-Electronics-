using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Infrastructure.Services;

public sealed class PurchaseCatalogReadService : IPurchaseCatalogReadService
{
    private readonly EdgeRetailsDbContext _db;

    public PurchaseCatalogReadService(EdgeRetailsDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<PurchaseCatalogProductDto>> GetPurchasableCatalogAsync(
        CancellationToken cancellationToken)
    {
        return await (
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
                  productUnit.CanPurchase &&
                  productUnit.IsDefaultPurchaseUnit
            orderby product.Name, product.Id
            select new PurchaseCatalogProductDto(
                product.Id,
                productUnit.Id,
                product.Name,
                product.Sku,
                category == null ? "Uncategorized" : category.Name,
                unit.Symbol,
                stock == null ? 0m : stock.SellableQty,
                product.ReferencePurchaseCost ?? 0m,
                product.DefaultSalePrice,
                productUnit.FactorToBaseUnit,
                product.TrackingMode == TrackingMode.Serialized,
                product.SerialTrackingEnabled,
                product.ImeiTrackingEnabled))
            .ToListAsync(cancellationToken);
    }
}
