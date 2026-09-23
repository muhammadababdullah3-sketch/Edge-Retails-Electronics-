using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Desktop.Navigation;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public enum SalesHistoryPeriod
{
    Today,
    Yesterday,
    ThisWeek,
    ThisMonth
}

public sealed class SalesHistoryViewModel : ViewModelBase
{
    private readonly INavigationService? _navigationService;
    private readonly IToastService? _toastService;
    private readonly IDrawerService? _drawerService;
    private readonly IDialogService? _dialogService;
    private readonly ITransactionService _transactionService;
    private readonly IBackendSalesHistoryService? _backendSalesHistoryService;

    private string _searchText = string.Empty;
    private SalesHistoryPeriod _selectedPeriod = SalesHistoryPeriod.Today;
    private SaleTransactionItemViewModel? _selectedSale;
    private SaleDetailViewModel? _currentSaleDetail;
    private readonly List<SaleTransactionItemViewModel> _allSales = [];
    private CancellationTokenSource? _historyRefreshCancellation;
    private long _historyRefreshVersion;
    private DateTimeOffset? _nextCompletedAt;
    private Guid? _nextSaleId;
    private bool _hasMore;
    private const int HistoryPageSize = 200;

    public SalesHistoryViewModel()
        : this(null, null, null, null, null, null, null)
    {
    }

    public SalesHistoryViewModel(
        ISessionContext? sessionContext = null,
        INavigationService? navigationService = null,
        IToastService? toastService = null,
        IDrawerService? drawerService = null,
        IDialogService? dialogService = null,
        ITransactionService? transactionService = null,
        IBackendSalesHistoryService? backendSalesHistoryService = null)
    {
        _navigationService = navigationService;
        _toastService = toastService;
        _drawerService = drawerService;
        _dialogService = dialogService;
        _transactionService = transactionService ?? DemoTransactionService.Instance;
        _backendSalesHistoryService = backendSalesHistoryService;

        CashierContext = sessionContext != null && !string.IsNullOrWhiteSpace(sessionContext.DisplayName)
            ? $"{sessionContext.DisplayName}, {sessionContext.RoleName}"
            : "Abdullah, Owner";

        FilteredSales = new ObservableCollection<SaleTransactionItemViewModel>();

        OpenPosCommand = new RelayCommand(NavigateToPos);
        SelectPeriodCommand = new RelayCommand<string>(SetPeriod);
        ViewInvoiceCommand = new RelayCommand<SaleTransactionItemViewModel>(OpenSaleDetail);
        CloseDetailCommand = new RelayCommand(CloseSaleDetail);
        LoadMoreCommand = new RelayCommand(() => _ = LoadMoreAsync(), () => HasMore);
        RefreshCommand = new RelayCommand(() => _ = RefreshFromTransactionServiceAsync());

        _ = RefreshFromTransactionServiceAsync();
    }

    public string CashierContext { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                ScheduleBackendRefresh();
            }
        }
    }

    public SalesHistoryPeriod SelectedPeriod
    {
        get => _selectedPeriod;
        set
        {
            if (SetProperty(ref _selectedPeriod, value))
            {
                OnPropertyChanged(nameof(IsTodaySelected));
                OnPropertyChanged(nameof(IsYesterdaySelected));
                OnPropertyChanged(nameof(IsThisWeekSelected));
                OnPropertyChanged(nameof(IsThisMonthSelected));
                OnPropertyChanged(nameof(SalesCountSubtitle));
                OnPropertyChanged(nameof(TotalSalesSubtitle));
                ScheduleBackendRefresh();
            }
        }
    }

    public bool IsTodaySelected => SelectedPeriod == SalesHistoryPeriod.Today;
    public bool IsYesterdaySelected => SelectedPeriod == SalesHistoryPeriod.Yesterday;
    public bool IsThisWeekSelected => SelectedPeriod == SalesHistoryPeriod.ThisWeek;
    public bool IsThisMonthSelected => SelectedPeriod == SalesHistoryPeriod.ThisMonth;

    public ObservableCollection<SaleTransactionItemViewModel> FilteredSales { get; }

    public SaleTransactionItemViewModel? SelectedSale
    {
        get => _selectedSale;
        set => SetProperty(ref _selectedSale, value);
    }

    public SaleDetailViewModel? CurrentSaleDetail
    {
        get => _currentSaleDetail;
        set
        {
            if (SetProperty(ref _currentSaleDetail, value))
            {
                OnPropertyChanged(nameof(IsDetailViewActive));
            }
        }
    }

    public bool IsDetailViewActive => CurrentSaleDetail != null;

    public int SalesCount { get; private set; }
    public string SalesCountDisplay => $"{SalesCount}";
    public string SalesCountSubtitle => SelectedPeriod switch
    {
        SalesHistoryPeriod.Today => "Today transactions",
        SalesHistoryPeriod.Yesterday => "Yesterday transactions",
        SalesHistoryPeriod.ThisWeek => "This week transactions",
        _ => "This month transactions"
    };

    public decimal TotalSalesAmount { get; private set; }
    public string TotalSalesDisplay => $"Rs. {TotalSalesAmount:N0}";
    public string TotalSalesSubtitle => SelectedPeriod switch
    {
        SalesHistoryPeriod.Today => "Gross revenue today",
        SalesHistoryPeriod.Yesterday => "Gross revenue yesterday",
        SalesHistoryPeriod.ThisWeek => "Gross revenue this week",
        _ => "Gross revenue this month"
    };

    public decimal ReturnsAmount { get; private set; }
    public string ReturnsDisplay => $"Rs. {ReturnsAmount:N0}";
    public string ReturnsSubtitle => "Items returned";

    public decimal NetSalesAmount { get; private set; }
    public string NetSalesDisplay => $"Rs. {NetSalesAmount:N0}";
    public string NetSalesSubtitle => "Gross sales less returns";

    public bool HasNoMatchingSales => FilteredSales.Count == 0;
    public bool HasMore => _hasMore;
    public bool IsBackendBoundedMode => _backendSalesHistoryService is not null;

    public ICommand OpenPosCommand { get; }
    public ICommand SelectPeriodCommand { get; }
    public ICommand ViewInvoiceCommand { get; }
    public ICommand CloseDetailCommand { get; }
    public ICommand LoadMoreCommand { get; }
    public ICommand RefreshCommand { get; }

    public void NavigateToPos()
    {
        _navigationService?.Navigate(NavigationTarget.POS);
    }

    public void SetPeriod(string? periodName)
    {
        if (Enum.TryParse<SalesHistoryPeriod>(periodName, true, out var parsed))
        {
            SelectedPeriod = parsed;
        }
    }

    public void OpenSaleDetail(SaleTransactionItemViewModel? sale)
    {
        if (sale == null)
        {
            return;
        }

        if (_backendSalesHistoryService is not null)
        {
            _ = OpenBackendSaleDetailAsync(sale);
            return;
        }

        SelectedSale = sale;
        CurrentSaleDetail = new SaleDetailViewModel(
            sale,
            _navigationService,
            _toastService,
            _drawerService,
            _dialogService,
            onBack: CloseSaleDetail,
            onSaleUpdated: ApplyFiltersAndRecalculate,
            transactionService: _transactionService);
    }

    private async Task OpenBackendSaleDetailAsync(SaleTransactionItemViewModel sale)
    {
        var invoice = sale.InvoiceDisplay;
        try
        {
            var record = await _transactionService.GetByInvoiceNumberAsync(invoice, CancellationToken.None);
            if (record is null)
            {
                _toastService?.Show($"Invoice {invoice} could not be loaded.", ToastTone.Danger);
                return;
            }

            var detail = ProjectRecord(record);
            SelectedSale = detail;
            CurrentSaleDetail = new SaleDetailViewModel(
                detail,
                _navigationService,
                _toastService,
                _drawerService,
                _dialogService,
                onBack: CloseSaleDetail,
                onSaleUpdated: ApplyFiltersAndRecalculate,
                transactionService: _transactionService);
        }
        catch (Exception ex)
        {
            _toastService?.Show($"Invoice {invoice} could not be loaded: {ex.Message}", ToastTone.Danger);
        }
    }

    public void CloseSaleDetail()
    {
        CurrentSaleDetail = null;
        ApplyFiltersAndRecalculate();
    }

    public void RefreshFromTransactionService()
    {
        _ = RefreshFromTransactionServiceAsync();
    }

    public async Task RefreshFromTransactionServiceAsync()
    {
        if (_backendSalesHistoryService is not null)
        {
            ScheduleBackendRefresh(immediate: true);
            return;
        }

        try
        {
            var records = await _transactionService.GetAllTransactionsAsync();
            _allSales.Clear();

            foreach (var record in records)
            {
                _allSales.Add(ProjectRecord(record));
            }

            ApplyFiltersAndRecalculate();
        }
        catch (Exception ex)
        {
            _toastService?.Show(
                $"Sales history could not be refreshed: {ex.Message}",
                ToastTone.Danger);
        }
    }

    private void ScheduleBackendRefresh(bool immediate = false)
    {
        if (_backendSalesHistoryService is null)
        {
            ApplyFiltersAndRecalculate();
            return;
        }

        var version = Interlocked.Increment(ref _historyRefreshVersion);
        var previous = Interlocked.Exchange(ref _historyRefreshCancellation, new CancellationTokenSource());
        previous?.Cancel();
        previous?.Dispose();
        var cts = _historyRefreshCancellation!;

        _nextCompletedAt = null;
        _nextSaleId = null;
        _hasMore = false;
        OnPropertyChanged(nameof(HasMore));

        if (immediate)
        {
            _ = RefreshBackendPageAsync(version, cts);
        }
        else
        {
            _ = RefreshBackendAfterDebounceAsync(version, cts);
        }
    }

    private async Task RefreshBackendAfterDebounceAsync(long version, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(250, cts.Token);
            if (version != Volatile.Read(ref _historyRefreshVersion))
            {
                return;
            }

            await RefreshBackendPageAsync(version, cts);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
    }

    private async Task RefreshBackendPageAsync(long version, CancellationTokenSource cts)
    {
        if (_backendSalesHistoryService is null)
        {
            return;
        }

        try
        {
            var page = await _backendSalesHistoryService.GetPageAsync(
                SelectedPeriod,
                SearchText,
                HistoryPageSize,
                _nextCompletedAt,
                _nextSaleId,
                cts.Token);

            if (cts.IsCancellationRequested || version != Volatile.Read(ref _historyRefreshVersion))
            {
                return;
            }

            var projected = page.Rows.Select(ProjectHistoryRow).ToArray();
            if (_nextCompletedAt is null && _nextSaleId is null)
            {
                FilteredSales.Clear();
            }

            foreach (var row in projected)
            {
                FilteredSales.Add(row);
            }

            _nextCompletedAt = page.NextCompletedAt;
            _nextSaleId = page.NextSaleId;
            _hasMore = page.HasMore;
            RecalculateFromRows();
            OnPropertyChanged(nameof(HasMore));
            if (LoadMoreCommand is RelayCommand loadMore)
            {
                loadMore.NotifyCanExecuteChanged();
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (version == Volatile.Read(ref _historyRefreshVersion))
        {
            _toastService?.Show($"Sales history could not be refreshed: {ex.Message}", ToastTone.Danger);
        }
    }

    private SaleTransactionItemViewModel ProjectHistoryRow(SalesHistoryRowDto row)
    {
        var invoiceNumber = int.TryParse(row.InvoiceNumber.TrimStart('#'), out var parsed) ? parsed : 0;
        var returnState = row.ReturnedAmount <= 0m
            ? SaleReturnState.None
            : row.ReturnedAmount >= row.GrandTotal
                ? SaleReturnState.Refunded
                : SaleReturnState.Partial;
        return new SaleTransactionItemViewModel(
            invoiceNumber,
            row.CompletedAt.LocalDateTime,
            row.CustomerName,
            string.Empty,
            "Backend User",
            row.PaymentMethod.ToString(),
            Array.Empty<SaleLineItemViewModel>(),
            paymentState: PaymentState.Paid,
            returnState: returnState,
            totalReturnedAmount: row.ReturnedAmount,
            invoiceDisplayOverride: row.InvoiceNumber,
            itemCountOverride: row.ItemCount,
            totalAmountOverride: row.GrandTotal);
    }

    private void RecalculateFromRows()
    {
        SalesCount = FilteredSales.Count;
        TotalSalesAmount = FilteredSales.Sum(s => s.TotalAmount);
        ReturnsAmount = FilteredSales.Sum(s => s.TotalReturnedAmount);
        NetSalesAmount = Math.Max(0m, TotalSalesAmount - ReturnsAmount);
        OnPropertyChanged(nameof(SalesCount));
        OnPropertyChanged(nameof(SalesCountDisplay));
        OnPropertyChanged(nameof(TotalSalesAmount));
        OnPropertyChanged(nameof(TotalSalesDisplay));
        OnPropertyChanged(nameof(ReturnsAmount));
        OnPropertyChanged(nameof(ReturnsDisplay));
        OnPropertyChanged(nameof(NetSalesAmount));
        OnPropertyChanged(nameof(NetSalesDisplay));
        OnPropertyChanged(nameof(HasNoMatchingSales));
    }

    public async Task LoadMoreAsync()
    {
        if (_backendSalesHistoryService is null || !_hasMore)
        {
            return;
        }

        var version = Volatile.Read(ref _historyRefreshVersion);
        var cts = _historyRefreshCancellation ?? new CancellationTokenSource();
        await RefreshBackendPageAsync(version, cts);
    }

    public void ApplyFiltersAndRecalculate()
    {
        var referenceDate = DateTime.Today;
        var query = _allSales.AsEnumerable();

        query = SelectedPeriod switch
        {
            SalesHistoryPeriod.Today =>
                query.Where(s => s.TransactionDate.Date == referenceDate),
            SalesHistoryPeriod.Yesterday =>
                query.Where(s => s.TransactionDate.Date == referenceDate.AddDays(-1)),
            SalesHistoryPeriod.ThisWeek =>
                query.Where(s => s.TransactionDate.Date >= StartOfWeek(referenceDate) &&
                                 s.TransactionDate.Date <= referenceDate),
            SalesHistoryPeriod.ThisMonth =>
                query.Where(s => s.TransactionDate.Year == referenceDate.Year &&
                                 s.TransactionDate.Month == referenceDate.Month),
            _ => query
        };

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var term = SearchText.Trim();
            query = query.Where(s =>
                s.InvoiceDisplay.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                s.CustomerName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                s.CustomerPhone.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                s.PaymentMethod.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                s.Status.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        var results = query
            .OrderByDescending(s => s.TransactionDate)
            .ThenByDescending(s => s.InvoiceDisplay, StringComparer.OrdinalIgnoreCase)
            .ToList();

        FilteredSales.Clear();
        foreach (var sale in results)
        {
            FilteredSales.Add(sale);
        }

        SalesCount = results.Count;
        TotalSalesAmount = results.Sum(s => s.TotalAmount);
        ReturnsAmount = results.Sum(s => s.TotalReturnedAmount);
        NetSalesAmount = Math.Max(0m, TotalSalesAmount - ReturnsAmount);

        OnPropertyChanged(nameof(SalesCount));
        OnPropertyChanged(nameof(SalesCountDisplay));
        OnPropertyChanged(nameof(TotalSalesAmount));
        OnPropertyChanged(nameof(TotalSalesDisplay));
        OnPropertyChanged(nameof(ReturnsAmount));
        OnPropertyChanged(nameof(ReturnsDisplay));
        OnPropertyChanged(nameof(NetSalesAmount));
        OnPropertyChanged(nameof(NetSalesDisplay));
        OnPropertyChanged(nameof(HasNoMatchingSales));
    }

    private SaleTransactionItemViewModel ProjectRecord(SaleTransactionRecord record)
    {
        var returns = _transactionService.GetReturnsForInvoice(record.InvoiceNumber);
        var returnedByItem = returns
            .SelectMany(r => r.Items)
            .GroupBy(i => (i.ProductId, i.Sku))
            .ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity));

        var lines = record.Items
            .Select((item, index) =>
            {
                returnedByItem.TryGetValue((item.ProductId, item.Sku), out var returnedQuantity);
                return new SaleLineItemViewModel(
                    index + 1,
                    item.ProductId,
                    item.ProductName,
                    item.Sku,
                    item.Brand,
                    item.UnitPrice,
                    item.Quantity,
                    item.Discount,
                    returnedQuantity);
            })
            .ToArray();

        var totalReturned = returns.Sum(r => r.TotalRefundAmount);
        var hasReturn = totalReturned > 0m;
        var allReturned = lines.Length > 0 && lines.All(line => line.EligibleReturnQuantity <= 0m);
        var returnState = !hasReturn
            ? SaleReturnState.None
            : allReturned
                ? SaleReturnState.Refunded
                : SaleReturnState.Partial;

        var invoiceNumber = int.TryParse(record.InvoiceNumber.TrimStart('#'), out var parsed)
            ? parsed
            : 0;

        return new SaleTransactionItemViewModel(
            invoiceNumber,
            record.Timestamp,
            record.CustomerName,
            record.CustomerPhone,
            record.CashierName,
            record.PaymentMethodDisplay,
            lines,
            discountAmount: record.DiscountAmount,
            paymentReceived: record.AmountReceived,
            paymentState: record.PaymentState,
            returnState: returnState,
            totalReturnedAmount: totalReturned,
            invoiceDisplayOverride: record.InvoiceNumber.StartsWith('#')
                ? null
                : record.InvoiceNumber);
    }

    private static DateTime StartOfWeek(DateTime date)
    {
        var diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
        return date.AddDays(-diff).Date;
    }
}
