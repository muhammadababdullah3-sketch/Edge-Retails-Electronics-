using System.Windows.Input;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class PurchaseDetailViewModel : ViewModelBase
{
    private readonly IDrawerService _drawerService;
    private readonly IDialogService _dialogService;
    private readonly IToastService _toastService;
    private readonly IBackendPurchasingInventoryService? _backendService;
    private readonly IBackendWorkflowReadService? _workflowService;
    private PurchaseRecord _purchase = null!;
    private bool _isLoadingBackendDetail;

    public PurchaseDetailViewModel(
        PurchaseRecord purchase,
        IDrawerService drawerService,
        IDialogService dialogService,
        IToastService toastService,
        IBackendPurchasingInventoryService? backendService = null,
        IBackendWorkflowReadService? workflowService = null)
    {
        _purchase = purchase;
        _drawerService = drawerService;
        _dialogService = dialogService;
        _toastService = toastService;
        _backendService = backendService;
        _workflowService = workflowService;

        CloseCommand = new RelayCommand(_drawerService.Close);
        ReturnPurchaseCommand = new RelayCommand(OpenReturn, () => CanReturnPurchase);

        if (_backendService is not null && purchase.BackendPurchaseId is Guid)
        {
            _isLoadingBackendDetail = true;
            _ = LoadBackendDetailAsync();
        }
    }

    private async Task LoadBackendDetailAsync()
    {
        if (_backendService is null || Purchase.BackendPurchaseId is not Guid purchaseId)
        {
            return;
        }

        try
        {
            var detail = await _backendService.GetPurchaseAsync(purchaseId);
            if (detail is not null)
            {
                _purchase = detail;
                OnPropertyChanged(nameof(Purchase));
                OnPropertyChanged(nameof(NoteDisplay));
                OnPropertyChanged(nameof(Items));
                OnPropertyChanged(nameof(SubtotalDisplay));
                OnPropertyChanged(nameof(OtherChargesDisplay));
                OnPropertyChanged(nameof(TotalDisplay));
                OnPropertyChanged(nameof(CanReturnPurchase));
                ((RelayCommand)ReturnPurchaseCommand).NotifyCanExecuteChanged();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _toastService.Show($"Purchase details could not be loaded: {ex.Message}", ToastTone.Danger);
        }
        finally
        {
            _isLoadingBackendDetail = false;
            OnPropertyChanged(nameof(IsLoadingBackendDetail));
        }
    }

    public bool IsLoadingBackendDetail => _isLoadingBackendDetail;
    public PurchaseRecord Purchase => _purchase;
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
    public bool CanReturnPurchase =>
        !Purchase.IsVoided &&
        Purchase.Items.Any(item => item.EligibleReturnQuantity > 0m);

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
            completed: OnReturnProcessed,
            backendService: _backendService,
            workflowService: _workflowService,
            dialogService: _dialogService));
    }

    private void OnReturnProcessed()
    {
        OnPropertyChanged(nameof(CanReturnPurchase));
        OnPropertyChanged(nameof(Items));
        ((RelayCommand)ReturnPurchaseCommand).NotifyCanExecuteChanged();
    }
}
