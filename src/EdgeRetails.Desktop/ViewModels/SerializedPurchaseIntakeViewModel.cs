using System.Collections.ObjectModel;
using System.Windows.Input;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class SerializedIdentityEntryViewModel : ViewModelBase
{
    private string _serialNumber = string.Empty;
    private string _imei1 = string.Empty;
    private string _imei2 = string.Empty;

    public int Sequence { get; init; }
    public string SerialNumber
    {
        get => _serialNumber;
        set => SetProperty(ref _serialNumber, value?.Trim() ?? string.Empty);
    }

    public string Imei1
    {
        get => _imei1;
        set => SetProperty(ref _imei1, value?.Trim() ?? string.Empty);
    }

    public string Imei2
    {
        get => _imei2;
        set => SetProperty(ref _imei2, value?.Trim() ?? string.Empty);
    }
}

public sealed class SerializedPurchaseIntakeViewModel : ViewModelBase
{
    private readonly NewPurchaseLineViewModel _line;
    private readonly IDialogService _dialogService;
    private readonly Action<IReadOnlyList<BackendSerializedIdentityInput>> _confirmed;
    private string? _validationMessage;

    public SerializedPurchaseIntakeViewModel(
        NewPurchaseLineViewModel line,
        IDialogService dialogService,
        Action<IReadOnlyList<BackendSerializedIdentityInput>> confirmed)
    {
        _line = line;
        _dialogService = dialogService;
        _confirmed = confirmed;

        Rows = [];
        var existing = line.SerializedIdentities;
        for (var i = 0; i < line.RequiredSerializedUnitCount; i++)
        {
            var old = i < existing.Count ? existing[i] : null;
            Rows.Add(new SerializedIdentityEntryViewModel
            {
                Sequence = i + 1,
                SerialNumber = old?.SerialNumber ?? string.Empty,
                Imei1 = old?.Imei1 ?? string.Empty,
                Imei2 = old?.Imei2 ?? string.Empty
            });
        }

        ConfirmCommand = new RelayCommand(Confirm);
        CancelCommand = new RelayCommand(_dialogService.Close);
    }

    public string Title => $"Serialized Intake · {_line.ProductName}";
    public string RequirementDisplay =>
        $"{_line.RequiredSerializedUnitCount} exact manufacturer identity row(s) required for {_line.Quantity:0.##} {_line.Unit}.";
    public bool SerialRequired => _line.Product.SerialTrackingEnabled;
    public bool ImeiRequired => _line.Product.ImeiTrackingEnabled;
    public ObservableCollection<SerializedIdentityEntryViewModel> Rows { get; }

    public string? ValidationMessage
    {
        get => _validationMessage;
        private set => SetProperty(ref _validationMessage, value);
    }

    public ICommand ConfirmCommand { get; }
    public ICommand CancelCommand { get; }

    private void Confirm()
    {
        ValidationMessage = null;
        if (Rows.Count != _line.RequiredSerializedUnitCount)
        {
            ValidationMessage = "Identity count no longer matches the purchase base quantity.";
            return;
        }

        if (SerialRequired && Rows.Any(x => string.IsNullOrWhiteSpace(x.SerialNumber)))
        {
            ValidationMessage = "Every unit requires a Serial Number.";
            return;
        }

        if (ImeiRequired && Rows.Any(x => string.IsNullOrWhiteSpace(x.Imei1)))
        {
            ValidationMessage = "Every unit requires IMEI 1.";
            return;
        }

        var serials = Rows
            .Where(x => !string.IsNullOrWhiteSpace(x.SerialNumber))
            .Select(x => x.SerialNumber.Trim())
            .ToArray();
        if (serials.Distinct(StringComparer.OrdinalIgnoreCase).Count() != serials.Length)
        {
            ValidationMessage = "Duplicate Serial Number detected in this intake.";
            return;
        }

        var imeis = Rows
            .SelectMany(x => new[] { x.Imei1, x.Imei2 })
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .ToArray();
        if (imeis.Distinct(StringComparer.OrdinalIgnoreCase).Count() != imeis.Length)
        {
            ValidationMessage = "Duplicate IMEI detected in this intake.";
            return;
        }

        _confirmed(Rows.Select(x => new BackendSerializedIdentityInput(
            string.IsNullOrWhiteSpace(x.SerialNumber) ? null : x.SerialNumber.Trim(),
            string.IsNullOrWhiteSpace(x.Imei1) ? null : x.Imei1.Trim(),
            string.IsNullOrWhiteSpace(x.Imei2) ? null : x.Imei2.Trim()))
            .ToArray());
        _dialogService.Close();
    }
}
