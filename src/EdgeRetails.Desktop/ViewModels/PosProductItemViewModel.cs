using EdgeRetails.Desktop.Controls;

namespace EdgeRetails.Desktop.ViewModels;

/// <summary>
/// Shared product projection used by POS, Thaka and Sprint 4 inventory workflows.
/// Stock remains a result of controlled mutations; it is never a free-edit field.
/// </summary>
public sealed class PosProductItemViewModel : ViewModelBase
{
    private string _name;
    private string _sku;
    private string _brand;
    private string _category;
    private string _unit;
    private string _model;
    private decimal _stock;
    private decimal _price;
    private decimal _cost;
    private decimal _minimumStock;
    private bool _isInStock;
    private string _stockDisplay;
    private BadgeTone _stockTone;

    public PosProductItemViewModel(
        string id,
        string name,
        string sku,
        string brand,
        string category,
        decimal stock,
        decimal price,
        string? stockLabel = null,
        BadgeTone? stockTone = null,
        string unit = "Pcs",
        decimal cost = 0m,
        decimal minimumStock = 10m,
        string model = "",
        Guid? backendProductId = null,
        Guid? backendProductUnitId = null,
        bool isSerialized = false,
        bool serialTrackingEnabled = false,
        bool imeiTrackingEnabled = false,
        decimal factorToBaseUnit = 1m)
    {
        Id = id;
        BackendProductId = backendProductId;
        BackendProductUnitId = backendProductUnitId;
        IsSerialized = isSerialized;
        SerialTrackingEnabled = serialTrackingEnabled;
        ImeiTrackingEnabled = imeiTrackingEnabled;
        FactorToBaseUnit = factorToBaseUnit <= 0m ? 1m : factorToBaseUnit;
        _name = name;
        _sku = sku;
        _brand = brand;
        _category = category;
        _price = Math.Max(0m, price);
        _cost = Math.Max(0m, cost);
        _minimumStock = Math.Max(0m, minimumStock);
        _model = model ?? string.Empty;
        _unit = string.IsNullOrWhiteSpace(unit) ? "Pcs" : unit;
        _stock = Math.Max(0m, stock);
        _isInStock = _stock > 0m;

        if (stockTone.HasValue && !string.IsNullOrWhiteSpace(stockLabel))
        {
            _stockDisplay = stockLabel;
            _stockTone = stockTone.Value;
        }
        else
        {
            (_stockDisplay, _stockTone) = BuildStockState(_stock, _minimumStock);
        }
    }

    public string Id { get; }

    public Guid? BackendProductId { get; }

    public Guid? BackendProductUnitId { get; }

    public bool IsSerialized { get; }
    public bool SerialTrackingEnabled { get; }
    public bool ImeiTrackingEnabled { get; }
    public decimal FactorToBaseUnit { get; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value?.Trim() ?? string.Empty);
    }

    public string Sku
    {
        get => _sku;
        set => SetProperty(ref _sku, value?.Trim() ?? string.Empty);
    }

    public string Brand
    {
        get => _brand;
        set => SetProperty(ref _brand, value?.Trim() ?? string.Empty);
    }

    public string Category
    {
        get => _category;
        set => SetProperty(ref _category, value?.Trim() ?? string.Empty);
    }

    public string Unit
    {
        get => _unit;
        set => SetProperty(ref _unit, string.IsNullOrWhiteSpace(value) ? "Pcs" : value.Trim());
    }

    public string Model
    {
        get => _model;
        set => SetProperty(ref _model, value?.Trim() ?? string.Empty);
    }

    public decimal Price
    {
        get => _price;
        set
        {
            var normalized = Math.Max(0m, Math.Round(value, 2));
            if (SetProperty(ref _price, normalized))
            {
                OnPropertyChanged(nameof(PriceDisplay));
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
                OnPropertyChanged(nameof(CostDisplay));
            }
        }
    }

    public decimal MinimumStock
    {
        get => _minimumStock;
        set
        {
            var normalized = Math.Max(0m, Math.Round(value, 2));
            if (SetProperty(ref _minimumStock, normalized))
            {
                RefreshStockState();
                OnPropertyChanged(nameof(MinimumStockDisplay));
                OnPropertyChanged(nameof(IsLowStock));
            }
        }
    }

    public string PriceDisplay => $"Rs. {Price:N0}";
    public string CostDisplay => $"Rs. {Cost:N0}";
    public string MinimumStockDisplay => MinimumStock.ToString("0.##");

    public decimal Stock
    {
        get => _stock;
        set
        {
            var normalized = Math.Max(0m, Math.Round(value, 2));
            if (SetProperty(ref _stock, normalized))
            {
                RefreshStockState();
                OnPropertyChanged(nameof(IsLowStock));
                OnPropertyChanged(nameof(IsOutOfStock));
            }
        }
    }

    public bool IsLowStock => Stock > 0m && Stock <= MinimumStock;
    public bool IsOutOfStock => Stock <= 0m;

    public bool IsInStock
    {
        get => _isInStock;
        private set
        {
            if (SetProperty(ref _isInStock, value))
            {
                OnPropertyChanged(nameof(IsDimmed));
            }
        }
    }

    public bool IsDimmed => !IsInStock;

    public string StockDisplay
    {
        get => _stockDisplay;
        private set => SetProperty(ref _stockDisplay, value);
    }

    public BadgeTone StockTone
    {
        get => _stockTone;
        private set => SetProperty(ref _stockTone, value);
    }

    private void RefreshStockState()
    {
        IsInStock = Stock > 0m;
        var (display, tone) = BuildStockState(Stock, MinimumStock);
        StockDisplay = display;
        StockTone = tone;
    }

    private static (string Display, BadgeTone Tone) BuildStockState(decimal stock, decimal minimumStock)
    {
        if (stock <= 0m)
        {
            return ("Out of Stock", BadgeTone.Danger);
        }

        if (stock <= minimumStock)
        {
            return ($"{stock:0.##} left", BadgeTone.Warning);
        }

        return ($"{stock:0.##} in stock", BadgeTone.Success);
    }
}
