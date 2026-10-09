using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class ExpenseEditViewModel : ViewModelBase
{
    private int _submissionGate;
    private bool _confirmed;
    private SubmittedExpense? _submittedExpense;
    private sealed record SubmittedExpense(string Category, string Subcategory, decimal Amount,
        DateTime Date, string PaymentMethod, string StaffMember, string Note);
    public bool IsBusy => Volatile.Read(ref _submissionGate) != 0;
    private Guid? _voidClientOperationId;
    private bool _isVoided;
    private string _voidReason = string.Empty;

    public bool IsReadOnly => _existing?.BackendId is not null;
    public bool CanVoid => IsReadOnly && !IsBusy && !_isVoided && _backendService is not null;
    public bool CanEdit => !IsReadOnly && !IsBusy && _submittedExpense is null && !_confirmed;

    public string VoidReason
    {
        get => _voidReason;
        set
        {
            if (SetProperty(ref _voidReason, value))
            {
                ((RelayCommand)VoidCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public string SubmissionStatus => IsReadOnly
        ? (_isVoided ? "Expense has been voided." : _voidClientOperationId is not null
            ? "Void outcome unconfirmed. Retry preserves the original void operation identity."
            : "Posted expense — read only. Enter void reason and click Void Expense to reverse this transaction.")
        : IsBusy ? "Submitting expense…" : _submittedExpense is not null
        ? "Outcome unresolved. Retry preserves the original expense; restart recovery is not yet supported." : string.Empty;
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

        SaveCommand = new RelayCommand(async () => await SaveAsync(), () => !IsReadOnly && !IsBusy && !_confirmed);
        VoidCommand = new RelayCommand(async () => await VoidAsync(), () => CanVoid && !string.IsNullOrWhiteSpace(_voidReason));
        CancelCommand = new RelayCommand(_close);
    }

    public string Title => IsReadOnly ? "View Posted Expense" : _existing is null ? "Add Expense" : "Edit Expense";
    public string SaveButtonText => IsReadOnly ? "Read only" : _existing is null ? "Add Expense" : "Save Changes";
    public IReadOnlyList<string> Categories => _categories;
    public IReadOnlyList<string> PaymentMethods => _service.PaymentMethods;

    public string SelectedCategory
    {
        get => _selectedCategory;
        set { if (CanEdit) { SetProperty(ref _selectedCategory, value ?? string.Empty); } }
    }

    public string Subcategory
    {
        get => _subcategory;
        set { if (CanEdit) { SetProperty(ref _subcategory, value ?? string.Empty); } }
    }

    public string AmountText
    {
        get => _amountText;
        set { if (CanEdit) { SetProperty(ref _amountText, value ?? string.Empty); } }
    }

    public DateTime Date
    {
        get => _date;
        set { if (CanEdit) { SetProperty(ref _date, value); } }
    }

    public string SelectedPaymentMethod
    {
        get => _selectedPaymentMethod;
        set { if (CanEdit) { SetProperty(ref _selectedPaymentMethod, value ?? string.Empty); } }
    }

    public string StaffMember
    {
        get => _staffMember;
        set { if (CanEdit) { SetProperty(ref _staffMember, value ?? string.Empty); } }
    }

    public string Note
    {
        get => _note;
        set { if (CanEdit) { SetProperty(ref _note, value ?? string.Empty); } }
    }

    public ICommand SaveCommand { get; }
    public ICommand VoidCommand { get; }
    public ICommand CancelCommand { get; }

    private async Task SaveAsync()
    {
        if (IsReadOnly)
        {
            return;
        }
        if (Interlocked.CompareExchange(ref _submissionGate, 1, 0) != 0)
        {
            return;
        }
        NotifySubmissionState();
        try
        {
        if (_confirmed)
        {
            return;
        }
        if (!decimal.TryParse(AmountText, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) &&
            !decimal.TryParse(AmountText, NumberStyles.Number, CultureInfo.CurrentCulture, out amount))
        {
            _toastService.Show("Expense amount must be a valid number.", ToastTone.Warning);
            return;
        }

        var submitted = _submittedExpense ?? new SubmittedExpense(SelectedCategory, Subcategory, amount,
            Date, SelectedPaymentMethod, StaffMember, Note);

        try
        {
            if (_backendService is null)
            {
                _service.SaveExpense(
                    _existing,
                    submitted.Category,
                    submitted.Subcategory,
                    submitted.Amount,
                    submitted.Date,
                    submitted.PaymentMethod,
                    submitted.StaffMember,
                    submitted.Note);
            }
            else
            {
                if (_existing?.BackendId is not null)
                {
                    throw new InvalidOperationException(
                        "Posted expenses are immutable. Use a void/correction flow instead of editing in place.");
                }

                _submittedExpense = submitted;
                NotifySubmissionState();
                await _backendService.PostExpenseAsync(submitted.Category, submitted.Subcategory,
                    submitted.Amount, submitted.Date, submitted.PaymentMethod, submitted.Note);
                _submittedExpense = null;
            }

            _confirmed = true;
            _toastService.Show(
                _existing is null ? "Expense added." : "Expense updated.",
                ToastTone.Success);
            _close();
            _saved?.Invoke();
        }
        catch (Exception ex)
        {
            _toastService.Show(
                DesktopErrorPresentation.ForException(ex, "Expense could not be saved."),
                ToastTone.Danger);
        }
        }
        finally
        {
            Interlocked.Exchange(ref _submissionGate, 0);
            NotifySubmissionState();
        }
    }

    private async Task VoidAsync()
    {
        if (!CanVoid || string.IsNullOrWhiteSpace(_voidReason))
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _submissionGate, 1, 0) != 0)
        {
            return;
        }
        NotifySubmissionState();

        try
        {
            _voidClientOperationId ??= Guid.NewGuid();
            if (_backendService is not null && _existing?.BackendId is Guid expenseId)
            {
                await _backendService.VoidExpenseAsync(expenseId, _voidReason, _voidClientOperationId.Value);
            }

            _isVoided = true;
            _toastService.Show("Expense voided successfully.", ToastTone.Success);
            _close();
            _saved?.Invoke();
        }
        catch (Exception ex)
        {
            _toastService.Show(
                DesktopErrorPresentation.ForException(ex, "Failed to void expense. Retry preserves exact operation identity."),
                ToastTone.Danger);
        }
        finally
        {
            Interlocked.Exchange(ref _submissionGate, 0);
            NotifySubmissionState();
        }
    }

    private void NotifySubmissionState()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanVoid));
        OnPropertyChanged(nameof(SubmissionStatus));
        ((RelayCommand)SaveCommand).NotifyCanExecuteChanged();
        ((RelayCommand)VoidCommand).NotifyCanExecuteChanged();
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

    private const int PageSize = 100;
    private bool _hasMoreExpenses;
    public bool HasMoreExpenses
    {
        get => _hasMoreExpenses;
        private set
        {
            if (SetProperty(ref _hasMoreExpenses, value))
            {
                OnPropertyChanged(nameof(CanLoadMoreExpenses));
                ((RelayCommand)LoadMoreExpensesCommand).NotifyCanExecuteChanged();
            }
        }
    }
    public bool CanLoadMoreExpenses => HasMoreExpenses && !_backendLoading;

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
        ViewExpenseCommand = new RelayCommand<ExpenseRecord>(OpenExpenseDialog);
        SelectPeriodCommand = new RelayCommand<string>(SelectPeriod);
        LoadMoreExpensesCommand = new RelayCommand(async () => await LoadMoreExpensesAsync(), () => CanLoadMoreExpenses);

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
    public string TodayAmountDisplay => $"Rs. {TodayAmount:N2}";
    public string ThisMonthAmountDisplay => $"Rs. {ThisMonthAmount:N2}";

    public ICommand AddExpenseCommand { get; }
    public ICommand ViewExpenseCommand { get; }
    public ICommand SelectPeriodCommand { get; }
    public ICommand LoadMoreExpensesCommand { get; }

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
        OnPropertyChanged(nameof(CanLoadMoreExpenses));
        ((RelayCommand)LoadMoreExpensesCommand).NotifyCanExecuteChanged();
        try
        {
            var expenses = await _backendService.GetExpensesAsync(PageSize);
            var categories = await _backendService.GetExpenseCategoriesAsync();

            _backendExpenses.Clear();
            _backendExpenses.AddRange(expenses);
            _backendLoaded = true;
            HasMoreExpenses = expenses.Count >= PageSize;

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
                DesktopErrorPresentation.ForException(
                    ex,
                    "Expenses could not be refreshed. Check the connection and try again."),
                ToastTone.Danger);
        }
        finally
        {
            _backendLoading = false;
            OnPropertyChanged(nameof(CanLoadMoreExpenses));
            ((RelayCommand)LoadMoreExpensesCommand).NotifyCanExecuteChanged();
        }
    }

    private async Task LoadMoreExpensesAsync()
    {
        if (_backendService is null || _backendLoading || !HasMoreExpenses || _backendExpenses.Count == 0)
        {
            return;
        }

        _backendLoading = true;
        OnPropertyChanged(nameof(CanLoadMoreExpenses));
        ((RelayCommand)LoadMoreExpensesCommand).NotifyCanExecuteChanged();
        try
        {
            var last = _backendExpenses[^1];
            var lastDate = DateOnly.FromDateTime(last.Date);
            var lastId = last.BackendId ?? Guid.Empty;

            var nextPage = await _backendService.GetExpensesAsync(PageSize, lastDate, lastId);
            _backendExpenses.AddRange(nextPage);
            HasMoreExpenses = nextPage.Count >= PageSize;

            ApplyExpenseState(_backendExpenses);
        }
        catch (Exception ex)
        {
            _toastService.Show(
                DesktopErrorPresentation.ForException(ex, "Failed to load more expenses."),
                ToastTone.Danger);
        }
        finally
        {
            _backendLoading = false;
            OnPropertyChanged(nameof(CanLoadMoreExpenses));
            ((RelayCommand)LoadMoreExpensesCommand).NotifyCanExecuteChanged();
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
