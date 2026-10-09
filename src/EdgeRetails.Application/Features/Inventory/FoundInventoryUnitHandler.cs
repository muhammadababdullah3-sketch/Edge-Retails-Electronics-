using System.Text.Json;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Warranty;

namespace EdgeRetails.Application.Features.Inventory;

public sealed record FoundInventoryUnitCommand(Guid ProductId, Guid InventoryUnitId,
    Guid SourceMissingMovementId, InventoryBucket TargetCondition, string Reason,
    Guid ActorId, Guid ClientOperationId);

public sealed record FoundInventoryUnitResult(Guid MovementId, decimal RestoredInventoryValue,
    decimal InventoryLossRecoveryGain, bool WasExisting);

public sealed record MissingRecoverySource(Guid SourceMovementId, Guid UnitId, Guid LotId,
    decimal BaseQuantity, decimal RemovedValue, decimal RecognizedLoss, Guid ProductId);

public sealed class FoundInventoryUnitHandler(
    ICatalogRepository catalog, IInventoryRepository inventory, IWarrantyRepository warranty,
    IInventoryCostAllocator costs, IApplicationPermissionAuthorizer authorization,
    IOperationLock operationLock, IResourceLock resourceLock, IOperationOutcomeLedger outcomes,
    IBusinessAuditWriter audit, IClock clock, ITransactionRunner transactions, IUnitOfWork unitOfWork)
{
    public const string RecoveryReferenceType = "InventoryLossRecoveryGain";
    private const string OperationType = "FoundInventoryUnit";

    public Task<Result<FoundInventoryUnitResult>> HandleAsync(FoundInventoryUnitCommand command, CancellationToken ct)
    {
        if (command.ProductId == Guid.Empty || command.InventoryUnitId == Guid.Empty ||
            command.SourceMissingMovementId == Guid.Empty || command.ActorId == Guid.Empty ||
            command.ClientOperationId == Guid.Empty || string.IsNullOrWhiteSpace(command.Reason) ||
            command.Reason.Trim().Length > 500 || !FoundRecoveryAuthority.IsRecoveryBucket(command.TargetCondition))
        {
            return Task.FromResult(Result<FoundInventoryUnitResult>.Failure("inventory.found_invalid",
                "Found requires an existing identity, explicit Missing source, eligible condition, actor, operation and reason."));
        }
        var fingerprint = OperationPayloadFingerprint.ComputeSha256(JsonSerializer.Serialize(command with { Reason = command.Reason.Trim() }));
        return transactions.ExecuteAsync(async token =>
        {
            var allowed = await authorization.AuthorizeAsync(command.ActorId, PermissionKeys.InventoryManage, token);
            if (!allowed.IsSuccess)
                return Result<FoundInventoryUnitResult>.Failure(allowed.Error!.Code, allowed.Error.Message);
            await operationLock.AcquireAsync(command.ClientOperationId, token);
            await resourceLock.AcquireAsync("product", command.ProductId, token);
            await resourceLock.AcquireAsync("inventory-missing-source", command.SourceMissingMovementId, token);
            try
            {
                var prior = await outcomes.GetOutcomeAsync(command.ClientOperationId, token);
                if (prior is not null)
                {
                    if (prior.OperationType != OperationType || prior.ActorId != command.ActorId || prior.PayloadFingerprint != fingerprint)
                        return Result<FoundInventoryUnitResult>.Failure("idempotency.payload_mismatch", "Found operation has different authority or parameters.");
                    if (prior.State != OperationOutcomeState.Succeeded || !prior.WasCommitted || prior.EntityId is not Guid foundId)
                        return Result<FoundInventoryUnitResult>.Failure("inventory.found_replay_requires_review", "Found operation has no proven committed result.");
                    var replayUnit = (await inventory.GetInventoryUnitsForUpdateAsync(command.ProductId, [command.InventoryUnitId], token)).SingleOrDefault();
                    var replaySource = await inventory.GetInventoryMovementEvidenceAsync(command.SourceMissingMovementId, token);
                    var replayFound = await inventory.GetInventoryMovementEvidenceAsync(foundId, token);
                    if (replayUnit is null || replaySource is null || replayFound is null)
                        throw FoundRecoveryAuthority.Review("Committed recovery source or identity is unavailable.");
                    var source = await FoundRecoveryAuthority.ValidateSourceAsync(inventory, replayUnit, replaySource, token);
                    FoundRecoveryAuthority.ValidateFound(replayFound, source);
                    var replayReferences = await inventory.GetMovementsByReferenceAsync(RecoveryReferenceType, source.SourceMovementId, token);
                    if (replayReferences.Count != 1 || replayReferences[0].Movement.Id != foundId ||
                        replayFound.Movement.CorrelationId != command.ClientOperationId || replayFound.Movement.ActorId != command.ActorId ||
                        replayFound.Effects[0].StockBucket != command.TargetCondition || replayFound.Movement.Reason != command.Reason.Trim())
                        throw FoundRecoveryAuthority.Review("Committed recovery does not match its operation authority.");
                    return Result<FoundInventoryUnitResult>.Success(new(foundId, source.RemovedValue, source.RecognizedLoss, true));
                }
                var product = await catalog.GetProductForUpdateAsync(command.ProductId, token);
                if (product is null)
                    return Result<FoundInventoryUnitResult>.Failure("catalog.product_not_found", "Product was not found.");
                if (product.TrackingMode is not (TrackingMode.Serialized or TrackingMode.IndividualPiece or TrackingMode.Container))
                    return Result<FoundInventoryUnitResult>.Failure("inventory.found_exact_identity_required", "Found operates on an existing physical identity.");
                if (await inventory.IsProductBlockedByCountingStocktakeAsync(command.ProductId, token))
                    return Result<FoundInventoryUnitResult>.Failure("inventory.stocktake_in_progress", "Finish the active stocktake before recovery.");
                var balance = await inventory.GetStockBalanceForUpdateAsync(product.Id, token)
                    ?? throw FoundRecoveryAuthority.Review("Stock balance is unavailable.");
                var state = await inventory.GetCostStateForUpdateAsync(product.Id, token)
                    ?? throw FoundRecoveryAuthority.Review("Carrying state is unavailable.");
                var unit = (await inventory.GetInventoryUnitsForUpdateAsync(command.ProductId, [command.InventoryUnitId], token)).SingleOrDefault();
                if (unit is null)
                    return Result<FoundInventoryUnitResult>.Failure("inventory.unit_not_found", "Physical identity was not found.");
                if (unit.Status != InventoryUnitStatus.Missing)
                    return Result<FoundInventoryUnitResult>.Failure("inventory.found_unit_not_missing", "Only a currently Missing identity may be recovered.");
                if (await warranty.HasActiveClaimForUnitAsync(unit.Id, token) || await warranty.IsUnitTerminallyResolvedAsync(unit.Id, token))
                    return Result<FoundInventoryUnitResult>.Failure("inventory.found_condition_unresolved", "Warranty or terminal custody requires review before recovery.");
                if (command.TargetCondition == InventoryBucket.Sellable && !product.IsActive)
                    return Result<FoundInventoryUnitResult>.Failure("inventory.found_condition_unresolved", "An inactive product cannot be restored as Sellable.");

                var history = await inventory.GetUnitMovementEvidenceAsync(unit.Id, token);
                foreach (var caseId in history.Where(x => x.Movement.ReferenceType == "SHOP_WARRANTY").Select(x => x.Movement.ReferenceId).Distinct())
                {
                    var warrantyCase = caseId.HasValue ? await warranty.GetShopStockCaseAsync(caseId.Value, token) : null;
                    if (warrantyCase is null || warrantyCase.ProductId != unit.ProductId || warrantyCase.Status != ShopWarrantyCaseStatus.Closed)
                        return Result<FoundInventoryUnitResult>.Failure("inventory.found_condition_unresolved", "Resolve the linked shop warranty custody before recovery.");
                }
                var missing = history.Where(x => x.Units.Any(link => link.InventoryUnitId == unit.Id && link.ToStatus == InventoryUnitStatus.Missing)).ToArray();
                if (missing.Length == 0)
                    throw FoundRecoveryAuthority.Review("Missing identity has no conclusive economic episode.");
                var outstanding = new List<MissingRecoverySource>();
                var validatedRecoveries = new HashSet<Guid>();
                foreach (var episode in missing)
                {
                    var source = await FoundRecoveryAuthority.ValidateSourceAsync(inventory, unit, episode, token);
                    var recoveries = await inventory.GetMovementsByReferenceAsync(RecoveryReferenceType, source.SourceMovementId, token);
                    if (recoveries.Count == 0)
                        outstanding.Add(source);
                    else if (recoveries.Count == 1)
                    {
                        FoundRecoveryAuthority.ValidateFound(recoveries[0], source);
                        validatedRecoveries.Add(recoveries[0].Movement.Id);
                    }
                    else
                        throw FoundRecoveryAuthority.Review("A source has duplicate recovery references.");
                }
                if (history.Any(x => x.Units.Any(link => link.InventoryUnitId == unit.Id && link.FromStatus == InventoryUnitStatus.Missing) &&
                                     !validatedRecoveries.Contains(x.Movement.Id)))
                    throw FoundRecoveryAuthority.Review("Missing history contains an unsupported restoration.");
                if (outstanding.Count != 1 || outstanding[0].SourceMovementId != command.SourceMissingMovementId)
                    throw FoundRecoveryAuthority.Review("Select the one latest unrecovered approved Missing episode.");
                var selected = outstanding[0];
                if (selected.LotId != unit.InventoryLotId ||
                    await inventory.GetPhysicalUnitBaseQuantitySnapshotAsync(unit, token) != selected.BaseQuantity)
                    throw FoundRecoveryAuthority.Review("Source lot or whole physical quantity does not match the Missing identity.");
                var beforeValue = state.TotalInventoryCost;
                var beforeCosted = state.CostedQty;
                var now = clock.UtcNow;
                var movement = new InventoryMovement
                {
                    ProductId = product.Id, MovementType = InventoryMovementType.StockAdjustment,
                    ReferenceType = RecoveryReferenceType, ReferenceId = selected.SourceMovementId,
                    ActorId = command.ActorId, CorrelationId = command.ClientOperationId,
                    OccurredAt = now, Reason = command.Reason.Trim(), RecognizedLossAmount = 0m,
                    UnitCostSnapshot = decimal.Round(selected.RemovedValue / selected.BaseQuantity, 6, MidpointRounding.AwayFromZero)
                };
                inventory.AddMovement(movement);
                var lotId = await costs.AddCarryingValueAndLotWithIdAsync(product.Id, selected.BaseQuantity,
                    selected.RemovedValue / selected.BaseQuantity, movement.Id, unit.SourcePurchaseItemId, command.TargetCondition, token);
                var represented = decimal.Round(selected.BaseQuantity * movement.UnitCostSnapshot!.Value, 6, MidpointRounding.AwayFromZero);
                if (state.TotalInventoryCost - beforeValue != represented)
                    throw FoundRecoveryAuthority.Review("Canonical lot creation did not produce its represented carrying value.");
                // A rounded per-base lot cost cannot represent every whole pack.
                // The immutable source consumption total owns the exact residual.
                state.TotalInventoryCost = decimal.Round(beforeValue + selected.RemovedValue, 6, MidpointRounding.AwayFromZero);
                state.MovingAverageCost = decimal.Round(state.TotalInventoryCost / state.CostedQty, 6, MidpointRounding.AwayFromZero);
                if (state.CostedQty - beforeCosted != selected.BaseQuantity || state.TotalInventoryCost - beforeValue != selected.RemovedValue)
                    throw FoundRecoveryAuthority.Review("Actual carrying restoration does not reconcile to its source.");
                var beforeQuantity = balance.Get(command.TargetCondition);
                balance.ApplyDelta(command.TargetCondition, selected.BaseQuantity);
                inventory.AddMovementEffect(new InventoryMovementEffect
                {
                    MovementId = movement.Id, StockBucket = command.TargetCondition, QuantityDelta = selected.BaseQuantity,
                    QuantityBefore = beforeQuantity, QuantityAfter = balance.Get(command.TargetCondition)
                });
                var status = FoundRecoveryAuthority.StatusFor(command.TargetCondition);
                inventory.AddMovementUnit(new InventoryMovementUnit
                {
                    MovementId = movement.Id, InventoryUnitId = unit.Id,
                    FromStatus = InventoryUnitStatus.Missing, ToStatus = status
                });
                unit.Status = status; unit.InventoryLotId = lotId; unit.Version++;
                audit.Record("INVENTORY_UNIT_FOUND", RecoveryReferenceType, movement.Id, command.ActorId,
                    command.ClientOperationId, $"Source={selected.SourceMovementId:D};Unit={unit.Id:D};Reason={command.Reason.Trim()}");
                await outcomes.RecordSuccessAsync(command.ClientOperationId, OperationType, movement.Id, unit.TrackingCode,
                    actorId: command.ActorId, payloadFingerprint: fingerprint, cancellationToken: token);
                await unitOfWork.SaveChangesAsync(token);
                return Result<FoundInventoryUnitResult>.Success(new(movement.Id, selected.RemovedValue, selected.RecognizedLoss, false));
            }
            catch (BusinessRuleException ex)
            {
                return Result<FoundInventoryUnitResult>.Failure(ex.Code, ex.Message);
            }
        }, ct);
    }
}

// Validation is shared by Found and the current recovered-lot readers. It does
// not authorize another recovery or depend on mutable current status/cost.
public static class FoundRecoveryAuthority
{
    public static BusinessRuleException Review(string reason) => new("inventory.found_source_requires_review", reason);
    public static bool IsRecoveryBucket(InventoryBucket bucket) => bucket is InventoryBucket.Sellable or InventoryBucket.Damaged or InventoryBucket.Defective;
    public static InventoryUnitStatus StatusFor(InventoryBucket bucket) => bucket switch
    {
        InventoryBucket.Sellable => InventoryUnitStatus.InStock,
        InventoryBucket.Damaged => InventoryUnitStatus.Damaged,
        InventoryBucket.Defective => InventoryUnitStatus.Defective,
        _ => throw Review("Recovery condition is not eligible.")
    };

    public static MissingRecoverySource ReadShape(InventoryMovementEvidence evidence, Guid productId, Guid? expectedUnitId = null)
    {
        var movement = evidence.Movement;
        if (movement.ProductId != productId || movement.ActorId == Guid.Empty || movement.CorrelationId == Guid.Empty ||
            movement.ReferenceId is null || movement.OccurredAt == default || evidence.Units.Count != 1 || evidence.Effects.Count != 1 ||
            evidence.Consumptions.Count != 1 || movement.RecognizedLossAmount < 0m)
            throw Review("Missing source lacks conclusive per-identity economics.");
        var link = evidence.Units[0]; var effect = evidence.Effects[0]; var consumed = evidence.Consumptions[0];
        if (link.ToStatus != InventoryUnitStatus.Missing || link.FromStatus is not InventoryUnitStatus from ||
            (expectedUnitId.HasValue && link.InventoryUnitId != expectedUnitId) || effect.QuantityDelta >= 0m ||
            effect.QuantityBefore + effect.QuantityDelta != effect.QuantityAfter || effect.QuantityAfter < 0m ||
            consumed.Quantity != -effect.QuantityDelta || consumed.TotalCostSnapshot < 0m || consumed.UnitCostSnapshot < 0m ||
            link.MovementId != movement.Id || effect.MovementId != movement.Id || consumed.MovementId != movement.Id)
            throw Review("Missing source identity, quantity or consumption is ambiguous.");
        var cost = decimal.Round(consumed.TotalCostSnapshot / consumed.Quantity, 6, MidpointRounding.AwayFromZero);
        if (movement.UnitCostSnapshot != cost || consumed.UnitCostSnapshot != cost)
            throw Review("Missing source cost snapshots do not reconcile to its actual total.");
        var rule = InventoryUnitAccountingPolicy.GetRule(from);
        if (!rule.ContributesToProductCostState || rule.AuthoritativeBucket != effect.StockBucket)
            throw Review("Source state was not an eligible carrying bucket.");
        return new(movement.Id, link.InventoryUnitId, consumed.LotId, consumed.Quantity, consumed.TotalCostSnapshot, movement.RecognizedLossAmount, productId);
    }

    public static async Task<MissingRecoverySource> ValidateSourceAsync(IInventoryRepository inventory, InventoryUnit unit,
        InventoryMovementEvidence evidence, CancellationToken ct)
    {
        var selected = ReadShape(evidence, unit.ProductId, unit.Id);
        var movement = evidence.Movement;
        var parentSources = await inventory.GetMovementsByReferenceAsync(movement.ReferenceType, movement.ReferenceId!.Value, ct);
        if (movement.ReferenceType == "STOCK_ADJUSTMENT" && parentSources.Any(x => x.Movement.ProductId != unit.ProductId))
            throw Review("An adjustment item has conflicting product sources.");
        var siblings = parentSources.Where(x => x.Movement.ProductId == unit.ProductId).ToArray();
        if (siblings.Length == 0 || !siblings.Any(x => x.Movement.Id == movement.Id))
            throw Review("Missing parent has no authoritative source set.");
        var sources = siblings.Select(x => ReadShape(x, unit.ProductId)).OrderBy(x => x.UnitId).ToArray();
        if (sources.Select(x => x.UnitId).Distinct().Count() != sources.Length ||
            siblings.Any(x => x.Movement.ActorId != movement.ActorId || x.Movement.CorrelationId != movement.CorrelationId))
            throw Review("Missing parent has conflicting identity or operation sources.");
        var quantity = sources.Sum(x => x.BaseQuantity); var value = sources.Sum(x => x.RemovedValue);
        var allocation = MoneyRoundingPolicy.Allocate(MoneyRoundingPolicy.Round(value), sources.Select(x => x.RemovedValue).ToArray());
        for (var i = 0; i < sources.Length; i++)
            if (sources[i].RecognizedLoss != allocation[i])
                throw Review("Persisted per-identity loss does not match its original stable allocation.");
        if (movement.ReferenceType == "STOCK_ADJUSTMENT")
        {
            var item = await inventory.GetStockAdjustmentItemAsync(movement.ReferenceId.Value, ct);
            var parent = item is null ? null : await inventory.GetStockAdjustmentAsync(item.StockAdjustmentId, ct);
            if (item is null || parent is null || parent.Status != StockAdjustmentStatus.Posted || parent.Reason != StockAdjustmentReason.Lost ||
                parent.ActorId != movement.ActorId || parent.CorrelationId != movement.CorrelationId || item.ProductId != unit.ProductId ||
                item.Direction != StockAdjustmentDirection.Decrease || item.BaseQuantity != quantity || item.TotalCostSnapshot != value ||
                siblings.Any(x => x.Movement.MovementType != InventoryMovementType.StockAdjustment || x.Movement.Reason != StockAdjustmentReason.Lost.ToString() ||
                                  x.Effects[0].StockBucket != item.TargetBucket))
                throw Review("Source is not an approved posted Lost adjustment.");
        }
        else if (movement.ReferenceType == "STOCKTAKE")
        {
            var parent = await inventory.GetStocktakeForUpdateAsync(movement.ReferenceId.Value, ct);
            var items = await inventory.GetStocktakeItemsAsync(movement.ReferenceId.Value, ct);
            var item = items.SingleOrDefault(x => x.ProductId == unit.ProductId);
            IReadOnlyList<StocktakeUnitCheck> checks = item is null ? [] : await inventory.GetStocktakeUnitChecksAsync(item.Id, ct);
            var missingIds = checks.Where(x => x.Result == StocktakeUnitCheckResult.Missing).Select(x => x.InventoryUnitId).OrderBy(x => x).ToArray();
            if (parent is null || parent.Status != StocktakeStatus.Posted || parent.PostedAt is null || item is null || item.VarianceQty != -quantity ||
                !missingIds.SequenceEqual(sources.Select(x => (Guid?)x.UnitId)) ||
                siblings.Any(x => x.Movement.MovementType != InventoryMovementType.PhysicalCountCorrection || x.Movement.Reason != "PHYSICAL_COUNT" ||
                                  x.Units[0].FromStatus != InventoryUnitStatus.InStock || x.Effects[0].StockBucket != InventoryBucket.Sellable))
                throw Review("Source is not an approved posted exact-unit stocktake.");
        }
        else
            throw Review("Source has no approved Missing parent authority.");
        var lot = await inventory.GetInventoryLotForUpdateAsync(selected.LotId, ct);
        if (lot is null || lot.ProductId != unit.ProductId || lot.PurchaseItemId != unit.SourcePurchaseItemId)
            throw Review("Consumed lot does not match the identity's acquisition provenance.");
        var origin = await inventory.GetInventoryMovementEvidenceAsync(lot.SourceMovementId, ct);
        if (origin is null || origin.Movement.ProductId != unit.ProductId ||
            !origin.Units.Any(x => x.InventoryUnitId == unit.Id) || origin.Effects.Count != 1 || origin.Consumptions.Count != 0 ||
            origin.Effects[0].QuantityDelta <= 0m || origin.Effects[0].QuantityBefore < 0m ||
            origin.Effects[0].QuantityBefore + origin.Effects[0].QuantityDelta != origin.Effects[0].QuantityAfter)
            throw Review("Consumed lot lacks physical identity origin evidence.");
        return selected;
    }

    public static void ValidateFound(InventoryMovementEvidence evidence, MissingRecoverySource source)
    {
        var movement = evidence.Movement;
        if (movement.ProductId != source.ProductId || movement.ReferenceType != FoundInventoryUnitHandler.RecoveryReferenceType || movement.ReferenceId != source.SourceMovementId ||
            movement.MovementType != InventoryMovementType.StockAdjustment || movement.RecognizedLossAmount != 0m ||
            movement.ActorId == Guid.Empty || movement.CorrelationId == Guid.Empty || string.IsNullOrWhiteSpace(movement.Reason) ||
            evidence.Units.Count != 1 || evidence.Effects.Count != 1 || evidence.Consumptions.Count != 0)
            throw Review("Recovery reference has invalid business authority.");
        var link = evidence.Units[0]; var effect = evidence.Effects[0];
        if (link.InventoryUnitId != source.UnitId || link.FromStatus != InventoryUnitStatus.Missing ||
            !IsRecoveryBucket(effect.StockBucket) || link.ToStatus != StatusFor(effect.StockBucket) ||
            effect.QuantityDelta != source.BaseQuantity || effect.QuantityBefore + effect.QuantityDelta != effect.QuantityAfter ||
            effect.QuantityBefore < 0m || link.MovementId != movement.Id || effect.MovementId != movement.Id ||
            movement.UnitCostSnapshot != decimal.Round(source.RemovedValue / source.BaseQuantity, 6, MidpointRounding.AwayFromZero))
            throw Review("Recovery quantity or identity does not reconcile to its Missing source.");
    }
}
