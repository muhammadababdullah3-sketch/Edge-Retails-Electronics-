using EdgeRetails.Application.Production;
using EdgeRetails.Application.Production.Licensing;

namespace EdgeRetails.Application.Production.Startup;

public sealed class ProductionStartupCoordinator
{
    private readonly RuntimeLicenseService _license;
    private readonly IDatabaseReadinessProbe _database;
    private readonly IMigrationCompatibilityProbe _migrations;
    private readonly IProductionMaintenanceBarrier _maintenance;
    private readonly ISetupStateProbe _setup;
    private readonly ISessionRecoveryProbe _session;

    private readonly Diagnostics.IDiskSpaceProbe? _disk;

    public ProductionStartupCoordinator(
        RuntimeLicenseService license,
        IDatabaseReadinessProbe database,
        IMigrationCompatibilityProbe migrations,
        IProductionMaintenanceBarrier maintenance,
        ISetupStateProbe setup,
        ISessionRecoveryProbe session,
        Diagnostics.IDiskSpaceProbe? disk = null)
    {
        _license = license;
        _database = database;
        _migrations = migrations;
        _maintenance = maintenance ?? throw new ArgumentNullException(nameof(maintenance));
        _setup = setup;
        _session = session;
        _disk = disk;
    }

    public async Task<StartupResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var correlationId = Guid.NewGuid().ToString("N");

        LicenseValidationResult license;
        try { license = await _license.ValidatePersistedAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            return Blocked("License", "license.probe_failed", "License validation could not be completed safely.", "Open diagnostics or reinstall the signed license artifact.", correlationId);
        }

        if (license.Status == LicenseValidationStatus.Missing)
        {
            return new StartupResult(
                StartupDisposition.LicenseRequired,
                "License",
                "A signed license is required before Edge Retails can continue.",
                ErrorCode: "license.missing",
                RecommendedAction: "Import the signed license file from the First Setup / License screen.",
                CorrelationId: correlationId);
        }

        if (!license.IsValid)
        {
            return Blocked(
                "License",
                license.ErrorCode ?? "license.invalid",
                $"License validation failed: {license.Status}.",
                "Open the License section and install a valid license using the permitted recovery/replacement flow.",
                correlationId);
        }

        DatabaseReadinessResult database;
        try { database = await _database.CheckAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            return Blocked("Database", "database.probe_failed", "PostgreSQL readiness could not be verified.", "Check the PostgreSQL service and database configuration, then retry.", correlationId);
        }
        if (!database.Ready)
        {
            return Blocked(
                "Database",
                DatabaseErrorCode(database.Code),
                database.Message,
                "Check PostgreSQL service/readiness and use Diagnostics for the safe technical details.",
                correlationId);
        }

        MigrationCompatibilityResult migration;
        try { migration = await _migrations.CheckAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            return Blocked("Migrations", "migration.probe_failed", "Database migration compatibility could not be verified.", "Do not continue with business writes. Run the controlled migration compatibility procedure.", correlationId);
        }
        if (!migration.Compatible)
        {
            return Blocked(
                "Migrations",
                MigrationErrorCode(migration.State),
                migration.Message,
                MigrationRecommendedAction(migration.State),
                correlationId);
        }

        if (_disk is not null)
        {
            Diagnostics.DiskSpaceResult disk;
            try { disk = await _disk.CheckAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch
            {
                return Blocked("DiskSpace", "disk.probe_failed", "Critical disk-space floor could not be verified.", "Verify host storage and free up space before continuing.", correlationId);
            }

            if (!disk.Healthy)
            {
                return Blocked("DiskSpace", "disk.critical_floor_breached", disk.Message, "Free up drive space on the production volume to meet the write-safety floor.", correlationId);
            }
        }

        ProductionMaintenanceState maintenanceState;
        try { maintenanceState = await _maintenance.GetStateAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            return Blocked(
                "Maintenance",
                "maintenance.probe_failed",
                "Production maintenance/recovery state could not be verified safely.",
                "Open diagnostics and resolve maintenance-state integrity before continuing.",
                correlationId);
        }

        if (maintenanceState != ProductionMaintenanceState.Normal)
        {
            var recoveryRequired = maintenanceState == ProductionMaintenanceState.RecoveryRequired;
            return Blocked(
                "Maintenance",
                recoveryRequired ? "maintenance.recovery_required" : "maintenance.restore_active",
                recoveryRequired
                    ? "Restore recovery is required before normal startup can continue."
                    : $"A restore operation is active ({maintenanceState}); normal startup is blocked.",
                recoveryRequired
                    ? "Complete the controlled recovery procedure before setup, login, or business activity."
                    : "Complete or safely discard the active restore before continuing.",
                correlationId);
        }

        SetupStateResult setup;
        try { setup = await _setup.CheckAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            return Blocked("Setup", "setup.probe_failed", "Shop setup state could not be verified.", "Open diagnostics and repair setup state before login.", correlationId);
        }
        if (!setup.Complete)
        {
            return new StartupResult(
                StartupDisposition.SetupRequired,
                "Setup",
                setup.Message,
                ErrorCode: "setup.incomplete",
                RecommendedAction: "Complete the existing First Setup flow. No additional screen is required.",
                CorrelationId: correlationId);
        }

        SessionRecoveryResult session;
        try { session = await _session.CheckAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            session = new SessionRecoveryResult(false, null) { State = SessionRecoveryState.ProbeFailed };
        }

        return new StartupResult(
            StartupDisposition.LoginReady,
            "Login",
            "Production startup checks passed. Continue to login.",
            session.HasRecoverableSession ? session.Hint : null,
            session.State == SessionRecoveryState.ProbeFailed ? "session.recovery_probe_failed" : null,
            session.State == SessionRecoveryState.ProbeFailed ? "Login normally. Session recovery was safely skipped." : null,
            correlationId);
    }

    private static StartupResult Blocked(string stage, string code, string message, string action, string correlationId)
        => new(StartupDisposition.Blocked, stage, message, ErrorCode: code, RecommendedAction: action, CorrelationId: correlationId);

    private static string DatabaseErrorCode(DatabaseReadinessCode code) => code switch
    {
        DatabaseReadinessCode.Unavailable => "database.unavailable",
        DatabaseReadinessCode.UnexpectedDatabase => "database.identity_mismatch",
        _ => "database.probe_failed"
    };

    private static string MigrationErrorCode(MigrationCompatibilityState state) => state switch
    {
        MigrationCompatibilityState.DatabaseBehind => "migration.database_behind",
        MigrationCompatibilityState.DatabaseAhead => "migration.database_ahead",
        MigrationCompatibilityState.HistoryDiverged => "migration.history_diverged",
        MigrationCompatibilityState.ModelDrift => "migration.model_drift",
        _ => "migration.probe_failed"
    };

    private static string MigrationRecommendedAction(MigrationCompatibilityState state) => state switch
    {
        MigrationCompatibilityState.DatabaseBehind => "Apply only the canonical pending Edge Retails migrations through the controlled deployment path.",
        MigrationCompatibilityState.DatabaseAhead => "Use an application build that recognizes the database migration history. Do not downgrade blindly.",
        MigrationCompatibilityState.HistoryDiverged => "Stop writes and investigate migration history/snapshot compatibility before continuing.",
        MigrationCompatibilityState.ModelDrift => "Add/synchronize the canonical EF migration before deployment. Do not suppress the pending-model-change condition.",
        _ => "Run migration diagnostics before continuing."
    };
}
