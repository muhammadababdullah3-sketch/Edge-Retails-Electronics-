using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Purchasing;

namespace EdgeRetails.Application.Features.Purchasing;

public sealed record PurchaseReturnLineInput(
    Guid PurchaseItemId,
    decimal EnteredQuantity,
    decimal? SupplierUnitReturnValue,
    IReadOnlyList<Guid> InventoryUnitIds);

public sealed record CreatePurchaseReturnCommand(
    Guid PurchaseId,
    string Reason,
    string? Note,
    PurchaseReturnSettlementMode SettlementMode,
    Guid CreatedBy,
    Guid ClientOperationId,
    IReadOnlyList<PurchaseReturnLineInput> Lines,
    SupplierSettlementMethod? ImmediateRefundMethod = null,
    decimal? ImmediateRefundAmount = null,
    string? RefundExternalReference = null);

public sealed record CreatePurchaseReturnResult(
    Guid PurchaseReturnId,
    string ReturnNumber,
    decimal SupplierReturnValue,
    decimal InventoryCostRemoved,
    bool WasExisting);

public sealed class CreatePurchaseReturnHandler
{
    private readonly IPurchasingRepository _purchases;
    private readonly IInventoryRepository _inventory;
    private readonly IInventoryCostAllocator _costs;
    private readonly ISupplierAccountRepository _supplierAccounts;
    private readonly ICashMovementService _cashMovements;
    private readonly IOperationLock _operationLock;
    private readonly IResourceLock _resourceLock;
    private readonly IBusinessAuditWriter _audit;
    private readonly IDocumentNumberService _numbers;
    private readonly IClock _clock;
    private readonly ITransactionRunner _transactions;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly IUnitOfWork _unitOfWork;
    private readonly CreateSupplierRefundHandler? _refunds;

    public CreatePurchaseReturnHandler(
        IPurchasingRepository purchases,
        IInventoryRepository inventory,
        IInventoryCostAllocator costs,
        ISupplierAccountRepository supplierAccounts,
        ICashMovementService cashMovements,
        IOperationLock operationLock,
        IResourceLock resourceLock,
        IBusinessAuditWriter audit,
        IDocumentNumberService numbers,
        IClock clock,
        ITransactionRunner transactions,
        IApplicationPermissionAuthorizer authorization,
        IUnitOfWork unitOfWork,
        IOperationOutcomeLedger? outcomeLedger = null,
        ICashRepository? cash = null,
        CreateSupplierRefundHandler? refunds = null)
    {
        _purchases = purchases;
        _inventory = inventory;
        _costs = costs;
        _supplierAccounts = supplierAccounts;
        _cashMovements = cashMovements;
        _operationLock = operationLock;
        _resourceLock = resourceLock;
        _audit = audit;
        _numbers = numbers;
        _clock = clock;
        _transactions = transactions;
        _authorization = authorization;
        _unitOfWork = unitOfWork;
        _outcomeLedger = outcomeLedger;
        _cash = cash;
        _refunds = refunds;
    }

    private readonly IOperationOutcomeLedger? _outcomeLedger;
    private readonly ICashRepository? _cash;

    public async Task<Result<CreatePurchaseReturnResult>> HandleAsync(
        CreatePurchaseReturnCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ClientOperationId == Guid.Empty ||
            command.Lines.Count == 0 ||
            string.IsNullOrWhiteSpace(command.Reason))
        {
            return Result<CreatePurchaseReturnResult>.Failure(
                "purchasing.return_invalid",
                "Purchase return requires operation id, reason and at least one item.");
        }

        if (command.Lines.GroupBy(x => x.PurchaseItemId).Any(g => g.Count() > 1))
        {
            return Result<CreatePurchaseReturnResult>.Failure(
                "purchasing.return_duplicate_item",
                "A purchase item may appear only once in a return.");
        }

        var refundMethod = command.SettlementMode == PurchaseReturnSettlementMode.CashDrawer
            ? SupplierSettlementMethod.CashDrawer : command.ImmediateRefundMethod;
        if (!Enum.IsDefined(command.SettlementMode) ||
            (command.ImmediateRefundMethod is { } explicitMethod && !Enum.IsDefined(explicitMethod)) ||
            (command.SettlementMode == PurchaseReturnSettlementMode.CashDrawer &&
                command.ImmediateRefundMethod is { } drawerMethod && drawerMethod != SupplierSettlementMethod.CashDrawer) ||
            (command.ImmediateRefundAmount is { } refundAmount && (refundMethod is null || Money(refundAmount) <= 0m)))
        {
            return Result<CreatePurchaseReturnResult>.Failure(
                "purchasing.return_refund_invalid", "Immediate refund requires a valid, consistent method and positive monetary amount.");
        }

        var fingerprint = OperationPayloadFingerprint.ComputeSha256(System.Text.Json.JsonSerializer.Serialize(new
        {
            command.PurchaseId, command.CreatedBy, Reason = command.Reason.Trim(), Note = command.Note?.Trim(),
            command.SettlementMode, RefundMethod = refundMethod, command.ImmediateRefundAmount,
            ExternalReference = command.RefundExternalReference?.Trim(),
            Lines = command.Lines.OrderBy(x => x.PurchaseItemId).Select(x => new
            {
                x.PurchaseItemId, x.EnteredQuantity, x.SupplierUnitReturnValue,
                UnitIds = x.InventoryUnitIds.OrderBy(id => id).ToArray()
            }).ToArray()
        }));

        var result = await _transactions.ExecuteAsync(async ct =>
        {
            var authorization = await _authorization.AuthorizeAsync(
                command.CreatedBy,
                PermissionKeys.PurchasingManage,
                ct);
            if (!authorization.IsSuccess)
            {
                return Result<CreatePurchaseReturnResult>.Failure(
                    authorization.Error!.Code,
                    authorization.Error.Message);
            }

            await _operationLock.AcquireAsync(command.ClientOperationId, ct);

            var existing = await _purchases.GetReturnByClientOperationIdAsync(
                command.ClientOperationId, ct);
            if (existing is not null)
            {
                if (existing.PurchaseId != command.PurchaseId)
                {
                    return Result<CreatePurchaseReturnResult>.Failure(
                        "idempotency.payload_mismatch",
                        "Operation was previously submitted for a different purchase.");
                }

                if (_outcomeLedger is not null)
                {
                    var outcome = await _outcomeLedger.GetOutcomeAsync(command.ClientOperationId, ct);
                    if (!string.IsNullOrEmpty(outcome?.PayloadFingerprint) && outcome.PayloadFingerprint != fingerprint)
                    {
                        return Result<CreatePurchaseReturnResult>.Failure(
                            "idempotency.payload_mismatch", "The return operation was previously posted with different parameters.");
                    }
                    if (refundMethod is not null && string.IsNullOrEmpty(outcome?.PayloadFingerprint))
                    {
                        return Result<CreatePurchaseReturnResult>.Failure(
                            "idempotency.legacy_replay_requires_review", "The historical return lacks a proven immediate-refund payload. Review is required.");
                    }
                    await _outcomeLedger.RecordSuccessAsync(
                        command.ClientOperationId,
                        "PurchaseReturn",
                        existing.Id,
                        existing.ReturnNumber,
                        actorId: command.CreatedBy,
                        payloadFingerprint: fingerprint,
                        cancellationToken: ct);
                }

                return Result<CreatePurchaseReturnResult>.Success(new(
                    existing.Id, existing.ReturnNumber, existing.SupplierReturnValue,
                    existing.InventoryCostRemoved, true));
            }

            if (refundMethod is not null)
            {
                if (_refunds is null || _outcomeLedger is null)
                {
                    return Result<CreatePurchaseReturnResult>.Failure(
                        "purchasing.refund_authority_unavailable", "Canonical refund and durable return replay authority are required.");
                }
                var refundAuthorization = await _authorization.AuthorizeAsync(command.CreatedBy, PermissionKeys.SupplierRefundCreate, ct);
                if (!refundAuthorization.IsSuccess)
                {
                    return Result<CreatePurchaseReturnResult>.Failure(refundAuthorization.Error!.Code, refundAuthorization.Error.Message);
                }
                if (refundMethod == SupplierSettlementMethod.CashDrawer && _cash is not null &&
                    await _cash.GetOpenSessionForUpdateAsync(ct) is null)
                {
                    return Result<CreatePurchaseReturnResult>.Failure(
                        "cash.session_required", "Open a cash session before posting a drawer-affecting cash transaction.");
                }
            }

            try
            {
                var purchase = await _purchases.GetPurchaseForUpdateAsync(command.PurchaseId, ct);
                if (purchase is null || purchase.Status != PurchaseStatus.Completed)
                {
                    return Result<CreatePurchaseReturnResult>.Failure(
                        "purchasing.purchase_not_returnable",
                        "Purchase was not found or is not returnable.");
                }

                if (await _purchases.HasVoidAsync(purchase.Id, ct))
                {
                    return Result<CreatePurchaseReturnResult>.Failure(
                        "purchasing.purchase_voided",
                        "A voided purchase cannot be returned.");
                }

                var purchaseItems = await _purchases.GetPurchaseItemsAsync(
                    purchase.Id,
                    ct);
                var requestedItemIds = command.Lines
                    .Select(x => x.PurchaseItemId)
                    .ToHashSet();
                var requestedItems = purchaseItems
                    .Where(x => requestedItemIds.Contains(x.Id))
                    .ToArray();

                if (requestedItems.Length != command.Lines.Count)
                {
                    return Result<CreatePurchaseReturnResult>.Failure(
                        "purchasing.return_item_invalid",
                        "One or more return items do not belong to the purchase.");
                }

                foreach (var productId in requestedItems
                    .Select(x => x.ProductId)
                    .Distinct()
                    .OrderBy(x => x))
                {
                    await _resourceLock.AcquireAsync("product", productId, ct);
                }

                await _resourceLock.AcquireAsync("supplier-account", purchase.SupplierId, ct);
                var balanceBeforeReturn = refundMethod is not null
                    ? await _supplierAccounts.GetCurrentBalanceAsync(purchase.SupplierId, ct) : 0m;

                foreach (var inventoryUnitId in command.Lines
                    .SelectMany(x => x.InventoryUnitIds)
                    .Distinct()
                    .OrderBy(x => x))
                {
                    await _resourceLock.AcquireAsync("inventory-unit", inventoryUnitId, ct);
                }

                var purchaseReturn = new PurchaseReturn
                {
                    ReturnNumber = await _numbers.NextAsync("PR", ct),
                    PurchaseId = purchase.Id,
                    Reason = command.Reason.Trim(),
                    Note = command.Note?.Trim(),
                    SettlementMode = command.SettlementMode,
                    CreatedBy = command.CreatedBy,
                    CreatedAt = _clock.UtcNow,
                    ClientOperationId = command.ClientOperationId
                };
                _purchases.AddReturn(purchaseReturn);

                decimal totalSupplierValue = 0m;
                decimal totalInventoryCost = 0m;

                foreach (var input in command.Lines.OrderBy(x => x.PurchaseItemId))
                {
                    var item = await _purchases.GetPurchaseItemForUpdateAsync(
                        input.PurchaseItemId, ct);
                    if (item is null || item.PurchaseId != purchase.Id)
                    {
                        return Result<CreatePurchaseReturnResult>.Failure(
                            "purchasing.return_item_invalid",
                            "Purchase return item does not belong to the purchase.");
                    }

                    if (input.EnteredQuantity <= 0)
                    {
                        return Result<CreatePurchaseReturnResult>.Failure(
                            "purchasing.return_quantity_positive",
                            "Purchase return quantity must be greater than zero.");
                    }

                    var originalItemUnits = await _purchases.GetPurchaseItemUnitsAsync(item.Id, ct);
                    var isSerialized = originalItemUnits.Count > 0;
                    var exactBaseQuantity = input.EnteredQuantity * item.FactorToBaseSnapshot;
                    decimal baseQuantity;

                    if (isSerialized)
                    {
                        if (!QuantityMath.IsWhole(exactBaseQuantity))
                        {
                            return Result<CreatePurchaseReturnResult>.Failure(
                                "purchasing.return_serialized_quantity_whole",
                                "Serialized purchase return must resolve to an exact whole base quantity before rounding.");
                        }

                        baseQuantity = exactBaseQuantity;
                        if (input.InventoryUnitIds.Count == 0 || input.InventoryUnitIds.Distinct().Count() != input.InventoryUnitIds.Count)
                        {
                            return Result<CreatePurchaseReturnResult>.Failure(
                                "purchasing.return_serial_count_mismatch",
                                "Serialized purchase return requires one exact InventoryUnit per base unit.");
                        }
                    }
                    else
                    {
                        if (input.InventoryUnitIds.Count > 0)
                        {
                            return Result<CreatePurchaseReturnResult>.Failure(
                                "purchasing.return_units_not_allowed",
                                "Quantity/length purchase returns must not submit exact InventoryUnit identities.");
                        }

                        baseQuantity = QuantityMath.RoundQuantity(exactBaseQuantity);
                    }

                    var alreadyReceived = await _inventory.GetPurchaseItemReceivedBaseQuantityAsync(
                        item.Id, ct);
                    var alreadyReturned = await _purchases.GetReturnedBaseQuantityAsync(
                        item.Id, ct);

                    if (baseQuantity + alreadyReturned > item.BaseQuantity)
                    {
                        return Result<CreatePurchaseReturnResult>.Failure(
                            "purchasing.return_exceeds_original",
                            "Total returned quantity cannot exceed original purchase quantity.");
                    }

                    var maxReturnable = Math.Max(0m, QuantityMath.RoundQuantity(alreadyReceived - alreadyReturned));

                    if (baseQuantity > maxReturnable)
                    {
                        return Result<CreatePurchaseReturnResult>.Failure(
                            "purchasing.return_exceeds_received",
                            "Purchase return exceeds remaining received quantity.");
                    }

                    if (await _inventory.IsProductBlockedByCountingStocktakeAsync(
                            item.ProductId, ct))
                    {
                        return Result<CreatePurchaseReturnResult>.Failure(
                            "inventory.stocktake_blocks_product",
                            "Purchase return is blocked by active stocktake.");
                    }

                    var stock = await _inventory.GetStockBalanceForUpdateAsync(
                        item.ProductId, ct)
                        ?? throw new BusinessRuleException(
                            "inventory.stock_missing", "Stock balance was not found.");

                    if (await _inventory.IsProductBlockedByCountingStocktakeAsync(
                            item.ProductId,
                            ct))
                    {
                        return Result<CreatePurchaseReturnResult>.Failure(
                            "inventory.stocktake_blocks_product",
                            "Purchase return is blocked by active stocktake.");
                    }

                    var movement = new InventoryMovement
                    {
                        ProductId = item.ProductId,
                        MovementType = InventoryMovementType.PurchaseReturn,
                        ReferenceType = "PURCHASE_RETURN",
                        ReferenceId = purchaseReturn.Id,
                        ActorId = command.CreatedBy,
                        OccurredAt = _clock.UtcNow,
                        CorrelationId = command.ClientOperationId,
                        Reason = command.Reason.Trim()
                    };
                    _inventory.AddMovement(movement);

                    var before = stock.SellableQty;
                    decimal inventoryCostRemoved;

                    if (isSerialized)
                    {
                        inventoryCostRemoved = await ProcessSerializedAsync(
                            purchase, item, input, baseQuantity, movement, ct);
                    }
                    else
                    {
                        inventoryCostRemoved = await ProcessQuantityAsync(
                            item, baseQuantity, movement, ct);
                    }

                    stock.ApplyDelta(InventoryBucket.Sellable, -baseQuantity);
                    movement.UnitCostSnapshot = Cost(inventoryCostRemoved / baseQuantity);
                    _inventory.AddMovementEffect(new InventoryMovementEffect
                    {
                        MovementId = movement.Id,
                        StockBucket = InventoryBucket.Sellable,
                        QuantityDelta = -baseQuantity,
                        QuantityBefore = before,
                        QuantityAfter = stock.SellableQty
                    });

                    var supplierUnitValue = input.SupplierUnitReturnValue
                        ?? item.EnteredUnitCost;
                    if (supplierUnitValue < 0)
                    {
                        return Result<CreatePurchaseReturnResult>.Failure(
                            "purchasing.return_supplier_value_negative",
                            "Supplier return value cannot be negative.");
                    }

                    var supplierValue = Money(
                        input.EnteredQuantity * supplierUnitValue);

                    var returnItem = new PurchaseReturnItem
                    {
                        PurchaseReturnId = purchaseReturn.Id,
                        PurchaseItemId = item.Id,
                        ProductId = item.ProductId,
                        ProductUnitId = item.ProductUnitId,
                        EnteredQuantity = QuantityMath.RoundQuantity(input.EnteredQuantity),
                        FactorToBaseSnapshot = item.FactorToBaseSnapshot,
                        BaseQuantity = baseQuantity,
                        SupplierUnitReturnValue = Cost(supplierUnitValue),
                        SupplierReturnValue = supplierValue,
                        InventoryUnitCostRemoved = Cost(
                            inventoryCostRemoved / baseQuantity),
                        InventoryCostRemoved = Cost(inventoryCostRemoved)
                    };
                    _purchases.AddReturnItem(returnItem);

                    foreach (var unitId in input.InventoryUnitIds)
                    {
                        _purchases.AddReturnItemUnit(new PurchaseReturnItemUnit
                        {
                            PurchaseReturnItemId = returnItem.Id,
                            InventoryUnitId = unitId
                        });
                    }

                    totalSupplierValue += supplierValue;
                    totalInventoryCost += inventoryCostRemoved;
                }

                purchaseReturn.SupplierReturnValue = Money(totalSupplierValue);
                purchaseReturn.InventoryCostRemoved = Cost(totalInventoryCost);

                if (purchaseReturn.SupplierReturnValue > 0)
                {
                    var accountEntry = new SupplierAccountEntry
                    {
                        EntryNumber = await _numbers.NextAsync("SAE", ct),
                        SupplierId = purchase.SupplierId,
                        EntryType = SupplierAccountEntryType.PurchaseReturnCredit,
                        Direction = SupplierAccountDirection.DecreasePayable,
                        Amount = purchaseReturn.SupplierReturnValue,
                        ReferenceType = "PurchaseReturn",
                        ReferenceId = purchaseReturn.Id,
                        OccurredAt = _clock.UtcNow,
                        ActorId = command.CreatedBy,
                        ClientOperationId = command.ClientOperationId,
                        Note = purchaseReturn.ReturnNumber,
                        CreatedAt = _clock.UtcNow
                    };
                    accountEntry.ValidateDirection();
                    _supplierAccounts.AddEntry(accountEntry);

                }

                if (refundMethod is { } method)
                {
                    var refundResult = await _refunds!.PostInTransactionAsync(
                        new CreateSupplierRefundCommand(purchase.SupplierId,
                            command.ImmediateRefundAmount ?? purchaseReturn.SupplierReturnValue, method,
                            command.CreatedBy, command.ClientOperationId, command.RefundExternalReference,
                            "PurchaseReturn", purchaseReturn.Id, command.Reason.Trim()),
                        balanceBeforeReturn - purchaseReturn.SupplierReturnValue, ct);
                    if (!refundResult.IsSuccess)
                    {
                        return Result<CreatePurchaseReturnResult>.Failure(refundResult.Error!.Code, refundResult.Error.Message);
                    }
                }
                _audit.Record(
                    "PURCHASE_RETURN_COMPLETED",
                    "PURCHASE_RETURN",
                    purchaseReturn.Id,
                    command.CreatedBy,
                    command.ClientOperationId,
                    $"Return {purchaseReturn.ReturnNumber}; purchase {purchase.PurchaseNumber}; supplier value {purchaseReturn.SupplierReturnValue:0.00}; inventory cost {purchaseReturn.InventoryCostRemoved:0.000000}.");

                if (_outcomeLedger is not null)
                {
                    await _outcomeLedger.RecordSuccessAsync(
                        command.ClientOperationId,
                        "PurchaseReturn",
                        purchaseReturn.Id,
                        purchaseReturn.ReturnNumber,
                        actorId: command.CreatedBy,
                        payloadFingerprint: fingerprint,
                        cancellationToken: ct);
                }

                await _unitOfWork.SaveChangesAsync(ct);
                return Result<CreatePurchaseReturnResult>.Success(new(
                    purchaseReturn.Id, purchaseReturn.ReturnNumber,
                    purchaseReturn.SupplierReturnValue,
                    purchaseReturn.InventoryCostRemoved, false));
            }
            catch (BusinessRuleException ex)
            {
                return Result<CreatePurchaseReturnResult>.Failure(ex.Code, ex.Message);
            }
        }, cancellationToken);

        if (!result.IsSuccess && _outcomeLedger is not null && command.ClientOperationId != Guid.Empty)
        {
            await _outcomeLedger.RecordFailureAsync(
                command.ClientOperationId,
                "PurchaseReturn",
                result.Error?.Code ?? "purchasing.return_failed",
                result.Error?.Message ?? "Purchase return failed.",
                actorId: command.CreatedBy,
                payloadFingerprint: fingerprint,
                cancellationToken: cancellationToken);
        }

        return result;
    }

    private async Task<decimal> ProcessQuantityAsync(
        PurchaseItem item,
        decimal baseQuantity,
        InventoryMovement movement,
        CancellationToken ct)
    {
        var positions = await _inventory.GetPurchaseItemLotPositionsForUpdateAsync(
            item.Id, InventoryBucket.Sellable, ct);
        var available = QuantityMath.RoundQuantity(
            positions.Sum(x => x.Balance.Quantity));
        if (available < baseQuantity)
        {
            throw new BusinessRuleException(
                "purchasing.return_origin_not_available",
                "Enough stock from this purchase origin is no longer available.");
        }

        var remaining = baseQuantity;
        decimal removed = 0m;
        foreach (var position in positions)
        {
            if (remaining <= 0)
            {
                break;
            }

            var take = QuantityMath.RoundQuantity(
                Math.Min(position.Balance.Quantity, remaining));
            position.Balance.Quantity = QuantityMath.RoundQuantity(
                position.Balance.Quantity - take);

            _inventory.AddLotConsumption(new InventoryLotConsumption
            {
                LotId = position.Lot.Id,
                MovementId = movement.Id,
                Quantity = take,
                UnitCostSnapshot = position.Lot.EffectiveUnitCost,
                TotalCostSnapshot = Cost(take * position.Lot.EffectiveUnitCost),
                OccurredAt = _clock.UtcNow
            });

            removed += await _costs.RemoveCarryingValueAsync(
                item.ProductId, take, position.Lot.EffectiveUnitCost, ct);
            remaining = QuantityMath.RoundQuantity(remaining - take);
        }
        return Cost(removed);
    }

    private async Task<decimal> ProcessSerializedAsync(
        Purchase purchase,
        PurchaseItem item,
        PurchaseReturnLineInput input,
        decimal baseQuantity,
        InventoryMovement movement,
        CancellationToken ct)
    {
        if (!QuantityMath.IsWhole(baseQuantity) ||
            input.InventoryUnitIds.Count == 0 ||
            input.InventoryUnitIds.Distinct().Count() != input.InventoryUnitIds.Count)
        {
            throw new BusinessRuleException(
                "purchasing.return_serial_count_mismatch",
                "Serialized return requires one distinct unit per base unit.");
        }

        var units = await _inventory.GetInventoryUnitsForUpdateAsync(
            item.ProductId, input.InventoryUnitIds, ct);
        if (units.Count != input.InventoryUnitIds.Count)
        {
            throw new BusinessRuleException(
                "purchasing.return_serial_not_eligible",
                "One or more serialized units were not found.");
        }

        foreach (var unit in units)
        {
            if (unit.SourcePurchaseItemId is null)
            {
                throw new BusinessRuleException(
                    "purchasing.return_requires_supplier_provenance",
                    "Serialized unit lacks original purchase/supplier provenance.");
            }

            if (unit.SourcePurchaseItemId != item.Id)
            {
                var sourceItem = await _purchases.GetPurchaseItemForUpdateAsync(unit.SourcePurchaseItemId.Value, ct);
                if (sourceItem is not null)
                {
                    var sourcePurchase = await _purchases.GetPurchaseForUpdateAsync(sourceItem.PurchaseId, ct);
                    if (sourcePurchase is not null && sourcePurchase.SupplierId != purchase.SupplierId)
                    {
                        throw new BusinessRuleException(
                            "purchasing.return_wrong_supplier",
                            "An item received from another supplier cannot be returned to this supplier.");
                    }
                }

                throw new BusinessRuleException(
                    "purchasing.return_wrong_supplier",
                    "Serialized unit does not belong to the purchase item being returned.");
            }

            if (unit.Status != InventoryUnitStatus.InStock || unit.InventoryLotId is null)
            {
                throw new BusinessRuleException(
                    "purchasing.return_serial_not_eligible",
                    "One or more serialized units are not currently in stock or eligible for return.");
            }
        }

        decimal removed = 0m;
        var quantities = new Dictionary<Guid, decimal>();
        foreach (var unit in units)
            quantities[unit.Id] = await _inventory.GetPhysicalUnitBaseQuantitySnapshotAsync(unit, ct);
        if (quantities.Values.Sum() != baseQuantity)
            throw new BusinessRuleException("purchasing.return_serial_count_mismatch",
                "Purchase return quantity must match the original received physical quantities.");
        foreach (var unit in units.OrderBy(x => x.Id))
        {
            var basePerUnit = quantities[unit.Id];
            var balance = await _inventory.GetLotBucketBalanceForUpdateAsync(
                unit.InventoryLotId!.Value, InventoryBucket.Sellable, ct)
                ?? throw new BusinessRuleException(
                    "purchasing.return_lot_missing",
                    "Serialized unit lot balance was not found.");
            if (balance.Quantity < basePerUnit)
            {
                throw new BusinessRuleException(
                    "purchasing.return_lot_insufficient",
                    "Serialized unit lot is no longer available.");
            }

            balance.Quantity = QuantityMath.RoundQuantity(balance.Quantity - basePerUnit);

            _inventory.AddLotConsumption(new InventoryLotConsumption
            {
                LotId = unit.InventoryLotId.Value,
                MovementId = movement.Id,
                Quantity = basePerUnit,
                UnitCostSnapshot = unit.AcquisitionCost / basePerUnit,
                TotalCostSnapshot = unit.AcquisitionCost,
                OccurredAt = _clock.UtcNow
            });

            removed += await _costs.RemoveCarryingValueAsync(
                item.ProductId, basePerUnit, unit.AcquisitionCost / basePerUnit, ct);

            var from = unit.Status;
            unit.Status = InventoryUnitStatus.SupplierReturned;
            unit.Version++;
            _inventory.AddMovementUnit(new InventoryMovementUnit
            {
                MovementId = movement.Id,
                InventoryUnitId = unit.Id,
                FromStatus = from,
                ToStatus = unit.Status
            });
        }
        return Cost(removed);
    }

    private static decimal Money(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    private static decimal Cost(decimal value) =>
        decimal.Round(value, 6, MidpointRounding.AwayFromZero);
}

