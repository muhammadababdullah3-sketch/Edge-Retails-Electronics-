using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using System.Security.Cryptography;
using System.Text;
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
}

public sealed record WarrantyClaimUnitInput(
    Guid? OriginalInventoryUnitId,
    string? OriginalIdentitySnapshot);

public sealed record WarrantyClaimItemInput(
    Guid ProductId,
    decimal Quantity,
    string FaultDescription,
    Guid? OriginalSaleItemId,
    DateOnly? WarrantyValidUntil,
    IReadOnlyList<WarrantyClaimUnitInput>? Units);

public sealed record CreateWarrantyClaimCommand(
    Guid CustomerId,
    Guid? OriginalSaleId,
    Guid? SupplierId,
    Guid ActorId,
    IReadOnlyList<WarrantyClaimItemInput> Items,
    Guid ClientOperationId);

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
        IOperationLock? operationLock = null)
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
    }

    public Task<Result<Guid>> HandleAsync(
        CreateWarrantyClaimCommand command,
        CancellationToken cancellationToken)
    {
        if (command.CustomerId == Guid.Empty || command.OriginalSaleId is null ||
            command.OriginalSaleId == Guid.Empty || command.Items.Count == 0)
        {
            return Task.FromResult(Result<Guid>.Failure(
                "warranty.sale_customer_items_required",
                "Customer, original Sale, and at least one warranty item are required."));
        }

        if (command.ClientOperationId == Guid.Empty)
        {
            return Task.FromResult(Result<Guid>.Failure(
                "validation.client_operation_id_required",
                "ClientOperationId is required for Customer Warranty Claim creation."));
        }

        if (command.Items.Any(x => x.OriginalSaleItemId is null || x.OriginalSaleItemId == Guid.Empty) ||
            command.Items.GroupBy(x => x.OriginalSaleItemId).Any(g => g.Count() > 1))
        {
            return Task.FromResult(Result<Guid>.Failure(
                "warranty.sale_item_required",
                "Every warranty line requires one distinct original SaleItem."));
        }

        return _transactions.ExecuteAsync(async ct =>
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
                return Result<Guid>.Success(existingByOperation.Id);
            }

            var customer = await _parties.GetCustomerAsync(command.CustomerId, ct);
            if (customer is null)
            {
                return Result<Guid>.Failure(
                    "warranty.customer_not_found",
                    "Warranty customer was not found.");
            }

            foreach (var saleItemId in command.Items
                .Select(x => x.OriginalSaleItemId!.Value)
                .OrderBy(x => x))
            {
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
                await _resourceLock.AcquireAsync("warranty-unit", unitId, ct);
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

                if (product.TrackingMode == TrackingMode.Serialized)
                {
                    if (!QuantityMath.IsWhole(input.Quantity))
                    {
                        return Result<Guid>.Failure(
                            "warranty.serialized_quantity_whole",
                            "Serialized warranty quantity must be an exact whole number.");
                    }

                    quantity = input.Quantity;
                    if (input.Units is null || input.Units.Count != decimal.ToInt32(quantity) ||
                        input.Units.Any(x => x.OriginalInventoryUnitId is null))
                    {
                        return Result<Guid>.Failure(
                            "warranty.serialized_identity_count",
                            "Capture one exact original InventoryUnit for every serialized warranty item.");
                    }

                    var ids = input.Units.Select(x => x.OriginalInventoryUnitId!.Value).ToArray();
                    var soldLinks = await _sales.GetSaleItemUnitsAsync(saleItem.Id, ct);
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

                        if (unit.SupplierProductId is null || unit.SourcePurchaseItemId is null)
                        {
                            return Result<Guid>.Failure(
                                "warranty.supplier_provenance_missing",
                                "Original Supplier provenance is missing for a tracked unit.");
                        }

                        var supplierProduct = await _traceability.GetSupplierProductByIdForUpdateAsync(
                            unit.SupplierProductId.Value,
                            ct);
                        var sourceItem = await _purchases.GetPurchaseItemForUpdateAsync(
                            unit.SourcePurchaseItemId.Value,
                            ct);
                        if (supplierProduct is null || sourceItem is null)
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

                    var supplierCapacities = await ResolveSaleItemSupplierCapacitiesAsync(saleItem, ct);
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

                if (product.TrackingMode == TrackingMode.Serialized)
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

            await _unitOfWork.SaveChangesAsync(ct);
            return Result<Guid>.Success(claim.Id);
        }, cancellationToken);
    }

    private async Task<IReadOnlyDictionary<Guid, decimal>> ResolveSaleItemSupplierCapacitiesAsync(
        EdgeRetails.Domain.Sales.SaleItem saleItem,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, decimal>();
        var consumptions = await _inventory.GetMovementLotConsumptionsAsync(
            saleItem.InventoryMovementId,
            cancellationToken);

        foreach (var consumption in consumptions)
        {
            var lot = await _inventory.GetInventoryLotForUpdateAsync(
                consumption.LotId,
                cancellationToken);
            if (lot?.PurchaseItemId is not Guid purchaseItemId)
            {
                continue;
            }

            var purchaseItem = await _purchases.GetPurchaseItemForUpdateAsync(
                purchaseItemId,
                cancellationToken);
            if (purchaseItem is null)
            {
                continue;
            }

            var purchase = await _purchases.GetPurchaseForUpdateAsync(
                purchaseItem.PurchaseId,
                cancellationToken);
            if (purchase is null)
            {
                continue;
            }

            result[purchase.SupplierId] = QuantityMath.RoundQuantity(
                result.GetValueOrDefault(purchase.SupplierId) + consumption.Quantity);
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
        IUnitOfWork unitOfWork)
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
            var replacementPayload = string.Join(";", command.Units
                .OrderBy(x => x.ClaimItemUnitId)
                .Select(x => $"{x.ClaimItemUnitId:D}|{WarrantyOperationIdentity.Normalize(x.SerialNumber)}|{WarrantyOperationIdentity.Normalize(x.Imei1)}|{WarrantyOperationIdentity.Normalize(x.Imei2)}"));
            var payloadHash = WarrantyOperationIdentity.Hash(
                "CUSTOMER_CLAIM",
                command.ClaimId.ToString("D"),
                "CUSTOMER_REPLACEMENT",
                replacementPayload,
                WarrantyOperationIdentity.Normalize(command.Note));
            var existingOperation = await _warranty.GetOperationByClientOperationIdAsync(command.ClientOperationId, ct);
            if (existingOperation is not null)
            {
                if (existingOperation.OperationType != WarrantyOperationType.CustomerReplacement ||
                    existingOperation.TargetType != "CUSTOMER_CLAIM" ||
                    existingOperation.TargetId != command.ClaimId ||
                    existingOperation.PayloadHash != payloadHash)
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

            var claimItems = await _warranty.GetClaimItemsAsync(claim.Id, ct);
            var claimItemById = claimItems.ToDictionary(x => x.Id);
            var allClaimUnits = await _warranty.GetClaimUnitsAsync(claim.Id, ct);
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

            var products = new Dictionary<Guid, Product>();
            var supplierProducts = new Dictionary<Guid, SupplierProduct>();
            foreach (var productId in productIds)
            {
                var product = await _catalog.GetProductAsync(productId, ct);
                if (product is null || product.TrackingMode != TrackingMode.Serialized ||
                    string.IsNullOrWhiteSpace(product.Sku))
                {
                    return Result.Failure(
                        "warranty.replacement_product_not_trackable",
                        "Customer exact-unit replacement requires an active serialized Product with permanent SKU.");
                }

                products[productId] = product;

                await _resourceLock.AcquireAsync(
                    "supplier-product",
                    $"{supplierId:D}:{productId:D}",
                    ct);

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
                    NormalizeIdentity(x.Input.SerialNumber) is string serial ? $"SERIAL:{serial}" : null,
                    NormalizeIdentity(x.Input.Imei1) is string imei1 ? $"IMEI:{imei1}" : null,
                    NormalizeIdentity(x.Input.Imei2) is string imei2 ? $"IMEI:{imei2}" : null
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
                var serial = NormalizeIdentity(selected.Input.SerialNumber);
                var imei1 = NormalizeIdentity(selected.Input.Imei1);
                var imei2 = NormalizeIdentity(selected.Input.Imei2);

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

            var nextSequences = supplierProducts.ToDictionary(
                x => x.Key,
                x => x.Value.NextItemSequence);

            var replacementReferences = new Dictionary<Guid, List<string>>();
            foreach (var selected in selectedUnits.OrderBy(x => x.Item.ProductId).ThenBy(x => x.Link.Id))
            {
                var product = products[selected.Item.ProductId];
                var supplierProduct = supplierProducts[product.Id];
                var sequence = nextSequences[product.Id];
                nextSequences[product.Id] = checked(sequence + 1);

                var sku = TraceabilityCodeRules.NormalizeSku(product.Sku!);
                var trackingCode = TraceabilityCodeRules.BuildTrackingCode(
                    supplier.DealerCode!,
                    sku,
                    sequence);

                var original = originalUnitsById[selected.Link.OriginalInventoryUnitId!.Value];
                var replacement = new InventoryUnit
                {
                    ProductId = product.Id,
                    SupplierProductId = supplierProduct.Id,
                    OriginType = InventoryUnitOriginType.WarrantyReplacement,
                    SourceWarrantyClaimItemId = selected.Item.Id,
                    ItemSequence = sequence,
                    TrackingCode = trackingCode,
                    SupplierCodeSnapshot = supplier.DealerCode,
                    ProductSkuSnapshot = sku,
                    SerialNumber = NormalizeIdentity(selected.Input.SerialNumber),
                    Imei1 = NormalizeIdentity(selected.Input.Imei1),
                    Imei2 = NormalizeIdentity(selected.Input.Imei2),
                    Status = InventoryUnitStatus.WarrantyCustomerHeld,
                    AcquisitionCost = original.AcquisitionCost,
                    InventoryLotId = null,
                    SourcePurchaseItemId = null,
                    SourceWarrantyCaseId = null,
                    CreatedAt = _clock.UtcNow
                };

                _inventory.AddInventoryUnit(replacement);
                selected.Link.ReplacementInventoryUnitId = replacement.Id;
                selected.Link.ReplacementIdentitySnapshot = BuildIdentitySnapshot(replacement);

                if (!replacementReferences.TryGetValue(selected.Item.Id, out var refs))
                {
                    refs = new List<string>();
                    replacementReferences[selected.Item.Id] = refs;
                }
                refs.Add(trackingCode);
            }

            foreach (var pair in supplierProducts)
            {
                pair.Value.NextItemSequence = nextSequences[pair.Key];
                pair.Value.UpdatedAt = _clock.UtcNow;
                pair.Value.Version++;
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

    private static string? NormalizeIdentity(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : normalized.ToUpperInvariant();
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

            var quantity = product.TrackingMode == TrackingMode.Serialized
                ? command.BaseQuantity
                : QuantityMath.RoundQuantity(command.BaseQuantity);

            if (product.TrackingMode == TrackingMode.Serialized)
            {
                if (!QuantityMath.IsWhole(command.BaseQuantity))
                {
                    return Result<Guid>.Failure(
                        "warranty.serialized_quantity_whole",
                        "Serialized shop warranty quantity must be an exact whole number before rounding.");
                }

                if (command.InventoryUnitIds is null ||
                    command.InventoryUnitIds.Count != decimal.ToInt32(quantity) ||
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

                var units = await _inventory.GetInventoryUnitsForUpdateAsync(
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
                SourcePurchaseItemId = command.SourcePurchaseItemId,
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
                    InventoryMovementType.SendToSupplierWarranty),
                ct);

            if (!transfer.IsSuccess)
            {
                return Result<Guid>.Failure(transfer.Error!.Code, transfer.Error.Message);
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
    string? SupplierReference = null);

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
        IUnitOfWork unitOfWork)
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
            var payloadHash = WarrantyOperationIdentity.Hash(
                "SHOP_STOCK",
                "RECEIVE",
                command.CaseId.ToString("D"),
                command.Resolution.ToString(),
                string.Join(",", (command.OriginalInventoryUnitIds ?? Array.Empty<Guid>()).OrderBy(x => x).Select(x => x.ToString("D"))),
                string.Join(";", (command.ReplacementUnits ?? Array.Empty<ReplacementSerializedUnitInput>()).Select(x =>
                    $"{WarrantyOperationIdentity.Normalize(x.SerialNumber)}|{WarrantyOperationIdentity.Normalize(x.Imei1)}|{WarrantyOperationIdentity.Normalize(x.Imei2)}")),
                WarrantyOperationIdentity.Normalize(command.Note),
                command.SupplierCreditAmount?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                WarrantyOperationIdentity.Normalize(command.SupplierReference));
            var existingOperation = await _warranty.GetOperationByClientOperationIdAsync(command.ClientOperationId, ct);
            if (existingOperation is not null)
            {
                if (existingOperation.OperationType != operationType ||
                    existingOperation.TargetType != "SHOP_STOCK" ||
                    existingOperation.TargetId != command.CaseId ||
                    existingOperation.PayloadHash != payloadHash)
                {
                    return Result.Failure("payload_mismatch", "Operation was previously submitted with a different Warranty payload.");
                }
                return Result.Success();
            }

            await _resourceLock.AcquireAsync("warranty-case", command.CaseId, ct);
            await _resourceLock.AcquireAsync("product", preview.ProductId, ct);

            var product = await _catalog.GetProductAsync(preview.ProductId, ct);
            if (product is null)
            {
                return Result.Failure(
                    "catalog.product_not_found",
                    "Warranty product was not found.");
            }

            if (command.Resolution == WarrantyResolutionType.Replaced &&
                product.TrackingMode == TrackingMode.Serialized)
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
                        NormalizeIdentity(x.SerialNumber) is string serial ? $"SERIAL:{serial}" : null,
                        NormalizeIdentity(x.Imei1) is string imei1 ? $"IMEI:{imei1}" : null,
                        NormalizeIdentity(x.Imei2) is string imei2 ? $"IMEI:{imei2}" : null
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

            if (command.Resolution == WarrantyResolutionType.Replaced &&
                product.TrackingMode == TrackingMode.Serialized)
            {
                var replacementResult = await ReceiveSerializedReplacementAsync(
                    warrantyCase,
                    product,
                    command,
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

                var transfer = await _conditions.TransferAsync(
                    new TransferInventoryConditionCommand(
                        product.Id,
                        InventoryBucket.WithSupplier,
                        destination,
                        warrantyCase.BaseQuantity,
                        command.ActorId,
                        $"SUPPLIER_WARRANTY_{command.Resolution.ToString().ToUpperInvariant()}",
                        command.Note,
                        "SHOP_WARRANTY",
                        warrantyCase.Id,
                        command.OriginalInventoryUnitIds,
                        movementType),
                    ct);

                if (!transfer.IsSuccess)
                {
                    return Result.Failure(transfer.Error!.Code, transfer.Error.Message);
                }
            }

            warrantyCase.ResolutionType = command.Resolution;
            warrantyCase.SupplierReference = NormalizeNullable(command.SupplierReference)
                ?? warrantyCase.SupplierReference;
            warrantyCase.ReceivedAt = _clock.UtcNow;
            warrantyCase.ClosedAt = _clock.UtcNow;
            warrantyCase.Status = command.Resolution == WarrantyResolutionType.Scrapped
                ? ShopWarrantyCaseStatus.WrittenOff
                : ShopWarrantyCaseStatus.Closed;
            warrantyCase.ResolutionClientOperationId = command.ClientOperationId;
            warrantyCase.Version++;

            _warranty.AddOperation(new WarrantyOperation
            {
                ClientOperationId = command.ClientOperationId,
                TargetType = "SHOP_STOCK",
                TargetId = warrantyCase.Id,
                OperationType = operationType,
                ActorId = command.ActorId,
                PayloadHash = payloadHash,
                ResultId = warrantyCase.Id,
                OccurredAt = warrantyCase.ReceivedAt!.Value
            });

            _audit.Record(
                command.Resolution == WarrantyResolutionType.Credited
                    ? "SHOP_WARRANTY_CREDITED"
                    : "SHOP_WARRANTY_RESOLVED",
                "SHOP_WARRANTY",
                warrantyCase.Id,
                command.ActorId,
                command.ClientOperationId,
                $"Case {warrantyCase.CaseNumber}; resolution={command.Resolution}; supplier={warrantyCase.SupplierId:D}.");

            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }, cancellationToken);
    }

    private async Task<Result> ReceiveSerializedReplacementAsync(
        ShopStockWarrantyCase warrantyCase,
        Product product,
        ReceiveShopStockWarrantyCommand command,
        CancellationToken cancellationToken)
    {
        var quantity = warrantyCase.BaseQuantity;
        if (!QuantityMath.IsWhole(quantity) ||
            command.OriginalInventoryUnitIds is null ||
            command.OriginalInventoryUnitIds.Count != decimal.ToInt32(quantity) ||
            command.OriginalInventoryUnitIds.Distinct().Count() != command.OriginalInventoryUnitIds.Count ||
            command.ReplacementUnits is null ||
            command.ReplacementUnits.Count != decimal.ToInt32(quantity))
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

        var seenSerials = new HashSet<string>(StringComparer.Ordinal);
        var seenImeis = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in command.ReplacementUnits)
        {
            var serial = NormalizeIdentity(input.SerialNumber);
            var imei1 = NormalizeIdentity(input.Imei1);
            var imei2 = NormalizeIdentity(input.Imei2);

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
        await _costAllocator.TransferBucketAsync(
            product.Id,
            InventoryBucket.WithSupplier,
            InventoryBucket.Sellable,
            quantity,
            cancellationToken);

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

        var sku = TraceabilityCodeRules.NormalizeSku(product.Sku!);
        var firstSequence = supplierProduct.NextItemSequence;
        supplierProduct.NextItemSequence = checked(firstSequence + oldUnits.Count);
        supplierProduct.UpdatedAt = _clock.UtcNow;
        supplierProduct.Version++;

        var orderedOldUnits = oldUnits.OrderBy(x => x.Id).ToArray();
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

            var input = command.ReplacementUnits[index];
            var itemSequence = checked(firstSequence + index);
            var trackingCode = TraceabilityCodeRules.BuildTrackingCode(
                supplier.DealerCode!,
                sku,
                itemSequence);

            var replacement = new InventoryUnit
            {
                ProductId = product.Id,
                SupplierProductId = supplierProduct.Id,
                OriginType = InventoryUnitOriginType.WarrantyReplacement,
                SourceWarrantyClaimItemId = null,
                SourceWarrantyCaseId = warrantyCase.Id,
                SourcePurchaseItemId = null,
                ItemSequence = itemSequence,
                TrackingCode = trackingCode,
                SupplierCodeSnapshot = supplier.DealerCode,
                ProductSkuSnapshot = sku,
                SerialNumber = NormalizeIdentity(input.SerialNumber),
                Imei1 = NormalizeIdentity(input.Imei1),
                Imei2 = NormalizeIdentity(input.Imei2),
                Status = InventoryUnitStatus.InStock,
                AcquisitionCost = oldUnit.AcquisitionCost,
                InventoryLotId = oldUnit.InventoryLotId,
                CreatedAt = _clock.UtcNow
            };

            _inventory.AddInventoryUnit(replacement);
            _inventory.AddMovementUnit(new InventoryMovementUnit
            {
                MovementId = movement.Id,
                InventoryUnitId = replacement.Id,
                FromStatus = null,
                ToStatus = InventoryUnitStatus.InStock
            });
        }

        return Result.Success();
    }

    private async Task<Result> ResolveWarrantyCreditAsync(
        ShopStockWarrantyCase warrantyCase,
        Product product,
        ReceiveShopStockWarrantyCommand command,
        CancellationToken cancellationToken)
    {
        var operationId = command.ClientOperationId;
        var supplierCredit = Money(command.SupplierCreditAmount!.Value);
        var quantity = warrantyCase.BaseQuantity;

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

        if (product.TrackingMode == TrackingMode.Serialized)
        {
            if (!QuantityMath.IsWhole(quantity) ||
                command.OriginalInventoryUnitIds is null ||
                command.OriginalInventoryUnitIds.Count != decimal.ToInt32(quantity) ||
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
            foreach (var unit in units.OrderBy(x => x.Id))
            {
                var lotBalance = await _inventory.GetLotBucketBalanceForUpdateAsync(
                    unit.InventoryLotId!.Value,
                    InventoryBucket.WithSupplier,
                    cancellationToken);
                if (lotBalance is null || lotBalance.Quantity < 1m)
                {
                    return Result.Failure(
                        "warranty.credit_lot_insufficient",
                        "A warranty unit no longer has recoverable WITH_SUPPLIER lot quantity.");
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

                carryingCostRemoved += await _costAllocator.RemoveCarryingValueAsync(
                    product.Id,
                    1m,
                    unit.AcquisitionCost,
                    cancellationToken);

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

            var unitCost = await _costAllocator.GetCurrentUnitCostAsync(product.Id, cancellationToken) ?? 0m;
            await _costAllocator.ConsumeBucketAsync(
                product.Id,
                InventoryBucket.WithSupplier,
                quantity,
                movement.Id,
                unitCost,
                cancellationToken);
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
            ReferenceType = "WarrantyCase",
            ReferenceId = warrantyCase.Id,
            OccurredAt = _clock.UtcNow,
            ActorId = command.ActorId,
            ClientOperationId = operationId,
            Note = command.Note?.Trim(),
            CreatedAt = _clock.UtcNow
        };
        accountEntry.ValidateDirection();
        _supplierAccounts.AddEntry(accountEntry);

        warrantyCase.InventoryCarryingCostResolved = carryingCostRemoved;
        warrantyCase.SupplierCreditAmount = supplierCredit;
        warrantyCase.RecoveryDifference = recoveryDifference;

        return Result.Success();
    }

    private static decimal Money(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    private static decimal Cost(decimal value) =>
        decimal.Round(value, 6, MidpointRounding.AwayFromZero);

    private static string? NormalizeIdentity(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : normalized.ToUpperInvariant();
    }

    private static string? NormalizeNullable(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
