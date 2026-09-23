using EdgeRetails.Domain.Sales;

namespace EdgeRetails.Application.Features.Sales;

public sealed record GetSalesHistoryQuery(
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    string? Search = null,
    int PageSize = 50,
    DateTimeOffset? BeforeCompletedAt = null,
    Guid? BeforeSaleId = null);

public sealed record SalesHistoryRowDto(
    Guid SaleId,
    string InvoiceNumber,
    DateTimeOffset CompletedAt,
    string CustomerName,
    decimal GrandTotal,
    SalePaymentMethod PaymentMethod,
    int ItemCount,
    decimal ReturnedAmount);

public sealed record SaleDetailItemDto(
    Guid SaleItemId,
    Guid ProductId,
    string ProductName,
    string? Sku,
    decimal EnteredQuantity,
    decimal FactorToBaseSnapshot,
    decimal BaseQuantity,
    decimal UnitPrice,
    decimal GrossLineTotal,
    decimal AllocatedInvoiceDiscount,
    decimal NetLineTotal);

public sealed record SaleReturnSummaryDto(
    Guid SaleReturnId,
    string ReturnNumber,
    DateTimeOffset CreatedAt,
    string ReasonCode,
    RefundMethod RefundMethod,
    decimal RefundAmount);

public sealed record SaleReturnItemDetailDto(
    Guid SaleReturnId,
    Guid SaleItemId,
    Guid ProductId,
    string? Sku,
    decimal EnteredQuantity,
    decimal RefundAmount);

public sealed record SaleDetailDto(
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
    string? ReceiptTemplateSnapshot,
    IReadOnlyList<SaleDetailItemDto> Items,
    IReadOnlyList<SaleReturnSummaryDto> Returns,
    IReadOnlyList<SaleReturnItemDetailDto> ReturnItems);

public sealed record GetSaleDetailQuery(Guid SaleId);

public sealed record GetQuotationsQuery(
    QuotationStatus? Status = null,
    string? Search = null,
    int PageSize = 50,
    DateOnly? BeforeQuotationDate = null,
    Guid? BeforeQuotationId = null);

public sealed record QuotationListRowDto(
    Guid QuotationId,
    string QuotationNumber,
    DateOnly QuotationDate,
    DateOnly? ValidUntil,
    QuotationStatus Status,
    string CustomerName,
    decimal GrandTotal,
    Guid? ConvertedSaleId);

public sealed record QuotationDetailItemDto(
    Guid ProductId,
    string ProductName,
    string? Sku,
    Guid SelectedUnitId,
    decimal EnteredQuantity,
    decimal FactorToBaseSnapshot,
    decimal BaseQuantity,
    decimal QuotedUnitPrice,
    decimal LineTotal);

public sealed record QuotationDetailDto(
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
    Guid? ConvertedSaleId,
    IReadOnlyList<QuotationDetailItemDto> Items);

public sealed record GetQuotationDetailQuery(Guid QuotationId);

public sealed record GetSaleReturnHistoryQuery(
    Guid? SaleId = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    int PageSize = 100,
    DateTimeOffset? BeforeCreatedAt = null,
    Guid? BeforeReturnId = null);

public sealed record SaleReturnHistoryRowDto(
    Guid SaleReturnId,
    string ReturnNumber,
    Guid SaleId,
    string InvoiceNumber,
    DateTimeOffset CreatedAt,
    string ReasonCode,
    string? ReasonNote,
    RefundMethod RefundMethod,
    decimal RefundAmount);

public interface ISalesReadService
{
    Task<IReadOnlyList<SalesHistoryRowDto>> GetHistoryAsync(
        GetSalesHistoryQuery query,
        CancellationToken cancellationToken);

    Task<SaleDetailDto?> GetDetailAsync(
        GetSaleDetailQuery query,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<QuotationListRowDto>> GetQuotationsAsync(
        GetQuotationsQuery query,
        CancellationToken cancellationToken);

    Task<QuotationDetailDto?> GetQuotationDetailAsync(
        GetQuotationDetailQuery query,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SaleReturnHistoryRowDto>> GetReturnHistoryAsync(
        GetSaleReturnHistoryQuery query,
        CancellationToken cancellationToken);
}

public sealed class GetSalesHistoryHandler
{
    private readonly ISalesReadService _reads;

    public GetSalesHistoryHandler(ISalesReadService reads) => _reads = reads;

    public Task<IReadOnlyList<SalesHistoryRowDto>> HandleAsync(
        GetSalesHistoryQuery query,
        CancellationToken cancellationToken) =>
        _reads.GetHistoryAsync(Normalize(query), cancellationToken);

    private static GetSalesHistoryQuery Normalize(GetSalesHistoryQuery query) =>
        query with
        {
            Search = NormalizeText(query.Search),
            PageSize = Math.Clamp(query.PageSize, 1, 200)
        };

    private static string? NormalizeText(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}

public sealed class GetSaleDetailHandler
{
    private readonly ISalesReadService _reads;

    public GetSaleDetailHandler(ISalesReadService reads) => _reads = reads;

    public Task<SaleDetailDto?> HandleAsync(
        GetSaleDetailQuery query,
        CancellationToken cancellationToken) =>
        _reads.GetDetailAsync(query, cancellationToken);
}

public sealed class GetQuotationsHandler
{
    private readonly ISalesReadService _reads;

    public GetQuotationsHandler(ISalesReadService reads) => _reads = reads;

    public Task<IReadOnlyList<QuotationListRowDto>> HandleAsync(
        GetQuotationsQuery query,
        CancellationToken cancellationToken) =>
        _reads.GetQuotationsAsync(
            query with
            {
                Search = NormalizeText(query.Search),
                PageSize = Math.Clamp(query.PageSize, 1, 200)
            },
            cancellationToken);

    private static string? NormalizeText(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}

public sealed class GetQuotationDetailHandler
{
    private readonly ISalesReadService _reads;

    public GetQuotationDetailHandler(ISalesReadService reads) => _reads = reads;

    public Task<QuotationDetailDto?> HandleAsync(
        GetQuotationDetailQuery query,
        CancellationToken cancellationToken) =>
        _reads.GetQuotationDetailAsync(query, cancellationToken);
}


public sealed class GetSaleReturnHistoryHandler
{
    private readonly ISalesReadService _reads;

    public GetSaleReturnHistoryHandler(ISalesReadService reads) => _reads = reads;

    public Task<IReadOnlyList<SaleReturnHistoryRowDto>> HandleAsync(
        GetSaleReturnHistoryQuery query,
        CancellationToken cancellationToken) =>
        _reads.GetReturnHistoryAsync(
            query with { PageSize = Math.Clamp(query.PageSize, 1, 500) },
            cancellationToken);
}
