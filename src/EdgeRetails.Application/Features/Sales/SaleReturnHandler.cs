using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Sales;

namespace EdgeRetails.Application.Features.Sales;

public sealed record SaleReturnLineInput(
    Guid SaleItemId,
    decimal BaseQuantity,
    SaleReturnDisposition Disposition,
    IReadOnlyList<Guid> InventoryUnitIds);

public sealed record CreateSaleReturnCommand(
    Guid SaleId,
    string ReasonCode,
    string? ReasonNote,
    RefundMethod RefundMethod,
    Guid CreatedBy,
    Guid ClientOperationId,
    IReadOnlyList<SaleReturnLineInput> Lines);

public sealed record CreateSaleReturnResult(
    Guid SaleReturnId,
    string ReturnNumber,
    decimal RefundAmount,
    bool WasExisting);

internal sealed record OriginalLotReturnAllocation(
    Guid LotId,
    Guid? PurchaseItemId,
    decimal Quantity);

public sealed class CreateSaleReturnHandler
{
    private readonly ISalesRepository _sales;
    private readonly IInventoryRepository _inventory;
    private readonly IInventoryCostAllocator _costs;
    private readonly ICashRepository _cash;
    private readonly IOperationLock _operationLock;
    private readonly IResourceLock _resourceLock;
    private readonly IBusinessAuditWriter _audit;
    private readonly IDocumentNumberService _numbers;
    private readonly IClock _clock;
    private readonly ITransactionRunner _transactions;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly IUnitOfWork _unitOfWork;

    public CreateSaleReturnHandler(
        ISalesRepository sales,
        IInventoryRepository inventory,
        IInventoryCostAllocator costs,
        ICashRepository cash,
        IOperationLock operationLock,
        IResourceLock resourceLock,
        IBusinessAuditWriter audit,
        IDocumentNumberService numbers,
        IClock clock,
        ITransactionRunner transactions,
        IApplicationPermissionAuthorizer authorization,
        IUnitOfWork unitOfWork)
    {
        _sales = sales;
        _inventory = inventory;
        _costs = costs;
        _cash = cash;
        _operationLock = operationLock;
        _resourceLock = resourceLock;
        _audit = audit;
        _numbers = numbers;
        _clock = clock;
        _transactions = transactions;
        _authorization = authorization;
        _unitOfWork = unitOfWork;
    }

    public Task<Result<CreateSaleReturnResult>> HandleAsync(
        CreateSaleReturnCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ClientOperationId == Guid.Empty ||
            command.Lines.Count == 0 ||
            string.IsNullOrWhiteSpace(command.ReasonCode))
        {
            return Task.FromResult(Result<CreateSaleReturnResult>.Failure(
                "sales.return_invalid",
                "Sale return requires operation id, reason and at least one item."));
        }

        if (command.Lines.GroupBy(x => x.SaleItemId).Any(g => g.Count() > 1))
        {
            return Task.FromResult(Result<CreateSaleReturnResult>.Failure(
                "sales.return_duplicate_item",
                "A sale item may appear only once in a return."));
        }

        return _transactions.ExecuteAsync(async ct =>
        {
            var authorization = await _authorization.AuthorizeAsync(
                command.CreatedBy,
                PermissionKeys.SalesCreate,
                ct);
            if (!authorization.IsSuccess)
            {
                return Result<CreateSaleReturnResult>.Failure(
                    authorization.Error!.Code,
                    authorization.Error.Message);
            }

            await _operationLock.AcquireAsync(command.ClientOperationId, ct);

            var existing = await _sales.GetReturnByClientOperationIdAsync(
                command.ClientOperationId,
                ct);
            if (existing is not null)
            {
                return Result<CreateSaleReturnResult>.Success(new(
                    existing.Id,
                    existing.ReturnNumber,
                    existing.RefundAmount,
                    true));
            }

            try
            {
                var sale = await _sales.GetSaleForUpdateAsync(command.SaleId, ct);
                if (sale is null || sale.Status != SaleStatus.Completed)
                {
                    return Result<CreateSaleReturnResult>.Failure(
                        "sales.sale_not_returnable",
                        "Sale was not found or is not returnable.");
                }

                var saleItems = await _sales.GetSaleItemsAsync(sale.Id, ct);
                var requestedItemIds = command.Lines
                    .Select(x => x.SaleItemId)
                    .ToHashSet();
                var requestedItems = saleItems
                    .Where(x => requestedItemIds.Contains(x.Id))
                    .ToArray();

                if (requestedItems.Length != command.Lines.Count)
                {
                    return Result<CreateSaleReturnResult>.Failure(
                        "sales.return_item_invalid",
                        "One or more return items do not belong to the sale.");
                }

                foreach (var productId in requestedItems
                    .Select(x => x.ProductId)
                    .Distinct()
                    .OrderBy(x => x))
                {
                    await _resourceLock.AcquireAsync("product", productId, ct);
                }

                var saleReturn = new SaleReturn
                {
                    ReturnNumber = await _numbers.NextAsync("SR", ct),
                    SaleId = sale.Id,
                    ReasonCode = command.ReasonCode.Trim().ToUpperInvariant(),
                    ReasonNote = Normalize(command.ReasonNote),
                    RefundMethod = command.RefundMethod,
                    CreatedBy = command.CreatedBy,
                    CreatedAt = _clock.UtcNow,
                    ClientOperationId = command.ClientOperationId
                };
                _sales.AddReturn(saleReturn);

                decimal totalRefund = 0m;

                foreach (var input in command.Lines.OrderBy(x => x.SaleItemId))
                {
                    var item = await _sales.GetSaleItemForUpdateAsync(
                        input.SaleItemId,
                        ct);
                    if (item is null || item.SaleId != sale.Id)
                    {
                        return Result<CreateSaleReturnResult>.Failure(
                            "sales.return_item_invalid",
                            "Sale return item does not belong to the sale.");
                    }

                    var baseQuantity = QuantityMath.RoundQuantity(input.BaseQuantity);
                    if (baseQuantity <= 0)
                    {
                        return Result<CreateSaleReturnResult>.Failure(
                            "sales.return_quantity_positive",
                            "Return base quantity must be greater than zero.");
                    }

                    var priorQty = await _sales.GetReturnedBaseQuantityAsync(item.Id, ct);
                    var cumulativeQty = QuantityMath.RoundQuantity(priorQty + baseQuantity);
                    if (cumulativeQty > item.BaseQuantity)
                    {
                        return Result<CreateSaleReturnResult>.Failure(
                            "sales.return_exceeds_original",
                            "Return quantity exceeds the remaining sold quantity.");
                    }

                    if (await _inventory.IsProductBlockedByCountingStocktakeAsync(
                            item.ProductId,
                            ct))
                    {
                        return Result<CreateSaleReturnResult>.Failure(
                            "inventory.stocktake_blocks_product",
                            "Sale return is blocked by an active stocktake.");
                    }

                    var stock = await _inventory.GetStockBalanceForUpdateAsync(
                        item.ProductId,
                        ct);
                    if (stock is null)
                    {
                        return Result<CreateSaleReturnResult>.Failure(
                            "sales.stock_missing",
                            "Stock balance was not found for the returned product.");
                    }

                    if (await _inventory.IsProductBlockedByCountingStocktakeAsync(
                            item.ProductId,
                            ct))
                    {
                        return Result<CreateSaleReturnResult>.Failure(
                            "inventory.stocktake_blocks_product",
                            "Sale return is blocked by an active stocktake.");
                    }

                    var priorRefund = await _sales.GetRefundedAmountAsync(item.Id, ct);
                    var refundAmount = CalculateResidualAmount(
                        item.NetLineTotal,
                        item.BaseQuantity,
                        priorQty,
                        baseQuantity,
                        priorRefund,
                        2);

                    var saleItemUnits = await _sales.GetSaleItemUnitsAsync(item.Id, ct);
                    var serialized = saleItemUnits.Count > 0;

                    decimal originalCostAmount;
                    IReadOnlyList<(InventoryUnit Unit, SaleItemUnit Snapshot)> serializedUnits =
                        Array.Empty<(InventoryUnit, SaleItemUnit)>();
                    IReadOnlyList<OriginalLotReturnAllocation> originAllocations =
                        Array.Empty<OriginalLotReturnAllocation>();

                    if (serialized)
                    {
                        var prepared = await PrepareSerializedUnitsAsync(
                            item,
                            input,
                            baseQuantity,
                            saleItemUnits,
                            ct);
                        serializedUnits = prepared;
                        originalCostAmount = Cost(
                            prepared.Sum(x => x.Snapshot.UnitCostSnapshot));
                    }
                    else
                    {
                        if (input.InventoryUnitIds.Count > 0)
                        {
                            return Result<CreateSaleReturnResult>.Failure(
                                "sales.return_serials_not_allowed",
                                "Non-serialized sale items cannot return inventory-unit ids.");
                        }

                        var priorCost = await _sales.GetReturnedOriginalCostAmountAsync(
                            item.Id,
                            ct);
                        originalCostAmount = CalculateResidualAmount(
                            item.TotalCostSnapshot,
                            item.BaseQuantity,
                            priorQty,
                            baseQuantity,
                            priorCost,
                            6);

                        originAllocations = await ResolveOriginalLotAllocationsAsync(
                            item,
                            priorQty,
                            baseQuantity,
                            ct);
                    }

                    var destination = SaleMath.ToInventoryBucket(input.Disposition);
                    var movement = new InventoryMovement
                    {
                        ProductId = item.ProductId,
                        MovementType = InventoryMovementType.SaleReturn,
                        ReferenceType = "SALE_RETURN",
                        ReferenceId = saleReturn.Id,
                        UnitCostSnapshot = Cost(originalCostAmount / baseQuantity),
                        RecognizedLossAmount = input.Disposition == SaleReturnDisposition.Scrap
                            ? Money(originalCostAmount)
                            : 0m,
                        ActorId = command.CreatedBy,
                        OccurredAt = _clock.UtcNow,
                        CorrelationId = command.ClientOperationId,
                        Reason = saleReturn.ReasonCode,
                        Note = saleReturn.ReasonNote
                    };
                    _inventory.AddMovement(movement);

                    var before = stock.Get(destination);
                    stock.ApplyDelta(destination, baseQuantity);
                    _inventory.AddMovementEffect(new InventoryMovementEffect
                    {
                        MovementId = movement.Id,
                        StockBucket = destination,
                        QuantityDelta = baseQuantity,
                        QuantityBefore = before,
                        QuantityAfter = stock.Get(destination)
                    });

                    if (serialized)
                    {
                        await RestoreSerializedUnitsAsync(
                            serializedUnits,
                            input.Disposition,
                            movement,
                            ct);
                    }
                    else
                    {
                        await RestoreQuantityReturnLotsAsync(
                            item,
                            originAllocations,
                            input.Disposition,
                            originalCostAmount,
                            movement,
                            ct);
                    }

                    var enteredQuantity = QuantityMath.RoundQuantity(
                        baseQuantity / item.FactorToBaseSnapshot);

                    var returnItem = new SaleReturnItem
                    {
                        SaleReturnId = saleReturn.Id,
                        SaleItemId = item.Id,
                        ProductId = item.ProductId,
                        EnteredQuantity = enteredQuantity,
                        ProductUnitId = item.ProductUnitId,
                        FactorToBaseSnapshot = item.FactorToBaseSnapshot,
                        BaseQuantity = baseQuantity,
                        Disposition = input.Disposition,
                        RefundAmount = refundAmount,
                        OriginalCostAmount = originalCostAmount,
                        CostReversalAmount = originalCostAmount
                    };
                    _sales.AddReturnItem(returnItem);

                    foreach (var pair in serializedUnits)
                    {
                        _sales.AddReturnItemUnit(new SaleReturnItemUnit
                        {
                            SaleReturnItemId = returnItem.Id,
                            InventoryUnitId = pair.Unit.Id
                        });
                    }

                    totalRefund += refundAmount;
                }

                saleReturn.RefundAmount = Money(totalRefund);

                CashSession? cashSession = null;
                if (command.RefundMethod == RefundMethod.Cash)
                {
                    cashSession = await _cash.GetOpenSessionForUpdateAsync(ct);
                    if (cashSession is null)
                    {
                        return Result<CreateSaleReturnResult>.Failure(
                            "cash.session_required",
                            "An open cash session is required for a cash refund.");
                    }
                }
                if (cashSession is not null)
                {
                    _cash.AddMovement(new CashMovement
                    {
                        CashSessionId = cashSession.Id,
                        MovementType = CashMovementType.SaleRefundCashOut,
                        Direction = CashMovementDirection.Out,
                        Amount = saleReturn.RefundAmount,
                        SourceType = "SALE_RETURN",
                        SourceId = saleReturn.Id,
                        ActorId = command.CreatedBy,
                        OccurredAt = _clock.UtcNow,
                        Reason = saleReturn.ReturnNumber
                    });
                }

                _audit.Record(
                    "SALE_RETURN_COMPLETED",
                    "SALE_RETURN",
                    saleReturn.Id,
                    command.CreatedBy,
                    command.ClientOperationId,
                    $"Return {saleReturn.ReturnNumber}; sale {sale.InvoiceNumber}; refund {saleReturn.RefundAmount:0.00}; method {command.RefundMethod}.");

                await _unitOfWork.SaveChangesAsync(ct);

                return Result<CreateSaleReturnResult>.Success(new(
                    saleReturn.Id,
                    saleReturn.ReturnNumber,
                    saleReturn.RefundAmount,
                    false));
            }
            catch (BusinessRuleException ex)
            {
                return Result<CreateSaleReturnResult>.Failure(ex.Code, ex.Message);
            }
        }, cancellationToken);
    }

    private async Task<IReadOnlyList<(InventoryUnit Unit, SaleItemUnit Snapshot)>>
        PrepareSerializedUnitsAsync(
            SaleItem item,
            SaleReturnLineInput input,
            decimal baseQuantity,
            IReadOnlyList<SaleItemUnit> soldUnits,
            CancellationToken ct)
    {
        if (!QuantityMath.IsWhole(baseQuantity) ||
            input.InventoryUnitIds.Count != decimal.ToInt32(baseQuantity) ||
            input.InventoryUnitIds.Distinct().Count() != input.InventoryUnitIds.Count)
        {
            throw new BusinessRuleException(
                "sales.return_serial_count_mismatch",
                "Serialized return requires one distinct sold unit per base unit.");
        }

        var soldById = soldUnits.ToDictionary(x => x.InventoryUnitId);
        if (input.InventoryUnitIds.Any(x => !soldById.ContainsKey(x)))
        {
            throw new BusinessRuleException(
                "sales.return_serial_not_original",
                "One or more serialized units were not sold on this sale item.");
        }

        var priorReturned = await _sales.GetReturnedInventoryUnitIdsAsync(item.Id, ct);
        if (input.InventoryUnitIds.Any(priorReturned.Contains))
        {
            throw new BusinessRuleException(
                "sales.return_serial_already_returned",
                "One or more serialized units were already returned.");
        }

        var units = await _inventory.GetInventoryUnitsForUpdateAsync(
            item.ProductId,
            input.InventoryUnitIds,
            ct);

        if (units.Count != input.InventoryUnitIds.Count ||
            units.Any(x => x.Status != InventoryUnitStatus.Sold))
        {
            throw new BusinessRuleException(
                "sales.return_serial_not_eligible",
                "One or more serialized units are not currently eligible for return.");
        }

        return units
            .OrderBy(x => x.Id)
            .Select(x => (x, soldById[x.Id]))
            .ToArray();
    }

    private async Task<IReadOnlyList<OriginalLotReturnAllocation>>
        ResolveOriginalLotAllocationsAsync(
            SaleItem item,
            decimal priorReturnedBaseQuantity,
            decimal currentBaseQuantity,
            CancellationToken ct)
    {
        var consumptions = await _inventory.GetMovementLotConsumptionsAsync(
            item.InventoryMovementId,
            ct);

        if (consumptions.Count == 0)
        {
            throw new BusinessRuleException(
                "sales.return_consumption_missing",
                "Original sale lot-consumption history was not found.");
        }

        var toSkip = QuantityMath.RoundQuantity(priorReturnedBaseQuantity);
        var remaining = QuantityMath.RoundQuantity(currentBaseQuantity);
        var result = new List<OriginalLotReturnAllocation>();

        foreach (var consumption in consumptions)
        {
            var available = QuantityMath.RoundQuantity(consumption.Quantity);

            if (toSkip > 0)
            {
                var skipped = QuantityMath.RoundQuantity(Math.Min(available, toSkip));
                available = QuantityMath.RoundQuantity(available - skipped);
                toSkip = QuantityMath.RoundQuantity(toSkip - skipped);
            }

            if (available <= 0 || remaining <= 0)
            {
                continue;
            }

            var take = QuantityMath.RoundQuantity(Math.Min(available, remaining));
            var lot = await _inventory.GetInventoryLotForUpdateAsync(
                consumption.LotId,
                ct)
                ?? throw new BusinessRuleException(
                    "sales.return_origin_lot_missing",
                    "An original sale inventory lot no longer exists.");

            result.Add(new OriginalLotReturnAllocation(
                lot.Id,
                lot.PurchaseItemId,
                take));

            remaining = QuantityMath.RoundQuantity(remaining - take);
        }

        if (toSkip > 0 || remaining > 0)
        {
            throw new BusinessRuleException(
                "sales.return_consumption_incomplete",
                "Original sale lot-consumption history cannot cover this return.");
        }

        return result;
    }

    private async Task RestoreQuantityReturnLotsAsync(
        SaleItem item,
        IReadOnlyList<OriginalLotReturnAllocation> allocations,
        SaleReturnDisposition disposition,
        decimal originalCostAmount,
        InventoryMovement movement,
        CancellationToken ct)
    {
        var totalQuantity = QuantityMath.RoundQuantity(
            allocations.Sum(x => x.Quantity));

        if (totalQuantity <= 0)
        {
            throw new BusinessRuleException(
                "sales.return_origin_allocation_missing",
                "Return origin allocation is required.");
        }

        var unitCost = Cost(originalCostAmount / totalQuantity);
        var destination = SaleMath.ToInventoryBucket(disposition);

        foreach (var allocation in allocations)
        {
            if (disposition == SaleReturnDisposition.Scrap)
            {
                await _costs.AddZeroCarryingLotAsync(
                    item.ProductId,
                    allocation.Quantity,
                    unitCost,
                    movement.Id,
                    allocation.PurchaseItemId,
                    InventoryBucket.Scrap,
                    ct);
            }
            else
            {
                await _costs.AddCarryingValueAndLotWithIdAsync(
                    item.ProductId,
                    allocation.Quantity,
                    unitCost,
                    movement.Id,
                    allocation.PurchaseItemId,
                    destination,
                    ct);
            }
        }
    }

    private async Task RestoreSerializedUnitsAsync(
        IReadOnlyList<(InventoryUnit Unit, SaleItemUnit Snapshot)> units,
        SaleReturnDisposition disposition,
        InventoryMovement movement,
        CancellationToken ct)
    {
        foreach (var pair in units)
        {
            var destination = SaleMath.ToInventoryBucket(disposition);
            Guid lotId;

            if (disposition == SaleReturnDisposition.Scrap)
            {
                lotId = await _costs.AddZeroCarryingLotAsync(
                    pair.Unit.ProductId,
                    1m,
                    pair.Snapshot.UnitCostSnapshot,
                    movement.Id,
                    pair.Unit.SourcePurchaseItemId,
                    InventoryBucket.Scrap,
                    ct);
            }
            else
            {
                lotId = await _costs.AddCarryingValueAndLotWithIdAsync(
                    pair.Unit.ProductId,
                    1m,
                    pair.Snapshot.UnitCostSnapshot,
                    movement.Id,
                    pair.Unit.SourcePurchaseItemId,
                    destination,
                    ct);
            }

            var from = pair.Unit.Status;
            pair.Unit.Status = disposition switch
            {
                SaleReturnDisposition.RestockSellable => InventoryUnitStatus.InStock,
                SaleReturnDisposition.Damaged => InventoryUnitStatus.Damaged,
                SaleReturnDisposition.Defective => InventoryUnitStatus.Defective,
                SaleReturnDisposition.Scrap => InventoryUnitStatus.Scrapped,
                _ => throw new ArgumentOutOfRangeException(nameof(disposition))
            };
            pair.Unit.InventoryLotId = lotId;
            pair.Unit.Version++;

            _inventory.AddMovementUnit(new InventoryMovementUnit
            {
                MovementId = movement.Id,
                InventoryUnitId = pair.Unit.Id,
                FromStatus = from,
                ToStatus = pair.Unit.Status
            });
        }
    }

    private static decimal CalculateResidualAmount(
        decimal originalTotal,
        decimal originalBaseQuantity,
        decimal priorReturnedBaseQuantity,
        decimal currentBaseQuantity,
        decimal priorAmount,
        int decimals)
    {
        var cumulativeQty = QuantityMath.RoundQuantity(
            priorReturnedBaseQuantity + currentBaseQuantity);

        var targetCumulative = cumulativeQty == originalBaseQuantity
            ? originalTotal
            : decimal.Round(
                originalTotal * cumulativeQty / originalBaseQuantity,
                decimals,
                MidpointRounding.AwayFromZero);

        var current = decimal.Round(
            targetCumulative - priorAmount,
            decimals,
            MidpointRounding.AwayFromZero);

        if (current < 0)
        {
            throw new BusinessRuleException(
                "sales.return_residual_negative",
                "Return residual amount cannot be negative.");
        }

        return current;
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

