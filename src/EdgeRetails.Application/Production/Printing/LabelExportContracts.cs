namespace EdgeRetails.Application.Production.Printing;

public sealed record ProductLabelDocument(
    Guid ProductUnitId, Guid ProductId, string ProductName, string CompanyName,
    string? ModelName, string ProductCode, string UnitName, string? UnitBarcode, decimal? RetailSalePrice, bool IsReprint = false,
    Guid ClientPrintAttemptId = default);

public interface IProductLabelDocumentSource
{
    Task<ProductLabelDocument> LoadProductLabelAsync(Guid productUnitId, CancellationToken cancellationToken = default);
}
public interface IProductLabelPrintEngine
{
    Task<PrintJobResult> PrintProductLabelAsync(ProductLabelDocument document, string? printerName = null, int copies = 1, CancellationToken cancellationToken = default);
}

public sealed record LabelExportRequest(IReadOnlyList<Guid> InventoryUnitIds, IReadOnlyList<Guid> ProductUnitIds, bool IsReprint = false,
    Guid ClientExportAttemptId = default);
public sealed record LabelManifestRecord(
    Guid? InventoryUnitId, Guid? ProductUnitId, string ProductName, string ProductCode,
    string? SupplierName, string? SupplierCode, string? TrackingCode, string? SerialNumber,
    string? PurchaseNumber, Guid? SourcePurchaseItemId, Guid? SupplierProductId,
    string? BarcodePayload, string DocumentName, int PageNumber, int LabelIndex);
public sealed record LabelExportBundle(byte[] ExactUnitPdf, byte[] ProductPdf, byte[] ManifestPdf, IReadOnlyList<LabelManifestRecord> Manifest,
    Guid ExportAttemptId = default, bool GenerationAuditPersisted = true);
public sealed record StickerPrintReceipt(Guid InventoryUnitId, bool IsReprint, bool Submitted, string? ErrorCode, Guid ClientPrintAttemptId);
public sealed record StickerPrintReceiptResult(bool AuditPersisted, string? WarningCode);
public sealed record ProductLabelPrintReceipt(Guid ProductUnitId, bool IsReprint, bool Submitted, string? ErrorCode, Guid ClientPrintAttemptId);
