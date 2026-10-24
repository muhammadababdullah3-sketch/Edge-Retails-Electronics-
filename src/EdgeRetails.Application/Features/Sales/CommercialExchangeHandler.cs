using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Posting;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.Warranty;

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
    string? PaymentReference,
    DateOnly? PostingDate = null,
    SupervisorApprovalRequest? SupervisorApproval = null,
    bool? CallerSuppliedBooleanOverride = null);

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
    private readonly IWarrantyRepository? _warranty;
    private readonly IOperationOutcomeLedger? _outcomeLedger;
    private readonly IPackSubLedgerAllocationAuthority _packAllocations;
    private readonly IReturnExchangeEligibilityAuthority _eligibility;
    private readonly IShopPostingValidationService? _postingValidation;

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
        IUnitOfWork unitOfWork,
        IWarrantyRepository? warranty = null,
        IOperationOutcomeLedger? outcomeLedger = null,
        IPackSubLedgerAllocationAuthority? packAllocations = null,
        IPhysicalUnitCreationAuthority? physicalUnits = null,
        ITraceabilityRepository? traceability = null,
        IReturnExchangeEligibilityAuthority? eligibility = null,
        IShopPostingValidationService? postingValidation = null)
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
        _warranty = warranty;
        _outcomeLedger = outcomeLedger;
        _postingValidation = postingValidation;
        _packAllocations = packAllocations ?? new PackSubLedgerAllocationAuthority();
        _eligibility = eligibility ?? new ReturnExchangeEligibilityAuthority(
            sales,
            inventory,
            catalog,
            costs,
            clock,
            warranty,
            physicalUnits,
            _packAllocations,
            traceability,
            parties);
    }

    public async Task<Result<CommercialExchangeResult>> HandleAsync(
        CommercialExchangeCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ClientOperationId == Guid.Empty ||
            command.ReturnLines.Count == 0 ||
            command.ReplacementLines.Count == 0 ||
            string.IsNullOrWhiteSpace(command.ReturnReasonCode))
        {
            return Result<CommercialExchangeResult>.Failure(
                "sales.exchange_invalid",
                "Commercial exchange requires an operation id, return reason, return lines, and replacement lines.");
        }

        if (command.ReturnLines.GroupBy(x => x.SaleItemId).Any(g => g.Count() > 1))
        {
            return Result<CommercialExchangeResult>.Failure(
                "sales.return_duplicate_item",
                "A sale item may appear only once in the return side of an exchange.");
        }

        if (command.ReplacementLines.GroupBy(x => new { x.ProductId, x.ProductUnitId }).Any(g => g.Count() > 1))
        {
            return Result<CommercialExchangeResult>.Failure(
                "sales.duplicate_line",
                "The same product and unit may appear only once in the replacement cart.");
        }

        var currentFingerprint = ComputePayloadFingerprint(command);

        if (_outcomeLedger is not null)
        {
            var preLockOutcome = await _outcomeLedger.GetOutcomeAsync(command.ClientOperationId, cancellationToken);
            if (preLockOutcome is not null)
            {
                var evaluated = await EvaluateGuardedOutcomeAsync(preLockOutcome, command, currentFingerprint, cancellationToken);
                if (evaluated.HasValue)
                {
                    return evaluated.Value;
                }
            }
        }

        var result = await _transactions.ExecuteAsync(async ct =>
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

            if (_outcomeLedger is not null)
            {
                var postLockOutcome = await _outcomeLedger.GetOutcomeAsync(command.ClientOperationId, ct);
                if (postLockOutcome is not null)
                {
                    var evaluated = await EvaluateGuardedOutcomeAsync(postLockOutcome, command, currentFingerprint, ct);
                    if (evaluated.HasValue)
                    {
                        return evaluated.Value;
                    }
                }
            }

            var existingSale = await _sales.GetSaleByClientOperationIdAsync(command.ClientOperationId, ct);
            var existingReturn = await _sales.GetReturnByClientOperationIdAsync(command.ClientOperationId, ct);

            if ((existingSale is not null) != (existingReturn is not null))
            {
                return Result<CommercialExchangeResult>.Failure(
                    "idempotency.operation_conflict",
                    "Operation was previously submitted under a conflicting partial transaction.");
            }

            if (existingSale is not null && existingReturn is not null)
            {
                if (existingSale.CashierUserId != command.CashierUserId)
                {
                    return Result<CommercialExchangeResult>.Failure(
                        "idempotency.context_mismatch",
                        "Operation actor mismatch.");
                }

                if (existingReturn.SaleId != command.OriginalSaleId)
                {
                    return Result<CommercialExchangeResult>.Failure(
                        "idempotency.payload_mismatch",
                        "The operation was previously executed with different parameters.");
                }

                if (_outcomeLedger is not null)
                {
                    await _outcomeLedger.RecordSuccessAsync(
                        command.ClientOperationId,
                        "CommercialExchange",
                        existingSale.Id,
                        existingSale.InvoiceNumber,
                        actorId: command.CashierUserId,
                        sessionId: command.SessionId,
                        payloadFingerprint: currentFingerprint,
                        cancellationToken: ct);
                }

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
                    var customer = await _parties.GetCustomerForUpdateAsync(effectiveCustomerId.Value, ct);
                    if (customer is null || !customer.IsActive)
                    {
                        return Result<CommercialExchangeResult>.Failure(
                            "sales.customer_not_active",
                            "Customer was not found or is inactive.");
                    }
                }

                // Deterministic Resource Locking in canonical order
                var allProductIds = requestedSaleItems.Select(x => x.ProductId)
                    .Concat(command.ReplacementLines.Select(x => x.ProductId))
                    .Distinct()
                    .OrderBy(x => x)
                    .ToArray();

                foreach (var productId in allProductIds)
                {
                    await _resourceLock.AcquireAsync("product", productId, ct);
                }

                var returnSaleItemIds = requestedSaleItems.Select(x => x.Id).Distinct().OrderBy(x => x).ToArray();
                foreach (var saleItemId in returnSaleItemIds)
                {
                    await _resourceLock.AcquireAsync("sale-item", saleItemId, ct);
                    await _resourceLock.AcquireAsync("warranty-sale-item", saleItemId, ct);
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

                var returnUnitIds = command.ReturnLines.SelectMany(x => x.InventoryUnitIds).Distinct().OrderBy(x => x).ToArray();
                foreach (var unitId in returnUnitIds)
                {
                    await _resourceLock.AcquireAsync("warranty-unit", unitId, ct);
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
                    var prepared = await _eligibility.PrepareReturnLineAsync(
                        returnInput,
                        originalSale.Id,
                        ct);

                    var destination = SaleMath.ToInventoryBucket(returnInput.Disposition);
                    var movement = new InventoryMovement
                    {
                        ProductId = prepared.Item.ProductId,
                        MovementType = InventoryMovementType.SaleReturn,
                        ReferenceType = "SALE_RETURN",
                        ReferenceId = saleReturn.Id,
                        UnitCostSnapshot = Cost(prepared.OriginalCostAmount / prepared.BaseQuantity),
                        RecognizedLossAmount = returnInput.Disposition == SaleReturnDisposition.Scrap
                            ? Money(prepared.OriginalCostAmount)
                            : 0m,
                        ActorId = command.CashierUserId,
                        OccurredAt = _clock.UtcNow,
                        CorrelationId = command.ClientOperationId,
                        Reason = saleReturn.ReasonCode,
                        Note = saleReturn.ReasonNote
                    };
                    _inventory.AddMovement(movement);

                    var before = prepared.Stock.Get(destination);
                    prepared.Stock.ApplyDelta(destination, prepared.BaseQuantity);
                    _inventory.AddMovementEffect(new InventoryMovementEffect
                    {
                        MovementId = movement.Id,
                        StockBucket = destination,
                        QuantityDelta = prepared.BaseQuantity,
                        QuantityBefore = before,
                        QuantityAfter = prepared.Stock.Get(destination)
                    });

                    var returnItemId = Guid.NewGuid();

                    await _eligibility.RestoreReturnLineAsync(
                        prepared,
                        returnItemId,
                        movement,
                        command.CashierUserId,
                        ct);

                    var enteredQuantity = QuantityMath.RoundQuantity(
                        prepared.BaseQuantity / prepared.Item.FactorToBaseSnapshot);

                    var returnItem = new SaleReturnItem
                    {
                        Id = returnItemId,
                        SaleReturnId = saleReturn.Id,
                        SaleItemId = prepared.Item.Id,
                        ProductId = prepared.Item.ProductId,
                        EnteredQuantity = enteredQuantity,
                        ProductUnitId = prepared.Item.ProductUnitId,
                        FactorToBaseSnapshot = prepared.Item.FactorToBaseSnapshot,
                        BaseQuantity = prepared.BaseQuantity,
                        Disposition = returnInput.Disposition,
                        RefundAmount = prepared.RefundAmount,
                        OriginalCostAmount = prepared.OriginalCostAmount,
                        CostReversalAmount = prepared.OriginalCostAmount
                    };
                    _sales.AddReturnItem(returnItem);

                    totalReturnRefund += prepared.RefundAmount;
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

                var preparedSaleLines = await PrepareSaleLinesAsync(command.ReplacementLines, products, command.CashierUserId, ct);

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
                    if (line.Product.TrackingMode is TrackingMode.Serialized or TrackingMode.IndividualPiece)
                    {
                        totalCost = await ConsumeSerializedSaleAsync(line, movement, ct);
                    }
                    else if (line.Product.TrackingMode == TrackingMode.Container && line.PackAllocation is not null)
                    {
                        totalCost = await ConsumePackAllocationAsync(line, line.PackAllocation, movement, ct);
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
                        ListUnitPriceSnapshot = line.ListUnitPrice,
                        UnitPrice = line.UnitPrice,
                        PriceOverrideReason = line.PriceOverrideReason,
                        PriceOverrideBy = line.PriceOverrideBy,
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

                    if (line.Product.TrackingMode == TrackingMode.Container && line.PackAllocation is not null)
                    {
                        foreach (var alloc in line.PackAllocation.Allocations)
                        {
                            alloc.MovementId = movement.Id;
                            alloc.SaleItemId = saleItem.Id;
                            alloc.ValidateInvariants();
                            _inventory.AddPackAllocation(alloc);

                            _sales.AddSaleItemUnit(new SaleItemUnit
                            {
                                SaleItemId = saleItem.Id,
                                InventoryUnitId = alloc.PackUnitId,
                                UnitCostSnapshot = alloc.TotalCostSnapshot,
                                WarrantyValidUntil = saleItem.WarrantyValidUntil
                            });
                        }
                    }
                    else
                    {
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
                        saleReturn.RefundMethod = command.SettlementMethod == SalePaymentMethod.Other
                            ? RefundMethod.Other
                            : RefundMethod.Bank;
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

                if (_outcomeLedger is not null)
                {
                    await _outcomeLedger.RecordSuccessAsync(
                        command.ClientOperationId,
                        "CommercialExchange",
                        replacementSale.Id,
                        replacementSale.InvoiceNumber,
                        actorId: command.CashierUserId,
                        sessionId: command.SessionId,
                        payloadFingerprint: currentFingerprint,
                        cancellationToken: ct);
                }

                if (_postingValidation is not null)
                {
                    var postingDate = command.PostingDate ?? _clock.ShopDate;
                    var postingValidationResult = await _postingValidation.ValidateAndAuditPostingAsync(new PostingValidationCommand(
                        OperationType: PostingOperationTypes.Sale,
                        EntityId: replacementSale.Id,
                        EntityNumber: replacementSale.InvoiceNumber,
                        ActorId: command.CashierUserId,
                        PostingDate: postingDate,
                        SupplierDocumentDate: null,
                        SupervisorApproval: command.SupervisorApproval,
                        CallerSuppliedBooleanOverride: command.CallerSuppliedBooleanOverride), ct);

                    if (!postingValidationResult.IsSuccess)
                    {
                        return Result<CommercialExchangeResult>.Failure(
                            postingValidationResult.Error!.Code,
                            postingValidationResult.Error.Message);
                    }
                }

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

        if (!result.IsSuccess && _outcomeLedger is not null && command.ClientOperationId != Guid.Empty &&
            result.Error?.Code?.StartsWith("idempotency.") != true)
        {
            await _outcomeLedger.RecordFailureAsync(
                command.ClientOperationId,
                "CommercialExchange",
                result.Error?.Code ?? "sales.exchange_failed",
                result.Error?.Message ?? "Commercial exchange failed.",
                actorId: command.CashierUserId,
                sessionId: command.SessionId,
                payloadFingerprint: currentFingerprint,
                cancellationToken: cancellationToken);
        }

        return result;
    }

    private async Task<IReadOnlyList<PreparedSaleLine>> PrepareSaleLinesAsync(
        IReadOnlyList<CompleteSaleLineInput> inputs,
        IReadOnlyDictionary<Guid, Product> products,
        Guid cashierUserId,
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
            PackAllocationResult? packAllocationResult = null;

            if (product.TrackingMode is TrackingMode.Serialized or TrackingMode.IndividualPiece)
            {
                var physicalCount = quantity.BaseQuantity;
                if (!QuantityMath.IsWhole(physicalCount) || physicalCount <= 0m || physicalCount > int.MaxValue ||
                    input.InventoryUnitIds.Count != decimal.ToInt32(physicalCount) ||
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
            else if (product.TrackingMode == TrackingMode.Container)
            {
                var isCable = product.Name.Contains("Cable", StringComparison.OrdinalIgnoreCase) ||
                              product.Name.Contains("Wire", StringComparison.OrdinalIgnoreCase) ||
                              product.AttributesJson?.Contains("Length", StringComparison.OrdinalIgnoreCase) == true;

                if (!isCable && !QuantityMath.IsWhole(quantity.BaseQuantity))
                {
                    throw new BusinessRuleException(
                        "sales.pack_fractional_count_prohibited",
                        $"Fractional unit quantity '{quantity.BaseQuantity}' is not permitted for count-based pack product '{product.Name}'.");
                }

                if (!isCable)
                {
                    var physicalCount = quantity.EnteredQuantity;
                    if (!QuantityMath.IsWhole(physicalCount) || physicalCount <= 0m || physicalCount > int.MaxValue ||
                        input.InventoryUnitIds.Count != decimal.ToInt32(physicalCount) ||
                        input.InventoryUnitIds.Distinct().Count() != input.InventoryUnitIds.Count)
                    {
                        throw new BusinessRuleException(
                            "sales.serial_count_mismatch",
                            $"Select one exact serialized unit per base unit for '{product.Name}'.");
                    }
                }

                IReadOnlyList<InventoryUnit> eligibleUnits;
                if (input.InventoryUnitIds.Count > 0)
                {
                    foreach (var id in input.InventoryUnitIds)
                    {
                        if (!selectedSerializedIds.Add(id))
                        {
                            throw new BusinessRuleException(
                                "sales.serial_selected_twice",
                                "A pack unit cannot be selected twice in one sale.");
                        }
                    }

                    eligibleUnits = await _inventory.GetInventoryUnitsForUpdateAsync(
                        product.Id,
                        input.InventoryUnitIds,
                        ct);
                    if (eligibleUnits.Count != input.InventoryUnitIds.Count ||
                        eligibleUnits.Any(x => x.Status != InventoryUnitStatus.InStock || x.InventoryLotId is null))
                    {
                        throw new BusinessRuleException(
                            "sales.serial_not_sellable",
                            $"One or more pack units for '{product.Name}' are not sellable.");
                    }
                }
                else
                {
                    eligibleUnits = await _inventory.GetSellableInventoryUnitsAsync(product.Id, ct);
                }

                var preferredUnitId = input.PreferredPackUnitId ?? (input.InventoryUnitIds.Count == 1 ? input.InventoryUnitIds[0] : null);

                var allocRequest = new PackAllocationRequest(
                    ProductId: product.Id,
                    RequestedBaseQuantity: quantity.BaseQuantity,
                    MovementId: Guid.Empty,
                    OperationType: PackAllocationOperationTypes.Sale,
                    ActorId: cashierUserId,
                    OccurredAt: _clock.UtcNow,
                    PreferredPackUnitId: preferredUnitId,
                    AllowSplitCuts: input.AllowSplitCuts,
                    IsCableOrLengthProduct: isCable,
                    MinimumCutIncrement: 1m);

                packAllocationResult = _packAllocations.Allocate(eligibleUnits, allocRequest);
                serializedUnits = packAllocationResult.Allocations
                    .Select(a => eligibleUnits.First(u => u.Id == a.PackUnitId))
                    .Distinct()
                    .OrderBy(u => u.Id)
                    .ToArray();
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
                authoritativePrice,
                null,
                null,
                Money(quantity.EnteredQuantity * authoritativePrice),
                serializedUnits,
                packAllocationResult));
        }

        return result;
    }

    private async Task<decimal> ConsumePackAllocationAsync(
        PreparedSaleLine line,
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
                    "sales.serial_lot_missing",
                    "Pack inventory lot was not found.");

            if (lotBalance.Quantity < alloc.AllocatedBaseQuantity)
            {
                throw new BusinessRuleException(
                    "sales.serial_lot_insufficient",
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

            totalCost += await _costs.RemoveCarryingValueAsync(
                line.Product.Id,
                alloc.AllocatedBaseQuantity,
                alloc.UnitCostSnapshot,
                ct);

            var fromStatus = unit.Status;
            if (alloc.PackStateAfter == PackState.Consumed)
            {
                unit.Status = InventoryUnitStatus.Sold;
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

    private async Task<decimal> ConsumeSerializedSaleAsync(
        PreparedSaleLine line,
        InventoryMovement movement,
        CancellationToken ct)
    {
        decimal totalCost = 0m;
        var basePerUnit = line.Quantity.BaseQuantity / line.SerializedUnits.Count;
        foreach (var unit in line.SerializedUnits.OrderBy(x => x.Id))
        {
            var lotBalance = await _inventory.GetLotBucketBalanceForUpdateAsync(
                unit.InventoryLotId!.Value,
                InventoryBucket.Sellable,
                ct)
                ?? throw new BusinessRuleException(
                    "sales.serial_lot_missing",
                    "Serialized unit inventory lot was not found.");

            if (lotBalance.Quantity < basePerUnit)
            {
                throw new BusinessRuleException(
                    "sales.serial_lot_insufficient",
                    "Serialized unit inventory lot is no longer sellable.");
            }

            lotBalance.Quantity = QuantityMath.RoundQuantity(lotBalance.Quantity - basePerUnit);

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

    private static string ComputePayloadFingerprint(CommercialExchangeCommand command)
    {
        var projection = new
        {
            command.OriginalSaleId,
            command.CustomerId,
            command.CashierUserId,
            ReturnReasonCode = command.ReturnReasonCode.Trim().ToUpperInvariant(),
            ReturnReasonNote = Normalize(command.ReturnReasonNote),
            command.SettlementMethod,
            InvoiceDiscount = command.InvoiceDiscount.ToString("G29", CultureInfo.InvariantCulture),
            ReturnLines = command.ReturnLines
                .OrderBy(x => x.SaleItemId)
                .Select(x => new
                {
                    x.SaleItemId,
                    BaseQuantity = x.BaseQuantity.ToString("G29", CultureInfo.InvariantCulture),
                    Disposition = x.Disposition.ToString(),
                    InventoryUnitIds = x.InventoryUnitIds.OrderBy(u => u).ToArray(),
                    x.ReturnSourceContainerId,
                    ConditionInspectionResult = Normalize(x.ConditionInspectionResult),
                    x.IsRestockAsRemnant
                }).ToArray(),
            ReplacementLines = command.ReplacementLines
                .OrderBy(x => x.ProductId)
                .ThenBy(x => x.ProductUnitId)
                .Select(x => new
                {
                    x.ProductId,
                    x.ProductUnitId,
                    EnteredQuantity = x.EnteredQuantity.ToString("G29", CultureInfo.InvariantCulture),
                    ExpectedUnitPrice = x.ExpectedUnitPrice.ToString("G29", CultureInfo.InvariantCulture),
                    InventoryUnitIds = x.InventoryUnitIds.OrderBy(u => u).ToArray(),
                    PackAllocationDetails = x.PackAllocationDetails is null ? null : new
                    {
                        x.PackAllocationDetails.TrackingCode,
                        x.PackAllocationDetails.AllocatedContainerId,
                        x.PackAllocationDetails.RequestedCutLength
                    }
                }).ToArray()
        };

        return OperationPayloadFingerprint.ComputeVersionedFingerprint(
            "CommercialExchange",
            projection,
            OperationPayloadFingerprint.DefaultVersion);
    }

    private static string ComputeLegacyHexFingerprint(CommercialExchangeCommand command)
    {
        var returnLines = command.ReturnLines
            .OrderBy(x => x.SaleItemId)
            .Select(x => $"{x.SaleItemId}:{x.BaseQuantity.ToString("0.####", CultureInfo.InvariantCulture)}:{string.Join(",", x.InventoryUnitIds.OrderBy(u => u))}");

        var replacementLines = command.ReplacementLines
            .OrderBy(x => x.ProductId)
            .ThenBy(x => x.ProductUnitId)
            .Select(x => $"{x.ProductId}:{x.ProductUnitId}:{x.EnteredQuantity.ToString("0.####", CultureInfo.InvariantCulture)}:{string.Join(",", x.InventoryUnitIds.OrderBy(u => u))}");

        var payload = string.Join("|", new[]
        {
            command.OriginalSaleId.ToString(),
            command.CustomerId?.ToString() ?? string.Empty,
            command.CashierUserId.ToString(),
            command.ReturnReasonCode.Trim().ToUpperInvariant(),
            command.SettlementMethod.ToString(),
            command.InvoiceDiscount.ToString("0.##", CultureInfo.InvariantCulture),
            string.Join(";", returnLines),
            string.Join(";", replacementLines)
        });

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private async Task<Result<CommercialExchangeResult>?> EvaluateGuardedOutcomeAsync(
        OperationOutcomeRecord outcome,
        CommercialExchangeCommand command,
        string currentFingerprint,
        CancellationToken ct)
    {
        if (outcome.OperationType != "CommercialExchange")
        {
            return Result<CommercialExchangeResult>.Failure(
                "idempotency.operation_type_mismatch",
                $"Operation was registered as '{outcome.OperationType}', not 'CommercialExchange'.");
        }

        if (outcome.ActorId.HasValue && outcome.ActorId.Value != command.CashierUserId)
        {
            return Result<CommercialExchangeResult>.Failure(
                "idempotency.context_mismatch",
                "Operation actor mismatch.");
        }

        var legacyFingerprint = ComputeLegacyHexFingerprint(command);
        var matches = string.IsNullOrEmpty(outcome.PayloadFingerprint) ||
                      string.Equals(outcome.PayloadFingerprint, currentFingerprint, StringComparison.Ordinal) ||
                      string.Equals(outcome.PayloadFingerprint, legacyFingerprint, StringComparison.OrdinalIgnoreCase);

        if (!matches)
        {
            return Result<CommercialExchangeResult>.Failure(
                "idempotency.payload_mismatch",
                "The operation was previously executed with different parameters.");
        }

        if (outcome.State == OperationOutcomeState.Succeeded && outcome.WasCommitted)
        {
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
                    WasExisting: true));
            }

            return Result<CommercialExchangeResult>.Failure(
                "idempotency.outcome_unknown",
                "Committed exchange outcome has no matching entities; reconciliation is required.");
        }

        if (outcome.State != OperationOutcomeState.Succeeded || !outcome.WasCommitted)
        {
            return Result<CommercialExchangeResult>.Failure(
                outcome.ErrorCode ?? "idempotency.outcome_unknown",
                outcome.ErrorMessage ?? "Reconcile the previous exchange outcome before another execution.");
        }

        return null;
    }
}
