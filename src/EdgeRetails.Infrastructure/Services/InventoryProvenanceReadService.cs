using System.Data;
using System.Data.Common;
using Dapper;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Infrastructure.Persistence;

namespace EdgeRetails.Infrastructure.Services;

public sealed class InventoryProvenanceReadService : IInventoryProvenanceReadService
{
    private readonly EdgeRetailsDbContext _db;

    public InventoryProvenanceReadService(EdgeRetailsDbContext db) => _db = db;

    public async Task<IReadOnlyList<ProductPurchaseProvenanceRowDto>>
        GetProductPurchaseProvenanceAsync(
            GetProductPurchaseProvenanceQuery query,
            CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                l.id AS LotId,
                l.product_id AS ProductId,
                l.purchase_item_id AS PurchaseItemId,
                p.id AS PurchaseId,
                p.purchase_number AS PurchaseNumber,
                s.name AS SupplierName,
                l.received_quantity AS ReceivedQuantity,
                l.original_unit_cost AS OriginalUnitCost,
                l.effective_unit_cost AS EffectiveUnitCost,
                COALESCE((
                    SELECT sum(b.quantity)
                    FROM inventory.lot_bucket_balances b
                    WHERE b.lot_id = l.id AND b.stock_bucket = 1
                ), 0) AS SellableQuantity,
                COALESCE((
                    SELECT sum(b.quantity)
                    FROM inventory.lot_bucket_balances b
                    WHERE b.lot_id = l.id AND b.stock_bucket = 2
                ), 0) AS DamagedQuantity,
                COALESCE((
                    SELECT sum(b.quantity)
                    FROM inventory.lot_bucket_balances b
                    WHERE b.lot_id = l.id AND b.stock_bucket = 3
                ), 0) AS DefectiveQuantity,
                COALESCE((
                    SELECT sum(b.quantity)
                    FROM inventory.lot_bucket_balances b
                    WHERE b.lot_id = l.id AND b.stock_bucket = 4
                ), 0) AS WithSupplierQuantity,
                COALESCE((
                    SELECT sum(b.quantity)
                    FROM inventory.lot_bucket_balances b
                    WHERE b.lot_id = l.id AND b.stock_bucket = 5
                ), 0) AS ScrapQuantity,
                COALESCE((
                    SELECT sum(c.quantity)
                    FROM inventory.lot_consumptions c
                    WHERE c.lot_id = l.id
                ), 0) AS ConsumedQuantity,
                l.created_at AS CreatedAt
            FROM inventory.lots l
            LEFT JOIN purchasing.purchase_items pi ON pi.id = l.purchase_item_id
            LEFT JOIN purchasing.purchases p ON p.id = pi.purchase_id
            LEFT JOIN parties.suppliers s ON s.id = p.supplier_id
            WHERE l.product_id = @ProductId
              AND (
                    CAST(@BeforeCreatedAt AS timestamp with time zone) IS NULL
                    OR l.created_at < @BeforeCreatedAt
                    OR (
                        l.created_at = @BeforeCreatedAt
                        AND (CAST(@BeforeLotId AS uuid) IS NULL OR l.id < @BeforeLotId)
                    )
                  )
            ORDER BY l.created_at DESC, l.id DESC
            LIMIT @PageSize;
            """;

        return await WithConnectionAsync(async (connection, transaction) =>
        {
            var rows = await connection.QueryAsync<ProductPurchaseProvenanceDbRow>(
                new CommandDefinition(
                    sql,
                    new
                    {
                        query.ProductId,
                        query.BeforeCreatedAt,
                        query.BeforeLotId,
                        query.PageSize
                    },
                    transaction,
                    cancellationToken: cancellationToken));

            return rows.Select(row => new ProductPurchaseProvenanceRowDto(
                row.LotId,
                row.ProductId,
                row.PurchaseItemId,
                row.PurchaseId,
                row.PurchaseNumber,
                row.SupplierName,
                row.ReceivedQuantity,
                row.OriginalUnitCost,
                row.EffectiveUnitCost,
                row.SellableQuantity,
                row.DamagedQuantity,
                row.DefectiveQuantity,
                row.WithSupplierQuantity,
                row.ScrapQuantity,
                row.ConsumedQuantity,
                ToUtcOffset(row.CreatedAt))).ToArray();
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<ProductSaleHistoryRowDto>>
        GetProductSaleHistoryAsync(
            GetProductSaleHistoryQuery query,
            CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                s.id AS SaleId,
                si.id AS SaleItemId,
                s.invoice_number AS InvoiceNumber,
                s.completed_at AS CompletedAt,
                COALESCE(c.name, 'Walk-in Customer') AS CustomerName,
                si.base_quantity AS BaseQuantity,
                si.net_line_total AS NetLineTotal,
                si.unit_cost_snapshot AS UnitCostSnapshot,
                si.total_cost_snapshot AS TotalCostSnapshot,
                si.gross_profit_snapshot AS GrossProfitSnapshot
            FROM sales.sale_items si
            INNER JOIN sales.sales s ON s.id = si.sale_id
            LEFT JOIN parties.customers c ON c.id = s.customer_id
            WHERE si.product_id = @ProductId
              AND (
                    CAST(@BeforeCompletedAt AS timestamp with time zone) IS NULL
                    OR s.completed_at < @BeforeCompletedAt
                    OR (
                        s.completed_at = @BeforeCompletedAt
                        AND (CAST(@BeforeSaleItemId AS uuid) IS NULL OR si.id < @BeforeSaleItemId)
                    )
                  )
            ORDER BY s.completed_at DESC, si.id DESC
            LIMIT @PageSize;
            """;

        return await WithConnectionAsync(async (connection, transaction) =>
        {
            var rows = await connection.QueryAsync<ProductSaleHistoryDbRow>(
                new CommandDefinition(
                    sql,
                    new
                    {
                        query.ProductId,
                        query.BeforeCompletedAt,
                        query.BeforeSaleItemId,
                        query.PageSize
                    },
                    transaction,
                    cancellationToken: cancellationToken));

            return rows.Select(row => new ProductSaleHistoryRowDto(
                row.SaleId,
                row.SaleItemId,
                row.InvoiceNumber,
                ToUtcOffset(row.CompletedAt),
                row.CustomerName,
                row.BaseQuantity,
                row.NetLineTotal,
                row.UnitCostSnapshot,
                row.TotalCostSnapshot,
                row.GrossProfitSnapshot)).ToArray();
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<LotConsumptionTraceRowDto>>
        GetLotConsumptionTraceAsync(
            GetLotConsumptionTraceQuery query,
            CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                c.id AS ConsumptionId,
                c.lot_id AS LotId,
                c.movement_id AS MovementId,
                m.movement_type AS MovementType,
                m.reference_type AS ReferenceType,
                m.reference_id AS ReferenceId,
                c.quantity AS Quantity,
                c.unit_cost_snapshot AS UnitCostSnapshot,
                c.total_cost_snapshot AS TotalCostSnapshot,
                c.occurred_at AS OccurredAt
            FROM inventory.lot_consumptions c
            INNER JOIN inventory.movements m ON m.id = c.movement_id
            WHERE c.lot_id = @LotId
            ORDER BY c.occurred_at, c.id;
            """;

        return await WithConnectionAsync(async (connection, transaction) =>
        {
            var rows = await connection.QueryAsync<LotConsumptionTraceRowDto>(
                new CommandDefinition(
                    sql,
                    new { query.LotId },
                    transaction,
                    cancellationToken: cancellationToken));
            return rows.ToArray();
        }, cancellationToken);
    }

    public async Task<SerializedUnitHistoryDto?> GetSerializedUnitHistoryAsync(
        GetSerializedUnitHistoryQuery query,
        CancellationToken cancellationToken)
    {
        const string headerSql = """
            SELECT
                u.id AS InventoryUnitId,
                u.product_id AS ProductId,
                p.name AS ProductName,
                u.serial_number AS SerialNumber,
                u.imei1 AS Imei1,
                u.imei2 AS Imei2,
                u.status AS Status,
                u.acquisition_cost AS AcquisitionCost,
                u.source_purchase_item_id AS SourcePurchaseItemId,
                u.inventory_lot_id AS InventoryLotId
            FROM inventory.units u
            INNER JOIN catalog.products p ON p.id = u.product_id
            WHERE u.id = @InventoryUnitId;
            """;

        const string movementSql = """
            SELECT
                m.id AS MovementId,
                m.movement_type AS MovementType,
                m.reference_type AS ReferenceType,
                m.reference_id AS ReferenceId,
                mu.from_status AS FromStatus,
                mu.to_status AS ToStatus,
                m.occurred_at AS OccurredAt,
                m.reason AS Reason,
                m.note AS Note
            FROM inventory.movement_units mu
            INNER JOIN inventory.movements m ON m.id = mu.movement_id
            WHERE mu.inventory_unit_id = @InventoryUnitId
            ORDER BY m.occurred_at, m.id;
            """;

        return await WithConnectionAsync(async (connection, transaction) =>
        {
            var header = await connection.QuerySingleOrDefaultAsync<SerializedUnitHeaderRow>(
                new CommandDefinition(
                    headerSql,
                    new { query.InventoryUnitId },
                    transaction,
                    cancellationToken: cancellationToken));

            if (header is null)
            {
                return null;
            }

            var movements = (await connection.QueryAsync<SerializedUnitMovementDto>(
                new CommandDefinition(
                    movementSql,
                    new { query.InventoryUnitId },
                    transaction,
                    cancellationToken: cancellationToken))).ToArray();

            return new SerializedUnitHistoryDto(
                header.InventoryUnitId,
                header.ProductId,
                header.ProductName,
                header.SerialNumber,
                header.Imei1,
                header.Imei2,
                header.Status,
                header.AcquisitionCost,
                header.SourcePurchaseItemId,
                header.InventoryLotId,
                movements);
        }, cancellationToken);
    }

    private Task<T> WithConnectionAsync<T>(
        Func<DbConnection, IDbTransaction?, Task<T>> operation,
        CancellationToken cancellationToken) =>
        DbReadConnection.WithAsync(_db, operation, cancellationToken);

    private static DateTimeOffset ToUtcOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed record ProductPurchaseProvenanceDbRow(
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
        DateTime CreatedAt);

    private sealed record ProductSaleHistoryDbRow(
        Guid SaleId,
        Guid SaleItemId,
        string InvoiceNumber,
        DateTime CompletedAt,
        string CustomerName,
        decimal BaseQuantity,
        decimal NetLineTotal,
        decimal UnitCostSnapshot,
        decimal TotalCostSnapshot,
        decimal GrossProfitSnapshot);

    private sealed record SerializedUnitHeaderRow(
        Guid InventoryUnitId,
        Guid ProductId,
        string ProductName,
        string? SerialNumber,
        string? Imei1,
        string? Imei2,
        EdgeRetails.Domain.Inventory.InventoryUnitStatus Status,
        decimal AcquisitionCost,
        Guid? SourcePurchaseItemId,
        Guid? InventoryLotId);
}

