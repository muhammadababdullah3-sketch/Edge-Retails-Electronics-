using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Inventory;

namespace EdgeRetails.Application.Features.Inventory;

public sealed record ExactInventoryUnitDto(
    Guid InventoryUnitId,
    Guid ProductId,
    string ProductName,
    string? Sku,
    string? TrackingCode,
    string? SerialNumber,
    string? Imei1,
    string? Imei2,
    InventoryUnitStatus Status,
    decimal AcquisitionCost,
    Guid? SourcePurchaseItemId,
    string? PurchaseNumber,
    string? SupplierName,
    DateTimeOffset CreatedAt,
    long Version);

/// <summary>
/// Minimum serialized-unit identity data needed by POS. Deliberately excludes acquisition cost,
/// supplier, and purchase provenance, which belong to inventory authority.
/// </summary>
public sealed record PosExactUnitDto(
    Guid InventoryUnitId,
    Guid ProductId,
    string ProductName,
    string? Sku,
    string? TrackingCode,
    string? SerialNumber,
    string? Imei1,
    string? Imei2,
    InventoryUnitStatus Status,
    long Version);

public sealed record ScannerProductMatchDto(
    ScannerResolutionNamespace Namespace,
    Guid ProductId,
    Guid ProductUnitId,
    string ProductName,
    string? Sku,
    string? Brand,
    string Category,
    string UnitSymbol,
    decimal SellableStock,
    decimal UnitPrice,
    bool IsSerialized,
    Guid? InventoryUnitId,
    string? TrackingCode,
    string? SerialNumber,
    string? Imei1,
    string? Imei2,
    InventoryUnitStatus? UnitStatus,
    bool IsSellable = true,
    string? SupplierName = null,
    string? PurchaseNumber = null,
    Guid? SourcePurchaseItemId = null,
    Guid? SupplierId = null);

public sealed record PosDraftSummaryDto(
    Guid DraftId,
    string DraftNumber,
    Guid? CustomerId,
    string CustomerName,
    Guid CreatedBy,
    string? TerminalId,
    string? Note,
    DateTimeOffset UpdatedAt,
    long Version,
    int ItemCount);

public sealed record PosDraftLineDto(
    Guid DraftItemId,
    Guid ProductId,
    Guid ProductUnitId,
    string ProductName,
    string? Sku,
    string UnitSymbol,
    decimal EnteredQuantity,
    decimal DisplayedUnitPriceSnapshot,
    Guid? SelectedInventoryUnitId,
    ExactInventoryUnitDto? SelectedUnit);

public sealed record PosDraftDetailDto(
    PosDraftSummaryDto Draft,
    IReadOnlyList<PosDraftLineDto> Items);


public sealed record StocktakeLineDto(
    Guid StocktakeItemId,
    Guid ProductId,
    string ProductName,
    string? Sku,
    bool IsSerialized,
    decimal ExpectedSellableQty,
    decimal? CountedSellableQty,
    decimal VarianceQty,
    string? ReviewNote);

public sealed record StocktakeSnapshotDto(
    Guid StocktakeId,
    StocktakeScope Scope,
    StocktakeStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? ReviewAt,
    string? Note,
    long Version,
    IReadOnlyList<StocktakeLineDto> Items);

public interface IPhase4WorkflowReadService
{
    Task<IReadOnlyList<ExactInventoryUnitDto>> GetExactUnitsAsync(
        Guid productId,
        InventoryUnitStatus? status,
        Guid? sourcePurchaseItemId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ExactInventoryUnitDto>> GetExactUnitsAsync(
        Guid productId,
        InventoryUnitStatus? status,
        Guid? sourcePurchaseItemId,
        int pageSize,
        CancellationToken cancellationToken) =>
        GetExactUnitsAsync(productId, status, sourcePurchaseItemId, cancellationToken);

    Task<IReadOnlyList<ExactInventoryUnitDto>> GetExactUnitsAsync(
        Guid productId,
        InventoryUnitStatus? status,
        Guid? sourcePurchaseItemId,
        int pageSize,
        Guid? beforeUnitId,
        CancellationToken cancellationToken) =>
        GetExactUnitsAsync(productId, status, sourcePurchaseItemId, pageSize, cancellationToken);

    Task<IReadOnlyList<ScannerProductMatchDto>> ResolveScannerAsync(
        string input,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PosDraftSummaryDto>> GetOpenDraftsAsync(
        CancellationToken cancellationToken);

    Task<PosDraftDetailDto?> GetDraftAsync(
        Guid draftId,
        CancellationToken cancellationToken);

    Task<StocktakeSnapshotDto?> GetOpenStocktakeAsync(
        CancellationToken cancellationToken);
}
