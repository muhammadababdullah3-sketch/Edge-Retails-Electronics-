using EdgeRetails.Application.Features.Catalog;
using EdgeRetails.Application.Features.Parties;
using EdgeRetails.Domain.Catalog;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.Desktop.Services;

public sealed record BackendCatalogCategory(Guid Id, string Name, bool IsActive)
{
    public override string ToString() => Name;
}

public sealed record BackendCatalogUnit(
    Guid Id,
    string Name,
    string Symbol,
    int DisplayDecimalPlaces,
    bool IsActive)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Symbol) ? Name : $"{Name} ({Symbol})";
    public override string ToString() => DisplayName;
}

public sealed record BackendProductUnitConfiguration(
    Guid? ProductUnitId,
    Guid UnitId,
    string UnitName,
    string UnitSymbol,
    decimal FactorToBaseUnit,
    bool CanPurchase,
    bool CanSell,
    bool CanUseInThaka,
    bool IsDefaultPurchaseUnit,
    bool IsDefaultSaleUnit,
    bool IsActive);

public sealed record BackendSupplierProductLink(
    Guid SupplierProductId,
    Guid SupplierId,
    string SupplierName,
    bool IsActive,
    long Version);

public sealed record BackendProductManagementItem(
    Guid ProductId,
    string Sku,
    string Name,
    string? Brand,
    string? Model,
    Guid? CategoryId,
    string Category,
    Guid BaseUnitId,
    string BaseUnit,
    TrackingMode TrackingMode,
    bool SerialTrackingEnabled,
    bool ImeiTrackingEnabled,
    decimal? ReferencePurchaseCost,
    decimal DefaultSalePrice,
    decimal MinimumStockLevel,
    int DefaultWarrantyMonths,
    string? AttributesJson,
    int AttributesSchemaVersion,
    bool IsActive,
    long Version,
    IReadOnlyList<BackendProductUnitConfiguration> ProductUnits,
    IReadOnlyList<BackendSupplierProductLink> SupplierProducts)
{
    public string TrackingDisplay => TrackingMode switch
    {
        TrackingMode.Quantity => "Quantity",
        TrackingMode.Length => "Length",
        TrackingMode.Serialized when ImeiTrackingEnabled && SerialTrackingEnabled => "Serial + IMEI",
        TrackingMode.Serialized when ImeiTrackingEnabled => "IMEI",
        TrackingMode.Serialized => "Serial",
        _ => TrackingMode.ToString()
    };

    public string PriceDisplay => $"Rs. {DefaultSalePrice:N2}";
    public string MinimumStockDisplay => $"{MinimumStockLevel:0.##} {BaseUnit}";
    public string StatusDisplay => IsActive ? "Active" : "Inactive";
    public string SupplierDisplay => SupplierProducts.Count(x => x.IsActive) == 0
        ? "No supplier links"
        : $"{SupplierProducts.Count(x => x.IsActive)} supplier link(s)";
}

public sealed record BackendProductManagementSnapshot(
    IReadOnlyList<BackendProductManagementItem> Products,
    IReadOnlyList<BackendCatalogCategory> Categories,
    IReadOnlyList<BackendCatalogUnit> Units,
    IReadOnlyList<BackendSupplierOption> Suppliers);

public sealed record BackendProductCatalogRequest(
    string Name,
    string Sku,
    string? Brand,
    string? Model,
    Guid? CategoryId,
    Guid BaseUnitId,
    TrackingMode TrackingMode,
    bool SerialTrackingEnabled,
    bool ImeiTrackingEnabled,
    decimal? ReferencePurchaseCost,
    decimal DefaultSalePrice,
    decimal MinimumStockLevel,
    int DefaultWarrantyMonths,
    string? AttributesJson,
    int AttributesSchemaVersion);

public sealed class BackendCatalogOperationException : Exception
{
    public BackendCatalogOperationException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public interface IBackendProductManagementService
{
    Task<BackendProductManagementSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BackendProductManagementItem>> GetProductsPageAsync(
        string? search,
        bool? isActive,
        Guid? categoryId,
        int pageSize = 200,
        string? beforeName = null,
        Guid? beforeProductId = null,
        CancellationToken cancellationToken = default);

    Task<BackendProductManagementItem?> GetProductAsync(
        Guid productId,
        CancellationToken cancellationToken = default);

    Task<BackendProductManagementItem?> GetProductBySkuAsync(
        string sku,
        CancellationToken cancellationToken = default);

    Task<BackendProductManagementItem> CreateProductAsync(
        BackendProductCatalogRequest request,
        IReadOnlyList<BackendProductUnitConfiguration> units,
        IReadOnlyCollection<Guid> linkedSupplierIds,
        CancellationToken cancellationToken = default);

    Task<BackendProductManagementItem> UpdateProductAsync(
        Guid productId,
        long expectedVersion,
        BackendProductCatalogRequest request,
        IReadOnlyList<BackendProductUnitConfiguration> units,
        IReadOnlyCollection<Guid> linkedSupplierIds,
        CancellationToken cancellationToken = default);

    Task<BackendProductManagementItem> SetProductActiveAsync(
        Guid productId,
        long expectedVersion,
        bool isActive,
        CancellationToken cancellationToken = default);

    Task SaveCategoryAsync(
        Guid? categoryId,
        string name,
        CancellationToken cancellationToken = default);

    Task SetCategoryActiveAsync(
        Guid categoryId,
        bool isActive,
        CancellationToken cancellationToken = default);

    Task SaveUnitAsync(
        Guid? unitId,
        string name,
        string symbol,
        int displayDecimalPlaces,
        CancellationToken cancellationToken = default);

    Task SetUnitActiveAsync(
        Guid unitId,
        bool isActive,
        CancellationToken cancellationToken = default);
}

public sealed class BackendProductManagementService : IBackendProductManagementService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Func<Guid?> _actorUserId;

    public BackendProductManagementService(
        IServiceScopeFactory scopeFactory,
        Func<Guid?> actorUserId)
    {
        _scopeFactory = scopeFactory;
        _actorUserId = actorUserId;
    }

    public async Task<BackendProductManagementSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var reads = scope.ServiceProvider.GetRequiredService<IProductManagementReadService>();
        var suppliers = scope.ServiceProvider.GetRequiredService<GetSuppliersHandler>();

        var productRows = await reads.GetProductsPageAsync(
            new ProductManagementPageQuery(IncludeInactive: true, PageSize: 200),
            cancellationToken);
        var categoryRows = await reads.GetCategoriesAsync(true, cancellationToken);
        var unitRows = await reads.GetUnitsAsync(true, cancellationToken);

        if (unitRows.Count == 0)
        {
            var actor = _actorUserId() ?? Guid.Empty;
            if (actor != Guid.Empty)
            {
                var saveUnitHandler = scope.ServiceProvider.GetRequiredService<SaveUnitHandler>();
                await saveUnitHandler.HandleAsync(new SaveUnitCommand(actor, null, "Piece", "Pcs", 0), cancellationToken);
                await saveUnitHandler.HandleAsync(new SaveUnitCommand(actor, null, "Meter", "m", 2), cancellationToken);
                await saveUnitHandler.HandleAsync(new SaveUnitCommand(actor, null, "Box", "Box", 0), cancellationToken);

                if (categoryRows.Count == 0)
                {
                    var saveCategoryHandler = scope.ServiceProvider.GetRequiredService<SaveCategoryHandler>();
                    await saveCategoryHandler.HandleAsync(new SaveCategoryCommand(actor, null, "General Electronics"), cancellationToken);
                }

                categoryRows = await reads.GetCategoriesAsync(true, cancellationToken);
                unitRows = await reads.GetUnitsAsync(true, cancellationToken);
            }
        }

        var supplierRows = await suppliers.HandleAsync(false, cancellationToken);

        return new BackendProductManagementSnapshot(
            productRows.Select(Map).ToArray(),
            categoryRows.Select(x => new BackendCatalogCategory(x.Id, x.Name, x.IsActive)).ToArray(),
            unitRows.Select(x => new BackendCatalogUnit(
                x.Id,
                x.Name,
                x.Symbol,
                x.DisplayDecimalPlaces,
                x.IsActive)).ToArray(),
            supplierRows
                .Where(x => x.IsActive)
                .Select(x => new BackendSupplierOption(x.Id, x.Name))
                .ToArray());
    }

    public async Task<IReadOnlyList<BackendProductManagementItem>> GetProductsPageAsync(
        string? search,
        bool? isActive,
        Guid? categoryId,
        int pageSize = 200,
        string? beforeName = null,
        Guid? beforeProductId = null,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var reads = scope.ServiceProvider.GetRequiredService<IProductManagementReadService>();
        var rows = await reads.GetProductsPageAsync(
            new ProductManagementPageQuery(
                IncludeInactive: isActive != true,
                Search: string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
                PageSize: Math.Clamp(pageSize, 1, 200),
                BeforeName: beforeName,
                BeforeProductId: beforeProductId,
                CategoryId: categoryId,
                IsActive: isActive),
            cancellationToken);
        return rows.Select(Map).ToArray();
    }

    public async Task<BackendProductManagementItem?> GetProductBySkuAsync(
        string sku,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var reads = scope.ServiceProvider.GetRequiredService<IProductManagementReadService>();
        var row = await reads.GetProductBySkuAsync(sku, cancellationToken);
        return row is null ? null : Map(row);
    }

    public async Task<BackendProductManagementItem?> GetProductAsync(
        Guid productId,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var reads = scope.ServiceProvider.GetRequiredService<IProductManagementReadService>();
        var row = await reads.GetProductAsync(productId, cancellationToken);
        return row is null ? null : Map(row);
    }

    public async Task<BackendProductManagementItem> CreateProductAsync(
        BackendProductCatalogRequest request,
        IReadOnlyList<BackendProductUnitConfiguration> units,
        IReadOnlyCollection<Guid> linkedSupplierIds,
        CancellationToken cancellationToken = default)
    {
        var actor = RequireActor();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<CreateProductHandler>();
        var result = await handler.HandleAsync(
            new CreateProductCommand(actor, ToInput(request)),
            cancellationToken);
        EnsureSuccess(result.IsSuccess, result.Error);

        var productId = result.Value!.ProductId;
        await ConfigureUnitsAsync(scope.ServiceProvider, productId, request.BaseUnitId, units, cancellationToken);
        await SyncSupplierLinksAsync(
            scope.ServiceProvider,
            productId,
            linkedSupplierIds,
            actor,
            cancellationToken);

        return await ReadRequiredAsync(scope.ServiceProvider, productId, cancellationToken);
    }

    public async Task<BackendProductManagementItem> UpdateProductAsync(
        Guid productId,
        long expectedVersion,
        BackendProductCatalogRequest request,
        IReadOnlyList<BackendProductUnitConfiguration> units,
        IReadOnlyCollection<Guid> linkedSupplierIds,
        CancellationToken cancellationToken = default)
    {
        var actor = RequireActor();
        await using var scope = _scopeFactory.CreateAsyncScope();

        var existing = await scope.ServiceProvider
            .GetRequiredService<IProductManagementReadService>()
            .GetProductAsync(productId, cancellationToken);
        if (existing is null)
        {
            throw new BackendCatalogOperationException(
                "catalog.product_not_found",
                "Product was not found.");
        }

        if (existing.BaseUnitId != request.BaseUnitId)
        {
            throw new BackendCatalogOperationException(
                "catalog.base_unit_change_requires_reconfiguration",
                "Base unit cannot be changed from the normal edit workflow. Reconfigure product units first.");
        }

        var handler = scope.ServiceProvider.GetRequiredService<UpdateProductHandler>();
        var result = await handler.HandleAsync(
            new UpdateProductCommand(actor, productId, expectedVersion, ToInput(request)),
            cancellationToken);
        EnsureSuccess(result.IsSuccess, result.Error);

        await ConfigureUnitsAsync(scope.ServiceProvider, productId, request.BaseUnitId, units, cancellationToken);
        await SyncSupplierLinksAsync(
            scope.ServiceProvider,
            productId,
            linkedSupplierIds,
            actor,
            cancellationToken);

        return await ReadRequiredAsync(scope.ServiceProvider, productId, cancellationToken);
    }

    public async Task<BackendProductManagementItem> SetProductActiveAsync(
        Guid productId,
        long expectedVersion,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        var actor = RequireActor();
        await using var scope = _scopeFactory.CreateAsyncScope();

        if (isActive)
        {
            var handler = scope.ServiceProvider.GetRequiredService<ReactivateProductHandler>();
            var result = await handler.HandleAsync(
                new ReactivateProductCommand(actor, productId, expectedVersion),
                cancellationToken);
            EnsureSuccess(result.IsSuccess, result.Error);
        }
        else
        {
            var handler = scope.ServiceProvider.GetRequiredService<DeactivateProductHandler>();
            var result = await handler.HandleAsync(
                new DeactivateProductCommand(actor, productId, expectedVersion),
                cancellationToken);
            EnsureSuccess(result.IsSuccess, result.Error);
        }

        return await ReadRequiredAsync(scope.ServiceProvider, productId, cancellationToken);
    }

    public async Task SaveCategoryAsync(
        Guid? categoryId,
        string name,
        CancellationToken cancellationToken = default)
    {
        var actor = RequireActor();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<SaveCategoryHandler>();
        var result = await handler.HandleAsync(
            new SaveCategoryCommand(actor, categoryId, name),
            cancellationToken);
        EnsureSuccess(result.IsSuccess, result.Error);
    }

    public async Task SetCategoryActiveAsync(
        Guid categoryId,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        var actor = RequireActor();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<SetCategoryActiveHandler>();
        var result = await handler.HandleAsync(
            new SetCategoryActiveCommand(actor, categoryId, isActive),
            cancellationToken);
        EnsureSuccess(result.IsSuccess, result.Error);
    }

    public async Task SaveUnitAsync(
        Guid? unitId,
        string name,
        string symbol,
        int displayDecimalPlaces,
        CancellationToken cancellationToken = default)
    {
        var actor = RequireActor();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<SaveUnitHandler>();
        var result = await handler.HandleAsync(
            new SaveUnitCommand(actor, unitId, name, symbol, displayDecimalPlaces),
            cancellationToken);
        EnsureSuccess(result.IsSuccess, result.Error);
    }

    public async Task SetUnitActiveAsync(
        Guid unitId,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        var actor = RequireActor();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<SetUnitActiveHandler>();
        var result = await handler.HandleAsync(
            new SetUnitActiveCommand(actor, unitId, isActive),
            cancellationToken);
        EnsureSuccess(result.IsSuccess, result.Error);
    }

    private static async Task ConfigureUnitsAsync(
        IServiceProvider services,
        Guid productId,
        Guid baseUnitId,
        IReadOnlyList<BackendProductUnitConfiguration> units,
        CancellationToken cancellationToken)
    {
        var normalized = units
            .Where(x => x.IsActive)
            .Select(x => new ProductUnitInput(
                x.UnitId,
                x.UnitId == baseUnitId ? 1m : x.FactorToBaseUnit,
                x.CanPurchase,
                x.CanSell,
                x.CanUseInThaka,
                x.IsDefaultPurchaseUnit,
                x.IsDefaultSaleUnit))
            .ToList();

        if (normalized.All(x => x.UnitId != baseUnitId))
        {
            normalized.Insert(0, new ProductUnitInput(
                baseUnitId,
                1m,
                true,
                true,
                true,
                true,
                true));
        }

        var handler = services.GetRequiredService<ConfigureProductUnitsHandler>();
        var result = await handler.HandleAsync(
            new ConfigureProductUnitsCommand(productId, normalized),
            cancellationToken);
        EnsureSuccess(result.IsSuccess, result.Error);
    }

    private static async Task SyncSupplierLinksAsync(
        IServiceProvider services,
        Guid productId,
        IReadOnlyCollection<Guid> desiredSupplierIds,
        Guid actor,
        CancellationToken cancellationToken)
    {
        var reads = services.GetRequiredService<IProductManagementReadService>();
        var handler = services.GetRequiredService<SetSupplierProductActiveHandler>();
        var current = await reads.GetProductAsync(productId, cancellationToken)
            ?? throw new BackendCatalogOperationException(
                "catalog.product_not_found",
                "Product was not found after save.");

        var desired = desiredSupplierIds.ToHashSet();
        var existing = current.SupplierProducts.ToDictionary(x => x.SupplierId);

        foreach (var supplierId in desired)
        {
            existing.TryGetValue(supplierId, out var link);
            if (link is { IsActive: true })
            {
                continue;
            }

            var result = await handler.HandleAsync(
                new SetSupplierProductActiveCommand(
                    actor,
                    productId,
                    supplierId,
                    true,
                    link?.Version),
                cancellationToken);
            EnsureSuccess(result.IsSuccess, result.Error);
        }

        foreach (var link in current.SupplierProducts.Where(x => x.IsActive))
        {
            if (desired.Contains(link.SupplierId))
            {
                continue;
            }

            var result = await handler.HandleAsync(
                new SetSupplierProductActiveCommand(
                    actor,
                    productId,
                    link.SupplierId,
                    false,
                    link.Version),
                cancellationToken);
            EnsureSuccess(result.IsSuccess, result.Error);
        }
    }

    private static async Task<BackendProductManagementItem> ReadRequiredAsync(
        IServiceProvider services,
        Guid productId,
        CancellationToken cancellationToken)
    {
        var row = await services
            .GetRequiredService<IProductManagementReadService>()
            .GetProductAsync(productId, cancellationToken);

        return row is null
            ? throw new BackendCatalogOperationException(
                "catalog.product_readback_failed",
                "Product was saved but could not be read back.")
            : Map(row);
    }

    private static ProductCatalogInput ToInput(BackendProductCatalogRequest request) =>
        new(
            request.Name,
            request.Sku,
            request.Brand,
            request.Model,
            request.CategoryId,
            request.BaseUnitId,
            request.TrackingMode,
            request.SerialTrackingEnabled,
            request.ImeiTrackingEnabled,
            request.ReferencePurchaseCost,
            request.DefaultSalePrice,
            request.MinimumStockLevel,
            request.DefaultWarrantyMonths,
            request.AttributesJson,
            request.AttributesSchemaVersion);

    private static BackendProductManagementItem Map(ProductManagementRowDto row) =>
        new(
            row.ProductId,
            row.Sku,
            row.Name,
            row.Brand,
            row.Model,
            row.CategoryId,
            row.Category,
            row.BaseUnitId,
            row.BaseUnit,
            row.TrackingMode,
            row.SerialTrackingEnabled,
            row.ImeiTrackingEnabled,
            row.ReferencePurchaseCost,
            row.DefaultSalePrice,
            row.MinimumStockLevel,
            row.DefaultWarrantyMonths,
            row.AttributesJson,
            row.AttributesSchemaVersion,
            row.IsActive,
            row.Version,
            row.ProductUnits.Select(x => new BackendProductUnitConfiguration(
                x.ProductUnitId,
                x.UnitId,
                x.UnitName,
                x.UnitSymbol,
                x.FactorToBaseUnit,
                x.CanPurchase,
                x.CanSell,
                x.CanUseInThaka,
                x.IsDefaultPurchaseUnit,
                x.IsDefaultSaleUnit,
                x.IsActive)).ToArray(),
            row.SupplierProducts.Select(x => new BackendSupplierProductLink(
                x.SupplierProductId,
                x.SupplierId,
                x.SupplierName,
                x.IsActive,
                x.Version)).ToArray());

    private Guid RequireActor() =>
        _actorUserId() is Guid actor && actor != Guid.Empty
            ? actor
            : throw new BackendCatalogOperationException(
                "authorization.session_required",
                "An authenticated backend user is required.");

    private static void EnsureSuccess(bool isSuccess, EdgeRetails.Application.Common.Error? error)
    {
        if (!isSuccess)
        {
            throw new BackendCatalogOperationException(
                error?.Code ?? "catalog.operation_failed",
                error?.Message ?? "Catalog operation failed.");
        }
    }
}
