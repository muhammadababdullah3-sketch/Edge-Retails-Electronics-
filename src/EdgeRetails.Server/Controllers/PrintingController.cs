using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Production;
using EdgeRetails.Application.Production.Printing;
using EdgeRetails.Server.Middleware;
using Microsoft.AspNetCore.Mvc;

namespace EdgeRetails.Server.Controllers;

[ApiController]
[Route("api/printing")]
public sealed class PrintingController(
    IPhysicalStickerDocumentSource stickerDocuments,
    IProductionDocumentSource productionDocuments,
    IProductionDocumentAuthorizationPolicy documentAuthorization,
    IProductLabelDocumentSource? productLabels = null,
    ProductionAuditCoordinator? audit = null) : ControllerBase
{
    [HttpGet("product-labels/{productUnitId:guid}")]
    public async Task<IActionResult> GetProductLabel([FromRoute] Guid productUnitId, [FromQuery] bool reprint = false, CancellationToken cancellationToken = default)
    {
        var denied = LabelPermission(reprint);
        if (denied is not null)
        {
            return denied;
        }
        try
        {
            var document = await (productLabels ?? throw new InvalidOperationException("Product label source unavailable."))
                .LoadProductLabelAsync(productUnitId, cancellationToken);
            return Ok(document with { IsReprint = reprint });
        }
        catch (InvalidOperationException)
        {
            return NotFound(new { code = "printing.product_label_unavailable", message = "The committed product label could not be loaded." });
        }
    }

    [HttpPost("product-labels/receipts")]
    public async Task<IActionResult> RecordProductLabelReceipt([FromBody] ProductLabelPrintReceipt receipt, CancellationToken cancellationToken = default)
    {
        var denied = LabelPermission(receipt.IsReprint);
        if (denied is not null)
        {
            return denied;
        }
        if (receipt.ProductUnitId == Guid.Empty || receipt.ClientPrintAttemptId == Guid.Empty ||
            receipt.ErrorCode is { Length: > 100 } || (receipt.ErrorCode?.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_')) ?? false))
        {
            return BadRequest(new { code = "printing.receipt_invalid", message = "A valid print attempt is required." });
        }
        try
        {
            var document = await (productLabels ?? throw new InvalidOperationException("Product label source unavailable."))
                .LoadProductLabelAsync(receipt.ProductUnitId, cancellationToken);
            if (audit is null)
            {
                return Ok(new StickerPrintReceiptResult(false, "audit.persist_failed"));
            }
            var result = await audit.AppendAfterSideEffectAsync(new ProductionAuditRecord(
                receipt.Submitted ? receipt.IsReprint ? ProductionAuditEvents.DocumentReprinted : ProductionAuditEvents.DocumentPrinted : ProductionAuditEvents.DocumentPrintFailed,
                DateTimeOffset.UtcNow, "ProductUnitLabel", document.ProductUnitId.ToString(),
                $"ProductCode={document.ProductCode}; WorkstationReportedSubmission={receipt.Submitted}; PhysicalPaperConfirmed=False; Actor={HttpContext.GetCurrentUserId()}; Error={receipt.ErrorCode}", receipt.ClientPrintAttemptId.ToString()));
            return Ok(new StickerPrintReceiptResult(result.Persisted, result.FailureCode));
        }
        catch (InvalidOperationException)
        {
            return NotFound(new { code = "printing.product_label_unavailable", message = "The committed product label could not be loaded." });
        }
    }

    private IActionResult? LabelPermission(bool reprint)
    {
        var denied = this.RequirePermission(PermissionKeys.InventoryManage);
        return denied ?? (reprint ? this.RequirePermission(ProductionPermissionNames.PrintingReprint) : null);
    }

    [HttpPost("stickers/receipts")]
    public async Task<IActionResult> RecordStickerReceipt([FromBody] StickerPrintReceipt receipt, CancellationToken cancellationToken = default)
    {
        var denied = this.RequirePermission(PermissionKeys.InventoryManage);
        if (denied is not null)
        {
            return denied;
        }
        if (receipt.IsReprint)
        {
            denied = this.RequirePermission(ProductionPermissionNames.PrintingReprint);
            if (denied is not null)
            {
                return denied;
            }
        }
        if (receipt.InventoryUnitId == Guid.Empty || receipt.ClientPrintAttemptId == Guid.Empty ||
            receipt.ErrorCode is { Length: > 100 } || (receipt.ErrorCode?.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_')) ?? false))
        {
            return BadRequest(new { code = "printing.receipt_invalid", message = "A valid print attempt is required." });
        }
        try
        {
            var document = await stickerDocuments.LoadStickerDocumentAsync(receipt.InventoryUnitId, receipt.IsReprint, cancellationToken);
            if (audit is null)
            {
                return Ok(new StickerPrintReceiptResult(false, "audit.persist_failed"));
            }
            var outcome = await audit.AppendAfterSideEffectAsync(new ProductionAuditRecord(
                receipt.Submitted ? receipt.IsReprint ? ProductionAuditEvents.DocumentReprinted : ProductionAuditEvents.DocumentPrinted : ProductionAuditEvents.DocumentPrintFailed,
                DateTimeOffset.UtcNow, "PhysicalItemSticker", document.InventoryUnitId.ToString(),
                $"TrackingCode={document.TrackingCode}; WorkstationReportedSubmission={receipt.Submitted}; PhysicalPaperConfirmed=False; Actor={HttpContext.GetCurrentUserId()}; Error={receipt.ErrorCode}",
                receipt.ClientPrintAttemptId.ToString()));
            return Ok(new StickerPrintReceiptResult(outcome.Persisted, outcome.FailureCode));
        }
        catch (InvalidOperationException)
        {
            return NotFound(new { code = "printing.label_document_unavailable", message = "The committed label could not be loaded." });
        }
    }

    [HttpPost("labels/export")]
    public async Task<IActionResult> ExportLabels([FromBody] LabelExportRequest request, CancellationToken cancellationToken = default)
    {
        var denied = this.RequirePermission(PermissionKeys.InventoryManage);
        if (denied is not null)
        {
            return denied;
        }
        if (request.IsReprint)
        {
            denied = this.RequirePermission(ProductionPermissionNames.PrintingReprint);
            if (denied is not null)
            {
                return denied;
            }
        }
        if (request.InventoryUnitIds is null || request.ProductUnitIds is null ||
            request.InventoryUnitIds.Count + request.ProductUnitIds.Count is < 1 or > 500 ||
            request.InventoryUnitIds.Any(x => x == Guid.Empty) || request.ProductUnitIds.Any(x => x == Guid.Empty) ||
            request.InventoryUnitIds.Distinct().Count() != request.InventoryUnitIds.Count ||
            request.ProductUnitIds.Distinct().Count() != request.ProductUnitIds.Count)
        {
            return BadRequest(new { code = "printing.label_selection_invalid", message = "Select 1 to 500 unique committed labels." });
        }
        try
        {
            var exact = new List<PhysicalItemStickerDocument>();
            foreach (var id in request.InventoryUnitIds)
            {
                exact.Add(await stickerDocuments.LoadStickerDocumentAsync(id, request.IsReprint, cancellationToken));
            }
            var products = new List<ProductLabelDocument>();
            foreach (var id in request.ProductUnitIds)
            {
                var productDocument = await (productLabels ?? throw new InvalidOperationException("Product label source unavailable."))
                    .LoadProductLabelAsync(id, cancellationToken);
                products.Add(productDocument with { IsReprint = request.IsReprint });
            }
            var attemptId = request.ClientExportAttemptId == Guid.Empty ? Guid.NewGuid() : request.ClientExportAttemptId;
            var bundle = VectorLabelPdfExporter.Export(exact, products) with
            {
                ExportAttemptId = attemptId,
                GenerationAuditPersisted = false
            };
            if (audit is not null)
            {
                var persisted = true;
                foreach (var document in exact)
                {
                    var outcome = await audit.AppendAfterSideEffectAsync(new ProductionAuditRecord("LABEL_PDF_GENERATED", DateTimeOffset.UtcNow,
                        "PhysicalItemSticker", document.InventoryUnitId.ToString(),
                        $"TrackingCode={document.TrackingCode}; Reprint={request.IsReprint}; Actor={HttpContext.GetCurrentUserId()}; PhysicalPaperConfirmed=False", attemptId.ToString("D")));
                    persisted &= outcome.Persisted;
                }
                foreach (var document in products)
                {
                    var outcome = await audit.AppendAfterSideEffectAsync(new ProductionAuditRecord("LABEL_PDF_GENERATED", DateTimeOffset.UtcNow,
                        "ProductUnitLabel", document.ProductUnitId.ToString(),
                        $"ProductCode={document.ProductCode}; Reprint={request.IsReprint}; Actor={HttpContext.GetCurrentUserId()}; PhysicalPaperConfirmed=False", attemptId.ToString("D")));
                    persisted &= outcome.Persisted;
                }
                bundle = bundle with { GenerationAuditPersisted = persisted };
            }
            return Ok(bundle);
        }
        catch (InvalidOperationException)
        {
            return NotFound(new { code = "printing.label_document_unavailable", message = "A selected committed label could not be loaded." });
        }
        catch (ArgumentException)
        {
            return BadRequest(new { code = "printing.label_layout_invalid", message = "A label cannot fit this supported layout. Check its text and barcode length." });
        }
    }

    [HttpGet("documents/{kind}/{businessDocumentId:guid}")]
    public async Task<IActionResult> GetProductionDocument(
        [FromRoute] string kind,
        [FromRoute] Guid businessDocumentId,
        [FromQuery] bool reprint = false,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<ProductionDocumentKind>(kind, ignoreCase: true, out var documentKind) ||
            !Enum.IsDefined(documentKind))
        {
            return BadRequest(new
            {
                code = "printing.document_kind_invalid",
                message = "The requested production document kind is invalid."
            });
        }

        if (businessDocumentId == Guid.Empty)
        {
            return BadRequest(new
            {
                code = "printing.document_id_invalid",
                message = "A non-empty business document ID is required."
            });
        }

        if (reprint)
        {
            var denied = this.RequirePermission(ProductionPermissionNames.PrintingReprint);
            if (denied is not null)
            {
                return denied;
            }
        }

        try
        {
            await documentAuthorization.EnsureCanPrintAsync(
                documentKind,
                businessDocumentId,
                reprint,
                cancellationToken);
            var document = await productionDocuments.LoadAsync(documentKind, businessDocumentId, cancellationToken);
            return Ok(document);
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (InvalidOperationException)
        {
            return NotFound(new
            {
                code = "printing.document_not_found",
                message = "The requested production document could not be found."
            });
        }
    }

    [HttpGet("stickers/{inventoryUnitId:guid}")]
    public async Task<IActionResult> GetStickerDocument(
        [FromRoute] Guid inventoryUnitId,
        [FromQuery] bool reprint = false,
        CancellationToken cancellationToken = default)
    {
        var denied = this.RequirePermission(PermissionKeys.InventoryManage);
        if (denied is not null)
        {
            return denied;
        }

        if (reprint)
        {
            denied = this.RequirePermission(ProductionPermissionNames.PrintingReprint);
            if (denied is not null)
            {
                return denied;
            }
        }

        try
        {
            var document = await stickerDocuments.LoadStickerDocumentAsync(
                inventoryUnitId,
                reprint,
                cancellationToken);
            return Ok(document);
        }
        catch (InvalidOperationException)
        {
            return NotFound(new
            {
                code = "inventory.unit_not_found",
                message = "The physical inventory unit could not be found for label printing."
            });
        }
    }
}
