using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Media;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Application.Gateways;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Desktop.Navigation;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Desktop.ViewModels;
using EdgeRetails.Domain.Sales;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EdgeRetails.Desktop.PerformanceTests;

public sealed class Phase3DesktopCutoverTests
{
    [Fact]
    public void NavigationItem_IsVisibleOnlyWhenVerifiedSessionHasScreenPermission()
    {
        var session = new UserSessionContext(
            "Cashier", "CASHIER", "CA", userId: Guid.NewGuid(), sessionId: Guid.NewGuid(),
            permissionKeys: new HashSet<string>([PermissionKeys.SalesPosUse], StringComparer.OrdinalIgnoreCase));
        var permissionService = new BackendFrontendPermissionService();
        var icon = Geometry.Parse("M0,0 L1,1");

        var pos = NavigationItemViewModel.CreateForSession(
            "POS", NavigationTarget.POS, icon, session, permissionService);
        var reports = NavigationItemViewModel.CreateForSession(
            "Reports", NavigationTarget.Reports, icon, session, permissionService);

        Assert.True(pos.IsVisible);
        Assert.False(reports.IsVisible);
    }

    [Fact]
    public void ErrorPresentation_UsesStableCodeAndHidesRawTechnicalMessage()
    {
        var permissionFailure = new DesktopApiException(
            "auth.permission_denied",
            "NpgsqlException: SELECT * FROM protected_table; connection password=secret");
        var unexpectedFailure = new InvalidOperationException(
            "System.InvalidOperationException: internal file path and stack details");

        var permissionMessage = DesktopErrorPresentation.ForException(
            permissionFailure,
            "The request could not be completed.");
        var unexpectedMessage = DesktopErrorPresentation.ForException(
            unexpectedFailure,
            "The request could not be completed.");

        Assert.Equal("Your account does not have permission to perform this action.", permissionMessage);
        Assert.Equal("The request could not be completed.", unexpectedMessage);
        Assert.DoesNotContain("NpgsqlException", permissionMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("protected_table", permissionMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("internal file path", unexpectedMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProductManagement_DisposeCancelsInitialReadAndClearsBusyState()
    {
        var service = new CancellationAwareProductManagementService();
        var viewModel = new ProductManagementViewModel(
            new TestToastService(),
            new TestDialogService(),
            service);

        var cancellationToken = await service.SnapshotStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(viewModel.IsLoading);
        Assert.False(viewModel.RefreshCommand.CanExecute(null));

        viewModel.Dispose();

        Assert.True(cancellationToken.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await service.SnapshotReadTask.WaitAsync(TimeSpan.FromSeconds(5)));
        for (var attempt = 0; viewModel.IsLoading && attempt < 50; attempt++)
        {
            await Task.Delay(10);
        }
        Assert.False(viewModel.IsLoading);
    }

    [Fact]
    public async Task ApiClient_AppliesVerifiedTerminalAndSessionHeadersToServerRequest()
    {
        var terminalId = Guid.CreateVersion7();
        var sessionId = Guid.CreateVersion7();
        string? observedTerminalId = null;
        string? observedTerminalSecret = null;
        string? observedSessionId = null;
        string? observedProtocolVersion = null;
        using var http = new HttpClient(new StubHandler(request =>
        {
            observedTerminalId = request.Headers.GetValues("X-Terminal-Id").Single();
            observedTerminalSecret = request.Headers.GetValues("X-Terminal-Secret").Single();
            observedSessionId = request.Headers.GetValues("X-Session-Id").Single();
            observedProtocolVersion = request.Headers.GetValues("X-Protocol-Version").Single();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"value\":7}")
            };
        })) { BaseAddress = new Uri("http://127.0.0.1:7150") };
        using var client = new DesktopApiClient(http);
        client.SetTerminalContext(terminalId, "installed-secret");
        client.SetSession(sessionId);

        var result = await client.GetAsync<NumberResponse>("/api/example?x=1");

        Assert.Equal(7, result.Value);
        Assert.Equal(terminalId.ToString("D"), observedTerminalId);
        Assert.Equal("installed-secret", observedTerminalSecret);
        Assert.Equal(sessionId.ToString("D"), observedSessionId);
        Assert.Equal(TerminalProtocol.CurrentProtocolVersion, observedProtocolVersion);
    }

    [Fact]
    public async Task ApiClient_MapsCanonicalPermissionDenialWithoutRawServerBody()
    {
        using var http = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("{\"code\":\"auth.permission_denied\",\"message\":\"Permission required.\"}")
            })) { BaseAddress = new Uri("http://127.0.0.1:7150") };
        using var client = new DesktopApiClient(http);

        var error = await Assert.ThrowsAsync<DesktopApiException>(
            () => client.GetAsync<NumberResponse>("/api/example"));

        Assert.Equal("auth.permission_denied", error.Code);
        Assert.Equal("Permission required.", error.Message);
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
    }

    [Fact]
    public void ApiClient_RejectsNonLoopbackServerOrigin()
    {
        using var http = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)))
        {
            BaseAddress = new Uri("http://example.com:7150")
        };

        Assert.Throws<ArgumentException>(() => new DesktopApiClient(http));
    }

    [Fact]
    public async Task SharedHandler_ProvidesCurrentSessionToRemoteApplicationGatewayRequests()
    {
        var sessionId = Guid.CreateVersion7();
        string? observed = null;
        var handler = new DesktopSessionForwardingHandler(() => sessionId)
        {
            InnerHandler = new StubHandler(request =>
            {
                observed = request.Headers.GetValues("X-Session-Id").Single();
                return new HttpResponseMessage(HttpStatusCode.OK);
            })
        };
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:7150")
        };

        using var response = await http.GetAsync("/api/example");

        Assert.Equal(sessionId.ToString("D"), observed);
    }

    [Fact]
    public async Task SharedHandler_ReportsInvalidatedSessionFromRemoteGatewayResponse()
    {
        var sessionId = Guid.CreateVersion7();
        Guid? invalidatedSession = null;
        string? invalidationCode = null;
        var handler = new DesktopSessionForwardingHandler(
            () => sessionId,
            (id, code) =>
            {
                invalidatedSession = id;
                invalidationCode = code;
            })
        {
            InnerHandler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(
                    "{\"code\":\"auth.session_invalid\",\"message\":\"Session expired.\"}")
            })
        };
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:7150")
        };

        using var response = await http.GetAsync("/api/example");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(sessionId, invalidatedSession);
        Assert.Equal("auth.session_invalid", invalidationCode);
    }

    [Fact]
    public async Task FirstSetup_UsesServerBootstrapWithOneOperationIdentity()
    {
        string? observedPath = null;
        var operationIds = new List<Guid>();
        var requestCount = 0;
        using var http = new HttpClient(new StubHandler(request =>
        {
            observedPath = request.RequestUri?.AbsolutePath;
            using var payload = JsonDocument.Parse(
                request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            operationIds.Add(payload.RootElement.GetProperty("clientOperationId").GetGuid());
            if (requestCount++ == 0)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent(
                        "{\"code\":\"system.not_ready\",\"message\":\"Server unavailable.\"}")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"installationId\":\"00000000-0000-0000-0000-000000000001\"," +
                    "\"ownerUserId\":\"00000000-0000-0000-0000-000000000002\"," +
                    "\"walkInCustomerId\":\"00000000-0000-0000-0000-000000000003\"}")
            };
        })) { BaseAddress = new Uri("http://127.0.0.1:7150") };
        using var client = new DesktopApiClient(http);
        var setup = new RemoteBackendSetupService(client);

        await Assert.ThrowsAsync<DesktopApiException>(() => setup.CompleteFirstSetupAsync(
            "Shop", "Owner", "", "", "1234", "Retail", "signed-license"));
        await setup.CompleteFirstSetupAsync(
            "Shop", "Owner", "", "", "1234", "Retail", "signed-license");

        Assert.Equal("/api/setup/bootstrap", observedPath);
        Assert.Equal(2, operationIds.Count);
        Assert.NotEqual(Guid.Empty, operationIds[0]);
        Assert.Equal(operationIds[0], operationIds[1]);
    }

    [Fact]
    public async Task SalesHistory_UsesBoundedServerReadWithCursor()
    {
        Uri? observed = null;
        using var http = new HttpClient(new StubHandler(request =>
        {
            observed = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]")
            };
        })) { BaseAddress = new Uri("http://127.0.0.1:7150") };
        using var client = new DesktopApiClient(http);
        var history = new BackendSalesHistoryService(client);
        var cursorTime = new DateTimeOffset(2026, 9, 27, 12, 30, 0, TimeSpan.Zero);
        var cursorId = Guid.CreateVersion7();

        var page = await history.GetPageAsync(
            SalesHistoryPeriod.Today, "INV 001", 25, cursorTime, cursorId);

        Assert.Empty(page.Rows);
        Assert.False(page.HasMore);
        Assert.Equal("/api/sales", observed?.AbsolutePath);
        Assert.Contains("pageSize=25", observed?.Query);
        Assert.Contains("search=INV%20001", observed?.Query);
        Assert.Contains($"beforeSaleId={cursorId:D}", observed?.Query);
        Assert.Contains("beforeCompletedAt=", observed?.Query);
    }

    [Fact]
    public async Task ApiClient_PutUsesServerRouteAndCurrentSession()
    {
        HttpMethod? method = null;
        string? path = null;
        string? session = null;
        using var http = new HttpClient(new StubHandler(request =>
        {
            method = request.Method;
            path = request.RequestUri?.AbsolutePath;
            session = request.Headers.GetValues("X-Session-Id").Single();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"value\":1}")
            };
        })) { BaseAddress = new Uri("http://127.0.0.1:7150") };
        using var client = new DesktopApiClient(http);
        var sessionId = Guid.CreateVersion7();
        client.SetSession(sessionId);

        var result = await client.PutAsync<object, NumberResponse>(
            "/api/suppliers/example", new { name = "Updated" });

        Assert.Equal(1, result.Value);
        Assert.Equal(HttpMethod.Put, method);
        Assert.Equal("/api/suppliers/example", path);
        Assert.Equal(sessionId.ToString("D"), session);
    }

    [Fact]
    public async Task SaleReturn_LostResponseRecoversCommittedReturnUsingSameOperationId()
    {
        var saleId = Guid.CreateVersion7();
        var saleItemId = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();
        var returnId = Guid.CreateVersion7();
        var operationId = Guid.CreateVersion7();
        var postCount = 0;
        var returned = false;
        var detail = new SaleDetailDto(
            saleId, "INV-1", DateTimeOffset.UtcNow, null, "Walk-in", 100m, 0m, 100m,
            SalePaymentMethod.Cash, 100m, 100m, 0m, null, null,
            [new SaleDetailItemDto(saleItemId, productId, "Cable", "CBL-1", 1m, 1m, 1m,
                100m, 100m, 0m, 100m)],
            [new SaleReturnSummaryDto(returnId, "RET-1", DateTimeOffset.UtcNow,
                "DAMAGED", RefundMethod.Cash, 100m)],
            [new SaleReturnItemDetailDto(returnId, saleItemId, productId, "CBL-1", 1m, 100m)]);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        using var http = new HttpClient(new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/sales" && request.Method == HttpMethod.Get)
            {
                var row = new SalesHistoryRowDto(saleId, "INV-1", detail.CompletedAt,
                    "Walk-in", 100m, SalePaymentMethod.Cash, 1, returned ? 100m : 0m);
                return JsonResponse(new[] { row });
            }
            if (path == $"/api/sales/{saleId:D}" && request.Method == HttpMethod.Get)
            {
                return JsonResponse(returned ? detail : detail with { Returns = [], ReturnItems = [] });
            }
            if (path == "/api/sales/return" && request.Method == HttpMethod.Post)
            {
                postCount++;
                using var payload = JsonDocument.Parse(
                    request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                Assert.Equal(operationId, payload.RootElement.GetProperty("clientOperationId").GetGuid());
                returned = true;
                throw new HttpRequestException("Response lost after commit");
            }
            if (path == $"/api/system/operations/{operationId:D}")
            {
                return JsonResponse(new OperationStatusResult(
                    operationId, true, "SaleReturn", returnId, "RET-1", true));
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {path}");
        })) { BaseAddress = new Uri("http://127.0.0.1:7150") };
        using var client = new DesktopApiClient(http);
        var gateway = new RemoteApplicationGateway(http);
        using var provider = new ServiceCollection()
            .AddSingleton<IApplicationGateway>(gateway)
            .BuildServiceProvider();
        var service = new BackendTransactionService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            () => Guid.CreateVersion7(), client);

        var result = await service.RecordReturnAsync(new RecordSaleReturnRequest
        {
            ClientOperationId = operationId,
            InvoiceNumber = "INV-1",
            Disposition = EdgeRetails.Desktop.Services.SaleReturnDisposition.Damaged,
            RefundMethod = "Cash Refund",
            TotalRefundAmount = 100m,
            Items = [new SaleReturnItemRecord
            {
                ProductId = productId.ToString("D"), Sku = "CBL-1", Quantity = 1m,
                RefundAmount = 100m
            }]
        });

        Assert.Equal("RET-1", result.ReturnNumber);
        Assert.Equal(1, postCount);

        HttpResponseMessage JsonResponse<T>(T payload) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, jsonOptions))
        };
    }

    private sealed class CancellationAwareProductManagementService : IBackendProductManagementService
    {
        public TaskCompletionSource<CancellationToken> SnapshotStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<BackendProductManagementSnapshot> SnapshotReadTask { get; private set; } = null!;

        public Task<BackendProductManagementSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<BackendProductManagementSnapshot>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            SnapshotReadTask = completion.Task;
            SnapshotStarted.TrySetResult(cancellationToken);
            cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            return completion.Task;
        }

        public Task<IReadOnlyList<BackendProductManagementItem>> GetProductsPageAsync(
            string? search, bool? isActive, Guid? categoryId, int pageSize = 200,
            string? beforeName = null, Guid? beforeProductId = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<BackendProductManagementItem?> GetProductAsync(Guid productId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackendProductManagementItem?> GetProductBySkuAsync(string sku, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackendProductManagementItem> CreateProductAsync(BackendProductCatalogRequest request, IReadOnlyList<BackendProductUnitConfiguration> units, IReadOnlyCollection<Guid> linkedSupplierIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackendProductManagementItem> UpdateProductAsync(Guid productId, long expectedVersion, BackendProductCatalogRequest request, IReadOnlyList<BackendProductUnitConfiguration> units, IReadOnlyCollection<Guid> linkedSupplierIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackendProductManagementItem> SetProductActiveAsync(Guid productId, long expectedVersion, bool isActive, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveCompanyAsync(Guid? companyId, string name, string? code = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetCompanyActiveAsync(Guid companyId, bool isActive, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveCategoryAsync(Guid? categoryId, string name, string? identitySymbol = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetCategoryActiveAsync(Guid categoryId, bool isActive, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveUnitAsync(Guid? unitId, string name, string symbol, int displayDecimalPlaces, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetUnitActiveAsync(Guid unitId, bool isActive, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class TestToastService : IToastService
    {
        private readonly ObservableCollection<ToastMessage> _items = [];
        public ReadOnlyObservableCollection<ToastMessage> Messages { get; }

        public TestToastService() => Messages = new ReadOnlyObservableCollection<ToastMessage>(_items);

        public void Show(string message, ToastTone tone = ToastTone.Neutral, TimeSpan? duration = null) { }
    }

    private sealed class TestDialogService : IDialogService
    {
        public event PropertyChangedEventHandler? PropertyChanged
        {
            add { }
            remove { }
        }

        public bool IsOpen => false;
        public object? Content => null;
        public void Show(object content) { }
        public void Close() { }
    }

    private sealed record NumberResponse(int Value);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
