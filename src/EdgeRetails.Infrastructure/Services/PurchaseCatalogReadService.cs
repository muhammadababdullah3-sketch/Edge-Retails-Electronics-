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
                (product.TrackingMode == TrackingMode.Serialized || product.TrackingMode == TrackingMode.IndividualPiece || product.TrackingMode == TrackingMode.Container),
                product.SerialTrackingEnabled,
                product.ImeiTrackingEnabled,
                product.TrackingMode))
            .ToListAsync(cancellationToken);
    }

    public async Task<PurchaseCatalogPageDto> SearchAsync(PurchaseCatalogPageQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.AfterName) != !request.AfterProductId.HasValue)
        {
            throw new ArgumentException("Product cursor requires both name and ID.");
        }
        var products = _db.Products.AsNoTracking().AsQueryable();
        if (!request.IncludeInactive)
        {
            products = products.Where(x => x.IsActive);
        }
        if (request.ProductId is Guid selected)
        {
            products = products.Where(x => x.Id == selected);
        }
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var pattern = "%" + request.Search.Trim() + "%";
            products = products.Where(x => EF.Functions.ILike(x.Name, pattern) ||
                (x.Sku != null && EF.Functions.ILike(x.Sku, pattern)) ||
                (x.Brand != null && EF.Functions.ILike(x.Brand, pattern)));
        }
        if (request.AfterProductId is Guid cursor)
        {
            products = products.Where(x => x.Name.CompareTo(request.AfterName) > 0 ||
                    (x.Name == request.AfterName && x.Id.CompareTo(cursor) > 0));
        }
        products = products.Where(x => _db.ProductUnits.Any(u => u.ProductId == x.Id &&
            (request.IncludeInactive || (u.IsActive && u.CanPurchase))));
        var take = Math.Clamp(request.PageSize, 1, 200);
        var page = await products.OrderBy(x => x.Name).ThenBy(x => x.Id).Take(take + 1)
            .Select(x => new { x.Id, x.Name }).ToListAsync(cancellationToken);
        var hasMore = page.Count > take;
        var visible = page.Take(take).ToArray();
        var ids = visible.Select(x => x.Id).ToArray();
        // Page Product rows first: alternate purchase UOMs must never split a cursor.
        var items = await (from product in _db.Products.AsNoTracking()
            join productUnit in _db.ProductUnits.AsNoTracking() on product.Id equals productUnit.ProductId
            join unit in _db.Units.AsNoTracking() on productUnit.UnitId equals unit.Id
            join stockJoin in _db.StockBalances.AsNoTracking() on product.Id equals stockJoin.ProductId into stocks
            from stock in stocks.DefaultIfEmpty()
            join costJoin in _db.ProductCostStates.AsNoTracking() on product.Id equals costJoin.ProductId into costs
            from cost in costs.DefaultIfEmpty()
            join categoryJoin in _db.Categories.AsNoTracking() on product.CategoryId equals categoryJoin.Id into categories
            from category in categories.DefaultIfEmpty()
            where ids.Contains(product.Id) && (request.IncludeInactive || (productUnit.IsActive && productUnit.CanPurchase && unit.IsActive))
            orderby product.Name, product.Id, productUnit.Id
            select new PurchaseCatalogProductDto(product.Id, productUnit.Id, product.Name, product.Sku,
                category == null ? "Uncategorized" : category.Name, unit.Symbol,
                stock == null ? 0m : stock.SellableQty,
                product.ReferencePurchaseCost ?? (cost == null ? 0m : cost.MovingAverageCost),
                product.DefaultSalePrice, productUnit.FactorToBaseUnit,
                product.TrackingMode == TrackingMode.Serialized || product.TrackingMode == TrackingMode.IndividualPiece || product.TrackingMode == TrackingMode.Container,
                product.SerialTrackingEnabled, product.ImeiTrackingEnabled, product.TrackingMode)).ToListAsync(cancellationToken);
        return new PurchaseCatalogPageDto(items, hasMore ? visible[^1].Name : null, hasMore ? visible[^1].Id : null);
    }
}
