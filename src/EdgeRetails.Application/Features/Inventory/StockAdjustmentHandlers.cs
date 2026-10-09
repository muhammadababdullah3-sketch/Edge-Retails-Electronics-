using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;

namespace EdgeRetails.Application.Features.Inventory;

public sealed record SerializedAdjustmentUnitCommand(
    string? SerialNumber,
    string? Imei1 = null,
    string? Imei2 = null);

internal sealed record EffectiveAdjustmentItem(
    StockAdjustmentDirection Direction,
    decimal BaseQuantity,
    bool IsNoOp);

public sealed record StockAdjustmentItemCommand(
    Guid ProductId,
    Guid? ProductUnitId,
    StockAdjustmentDirection Direction,
    InventoryBucket TargetBucket,
    decimal BaseQuantity,
    decimal? UnitCostSnapshot,
    Guid? SupplierId = null,
    IReadOnlyList<SerializedAdjustmentUnitCommand>? SerializedUnits = null,
    IReadOnlyList<Guid>? InventoryUnitIds = null,
    string? ReasonDetails = null);

public sealed record CreateStockAdjustmentCommand(
    StockAdjustmentMode Mode,
    StockAdjustmentReason Reason,
    IReadOnlyList<StockAdjustmentItemCommand> Items,
    Guid ActorId,
    Guid CorrelationId,
    string? Note = null);

public sealed class CreateStockAdjustmentHandler
{
    private readonly ICatalogRepository _catalog;
    private readonly IInventoryRepository _inventory;
    private readonly IInventoryCostAllocator _costAllocator;
    private readonly IPartyRepository _parties;
    private readonly ITraceabilityRepository _traceability;
    private readonly IResourceLock _resourceLock;
    private readonly IBusinessAuditWriter _audit;
    private readonly IClock _clock;
    private readonly ITransactionRunner _transactions;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IPhysicalUnitCreationAuthority? _physicalUnits;
    private readonly IOperationLock? _operationLock;

    public CreateStockAdjustmentHandler(
        ICatalogRepository catalog,
        IInventoryRepository inventory,
        IInventoryCostAllocator costAllocator,
        IPartyRepository parties,
        ITraceabilityRepository traceability,
        IResourceLock resourceLock,
        IBusinessAuditWriter audit,
        IClock clock,
        ITransactionRunner transactions,
        IApplicationPermissionAuthorizer authorization,
        IUnitOfWork unitOfWork,
        IOperationOutcomeLedger? outcomeLedger = null,
        IPhysicalUnitCreationAuthority? physicalUnitCreationAuthority = null,
        IOperationLock? operationLock = null)
    {
        _catalog = catalog;
        _inventory = inventory;
        _costAllocator = costAllocator;
        _parties = parties;
        _traceability = traceability;
        _resourceLock = resourceLock;
        _audit = audit;
        _clock = clock;
        _transactions = transactions;
        _authorization = authorization;
        _unitOfWork = unitOfWork;
        _outcomeLedger = outcomeLedger;
        _physicalUnits = physicalUnitCreationAuthority;
        _operationLock = operationLock;
    }

    private readonly IOperationOutcomeLedger? _outcomeLedger;

    public async Task<Result<Guid>> HandleAsync(
        CreateStockAdjustmentCommand command,
        CancellationToken cancellationToken)
    {
        if (command.CorrelationId == Guid.Empty)
        {
            return Result<Guid>.Failure("idempotency.operation_id_required", "Stock adjustment operation identity is required.");
        }
        if (_operationLock is null || _outcomeLedger is null)
        {
            return Result<Guid>.Failure("inventory.adjustment_replay_authority_missing", "Canonical operation lock and outcome ledger are required.");
        }
        if (command.Items is null || command.Items.Count == 0)
        {
            return Result<Guid>.Failure(
                "inventory.adjustment_items_required",
                "At least one adjustment item is required.");
        }

        var auth = await _authorization.AuthorizeAsync(
            command.ActorId,
            PermissionKeys.InventoryManage,
            cancellationToken);
        if (!auth.IsSuccess)
        {
            return Result<Guid>.Failure(auth.Error!.Code, auth.Error.Message);
        }

        string fingerprint;
        try
        {
            fingerprint = OperationPayloadFingerprint.ComputeSha256(System.Text.Json.JsonSerializer.Serialize(new
            {
                command.Mode,
                command.Reason,
                command.ActorId,
                Note = command.Note?.Trim(),
                Items = command.Items.Select(x => new
                {
                    x.ProductId,
                    x.ProductUnitId,
                    x.Direction,
                    x.TargetBucket,
                    Quantity = x.BaseQuantity.ToString("G29", System.Globalization.CultureInfo.InvariantCulture),
                    Cost = x.UnitCostSnapshot?.ToString("G29", System.Globalization.CultureInfo.InvariantCulture),
                    x.SupplierId,
                    Units = x.SerializedUnits?.Select(u => new
                    {
                        Serial = IdentityNormalizationRules.NormalizeOptionalSerialNumber(u.SerialNumber),
                        Imei1 = IdentityNormalizationRules.NormalizeOptionalImei(u.Imei1),
                        Imei2 = IdentityNormalizationRules.NormalizeOptionalImei(u.Imei2)
                    }).ToArray(),
                    UnitIds = x.InventoryUnitIds?.OrderBy(id => id).ToArray(),
                    Details = x.ReasonDetails?.Trim()
                }).ToArray()
            }));
        }
        catch (BusinessRuleException ex)
        {
            return Result<Guid>.Failure(ex.Code, ex.Message);
        }

        var ownsOutcome = false;
        var result = await _transactions.ExecuteAsync(async ct =>
        {
            await _operationLock.AcquireAsync(command.CorrelationId, ct);
            var outcome = await _outcomeLedger.GetOutcomeAsync(command.CorrelationId, ct);
            if (outcome is not null)
            {
                if (outcome.OperationType != "StockAdjustment" || outcome.ActorId != command.ActorId ||
                    outcome.PayloadFingerprint != fingerprint)
                {
                    return Result<Guid>.Failure("idempotency.payload_mismatch", "Operation was submitted with a different adjustment payload or actor.");
                }
                if (outcome.State == OperationOutcomeState.Succeeded && outcome.WasCommitted && outcome.EntityId.HasValue)
                {
                    return Result<Guid>.Success(outcome.EntityId.Value);
                }
                return Result<Guid>.Failure(outcome.ErrorCode ?? "idempotency.outcome_unknown",
                    outcome.ErrorMessage ?? "Operation outcome requires reconciliation before another adjustment can execute.");
            }
            if (await _inventory.GetMovementByCorrelationIdAsync(command.CorrelationId, ct) is not null)
            {
                return Result<Guid>.Failure("idempotency.legacy_adjustment_requires_reconciliation",
                    "An existing adjustment movement has no canonical payload outcome; reconcile it before retrying.");
            }
            ownsOutcome = true;

            if (command.Reason == StockAdjustmentReason.Damaged)
            {
                return Result<Guid>.Failure("inventory.condition_transfer_required",
                    "Use the inventory condition transfer from the current bucket to Damaged; damage preserves recoverable quantity and carrying value.");
            }

            // Validate current masters only for a new operation; committed replay
            // must not depend on whether a product unit was later deactivated.
            var productIds = command.Items.Select(x => x.ProductId).Distinct().OrderBy(x => x).ToArray();
            var products = new Dictionary<Guid, Product>();
            foreach (var pid in productIds)
            {
                await _resourceLock.AcquireAsync("product", pid, ct);
            }
            // Validate items and tracking rules
            var seenSerials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenImeis = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var physicalInputs = new Dictionary<StockAdjustmentItemCommand, IReadOnlyList<SerializedAdjustmentUnitCommand>>();
            var physicalCounts = new Dictionary<StockAdjustmentItemCommand, int>();
            var effectiveItems = new Dictionary<StockAdjustmentItemCommand, EffectiveAdjustmentItem>();
            var runningBalances = new Dictionary<(Guid ProductId, InventoryBucket Bucket), decimal>();
            var balances = new Dictionary<Guid, StockBalance>();

            async Task<Result<Guid>?> ValidateItemsAsync()
            {
                seenSerials.Clear();
                seenImeis.Clear();
                physicalInputs.Clear();
                physicalCounts.Clear();
                effectiveItems.Clear();
                runningBalances.Clear();
                foreach (var item in command.Items)
                {
                    var product = products[item.ProductId];
                    var factor = 1m;
                    if (product.TrackingMode == TrackingMode.Container ||
                        (command.Mode == StockAdjustmentMode.SetPhysicalCount && item.ProductUnitId.HasValue))
                    {
                        if (!item.ProductUnitId.HasValue)
                        {
                            return Result<Guid>.Failure(
                                "catalog.product_unit_required",
                                "Container adjustment requires a valid product unit.");
                        }

                        var productUnit = await _catalog.GetProductUnitSnapshotAsync(item.ProductUnitId.Value, ct);
                        if (productUnit is null || productUnit.ProductId != product.Id || !productUnit.IsActive || productUnit.FactorToBaseUnit <= 0m)
                        {
                            return Result<Guid>.Failure("catalog.product_unit_not_allowed", "Container adjustment requires a valid product unit.");
                        }
                        factor = productUnit.FactorToBaseUnit;
                    }

                    decimal effectiveBaseQuantity;
                    StockAdjustmentDirection effectiveDirection;
                    bool isNoOp = false;

                    if (command.Mode == StockAdjustmentMode.Delta)
                    {
                        if (item.BaseQuantity <= 0)
                        {
                            return Result<Guid>.Failure(
                                "inventory.quantity_positive",
                                "Adjustment quantity must be greater than zero.");
                        }
                        effectiveBaseQuantity = item.BaseQuantity;
                        effectiveDirection = item.Direction;
                    }
                    else // StockAdjustmentMode.SetPhysicalCount
                    {
                        if (item.BaseQuantity < 0)
                        {
                            return Result<Guid>.Failure(
                                "inventory.quantity_negative",
                                "Target quantity cannot be negative.");
                        }

                        if (IsPhysical(product) && !QuantityMath.IsWhole(item.BaseQuantity))
                        {
                            return Result<Guid>.Failure("inventory.physical_count_invalid",
                                "Physical target count must be a whole number before conversion or rounding.");
                        }

                        var bucketKey = (item.ProductId, item.TargetBucket);
                        if (!runningBalances.TryGetValue(bucketKey, out var currentBase))
                        {
                            var currentBalance = balances[product.Id];
                            currentBase = currentBalance?.Get(item.TargetBucket) ?? 0m;
                        }
                        var targetBase = QuantityMath.RoundQuantity(item.BaseQuantity * factor);
                        var deltaBase = targetBase - currentBase;
                        runningBalances[bucketKey] = targetBase;

                        if (deltaBase == 0m)
                        {
                            isNoOp = true;
                            effectiveBaseQuantity = 0m;
                            effectiveDirection = StockAdjustmentDirection.Increase;
                        }
                        else if (deltaBase > 0m)
                        {
                            if (IsPhysical(product))
                            {
                                return Result<Guid>.Failure(
                                    "inventory.physical_positive_adjustment_unsupported",
                                    "Positive physical adjustments are not supported in SetPhysicalCount mode.");
                            }
                            effectiveBaseQuantity = deltaBase;
                            effectiveDirection = StockAdjustmentDirection.Increase;
                        }
                        else // deltaBase < 0m
                        {
                            effectiveBaseQuantity = Math.Abs(deltaBase);
                            effectiveDirection = StockAdjustmentDirection.Decrease;
                        }
                    }

                    effectiveItems[item] = new EffectiveAdjustmentItem(effectiveDirection, effectiveBaseQuantity, isNoOp);

                    if (item.UnitCostSnapshot.HasValue && item.UnitCostSnapshot.Value < 0)
                    {
                        return Result<Guid>.Failure(
                            "inventory.cost_negative",
                            "Unit cost cannot be negative.");
                    }

                    if (!isNoOp && effectiveDirection == StockAdjustmentDirection.Increase)
                    {
                        var costBasis = item.UnitCostSnapshot ?? product.ReferencePurchaseCost;
                        if (costBasis is null)
                        {
                            return Result<Guid>.Failure("inventory.cost_basis_required",
                                "Positive inventory requires an explicit cost or an approved product reference cost; provide zero explicitly for free stock.");
                        }
                        if (costBasis.Value < 0m)
                        {
                            return Result<Guid>.Failure("inventory.cost_negative", "Unit cost cannot be negative.");
                        }
                        if (item.TargetBucket == InventoryBucket.Scrap && costBasis.Value != 0m)
                        {
                            return Result<Guid>.Failure("inventory.scrap_zero_carrying_required",
                                "Scrap opening requires a zero carrying basis; use the canonical disposition workflow for existing costed stock.");
                        }
                    }

                    if (!isNoOp && IsPhysical(product))
                    {
                        if (!QuantityMath.IsWhole(effectiveBaseQuantity))
                        {
                            return Result<Guid>.Failure(
                                "catalog.serialized_whole_quantity",
                                "Serialized adjustment quantity must be a whole number.");
                        }

                        var physicalQuantity = effectiveBaseQuantity /
                            (product.TrackingMode == TrackingMode.Container ? factor : 1m);
                        if (!QuantityMath.IsWhole(physicalQuantity) || physicalQuantity > int.MaxValue)
                        {
                            return Result<Guid>.Failure("inventory.physical_count_invalid", "Physical adjustment must contain a whole number of physical units.");
                        }
                        var count = decimal.ToInt32(physicalQuantity);
                        physicalCounts[item] = count;

                        if (effectiveDirection == StockAdjustmentDirection.Increase)
                        {
                            if (string.IsNullOrWhiteSpace(product.Sku))
                            {
                                return Result<Guid>.Failure(
                                    "catalog.sku_required",
                                    $"Serialized product '{product.Name}' requires a valid SKU.");
                            }

                            if (item.SupplierId is null || item.SupplierId == Guid.Empty)
                            {
                                return Result<Guid>.Failure(
                                    "inventory.serialized_supplier_provenance_required",
                                    "Positive serialized adjustment requires explicit Supplier provenance.");
                            }

                            var inputs = item.SerializedUnits;
                            if ((inputs is null || inputs.Count == 0) && !product.SerialTrackingEnabled && !product.ImeiTrackingEnabled)
                            {
                                inputs = Enumerable.Repeat(new SerializedAdjustmentUnitCommand(null), count).ToArray();
                            }
                            if (inputs is null || inputs.Count != count)
                            {
                                return Result<Guid>.Failure(
                                    "inventory.serialized_units_count_mismatch",
                                    $"Expected {count} serialized unit definitions but received {item.SerializedUnits?.Count ?? 0}.");
                            }
                            physicalInputs[item] = inputs;

                            foreach (var u in inputs)
                            {
                                if (product.SerialTrackingEnabled)
                                {
                                    if (string.IsNullOrWhiteSpace(u.SerialNumber))
                                    {
                                        return Result<Guid>.Failure(
                                            "identity.serial_required",
                                            $"Serial number is required for product '{product.Name}'.");
                                    }

                                    var normSerial = IdentityNormalizationRules.NormalizeSerialNumber(u.SerialNumber);
                                    if (!seenSerials.Add(normSerial))
                                    {
                                        return Result<Guid>.Failure(
                                            "identity.duplicate_serial_in_command",
                                            $"Duplicate serial number '{normSerial}' in adjustment items.");
                                    }
                                }

                                if (product.ImeiTrackingEnabled)
                                {
                                    if (string.IsNullOrWhiteSpace(u.Imei1))
                                    {
                                        return Result<Guid>.Failure(
                                            "identity.imei_required",
                                            $"IMEI1 is required for product '{product.Name}'.");
                                    }

                                    var normImei1 = IdentityNormalizationRules.NormalizeImeiIdentity(u.Imei1);
                                    if (!seenImeis.Add(normImei1))
                                    {
                                        return Result<Guid>.Failure(
                                            "identity.duplicate_imei_in_command",
                                            $"Duplicate IMEI1 '{normImei1}' in adjustment items.");
                                    }

                                    if (!string.IsNullOrWhiteSpace(u.Imei2))
                                    {
                                        var normImei2 = IdentityNormalizationRules.NormalizeImeiIdentity(u.Imei2);
                                        if (!seenImeis.Add(normImei2))
                                        {
                                            return Result<Guid>.Failure(
                                                "identity.duplicate_imei_in_command",
                                                $"Duplicate IMEI2 '{normImei2}' in adjustment items.");
                                        }
                                    }
                                }
                            }
                        }
                        else
                        {
                            // Negative serialized
                            if (item.InventoryUnitIds is null || item.InventoryUnitIds.Count != count)
                            {
                                return Result<Guid>.Failure(
                                    "inventory.serialized_exact_units_required",
                                    $"Negative serialized adjustment of {count} units requires exactly {count} selected InventoryUnit IDs.");
                            }

                            if (item.InventoryUnitIds.Distinct().Count() != count)
                            {
                                return Result<Guid>.Failure(
                                    "inventory.duplicate_unit_in_command",
                                    "Duplicate InventoryUnit ID found in adjustment items.");
                            }
                        }
                    }
                }
                return null;
            }

            // Product logical locks were acquired in sorted order.
            // Include every supplied pair so a policy change that wins the Product
            // row boundary can still be honored without acquiring a late key.
            var supplierProductKeys = command.Items
                .Where(x => x.SupplierId.HasValue)
                .Select(x => $"{x.SupplierId!.Value:D}:{x.ProductId:D}")
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();

            foreach (var spKey in supplierProductKeys)
            {
                await _resourceLock.AcquireAsync("supplier-product", spKey, ct);
            }

            foreach (var unitId in command.Items.SelectMany(x => x.InventoryUnitIds ?? [])
                .Distinct().OrderBy(x => x))
            {
                await _resourceLock.AcquireAsync("inventory-unit", unitId, ct);
            }

            // Prelock supplied normalized identities even when discovery policy
            // does not consume them. Definitive validation runs under row locks.
            var identityKeys = command.Items.SelectMany(x => x.SerializedUnits ?? [])
                .SelectMany(x => new[]
                {
                    IdentityNormalizationRules.NormalizeOptionalSerialNumber(x.SerialNumber) is string serial ? $"SERIAL:{serial}" : null,
                    IdentityNormalizationRules.NormalizeOptionalImei(x.Imei1) is string imei1 ? $"IMEI:{imei1}" : null,
                    IdentityNormalizationRules.NormalizeOptionalImei(x.Imei2) is string imei2 ? $"IMEI:{imei2}" : null
                })
                .Where(x => x is not null).Select(x => x!)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();

            foreach (var idKey in identityKeys)
            {
                await _resourceLock.AcquireAsync("inventory-identity", idKey, ct);
            }

            foreach (var pid in productIds)
            {
                var product = await _catalog.GetProductForUpdateAsync(pid, ct);
                if (product is null)
                {
                    return Result<Guid>.Failure("catalog.product_not_found", $"Product {pid} was not found.");
                }
                products[pid] = product;
            }
            foreach (var pid in productIds)
            {
                var balance = await _inventory.GetStockBalanceForUpdateAsync(pid, ct);
                if (balance is null)
                {
                    balance = new StockBalance { ProductId = pid, Version = 1 };
                    _inventory.AddStockBalance(balance);
                }
                balances[pid] = balance;
            }
            var validation = await ValidateItemsAsync();
            if (validation is { } validationFailure)
            {
                return validationFailure;
            }
            if (seenSerials.Select(x => $"SERIAL:{x}").Concat(seenImeis.Select(x => $"IMEI:{x}"))
                .Any(x => !identityKeys.Contains(x, StringComparer.Ordinal)))
            {
                return Result<Guid>.Failure("inventory.physical_unit_policy_changed_retry",
                    "Product tracking policy changed; retry the adjustment before allocating physical units.");
            }

            var now = _clock.UtcNow;

            // Check if identity already exists in database history
            foreach (var item in command.Items.Where(x => effectiveItems[x].Direction == StockAdjustmentDirection.Increase && !effectiveItems[x].IsNoOp && IsPhysical(products[x.ProductId])))
            {
                var p = products[item.ProductId];
                foreach (var u in physicalInputs[item])
                {
                    var normSerial = p.SerialTrackingEnabled && !string.IsNullOrWhiteSpace(u.SerialNumber)
                        ? IdentityNormalizationRules.NormalizeSerialNumber(u.SerialNumber)
                        : null;
                    var normImei1 = p.ImeiTrackingEnabled && !string.IsNullOrWhiteSpace(u.Imei1)
                        ? IdentityNormalizationRules.NormalizeImeiIdentity(u.Imei1)
                        : null;
                    var normImei2 = p.ImeiTrackingEnabled && !string.IsNullOrWhiteSpace(u.Imei2)
                        ? IdentityNormalizationRules.NormalizeImeiIdentity(u.Imei2)
                        : null;

                    if (await _inventory.InventoryIdentityExistsAsync(normSerial, normImei1, normImei2, ct))
                    {
                        return Result<Guid>.Failure(
                            "identity.already_exists",
                            "A specified Serial or IMEI already exists in inventory history.");
                    }
                }
            }

            var adjustmentId = Guid.CreateVersion7();
            var adjustmentNumber = $"ADJ-{now:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..12].ToUpperInvariant()}";

            var adjustment = new StockAdjustment
            {
                Id = adjustmentId,
                AdjustmentNumber = adjustmentNumber,
                Mode = command.Mode,
                Reason = command.Reason,
                Status = StockAdjustmentStatus.Posted,
                ActorId = command.ActorId,
                OccurredAt = now,
                CorrelationId = command.CorrelationId,
                Note = command.Note,
                CreatedAt = now,
                Version = 1
            };
            _inventory.AddStockAdjustment(adjustment);

            // Pre-load ProductCostState for all products in sorted order.
            // Stock balances already provide the definitive count basis above.
            var costStates = new Dictionary<Guid, ProductCostState>();

            foreach (var pid in productIds)
            {
                if (await _inventory.IsProductBlockedByCountingStocktakeAsync(pid, ct))
                {
                    return Result<Guid>.Failure(
                        "inventory.stocktake_in_progress",
                        $"Product '{products[pid].Name}' is locked by an active stocktake.");
                }

                var cs = await _inventory.GetCostStateForUpdateAsync(pid, ct);
                if (cs is null)
                {
                    cs = new ProductCostState { ProductId = pid, Version = 1 };
                    _inventory.AddCostState(cs);
                }
                costStates[pid] = cs;
            }

            // Process items
            foreach (var itemCommand in command.Items)
            {
                var product = products[itemCommand.ProductId];
                var balance = balances[itemCommand.ProductId];
                var costState = costStates[itemCommand.ProductId];
                var effective = effectiveItems[itemCommand];

                if (effective.IsNoOp)
                {
                    // The committed header/outcome records the no-op; detail rows
                    // require a positive quantity under the existing schema.
                    continue;
                }

                var effectiveDirection = effective.Direction;
                var effectiveBaseQuantity = effective.BaseQuantity;

                // Determine unit cost
                decimal unitCost;
                if (itemCommand.UnitCostSnapshot.HasValue)
                {
                    unitCost = itemCommand.UnitCostSnapshot.Value;
                }
                else if (effectiveDirection == StockAdjustmentDirection.Increase && product.ReferencePurchaseCost.HasValue)
                {
                    unitCost = product.ReferencePurchaseCost.Value;
                }
                else
                {
                    unitCost = effectiveDirection == StockAdjustmentDirection.Decrease
                        ? costState.MovingAverageCost
                        : 0m;
                }

                var adjustmentItemId = Guid.CreateVersion7();
                var adjustmentItem = new StockAdjustmentItem
                {
                    Id = adjustmentItemId,
                    StockAdjustmentId = adjustment.Id,
                    ProductId = itemCommand.ProductId,
                    ProductUnitId = itemCommand.ProductUnitId,
                    Direction = effectiveDirection,
                    TargetBucket = itemCommand.TargetBucket,
                    BaseQuantity = effectiveBaseQuantity,
                    UnitCostSnapshot = unitCost,
                    TotalCostSnapshot = decimal.Round(unitCost * effectiveBaseQuantity, 6, MidpointRounding.AwayFromZero),
                    SupplierId = itemCommand.SupplierId,
                    ReasonDetails = itemCommand.ReasonDetails
                };
                _inventory.AddStockAdjustmentItem(adjustmentItem);

                if (effectiveDirection == StockAdjustmentDirection.Decrease && IsPhysical(product))
                {
                    if (balance.Get(itemCommand.TargetBucket) < effectiveBaseQuantity)
                        return Result<Guid>.Failure("inventory.insufficient_stock", "Selected source bucket has insufficient stock.");
                    var count = physicalCounts[itemCommand];
                    var units = await _inventory.GetInventoryUnitsForUpdateAsync(product.Id, itemCommand.InventoryUnitIds!, ct);
                    if (units.Count != count)
                        return Result<Guid>.Failure("inventory.serialized_units_not_found", "One or more selected physical units were not found.");
                    var quantities = new Dictionary<Guid, decimal>();
                    foreach (var unit in units)
                    {
                        var rule = InventoryUnitAccountingPolicy.GetRule(unit.Status);
                        if (!rule.ContributesToStockBalance || rule.AuthoritativeBucket is null)
                            return Result<Guid>.Failure("inventory.unit_not_in_stock", "Selected unit is not in an active stock state.");
                        if (rule.AuthoritativeBucket != itemCommand.TargetBucket)
                            return Result<Guid>.Failure("inventory.unit_bucket_mismatch", "Selected unit does not belong to the source bucket.");
                        var quantity = await _inventory.GetPhysicalUnitBaseQuantitySnapshotAsync(unit, ct);
                        if (quantity != effectiveBaseQuantity / count)
                            return Result<Guid>.Failure("inventory.physical_quantity_mismatch", "Adjustment must remove the original whole physical quantity.");
                        quantities[unit.Id] = quantity;
                    }
                    if (command.Reason is StockAdjustmentReason.Damaged or StockAdjustmentReason.OpeningStock)
                        return Result<Guid>.Failure("inventory.exact_disposition_required", "Exact-unit shortage requires explicit Lost authority; retained Scrap uses the condition workflow.");
                    try
                    {
                        adjustmentItem.TotalCostSnapshot = await ExactMissingSourcePosting.PostAsync(
                            _inventory, _costAllocator, product.Id, units, quantities, balance,
                            itemCommand.TargetBucket, InventoryMovementType.StockAdjustment,
                            "STOCK_ADJUSTMENT", adjustmentItem.Id, command.ActorId, command.CorrelationId,
                            now, command.Reason.ToString(), itemCommand.ReasonDetails, ct);
                    }
                    catch (BusinessRuleException ex)
                    {
                        return Result<Guid>.Failure(ex.Code, ex.Message);
                    }
                    adjustmentItem.UnitCostSnapshot = decimal.Round(adjustmentItem.TotalCostSnapshot!.Value / effectiveBaseQuantity, 6, MidpointRounding.AwayFromZero);
                    continue;
                }

                var movement = new InventoryMovement
                {
                    ProductId = itemCommand.ProductId,
                    MovementType = command.Reason == StockAdjustmentReason.OpeningStock
                        ? InventoryMovementType.OpeningStock
                        : InventoryMovementType.StockAdjustment,
                    ReferenceType = "STOCK_ADJUSTMENT",
                    ReferenceId = adjustmentItem.Id,
                    UnitCostSnapshot = unitCost,
                    ActorId = command.ActorId,
                    OccurredAt = now,
                    CorrelationId = command.CorrelationId,
                    Reason = command.Reason.ToString(),
                    Note = itemCommand.ReasonDetails
                };
                _inventory.AddMovement(movement);

                if (effectiveDirection == StockAdjustmentDirection.Increase)
                {
                    // Positive adjustment
                    Guid lotId;
                    if (itemCommand.TargetBucket == InventoryBucket.Scrap && unitCost == 0m)
                    {
                        lotId = await _costAllocator.AddZeroCarryingLotAsync(
                            product.Id,
                            effectiveBaseQuantity,
                            unitCost,
                            movement.Id,
                            null,
                            itemCommand.TargetBucket,
                            ct);
                    }
                    else
                    {
                        lotId = await _costAllocator.AddCarryingValueAndLotWithIdAsync(
                            product.Id,
                            effectiveBaseQuantity,
                            unitCost,
                            movement.Id,
                            null,
                            itemCommand.TargetBucket,
                            ct);
                    }

                    if (IsPhysical(product))
                    {
                        if (_physicalUnits is null)
                        {
                            return Result<Guid>.Failure(
                                "inventory.physical_unit_authority_unavailable",
                                "Physical-unit creation authority is unavailable.");
                        }

                        var targetStatus = itemCommand.TargetBucket switch
                        {
                            InventoryBucket.Sellable => InventoryUnitStatus.InStock,
                            InventoryBucket.Damaged => InventoryUnitStatus.Damaged,
                            InventoryBucket.Defective => InventoryUnitStatus.Defective,
                            InventoryBucket.WithSupplier => InventoryUnitStatus.WithSupplier,
                            _ => InventoryUnitStatus.Scrapped
                        };
                        var creation = await _physicalUnits.CreateAsync(
                            itemCommand.SupplierId!.Value,
                            itemCommand.ProductId,
                            physicalInputs[itemCommand].Select(unit => new PhysicalUnitCreationEntry(
                                unit.SerialNumber, unit.Imei1, unit.Imei2,
                                targetStatus, unitCost * effectiveBaseQuantity / physicalCounts[itemCommand], lotId,
                                InventoryUnitOriginType.StockAdjustment,
                                SourceStockAdjustmentItemId: adjustmentItem.Id)).ToArray(),
                            ct);
                        if (!creation.IsSuccess || creation.Value is null)
                        {
                            return Result<Guid>.Failure(creation.Error!.Code, creation.Error.Message);
                        }
                        adjustmentItem.SupplierProductId = creation.Value[0].SupplierProductId;
                        foreach (var unit in creation.Value)
                        {
                            _inventory.AddMovementUnit(new InventoryMovementUnit
                            {
                                MovementId = movement.Id,
                                InventoryUnitId = unit.Id,
                                FromStatus = null,
                                ToStatus = targetStatus
                            });
                        }
                    }
                    var beforeQty = balance.Get(itemCommand.TargetBucket);
                    balance.ApplyDelta(itemCommand.TargetBucket, effectiveBaseQuantity);
                    var afterQty = balance.Get(itemCommand.TargetBucket);

                    _inventory.AddMovementEffect(new InventoryMovementEffect
                    {
                        MovementId = movement.Id,
                        StockBucket = itemCommand.TargetBucket,
                        QuantityDelta = effectiveBaseQuantity,
                        QuantityBefore = beforeQty,
                        QuantityAfter = afterQty
                    });
                }
                else
                {
                    // Negative adjustment (Decrease)
                    decimal removedValue = 0m;
                    var available = balance.Get(itemCommand.TargetBucket);
                    if (available < effectiveBaseQuantity)
                    {
                        return Result<Guid>.Failure(
                            "inventory.insufficient_stock",
                            $"Insufficient stock in bucket '{itemCommand.TargetBucket}'. Available: {available}, Requested: {effectiveBaseQuantity}.");
                    }

                    await _costAllocator.ConsumeBucketAsync(
                        product.Id, itemCommand.TargetBucket, effectiveBaseQuantity,
                        movement.Id, costState.MovingAverageCost, ct);
                    removedValue = await _costAllocator.RemoveCarryingValueAsync(
                        product.Id, effectiveBaseQuantity, null, ct);
                    // A destructive correction recognizes the actual derecognized
                    // carrying value in either mode. Caller cost hints cannot
                    // replace the allocator's authoritative removal result.
                    movement.RecognizedLossAmount = removedValue;
                    adjustmentItem.TotalCostSnapshot = removedValue;
                    adjustmentItem.UnitCostSnapshot = decimal.Round(
                        removedValue / effectiveBaseQuantity, 6, MidpointRounding.AwayFromZero);
                    movement.UnitCostSnapshot = adjustmentItem.UnitCostSnapshot;

                    var beforeQty = balance.Get(itemCommand.TargetBucket);
                    balance.ApplyDelta(itemCommand.TargetBucket, -effectiveBaseQuantity);
                    var afterQty = balance.Get(itemCommand.TargetBucket);

                    _inventory.AddMovementEffect(new InventoryMovementEffect
                    {
                        MovementId = movement.Id,
                        StockBucket = itemCommand.TargetBucket,
                        QuantityDelta = -effectiveBaseQuantity,
                        QuantityBefore = beforeQty,
                        QuantityAfter = afterQty
                    });
                }
            }

            _audit.Record(
                "STOCK_ADJUSTMENT_POSTED",
                "STOCK_ADJUSTMENT",
                adjustment.Id,
                command.ActorId,
                command.CorrelationId,
                adjustment.AdjustmentNumber);

            if (_outcomeLedger is not null)
            {
                await _outcomeLedger.RecordSuccessAsync(
                    command.CorrelationId,
                    "StockAdjustment",
                    adjustment.Id,
                    adjustment.AdjustmentNumber,
                    actorId: command.ActorId,
                    payloadFingerprint: fingerprint,
                    cancellationToken: ct);
            }

            await _unitOfWork.SaveChangesAsync(ct);
            return Result<Guid>.Success(adjustment.Id);
        }, cancellationToken);

        if (!result.IsSuccess && ownsOutcome)
        {
            await _outcomeLedger.RecordFailureAsync(
                command.CorrelationId,
                "StockAdjustment",
                result.Error?.Code ?? "inventory.adjustment_failed",
                result.Error?.Message ?? "Stock adjustment failed.",
                actorId: command.ActorId,
                payloadFingerprint: fingerprint,
                cancellationToken: cancellationToken);
        }

        return result;
    }

    private static bool IsPhysical(Product product) =>
        product.TrackingMode is TrackingMode.Serialized or TrackingMode.IndividualPiece or TrackingMode.Container;
}

// Both approved shortage writers share the same per-identity economic source.
// The caller owns authorization, locks, transaction, parent outcome and save.
internal static class ExactMissingSourcePosting
{
    public static async Task<decimal> PostAsync(
        IInventoryRepository inventory, IInventoryCostAllocator allocator, Guid productId,
        IReadOnlyList<InventoryUnit> units, IReadOnlyDictionary<Guid, decimal> baseQuantities,
        StockBalance balance, InventoryBucket sourceBucket, InventoryMovementType movementType,
        string referenceType, Guid referenceId, Guid actorId, Guid correlationId,
        DateTimeOffset occurredAt, string reason, string? note, CancellationToken ct)
    {
        if (units.Count == 0 || units.Select(x => x.Id).Distinct().Count() != units.Count)
            throw new BusinessRuleException("inventory.missing_source_invalid", "Missing posting requires distinct existing identities.");
        var state = await inventory.GetCostStateForUpdateAsync(productId, ct)
            ?? throw new BusinessRuleException("inventory.cost_state_missing", "Inventory carrying state is unavailable.");
        var sources = new List<(InventoryMovement Movement, decimal Value)>();
        foreach (var unit in units.OrderBy(x => x.Id))
        {
            var rule = InventoryUnitAccountingPolicy.GetRule(unit.Status);
            if (unit.ProductId != productId || !rule.ContributesToProductCostState ||
                rule.AuthoritativeBucket != sourceBucket || unit.InventoryLotId is null ||
                !baseQuantities.TryGetValue(unit.Id, out var quantity) || quantity <= 0m)
                throw new BusinessRuleException("inventory.missing_source_invalid", "Selected identity lacks active carrying and whole-unit provenance.");
            var lotBalance = await inventory.GetLotBucketBalanceForUpdateAsync(unit.InventoryLotId.Value, sourceBucket, ct);
            if (lotBalance is null || lotBalance.Quantity < quantity || balance.Get(sourceBucket) < quantity)
                throw new BusinessRuleException("inventory.exact_unit_lot_insufficient", "Selected physical source lot quantity is unavailable.");

            var beforeValue = state.TotalInventoryCost;
            var beforeCostedQuantity = state.CostedQty;
            var carryingValue = await inventory.GetPhysicalUnitCarryingValueSnapshotAsync(unit, ct);
            var reportedValue = await allocator.RemoveCarryingValueAsync(productId, quantity, carryingValue / quantity, ct);
            var actualValue = decimal.Round(beforeValue - state.TotalInventoryCost, 6, MidpointRounding.AwayFromZero);
            if (actualValue < 0m || beforeCostedQuantity - state.CostedQty != quantity || reportedValue != actualValue)
                throw new BusinessRuleException("inventory.missing_source_value_invalid", "Actual carrying removal cannot be attributed conclusively to this identity.");

            var movement = new InventoryMovement
            {
                ProductId = productId, MovementType = movementType, ReferenceType = referenceType,
                ReferenceId = referenceId, ActorId = actorId, CorrelationId = correlationId,
                OccurredAt = occurredAt, Reason = reason, Note = note,
                UnitCostSnapshot = decimal.Round(actualValue / quantity, 6, MidpointRounding.AwayFromZero)
            };
            inventory.AddMovement(movement);
            inventory.AddLotConsumption(new InventoryLotConsumption
            {
                LotId = unit.InventoryLotId.Value, MovementId = movement.Id, Quantity = quantity,
                UnitCostSnapshot = movement.UnitCostSnapshot.Value, TotalCostSnapshot = actualValue, OccurredAt = occurredAt
            });
            lotBalance.Quantity = QuantityMath.RoundQuantity(lotBalance.Quantity - quantity);
            var beforeQuantity = balance.Get(sourceBucket);
            balance.ApplyDelta(sourceBucket, -quantity);
            inventory.AddMovementEffect(new InventoryMovementEffect
            {
                MovementId = movement.Id, StockBucket = sourceBucket, QuantityDelta = -quantity,
                QuantityBefore = beforeQuantity, QuantityAfter = balance.Get(sourceBucket)
            });
            inventory.AddMovementUnit(new InventoryMovementUnit
            {
                MovementId = movement.Id, InventoryUnitId = unit.Id, FromStatus = unit.Status,
                ToStatus = InventoryUnitStatus.Missing
            });
            unit.Status = InventoryUnitStatus.Missing;
            unit.Version++;
            sources.Add((movement, actualValue));
        }
        var removedValue = sources.Sum(x => x.Value);
        var allocations = MoneyRoundingPolicy.Allocate(MoneyRoundingPolicy.Round(removedValue), sources.Select(x => x.Value).ToArray());
        for (var i = 0; i < sources.Count; i++)
            sources[i].Movement.RecognizedLossAmount = allocations[i];
        return removedValue;
    }
}
