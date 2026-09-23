using System.Globalization;
using System.Windows.Input;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class PosCartItemViewModel : ViewModelBase
{
    private readonly Action<PosCartItemViewModel>? _onChanged;
    private readonly Action<PosCartItemViewModel>? _onRemove;
    private decimal _quantity;

    public PosCartItemViewModel(
        PosProductItemViewModel product,
        decimal quantity = 1m,
        BackendExactUnit? exactUnit = null,
        Action<PosCartItemViewModel>? onChanged = null,
        Action<PosCartItemViewModel>? onRemove = null)
    {
        ArgumentNullException.ThrowIfNull(product);
        Product = product;
        ExactUnit = exactUnit;
        _quantity = exactUnit is not null
            ? 1m
            : Math.Min(product.Stock, Math.Max(0.01m, quantity));
        _onChanged = onChanged;
        _onRemove = onRemove;

        IncrementCommand = new RelayCommand(Increment, () => CanIncrement);
        DecrementCommand = new RelayCommand(Decrement);
        RemoveCommand = new RelayCommand(Remove);
    }

    public PosProductItemViewModel Product { get; }
    public BackendExactUnit? ExactUnit { get; }
    public string ProductId => Product.Id;
    public string Name => Product.Name;
    public string Sku => Product.Sku;
    public string Brand => Product.Brand;
    public decimal UnitPrice => Product.Price;
    public bool HasExactUnit => ExactUnit is not null;
    public IReadOnlyList<Guid> InventoryUnitIds =>
        ExactUnit is null ? Array.Empty<Guid>() : new[] { ExactUnit.InventoryUnitId };
    public string ExactIdentityDisplay => ExactUnit?.PrimaryIdentity ?? string.Empty;

    public decimal Quantity
    {
        get => _quantity;
        set
        {
            var clamped = HasExactUnit
                ? 1m
                : Math.Clamp(Math.Round(value, 2), 0m, Product.Stock);
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
    public string UnitPriceCalculationDisplay => HasExactUnit
        ? $"{ExactIdentityDisplay} · Rs. {UnitPrice:N0}"
        : $"Rs. {UnitPrice:N0} × {QuantityDisplay}";
    public decimal LineTotal => Math.Round(UnitPrice * _quantity, 2);
    public string LineTotalDisplay => $"Rs. {LineTotal:N0}";
    public bool CanIncrement => !HasExactUnit && Quantity < Product.Stock;
    public ICommand IncrementCommand { get; }
    public ICommand DecrementCommand { get; }
    public ICommand RemoveCommand { get; }

    public void Increment()
    {
        if (CanIncrement)
        {
            Quantity = Math.Min(Product.Stock, Quantity + 1m);
        }
    }

    public void Decrement()
    {
        if (HasExactUnit || Quantity <= 1m)
        {
            Remove();
            return;
        }

        Quantity -= 1m;
    }

    public void Remove() => _onRemove?.Invoke(this);
}
