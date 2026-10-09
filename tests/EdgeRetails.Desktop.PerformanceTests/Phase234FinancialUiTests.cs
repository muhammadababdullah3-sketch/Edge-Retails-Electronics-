using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Desktop.ViewModels;
using Xunit;

namespace EdgeRetails.Desktop.PerformanceTests;

public sealed class Phase234FinancialUiTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public void DecimalMoneyRemainsVisibleInCheckoutCartAndProduct(string cultureName)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            var expected = 100.50m.ToString("N2", CultureInfo.CurrentCulture);
            var checkout = new CompleteSaleViewModel(100.50m);
            var product = new PosProductItemViewModel("P", "Product", "SKU", "Brand", "Category", 10m, 100.50m);
            var cart = new PosCartItemViewModel(product);
            Assert.Equal($"Rs. {expected}", checkout.TotalToPayDisplay);
            Assert.Equal($"Rs. {expected}", product.PriceDisplay);
            Assert.Equal($"Rs. {expected}", cart.LineTotalDisplay);
            checkout.SelectBankCommand.Execute(null);
            checkout.AmountReceivedText = "100";
            Assert.Contains(expected, checkout.ValidationMessage);
            Assert.Equal(100.50m, checkout.TotalToPay);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public async Task PostedExpenseIsReadOnlyEvenWhenSaveMethodInvokedDirectly()
    {
        var service = new ControlledService();
        var expense = new ExpenseRecord { Id = "EXP", BackendId = Guid.NewGuid(), Amount = 10.25m, Note = "original" };
        var vm = new ExpenseEditViewModel(new Toasts(), () => { }, expense, backendService: service);
        Assert.True(vm.IsReadOnly);
        Assert.False(vm.CanEdit);
        Assert.False(vm.SaveCommand.CanExecute(null));
        vm.Note = "replacement";
        Assert.Equal("original", vm.Note);
        await (Task)typeof(ExpenseEditViewModel).GetMethod("SaveAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null)!;
        Assert.Equal(0, service.PostCalls);
        Assert.Contains("audit", vm.SubmissionStatus);
    }

    [Fact]
    public async Task ReportFirstFailureIsUnavailableRatherThanZero()
    {
        var service = new ControlledService();
        using var vm = new ReportsViewModel(service);
        Assert.Equal(ReportLoadState.Loading, vm.LoadState);
        Assert.Equal("Unavailable", vm.NetSalesDisplay);
        service.Reports[0].SetException(new IOException("controlled unavailable"));
        await WaitFor(() => vm.LoadState == ReportLoadState.Unavailable);
        Assert.False(vm.HasReportData);
        Assert.Empty(vm.AverageMetrics);
        Assert.Contains("unavailable", vm.ReportStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Unavailable", vm.NetProfitDisplay);
    }

    [Fact]
    public async Task FailedRefreshRetainsOriginalPeriodAndMarksSnapshotStale()
    {
        var service = new ControlledService();
        using var vm = new ReportsViewModel(service);
        service.Reports[0].SetResult(Snapshot("Original period", 100.50m));
        await WaitFor(() => vm.LoadState == ReportLoadState.Loaded);
        vm.SelectedYear -= 1;
        service.Reports[1].SetException(new IOException("controlled failure"));
        await WaitFor(() => vm.LoadState == ReportLoadState.Stale);
        Assert.True(vm.HasReportData);
        Assert.Equal("Original period", vm.PeriodLabel);
        Assert.Contains("STALE", vm.ReportStatus);
        Assert.Contains("Original period", vm.ReportStatus);
        Assert.Contains("fetched", vm.ReportStatus);
        Assert.Equal($"Rs. {100.50m:N2}", vm.NetSalesDisplay);
    }

    [Fact]
    public async Task SupersededFailureCannotMarkNewestReportStale()
    {
        var service = new ControlledService();
        using var vm = new ReportsViewModel(service);
        vm.SelectedYear -= 1;
        service.Reports[1].SetResult(Snapshot("Newest period", 20.25m));
        await WaitFor(() => vm.LoadState == ReportLoadState.Loaded);
        service.Reports[0].SetException(new IOException("old failure"));
        await Task.Delay(30);
        Assert.Equal(ReportLoadState.Loaded, vm.LoadState);
        Assert.Equal("Newest period", vm.PeriodLabel);
    }

    private static ReportSnapshot Snapshot(string period, decimal amount) => new()
    {
        Mode = ReportPeriodMode.Monthly, PeriodLabel = period, NetSales = amount,
        ProfitSeriesLabel = "Net Profit", Trend = [], ExpenseBreakdown = [], ThakaActivity = []
    };
    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) { await Task.Delay(10); }
        Assert.True(condition());
    }
    private sealed class ControlledService : IBackendBusinessOperationsService
    {
        public List<TaskCompletionSource<ReportSnapshot>> Reports { get; } = [];
        public int PostCalls { get; private set; }
        public Task<ReportSnapshot> GetReportAsync(ReportPeriodMode mode, DateTime selectedDate, int selectedMonth, int selectedYear, CancellationToken cancellationToken = default)
        {
            var response = new TaskCompletionSource<ReportSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            Reports.Add(response);
            return response.Task;
        }
        public Task<ExpenseRecord> PostExpenseAsync(string category, string subcategory, decimal amount, DateTime date, string paymentMethod, string note, CancellationToken cancellationToken = default)
        { PostCalls++; throw new NotSupportedException(); }
        public Task SaveCustomerAsync(CustomerDirectoryRecord? existing, string name, string phone, string address, string notes, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetExpenseCategoriesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ExpenseRecord>> GetExpensesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CustomerDirectoryRecord>> GetCustomersAsync(string? search, int pageSize = 100, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SupplierDirectoryRecord>> GetSuppliersAsync(string? search, int pageSize = 100, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class Toasts : IToastService
    {
        public ReadOnlyObservableCollection<ToastMessage> Messages { get; } = new(new ObservableCollection<ToastMessage>());
        public void Show(string message, ToastTone tone = ToastTone.Neutral, TimeSpan? duration = null) { }
    }
}
