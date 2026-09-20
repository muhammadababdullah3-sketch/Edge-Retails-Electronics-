using System.Globalization;
using System.Windows.Input;

namespace EdgeRetails.Desktop.ViewModels;

/// <summary>
/// Represents an individual item in the active POS sale/issue cart.
/// </summary>
public sealed class PosCartItemViewModel : ViewModelBase
{
    private readonly Action<PosCartItemViewModel>? _onChanged;
    private readonly Action<PosCartItemViewModel>? _onRemove;
    private decimal _quantity;

    public PosCartItemViewModel(
        PosProductItemViewModel product,
        decimal quantity = 1m,
        Action<PosCartItemViewModel>? onChanged = null,
        Action<PosCartItemViewModel>? onRemove = null)
    {
        ArgumentNullException.ThrowIfNull(product);
        Product = product;
        _quantity = Math.Min(product.Stock, Math.Max(0.01m, quantity));
        _onChanged = onChanged;
        _onRemove = onRemove;

        IncrementCommand = new RelayCommand(Increment, () => CanIncrement);
        DecrementCommand = new RelayCommand(Decrement);
        RemoveCommand = new RelayCommand(Remove);
    }

    public PosProductItemViewModel Product { get; }

    public string ProductId => Product.Id;

    public string Name => Product.Name;

    public string Sku => Product.Sku;

    public string Brand => Product.Brand;

    public decimal UnitPrice => Product.Price;

    public decimal Quantity
    {
        get => _quantity;
        set
        {
            var clamped = Math.Clamp(Math.Round(value, 2), 0m, Product.Stock);
            if (SetProperty(ref _quantity, clamped))
            {
                OnPropertyChanged(nameof(QuantityDisplay));
                OnPropertyChanged(nameof(UnitPriceCalculationDisplay));
                OnPropertyChanged(nameof(LineTotal));
                OnPropertyChanged(nameof(LineTotalDisplay));
                OnPropertyChanged(nameof(CanIncrement));
                ((RelayCommand)IncrementCommand).NotifyCanExecuteChanged();
                _onChanged?.Invoke(this);
            }
        }
    }

    public string QuantityDisplay => _quantity.ToString("0.##", CultureInfo.InvariantCulture);

    public string UnitPriceCalculationDisplay => $"Rs. {UnitPrice:N0} × {QuantityDisplay}";

    public decimal LineTotal => Math.Round(UnitPrice * _quantity, 2);

    public string LineTotalDisplay => $"Rs. {LineTotal:N0}";

    public bool CanIncrement => Quantity < Product.Stock;

    public ICommand IncrementCommand { get; }

    public ICommand DecrementCommand { get; }

    public ICommand RemoveCommand { get; }

    public void Increment()
    {
        if (!CanIncrement)
        {
            return;
        }

        Quantity = Math.Min(Product.Stock, Quantity + 1m);
    }

    public void Decrement()
    {
        if (Quantity > 1m)
        {
            Quantity -= 1m;
        }
        else
        {
            Remove();
        }
    }

    public void Remove()
    {
        _onRemove?.Invoke(this);
    }
}
