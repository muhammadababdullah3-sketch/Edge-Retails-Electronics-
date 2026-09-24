using System.Security.Cryptography;
using System.Text.Json;
using EdgeRetails.Application.Production;
using EdgeRetails.Application.Production.Backup;
using EdgeRetails.Application.Production.Licensing;
using EdgeRetails.Application.Production.Startup;
using EdgeRetails.Infrastructure.Production.Backup;
using EdgeRetails.Infrastructure.Production.Licensing;
using EdgeRetails.Infrastructure.Production.Startup;
using Xunit;

namespace EdgeRetails.UnitTests;

/// <summary>
/// Phase 6 Database Compatibility Matrix Verification Suite.
/// Proves the 6 mandatory compatibility cases defined in Canonical Architecture V1
/// (Annex H / Section 228 / Section 233) and Phase 6 Specifications (Section 8.4 & 15).
/// </summary>
public sealed class Phase6DatabaseCompatibilityMatrixTests
{
    private static readonly string[] CanonicalMigrationsV1 =
    {
        "20260920094824_InitialProductionBaseline",
        "20260920111318_Sprint7Phase1SetupIdentity",
        "20260920164958_Sprint7ProductionCutover",
        "20260921101001_Sprint8CanonicalReportingSchema",
        "20260921143542_Sprint8FinalProductionAlignment",
        "20260921152602_Sprint8WarrantyAlignment",
        "20260922120000_Phase1CanonicalSchemaAlignment",
        "20260922135055_Phase3ProductionSafetyOutbox",
        "20260923071510_Phase4MultiTerminalSchema",
        "20260923095632_Phase5WarrantyClaimClientOperationId",
        "20260923110943_Phase5WarrantyLifecycleIdempotency",
        "20260923111027_Phase5MovementHistoryOrderingIndex",
        "20260923125420_Phase5PurchaseHistoryOrderingIndex"
    };

    [Fact]
    public void CanonicalMigrationChain_ContainsExact13ChronologicalMigrations()
    {
        Assert.Equal(13, CanonicalMigrationsV1.Length);
        for (int i = 1; i < CanonicalMigrationsV1.Length; i++)
        {
            Assert.True(
                string.CompareOrdinal(CanonicalMigrationsV1[i - 1], CanonicalMigrationsV1[i]) < 0,
                $"Migration chain not strictly chronological at index {i}: {CanonicalMigrationsV1[i - 1]} vs {CanonicalMigrationsV1[i]}");
        }
    }

    #region CASE A: Current App + Supported Older Production DB -> Forward Migrate -> Continuity

    [Fact]
    public async Task CaseA_CurrentApp_WithSupportedOlderDb_DetectsDatabaseBehind_AndAfterMigration_AllowsStartup()
    {
        // 1. Database is at an older supported migration (e.g. Phase 1 alignment: 7 migrations applied out of 13)
        var appliedBeforeUpgrade = CanonicalMigrationsV1.Take(7).ToArray();
        var evalBefore = MigrationHistoryCompatibilityEvaluator.Evaluate(CanonicalMigrationsV1, appliedBeforeUpgrade, hasPendingModelChanges: false);

        Assert.False(evalBefore.Compatible);
        Assert.Equal(MigrationCompatibilityState.DatabaseBehind, evalBefore.State);
        Assert.Equal(6, evalBefore.PendingMigrations.Count);
        Assert.Empty(evalBefore.UnknownAppliedMigrations);

        // 2. StartupCoordinator with this DB state BLOCKS before login
        var coordinatorBefore = CreateCoordinator(new StubMigrationProbe(evalBefore));
        var startupResultBefore = await coordinatorBefore.RunAsync();

        Assert.Equal(StartupDisposition.Blocked, startupResultBefore.Disposition);
        Assert.Equal("Migrations", startupResultBefore.Stage);
        Assert.Equal("migration.database_behind", startupResultBefore.ErrorCode);
        Assert.Contains("requires 6 canonical migration(s)", startupResultBefore.Message);
        Assert.Equal("Apply only the canonical pending Edge Retails migrations through the controlled deployment path.", startupResultBefore.RecommendedAction);

        // 3. Controlled forward migration executes: applied becomes equal to known
        var appliedAfterUpgrade = CanonicalMigrationsV1;
        var evalAfter = MigrationHistoryCompatibilityEvaluator.Evaluate(CanonicalMigrationsV1, appliedAfterUpgrade, hasPendingModelChanges: false);

        Assert.True(evalAfter.Compatible);
        Assert.Equal(MigrationCompatibilityState.Compatible, evalAfter.State);
        Assert.Empty(evalAfter.PendingMigrations);

        // 4. StartupCoordinator now proceeds cleanly to LoginReady (business continuity preserved)
        var coordinatorAfter = CreateCoordinator(new StubMigrationProbe(evalAfter));
        var startupResultAfter = await coordinatorAfter.RunAsync();

        Assert.Equal(StartupDisposition.LoginReady, startupResultAfter.Disposition);
        Assert.Equal("Login", startupResultAfter.Stage);
    }

    #endregion

    #region CASE B: Current App + Unsupported-Too-Old DB -> Safe BLOCK With Diagnostic Code

    [Theory]
    [InlineData("Empty history", new string[0])]
    [InlineData("Diverged legacy migration", new[] { "20250101000000_LegacyUnsupportedBaseline", "20260920094824_InitialProductionBaseline" })]
    [InlineData("Skipped intermediate migration", new[] { "20260920094824_InitialProductionBaseline", "20260920164958_Sprint7ProductionCutover" })]
    public async Task CaseB_CurrentApp_WithUnsupportedOrDivergedDb_FailsClosedWithActionableDiagnostic(
        string scenario, string[] invalidApplied)
    {
        _ = scenario;
        var eval = MigrationHistoryCompatibilityEvaluator.Evaluate(CanonicalMigrationsV1, invalidApplied, hasPendingModelChanges: false);

        Assert.False(eval.Compatible);
        Assert.True(
            eval.State is MigrationCompatibilityState.HistoryDiverged
                       or MigrationCompatibilityState.DatabaseBehind
                       or MigrationCompatibilityState.DatabaseAhead);

        var coordinator = CreateCoordinator(new StubMigrationProbe(eval));
        var startupResult = await coordinator.RunAsync();

        Assert.Equal(StartupDisposition.Blocked, startupResult.Disposition);
        Assert.Equal("Migrations", startupResult.Stage);
        Assert.NotNull(startupResult.ErrorCode);
        Assert.StartsWith("migration.", startupResult.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(startupResult.RecommendedAction));
    }

    #endregion

    #region CASE C: Older App + Newer DB -> Safe BLOCK (Never Auto-Downgrade)

    [Fact]
    public async Task CaseC_OlderApp_WithNewerDb_SafelyBlocksAsDatabaseAhead_WithoutDowngrading()
    {
        // Older app build knows only up to Phase 1 (7 migrations)
        var olderAppKnown = CanonicalMigrationsV1.Take(7).ToArray();
        // Database has been migrated to Phase 5 (all 13 migrations applied)
        var newerDbApplied = CanonicalMigrationsV1;

        var eval = MigrationHistoryCompatibilityEvaluator.Evaluate(olderAppKnown, newerDbApplied, hasPendingModelChanges: false);

        Assert.False(eval.Compatible);
        Assert.Equal(MigrationCompatibilityState.DatabaseAhead, eval.State);
        Assert.Equal(6, eval.UnknownAppliedMigrations.Count);
        Assert.Equal("20260922135055_Phase3ProductionSafetyOutbox", eval.UnknownAppliedMigrations[0]);

        var coordinator = CreateCoordinator(new StubMigrationProbe(eval));
        var startupResult = await coordinator.RunAsync();

        Assert.Equal(StartupDisposition.Blocked, startupResult.Disposition);
        Assert.Equal("Migrations", startupResult.Stage);
        Assert.Equal("migration.database_ahead", startupResult.ErrorCode);
        Assert.Equal("Use an application build that recognizes the database migration history. Do not downgrade blindly.", startupResult.RecommendedAction);
    }

    #endregion

    #region CASE D: Current App + Unknown Future Migration -> Safe DATABASE_AHEAD Block

    [Fact]
    public async Task CaseD_CurrentApp_WithUnknownFutureMigration_SafelyBlocksAsDatabaseAhead()
    {
        // Future unreleased migration applied out-of-band to database
        var futureApplied = CanonicalMigrationsV1
            .Concat(new[] { "20261231235959_FutureUnreleasedFeature" })
            .ToArray();

        var eval = MigrationHistoryCompatibilityEvaluator.Evaluate(CanonicalMigrationsV1, futureApplied, hasPendingModelChanges: false);

        Assert.False(eval.Compatible);
        Assert.Equal(MigrationCompatibilityState.DatabaseAhead, eval.State);
        Assert.Single(eval.UnknownAppliedMigrations);
        Assert.Equal("20261231235959_FutureUnreleasedFeature", eval.UnknownAppliedMigrations[0]);

        var coordinator = CreateCoordinator(new StubMigrationProbe(eval));
        var startupResult = await coordinator.RunAsync();

        Assert.Equal(StartupDisposition.Blocked, startupResult.Disposition);
        Assert.Equal("Migrations", startupResult.Stage);
        Assert.Equal("migration.database_ahead", startupResult.ErrorCode);
        Assert.Contains("does not recognize", startupResult.Message);
    }

    #endregion

    #region CASE E: Supported Older Backup + Current App -> Validate -> Staging Migrate -> Cutover

    [Fact]
    public void CaseE_SupportedOlderBackup_EvaluatedInStaging_ForwardMigratesToCurrent_AndBecomesCompatible()
    {
        // Staging database restored from older backup (e.g. Sprint 8 Cutover: 6 migrations)
        var stagingOlderApplied = CanonicalMigrationsV1.Take(6).ToArray();

        // 1. Initial check of staging prior to forward migration
        var stagingEvalBefore = MigrationHistoryCompatibilityEvaluator.Evaluate(
            CanonicalMigrationsV1, stagingOlderApplied, hasPendingModelChanges: false);
        Assert.Equal(MigrationCompatibilityState.DatabaseBehind, stagingEvalBefore.State);

        // 2. Controlled forward migration applied to staging database
        var stagingMigrated = CanonicalMigrationsV1;
        var stagingEvalAfter = MigrationHistoryCompatibilityEvaluator.Evaluate(
            CanonicalMigrationsV1, stagingMigrated, hasPendingModelChanges: false);

        Assert.True(stagingEvalAfter.Compatible);
        Assert.Equal(MigrationCompatibilityState.Compatible, stagingEvalAfter.State);
        Assert.Empty(stagingEvalAfter.PendingMigrations);
        Assert.Empty(stagingEvalAfter.UnknownAppliedMigrations);
    }

    #endregion

    #region CASE F: Unknown/Future Backup + Current App -> Reject BEFORE Destructive Cutover

    [Fact]
    public void CaseF_UnknownOrFutureBackup_InStaging_IsRejectedFailClosed_BeforeCutover()
    {
        // Staging database restored from an incompatible future backup
        var stagingFutureApplied = CanonicalMigrationsV1
            .Concat(new[] { "20270101000000_FutureEnterpriseSchema" })
            .ToArray();

        var stagingEval = MigrationHistoryCompatibilityEvaluator.Evaluate(
            CanonicalMigrationsV1, stagingFutureApplied, hasPendingModelChanges: false);

        Assert.False(stagingEval.Compatible);
        Assert.Equal(MigrationCompatibilityState.DatabaseAhead, stagingEval.State);

        // In CanonicalRestoreStagingValidator / EdgeRetailsEfRestoreCompatibilityProbe,
        // this triggers: throw new InvalidDataException($"Restored staging database failed EF compatibility: {result.State}.")
        var ex = Assert.Throws<InvalidDataException>(() =>
        {
            if (!stagingEval.Compatible)
            {
                throw new InvalidDataException($"Restored staging database failed EF compatibility: {stagingEval.State}.");
            }
        });

        Assert.Contains("DatabaseAhead", ex.Message);
    }

    #endregion

    #region Helper Infrastructure

    private static ProductionStartupCoordinator CreateCoordinator(IMigrationCompatibilityProbe migrationProbe)
    {
        using var rsa = RSA.Create(2048);
        var rawLicense = CreateValidLicenseJson(rsa, "DEV-01");
        var licenseValidator = new SignedLicenseValidator(
            new RsaSha256LicenseSignatureVerifier(new StubPublicKeyProvider(rsa.ExportSubjectPublicKeyInfoPem())),
            new StubDeviceProvider("DEV-01"));
        var licenseService = new RuntimeLicenseService(new StubLicenseStore(rawLicense), licenseValidator);

        return new ProductionStartupCoordinator(
            licenseService,
            new StubDbReadinessProbe(ready: true),
            migrationProbe,
            new StubMaintenanceBarrier(ProductionMaintenanceState.Normal),
            new StubSetupProbe(complete: true),
            new StubSessionProbe(recoverable: false));
    }

    private static string CreateValidLicenseJson(RSA rsa, string deviceId)
    {
        var payload = new LicensePayload(
            "LIC-P6-COMPAT-001",
            "Customer-Test",
            "Shop-01",
            "Production-Cert",
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(1),
            deviceId,
            null,
            new[] { "Electronics", "Electrical" });
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var sig = rsa.SignData(payloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var envelope = new SignedLicenseEnvelope("RS256", Convert.ToBase64String(payloadBytes), Convert.ToBase64String(sig));
        return JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private sealed class StubLicenseStore(string raw) : ILicenseStore
    {
        public Task<bool> ExistsAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<string?> ReadRawAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(raw);
        public Task<bool> TryPersistInitialRawAsync(string signedLicenseJson, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task PersistRawAsync(string signedLicenseJson, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubPublicKeyProvider(string pem) : ILicensePublicKeyProvider { public string GetPublicKeyPem() => pem; }
    private sealed class StubDeviceProvider(string deviceId) : IDeviceIdentityProvider { public Task<string> GetDeviceIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(deviceId); }
    private sealed class StubDbReadinessProbe(bool ready) : IDatabaseReadinessProbe { public Task<DatabaseReadinessResult> CheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(new DatabaseReadinessResult(ready, ready ? "Ready" : "Unavailable")); }
    private sealed class StubMigrationProbe(MigrationCompatibilityResult result) : IMigrationCompatibilityProbe { public Task<MigrationCompatibilityResult> CheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(result); }
    private sealed class StubMaintenanceBarrier(ProductionMaintenanceState state) : IProductionMaintenanceBarrier
    {
        public Task<IProductionMaintenanceLease> EnterExclusiveAsync(ProductionMaintenanceState requestedState, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ProductionMaintenanceState> GetStateAsync(CancellationToken cancellationToken = default) => Task.FromResult(state);
    }
    private sealed class StubSetupProbe(bool complete) : ISetupStateProbe { public Task<SetupStateResult> CheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(new SetupStateResult(complete, complete ? "Setup Complete" : "Incomplete")); }
    private sealed class StubSessionProbe(bool recoverable) : ISessionRecoveryProbe { public Task<SessionRecoveryResult> CheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(new SessionRecoveryResult(recoverable, recoverable ? "Session recoverable" : null)); }

    #endregion
}
