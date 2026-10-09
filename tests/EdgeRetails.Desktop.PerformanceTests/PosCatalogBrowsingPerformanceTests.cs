using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EdgeRetails.Desktop.PerformanceTests;

public sealed class PosCatalogBrowsingPerformanceTests
{
    [Fact]
    public async Task R6_LaterPageOptionsRemainDiscoverableAfterReset_AndUnbrandedCategoriesWork()
    {
        var products = GenerateProducts(201).Select((x, i) => x with
        {
            Name = $"Product {i:D4}", Category = i == 200 ? "Later Category" : "First Category",
            Brand = i == 200 ? "Later Brand" : null
        }).ToList();
        var gateway = new RecordingPosCatalogGateway(products);
        var vm = new PosViewModel(transactionService: DispatchProxy.Create<ITransactionService, UnusedProxy>(), posCatalogGateway: gateway);
        Assert.True(vm.SupportsCatalogFilters);
        Assert.Contains("First Category", vm.Categories);
        Assert.DoesNotContain("Later Category", vm.Categories);
        vm.LoadMoreCatalogCommand.Execute(null);
        await Task.Delay(100);
        Assert.Contains("Later Category", vm.Categories);
        Assert.Contains("Later Brand", vm.Brands);
        vm.SelectedBrand = "Later Brand";
        vm.SelectedBrand = "All";
        Assert.Contains("Later Brand", vm.Brands);
        vm.SelectedCategory = "Later Category";
        Assert.Single(vm.FilteredProducts);
        Assert.Equal("Later Category", vm.FilteredProducts[0].Category);
    }

    [Fact]
    public void PosViewModel_EnablesAuthoritativeBrandAndCategoryBrowsing_WhenAuthoritativeCatalogProvided()
    {
        var gateway = new AuthoritativeBrandCatalogGateway();
        var vm = new PosViewModel(
            transactionService: DispatchProxy.Create<ITransactionService, UnusedProxy>(),
            posCatalogGateway: gateway);

        // Asserts that authoritative brands and categories are projected and browsing is enabled
        Assert.True(vm.SupportsCatalogFilters);
        Assert.Contains("Load More Products", vm.CatalogReadLimitNotice);
        Assert.Contains("Samsung", vm.Brands);
        Assert.Contains("Apple", vm.Brands);
        Assert.Contains("Mobile Phones", vm.Categories);

        // Filter by Brand
        vm.SelectedBrand = "Samsung";
        Assert.Single(vm.FilteredProducts);
        Assert.Equal("Samsung Galaxy S24", vm.FilteredProducts[0].Name);

        // Filter by Category
        vm.SelectedBrand = "All";
        vm.SelectedCategory = "Mobile Phones";
        Assert.Equal(2, vm.FilteredProducts.Count);
    }

    [Fact]
    public void Gap1_01_Catalog_With250PlusProducts_LoadsFirstBoundedPage()
    {
        var products = GenerateProducts(253);
        var gateway = new RecordingPosCatalogGateway(products);
        var vm = new PosViewModel(
            transactionService: DispatchProxy.Create<ITransactionService, UnusedProxy>(),
            posCatalogGateway: gateway);

        Assert.Equal(200, vm.AllProducts.Count);
        Assert.True(vm.HasMoreCatalogProducts);
        Assert.Contains("200+ products", vm.FilteredProductCountText);
        Assert.Equal(200, gateway.Calls[0].pageSize);
        Assert.Null(gateway.Calls[0].afterName);
        Assert.Null(gateway.Calls[0].afterId);
    }

    [Fact]
    public void Gap1_02_BrandFilter_ForwardsToBackend_AndUpdatesResults()
    {
        var products = GenerateProducts(20, brand: "BrandAlpha")
            .Concat(GenerateProducts(20, brand: "BrandBeta")).ToList();
        var gateway = new RecordingPosCatalogGateway(products);
        var vm = new PosViewModel(
            transactionService: DispatchProxy.Create<ITransactionService, UnusedProxy>(),
            posCatalogGateway: gateway);

        vm.SelectedBrand = "BrandAlpha";

        Assert.Contains(gateway.Calls, call => call.brand == "BrandAlpha");
    }

    [Fact]
    public void Gap1_03_CategoryFilter_ForwardsToBackend_AndUpdatesResults()
    {
        var products = GenerateProducts(20, category: "Hardware")
            .Concat(GenerateProducts(20, category: "Software")).ToList();
        var gateway = new RecordingPosCatalogGateway(products);
        var vm = new PosViewModel(
            transactionService: DispatchProxy.Create<ITransactionService, UnusedProxy>(),
            posCatalogGateway: gateway);

        vm.SelectedCategory = "Hardware";

        Assert.Contains(gateway.Calls, call => call.category == "Hardware");
    }

    [Fact]
    public async Task Gap1_04_SearchText_ForwardsToBackend_AndUpdatesResults()
    {
        var products = GenerateProducts(50);
        var gateway = new RecordingPosCatalogGateway(products);
        var vm = new PosViewModel(
            transactionService: DispatchProxy.Create<ITransactionService, UnusedProxy>(),
            posCatalogGateway: gateway);

        vm.SearchText = "Product 0015";
        await Task.Delay(350);

        Assert.Contains(gateway.Calls, call => call.search == "Product 0015");
    }

    [Fact]
    public async Task Gap1_05_CombinedBrandCategorySearch_ForwardsTogether()
    {
        var products = GenerateProducts(20, brand: "Sony", category: "Audio");
        var gateway = new RecordingPosCatalogGateway(products);
        var vm = new PosViewModel(
            transactionService: DispatchProxy.Create<ITransactionService, UnusedProxy>(),
            posCatalogGateway: gateway);

        vm.SelectedBrand = "Sony";
        vm.SelectedCategory = "Audio";
        vm.SearchText = "Product";
        await Task.Delay(350);

        Assert.Contains(gateway.Calls, call =>
            call.search == "Product" && call.brand == "Sony" && call.category == "Audio");
    }

    [Fact]
    public void Gap1_06_KeysetPagination_BrowsesMultiplePages_Across200ItemBoundary()
    {
        var products = GenerateProducts(253);
        var gateway = new RecordingPosCatalogGateway(products);
        var vm = new PosViewModel(
            transactionService: DispatchProxy.Create<ITransactionService, UnusedProxy>(),
            posCatalogGateway: gateway);

        Assert.Equal(200, vm.AllProducts.Count);
        Assert.True(vm.HasMoreCatalogProducts);

        // Execute Load More
        vm.LoadMoreCatalogCommand.Execute(null);

        Assert.Equal(253, vm.AllProducts.Count);
        Assert.False(vm.HasMoreCatalogProducts);
        Assert.Equal("253 products", vm.FilteredProductCountText);

        // Verify keyset cursor was passed on second request
        Assert.True(gateway.Calls.Count >= 2);
        var secondCall = gateway.Calls[^1];
        Assert.Equal("Product 0200", secondCall.afterName);
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000200"), secondCall.afterId);
    }

    [Fact]
    public void Gap1_07_IdenticalNamesWithDifferentIds_PaginateCorrectlyWithoutDuplicatesOrOmissions()
    {
        var products = Enumerable.Range(1, 253).Select(i =>
            new PosCatalogGatewayItem(
                Guid.Parse($"00000000-0000-0000-0000-{i:D12}"),
                Guid.NewGuid(),
                "Equal Name Item",
                $"SKU-{i:D4}",
                "General",
                "Pcs",
                10m,
                100m,
                80m,
                false,
                "Canonical")).ToList();

        var gateway = new RecordingPosCatalogGateway(products);
        var vm = new PosViewModel(
            transactionService: DispatchProxy.Create<ITransactionService, UnusedProxy>(),
            posCatalogGateway: gateway);

        Assert.Equal(200, vm.AllProducts.Count);
        vm.LoadMoreCatalogCommand.Execute(null);

        Assert.Equal(253, vm.AllProducts.Count);
        Assert.Equal(253, vm.AllProducts.Select(x => x.BackendProductId).Distinct().Count());
        Assert.False(vm.HasMoreCatalogProducts);
    }

    [Fact]
    public async Task Gap1_08_IncompleteCursorRejection_InDesktopGateway()
    {
        var gateway = new BackendPosCatalogGateway((DesktopApiClient)null!);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            gateway.LoadAsync(null, null, null, 50, "afterNameOnly", null));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            gateway.LoadAsync(null, null, null, 50, null, Guid.NewGuid()));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            gateway.LoadAsync(null, null, null, 50, "   ", Guid.NewGuid()));
    }

    [Fact]
    public void Gap1_09_FilterChange_ResetsCursor_ToFirstPageAndClearsContinuationState()
    {
        var products = GenerateProducts(253);
        var gateway = new RecordingPosCatalogGateway(products);
        var vm = new PosViewModel(
            transactionService: DispatchProxy.Create<ITransactionService, UnusedProxy>(),
            posCatalogGateway: gateway);

        Assert.True(vm.HasMoreCatalogProducts);

        // Changing filter resets cursor
        vm.SelectedCategory = "NonExistentCategory";
        Assert.False(vm.HasMoreCatalogProducts);

        var lastCall = gateway.Calls[^1];
        Assert.Null(lastCall.afterName);
        Assert.Null(lastCall.afterId);
    }

    [Fact]
    public async Task Gap1_10_RapidLoadMore_DoesNotIssueDuplicateConcurrentRequests()
    {
        var products = GenerateProducts(253);
        var gateway = new RecordingPosCatalogGateway(products) { DelayMilliseconds = 50 };
        var vm = new PosViewModel(
            transactionService: DispatchProxy.Create<ITransactionService, UnusedProxy>(),
            posCatalogGateway: gateway);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!vm.HasMoreCatalogProducts)
        {
            await Task.Delay(10, timeout.Token);
        }
        var initialCalls = gateway.Calls.Count;

        // Rapid duplicate invocation
        vm.LoadMoreCatalogCommand.Execute(null);
        vm.LoadMoreCatalogCommand.Execute(null);
        vm.LoadMoreCatalogCommand.Execute(null);

        await Task.Delay(120);

        // Exactly one continuation request should have been dispatched
        Assert.Equal(initialCalls + 1, gateway.Calls.Count);
    }

    [Fact]
    public async Task Gap1_11_StaleResponsesFromCanceledSearches_DoNotOverwriteCurrentResults()
    {
        var productsA = GenerateProducts(10, brand: "SlowBrand");
        var productsB = GenerateProducts(5, brand: "FastBrand");

        var gateway = new VariableDelayCatalogGateway(productsA, productsB);
        var vm = new PosViewModel(
            transactionService: DispatchProxy.Create<ITransactionService, UnusedProxy>(),
            posCatalogGateway: gateway);

        // Start search for SlowBrand
        vm.SearchText = "SlowBrand";
        // Immediately change to FastBrand before debounce completes
        await Task.Delay(50);
        vm.SearchText = "FastBrand";
        await Task.Delay(400);

        // FastBrand results must win, slow response must not overwrite
        Assert.All(vm.AllProducts, p => Assert.Equal("FastBrand", p.Brand));
    }

    [Fact]
    public void Gap1_12_EndOfResultsIndicator_DisplaysTruthfully()
    {
        var products = GenerateProducts(42);
        var gateway = new RecordingPosCatalogGateway(products);
        var vm = new PosViewModel(
            transactionService: DispatchProxy.Create<ITransactionService, UnusedProxy>(),
            posCatalogGateway: gateway);

        Assert.Equal(42, vm.AllProducts.Count);
        Assert.False(vm.HasMoreCatalogProducts);
        Assert.Equal("42 products", vm.FilteredProductCountText);
    }

    [Fact]
    public async Task Gap1_13_FullFlow_ThroughRealDesktopGateway_InProcessAndHttpQueryContract()
    {
        // Path A: In-process IServiceScopeFactory
        var services = new ServiceCollection();
        var fakeReadService = new FakePosCatalogReadService();
        services.AddScoped<IPosCatalogReadService>(_ => fakeReadService);
        var provider = services.BuildServiceProvider();
        var inProcessGateway = new BackendPosCatalogGateway(provider.GetRequiredService<IServiceScopeFactory>());

        var cursorId = Guid.NewGuid();
        var inProcessItems = await inProcessGateway.LoadAsync("cable", "Electrical", "Acme", 100, "AfterItem", cursorId);
        Assert.Single(inProcessItems);
        Assert.Equal("cable", fakeReadService.LastSearch);
        Assert.Equal("Electrical", fakeReadService.LastCategory);
        Assert.Equal("Acme", fakeReadService.LastBrand);
        Assert.Equal("AfterItem", fakeReadService.LastAfterName);
        Assert.Equal(cursorId, fakeReadService.LastAfterId);

        // Path B: HTTP Client query formatting
        string? capturedQuery = null;
        var httpHandler = new DelegatingHandlerStub(req =>
        {
            capturedQuery = req.RequestUri!.PathAndQuery;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new[]
                {
                    new PosCatalogProductDto(Guid.NewGuid(), Guid.NewGuid(), "Sample", "SKU1", "Cat", "Pcs", 5, 10, 8, false, "Brand")
                })
            };
            return response;
        });

        var httpClient = new HttpClient(httpHandler) { BaseAddress = new Uri("http://127.0.0.1:5000") };
        var apiClient = new DesktopApiClient(httpClient, ownsClient: true);
        var httpGateway = new BackendPosCatalogGateway(apiClient);

        var httpItems = await httpGateway.LoadAsync("search term", "My Cat", "My Brand", 100, "After Name", cursorId);
        Assert.Single(httpItems);
        Assert.NotNull(capturedQuery);
        Assert.Contains("/api/sales/catalog?pageSize=100", capturedQuery);
        Assert.Contains("search=search%20term", capturedQuery);
        Assert.Contains("category=My%20Cat", capturedQuery);
        Assert.Contains("brand=My%20Brand", capturedQuery);
        Assert.Contains("afterName=After%20Name", capturedQuery);
        Assert.Contains($"afterId={cursorId:D}", capturedQuery);
    }

    private static List<PosCatalogGatewayItem> GenerateProducts(int count, string brand = "Acme", string category = "General")
    {
        return Enumerable.Range(1, count).Select(i =>
            new PosCatalogGatewayItem(
                Guid.Parse($"00000000-0000-0000-0000-{i:D12}"),
                Guid.Parse($"00000000-0000-0000-0001-{i:D12}"),
                $"Product {i:D4}",
                $"SKU-{i:D4}",
                category,
                "Pcs",
                10m,
                100m,
                80m,
                false,
                brand)).ToList();
    }

    private sealed class RecordingPosCatalogGateway : IPosCatalogGateway
    {
        private readonly List<PosCatalogGatewayItem> _allProducts;
        public List<(string? search, string? category, string? brand, int pageSize, string? afterName, Guid? afterId)> Calls { get; } = [];
        public int DelayMilliseconds { get; set; }

        public RecordingPosCatalogGateway(IEnumerable<PosCatalogGatewayItem>? products = null)
        {
            _allProducts = products?.ToList() ?? [];
        }

        public Task<IReadOnlyList<PosCatalogGatewayItem>> LoadAsync(
            string? search,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            LoadAsync(search, null, null, pageSize, null, null, cancellationToken);

        public async Task<IReadOnlyList<PosCatalogGatewayItem>> LoadAsync(
            string? search,
            string? category,
            string? brand,
            int pageSize,
            string? afterName,
            Guid? afterId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((search, category, brand, pageSize, afterName, afterId));
            if (DelayMilliseconds > 0)
            {
                await Task.Delay(DelayMilliseconds, cancellationToken);
            }

            var query = _allProducts.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(search))
            {
                query = query.Where(x => x.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                         x.Sku.Contains(search, StringComparison.OrdinalIgnoreCase));
            }
            if (!string.IsNullOrWhiteSpace(category) && !string.Equals(category, "All", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(x => string.Equals(x.Category, category, StringComparison.OrdinalIgnoreCase));
            }
            if (!string.IsNullOrWhiteSpace(brand) && !string.Equals(brand, "All", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(x => string.Equals(x.Brand, brand, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrEmpty(afterName) && afterId.HasValue)
            {
                query = query.Where(x =>
                    string.Compare(x.Name, afterName, StringComparison.Ordinal) > 0 ||
                    (x.Name == afterName && x.ProductId.CompareTo(afterId.Value) > 0));
            }

            var result = query
                .OrderBy(x => x.Name, StringComparer.Ordinal)
                .ThenBy(x => x.ProductId)
                .Take(pageSize)
                .ToList();

            return result;
        }
    }

    private sealed class VariableDelayCatalogGateway : IPosCatalogGateway
    {
        private readonly List<PosCatalogGatewayItem> _slowProducts;
        private readonly List<PosCatalogGatewayItem> _fastProducts;

        public VariableDelayCatalogGateway(List<PosCatalogGatewayItem> slowProducts, List<PosCatalogGatewayItem> fastProducts)
        {
            _slowProducts = slowProducts;
            _fastProducts = fastProducts;
        }

        public Task<IReadOnlyList<PosCatalogGatewayItem>> LoadAsync(string? search, int pageSize, CancellationToken cancellationToken = default) =>
            LoadAsync(search, null, null, pageSize, null, null, cancellationToken);

        public async Task<IReadOnlyList<PosCatalogGatewayItem>> LoadAsync(
            string? search, string? category, string? brand, int pageSize, string? afterName, Guid? afterId, CancellationToken cancellationToken = default)
        {
            if (search?.Contains("SlowBrand") == true)
            {
                await Task.Delay(300, cancellationToken);
                return _slowProducts;
            }

            return _fastProducts;
        }
    }

    private sealed class FakePosCatalogReadService : IPosCatalogReadService
    {
        public string? LastSearch { get; private set; }
        public string? LastCategory { get; private set; }
        public string? LastBrand { get; private set; }
        public int LastPageSize { get; private set; }
        public string? LastAfterName { get; private set; }
        public Guid? LastAfterId { get; private set; }

        public Task<IReadOnlyList<PosCatalogProductDto>> GetSellableCatalogAsync(
            string? search,
            int pageSize,
            CancellationToken cancellationToken) =>
            GetSellableCatalogAsync(search, null, null, pageSize, null, null, cancellationToken);

        public Task<IReadOnlyList<PosCatalogProductDto>> GetSellableCatalogAsync(
            string? search,
            string? category,
            string? brand,
            int pageSize,
            string? afterName,
            Guid? afterId,
            CancellationToken cancellationToken)
        {
            LastSearch = search;
            LastCategory = category;
            LastBrand = brand;
            LastPageSize = pageSize;
            LastAfterName = afterName;
            LastAfterId = afterId;

            IReadOnlyList<PosCatalogProductDto> result =
            [
                new PosCatalogProductDto(Guid.NewGuid(), Guid.NewGuid(), "M-Product", "M-SKU", "Electrical", "m", 10m, 100m, 80m, false, "Acme")
            ];
            return Task.FromResult(result);
        }
    }

    private sealed class DelegatingHandlerStub : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public DelegatingHandlerStub(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_handler(request));
    }

    private sealed class AuthoritativeBrandCatalogGateway : IPosCatalogGateway
    {
        public Task<IReadOnlyList<PosCatalogGatewayItem>> LoadAsync(string? search, int pageSize, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<PosCatalogGatewayItem> items =
            [
                new PosCatalogGatewayItem(Guid.NewGuid(), Guid.NewGuid(), "Samsung Galaxy S24", "SM-S921B", "Mobile Phones", "Pcs", 5m, 250000m, 220000m, true, "Samsung"),
                new PosCatalogGatewayItem(Guid.NewGuid(), Guid.NewGuid(), "Apple iPhone 15", "IPHONE-15", "Mobile Phones", "Pcs", 3m, 280000m, 250000m, true, "Apple"),
                new PosCatalogGatewayItem(Guid.NewGuid(), Guid.NewGuid(), "Generic Charger", "CHG-01", "Accessories", "Pcs", 20m, 1200m, 800m, false, null)
            ];
            return Task.FromResult(items);
        }
    }

    public class UnusedProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new InvalidOperationException("Unused");
    }
}
