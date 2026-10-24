using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Catalog;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;

namespace EdgeRetails.Application.Features.Setup;

public sealed record CatalogReferenceUpgradeResult(int UnitsAdded, int UnitsPreserved, bool AlreadyApplied);

/// <summary>
/// Explicit, one-time existing-shop operational fixed-unit reconciliation. It is invoked only
/// by an authorized maintenance/upgrade command, never at startup or during ordinary reads.
/// Categories are excluded pending an owner-approved symbol/name map.
/// </summary>
public sealed class CatalogReferenceUpgradeHandler
{
    private readonly ICatalogRepository _catalog;
    private readonly IApplicationPermissionAuthorizer _authorizer;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ISetupRepository _setup;
    private readonly IClock _clock;
    private readonly IBusinessAuditWriter _audit;

    public CatalogReferenceUpgradeHandler(
        ICatalogRepository catalog,
        IApplicationPermissionAuthorizer authorizer,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork,
        ISetupRepository setup,
        IClock clock,
        IBusinessAuditWriter audit)
    {
        _catalog = catalog;
        _authorizer = authorizer;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
        _setup = setup;
        _clock = clock;
        _audit = audit;
    }

    public Task<Result<CatalogReferenceUpgradeResult>> HandleAsync(Guid actorId, CancellationToken cancellationToken) =>
        _transactions.ExecuteAsync(async ct =>
        {
            if (actorId == Guid.Empty)
            {
                return Result<CatalogReferenceUpgradeResult>.Failure("catalog.upgrade_actor_required", "An authenticated actor is required.");
            }

            var auth = await _authorizer.AuthorizeAsync(actorId, PermissionKeys.InventoryManage, ct);
            if (!auth.IsSuccess)
            {
                return Result<CatalogReferenceUpgradeResult>.Failure(auth.Error!.Code, auth.Error.Message);
            }

            var installation = await _setup.GetInstallationStateForUpdateAsync(ct);
            if (installation is null || installation.SetupStatus != Domain.SystemConfiguration.SetupStatus.Complete)
            {
                return Result<CatalogReferenceUpgradeResult>.Failure(
                    "catalog.upgrade_installation_not_complete",
                    "Operational unit reconciliation requires a completed shop setup.");
            }

            if (await _setup.HasCatalogReferenceUpgradeAsync(installation.InstallationId, ct))
            {
                return Result<CatalogReferenceUpgradeResult>.Success(
                    new CatalogReferenceUpgradeResult(0, 0, AlreadyApplied: true));
            }

            var existing = await _catalog.GetUnitsAsync(includeInactive: true, ct);
            var bySymbol = existing.ToDictionary(x => x.Symbol, StringComparer.OrdinalIgnoreCase);
            var reservedNames = existing.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var toAdd = new List<Unit>();
            var preserved = 0;

            foreach (var definition in CatalogOperationalReferenceSeed.FixedUnits)
            {
                // Only fixed, owner-selected Count/Length conversions are added by this upgrade.
                // Product-defined PAIR/SET, NOT_AUTO KIT, gated stock, and service rows remain untouched.
                if (!definition.IsActiveSupported)
                {
                    continue;
                }

                if (bySymbol.TryGetValue(definition.Symbol, out var current))
                {
                    if (!string.Equals(current.Name, definition.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        return Result<CatalogReferenceUpgradeResult>.Failure(
                            "catalog.upgrade_unit_symbol_conflict",
                            $"Unit symbol '{definition.Symbol}' is already assigned to a different name; no unit rows were changed.");
                    }
                    if (definition.ConversionKind == "FIXED" || definition.ConversionKind == "FIXED_TO_M")
                    {
                        if (current.Dimension != definition.Dimension ||
                            current.FactorToReference != definition.FactorToReference ||
                            !string.Equals(current.ConversionKind, definition.ConversionKind, StringComparison.Ordinal))
                        {
                            return Result<CatalogReferenceUpgradeResult>.Failure(
                                "catalog.upgrade_unit_conversion_conflict",
                                $"Fixed unit '{definition.Symbol}' has a conflicting existing conversion; no unit rows were changed.");
                        }
                    }
                    // Existing fixed unit definition already matches; retain its ID, active flag,
                    // precision and all historical ProductUnit references.
                    preserved++;
                    continue;
                }

                if (reservedNames.Contains(definition.Name))
                {
                    return Result<CatalogReferenceUpgradeResult>.Failure(
                        "catalog.upgrade_unit_name_conflict",
                        $"Unit name '{definition.Name}' is assigned to a different symbol; no unit rows were changed.");
                }

                toAdd.Add(new Unit
                {
                    Name = definition.Name,
                    Symbol = definition.Symbol,
                    Dimension = definition.Dimension,
                    FactorToReference = definition.FactorToReference,
                    ConversionKind = definition.ConversionKind,
                    DisplayDecimalPlaces = definition.DecimalPlaces,
                    IsActive = definition.IsActiveSupported
                });
                reservedNames.Add(definition.Name);
            }

            foreach (var unit in toAdd)
            {
                _catalog.AddUnit(unit);
            }

            installation.Version++;
            _audit.Record(
                "FIXED_UNITS_SEEDED",
                "CATALOG_REFERENCE_UPGRADE",
                installation.InstallationId,
                actorId,
                installation.InstallationId,
                $"Added {toAdd.Count} fixed units; preserved {preserved}.");
            await _unitOfWork.SaveChangesAsync(ct);
            return Result<CatalogReferenceUpgradeResult>.Success(new CatalogReferenceUpgradeResult(toAdd.Count, preserved, AlreadyApplied: false));
        }, cancellationToken);
}
