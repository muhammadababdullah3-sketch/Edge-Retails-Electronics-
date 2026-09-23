using EdgeRetails.Application.Production.Printing;

namespace EdgeRetails.Application.Production;

public static class ProductionPermissionNames
{
    public const string SettingsManage = "settings.manage";
    public const string SalesView = "sales.view";
    public const string PrintingReprint = "printing.reprint";
}

public interface IProductionAuthorization
{
    Task EnsureAuthenticatedAsync(CancellationToken cancellationToken = default);
    Task EnsurePermissionAsync(string permission, CancellationToken cancellationToken = default);
}

/// <summary>
/// Deliberately does not invent new permission names. During merge this adapter must delegate to
/// the canonical Application authorization rules for the requested business document kind.
/// </summary>
public interface IProductionDocumentAuthorizationPolicy
{
    Task EnsureCanPrintAsync(
        ProductionDocumentKind kind,
        Guid businessDocumentId,
        bool isReprint,
        CancellationToken cancellationToken = default);
}

public enum ProductionMaintenanceState
{
    Normal,
    RestorePreparing,
    RestoreCutover,
    RecoveryRequired
}

/// <summary>
/// Application-wide maintenance barrier. Live command pipeline integration must reject business
/// writes whenever the active state is not Normal.
/// </summary>
public interface IProductionMaintenanceLease : IAsyncDisposable
{
    Task SetExitStateAsync(ProductionMaintenanceState state, CancellationToken cancellationToken = default);
}

public interface IProductionMaintenanceBarrier
{
    Task<IProductionMaintenanceLease> EnterExclusiveAsync(ProductionMaintenanceState state, CancellationToken cancellationToken = default);
    Task<ProductionMaintenanceState> GetStateAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Integrity key for the cross-process production-maintenance state. This is a separate trust domain
/// from license signing, update signing, recovery signing, restore-journal integrity and backup encryption.
/// </summary>
public interface IProductionMaintenanceIntegrityKeyProvider
{
    Task<byte[]> GetIntegrityKeyAsync(CancellationToken cancellationToken = default);
}

public sealed class ProductionMaintenanceWriteGuard
{
    private readonly IProductionMaintenanceBarrier _barrier;
    private readonly EdgeRetails.Application.Production.Diagnostics.IDiskSpaceProbe? _disk;

    public ProductionMaintenanceWriteGuard(
        IProductionMaintenanceBarrier barrier,
        EdgeRetails.Application.Production.Diagnostics.IDiskSpaceProbe? disk = null)
    {
        _barrier = barrier ?? throw new ArgumentNullException(nameof(barrier));
        _disk = disk;
    }

    public async Task EnsureBusinessWritesAllowedAsync(CancellationToken cancellationToken = default)
    {
        var state = await _barrier.GetStateAsync(cancellationToken);
        if (state != ProductionMaintenanceState.Normal)
        {
            throw new ProductionMaintenanceException(state);
        }

        if (_disk is not null)
        {
            var diskResult = await _disk.CheckAsync(cancellationToken);
            if (!diskResult.Healthy)
            {
                throw new CriticalDiskFloorException(
                    $"Business writes are blocked: {diskResult.Message}",
                    diskResult.AvailableBytes);
            }
        }
    }
}

public sealed class CriticalDiskFloorException : InvalidOperationException
{
    public long? AvailableBytes { get; }

    public CriticalDiskFloorException(string message, long? availableBytes = null)
        : base(message) => AvailableBytes = availableBytes;
}

public sealed class ProductionMaintenanceException : InvalidOperationException
{
    public ProductionMaintenanceState State { get; }

    public ProductionMaintenanceException(ProductionMaintenanceState state)
        : base($"Business writes are blocked while Edge Retails is in production maintenance state '{state}'.")
        => State = state;
}

public static class ProductionAuditEvents
{
    public const string BackupCreated = "BACKUP_CREATED";
    public const string BackupFailed = "BACKUP_FAILED";
    public const string BackupRetentionWarning = "BACKUP_RETENTION_WARNING";
    public const string RestorePrepared = "RESTORE_PREPARED";
    public const string RestoreCutoverCompleted = "RESTORE_CUTOVER_COMPLETED";
    public const string RestoreDiscarded = "RESTORE_DISCARDED";
    public const string RestoreFailed = "RESTORE_FAILED";
    public const string RestoreRecoveryRequired = "RESTORE_RECOVERY_REQUIRED";
    public const string LicenseImported = "LICENSE_IMPORTED";
    public const string LicenseReplaced = "LICENSE_REPLACED";
    public const string LicenseRejected = "LICENSE_REJECTED";
    public const string LicenseImportBlocked = "LICENSE_IMPORT_BLOCKED";
    public const string PrinterSettingsChanged = "PRINTER_SETTINGS_CHANGED";
    public const string DocumentPrinted = "DOCUMENT_PRINTED";
    public const string DocumentPrintFailed = "DOCUMENT_PRINT_FAILED";
    public const string DocumentReprinted = "DOCUMENT_REPRINTED";
}

public sealed record ProductionAuditRecord(
    string EventType,
    DateTimeOffset OccurredAtUtc,
    string? EntityType,
    string? EntityId,
    string? Detail,
    string? CorrelationId);

public interface IProductionAuditSink
{
    Task AppendAsync(ProductionAuditRecord record, CancellationToken cancellationToken = default);
}

public interface IProductionAuditFailureReporter
{
    Task ReportAsync(ProductionAuditRecord record, Exception error, CancellationToken cancellationToken = default);
}

public sealed record ProductionAuditOutcome(bool Persisted, string? FailureCode = null)
{
    public static ProductionAuditOutcome Success { get; } = new(true);
}

/// <summary>
/// Preserves the truth of an already-committed/physical side effect. Audit failure is reported
/// separately and never converted into a false operation failure.
/// </summary>
public sealed class ProductionAuditCoordinator
{
    private readonly IProductionAuditSink _sink;
    private readonly IProductionAuditFailureReporter _failureReporter;

    public ProductionAuditCoordinator(IProductionAuditSink sink, IProductionAuditFailureReporter failureReporter)
    {
        _sink = sink;
        _failureReporter = failureReporter;
    }

    public async Task<ProductionAuditOutcome> AppendAfterSideEffectAsync(ProductionAuditRecord record)
    {
        try
        {
            await _sink.AppendAsync(record, CancellationToken.None);
            return ProductionAuditOutcome.Success;
        }
        catch (Exception ex)
        {
            try { await _failureReporter.ReportAsync(record, ex, CancellationToken.None); } catch { }
            return new ProductionAuditOutcome(false, "audit.persist_failed");
        }
    }

    public async Task AppendFailureBestEffortAsync(ProductionAuditRecord record)
    {
        try { await _sink.AppendAsync(record, CancellationToken.None); }
        catch (Exception ex)
        {
            try { await _failureReporter.ReportAsync(record, ex, CancellationToken.None); } catch { }
        }
    }
}
