using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Gateways;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Desktop.ViewModels;
using EdgeRetails.Domain.Sales;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.Desktop.PerformanceTests;

public sealed class Phase3SaleDurableIntentTests
{
    [Fact]
    public async Task SaleIntentSurvivesRestartBindsPayloadAndReplaysOneCanonicalOperation()
    {
        var directory = Directory.CreateTempSubdirectory("edge-retails-sale-intent-");
        try
        {
            var storePath = Path.Combine(directory.FullName, "operation-intents.json");
            var originalOperationId = Guid.CreateVersion7();
            var saleId = Guid.CreateVersion7();
            var productId = Guid.CreateVersion7();
            var unitId = Guid.CreateVersion7();
            var gateway = IdempotentSaleGateway.Create(saleId);
            var gatewayState = (IdempotentSaleGateway)(object)gateway;
            using var http = new HttpClient(new StubHandler(_ => JsonResponse(new SaleDetailDto(
                saleId, "INV-RESTART-1", DateTimeOffset.UtcNow, null, "Walk-in Customer",
                100m, 0m, 100m, SalePaymentMethod.Cash, 100m, 100m, 0m, null, null,
                [], [], []))))
            {
                BaseAddress = new Uri("http://127.0.0.1:7150")
            };
            using var api = new DesktopApiClient(http);

            var firstStore = new FileClientOperationIntentStore(storePath);
            var firstService = CreateService(gateway, api, Guid.CreateVersion7(), firstStore);
            var originalItem = CreateItem(productId, unitId, quantity: 1m);
            var firstCheckout = CreateCheckout(firstService, originalOperationId, [originalItem], 100m);

            await firstCheckout.CompleteSaleAsync();

            Assert.Null(firstCheckout.CompletedTransaction);
            Assert.Contains("result is unknown", firstCheckout.ValidationMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, gatewayState.MutationAttempts);
            Assert.Equal(1, gatewayState.CanonicalOperations);

            // Recreate both the file-backed store and the checkout after a process restart.
            var restartedStore = new FileClientOperationIntentStore(storePath);
            var pendingIntent = Assert.Single(restartedStore.FindPendingByPrefix(
                BackendTransactionService.SaleCheckoutIntentKey));
            Assert.Equal(originalOperationId, pendingIntent.OperationId);

            var changedService = CreateService(gateway, api, Guid.CreateVersion7(), restartedStore);
            var changedCheckout = CreateCheckout(
                changedService,
                pendingIntent.OperationId,
                [CreateItem(productId, unitId, quantity: 2m)],
                200m);
            await changedCheckout.CompleteSaleAsync();

            Assert.Null(changedCheckout.CompletedTransaction);
            Assert.NotNull(changedCheckout.ValidationMessage);
            Assert.Equal(1, gatewayState.MutationAttempts);
            Assert.Equal(1, gatewayState.CanonicalOperations);

            // Rebuild the exact original payment/cart payload and replay it through a new service.
            var replayStore = new FileClientOperationIntentStore(storePath);
            var replayService = CreateService(gateway, api, Guid.CreateVersion7(), replayStore);
            var replayCheckout = CreateCheckout(
                replayService,
                pendingIntent.OperationId,
                [CreateItem(productId, unitId, quantity: 1m)],
                100m);
            await replayCheckout.CompleteSaleAsync();

            Assert.NotNull(replayCheckout.CompletedTransaction);
            Assert.Equal(saleId, replayCheckout.CompletedTransaction.BackendSaleId);
            Assert.Equal(2, gatewayState.MutationAttempts);
            Assert.Equal(1, gatewayState.CanonicalOperations);
            Assert.Equal(new[] { originalOperationId, originalOperationId },
                gatewayState.Commands.Select(command => command.ClientOperationId));
            Assert.All(gatewayState.Commands, command =>
            {
                Assert.Equal(100m, command.AmountTendered);
                Assert.Equal(productId, Assert.Single(command.Lines).ProductId);
                Assert.Equal(1m, Assert.Single(command.Lines).EnteredQuantity);
            });
            Assert.Empty(new FileClientOperationIntentStore(storePath).FindPendingByPrefix(
                BackendTransactionService.SaleCheckoutIntentKey));
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    private static BackendTransactionService CreateService(
        IApplicationGateway gateway,
        DesktopApiClient api,
        Guid actorId,
        IClientOperationIntentStore intents)
    {
        var provider = new ServiceCollection()
            .AddSingleton<IApplicationGateway>(gateway)
            .BuildServiceProvider();
        return new BackendTransactionService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            () => actorId,
            api,
            intents);
    }

    private static CompleteSaleViewModel CreateCheckout(
        ITransactionService service,
        Guid operationId,
        IReadOnlyList<SaleTransactionItem> items,
        decimal total) => new(
        totalToPay: total,
        transactionService: service,
        items: items,
        subtotal: total,
        clientOperationId: operationId);

    private static SaleTransactionItem CreateItem(Guid productId, Guid unitId, decimal quantity) => new()
    {
        ProductId = productId.ToString("D"),
        BackendProductId = productId,
        BackendProductUnitId = unitId,
        ProductName = "Test product",
        Sku = "TEST-1",
        UnitPrice = 100m,
        ListUnitPrice = 100m,
        Quantity = quantity,
        LineTotal = quantity * 100m
    };

    private static HttpResponseMessage JsonResponse<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value))
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private class IdempotentSaleGateway : DispatchProxy
    {
        private Guid _saleId;
        private readonly ConcurrentDictionary<Guid, CompleteSaleResult> _committed = new();

        public List<CompleteSaleCommand> Commands { get; } = [];
        public int MutationAttempts { get; private set; }
        public int CanonicalOperations => _committed.Count;

        public static IApplicationGateway Create(Guid saleId)
        {
            var proxy = DispatchProxy.Create<IApplicationGateway, IdempotentSaleGateway>();
            var state = (IdempotentSaleGateway)(object)proxy;
            state._saleId = saleId;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IApplicationGateway.CompleteSaleAsync))
            {
                var command = (CompleteSaleCommand)args![0]!;
                Commands.Add(command);
                MutationAttempts++;
                var outcome = _committed.GetOrAdd(command.ClientOperationId, _ =>
                    new CompleteSaleResult(_saleId, "INV-RESTART-1", 100m, 0m, WasExisting: false));

                if (MutationAttempts == 1)
                {
                    return Task.FromResult(Result<CompleteSaleResult>.Failure(
                        "network.connection_lost", "Response was lost after commit."));
                }

                return Task.FromResult(Result<CompleteSaleResult>.Success(outcome with { WasExisting = true }));
            }

            if (targetMethod?.Name == nameof(IApplicationGateway.QueryOperationStatusAsync))
            {
                return Task.FromResult(Result<OperationStatusResult>.Failure(
                    "network.unavailable", "Status endpoint was unavailable."));
            }

            throw new NotSupportedException($"Unexpected gateway call: {targetMethod?.Name}");
        }
    }
}
