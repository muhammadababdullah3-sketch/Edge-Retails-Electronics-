using EdgeRetails.Domain.Catalog;

namespace EdgeRetails.Application.Features.Catalog;

public sealed record CatalogCategoryDto(Guid Id, string Name, string IdentitySymbol = "", bool IsActive = true);

public sealed record CatalogCompanyDto(Guid Id, string Name, string Code, bool IsActive);

public sealed record CatalogUnitDto(
    Guid Id,
    string Name,
    string Symbol,
    int DisplayDecimalPlaces,
    bool IsActive);

public sealed record ProductUnitDto(
    Guid ProductUnitId,
    Guid UnitId,
    string UnitName,
    string UnitSymbol,
    decimal FactorToBaseUnit,
    bool CanPurchase,
    bool CanSell,
    bool CanUseInThaka,
    bool IsDefaultPurchaseUnit,
    bool IsDefaultSaleUnit,
    bool IsActive);

public sealed record SupplierProductLinkDto(
    Guid SupplierProductId,
    Guid SupplierId,
    string SupplierName,
    bool IsActive,
    long Version);

public sealed record ProductManagementRowDto(
    Guid ProductId,
    string Sku,
    string Name,
    string? Brand,
    string? Model,
    Guid? CategoryId,
    string Category,
    Guid BaseUnitId,
    string BaseUnit,
    TrackingMode TrackingMode,
    bool SerialTrackingEnabled,
    bool ImeiTrackingEnabled,
    decimal? ReferencePurchaseCost,
    decimal DefaultSalePrice,
    decimal MinimumStockLevel,
    int DefaultWarrantyMonths,
    string? AttributesJson,
    int AttributesSchemaVersion,
    bool IsActive,
    long Version,
    IReadOnlyList<ProductUnitDto> ProductUnits,
    IReadOnlyList<SupplierProductLinkDto> SupplierProducts,
    Guid? CompanyId = null,
    string? Company = null,
    string? CompanyCode = null,
    string? ModelCode = null,
    string? ClassRef = null)
{
    public decimal? CostPrice => ReferencePurchaseCost;
    public decimal UnitPrice => DefaultSalePrice;
}

public sealed record ProductManagementPageQuery(
    bool IncludeInactive = false,
    string? Search = null,
    int PageSize = 200,
    string? BeforeName = null,
    Guid? BeforeProductId = null,
    Guid? CategoryId = null,
    bool? IsActive = null,
    string? ClassRef = null);

public sealed record CatalogSearchItemDto(
    Guid ProductId,
    string Sku,
    string Name,
    string? Brand,
    string Category,
    string BaseUnit,
    decimal UnitPrice,
    decimal? ReferencePurchaseCost,
    string? PackagingFacet,
    bool IsActive);

public sealed record CatalogSearchResponseDto(
    int TotalCount,
    int PageNumber,
    int PageSize,
    IReadOnlyList<CatalogSearchItemDto> Items,
    string? ResolvedAlias = null,
    string? PackagingFacet = null);

public interface IProductManagementReadService
{
    Task<IReadOnlyList<ProductManagementRowDto>> GetProductsAsync(
        bool includeInactive,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ProductManagementRowDto>> GetProductsPageAsync(
        ProductManagementPageQuery query,
        CancellationToken cancellationToken);

    Task<CatalogSearchResponseDto> SearchCatalogAsync(
        string? query,
        string? packagingFacet,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken) =>
        Task.FromResult(new CatalogSearchResponseDto(0, pageNumber, pageSize, Array.Empty<CatalogSearchItemDto>()));

    Task<ProductManagementRowDto?> GetProductAsync(
        Guid productId,
        CancellationToken cancellationToken);

    Task<ProductManagementRowDto?> GetProductBySkuAsync(
        string sku,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CatalogCompanyDto>> GetCompaniesAsync(
        bool includeInactive,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CatalogCategoryDto>> GetCategoriesAsync(
        bool includeInactive,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CatalogUnitDto>> GetUnitsAsync(
        bool includeInactive,
        CancellationToken cancellationToken);
}


public interface IProductCatalogSafetyReadService
{
    Task<bool> HasStockOrHistoryAsync(
        Guid productId,
        CancellationToken cancellationToken);

    Task<bool> HasUnitUsageAsync(
        Guid productUnitId,
        CancellationToken cancellationToken) => Task.FromResult(false);
}
