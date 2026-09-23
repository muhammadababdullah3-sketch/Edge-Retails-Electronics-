using EdgeRetails.Desktop.Services;
using EdgeRetails.Desktop.ViewModels;

namespace EdgeRetails.Desktop.PerformanceTests;

public sealed class ReportsViewModelPerformanceTests
{
    [Fact]
    public async Task LaterReportCompletion_CannotOverwriteNewerReport()
    {
        var backend = new SequencedReportBackend();
        using var viewModel = new ReportsViewModel(backend);

        await backend.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.SelectedYear = viewModel.SelectedYear - 1;
        await backend.SecondRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        backend.SecondResult.TrySetResult(CreateSnapshot("B"));
        await WaitUntilAsync(() => viewModel.Snapshot.PeriodLabel == "B");

        backend.FirstResult.TrySetResult(CreateSnapshot("A"));
        await Task.Delay(300);

        Assert.Equal("B", viewModel.Snapshot.PeriodLabel);
        Assert.Equal("B", viewModel.PeriodLabel);
    }

    private static ReportSnapshot CreateSnapshot(string label) =>
        new()
        {
            PeriodLabel = label,
            ProfitSeriesLabel = "Net Profit",
            ShowExpenseSeries = true,
            Trend = Array.Empty<ReportTrendPoint>(),
            ExpenseBreakdown = Array.Empty<ReportExpenseBreakdownItem>(),
            ThakaActivity = Array.Empty<ReportThakaActivityItem>()
        };

    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Condition was not reached in time.");
            }

            await Task.Delay(20);
        }
    }

    private sealed class SequencedReportBackend : IBackendBusinessOperationsService
    {
        private int _requestNumber;

        public TaskCompletionSource<bool> FirstRequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> SecondRequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ReportSnapshot> FirstResult { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ReportSnapshot> SecondResult { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<string>> GetExpenseCategoriesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<ExpenseRecord>> GetExpensesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExpenseRecord>>([]);

        public Task<ExpenseRecord> PostExpenseAsync(
            string category,
            string subcategory,
            decimal amount,
            DateTime date,
            string paymentMethod,
            string note,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CustomerDirectoryRecord>> GetCustomersAsync(
            string? search,
            int pageSize = 100,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CustomerDirectoryRecord>>([]);

        public Task SaveCustomerAsync(
            CustomerDirectoryRecord? existing,
            string name,
            string phone,
            string address,
            string notes,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<SupplierDirectoryRecord>> GetSuppliersAsync(
            string? search,
            int pageSize = 100,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SupplierDirectoryRecord>>([]);

        public Task SaveSupplierAsync(
            SupplierDirectoryRecord? existing,
            string name,
            string phone,
            string city,
            string address,
            string notes,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<ReportSnapshot> GetReportAsync(
            ReportPeriodMode mode,
            DateTime selectedDate,
            int selectedMonth,
            int selectedYear,
            CancellationToken cancellationToken = default)
        {
            var request = Interlocked.Increment(ref _requestNumber);
            if (request == 1)
            {
                FirstRequestStarted.TrySetResult(true);
                return FirstResult.Task;
            }

            SecondRequestStarted.TrySetResult(true);
            return SecondResult.Task;
        }
    }
}

