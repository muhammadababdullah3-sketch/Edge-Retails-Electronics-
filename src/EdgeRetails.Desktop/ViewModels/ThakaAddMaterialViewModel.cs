using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class ThakaMaterialProductOption
{
    public ThakaMaterialProductOption(PosProductItemViewModel product)
    {
        Product = product ?? throw new ArgumentNullException(nameof(product));
    }

    public PosProductItemViewModel Product { get; }
    public string Id => Product.Id;
    public string Name => Product.Name;
    public string Sku => Product.Sku;
    public string Unit => Product.Unit;
    public decimal AvailableStock => Product.Stock;
    public decimal UnitPrice => Product.Price;
    public string DisplayName => $"{Name} · {Sku}";
    public string AvailableStockDisplay => $"{AvailableStock:0.##} {Unit}";
    public string UnitPriceDisplay => $"Rs. {UnitPrice:N0}";
}

public sealed class ThakaAddMaterialViewModel : ViewModelBase
{
    private readonly ThakaProjectListItemViewModel _project;
    private readonly Action<MaterialLedgerEntry>? _onMaterialAdded;
    private readonly Action? _onClose;
    private readonly IToastService? _toastService;
    private readonly DemoRetailState _retailState = DemoRetailState.Instance;
    private string _searchText = string.Empty;
    private ThakaMaterialProductOption? _selectedProduct;
    private decimal _quantity = 1m;
    private string _quantityText = "1";
    private bool _isProcessing;
    private string? _validationMessage;

    public ThakaAddMaterialViewModel(
        ThakaProjectListItemViewModel project,
        Action<MaterialLedgerEntry>? onMaterialAdded = null,
        Action? onClose = null,
        IToastService? toastService = null)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        _onMaterialAdded = onMaterialAdded;
        _onClose = onClose;
        _toastService = toastService;

        Products = new ReadOnlyCollection<ThakaMaterialProductOption>(
            _retailState.Products
                .Select(product => new ThakaMaterialProductOption(product))
                .ToList());

        FilteredProducts = new ObservableCollection<ThakaMaterialProductOption>(Products);
        SelectedProduct = Products[0];

        IssueMaterialCommand = new RelayCommand(IssueMaterial, () => CanIssueMaterial);
        CancelCommand = new RelayCommand(Cancel, () => !IsProcessing);
    }

    public string ProjectName => _project.ProjectName;
    public decimal CurrentMaterialValue => _project.MaterialValue;
    public string CurrentMaterialValueDisplay => $"Rs. {CurrentMaterialValue:N0}";
    public IReadOnlyList<ThakaMaterialProductOption> Products { get; }
    public ObservableCollection<ThakaMaterialProductOption> FilteredProducts { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ApplyFilter();
            }
        }
    }

    public ThakaMaterialProductOption? SelectedProduct
    {
        get => _selectedProduct;
        set
        {
            if (SetProperty(ref _selectedProduct, value))
            {
                OnPropertyChanged(nameof(AvailableStockDisplay));
                OnPropertyChanged(nameof(UnitPriceDisplay));
                Recalculate();
            }
        }
    }

    public decimal Quantity
    {
        get => _quantity;
        set
        {
            var rounded = Math.Max(0m, Math.Round(value, 2));
            if (SetProperty(ref _quantity, rounded))
            {
                _quantityText = rounded.ToString("0.##", CultureInfo.InvariantCulture);
                OnPropertyChanged(nameof(QuantityText));
                Recalculate();
            }
        }
    }

    public string QuantityText
    {
        get => _quantityText;
        set
        {
            if (SetProperty(ref _quantityText, value ?? string.Empty))
            {
                if (decimal.TryParse(_quantityText, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ||
                    decimal.TryParse(_quantityText, NumberStyles.Number, CultureInfo.CurrentCulture, out parsed))
                {
                    _quantity = Math.Max(0m, Math.Round(parsed, 2));
                    OnPropertyChanged(nameof(Quantity));
                }
                else if (string.IsNullOrWhiteSpace(_quantityText))
                {
                    _quantity = 0m;
                    OnPropertyChanged(nameof(Quantity));
                }

                Recalculate();
            }
        }
    }

    public string AvailableStockDisplay => SelectedProduct == null ? "Select a product" : $"Available Stock: {SelectedProduct.AvailableStockDisplay}";
    public string UnitPriceDisplay => SelectedProduct?.UnitPriceDisplay ?? "Rs. 0";
    public decimal ThisIssueValue => SelectedProduct == null ? 0m : Math.Round(SelectedProduct.UnitPrice * Quantity, 2);
    public string ThisIssueValueDisplay => $"Rs. {ThisIssueValue:N0}";
    public decimal NewMaterialValue => CurrentMaterialValue + ThisIssueValue;
    public string NewMaterialValueDisplay => $"Rs. {NewMaterialValue:N0}";

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

    public bool IsProcessing
    {
        get => _isProcessing;
        private set
        {
            if (SetProperty(ref _isProcessing, value))
            {
                OnPropertyChanged(nameof(CanIssueMaterial));
                ((RelayCommand)IssueMaterialCommand).NotifyCanExecuteChanged();
                ((RelayCommand)CancelCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public bool CanIssueMaterial =>
        !IsProcessing &&
        SelectedProduct != null &&
        Quantity > 0m &&
        Quantity <= SelectedProduct.AvailableStock;

    public ICommand IssueMaterialCommand { get; }
    public ICommand CancelCommand { get; }

    private void ApplyFilter()
    {
        var term = SearchText.Trim();
        var query = string.IsNullOrWhiteSpace(term)
            ? Products
            : Products.Where(p =>
                p.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                p.Sku.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();

        FilteredProducts.Clear();
        foreach (var product in query)
        {
            FilteredProducts.Add(product);
        }
    }

    private void Recalculate()
    {
        if (SelectedProduct == null)
        {
            ValidationMessage = "Select a product.";
        }
        else if (Quantity <= 0m)
        {
            ValidationMessage = "Quantity must be greater than zero.";
        }
        else if (Quantity > SelectedProduct.AvailableStock)
        {
            ValidationMessage = $"Only {SelectedProduct.AvailableStock:0.##} {SelectedProduct.Unit} are available.";
        }
        else
        {
            ValidationMessage = null;
        }

        OnPropertyChanged(nameof(ThisIssueValue));
        OnPropertyChanged(nameof(ThisIssueValueDisplay));
        OnPropertyChanged(nameof(NewMaterialValue));
        OnPropertyChanged(nameof(NewMaterialValueDisplay));
        OnPropertyChanged(nameof(CanIssueMaterial));
        ((RelayCommand)IssueMaterialCommand).NotifyCanExecuteChanged();
    }

    private void IssueMaterial()
    {
        if (!CanIssueMaterial || SelectedProduct == null)
        {
            return;
        }

        IsProcessing = true;
        try
        {
            var entry = _retailState.IssueMaterial(
                _project,
                SelectedProduct.Product,
                Quantity);

            _onMaterialAdded?.Invoke(entry);
            _toastService?.Show(
                $"{Quantity:0.##} {SelectedProduct.Unit} of {SelectedProduct.Name} issued to {ProjectName}.",
                ToastTone.Success);
            _onClose?.Invoke();
        }
        catch (Exception ex)
        {
            ValidationMessage = ex.Message;
            _toastService?.Show(ex.Message, ToastTone.Danger);
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private void Cancel()
    {
        if (!IsProcessing)
        {
            _onClose?.Invoke();
        }
    }
}
