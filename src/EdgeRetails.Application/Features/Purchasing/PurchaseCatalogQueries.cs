namespace EdgeRetails.Application.Features.Purchasing;

public sealed record PurchaseCatalogProductDto(
    Guid ProductId,
    Guid ProductUnitId,
    string Name,
    string? Sku,
    string Category,
    string UnitSymbol,
    decimal SellableStock,
    decimal ReferenceCost,
    decimal DefaultSalePrice,
    decimal FactorToBaseUnit,
    bool IsSerialized,
    bool SerialTrackingEnabled,
    bool ImeiTrackingEnabled,
    EdgeRetails.Domain.Catalog.TrackingMode TrackingMode = EdgeRetails.Domain.Catalog.TrackingMode.Quantity);

public interface IPurchaseCatalogReadService
{
    Task<IReadOnlyList<PurchaseCatalogProductDto>> GetPurchasableCatalogAsync(
        CancellationToken cancellationToken);

    Task<PurchaseCatalogPageDto> SearchAsync(PurchaseCatalogPageQuery query, CancellationToken cancellationToken);
}

public sealed record PurchaseCatalogPageQuery(string? Search = null, int PageSize = 50,
    string? AfterName = null, Guid? AfterProductId = null, Guid? ProductId = null, bool IncludeInactive = false);

public sealed record PurchaseCatalogPageDto(IReadOnlyList<PurchaseCatalogProductDto> Items,
    string? NextName, Guid? NextProductId);
