using EdgeRetails.Domain.Catalog;

namespace EdgeRetails.Application.Features.Catalog;

public sealed record CatalogCategoryDto(Guid Id, string Name, bool IsActive);

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
    IReadOnlyList<SupplierProductLinkDto> SupplierProducts);

public sealed record ProductManagementPageQuery(
    bool IncludeInactive = false,
    string? Search = null,
    int PageSize = 200,
    string? BeforeName = null,
    Guid? BeforeProductId = null,
    Guid? CategoryId = null,
    bool? IsActive = null);

public interface IProductManagementReadService
{
    Task<IReadOnlyList<ProductManagementRowDto>> GetProductsAsync(
        bool includeInactive,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ProductManagementRowDto>> GetProductsPageAsync(
        ProductManagementPageQuery query,
        CancellationToken cancellationToken);

    Task<ProductManagementRowDto?> GetProductAsync(
        Guid productId,
        CancellationToken cancellationToken);

    Task<ProductManagementRowDto?> GetProductBySkuAsync(
        string sku,
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
}
