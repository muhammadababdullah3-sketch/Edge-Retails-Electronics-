using System.Net.Http;
using System.Text;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Catalog;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Parties;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Gateways;
using EdgeRetails.Application.Production.Printing;
using EdgeRetails.Desktop.ViewModels;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Purchasing;

namespace EdgeRetails.Desktop.Services;

/// <summary>Purchasing and inventory presentation data sourced from the local Server API.</summary>
public sealed class RemotePurchasingInventoryService(
    DesktopApiClient apiClient,
    Func<Guid?> actorUserId,
    IClientOperationIntentStore? operationIntents = null) : IBackendPurchasingInventoryService, IBackendLabelService, IBackendPurchaseLookupService
{
    private readonly IClientOperationIntentStore _operationIntents = operationIntents ?? new FileClientOperationIntentStore();
    private readonly RemotePhysicalStickerPrintService _stickerPrinter = new(apiClient);

    public bool ReceivesStockImmediately => false;

    public async Task<BackendSupplierPage> GetSupplierPageAsync(string? search, int pageSize = 50,
        string? beforeName = null, Guid? beforeSupplierId = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(beforeName) != !beforeSupplierId.HasValue)
        {
            throw new ArgumentException("Supplier cursor requires both name and ID.");
        }
        var take = Math.Clamp(pageSize, 1, 200);
        var query = $"/api/suppliers?pageSize={take + 1}";
        if (!string.IsNullOrWhiteSpace(search))
        {
            query += $"&search={Uri.EscapeDataString(search.Trim())}";
        }
        if (beforeSupplierId is Guid cursor)
        {
            query += $"&beforeName={Uri.EscapeDataString(beforeName!)}&beforeSupplierId={cursor:D}";
        }
        var rows = await apiClient.GetAsync<SupplierDirectoryDto[]>(query, cancellationToken);
        var visible = rows.Take(take).ToArray();
        return new BackendSupplierPage(visible.Select(x => new BackendSupplierOption(x.SupplierId, x.Name, x.City, x.Phone)).ToArray(),
            rows.Length > take ? visible[^1].Name : null, rows.Length > take ? visible[^1].SupplierId : null);
    }

    public async Task<IReadOnlyList<BackendSupplierOption>> GetSuppliersAsync(CancellationToken cancellationToken = default)
        => (await GetSupplierPageAsync(null, 200, cancellationToken: cancellationToken)).Items;

    public Task<PurchaseCatalogPageDto> GetCatalogPageAsync(PurchaseCatalogPageQuery request, CancellationToken cancellationToken = default)
    {
        var query = $"/api/purchasing/catalog?pageSize={Math.Clamp(request.PageSize, 1, 200)}&includeInactive={request.IncludeInactive.ToString().ToLowerInvariant()}";
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            query += $"&search={Uri.EscapeDataString(request.Search.Trim())}";
        }
        if (request.AfterProductId is Guid cursor)
        {
            query += $"&afterName={Uri.EscapeDataString(request.AfterName!)}&afterProductId={cursor:D}";
        }
        if (request.ProductId is Guid productId)
        {
            query += $"&productId={productId:D}";
        }
        return apiClient.GetAsync<PurchaseCatalogPageDto>(query, cancellationToken);
    }

    public async Task<IReadOnlyList<BackendPurchaseCatalogItem>> GetCatalogAsync(CancellationToken cancellationToken = default)
        => (await GetCatalogPageAsync(new PurchaseCatalogPageQuery(PageSize: 200), cancellationToken)).Items.Select(MapCatalog).ToArray();

    internal static BackendPurchaseCatalogItem MapCatalog(PurchaseCatalogProductDto x) => new(
        x.ProductId, x.ProductUnitId, x.Name, x.Sku ?? string.Empty, x.Category, x.UnitSymbol,
        x.SellableStock, x.ReferenceCost, x.DefaultSalePrice, x.FactorToBaseUnit, x.IsSerialized,
        x.SerialTrackingEnabled, x.ImeiTrackingEnabled, x.TrackingMode);

    public async Task<IReadOnlyList<PurchaseRecord>> GetPurchasesAsync(
        string? search = null,
        DateOnly? fromDate = null,
        DateOnly? toDate = null,
        Guid? supplierId = null,
        CancellationToken cancellationToken = default)
    {
        var filters = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(search))
        {
            filters.Append($"&search={Uri.EscapeDataString(search.Trim())}");
        }

        if (fromDate is not null)
        {
            filters.Append($"&fromDate={fromDate.Value:yyyy-MM-dd}");
        }

        if (toDate is not null)
        {
            filters.Append($"&toDate={toDate.Value:yyyy-MM-dd}");
        }

        if (supplierId is Guid selectedSupplierId)
        {
            filters.Append($"&supplierId={selectedSupplierId:D}");
        }

        const int pageSize = 500;
        var results = new List<PurchaseHistoryRowDto>();
        DateOnly? beforeDate = null;
        Guid? beforeId = null;
        while (true)
        {
            var query = $"/api/purchasing?pageSize={pageSize}{filters}";
            if (beforeDate.HasValue && beforeId.HasValue)
            {
                query += $"&beforePurchaseDate={beforeDate.Value:yyyy-MM-dd}&beforePurchaseId={beforeId.Value:D}";
            }

            var page = await apiClient.GetAsync<PurchaseHistoryRowDto[]>(query, cancellationToken);
            results.AddRange(page);
            if (page.Length < pageSize)
            {
                return [.. results.Select(MapPurchaseSummary)];
            }

            var last = page[^1];
            if (beforeDate == last.PurchaseDate && beforeId == last.PurchaseId)
            {
                throw Error("purchasing.history_cursor_stalled", "The Server purchase history cursor did not advance.");
            }

            beforeDate = last.PurchaseDate;
            beforeId = last.PurchaseId;
        }
    }

    public async Task<PurchaseRecord?> GetPurchaseAsync(
        Guid purchaseId,
        CancellationToken cancellationToken = default)
    {
        PurchaseDocumentDto document;
        try
        {
            document = await apiClient.GetAsync<PurchaseDocumentDto>(
                $"/api/purchasing/{purchaseId:D}", cancellationToken);
        }
        catch (DesktopApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        // Historical quantities, return eligibility and tracking are purchase snapshots.
        // A purchasing read must not depend on InventoryManage or current catalog state.
        var record = BackendPurchasingInventoryService.ProjectPurchase(document,
            new Dictionary<Guid, InventoryStockRowDto>(), new Dictionary<Guid, PurchaseCatalogProductDto>());
        foreach (var item in record.Items)
        {
            item.Product.Category = "Not provided in purchase document";
        }
        return record;
    }

    public async Task<PurchaseRecord> CreatePurchaseAsync(
        Guid supplierId,
        string invoiceNumber,
        DateTime purchaseDate,
        string note,
        decimal otherCharges,
        IReadOnlyList<PurchaseDraftLine> lines,
        Guid clientOperationId,
        CancellationToken cancellationToken = default)
    {
        if (clientOperationId == Guid.Empty)
        {
            throw Error("purchasing.operation_id_required", "Purchase operation id is required.");
        }

        var commandLines = lines.Select(line => new CreatePurchaseLineInput(
            line.Product.BackendProductId ?? throw Error("catalog.product_not_attached", "Purchase product is not attached to backend."),
            line.Product.BackendProductUnitId ?? throw Error("catalog.product_unit_not_attached", "Purchase unit is not attached to backend."),
            line.Quantity,
            line.Cost,
            line.SalePrice,
            [.. line.SerializedIdentities.Select(x => new SerializedIdentityInput(x.SerialNumber, x.Imei1, x.Imei2))])).ToArray();
        var actor = actorUserId() ?? throw Error("identity.session_required", "A backend user session is required.");
        var command = new CreatePurchaseCommand(
            supplierId, invoiceNumber.Trim(), DateOnly.FromDateTime(purchaseDate), Normalize(note),
            otherCharges, PurchaseSettlementMode.External, actor, clientOperationId, commandLines,
            ReceiveStockImmediately: false);

        CreatePurchaseResult result;
        try
        {
            result = await apiClient.PostAsync<CreatePurchaseCommand, CreatePurchaseResult>(
                "/api/purchasing/create", command, cancellationToken);
        }
        catch (DesktopApiException ex) when (IsTransportFailure(ex.Code))
        {
            result = await RecoverPurchaseAsync(command, cancellationToken);
        }

        try
        {
            var record = await GetPurchaseAsync(result.PurchaseId, cancellationToken)
                ?? throw Error("purchasing.readback_missing", "Purchase was committed but its document could not be read back.");
            record.StockReceivedImmediately = false;
            return record;
        }
        catch (Exception ex)
        {
            // The authoritative POST result must survive even cancellation of display readback.
            throw new PurchaseCommittedReadbackException(result, ex);
        }
    }

    public async Task<decimal> ReturnPurchaseAsync(
        PurchaseRecord purchase,
        IReadOnlyDictionary<Guid, BackendPurchaseReturnSelection> selectionsByPurchaseItem,
        string reason,
        string note,
        Guid clientOperationId,
        CancellationToken cancellationToken = default)
    {
        var purchaseId = purchase.BackendPurchaseId
            ?? throw Error("purchasing.purchase_not_attached", "Purchase is not attached to backend authority.");
        var lines = purchase.Items
            .Where(x => x.BackendPurchaseItemId is Guid id && selectionsByPurchaseItem.ContainsKey(id))
            .Select(item =>
            {
                var id = item.BackendPurchaseItemId!.Value;
                var selection = selectionsByPurchaseItem[id];
                if (item.Product.IsSerialized)
                {
                    var baseQuantity = item.Product.TrackingMode == EdgeRetails.Domain.Catalog.TrackingMode.Container
                        ? selection.EnteredQuantity : selection.EnteredQuantity * item.Product.FactorToBaseUnit;
                    if (baseQuantity != decimal.Truncate(baseQuantity) ||
                        selection.InventoryUnitIds.Count != decimal.ToInt32(baseQuantity))
                    {
                        throw Error("purchasing.return_exact_unit_count_mismatch", "Select the exact eligible physical units for each serialized return line.");
                    }
                }
                return new PurchaseReturnLineInput(id, selection.EnteredQuantity, item.Cost, selection.InventoryUnitIds);
            }).ToArray();
        var command = new CreatePurchaseReturnCommand(
            purchaseId, string.IsNullOrWhiteSpace(reason) ? "SUPPLIER_RETURN" : reason.Trim(),
            Normalize(note), PurchaseReturnSettlementMode.External,
            actorUserId() ?? throw Error("identity.session_required", "A backend user session is required."),
            clientOperationId, lines);
        try
        {
            var result = await apiClient.PostAsync<CreatePurchaseReturnCommand, CreatePurchaseReturnResult>(
                "/api/purchasing/return", command, cancellationToken);
            return result.SupplierReturnValue;
        }
        catch (DesktopApiException ex) when (IsTransportFailure(ex.Code))
        {
            var status = await QueryOperationAsync(clientOperationId, cancellationToken);
            if (status is { WasCommitted: true, OperationType: "PurchaseReturn", DocumentNumber: not null } committed)
            {
                var returns = await apiClient.GetAsync<PurchaseReturnHistoryRowDto[]>(
                    $"/api/purchasing/returns?purchaseId={purchaseId:D}&pageSize=200", cancellationToken);
                var recovered = returns.FirstOrDefault(x => string.Equals(
                    x.ReturnNumber,
                    committed.DocumentNumber,
                    StringComparison.OrdinalIgnoreCase));
                if (recovered is not null)
                {
                    return recovered.SupplierReturnValue;
                }
            }
            throw Error("purchasing.return_outcome_unknown", "Purchase return status is not confirmed. Retry with the same operation id.");
        }
    }

    public async Task VoidPurchaseAsync(
        PurchaseRecord purchase,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var purchaseId = purchase.BackendPurchaseId
            ?? throw Error("purchasing.purchase_not_attached", "Purchase is not attached to backend authority.");
        var actor = actorUserId() ?? throw Error("identity.session_required", "A backend user session is required.");
        var normalizedReason = Normalize(reason) ?? "Purchase void";
        var payload = $"{purchaseId:D}|{actor:D}|{normalizedReason}";
        var operationKey = $"purchase-void:{purchaseId:D}";
        Guid operationId;
        try
        {
            operationId = _operationIntents.GetOrCreate(operationKey, payload);
        }
        catch (InvalidOperationException)
        {
            throw Error("purchasing.void_outcome_unknown", "The previous void outcome is unknown. Reconcile it before changing the void reason.");
        }

        var command = new VoidPurchaseCommand(purchaseId, operationId, actor, normalizedReason);
        try
        {
            await apiClient.PostAsync<VoidPurchaseCommand, VoidPurchaseResult>(
                "/api/purchasing/void", command, cancellationToken);
            _operationIntents.Complete(operationKey, operationId);
        }
        catch (DesktopApiException ex) when (IsDefinitiveRejection(ex.StatusCode))
        {
            _operationIntents.Complete(operationKey, operationId);
            throw;
        }
        catch (DesktopApiException ex) when (IsTransportFailure(ex.Code) || ex.StatusCode >= System.Net.HttpStatusCode.InternalServerError)
        {
            try
            {
                var status = await QueryOperationAsync(operationId, cancellationToken);
                if (status is { WasCommitted: true, OperationType: "PurchaseVoid" })
                {
                    _operationIntents.Complete(operationKey, operationId);
                    return;
                }

                if (status is { EffectiveStatus: "NotFound" })
                {
                    try
                    {
                        await apiClient.PostAsync<VoidPurchaseCommand, VoidPurchaseResult>(
                            "/api/purchasing/void", command, cancellationToken);
                        _operationIntents.Complete(operationKey, operationId);
                        return;
                    }
                    catch (DesktopApiException definitiveReplayException) when (IsDefinitiveRejection(definitiveReplayException.StatusCode))
                    {
                        _operationIntents.Complete(operationKey, operationId);
                        throw;
                    }
                }
            }
            catch (DesktopApiException recoveryException) when (IsTransportFailure(recoveryException.Code))
            {
                // Keep the same operation id until its outcome can be reconciled.
            }

            throw Error("purchasing.void_outcome_unknown", "Purchase void status is not confirmed. Retry using the same operation intent.");
        }
    }

    public async Task<IReadOnlyList<BackendProductSaleHistoryItem>> GetProductSalesAsync(
        Guid productId, CancellationToken cancellationToken = default)
    {
        var rows = await apiClient.GetAsync<ProductSaleHistoryRowDto[]>(
            $"/api/inventory/products/{productId:D}/sale-history?pageSize=200", cancellationToken);
        return [.. rows.Select(x => new BackendProductSaleHistoryItem(
            x.InvoiceNumber, x.CompletedAt.LocalDateTime, x.CustomerName,
            x.BaseQuantity, x.BaseQuantity <= 0m ? 0m : x.NetLineTotal / x.BaseQuantity,
            x.NetLineTotal))];
    }

    public async Task<IReadOnlyList<PurchaseRecord>> GetProductPurchasesAsync(
        Guid productId, CancellationToken cancellationToken = default)
    {
        var rows = await apiClient.GetAsync<ProductPurchaseProvenanceRowDto[]>(
            $"/api/inventory/products/{productId:D}/purchase-provenance?pageSize=200", cancellationToken);
        return [.. rows.Where(x => x.PurchaseId is not null)
            .GroupBy(x => x.PurchaseId!.Value)
            .Select(group =>
            {
                var row = group.First();
                return new PurchaseRecord
                {
                    BackendPurchaseId = row.PurchaseId,
                    PurchaseNumber = row.PurchaseNumber ?? "—",
                    InvoiceNumber = row.PurchaseNumber ?? "—",
                    Supplier = row.SupplierName ?? "—",
                    Date = row.CreatedAt.LocalDateTime,
                    BackendTotal = group.Sum(x => x.ReceivedQuantity * x.EffectiveUnitCost),
                    BackendItemCount = group.Count(),
                    Items = []
                };
            }).OrderByDescending(x => x.Date)];
    }

    public async Task<BackendInventorySnapshot> GetInventorySnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var stock = await apiClient.GetAsync<InventoryStockRowDto[]>(
            "/api/inventory/stock?pageSize=500", cancellationToken);
        var movements = await apiClient.GetAsync<InventoryMovementRowDto[]>(
            "/api/inventory/movements?pageSize=500", cancellationToken);
        var catalog = await GetCatalogAsync(cancellationToken);
        var unitByProduct = catalog.GroupBy(x => x.ProductId).ToDictionary(x => x.Key, x => x.First());
        var products = stock.Select(row =>
        {
            unitByProduct.TryGetValue(row.ProductId, out var unit);
            return new PosProductItemViewModel(
                row.ProductId.ToString("D"), row.Name, row.Sku ?? string.Empty,
                row.Brand ?? "—", row.Category, row.SellableQty, row.DefaultSalePrice,
                unit: row.UnitSymbol ?? "Pcs", cost: row.MovingAverageCost,
                minimumStock: row.MinimumStockLevel, model: row.Model ?? string.Empty,
                backendProductId: row.ProductId, backendProductUnitId: unit?.ProductUnitId,
                isSerialized: row.IsSerialized,
                serialTrackingEnabled: unit?.SerialTrackingEnabled ?? false,
                imeiTrackingEnabled: unit?.ImeiTrackingEnabled ?? false,
                factorToBaseUnit: unit?.FactorToBaseUnit ?? 1m,
                trackingMode: row.TrackingMode);
        }).ToArray();
        var movementRecords = movements.Select(row => new InventoryMovementRecord
        {
            Timestamp = row.OccurredAt.LocalDateTime,
            ProductId = row.ProductId.ToString("D"),
            ProductName = row.ProductName,
            Kind = BackendPurchasingInventoryService.MapMovement(row.MovementType, row.QuantityDelta),
            QuantityDelta = row.QuantityDelta,
            BeforeQuantity = row.QuantityBefore,
            AfterQuantity = row.QuantityAfter,
            Reference = row.ReferenceId is null ? row.ReferenceType : $"{row.ReferenceType} · {row.ReferenceId.Value.ToString("N")[..8]}",
            Reason = row.Reason ?? row.Note ?? row.ReferenceType
        }).ToArray();
        return new BackendInventorySnapshot(products, movementRecords);
    }

    public async Task<Result<ReceiveProductIntakeResult>> ReceiveProductIntakeAsync(
        ReceiveProductIntakeCommand command,
        CancellationToken cancellationToken = default)
    {
        var operationKey = PhysicalIntakeOperationIntent.Key(command.PurchaseId, command.ProductUnitId);
        var payload = PhysicalIntakeOperationIntent.Payload(command);
        Guid operationId;
        try
        {
            operationId = _operationIntents.GetOrCreate(operationKey, payload, command.ClientOperationId);
        }
        catch (InvalidOperationException ex)
        {
            return Result<ReceiveProductIntakeResult>.Failure(
                "purchasing.intake_outcome_unknown",
                $"The prior intake identity could not be confirmed: {ex.Message}");
        }

        var stableCommand = command with { ClientOperationId = operationId };
        try
        {
            var result = await apiClient.PostAsync<ReceiveProductIntakeCommand, ReceiveProductIntakeResult>(
                "/api/purchasing/intake", stableCommand, cancellationToken);
            _operationIntents.Complete(operationKey, operationId);
            return Result<ReceiveProductIntakeResult>.Success(result);
        }
        catch (DesktopApiException ex) when (IsAmbiguous(ex))
        {
            try
            {
                var status = await QueryOperationAsync(operationId, cancellationToken);
                if (status is { WasCommitted: true, OperationType: "InventoryMovement" } or
                    { EffectiveStatus: "NotFound" })
                {
                    var replay = await apiClient.PostAsync<ReceiveProductIntakeCommand, ReceiveProductIntakeResult>(
                        "/api/purchasing/intake", stableCommand, cancellationToken);
                    _operationIntents.Complete(operationKey, operationId);
                    return Result<ReceiveProductIntakeResult>.Success(replay);
                }
            }
            catch (DesktopApiException recoveryException) when (IsAmbiguous(recoveryException))
            {
                // Preserve the durable intent until the server outcome can be confirmed.
            }
            catch (DesktopApiException definitiveException)
            {
                _operationIntents.Complete(operationKey, operationId);
                return Result<ReceiveProductIntakeResult>.Failure(definitiveException.Code, definitiveException.Message);
            }

            return Result<ReceiveProductIntakeResult>.Failure(
                "purchasing.intake_outcome_unknown",
                "Receiving status could not be confirmed. Retry using the same operation id.");
        }
        catch (DesktopApiException ex)
        {
            _operationIntents.Complete(operationKey, operationId);
            return Result<ReceiveProductIntakeResult>.Failure(ex.Code, ex.Message);
        }
    }

    public Task<Result<PrintPhysicalStickersResult>> PrintStickersAsync(
        PrintPhysicalStickersCommand command,
        CancellationToken cancellationToken = default) =>
        _stickerPrinter.PrintAsync(
            command.InventoryUnitIds,
            command.PrinterName,
            command.IsReprint,
            cancellationToken);

    public Task<Result<PrintJobResult>> PrintProductLabelAsync(Guid productUnitId, string? printerName,
        bool isReprint, CancellationToken cancellationToken = default) =>
        _stickerPrinter.PrintProductAsync(productUnitId, printerName, isReprint, cancellationToken);

    public Task<Result<IReadOnlyList<string>>> ExportLabelPdfAsync(IReadOnlyList<Guid> inventoryUnitIds,
        IReadOnlyList<Guid> productUnitIds, string outputDirectory, bool isReprint = false,
        CancellationToken cancellationToken = default) =>
        _stickerPrinter.ExportPdfAsync(inventoryUnitIds, productUnitIds, outputDirectory, isReprint, cancellationToken);

    public async Task<IReadOnlyList<CommittedInventoryUnitDto>> GetUnitsForPurchaseItemAsync(
        Guid purchaseItemId,
        CancellationToken cancellationToken = default) =>
        await apiClient.GetAsync<CommittedInventoryUnitDto[]>(
            $"/api/purchasing/items/{purchaseItemId:D}/units", cancellationToken);

    private async Task<CreatePurchaseResult> RecoverPurchaseAsync(
        CreatePurchaseCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var status = await QueryOperationAsync(command.ClientOperationId, cancellationToken);
            if (status is { WasCommitted: true, OperationType: "Purchase", EntityId: Guid id } && id != Guid.Empty)
            {
                return new CreatePurchaseResult(id, status.DocumentNumber ?? string.Empty, 0m, true);
            }

            if (status is { EffectiveStatus: "NotFound" })
            {
                return await apiClient.PostAsync<CreatePurchaseCommand, CreatePurchaseResult>(
                    "/api/purchasing/create", command, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is DesktopApiException or HttpRequestException)
        {
            throw Error("purchasing.create_outcome_unknown", "Purchase status is not confirmed. Retry using the same operation id.");
        }
        throw Error("purchasing.create_outcome_unknown", "Purchase status is not confirmed. Retry using the same operation id.");
    }

    private Task<OperationStatusResult?> QueryOperationAsync(Guid operationId, CancellationToken cancellationToken) =>
        apiClient.GetAsync<OperationStatusResult?>(
            $"/api/system/operations/{operationId:D}", cancellationToken);

    private static PurchaseRecord MapPurchaseSummary(PurchaseHistoryRowDto row) => new()
    {
        BackendPurchaseId = row.PurchaseId,
        BackendSupplierId = row.SupplierId,
        IsVoided = row.Status == PurchaseStatus.Voided,
        PurchaseNumber = row.PurchaseNumber,
        Supplier = row.SupplierName,
        InvoiceNumber = row.SupplierInvoiceNumber,
        Date = row.PurchaseDate.ToDateTime(TimeOnly.MinValue),
        OtherCharges = row.OtherCharges,
        BackendSubtotal = row.Subtotal,
        BackendTotal = row.GrandTotal,
        BackendItemCount = row.ItemCount,
        Items = []
    };

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsTransportFailure(string code) =>
        code.StartsWith("network.", StringComparison.OrdinalIgnoreCase) ||
        code.StartsWith("gateway.communication", StringComparison.OrdinalIgnoreCase);

    private static bool IsAmbiguous(DesktopApiException exception) =>
        IsTransportFailure(exception.Code) ||
        exception.StatusCode >= System.Net.HttpStatusCode.InternalServerError;

    private static bool IsDefinitiveRejection(System.Net.HttpStatusCode? status) =>
        status is >= System.Net.HttpStatusCode.BadRequest and < System.Net.HttpStatusCode.InternalServerError;

    private static BackendOperationException Error(string code, string message) => new(code, message);
}
