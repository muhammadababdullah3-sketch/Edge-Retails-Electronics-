using System.IO;
using System.Windows.Input;
using EdgeRetails.Application.Production.CentralActivation;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Infrastructure.Production.Central;
using EdgeRetails.Infrastructure.Production.Licensing;
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
    private string? _cloudLicenseJson;
    private readonly IShopCentralActivationService? _centralActivation;
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

        _centralActivation = ShopCentralActivationService.TryCreateFromEnvironment(new WindowsMachineIdentityProvider());

        BrowseLicenseCommand = new RelayCommand(BrowseLicense);
        RequestCloudActivationCommand = new RelayCommand(() => _ = RequestCloudActivationAsync());
        ContinueCommand = new RelayCommand(() => _ = ContinueAsync());
        BackCommand = new RelayCommand(Back, () => Step != FirstSetupStep.License);
        StartCommand = new RelayCommand(Start, () => Step == FirstSetupStep.Ready);

        TryAutoDetectLicense();
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
        !string.IsNullOrWhiteSpace(_cloudLicenseJson)
            ? "Cloud license ready · complete shop setup"
            : string.IsNullOrWhiteSpace(LicensePath)
                ? "Not selected"
                : "File selected · Verification pending";

    public bool IsCloudActivationAvailable => _centralActivation?.IsConfigured == true;

    private string _activationToken = string.Empty;

    public string ActivationToken
    {
        get => _activationToken;
        set => SetProperty(ref _activationToken, value);
    }

    private string _cloudActivationStatus = string.Empty;

    public string CloudActivationStatus
    {
        get => _cloudActivationStatus;
        private set => SetProperty(ref _cloudActivationStatus, value);
    }

    public ICommand RequestCloudActivationCommand { get; }

    private string? _licenseErrorMessage;

    public string? LicenseErrorMessage
    {
        get => _licenseErrorMessage;
        set
        {
            if (SetProperty(ref _licenseErrorMessage, value))
            {
                OnPropertyChanged(nameof(HasLicenseError));
            }
        }
    }

    public bool HasLicenseError => !string.IsNullOrWhiteSpace(LicenseErrorMessage);

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
            LicenseErrorMessage = "Selected license file does not exist.";
            _toastService.Show(
                "Select an existing license file.",
                ToastTone.Warning);
            return false;
        }

        var extension = Path.GetExtension(filePath);
        if (!string.Equals(extension, ".erlic", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".lic", StringComparison.OrdinalIgnoreCase))
        {
            LicenseErrorMessage = "License file must use canonical .erlic format (or legacy .lic).";
            _toastService.Show(
                "License file must use canonical .erlic format (or legacy .lic).",
                ToastTone.Warning);
            return false;
        }

        LicensePath = filePath;
        LicenseErrorMessage = null;
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

    private void TryAutoDetectLicense()
    {
        var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var candidates = new List<string>
        {
            Path.Combine(commonAppData, "EdgeRetails", "license.erlic"),
            Path.Combine(AppContext.BaseDirectory, "license.erlic"),
            Path.Combine(Environment.CurrentDirectory, "license.erlic")
        };

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        if (!string.IsNullOrEmpty(desktop))
        {
            candidates.Add(Path.Combine(desktop, "license.erlic"));
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(userProfile))
        {
            candidates.Add(Path.Combine(userProfile, "OneDrive", "Desktop", "license.erlic"));
        }

        foreach (var candidate in candidates.Distinct())
        {
            if (File.Exists(candidate))
            {
                TrySelectLicense(candidate);
                break;
            }
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
            if (string.IsNullOrWhiteSpace(_cloudLicenseJson) &&
                (string.IsNullOrWhiteSpace(LicensePath) || !File.Exists(LicensePath)))
            {
                LicenseErrorMessage = "Import a signed .erlic file or complete cloud activation (admin approval) before continuing.";
                _toastService.Show(
                    "A license is required before continuing.",
                    ToastTone.Warning);
                return;
            }

            LicenseErrorMessage = null;
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
                var licenseContent = _cloudLicenseJson;
                if (string.IsNullOrWhiteSpace(licenseContent))
                {
                    if (string.IsNullOrWhiteSpace(LicensePath) || !File.Exists(LicensePath))
                    {
                        _toastService.Show("A valid license is required before completing setup.", ToastTone.Warning);
                        return;
                    }

                    licenseContent = await File.ReadAllTextAsync(LicensePath);
                }
                if (licenseContent.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
                {
                    LicenseErrorMessage =
                        "This file contains private signing material. Use only the signed license (.erlic) issued from Edge Retails.";
                    _toastService.Show(LicenseErrorMessage, ToastTone.Warning);
                    return;
                }

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
            _toastService.Show(
                DesktopErrorPresentation.ForException(ex, "Initial setup could not be completed."),
                ToastTone.Warning);
        }
        finally
        {
            ClearOwnerPin();
            _isCompletingSetup = false;
        }
    }

    private async Task RequestCloudActivationAsync()
    {
        if (_centralActivation is null)
        {
            LicenseErrorMessage = "Cloud activation is not configured. Set EDGE_RETAILS_CENTRAL_BASE_URL.";
            return;
        }

        if (string.IsNullOrWhiteSpace(ActivationToken))
        {
            LicenseErrorMessage = "Enter the activation token from Edge Retails.";
            return;
        }

        LicenseErrorMessage = null;
        CloudActivationStatus = "Submitting activation request…";

        var submit = await _centralActivation.SubmitActivationTokenAsync(ActivationToken.Trim());
        if (!submit.IsSuccess)
        {
            LicenseErrorMessage = submit.Message ?? submit.ErrorCode ?? "Activation request failed.";
            CloudActivationStatus = string.Empty;
            return;
        }

        CloudActivationStatus = "Waiting for admin approval in Edge Retails cloud…";
        _toastService.Show("Request sent. Admin must approve before the license is issued.", ToastTone.Info);

        for (var attempt = 0; attempt < 120; attempt++)
        {
            var delivery = await _centralActivation.PollDeliveryAsync();
            CloudActivationStatus = delivery.Message ?? delivery.State.ToString();

            if (delivery.State == ShopActivationDeliveryState.Ready &&
                !string.IsNullOrWhiteSpace(delivery.SignedLicenseJson))
            {
                _cloudLicenseJson = delivery.SignedLicenseJson;
                OnPropertyChanged(nameof(LicenseStatus));
                CloudActivationStatus = "License received. Continue to shop setup.";
                _toastService.Show("License is ready.", ToastTone.Success);
                return;
            }

            if (delivery.State is ShopActivationDeliveryState.Rejected or ShopActivationDeliveryState.Failed)
            {
                LicenseErrorMessage = delivery.Message ?? "Activation was not approved.";
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(5));
        }

        LicenseErrorMessage = "Timed out waiting for admin approval or certificate. Try again later.";
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
