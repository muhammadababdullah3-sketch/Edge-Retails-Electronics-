using System.Globalization;
using System.Windows.Input;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class ProductEditViewModel : ViewModelBase
{
    private readonly DemoRetailState _retailState;
    private readonly IToastService _toastService;
    private readonly Action _close;
    private readonly PosProductItemViewModel? _product;

    public ProductEditViewModel(
        PosProductItemViewModel? product,
        IToastService toastService,
        Action close)
    {
        _product = product;
        _toastService = toastService;
        _close = close;
        _retailState = DemoRetailState.Instance;
        Name = product?.Name ?? string.Empty;
        Sku = product?.Sku ?? string.Empty;
        Brand = product?.Brand ?? string.Empty;
        Category = product?.Category ?? string.Empty;
        Unit = product?.Unit ?? "Pcs";
        Model = product?.Model ?? string.Empty;
        CostText = (product?.Cost ?? 0m).ToString("0.##", CultureInfo.InvariantCulture);
        SalePriceText = (product?.Price ?? 0m).ToString("0.##", CultureInfo.InvariantCulture);
        MinimumStockText = (product?.MinimumStock ?? 10m).ToString("0.##", CultureInfo.InvariantCulture);
        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(close);
    }

    public bool IsEdit => _product is not null;
    public string Title => IsEdit ? "Edit Product" : "Add Product";
    public string Name { get; set; }
    public string Sku { get; set; }
    public string Brand { get; set; }
    public string Category { get; set; }
    public string Unit { get; set; }
    public string Model { get; set; }
    public string CostText { get; set; }
    public string SalePriceText { get; set; }
    public string MinimumStockText { get; set; }
    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Sku) ||
            string.IsNullOrWhiteSpace(Brand) || string.IsNullOrWhiteSpace(Category))
        {
            _toastService.Show("Name, SKU, brand and category are required.", ToastTone.Warning);
            return;
        }

        if (!decimal.TryParse(CostText, NumberStyles.Number, CultureInfo.InvariantCulture, out var cost) ||
            !decimal.TryParse(SalePriceText, NumberStyles.Number, CultureInfo.InvariantCulture, out var salePrice) ||
            !decimal.TryParse(MinimumStockText, NumberStyles.Number, CultureInfo.InvariantCulture, out var minimumStock) ||
            cost < 0m || salePrice < 0m || minimumStock < 0m)
        {
            _toastService.Show("Cost, sale price and minimum stock must be valid non-negative numbers.", ToastTone.Warning);
            return;
        }

        var normalizedSku = Sku.Trim();
        if (_retailState.Products.Any(existing =>
            !ReferenceEquals(existing, _product) &&
            string.Equals(existing.Sku, normalizedSku, StringComparison.OrdinalIgnoreCase)))
        {
            _toastService.Show("Another product already uses this SKU.", ToastTone.Warning);
            return;
        }

        try
        {
            if (_product is null)
            {
                var id = $"P{_retailState.Products.Count + 1}";
                _retailState.AddProduct(new PosProductItemViewModel(
                    id, Name, Sku, Brand, Category, 0m, salePrice,
                    unit: Unit, cost: cost, minimumStock: minimumStock, model: Model));
            }
            else
            {
                _product.Name = Name;
                _product.Sku = Sku;
                _product.Brand = Brand;
                _product.Category = Category;
                _product.Unit = Unit;
                _product.Model = Model;
                _product.Cost = cost;
                _product.Price = salePrice;
                _product.MinimumStock = minimumStock;
                _retailState.NotifyProductChanged();
            }

            _toastService.Show(IsEdit ? "Product updated." : "Product added.", ToastTone.Success);
            _close();
        }
        catch (Exception ex)
        {
            _toastService.Show(ex.Message, ToastTone.Danger);
        }
    }
}
