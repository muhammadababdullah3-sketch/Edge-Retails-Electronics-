using System.Globalization;
using System.Text;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Purchasing;

namespace EdgeRetails.Application.Features.Purchasing;

public sealed record ReceiveProductIntakeCommand(
    Guid PurchaseId,
    Guid ProductId,
    Guid ProductUnitId,
    decimal EnteredQuantity,
    decimal? EnteredUnitCost,
    IReadOnlyList<SerializedIdentityInput> SerializedUnits,
    Guid CreatedBy,
    Guid ClientOperationId,
    string? Note = null);

public sealed record CommittedInventoryUnitDto(
    Guid Id,
    string TrackingCode,
    long ItemSequence,
    string? SerialNumber,
    string? Imei1,
    string? Imei2,
    decimal AcquisitionCost);

public sealed record ReceiveProductIntakeResult(
    Guid PurchaseId,
    Guid ProductId,
    string ProductName,
    string ProductCode,
    string? ModelCode,
    string? CompanyCode,
    string? CategorySymbol,
    string DealerCode,
    decimal ReceivedQuantity,
    decimal BaseQuantity,
    TrackingMode TrackingMode,
    IReadOnlyList<CommittedInventoryUnitDto> CommittedUnits,
    bool WasExisting);

public sealed class ReceiveProductIntakeHandler
{
    private readonly IPurchasingRepository _purchases;
    private readonly IPartyRepository _parties;
    private readonly ICatalogRepository _catalog;
    private readonly IInventoryRepository _inventory;
    private readonly IInventoryCostAllocator _costs;
    private readonly ITraceabilityRepository _traceability;
    private readonly IOperationLock _operationLock;
    private readonly IResourceLock _resourceLock;
    private readonly IBusinessAuditWriter _audit;
    private readonly IClock _clock;
    private readonly ITransactionRunner _transactions;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ISequenceHighWaterService _highWaterService;

    public ReceiveProductIntakeHandler(
        IPurchasingRepository purchases,
        IPartyRepository parties,
        ICatalogRepository catalog,
        IInventoryRepository inventory,
        IInventoryCostAllocator costs,
        ITraceabilityRepository traceability,
        IOperationLock operationLock,
        IResourceLock resourceLock,
        IBusinessAuditWriter audit,
        IClock clock,
        ITransactionRunner transactions,
        IApplicationPermissionAuthorizer authorization,
        IUnitOfWork unitOfWork,
        ISequenceHighWaterService? highWaterService = null,
        IOperationOutcomeLedger? outcomeLedger = null)
    {
        _purchases = purchases;
        _parties = parties;
        _catalog = catalog;
        _inventory = inventory;
        _costs = costs;
        _traceability = traceability;
        _operationLock = operationLock;
        _resourceLock = resourceLock;
        _audit = audit;
        _clock = clock;
        _transactions = transactions;
        _authorization = authorization;
        _unitOfWork = unitOfWork;
        _highWaterService = highWaterService ?? NullSequenceHighWaterService.Instance;
        _outcomeLedger = outcomeLedger;
    }

    private readonly IOperationOutcomeLedger? _outcomeLedger;

    public IOperationOutcomeLedger? OutcomeLedger => _outcomeLedger;

    public static string ComputePayloadFingerprint(
        Guid purchaseId,
        Guid purchaseItemId,
        Guid productId,
        Guid supplierId,
        Guid productUnitId,
        decimal enteredQuantity,
        decimal? enteredUnitCost,
        IReadOnlyList<SerializedIdentityInput>? serializedUnits,
        string? note = null)
    {
        var sb = new StringBuilder();
        sb.Append(purchaseId.ToString("D")).Append('|')
          .Append(purchaseItemId.ToString("D")).Append('|')
          .Append(productId.ToString("D")).Append('|')
          .Append(supplierId.ToString("D")).Append('|')
          .Append(productUnitId.ToString("D")).Append('|')
          .Append(enteredQuantity.ToString("0.####", CultureInfo.InvariantCulture)).Append('|')
          .Append(enteredUnitCost.HasValue ? enteredUnitCost.Value.ToString("0.####", CultureInfo.InvariantCulture) : "").Append('|')
          .Append(note?.Trim() ?? "").Append('|');

        if (serializedUnits != null && serializedUnits.Count > 0)
        {
            var normalized = serializedUnits
                .Select(u => $"{NormalizeIdentity(u.SerialNumber) ?? ""}:{NormalizeIdentity(u.Imei1) ?? ""}:{NormalizeIdentity(u.Imei2) ?? ""}")
                .OrderBy(x => x, StringComparer.Ordinal);
            sb.Append(string.Join(";", normalized));
        }

        return OperationPayloadFingerprint.ComputeSha256(sb.ToString());
    }

    public static string ComputeFallbackFingerprint(ReceiveProductIntakeCommand command)
    {
        var sb = new StringBuilder();
        sb.Append(command.PurchaseId.ToString("D")).Append('|')
          .Append(command.ProductId.ToString("D")).Append('|')
          .Append(command.ProductUnitId.ToString("D")).Append('|')
          .Append(command.EnteredQuantity.ToString("0.####", CultureInfo.InvariantCulture)).Append('|')
          .Append(command.EnteredUnitCost.HasValue ? command.EnteredUnitCost.Value.ToString("0.####", CultureInfo.InvariantCulture) : "").Append('|')
          .Append(command.Note?.Trim() ?? "").Append('|');

        if (command.SerializedUnits != null && command.SerializedUnits.Count > 0)
        {
            var normalized = command.SerializedUnits
                .Select(u => $"{NormalizeIdentity(u.SerialNumber) ?? ""}:{NormalizeIdentity(u.Imei1) ?? ""}:{NormalizeIdentity(u.Imei2) ?? ""}")
                .OrderBy(x => x, StringComparer.Ordinal);
            sb.Append(string.Join(";", normalized));
        }

        return OperationPayloadFingerprint.ComputeSha256(sb.ToString());
    }

    public async Task<Result<ReceiveProductIntakeResult>> HandleAsync(
        ReceiveProductIntakeCommand command,
        CancellationToken cancellationToken = default)
    {
        if (command.ClientOperationId == Guid.Empty)
        {
            return Result<ReceiveProductIntakeResult>.Failure(
                "receiving.operation_id_required", "Client operation id is required.");
        }

        var computedFingerprint = ComputeFallbackFingerprint(command);

        if (command.PurchaseId == Guid.Empty)
        {
            var fail = Result<ReceiveProductIntakeResult>.Failure(
                "receiving.purchase_required", "Purchase id is required for physical intake.");
            if (_outcomeLedger is not null)
            {
                await _outcomeLedger.RecordFailureAsync(
                    command.ClientOperationId,
                    "InventoryMovement",
                    fail.Error!.Code,
                    fail.Error.Message,
                    actorId: command.CreatedBy,
                    payloadFingerprint: computedFingerprint,
                    cancellationToken: cancellationToken);
            }
            return fail;
        }

        if (command.ProductId == Guid.Empty)
        {
            var fail = Result<ReceiveProductIntakeResult>.Failure(
                "receiving.product_required", "Product id is required. Physical intake session is locked to exactly one product.");
            if (_outcomeLedger is not null)
            {
                await _outcomeLedger.RecordFailureAsync(
                    command.ClientOperationId,
                    "InventoryMovement",
                    fail.Error!.Code,
                    fail.Error.Message,
                    actorId: command.CreatedBy,
                    payloadFingerprint: computedFingerprint,
                    cancellationToken: cancellationToken);
            }
            return fail;
        }

        if (command.EnteredQuantity <= 0)
        {
            var fail = Result<ReceiveProductIntakeResult>.Failure(
                "receiving.quantity_positive", "Entered quantity must be greater than zero.");
            if (_outcomeLedger is not null)
            {
                await _outcomeLedger.RecordFailureAsync(
                    command.ClientOperationId,
                    "InventoryMovement",
                    fail.Error!.Code,
                    fail.Error.Message,
                    actorId: command.CreatedBy,
                    payloadFingerprint: computedFingerprint,
                    cancellationToken: cancellationToken);
            }
            return fail;
        }

        var result = await _transactions.ExecuteAsync(async ct =>
        {
            var authorization = await _authorization.AuthorizeAsync(
                command.CreatedBy,
                PermissionKeys.PurchasingManage,
                ct);
            if (!authorization.IsSuccess)
            {
                return Result<ReceiveProductIntakeResult>.Failure(
                    authorization.Error!.Code,
                    authorization.Error.Message);
            }

            await _operationLock.AcquireAsync(command.ClientOperationId, ct);

            // Invariant check: Purchase existence and state
            var purchase = await _purchases.GetPurchaseForUpdateAsync(command.PurchaseId, ct);
            if (purchase is null)
            {
                return Result<ReceiveProductIntakeResult>.Failure(
                    "purchasing.purchase_not_found", $"Purchase '{command.PurchaseId}' was not found.");
            }

            if (purchase.Status == PurchaseStatus.Voided)
            {
                return Result<ReceiveProductIntakeResult>.Failure(
                    "purchasing.purchase_voided", "Cannot receive stock against a voided purchase.");
            }

            // Invariant check: Product must exist on this purchase
            var purchaseItems = await _purchases.GetPurchaseItemsAsync(purchase.Id, ct);
            var purchaseItem = purchaseItems.FirstOrDefault(x => x.ProductId == command.ProductId);
            if (purchaseItem is null)
            {
                return Result<ReceiveProductIntakeResult>.Failure(
                    "purchasing.product_not_on_purchase",
                    $"Product '{command.ProductId}' does not belong to purchase '{purchase.PurchaseNumber}'. Receiving session is strictly locked to products on the selected purchase.");
            }

            // Compute authoritative material payload fingerprint with resolved domain identities
            computedFingerprint = ComputePayloadFingerprint(
                command.PurchaseId,
                purchaseItem.Id,
                command.ProductId,
                purchase.SupplierId,
                command.ProductUnitId,
                command.EnteredQuantity,
                command.EnteredUnitCost,
                command.SerializedUnits,
                command.Note);

            // Check canonical outcome ledger for existing execution or payload mismatch
            if (_outcomeLedger is not null)
            {
                var existingOutcome = await _outcomeLedger.GetOutcomeAsync(command.ClientOperationId, ct);
                if (existingOutcome is not null)
                {
                    if (!string.IsNullOrEmpty(existingOutcome.PayloadFingerprint) &&
                        !string.Equals(existingOutcome.PayloadFingerprint, computedFingerprint, StringComparison.Ordinal))
                    {
                        return Result<ReceiveProductIntakeResult>.Failure(
                            "idempotency.payload_mismatch",
                            "Operation was previously submitted with a different payload.");
                    }
                }
            }

            // Check if command.ClientOperationId was already executed (Crash-After-Commit / Idempotency Replay)
            var existingMovement = await _inventory.GetMovementByCorrelationIdAsync(command.ClientOperationId, ct);
            if (existingMovement is not null)
            {
                if (existingMovement.ReferenceId != purchase.Id || existingMovement.ProductId != command.ProductId)
                {
                    return Result<ReceiveProductIntakeResult>.Failure(
                        "idempotency.payload_mismatch",
                        "Operation was previously submitted with a different purchase or product.");
                }

                if (_outcomeLedger is not null)
                {
                    var existingOutcome = await _outcomeLedger.GetOutcomeAsync(command.ClientOperationId, ct);
                    if (existingOutcome is not null &&
                        !string.IsNullOrEmpty(existingOutcome.PayloadFingerprint) &&
                        !string.Equals(existingOutcome.PayloadFingerprint, computedFingerprint, StringComparison.Ordinal))
                    {
                        return Result<ReceiveProductIntakeResult>.Failure(
                            "idempotency.payload_mismatch",
                            "Operation was previously submitted with a different payload.");
                    }
                }

                var productExisting = await _catalog.GetProductAsync(command.ProductId, ct);
                var productUnitExisting = await _catalog.GetProductUnitAsync(command.ProductUnitId, ct);
                var supplierExisting = await _parties.GetSupplierAsync(purchase.SupplierId, ct);
                var dealerCodeExisting = supplierExisting?.DealerCode ?? string.Empty;

                var existingUnits = await _inventory.GetUnitsForMovementAsync(existingMovement.Id, ct);
                if (existingUnits.Count == 0 && (productExisting?.TrackingMode == TrackingMode.Serialized || productExisting?.TrackingMode == TrackingMode.IndividualPiece || productExisting?.TrackingMode == TrackingMode.Container))
                {
                    existingUnits = await _inventory.GetUnitsByPurchaseItemAsync(purchaseItem.Id, ct);
                }

                // Check payload mismatch on quantity and serials
                if (existingUnits.Count > 0)
                {
                    var isContainerExisting = productExisting?.TrackingMode == TrackingMode.Container;
                    var expectedUnitsCount = isContainerExisting
                        ? decimal.ToInt32(command.EnteredQuantity)
                        : decimal.ToInt32(command.EnteredQuantity * (productUnitExisting?.FactorToBaseUnit ?? 1m));

                    if (existingUnits.Count != expectedUnitsCount)
                    {
                        return Result<ReceiveProductIntakeResult>.Failure(
                            "idempotency.payload_mismatch",
                            "Operation was previously submitted with a different quantity.");
                    }

                    if (command.SerializedUnits.Count > 0)
                    {
                        var existingSerials = existingUnits.Select(u => NormalizeIdentity(u.SerialNumber)).Where(s => s != null).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var existingImeis = existingUnits.Select(u => NormalizeIdentity(u.Imei1)).Where(s => s != null).ToHashSet(StringComparer.OrdinalIgnoreCase);

                        foreach (var inputUnit in command.SerializedUnits)
                        {
                            var s = NormalizeIdentity(inputUnit.SerialNumber);
                            var im1 = NormalizeIdentity(inputUnit.Imei1);
                            if (s != null && !existingSerials.Contains(s))
                            {
                                return Result<ReceiveProductIntakeResult>.Failure(
                                    "idempotency.payload_mismatch",
                                    "Operation was previously submitted with different serial numbers.");
                            }
                            if (im1 != null && !existingImeis.Contains(im1))
                            {
                                return Result<ReceiveProductIntakeResult>.Failure(
                                    "idempotency.payload_mismatch",
                                    "Operation was previously submitted with different IMEIs.");
                            }
                        }
                    }
                }

                if (_outcomeLedger is not null)
                {
                    await _outcomeLedger.RecordSuccessAsync(
                        command.ClientOperationId,
                        "InventoryMovement",
                        existingMovement.Id,
                        existingMovement.ReferenceId?.ToString("D"),
                        actorId: command.CreatedBy,
                        payloadFingerprint: computedFingerprint,
                        cancellationToken: ct);
                }

                var committedExistingUnits = existingUnits
                    .OrderBy(u => u.ItemSequence)
                    .ThenBy(u => u.CreatedAt)
                    .Select(u => new CommittedInventoryUnitDto(
                        u.Id,
                        u.TrackingCode ?? string.Empty,
                        u.ItemSequence ?? 0,
                        u.SerialNumber,
                        u.Imei1,
                        u.Imei2,
                        u.AcquisitionCost))
                    .ToList();

                var quantitySnapshotExisting = (productUnitExisting != null && productExisting != null)
                    ? TransactionQuantitySnapshot.Create(productUnitExisting, command.EnteredQuantity, productExisting.TrackingMode)
                    : null;
                var baseQuantityExisting = quantitySnapshotExisting?.BaseQuantity ?? command.EnteredQuantity;

                Company? companyExisting = null;
                if (productExisting?.CompanyId.HasValue == true)
                {
                    companyExisting = await _catalog.GetCompanyAsync(productExisting.CompanyId.Value, ct);
                }

                Category? categoryExisting = null;
                if (productExisting?.CategoryId.HasValue == true)
                {
                    categoryExisting = await _catalog.GetCategoryAsync(productExisting.CategoryId.Value, ct);
                }

                return Result<ReceiveProductIntakeResult>.Success(new ReceiveProductIntakeResult(
                    purchase.Id,
                    command.ProductId,
                    productExisting?.Name ?? string.Empty,
                    productExisting?.Sku ?? string.Empty,
                    productExisting?.ModelCode,
                    companyExisting?.Code,
                    categoryExisting?.IdentitySymbol,
                    dealerCodeExisting,
                    command.EnteredQuantity,
                    baseQuantityExisting,
                    productExisting?.TrackingMode ?? TrackingMode.Quantity,
                    committedExistingUnits,
                    WasExisting: true));
            }

            // Invariant check: Product master data
            var product = await _catalog.GetProductAsync(command.ProductId, ct);
            if (product is null)
            {
                return Result<ReceiveProductIntakeResult>.Failure(
                    "purchasing.product_not_found", "Product master was not found.");
            }

            if (!product.IsActive)
            {
                return Result<ReceiveProductIntakeResult>.Failure(
                    "purchasing.product_inactive", $"Product '{product.Name}' is inactive.");
            }

            // Unit validation
            var productUnit = await _catalog.GetProductUnitAsync(command.ProductUnitId, ct);
            if (productUnit is null || productUnit.ProductId != product.Id || !productUnit.IsActive || !productUnit.CanPurchase)
            {
                return Result<ReceiveProductIntakeResult>.Failure(
                    "purchasing.product_unit_not_allowed",
                    $"Selected unit is not valid or not allowed for purchase on '{product.Name}'.");
            }

            // Supplier resolution
            var supplier = await _parties.GetSupplierAsync(purchase.SupplierId, ct);
            if (supplier is null)
            {
                return Result<ReceiveProductIntakeResult>.Failure(
                    "purchasing.supplier_not_found", "Supplier was not found.");
            }

            var dealerCode = supplier.DealerCode;
            if (string.IsNullOrWhiteSpace(dealerCode))
            {
                return Result<ReceiveProductIntakeResult>.Failure(
                    "parties.dealer_code_missing", "Supplier lacks an authoritative dealer code.");
            }

            // Match the canonical product lock used by purchase creation before reading
            // or creating shared stock/cost rows. Supplier-pair locking alone cannot
            // serialize receipts for the same product from different suppliers.
            await _resourceLock.AcquireAsync("product", product.Id, ct);

            // Stocktake check
            if (await _inventory.IsProductBlockedByCountingStocktakeAsync(product.Id, ct))
            {
                return Result<ReceiveProductIntakeResult>.Failure(
                    "inventory.stocktake_blocks_product",
                    $"Product '{product.Name}' is locked by an active stocktake.");
            }

            var stock = await _inventory.GetStockBalanceForUpdateAsync(product.Id, ct);
            if (stock is null)
            {
                stock = new StockBalance { ProductId = product.Id };
                _inventory.AddStockBalance(stock);
            }

            var quantitySnapshot = TransactionQuantitySnapshot.Create(
                productUnit,
                command.EnteredQuantity,
                product.TrackingMode);
            var baseQuantity = quantitySnapshot.BaseQuantity;

            var effectiveUnitCost = command.EnteredUnitCost.HasValue
                ? Cost(command.EnteredUnitCost.Value / productUnit.FactorToBaseUnit)
                : purchaseItem.EffectiveBaseUnitCost;

            var isPiece = product.TrackingMode == TrackingMode.Serialized || product.TrackingMode == TrackingMode.IndividualPiece;
            var isContainer = product.TrackingMode == TrackingMode.Container;
            var isBulk = product.TrackingMode == TrackingMode.Quantity || product.TrackingMode == TrackingMode.Length;

            // Tracking policy validation
            if (isBulk && command.SerializedUnits.Count > 0)
            {
                var label = product.TrackingMode == TrackingMode.Length ? "Length" : "Quantity";
                return Result<ReceiveProductIntakeResult>.Failure(
                    "purchasing.serials_not_allowed",
                    $"{label} products update stock balances and lots only; they do not accept individual piece identities.");
            }

            // Enforce that receiving cannot exceed outstanding quantity
            var alreadyReceived = await _inventory.GetPurchaseItemReceivedBaseQuantityAsync(purchaseItem.Id, ct);
            var outstandingQuantity = QuantityMath.RoundQuantity(purchaseItem.BaseQuantity - alreadyReceived);
            if (baseQuantity > outstandingQuantity)
            {
                return Result<ReceiveProductIntakeResult>.Failure(
                    "purchasing.intake_exceeds_ordered",
                    $"Requested intake quantity ({baseQuantity}) exceeds outstanding quantity ({outstandingQuantity}) for product '{product.Name}'.");
            }

            // Lock resources
            await _resourceLock.AcquireAsync("purchase-item", purchaseItem.Id, ct);
            await _resourceLock.AcquireAsync("supplier-product", $"{supplier.Id}:{product.Id}", ct);

            // Execute inventory movement
            var before = stock.SellableQty;
            stock.ApplyDelta(InventoryBucket.Sellable, baseQuantity);

            var movement = new InventoryMovement
            {
                ProductId = product.Id,
                MovementType = InventoryMovementType.PurchaseIn,
                ReferenceType = "PURCHASE",
                ReferenceId = purchase.Id,
                UnitCostSnapshot = effectiveUnitCost,
                ActorId = command.CreatedBy,
                OccurredAt = _clock.UtcNow,
                CorrelationId = command.ClientOperationId,
                Note = command.Note?.Trim() ?? $"Intake for {purchase.PurchaseNumber}"
            };
            _inventory.AddMovement(movement);
            _inventory.AddMovementEffect(new InventoryMovementEffect
            {
                MovementId = movement.Id,
                StockBucket = InventoryBucket.Sellable,
                QuantityDelta = baseQuantity,
                QuantityBefore = before,
                QuantityAfter = stock.SellableQty
            });

            var lotId = await _costs.AddCarryingValueAndLotWithIdAsync(
                product.Id,
                baseQuantity,
                effectiveUnitCost,
                movement.Id,
                purchaseItem.Id,
                ct);

            var committedUnits = new List<CommittedInventoryUnitDto>();

            if (isPiece || isContainer)
            {
                if (string.IsNullOrWhiteSpace(product.Sku))
                {
                    return Result<ReceiveProductIntakeResult>.Failure(
                        "purchasing.sku_required_for_tracking",
                        $"Tracked product '{product.Name}' requires an authoritative ProductCode (SKU) before receipt.");
                }

                var requiredUnitCount = isContainer
                    ? decimal.ToInt32(quantitySnapshot.EnteredQuantity)
                    : decimal.ToInt32(baseQuantity);

                if (requiredUnitCount <= 0 || (!isContainer && baseQuantity != requiredUnitCount))
                {
                    return Result<ReceiveProductIntakeResult>.Failure(
                        "purchasing.invalid_base_quantity",
                        $"Physical intake requires whole integer units. Base quantity: {baseQuantity}.");
                }

                var lineUnits = command.SerializedUnits;
                if (lineUnits.Count == 0 && !product.SerialTrackingEnabled && !product.ImeiTrackingEnabled)
                {
                    var autoList = new List<SerializedIdentityInput>(requiredUnitCount);
                    for (var i = 0; i < requiredUnitCount; i++)
                    {
                        autoList.Add(new SerializedIdentityInput(null, null, null));
                    }
                    lineUnits = autoList;
                }
                else if (lineUnits.Count != requiredUnitCount)
                {
                    var unitLabel = isContainer ? "container" : "individual piece";
                    return Result<ReceiveProductIntakeResult>.Failure(
                        "purchasing.serial_count_mismatch",
                        $"{unitLabel} identities for '{product.Name}' must equal {unitLabel} count ({requiredUnitCount}), received {lineUnits.Count}.");
                }

                // Validate identities
                var validationError = ValidateIdentities(product, lineUnits);
                if (validationError != null)
                {
                    return Result<ReceiveProductIntakeResult>.Failure(
                        validationError.Code,
                        validationError.Message);
                }

                // Check duplicates against DB
                foreach (var identity in lineUnits)
                {
                    var serial = NormalizeIdentity(identity.SerialNumber);
                    var imei1 = NormalizeIdentity(identity.Imei1);
                    var imei2 = NormalizeIdentity(identity.Imei2);
                    if (serial is not null || imei1 is not null || imei2 is not null)
                    {
                        if (await _inventory.InventoryIdentityExistsAsync(serial, imei1, imei2, ct))
                        {
                            return Result<ReceiveProductIntakeResult>.Failure(
                                "purchasing.identity_already_exists",
                                "A received Serial or IMEI already exists in inventory history.");
                        }
                    }
                }

                // Resolve SupplierProduct sequence authority
                var supplierProduct = await _traceability.GetSupplierProductForUpdateAsync(supplier.Id, product.Id, ct);
                if (supplierProduct is null)
                {
                    supplierProduct = new SupplierProduct
                    {
                        SupplierId = supplier.Id,
                        ProductId = product.Id,
                        NextItemSequence = 1,
                        IsActive = true,
                        CreatedAt = _clock.UtcNow,
                        UpdatedAt = _clock.UtcNow
                    };
                    _traceability.AddSupplierProduct(supplierProduct);
                }

                var machineSeq = _highWaterService.GetSupplierProductHighWater(supplierProduct.SupplierId, supplierProduct.ProductId);
                if (machineSeq > supplierProduct.NextItemSequence)
                {
                    supplierProduct.NextItemSequence = machineSeq;
                }

                var firstSequence = supplierProduct.NextItemSequence;
                supplierProduct.NextItemSequence = checked(firstSequence + requiredUnitCount);
                supplierProduct.UpdatedAt = _clock.UtcNow;
                supplierProduct.Version++;

                _highWaterService.RecordSupplierProductHighWater(
                    supplierProduct.SupplierId,
                    supplierProduct.ProductId,
                    supplierProduct.NextItemSequence);

                for (var i = 0; i < requiredUnitCount; i++)
                {
                    var identity = lineUnits[i];
                    var itemSequence = checked(firstSequence + i);
                    var trackingCode = TraceabilityCodeRules.BuildTrackingCode(
                        dealerCode,
                        product.Sku!,
                        itemSequence);

                    var unit = new InventoryUnit
                    {
                        ProductId = product.Id,
                        SupplierProductId = supplierProduct.Id,
                        OriginType = InventoryUnitOriginType.Purchase,
                        ItemSequence = itemSequence,
                        TrackingCode = trackingCode,
                        SupplierCodeSnapshot = dealerCode,
                        ProductSkuSnapshot = product.Sku,
                        SerialNumber = NormalizeIdentity(identity.SerialNumber),
                        Imei1 = NormalizeIdentity(identity.Imei1),
                        Imei2 = NormalizeIdentity(identity.Imei2),
                        Status = InventoryUnitStatus.InStock,
                        AcquisitionCost = effectiveUnitCost,
                        InventoryLotId = lotId,
                        SourcePurchaseItemId = purchaseItem.Id,
                        CreatedAt = _clock.UtcNow,
                        Version = 1
                    };

                    _inventory.AddInventoryUnit(unit);
                    _purchases.AddPurchaseItemUnit(new PurchaseItemUnit
                    {
                        PurchaseItemId = purchaseItem.Id,
                        InventoryUnitId = unit.Id
                    });
                    _inventory.AddMovementUnit(new InventoryMovementUnit
                    {
                        MovementId = movement.Id,
                        InventoryUnitId = unit.Id,
                        FromStatus = null,
                        ToStatus = InventoryUnitStatus.InStock
                    });

                    committedUnits.Add(new CommittedInventoryUnitDto(
                        unit.Id,
                        trackingCode,
                        itemSequence,
                        unit.SerialNumber,
                        unit.Imei1,
                        unit.Imei2,
                        effectiveUnitCost));
                }
            }

            var costState = await _inventory.GetCostStateForUpdateAsync(product.Id, ct)
                ?? throw new BusinessRuleException(
                    "inventory.cost_state_missing",
                    "Inventory cost state was not created for the physical intake.");

            costState.LastPurchaseCost = effectiveUnitCost;
            costState.LastPurchaseAt = _clock.UtcNow;

            if (_outcomeLedger is not null)
            {
                await _outcomeLedger.RecordSuccessAsync(
                    command.ClientOperationId,
                    "InventoryMovement",
                    movement.Id,
                    movement.ReferenceId?.ToString("D"),
                    actorId: command.CreatedBy,
                    payloadFingerprint: computedFingerprint,
                    cancellationToken: ct);
            }

            await _unitOfWork.SaveChangesAsync(ct);

            // Resolve company/category details for response
            Company? company = null;
            if (product.CompanyId.HasValue)
            {
                company = await _catalog.GetCompanyAsync(product.CompanyId.Value, ct);
            }

            Category? category = null;
            if (product.CategoryId.HasValue)
            {
                category = await _catalog.GetCategoryAsync(product.CategoryId.Value, ct);
            }

            return Result<ReceiveProductIntakeResult>.Success(new ReceiveProductIntakeResult(
                purchase.Id,
                product.Id,
                product.Name,
                product.Sku ?? string.Empty,
                product.ModelCode,
                company?.Code,
                category?.IdentitySymbol,
                dealerCode,
                command.EnteredQuantity,
                baseQuantity,
                product.TrackingMode,
                committedUnits,
                false));
        }, cancellationToken);

        if (!result.IsSuccess && _outcomeLedger is not null && command.ClientOperationId != Guid.Empty)
        {
            await _outcomeLedger.RecordFailureAsync(
                command.ClientOperationId,
                "InventoryMovement",
                result.Error?.Code ?? "receiving.failed",
                result.Error?.Message ?? "Physical receiving failed.",
                actorId: command.CreatedBy,
                payloadFingerprint: computedFingerprint,
                cancellationToken: cancellationToken);
        }

        return result;
    }

    private static Error? ValidateIdentities(
        Product product,
        IReadOnlyList<SerializedIdentityInput> identities)
    {
        var serials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var imeis = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < identities.Count; i++)
        {
            var identity = identities[i];
            var serial = NormalizeIdentity(identity.SerialNumber);
            var imei1 = NormalizeIdentity(identity.Imei1);
            var imei2 = NormalizeIdentity(identity.Imei2);

            if (product.SerialTrackingEnabled && string.IsNullOrWhiteSpace(serial))
            {
                return new("purchasing.serial_required", $"Unit #{i + 1} requires a Serial Number.");
            }

            if (product.ImeiTrackingEnabled && string.IsNullOrWhiteSpace(imei1))
            {
                return new("purchasing.imei_required", $"Unit #{i + 1} requires an IMEI 1.");
            }

            if (serial is not null)
            {
                if (!serials.Add(serial))
                {
                    return new("purchasing.duplicate_serial_in_batch", $"Duplicate serial '{serial}' in current intake batch.");
                }
            }

            if (imei1 is not null)
            {
                if (!imeis.Add(imei1))
                {
                    return new("purchasing.duplicate_imei_in_batch", $"Duplicate IMEI '{imei1}' in current intake batch.");
                }
            }

            if (imei2 is not null)
            {
                if (!imeis.Add(imei2))
                {
                    return new("purchasing.duplicate_imei_in_batch", $"Duplicate IMEI '{imei2}' in current intake batch.");
                }
            }

            if (imei1 is not null && imei2 is not null && string.Equals(imei1, imei2, StringComparison.OrdinalIgnoreCase))
            {
                return new("purchasing.same_imei_on_unit", $"Unit #{i + 1} has identical IMEI 1 and IMEI 2.");
            }
        }

        return null;
    }

    private static string? NormalizeIdentity(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static decimal Cost(decimal value) =>
        decimal.Round(value, 4, MidpointRounding.AwayFromZero);
}
