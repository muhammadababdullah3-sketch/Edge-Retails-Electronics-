using System;
using System.Collections.ObjectModel;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Domain.Posting;

namespace EdgeRetails.Desktop.ViewModels;

public sealed record SupervisorApprovalResult(
    bool IsAuthorized,
    Guid SupervisorId,
    string SupervisorName,
    string ApprovalToken,
    SupervisorApprovalRequest? Request = null);

/// <summary>
/// Desktop supervisor approval modal view model.
/// Intercepts backdated operations within 7 days to require cryptographic supervisor PIN,
/// and enforces locked accounting period hard error banners, disabling submit without client authority.
/// </summary>
public sealed class SupervisorApprovalModalViewModel : ViewModelBase, IDisposable
{
    private readonly DesktopApiClient _apiClient;
    private readonly IDialogService? _dialogService;
    private readonly Action<SupervisorApprovalResult>? _onAuthorized;
    private DateOnly _postingDate;
    private DateOnly _shopDate;
    private bool _isPeriodLocked;
    private string? _lockedPeriodBanner;
    private LoginAccountDto? _selectedSupervisor;
    private string _supervisorPin = string.Empty;
    private string _reason = string.Empty;
    private string _operationType;
    private bool _isSubmitting;
    private string? _errorMessage;
    private SupervisorApprovalResult? _approvalResult;

    public SupervisorApprovalModalViewModel(
        DesktopApiClient apiClient,
        DateOnly postingDate,
        DateOnly shopDate,
        string operationType = "Sale",
        IDialogService? dialogService = null,
        Action<SupervisorApprovalResult>? onAuthorized = null)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _postingDate = postingDate;
        _shopDate = shopDate;
        _operationType = operationType;
        _dialogService = dialogService;
        _onAuthorized = onAuthorized;

        Supervisors = new ObservableCollection<LoginAccountDto>();
        SubmitCommand = new RelayCommand(async () => await AuthorizeAsync(), () => CanSubmit);
        CancelCommand = new RelayCommand(Cancel);
    }

    public DateOnly PostingDate
    {
        get => _postingDate;
        set
        {
            if (SetProperty(ref _postingDate, value))
            {
                OnPropertyChanged(nameof(IsBackdating));
                OnPropertyChanged(nameof(BackdateDays));
                OnPropertyChanged(nameof(CanSubmit));
            }
        }
    }

    public DateOnly ShopDate => _shopDate;
    public bool IsBackdating => PostingDate < ShopDate;
    public int BackdateDays => Math.Max(0, ShopDate.DayNumber - PostingDate.DayNumber);

    public bool IsPeriodLocked
    {
        get => _isPeriodLocked;
        set
        {
            if (SetProperty(ref _isPeriodLocked, value))
            {
                OnPropertyChanged(nameof(CanSubmit));
                ((RelayCommand)SubmitCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public string? LockedPeriodBanner
    {
        get => _lockedPeriodBanner;
        set => SetProperty(ref _lockedPeriodBanner, value);
    }

    public ObservableCollection<LoginAccountDto> Supervisors { get; }

    public LoginAccountDto? SelectedSupervisor
    {
        get => _selectedSupervisor;
        set
        {
            if (SetProperty(ref _selectedSupervisor, value))
            {
                OnPropertyChanged(nameof(CanSubmit));
                ((RelayCommand)SubmitCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public string SupervisorPin
    {
        get => _supervisorPin;
        set
        {
            if (SetProperty(ref _supervisorPin, value))
            {
                OnPropertyChanged(nameof(CanSubmit));
                ((RelayCommand)SubmitCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public string Reason
    {
        get => _reason;
        set => SetProperty(ref _reason, value);
    }

    public string OperationType
    {
        get => _operationType;
        set => SetProperty(ref _operationType, value);
    }

    public bool IsSubmitting
    {
        get => _isSubmitting;
        set
        {
            if (SetProperty(ref _isSubmitting, value))
            {
                OnPropertyChanged(nameof(CanSubmit));
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    public SupervisorApprovalResult? ApprovalResult => _approvalResult;
    public bool IsAuthorized => _approvalResult?.IsAuthorized == true;

    public bool CanSubmit =>
        !IsPeriodLocked &&
        IsBackdating &&
        SelectedSupervisor is not null &&
        !string.IsNullOrWhiteSpace(SupervisorPin) &&
        !IsSubmitting;

    public ICommand SubmitCommand { get; }
    public ICommand CancelCommand { get; }

    public async Task LoadSupervisorsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var accounts = await _apiClient.GetAsync<IReadOnlyList<LoginAccountDto>>(
                "/api/auth/accounts",
                cancellationToken);
            Supervisors.Clear();
            if (accounts is not null)
            {
                foreach (var account in accounts)
                {
                    Supervisors.Add(account);
                }
                if (SelectedSupervisor is null && Supervisors.Count > 0)
                {
                    SelectedSupervisor = Supervisors.FirstOrDefault(x =>
                        x.IsPrimary ||
                        x.RoleName.Contains("Owner", StringComparison.OrdinalIgnoreCase) ||
                        x.RoleName.Contains("Supervisor", StringComparison.OrdinalIgnoreCase)) ?? Supervisors[0];
                }
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Unable to load supervisor accounts: {ex.Message}";
        }
    }

    public async Task EvaluatePeriodLockAsync(CancellationToken cancellationToken = default)
    {
        ErrorMessage = null;
        try
        {
            var result = await _apiClient.GetAsync<PeriodLockCheckDto>(
                $"/api/posting/period-lock-check?postingDate={PostingDate:yyyy-MM-dd}",
                cancellationToken);

            if (result.IsLocked)
            {
                IsPeriodLocked = true;
                LockedPeriodBanner = $"HARD ERROR: Accounting period for date {PostingDate:yyyy-MM-dd} is LOCKED ({result.LockedPeriodName ?? "Closed"}). Historical posting is strictly prohibited.";
            }
            else if (result.ExceedsWindow)
            {
                IsPeriodLocked = true;
                LockedPeriodBanner = $"HARD ERROR: Posting date {PostingDate:yyyy-MM-dd} exceeds maximum 7-calendar-day historical window (earliest allowed: {result.EarliestAllowed:yyyy-MM-dd}).";
            }
            else if (result.IsFuture)
            {
                IsPeriodLocked = true;
                LockedPeriodBanner = $"HARD ERROR: Posting date {PostingDate:yyyy-MM-dd} cannot be in the future. Authoritative shop date is {result.ShopDate:yyyy-MM-dd}.";
            }
            else
            {
                IsPeriodLocked = false;
                LockedPeriodBanner = null;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Unable to verify period lock status: {ex.Message}";
        }
    }

    public async Task<bool> AuthorizeAsync(CancellationToken cancellationToken = default)
    {
        if (!CanSubmit || SelectedSupervisor is null)
        {
            return false;
        }

        IsSubmitting = true;
        ErrorMessage = null;

        try
        {
            var payload = new
            {
                supervisorId = SelectedSupervisor.UserId,
                supervisorPin = SupervisorPin.Trim(),
                postingDate = PostingDate,
                operationType = OperationType,
                reason = string.IsNullOrWhiteSpace(Reason) ? "Historical posting authorization" : Reason.Trim()
            };

            var response = await _apiClient.PostAsync<object, SupervisorApprovalApiResponse>(
                "/api/posting/supervisor-approval",
                payload,
                cancellationToken);

            var request = new SupervisorApprovalRequest(
                SelectedSupervisor.UserId,
                SupervisorPin: null,
                string.IsNullOrWhiteSpace(Reason) ? "Historical posting authorization" : Reason.Trim(),
                DateTimeOffset.UtcNow,
                ApprovalToken: response.ApprovalToken);

            _approvalResult = new SupervisorApprovalResult(
                IsAuthorized: response.Authorized,
                SupervisorId: response.SupervisorId,
                SupervisorName: response.SupervisorDisplayName,
                ApprovalToken: response.ApprovalToken,
                Request: request);

            _onAuthorized?.Invoke(_approvalResult);
            _dialogService?.Close();
            return true;
        }
        catch (DesktopApiException ex) when (ex.StatusCode == HttpStatusCode.Forbidden || ex.Code.Contains("invalid_supervisor_pin"))
        {
            ErrorMessage = "Supervisor PIN verification failed. Incorrect credentials.";
            return false;
        }
        catch (DesktopApiException ex) when (ex.StatusCode == HttpStatusCode.Conflict || ex.Code.Contains("accounting_period_locked"))
        {
            IsPeriodLocked = true;
            LockedPeriodBanner = "Accounting period is LOCKED. Historical posting is strictly forbidden.";
            ErrorMessage = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Authorization request failed: {ex.Message}";
            return false;
        }
        finally
        {
            ClearSecret();
            IsSubmitting = false;
        }
    }

    public void ClearSecret()
    {
        SupervisorPin = string.Empty;
    }

    public void Dispose()
    {
        ClearSecret();
    }

    public void Cancel()
    {
        ClearSecret();
        _approvalResult = null;
        _dialogService?.Close();
    }

    internal sealed record PeriodLockCheckDto(
        DateOnly PostingDate,
        DateOnly ShopDate,
        bool IsLocked,
        string? LockedPeriodName,
        bool IsFuture,
        bool ExceedsWindow,
        DateOnly EarliestAllowed);

    internal sealed record SupervisorApprovalApiResponse(
        bool Authorized,
        Guid SupervisorId,
        string SupervisorDisplayName,
        DateTimeOffset AuthorizedAtUtc,
        string ApprovalToken);
}
