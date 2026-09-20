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
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly DemoSettingsState _state = DemoSettingsState.Instance;
    private readonly IThemeService _themeService;
    private readonly IDialogService _dialogService;
    private readonly IToastService _toastService;

    private SettingsSection _selectedSection = SettingsSection.Shop;
    private AppTheme _selectedTheme;

    public SettingsViewModel(
        IThemeService themeService,
        IDialogService dialogService,
        IToastService toastService)
    {
        _themeService = themeService;
        _dialogService = dialogService;
        _toastService = toastService;
        _selectedTheme = themeService.CurrentTheme;

        LoadEditableState();

        SelectSectionCommand = new RelayCommand<string>(SelectSection);
        SaveShopCommand = new RelayCommand(SaveShop);
        SaveReceiptCommand = new RelayCommand(SaveReceipt);
        UploadLogoCommand = new RelayCommand(ChooseLogo);
        AddUserCommand = new RelayCommand(() => OpenEditor(SettingsEditorKind.User));
        EditUserCommand = new RelayCommand<SettingsUserRecord>(
            user => OpenEditor(SettingsEditorKind.User, user));
        AddCategoryCommand = new RelayCommand(
            () => OpenEditor(SettingsEditorKind.Category));
        EditCategoryCommand = new RelayCommand<SettingsCategoryRecord>(
            category => OpenEditor(SettingsEditorKind.Category, category));
        ToggleCategoryCommand = new RelayCommand<SettingsCategoryRecord>(ToggleCategory);
        AddUnitCommand = new RelayCommand(() => OpenEditor(SettingsEditorKind.Unit));
        EditUnitCommand = new RelayCommand<SettingsUnitRecord>(
            unit => OpenEditor(SettingsEditorKind.Unit, unit));
        ToggleUnitCommand = new RelayCommand<SettingsUnitRecord>(ToggleUnit);
        BackupNowCommand = new RelayCommand(PreviewBackup);
        RestoreCommand = new RelayCommand(OpenRestore);
        ImportLicenseCommand = new RelayCommand(
            () => OpenEditor(SettingsEditorKind.LicenseImport));
        ApplyThemeCommand = new RelayCommand(ApplyTheme);
        RefreshDiagnosticsCommand = new RelayCommand(
            () => _toastService.Show(
                "Diagnostics preview refreshed. Backend health provider is not attached yet.",
                ToastTone.Info));

        _state.StateChanged += OnStateChanged;
        _themeService.ThemeChanged += OnThemeChanged;
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

    public ObservableCollection<SettingsUserRecord> Users => _state.Users;
    public ObservableCollection<SettingsCategoryRecord> Categories => _state.Categories;
    public ObservableCollection<SettingsUnitRecord> Units => _state.Units;
    public ObservableCollection<SettingsBackupRecord> Backups => _state.Backups;
    public IReadOnlyList<SettingsPermissionRow> PermissionMatrix => _state.PermissionMatrix;

    public string ShopName { get; set; } = string.Empty;
    public string OwnerName { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string LogoPath { get; set; } = string.Empty;

    public IReadOnlyList<string> Printers { get; } =
        ["Thermal 80mm", "Thermal 58mm", "Windows Default"];
    public IReadOnlyList<string> PaperSizes { get; } = ["80mm", "58mm"];

    public string Printer { get; set; } = "Thermal 80mm";
    public string PaperSize { get; set; } = "80mm";
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

    public string LicenseId => _state.LicenseId;
    public string LicenseStore => _state.LicenseStore;
    public string LicenseModule => _state.LicenseModule;
    public string LicenseExpiry => _state.LicenseExpiry.ToString("dd MMM yyyy");
    public string LicensedTerminals => _state.LicensedTerminals.ToString();
    public string LicenseStatus => _state.LicenseStatus;

    public string DatabaseName => _state.DatabaseName;
    public string DatabaseSize => _state.DatabaseSize;
    public string DatabaseStatus => _state.DatabaseStatus;
    public string ConnectionStatus => _state.ConnectionStatus;
    public string WorkerStatus => _state.WorkerStatus;
    public string LastBackupDisplay =>
        $"Preview · {_state.LastBackup:yyyy-MM-dd hh:mm tt}";

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

    private void SaveShop()
    {
        try
        {
            _state.SaveShop(ShopName, OwnerName, Phone, Address, LogoPath);
            _toastService.Show("Shop settings saved.", ToastTone.Success);
        }
        catch (Exception ex)
        {
            _toastService.Show(ex.Message, ToastTone.Warning);
        }
    }

    private void SaveReceipt()
    {
        _state.SaveReceipt(
            Printer,
            PaperSize,
            HeaderText,
            FooterText,
            ShowCustomer,
            ShowCashier,
            AutoPrint);
        _toastService.Show("Receipt settings saved.", ToastTone.Success);
    }

    private void ChooseLogo()
    {
        var picker = new OpenFileDialog
        {
            Title = "Select Shop Logo",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.webp"
        };

        if (picker.ShowDialog() == true)
        {
            LogoPath = picker.FileName;
            OnPropertyChanged(nameof(LogoPath));
        }
    }

    private void OpenEditor(SettingsEditorKind kind, object? existing = null)
    {
        _dialogService.Show(new SettingsEditorViewModel(
            kind,
            _state,
            _toastService,
            _dialogService.Close,
            existing));
    }

    private void ToggleCategory(SettingsCategoryRecord? category)
    {
        if (category is null)
        {
            return;
        }

        if (!category.IsActive)
        {
            _state.SetCategoryActive(category, true);
            _toastService.Show("Category reactivated.", ToastTone.Success);
            return;
        }

        OpenDeactivateConfirm(
            "Deactivate Category",
            $"Deactivate {category.Name}? Existing products keep their historical category.",
            () => _state.SetCategoryActive(category, false));
    }

    private void ToggleUnit(SettingsUnitRecord? unit)
    {
        if (unit is null)
        {
            return;
        }

        if (!unit.IsActive)
        {
            _state.SetUnitActive(unit, true);
            _toastService.Show("Unit reactivated.", ToastTone.Success);
            return;
        }

        OpenDeactivateConfirm(
            "Deactivate Unit",
            $"Deactivate {unit.Name}? Existing references remain intact.",
            () => _state.SetUnitActive(unit, false));
    }

    private void OpenDeactivateConfirm(
        string title,
        string message,
        Action action)
    {
        _dialogService.Show(new SettingsConfirmViewModel(
            title,
            message,
            "Deactivate",
            _ =>
            {
                action();
                _toastService.Show("Setting deactivated.", ToastTone.Success);
            },
            _dialogService.Close));
    }

    private void PreviewBackup()
    {
        _toastService.Show(
            "Backup command preview only. Worker integration is deferred until backend attachment.",
            ToastTone.Info);
    }

    private void OpenRestore()
    {
        var options = _state.Backups.Select(backup => backup.Summary).ToArray();

        _dialogService.Show(new SettingsConfirmViewModel(
            "Restore Backup",
            "Restoring a backup will overwrite current data. Select a verified backup point.",
            "Confirm Restore",
            selected =>
            {
                _toastService.Show(
                    $"Restore confirmation captured for {selected}. Backend restore is not attached yet.",
                    ToastTone.Info);
            },
            _dialogService.Close,
            options));
    }

    private void ApplyTheme()
    {
        _themeService.ApplyTheme(SelectedTheme);
        _toastService.Show(
            $"{SelectedTheme} theme applied.",
            ToastTone.Success);
    }

    private void OnThemeChanged(object? sender, AppTheme theme)
    {
        SelectedTheme = _themeService.CurrentTheme;
    }
    private void OnStateChanged(object? sender, EventArgs e)
    {
        foreach (var category in Categories)
        {
            category.RefreshCount();
        }

        OnPropertyChanged(nameof(LicenseId));
        OnPropertyChanged(nameof(LicenseStore));
        OnPropertyChanged(nameof(LicenseModule));
        OnPropertyChanged(nameof(LicenseExpiry));
        OnPropertyChanged(nameof(LicensedTerminals));
        OnPropertyChanged(nameof(LicenseStatus));
    }

    private void LoadEditableState()
    {
        ShopName = _state.ShopName;
        OwnerName = _state.OwnerName;
        Phone = _state.Phone;
        Address = _state.Address;
        LogoPath = _state.LogoPath;

        Printer = _state.Printer;
        PaperSize = _state.PaperSize;
        HeaderText = _state.HeaderText;
        FooterText = _state.FooterText;
        ShowCustomer = _state.ShowCustomer;
        ShowCashier = _state.ShowCashier;
        AutoPrint = _state.AutoPrint;
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
}
