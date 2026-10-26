using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Exceptions;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Posting;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.SystemConfiguration;
using EdgeRetails.Application.Production.Outbox;
using EdgeRetails.Application.Production.Printing;

namespace EdgeRetails.Application.Features.Sales;

public sealed record PackAllocationDetails(
    string? TrackingCode = null,
    Guid? AllocatedContainerId = null,
    decimal? RequestedCutLength = null);

public sealed record CompleteSaleLineInput(
    Guid ProductId,
    Guid ProductUnitId,
    decimal EnteredQuantity,
    decimal ExpectedUnitPrice,
    IReadOnlyList<Guid> InventoryUnitIds,
    decimal? PriceOverrideUnitPrice = null,
    string? PriceOverrideReason = null,
    Guid? PreferredPackUnitId = null,
    bool AllowSplitCuts = false,
    PackAllocationDetails? PackAllocationDetails = null);

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
    IReadOnlyList<CompleteSaleLineInput> Lines,
    DateOnly? PostingDate = null,
    SupervisorApprovalRequest? SupervisorApproval = null,
    bool? CallerSuppliedBooleanOverride = null,
    Guid? RegisteredTerminalId = null)
{
    // Trusted application context: these properties are inaccessible to the public API binder.
    internal Guid? SourceDraftId { get; init; }
    internal long? SourceDraftVersion { get; init; }
    internal bool SourceDraftReplayOnly { get; init; }
}

public sealed record CompleteSaleResult(
    Guid SaleId,
    string InvoiceNumber,
    decimal GrandTotal,
    decimal ChangeGiven,
    bool WasExisting);

public static class CompleteSaleCanonicalPayload
{
    public static object CreateIntentProjection(CompleteSaleCommand command)
    {
        return new
        {
            command.CustomerId,
            command.CashierUserId,
            command.SessionId,
            InvoiceDiscount = Math.Round(command.InvoiceDiscount, 2, MidpointRounding.AwayFromZero),
            PaymentMethod = command.PaymentMethod.ToString(),
            AmountTendered = Math.Round(command.AmountTendered, 2, MidpointRounding.AwayFromZero),
            command.QuotationId,
            PostingDate = command.PostingDate?.ToString("yyyy-MM-dd"),
            SupervisorApproval = command.SupervisorApproval is null
                ? null
                : new
                {
                    command.SupervisorApproval.SupervisorId,
                    Reason = command.SupervisorApproval.Reason?.Trim(),
                    command.SupervisorApproval.ApprovalToken
                    // Explicitly EXCLUDE SupervisorPin and RequestedAtUtc per §22 / MON-P12W1-005
                },
            // Exclude PaymentReference per approved legacy concurrent retry rule (lines 1149-1152)
            // Exclude CallerSuppliedBooleanOverride as transport flag
            Lines = command.Lines.Select(line => new
            {
                line.ProductId,
                line.ProductUnitId,
                EnteredQuantity = line.EnteredQuantity,
                ExpectedUnitPrice = Math.Round(line.ExpectedUnitPrice, 2, MidpointRounding.AwayFromZero),
                InventoryUnitIds = line.InventoryUnitIds.OrderBy(id => id).ToList(), // Canonical sort for unordered sets per §22
                PriceOverrideUnitPrice = line.PriceOverrideUnitPrice.HasValue
                    ? Math.Round(line.PriceOverrideUnitPrice.Value, 2, MidpointRounding.AwayFromZero)
                    : (decimal?)null,
                PriceOverrideReason = line.PriceOverrideReason?.Trim(),
                line.PreferredPackUnitId,
                line.AllowSplitCuts,
                PackAllocationDetails = line.PackAllocationDetails is null
                    ? null
                    : new
                    {
                        line.PackAllocationDetails.TrackingCode,
                        line.PackAllocationDetails.AllocatedContainerId,
                        line.PackAllocationDetails.RequestedCutLength
                    }
            }).ToList()
        };
    }

    public static string ComputeFingerprint(CompleteSaleCommand command)
    {
        var projection = CreateIntentProjection(command);
        return OperationPayloadFingerprint.ComputeVersionedFingerprint("Sale", projection, OperationPayloadFingerprint.DefaultVersion);
    }
}

internal sealed record PreparedSaleLine(
    CompleteSaleLineInput Input,
    Product Product,
    ProductUnit ProductUnit,
    TransactionQuantitySnapshot Quantity,
    decimal ListUnitPrice,
    decimal UnitPrice,
    string? PriceOverrideReason,
    Guid? PriceOverrideBy,
    decimal GrossLineTotal,
    IReadOnlyList<InventoryUnit> SerializedUnits,
    PackAllocationResult? PackAllocation = null);

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
    private readonly IOperationOutcomeLedger? _outcomeLedger;
    private readonly IPackSubLedgerAllocationAuthority _packAllocations;
    private readonly IShopPostingValidationService? _postingValidation;
    private readonly IOutboxWriter? _outboxWriter;

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
        IUnitOfWork unitOfWork,
        IOperationOutcomeLedger? outcomeLedger = null,
        IPackSubLedgerAllocationAuthority? packAllocations = null,
        IShopPostingValidationService? postingValidation = null,
        IOutboxWriter? outboxWriter = null)
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
        _outcomeLedger = outcomeLedger;
        _packAllocations = packAllocations ?? new PackSubLedgerAllocationAuthority();
        _postingValidation = postingValidation;
        _outboxWriter = outboxWriter;
    }

    public async Task<Result<CompleteSaleResult>> HandleAsync(
        CompleteSaleCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ClientOperationId == Guid.Empty || command.Lines.Count == 0)
        {
            return Result<CompleteSaleResult>.Failure(
                "sales.invalid_request",
                "Sale requires an operation id and at least one item.");
        }

        if (command.SourceDraftId.HasValue != command.SourceDraftVersion.HasValue || command.SourceDraftId == Guid.Empty)
        {
            return Result<CompleteSaleResult>.Failure("sales.draft_context_invalid", "Draft identity and original version must be supplied together.");
        }
        var draftFingerprint = command.SourceDraftId.HasValue
            ? "PosDraft:v1:" + OperationPayloadFingerprint.ComputeSha256(System.Text.Json.JsonSerializer.Serialize(new
            {
                command.SourceDraftId, command.SourceDraftVersion
            }))
            : null;
        if (draftFingerprint is not null && _outcomeLedger is null)
        {
            return Result<CompleteSaleResult>.Failure("sales.draft_replay_authority_required", "Draft completion requires canonical outcome authority.");
        }
        var canonicalSaleFingerprint = command.ClientOperationId != Guid.Empty
            ? CompleteSaleCanonicalPayload.ComputeFingerprint(command)
            : null;
        var effectiveFingerprint = draftFingerprint ?? canonicalSaleFingerprint;
        var ownsDraftOutcome = false;
        var bindingRejected = false;

        var allInventoryUnits = command.Lines.SelectMany(x => x.InventoryUnitIds).ToList();
        if (allInventoryUnits.Count != allInventoryUnits.Distinct().Count())
        {
            return Result<CompleteSaleResult>.Failure(
                "sales.serial_selected_twice",
                "A serialized unit cannot be selected twice in one sale.");
        }

        var hasDuplicateCommodityLine = command.Lines
            .GroupBy(x => new { x.ProductId, x.ProductUnitId })
            .Any(g => g.Count() > 1 && g.Any(x => x.InventoryUnitIds.Count == 0));

        if (hasDuplicateCommodityLine)
        {
            return Result<CompleteSaleResult>.Failure(
                "sales.duplicate_line",
                "The same product and unit may appear only once in the cart.");
        }

        var result = await _transactions.ExecuteAsync(async ct =>
        {
            var authorization = await _authorization.AuthorizeAsync(
                command.CashierUserId,
                PermissionKeys.SalesCreate,
                ct);
            if (!authorization.IsSuccess)
            {
                bindingRejected = true;
                return Result<CompleteSaleResult>.Failure(
                    authorization.Error!.Code,
                    authorization.Error.Message);
            }

            await _operationLock.AcquireAsync(command.ClientOperationId, ct);

            var outcome = _outcomeLedger is null ? null : await _outcomeLedger.GetOutcomeAsync(command.ClientOperationId, ct);
            var existingDraftBinding = outcome?.PayloadFingerprint?.StartsWith("PosDraft:v1:", StringComparison.Ordinal) == true;
            if (draftFingerprint is not null || existingDraftBinding)
            {
                var existingForDraft = await _sales.GetSaleByClientOperationIdAsync(command.ClientOperationId, ct);
                if (outcome is not null)
                {
                    if (draftFingerprint is null || outcome.OperationType != "Sale" ||
                        outcome.ActorId != command.CashierUserId || outcome.PayloadFingerprint != draftFingerprint)
                    {
                        bindingRejected = true;
                        return Result<CompleteSaleResult>.Failure("idempotency.payload_mismatch", "Operation belongs to another draft completion intent.");
                    }
                    if (outcome.State != OperationOutcomeState.Succeeded || !outcome.WasCommitted ||
                        existingForDraft is null || outcome.EntityId != existingForDraft.Id)
                    {
                        bindingRejected = true;
                        return Result<CompleteSaleResult>.Failure(outcome.ErrorCode ?? "idempotency.outcome_unknown",
                            outcome.ErrorMessage ?? "Reconcile the previous draft sale outcome.");
                    }

                    if (!await MatchesExistingSalePayloadAsync(existingForDraft, command, ct))
                    {
                        bindingRejected = true;
                        return Result<CompleteSaleResult>.Failure("idempotency.payload_mismatch", "Operation was previously submitted with different sale details.");
                    }

                    var payment = await _sales.GetSalePaymentAsync(existingForDraft.Id, ct);
                    return Result<CompleteSaleResult>.Success(new(
                        existingForDraft.Id,
                        existingForDraft.InvoiceNumber,
                        existingForDraft.GrandTotal,
                        payment?.ChangeGiven ?? 0m,
                        true));
                }
                else if (existingForDraft is not null)
                {
                    bindingRejected = true;
                    return Result<CompleteSaleResult>.Failure("idempotency.legacy_draft_requires_reconciliation", "Existing sale has no canonical draft binding.");
                }
                else
                {
                    if (command.SourceDraftReplayOnly)
                    {
                        bindingRejected = true;
                        return Result<CompleteSaleResult>.Failure("idempotency.legacy_draft_requires_reconciliation",
                            "Converted draft requires its original committed sale outcome; a new operation cannot charge it again.");
                    }
                    ownsDraftOutcome = true;
                }
            }

            // Area 12D / MON-P12W1-007: Outcome ledger claim & replay evaluation BEFORE mutable current-state evaluation
            if (draftFingerprint is null && !existingDraftBinding && _outcomeLedger is not null && command.ClientOperationId != Guid.Empty && effectiveFingerprint is not null)
            {
                try
                {
                    var claim = await _outcomeLedger.ClaimAsync(
                        command.ClientOperationId,
                        "Sale",
                        effectiveFingerprint,
                        actorId: command.CashierUserId,
                        terminalId: command.RegisteredTerminalId,
                        sessionId: command.SessionId,
                        cancellationToken: ct);

                    if (claim.Status == OperationClaimStatus.AlreadySucceeded)
                    {
                        // 1. Authoritative durable response payload replay
                        if (!string.IsNullOrEmpty(claim.Outcome?.ResponsePayload))
                        {
                            try
                            {
                                var cached = System.Text.Json.JsonSerializer.Deserialize<CompleteSaleResult>(claim.Outcome.ResponsePayload);
                                if (cached is not null && cached.SaleId != Guid.Empty && !string.IsNullOrEmpty(cached.InvoiceNumber))
                                {
                                    return Result<CompleteSaleResult>.Success(cached with { WasExisting = true });
                                }

                                return Result<CompleteSaleResult>.Failure(
                                    "idempotency.outcome_corrupted",
                                    "The operation was recorded as succeeded, but the cached response payload is corrupted.");
                            }
                            catch
                            {
                                return Result<CompleteSaleResult>.Failure(
                                    "idempotency.outcome_corrupted",
                                    "The operation was recorded as succeeded, but the cached response payload is corrupted.");
                            }
                        }

                        // 2. Legacy fallback if ResponsePayload was not cached (e.g. pre-Phase-12 record)
                        var existingSale = await _sales.GetSaleByClientOperationIdAsync(command.ClientOperationId, ct);
                        if (existingSale is not null)
                        {
                            var payment = await _sales.GetSalePaymentAsync(existingSale.Id, ct);
                            return Result<CompleteSaleResult>.Success(new CompleteSaleResult(
                                existingSale.Id,
                                existingSale.InvoiceNumber,
                                existingSale.GrandTotal,
                                payment?.ChangeGiven ?? 0m,
                                true));
                        }

                        // 3. MON-P12W1-007: Fail closed on corrupt/missing outcome; NEVER fall through to mutate a second sale!
                        return Result<CompleteSaleResult>.Failure(
                            "idempotency.outcome_corrupted",
                            "Operation was recorded as succeeded but the resulting sale could not be recovered.");
                    }

                    if (claim.Status == OperationClaimStatus.AlreadyFailed)
                    {
                        return Result<CompleteSaleResult>.Failure(
                            claim.ErrorCode ?? "sale.already_failed",
                            claim.ErrorMessage ?? "The operation previously failed.");
                    }

                    if (claim.Status == OperationClaimStatus.InFlight)
                    {
                        return Result<CompleteSaleResult>.Failure(
                            "idempotency.operation_in_flight",
                            "Operation is currently in flight.");
                    }
                }
                catch (IdempotencyPayloadMismatchException)
                {
                    bindingRejected = true;
                    return Result<CompleteSaleResult>.Failure(
                        "idempotency.payload_mismatch",
                        "The operation payload does not match the original submission for this operation ID.");
                }
                catch (IdempotencyOperationTypeMismatchException)
                {
                    bindingRejected = true;
                    return Result<CompleteSaleResult>.Failure(
                        "idempotency.operation_type_mismatch",
                        "The operation type does not match the original submission for this operation ID.");
                }
                catch (IdempotencyContextMismatchException ex)
                {
                    bindingRejected = true;
                    return Result<CompleteSaleResult>.Failure(
                        "idempotency.context_mismatch",
                        ex.Message);
                }
            }

            // Fallback for tests or environments running without an outcome ledger
            if (_outcomeLedger is null && command.ClientOperationId != Guid.Empty)
            {
                var existing = await _sales.GetSaleByClientOperationIdAsync(command.ClientOperationId, ct);
                if (existing is not null)
                {
                    if (!await MatchesExistingSalePayloadAsync(existing, command, ct))
                    {
                        return Result<CompleteSaleResult>.Failure(
                            "idempotency.payload_mismatch",
                            "Operation was previously submitted with different sale details.");
                    }

                    var payment = await _sales.GetSalePaymentAsync(existing.Id, ct);
                    return Result<CompleteSaleResult>.Success(new CompleteSaleResult(
                        existing.Id,
                        existing.InvoiceNumber,
                        existing.GrandTotal,
                        payment?.ChangeGiven ?? 0m,
                        true));
                }
            }

            foreach (var productId in command.Lines
                .Select(x => x.ProductId)
                .Distinct()
                .OrderBy(x => x))
            {
                await _resourceLock.AcquireAsync("product", productId, ct);
            }

            var serializedUnitIds = command.Lines
                .SelectMany(x => x.InventoryUnitIds)
                .Distinct()
                .OrderBy(x => x)
                .ToArray();

            foreach (var unitId in serializedUnitIds)
            {
                await _resourceLock.AcquireAsync("inventory-unit", unitId, ct);
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
                    var customer = await _parties.GetCustomerForUpdateAsync(
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
                    command.CashierUserId,
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
                    if (line.Product.TrackingMode is TrackingMode.Serialized or TrackingMode.IndividualPiece)
                    {
                        totalCost = await ConsumeSerializedAsync(
                            line,
                            movement,
                            ct);
                    }
                    else if (line.Product.TrackingMode == TrackingMode.Container && line.PackAllocation is not null)
                    {
                        totalCost = await ConsumePackAllocationAsync(
                            line,
                            line.PackAllocation,
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

                var saleResult = new CompleteSaleResult(
                    sale.Id,
                    sale.InvoiceNumber,
                    sale.GrandTotal,
                    change,
                    false);

                if (_outcomeLedger is not null)
                {
                    var responseJson = System.Text.Json.JsonSerializer.Serialize(saleResult);
                    await _outcomeLedger.RecordSuccessWithResponseAsync(
                        command.ClientOperationId,
                        "Sale",
                        sale.Id,
                        responsePayload: responseJson,
                        responseStatusCode: 200,
                        responseContentType: "application/json",
                        documentNumber: sale.InvoiceNumber,
                        actorId: command.CashierUserId,
                        terminalId: command.RegisteredTerminalId,
                        sessionId: command.SessionId,
                        payloadFingerprint: effectiveFingerprint,
                        cancellationToken: ct);
                }

                if (_postingValidation is not null)
                {
                    var postingDate = command.PostingDate ?? _clock.ShopDate;
                    var postingValidationResult = await _postingValidation.ValidateAndAuditPostingAsync(new PostingValidationCommand(
                        OperationType: PostingOperationTypes.Sale,
                        EntityId: sale.Id,
                        EntityNumber: sale.InvoiceNumber,
                        ActorId: command.CashierUserId,
                        PostingDate: postingDate,
                        SupplierDocumentDate: null,
                        SupervisorApproval: command.SupervisorApproval,
                        CallerSuppliedBooleanOverride: command.CallerSuppliedBooleanOverride), ct);

                    if (!postingValidationResult.IsSuccess)
                    {
                        return Result<CompleteSaleResult>.Failure(
                            postingValidationResult.Error!.Code,
                            postingValidationResult.Error.Message);
                    }
                }

                if (_outboxWriter is not null)
                {
                    _outboxWriter.Enqueue(new OutboxMessage
                    {
                        Id = Guid.NewGuid(),
                        EffectType = PrintOutboxEffectHandler.TypeName,
                        SourceType = "Sale",
                        SourceId = sale.Id.ToString("D"),
                        PayloadJson = System.Text.Json.JsonSerializer.Serialize(new PrintOutboxPayload(
                            ProductionDocumentKind.PosSaleReceipt,
                            sale.Id,
                            "DefaultReceipt",
                            IsReprint: false,
                            CorrelationId: command.ClientOperationId.ToString("D"))),
                        IdempotencyKey = "print-sale-" + command.ClientOperationId.ToString("N"),
                        Status = OutboxMessageStatus.Pending,
                        AttemptCount = 0,
                        CreatedAt = _clock.UtcNow
                    });
                }

                await _unitOfWork.SaveChangesAsync(ct);

                return Result<CompleteSaleResult>.Success(saleResult);
            }
            catch (BusinessRuleException ex)
            {
                return Result<CompleteSaleResult>.Failure(ex.Code, ex.Message);
            }
        }, cancellationToken);

        if (!result.IsSuccess && _outcomeLedger is not null && command.ClientOperationId != Guid.Empty && !bindingRejected)
        {
            if (draftFingerprint is not null)
            {
                if (ownsDraftOutcome)
                {
                    await _transactions.ExecuteAsync(async ct =>
                    {
                        await _operationLock.AcquireAsync(command.ClientOperationId, ct);
                        if (await _outcomeLedger.GetOutcomeAsync(command.ClientOperationId, ct) is null &&
                            await _sales.GetSaleByClientOperationIdAsync(command.ClientOperationId, ct) is null)
                        {
                            await _outcomeLedger.RecordFailureAsync(command.ClientOperationId, "Sale",
                                result.Error!.Code, result.Error.Message, actorId: command.CashierUserId,
                                terminalId: command.RegisteredTerminalId,
                                sessionId: command.SessionId, payloadFingerprint: draftFingerprint, cancellationToken: ct);
                        }
                        return true;
                    }, cancellationToken);
                }
                return result;
            }
            await _outcomeLedger.RecordFailureAsync(
                command.ClientOperationId,
                "Sale",
                result.Error?.Code ?? "sale.failed",
                result.Error?.Message ?? "Sale completion failed.",
                actorId: command.CashierUserId,
                terminalId: command.RegisteredTerminalId,
                sessionId: command.SessionId,
                payloadFingerprint: effectiveFingerprint,
                cancellationToken: cancellationToken);
        }

        return result;
    }

    private async Task<IReadOnlyList<PreparedSaleLine>> PrepareLinesAsync(
        IReadOnlyList<CompleteSaleLineInput> inputs,
        IReadOnlyDictionary<Guid, Product> products,
        Quotation? quotation,
        IReadOnlyList<QuotationItem> quotationItems,
        Guid cashierUserId,
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

            if (input.PriceOverrideUnitPrice is null && !string.IsNullOrWhiteSpace(input.PriceOverrideReason))
            {
                throw new BusinessRuleException(
                    "sales.price_override_reason_without_price",
                    "A price override reason requires an override price.");
            }

            if (input.PriceOverrideUnitPrice is decimal negativePrice && negativePrice < 0m)
            {
                throw new BusinessRuleException(
                    "sales.price_override_invalid",
                    "Final override price cannot be negative.");
            }

            if (Money(input.ExpectedUnitPrice) != authoritativePrice)
            {
                throw new BusinessRuleException(
                    "sales.price_changed",
                    $"Price changed for '{product.Name}'. Recalculate the cart.");
            }

            var finalUnitPrice = authoritativePrice;
            string? overrideReason = null;
            Guid? overrideBy = null;
            if (input.PriceOverrideUnitPrice is decimal requestedOverride)
            {
                var reason = input.PriceOverrideReason?.Trim();
                if (string.IsNullOrWhiteSpace(reason))
                {
                    throw new BusinessRuleException(
                        "sales.price_override_reason_required",
                        "A reason is required for a one-sale price override.");
                }

                if (reason.Length > 500)
                {
                    throw new BusinessRuleException(
                        "sales.price_override_reason_too_long",
                        "Price override reason must be 500 characters or fewer.");
                }

                var permission = await _authorization.AuthorizeAsync(
                    cashierUserId,
                    PermissionKeys.SalesPriceOverride,
                    ct);
                if (!permission.IsSuccess)
                {
                    throw new BusinessRuleException(
                        permission.Error?.Code ?? "authorization.denied",
                        permission.Error?.Message ?? "Price override permission is required.");
                }

                finalUnitPrice = Money(requestedOverride);
                overrideReason = reason;
                overrideBy = cashierUserId;
            }

            IReadOnlyList<InventoryUnit> serializedUnits = Array.Empty<InventoryUnit>();
            PackAllocationResult? packAllocationResult = null;

            if (product.TrackingMode is TrackingMode.Serialized or TrackingMode.IndividualPiece)
            {
                var physicalCount = quantity.BaseQuantity;
                if (!QuantityMath.IsWhole(physicalCount) ||
                    input.InventoryUnitIds.Count != decimal.ToInt32(physicalCount))
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
            else if (product.TrackingMode == TrackingMode.Container)
            {
                var isCable = ProductPackBehaviorAuthority.IsCableOrLengthProduct(product);

                if (!isCable && !QuantityMath.IsWhole(quantity.BaseQuantity))
                {
                    throw new BusinessRuleException(
                        "sales.pack_fractional_count_prohibited",
                        $"Fractional unit quantity '{quantity.BaseQuantity}' is not permitted for count-based pack product '{product.Name}'.");
                }

                var isStrictPack = product.AttributesJson?.Contains("\"strict_pack\": true", StringComparison.OrdinalIgnoreCase) == true ||
                                   product.AttributesJson?.Contains("\"AllowLooseSale\": false", StringComparison.OrdinalIgnoreCase) == true;

                if (isStrictPack && input.InventoryUnitIds.Count == 0 && input.PreferredPackUnitId is null && input.PackAllocationDetails is null)
                {
                    throw new BusinessRuleException(
                        "sales.pack_tracking_required",
                        $"Product '{product.Name}' strictly enforces pack tracking; container allocation details are required.");
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

                    if (!isCable && input.PackAllocationDetails is null)
                    {
                        foreach (var u in eligibleUnits)
                        {
                            var unitQty = u.RemainingBaseQuantity ?? u.InitialBaseQuantity ?? await _inventory.GetPhysicalUnitBaseQuantitySnapshotAsync(u, ct);
                            if (unitQty > 0m && unitQty != quantity.BaseQuantity / eligibleUnits.Count)
                            {
                                throw new BusinessRuleException(
                                    "sales.container_quantity_mismatch",
                                    "Selected sale unit does not match the original physical pack quantity.");
                            }
                        }
                    }
                }
                else
                {
                    eligibleUnits = await _inventory.GetSellableInventoryUnitsAsync(product.Id, ct);
                }

                var preferredUnitId = input.PreferredPackUnitId
                    ?? input.PackAllocationDetails?.AllocatedContainerId
                    ?? (input.InventoryUnitIds.Count == 1 ? input.InventoryUnitIds[0] : null);

                if (preferredUnitId is null && !string.IsNullOrWhiteSpace(input.PackAllocationDetails?.TrackingCode))
                {
                    var matchedByCode = eligibleUnits.FirstOrDefault(u =>
                        string.Equals(u.TrackingCode, input.PackAllocationDetails.TrackingCode.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (matchedByCode is not null)
                    {
                        preferredUnitId = matchedByCode.Id;
                    }
                }

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

            if (input.PriceOverrideUnitPrice is not null)
            {
                var belowCurrentCost = product.TrackingMode is TrackingMode.Serialized or TrackingMode.IndividualPiece
                    ? serializedUnits.Any(unit => finalUnitPrice < Money(unit.AcquisitionCost))
                    : product.TrackingMode == TrackingMode.Container
                        ? (packAllocationResult is not null
                            ? (finalUnitPrice < Money(packAllocationResult.TotalCostDerecognized / quantity.EnteredQuantity))
                            : await IsBelowCurrentCostAsync(product.Id, productUnit.FactorToBaseUnit, finalUnitPrice, ct))
                        : await IsBelowCurrentCostAsync(
                            product.Id,
                            productUnit.FactorToBaseUnit,
                            finalUnitPrice,
                            ct);
                if (belowCurrentCost)
                {
                    var belowCostPermission = await _authorization.AuthorizeAsync(
                        cashierUserId,
                        PermissionKeys.SalesPriceOverrideBelowCost,
                        ct);
                    if (!belowCostPermission.IsSuccess)
                    {
                        throw new BusinessRuleException(
                            belowCostPermission.Error?.Code ?? "authorization.denied",
                            belowCostPermission.Error?.Message ?? "Below-cost price override permission is required.");
                    }
                }
            }

            result.Add(new PreparedSaleLine(
                input,
                product,
                productUnit,
                quantity,
                authoritativePrice,
                finalUnitPrice,
                overrideReason,
                overrideBy,
                Money(quantity.EnteredQuantity * finalUnitPrice),
                serializedUnits,
                packAllocationResult));
        }

        return result;
    }

    private async Task<bool> IsBelowCurrentCostAsync(
        Guid productId,
        decimal factorToBaseUnit,
        decimal finalUnitPrice,
        CancellationToken cancellationToken)
    {
        var baseUnitCost = await _costs.GetCurrentUnitCostAsync(productId, cancellationToken);
        return baseUnitCost is null ||
               finalUnitPrice < Money(baseUnitCost.Value * factorToBaseUnit);
    }

    private async Task<bool> MatchesExistingSalePayloadAsync(
        Sale sale,
        CompleteSaleCommand command,
        CancellationToken cancellationToken)
    {
        if (sale.CustomerId != command.CustomerId ||
            sale.CashierUserId != command.CashierUserId ||
            sale.SessionId != command.SessionId ||
            sale.InvoiceDiscount != Money(command.InvoiceDiscount))
        {
            return false;
        }

        var payment = await _sales.GetSalePaymentAsync(sale.Id, cancellationToken);
        if (payment is null ||
            payment.Method != command.PaymentMethod ||
            payment.AmountTendered != Money(command.AmountTendered))
        {
            return false;
        }

        // An operation ID selects one canonical result. On a concurrent retry, retain the
        // reference stored by the winning request rather than turning the replay into a second
        // payment or rejecting an otherwise identical sale.

        var persistedItems = (await _sales.GetSaleItemsAsync(sale.Id, cancellationToken)).ToArray();
        var persistedUnitsMap = new Dictionary<Guid, Guid[]>();
        foreach (var item in persistedItems)
        {
            var units = (await _sales.GetSaleItemUnitsAsync(item.Id, cancellationToken))
                .Select(x => x.InventoryUnitId)
                .OrderBy(x => x)
                .ToArray();
            persistedUnitsMap[item.Id] = units;
        }

        var sortedPersisted = persistedItems
            .OrderBy(x => x.ProductId)
            .ThenBy(x => x.ProductUnitId)
            .ThenBy(x => x.UnitPrice)
            .ThenBy(x => x.EnteredQuantity)
            .ThenBy(x => persistedUnitsMap[x.Id].FirstOrDefault())
            .ToArray();

        var sortedRequested = command.Lines
            .OrderBy(x => x.ProductId)
            .ThenBy(x => x.ProductUnitId)
            .ThenBy(x => x.PriceOverrideUnitPrice is decimal overridePrice ? Money(overridePrice) : Money(x.ExpectedUnitPrice))
            .ThenBy(x => x.EnteredQuantity)
            .ThenBy(x => x.InventoryUnitIds.OrderBy(id => id).FirstOrDefault())
            .ToArray();

        if (sortedPersisted.Length != sortedRequested.Length)
        {
            return false;
        }

        for (var index = 0; index < sortedPersisted.Length; index++)
        {
            var persisted = sortedPersisted[index];
            var requested = sortedRequested[index];
            var requestedReason = requested.PriceOverrideUnitPrice is null
                ? null
                : requested.PriceOverrideReason?.Trim();
            var requestedFinalPrice = requested.PriceOverrideUnitPrice is decimal overridePrice
                ? Money(overridePrice)
                : Money(requested.ExpectedUnitPrice);

            if (persisted.ProductId != requested.ProductId ||
                persisted.ProductUnitId != requested.ProductUnitId ||
                persisted.EnteredQuantity != requested.EnteredQuantity ||
                persisted.ListUnitPriceSnapshot != Money(requested.ExpectedUnitPrice) ||
                persisted.UnitPrice != requestedFinalPrice ||
                !string.Equals(persisted.PriceOverrideReason, requestedReason, StringComparison.Ordinal) ||
                persisted.PriceOverrideBy != (requested.PriceOverrideUnitPrice is null ? null : command.CashierUserId))
            {
                return false;
            }

            var selectedUnitIds = persistedUnitsMap[persisted.Id];
            var requestedUnitIds = requested.InventoryUnitIds.OrderBy(x => x).ToArray();
            if (!selectedUnitIds.SequenceEqual(requestedUnitIds))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<decimal> ConsumeSerializedAsync(
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

            lotBalance.Quantity = QuantityMath.RoundQuantity(
                lotBalance.Quantity - basePerUnit);

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

            totalCost += await _costs.RemoveCarryingValueByAmountAsync(
                line.Product.Id,
                alloc.AllocatedBaseQuantity,
                alloc.TotalCostSnapshot,
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

