using EdgeRetails.Application.Production;

namespace EdgeRetails.Application.Production.Backup;

public sealed class CreateBackupHandler
{
    private readonly IProductionAuthorization _authorization;
    private readonly IPostgresBackupEngine _engine;
    private readonly ProductionAuditCoordinator _audit;
    private readonly IBackupJobLock? _jobLock;

    public CreateBackupHandler(
        IProductionAuthorization authorization,
        IPostgresBackupEngine engine,
        ProductionAuditCoordinator audit,
        IBackupJobLock? jobLock = null)
    {
        _authorization = authorization;
        _engine = engine;
        _audit = audit;
        _jobLock = jobLock;
    }

    public async Task<BackupCreateResult> HandleAsync(BackupCreateRequest request, CancellationToken cancellationToken = default)
    {
        await _authorization.EnsurePermissionAsync(ProductionPermissionNames.SettingsManage, cancellationToken);

        IAsyncDisposable? lockLease = null;
        if (_jobLock is not null)
        {
            lockLease = await _jobLock.TryAcquireLockAsync(cancellationToken);
            if (lockLease is null)
            {
                throw new InvalidOperationException("Another backup operation is currently active.");
            }
        }

        try
        {
            var result = await _engine.CreateAsync(request, cancellationToken);
            _ = await _audit.AppendAfterSideEffectAsync(new ProductionAuditRecord(
                ProductionAuditEvents.BackupCreated,
                DateTimeOffset.UtcNow,
                "Backup",
                result.Manifest.BackupId.ToString(),
                $"File={result.Manifest.FileName}; Sha256={result.Manifest.Sha256}; Size={result.Manifest.SizeBytes}",
                request.CorrelationId));
            if (!string.IsNullOrWhiteSpace(result.RetentionWarningCode))
            {
                await _audit.AppendFailureBestEffortAsync(new ProductionAuditRecord(
                    ProductionAuditEvents.BackupRetentionWarning,
                    DateTimeOffset.UtcNow,
                    "Backup",
                    result.Manifest.BackupId.ToString(),
                    result.RetentionWarningCode,
                    request.CorrelationId));
            }
            return result;
        }
        catch (Exception ex)
        {
            await _audit.AppendFailureBestEffortAsync(new ProductionAuditRecord(
                ProductionAuditEvents.BackupFailed,
                DateTimeOffset.UtcNow,
                "Backup",
                null,
                ex.GetType().Name,
                request.CorrelationId));
            throw;
        }
        finally
        {
            if (lockLease is not null)
            {
                await lockLease.DisposeAsync();
            }
        }
    }
}

public sealed class PrepareRestoreHandler
{
    private readonly IProductionAuthorization _authorization;
    private readonly IPostgresBackupEngine _engine;
    private readonly ProductionAuditCoordinator _audit;

    public PrepareRestoreHandler(IProductionAuthorization authorization, IPostgresBackupEngine engine, ProductionAuditCoordinator audit)
    {
        _authorization = authorization;
        _engine = engine;
        _audit = audit;
    }

    public async Task<RestoreSessionToken> HandleAsync(RestorePrepareRequest request, CancellationToken cancellationToken = default)
    {
        await _authorization.EnsurePermissionAsync(ProductionPermissionNames.SettingsManage, cancellationToken);
        try
        {
            var token = await _engine.PrepareRestoreAsync(request, cancellationToken);
            _ = await _audit.AppendAfterSideEffectAsync(new ProductionAuditRecord(
                ProductionAuditEvents.RestorePrepared,
                DateTimeOffset.UtcNow,
                "Restore",
                token.RestoreId.ToString(),
                "Staging restore verified and journaled.",
                request.CorrelationId));
            return token;
        }
        catch (Exception ex)
        {
            await _audit.AppendFailureBestEffortAsync(new ProductionAuditRecord(
                ProductionAuditEvents.RestoreFailed,
                DateTimeOffset.UtcNow,
                "Restore",
                null,
                $"Prepare:{ex.GetType().Name}",
                request.CorrelationId));
            throw;
        }
    }
}

public sealed class CutoverRestoreHandler
{
    private readonly IProductionAuthorization _authorization;
    private readonly IPostgresBackupEngine _engine;
    private readonly ProductionAuditCoordinator _audit;

    public CutoverRestoreHandler(IProductionAuthorization authorization, IPostgresBackupEngine engine, ProductionAuditCoordinator audit)
    {
        _authorization = authorization;
        _engine = engine;
        _audit = audit;
    }

    public async Task<RestoreCutoverResult> HandleAsync(
        PostgresConnectionDescriptor runtimeConnection,
        RestoreSessionToken token,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        await _authorization.EnsurePermissionAsync(ProductionPermissionNames.SettingsManage, cancellationToken);
        try
        {
            var result = await _engine.CutoverAsync(runtimeConnection, token, cancellationToken);
            _ = await _audit.AppendAfterSideEffectAsync(new ProductionAuditRecord(
                ProductionAuditEvents.RestoreCutoverCompleted,
                DateTimeOffset.UtcNow,
                "Restore",
                token.RestoreId.ToString(),
                $"PreservedDatabase={result.PreservedPreRestoreDatabase}",
                correlationId));
            return result;
        }
        catch (RestoreRecoveryRequiredException ex)
        {
            await _audit.AppendFailureBestEffortAsync(new ProductionAuditRecord(
                ProductionAuditEvents.RestoreRecoveryRequired,
                DateTimeOffset.UtcNow,
                "Restore",
                token.RestoreId.ToString(),
                ex.GetType().Name,
                correlationId));
            throw;
        }
        catch (Exception ex)
        {
            await _audit.AppendFailureBestEffortAsync(new ProductionAuditRecord(
                ProductionAuditEvents.RestoreFailed,
                DateTimeOffset.UtcNow,
                "Restore",
                token.RestoreId.ToString(),
                ex.GetType().Name,
                correlationId));
            throw;
        }
    }
}

public sealed class DiscardPreparedRestoreHandler
{
    private readonly IProductionAuthorization _authorization;
    private readonly IPostgresBackupEngine _engine;
    private readonly ProductionAuditCoordinator _audit;

    public DiscardPreparedRestoreHandler(IProductionAuthorization authorization, IPostgresBackupEngine engine, ProductionAuditCoordinator audit)
    {
        _authorization = authorization;
        _engine = engine;
        _audit = audit;
    }

    public async Task HandleAsync(
        PostgresConnectionDescriptor runtimeConnection,
        RestoreSessionToken token,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        await _authorization.EnsurePermissionAsync(ProductionPermissionNames.SettingsManage, cancellationToken);
        await _engine.DiscardPreparedRestoreAsync(runtimeConnection, token, cancellationToken);
        _ = await _audit.AppendAfterSideEffectAsync(new ProductionAuditRecord(
            ProductionAuditEvents.RestoreDiscarded,
            DateTimeOffset.UtcNow,
            "Restore",
            token.RestoreId.ToString(),
            "Prepared restore staging database discarded.",
            correlationId));
    }
}
