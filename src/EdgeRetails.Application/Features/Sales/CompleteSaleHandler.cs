using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Sales;

namespace EdgeRetails.Application.Features.Sales;

public sealed record CompleteSaleLineInput(
    Guid ProductId,
    Guid ProductUnitId,
    decimal EnteredQuantity,
    decimal ExpectedUnitPrice,
    IReadOnlyList<Guid> InventoryUnitIds);

public sealed record CompleteSaleCommand(
    Guid ClientOperationId,
    Guid? CustomerId,
    Guid CashierUserId,
    Guid? SessionId,
    decimal InvoiceDiscount,
    SalePaymentMethod PaymentMethod,
    decimal AmountTendered,
    string? PaymentReference,
    Guid? QuotationId,
    IReadOnlyList<CompleteSaleLineInput> Lines);

public sealed record CompleteSaleResult(
    Guid SaleId,
    string InvoiceNumber,
    decimal GrandTotal,
    decimal ChangeGiven,
    bool WasExisting);

internal sealed record PreparedSaleLine(
    CompleteSaleLineInput Input,
    Product Product,
    ProductUnit ProductUnit,
    TransactionQuantitySnapshot Quantity,
    decimal UnitPrice,
    decimal GrossLineTotal,
    IReadOnlyList<InventoryUnit> SerializedUnits);

public sealed class CompleteSaleHandler
{
    private readonly ISalesRepository _sales;
    private readonly IQuotationRepository _quotations;
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

    public CompleteSaleHandler(
        ISalesRepository sales,
        IQuotationRepository quotations,
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
        _quotations = quotations;
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

    public Task<Result<CompleteSaleResult>> HandleAsync(
        CompleteSaleCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ClientOperationId == Guid.Empty || command.Lines.Count == 0)
        {
            return Task.FromResult(Result<CompleteSaleResult>.Failure(
                "sales.invalid_request",
                "Sale requires an operation id and at least one item."));
        }

        if (command.Lines.GroupBy(x => new { x.ProductId, x.ProductUnitId })
            .Any(g => g.Count() > 1))
        {
            return Task.FromResult(Result<CompleteSaleResult>.Failure(
                "sales.duplicate_line",
                "The same product and unit may appear only once in the cart."));
        }

        return _transactions.ExecuteAsync(async ct =>
        {
            var authorization = await _authorization.AuthorizeAsync(
                command.CashierUserId,
                PermissionKeys.SalesCreate,
                ct);
            if (!authorization.IsSuccess)
            {
                return Result<CompleteSaleResult>.Failure(
                    authorization.Error!.Code,
                    authorization.Error.Message);
            }

            await _operationLock.AcquireAsync(command.ClientOperationId, ct);

            var existing = await _sales.GetSaleByClientOperationIdAsync(
                command.ClientOperationId,
                ct);
            if (existing is not null)
            {
                if (existing.CustomerId != command.CustomerId)
                {
                    return Result<CompleteSaleResult>.Failure(
                        "idempotency.payload_mismatch",
                        "Operation was previously submitted with a different customer.");
                }

                var payment = await _sales.GetSalePaymentAsync(existing.Id, ct);
                return Result<CompleteSaleResult>.Success(new(
                    existing.Id,
                    existing.InvoiceNumber,
                    existing.GrandTotal,
                    payment?.ChangeGiven ?? 0m,
                    true));
            }

            foreach (var productId in command.Lines
                .Select(x => x.ProductId)
                .Distinct()
                .OrderBy(x => x))
            {
                await _resourceLock.AcquireAsync("product", productId, ct);
            }

            try
            {
                Quotation? quotation = null;
                IReadOnlyList<QuotationItem> quotationItems = Array.Empty<QuotationItem>();
                if (command.QuotationId is not null)
                {
                    quotation = await _quotations.GetQuotationForUpdateAsync(
                        command.QuotationId.Value,
                        ct);
                    if (quotation is null || quotation.Status != QuotationStatus.Issued)
                    {
                        return Result<CompleteSaleResult>.Failure(
                            "sales.quotation_not_convertible",
                            "Quotation was not found or is not issued.");
                    }

                    if (quotation.ValidUntil is not null &&
                        _clock.ShopDate > quotation.ValidUntil.Value)
                    {
                        return Result<CompleteSaleResult>.Failure(
                            "sales.quotation_expired",
                            "Expired quotation cannot be converted.");
                    }

                    quotationItems = await _quotations.GetItemsAsync(quotation.Id, ct);
                }

                var effectiveCustomerId = quotation?.CustomerId ?? command.CustomerId;
                if (quotation?.CustomerId is not null &&
                    command.CustomerId is not null &&
                    quotation.CustomerId != command.CustomerId)
                {
                    return Result<CompleteSaleResult>.Failure(
                        "sales.quotation_customer_mismatch",
                        "Sale customer does not match the quotation.");
                }

                if (effectiveCustomerId is not null)
                {
                    var customer = await _parties.GetCustomerAsync(
                        effectiveCustomerId.Value,
                        ct);
                    if (customer is null || !customer.IsActive)
                    {
                        return Result<CompleteSaleResult>.Failure(
                            "sales.customer_not_active",
                            "Customer was not found or is inactive.");
                    }
                }

                var productIds = command.Lines
                    .Select(x => x.ProductId)
                    .Distinct()
                    .OrderBy(x => x)
                    .ToArray();

                var products = new Dictionary<Guid, Product>();
                var stocks = new Dictionary<Guid, StockBalance>();

                foreach (var productId in productIds)
                {
                    var product = await _catalog.GetProductForUpdateAsync(productId, ct);
                    if (product is null || !product.IsActive)
                    {
                        return Result<CompleteSaleResult>.Failure(
                            "sales.product_not_active",
                            "One or more sale products are missing or inactive.");
                    }

                    if (await _inventory.IsProductBlockedByCountingStocktakeAsync(
                            productId,
                            ct))
                    {
                        return Result<CompleteSaleResult>.Failure(
                            "inventory.stocktake_blocks_product",
                            $"Product '{product.Name}' is locked by an active stocktake.");
                    }

                    var stock = await _inventory.GetStockBalanceForUpdateAsync(productId, ct);
                    if (stock is null)
                    {
                        return Result<CompleteSaleResult>.Failure(
                            "sales.stock_missing",
                            $"Stock balance for '{product.Name}' was not found.");
                    }

                    products.Add(productId, product);
                    stocks.Add(productId, stock);
                }

                var prepared = await PrepareLinesAsync(
                    command.Lines,
                    products,
                    quotation,
                    quotationItems,
                    ct);

                foreach (var group in prepared.GroupBy(x => x.Product.Id))
                {
                    var requested = QuantityMath.RoundQuantity(
                        group.Sum(x => x.Quantity.BaseQuantity));
                    if (stocks[group.Key].SellableQty < requested)
                    {
                        return Result<CompleteSaleResult>.Failure(
                            "sales.insufficient_stock",
                            $"Insufficient sellable stock for '{group.First().Product.Name}'.");
                    }
                }

                if (quotation is not null && prepared.Count != quotationItems.Count)
                {
                    return Result<CompleteSaleResult>.Failure(
                        "sales.quotation_items_mismatch",
                        "Sale cart does not exactly match the quotation.");
                }

                var subtotal = Money(prepared.Sum(x => x.GrossLineTotal));
                var discount = quotation is null
                    ? Money(command.InvoiceDiscount)
                    : quotation.Discount;
                var allocations = SaleMath.AllocateInvoiceDiscount(
                    prepared.Select(x => x.GrossLineTotal).ToArray(),
                    discount);
                var grandTotal = Money(subtotal - discount);

                var paymentValidation = ValidatePayment(
                    command.PaymentMethod,
                    command.AmountTendered,
                    grandTotal);
                if (!paymentValidation.IsSuccess)
                {
                    return Result<CompleteSaleResult>.Failure(
                        paymentValidation.Error!.Code,
                        paymentValidation.Error.Message);
                }

                var amountTendered = Money(command.AmountTendered);
                var change = command.PaymentMethod == SalePaymentMethod.Cash
                    ? Money(amountTendered - grandTotal)
                    : 0m;

                var receiptSnapshot = await _receiptSnapshots.CaptureAsync(ct);

                var sale = new Sale
                {
                    InvoiceNumber = await _numbers.NextAsync("SALE", ct),
                    CustomerId = effectiveCustomerId,
                    CashierUserId = command.CashierUserId,
                    SessionId = command.SessionId,
                    CompletedAt = _clock.UtcNow,
                    Subtotal = subtotal,
                    InvoiceDiscount = discount,
                    GrandTotal = grandTotal,
                    Status = SaleStatus.Completed,
                    PaymentStatus = SalePaymentStatus.Paid,
                    ClientOperationId = command.ClientOperationId,
                    ReceiptTemplateSnapshot = receiptSnapshot,
                    CreatedAt = _clock.UtcNow
                };
                _sales.AddSale(sale);

                for (var index = 0; index < prepared.Count; index++)
                {
                    var line = prepared[index];
                    var stock = stocks[line.Product.Id];
                    var before = stock.SellableQty;

                    var movement = new InventoryMovement
                    {
                        ProductId = line.Product.Id,
                        MovementType = InventoryMovementType.SaleOut,
                        ReferenceType = "SALE",
                        ReferenceId = sale.Id,
                        ActorId = command.CashierUserId,
                        OccurredAt = _clock.UtcNow,
                        CorrelationId = command.ClientOperationId,
                        Note = sale.InvoiceNumber
                    };
                    _inventory.AddMovement(movement);

                    decimal totalCost;
                    if (line.Product.TrackingMode == TrackingMode.Serialized)
                    {
                        totalCost = await ConsumeSerializedAsync(
                            line,
                            movement,
                            ct);
                    }
                    else
                    {
                        totalCost = await _costs.RemoveCarryingValueAsync(
                            line.Product.Id,
                            line.Quantity.BaseQuantity,
                            null,
                            ct);
                        var saleUnitCost = Cost(
                            totalCost / line.Quantity.BaseQuantity);
                        await _costs.ConsumeBucketAsync(
                            line.Product.Id,
                            InventoryBucket.Sellable,
                            line.Quantity.BaseQuantity,
                            movement.Id,
                            saleUnitCost,
                            ct);
                    }

                    stock.ApplyDelta(
                        InventoryBucket.Sellable,
                        -line.Quantity.BaseQuantity);

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

                    var allocatedDiscount = allocations[index];
                    var netLineTotal = Money(
                        line.GrossLineTotal - allocatedDiscount);
                    var saleItem = new SaleItem
                    {
                        SaleId = sale.Id,
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

                CashSession? cashSession = null;
                if (command.PaymentMethod == SalePaymentMethod.Cash)
                {
                    cashSession = await _cash.GetOpenSessionForUpdateAsync(ct);
                    if (cashSession is null)
                    {
                        return Result<CompleteSaleResult>.Failure(
                            "cash.session_required",
                            "An open cash session is required for a cash sale.");
                    }
                }

                _sales.AddSalePayment(new SalePayment
                {
                    SaleId = sale.Id,
                    Method = command.PaymentMethod,
                    AmountTendered = amountTendered,
                    AppliedAmount = grandTotal,
                    ChangeGiven = change,
                    Reference = command.PaymentReference?.Trim()
                });

                if (cashSession is not null)
                {
                    _cash.AddMovement(new CashMovement
                    {
                        CashSessionId = cashSession.Id,
                        MovementType = CashMovementType.SaleCashIn,
                        Direction = CashMovementDirection.In,
                        Amount = grandTotal,
                        SourceType = "SALE",
                        SourceId = sale.Id,
                        ActorId = command.CashierUserId,
                        OccurredAt = _clock.UtcNow,
                        Reason = sale.InvoiceNumber
                    });
                }

                if (quotation is not null)
                {
                    quotation.MarkConverted(sale.Id, _clock.ShopDate);
                    _audit.Record(
                        "QUOTATION_CONVERTED",
                        "QUOTATION",
                        quotation.Id,
                        command.CashierUserId,
                        command.ClientOperationId,
                        $"Converted to sale {sale.InvoiceNumber}.");
                }

                _audit.Record(
                    "SALE_COMPLETED",
                    "SALE",
                    sale.Id,
                    command.CashierUserId,
                    command.ClientOperationId,
                    $"Invoice {sale.InvoiceNumber}; total {sale.GrandTotal:0.00}; payment {command.PaymentMethod}.");

                await _unitOfWork.SaveChangesAsync(ct);

                return Result<CompleteSaleResult>.Success(new(
                    sale.Id,
                    sale.InvoiceNumber,
                    sale.GrandTotal,
                    change,
                    false));
            }
            catch (BusinessRuleException ex)
            {
                return Result<CompleteSaleResult>.Failure(ex.Code, ex.Message);
            }
        }, cancellationToken);
    }

    private async Task<IReadOnlyList<PreparedSaleLine>> PrepareLinesAsync(
        IReadOnlyList<CompleteSaleLineInput> inputs,
        IReadOnlyDictionary<Guid, Product> products,
        Quotation? quotation,
        IReadOnlyList<QuotationItem> quotationItems,
        CancellationToken ct)
    {
        var result = new List<PreparedSaleLine>(inputs.Count);
        var selectedSerializedIds = new HashSet<Guid>();

        foreach (var input in inputs
            .OrderBy(x => x.ProductId)
            .ThenBy(x => x.ProductUnitId))
        {
            var product = products[input.ProductId];
            var productUnit = await _catalog.GetProductUnitAsync(
                input.ProductUnitId,
                ct);
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

            decimal authoritativePrice;
            if (quotation is null)
            {
                authoritativePrice = Money(
                    product.DefaultSalePrice * productUnit.FactorToBaseUnit);
            }
            else
            {
                var matchingQuoteItems = quotationItems
                    .Where(x =>
                        x.ProductId == product.Id &&
                        x.SelectedUnitId == productUnit.UnitId)
                    .ToArray();

                if (matchingQuoteItems.Length != 1 ||
                    matchingQuoteItems[0].EnteredQuantity != quantity.EnteredQuantity ||
                    matchingQuoteItems[0].FactorToBaseSnapshot != quantity.FactorToBaseSnapshot)
                {
                    throw new BusinessRuleException(
                        "sales.quotation_line_mismatch",
                        $"Cart line for '{product.Name}' does not match the quotation.");
                }

                authoritativePrice = matchingQuoteItems[0].QuotedUnitPrice;
            }

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
                    units.Any(x =>
                        x.Status != InventoryUnitStatus.InStock ||
                        x.InventoryLotId is null))
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

    private async Task<decimal> ConsumeSerializedAsync(
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

            lotBalance.Quantity = QuantityMath.RoundQuantity(
                lotBalance.Quantity - 1m);

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

    private static Result ValidatePayment(
        SalePaymentMethod method,
        decimal amountTendered,
        decimal grandTotal)
    {
        var tendered = Money(amountTendered);
        if (tendered < 0)
        {
            return Result.Failure(
                "sales.payment_negative",
                "Payment amount cannot be negative.");
        }

        if (method == SalePaymentMethod.Cash && tendered < grandTotal)
        {
            return Result.Failure(
                "sales.cash_insufficient",
                "Cash tendered must cover the sale total.");
        }

        if (method is SalePaymentMethod.Bank or SalePaymentMethod.Other &&
            tendered != grandTotal)
        {
            return Result.Failure(
                "sales.non_cash_exact",
                "Bank and Other payments must equal the sale total.");
        }

        return Result.Success();
    }

    private static decimal Money(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    private static decimal Cost(decimal value) =>
        decimal.Round(value, 6, MidpointRounding.AwayFromZero);
}

