using System.Globalization;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Warranty;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Warranty;

namespace EdgeRetails.Desktop.Services;

/// <summary>Warranty and supplier ledger adapter for the local Server HTTP boundary.</summary>
public sealed class RemoteBackendOperationsService(DesktopApiClient apiClient) : IBackendOperationsService
{
    public async Task<SupplierAccountWorkspaceDto> GetSupplierWorkspaceAsync(Guid supplierId, int pageSize = 200,
        DateTimeOffset? beforeOccurredAt = null, DateTimeOffset? beforeCreatedAt = null, Guid? beforeEntryId = null,
        CancellationToken cancellationToken = default)
    {
        ValidateCursor(beforeOccurredAt.HasValue, beforeCreatedAt.HasValue, beforeEntryId.HasValue,
            "Supplier workspace cursor requires beforeOccurredAt, beforeCreatedAt, and beforeEntryId together.");
        var size = Math.Clamp(pageSize, 1, 200);
        DateTimeOffset? entryCursorOccurredAt = beforeOccurredAt;
        DateTimeOffset? entryCursorCreatedAt = beforeCreatedAt;
        Guid? entryCursorId = beforeEntryId;
        DateTimeOffset? paymentCursorDate = null;
        Guid? paymentCursorId = null;
        DateTimeOffset? refundCursorDate = null;
        Guid? refundCursorId = null;
        string? productCursorName = null;
        Guid? productCursorId = null;
        var statement = new List<SupplierKhataEntryDto>();
        var payments = new List<SupplierPaymentReadDto>();
        var refunds = new List<SupplierRefundReadDto>();
        var products = new List<SupplierProductContextDto>();
        var statementComplete = false;
        var paymentsComplete = false;
        var refundsComplete = false;
        var productsComplete = false;
        SupplierAccountWorkspaceDto? first = null;
        while (true)
        {
            var path = $"/api/suppliers/{supplierId:D}/workspace?pageSize={size}";
            if (entryCursorOccurredAt.HasValue)
            {
                path += $"&beforeOccurredAt={Uri.EscapeDataString(entryCursorOccurredAt.Value.ToString("O", CultureInfo.InvariantCulture))}" +
                        $"&beforeCreatedAt={Uri.EscapeDataString(entryCursorCreatedAt!.Value.ToString("O", CultureInfo.InvariantCulture))}" +
                        $"&beforeEntryId={entryCursorId:D}";
            }

            if (paymentCursorDate.HasValue)
            {
                path += $"&beforePaymentPaidAt={Uri.EscapeDataString(paymentCursorDate.Value.ToString("O", CultureInfo.InvariantCulture))}&beforePaymentId={paymentCursorId:D}";
            }

            if (refundCursorDate.HasValue)
            {
                path += $"&beforeRefundReceivedAt={Uri.EscapeDataString(refundCursorDate.Value.ToString("O", CultureInfo.InvariantCulture))}&beforeRefundId={refundCursorId:D}";
            }

            if (productCursorName is not null)
            {
                path += $"&beforeProductName={Uri.EscapeDataString(productCursorName)}&beforeProductId={productCursorId:D}";
            }

            var page = await apiClient.GetAsync<SupplierAccountWorkspaceDto>(path, cancellationToken);
            first ??= page;
            if (!statementComplete)
            {
                statement.AddRange(page.Statement);
                statementComplete = page.Statement.Count < size;
                if (!statementComplete)
                {
                    entryCursorOccurredAt = page.Statement[^1].OccurredAt;
                    entryCursorCreatedAt = page.Statement[^1].CreatedAt;
                    entryCursorId = page.Statement[^1].EntryId;
                }
            }

            if (!paymentsComplete)
            {
                payments.AddRange(page.Payments);
                paymentsComplete = page.Payments.Count < size;
                if (!paymentsComplete)
                {
                    paymentCursorDate = page.Payments[^1].PaidAt;
                    paymentCursorId = page.Payments[^1].PaymentId;
                }
            }

            if (!refundsComplete)
            {
                refunds.AddRange(page.Refunds);
                refundsComplete = page.Refunds.Count < size;
                if (!refundsComplete)
                {
                    refundCursorDate = page.Refunds[^1].ReceivedAt;
                    refundCursorId = page.Refunds[^1].RefundId;
                }
            }

            if (!productsComplete)
            {
                products.AddRange(page.SuppliedProducts);
                productsComplete = page.SuppliedProducts.Count < size;
                if (!productsComplete)
                {
                    productCursorName = page.SuppliedProducts[^1].ProductName;
                    productCursorId = page.SuppliedProducts[^1].ProductId;
                }
            }

            if (statementComplete && paymentsComplete && refundsComplete && productsComplete)
            {
                break;
            }
        }

        return first! with
        {
            Statement = statement,
            Payments = payments,
            Refunds = refunds,
            SuppliedProducts = products
        };
    }

    public async Task CreateSupplierPaymentAsync(Guid supplierId, decimal amount, SupplierPaymentPurpose purpose,
        SupplierSettlementMethod method, Guid clientOperationId, string? externalReference, string? note,
        CancellationToken cancellationToken = default)
    {
        var command = new CreateSupplierPaymentCommand(supplierId, amount, purpose, method, Guid.Empty,
            RequireOperation(clientOperationId), externalReference, note);
        await apiClient.PostAsync<CreateSupplierPaymentCommand, SupplierPaymentResult>(
            "/api/finance/supplier-payment", command, cancellationToken);
    }

    public async Task ReverseSupplierPaymentAsync(Guid paymentId, string reason, Guid clientOperationId,
        CancellationToken cancellationToken = default) =>
        _ = await apiClient.PostAsync<ReverseRequest, bool>(
            $"/api/finance/supplier-payment/{paymentId:D}/reverse",
            new(clientOperationId, reason), cancellationToken);

    public async Task CreateSupplierRefundAsync(Guid supplierId, decimal amount, SupplierSettlementMethod method,
        Guid clientOperationId, string? externalReference, string? note, CancellationToken cancellationToken = default)
    {
        var command = new CreateSupplierRefundCommand(supplierId, amount, method, Guid.Empty,
            RequireOperation(clientOperationId), ExternalReference: externalReference, Note: note);
        await apiClient.PostAsync<CreateSupplierRefundCommand, SupplierRefundResult>(
            "/api/finance/supplier-refund", command, cancellationToken);
    }

    public async Task ReverseSupplierRefundAsync(Guid refundId, string reason, Guid clientOperationId,
        CancellationToken cancellationToken = default) =>
        _ = await apiClient.PostAsync<ReverseRequest, bool>(
            $"/api/finance/supplier-refund/{refundId:D}/reverse",
            new(clientOperationId, reason), cancellationToken);

    public Task<WarrantyDashboardDto> GetWarrantyDashboardAsync(string? search, int pageSize = 100,
        DateTimeOffset? beforeCreatedAt = null, Guid? beforeWorkId = null,
        CancellationToken cancellationToken = default, WarrantyWorkKind? beforeWorkKind = null)
    {
        ValidateCursor(beforeCreatedAt.HasValue, beforeWorkId.HasValue, beforeWorkKind.HasValue,
            "Warranty dashboard cursor requires beforeCreatedAt, beforeWorkKind, and beforeWorkId together.");
        var path = $"/api/warranty/dashboard?pageSize={Math.Clamp(pageSize, 1, 200)}&search={Uri.EscapeDataString(search ?? string.Empty)}";
        if (beforeCreatedAt.HasValue)
        {
            path += $"&beforeCreatedAt={Uri.EscapeDataString(beforeCreatedAt.Value.ToString("O", CultureInfo.InvariantCulture))}" +
                    $"&beforeWorkKind={beforeWorkKind}&beforeWorkId={beforeWorkId:D}";
        }
        return apiClient.GetAsync<WarrantyDashboardDto>(path, cancellationToken);
    }

    public Task<IReadOnlyList<WarrantyEventDto>> GetWarrantyClaimTimelineAsync(Guid claimId,
        CancellationToken cancellationToken = default) =>
        apiClient.GetAsync<IReadOnlyList<WarrantyEventDto>>($"/api/warranty/claims/{claimId:D}/timeline", cancellationToken);

    public Task<IReadOnlyList<WarrantyClaimIntakeRowDto>> SearchWarrantyClaimIntakeAsync(string search,
        CancellationToken cancellationToken = default) =>
        apiClient.GetAsync<IReadOnlyList<WarrantyClaimIntakeRowDto>>(
            $"/api/warranty/search?query={Uri.EscapeDataString(search)}", cancellationToken);

    public Task<Guid> CreateWarrantyClaimAsync(Guid customerId, Guid saleId, Guid? supplierId, Guid productId,
        decimal quantity, Guid saleItemId, string faultDescription, IReadOnlyList<WarrantyClaimUnitInput>? units,
        Guid clientOperationId, CancellationToken cancellationToken = default) =>
        apiClient.PostAsync<CreateWarrantyClaimCommand, Guid>("/api/warranty/claims",
            new(customerId, saleId, supplierId, Guid.Empty,
                [new WarrantyClaimItemInput(productId, quantity, faultDescription, saleItemId, units)],
                RequireOperation(clientOperationId)), cancellationToken);

    public Task BeginWarrantyClaimReviewAsync(Guid claimId, Guid clientOperationId, string? note,
        CancellationToken cancellationToken = default) => PostActionAsync($"/api/warranty/claims/{claimId:D}/review", clientOperationId, note, cancellationToken);
    public Task SendWarrantyClaimToSupplierAsync(Guid claimId, Guid clientOperationId, string? note,
        CancellationToken cancellationToken = default) => PostActionAsync($"/api/warranty/claims/{claimId:D}/send-to-supplier", clientOperationId, note, cancellationToken);
    public Task MarkWarrantySupplierProcessingAsync(Guid claimId, Guid clientOperationId, string? note,
        CancellationToken cancellationToken = default) => PostActionAsync($"/api/warranty/claims/{claimId:D}/supplier-processing", clientOperationId, note, cancellationToken);

    public async Task ResolveWarrantyClaimAsync(Guid claimId, WarrantyResolutionType resolution,
        Guid clientOperationId, string? note, CancellationToken cancellationToken = default) =>
        _ = await apiClient.PostAsync<ResolutionRequest, bool>($"/api/warranty/claims/{claimId:D}/resolution",
            new(clientOperationId, resolution, note), cancellationToken);

    public async Task ReceiveCustomerWarrantyReplacementAsync(Guid claimId,
        IReadOnlyList<CustomerWarrantyReplacementUnitInput> units, Guid clientOperationId, string? note,
        CancellationToken cancellationToken = default) =>
        _ = await apiClient.PostAsync<CustomerReplacementRequest, bool>($"/api/warranty/claims/{claimId:D}/replacement-receipt",
            new(clientOperationId, units, note), cancellationToken);

    public Task HandoverWarrantyClaimAsync(Guid claimId, Guid clientOperationId, string? note,
        CancellationToken cancellationToken = default) => PostActionAsync($"/api/warranty/claims/{claimId:D}/handover", clientOperationId, note, cancellationToken);
    public Task CancelWarrantyClaimAsync(Guid claimId, Guid clientOperationId, string? note,
        CancellationToken cancellationToken = default) => PostActionAsync($"/api/warranty/claims/{claimId:D}/cancel", clientOperationId, note, cancellationToken);

    public Task<Guid> SendShopStockWarrantyAsync(Guid productId, InventoryBucket sourceBucket, decimal quantity,
        Guid supplierId, Guid? sourcePurchaseItemId, string faultDescription, Guid clientOperationId,
        IReadOnlyCollection<Guid>? inventoryUnitIds, CancellationToken cancellationToken = default) =>
        apiClient.PostAsync<SendShopWarrantyRequest, Guid>("/api/warranty/shop-stock/send",
            new(productId, sourceBucket, quantity, supplierId, sourcePurchaseItemId, faultDescription,
                RequireOperation(clientOperationId), inventoryUnitIds), cancellationToken);

    public async Task ReceiveShopStockWarrantyAsync(Guid caseId, WarrantyResolutionType resolution,
        IReadOnlyCollection<Guid>? originalInventoryUnitIds, IReadOnlyList<ReplacementSerializedUnitInput>? replacementUnits,
        string? note, Guid clientOperationId, decimal? supplierCreditAmount, string? supplierReference,
        CancellationToken cancellationToken = default) =>
        _ = await apiClient.PostAsync<ReceiveShopWarrantyRequest, bool>($"/api/warranty/shop-stock/{caseId:D}/receive",
            new(resolution, RequireOperation(clientOperationId), originalInventoryUnitIds, replacementUnits,
                note, supplierCreditAmount, supplierReference), cancellationToken);

    private async Task PostActionAsync(string path, Guid operationId, string? note, CancellationToken cancellationToken) =>
        _ = await apiClient.PostAsync<ActionRequest, bool>(path, new(RequireOperation(operationId), note), cancellationToken);

    private static Guid RequireOperation(Guid operationId) => operationId == Guid.Empty
        ? throw new InvalidOperationException("ClientOperationId is required for this operation.")
        : operationId;

    private static void ValidateCursor(bool first, bool second, bool third, string message)
    {
        if (first != second || second != third)
        {
            throw new ArgumentException(message);
        }
    }

    private sealed record ActionRequest(Guid ClientOperationId, string? Note);
    private sealed record ReverseRequest(Guid ClientOperationId, string Reason);
    private sealed record ResolutionRequest(Guid ClientOperationId, WarrantyResolutionType Resolution, string? Note);
    private sealed record CustomerReplacementRequest(Guid ClientOperationId, IReadOnlyList<CustomerWarrantyReplacementUnitInput> Units, string? Note);
    private sealed record SendShopWarrantyRequest(Guid ProductId, InventoryBucket SourceBucket, decimal BaseQuantity,
        Guid SupplierId, Guid? SourcePurchaseItemId, string FaultDescription, Guid ClientOperationId, IReadOnlyCollection<Guid>? InventoryUnitIds);
    private sealed record ReceiveShopWarrantyRequest(WarrantyResolutionType Resolution, Guid ClientOperationId,
        IReadOnlyCollection<Guid>? OriginalInventoryUnitIds, IReadOnlyList<ReplacementSerializedUnitInput>? ReplacementUnits,
        string? Note, decimal? SupplierCreditAmount, string? SupplierReference);
}
