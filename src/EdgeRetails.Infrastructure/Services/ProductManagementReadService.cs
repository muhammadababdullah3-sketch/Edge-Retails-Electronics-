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
                (x.Sku != null && EF.Functions.ILike(x.Sku, "%" + term + "%")) ||
                (x.Brand != null && EF.Functions.ILike(x.Brand, "%" + term + "%")) ||
                (x.Model != null && EF.Functions.ILike(x.Model, "%" + term + "%")));
        }

        if (!string.IsNullOrWhiteSpace(request.BeforeName) &&
            request.BeforeProductId is Guid beforeProductId)
        {
            query = query.Where(x =>
                x.Name.CompareTo(request.BeforeName) > 0 ||
                (x.Name == request.BeforeName && x.Id.CompareTo(beforeProductId) > 0));
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

    public async Task<IReadOnlyList<CatalogCompanyDto>> GetCompaniesAsync(
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        var query = _db.Companies.AsNoTracking();
        if (!includeInactive)
        {
            query = query.Where(x => x.IsActive);
        }

        return await query
            .OrderBy(x => x.Name)
            .Select(x => new CatalogCompanyDto(x.Id, x.Name, x.Code, x.IsActive))
            .ToListAsync(cancellationToken);
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
            .Select(x => new CatalogCategoryDto(x.Id, x.Name, x.IdentitySymbol, x.IsActive))
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
                   .AnyAsync(x => x.ProductId == productId, cancellationToken) ||
               await _db.SupplierProducts.AsNoTracking()
                   .AnyAsync(x => x.ProductId == productId, cancellationToken) ||
               await _db.PurchaseItems.AsNoTracking()
                   .AnyAsync(x => x.ProductId == productId, cancellationToken) ||
               await _db.SaleItems.AsNoTracking()
                   .AnyAsync(x => x.ProductId == productId, cancellationToken) ||
               await _db.PurchaseReturnItems.AsNoTracking()
                   .AnyAsync(x => x.ProductId == productId, cancellationToken) ||
               await _db.SaleReturnItems.AsNoTracking()
                   .AnyAsync(x => x.ProductId == productId, cancellationToken) ||
               await _db.WarrantyClaimItems.AsNoTracking()
                   .AnyAsync(x => x.ProductId == productId, cancellationToken) ||
               await _db.ShopStockWarrantyCases.AsNoTracking()
                   .AnyAsync(x => x.ProductId == productId, cancellationToken);
    }

    public async Task<bool> HasUnitUsageAsync(
        Guid productUnitId,
        CancellationToken cancellationToken)
    {
        return await _db.PurchaseItems.AsNoTracking()
                   .AnyAsync(x => x.ProductUnitId == productUnitId, cancellationToken) ||
               await _db.PurchaseReturnItems.AsNoTracking()
                   .AnyAsync(x => x.ProductUnitId == productUnitId, cancellationToken) ||
               await _db.SaleItems.AsNoTracking()
                   .AnyAsync(x => x.ProductUnitId == productUnitId, cancellationToken) ||
               await _db.SaleReturnItems.AsNoTracking()
                   .AnyAsync(x => x.ProductUnitId == productUnitId, cancellationToken) ||
               await _db.ThakaMaterialIssueItems.AsNoTracking()
                   .AnyAsync(x => x.ProductUnitId == productUnitId, cancellationToken) ||
               await _db.StockAdjustmentItems.AsNoTracking()
                   .AnyAsync(x => x.ProductUnitId == productUnitId, cancellationToken);
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
        var companyIds = products
            .Where(x => x.CompanyId.HasValue)
            .Select(x => x.CompanyId!.Value)
            .Distinct()
            .ToArray();
        var unitIds = products.Select(x => x.BaseUnitId).Distinct().ToArray();

        var categories = await _db.Categories
            .AsNoTracking()
            .Where(x => categoryIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var companies = await _db.Companies
            .AsNoTracking()
            .Where(x => companyIds.Contains(x.Id))
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
            var company = product.CompanyId is Guid compId &&
                          companies.TryGetValue(compId, out var companyEntity)
                ? companyEntity
                : null;
            var baseUnit = units.TryGetValue(product.BaseUnitId, out var baseUnitEntity)
                ? baseUnitEntity.Symbol
                : "—";

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
                mappedLinks ?? Array.Empty<SupplierProductLinkDto>(),
                product.CompanyId,
                company?.Name,
                company?.Code,
                product.ModelCode);
        }).ToArray();
    }

    public async Task<CatalogSearchResponseDto> SearchCatalogAsync(
        string? query,
        string? packagingFacet,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken)
    {
        pageNumber = Math.Max(1, pageNumber);
        var take = Math.Clamp(pageSize <= 0 ? 50 : pageSize, 1, 100);
        var skip = (pageNumber - 1) * take;

        string? resolvedAlias = null;
        string? aliasDepartment = null;

        var term = query?.Trim();
        if (!string.IsNullOrWhiteSpace(term))
        {
            var matched = TaxonomyV3Store.SearchAliases.FirstOrDefault(a =>
                string.Equals(a.Keyword, term, StringComparison.OrdinalIgnoreCase));
            if (matched is not null)
            {
                resolvedAlias = matched.Keyword;
                aliasDepartment = matched.TargetRef;
            }
        }

        var baseQuery = (
            from product in _db.Products.AsNoTracking()
            join unit in _db.Units.AsNoTracking() on product.BaseUnitId equals unit.Id into unitGroup
            from u in unitGroup.DefaultIfEmpty()
            join category in _db.Categories.AsNoTracking() on product.CategoryId equals category.Id into catGroup
            from c in catGroup.DefaultIfEmpty()
            where product.IsActive
            select new
            {
                product,
                UnitSymbol = u == null ? "Pcs" : u.Symbol,
                CategoryName = c == null ? "Uncategorized" : c.Name
            });

        if (!string.IsNullOrWhiteSpace(term))
        {
            if (resolvedAlias != null)
            {
                baseQuery = baseQuery.Where(x =>
                    x.product.Name.Contains(term) ||
                    (x.product.Sku != null && x.product.Sku.Contains(term)) ||
                    (x.product.Brand != null && x.product.Brand.Contains(term)) ||
                    (x.CategoryName != null && x.CategoryName.Contains(resolvedAlias)) ||
                    (aliasDepartment != null && x.product.AttributesJson != null && x.product.AttributesJson.Contains(aliasDepartment)) ||
                    (aliasDepartment != null && x.product.Name.Contains(resolvedAlias)) ||
                    (aliasDepartment == "A01" && (x.product.Name.Contains("Wire") || x.product.Name.Contains("Cable") || (x.CategoryName != null && (x.CategoryName.Contains("Wire") || x.CategoryName.Contains("Cable"))))) ||
                    (aliasDepartment == "A03" && (x.product.Name.Contains("Breaker") || x.product.Name.Contains("MCB") || (x.CategoryName != null && x.CategoryName.Contains("Breaker")))) ||
                    (aliasDepartment == "B01" && (x.product.Name.Contains("Fan") || (x.CategoryName != null && x.CategoryName.Contains("Fan")))) ||
                    (aliasDepartment == "B04" && (x.product.Name.Contains("Iron") || (x.CategoryName != null && x.CategoryName.Contains("Iron")))));
            }
            else
            {
                baseQuery = baseQuery.Where(x =>
                    x.product.Name.Contains(term) ||
                    (x.product.Sku != null && x.product.Sku.Contains(term)) ||
                    (x.product.Brand != null && x.product.Brand.Contains(term)) ||
                    (x.CategoryName != null && x.CategoryName.Contains(term)));
            }
        }

        if (!string.IsNullOrWhiteSpace(packagingFacet) && !string.Equals(packagingFacet, "All", StringComparison.OrdinalIgnoreCase))
        {
            var facet = packagingFacet.Trim().ToUpperInvariant();
            switch (facet)
            {
                case "PACK":
                    baseQuery = baseQuery.Where(x =>
                        x.UnitSymbol.Contains("Pack") || x.UnitSymbol == "PK" ||
                        x.product.Name.Contains("Pack") ||
                        (x.product.AttributesJson != null && x.product.AttributesJson.Contains("PACK")));
                    break;
                case "COIL":
                    baseQuery = baseQuery.Where(x =>
                        x.UnitSymbol.Contains("Coil") || x.UnitSymbol == "CL" || x.UnitSymbol == "m" ||
                        x.product.Name.Contains("Coil") || x.product.Name.Contains("Cable") ||
                        (x.product.AttributesJson != null && x.product.AttributesJson.Contains("COIL")));
                    break;
                case "BOX":
                    baseQuery = baseQuery.Where(x =>
                        x.UnitSymbol.Contains("Box") || x.UnitSymbol == "BX" ||
                        x.product.Name.Contains("Box") ||
                        (x.product.AttributesJson != null && x.product.AttributesJson.Contains("BOX")));
                    break;
                case "REEL":
                    baseQuery = baseQuery.Where(x =>
                        x.UnitSymbol.Contains("Reel") || x.UnitSymbol == "RL" ||
                        x.product.Name.Contains("Reel") ||
                        (x.product.AttributesJson != null && x.product.AttributesJson.Contains("REEL")));
                    break;
            }
        }

        var totalCount = await baseQuery.CountAsync(cancellationToken);

        var pagedRows = await baseQuery
            .OrderBy(x => x.product.Name)
            .ThenBy(x => x.product.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        var items = pagedRows.Select(r => new CatalogSearchItemDto(
            ProductId: r.product.Id,
            Sku: r.product.Sku ?? string.Empty,
            Name: r.product.Name,
            Brand: r.product.Brand,
            Category: r.CategoryName,
            BaseUnit: r.UnitSymbol,
            UnitPrice: r.product.DefaultSalePrice,
            ReferencePurchaseCost: r.product.ReferencePurchaseCost,
            PackagingFacet: DeterminePackagingFacet(r.UnitSymbol, r.product.Name, r.product.AttributesJson),
            IsActive: r.product.IsActive)).ToList();

        return new CatalogSearchResponseDto(
            TotalCount: totalCount,
            PageNumber: pageNumber,
            PageSize: take,
            Items: items,
            ResolvedAlias: resolvedAlias,
            PackagingFacet: packagingFacet);
    }

    private static string? DeterminePackagingFacet(string unitSymbol, string productName, string? attributesJson)
    {
        if (unitSymbol.Contains("Pack", StringComparison.OrdinalIgnoreCase) || unitSymbol.Equals("PK", StringComparison.OrdinalIgnoreCase) || productName.Contains("Pack", StringComparison.OrdinalIgnoreCase))
        {
            return "PACK";
        }
        if (unitSymbol.Contains("Coil", StringComparison.OrdinalIgnoreCase) || unitSymbol.Equals("CL", StringComparison.OrdinalIgnoreCase) || unitSymbol.Equals("m", StringComparison.OrdinalIgnoreCase) || productName.Contains("Coil", StringComparison.OrdinalIgnoreCase))
        {
            return "COIL";
        }
        if (unitSymbol.Contains("Box", StringComparison.OrdinalIgnoreCase) || unitSymbol.Equals("BX", StringComparison.OrdinalIgnoreCase) || productName.Contains("Box", StringComparison.OrdinalIgnoreCase))
        {
            return "BOX";
        }
        if (unitSymbol.Contains("Reel", StringComparison.OrdinalIgnoreCase) || unitSymbol.Equals("RL", StringComparison.OrdinalIgnoreCase) || productName.Contains("Reel", StringComparison.OrdinalIgnoreCase))
        {
            return "REEL";
        }
        return null;
    }
}
