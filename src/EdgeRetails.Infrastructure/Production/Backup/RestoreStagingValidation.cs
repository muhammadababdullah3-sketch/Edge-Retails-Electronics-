using EdgeRetails.Application.Production.Backup;

namespace EdgeRetails.Infrastructure.Production.Backup;

public sealed record RestoreStagingValidationContext(
    PostgresConnectionDescriptor RuntimeConnection,
    PostgresMaintenanceDescriptor MaintenanceConnection,
    string StagingDatabase,
    BackupManifest Manifest,
    Guid RestoreId);

/// <summary>
/// Mandatory merge seam for schema/migration/business-invariant verification. Production DI must
/// provide the canonical Edge Retails validator; the backup engine will not mark a session Prepared
/// until this validator succeeds.
/// </summary>
public interface IRestoreStagingValidator
{
    Task ValidateAsync(RestoreStagingValidationContext context, CancellationToken cancellationToken = default);
}
