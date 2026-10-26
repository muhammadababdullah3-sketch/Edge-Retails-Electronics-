using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Production;

namespace EdgeRetails.Application.Production.Printing;

public sealed class PrintPhysicalStickersHandler
{
    private readonly IProductionAuthorization _authorization;
    private readonly IPhysicalStickerDocumentSource _source;
    private readonly IPhysicalStickerPrintEngine _engine;
    private readonly ProductionAuditCoordinator _audit;
    private readonly IClock _clock;
    private readonly IPhysicalLabelPrintIntentCoordinator? _labelIntents;

    public PrintPhysicalStickersHandler(
        IProductionAuthorization authorization,
        IPhysicalStickerDocumentSource source,
        IPhysicalStickerPrintEngine engine,
        ProductionAuditCoordinator audit,
        IClock clock,
        IPhysicalLabelPrintIntentCoordinator? labelPrintIntentCoordinator = null)
    {
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _labelIntents = labelPrintIntentCoordinator;
    }

    public async Task<Result<PrintPhysicalStickersResult>> HandleAsync(
        PrintPhysicalStickersCommand command,
        CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthenticatedAsync(cancellationToken);

        if (command.InventoryUnitIds.Count == 0)
        {
            return Result<PrintPhysicalStickersResult>.Failure(
                "printing.no_units_selected", "No inventory units selected for sticker printing.");
        }

        if (command.IsReprint)
        {
            await _authorization.EnsurePermissionAsync(
                ProductionPermissionNames.PrintingReprint, cancellationToken);
        }

        var results = new List<PrintJobResult>(command.InventoryUnitIds.Count);
        var succeeded = 0;
        var failed = 0;

        var workstationId = Environment.MachineName;
        var allowRetry = command.RequestMode == PrintRequestMode.Retry;

        foreach (var unitId in command.InventoryUnitIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_labelIntents is not null)
            {
                var dispatch = command.IsReprint
                    ? await RegisterAndClaimReprintAsync(unitId, workstationId, cancellationToken)
                    : await _labelIntents.ClaimOriginalForUnitAsync(unitId, workstationId, allowRetry, cancellationToken);
                if (!dispatch.IsSuccess)
                {
                    results.Add(new PrintJobResult(
                        false,
                        dispatch.Error!.Code,
                        dispatch.Error.Message,
                        Guid.NewGuid().ToString("D"),
                        InventoryUnitId: unitId));
                    failed++;
                    continue;
                }
            }

            PhysicalItemStickerDocument doc;
            try
            {
                doc = await _source.LoadStickerDocumentAsync(unitId, command.IsReprint, cancellationToken);
            }
            catch (Exception ex)
            {
                var failResult = new PrintJobResult(
                    false,
                    "print.document_load_failed",
                    $"Sticker document for unit '{unitId}' could not be loaded: {ex.Message}",
                    Guid.NewGuid().ToString("D"));
                results.Add(failResult);
                failed++;

                await _audit.AppendFailureBestEffortAsync(new ProductionAuditRecord(
                    ProductionAuditEvents.DocumentPrintFailed,
                    _clock.UtcNow,
                    "PhysicalItemSticker",
                    unitId.ToString(),
                    $"Error=print.document_load_failed; Detail={ex.Message}",
                    command.CorrelationId));
                continue;
            }

            var printJobId = Guid.NewGuid().ToString("D");
            PrintJobResult physical;
            try
            {
                physical = await _engine.PrintStickerAsync(doc, command.PrinterName, 1, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                physical = new PrintJobResult(
                    false,
                    "print.engine_exception",
                    $"Printer exception: {ex.Message}",
                    printJobId);
            }

            var eventType = physical.Succeeded
                ? (command.IsReprint ? ProductionAuditEvents.DocumentReprinted : ProductionAuditEvents.DocumentPrinted)
                : ProductionAuditEvents.DocumentPrintFailed;

            await _audit.AppendAfterSideEffectAsync(new ProductionAuditRecord(
                eventType,
                _clock.UtcNow,
                "PhysicalItemSticker",
                unitId.ToString(),
                $"TrackingCode={doc.TrackingCode}; Mode={(command.IsReprint ? "Reprint" : "Initial")}; Error={physical.ErrorCode}",
                command.CorrelationId));

            if (_labelIntents is not null)
            {
                await _labelIntents.ApplyStickerReceiptAsync(
                    new ApplyLabelIntentStickerReceipt(
                        unitId,
                        command.IsReprint,
                        physical.Succeeded,
                        physical.ErrorCode,
                        Guid.TryParse(printJobId, out var attemptId) ? attemptId : Guid.CreateVersion7()),
                    cancellationToken);
            }

            if (physical.Succeeded)
            {
                succeeded++;
            }
            else
            {
                failed++;
            }

            results.Add(physical with { PrintJobId = printJobId });
        }

        return Result<PrintPhysicalStickersResult>.Success(new PrintPhysicalStickersResult(
            results,
            command.InventoryUnitIds.Count,
            succeeded,
            failed));
    }

    private async Task<Result<PhysicalLabelPrintIntentSummary>> RegisterAndClaimReprintAsync(
        Guid unitId,
        string workstationId,
        CancellationToken cancellationToken)
    {
        var registered = await _labelIntents!.RegisterReprintIntentAsync(unitId, workstationId, cancellationToken);
        if (!registered.IsSuccess)
        {
            return registered;
        }

        return await _labelIntents.ClaimForDispatchAsync(
            registered.Value!.Id,
            workstationId,
            retryAfterUnknownOutcome: true,
            cancellationToken);
    }
}
