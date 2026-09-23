using System.Security.Cryptography;
using System.Text.Json;
using EdgeRetails.Application.Production;
using EdgeRetails.Application.Production.Backup;
using EdgeRetails.Application.Production.Licensing;
using EdgeRetails.Application.Production.Printing;
using EdgeRetails.Infrastructure.Production.Backup;
using Xunit;

namespace EdgeRetails.UnitTests.Sprint8;

public sealed class Sprint8Phase1SafetyTests
{
    [Fact]
    public void BackupPathSafety_RejectsTraversalAndRootedPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "EdgeRetailsPhase1Path", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Throws<InvalidDataException>(() => BackupArtifactPathSafety.ResolveOwnedBackupPath(root, ".." + Path.DirectorySeparatorChar + "outside.erbak"));
            Assert.Throws<InvalidDataException>(() => BackupArtifactPathSafety.ResolveOwnedBackupPath(root, Path.GetFullPath(Path.Combine(root, "outside.erbak"))));
            var safe = BackupArtifactPathSafety.ResolveOwnedBackupPath(root, "safe.erbak");
            Assert.Equal(Path.Combine(root, "safe.erbak"), safe);
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    public async Task Retention_CorruptTraversalManifest_NeverDeletesOutsideBackupRoot()
    {
        var parent = Path.Combine(Path.GetTempPath(), "EdgeRetailsPhase1Retention", Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "backups");
        Directory.CreateDirectory(root);
        var outside = Path.Combine(parent, "outside.erbak");
        await File.WriteAllBytesAsync(outside, new byte[] { 1 });

        var malicious = Manifest(Guid.NewGuid(), "../outside.erbak", DateTimeOffset.UtcNow.AddYears(-1), 1);
        await File.WriteAllTextAsync(Path.Combine(root, "evil.erbak.manifest.json"), JsonSerializer.Serialize(malicious, JsonOptions));
        await WriteValidBackupAsync(root, "keep.erbak", Guid.NewGuid(), DateTimeOffset.UtcNow);

        try
        {
            await new BackupHistoryService(new TestManifestAuthenticator()).ApplyRetentionAsync(root, new BackupRetentionPolicy(1, null));
            Assert.True(File.Exists(outside));
            Assert.True(File.Exists(Path.Combine(root, "keep.erbak")));
        }
        finally { TryDeleteDirectory(parent); }
    }

    [Fact]
    public async Task Retention_DuplicateManifestIds_DeleteByCanonicalArtifact_NotByBackupId()
    {
        var root = Path.Combine(Path.GetTempPath(), "EdgeRetailsPhase1Retention", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var duplicateId = Guid.NewGuid();
        await WriteValidBackupAsync(root, "new.erbak", duplicateId, DateTimeOffset.UtcNow);
        await WriteValidBackupAsync(root, "old.erbak", duplicateId, DateTimeOffset.UtcNow.AddDays(-1));

        try
        {
            await new BackupHistoryService(new TestManifestAuthenticator()).ApplyRetentionAsync(root, new BackupRetentionPolicy(1, null));
            Assert.True(File.Exists(Path.Combine(root, "new.erbak")));
            Assert.False(File.Exists(Path.Combine(root, "old.erbak")));
            Assert.True(File.Exists(Path.Combine(root, "new.erbak.manifest.json")));
            Assert.False(File.Exists(Path.Combine(root, "old.erbak.manifest.json")));
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    public void RecoveryDatabaseNames_RemainUnderPostgresIdentifierLimitEvenForVeryLongProductionNames()
    {
        var target = new string('x', 200);
        var name = PostgresRecoveryDatabaseName.Create("restore", target, Guid.NewGuid());
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(name) <= 63);
        Assert.StartsWith("er_restore_", name, StringComparison.Ordinal);
    }

    [Fact]
    public void RestorePrepareRequest_DoesNotExposeRecoveryCredentialAuthorityToCaller()
    {
        var properties = typeof(RestorePrepareRequest).GetProperties();
        Assert.DoesNotContain(properties, x => x.PropertyType == typeof(PostgresMaintenanceDescriptor));
    }

    [Fact]
    public void CanonicalRestoreValidator_RefusesProductionConfigurationWithoutCompatibilityProbe()
    {
        var marker = typeof(Sprint8Phase1SafetyTests).Assembly.Location;
        Assert.Throws<InvalidOperationException>(() => new CanonicalRestoreStagingValidator(
            marker, Array.Empty<IRestoreStagingCompatibilityProbe>(), new AlwaysHealthyPostgresRunner()));
    }

    [Fact]
    public async Task RestoreJournal_TamperingFailsClosed()
    {
        var root = Path.Combine(Path.GetTempPath(), "EdgeRetailsPhase1Journal", Guid.NewGuid().ToString("N"));
        var store = new HmacRestoreSessionStore(root, new JournalKeyProvider());
        var id = Guid.NewGuid();
        var session = new RestoreSessionRecord(id, "prod", "er_rst_test", 10, 11, "backup.erbak", new string('A', 64), DateTimeOffset.UtcNow, RestoreSessionState.Prepared);

        try
        {
            await store.CreateAsync(session);
            var path = Path.Combine(root, $"restore-{id:N}.journal");
            var text = await File.ReadAllTextAsync(path);
            const string marker = "\"payloadBase64\":\"";
            var payloadStart = text.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(payloadStart >= 0);
            payloadStart += marker.Length;
            var replacement = text[payloadStart] == 'A' ? 'B' : 'A';
            await File.WriteAllTextAsync(path, text[..payloadStart] + replacement + text[(payloadStart + 1)..]);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAsync(id));
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    public async Task RestoreJournal_AuthenticatedMalformedPayload_FailsControlled()
    {
        var root = Path.Combine(Path.GetTempPath(), "EdgeRetailsPhase1JournalMalformed", Guid.NewGuid().ToString("N"));
        var store = new HmacRestoreSessionStore(root, new JournalKeyProvider());
        var id = Guid.NewGuid();
        var payload = System.Text.Encoding.UTF8.GetBytes("{not-json");
        var key = Enumerable.Range(1, 32).Select(x => (byte)x).ToArray();
        byte[] mac;

        using (var hmac = new HMACSHA256(key))
        {
            mac = hmac.ComputeHash(payload);
        }

        try
        {
            Directory.CreateDirectory(root);
            var envelope = JsonSerializer.Serialize(
                new
                {
                    payloadBase64 = Convert.ToBase64String(payload),
                    hmacBase64 = Convert.ToBase64String(mac)
                },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            var path = Path.Combine(root, $"restore-{id:N}.journal");
            await File.WriteAllTextAsync(path, envelope);

            var error = await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAsync(id));
            Assert.Equal("Restore journal payload JSON is invalid.", error.Message);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(mac);
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void RestoreSessionToken_ContainsNoDatabaseNameOrPathAuthority()
    {
        var properties = typeof(RestoreSessionToken).GetProperties();
        Assert.Single(properties);
        Assert.Equal(nameof(RestoreSessionToken.RestoreId), properties[0].Name);
        Assert.Equal(typeof(Guid), properties[0].PropertyType);
    }

    [Fact]
    public async Task MaintenanceBarrier_CanExitIntoRecoveryRequiredFailClosedState()
    {
        var root = Path.Combine(Path.GetTempPath(), "EdgeRetailsPhase1Barrier", Guid.NewGuid().ToString("N"));
        var barrier = new FileProductionMaintenanceBarrier(root, new MaintenanceKeyProvider());
        try
        {
            await using (var lease = await barrier.EnterExclusiveAsync(ProductionMaintenanceState.RestoreCutover))
            {
                Assert.Equal(ProductionMaintenanceState.RestoreCutover, await barrier.GetStateAsync());
                await lease.SetExitStateAsync(ProductionMaintenanceState.RecoveryRequired);
            }
            Assert.Equal(ProductionMaintenanceState.RecoveryRequired, await barrier.GetStateAsync());
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    public async Task MaintenanceBarrier_LockHeldWithMissingState_FailsClosed()
    {
        var root = Path.Combine(Path.GetTempPath(), "EdgeRetailsPhase1BarrierRace", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var barrier = new FileProductionMaintenanceBarrier(root, new MaintenanceKeyProvider());
        var lockPath = Path.Combine(root, "production-maintenance.lock");
        try
        {
            await using var held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous);
            Assert.Equal(ProductionMaintenanceState.RestoreCutover, await barrier.GetStateAsync());
        }
        finally { TryDeleteDirectory(root); }
    }

    [Theory]
    [InlineData("{\"payloadBase64\":\"AA==\"}")]
    [InlineData("{\"payloadBase64\":\"AA==\",\"authentication\":\"AA==\"}")]
    public async Task MaintenanceBarrier_MissingOrInvalidAuthentication_FailsClosedToRecoveryRequired(
        string tamperedState)
    {
        var root = Path.Combine(Path.GetTempPath(), "EdgeRetailsPhase1BarrierTamper", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var barrier = new FileProductionMaintenanceBarrier(root, new MaintenanceKeyProvider());
        var statePath = Path.Combine(root, "production-maintenance.state.json");

        try
        {
            await File.WriteAllTextAsync(statePath, tamperedState);

            Assert.Equal(
                ProductionMaintenanceState.RecoveryRequired,
                await barrier.GetStateAsync());
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    public async Task MaintenanceBarrier_AuthenticatedStateTampering_FailsClosedToRecoveryRequired()
    {
        var root = Path.Combine(Path.GetTempPath(), "EdgeRetailsPhase1BarrierHmacTamper", Guid.NewGuid().ToString("N"));
        var barrier = new FileProductionMaintenanceBarrier(root, new MaintenanceKeyProvider());
        var statePath = Path.Combine(root, "production-maintenance.state.json");

        try
        {
            await using (var lease = await barrier.EnterExclusiveAsync(ProductionMaintenanceState.RestorePreparing))
            {
                await lease.SetExitStateAsync(ProductionMaintenanceState.RestorePreparing);
            }

            var text = await File.ReadAllTextAsync(statePath);
            var marker = "\"payloadBase64\":\"";
            var index = text.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(index >= 0);
            index += marker.Length;
            var replacement = text[index] == 'A' ? 'B' : 'A';
            await File.WriteAllTextAsync(statePath, text[..index] + replacement + text[(index + 1)..]);

            Assert.Equal(
                ProductionMaintenanceState.RecoveryRequired,
                await barrier.GetStateAsync());
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    public async Task MaintenanceWriteGuard_BlocksBusinessWritesOutsideNormalState()
    {
        var root = Path.Combine(Path.GetTempPath(), "EdgeRetailsPhase1Guard", Guid.NewGuid().ToString("N"));
        var barrier = new FileProductionMaintenanceBarrier(root, new MaintenanceKeyProvider());
        var guard = new ProductionMaintenanceWriteGuard(barrier);
        try
        {
            await guard.EnsureBusinessWritesAllowedAsync();
            await using (var lease = await barrier.EnterExclusiveAsync(ProductionMaintenanceState.RestoreCutover))
            {
                var error = await Assert.ThrowsAsync<ProductionMaintenanceException>(
                    () => guard.EnsureBusinessWritesAllowedAsync());
                Assert.Equal(ProductionMaintenanceState.RestoreCutover, error.State);
            }
            await guard.EnsureBusinessWritesAllowedAsync();
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    public async Task AuditFailureAfterCommittedSideEffect_IsReportedSeparatelyAndDoesNotThrow()
    {
        var reporter = new RecordingAuditFailureReporter();
        var coordinator = new ProductionAuditCoordinator(new ThrowingAuditSink(), reporter);
        var outcome = await coordinator.AppendAfterSideEffectAsync(new ProductionAuditRecord("TEST", DateTimeOffset.UtcNow, null, null, null, null));
        Assert.False(outcome.Persisted);
        Assert.Equal("audit.persist_failed", outcome.FailureCode);
        Assert.Equal(1, reporter.Count);
    }

    [Fact]
    public async Task SuccessfulPrint_AuditFailure_DoesNotBecomeFalsePrintFailure()
    {
        var reporter = new RecordingAuditFailureReporter();
        var handler = new PrintDocumentHandler(
            new AllowAuthorization(),
            new AllowDocumentAuthorization(),
            new FakeDocumentSource(),
            new FakeProfileStore(),
            new AlwaysValidPrinterProfileValidator(),
            new MemoryPrintJobStore(),
            new SuccessfulPrintEngine(),
            new ProductionAuditCoordinator(new ThrowingAuditSink(), reporter));

        var result = await handler.HandleAsync(new PrintDocumentCommand(
            ProductionDocumentKind.PosSaleReceipt, Guid.NewGuid(), "POS", false, "c"));

        Assert.True(result.Succeeded);
        Assert.Equal(1, reporter.Count);
    }

    [Fact]
    public async Task SuccessfulLicensePersistence_AuditFailure_DoesNotBecomeFalseImportFailure()
    {
        var reporter = new RecordingAuditFailureReporter();
        var store = new RecordingLicenseStore();
        var payload = new LicensePayload("L1", "Customer", "Store", "ExistingPlan", DateTimeOffset.UtcNow, null, "DEVICE", null, Array.Empty<string>());
        var audit = new ProductionAuditCoordinator(new ThrowingAuditSink(), reporter);
        var installation = new LicenseInstallationService(
            new FixedLicenseValidator(new LicenseValidationResult(LicenseValidationStatus.Valid, payload, null)),
            store,
            audit);
        var handler = new ImportLicenseHandler(store, installation, audit);

        var result = await handler.HandleAsync("signed-license", "c");
        Assert.True(result.IsValid);
        Assert.True(store.Persisted);
        Assert.Equal(1, reporter.Count);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string SingleByteBackupSha256 = Convert.ToHexString(SHA256.HashData(new byte[] { 1 }));

    private static BackupManifest Manifest(Guid id, string fileName, DateTimeOffset createdAt, long size)
        => new(id, "db", fileName, SingleByteBackupSha256, size, createdAt, "16", "pg_dump 16", "test", null, "AES-256-GCM");

    private static async Task WriteValidBackupAsync(string root, string fileName, Guid id, DateTimeOffset createdAt)
    {
        var path = Path.Combine(root, fileName);
        await File.WriteAllBytesAsync(path, new byte[] { 1 });
        var manifest = Manifest(id, fileName, createdAt, 1);
        var envelope = new BackupManifestEnvelope(manifest, new string('A', 64));
        await File.WriteAllTextAsync(path + ".manifest.json", JsonSerializer.Serialize(envelope, JsonOptions));
    }

    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) { Directory.Delete(path, true); } } catch { } }

    private sealed class AlwaysHealthyPostgresRunner : IPostgresProcessRunner
    {
        public Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken)
            => Task.FromResult(new ProcessResult(0, "1", string.Empty));
    }

    private sealed class JournalKeyProvider : IRestoreJournalIntegrityKeyProvider
    {
        public Task<byte[]> GetIntegrityKeyAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray());
    }

    private sealed class MaintenanceKeyProvider : IProductionMaintenanceIntegrityKeyProvider
    {
        public Task<byte[]> GetIntegrityKeyAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Enumerable.Range(65, 32).Select(x => (byte)x).ToArray());
    }

    private sealed class TestManifestAuthenticator : IBackupManifestAuthenticator
    {
        public Task<string> ComputeAuthenticationAsync(BackupManifest manifest, CancellationToken cancellationToken = default) => Task.FromResult(new string('A', 64));
        public Task<bool> VerifyAuthenticationAsync(BackupManifest manifest, string authentication, CancellationToken cancellationToken = default) => Task.FromResult(authentication == new string('A', 64));
    }

    private sealed class ThrowingAuditSink : IProductionAuditSink
    {
        public Task AppendAsync(ProductionAuditRecord record, CancellationToken cancellationToken = default)
            => throw new IOException("audit unavailable");
    }

    private sealed class RecordingAuditFailureReporter : IProductionAuditFailureReporter
    {
        public int Count { get; private set; }
        public Task ReportAsync(ProductionAuditRecord record, Exception error, CancellationToken cancellationToken = default)
        {
            Count++;
            return Task.CompletedTask;
        }
    }

    private sealed class AllowDocumentAuthorization : IProductionDocumentAuthorizationPolicy
    {
        public Task EnsureCanPrintAsync(ProductionDocumentKind kind, Guid businessDocumentId, bool isReprint, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class AllowAuthorization : IProductionAuthorization
    {
        public Task EnsureAuthenticatedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EnsurePermissionAsync(string permission, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeDocumentSource : IProductionDocumentSource
    {
        public Task<ProductionDocument> LoadAsync(ProductionDocumentKind kind, Guid businessDocumentId, CancellationToken cancellationToken = default)
            => Task.FromResult(new ProductionDocument(kind, businessDocumentId, "D1", DateTimeOffset.UtcNow, "Shop", null, null, null, "Receipt", Array.Empty<ProductionDocumentLine>(), Array.Empty<ProductionDocumentTotal>(), Array.Empty<string>(), null));
    }

    private sealed class FakeProfileStore : IPrinterProfileStore
    {
        public Task<PrinterProfile?> GetAsync(string profileName, CancellationToken cancellationToken = default)
            => Task.FromResult<PrinterProfile?>(new PrinterProfile(profileName, "Printer", PaperKind.Thermal80Mm, 1, false));
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

    private sealed class SuccessfulPrintEngine : IProductionPrintEngine
    {
        public Task<PrintJobResult> PrintAsync(ProductionDocument document, PrinterProfile profile, CancellationToken cancellationToken = default)
            => Task.FromResult(new PrintJobResult(true, null, null));
    }

    private sealed class FixedLicenseValidator : ILicenseValidator
    {
        private readonly LicenseValidationResult _result;
        public FixedLicenseValidator(LicenseValidationResult result) => _result = result;
        public Task<LicenseValidationResult> ValidateAsync(string signedLicenseJson, CancellationToken cancellationToken = default) => Task.FromResult(_result);
    }

    private sealed class RecordingLicenseStore : ILicenseStore
    {
        public bool Persisted { get; private set; }
        public Task<bool> ExistsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Persisted);
        public Task<string?> ReadRawAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<bool> TryPersistInitialRawAsync(string signedLicenseJson, CancellationToken cancellationToken = default)
        {
            if (Persisted)
            {
                return Task.FromResult(false);
            }

            Persisted = true;
            return Task.FromResult(true);
        }
        public Task PersistRawAsync(string signedLicenseJson, CancellationToken cancellationToken = default) { Persisted = true; return Task.CompletedTask; }
    }
}
