using EdgeRetails.Domain.Inventory;

namespace EdgeRetails.Application.Features.Inventory;

public sealed record InventoryStockRowDto(
    Guid ProductId,
    string Name,
    string? Sku,
    string? Brand,
    string? Model,
    string Category,
    string UnitSymbol,
    decimal SellableQty,
    decimal DamagedQty,
    decimal DefectiveQty,
    decimal WithSupplierQty,
    decimal ScrapQty,
    decimal MovingAverageCost,
    decimal? LastPurchaseCost,
    decimal DefaultSalePrice,
    decimal MinimumStockLevel,
    bool IsSerialized);

public sealed record InventoryMovementRowDto(
    Guid MovementId,
    Guid ProductId,
    string ProductName,
    InventoryMovementType MovementType,
    string ReferenceType,
    Guid? ReferenceId,
    decimal QuantityDelta,
    decimal QuantityBefore,
    decimal QuantityAfter,
    DateTimeOffset OccurredAt,
    string? Reason,
    string? Note); public sealed record InventoryStockPageQuery(
    string? Search = null,
    string? Category = null,
    string? Brand = null,
    int PageSize = 200,
    string? BeforeName = null,
    Guid? BeforeProductId = null);

public interface IInventoryOverviewReadService
{
    Task<IReadOnlyList<InventoryStockRowDto>> GetStockAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<InventoryStockRowDto>> GetStockPageAsync(
        InventoryStockPageQuery query,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<InventoryMovementRowDto>> GetMovementsAsync(
        int pageSize,
        CancellationToken cancellationToken,
        DateTimeOffset? beforeOccurredAt = null,
        Guid? beforeMovementId = null);
}
