using EdgeRetails.Application.Features.Catalog;
using EdgeRetails.Application.Features.Parties;

namespace EdgeRetails.Desktop.Services;

/// <summary>Catalog management adapter. All product and reference mutations go to Server.</summary>
public sealed class RemoteProductManagementService(
    DesktopApiClient apiClient,
    Func<Guid?> actorUserId) : IBackendProductManagementService
{
    public async Task<BackendProductManagementSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var products = await GetProductsPageAsync(null, null, null, 200, cancellationToken: cancellationToken);
        var categories = await apiClient.GetAsync<CatalogCategoryDto[]>(
            "/api/catalog/categories?includeInactive=true", cancellationToken);
        var units = await apiClient.GetAsync<CatalogUnitDto[]>(
            "/api/catalog/units?includeInactive=true", cancellationToken);
        var companies = await apiClient.GetAsync<CatalogCompanyDto[]>(
            "/api/catalog/companies?includeInactive=true", cancellationToken);
        var suppliers = await apiClient.GetAsync<SupplierDirectoryDto[]>(
            "/api/suppliers?pageSize=200", cancellationToken);
        return new BackendProductManagementSnapshot(
            products,
            [.. categories.Select(x => new BackendCatalogCategory(x.Id, x.Name, x.IdentitySymbol, x.IsActive))],
            [.. units.Select(x => new BackendCatalogUnit(x.Id, x.Name, x.Symbol, x.DisplayDecimalPlaces, x.IsActive))],
            [.. suppliers.Select(x => new BackendSupplierOption(x.SupplierId, x.Name))],
            [.. companies.Select(x => new BackendCatalogCompany(x.Id, x.Name, x.Code, x.IsActive))]);
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
        if (string.IsNullOrWhiteSpace(beforeName) != !beforeProductId.HasValue)
        {
            throw new ArgumentException("Product paging cursor requires both name and id.");
        }

        var query = $"/api/catalog/products?pageSize={Math.Clamp(pageSize, 1, 200)}";
        if (!string.IsNullOrWhiteSpace(search))
        {
            query += $"&search={Uri.EscapeDataString(search.Trim())}";
        }

        if (isActive is not null)
        {
            query += $"&isActive={isActive.Value.ToString().ToLowerInvariant()}";
        }

        if (categoryId is not null)
        {
            query += $"&categoryId={categoryId:D}";
        }

        if (!string.IsNullOrWhiteSpace(beforeName) && beforeProductId is Guid cursor)
        {
            query += $"&beforeName={Uri.EscapeDataString(beforeName)}&beforeProductId={cursor:D}";
        }

        var rows = await apiClient.GetAsync<ProductManagementRowDto[]>(query, cancellationToken);
        return [.. rows.Select(BackendProductManagementService.Map)];
    }

    public async Task<BackendProductManagementItem?> GetProductAsync(
        Guid productId, CancellationToken cancellationToken = default)
    {
        try
        {
            var row = await apiClient.GetAsync<ProductManagementRowDto>(
                $"/api/catalog/products/{productId:D}", cancellationToken);
            return BackendProductManagementService.Map(row);
        }
        catch (DesktopApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<BackendProductManagementItem?> GetProductBySkuAsync(
        string sku, CancellationToken cancellationToken = default)
    {
        try
        {
            var row = await apiClient.GetAsync<ProductManagementRowDto>(
                $"/api/catalog/products/sku/{Uri.EscapeDataString(sku.Trim())}", cancellationToken);
            return BackendProductManagementService.Map(row);
        }
        catch (DesktopApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<BackendProductManagementItem> CreateProductAsync(
        BackendProductCatalogRequest request,
        IReadOnlyList<BackendProductUnitConfiguration> units,
        IReadOnlyCollection<Guid> linkedSupplierIds,
        CancellationToken cancellationToken = default)
    {
        var actor = RequireActor();
        var created = await apiClient.PostAsync<CreateProductCommand, ProductMutationResult>(
            "/api/catalog/products",
            new CreateProductCommand(actor, ToInput(request)), cancellationToken);
        await ConfigureUnitsAsync(created.ProductId, request.BaseUnitId, units, cancellationToken);
        await SyncSupplierLinksAsync(created.ProductId, linkedSupplierIds, cancellationToken);
        return await GetProductAsync(created.ProductId, cancellationToken)
            ?? throw Error("catalog.product_readback_failed", "Product was created but could not be read back.");
    }

    public async Task<BackendProductManagementItem> UpdateProductAsync(
        Guid productId,
        long expectedVersion,
        BackendProductCatalogRequest request,
        IReadOnlyList<BackendProductUnitConfiguration> units,
        IReadOnlyCollection<Guid> linkedSupplierIds,
        CancellationToken cancellationToken = default)
    {
        var existing = await GetProductAsync(productId, cancellationToken)
            ?? throw Error("catalog.product_not_found", "Product was not found.");
        if (existing.BaseUnitId != request.BaseUnitId)
        {
            throw Error("catalog.base_unit_change_requires_reconfiguration", "Base unit cannot be changed from the normal edit workflow.");
        }

        var actor = RequireActor();
        await apiClient.PutAsync<UpdateProductCommand, ProductMutationResult>(
            $"/api/catalog/products/{productId:D}",
            new UpdateProductCommand(actor, productId, expectedVersion, ToInput(request)), cancellationToken);
        await ConfigureUnitsAsync(productId, request.BaseUnitId, units, cancellationToken);
        await SyncSupplierLinksAsync(productId, linkedSupplierIds, cancellationToken);
        return await GetProductAsync(productId, cancellationToken)
            ?? throw Error("catalog.product_readback_failed", "Product was updated but could not be read back.");
    }

    public async Task<BackendProductManagementItem> SetProductActiveAsync(
        Guid productId,
        long expectedVersion,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        var path = $"/api/catalog/products/{productId:D}/{(isActive ? "reactivate" : "deactivate")}";
        await apiClient.PostAsync<ProductActiveRequest, SuccessResponse>(
            path, new ProductActiveRequest(expectedVersion), cancellationToken);
        return await GetProductAsync(productId, cancellationToken)
            ?? throw Error("catalog.product_readback_failed", "Product status was updated but could not be read back.");
    }

    public async Task SaveCompanyAsync(Guid? companyId, string name, string? code = null, CancellationToken cancellationToken = default)
    {
        await apiClient.PostAsync<SaveCompanyCommand, Guid>(
            "/api/catalog/companies",
            new SaveCompanyCommand(RequireActor(), companyId, name, code), cancellationToken);
    }

    public Task SetCompanyActiveAsync(Guid companyId, bool isActive, CancellationToken cancellationToken = default) =>
        PostSuccessAsync($"/api/catalog/companies/{companyId:D}/active", new { isActive }, cancellationToken);

    public async Task SaveCategoryAsync(Guid? categoryId, string name, string? identitySymbol = null, CancellationToken cancellationToken = default)
    {
        await apiClient.PostAsync<SaveCategoryCommand, Guid>(
            "/api/catalog/categories",
            new SaveCategoryCommand(RequireActor(), categoryId, name, identitySymbol), cancellationToken);
    }

    public Task SetCategoryActiveAsync(Guid categoryId, bool isActive, CancellationToken cancellationToken = default) =>
        PostSuccessAsync($"/api/catalog/categories/{categoryId:D}/active", new { isActive }, cancellationToken);

    public async Task SaveUnitAsync(Guid? unitId, string name, string symbol, int displayDecimalPlaces, CancellationToken cancellationToken = default)
    {
        await apiClient.PostAsync<SaveUnitCommand, Guid>(
            "/api/catalog/units",
            new SaveUnitCommand(RequireActor(), unitId, name, symbol, displayDecimalPlaces), cancellationToken);
    }

    public Task SetUnitActiveAsync(Guid unitId, bool isActive, CancellationToken cancellationToken = default) =>
        PostSuccessAsync($"/api/catalog/units/{unitId:D}/active", new { isActive }, cancellationToken);

    private async Task ConfigureUnitsAsync(
        Guid productId,
        Guid baseUnitId,
        IReadOnlyList<BackendProductUnitConfiguration> units,
        CancellationToken cancellationToken)
    {
        var normalized = units.Where(x => x.IsActive)
            .Select(x => new ProductUnitInput(
                x.UnitId,
                x.UnitId == baseUnitId ? 1m : x.FactorToBaseUnit,
                x.CanPurchase,
                x.CanSell,
                x.CanUseInThaka,
                x.IsDefaultPurchaseUnit,
                x.IsDefaultSaleUnit)).ToList();
        if (normalized.All(x => x.UnitId != baseUnitId))
        {
            normalized.Insert(0, new ProductUnitInput(baseUnitId, 1m, true, true, true, true, true));
        }

        await apiClient.PostAsync<ProductUnitInput[], SuccessResponse>(
            $"/api/catalog/products/{productId:D}/units", [.. normalized], cancellationToken);
    }

    private async Task SyncSupplierLinksAsync(
        Guid productId,
        IReadOnlyCollection<Guid> desiredSupplierIds,
        CancellationToken cancellationToken)
    {
        var existing = await GetProductAsync(productId, cancellationToken)
            ?? throw Error("catalog.product_not_found", "Product was not found after save.");
        var currentBySupplier = existing.SupplierProducts.ToDictionary(x => x.SupplierId);
        var desired = desiredSupplierIds.ToHashSet();
        foreach (var supplierId in desired)
        {
            currentBySupplier.TryGetValue(supplierId, out var link);
            if (link?.IsActive == true)
            {
                continue;
            }

            await PostSuccessAsync(
                $"/api/catalog/products/{productId:D}/suppliers/{supplierId:D}/active",
                new { isActive = true, expectedVersion = link?.Version }, cancellationToken);
        }
        foreach (var link in existing.SupplierProducts.Where(x => x.IsActive && !desired.Contains(x.SupplierId)))
        {
            await PostSuccessAsync(
                $"/api/catalog/products/{productId:D}/suppliers/{link.SupplierId:D}/active",
                new { isActive = false, expectedVersion = link.Version }, cancellationToken);
        }
    }

    private async Task PostSuccessAsync<TRequest>(string path, TRequest request, CancellationToken cancellationToken)
    {
        var result = await apiClient.PostAsync<TRequest, SuccessResponse>(path, request, cancellationToken);
        if (!result.Success)
        {
            throw Error("catalog.operation_unconfirmed", "Server did not confirm the catalog change.");
        }
    }

    private Guid RequireActor() => actorUserId() is Guid actor && actor != Guid.Empty
        ? actor
        : throw Error("identity.session_required", "A persistent backend user session is required.");

    private static ProductCatalogInput ToInput(BackendProductCatalogRequest request) => new(
        request.Name, request.Sku, request.Brand, request.Model, request.CategoryId,
        request.CompanyId, request.ModelCode, request.BaseUnitId, request.TrackingMode,
        request.SerialTrackingEnabled, request.ImeiTrackingEnabled, request.ReferencePurchaseCost,
        request.DefaultSalePrice, request.MinimumStockLevel, request.DefaultWarrantyMonths,
        request.AttributesJson, request.AttributesSchemaVersion);

    private static BackendCatalogOperationException Error(string code, string message) => new(code, message);
    private sealed record SuccessResponse(bool Success);
    private sealed record ProductActiveRequest(long ExpectedVersion);
}
