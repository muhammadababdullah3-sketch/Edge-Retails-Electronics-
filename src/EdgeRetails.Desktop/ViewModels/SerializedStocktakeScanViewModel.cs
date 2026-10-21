using System.Globalization;
using System.Windows.Input;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class SerializedStocktakeScanViewModel : ViewModelBase
{
    private readonly BackendStocktakeSnapshot _stocktake;
    private readonly StocktakeLineViewModel _line;
    private readonly IBackendWorkflowReadService _service;
    private readonly IDialogService _dialogService;
    private readonly Func<Task> _refreshParent;
    private string _scannedIdentities = string.Empty;
    private string _separateLooseCountText = string.Empty;
    private string? _message;
    private bool _isProcessing;

    public SerializedStocktakeScanViewModel(
        BackendStocktakeSnapshot stocktake,
        StocktakeLineViewModel line,
        IBackendWorkflowReadService service,
        IDialogService dialogService,
        Func<Task> refreshParent)
    {
        _stocktake = stocktake;
        _line = line;
        _service = service;
        _dialogService = dialogService;
        _refreshParent = refreshParent;
        ConfirmCommand = new RelayCommand(
            async () => await ConfirmAsync(),
            () => !IsProcessing);
        CancelCommand = new RelayCommand(
            _dialogService.Close,
            () => !IsProcessing);
    }

    public string Title => $"Serialized Count · {_line.ProductName}";
    public string ExpectedDisplay => $"Snapshot expected: {_line.ExpectedSellableQty:0} exact unit(s)";

    public string ScannedIdentities
    {
        get => _scannedIdentities;
        set => SetProperty(ref _scannedIdentities, value ?? string.Empty);
    }

    public string SeparateLooseCountText
    {
        get => _separateLooseCountText;
        set => SetProperty(ref _separateLooseCountText, value ?? string.Empty);
    }

    public string? Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    public bool IsProcessing
    {
        get => _isProcessing;
        private set
        {
            if (SetProperty(ref _isProcessing, value))
            {
                ((RelayCommand)ConfirmCommand).NotifyCanExecuteChanged();
                ((RelayCommand)CancelCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public ICommand ConfirmCommand { get; }
    public ICommand CancelCommand { get; }

    public async Task ConfirmAsync()
    {
        if (IsProcessing)
        {
            return;
        }

        IsProcessing = true;
        Message = null;
        try
        {
            var rawLines = ScannedIdentities
                .Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray();

            var parsedItems = new List<(string Identity, decimal? MeasuredQuantity)>();
            foreach (var rawLine in rawLines)
            {
                var parts = rawLine.Split(['=', ':'], 2, StringSplitOptions.TrimEntries);
                var identity = parts[0];
                decimal? operatorMeasuredQty = null;
                if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[1]))
                {
                    if (!decimal.TryParse(parts[1], NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedQty) &&
                        !decimal.TryParse(parts[1], NumberStyles.Number, CultureInfo.CurrentCulture, out parsedQty))
                    {
                        Message = $"Invalid measured quantity format '{parts[1]}' for identity '{identity}'.";
                        return;
                    }
                    if (parsedQty < 0m)
                    {
                        Message = "Measured container quantity cannot be negative.";
                        return;
                    }
                    operatorMeasuredQty = parsedQty;
                }
                parsedItems.Add((identity, operatorMeasuredQty));
            }

            if (parsedItems.Select(x => x.Identity).Distinct(StringComparer.OrdinalIgnoreCase).Count() != parsedItems.Count)
            {
                Message = "The same scanner identity appears more than once.";
                return;
            }

            string? reviewNote = null;
            decimal separateLooseQuantity = 0m;
            if (!string.IsNullOrWhiteSpace(SeparateLooseCountText))
            {
                if (!decimal.TryParse(SeparateLooseCountText.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var looseCount) &&
                    !decimal.TryParse(SeparateLooseCountText.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out looseCount))
                {
                    Message = "Separate loose count must be a valid number.";
                    return;
                }
                if (looseCount < 0m)
                {
                    Message = "Separate loose count cannot be negative.";
                    return;
                }
                separateLooseQuantity = looseCount;
                reviewNote = FormattableString.Invariant($"[Loose stock counted: {looseCount}]");
            }

            var foundIds = new List<Guid>();
            var unexpected = new List<string>();
            var measuredQuantities = new Dictionary<Guid, decimal>();

            foreach (var (identity, operatorMeasuredQty) in parsedItems)
            {
                var matches = await _service.ResolveScannerAsync(identity);
                var exactMatches = matches
                    .Where(x => x.InventoryUnitId is not null)
                    .ToArray();

                if (exactMatches.Length == 1)
                {
                    var unitId = exactMatches[0].InventoryUnitId!.Value;
                    foundIds.Add(unitId);
                    if (operatorMeasuredQty.HasValue)
                    {
                        measuredQuantities[unitId] = operatorMeasuredQty.Value;
                    }
                }
                else
                {
                    var pack = await _service.GetPackByTrackingCodeAsync(identity);
                    if (pack is not null)
                    {
                        foundIds.Add(pack.ContainerId);
                        var measured = operatorMeasuredQty ?? pack.RemainingQuantity;
                        if (pack.InitialQuantity > 0m && measured > pack.InitialQuantity)
                        {
                            Message = $"Measured quantity {measured} cannot exceed initial container capacity {pack.InitialQuantity}.";
                            return;
                        }
                        measuredQuantities[pack.ContainerId] = measured;
                    }
                    else
                    {
                        unexpected.Add(identity);
                    }
                }
            }

            if (foundIds.Distinct().Count() != foundIds.Count)
            {
                Message = "Multiple scanned identities resolve to the same physical unit or container.";
                return;
            }

            await _service.RecordSerializedStocktakeAsync(
                _stocktake.StocktakeId,
                _line.ProductId,
                foundIds,
                unexpected,
                reviewNote,
                measuredQuantities.Count > 0 ? measuredQuantities : null,
                separateLooseQuantity: separateLooseQuantity);

            await _refreshParent();
            _dialogService.Close();
        }
        catch (BackendOperationException ex)
        {
            Message = DesktopErrorPresentation.ForException(
                ex,
                "The stocktake scan was rejected.");
        }
        catch (Exception ex)
        {
            Message = DesktopErrorPresentation.ForException(
                ex,
                "The stocktake scan could not be recorded.");
        }
        finally
        {
            IsProcessing = false;
        }
    }
}
