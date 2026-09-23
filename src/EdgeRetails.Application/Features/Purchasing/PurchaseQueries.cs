using EdgeRetails.Domain.Purchasing;

namespace EdgeRetails.Application.Features.Purchasing;

public sealed record GetPurchaseHistoryQuery(
    DateOnly? FromDate = null,
    DateOnly? ToDate = null,
    Guid? SupplierId = null,
    string? Search = null,
    int PageSize = 50,
    DateOnly? BeforePurchaseDate = null,
    Guid? BeforePurchaseId = null);

public sealed record PurchaseHistoryRowDto
{
    public Guid PurchaseId { get; init; }
    public string PurchaseNumber { get; init; } = string.Empty;
    public DateOnly PurchaseDate { get; init; }
    public Guid? SupplierId { get; init; }
    public string SupplierName { get; init; } = string.Empty;
    public string SupplierInvoiceNumber { get; init; } = string.Empty;
    public decimal Subtotal { get; init; }
    public decimal OtherCharges { get; init; }
    public decimal GrandTotal { get; init; }
    public PurchaseStatus Status { get; init; }
    public PurchaseSettlementMode SettlementMode { get; init; }
    public decimal SupplierReturnValue { get; init; }
    public int ItemCount { get; init; }

    public PurchaseHistoryRowDto() { }

    public PurchaseHistoryRowDto(
        Guid PurchaseId,
        string PurchaseNumber,
        DateOnly PurchaseDate,
        string SupplierName,
        string SupplierInvoiceNumber,
        decimal GrandTotal,
        PurchaseStatus Status,
        PurchaseSettlementMode SettlementMode,
        decimal SupplierReturnValue,
        decimal Subtotal = 0m,
        decimal OtherCharges = 0m,
        int ItemCount = 0,
        Guid? SupplierId = null)
    {
        this.PurchaseId = PurchaseId;
        this.PurchaseNumber = PurchaseNumber;
        this.PurchaseDate = PurchaseDate;
        this.SupplierName = SupplierName;
        this.SupplierInvoiceNumber = SupplierInvoiceNumber;
        this.GrandTotal = GrandTotal;
        this.Status = Status;
        this.SettlementMode = SettlementMode;
        this.SupplierReturnValue = SupplierReturnValue;
        this.Subtotal = Subtotal;
        this.OtherCharges = OtherCharges;
        this.ItemCount = ItemCount;
        this.SupplierId = SupplierId;
    }
}

public sealed record PurchaseDocumentLineDto(
    Guid PurchaseItemId,
    Guid ProductId,
    string ProductName,
    string? Sku,
    Guid ProductUnitId,
    string UnitSymbol,
    bool IsSerialized,
    decimal EnteredQuantity,
    decimal FactorToBaseSnapshot,
    decimal BaseQuantity,
    decimal EnteredUnitCost,
    decimal AllocatedOtherCost,
    decimal EffectiveBaseUnitCost,
    decimal EffectiveLineCost,
    decimal SalePriceAtPurchase,
    decimal ReturnedBaseQuantity,
    decimal EligibleBaseReturnQuantity);

public sealed record PurchaseReturnSummaryDto(
    Guid PurchaseReturnId,
    string ReturnNumber,
    DateTimeOffset CreatedAt,
    string Reason,
    decimal SupplierReturnValue,
    decimal InventoryCostRemoved);

public sealed record PurchaseDocumentDto(
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
    PurchaseStatus Status,
    PurchaseSettlementMode SettlementMode,
    DateTimeOffset CreatedAt,
    IReadOnlyList<PurchaseDocumentLineDto> Items,
    IReadOnlyList<PurchaseReturnSummaryDto> Returns);

public sealed record GetPurchaseDocumentQuery(Guid PurchaseId);
public sealed record GetPurchaseDetailQuery(Guid PurchaseId);

public sealed record GetSupplierPurchaseHistoryQuery(
    Guid SupplierId,
    DateOnly? FromDate = null,
    DateOnly? ToDate = null,
    string? Search = null,
    int PageSize = 50,
    DateOnly? BeforePurchaseDate = null,
    Guid? BeforePurchaseId = null);

public sealed record GetPurchaseReturnHistoryQuery(
    Guid? PurchaseId = null,
    Guid? SupplierId = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    int PageSize = 100,
    DateTimeOffset? BeforeCreatedAt = null,
    Guid? BeforeReturnId = null);

public sealed record PurchaseReturnHistoryRowDto(
    Guid PurchaseReturnId,
    string ReturnNumber,
    Guid PurchaseId,
    string PurchaseNumber,
    Guid SupplierId,
    string SupplierName,
    DateTimeOffset CreatedAt,
    string Reason,
    PurchaseReturnSettlementMode SettlementMode,
    decimal SupplierReturnValue,
    decimal InventoryCostRemoved);

public interface IPurchasingReadService
{
    Task<IReadOnlyList<PurchaseHistoryRowDto>> GetHistoryAsync(
        GetPurchaseHistoryQuery query,
        CancellationToken cancellationToken);

    Task<PurchaseDocumentDto?> GetDocumentAsync(
        GetPurchaseDocumentQuery query,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PurchaseReturnHistoryRowDto>> GetReturnHistoryAsync(
        GetPurchaseReturnHistoryQuery query,
        CancellationToken cancellationToken);
}

public sealed class GetPurchaseHistoryHandler
{
    private readonly IPurchasingReadService _reads;

    public GetPurchaseHistoryHandler(IPurchasingReadService reads) => _reads = reads;

    public Task<IReadOnlyList<PurchaseHistoryRowDto>> HandleAsync(
        GetPurchaseHistoryQuery query,
        CancellationToken cancellationToken) =>
        _reads.GetHistoryAsync(
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

public sealed class GetPurchaseDocumentHandler
{
    private readonly IPurchasingReadService _reads;

    public GetPurchaseDocumentHandler(IPurchasingReadService reads) => _reads = reads;

    public Task<PurchaseDocumentDto?> HandleAsync(
        GetPurchaseDocumentQuery query,
        CancellationToken cancellationToken) =>
        _reads.GetDocumentAsync(query, cancellationToken);
}


public sealed class GetPurchaseDetailHandler
{
    private readonly IPurchasingReadService _reads;

    public GetPurchaseDetailHandler(IPurchasingReadService reads) => _reads = reads;

    public Task<PurchaseDocumentDto?> HandleAsync(
        GetPurchaseDetailQuery query,
        CancellationToken cancellationToken) =>
        _reads.GetDocumentAsync(
            new GetPurchaseDocumentQuery(query.PurchaseId),
            cancellationToken);
}

public sealed class GetSupplierPurchaseHistoryHandler
{
    private readonly IPurchasingReadService _reads;

    public GetSupplierPurchaseHistoryHandler(IPurchasingReadService reads) => _reads = reads;

    public Task<IReadOnlyList<PurchaseHistoryRowDto>> HandleAsync(
        GetSupplierPurchaseHistoryQuery query,
        CancellationToken cancellationToken) =>
        _reads.GetHistoryAsync(
            new GetPurchaseHistoryQuery(
                query.FromDate,
                query.ToDate,
                query.SupplierId,
                NormalizeText(query.Search),
                Math.Clamp(query.PageSize, 1, 200),
                query.BeforePurchaseDate,
                query.BeforePurchaseId),
            cancellationToken);

    private static string? NormalizeText(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}

public sealed class GetPurchaseReturnHistoryHandler
{
    private readonly IPurchasingReadService _reads;

    public GetPurchaseReturnHistoryHandler(IPurchasingReadService reads) => _reads = reads;

    public Task<IReadOnlyList<PurchaseReturnHistoryRowDto>> HandleAsync(
        GetPurchaseReturnHistoryQuery query,
        CancellationToken cancellationToken) =>
        _reads.GetReturnHistoryAsync(
            query with { PageSize = Math.Clamp(query.PageSize, 1, 500) },
            cancellationToken);
}

