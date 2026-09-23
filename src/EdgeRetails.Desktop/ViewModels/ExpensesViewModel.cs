using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class ExpenseEditViewModel : ViewModelBase
{
    private readonly DemoBusinessDirectoryService _service = DemoBusinessDirectoryService.Instance;
    private readonly IBackendBusinessOperationsService? _backendService;
    private readonly IReadOnlyList<string> _categories;
    private readonly ExpenseRecord? _existing;
    private readonly IToastService _toastService;
    private readonly Action _close;
    private readonly Action? _saved;

    private string _selectedCategory;
    private string _subcategory;
    private string _amountText;
    private DateTime _date;
    private string _selectedPaymentMethod;
    private string _staffMember;
    private string _note;

    public ExpenseEditViewModel(
        IToastService toastService,
        Action close,
        ExpenseRecord? existing = null,
        Action? saved = null,
        IBackendBusinessOperationsService? backendService = null,
        IReadOnlyList<string>? categories = null)
    {
        _toastService = toastService;
        _close = close;
        _existing = existing;
        _saved = saved;
        _backendService = backendService;
        _categories = categories ?? _service.ExpenseCategories;

        _selectedCategory = existing?.Category ?? _categories.FirstOrDefault() ?? string.Empty;
        _subcategory = existing?.Subcategory ?? string.Empty;
        _amountText = existing?.Amount.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty;
        _date = existing?.Date ?? DateTime.Today;
        _selectedPaymentMethod = existing?.PaymentMethod ?? _service.PaymentMethods[0];
        _staffMember = existing?.StaffMember ?? string.Empty;
        _note = existing?.Note ?? string.Empty;

        SaveCommand = new RelayCommand(async () => await SaveAsync());
        CancelCommand = new RelayCommand(_close);
    }

    public string Title => _existing is null ? "Add Expense" : "Edit Expense";
    public string SaveButtonText => _existing is null ? "Add Expense" : "Save Changes";
    public IReadOnlyList<string> Categories => _categories;
    public IReadOnlyList<string> PaymentMethods => _service.PaymentMethods;

    public string SelectedCategory
    {
        get => _selectedCategory;
        set => SetProperty(ref _selectedCategory, value ?? string.Empty);
    }

    public string Subcategory
    {
        get => _subcategory;
        set => SetProperty(ref _subcategory, value ?? string.Empty);
    }

    public string AmountText
    {
        get => _amountText;
        set => SetProperty(ref _amountText, value ?? string.Empty);
    }

    public DateTime Date
    {
        get => _date;
        set => SetProperty(ref _date, value);
    }

    public string SelectedPaymentMethod
    {
        get => _selectedPaymentMethod;
        set => SetProperty(ref _selectedPaymentMethod, value ?? string.Empty);
    }

    public string StaffMember
    {
        get => _staffMember;
        set => SetProperty(ref _staffMember, value ?? string.Empty);
    }

    public string Note
    {
        get => _note;
        set => SetProperty(ref _note, value ?? string.Empty);
    }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    private async Task SaveAsync()
    {
        if (!decimal.TryParse(AmountText, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) &&
            !decimal.TryParse(AmountText, NumberStyles.Number, CultureInfo.CurrentCulture, out amount))
        {
            _toastService.Show("Expense amount must be a valid number.", ToastTone.Warning);
            return;
        }

        try
        {
            if (_backendService is null)
            {
                _service.SaveExpense(
                    _existing,
                    SelectedCategory,
                    Subcategory,
                    amount,
                    Date,
                    SelectedPaymentMethod,
                    StaffMember,
                    Note);
            }
            else
            {
                if (_existing?.BackendId is not null)
                {
                    throw new InvalidOperationException(
                        "Posted expenses are immutable. Use a void/correction flow instead of editing in place.");
                }

                await _backendService.PostExpenseAsync(
                    SelectedCategory,
                    Subcategory,
                    amount,
                    Date,
                    SelectedPaymentMethod,
                    Note);
            }

            _toastService.Show(
                _existing is null ? "Expense added." : "Expense updated.",
                ToastTone.Success);
            _close();
            _saved?.Invoke();
        }
        catch (Exception ex)
        {
            _toastService.Show(ex.Message, ToastTone.Danger);
        }
    }
}

public sealed class ExpensesViewModel : ViewModelBase, IDisposable
{
    private readonly DemoBusinessDirectoryService _service = DemoBusinessDirectoryService.Instance;
    private readonly IBackendBusinessOperationsService? _backendService;
    private readonly List<ExpenseRecord> _backendExpenses = [];
    private bool _backendLoaded;
    private bool _backendLoading;
    private readonly IToastService _toastService;
    private readonly IDialogService _dialogService;
    private string _selectedPeriod = "Today";
    private string _selectedCategory = "All";

    public ExpensesViewModel(
        IToastService toastService,
        IDialogService dialogService,
        IBackendBusinessOperationsService? backendService = null)
    {
        _toastService = toastService;
        _dialogService = dialogService;
        _backendService = backendService;

        FilteredExpenses = [];
        Categories = backendService is null
            ? new ObservableCollection<string>(["All", .. _service.ExpenseCategories])
            : new ObservableCollection<string>(["All"]);

        AddExpenseCommand = new RelayCommand(() => OpenExpenseDialog(null));
        EditExpenseCommand = new RelayCommand<ExpenseRecord>(OpenExpenseDialog);
        SelectPeriodCommand = new RelayCommand<string>(SelectPeriod);

        if (_backendService is null)
        {
            _service.StateChanged += OnStateChanged;
            Refresh();
        }
        else
        {
            _ = RefreshBackendAsync();
        }
    }

    public void Dispose()
    {
        if (_backendService is null)
        {
            _service.StateChanged -= OnStateChanged;
        }
    }

    public ObservableCollection<ExpenseRecord> FilteredExpenses { get; }
    public ObservableCollection<string> Categories { get; }

    public string SelectedPeriod
    {
        get => _selectedPeriod;
        private set
        {
            if (SetProperty(ref _selectedPeriod, value))
            {
                OnPropertyChanged(nameof(IsTodaySelected));
                OnPropertyChanged(nameof(IsThisWeekSelected));
                OnPropertyChanged(nameof(IsThisMonthSelected));
                Refresh();
            }
        }
    }

    public string SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetProperty(ref _selectedCategory, value ?? "All"))
            {
                Refresh();
            }
        }
    }

    public bool IsTodaySelected => SelectedPeriod == "Today";
    public bool IsThisWeekSelected => SelectedPeriod == "ThisWeek";
    public bool IsThisMonthSelected => SelectedPeriod == "ThisMonth";

    public decimal TodayAmount { get; private set; }
    public decimal ThisMonthAmount { get; private set; }
    public string TopCategory { get; private set; } = "—";
    public string TodayAmountDisplay => $"Rs. {TodayAmount:N0}";
    public string ThisMonthAmountDisplay => $"Rs. {ThisMonthAmount:N0}";

    public ICommand AddExpenseCommand { get; }
    public ICommand EditExpenseCommand { get; }
    public ICommand SelectPeriodCommand { get; }

    private void OpenExpenseDialog(ExpenseRecord? expense)
    {
        _dialogService.Show(new ExpenseEditViewModel(
            _toastService,
            _dialogService.Close,
            expense,
            RefreshAfterMutation,
            _backendService,
            Categories.Where(x => !string.Equals(x, "All", StringComparison.OrdinalIgnoreCase)).ToArray()));
    }

    private void SelectPeriod(string period)
    {
        if (!string.IsNullOrWhiteSpace(period))
        {
            SelectedPeriod = period;
        }
    }

    private void OnStateChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        if (_backendService is not null)
        {
            if (!_backendLoaded && !_backendLoading)
            {
                _ = RefreshBackendAsync();
                return;
            }

            ApplyExpenseState(_backendExpenses);
            return;
        }

        ApplyExpenseState(_service.Expenses);
    }

    private void RefreshAfterMutation()
    {
        if (_backendService is null)
        {
            Refresh();
        }
        else
        {
            _ = RefreshBackendAsync();
        }
    }

    private async Task RefreshBackendAsync()
    {
        if (_backendService is null || _backendLoading)
        {
            return;
        }

        _backendLoading = true;
        try
        {
            var expenses = await _backendService.GetExpensesAsync();
            var categories = await _backendService.GetExpenseCategoriesAsync();

            _backendExpenses.Clear();
            _backendExpenses.AddRange(expenses);
            _backendLoaded = true;

            var selected = SelectedCategory;
            Categories.Clear();
            Categories.Add("All");
            foreach (var category in categories
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                Categories.Add(category);
            }

            SelectedCategory = Categories.Any(x =>
                string.Equals(x, selected, StringComparison.OrdinalIgnoreCase))
                    ? selected
                    : "All";

            ApplyExpenseState(_backendExpenses);
        }
        catch (Exception ex)
        {
            _toastService.Show(
                $"Expenses could not be refreshed: {ex.Message}",
                ToastTone.Danger);
        }
        finally
        {
            _backendLoading = false;
        }
    }

    private void ApplyExpenseState(IEnumerable<ExpenseRecord> source)
    {
        var all = source.ToArray();
        var today = DateTime.Today;
        var startOfWeek = today.AddDays(-((7 + (today.DayOfWeek - DayOfWeek.Monday)) % 7));

        IEnumerable<ExpenseRecord> query = all;
        query = SelectedPeriod switch
        {
            "Today" => query.Where(expense => expense.Date.Date == today),
            "ThisWeek" => query.Where(expense => expense.Date.Date >= startOfWeek),
            "ThisMonth" => query.Where(expense => expense.Date.Year == today.Year && expense.Date.Month == today.Month),
            _ => query
        };

        if (!string.Equals(SelectedCategory, "All", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(expense =>
                string.Equals(expense.Category, SelectedCategory, StringComparison.OrdinalIgnoreCase));
        }

        FilteredExpenses.Clear();
        foreach (var expense in query.OrderByDescending(expense => expense.Date).ThenByDescending(expense => expense.Id))
        {
            FilteredExpenses.Add(expense);
        }

        TodayAmount = all
            .Where(expense => expense.Date.Date == today)
            .Sum(expense => expense.Amount);
        var monthly = all
            .Where(expense => expense.Date.Year == today.Year && expense.Date.Month == today.Month)
            .ToArray();
        ThisMonthAmount = monthly.Sum(expense => expense.Amount);
        TopCategory = monthly
            .GroupBy(expense => expense.Category)
            .OrderByDescending(group => group.Sum(expense => expense.Amount))
            .Select(group => group.Key)
            .FirstOrDefault() ?? "—";

        OnPropertyChanged(nameof(TodayAmount));
        OnPropertyChanged(nameof(ThisMonthAmount));
        OnPropertyChanged(nameof(TopCategory));
        OnPropertyChanged(nameof(TodayAmountDisplay));
        OnPropertyChanged(nameof(ThisMonthAmountDisplay));
    }
}
