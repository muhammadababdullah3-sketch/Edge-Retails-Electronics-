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

    private async Task ConfirmAsync()
    {
        if (IsProcessing)
        {
            return;
        }

        IsProcessing = true;
        Message = null;
        try
        {
            var identities = ScannedIdentities
                .Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray();

            if (identities.Distinct(StringComparer.OrdinalIgnoreCase).Count() != identities.Length)
            {
                Message = "The same scanner identity appears more than once.";
                return;
            }

            var foundIds = new List<Guid>();
            var unexpected = new List<string>();

            foreach (var identity in identities)
            {
                var matches = await _service.ResolveScannerAsync(identity);
                var exactMatches = matches
                    .Where(x => x.InventoryUnitId is not null)
                    .ToArray();

                if (exactMatches.Length == 1)
                {
                    foundIds.Add(exactMatches[0].InventoryUnitId!.Value);
                }
                else
                {
                    unexpected.Add(identity);
                }
            }

            if (foundIds.Distinct().Count() != foundIds.Count)
            {
                Message = "Multiple scanned identities resolve to the same physical unit.";
                return;
            }

            await _service.RecordSerializedStocktakeAsync(
                _stocktake.StocktakeId,
                _line.ProductId,
                foundIds,
                unexpected,
                null);

            await _refreshParent();
            _dialogService.Close();
        }
        catch (BackendOperationException ex)
        {
            Message = $"{ex.Code}: {ex.Message}";
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
        finally
        {
            IsProcessing = false;
        }
    }
}
