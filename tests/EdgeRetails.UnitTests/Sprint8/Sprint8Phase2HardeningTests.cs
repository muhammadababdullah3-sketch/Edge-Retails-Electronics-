using System.Text;
using EdgeRetails.Application.Production;
using EdgeRetails.Application.Production.Backup;
using EdgeRetails.Application.Production.Licensing;
using EdgeRetails.Application.Production.Printing;
using EdgeRetails.Application.Production.Startup;
using EdgeRetails.Infrastructure.Production.Backup;
using EdgeRetails.Infrastructure.Production.Startup;
using Xunit;

namespace EdgeRetails.UnitTests.Sprint8;

public sealed class Sprint8Phase2HardeningTests
{
    [Fact]
    public async Task InitialLicenseImport_WhenArtifactAlreadyExists_IsBlockedInsideApplicationHandler()
    {
        var store = new MemoryLicenseStore("existing");
        var audit = Audit();
        var installation = new LicenseInstallationService(new FixedLicenseValidator(ValidLicense()), store, audit);
        var handler = new ImportLicenseHandler(store, installation, audit);

        await Assert.ThrowsAsync<LicenseImportRequiresAuthorizationException>(() => handler.HandleAsync("replacement", "c"));
        Assert.Equal(0, store.PersistCount);
    }

    [Fact]
    public async Task LicenseReplacement_RequiresSettingsManageBeforePersistence()
    {
        var store = new MemoryLicenseStore("existing");
        var audit = Audit();
        var installation = new LicenseInstallationService(new FixedLicenseValidator(ValidLicense()), store, audit);
        var handler = new ReplaceLicenseHandler(new DenyAuthorization(), installation);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => handler.HandleAsync("replacement", "c"));
        Assert.Equal(0, store.PersistCount);
    }

    [Fact]
    public async Task RuntimeLicense_InfrastructureFailure_FailsClosedWithoutLeakingExceptionText()
    {
        var runtime = new RuntimeLicenseService(new ThrowingLicenseStore(), new FixedLicenseValidator(ValidLicense()));
        var result = await runtime.ValidatePersistedAsync();
        Assert.Equal(LicenseValidationStatus.ValidationUnavailable, result.Status);
        Assert.Equal("license.validation_unavailable", result.ErrorCode);
        Assert.DoesNotContain("super-secret", result.Message ?? string.Empty);
    }


    [Fact]
    public async Task LicensePersistenceFailure_ReturnsControlledFailClosedResult()
    {
        var payload = ValidLicense();
        var service = new LicenseInstallationService(new FixedLicenseValidator(payload), new PersistFailLicenseStore(), Audit());
        var result = await service.ValidateAndPersistAsync("valid", ProductionAuditEvents.LicenseReplaced, "c");
        Assert.Equal(LicenseValidationStatus.ValidationUnavailable, result.Status);
        Assert.Equal("license.persistence_failed", result.ErrorCode);
    }

    [Fact]
    public async Task PrintAuthorization_DenialOccursBeforeDocumentLoadOrPhysicalPrint()
    {
        var source = new RecordingDocumentSource();
        var engine = new RecordingPrintEngine();
        var handler = new PrintDocumentHandler(
            new AllowAuthorization(),
            new DenyDocumentAuthorization(),
            source,
            new SingleProfileStore(),
            new AlwaysValidPrinterProfileValidator(),
            new MemoryPrintJobStore(),
            engine,
            Audit());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => handler.HandleAsync(new PrintDocumentCommand(
            ProductionDocumentKind.PurchaseDocument, Guid.NewGuid(), "A4", false, "c")));
        Assert.False(source.Called);
        Assert.False(engine.Called);
    }

    [Fact]
    public async Task Startup_DatabaseAhead_IsFailClosedWithActionableCode()
    {
        var coordinator = Coordinator(
            new ReadyDb(),
            new FixedMigration(new MigrationCompatibilityResult(false, "ahead") { State = MigrationCompatibilityState.DatabaseAhead }),
            new CompleteSetup(),
            new NoSession());

        var result = await coordinator.RunAsync();
        Assert.Equal(StartupDisposition.Blocked, result.Disposition);
        Assert.Equal("migration.database_ahead", result.ErrorCode);
        Assert.Contains("Do not downgrade", result.RecommendedAction ?? string.Empty);
    }

    [Fact]
    public async Task Startup_ProbeException_IsMappedWithoutRawSecretLeak()
    {
        var coordinator = Coordinator(new ThrowingDb(), new FixedMigration(new(true, "ok")), new CompleteSetup(), new NoSession());
        var result = await coordinator.RunAsync();
        Assert.Equal(StartupDisposition.Blocked, result.Disposition);
        Assert.Equal("database.probe_failed", result.ErrorCode);
        Assert.DoesNotContain("super-secret", result.Message);
    }


    [Theory]
    [InlineData(MigrationCompatibilityState.Compatible, false, "001,002", "001,002")]
    [InlineData(MigrationCompatibilityState.DatabaseBehind, false, "001,002", "001")]
    [InlineData(MigrationCompatibilityState.DatabaseAhead, false, "001", "001,002")]
    [InlineData(MigrationCompatibilityState.HistoryDiverged, false, "001,002,003", "001,003")]
    [InlineData(MigrationCompatibilityState.ModelDrift, true, "001,002", "001,002")]
    public void MigrationHistoryEvaluator_FailsClosedForAheadBehindDivergedAndModelDrift(
        MigrationCompatibilityState expected, bool modelDrift, string knownCsv, string appliedCsv)
    {
        var known = knownCsv.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var applied = appliedCsv.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var result = MigrationHistoryCompatibilityEvaluator.Evaluate(known, applied, modelDrift);
        Assert.Equal(expected, result.State);
        Assert.Equal(expected == MigrationCompatibilityState.Compatible, result.Compatible);
    }

    [Fact]
    public async Task SessionRecovery_ExpiredSession_IsClearedAndNeverAuthenticatesAutomatically()
    {
        var store = new SessionStore(new ProductionSessionSnapshot(Guid.NewGuid(), "Owner", DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow.AddMinutes(-1)));
        var probe = new ProductionSessionRecoveryProbe(store, new EligibleUser());
        var result = await probe.CheckAsync();
        Assert.False(result.HasRecoverableSession);
        Assert.Equal(SessionRecoveryState.Expired, result.State);
        Assert.True(store.Cleared);
    }

    [Fact]
    public async Task BackupEncryption_DoesNotZeroProviderOwnedKeyBuffer()
    {
        var root = TempRoot();
        try
        {
            var provider = new ReusedKeyProvider();
            var expected = provider.Buffer.ToArray();
            var plain = Path.Combine(root, "plain.dump");
            var encrypted = Path.Combine(root, "backup.erbak");
            await File.WriteAllTextAsync(plain, "forensic-backup-content");
            await new AesGcmBackupProtector(provider).ProtectAsync(plain, encrypted);
            Assert.Equal(expected, provider.Buffer);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task BackupEncryption_TruncationAtChunkBoundary_IsRejectedByAuthenticatedFooter()
    {
        var root = TempRoot();
        try
        {
            var provider = new ReusedKeyProvider();
            var protector = new AesGcmBackupProtector(provider);
            var plain = Path.Combine(root, "plain.dump");
            var encrypted = Path.Combine(root, "backup.erbak");
            var restored = Path.Combine(root, "restored.dump");
            await File.WriteAllBytesAsync(plain, Encoding.UTF8.GetBytes("content"));
            await protector.ProtectAsync(plain, encrypted);
            using (var stream = new FileStream(encrypted, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                stream.SetLength(stream.Length - 24); // footer header (8) + tag (16)
            }

            await Assert.ThrowsAsync<InvalidDataException>(() => protector.UnprotectAsync(encrypted, restored));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task BackupManifest_TamperInvalidatesAuthentication()
    {
        var provider = new ReusedKeyProvider();
        var auth = new HmacBackupManifestAuthenticator(provider);
        var manifest = Manifest("prod");
        var signature = await auth.ComputeAuthenticationAsync(manifest);
        Assert.True(await auth.VerifyAuthenticationAsync(manifest, signature));
        Assert.False(await auth.VerifyAuthenticationAsync(manifest with { DatabaseName = "other" }, signature));
    }

    [Fact]
    public async Task BackupHistory_CorruptManifest_IsVisibleAsDiagnosticIssue()
    {
        var root = TempRoot();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "broken.erbak.manifest.json"), "{broken");
            var history = new BackupHistoryService(new HmacBackupManifestAuthenticator(new ReusedKeyProvider()));
            var diagnostic = await history.ReadDiagnosticsAsync(root);
            Assert.Empty(diagnostic.ValidBackups);
            Assert.Single(diagnostic.Issues);
            Assert.Equal("backup.manifest_invalid_json", diagnostic.Issues[0].Code);
        }
        finally { DeleteRoot(root); }
    }

    private static ProductionStartupCoordinator Coordinator(
        IDatabaseReadinessProbe db,
        IMigrationCompatibilityProbe migration,
        ISetupStateProbe setup,
        ISessionRecoveryProbe session)
    {
        var runtime = new RuntimeLicenseService(new MemoryLicenseStore("valid"), new FixedLicenseValidator(ValidLicense()));
        return new(runtime, db, migration, new FixedMaintenanceBarrier(ProductionMaintenanceState.Normal), setup, session);
    }

    private static LicenseValidationResult ValidLicense()
        => new(LicenseValidationStatus.Valid,
            new LicensePayload("L1", "Customer", "Store", "ExistingPlan", DateTimeOffset.UtcNow, null, "DEVICE", null, Array.Empty<string>()),
            null);

    private static BackupManifest Manifest(string database)
        => new(Guid.NewGuid(), database, "b.erbak", new string('A', 64), 10, DateTimeOffset.UtcNow, "16", "pg_dump 16", "test", "schema", "AES-256-GCM-CHUNKED-V2") { FormatVersion = 2 };

    private static ProductionAuditCoordinator Audit()
        => new(new NoopAuditSink(), new NoopAuditFailureReporter());

    private static string TempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "EdgeRetailsPhase2", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteRoot(string path) { try { if (Directory.Exists(path)) { Directory.Delete(path, true); } } catch { } }

    private sealed class FixedLicenseValidator(LicenseValidationResult result) : ILicenseValidator
    {
        public Task<LicenseValidationResult> ValidateAsync(string signedLicenseJson, CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    private sealed class MemoryLicenseStore(string? raw) : ILicenseStore
    {
        private string? _raw = raw;
        public int PersistCount { get; private set; }
        public Task<bool> ExistsAsync(CancellationToken cancellationToken = default) => Task.FromResult(_raw is not null);
        public Task<string?> ReadRawAsync(CancellationToken cancellationToken = default) => Task.FromResult(_raw);
        public Task<bool> TryPersistInitialRawAsync(string signedLicenseJson, CancellationToken cancellationToken = default)
        {
            if (_raw is not null)
            {
                return Task.FromResult(false);
            }

            _raw = signedLicenseJson;
            PersistCount++;
            return Task.FromResult(true);
        }
        public Task PersistRawAsync(string signedLicenseJson, CancellationToken cancellationToken = default) { _raw = signedLicenseJson; PersistCount++; return Task.CompletedTask; }
    }

    private sealed class PersistFailLicenseStore : ILicenseStore
    {
        public Task<bool> ExistsAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<string?> ReadRawAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>("existing");
        public Task<bool> TryPersistInitialRawAsync(string signedLicenseJson, CancellationToken cancellationToken = default) => throw new IOException("disk failure");
        public Task PersistRawAsync(string signedLicenseJson, CancellationToken cancellationToken = default) => throw new IOException("disk failure");
    }

    private sealed class ThrowingLicenseStore : ILicenseStore
    {
        public Task<bool> ExistsAsync(CancellationToken cancellationToken = default) => throw new IOException("super-secret");
        public Task<string?> ReadRawAsync(CancellationToken cancellationToken = default) => throw new IOException("super-secret");
        public Task<bool> TryPersistInitialRawAsync(string signedLicenseJson, CancellationToken cancellationToken = default) => throw new IOException("super-secret");
        public Task PersistRawAsync(string signedLicenseJson, CancellationToken cancellationToken = default) => throw new IOException("super-secret");
    }

    private sealed class FixedMaintenanceBarrier(ProductionMaintenanceState state) : IProductionMaintenanceBarrier
    {
        public Task<IProductionMaintenanceLease> EnterExclusiveAsync(ProductionMaintenanceState requestedState, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<ProductionMaintenanceState> GetStateAsync(CancellationToken cancellationToken = default) => Task.FromResult(state);
    }

    private sealed class AllowAuthorization : IProductionAuthorization
    {
        public Task EnsureAuthenticatedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EnsurePermissionAsync(string permission, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class DenyAuthorization : IProductionAuthorization
    {
        public Task EnsureAuthenticatedAsync(CancellationToken cancellationToken = default) => throw new UnauthorizedAccessException();
        public Task EnsurePermissionAsync(string permission, CancellationToken cancellationToken = default) => throw new UnauthorizedAccessException();
    }

    private sealed class DenyDocumentAuthorization : IProductionDocumentAuthorizationPolicy
    {
        public Task EnsureCanPrintAsync(ProductionDocumentKind kind, Guid businessDocumentId, bool isReprint, CancellationToken cancellationToken = default)
            => throw new UnauthorizedAccessException();
    }

    private sealed class RecordingDocumentSource : IProductionDocumentSource
    {
        public bool Called { get; private set; }
        public Task<ProductionDocument> LoadAsync(ProductionDocumentKind kind, Guid businessDocumentId, CancellationToken cancellationToken = default)
        {
            Called = true;
            return Task.FromResult(new ProductionDocument(kind, businessDocumentId, "D", DateTimeOffset.UtcNow, "Shop", null, null, null, "Title", Array.Empty<ProductionDocumentLine>(), Array.Empty<ProductionDocumentTotal>(), Array.Empty<string>(), null));
        }
    }

    private sealed class SingleProfileStore : IPrinterProfileStore
    {
        public Task<PrinterProfile?> GetAsync(string profileName, CancellationToken cancellationToken = default)
            => Task.FromResult<PrinterProfile?>(new(profileName, "Printer", PaperKind.A4, 1, false));
        public Task SaveAsync(PrinterProfile profile, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class AlwaysValidPrinterProfileValidator : IPrinterProfileValidator
    {
        public Task<PrinterProfileValidationResult> ValidateAsync(PrinterProfile profile, CancellationToken cancellationToken = default)
            => Task.FromResult(PrinterProfileValidationResult.Valid);
    }

    private sealed class MemoryPrintJobStore : IPrintJobStore
    {
        private readonly Dictionary<string, PrintJobRecord> _items = new();
        public Task<PrintJobRecord?> GetAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(_items.GetValueOrDefault(id));
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

    private sealed class RecordingPrintEngine : IProductionPrintEngine
    {
        public bool Called { get; private set; }
        public Task<PrintJobResult> PrintAsync(ProductionDocument document, PrinterProfile profile, CancellationToken cancellationToken = default)
        {
            Called = true;
            return Task.FromResult(new PrintJobResult(true, null, null));
        }
    }

    private sealed class ReadyDb : IDatabaseReadinessProbe
    {
        public Task<DatabaseReadinessResult> CheckAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new DatabaseReadinessResult(true, "ok") { Code = DatabaseReadinessCode.Ready });
    }

    private sealed class ThrowingDb : IDatabaseReadinessProbe
    {
        public Task<DatabaseReadinessResult> CheckAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Pass" + "word=" + "super-secret");
    }

    private sealed class FixedMigration(MigrationCompatibilityResult value) : IMigrationCompatibilityProbe
    {
        public Task<MigrationCompatibilityResult> CheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(value);
    }

    private sealed class CompleteSetup : ISetupStateProbe
    {
        public Task<SetupStateResult> CheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(new SetupStateResult(true, "ok"));
    }

    private sealed class NoSession : ISessionRecoveryProbe
    {
        public Task<SessionRecoveryResult> CheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(new SessionRecoveryResult(false, null));
    }

    private sealed class SessionStore(ProductionSessionSnapshot? session) : IProductionSessionStore
    {
        public bool Cleared { get; private set; }
        public Task<ProductionSessionSnapshot?> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(session);
        public Task ClearAsync(CancellationToken cancellationToken = default) { Cleared = true; return Task.CompletedTask; }
    }

    private sealed class EligibleUser : IProductionSessionUserProbe
    {
        public Task<SessionUserEligibility> CheckAsync(Guid userId, CancellationToken cancellationToken = default)
            => Task.FromResult(new SessionUserEligibility(true, "Owner"));
    }

    private sealed class ReusedKeyProvider : IBackupEncryptionKeyProvider
    {
        public byte[] Buffer { get; } = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        public Task<byte[]> GetKeyAsync(CancellationToken cancellationToken = default) => Task.FromResult(Buffer);
    }

    private sealed class NoopAuditSink : IProductionAuditSink
    {
        public Task AppendAsync(ProductionAuditRecord record, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoopAuditFailureReporter : IProductionAuditFailureReporter
    {
        public Task ReportAsync(ProductionAuditRecord record, Exception error, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
