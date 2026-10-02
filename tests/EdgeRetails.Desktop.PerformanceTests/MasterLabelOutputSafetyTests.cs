using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.IO;
using EdgeRetails.Application.Production.Printing;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.PerformanceTests;

/// <summary>NEW_COVERAGE: output publication and durable submission boundaries; owned files only.</summary>
public sealed class MasterLabelOutputSafetyTests
{
    [Fact]
    public async Task FourthStagedWriteFailurePublishesNoPartialPdfPack()
    {
        using var fixture = new Fixture();
        var writes = new FailingFourthWrite();
        var service = fixture.Service(exportFiles: writes);
        var result = await service.ExportPdfAsync([fixture.UnitId], [], fixture.Output);
        Assert.False(result.IsSuccess);
        Assert.Equal("printing.pdf_export_failed", result.Error!.Code);
        Assert.Empty(Directory.EnumerateFiles(fixture.Output, "*.pdf", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Output));
        Assert.Equal(0, fixture.Engine.Calls);
    }

    [Fact]
    public async Task SuccessPublishesAllCanonicalFilesInsideOneCompleteChildDirectory()
    {
        using var fixture = new Fixture();
        var result = await fixture.Service().ExportPdfAsync([fixture.UnitId], [], fixture.Output);
        Assert.True(result.IsSuccess);
        Assert.Equal(4, result.Value!.Count);
        var parent = Path.Combine(fixture.Output, "Pilot_Label_Pack");
        Assert.All(result.Value, path => Assert.Equal(parent, Path.GetDirectoryName(path)));
        Assert.Equal(new[] { "Pilot_Exact_Unit_Labels.pdf", "Pilot_Product_Labels.pdf", "Pilot_Label_Manifest.pdf", "Pilot_Label_Manifest.json" },
            result.Value.Select(Path.GetFileName));
        Assert.All(result.Value, path => Assert.True(File.Exists(path)));
        Assert.Single(Directory.EnumerateDirectories(fixture.Output));
    }

    [Fact]
    public async Task EarlierLabelFilesRejectExportBeforeAnyServerRequest()
    {
        using var fixture = new Fixture();
        var earlier = Path.Combine(fixture.Output, "Pilot_Exact_Unit_Labels.pdf");
        await File.WriteAllTextAsync(earlier, "preserved historical fixture");
        var result = await fixture.Service().ExportPdfAsync([fixture.UnitId], [], fixture.Output);
        Assert.False(result.IsSuccess);
        Assert.Equal("printing.export_exists", result.Error!.Code);
        Assert.Equal(0, fixture.Requests);
        Assert.Equal("preserved historical fixture", await File.ReadAllTextAsync(earlier));
    }

    [Fact]
    public async Task CorrelationAttemptIsPresentBeforeOsSubmissionAndReplayNeverResubmits()
    {
        using var fixture = new Fixture();
        var attempt = Guid.NewGuid();
        var first = await fixture.Service().PrintAsync([fixture.UnitId], null, false,
            correlationId: attempt.ToString("D"), requestMode: PrintRequestMode.Initial);
        Assert.True(first.IsSuccess);
        Assert.Equal(attempt, fixture.Engine.LastDocument!.ClientPrintAttemptId);
        Assert.True(File.Exists(fixture.Attempts));
        var replay = await fixture.Service().PrintAsync([fixture.UnitId], null, false,
            correlationId: attempt.ToString("D"), requestMode: PrintRequestMode.Retry);
        Assert.True(replay.IsSuccess);
        Assert.True(replay.Value!.JobResults.Single().AlreadyCompleted);
        Assert.Equal(1, fixture.Engine.Calls);
        Assert.Equal(attempt.ToString("D"), replay.Value.JobResults.Single().PrintJobId);
    }

    [Fact]
    public async Task UnresolvedSubmissionAfterRestartRequiresExplicitReprint()
    {
        using var fixture = new Fixture();
        fixture.Engine.Result = new(false, "print.outcome_unknown", "Check printer");
        await fixture.Service().PrintAsync([fixture.UnitId], null, false,
            correlationId: Guid.NewGuid().ToString("D"), requestMode: PrintRequestMode.Initial);
        var nextEngine = new Engine();
        var resumed = await fixture.Service(nextEngine).PrintAsync([fixture.UnitId], null, false,
            correlationId: Guid.NewGuid().ToString("D"), requestMode: PrintRequestMode.Initial);
        Assert.True(resumed.IsSuccess);
        Assert.Equal("print.outcome_unknown", resumed.Value!.JobResults.Single().ErrorCode);
        Assert.Equal(0, nextEngine.Calls);
        var reprint = await fixture.Service(nextEngine).PrintAsync([fixture.UnitId], null, true,
            correlationId: Guid.NewGuid().ToString("D"), requestMode: PrintRequestMode.Reprint);
        Assert.True(reprint.Value!.JobResults.Single().Succeeded);
        Assert.Equal(1, nextEngine.Calls);
    }

    [Fact]
    public async Task MissingJournalCannotAuthorizeRetryOfEarlierCorrelation()
    {
        using var fixture = new Fixture();
        var result = await fixture.Service().PrintAsync([fixture.UnitId], null, false,
            correlationId: Guid.NewGuid().ToString("D"), requestMode: PrintRequestMode.Retry);
        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.JobResults.Single().Succeeded);
        Assert.Equal("print.attempt_unavailable", result.Value.JobResults.Single().ErrorCode);
        Assert.Equal(0, fixture.Engine.Calls);
    }

    [Fact]
    public async Task CancellationBeforePublicationLeavesNoPackOrStagingDirectory()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var writer = new CancellingWrite(cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service(exportFiles: writer)
            .ExportPdfAsync([fixture.UnitId], [], fixture.Output, cancellationToken: cancellation.Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Output));
    }

    private sealed class FailingFourthWrite : ILabelPackFileWriter
    {
        private int _count;
        public Task WriteAsync(string path, byte[] bytes, CancellationToken cancellationToken) => ++_count == 4
            ? Task.FromException(new IOException("Owned fourth-write failure"))
            : File.WriteAllBytesAsync(path, bytes, cancellationToken);
    }

    private sealed class CancellingWrite(CancellationTokenSource cancellation) : ILabelPackFileWriter
    {
        public async Task WriteAsync(string path, byte[] bytes, CancellationToken cancellationToken)
        {
            await File.WriteAllBytesAsync(path, bytes, cancellationToken);
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private sealed class Fixture : IDisposable
    {
        public readonly string Root = Directory.CreateTempSubdirectory("edge-label-output-").FullName;
        public readonly Guid UnitId = Guid.NewGuid();
        public readonly Engine Engine = new();
        public int Requests;
        private readonly HttpClient _http;
        private readonly DesktopApiClient _api;
        public string Output => Path.Combine(Root, "output");
        public string Attempts => Path.Combine(Root, "attempts.json");

        public Fixture()
        {
            Directory.CreateDirectory(Output);
            _http = new HttpClient(new Handler(async request =>
            {
                Requests++;
                if (request.RequestUri!.AbsolutePath == "/api/printing/labels/export")
                {
                    var input = await request.Content!.ReadFromJsonAsync<LabelExportRequest>();
                    return new(HttpStatusCode.OK) { Content = JsonContent.Create(VectorLabelPdfExporter.Export([Document(UnitId)], [])
                        with { ExportAttemptId = input!.ClientExportAttemptId }) };
                }
                return new(HttpStatusCode.OK) { Content = request.Method == HttpMethod.Get
                    ? JsonContent.Create(Document(UnitId)) : JsonContent.Create(new StickerPrintReceiptResult(true, null)) };
            })) { BaseAddress = new Uri("http://127.0.0.1:7150") };
            _api = new DesktopApiClient(_http);
        }

        public RemotePhysicalStickerPrintService Service(Engine? engine = null, ILabelPackFileWriter? exportFiles = null) =>
            new(_api, engine ?? Engine, Attempts, exportFiles);

        public void Dispose()
        {
            _api.Dispose(); _http.Dispose();
            var prefix = Path.Combine(Path.GetTempPath(), "edge-label-output-");
            if (!Path.GetFullPath(Root).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Owned output fixture escaped temporary boundary.");
            }
            Directory.Delete(Root, true);
        }
    }

    private static PhysicalItemStickerDocument Document(Guid id) => new(id, "SUP-UNIT-000042", "Edge Retails", "Test unit", "Model A",
        "SKU-42", 42, null, null, null, "Edge Retails", 100m, false, DateTimeOffset.UtcNow);

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }

    private sealed class Engine : IPhysicalStickerPrintEngine
    {
        public int Calls;
        public PhysicalItemStickerDocument? LastDocument;
        public PrintJobResult Result = new(true, null, null);
        public Task<PrintJobResult> PrintStickerAsync(PhysicalItemStickerDocument document, string? printerName = null, int copies = 1,
            CancellationToken cancellationToken = default)
        {
            Calls++; LastDocument = document;
            return Task.FromResult(Result);
        }
    }
}
