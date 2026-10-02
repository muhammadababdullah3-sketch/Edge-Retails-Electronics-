using System.Collections.ObjectModel;
using System.Windows.Input;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Production.Printing;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Domain.Catalog;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class PhysicalUnitRowViewModel : ViewModelBase
{
    private bool _isSelected = true;

    public Guid Id { get; init; }
    public string TrackingCode { get; init; } = string.Empty;
    public long ItemSequence { get; init; }
    public string? SerialNumber { get; init; }
    public string? Imei1 { get; init; }
    public string? Imei2 { get; init; }
    public decimal AcquisitionCost { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string SequenceDisplay => $"#{ItemSequence:D6}";
    public string SerialDisplay => !string.IsNullOrWhiteSpace(SerialNumber) ? SerialNumber : "—";
    public string ImeiDisplay => !string.IsNullOrWhiteSpace(Imei1) ? Imei1 : "—";
}

public sealed class PhysicalIntakeViewModel : ViewModelBase
{
    private PurchaseRecord _purchase;
    private PurchaseItemRecord _item;
    private readonly IBackendPurchasingInventoryService? _backendService;
    private readonly IDialogService _dialogService;
    private readonly IToastService _toastService;
    private readonly Func<Guid?> _actorUserId;
    private readonly Action? _onCompleted;
    private readonly IClientOperationIntentStore _operationIntents;
    private readonly IBackendLabelService? _labelService;
    private readonly Func<string?> _chooseExportFolder;

    private decimal _enteredQuantity;
    private string? _validationMessage;
    private string? _printStatusMessage;
    private bool _isProcessing;
    private bool _intakeCompleted;
    private bool _hasExistingUnits;
    private bool _authorityLoaded;

    public PhysicalIntakeViewModel(
        PurchaseRecord purchase,
        PurchaseItemRecord item,
        IDialogService dialogService,
        IToastService toastService,
        IBackendPurchasingInventoryService? backendService,
        Func<Guid?> actorUserId,
        Action? onCompleted = null,
        IClientOperationIntentStore? operationIntents = null,
        IBackendLabelService? labelService = null,
        Func<string?>? chooseExportFolder = null)
    {
        _purchase = purchase ?? throw new ArgumentNullException(nameof(purchase));
        _item = item ?? throw new ArgumentNullException(nameof(item));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _toastService = toastService ?? throw new ArgumentNullException(nameof(toastService));
        _backendService = backendService;
        _actorUserId = actorUserId;
        _onCompleted = onCompleted;
        _operationIntents = operationIntents ?? new FileClientOperationIntentStore();
        _labelService = labelService ?? backendService as IBackendLabelService;
        _chooseExportFolder = chooseExportFolder ?? LabelExportFolderPicker.Choose;

        _enteredQuantity = Math.Max(0m, item.PurchasedQuantity - (item.ReceivedQuantity ?? 0m));
        _authorityLoaded = !item.BackendPurchaseItemId.HasValue;

        SerialRows = [];
        CommittedUnits = [];

        UpdateSerialRows();

        ReceiveIntakeCommand = new RelayCommand(ExecuteIntakeAsync, () => !IsProcessing && CanExecuteIntake);
        PrintAllCommand = new RelayCommand(() => _ = PrintStickersAsync(false, all: true), () => !IsProcessing && CommittedUnits.Count > 0);
        PrintSelectedCommand = new RelayCommand(() => _ = PrintStickersAsync(false, all: false), () => !IsProcessing && CommittedUnits.Any(u => u.IsSelected));
        ReprintCommand = new RelayCommand(() => _ = PrintStickersAsync(true, all: false), () => !IsProcessing && CommittedUnits.Any(u => u.IsSelected));
        ExportAllPdfCommand = new RelayCommand(() => _ = ExportLabelsAsync(false, true), () => CanExportLabels(all: true));
        ExportSelectedPdfCommand = new RelayCommand(() => _ = ExportLabelsAsync(false, false), () => CanExportLabels(all: false));
        ReprintSelectedPdfCommand = new RelayCommand(() => _ = ExportLabelsAsync(true, false), () => CanExportLabels(all: false));
        PrintLaterCommand = new RelayCommand(CloseDialog);
        CloseCommand = new RelayCommand(CloseDialog);
        CommittedUnits.CollectionChanged += (_, change) =>
        {
            if (change.NewItems is not null)
            {
                foreach (PhysicalUnitRowViewModel row in change.NewItems)
                {
                    row.PropertyChanged += (_, args) =>
                    {
                        if (args.PropertyName == nameof(PhysicalUnitRowViewModel.IsSelected)) { NotifyLabelCommands(); }
                    };
                }
            }
            NotifyLabelCommands();
        };

        if (_item.BackendPurchaseItemId.HasValue && _backendService is not null)
        {
            _ = LoadExistingUnitsAsync(_item.BackendPurchaseItemId.Value);
        }
    }

    public string Title => $"Physical Intake · {_item.ProductName}";
    public string ProductInfo => $"{_item.Product.Category} · {_item.Product.Brand} · ProductCode: {_item.Product.Sku}";
    public string PurchaseInfo => $"Purchase: {_purchase.PurchaseNumber} · Invoice: {_purchase.InvoiceNumber} · Supplier: {_purchase.Supplier} · SupplierCode: {_purchase.SupplierCode ?? "Unavailable"}";
    public string TrackingPolicyInfo => $"Tracking policy: {_item.Product.TrackingMode} · Product is locked to this purchase line";
    public decimal ReceivedQuantity => _item.ReceivedQuantity ?? 0m;
    public decimal PendingQuantity => Math.Max(0m, _item.PurchasedQuantity - ReceivedQuantity);
    public string ReceiptProgressInfo => _authorityLoaded
        ? $"Ordered: {_item.PurchasedQuantity:0.####} · Received: {ReceivedQuantity:0.####} · Pending: {PendingQuantity:0.####} {UnitDisplay}"
        : "Receipt authority must be loaded before receiving.";
    public string UnitDisplay => _item.Product.Unit;

    public bool IsSerializedProduct => _item.Product.TrackingMode == TrackingMode.Serialized || _item.Product.SerialTrackingEnabled || _item.Product.ImeiTrackingEnabled;
    private bool IsPiece => _item.Product.TrackingMode is TrackingMode.Serialized or TrackingMode.IndividualPiece;
    private decimal IdentityCount => _item.Product.TrackingMode == TrackingMode.Container
        ? EnteredQuantity : EnteredQuantity * _item.Product.FactorToBaseUnit;
    public bool SerialRequired => _item.Product.SerialTrackingEnabled;
    public bool ImeiRequired => _item.Product.ImeiTrackingEnabled;

    public decimal EnteredQuantity
    {
        get => _enteredQuantity;
        set
        {
            if (SetProperty(ref _enteredQuantity, Math.Max(0m, value)))
            {
                UpdateSerialRows();
                OnPropertyChanged(nameof(CanExecuteIntake));
                ((RelayCommand)ReceiveIntakeCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsProcessing
    {
        get => _isProcessing;
        private set
        {
            if (SetProperty(ref _isProcessing, value))
            {
                ((RelayCommand)ReceiveIntakeCommand).NotifyCanExecuteChanged();
                ((RelayCommand)PrintAllCommand).NotifyCanExecuteChanged();
                ((RelayCommand)PrintSelectedCommand).NotifyCanExecuteChanged();
                ((RelayCommand)ReprintCommand).NotifyCanExecuteChanged();
                NotifyLabelCommands();
            }
        }
    }

    public bool IntakeCompleted
    {
        get => _intakeCompleted;
        private set
        {
            if (SetProperty(ref _intakeCompleted, value))
            {
                OnPropertyChanged(nameof(CanExecuteIntake));
                ((RelayCommand)ReceiveIntakeCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasExistingUnits
    {
        get => _hasExistingUnits;
        private set => SetProperty(ref _hasExistingUnits, value);
    }

    public string? ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (SetProperty(ref _validationMessage, value))
            {
                OnPropertyChanged(nameof(HasValidationMessage));
            }
        }
    }
    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(_validationMessage);

    public string? PrintStatusMessage
    {
        get => _printStatusMessage;
        private set
        {
            if (SetProperty(ref _printStatusMessage, value))
            {
                OnPropertyChanged(nameof(HasPrintStatusMessage));
            }
        }
    }
    public bool HasPrintStatusMessage => !string.IsNullOrWhiteSpace(_printStatusMessage);

    public bool HasCommittedUnits => CommittedUnits.Count > 0;

    public ObservableCollection<SerializedIdentityEntryViewModel> SerialRows { get; }
    public ObservableCollection<PhysicalUnitRowViewModel> CommittedUnits { get; }

    public bool CanExecuteIntake => _authorityLoaded && !IsProcessing && _enteredQuantity > 0m
        && _enteredQuantity <= PendingQuantity && !IntakeCompleted && !_purchase.IsVoided
        && (!(IsPiece || _item.Product.TrackingMode == TrackingMode.Container)
            || (IdentityCount > 0m && IdentityCount == decimal.Truncate(IdentityCount)))
        && (!IsSerializedProduct || IdentityCount <= 500m);

    public ICommand ReceiveIntakeCommand { get; }
    public ICommand PrintAllCommand { get; }
    public ICommand PrintSelectedCommand { get; }
    public ICommand ReprintCommand { get; }
    public ICommand ExportAllPdfCommand { get; }
    public ICommand ExportSelectedPdfCommand { get; }
    public ICommand ReprintSelectedPdfCommand { get; }
    public ICommand PrintLaterCommand { get; }
    public ICommand CloseCommand { get; }

    private bool CanExportLabels(bool all) => !IsProcessing && _labelService is not null && _authorityLoaded
        && (HasCommittedUnits ? all || CommittedUnits.Any(unit => unit.IsSelected)
            : ReceivedQuantity > 0m && _item.BackendProductUnitId is Guid id && id != Guid.Empty);

    private void NotifyLabelCommands()
    {
        ((RelayCommand)PrintSelectedCommand).NotifyCanExecuteChanged();
        ((RelayCommand)ReprintCommand).NotifyCanExecuteChanged();
        ((RelayCommand)ExportAllPdfCommand).NotifyCanExecuteChanged();
        ((RelayCommand)ExportSelectedPdfCommand).NotifyCanExecuteChanged();
        ((RelayCommand)ReprintSelectedPdfCommand).NotifyCanExecuteChanged();
    }

    private async Task ExportLabelsAsync(bool isReprint, bool all)
    {
        if (!CanExportLabels(all)) { return; }
        IsProcessing = true;
        try
        {
            var ids = (all ? CommittedUnits : CommittedUnits.Where(unit => unit.IsSelected))
                .Select(unit => unit.Id).ToArray();
            var productIds = HasCommittedUnits ? Array.Empty<Guid>() : new[] { _item.BackendProductUnitId!.Value };
            var folder = _chooseExportFolder();
            if (string.IsNullOrWhiteSpace(folder)) { return; }
            var result = await _labelService!.ExportLabelPdfAsync(ids, productIds, folder, isReprint);
            PrintStatusMessage = result.IsSuccess
                ? $"{ids.Length} exact-unit and {productIds.Length} product label(s) exported with PDFs and manifest in {folder}."
                : $"Label export failed: {result.Error?.Message}. Receiving and tracking identities remain committed.";
        }
        catch (Exception ex)
        {
            PrintStatusMessage = DesktopErrorPresentation.ForException(ex,
                "Label export could not be completed. Receiving and tracking identities remain committed; check the folder before retrying.");
        }
        finally { IsProcessing = false; }
    }

    private void UpdateSerialRows()
    {
        if (!IsSerializedProduct)
        {
            SerialRows.Clear();
            return;
        }

        var count = (int)Math.Min(IdentityCount, 500);
        while (SerialRows.Count < count)
        {
            SerialRows.Add(new SerializedIdentityEntryViewModel
            {
                Sequence = SerialRows.Count + 1
            });
        }
        while (SerialRows.Count > count)
        {
            SerialRows.RemoveAt(SerialRows.Count - 1);
        }
    }

    private async Task LoadExistingUnitsAsync(Guid purchaseItemId, bool afterCommit = false)
    {
        try
        {
            if (_backendService is null)
            {
                return;
            }

            _authorityLoaded = false;
            var currentPurchase = await _backendService.GetPurchaseAsync(_purchase.BackendPurchaseId!.Value);
            var currentItem = currentPurchase?.Items.SingleOrDefault(x => x.BackendPurchaseItemId == purchaseItemId);
            if (currentPurchase is null || currentItem is null || currentItem.ReceivedQuantity is null
                || currentPurchase.BackendSupplierId != _purchase.BackendSupplierId
                || currentItem.Product.BackendProductId != _item.Product.BackendProductId
                || currentItem.BackendProductUnitId != _item.BackendProductUnitId
                || string.IsNullOrWhiteSpace(currentPurchase.SupplierCode))
            {
                throw new InvalidOperationException("Receipt authority was not available.");
            }
            _purchase = currentPurchase;
            _item = currentItem;
            var existing = await _backendService.GetUnitsForPurchaseItemAsync(purchaseItemId);
            HasExistingUnits = existing.Count > 0;
            IntakeCompleted = PendingQuantity <= 0m || _purchase.IsVoided;
            _authorityLoaded = true;
            EnteredQuantity = PendingQuantity;
            UpdateSerialRows();
            CommittedUnits.Clear();
            foreach (var unit in existing.OrderBy(x => x.ItemSequence))
            {
                CommittedUnits.Add(new PhysicalUnitRowViewModel
                {
                    Id = unit.Id,
                    TrackingCode = unit.TrackingCode,
                    ItemSequence = unit.ItemSequence,
                    SerialNumber = unit.SerialNumber,
                    Imei1 = unit.Imei1,
                    Imei2 = unit.Imei2,
                    AcquisitionCost = unit.AcquisitionCost
                });
            }
        }
        catch
        {
            _authorityLoaded = false;
            ValidationMessage = afterCommit
                ? "Intake is safely committed, but refreshed receipt totals could not be confirmed. Labels remain available. Check the Server and reopen this purchase before receiving more."
                : "Purchase receiving status could not be loaded. Close and reopen this intake after checking the Server. No stock was changed.";
        }
        finally
        {
            OnPropertyChanged(nameof(ProductInfo));
            OnPropertyChanged(nameof(PurchaseInfo));
            OnPropertyChanged(nameof(TrackingPolicyInfo));
            OnPropertyChanged(nameof(UnitDisplay));
            OnPropertyChanged(nameof(IsSerializedProduct));
            OnPropertyChanged(nameof(ReceivedQuantity));
            OnPropertyChanged(nameof(PendingQuantity));
            OnPropertyChanged(nameof(ReceiptProgressInfo));
            OnPropertyChanged(nameof(HasCommittedUnits));
            OnPropertyChanged(nameof(CanExecuteIntake));
            ((RelayCommand)ReceiveIntakeCommand).NotifyCanExecuteChanged();
            ((RelayCommand)PrintAllCommand).NotifyCanExecuteChanged();
            ((RelayCommand)PrintSelectedCommand).NotifyCanExecuteChanged();
            ((RelayCommand)ReprintCommand).NotifyCanExecuteChanged();
        }
    }

    private async void ExecuteIntakeAsync()
    {
        if (!CanExecuteIntake)
        {
            ValidationMessage = "Confirm the purchase receiving status and enter a valid pending quantity before receiving.";
            return;
        }
        ValidationMessage = null;
        PrintStatusMessage = null;

        if (_purchase.BackendPurchaseId is not Guid purchaseId)
        {
            ValidationMessage = "Purchase does not have a backend ID.";
            return;
        }

        var productId = _item.Product.BackendProductId ?? Guid.Empty;
        if (productId == Guid.Empty)
        {
            ValidationMessage = "Selected product is missing backend ID.";
            return;
        }

        var productUnitId = _item.BackendProductUnitId
            ?? _item.Product.BackendProductUnitId
            ?? Guid.Empty;
        if (productUnitId == Guid.Empty)
        {
            ValidationMessage = "Selected product unit is missing backend ID.";
            return;
        }

        // Validate serials if serialized
        var serializedInputs = new List<SerializedIdentityInput>();
        if (IsSerializedProduct)
        {
            if (SerialRows.Count != (int)IdentityCount)
            {
                ValidationMessage = $"Serial row count ({SerialRows.Count}) does not match physical identity count ({IdentityCount}).";
                return;
            }

            if (SerialRequired && SerialRows.Any(x => string.IsNullOrWhiteSpace(x.SerialNumber)))
            {
                ValidationMessage = "Every unit requires a Serial Number.";
                return;
            }

            if (ImeiRequired && SerialRows.Any(x => string.IsNullOrWhiteSpace(x.Imei1)))
            {
                ValidationMessage = "Every unit requires IMEI 1.";
                return;
            }

            var serials = SerialRows
                .Where(x => !string.IsNullOrWhiteSpace(x.SerialNumber))
                .Select(x => x.SerialNumber.Trim())
                .ToArray();
            if (serials.Distinct(StringComparer.OrdinalIgnoreCase).Count() != serials.Length)
            {
                ValidationMessage = "Duplicate Serial Number detected in intake inputs.";
                return;
            }

            var imeis = SerialRows
                .SelectMany(x => new[] { x.Imei1, x.Imei2 })
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .ToArray();
            if (imeis.Distinct(StringComparer.OrdinalIgnoreCase).Count() != imeis.Length)
            {
                ValidationMessage = "Duplicate IMEI detected in intake inputs.";
                return;
            }

            serializedInputs.AddRange(SerialRows.Select(x => new SerializedIdentityInput(
                string.IsNullOrWhiteSpace(x.SerialNumber) ? null : x.SerialNumber.Trim(),
                string.IsNullOrWhiteSpace(x.Imei1) ? null : x.Imei1.Trim(),
                string.IsNullOrWhiteSpace(x.Imei2) ? null : x.Imei2.Trim())));
        }

        var actorId = _actorUserId()
            ?? throw new BackendOperationException(
                "identity.session_required",
                "A persistent backend user session is required for physical intake.");

        var command = new ReceiveProductIntakeCommand(
            purchaseId,
            productId,
            productUnitId,
            EnteredQuantity,
            _item.Cost,
            serializedInputs,
            actorId,
            Guid.Empty,
            $"Intake via desktop for {_purchase.InvoiceNumber}");

        var operationKey = PhysicalIntakeOperationIntent.Key(purchaseId, productUnitId);
        Guid clientOperationId;
        try
        {
            clientOperationId = _operationIntents.GetOrCreate(
                operationKey,
                PhysicalIntakeOperationIntent.Payload(command));
        }
        catch (InvalidOperationException)
        {
            ValidationMessage = "A previous intake with different details has an unresolved outcome. Reconcile it before changing the intake.";
            return;
        }
        command = command with { ClientOperationId = clientOperationId };

        IsProcessing = true;
        try
        {
            if (_backendService is null)
            {
                ValidationMessage = "Backend purchasing service is unavailable.";
                return;
            }

            var result = await _backendService.ReceiveProductIntakeAsync(command);
            if (!result.IsSuccess || result.Value is null)
            {
                if (!string.Equals(result.Error?.Code, "purchasing.intake_outcome_unknown", StringComparison.OrdinalIgnoreCase))
                {
                    _operationIntents.Complete(operationKey, clientOperationId);
                }
                ValidationMessage = $"Intake failed: {result.Error}";
                return;
            }

            _operationIntents.Complete(operationKey, clientOperationId);

            IntakeCompleted = true;
            _toastService.Show($"Physical intake completed for {_item.ProductName}!", ToastTone.Success);

            // Populate committed units
            foreach (var unit in result.Value.CommittedUnits)
            {
                if (CommittedUnits.Any(x => x.Id == unit.Id))
                {
                    continue;
                }
                CommittedUnits.Add(new PhysicalUnitRowViewModel
                {
                    Id = unit.Id,
                    TrackingCode = unit.TrackingCode,
                    ItemSequence = unit.ItemSequence,
                    SerialNumber = unit.SerialNumber,
                    Imei1 = unit.Imei1,
                    Imei2 = unit.Imei2,
                    AcquisitionCost = unit.AcquisitionCost
                });
            }

            OnPropertyChanged(nameof(HasCommittedUnits));
            if (_item.BackendPurchaseItemId is Guid itemId)
            {
                await LoadExistingUnitsAsync(itemId, afterCommit: true);
                // One committed product intake per session. Reopen the purchase line for its remainder.
                IntakeCompleted = true;
            }
            _onCompleted?.Invoke();
        }
        catch (Exception ex)
        {
            ValidationMessage = DesktopErrorPresentation.ForException(
                ex,
                "Physical intake could not be completed. Check operation status before retrying.");
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private async Task PrintStickersAsync(bool isReprint, bool all)
    {
        ValidationMessage = null;
        PrintStatusMessage = null;

        var unitsToPrint = all
            ? CommittedUnits.ToList()
            : CommittedUnits.Where(u => u.IsSelected).ToList();

        if (unitsToPrint.Count == 0)
        {
            ValidationMessage = "No units selected for printing.";
            return;
        }

        var actorId = _actorUserId() ?? Guid.NewGuid();
        var command = new PrintPhysicalStickersCommand(
            unitsToPrint.Select(u => u.Id).ToArray(),
            PrinterName: null,
            IsReprint: isReprint,
            RequestedBy: actorId,
            CorrelationId: Guid.NewGuid().ToString("D"));

        IsProcessing = true;
        try
        {
            if (_backendService is null)
            {
                ValidationMessage = "Backend printing service is unavailable.";
                return;
            }

            var result = await _backendService.PrintStickersAsync(command);
            if (!result.IsSuccess || result.Value is null)
            {
                PrintStatusMessage = $"Print error: {result.Error}. Inventory is safely recorded; labels can be printed or reprinted later.";
                return;
            }

            var outcome = result.Value;
            var unknownCount = outcome.JobResults.Count(job =>
                string.Equals(job.ErrorCode, "print.outcome_unknown", StringComparison.OrdinalIgnoreCase));
            if (unknownCount > 0)
            {
                PrintStatusMessage = $"The printer outcome is unknown for {unknownCount} label(s). Check the physical printer before choosing Reprint; a reprint may produce another copy. Receiving remains safely committed.";
                _toastService.Show("Print outcome unknown. Check the printer before reprinting.", ToastTone.Warning);
                return;
            }

            if (outcome.FailedCount > 0)
            {
                PrintStatusMessage = $"{outcome.FailedCount} label(s) could not be printed by the printer. Remaining units can be retried. Inventory was safely committed.";
                _toastService.Show($"Partial print: {outcome.SucceededCount} printed, {outcome.FailedCount} failed.", ToastTone.Warning);
            }
            else
            {
                _toastService.Show($"Submitted {outcome.SucceededCount} sticker(s) to Windows. Check the printer.", ToastTone.Success);
                PrintStatusMessage = $"Submitted {outcome.SucceededCount} sticker(s) to Windows. Check the physical printer.";
            }
            var unrecorded = outcome.JobResults.Count(job => !job.AuditPersisted);
            if (unrecorded > 0)
            {
                PrintStatusMessage += $" Audit receipt could not be recorded for {unrecorded} label(s). Check the printer before reprinting; no automatic resubmission occurred.";
                _toastService.Show("Some label audit receipts are unconfirmed. Check the printer before reprinting.", ToastTone.Warning);
            }
        }
        catch (Exception)
        {
            PrintStatusMessage = "The print result could not be confirmed. Check the physical printer before choosing Reprint; a reprint may produce another copy. Receiving remains safely committed.";
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private void CloseDialog()
    {
        _dialogService.Close();
    }
}
