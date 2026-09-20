using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
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

    private string _searchText = string.Empty;
    private SalesHistoryPeriod _selectedPeriod = SalesHistoryPeriod.Today;
    private SaleTransactionItemViewModel? _selectedSale;
    private SaleDetailViewModel? _currentSaleDetail;
    private readonly List<SaleTransactionItemViewModel> _allSales = [];

    public SalesHistoryViewModel()
        : this(null, null, null, null, null, null)
    {
    }

    public SalesHistoryViewModel(
        ISessionContext? sessionContext = null,
        INavigationService? navigationService = null,
        IToastService? toastService = null,
        IDrawerService? drawerService = null,
        IDialogService? dialogService = null,
        ITransactionService? transactionService = null)
    {
        _navigationService = navigationService;
        _toastService = toastService;
        _drawerService = drawerService;
        _dialogService = dialogService;
        _transactionService = transactionService ?? DemoTransactionService.Instance;

        CashierContext = sessionContext != null && !string.IsNullOrWhiteSpace(sessionContext.DisplayName)
            ? $"{sessionContext.DisplayName}, {sessionContext.RoleName}"
            : "Abdullah, Owner";

        FilteredSales = new ObservableCollection<SaleTransactionItemViewModel>();

        NewSaleCommand = new RelayCommand(NavigateToNewSale);
        SelectPeriodCommand = new RelayCommand<string>(SetPeriod);
        ViewInvoiceCommand = new RelayCommand<SaleTransactionItemViewModel>(OpenSaleDetail);
        CloseDetailCommand = new RelayCommand(CloseSaleDetail);
        RefreshCommand = new RelayCommand(RefreshFromTransactionService);

        RefreshFromTransactionService();
    }

    public string CashierContext { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ApplyFiltersAndRecalculate();
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
                ApplyFiltersAndRecalculate();
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

    public ICommand NewSaleCommand { get; }
    public ICommand SelectPeriodCommand { get; }
    public ICommand ViewInvoiceCommand { get; }
    public ICommand CloseDetailCommand { get; }
    public ICommand RefreshCommand { get; }

    public void NavigateToNewSale()
    {
        _navigationService?.Navigate(NavigationTarget.NewSale);
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

    public void CloseSaleDetail()
    {
        CurrentSaleDetail = null;
        ApplyFiltersAndRecalculate();
    }

    public void RefreshFromTransactionService()
    {
        _allSales.Clear();

        foreach (var record in _transactionService.GetAllTransactions())
        {
            _allSales.Add(ProjectRecord(record));
        }

        ApplyFiltersAndRecalculate();
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
                s.InvoiceNumber.ToString(CultureInfo.InvariantCulture).Contains(term, StringComparison.OrdinalIgnoreCase) ||
                s.CustomerName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                s.CustomerPhone.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                s.PaymentMethod.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                s.Status.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        var results = query
            .OrderByDescending(s => s.TransactionDate)
            .ThenByDescending(s => s.InvoiceNumber)
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
            totalReturnedAmount: totalReturned);
    }

    private static DateTime StartOfWeek(DateTime date)
    {
        var diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
        return date.AddDays(-diff).Date;
    }
}
