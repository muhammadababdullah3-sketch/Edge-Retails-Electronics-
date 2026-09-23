using System.Collections.ObjectModel;
using System.Windows.Input;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Domain.Inventory;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class ExactUnitOptionViewModel : ViewModelBase
{
    private readonly Action _changed;
    private bool _isSelected;

    public ExactUnitOptionViewModel(BackendExactUnit unit, Action changed)
    {
        Unit = unit;
        _changed = changed;
    }

    public BackendExactUnit Unit { get; }
    public Guid InventoryUnitId => Unit.InventoryUnitId;
    public string TrackingCode => Unit.TrackingCode;
    public string ManufacturerIdentity => Unit.ManufacturerIdentity;
    public string Status => Unit.Status.ToString();
    public string Provenance => Unit.ProvenanceDisplay;
    public string CostDisplay => $"Rs. {Unit.AcquisitionCost:N0}";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                _changed();
            }
        }
    }
}

public sealed class ExactUnitPickerViewModel : ViewModelBase
{
    private readonly IBackendPhase4WorkflowService _service;
    private readonly IDialogService _dialogService;
    private readonly Action<IReadOnlyList<BackendExactUnit>> _confirmed;
    private readonly Guid _productId;
    private readonly InventoryUnitStatus? _status;
    private readonly Guid? _sourcePurchaseItemId;
    private readonly int _requiredCount;
    private readonly bool _selectionRequired;
    private readonly List<ExactUnitOptionViewModel> _all = [];
    private string _searchText = string.Empty;
    private bool _isLoading;
    private string? _validationMessage;

    public ExactUnitPickerViewModel(
        string title,
        string subtitle,
        Guid productId,
        IBackendPhase4WorkflowService service,
        IDialogService dialogService,
        Action<IReadOnlyList<BackendExactUnit>> confirmed,
        int requiredCount = 1,
        InventoryUnitStatus? status = InventoryUnitStatus.InStock,
        Guid? sourcePurchaseItemId = null,
        bool selectionRequired = true)
    {
        Title = title;
        Subtitle = subtitle;
        _productId = productId;
        _service = service;
        _dialogService = dialogService;
        _confirmed = confirmed;
        _requiredCount = Math.Max(0, requiredCount);
        _status = status;
        _sourcePurchaseItemId = sourcePurchaseItemId;
        _selectionRequired = selectionRequired;

        Units = [];
        ConfirmCommand = new RelayCommand(Confirm, () => CanConfirm);
        CancelCommand = new RelayCommand(_dialogService.Close);
        RefreshCommand = new RelayCommand(async () => await LoadAsync(), () => !IsLoading);
        _ = LoadAsync();
    }

    public string Title { get; }
    public string Subtitle { get; }
    public ObservableCollection<ExactUnitOptionViewModel> Units { get; }
    public string ConfirmLabel => _selectionRequired ? "Use Selected Unit(s)" : "Close";
    public string SelectionHint => !_selectionRequired
        ? "Authoritative physical-unit view"
        : _requiredCount == 1
            ? "Select one exact physical unit."
            : $"Select exactly {_requiredCount} physical units.";
    public int SelectedCount => _all.Count(x => x.IsSelected);
    public string SelectedCountDisplay => _selectionRequired
        ? $"{SelectedCount}/{_requiredCount} selected"
        : $"{_all.Count} unit(s)";

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                ApplyFilter();
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                ((RelayCommand)RefreshCommand).NotifyCanExecuteChanged();
            }
        }
    }

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

    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(ValidationMessage);
    public bool CanConfirm => !_selectionRequired || SelectedCount == _requiredCount;
    public ICommand ConfirmCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand RefreshCommand { get; }

    private async Task LoadAsync()
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        ValidationMessage = null;
        try
        {
            var rows = await _service.GetExactUnitsAsync(
                _productId,
                _status,
                _sourcePurchaseItemId);

            _all.Clear();
            foreach (var row in rows)
            {
                _all.Add(new ExactUnitOptionViewModel(row, SelectionChanged));
            }

            ApplyFilter();
            if (_selectionRequired && rows.Count == 0)
            {
                ValidationMessage = "No eligible physical units are currently available.";
            }
        }
        catch (BackendOperationException ex)
        {
            ValidationMessage = $"{ex.Code}: {ex.Message}";
        }
        catch (Exception ex)
        {
            ValidationMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyFilter()
    {
        var term = SearchText.Trim();
        var source = string.IsNullOrWhiteSpace(term)
            ? _all
            : _all.Where(x =>
                x.TrackingCode.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                x.ManufacturerIdentity.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                x.Provenance.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                x.InventoryUnitId.ToString("D").Contains(term, StringComparison.OrdinalIgnoreCase))
                .ToList();

        Units.Clear();
        foreach (var row in source)
        {
            Units.Add(row);
        }
    }

    private void SelectionChanged()
    {
        if (_selectionRequired && _requiredCount == 1)
        {
            var selected = _all.Where(x => x.IsSelected).ToArray();
            if (selected.Length > 1)
            {
                foreach (var row in selected.Take(selected.Length - 1))
                {
                    row.IsSelected = false;
                }
            }
        }

        ValidationMessage = null;
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedCountDisplay));
        OnPropertyChanged(nameof(CanConfirm));
        ((RelayCommand)ConfirmCommand).NotifyCanExecuteChanged();
    }

    private void Confirm()
    {
        if (!CanConfirm)
        {
            ValidationMessage = $"Select exactly {_requiredCount} eligible unit(s).";
            return;
        }

        _confirmed(_all.Where(x => x.IsSelected).Select(x => x.Unit).ToArray());
        _dialogService.Close();
    }
}
