using System.Reflection;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Catalog;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Domain.Catalog;
using Xunit;

namespace EdgeRetails.UnitTests;

public sealed class Sprint9Phase3ProductCatalogTests
{
    private readonly FakeCatalogRepository _catalog = new();
    private readonly Phase3PermissionAuthorizer _authorization = new();
    private readonly Phase3TransactionRunner _transactions = new();
    private readonly Phase3UnitOfWork _unitOfWork = new();

    [Fact]
    public void ProductManagementOwnsProductCreation_InventoryDoesNot()
    {
        var productManagement = ReadRepoFile(
            "src", "EdgeRetails.Desktop", "ViewModels", "ProductManagementViewModel.cs");
        var inventory = ReadRepoFile(
            "src", "EdgeRetails.Desktop", "ViewModels", "InventoryViewModel.cs");
        var inventoryView = ReadRepoFile(
            "src", "EdgeRetails.Desktop", "Views", "InventoryView.xaml");

        Assert.Contains("AddProductCommand", productManagement, StringComparison.Ordinal);
        Assert.DoesNotContain("AddProductCommand", inventory, StringComparison.Ordinal);
        Assert.DoesNotContain("+ Add Product", inventoryView, StringComparison.Ordinal);

        var createParameters = typeof(CreateProductHandler)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(x => x.ParameterType)
            .ToArray();

        Assert.DoesNotContain(typeof(IInventoryRepository), createParameters);
    }

    [Fact]
    public void ProductionCatalogViewModels_UseBackendAuthority_NotDemoRetailState()
    {
        var productManagement = ReadRepoFile(
            "src", "EdgeRetails.Desktop", "ViewModels", "ProductManagementViewModel.cs");
        var productEdit = ReadRepoFile(
            "src", "EdgeRetails.Desktop", "ViewModels", "ProductEditViewModel.cs");
        var pageFactory = ReadRepoFile(
            "src", "EdgeRetails.Desktop", "Navigation", "PageViewModelFactory.cs");

        Assert.Contains("IBackendProductManagementService", productManagement, StringComparison.Ordinal);
        Assert.Contains("BackendProductManagementService", pageFactory, StringComparison.Ordinal);
        Assert.DoesNotContain("DemoRetailState", productManagement, StringComparison.Ordinal);
        Assert.DoesNotContain("DemoRetailState", productEdit, StringComparison.Ordinal);
        Assert.DoesNotContain("DemoPurchaseInventoryService", productManagement, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateProduct_NormalizesSku_CreatesBaseUnit_AndDoesNotNeedInventoryAuthority()
    {
        var unitId = Guid.NewGuid();
        var handler = new CreateProductHandler(
            _catalog,
            _authorization,
            _transactions,
            _unitOfWork);

        var result = await handler.HandleAsync(
            new CreateProductCommand(
                Guid.NewGuid(),
                ValidInput(unitId) with { Sku = "  ab-1  " }),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var product = Assert.Single(_catalog.Products.Values);
        Assert.Equal("AB-1", product.Sku);
        Assert.Equal("Acme", product.Brand);
        Assert.Equal("M1", product.Model);
        Assert.Equal(5m, product.MinimumStockLevel);
        Assert.True(product.IsActive);

        var baseUnit = Assert.Single(_catalog.ProductUnits.Values);
        Assert.Equal(product.Id, baseUnit.ProductId);
        Assert.Equal(unitId, baseUnit.UnitId);
        Assert.Equal(1m, baseUnit.FactorToBaseUnit);
        Assert.True(baseUnit.IsDefaultPurchaseUnit);
        Assert.True(baseUnit.IsDefaultSaleUnit);
    }

    [Fact]
    public async Task CreateProduct_DuplicateNormalizedSku_IsRejectedByBackendAuthority()
    {
        var existing = new Product
        {
            Name = "Existing",
            Sku = "AB-1",
            BaseUnitId = Guid.NewGuid(),
            IsActive = true,
            Version = 1
        };
        _catalog.Products[existing.Id] = existing;

        var handler = new CreateProductHandler(
            _catalog,
            _authorization,
            _transactions,
            _unitOfWork);

        var result = await handler.HandleAsync(
            new CreateProductCommand(
                Guid.NewGuid(),
                ValidInput(Guid.NewGuid()) with { Sku = " ab-1 " }),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("catalog.sku_duplicate", result.Error?.Code);
        Assert.Single(_catalog.Products);
    }

    [Fact]
    public async Task CreateProduct_InvalidSerializedTracking_IsRejected()
    {
        var input = ValidInput(Guid.NewGuid()) with
        {
            TrackingMode = TrackingMode.Serialized,
            SerialTrackingEnabled = false,
            ImeiTrackingEnabled = false
        };
        var handler = new CreateProductHandler(
            _catalog,
            _authorization,
            _transactions,
            _unitOfWork);

        var result = await handler.HandleAsync(
            new CreateProductCommand(Guid.NewGuid(), input),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Empty(_catalog.Products);
    }

    [Fact]
    public async Task UpdateProduct_StaleVersion_FailsWithoutOverwritingCatalog()
    {
        var product = SeedProduct(version: 4);
        var safety = new Phase3CatalogSafetyReadService(false);
        var handler = new UpdateProductHandler(
            _catalog,
            _authorization,
            safety,
            _transactions,
            _unitOfWork);

        var result = await handler.HandleAsync(
            new UpdateProductCommand(
                Guid.NewGuid(),
                product.Id,
                3,
                ValidInput(product.BaseUnitId) with { Name = "Stale Write" }),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("concurrency.stale_product", result.Error?.Code);
        Assert.Equal("Phone", product.Name);
        Assert.Equal(4, product.Version);
    }

    [Fact]
    public async Task UpdateProduct_BaseUnitChange_IsRejectedByApplicationAuthority()
    {
        var product = SeedProduct(version: 2);
        var handler = new UpdateProductHandler(
            _catalog,
            _authorization,
            new Phase3CatalogSafetyReadService(false),
            _transactions,
            _unitOfWork);

        var result = await handler.HandleAsync(
            new UpdateProductCommand(
                Guid.NewGuid(),
                product.Id,
                product.Version,
                ValidInput(Guid.NewGuid())),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("catalog.base_unit_change_requires_reconfiguration", result.Error?.Code);
        Assert.Equal(2, product.Version);
    }

    [Fact]
    public async Task UpdateProduct_TrackingPolicyChange_IsLockedWhenInventoryHistoryExists()
    {
        var product = SeedProduct(version: 2);
        var handler = new UpdateProductHandler(
            _catalog,
            _authorization,
            new Phase3CatalogSafetyReadService(true),
            _transactions,
            _unitOfWork);

        var result = await handler.HandleAsync(
            new UpdateProductCommand(
                Guid.NewGuid(),
                product.Id,
                product.Version,
                ValidInput(product.BaseUnitId) with
                {
                    TrackingMode = TrackingMode.Serialized,
                    SerialTrackingEnabled = true
                }),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("catalog.tracking_policy_locked", result.Error?.Code);
        Assert.Equal(TrackingMode.Quantity, product.TrackingMode);
    }

    [Fact]
    public async Task UpdateProduct_CatalogFieldsOnly_AdvancesOptimisticVersion()
    {
        var product = SeedProduct(version: 7);
        var handler = new UpdateProductHandler(
            _catalog,
            _authorization,
            new Phase3CatalogSafetyReadService(false),
            _transactions,
            _unitOfWork);

        var result = await handler.HandleAsync(
            new UpdateProductCommand(
                Guid.NewGuid(),
                product.Id,
                7,
                ValidInput(product.BaseUnitId) with
                {
                    Name = "Updated Phone",
                    DefaultSalePrice = 2250m,
                    MinimumStockLevel = 9m
                }),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Updated Phone", product.Name);
        Assert.Equal(2250m, product.DefaultSalePrice);
        Assert.Equal(9m, product.MinimumStockLevel);
        Assert.Equal(8, product.Version);
    }

    [Fact]
    public async Task DeactivateAndReactivate_AreSoftLifecycleTransitions()
    {
        var product = SeedProduct(version: 1);
        var deactivate = new DeactivateProductHandler(
            _catalog,
            _authorization,
            _transactions,
            _unitOfWork);

        var deactivated = await deactivate.HandleAsync(
            new DeactivateProductCommand(
                Guid.NewGuid(),
                product.Id,
                1),
            CancellationToken.None);

        Assert.True(deactivated.IsSuccess);
        Assert.False(product.IsActive);
        Assert.Equal("PH-1", product.Sku);
        Assert.Equal(2, product.Version);

        var reactivate = new ReactivateProductHandler(
            _catalog,
            _authorization,
            _transactions,
            _unitOfWork);
        var reactivated = await reactivate.HandleAsync(
            new ReactivateProductCommand(
                Guid.NewGuid(),
                product.Id,
                2),
            CancellationToken.None);

        Assert.True(reactivated.IsSuccess);
        Assert.True(product.IsActive);
        Assert.Equal(3, product.Version);
    }

    private static string ReadRepoFile(params string[] segments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "EdgeRetails.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = segments.Aggregate(
            directory!.FullName,
            Path.Combine);
        return File.ReadAllText(path);
    }

    private Product SeedProduct(long version)
    {
        var product = new Product
        {
            Name = "Phone",
            Sku = "PH-1",
            Brand = "Acme",
            Model = "M1",
            BaseUnitId = Guid.NewGuid(),
            TrackingMode = TrackingMode.Quantity,
            DefaultSalePrice = 2000m,
            MinimumStockLevel = 5m,
            IsActive = true,
            Version = version
        };
        _catalog.Products[product.Id] = product;
        return product;
    }

    private static ProductCatalogInput ValidInput(Guid unitId) =>
        new(
            Name: "Phone",
            Sku: "PH-1",
            Brand: "Acme",
            Model: "M1",
            CategoryId: null,
            BaseUnitId: unitId,
            TrackingMode: TrackingMode.Quantity,
            SerialTrackingEnabled: false,
            ImeiTrackingEnabled: false,
            ReferencePurchaseCost: 1500m,
            DefaultSalePrice: 2000m,
            MinimumStockLevel: 5m,
            DefaultWarrantyMonths: 12,
            AttributesJson: null,
            AttributesSchemaVersion: 1);
}

internal sealed class Phase3PermissionAuthorizer : IApplicationPermissionAuthorizer
{
    public Task<Result> AuthorizeAsync(
        Guid actorId,
        string permissionKey,
        CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());
}

internal sealed class Phase3CatalogSafetyReadService : IProductCatalogSafetyReadService
{
    private readonly bool _hasHistory;
    public Phase3CatalogSafetyReadService(bool hasHistory) => _hasHistory = hasHistory;

    public Task<bool> HasStockOrHistoryAsync(
        Guid productId,
        CancellationToken cancellationToken) =>
        Task.FromResult(_hasHistory);
}

internal sealed class Phase3TransactionRunner : ITransactionRunner
{
    public Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken) =>
        operation(cancellationToken);
}

internal sealed class Phase3UnitOfWork : IUnitOfWork
{
    public int SaveCount { get; private set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        SaveCount++;
        return Task.FromResult(1);
    }
}
