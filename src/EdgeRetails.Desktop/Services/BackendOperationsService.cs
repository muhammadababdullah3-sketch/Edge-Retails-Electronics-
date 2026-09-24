using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Warranty;
using EdgeRetails.Application.Gateways;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Warranty;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.Desktop.Services;

public sealed class OperationException : InvalidOperationException
{
    public OperationException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public interface IBackendOperationsService
{
    Task<SupplierAccountWorkspaceDto> GetSupplierWorkspaceAsync(
        Guid supplierId,
        int pageSize = 200,
        DateTimeOffset? beforeOccurredAt = null,
        DateTimeOffset? beforeCreatedAt = null,
        Guid? beforeEntryId = null,
        CancellationToken cancellationToken = default);

    Task CreateSupplierPaymentAsync(
        Guid supplierId,
        decimal amount,
        SupplierPaymentPurpose purpose,
        SupplierSettlementMethod method,
        Guid clientOperationId,
        string? externalReference,
        string? note,
        CancellationToken cancellationToken = default);

    Task ReverseSupplierPaymentAsync(
        Guid paymentId,
        string reason,
        Guid clientOperationId,
        CancellationToken cancellationToken = default);

    Task CreateSupplierRefundAsync(
        Guid supplierId,
        decimal amount,
        SupplierSettlementMethod method,
        Guid clientOperationId,
        string? externalReference,
        string? note,
        CancellationToken cancellationToken = default);

    Task ReverseSupplierRefundAsync(
        Guid refundId,
        string reason,
        Guid clientOperationId,
        CancellationToken cancellationToken = default);

    Task<WarrantyDashboardDto> GetWarrantyDashboardAsync(
        string? search,
        int pageSize = 100,
        DateTimeOffset? beforeCreatedAt = null,
        Guid? beforeWorkId = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WarrantyEventDto>> GetWarrantyClaimTimelineAsync(
        Guid claimId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WarrantyClaimIntakeRowDto>> SearchWarrantyClaimIntakeAsync(
        string search,
        CancellationToken cancellationToken = default);

    Task<Guid> CreateWarrantyClaimAsync(
        Guid customerId,
        Guid saleId,
        Guid? supplierId,
        Guid productId,
        decimal quantity,
        Guid saleItemId,
        string faultDescription,
        IReadOnlyList<WarrantyClaimUnitInput>? units,
        Guid clientOperationId,
        CancellationToken cancellationToken = default);

    Task BeginWarrantyClaimReviewAsync(
        Guid claimId,
        Guid clientOperationId,
        string? note,
        CancellationToken cancellationToken = default);

    Task SendWarrantyClaimToSupplierAsync(
        Guid claimId,
        Guid clientOperationId,
        string? note,
        CancellationToken cancellationToken = default);

    Task MarkWarrantySupplierProcessingAsync(
        Guid claimId,
        Guid clientOperationId,
        string? note,
        CancellationToken cancellationToken = default);

    Task ResolveWarrantyClaimAsync(
        Guid claimId,
        WarrantyResolutionType resolution,
        Guid clientOperationId,
        string? note,
        CancellationToken cancellationToken = default);

    Task ReceiveCustomerWarrantyReplacementAsync(
        Guid claimId,
        IReadOnlyList<CustomerWarrantyReplacementUnitInput> units,
        Guid clientOperationId,
        string? note,
        CancellationToken cancellationToken = default);

    Task HandoverWarrantyClaimAsync(
        Guid claimId,
        Guid clientOperationId,
        string? note,
        CancellationToken cancellationToken = default);

    Task CancelWarrantyClaimAsync(
        Guid claimId,
        Guid clientOperationId,
        string? note,
        CancellationToken cancellationToken = default);

    Task<Guid> SendShopStockWarrantyAsync(
        Guid productId,
        InventoryBucket sourceBucket,
        decimal quantity,
        Guid supplierId,
        Guid? sourcePurchaseItemId,
        string faultDescription,
        Guid clientOperationId,
        IReadOnlyCollection<Guid>? inventoryUnitIds,
        CancellationToken cancellationToken = default);

    Task ReceiveShopStockWarrantyAsync(
        Guid caseId,
        WarrantyResolutionType resolution,
        IReadOnlyCollection<Guid>? originalInventoryUnitIds,
        IReadOnlyList<ReplacementSerializedUnitInput>? replacementUnits,
        string? note,
        Guid clientOperationId,
        decimal? supplierCreditAmount,
        string? supplierReference,
        CancellationToken cancellationToken = default);
}

public sealed class BackendOperationsService : IBackendOperationsService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Func<Guid?> _actorUserId;

    public BackendOperationsService(
        IServiceScopeFactory scopeFactory,
        Func<Guid?> actorUserId)
    {
        _scopeFactory = scopeFactory;
        _actorUserId = actorUserId;
    }

    public async Task<SupplierAccountWorkspaceDto> GetSupplierWorkspaceAsync(
        Guid supplierId,
        int pageSize = 200,
        DateTimeOffset? beforeOccurredAt = null,
        DateTimeOffset? beforeCreatedAt = null,
        Guid? beforeEntryId = null,
        CancellationToken cancellationToken = default)
    {
        var actor = RequireActor();
        await using var scope = _scopeFactory.CreateAsyncScope();
        await AuthorizeAsync(scope.ServiceProvider, actor, PermissionKeys.SupplierAccountView, cancellationToken);
        var reads = scope.ServiceProvider.GetRequiredService<ISupplierAccountReadService>();
        return await reads.GetWorkspaceAsync(
            supplierId,
            Math.Clamp(pageSize, 1, 200),
            beforeOccurredAt,
            beforeCreatedAt,
            beforeEntryId,
            cancellationToken);
    }

    public async Task CreateSupplierPaymentAsync(
        Guid supplierId,
        decimal amount,
        SupplierPaymentPurpose purpose,
        SupplierSettlementMethod method,
        Guid clientOperationId,
        string? externalReference,
        string? note,
        CancellationToken cancellationToken = default)
    {
        var actor = RequireActor();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var gateway = scope.ServiceProvider.GetRequiredService<IApplicationGateway>();
        var result = await gateway.CreateSupplierPaymentAsync(
            new CreateSupplierPaymentCommand(
                supplierId,
                amount,
                purpose,
                method,
                actor,
                clientOperationId,
                Normalize(externalReference),
                Normalize(note)),
            cancellationToken);
        EnsureSuccess(result.IsSuccess, result.Error?.Code, result.Error?.Message, "Supplier payment could not be posted.");
    }

    public async Task ReverseSupplierPaymentAsync(
        Guid paymentId,
        string reason,
        Guid clientOperationId,
        CancellationToken cancellationToken = default)
    {
        var actor = RequireActor();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ReverseSupplierPaymentHandler>();
        var result = await handler.HandleAsync(
            new ReverseSupplierPaymentCommand(
                paymentId,
                Required(reason, "Reversal reason"),
                actor,
                clientOperationId),
            cancellationToken);
        EnsureSuccess(result.IsSuccess, result.Error?.Code, result.Error?.Message, "Supplier payment could not be reversed.");
    }

    public async Task CreateSupplierRefundAsync(
        Guid supplierId,
        decimal amount,
        SupplierSettlementMethod method,
        Guid clientOperationId,
        string? externalReference,
        string? note,
        CancellationToken cancellationToken = default)
    {
        var actor = RequireActor();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var gateway = scope.ServiceProvider.GetRequiredService<IApplicationGateway>();
        var result = await gateway.CreateSupplierRefundAsync(
            new CreateSupplierRefundCommand(
                supplierId,
                amount,
                method,
                actor,
                clientOperationId,
                Normalize(externalReference),
                null,
                null,
                Normalize(note)),
            cancellationToken);
        EnsureSuccess(result.IsSuccess, result.Error?.Code, result.Error?.Message, "Supplier refund could not be posted.");
    }

    public async Task ReverseSupplierRefundAsync(
        Guid refundId,
        string reason,
        Guid clientOperationId,
        CancellationToken cancellationToken = default)
    {
        var actor = RequireActor();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ReverseSupplierRefundHandler>();
        var result = await handler.HandleAsync(
            new ReverseSupplierRefundCommand(
                refundId,
                Required(reason, "Reversal reason"),
                actor,
                clientOperationId),
            cancellationToken);
        EnsureSuccess(result.IsSuccess, result.Error?.Code, result.Error?.Message, "Supplier refund could not be reversed.");
    }

    public async Task<WarrantyDashboardDto> GetWarrantyDashboardAsync(
        string? search,
        int pageSize = 100,
        DateTimeOffset? beforeCreatedAt = null,
        Guid? beforeWorkId = null,
        CancellationToken cancellationToken = default)
    {
        var actor = RequireActor();
        await using var scope = _scopeFactory.CreateAsyncScope();
        await AuthorizeAsync(scope.ServiceProvider, actor, PermissionKeys.WarrantyView, cancellationToken);
        var reads = scope.ServiceProvider.GetRequiredService<IWarrantyReadService>();
        return await reads.GetDashboardAsync(
            Normalize(search),
            Math.Clamp(pageSize, 1, 200),
            beforeCreatedAt,
            beforeWorkId,
            cancellationToken);
    }

    public async Task<IReadOnlyList<WarrantyEventDto>> GetWarrantyClaimTimelineAsync(
        Guid claimId,
        CancellationToken cancellationToken = default)
    {
        var actor = RequireActor();
        await using var scope = _scopeFactory.CreateAsyncScope();
        await AuthorizeAsync(scope.ServiceProvider, actor, PermissionKeys.WarrantyView, cancellationToken);
        var reads = scope.ServiceProvider.GetRequiredService<IWarrantyReadService>();
        return await reads.GetClaimTimelineAsync(claimId, cancellationToken);
    }

    public async Task<IReadOnlyList<WarrantyClaimIntakeRowDto>> SearchWarrantyClaimIntakeAsync(
        string search,
        CancellationToken cancellationToken = default)
    {
        var actor = RequireActor();
        await using var scope = _scopeFactory.CreateAsyncScope();
        await AuthorizeAsync(scope.ServiceProvider, actor, PermissionKeys.WarrantyView, cancellationToken);
        var reads = scope.ServiceProvider.GetRequiredService<IWarrantyReadService>();
        return await reads.SearchClaimIntakeAsync(Required(search, "Warranty search"), cancellationToken);
    }

    public async Task<Guid> CreateWarrantyClaimAsync(
        Guid customerId,
        Guid saleId,
        Guid? supplierId,
        Guid productId,
        decimal quantity,
        Guid saleItemId,
        string faultDescription,
        IReadOnlyList<WarrantyClaimUnitInput>? units,
        Guid clientOperationId,
        CancellationToken cancellationToken = default)
    {
        var actor = RequireActor();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var gateway = scope.ServiceProvider.GetRequiredService<IApplicationGateway>();
        var result = await gateway.CreateWarrantyClaimAsync(
            new CreateWarrantyClaimCommand(
                customerId,
                saleId,
                supplierId,
                actor,
                new[]
                {
                    new WarrantyClaimItemInput(
                        productId,
                        quantity,
                        Required(faultDescription, "Fault description"),
                        saleItemId,
                        null,
                        units)
                },
                clientOperationId),
            cancellationToken);
        if (!result.IsSuccess)
        {
            throw new OperationException(
                result.Error?.Code ?? "warranty.claim_create_failed",
                result.Error?.Message ?? "Warranty claim could not be created.");
        }

        return result.Value;
    }

    public Task BeginWarrantyClaimReviewAsync(
        Guid claimId,
        Guid clientOperationId,
        string? note,
        CancellationToken cancellationToken = default) =>
        ExecuteWarrantyAsync<BeginWarrantyClaimReviewHandler>(
            (handler, actor) => handler.HandleAsync(
                new BeginWarrantyClaimReviewCommand(claimId, actor, clientOperationId, Normalize(note)),
                cancellationToken),
            "Warranty claim could not begin review.",
            cancellationToken);

    public Task SendWarrantyClaimToSupplierAsync(
        Guid claimId,
        Guid clientOperationId,
        string? note,
        CancellationToken cancellationToken = default) =>
        ExecuteWarrantyAsync<SendWarrantyClaimToSupplierHandler>(
            (handler, actor) => handler.HandleAsync(
                new SendWarrantyClaimToSupplierCommand(claimId, actor, clientOperationId, Normalize(note)),
                cancellationToken),
            "Warranty claim could not be sent to Supplier.",
            cancellationToken);

    public Task MarkWarrantySupplierProcessingAsync(
        Guid claimId,
        Guid clientOperationId,
        string? note,
        CancellationToken cancellationToken = default) =>
        ExecuteWarrantyAsync<MarkWarrantySupplierProcessingHandler>(
            (handler, actor) => handler.HandleAsync(
                new MarkWarrantySupplierProcessingCommand(claimId, actor, clientOperationId, Normalize(note)),
                cancellationToken),
            "Warranty claim could not be marked as Supplier processing.",
            cancellationToken);

    public Task ResolveWarrantyClaimAsync(
        Guid claimId,
        WarrantyResolutionType resolution,
        Guid clientOperationId,
        string? note,
        CancellationToken cancellationToken = default) =>
        ExecuteWarrantyAsync<RecordWarrantyResolutionHandler>(
            (handler, actor) => handler.HandleAsync(
                new RecordWarrantyResolutionCommand(claimId, resolution, actor, clientOperationId, Normalize(note)),
                cancellationToken),
            "Warranty claim could not be resolved.",
            cancellationToken);

    public async Task ReceiveCustomerWarrantyReplacementAsync(
        Guid claimId,
        IReadOnlyList<CustomerWarrantyReplacementUnitInput> units,
        Guid clientOperationId,
        string? note,
        CancellationToken cancellationToken = default)
    {
        var actor = RequireActor();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ReceiveCustomerWarrantyReplacementHandler>();
        var result = await handler.HandleAsync(
            new ReceiveCustomerWarrantyReplacementCommand(
                claimId,
                actor,
                clientOperationId,
                units,
                Normalize(note)),
            cancellationToken);
        EnsureSuccess(
            result.IsSuccess,
            result.Error?.Code,
            result.Error?.Message,
            "Customer Warranty replacement could not be recorded.");
    }

    public Task HandoverWarrantyClaimAsync(
        Guid claimId,
        Guid clientOperationId,
        string? note,
        CancellationToken cancellationToken = default) =>
        ExecuteWarrantyAsync<HandoverWarrantyItemHandler>(
            (handler, actor) => handler.HandleAsync(
                new HandoverWarrantyItemCommand(claimId, actor, clientOperationId, Normalize(note)),
                cancellationToken),
            "Warranty item could not be handed to Customer.",
            cancellationToken);

    public Task CancelWarrantyClaimAsync(
        Guid claimId,
        Guid clientOperationId,
        string? note,
        CancellationToken cancellationToken = default) =>
        ExecuteWarrantyAsync<CancelWarrantyClaimHandler>(
            (handler, actor) => handler.HandleAsync(
                new CancelWarrantyClaimCommand(claimId, actor, clientOperationId, Normalize(note)),
                cancellationToken),
            "Warranty claim could not be cancelled.",
            cancellationToken);

    public async Task<Guid> SendShopStockWarrantyAsync(
        Guid productId,
        InventoryBucket sourceBucket,
        decimal quantity,
        Guid supplierId,
        Guid? sourcePurchaseItemId,
        string faultDescription,
        Guid clientOperationId,
        IReadOnlyCollection<Guid>? inventoryUnitIds,
        CancellationToken cancellationToken = default)
    {
        var actor = RequireActor();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<SendShopStockToSupplierWarrantyHandler>();
        var result = await handler.HandleAsync(
            new SendShopStockToSupplierWarrantyCommand(
                productId,
                sourceBucket,
                quantity,
                supplierId,
                sourcePurchaseItemId,
                Required(faultDescription, "Fault description"),
                actor,
                clientOperationId,
                inventoryUnitIds),
            cancellationToken);
        if (!result.IsSuccess)
        {
            throw new OperationException(
                result.Error?.Code ?? "phase5.operation_failed",
                result.Error?.Message ?? "Shop-stock warranty could not be sent to Supplier.");
        }

        return result.Value;
    }

    public async Task ReceiveShopStockWarrantyAsync(
        Guid caseId,
        WarrantyResolutionType resolution,
        IReadOnlyCollection<Guid>? originalInventoryUnitIds,
        IReadOnlyList<ReplacementSerializedUnitInput>? replacementUnits,
        string? note,
        Guid clientOperationId,
        decimal? supplierCreditAmount,
        string? supplierReference,
        CancellationToken cancellationToken = default)
    {
        var actor = RequireActor();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ReceiveShopStockWarrantyHandler>();
        var result = await handler.HandleAsync(
            new ReceiveShopStockWarrantyCommand(
                caseId,
                resolution,
                actor,
                originalInventoryUnitIds,
                replacementUnits,
                Normalize(note),
                clientOperationId,
                supplierCreditAmount,
                Normalize(supplierReference)),
            cancellationToken);
        EnsureSuccess(
            result.IsSuccess,
            result.Error?.Code,
            result.Error?.Message,
            "Shop-stock warranty receipt could not be recorded.");
    }

    private async Task ExecuteWarrantyAsync<THandler>(
        Func<THandler, Guid, Task<EdgeRetails.Application.Common.Result>> operation,
        string fallbackMessage,
        CancellationToken cancellationToken)
        where THandler : notnull
    {
        var actor = RequireActor();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<THandler>();
        var result = await operation(handler, actor);
        EnsureSuccess(result.IsSuccess, result.Error?.Code, result.Error?.Message, fallbackMessage);
    }

    private static async Task AuthorizeAsync(
        IServiceProvider services,
        Guid actor,
        string permission,
        CancellationToken cancellationToken)
    {
        var authorization = services.GetRequiredService<IApplicationPermissionAuthorizer>();
        var result = await authorization.AuthorizeAsync(actor, permission, cancellationToken);
        EnsureSuccess(result.IsSuccess, result.Error?.Code, result.Error?.Message, "Permission denied.");
    }

    private Guid RequireActor() =>
        _actorUserId()
        ?? throw new InvalidOperationException(
            "A persistent backend user session is required for this operation.");

    private static void EnsureSuccess(
        bool isSuccess,
        string? code,
        string? message,
        string fallback)
    {
        if (!isSuccess)
        {
            throw new OperationException(
                code ?? "phase5.operation_failed",
                message ?? fallback);
        }
    }

    private static string Required(string? value, string field)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new OperationException(
                "phase5.validation_required",
                $"{field} is required.");
        }

        return normalized;
    }

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
