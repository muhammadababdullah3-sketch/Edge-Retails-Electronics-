using System.Collections.ObjectModel;
using System.Reflection;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Desktop.ViewModels;
using EdgeRetails.Domain.Inventory;

namespace EdgeRetails.Desktop.PerformanceTests;

/// <summary>Frontend state tests only; these do not certify canonical ledger or restart safety.</summary>
public sealed class Phase1EditorSubmissionSafetyTests
{
    [Fact]
    public async Task Stock_OverlappingDirectInvocationsAndPendingEdits_DoNotReplaceSubmittedRequest()
    {
        var service = new StockService();
        var vm = Stock(service);
        var first = vm.ApplyAsync();
        Assert.True(vm.IsBusy);
        Assert.False(vm.CanEdit);
        vm.QuantityText = "99";
        vm.Note = "changed";
        await vm.ApplyAsync();
        Assert.Single(service.Requests);
        Assert.Equal("2", vm.QuantityText);
        Assert.Equal(2m, service.Requests[0].Quantity);
        service.Release.SetResult(Guid.NewGuid());
        await first;
        await vm.ApplyAsync();
        Assert.Single(service.Requests);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Stock_UnknownOutcomeRetainsSameIdentityAndPayloadAcrossSameDialogRetry()
    {
        var service = new StockService { FailFirst = true };
        var vm = Stock(service);
        await vm.ApplyAsync();
        Assert.False(vm.CanEdit);
        Assert.Contains("unresolved", vm.SubmissionStatus);
        vm.QuantityText = "99";
        vm.Note = "edited after loss";
        var retry = vm.ApplyAsync();
        Assert.Equal(2, service.Requests.Count);
        Assert.Equal(service.Requests[0], service.Requests[1]);
        service.Release.SetResult(Guid.NewGuid());
        await retry;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CustomerAndExpense_AtomicGateRejectsConcurrentDirectInvocation(bool expense)
    {
        var service = new BusinessService();
        object vm = expense
            ? new ExpenseEditViewModel(new Toasts(), () => { }, backendService: service, categories: ["General"])
                { AmountText = "10", Note = "original" }
            : new CustomerEditViewModel(new Toasts(), () => { }, backendService: service) { Name = "original" };
        var first = Save(vm);
        if (vm is CustomerEditViewModel customer)
        {
            customer.Name = "edited";
            Assert.Equal("original", customer.Name);
            Assert.True(customer.IsBusy);
        }
        else if (vm is ExpenseEditViewModel expenseVm)
        {
            expenseVm.Note = "edited";
            Assert.Equal("original", expenseVm.Note);
            Assert.True(expenseVm.IsBusy);
        }
        await Save(vm);
        Assert.Equal(1, service.Calls);
        Assert.Equal("original", service.Submitted);
        service.Release.SetResult();
        await first;
        await Save(vm);
        Assert.Equal(1, service.Calls);
    }

    [Fact]
    public async Task Expense_UnknownOutcomePreservesOriginalPayloadForRetry()
    {
        var service = new BusinessService { FailFirst = true };
        var vm = new ExpenseEditViewModel(new Toasts(), () => { }, backendService: service, categories: ["General"])
            { AmountText = "10", Note = "original" };
        await Save(vm);
        Assert.False(vm.CanEdit);
        Assert.Contains("unresolved", vm.SubmissionStatus);
        vm.Note = "replacement";
        vm.AmountText = "500";
        var retry = Save(vm);
        Assert.Equal("original", service.Submitted);
        Assert.Equal(10m, service.Amount);
        service.Release.SetResult();
        await retry;
    }

    private static Task Save(object vm) => (Task)vm.GetType()
        .GetMethod("SaveAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, null)!;

    private static StockAdjustmentViewModel Stock(StockService service) => new(
        new PosProductItemViewModel("P", "Product", "SKU", "Brand", "General", 10m, 20m,
            backendProductId: Guid.NewGuid(), backendProductUnitId: Guid.NewGuid()), new Toasts(), () => { }, service)
        { QuantityText = "2", Note = "original" };

    private sealed record StockRequest(Guid Id, decimal Quantity, bool Increase, StockAdjustmentReason Reason, string? Note);
    private sealed class StockService : IBackendStockAdjustmentService
    {
        public List<StockRequest> Requests { get; } = [];
        public TaskCompletionSource<Guid> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FailFirst { get; init; }
        public Task<Guid> CreateDeltaAdjustmentAsync(Guid productId, Guid? productUnitId, decimal quantity,
            bool increase, StockAdjustmentReason reason, string? note, Guid clientOperationId,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(new(clientOperationId, quantity, increase, reason, note));
            return FailFirst && Requests.Count == 1
                ? Task.FromException<Guid>(new DesktopApiException("network.timeout", "Unknown test outcome"))
                : Release.Task;
        }
    }

    private sealed class BusinessService : IBackendBusinessOperationsService
    {
        public int Calls { get; private set; }
        public string? Submitted { get; private set; }
        public decimal Amount { get; private set; }
        public bool FailFirst { get; init; }
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task SaveCustomerAsync(CustomerDirectoryRecord? existing, string name, string phone,
            string address, string notes, CancellationToken cancellationToken = default)
        {
            Calls++;
            Submitted = name;
            await Release.Task;
        }
        public async Task<ExpenseRecord> PostExpenseAsync(string category, string subcategory, decimal amount,
            DateTime date, string paymentMethod, string note, CancellationToken cancellationToken = default)
        {
            Calls++;
            Submitted = note;
            Amount = amount;
            if (FailFirst && Calls == 1)
            {
                throw new DesktopApiException("network.timeout", "Unknown test outcome");
            }
            await Release.Task;
            return new ExpenseRecord { Id = Guid.NewGuid().ToString("N") };
        }
        public Task<IReadOnlyList<string>> GetExpenseCategoriesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ExpenseRecord>> GetExpensesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CustomerDirectoryRecord>> GetCustomersAsync(string? search, int pageSize = 100, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SupplierDirectoryRecord>> GetSuppliersAsync(string? search, int pageSize = 100, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ReportSnapshot> GetReportAsync(ReportPeriodMode mode, DateTime selectedDate, int selectedMonth,
            int selectedYear, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class Toasts : IToastService
    {
        public ReadOnlyObservableCollection<ToastMessage> Messages { get; } = new(new ObservableCollection<ToastMessage>());
        public void Show(string message, ToastTone tone = ToastTone.Neutral, TimeSpan? duration = null) { }
    }
}
