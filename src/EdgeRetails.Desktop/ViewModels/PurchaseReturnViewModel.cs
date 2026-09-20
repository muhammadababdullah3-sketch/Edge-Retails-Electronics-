using System.Collections.ObjectModel;
using System.Windows.Input;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class PurchaseReturnLineViewModel(PurchaseItemRecord item, Action changed) : ViewModelBase
{
    private readonly Action _changed = changed;
    private decimal _returnQuantity;

    public PurchaseItemRecord Item { get; } = item;
    public string ProductName => Item.ProductName;
    public string ProductMeta => Item.ProductMeta;
    public decimal Purchased => Item.PurchasedQuantity;
    public decimal Used => Item.UsedQuantity;
    public decimal Returned => Item.ReturnedQuantity;
    public decimal Eligible => Item.EligibleReturnQuantity;

    public decimal ReturnQuantity
    {
        get => _returnQuantity;
        set
        {
            var normalized = Math.Clamp(Math.Round(value, 2), 0m, Eligible);
            if (SetProperty(ref _returnQuantity, normalized))
            {
                OnPropertyChanged(nameof(ReturnValue));
                OnPropertyChanged(nameof(ReturnValueDisplay));
                _changed();
            }
        }
    }

    public decimal ReturnValue => Math.Round(ReturnQuantity * Item.Cost, 2);
    public string ReturnValueDisplay => ReturnQuantity <= 0m ? "—" : $"Rs. {ReturnValue:N0}";
}

public sealed class PurchaseReturnViewModel : ViewModelBase
{
    private readonly DemoPurchaseInventoryService _service;
    private readonly IToastService _toastService;
    private readonly Action _close;
    private readonly Action? _completed;
    private string _reason = "Supplier Return";
    private string _note = string.Empty;

    public PurchaseReturnViewModel(
        PurchaseRecord purchase,
        IToastService toastService,
        Action close,
        Action? completed = null)
    {
        Purchase = purchase;
        _toastService = toastService;
        _close = close;
        _completed = completed;
        _service = DemoPurchaseInventoryService.Instance;
        Lines = new ObservableCollection<PurchaseReturnLineViewModel>(
            purchase.Items.Select(item => new PurchaseReturnLineViewModel(item, RefreshTotals)));
        ConfirmCommand = new RelayCommand(Confirm);
        CancelCommand = new RelayCommand(close);
    }

    public PurchaseRecord Purchase { get; }
    public ObservableCollection<PurchaseReturnLineViewModel> Lines { get; }
    public string HeaderDisplay => $"Purchase {Purchase.PurchaseNumber} — {Purchase.Supplier}";
    public string MetaDisplay => $"{Purchase.DateDisplay} · {Purchase.InvoiceNumber}";
    public string Reason { get => _reason; set => SetProperty(ref _reason, value ?? string.Empty); }
    public string Note { get => _note; set => SetProperty(ref _note, value ?? string.Empty); }
    public decimal ReturnTotal => Lines.Sum(line => line.ReturnValue);
    public string ReturnTotalDisplay => $"Rs. {ReturnTotal:N0}";
    public ICommand ConfirmCommand { get; }
    public ICommand CancelCommand { get; }

    private void Confirm()
    {
        try
        {
            var values = Lines.ToDictionary(line => line.Item.Product.Id, line => line.ReturnQuantity);
            var total = _service.ReturnPurchase(Purchase, values, Reason, Note);
            _toastService.Show($"Purchase return recorded: Rs. {total:N0}.", ToastTone.Success);
            _completed?.Invoke();
            _close();
        }
        catch (Exception ex)
        {
            _toastService.Show(ex.Message, ToastTone.Danger);
        }
    }

    private void RefreshTotals()
    {
        OnPropertyChanged(nameof(ReturnTotal));
        OnPropertyChanged(nameof(ReturnTotalDisplay));
    }
}
