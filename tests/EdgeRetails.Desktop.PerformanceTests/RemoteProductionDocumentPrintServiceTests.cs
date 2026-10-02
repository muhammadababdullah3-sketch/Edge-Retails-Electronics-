using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EdgeRetails.Application.Production.Printing;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Infrastructure.Production.Printing;

namespace EdgeRetails.Desktop.PerformanceTests;

public sealed class RemoteProductionDocumentPrintServiceTests
{
    [Fact]
    public async Task PrintAsync_FetchesCanonicalDocumentAndPrintsLocally()
    {
        var document = CreateDocument();
        var handler = RespondWith(document);
        using var apiClient = CreateApiClient(handler);
        var engine = new RecordingProductionPrintEngine(new PrintJobResult(true, null, null));
        var service = new RemoteProductionDocumentPrintService(apiClient, engine);
        var profile = new PrinterProfile("Receipt", "Local Receipt Printer", PaperKind.Thermal80Mm, 1, false);

        var result = await service.PrintAsync(document.Kind, document.BusinessDocumentId, profile, isReprint: false);

        Assert.True(result.Succeeded);
        Assert.Equal($"/api/printing/documents/{document.Kind}/{document.BusinessDocumentId:D}?reprint=false", handler.RequestPath);
        Assert.NotNull(engine.Document);
        Assert.Equal(document.Kind, engine.Document.Kind);
        Assert.Equal(document.BusinessDocumentId, engine.Document.BusinessDocumentId);
        Assert.Equal(document.DocumentNumber, engine.Document.DocumentNumber);
        Assert.Same(profile, engine.Profile);
        Assert.Equal(1, engine.CallCount);

        var repeatedInitial = await service.PrintAsync(document.Kind, document.BusinessDocumentId, profile, isReprint: false);

        Assert.True(repeatedInitial.Succeeded);
        Assert.True(repeatedInitial.AlreadyCompleted);
        Assert.Equal(result.PrintJobId, repeatedInitial.PrintJobId);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(1, engine.CallCount);
    }

    [Fact]
    public async Task PrintAsync_ExplicitReprintIsPassedToCanonicalDocumentRoute()
    {
        var document = CreateDocument();
        var handler = RespondWith(document);
        using var apiClient = CreateApiClient(handler);
        var engine = new RecordingProductionPrintEngine(new PrintJobResult(true, null, null));
        var service = new RemoteProductionDocumentPrintService(apiClient, engine);

        var result = await service.PrintAsync(
            document.Kind,
            document.BusinessDocumentId,
            new PrinterProfile("Receipt", "Local Receipt Printer", PaperKind.A4, 1, true),
            isReprint: true);

        Assert.True(result.Succeeded);
        Assert.Equal($"/api/printing/documents/{document.Kind}/{document.BusinessDocumentId:D}?reprint=true", handler.RequestPath);
        Assert.Equal(1, engine.CallCount);
    }

    [Fact]
    public async Task PrintAsync_PreservesConfirmedLocalPrinterFailure()
    {
        var document = CreateDocument();
        using var apiClient = CreateApiClient(RespondWith(document));
        var engine = new RecordingProductionPrintEngine(new PrintJobResult(
            false,
            "print.printer_unavailable",
            "The configured printer is offline."));
        var service = new RemoteProductionDocumentPrintService(apiClient, engine);

        var result = await service.PrintAsync(
            document.Kind,
            document.BusinessDocumentId,
            new PrinterProfile("Receipt", "Unavailable", PaperKind.Thermal80Mm, 1, false),
            isReprint: false);

        Assert.False(result.Succeeded);
        Assert.Equal("print.printer_unavailable", result.ErrorCode);
        Assert.Equal("The configured printer is offline.", result.ErrorMessage);
    }

    [Fact]
    public async Task PrintAsync_ReportsOutcomeUnknownWhenLocalSubmissionThrows()
    {
        var document = CreateDocument();
        using var apiClient = CreateApiClient(RespondWith(document));
        var engine = new RecordingProductionPrintEngine(exception: new InvalidOperationException("Spooler disconnected."));
        var service = new RemoteProductionDocumentPrintService(apiClient, engine);

        var result = await service.PrintAsync(
            document.Kind,
            document.BusinessDocumentId,
            new PrinterProfile("Receipt", "Local Receipt Printer", PaperKind.Thermal80Mm, 1, false),
            isReprint: false);

        Assert.False(result.Succeeded);
        Assert.Equal("print.outcome_unknown", result.ErrorCode);
        Assert.Contains("Check the physical printer", result.ErrorMessage);
    }

    [Fact]
    public async Task PrintAsync_UnknownOutcomeSurvivesRestartAndBlocksAnotherSubmission()
    {
        var document = CreateDocument();
        var temp = Directory.CreateTempSubdirectory("edge-print-state-");
        try
        {
            var storePath = Path.Combine(temp.FullName, "print-jobs.json");
            var firstHandler = RespondWith(document);
            using var firstApi = CreateApiClient(firstHandler);
            var firstEngine = new RecordingProductionPrintEngine(exception: new InvalidOperationException("Spooler disconnected."));
            var firstService = new RemoteProductionDocumentPrintService(
                firstApi,
                firstEngine,
                new JsonPrintJobStore(storePath));
            var profile = new PrinterProfile("Receipt", "Local Receipt Printer", PaperKind.Thermal80Mm, 1, false);

            var firstResult = await firstService.PrintAsync(document.Kind, document.BusinessDocumentId, profile, isReprint: false);

            Assert.False(firstResult.Succeeded);
            Assert.Equal("print.outcome_unknown", firstResult.ErrorCode);
            Assert.NotNull(firstResult.PrintJobId);
            Assert.Equal(1, firstEngine.CallCount);

            var secondHandler = RespondWith(document);
            using var secondApi = CreateApiClient(secondHandler);
            var secondEngine = new RecordingProductionPrintEngine(new PrintJobResult(true, null, null));
            var afterRestart = new RemoteProductionDocumentPrintService(
                secondApi,
                secondEngine,
                new JsonPrintJobStore(storePath));

            var retryResult = await afterRestart.PrintAsync(document.Kind, document.BusinessDocumentId, profile, isReprint: false);

            Assert.False(retryResult.Succeeded);
            Assert.Equal("print.outcome_unknown", retryResult.ErrorCode);
            Assert.Equal(firstResult.PrintJobId, retryResult.PrintJobId);
            Assert.Equal(0, secondHandler.RequestCount);
            Assert.Equal(0, secondEngine.CallCount);
            Assert.Equal(PrintJobState.OutcomeUnknown,
                (await new JsonPrintJobStore(storePath).GetAsync(firstResult.PrintJobId!))!.State);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task PrintAsync_ExplicitReprintCreatesNewDurableJobAndRequestsReprintDocument()
    {
        var document = CreateDocument();
        var handler = RespondWith(document);
        using var apiClient = CreateApiClient(handler);
        var temp = Directory.CreateTempSubdirectory("edge-print-reprint-");
        try
        {
            var store = new JsonPrintJobStore(Path.Combine(temp.FullName, "print-jobs.json"));
            var engine = new RecordingProductionPrintEngine(new PrintJobResult(true, null, null));
            var service = new RemoteProductionDocumentPrintService(apiClient, engine, store);
            var profile = new PrinterProfile("Receipt", "Local Receipt Printer", PaperKind.Thermal80Mm, 1, false);

            var initial = await service.PrintAsync(document.Kind, document.BusinessDocumentId, profile, isReprint: false);
            var reprint = await service.PrintAsync(document.Kind, document.BusinessDocumentId, profile, isReprint: true);

            Assert.True(initial.Succeeded);
            Assert.True(reprint.Succeeded);
            Assert.NotEqual(initial.PrintJobId, reprint.PrintJobId);
            Assert.Equal($"/api/printing/documents/{document.Kind}/{document.BusinessDocumentId:D}?reprint=true", handler.RequestPath);
            Assert.Equal(2, handler.RequestCount);
            Assert.Equal(PrintJobState.Succeeded, (await store.GetAsync(initial.PrintJobId!))!.State);
            var reprintJob = await store.GetAsync(reprint.PrintJobId!);
            Assert.Equal(PrintRequestMode.Reprint, reprintJob!.Mode);
            Assert.Equal(PrintJobState.Succeeded, reprintJob.State);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task PrintAsync_RejectsServerDocumentWithDifferentIdentityBeforePrinting()
    {
        var requested = CreateDocument();
        var mismatched = requested with { BusinessDocumentId = Guid.NewGuid() };
        using var apiClient = CreateApiClient(RespondWith(mismatched));
        var engine = new RecordingProductionPrintEngine(new PrintJobResult(true, null, null));
        var service = new RemoteProductionDocumentPrintService(apiClient, engine);

        var result = await service.PrintAsync(
            requested.Kind,
            requested.BusinessDocumentId,
            new PrinterProfile("Receipt", "Local Receipt Printer", PaperKind.Thermal80Mm, 1, false),
            isReprint: false);

        Assert.False(result.Succeeded);
        Assert.Equal("printing.document_identity_mismatch", result.ErrorCode);
        Assert.Equal(0, engine.CallCount);
    }

    private static ProductionDocument CreateDocument()
    {
        return new ProductionDocument(
            ProductionDocumentKind.PosSaleReceipt,
            Guid.NewGuid(),
            "SALE-000042",
            DateTimeOffset.Parse("2026-09-28T10:00:00Z"),
            "Edge Retails",
            "Test Street",
            "03000000000",
            "Walk-in Customer",
            "Sale Receipt",
            [new ProductionDocumentLine("Sample product", 1m, "piece", 100m, 100m)],
            [new ProductionDocumentTotal("Total", 100m, true)],
            [],
            "Thank you");
    }

    private static RecordingHttpHandler RespondWith(ProductionDocument document) => new(request =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(document, options: new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Converters = { new JsonStringEnumConverter() }
            })
        }));

    private static DesktopApiClient CreateApiClient(RecordingHttpHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:7150") };
        return new DesktopApiClient(httpClient, ownsClient: true);
    }

    private sealed class RecordingHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        public string? RequestPath { get; private set; }
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            RequestPath = request.RequestUri!.PathAndQuery;
            return responder(request);
        }
    }

    private sealed class RecordingProductionPrintEngine(
        PrintJobResult? result = null,
        Exception? exception = null) : IProductionPrintEngine
    {
        public ProductionDocument? Document { get; private set; }
        public PrinterProfile? Profile { get; private set; }
        public int CallCount { get; private set; }

        public Task<PrintJobResult> PrintAsync(
            ProductionDocument document,
            PrinterProfile profile,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Document = document;
            Profile = profile;
            if (exception is not null)
            {
                throw exception;
            }

            return Task.FromResult(result ?? new PrintJobResult(true, null, null));
        }
    }
}
