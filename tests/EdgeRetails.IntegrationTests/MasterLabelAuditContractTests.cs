using System.IO;
using EdgeRetails.Application.Production;
using EdgeRetails.Application.Production.Printing;
using EdgeRetails.Infrastructure.Production;
using EdgeRetails.Server.Controllers;
using EdgeRetails.Server.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace EdgeRetails.IntegrationTests;

/// <summary>NEW_COVERAGE: truthful label events and receipt replay; no database fixture.</summary>
public sealed class MasterLabelAuditContractTests
{
    [Fact]
    public async Task ServerGenerationCannotClaimWorkstationFilePublication()
    {
        var sink = new Sink();
        var attempt = Guid.NewGuid();
        var response = await Controller(sink).ExportLabels(new([Guid.NewGuid()], [], ClientExportAttemptId: attempt));
        var bundle = Assert.IsType<LabelExportBundle>(Assert.IsType<OkObjectResult>(response).Value);
        Assert.Equal(attempt, bundle.ExportAttemptId);
        Assert.True(bundle.GenerationAuditPersisted);
        var record = Assert.Single(sink.Records);
        Assert.Equal("LABEL_PDF_GENERATED", record.EventType);
        Assert.Equal(attempt.ToString("D"), record.CorrelationId);
        Assert.DoesNotContain("LABEL_PDF_EXPORTED", sink.Records.Select(record => record.EventType));
    }

    [Fact]
    public async Task GenerationAuditFailureIsReturnedWithoutFalselyFailingGeneratedBundle()
    {
        var response = await Controller(new Sink { Fail = true }).ExportLabels(new([Guid.NewGuid()], []));
        var bundle = Assert.IsType<LabelExportBundle>(Assert.IsType<OkObjectResult>(response).Value);
        Assert.NotEmpty(bundle.ExactUnitPdf);
        Assert.False(bundle.GenerationAuditPersisted);
    }

    [Fact]
    public async Task LabelReceiptReplayAcrossSinkInstancesAppendsOnlyOneEvent()
    {
        var root = Directory.CreateTempSubdirectory("edge-label-audit-").FullName;
        var path = Path.Combine(root, "audit.jsonl");
        try
        {
            var record = new ProductionAuditRecord("DOCUMENT_PRINTED", DateTimeOffset.UtcNow,
                "PhysicalItemSticker", Guid.NewGuid().ToString("D"), "WorkstationReportedSubmission=True", Guid.NewGuid().ToString("D"));
            await new FileProductionAuditSink(path).AppendAsync(record);
            await new FileProductionAuditSink(path).AppendAsync(record with { OccurredAtUtc = record.OccurredAtUtc.AddMinutes(1) });
            Assert.Single(await File.ReadAllLinesAsync(path));
        }
        finally { DeleteOwned(root); }
    }

    [Fact]
    public async Task SameLabelReceiptAttemptCannotChangeReportedOutcome()
    {
        var root = Directory.CreateTempSubdirectory("edge-label-audit-").FullName;
        var path = Path.Combine(root, "audit.jsonl");
        try
        {
            var record = new ProductionAuditRecord("DOCUMENT_PRINTED", DateTimeOffset.UtcNow,
                "PhysicalItemSticker", Guid.NewGuid().ToString("D"), "WorkstationReportedSubmission=True", Guid.NewGuid().ToString("D"));
            await new FileProductionAuditSink(path).AppendAsync(record);
            await Assert.ThrowsAsync<InvalidOperationException>(() => new FileProductionAuditSink(path)
                .AppendAsync(record with { Detail = "WorkstationReportedSubmission=False" }));
            Assert.Single(await File.ReadAllLinesAsync(path));
        }
        finally { DeleteOwned(root); }
    }

    [Fact]
    public async Task OtherAuditEventsRetainTheirExistingAppendBehavior()
    {
        var root = Directory.CreateTempSubdirectory("edge-label-audit-").FullName;
        var path = Path.Combine(root, "audit.jsonl");
        try
        {
            var record = new ProductionAuditRecord("UNRELATED_TEST_EVENT", DateTimeOffset.UtcNow,
                "Unrelated", "fixture", "unchanged", "fixture-correlation");
            var sink = new FileProductionAuditSink(path);
            await sink.AppendAsync(record);
            await sink.AppendAsync(record);
            Assert.Equal(2, (await File.ReadAllLinesAsync(path)).Length);
        }
        finally { DeleteOwned(root); }
    }

    // NEW_COVERAGE: the same receipt cannot change event family, and concurrent
    // sink instances must check and append under the same file lease.
    [Fact]
    public async Task ConcurrentAuthenticatedReceiptReplayPreservesFirstRecordAndRejectsChangedEvent()
    {
        var root = Directory.CreateTempSubdirectory("edge-label-audit-").FullName;
        var path = Path.Combine(root, "audit.jsonl");
        try
        {
            var record = new ProductionAuditRecord("DOCUMENT_PRINTED", DateTimeOffset.UtcNow,
                "ProductUnitLabel", Guid.NewGuid().ToString("D"), "WorkstationReportedSubmission=True", Guid.NewGuid().ToString("D"));
            await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => new FileProductionAuditSink(path, new KeyProvider()).AppendAsync(record)));
            var original = await File.ReadAllTextAsync(path);
            Assert.Single(await File.ReadAllLinesAsync(path));
            await Assert.ThrowsAsync<InvalidOperationException>(() => new FileProductionAuditSink(path, new KeyProvider())
                .AppendAsync(record with { EventType = "DOCUMENT_PRINT_FAILED" }));
            Assert.Equal(original, await File.ReadAllTextAsync(path));
        }
        finally { DeleteOwned(root); }
    }

    [Fact]
    public async Task AuthenticatedReceiptReplayRejectsTamperedHistoryWithoutAppending()
    {
        var root = Directory.CreateTempSubdirectory("edge-label-audit-").FullName;
        var path = Path.Combine(root, "audit.jsonl");
        try
        {
            var record = new ProductionAuditRecord("DOCUMENT_PRINTED", DateTimeOffset.UtcNow,
                "PhysicalItemSticker", Guid.NewGuid().ToString("D"), "WorkstationReportedSubmission=True", Guid.NewGuid().ToString("D"));
            await new FileProductionAuditSink(path, new KeyProvider()).AppendAsync(record);
            var tampered = (await File.ReadAllTextAsync(path)).Replace("Submission=True", "Submission=False", StringComparison.Ordinal);
            await File.WriteAllTextAsync(path, tampered);
            await Assert.ThrowsAsync<InvalidOperationException>(() => new FileProductionAuditSink(path, new KeyProvider()).AppendAsync(record));
            Assert.Equal(tampered, await File.ReadAllTextAsync(path));
        }
        finally { DeleteOwned(root); }
    }

    [Fact]
    public async Task AuditKeyFailureCannotFallBackToPlaintext()
    {
        var root = Directory.CreateTempSubdirectory("edge-label-audit-").FullName;
        var path = Path.Combine(root, "audit.jsonl");
        try
        {
            var record = new ProductionAuditRecord("DOCUMENT_PRINTED", DateTimeOffset.UtcNow,
                "PhysicalItemSticker", Guid.NewGuid().ToString("D"), "unchanged", Guid.NewGuid().ToString("D"));
            await Assert.ThrowsAsync<IOException>(() => new FileProductionAuditSink(path, new FailingKeyProvider()).AppendAsync(record));
            Assert.Empty(await File.ReadAllTextAsync(path));
        }
        finally { DeleteOwned(root); }
    }

    private sealed class KeyProvider : IProductionMaintenanceIntegrityKeyProvider
    {
        public Task<byte[]> GetIntegrityKeyAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray());
    }

    private sealed class FailingKeyProvider : IProductionMaintenanceIntegrityKeyProvider
    {
        public Task<byte[]> GetIntegrityKeyAsync(CancellationToken cancellationToken = default)
            => Task.FromException<byte[]>(new IOException("Owned key failure"));
    }

    private static void DeleteOwned(string root)
    {
        if (!Path.GetFullPath(root).StartsWith(Path.Combine(Path.GetTempPath(), "edge-label-audit-"), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Owned audit fixture escaped temporary boundary.");
        }
        Directory.Delete(root, true);
    }

    private static PrintingController Controller(Sink sink)
    {
        var source = new Source();
        var controller = new PrintingController(source, source, source, audit: new ProductionAuditCoordinator(sink, new Reporter()))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.HttpContext.Items["ActorContext"] = new ActorContext(Guid.NewGuid(), Guid.NewGuid(), "Operator", Guid.NewGuid(), "Manager",
            new HashSet<string>(["inventory.manage", "printing.reprint"]));
        return controller;
    }

    private sealed class Sink : IProductionAuditSink
    {
        public readonly List<ProductionAuditRecord> Records = [];
        public bool Fail;
        public Task AppendAsync(ProductionAuditRecord record, CancellationToken cancellationToken = default)
        {
            if (Fail) { return Task.FromException(new IOException("Owned audit failure")); }
            Records.Add(record);
            return Task.CompletedTask;
        }
    }

    private sealed class Reporter : IProductionAuditFailureReporter
    {
        public Task ReportAsync(ProductionAuditRecord record, Exception error, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Source : IPhysicalStickerDocumentSource, IProductionDocumentSource, IProductionDocumentAuthorizationPolicy
    {
        public Task<PhysicalItemStickerDocument> LoadStickerDocumentAsync(Guid inventoryUnitId, bool isReprint = false,
            CancellationToken cancellationToken = default) => Task.FromResult(new PhysicalItemStickerDocument(inventoryUnitId,
                "SUP-UNIT-000042", "Edge Retails", "Test unit", "Model A", "SKU-42", 42, null, null, null, "Edge Retails", 100m,
                isReprint, DateTimeOffset.UtcNow));
        public Task<ProductionDocument> LoadAsync(ProductionDocumentKind kind, Guid businessDocumentId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task EnsureCanPrintAsync(ProductionDocumentKind kind, Guid businessDocumentId, bool isReprint,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
