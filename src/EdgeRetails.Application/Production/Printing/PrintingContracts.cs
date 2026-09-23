namespace EdgeRetails.Application.Production.Printing;

public enum ProductionDocumentKind
{
    PosSaleReceipt,
    SaleReturnReceipt,
    PurchaseDocument,
    PurchaseReturnDocument,
    ThakaMaterialChallan,
    ThakaPaymentReceipt,
    FinalSettlementStatement
}

public enum PaperKind
{
    Thermal58Mm,
    Thermal80Mm,
    A4
}

public enum PrintRequestMode
{
    Initial,
    Retry,
    Reprint
}

public enum PrintJobState
{
    Prepared,
    Printing,
    Succeeded,
    Failed,
    Cancelled,
    OutcomeUnknown
}

public sealed record ProductionDocumentLine(
    string Description,
    decimal Quantity,
    string? Unit,
    decimal UnitPrice,
    decimal LineTotal,
    string? TrackingReference = null);

public sealed record ProductionDocumentTotal(string Label, decimal Amount, bool Emphasized = false);

public sealed record ProductionDocument(
    ProductionDocumentKind Kind,
    Guid BusinessDocumentId,
    string DocumentNumber,
    DateTimeOffset IssuedAt,
    string ShopName,
    string? ShopAddress,
    string? ShopPhone,
    string? PartyName,
    string Title,
    IReadOnlyList<ProductionDocumentLine> Lines,
    IReadOnlyList<ProductionDocumentTotal> Totals,
    IReadOnlyList<string> Notes,
    string? Footer,
    string? CopyLabel = null);

public sealed record PrinterProfile(
    string ProfileName,
    string PrinterName,
    PaperKind Paper,
    int Copies,
    bool ShowPreviewBeforePrint);

public sealed record PrinterProfileValidationResult(bool Supported, string? ErrorCode = null, string? Message = null)
{
    public static PrinterProfileValidationResult Valid { get; } = new(true);
}

public sealed record PrintDocumentCommand(
    ProductionDocumentKind Kind,
    Guid BusinessDocumentId,
    string PrinterProfileName,
    bool IsReprint,
    string? CorrelationId,
    string? PrintJobId = null,
    PrintRequestMode? RequestMode = null);

public sealed record PrintJobResult(
    bool Succeeded,
    string? ErrorCode,
    string? ErrorMessage,
    string? PrintJobId = null,
    bool AlreadyCompleted = false,
    bool AuditPersisted = true);

public sealed record PrintJobRecord(
    string PrintJobId,
    ProductionDocumentKind Kind,
    Guid BusinessDocumentId,
    string PrinterProfileName,
    PrintRequestMode Mode,
    PrintJobState State,
    int AttemptNumber,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? LastErrorCode = null);

public interface IProductionDocumentSource
{
    Task<ProductionDocument> LoadAsync(
        ProductionDocumentKind kind,
        Guid businessDocumentId,
        CancellationToken cancellationToken = default);
}

public interface IPrinterProfileStore
{
    Task<PrinterProfile?> GetAsync(string profileName, CancellationToken cancellationToken = default);
    Task SaveAsync(PrinterProfile profile, CancellationToken cancellationToken = default);
}

public interface IPrinterProfileValidator
{
    Task<PrinterProfileValidationResult> ValidateAsync(PrinterProfile profile, CancellationToken cancellationToken = default);
}

public interface IPrintJobStore
{
    Task<PrintJobRecord?> GetAsync(string printJobId, CancellationToken cancellationToken = default);
    Task CreateAsync(PrintJobRecord record, CancellationToken cancellationToken = default);
    Task<bool> TryTransitionAsync(
        string printJobId,
        PrintJobState expectedState,
        PrintJobState newState,
        int attemptNumber,
        string? lastErrorCode,
        CancellationToken cancellationToken = default);
}

public interface IProductionPrintEngine
{
    Task<PrintJobResult> PrintAsync(
        ProductionDocument document,
        PrinterProfile profile,
        CancellationToken cancellationToken = default);
}
