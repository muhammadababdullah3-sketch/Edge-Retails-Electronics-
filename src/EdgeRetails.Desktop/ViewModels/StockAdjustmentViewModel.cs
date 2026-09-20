using System.Globalization;
using System.Windows.Input;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class StockAdjustmentViewModel : ViewModelBase
{
    private readonly DemoPurchaseInventoryService _service;
    private readonly IToastService _toastService;
    private readonly Action _close;
    private string _quantityText = string.Empty;
    private string _reason = "Physical Count";
    private string _note = string.Empty;
    private bool _isIncrease = true;

    public StockAdjustmentViewModel(
        PosProductItemViewModel product,
        IToastService toastService,
        Action close)
    {
        Product = product;
        _toastService = toastService;
        _close = close;
        _service = DemoPurchaseInventoryService.Instance;
        ApplyCommand = new RelayCommand(Apply);
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

    private void Apply()
    {
        if (!decimal.TryParse(QuantityText, NumberStyles.Number, CultureInfo.InvariantCulture, out var qty) || qty <= 0m)
        {
            _toastService.Show("Enter a valid positive adjustment quantity.", ToastTone.Warning);
            return;
        }

        try
        {
            var delta = IsIncrease ? qty : -qty;
            _service.AdjustStock(Product, delta, Reason, Note);
            _toastService.Show($"Stock adjusted to {Product.Stock:0.##} {Product.Unit}.", ToastTone.Success);
            _close();
        }
        catch (Exception ex)
        {
            _toastService.Show(ex.Message, ToastTone.Danger);
        }
    }
}
