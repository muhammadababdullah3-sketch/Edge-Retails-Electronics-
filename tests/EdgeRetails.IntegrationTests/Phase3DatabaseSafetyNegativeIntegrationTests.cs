using EdgeRetails.Application.Production;
using EdgeRetails.Application.Production.Diagnostics;
using EdgeRetails.Application.Production.Licensing;
using EdgeRetails.Application.Production.Startup;
using EdgeRetails.Infrastructure.Production.Backup;
using EdgeRetails.Infrastructure.Production.Startup;
using Xunit;

namespace EdgeRetails.IntegrationTests;

public sealed class Phase3DatabaseSafetyNegativeIntegrationTests
{
    [Fact]
    public async Task DBSAFE_01_DatabaseUnavailable_SocketTimeout_ReturnsUnavailableAndFailsFast()
    {
        // Target an unreachable port on localhost to trigger connection timeout
        var unreachableConn = "Host=127.0.0.1;Port=54399;Database=nonexistent;Username=none;Password=none;Timeout=10";
        var probe = new NpgsqlDatabaseReadinessProbe(unreachableConn);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await probe.CheckAsync(CancellationToken.None);
        stopwatch.Stop();

        Assert.False(result.Ready);
        Assert.Equal(DatabaseReadinessCode.Unavailable, result.Code);
        // Enforce 3-second fail-fast timeout (allowing up to 5s margin for thread switching)
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(6), $"Probe took too long to fail fast: {stopwatch.Elapsed.TotalSeconds}s");
    }

    [Fact]
    public async Task DBSAFE_02_UnsafeDurabilitySetting_BlocksStartupDisposition()
    {
        var license = new RuntimeLicenseService(new ValidLicenseStore(), new ValidLicenseValidator());
        var unsafeDbProbe = new StubDatabaseProbe(
            new DatabaseReadinessResult(false, "PostgreSQL durability setting breached: synchronous_commit is 'off'.")
            {
                Code = DatabaseReadinessCode.ProbeFailed
            });
        var migrations = new StubMigrationProbe(new MigrationCompatibilityResult(true, "OK") { State = MigrationCompatibilityState.Compatible });
        var temp = Path.Combine(Path.GetTempPath(), "DbSafeTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        try
        {
            var barrier = new FileProductionMaintenanceBarrier(
                Path.Combine(temp, "maint"),
                new FixedMaintenanceKeyProvider());
            var coordinator = new ProductionStartupCoordinator(
                license,
                unsafeDbProbe,
                migrations,
                barrier,
                new StubSetupProbe(),
                new StubSessionProbe());

            var startupResult = await coordinator.RunAsync(CancellationToken.None);

            Assert.Equal(StartupDisposition.Blocked, startupResult.Disposition);
            Assert.Equal("Database", startupResult.Stage);
            Assert.Equal("database.probe_failed", startupResult.ErrorCode);
            Assert.Contains("synchronous_commit", startupResult.Message);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DBSAFE_03_MigrationIncompatible_BlocksStartupDisposition()
    {
        var license = new RuntimeLicenseService(new ValidLicenseStore(), new ValidLicenseValidator());
        var safeDbProbe = new StubDatabaseProbe(new DatabaseReadinessResult(true, "Ready") { Code = DatabaseReadinessCode.Ready });
        var incompatibleMigrations = new StubMigrationProbe(
            new MigrationCompatibilityResult(false, "Pending migrations exist.") { State = MigrationCompatibilityState.DatabaseBehind });
        var temp = Path.Combine(Path.GetTempPath(), "DbSafeMigTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        try
        {
            var barrier = new FileProductionMaintenanceBarrier(
                Path.Combine(temp, "maint"),
                new FixedMaintenanceKeyProvider());
            var coordinator = new ProductionStartupCoordinator(
                license,
                safeDbProbe,
                incompatibleMigrations,
                barrier,
                new StubSetupProbe(),
                new StubSessionProbe());

            var startupResult = await coordinator.RunAsync(CancellationToken.None);

            Assert.Equal(StartupDisposition.Blocked, startupResult.Disposition);
            Assert.Equal("Migrations", startupResult.Stage);
            Assert.Equal("migration.database_behind", startupResult.ErrorCode);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DBSAFE_04_CriticalDiskFloor_ThrowsCriticalDiskFloorException_AndBlocksWrite()
    {
        var temp = Path.Combine(Path.GetTempPath(), "DbSafeDiskTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        try
        {
            var barrier = new FileProductionMaintenanceBarrier(
                Path.Combine(temp, "maint"),
                new FixedMaintenanceKeyProvider());

            // 100 MB free is below the mandatory 500 MB floor
            var lowDiskProbe = new StubDiskProbe(new DiskSpaceResult(false, 100L * 1024 * 1024, "Disk free space is below the configured production threshold."));
            var writeGuard = new ProductionMaintenanceWriteGuard(barrier, lowDiskProbe);

            var ex = await Assert.ThrowsAsync<CriticalDiskFloorException>(
                () => writeGuard.EnsureBusinessWritesAllowedAsync());

            Assert.Equal(100L * 1024 * 1024, ex.AvailableBytes);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    private sealed class StubDatabaseProbe : IDatabaseReadinessProbe
    {
        private readonly DatabaseReadinessResult _result;
        public StubDatabaseProbe(DatabaseReadinessResult result) => _result = result;
        public Task<DatabaseReadinessResult> CheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(_result);
    }

    private sealed class StubMigrationProbe : IMigrationCompatibilityProbe
    {
        private readonly MigrationCompatibilityResult _result;
        public StubMigrationProbe(MigrationCompatibilityResult result) => _result = result;
        public Task<MigrationCompatibilityResult> CheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(_result);
    }

    private sealed class StubDiskProbe : IDiskSpaceProbe
    {
        private readonly DiskSpaceResult _result;
        public StubDiskProbe(DiskSpaceResult result) => _result = result;
        public Task<DiskSpaceResult> CheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(_result);
    }

    private sealed class StubSetupProbe : ISetupStateProbe
    {
        public Task<SetupStateResult> CheckAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new SetupStateResult(true, "Ready"));
    }

    private sealed class StubSessionProbe : ISessionRecoveryProbe
    {
        public Task<SessionRecoveryResult> CheckAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new SessionRecoveryResult(false, null));
    }

    private sealed class ValidLicenseStore : ILicenseStore
    {
        public Task<bool> ExistsAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<string?> ReadRawAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>("{}");
        public Task<bool> TryPersistInitialRawAsync(string signedLicenseJson, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task PersistRawAsync(string signedLicenseJson, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ValidLicenseValidator : ILicenseValidator
    {
        public Task<LicenseValidationResult> ValidateAsync(string signedLicenseJson, CancellationToken cancellationToken = default)
            => Task.FromResult(new LicenseValidationResult(LicenseValidationStatus.Valid, new LicensePayload("LIC-1", "Test", "Test Store", "Retail", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1), "DEV-1", 5, Array.Empty<string>()), "Valid"));
    }

    private sealed class FixedMaintenanceKeyProvider : IProductionMaintenanceIntegrityKeyProvider
    {
        public Task<byte[]> GetIntegrityKeyAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Enumerable.Range(65, 32).Select(i => (byte)i).ToArray());
    }
}
