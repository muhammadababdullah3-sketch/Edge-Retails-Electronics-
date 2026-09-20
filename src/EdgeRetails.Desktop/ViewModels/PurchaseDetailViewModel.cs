using System.Windows.Input;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class PurchaseDetailViewModel : ViewModelBase
{
    private readonly IDrawerService _drawerService;
    private readonly IDialogService _dialogService;
    private readonly IToastService _toastService;

    public PurchaseDetailViewModel(
        PurchaseRecord purchase,
        IDrawerService drawerService,
        IDialogService dialogService,
        IToastService toastService)
    {
        Purchase = purchase;
        _drawerService = drawerService;
        _dialogService = dialogService;
        _toastService = toastService;

        CloseCommand = new RelayCommand(_drawerService.Close);
        ReturnPurchaseCommand = new RelayCommand(OpenReturn, () => CanReturnPurchase);
    }

    public PurchaseRecord Purchase { get; }
    public string Title => $"Purchase {Purchase.PurchaseNumber}";
    public string Supplier => Purchase.Supplier;
    public string PurchaseId => Purchase.PurchaseNumber;
    public string SupplierInvoice => Purchase.InvoiceNumber;
    public string DateDisplay => Purchase.DateDisplay;
    public string NoteDisplay => string.IsNullOrWhiteSpace(Purchase.Note) ? "—" : Purchase.Note;
    public string EnteredBy => "Abdullah";
    public IReadOnlyList<PurchaseItemRecord> Items => Purchase.Items;
    public string SubtotalDisplay => $"Rs. {Purchase.Subtotal:N0}";
    public string OtherChargesDisplay => $"Rs. {Purchase.OtherCharges:N0}";
    public string TotalDisplay => Purchase.TotalDisplay;
    public bool CanReturnPurchase => Purchase.Items.Any(item => item.EligibleReturnQuantity > 0m);

    public ICommand CloseCommand { get; }
    public ICommand ReturnPurchaseCommand { get; }

    private void OpenReturn()
    {
        if (!CanReturnPurchase)
        {
            _toastService.Show("No eligible purchase quantity remains to return.", ToastTone.Warning);
            return;
        }

        _dialogService.Show(new PurchaseReturnViewModel(
            Purchase,
            _toastService,
            _dialogService.Close,
            completed: OnReturnProcessed));
    }

    private void OnReturnProcessed()
    {
        OnPropertyChanged(nameof(CanReturnPurchase));
        OnPropertyChanged(nameof(Items));
        ((RelayCommand)ReturnPurchaseCommand).NotifyCanExecuteChanged();
    }
}
