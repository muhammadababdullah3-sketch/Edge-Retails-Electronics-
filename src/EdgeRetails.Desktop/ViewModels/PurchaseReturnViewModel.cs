using System.Collections.ObjectModel;
using System.Windows.Input;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class PurchaseReturnLineViewModel : ViewModelBase
{
    private readonly Action _changed;
    private decimal _returnQuantity;
    private IReadOnlyList<BackendExactUnit> _selectedUnits = [];

    public PurchaseReturnLineViewModel(PurchaseItemRecord item, Action changed)
    {
        Item = item;
        _changed = changed;
    }

    public PurchaseItemRecord Item { get; }
    public string ProductName => Item.ProductName;
    public string ProductMeta => Item.ProductMeta;
    public decimal Purchased => Item.PurchasedQuantity;
    public decimal Used => Item.UsedQuantity;
    public decimal Returned => Item.ReturnedQuantity;
    public decimal Eligible => Item.EligibleReturnQuantity;
    public bool IsSerialized => Item.Product.IsSerialized;
    public IReadOnlyList<BackendExactUnit> SelectedUnits => _selectedUnits;

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
                OnPropertyChanged(nameof(RequiredExactUnitCount));
                OnPropertyChanged(nameof(ExactUnitSelectionDisplay));
                OnPropertyChanged(nameof(HasValidSelection));
                _changed();
            }
        }
    }

    public int RequiredExactUnitCount
    {
        get
        {
            if (!IsSerialized || ReturnQuantity <= 0m)
            {
                return 0;
            }

            var baseQuantity = ReturnQuantity * Item.Product.FactorToBaseUnit;
            if (baseQuantity != decimal.Truncate(baseQuantity) ||
                baseQuantity > int.MaxValue)
            {
                return -1;
            }

            return decimal.ToInt32(baseQuantity);
        }
    }

    public string ExactUnitSelectionDisplay => !IsSerialized
        ? "Quantity"
        : RequiredExactUnitCount <= 0
            ? "Select return qty"
            : $"{_selectedUnits.Count}/{RequiredExactUnitCount} units";

    public bool HasValidSelection =>
        ReturnQuantity <= 0m ||
        !IsSerialized ||
        (RequiredExactUnitCount > 0 &&
         _selectedUnits.Count == RequiredExactUnitCount);

    public decimal ReturnValue => Math.Round(ReturnQuantity * Item.Cost, 2);
    public string ReturnValueDisplay => ReturnQuantity <= 0m ? "—" : $"Rs. {ReturnValue:N0}";

    public void SetExactUnits(IReadOnlyList<BackendExactUnit> units)
    {
        _selectedUnits = units.ToArray();
        OnPropertyChanged(nameof(SelectedUnits));
        OnPropertyChanged(nameof(ExactUnitSelectionDisplay));
        OnPropertyChanged(nameof(HasValidSelection));
        _changed();
    }
}

public sealed class PurchaseReturnViewModel : ViewModelBase
{
    private readonly DemoPurchaseInventoryService? _previewService;
    private readonly IBackendPurchasingInventoryService? _backendService;
    private readonly IBackendWorkflowReadService? _workflowService;
    private readonly IToastService _toastService;
    private readonly IDialogService? _dialogService;
    private readonly Action _close;
    private readonly Action? _completed;
    private readonly Guid _clientOperationId = Guid.CreateVersion7();
    private string _reason = "Supplier Return";
    private string _note = string.Empty;

    public PurchaseReturnViewModel(
        PurchaseRecord purchase,
        IToastService toastService,
        Action close,
        Action? completed = null,
        IBackendPurchasingInventoryService? backendService = null,
        IBackendWorkflowReadService? workflowService = null,
        IDialogService? dialogService = null)
    {
        Purchase = purchase;
        _toastService = toastService;
        _close = close;
        _completed = completed;
        _backendService = backendService;
#if DEBUG
        _previewService = backendService is null ? DemoPurchaseInventoryService.Instance : null;
#else
        _previewService = null;
#endif
        _workflowService = workflowService;
        _dialogService = dialogService;
        Lines = new ObservableCollection<PurchaseReturnLineViewModel>(
            purchase.Items.Select(item => new PurchaseReturnLineViewModel(item, RefreshTotals)));
        ConfirmCommand = new RelayCommand(async () => await ConfirmAsync());
        SelectExactUnitsCommand = new RelayCommand<PurchaseReturnLineViewModel>(
            SelectExactUnits,
            line => line?.IsSerialized == true && line.RequiredExactUnitCount > 0);
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
    public ICommand SelectExactUnitsCommand { get; }
    public ICommand CancelCommand { get; }

    private void SelectExactUnits(PurchaseReturnLineViewModel? line)
    {
        if (line is null || line.Item.BackendPurchaseItemId is not Guid purchaseItemId ||
            line.Item.Product.BackendProductId is not Guid productId)
        {
            _toastService.Show(
                "Purchase item is not attached to backend exact-unit authority.",
                ToastTone.Warning);
            return;
        }

        if (_workflowService is null || _dialogService is null)
        {
            _toastService.Show(
                "Exact-unit picker is unavailable.",
                ToastTone.Warning);
            return;
        }

        if (line.RequiredExactUnitCount <= 0)
        {
            _toastService.Show(
                "Enter a valid serialized return quantity before selecting units.",
                ToastTone.Warning);
            return;
        }

        _dialogService.ShowNested(new ExactUnitPickerViewModel(
            "Select Units to Return",
            $"{line.ProductName} · original purchase provenance required",
            productId,
            _workflowService,
            _dialogService,
            line.SetExactUnits,
            requiredCount: line.RequiredExactUnitCount,
            status: EdgeRetails.Domain.Inventory.InventoryUnitStatus.InStock,
            sourcePurchaseItemId: purchaseItemId));
    }

    private async Task ConfirmAsync()
    {
        try
        {
            var selectedLines = Lines.Where(x => x.ReturnQuantity > 0m).ToArray();
            if (selectedLines.Length == 0)
            {
                _toastService.Show("Select at least one item to return.", ToastTone.Warning);
                return;
            }

            if (selectedLines.Any(x => !x.HasValidSelection))
            {
                _toastService.Show(
                    "Every serialized return line requires the exact eligible physical units.",
                    ToastTone.Warning);
                return;
            }

            decimal total;
            if (_backendService is null)
            {
                var values = Lines.ToDictionary(
                    line => line.Item.Product.Id,
                    line => line.ReturnQuantity);
                if (_previewService is null)
                {
                    throw new InvalidOperationException(
                        "Production purchase return requires authoritative backend service.");
                }

                total = _previewService.ReturnPurchase(
                    Purchase,
                    values,
                    Reason,
                    Note);
            }
            else
            {
                var values = selectedLines
                    .Where(line => line.Item.BackendPurchaseItemId is not null)
                    .ToDictionary(
                        line => line.Item.BackendPurchaseItemId!.Value,
                        line => new BackendPurchaseReturnSelection(
                            line.ReturnQuantity,
                            line.SelectedUnits.Select(x => x.InventoryUnitId).ToArray()));

                total = await _backendService.ReturnPurchaseAsync(
                    Purchase,
                    values,
                    Reason,
                    Note,
                    _clientOperationId);

                foreach (var line in selectedLines)
                {
                    line.Item.ReturnedQuantity = Math.Round(
                        line.Item.ReturnedQuantity + line.ReturnQuantity,
                        2);
                    if (line.Item.BackendEligibleReturnQuantity is decimal eligible)
                    {
                        line.Item.BackendEligibleReturnQuantity = Math.Max(
                            0m,
                            Math.Round(eligible - line.ReturnQuantity, 2));
                    }
                }
            }

            _toastService.Show(
                $"Purchase return recorded: Rs. {total:N0}.",
                ToastTone.Success);
            _completed?.Invoke();
            _close();
        }
        catch (BackendOperationException ex)
        {
            _toastService.Show($"{ex.Code}: {ex.Message}", ToastTone.Danger);
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
