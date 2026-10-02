using EdgeRetails.Domain.Common;

namespace EdgeRetails.Application.Production.Printing;

public sealed record PhysicalItemStickerDocument(
    Guid InventoryUnitId,
    string TrackingCode,
    string CompanyName,
    string ProductName,
    string? ModelName,
    string ProductCode,
    long ItemSequence,
    string? SerialNumber,
    string? Imei1,
    string? Imei2,
    string? ShopName,
    decimal? RetailSalePrice,
    bool IsReprint,
    DateTimeOffset CreatedAt,
    string? SupplierName = null,
    string? SupplierCode = null,
    string? PurchaseNumber = null,
    Guid? SourcePurchaseItemId = null,
    Guid? SupplierProductId = null,
    Guid ClientPrintAttemptId = default)
{
    public string BarcodePayload => TrackingCode;
    public string BarcodeModules => Code128Encoder.EncodeToModules(TrackingCode);
    public string DisplayPhysicalSku => TrackingCode;
}

public sealed record PhysicalStickerPrintJobRecord(
    string PrintJobId,
    Guid InventoryUnitId,
    string TrackingCode,
    PrintRequestMode Mode,
    PrintJobState State,
    int AttemptNumber,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? LastErrorCode = null);

public interface IPhysicalStickerDocumentSource
{
    Task<PhysicalItemStickerDocument> LoadStickerDocumentAsync(
        Guid inventoryUnitId,
        bool isReprint = false,
        CancellationToken cancellationToken = default);
}

public interface IPhysicalStickerPrintEngine
{
    Task<PrintJobResult> PrintStickerAsync(
        PhysicalItemStickerDocument document,
        string? printerName = null,
        int copies = 1,
        CancellationToken cancellationToken = default);
}

public sealed record PrintPhysicalStickersCommand(
    IReadOnlyList<Guid> InventoryUnitIds,
    string? PrinterName,
    bool IsReprint,
    Guid RequestedBy,
    string? CorrelationId = null,
    PrintRequestMode? RequestMode = null);

public sealed record PrintPhysicalStickersResult(
    IReadOnlyList<PrintJobResult> JobResults,
    int TotalRequested,
    int SucceededCount,
    int FailedCount);
