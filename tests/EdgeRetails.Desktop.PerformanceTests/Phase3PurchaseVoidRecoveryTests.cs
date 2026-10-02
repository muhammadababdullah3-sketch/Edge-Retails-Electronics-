using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Desktop.ViewModels;

namespace EdgeRetails.Desktop.PerformanceTests;

public sealed class Phase3PurchaseVoidRecoveryTests
{
    [Fact]
    public async Task PendingVoidReusesOriginalOperationIdentityAfterDesktopServiceRestart()
    {
        var directory = Directory.CreateTempSubdirectory("edge-retails-purchase-void-");
        try
        {
            var storePath = Path.Combine(directory.FullName, "operation-intents.json");
            var purchase = CreatePurchase();
            var actorId = Guid.CreateVersion7();
            var firstCommand = new TaskCompletionSource<VoidPurchaseCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var firstClient = CreateApiClient(async request =>
                   {
                       if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/purchasing/void")
                       {
                           firstCommand.TrySetResult(await request.Content!.ReadFromJsonAsync<VoidPurchaseCommand>()
                               ?? throw new InvalidDataException("Void request was empty."));
                           throw new HttpRequestException("Simulated response loss after the request reached the Server.");
                       }

                       if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.StartsWith("/api/system/operations/", StringComparison.Ordinal))
                       {
                           throw new HttpRequestException("Simulated Server outage during outcome reconciliation.");
                       }

                       throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
                   }))
            {
                var service = new RemotePurchasingInventoryService(
                    firstClient, () => actorId, new FileClientOperationIntentStore(storePath));
                var error = await Assert.ThrowsAsync<BackendOperationException>(() =>
                    service.VoidPurchaseAsync(purchase, "Purchase void"));
                Assert.Equal("purchasing.void_outcome_unknown", error.Code);
            }

            var originalCommand = await firstCommand.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotEqual(Guid.Empty, originalCommand.ClientOperationId);

            var changedPayloadRequests = 0;
            using (var changedPayloadClient = CreateApiClient(_ =>
                   {
                       changedPayloadRequests++;
                       return Task.FromResult(JsonResponse(new VoidPurchaseResult(
                           purchase.BackendPurchaseId!.Value, Guid.CreateVersion7(), WasExisting: true)));
                   }))
            {
                var changedPayloadService = new RemotePurchasingInventoryService(
                    changedPayloadClient, () => actorId, new FileClientOperationIntentStore(storePath));
                var error = await Assert.ThrowsAsync<BackendOperationException>(() =>
                    changedPayloadService.VoidPurchaseAsync(purchase, "Different reason"));
                Assert.Equal("purchasing.void_outcome_unknown", error.Code);
            }
            Assert.Equal(0, changedPayloadRequests);

            VoidPurchaseCommand? replayedCommand = null;
            using (var restartedClient = CreateApiClient(async request =>
                   {
                       Assert.Equal(HttpMethod.Post, request.Method);
                       Assert.Equal("/api/purchasing/void", request.RequestUri!.AbsolutePath);
                       replayedCommand = await request.Content!.ReadFromJsonAsync<VoidPurchaseCommand>();
                       return JsonResponse(new VoidPurchaseResult(
                           purchase.BackendPurchaseId!.Value, Guid.CreateVersion7(), WasExisting: true));
                   }))
            {
                var restartedService = new RemotePurchasingInventoryService(
                    restartedClient, () => actorId, new FileClientOperationIntentStore(storePath));
                await restartedService.VoidPurchaseAsync(purchase, "Purchase void");
            }

            Assert.NotNull(replayedCommand);
            Assert.Equal(originalCommand.ClientOperationId, replayedCommand.ClientOperationId);
            Assert.Equal(purchase.BackendPurchaseId, replayedCommand.PurchaseId);
            Assert.False(new FileClientOperationIntentStore(storePath)
                .FindPendingByPrefix($"purchase-void:{purchase.BackendPurchaseId:D}").Any());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task PurchaseDetailRequiresConfirmationBeforeVoidingAndDisablesActionAfterSuccess()
    {
        var purchase = CreatePurchase();
        var backend = PurchasingBackendProxy.Create(purchase);
        var dialog = new DialogService();
        var toast = new TestToastService();
        var viewModel = new PurchaseDetailViewModel(
            purchase, new TestDrawerService(), dialog, toast, backend);

        Assert.True(viewModel.CanVoidPurchase);
        viewModel.VoidPurchaseCommand.Execute(null);
        var cancelConfirmation = Assert.IsType<ConfirmationDialogViewModel>(dialog.Content);
        cancelConfirmation.CancelCommand.Execute(null);
        Assert.Equal(0, ((PurchasingBackendProxy)(object)backend).VoidCallCount);

        viewModel.VoidPurchaseCommand.Execute(null);
        var confirmation = Assert.IsType<ConfirmationDialogViewModel>(dialog.Content);
        confirmation.ConfirmCommand.Execute(null);
        await ((PurchasingBackendProxy)(object)backend).VoidCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await toast.SuccessShown.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(viewModel.CanVoidPurchase);
        Assert.Equal(1, ((PurchasingBackendProxy)(object)backend).VoidCallCount);
    }

    private static PurchaseRecord CreatePurchase() => new()
    {
        BackendPurchaseId = Guid.CreateVersion7(),
        BackendSupplierId = Guid.CreateVersion7(),
        PurchaseNumber = "PO-VOID-1",
        Supplier = "Test supplier",
        InvoiceNumber = "INV-VOID-1",
        Date = DateTime.Today,
        Items = []
    };

    private static DesktopApiClient CreateApiClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
    {
        var http = new HttpClient(new StubHandler(respond))
        {
            BaseAddress = new Uri("http://127.0.0.1:7150")
        };
        return new DesktopApiClient(http, ownsClient: true);
    }

    private static HttpResponseMessage JsonResponse<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(value, options: JsonOptions)
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request);
    }

    private sealed class TestToastService : IToastService
    {
        private readonly ObservableCollection<ToastMessage> _messages = [];
        public ReadOnlyObservableCollection<ToastMessage> Messages { get; }
        public TaskCompletionSource<bool> SuccessShown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TestToastService() => Messages = new ReadOnlyObservableCollection<ToastMessage>(_messages);

        public void Show(string message, ToastTone tone = ToastTone.Neutral, TimeSpan? duration = null)
        {
            _messages.Add(new ToastMessage(Guid.CreateVersion7(), message, tone));
            if (tone == ToastTone.Success)
            {
                SuccessShown.TrySetResult(true);
            }
        }
    }

    private sealed class TestDrawerService : IDrawerService
    {
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public bool IsOpen => false;
        public object? Content => null;
        public void Show(object content) { }
        public void Close() { }
    }

    public class PurchasingBackendProxy : DispatchProxy
    {
        private PurchaseRecord _purchase = null!;
        public int VoidCallCount { get; private set; }
        public TaskCompletionSource<bool> VoidCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static IBackendPurchasingInventoryService Create(PurchaseRecord purchase)
        {
            var backend = DispatchProxy.Create<IBackendPurchasingInventoryService, PurchasingBackendProxy>();
            ((PurchasingBackendProxy)(object)backend)._purchase = purchase;
            return backend;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            nameof(IBackendPurchasingInventoryService.GetPurchaseAsync) => Task.FromResult<PurchaseRecord?>(_purchase),
            nameof(IBackendPurchasingInventoryService.VoidPurchaseAsync) => RecordVoid(),
            _ => throw new NotSupportedException($"Unexpected backend call: {targetMethod?.Name}")
        };

        private Task RecordVoid()
        {
            VoidCallCount++;
            _purchase = new PurchaseRecord
            {
                BackendPurchaseId = _purchase.BackendPurchaseId,
                BackendSupplierId = _purchase.BackendSupplierId,
                IsVoided = true,
                PurchaseNumber = _purchase.PurchaseNumber,
                Supplier = _purchase.Supplier,
                InvoiceNumber = _purchase.InvoiceNumber,
                Date = _purchase.Date,
                Note = _purchase.Note,
                OtherCharges = _purchase.OtherCharges,
                Items = _purchase.Items,
                BackendSubtotal = _purchase.BackendSubtotal,
                BackendTotal = _purchase.BackendTotal,
                BackendItemCount = _purchase.BackendItemCount
            };
            VoidCompleted.TrySetResult(true);
            return Task.CompletedTask;
        }
    }
}
