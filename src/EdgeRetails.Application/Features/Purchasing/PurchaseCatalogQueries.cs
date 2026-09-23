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
    bool ImeiTrackingEnabled);

public interface IPurchaseCatalogReadService
{
    Task<IReadOnlyList<PurchaseCatalogProductDto>> GetPurchasableCatalogAsync(
        CancellationToken cancellationToken);
}
