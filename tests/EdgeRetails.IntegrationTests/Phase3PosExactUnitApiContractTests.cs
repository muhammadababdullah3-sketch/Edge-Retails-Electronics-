using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Domain.Inventory;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace EdgeRetails.IntegrationTests;

public sealed class Phase3PosExactUnitApiContractTests
{
    [Fact]
    public async Task PosCashier_CanReadExactUnitsWithoutInventoryManagementPermission()
    {
        using var baseFactory = new Phase2ServerWebApplicationFactory();
        baseFactory.Reset();
        baseFactory.Identity.SetPermissions(baseFactory.ValidUserId,
            [PermissionKeys.SalesPosUse]);
        var reads = new StubWorkflowReads();
        using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPhase4WorkflowReadService>();
                services.AddSingleton<IPhase4WorkflowReadService>(reads);
            }));
        using var client = factory.CreateClient();
        var sessionId = baseFactory.SeedValidSession();
        var productId = Guid.NewGuid();

        using var anonymous = CreateRequest(baseFactory,
            $"/api/sales/exact-units?productId={productId}");
        using var anonymousResponse = await client.SendAsync(anonymous);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using var inventory = CreateRequest(baseFactory,
            $"/api/inventory/exact-units?productId={productId}", sessionId);
        using var inventoryResponse = await client.SendAsync(inventory);
        Assert.Equal(HttpStatusCode.Forbidden, inventoryResponse.StatusCode);

        using var pos = CreateRequest(baseFactory,
            $"/api/sales/exact-units?productId={productId}&status=InStock&pageSize=500", sessionId);
        using var posResponse = await client.SendAsync(pos);
        Assert.Equal(HttpStatusCode.OK, posResponse.StatusCode);
        var json = await posResponse.Content.ReadAsStringAsync();
        var units = JsonSerializer.Deserialize<PosExactUnitDto[]>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        });
        Assert.Equal(reads.UnitId, Assert.Single(units!).InventoryUnitId);
        Assert.DoesNotContain("acquisitionCost", json);
        Assert.DoesNotContain("supplierName", json);
        Assert.Equal(productId, reads.LastProductId);
        Assert.Equal(InventoryUnitStatus.InStock, reads.LastStatus);
        Assert.Equal(200, reads.LastPageSize);
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

    private sealed class StubWorkflowReads : IPhase4WorkflowReadService
    {
        public Guid UnitId { get; } = Guid.NewGuid();
        public Guid LastProductId { get; private set; }
        public InventoryUnitStatus? LastStatus { get; private set; }
        public int LastPageSize { get; private set; }
        public Guid? LastBeforeUnitId { get; private set; }

        public Task<IReadOnlyList<ExactInventoryUnitDto>> GetExactUnitsAsync(
            Guid productId,
            InventoryUnitStatus? status,
            Guid? sourcePurchaseItemId,
            CancellationToken cancellationToken) =>
            GetExactUnitsAsync(productId, status, sourcePurchaseItemId, 50, cancellationToken);

        public Task<IReadOnlyList<ExactInventoryUnitDto>> GetExactUnitsAsync(
            Guid productId,
            InventoryUnitStatus? status,
            Guid? sourcePurchaseItemId,
            int pageSize,
            CancellationToken cancellationToken)
            => GetExactUnitsAsync(productId, status, sourcePurchaseItemId, pageSize, null, cancellationToken);

        public Task<IReadOnlyList<ExactInventoryUnitDto>> GetExactUnitsAsync(
            Guid productId,
            InventoryUnitStatus? status,
            Guid? sourcePurchaseItemId,
            int pageSize,
            Guid? beforeUnitId,
            CancellationToken cancellationToken)
        {
            LastProductId = productId;
            LastStatus = status;
            LastPageSize = pageSize;
            LastBeforeUnitId = beforeUnitId;
            IReadOnlyList<ExactInventoryUnitDto> units =
            [
                new ExactInventoryUnitDto(
                    UnitId, productId, "Serialized item", "SER-01", "TRACK-01",
                    "SERIAL-01", null, null, InventoryUnitStatus.InStock, 90m,
                    null, null, null, DateTimeOffset.UtcNow, 1)
            ];
            return Task.FromResult(units);
        }

        public Task<IReadOnlyList<ScannerProductMatchDto>> ResolveScannerAsync(
            string input, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ScannerProductMatchDto>>([]);

        public Task<IReadOnlyList<PosDraftSummaryDto>> GetOpenDraftsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PosDraftSummaryDto>>([]);

        public Task<PosDraftDetailDto?> GetDraftAsync(
            Guid draftId, CancellationToken cancellationToken) =>
            Task.FromResult<PosDraftDetailDto?>(null);

        public Task<StocktakeSnapshotDto?> GetOpenStocktakeAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<StocktakeSnapshotDto?>(null);
    }
}
