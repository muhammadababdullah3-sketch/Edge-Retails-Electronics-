using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Application.Gateways;
using EdgeRetails.Domain.Inventory;

namespace EdgeRetails.Desktop.Services;

public interface IBackendStockAdjustmentService
{
    Task<Guid> CreateDeltaAdjustmentAsync(
        Guid productId,
        Guid? productUnitId,
        decimal quantity,
        bool increase,
        StockAdjustmentReason reason,
        string? note,
        Guid clientOperationId,
        CancellationToken cancellationToken = default);
}

public sealed class RemoteStockAdjustmentService(
    DesktopApiClient apiClient,
    Func<Guid?> actorUserId) : IBackendStockAdjustmentService
{
    public async Task<Guid> CreateDeltaAdjustmentAsync(
        Guid productId,
        Guid? productUnitId,
        decimal quantity,
        bool increase,
        StockAdjustmentReason reason,
        string? note,
        Guid clientOperationId,
        CancellationToken cancellationToken = default)
    {
        if (clientOperationId == Guid.Empty)
        {
            throw new BackendOperationException("inventory.adjustment_operation_id_required", "Adjustment operation id is required.");
        }

        var actor = actorUserId()
            ?? throw new BackendOperationException("identity.session_required", "A backend user session is required.");
        var command = new CreateStockAdjustmentCommand(
            StockAdjustmentMode.Delta,
            reason,
            [new StockAdjustmentItemCommand(
                productId,
                productUnitId,
                increase ? StockAdjustmentDirection.Increase : StockAdjustmentDirection.Decrease,
                InventoryBucket.Sellable,
                quantity,
                null,
                ReasonDetails: string.IsNullOrWhiteSpace(note) ? null : note.Trim())],
            actor,
            clientOperationId,
            string.IsNullOrWhiteSpace(note) ? null : note.Trim());
        try
        {
            return await apiClient.PostAsync<CreateStockAdjustmentCommand, Guid>(
                "/api/inventory/adjustments", command, cancellationToken);
        }
        catch (DesktopApiException ex) when (
            ex.Code.StartsWith("network.", StringComparison.OrdinalIgnoreCase) ||
            ex.Code.StartsWith("gateway.communication", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var status = await apiClient.GetAsync<OperationStatusResult?>(
                    $"/api/system/operations/{clientOperationId:D}", cancellationToken);
                if (status is { WasCommitted: true, EntityId: Guid adjustmentId } && adjustmentId != Guid.Empty)
                {
                    return adjustmentId;
                }

                if (status is { EffectiveStatus: "NotFound" })
                {
                    return await apiClient.PostAsync<CreateStockAdjustmentCommand, Guid>(
                        "/api/inventory/adjustments", command, cancellationToken);
                }
            }
            catch (DesktopApiException)
            {
                // Do not retry with a new identity when operation status cannot be verified.
            }

            throw new BackendOperationException(
                "inventory.adjustment_outcome_unknown",
                "Adjustment status is not confirmed. Retry using the same operation id.");
        }
    }
}
