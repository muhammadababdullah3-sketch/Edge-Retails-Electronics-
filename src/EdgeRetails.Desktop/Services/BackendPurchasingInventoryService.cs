using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Parties;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Desktop.ViewModels;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Purchasing;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.Desktop.Services;

public sealed record BackendSupplierOption(Guid Id, string Name);

public sealed record BackendSerializedIdentityInput(
    string? SerialNumber,
    string? Imei1,
    string? Imei2);

public sealed record BackendPurchaseReturnSelection(
    decimal EnteredQuantity,
    IReadOnlyList<Guid> InventoryUnitIds);

public sealed record BackendPurchaseCatalogItem(
    Guid ProductId,
    Guid ProductUnitId,
    string Name,
    string Sku,
    string Category,
    string UnitSymbol,
    decimal SellableStock,
    decimal ReferenceCost,
    decimal DefaultSalePrice,
    decimal FactorToBaseUnit,
    bool IsSerialized,
    bool SerialTrackingEnabled,
    bool ImeiTrackingEnabled);

public sealed record BackendInventorySnapshot(
    IReadOnlyList<PosProductItemViewModel> Products,
    IReadOnlyList<InventoryMovementRecord> Movements);

public sealed record BackendProductSaleHistoryItem(
    string InvoiceNumber,
    DateTime Timestamp,
    string CustomerName,
    decimal Quantity,
    decimal UnitPrice,
    decimal LineTotal); public interface IBackendPurchasingInventoryService
{
    Task<IReadOnlyList<BackendSupplierOption>> GetSuppliersAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BackendPurchaseCatalogItem>> GetCatalogAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PurchaseRecord>> GetPurchasesAsync(
        string? search = null,
        DateOnly? fromDate = null,
        DateOnly? toDate = null,
        Guid? supplierId = null,
        CancellationToken cancellationToken = default);

    Task<PurchaseRecord?> GetPurchaseAsync(
        Guid purchaseId,
        CancellationToken cancellationToken = default);

    Task<PurchaseRecord> CreatePurchaseAsync(
        Guid supplierId,
        string invoiceNumber,
        DateTime purchaseDate,
        string note,
        decimal otherCharges,
        IReadOnlyList<PurchaseDraftLine> lines,
        Guid clientOperationId,
        CancellationToken cancellationToken = default);

    Task<decimal> ReturnPurchaseAsync(
        PurchaseRecord purchase,
        IReadOnlyDictionary<Guid, BackendPurchaseReturnSelection> selectionsByPurchaseItem,
        string reason,
        string note,
        Guid clientOperationId,
        CancellationToken cancellationToken = default);

    Task VoidPurchaseAsync(
        PurchaseRecord purchase,
        string reason,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BackendProductSaleHistoryItem>> GetProductSalesAsync(
        Guid productId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PurchaseRecord>> GetProductPurchasesAsync(
        Guid productId,
        CancellationToken cancellationToken = default);

    Task<BackendInventorySnapshot> GetInventorySnapshotAsync(
        CancellationToken cancellationToken = default);
}
public sealed class BackendPurchasingInventoryService
    : IBackendPurchasingInventoryService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Func<Guid?> _actorUserId;

    public BackendPurchasingInventoryService(
        IServiceScopeFactory scopeFactory,
        Func<Guid?> actorUserId)
    {
        _scopeFactory = scopeFactory;
        _actorUserId = actorUserId;
    }

    public async Task<IReadOnlyList<BackendSupplierOption>> GetSuppliersAsync(
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<GetSuppliersHandler>();
        var rows = await handler.HandleAsync(false, cancellationToken);

        return rows
            .Where(x => x.IsActive)
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Id)
            .Select(x => new BackendSupplierOption(x.Id, x.Name))
            .ToArray();
    }

    public async Task<IReadOnlyList<BackendPurchaseCatalogItem>> GetCatalogAsync(
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var reads = scope.ServiceProvider
            .GetRequiredService<IPurchaseCatalogReadService>();
        var rows = await reads.GetPurchasableCatalogAsync(cancellationToken); return rows.Select(x => new BackendPurchaseCatalogItem(
            x.ProductId,
            x.ProductUnitId,
            x.Name,
            x.Sku ?? string.Empty,
            x.Category,
            x.UnitSymbol,
            x.SellableStock,
            x.ReferenceCost,
            x.DefaultSalePrice,
            x.FactorToBaseUnit,
            x.IsSerialized,
            x.SerialTrackingEnabled,
            x.ImeiTrackingEnabled)).ToArray();
    }

    public async Task<IReadOnlyList<PurchaseRecord>> GetPurchasesAsync(
        string? search = null,
        DateOnly? fromDate = null,
        DateOnly? toDate = null,
        Guid? supplierId = null,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var reads = scope.ServiceProvider.GetRequiredService<IPurchasingReadService>();
        var history = await reads.GetHistoryAsync(
            new GetPurchaseHistoryQuery(
                FromDate: fromDate,
                ToDate: toDate,
                SupplierId: supplierId,
                Search: string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
                PageSize: 200),
            cancellationToken);

        return history.Select(row => new PurchaseRecord
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
        }).ToArray();
    }

    public async Task<PurchaseRecord?> GetPurchaseAsync(
        Guid purchaseId,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        return await LoadPurchaseAsync(scope.ServiceProvider, purchaseId, cancellationToken);
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
        var actor = RequireActor();
        if (supplierId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "A persistent backend supplier is required.");
        }

        var backendLines = lines.Select(line =>
        {
            var productId = line.Product.BackendProductId
                ?? throw new InvalidOperationException(
                    $"Product '{line.Product.Name}' is not attached to backend.");
            var productUnitId = line.Product.BackendProductUnitId
                ?? throw new InvalidOperationException(
                    $"Purchase unit for '{line.Product.Name}' is unavailable.");

            if (line.Product.IsSerialized && line.SerializedIdentities.Count == 0)
            {
                throw new BackendOperationException(
                    "purchasing.serialized_identities_required",
                    "Serialized purchase requires Serial/IMEI intake.");
            }

            return new CreatePurchaseLineInput(
                productId,
                productUnitId,
                line.Quantity,
                line.Cost,
                line.SalePrice,
                line.SerializedIdentities
                    .Select(x => new SerializedIdentityInput(
                        x.SerialNumber,
                        x.Imei1,
                        x.Imei2))
                    .ToArray());
        }).ToArray();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<CreatePurchaseHandler>();
        var result = await handler.HandleAsync(
            new CreatePurchaseCommand(
                supplierId,
                invoiceNumber,
                DateOnly.FromDateTime(purchaseDate),
                string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
                otherCharges,
                PurchaseSettlementMode.External,
                actor,
                clientOperationId,
                backendLines),
            cancellationToken);

        if (!result.IsSuccess || result.Value is null)
        {
            throw new BackendOperationException(
                result.Error?.Code ?? "purchasing.create_failed",
                result.Error?.Message ?? "Backend purchase failed.");
        }

        return await LoadPurchaseAsync(
            scope.ServiceProvider,
            result.Value.PurchaseId,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "Purchase was committed but could not be read back.");
    }

    public async Task<decimal> ReturnPurchaseAsync(
        PurchaseRecord purchase,
        IReadOnlyDictionary<Guid, BackendPurchaseReturnSelection> selectionsByPurchaseItem,
        string reason,
        string note,
        Guid clientOperationId,
        CancellationToken cancellationToken = default)
    {
        var actor = RequireActor();
        var purchaseId = purchase.BackendPurchaseId
            ?? throw new BackendOperationException(
                "purchasing.purchase_not_attached",
                "Purchase is not attached to the production backend.");

        if (clientOperationId == Guid.Empty)
        {
            throw new BackendOperationException(
                "purchasing.return_operation_id_required",
                "Purchase return operation id is required.");
        }

        var selected = purchase.Items
            .Where(item =>
                item.BackendPurchaseItemId is Guid id &&
                selectionsByPurchaseItem.TryGetValue(id, out var selection) &&
                selection.EnteredQuantity > 0m)
            .ToArray();

        if (selected.Length == 0)
        {
            throw new BackendOperationException(
                "purchasing.return_items_required",
                "Select at least one quantity or exact unit to return.");
        }

        var lines = selected.Select(item =>
        {
            var id = item.BackendPurchaseItemId!.Value;
            var selection = selectionsByPurchaseItem[id];
            if (selection.EnteredQuantity > item.EligibleReturnQuantity)
            {
                throw new BackendOperationException(
                    "purchasing.return_quantity_exceeds_eligible",
                    $"Return quantity for {item.ProductName} exceeds backend eligibility.");
            }

            if (item.Product.IsSerialized)
            {
                var exactBaseQuantity =
                    selection.EnteredQuantity * item.Product.FactorToBaseUnit;
                if (exactBaseQuantity != decimal.Truncate(exactBaseQuantity) ||
                    selection.InventoryUnitIds.Count != decimal.ToInt32(exactBaseQuantity))
                {
                    throw new BackendOperationException(
                        "purchasing.return_exact_unit_count_mismatch",
                        $"Serialized purchase return requires exact unit selection for {item.ProductName}.");
                }
            }
            else if (selection.InventoryUnitIds.Count > 0)
            {
                throw new BackendOperationException(
                    "purchasing.return_exact_unit_unexpected",
                    $"Quantity-tracked product {item.ProductName} cannot submit exact unit identities.");
            }

            return new PurchaseReturnLineInput(
                id,
                selection.EnteredQuantity,
                item.Cost,
                selection.InventoryUnitIds);
        }).ToArray();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider
            .GetRequiredService<CreatePurchaseReturnHandler>();
        var result = await handler.HandleAsync(
            new CreatePurchaseReturnCommand(
                purchaseId,
                string.IsNullOrWhiteSpace(reason)
                    ? "SUPPLIER_RETURN"
                    : reason.Trim(),
                string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
                PurchaseReturnSettlementMode.External,
                actor,
                clientOperationId,
                lines),
            cancellationToken);

        if (!result.IsSuccess || result.Value is null)
        {
            throw new BackendOperationException(
                result.Error?.Code ?? "purchasing.return_failed",
                result.Error?.Message ?? "Backend purchase return failed.");
        }

        return result.Value.SupplierReturnValue;
    }

    public async Task VoidPurchaseAsync(
        PurchaseRecord purchase,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var actor = RequireActor();
        var purchaseId = purchase.BackendPurchaseId
            ?? throw new InvalidOperationException(
                "Purchase is not attached to the production backend.");

        await using var scope = _scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<VoidPurchaseHandler>();
        var result = await handler.HandleAsync(
            new VoidPurchaseCommand(
                purchaseId,
                Guid.CreateVersion7(),
                actor,
                string.IsNullOrWhiteSpace(reason)
                    ? "Purchase void"
                    : reason.Trim()),
            cancellationToken); if (!result.IsSuccess)
        {
            throw new InvalidOperationException(
                result.Error?.Message ?? "Backend purchase void failed.");
        }
    }

    public async Task<IReadOnlyList<BackendProductSaleHistoryItem>> GetProductSalesAsync(
        Guid productId,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var reads = scope.ServiceProvider
            .GetRequiredService<IInventoryProvenanceReadService>();
        var rows = await reads.GetProductSaleHistoryAsync(
            new GetProductSaleHistoryQuery(productId, PageSize: 200),
            cancellationToken);

        return rows.Select(row =>
        {
            var unitPrice = row.BaseQuantity <= 0m
                ? 0m
                : decimal.Round(
                    row.NetLineTotal / row.BaseQuantity,
                    2,
                    MidpointRounding.AwayFromZero);

            return new BackendProductSaleHistoryItem(
                row.InvoiceNumber,
                row.CompletedAt.LocalDateTime,
                row.CustomerName,
                row.BaseQuantity,
                unitPrice,
                row.NetLineTotal);
        }).ToArray();
    }

    public async Task<IReadOnlyList<PurchaseRecord>> GetProductPurchasesAsync(
        Guid productId,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var reads = scope.ServiceProvider
            .GetRequiredService<IInventoryProvenanceReadService>();
        var rows = await reads.GetProductPurchaseProvenanceAsync(
            new GetProductPurchaseProvenanceQuery(productId, PageSize: 50),
            cancellationToken);

        return rows
            .Where(r => r.PurchaseId.HasValue)
            .GroupBy(r => r.PurchaseId!.Value)
            .Select(g =>
            {
                var first = g.First();
                return new PurchaseRecord
                {
                    BackendPurchaseId = first.PurchaseId,
                    PurchaseNumber = first.PurchaseNumber ?? "—",
                    InvoiceNumber = first.PurchaseNumber ?? "—",
                    Supplier = first.SupplierName ?? "—",
                    Date = first.CreatedAt.LocalDateTime,
                    BackendTotal = g.Sum(x => x.ReceivedQuantity * x.EffectiveUnitCost),
                    BackendItemCount = g.Count(),
                    Items = []
                };
            })
            .OrderByDescending(x => x.Date)
            .ToArray();
    }

    public async Task<BackendInventorySnapshot> GetInventorySnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var reads = scope.ServiceProvider
            .GetRequiredService<IInventoryOverviewReadService>();
        var catalog = scope.ServiceProvider
            .GetRequiredService<IPurchaseCatalogReadService>();

        var stockRows = await reads.GetStockAsync(cancellationToken);
        var movements = await reads.GetMovementsAsync(500, cancellationToken);
        var catalogRows = await catalog.GetPurchasableCatalogAsync(cancellationToken);
        var purchaseUnitByProduct = catalogRows
            .GroupBy(x => x.ProductId)
            .ToDictionary(x => x.Key, x => x.First());

        var products = stockRows.Select(row =>
        {
            purchaseUnitByProduct.TryGetValue(row.ProductId, out var purchaseUnit);
            return new PosProductItemViewModel(
                id: row.ProductId.ToString("D"),
                name: row.Name,
                sku: row.Sku ?? string.Empty,
                brand: "—",
                category: row.Category,
                stock: row.SellableQty,
                price: row.DefaultSalePrice,
                unit: string.IsNullOrWhiteSpace(row.UnitSymbol) ? "Pcs" : row.UnitSymbol,
                cost: row.MovingAverageCost,
                minimumStock: row.MinimumStockLevel,
                model: row.Model ?? string.Empty,
                backendProductId: row.ProductId,
                backendProductUnitId: purchaseUnit?.ProductUnitId,
                isSerialized: row.IsSerialized);
        }).ToArray(); var movementRows = movements.Select(row => new InventoryMovementRecord
        {
            Timestamp = row.OccurredAt.LocalDateTime,
            ProductId = row.ProductId.ToString("D"),
            ProductName = row.ProductName,
            Kind = MapMovement(row.MovementType, row.QuantityDelta),
            QuantityDelta = row.QuantityDelta,
            BeforeQuantity = row.QuantityBefore,
            AfterQuantity = row.QuantityAfter,
            Reference = row.ReferenceId is null
                ? row.ReferenceType
                : $"{row.ReferenceType} · {row.ReferenceId.Value.ToString("N")[..8]}",
            Reason = row.Reason ?? row.Note ?? row.ReferenceType
        }).ToArray();

        return new BackendInventorySnapshot(products, movementRows);
    }

    private async Task<PurchaseRecord?> LoadPurchaseAsync(
        IServiceProvider services,
        Guid purchaseId,
        CancellationToken cancellationToken)
    {
        var reads = services.GetRequiredService<IPurchasingReadService>();
        var inventory = services.GetRequiredService<IInventoryOverviewReadService>();
        var catalog = services.GetRequiredService<IPurchaseCatalogReadService>();

        var document = await reads.GetDocumentAsync(
            new GetPurchaseDocumentQuery(purchaseId),
            cancellationToken);
        if (document is null)
        {
            return null;
        }

        var stockRows = await inventory.GetStockAsync(cancellationToken);
        var catalogRows = await catalog.GetPurchasableCatalogAsync(cancellationToken);

        return ProjectPurchase(
            document,
            stockRows.ToDictionary(x => x.ProductId),
            catalogRows.ToDictionary(x => x.ProductUnitId));
    }
    private static PurchaseRecord ProjectPurchase(
        PurchaseDocumentDto document,
        IReadOnlyDictionary<Guid, InventoryStockRowDto> stockByProduct,
        IReadOnlyDictionary<Guid, PurchaseCatalogProductDto> catalogByUnit)
    {
        var items = document.Items.Select(item =>
        {
            stockByProduct.TryGetValue(item.ProductId, out var stock);
            catalogByUnit.TryGetValue(item.ProductUnitId, out var catalog);

            var factor = item.FactorToBaseSnapshot <= 0m
                ? 1m
                : item.FactorToBaseSnapshot;
            var returnedEntered = item.ReturnedBaseQuantity / factor;
            var eligibleEntered = item.EligibleBaseReturnQuantity / factor;
            var usedEntered = Math.Max(
                0m,
                item.EnteredQuantity - returnedEntered - eligibleEntered);

            var product = new PosProductItemViewModel(
                id: item.ProductId.ToString("D"),
                name: item.ProductName,
                sku: item.Sku ?? string.Empty,
                brand: "—",
                category: stock?.Category ?? catalog?.Category ?? "Uncategorized",
                stock: stock?.SellableQty ?? 0m,
                price: item.SalePriceAtPurchase,
                unit: string.IsNullOrWhiteSpace(item.UnitSymbol)
                    ? catalog?.UnitSymbol ?? "Pcs"
                    : item.UnitSymbol,
                cost: stock?.MovingAverageCost ?? item.EffectiveBaseUnitCost,
                minimumStock: stock?.MinimumStockLevel ?? 0m,
                model: stock?.Model ?? string.Empty,
                backendProductId: item.ProductId,
                backendProductUnitId: item.ProductUnitId,
                isSerialized: item.IsSerialized);

            return new PurchaseItemRecord
            {
                BackendPurchaseItemId = item.PurchaseItemId,
                BackendProductUnitId = item.ProductUnitId,
                BackendEligibleReturnQuantity = eligibleEntered,
                Product = product,
                PurchasedQuantity = item.EnteredQuantity,
                UsedQuantity = usedEntered,
                ReturnedQuantity = returnedEntered,
                Cost = item.EnteredUnitCost,
                EffectiveUnitCost = item.EffectiveBaseUnitCost,
                SalePrice = item.SalePriceAtPurchase
            };
        }).ToArray(); return new PurchaseRecord
        {
            BackendPurchaseId = document.PurchaseId,
            BackendSupplierId = document.SupplierId,
            IsVoided = document.Status == PurchaseStatus.Voided,
            PurchaseNumber = document.PurchaseNumber,
            Supplier = document.SupplierName,
            InvoiceNumber = document.SupplierInvoiceNumber,
            Date = document.PurchaseDate.ToDateTime(TimeOnly.MinValue),
            Note = document.Note ?? string.Empty,
            OtherCharges = document.OtherCharges,
            Items = items
        };
    }

    private Guid RequireActor() =>
        _actorUserId()
        ?? throw new InvalidOperationException(
            "A persistent backend user session is required for this operation.");

    private static InventoryMovementKind MapMovement(
        InventoryMovementType type,
        decimal delta) => type switch
        {
            InventoryMovementType.OpeningStock => InventoryMovementKind.OpeningBalance,
            InventoryMovementType.PurchaseIn => InventoryMovementKind.PurchaseIn,
            InventoryMovementType.PurchaseReturn => InventoryMovementKind.PurchaseReturnOut,
            InventoryMovementType.PurchaseVoid => InventoryMovementKind.PurchaseVoidOut,
            InventoryMovementType.SaleOut => InventoryMovementKind.SaleOut,
            InventoryMovementType.SaleReturn => InventoryMovementKind.SaleReturnIn,
            InventoryMovementType.ThakaOut => InventoryMovementKind.ThakaOut,
            InventoryMovementType.StockAdjustment or
            InventoryMovementType.PhysicalCountCorrection =>
                delta >= 0m
                    ? InventoryMovementKind.AdjustmentIn
                    : InventoryMovementKind.AdjustmentOut,
            _ => InventoryMovementKind.Damage
        };
}
