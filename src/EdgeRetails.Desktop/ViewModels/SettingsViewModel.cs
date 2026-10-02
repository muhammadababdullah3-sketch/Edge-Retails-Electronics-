using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using EdgeRetails.Application.Production.Backup;
using EdgeRetails.Application.Production.Printing;
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
    private readonly IBackendBackupRestoreService? _backupRestoreService;
    public IWorkstationPrinterSettings? WorkstationPrinterSettings { get; init; }
    private readonly DemoSettingsState? _previewState;
    private CancellationTokenSource? _backupLoadCancellation;

    private SettingsSection _selectedSection = SettingsSection.Shop;
    private AppTheme _selectedTheme;
    private bool _isLoading;
    private bool _isBackupBusy;
    private string? _loadError;
    private string? _printerSettingsError;
    private string _printer = string.Empty;
    private string _paperSize = "80mm";
    private string _backupOperationStatus = "Backup history unavailable";
    private SettingsBackupRecord? _selectedBackup;
    private RestoreStatusApiResponse? _activeRestore;

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
    private string _backupVerificationStatus = "Backup integrity status unavailable";
    private string _maintenanceStatus = "Unavailable";

    public SettingsViewModel(
        IThemeService themeService,
        IDialogService dialogService,
        IToastService toastService,
        IBackendSettingsService? backendService = null,
        IBackendBackupRestoreService? backupRestoreService = null,
        IWorkstationPrinterSettings? workstationPrinterSettings = null)
    {
        _themeService = themeService;
        _dialogService = dialogService;
        _toastService = toastService;
        _backendService = backendService;
        _backupRestoreService = backupRestoreService;
        WorkstationPrinterSettings = workstationPrinterSettings;
        _previewState = ResolvePreviewState(backendService);
        _selectedTheme = themeService.CurrentTheme;

        Users = [];
        Categories = [];
        Units = [];
        Backups = [];
        Printers = [];
        PermissionMatrix = [];
        PaperSizes = ["80mm", "58mm"];

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
        BackupNowCommand = new RelayCommand(() => _ = BackupNowAsync(), () => !IsBackupBusy && _backupRestoreService is not null);
        RestoreCommand = new RelayCommand(OpenRestore, () => !IsBackupBusy && _backupRestoreService is not null && SelectedBackup?.BackupId is not null);
        RecoverRestoreCommand = new RelayCommand(() => _ = RecoverRestoreAsync(),
            () => !IsBackupBusy && _activeRestore is { Session.State: RestoreSessionState.Preparing, Session.ClientOperationId: not null });
        CutoverRestoreCommand = new RelayCommand(OpenCutoverConfirmation,
            () => !IsBackupBusy && _activeRestore is { Session.State: RestoreSessionState.Prepared, CutoverConfirmation.Length: > 0 });
        DiscardRestoreCommand = new RelayCommand(OpenDiscardConfirmation,
            () => !IsBackupBusy && _activeRestore is { Session.State: RestoreSessionState.Prepared, DiscardConfirmation.Length: > 0 });
        RefreshBackupHistoryCommand = new RelayCommand(() => _ = RefreshBackupHistoryAsync(true),
            () => !IsBackupBusy && _backupRestoreService is not null);
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

        if (_backupRestoreService is not null)
        {
            _ = RefreshBackupHistoryAsync(false);
            _ = ReconcilePendingRestorePreparationAsync();
        }

        if (WorkstationPrinterSettings is not null)
        {
            _ = LoadWorkstationPrinterSettingsAsync();
        }
    }

    public void Dispose()
    {
        _backupLoadCancellation?.Cancel();
        _backupLoadCancellation?.Dispose();
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

    public ObservableCollection<string> Printers { get; }
    public IReadOnlyList<string> PaperSizes { get; }

    public string Printer
    {
        get => _printer;
        set => SetProperty(ref _printer, value);
    }
    public string PaperSize
    {
        get => _paperSize;
        set => SetProperty(ref _paperSize, value);
    }
    public string? PrinterSettingsError
    {
        get => _printerSettingsError;
        private set
        {
            if (SetProperty(ref _printerSettingsError, value))
            {
                OnPropertyChanged(nameof(HasPrinterSettingsError));
            }
        }
    }
    public bool HasPrinterSettingsError => !string.IsNullOrWhiteSpace(PrinterSettingsError);
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
    public string BackupVerificationStatus
    {
        get => _backupVerificationStatus;
        private set => SetProperty(ref _backupVerificationStatus, value);
    }
    public string BackupOperationStatus
    {
        get => _backupOperationStatus;
        private set
        {
            SetProperty(ref _backupOperationStatus, value);
        }
    }
    public string RestoreStatusDisplay => _activeRestore is null
        ? "No restore is prepared."
        : $"Restore {_activeRestore.Session.RestoreId:D} · {_activeRestore.Session.State}";
    public bool IsRestorePrepared => _activeRestore?.Session.State == RestoreSessionState.Prepared;
    public bool IsRestorePreparing => _activeRestore?.Session.State == RestoreSessionState.Preparing;
    public bool IsRestoreRecoveryRequired => _activeRestore?.Session.State == RestoreSessionState.RecoveryRequired;
    public bool IsBackupBusy
    {
        get => _isBackupBusy;
        private set
        {
            if (SetProperty(ref _isBackupBusy, value))
            {
                NotifyBackupCommands();
            }
        }
    }
    public SettingsBackupRecord? SelectedBackup
    {
        get => _selectedBackup;
        set
        {
            if (SetProperty(ref _selectedBackup, value))
            {
                RestoreCommand.NotifyCanExecuteChanged();
            }
        }
    }
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
    public RelayCommand BackupNowCommand { get; }
    public RelayCommand RestoreCommand { get; }
    public RelayCommand RecoverRestoreCommand { get; }
    public RelayCommand CutoverRestoreCommand { get; }
    public RelayCommand DiscardRestoreCommand { get; }
    public RelayCommand RefreshBackupHistoryCommand { get; }
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
            if (parsed == SettingsSection.Backup)
            {
                _ = RefreshBackupHistoryAsync(false);
            }
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
                _toastService.Show(
                    DesktopErrorPresentation.ForException(ex, "Shop settings could not be saved."),
                    ToastTone.Warning);
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
                DesktopErrorPresentation.ForException(ex, "Shop settings were not saved."),
                ToastTone.Danger);
        }
    }

    private async Task SaveReceiptAsync()
    {
        if (WorkstationPrinterSettings is null)
        {
            const string message = "Workstation printer settings are unavailable. Printer selection was not saved.";
            PrinterSettingsError = message;
            _toastService.Show(message, ToastTone.Danger);
            return;
        }

        if (string.IsNullOrWhiteSpace(Printer) ||
            !Printers.Contains(Printer, StringComparer.OrdinalIgnoreCase))
        {
            PrinterSettingsError = Printers.Count == 0
                ? "Windows did not report any installed printers. Connect or install a printer before saving receipt settings."
                : "Select an installed Windows printer before saving receipt settings.";
            _toastService.Show(PrinterSettingsError!, ToastTone.Warning);
            return;
        }

        var paper = PaperSize switch
        {
            "58mm" => PaperKind.Thermal58Mm,
            "80mm" => PaperKind.Thermal80Mm,
            _ => (PaperKind?)null
        };
        if (paper is null)
        {
            PrinterSettingsError = "Select either 58mm or 80mm receipt media.";
            _toastService.Show(PrinterSettingsError!, ToastTone.Warning);
            return;
        }

        PrinterSettingsError = null;
        var profileToSave = new PrinterProfile(
            WindowsWorkstationPrinterSettings.ReceiptProfileName,
            Printer,
            paper.Value,
            Copies: 1,
            ShowPreviewBeforePrint: true);
        try
        {
            var validation = await WorkstationPrinterSettings.ValidateReceiptProfileAsync(profileToSave);
            if (!validation.Supported)
            {
                PrinterSettingsError = validation.Message ?? "The selected printer does not support this receipt profile.";
                _toastService.Show(PrinterSettingsError!, ToastTone.Warning);
                return;
            }

            await WorkstationPrinterSettings.SaveReceiptProfileAsync(profileToSave);
        }
        catch (Exception ex)
        {
            PrinterSettingsError = DesktopErrorPresentation.ForException(ex, "Workstation printer settings could not be saved.");
            _toastService.Show(PrinterSettingsError!, ToastTone.Danger);
            return;
        }

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
            PrinterSettingsError = "Printer and media preferences were saved on this workstation. Receipt template changes were not saved because the authoritative backend service is unavailable.";
            _toastService.Show(PrinterSettingsError!, ToastTone.Warning);
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
                "Receipt template saved. Printer and media preferences were saved on this workstation.",
                ToastTone.Success);
            await RefreshBackendAsync(false);
        }
        catch (Exception ex)
        {
            PrinterSettingsError = "Workstation printer and media preferences were saved, but the receipt template was not: " +
                DesktopErrorPresentation.ForException(ex, "Receipt template save failed.");
            _toastService.Show(PrinterSettingsError!, ToastTone.Danger);
        }
    }

    private async Task LoadWorkstationPrinterSettingsAsync()
    {
        try
        {
            var printers = await WorkstationPrinterSettings!.GetInstalledPrinterNamesAsync();
            Printers.Clear();
            foreach (var name in printers.Where(name => !string.IsNullOrWhiteSpace(name)))
            {
                Printers.Add(name);
            }

            var profile = await WorkstationPrinterSettings.GetReceiptProfileAsync();
            if (profile is not null)
            {
                Printer = profile.PrinterName;
                PaperSize = profile.Paper switch
                {
                    PaperKind.Thermal58Mm => "58mm",
                    PaperKind.Thermal80Mm => "80mm",
                    _ => "80mm"
                };
            }
            else if (Printers.Count > 0)
            {
                Printer = Printers[0];
            }

            PrinterSettingsError = Printers.Count == 0
                ? "No installed Windows printers were found. Install or connect a printer to configure receipt output."
                : profile is not null && !Printers.Contains(profile.PrinterName, StringComparer.OrdinalIgnoreCase)
                    ? $"Saved printer '{profile.PrinterName}' is not currently installed on this workstation. Select an installed printer to replace it."
                    : null;
        }
        catch (Exception ex)
        {
            PrinterSettingsError = DesktopErrorPresentation.ForException(ex, "Installed printers or saved workstation settings could not be loaded.");
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
        if (kind == SettingsEditorKind.User && existing is null && _backendService?.SupportsCashierCreation == true)
        {
            _dialogService.Show(new SettingsEditorViewModel(
                _backendService, _toastService, _dialogService.Close,
                () => RefreshBackendAsync(false)));
            return;
        }
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

    private async Task BackupNowAsync()
    {
#if DEBUG
        if (_previewState is not null && _backupRestoreService is null)
        {
            _toastService.Show(
                "Preview only. No backup was created. Worker integration is deferred until backend attachment.",
                ToastTone.Info);
            return;
        }
#endif
        if (_backupRestoreService is null)
        {
            ShowProductionUnavailable("The authoritative backup service is unavailable. No backup was started.");
            return;
        }

        IsBackupBusy = true;
        BackupOperationStatus = "Creating encrypted database backup…";
        try
        {
            var created = await _backupRestoreService.CreateBackupAsync();
            AddOrReplaceBackup(created);
            BackupOperationStatus = created.CreatedAtUtc.ToLocalTime().ToString("Backup completed · dd MMM yyyy hh:mm tt");
            _toastService.Show("Encrypted PostgreSQL backup completed and verified.", ToastTone.Success);
        }
        catch (BackendOperationException ex)
        {
            BackupOperationStatus = DesktopErrorPresentation.ForException(ex, "The backup could not be completed.");
            _toastService.Show(BackupOperationStatus, ToastTone.Warning);
        }
        finally
        {
            IsBackupBusy = false;
        }
    }

    private void OpenRestore()
    {
#if DEBUG
        if (_previewState is not null && _backupRestoreService is null)
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
        if (_backupRestoreService is null)
        {
            ShowProductionUnavailable("The authoritative restore service is unavailable. No restore was prepared.");
            return;
        }

        if (SelectedBackup?.BackupId is not Guid backupId || backupId == Guid.Empty)
        {
            BackupOperationStatus = "Select a verified backup before preparing a restore.";
            _toastService.Show(BackupOperationStatus, ToastTone.Warning);
            return;
        }

        _ = PrepareRestoreAsync(backupId);
    }

    private async Task PrepareRestoreAsync(Guid backupId)
    {
        if (_backupRestoreService is null)
        {
            return;
        }

        IsBackupBusy = true;
        BackupOperationStatus = "Validating backup and preparing an isolated restore…";
        try
        {
            var status = await _backupRestoreService.PrepareRestoreAsync(backupId);
            SetActiveRestore(status);
            BackupOperationStatus = RestoreStatusMessage(status.Session.State);
            _toastService.Show(BackupOperationStatus, status.Session.State == RestoreSessionState.Prepared
                ? ToastTone.Success
                : ToastTone.Warning);
        }
        catch (BackendOperationException ex)
        {
            BackupOperationStatus = DesktopErrorPresentation.ForException(ex, "Restore preparation could not be confirmed.");
            _toastService.Show(BackupOperationStatus, ToastTone.Warning);
        }
        finally
        {
            IsBackupBusy = false;
        }
    }

    private async Task RefreshBackupHistoryAsync(bool showToast)
    {
        if (_backupRestoreService is null)
        {
            return;
        }

        _backupLoadCancellation?.Cancel();
        _backupLoadCancellation?.Dispose();
        _backupLoadCancellation = new CancellationTokenSource();
        var cancellationToken = _backupLoadCancellation.Token;
        try
        {
            var selectedId = SelectedBackup?.BackupId;
            IReadOnlyList<SafeBackupRecord> history;
            try
            {
                var diagnostics = await _backupRestoreService.LoadHistoryDiagnosticsAsync(cancellationToken);
                history = diagnostics.VerifiedBackups;
                BackupVerificationStatus = diagnostics.InvalidArtifactCount == 0
                    ? "All discovered backup artifacts passed integrity checks."
                    : $"{diagnostics.InvalidArtifactCount} artifact(s) failed integrity checks and are unavailable for restore. " +
                      string.Join(" · ", diagnostics.Issues.Select(issue => $"{issue.Code} ({issue.Count})"));
            }
            catch (NotSupportedException)
            {
                history = await _backupRestoreService.LoadHistoryAsync(cancellationToken);
                BackupVerificationStatus = "The Server does not provide backup integrity diagnostics.";
            }
            catch (BackendOperationException)
            {
                history = await _backupRestoreService.LoadHistoryAsync(cancellationToken);
                BackupVerificationStatus = "Backup integrity diagnostics are unavailable; the verified backup list was refreshed.";
            }

            ReplaceCollection(Backups, history.OrderByDescending(item => item.CreatedAtUtc).Select(ToSettingsBackupRecord));
            SelectedBackup = selectedId is Guid id
                ? Backups.FirstOrDefault(item => item.BackupId == id) ?? Backups.FirstOrDefault()
                : Backups.FirstOrDefault();

            if (history.Count > 0)
            {
                _lastBackupDisplay = history.MaxBy(item => item.CreatedAtUtc)!.CreatedAtUtc.ToLocalTime()
                    .ToString("dd MMM yyyy hh:mm tt");
            }
            else
            {
                _lastBackupDisplay = "No verified backups";
            }

            BackupOperationStatus = history.Count == 0
                ? "No verified backups are available."
                : $"{history.Count} verified backup{(history.Count == 1 ? string.Empty : "s")} available.";
            RaiseBackendProperties();
            if (showToast)
            {
                _toastService.Show("Verified backup history refreshed from the Server.", ToastTone.Success);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (BackendOperationException ex)
        {
            BackupOperationStatus = DesktopErrorPresentation.ForException(ex, "Backup history is unavailable.");
            if (showToast)
            {
                _toastService.Show(BackupOperationStatus, ToastTone.Warning);
            }
        }
    }

    private async Task ReconcilePendingRestorePreparationAsync()
    {
        if (_backupRestoreService is null)
        {
            return;
        }

        try
        {
            // This is a read-only status lookup. Recovery remains an explicit operator action.
            var status = await _backupRestoreService.ReconcilePendingRestorePreparationAsync();
            if (status is null)
            {
                return;
            }

            SetActiveRestore(status);
            BackupOperationStatus = RestoreStatusMessage(status.Session.State);
        }
        catch (BackendOperationException ex)
        {
            BackupOperationStatus = DesktopErrorPresentation.ForException(
                ex,
                "The previous restore operation could not be reconciled. Its saved operation identity remains available.");
        }
    }

    private async Task RecoverRestoreAsync()
    {
        if (_backupRestoreService is null || _activeRestore?.Session.ClientOperationId is not Guid operationId)
        {
            return;
        }

        IsBackupBusy = true;
        try
        {
            var status = await _backupRestoreService.RecoverPreparationAsync(operationId);
            SetActiveRestore(status);
            BackupOperationStatus = RestoreStatusMessage(status.Session.State);
            _toastService.Show(BackupOperationStatus, ToastTone.Warning);
        }
        catch (BackendOperationException ex)
        {
            BackupOperationStatus = DesktopErrorPresentation.ForException(ex, "Restore recovery could not be confirmed.");
            _toastService.Show(BackupOperationStatus, ToastTone.Warning);
        }
        finally
        {
            IsBackupBusy = false;
        }
    }

    private void OpenCutoverConfirmation()
    {
        if (_activeRestore?.CutoverConfirmation is not { Length: > 0 } phrase)
        {
            return;
        }

        _dialogService.Show(new TypedRestoreConfirmationViewModel(
            "Confirm restore cutover",
            "The verified restore will become the active database. The current production database will be preserved under a recovery name.",
            phrase,
            "Cut Over",
            value => _ = CutoverRestoreAsync(value),
            _dialogService.Close));
    }

    private void OpenDiscardConfirmation()
    {
        if (_activeRestore?.DiscardConfirmation is not { Length: > 0 } phrase)
        {
            return;
        }

        _dialogService.Show(new TypedRestoreConfirmationViewModel(
            "Discard prepared restore",
            "The isolated staging database for this prepared restore will be removed. The active production database remains unchanged.",
            phrase,
            "Discard Staging",
            value => _ = DiscardPreparedRestoreAsync(value),
            _dialogService.Close));
    }

    private async Task CutoverRestoreAsync(string confirmation)
        => await RunPreparedRestoreMutationAsync(
            confirmation,
            cutover: true);

    private async Task DiscardPreparedRestoreAsync(string confirmation)
        => await RunPreparedRestoreMutationAsync(
            confirmation,
            cutover: false);

    private async Task RunPreparedRestoreMutationAsync(string confirmation, bool cutover)
    {
        if (_backupRestoreService is null || _activeRestore is null)
        {
            return;
        }

        IsBackupBusy = true;
        try
        {
            var status = cutover
                ? await _backupRestoreService.CutoverAsync(_activeRestore.Session.RestoreId, confirmation)
                : await _backupRestoreService.DiscardAsync(_activeRestore.Session.RestoreId, confirmation);
            SetActiveRestore(status);
            BackupOperationStatus = RestoreStatusMessage(status.Session.State);
            _toastService.Show(BackupOperationStatus, status.Session.State == RestoreSessionState.Completed
                ? ToastTone.Success
                : ToastTone.Info);
            await RefreshBackupHistoryAsync(false);
        }
        catch (BackendOperationException ex)
        {
            BackupOperationStatus = DesktopErrorPresentation.ForException(ex, "The restore operation could not be confirmed.");
            _toastService.Show(BackupOperationStatus, ToastTone.Warning);
        }
        finally
        {
            IsBackupBusy = false;
        }
    }

    private void SetActiveRestore(RestoreStatusApiResponse status)
    {
        _activeRestore = status;
        OnPropertyChanged(nameof(RestoreStatusDisplay));
        OnPropertyChanged(nameof(IsRestorePrepared));
        OnPropertyChanged(nameof(IsRestorePreparing));
        OnPropertyChanged(nameof(IsRestoreRecoveryRequired));
        RecoverRestoreCommand.NotifyCanExecuteChanged();
        CutoverRestoreCommand.NotifyCanExecuteChanged();
        DiscardRestoreCommand.NotifyCanExecuteChanged();
    }

    private void AddOrReplaceBackup(SafeBackupRecord backup)
    {
        var row = ToSettingsBackupRecord(backup);
        var existing = Backups.FirstOrDefault(item => item.BackupId == backup.BackupId);
        if (existing is not null)
        {
            Backups.Remove(existing);
        }

        Backups.Insert(0, row);
        SelectedBackup = row;
        _lastBackupDisplay = row.TimestampDisplay;
        RaiseBackendProperties();
    }

    private static SettingsBackupRecord ToSettingsBackupRecord(SafeBackupRecord backup)
        => new()
        {
            BackupId = backup.BackupId,
            Timestamp = backup.CreatedAtUtc.LocalDateTime,
            Type = $"PostgreSQL {backup.FormatVersion}",
            Status = "Verified",
            Size = FormatBytes(backup.SizeBytes),
            Protection = backup.Protection,
            PostgreSqlVersion = backup.PostgreSqlServerVersion
        };

    private static string FormatBytes(long bytes)
    {
        if (bytes < 0)
        {
            return "Unavailable";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }

    private static string RestoreStatusMessage(RestoreSessionState state)
        => state switch
        {
            RestoreSessionState.Prepared => "Restore validated and prepared. Review the verified backup, then choose Cut Over or Discard.",
            RestoreSessionState.Preparing => "Restore preparation is still resolving. Recover this operation before retrying.",
            RestoreSessionState.Completed => "Restore cutover completed. The previous production database was preserved.",
            RestoreSessionState.Discarded => "Prepared restore staging was discarded; production remains unchanged.",
            RestoreSessionState.RecoveryRequired => "Restore recovery requires operator intervention. No automatic retry was attempted.",
            RestoreSessionState.RolledBack => "Restore cutover was rolled back; recovery status is available on the Server.",
            _ => "Restore operation is in progress. Refresh status before continuing."
        };

    private void NotifyBackupCommands()
    {
        BackupNowCommand?.NotifyCanExecuteChanged();
        RestoreCommand?.NotifyCanExecuteChanged();
        RecoverRestoreCommand?.NotifyCanExecuteChanged();
        CutoverRestoreCommand?.NotifyCanExecuteChanged();
        DiscardRestoreCommand?.NotifyCanExecuteChanged();
        RefreshBackupHistoryCommand?.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsBackupBusy));
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
            ApplyUnavailable(DesktopErrorPresentation.ForException(
                ex,
                "Settings backend is unavailable. Check the connection and try again."));
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
        _lastBackupDisplay = _backupRestoreService is null
            ? snapshot.LastBackupDisplay
            : Backups.Count == 0
                ? "No verified backups"
                : Backups.MaxBy(item => item.Timestamp)!.TimestampDisplay;
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

        Printer = string.Empty;
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
        OnPropertyChanged(nameof(RestoreStatusDisplay));
        OnPropertyChanged(nameof(IsRestorePrepared));
        OnPropertyChanged(nameof(IsRestorePreparing));
        OnPropertyChanged(nameof(IsRestoreRecoveryRequired));
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
    private readonly DemoSettingsState? _state;
    private readonly IBackendSettingsService? _production;
    private readonly Func<Task>? _refresh;
    private readonly IToastService _toast;
    private readonly Action _close;
    private readonly object? _existing;
    private string _newPin = string.Empty;
    private bool _isSaving;
    private bool _outcomeUnknown;
    private string? _submittedName;
    private string? _userOperationMessage;

    public SettingsEditorViewModel(IBackendSettingsService production, IToastService toast,
        Action close, Func<Task> refresh)
    {
        _production = production;
        _toast = toast;
        _close = close;
        _refresh = refresh;
        Kind = SettingsEditorKind.User;
        Roles = ["Cashier"];
        Statuses = ["Active"];
        SaveCommand = new RelayCommand(() => _ = SaveProductionUserAsync(), () => !IsSaving);
        CancelCommand = new RelayCommand(Cancel, () => !IsSaving);
        CheckStatusCommand = new RelayCommand(() => _ = CheckCreationStatusAsync(), () => !IsSaving && HasUnknownOutcome);
        BrowseLicenseFileCommand = new RelayCommand(() => { });
    }

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
        CheckStatusCommand = new RelayCommand(() => { }, () => false);
    }

    public SettingsEditorKind Kind { get; }
    public IReadOnlyList<string> Roles { get; }
    public IReadOnlyList<string> Statuses { get; }

    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = "Cashier";
    public string Status { get; set; } = "Active";
    public string NewPin { get => _newPin; set => SetProperty(ref _newPin, value); }
    public Guid ClientOperationId { get; } = Guid.CreateVersion7();
    public bool IsSaving
    {
        get => _isSaving;
        private set
        {
            if (!SetProperty(ref _isSaving, value))
            {
                return;
            }
            OnPropertyChanged(nameof(IsUserFieldsEnabled));
            OnPropertyChanged(nameof(IsPinEnabled));
            (SaveCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (CancelCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (CheckStatusCommand as RelayCommand)?.NotifyCanExecuteChanged();
        }
    }
    public bool IsUserFieldsEnabled => !IsSaving && !HasUnknownOutcome;
    public bool IsPinEnabled => !IsSaving;
    public bool HasUnknownOutcome
    {
        get => _outcomeUnknown;
        private set
        {
            if (!SetProperty(ref _outcomeUnknown, value))
            {
                return;
            }
            OnPropertyChanged(nameof(IsUserFieldsEnabled));
            OnPropertyChanged(nameof(SaveButtonText));
            (CheckStatusCommand as RelayCommand)?.NotifyCanExecuteChanged();
        }
    }
    public string? UserOperationMessage { get => _userOperationMessage; private set => SetProperty(ref _userOperationMessage, value); }
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
        SettingsEditorKind.User => _production is not null ? "Add Cashier" : _existing is null ? "Add User" : "Edit User",
        SettingsEditorKind.Category => _existing is null ? "Add Category" : "Edit Category",
        SettingsEditorKind.Unit => _existing is null ? "Add Unit" : "Edit Unit",
        SettingsEditorKind.LicenseImport => "Import New License",
        _ => "Edit"
    };

    public string SaveButtonText => Kind switch
    {
        SettingsEditorKind.User => HasUnknownOutcome ? "Retry Same Creation" : _existing is null ? "Add User" : "Save Changes",
        SettingsEditorKind.Category => "Save Category",
        SettingsEditorKind.Unit => "Save Unit",
        SettingsEditorKind.LicenseImport => "Activate License",
        _ => "Save"
    };

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand BrowseLicenseFileCommand { get; }
    public ICommand CheckStatusCommand { get; }

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
                    _state!.SaveCategory(_existing as SettingsCategoryRecord, Name);
                    _toast.Show("Category settings saved.", ToastTone.Success);
                    break;
                case SettingsEditorKind.Unit:
                    _state!.SaveUnit(_existing as SettingsUnitRecord, Name, Symbol);
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
            _toast.Show(
                DesktopErrorPresentation.ForException(ex, "The settings change was rejected."),
                ToastTone.Warning);
        }
    }

    private void SaveUser()
    {
        if (_existing is null && string.IsNullOrWhiteSpace(NewPin))
        {
            throw new InvalidOperationException("A PIN is required when creating a user.");
        }

        _state!.SaveUser(
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
        if (IsSaving)
        {
            return;
        }
        NewPin = string.Empty;
        _close();
    }

    private async Task SaveProductionUserAsync()
    {
        if (IsSaving || _production is null)
        {
            return;
        }
        if (Role != "Cashier" || Status != "Active")
        {
            UserOperationMessage = "This workflow creates active Cashier accounts.";
            NewPin = string.Empty;
            return;
        }
        if (string.IsNullOrWhiteSpace(Name) || Name.Trim().Length > 160 || NewPin.Length != 4 ||
            NewPin.Any(ch => !char.IsAsciiDigit(ch)))
        {
            UserOperationMessage = "Enter a name and a four-digit PIN.";
            NewPin = string.Empty;
            return;
        }
        if (HasUnknownOutcome && _submittedName != Name.Trim())
        {
            UserOperationMessage = "Retry the original account name while its outcome is unconfirmed.";
            NewPin = string.Empty;
            return;
        }
        IsSaving = true;
        _submittedName = Name.Trim();
        var pin = NewPin;
        NewPin = string.Empty;
        try
        {
            await _production.CreateCashierAsync(_submittedName, pin, ClientOperationId);
            await CompleteUserCreationAsync();
        }
        catch (Exception ex)
        {
            HasUnknownOutcome = IsUnconfirmed(ex);
            UserOperationMessage = SafeUserError(ex);
            _toast.Show(UserOperationMessage, ToastTone.Warning);
        }
        finally
        {
            NewPin = string.Empty;
            IsSaving = false;
        }
    }

    private async Task CheckCreationStatusAsync()
    {
        if (IsSaving || _production is null || !HasUnknownOutcome)
        {
            return;
        }
        IsSaving = true;
        try
        {
            var status = await _production.GetCashierCreationStatusAsync(ClientOperationId);
            if (status.State == "Succeeded" && status.UserId is not null)
            {
                await CompleteUserCreationAsync();
            }
            else
            {
                HasUnknownOutcome = status.State != "Failed";
                UserOperationMessage = status.State == "Failed"
                    ? "The Server rejected the original account creation."
                    : "Account creation is unconfirmed. Retry this same creation with the original PIN.";
            }
        }
        catch (Exception ex)
        {
            UserOperationMessage = SafeUserError(ex);
        }
        finally
        {
            NewPin = string.Empty;
            IsSaving = false;
        }
    }

    private async Task CompleteUserCreationAsync()
    {
        HasUnknownOutcome = false;
        UserOperationMessage = "Cashier account created.";
        _toast.Show(UserOperationMessage, ToastTone.Success);
        _close();
        if (_refresh is not null)
        {
            await _refresh();
        }
    }

    private static bool IsUnconfirmed(Exception exception) => exception switch
    {
        BackendOperationException backend => backend.Code is "operation.outcome_unknown" or "idempotency.payload_mismatch",
        DesktopApiException api => api.Code is "operation.outcome_unknown" or "idempotency.payload_mismatch" ||
            api.StatusCode is null || (int)api.StatusCode >= 500,
        _ => true
    };

    private static string SafeUserError(Exception exception)
    {
        var code = exception switch
        {
            BackendOperationException backend => backend.Code,
            DesktopApiException api => api.Code,
            _ => "operation.outcome_unknown"
        };
        return code switch
        {
            "identity.display_name_duplicate" => "A user with this name already exists.",
            "identity.invalid_pin" => "Enter a four-digit PIN.",
            "identity.invalid_display_name" => "Enter a user name of 1 to 160 characters.",
            "identity.cashier_role_unavailable" => "The Cashier role is unavailable. Contact the Owner.",
            "idempotency.payload_mismatch" => "This operation identity belongs to another request. Check its status.",
            "operation.outcome_unknown" or "network.timeout" or "network.server_unavailable" =>
                "Account creation is unconfirmed. Check status or retry this same creation with the original PIN.",
            var auth when auth.StartsWith("auth.", StringComparison.Ordinal) || auth.StartsWith("authorization.", StringComparison.Ordinal) =>
                "An active Owner session and terminal are required to create a Cashier.",
            _ => "The account could not be created. Check status before trying again."
        };
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
