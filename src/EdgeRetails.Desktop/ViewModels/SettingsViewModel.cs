using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using EdgeRetails.Desktop.Services;
using Microsoft.Win32;

namespace EdgeRetails.Desktop.ViewModels;

public enum SettingsSection
{
    Shop,
    Receipt,
    UsersAccess,
    CategoriesUnits,
    Backup,
    License,
    Appearance,
    Database
}

public sealed class SettingsViewModel : ViewModelBase, IDisposable
{
    private readonly IThemeService _themeService;
    private readonly IDialogService _dialogService;
    private readonly IToastService _toastService;
    private readonly IBackendSettingsService? _backendService;
    private readonly DemoSettingsState? _previewState;

    private SettingsSection _selectedSection = SettingsSection.Shop;
    private AppTheme _selectedTheme;
    private bool _isLoading;
    private string? _loadError;

    private string _licenseId = "Unavailable";
    private string _licenseStore = "Unavailable";
    private string _licenseModule = "Unavailable";
    private string _licenseExpiry = "Unavailable";
    private string _licensedTerminals = "Unavailable";
    private string _licenseStatus = "Unavailable";

    private string _databaseName = "PostgreSQL";
    private string _databaseSize = "Unavailable";
    private string _databaseStatus = "Unavailable";
    private string _connectionStatus = "Database readiness unavailable";
    private string _workerStatus = "Unavailable";
    private string _lastBackupDisplay = "Unavailable";
    private string _maintenanceStatus = "Unavailable";

    public SettingsViewModel(
        IThemeService themeService,
        IDialogService dialogService,
        IToastService toastService,
        IBackendSettingsService? backendService = null)
    {
        _themeService = themeService;
        _dialogService = dialogService;
        _toastService = toastService;
        _backendService = backendService;
        _previewState = ResolvePreviewState(backendService);
        _selectedTheme = themeService.CurrentTheme;

        Users = [];
        Categories = [];
        Units = [];
        Backups = [];
        PermissionMatrix = [];

        Printers =
        [
            "Not configured",
            "Windows Default",
            "Thermal 80mm",
            "Thermal 58mm"
        ];
        PaperSizes = ["80mm", "58mm"];
        Printer = "Not configured";
        PaperSize = "80mm";

        SelectSectionCommand = new RelayCommand<string>(SelectSection);
        SaveShopCommand = new RelayCommand(() => _ = SaveShopAsync());
        SaveReceiptCommand = new RelayCommand(() => _ = SaveReceiptAsync());
        UploadLogoCommand = new RelayCommand(ChooseLogo);
        AddUserCommand = new RelayCommand(() => OpenEditor(SettingsEditorKind.User));
        EditUserCommand = new RelayCommand<SettingsUserRecord>(
            user => OpenEditor(SettingsEditorKind.User, user));
        AddCategoryCommand = new RelayCommand(
            () => OpenEditor(SettingsEditorKind.Category));
        EditCategoryCommand = new RelayCommand<SettingsCategoryRecord>(
            category => OpenEditor(SettingsEditorKind.Category, category));
        ToggleCategoryCommand =
            new RelayCommand<SettingsCategoryRecord>(ToggleCategory);
        AddUnitCommand = new RelayCommand(() => OpenEditor(SettingsEditorKind.Unit));
        EditUnitCommand = new RelayCommand<SettingsUnitRecord>(
            unit => OpenEditor(SettingsEditorKind.Unit, unit));
        ToggleUnitCommand = new RelayCommand<SettingsUnitRecord>(ToggleUnit);
        BackupNowCommand = new RelayCommand(BackupNow);
        RestoreCommand = new RelayCommand(OpenRestore);
        ImportLicenseCommand = new RelayCommand(OpenLicenseImport);
        ApplyThemeCommand = new RelayCommand(ApplyTheme);
        RefreshDiagnosticsCommand =
            new RelayCommand(() => _ = RefreshBackendAsync(true));

        _themeService.ThemeChanged += OnThemeChanged;

#if DEBUG
        if (_previewState is not null)
        {
            _previewState.StateChanged += OnPreviewStateChanged;
            LoadPreviewState();
        }
        else
#endif
        if (_backendService is not null)
        {
            SetProductionEditableDefaults();
            _ = RefreshBackendAsync(false);
        }
        else
        {
            SetProductionEditableDefaults();
            ApplyUnavailable(
                "Authoritative Settings backend service is not attached.");
        }
    }

    public void Dispose()
    {
#if DEBUG
        if (_previewState is not null)
        {
            _previewState.StateChanged -= OnPreviewStateChanged;
        }
#endif
        _themeService.ThemeChanged -= OnThemeChanged;
    }

    public SettingsSection SelectedSection
    {
        get => _selectedSection;
        private set
        {
            if (SetProperty(ref _selectedSection, value))
            {
                RaiseSectionProperties();
            }
        }
    }

    public bool IsShop => SelectedSection == SettingsSection.Shop;
    public bool IsReceipt => SelectedSection == SettingsSection.Receipt;
    public bool IsUsersAccess => SelectedSection == SettingsSection.UsersAccess;
    public bool IsCategoriesUnits => SelectedSection == SettingsSection.CategoriesUnits;
    public bool IsBackup => SelectedSection == SettingsSection.Backup;
    public bool IsLicense => SelectedSection == SettingsSection.License;
    public bool IsAppearance => SelectedSection == SettingsSection.Appearance;
    public bool IsDatabase => SelectedSection == SettingsSection.Database;

    public ObservableCollection<SettingsUserRecord> Users { get; }
    public ObservableCollection<SettingsCategoryRecord> Categories { get; }
    public ObservableCollection<SettingsUnitRecord> Units { get; }
    public ObservableCollection<SettingsBackupRecord> Backups { get; }

    public IReadOnlyList<SettingsPermissionRow> PermissionMatrix { get; private set; }

    public string ShopName { get; set; } = string.Empty;
    public string OwnerName { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string LogoPath { get; set; } = string.Empty;

    public IReadOnlyList<string> Printers { get; }
    public IReadOnlyList<string> PaperSizes { get; }

    public string Printer { get; set; }
    public string PaperSize { get; set; }
    public string HeaderText { get; set; } = string.Empty;
    public string FooterText { get; set; } = string.Empty;
    public bool ShowCustomer { get; set; }
    public bool ShowCashier { get; set; }
    public bool AutoPrint { get; set; }

    public IReadOnlyList<AppTheme> Themes { get; } =
        [AppTheme.Light, AppTheme.Dark, AppTheme.System];

    public AppTheme SelectedTheme
    {
        get => _selectedTheme;
        set => SetProperty(ref _selectedTheme, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public string? LoadError
    {
        get => _loadError;
        private set
        {
            if (SetProperty(ref _loadError, value))
            {
                OnPropertyChanged(nameof(HasLoadError));
            }
        }
    }

    public bool HasLoadError => !string.IsNullOrWhiteSpace(LoadError);

    public string LicenseId => _licenseId;
    public string LicenseStore => _licenseStore;
    public string LicenseModule => _licenseModule;
    public string LicenseExpiry => _licenseExpiry;
    public string LicensedTerminals => _licensedTerminals;
    public string LicenseStatus => _licenseStatus;

    public string DatabaseName => _databaseName;
    public string DatabaseSize => _databaseSize;
    public string DatabaseStatus => _databaseStatus;
    public string ConnectionStatus => _connectionStatus;
    public string WorkerStatus => _workerStatus;
    public string LastBackupDisplay => _lastBackupDisplay;
    public string MaintenanceStatus => _maintenanceStatus;

    public string ProductionAuthorityNotice =>
        _backendService is null
            ? "Preview/local composition"
            : "Production values are backend reads or explicit unavailable states.";

    public ICommand SelectSectionCommand { get; }
    public ICommand SaveShopCommand { get; }
    public ICommand SaveReceiptCommand { get; }
    public ICommand UploadLogoCommand { get; }
    public ICommand AddUserCommand { get; }
    public ICommand EditUserCommand { get; }
    public ICommand AddCategoryCommand { get; }
    public ICommand EditCategoryCommand { get; }
    public ICommand ToggleCategoryCommand { get; }
    public ICommand AddUnitCommand { get; }
    public ICommand EditUnitCommand { get; }
    public ICommand ToggleUnitCommand { get; }
    public ICommand BackupNowCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand ImportLicenseCommand { get; }
    public ICommand ApplyThemeCommand { get; }
    public ICommand RefreshDiagnosticsCommand { get; }

    private static DemoSettingsState? ResolvePreviewState(
        IBackendSettingsService? backendService)
    {
#if DEBUG
        return backendService is null ? DemoSettingsState.Instance : null;
#else
        return null;
#endif
    }

    private void SelectSection(string? section)
    {
        if (Enum.TryParse<SettingsSection>(
                section,
                ignoreCase: true,
                out var parsed))
        {
            SelectedSection = parsed;
        }
    }

    private async Task SaveShopAsync()
    {
#if DEBUG
        if (_previewState is not null)
        {
            try
            {
                _previewState.SaveShop(
                    ShopName,
                    OwnerName,
                    Phone,
                    Address,
                    LogoPath);
                _toastService.Show("Preview shop settings saved.", ToastTone.Success);
            }
            catch (Exception ex)
            {
                _toastService.Show(ex.Message, ToastTone.Warning);
            }
            return;
        }
#endif
        if (_backendService is null)
        {
            ShowProductionUnavailable(
                "Authoritative shop-settings service is unavailable. No changes were saved.");
            return;
        }

        try
        {
            await _backendService.SaveShopAsync(ShopName, Phone, Address);
            _toastService.Show(
                "Shop profile saved to authoritative backend persistence.",
                ToastTone.Success);
            await RefreshBackendAsync(false);
        }
        catch (Exception ex)
        {
            _toastService.Show(
                $"Shop settings were not saved: {ex.Message}",
                ToastTone.Danger);
        }
    }

    private async Task SaveReceiptAsync()
    {
#if DEBUG
        if (_previewState is not null)
        {
            _previewState.SaveReceipt(
                Printer,
                PaperSize,
                HeaderText,
                FooterText,
                ShowCustomer,
                ShowCashier,
                AutoPrint);
            _toastService.Show("Preview receipt settings saved.", ToastTone.Success);
            return;
        }
#endif
        if (_backendService is null)
        {
            ShowProductionUnavailable(
                "Authoritative receipt-template service is unavailable. No production settings were changed.");
            return;
        }

        try
        {
            await _backendService.SaveReceiptTemplateAsync(
                HeaderText,
                FooterText,
                ShowCustomer,
                ShowCashier,
                AutoPrint);
            _toastService.Show(
                "Global receipt template saved. Printer and paper selection remain workstation-local.",
                ToastTone.Success);
            await RefreshBackendAsync(false);
        }
        catch (Exception ex)
        {
            _toastService.Show(
                $"Receipt template was not saved: {ex.Message}",
                ToastTone.Danger);
        }
    }

    private void ChooseLogo()
    {
#if DEBUG
        if (_previewState is not null)
        {
            var picker = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Shop Logo",
                Filter = "Images|*.png;*.jpg;*.jpeg;*.webp"
            };

            if (picker.ShowDialog() == true)
            {
                LogoPath = picker.FileName;
                OnPropertyChanged(nameof(LogoPath));
            }
            return;
        }
#endif
        ShowProductionUnavailable(
            "Production shop-logo editing is not attached to authoritative shop settings.");
    }

    private void OpenEditor(SettingsEditorKind kind, object? existing = null)
    {
#if DEBUG
        if (_previewState is not null)
        {
            _dialogService.Show(new SettingsEditorViewModel(
                kind,
                _previewState,
                _toastService,
                _dialogService.Close,
                existing));
            return;
        }
#endif
        var area = kind switch
        {
            SettingsEditorKind.User => "User administration",
            SettingsEditorKind.Category => "Category administration",
            SettingsEditorKind.Unit => "Unit administration",
            _ => "This settings operation"
        };

        ShowProductionUnavailable(
            $"{area} does not yet have a production Desktop edit adapter. No backend state was changed.");
    }

    private void ToggleCategory(SettingsCategoryRecord? category)
    {
        // Deactivate Category / Activate Category toggle
        if (category is null)
        {
            return;
        }

#if DEBUG
        if (_previewState is not null)
        {
            _previewState.SetCategoryActive(category, !category.IsActive);
            _toastService.Show("Preview category state updated.", ToastTone.Success);
            return;
        }
#endif
        ShowProductionUnavailable(
            "Category lifecycle belongs to authoritative catalog management. No production state was changed.");
    }

    private void ToggleUnit(SettingsUnitRecord? unit)
    {
        // Deactivate Unit / Activate Unit toggle
        if (unit is null)
        {
            return;
        }

#if DEBUG
        if (_previewState is not null)
        {
            _previewState.SetUnitActive(unit, !unit.IsActive);
            _toastService.Show("Preview unit state updated.", ToastTone.Success);
            return;
        }
#endif
        ShowProductionUnavailable(
            "Unit lifecycle belongs to authoritative catalog management. No production state was changed.");
    }

    private void BackupNow()
    {
        // Worker integration is deferred until backend attachment.
#if DEBUG
        if (_previewState is not null)
        {
            _toastService.Show(
                "Preview only. No backup was created. Worker integration is deferred until backend attachment.",
                ToastTone.Info);
            return;
        }
#endif
        ShowProductionUnavailable(
            "Hardened backup infrastructure exists, but the safe production Settings operator adapter is reserved for Phase 6. No backup was started.");
    }

    private void OpenRestore()
    {
        // Backend restore is not attached yet.
#if DEBUG
        if (_previewState is not null)
        {
            var options = Backups.Select(backup => backup.Summary).ToArray();
            _dialogService.Show(new SettingsConfirmViewModel(
                "Preview Restore",
                "Preview only. Backend restore is not attached yet.",
                "Close Preview",
                _ => _toastService.Show(
                    "Preview closed. Backend restore is not attached yet.",
                    ToastTone.Info),
                _dialogService.Close,
                options));
            return;
        }
#endif
        ShowProductionUnavailable(
            "Restore operator integration is not attached to the hardened Sprint 8 restore pipeline yet. No restore was prepared or executed.");
    }

    private void OpenLicenseImport()
    {
        // Licensing backend is not attached yet.
#if DEBUG
        if (_previewState is not null)
        {
            _dialogService.Show(new SettingsEditorViewModel(
                SettingsEditorKind.LicenseImport,
                _previewState,
                _toastService,
                _dialogService.Close));
            return;
        }
#endif
        ShowProductionUnavailable(
            "Production license replacement/import UI is reserved for the authorized Phase 6 operator flow. No license state was changed.");
    }

    private void ApplyTheme()
    {
        _themeService.ApplyTheme(SelectedTheme);
        _toastService.Show(
            $"{SelectedTheme} theme applied.",
            ToastTone.Success);
    }

    private async Task RefreshBackendAsync(bool showToast)
    {
        if (_backendService is null)
        {
#if DEBUG
            if (_previewState is not null)
            {
                LoadPreviewState();
                if (showToast)
                {
                    _toastService.Show(
                        "Preview Settings refreshed.",
                        ToastTone.Info);
                }
                return;
            }
#endif
            ApplyUnavailable(
                "Authoritative Settings backend service is not attached.");
            return;
        }

        IsLoading = true;
        LoadError = null;
        try
        {
            var snapshot = await _backendService.LoadAsync();
            ApplyBackendSnapshot(snapshot);

            if (snapshot.Issues.Count > 0)
            {
                LoadError = string.Join(" ", snapshot.Issues);
                if (showToast)
                {
                    _toastService.Show(
                        "Settings diagnostics refreshed with unavailable sections.",
                        ToastTone.Warning);
                }
            }
            else if (showToast)
            {
                _toastService.Show(
                    "Settings diagnostics refreshed from authoritative backend reads.",
                    ToastTone.Success);
            }
        }
        catch (Exception ex)
        {
            ApplyUnavailable($"Settings backend unavailable: {ex.Message}");
            _toastService.Show(LoadError!, ToastTone.Danger);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyBackendSnapshot(BackendSettingsSnapshot snapshot)
    {
        ReplaceCollection(Users, snapshot.Users);

        ShopName = snapshot.ShopName;
        OwnerName = snapshot.OwnerName;
        Phone = snapshot.Phone;
        Address = snapshot.Address;
        HeaderText = snapshot.ReceiptHeader;
        FooterText = snapshot.ReceiptFooter;
        ShowCustomer = snapshot.ShowCustomer;
        ShowCashier = snapshot.ShowCashier;
        AutoPrint = snapshot.AutoPrintDefault;
        RaiseEditableProperties();

        // No authoritative Desktop read/edit models currently expose these catalog
        // tables in Settings. Phase 3 Product Management owns their final workflow.
        Categories.Clear();
        Units.Clear();
        Backups.Clear();
        PermissionMatrix = [];
        OnPropertyChanged(nameof(PermissionMatrix));

        _databaseName = snapshot.DatabaseName;
        _databaseSize = snapshot.DatabaseSize;
        _databaseStatus = snapshot.DatabaseStatus;
        _connectionStatus = snapshot.ConnectionStatus;
        _workerStatus = snapshot.WorkerStatus;
        _licenseId = snapshot.LicenseId;
        _licenseStore = snapshot.LicenseStore;
        _licenseModule = snapshot.LicenseModule;
        _licenseExpiry = snapshot.LicenseExpiry;
        _licensedTerminals = snapshot.LicensedTerminals;
        _licenseStatus = snapshot.LicenseStatus;
        _lastBackupDisplay = snapshot.LastBackupDisplay;
        _maintenanceStatus = snapshot.MaintenanceStatus;

        RaiseBackendProperties();
    }

#if DEBUG
    private void OnPreviewStateChanged(object? sender, EventArgs e)
    {
        LoadPreviewState();
    }

    private void LoadPreviewState()
    {
        if (_previewState is null)
        {
            return;
        }

        ReplaceCollection(Users, _previewState.Users);
        ReplaceCollection(Categories, _previewState.Categories);
        ReplaceCollection(Units, _previewState.Units);
        ReplaceCollection(Backups, _previewState.Backups);
        PermissionMatrix = _previewState.PermissionMatrix;

        ShopName = _previewState.ShopName;
        OwnerName = _previewState.OwnerName;
        Phone = _previewState.Phone;
        Address = _previewState.Address;
        LogoPath = _previewState.LogoPath;

        Printer = _previewState.Printer;
        PaperSize = _previewState.PaperSize;
        HeaderText = _previewState.HeaderText;
        FooterText = _previewState.FooterText;
        ShowCustomer = _previewState.ShowCustomer;
        ShowCashier = _previewState.ShowCashier;
        AutoPrint = _previewState.AutoPrint;

        _licenseId = _previewState.LicenseId;
        _licenseStore = _previewState.LicenseStore;
        _licenseModule = _previewState.LicenseModule;
        _licenseExpiry = _previewState.LicenseExpiry.ToString("dd MMM yyyy");
        _licensedTerminals = _previewState.LicensedTerminals.ToString();
        _licenseStatus = _previewState.LicenseStatus;

        _databaseName = _previewState.DatabaseName;
        _databaseSize = _previewState.DatabaseSize;
        _databaseStatus = _previewState.DatabaseStatus;
        _connectionStatus = _previewState.ConnectionStatus;
        _workerStatus = _previewState.WorkerStatus;
        _lastBackupDisplay =
            $"Preview · {_previewState.LastBackup:yyyy-MM-dd hh:mm tt}";
        _maintenanceStatus = "Preview";

        RaiseEditableProperties();
        RaiseBackendProperties();
        OnPropertyChanged(nameof(PermissionMatrix));
    }
#endif

    private void SetProductionEditableDefaults()
    {
        ShopName = string.Empty;
        OwnerName = string.Empty;
        Phone = string.Empty;
        Address = string.Empty;
        LogoPath = string.Empty;

        Printer = "Not configured";
        PaperSize = "80mm";
        HeaderText = string.Empty;
        FooterText = string.Empty;
        ShowCustomer = false;
        ShowCashier = false;
        AutoPrint = false;

        RaiseEditableProperties();
    }

    private void ApplyUnavailable(string reason)
    {
        LoadError = reason;
        Users.Clear();
        Categories.Clear();
        Units.Clear();
        Backups.Clear();
        PermissionMatrix = [];

        _databaseName = "PostgreSQL";
        _databaseSize = "Unavailable";
        _databaseStatus = "Unavailable";
        _connectionStatus = "Database readiness unavailable";
        _workerStatus = "Unavailable";
        _licenseId = "Unavailable";
        _licenseStore = "Unavailable";
        _licenseModule = "Unavailable";
        _licenseExpiry = "Unavailable";
        _licensedTerminals = "Unavailable";
        _licenseStatus = "Unavailable";
        _lastBackupDisplay = "Unavailable";
        _maintenanceStatus = "Unavailable";

        RaiseBackendProperties();
        OnPropertyChanged(nameof(PermissionMatrix));
    }

    private void ShowProductionUnavailable(string message)
    {
        _toastService.Show(message, ToastTone.Warning);
    }

    private void OnThemeChanged(object? sender, AppTheme theme)
    {
        SelectedTheme = _themeService.CurrentTheme;
    }

    private void RaiseEditableProperties()
    {
        OnPropertyChanged(nameof(ShopName));
        OnPropertyChanged(nameof(OwnerName));
        OnPropertyChanged(nameof(Phone));
        OnPropertyChanged(nameof(Address));
        OnPropertyChanged(nameof(LogoPath));
        OnPropertyChanged(nameof(Printer));
        OnPropertyChanged(nameof(PaperSize));
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(FooterText));
        OnPropertyChanged(nameof(ShowCustomer));
        OnPropertyChanged(nameof(ShowCashier));
        OnPropertyChanged(nameof(AutoPrint));
    }

    private void RaiseBackendProperties()
    {
        OnPropertyChanged(nameof(LicenseId));
        OnPropertyChanged(nameof(LicenseStore));
        OnPropertyChanged(nameof(LicenseModule));
        OnPropertyChanged(nameof(LicenseExpiry));
        OnPropertyChanged(nameof(LicensedTerminals));
        OnPropertyChanged(nameof(LicenseStatus));
        OnPropertyChanged(nameof(DatabaseName));
        OnPropertyChanged(nameof(DatabaseSize));
        OnPropertyChanged(nameof(DatabaseStatus));
        OnPropertyChanged(nameof(ConnectionStatus));
        OnPropertyChanged(nameof(WorkerStatus));
        OnPropertyChanged(nameof(LastBackupDisplay));
        OnPropertyChanged(nameof(MaintenanceStatus));
        OnPropertyChanged(nameof(ProductionAuthorityNotice));
    }

    private void RaiseSectionProperties()
    {
        OnPropertyChanged(nameof(IsShop));
        OnPropertyChanged(nameof(IsReceipt));
        OnPropertyChanged(nameof(IsUsersAccess));
        OnPropertyChanged(nameof(IsCategoriesUnits));
        OnPropertyChanged(nameof(IsBackup));
        OnPropertyChanged(nameof(IsLicense));
        OnPropertyChanged(nameof(IsAppearance));
        OnPropertyChanged(nameof(IsDatabase));
    }

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
}

public enum SettingsEditorKind
{
    User,
    Category,
    Unit,
    LicenseImport
}

public sealed class SettingsEditorViewModel : ViewModelBase
{
    private readonly DemoSettingsState _state;
    private readonly IToastService _toast;
    private readonly Action _close;
    private readonly object? _existing;

    public SettingsEditorViewModel(
        SettingsEditorKind kind,
        DemoSettingsState state,
        IToastService toast,
        Action close,
        object? existing = null)
    {
        Kind = kind;
        _state = state;
        _toast = toast;
        _close = close;
        _existing = existing;

        Roles = ["Owner", "Manager", "Cashier"];
        Statuses = ["Active", "Inactive"];

        if (existing is SettingsUserRecord user)
        {
            Name = user.Name;
            Role = user.Role;
            Status = user.Status;
        }
        else if (existing is SettingsCategoryRecord category)
        {
            Name = category.Name;
        }
        else if (existing is SettingsUnitRecord unit)
        {
            Name = unit.Name;
            Symbol = unit.Symbol;
        }

        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(Cancel);
        BrowseLicenseFileCommand = new RelayCommand(BrowseLicenseFile);
    }

    public SettingsEditorKind Kind { get; }
    public IReadOnlyList<string> Roles { get; }
    public IReadOnlyList<string> Statuses { get; }

    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = "Cashier";
    public string Status { get; set; } = "Active";
    public string NewPin { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;

    public string PinLabel => _existing is null
        ? "PIN"
        : "New PIN (leave blank to keep)";

    public bool IsUser => Kind == SettingsEditorKind.User;
    public bool IsCategory => Kind == SettingsEditorKind.Category;
    public bool IsUnit => Kind == SettingsEditorKind.Unit;
    public bool IsLicenseImport => Kind == SettingsEditorKind.LicenseImport;

    public string Title => Kind switch
    {
        SettingsEditorKind.User => _existing is null ? "Add User" : "Edit User",
        SettingsEditorKind.Category => _existing is null ? "Add Category" : "Edit Category",
        SettingsEditorKind.Unit => _existing is null ? "Add Unit" : "Edit Unit",
        SettingsEditorKind.LicenseImport => "Import New License",
        _ => "Edit"
    };

    public string SaveButtonText => Kind switch
    {
        SettingsEditorKind.User => _existing is null ? "Add User" : "Save Changes",
        SettingsEditorKind.Category => "Save Category",
        SettingsEditorKind.Unit => "Save Unit",
        SettingsEditorKind.LicenseImport => "Activate License",
        _ => "Save"
    };

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand BrowseLicenseFileCommand { get; }

    private void Save()
    {
        try
        {
            switch (Kind)
            {
                case SettingsEditorKind.User:
                    SaveUser();
                    break;
                case SettingsEditorKind.Category:
                    _state.SaveCategory(_existing as SettingsCategoryRecord, Name);
                    _toast.Show("Category settings saved.", ToastTone.Success);
                    break;
                case SettingsEditorKind.Unit:
                    _state.SaveUnit(_existing as SettingsUnitRecord, Name, Symbol);
                    _toast.Show("Unit settings saved.", ToastTone.Success);
                    break;
                case SettingsEditorKind.LicenseImport:
                    ValidateLicenseFile();
                    _toast.Show(
                        "License import UI validated. Licensing backend is not attached yet.",
                        ToastTone.Info);
                    break;
            }

            _close();
        }
        catch (Exception ex)
        {
            _toast.Show(ex.Message, ToastTone.Warning);
        }
    }

    private void SaveUser()
    {
        if (_existing is null && string.IsNullOrWhiteSpace(NewPin))
        {
            throw new InvalidOperationException("A PIN is required when creating a user.");
        }

        _state.SaveUser(
            _existing as SettingsUserRecord,
            Name,
            Role,
            string.Equals(Status, "Active", StringComparison.OrdinalIgnoreCase));

        _toast.Show(
            _existing is null ? "User added." : "User settings updated.",
            ToastTone.Success);

        NewPin = string.Empty;
    }

    private void Cancel()
    {
        NewPin = string.Empty;
        _close();
    }

    private void BrowseLicenseFile()
    {
        if (Kind != SettingsEditorKind.LicenseImport)
        {
            return;
        }

        var picker = new OpenFileDialog
        {
            Title = "Select Edge Retails License",
            Filter = "License Files (*.lic;*.key)|*.lic;*.key"
        };

        if (picker.ShowDialog() == true)
        {
            FilePath = picker.FileName;
            OnPropertyChanged(nameof(FilePath));
        }
    }

    private void ValidateLicenseFile()
    {
        if (string.IsNullOrWhiteSpace(FilePath))
        {
            throw new InvalidOperationException("Select a license file first.");
        }

        if (!File.Exists(FilePath))
        {
            throw new InvalidOperationException("The selected license file no longer exists.");
        }

        var extension = Path.GetExtension(FilePath);
        if (!string.Equals(extension, ".lic", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".key", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("License file must use .lic or .key format.");
        }
    }
}

public sealed class SettingsConfirmViewModel : ViewModelBase
{
    private readonly Action<string?> _confirm;
    private readonly Action _close;
    private string? _selectedOption;

    public SettingsConfirmViewModel(
        string title,
        string message,
        string confirmText,
        Action<string?> confirm,
        Action close,
        IEnumerable<string>? options = null)
    {
        Title = title;
        Message = message;
        ConfirmText = confirmText;
        _confirm = confirm;
        _close = close;
        Options = new ObservableCollection<string>(options ?? []);
        SelectedOption = Options.FirstOrDefault();
        ConfirmCommand = new RelayCommand(ExecuteConfirm);
        CancelCommand = new RelayCommand(_close);
    }

    public string Title { get; }
    public string Message { get; }
    public string ConfirmText { get; }
    public ObservableCollection<string> Options { get; }
    public bool HasOptions => Options.Count > 0;

    public string? SelectedOption
    {
        get => _selectedOption;
        set => SetProperty(ref _selectedOption, value);
    }

    public ICommand ConfirmCommand { get; }
    public ICommand CancelCommand { get; }

    private void ExecuteConfirm()
    {
        if (HasOptions && string.IsNullOrWhiteSpace(SelectedOption))
        {
            return;
        }

        _confirm(SelectedOption);
        _close();
    }
}
