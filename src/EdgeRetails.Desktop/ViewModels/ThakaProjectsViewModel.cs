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
public sealed class ThakaProjectsViewModel : ViewModelBase
{
    private readonly ISessionContext? _sessionContext;
    private readonly INavigationService? _navigationService;
    private readonly IToastService? _toastService;
    private readonly IDialogService? _dialogService;
    private readonly IDrawerService? _drawerService;
    private readonly DemoRetailState _retailState = DemoRetailState.Instance;

    private string _searchText = string.Empty;
    private string _selectedFilter = "All";
    private bool _isCardsView = true;
    private bool _isNewThakaOpen = false;

    private ThakaProjectListItemViewModel? _selectedProject;

    public event EventHandler<ThakaProjectListItemViewModel>? WorkspaceOpened;

    public ThakaProjectsViewModel()
        : this(null, null, null, null, null)
    {
    }

    public ThakaProjectsViewModel(
        ISessionContext? sessionContext = null,
        INavigationService? navigationService = null,
        IToastService? toastService = null,
        IDialogService? dialogService = null,
        IDrawerService? drawerService = null)
    {
        _sessionContext = sessionContext;
        _navigationService = navigationService;
        _toastService = toastService;
        _dialogService = dialogService;
        _drawerService = drawerService;

        // Cashier profile context
        CashierName = _sessionContext?.DisplayName ?? "Abdullah";
        CashierRole = _sessionContext?.RoleName ?? "Owner";
        CashierInitials = _sessionContext?.Initials ?? "A";
        IsOnline = _sessionContext?.IsOnline ?? true;
        AllProjects = _retailState.ThakaProjects;
        FilteredProjects = new ObservableCollection<ThakaProjectListItemViewModel>(AllProjects);

        // Sub-ViewModel for New Thaka creation
        NewThaka = new NewThakaViewModel(
            onProjectCreated: OnNewProjectCreated,
            onCloseRequested: CloseNewThaka,
            toastService: _toastService);

        // Commands
        NewThakaCommand = new RelayCommand(OpenNewThaka);
        CloseNewThakaCommand = new RelayCommand(CloseNewThaka);
        SetFilterCommand = new RelayCommand<string>(ExecuteSetFilter);
        ToggleViewModeCommand = new RelayCommand<string>(ExecuteToggleViewMode);
        OpenWorkspaceCommand = new RelayCommand<ThakaProjectListItemViewModel>(OpenWorkspace);

        _retailState.StateChanged += OnRetailStateChanged;
        RecalculateKpis();
        ApplyFilter();
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
    private int _activeThakasCount = 3;
    public string ActiveThakasCount => _activeThakasCount.ToString(CultureInfo.InvariantCulture);
    public string ActiveThakasSubtitle => "Ongoing projects";

    private decimal _totalMaterialValue = 687400m;
    public string TotalMaterialValueFormatted => $"Rs. {_totalMaterialValue:N0}";
    public string TotalMaterialValueSubtitle => "Total issued";

    private decimal _outstandingBalance = 587400m;
    public string OutstandingBalanceFormatted => $"Rs. {_outstandingBalance:N0}";
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
            if (SetProperty(ref _searchText, value))
            {
                ApplyFilter();
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
                ApplyFilter();
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
        _retailState.AddProject(newProject);
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
