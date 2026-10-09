using System.Net;
using System.Net.Http.Json;
using EdgeRetails.Application.Features.Sales;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace EdgeRetails.IntegrationTests;

public sealed class Phase3PosCatalogApiContractTests
{
    [Fact]
    public async Task CatalogSearch_ForwardsFiltersAndCompleteCompositeCursor()
    {
        using var baseFactory = new Phase2ServerWebApplicationFactory();
        baseFactory.Reset();
        var catalog = new CapturingPosCatalogReadService();
        using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPosCatalogReadService>();
            services.AddSingleton<IPosCatalogReadService>(catalog);
        }));
        using var client = factory.CreateClient();
        var cursorId = Guid.NewGuid();
        using var request = CreateRequest(baseFactory,
            $"/api/sales/catalog?search=wire&category=Electrical&brand=Canonical&pageSize=100&afterName=Equal%20name&afterId={cursorId:D}",
            baseFactory.SeedValidSession());
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Electrical", catalog.LastCategory);
        Assert.Equal("Canonical", catalog.LastBrand);
        Assert.Equal("Equal name", catalog.LastAfterName);
        Assert.Equal(cursorId, catalog.LastAfterId);
        Assert.Equal(100, catalog.LastPageSize);
    }

    [Theory]
    [InlineData("afterName=Equal%20name")]
    [InlineData("afterId=00000000-0000-0000-0000-000000000001")]
    [InlineData("afterName=%20%20&afterId=00000000-0000-0000-0000-000000000001")]
    public async Task CatalogSearch_IncompleteCursorMustNotSilentlyRestartFirstPage(string cursor)
    {
        using var baseFactory = new Phase2ServerWebApplicationFactory();
        baseFactory.Reset();
        var catalog = new CapturingPosCatalogReadService();
        using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPosCatalogReadService>();
            services.AddSingleton<IPosCatalogReadService>(catalog);
        }));
        using var client = factory.CreateClient();
        using var request = CreateRequest(baseFactory, "/api/sales/catalog?" + cursor, baseFactory.SeedValidSession());
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CatalogSearch_UsesAuthenticatedBoundedBackendRead()
    {
        using var baseFactory = new Phase2ServerWebApplicationFactory();
        baseFactory.Reset();
        var catalog = new CapturingPosCatalogReadService();
        using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPosCatalogReadService>();
                services.AddSingleton<IPosCatalogReadService>(catalog);
            }));
        using var client = factory.CreateClient();
        var sessionId = baseFactory.SeedValidSession();

        using var anonymous = CreateRequest(baseFactory, "/api/sales/catalog?search=wire&pageSize=500");
        using var anonymousResponse = await client.SendAsync(anonymous);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using var request = CreateRequest(
            baseFactory,
            "/api/sales/catalog?search=wire&pageSize=500",
            sessionId);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var products = await response.Content.ReadFromJsonAsync<PosCatalogProductDto[]>();
        var product = Assert.Single(products!);
        Assert.Equal(catalog.ProductId, product.ProductId);
        Assert.Equal(125m, product.UnitPrice);
        Assert.Equal("wire", catalog.LastSearch);
        Assert.Equal(200, catalog.LastPageSize);
    }

    private static HttpRequestMessage CreateRequest(
        Phase2ServerWebApplicationFactory factory,
        string path,
        Guid? sessionId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Terminal-Id", factory.ActiveTerminalId.ToString());
        request.Headers.Add("X-Terminal-Secret", factory.ActiveTerminalSecret);
        if (sessionId.HasValue)
        {
            request.Headers.Add("X-Session-Id", sessionId.Value.ToString());
        }

        return request;
    }

    private sealed class CapturingPosCatalogReadService : IPosCatalogReadService
    {
        public Guid ProductId { get; } = Guid.NewGuid();
        public string? LastSearch { get; private set; }
        public int LastPageSize { get; private set; }
        public string? LastCategory { get; private set; }
        public string? LastBrand { get; private set; }
        public string? LastAfterName { get; private set; }
        public Guid? LastAfterId { get; private set; }

        public Task<IReadOnlyList<PosCatalogProductDto>> GetSellableCatalogAsync(string? search, string? category,
            string? brand, int pageSize, string? afterName, Guid? afterId, CancellationToken cancellationToken)
        {
            LastCategory = category;
            LastBrand = brand;
            LastAfterName = afterName;
            LastAfterId = afterId;
            return GetSellableCatalogAsync(search, pageSize, cancellationToken);
        }

        public Task<IReadOnlyList<PosCatalogProductDto>> GetSellableCatalogAsync(
            string? search,
            int pageSize,
            CancellationToken cancellationToken)
        {
            LastSearch = search;
            LastPageSize = pageSize;
            IReadOnlyList<PosCatalogProductDto> products =
            [
                new PosCatalogProductDto(
                    ProductId,
                    Guid.NewGuid(),
                    "Copper wire",
                    "WIRE-01",
                    "Electrical",
                    "m",
                    18m,
                    125m,
                    90m,
                    false)
            ];
            return Task.FromResult(products);
        }
    }
}
