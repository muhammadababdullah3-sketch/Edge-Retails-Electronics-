using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public enum CatalogReferenceKind
{
    Category,
    Unit
}

public sealed record CatalogReferenceRowViewModel(
    Guid Id,
    string Name,
    string Symbol,
    int DisplayDecimalPlaces,
    bool IsActive)
{
    public string StatusDisplay => IsActive ? "Active" : "Inactive";
}

public sealed class CatalogReferenceManagerViewModel : ViewModelBase
{
    private readonly CatalogReferenceKind _kind;
    private readonly IBackendProductManagementService _service;
    private readonly IToastService _toastService;
    private readonly Action _close;
    private readonly Func<Task> _parentRefresh;
    private CatalogReferenceRowViewModel? _selectedRow;
    private Guid? _editingId;
    private string _name = string.Empty;
    private string _symbol = string.Empty;
    private string _decimalPlacesText = "0";
    private bool _isBusy;
    private string? _errorMessage;

    public CatalogReferenceManagerViewModel(
        CatalogReferenceKind kind,
        IBackendProductManagementService service,
        IToastService toastService,
        Action close,
        Func<Task> parentRefresh)
    {
        _kind = kind;
        _service = service;
        _toastService = toastService;
        _close = close;
        _parentRefresh = parentRefresh;

        Rows = [];
        NewCommand = new RelayCommand(BeginNew, () => !IsBusy);
        SaveCommand = new RelayCommand(() => _ = SaveAsync(), () => !IsBusy);
        ToggleActiveCommand = new RelayCommand<CatalogReferenceRowViewModel>(
            row => _ = ToggleActiveAsync(row));
        CloseCommand = new RelayCommand(close);

        _ = RefreshAsync();
    }

    public ObservableCollection<CatalogReferenceRowViewModel> Rows { get; }
    public bool IsUnitMode => _kind == CatalogReferenceKind.Unit;
    public bool IsCategoryMode => !IsUnitMode;
    public string Title => IsUnitMode ? "Manage Units" : "Manage Categories";
    public string EditorTitle => _editingId.HasValue ? "Edit" : "New";

    public CatalogReferenceRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (SetProperty(ref _selectedRow, value) && value is not null)
            {
                _editingId = value.Id;
                Name = value.Name;
                Symbol = value.Symbol;
                DecimalPlacesText = value.DisplayDecimalPlaces.ToString(CultureInfo.InvariantCulture);
                OnPropertyChanged(nameof(EditorTitle));
            }
        }
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value ?? string.Empty);
    }

    public string Symbol
    {
        get => _symbol;
        set => SetProperty(ref _symbol, value ?? string.Empty);
    }

    public string DecimalPlacesText
    {
        get => _decimalPlacesText;
        set => SetProperty(ref _decimalPlacesText, value ?? string.Empty);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                (NewCommand as RelayCommand)?.NotifyCanExecuteChanged();
                (SaveCommand as RelayCommand)?.NotifyCanExecuteChanged();
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public ICommand NewCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand ToggleActiveCommand { get; }
    public ICommand CloseCommand { get; }

    private async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var snapshot = await _service.GetSnapshotAsync();
            Rows.Clear();

            if (IsUnitMode)
            {
                foreach (var unit in snapshot.Units.OrderBy(x => x.Name))
                {
                    Rows.Add(new CatalogReferenceRowViewModel(
                        unit.Id,
                        unit.Name,
                        unit.Symbol,
                        unit.DisplayDecimalPlaces,
                        unit.IsActive));
                }
            }
            else
            {
                foreach (var category in snapshot.Categories.OrderBy(x => x.Name))
                {
                    Rows.Add(new CatalogReferenceRowViewModel(
                        category.Id,
                        category.Name,
                        string.Empty,
                        0,
                        category.IsActive));
                }
            }
        }
        catch (BackendCatalogOperationException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Catalog references could not be loaded: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void BeginNew()
    {
        _editingId = null;
        SelectedRow = null;
        Name = string.Empty;
        Symbol = string.Empty;
        DecimalPlacesText = "0";
        ErrorMessage = null;
        OnPropertyChanged(nameof(EditorTitle));
    }

    private async Task SaveAsync()
    {
        if (IsBusy)
        {
            return;
        }

        ErrorMessage = null;
        if (string.IsNullOrWhiteSpace(Name))
        {
            ErrorMessage = IsUnitMode ? "Unit name is required." : "Category name is required.";
            return;
        }

        IsBusy = true;
        try
        {
            if (IsUnitMode)
            {
                if (string.IsNullOrWhiteSpace(Symbol) ||
                    !int.TryParse(
                        DecimalPlacesText,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var decimals))
                {
                    ErrorMessage = "Unit symbol and decimal precision are required.";
                    return;
                }

                await _service.SaveUnitAsync(_editingId, Name, Symbol, decimals);
                _toastService.Show("Unit saved.", ToastTone.Success);
            }
            else
            {
                await _service.SaveCategoryAsync(_editingId, Name);
                _toastService.Show("Category saved.", ToastTone.Success);
            }

            BeginNew();
            IsBusy = false;
            await RefreshAsync();
            await _parentRefresh();
        }
        catch (BackendCatalogOperationException ex)
        {
            ErrorMessage = ex.Code switch
            {
                "catalog.category_duplicate" => "A category with this name already exists.",
                "catalog.unit_duplicate" => "A unit with this name or symbol already exists.",
                _ => ex.Message
            };
            _toastService.Show(ErrorMessage, ToastTone.Danger);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Catalog reference save failed: {ex.Message}";
            _toastService.Show(ErrorMessage, ToastTone.Danger);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ToggleActiveAsync(CatalogReferenceRowViewModel row)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            if (IsUnitMode)
            {
                await _service.SetUnitActiveAsync(row.Id, !row.IsActive);
            }
            else
            {
                await _service.SetCategoryActiveAsync(row.Id, !row.IsActive);
            }

            _toastService.Show(
                row.IsActive ? "Catalog reference deactivated." : "Catalog reference reactivated.",
                ToastTone.Success);
            IsBusy = false;
            await RefreshAsync();
            await _parentRefresh();
        }
        catch (BackendCatalogOperationException ex)
        {
            ErrorMessage = ex.Code switch
            {
                "catalog.unit_in_use" => "This unit is in use. Reconfigure active products before deactivating it.",
                "catalog.category_in_use" => "This category is in use. Reassign active products before deactivating it.",
                _ => ex.Message
            };
            _toastService.Show(ErrorMessage, ToastTone.Danger);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Catalog reference update failed: {ex.Message}";
            _toastService.Show(ErrorMessage, ToastTone.Danger);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
