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
    private readonly DemoSettingsState _settingsState;
    private readonly IToastService _toastService;

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
        DemoSettingsState? settingsState = null)
    {
        ArgumentNullException.ThrowIfNull(setupState);
        ArgumentNullException.ThrowIfNull(identityService);
        ArgumentNullException.ThrowIfNull(toastService);

        _setupState = setupState;
        _identityService = identityService;
        _toastService = toastService;
        _settingsState = settingsState ?? DemoSettingsState.Instance;

        _shopName = _settingsState.ShopName;
        _ownerName = _settingsState.OwnerName;
        _phone = _settingsState.Phone;
        _address = _settingsState.Address;

        BrowseLicenseCommand = new RelayCommand(BrowseLicense);
        ContinueCommand = new RelayCommand(Continue);
        BackCommand = new RelayCommand(Back, () => Step != FirstSetupStep.License);
        StartCommand = new RelayCommand(Start, () => Step == FirstSetupStep.Ready);
    }

    public event EventHandler? SetupCompleted;

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

    public string DatabaseStatus => "Integration Pending";
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
        if (!string.Equals(extension, ".lic", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".key", StringComparison.OrdinalIgnoreCase))
        {
            _toastService.Show(
                "License file must use .lic or .key format.",
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
            Filter = "License Files (*.lic;*.key)|*.lic;*.key"
        };

        if (picker.ShowDialog() == true)
        {
            TrySelectLicense(picker.FileName);
        }
    }

    private void Continue()
    {
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
            CompleteShopStep();
        }
    }

    private void CompleteShopStep()
    {
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

            _settingsState.SaveShop(
                ShopName,
                OwnerName,
                Phone,
                Address,
                _settingsState.LogoPath);

            var ownerUser = _settingsState.Users.FirstOrDefault(
                user => string.Equals(
                    user.Role,
                    "Owner",
                    StringComparison.OrdinalIgnoreCase));

            if (ownerUser is not null)
            {
                _settingsState.SaveUser(
                    ownerUser,
                    OwnerName,
                    "Owner",
                    isActive: true);
            }

            _identityService.ConfigureOwner(OwnerName, _ownerPin);
            _ownerPin = string.Empty;
            Step = FirstSetupStep.Ready;
        }
        catch (Exception ex)
        {
            _toastService.Show(ex.Message, ToastTone.Warning);
        }
    }

    private void Back()
    {
        Step = Step switch
        {
            FirstSetupStep.Ready => FirstSetupStep.ShopSetup,
            FirstSetupStep.ShopSetup => FirstSetupStep.License,
            _ => FirstSetupStep.License
        };
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

    private void RaiseStepStateChanged()
    {
        OnPropertyChanged(nameof(IsLicenseStep));
        OnPropertyChanged(nameof(IsShopStep));
        OnPropertyChanged(nameof(IsReadyStep));
        BackCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged();
    }
}
