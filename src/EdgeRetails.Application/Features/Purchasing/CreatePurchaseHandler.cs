using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Terminals;
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
    string? InitialPaymentExternalReference = null,
    bool ReceiveStockImmediately = true);

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
    private readonly ISequenceHighWaterService _highWaterService;
    private readonly IPhysicalUnitCreationAuthority? _physicalUnits;

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
        IUnitOfWork unitOfWork,
        ISequenceHighWaterService? highWaterService = null,
        IOperationOutcomeLedger? outcomeLedger = null,
        IPhysicalUnitCreationAuthority? physicalUnitCreationAuthority = null)
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
        _highWaterService = highWaterService ?? NullSequenceHighWaterService.Instance;
        _outcomeLedger = outcomeLedger;
        _physicalUnits = physicalUnitCreationAuthority;
    }

    private readonly IOperationOutcomeLedger? _outcomeLedger;

    public async Task<Result<CreatePurchaseResult>> HandleAsync(
        CreatePurchaseCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ClientOperationId == Guid.Empty)
        {
            return Result<CreatePurchaseResult>.Failure(
                "purchasing.operation_id_required", "Client operation id is required.");
        }

        if (command.Lines.Count == 0)
        {
            return Result<CreatePurchaseResult>.Failure(
                "purchasing.items_required", "Purchase requires at least one item.");
        }

        if (command.Lines.GroupBy(x => x.ProductId).Any(g => g.Count() > 1))
        {
            return Result<CreatePurchaseResult>.Failure(
                "purchasing.duplicate_product_line",
                "A product may appear only once on a purchase.");
        }

        if (_outcomeLedger is null)
        {
            return Result<CreatePurchaseResult>.Failure("purchasing.replay_authority_missing", "Canonical purchase outcome authority is required.");
        }
        string fingerprint;
        try
        {
            fingerprint = OperationPayloadFingerprint.ComputeSha256(System.Text.Json.JsonSerializer.Serialize(new
            {
                Operation = "CreatePurchase", Version = 1, command.ClientOperationId, command.SupplierId,
                Invoice = command.SupplierInvoiceNumber.Trim(), command.PurchaseDate,
                Note = command.Note?.Trim(), OtherCharges = command.OtherCharges.ToString("G29", System.Globalization.CultureInfo.InvariantCulture),
                command.SettlementMode, command.CreatedBy, command.ReceiveStockImmediately,
                InitialPayment = command.InitialPaymentAmount?.ToString("G29", System.Globalization.CultureInfo.InvariantCulture),
                command.InitialPaymentMethod, InitialReference = command.InitialPaymentExternalReference?.Trim(),
                Lines = command.Lines.Select(x => new
                {
                    x.ProductId, x.ProductUnitId,
                    Quantity = x.EnteredQuantity.ToString("G29", System.Globalization.CultureInfo.InvariantCulture),
                    Cost = x.EnteredUnitCost.ToString("G29", System.Globalization.CultureInfo.InvariantCulture),
                    Price = x.BaseUnitSalePrice.ToString("G29", System.Globalization.CultureInfo.InvariantCulture),
                    Units = x.SerializedUnits.Select(u => new
                    {
                        Serial = IdentityNormalizationRules.NormalizeOptionalSerialNumber(u.SerialNumber),
                        Imei1 = IdentityNormalizationRules.NormalizeOptionalImei(u.Imei1),
                        Imei2 = IdentityNormalizationRules.NormalizeOptionalImei(u.Imei2)
                    }).ToArray()
                }).ToArray()
            }));
        }
        catch (BusinessRuleException ex)
        {
            return Result<CreatePurchaseResult>.Failure(ex.Code, ex.Message);
        }
        var ownsOutcome = false;
        var result = await _transactions.ExecuteAsync(async ct =>
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
            var outcome = await _outcomeLedger.GetOutcomeAsync(command.ClientOperationId, ct);
            if (outcome is not null)
            {
                if (outcome.OperationType != "Purchase" || outcome.ActorId != command.CreatedBy ||
                    outcome.PayloadFingerprint != fingerprint)
                {
                    return Result<CreatePurchaseResult>.Failure("idempotency.payload_mismatch", "Operation was submitted with a different purchase intent or lacks canonical payload evidence.");
                }
                if (outcome.State != OperationOutcomeState.Succeeded || !outcome.WasCommitted || !outcome.EntityId.HasValue)
                {
                    return Result<CreatePurchaseResult>.Failure(outcome.ErrorCode ?? "idempotency.outcome_unknown",
                        outcome.ErrorMessage ?? "Reconcile the previous purchase outcome before another execution.");
                }
            }

            var existing = await _purchases.GetPurchaseByClientOperationIdAsync(
                command.ClientOperationId, ct);
            if (existing is not null)
            {
                if (outcome is null || outcome.EntityId != existing.Id)
                {
                    return Result<CreatePurchaseResult>.Failure("idempotency.legacy_purchase_requires_reconciliation",
                        "Existing purchase has no matching canonical intent evidence; reconcile it before retrying.");
                }
                if (existing.SupplierId != command.SupplierId || existing.SupplierInvoiceNumber != command.SupplierInvoiceNumber.Trim())
                {
                    return Result<CreatePurchaseResult>.Failure(
                        "idempotency.payload_mismatch",
                        "Operation was previously submitted with a different supplier or invoice number.");
                }

                return Result<CreatePurchaseResult>.Success(new(
                    existing.Id, existing.PurchaseNumber, existing.GrandTotal, true));
            }
            if (outcome is not null)
            {
                return Result<CreatePurchaseResult>.Failure("idempotency.outcome_unknown", "Committed purchase outcome has no matching purchase; reconciliation is required.");
            }
            ownsOutcome = true;

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

                var prepared = await PrepareLinesAsync(command.Lines, command.ReceiveStockImmediately, ct);

                if (string.IsNullOrWhiteSpace(supplier.DealerCode))
                {
                    return Result<CreatePurchaseResult>.Failure(
                        "purchasing.supplier_dealer_code_required",
                        "Supplier requires a permanent DealerCode before inventory can be received.");
                }

                if (command.ReceiveStockImmediately)
                {
                    foreach (var line in prepared.OrderBy(x => x.Product.Id))
                    {
                        var resourceKey = $"{command.SupplierId:D}:{line.Product.Id:D}";
                        await _resourceLock.AcquireAsync("supplier-product", resourceKey, ct);
                    }
                }

                await _resourceLock.AcquireAsync("supplier-account", command.SupplierId, ct);
                var lockedIdentityKeys = new HashSet<string>(StringComparer.Ordinal);

                if (command.ReceiveStockImmediately)
                {
                    var receivedIdentities = prepared
                        .Where(x => x.Product.TrackingMode == TrackingMode.Serialized || x.Product.TrackingMode == TrackingMode.IndividualPiece || x.Product.TrackingMode == TrackingMode.Container)
                        .SelectMany(x => x.Input.SerializedUnits)
                        .Select(x => new
                        {
                            Serial = NormalizeSerial(x.SerialNumber),
                            Imei1 = NormalizeImei(x.Imei1),
                            Imei2 = NormalizeImei(x.Imei2)
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
                        lockedIdentityKeys.Add(identityKey);
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
                }

                // Every command-wide logical key is held before the row phase.
                // Reload Product policy under the definitive row locks and rerun
                // preparation so discovery data cannot authorize stale masters.
                foreach (var line in prepared.OrderBy(x => x.Product.Id))
                {
                    await _catalog.GetProductForUpdateAsync(line.Product.Id, ct);
                }
                prepared = await PrepareLinesAsync(command.Lines, command.ReceiveStockImmediately, ct);
                if (command.ReceiveStockImmediately)
                {
                    var refreshedIdentityKeys = prepared
                        .Where(x => x.Product.TrackingMode is TrackingMode.Serialized or TrackingMode.IndividualPiece or TrackingMode.Container)
                        .SelectMany(x => x.Input.SerializedUnits)
                        .SelectMany(x => new[]
                        {
                            NormalizeSerial(x.SerialNumber) is string serial ? $"SERIAL:{serial}" : null,
                            NormalizeImei(x.Imei1) is string imei1 ? $"IMEI:{imei1}" : null,
                            NormalizeImei(x.Imei2) is string imei2 ? $"IMEI:{imei2}" : null
                        })
                        .Where(x => x is not null);
                    if (refreshedIdentityKeys.Any(x => !lockedIdentityKeys.Contains(x!)))
                    {
                        return Result<CreatePurchaseResult>.Failure(
                            "inventory.physical_unit_policy_changed_retry",
                            "Product tracking policy requires additional identity locks. Retry the complete operation.");
                    }
                }

                if (command.ReceiveStockImmediately)
                {
                    foreach (var line in prepared.OrderBy(x => x.Product.Id))
                    {
                        var supplierProduct = await _traceability.GetSupplierProductForUpdateAsync(
                            command.SupplierId, line.Product.Id, ct);
                        if (supplierProduct is null)
                        {
                            _traceability.AddSupplierProduct(new SupplierProduct
                            {
                                SupplierId = command.SupplierId,
                                ProductId = line.Product.Id,
                                NextItemSequence = 1,
                                IsActive = true,
                                CreatedAt = _clock.UtcNow,
                                UpdatedAt = _clock.UtcNow
                            });
                        }
                        else if (!supplierProduct.IsActive)
                        {
                            return Result<CreatePurchaseResult>.Failure(
                                "purchasing.supplier_product_inactive",
                                $"Supplier/Product mapping for '{line.Product.Name}' is inactive.");
                        }
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

                    if (command.ReceiveStockImmediately)
                    {
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

                        var receiptUnitCost = PurchaseReceiptCost.Round(effectiveLineCost / line.Quantity.BaseQuantity);
                        var lotId = await _costs.AddCarryingValueAndLotWithIdAsync(
                            line.Product.Id, line.Quantity.BaseQuantity, receiptUnitCost,
                            movement.Id, item.Id, ct);

                        if (line.Product.TrackingMode == TrackingMode.Serialized || line.Product.TrackingMode == TrackingMode.IndividualPiece || line.Product.TrackingMode == TrackingMode.Container)
                        {
                            if (_physicalUnits is null)
                            {
                                return Result<CreatePurchaseResult>.Failure(
                                    "inventory.physical_unit_authority_unavailable",
                                    "Physical-unit creation authority is unavailable.");
                            }
                            var creation = await _physicalUnits.CreateAsync(
                                command.SupplierId,
                                line.Product.Id,
                                line.Input.SerializedUnits.Select((identity, unitIndex) => new PhysicalUnitCreationEntry(
                                    identity.SerialNumber, identity.Imei1, identity.Imei2,
                                    InventoryUnitStatus.InStock,
                                    PurchaseReceiptCost.PhysicalAcquisitionCost(effectiveLineCost, line.Input.SerializedUnits.Count, unitIndex), lotId,
                                    InventoryUnitOriginType.Purchase,
                                    SourcePurchaseItemId: item.Id)).ToArray(),
                                ct);
                            if (!creation.IsSuccess || creation.Value is null)
                            {
                                return Result<CreatePurchaseResult>.Failure(creation.Error!.Code, creation.Error.Message);
                            }
                            foreach (var unit in creation.Value)
                            {
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
                        costState.TotalInventoryCost = PurchaseReceiptCost.Round(costState.TotalInventoryCost +
                            effectiveLineCost - PurchaseReceiptCost.Round(line.Quantity.BaseQuantity * receiptUnitCost));
                        costState.MovingAverageCost = PurchaseReceiptCost.Round(costState.TotalInventoryCost / costState.CostedQty);
                        var receivedLot = await _inventory.GetInventoryLotForUpdateAsync(lotId, ct);
                        if (receivedLot is not null)
                        {
                            receivedLot.OriginalUnitCost = effectiveBaseCost;
                        }
                    }
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

                if (_outcomeLedger is not null)
                {
                    await _outcomeLedger.RecordSuccessAsync(
                        command.ClientOperationId,
                        "Purchase",
                        purchase.Id,
                        purchase.PurchaseNumber,
                        actorId: command.CreatedBy,
                        payloadFingerprint: fingerprint,
                        cancellationToken: ct);
                }

                await _unitOfWork.SaveChangesAsync(ct);
                return Result<CreatePurchaseResult>.Success(new(
                    purchase.Id, purchase.PurchaseNumber, purchase.GrandTotal, false));
            }
            catch (BusinessRuleException ex)
            {
                return Result<CreatePurchaseResult>.Failure(ex.Code, ex.Message);
            }
        }, cancellationToken);

        if (!result.IsSuccess && ownsOutcome)
        {
            var settled = await _transactions.ExecuteAsync(async ct =>
            {
                await _operationLock.AcquireAsync(command.ClientOperationId, ct);
                var latest = await _outcomeLedger.GetOutcomeAsync(command.ClientOperationId, ct);
                var purchase = await _purchases.GetPurchaseByClientOperationIdAsync(command.ClientOperationId, ct);
                if (latest is not null)
                {
                    Result<CreatePurchaseResult> replay;
                    if (latest.OperationType != "Purchase" || latest.ActorId != command.CreatedBy || latest.PayloadFingerprint != fingerprint)
                        replay = Result<CreatePurchaseResult>.Failure("idempotency.payload_mismatch", "Operation identity now belongs to a different purchase intent.");
                    else if (latest.State == OperationOutcomeState.Succeeded && latest.WasCommitted &&
                             purchase is not null && latest.EntityId == purchase.Id)
                        replay = Result<CreatePurchaseResult>.Success(new(purchase.Id, purchase.PurchaseNumber, purchase.GrandTotal, true));
                    else
                        replay = Result<CreatePurchaseResult>.Failure(latest.ErrorCode ?? "idempotency.outcome_unknown",
                            latest.ErrorMessage ?? "Reconcile the previous purchase outcome.");
                    return (Result: replay, Recorded: false);
                }
                if (purchase is not null)
                {
                    return (Result: Result<CreatePurchaseResult>.Failure("idempotency.legacy_purchase_requires_reconciliation",
                        "Existing purchase has no canonical intent evidence."), Recorded: false);
                }
                await _outcomeLedger.RecordFailureAsync(command.ClientOperationId, "Purchase",
                    result.Error!.Code, result.Error.Message, actorId: command.CreatedBy,
                    payloadFingerprint: fingerprint, cancellationToken: ct);
                // Preserve failure evidence in a committed transaction, without replacing a newer outcome.
                return (Result: result, Recorded: true);
            }, cancellationToken);
            return settled.Result;
        }

        return result;
    }

    private async Task<IReadOnlyList<PreparedPurchaseLine>> PrepareLinesAsync(
        IReadOnlyList<CreatePurchaseLineInput> lines,
        bool receiveStockImmediately,
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

            var isPiece = product.TrackingMode == TrackingMode.Serialized || product.TrackingMode == TrackingMode.IndividualPiece;
            var isContainer = product.TrackingMode == TrackingMode.Container;

            if (isContainer)
            {
                if (!QuantityMath.IsWhole(input.EnteredQuantity))
                {
                    throw new BusinessRuleException(
                        "catalog.container_quantity_whole",
                        $"Container quantity for '{product.Name}' must be an exact whole integer count.");
                }

                if (!QuantityMath.IsWhole(productUnit.FactorToBaseUnit))
                {
                    throw new BusinessRuleException(
                        "catalog.container_conversion_whole",
                        $"Container conversion factor for '{product.Name}' must be an exact whole integer count.");
                }
            }

            var quantity = TransactionQuantitySnapshot.Create(
                productUnit, input.EnteredQuantity, product.TrackingMode);

            if (isPiece || isContainer)
            {
                if (string.IsNullOrWhiteSpace(product.Sku))
                {
                    throw new BusinessRuleException(
                        "purchasing.sku_required_for_tracking",
                        $"Tracked product '{product.Name}' requires a permanent SKU before receipt.");
                }

                var requiredUnitCountDecimal = isContainer
                    ? quantity.EnteredQuantity
                    : quantity.BaseQuantity;

                if (requiredUnitCountDecimal <= 0 || requiredUnitCountDecimal > 100_000 || !QuantityMath.IsWhole(requiredUnitCountDecimal))
                {
                    throw new BusinessRuleException(
                        "purchasing.physical_unit_count_out_of_range",
                        $"Physical unit count must be a positive whole integer within valid range (1 - 100,000). Evaluated count: {requiredUnitCountDecimal}.");
                }

                var requiredUnitCount = decimal.ToInt32(requiredUnitCountDecimal);

                if (!receiveStockImmediately && input.SerializedUnits.Count == 0)
                {
                    // An order reserves no physical identity; require manufacturer
                    // identities when its physical receipt actually occurs.
                    result.Add(new(input, product, productUnit, quantity, Money(quantity.EnteredQuantity * input.EnteredUnitCost)));
                    continue;
                }

                var lineInput = input;
                if (input.SerializedUnits.Count == 0 && !product.SerialTrackingEnabled && !product.ImeiTrackingEnabled)
                {
                    var autoList = new List<SerializedIdentityInput>(requiredUnitCount);
                    for (var i = 0; i < requiredUnitCount; i++)
                    {
                        autoList.Add(new SerializedIdentityInput(null, null, null));
                    }
                    lineInput = input with { SerializedUnits = autoList };
                }
                else if (input.SerializedUnits.Count != requiredUnitCount)
                {
                    var unitLabel = isContainer ? "container" : "individual piece";
                    throw new BusinessRuleException(
                        "purchasing.serial_count_mismatch",
                        $"{unitLabel} identities for '{product.Name}' must equal {unitLabel} count ({requiredUnitCount}).");
                }

                ValidateIdentityBatch(product, lineInput.SerializedUnits);
                var lineTotal = Money(quantity.EnteredQuantity * lineInput.EnteredUnitCost);
                result.Add(new(lineInput, product, productUnit, quantity, lineTotal));
            }
            else if (input.SerializedUnits.Count > 0)
            {
                throw new BusinessRuleException(
                    "purchasing.serials_not_allowed",
                    $"Product '{product.Name}' does not support individual piece tracking.");
            }
            else
            {
                ValidateIdentityBatch(product, input.SerializedUnits);
                var lineTotal = Money(quantity.EnteredQuantity * input.EnteredUnitCost);
                result.Add(new(input, product, productUnit, quantity, lineTotal));
            }
        }
        return result;
    }

    private static void ValidateIdentityBatch(
        Product product,
        IReadOnlyList<SerializedIdentityInput> identities)
    {
        if (product.TrackingMode != TrackingMode.Serialized && product.TrackingMode != TrackingMode.IndividualPiece && product.TrackingMode != TrackingMode.Container)
        {
            return;
        }

        var serials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var imeis = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var identity in identities)
        {
            var serial = NormalizeSerial(identity.SerialNumber);
            var imei1 = NormalizeImei(identity.Imei1);
            var imei2 = NormalizeImei(identity.Imei2);

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

    private static string? NormalizeSerial(string? value) =>
        IdentityNormalizationRules.NormalizeOptionalSerialNumber(value);

    private static string? NormalizeImei(string? value) =>
        IdentityNormalizationRules.NormalizeOptionalImei(value);

    private static decimal Money(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    private static decimal Cost(decimal value) =>
        decimal.Round(value, 6, MidpointRounding.AwayFromZero);
}

