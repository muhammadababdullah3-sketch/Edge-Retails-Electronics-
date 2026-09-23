using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Infrastructure.Services;

public sealed class InventoryOverviewReadService : IInventoryOverviewReadService
{
    private readonly EdgeRetailsDbContext _db;

    public InventoryOverviewReadService(EdgeRetailsDbContext db)
    {
        _db = db;
    }

    public Task<IReadOnlyList<InventoryStockRowDto>> GetStockAsync(
        CancellationToken cancellationToken) =>
        GetStockPageAsync(new InventoryStockPageQuery(), cancellationToken);

    public async Task<IReadOnlyList<InventoryStockRowDto>> GetStockPageAsync(
        InventoryStockPageQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var take = Math.Clamp(query.PageSize, 1, 200);
        var search = query.Search?.Trim();
        var category = query.Category?.Trim();
        var brand = query.Brand?.Trim();

        var baseQuery =
            from product in _db.Products.AsNoTracking()
            join unit in _db.Units.AsNoTracking()
                on product.BaseUnitId equals unit.Id
            join stockJoin in _db.StockBalances.AsNoTracking()
                on product.Id equals stockJoin.ProductId into stockGroup
            from stock in stockGroup.DefaultIfEmpty()
            join costJoin in _db.ProductCostStates.AsNoTracking()
                on product.Id equals costJoin.ProductId into costGroup
            from cost in costGroup.DefaultIfEmpty()
            join categoryJoin in _db.Categories.AsNoTracking()
                on product.CategoryId equals categoryJoin.Id into categoryGroup
            from categoryRow in categoryGroup.DefaultIfEmpty()
            where product.IsActive
            select new
            {
                product,
                unit,
                stock,
                cost,
                categoryRow
            };

        if (!string.IsNullOrWhiteSpace(search))
        {
            baseQuery = baseQuery.Where(x =>
                EF.Functions.ILike(x.product.Name, $"%{search}%") ||
                (x.product.Sku != null && EF.Functions.ILike(x.product.Sku, $"%{search}%")) ||
                (x.product.Brand != null && EF.Functions.ILike(x.product.Brand, $"%{search}%")) ||
                (x.product.Model != null && EF.Functions.ILike(x.product.Model, $"%{search}%")));
        }

        if (!string.IsNullOrWhiteSpace(category) &&
            !string.Equals(category, "All", StringComparison.OrdinalIgnoreCase))
        {
            baseQuery = baseQuery.Where(x =>
                x.categoryRow != null && x.categoryRow.Name == category);
        }

        if (!string.IsNullOrWhiteSpace(brand) &&
            !string.Equals(brand, "All", StringComparison.OrdinalIgnoreCase))
        {
            baseQuery = baseQuery.Where(x => x.product.Brand == brand);
        }

        if (!string.IsNullOrWhiteSpace(query.BeforeName) &&
            query.BeforeProductId is Guid beforeProductId)
        {
            baseQuery = baseQuery.Where(x =>
                string.Compare(x.product.Name, query.BeforeName, StringComparison.Ordinal) < 0 ||
                (x.product.Name == query.BeforeName && x.product.Id.CompareTo(beforeProductId) < 0));
        }

        return await baseQuery
            .OrderBy(x => x.product.Name)
            .ThenBy(x => x.product.Id)
            .Take(take)
            .Select(x => new InventoryStockRowDto(
                x.product.Id,
                x.product.Name,
                x.product.Sku,
                x.product.Brand,
                x.product.Model,
                x.categoryRow == null ? "Uncategorized" : x.categoryRow.Name,
                x.unit.Symbol,
                x.stock == null ? 0m : x.stock.SellableQty,
                x.stock == null ? 0m : x.stock.DamagedQty,
                x.stock == null ? 0m : x.stock.DefectiveQty,
                x.stock == null ? 0m : x.stock.WithSupplierQty,
                x.stock == null ? 0m : x.stock.ScrapQty,
                x.cost == null ? 0m : x.cost.MovingAverageCost,
                x.cost == null ? null : x.cost.LastPurchaseCost,
                x.product.DefaultSalePrice,
                x.product.MinimumStockLevel,
                x.product.TrackingMode == TrackingMode.Serialized))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<InventoryMovementRowDto>> GetMovementsAsync(
        int pageSize,
        CancellationToken cancellationToken,
        DateTimeOffset? beforeOccurredAt = null,
        Guid? beforeMovementId = null)
    {
        var take = Math.Clamp(pageSize, 1, 500);

        var query =
            from movement in _db.InventoryMovements.AsNoTracking()
            join product in _db.Products.AsNoTracking()
                on movement.ProductId equals product.Id
            join effect in _db.InventoryMovementEffects.AsNoTracking()
                on movement.Id equals effect.MovementId
            where effect.StockBucket == EdgeRetails.Domain.Inventory.InventoryBucket.Sellable
            select new { movement, product, effect };

        if (beforeOccurredAt is DateTimeOffset cursorAt &&
            beforeMovementId is Guid cursorId)
        {
            query = query.Where(x =>
                x.movement.OccurredAt < cursorAt ||
                (x.movement.OccurredAt == cursorAt && x.movement.Id.CompareTo(cursorId) < 0));
        }

        return await query
            .OrderByDescending(x => x.movement.OccurredAt)
            .ThenByDescending(x => x.movement.Id)
            .Take(take)
            .Select(x => new InventoryMovementRowDto(
                x.movement.Id,
                x.movement.ProductId,
                x.product.Name,
                x.movement.MovementType,
                x.movement.ReferenceType,
                x.movement.ReferenceId,
                x.effect.QuantityDelta,
                x.effect.QuantityBefore,
                x.effect.QuantityAfter,
                x.movement.OccurredAt,
                x.movement.Reason,
                x.movement.Note))
            .ToListAsync(cancellationToken);
    }
}
