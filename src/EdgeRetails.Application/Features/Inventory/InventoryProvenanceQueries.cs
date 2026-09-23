using EdgeRetails.Domain.Inventory;

namespace EdgeRetails.Application.Features.Inventory;

public sealed record GetProductPurchaseProvenanceQuery(
    Guid ProductId,
    int PageSize = 100,
    DateTimeOffset? BeforeCreatedAt = null,
    Guid? BeforeLotId = null);

public sealed record ProductPurchaseProvenanceRowDto(
    Guid LotId,
    Guid ProductId,
    Guid? PurchaseItemId,
    Guid? PurchaseId,
    string? PurchaseNumber,
    string? SupplierName,
    decimal ReceivedQuantity,
    decimal OriginalUnitCost,
    decimal EffectiveUnitCost,
    decimal SellableQuantity,
    decimal DamagedQuantity,
    decimal DefectiveQuantity,
    decimal WithSupplierQuantity,
    decimal ScrapQuantity,
    decimal ConsumedQuantity,
    DateTimeOffset CreatedAt);

public sealed record GetProductSaleHistoryQuery(
    Guid ProductId,
    int PageSize = 100,
    DateTimeOffset? BeforeCompletedAt = null,
    Guid? BeforeSaleItemId = null);

public sealed record ProductSaleHistoryRowDto(
    Guid SaleId,
    Guid SaleItemId,
    string InvoiceNumber,
    DateTimeOffset CompletedAt,
    string CustomerName,
    decimal BaseQuantity,
    decimal NetLineTotal,
    decimal UnitCostSnapshot,
    decimal TotalCostSnapshot,
    decimal GrossProfitSnapshot);

public sealed record GetLotConsumptionTraceQuery(Guid LotId);

public sealed record LotConsumptionTraceRowDto(
    Guid ConsumptionId,
    Guid LotId,
    Guid MovementId,
    InventoryMovementType MovementType,
    string ReferenceType,
    Guid ReferenceId,
    decimal Quantity,
    decimal UnitCostSnapshot,
    decimal TotalCostSnapshot,
    DateTimeOffset OccurredAt);

public sealed record GetSerializedUnitHistoryQuery(Guid InventoryUnitId);

public sealed record SerializedUnitMovementDto(
    Guid MovementId,
    InventoryMovementType MovementType,
    string ReferenceType,
    Guid ReferenceId,
    InventoryUnitStatus? FromStatus,
    InventoryUnitStatus ToStatus,
    DateTimeOffset OccurredAt,
    string? Reason,
    string? Note);

public sealed record SerializedUnitHistoryDto(
    Guid InventoryUnitId,
    Guid ProductId,
    string ProductName,
    string? SerialNumber,
    string? Imei1,
    string? Imei2,
    InventoryUnitStatus Status,
    decimal AcquisitionCost,
    Guid? SourcePurchaseItemId,
    Guid? InventoryLotId,
    IReadOnlyList<SerializedUnitMovementDto> Movements);

public interface IInventoryProvenanceReadService
{
    Task<IReadOnlyList<ProductPurchaseProvenanceRowDto>> GetProductPurchaseProvenanceAsync(
        GetProductPurchaseProvenanceQuery query,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ProductSaleHistoryRowDto>> GetProductSaleHistoryAsync(
        GetProductSaleHistoryQuery query,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<LotConsumptionTraceRowDto>> GetLotConsumptionTraceAsync(
        GetLotConsumptionTraceQuery query,
        CancellationToken cancellationToken);

    Task<SerializedUnitHistoryDto?> GetSerializedUnitHistoryAsync(
        GetSerializedUnitHistoryQuery query,
        CancellationToken cancellationToken);
}

public sealed class GetProductPurchaseProvenanceHandler
{
    private readonly IInventoryProvenanceReadService _reads;

    public GetProductPurchaseProvenanceHandler(IInventoryProvenanceReadService reads) =>
        _reads = reads;

    public Task<IReadOnlyList<ProductPurchaseProvenanceRowDto>> HandleAsync(
        GetProductPurchaseProvenanceQuery query,
        CancellationToken cancellationToken) =>
        _reads.GetProductPurchaseProvenanceAsync(
            query with { PageSize = Math.Clamp(query.PageSize, 1, 500) },
            cancellationToken);
}

public sealed class GetProductSaleHistoryHandler
{
    private readonly IInventoryProvenanceReadService _reads;

    public GetProductSaleHistoryHandler(IInventoryProvenanceReadService reads) =>
        _reads = reads;

    public Task<IReadOnlyList<ProductSaleHistoryRowDto>> HandleAsync(
        GetProductSaleHistoryQuery query,
        CancellationToken cancellationToken) =>
        _reads.GetProductSaleHistoryAsync(
            query with { PageSize = Math.Clamp(query.PageSize, 1, 500) },
            cancellationToken);
}

public sealed class GetLotConsumptionTraceHandler
{
    private readonly IInventoryProvenanceReadService _reads;

    public GetLotConsumptionTraceHandler(IInventoryProvenanceReadService reads) =>
        _reads = reads;

    public Task<IReadOnlyList<LotConsumptionTraceRowDto>> HandleAsync(
        GetLotConsumptionTraceQuery query,
        CancellationToken cancellationToken) =>
        _reads.GetLotConsumptionTraceAsync(query, cancellationToken);
}

public sealed class GetSerializedUnitHistoryHandler
{
    private readonly IInventoryProvenanceReadService _reads;

    public GetSerializedUnitHistoryHandler(IInventoryProvenanceReadService reads) =>
        _reads = reads;

    public Task<SerializedUnitHistoryDto?> HandleAsync(
        GetSerializedUnitHistoryQuery query,
        CancellationToken cancellationToken) =>
        _reads.GetSerializedUnitHistoryAsync(query, cancellationToken);
}

