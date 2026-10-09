using System.Globalization;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Catalog;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Parties;

namespace EdgeRetails.UnitTests;

// NEW_COVERAGE: application contracts; relational effects are asserted separately in PostgreSQL.
public sealed class Phase7Pass4AggregateTests
{
    [Theory]
    [InlineData("actor")]
    [InlineData("version")]
    [InlineData("brand")]
    [InlineData("model")]
    [InlineData("company")]
    [InlineData("modelCode")]
    [InlineData("serial")]
    [InlineData("imei")]
    [InlineData("cost")]
    [InlineData("price")]
    [InlineData("minimum")]
    [InlineData("warranty")]
    [InlineData("attributes")]
    [InlineData("schema")]
    public void G01_FingerprintBindsEveryPreviouslyOmittedIntent(string field)
    {
        var command = Command();
        var changed = field switch
        {
            "actor" => command with { ActorId = Guid.NewGuid() },
            "version" => command with { ExpectedVersion = 2 },
            "brand" => command with { Product = command.Product with { Brand = "Other" } },
            "model" => command with { Product = command.Product with { Model = "Other" } },
            "company" => command with { Product = command.Product with { CompanyId = Guid.NewGuid() } },
            "modelCode" => command with { Product = command.Product with { ModelCode = "OTHER" } },
            "serial" => command with { Product = command.Product with { SerialTrackingEnabled = true } },
            "imei" => command with { Product = command.Product with { ImeiTrackingEnabled = true } },
            "cost" => command with { Product = command.Product with { ReferencePurchaseCost = 11m } },
            "price" => command with { Product = command.Product with { DefaultSalePrice = 21m } },
            "minimum" => command with { Product = command.Product with { MinimumStockLevel = 2m } },
            "warranty" => command with { Product = command.Product with { DefaultWarrantyMonths = 3 } },
            "attributes" => command with { Product = command.Product with { AttributesJson = "{}" } },
            "schema" => command with { Product = command.Product with { AttributesSchemaVersion = 2 } },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        Assert.NotEqual(ProductAggregatePayloadFingerprint.Compute(command), ProductAggregatePayloadFingerprint.Compute(changed));
    }

    [Fact]
    public void G01_FingerprintIsInvariantAcrossCultureAndUnorderedSelections()
    {
        var command = Command();
        command = command with
        {
            Units = [.. command.Units, new ProductUnitInput(Guid.NewGuid(), 2.5m, true, false, false, false, false)],
            LinkedSupplierIds = [Guid.NewGuid(), Guid.NewGuid()]
        };
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var first = ProductAggregatePayloadFingerprint.Compute(command);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal(first, ProductAggregatePayloadFingerprint.Compute(command with
            {
                Product = command.Product with { Name = "  Test   product  " },
                Units = command.Units.Reverse().ToArray(),
                LinkedSupplierIds = command.LinkedSupplierIds.Reverse().ToArray(),
                ClientOperationId = Guid.NewGuid()
            }));
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public async Task G01_ReplayRejectsChangedCostAndRecoversOriginalProduct()
    {
        var fakes = new Phase2TestDoubles();
        var command = Command();
        var handler = Handler(fakes);
        var first = await handler.HandleAsync(command, default);
        Assert.True(first.IsSuccess, first.Error?.Message);
        var replay = await handler.HandleAsync(command, default);
        Assert.True(replay.IsSuccess, replay.Error?.Message);
        Assert.Equal(first.Value!.ProductId, replay.Value!.ProductId);
        var mismatch = await handler.HandleAsync(command with { Product = command.Product with { ReferencePurchaseCost = 99m } }, default);
        Assert.Equal("idempotency.payload_mismatch", mismatch.Error?.Code);
        Assert.Single(fakes.Catalog.Products);
        Assert.Single(fakes.Catalog.ProductUnits);
    }

    [Fact]
    public async Task G01_OperationAndProductPairLocksAreInsideTheTransactionAndSorted()
    {
        var fakes = new Phase2TestDoubles();
        var transaction = new GuardedTransaction();
        var locks = new GuardedLocks(transaction);
        var command = Command();
        var product = new Product { Name = command.Product.Name, Sku = command.Product.Sku!, CategoryId = command.Product.CategoryId, BaseUnitId = command.Product.BaseUnitId, Version = 1 };
        fakes.Catalog.Products[product.Id] = product;
        var suppliers = new[] { new Supplier { IsActive = true }, new Supplier { IsActive = true } };
        foreach (var supplier in suppliers)
        {
            fakes.Parties.Suppliers[supplier.Id] = supplier;
        }

        var removed = new SupplierProduct { ProductId = product.Id, SupplierId = Guid.NewGuid(), NextItemSequence = 19, Version = 4, IsActive = true };
        fakes.Traceability.SupplierProducts[(removed.SupplierId, product.Id)] = removed;
        var reads = new LinksRead(new ProductManagementRowDto(product.Id, product.Sku!, product.Name,
            null, null, product.CategoryId, "Category", product.BaseUnitId, "Piece", product.TrackingMode, false, false,
            null, 20m, 0m, 0, null, 1, true, product.Version, [],
            [new SupplierProductLinkDto(removed.Id, removed.SupplierId, "Removed supplier", true, removed.Version)]));
        command = command with { ProductId = product.Id, ExpectedVersion = 1, LinkedSupplierIds = suppliers.Select(x => x.Id).OrderDescending().ToArray() };
        var result = await Handler(fakes, transaction, locks, reads: reads).HandleAsync(command, default);
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("operation", locks.Order[0].Type);
        Assert.Equal("product", locks.Order[1].Type);
        Assert.Equal(suppliers.Select(x => x.Id).Append(removed.SupplierId).Order().Select(x => $"{x:D}:{product.Id:D}"), locks.Order.Skip(2).Select(x => x.Key));
        Assert.False(removed.IsActive);
        Assert.Equal(19, removed.NextItemSequence);
        Assert.Equal(5, removed.Version);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task G01_HistoricalAggregateEditCannotBypassSkuOrTrackingGuards(bool changeSku)
    {
        var fakes = new Phase2TestDoubles();
        var command = Command();
        var product = new Product { Name = command.Product.Name, Sku = command.Product.Sku!, CategoryId = command.Product.CategoryId, BaseUnitId = command.Product.BaseUnitId, Version = 1 };
        fakes.Catalog.Products[product.Id] = product;
        command = command with
        {
            ProductId = product.Id, ExpectedVersion = 1,
            Product = changeSku ? command.Product with { Sku = "OTHER-SKU" } : command.Product with { TrackingMode = TrackingMode.IndividualPiece }
        };
        var result = await Handler(fakes, safety: new HistoricalSafety()).HandleAsync(command, default);
        Assert.Equal(changeSku ? "catalog.sku_immutable" : "catalog.tracking_policy_locked", result.Error?.Code);
        Assert.Equal(1, product.Version);
        Assert.Equal("TEST-SKU", product.Sku);
        Assert.Equal(TrackingMode.Quantity, product.TrackingMode);
    }

    [Theory]
    [InlineData("Abdullah", null, "AB")]
    [InlineData("عبداللہ", "ur", "UR")]
    [InlineData("A", "xy", "XY")]
    [InlineData("1-2", "az", "AZ")]
    public void G13_PrefixDerivationAndExplicitFallback(string name, string? explicitPrefix, string expected) =>
        Assert.Equal(expected, TraceabilityCodeRules.DeriveDealerPrefix(name, explicitPrefix));

    [Theory]
    [InlineData("عبداللہ", null, "parties.dealer_prefix_required")]
    [InlineData("A", "A", "parties.dealer_prefix_invalid")]
    [InlineData("1", "12", "parties.dealer_prefix_invalid")]
    [InlineData("1", "اردو", "parties.dealer_prefix_invalid")]
    public void G13_InsufficientAsciiAndInvalidFallbackReject(string name, string? prefix, string code) =>
        Assert.Equal(code, Assert.Throws<BusinessRuleException>(() => TraceabilityCodeRules.DeriveDealerPrefix(name, prefix)).Code);

    [Fact]
    public void G13_PrefixFieldReachesBothAdaptersControllerAndApplicationFingerprint()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "EdgeRetails.sln")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        string Source(string relative) => File.ReadAllText(Path.Combine(root.FullName, relative));
        Assert.Contains("Binding ExplicitDealerPrefix", Source("src/EdgeRetails.Desktop/Views/Dialogs/SupplierEditDialog.xaml"), StringComparison.Ordinal);
        Assert.Contains("ExplicitDealerPrefix.Trim()", Source("src/EdgeRetails.Desktop/ViewModels/SuppliersViewModel.cs"), StringComparison.Ordinal);
        Assert.Contains("Normalize(explicitDealerPrefix)", Source("src/EdgeRetails.Desktop/Services/BackendBusinessOperationsService.cs"), StringComparison.Ordinal);
        Assert.Contains("ExplicitDealerPrefix: Normalize(explicitDealerPrefix)", Source("src/EdgeRetails.Desktop/Services/RemoteBackendBusinessOperationsService.cs"), StringComparison.Ordinal);
        Assert.Contains("ExplicitDealerPrefix: request.ExplicitDealerPrefix", Source("src/EdgeRetails.Server/Controllers/SuppliersController.cs"), StringComparison.Ordinal);
        Assert.Contains("Normalize(command.ExplicitDealerPrefix)", Source("src/EdgeRetails.Application/Features/Parties/PartyHandlers.cs"), StringComparison.Ordinal);
        Assert.Null(typeof(EdgeRetails.Server.Controllers.SaveSupplierRequest).GetProperty("DealerCode"));
    }

    [Fact]
    public void G01_RemoteAggregateMutatesOnceAndReadbackUsesCommittedFallback()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "EdgeRetails.sln")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root.FullName, "src/EdgeRetails.Desktop/Services/RemoteProductManagementService.cs"));
        var aggregateMethods = source[source.IndexOf("public async Task<BackendProductManagementItem> CreateProductAsync", StringComparison.Ordinal)..source.IndexOf("public async Task<BackendProductManagementItem> SetProductActiveAsync", StringComparison.Ordinal)];
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(aggregateMethods, "PostAsync<SaveProductAggregateCommand, ProductMutationResult>").Count);
        Assert.DoesNotContain("ConfigureUnitsAsync(", aggregateMethods, StringComparison.Ordinal);
        Assert.DoesNotContain("SyncSupplierLinksAsync(", aggregateMethods, StringComparison.Ordinal);
        Assert.Contains("_pendingAggregateOperations.GetOrAdd", aggregateMethods, StringComparison.Ordinal);
        Assert.Contains("CommittedAggregateFallback", aggregateMethods, StringComparison.Ordinal);
        Assert.DoesNotContain("throw Error(\"catalog.product_readback_failed\"", aggregateMethods, StringComparison.Ordinal);
    }

    private static SaveProductAggregateCommand Command()
    {
        var baseUnit = Guid.NewGuid();
        return new(Guid.NewGuid(), null, null,
            new ProductCatalogInput("Test product", "TEST-SKU", CategoryId: Guid.NewGuid(), BaseUnitId: baseUnit, ReferencePurchaseCost: 10m, DefaultSalePrice: 20m),
            [new ProductUnitInput(baseUnit, 1m, true, true, true, true, true)], [], Guid.NewGuid());
    }

    private static SaveProductAggregateHandler Handler(Phase2TestDoubles fakes,
        ITransactionRunner? transaction = null, GuardedLocks? locks = null, IProductCatalogSafetyReadService? safety = null,
        IProductManagementReadService? reads = null) =>
        new(fakes.Catalog, fakes.Traceability, fakes.Parties, fakes.Authorization,
            transaction ?? fakes.Transactions, fakes.UnitOfWork, fakes.Clock, safety, reads,
            resourceLock: locks is null ? fakes.ResourceLock : locks,
            operationLock: locks is null ? fakes.OperationLock : locks, outcomeLedger: fakes.OutcomeLedger, audit: fakes.Audit);

    private sealed class HistoricalSafety : IProductCatalogSafetyReadService
    {
        public Task<bool> HasStockOrHistoryAsync(Guid productId, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class LinksRead(ProductManagementRowDto row) : IProductManagementReadService
    {
        public Task<ProductManagementRowDto?> GetProductAsync(Guid productId, CancellationToken cancellationToken) => Task.FromResult<ProductManagementRowDto?>(row);
        public Task<ProductManagementRowDto?> GetProductBySkuAsync(string sku, CancellationToken cancellationToken) => Task.FromResult<ProductManagementRowDto?>(row);
        public Task<IReadOnlyList<ProductManagementRowDto>> GetProductsAsync(bool includeInactive, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ProductManagementRowDto>>([row]);
        public Task<IReadOnlyList<ProductManagementRowDto>> GetProductsPageAsync(ProductManagementPageQuery query, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ProductManagementRowDto>>([row]);
        public Task<IReadOnlyList<CatalogCompanyDto>> GetCompaniesAsync(bool includeInactive, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CatalogCompanyDto>>([]);
        public Task<IReadOnlyList<CatalogCategoryDto>> GetCategoriesAsync(bool includeInactive, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CatalogCategoryDto>>([]);
        public Task<IReadOnlyList<CatalogUnitDto>> GetUnitsAsync(bool includeInactive, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CatalogUnitDto>>([]);
    }

    private sealed class GuardedTransaction : ITransactionRunner
    {
        public bool Active { get; private set; }
        public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
        {
            Active = true;
            try { return await operation(cancellationToken); }
            finally { Active = false; }
        }
    }

    private sealed class GuardedLocks(GuardedTransaction transaction) : IResourceLock, IOperationLock
    {
        public List<(string Type, string Key)> Order { get; } = [];
        public Task AcquireAsync(Guid operation, CancellationToken cancellationToken) => AcquireAsync("operation", operation.ToString("D"), cancellationToken);
        public Task AcquireAsync(string type, Guid id, CancellationToken cancellationToken) => AcquireAsync(type, id.ToString("D"), cancellationToken);
        public Task AcquireAsync(string type, string key, CancellationToken cancellationToken)
        {
            Assert.True(transaction.Active, "Advisory locks require the owning transaction.");
            Order.Add((type, key));
            return Task.CompletedTask;
        }
    }
}
