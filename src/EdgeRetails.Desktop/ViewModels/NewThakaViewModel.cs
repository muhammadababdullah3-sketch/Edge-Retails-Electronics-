using System.Windows.Input;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

/// <summary>
/// ViewModel for creating a new Thaka Project (Figma 14:4836).
/// Validates required fields (Customer Name, Project Name) and adds the new project.
/// </summary>
public sealed class NewThakaViewModel : ViewModelBase
{
    private readonly IToastService? _toastService;
    private string _customerName = string.Empty;
    private string _phoneNumber = string.Empty;
    private string _projectName = string.Empty;
    private string _location = string.Empty;
    private string _notes = string.Empty;

    private string? _customerNameError;
    private string? _projectNameError;
    private string? _validationMessage;

    public Action<ThakaProjectListItemViewModel>? ProjectCreated { get; set; }
    public Action? CloseRequested { get; set; }

    public NewThakaViewModel()
        : this(null, null, null)
    {
    }

    public NewThakaViewModel(
        Action<ThakaProjectListItemViewModel>? onProjectCreated = null,
        Action? onCloseRequested = null,
        IToastService? toastService = null)
    {
        ProjectCreated = onProjectCreated;
        CloseRequested = onCloseRequested;
        _toastService = toastService;

        CreateThakaCommand = new RelayCommand(ExecuteCreateThaka, () => CanCreate);
        CancelCommand = new RelayCommand(ExecuteCancel);
        CloseCommand = new RelayCommand(ExecuteCancel);
    }

    public string CustomerName
    {
        get => _customerName;
        set
        {
            if (SetProperty(ref _customerName, value))
            {
                ValidateCustomerName();
                UpdateCanCreate();
            }
        }
    }

    public string PhoneNumber
    {
        get => _phoneNumber;
        set => SetProperty(ref _phoneNumber, value);
    }

    public string ProjectName
    {
        get => _projectName;
        set
        {
            if (SetProperty(ref _projectName, value))
            {
                ValidateProjectName();
                UpdateCanCreate();
            }
        }
    }

    public string Location
    {
        get => _location;
        set => SetProperty(ref _location, value);
    }

    public string Notes
    {
        get => _notes;
        set => SetProperty(ref _notes, value);
    }

    public string? CustomerNameError
    {
        get => _customerNameError;
        private set
        {
            if (SetProperty(ref _customerNameError, value))
            {
                OnPropertyChanged(nameof(HasCustomerNameError));
            }
        }
    }

    public bool HasCustomerNameError => !string.IsNullOrEmpty(_customerNameError);

    public string? ProjectNameError
    {
        get => _projectNameError;
        private set
        {
            if (SetProperty(ref _projectNameError, value))
            {
                OnPropertyChanged(nameof(HasProjectNameError));
            }
        }
    }

    public bool HasProjectNameError => !string.IsNullOrEmpty(_projectNameError);

    public string? ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (SetProperty(ref _validationMessage, value))
            {
                OnPropertyChanged(nameof(HasValidationMessage));
            }
        }
    }

    public bool HasValidationMessage => !string.IsNullOrEmpty(_validationMessage);

    public bool CanCreate =>
        !string.IsNullOrWhiteSpace(CustomerName) &&
        !string.IsNullOrWhiteSpace(ProjectName);

    public ICommand CreateThakaCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand CloseCommand { get; }

    public void Reset()
    {
        _customerName = string.Empty;
        _phoneNumber = string.Empty;
        _projectName = string.Empty;
        _location = string.Empty;
        _notes = string.Empty;
        _customerNameError = null;
        _projectNameError = null;
        _validationMessage = null;

        OnPropertyChanged(nameof(CustomerName));
        OnPropertyChanged(nameof(PhoneNumber));
        OnPropertyChanged(nameof(ProjectName));
        OnPropertyChanged(nameof(Location));
        OnPropertyChanged(nameof(Notes));
        OnPropertyChanged(nameof(CustomerNameError));
        OnPropertyChanged(nameof(HasCustomerNameError));
        OnPropertyChanged(nameof(ProjectNameError));
        OnPropertyChanged(nameof(HasProjectNameError));
        OnPropertyChanged(nameof(ValidationMessage));
        OnPropertyChanged(nameof(HasValidationMessage));
        OnPropertyChanged(nameof(CanCreate));

        ((RelayCommand)CreateThakaCommand).NotifyCanExecuteChanged();
    }

    private void ValidateCustomerName()
    {
        if (string.IsNullOrWhiteSpace(CustomerName))
        {
            CustomerNameError = "Customer Name is required.";
        }
        else
        {
            CustomerNameError = null;
        }
    }

    private void ValidateProjectName()
    {
        if (string.IsNullOrWhiteSpace(ProjectName))
        {
            ProjectNameError = "Project Name is required.";
        }
        else
        {
            ProjectNameError = null;
        }
    }

    private void UpdateCanCreate()
    {
        OnPropertyChanged(nameof(CanCreate));
        ((RelayCommand)CreateThakaCommand).NotifyCanExecuteChanged();
    }

    private void ExecuteCreateThaka()
    {
        ValidateCustomerName();
        ValidateProjectName();

        if (!CanCreate)
        {
            ValidationMessage = "Please fill in all required fields.";
            return;
        }

        ValidationMessage = null;

        var id = $"THK-{DateTime.Now:yyyyMMddHHmmss}";
        var newProject = new ThakaProjectListItemViewModel(
            id: id,
            projectName: ProjectName.Trim(),
            customerName: CustomerName.Trim(),
            phone: string.IsNullOrWhiteSpace(PhoneNumber) ? "N/A" : PhoneNumber.Trim(),
            location: string.IsNullOrWhiteSpace(Location) ? "N/A" : Location.Trim(),
            startDate: DateTime.Today,
            materialValue: 0m,
            paid: 0m,
            status: "ACTIVE",
            notes: Notes?.Trim() ?? string.Empty);

        ProjectCreated?.Invoke(newProject);
        _toastService?.Show($"Thaka project '{newProject.ProjectName}' created successfully.", ToastTone.Success);

        Reset();
        CloseRequested?.Invoke();
    }

    private void ExecuteCancel()
    {
        Reset();
        CloseRequested?.Invoke();
    }
}
