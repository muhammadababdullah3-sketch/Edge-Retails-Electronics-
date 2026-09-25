using System.Collections.ObjectModel;
using System.Windows.Input;
using EdgeRetails.Application.Features.Warranty;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Warranty;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class WarrantyViewModel : ViewModelBase
{
    private readonly IBackendOperationsService? _operationsService;
    private readonly IToastService? _toastService;
    private const int DashboardPageSize = 200;
    private readonly List<WarrantyQueueRowDto> _allRows = [];
    private CancellationTokenSource? _searchCts;
    private long _searchVersion;

    private WarrantyDashboardSummaryDto? _summary;
    private WarrantyQueueRowDto? _selectedRow;
    private string _searchText = string.Empty;
    private string _selectedMode = "All";
    private string _actionNote = string.Empty;
    private string _statusMessage = "Loading warranty authority...";
    private bool _isLoading;
    private string _shopProductIdText = string.Empty;
    private string _shopSupplierIdText = string.Empty;
    private string _shopQuantityText = "1";
    private string _shopSourcePurchaseItemIdText = string.Empty;
    private string _shopOriginalUnitIdsText = string.Empty;
    private string _shopReplacementUnitsText = string.Empty;
    private string _shopSupplierCreditText = string.Empty;
    private string _shopSupplierReferenceText = string.Empty;
    private string _claimIntakeSearchText = string.Empty;
    private string _claimIntakeCustomerIdText = string.Empty;
    private string _claimIntakeFaultText = string.Empty;
    private string _claimIntakeQuantityText = "1";
    private string _claimIntakeUnitIdsText = string.Empty;
    private string _customerReplacementUnitsText = string.Empty;
    private WarrantyClaimIntakeRowDto? _selectedIntakeRow;
    private Guid? _pendingClaimOperationId;
    private Guid? _pendingCustomerOperationId;
    private string? _pendingCustomerOperationKey;
    private Guid? _pendingCustomerReplacementOperationId;
    private string? _pendingCustomerReplacementOperationKey;
    private Guid? _pendingShopSendOperationId;
    private string? _pendingShopSendOperationKey;
    private Guid? _pendingShopReceiveOperationId;
    private string? _pendingShopReceiveOperationKey;

    public WarrantyViewModel(
        IBackendOperationsService? operationsService = null,
        IToastService? toastService = null)
    {
        _operationsService = operationsService;
        _toastService = toastService;

        Rows = [];
        Timeline = [];
        IntakeRows = [];
        RefreshCommand = new RelayCommand(async () => await RefreshAsync());
        SearchClaimIntakeCommand = new RelayCommand(async () => await SearchClaimIntakeAsync());
        CreateClaimCommand = new RelayCommand(async () => await CreateClaimAsync());
        BeginReviewCommand = new RelayCommand(async () => await ExecuteCustomerActionAsync(
            "REVIEW",
            (service, id, operationId) => service.BeginWarrantyClaimReviewAsync(id, operationId, ActionNote),
            "Claim moved to review."));
        SendToSupplierCommand = new RelayCommand(async () => await ExecuteCustomerActionAsync(
            "SEND_TO_SUPPLIER",
            (service, id, operationId) => service.SendWarrantyClaimToSupplierAsync(id, operationId, ActionNote),
            "Claim sent to Supplier."));
        SupplierProcessingCommand = new RelayCommand(async () => await ExecuteCustomerActionAsync(
            "SUPPLIER_PROCESSING",
            (service, id, operationId) => service.MarkWarrantySupplierProcessingAsync(id, operationId, ActionNote),
            "Supplier processing recorded."));
        ResolveRepairedCommand = new RelayCommand(async () => await ResolveAsync(WarrantyResolutionType.Repaired));
        ResolveRejectedCommand = new RelayCommand(async () => await ResolveAsync(WarrantyResolutionType.Rejected));
        ResolveRefundedCommand = new RelayCommand(async () => await ResolveAsync(WarrantyResolutionType.Refunded));
        CustomerReplacementCommand = new RelayCommand(async () => await ReceiveCustomerReplacementAsync());
        HandoverCommand = new RelayCommand(async () => await ExecuteCustomerActionAsync(
            "HANDOVER",
            (service, id, operationId) => service.HandoverWarrantyClaimAsync(id, operationId, ActionNote),
            "Warranty item handed over to Customer."));
        CancelClaimCommand = new RelayCommand(async () => await ExecuteCustomerActionAsync(
            "CANCELLATION",
            (service, id, operationId) => service.CancelWarrantyClaimAsync(id, operationId, ActionNote),
            "Warranty claim cancelled."));
        SendShopStockCommand = new RelayCommand(async () => await SendShopStockAsync());
        ReceiveShopRepairedCommand = new RelayCommand(async () => await ReceiveShopStockAsync(WarrantyResolutionType.Repaired));
        ReceiveShopRejectedCommand = new RelayCommand(async () => await ReceiveShopStockAsync(WarrantyResolutionType.Rejected));
        ReceiveShopScrappedCommand = new RelayCommand(async () => await ReceiveShopStockAsync(WarrantyResolutionType.Scrapped));
        ReceiveShopReplacedCommand = new RelayCommand(async () => await ReceiveShopStockAsync(WarrantyResolutionType.Replaced));
        ReceiveShopCreditedCommand = new RelayCommand(async () => await ReceiveShopStockAsync(WarrantyResolutionType.Credited));

        _ = RefreshAsync();
    }

    public ObservableCollection<WarrantyQueueRowDto> Rows { get; }
    public ObservableCollection<WarrantyEventDto> Timeline { get; }
    public ObservableCollection<WarrantyClaimIntakeRowDto> IntakeRows { get; }

    public IReadOnlyList<string> Modes { get; } =
    [
        "All",
        "Customer Warranty Claims",
        "Shop Stock Warranty Cases",
        "With Supplier",
        "Ready",
        "Closed"
    ];

    public WarrantyDashboardSummaryDto? Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public WarrantyQueueRowDto? SelectedRow
    {
        get => _selectedRow;
        set
        {
            var changedIntentTarget = _selectedRow?.Kind != value?.Kind ||
                                      _selectedRow?.WorkId != value?.WorkId;
            if (SetProperty(ref _selectedRow, value))
            {
                if (changedIntentTarget)
                {
                    ClearPendingCustomerOperation();
                    ClearPendingCustomerReplacementOperation();
                    ClearPendingShopReceiveOperation();
                }
                _ = LoadTimelineAsync();
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                _ = ScheduleSearchAsync(_searchText);
            }
        }
    }

    public string SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (SetProperty(ref _selectedMode, value ?? "All"))
            {
                ApplyFilter();
            }
        }
    }

    public string ActionNote
    {
        get => _actionNote;
        set => SetProperty(ref _actionNote, value ?? string.Empty);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    public bool IsEmpty => !IsLoading && Rows.Count == 0;

    public string ShopProductIdText
    {
        get => _shopProductIdText;
        set => SetProperty(ref _shopProductIdText, value ?? string.Empty);
    }

    public string ShopSupplierIdText
    {
        get => _shopSupplierIdText;
        set => SetProperty(ref _shopSupplierIdText, value ?? string.Empty);
    }

    public string ShopQuantityText
    {
        get => _shopQuantityText;
        set => SetProperty(ref _shopQuantityText, value ?? string.Empty);
    }

    public string ShopSourcePurchaseItemIdText
    {
        get => _shopSourcePurchaseItemIdText;
        set => SetProperty(ref _shopSourcePurchaseItemIdText, value ?? string.Empty);
    }

    public string ShopOriginalUnitIdsText
    {
        get => _shopOriginalUnitIdsText;
        set => SetProperty(ref _shopOriginalUnitIdsText, value ?? string.Empty);
    }

    public string ShopReplacementUnitsText
    {
        get => _shopReplacementUnitsText;
        set => SetProperty(ref _shopReplacementUnitsText, value ?? string.Empty);
    }

    public string ShopSupplierCreditText
    {
        get => _shopSupplierCreditText;
        set => SetProperty(ref _shopSupplierCreditText, value ?? string.Empty);
    }

    public string ShopSupplierReferenceText
    {
        get => _shopSupplierReferenceText;
        set => SetProperty(ref _shopSupplierReferenceText, value ?? string.Empty);
    }

    public string ClaimIntakeSearchText
    {
        get => _claimIntakeSearchText;
        set => SetProperty(ref _claimIntakeSearchText, value ?? string.Empty);
    }

    public string ClaimIntakeCustomerIdText
    {
        get => _claimIntakeCustomerIdText;
        set => SetProperty(ref _claimIntakeCustomerIdText, value ?? string.Empty);
    }

    public string ClaimIntakeFaultText
    {
        get => _claimIntakeFaultText;
        set => SetProperty(ref _claimIntakeFaultText, value ?? string.Empty);
    }

    public string ClaimIntakeQuantityText
    {
        get => _claimIntakeQuantityText;
        set => SetProperty(ref _claimIntakeQuantityText, value ?? string.Empty);
    }

    public string ClaimIntakeUnitIdsText
    {
        get => _claimIntakeUnitIdsText;
        set => SetProperty(ref _claimIntakeUnitIdsText, value ?? string.Empty);
    }

    public string CustomerReplacementUnitsText
    {
        get => _customerReplacementUnitsText;
        set => SetProperty(ref _customerReplacementUnitsText, value ?? string.Empty);
    }

    public WarrantyClaimIntakeRowDto? SelectedIntakeRow
    {
        get => _selectedIntakeRow;
        set
        {
            if (SetProperty(ref _selectedIntakeRow, value))
            {
                if (value?.CustomerId is Guid customerId)
                {
                    ClaimIntakeCustomerIdText = customerId.ToString();
                }

                if (value is { Units.Count: > 0 })
                {
                    var eligible = value.Units.Where(x => x.Eligible).Select(x => x.InventoryUnitId).ToArray();
                    ClaimIntakeUnitIdsText = eligible.Length == 1 ? eligible[0].ToString() : string.Empty;
                }
            }
        }
    }

    public ICommand RefreshCommand { get; }
    public ICommand SearchClaimIntakeCommand { get; }
    public ICommand CreateClaimCommand { get; }
    public ICommand BeginReviewCommand { get; }
    public ICommand SendToSupplierCommand { get; }
    public ICommand SupplierProcessingCommand { get; }
    public ICommand ResolveRepairedCommand { get; }
    public ICommand ResolveRejectedCommand { get; }
    public ICommand ResolveRefundedCommand { get; }
    public ICommand CustomerReplacementCommand { get; }
    public ICommand HandoverCommand { get; }
    public ICommand CancelClaimCommand { get; }
    public ICommand SendShopStockCommand { get; }
    public ICommand ReceiveShopRepairedCommand { get; }
    public ICommand ReceiveShopRejectedCommand { get; }
    public ICommand ReceiveShopScrappedCommand { get; }
    public ICommand ReceiveShopReplacedCommand { get; }
    public ICommand ReceiveShopCreditedCommand { get; }

    private async Task SearchClaimIntakeAsync()
    {
        if (_operationsService is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(ClaimIntakeSearchText) || ClaimIntakeSearchText.Trim().Length < 2)
        {
            _toastService?.Show("Enter at least 2 characters: Invoice, customer, product, TrackingCode, Serial or IMEI.", ToastTone.Warning);
            return;
        }

        IsLoading = true;
        try
        {
            var results = await _operationsService.SearchWarrantyClaimIntakeAsync(ClaimIntakeSearchText);
            IntakeRows.Clear();
            foreach (var row in results)
            {
                IntakeRows.Add(row);
            }

            SelectedIntakeRow = IntakeRows.FirstOrDefault();
            StatusMessage = results.Count == 0
                ? "No eligible sale/item candidates found."
                : $"{results.Count} authoritative sale/item candidate(s) found.";
        }
        catch (OperationException ex)
        {
            _toastService?.Show($"{ex.Code}: {ex.Message}", ToastTone.Danger);
        }
        catch (Exception ex)
        {
            _toastService?.Show($"Warranty intake search failed: {ex.Message}", ToastTone.Danger);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task CreateClaimAsync()
    {
        if (_operationsService is null || SelectedIntakeRow is null)
        {
            _toastService?.Show("Search and select an eligible sold item first.", ToastTone.Warning);
            return;
        }

        if (!SelectedIntakeRow.Eligible)
        {
            _toastService?.Show(
                $"Selected item is not eligible: {SelectedIntakeRow.EligibilityCode ?? "backend eligibility failed"}.",
                ToastTone.Warning);
            return;
        }

        if (!Guid.TryParse(ClaimIntakeCustomerIdText.Trim(), out var customerId))
        {
            _toastService?.Show("Customer ID is required and must be a valid ID.", ToastTone.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(ClaimIntakeFaultText))
        {
            _toastService?.Show("Fault/reason is required.", ToastTone.Warning);
            return;
        }

        var quantity = SelectedIntakeRow.Units.Count > 0
            ? ParseQuantityForSerialized(ClaimIntakeUnitIdsText)
            : ParseQuantityForQuantityItem();
        if (quantity <= 0)
        {
            return;
        }

        IReadOnlyList<WarrantyClaimUnitInput>? units = null;
        if (SelectedIntakeRow.Units.Count > 0)
        {
            var ids = ParseGuidList(ClaimIntakeUnitIdsText);
            if (ids is null || ids.Count != decimal.ToInt32(quantity))
            {
                _toastService?.Show("Select one exact InventoryUnitId for every serialized warranty unit.", ToastTone.Warning);
                return;
            }

            var byId = SelectedIntakeRow.Units.ToDictionary(x => x.InventoryUnitId);
            units = ids.Select(id =>
            {
                byId.TryGetValue(id, out var unit);
                var snapshot = unit is null
                    ? null
                    : $"TrackingCode={unit.TrackingCode}; Serial={unit.SerialNumber}; IMEI1={unit.Imei1}; IMEI2={unit.Imei2}";
                return new WarrantyClaimUnitInput(id, snapshot);
            }).ToArray();
        }

        _pendingClaimOperationId ??= Guid.CreateVersion7();
        IsLoading = true;
        try
        {
            var claimId = await _operationsService.CreateWarrantyClaimAsync(
                customerId,
                SelectedIntakeRow.SaleId,
                null,
                SelectedIntakeRow.ProductId,
                quantity,
                SelectedIntakeRow.SaleItemId,
                ClaimIntakeFaultText,
                units,
                _pendingClaimOperationId.Value);

            _toastService?.Show($"Warranty claim {claimId:D} created.", ToastTone.Success);
            _pendingClaimOperationId = null;
            ClaimIntakeFaultText = string.Empty;
            ClaimIntakeUnitIdsText = string.Empty;
            await RefreshAsync();
        }
        catch (OperationException ex)
        {
            _toastService?.Show(
                $"{ex.Code}: {ex.Message} The same ClientOperationId is retained for a safe retry.",
                ToastTone.Danger);
        }
        catch (Exception ex)
        {
            _toastService?.Show(
                $"Warranty claim outcome could not be confirmed: {ex.Message}. The same ClientOperationId is retained for retry.",
                ToastTone.Danger);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private decimal ParseQuantityForSerialized(string text)
    {
        var ids = ParseGuidList(text);
        if (ids is null || ids.Count == 0)
        {
            _toastService?.Show("Enter one or more exact InventoryUnit IDs.", ToastTone.Warning);
            return 0;
        }

        return ids.Count;
    }

    private decimal ParseQuantityForQuantityItem()
    {
        if (!decimal.TryParse(ClaimIntakeQuantityText.Trim(), out var quantity) || quantity <= 0)
        {
            _toastService?.Show("Enter a positive warranty quantity.", ToastTone.Warning);
            return 0;
        }

        if (quantity > SelectedIntakeRow?.BaseQuantity)
        {
            _toastService?.Show("Requested quantity exceeds the sold quantity.", ToastTone.Warning);
            return 0;
        }

        return quantity;
    }

    private async Task RefreshAsync()
    {
        if (_operationsService is null)
        {
            Summary = null;
            _allRows.Clear();
            Rows.Clear();
            StatusMessage = "Warranty backend authority is unavailable in preview mode.";
            return;
        }

        IsLoading = true;
        StatusMessage = "Loading authoritative warranty queues...";
        try
        {
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = null;
            Interlocked.Increment(ref _searchVersion);

            var dashboard = await _operationsService.GetWarrantyDashboardAsync(
                null,
                DashboardPageSize,
                cancellationToken: CancellationToken.None);
            Summary = dashboard.Summary;
            _allRows.Clear();
            _allRows.AddRange(dashboard.Rows);
            ApplyFilter();
            StatusMessage = _allRows.Count == 0
                ? "No warranty cases are currently recorded."
                : $"{_allRows.Count} warranty work item(s) loaded from backend authority.";
        }
        catch (OperationException ex)
        {
            Summary = null;
            _allRows.Clear();
            Rows.Clear();
            StatusMessage = $"{ex.Code}: {ex.Message}";
        }
        catch (Exception ex)
        {
            Summary = null;
            _allRows.Clear();
            Rows.Clear();
            StatusMessage = $"Warranty authority unavailable: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task ScheduleSearchAsync(string search)
    {
        var version = Interlocked.Increment(ref _searchVersion);
        var previous = Interlocked.Exchange(ref _searchCts, new CancellationTokenSource());
        previous?.Cancel();
        previous?.Dispose();
        var cts = _searchCts!;

        try
        {
            await Task.Delay(250, cts.Token);
            if (_operationsService is null || version != Volatile.Read(ref _searchVersion))
            {
                return;
            }

            var dashboard = await _operationsService.GetWarrantyDashboardAsync(
                string.IsNullOrWhiteSpace(search) ? null : search,
                DashboardPageSize,
                cancellationToken: cts.Token);

            if (version != Volatile.Read(ref _searchVersion) || cts.IsCancellationRequested)
            {
                return;
            }

            Summary = dashboard.Summary;
            _allRows.Clear();
            _allRows.AddRange(dashboard.Rows);
            ApplyFilter();
            StatusMessage = dashboard.Rows.Count == 0
                ? (string.IsNullOrWhiteSpace(search) ? "No warranty cases are currently recorded." : "No warranty cases match the search.")
                : $"{dashboard.Rows.Count} warranty work item(s) loaded from backend authority.";
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (OperationException ex) when (version == Volatile.Read(ref _searchVersion))
        {
            StatusMessage = $"{ex.Code}: {ex.Message}";
        }
        catch (Exception ex) when (version == Volatile.Read(ref _searchVersion))
        {
            StatusMessage = $"Warranty search failed: {ex.Message}";
        }
    }

    private void ApplyFilter()
    {
        IEnumerable<WarrantyQueueRowDto> query = _allRows;

        query = SelectedMode switch
        {
            "Customer Warranty Claims" => query.Where(x => x.Kind == WarrantyWorkKind.CustomerClaim),
            "Shop Stock Warranty Cases" => query.Where(x => x.Kind == WarrantyWorkKind.ShopStock),
            "With Supplier" => query.Where(x => x.Custody.Equals("WithSupplier", StringComparison.OrdinalIgnoreCase)),
            "Ready" => query.Where(x => x.Status.Contains("Ready", StringComparison.OrdinalIgnoreCase)),
            "Closed" => query.Where(x =>
                x.Status.Equals("Closed", StringComparison.OrdinalIgnoreCase) ||
                x.Status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase) ||
                x.Status.Equals("WrittenOff", StringComparison.OrdinalIgnoreCase)),
            _ => query
        };

        Rows.Clear();
        foreach (var row in query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Number))
        {
            Rows.Add(row);
        }

        if (SelectedRow is not null && !Rows.Any(x => x.WorkId == SelectedRow.WorkId && x.Kind == SelectedRow.Kind))
        {
            SelectedRow = null;
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    private async Task LoadTimelineAsync()
    {
        Timeline.Clear();
        if (_operationsService is null ||
            SelectedRow is not { Kind: WarrantyWorkKind.CustomerClaim } selected)
        {
            return;
        }

        try
        {
            var events = await _operationsService.GetWarrantyClaimTimelineAsync(selected.WorkId);
            foreach (var entry in events)
            {
                Timeline.Add(entry);
            }
        }
        catch (Exception ex)
        {
            _toastService?.Show($"Warranty history could not be loaded: {ex.Message}", ToastTone.Danger);
        }
    }

    private async Task ResolveAsync(WarrantyResolutionType resolution)
    {
        await ExecuteCustomerActionAsync(
            $"RESOLUTION:{resolution}",
            (service, id, operationId) => service.ResolveWarrantyClaimAsync(id, resolution, operationId, ActionNote),
            $"Warranty resolution recorded: {resolution}.");
    }

    private async Task ReceiveCustomerReplacementAsync()
    {
        if (_operationsService is null || SelectedRow is null)
        {
            _toastService?.Show("Select a Customer Warranty claim first.", ToastTone.Warning);
            return;
        }

        if (SelectedRow.Kind != WarrantyWorkKind.CustomerClaim)
        {
            _toastService?.Show("Customer replacement applies only to Customer Warranty claims.", ToastTone.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(CustomerReplacementUnitsText))
        {
            _toastService?.Show("Enter serialized replacement rows as ClaimItemUnitId|Serial|IMEI1|IMEI2; separate units with semicolons.", ToastTone.Warning);
            return;
        }

        var inputs = new List<CustomerWarrantyReplacementUnitInput>();
        foreach (var row in CustomerReplacementUnitsText.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = row.Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length != 4 || !Guid.TryParse(parts[0], out var claimItemUnitId))
            {
                _toastService?.Show("Customer replacement rows must use ClaimItemUnitId|Serial|IMEI1|IMEI2.", ToastTone.Warning);
                return;
            }

            inputs.Add(new CustomerWarrantyReplacementUnitInput(
                claimItemUnitId,
                NullIfBlank(parts[1]),
                NullIfBlank(parts[2]),
                NullIfBlank(parts[3])));
        }

        var operationKey = string.Join("|",
            SelectedRow.WorkId.ToString("D"),
            string.Join(";", inputs
                .OrderBy(x => x.ClaimItemUnitId)
                .Select(x => $"{x.ClaimItemUnitId:D}|{x.SerialNumber?.Trim()}|{x.Imei1?.Trim()}|{x.Imei2?.Trim()}")),
            ActionNote.Trim());
        _pendingCustomerReplacementOperationId ??= Guid.CreateVersion7();
        if (!string.Equals(_pendingCustomerReplacementOperationKey, operationKey, StringComparison.Ordinal))
        {
            _pendingCustomerReplacementOperationKey = operationKey;
            _pendingCustomerReplacementOperationId = Guid.CreateVersion7();
        }

        IsLoading = true;
        try
        {
            await _operationsService.ReceiveCustomerWarrantyReplacementAsync(
                SelectedRow.WorkId,
                inputs,
                _pendingCustomerReplacementOperationId.Value,
                ActionNote);
            _toastService?.Show("Customer Warranty replacement recorded.", ToastTone.Success);
            ActionNote = string.Empty;
            CustomerReplacementUnitsText = string.Empty;
            ClearPendingCustomerReplacementOperation();
            await RefreshAsync();
        }
        catch (OperationException ex)
        {
            _toastService?.Show($"{ex.Code}: {ex.Message}. The same ClientOperationId is retained for safe retry.", ToastTone.Danger);
        }
        catch (Exception ex)
        {
            _toastService?.Show($"Customer replacement outcome could not be confirmed: {ex.Message}. The same ClientOperationId is retained for retry.", ToastTone.Danger);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task SendShopStockAsync()
    {
        if (_operationsService is null)
        {
            return;
        }

        if (!Guid.TryParse(ShopProductIdText.Trim(), out var productId) ||
            !Guid.TryParse(ShopSupplierIdText.Trim(), out var supplierId) ||
            !decimal.TryParse(ShopQuantityText.Trim(), out var quantity) ||
            quantity <= 0)
        {
            _toastService?.Show("Shop Stock send requires valid Product ID, Supplier ID, and positive quantity.", ToastTone.Warning);
            return;
        }

        var sourceBucket = InventoryBucket.Defective;
        if (SelectedRow?.Kind == WarrantyWorkKind.ShopStock)
        {
            _toastService?.Show("Select the source stock in Inventory for a new Shop Stock warranty case; an existing warranty case cannot be sent again.", ToastTone.Warning);
            return;
        }

        Guid? purchaseItemId = null;
        if (!string.IsNullOrWhiteSpace(ShopSourcePurchaseItemIdText))
        {
            if (!Guid.TryParse(ShopSourcePurchaseItemIdText.Trim(), out var parsedPurchaseItemId))
            {
                _toastService?.Show("Source PurchaseItem ID is invalid.", ToastTone.Warning);
                return;
            }
            purchaseItemId = parsedPurchaseItemId;
        }

        IReadOnlyCollection<Guid>? unitIds = null;
        if (!string.IsNullOrWhiteSpace(ShopOriginalUnitIdsText))
        {
            var parsed = ParseGuidList(ShopOriginalUnitIdsText);
            if (parsed is null)
            {
                _toastService?.Show("One or more InventoryUnit IDs are invalid.", ToastTone.Warning);
                return;
            }
            unitIds = parsed;
        }

        var operationKey = BuildShopSendOperationKey(
            productId,
            sourceBucket,
            quantity,
            supplierId,
            purchaseItemId,
            unitIds,
            ActionNote);
        _pendingShopSendOperationId ??= Guid.CreateVersion7();
        if (!string.Equals(_pendingShopSendOperationKey, operationKey, StringComparison.Ordinal))
        {
            _pendingShopSendOperationKey = operationKey;
            _pendingShopSendOperationId = Guid.CreateVersion7();
        }

        IsLoading = true;
        try
        {
            var caseId = await _operationsService.SendShopStockWarrantyAsync(
                productId,
                sourceBucket,
                quantity,
                supplierId,
                purchaseItemId,
                ActionNote,
                _pendingShopSendOperationId.Value,
                unitIds);
            _toastService?.Show($"Shop Stock warranty case {caseId:D} sent to Supplier.", ToastTone.Success);
            ActionNote = string.Empty;
            ClearPendingShopSendOperation();
            await RefreshAsync();
        }
        catch (OperationException ex)
        {
            _toastService?.Show($"{ex.Code}: {ex.Message}", ToastTone.Danger);
        }
        catch (Exception ex)
        {
            _toastService?.Show($"Shop Stock warranty send failed: {ex.Message}", ToastTone.Danger);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task ReceiveShopStockAsync(WarrantyResolutionType resolution)
    {
        if (_operationsService is null || SelectedRow is null)
        {
            _toastService?.Show("Select a Shop Stock warranty case first.", ToastTone.Warning);
            return;
        }

        if (SelectedRow.Kind != WarrantyWorkKind.ShopStock)
        {
            _toastService?.Show("This action applies only to Shop Stock warranty cases.", ToastTone.Warning);
            return;
        }

        var originalUnitIds = ParseGuidListOrNull(ShopOriginalUnitIdsText);
        if (originalUnitIds is null && !string.IsNullOrWhiteSpace(ShopOriginalUnitIdsText))
        {
            _toastService?.Show("Original InventoryUnit IDs are invalid.", ToastTone.Warning);
            return;
        }

        IReadOnlyList<ReplacementSerializedUnitInput>? replacementUnits = null;
        if (!string.IsNullOrWhiteSpace(ShopReplacementUnitsText))
        {
            var parsed = new List<ReplacementSerializedUnitInput>();
            foreach (var row in ShopReplacementUnitsText.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = row.Split('|', StringSplitOptions.TrimEntries);
                if (parts.Length != 3)
                {
                    _toastService?.Show("Replacement units use Serial|IMEI1|IMEI2; separate units with semicolons.", ToastTone.Warning);
                    return;
                }
                parsed.Add(new ReplacementSerializedUnitInput(
                    NullIfBlank(parts[0]),
                    NullIfBlank(parts[1]),
                    NullIfBlank(parts[2])));
            }
            replacementUnits = parsed;
        }

        decimal? credit = null;
        if (!string.IsNullOrWhiteSpace(ShopSupplierCreditText))
        {
            if (!decimal.TryParse(ShopSupplierCreditText.Trim(), out var parsedCredit) || parsedCredit <= 0)
            {
                _toastService?.Show("Supplier credit must be a positive amount.", ToastTone.Warning);
                return;
            }
            credit = parsedCredit;
        }

        var operationKey = BuildShopReceiveOperationKey(
            SelectedRow.WorkId,
            resolution,
            originalUnitIds,
            replacementUnits,
            ActionNote,
            credit,
            ShopSupplierReferenceText);
        _pendingShopReceiveOperationId ??= Guid.CreateVersion7();
        if (!string.Equals(_pendingShopReceiveOperationKey, operationKey, StringComparison.Ordinal))
        {
            _pendingShopReceiveOperationKey = operationKey;
            _pendingShopReceiveOperationId = Guid.CreateVersion7();
        }

        IsLoading = true;
        try
        {
            await _operationsService.ReceiveShopStockWarrantyAsync(
                SelectedRow.WorkId,
                resolution,
                originalUnitIds,
                replacementUnits,
                ActionNote,
                _pendingShopReceiveOperationId.Value,
                credit,
                ShopSupplierReferenceText);
            _toastService?.Show($"Shop Stock warranty resolution recorded: {resolution}.", ToastTone.Success);
            ActionNote = string.Empty;
            ClearPendingShopReceiveOperation();
            await RefreshAsync();
        }
        catch (OperationException ex)
        {
            _toastService?.Show($"{ex.Code}: {ex.Message}. The same ClientOperationId is retained for safe retry.", ToastTone.Danger);
        }
        catch (Exception ex)
        {
            _toastService?.Show($"Shop Stock warranty receipt outcome could not be confirmed: {ex.Message}. The same ClientOperationId is retained for retry.", ToastTone.Danger);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static IReadOnlyCollection<Guid>? ParseGuidListOrNull(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<Guid>();
        }

        return ParseGuidList(text);
    }

    private static IReadOnlyCollection<Guid>? ParseGuidList(string text)
    {
        var values = text.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<Guid>(values.Length);
        foreach (var value in values)
        {
            if (!Guid.TryParse(value, out var id))
            {
                return null;
            }
            result.Add(id);
        }

        return result.Distinct().ToArray();
    }

    private static string? NullIfBlank(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private async Task ExecuteCustomerActionAsync(
        string operationName,
        Func<IBackendOperationsService, Guid, Guid, Task> action,
        string successMessage)
    {
        if (_operationsService is null)
        {
            return;
        }

        if (SelectedRow is null)
        {
            _toastService?.Show("Select a warranty claim first.", ToastTone.Warning);
            return;
        }

        if (SelectedRow.Kind != WarrantyWorkKind.CustomerClaim)
        {
            _toastService?.Show(
                "This action belongs to Customer Warranty Claims. Shop-stock receive/credit actions are wired separately.",
                ToastTone.Warning);
            return;
        }

        var operationKey = $"{SelectedRow.WorkId:D}|{operationName}|{ActionNote.Trim()}";
        _pendingCustomerOperationId ??= Guid.CreateVersion7();
        if (!string.Equals(_pendingCustomerOperationKey, operationKey, StringComparison.Ordinal))
        {
            _pendingCustomerOperationKey = operationKey;
            _pendingCustomerOperationId = Guid.CreateVersion7();
        }

        IsLoading = true;
        try
        {
            await action(_operationsService, SelectedRow.WorkId, _pendingCustomerOperationId.Value);
            _toastService?.Show(successMessage, ToastTone.Success);
            ActionNote = string.Empty;
            ClearPendingCustomerOperation();
            await RefreshAsync();
        }
        catch (OperationException ex)
        {
            _toastService?.Show(
                $"{ex.Code}: {ex.Message}. The same ClientOperationId is retained for safe retry.",
                ToastTone.Danger);
        }
        catch (Exception ex)
        {
            _toastService?.Show(
                $"Warranty operation outcome could not be confirmed: {ex.Message}. The same ClientOperationId is retained for retry.",
                ToastTone.Danger);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ClearPendingCustomerOperation()
    {
        _pendingCustomerOperationId = null;
        _pendingCustomerOperationKey = null;
    }

    private void ClearPendingCustomerReplacementOperation()
    {
        _pendingCustomerReplacementOperationId = null;
        _pendingCustomerReplacementOperationKey = null;
    }

    private void ClearPendingShopSendOperation()
    {
        _pendingShopSendOperationId = null;
        _pendingShopSendOperationKey = null;
    }

    private void ClearPendingShopReceiveOperation()
    {
        _pendingShopReceiveOperationId = null;
        _pendingShopReceiveOperationKey = null;
    }

    private static string BuildShopSendOperationKey(
        Guid productId,
        InventoryBucket sourceBucket,
        decimal quantity,
        Guid supplierId,
        Guid? sourcePurchaseItemId,
        IReadOnlyCollection<Guid>? unitIds,
        string note) =>
        string.Join("|",
            "SEND",
            productId.ToString("D"),
            sourceBucket,
            quantity.ToString(System.Globalization.CultureInfo.InvariantCulture),
            supplierId.ToString("D"),
            sourcePurchaseItemId?.ToString("D") ?? string.Empty,
            string.Join(",", (unitIds ?? Array.Empty<Guid>()).OrderBy(x => x).Select(x => x.ToString("D"))),
            note.Trim());

    private static string BuildShopReceiveOperationKey(
        Guid caseId,
        WarrantyResolutionType resolution,
        IReadOnlyCollection<Guid>? originalUnitIds,
        IReadOnlyList<ReplacementSerializedUnitInput>? replacementUnits,
        string note,
        decimal? credit,
        string supplierReference) =>
        string.Join("|",
            "RECEIVE",
            caseId.ToString("D"),
            resolution,
            string.Join(",", (originalUnitIds ?? Array.Empty<Guid>()).OrderBy(x => x).Select(x => x.ToString("D"))),
            string.Join(";", (replacementUnits ?? Array.Empty<ReplacementSerializedUnitInput>())
                .Select(x => $"{x.SerialNumber?.Trim()}|{x.Imei1?.Trim()}|{x.Imei2?.Trim()}")),
            note.Trim(),
            credit?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            supplierReference.Trim());

    private static bool Contains(string? source, string value) =>
        source?.Contains(value, StringComparison.OrdinalIgnoreCase) == true;
}
