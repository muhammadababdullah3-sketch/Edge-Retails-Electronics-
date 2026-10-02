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
    private bool _isVoidingPurchase;
    private const string PurchaseVoidReason = "Purchase void";

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
        OpenPhysicalIntakeCommand = new RelayCommand<PurchaseItemRecord>(OpenPhysicalIntake, _ => CanPerformPhysicalIntake);
        VoidPurchaseCommand = new RelayCommand(OpenVoidConfirmation, () => CanVoidPurchase);

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
                OnPropertyChanged(nameof(CanVoidPurchase));
                ((RelayCommand)VoidPurchaseCommand).NotifyCanExecuteChanged();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _toastService.Show(
                DesktopErrorPresentation.ForException(ex, "Purchase details could not be loaded."),
                ToastTone.Danger);
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

    public bool CanPerformPhysicalIntake => !Purchase.IsVoided && Items.Count > 0;

    public bool IsBackendPurchase =>
        _backendService is not null && _dialogService is not null && Purchase.BackendPurchaseId.HasValue;

    public bool CanVoidPurchase => IsBackendPurchase && !Purchase.IsVoided && !_isVoidingPurchase;

    private PurchaseItemRecord? _selectedItem;
    public PurchaseItemRecord? SelectedItem
    {
        get => _selectedItem;
        set => SetProperty(ref _selectedItem, value);
    }

    public ICommand CloseCommand { get; }
    public ICommand ReturnPurchaseCommand { get; }
    public ICommand OpenPhysicalIntakeCommand { get; }
    public ICommand VoidPurchaseCommand { get; }

    private void OpenVoidConfirmation()
    {
        if (!CanVoidPurchase || _dialogService is null)
        {
            return;
        }

        _dialogService.Show(new ConfirmationDialogViewModel(
            "Void purchase?",
            "This reverses the purchase payable and inventory effects. If the Server response is lost, confirm this same action again to reconcile its saved operation identity.",
            "Void Purchase",
            () => _ = VoidPurchaseAsync(),
            _dialogService.Close));
    }

    private async Task VoidPurchaseAsync()
    {
        if (!CanVoidPurchase || _backendService is null)
        {
            return;
        }

        _isVoidingPurchase = true;
        OnPropertyChanged(nameof(CanVoidPurchase));
        ((RelayCommand)VoidPurchaseCommand).NotifyCanExecuteChanged();
        try
        {
            await _backendService.VoidPurchaseAsync(Purchase, PurchaseVoidReason);
            _purchase = new PurchaseRecord
            {
                BackendPurchaseId = Purchase.BackendPurchaseId,
                BackendSupplierId = Purchase.BackendSupplierId,
                IsVoided = true,
                PurchaseNumber = Purchase.PurchaseNumber,
                Supplier = Purchase.Supplier,
                InvoiceNumber = Purchase.InvoiceNumber,
                Date = Purchase.Date,
                Note = Purchase.Note,
                OtherCharges = Purchase.OtherCharges,
                Items = Purchase.Items,
                BackendSubtotal = Purchase.BackendSubtotal,
                BackendTotal = Purchase.BackendTotal,
                BackendItemCount = Purchase.BackendItemCount
            };
            OnPropertyChanged(nameof(Purchase));
            OnPropertyChanged(nameof(CanReturnPurchase));
            OnPropertyChanged(nameof(CanPerformPhysicalIntake));
            OnPropertyChanged(nameof(CanVoidPurchase));
            ((RelayCommand)ReturnPurchaseCommand).NotifyCanExecuteChanged();
            ((RelayCommand)VoidPurchaseCommand).NotifyCanExecuteChanged();
            _toastService.Show("Purchase void was confirmed by the Server.", ToastTone.Success);
            await LoadBackendDetailAsync();
        }
        catch (Exception ex)
        {
            _toastService.Show(
                DesktopErrorPresentation.ForException(
                    ex,
                    "Purchase void status could not be confirmed. Retry this action to reconcile the saved operation identity."),
                ToastTone.Danger);
        }
        finally
        {
            _isVoidingPurchase = false;
            OnPropertyChanged(nameof(CanVoidPurchase));
            ((RelayCommand)VoidPurchaseCommand).NotifyCanExecuteChanged();
        }
    }

    private void OpenPhysicalIntake(PurchaseItemRecord? item = null)
    {
        var targetItem = item ?? SelectedItem ?? Items.FirstOrDefault();
        if (targetItem is null)
        {
            _toastService.Show("Select a product line to begin physical intake.", ToastTone.Warning);
            return;
        }

        if (Purchase.IsVoided)
        {
            _toastService.Show("Cannot perform physical intake on a voided purchase.", ToastTone.Warning);
            return;
        }

        _dialogService.Show(new PhysicalIntakeViewModel(
            Purchase,
            targetItem,
            _dialogService,
            _toastService,
            _backendService,
            () => Guid.Empty,
            onCompleted: () =>
            {
                _ = LoadBackendDetailAsync();
            }));
    }

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
