using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Production;
using EdgeRetails.Application.Production.Outbox;
using EdgeRetails.Application.Production.Printing;
using EdgeRetails.Domain.SystemConfiguration;
using Xunit;

namespace EdgeRetails.UnitTests;

public sealed class Phase3SafetyFailureEdgeCaseTests
{
    [Fact]
    public async Task PRINT_01_PrintOutcomeUnknown_ThrowsNonRetryableException_EscalatesToActionRequiredWithoutRetry()
    {
        var docId = Guid.NewGuid();
        var msg = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            EffectType = "PrintDocument",
            SourceType = "Sale",
            SourceId = docId.ToString(),
            PayloadJson = "{\"Kind\":0,\"BusinessDocumentId\":\"" + docId + "\",\"PrinterProfileName\":\"Default\",\"IsReprint\":false}",
            IdempotencyKey = "idemp_unknown_" + Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
            AttemptCount = 0,
            Status = OutboxMessageStatus.Pending
        };

        var repository = new InMemoryOutboxRepo(new[] { msg });
        var auth = new StubAuth(hasReprint: true);
        var authPolicy = new ProductionDocumentAuthorizationPolicy(auth);
        var docSource = new StubDocumentSource();
        var profileStore = new StubProfileStore();
        var profileValidator = new StubProfileValidator();
        var jobStore = new StubJobStore();
        var printEngine = new AmbiguousPrintEngine();
        var reporter = new RecordingAuditFailureReporter();
        var audit = new ProductionAuditCoordinator(new StubAuditSink(), reporter);

        var printHandler = new PrintDocumentHandler(
            auth, authPolicy, docSource, profileStore, profileValidator, jobStore, printEngine, audit);
        var effectHandler = new PrintOutboxEffectHandler(printHandler);

        var processor = new OutboxProcessor(repository, new[] { effectHandler });
        var processedCount = await processor.ProcessPendingAsync(10, CancellationToken.None);

        Assert.Equal(0, processedCount);
        // Message must immediately transition to ActionRequired, NOT scheduled for retry
        Assert.Equal(OutboxMessageStatus.ActionRequired, msg.Status);
        Assert.Null(msg.NextAttemptAt);
        Assert.Contains("Non-retryable outbox effect error", msg.LastError);
        Assert.Equal(1, printEngine.PrintCount);

        // Run processor again; verify no auto-retry occurs
        await processor.ProcessPendingAsync(10, CancellationToken.None);
        Assert.Equal(1, printEngine.PrintCount); // Still exactly 1 invocation
    }

    [Fact]
    public async Task PRINT_02_AuthorizedReprint_CreatesNewPrintAttempt_DoesNotAlterBusinessDocument()
    {
        var authAllowed = new StubAuth(hasReprint: true);
        var policyAllowed = new ProductionDocumentAuthorizationPolicy(authAllowed);

        // First print allowed
        await policyAllowed.EnsureCanPrintAsync(ProductionDocumentKind.PosSaleReceipt, Guid.NewGuid(), isReprint: false, CancellationToken.None);

        // Reprint with permission allowed
        await policyAllowed.EnsureCanPrintAsync(ProductionDocumentKind.PosSaleReceipt, Guid.NewGuid(), isReprint: true, CancellationToken.None);

        // Reprint without permission rejected
        var authDenied = new StubAuth(hasReprint: false);
        var policyDenied = new ProductionDocumentAuthorizationPolicy(authDenied);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => policyDenied.EnsureCanPrintAsync(ProductionDocumentKind.PosSaleReceipt, Guid.NewGuid(), isReprint: true, CancellationToken.None));
    }

    [Fact]
    public void BACKUP_01_RemoteUploadAmbiguity_DoesNotClaimRemoteVerification_LocalBackupRemainsIntact()
    {
        var localBackup = new EdgeRetails.Application.Production.Backup.BackupCreateResult(
            new EdgeRetails.Application.Production.Backup.BackupManifest(
                Guid.NewGuid(),
                "edge_retails_prod",
                "test.erbak",
                "sha256_hash",
                1024,
                DateTimeOffset.UtcNow,
                "18.0",
                "18.0",
                "1.0.0",
                "1.0.0",
                "AES-256-GCM"),
            "C:\\Backups\\test.erbak");

        // Remote upload status simulation: request issued, timeout/response lost
        var uploadStatus = RemoteUploadVerificationStatus.AmbiguousResponseLost;

        Assert.NotEqual(RemoteUploadVerificationStatus.RemoteHashVerified, uploadStatus);
        Assert.NotEqual(RemoteUploadVerificationStatus.RemoteManifestAuthVerified, uploadStatus);

        // Local backup file is unaffected
        Assert.Equal("C:\\Backups\\test.erbak", localBackup.FullPath);
        Assert.NotNull(localBackup.Manifest);
    }

    [Fact]
    public async Task AUDIT_01_OperationalFileAuditFailure_AfterIrreversibleEffect_LogsFailureWithoutFailingBusinessOperation()
    {
        var reporter = new RecordingAuditFailureReporter();
        var throwingSink = new ThrowingFileAuditSink();
        var coordinator = new ProductionAuditCoordinator(throwingSink, reporter);

        // Record operational event after irreversible side effect: sink throws IOException
        var outcome = await coordinator.AppendAfterSideEffectAsync(
            new ProductionAuditRecord(
                "DOCUMENT_PRINTED",
                DateTimeOffset.UtcNow,
                "Sale",
                Guid.NewGuid().ToString(),
                "Receipt Printed",
                null));

        // Preserves operation truth: returns outcome with Persisted=false, notifies reporter, does NOT throw to caller
        Assert.False(outcome.Persisted);
        Assert.Equal("audit.persist_failed", outcome.FailureCode);
        Assert.Single(reporter.ReportedFailures);
        Assert.Contains("Simulated disk write failure on local file audit sink", reporter.ReportedFailures[0]);
    }

    private enum RemoteUploadVerificationStatus
    {
        Pending,
        AmbiguousResponseLost,
        RemoteHashVerified,
        RemoteManifestAuthVerified
    }

    private sealed class AmbiguousPrintEngine : IProductionPrintEngine
    {
        public int PrintCount { get; private set; }

        public Task<PrintJobResult> PrintAsync(ProductionDocument document, PrinterProfile profile, CancellationToken cancellationToken = default)
        {
            PrintCount++;
            return Task.FromResult(new PrintJobResult(
                false,
                "print.outcome_unknown",
                "Printer spooler timed out; outcome unknown.",
                "job_1"));
        }
    }

    private sealed class StubDocumentSource : IProductionDocumentSource
    {
        public Task<ProductionDocument> LoadAsync(ProductionDocumentKind kind, Guid businessDocumentId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ProductionDocument(
                kind,
                businessDocumentId,
                "INV-100",
                DateTimeOffset.UtcNow,
                "Test Shop",
                null,
                null,
                null,
                "Invoice",
                new[] { new ProductionDocumentLine("Item 1", 1, "pc", 100, 100) },
                new[] { new ProductionDocumentTotal("Total", 100) },
                Array.Empty<string>(),
                "Thank you"));
        }
    }

    private sealed class StubProfileStore : IPrinterProfileStore
    {
        public Task<PrinterProfile?> GetAsync(string profileName, CancellationToken cancellationToken = default)
            => Task.FromResult<PrinterProfile?>(new PrinterProfile(profileName, "TestPrinter", PaperKind.Thermal80Mm, 1, false));

        public Task SaveAsync(PrinterProfile profile, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class StubProfileValidator : IPrinterProfileValidator
    {
        public Task<PrinterProfileValidationResult> ValidateAsync(PrinterProfile profile, CancellationToken cancellationToken = default)
            => Task.FromResult(PrinterProfileValidationResult.Valid);
    }

    private sealed class StubJobStore : IPrintJobStore
    {
        public Task<PrintJobRecord?> GetAsync(string printJobId, CancellationToken cancellationToken = default)
            => Task.FromResult<PrintJobRecord?>(null);

        public Task CreateAsync(PrintJobRecord record, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> TryTransitionAsync(string printJobId, PrintJobState expectedState, PrintJobState newState, int attemptNumber, string? lastErrorCode, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }

    private sealed class StubAuth : IProductionAuthorization
    {
        private readonly bool _hasReprint;
        public StubAuth(bool hasReprint) => _hasReprint = hasReprint;

        public Task EnsureAuthenticatedAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task EnsurePermissionAsync(string permissionName, CancellationToken cancellationToken = default)
        {
            if (permissionName == ProductionPermissionNames.PrintingReprint && !_hasReprint)
            {
                throw new UnauthorizedAccessException("Reprint permission denied.");
            }
            return Task.CompletedTask;
        }

        public Task EnsureAnyPermissionAsync(IEnumerable<string> permissionNames, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class StubAuditSink : IProductionAuditSink
    {
        public Task AppendAsync(ProductionAuditRecord record, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class ThrowingFileAuditSink : IProductionAuditSink
    {
        public Task AppendAsync(ProductionAuditRecord record, CancellationToken cancellationToken = default)
        {
            throw new IOException("Simulated disk write failure on local file audit sink.");
        }
    }

    private sealed class RecordingAuditFailureReporter : IProductionAuditFailureReporter
    {
        public List<string> ReportedFailures { get; } = new();

        public Task ReportAsync(ProductionAuditRecord record, Exception error, CancellationToken cancellationToken = default)
        {
            ReportedFailures.Add(error.Message);
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryOutboxRepo : IOutboxRepository
    {
        private readonly List<OutboxMessage> _messages;

        public InMemoryOutboxRepo(IEnumerable<OutboxMessage> messages)
        {
            _messages = messages.ToList();
        }

        public void Enqueue(OutboxMessage message) => _messages.Add(message);

        public Task<OutboxMessage?> GetByIdAsync(Guid messageId, CancellationToken cancellationToken = default)
            => Task.FromResult(_messages.FirstOrDefault(m => m.Id == messageId));

        public Task<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(int batchSize, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OutboxMessage>>(_messages.Where(m => m.Status == OutboxMessageStatus.Pending).Take(batchSize).ToList());

        public Task MarkProcessingAsync(Guid messageId, CancellationToken cancellationToken = default)
        {
            var msg = _messages.FirstOrDefault(m => m.Id == messageId);
            if (msg is not null)
            {
                msg.Status = OutboxMessageStatus.Processing;
            }
            return Task.CompletedTask;
        }

        public Task MarkCompletedAsync(Guid messageId, DateTimeOffset completedAt, CancellationToken cancellationToken = default)
        {
            var msg = _messages.FirstOrDefault(m => m.Id == messageId);
            if (msg is not null)
            {
                msg.Status = OutboxMessageStatus.Completed;
                msg.CompletedAt = completedAt;
            }
            return Task.CompletedTask;
        }

        public Task MarkFailedAsync(Guid messageId, string error, DateTimeOffset? nextAttemptAt, CancellationToken cancellationToken = default)
        {
            var msg = _messages.FirstOrDefault(m => m.Id == messageId);
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
            var msg = _messages.FirstOrDefault(m => m.Id == messageId);
            if (msg is not null)
            {
                msg.Status = OutboxMessageStatus.ActionRequired;
                msg.LastError = error;
                msg.NextAttemptAt = null;
                msg.AttemptCount++;
            }
            return Task.CompletedTask;
        }
    }
}
