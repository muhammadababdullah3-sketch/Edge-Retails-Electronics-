using System.Globalization;
using System.Windows.Input;
using EdgeRetails.Application.Contracts.Packs;
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
        bool canOverridePrice = false,
        PackContainerDto? packContainer = null,
        bool allowSplitCuts = false)
    {
        ArgumentNullException.ThrowIfNull(product);
        Product = product;
        ExactUnit = exactUnit;
        PackContainer = packContainer;
        AllowSplitCuts = allowSplitCuts;
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
    public PackContainerDto? PackContainer { get; }
    public bool AllowSplitCuts { get; }
    public bool IsPackContainer => PackContainer is not null;
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
    public bool HasExactUnit => ExactUnit is not null || PackContainer is not null;
    public Guid? InventoryUnitId => ExactUnit?.InventoryUnitId ?? PackContainer?.ContainerId;
    public string? TrackingCode => ExactUnit?.TrackingCode ?? PackContainer?.TrackingCode;
    public Guid? ProductUnitId => Product.BackendProductUnitId;
    public IReadOnlyList<Guid> InventoryUnitIds =>
        ExactUnit is not null ? new[] { ExactUnit.InventoryUnitId } :
        PackContainer is not null ? new[] { PackContainer.ContainerId } :
        Array.Empty<Guid>();
    public string ExactIdentityDisplay =>
        ExactUnit?.PrimaryIdentity ??
        PackContainer?.TrackingCode ??
        string.Empty;

    public decimal Quantity
    {
        get => _quantity;
        set
        {
            var maxStock = PackContainer is not null && !AllowSplitCuts
                ? PackContainer.RemainingQuantity
                : Product.Stock;
            var clamped = ExactUnit is not null
                ? 1m
                : (PackContainer is not null && !AllowSplitCuts
                    ? Math.Clamp(Math.Round(value, 2), 0m, maxStock)
                    : Math.Clamp(Math.Round(value, 2), 0m, Product.Stock));
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
    public string UnitPriceCalculationDisplay => ExactUnit is not null
        ? $"{ExactIdentityDisplay} · Rs. {UnitPrice:N2}"
        : PackContainer is not null
            ? $"{ExactIdentityDisplay} · Rs. {UnitPrice:N2} × {QuantityDisplay}"
            : $"Rs. {UnitPrice:N2} × {QuantityDisplay}";
    public decimal LineTotal => Math.Round(UnitPrice * _quantity, 2);
    public string LineTotalDisplay => $"Rs. {LineTotal:N2}";
    public bool CanIncrement => ExactUnit is null && (PackContainer is not null && !AllowSplitCuts ? Quantity < PackContainer.RemainingQuantity : Quantity < Product.Stock);
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

    public void ApplyPriceOverride(decimal price, string reason)
    {
        _priceOverrideUnitPrice = Math.Round(price, 2, MidpointRounding.AwayFromZero);
        _priceOverrideReason = reason.Trim();
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

    private void ApplyPriceOverride()
    {
        if (!CanApplyPriceOverride ||
            !decimal.TryParse(PriceOverrideUnitPriceText, NumberStyles.Number, CultureInfo.InvariantCulture, out var price))
        {
            return;
        }

        ApplyPriceOverride(price, PriceOverrideReasonInput);
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
