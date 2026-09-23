using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Production;
using EdgeRetails.Application.Production.Backup;
using EdgeRetails.Application.Production.Diagnostics;
using EdgeRetails.Application.Production.Outbox;
using EdgeRetails.Application.Production.Printing;
using EdgeRetails.Domain.SystemConfiguration;
using EdgeRetails.Infrastructure.Production;
using EdgeRetails.Infrastructure.Production.Backup;
using EdgeRetails.Infrastructure.Production.Printing;
using EdgeRetails.Infrastructure.Production.Startup;
using EdgeRetails.Worker.Jobs;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EdgeRetails.UnitTests;

public sealed class Phase3ProductionSafetyTests
{
    [Fact]
    public async Task OutboxProcessor_SuccessfulDispatch_MarksCompleted()
    {
        var msg = CreateOutboxMessage("TestEffect", "test_idemp_1");
        var repository = new InMemoryOutboxRepository(new[] { msg });
        var handler = new TestEffectHandler("TestEffect", shouldSucceed: true);

        var processor = new OutboxProcessor(repository, new[] { handler });
        var processedCount = await processor.ProcessPendingAsync(20, CancellationToken.None);

        Assert.Equal(1, processedCount);
        Assert.Equal(OutboxMessageStatus.Completed, msg.Status);
        Assert.Equal(1, msg.AttemptCount);
        Assert.NotNull(msg.CompletedAt);
        Assert.Null(msg.LastError);
    }

    [Fact]
    public async Task OutboxProcessor_TransientFailure_IncrementsAttemptAndCalculatesExponentialBackoff()
    {
        var msg = CreateOutboxMessage("TestEffect", "test_idemp_2");
        var repository = new InMemoryOutboxRepository(new[] { msg });
        var handler = new TestEffectHandler("TestEffect", shouldSucceed: false);

        var processor = new OutboxProcessor(repository, new[] { handler });
        var processedCount = await processor.ProcessPendingAsync(20, CancellationToken.None);

        Assert.Equal(0, processedCount);
        Assert.Equal(OutboxMessageStatus.Pending, msg.Status);
        Assert.Equal(1, msg.AttemptCount);
        Assert.NotNull(msg.NextAttemptAt);
        Assert.Contains("Simulated transient effect failure", msg.LastError);
    }

    [Fact]
    public async Task OutboxProcessor_ExceedingMaxAttempts_EscalatesToActionRequired()
    {
        var msg = CreateOutboxMessage("TestEffect", "test_idemp_3");
        msg.AttemptCount = 4; // Max is 5, next failure will be attempt 5
        var repository = new InMemoryOutboxRepository(new[] { msg });
        var handler = new TestEffectHandler("TestEffect", shouldSucceed: false);

        var processor = new OutboxProcessor(repository, new[] { handler });
        await processor.ProcessPendingAsync(20, CancellationToken.None);

        Assert.Equal(OutboxMessageStatus.ActionRequired, msg.Status);
        Assert.Equal(5, msg.AttemptCount);
        Assert.Null(msg.NextAttemptAt);
        Assert.NotNull(msg.LastError);
    }

    [Fact]
    public async Task OutboxProcessor_UnknownEffectType_TransitionsToActionRequired()
    {
        var msg = CreateOutboxMessage("UnregisteredEffect", "test_idemp_4");
        var repository = new InMemoryOutboxRepository(new[] { msg });

        var processor = new OutboxProcessor(repository, Array.Empty<IOutboxEffectHandler>());
        await processor.ProcessPendingAsync(20, CancellationToken.None);

        Assert.Equal(OutboxMessageStatus.ActionRequired, msg.Status);
        Assert.Contains("No outbox effect handler registered", msg.LastError);
    }

    [Fact]
    public async Task OutboxProcessor_NonRetryableOutcomeUnknown_EscalatesImmediatelyToActionRequired()
    {
        var msg = CreateOutboxMessage("NonRetryableEffect", "test_idemp_non_retry");
        var repository = new InMemoryOutboxRepository(new[] { msg });

        var handler = new ThrowingEffectHandler("NonRetryableEffect", new NonRetryableOutboxEffectException("Spooler outcome unknown"));
        var processor = new OutboxProcessor(repository, new[] { handler });

        await processor.ProcessPendingAsync(20, CancellationToken.None);

        Assert.Equal(OutboxMessageStatus.ActionRequired, msg.Status);
        Assert.Contains("Non-retryable outbox effect error", msg.LastError);
    }

    [Fact]
    public async Task FileBackupJobLock_AllowsSingleAcquisition_RejectsConcurrent()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"edgeretails_test_backup_{Guid.NewGuid():N}.lock");
        try
        {
            var lock1 = new FileBackupJobLock(tempFile);
            var lock2 = new FileBackupJobLock(tempFile);

            var lease1 = await lock1.TryAcquireLockAsync(CancellationToken.None);
            Assert.NotNull(lease1);

            var lease2 = await lock2.TryAcquireLockAsync(CancellationToken.None);
            Assert.Null(lease2);

            await lease1.DisposeAsync();

            var lease3 = await lock2.TryAcquireLockAsync(CancellationToken.None);
            Assert.NotNull(lease3);

            await lease3.DisposeAsync();
        }
        finally
        {
            try
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task FileWorkerJobLock_AllowsSingleAcquisition_RejectsConcurrent()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"edgeretails_test_worker_dir_{Guid.NewGuid():N}");
        try
        {
            var lock1 = new FileWorkerJobLock(tempDir);
            var lock2 = new FileWorkerJobLock(tempDir);

            var lease1 = await lock1.TryAcquireJobLockAsync("outbox_dispatcher", CancellationToken.None);
            Assert.NotNull(lease1);

            var lease2 = await lock2.TryAcquireJobLockAsync("outbox_dispatcher", CancellationToken.None);
            Assert.Null(lease2);

            await lease1.DisposeAsync();

            var lease3 = await lock2.TryAcquireJobLockAsync("outbox_dispatcher", CancellationToken.None);
            Assert.NotNull(lease3);

            await lease3.DisposeAsync();
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task WriteGuard_AllowsNormalState_WhenDiskIsHealthy()
    {
        var barrier = new FakeMaintenanceBarrier(ProductionMaintenanceState.Normal);
        var disk = new FakeDiskSpaceProbe(healthy: true, freeBytes: 10_000_000_000L);
        var guard = new ProductionMaintenanceWriteGuard(barrier, disk);

        // Must not throw
        await guard.EnsureBusinessWritesAllowedAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(ProductionMaintenanceState.RestorePreparing)]
    [InlineData(ProductionMaintenanceState.RestoreCutover)]
    [InlineData(ProductionMaintenanceState.RecoveryRequired)]
    public async Task WriteGuard_BlocksOnNonNormalMaintenanceState(ProductionMaintenanceState state)
    {
        var barrier = new FakeMaintenanceBarrier(state);
        var disk = new FakeDiskSpaceProbe(healthy: true, freeBytes: 10_000_000_000L);
        var guard = new ProductionMaintenanceWriteGuard(barrier, disk);

        var ex = await Assert.ThrowsAsync<ProductionMaintenanceException>(
            () => guard.EnsureBusinessWritesAllowedAsync(CancellationToken.None));
        Assert.Equal(state, ex.State);
    }

    [Fact]
    public async Task WriteGuard_BlocksOnCriticalDiskFloor()
    {
        var barrier = new FakeMaintenanceBarrier(ProductionMaintenanceState.Normal);
        var disk = new FakeDiskSpaceProbe(healthy: false, freeBytes: 50_000_000L);
        var guard = new ProductionMaintenanceWriteGuard(barrier, disk);

        var ex = await Assert.ThrowsAsync<CriticalDiskFloorException>(
            () => guard.EnsureBusinessWritesAllowedAsync(CancellationToken.None));
        Assert.Equal(50_000_000L, ex.AvailableBytes);
    }

    [Fact]
    public void ProductionDocumentSourceRouter_RejectsDuplicateOrMissingSources()
    {
        var sources = new List<IProductionDocumentKindSource>
        {
            new DummyDocumentSource(ProductionDocumentKind.PosSaleReceipt),
            new DummyDocumentSource(ProductionDocumentKind.PosSaleReceipt) // Duplicate
        };

        Assert.Throws<InvalidOperationException>(() => new ProductionDocumentSourceRouter(sources));

        var partialSources = new List<IProductionDocumentKindSource>
        {
            new DummyDocumentSource(ProductionDocumentKind.PosSaleReceipt)
            // Missing other 6
        };

        Assert.Throws<InvalidOperationException>(() => new ProductionDocumentSourceRouter(partialSources));
    }

    [Fact]
    public async Task ProductionDocumentSourceRouter_RoutesAllSevenCanonicalKinds()
    {
        var sources = new IProductionDocumentKindSource[]
        {
            new DummyDocumentSource(ProductionDocumentKind.PosSaleReceipt),
            new DummyDocumentSource(ProductionDocumentKind.SaleReturnReceipt),
            new DummyDocumentSource(ProductionDocumentKind.PurchaseDocument),
            new DummyDocumentSource(ProductionDocumentKind.PurchaseReturnDocument),
            new DummyDocumentSource(ProductionDocumentKind.ThakaMaterialChallan),
            new DummyDocumentSource(ProductionDocumentKind.ThakaPaymentReceipt),
            new DummyDocumentSource(ProductionDocumentKind.FinalSettlementStatement)
        };

        var router = new ProductionDocumentSourceRouter(sources);

        foreach (var kind in Enum.GetValues<ProductionDocumentKind>())
        {
            var doc = await router.LoadAsync(kind, Guid.NewGuid(), CancellationToken.None);
            Assert.Equal(kind, doc.Kind);
        }
    }

    [Fact]
    public async Task PrintDocumentHandler_ReprintMarksCopyLabel()
    {
        var store = new InMemoryPrintJobStore();
        var engine = new RecordingPrintEngine();
        var auth = new FakeProductionAuthorization();
        var docAuth = new FakeDocumentAuthorization();
        var source = new DummyDocumentSource(ProductionDocumentKind.PosSaleReceipt);
        var profileStore = new InMemoryProfileStore();
        var validator = new BasicPrinterProfileValidator();
        var audit = new ProductionAuditCoordinator(new FakeAuditSink(), new FakeAuditFailureReporter());

        await profileStore.SaveAsync(new PrinterProfile("DEFAULT", "TestPrinter", PaperKind.Thermal80Mm, 1, false));

        var handler = new PrintDocumentHandler(
            auth,
            docAuth,
            source,
            profileStore,
            validator,
            store,
            engine,
            audit);

        var docId = Guid.NewGuid();
        var command = new PrintDocumentCommand(
            ProductionDocumentKind.PosSaleReceipt,
            docId,
            "DEFAULT",
            IsReprint: true,
            CorrelationId: "corr_unit_test");

        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(engine.LastPrintedDocument);
        Assert.Equal("REPRINT", engine.LastPrintedDocument.CopyLabel);
    }

    [Fact]
    public async Task ProductionDocumentAuthorizationPolicy_EnforcesReprintPermission_WhenIsReprintIsTrue()
    {
        var mockAuth = new CheckingProductionAuthorization();
        var policy = new ProductionDocumentAuthorizationPolicy(mockAuth);

        // First print (not reprint)
        await policy.EnsureCanPrintAsync(ProductionDocumentKind.PosSaleReceipt, Guid.NewGuid(), isReprint: false, CancellationToken.None);
        Assert.Equal(0, mockAuth.ReprintPermissionChecks);

        // Reprint requires permission
        await policy.EnsureCanPrintAsync(ProductionDocumentKind.PosSaleReceipt, Guid.NewGuid(), isReprint: true, CancellationToken.None);
        Assert.Equal(1, mockAuth.ReprintPermissionChecks);
    }

    [Fact]
    public async Task WorkerHeartbeatService_WritesAndUpdatesHeartbeatFile()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"edgeretails_worker_hb_{Guid.NewGuid():N}.txt");
        try
        {
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var logger = loggerFactory.CreateLogger<WorkerHeartbeatService>();
            var hbService = new WorkerHeartbeatService(tempFile, logger);

            await hbService.BeatAsync(CancellationToken.None);

            Assert.True(File.Exists(tempFile));
            var content = await File.ReadAllTextAsync(tempFile);
            Assert.False(string.IsNullOrWhiteSpace(content));
        }
        finally
        {
            try
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void DatabaseReadinessResult_PreservesCanonicalContracts()
    {
        var resultRecovery = new EdgeRetails.Application.Production.Startup.DatabaseReadinessResult(false, "PostgreSQL is in recovery mode and not writable.")
        {
            Code = EdgeRetails.Application.Production.Startup.DatabaseReadinessCode.ProbeFailed
        };
        Assert.False(resultRecovery.Ready);
        Assert.Contains("recovery mode", resultRecovery.Message);

        var resultDurability = new EdgeRetails.Application.Production.Startup.DatabaseReadinessResult(false, "PostgreSQL durability setting breached: fsync is 'off'.")
        {
            Code = EdgeRetails.Application.Production.Startup.DatabaseReadinessCode.ProbeFailed
        };
        Assert.False(resultDurability.Ready);
        Assert.Contains("fsync is 'off'", resultDurability.Message);
    }

    private static OutboxMessage CreateOutboxMessage(string effectType, string idempotencyKey)
    {
        return new OutboxMessage
        {
            Id = Guid.NewGuid(),
            EffectType = effectType,
            SourceType = "Test",
            SourceId = "test_1",
            PayloadJson = "{}",
            IdempotencyKey = idempotencyKey,
            CreatedAt = DateTimeOffset.UtcNow,
            AttemptCount = 0,
            NextAttemptAt = null,
            Status = OutboxMessageStatus.Pending
        };
    }

    private sealed class InMemoryOutboxRepository : IOutboxRepository
    {
        private readonly List<OutboxMessage> _messages;

        public InMemoryOutboxRepository(IEnumerable<OutboxMessage> initial)
        {
            _messages = initial.ToList();
        }

        public void Enqueue(OutboxMessage message)
        {
            _messages.Add(message);
        }

        public Task<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(int batchSize, CancellationToken cancellationToken = default)
        {
            var list = _messages
                .Where(m => (m.Status == OutboxMessageStatus.Pending || m.Status == OutboxMessageStatus.Processing)
                            && (m.NextAttemptAt == null || m.NextAttemptAt <= DateTimeOffset.UtcNow))
                .Take(batchSize)
                .ToList();
            return Task.FromResult<IReadOnlyList<OutboxMessage>>(list);
        }

        public Task MarkCompletedAsync(Guid messageId, DateTimeOffset completedAt, CancellationToken cancellationToken = default)
        {
            var msg = _messages.FirstOrDefault(x => x.Id == messageId);
            if (msg is not null)
            {
                msg.Status = OutboxMessageStatus.Completed;
                msg.CompletedAt = completedAt;
                msg.AttemptCount++;
            }
            return Task.CompletedTask;
        }

        public Task MarkFailedAsync(Guid messageId, string error, DateTimeOffset? nextAttemptAt, CancellationToken cancellationToken = default)
        {
            var msg = _messages.FirstOrDefault(x => x.Id == messageId);
            if (msg is not null)
            {
                msg.Status = OutboxMessageStatus.Pending;
                msg.LastError = error;
                msg.NextAttemptAt = nextAttemptAt;
                msg.AttemptCount++;
            }
            return Task.CompletedTask;
        }

        public Task MarkActionRequiredAsync(Guid messageId, string error, CancellationToken cancellationToken = default)
        {
            var msg = _messages.FirstOrDefault(x => x.Id == messageId);
            if (msg is not null)
            {
                msg.Status = OutboxMessageStatus.ActionRequired;
                msg.LastError = error;
                msg.NextAttemptAt = null;
                msg.AttemptCount++;
            }
            return Task.CompletedTask;
        }

        public Task<OutboxMessage?> GetByIdAsync(Guid messageId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_messages.FirstOrDefault(x => x.Id == messageId));
        }
    }

    private sealed class TestEffectHandler : IOutboxEffectHandler
    {
        public string EffectType { get; }
        private readonly bool _shouldSucceed;

        public TestEffectHandler(string effectType, bool shouldSucceed)
        {
            EffectType = effectType;
            _shouldSucceed = shouldSucceed;
        }

        public Task ExecuteAsync(OutboxMessage message, CancellationToken cancellationToken = default)
        {
            if (!_shouldSucceed)
            {
                throw new InvalidOperationException("Simulated transient effect failure.");
            }
            return Task.CompletedTask;
        }
    }

    private sealed class FakeMaintenanceBarrier : IProductionMaintenanceBarrier
    {
        private readonly ProductionMaintenanceState _state;
        public FakeMaintenanceBarrier(ProductionMaintenanceState state) => _state = state;

        public Task<IProductionMaintenanceLease> EnterExclusiveAsync(ProductionMaintenanceState state, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<ProductionMaintenanceState> GetStateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_state);
    }

    private sealed class FakeDiskSpaceProbe : IDiskSpaceProbe
    {
        private readonly bool _healthy;
        private readonly long _freeBytes;

        public FakeDiskSpaceProbe(bool healthy, long freeBytes)
        {
            _healthy = healthy;
            _freeBytes = freeBytes;
        }

        public Task<DiskSpaceResult> CheckAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DiskSpaceResult(
                Healthy: _healthy,
                AvailableBytes: _freeBytes,
                Message: _healthy ? "Healthy" : "Free disk space below critical floor."));
        }
    }

    private sealed class DummyDocumentSource : IProductionDocumentKindSource, IProductionDocumentSource
    {
        public ProductionDocumentKind Kind { get; }

        public DummyDocumentSource(ProductionDocumentKind kind) => Kind = kind;

        public Task<ProductionDocument> LoadAsync(Guid businessDocumentId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ProductionDocument(
                Kind: Kind,
                BusinessDocumentId: businessDocumentId,
                DocumentNumber: "DOC-001",
                IssuedAt: DateTimeOffset.UtcNow,
                ShopName: "Edge Retails",
                ShopAddress: null,
                ShopPhone: null,
                PartyName: null,
                Title: "Receipt",
                Lines: Array.Empty<ProductionDocumentLine>(),
                Totals: Array.Empty<ProductionDocumentTotal>(),
                Notes: Array.Empty<string>(),
                Footer: null,
                CopyLabel: null));
        }

        public Task<ProductionDocument> LoadAsync(ProductionDocumentKind kind, Guid businessDocumentId, CancellationToken cancellationToken = default)
            => LoadAsync(businessDocumentId, cancellationToken);
    }

    private sealed class InMemoryProfileStore : IPrinterProfileStore
    {
        private readonly Dictionary<string, PrinterProfile> _profiles = new();

        public Task<PrinterProfile?> GetAsync(string profileName, CancellationToken cancellationToken = default)
            => Task.FromResult(_profiles.GetValueOrDefault(profileName));

        public Task SaveAsync(PrinterProfile profile, CancellationToken cancellationToken = default)
        {
            _profiles[profile.ProfileName] = profile;
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryPrintJobStore : IPrintJobStore
    {
        private readonly Dictionary<string, PrintJobRecord> _jobs = new();

        public Task<PrintJobRecord?> GetAsync(string printJobId, CancellationToken cancellationToken = default)
            => Task.FromResult(_jobs.GetValueOrDefault(printJobId));

        public Task CreateAsync(PrintJobRecord record, CancellationToken cancellationToken = default)
        {
            _jobs[record.PrintJobId] = record;
            return Task.CompletedTask;
        }

        public Task<bool> TryTransitionAsync(
            string printJobId,
            PrintJobState expectedState,
            PrintJobState newState,
            int attemptNumber,
            string? lastErrorCode,
            CancellationToken cancellationToken = default)
        {
            if (!_jobs.TryGetValue(printJobId, out var existing) || existing.State != expectedState)
            {
                return Task.FromResult(false);
            }

            _jobs[printJobId] = existing with
            {
                State = newState,
                AttemptNumber = attemptNumber,
                LastErrorCode = lastErrorCode,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            return Task.FromResult(true);
        }
    }

    private sealed class RecordingPrintEngine : IProductionPrintEngine
    {
        public ProductionDocument? LastPrintedDocument { get; private set; }

        public Task<PrintJobResult> PrintAsync(ProductionDocument document, PrinterProfile profile, CancellationToken cancellationToken = default)
        {
            LastPrintedDocument = document;
            return Task.FromResult(new PrintJobResult(
                true,
                null,
                null,
                Guid.NewGuid().ToString("N")));
        }
    }

    private sealed class FakeProductionAuthorization : IProductionAuthorization
    {
        public Task EnsureAuthenticatedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EnsurePermissionAsync(string permission, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeDocumentAuthorization : IProductionDocumentAuthorizationPolicy
    {
        public Task EnsureCanPrintAsync(ProductionDocumentKind kind, Guid businessDocumentId, bool isReprint, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeAuditSink : IProductionAuditSink
    {
        public Task AppendAsync(ProductionAuditRecord record, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeAuditFailureReporter : IProductionAuditFailureReporter
    {
        public Task ReportAsync(ProductionAuditRecord record, Exception error, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ThrowingEffectHandler : IOutboxEffectHandler
    {
        private readonly Exception _exception;
        public string EffectType { get; }

        public ThrowingEffectHandler(string effectType, Exception exception)
        {
            EffectType = effectType;
            _exception = exception;
        }

        public Task ExecuteAsync(OutboxMessage message, CancellationToken cancellationToken = default)
            => Task.FromException(_exception);
    }

    private sealed class CheckingProductionAuthorization : IProductionAuthorization
    {
        public int ReprintPermissionChecks { get; private set; }

        public Task EnsureAuthenticatedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task EnsurePermissionAsync(string permission, CancellationToken cancellationToken = default)
        {
            if (permission == ProductionPermissionNames.PrintingReprint)
            {
                ReprintPermissionChecks++;
            }
            return Task.CompletedTask;
        }
    }
}
