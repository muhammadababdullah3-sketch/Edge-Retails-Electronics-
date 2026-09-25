using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class ReportMonthOption
{
    public required int Number { get; init; }
    public required string Name { get; init; }

    public override string ToString() => Name;
}

public sealed class ReportAverageMetric
{
    public required string Label { get; init; }
    public required string Value { get; init; }
}

public sealed class ReportsViewModel : ViewModelBase, IDisposable
{
    private readonly DemoReportingService _reportingService = DemoReportingService.Instance;
    private readonly IBackendBusinessOperationsService? _backendService;
    private readonly IToastService? _toastService;

    private ReportPeriodMode _mode = ReportPeriodMode.Monthly;
    private DateTime _selectedDate = DateTime.Today;
    private ReportMonthOption _selectedMonth;
    private int _selectedYear;
    private ReportSnapshot _snapshot;
    private CancellationTokenSource? _refreshCancellation;
    private long _refreshVersion;

    public ReportsViewModel(
        IBackendBusinessOperationsService? backendService = null,
        IToastService? toastService = null)
    {
        _backendService = backendService;
        _toastService = toastService;
        Months = [.. Enumerable.Range(1, 12)
            .Select(month => new ReportMonthOption
            {
                Number = month,
                Name = CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(month)
            })];

        var currentYear = DateTime.Today.Year;
        Years = [.. Enumerable.Range(currentYear - 4, 5).Reverse()];

        _selectedMonth = Months.First(month => month.Number == DateTime.Today.Month);
        _selectedYear = currentYear;
        _snapshot = _backendService is null
            ? _reportingService.GetSnapshot(
                _mode,
                _selectedDate,
                _selectedMonth.Number,
                _selectedYear)
            : CreateEmptySnapshot(_mode, "Loading…");

        Trend = [];
        ExpenseBreakdown = [];
        ThakaActivity = [];
        AverageMetrics = [];

        SelectModeCommand = new RelayCommand<string>(SelectMode);
        RefreshCommand = new RelayCommand(Refresh);

        if (_backendService is null)
        {
            _reportingService.StateChanged += OnReportingStateChanged;
            ApplySnapshot(_snapshot);
        }
        else
        {
            ApplySnapshot(_snapshot);
            _ = RefreshBackendAsync();
        }
    }

    public void Dispose()
    {
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = null;

        if (_backendService is null)
        {
            _reportingService.StateChanged -= OnReportingStateChanged;
        }
    }

    public IReadOnlyList<ReportMonthOption> Months { get; }
    public IReadOnlyList<int> Years { get; }

    public ObservableCollection<ReportTrendPoint> Trend { get; }
    public ObservableCollection<ReportExpenseBreakdownItem> ExpenseBreakdown { get; }
    public ObservableCollection<ReportThakaActivityItem> ThakaActivity { get; }
    public ObservableCollection<ReportAverageMetric> AverageMetrics { get; }

    public ReportPeriodMode Mode
    {
        get => _mode;
        private set
        {
            if (SetProperty(ref _mode, value))
            {
                OnPropertyChanged(nameof(IsDaily));
                OnPropertyChanged(nameof(IsMonthly));
                OnPropertyChanged(nameof(IsYearly));
                OnPropertyChanged(nameof(IsDateSelectorVisible));
                OnPropertyChanged(nameof(IsMonthSelectorVisible));
                OnPropertyChanged(nameof(IsExpenseBreakdownVisible));
                OnPropertyChanged(nameof(IsThakaActivityVisible));
                OnPropertyChanged(nameof(ShowSecondaryKpis));
                Refresh();
            }
        }
    }

    public bool IsDaily => Mode == ReportPeriodMode.Daily;
    public bool IsMonthly => Mode == ReportPeriodMode.Monthly;
    public bool IsYearly => Mode == ReportPeriodMode.Yearly;
    public bool IsDateSelectorVisible => IsDaily;
    public bool IsMonthSelectorVisible => IsMonthly;
    public bool IsExpenseBreakdownVisible => IsMonthly;
    public bool IsThakaActivityVisible => IsMonthly;
    public bool ShowSecondaryKpis => !IsYearly;
    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public DateTime SelectedDate
    {
        get => _selectedDate;
        set
        {
            if (SetProperty(ref _selectedDate, value.Date))
            {
                Refresh();
            }
        }
    }

    public ReportMonthOption SelectedMonth
    {
        get => _selectedMonth;
        set
        {
            if (value is not null && SetProperty(ref _selectedMonth, value))
            {
                Refresh();
            }
        }
    }

    public int SelectedYear
    {
        get => _selectedYear;
        set
        {
            if (SetProperty(ref _selectedYear, value))
            {
                Refresh();
            }
        }
    }

    public ReportSnapshot Snapshot
    {
        get => _snapshot;
        private set => SetProperty(ref _snapshot, value);
    }

    public string PeriodLabel => Snapshot.PeriodLabel;

    public string NetSalesDisplay => Currency(Snapshot.NetSales);
    public string GrossProfitDisplay => Currency(Snapshot.GrossProfit);
    public string ExpensesDisplay => Currency(Snapshot.Expenses);
    public string NetProfitDisplay => Currency(Snapshot.NetProfit);
    public string PurchasesDisplay => Currency(Snapshot.Purchases);
    public string ThakaMaterialDisplay => Currency(Snapshot.ThakaMaterial);

    public string ProfitSeriesLabel => Snapshot.ProfitSeriesLabel;
    public bool ShowExpenseSeries => Snapshot.ShowExpenseSeries;

    public string ChartTitle => Mode switch
    {
        ReportPeriodMode.Daily => "Hourly Sales & Gross Profit",
        ReportPeriodMode.Monthly => "Daily Sales, Net Profit & Expenses",
        ReportPeriodMode.Yearly => "Monthly Sales, Net Profit & Expenses",
        _ => "Performance"
    };

    public string ChartSubtitle => Mode switch
    {
        ReportPeriodMode.Daily => "Hourly operating trend for the selected day",
        ReportPeriodMode.Monthly => "Calendar days are zero-filled so averages stay honest",
        ReportPeriodMode.Yearly => "January to December performance trend",
        _ => string.Empty
    };

    public ICommand SelectModeCommand { get; }
    public ICommand RefreshCommand { get; }

    private void SelectMode(string? mode)
    {
        if (Enum.TryParse<ReportPeriodMode>(mode, ignoreCase: true, out var parsed))
        {
            Mode = parsed;
        }
    }

    private void OnReportingStateChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        if (_backendService is null)
        {
            Snapshot = _reportingService.GetSnapshot(
                Mode,
                SelectedDate,
                SelectedMonth.Number,
                SelectedYear);
            ApplySnapshot(Snapshot);
            return;
        }

        _ = RefreshBackendAsync();
    }

    private async Task RefreshBackendAsync()
    {
        if (_backendService is null)
        {
            return;
        }

        var requestVersion = Interlocked.Increment(ref _refreshVersion);
        var previous = _refreshCancellation;
        _refreshCancellation = new CancellationTokenSource();
        previous?.Cancel();
        previous?.Dispose();

        var cancellationToken = _refreshCancellation.Token;
        var mode = Mode;
        var selectedDate = SelectedDate;
        var selectedMonth = SelectedMonth.Number;
        var selectedYear = SelectedYear;

        IsLoading = true;
        try
        {
            var snapshot = await _backendService.GetReportAsync(
                mode,
                selectedDate,
                selectedMonth,
                selectedYear,
                cancellationToken);

            if (cancellationToken.IsCancellationRequested ||
                requestVersion != Volatile.Read(ref _refreshVersion))
            {
                return;
            }

            Snapshot = snapshot;
            ApplySnapshot(snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            if (requestVersion == Volatile.Read(ref _refreshVersion))
            {
                _toastService?.Show(
                    $"Reports could not be refreshed: {ex.Message}",
                    ToastTone.Danger);
            }
        }
        finally
        {
            if (requestVersion == Volatile.Read(ref _refreshVersion))
            {
                IsLoading = false;
            }
        }
    }

    private void ApplySnapshot(ReportSnapshot snapshot)
    {
        ReplaceCollection(Trend, snapshot.Trend);
        ReplaceCollection(ExpenseBreakdown, snapshot.ExpenseBreakdown);
        ReplaceCollection(ThakaActivity, snapshot.ThakaActivity);

        AverageMetrics.Clear();
        foreach (var metric in BuildAverageMetrics(snapshot))
        {
            AverageMetrics.Add(metric);
        }

        OnPropertyChanged(nameof(PeriodLabel));
        OnPropertyChanged(nameof(NetSalesDisplay));
        OnPropertyChanged(nameof(GrossProfitDisplay));
        OnPropertyChanged(nameof(ExpensesDisplay));
        OnPropertyChanged(nameof(NetProfitDisplay));
        OnPropertyChanged(nameof(PurchasesDisplay));
        OnPropertyChanged(nameof(ThakaMaterialDisplay));
        OnPropertyChanged(nameof(ProfitSeriesLabel));
        OnPropertyChanged(nameof(ShowExpenseSeries));
        OnPropertyChanged(nameof(ChartTitle));
        OnPropertyChanged(nameof(ChartSubtitle));
    }

    private IEnumerable<ReportAverageMetric> BuildAverageMetrics(ReportSnapshot snapshot)
    {
        if (snapshot.Mode == ReportPeriodMode.Daily)
        {
            yield return new ReportAverageMetric
            {
                Label = "Average Invoice",
                Value = Currency(snapshot.AverageInvoiceValue)
            };
            yield return new ReportAverageMetric
            {
                Label = "Avg Gross Profit / Invoice",
                Value = Currency(snapshot.AverageGrossProfitPerInvoice)
            };
            yield return new ReportAverageMetric
            {
                Label = "Sales Count",
                Value = snapshot.SalesCount.ToString("N0")
            };
            yield return new ReportAverageMetric
            {
                Label = "Avg Items / Invoice",
                Value = snapshot.AverageItemsPerInvoice.ToString("0.##")
            };
            yield break;
        }

        if (snapshot.Mode == ReportPeriodMode.Monthly)
        {
            yield return new ReportAverageMetric
            {
                Label = "Average Daily Sales",
                Value = Currency(snapshot.AverageDailySales)
            };
            yield return new ReportAverageMetric
            {
                Label = "Average Daily Net Profit",
                Value = Currency(snapshot.AverageDailyNetProfit)
            };
            yield return new ReportAverageMetric
            {
                Label = "Average Daily Expenses",
                Value = Currency(snapshot.AverageDailyExpenses)
            };
            yield return new ReportAverageMetric
            {
                Label = "Average Invoice",
                Value = Currency(snapshot.AverageInvoiceValue)
            };
            yield break;
        }

        yield return new ReportAverageMetric
        {
            Label = "Average Monthly Sales",
            Value = Currency(snapshot.AverageMonthlySales)
        };
        yield return new ReportAverageMetric
        {
            Label = "Average Monthly Net Profit",
            Value = Currency(snapshot.AverageMonthlyNetProfit)
        };
        yield return new ReportAverageMetric
        {
            Label = "Average Monthly Expenses",
            Value = Currency(snapshot.AverageMonthlyExpenses)
        };
        yield return new ReportAverageMetric
        {
            Label = "Sales Count",
            Value = snapshot.SalesCount.ToString("N0")
        };
    }

    private static ReportSnapshot CreateEmptySnapshot(
        ReportPeriodMode mode,
        string periodLabel) =>
        new()
        {
            Mode = mode,
            PeriodLabel = periodLabel,
            ProfitSeriesLabel = mode == ReportPeriodMode.Daily
                ? "Gross Profit"
                : "Net Profit",
            ShowExpenseSeries = mode != ReportPeriodMode.Daily,
            Trend = Array.Empty<ReportTrendPoint>(),
            ExpenseBreakdown = Array.Empty<ReportExpenseBreakdownItem>(),
            ThakaActivity = Array.Empty<ReportThakaActivityItem>()
        };

    private static void ReplaceCollection<T>(
        ObservableCollection<T> target,
        IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }

    private static string Currency(decimal amount) => $"Rs. {amount:N0}";
}
