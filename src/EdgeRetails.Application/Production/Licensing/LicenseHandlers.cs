using EdgeRetails.Application.Production;

namespace EdgeRetails.Application.Production.Licensing;

/// <summary>
/// Shared validation/persistence implementation. Initial import and authorized replacement deliberately
/// enter through different handlers so setup-state rules cannot be bypassed by UI routing.
/// </summary>
public sealed class LicenseInstallationService
{
    private readonly ILicenseValidator _validator;
    private readonly ILicenseStore _store;
    private readonly ProductionAuditCoordinator _audit;

    public LicenseInstallationService(ILicenseValidator validator, ILicenseStore store, ProductionAuditCoordinator audit)
    {
        _validator = validator;
        _store = store;
        _audit = audit;
    }

    public async Task<LicenseValidationResult> ValidateAndPersistInitialAsync(
        string signedLicenseJson,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        var result = await _validator.ValidateAsync(signedLicenseJson, cancellationToken);
        if (!result.IsValid)
        {
            await _audit.AppendFailureBestEffortAsync(new ProductionAuditRecord(
                ProductionAuditEvents.LicenseRejected,
                DateTimeOffset.UtcNow,
                "License",
                result.Payload?.LicenseId,
                result.ErrorCode ?? result.Status.ToString(),
                correlationId));
            return result;
        }

        bool created;
        try
        {
            created = await _store.TryPersistInitialRawAsync(signedLicenseJson, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await _audit.AppendFailureBestEffortAsync(new ProductionAuditRecord(
                ProductionAuditEvents.LicenseRejected,
                DateTimeOffset.UtcNow,
                "License",
                result.Payload!.LicenseId,
                "license.persistence_failed",
                correlationId));
            return new LicenseValidationResult(
                LicenseValidationStatus.ValidationUnavailable,
                result.Payload,
                "The validated license could not be persisted safely.",
                "license.persistence_failed");
        }

        if (!created)
        {
            await _audit.AppendFailureBestEffortAsync(new ProductionAuditRecord(
                ProductionAuditEvents.LicenseImportBlocked,
                DateTimeOffset.UtcNow,
                "License",
                result.Payload!.LicenseId,
                "installed_license_exists",
                correlationId));
            throw new LicenseImportRequiresAuthorizationException();
        }

        _ = await _audit.AppendAfterSideEffectAsync(new ProductionAuditRecord(
            ProductionAuditEvents.LicenseImported,
            DateTimeOffset.UtcNow,
            "License",
            result.Payload!.LicenseId,
            $"Expiry={result.Payload.ExpiryDate:O}",
            correlationId));
        return result;
    }

    public async Task<LicenseValidationResult> ValidateAndPersistAsync(
        string signedLicenseJson,
        string successAuditEvent,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        var result = await _validator.ValidateAsync(signedLicenseJson, cancellationToken);
        if (!result.IsValid)
        {
            await _audit.AppendFailureBestEffortAsync(new ProductionAuditRecord(
                ProductionAuditEvents.LicenseRejected,
                DateTimeOffset.UtcNow,
                "License",
                result.Payload?.LicenseId,
                result.ErrorCode ?? result.Status.ToString(),
                correlationId));
            return result;
        }

        try
        {
            await _store.PersistRawAsync(signedLicenseJson, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await _audit.AppendFailureBestEffortAsync(new ProductionAuditRecord(
                ProductionAuditEvents.LicenseRejected,
                DateTimeOffset.UtcNow,
                "License",
                result.Payload!.LicenseId,
                "license.persistence_failed",
                correlationId));
            return new LicenseValidationResult(
                LicenseValidationStatus.ValidationUnavailable,
                result.Payload,
                "The validated license could not be persisted safely.",
                "license.persistence_failed");
        }

        _ = await _audit.AppendAfterSideEffectAsync(new ProductionAuditRecord(
            successAuditEvent,
            DateTimeOffset.UtcNow,
            "License",
            result.Payload!.LicenseId,
            $"Expiry={result.Payload.ExpiryDate:O}",
            correlationId));
        return result;
    }
}

/// <summary>
/// Unauthenticated license import is permitted only when no license artifact is installed. Once any
/// license artifact exists, even a corrupt one, the authorized replacement flow is required.
/// </summary>
public sealed class ImportLicenseHandler
{
    private readonly ILicenseStore _store;
    private readonly LicenseInstallationService _installation;
    private readonly ProductionAuditCoordinator _audit;

    public ImportLicenseHandler(ILicenseStore store, LicenseInstallationService installation, ProductionAuditCoordinator audit)
    {
        _store = store;
        _installation = installation;
        _audit = audit;
    }

    public async Task<LicenseValidationResult> HandleAsync(string signedLicenseJson, string? correlationId, CancellationToken cancellationToken = default)
    {
        bool exists;
        try
        {
            exists = await _store.ExistsAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new LicenseValidationResult(
                LicenseValidationStatus.ValidationUnavailable,
                null,
                "Installed-license state could not be verified safely.",
                "license.installation_state_unavailable");
        }

        if (exists)
        {
            await _audit.AppendFailureBestEffortAsync(new ProductionAuditRecord(
                ProductionAuditEvents.LicenseImportBlocked,
                DateTimeOffset.UtcNow,
                "License",
                null,
                "installed_license_exists",
                correlationId));
            throw new LicenseImportRequiresAuthorizationException();
        }

        return await _installation.ValidateAndPersistInitialAsync(
            signedLicenseJson,
            correlationId,
            cancellationToken);
    }
}

public sealed class RuntimeLicenseService
{
    private readonly ILicenseStore _store;
    private readonly ILicenseValidator _validator;

    public RuntimeLicenseService(ILicenseStore store, ILicenseValidator validator)
    {
        _store = store;
        _validator = validator;
    }

    public async Task<LicenseValidationResult> ValidatePersistedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var raw = await _store.ReadRawAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new LicenseValidationResult(LicenseValidationStatus.Missing, null, "No license is installed.", "license.missing");
            }

            return await _validator.ValidateAsync(raw, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new LicenseValidationResult(
                LicenseValidationStatus.ValidationUnavailable,
                null,
                "The installed license could not be read or validated safely.",
                "license.validation_unavailable");
        }
    }
}

public sealed class ReplaceLicenseHandler
{
    private readonly IProductionAuthorization _authorization;
    private readonly LicenseInstallationService _installation;

    public ReplaceLicenseHandler(IProductionAuthorization authorization, LicenseInstallationService installation)
    {
        _authorization = authorization;
        _installation = installation;
    }

    public async Task<LicenseValidationResult> HandleAsync(string signedLicenseJson, string? correlationId, CancellationToken cancellationToken = default)
    {
        await _authorization.EnsurePermissionAsync(ProductionPermissionNames.SettingsManage, cancellationToken);
        return await _installation.ValidateAndPersistAsync(
            signedLicenseJson,
            ProductionAuditEvents.LicenseReplaced,
            correlationId,
            cancellationToken);
    }
}
