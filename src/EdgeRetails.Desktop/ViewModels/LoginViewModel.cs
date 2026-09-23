using System.Collections.ObjectModel;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

/// <summary>
/// Session context payload emitted upon successful authentication.
/// </summary>
public sealed class UserSessionContext(
    string displayName,
    string roleName,
    string initials,
    bool isOnline = true,
    Guid? userId = null,
    Guid? sessionId = null,
    IReadOnlySet<string>? permissionKeys = null) : ISessionContext
{
    public Guid? UserId { get; } = userId;

    public Guid? SessionId { get; } = sessionId;

    public string DisplayName { get; } = displayName;

    public string RoleName { get; } = roleName;

    public string Initials { get; } = initials;

    public bool IsOnline { get; } = isOnline;

    public IReadOnlySet<string> PermissionKeys { get; } =
        permissionKeys ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
    private readonly IBackendIdentityService? _backendIdentityService;
    private readonly ObservableCollection<LoginAccountItemViewModel> _accounts;
    private LoginAccountItemViewModel? _selectedAccount;
    private string _pin = string.Empty;
    private string _helperText = DefaultHelperText;
    private bool _hasError;
    private bool _isSigningIn;

    public LoginViewModel(
        IDemoIdentityService? identityService = null,
        IBackendIdentityService? backendIdentityService = null)
    {
        _identityService = identityService ?? new DemoIdentityService();
        _backendIdentityService = backendIdentityService;

        _accounts = [];
        if (_backendIdentityService is null)
        {
            foreach (var account in _identityService.Accounts)
            {
                _accounts.Add(new LoginAccountItemViewModel(
                    account.Id,
                    account.DisplayName,
                    account.RoleName,
                    account.Initials,
                    account.AvatarLetter,
                    account.IsOnline,
                    account.IsPrimary));
            }

            SelectInitialAccount();
        }
        else
        {
            _ = LoadBackendAccountsAsync();
        }

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

        IsSigningIn = true;
        HasError = false;

        try
        {
            UserSessionContext? session = null;
            if (_backendIdentityService is null)
            {
                await Task.Delay(200);

                if (_identityService.VerifyPin(
                        _selectedAccount.AccountId,
                        _pin))
                {
                    session = new UserSessionContext(
                        _selectedAccount.DisplayName,
                        _selectedAccount.RoleName,
                        _selectedAccount.Initials,
                        _selectedAccount.IsOnline);
                }
            }
            else
            {
                var authenticated = await _backendIdentityService.AuthenticateAsync(
                    _selectedAccount.AccountId,
                    _pin);

                session = new UserSessionContext(
                    authenticated.DisplayName,
                    authenticated.RoleName,
                    authenticated.Initials,
                    isOnline: true,
                    userId: authenticated.UserId,
                    sessionId: authenticated.SessionId,
                    permissionKeys: authenticated.PermissionKeys);
            }

            if (session is null)
            {
                await ShowAuthenticationFailureAsync();
                return;
            }

            IsSigningIn = false;
            ClearPin();
            OnLoginSuccess?.Invoke(session);
            LoginSucceeded?.Invoke(this, session);
        }
        catch (UnauthorizedAccessException)
        {
            await ShowAuthenticationFailureAsync();
        }
        catch (Exception ex)
        {
            HasError = true;
            HelperText = $"Sign in unavailable: {ex.Message}";
            IsSigningIn = false;
            Pin = string.Empty;
            NotifyPinStateChanged();
        }
    }

    private async Task LoadBackendAccountsAsync()
    {
        if (_backendIdentityService is null)
        {
            return;
        }

        try
        {
            var accounts = await _backendIdentityService.GetAccountsAsync();

            _accounts.Clear();
            foreach (var account in accounts)
            {
                _accounts.Add(new LoginAccountItemViewModel(
                    account.UserId.ToString("D"),
                    account.DisplayName,
                    account.RoleName,
                    account.Initials,
                    account.Initials,
                    isOnline: true,
                    account.IsPrimary));
            }

            SelectInitialAccount();

            if (_accounts.Count == 0)
            {
                HasError = true;
                HelperText = "No active users are available.";
            }
        }
        catch (Exception ex)
        {
            HasError = true;
            HelperText = $"Accounts unavailable: {ex.Message}";
        }
    }

    private void SelectInitialAccount()
    {
        var initial = _accounts.FirstOrDefault(x => x.IsPrimary)
            ?? _accounts.FirstOrDefault();

        foreach (var item in _accounts)
        {
            item.IsSelected = ReferenceEquals(item, initial);
        }

        _selectedAccount = initial;
        OnPropertyChanged(nameof(SelectedAccount));
    }

    private async Task ShowAuthenticationFailureAsync()
    {
        HasError = true;
        HelperText = ErrorHelperText;
        IsSigningIn = false;

        await Task.Delay(600);
        Pin = string.Empty;
        NotifyPinStateChanged();
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
