using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using EdgeRetails.Desktop.Navigation;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

/// <summary>
/// ViewModel for Thaka Projects screen (Figma 14:4538 "Thaka Project").
/// Manages active/settled customer project contracts, financial KPIs, and workspace navigation.
/// </summary>
public sealed class ThakaProjectsViewModel : ViewModelBase, IDisposable
{
    private readonly ISessionContext? _sessionContext;
    private readonly INavigationService? _navigationService;
    private readonly IToastService? _toastService;
    private readonly IDialogService? _dialogService;
    private readonly IDrawerService? _drawerService;
    private readonly IBackendThakaService? _backendService;
    private readonly DemoRetailState? _retailState;
    private CancellationTokenSource? _backendRefreshCancellation;
    private long _backendRefreshVersion;
    private DateOnly? _nextStartedOn;
    private Guid? _nextProjectId;
    private bool _hasMore;
    private const int ProjectPageSize = 200;

    private string _searchText = string.Empty;
    private string _selectedFilter = "All";
    private bool _isCardsView = true;
    private bool _isNewThakaOpen = false;

    private ThakaProjectListItemViewModel? _selectedProject;

    public event EventHandler<ThakaProjectListItemViewModel>? WorkspaceOpened;

    public ThakaProjectsViewModel()
        : this(null, null, null, null, null, null)
    {
    }

    public ThakaProjectsViewModel(
        ISessionContext? sessionContext = null,
        INavigationService? navigationService = null,
        IToastService? toastService = null,
        IDialogService? dialogService = null,
        IDrawerService? drawerService = null,
        IBackendThakaService? backendService = null)
    {
        _sessionContext = sessionContext;
        _navigationService = navigationService;
        _toastService = toastService;
        _dialogService = dialogService;
        _drawerService = drawerService;
        _backendService = backendService;
        _retailState = ResolvePreviewRetailState(backendService);
        _isBackendLoading = backendService is not null;

        // Cashier profile context
        CashierName = _sessionContext?.DisplayName ?? "Abdullah";
        CashierRole = _sessionContext?.RoleName ?? "Owner";
        CashierInitials = _sessionContext?.Initials ?? "A";
        IsOnline = _sessionContext?.IsOnline ?? true;
        AllProjects = _retailState?.ThakaProjects ?? [];
        FilteredProjects = new ObservableCollection<ThakaProjectListItemViewModel>(AllProjects);

        // Sub-ViewModel for New Thaka creation
        NewThaka = new NewThakaViewModel(
            onProjectCreated: OnNewProjectCreated,
            onCloseRequested: CloseNewThaka,
            toastService: _toastService,
            backendService: _backendService);

        // Commands
        NewThakaCommand = new RelayCommand(OpenNewThaka);
        CloseNewThakaCommand = new RelayCommand(CloseNewThaka);
        SetFilterCommand = new RelayCommand<string>(ExecuteSetFilter);
        ToggleViewModeCommand = new RelayCommand<string>(ExecuteToggleViewMode);
        OpenWorkspaceCommand = new RelayCommand<ThakaProjectListItemViewModel>(OpenWorkspace);
        LoadMoreCommand = new RelayCommand(() => _ = LoadMoreAsync(), () => HasMore);

        if (_backendService is null)
        {
            if (_retailState is not null)
            {
                _retailState.StateChanged += OnRetailStateChanged;
            }

            RecalculateKpis();
            ApplyFilter();
        }
        else
        {
            _ = RefreshBackendAsync();
        }
    }

    public void Dispose()
    {
        if (_retailState is not null)
        {
            _retailState.StateChanged -= OnRetailStateChanged;
        }

        _backendRefreshCancellation?.Cancel();
        _backendRefreshCancellation?.Dispose();
    }

    // Cashier Profile Context
    public string CashierName { get; }
    public string CashierRole { get; }
    public string CashierInitials { get; }
    public string CashierDisplay => $"{CashierName}, {CashierRole}";
    public bool IsOnline { get; }

    // Page Header
    public string PageTitle => "Thaka / Projects";
    public string PageSubtitle => "Manage customer project contracts & material tracking";

    // Summary KPI Cards (Figma 14:4538)
    private bool _isBackendLoading;
    private bool _backendUnavailable;
    private int _activeThakasCount;
    public string ActiveThakasCount => _isBackendLoading
        ? "Loading…"
        : _backendUnavailable
            ? "Unavailable"
            : _activeThakasCount.ToString(CultureInfo.InvariantCulture);
    public string ActiveThakasSubtitle => "Ongoing projects";

    private decimal _totalMaterialValue;
    public string TotalMaterialValueFormatted => _isBackendLoading
        ? "Loading…"
        : _backendUnavailable
            ? "Unavailable"
            : $"Rs. {_totalMaterialValue:N0}";
    public string TotalMaterialValueSubtitle => "Total issued";

    private decimal _outstandingBalance;
    public string OutstandingBalanceFormatted => _isBackendLoading
        ? "Loading…"
        : _backendUnavailable
            ? "Unavailable"
            : $"Rs. {_outstandingBalance:N0}";
    public string OutstandingBalanceSubtitle => "Unpaid balance";

    // Collections
    public ObservableCollection<ThakaProjectListItemViewModel> AllProjects { get; }
    public ObservableCollection<ThakaProjectListItemViewModel> FilteredProjects { get; }

    public ThakaProjectListItemViewModel? SelectedProject
    {
        get => _selectedProject;
        set => SetProperty(ref _selectedProject, value);
    }

    // Search and Filter
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

    public string SelectedFilter
    {
        get => _selectedFilter;
        private set
        {
            if (SetProperty(ref _selectedFilter, value))
            {
                OnPropertyChanged(nameof(IsFilterAll));
                OnPropertyChanged(nameof(IsFilterActive));
                OnPropertyChanged(nameof(IsFilterSettled));
                ScheduleBackendRefresh();
            }
        }
    }

    public bool IsFilterAll => string.Equals(SelectedFilter, "All", StringComparison.OrdinalIgnoreCase);
    public bool IsFilterActive => string.Equals(SelectedFilter, "Active", StringComparison.OrdinalIgnoreCase);
    public bool IsFilterSettled => string.Equals(SelectedFilter, "Settled", StringComparison.OrdinalIgnoreCase);

    // View Mode Toggle (Cards vs Table)
    public bool IsCardsView
    {
        get => _isCardsView;
        private set
        {
            if (SetProperty(ref _isCardsView, value))
            {
                OnPropertyChanged(nameof(IsTableView));
            }
        }
    }

    public bool IsTableView => !_isCardsView;

    // New Thaka Dialog State
    public NewThakaViewModel NewThaka { get; }

    public bool IsNewThakaOpen
    {
        get => _isNewThakaOpen;
        set => SetProperty(ref _isNewThakaOpen, value);
    }

    // Commands
    public ICommand NewThakaCommand { get; }
    public ICommand CloseNewThakaCommand { get; }
    public ICommand SetFilterCommand { get; }
    public ICommand ToggleViewModeCommand { get; }
    public ICommand OpenWorkspaceCommand { get; }
    public ICommand LoadMoreCommand { get; }
    public bool HasMore => _hasMore;

    public void OpenNewThaka()
    {
        NewThaka.Reset();
        IsNewThakaOpen = true;
    }

    public void CloseNewThaka()
    {
        IsNewThakaOpen = false;
        _dialogService?.Close();
        _drawerService?.Close();
    }

    public void OpenWorkspace(ThakaProjectListItemViewModel? project)
    {
        if (project == null)
        {
            return;
        }

        SelectedProject = project;
        WorkspaceOpened?.Invoke(this, project);

        _toastService?.Show(
            $"Opening workspace for '{project.ProjectName}' ({project.CustomerName})",
            ToastTone.Info);
    }

    private void OnNewProjectCreated(ThakaProjectListItemViewModel newProject)
    {
        if (_backendService is null)
        {
            if (_retailState is not null)
            {
                _retailState.AddProject(newProject);
            }
            else
            {
                _toastService?.Show(
                    "Authoritative Thaka project service is unavailable.",
                    ToastTone.Warning);
            }
            return;
        }

        AllProjects.Insert(0, newProject);
        RecalculateKpis();
        ApplyFilter();
    }

    private void ScheduleBackendRefresh()
    {
        if (_backendService is null)
        {
            ApplyFilter();
            return;
        }

        _nextStartedOn = null;
        _nextProjectId = null;
        _hasMore = false;
        OnPropertyChanged(nameof(HasMore));
        if (LoadMoreCommand is RelayCommand command)
        {
            command.NotifyCanExecuteChanged();
        }

        _ = RefreshBackendAsync();
    }

    public Task<IReadOnlyList<ThakaProjectListItemViewModel>> LoadProjectsAsync(CancellationToken cancellationToken = default)
    {
        return _backendService is null
            ? Task.FromResult<IReadOnlyList<ThakaProjectListItemViewModel>>(AllProjects)
            : _backendService.GetProjectsAsync();
    }

    private async Task RefreshBackendAsync()
    {
        if (_backendService is null)
        {
            return;
        }

        var version = Interlocked.Increment(ref _backendRefreshVersion);
        var previous = Interlocked.Exchange(ref _backendRefreshCancellation, new CancellationTokenSource());
        previous?.Cancel();
        previous?.Dispose();
        var cts = _backendRefreshCancellation!;

        try
        {
            await Task.Delay(250, cts.Token);

            if (cts.IsCancellationRequested || version != Volatile.Read(ref _backendRefreshVersion))
            {
                return;
            }

            var page = await _backendService.GetProjectsPageAsync(
                SearchText,
                SelectedFilter,
                ProjectPageSize,
                _nextStartedOn,
                _nextProjectId,
                cts.Token);

            if (cts.IsCancellationRequested || version != Volatile.Read(ref _backendRefreshVersion))
            {
                return;
            }

            var reset = _nextStartedOn is null && _nextProjectId is null;
            if (reset)
            {
                AllProjects.Clear();
                FilteredProjects.Clear();
            }

            foreach (var project in page.Items)
            {
                AllProjects.Add(project);
                FilteredProjects.Add(project);
            }

            _nextStartedOn = page.NextStartedOn;
            _nextProjectId = page.NextProjectId;
            _hasMore = page.HasMore;
            _activeThakasCount = page.TotalActiveCount;
            _totalMaterialValue = page.TotalActiveMaterialValue;
            _outstandingBalance = page.TotalActiveBalance;
            _isBackendLoading = false;
            _backendUnavailable = false;
            NotifyBackendState();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (version == Volatile.Read(ref _backendRefreshVersion))
        {
            _isBackendLoading = false;
            _backendUnavailable = true;
            NotifyBackendState();
            _toastService?.Show(
                $"Thaka projects could not be refreshed: {ex.Message}",
                ToastTone.Danger);
        }
    }

    public async Task LoadMoreAsync()
    {
        if (_backendService is null || !_hasMore)
        {
            return;
        }

        var version = Volatile.Read(ref _backendRefreshVersion);
        var cts = _backendRefreshCancellation ?? new CancellationTokenSource();
        try
        {
            var page = await _backendService.GetProjectsPageAsync(
                SearchText,
                SelectedFilter,
                ProjectPageSize,
                _nextStartedOn,
                _nextProjectId,
                cts.Token);

            if (version != Volatile.Read(ref _backendRefreshVersion))
            {
                return;
            }

            foreach (var project in page.Items)
            {
                AllProjects.Add(project);
                FilteredProjects.Add(project);
            }

            _nextStartedOn = page.NextStartedOn;
            _nextProjectId = page.NextProjectId;
            _hasMore = page.HasMore;
            OnPropertyChanged(nameof(HasMore));
            if (LoadMoreCommand is RelayCommand command)
            {
                command.NotifyCanExecuteChanged();
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
    }

    private void NotifyBackendState()
    {
        OnPropertyChanged(nameof(ActiveThakasCount));
        OnPropertyChanged(nameof(TotalMaterialValueFormatted));
        OnPropertyChanged(nameof(OutstandingBalanceFormatted));
        OnPropertyChanged(nameof(HasMore));
        if (LoadMoreCommand is RelayCommand command)
        {
            command.NotifyCanExecuteChanged();
        }
    }

    private void OnRetailStateChanged(object? sender, EventArgs e)
    {
        RecalculateKpis();
        ApplyFilter();
    }

    private void ExecuteSetFilter(string? filter)
    {
        if (!string.IsNullOrEmpty(filter))
        {
            SelectedFilter = filter;
        }
    }

    private void ExecuteToggleViewMode(string? mode)
    {
        if (string.Equals(mode, "Table", StringComparison.OrdinalIgnoreCase))
        {
            IsCardsView = false;
        }
        else
        {
            IsCardsView = true;
        }
    }

    private void RecalculateKpis()
    {
        var activeProjects = AllProjects.Where(p => p.IsActive).ToList();

        _activeThakasCount = activeProjects.Count;
        _totalMaterialValue = activeProjects.Sum(p => p.MaterialValue);
        _outstandingBalance = activeProjects.Sum(p => p.Balance);

        OnPropertyChanged(nameof(ActiveThakasCount));
        OnPropertyChanged(nameof(TotalMaterialValueFormatted));
        OnPropertyChanged(nameof(OutstandingBalanceFormatted));
    }

    private static DemoRetailState? ResolvePreviewRetailState(
        IBackendThakaService? backendService)
    {
#if DEBUG
        return backendService is null ? DemoRetailState.Instance : null;
#else
        return null;
#endif
    }

    private void ApplyFilter()
    {
        var query = AllProjects.AsEnumerable();

        // 1. Search term filter (customer, project or phone)
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var term = SearchText.Trim();
            query = query.Where(p =>
                p.ProjectName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                p.CustomerName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                p.Phone.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                p.Location.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        // 2. Status pill tab filter
        if (string.Equals(SelectedFilter, "Active", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(p => p.IsActive);
        }
        else if (string.Equals(SelectedFilter, "Settled", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(p => p.IsSettled);
        }

        FilteredProjects.Clear();
        foreach (var project in query)
        {
            FilteredProjects.Add(project);
        }
    }
}
