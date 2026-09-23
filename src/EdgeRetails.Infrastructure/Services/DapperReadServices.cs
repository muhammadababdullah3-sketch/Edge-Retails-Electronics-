using System.Data;
using System.Data.Common;
using Dapper;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace EdgeRetails.Infrastructure.Services;

public sealed class SalesReadService : ISalesReadService
{
    private readonly EdgeRetailsDbContext _db;

    public SalesReadService(EdgeRetailsDbContext db) => _db = db;

    public async Task<IReadOnlyList<SalesHistoryRowDto>> GetHistoryAsync(
        GetSalesHistoryQuery query,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                s.id AS SaleId,
                s.invoice_number AS InvoiceNumber,
                s.completed_at AS CompletedAt,
                COALESCE(c.name, 'Walk-in Customer') AS CustomerName,
                s.grand_total AS GrandTotal,
                p.method AS PaymentMethod,
                (
                    SELECT count(*)::int
                    FROM sales.sale_items si
                    WHERE si.sale_id = s.id
                ) AS ItemCount,
                COALESCE((
                    SELECT sum(r.refund_amount)
                    FROM sales.returns r
                    WHERE r.sale_id = s.id
                ), 0) AS ReturnedAmount
            FROM sales.sales s
            LEFT JOIN parties.customers c ON c.id = s.customer_id
            INNER JOIN sales.sale_payments p ON p.sale_id = s.id
            WHERE (CAST(@FromUtc AS timestamp with time zone) IS NULL OR s.completed_at >= @FromUtc)
              AND (CAST(@ToUtc AS timestamp with time zone) IS NULL OR s.completed_at < @ToUtc)
              AND (
                    CAST(@Search AS text) IS NULL
                    OR s.invoice_number ILIKE @SearchLike
                    OR c.name ILIKE @SearchLike
                  )
              AND (
                    CAST(@BeforeCompletedAt AS timestamp with time zone) IS NULL
                    OR s.completed_at < @BeforeCompletedAt
                    OR (
                        s.completed_at = @BeforeCompletedAt
                        AND (CAST(@BeforeSaleId AS uuid) IS NULL OR s.id < @BeforeSaleId)
                    )
                  )
            ORDER BY s.completed_at DESC, s.id DESC
            LIMIT @PageSize;
            """;

        return await WithConnectionAsync(async (connection, transaction) =>
        {
            var rows = await connection.QueryAsync<SalesHistoryRowDto>(
                new CommandDefinition(
                    sql,
                    new
                    {
                        query.FromUtc,
                        query.ToUtc,
                        query.Search,
                        SearchLike = query.Search is null ? null : $"%{query.Search}%",
                        query.BeforeCompletedAt,
                        query.BeforeSaleId,
                        query.PageSize
                    },
                    transaction,
                    cancellationToken: cancellationToken));
            return rows.ToArray();
        }, cancellationToken);
    }

    public async Task<SaleDetailDto?> GetDetailAsync(
        GetSaleDetailQuery query,
        CancellationToken cancellationToken)
    {
        const string headerSql = """
            SELECT
                s.id AS SaleId,
                s.invoice_number AS InvoiceNumber,
                s.completed_at AS CompletedAt,
                s.customer_id AS CustomerId,
                COALESCE(c.name, 'Walk-in Customer') AS CustomerName,
                s.subtotal AS Subtotal,
                s.invoice_discount AS InvoiceDiscount,
                s.grand_total AS GrandTotal,
                p.method AS PaymentMethod,
                p.amount_tendered AS AmountTendered,
                p.applied_amount AS AppliedAmount,
                p.change_given AS ChangeGiven,
                p.reference AS PaymentReference,
                s.receipt_template_snapshot AS ReceiptTemplateSnapshot
            FROM sales.sales s
            LEFT JOIN parties.customers c ON c.id = s.customer_id
            INNER JOIN sales.sale_payments p ON p.sale_id = s.id
            WHERE s.id = @SaleId;
            """;

        const string itemsSql = """
            SELECT
                si.id AS SaleItemId,
                si.product_id AS ProductId,
                si.product_name_snapshot AS ProductName,
                si.sku_snapshot AS Sku,
                si.entered_quantity AS EnteredQuantity,
                si.factor_to_base_snapshot AS FactorToBaseSnapshot,
                si.base_quantity AS BaseQuantity,
                si.unit_price AS UnitPrice,
                si.gross_line_total AS GrossLineTotal,
                si.allocated_invoice_discount AS AllocatedInvoiceDiscount,
                si.net_line_total AS NetLineTotal
            FROM sales.sale_items si
            WHERE si.sale_id = @SaleId
            ORDER BY si.id;
            """;

        const string returnsSql = """
            SELECT
                r.id AS SaleReturnId,
                r.return_number AS ReturnNumber,
                r.created_at AS CreatedAt,
                r.reason_code AS ReasonCode,
                r.refund_method AS RefundMethod,
                r.refund_amount AS RefundAmount
            FROM sales.returns r
            WHERE r.sale_id = @SaleId
            ORDER BY r.created_at, r.id;
            """;

        const string returnItemsSql = """
            SELECT
                ri.sale_return_id AS SaleReturnId,
                ri.sale_item_id AS SaleItemId,
                ri.product_id AS ProductId,
                si.sku_snapshot AS Sku,
                ri.entered_quantity AS EnteredQuantity,
                ri.refund_amount AS RefundAmount
            FROM sales.return_items ri
            INNER JOIN sales.sale_items si ON si.id = ri.sale_item_id
            INNER JOIN sales.returns r ON r.id = ri.sale_return_id
            WHERE r.sale_id = @SaleId
            ORDER BY r.created_at, r.id, ri.id;
            """;

        return await WithConnectionAsync(async (connection, transaction) =>
        {
            var header = await connection.QuerySingleOrDefaultAsync<SaleHeaderRow>(
                new CommandDefinition(
                    headerSql,
                    new { query.SaleId },
                    transaction,
                    cancellationToken: cancellationToken));

            if (header is null)
            {
                return null;
            }

            var items = (await connection.QueryAsync<SaleDetailItemDto>(
                new CommandDefinition(
                    itemsSql,
                    new { query.SaleId },
                    transaction,
                    cancellationToken: cancellationToken))).ToArray();

            var returns = (await connection.QueryAsync<SaleReturnSummaryDto>(
                new CommandDefinition(
                    returnsSql,
                    new { query.SaleId },
                    transaction,
                    cancellationToken: cancellationToken))).ToArray();

            var returnItems = (await connection.QueryAsync<SaleReturnItemDetailDto>(
                new CommandDefinition(
                    returnItemsSql,
                    new { query.SaleId },
                    transaction,
                    cancellationToken: cancellationToken))).ToArray();

            return new SaleDetailDto(
                header.SaleId,
                header.InvoiceNumber,
                header.CompletedAt,
                header.CustomerId,
                header.CustomerName,
                header.Subtotal,
                header.InvoiceDiscount,
                header.GrandTotal,
                header.PaymentMethod,
                header.AmountTendered,
                header.AppliedAmount,
                header.ChangeGiven,
                header.PaymentReference,
                header.ReceiptTemplateSnapshot,
                items,
                returns,
                returnItems);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<QuotationListRowDto>> GetQuotationsAsync(
        GetQuotationsQuery query,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                q.id AS QuotationId,
                q.quotation_number AS QuotationNumber,
                q.quotation_date AS QuotationDate,
                q.valid_until AS ValidUntil,
                q.status AS Status,
                COALESCE(c.name, q.customer_name_snapshot, 'Walk-in Customer') AS CustomerName,
                q.grand_total AS GrandTotal,
                q.converted_sale_id AS ConvertedSaleId
            FROM sales.quotations q
            LEFT JOIN parties.customers c ON c.id = q.customer_id
            WHERE (CAST(@Status AS integer) IS NULL OR q.status = @Status)
              AND (
                    CAST(@Search AS text) IS NULL
                    OR q.quotation_number ILIKE @SearchLike
                    OR c.name ILIKE @SearchLike
                    OR q.customer_name_snapshot ILIKE @SearchLike
                  )
              AND (
                    CAST(@BeforeQuotationDate AS date) IS NULL
                    OR q.quotation_date < @BeforeQuotationDate
                    OR (
                        q.quotation_date = @BeforeQuotationDate
                        AND (CAST(@BeforeQuotationId AS uuid) IS NULL OR q.id < @BeforeQuotationId)
                    )
                  )
            ORDER BY q.quotation_date DESC, q.id DESC
            LIMIT @PageSize;
            """;

        return await WithConnectionAsync(async (connection, transaction) =>
        {
            var rows = await connection.QueryAsync<QuotationListRowDto>(
                new CommandDefinition(
                    sql,
                    new
                    {
                        Status = query.Status is null ? null : (int?)query.Status.Value,
                        query.Search,
                        SearchLike = query.Search is null ? null : $"%{query.Search}%",
                        query.BeforeQuotationDate,
                        query.BeforeQuotationId,
                        query.PageSize
                    },
                    transaction,
                    cancellationToken: cancellationToken));
            return rows.ToArray();
        }, cancellationToken);
    }

    public async Task<QuotationDetailDto?> GetQuotationDetailAsync(
        GetQuotationDetailQuery query,
        CancellationToken cancellationToken)
    {
        const string headerSql = """
            SELECT
                q.id AS QuotationId,
                q.quotation_number AS QuotationNumber,
                q.customer_id AS CustomerId,
                COALESCE(c.name, q.customer_name_snapshot) AS CustomerName,
                q.quotation_date AS QuotationDate,
                q.valid_until AS ValidUntil,
                q.status AS Status,
                q.subtotal AS Subtotal,
                q.discount AS Discount,
                q.grand_total AS GrandTotal,
                q.notes AS Notes,
                q.converted_sale_id AS ConvertedSaleId
            FROM sales.quotations q
            LEFT JOIN parties.customers c ON c.id = q.customer_id
            WHERE q.id = @QuotationId;
            """;

        const string itemsSql = """
            SELECT
                qi.product_id AS ProductId,
                qi.product_name AS ProductName,
                qi.sku AS Sku,
                qi.selected_unit_id AS SelectedUnitId,
                qi.entered_quantity AS EnteredQuantity,
                qi.factor_to_base_snapshot AS FactorToBaseSnapshot,
                qi.base_quantity AS BaseQuantity,
                qi.quoted_unit_price AS QuotedUnitPrice,
                qi.line_total AS LineTotal
            FROM sales.quotation_items qi
            WHERE qi.quotation_id = @QuotationId
            ORDER BY qi.id;
            """;

        return await WithConnectionAsync(async (connection, transaction) =>
        {
            var header = await connection.QuerySingleOrDefaultAsync<QuotationHeaderRow>(
                new CommandDefinition(
                    headerSql,
                    new { query.QuotationId },
                    transaction,
                    cancellationToken: cancellationToken));
            if (header is null)
            {
                return null;
            }

            var items = (await connection.QueryAsync<QuotationDetailItemDto>(
                new CommandDefinition(
                    itemsSql,
                    new { query.QuotationId },
                    transaction,
                    cancellationToken: cancellationToken))).ToArray();

            return new QuotationDetailDto(
                header.QuotationId,
                header.QuotationNumber,
                header.CustomerId,
                header.CustomerName,
                header.QuotationDate,
                header.ValidUntil,
                header.Status,
                header.Subtotal,
                header.Discount,
                header.GrandTotal,
                header.Notes,
                header.ConvertedSaleId,
                items);
        }, cancellationToken);
    }


    public async Task<IReadOnlyList<SaleReturnHistoryRowDto>> GetReturnHistoryAsync(
        GetSaleReturnHistoryQuery query,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                r.id AS SaleReturnId,
                r.return_number AS ReturnNumber,
                r.sale_id AS SaleId,
                s.invoice_number AS InvoiceNumber,
                r.created_at AS CreatedAt,
                r.reason_code AS ReasonCode,
                r.reason_note AS ReasonNote,
                r.refund_method AS RefundMethod,
                r.refund_amount AS RefundAmount
            FROM sales.returns r
            INNER JOIN sales.sales s ON s.id = r.sale_id
            WHERE (CAST(@SaleId AS uuid) IS NULL OR r.sale_id = @SaleId)
              AND (CAST(@FromUtc AS timestamp with time zone) IS NULL OR r.created_at >= @FromUtc)
              AND (CAST(@ToUtc AS timestamp with time zone) IS NULL OR r.created_at < @ToUtc)
              AND (
                    CAST(@BeforeCreatedAt AS timestamp with time zone) IS NULL
                    OR r.created_at < @BeforeCreatedAt
                    OR (
                        r.created_at = @BeforeCreatedAt
                        AND (CAST(@BeforeReturnId AS uuid) IS NULL OR r.id < @BeforeReturnId)
                    )
                  )
            ORDER BY r.created_at DESC, r.id DESC
            LIMIT @PageSize;
            """;

        return await WithConnectionAsync(async (connection, transaction) =>
        {
            var rows = await connection.QueryAsync<SaleReturnHistoryRowDto>(
                new CommandDefinition(
                    sql,
                    new
                    {
                        query.SaleId,
                        query.FromUtc,
                        query.ToUtc,
                        query.BeforeCreatedAt,
                        query.BeforeReturnId,
                        query.PageSize
                    },
                    transaction,
                    cancellationToken: cancellationToken));
            return rows.ToArray();
        }, cancellationToken);
    }

    private Task<T> WithConnectionAsync<T>(
        Func<DbConnection, IDbTransaction?, Task<T>> operation,
        CancellationToken cancellationToken) =>
        DbReadConnection.WithAsync(_db, operation, cancellationToken);

    private sealed record SaleHeaderRow(
        Guid SaleId,
        string InvoiceNumber,
        DateTimeOffset CompletedAt,
        Guid? CustomerId,
        string CustomerName,
        decimal Subtotal,
        decimal InvoiceDiscount,
        decimal GrandTotal,
        SalePaymentMethod PaymentMethod,
        decimal AmountTendered,
        decimal AppliedAmount,
        decimal ChangeGiven,
        string? PaymentReference,
        string? ReceiptTemplateSnapshot);

    private sealed record QuotationHeaderRow(
        Guid QuotationId,
        string QuotationNumber,
        Guid? CustomerId,
        string? CustomerName,
        DateOnly QuotationDate,
        DateOnly? ValidUntil,
        QuotationStatus Status,
        decimal Subtotal,
        decimal Discount,
        decimal GrandTotal,
        string? Notes,
        Guid? ConvertedSaleId);
}

public sealed class PurchasingReadService : IPurchasingReadService
{
    private readonly EdgeRetailsDbContext _db;

    public PurchasingReadService(EdgeRetailsDbContext db) => _db = db;

    public async Task<IReadOnlyList<PurchaseHistoryRowDto>> GetHistoryAsync(
        GetPurchaseHistoryQuery query,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                p.id AS PurchaseId,
                p.purchase_number AS PurchaseNumber,
                p.purchase_date AS PurchaseDate,
                p.supplier_id AS SupplierId,
                s.name AS SupplierName,
                p.supplier_invoice_number AS SupplierInvoiceNumber,
                p.subtotal AS Subtotal,
                p.other_charges AS OtherCharges,
                p.grand_total AS GrandTotal,
                p.status AS Status,
                p.settlement_mode AS SettlementMode,
                COALESCE((
                    SELECT sum(r.supplier_return_value)
                    FROM purchasing.returns r
                    WHERE r.purchase_id = p.id
                ), 0) AS SupplierReturnValue,
                COALESCE((
                    SELECT count(*)::int
                    FROM purchasing.purchase_items pi
                    WHERE pi.purchase_id = p.id
                ), 0) AS ItemCount
            FROM purchasing.purchases p
            INNER JOIN parties.suppliers s ON s.id = p.supplier_id
            WHERE (CAST(@FromDate AS date) IS NULL OR p.purchase_date >= @FromDate)
              AND (CAST(@ToDate AS date) IS NULL OR p.purchase_date <= @ToDate)
              AND (CAST(@SupplierId AS uuid) IS NULL OR p.supplier_id = @SupplierId)
              AND (
                    CAST(@Search AS text) IS NULL
                    OR p.purchase_number ILIKE @SearchLike
                    OR p.supplier_invoice_number ILIKE @SearchLike
                    OR s.name ILIKE @SearchLike
                  )
              AND (
                    CAST(@BeforePurchaseDate AS date) IS NULL
                    OR p.purchase_date < @BeforePurchaseDate
                    OR (
                        p.purchase_date = @BeforePurchaseDate
                        AND (CAST(@BeforePurchaseId AS uuid) IS NULL OR p.id < @BeforePurchaseId)
                    )
                  )
            ORDER BY p.purchase_date DESC, p.id DESC
            LIMIT @PageSize;
            """;

        return await WithConnectionAsync(async (connection, transaction) =>
        {
            var rows = await connection.QueryAsync<PurchaseHistoryRowDto>(
                new CommandDefinition(
                    sql,
                    new
                    {
                        query.FromDate,
                        query.ToDate,
                        query.SupplierId,
                        query.Search,
                        SearchLike = query.Search is null ? null : $"%{query.Search}%",
                        query.BeforePurchaseDate,
                        query.BeforePurchaseId,
                        query.PageSize
                    },
                    transaction,
                    cancellationToken: cancellationToken));
            return rows.ToArray();
        }, cancellationToken);
    }

    public async Task<PurchaseDocumentDto?> GetDocumentAsync(
        GetPurchaseDocumentQuery query,
        CancellationToken cancellationToken)
    {
        const string headerSql = """
            SELECT
                p.id AS PurchaseId,
                p.purchase_number AS PurchaseNumber,
                p.purchase_date AS PurchaseDate,
                p.supplier_id AS SupplierId,
                s.name AS SupplierName,
                p.supplier_invoice_number AS SupplierInvoiceNumber,
                p.note AS Note,
                p.subtotal AS Subtotal,
                p.other_charges AS OtherCharges,
                p.grand_total AS GrandTotal,
                p.status AS Status,
                p.settlement_mode AS SettlementMode,
                p.created_at AS CreatedAt
            FROM purchasing.purchases p
            INNER JOIN parties.suppliers s ON s.id = p.supplier_id
            WHERE p.id = @PurchaseId;
            """;

        const string itemsSql = """
            SELECT
                pi.id AS PurchaseItemId,
                pi.product_id AS ProductId,
                pi.product_name_snapshot AS ProductName,
                pi.sku_snapshot AS Sku,
                pi.product_unit_id AS ProductUnitId,
                u.symbol AS UnitSymbol,
                (cp.tracking_mode = 3) AS IsSerialized,
                pi.entered_quantity AS EnteredQuantity,
                pi.factor_to_base_snapshot AS FactorToBaseSnapshot,
                pi.base_quantity AS BaseQuantity,
                pi.entered_unit_cost AS EnteredUnitCost,
                pi.allocated_other_cost AS AllocatedOtherCost,
                pi.effective_base_unit_cost AS EffectiveBaseUnitCost,
                pi.effective_line_cost AS EffectiveLineCost,
                pi.sale_price_at_purchase AS SalePriceAtPurchase,
                COALESCE((
                    SELECT sum(pri.base_quantity)
                    FROM purchasing.return_items pri
                    INNER JOIN purchasing.returns pr ON pr.id = pri.purchase_return_id
                    WHERE pri.purchase_item_id = pi.id
                ), 0) AS ReturnedBaseQuantity,
                COALESCE((
                    SELECT sum(lb.quantity)
                    FROM inventory.lots l
                    INNER JOIN inventory.lot_bucket_balances lb ON lb.lot_id = l.id
                    WHERE l.purchase_item_id = pi.id
                      AND lb.stock_bucket = 1
                ), 0) AS EligibleBaseReturnQuantity
            FROM purchasing.purchase_items pi
            INNER JOIN catalog.product_units pu ON pu.id = pi.product_unit_id
            INNER JOIN catalog.units u ON u.id = pu.unit_id
            INNER JOIN catalog.products cp ON cp.id = pi.product_id
            WHERE pi.purchase_id = @PurchaseId
            ORDER BY pi.id;
            """;

        const string returnsSql = """
            SELECT
                r.id AS PurchaseReturnId,
                r.return_number AS ReturnNumber,
                r.created_at AS CreatedAt,
                r.reason AS Reason,
                r.supplier_return_value AS SupplierReturnValue,
                r.inventory_cost_removed AS InventoryCostRemoved
            FROM purchasing.returns r
            WHERE r.purchase_id = @PurchaseId
            ORDER BY r.created_at, r.id;
            """;

        return await WithConnectionAsync(async (connection, transaction) =>
        {
            var header = await connection.QuerySingleOrDefaultAsync<PurchaseHeaderRow>(
                new CommandDefinition(
                    headerSql,
                    new { query.PurchaseId },
                    transaction,
                    cancellationToken: cancellationToken));
            if (header is null)
            {
                return null;
            }

            var items = (await connection.QueryAsync<PurchaseDocumentLineDto>(
                new CommandDefinition(
                    itemsSql,
                    new { query.PurchaseId },
                    transaction,
                    cancellationToken: cancellationToken))).ToArray();

            var returns = (await connection.QueryAsync<PurchaseReturnSummaryDto>(
                new CommandDefinition(
                    returnsSql,
                    new { query.PurchaseId },
                    transaction,
                    cancellationToken: cancellationToken))).ToArray();

            return new PurchaseDocumentDto(
                header.PurchaseId,
                header.PurchaseNumber,
                header.PurchaseDate,
                header.SupplierId,
                header.SupplierName,
                header.SupplierInvoiceNumber,
                header.Note,
                header.Subtotal,
                header.OtherCharges,
                header.GrandTotal,
                header.Status,
                header.SettlementMode,
                header.CreatedAt,
                items,
                returns);
        }, cancellationToken);
    }


    public async Task<IReadOnlyList<PurchaseReturnHistoryRowDto>> GetReturnHistoryAsync(
        GetPurchaseReturnHistoryQuery query,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                r.id AS PurchaseReturnId,
                r.return_number AS ReturnNumber,
                r.purchase_id AS PurchaseId,
                p.purchase_number AS PurchaseNumber,
                p.supplier_id AS SupplierId,
                s.name AS SupplierName,
                r.created_at AS CreatedAt,
                r.reason AS Reason,
                r.settlement_mode AS SettlementMode,
                r.supplier_return_value AS SupplierReturnValue,
                r.inventory_cost_removed AS InventoryCostRemoved
            FROM purchasing.returns r
            INNER JOIN purchasing.purchases p ON p.id = r.purchase_id
            INNER JOIN parties.suppliers s ON s.id = p.supplier_id
            WHERE (CAST(@PurchaseId AS uuid) IS NULL OR r.purchase_id = @PurchaseId)
              AND (CAST(@SupplierId AS uuid) IS NULL OR p.supplier_id = @SupplierId)
              AND (CAST(@FromUtc AS timestamp with time zone) IS NULL OR r.created_at >= @FromUtc)
              AND (CAST(@ToUtc AS timestamp with time zone) IS NULL OR r.created_at < @ToUtc)
              AND (
                    CAST(@BeforeCreatedAt AS timestamp with time zone) IS NULL
                    OR r.created_at < @BeforeCreatedAt
                    OR (
                        r.created_at = @BeforeCreatedAt
                        AND (CAST(@BeforeReturnId AS uuid) IS NULL OR r.id < @BeforeReturnId)
                    )
                  )
            ORDER BY r.created_at DESC, r.id DESC
            LIMIT @PageSize;
            """;

        return await WithConnectionAsync(async (connection, transaction) =>
        {
            var rows = await connection.QueryAsync<PurchaseReturnHistoryDbRow>(
                new CommandDefinition(
                    sql,
                    new
                    {
                        query.PurchaseId,
                        query.SupplierId,
                        query.FromUtc,
                        query.ToUtc,
                        query.BeforeCreatedAt,
                        query.BeforeReturnId,
                        query.PageSize
                    },
                    transaction,
                    cancellationToken: cancellationToken));

            return rows.Select(row => new PurchaseReturnHistoryRowDto(
                row.PurchaseReturnId,
                row.ReturnNumber,
                row.PurchaseId,
                row.PurchaseNumber,
                row.SupplierId,
                row.SupplierName,
                ToUtcOffset(row.CreatedAt),
                row.Reason,
                (PurchaseReturnSettlementMode)row.SettlementMode,
                row.SupplierReturnValue,
                row.InventoryCostRemoved)).ToArray();
        }, cancellationToken);
    }

    private Task<T> WithConnectionAsync<T>(
        Func<DbConnection, IDbTransaction?, Task<T>> operation,
        CancellationToken cancellationToken) =>
        DbReadConnection.WithAsync(_db, operation, cancellationToken);

    private static DateTimeOffset ToUtcOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed record PurchaseReturnHistoryDbRow(
        Guid PurchaseReturnId,
        string ReturnNumber,
        Guid PurchaseId,
        string PurchaseNumber,
        Guid SupplierId,
        string SupplierName,
        DateTime CreatedAt,
        string Reason,
        int SettlementMode,
        decimal SupplierReturnValue,
        decimal InventoryCostRemoved);

    private sealed record PurchaseHeaderRow(
        Guid PurchaseId,
        string PurchaseNumber,
        DateOnly PurchaseDate,
        Guid SupplierId,
        string SupplierName,
        string SupplierInvoiceNumber,
        string? Note,
        decimal Subtotal,
        decimal OtherCharges,
        decimal GrandTotal,
        EdgeRetails.Domain.Purchasing.PurchaseStatus Status,
        EdgeRetails.Domain.Purchasing.PurchaseSettlementMode SettlementMode,
        DateTimeOffset CreatedAt);
}

internal static class DbReadConnection
{
    public static async Task<T> WithAsync<T>(
        EdgeRetailsDbContext db,
        Func<DbConnection, IDbTransaction?, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;

        if (shouldClose)
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            var transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            return await operation(connection, transaction);
        }
        finally
        {
            if (shouldClose)
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }
}

