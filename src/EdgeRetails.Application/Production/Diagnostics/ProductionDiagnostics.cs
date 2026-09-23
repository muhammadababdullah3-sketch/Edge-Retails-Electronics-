using EdgeRetails.Application.Production.Backup;
using EdgeRetails.Application.Production.Licensing;
using EdgeRetails.Application.Production.Printing;
using EdgeRetails.Application.Production.Startup;

namespace EdgeRetails.Application.Production.Diagnostics;

public enum BackupHealthState
{
    Healthy,
    NoBackups,
    Degraded,
    Unavailable
}

public sealed record BackupHealthResult(
    BackupHealthState State,
    BackupManifest? LatestValidBackup,
    int ValidBackupCount,
    int IssueCount,
    string Message);

public sealed record WorkerHeartbeatResult(bool Healthy, DateTimeOffset? LastHeartbeatUtc, string Message);
public sealed record DiskSpaceResult(bool Healthy, long? AvailableBytes, string Message);
public sealed record PostgresToolchainResult(bool Ready, string Message, IReadOnlyDictionary<string, string> Versions);

public interface IBackupHealthProbe
{
    Task<BackupHealthResult> CheckAsync(CancellationToken cancellationToken = default);
}

public interface IWorkerHeartbeatProbe
{
    Task<WorkerHeartbeatResult> CheckAsync(CancellationToken cancellationToken = default);
}

public interface IDiskSpaceProbe
{
    Task<DiskSpaceResult> CheckAsync(CancellationToken cancellationToken = default);
}

public interface IPostgresToolchainReadinessProbe
{
    Task<PostgresToolchainResult> CheckAsync(CancellationToken cancellationToken = default);
}

public sealed record ProductionDiagnosticsSnapshot(
    DateTimeOffset CapturedAtUtc,
    LicenseValidationStatus LicenseStatus,
    DatabaseReadinessResult Database,
    MigrationCompatibilityResult Migrations,
    BackupHealthResult Backup,
    PrinterProfile? ReceiptPrinter,
    WorkerHeartbeatResult Worker,
    DiskSpaceResult Disk,
    PostgresToolchainResult PostgresToolchain,
    ProductionMaintenanceState MaintenanceState,
    string ApplicationVersion,
    string? SchemaVersion);

public interface IProductionVersionInfo
{
    string ApplicationVersion { get; }
    string? SchemaVersion { get; }
}

public sealed class ProductionDiagnosticsService
{
    private readonly RuntimeLicenseService _license;
    private readonly IDatabaseReadinessProbe _database;
    private readonly IMigrationCompatibilityProbe _migrations;
    private readonly IBackupHealthProbe _backup;
    private readonly IPrinterProfileStore _printers;
    private readonly IWorkerHeartbeatProbe _worker;
    private readonly IDiskSpaceProbe _disk;
    private readonly IPostgresToolchainReadinessProbe _toolchain;
    private readonly IProductionMaintenanceBarrier _maintenance;
    private readonly IProductionVersionInfo _version;
    private readonly string _receiptProfileName;

    public ProductionDiagnosticsService(
        RuntimeLicenseService license,
        IDatabaseReadinessProbe database,
        IMigrationCompatibilityProbe migrations,
        IBackupHealthProbe backup,
        IPrinterProfileStore printers,
        IWorkerHeartbeatProbe worker,
        IDiskSpaceProbe disk,
        IPostgresToolchainReadinessProbe toolchain,
        IProductionMaintenanceBarrier maintenance,
        IProductionVersionInfo version,
        string receiptProfileName)
    {
        _license = license;
        _database = database;
        _migrations = migrations;
        _backup = backup;
        _printers = printers;
        _worker = worker;
        _disk = disk;
        _toolchain = toolchain;
        _maintenance = maintenance;
        _version = version;
        _receiptProfileName = receiptProfileName;
    }

    public async Task<ProductionDiagnosticsSnapshot> CaptureAsync(CancellationToken cancellationToken = default)
    {
        var license = await SafeLicenseAsync(cancellationToken);
        var database = await SafeDatabaseAsync(cancellationToken);
        var migrations = database.Ready
            ? await SafeMigrationsAsync(cancellationToken)
            : new MigrationCompatibilityResult(false, "Migration compatibility was not checked because PostgreSQL is unavailable.")
            {
                State = MigrationCompatibilityState.ProbeFailed
            };
        var backup = await SafeBackupAsync(cancellationToken);
        var printer = await SafePrinterAsync(cancellationToken);
        var worker = await SafeWorkerAsync(cancellationToken);
        var disk = await SafeDiskAsync(cancellationToken);
        var toolchain = await SafeToolchainAsync(cancellationToken);
        var maintenance = await SafeMaintenanceAsync(cancellationToken);

        return new ProductionDiagnosticsSnapshot(
            DateTimeOffset.UtcNow,
            license.Status,
            database,
            migrations,
            backup,
            printer,
            worker,
            disk,
            toolchain,
            maintenance,
            _version.ApplicationVersion,
            _version.SchemaVersion);
    }

    private async Task<LicenseValidationResult> SafeLicenseAsync(CancellationToken ct)
    {
        try { return await _license.ValidatePersistedAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return new(LicenseValidationStatus.ValidationUnavailable, null, "License diagnostics unavailable.", "license.diagnostics_unavailable"); }
    }

    private async Task<DatabaseReadinessResult> SafeDatabaseAsync(CancellationToken ct)
    {
        try { return await _database.CheckAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return new(false, "Database diagnostics unavailable.") { Code = DatabaseReadinessCode.ProbeFailed }; }
    }

    private async Task<MigrationCompatibilityResult> SafeMigrationsAsync(CancellationToken ct)
    {
        try { return await _migrations.CheckAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return new(false, "Migration diagnostics unavailable.") { State = MigrationCompatibilityState.ProbeFailed }; }
    }

    private async Task<BackupHealthResult> SafeBackupAsync(CancellationToken ct)
    {
        try { return await _backup.CheckAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return new(BackupHealthState.Unavailable, null, 0, 0, "Backup diagnostics unavailable."); }
    }

    private async Task<PrinterProfile?> SafePrinterAsync(CancellationToken ct)
    {
        try { return await _printers.GetAsync(_receiptProfileName, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    private async Task<WorkerHeartbeatResult> SafeWorkerAsync(CancellationToken ct)
    {
        try { return await _worker.CheckAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return new(false, null, "Worker heartbeat diagnostics unavailable."); }
    }

    private async Task<DiskSpaceResult> SafeDiskAsync(CancellationToken ct)
    {
        try { return await _disk.CheckAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return new(false, null, "Disk diagnostics unavailable."); }
    }


    private async Task<PostgresToolchainResult> SafeToolchainAsync(CancellationToken ct)
    {
        try { return await _toolchain.CheckAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return new(false, "PostgreSQL client-tool diagnostics unavailable.", new Dictionary<string, string>()); }
    }
    private async Task<ProductionMaintenanceState> SafeMaintenanceAsync(CancellationToken ct)
    {
        try { return await _maintenance.GetStateAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return ProductionMaintenanceState.RecoveryRequired; }
    }
}
