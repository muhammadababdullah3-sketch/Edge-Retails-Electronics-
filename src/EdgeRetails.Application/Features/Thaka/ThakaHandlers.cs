using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Operations;
using EdgeRetails.Domain.Posting;
using EdgeRetails.Domain.Thaka;
using System.Globalization;

namespace EdgeRetails.Application.Features.Thaka;

public sealed record CreateThakaProjectCommand(
    Guid CustomerId,
    string ProjectName,
    string? SiteAddress,
    string? Note,
    DateOnly StartedOn,
    Guid ActorId,
    Guid CorrelationId,
    Guid ClientOperationId = default);

public sealed record IssueThakaMaterialLineInput(
    Guid ProductId,
    Guid ProductUnitId,
    decimal EnteredQuantity,
    decimal ExpectedUnitCharge,
    IReadOnlyList<Guid> InventoryUnitIds,
    Guid? PreferredPackUnitId = null,
    bool AllowSplitCuts = false);

public sealed record IssueThakaMaterialCommand(
    Guid ClientOperationId,
    Guid ProjectId,
    Guid ActorId,
    string? Note,
    IReadOnlyList<IssueThakaMaterialLineInput> Lines,
    DateOnly? PostingDate = null,
    SupervisorApprovalRequest? SupervisorApproval = null,
    bool? CallerSuppliedBooleanOverride = null);

public sealed record IssueThakaMaterialResult(
    Guid MaterialIssueId,
    string ChallanNumber,
    decimal TotalCharge,
    decimal TotalCost,
    bool WasExisting);

internal sealed record PreparedThakaLine(
    IssueThakaMaterialLineInput Input,
    Product Product,
    ProductUnit ProductUnit,
    TransactionQuantitySnapshot Quantity,
    decimal UnitCharge,
    decimal LineCharge,
    IReadOnlyList<InventoryUnit> SerializedUnits,
    PackAllocationResult? PackAllocation = null);

public sealed class CreateThakaProjectHandler
{
    private readonly IThakaRepository _thaka;
    private readonly IPartyRepository _parties;
    private readonly IDocumentNumberService _numbers;
    private readonly IBusinessAuditWriter _audit;
    private readonly IClock _clock;
    private readonly ITransactionRunner _transactions;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IOperationLock? _operationLock;
    private readonly IOperationOutcomeLedger? _outcomeLedger;

    public CreateThakaProjectHandler(
        IThakaRepository thaka,
        IPartyRepository parties,
        IDocumentNumberService numbers,
        IBusinessAuditWriter audit,
        IClock clock,
        ITransactionRunner transactions,
        IApplicationPermissionAuthorizer authorization,
        IUnitOfWork unitOfWork,
        IOperationLock? operationLock = null,
        IOperationOutcomeLedger? outcomeLedger = null)
    {
        _thaka = thaka;
        _parties = parties;
        _numbers = numbers;
        _audit = audit;
        _clock = clock;
        _transactions = transactions;
        _authorization = authorization;
        _unitOfWork = unitOfWork;
        _operationLock = operationLock;
        _outcomeLedger = outcomeLedger;
    }

    public async Task<Result<Guid>> HandleAsync(
        CreateThakaProjectCommand command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.ProjectName))
        {
            return Result<Guid>.Failure(
                "thaka.project_name_required",
                "Thaka project name is required.");
        }

        var domainProjection = new
        {
            command.CustomerId,
            ProjectName = command.ProjectName.Trim(),
            SiteAddress = Normalize(command.SiteAddress),
            Note = Normalize(command.Note),
            StartedOn = command.StartedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            command.ActorId
        };
        var versionedFingerprint = OperationPayloadFingerprint.ComputeVersionedFingerprint("CreateThakaProject", domainProjection);
        var payloadFingerprint = OperationPayloadFingerprint.ComputeSha256(
            "CreateThakaProject",
            command.CustomerId.ToString("D"),
            command.ProjectName.Trim(),
            Normalize(command.SiteAddress),
            Normalize(command.Note),
            command.StartedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            command.ActorId.ToString("D"));

        bool FingerprintMatches(string? fp) =>
            string.Equals(fp, versionedFingerprint, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fp, payloadFingerprint, StringComparison.OrdinalIgnoreCase);

        if (command.ClientOperationId != Guid.Empty && _outcomeLedger is not null)
        {
            var preOutcome = await _outcomeLedger.GetOutcomeAsync(command.ClientOperationId, cancellationToken);
            if (preOutcome is not null)
            {
                if (!string.Equals(preOutcome.OperationType, "CreateThakaProject", StringComparison.Ordinal))
                {
                    return Result<Guid>.Failure(
                        "idempotency.operation_type_mismatch",
                        $"Operation identity was already used for '{preOutcome.OperationType}'.");
                }

                if (preOutcome.ActorId.HasValue && command.ActorId != Guid.Empty && preOutcome.ActorId.Value != command.ActorId)
                {
                    return Result<Guid>.Failure(
                        "idempotency.context_mismatch",
                        "Operation actor mismatch.");
                }

                if (!string.IsNullOrEmpty(preOutcome.PayloadFingerprint) &&
                    !FingerprintMatches(preOutcome.PayloadFingerprint))
                {
                    return Result<Guid>.Failure(
                        "idempotency.payload_mismatch",
                        "This operation id is already associated with another or unresolved Thaka project operation.");
                }

                if (preOutcome.State == OperationOutcomeState.Succeeded && preOutcome.WasCommitted && preOutcome.EntityId.HasValue)
                {
                    return Result<Guid>.Success(preOutcome.EntityId.Value);
                }

                if (preOutcome.State == OperationOutcomeState.Failed)
                {
                    return Result<Guid>.Failure(
                        preOutcome.ErrorCode ?? "idempotency.outcome_unknown",
                        preOutcome.ErrorMessage ?? "Thaka project operation previously failed.");
                }
            }
        }

        return await _transactions.ExecuteAsync(async ct =>
        {
            var authorization = await _authorization.AuthorizeAsync(
                command.ActorId,
                PermissionKeys.ThakaManage,
                ct);
            if (!authorization.IsSuccess)
            {
                return Result<Guid>.Failure(
                    authorization.Error!.Code,
                    authorization.Error.Message);
            }

            if (command.ClientOperationId != Guid.Empty)
            {
                if (_operationLock is not null)
                    await _operationLock.AcquireAsync(command.ClientOperationId, ct);
                if (_outcomeLedger is not null)
                {
                    var saved = await _outcomeLedger.GetOutcomeAsync(command.ClientOperationId, ct);
                    if (saved is not null)
                    {
                        if (!string.Equals(saved.OperationType, "CreateThakaProject", StringComparison.Ordinal))
                        {
                            return Result<Guid>.Failure(
                                "idempotency.operation_type_mismatch",
                                $"Operation identity was already used for '{saved.OperationType}'.");
                        }

                        if (saved.ActorId.HasValue && command.ActorId != Guid.Empty && saved.ActorId.Value != command.ActorId)
                        {
                            return Result<Guid>.Failure(
                                "idempotency.context_mismatch",
                                "Operation actor mismatch.");
                        }

                        if (!string.IsNullOrEmpty(saved.PayloadFingerprint) &&
                            !FingerprintMatches(saved.PayloadFingerprint))
                        {
                            return Result<Guid>.Failure(
                                "idempotency.payload_mismatch",
                                "This operation id is already associated with another or unresolved Thaka project operation.");
                        }

                        if (saved.State == OperationOutcomeState.Succeeded && saved.EntityId.HasValue)
                        {
                            return Result<Guid>.Success(saved.EntityId.Value);
                        }

                        return Result<Guid>.Failure(
                            saved.ErrorCode ?? "idempotency.outcome_unknown",
                            saved.ErrorMessage ?? "Thaka project operation outcome requires reconciliation.");
                    }
                }
            }

            var customer = await _parties.GetCustomerForUpdateAsync(command.CustomerId, ct);
            if (customer is null || !customer.IsActive || customer.IsWalkIn)
            {
                return Result<Guid>.Failure(
                    "thaka.customer_invalid",
                    "Thaka requires an active named customer.");
            }

            var project = new ThakaProject
            {
                ProjectNumber = await _numbers.NextAsync("THAKA", ct),
                CustomerId = customer.Id,
                ProjectName = command.ProjectName.Trim(),
                SiteAddress = Normalize(command.SiteAddress),
                Note = Normalize(command.Note),
                StartedOn = command.StartedOn,
                CreatedBy = command.ActorId,
                CreatedAt = _clock.UtcNow
            };
            _thaka.AddProject(project); _audit.Record(
                "THAKA_PROJECT_CREATED",
                "THAKA_PROJECT",
                project.Id,
                command.ActorId,
                command.CorrelationId,
                project.ProjectNumber);

            if (_outcomeLedger is not null && command.ClientOperationId != Guid.Empty)
            {
                await _outcomeLedger.RecordSuccessAsync(
                    command.ClientOperationId,
                    "CreateThakaProject",
                    project.Id,
                    project.ProjectNumber,
                    actorId: command.ActorId,
                    payloadFingerprint: versionedFingerprint,
                    cancellationToken: ct);
            }

            await _unitOfWork.SaveChangesAsync(ct);
            return Result<Guid>.Success(project.Id);
        }, cancellationToken);
    }

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}

public sealed class IssueThakaMaterialHandler
{
    private readonly IThakaRepository _thaka;
    private readonly IPartyRepository _parties;
    private readonly ICatalogRepository _catalog;
    private readonly IInventoryRepository _inventory;
    private readonly IInventoryCostAllocator _costs;
    private readonly IOperationLock _operationLock;
    private readonly IResourceLock _resourceLock;
    private readonly IDocumentNumberService _numbers;
    private readonly IBusinessAuditWriter _audit;
    private readonly IClock _clock;
    private readonly ITransactionRunner _transactions;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IPackSubLedgerAllocationAuthority _packAllocations;
    private readonly IShopPostingValidationService? _postingValidation;

    public IssueThakaMaterialHandler(
        IThakaRepository thaka,
        IPartyRepository parties,
        ICatalogRepository catalog,
        IInventoryRepository inventory,
        IInventoryCostAllocator costs,
        IOperationLock operationLock,
        IResourceLock resourceLock,
        IDocumentNumberService numbers,
        IBusinessAuditWriter audit,
        IClock clock,
        ITransactionRunner transactions,
        IApplicationPermissionAuthorizer authorization,
        IUnitOfWork unitOfWork,
        IOperationOutcomeLedger? outcomeLedger = null,
        IPackSubLedgerAllocationAuthority? packAllocations = null,
        IShopPostingValidationService? postingValidation = null)
    {
        _thaka = thaka;
        _parties = parties;
        _catalog = catalog;
        _inventory = inventory;
        _costs = costs;
        _operationLock = operationLock;
        _resourceLock = resourceLock;
        _numbers = numbers;
        _audit = audit;
        _clock = clock;
        _transactions = transactions;
        _authorization = authorization;
        _unitOfWork = unitOfWork;
        _outcomeLedger = outcomeLedger;
        _packAllocations = packAllocations ?? new PackSubLedgerAllocationAuthority();
        _postingValidation = postingValidation;
    }

    private readonly IOperationOutcomeLedger? _outcomeLedger;

    public async Task<Result<IssueThakaMaterialResult>> HandleAsync(
        IssueThakaMaterialCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ClientOperationId == Guid.Empty || command.Lines.Count == 0)
        {
            return Result<IssueThakaMaterialResult>.Failure(
                "thaka.issue_invalid",
                "Material issue requires operation id and at least one item.");
        }
        if (command.Lines.GroupBy(x => new { x.ProductId, x.ProductUnitId })
            .Any(x => x.Count() > 1))
        {
            return Result<IssueThakaMaterialResult>.Failure(
                "thaka.issue_duplicate_line",
                "The same product and unit may appear only once.");
        }

        var domainProjection = new
        {
            command.ProjectId,
            Lines = command.Lines.OrderBy(x => x.ProductId).ThenBy(x => x.ProductUnitId)
                .Select(x => new
                {
                    x.ProductId,
                    x.ProductUnitId,
                    EnteredQuantity = QuantityMath.RoundQuantity(x.EnteredQuantity),
                    ExpectedUnitCharge = decimal.Round(x.ExpectedUnitCharge, 2, MidpointRounding.AwayFromZero)
                }).ToList(),
            Note = Normalize(command.Note),
            command.ActorId
        };
        var versionedFingerprint = OperationPayloadFingerprint.ComputeVersionedFingerprint("ThakaMaterialIssue", domainProjection);
        var payloadFingerprint = OperationPayloadFingerprint.ComputeSha256(
            "ThakaMaterialIssue",
            command.ProjectId.ToString("D"),
            string.Join(";", command.Lines.OrderBy(x => x.ProductId).ThenBy(x => x.ProductUnitId)
                .Select(x => string.Create(CultureInfo.InvariantCulture, $"{x.ProductId:D}:{x.ProductUnitId:D}:{x.EnteredQuantity:0.0000}:{x.ExpectedUnitCharge:0.00}"))),
            Normalize(command.Note),
            command.ActorId.ToString("D"));

        bool FingerprintMatches(string? fp) =>
            string.Equals(fp, versionedFingerprint, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fp, payloadFingerprint, StringComparison.OrdinalIgnoreCase);

        if (_outcomeLedger is not null)
        {
            var preOutcome = await _outcomeLedger.GetOutcomeAsync(command.ClientOperationId, cancellationToken);
            if (preOutcome is not null)
            {
                if (!string.Equals(preOutcome.OperationType, "ThakaMaterialIssue", StringComparison.Ordinal))
                {
                    return Result<IssueThakaMaterialResult>.Failure(
                        "idempotency.operation_type_mismatch",
                        $"Operation identity was already used for '{preOutcome.OperationType}'.");
                }

                if (preOutcome.ActorId.HasValue && command.ActorId != Guid.Empty && preOutcome.ActorId.Value != command.ActorId)
                {
                    return Result<IssueThakaMaterialResult>.Failure(
                        "idempotency.context_mismatch",
                        "Operation actor mismatch.");
                }

                if (!string.IsNullOrEmpty(preOutcome.PayloadFingerprint) &&
                    !FingerprintMatches(preOutcome.PayloadFingerprint))
                {
                    return Result<IssueThakaMaterialResult>.Failure(
                        "idempotency.payload_mismatch",
                        "This operation id is already associated with another or unresolved material issue.");
                }

                if (preOutcome.State == OperationOutcomeState.Succeeded && preOutcome.WasCommitted && preOutcome.EntityId.HasValue)
                {
                    var existingIssue = await _thaka.GetIssueByOperationIdAsync(command.ClientOperationId, cancellationToken);
                    if (existingIssue is not null)
                    {
                        return Result<IssueThakaMaterialResult>.Success(new(
                            existingIssue.Id,
                            existingIssue.ChallanNumber,
                            existingIssue.TotalCharge,
                            existingIssue.TotalCost,
                            true));
                    }
                }

                if (preOutcome.State == OperationOutcomeState.Failed)
                {
                    return Result<IssueThakaMaterialResult>.Failure(
                        preOutcome.ErrorCode ?? "idempotency.outcome_unknown",
                        preOutcome.ErrorMessage ?? "Material issue previously failed.");
                }
            }
        }

        var result = await _transactions.ExecuteAsync(async ct =>
        {
            var authorization = await _authorization.AuthorizeAsync(
                command.ActorId,
                PermissionKeys.ThakaManage,
                ct);
            if (!authorization.IsSuccess)
            {
                return Result<IssueThakaMaterialResult>.Failure(
                    authorization.Error!.Code,
                    authorization.Error.Message);
            }

            await _operationLock.AcquireAsync(command.ClientOperationId, ct);
            var existing = await _thaka.GetIssueByOperationIdAsync(
                command.ClientOperationId,
                ct);
            if (existing is not null)
            {
                var savedOutcome = _outcomeLedger is null
                    ? null
                    : await _outcomeLedger.GetOutcomeAsync(command.ClientOperationId, ct);
                if (_outcomeLedger is not null && savedOutcome is not null)
                {
                    if (!string.Equals(savedOutcome.OperationType, "ThakaMaterialIssue", StringComparison.Ordinal))
                    {
                        return Result<IssueThakaMaterialResult>.Failure(
                            "idempotency.operation_type_mismatch",
                            $"Operation identity was already used for '{savedOutcome.OperationType}'.");
                    }

                    if (savedOutcome.ActorId.HasValue && command.ActorId != Guid.Empty && savedOutcome.ActorId.Value != command.ActorId)
                    {
                        return Result<IssueThakaMaterialResult>.Failure(
                            "idempotency.context_mismatch",
                            "Operation actor mismatch.");
                    }

                    if (!string.IsNullOrEmpty(savedOutcome.PayloadFingerprint) &&
                        !FingerprintMatches(savedOutcome.PayloadFingerprint))
                    {
                        return Result<IssueThakaMaterialResult>.Failure(
                            "idempotency.payload_mismatch",
                            "This operation id is already associated with another or unresolved material issue.");
                    }
                }

                if (_outcomeLedger is not null)
                {
                    await _outcomeLedger.RecordSuccessAsync(
                        command.ClientOperationId,
                        "ThakaMaterialIssue",
                        existing.Id,
                        existing.ChallanNumber,
                        actorId: command.ActorId,
                        payloadFingerprint: versionedFingerprint,
                        cancellationToken: ct);
                }

                return Result<IssueThakaMaterialResult>.Success(new(
                    existing.Id,
                    existing.ChallanNumber,
                    existing.TotalCharge,
                    existing.TotalCost,
                    true));
            }

            await _resourceLock.AcquireAsync("thaka-project", command.ProjectId, ct);
            var project = await _thaka.GetProjectForUpdateAsync(command.ProjectId, ct);
            if (project is null || project.Status != ThakaProjectStatus.Active)
            {
                return Result<IssueThakaMaterialResult>.Failure(
                    "thaka.project_not_active",
                    "Material can only be issued to an active Thaka project.");
            }

            var customer = await _parties.GetCustomerForUpdateAsync(project.CustomerId, ct);
            if (customer is null || !customer.IsActive)
            {
                return Result<IssueThakaMaterialResult>.Failure("thaka.customer_suspended", "New material cannot be issued to a suspended customer.");
            }
            foreach (var productId in command.Lines
                .Select(x => x.ProductId)
                .Distinct()
                .OrderBy(x => x))
            {
                await _resourceLock.AcquireAsync("product", productId, ct);
            }
            var allUnitIds = command.Lines
                .Where(x => x.InventoryUnitIds is not null)
                .SelectMany(x => x.InventoryUnitIds!)
                .ToList();

            if (allUnitIds.Distinct().Count() != allUnitIds.Count)
            {
                return Result<IssueThakaMaterialResult>.Failure(
                    "thaka.serial_selection_invalid",
                    "Serialized issue requires one unique inventory unit per base unit.");
            }

            try
            {
                var prepared = new List<PreparedThakaLine>();
                var stocks = new Dictionary<Guid, StockBalance>();

                foreach (var input in command.Lines)
                {
                    var product = await _catalog.GetProductForUpdateAsync(
                        input.ProductId,
                        ct);
                    if (product is null || !product.IsActive)
                    {
                        return Result<IssueThakaMaterialResult>.Failure(
                            "thaka.product_inactive",
                            "One or more products are missing or inactive.");
                    }

                    if (await _inventory.IsProductBlockedByCountingStocktakeAsync(
                            product.Id,
                            ct))
                    {
                        return Result<IssueThakaMaterialResult>.Failure(
                            "inventory.stocktake_blocks_product",
                            $"Product '{product.Name}' is locked by an active stocktake.");
                    }

                    var productUnit = await _catalog.GetProductUnitAsync(
                        input.ProductUnitId,
                        ct);
                    if (productUnit is null ||
                        productUnit.ProductId != product.Id ||
                        !productUnit.IsActive ||
                        !productUnit.CanUseInThaka)
                    {
                        return Result<IssueThakaMaterialResult>.Failure(
                            "thaka.unit_invalid",
                            $"Selected unit is not valid for '{product.Name}'.");
                    }

                    var authoritativeCharge = Money(product.DefaultSalePrice);
                    if (Money(input.ExpectedUnitCharge) != authoritativeCharge)
                    {
                        return Result<IssueThakaMaterialResult>.Failure(
                            "thaka.price_stale",
                            $"Price changed for '{product.Name}'. Refresh before issuing.");
                    }
                    var quantity = TransactionQuantitySnapshot.Create(
                        productUnit,
                        input.EnteredQuantity,
                        product.TrackingMode);

                    var stock = await _inventory.GetStockBalanceForUpdateAsync(
                        product.Id,
                        ct);
                    if (stock is null || stock.SellableQty < quantity.BaseQuantity)
                    {
                        return Result<IssueThakaMaterialResult>.Failure(
                            "thaka.insufficient_stock",
                            $"Insufficient sellable stock for '{product.Name}'.");
                    }
                    stocks[product.Id] = stock;

                    IReadOnlyList<InventoryUnit> serialized = Array.Empty<InventoryUnit>();
                    PackAllocationResult? packAllocationResult = null;

                    if (product.TrackingMode == TrackingMode.Container)
                    {
                        var isCable = product.Name.Contains("Cable", StringComparison.OrdinalIgnoreCase) ||
                                      product.Name.Contains("Wire", StringComparison.OrdinalIgnoreCase) ||
                                      product.AttributesJson?.Contains("Length", StringComparison.OrdinalIgnoreCase) == true;

                        if (!isCable && !QuantityMath.IsWhole(quantity.BaseQuantity))
                        {
                            return Result<IssueThakaMaterialResult>.Failure(
                                "thaka.pack_fractional_count_prohibited",
                                $"Fractional unit quantity '{quantity.BaseQuantity}' is not permitted for count-based pack product '{product.Name}'.");
                        }

                        IReadOnlyList<InventoryUnit> eligibleUnits;
                        if (input.InventoryUnitIds.Count > 0)
                        {
                            eligibleUnits = await _inventory.GetInventoryUnitsForUpdateAsync(
                                product.Id,
                                input.InventoryUnitIds,
                                ct);

                            if (eligibleUnits.Count != input.InventoryUnitIds.Count ||
                                eligibleUnits.Any(x => x.Status != InventoryUnitStatus.InStock || x.InventoryLotId is null))
                            {
                                return Result<IssueThakaMaterialResult>.Failure(
                                    "thaka.serial_not_available",
                                    "One or more serialized units are not available.");
                            }
                        }
                        else
                        {
                            eligibleUnits = await _inventory.GetSellableInventoryUnitsAsync(product.Id, ct);
                        }

                        foreach (var unit in eligibleUnits)
                        {
                            if (unit.PackState is null)
                            {
                                var qty = unit.RemainingBaseQuantity ?? unit.InitialBaseQuantity;
                                if (qty is null || qty <= 0m)
                                {
                                    qty = await _inventory.GetPhysicalUnitBaseQuantitySnapshotAsync(unit, ct);
                                }
                                unit.InitialBaseQuantity = qty;
                                unit.RemainingBaseQuantity = qty;
                                unit.PackState = PackState.Sealed;
                            }
                        }

                        var preferredUnitId = input.PreferredPackUnitId ?? (input.InventoryUnitIds.Count == 1 ? input.InventoryUnitIds[0] : null);

                        var allocRequest = new PackAllocationRequest(
                            ProductId: product.Id,
                            RequestedBaseQuantity: quantity.BaseQuantity,
                            MovementId: Guid.Empty,
                            OperationType: PackAllocationOperationTypes.ThakaIssue,
                            ActorId: command.ActorId,
                            OccurredAt: _clock.UtcNow,
                            PreferredPackUnitId: preferredUnitId,
                            AllowSplitCuts: input.AllowSplitCuts,
                            IsCableOrLengthProduct: isCable,
                            MinimumCutIncrement: 1m);

                        try
                        {
                            packAllocationResult = _packAllocations.Allocate(eligibleUnits, allocRequest);
                        }
                        catch (BusinessRuleException ex)
                        {
                            return Result<IssueThakaMaterialResult>.Failure(ex.Code, ex.Message);
                        }

                        serialized = packAllocationResult.Allocations
                            .Select(a => eligibleUnits.First(u => u.Id == a.PackUnitId))
                            .Distinct()
                            .OrderBy(u => u.Id)
                            .ToArray();
                    }
                    else if (product.TrackingMode is TrackingMode.Serialized or TrackingMode.IndividualPiece)
                    {
                        if (!QuantityMath.IsWhole(quantity.BaseQuantity) ||
                            input.InventoryUnitIds.Count != quantity.BaseQuantity ||
                            input.InventoryUnitIds.Distinct().Count() != input.InventoryUnitIds.Count)
                        {
                            return Result<IssueThakaMaterialResult>.Failure(
                                "thaka.serial_selection_invalid",
                                "Serialized issue requires one unique inventory unit per base unit.");
                        }

                        serialized = await _inventory.GetInventoryUnitsForUpdateAsync(
                            product.Id,
                            input.InventoryUnitIds,
                            ct);
                        if (serialized.Count != input.InventoryUnitIds.Count ||
                            serialized.Any(x =>
                                x.Status != InventoryUnitStatus.InStock ||
                                x.InventoryLotId is null))
                        {
                            return Result<IssueThakaMaterialResult>.Failure(
                                "thaka.serial_not_available",
                                "One or more serialized units are not available.");
                        }
                    }
                    else if (input.InventoryUnitIds.Count > 0)
                    {
                        return Result<IssueThakaMaterialResult>.Failure(
                            "thaka.serials_not_allowed",
                            $"Product '{product.Name}' is not serialized.");
                    }

                    prepared.Add(new PreparedThakaLine(
                        input,
                        product,
                        productUnit,
                        quantity,
                        authoritativeCharge,
                        Money(authoritativeCharge * quantity.BaseQuantity),
                        serialized,
                        packAllocationResult));
                }
                var issue = new ThakaMaterialIssue
                {
                    ProjectId = project.Id,
                    ChallanNumber = await _numbers.NextAsync("THK-ISS", ct),
                    ClientOperationId = command.ClientOperationId,
                    Note = Normalize(command.Note),
                    IssuedBy = command.ActorId,
                    IssuedAt = _clock.UtcNow
                };
                _thaka.AddMaterialIssue(issue);

                decimal totalCost = 0m;
                foreach (var line in prepared)
                {
                    var stock = stocks[line.Product.Id];
                    var before = stock.SellableQty;
                    var movement = new InventoryMovement
                    {
                        ProductId = line.Product.Id,
                        MovementType = InventoryMovementType.ThakaOut,
                        ReferenceType = "THAKA_MATERIAL_ISSUE",
                        ReferenceId = issue.Id,
                        ActorId = command.ActorId,
                        OccurredAt = _clock.UtcNow,
                        CorrelationId = command.ClientOperationId,
                        Note = issue.ChallanNumber
                    };
                    _inventory.AddMovement(movement);

                    decimal lineCost;
                    if (line.Product.TrackingMode == TrackingMode.Container && line.PackAllocation is not null)
                    {
                        lineCost = await ConsumePackAllocationAsync(line, line.PackAllocation, movement, ct);
                    }
                    else if (line.Product.TrackingMode is TrackingMode.Serialized or TrackingMode.IndividualPiece)
                    {
                        lineCost = await ConsumeSerializedAsync(line, movement, ct);
                    }
                    else
                    {
                        lineCost = await _costs.RemoveCarryingValueAsync(
                            line.Product.Id,
                            line.Quantity.BaseQuantity,
                            null,
                            ct);
                        var unitCost = Cost(lineCost / line.Quantity.BaseQuantity);
                        await _costs.ConsumeBucketAsync(
                            line.Product.Id,
                            InventoryBucket.Sellable,
                            line.Quantity.BaseQuantity,
                            movement.Id,
                            unitCost,
                            ct);
                    }
                    stock.ApplyDelta(
                        InventoryBucket.Sellable,
                        -line.Quantity.BaseQuantity);
                    var costPerBase = Cost(lineCost / line.Quantity.BaseQuantity);
                    movement.UnitCostSnapshot = costPerBase;

                    _inventory.AddMovementEffect(new InventoryMovementEffect
                    {
                        MovementId = movement.Id,
                        StockBucket = InventoryBucket.Sellable,
                        QuantityDelta = -line.Quantity.BaseQuantity,
                        QuantityBefore = before,
                        QuantityAfter = stock.SellableQty
                    });

                    var item = new ThakaMaterialIssueItem
                    {
                        MaterialIssueId = issue.Id,
                        InventoryMovementId = movement.Id,
                        ProductId = line.Product.Id,
                        ProductUnitId = line.ProductUnit.Id,
                        ProductNameSnapshot = line.Product.Name,
                        SkuSnapshot = line.Product.Sku,
                        EnteredQuantity = line.Quantity.EnteredQuantity,
                        FactorToBaseSnapshot = line.Quantity.FactorToBaseSnapshot,
                        BaseQuantity = line.Quantity.BaseQuantity,
                        UnitCharge = line.UnitCharge,
                        LineCharge = line.LineCharge,
                        UnitCostSnapshot = costPerBase,
                        TotalCostSnapshot = Cost(lineCost),
                        GrossProfitSnapshot = Money(line.LineCharge - lineCost)
                    };
                    _thaka.AddMaterialIssueItem(item);

                    if (line.Product.TrackingMode == TrackingMode.Container && line.PackAllocation is not null)
                    {
                        foreach (var alloc in line.PackAllocation.Allocations)
                        {
                            alloc.MovementId = movement.Id;
                            alloc.ThakaIssueItemId = item.Id;
                            alloc.ValidateInvariants();
                            _inventory.AddPackAllocation(alloc);

                            _thaka.AddMaterialIssueUnit(new ThakaMaterialIssueUnit
                            {
                                MaterialIssueItemId = item.Id,
                                InventoryUnitId = alloc.PackUnitId,
                                UnitCostSnapshot = alloc.TotalCostSnapshot
                            });
                        }
                    }
                    else
                    {
                        foreach (var unit in line.SerializedUnits)
                        {
                            _thaka.AddMaterialIssueUnit(new ThakaMaterialIssueUnit
                            {
                                MaterialIssueItemId = item.Id,
                                InventoryUnitId = unit.Id,
                                UnitCostSnapshot = unit.AcquisitionCost
                            });
                        }
                    }

                    totalCost += lineCost;
                }
                issue.TotalCharge = Money(prepared.Sum(x => x.LineCharge));
                issue.TotalCost = Cost(totalCost);
                issue.GrossProfit = Money(issue.TotalCharge - totalCost);

                _audit.Record(
                    "THAKA_MATERIAL_ISSUED",
                    "THAKA_MATERIAL_ISSUE",
                    issue.Id,
                    command.ActorId,
                    command.ClientOperationId,
                    $"{issue.ChallanNumber}: {issue.TotalCharge:0.00}");

                if (_outcomeLedger is not null)
                {
                    await _outcomeLedger.RecordSuccessAsync(
                        command.ClientOperationId,
                        "ThakaMaterialIssue",
                        issue.Id,
                        issue.ChallanNumber,
                        actorId: command.ActorId,
                        payloadFingerprint: versionedFingerprint,
                        cancellationToken: ct);
                }

                if (_postingValidation is not null)
                {
                    var postingDate = command.PostingDate ?? _clock.ShopDate;
                    var postingValidationResult = await _postingValidation.ValidateAndAuditPostingAsync(new PostingValidationCommand(
                        OperationType: PostingOperationTypes.Thaka,
                        EntityId: issue.Id,
                        EntityNumber: issue.ChallanNumber,
                        ActorId: command.ActorId,
                        PostingDate: postingDate,
                        SupplierDocumentDate: null,
                        SupervisorApproval: command.SupervisorApproval,
                        CallerSuppliedBooleanOverride: command.CallerSuppliedBooleanOverride), ct);

                    if (!postingValidationResult.IsSuccess)
                    {
                        return Result<IssueThakaMaterialResult>.Failure(
                            postingValidationResult.Error!.Code,
                            postingValidationResult.Error.Message);
                    }
                }

                await _unitOfWork.SaveChangesAsync(ct);
                return Result<IssueThakaMaterialResult>.Success(new(
                    issue.Id,
                    issue.ChallanNumber,
                    issue.TotalCharge,
                    issue.TotalCost,
                    false));
            }
            catch (BusinessRuleException ex)
            {
                return Result<IssueThakaMaterialResult>.Failure(ex.Code, ex.Message);
            }
        }, cancellationToken);

        if (!result.IsSuccess && _outcomeLedger is not null && command.ClientOperationId != Guid.Empty &&
            result.Error?.Code?.StartsWith("idempotency.") != true)
        {
            await _outcomeLedger.RecordFailureAsync(
                command.ClientOperationId,
                "ThakaMaterialIssue",
                result.Error?.Code ?? "thaka.issue_failed",
                result.Error?.Message ?? "Thaka material issue failed.",
                actorId: command.ActorId,
                cancellationToken: cancellationToken);
        }

        return result;
    }

    private async Task<decimal> ConsumeSerializedAsync(
        PreparedThakaLine line,
        InventoryMovement movement,
        CancellationToken cancellationToken)
    {
        decimal totalCost = 0m;
        var basePerUnit = line.Quantity.BaseQuantity / line.SerializedUnits.Count;
        foreach (var unit in line.SerializedUnits.OrderBy(x => x.Id))
        {
            var lotBalance = await _inventory.GetLotBucketBalanceForUpdateAsync(
                unit.InventoryLotId!.Value,
                InventoryBucket.Sellable,
                cancellationToken)
                ?? throw new BusinessRuleException(
                    "thaka.serial_lot_missing",
                    "Serialized unit inventory lot was not found."); if (lotBalance.Quantity < basePerUnit)
            {
                throw new BusinessRuleException(
                    "thaka.serial_lot_insufficient",
                    "Serialized unit inventory lot is no longer sellable.");
            }

            lotBalance.Quantity = QuantityMath.RoundQuantity(
                lotBalance.Quantity - basePerUnit);
            _inventory.AddLotConsumption(new InventoryLotConsumption
            {
                LotId = unit.InventoryLotId.Value,
                MovementId = movement.Id,
                Quantity = basePerUnit,
                UnitCostSnapshot = unit.AcquisitionCost / basePerUnit,
                TotalCostSnapshot = unit.AcquisitionCost,
                OccurredAt = _clock.UtcNow
            });

            totalCost += await _costs.RemoveCarryingValueAsync(
                line.Product.Id,
                basePerUnit,
                unit.AcquisitionCost / basePerUnit,
                cancellationToken);

            var from = unit.Status;
            unit.Status = InventoryUnitStatus.IssuedThaka;
            unit.Version++;
            _inventory.AddMovementUnit(new InventoryMovementUnit
            {
                MovementId = movement.Id,
                InventoryUnitId = unit.Id,
                FromStatus = from,
                ToStatus = unit.Status
            });
        }

        return Cost(totalCost);
    }

    private async Task<decimal> ConsumePackAllocationAsync(
        PreparedThakaLine line,
        PackAllocationResult packAllocResult,
        InventoryMovement movement,
        CancellationToken ct)
    {
        decimal totalCost = 0m;

        foreach (var alloc in packAllocResult.Allocations)
        {
            var unit = line.SerializedUnits.First(u => u.Id == alloc.PackUnitId);
            var lotId = unit.InventoryLotId ?? alloc.LotId;

            var lotBalance = await _inventory.GetLotBucketBalanceForUpdateAsync(
                lotId,
                InventoryBucket.Sellable,
                ct)
                ?? throw new BusinessRuleException(
                    "thaka.serial_lot_missing",
                    "Pack inventory lot was not found.");

            if (lotBalance.Quantity < alloc.AllocatedBaseQuantity)
            {
                throw new BusinessRuleException(
                    "thaka.serial_lot_insufficient",
                    "Pack inventory lot is no longer sellable.");
            }

            lotBalance.Quantity = QuantityMath.RoundQuantity(
                lotBalance.Quantity - alloc.AllocatedBaseQuantity);

            _inventory.AddLotConsumption(new InventoryLotConsumption
            {
                LotId = lotId,
                MovementId = movement.Id,
                Quantity = alloc.AllocatedBaseQuantity,
                UnitCostSnapshot = alloc.UnitCostSnapshot,
                TotalCostSnapshot = alloc.TotalCostSnapshot,
                OccurredAt = _clock.UtcNow
            });

            totalCost += await _costs.RemoveCarryingValueByAmountAsync(
                line.Product.Id,
                alloc.AllocatedBaseQuantity,
                alloc.TotalCostSnapshot,
                ct);

            var fromStatus = unit.Status;
            if (alloc.PackStateAfter == PackState.Consumed)
            {
                unit.Status = InventoryUnitStatus.IssuedThaka;
            }
            unit.Version++;

            _inventory.AddMovementUnit(new InventoryMovementUnit
            {
                MovementId = movement.Id,
                InventoryUnitId = unit.Id,
                FromStatus = fromStatus,
                ToStatus = unit.Status
            });
        }

        return Cost(totalCost);
    }

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
    private static decimal Money(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    private static decimal Cost(decimal value) =>
        decimal.Round(value, 6, MidpointRounding.AwayFromZero);
}
