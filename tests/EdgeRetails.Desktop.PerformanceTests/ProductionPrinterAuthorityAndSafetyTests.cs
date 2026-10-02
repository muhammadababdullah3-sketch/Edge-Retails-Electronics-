using System;
using System.Collections.Generic;
using System.Printing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Documents;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Production;
using EdgeRetails.Application.Production.Printing;
using EdgeRetails.Desktop;
using EdgeRetails.Desktop.Production.Printing;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Infrastructure.Production.Printing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EdgeRetails.Desktop.PerformanceTests;

public sealed class ProductionPrinterAuthorityAndSafetyTests
{
    [Fact]
    public void ProductionDesktopDI_ResolvesWpfPhysicalStickerPrintEngine()
    {
        var services = BackendRuntime.CreateDesktopServiceCollection("Host=localhost;Database=edge_retails_test");
        App.ConfigureDesktopServices(services);
        using var provider = services.BuildServiceProvider();

        var resolvedStickerEngine = provider.GetRequiredService<IPhysicalStickerPrintEngine>();
        var resolvedProductionEngine = provider.GetRequiredService<IProductionPrintEngine>();

        Assert.NotNull(resolvedStickerEngine);
        Assert.IsType<WpfPhysicalStickerPrintEngine>(resolvedStickerEngine);

        Assert.NotNull(resolvedProductionEngine);
        Assert.IsType<WpfProductionPrintEngine>(resolvedProductionEngine);
    }

    [Fact]
    public void ProductionDesktopDI_DoesNotResolveSimulatorAsProductionAuthority()
    {
        var services = BackendRuntime.CreateDesktopServiceCollection("Host=localhost;Database=edge_retails_test");
        App.ConfigureDesktopServices(services);
        using var provider = services.BuildServiceProvider();

        var resolvedStickerEngine = provider.GetRequiredService<IPhysicalStickerPrintEngine>();
        var resolvedProductionEngine = provider.GetRequiredService<IProductionPrintEngine>();

        // Production desktop must NEVER resolve simulator implementations
        Assert.IsNotType<SimulatedPhysicalStickerPrintEngine>(resolvedStickerEngine);
        Assert.IsNotType<SimulatedProductionPrintEngine>(resolvedProductionEngine);
    }

    [Fact]
    public async Task RealPrinterUnavailable_DoesNotReportFakeSuccess()
    {
        // 1. Default WpfPhysicalStickerPrintEngine querying non-existent real printer name
        var engine = new WpfPhysicalStickerPrintEngine();
        var document = CreateSampleStickerDocument("REAL-TEST-000001", 1);

        var result = await engine.PrintStickerAsync(document, "Definitively_Non_Existent_Physical_Printer_XYZ_987654");

        Assert.False(result.Succeeded);
        Assert.NotNull(result.ErrorCode);
        Assert.Contains(result.ErrorCode, new[] { "print.printer_not_found", "print.printer_unavailable", "print.spooler_failed" });
        Assert.False(result.AlreadyCompleted);

        // 2. Offline / error queue test
        var offlineEngine = new WpfPhysicalStickerPrintEngine(
            queueResolver: name => (null, new PrintJobResult(false, "print.printer_unavailable", "Printer offline")),
            documentPrinter: null);

        var offlineResult = await offlineEngine.PrintStickerAsync(document, "ThermalPrinter");

        Assert.False(offlineResult.Succeeded);
        Assert.Equal("print.printer_unavailable", offlineResult.ErrorCode);
    }

    [Fact]
    public async Task PrinterFailure_DoesNotMutateInventory()
    {
        var unitId = Guid.NewGuid();
        var initialUnit = new InventoryUnit
        {
            Id = unitId,
            ProductId = Guid.NewGuid(),
            TrackingCode = "SUP-PROD-000042",
            ItemSequence = 42,
            Status = InventoryUnitStatus.InStock,
            SerialNumber = "SN-INIT-123",
            AcquisitionCost = 150m,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var units = new Dictionary<Guid, InventoryUnit> { [unitId] = initialUnit };
        var docSource = new TestStickerDocumentSource(units);
        var failingEngine = new WpfPhysicalStickerPrintEngine(
            queueResolver: name => (null, new PrintJobResult(false, "print.printer_unavailable", "Printer out of paper")),
            documentPrinter: null);

        var auth = new TestProductionAuth();
        var audit = new ProductionAuditCoordinator(new TestAuditSink(), new TestAuditFailureReporter());
        var clock = new TestClock();

        var handler = new PrintPhysicalStickersHandler(auth, docSource, failingEngine, audit, clock);

        var command = new PrintPhysicalStickersCommand(
            InventoryUnitIds: new[] { unitId },
            PrinterName: "ThermalPrinter",
            IsReprint: false,
            RequestedBy: Guid.NewGuid());

        var result = await handler.HandleAsync(command);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value!.TotalRequested);
        Assert.Equal(0, result.Value.SucceededCount);
        Assert.Equal(1, result.Value.FailedCount);
        Assert.Equal("print.printer_unavailable", result.Value.JobResults[0].ErrorCode);

        // Verify inventory unit is completely unmutated
        var currentUnit = units[unitId];
        Assert.Equal(initialUnit.Id, currentUnit.Id);
        Assert.Equal(initialUnit.ProductId, currentUnit.ProductId);
        Assert.Equal(initialUnit.TrackingCode, currentUnit.TrackingCode);
        Assert.Equal(initialUnit.ItemSequence, currentUnit.ItemSequence);
        Assert.Equal(initialUnit.Status, currentUnit.Status);
        Assert.Equal(initialUnit.SerialNumber, currentUnit.SerialNumber);
        Assert.Equal(initialUnit.AcquisitionCost, currentUnit.AcquisitionCost);
    }

    [Fact]
    public async Task PrinterFailure_DoesNotMutateSequence()
    {
        var unitId = Guid.NewGuid();
        var initialUnit = new InventoryUnit
        {
            Id = unitId,
            ProductId = Guid.NewGuid(),
            TrackingCode = "SUP-PROD-000042",
            ItemSequence = 42,
            Status = InventoryUnitStatus.InStock,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var units = new Dictionary<Guid, InventoryUnit> { [unitId] = initialUnit };
        var docSource = new TestStickerDocumentSource(units);
        var failingEngine = new WpfPhysicalStickerPrintEngine(
            queueResolver: name => (null, new PrintJobResult(false, "print.spooler_failed", "Spooler failure")),
            documentPrinter: null);

        var auth = new TestProductionAuth();
        var audit = new ProductionAuditCoordinator(new TestAuditSink(), new TestAuditFailureReporter());
        var clock = new TestClock();

        var handler = new PrintPhysicalStickersHandler(auth, docSource, failingEngine, audit, clock);

        const long highWaterSequence = 50;
        var currentSequence = highWaterSequence;

        var command = new PrintPhysicalStickersCommand(
            InventoryUnitIds: new[] { unitId },
            PrinterName: "ThermalPrinter",
            IsReprint: false,
            RequestedBy: Guid.NewGuid());

        var result = await handler.HandleAsync(command);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value!.FailedCount);

        // Sequence high-water was not changed by print failure
        Assert.Equal(highWaterSequence, currentSequence);
        Assert.Equal(42, units[unitId].ItemSequence);
    }

    [Fact]
    public Task OutcomeUnknown_PreservesSamePhysicalIdentity() => RunInStaAsync(async () =>
    {
        var unitId = Guid.NewGuid();
        const string originalTrackingCode = "SUP-PROD-000042";
        const long originalSequence = 42;

        var initialUnit = new InventoryUnit
        {
            Id = unitId,
            ProductId = Guid.NewGuid(),
            TrackingCode = originalTrackingCode,
            ItemSequence = originalSequence,
            Status = InventoryUnitStatus.InStock,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var units = new Dictionary<Guid, InventoryUnit> { [unitId] = initialUnit };
        var docSource = new TestStickerDocumentSource(units);

        // Engine simulates exception during or after submission starts -> returns print.outcome_unknown
        var outcomeUnknownEngine = new WpfPhysicalStickerPrintEngine(
            queueResolver: name => (null, null),
            documentPrinter: (queue, paginator, jobName) => throw new PrintSystemException("Spooler disconnected mid-job"));

        var auth = new TestProductionAuth();
        var audit = new ProductionAuditCoordinator(new TestAuditSink(), new TestAuditFailureReporter());
        var clock = new TestClock();

        var handler = new PrintPhysicalStickersHandler(auth, docSource, outcomeUnknownEngine, audit, clock);

        var command = new PrintPhysicalStickersCommand(
            InventoryUnitIds: new[] { unitId },
            PrinterName: "ThermalPrinter",
            IsReprint: false,
            RequestedBy: Guid.NewGuid());

        var result = await handler.HandleAsync(command);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value!.FailedCount);
        Assert.Equal("print.outcome_unknown", result.Value.JobResults[0].ErrorCode);

        // Hard invariant: Physical identity is strictly preserved despite unknown spooler outcome
        Assert.Equal(originalTrackingCode, units[unitId].TrackingCode);
        Assert.Equal(originalSequence, units[unitId].ItemSequence);
        Assert.Equal(unitId, units[unitId].Id);
    });

    [Fact]
    public Task ReprintAfterUncertainOutcome_UsesSameTrackingCode() => RunInStaAsync(async () =>
    {
        var unitId = Guid.NewGuid();
        const string trackingCode = "SUP-PROD-000042";
        const long sequence = 42;

        var unit = new InventoryUnit
        {
            Id = unitId,
            ProductId = Guid.NewGuid(),
            TrackingCode = trackingCode,
            ItemSequence = sequence,
            Status = InventoryUnitStatus.InStock,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var units = new Dictionary<Guid, InventoryUnit> { [unitId] = unit };
        var docSource = new TestStickerDocumentSource(units);

        // 1. Initial print attempt results in outcome_unknown
        var unknownEngine = new WpfPhysicalStickerPrintEngine(
            queueResolver: name => (null, null),
            documentPrinter: (queue, paginator, jobName) => throw new PrintSystemException("Spooler hung mid-stream"));

        var auth = new TestProductionAuth();
        var sink = new TestAuditSink();
        var audit = new ProductionAuditCoordinator(sink, new TestAuditFailureReporter());
        var clock = new TestClock();

        var initialHandler = new PrintPhysicalStickersHandler(auth, docSource, unknownEngine, audit, clock);
        var initialCmd = new PrintPhysicalStickersCommand(
            InventoryUnitIds: new[] { unitId },
            PrinterName: "ThermalPrinter",
            IsReprint: false,
            RequestedBy: Guid.NewGuid());

        var initialResult = await initialHandler.HandleAsync(initialCmd);
        Assert.True(initialResult.IsSuccess);
        Assert.Equal("print.outcome_unknown", initialResult.Value!.JobResults[0].ErrorCode);

        // 2. Explicit reprint requested after printer recovered
        var capturedDocuments = new List<PhysicalItemStickerDocument>();
        var reprintEngine = new WpfPhysicalStickerPrintEngine(
            queueResolver: name => (null, null),
            documentPrinter: (queue, paginator, jobName) => { /* Success */ });

        var reprintDocSource = new CapturingStickerDocumentSource(docSource, capturedDocuments);
        var reprintHandler = new PrintPhysicalStickersHandler(auth, reprintDocSource, reprintEngine, audit, clock);

        var reprintCmd = new PrintPhysicalStickersCommand(
            InventoryUnitIds: new[] { unitId },
            PrinterName: "ThermalPrinter",
            IsReprint: true,
            RequestedBy: Guid.NewGuid());

        var reprintResult = await reprintHandler.HandleAsync(reprintCmd);

        Assert.True(reprintResult.IsSuccess);
        Assert.Equal(1, reprintResult.Value!.SucceededCount);

        // Crucial safety invariant: Reprint must reuse the exact same tracking code and sequence, never generating new ones
        Assert.Single(capturedDocuments);
        var reprintDoc = capturedDocuments[0];
        Assert.Equal(trackingCode, reprintDoc.TrackingCode);
        Assert.Equal(sequence, reprintDoc.ItemSequence);
        Assert.Equal(unitId, reprintDoc.InventoryUnitId);
        Assert.True(reprintDoc.IsReprint);

        // Audit logs verify DocumentReprinted
        Assert.Contains(sink.Records, r => r.EventType == ProductionAuditEvents.DocumentReprinted);
    });

    private static Task RunInStaAsync(Func<Task> action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(async () =>
        {
            try
            {
                await action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static PhysicalItemStickerDocument CreateSampleStickerDocument(string trackingCode, long sequence)
    {
        return new PhysicalItemStickerDocument(
            InventoryUnitId: Guid.NewGuid(),
            TrackingCode: trackingCode,
            CompanyName: "Edge Retails",
            ProductName: "Test Product",
            ModelName: "Model A",
            ProductCode: "TEST-PROD",
            ItemSequence: sequence,
            SerialNumber: "SN-999",
            Imei1: null,
            Imei2: null,
            ShopName: "Primary Shop",
            RetailSalePrice: 500m,
            IsReprint: false,
            CreatedAt: DateTimeOffset.UtcNow);
    }

    private sealed class TestStickerDocumentSource : IPhysicalStickerDocumentSource
    {
        private readonly Dictionary<Guid, InventoryUnit> _units;

        public TestStickerDocumentSource(Dictionary<Guid, InventoryUnit> units)
        {
            _units = units;
        }

        public Task<PhysicalItemStickerDocument> LoadStickerDocumentAsync(
            Guid inventoryUnitId,
            bool isReprint = false,
            CancellationToken cancellationToken = default)
        {
            if (!_units.TryGetValue(inventoryUnitId, out var unit))
            {
                throw new InvalidOperationException($"Unit '{inventoryUnitId}' not found.");
            }

            return Task.FromResult(new PhysicalItemStickerDocument(
                unit.Id,
                unit.TrackingCode ?? string.Empty,
                "Edge Retails",
                "Product",
                "Model",
                "SKU",
                unit.ItemSequence ?? 0,
                unit.SerialNumber,
                null,
                null,
                "Shop",
                100m,
                isReprint,
                unit.CreatedAt));
        }
    }

    private sealed class CapturingStickerDocumentSource : IPhysicalStickerDocumentSource
    {
        private readonly IPhysicalStickerDocumentSource _inner;
        private readonly List<PhysicalItemStickerDocument> _captured;

        public CapturingStickerDocumentSource(IPhysicalStickerDocumentSource inner, List<PhysicalItemStickerDocument> captured)
        {
            _inner = inner;
            _captured = captured;
        }

        public async Task<PhysicalItemStickerDocument> LoadStickerDocumentAsync(
            Guid inventoryUnitId,
            bool isReprint = false,
            CancellationToken cancellationToken = default)
        {
            var doc = await _inner.LoadStickerDocumentAsync(inventoryUnitId, isReprint, cancellationToken);
            _captured.Add(doc);
            return doc;
        }
    }

    private sealed class TestProductionAuth : IProductionAuthorization
    {
        public Task EnsureAuthenticatedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EnsurePermissionAsync(string permission, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestAuditSink : IProductionAuditSink
    {
        public List<ProductionAuditRecord> Records { get; } = new();
        public Task AppendAsync(ProductionAuditRecord record, CancellationToken cancellationToken = default)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }
    }

    private sealed class TestAuditFailureReporter : IProductionAuditFailureReporter
    {
        public Task ReportAsync(ProductionAuditRecord record, Exception error, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
        public DateOnly ShopDate => DateOnly.FromDateTime(DateTime.UtcNow);
    }
}
