using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Reflection;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Server.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EdgeRetails.IntegrationTests;

public sealed class Phase3StocktakeApiContractTests
{
    [Fact]
    public void CatalogReferenceAndSupplierMutations_AreExposedThroughServerRoutes()
    {
        AssertPostRoute(nameof(CatalogController.SetCompanyActive), "companies/{id:guid}/active");
        AssertPostRoute(nameof(CatalogController.SetCategoryActive), "categories/{id:guid}/active");
        AssertPostRoute(nameof(CatalogController.SetUnitActive), "units/{id:guid}/active");
        AssertPostRoute(nameof(CatalogController.SetSupplierProductActive), "products/{id:guid}/suppliers/{supplierId:guid}/active");
    }

    [Fact]
    public void CatalogUnitSave_IsExposedThroughServerRoute()
    {
        AssertPostRoute(nameof(CatalogController.SaveUnit), "units");
    }

    [Fact]
    public void StocktakeMutationLifecycle_IsExposedThroughServerRoutes()
    {
        AssertInventoryPostRoute(nameof(InventoryController.StartStocktake), "stocktake/{id:guid}/start");
        AssertInventoryPostRoute(nameof(InventoryController.RecordStocktakeCount), "stocktake/{id:guid}/counts");
        AssertInventoryPostRoute(nameof(InventoryController.RecordSerializedStocktake), "stocktake/{id:guid}/serialized-counts");
        AssertInventoryPostRoute(nameof(InventoryController.ReviewStocktake), "stocktake/{id:guid}/review");
        AssertInventoryPostRoute(nameof(InventoryController.PostStocktake), "stocktake/{id:guid}/post");
        AssertInventoryPostRoute(nameof(InventoryController.CancelStocktake), "stocktake/{id:guid}/cancel");
    }

    private static void AssertPostRoute(string methodName, string expectedTemplate)
    {
        var method = typeof(CatalogController).GetMethod(methodName);
        Assert.NotNull(method);
        var route = Assert.Single(method!.GetCustomAttributes<HttpPostAttribute>());
        Assert.Equal(expectedTemplate, route.Template);
    }

    private static void AssertInventoryPostRoute(string methodName, string expectedTemplate)
    {
        var method = typeof(InventoryController).GetMethod(methodName);
        Assert.NotNull(method);
        var route = Assert.Single(method!.GetCustomAttributes<HttpPostAttribute>());
        Assert.Equal(expectedTemplate, route.Template);
    }

    [Fact]
    public async Task OpenStocktake_IsAvailableThroughAuthenticatedServerContract()
    {
        using var baseFactory = new Phase2ServerWebApplicationFactory();
        baseFactory.Reset();
        var reads = new StubPhase4WorkflowReads();
        using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPhase4WorkflowReadService>();
                services.AddSingleton<IPhase4WorkflowReadService>(reads);
            }));
        using var client = factory.CreateClient();
        var sessionId = baseFactory.SeedValidSession();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/inventory/stocktake/open");
        request.Headers.Add("X-Terminal-Id", baseFactory.ActiveTerminalId.ToString("D"));
        request.Headers.Add("X-Terminal-Secret", baseFactory.ActiveTerminalSecret);
        request.Headers.Add("X-Session-Id", sessionId.ToString("D"));
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<OpenStocktakeResponse>();
        Assert.NotNull(body);
        Assert.Null(body.Stocktake);
        Assert.True(reads.OpenSnapshotWasRead);
    }

    [Fact]
    public async Task StocktakeMutations_RejectEmptyOperationIdentity()
    {
        using var baseFactory = new Phase2ServerWebApplicationFactory();
        baseFactory.Reset();
        using var client = baseFactory.CreateClient();
        var sessionId = baseFactory.SeedValidSession();
        var stocktakeId = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();
        var requests = new (string Path, object Payload)[]
        {
            ("/api/inventory/stocktake", new CreateStocktakeCommand(StocktakeScope.FullShop, null, Guid.Empty, null, Guid.Empty)),
            ($"/api/inventory/stocktake/{stocktakeId:D}/start", new StocktakeActionApiRequest(Guid.Empty)),
            ($"/api/inventory/stocktake/{stocktakeId:D}/counts", new RecordStocktakeCountApiRequest(productId, 1m, null, Guid.Empty)),
            ($"/api/inventory/stocktake/{stocktakeId:D}/serialized-counts", new RecordSerializedStocktakeApiRequest(productId, [], [], null, Guid.Empty)),
            ($"/api/inventory/stocktake/{stocktakeId:D}/review", new StocktakeActionApiRequest(Guid.Empty)),
            ($"/api/inventory/stocktake/{stocktakeId:D}/cancel", new StocktakeActionApiRequest(Guid.Empty))
        };

        foreach (var (path, payload) in requests)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent.Create(payload)
            };
            request.Headers.Add("X-Terminal-Id", baseFactory.ActiveTerminalId.ToString("D"));
            request.Headers.Add("X-Terminal-Secret", baseFactory.ActiveTerminalSecret);
            request.Headers.Add("X-Session-Id", sessionId.ToString("D"));
            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("inventory.stocktake_operation_id_required", body.GetProperty("code").GetString());
        }
    }

    private sealed class StubPhase4WorkflowReads : IPhase4WorkflowReadService
    {
        public bool OpenSnapshotWasRead { get; private set; }

        public Task<IReadOnlyList<ExactInventoryUnitDto>> GetExactUnitsAsync(
            Guid productId,
            InventoryUnitStatus? status,
            Guid? sourcePurchaseItemId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExactInventoryUnitDto>>([]);

        public Task<IReadOnlyList<ScannerProductMatchDto>> ResolveScannerAsync(
            string input,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ScannerProductMatchDto>>([]);

        public Task<IReadOnlyList<PosDraftSummaryDto>> GetOpenDraftsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PosDraftSummaryDto>>([]);

        public Task<PosDraftDetailDto?> GetDraftAsync(
            Guid draftId,
            CancellationToken cancellationToken) =>
            Task.FromResult<PosDraftDetailDto?>(null);

        public Task<StocktakeSnapshotDto?> GetOpenStocktakeAsync(
            CancellationToken cancellationToken)
        {
            OpenSnapshotWasRead = true;
            return Task.FromResult<StocktakeSnapshotDto?>(null);
        }
    }

    private sealed record OpenStocktakeResponse(StocktakeSnapshotDto? Stocktake);
}
