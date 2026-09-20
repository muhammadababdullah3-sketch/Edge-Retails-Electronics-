using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class NewPurchaseLineViewModel(PosProductItemViewModel product, Action changed) : ViewModelBase
{
    private decimal _quantity = 1m;
    private decimal _cost = product.Cost;
    private decimal _salePrice = product.Price;
    private readonly Action _changed = changed;

    public PosProductItemViewModel Product { get; } = product;
    public string ProductName => Product.Name;
    public string ProductMeta => $"{Product.Brand} · {Product.Sku}";
    public string Unit => Product.Unit;

    public decimal Quantity
    {
        get => _quantity;
        set
        {
            var normalized = Math.Max(0m, Math.Round(value, 2));
            if (SetProperty(ref _quantity, normalized))
            {
                OnPropertyChanged(nameof(LineTotal));
                OnPropertyChanged(nameof(LineTotalDisplay));
                _changed();
            }
        }
    }

    public decimal Cost
    {
        get => _cost;
        set
        {
            var normalized = Math.Max(0m, Math.Round(value, 2));
            if (SetProperty(ref _cost, normalized))
            {
                OnPropertyChanged(nameof(LineTotal));
                OnPropertyChanged(nameof(LineTotalDisplay));
                _changed();
            }
        }
    }

    public decimal SalePrice
    {
        get => _salePrice;
        set => SetProperty(ref _salePrice, Math.Max(0m, Math.Round(value, 2)));
    }
    public decimal LineTotal => Math.Round(Quantity * Cost, 2);
    public string LineTotalDisplay => $"Rs. {LineTotal:N0}";
}

public sealed class NewPurchaseViewModel : ViewModelBase
{
    private readonly DemoPurchaseInventoryService _service;
    private readonly IToastService? _toastService;
    private readonly Action? _cancel;
    private readonly Action<PurchaseRecord>? _saved;
    private string _selectedSupplier = string.Empty;
    private string _invoiceNumber = string.Empty;
    private DateTime _purchaseDate = DateTime.Today;
    private string _note = string.Empty;
    private string _searchText = string.Empty;
    private PosProductItemViewModel? _selectedProduct;
    private decimal _otherCharges;

    public NewPurchaseViewModel(
        IToastService? toastService = null,
        Action? cancel = null,
        Action<PurchaseRecord>? saved = null)
    {
        _service = DemoPurchaseInventoryService.Instance;
        _toastService = toastService;
        _cancel = cancel;
        _saved = saved;

        Suppliers = _service.Suppliers;
        Products = new ObservableCollection<PosProductItemViewModel>(DemoRetailState.Instance.Products);
        FilteredProducts = new ObservableCollection<PosProductItemViewModel>(Products);
        Lines = [];

        AddSelectedProductCommand = new RelayCommand(AddSelectedProduct, () => SelectedProduct != null);
        RemoveLineCommand = new RelayCommand<NewPurchaseLineViewModel>(RemoveLine);
        SavePurchaseCommand = new RelayCommand(SavePurchase, () => CanSave);
        CancelCommand = new RelayCommand(() => _cancel?.Invoke());
    }

    public IReadOnlyList<string> Suppliers { get; }
    public ObservableCollection<PosProductItemViewModel> Products { get; }
    public ObservableCollection<PosProductItemViewModel> FilteredProducts { get; }
    public ObservableCollection<NewPurchaseLineViewModel> Lines { get; }

    public string SelectedSupplier
    {
        get => _selectedSupplier;
        set
        {
            if (SetProperty(ref _selectedSupplier, value ?? string.Empty))
            {
                RefreshCanSave();
            }
        }
    }

    public string InvoiceNumber
    {
        get => _invoiceNumber;
        set
        {
            if (SetProperty(ref _invoiceNumber, value ?? string.Empty))
            {
                RefreshCanSave();
            }
        }
    }
    public DateTime PurchaseDate
    {
        get => _purchaseDate;
        set => SetProperty(ref _purchaseDate, value);
    }

    public string Note
    {
        get => _note;
        set => SetProperty(ref _note, value ?? string.Empty);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                ApplyProductFilter();
            }
        }
    }

    public PosProductItemViewModel? SelectedProduct
    {
        get => _selectedProduct;
        set
        {
            if (SetProperty(ref _selectedProduct, value))
            {
                ((RelayCommand)AddSelectedProductCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public decimal OtherCharges
    {
        get => _otherCharges;
        set
        {
            if (SetProperty(ref _otherCharges, Math.Max(0m, Math.Round(value, 2))))
            {
                Recalculate();
            }
        }
    }
    public string OtherChargesText
    {
        get => OtherCharges.ToString("0.##", CultureInfo.InvariantCulture);
        set
        {
            if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
            {
                OtherCharges = parsed;
            }
            else if (string.IsNullOrWhiteSpace(value))
            {
                OtherCharges = 0m;
            }
        }
    }

    public decimal Subtotal => Lines.Sum(line => line.LineTotal);
    public decimal Total => Subtotal + OtherCharges;
    public string SubtotalDisplay => $"Rs. {Subtotal:N0}";
    public string TotalDisplay => $"Rs. {Total:N0}";
    public bool CanSave =>
        !string.IsNullOrWhiteSpace(SelectedSupplier) &&
        !string.IsNullOrWhiteSpace(InvoiceNumber) &&
        Lines.Count > 0 &&
        Lines.All(line => line.Quantity > 0m);

    public ICommand AddSelectedProductCommand { get; }
    public ICommand RemoveLineCommand { get; }
    public ICommand SavePurchaseCommand { get; }
    public ICommand CancelCommand { get; }

    private void AddSelectedProduct()
    {
        if (SelectedProduct is null)
        {
            return;
        }

        var existing = Lines.FirstOrDefault(line => line.Product.Id == SelectedProduct.Id);
        if (existing is not null)
        {
            existing.Quantity += 1m;
        }
        else
        {
            Lines.Add(new NewPurchaseLineViewModel(SelectedProduct, Recalculate));
        }

        Recalculate();
    }

    private void RemoveLine(NewPurchaseLineViewModel line)
    {
        if (Lines.Remove(line))
        {
            Recalculate();
        }
    }

    private void SavePurchase()
    {
        if (!CanSave)
        {
            return;
        }

        try
        {
            var record = _service.SavePurchase(
                SelectedSupplier,
                InvoiceNumber,
                PurchaseDate,
                Note,
                OtherCharges,
                Lines.Select(line => new PurchaseDraftLine
                {
                    Product = line.Product,
                    Quantity = line.Quantity,
                    Cost = line.Cost,
                    SalePrice = line.SalePrice
                }));

            _toastService?.Show(
                $"{record.PurchaseNumber} saved. Stock updated for {record.ItemCount} products.",
                ToastTone.Success);
            _saved?.Invoke(record);
        }
        catch (Exception ex)
        {
            _toastService?.Show(ex.Message, ToastTone.Danger);
        }
    }

    private void ApplyProductFilter()
    {
        var term = SearchText.Trim();
        var results = string.IsNullOrWhiteSpace(term)
            ? Products
            : Products.Where(product =>
                product.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                product.Sku.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                product.Brand.Contains(term, StringComparison.OrdinalIgnoreCase));

        FilteredProducts.Clear();
        foreach (var product in results)
        {
            FilteredProducts.Add(product);
        }
    }

    private void Recalculate()
    {
        OnPropertyChanged(nameof(Subtotal));
        OnPropertyChanged(nameof(Total));
        OnPropertyChanged(nameof(SubtotalDisplay));
        OnPropertyChanged(nameof(TotalDisplay));
        RefreshCanSave();
    }

    private void RefreshCanSave()
    {
        OnPropertyChanged(nameof(CanSave));
        ((RelayCommand)SavePurchaseCommand).NotifyCanExecuteChanged();
    }
}
