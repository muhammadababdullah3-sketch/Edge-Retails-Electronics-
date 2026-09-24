using System.IO;
using System.Windows.Input;
using EdgeRetails.Desktop.Services;
using Microsoft.Win32;

namespace EdgeRetails.Desktop.ViewModels;

public enum FirstSetupStep
{
    License = 1,
    ShopSetup = 2,
    Ready = 3
}

public sealed class FirstSetupViewModel : ViewModelBase
{
    private readonly IFirstRunSetupState _setupState;
    private readonly IDemoIdentityService _identityService;
    private readonly DemoSettingsState? _previewSettingsState;
    private readonly IToastService _toastService;
    private readonly IBackendSetupService? _backendSetupService;
    private bool _isCompletingSetup;

    private FirstSetupStep _step = FirstSetupStep.License;
    private string _licensePath = string.Empty;
    private string _shopName;
    private string _ownerName;
    private string _phone;
    private string _address;
    private string _ownerPin = string.Empty;

    public FirstSetupViewModel(
        IFirstRunSetupState setupState,
        IDemoIdentityService identityService,
        IToastService toastService,
        DemoSettingsState? settingsState = null,
        IBackendSetupService? backendSetupService = null)
    {
        ArgumentNullException.ThrowIfNull(setupState);
        ArgumentNullException.ThrowIfNull(identityService);
        ArgumentNullException.ThrowIfNull(toastService);

        _setupState = setupState;
        _identityService = identityService;
        _toastService = toastService;
        _backendSetupService = backendSetupService;
        _previewSettingsState = ResolvePreviewSettingsState(
            settingsState,
            backendSetupService);

        _shopName = _previewSettingsState?.ShopName ?? string.Empty;
        _ownerName = _previewSettingsState?.OwnerName ?? string.Empty;
        _phone = _previewSettingsState?.Phone ?? string.Empty;
        _address = _previewSettingsState?.Address ?? string.Empty;

        BrowseLicenseCommand = new RelayCommand(BrowseLicense);
        ContinueCommand = new RelayCommand(() => _ = ContinueAsync());
        BackCommand = new RelayCommand(Back, () => Step != FirstSetupStep.License);
        StartCommand = new RelayCommand(Start, () => Step == FirstSetupStep.Ready);
    }

    public event EventHandler? SetupCompleted;
    public event EventHandler? OwnerPinClearRequested;

    public FirstSetupStep Step
    {
        get => _step;
        private set
        {
            if (SetProperty(ref _step, value))
            {
                RaiseStepStateChanged();
            }
        }
    }

    public bool IsLicenseStep => Step == FirstSetupStep.License;
    public bool IsShopStep => Step == FirstSetupStep.ShopSetup;
    public bool IsReadyStep => Step == FirstSetupStep.Ready;

    public string LicensePath
    {
        get => _licensePath;
        private set => SetProperty(ref _licensePath, value);
    }

    public string LicenseFileName =>
        string.IsNullOrWhiteSpace(LicensePath)
            ? "No license file selected"
            : Path.GetFileName(LicensePath);

    public string LicenseStatus =>
        string.IsNullOrWhiteSpace(LicensePath)
            ? "Not selected"
            : "File selected · Verification pending";

    public string ShopName
    {
        get => _shopName;
        set => SetProperty(ref _shopName, value);
    }

    public string OwnerName
    {
        get => _ownerName;
        set => SetProperty(ref _ownerName, value);
    }

    public string Phone
    {
        get => _phone;
        set => SetProperty(ref _phone, value);
    }

    public string Address
    {
        get => _address;
        set => SetProperty(ref _address, value);
    }

    public string DatabaseStatus => _backendSetupService is null
        ? "Demo / Offline"
        : "PostgreSQL Ready";
    public string ModuleName => "Electronics";
    public string ReadyLicenseStatus => "Verification Pending";

    public ICommand BrowseLicenseCommand { get; }
    public RelayCommand ContinueCommand { get; }
    public RelayCommand BackCommand { get; }
    public RelayCommand StartCommand { get; }

    public void SetOwnerPin(string pin)
    {
        _ownerPin = pin ?? string.Empty;
    }

    public bool TrySelectLicense(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            _toastService.Show(
                "Select an existing license file.",
                ToastTone.Warning);
            return false;
        }

        var extension = Path.GetExtension(filePath);
        if (!string.Equals(extension, ".erlic", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".lic", StringComparison.OrdinalIgnoreCase))
        {
            _toastService.Show(
                "License file must use canonical .erlic format (or legacy .lic).",
                ToastTone.Warning);
            return false;
        }

        LicensePath = filePath;
        OnPropertyChanged(nameof(LicenseFileName));
        OnPropertyChanged(nameof(LicenseStatus));
        return true;
    }

    private void BrowseLicense()
    {
        var picker = new OpenFileDialog
        {
            Title = "Import Edge Retails License",
            Filter = "Canonical Edge Retails License (*.erlic)|*.erlic|Legacy License (*.lic)|*.lic|All Files (*.*)|*.*"
        };

        if (picker.ShowDialog() == true)
        {
            TrySelectLicense(picker.FileName);
        }
    }

    private async Task ContinueAsync()
    {
        if (_isCompletingSetup)
        {
            return;
        }

        if (Step == FirstSetupStep.License)
        {
            if (string.IsNullOrWhiteSpace(LicensePath) || !File.Exists(LicensePath))
            {
                _toastService.Show(
                    "Select a license file before continuing.",
                    ToastTone.Warning);
                return;
            }

            Step = FirstSetupStep.ShopSetup;
            return;
        }

        if (Step == FirstSetupStep.ShopSetup)
        {
            await CompleteShopStepAsync();
        }
    }

    private async Task CompleteShopStepAsync()
    {
        _isCompletingSetup = true;
        try
        {
            if (string.IsNullOrWhiteSpace(ShopName) ||
                string.IsNullOrWhiteSpace(OwnerName))
            {
                throw new InvalidOperationException(
                    "Shop name and owner name are required.");
            }

            if (_ownerPin.Length != 4 || !_ownerPin.All(char.IsAsciiDigit))
            {
                throw new InvalidOperationException(
                    "Owner PIN must be exactly 4 numeric digits.");
            }

            if (_backendSetupService is null)
            {
                _identityService.ConfigureOwner(OwnerName, _ownerPin);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(LicensePath) || !File.Exists(LicensePath))
                {
                    _toastService.Show("A valid license file (.erlic) is required before completing setup.", ToastTone.Warning);
                    return;
                }

                var licenseContent = await File.ReadAllTextAsync(LicensePath);

                await _backendSetupService.CompleteFirstSetupAsync(
                    ShopName,
                    OwnerName,
                    Phone,
                    Address,
                    _ownerPin,
                    ModuleName,
                    licenseContent);
            }

#if DEBUG
            if (_previewSettingsState is not null)
            {
                _previewSettingsState.SaveShop(
                    ShopName,
                    OwnerName,
                    Phone,
                    Address,
                    _previewSettingsState.LogoPath);

                var ownerUser = _previewSettingsState.Users.FirstOrDefault(
                    user => string.Equals(
                        user.Role,
                        "Owner",
                        StringComparison.OrdinalIgnoreCase));

                if (ownerUser is not null)
                {
                    _previewSettingsState.SaveUser(
                        ownerUser,
                        OwnerName,
                        "Owner",
                        isActive: true);
                }
            }
#endif

            Step = FirstSetupStep.Ready;
        }
        catch (Exception ex)
        {
            _toastService.Show(ex.Message, ToastTone.Warning);
        }
        finally
        {
            ClearOwnerPin();
            _isCompletingSetup = false;
        }
    }

    private static DemoSettingsState? ResolvePreviewSettingsState(
        DemoSettingsState? settingsState,
        IBackendSetupService? backendSetupService)
    {
#if DEBUG
        return backendSetupService is null
            ? settingsState ?? DemoSettingsState.Instance
            : null;
#else
        return null;
#endif
    }

    private void Back()
    {
        if (Step == FirstSetupStep.ShopSetup)
        {
            ClearOwnerPin();
            Step = FirstSetupStep.License;
            return;
        }

        Step = Step == FirstSetupStep.Ready
            ? FirstSetupStep.ShopSetup
            : FirstSetupStep.License;
    }

    private void Start()
    {
        if (Step != FirstSetupStep.Ready)
        {
            return;
        }

        _setupState.MarkSetupCompleted();
        SetupCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void ClearOwnerPin()
    {
        _ownerPin = string.Empty;
        OwnerPinClearRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseStepStateChanged()
    {
        OnPropertyChanged(nameof(IsLicenseStep));
        OnPropertyChanged(nameof(IsShopStep));
        OnPropertyChanged(nameof(IsReadyStep));
        BackCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged();
    }
}
