using System.IO;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Desktop.ViewModels;
using EdgeRetails.Domain.Catalog;

namespace EdgeRetails.Desktop.PerformanceTests;

public sealed class Phase3PhysicalIntakeDurableIntentTests
{
    [Fact]
    public async Task RemoteIntakeReconcilesCommittedOutcomeAndReplaysCanonicalResult()
    {
        var directory = Directory.CreateTempSubdirectory("edge-retails-intake-reconcile-");
        try
        {
            var purchaseId = Guid.CreateVersion7();
            var productId = Guid.CreateVersion7();
            var unitId = Guid.CreateVersion7();
            var actorId = Guid.CreateVersion7();
            var operationId = Guid.CreateVersion7();
            var command = new ReceiveProductIntakeCommand(
                purchaseId, productId, unitId, 1m, 50m, [], actorId, operationId, "receive test");
            var store = new FileClientOperationIntentStore(Path.Combine(directory.FullName, "operation-intents.json"));
            var key = $"physical-intake:{purchaseId:D}:{unitId:D}";
            var payload = JsonSerializer.Serialize(new
            {
                command.PurchaseId,
                command.ProductId,
                command.ProductUnitId,
                command.EnteredQuantity,
                command.EnteredUnitCost,
                command.SerializedUnits,
                command.CreatedBy,
                command.Note
            });
            _ = store.GetOrCreate(key, payload, operationId);

            var postCount = 0;
            var postedIds = new List<Guid>();
            using var http = new HttpClient(new StubHandler(async request =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    return JsonResponse(JsonSerializer.Serialize(new
                    {
                        ClientOperationId = operationId,
                        Found = true,
                        OperationType = "InventoryMovement",
                        EntityId = Guid.CreateVersion7(),
                        DocumentNumber = purchaseId.ToString("D"),
                        WasCommitted = true,
                        Status = "Succeeded"
                    }, JsonOptions));
                }

                var requestCommand = await request.Content!.ReadFromJsonAsync<ReceiveProductIntakeCommand>();
                postedIds.Add(requestCommand!.ClientOperationId);
                if (Interlocked.Increment(ref postCount) == 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }

                return JsonResponse(JsonSerializer.Serialize(new ReceiveProductIntakeResult(
                    purchaseId, productId, "Test product", "SKU-1", null, null, null, "DLR", 1m, 1m,
                    TrackingMode.Quantity, [], WasExisting: true), JsonOptions));
            }))
            {
                BaseAddress = new Uri("http://127.0.0.1:7150")
            };
            using var api = new DesktopApiClient(http);
            var service = new RemotePurchasingInventoryService(api, () => actorId, store);

            var result = await service.ReceiveProductIntakeAsync(command);

            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.Equal(2, postCount);
            Assert.Equal(new[] { operationId, operationId }, postedIds);
            Assert.DoesNotContain("physical-intake", File.ReadAllText(Path.Combine(directory.FullName, "operation-intents.json")), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task IntakeIdentitySurvivesViewModelRecreationAfterUnknownOutcome()
    {
        var directory = Directory.CreateTempSubdirectory("edge-retails-intake-intent-");
        try
        {
            var storePath = Path.Combine(directory.FullName, "operation-intents.json");
            var purchaseId = Guid.CreateVersion7();
            var productId = Guid.CreateVersion7();
            var unitId = Guid.CreateVersion7();
            var intents = new FileClientOperationIntentStore(storePath);
            var firstCommand = new TaskCompletionSource<ReceiveProductIntakeCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstBackend = CreateBackend(command =>
            {
                firstCommand.TrySetResult(command);
                return Task.FromResult(Result<ReceiveProductIntakeResult>.Failure(
                    "purchasing.intake_outcome_unknown", "Simulated response loss."));
            });

            var firstViewModel = CreateViewModel(purchaseId, productId, unitId, firstBackend, intents);
            firstViewModel.ReceiveIntakeCommand.Execute(null);
            var originalCommand = await firstCommand.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var retryCommand = new TaskCompletionSource<ReceiveProductIntakeCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
            var retryBackend = CreateBackend(command =>
            {
                retryCommand.TrySetResult(command);
                return Task.FromResult(Result<ReceiveProductIntakeResult>.Success(new ReceiveProductIntakeResult(
                    purchaseId, productId, "Test product", "SKU-1", null, null, null, "DLR", 1m, 1m,
                    TrackingMode.Quantity, [], WasExisting: true)));
            });

            var recreatedViewModel = CreateViewModel(purchaseId, productId, unitId, retryBackend,
                new FileClientOperationIntentStore(storePath));
            recreatedViewModel.ReceiveIntakeCommand.Execute(null);
            var replayCommand = await retryCommand.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.NotEqual(Guid.Empty, originalCommand.ClientOperationId);
            Assert.Equal(originalCommand.ClientOperationId, replayCommand.ClientOperationId);
            Assert.True(recreatedViewModel.IntakeCompleted);
            Assert.False(File.Exists(storePath) && File.ReadAllText(storePath).Contains("physical-intake", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    private static PhysicalIntakeViewModel CreateViewModel(
        Guid purchaseId,
        Guid productId,
        Guid unitId,
        IBackendPurchasingInventoryService backend,
        IClientOperationIntentStore intents)
    {
        var actorId = Guid.Parse("01937c58-0a9d-7c40-8e56-441e97263ae1");
        var product = new PosProductItemViewModel(
            "SKU-1", "Test product", "SKU-1", "Brand", "General", 0m, 100m,
            backendProductId: productId, backendProductUnitId: unitId);
        var item = new PurchaseItemRecord
        {
            BackendProductUnitId = unitId,
            Product = product,
            PurchasedQuantity = 1m,
            Cost = 50m
        };
        var purchase = new PurchaseRecord
        {
            BackendPurchaseId = purchaseId,
            PurchaseNumber = "PO-1",
            Supplier = "Supplier",
            InvoiceNumber = "INV-1",
            Items = [item]
        };

        return new PhysicalIntakeViewModel(
            purchase, item, new TestDialogService(), new TestToastService(), backend,
            () => actorId, operationIntents: intents);
    }

    private static IBackendPurchasingInventoryService CreateBackend(
        Func<ReceiveProductIntakeCommand, Task<Result<ReceiveProductIntakeResult>>> receive) =>
        BackendProxy.Create(receive);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json)
    };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request);
    }

    private class BackendProxy : DispatchProxy
    {
        private Func<ReceiveProductIntakeCommand, Task<Result<ReceiveProductIntakeResult>>> _receive = null!;

        public static IBackendPurchasingInventoryService Create(
            Func<ReceiveProductIntakeCommand, Task<Result<ReceiveProductIntakeResult>>> receive)
        {
            var proxy = DispatchProxy.Create<IBackendPurchasingInventoryService, BackendProxy>();
            ((BackendProxy)(object)proxy)._receive = receive;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod?.Name switch
            {
                nameof(IBackendPurchasingInventoryService.ReceiveProductIntakeAsync) =>
                    _receive((ReceiveProductIntakeCommand)args![0]!),
                nameof(IBackendPurchasingInventoryService.GetUnitsForPurchaseItemAsync) =>
                    Task.FromResult<IReadOnlyList<CommittedInventoryUnitDto>>([]),
                _ => throw new NotSupportedException($"Unexpected backend call: {targetMethod?.Name}")
            };
        }
    }

    private sealed class TestDialogService : IDialogService
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public bool IsOpen => false;
        public object? Content => null;
        public void Show(object content) { }
        public void Close() { }
    }

    private sealed class TestToastService : IToastService
    {
        private readonly ObservableCollection<ToastMessage> _messages = [];
        public ReadOnlyObservableCollection<ToastMessage> Messages { get; }

        public TestToastService() => Messages = new ReadOnlyObservableCollection<ToastMessage>(_messages);

        public void Show(string message, ToastTone tone = ToastTone.Neutral, TimeSpan? duration = null) { }
    }
}
