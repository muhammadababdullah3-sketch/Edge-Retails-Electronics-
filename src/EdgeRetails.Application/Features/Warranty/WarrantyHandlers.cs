using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using EdgeRetails.Domain.Warranty;

namespace EdgeRetails.Application.Features.Warranty;

internal static class WarrantyOperationIdentity
{
    public static string Hash(params string?[] parts)
    {
        var payload = string.Join("\u001F", parts.Select(x => x ?? string.Empty));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    public static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    public static string ManufacturerIdentity(string? serial, string? imei1, string? imei2) =>
        System.Text.Json.JsonSerializer.Serialize(new[]
        {
            IdentityNormalizationRules.NormalizeOptionalSerialNumber(serial),
            IdentityNormalizationRules.NormalizeOptionalImei(imei1),
            IdentityNormalizationRules.NormalizeOptionalImei(imei2)
        });
}

public sealed record WarrantyClaimUnitInput
{
    [JsonConstructor]
    public WarrantyClaimUnitInput(Guid? OriginalInventoryUnitId, string? OriginalIdentitySnapshot = null)
    {
        this.OriginalInventoryUnitId = OriginalInventoryUnitId;
        this.OriginalIdentitySnapshot = OriginalIdentitySnapshot;
    }

    public Guid? OriginalInventoryUnitId { get; init; }
    public string? OriginalIdentitySnapshot { get; init; }

    public WarrantyClaimUnitInput(Guid? originalInventoryUnitId, string? serialNumber, string? imei1, string? imei2)
        : this(originalInventoryUnitId, string.Join(" | ", new[] { serialNumber, imei1, imei2 }.Where(x => !string.IsNullOrWhiteSpace(x))))
    {
    }
}

public sealed record WarrantyClaimItemInput
{
    [JsonConstructor]
    public WarrantyClaimItemInput(
        Guid ProductId,
        decimal Quantity,
        string FaultDescription,
        Guid? OriginalSaleItemId,
        DateOnly? WarrantyValidUntil = null,
        IReadOnlyList<WarrantyClaimUnitInput>? Units = null)
    {
        this.ProductId = ProductId;
        this.Quantity = Quantity;
        this.FaultDescription = FaultDescription;
        this.OriginalSaleItemId = OriginalSaleItemId;
        this.WarrantyValidUntil = WarrantyValidUntil;
        this.Units = Units;
    }

    public Guid ProductId { get; init; }
    public decimal Quantity { get; init; }
    public string FaultDescription { get; init; }
    public Guid? OriginalSaleItemId { get; init; }
    public DateOnly? WarrantyValidUntil { get; init; }
    public IReadOnlyList<WarrantyClaimUnitInput>? Units { get; init; }

    public WarrantyClaimItemInput(Guid ProductId, decimal Quantity, string FaultDescription, Guid? OriginalSaleItemId, IReadOnlyList<WarrantyClaimUnitInput>? Units)
        : this(ProductId, Quantity, FaultDescription, OriginalSaleItemId, null, Units)
    {
    }
}

public sealed record CreateWarrantyClaimCommand
{
    [JsonConstructor]
    public CreateWarrantyClaimCommand(
        Guid CustomerId,
        Guid? OriginalSaleId,
        Guid? SupplierId,
        Guid ActorId,
        IReadOnlyList<WarrantyClaimItemInput> Items,
        Guid ClientOperationId)
    {
        this.CustomerId = CustomerId;
        this.OriginalSaleId = OriginalSaleId;
        this.SupplierId = SupplierId;
        this.ActorId = ActorId;
        this.Items = Items;
        this.ClientOperationId = ClientOperationId;
    }

    public Guid CustomerId { get; init; }
    public Guid? OriginalSaleId { get; init; }
    public Guid? SupplierId { get; init; }
    public Guid ActorId { get; init; }
    public IReadOnlyList<WarrantyClaimItemInput> Items { get; init; }
    public Guid ClientOperationId { get; init; }

    public CreateWarrantyClaimCommand(
        Guid customerId,
        Guid? originalSaleId,
        Guid actorId,
        Guid clientOperationId,
        IReadOnlyList<WarrantyClaimItemInput> items)
        : this(customerId, originalSaleId, null, actorId, items, clientOperationId)
    {
    }
}

public sealed class CreateWarrantyClaimHandler
{
    private readonly IOperationLock? _operationLock;
    private readonly ICatalogRepository _catalog;
    private readonly IPartyRepository _parties;
    private readonly ISalesRepository _sales;
    private readonly IPurchasingRepository _purchases;
    private readonly IInventoryRepository _inventory;
    private readonly ITraceabilityRepository _traceability;
    private readonly IWarrantyRepository _warranty;
    private readonly IResourceLock _resourceLock;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly IDocumentNumberService _numbers;
    private readonly IClock _clock;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;

    public CreateWarrantyClaimHandler(
        ICatalogRepository catalog,
        IPartyRepository parties,
        ISalesRepository sales,
        IPurchasingRepository purchases,
        IInventoryRepository inventory,
        ITraceabilityRepository traceability,
        IWarrantyRepository warranty,
        IResourceLock resourceLock,
        IApplicationPermissionAuthorizer authorization,
        IDocumentNumberService numbers,
        IClock clock,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork,
        IOperationLock? operationLock = null,
        IOperationOutcomeLedger? outcomeLedger = null)
    {
        _operationLock = operationLock;
        _catalog = catalog;
        _parties = parties;
        _sales = sales;
        _purchases = purchases;
        _inventory = inventory;
        _traceability = traceability;
        _warranty = warranty;
        _resourceLock = resourceLock;
        _authorization = authorization;
        _numbers = numbers;
        _clock = clock;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
        _outcomeLedger = outcomeLedger;
    }

    private readonly IOperationOutcomeLedger? _outcomeLedger;

    public async Task<Result<Guid>> HandleAsync(
        CreateWarrantyClaimCommand command,
        CancellationToken cancellationToken)
    {
        if (command.CustomerId == Guid.Empty || command.OriginalSaleId is null ||
            command.OriginalSaleId == Guid.Empty || command.Items.Count == 0)
        {
            return Result<Guid>.Failure(
                "warranty.sale_customer_items_required",
                "Customer, original Sale, and at least one warranty item are required.");
        }

        if (command.ClientOperationId == Guid.Empty)
        {
            return Result<Guid>.Failure(
                "validation.client_operation_id_required",
                "ClientOperationId is required for Customer Warranty Claim creation.");
        }

        if (command.Items.Any(x => x.OriginalSaleItemId is null || x.OriginalSaleItemId == Guid.Empty) ||
            command.Items.GroupBy(x => x.OriginalSaleItemId).Any(g => g.Count() > 1))
        {
            return Result<Guid>.Failure(
                "warranty.sale_item_required",
                "Every warranty line requires one distinct original SaleItem.");
        }

        var result = await _transactions.ExecuteAsync(async ct =>
        {
            var authorization = await _authorization.AuthorizeAsync(
                command.ActorId,
                PermissionKeys.WarrantyClaimCreate,
                ct);
            if (!authorization.IsSuccess)
            {
                return Result<Guid>.Failure(
                    authorization.Error!.Code,
                    authorization.Error.Message);
            }

            if (_operationLock is not null)
            {
                await _operationLock.AcquireAsync(command.ClientOperationId, ct);
            }

            var existingByOperation = await _warranty.GetClaimByClientOperationIdAsync(command.ClientOperationId, ct);
            if (existingByOperation is not null)
            {
                if (existingByOperation.CustomerId != command.CustomerId ||
                    existingByOperation.OriginalSaleId != command.OriginalSaleId)
                {
                    return Result<Guid>.Failure(
                        "payload_mismatch",
                        "Operation was previously submitted with a different Warranty claim payload.");
                }

                if (_outcomeLedger is not null)
                {
                    await _outcomeLedger.RecordSuccessAsync(
                        command.ClientOperationId,
                        "WarrantyClaim",
                        existingByOperation.Id,
                        existingByOperation.ClaimNumber,
                        actorId: command.ActorId,
                        cancellationToken: ct);
                }

                return Result<Guid>.Success(existingByOperation.Id);
            }

            var customer = await _parties.GetCustomerAsync(command.CustomerId, ct);
            if (customer is null)
            {
                return Result<Guid>.Failure(
                    "warranty.customer_not_found",
                    "Warranty customer was not found.");
            }

            var sale = await _sales.GetSaleForUpdateAsync(command.OriginalSaleId.Value, ct);
            if (sale is null || sale.Status != EdgeRetails.Domain.Sales.SaleStatus.Completed)
            {
                return Result<Guid>.Failure(
                    "warranty.sale_not_eligible",
                    "Original Sale was not found or is not completed.");
            }

            if (sale.CustomerId is Guid saleCustomerId && saleCustomerId != command.CustomerId)
            {
                return Result<Guid>.Failure(
                    "warranty.customer_sale_mismatch",
                    "The selected customer does not match the original Sale.");
            }

            foreach (var productId in command.Items.Select(x => x.ProductId).Distinct().OrderBy(x => x))
                await _resourceLock.AcquireAsync("product", productId, ct);
            foreach (var saleItemId in command.Items
                .Select(x => x.OriginalSaleItemId!.Value)
                .OrderBy(x => x))
            {
                await _resourceLock.AcquireAsync("sale-item", saleItemId, ct);
                await _resourceLock.AcquireAsync("warranty-sale-item", saleItemId, ct);
            }

            var submittedUnitIds = command.Items
                .SelectMany(x => x.Units ?? Array.Empty<WarrantyClaimUnitInput>())
                .Where(x => x.OriginalInventoryUnitId is not null)
                .Select(x => x.OriginalInventoryUnitId!.Value)
                .ToArray();

            if (submittedUnitIds.Length != submittedUnitIds.Distinct().Count())
            {
                return Result<Guid>.Failure(
                    "warranty.duplicate_unit",
                    "The same physical unit cannot appear twice in one warranty claim.");
            }

            foreach (var unitId in submittedUnitIds.OrderBy(x => x))
            {
                await _resourceLock.AcquireAsync("inventory-unit", unitId, ct);
                await _resourceLock.AcquireAsync("warranty-unit", unitId, ct);
            }

            var now = _clock.UtcNow;
            var claim = new WarrantyClaim
            {
                ClaimNumber = await _numbers.NextAsync("WC", ct),
                CustomerId = command.CustomerId,
                OriginalSaleId = sale.Id,
                ClientOperationId = command.ClientOperationId,
                Status = WarrantyClaimStatus.Received,
                CurrentCustody = WarrantyCustody.WithShop,
                ReceivedAt = now,
                CreatedBy = command.ActorId,
                CreatedAt = now
            };

            Guid? resolvedSupplierId = null;

            foreach (var input in command.Items)
            {
                if (input.Quantity <= 0 || string.IsNullOrWhiteSpace(input.FaultDescription))
                {
                    return Result<Guid>.Failure(
                        "warranty.invalid_line",
                        "Warranty quantity must be positive and fault description is required.");
                }

                var saleItem = await _sales.GetSaleItemForUpdateAsync(
                    input.OriginalSaleItemId!.Value,
                    ct);
                if (saleItem is null || saleItem.SaleId != sale.Id || saleItem.ProductId != input.ProductId)
                {
                    return Result<Guid>.Failure(
                        "warranty.sale_item_mismatch",
                        "Original SaleItem does not belong to the Sale/product being claimed.");
                }

                var product = await _catalog.GetProductAsync(input.ProductId, ct);
                if (product is null)
                {
                    return Result<Guid>.Failure(
                        "catalog.product_not_found",
                        "Warranty product was not found.");
                }

                if (saleItem.WarrantyValidUntil is null)
                {
                    return Result<Guid>.Failure(
                        "warranty.not_covered",
                        $"Product '{product.Name}' has no warranty snapshot on the original Sale.");
                }

                if (_clock.ShopDate > saleItem.WarrantyValidUntil.Value)
                {
                    return Result<Guid>.Failure(
                        "warranty.expired",
                        $"Warranty for '{product.Name}' expired on {saleItem.WarrantyValidUntil:yyyy-MM-dd}.");
                }

                decimal quantity;
                Guid lineSupplierId;
                IReadOnlyList<OriginalLotReturnAllocation> sourceAllocations = Array.Empty<OriginalLotReturnAllocation>();

                if (product.TrackingMode is TrackingMode.Serialized or TrackingMode.IndividualPiece or TrackingMode.Container)
                {
                    if (!QuantityMath.IsWhole(input.Quantity))
                    {
                        return Result<Guid>.Failure(
                            "warranty.serialized_quantity_whole",
                            "Serialized warranty quantity must be an exact whole number.");
                    }

                    quantity = input.Quantity;
                    if (input.Units is null || input.Units.Count == 0 ||
                        (product.TrackingMode != TrackingMode.Container && input.Units.Count != quantity) ||
                        input.Units.Any(x => x.OriginalInventoryUnitId is null))
                    {
                        return Result<Guid>.Failure(
                            "warranty.serialized_identity_count",
                            "Capture one exact original InventoryUnit for every serialized warranty item.");
                    }

                    var ids = input.Units.Select(x => x.OriginalInventoryUnitId!.Value).ToArray();
                    var soldLinks = await _sales.GetSaleItemUnitsAsync(saleItem.Id, ct);
                    if (product.TrackingMode == TrackingMode.Container &&
                        (soldLinks.Count == 0 || quantity != saleItem.BaseQuantity / soldLinks.Count * ids.Length))
                        return Result<Guid>.Failure("warranty.container_quantity_mismatch",
                            "Claim quantity must match the original sold pack quantity.");
                    var soldLinkByUnit = soldLinks.ToDictionary(x => x.InventoryUnitId);
                    var returnedIds = await _sales.GetReturnedInventoryUnitIdsAsync(saleItem.Id, ct);
                    var units = await _inventory.GetInventoryUnitsForUpdateAsync(product.Id, ids, ct);

                    if (units.Count != ids.Length)
                    {
                        return Result<Guid>.Failure(
                            "warranty.unit_not_found",
                            "One or more original physical units were not found.");
                    }

                    Guid? exactSupplier = null;
                    foreach (var unit in units.OrderBy(x => x.Id))
                    {
                        if (!soldLinkByUnit.TryGetValue(unit.Id, out var saleUnit))
                        {
                            return Result<Guid>.Failure(
                                "warranty.unit_not_sold_on_item",
                                "A claimed physical unit was not sold on the referenced SaleItem.");
                        }

                        if (returnedIds.Contains(unit.Id) || unit.Status != InventoryUnitStatus.Sold)
                        {
                            return Result<Guid>.Failure(
                                "warranty.unit_no_longer_customer_owned",
                                "A claimed physical unit has already returned to shop control or is no longer customer-held.");
                        }

                        var expiry = saleUnit.WarrantyValidUntil ?? saleItem.WarrantyValidUntil;
                        if (expiry is null || _clock.ShopDate > expiry.Value)
                        {
                            return Result<Guid>.Failure(
                                "warranty.expired",
                                "A claimed physical unit is outside its authoritative warranty period.");
                        }

                        if (await _warranty.HasActiveClaimForUnitAsync(unit.Id, ct))
                        {
                            return Result<Guid>.Failure(
                                "warranty.active_claim_exists",
                                "A claimed physical unit already has an active warranty claim.");
                        }

                        if (await _warranty.IsUnitTerminallyResolvedAsync(unit.Id, ct))
                        {
                            return Result<Guid>.Failure(
                                "warranty.unit_terminally_resolved",
                                "A claimed physical unit was already terminally replaced/refunded.");
                        }

                        if (unit.SupplierProductId is null)
                        {
                            return Result<Guid>.Failure(
                                "warranty.supplier_provenance_missing",
                                "Original Supplier provenance is missing for a tracked unit.");
                        }

                        var supplierProduct = await _traceability.GetSupplierProductByIdForUpdateAsync(
                            unit.SupplierProductId.Value,
                            ct);
                        if (supplierProduct is null)
                        {
                            return Result<Guid>.Failure(
                                "warranty.supplier_provenance_missing",
                                "Original Supplier provenance could not be resolved.");
                        }

                        if (unit.SourcePurchaseItemId is Guid sourcePurchaseItemId)
                        {
                            var sourceItem = await _purchases.GetPurchaseItemForUpdateAsync(
                                sourcePurchaseItemId,
                                ct);
                            if (sourceItem is null)
                            {
                                return Result<Guid>.Failure(
                                    "warranty.supplier_provenance_missing",
                                    "Original Supplier provenance could not be resolved.");
                            }

                            var sourcePurchase = await _purchases.GetPurchaseForUpdateAsync(sourceItem.PurchaseId, ct);
                            if (sourcePurchase is null || sourcePurchase.SupplierId != supplierProduct.SupplierId)
                            {
                                return Result<Guid>.Failure(
                                    "warranty.supplier_provenance_mismatch",
                                    "Tracked-unit Supplier provenance is inconsistent.");
                            }
                        }

                        if (exactSupplier is null)
                        {
                            exactSupplier = supplierProduct.SupplierId;
                        }
                        else if (exactSupplier.Value != supplierProduct.SupplierId)
                        {
                            return Result<Guid>.Failure(
                                "warranty.mixed_supplier_claim",
                                "Physical units from different Suppliers must be split into separate warranty claims.");
                        }
                    }

                    lineSupplierId = exactSupplier!.Value;
                }
                else
                {
                    quantity = QuantityMath.RoundQuantity(input.Quantity);
                    var returned = await _sales.GetReturnedBaseQuantityAsync(saleItem.Id, ct);
                    var active = await _warranty.GetActiveClaimedQuantityAsync(saleItem.Id, ct);
                    var terminal = await _warranty.GetTerminallyRemovedQuantityAsync(saleItem.Id, ct);
                    var eligible = QuantityMath.RoundQuantity(
                        saleItem.BaseQuantity - returned - active - terminal);

                    if (quantity > eligible)
                    {
                        return Result<Guid>.Failure(
                            "warranty.quantity_exceeds_eligible",
                            $"Requested warranty quantity exceeds eligible remaining quantity ({eligible}).");
                    }

                    IReadOnlyList<SoldSourceCapacity> sourcePositions;
                    try { sourcePositions = await _sales.GetSoldSourceCapacityForUpdateAsync(saleItem.Id, ct); }
                    catch (BusinessRuleException ex) { return Result<Guid>.Failure(ex.Code, ex.Message); }
                    var supplierCapacities = sourcePositions.Where(x => x.SupplierId.HasValue && x.RemainingQuantity > 0m)
                        .GroupBy(x => x.SupplierId!.Value).ToDictionary(x => x.Key, x => x.Sum(y => y.RemainingQuantity));
                    if (supplierCapacities.Count == 0)
                    {
                        return Result<Guid>.Failure(
                            "warranty.supplier_provenance_missing",
                            "Supplier provenance could not be resolved for the SaleItem.");
                    }

                    if (command.SupplierId is Guid requestedSupplier)
                    {
                        if (!supplierCapacities.TryGetValue(requestedSupplier, out var capacity) || quantity > capacity)
                        {
                            return Result<Guid>.Failure(
                                "warranty.supplier_quantity_mismatch",
                                "Requested quantity cannot be attributed to the selected Supplier.");
                        }

                        lineSupplierId = requestedSupplier;
                    }
                    else if (supplierCapacities.Count == 1)
                    {
                        lineSupplierId = supplierCapacities.Keys.Single();
                    }
                    else
                    {
                        return Result<Guid>.Failure(
                            "warranty.mixed_supplier_claim",
                            "This SaleItem contains stock from multiple Suppliers. Create a separate claim per Supplier.");
                    }
                }

                if (product.TrackingMode is not (TrackingMode.Serialized or TrackingMode.IndividualPiece or TrackingMode.Container))
                {
                    try { sourceAllocations = SoldSourceAllocationAuthority.Select(await _sales.GetSoldSourceCapacityForUpdateAsync(saleItem.Id, ct), quantity, lineSupplierId); }
                    catch (BusinessRuleException ex) { return Result<Guid>.Failure(ex.Code, ex.Message); }
                }

                if (command.SupplierId is Guid explicitSupplier && explicitSupplier != lineSupplierId)
                {
                    return Result<Guid>.Failure(
                        "warranty.wrong_supplier",
                        "Selected Supplier does not match original inventory provenance.");
                }

                if (resolvedSupplierId is null)
                {
                    resolvedSupplierId = lineSupplierId;
                }
                else if (resolvedSupplierId.Value != lineSupplierId)
                {
                    return Result<Guid>.Failure(
                        "warranty.mixed_supplier_claim",
                        "One warranty claim cannot contain items from different Suppliers.");
                }

                var item = new WarrantyClaimItem
                {
                    ClaimId = claim.Id,
                    OriginalSaleItemId = saleItem.Id,
                    ProductId = product.Id,
                    Quantity = quantity,
                    FaultDescription = input.FaultDescription.Trim(),
                    WarrantyValidUntil = saleItem.WarrantyValidUntil
                };
                _warranty.AddClaimItem(item);
                foreach (var allocation in sourceAllocations)
                    _sales.AddClaimSourceAllocation(new WarrantyClaimSourceAllocation
                    {
                        ClaimItemId = item.Id, SaleConsumptionId = allocation.SaleConsumptionId,
                        BaseQuantity = allocation.Quantity, ActorId = command.ActorId,
                        ClientOperationId = command.ClientOperationId, OccurredAt = now
                    });

                if (product.TrackingMode is TrackingMode.Serialized or TrackingMode.IndividualPiece or TrackingMode.Container)
                {
                    var ids = input.Units!.Select(x => x.OriginalInventoryUnitId!.Value).ToArray();
                    var units = await _inventory.GetInventoryUnitsForUpdateAsync(product.Id, ids, ct);
                    foreach (var unit in units.OrderBy(x => x.Id))
                    {
                        _warranty.AddClaimItemUnit(new WarrantyClaimItemUnit
                        {
                            ClaimItemId = item.Id,
                            OriginalInventoryUnitId = unit.Id,
                            ActiveOriginalInventoryUnitId = unit.Id,
                            OriginalIdentitySnapshot = BuildIdentitySnapshot(unit)
                        });
                    }
                }
            }

            claim.SupplierId = resolvedSupplierId;
            _warranty.AddClaim(claim);
            _warranty.AddClaimEvent(new WarrantyClaimEvent
            {
                ClaimId = claim.Id,
                Status = claim.Status,
                Custody = claim.CurrentCustody,
                EventType = "CLAIM_RECEIVED",
                ActorId = command.ActorId,
                OccurredAt = now
            });

            if (_outcomeLedger is not null)
            {
                await _outcomeLedger.RecordSuccessAsync(
                    command.ClientOperationId,
                    "WarrantyClaim",
                    claim.Id,
                    claim.ClaimNumber,
                    actorId: command.ActorId,
                    cancellationToken: ct);
            }

            await _unitOfWork.SaveChangesAsync(ct);
            return Result<Guid>.Success(claim.Id);
        }, cancellationToken);

        if (!result.IsSuccess && _outcomeLedger is not null && command.ClientOperationId != Guid.Empty)
        {
            await _outcomeLedger.RecordFailureAsync(
                command.ClientOperationId,
                "WarrantyClaim",
                result.Error?.Code ?? "warranty.claim_failed",
                result.Error?.Message ?? "Warranty claim creation failed.",
                actorId: command.ActorId,
                cancellationToken: cancellationToken);
        }

        return result;
    }

    private static string BuildIdentitySnapshot(InventoryUnit unit)
    {
        var parts = new[]
        {
            unit.TrackingCode,
            unit.SerialNumber,
            unit.Imei1,
            unit.Imei2
        }.Where(x => !string.IsNullOrWhiteSpace(x));

        return string.Join(" | ", parts);
    }
}

public sealed record BeginWarrantyClaimReviewCommand(Guid ClaimId, Guid ActorId, Guid ClientOperationId, string? Note = null);

public sealed class BeginWarrantyClaimReviewHandler
{
    private readonly IWarrantyRepository _warranty;
    private readonly IOperationLock? _operationLock;
    private readonly IResourceLock _resourceLock;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly IClock _clock;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;

    public BeginWarrantyClaimReviewHandler(
        IWarrantyRepository warranty,
        IResourceLock resourceLock,
        IApplicationPermissionAuthorizer authorization,
        IClock clock,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork,
        IOperationLock? operationLock = null)
    {
        _warranty = warranty;
        _operationLock = operationLock;
        _resourceLock = resourceLock;
        _authorization = authorization;
        _clock = clock;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
    }

    public Task<Result> HandleAsync(BeginWarrantyClaimReviewCommand command, CancellationToken cancellationToken) =>
        _transactions.ExecuteAsync(async ct =>
        {
            var authorization = await _authorization.AuthorizeAsync(
                command.ActorId,
                PermissionKeys.WarrantyClaimUpdate,
                ct);
            if (!authorization.IsSuccess)
            {
                return Result.Failure(authorization.Error!.Code, authorization.Error.Message);
            }

            if (command.ClientOperationId == Guid.Empty)
            {
                return Result.Failure("validation.client_operation_id_required", "ClientOperationId is required for Warranty mutations.");
            }
            if (_operationLock is not null)
            {
                await _operationLock.AcquireAsync(command.ClientOperationId, ct);
            }
            var payloadHash = WarrantyOperationIdentity.Hash("CUSTOMER_CLAIM", command.ClaimId.ToString("D"), "REVIEW", WarrantyOperationIdentity.Normalize(command.Note));
            var existingOperation = await _warranty.GetOperationByClientOperationIdAsync(command.ClientOperationId, ct);
            if (existingOperation is not null)
            {
                if (existingOperation.OperationType != WarrantyOperationType.BeginReview || existingOperation.TargetType != "CUSTOMER_CLAIM" || existingOperation.TargetId != command.ClaimId || existingOperation.PayloadHash != payloadHash)
                {
                    return Result.Failure("payload_mismatch", "Operation was previously submitted with a different Warranty payload.");
                }
                return Result.Success();
            }

            await _resourceLock.AcquireAsync("warranty-claim", command.ClaimId, ct);
            var claim = await _warranty.GetClaimForUpdateAsync(command.ClaimId, ct);
            if (claim is null)
            {
                return Result.Failure("warranty.claim_not_found", "Warranty claim was not found.");
            }

            try
            {
                claim.BeginReview();
            }
            catch (BusinessRuleException ex)
            {
                return Result.Failure(ex.Code, ex.Message);
            }

            var now = _clock.UtcNow;
            _warranty.AddClaimEvent(new WarrantyClaimEvent
            {
                ClaimId = claim.Id,
                Status = claim.Status,
                Custody = claim.CurrentCustody,
                EventType = "UNDER_REVIEW",
                Note = command.Note?.Trim(),
                ActorId = command.ActorId,
                OccurredAt = now
            });
            _warranty.AddOperation(new WarrantyOperation
            {
                ClientOperationId = command.ClientOperationId,
                TargetType = "CUSTOMER_CLAIM",
                TargetId = claim.Id,
                OperationType = WarrantyOperationType.BeginReview,
                ActorId = command.ActorId,
                PayloadHash = payloadHash,
                ResultId = claim.Id,
                OccurredAt = now
            });
            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }, cancellationToken);
}

public sealed record MarkWarrantySupplierProcessingCommand(Guid ClaimId, Guid ActorId, Guid ClientOperationId, string? Note = null);

public sealed class MarkWarrantySupplierProcessingHandler
{
    private readonly IWarrantyRepository _warranty;
    private readonly IOperationLock? _operationLock;
    private readonly IResourceLock _resourceLock;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly IClock _clock;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;

    public MarkWarrantySupplierProcessingHandler(
        IWarrantyRepository warranty,
        IResourceLock resourceLock,
        IApplicationPermissionAuthorizer authorization,
        IClock clock,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork,
        IOperationLock? operationLock = null)
    {
        _warranty = warranty;
        _operationLock = operationLock;
        _resourceLock = resourceLock;
        _authorization = authorization;
        _clock = clock;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
    }

    public Task<Result> HandleAsync(MarkWarrantySupplierProcessingCommand command, CancellationToken cancellationToken) =>
        _transactions.ExecuteAsync(async ct =>
        {
            var authorization = await _authorization.AuthorizeAsync(
                command.ActorId,
                PermissionKeys.WarrantyClaimUpdate,
                ct);
            if (!authorization.IsSuccess)
            {
                return Result.Failure(authorization.Error!.Code, authorization.Error.Message);
            }

            if (command.ClientOperationId == Guid.Empty)
            {
                return Result.Failure("validation.client_operation_id_required", "ClientOperationId is required for Warranty mutations.");
            }
            if (_operationLock is not null)
            {
                await _operationLock.AcquireAsync(command.ClientOperationId, ct);
            }
            var payloadHash = WarrantyOperationIdentity.Hash("CUSTOMER_CLAIM", command.ClaimId.ToString("D"), "SUPPLIER_PROCESSING", WarrantyOperationIdentity.Normalize(command.Note));
            var existingOperation = await _warranty.GetOperationByClientOperationIdAsync(command.ClientOperationId, ct);
            if (existingOperation is not null)
            {
                if (existingOperation.OperationType != WarrantyOperationType.SupplierProcessing || existingOperation.TargetType != "CUSTOMER_CLAIM" || existingOperation.TargetId != command.ClaimId || existingOperation.PayloadHash != payloadHash)
                {
                    return Result.Failure("payload_mismatch", "Operation was previously submitted with a different Warranty payload.");
                }
                return Result.Success();
            }

            await _resourceLock.AcquireAsync("warranty-claim", command.ClaimId, ct);
            var claim = await _warranty.GetClaimForUpdateAsync(command.ClaimId, ct);
            if (claim is null)
            {
                return Result.Failure("warranty.claim_not_found", "Warranty claim was not found.");
            }

            try
            {
                claim.MarkSupplierProcessing();
            }
            catch (BusinessRuleException ex)
            {
                return Result.Failure(ex.Code, ex.Message);
            }

            var now = _clock.UtcNow;
            _warranty.AddClaimEvent(new WarrantyClaimEvent
            {
                ClaimId = claim.Id,
                Status = claim.Status,
                Custody = claim.CurrentCustody,
                EventType = "SUPPLIER_PROCESSING",
                Note = command.Note?.Trim(),
                ActorId = command.ActorId,
                OccurredAt = now
            });
            _warranty.AddOperation(new WarrantyOperation
            {
                ClientOperationId = command.ClientOperationId,
                TargetType = "CUSTOMER_CLAIM",
                TargetId = claim.Id,
                OperationType = WarrantyOperationType.SupplierProcessing,
                ActorId = command.ActorId,
                PayloadHash = payloadHash,
                ResultId = claim.Id,
                OccurredAt = now
            });
            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }, cancellationToken);
}

public sealed record CancelWarrantyClaimCommand(Guid ClaimId, Guid ActorId, Guid ClientOperationId, string? Note);

public sealed class CancelWarrantyClaimHandler
{
    private readonly IWarrantyRepository _warranty;
    private readonly IOperationLock? _operationLock;
    private readonly IResourceLock _resourceLock;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly IClock _clock;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;

    public CancelWarrantyClaimHandler(
        IWarrantyRepository warranty,
        IResourceLock resourceLock,
        IApplicationPermissionAuthorizer authorization,
        IClock clock,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork,
        IOperationLock? operationLock = null)
    {
        _warranty = warranty;
        _operationLock = operationLock;
        _resourceLock = resourceLock;
        _authorization = authorization;
        _clock = clock;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
    }

    public Task<Result> HandleAsync(CancelWarrantyClaimCommand command, CancellationToken cancellationToken) =>
        _transactions.ExecuteAsync(async ct =>
        {
            var authorization = await _authorization.AuthorizeAsync(
                command.ActorId,
                PermissionKeys.WarrantyClaimUpdate,
                ct);
            if (!authorization.IsSuccess)
            {
                return Result.Failure(authorization.Error!.Code, authorization.Error.Message);
            }

            if (command.ClientOperationId == Guid.Empty)
            {
                return Result.Failure("validation.client_operation_id_required", "ClientOperationId is required for Warranty mutations.");
            }
            if (_operationLock is not null)
            {
                await _operationLock.AcquireAsync(command.ClientOperationId, ct);
            }
            var payloadHash = WarrantyOperationIdentity.Hash("CUSTOMER_CLAIM", command.ClaimId.ToString("D"), "CANCELLATION", WarrantyOperationIdentity.Normalize(command.Note));
            var existingOperation = await _warranty.GetOperationByClientOperationIdAsync(command.ClientOperationId, ct);
            if (existingOperation is not null)
            {
                if (existingOperation.OperationType != WarrantyOperationType.Cancellation || existingOperation.TargetType != "CUSTOMER_CLAIM" || existingOperation.TargetId != command.ClaimId || existingOperation.PayloadHash != payloadHash)
                {
                    return Result.Failure("payload_mismatch", "Operation was previously submitted with a different Warranty payload.");
                }
                return Result.Success();
            }

            await _resourceLock.AcquireAsync("warranty-claim", command.ClaimId, ct);
            var claim = await _warranty.GetClaimForUpdateAsync(command.ClaimId, ct);
            if (claim is null)
            {
                return Result.Failure("warranty.claim_not_found", "Warranty claim was not found.");
            }

            try
            {
                claim.Cancel();
            }
            catch (BusinessRuleException ex)
            {
                return Result.Failure(ex.Code, ex.Message);
            }

            var units = await _warranty.GetClaimUnitsAsync(claim.Id, ct);
            foreach (var unit in units)
            {
                unit.ActiveOriginalInventoryUnitId = null;
            }

            var now = _clock.UtcNow;
            _warranty.AddClaimEvent(new WarrantyClaimEvent
            {
                ClaimId = claim.Id,
                Status = claim.Status,
                Custody = claim.CurrentCustody,
                EventType = "CLAIM_CANCELLED",
                Note = command.Note?.Trim(),
                ActorId = command.ActorId,
                OccurredAt = now
            });
            _warranty.AddOperation(new WarrantyOperation
            {
                ClientOperationId = command.ClientOperationId,
                TargetType = "CUSTOMER_CLAIM",
                TargetId = claim.Id,
                OperationType = WarrantyOperationType.Cancellation,
                ActorId = command.ActorId,
                PayloadHash = payloadHash,
                ResultId = claim.Id,
                OccurredAt = now
            });
            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }, cancellationToken);
}

public sealed record SendWarrantyClaimToSupplierCommand(
    Guid ClaimId,
    Guid ActorId,
    Guid ClientOperationId,
    string? Note = null);

public sealed class SendWarrantyClaimToSupplierHandler
{
    private readonly IWarrantyRepository _warranty;
    private readonly IOperationLock? _operationLock;
    private readonly IResourceLock _resourceLock;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly IClock _clock;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;

    public SendWarrantyClaimToSupplierHandler(
        IWarrantyRepository warranty,
        IResourceLock resourceLock,
        IApplicationPermissionAuthorizer authorization,
        IClock clock,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork,
        IOperationLock? operationLock = null)
    {
        _warranty = warranty;
        _operationLock = operationLock;
        _resourceLock = resourceLock;
        _authorization = authorization;
        _clock = clock;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
    }

    public Task<Result> HandleAsync(
        SendWarrantyClaimToSupplierCommand command,
        CancellationToken cancellationToken) =>
        _transactions.ExecuteAsync(async ct =>
        {
            var authorization = await _authorization.AuthorizeAsync(
                command.ActorId,
                PermissionKeys.WarrantyClaimSendSupplier,
                ct);
            if (!authorization.IsSuccess)
            {
                return Result.Failure(authorization.Error!.Code, authorization.Error.Message);
            }

            if (command.ClientOperationId == Guid.Empty)
            {
                return Result.Failure("validation.client_operation_id_required", "ClientOperationId is required for Warranty mutations.");
            }
            if (_operationLock is not null)
            {
                await _operationLock.AcquireAsync(command.ClientOperationId, ct);
            }

            await _resourceLock.AcquireAsync("warranty-claim", command.ClaimId, ct);
            var claim = await _warranty.GetClaimForUpdateAsync(command.ClaimId, ct);
            if (claim is null)
            {
                return Result.Failure("warranty.claim_not_found", "Warranty claim was not found.");
            }

            if (claim.SupplierId is null)
            {
                return Result.Failure(
                    "warranty.supplier_required",
                    "Supplier provenance must be resolved before sending the claim.");
            }

            var payloadHash = WarrantyOperationIdentity.Hash("CUSTOMER_CLAIM", command.ClaimId.ToString("D"), "SEND_TO_SUPPLIER", claim.SupplierId.Value.ToString("D"), WarrantyOperationIdentity.Normalize(command.Note));
            var existingOperation = await _warranty.GetOperationByClientOperationIdAsync(command.ClientOperationId, ct);
            if (existingOperation is not null)
            {
                if (existingOperation.OperationType != WarrantyOperationType.SendToSupplier || existingOperation.TargetType != "CUSTOMER_CLAIM" || existingOperation.TargetId != claim.Id || existingOperation.PayloadHash != payloadHash)
                {
                    return Result.Failure("payload_mismatch", "Operation was previously submitted with a different Warranty payload.");
                }
                return Result.Success();
            }

            try
            {
                claim.SendToSupplier(_clock.UtcNow);
            }
            catch (BusinessRuleException ex)
            {
                return Result.Failure(ex.Code, ex.Message);
            }

            var now = _clock.UtcNow;
            _warranty.AddClaimEvent(new WarrantyClaimEvent
            {
                ClaimId = claim.Id,
                Status = claim.Status,
                Custody = claim.CurrentCustody,
                EventType = "SENT_TO_SUPPLIER",
                Note = command.Note?.Trim(),
                ActorId = command.ActorId,
                OccurredAt = now
            });
            _warranty.AddOperation(new WarrantyOperation
            {
                ClientOperationId = command.ClientOperationId,
                TargetType = "CUSTOMER_CLAIM",
                TargetId = claim.Id,
                OperationType = WarrantyOperationType.SendToSupplier,
                ActorId = command.ActorId,
                PayloadHash = payloadHash,
                ResultId = claim.Id,
                OccurredAt = now
            });

            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }, cancellationToken);
}

public sealed record RecordWarrantyResolutionCommand(
    Guid ClaimId,
    WarrantyResolutionType Resolution,
    Guid ActorId,
    Guid ClientOperationId,
    string? ResolutionNote);

public sealed class RecordWarrantyResolutionHandler
{
    private readonly IWarrantyRepository _warranty;
    private readonly IOperationLock? _operationLock;
    private readonly IResourceLock _resourceLock;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly IClock _clock;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;

    public RecordWarrantyResolutionHandler(
        IWarrantyRepository warranty,
        IResourceLock resourceLock,
        IApplicationPermissionAuthorizer authorization,
        IClock clock,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork,
        IOperationLock? operationLock = null)
    {
        _warranty = warranty;
        _operationLock = operationLock;
        _resourceLock = resourceLock;
        _authorization = authorization;
        _clock = clock;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
    }

    public Task<Result> HandleAsync(
        RecordWarrantyResolutionCommand command,
        CancellationToken cancellationToken) =>
        _transactions.ExecuteAsync(async ct =>
        {
            var authorization = await _authorization.AuthorizeAsync(
                command.ActorId,
                PermissionKeys.WarrantyClaimResolve,
                ct);
            if (!authorization.IsSuccess)
            {
                return Result.Failure(authorization.Error!.Code, authorization.Error.Message);
            }

            if (command.Resolution is WarrantyResolutionType.Credited or
                WarrantyResolutionType.Scrapped or
                WarrantyResolutionType.Other)
            {
                return Result.Failure(
                    "warranty.customer_resolution_invalid",
                    "Customer warranty resolution supports Repaired, Replaced, Rejected, or Refunded outcomes.");
            }

            if (command.Resolution == WarrantyResolutionType.Replaced)
            {
                return Result.Failure(
                    "warranty.replacement_command_required",
                    "Use the replacement-receipt command so each replacement receives a new TrackingCode.");
            }

            if (command.ClientOperationId == Guid.Empty)
            {
                return Result.Failure("validation.client_operation_id_required", "ClientOperationId is required for Warranty mutations.");
            }
            if (_operationLock is not null)
            {
                await _operationLock.AcquireAsync(command.ClientOperationId, ct);
            }
            var payloadHash = WarrantyOperationIdentity.Hash("CUSTOMER_CLAIM", command.ClaimId.ToString("D"), "RESOLUTION", command.Resolution.ToString(), WarrantyOperationIdentity.Normalize(command.ResolutionNote));
            var existingOperation = await _warranty.GetOperationByClientOperationIdAsync(command.ClientOperationId, ct);
            if (existingOperation is not null)
            {
                if (existingOperation.OperationType != WarrantyOperationType.Resolution || existingOperation.TargetType != "CUSTOMER_CLAIM" || existingOperation.TargetId != command.ClaimId || existingOperation.PayloadHash != payloadHash)
                {
                    return Result.Failure("payload_mismatch", "Operation was previously submitted with a different Warranty payload.");
                }
                return Result.Success();
            }

            await _resourceLock.AcquireAsync("warranty-claim", command.ClaimId, ct);
            var claim = await _warranty.GetClaimForUpdateAsync(command.ClaimId, ct);
            if (claim is null)
            {
                return Result.Failure("warranty.claim_not_found", "Warranty claim was not found.");
            }

            var items = await _warranty.GetClaimItemsAsync(claim.Id, ct);
            if (items.Count == 0)
            {
                return Result.Failure(
                    "warranty.claim_items_missing",
                    "Warranty claim contains no items.");
            }

            foreach (var item in items)
            {
                item.ResolutionType = command.Resolution;
                item.ResolutionNote = command.ResolutionNote?.Trim();
            }

            try
            {
                claim.MarkReadyForCustomer(_clock.UtcNow);
            }
            catch (BusinessRuleException ex)
            {
                return Result.Failure(ex.Code, ex.Message);
            }

            var now = _clock.UtcNow;
            _warranty.AddClaimEvent(new WarrantyClaimEvent
            {
                ClaimId = claim.Id,
                Status = claim.Status,
                Custody = claim.CurrentCustody,
                EventType = $"RESOLVED_{command.Resolution.ToString().ToUpperInvariant()}",
                Note = command.ResolutionNote?.Trim(),
                ActorId = command.ActorId,
                OccurredAt = now
            });
            _warranty.AddOperation(new WarrantyOperation
            {
                ClientOperationId = command.ClientOperationId,
                TargetType = "CUSTOMER_CLAIM",
                TargetId = claim.Id,
                OperationType = WarrantyOperationType.Resolution,
                ActorId = command.ActorId,
                PayloadHash = payloadHash,
                ResultId = claim.Id,
                OccurredAt = now
            });

            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }, cancellationToken);
}

public sealed record HandoverWarrantyItemCommand(
    Guid ClaimId,
    Guid ActorId,
    Guid ClientOperationId,
    string? Note = null);

public sealed class HandoverWarrantyItemHandler
{
    private readonly IWarrantyRepository _warranty;
    private readonly IOperationLock? _operationLock;
    private readonly IInventoryRepository _inventory;
    private readonly IResourceLock _resourceLock;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly IClock _clock;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;

    public HandoverWarrantyItemHandler(
        IWarrantyRepository warranty,
        IInventoryRepository inventory,
        IResourceLock resourceLock,
        IApplicationPermissionAuthorizer authorization,
        IClock clock,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork,
        IOperationLock? operationLock = null)
    {
        _warranty = warranty;
        _operationLock = operationLock;
        _inventory = inventory;
        _resourceLock = resourceLock;
        _authorization = authorization;
        _clock = clock;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
    }

    public Task<Result> HandleAsync(
        HandoverWarrantyItemCommand command,
        CancellationToken cancellationToken) =>
        _transactions.ExecuteAsync(async ct =>
        {
            var authorization = await _authorization.AuthorizeAsync(
                command.ActorId,
                PermissionKeys.WarrantyClaimHandover,
                ct);
            if (!authorization.IsSuccess)
            {
                return Result.Failure(authorization.Error!.Code, authorization.Error.Message);
            }

            if (command.ClientOperationId == Guid.Empty)
            {
                return Result.Failure("validation.client_operation_id_required", "ClientOperationId is required for Warranty mutations.");
            }
            if (_operationLock is not null)
            {
                await _operationLock.AcquireAsync(command.ClientOperationId, ct);
            }
            var payloadHash = WarrantyOperationIdentity.Hash("CUSTOMER_CLAIM", command.ClaimId.ToString("D"), "HANDOVER", WarrantyOperationIdentity.Normalize(command.Note));
            var existingOperation = await _warranty.GetOperationByClientOperationIdAsync(command.ClientOperationId, ct);
            if (existingOperation is not null)
            {
                if (existingOperation.OperationType != WarrantyOperationType.Handover || existingOperation.TargetType != "CUSTOMER_CLAIM" || existingOperation.TargetId != command.ClaimId || existingOperation.PayloadHash != payloadHash)
                {
                    return Result.Failure("payload_mismatch", "Operation was previously submitted with a different Warranty payload.");
                }
                return Result.Success();
            }

            await _resourceLock.AcquireAsync("warranty-claim", command.ClaimId, ct);
            var claim = await _warranty.GetClaimForUpdateAsync(command.ClaimId, ct);
            if (claim is null)
            {
                return Result.Failure("warranty.claim_not_found", "Warranty claim was not found.");
            }

            if (claim.Status != WarrantyClaimStatus.ReadyForCustomer)
            {
                return Result.Failure(
                    "warranty.not_ready",
                    "Warranty claim must be ready for customer before handover.");
            }

            var items = await _warranty.GetClaimItemsAsync(claim.Id, ct);
            var itemById = items.ToDictionary(x => x.Id);
            var links = await _warranty.GetClaimUnitsAsync(claim.Id, ct);

            var replacementIds = links
                .Where(x => x.ReplacementInventoryUnitId is not null)
                .Select(x => x.ReplacementInventoryUnitId!.Value)
                .Distinct()
                .OrderBy(x => x)
                .ToArray();

            foreach (var replacementId in replacementIds)
            {
                await _resourceLock.AcquireAsync("warranty-unit", replacementId, ct);
            }

            foreach (var productGroup in links
                .Where(x => x.ReplacementInventoryUnitId is not null)
                .GroupBy(x => itemById[x.ClaimItemId].ProductId))
            {
                var ids = productGroup
                    .Select(x => x.ReplacementInventoryUnitId!.Value)
                    .Distinct()
                    .ToArray();

                var units = await _inventory.GetInventoryUnitsForUpdateAsync(
                    productGroup.Key,
                    ids,
                    ct);

                if (units.Count != ids.Length ||
                    units.Any(x => x.Status != InventoryUnitStatus.WarrantyCustomerHeld))
                {
                    return Result.Failure(
                        "warranty.replacement_handover_state",
                        "One or more replacement units are not in customer-held warranty state.");
                }

                foreach (var unit in units)
                {
                    unit.Status = InventoryUnitStatus.WarrantyCustomerHandedOver;
                    unit.Version++;
                }
            }

            foreach (var link in links)
            {
                link.ActiveOriginalInventoryUnitId = null;
            }

            try
            {
                claim.Handover(_clock.UtcNow);
            }
            catch (BusinessRuleException ex)
            {
                return Result.Failure(ex.Code, ex.Message);
            }

            var now = _clock.UtcNow;
            _warranty.AddClaimEvent(new WarrantyClaimEvent
            {
                ClaimId = claim.Id,
                Status = claim.Status,
                Custody = claim.CurrentCustody,
                EventType = "HANDED_TO_CUSTOMER",
                Note = command.Note?.Trim(),
                ActorId = command.ActorId,
                OccurredAt = now
            });
            _warranty.AddOperation(new WarrantyOperation
            {
                ClientOperationId = command.ClientOperationId,
                TargetType = "CUSTOMER_CLAIM",
                TargetId = claim.Id,
                OperationType = WarrantyOperationType.Handover,
                ActorId = command.ActorId,
                PayloadHash = payloadHash,
                ResultId = claim.Id,
                OccurredAt = now
            });

            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }, cancellationToken);
}

public sealed record CustomerWarrantyReplacementUnitInput(
    Guid ClaimItemUnitId,
    string? SerialNumber,
    string? Imei1,
    string? Imei2);

public sealed record ReceiveCustomerWarrantyReplacementCommand(
    Guid ClaimId,
    Guid ActorId,
    Guid ClientOperationId,
    IReadOnlyList<CustomerWarrantyReplacementUnitInput> Units,
    string? Note);

public sealed class ReceiveCustomerWarrantyReplacementHandler
{
    private readonly IWarrantyRepository _warranty;
    private readonly ICatalogRepository _catalog;
    private readonly IInventoryRepository _inventory;
    private readonly ITraceabilityRepository _traceability;
    private readonly IPartyRepository _parties;
    private readonly IOperationLock _operationLock;
    private readonly IResourceLock _resourceLock;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly IClock _clock;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IPhysicalUnitCreationAuthority? _physicalUnits;

    public ReceiveCustomerWarrantyReplacementHandler(
        IWarrantyRepository warranty,
        ICatalogRepository catalog,
        IInventoryRepository inventory,
        ITraceabilityRepository traceability,
        IPartyRepository parties,
        IOperationLock operationLock,
        IResourceLock resourceLock,
        IApplicationPermissionAuthorizer authorization,
        IClock clock,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork,
        IPhysicalUnitCreationAuthority? physicalUnitCreationAuthority = null)
    {
        _warranty = warranty;
        _catalog = catalog;
        _inventory = inventory;
        _traceability = traceability;
        _parties = parties;
        _operationLock = operationLock;
        _resourceLock = resourceLock;
        _authorization = authorization;
        _clock = clock;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
        _physicalUnits = physicalUnitCreationAuthority;
    }

    public Task<Result> HandleAsync(
        ReceiveCustomerWarrantyReplacementCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ClaimId == Guid.Empty ||
            command.ClientOperationId == Guid.Empty ||
            command.Units.Count == 0 ||
            command.Units.GroupBy(x => x.ClaimItemUnitId).Any(x => x.Count() > 1))
        {
            return Task.FromResult(Result.Failure(
                "warranty.replacement_invalid",
                "Claim, client operation id, and distinct replacement unit inputs are required."));
        }

        return _transactions.ExecuteAsync(async ct =>
        {
            var authorization = await _authorization.AuthorizeAsync(
                command.ActorId,
                PermissionKeys.WarrantyClaimReplace,
                ct);
            if (!authorization.IsSuccess)
            {
                return Result.Failure(authorization.Error!.Code, authorization.Error.Message);
            }

            await _operationLock.AcquireAsync(command.ClientOperationId, ct);
            string replacementPayload;
            try
            {
                replacementPayload = System.Text.Json.JsonSerializer.Serialize(command.Units
                    .OrderBy(x => x.ClaimItemUnitId)
                    .Select(x => new { x.ClaimItemUnitId, Identity = WarrantyOperationIdentity.ManufacturerIdentity(x.SerialNumber, x.Imei1, x.Imei2) }));
            }
            catch (BusinessRuleException ex)
            {
                return Result.Failure(ex.Code, ex.Message);
            }
            var payloadHash = WarrantyOperationIdentity.Hash(
                "CUSTOMER_CLAIM",
                command.ClaimId.ToString("D"),
                "CUSTOMER_REPLACEMENT",
                replacementPayload,
                WarrantyOperationIdentity.Normalize(command.Note));
            var legacyPayloadHash = WarrantyOperationIdentity.Hash("CUSTOMER_CLAIM", command.ClaimId.ToString("D"),
                "CUSTOMER_REPLACEMENT", string.Join(";", command.Units.OrderBy(x => x.ClaimItemUnitId)
                    .Select(x => $"{x.ClaimItemUnitId:D}|{WarrantyOperationIdentity.Normalize(x.SerialNumber)}|{WarrantyOperationIdentity.Normalize(x.Imei1)}|{WarrantyOperationIdentity.Normalize(x.Imei2)}")),
                WarrantyOperationIdentity.Normalize(command.Note));
            var existingOperation = await _warranty.GetOperationForReplayAsync(command.ClientOperationId, ct);
            if (existingOperation is not null)
            {
                if (existingOperation.OperationType != WarrantyOperationType.CustomerReplacement ||
                    existingOperation.TargetType != "CUSTOMER_CLAIM" ||
                    existingOperation.TargetId != command.ClaimId)
                {
                    return Result.Failure("payload_mismatch", "Operation was previously submitted with a different Warranty payload.");
                }
                if (existingOperation.PayloadHash != payloadHash && existingOperation.PayloadHash != legacyPayloadHash)
                {
                    return Result.Failure("warranty.replay_reconciliation_required",
                        "Stored Warranty payload cannot establish replay equivalence, including a possible legacy hash. Reconcile the original operation before retrying; no replacement was allocated.");
                }
                return Result.Success();
            }

            await _resourceLock.AcquireAsync("warranty-claim", command.ClaimId, ct);

            var claim = await _warranty.GetClaimAsync(command.ClaimId, ct);
            if (claim is null)
            {
                return Result.Failure("warranty.claim_not_found", "Warranty claim was not found.");
            }

            if (claim.SupplierId is not Guid supplierId)
            {
                return Result.Failure(
                    "warranty.supplier_required",
                    "Supplier provenance is required before receiving a replacement.");
            }

            if (claim.Status is not WarrantyClaimStatus.SentToSupplier and
                not WarrantyClaimStatus.SupplierProcessing)
            {
                return Result.Failure(
                    "warranty.replacement_wrong_state",
                    "Customer replacement can be received only after the claim was sent to the Supplier.");
            }

            var supplier = await _parties.GetSupplierAsync(supplierId, ct);
            if (supplier is null || !supplier.IsActive || string.IsNullOrWhiteSpace(supplier.DealerCode))
            {
                return Result.Failure(
                    "warranty.supplier_tracking_identity_missing",
                    "Active Supplier with permanent DealerCode is required for replacement tracking.");
            }

            var claimItems = await _warranty.GetClaimItemsForDiscoveryAsync(claim.Id, ct);
            var claimItemById = claimItems.ToDictionary(x => x.Id);
            var allClaimUnits = await _warranty.GetClaimUnitsForDiscoveryAsync(claim.Id, ct);
            var claimUnitById = allClaimUnits.ToDictionary(x => x.Id);

            var selectedUnits = new List<(WarrantyClaimItemUnit Link, WarrantyClaimItem Item, CustomerWarrantyReplacementUnitInput Input)>();
            foreach (var input in command.Units)
            {
                if (!claimUnitById.TryGetValue(input.ClaimItemUnitId, out var link) ||
                    !claimItemById.TryGetValue(link.ClaimItemId, out var item) ||
                    link.OriginalInventoryUnitId is null)
                {
                    return Result.Failure(
                        "warranty.replacement_link_invalid",
                        "Replacement input does not belong to the warranty claim.");
                }

                selectedUnits.Add((link, item, input));
            }

            if (selectedUnits.All(x => x.Link.ReplacementInventoryUnitId is not null))
            {
                return Result.Success();
            }

            if (selectedUnits.Any(x => x.Link.ReplacementInventoryUnitId is not null))
            {
                return Result.Failure(
                    "warranty.replacement_partial_replay",
                    "Some selected claim units already have replacements. Reload the claim before retrying.");
            }

            var productIds = selectedUnits.Select(x => x.Item.ProductId).Distinct().OrderBy(x => x).ToArray();
            foreach (var productId in productIds)
            {
                await _resourceLock.AcquireAsync("product", productId, ct);
            }

            var discoveredSelections = selectedUnits.ToDictionary(x => x.Link.Id,
                x => (ItemId: x.Item.Id, ProductId: x.Item.ProductId, OriginalUnitId: x.Link.OriginalInventoryUnitId));
            foreach (var productId in productIds)
            {
                await _resourceLock.AcquireAsync("supplier-product", $"{supplierId:D}:{productId:D}", ct);
            }
            var originalUnitIds = selectedUnits
                .Select(x => x.Link.OriginalInventoryUnitId!.Value)
                .Distinct()
                .OrderBy(x => x)
                .ToArray();

            foreach (var unitId in originalUnitIds)
            {
                await _resourceLock.AcquireAsync("warranty-unit", unitId, ct);
            }

            var identityKeys = selectedUnits
                .SelectMany(x => new[]
                {
                    NormalizeSerial(x.Input.SerialNumber) is string serial ? $"SERIAL:{serial}" : null,
                    NormalizeImei(x.Input.Imei1) is string imei1 ? $"IMEI:{imei1}" : null,
                    NormalizeImei(x.Input.Imei2) is string imei2 ? $"IMEI:{imei2}" : null
                })
                .Where(x => x is not null)
                .Select(x => x!)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();

            foreach (var identityKey in identityKeys)
            {
                await _resourceLock.AcquireAsync("inventory-identity", identityKey, ct);
            }

            claim = await _warranty.GetClaimForUpdateAsync(command.ClaimId, ct);
            if (claim is null)
            {
                return Result.Failure("warranty.claim_not_found", "Warranty claim was not found.");
            }
            if (claim.SupplierId != supplierId)
            {
                return Result.Failure("warranty.replacement_context_changed_retry", "Warranty Supplier changed; reload the claim before retrying.");
            }
            if (claim.Status is not WarrantyClaimStatus.SentToSupplier and not WarrantyClaimStatus.SupplierProcessing)
            {
                return Result.Failure("warranty.replacement_wrong_state", "Customer replacement can be received only after the claim was sent to the Supplier.");
            }
            claimItems = await _warranty.GetClaimItemsAsync(claim.Id, ct);
            claimItemById = claimItems.ToDictionary(x => x.Id);
            allClaimUnits = await _warranty.GetClaimUnitsAsync(claim.Id, ct);
            claimUnitById = allClaimUnits.ToDictionary(x => x.Id);
            selectedUnits.Clear();
            foreach (var input in command.Units)
            {
                if (!claimUnitById.TryGetValue(input.ClaimItemUnitId, out var link) ||
                    !claimItemById.TryGetValue(link.ClaimItemId, out var item) ||
                    !discoveredSelections.TryGetValue(link.Id, out var discovered) ||
                    item.Id != discovered.ItemId || item.ProductId != discovered.ProductId ||
                    link.OriginalInventoryUnitId != discovered.OriginalUnitId)
                {
                    return Result.Failure("warranty.replacement_context_changed_retry", "Warranty unit membership changed; reload the claim before retrying.");
                }
                selectedUnits.Add((link, item, input));
            }
            if (selectedUnits.All(x => x.Link.ReplacementInventoryUnitId is not null))
            {
                return Result.Success();
            }
            if (selectedUnits.Any(x => x.Link.ReplacementInventoryUnitId is not null))
            {
                return Result.Failure("warranty.replacement_partial_replay", "Some selected claim units already have replacements. Reload the claim before retrying.");
            }
            var products = new Dictionary<Guid, Product>();
            var supplierProducts = new Dictionary<Guid, SupplierProduct>();
            foreach (var productId in productIds)
            {
                var product = await _catalog.GetProductForUpdateAsync(productId, ct);
                if (product is null || product.TrackingMode is not (TrackingMode.Serialized or TrackingMode.IndividualPiece or TrackingMode.Container) ||
                    string.IsNullOrWhiteSpace(product.Sku))
                {
                    return Result.Failure(
                        "warranty.replacement_product_not_trackable",
                        "Customer exact-unit replacement requires an active serialized Product with permanent SKU.");
                }

                products[productId] = product;


                var supplierProduct = await _traceability.GetSupplierProductForUpdateAsync(
                    supplierId,
                    productId,
                    ct);
                if (supplierProduct is null || !supplierProduct.IsActive)
                {
                    return Result.Failure(
                        "warranty.supplier_product_missing",
                        "Active SupplierProduct sequence authority is required for replacement.");
                }

                supplierProducts[productId] = supplierProduct;
            }

            var originalUnitsById = new Dictionary<Guid, InventoryUnit>();
            foreach (var productId in productIds)
            {
                var ids = selectedUnits
                    .Where(x => x.Item.ProductId == productId)
                    .Select(x => x.Link.OriginalInventoryUnitId!.Value)
                    .Distinct()
                    .ToArray();

                var units = await _inventory.GetInventoryUnitsForUpdateAsync(productId, ids, ct);
                if (units.Count != ids.Length)
                {
                    return Result.Failure(
                        "warranty.original_unit_missing",
                        "One or more original warranty units were not found.");
                }

                foreach (var unit in units)
                {
                    if (unit.SupplierProductId is not Guid originalSupplierProductId)
                    {
                        return Result.Failure(
                            "warranty.original_supplier_provenance_missing",
                            "Original unit is missing SupplierProduct provenance.");
                    }

                    var sourceSupplierProduct = await _traceability.GetSupplierProductByIdForUpdateAsync(
                        originalSupplierProductId,
                        ct);
                    if (sourceSupplierProduct is null || sourceSupplierProduct.SupplierId != supplierId)
                    {
                        return Result.Failure(
                            "warranty.original_supplier_provenance_mismatch",
                            "Original unit Supplier does not match the warranty claim Supplier.");
                    }

                    originalUnitsById[unit.Id] = unit;
                }
            }

            foreach (var selected in selectedUnits)
            {
                var product = products[selected.Item.ProductId];
                var serial = NormalizeSerial(selected.Input.SerialNumber);
                var imei1 = NormalizeImei(selected.Input.Imei1);
                var imei2 = NormalizeImei(selected.Input.Imei2);

                if (product.SerialTrackingEnabled && serial is null)
                {
                    return Result.Failure(
                        "warranty.replacement_serial_required",
                        $"Serial number is required for '{product.Name}'.");
                }

                if (product.ImeiTrackingEnabled && imei1 is null)
                {
                    return Result.Failure(
                        "warranty.replacement_imei_required",
                        $"IMEI1 is required for '{product.Name}'.");
                }

                if (await _inventory.InventoryIdentityExistsAsync(serial, imei1, imei2, ct))
                {
                    return Result.Failure(
                        "warranty.replacement_identity_exists",
                        "Replacement Serial/IMEI already exists in inventory history.");
                }
            }

            var replacementReferences = new Dictionary<Guid, List<string>>();
            if (_physicalUnits is null)
            {
                return Result.Failure(
                    "inventory.physical_unit_authority_unavailable",
                    "Physical-unit creation authority is unavailable.");
            }

            foreach (var group in selectedUnits
                .OrderBy(x => x.Item.ProductId)
                .ThenBy(x => x.Link.Id)
                .GroupBy(x => x.Item.ProductId))
            {
                var ordered = group.ToArray();
                var entries = ordered.Select(selected =>
                {
                    var original = originalUnitsById[selected.Link.OriginalInventoryUnitId!.Value];
                    return new PhysicalUnitCreationEntry(
                        selected.Input.SerialNumber,
                        selected.Input.Imei1,
                        selected.Input.Imei2,
                        InventoryUnitStatus.WarrantyCustomerHeld,
                        original.AcquisitionCost,
                        null,
                        InventoryUnitOriginType.WarrantyReplacement,
                        SourceWarrantyClaimItemId: selected.Item.Id);
                }).ToArray();
                var creation = await _physicalUnits.CreateAsync(supplierId, group.Key, entries, ct);
                if (!creation.IsSuccess || creation.Value is null)
                {
                    return Result.Failure(creation.Error!.Code, creation.Error.Message);
                }
                for (var i = 0; i < ordered.Length; i++)
                {
                    var selected = ordered[i];
                    var replacement = creation.Value[i];
                    selected.Link.ReplacementInventoryUnitId = replacement.Id;
                    selected.Link.ReplacementIdentitySnapshot = BuildIdentitySnapshot(replacement);
                    if (!replacementReferences.TryGetValue(selected.Item.Id, out var refs))
                    {
                        refs = new List<string>();
                        replacementReferences[selected.Item.Id] = refs;
                    }
                    refs.Add(replacement.TrackingCode!);
                }
            }
            foreach (var item in claimItems)
            {
                var itemLinks = allClaimUnits.Where(x => x.ClaimItemId == item.Id).ToArray();
                if (itemLinks.Length > 0 && itemLinks.All(x => x.ReplacementInventoryUnitId is not null))
                {
                    item.ResolutionType = WarrantyResolutionType.Replaced;
                    item.ReplacementProductId = item.ProductId;
                    item.ResolutionNote = command.Note?.Trim();
                    if (replacementReferences.TryGetValue(item.Id, out var refs))
                    {
                        item.ReplacementReference = string.Join(",", refs);
                    }
                }
            }

            var allResolved = claimItems.All(x => x.ResolutionType is not null);
            if (allResolved)
            {
                try
                {
                    claim.MarkReadyForCustomer(_clock.UtcNow);
                }
                catch (BusinessRuleException ex)
                {
                    return Result.Failure(ex.Code, ex.Message);
                }
            }

            var now = _clock.UtcNow;
            _warranty.AddClaimEvent(new WarrantyClaimEvent
            {
                ClaimId = claim.Id,
                Status = claim.Status,
                Custody = claim.CurrentCustody,
                EventType = allResolved ? "REPLACEMENT_READY_FOR_CUSTOMER" : "REPLACEMENT_RECEIVED",
                Note = command.Note?.Trim(),
                ActorId = command.ActorId,
                OccurredAt = now
            });
            _warranty.AddOperation(new WarrantyOperation
            {
                ClientOperationId = command.ClientOperationId,
                TargetType = "CUSTOMER_CLAIM",
                TargetId = claim.Id,
                OperationType = WarrantyOperationType.CustomerReplacement,
                ActorId = command.ActorId,
                PayloadHash = payloadHash,
                ResultId = claim.Id,
                OccurredAt = now
            });

            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }, cancellationToken);
    }

    private static string? NormalizeSerial(string? value) =>
        IdentityNormalizationRules.NormalizeOptionalSerialNumber(value);

    private static string? NormalizeImei(string? value) =>
        IdentityNormalizationRules.NormalizeOptionalImei(value);

    private static string BuildIdentitySnapshot(InventoryUnit unit)
    {
        var parts = new[]
        {
            unit.TrackingCode,
            unit.SerialNumber,
            unit.Imei1,
            unit.Imei2
        }.Where(x => !string.IsNullOrWhiteSpace(x));

        return string.Join(" | ", parts);
    }
}

public sealed record SendShopStockToSupplierWarrantyCommand(
    Guid ProductId,
    InventoryBucket SourceBucket,
    decimal BaseQuantity,
    Guid SupplierId,
    Guid? SourcePurchaseItemId,
    string FaultDescription,
    Guid ActorId,
    Guid ClientOperationId,
    IReadOnlyCollection<Guid>? InventoryUnitIds);

public sealed class SendShopStockToSupplierWarrantyHandler
{
    private readonly IWarrantyRepository _warranty;
    private readonly IOperationLock? _operationLock;
    private readonly ICatalogRepository _catalog;
    private readonly IPartyRepository _parties;
    private readonly IPurchasingRepository _purchases;
    private readonly IInventoryRepository _inventory;
    private readonly ITraceabilityRepository _traceability;
    private readonly IInventoryConditionService _conditions;
    private readonly IResourceLock _resourceLock;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly IBusinessAuditWriter _audit;
    private readonly IDocumentNumberService _numbers;
    private readonly IClock _clock;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;

    public SendShopStockToSupplierWarrantyHandler(
        IWarrantyRepository warranty,
        ICatalogRepository catalog,
        IPartyRepository parties,
        IPurchasingRepository purchases,
        IInventoryRepository inventory,
        ITraceabilityRepository traceability,
        IInventoryConditionService conditions,
        IResourceLock resourceLock,
        IApplicationPermissionAuthorizer authorization,
        IBusinessAuditWriter audit,
        IDocumentNumberService numbers,
        IClock clock,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork,
        IOperationLock? operationLock = null)
    {
        _warranty = warranty;
        _operationLock = operationLock;
        _catalog = catalog;
        _parties = parties;
        _purchases = purchases;
        _inventory = inventory;
        _traceability = traceability;
        _conditions = conditions;
        _resourceLock = resourceLock;
        _authorization = authorization;
        _audit = audit;
        _numbers = numbers;
        _clock = clock;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
    }

    public Task<Result<Guid>> HandleAsync(
        SendShopStockToSupplierWarrantyCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ProductId == Guid.Empty ||
            command.SupplierId == Guid.Empty ||
            command.BaseQuantity <= 0 ||
            string.IsNullOrWhiteSpace(command.FaultDescription))
        {
            return Task.FromResult(Result<Guid>.Failure(
                "warranty.shop_send_invalid",
                "Product, Supplier, positive quantity, and fault description are required."));
        }

        return _transactions.ExecuteAsync(async ct =>
        {
            var authorization = await _authorization.AuthorizeAsync(
                command.ActorId,
                PermissionKeys.WarrantyShopStockManage,
                ct);
            if (!authorization.IsSuccess)
            {
                return Result<Guid>.Failure(
                    authorization.Error!.Code,
                    authorization.Error.Message);
            }

            if (command.ClientOperationId == Guid.Empty)
            {
                return Result<Guid>.Failure("validation.client_operation_id_required", "ClientOperationId is required for Warranty mutations.");
            }
            if (_operationLock is not null)
            {
                await _operationLock.AcquireAsync(command.ClientOperationId, ct);
            }

            if (command.SourceBucket is not InventoryBucket.Damaged and
                not InventoryBucket.Defective)
            {
                return Result<Guid>.Failure(
                    "warranty.invalid_shop_source_bucket",
                    "Only damaged or defective shop stock can be sent for Supplier warranty.");
            }

            await _resourceLock.AcquireAsync("product", command.ProductId, ct);

            var product = await _catalog.GetProductAsync(command.ProductId, ct);
            if (product is null || !product.IsActive)
            {
                return Result<Guid>.Failure(
                    "catalog.product_not_found",
                    "Warranty product was not found or is inactive.");
            }

            var supplier = await _parties.GetSupplierAsync(command.SupplierId, ct);
            if (supplier is null || !supplier.IsActive)
            {
                return Result<Guid>.Failure(
                    "warranty.supplier_not_active",
                    "Warranty Supplier was not found or is inactive.");
            }

            var quantity = product.TrackingMode is TrackingMode.Serialized or TrackingMode.IndividualPiece or TrackingMode.Container
                ? command.BaseQuantity
                : QuantityMath.RoundQuantity(command.BaseQuantity);

            IReadOnlyList<InventoryUnit>? units = null;
            InventoryLot? targetLot = null;
            if (product.TrackingMode is TrackingMode.Serialized or TrackingMode.IndividualPiece or TrackingMode.Container)
            {
                if (!QuantityMath.IsWhole(command.BaseQuantity))
                {
                    return Result<Guid>.Failure(
                        "warranty.serialized_quantity_whole",
                        "Serialized shop warranty quantity must be an exact whole number before rounding.");
                }

                if (command.InventoryUnitIds is null ||
                    command.InventoryUnitIds.Count == 0 ||
                    (product.TrackingMode != TrackingMode.Container && command.InventoryUnitIds.Count != quantity) ||
                    command.InventoryUnitIds.Distinct().Count() != command.InventoryUnitIds.Count)
                {
                    return Result<Guid>.Failure(
                        "warranty.serialized_units_required",
                        "Select one distinct exact InventoryUnit for every serialized warranty unit.");
                }

                foreach (var unitId in command.InventoryUnitIds.OrderBy(x => x))
                {
                    await _resourceLock.AcquireAsync("warranty-unit", unitId, ct);
                }

                units = await _inventory.GetInventoryUnitsForUpdateAsync(
                    product.Id,
                    command.InventoryUnitIds,
                    ct);
                if (units.Count != command.InventoryUnitIds.Count)
                {
                    return Result<Guid>.Failure(
                        "warranty.serialized_unit_missing",
                        "One or more selected warranty units were not found.");
                }

                foreach (var unit in units)
                {
                    if (unit.SupplierProductId is not Guid supplierProductId)
                    {
                        return Result<Guid>.Failure(
                            "warranty.supplier_provenance_missing",
                            "Selected physical unit is missing SupplierProduct provenance.");
                    }

                    var supplierProduct = await _traceability.GetSupplierProductByIdForUpdateAsync(
                        supplierProductId,
                        ct);
                    if (supplierProduct is null || supplierProduct.SupplierId != command.SupplierId)
                    {
                        return Result<Guid>.Failure(
                            "warranty.wrong_supplier",
                            "Selected physical unit does not belong to the requested Supplier provenance.");
                    }
                }
                if (product.TrackingMode == TrackingMode.Container)
                {
                    decimal selectedQuantity = 0m;
                    foreach (var unit in units)
                        selectedQuantity += await _inventory.GetPhysicalUnitBaseQuantitySnapshotAsync(unit, ct);
                    if (selectedQuantity != quantity)
                        return Result<Guid>.Failure("warranty.container_quantity_mismatch",
                            "Warranty quantity must match the original physical pack quantities.");
                }
            }
            else
            {
                if (command.InventoryUnitIds is { Count: > 0 })
                {
                    return Result<Guid>.Failure(
                        "warranty.units_not_allowed",
                        "Quantity/length warranty does not accept exact InventoryUnit IDs.");
                }

                if (command.SourcePurchaseItemId is not Guid sourcePurchaseItemId)
                {
                    return Result<Guid>.Failure(
                        "warranty.source_purchase_required",
                        "Quantity/length supplier warranty requires source PurchaseItem provenance.");
                }

                var sourceItem = await _purchases.GetPurchaseItemForUpdateAsync(sourcePurchaseItemId, ct);
                if (sourceItem is null || sourceItem.ProductId != product.Id)
                {
                    return Result<Guid>.Failure(
                        "warranty.source_purchase_invalid",
                        "Source PurchaseItem does not match the warranty Product.");
                }

                var sourcePurchase = await _purchases.GetPurchaseForUpdateAsync(
                    sourceItem.PurchaseId,
                    ct);
                if (sourcePurchase is null || sourcePurchase.SupplierId != command.SupplierId)
                {
                    return Result<Guid>.Failure(
                        "warranty.wrong_supplier",
                        "Source purchase Supplier does not match the requested warranty Supplier.");
                }

                var lotPositions = await _inventory.GetPurchaseItemLotPositionsForUpdateAsync(
                    sourceItem.Id,
                    command.SourceBucket,
                    ct);
                var lotPosition = lotPositions.FirstOrDefault();
                if (lotPosition is null || lotPosition.Balance.Quantity < quantity)
                {
                    return Result<Guid>.Failure(
                        "warranty.source_capacity_exceeded",
                        "Selected source lot has insufficient quantity in the requested bucket.");
                }
                targetLot = lotPosition.Lot;
            }

            var payloadHash = WarrantyOperationIdentity.Hash(
                "SHOP_STOCK",
                "SEND",
                command.ProductId.ToString("D"),
                command.SourceBucket.ToString(),
                QuantityMath.RoundQuantity(command.BaseQuantity).ToString(System.Globalization.CultureInfo.InvariantCulture),
                command.SupplierId.ToString("D"),
                command.SourcePurchaseItemId?.ToString("D"),
                string.Join(",", (command.InventoryUnitIds ?? Array.Empty<Guid>()).OrderBy(x => x).Select(x => x.ToString("D"))),
                WarrantyOperationIdentity.Normalize(command.FaultDescription));
            var existingOperation = await _warranty.GetOperationByClientOperationIdAsync(command.ClientOperationId, ct);
            if (existingOperation is not null)
            {
                if (existingOperation.OperationType != WarrantyOperationType.ShopSend ||
                    existingOperation.TargetType != "SHOP_STOCK" ||
                    existingOperation.PayloadHash != payloadHash)
                {
                    return Result<Guid>.Failure("payload_mismatch", "Operation was previously submitted with a different Warranty payload.");
                }
                return existingOperation.ResultId is Guid existingCaseId
                    ? Result<Guid>.Success(existingCaseId)
                    : Result<Guid>.Failure("warranty.operation_result_missing", "Previously completed Warranty operation has no result identity.");
            }

            var now = _clock.UtcNow;
            var warrantyCase = new ShopStockWarrantyCase
            {
                CaseNumber = await _numbers.NextAsync("SWC", ct),
                ProductId = command.ProductId,
                BaseQuantity = quantity,
                SupplierId = command.SupplierId,
                SourcePurchaseItemId = units is not null ? null : command.SourcePurchaseItemId,
                FaultDescription = command.FaultDescription.Trim(),
                Status = ShopWarrantyCaseStatus.Open,
                CreatedAt = now,
                CreatedBy = command.ActorId
            };
            _warranty.AddShopStockCase(warrantyCase);

            var transfer = await _conditions.TransferAsync(
                new TransferInventoryConditionCommand(
                    command.ProductId,
                    command.SourceBucket,
                    InventoryBucket.WithSupplier,
                    quantity,
                    command.ActorId,
                    "SUPPLIER_WARRANTY",
                    command.FaultDescription,
                    "SHOP_WARRANTY",
                    warrantyCase.Id,
                    command.InventoryUnitIds,
                    InventoryMovementType.SendToSupplierWarranty,
                    TargetLotId: targetLot?.Id,
                    CorrelationId: command.ClientOperationId),
                ct);

            if (!transfer.IsSuccess)
            {
                return Result<Guid>.Failure(transfer.Error!.Code, transfer.Error.Message);
            }

            if (targetLot is not null)
            {
                var costState = await _inventory.GetCostStateForUpdateAsync(command.ProductId, ct);
                var sendTimeMwa = costState is not null && costState.CostedQty > 0
                    ? Cost(costState.TotalInventoryCost / costState.CostedQty)
                    : targetLot.OriginalUnitCost;
                var sendTimeCarrying = Cost(sendTimeMwa * quantity);

                _warranty.AddShopWarrantySendAllocation(new ShopWarrantySendAllocation
                {
                    CaseId = warrantyCase.Id,
                    OriginalInventoryLotId = targetLot.Id,
                    SendMovementId = transfer.Value,
                    BaseQuantity = quantity,
                    SourceUnitCostSnapshot = targetLot.OriginalUnitCost,
                    SendTimeMwaUnitCostSnapshot = sendTimeMwa,
                    SendTimeCarryingValueSnapshot = sendTimeCarrying,
                    ClientOperationId = command.ClientOperationId,
                    ActorId = command.ActorId,
                    OccurredAt = now
                });
            }
            else if (units is not null)
            {
                var costState = await _inventory.GetCostStateForUpdateAsync(command.ProductId, ct);
                foreach (var unitGroup in units.GroupBy(u => u.InventoryLotId!.Value).OrderBy(x => x.Key))
                {
                    decimal groupQty = 0m;
                    foreach (var u in unitGroup)
                    {
                        groupQty += await _inventory.GetPhysicalUnitBaseQuantitySnapshotAsync(u, ct);
                    }
                    var lot = await _inventory.GetInventoryLotForUpdateAsync(unitGroup.Key, ct);
                    var sendTimeMwa = costState is not null && costState.CostedQty > 0
                        ? Cost(costState.TotalInventoryCost / costState.CostedQty)
                        : (lot?.OriginalUnitCost ?? 0m);
                    _warranty.AddShopWarrantySendAllocation(new ShopWarrantySendAllocation
                    {
                        CaseId = warrantyCase.Id,
                        OriginalInventoryLotId = unitGroup.Key,
                        SendMovementId = transfer.Value,
                        BaseQuantity = groupQty,
                        SourceUnitCostSnapshot = lot?.OriginalUnitCost ?? 0m,
                        SendTimeMwaUnitCostSnapshot = sendTimeMwa,
                        SendTimeCarryingValueSnapshot = Cost(sendTimeMwa * groupQty),
                        ClientOperationId = command.ClientOperationId,
                        ActorId = command.ActorId,
                        OccurredAt = now
                    });
                }
            }

            warrantyCase.Status = ShopWarrantyCaseStatus.WithSupplier;
            warrantyCase.SentAt = now;
            warrantyCase.Version++;

            _audit.Record(
                "SHOP_WARRANTY_SENT",
                "SHOP_WARRANTY",
                warrantyCase.Id,
                command.ActorId,
                command.ClientOperationId,
                $"Case {warrantyCase.CaseNumber}; supplier={command.SupplierId:D}; product={command.ProductId:D}; qty={quantity}.");
            _warranty.AddOperation(new WarrantyOperation
            {
                ClientOperationId = command.ClientOperationId,
                TargetType = "SHOP_STOCK",
                TargetId = warrantyCase.Id,
                OperationType = WarrantyOperationType.ShopSend,
                ActorId = command.ActorId,
                PayloadHash = payloadHash,
                ResultId = warrantyCase.Id,
                OccurredAt = now
            });

            await _unitOfWork.SaveChangesAsync(ct);
            return Result<Guid>.Success(warrantyCase.Id);
        }, cancellationToken);
    }

    private static decimal Money(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    private static decimal Cost(decimal value) =>
        decimal.Round(value, 6, MidpointRounding.AwayFromZero);
}

public sealed record ReplacementSerializedUnitInput(
    string? SerialNumber,
    string? Imei1,
    string? Imei2);

public sealed record ReceiveShopStockWarrantyCommand(
    Guid CaseId,
    WarrantyResolutionType Resolution,
    Guid ActorId,
    IReadOnlyCollection<Guid>? OriginalInventoryUnitIds,
    IReadOnlyList<ReplacementSerializedUnitInput>? ReplacementUnits,
    string? Note,
    Guid ClientOperationId,
    decimal? SupplierCreditAmount = null,
    string? SupplierReference = null,
    decimal? ResolvedQuantity = null);

public sealed class ReceiveShopStockWarrantyHandler
{
    private readonly ICatalogRepository _catalog;
    private readonly IInventoryRepository _inventory;
    private readonly IInventoryConditionService _conditions;
    private readonly IInventoryCostAllocator _costAllocator;
    private readonly IWarrantyRepository _warranty;
    private readonly ITraceabilityRepository _traceability;
    private readonly IPartyRepository _parties;
    private readonly ISupplierAccountRepository _supplierAccounts;
    private readonly IOperationLock _operationLock;
    private readonly IResourceLock _resourceLock;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly IDocumentNumberService _numbers;
    private readonly IBusinessAuditWriter _audit;
    private readonly IClock _clock;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IPhysicalUnitCreationAuthority? _physicalUnits;

    public ReceiveShopStockWarrantyHandler(
        ICatalogRepository catalog,
        IInventoryRepository inventory,
        IInventoryConditionService conditions,
        IInventoryCostAllocator costAllocator,
        IWarrantyRepository warranty,
        ITraceabilityRepository traceability,
        IPartyRepository parties,
        ISupplierAccountRepository supplierAccounts,
        IOperationLock operationLock,
        IResourceLock resourceLock,
        IApplicationPermissionAuthorizer authorization,
        IDocumentNumberService numbers,
        IBusinessAuditWriter audit,
        IClock clock,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork,
        IPhysicalUnitCreationAuthority? physicalUnitCreationAuthority = null)
    {
        _catalog = catalog;
        _inventory = inventory;
        _conditions = conditions;
        _costAllocator = costAllocator;
        _warranty = warranty;
        _traceability = traceability;
        _parties = parties;
        _supplierAccounts = supplierAccounts;
        _operationLock = operationLock;
        _resourceLock = resourceLock;
        _authorization = authorization;
        _numbers = numbers;
        _audit = audit;
        _clock = clock;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
        _physicalUnits = physicalUnitCreationAuthority;
    }

    public Task<Result> HandleAsync(
        ReceiveShopStockWarrantyCommand command,
        CancellationToken cancellationToken)
    {
        if (command.CaseId == Guid.Empty)
        {
            return Task.FromResult(Result.Failure(
                "warranty.shop_case_required",
                "Warranty case is required."));
        }

        if (command.ClientOperationId == Guid.Empty)
        {
            return Task.FromResult(Result.Failure(
                "validation.client_operation_id_required",
                "ClientOperationId is required for Warranty mutations."));
        }

        if (command.Resolution is WarrantyResolutionType.Refunded or WarrantyResolutionType.Other)
        {
            return Task.FromResult(Result.Failure(
                "warranty.shop_resolution_invalid",
                "Shop-owned warranty cases support Repaired, Replaced, Rejected, Scrapped, or Credited outcomes."));
        }

        if (command.Resolution == WarrantyResolutionType.Credited &&
            (command.SupplierCreditAmount is null || command.SupplierCreditAmount <= 0))
        {
            return Task.FromResult(Result.Failure(
                "warranty.credit_input_required",
                "Warranty credit requires ClientOperationId and a positive SupplierCreditAmount."));
        }

        if (command.ResolvedQuantity is <= 0m)
        {
            return Task.FromResult(Result.Failure(
                "validation.resolved_quantity_invalid",
                "Resolved quantity must be greater than zero."));
        }

        return _transactions.ExecuteAsync(async ct =>
        {
            var permission = command.Resolution == WarrantyResolutionType.Credited
                ? PermissionKeys.WarrantyShopStockCredit
                : PermissionKeys.WarrantyShopStockManage;

            var authorization = await _authorization.AuthorizeAsync(
                command.ActorId,
                permission,
                ct);
            if (!authorization.IsSuccess)
            {
                return Result.Failure(authorization.Error!.Code, authorization.Error.Message);
            }

            await _operationLock.AcquireAsync(command.ClientOperationId, ct);

            var preview = await _warranty.GetShopStockCaseAsync(command.CaseId, ct);
            if (preview is null)
            {
                return Result.Failure(
                    "warranty.shop_case_not_found",
                    "Shop warranty case was not found.");
            }

            var operationType = command.Resolution switch
            {
                WarrantyResolutionType.Repaired => WarrantyOperationType.ShopReceiveRepaired,
                WarrantyResolutionType.Rejected => WarrantyOperationType.ShopReceiveRejected,
                WarrantyResolutionType.Scrapped => WarrantyOperationType.ShopReceiveScrapped,
                WarrantyResolutionType.Replaced => WarrantyOperationType.ShopReceiveReplacement,
                WarrantyResolutionType.Credited => WarrantyOperationType.ShopSupplierCredit,
                _ => throw new BusinessRuleException("warranty.shop_resolution_invalid", "Unsupported shop warranty resolution.")
            };
            string replacementPayload;
            try
            {
                replacementPayload = System.Text.Json.JsonSerializer.Serialize(
                    (command.ReplacementUnits ?? Array.Empty<ReplacementSerializedUnitInput>())
                        .Select(x => WarrantyOperationIdentity.ManufacturerIdentity(x.SerialNumber, x.Imei1, x.Imei2)));
            }
            catch (BusinessRuleException ex)
            {
                return Result.Failure(ex.Code, ex.Message);
            }
            var payloadHash = WarrantyOperationIdentity.Hash(
                "SHOP_STOCK",
                "RECEIVE",
                command.CaseId.ToString("D"),
                command.Resolution.ToString(),
                string.Join(",", (command.OriginalInventoryUnitIds ?? Array.Empty<Guid>()).OrderBy(x => x).Select(x => x.ToString("D"))),
                replacementPayload,
                WarrantyOperationIdentity.Normalize(command.Note),
                command.SupplierCreditAmount?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                WarrantyOperationIdentity.Normalize(command.SupplierReference),
                command.ResolvedQuantity?.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var legacyPayloadHash = WarrantyOperationIdentity.Hash("SHOP_STOCK", "RECEIVE", command.CaseId.ToString("D"),
                command.Resolution.ToString(), string.Join(",", (command.OriginalInventoryUnitIds ?? Array.Empty<Guid>()).OrderBy(x => x).Select(x => x.ToString("D"))),
                string.Join(";", (command.ReplacementUnits ?? Array.Empty<ReplacementSerializedUnitInput>()).Select(x =>
                    $"{WarrantyOperationIdentity.Normalize(x.SerialNumber)}|{WarrantyOperationIdentity.Normalize(x.Imei1)}|{WarrantyOperationIdentity.Normalize(x.Imei2)}")),
                WarrantyOperationIdentity.Normalize(command.Note), command.SupplierCreditAmount?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                WarrantyOperationIdentity.Normalize(command.SupplierReference));
            var existingOperation = await _warranty.GetOperationForReplayAsync(command.ClientOperationId, ct);
            if (existingOperation is not null)
            {
                if (existingOperation.OperationType != operationType ||
                    existingOperation.TargetType != "SHOP_STOCK" ||
                    existingOperation.TargetId != command.CaseId)
                {
                    return Result.Failure("payload_mismatch", "Operation was previously submitted with a different Warranty payload.");
                }
                if (existingOperation.PayloadHash != payloadHash && existingOperation.PayloadHash != legacyPayloadHash)
                {
                    return Result.Failure("warranty.replay_reconciliation_required",
                        "Stored Warranty payload cannot establish replay equivalence, including a possible legacy hash. Reconcile the original operation before retrying; no replacement was allocated.");
                }
                return Result.Success();
            }

            await _resourceLock.AcquireAsync("warranty-case", command.CaseId, ct);
            await _resourceLock.AcquireAsync("product", preview.ProductId, ct);

            if (command.Resolution == WarrantyResolutionType.Replaced)

            {
                await _resourceLock.AcquireAsync(
                    "supplier-product",
                    $"{preview.SupplierId:D}:{preview.ProductId:D}",
                    ct);
            }

            if (command.Resolution == WarrantyResolutionType.Credited)
            {
                await _resourceLock.AcquireAsync("supplier-account", preview.SupplierId, ct);
            }

            var submittedOriginalUnitIds = command.OriginalInventoryUnitIds?
                .Distinct()
                .OrderBy(x => x)
                .ToArray() ?? Array.Empty<Guid>();

            foreach (var unitId in submittedOriginalUnitIds)
            {
                await _resourceLock.AcquireAsync("warranty-unit", unitId, ct);
            }

            if (command.Resolution == WarrantyResolutionType.Replaced &&
                command.ReplacementUnits is { Count: > 0 })
            {
                var identityKeys = command.ReplacementUnits
                    .SelectMany(x => new[]
                    {
                        NormalizeSerial(x.SerialNumber) is string serial ? $"SERIAL:{serial}" : null,
                        NormalizeImei(x.Imei1) is string imei1 ? $"IMEI:{imei1}" : null,
                        NormalizeImei(x.Imei2) is string imei2 ? $"IMEI:{imei2}" : null
                    })
                    .Where(x => x is not null)
                    .Select(x => x!)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToArray();

                foreach (var identityKey in identityKeys)
                {
                    await _resourceLock.AcquireAsync("inventory-identity", identityKey, ct);
                }
            }

            var warrantyCase = await _warranty.GetShopStockCaseForUpdateAsync(command.CaseId, ct);
            if (warrantyCase is null)
            {
                return Result.Failure(
                    "warranty.shop_case_not_found",
                    "Shop warranty case was not found.");
            }

            if (warrantyCase.ProductId != preview.ProductId || warrantyCase.SupplierId != preview.SupplierId)
            {
                return Result.Failure("warranty.shop_context_changed_retry", "Warranty Product or Supplier changed; reload the case before retrying.");
            }
            var product = await _catalog.GetProductForUpdateAsync(preview.ProductId, ct);
            if (product is null)
            {
                return Result.Failure("catalog.product_not_found", "Warranty product was not found.");
            }
            if (warrantyCase.Status == ShopWarrantyCaseStatus.Closed &&
                command.ClientOperationId is Guid replayId &&
                warrantyCase.ResolutionClientOperationId == replayId)
            {
                return Result.Success();
            }

            if (warrantyCase.Status != ShopWarrantyCaseStatus.WithSupplier)
            {
                return Result.Failure(
                    "warranty.shop_case_not_with_supplier",
                    "Warranty case is not currently with the Supplier.");
            }

            if (product.TrackingMode is TrackingMode.Quantity or TrackingMode.Length &&
                command.OriginalInventoryUnitIds is { Count: > 0 })
            {
                return Result.Failure(
                    "warranty.units_not_allowed",
                    "Quantity/length warranty does not accept exact InventoryUnit IDs.");
            }

            var sends = await _warranty.GetShopWarrantySendAllocationsByCaseIdAsync(warrantyCase.Id, ct);
            var resolutions = await _warranty.GetShopWarrantyResolutionAllocationsByCaseIdAsync(warrantyCase.Id, ct);
            var exact = product.TrackingMode is TrackingMode.Serialized or TrackingMode.IndividualPiece or TrackingMode.Container;
            if (sends.Any(x => x.CaseId != warrantyCase.Id || x.BaseQuantity <= 0m) ||
                resolutions.Any(x => x.ResolvedBaseQuantity <= 0m || !sends.Any(a => a.Id == x.SendAllocationId)) ||
                sends.Any(x => resolutions.Where(r => r.SendAllocationId == x.Id).Sum(r => r.ResolvedBaseQuantity) > x.BaseQuantity) ||
                (sends.Count > 0 && sends.Sum(x => x.BaseQuantity) != warrantyCase.BaseQuantity) ||
                (!exact && sends.Count > 1))
            {
                return Result.Failure("warranty.source_evidence_invalid", "Warranty source allocation evidence is missing or inconsistent.");
            }

            var remaining = QuantityMath.RoundQuantity((sends.Count == 0 ? warrantyCase.BaseQuantity : sends.Sum(x => x.BaseQuantity)) - resolutions.Sum(x => x.ResolvedBaseQuantity));
            var groups = new List<ResolutionSource>();
            decimal resolvedQuantity;
            if (exact)
            {
                var links = sends.Count > 0
                    ? await _warranty.GetShopWarrantyMovementUnitsAsync(warrantyCase.Id, ct)
                    : Array.Empty<InventoryMovementUnit>();
                var sentLinks = links.Where(x => sends.Any(a => a.SendMovementId == x.MovementId) && x.ToStatus == InventoryUnitStatus.WithSupplier).ToArray();
                var resolvedLinks = links.Where(x => resolutions.Any(a => a.ResolutionMovementId == x.MovementId) && x.FromStatus == InventoryUnitStatus.WithSupplier).ToArray();
                if (sends.Count == 0)
                {
                    var evidence = await _inventory.GetMovementsByReferenceAsync("SHOP_WARRANTY", warrantyCase.Id, ct);
                    var graphResult = await ValidateLegacyExactGraphAsync(warrantyCase, product, evidence, ct);
                    if (!graphResult.IsSuccess)
                    {
                        return Result.Failure(graphResult.Error!.Code, graphResult.Error.Message);
                    }
                    sentLinks = evidence.Where(x => x.Movement.MovementType == InventoryMovementType.SendToSupplierWarranty)
                        .SelectMany(x => x.Units).ToArray();
                    resolvedLinks = evidence.Where(x => IsShopResolutionMovement(x.Movement.MovementType))
                        .SelectMany(x => x.Units).Where(x => x.FromStatus == InventoryUnitStatus.WithSupplier).ToArray();
                    remaining = graphResult.Value;
                }
                var sentIds = sentLinks.Select(x => x.InventoryUnitId).ToHashSet();
                var resolvedIds = resolvedLinks.Select(x => x.InventoryUnitId).ToHashSet();
                var selected = command.OriginalInventoryUnitIds;
                if (sentIds.Count != sentLinks.Length || resolvedIds.Count != resolvedLinks.Length ||
                    !resolvedIds.IsSubsetOf(sentIds) || selected is null || selected.Count == 0 ||
                    selected.Distinct().Count() != selected.Count || selected.Any(x => !sentIds.Contains(x) || resolvedIds.Contains(x)))
                {
                    return Result.Failure("warranty.case_unit_mismatch", "Selected identities must be distinct unresolved originals sent in this warranty case.");
                }
                var units = await _inventory.GetInventoryUnitsForUpdateAsync(product.Id, selected, ct);
                if (units.Count != selected.Count || units.Any(x => x.ProductId != product.Id || x.Status != InventoryUnitStatus.WithSupplier || x.InventoryLotId is null))
                {
                    return Result.Failure("warranty.case_unit_mismatch", "Selected identities no longer match warranty custody and provenance.");
                }
                foreach (var unit in units.OrderBy(x => x.Id))
                {
                    if (unit.SupplierProductId is not Guid supplierProductId)
                    {
                        return Result.Failure("warranty.supplier_provenance_missing", "Original physical unit Supplier provenance is missing.");
                    }
                    var supplierProduct = await _traceability.GetSupplierProductByIdForUpdateAsync(supplierProductId, ct);
                    if (supplierProduct is null || supplierProduct.ProductId != product.Id || supplierProduct.SupplierId != warrantyCase.SupplierId)
                    {
                        return Result.Failure("warranty.wrong_supplier", "Original physical unit Supplier provenance does not match the case.");
                    }
                    var link = sentLinks.Single(x => x.InventoryUnitId == unit.Id);
                    var sources = sends.Where(x => x.SendMovementId == link.MovementId && x.OriginalInventoryLotId == unit.InventoryLotId).ToArray();
                    if (sends.Count > 0 && sources.Length != 1)
                    {
                        return Result.Failure("warranty.source_evidence_invalid", "Original identity source allocation is ambiguous.");
                    }
                    var physicalQuantity = await _inventory.GetPhysicalUnitBaseQuantitySnapshotAsync(unit, ct);
                    if (physicalQuantity <= 0m || (product.TrackingMode != TrackingMode.Container && physicalQuantity != 1m))
                    {
                        return Result.Failure("warranty.source_evidence_invalid", "Original physical quantity evidence is invalid.");
                    }
                    var source = sources.SingleOrDefault();
                    var group = groups.SingleOrDefault(x => source is not null
                        ? x.Send?.Id == source.Id
                        : x.Send is null && x.SourceLotId == unit.InventoryLotId && x.SendMovementId == link.MovementId);
                    if (group is null)
                    {
                        group = new ResolutionSource(source, unit.InventoryLotId!.Value, link.MovementId);
                        groups.Add(group);
                    }
                    group.UnitIds.Add(unit.Id);
                    group.Quantity += physicalQuantity;
                    group.ExactCarrying += Cost(physicalQuantity * (unit.AcquisitionCost / physicalQuantity));
                }
                groups = groups.OrderBy(x => x.SourceLotId).ThenBy(x => x.SendMovementId).ThenBy(x => x.Send?.Id).ToList();
                resolvedQuantity = groups.Sum(x => x.Quantity);
                if (command.ResolvedQuantity is decimal explicitQuantity && explicitQuantity != resolvedQuantity)
                {
                    return Result.Failure("warranty.resolved_quantity_mismatch", "Resolved quantity must equal selected original physical quantity.");
                }
                if (groups.Any(x => x.Send is { } send && x.Quantity > send.BaseQuantity - resolutions.Where(r => r.SendAllocationId == send.Id).Sum(r => r.ResolvedBaseQuantity)))
                {
                    return Result.Failure("warranty.resolution_quantity_exceeds_remaining", "Selected quantity exceeds original source remaining quantity.");
                }
            }
            else
            {
                resolvedQuantity = command.ResolvedQuantity is decimal quantity ? QuantityMath.RoundQuantity(quantity) : remaining;
                if (sends.Count == 1)
                {
                    groups.Add(new ResolutionSource(sends[0]) { Quantity = resolvedQuantity });
                }
            }
            if (resolvedQuantity <= 0m)
            {
                return Result.Failure("validation.resolved_quantity_invalid", "Resolved quantity must be greater than zero.");
            }
            if (remaining <= 0m || resolvedQuantity > remaining)
            {
                return Result.Failure("warranty.resolution_quantity_exceeds_remaining", "Resolution quantity exceeds remaining unresolved quantity.");
            }
            var sendAllocation = exact ? null : sends.SingleOrDefault();


            if (command.Resolution == WarrantyResolutionType.Replaced &&
                product.TrackingMode is TrackingMode.Serialized or TrackingMode.IndividualPiece or TrackingMode.Container)
            {
                var replacementResult = await ReceiveSerializedReplacementAsync(
                    warrantyCase,
                    product,
                    command,
                    groups,
                    resolvedQuantity,
                    ct);

                if (!replacementResult.IsSuccess)
                {
                    return replacementResult;
                }
            }
            else if (command.Resolution == WarrantyResolutionType.Credited)
            {
                var creditResult = await ResolveWarrantyCreditAsync(
                    warrantyCase,
                    product,
                    command,
                    groups,
                    resolvedQuantity,
                    ct);

                if (!creditResult.IsSuccess)
                {
                    return creditResult;
                }
            }
            else
            {
                var destination = command.Resolution switch
                {
                    WarrantyResolutionType.Repaired => InventoryBucket.Sellable,
                    WarrantyResolutionType.Replaced => InventoryBucket.Sellable,
                    WarrantyResolutionType.Rejected => InventoryBucket.Defective,
                    WarrantyResolutionType.Scrapped => InventoryBucket.Scrap,
                    _ => throw new BusinessRuleException(
                        "warranty.shop_resolution_invalid",
                        "Unsupported shop warranty resolution.")
                };

                var movementType = command.Resolution switch
                {
                    WarrantyResolutionType.Replaced =>
                        InventoryMovementType.ReceiveReplacementFromSupplier,
                    WarrantyResolutionType.Rejected =>
                        InventoryMovementType.WarrantyRejectedReturn,
                    WarrantyResolutionType.Scrapped =>
                        InventoryMovementType.WriteOffToScrap,
                    _ => InventoryMovementType.ReceiveRepairedFromSupplier
                };

                decimal? mwaBeforeScrap = null;
                if (command.Resolution == WarrantyResolutionType.Scrapped && !exact)
                {
                    var costState = await _inventory.GetCostStateForUpdateAsync(product.Id, ct);
                    mwaBeforeScrap = costState is not null && costState.CostedQty > 0
                        ? Cost(costState.TotalInventoryCost / costState.CostedQty)
                        : 0m;
                }

                var transfer = await _conditions.TransferAsync(
                    new TransferInventoryConditionCommand(
                        product.Id,
                        InventoryBucket.WithSupplier,
                        destination,
                        resolvedQuantity,
                        command.ActorId,
                        $"SUPPLIER_WARRANTY_{command.Resolution.ToString().ToUpperInvariant()}",
                        command.Note,
                        "SHOP_WARRANTY",
                        warrantyCase.Id,
                        command.OriginalInventoryUnitIds,
                        movementType,
                        TargetLotId: sendAllocation?.OriginalInventoryLotId,
                        CorrelationId: command.ClientOperationId),
                    ct);

                if (!transfer.IsSuccess)
                {
                    return Result.Failure(transfer.Error!.Code, transfer.Error.Message);
                }

                foreach (var group in groups)
                {
                    var actualCarrying = command.Resolution == WarrantyResolutionType.Scrapped
                        ? (exact ? group.ExactCarrying : Cost((mwaBeforeScrap ?? 0m) * group.Quantity)) : 0m;
                    if (command.Resolution == WarrantyResolutionType.Scrapped)
                    {
                        warrantyCase.InventoryCarryingCostResolved = (warrantyCase.InventoryCarryingCostResolved ?? 0m) + actualCarrying;
                        warrantyCase.RecoveryDifference = (warrantyCase.RecoveryDifference ?? 0m) - actualCarrying;
                    }
                    if (group.Send is null)
                    {
                        continue;
                    }
                    _warranty.AddShopWarrantyResolutionAllocation(new ShopWarrantyResolutionAllocation
                    {
                        SendAllocationId = group.Send.Id,
                        ResolutionMovementId = transfer.Value,
                        ResolvedBaseQuantity = group.Quantity,
                        ResolutionOutcome = command.Resolution,
                        // The legacy-named column requires a forensic per-base ratio for both valuation modes.
                        ResolutionTimeMwaUnitCostSnapshot = command.Resolution == WarrantyResolutionType.Scrapped
                            ? (exact ? Cost(actualCarrying / group.Quantity) : mwaBeforeScrap) : null,
                        ActualResolvedCarryingValue = actualCarrying,
                        ClientOperationId = command.ClientOperationId,
                        ActorId = command.ActorId,
                        OccurredAt = _clock.UtcNow
                    });
                }
            }

            var now = _clock.UtcNow;
            var remainingAfter = QuantityMath.RoundQuantity(remaining - resolvedQuantity);
            warrantyCase.ResolutionType = command.Resolution;
            warrantyCase.SupplierReference = NormalizeNullable(command.SupplierReference)
                ?? warrantyCase.SupplierReference;
            warrantyCase.ReceivedAt = now;
            warrantyCase.Version++;

            if (remainingAfter == 0m)
            {
                warrantyCase.ClosedAt = now;
                warrantyCase.Status = command.Resolution == WarrantyResolutionType.Scrapped
                    ? ShopWarrantyCaseStatus.WrittenOff
                    : ShopWarrantyCaseStatus.Closed;
                warrantyCase.ResolutionClientOperationId = command.ClientOperationId;
            }
            else
            {
                warrantyCase.Status = ShopWarrantyCaseStatus.WithSupplier;
            }

            _warranty.AddOperation(new WarrantyOperation
            {
                ClientOperationId = command.ClientOperationId,
                TargetType = "SHOP_STOCK",
                TargetId = warrantyCase.Id,
                OperationType = operationType,
                ActorId = command.ActorId,
                PayloadHash = payloadHash,
                ResultId = warrantyCase.Id,
                OccurredAt = now
            });

            _audit.Record(
                command.Resolution == WarrantyResolutionType.Credited
                    ? "SHOP_WARRANTY_CREDITED"
                    : "SHOP_WARRANTY_RESOLVED",
                "SHOP_WARRANTY",
                warrantyCase.Id,
                command.ActorId,
                command.ClientOperationId,
                $"Case {warrantyCase.CaseNumber}; resolution={command.Resolution}; supplier={warrantyCase.SupplierId:D}; qty={resolvedQuantity}; remaining={remainingAfter}.");

            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }, cancellationToken);
    }

    private async Task<Result> ReceiveSerializedReplacementAsync(
        ShopStockWarrantyCase warrantyCase,
        Product product,
        ReceiveShopStockWarrantyCommand command,
        IReadOnlyList<ResolutionSource> groups,
        decimal resolvedQuantity,
        CancellationToken cancellationToken)
    {
        var quantity = resolvedQuantity;
        if (!QuantityMath.IsWhole(quantity) ||
            command.OriginalInventoryUnitIds is null ||
            command.OriginalInventoryUnitIds.Count == 0 ||
            (product.TrackingMode != TrackingMode.Container && command.OriginalInventoryUnitIds.Count != quantity) ||
            command.OriginalInventoryUnitIds.Distinct().Count() != command.OriginalInventoryUnitIds.Count ||
            command.ReplacementUnits is null ||
            command.ReplacementUnits.Count != command.OriginalInventoryUnitIds.Count)
        {
            return Result.Failure(
                "warranty.serialized_replacement_count",
                "Replacement requires one distinct old and one new identity for every serialized unit.");
        }

        if (string.IsNullOrWhiteSpace(product.Sku))
        {
            return Result.Failure(
                "warranty.replacement_sku_required",
                "Serialized replacement requires a permanent Product SKU.");
        }

        var supplier = await _parties.GetSupplierAsync(warrantyCase.SupplierId, cancellationToken);
        if (supplier is null || !supplier.IsActive || string.IsNullOrWhiteSpace(supplier.DealerCode))
        {
            return Result.Failure(
                "warranty.replacement_supplier_identity",
                "Active Supplier with permanent DealerCode is required for replacement.");
        }

        var supplierProduct = await _traceability.GetSupplierProductForUpdateAsync(
            warrantyCase.SupplierId,
            product.Id,
            cancellationToken);
        if (supplierProduct is null || !supplierProduct.IsActive)
        {
            return Result.Failure(
                "warranty.supplier_product_missing",
                "Active SupplierProduct sequence authority is required for replacement.");
        }

        var oldIds = command.OriginalInventoryUnitIds.OrderBy(x => x).ToArray();
        var oldUnits = await _inventory.GetInventoryUnitsForUpdateAsync(
            product.Id,
            oldIds,
            cancellationToken);

        if (oldUnits.Count != oldIds.Length ||
            oldUnits.Any(x => x.Status != InventoryUnitStatus.WithSupplier ||
                              x.InventoryLotId is null))
        {
            return Result.Failure(
                "warranty.serialized_unit_state",
                "Original serialized units are not all recoverable WITH_SUPPLIER units.");
        }

        foreach (var oldUnit in oldUnits)
        {
            if (oldUnit.SupplierProductId is not Guid sourceSupplierProductId)
            {
                return Result.Failure(
                    "warranty.replacement_supplier_provenance_missing",
                    "Original unit SupplierProduct provenance is missing.");
            }

            var sourceSupplierProduct = await _traceability.GetSupplierProductByIdForUpdateAsync(
                sourceSupplierProductId,
                cancellationToken);
            if (sourceSupplierProduct is null ||
                sourceSupplierProduct.SupplierId != warrantyCase.SupplierId)
            {
                return Result.Failure(
                    "warranty.replacement_supplier_provenance_mismatch",
                    "Original unit Supplier does not match the warranty case Supplier.");
            }
        }
        if (product.TrackingMode == TrackingMode.Container)
        {
            decimal originalQuantity = 0m;
            foreach (var oldUnit in oldUnits)
                originalQuantity += await _inventory.GetPhysicalUnitBaseQuantitySnapshotAsync(oldUnit, cancellationToken);
            if (originalQuantity != quantity)
                return Result.Failure("warranty.container_quantity_mismatch",
                    "Replacement quantity must match the original physical pack quantities.");
        }

        var seenSerials = new HashSet<string>(StringComparer.Ordinal);
        var seenImeis = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in command.ReplacementUnits)
        {
            var serial = NormalizeSerial(input.SerialNumber);
            var imei1 = NormalizeImei(input.Imei1);
            var imei2 = NormalizeImei(input.Imei2);

            if (product.SerialTrackingEnabled && serial is null)
            {
                return Result.Failure(
                    "warranty.replacement_serial_required",
                    "Serial number is required for the replacement unit.");
            }

            if (product.ImeiTrackingEnabled && imei1 is null)
            {
                return Result.Failure(
                    "warranty.replacement_imei_required",
                    "IMEI1 is required for the replacement unit.");
            }

            if (serial is not null && !seenSerials.Add(serial))
            {
                return Result.Failure(
                    "warranty.replacement_serial_duplicate",
                    $"Duplicate replacement serial '{serial}'.");
            }

            foreach (var imei in new[] { imei1, imei2 }.Where(x => x is not null))
            {
                if (!seenImeis.Add(imei!))
                {
                    return Result.Failure(
                        "warranty.replacement_imei_duplicate",
                        $"Duplicate replacement IMEI '{imei}'.");
                }
            }

            if (await _inventory.InventoryIdentityExistsAsync(serial, imei1, imei2, cancellationToken))
            {
                return Result.Failure(
                    "warranty.replacement_identity_exists",
                    "Replacement Serial/IMEI already exists in inventory history.");
            }
        }

        var balance = await _inventory.GetStockBalanceForUpdateAsync(
            product.Id,
            cancellationToken);
        if (balance is null || balance.WithSupplierQty < quantity)
        {
            return Result.Failure(
                "inventory.insufficient_with_supplier",
                "WITH_SUPPLIER inventory is insufficient for replacement receipt.");
        }

        var beforeWithSupplier = balance.WithSupplierQty;
        var beforeSellable = balance.SellableQty;
        balance.Transfer(InventoryBucket.WithSupplier, InventoryBucket.Sellable, quantity);
        var lotTransfer = await ExactUnitLotTransfer.TransferAsync(_inventory, oldUnits,
            InventoryBucket.WithSupplier, InventoryBucket.Sellable, cancellationToken);
        if (!lotTransfer.IsSuccess)
            return lotTransfer;

        var movement = new InventoryMovement
        {
            ProductId = product.Id,
            MovementType = InventoryMovementType.ReceiveReplacementFromSupplier,
            ReferenceType = "SHOP_WARRANTY",
            ReferenceId = warrantyCase.Id,
            ActorId = command.ActorId,
            OccurredAt = _clock.UtcNow,
            CorrelationId = command.ClientOperationId,
            Reason = "SUPPLIER_WARRANTY_REPLACEMENT",
            Note = command.Note?.Trim()
        };

        _inventory.AddMovement(movement);
        _inventory.AddMovementEffect(new InventoryMovementEffect
        {
            MovementId = movement.Id,
            StockBucket = InventoryBucket.WithSupplier,
            QuantityDelta = -quantity,
            QuantityBefore = beforeWithSupplier,
            QuantityAfter = balance.WithSupplierQty
        });
        _inventory.AddMovementEffect(new InventoryMovementEffect
        {
            MovementId = movement.Id,
            StockBucket = InventoryBucket.Sellable,
            QuantityDelta = quantity,
            QuantityBefore = beforeSellable,
            QuantityAfter = balance.SellableQty
        });

        if (_physicalUnits is null)
        {
            return Result.Failure(
                "inventory.physical_unit_authority_unavailable",
                "Physical-unit creation authority is unavailable.");
        }

        var orderedOldUnits = oldUnits.OrderBy(x => x.Id).ToArray();
        var entries = orderedOldUnits.Select((oldUnit, index) =>
        {
            var input = command.ReplacementUnits[index];
            return new PhysicalUnitCreationEntry(
                input.SerialNumber,
                input.Imei1,
                input.Imei2,
                InventoryUnitStatus.InStock,
                oldUnit.AcquisitionCost,
                oldUnit.InventoryLotId,
                InventoryUnitOriginType.WarrantyReplacement,
                SourceWarrantyCaseId: warrantyCase.Id);
        }).ToArray();
        var creation = await _physicalUnits.CreateAsync(
            warrantyCase.SupplierId,
            product.Id,
            entries,
            cancellationToken);
        if (!creation.IsSuccess || creation.Value is null)
        {
            return Result.Failure(creation.Error!.Code, creation.Error.Message);
        }

        for (var index = 0; index < orderedOldUnits.Length; index++)
        {
            var oldUnit = orderedOldUnits[index];
            var from = oldUnit.Status;
            oldUnit.Status = InventoryUnitStatus.SupplierReturned;
            oldUnit.Version++;
            _inventory.AddMovementUnit(new InventoryMovementUnit
            {
                MovementId = movement.Id,
                InventoryUnitId = oldUnit.Id,
                FromStatus = from,
                ToStatus = oldUnit.Status
            });

            var replacement = creation.Value[index];
            _inventory.AddMovementUnit(new InventoryMovementUnit
            {
                MovementId = movement.Id,
                InventoryUnitId = replacement.Id,
                FromStatus = null,
                ToStatus = InventoryUnitStatus.InStock
            });
        }

        foreach (var group in groups)
        {
            if (group.Send is null)
            {
                continue;
            }
            _warranty.AddShopWarrantyResolutionAllocation(new ShopWarrantyResolutionAllocation
            {
                SendAllocationId = group.Send.Id,
                ResolutionMovementId = movement.Id,
                ResolvedBaseQuantity = group.Quantity,
                ResolutionOutcome = WarrantyResolutionType.Replaced,
                ActualResolvedCarryingValue = 0m,
                ReplacementInventoryLotId = group.SourceLotId,
                ClientOperationId = command.ClientOperationId,
                ActorId = command.ActorId,
                OccurredAt = _clock.UtcNow
            });
        }

        return Result.Success();
    }

    private async Task<Result> ResolveWarrantyCreditAsync(
        ShopStockWarrantyCase warrantyCase,
        Product product,
        ReceiveShopStockWarrantyCommand command,
        IReadOnlyList<ResolutionSource> groups,
        decimal resolvedQuantity,
        CancellationToken cancellationToken)
    {
        var sendAllocation = groups.Count == 1 ? groups[0].Send : null;
        var exact = product.TrackingMode is TrackingMode.Serialized or TrackingMode.IndividualPiece or TrackingMode.Container;
        var carryingBySource = new Dictionary<ResolutionSource, decimal>();
        var operationId = command.ClientOperationId;
        var supplierCredit = Money(command.SupplierCreditAmount!.Value);
        var quantity = resolvedQuantity;

        var balance = await _inventory.GetStockBalanceForUpdateAsync(product.Id, cancellationToken);
        if (balance is null || balance.WithSupplierQty < quantity)
        {
            return Result.Failure(
                "inventory.insufficient_with_supplier",
                "WITH_SUPPLIER inventory is insufficient for warranty credit resolution.");
        }

        var movement = new InventoryMovement
        {
            ProductId = product.Id,
            MovementType = InventoryMovementType.WarrantyCreditResolution,
            ReferenceType = "SHOP_WARRANTY",
            ReferenceId = warrantyCase.Id,
            ActorId = command.ActorId,
            OccurredAt = _clock.UtcNow,
            CorrelationId = operationId,
            Reason = "WARRANTY_CREDIT",
            Note = command.Note?.Trim()
        };
        _inventory.AddMovement(movement);

        var before = balance.WithSupplierQty;
        decimal carryingCostRemoved;

        if (product.TrackingMode is TrackingMode.Serialized or TrackingMode.IndividualPiece or TrackingMode.Container)
        {
            if (!QuantityMath.IsWhole(quantity) ||
                command.OriginalInventoryUnitIds is null ||
                command.OriginalInventoryUnitIds.Count == 0 ||
                (product.TrackingMode != TrackingMode.Container && command.OriginalInventoryUnitIds.Count != quantity) ||
                command.OriginalInventoryUnitIds.Distinct().Count() != command.OriginalInventoryUnitIds.Count)
            {
                return Result.Failure(
                    "warranty.credit_serialized_units_required",
                    "Warranty credit requires one distinct WITH_SUPPLIER InventoryUnit per serialized base unit.");
            }

            var units = await _inventory.GetInventoryUnitsForUpdateAsync(
                product.Id,
                command.OriginalInventoryUnitIds,
                cancellationToken);
            if (units.Count != command.OriginalInventoryUnitIds.Count ||
                units.Any(x => x.Status != InventoryUnitStatus.WithSupplier ||
                               x.InventoryLotId is null))
            {
                return Result.Failure(
                    "warranty.credit_unit_state",
                    "One or more units are not eligible WITH_SUPPLIER inventory.");
            }

            carryingCostRemoved = 0m;
            var quantities = new Dictionary<Guid, decimal>();
            foreach (var unit in units)
            {
                quantities[unit.Id] = await _inventory.GetPhysicalUnitBaseQuantitySnapshotAsync(unit, cancellationToken);
            }
            if (quantities.Values.Sum() != quantity)
            {
                return Result.Failure("warranty.container_quantity_mismatch",
                    "Credit quantity must match the original physical quantities.");
            }
            foreach (var unit in units.OrderBy(x => x.Id))
            {
                var basePerUnit = quantities[unit.Id];
                var lotBalance = await _inventory.GetLotBucketBalanceForUpdateAsync(
                    unit.InventoryLotId!.Value,
                    InventoryBucket.WithSupplier,
                    cancellationToken);
                if (lotBalance is null || lotBalance.Quantity < basePerUnit)
                {
                    return Result.Failure(
                        "warranty.credit_lot_insufficient",
                        "A warranty unit no longer has recoverable WITH_SUPPLIER lot quantity.");
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

                var actualRemoved = await _costAllocator.RemoveCarryingValueAsync(
                    product.Id, basePerUnit, unit.AcquisitionCost / basePerUnit, cancellationToken);
                carryingCostRemoved += actualRemoved;
                var source = groups.Single(x => x.UnitIds.Contains(unit.Id));
                carryingBySource[source] = carryingBySource.GetValueOrDefault(source) + actualRemoved;

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
        }
        else
        {
            if (command.OriginalInventoryUnitIds is { Count: > 0 })
            {
                return Result.Failure(
                    "warranty.credit_units_not_allowed",
                    "Quantity/length warranty credit must not submit exact InventoryUnit identities.");
            }

            var costState = await _inventory.GetCostStateForUpdateAsync(product.Id, cancellationToken);
            var currentMwa = costState is not null && costState.CostedQty > 0
                ? Cost(costState.TotalInventoryCost / costState.CostedQty)
                : 0m;

            if (sendAllocation is not null)
            {
                var lotBalance = await _inventory.GetLotBucketBalanceForUpdateAsync(
                    sendAllocation.OriginalInventoryLotId,
                    InventoryBucket.WithSupplier,
                    cancellationToken);
                if (lotBalance is null || lotBalance.Quantity < quantity)
                {
                    return Result.Failure(
                        "warranty.credit_lot_insufficient",
                        "A warranty unit no longer has recoverable WITH_SUPPLIER lot quantity.");
                }

                lotBalance.Quantity = QuantityMath.RoundQuantity(lotBalance.Quantity - quantity);
                _inventory.AddLotConsumption(new InventoryLotConsumption
                {
                    LotId = sendAllocation.OriginalInventoryLotId,
                    MovementId = movement.Id,
                    Quantity = quantity,
                    UnitCostSnapshot = currentMwa,
                    TotalCostSnapshot = decimal.Round(currentMwa * quantity, 6, MidpointRounding.AwayFromZero),
                    OccurredAt = _clock.UtcNow
                });
            }
            else
            {
                await _costAllocator.ConsumeBucketAsync(
                    product.Id,
                    InventoryBucket.WithSupplier,
                    quantity,
                    movement.Id,
                    currentMwa,
                    cancellationToken);
            }

            carryingCostRemoved = await _costAllocator.RemoveCarryingValueAsync(
                product.Id,
                quantity,
                null,
                cancellationToken);
        }

        balance.ApplyDelta(InventoryBucket.WithSupplier, -quantity);

        carryingCostRemoved = Cost(carryingCostRemoved);
        var recoveryDifference = Cost(supplierCredit - carryingCostRemoved);
        movement.UnitCostSnapshot = quantity == 0m
            ? null
            : Cost(carryingCostRemoved / quantity);
        movement.RecognizedLossAmount = recoveryDifference < 0m
            ? Money(-recoveryDifference)
            : 0m;

        _inventory.AddMovementEffect(new InventoryMovementEffect
        {
            MovementId = movement.Id,
            StockBucket = InventoryBucket.WithSupplier,
            QuantityDelta = -quantity,
            QuantityBefore = before,
            QuantityAfter = balance.WithSupplierQty
        });

        var accountEntry = new SupplierAccountEntry
        {
            EntryNumber = await _numbers.NextAsync("SAE", cancellationToken),
            SupplierId = warrantyCase.SupplierId,
            EntryType = SupplierAccountEntryType.WarrantyCredit,
            Direction = SupplierAccountDirection.DecreasePayable,
            Amount = supplierCredit,
            // The real immutable resolution movement is the resolution source identity for this credit.
            ReferenceType = "WarrantyResolution",
            ReferenceId = movement.Id,
            OccurredAt = _clock.UtcNow,
            ActorId = command.ActorId,
            ClientOperationId = operationId,
            Note = command.Note?.Trim(),
            CreatedAt = _clock.UtcNow
        };
        accountEntry.ValidateDirection();
        _supplierAccounts.AddEntry(accountEntry);

        warrantyCase.InventoryCarryingCostResolved = (warrantyCase.InventoryCarryingCostResolved ?? 0m) + carryingCostRemoved;
        warrantyCase.SupplierCreditAmount = (warrantyCase.SupplierCreditAmount ?? 0m) + supplierCredit;
        warrantyCase.RecoveryDifference = (warrantyCase.RecoveryDifference ?? 0m) + recoveryDifference;

        decimal cumulativeQuantity = 0m;
        decimal allocatedCredit = 0m;
        foreach (var group in groups)
        {
            cumulativeQuantity += group.Quantity;
            var cumulativeCredit = cumulativeQuantity == quantity ? supplierCredit : Money(supplierCredit * cumulativeQuantity / quantity);
            var groupCredit = cumulativeCredit - allocatedCredit;
            allocatedCredit = cumulativeCredit;
            var groupCarrying = exact ? Cost(carryingBySource[group]) : carryingCostRemoved;
            if (group.Send is null)
            {
                continue;
            }
            _warranty.AddShopWarrantyResolutionAllocation(new ShopWarrantyResolutionAllocation
            {
                SendAllocationId = group.Send.Id,
                ResolutionMovementId = movement.Id,
                ResolvedBaseQuantity = group.Quantity,
                ResolutionOutcome = WarrantyResolutionType.Credited,
                // For exact units this is the actual carrying ratio, not product MWA valuation.
                ResolutionTimeMwaUnitCostSnapshot = Cost(groupCarrying / group.Quantity),
                ActualResolvedCarryingValue = groupCarrying,
                SupplierCreditAmount = groupCredit,
                ClientOperationId = command.ClientOperationId,
                ActorId = command.ActorId,
                OccurredAt = _clock.UtcNow
            });
        }

        return Result.Success();
    }

    private async Task<Result<decimal>> ValidateLegacyExactGraphAsync(
        ShopStockWarrantyCase warrantyCase,
        Product product,
        IReadOnlyList<InventoryMovementEvidence> evidence,
        CancellationToken cancellationToken)
    {
        var sends = evidence.Where(x => x.Movement.MovementType == InventoryMovementType.SendToSupplierWarranty).ToArray();
        var resolutions = evidence.Where(x => IsShopResolutionMovement(x.Movement.MovementType)).ToArray();
        var sentLinks = sends.SelectMany(x => x.Units).ToArray();
        var resolvedLinks = resolutions.SelectMany(x => x.Units)
            .Where(x => x.FromStatus == InventoryUnitStatus.WithSupplier).ToArray();
        var sentIds = sentLinks.Select(x => x.InventoryUnitId).ToHashSet();
        var resolvedIds = resolvedLinks.Select(x => x.InventoryUnitId).ToHashSet();
        if (sends.Length == 0 || sentLinks.Length == 0 || sentLinks.Length != sentIds.Count ||
            resolvedLinks.Length != resolvedIds.Count || !resolvedIds.IsSubsetOf(sentIds) ||
            evidence.Any(x => x.Movement.ProductId != product.Id ||
                x.Movement.ReferenceType != "SHOP_WARRANTY" || x.Movement.ReferenceId != warrantyCase.Id) ||
            sends.Any(x => x.Units.Any(u => u.MovementId != x.Movement.Id ||
                u.ToStatus != InventoryUnitStatus.WithSupplier ||
                u.FromStatus is not (InventoryUnitStatus.Damaged or InventoryUnitStatus.Defective))))
        {
            return Result<decimal>.Failure("warranty.source_evidence_invalid", "Legacy exact warranty requires a complete unambiguous immutable send graph.");
        }

        var originals = await _inventory.GetInventoryUnitsForUpdateAsync(product.Id, sentIds.ToArray(), cancellationToken);
        if (originals.Count != sentIds.Count || originals.Any(x => x.ProductId != product.Id ||
            x.InventoryLotId is null ||
            (!resolvedIds.Contains(x.Id) && x.Status != InventoryUnitStatus.WithSupplier)))
        {
            return Result<decimal>.Failure("warranty.source_evidence_invalid", "Legacy original identities lack source provenance or unresolved custody.");
        }

        var quantities = new Dictionary<Guid, decimal>();
        foreach (var original in originals.OrderBy(x => x.Id))
        {
            var lot = await _inventory.GetInventoryLotForUpdateAsync(original.InventoryLotId!.Value, cancellationToken);
            var supplierProduct = original.SupplierProductId is Guid supplierProductId
                ? await _traceability.GetSupplierProductByIdForUpdateAsync(supplierProductId, cancellationToken)
                : null;
            var quantity = await _inventory.GetPhysicalUnitBaseQuantitySnapshotAsync(original, cancellationToken);
            if (lot is null || lot.ProductId != product.Id ||
                (original.SourcePurchaseItemId is Guid sourcePurchaseItemId && lot.PurchaseItemId != sourcePurchaseItemId) ||
                supplierProduct is null || supplierProduct.ProductId != product.Id || supplierProduct.SupplierId != warrantyCase.SupplierId ||
                quantity <= 0m || (product.TrackingMode != TrackingMode.Container && quantity != 1m))
            {
                return Result<decimal>.Failure("warranty.source_evidence_invalid", "Legacy original physical source evidence does not match the case.");
            }
            quantities.Add(original.Id, quantity);
        }

        if (quantities.Values.Sum() != warrantyCase.BaseQuantity ||
            sends.Any(x => x.Effects.Where(e => e.MovementId == x.Movement.Id && e.StockBucket == InventoryBucket.WithSupplier)
                .Sum(e => e.QuantityDelta) != x.Units.Sum(u => quantities[u.InventoryUnitId])))
        {
            return Result<decimal>.Failure("warranty.source_evidence_invalid", "Legacy send physical quantities and immutable effects do not cover the case.");
        }

        foreach (var resolution in resolutions)
        {
            var expectedStatus = resolution.Movement.MovementType switch
            {
                InventoryMovementType.ReceiveRepairedFromSupplier => InventoryUnitStatus.InStock,
                InventoryMovementType.WarrantyRejectedReturn => InventoryUnitStatus.Defective,
                InventoryMovementType.WriteOffToScrap => InventoryUnitStatus.Scrapped,
                _ => InventoryUnitStatus.SupplierReturned
            };
            var originalLinks = resolution.Units.Where(x => x.FromStatus == InventoryUnitStatus.WithSupplier).ToArray();
            if (originalLinks.Length == 0 || resolution.Units.Any(x => x.MovementId != resolution.Movement.Id ||
                (x.FromStatus == InventoryUnitStatus.WithSupplier
                    ? x.ToStatus != expectedStatus
                    : resolution.Movement.MovementType != InventoryMovementType.ReceiveReplacementFromSupplier ||
                      x.FromStatus is not null || x.ToStatus != InventoryUnitStatus.InStock)) ||
                resolution.Effects.Where(x => x.MovementId == resolution.Movement.Id && x.StockBucket == InventoryBucket.WithSupplier)
                    .Sum(x => x.QuantityDelta) != -originalLinks.Sum(x => quantities[x.InventoryUnitId]))
            {
                return Result<decimal>.Failure("warranty.source_evidence_invalid", "Legacy resolution original identities and effects are inconsistent.");
            }
        }

        var remaining = QuantityMath.RoundQuantity(warrantyCase.BaseQuantity - resolvedIds.Sum(x => quantities[x]));
        if (remaining <= 0m)
        {
            return Result<decimal>.Failure("warranty.resolution_quantity_exceeds_remaining", "Legacy warranty case has no unresolved physical quantity.");
        }
        return Result<decimal>.Success(remaining);
    }

    private static bool IsShopResolutionMovement(InventoryMovementType movementType) =>
        movementType is InventoryMovementType.ReceiveRepairedFromSupplier or
            InventoryMovementType.ReceiveReplacementFromSupplier or InventoryMovementType.WarrantyRejectedReturn or
            InventoryMovementType.WriteOffToScrap or InventoryMovementType.WarrantyCreditResolution;

    private sealed class ResolutionSource
    {
        public ResolutionSource(ShopWarrantySendAllocation send)
            : this(send, send.OriginalInventoryLotId, send.SendMovementId)
        {
        }

        public ResolutionSource(ShopWarrantySendAllocation? send, Guid sourceLotId, Guid sendMovementId)
        {
            Send = send;
            SourceLotId = sourceLotId;
            SendMovementId = sendMovementId;
        }

        public ShopWarrantySendAllocation? Send { get; }
        public Guid SourceLotId { get; }
        public Guid SendMovementId { get; }
        public decimal Quantity { get; set; }
        public decimal ExactCarrying { get; set; }
        public HashSet<Guid> UnitIds { get; } = new();
    }

    private static decimal Money(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    private static decimal Cost(decimal value) =>
        decimal.Round(value, 6, MidpointRounding.AwayFromZero);

    private static string? NormalizeSerial(string? value) =>
        IdentityNormalizationRules.NormalizeOptionalSerialNumber(value);

    private static string? NormalizeImei(string? value) =>
        IdentityNormalizationRules.NormalizeOptionalImei(value);

    private static string? NormalizeNullable(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
