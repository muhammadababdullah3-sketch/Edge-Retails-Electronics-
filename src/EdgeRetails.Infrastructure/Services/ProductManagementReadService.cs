using EdgeRetails.Application.Features.Catalog;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Infrastructure.Services;

public sealed class ProductManagementReadService : IProductManagementReadService, IProductCatalogSafetyReadService
{
    private readonly EdgeRetailsDbContext _db;

    public ProductManagementReadService(EdgeRetailsDbContext db)
    {
        _db = db;
    }

    public Task<IReadOnlyList<ProductManagementRowDto>> GetProductsAsync(
        bool includeInactive,
        CancellationToken cancellationToken) =>
        GetProductsPageAsync(
            new ProductManagementPageQuery(includeInactive),
            cancellationToken);

    public async Task<IReadOnlyList<ProductManagementRowDto>> GetProductsPageAsync(
        ProductManagementPageQuery request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = _db.Products.AsNoTracking();

        if (!request.IncludeInactive)
        {
            query = query.Where(x => x.IsActive);
        }

        if (request.IsActive is bool isActive)
        {
            query = query.Where(x => x.IsActive == isActive);
        }

        if (request.CategoryId is Guid categoryId)
        {
            query = query.Where(x => x.CategoryId == categoryId);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            query = query.Where(x =>
                EF.Functions.ILike(x.Name, "%" + term + "%") ||
                (x.Sku != null && EF.Functions.ILike(x.Sku, "%" + term + "%")));
        }

        if (!string.IsNullOrWhiteSpace(request.BeforeName) &&
            request.BeforeProductId is Guid beforeProductId)
        {
            query = query.Where(x =>
                x.Name.CompareTo(request.BeforeName) < 0 ||
                (x.Name == request.BeforeName && x.Id.CompareTo(beforeProductId) < 0));
        }

        var take = Math.Clamp(request.PageSize, 1, 200);
        var products = await query
            .OrderBy(x => x.Name)
            .ThenBy(x => x.Id)
            .Take(take)
            .ToListAsync(cancellationToken);

        return await ProjectAsync(products, cancellationToken);
    }

    public async Task<ProductManagementRowDto?> GetProductBySkuAsync(
        string sku,
        CancellationToken cancellationToken)
    {
        var normalizedSku = sku.Trim();
        if (normalizedSku.Length == 0)
        {
            return null;
        }

        var product = await _db.Products
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Sku == normalizedSku, cancellationToken);

        return product is null
            ? null
            : (await ProjectAsync([product], cancellationToken)).Single();
    }

    public async Task<ProductManagementRowDto?> GetProductAsync(
        Guid productId,
        CancellationToken cancellationToken)
    {
        var product = await _db.Products
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == productId, cancellationToken);

        if (product is null)
        {
            return null;
        }

        return (await ProjectAsync([product], cancellationToken)).Single();
    }

    public async Task<IReadOnlyList<CatalogCategoryDto>> GetCategoriesAsync(
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        var query = _db.Categories.AsNoTracking();
        if (!includeInactive)
        {
            query = query.Where(x => x.IsActive);
        }

        return await query
            .OrderBy(x => x.Name)
            .Select(x => new CatalogCategoryDto(x.Id, x.Name, x.IsActive))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CatalogUnitDto>> GetUnitsAsync(
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        var query = _db.Units.AsNoTracking();
        if (!includeInactive)
        {
            query = query.Where(x => x.IsActive);
        }

        return await query
            .OrderBy(x => x.Name)
            .Select(x => new CatalogUnitDto(
                x.Id,
                x.Name,
                x.Symbol,
                x.DisplayDecimalPlaces,
                x.IsActive))
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> HasStockOrHistoryAsync(
        Guid productId,
        CancellationToken cancellationToken)
    {
        if (await _db.StockBalances.AsNoTracking().AnyAsync(
                x => x.ProductId == productId &&
                     (x.SellableQty != 0m ||
                      x.DamagedQty != 0m ||
                      x.DefectiveQty != 0m ||
                      x.WithSupplierQty != 0m ||
                      x.ScrapQty != 0m),
                cancellationToken))
        {
            return true;
        }

        return await _db.InventoryMovements.AsNoTracking()
                   .AnyAsync(x => x.ProductId == productId, cancellationToken) ||
               await _db.InventoryUnits.AsNoTracking()
                   .AnyAsync(x => x.ProductId == productId, cancellationToken) ||
               await _db.InventoryLots.AsNoTracking()
                   .AnyAsync(x => x.ProductId == productId, cancellationToken);
    }

    private async Task<IReadOnlyList<ProductManagementRowDto>> ProjectAsync(
        IReadOnlyList<EdgeRetails.Domain.Catalog.Product> products,
        CancellationToken cancellationToken)
    {
        if (products.Count == 0)
        {
            return Array.Empty<ProductManagementRowDto>();
        }

        var productIds = products.Select(x => x.Id).ToArray();
        var categoryIds = products
            .Where(x => x.CategoryId.HasValue)
            .Select(x => x.CategoryId!.Value)
            .Distinct()
            .ToArray();
        var unitIds = products.Select(x => x.BaseUnitId).Distinct().ToArray();

        var categories = await _db.Categories
            .AsNoTracking()
            .Where(x => categoryIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var units = await _db.Units
            .AsNoTracking()
            .Where(x => unitIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);

        var productUnits = await (
            from productUnit in _db.ProductUnits.AsNoTracking()
            join unit in _db.Units.AsNoTracking() on productUnit.UnitId equals unit.Id
            where productIds.Contains(productUnit.ProductId)
            orderby productUnit.ProductId, unit.Name
            select new
            {
                productUnit.ProductId,
                Dto = new ProductUnitDto(
                    productUnit.Id,
                    unit.Id,
                    unit.Name,
                    unit.Symbol,
                    productUnit.FactorToBaseUnit,
                    productUnit.CanPurchase,
                    productUnit.CanSell,
                    productUnit.CanUseInThaka,
                    productUnit.IsDefaultPurchaseUnit,
                    productUnit.IsDefaultSaleUnit,
                    productUnit.IsActive)
            })
            .ToListAsync(cancellationToken);

        var supplierLinks = await (
            from link in _db.SupplierProducts.AsNoTracking()
            join supplier in _db.Suppliers.AsNoTracking() on link.SupplierId equals supplier.Id
            where productIds.Contains(link.ProductId)
            orderby link.ProductId, supplier.Name
            select new
            {
                link.ProductId,
                Dto = new SupplierProductLinkDto(
                    link.Id,
                    supplier.Id,
                    supplier.Name,
                    link.IsActive,
                    link.Version)
            })
            .ToListAsync(cancellationToken);

        var productUnitsByProduct = productUnits
            .GroupBy(x => x.ProductId)
            .ToDictionary(x => x.Key, x => (IReadOnlyList<ProductUnitDto>)x.Select(y => y.Dto).ToArray());
        var supplierLinksByProduct = supplierLinks
            .GroupBy(x => x.ProductId)
            .ToDictionary(x => x.Key, x => (IReadOnlyList<SupplierProductLinkDto>)x.Select(y => y.Dto).ToArray());

        return products.Select(product =>
        {
            productUnitsByProduct.TryGetValue(product.Id, out var mappedUnits);
            supplierLinksByProduct.TryGetValue(product.Id, out var mappedLinks);
            var category = product.CategoryId is Guid categoryId &&
                           categories.TryGetValue(categoryId, out var categoryEntity)
                ? categoryEntity.Name
                : "Uncategorized";
            var baseUnit = units.TryGetValue(product.BaseUnitId, out var baseUnitEntity)
                ? baseUnitEntity.Symbol
                : "â€”";

            return new ProductManagementRowDto(
                product.Id,
                product.Sku ?? string.Empty,
                product.Name,
                product.Brand,
                product.Model,
                product.CategoryId,
                category,
                product.BaseUnitId,
                baseUnit,
                product.TrackingMode,
                product.SerialTrackingEnabled,
                product.ImeiTrackingEnabled,
                product.ReferencePurchaseCost,
                product.DefaultSalePrice,
                product.MinimumStockLevel,
                product.DefaultWarrantyMonths,
                product.AttributesJson,
                product.AttributesSchemaVersion,
                product.IsActive,
                product.Version,
                mappedUnits ?? Array.Empty<ProductUnitDto>(),
                mappedLinks ?? Array.Empty<SupplierProductLinkDto>());
        }).ToArray();
    }
}
