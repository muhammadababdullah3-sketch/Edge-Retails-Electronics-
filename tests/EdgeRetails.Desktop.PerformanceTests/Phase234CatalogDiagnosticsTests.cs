using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Desktop.ViewModels;
using EdgeRetails.Domain.Finance;
using Xunit;

namespace EdgeRetails.Desktop.PerformanceTests;

public sealed class Phase234CatalogDiagnosticsTests
{
    [Fact]
    public void ProductionCatalogDoesNotClaimCompleteBrowsingOrInventBrands()
    {
        var gateway = new CatalogGateway();
        var vm = new PosViewModel(transactionService: DispatchProxy.Create<ITransactionService, UnusedProxy>(), posCatalogGateway: gateway);
        Assert.Equal(200, gateway.RequestedSize);
        Assert.Equal(200, vm.AllProducts.Count);
        Assert.False(vm.SupportsCatalogFilters);
        Assert.Contains("limited to 200", vm.CatalogReadLimitNotice);
        Assert.All(vm.AllProducts, row => Assert.Equal("—", row.Brand));
        vm.SelectedCategory = "Unreturned category";
        vm.SelectedBrand = "Fabricated brand";
        Assert.Equal(200, vm.FilteredProducts.Count);
        Assert.Contains("limit 200", vm.FilteredProductCountText);
    }

    [Fact]
    public async Task SupplierHistoryOver500PreservesEveryRowInThreeBoundedRequests()
    {
        var rows = Payments(501);
        var requests = 0;
        using var api = Api(_ => Json(Workspace(rows.Skip(requests++ * 200).Take(200).ToArray())));
        var result = await new RemoteBackendOperationsService(api).GetSupplierWorkspaceAsync(Guid.NewGuid());
        Assert.Equal(501, result.Payments.Count);
        Assert.Equal(3, requests);
        Assert.Equal(501, result.Payments.Select(row => row.PaymentId).Distinct().Count());
    }

    [Fact]
    public async Task SupplierIgnoredCursorFailsAfterSecondRequestInsteadOfLooping()
    {
        var requests = 0;
        var rows = Payments(200);
        using var api = Api(_ => { requests++; return Json(Workspace(rows)); });
        var error = await Assert.ThrowsAsync<DesktopApiException>(() => new RemoteBackendOperationsService(api).GetSupplierWorkspaceAsync(Guid.NewGuid()));
        Assert.Equal("gateway.cursor_invalid", error.Code);
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task SupplierForwardCursorWithNewIdentityFailsClosed()
    {
        var rows = Payments(200);
        var calls = 0;
        using var api = Api(_ => Json(Workspace(calls++ == 0 ? rows : Payments(1))));
        await Assert.ThrowsAsync<DesktopApiException>(() => new RemoteBackendOperationsService(api).GetSupplierWorkspaceAsync(Guid.NewGuid()));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task DashboardVerifiedArtifactsDoNotInventBackupFreshness()
    {
        using var api = Api(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/reports/snapshot" => new HttpResponseMessage(HttpStatusCode.Forbidden),
            "/api/system/ready" => Json(new { status = "Ready" }),
            "/api/backups/diagnostics" => Json(new BackupHistoryDiagnosticsResponse(0, [], 0, [])),
            _ => throw new InvalidOperationException()
        });
        var result = await new RemoteBackendDashboardService(api, DispatchProxy.Create<IBackendThakaService, ReadUnavailableProxy>()).LoadAsync();
        Assert.Null(result.IsBackupUpToDate);
        Assert.Contains("No verified backups", result.BackupStatusText);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SettingsUseOnlyExplicitMaintenanceAndAuthorizedBackupEvidence(bool supported)
    {
        using var api = Api(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/settings" => Json(new { shopName = "Shop" }),
            "/api/auth/accounts" => Json(Array.Empty<object>()),
            "/api/system/ready" => Json(new { status = "Ready", maintenanceState = supported ? "Normal" : null }),
            "/api/backups/diagnostics" when supported => Json(new BackupHistoryDiagnosticsResponse(0, [], 2, [])),
            "/api/backups/diagnostics" => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = JsonContent.Create(new { code = "permission.denied", message = "Denied" }) },
            _ => throw new InvalidOperationException()
        });
        var result = await new RemoteBackendSettingsService(api, () => Guid.NewGuid()).LoadAsync();
        Assert.StartsWith(supported ? "Normal" : "Unavailable", result.MaintenanceStatus);
        Assert.Contains(supported ? "2 invalid" : "Unavailable", result.LastBackupDisplay);
        Assert.StartsWith("Unavailable", result.WorkerStatus);
        Assert.StartsWith("Unavailable", result.LicenseStatus);
        Assert.StartsWith("Unavailable", result.DatabaseSize);
    }

    private static SupplierPaymentReadDto[] Payments(int count) => Enumerable.Range(0, count).Select(index =>
        new SupplierPaymentReadDto(Guid.NewGuid(), $"PAY-{index}", 1, SupplierPaymentPurpose.Settlement,
            SupplierSettlementMethod.External, DateTimeOffset.UtcNow.AddMinutes(-index), SupplierSettlementStatus.Posted, null, null)).ToArray();

    private static SupplierAccountWorkspaceDto Workspace(IReadOnlyList<SupplierPaymentReadDto> rows) =>
        new(new(Guid.NewGuid(), 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), [], rows, [], [], new(0, 0, 0, 0, 0, 0));

    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private static DesktopApiClient Api(Func<HttpRequestMessage, HttpResponseMessage> response) =>
        new(new HttpClient(new Handler(response)) { BaseAddress = new Uri("http://127.0.0.1:7150") }, ownsClient: true);

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
    private sealed class CatalogGateway : IPosCatalogGateway
    {
        public int RequestedSize { get; private set; }
        public Task<IReadOnlyList<PosCatalogGatewayItem>> LoadAsync(string? search, int pageSize, CancellationToken cancellationToken = default)
        {
            RequestedSize = pageSize;
            IReadOnlyList<PosCatalogGatewayItem> rows = Enumerable.Range(0, 501).Take(pageSize).Select(index =>
                new PosCatalogGatewayItem(Guid.NewGuid(), Guid.NewGuid(), $"Product {index}", $"SKU{index}", "Category", "Pcs", 1, 1, 1, false)).ToArray();
            return Task.FromResult(rows);
        }
    }
    public class UnusedProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new InvalidOperationException("Mutation must not run.");
    }
    public class ReadUnavailableProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new DesktopApiException("test.unavailable", "Unavailable");
    }
}
