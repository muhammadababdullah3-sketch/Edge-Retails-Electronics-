using EdgeRetails.Application.Production;
using EdgeRetails.Application.Production.Printing;
using Xunit;

namespace EdgeRetails.UnitTests.Sprint8;

public sealed class Sprint8PrintingTests
{
    [Theory]
    [InlineData(PaperKind.Thermal58Mm)]
    [InlineData(PaperKind.Thermal80Mm)]
    [InlineData(PaperKind.A4)]
    public void CanonicalPaperKinds_AreSupported(PaperKind paper) => Assert.True(Enum.IsDefined(paper));

    [Fact]
    public void CanonicalBusinessDocumentKinds_AreAllPresent()
    {
        var expected = new[]
        {
            ProductionDocumentKind.PosSaleReceipt,
            ProductionDocumentKind.SaleReturnReceipt,
            ProductionDocumentKind.PurchaseDocument,
            ProductionDocumentKind.PurchaseReturnDocument,
            ProductionDocumentKind.ThakaMaterialChallan,
            ProductionDocumentKind.ThakaPaymentReceipt,
            ProductionDocumentKind.FinalSettlementStatement
        };
        Assert.Equal(expected.Length, Enum.GetValues<ProductionDocumentKind>().Length);
    }

    [Fact]
    public async Task PrinterConfiguration_RequiresSettingsManage_AndDriverValidation()
    {
        var auth = new RecordingAuthorization();
        var store = new MemoryProfileStore();
        var validator = new RecordingValidator(true);
        var handler = new SavePrinterProfileHandler(auth, store, validator, Audit());
        await handler.HandleAsync(new PrinterProfile("POS", "Printer", PaperKind.Thermal80Mm, 1, true), null);
        Assert.Equal(ProductionPermissionNames.SettingsManage, auth.LastPermission);
        Assert.True(validator.Called);
    }

    [Fact]
    public async Task UnsupportedPrinterMedia_IsRejectedBeforePhysicalPrint()
    {
        var engine = new RecordingPrintEngine();
        var handler = Handler(new RecordingValidator(false), new MemoryPrintJobStore(), engine);
        var result = await handler.HandleAsync(Command());
        Assert.False(result.Succeeded);
        Assert.Equal("print.media_not_supported", result.ErrorCode);
        Assert.False(engine.Called);
    }

    [Fact]
    public async Task RetryOfSucceededJob_IsIdempotentAndDoesNotPrintTwice()
    {
        var jobs = new MemoryPrintJobStore();
        var engine = new RecordingPrintEngine();
        var handler = Handler(new RecordingValidator(true), jobs, engine);
        var first = await handler.HandleAsync(Command());
        Assert.True(first.Succeeded);
        Assert.Equal(1, engine.Count);

        var retry = await handler.HandleAsync(Command() with { PrintJobId = first.PrintJobId, RequestMode = PrintRequestMode.Retry });
        Assert.True(retry.Succeeded);
        Assert.True(retry.AlreadyCompleted);
        Assert.Equal(1, engine.Count);
    }

    [Fact]
    public async Task ExplicitReprint_CreatesNewJob_AndRendersReprintMarker()
    {
        var jobs = new MemoryPrintJobStore();
        var engine = new RecordingPrintEngine();
        var handler = Handler(new RecordingValidator(true), jobs, engine);
        var first = await handler.HandleAsync(Command());
        var reprint = await handler.HandleAsync(Command() with { IsReprint = true, RequestMode = PrintRequestMode.Reprint });
        Assert.True(reprint.Succeeded);
        Assert.NotEqual(first.PrintJobId, reprint.PrintJobId);
        Assert.Equal("REPRINT", engine.LastDocument?.CopyLabel);
    }

    [Fact]
    public async Task AmbiguousPhysicalOutcome_IsPersistedAndBlocksAutomaticRetry()
    {
        var jobs = new MemoryPrintJobStore();
        var engine = new RecordingPrintEngine(new PrintJobResult(false, "print.outcome_unknown", "unknown"));
        var handler = Handler(new RecordingValidator(true), jobs, engine);
        var first = await handler.HandleAsync(Command());
        Assert.False(first.Succeeded);
        Assert.Equal("print.outcome_unknown", first.ErrorCode);

        var retry = await handler.HandleAsync(Command() with { PrintJobId = first.PrintJobId, RequestMode = PrintRequestMode.Retry });
        Assert.False(retry.Succeeded);
        Assert.Equal("print.outcome_unknown", retry.ErrorCode);
        Assert.Equal(1, engine.Count);
    }

    [Fact]
    public async Task DocumentSourceFailureBeforeSubmission_LeavesJobPreparedAndRetryable()
    {
        var jobs = new MemoryPrintJobStore();
        var engine = new RecordingPrintEngine();
        var handler = Handler(
            new RecordingValidator(true),
            jobs,
            engine,
            new ThrowingDocumentSource());

        var result = await handler.HandleAsync(Command());

        Assert.False(result.Succeeded);
        Assert.Equal("print.document_load_failed", result.ErrorCode);
        Assert.Equal(0, engine.Count);
        Assert.NotNull(result.PrintJobId);

        var job = await jobs.GetAsync(result.PrintJobId!);
        Assert.NotNull(job);
        Assert.Equal(PrintJobState.Prepared, job!.State);
        Assert.Equal(0, job.AttemptNumber);
    }

    [Fact]
    public async Task RetryDocumentSourceFailure_PreservesExistingRetryableState()
    {
        var jobs = new MemoryPrintJobStore();
        var engine = new RecordingPrintEngine();
        var now = DateTimeOffset.UtcNow;
        var id = "retry-doc-source";
        await jobs.CreateAsync(new PrintJobRecord(
            id, ProductionDocumentKind.PosSaleReceipt, BusinessId, "POS",
            PrintRequestMode.Initial, PrintJobState.Failed, 1, now, now, "print.spooler_failed"));

        var result = await Handler(
            new RecordingValidator(true),
            jobs,
            engine,
            new ThrowingDocumentSource()).HandleAsync(
                Command() with { PrintJobId = id, RequestMode = PrintRequestMode.Retry });

        Assert.Equal("print.document_load_failed", result.ErrorCode);
        Assert.Equal(0, engine.Count);
        var job = await jobs.GetAsync(id);
        Assert.Equal(PrintJobState.Failed, job!.State);
        Assert.Equal(1, job.AttemptNumber);
        Assert.Equal("print.spooler_failed", job.LastErrorCode);
    }

    [Fact]
    public async Task PrintingState_IsNotAutomaticallyRetried_WhenPhysicalOutcomeIsUnknown()
    {
        var jobs = new MemoryPrintJobStore();
        var now = DateTimeOffset.UtcNow;
        var id = "job1";
        await jobs.CreateAsync(new PrintJobRecord(id, ProductionDocumentKind.PosSaleReceipt, BusinessId, "POS", PrintRequestMode.Initial, PrintJobState.Printing, 1, now, now));
        var engine = new RecordingPrintEngine();
        var result = await Handler(new RecordingValidator(true), jobs, engine).HandleAsync(Command() with { PrintJobId = id, RequestMode = PrintRequestMode.Retry });
        Assert.False(result.Succeeded);
        Assert.Equal("print.outcome_unknown", result.ErrorCode);
        Assert.Equal(0, engine.Count);
    }

    private static readonly Guid BusinessId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static PrintDocumentCommand Command() => new(ProductionDocumentKind.PosSaleReceipt, BusinessId, "POS", false, "c");

    private static PrintDocumentHandler Handler(
        IPrinterProfileValidator validator,
        IPrintJobStore jobs,
        RecordingPrintEngine engine,
        IProductionDocumentSource? source = null)
        => new(
            new RecordingAuthorization(),
            new AllowDocumentAuthorization(),
            source ?? new DocumentSource(),
            new MemoryProfileStore(new PrinterProfile("POS", "Printer", PaperKind.Thermal80Mm, 1, false)),
            validator,
            jobs,
            engine,
            Audit());

    private static ProductionAuditCoordinator Audit() => new(new NullAudit(), new NullAuditFailureReporter());

    private sealed class RecordingAuthorization : IProductionAuthorization
    {
        public string? LastPermission { get; private set; }
        public Task EnsureAuthenticatedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EnsurePermissionAsync(string permission, CancellationToken cancellationToken = default) { LastPermission = permission; return Task.CompletedTask; }
    }
    private sealed class AllowDocumentAuthorization : IProductionDocumentAuthorizationPolicy
    {
        public Task EnsureCanPrintAsync(ProductionDocumentKind kind, Guid businessDocumentId, bool isReprint, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class RecordingValidator(bool supported) : IPrinterProfileValidator
    {
        public bool Called { get; private set; }
        public Task<PrinterProfileValidationResult> ValidateAsync(PrinterProfile profile, CancellationToken cancellationToken = default)
        {
            Called = true;
            return Task.FromResult(supported ? PrinterProfileValidationResult.Valid : new PrinterProfileValidationResult(false, "print.media_not_supported", "unsupported"));
        }
    }
    private sealed class MemoryProfileStore(PrinterProfile? profile = null) : IPrinterProfileStore
    {
        private PrinterProfile? _profile = profile;
        public Task<PrinterProfile?> GetAsync(string profileName, CancellationToken cancellationToken = default) => Task.FromResult(_profile);
        public Task SaveAsync(PrinterProfile profile, CancellationToken cancellationToken = default) { _profile = profile; return Task.CompletedTask; }
    }
    private sealed class MemoryPrintJobStore : IPrintJobStore
    {
        private readonly Dictionary<string, PrintJobRecord> _items = new();
        public Task<PrintJobRecord?> GetAsync(string printJobId, CancellationToken cancellationToken = default) => Task.FromResult(_items.GetValueOrDefault(printJobId));
        public Task CreateAsync(PrintJobRecord record, CancellationToken cancellationToken = default) { _items.Add(record.PrintJobId, record); return Task.CompletedTask; }
        public Task<bool> TryTransitionAsync(string id, PrintJobState expected, PrintJobState next, int attempt, string? error, CancellationToken cancellationToken = default)
        {
            if (!_items.TryGetValue(id, out var r) || r.State != expected)
            {
                return Task.FromResult(false);
            }

            _items[id] = r with { State = next, AttemptNumber = attempt, LastErrorCode = error, UpdatedAtUtc = DateTimeOffset.UtcNow };
            return Task.FromResult(true);
        }
    }
    private sealed class DocumentSource : IProductionDocumentSource
    {
        public Task<ProductionDocument> LoadAsync(ProductionDocumentKind kind, Guid businessDocumentId, CancellationToken cancellationToken = default)
            => Task.FromResult(new ProductionDocument(kind, businessDocumentId, "INV-1", DateTimeOffset.UtcNow, "Shop", null, null, null, "Receipt", Array.Empty<ProductionDocumentLine>(), Array.Empty<ProductionDocumentTotal>(), Array.Empty<string>(), null));
    }

    private sealed class ThrowingDocumentSource : IProductionDocumentSource
    {
        public Task<ProductionDocument> LoadAsync(
            ProductionDocumentKind kind,
            Guid businessDocumentId,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("document source unavailable");
    }
    private sealed class RecordingPrintEngine : IProductionPrintEngine
    {
        private readonly PrintJobResult _result;
        public RecordingPrintEngine(PrintJobResult? result = null) => _result = result ?? new PrintJobResult(true, null, null);
        public bool Called => Count > 0;
        public int Count { get; private set; }
        public ProductionDocument? LastDocument { get; private set; }
        public Task<PrintJobResult> PrintAsync(ProductionDocument document, PrinterProfile profile, CancellationToken cancellationToken = default)
        {
            Count++; LastDocument = document; return Task.FromResult(_result);
        }
    }
    private sealed class NullAudit : IProductionAuditSink { public Task AppendAsync(ProductionAuditRecord record, CancellationToken cancellationToken = default) => Task.CompletedTask; }
    private sealed class NullAuditFailureReporter : IProductionAuditFailureReporter { public Task ReportAsync(ProductionAuditRecord record, Exception error, CancellationToken cancellationToken = default) => Task.CompletedTask; }
}
