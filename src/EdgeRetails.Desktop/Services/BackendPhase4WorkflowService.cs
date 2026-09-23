using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Inventory;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.Desktop.Services;

public sealed record BackendExactUnit(
    Guid InventoryUnitId,
    Guid ProductId,
    string ProductName,
    string Sku,
    string TrackingCode,
    string SerialNumber,
    string Imei1,
    string Imei2,
    InventoryUnitStatus Status,
    decimal AcquisitionCost,
    Guid? SourcePurchaseItemId,
    string PurchaseNumber,
    string SupplierName,
    DateTimeOffset CreatedAt,
    long Version)
{
    public string PrimaryIdentity =>
        !string.IsNullOrWhiteSpace(TrackingCode) ? TrackingCode :
        !string.IsNullOrWhiteSpace(SerialNumber) ? SerialNumber :
        !string.IsNullOrWhiteSpace(Imei1) ? Imei1 :
        InventoryUnitId.ToString("D");

    public string ManufacturerIdentity =>
        string.Join(" · ", new[] { SerialNumber, Imei1, Imei2 }
            .Where(x => !string.IsNullOrWhiteSpace(x)));

    public string ProvenanceDisplay =>
        string.Join(" · ", new[] { SupplierName, PurchaseNumber }
            .Where(x => !string.IsNullOrWhiteSpace(x)));
}

public sealed record BackendScannerMatch(
    ScannerResolutionNamespace Namespace,
    Guid ProductId,
    Guid ProductUnitId,
    string ProductName,
    string Sku,
    string Brand,
    string Category,
    string UnitSymbol,
    decimal SellableStock,
    decimal UnitPrice,
    bool IsSerialized,
    Guid? InventoryUnitId,
    string TrackingCode,
    string SerialNumber,
    string Imei1,
    string Imei2,
    InventoryUnitStatus? UnitStatus)
{
    public bool IsExactUnit => InventoryUnitId is not null;
}

public sealed record BackendPosDraftSummary(
    Guid DraftId,
    string DraftNumber,
    Guid? CustomerId,
    string CustomerName,
    string? Note,
    DateTimeOffset UpdatedAt,
    long Version,
    int ItemCount);

public sealed record BackendPosDraftLine(
    Guid ProductId,
    Guid ProductUnitId,
    string ProductName,
    string Sku,
    string UnitSymbol,
    decimal Quantity,
    decimal UnitPrice,
    BackendExactUnit? ExactUnit);

public sealed record BackendPosDraftDetail(
    BackendPosDraftSummary Draft,
    IReadOnlyList<BackendPosDraftLine> Items);

public sealed record BackendPosDraftItemInput(
    Guid ProductId,
    Guid ProductUnitId,
    decimal Quantity,
    Guid? InventoryUnitId);

public sealed record BackendPosDraftState(
    Guid DraftId,
    string DraftNumber,
    long Version,
    bool Created);

public sealed record BackendStocktakeLine(
    Guid StocktakeItemId,
    Guid ProductId,
    string ProductName,
    string Sku,
    bool IsSerialized,
    decimal ExpectedSellableQty,
    decimal? CountedSellableQty,
    decimal VarianceQty,
    string ReviewNote);

public sealed record BackendStocktakeSnapshot(
    Guid StocktakeId,
    StocktakeScope Scope,
    StocktakeStatus Status,
    DateTimeOffset CreatedAt,
    string Note,
    long Version,
    IReadOnlyList<BackendStocktakeLine> Items);

public interface IBackendPhase4WorkflowService
{
    Task<IReadOnlyList<BackendScannerMatch>> ResolveScannerAsync(
        string input,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BackendExactUnit>> GetExactUnitsAsync(
        Guid productId,
        InventoryUnitStatus? status = null,
        Guid? sourcePurchaseItemId = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BackendPosDraftSummary>> GetOpenDraftsAsync(
        CancellationToken cancellationToken = default);

    Task<BackendPosDraftDetail?> GetDraftAsync(
        Guid draftId,
        CancellationToken cancellationToken = default);

    Task<BackendPosDraftState> SaveDraftAsync(
        Guid? draftId,
        long? expectedVersion,
        Guid? customerId,
        string? note,
        IReadOnlyList<BackendPosDraftItemInput> items,
        CancellationToken cancellationToken = default);

    Task CancelDraftAsync(
        Guid draftId,
        long? expectedVersion,
        CancellationToken cancellationToken = default);

    Task<BackendStocktakeSnapshot?> GetOpenStocktakeAsync(
        CancellationToken cancellationToken = default);

    Task<BackendStocktakeSnapshot> StartFullShopStocktakeAsync(
        string? note,
        CancellationToken cancellationToken = default);

    Task RecordStocktakeCountAsync(
        Guid stocktakeId,
        Guid productId,
        decimal countedSellableQty,
        string? reviewNote,
        CancellationToken cancellationToken = default);

    Task RecordSerializedStocktakeAsync(
        Guid stocktakeId,
        Guid productId,
        IReadOnlyCollection<Guid> foundInventoryUnitIds,
        IReadOnlyCollection<string> unexpectedIdentitySnapshots,
        string? reviewNote,
        CancellationToken cancellationToken = default);

    Task ReviewStocktakeAsync(
        Guid stocktakeId,
        CancellationToken cancellationToken = default);

    Task PostStocktakeAsync(
        Guid stocktakeId,
        CancellationToken cancellationToken = default);

    Task CancelStocktakeAsync(
        Guid stocktakeId,
        CancellationToken cancellationToken = default);
}

public sealed class BackendPhase4WorkflowService : IBackendPhase4WorkflowService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Func<Guid?> _actorUserId;

    public BackendPhase4WorkflowService(
        IServiceScopeFactory scopeFactory,
        Func<Guid?> actorUserId)
    {
        _scopeFactory = scopeFactory;
        _actorUserId = actorUserId;
    }

    public async Task<IReadOnlyList<BackendScannerMatch>> ResolveScannerAsync(
        string input,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var reads = scope.ServiceProvider.GetRequiredService<IPhase4WorkflowReadService>();
        var rows = await reads.ResolveScannerAsync(input, cancellationToken);
        return rows.Select(MapScanner).ToArray();
    }

    public async Task<IReadOnlyList<BackendExactUnit>> GetExactUnitsAsync(
        Guid productId,
        InventoryUnitStatus? status = null,
        Guid? sourcePurchaseItemId = null,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var reads = scope.ServiceProvider.GetRequiredService<IPhase4WorkflowReadService>();
        var rows = await reads.GetExactUnitsAsync(
            productId,
            status,
            sourcePurchaseItemId,
            cancellationToken);
        return rows.Select(MapUnit).ToArray();
    }

    public async Task<IReadOnlyList<BackendPosDraftSummary>> GetOpenDraftsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var reads = scope.ServiceProvider.GetRequiredService<IPhase4WorkflowReadService>();
        var rows = await reads.GetOpenDraftsAsync(cancellationToken);
        return rows.Select(MapDraftSummary).ToArray();
    }

    public async Task<BackendPosDraftDetail?> GetDraftAsync(
        Guid draftId,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var reads = scope.ServiceProvider.GetRequiredService<IPhase4WorkflowReadService>();
        var row = await reads.GetDraftAsync(draftId, cancellationToken);
        if (row is null)
        {
            return null;
        }

        return new BackendPosDraftDetail(
            MapDraftSummary(row.Draft),
            row.Items.Select(x => new BackendPosDraftLine(
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
        CancellationToken cancellationToken = default)
    {
        var actor = RequireActor();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<SavePosDraftHandler>();
        var result = await handler.HandleAsync(
            new SavePosDraftCommand(
                draftId,
                expectedVersion,
                customerId,
                actor,
                Environment.MachineName,
                string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
                items.Select(x => new SavePosDraftItemInput(
                    x.ProductId,
                    x.ProductUnitId,
                    x.Quantity,
                    x.InventoryUnitId))
                    .ToArray()),
            cancellationToken);

        if (!result.IsSuccess || result.Value is null)
        {
            throw Error(result.Error?.Code, result.Error?.Message, "POS draft could not be saved.");
        }

        return new BackendPosDraftState(
            result.Value.DraftId,
            result.Value.DraftNumber,
            result.Value.Version,
            result.Value.Created);
    }

    public async Task CancelDraftAsync(
        Guid draftId,
        long? expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var actor = RequireActor();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<CancelPosDraftHandler>();
        var result = await handler.HandleAsync(
            new CancelPosDraftCommand(draftId, expectedVersion, actor),
            cancellationToken);
        if (!result.IsSuccess)
        {
            throw Error(result.Error?.Code, result.Error?.Message, "POS draft could not be cancelled.");
        }
    }

    public async Task<BackendStocktakeSnapshot?> GetOpenStocktakeAsync(
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var reads = scope.ServiceProvider.GetRequiredService<IPhase4WorkflowReadService>();
        var snapshot = await reads.GetOpenStocktakeAsync(cancellationToken);
        return snapshot is null ? null : MapStocktake(snapshot);
    }

    public async Task<BackendStocktakeSnapshot> StartFullShopStocktakeAsync(
        string? note,
        CancellationToken cancellationToken = default)
    {
        var actor = RequireActor();
        await using var scope = _scopeFactory.CreateAsyncScope();
        await EnsureInventoryManageAsync(scope.ServiceProvider, actor, cancellationToken);

        var reads = scope.ServiceProvider.GetRequiredService<IPhase4WorkflowReadService>();
        var existing = await reads.GetOpenStocktakeAsync(cancellationToken);
        Guid stocktakeId;
        if (existing is null)
        {
            var create = scope.ServiceProvider.GetRequiredService<CreateStocktakeHandler>();
            var created = await create.HandleAsync(
                new CreateStocktakeCommand(
                    StocktakeScope.FullShop,
                    null,
                    actor,
                    string.IsNullOrWhiteSpace(note) ? null : note.Trim()),
                cancellationToken);
            if (!created.IsSuccess || created.Value == Guid.Empty)
            {
                throw Error(
                    created.Error?.Code,
                    created.Error?.Message,
                    "Stocktake could not be created.");
            }
            stocktakeId = created.Value;
        }
        else
        {
            stocktakeId = existing.StocktakeId;
            if (existing.Status != StocktakeStatus.Draft)
            {
                return MapStocktake(existing);
            }
        }

        var start = scope.ServiceProvider.GetRequiredService<StartStocktakeHandler>();
        var started = await start.HandleAsync(
            new StartStocktakeCommand(stocktakeId),
            cancellationToken);
        if (!started.IsSuccess)
        {
            throw Error(
                started.Error?.Code,
                started.Error?.Message,
                "Stocktake could not be started.");
        }

        var snapshot = await reads.GetOpenStocktakeAsync(cancellationToken)
            ?? throw new BackendOperationException(
                "inventory.stocktake_readback_missing",
                "Stocktake started but could not be read back.");
        return MapStocktake(snapshot);
    }

    public async Task RecordStocktakeCountAsync(
        Guid stocktakeId,
        Guid productId,
        decimal countedSellableQty,
        string? reviewNote,
        CancellationToken cancellationToken = default)
    {
        var actor = RequireActor();
        await using var scope = _scopeFactory.CreateAsyncScope();
        await EnsureInventoryManageAsync(scope.ServiceProvider, actor, cancellationToken);
        var handler = scope.ServiceProvider.GetRequiredService<RecordStocktakeCountHandler>();
        var result = await handler.HandleAsync(
            new RecordStocktakeCountCommand(
                stocktakeId,
                productId,
                countedSellableQty,
                actor,
                string.IsNullOrWhiteSpace(reviewNote) ? null : reviewNote.Trim()),
            cancellationToken);
        EnsureSuccess(result, "Stocktake count could not be recorded.");
    }

    public async Task RecordSerializedStocktakeAsync(
        Guid stocktakeId,
        Guid productId,
        IReadOnlyCollection<Guid> foundInventoryUnitIds,
        IReadOnlyCollection<string> unexpectedIdentitySnapshots,
        string? reviewNote,
        CancellationToken cancellationToken = default)
    {
        var actor = RequireActor();
        await using var scope = _scopeFactory.CreateAsyncScope();
        await EnsureInventoryManageAsync(scope.ServiceProvider, actor, cancellationToken);
        var handler = scope.ServiceProvider.GetRequiredService<RecordSerializedStocktakeHandler>();
        var result = await handler.HandleAsync(
            new RecordSerializedStocktakeCommand(
                stocktakeId,
                productId,
                foundInventoryUnitIds,
                unexpectedIdentitySnapshots,
                actor,
                string.IsNullOrWhiteSpace(reviewNote) ? null : reviewNote.Trim()),
            cancellationToken);
        EnsureSuccess(result, "Serialized stocktake count could not be recorded.");
    }

    public async Task ReviewStocktakeAsync(
        Guid stocktakeId,
        CancellationToken cancellationToken = default)
    {
        var actor = RequireActor();
        await using var scope = _scopeFactory.CreateAsyncScope();
        await EnsureInventoryManageAsync(scope.ServiceProvider, actor, cancellationToken);
        var handler = scope.ServiceProvider.GetRequiredService<ReviewStocktakeHandler>();
        var result = await handler.HandleAsync(
            new ReviewStocktakeCommand(stocktakeId),
            cancellationToken);
        EnsureSuccess(result, "Stocktake could not enter review.");
    }

    public async Task PostStocktakeAsync(
        Guid stocktakeId,
        CancellationToken cancellationToken = default)
    {
        var actor = RequireActor();
        await using var scope = _scopeFactory.CreateAsyncScope();
        await EnsureInventoryManageAsync(scope.ServiceProvider, actor, cancellationToken);
        var handler = scope.ServiceProvider.GetRequiredService<PostStocktakeHandler>();
        var result = await handler.HandleAsync(
            new PostStocktakeCommand(stocktakeId, actor, null),
            cancellationToken);
        EnsureSuccess(result, "Stocktake could not be posted.");
    }

    public async Task CancelStocktakeAsync(
        Guid stocktakeId,
        CancellationToken cancellationToken = default)
    {
        var actor = RequireActor();
        await using var scope = _scopeFactory.CreateAsyncScope();
        await EnsureInventoryManageAsync(scope.ServiceProvider, actor, cancellationToken);
        var handler = scope.ServiceProvider.GetRequiredService<CancelStocktakeHandler>();
        var result = await handler.HandleAsync(
            new CancelStocktakeCommand(stocktakeId),
            cancellationToken);
        EnsureSuccess(result, "Stocktake could not be cancelled.");
    }

    private static async Task EnsureInventoryManageAsync(
        IServiceProvider services,
        Guid actor,
        CancellationToken cancellationToken)
    {
        var authorization = services.GetRequiredService<EdgeRetails.Application.Features.Identity.IApplicationPermissionAuthorizer>();
        var result = await authorization.AuthorizeAsync(
            actor,
            EdgeRetails.Application.Features.Identity.PermissionKeys.InventoryManage,
            cancellationToken);
        if (!result.IsSuccess)
        {
            throw new BackendOperationException(
                result.Error?.Code ?? "authorization.denied",
                result.Error?.Message ?? "Inventory management permission is required.");
        }
    }

    private static void EnsureSuccess(
        EdgeRetails.Application.Common.Result result,
        string fallback)
    {
        if (!result.IsSuccess)
        {
            throw Error(result.Error?.Code, result.Error?.Message, fallback);
        }
    }

    private static BackendStocktakeSnapshot MapStocktake(StocktakeSnapshotDto x) =>
        new(
            x.StocktakeId,
            x.Scope,
            x.Status,
            x.CreatedAt,
            x.Note ?? string.Empty,
            x.Version,
            x.Items.Select(item => new BackendStocktakeLine(
                item.StocktakeItemId,
                item.ProductId,
                item.ProductName,
                item.Sku ?? string.Empty,
                item.IsSerialized,
                item.ExpectedSellableQty,
                item.CountedSellableQty,
                item.VarianceQty,
                item.ReviewNote ?? string.Empty))
                .ToArray());

    private Guid RequireActor() =>
        _actorUserId()
        ?? throw new BackendOperationException(
            "identity.session_required",
            "A persistent backend user session is required.");

    private static BackendOperationException Error(
        string? code,
        string? message,
        string fallback) =>
        new(code ?? "backend.operation_failed", message ?? fallback);

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
}
