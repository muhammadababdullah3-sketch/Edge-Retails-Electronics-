using System.Globalization;
using System.Windows.Input;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Domain.Inventory;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class StockAdjustmentViewModel : ViewModelBase
{
    private readonly DemoPurchaseInventoryService _service;
    private readonly IToastService _toastService;
    private readonly Action _close;
    private readonly IBackendStockAdjustmentService? _backendService;
    private readonly Action? _completed;
    private readonly Guid _clientOperationId = Guid.CreateVersion7();
    private string _quantityText = string.Empty;
    private string _reason = "Physical Count";
    private string _note = string.Empty;
    private bool _isIncrease = true;

    public StockAdjustmentViewModel(
        PosProductItemViewModel product,
        IToastService toastService,
        Action close,
        IBackendStockAdjustmentService? backendService = null,
        Action? completed = null)
    {
        Product = product;
        _toastService = toastService;
        _close = close;
        _backendService = backendService;
        _completed = completed;
        _service = DemoPurchaseInventoryService.Instance;
        ApplyCommand = new RelayCommand(async () => await ApplyAsync());
        CancelCommand = new RelayCommand(close);
    }

    public PosProductItemViewModel Product { get; }
    public string ProductDisplay => $"{Product.Name} · {Product.Sku}";
    public string CurrentStockDisplay => $"{Product.Stock:0.##} {Product.Unit}";
    public string QuantityText { get => _quantityText; set => SetProperty(ref _quantityText, value ?? string.Empty); }
    public string Reason { get => _reason; set => SetProperty(ref _reason, value ?? string.Empty); }
    public string Note { get => _note; set => SetProperty(ref _note, value ?? string.Empty); }
    public bool IsIncrease { get => _isIncrease; set => SetProperty(ref _isIncrease, value); }
    public ICommand ApplyCommand { get; }
    public ICommand CancelCommand { get; }

    public async Task ApplyAsync()
    {
        if (!decimal.TryParse(QuantityText, NumberStyles.Number, CultureInfo.InvariantCulture, out var qty) || qty <= 0m)
        {
            _toastService.Show("Enter a valid positive adjustment quantity.", ToastTone.Warning);
            return;
        }

        if (Product.IsSerialized)
        {
            _toastService.Show("Serialized stock adjustments require exact physical-unit selection.", ToastTone.Warning);
            return;
        }

        try
        {
            if (_backendService is not null)
            {
                var reason = Reason.Contains("damag", StringComparison.OrdinalIgnoreCase)
                    ? StockAdjustmentReason.Damaged
                    : Reason.Contains("lost", StringComparison.OrdinalIgnoreCase)
                        ? StockAdjustmentReason.Lost
                        : Reason.Contains("opening", StringComparison.OrdinalIgnoreCase)
                            ? StockAdjustmentReason.OpeningStock
                            : StockAdjustmentReason.PhysicalCountCorrection;
                await _backendService.CreateDeltaAdjustmentAsync(
                    Product.BackendProductId
                        ?? throw new BackendOperationException("catalog.product_not_attached", "Product is not attached to backend authority."),
                    Product.BackendProductUnitId,
                    qty,
                    IsIncrease,
                    reason,
                    Note,
                    _clientOperationId);
                _toastService.Show("Stock adjustment recorded by inventory authority.", ToastTone.Success);
                _completed?.Invoke();
            }
            else
            {
                _service.AdjustStock(Product, IsIncrease ? qty : -qty, Reason, Note);
                _toastService.Show($"Stock adjusted to {Product.Stock:0.##} {Product.Unit}.", ToastTone.Success);
            }
            _close();
        }
        catch (Exception ex)
        {
            _toastService.Show(
                DesktopErrorPresentation.ForException(
                    ex,
                    "Stock adjustment outcome could not be confirmed. Check operation status before retrying."),
                ToastTone.Danger);
        }
    }
}
