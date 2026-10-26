using EdgeRetails.Application.Common;
using EdgeRetails.Application.Production.Printing;
using EdgeRetails.Domain.Production;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Infrastructure.Production.Printing;

public sealed class EfPhysicalLabelPrintIntentCoordinator(EdgeRetailsDbContext db) : IPhysicalLabelPrintIntentCoordinator
{
    public static readonly TimeSpan DispatchLeaseDuration = TimeSpan.FromMinutes(10);

    public async Task<IReadOnlyList<PhysicalLabelPrintIntentSummary>> ListPendingAsync(
        Guid? sourceClientOperationId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        var now = DateTimeOffset.UtcNow;
        var query = db.PhysicalLabelPrintIntents.AsNoTracking()
            .Where(x => x.Status == PhysicalLabelPrintIntentStatus.PendingDispatch
                || (x.Status == PhysicalLabelPrintIntentStatus.Dispatched && x.UpdatedAt + DispatchLeaseDuration < now));

        if (sourceClientOperationId is Guid opId && opId != Guid.Empty)
        {
            query = query.Where(x => x.SourceClientOperationId == opId);
        }

        var rows = await query
            .OrderBy(x => x.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return rows.Select(ToSummary).ToArray();
    }

    public async Task<Result<PhysicalLabelPrintIntentSummary>> ClaimForDispatchAsync(
        Guid intentId,
        string workstationId,
        bool retryAfterUnknownOutcome,
        CancellationToken cancellationToken = default)
    {
        if (intentId == Guid.Empty)
        {
            return Result<PhysicalLabelPrintIntentSummary>.Failure(
                "printing.label_intent_invalid",
                "A valid label print intent is required.");
        }

        workstationId = NormalizeWorkstation(workstationId);
        var intent = await db.PhysicalLabelPrintIntents
            .FirstOrDefaultAsync(x => x.Id == intentId, cancellationToken);
        if (intent is null)
        {
            return Result<PhysicalLabelPrintIntentSummary>.Failure(
                "printing.label_intent_not_found",
                "The durable label print intent could not be found.");
        }

        return TryClaim(intent, workstationId, retryAfterUnknownOutcome);
    }

    public async Task<Result<PhysicalLabelPrintIntentSummary>> ClaimOriginalForUnitAsync(
        Guid inventoryUnitId,
        string workstationId,
        bool retryAfterUnknownOutcome,
        CancellationToken cancellationToken = default)
    {
        if (inventoryUnitId == Guid.Empty)
        {
            return Result<PhysicalLabelPrintIntentSummary>.Failure(
                "printing.no_units_selected",
                "Select a committed unit before dispatching its ORIGINAL label.");
        }

        workstationId = NormalizeWorkstation(workstationId);
        var intent = await db.PhysicalLabelPrintIntents
            .FirstOrDefaultAsync(
                x => x.InventoryUnitId == inventoryUnitId && x.Kind == PhysicalLabelPrintIntentKind.Original,
                cancellationToken);
        if (intent is null)
        {
            return Result<PhysicalLabelPrintIntentSummary>.Failure(
                "printing.label_intent_not_found",
                "No ORIGINAL label intent exists for this unit.");
        }

        return TryClaim(intent, workstationId, retryAfterUnknownOutcome);
    }

    public async Task<Result<PhysicalLabelPrintIntentSummary>> RegisterReprintIntentAsync(
        Guid inventoryUnitId,
        string workstationId,
        CancellationToken cancellationToken = default)
    {
        if (inventoryUnitId == Guid.Empty)
        {
            return Result<PhysicalLabelPrintIntentSummary>.Failure(
                "printing.no_units_selected",
                "Select a committed unit before reprinting its label.");
        }

        workstationId = NormalizeWorkstation(workstationId);
        var now = DateTimeOffset.UtcNow;
        var intent = new PhysicalLabelPrintIntent
        {
            Id = Guid.CreateVersion7(),
            InventoryUnitId = inventoryUnitId,
            SourceClientOperationId = Guid.Empty,
            Kind = PhysicalLabelPrintIntentKind.Reprint,
            Status = PhysicalLabelPrintIntentStatus.PendingDispatch,
            TargetWorkstationId = workstationId,
            AttemptCount = 0,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1
        };
        db.PhysicalLabelPrintIntents.Add(intent);
        await db.SaveChangesAsync(cancellationToken);
        return Result<PhysicalLabelPrintIntentSummary>.Success(ToSummary(intent));
    }

    public async Task ApplyStickerReceiptAsync(
        ApplyLabelIntentStickerReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        if (receipt.InventoryUnitId == Guid.Empty)
        {
            return;
        }

        var kind = receipt.IsReprint
            ? PhysicalLabelPrintIntentKind.Reprint
            : PhysicalLabelPrintIntentKind.Original;

        var intent = await db.PhysicalLabelPrintIntents
            .Where(x => x.InventoryUnitId == receipt.InventoryUnitId && x.Kind == kind)
            .OrderByDescending(x => x.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (intent is null
            || intent.Status is PhysicalLabelPrintIntentStatus.Succeeded
                or PhysicalLabelPrintIntentStatus.Cancelled)
        {
            return;
        }

        intent.Status = MapReceiptStatus(receipt);
        intent.UpdatedAt = DateTimeOffset.UtcNow;
        intent.Version++;
        await db.SaveChangesAsync(cancellationToken);
    }

    private Result<PhysicalLabelPrintIntentSummary> TryClaim(
        PhysicalLabelPrintIntent intent,
        string workstationId,
        bool retryAfterUnknownOutcome)
    {
        var now = DateTimeOffset.UtcNow;
        if (intent.Status == PhysicalLabelPrintIntentStatus.OutcomeUnknown && !retryAfterUnknownOutcome)
        {
            return Result<PhysicalLabelPrintIntentSummary>.Failure(
                "print.outcome_unknown",
                "The printer response could not be confirmed. Check the physical printer before choosing Reprint.");
        }

        if (intent.Status == PhysicalLabelPrintIntentStatus.Succeeded)
        {
            return Result<PhysicalLabelPrintIntentSummary>.Success(ToSummary(intent));
        }

        if (intent.Status == PhysicalLabelPrintIntentStatus.Failed && !retryAfterUnknownOutcome)
        {
            return Result<PhysicalLabelPrintIntentSummary>.Failure(
                "printing.label_intent_failed",
                "The label intent failed. Verify the printer, then retry explicitly or use Reprint.");
        }

        var leaseExpired = intent.Status == PhysicalLabelPrintIntentStatus.Dispatched
            && intent.UpdatedAt + DispatchLeaseDuration < now;

        if (intent.Status != PhysicalLabelPrintIntentStatus.PendingDispatch
            && intent.Status != PhysicalLabelPrintIntentStatus.OutcomeUnknown
            && intent.Status != PhysicalLabelPrintIntentStatus.Failed
            && !leaseExpired)
        {
            if (intent.Status == PhysicalLabelPrintIntentStatus.Dispatched
                && string.Equals(intent.TargetWorkstationId, workstationId, StringComparison.OrdinalIgnoreCase))
            {
                return Result<PhysicalLabelPrintIntentSummary>.Success(ToSummary(intent));
            }

            return Result<PhysicalLabelPrintIntentSummary>.Failure(
                "printing.label_intent_dispatch_active",
                "Another workstation holds the active dispatch lease for this label intent.");
        }

        intent.Status = PhysicalLabelPrintIntentStatus.Dispatched;
        intent.TargetWorkstationId = workstationId;
        intent.AttemptCount++;
        intent.UpdatedAt = now;
        intent.Version++;
        try
        {
            db.SaveChanges();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result<PhysicalLabelPrintIntentSummary>.Failure(
                "persistence.concurrency_conflict",
                "The label dispatch lease changed concurrently. Retry the print request.");
        }

        return Result<PhysicalLabelPrintIntentSummary>.Success(ToSummary(intent));
    }

    private static PhysicalLabelPrintIntentStatus MapReceiptStatus(ApplyLabelIntentStickerReceipt receipt)
    {
        if (string.Equals(receipt.ErrorCode, "print.outcome_unknown", StringComparison.OrdinalIgnoreCase))
        {
            return PhysicalLabelPrintIntentStatus.OutcomeUnknown;
        }

        return receipt.Submitted
            ? PhysicalLabelPrintIntentStatus.Succeeded
            : PhysicalLabelPrintIntentStatus.Failed;
    }

    private static string NormalizeWorkstation(string workstationId)
    {
        if (string.IsNullOrWhiteSpace(workstationId))
        {
            return Environment.MachineName;
        }

        return workstationId.Trim();
    }

    private static PhysicalLabelPrintIntentSummary ToSummary(PhysicalLabelPrintIntent intent) =>
        new(
            intent.Id,
            intent.InventoryUnitId,
            intent.SourceClientOperationId,
            intent.Kind,
            intent.Status,
            intent.AttemptCount,
            intent.TargetWorkstationId);
}
