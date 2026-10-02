using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Domain.Inventory;

namespace EdgeRetails.Desktop.Services;

/// <summary>
/// POS workflow reads and draft commands cross the local Server boundary.
/// This adapter is supplied only to the POS page; stocktake uses its own service.
/// </summary>
public sealed class RemotePosWorkflowService(DesktopApiClient apiClient) : IBackendWorkflowReadService
{
    public async Task<IReadOnlyList<BackendScannerMatch>> ResolveScannerAsync(
        string input, CancellationToken cancellationToken = default)
    {
        var rows = await apiClient.GetAsync<ScannerProductMatchDto[]>(
            $"/api/sales/scan?code={Uri.EscapeDataString(input.Trim())}", cancellationToken);
        return rows.Where(x => x.IsSellable).Select(MapScanner).ToArray();
    }

    public async Task<IReadOnlyList<BackendExactUnit>> GetExactUnitsAsync(
        Guid productId,
        InventoryUnitStatus? status = null,
        Guid? sourcePurchaseItemId = null,
        CancellationToken cancellationToken = default)
    {
        const int pageSize = 200;
        var result = new List<BackendExactUnit>();
        Guid? beforeUnitId = null;
        do
        {
            var query = $"/api/sales/exact-units?productId={productId:D}&pageSize={pageSize}";
            if (status is not null)
            {
                query += $"&status={Uri.EscapeDataString(status.Value.ToString())}";
            }

            if (sourcePurchaseItemId is Guid purchaseItemId)
            {
                query += $"&sourcePurchaseItemId={purchaseItemId:D}";
            }

            if (beforeUnitId is Guid cursor)
            {
                query += $"&beforeUnitId={cursor:D}";
            }

            var rows = await apiClient.GetAsync<PosExactUnitDto[]>(query, cancellationToken);
            result.AddRange(rows.Select(MapUnit));
            if (rows.Length < pageSize)
            {
                break;
            }

            var nextCursor = rows[^1].InventoryUnitId;
            if (beforeUnitId == nextCursor)
            {
                throw new DesktopApiException(
                    "inventory.pagination_stalled",
                    "Physical-unit pagination did not advance.");
            }

            beforeUnitId = nextCursor;
        }
        while (true);

        return result;
    }

    public async Task<IReadOnlyList<BackendPosDraftSummary>> GetOpenDraftsAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await apiClient.GetAsync<PosDraftSummaryDto[]>(
            "/api/sales/drafts", cancellationToken);
        return rows.Select(MapDraftSummary).ToArray();
    }

    public async Task<BackendPosDraftDetail?> GetDraftAsync(
        Guid draftId, CancellationToken cancellationToken = default)
    {
        PosDraftDetailDto detail;
        try
        {
            detail = await apiClient.GetAsync<PosDraftDetailDto>(
                $"/api/sales/drafts/{draftId:D}", cancellationToken);
        }
        catch (DesktopApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        return new BackendPosDraftDetail(
            MapDraftSummary(detail.Draft),
            detail.Items.Select(x => new BackendPosDraftLine(
                x.ProductId,
                x.ProductUnitId,
                x.ProductName,
                x.Sku ?? string.Empty,
                x.UnitSymbol,
                x.EnteredQuantity,
                x.DisplayedUnitPriceSnapshot,
                x.SelectedUnit is null ? null : MapUnit(x.SelectedUnit)))
                .ToArray());
    }

    public async Task<BackendPosDraftState> SaveDraftAsync(
        Guid? draftId,
        long? expectedVersion,
        Guid? customerId,
        string? note,
        IReadOnlyList<BackendPosDraftItemInput> items,
        Guid clientOperationId,
        CancellationToken cancellationToken = default)
    {
        var command = new SavePosDraftCommand(
            draftId,
            expectedVersion,
            customerId,
            Guid.Empty, // Server replaces the actor from the authenticated session.
            null,
            string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            items.Select(x => new SavePosDraftItemInput(
                x.ProductId,
                x.ProductUnitId,
                x.Quantity,
                x.InventoryUnitId))
                .ToArray(),
            ClientOperationId: clientOperationId);
        var result = await apiClient.PostAsync<SavePosDraftCommand, SavePosDraftResult>(
            "/api/sales/drafts", command, cancellationToken);
        return new BackendPosDraftState(
            result.DraftId, result.DraftNumber, result.Version, result.Created);
    }

    public async Task CancelDraftAsync(
        Guid draftId,
        long? expectedVersion,
        CancellationToken cancellationToken = default)
    {
        await apiClient.PostAsync<object, CancelDraftResponse>(
            $"/api/sales/drafts/{draftId:D}/cancel",
            new { expectedVersion }, cancellationToken);
    }

    public Task<BackendStocktakeSnapshot?> GetOpenStocktakeAsync(
        CancellationToken cancellationToken = default) => UnsupportedStocktake<BackendStocktakeSnapshot?>();

    public Task<BackendStocktakeSnapshot> StartFullShopStocktakeAsync(
        string? note,
        Guid? clientOperationId = null,
        CancellationToken cancellationToken = default) =>
        UnsupportedStocktake<BackendStocktakeSnapshot>();

    public Task RecordStocktakeCountAsync(
        Guid stocktakeId, Guid productId, decimal countedSellableQty,
        string? reviewNote, CancellationToken cancellationToken = default) =>
        UnsupportedStocktake();

    public Task RecordSerializedStocktakeAsync(
        Guid stocktakeId, Guid productId,
        IReadOnlyCollection<Guid> foundInventoryUnitIds,
        IReadOnlyCollection<string> unexpectedIdentitySnapshots,
        string? reviewNote, CancellationToken cancellationToken = default) =>
        UnsupportedStocktake();

    public Task ReviewStocktakeAsync(
        Guid stocktakeId, CancellationToken cancellationToken = default) =>
        UnsupportedStocktake();

    public Task PostStocktakeAsync(
        Guid stocktakeId, Guid? clientOperationId = null,
        CancellationToken cancellationToken = default) =>
        UnsupportedStocktake();

    public Task CancelStocktakeAsync(
        Guid stocktakeId, CancellationToken cancellationToken = default) =>
        UnsupportedStocktake();

    private static Task<T> UnsupportedStocktake<T>() =>
        Task.FromException<T>(new NotSupportedException(
            "POS workflow adapter cannot perform stocktake operations."));

    private static Task UnsupportedStocktake() =>
        Task.FromException(new NotSupportedException(
            "POS workflow adapter cannot perform stocktake operations."));

    private static BackendExactUnit MapUnit(PosExactUnitDto x) =>
        new(
            x.InventoryUnitId,
            x.ProductId,
            x.ProductName,
            x.Sku ?? string.Empty,
            x.TrackingCode ?? string.Empty,
            x.SerialNumber ?? string.Empty,
            x.Imei1 ?? string.Empty,
            x.Imei2 ?? string.Empty,
            x.Status,
            0m,
            null,
            string.Empty,
            string.Empty,
            DateTimeOffset.MinValue,
            x.Version);

    private static BackendExactUnit MapUnit(ExactInventoryUnitDto x) =>
        new(
            x.InventoryUnitId,
            x.ProductId,
            x.ProductName,
            x.Sku ?? string.Empty,
            x.TrackingCode ?? string.Empty,
            x.SerialNumber ?? string.Empty,
            x.Imei1 ?? string.Empty,
            x.Imei2 ?? string.Empty,
            x.Status,
            x.AcquisitionCost,
            x.SourcePurchaseItemId,
            x.PurchaseNumber ?? string.Empty,
            x.SupplierName ?? string.Empty,
            x.CreatedAt,
            x.Version);

    private static BackendScannerMatch MapScanner(ScannerProductMatchDto x) =>
        new(
            x.Namespace,
            x.ProductId,
            x.ProductUnitId,
            x.ProductName,
            x.Sku ?? string.Empty,
            x.Brand ?? string.Empty,
            x.Category,
            x.UnitSymbol,
            x.SellableStock,
            x.UnitPrice,
            x.IsSerialized,
            x.InventoryUnitId,
            x.TrackingCode ?? string.Empty,
            x.SerialNumber ?? string.Empty,
            x.Imei1 ?? string.Empty,
            x.Imei2 ?? string.Empty,
            x.UnitStatus);

    private static BackendPosDraftSummary MapDraftSummary(PosDraftSummaryDto x) =>
        new(
            x.DraftId,
            x.DraftNumber,
            x.CustomerId,
            x.CustomerName,
            x.Note,
            x.UpdatedAt,
            x.Version,
            x.ItemCount);

    private sealed record CancelDraftResponse(bool Success);
}
