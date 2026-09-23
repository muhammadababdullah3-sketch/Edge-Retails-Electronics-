using System.Windows.Input;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class PriceCheckViewModel
{
    private readonly IDialogService _dialogService;

    public PriceCheckViewModel(BackendScannerMatch match, IDialogService dialogService)
    {
        Match = match;
        _dialogService = dialogService;
        CloseCommand = new RelayCommand(_dialogService.Close);
    }

    public BackendScannerMatch Match { get; }
    public string ProductName => Match.ProductName;
    public string Sku => Match.Sku;
    public string PriceDisplay => $"Rs. {Match.UnitPrice:N0}";
    public string AvailabilityDisplay => Match.IsSerialized
        ? $"{Match.SellableStock:0} exact unit(s) available"
        : $"{Match.SellableStock:0.##} {Match.UnitSymbol} available";
    public string TrackingDisplay => Match.IsSerialized ? "Serialized / exact-unit" : "Quantity tracked";
    public string UnitDisplay => Match.UnitSymbol;
    public string ResolutionDisplay => Match.Namespace.ToString();
    public ICommand CloseCommand { get; }
}
