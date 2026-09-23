using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Purchasing;

namespace EdgeRetails.Application.Features.Purchasing;

public sealed record VoidPurchaseCommand(
    Guid PurchaseId,
    Guid ClientOperationId,
    Guid VoidedBy,
    string Reason);

public sealed record VoidPurchaseResult(
    Guid PurchaseId,
    Guid PurchaseVoidId,
    bool WasExisting);

public sealed class VoidPurchaseHandler
{
    private readonly IPurchasingRepository _purchases;
    private readonly IInventoryRepository _inventory;
    private readonly IInventoryCostAllocator _costs;
    private readonly ISupplierAccountRepository _supplierAccounts;
    private readonly IOperationLock _operationLock;
    private readonly IResourceLock _resourceLock;
    private readonly IBusinessAuditWriter _audit;
    private readonly IDocumentNumberService _numbers;
    private readonly IClock _clock;
    private readonly ITransactionRunner _transactions;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly IUnitOfWork _unitOfWork;

    public VoidPurchaseHandler(
        IPurchasingRepository purchases,
        IInventoryRepository inventory,
        IInventoryCostAllocator costs,
        ISupplierAccountRepository supplierAccounts,
        IOperationLock operationLock,
        IResourceLock resourceLock,
        IBusinessAuditWriter audit,
        IDocumentNumberService numbers,
        IClock clock,
        ITransactionRunner transactions,
        IApplicationPermissionAuthorizer authorization,
        IUnitOfWork unitOfWork)
    {
        _purchases = purchases;
        _inventory = inventory;
        _costs = costs;
        _supplierAccounts = supplierAccounts;
        _operationLock = operationLock;
        _resourceLock = resourceLock;
        _audit = audit;
        _numbers = numbers;
        _clock = clock;
        _transactions = transactions;
        _authorization = authorization;
        _unitOfWork = unitOfWork;
    }

    public Task<Result<VoidPurchaseResult>> HandleAsync(
        VoidPurchaseCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ClientOperationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(command.Reason))
        {
            return Task.FromResult(Result<VoidPurchaseResult>.Failure(
                "purchasing.void_invalid",
                "Purchase void requires operation id and reason."));
        }

        return _transactions.ExecuteAsync(async ct =>
        {
            var authorization = await _authorization.AuthorizeAsync(
                command.VoidedBy,
                PermissionKeys.PurchasingManage,
                ct);
            if (!authorization.IsSuccess)
            {
                return Result<VoidPurchaseResult>.Failure(
                    authorization.Error!.Code,
                    authorization.Error.Message);
            }

            await _operationLock.AcquireAsync(command.ClientOperationId, ct);

            var existing = await _purchases.GetVoidByClientOperationIdAsync(
                command.ClientOperationId, ct);
            if (existing is not null)
            {
                return Result<VoidPurchaseResult>.Success(new(
                    existing.PurchaseId, existing.Id, true));
            }

            try
            {
                var purchase = await _purchases.GetPurchaseForUpdateAsync(
                    command.PurchaseId, ct);
                if (purchase is null || purchase.Status != PurchaseStatus.Completed)
                {
                    return Result<VoidPurchaseResult>.Failure(
                        "purchasing.purchase_not_voidable",
                        "Purchase was not found or is not voidable.");
                }

                if (await _purchases.HasCompletedReturnAsync(purchase.Id, ct))
                {
                    return Result<VoidPurchaseResult>.Failure(
                        "purchasing.void_has_returns",
                        "A purchase with supplier returns cannot be voided.");
                }

                var items = await _purchases.GetPurchaseItemsAsync(purchase.Id, ct);

                foreach (var productId in items
                    .Select(x => x.ProductId)
                    .Distinct()
                    .OrderBy(x => x))
                {
                    await _resourceLock.AcquireAsync("product", productId, ct);
                }

                await _resourceLock.AcquireAsync("supplier-account", purchase.SupplierId, ct);

                var allUnitIds = new List<Guid>();
                foreach (var purchaseItem in items)
                {
                    var itemUnits = await _purchases.GetPurchaseItemUnitsAsync(purchaseItem.Id, ct);
                    allUnitIds.AddRange(itemUnits.Select(x => x.InventoryUnitId));
                }

                foreach (var inventoryUnitId in allUnitIds.Distinct().OrderBy(x => x))
                {
                    await _resourceLock.AcquireAsync("inventory-unit", inventoryUnitId, ct);
                }

                foreach (var item in items.OrderBy(x => x.ProductId))
                {
                    if (await _inventory.HasPurchaseItemConsumptionAsync(item.Id, ct))
                    {
                        return Result<VoidPurchaseResult>.Failure(
                            "purchasing.void_origin_consumed",
                            "Purchase-origin stock has already been consumed by a downstream business transaction.");
                    }

                    if (await _inventory.IsProductBlockedByCountingStocktakeAsync(
                            item.ProductId, ct))
                    {
                        return Result<VoidPurchaseResult>.Failure(
                            "inventory.stocktake_blocks_product",
                            "Purchase void is blocked by an active stocktake.");
                    }

                    var positions = await _inventory.GetPurchaseItemLotPositionsForUpdateAsync(
                        item.Id, InventoryBucket.Sellable, ct);
                    var available = QuantityMath.RoundQuantity(
                        positions.Sum(x => x.Balance.Quantity));
                    if (available != item.BaseQuantity)
                    {
                        return Result<VoidPurchaseResult>.Failure(
                            "purchasing.void_stock_consumed",
                            "Purchase-origin stock has been consumed or moved.");
                    }

                    var stock = await _inventory.GetStockBalanceForUpdateAsync(
                        item.ProductId, ct)
                        ?? throw new BusinessRuleException(
                            "inventory.stock_missing", "Stock balance was not found.");

                    if (await _inventory.IsProductBlockedByCountingStocktakeAsync(
                            item.ProductId,
                            ct))
                    {
                        return Result<VoidPurchaseResult>.Failure(
                            "inventory.stocktake_blocks_product",
                            "Purchase void is blocked by an active stocktake.");
                    }

                    var before = stock.SellableQty;
                    stock.ApplyDelta(InventoryBucket.Sellable, -item.BaseQuantity);

                    var movement = new InventoryMovement
                    {
                        ProductId = item.ProductId,
                        MovementType = InventoryMovementType.PurchaseVoid,
                        ReferenceType = "PURCHASE_VOID",
                        ReferenceId = purchase.Id,
                        ActorId = command.VoidedBy,
                        OccurredAt = _clock.UtcNow,
                        CorrelationId = command.ClientOperationId,
                        Reason = command.Reason.Trim()
                    };
                    _inventory.AddMovement(movement);
                    _inventory.AddMovementEffect(new InventoryMovementEffect
                    {
                        MovementId = movement.Id,
                        StockBucket = InventoryBucket.Sellable,
                        QuantityDelta = -item.BaseQuantity,
                        QuantityBefore = before,
                        QuantityAfter = stock.SellableQty
                    });

                    decimal removed = 0m;
                    foreach (var position in positions)
                    {
                        var qty = position.Balance.Quantity;
                        position.Balance.Quantity = 0m;
                        removed += await _costs.RemoveCarryingValueAsync(
                            item.ProductId, qty, position.Lot.EffectiveUnitCost, ct);
                    }
                    movement.UnitCostSnapshot = item.BaseQuantity == 0m
                        ? null
                        : Cost(removed / item.BaseQuantity);

                    var itemUnits = await _purchases.GetPurchaseItemUnitsAsync(item.Id, ct);
                    if (itemUnits.Count > 0)
                    {
                        var units = await _inventory.GetInventoryUnitsForUpdateAsync(
                            item.ProductId,
                            itemUnits.Select(x => x.InventoryUnitId).ToArray(),
                            ct);

                        if (units.Count != itemUnits.Count ||
                            units.Any(x => x.Status != InventoryUnitStatus.InStock))
                        {
                            return Result<VoidPurchaseResult>.Failure(
                                "purchasing.void_serial_consumed",
                                "One or more serialized units are no longer in received stock.");
                        }

                        foreach (var unit in units)
                        {
                            var from = unit.Status;
                            unit.Status = InventoryUnitStatus.ReceiptVoided;
                            unit.Version++;
                            _inventory.AddMovementUnit(new InventoryMovementUnit
                            {
                                MovementId = movement.Id,
                                InventoryUnitId = unit.Id,
                                FromStatus = from,
                                ToStatus = unit.Status
                            });
                        }
                    }
                }

                purchase.Status = PurchaseStatus.Voided;
                purchase.Version++;

                var purchaseVoid = new PurchaseVoid
                {
                    PurchaseId = purchase.Id,
                    ClientOperationId = command.ClientOperationId,
                    Reason = command.Reason.Trim(),
                    VoidedBy = command.VoidedBy,
                    VoidedAt = _clock.UtcNow,
                    CashDrawerReversalAmount = null
                };
                _purchases.AddVoid(purchaseVoid);

                var accountEntry = new SupplierAccountEntry
                {
                    EntryNumber = await _numbers.NextAsync("SAE", ct),
                    SupplierId = purchase.SupplierId,
                    EntryType = SupplierAccountEntryType.PurchaseVoidReversal,
                    Direction = SupplierAccountDirection.DecreasePayable,
                    Amount = purchase.GrandTotal,
                    ReferenceType = "PurchaseVoid",
                    ReferenceId = purchaseVoid.Id,
                    OccurredAt = _clock.UtcNow,
                    ActorId = command.VoidedBy,
                    ClientOperationId = command.ClientOperationId,
                    Note = command.Reason.Trim(),
                    CreatedAt = _clock.UtcNow
                };
                accountEntry.ValidateDirection();
                _supplierAccounts.AddEntry(accountEntry);

                _audit.Record(
                    "PURCHASE_VOIDED",
                    "PURCHASE",
                    purchase.Id,
                    command.VoidedBy,
                    command.ClientOperationId,
                    $"Purchase {purchase.PurchaseNumber} voided; reason {command.Reason.Trim()}.");

                await _unitOfWork.SaveChangesAsync(ct);
                return Result<VoidPurchaseResult>.Success(new(
                    purchase.Id, purchaseVoid.Id, false));
            }
            catch (BusinessRuleException ex)
            {
                return Result<VoidPurchaseResult>.Failure(ex.Code, ex.Message);
            }
        }, cancellationToken);
    }

    private static decimal Cost(decimal value) =>
        decimal.Round(value, 6, MidpointRounding.AwayFromZero);
}

