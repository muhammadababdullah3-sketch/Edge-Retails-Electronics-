using System.Net.Http;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Application.Gateways;
using EdgeRetails.Domain.Inventory;

namespace EdgeRetails.Desktop.Services;

/// <summary>
/// Routes the shared workflow read surface to Server and owns the remote stocktake UX contract.
/// </summary>
public sealed class RemoteStocktakeWorkflowService(
    DesktopApiClient apiClient,
    IClientOperationIntentStore? operationIntents = null) : IBackendWorkflowReadService
{
    private readonly RemotePosWorkflowService _pos = new(apiClient);
    private readonly IClientOperationIntentStore _operationIntents = operationIntents ?? new FileClientOperationIntentStore();

    public Task<IReadOnlyList<BackendScannerMatch>> ResolveScannerAsync(
        string input, CancellationToken cancellationToken = default) =>
        _pos.ResolveScannerAsync(input, cancellationToken);

    public Task<IReadOnlyList<BackendExactUnit>> GetExactUnitsAsync(
        Guid productId,
        InventoryUnitStatus? status = null,
        Guid? sourcePurchaseItemId = null,
        CancellationToken cancellationToken = default) =>
        LoadExactUnitsAsync(productId, status, sourcePurchaseItemId, cancellationToken);

    public Task<IReadOnlyList<BackendPosDraftSummary>> GetOpenDraftsAsync(
        CancellationToken cancellationToken = default) =>
        _pos.GetOpenDraftsAsync(cancellationToken);

    public Task<BackendPosDraftDetail?> GetDraftAsync(
        Guid draftId, CancellationToken cancellationToken = default) =>
        _pos.GetDraftAsync(draftId, cancellationToken);

    public Task<BackendPosDraftState> SaveDraftAsync(
        Guid? draftId,
        long? expectedVersion,
        Guid? customerId,
        string? note,
        IReadOnlyList<BackendPosDraftItemInput> items,
        Guid clientOperationId,
        CancellationToken cancellationToken = default) =>
        _pos.SaveDraftAsync(draftId, expectedVersion, customerId, note, items, clientOperationId, cancellationToken);

    public Task CancelDraftAsync(
        Guid draftId,
        long? expectedVersion,
        CancellationToken cancellationToken = default) =>
        _pos.CancelDraftAsync(draftId, expectedVersion, cancellationToken);

    public async Task<BackendStocktakeSnapshot?> GetOpenStocktakeAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await apiClient.GetAsync<OpenStocktakeApiResponse>(
            "/api/inventory/stocktake/open", cancellationToken);
        var snapshot = response.Stocktake;
        return snapshot is null ? null : MapStocktake(snapshot);
    }

    public async Task<BackendStocktakeSnapshot> StartFullShopStocktakeAsync(
        string? note,
        Guid? clientOperationId = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedNote = Normalize(note);
        var createKey = "stocktake:create";
        var createPayload = $"FullShop|{normalizedNote}";
        var existing = await GetOpenStocktakeAsync(cancellationToken);
        var createOperationId = clientOperationId is { } supplied && supplied != Guid.Empty
            ? GetOperationId(createKey, createPayload, supplied)
            : (Guid?)null;
        if (existing is not null)
        {
            if (existing.Status == StocktakeStatus.Draft)
            {
                var startKey = $"start:{existing.StocktakeId:D}";
                var startPayload = existing.StocktakeId.ToString("D");
                await PostSuccessAsync(
                    $"/api/inventory/stocktake/{existing.StocktakeId:D}/start",
                    startKey, "Stocktake.Start", startPayload,
                    new StocktakeActionRequest(GetOperationId(startKey, startPayload)), cancellationToken);
            }

            var existingSnapshot = existing.Status == StocktakeStatus.Draft
                ? await ReadRequiredSnapshotAsync(cancellationToken)
                : existing;
            if (createOperationId.HasValue)
            {
                _operationIntents.Complete(createKey, createOperationId.Value);
            }
            return existingSnapshot;
        }

        var operationId = createOperationId
            ?? throw new BackendOperationException(
                "inventory.stocktake_operation_id_required",
                "A stocktake operation id is required before starting a count.");

        Guid stocktakeId;
        try
        {
            stocktakeId = await CreateStocktakeAsync(operationId, normalizedNote, cancellationToken);
        }
        catch (DesktopApiException ex) when (IsAmbiguous(ex))
        {
            stocktakeId = await RecoverCreatedStocktakeAsync(operationId, normalizedNote, cancellationToken);
        }
        catch (DesktopApiException ex) when (IsDefinitiveRejection(ex.StatusCode))
        {
            _operationIntents.Complete(createKey, operationId);
            throw;
        }

        _operationIntents.Complete(createKey, operationId);

        var startOperationKey = $"start:{stocktakeId:D}";
        var startOperationPayload = stocktakeId.ToString("D");
        await PostSuccessAsync(
            $"/api/inventory/stocktake/{stocktakeId:D}/start",
            startOperationKey, "Stocktake.Start", startOperationPayload,
            new StocktakeActionRequest(GetOperationId(startOperationKey, startOperationPayload)), cancellationToken);
        return await ReadRequiredSnapshotAsync(cancellationToken);
    }

    public async Task RecordStocktakeCountAsync(
        Guid stocktakeId,
        Guid productId,
        decimal countedSellableQty,
        string? reviewNote,
        CancellationToken cancellationToken = default)
    {
        var key = $"count:{stocktakeId:D}:{productId:D}";
        var payload = FormattableString.Invariant(
            $"{stocktakeId:D}|{productId:D}|{countedSellableQty:G29}|{Normalize(reviewNote)}");
        await PostSuccessAsync(
            $"/api/inventory/stocktake/{stocktakeId:D}/counts",
            key, "Stocktake.Count", payload,
            new StocktakeCountRequest(productId, countedSellableQty, Normalize(reviewNote), GetOperationId(key, payload)), cancellationToken);
    }

    public async Task RecordSerializedStocktakeAsync(
        Guid stocktakeId,
        Guid productId,
        IReadOnlyCollection<Guid> foundInventoryUnitIds,
        IReadOnlyCollection<string> unexpectedIdentitySnapshots,
        string? reviewNote,
        CancellationToken cancellationToken = default)
    {
        var key = $"serialized-count:{stocktakeId:D}:{productId:D}";
        var payload = $"{stocktakeId:D}|{productId:D}|{string.Join(',', foundInventoryUnitIds.Order())}|{string.Join('|', unexpectedIdentitySnapshots.Order(StringComparer.Ordinal))}|{Normalize(reviewNote)}";
        await PostSuccessAsync(
            $"/api/inventory/stocktake/{stocktakeId:D}/serialized-counts",
            key, "Stocktake.SerializedCount", payload,
            new SerializedStocktakeCountRequest(
                productId,
                foundInventoryUnitIds,
                unexpectedIdentitySnapshots,
                Normalize(reviewNote), GetOperationId(key, payload)), cancellationToken);
    }

    public async Task ReviewStocktakeAsync(
        Guid stocktakeId,
        CancellationToken cancellationToken = default)
    {
        var key = $"review:{stocktakeId:D}";
        var payload = stocktakeId.ToString("D");
        await PostSuccessAsync(
            $"/api/inventory/stocktake/{stocktakeId:D}/review",
            key, "Stocktake.Review", payload,
            new StocktakeActionRequest(GetOperationId(key, payload)), cancellationToken);
    }

    public async Task PostStocktakeAsync(
        Guid stocktakeId,
        Guid? clientOperationId = null,
        CancellationToken cancellationToken = default)
    {
        var operationId = clientOperationId is { } supplied && supplied != Guid.Empty
            ? GetOperationId($"post:{stocktakeId:D}", stocktakeId.ToString("D"), supplied)
            : throw new BackendOperationException(
                "inventory.stocktake_operation_id_required",
                "A stocktake operation id is required before posting.");
        await PostSuccessAsync($"/api/inventory/stocktake/{stocktakeId:D}/post",
            $"post:{stocktakeId:D}", "Stocktake", stocktakeId.ToString("D"),
            new StocktakePostRequest(operationId, null), cancellationToken);
    }

    public async Task CancelStocktakeAsync(
        Guid stocktakeId,
        CancellationToken cancellationToken = default)
    {
        var key = $"cancel:{stocktakeId:D}";
        var payload = stocktakeId.ToString("D");
        await PostSuccessAsync(
            $"/api/inventory/stocktake/{stocktakeId:D}/cancel",
            key, "Stocktake.Cancel", payload,
            new StocktakeActionRequest(GetOperationId(key, payload)), cancellationToken);
    }

    private async Task<Guid> CreateStocktakeAsync(
        Guid operationId,
        string? note,
        CancellationToken cancellationToken)
    {
        var response = await apiClient.PostAsync<CreateStocktakeCommand, Guid>(
            "/api/inventory/stocktake",
            new CreateStocktakeCommand(
                StocktakeScope.FullShop,
                null,
                Guid.Empty,
                Normalize(note),
                operationId),
            cancellationToken);
        if (response == Guid.Empty)
        {
            throw new DesktopApiException(
                "inventory.stocktake_create_failed",
                "Server did not return a stocktake identifier.");
        }

        return response;
    }

    private async Task<Guid> RecoverCreatedStocktakeAsync(
        Guid operationId,
        string? note,
        CancellationToken cancellationToken)
    {
        try
        {
            var status = await QueryOperationAsync(operationId, cancellationToken);
            if (status is { WasCommitted: true, OperationType: "CreateStocktake", EntityId: Guid id } && id != Guid.Empty)
            {
                return id;
            }

            if (status is { EffectiveStatus: "NotFound" })
            {
                try
                {
                    return await CreateStocktakeAsync(operationId, note, cancellationToken);
                }
                catch (DesktopApiException definitiveException) when (IsDefinitiveRejection(definitiveException.StatusCode))
                {
                    _operationIntents.Complete("stocktake:create", operationId);
                    throw;
                }
            }
        }
        catch (Exception recoveryException) when (recoveryException is HttpRequestException ||
            recoveryException is DesktopApiException desktopException && IsAmbiguous(desktopException))
        {
            // A failed status lookup cannot establish whether Server committed the request.
        }

        throw new BackendOperationException(
            "inventory.stocktake_outcome_unknown",
            "Stocktake creation status is not confirmed. Retry using the same operation id.");
    }

    private Task<OperationStatusResult?> QueryOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken) =>
        apiClient.GetAsync<OperationStatusResult?>(
            $"/api/system/operations/{operationId:D}", cancellationToken);

    private async Task<BackendStocktakeSnapshot> ReadRequiredSnapshotAsync(
        CancellationToken cancellationToken) =>
        await GetOpenStocktakeAsync(cancellationToken)
        ?? throw new BackendOperationException(
            "inventory.stocktake_readback_missing",
            "Stocktake operation completed but the open stocktake could not be read back.");

    private async Task PostSuccessAsync<TRequest>(
        string path,
        TRequest payload,
        CancellationToken cancellationToken)
    {
        var response = await apiClient.PostAsync<TRequest, SuccessResponse>(
            path, payload, cancellationToken);
        if (!response.Success)
        {
            throw new DesktopApiException("gateway.invalid_response", "Server did not confirm the operation.");
        }
    }

    private async Task PostSuccessAsync<TRequest>(
        string path,
        string operationKey,
        string operationType,
        string payloadFingerprint,
        TRequest payload,
        CancellationToken cancellationToken)
    {
        var operationId = payload switch
        {
            StocktakeCountRequest value => value.ClientOperationId,
            SerializedStocktakeCountRequest value => value.ClientOperationId,
            StocktakeActionRequest value => value.ClientOperationId,
            StocktakePostRequest value => value.ClientOperationId ?? Guid.Empty,
            _ => Guid.Empty
        };
        try
        {
            var response = await apiClient.PostAsync<TRequest, SuccessResponse>(path, payload, cancellationToken);
            if (!response.Success)
            {
                throw new DesktopApiException("gateway.invalid_response", "Server did not confirm the operation.");
            }
            _operationIntents.Complete(operationKey, operationId);
        }
        catch (DesktopApiException ex) when (IsDefinitiveRejection(ex.StatusCode))
        {
            _operationIntents.Complete(operationKey, operationId);
            throw;
        }
        catch (DesktopApiException ex) when (IsAmbiguous(ex))
        {
            try
            {
                var status = await QueryOperationAsync(operationId, cancellationToken);
                if (status is { WasCommitted: true } && status.OperationType == operationType)
                {
                    _operationIntents.Complete(operationKey, operationId);
                    return;
                }
                if (status is { EffectiveStatus: "NotFound" })
                {
                    try
                    {
                        var replay = await apiClient.PostAsync<TRequest, SuccessResponse>(path, payload, cancellationToken);
                        if (replay.Success)
                        {
                            _operationIntents.Complete(operationKey, operationId);
                            return;
                        }
                    }
                    catch (DesktopApiException definitiveReplayException) when (IsDefinitiveRejection(definitiveReplayException.StatusCode))
                    {
                        _operationIntents.Complete(operationKey, operationId);
                        throw;
                    }
                }
            }
            catch (DesktopApiException recoveryException) when (IsAmbiguous(recoveryException))
            {
                // Preserve the same pending id until Server can establish its outcome.
            }
            throw new BackendOperationException("inventory.stocktake_outcome_unknown",
                $"Stocktake operation status is not confirmed. Retry using the same operation intent ({payloadFingerprint}).");
        }
    }

    private Guid GetOperationId(string key, string payload, Guid? preferredOperationId = null)
    {
        try
        {
            return preferredOperationId is Guid preferredId
                ? _operationIntents.GetOrCreate(key, payload, preferredId)
                : _operationIntents.GetOrCreate(key, payload);
        }
        catch (InvalidOperationException)
        {
            throw new BackendOperationException("inventory.stocktake_outcome_unknown",
                "The previous stocktake outcome is unknown. Reconcile it before changing the operation.");
        }
    }

    private static bool IsDefinitiveRejection(System.Net.HttpStatusCode? status) =>
        status is >= System.Net.HttpStatusCode.BadRequest and < System.Net.HttpStatusCode.InternalServerError;

    private static bool IsAmbiguous(DesktopApiException exception) =>
        IsTransportFailure(exception.Code) || exception.StatusCode >= System.Net.HttpStatusCode.InternalServerError;

    private async Task<IReadOnlyList<BackendExactUnit>> LoadExactUnitsAsync(
        Guid productId,
        InventoryUnitStatus? status,
        Guid? sourcePurchaseItemId,
        CancellationToken cancellationToken)
    {
        var query = $"/api/inventory/exact-units?productId={productId:D}&pageSize=200";
        if (status is not null)
        {
            query += $"&status={Uri.EscapeDataString(status.Value.ToString())}";
        }

        if (sourcePurchaseItemId is Guid purchaseItemId)
        {
            query += $"&sourcePurchaseItemId={purchaseItemId:D}";
        }

        var rows = await apiClient.GetAsync<ExactInventoryUnitDto[]>(query, cancellationToken);
        return [.. rows.Select(x => new BackendExactUnit(
            x.InventoryUnitId, x.ProductId, x.ProductName, x.Sku ?? string.Empty,
            x.TrackingCode ?? string.Empty, x.SerialNumber ?? string.Empty,
            x.Imei1 ?? string.Empty, x.Imei2 ?? string.Empty, x.Status,
            x.AcquisitionCost, x.SourcePurchaseItemId, x.PurchaseNumber ?? string.Empty,
            x.SupplierName ?? string.Empty, x.CreatedAt, x.Version))];
    }

    private static BackendStocktakeSnapshot MapStocktake(StocktakeSnapshotDto row) =>
        new(
            row.StocktakeId,
            row.Scope,
            row.Status,
            row.CreatedAt,
            row.Note ?? string.Empty,
            row.Version,
            [.. row.Items.Select(item => new BackendStocktakeLine(
                item.StocktakeItemId,
                item.ProductId,
                item.ProductName,
                item.Sku ?? string.Empty,
                item.IsSerialized,
                item.ExpectedSellableQty,
                item.CountedSellableQty,
                item.VarianceQty,
                item.ReviewNote ?? string.Empty))]);

    private static string? Normalize(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static bool IsTransportFailure(string code) =>
        code.StartsWith("network.", StringComparison.OrdinalIgnoreCase) ||
        code.StartsWith("gateway.communication", StringComparison.OrdinalIgnoreCase);

    private sealed record SuccessResponse(bool Success);
    private sealed record OpenStocktakeApiResponse(StocktakeSnapshotDto? Stocktake);
    private sealed record StocktakeCountRequest(Guid ProductId, decimal CountedSellableQty, string? ReviewNote, Guid ClientOperationId);
    private sealed record SerializedStocktakeCountRequest(
        Guid ProductId,
        IReadOnlyCollection<Guid> FoundInventoryUnitIds,
        IReadOnlyCollection<string> UnexpectedIdentitySnapshots,
        string? ReviewNote,
        Guid ClientOperationId);
    private sealed record StocktakeActionRequest(Guid ClientOperationId);
    private sealed record StocktakePostRequest(
        Guid? ClientOperationId,
        IReadOnlyDictionary<Guid, decimal>? PositiveVarianceUnitCosts);
}
