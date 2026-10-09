using System.Windows.Input;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class PriceCheckViewModel : ViewModelBase
{
    private readonly IDialogService _dialogService;

    public PriceCheckViewModel(BackendScannerMatch match, IDialogService dialogService)
    {
        Match = match;
        _dialogService = dialogService;
        CloseCommand = new RelayCommand(_dialogService.Close);
    }

    private readonly Func<string, Task<IReadOnlyList<BackendScannerMatch>>>? _resolve;
    private BackendScannerMatch? _match;
    private string _query = string.Empty;
    private string _message = "Enter a name, SKU or scanned identity.";
    private bool _busy;

    public PriceCheckViewModel(Func<string, Task<IReadOnlyList<BackendScannerMatch>>> resolve,
        IDialogService dialogService, string initialQuery = "")
    {
        _resolve = resolve;
        _dialogService = dialogService;
        _query = initialQuery;
        CloseCommand = new RelayCommand(_dialogService.Close);
        CheckCommand = new RelayCommand(async () => await CheckAsync(), () => !IsBusy);
    }

    public string Query { get => _query; set => SetProperty(ref _query, value); }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public bool IsBusy { get => _busy; private set { SetProperty(ref _busy, value); (CheckCommand as RelayCommand)?.NotifyCanExecuteChanged(); } }
    public bool HasResult => _match is not null;
    public ICommand? CheckCommand { get; }
    public BackendScannerMatch Match { get => _match!; private set => _match = value; }

    public async Task CheckAsync()
    {
        if (IsBusy || _resolve is null) { return; }
        _match = null;
        OnPropertyChanged(nameof(HasResult));
        var input = Query.Trim();
        if (input.Length == 0) { Message = "Enter a product identity to check its price."; return; }
        IsBusy = true;
        Message = "Checking price…";
        try
        {
            // Preserve product unit and physical identity; distinct variants must not be silently collapsed.
            var matches = (await _resolve(input)).DistinctBy(x => (x.ProductId, x.ProductUnitId, x.InventoryUnitId)).ToArray();
            if (matches.Length != 1)
            {
                Message = matches.Length == 0 ? "No product found. Check the identity and try again." : "Multiple matches found. Refine the SKU, barcode or exact identity.";
                return;
            }
            _match = matches[0];
            foreach (var name in new[] { nameof(ProductName), nameof(Sku), nameof(PriceDisplay), nameof(AvailabilityDisplay), nameof(TrackingDisplay), nameof(UnitDisplay), nameof(HasExactUnit), nameof(ExactIdentityDisplay), nameof(ExactUnitStatusDisplay), nameof(ResolutionDisplay), nameof(HasResult) }) { OnPropertyChanged(name); }
            Message = "Read-only price information. Nothing has been added to the cart.";
        }
        catch (Exception ex)
        {
            Message = DesktopErrorPresentation.ForException(ex, "Price Check is unavailable. Reconnect and try again.");
        }
        finally { IsBusy = false; }
    }
    public string ProductName => _match?.ProductName ?? string.Empty;
    public string Sku => _match?.Sku ?? string.Empty;
    public string PriceDisplay => _match is null ? string.Empty : $"Rs. {Match.UnitPrice:N0}";
    public string AvailabilityDisplay => _match is null ? string.Empty : Match.IsSerialized
        ? $"{Match.SellableStock:0} exact unit(s) available"
        : $"{Match.SellableStock:0.##} {Match.UnitSymbol} available";
    public string TrackingDisplay => _match is null ? string.Empty : Match.IsSerialized ? "Serialized / exact-unit" : "Quantity tracked";
    public string UnitDisplay => _match?.UnitSymbol ?? string.Empty;
    public bool HasExactUnit => _match?.InventoryUnitId is not null;
    public string ExactIdentityDisplay => _match is null ? string.Empty : string.Join(" · ", new[]
    {
        Match.TrackingCode,
        Match.SerialNumber,
        Match.Imei1,
        Match.Imei2
    }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public string ExactUnitStatusDisplay => _match?.UnitStatus?.ToString() ?? string.Empty;
    public string ResolutionDisplay => _match?.Namespace.ToString() ?? string.Empty;
    public ICommand CloseCommand { get; }
}
