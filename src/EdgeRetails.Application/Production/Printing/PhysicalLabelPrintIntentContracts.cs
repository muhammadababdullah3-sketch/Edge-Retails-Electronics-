using EdgeRetails.Application.Common;
using EdgeRetails.Domain.Production;

namespace EdgeRetails.Application.Production.Printing;

public sealed record PhysicalLabelPrintIntentSummary(
    Guid Id,
    Guid InventoryUnitId,
    Guid SourceClientOperationId,
    PhysicalLabelPrintIntentKind Kind,
    PhysicalLabelPrintIntentStatus Status,
    int AttemptCount,
    string? TargetWorkstationId);

public sealed record LabelIntentClaimRequest(string WorkstationId, bool RetryAfterUnknownOutcome = false);

public sealed record LabelIntentClaimByUnitRequest(
    Guid InventoryUnitId,
    string WorkstationId,
    bool RetryAfterUnknownOutcome = false);

public sealed record ApplyLabelIntentStickerReceipt(
    Guid InventoryUnitId,
    bool IsReprint,
    bool Submitted,
    string? ErrorCode,
    Guid ClientPrintAttemptId);

public interface IPhysicalLabelPrintIntentCoordinator
{
    Task<IReadOnlyList<PhysicalLabelPrintIntentSummary>> ListPendingAsync(
        Guid? sourceClientOperationId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<Result<PhysicalLabelPrintIntentSummary>> ClaimForDispatchAsync(
        Guid intentId,
        string workstationId,
        bool retryAfterUnknownOutcome,
        CancellationToken cancellationToken = default);

    Task<Result<PhysicalLabelPrintIntentSummary>> ClaimOriginalForUnitAsync(
        Guid inventoryUnitId,
        string workstationId,
        bool retryAfterUnknownOutcome,
        CancellationToken cancellationToken = default);

    Task<Result<PhysicalLabelPrintIntentSummary>> RegisterReprintIntentAsync(
        Guid inventoryUnitId,
        string workstationId,
        CancellationToken cancellationToken = default);

    Task ApplyStickerReceiptAsync(
        ApplyLabelIntentStickerReceipt receipt,
        CancellationToken cancellationToken = default);
}
