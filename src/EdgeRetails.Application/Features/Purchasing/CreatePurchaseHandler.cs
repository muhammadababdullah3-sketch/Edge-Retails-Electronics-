using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Purchasing;

namespace EdgeRetails.Application.Features.Purchasing;

public sealed record SerializedIdentityInput(
    string? SerialNumber,
    string? Imei1 = null,
    string? Imei2 = null);

public sealed record CreatePurchaseLineInput(
    Guid ProductId,
    Guid ProductUnitId,
    decimal EnteredQuantity,
    decimal EnteredUnitCost,
    decimal BaseUnitSalePrice,
    IReadOnlyList<SerializedIdentityInput> SerializedUnits);

public sealed record CreatePurchaseCommand(
    Guid SupplierId,
    string SupplierInvoiceNumber,
    DateOnly PurchaseDate,
    string? Note,
    decimal OtherCharges,
    PurchaseSettlementMode SettlementMode,
    Guid CreatedBy,
    Guid ClientOperationId,
    IReadOnlyList<CreatePurchaseLineInput> Lines,
    decimal? InitialPaymentAmount = null,
    SupplierSettlementMethod? InitialPaymentMethod = null,
    string? InitialPaymentExternalReference = null);

public sealed record CreatePurchaseResult(
    Guid PurchaseId,
    string PurchaseNumber,
    decimal GrandTotal,
    bool WasExisting);

internal sealed record PreparedPurchaseLine(
    CreatePurchaseLineInput Input,
    Product Product,
    ProductUnit ProductUnit,
    TransactionQuantitySnapshot Quantity,
    decimal BaseLineTotal);

public sealed class CreatePurchaseHandler
{
    private readonly IPurchasingRepository _purchases;
    private readonly IPartyRepository _parties;
    private readonly ICatalogRepository _catalog;
    private readonly IInventoryRepository _inventory;
    private readonly IInventoryCostAllocator _costs;
    private readonly ICashRepository _cash;
    private readonly ITraceabilityRepository _traceability;
    private readonly ISupplierAccountRepository _supplierAccounts;
    private readonly IOperationLock _operationLock;
    private readonly IResourceLock _resourceLock;
    private readonly IBusinessAuditWriter _audit;
    private readonly IDocumentNumberService _numbers;
    private readonly IClock _clock;
    private readonly ITransactionRunner _transactions;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly IUnitOfWork _unitOfWork;

    public CreatePurchaseHandler(
        IPurchasingRepository purchases,
        IPartyRepository parties,
        ICatalogRepository catalog,
        IInventoryRepository inventory,
        IInventoryCostAllocator costs,
        ICashRepository cash,
        ITraceabilityRepository traceability,
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
        _parties = parties;
        _catalog = catalog;
        _inventory = inventory;
        _costs = costs;
        _cash = cash;
        _traceability = traceability;
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

    public Task<Result<CreatePurchaseResult>> HandleAsync(
        CreatePurchaseCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ClientOperationId == Guid.Empty)
        {
            return Task.FromResult(Result<CreatePurchaseResult>.Failure(
                "purchasing.operation_id_required", "Client operation id is required."));
        }

        if (command.Lines.Count == 0)
        {
            return Task.FromResult(Result<CreatePurchaseResult>.Failure(
                "purchasing.items_required", "Purchase requires at least one item."));
        }

        if (command.Lines.GroupBy(x => x.ProductId).Any(g => g.Count() > 1))
        {
            return Task.FromResult(Result<CreatePurchaseResult>.Failure(
                "purchasing.duplicate_product_line",
                "A product may appear only once on a purchase."));
        }

        return _transactions.ExecuteAsync(async ct =>
        {
            var authorization = await _authorization.AuthorizeAsync(
                command.CreatedBy,
                PermissionKeys.PurchasingManage,
                ct);
            if (!authorization.IsSuccess)
            {
                return Result<CreatePurchaseResult>.Failure(
                    authorization.Error!.Code,
                    authorization.Error.Message);
            }

            await _operationLock.AcquireAsync(command.ClientOperationId, ct);

            var existing = await _purchases.GetPurchaseByClientOperationIdAsync(
                command.ClientOperationId, ct);
            if (existing is not null)
            {
                if (existing.SupplierId != command.SupplierId || existing.SupplierInvoiceNumber != command.SupplierInvoiceNumber.Trim())
                {
                    return Result<CreatePurchaseResult>.Failure(
                        "idempotency.payload_mismatch",
                        "Operation was previously submitted with a different supplier or invoice number.");
                }

                return Result<CreatePurchaseResult>.Success(new(
                    existing.Id, existing.PurchaseNumber, existing.GrandTotal, true));
            }

            try
            {
                var supplier = await _parties.GetSupplierAsync(command.SupplierId, ct);
                if (supplier is null || !supplier.IsActive)
                {
                    return Result<CreatePurchaseResult>.Failure(
                        "purchasing.supplier_not_active",
                        "Supplier was not found or is inactive.");
                }

                var normalizedInvoice = PurchaseMath.NormalizeSupplierInvoiceNumber(
                    command.SupplierInvoiceNumber);

                await _resourceLock.AcquireAsync(
                    "supplier-invoice",
                    $"{command.SupplierId:D}:{normalizedInvoice}",
                    ct);

                if (await _purchases.GetPurchaseBySupplierInvoiceAsync(
                        command.SupplierId, normalizedInvoice, ct) is not null)
                {
                    return Result<CreatePurchaseResult>.Failure(
                        "purchasing.supplier_invoice_duplicate",
                        "This supplier invoice has already been recorded.");
                }

                if (command.OtherCharges < 0)
                {
                    return Result<CreatePurchaseResult>.Failure(
                        "purchasing.other_charges_negative",
                        "Other charges cannot be negative.");
                }

                foreach (var productId in command.Lines
                    .Select(x => x.ProductId)
                    .Distinct()
                    .OrderBy(x => x))
                {
                    await _resourceLock.AcquireAsync("product", productId, ct);
                }

                var prepared = await PrepareLinesAsync(command.Lines, ct);

                if (string.IsNullOrWhiteSpace(supplier.DealerCode))
                {
                    return Result<CreatePurchaseResult>.Failure(
                        "purchasing.supplier_dealer_code_required",
                        "Supplier requires a permanent DealerCode before inventory can be received.");
                }

                var supplierProducts = new Dictionary<Guid, SupplierProduct>();
                foreach (var line in prepared.OrderBy(x => x.Product.Id))
                {
                    var resourceKey = $"{command.SupplierId:D}:{line.Product.Id:D}";
                    await _resourceLock.AcquireAsync("supplier-product", resourceKey, ct);

                    var supplierProduct = await _traceability.GetSupplierProductForUpdateAsync(
                        command.SupplierId,
                        line.Product.Id,
                        ct);

                    if (supplierProduct is null)
                    {
                        supplierProduct = new SupplierProduct
                        {
                            SupplierId = command.SupplierId,
                            ProductId = line.Product.Id,
                            NextItemSequence = 1,
                            IsActive = true,
                            CreatedAt = _clock.UtcNow,
                            UpdatedAt = _clock.UtcNow
                        };
                        _traceability.AddSupplierProduct(supplierProduct);
                    }
                    else if (!supplierProduct.IsActive)
                    {
                        return Result<CreatePurchaseResult>.Failure(
                            "purchasing.supplier_product_inactive",
                            $"Supplier/Product mapping for '{line.Product.Name}' is inactive.");
                    }

                    supplierProducts[line.Product.Id] = supplierProduct;
                }

                await _resourceLock.AcquireAsync("supplier-account", command.SupplierId, ct);

                var receivedIdentities = prepared
                    .Where(x => x.Product.TrackingMode == TrackingMode.Serialized)
                    .SelectMany(x => x.Input.SerializedUnits)
                    .Select(x => new
                    {
                        Serial = NormalizeIdentity(x.SerialNumber),
                        Imei1 = NormalizeIdentity(x.Imei1),
                        Imei2 = NormalizeIdentity(x.Imei2)
                    })
                    .ToArray();

                var identityLockKeys = receivedIdentities
                    .SelectMany(x => new[]
                    {
                        x.Serial is null ? null : $"SERIAL:{x.Serial}",
                        x.Imei1 is null ? null : $"IMEI:{x.Imei1}",
                        x.Imei2 is null ? null : $"IMEI:{x.Imei2}"
                    })
                    .Where(x => x is not null)
                    .Select(x => x!)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToArray();

                foreach (var identityKey in identityLockKeys)
                {
                    await _resourceLock.AcquireAsync(
                        "inventory-identity",
                        identityKey,
                        ct);
                }

                foreach (var identity in receivedIdentities)
                {
                    if (await _inventory.InventoryIdentityExistsAsync(
                            identity.Serial,
                            identity.Imei1,
                            identity.Imei2,
                            ct))
                    {
                        return Result<CreatePurchaseResult>.Failure(
                            "purchasing.identity_already_exists",
                            "A received Serial/IMEI already exists in inventory history.");
                    }
                }

                var baseLineTotals = prepared.Select(x => x.BaseLineTotal).ToArray();
                var allocations = PurchaseMath.AllocateOtherCharges(
                    baseLineTotals, command.OtherCharges);

                var subtotal = Money(baseLineTotals.Sum());
                var otherCharges = Money(command.OtherCharges);
                var grandTotal = Money(subtotal + otherCharges);

                var purchase = new Purchase
                {
                    PurchaseNumber = await _numbers.NextAsync("PUR", ct),
                    SupplierId = command.SupplierId,
                    SupplierInvoiceNumber = command.SupplierInvoiceNumber.Trim(),
                    NormalizedSupplierInvoiceNumber = normalizedInvoice,
                    PurchaseDate = command.PurchaseDate,
                    Note = command.Note?.Trim(),
                    Subtotal = subtotal,
                    OtherCharges = otherCharges,
                    GrandTotal = grandTotal,
                    SettlementMode = command.SettlementMode,
                    CreatedBy = command.CreatedBy,
                    CreatedAt = _clock.UtcNow,
                    ClientOperationId = command.ClientOperationId
                };
                _purchases.AddPurchase(purchase);

                for (var index = 0; index < prepared.Count; index++)
                {
                    var line = prepared[index];
                    if (await _inventory.IsProductBlockedByCountingStocktakeAsync(
                            line.Product.Id, ct))
                    {
                        return Result<CreatePurchaseResult>.Failure(
                            "inventory.stocktake_blocks_product",
                            $"Product '{line.Product.Name}' is locked by an active stocktake.");
                    }

                    var stock = await _inventory.GetStockBalanceForUpdateAsync(
                        line.Product.Id, ct);
                    if (stock is null)
                    {
                        stock = new StockBalance { ProductId = line.Product.Id };
                        _inventory.AddStockBalance(stock);
                    }

                    if (await _inventory.IsProductBlockedByCountingStocktakeAsync(
                            line.Product.Id,
                            ct))
                    {
                        return Result<CreatePurchaseResult>.Failure(
                            "inventory.stocktake_blocks_product",
                            $"Product '{line.Product.Name}' is locked by an active stocktake.");
                    }

                    var allocatedOther = allocations[index];
                    var effectiveLineCost = Money(line.BaseLineTotal + allocatedOther);
                    var effectiveBaseCost = Cost(
                        effectiveLineCost / line.Quantity.BaseQuantity);

                    var item = new PurchaseItem
                    {
                        PurchaseId = purchase.Id,
                        ProductId = line.Product.Id,
                        ProductUnitId = line.ProductUnit.Id,
                        ProductNameSnapshot = line.Product.Name,
                        SkuSnapshot = line.Product.Sku,
                        EnteredQuantity = line.Quantity.EnteredQuantity,
                        FactorToBaseSnapshot = line.Quantity.FactorToBaseSnapshot,
                        BaseQuantity = line.Quantity.BaseQuantity,
                        EnteredUnitCost = Cost(line.Input.EnteredUnitCost),
                        BaseLineTotal = line.BaseLineTotal,
                        AllocatedOtherCost = allocatedOther,
                        EffectiveBaseUnitCost = effectiveBaseCost,
                        EffectiveLineCost = effectiveLineCost,
                        SalePriceAtPurchase = Money(line.Input.BaseUnitSalePrice)
                    };
                    _purchases.AddPurchaseItem(item);

                    var before = stock.SellableQty;
                    stock.ApplyDelta(InventoryBucket.Sellable, line.Quantity.BaseQuantity);

                    var movement = new InventoryMovement
                    {
                        ProductId = line.Product.Id,
                        MovementType = InventoryMovementType.PurchaseIn,
                        ReferenceType = "PURCHASE",
                        ReferenceId = purchase.Id,
                        UnitCostSnapshot = effectiveBaseCost,
                        ActorId = command.CreatedBy,
                        OccurredAt = _clock.UtcNow,
                        CorrelationId = command.ClientOperationId,
                        Note = purchase.PurchaseNumber
                    };
                    _inventory.AddMovement(movement);
                    _inventory.AddMovementEffect(new InventoryMovementEffect
                    {
                        MovementId = movement.Id,
                        StockBucket = InventoryBucket.Sellable,
                        QuantityDelta = line.Quantity.BaseQuantity,
                        QuantityBefore = before,
                        QuantityAfter = stock.SellableQty
                    });

                    var lotId = await _costs.AddCarryingValueAndLotWithIdAsync(
                        line.Product.Id, line.Quantity.BaseQuantity, effectiveBaseCost,
                        movement.Id, item.Id, ct);

                    if (line.Product.TrackingMode == TrackingMode.Serialized)
                    {
                        var supplierProduct = supplierProducts[line.Product.Id];
                        var unitCount = decimal.ToInt32(line.Quantity.BaseQuantity);
                        var firstSequence = supplierProduct.NextItemSequence;
                        supplierProduct.NextItemSequence = checked(firstSequence + unitCount);
                        supplierProduct.UpdatedAt = _clock.UtcNow;
                        supplierProduct.Version++;

                        AddSerializedUnits(
                            line,
                            item,
                            movement,
                            lotId,
                            effectiveBaseCost,
                            supplierProduct,
                            supplier.DealerCode!,
                            TraceabilityCodeRules.NormalizeSku(line.Product.Sku!),
                            firstSequence);
                    }
                    else if (line.Input.SerializedUnits.Count > 0)
                    {
                        return Result<CreatePurchaseResult>.Failure(
                            "purchasing.serials_not_allowed",
                            $"Product '{line.Product.Name}' is not serialized.");
                    }

                    var costState = await _inventory.GetCostStateForUpdateAsync(
                        line.Product.Id,
                        ct)
                        ?? throw new BusinessRuleException(
                            "inventory.cost_state_missing",
                            "Inventory cost state was not created for the purchase.");

                    costState.LastPurchaseCost = effectiveBaseCost;
                    costState.LastPurchaseAt = _clock.UtcNow;
                }

                var purchaseEntry = new SupplierAccountEntry
                {
                    EntryNumber = await _numbers.NextAsync("SAE", ct),
                    SupplierId = command.SupplierId,
                    EntryType = SupplierAccountEntryType.Purchase,
                    Direction = SupplierAccountDirection.IncreasePayable,
                    Amount = grandTotal,
                    ReferenceType = "Purchase",
                    ReferenceId = purchase.Id,
                    OccurredAt = _clock.UtcNow,
                    ActorId = command.CreatedBy,
                    ClientOperationId = command.ClientOperationId,
                    Note = purchase.PurchaseNumber,
                    CreatedAt = _clock.UtcNow
                };
                purchaseEntry.ValidateDirection();
                _supplierAccounts.AddEntry(purchaseEntry);

                var initialPayment = command.InitialPaymentAmount ?? grandTotal;
                initialPayment = Money(initialPayment);
                if (initialPayment < 0 || initialPayment > grandTotal)
                {
                    return Result<CreatePurchaseResult>.Failure(
                        "purchasing.initial_payment_invalid",
                        "Initial supplier payment must be between zero and the current Purchase GrandTotal.");
                }

                if (initialPayment > 0)
                {
                    var paymentMethod = command.InitialPaymentMethod ??
                        (command.SettlementMode == PurchaseSettlementMode.CashDrawer
                            ? SupplierSettlementMethod.CashDrawer
                            : SupplierSettlementMethod.External);

                    var payment = new SupplierPayment
                    {
                        PaymentNumber = await _numbers.NextAsync("SP", ct),
                        SupplierId = command.SupplierId,
                        Amount = initialPayment,
                        Purpose = SupplierPaymentPurpose.Settlement,
                        Method = paymentMethod,
                        ExternalReference = command.InitialPaymentExternalReference?.Trim(),
                        PaidAt = _clock.UtcNow,
                        ActorId = command.CreatedBy,
                        ClientOperationId = command.ClientOperationId,
                        Note = $"Initial payment for {purchase.PurchaseNumber}"
                    };

                    if (paymentMethod == SupplierSettlementMethod.CashDrawer)
                    {
                        var cashSession = await _cash.GetOpenSessionForUpdateAsync(ct);
                        if (cashSession is null)
                        {
                            return Result<CreatePurchaseResult>.Failure(
                                "cash.session_required",
                                "An open cash session is required for a cash-drawer supplier payment.");
                        }

                        payment.CashSessionId = cashSession.Id;
                        _cash.AddMovement(new CashMovement
                        {
                            CashSessionId = cashSession.Id,
                            MovementType = CashMovementType.SupplierPaymentCashOut,
                            Direction = CashMovementDirection.Out,
                            Amount = payment.Amount,
                            SourceType = "SUPPLIER_PAYMENT",
                            SourceId = payment.Id,
                            ActorId = command.CreatedBy,
                            OccurredAt = _clock.UtcNow,
                            Reason = payment.PaymentNumber
                        });
                    }

                    _supplierAccounts.AddPayment(payment);

                    var paymentEntry = new SupplierAccountEntry
                    {
                        EntryNumber = await _numbers.NextAsync("SAE", ct),
                        SupplierId = command.SupplierId,
                        EntryType = SupplierAccountEntryType.SupplierPayment,
                        Direction = SupplierAccountDirection.DecreasePayable,
                        Amount = payment.Amount,
                        ReferenceType = "SupplierPayment",
                        ReferenceId = payment.Id,
                        OccurredAt = _clock.UtcNow,
                        ActorId = command.CreatedBy,
                        ClientOperationId = command.ClientOperationId,
                        Note = payment.Note,
                        CreatedAt = _clock.UtcNow
                    };
                    paymentEntry.ValidateDirection();
                    _supplierAccounts.AddEntry(paymentEntry);
                }

                _audit.Record(
                    "PURCHASE_COMPLETED",
                    "PURCHASE",
                    purchase.Id,
                    command.CreatedBy,
                    command.ClientOperationId,
                    $"Purchase {purchase.PurchaseNumber}; supplier invoice {purchase.SupplierInvoiceNumber}; total {purchase.GrandTotal:0.00}; initial payment {initialPayment:0.00}.");

                await _unitOfWork.SaveChangesAsync(ct);
                return Result<CreatePurchaseResult>.Success(new(
                    purchase.Id, purchase.PurchaseNumber, purchase.GrandTotal, false));
            }
            catch (BusinessRuleException ex)
            {
                return Result<CreatePurchaseResult>.Failure(ex.Code, ex.Message);
            }
        }, cancellationToken);
    }

    private async Task<IReadOnlyList<PreparedPurchaseLine>> PrepareLinesAsync(
        IReadOnlyList<CreatePurchaseLineInput> lines,
        CancellationToken ct)
    {
        var result = new List<PreparedPurchaseLine>(lines.Count);
        foreach (var input in lines.OrderBy(x => x.ProductId))
        {
            if (input.EnteredUnitCost < 0 || input.BaseUnitSalePrice < 0)
            {
                throw new BusinessRuleException(
                    "purchasing.price_negative",
                    "Purchase cost and sale price cannot be negative.");
            }

            var product = await _catalog.GetProductAsync(input.ProductId, ct)
                ?? throw new BusinessRuleException(
                    "purchasing.product_not_found", "Purchase product was not found.");
            if (!product.IsActive)
            {
                throw new BusinessRuleException(
                    "purchasing.product_inactive", $"Product '{product.Name}' is inactive.");
            }

            var productUnit = await _catalog.GetProductUnitAsync(input.ProductUnitId, ct)
                ?? throw new BusinessRuleException(
                    "purchasing.product_unit_not_found", "Purchase unit was not found.");
            if (productUnit.ProductId != product.Id ||
                !productUnit.IsActive || !productUnit.CanPurchase)
            {
                throw new BusinessRuleException(
                    "purchasing.product_unit_not_allowed",
                    $"Selected unit is not allowed for '{product.Name}'.");
            }

            var quantity = TransactionQuantitySnapshot.Create(
                productUnit, input.EnteredQuantity, product.TrackingMode);

            if (product.TrackingMode == TrackingMode.Serialized)
            {
                if (string.IsNullOrWhiteSpace(product.Sku))
                {
                    throw new BusinessRuleException(
                        "purchasing.sku_required_for_tracking",
                        $"Serialized product '{product.Name}' requires a permanent SKU before receipt.");
                }

                if (input.SerializedUnits.Count != decimal.ToInt32(quantity.BaseQuantity))
                {
                    throw new BusinessRuleException(
                        "purchasing.serial_count_mismatch",
                        $"Serialized identities for '{product.Name}' must equal base quantity.");
                }
            }

            ValidateIdentityBatch(product, input.SerializedUnits);
            var lineTotal = Money(quantity.EnteredQuantity * input.EnteredUnitCost);
            result.Add(new(input, product, productUnit, quantity, lineTotal));
        }
        return result;
    }

    private void AddSerializedUnits(
        PreparedPurchaseLine line,
        PurchaseItem item,
        InventoryMovement movement,
        Guid lotId,
        decimal acquisitionCost,
        SupplierProduct supplierProduct,
        string dealerCode,
        string productSku,
        long firstSequence)
    {
        for (var index = 0; index < line.Input.SerializedUnits.Count; index++)
        {
            var identity = line.Input.SerializedUnits[index];
            var itemSequence = checked(firstSequence + index);
            var trackingCode = TraceabilityCodeRules.BuildTrackingCode(
                dealerCode,
                productSku,
                itemSequence);

            var unit = new InventoryUnit
            {
                ProductId = line.Product.Id,
                SupplierProductId = supplierProduct.Id,
                OriginType = InventoryUnitOriginType.Purchase,
                ItemSequence = itemSequence,
                TrackingCode = trackingCode,
                SupplierCodeSnapshot = dealerCode,
                ProductSkuSnapshot = productSku,
                SerialNumber = NormalizeIdentity(identity.SerialNumber),
                Imei1 = NormalizeIdentity(identity.Imei1),
                Imei2 = NormalizeIdentity(identity.Imei2),
                Status = InventoryUnitStatus.InStock,
                AcquisitionCost = acquisitionCost,
                InventoryLotId = lotId,
                SourcePurchaseItemId = item.Id,
                CreatedAt = _clock.UtcNow
            };
            _inventory.AddInventoryUnit(unit);
            _purchases.AddPurchaseItemUnit(new PurchaseItemUnit
            {
                PurchaseItemId = item.Id,
                InventoryUnitId = unit.Id
            });
            _inventory.AddMovementUnit(new InventoryMovementUnit
            {
                MovementId = movement.Id,
                InventoryUnitId = unit.Id,
                FromStatus = null,
                ToStatus = InventoryUnitStatus.InStock
            });
        }
    }

    private static void ValidateIdentityBatch(
        Product product,
        IReadOnlyList<SerializedIdentityInput> identities)
    {
        if (product.TrackingMode != TrackingMode.Serialized)
        {
            return;
        }

        var serials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var imeis = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var identity in identities)
        {
            var serial = NormalizeIdentity(identity.SerialNumber);
            var imei1 = NormalizeIdentity(identity.Imei1);
            var imei2 = NormalizeIdentity(identity.Imei2);

            if (product.SerialTrackingEnabled && serial is null)
            {
                throw new BusinessRuleException(
                    "purchasing.serial_required",
                    $"Serial number is required for '{product.Name}'.");
            }

            if (product.ImeiTrackingEnabled && imei1 is null)
            {
                throw new BusinessRuleException(
                    "purchasing.imei_required",
                    $"IMEI is required for '{product.Name}'.");
            }

            if (serial is not null && !serials.Add(serial))
            {
                throw new BusinessRuleException(
                    "purchasing.serial_duplicate", $"Duplicate serial '{serial}'.");
            }

            foreach (var imei in new[] { imei1, imei2 }.Where(x => x is not null))
            {
                if (!imeis.Add(imei!))
                {
                    throw new BusinessRuleException(
                        "purchasing.imei_duplicate", $"Duplicate IMEI '{imei}'.");
                }
            }
        }
    }

    private static string? NormalizeIdentity(string? value)
    {
        var v = value?.Trim();
        return string.IsNullOrWhiteSpace(v) ? null : v.ToUpperInvariant();
    }

    private static decimal Money(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    private static decimal Cost(decimal value) =>
        decimal.Round(value, 6, MidpointRounding.AwayFromZero);
}

