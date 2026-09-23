using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Sales;

namespace EdgeRetails.Application.Features.Sales;

public sealed record CommercialExchangeCommand(
    Guid ClientOperationId,
    Guid OriginalSaleId,
    Guid CashierUserId,
    Guid? SessionId,
    Guid? CustomerId,
    string ReturnReasonCode,
    string? ReturnReasonNote,
    IReadOnlyList<SaleReturnLineInput> ReturnLines,
    IReadOnlyList<CompleteSaleLineInput> ReplacementLines,
    decimal InvoiceDiscount,
    SalePaymentMethod SettlementMethod,
    decimal AmountTendered,
    string? PaymentReference);

public sealed record CommercialExchangeResult(
    Guid SaleId,
    string InvoiceNumber,
    Guid SaleReturnId,
    string ReturnNumber,
    decimal ReplacementGrandTotal,
    decimal ReturnRefundAmount,
    decimal NetDifference,
    decimal ChangeGiven,
    bool WasExisting);

public sealed class CommercialExchangeHandler
{
    private readonly ISalesRepository _sales;
    private readonly IPartyRepository _parties;
    private readonly ICatalogRepository _catalog;
    private readonly IInventoryRepository _inventory;
    private readonly IInventoryCostAllocator _costs;
    private readonly ICashRepository _cash;
    private readonly IOperationLock _operationLock;
    private readonly IResourceLock _resourceLock;
    private readonly IBusinessAuditWriter _audit;
    private readonly IReceiptSnapshotProvider _receiptSnapshots;
    private readonly IDocumentNumberService _numbers;
    private readonly IClock _clock;
    private readonly ITransactionRunner _transactions;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly IUnitOfWork _unitOfWork;

    public CommercialExchangeHandler(
        ISalesRepository sales,
        IPartyRepository parties,
        ICatalogRepository catalog,
        IInventoryRepository inventory,
        IInventoryCostAllocator costs,
        ICashRepository cash,
        IOperationLock operationLock,
        IResourceLock resourceLock,
        IBusinessAuditWriter audit,
        IReceiptSnapshotProvider receiptSnapshots,
        IDocumentNumberService numbers,
        IClock clock,
        ITransactionRunner transactions,
        IApplicationPermissionAuthorizer authorization,
        IUnitOfWork unitOfWork)
    {
        _sales = sales;
        _parties = parties;
        _catalog = catalog;
        _inventory = inventory;
        _costs = costs;
        _cash = cash;
        _operationLock = operationLock;
        _resourceLock = resourceLock;
        _audit = audit;
        _receiptSnapshots = receiptSnapshots;
        _numbers = numbers;
        _clock = clock;
        _transactions = transactions;
        _authorization = authorization;
        _unitOfWork = unitOfWork;
    }

    public Task<Result<CommercialExchangeResult>> HandleAsync(
        CommercialExchangeCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ClientOperationId == Guid.Empty ||
            command.ReturnLines.Count == 0 ||
            command.ReplacementLines.Count == 0 ||
            string.IsNullOrWhiteSpace(command.ReturnReasonCode))
        {
            return Task.FromResult(Result<CommercialExchangeResult>.Failure(
                "sales.exchange_invalid",
                "Commercial exchange requires an operation id, return reason, return lines, and replacement lines."));
        }

        if (command.ReturnLines.GroupBy(x => x.SaleItemId).Any(g => g.Count() > 1))
        {
            return Task.FromResult(Result<CommercialExchangeResult>.Failure(
                "sales.return_duplicate_item",
                "A sale item may appear only once in the return side of an exchange."));
        }

        if (command.ReplacementLines.GroupBy(x => new { x.ProductId, x.ProductUnitId }).Any(g => g.Count() > 1))
        {
            return Task.FromResult(Result<CommercialExchangeResult>.Failure(
                "sales.duplicate_line",
                "The same product and unit may appear only once in the replacement cart."));
        }

        return _transactions.ExecuteAsync(async ct =>
        {
            var authorization = await _authorization.AuthorizeAsync(
                command.CashierUserId,
                PermissionKeys.SalesCreate,
                ct);
            if (!authorization.IsSuccess)
            {
                return Result<CommercialExchangeResult>.Failure(
                    authorization.Error!.Code,
                    authorization.Error.Message);
            }

            await _operationLock.AcquireAsync(command.ClientOperationId, ct);

            var existingSale = await _sales.GetSaleByClientOperationIdAsync(command.ClientOperationId, ct);
            var existingReturn = await _sales.GetReturnByClientOperationIdAsync(command.ClientOperationId, ct);
            if (existingSale is not null && existingReturn is not null)
            {
                var payment = await _sales.GetSalePaymentAsync(existingSale.Id, ct);
                var netDiff = Money(existingSale.GrandTotal - existingReturn.RefundAmount);
                return Result<CommercialExchangeResult>.Success(new(
                    existingSale.Id,
                    existingSale.InvoiceNumber,
                    existingReturn.Id,
                    existingReturn.ReturnNumber,
                    existingSale.GrandTotal,
                    existingReturn.RefundAmount,
                    netDiff,
                    payment?.ChangeGiven ?? 0m,
                    true));
            }

            try
            {
                var originalSale = await _sales.GetSaleForUpdateAsync(command.OriginalSaleId, ct);
                if (originalSale is null || originalSale.Status != SaleStatus.Completed)
                {
                    return Result<CommercialExchangeResult>.Failure(
                        "sales.sale_not_returnable",
                        "Original sale was not found or is not returnable.");
                }

                var originalSaleItems = await _sales.GetSaleItemsAsync(originalSale.Id, ct);
                var returnItemIds = command.ReturnLines.Select(x => x.SaleItemId).ToHashSet();
                var requestedSaleItems = originalSaleItems.Where(x => returnItemIds.Contains(x.Id)).ToArray();
                if (requestedSaleItems.Length != command.ReturnLines.Count)
                {
                    return Result<CommercialExchangeResult>.Failure(
                        "sales.return_item_invalid",
                        "One or more return items do not belong to the original sale.");
                }

                var effectiveCustomerId = originalSale.CustomerId ?? command.CustomerId;
                if (effectiveCustomerId is not null)
                {
                    var customer = await _parties.GetCustomerAsync(effectiveCustomerId.Value, ct);
                    if (customer is null || !customer.IsActive)
                    {
                        return Result<CommercialExchangeResult>.Failure(
                            "sales.customer_not_active",
                            "Customer was not found or is inactive.");
                    }
                }

                // Deterministic Section 185 Resource Locking
                var allProductIds = requestedSaleItems.Select(x => x.ProductId)
                    .Concat(command.ReplacementLines.Select(x => x.ProductId))
                    .Distinct()
                    .OrderBy(x => x)
                    .ToArray();

                foreach (var productId in allProductIds)
                {
                    await _resourceLock.AcquireAsync("product", productId, ct);
                }

                var allUnitIds = command.ReturnLines.SelectMany(x => x.InventoryUnitIds)
                    .Concat(command.ReplacementLines.SelectMany(x => x.InventoryUnitIds))
                    .Distinct()
                    .OrderBy(x => x)
                    .ToArray();

                foreach (var unitId in allUnitIds)
                {
                    await _resourceLock.AcquireAsync("inventory-unit", unitId, ct);
                }

                // --- 1. PROCESS RETURN SIDE ---
                var saleReturn = new SaleReturn
                {
                    ReturnNumber = await _numbers.NextAsync("SR", ct),
                    SaleId = originalSale.Id,
                    ReasonCode = command.ReturnReasonCode.Trim().ToUpperInvariant(),
                    ReasonNote = Normalize(command.ReturnReasonNote),
                    RefundMethod = RefundMethod.Other,
                    CreatedBy = command.CashierUserId,
                    CreatedAt = _clock.UtcNow,
                    ClientOperationId = command.ClientOperationId
                };
                _sales.AddReturn(saleReturn);

                decimal totalReturnRefund = 0m;

                foreach (var returnInput in command.ReturnLines.OrderBy(x => x.SaleItemId))
                {
                    var item = await _sales.GetSaleItemForUpdateAsync(returnInput.SaleItemId, ct);
                    if (item is null || item.SaleId != originalSale.Id)
                    {
                        return Result<CommercialExchangeResult>.Failure(
                            "sales.return_item_invalid",
                            "Sale return item does not belong to the original sale.");
                    }

                    var baseQuantity = QuantityMath.RoundQuantity(returnInput.BaseQuantity);
                    if (baseQuantity <= 0)
                    {
                        return Result<CommercialExchangeResult>.Failure(
                            "sales.return_quantity_positive",
                            "Return base quantity must be greater than zero.");
                    }

                    var priorQty = await _sales.GetReturnedBaseQuantityAsync(item.Id, ct);
                    var cumulativeQty = QuantityMath.RoundQuantity(priorQty + baseQuantity);
                    if (cumulativeQty > item.BaseQuantity)
                    {
                        return Result<CommercialExchangeResult>.Failure(
                            "sales.return_exceeds_original",
                            "Return quantity exceeds the remaining sold quantity.");
                    }

                    if (await _inventory.IsProductBlockedByCountingStocktakeAsync(item.ProductId, ct))
                    {
                        return Result<CommercialExchangeResult>.Failure(
                            "inventory.stocktake_blocks_product",
                            "Sale return is blocked by an active stocktake.");
                    }

                    var stock = await _inventory.GetStockBalanceForUpdateAsync(item.ProductId, ct);
                    if (stock is null)
                    {
                        return Result<CommercialExchangeResult>.Failure(
                            "sales.stock_missing",
                            "Stock balance was not found for the returned product.");
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
                    var isSerialized = saleItemUnits.Count > 0;

                    decimal originalCostAmount;
                    IReadOnlyList<(InventoryUnit Unit, SaleItemUnit Snapshot)> serializedUnits =
                        Array.Empty<(InventoryUnit, SaleItemUnit)>();
                    IReadOnlyList<OriginalLotReturnAllocation> originAllocations =
                        Array.Empty<OriginalLotReturnAllocation>();

                    if (isSerialized)
                    {
                        var prepared = await PrepareSerializedReturnUnitsAsync(
                            item,
                            returnInput,
                            baseQuantity,
                            saleItemUnits,
                            ct);
                        serializedUnits = prepared;
                        originalCostAmount = Cost(prepared.Sum(x => x.Snapshot.UnitCostSnapshot));
                    }
                    else
                    {
                        if (returnInput.InventoryUnitIds.Count > 0)
                        {
                            return Result<CommercialExchangeResult>.Failure(
                                "sales.return_serials_not_allowed",
                                "Non-serialized sale items cannot return inventory-unit ids.");
                        }

                        var priorCost = await _sales.GetReturnedOriginalCostAmountAsync(item.Id, ct);
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

                    var destination = SaleMath.ToInventoryBucket(returnInput.Disposition);
                    var movement = new InventoryMovement
                    {
                        ProductId = item.ProductId,
                        MovementType = InventoryMovementType.SaleReturn,
                        ReferenceType = "SALE_RETURN",
                        ReferenceId = saleReturn.Id,
                        UnitCostSnapshot = Cost(originalCostAmount / baseQuantity),
                        RecognizedLossAmount = returnInput.Disposition == SaleReturnDisposition.Scrap
                            ? Money(originalCostAmount)
                            : 0m,
                        ActorId = command.CashierUserId,
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

                    if (isSerialized)
                    {
                        await RestoreSerializedUnitsAsync(
                            serializedUnits,
                            returnInput.Disposition,
                            movement,
                            ct);
                    }
                    else
                    {
                        await RestoreQuantityReturnLotsAsync(
                            item,
                            originAllocations,
                            returnInput.Disposition,
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
                        Disposition = returnInput.Disposition,
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

                    totalReturnRefund += refundAmount;
                }

                saleReturn.RefundAmount = Money(totalReturnRefund);

                // --- 2. PROCESS REPLACEMENT SALE SIDE ---
                var replacementProductIds = command.ReplacementLines.Select(x => x.ProductId).Distinct().OrderBy(x => x).ToArray();
                var products = new Dictionary<Guid, Product>();
                var stocks = new Dictionary<Guid, StockBalance>();

                foreach (var pid in replacementProductIds)
                {
                    var product = await _catalog.GetProductForUpdateAsync(pid, ct);
                    if (product is null || !product.IsActive)
                    {
                        return Result<CommercialExchangeResult>.Failure(
                            "sales.product_not_active",
                            "One or more replacement products are missing or inactive.");
                    }

                    if (await _inventory.IsProductBlockedByCountingStocktakeAsync(pid, ct))
                    {
                        return Result<CommercialExchangeResult>.Failure(
                            "inventory.stocktake_blocks_product",
                            $"Product '{product.Name}' is locked by an active stocktake.");
                    }

                    var stock = await _inventory.GetStockBalanceForUpdateAsync(pid, ct);
                    if (stock is null)
                    {
                        return Result<CommercialExchangeResult>.Failure(
                            "sales.stock_missing",
                            $"Stock balance for '{product.Name}' was not found.");
                    }

                    products[pid] = product;
                    stocks[pid] = stock;
                }

                var preparedSaleLines = await PrepareSaleLinesAsync(command.ReplacementLines, products, ct);

                foreach (var group in preparedSaleLines.GroupBy(x => x.Product.Id))
                {
                    var requested = QuantityMath.RoundQuantity(group.Sum(x => x.Quantity.BaseQuantity));
                    if (stocks[group.Key].SellableQty < requested)
                    {
                        return Result<CommercialExchangeResult>.Failure(
                            "sales.insufficient_stock",
                            $"Insufficient sellable stock for '{group.First().Product.Name}'.");
                    }
                }

                var subtotal = Money(preparedSaleLines.Sum(x => x.GrossLineTotal));
                var discount = Money(command.InvoiceDiscount);
                var allocations = SaleMath.AllocateInvoiceDiscount(
                    preparedSaleLines.Select(x => x.GrossLineTotal).ToArray(),
                    discount);
                var replacementGrandTotal = Money(subtotal - discount);

                var receiptSnapshot = await _receiptSnapshots.CaptureAsync(ct);

                var replacementSale = new Sale
                {
                    InvoiceNumber = await _numbers.NextAsync("SALE", ct),
                    CustomerId = effectiveCustomerId,
                    CashierUserId = command.CashierUserId,
                    SessionId = command.SessionId,
                    CompletedAt = _clock.UtcNow,
                    Subtotal = subtotal,
                    InvoiceDiscount = discount,
                    GrandTotal = replacementGrandTotal,
                    Status = SaleStatus.Completed,
                    PaymentStatus = SalePaymentStatus.Paid,
                    ClientOperationId = command.ClientOperationId,
                    ReceiptTemplateSnapshot = receiptSnapshot,
                    CreatedAt = _clock.UtcNow
                };
                _sales.AddSale(replacementSale);

                for (var i = 0; i < preparedSaleLines.Count; i++)
                {
                    var line = preparedSaleLines[i];
                    var stock = stocks[line.Product.Id];
                    var before = stock.SellableQty;

                    var movement = new InventoryMovement
                    {
                        ProductId = line.Product.Id,
                        MovementType = InventoryMovementType.SaleOut,
                        ReferenceType = "SALE",
                        ReferenceId = replacementSale.Id,
                        ActorId = command.CashierUserId,
                        OccurredAt = _clock.UtcNow,
                        CorrelationId = command.ClientOperationId,
                        Note = replacementSale.InvoiceNumber
                    };
                    _inventory.AddMovement(movement);

                    decimal totalCost;
                    if (line.Product.TrackingMode == TrackingMode.Serialized)
                    {
                        totalCost = await ConsumeSerializedSaleAsync(line, movement, ct);
                    }
                    else
                    {
                        totalCost = await _costs.RemoveCarryingValueAsync(
                            line.Product.Id,
                            line.Quantity.BaseQuantity,
                            null,
                            ct);
                        var saleUnitCost = Cost(totalCost / line.Quantity.BaseQuantity);
                        await _costs.ConsumeBucketAsync(
                            line.Product.Id,
                            InventoryBucket.Sellable,
                            line.Quantity.BaseQuantity,
                            movement.Id,
                            saleUnitCost,
                            ct);
                    }

                    stock.ApplyDelta(InventoryBucket.Sellable, -line.Quantity.BaseQuantity);
                    var unitCost = Cost(totalCost / line.Quantity.BaseQuantity);
                    movement.UnitCostSnapshot = unitCost;

                    _inventory.AddMovementEffect(new InventoryMovementEffect
                    {
                        MovementId = movement.Id,
                        StockBucket = InventoryBucket.Sellable,
                        QuantityDelta = -line.Quantity.BaseQuantity,
                        QuantityBefore = before,
                        QuantityAfter = stock.SellableQty
                    });

                    var allocatedDiscount = allocations[i];
                    var netLineTotal = Money(line.GrossLineTotal - allocatedDiscount);
                    var saleItem = new SaleItem
                    {
                        SaleId = replacementSale.Id,
                        InventoryMovementId = movement.Id,
                        ProductId = line.Product.Id,
                        ProductUnitId = line.ProductUnit.Id,
                        ProductNameSnapshot = line.Product.Name,
                        SkuSnapshot = line.Product.Sku,
                        EnteredQuantity = line.Quantity.EnteredQuantity,
                        FactorToBaseSnapshot = line.Quantity.FactorToBaseSnapshot,
                        BaseQuantity = line.Quantity.BaseQuantity,
                        UnitPrice = line.UnitPrice,
                        GrossLineTotal = line.GrossLineTotal,
                        AllocatedInvoiceDiscount = allocatedDiscount,
                        NetLineTotal = netLineTotal,
                        UnitCostSnapshot = unitCost,
                        TotalCostSnapshot = Cost(totalCost),
                        GrossProfitSnapshot = Money(netLineTotal - totalCost),
                        WarrantyValidUntil = line.Product.DefaultWarrantyMonths > 0
                            ? _clock.ShopDate.AddMonths(line.Product.DefaultWarrantyMonths)
                            : null
                    };
                    _sales.AddSaleItem(saleItem);

                    foreach (var unit in line.SerializedUnits)
                    {
                        _sales.AddSaleItemUnit(new SaleItemUnit
                        {
                            SaleItemId = saleItem.Id,
                            InventoryUnitId = unit.Id,
                            UnitCostSnapshot = unit.AcquisitionCost,
                            WarrantyValidUntil = saleItem.WarrantyValidUntil
                        });
                    }
                }

                // --- 3. RECONCILE NET DIFFERENCE ---
                var netDifference = Money(replacementGrandTotal - totalReturnRefund);
                decimal changeGiven = 0m;

                CashSession? cashSession = null;
                if (command.SettlementMethod == SalePaymentMethod.Cash && netDifference != 0)
                {
                    cashSession = await _cash.GetOpenSessionForUpdateAsync(ct);
                    if (cashSession is null)
                    {
                        return Result<CommercialExchangeResult>.Failure(
                            "cash.session_required",
                            "An open cash session is required for cash exchange difference settlement.");
                    }
                }

                if (netDifference > 0)
                {
                    // Customer owes net difference to shop
                    var tendered = Money(command.AmountTendered);
                    if (tendered < 0)
                    {
                        return Result<CommercialExchangeResult>.Failure(
                            "sales.payment_negative",
                            "Amount tendered cannot be negative.");
                    }

                    if (command.SettlementMethod == SalePaymentMethod.Cash)
                    {
                        if (tendered < netDifference)
                        {
                            return Result<CommercialExchangeResult>.Failure(
                                "sales.cash_insufficient",
                                $"Cash tendered ({tendered:0.00}) must cover the net difference ({netDifference:0.00}).");
                        }

                        changeGiven = Money(tendered - netDifference);

                        _cash.AddMovement(new CashMovement
                        {
                            CashSessionId = cashSession!.Id,
                            MovementType = CashMovementType.SaleCashIn,
                            Direction = CashMovementDirection.In,
                            Amount = netDifference,
                            SourceType = "COMMERCIAL_EXCHANGE",
                            SourceId = replacementSale.Id,
                            ActorId = command.CashierUserId,
                            OccurredAt = _clock.UtcNow,
                            Reason = $"Exchange net cash in for {replacementSale.InvoiceNumber}"
                        });
                    }
                    else
                    {
                        if (tendered != netDifference)
                        {
                            return Result<CommercialExchangeResult>.Failure(
                                "sales.non_cash_exact",
                                $"Non-cash payment ({tendered:0.00}) must equal the exact net difference ({netDifference:0.00}).");
                        }
                    }

                    _sales.AddSalePayment(new SalePayment
                    {
                        SaleId = replacementSale.Id,
                        Method = command.SettlementMethod,
                        AmountTendered = tendered,
                        AppliedAmount = replacementGrandTotal,
                        ChangeGiven = changeGiven,
                        Reference = command.PaymentReference?.Trim()
                    });

                    saleReturn.RefundMethod = RefundMethod.Other;
                }
                else if (netDifference < 0)
                {
                    // Shop owes net difference refund to customer
                    var refundDue = Money(-netDifference);

                    if (command.SettlementMethod == SalePaymentMethod.Cash)
                    {
                        _cash.AddMovement(new CashMovement
                        {
                            CashSessionId = cashSession!.Id,
                            MovementType = CashMovementType.SaleRefundCashOut,
                            Direction = CashMovementDirection.Out,
                            Amount = refundDue,
                            SourceType = "COMMERCIAL_EXCHANGE",
                            SourceId = saleReturn.Id,
                            ActorId = command.CashierUserId,
                            OccurredAt = _clock.UtcNow,
                            Reason = $"Exchange net refund out for {saleReturn.ReturnNumber}"
                        });

                        saleReturn.RefundMethod = RefundMethod.Cash;
                    }
                    else
                    {
                        saleReturn.RefundMethod = RefundMethod.Bank;
                    }

                    _sales.AddSalePayment(new SalePayment
                    {
                        SaleId = replacementSale.Id,
                        Method = SalePaymentMethod.Other,
                        AmountTendered = replacementGrandTotal,
                        AppliedAmount = replacementGrandTotal,
                        ChangeGiven = 0m,
                        Reference = command.PaymentReference?.Trim()
                    });
                }
                else
                {
                    // Exact even swap
                    _sales.AddSalePayment(new SalePayment
                    {
                        SaleId = replacementSale.Id,
                        Method = SalePaymentMethod.Other,
                        AmountTendered = replacementGrandTotal,
                        AppliedAmount = replacementGrandTotal,
                        ChangeGiven = 0m,
                        Reference = "EVEN_EXCHANGE"
                    });

                    saleReturn.RefundMethod = RefundMethod.Other;
                }

                _audit.Record(
                    "COMMERCIAL_EXCHANGE_COMPLETED",
                    "COMMERCIAL_EXCHANGE",
                    replacementSale.Id,
                    command.CashierUserId,
                    command.ClientOperationId,
                    $"Exchange: Sale {replacementSale.InvoiceNumber} (Total: {replacementGrandTotal:0.00}) vs Return {saleReturn.ReturnNumber} (Credit: {totalReturnRefund:0.00}). Net Difference: {netDifference:0.00}.");

                await _unitOfWork.SaveChangesAsync(ct);

                return Result<CommercialExchangeResult>.Success(new(
                    replacementSale.Id,
                    replacementSale.InvoiceNumber,
                    saleReturn.Id,
                    saleReturn.ReturnNumber,
                    replacementGrandTotal,
                    totalReturnRefund,
                    netDifference,
                    changeGiven,
                    false));
            }
            catch (BusinessRuleException ex)
            {
                return Result<CommercialExchangeResult>.Failure(ex.Code, ex.Message);
            }
        }, cancellationToken);
    }

    private async Task<IReadOnlyList<PreparedSaleLine>> PrepareSaleLinesAsync(
        IReadOnlyList<CompleteSaleLineInput> inputs,
        IReadOnlyDictionary<Guid, Product> products,
        CancellationToken ct)
    {
        var result = new List<PreparedSaleLine>(inputs.Count);
        var selectedSerializedIds = new HashSet<Guid>();

        foreach (var input in inputs.OrderBy(x => x.ProductId).ThenBy(x => x.ProductUnitId))
        {
            var product = products[input.ProductId];
            var productUnit = await _catalog.GetProductUnitAsync(input.ProductUnitId, ct);
            if (productUnit is null ||
                productUnit.ProductId != product.Id ||
                !productUnit.IsActive ||
                !productUnit.CanSell)
            {
                throw new BusinessRuleException(
                    "sales.product_unit_not_allowed",
                    $"Selected unit is not allowed for '{product.Name}'.");
            }

            var quantity = TransactionQuantitySnapshot.Create(
                productUnit,
                input.EnteredQuantity,
                product.TrackingMode);

            var authoritativePrice = Money(product.DefaultSalePrice * productUnit.FactorToBaseUnit);
            if (Money(input.ExpectedUnitPrice) != authoritativePrice)
            {
                throw new BusinessRuleException(
                    "sales.price_changed",
                    $"Price changed for '{product.Name}'. Recalculate the cart.");
            }

            IReadOnlyList<InventoryUnit> serializedUnits = Array.Empty<InventoryUnit>();
            if (product.TrackingMode == TrackingMode.Serialized)
            {
                if (!QuantityMath.IsWhole(quantity.BaseQuantity) ||
                    input.InventoryUnitIds.Count != decimal.ToInt32(quantity.BaseQuantity) ||
                    input.InventoryUnitIds.Distinct().Count() != input.InventoryUnitIds.Count)
                {
                    throw new BusinessRuleException(
                        "sales.serial_count_mismatch",
                        $"Select one exact serialized unit per base unit for '{product.Name}'.");
                }

                foreach (var id in input.InventoryUnitIds)
                {
                    if (!selectedSerializedIds.Add(id))
                    {
                        throw new BusinessRuleException(
                            "sales.serial_selected_twice",
                            "A serialized unit cannot be selected twice in one sale.");
                    }
                }

                var units = await _inventory.GetInventoryUnitsForUpdateAsync(
                    product.Id,
                    input.InventoryUnitIds,
                    ct);
                if (units.Count != input.InventoryUnitIds.Count ||
                    units.Any(x => x.Status != InventoryUnitStatus.InStock || x.InventoryLotId is null))
                {
                    throw new BusinessRuleException(
                        "sales.serial_not_sellable",
                        $"One or more serialized units for '{product.Name}' are not sellable.");
                }

                serializedUnits = units.OrderBy(x => x.Id).ToArray();
            }
            else if (input.InventoryUnitIds.Count > 0)
            {
                throw new BusinessRuleException(
                    "sales.serials_not_allowed",
                    $"Product '{product.Name}' is not serialized.");
            }

            result.Add(new PreparedSaleLine(
                input,
                product,
                productUnit,
                quantity,
                authoritativePrice,
                Money(quantity.EnteredQuantity * authoritativePrice),
                serializedUnits));
        }

        return result;
    }

    private async Task<decimal> ConsumeSerializedSaleAsync(
        PreparedSaleLine line,
        InventoryMovement movement,
        CancellationToken ct)
    {
        decimal totalCost = 0m;
        foreach (var unit in line.SerializedUnits.OrderBy(x => x.Id))
        {
            var lotBalance = await _inventory.GetLotBucketBalanceForUpdateAsync(
                unit.InventoryLotId!.Value,
                InventoryBucket.Sellable,
                ct)
                ?? throw new BusinessRuleException(
                    "sales.serial_lot_missing",
                    "Serialized unit inventory lot was not found.");

            if (lotBalance.Quantity < 1m)
            {
                throw new BusinessRuleException(
                    "sales.serial_lot_insufficient",
                    "Serialized unit inventory lot is no longer sellable.");
            }

            lotBalance.Quantity = QuantityMath.RoundQuantity(lotBalance.Quantity - 1m);

            _inventory.AddLotConsumption(new InventoryLotConsumption
            {
                LotId = unit.InventoryLotId.Value,
                MovementId = movement.Id,
                Quantity = 1m,
                UnitCostSnapshot = unit.AcquisitionCost,
                TotalCostSnapshot = unit.AcquisitionCost,
                OccurredAt = _clock.UtcNow
            });

            totalCost += await _costs.RemoveCarryingValueAsync(
                line.Product.Id,
                1m,
                unit.AcquisitionCost,
                ct);

            var from = unit.Status;
            unit.Status = InventoryUnitStatus.Sold;
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

    private async Task<IReadOnlyList<(InventoryUnit Unit, SaleItemUnit Snapshot)>> PrepareSerializedReturnUnitsAsync(
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

        return units.OrderBy(x => x.Id).Select(x => (x, soldById[x.Id])).ToArray();
    }

    private async Task<IReadOnlyList<OriginalLotReturnAllocation>> ResolveOriginalLotAllocationsAsync(
        SaleItem item,
        decimal priorReturnedBaseQuantity,
        decimal currentBaseQuantity,
        CancellationToken ct)
    {
        var consumptions = await _inventory.GetMovementLotConsumptionsAsync(item.InventoryMovementId, ct);
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
            var lot = await _inventory.GetInventoryLotForUpdateAsync(consumption.LotId, ct)
                ?? throw new BusinessRuleException(
                    "sales.return_origin_lot_missing",
                    "An original sale inventory lot no longer exists.");

            result.Add(new OriginalLotReturnAllocation(lot.Id, lot.PurchaseItemId, take));
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
        var totalQuantity = QuantityMath.RoundQuantity(allocations.Sum(x => x.Quantity));
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
        var cumulativeQty = QuantityMath.RoundQuantity(priorReturnedBaseQuantity + currentBaseQuantity);
        var targetCumulative = cumulativeQty == originalBaseQuantity
            ? originalTotal
            : decimal.Round(
                originalTotal * cumulativeQty / originalBaseQuantity,
                decimals,
                MidpointRounding.AwayFromZero);

        var current = decimal.Round(targetCumulative - priorAmount, decimals, MidpointRounding.AwayFromZero);
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
