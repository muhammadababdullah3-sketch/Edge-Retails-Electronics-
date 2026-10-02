using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Desktop.ViewModels;

namespace EdgeRetails.Desktop.PerformanceTests;

public sealed class Phase3PosResponsivenessRegressionTests
{
    [Fact]
    public async Task PosViewModelStartupDoesNotWaitForOperationIntentMutex()
    {
        var store = new BlockingIntentStore();
        var transactionService = DispatchProxy.Create<ITransactionService, UnusedTransactionProxy>();
        var construction = Task.Run(() => new PosViewModel(
            transactionService: transactionService,
            operationIntents: store));
        PosViewModel? viewModel = null;

        try
        {
            Assert.True(store.ReadStarted.Wait(TimeSpan.FromSeconds(2)), "POS did not inspect pending sale intents.");
            var completed = await Task.WhenAny(construction, Task.Delay(TimeSpan.FromMilliseconds(250)));
            Assert.Same(construction, completed);

            viewModel = await construction;
            viewModel.AddToCart(new PosProductItemViewModel(
                "test-product", "Test product", "TEST", "—", "Test", 1m, 10m));
            Assert.False(viewModel.CompleteSaleCommand.CanExecute(null));
        }
        finally
        {
            store.ReleaseRead.Set();
            await construction.WaitAsync(TimeSpan.FromSeconds(2));
        }

        Assert.NotNull(viewModel);
        Assert.True(await WaitUntilAsync(
            () => viewModel.CompleteSaleCommand.CanExecute(null),
            TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task SaleIntentPersistenceDoesNotBlockCallingThread()
    {
        var store = new BlockingWriteIntentStore();
        var operationId = Guid.CreateVersion7();
        Task<Guid>? createTask = null;
        var createInvocation = Task.Run(() =>
        {
            createTask = store.GetOrCreateAsync("sale-checkout", "sale-payload", operationId);
        });

        try
        {
            Assert.True(store.CreateStarted.Wait(TimeSpan.FromSeconds(2)));
            var returned = await Task.WhenAny(createInvocation, Task.Delay(TimeSpan.FromMilliseconds(250)));
            Assert.Same(createInvocation, returned);
            Assert.NotNull(createTask);
            Assert.False(createTask.IsCompleted);
        }
        finally
        {
            store.ReleaseCreate.Set();
            await createInvocation.WaitAsync(TimeSpan.FromSeconds(2));
        }

        Assert.Equal(operationId, await createTask!.WaitAsync(TimeSpan.FromSeconds(2)));

        Task? completeTask = null;
        var completeInvocation = Task.Run(() =>
        {
            completeTask = store.CompleteAsync("sale-checkout", operationId);
        });

        try
        {
            Assert.True(store.CompleteStarted.Wait(TimeSpan.FromSeconds(2)));
            var returned = await Task.WhenAny(completeInvocation, Task.Delay(TimeSpan.FromMilliseconds(250)));
            Assert.Same(completeInvocation, returned);
            Assert.NotNull(completeTask);
            Assert.False(completeTask.IsCompleted);
        }
        finally
        {
            store.ReleaseComplete.Set();
            await completeInvocation.WaitAsync(TimeSpan.FromSeconds(2));
        }

        await completeTask!.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task SupersededCatalogSearchCancellationDoesNotShowFailureToast()
    {
        var gateway = new SupersededSearchGateway();
        var toast = new CapturingToastService();
        var transactionService = DispatchProxy.Create<ITransactionService, UnusedTransactionProxy>();
        var viewModel = new PosViewModel(
            toastService: toast,
            transactionService: transactionService,
            posCatalogGateway: gateway);

        await gateway.InitialLoadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.SearchText = "older";
        await gateway.OlderSearchStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        viewModel.SearchText = "newer";
        await gateway.NewerSearchCompleted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.DoesNotContain(toast.Messages, message => message.Tone == ToastTone.Danger);
    }

    [Fact]
    public async Task CurrentCatalogSearchFailureStillShowsOperatorError()
    {
        var gateway = new SupersededSearchGateway();
        var toast = new CapturingToastService();
        var transactionService = DispatchProxy.Create<ITransactionService, UnusedTransactionProxy>();
        var viewModel = new PosViewModel(
            toastService: toast,
            transactionService: transactionService,
            posCatalogGateway: gateway);

        await gateway.InitialLoadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.SearchText = "broken";
        await gateway.CurrentSearchFailureStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(await WaitUntilAsync(
            () => toast.Messages.Any(message => message.Tone == ToastTone.Danger),
            TimeSpan.FromSeconds(1)));
    }

    private sealed class BlockingIntentStore : IClientOperationIntentStore
    {
        public ManualResetEventSlim ReadStarted { get; } = new();
        public ManualResetEventSlim ReleaseRead { get; } = new();

        public IReadOnlyList<ClientOperationIntent> FindPendingByPrefix(string operationKeyPrefix)
        {
            _ = operationKeyPrefix;
            ReadStarted.Set();
            if (!ReleaseRead.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Test did not release the intent-store read.");
            }

            return [];
        }

        public Guid GetOrCreate(string operationKey, string payload)
        {
            _ = operationKey;
            _ = payload;
            throw new NotSupportedException();
        }

        public void Complete(string operationKey, Guid operationId)
        {
            _ = operationKey;
            _ = operationId;
            throw new NotSupportedException();
        }
    }

    private sealed class SupersededSearchGateway : IPosCatalogGateway
    {
        public TaskCompletionSource InitialLoadCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource OlderSearchStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource NewerSearchCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CurrentSearchFailureStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<PosCatalogGatewayItem>> LoadAsync(
            string? search,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            _ = pageSize;
            if (search is null)
            {
                InitialLoadCompleted.TrySetResult();
                return [];
            }

            if (search == "older")
            {
                OlderSearchStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return [];
            }

            if (search == "newer")
            {
                NewerSearchCompleted.TrySetResult();
                return [];
            }

            if (search == "broken")
            {
                CurrentSearchFailureStarted.TrySetResult();
                throw new HttpRequestException("Simulated current-search failure.");
            }

            throw new InvalidOperationException("Unexpected test search value.");
        }
    }

    private sealed class BlockingWriteIntentStore : IClientOperationIntentStore
    {
        public ManualResetEventSlim CreateStarted { get; } = new();
        public ManualResetEventSlim ReleaseCreate { get; } = new();
        public ManualResetEventSlim CompleteStarted { get; } = new();
        public ManualResetEventSlim ReleaseComplete { get; } = new();

        public IReadOnlyList<ClientOperationIntent> FindPendingByPrefix(string operationKeyPrefix)
        {
            _ = operationKeyPrefix;
            return [];
        }

        public Guid GetOrCreate(string operationKey, string payload)
        {
            _ = operationKey;
            _ = payload;
            throw new NotSupportedException();
        }

        public Guid GetOrCreate(string operationKey, string payload, Guid preferredOperationId)
        {
            _ = operationKey;
            _ = payload;
            CreateStarted.Set();
            if (!ReleaseCreate.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Test did not release the create operation.");
            }

            return preferredOperationId;
        }

        public void Complete(string operationKey, Guid operationId)
        {
            _ = operationKey;
            _ = operationId;
            CompleteStarted.Set();
            if (!ReleaseComplete.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Test did not release the completion operation.");
            }
        }
    }

    private sealed class CapturingToastService : IToastService
    {
        private readonly ObservableCollection<ToastMessage> _messages = [];

        public ReadOnlyObservableCollection<ToastMessage> Messages { get; }

        public CapturingToastService() => Messages = new ReadOnlyObservableCollection<ToastMessage>(_messages);

        public void Show(string message, ToastTone tone = ToastTone.Neutral, TimeSpan? duration = null)
        {
            _ = duration;
            _messages.Add(new ToastMessage(Guid.NewGuid(), message, tone));
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed >= timeout)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }

        return true;
    }

    private class UnusedTransactionProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException($"Unexpected transaction call: {targetMethod?.Name}.");
    }
}
