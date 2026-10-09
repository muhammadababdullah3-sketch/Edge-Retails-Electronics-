using System.Globalization;
using System.Windows.Input;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class PosCartItemViewModel : ViewModelBase
{
    private readonly Action<PosCartItemViewModel>? _onChanged;
    private readonly Action<PosCartItemViewModel>? _onRemove;
    private decimal _quantity;
    private readonly bool _canOverridePrice;
    private decimal? _priceOverrideUnitPrice;
    private string? _priceOverrideReason;
    private string _priceOverrideUnitPriceText;
    private string _priceOverrideReasonInput = string.Empty;
    private bool _isPriceOverrideEditorOpen;

    public PosCartItemViewModel(
        PosProductItemViewModel product,
        decimal quantity = 1m,
        BackendExactUnit? exactUnit = null,
        Action<PosCartItemViewModel>? onChanged = null,
        Action<PosCartItemViewModel>? onRemove = null,
        bool canOverridePrice = false)
    {
        ArgumentNullException.ThrowIfNull(product);
        Product = product;
        ExactUnit = exactUnit;
        _quantity = exactUnit is not null
            ? 1m
            : Math.Min(product.Stock, Math.Max(0.01m, quantity));
        _onChanged = onChanged;
        _onRemove = onRemove;
        _canOverridePrice = canOverridePrice;
        _priceOverrideUnitPriceText = product.Price.ToString("0.##", CultureInfo.InvariantCulture);

        IncrementCommand = new RelayCommand(Increment, () => CanIncrement);
        DecrementCommand = new RelayCommand(Decrement);
        RemoveCommand = new RelayCommand(Remove);
        OpenPriceOverrideCommand = new RelayCommand(TogglePriceOverrideEditor, () => _canOverridePrice);
        ApplyPriceOverrideCommand = new RelayCommand(ApplyPriceOverride, () => CanApplyPriceOverride);
        CancelPriceOverrideCommand = new RelayCommand(CancelPriceOverride);
    }

    public PosProductItemViewModel Product { get; }
    public BackendExactUnit? ExactUnit { get; }
    public string ProductId => Product.Id;
    public string Name => Product.Name;
    public string Sku => Product.Sku;
    public string Brand => Product.Brand;
    public decimal UnitPrice => PriceOverrideUnitPrice ?? Product.Price;
    public decimal ListUnitPrice => Product.Price;
    public decimal? PriceOverrideUnitPrice => _priceOverrideUnitPrice;
    public string? PriceOverrideReason => _priceOverrideReason;
    public bool CanRequestPriceOverride => _canOverridePrice;
    public bool IsPriceOverridden => _priceOverrideUnitPrice is not null;
    public bool IsPriceOverrideEditorOpen
    {
        get => _isPriceOverrideEditorOpen;
        private set
        {
            if (SetProperty(ref _isPriceOverrideEditorOpen, value))
            {
                OnPropertyChanged(nameof(IsPriceOverrideEditorOpen));
            }
        }
    }
    public string PriceOverrideButtonLabel => IsPriceOverridden ? "Edit override" : "Override price";
    public string PriceOverrideSummary => IsPriceOverridden
        ? $"Price override · list Rs. {ListUnitPrice:N2} → Rs. {UnitPrice:N2} · {_priceOverrideReason}"
        : string.Empty;

    public string PriceOverrideUnitPriceText
    {
        get => _priceOverrideUnitPriceText;
        set
        {
            if (SetProperty(ref _priceOverrideUnitPriceText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(CanApplyPriceOverride));
                ((RelayCommand)ApplyPriceOverrideCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public string PriceOverrideReasonInput
    {
        get => _priceOverrideReasonInput;
        set
        {
            if (SetProperty(ref _priceOverrideReasonInput, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(CanApplyPriceOverride));
                ((RelayCommand)ApplyPriceOverrideCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public bool CanApplyPriceOverride =>
        _canOverridePrice &&
        decimal.TryParse(PriceOverrideUnitPriceText, NumberStyles.Number, CultureInfo.InvariantCulture, out var price) &&
        price >= 0m &&
        !string.IsNullOrWhiteSpace(PriceOverrideReasonInput);
    public bool HasExactUnit => ExactUnit is not null;
    public Guid? InventoryUnitId => ExactUnit?.InventoryUnitId;
    public string? TrackingCode => ExactUnit?.TrackingCode;
    public Guid? ProductUnitId => Product.BackendProductUnitId;
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
        ? $"{ExactIdentityDisplay} · Rs. {UnitPrice:N2}"
        : $"Rs. {UnitPrice:N2} × {QuantityDisplay}";
    public decimal LineTotal => Math.Round(UnitPrice * _quantity, 2);
    public string LineTotalDisplay => $"Rs. {LineTotal:N2}";
    public bool CanIncrement => !HasExactUnit && Quantity < Product.Stock;
    public ICommand IncrementCommand { get; }
    public ICommand DecrementCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand OpenPriceOverrideCommand { get; }
    public ICommand ApplyPriceOverrideCommand { get; }
    public ICommand CancelPriceOverrideCommand { get; }

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

    private void TogglePriceOverrideEditor()
    {
        PriceOverrideUnitPriceText = (PriceOverrideUnitPrice ?? Product.Price)
            .ToString("0.##", CultureInfo.InvariantCulture);
        PriceOverrideReasonInput = PriceOverrideReason ?? string.Empty;
        IsPriceOverrideEditorOpen = !IsPriceOverrideEditorOpen;
    }

    private void ApplyPriceOverride()
    {
        if (!CanApplyPriceOverride ||
            !decimal.TryParse(PriceOverrideUnitPriceText, NumberStyles.Number, CultureInfo.InvariantCulture, out var price))
        {
            return;
        }

        _priceOverrideUnitPrice = Math.Round(price, 2, MidpointRounding.AwayFromZero);
        _priceOverrideReason = PriceOverrideReasonInput.Trim();
        IsPriceOverrideEditorOpen = false;
        OnPropertyChanged(nameof(PriceOverrideUnitPrice));
        OnPropertyChanged(nameof(PriceOverrideReason));
        OnPropertyChanged(nameof(IsPriceOverridden));
        OnPropertyChanged(nameof(PriceOverrideButtonLabel));
        OnPropertyChanged(nameof(PriceOverrideSummary));
        OnPropertyChanged(nameof(UnitPrice));
        OnPropertyChanged(nameof(UnitPriceCalculationDisplay));
        OnPropertyChanged(nameof(LineTotal));
        OnPropertyChanged(nameof(LineTotalDisplay));
        _onChanged?.Invoke(this);
    }

    private void CancelPriceOverride()
    {
        if (IsPriceOverridden)
        {
            _priceOverrideUnitPrice = null;
            _priceOverrideReason = null;
            OnPropertyChanged(nameof(PriceOverrideUnitPrice));
            OnPropertyChanged(nameof(PriceOverrideReason));
            OnPropertyChanged(nameof(IsPriceOverridden));
            OnPropertyChanged(nameof(PriceOverrideButtonLabel));
            OnPropertyChanged(nameof(PriceOverrideSummary));
            OnPropertyChanged(nameof(UnitPrice));
            OnPropertyChanged(nameof(UnitPriceCalculationDisplay));
            OnPropertyChanged(nameof(LineTotal));
            OnPropertyChanged(nameof(LineTotalDisplay));
            _onChanged?.Invoke(this);
        }

        IsPriceOverrideEditorOpen = false;
    }

    public void RefreshFromProduct()
    {
        if (!IsPriceOverridden)
        {
            PriceOverrideUnitPriceText = Product.Price.ToString("0.##", CultureInfo.InvariantCulture);
        }

        OnPropertyChanged(nameof(UnitPrice));
        OnPropertyChanged(nameof(ListUnitPrice));
        OnPropertyChanged(nameof(UnitPriceCalculationDisplay));
        OnPropertyChanged(nameof(LineTotal));
        OnPropertyChanged(nameof(LineTotalDisplay));
        OnPropertyChanged(nameof(PriceOverrideSummary));
        OnPropertyChanged(nameof(CanIncrement));
        ((RelayCommand)IncrementCommand).NotifyCanExecuteChanged();
        _onChanged?.Invoke(this);
    }
}
