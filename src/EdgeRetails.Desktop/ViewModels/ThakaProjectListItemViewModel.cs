using System.Globalization;
using System.Windows.Input;
using EdgeRetails.Desktop.Controls;

namespace EdgeRetails.Desktop.ViewModels;

/// <summary>
/// Represents a Thaka project item displayed in the project list and cards (Figma 14:4538).
/// </summary>
public sealed class ThakaProjectListItemViewModel : ViewModelBase
{
    private string _id;
    private string _projectName;
    private string _customerName;
    private string _phone;
    private string _location;
    private DateTime _startDate;
    private decimal _materialValue;
    private decimal _paid;
    private decimal _settlementDiscount;
    private string _status;
    private string _notes;

    public ThakaProjectListItemViewModel(
        string id,
        string projectName,
        string customerName,
        string phone,
        string location,
        DateTime startDate,
        decimal materialValue,
        decimal paid,
        string status = "ACTIVE",
        string notes = "",
        Action<ThakaProjectListItemViewModel>? onOpenWorkspace = null,
        Guid? backendProjectId = null,
        decimal settlementDiscount = 0m)
    {
        _id = id;
        _projectName = projectName;
        _customerName = customerName;
        _phone = phone;
        _location = location;
        _startDate = startDate;
        _materialValue = materialValue;
        _paid = paid;
        _status = status;
        _notes = notes;
        BackendProjectId = backendProjectId;
        _settlementDiscount = Math.Max(0m, settlementDiscount);

        OpenWorkspaceCommand = new RelayCommand(
            () => onOpenWorkspace?.Invoke(this));
    }

    public Guid? BackendProjectId { get; }

    public string Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public string ProjectName
    {
        get => _projectName;
        set
        {
            if (SetProperty(ref _projectName, value))
            {
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(DisplayText));
            }
        }
    }

    public string CustomerName
    {
        get => _customerName;
        set
        {
            if (SetProperty(ref _customerName, value))
            {
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(DisplayText));
            }
        }
    }

    public string DisplayName => $"{ProjectName} • {CustomerName}";

    public string DisplayText => $"{ProjectName} ({CustomerName})";

    public string Subtitle => $"{Location} • Balance Rs. {Balance:N0}";

    public string Phone
    {
        get => _phone;
        set => SetProperty(ref _phone, value);
    }

    public string Location
    {
        get => _location;
        set
        {
            if (SetProperty(ref _location, value))
            {
                OnPropertyChanged(nameof(Subtitle));
            }
        }
    }

    public DateTime StartDate
    {
        get => _startDate;
        set
        {
            if (SetProperty(ref _startDate, value))
            {
                OnPropertyChanged(nameof(StartDateFormatted));
            }
        }
    }

    public string StartDateFormatted =>
        _startDate.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);

    public decimal MaterialValue
    {
        get => _materialValue;
        set
        {
            if (SetProperty(ref _materialValue, value))
            {
                OnPropertyChanged(nameof(MaterialValueFormatted));
                OnPropertyChanged(nameof(Balance));
                OnPropertyChanged(nameof(BalanceFormatted));
                OnPropertyChanged(nameof(ProgressPercentage));
                OnPropertyChanged(nameof(ProgressDisplay));
                OnPropertyChanged(nameof(Subtitle));
            }
        }
    }

    public string MaterialValueFormatted => $"Rs. {MaterialValue:N0}";

    public decimal Paid
    {
        get => _paid;
        set
        {
            if (SetProperty(ref _paid, value))
            {
                OnPropertyChanged(nameof(PaidFormatted));
                OnPropertyChanged(nameof(Balance));
                OnPropertyChanged(nameof(BalanceFormatted));
                OnPropertyChanged(nameof(ProgressPercentage));
                OnPropertyChanged(nameof(ProgressDisplay));
                OnPropertyChanged(nameof(Subtitle));
            }
        }
    }

    public string PaidFormatted => $"Rs. {Paid:N0}";

    public decimal SettlementDiscount
    {
        get => _settlementDiscount;
        set
        {
            if (SetProperty(ref _settlementDiscount, Math.Max(0m, value)))
            {
                OnPropertyChanged(nameof(Balance));
                OnPropertyChanged(nameof(BalanceFormatted));
                OnPropertyChanged(nameof(Subtitle));
            }
        }
    }

    public decimal Balance => Math.Max(0m, MaterialValue - Paid - SettlementDiscount);

    public string BalanceFormatted => $"Rs. {Balance:N0}";

    public string Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(IsSettled));
                OnPropertyChanged(nameof(StatusTone));
            }
        }
    }

    public bool IsActive => string.Equals(Status, "ACTIVE", StringComparison.OrdinalIgnoreCase);

    public bool IsSettled => string.Equals(Status, "SETTLED", StringComparison.OrdinalIgnoreCase);

    public BadgeTone StatusTone => IsActive ? BadgeTone.Success : BadgeTone.Neutral;

    public string Notes
    {
        get => _notes;
        set => SetProperty(ref _notes, value);
    }

    public double ProgressPercentage =>
        MaterialValue > 0 ? Math.Min(100.0, (double)(Paid / MaterialValue) * 100.0) : 0.0;

    public string ProgressDisplay => $"{ProgressPercentage:F0}% Paid";

    public ICommand OpenWorkspaceCommand { get; }
}
