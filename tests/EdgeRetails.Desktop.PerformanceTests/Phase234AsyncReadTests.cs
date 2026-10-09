using System.Collections.ObjectModel;
using System.Reflection;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Desktop.ViewModels;

namespace EdgeRetails.Desktop.PerformanceTests;

public sealed class Phase234AsyncReadTests
{
    [Fact]
    public async Task Supplier_SearchCancelsPendingContinuationAndDiscardsItsStaleResult()
    {
        var service = DispatchProxy.Create<IBackendBusinessOperationsService, Reads>();
        var reads = (Reads)(object)service;
        using var vm = new SuppliersViewModel(new Toasts(), new DialogService(), new DrawerService(), service);
        reads.Requests[0].SetResult(Enumerable.Range(0, 100).Select(i => new SupplierDirectoryRecord
        { Id = Guid.NewGuid().ToString(), BackendId = Guid.NewGuid(), Name = $"Old {i:D3}" }).ToArray());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!vm.CanLoadMoreSuppliers)
        {
            await Task.Delay(10, timeout.Token);
        }
        vm.LoadMoreSuppliersCommand.Execute(null);
        await AwaitCount(reads, 2);
        vm.SearchText = "new";
        await AwaitCount(reads, 3);
        Assert.True(reads.Tokens[1].IsCancellationRequested);
        reads.Complete(2, true, "new");
        await AwaitName(vm, true, "new");
        reads.Complete(1, true, "stale continuation");
        await reads.Applied[1].Task;
        await Task.Delay(30);
        Assert.Equal("new", Assert.Single(vm.FilteredSuppliers).Name);
        Assert.False(vm.HasMoreSuppliers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Directory_InitialResponseCannotOverwriteNewerSearch(bool supplier)
    {
        var service = DispatchProxy.Create<IBackendBusinessOperationsService, Reads>();
        var reads = (Reads)(object)service;
        using var vm = Create(supplier, service);
        Assert.Single(reads.Requests);
        SetSearch(vm, "new");
        await AwaitCount(reads, 2);
        reads.Complete(1, supplier, "new");
        await AwaitName(vm, supplier, "new");
        reads.Complete(0, supplier, "old");
        await reads.Applied[0].Task;
        await Task.Delay(30);
        Assert.Equal("new", Name(vm, supplier));
        Assert.True(reads.Tokens[0].IsCancellationRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Directory_PostSaveRefreshIsNotDroppedDuringPendingRead(bool supplier)
    {
        var service = DispatchProxy.Create<IBackendBusinessOperationsService, Reads>();
        var reads = (Reads)(object)service;
        using var vm = Create(supplier, service);
        vm.GetType().GetMethod("RefreshAfterMutation", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, null);
        Assert.Equal(2, reads.Requests.Count);
        reads.Complete(1, supplier, "saved");
        await AwaitName(vm, supplier, "saved");
        reads.Requests[0].SetException(new InvalidOperationException("stale failure"));
        await reads.Applied[0].Task;
        await Task.Delay(30);
        Assert.Equal("saved", Name(vm, supplier));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Directory_DisposalInvalidatesPendingResult(bool supplier)
    {
        var service = DispatchProxy.Create<IBackendBusinessOperationsService, Reads>();
        var reads = (Reads)(object)service;
        var vm = Create(supplier, service);
        vm.Dispose();
        reads.Complete(0, supplier, "late");
        await reads.Applied[0].Task;
        await Task.Delay(30);
        Assert.Null(Name(vm, supplier));
        Assert.True(reads.Tokens[0].IsCancellationRequested);
    }

    private static IDisposable Create(bool supplier, IBackendBusinessOperationsService service) => supplier
        ? new SuppliersViewModel(new Toasts(), new DialogService(), new DrawerService(), service)
        : new CustomersViewModel(new Toasts(), new DialogService(), new DrawerService(), service);
    private static void SetSearch(IDisposable vm, string search) => vm.GetType().GetProperty("SearchText")!.SetValue(vm, search);
    private static string? Name(IDisposable vm, bool supplier) => supplier
        ? ((SuppliersViewModel)vm).FilteredSuppliers.SingleOrDefault()?.Name
        : ((CustomersViewModel)vm).FilteredCustomers.SingleOrDefault()?.Name;
    private static async Task AwaitName(IDisposable vm, bool supplier, string name)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (Name(vm, supplier) != name)
        {
            await Task.Delay(10, timeout.Token);
        }
    }
    private static async Task AwaitCount(Reads reads, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (reads.Requests.Count < count)
        {
            await Task.Delay(10, timeout.Token);
        }
    }
    public class Reads : DispatchProxy
    {
        public List<TaskCompletionSource<object>> Requests { get; } = [];
        public List<TaskCompletionSource> Applied { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var release = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Add(release);
            Applied.Add(applied);
            Tokens.Add(args!.OfType<CancellationToken>().Single());
            return targetMethod!.Name == "GetSuppliersAsync"
                ? Convert<SupplierDirectoryRecord>(release.Task, applied)
                : Convert<CustomerDirectoryRecord>(release.Task, applied);
        }
        private static async Task<IReadOnlyList<T>> Convert<T>(Task<object> source, TaskCompletionSource applied)
        {
            try
            {
                return (IReadOnlyList<T>)await source;
            }
            finally
            {
                applied.SetResult();
            }
        }
        public void Complete(int index, bool supplier, string name) => Requests[index].SetResult(supplier
            ? (object)new SupplierDirectoryRecord[] { new() { Id = Guid.NewGuid().ToString(), Name = name } }
            : new CustomerDirectoryRecord[] { new() { Id = Guid.NewGuid().ToString(), Name = name } });
    }
    private sealed class Toasts : IToastService
    {
        public ReadOnlyObservableCollection<ToastMessage> Messages { get; } = new(new ObservableCollection<ToastMessage>());
        public void Show(string message, ToastTone tone = ToastTone.Neutral, TimeSpan? duration = null) { }
    }
}
