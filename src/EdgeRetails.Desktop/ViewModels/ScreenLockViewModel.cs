using System.Windows.Input;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class ScreenLockViewModel : ViewModelBase
{
    private readonly ISessionContext _session;
    private readonly IBackendIdentityService _identityService;
    private readonly Action _switchUser;
    private string _pin = string.Empty;
    private bool _hasError;
    private bool _isUnlocking;

    public ScreenLockViewModel(
        ISessionContext session,
        IBackendIdentityService identityService,
        Action switchUser)
    {
        _session = session;
        _identityService = identityService;
        _switchUser = switchUser;

        AppendDigitCommand = new RelayCommand<string>(AppendDigit);
        BackspaceCommand = new RelayCommand(Backspace);
        ClearCommand = new RelayCommand(ClearPin);
        UnlockCommand = new RelayCommand(() => _ = UnlockAsync(), () => CanUnlock);
        SwitchUserCommand = new RelayCommand(_switchUser);
    }

    public event EventHandler? Unlocked;

    public string DisplayName => _session.DisplayName;

    public string RoleName => _session.RoleName;

    public string Initials => _session.Initials;

    public ICommand AppendDigitCommand { get; }

    public ICommand BackspaceCommand { get; }

    public ICommand ClearCommand { get; }

    public ICommand UnlockCommand { get; }

    public ICommand SwitchUserCommand { get; }

    public bool HasError
    {
        get => _hasError;
        private set => SetProperty(ref _hasError, value);
    }

    public bool CanUnlock => _pin.Length == 4 && !_isUnlocking;

    public int PinLength => _pin.Length;

    public bool IsDigit1Filled => _pin.Length >= 1;

    public bool IsDigit2Filled => _pin.Length >= 2;

    public bool IsDigit3Filled => _pin.Length >= 3;

    public bool IsDigit4Filled => _pin.Length >= 4;

    private void AppendDigit(string? digit)
    {
        if (_isUnlocking || string.IsNullOrEmpty(digit) || _pin.Length >= 4)
        {
            return;
        }

        if (!char.IsAsciiDigit(digit[0]))
        {
            return;
        }

        HasError = false;
        _pin += digit[0];
        NotifyPinChanged();

        if (_pin.Length == 4)
        {
            _ = UnlockAsync();
        }
    }

    private void Backspace()
    {
        if (_isUnlocking || _pin.Length == 0)
        {
            return;
        }

        _pin = _pin[..^1];
        HasError = false;
        NotifyPinChanged();
    }

    private void ClearPin()
    {
        _pin = string.Empty;
        HasError = false;
        NotifyPinChanged();
    }

    private void NotifyPinChanged()
    {
        OnPropertyChanged(nameof(PinLength));
        OnPropertyChanged(nameof(IsDigit1Filled));
        OnPropertyChanged(nameof(IsDigit2Filled));
        OnPropertyChanged(nameof(IsDigit3Filled));
        OnPropertyChanged(nameof(IsDigit4Filled));
        OnPropertyChanged(nameof(CanUnlock));
        ((RelayCommand)UnlockCommand).NotifyCanExecuteChanged();
    }

    private async Task UnlockAsync()
    {
        if (!CanUnlock)
        {
            return;
        }

        _isUnlocking = true;
        ((RelayCommand)UnlockCommand).NotifyCanExecuteChanged();
        try
        {
            await _identityService.UnlockScreenAsync(_session, _pin);
            Unlocked?.Invoke(this, EventArgs.Empty);
            ClearPin();
        }
        catch (UnauthorizedAccessException)
        {
            HasError = true;
            ClearPin();
        }
        catch (DesktopApiException ex) when (ex.Code == "identity.invalid_unlock")
        {
            HasError = true;
            ClearPin();
        }
        finally
        {
            _isUnlocking = false;
            NotifyPinChanged();
        }
    }
}
