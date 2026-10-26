using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Application.Production.Printing;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Domain.Production;
using System.IO;

namespace EdgeRetails.Desktop.PerformanceTests;

public sealed class RemotePhysicalStickerPrintServiceTests
{
    // NEW_COVERAGE: export failures do not invoke a printer or mutate authoritative identity.
    [Fact]
    public async Task ExportPdfAsync_WriteFailurePreservesCommittedIdentityAndDoesNotPrint()
    {
        var unitId = Guid.NewGuid();
        var document = StickerDocument(unitId, "DE1-GFF-FAN18-000001");
        var bundle = VectorLabelPdfExporter.Export([document], []);
        var handler = new RecordingHttpHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/printing/labels/export", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(bundle) });
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:7150") };
        using var apiClient = new DesktopApiClient(httpClient);
        var engine = new RecordingStickerEngine(new PrintJobResult(true, null, null));
        var service = new RemotePhysicalStickerPrintService(apiClient, engine);
        var file = Path.GetTempFileName();
        try
        {
            var result = await service.ExportPdfAsync([unitId], [], file, true);
            Assert.False(result.IsSuccess);
            Assert.Equal("printing.pdf_export_failed", result.Error!.Code);
            Assert.Null(engine.Document);
            Assert.Equal("DE1-GFF-FAN18-000001", document.TrackingCode);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task ExportPdfAsync_ReprintUsesExistingServerIdentityAndPreservesPreviousFiles()
    {
        var unitId = Guid.NewGuid();
        var document = StickerDocument(unitId, "DE1-GFF-FAN18-000002");
        var handler = new RecordingHttpHandler(async (request, cancellationToken) =>
        {
            var payload = await request.Content!.ReadFromJsonAsync<LabelExportRequest>(cancellationToken);
            Assert.True(payload!.IsReprint);
            Assert.Equal(unitId, payload.InventoryUnitIds.Single());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(VectorLabelPdfExporter.Export([document], [])) };
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:7150") };
        using var apiClient = new DesktopApiClient(httpClient);
        var service = new RemotePhysicalStickerPrintService(apiClient, new RecordingStickerEngine(new PrintJobResult(true, null, null)));
        var directory = Path.Combine(Path.GetTempPath(), "edge-label-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Pilot_Exact_Unit_Labels.pdf");
        await File.WriteAllTextAsync(path, "preserved");
        try
        {
            var result = await service.ExportPdfAsync([unitId], [], directory, true);
            Assert.False(result.IsSuccess);
            Assert.Equal("printing.export_exists", result.Error!.Code);
            Assert.Equal("preserved", await File.ReadAllTextAsync(path));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { File.Delete(path); Directory.Delete(directory); }
    }

    [Fact]
    public async Task PrintAsync_LoadsCanonicalIdentityAndUsesDesktopPrinterWithoutChangingIt()
    {
        var unitId = Guid.NewGuid();
        var document = StickerDocument(unitId, "SUP-ITEM-000042");
        var handler = new RecordingHttpHandler((request, _) =>
            Task.FromResult(RespondToOriginalStickerPrint(request, unitId, document, HttpStatusCode.OK)));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:7150") };
        using var apiClient = new DesktopApiClient(httpClient);
        var engine = new RecordingStickerEngine(new PrintJobResult(true, null, null));
        var service = new RemotePhysicalStickerPrintService(apiClient, engine);

        var result = await service.PrintAsync([unitId], "Receipt Printer", isReprint: false);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value!.SucceededCount);
        Assert.Equal(0, result.Value.FailedCount);
        Assert.Equal("Receipt Printer", engine.PrinterName);
        Assert.Equal(document.InventoryUnitId, engine.Document!.InventoryUnitId);
        Assert.Equal(document.TrackingCode, engine.Document.TrackingCode);
        Assert.Equal(document.ItemSequence, engine.Document.ItemSequence);
        Assert.Equal(1, engine.Copies);
        Assert.Equal(unitId, document.InventoryUnitId);
    }

    [Fact]
    public async Task PrintAsync_ReportsAmbiguousPrinterOutcomeWithoutClaimingSuccess()
    {
        var unitId = Guid.NewGuid();
        // HARNESS_CORRECTION: keep the canonical GET and acknowledge the new separate audit receipt.
        var handler = new RecordingHttpHandler((request, _) =>
            Task.FromResult(RespondToOriginalStickerPrint(
                request,
                unitId,
                StickerDocument(unitId, "SUP-ITEM-000043"),
                HttpStatusCode.OK)));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:7150") };
        using var apiClient = new DesktopApiClient(httpClient);
        var service = new RemotePhysicalStickerPrintService(
            apiClient,
            new RecordingStickerEngine(new PrintJobResult(
                false,
                "print.outcome_unknown",
                "Check the printer before reprinting.")));

        var result = await service.PrintAsync([unitId], "Receipt Printer", isReprint: false);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Value!.SucceededCount);
        Assert.Equal(1, result.Value.FailedCount);
        Assert.Equal("print.outcome_unknown", result.Value.JobResults.Single().ErrorCode);
    }

    [Fact]
    public async Task PrintAsync_AuditFailureDoesNotTurnAcceptedSubmissionIntoPrintFailureOrRetry()
    {
        var unitId = Guid.NewGuid();
        var handler = new RecordingHttpHandler((request, _) =>
            Task.FromResult(RespondToOriginalStickerPrint(
                request,
                unitId,
                StickerDocument(unitId, "SUP-ITEM-000044"),
                HttpStatusCode.ServiceUnavailable)));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:7150") };
        using var api = new DesktopApiClient(http);
        var engine = new RecordingStickerEngine(new PrintJobResult(true, null, null));
        var result = await new RemotePhysicalStickerPrintService(api, engine).PrintAsync([unitId], null, false);
        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.JobResults.Single().Succeeded);
        Assert.False(result.Value.JobResults.Single().AuditPersisted);
        Assert.Equal(1, result.Value.SucceededCount);
        Assert.Equal(3, handler.RequestCount);
        Assert.Equal(1, engine.InvocationCount);
    }

    [Fact]
    public async Task PrintAsync_EmptySelectionDoesNotCallServerOrPrinter()
    {
        var handler = new RecordingHttpHandler((_, _) => throw new InvalidOperationException("No request expected."));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:7150") };
        using var apiClient = new DesktopApiClient(httpClient);
        var engine = new RecordingStickerEngine(new PrintJobResult(true, null, null));
        var service = new RemotePhysicalStickerPrintService(apiClient, engine);

        var result = await service.PrintAsync([], null, isReprint: false);

        Assert.False(result.IsSuccess);
        Assert.Equal("printing.no_units_selected", result.Error!.Code);
        Assert.Null(engine.Document);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task PrintAsync_DuplicateUnitIsRejectedBeforeAnyLabelIsSubmitted()
    {
        var unitId = Guid.NewGuid();
        var handler = new RecordingHttpHandler((_, _) => throw new InvalidOperationException("No request expected."));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:7150") };
        using var apiClient = new DesktopApiClient(httpClient);
        var engine = new RecordingStickerEngine(new PrintJobResult(true, null, null));
        var service = new RemotePhysicalStickerPrintService(apiClient, engine);

        var result = await service.PrintAsync([unitId, unitId], null, isReprint: false);

        Assert.False(result.IsSuccess);
        Assert.Equal("printing.duplicate_unit", result.Error!.Code);
        Assert.Null(engine.Document);
        Assert.Equal(0, handler.RequestCount);
    }

    private static HttpResponseMessage RespondToOriginalStickerPrint(
        HttpRequestMessage request,
        Guid unitId,
        PhysicalItemStickerDocument document,
        HttpStatusCode receiptStatus)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Post && path == "/api/printing/label-intents/claim-by-unit")
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new PhysicalLabelPrintIntentSummary(
                    Guid.NewGuid(),
                    unitId,
                    Guid.Empty,
                    PhysicalLabelPrintIntentKind.Original,
                    PhysicalLabelPrintIntentStatus.Dispatched,
                    1,
                    Environment.MachineName))
            };
        }

        if (request.Method == HttpMethod.Post && path == "/api/printing/stickers/receipts")
        {
            return new HttpResponseMessage(receiptStatus)
            {
                Content = receiptStatus == HttpStatusCode.OK
                    ? JsonContent.Create(new StickerPrintReceiptResult(true, null))
                    : null
            };
        }

        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"/api/printing/stickers/{unitId:D}?reprint=false", request.RequestUri!.PathAndQuery);
        Assert.Equal(TerminalProtocol.CurrentProtocolVersion, request.Headers.GetValues("X-Protocol-Version").Single());
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(document) };
    }

    private static PhysicalItemStickerDocument StickerDocument(Guid id, string trackingCode) => new(
        id,
        trackingCode,
        "Edge Retails",
        "Test product",
        "Model A",
        "SKU-1",
        42,
        "SER-42",
        null,
        null,
        "Edge Retails",
        100m,
        false,
        DateTimeOffset.UtcNow);

    private sealed class RecordingHttpHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return responder(request, cancellationToken);
        }
    }

    private sealed class RecordingStickerEngine(PrintJobResult result) : IPhysicalStickerPrintEngine
    {
        public PhysicalItemStickerDocument? Document { get; private set; }
        public string? PrinterName { get; private set; }
        public int Copies { get; private set; }
        public int InvocationCount { get; private set; }

        public Task<PrintJobResult> PrintStickerAsync(
            PhysicalItemStickerDocument document,
            string? printerName = null,
            int copies = 1,
            CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            Document = document;
            PrinterName = printerName;
            Copies = copies;
            return Task.FromResult(result);
        }
    }
}
