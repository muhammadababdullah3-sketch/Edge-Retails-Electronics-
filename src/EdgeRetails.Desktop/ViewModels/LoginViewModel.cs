using System.Collections.ObjectModel;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

/// <summary>
/// Session context payload emitted upon successful authentication.
/// </summary>
public sealed class UserSessionContext : ISessionContext
{
    public UserSessionContext(
        string displayName,
        string roleName,
        string initials,
        bool isOnline = true)
    {
        DisplayName = displayName;
        RoleName = roleName;
        Initials = initials;
        IsOnline = isOnline;
    }

    public string DisplayName { get; }

    public string RoleName { get; }

    public string Initials { get; }

    public bool IsOnline { get; }
}

/// <summary>
/// ViewModel for the Edge Retails login card (Figma Node 14:2).
/// Implements account selection, PIN entry, numeric keypad, and authentication verification.
/// </summary>
public sealed class LoginViewModel : ViewModelBase
{
    public const string DefaultHelperText = "Enter your PIN to continue";
    public const string ErrorHelperText = "Incorrect PIN. Please try again.";

    private readonly IDemoIdentityService _identityService;
    private readonly ObservableCollection<LoginAccountItemViewModel> _accounts;
    private LoginAccountItemViewModel? _selectedAccount;
    private string _pin = string.Empty;
    private string _helperText = DefaultHelperText;
    private bool _hasError;
    private bool _isSigningIn;

    public LoginViewModel(IDemoIdentityService? identityService = null)
    {
        _identityService = identityService ?? new DemoIdentityService();

        _accounts = new ObservableCollection<LoginAccountItemViewModel>(
            _identityService.Accounts.Select((account, index) =>
                new LoginAccountItemViewModel(
                    account.Id,
                    account.DisplayName,
                    account.RoleName,
                    account.Initials,
                    account.AvatarLetter,
                    account.IsOnline,
                    account.IsPrimary)
                {
                    IsSelected = index == 0
                }));

        _selectedAccount = _accounts.FirstOrDefault();

        SelectAccountCommand = new RelayCommand<LoginAccountItemViewModel>(SelectAccount);
        AppendDigitCommand = new RelayCommand<string>(AppendDigit);
        BackspaceCommand = new RelayCommand(Backspace);
        ClearCommand = new RelayCommand(ClearPin);
        SignInCommand = new RelayCommand(() => _ = AuthenticateAsync(), () => CanSignIn);
    }

    public event EventHandler<ISessionContext>? LoginSucceeded;

    public Action<ISessionContext>? OnLoginSuccess { get; set; }

    public ObservableCollection<LoginAccountItemViewModel> Accounts => _accounts;

    public RelayCommand<LoginAccountItemViewModel> SelectAccountCommand { get; }

    public RelayCommand<string> AppendDigitCommand { get; }

    public RelayCommand BackspaceCommand { get; }

    public RelayCommand ClearCommand { get; }

    public RelayCommand SignInCommand { get; }

    public LoginAccountItemViewModel? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (SetProperty(ref _selectedAccount, value))
            {
                ClearPin();
            }
        }
    }

    public string Pin
    {
        get => _pin;
        private set => SetProperty(ref _pin, value);
    }

    public int PinLength => _pin.Length;

    public bool IsDigit1Filled => _pin.Length >= 1;

    public bool IsDigit2Filled => _pin.Length >= 2;

    public bool IsDigit3Filled => _pin.Length >= 3;

    public bool IsDigit4Filled => _pin.Length >= 4;

    public bool HasError
    {
        get => _hasError;
        set => SetProperty(ref _hasError, value);
    }

    public string HelperText
    {
        get => _helperText;
        set => SetProperty(ref _helperText, value);
    }

    public bool IsSigningIn
    {
        get => _isSigningIn;
        set
        {
            if (SetProperty(ref _isSigningIn, value))
            {
                OnPropertyChanged(nameof(CanSignIn));
                SignInCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool CanSignIn => _pin.Length == 4 && !_isSigningIn;

    public void SelectAccount(LoginAccountItemViewModel? account)
    {
        if (account == null || _isSigningIn)
        {
            return;
        }

        foreach (var item in _accounts)
        {
            item.IsSelected = ReferenceEquals(item, account);
        }

        SelectedAccount = account;
        ClearPin();
        HasError = false;
        HelperText = DefaultHelperText;
    }

    public void AppendDigit(string? digit)
    {
        if (_isSigningIn || string.IsNullOrEmpty(digit) || _pin.Length >= 4)
        {
            return;
        }

        char ch = digit[0];
        if (!char.IsAsciiDigit(ch))
        {
            return;
        }

        if (HasError)
        {
            HasError = false;
            HelperText = DefaultHelperText;
        }

        Pin += ch;
        NotifyPinStateChanged();

        if (Pin.Length == 4)
        {
            _ = AuthenticateAsync();
        }
    }

    public void Backspace()
    {
        if (_isSigningIn || _pin.Length == 0)
        {
            return;
        }

        if (HasError)
        {
            HasError = false;
            HelperText = DefaultHelperText;
        }

        Pin = _pin[..^1];
        NotifyPinStateChanged();
    }

    public void ClearPin()
    {
        if (_isSigningIn)
        {
            return;
        }

        Pin = string.Empty;
        HasError = false;
        HelperText = DefaultHelperText;
        NotifyPinStateChanged();
    }

    public async Task AuthenticateAsync()
    {
        if (_selectedAccount == null || _pin.Length != 4 || _isSigningIn)
        {
            return;
        }

        try
        {
            IsSigningIn = true;
            HasError = false;

            // Short verification latency simulating authentication
            await Task.Delay(200);

            if (_identityService.VerifyPin(
                    _selectedAccount.AccountId,
                    _pin))
            {
                var session = new UserSessionContext(
                    _selectedAccount.DisplayName,
                    _selectedAccount.RoleName,
                    _selectedAccount.Initials,
                    _selectedAccount.IsOnline);

                IsSigningIn = false;
                OnLoginSuccess?.Invoke(session);
                LoginSucceeded?.Invoke(this, session);
            }
            else
            {
                HasError = true;
                HelperText = ErrorHelperText;
                IsSigningIn = false;

                // Brief pause so the user sees the error-highlighted dots and shake effect
                await Task.Delay(600);
                Pin = string.Empty;
                NotifyPinStateChanged();
            }
        }
        catch
        {
            IsSigningIn = false;
            throw;
        }
    }

    private void NotifyPinStateChanged()
    {
        OnPropertyChanged(nameof(PinLength));
        OnPropertyChanged(nameof(IsDigit1Filled));
        OnPropertyChanged(nameof(IsDigit2Filled));
        OnPropertyChanged(nameof(IsDigit3Filled));
        OnPropertyChanged(nameof(IsDigit4Filled));
        OnPropertyChanged(nameof(CanSignIn));
        SignInCommand.NotifyCanExecuteChanged();
    }
}
