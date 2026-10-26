using System.Globalization;
using System.Windows.Input;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class CashShiftOpenViewModel : ViewModelBase
{
    private readonly ICashSessionClient _cashSessionClient;
    private readonly IDialogService _dialogService;
    private readonly IToastService _toastService;
    private string _openingCashText = "0";
    private string? _note;
    private bool _isSubmitting;

    public CashShiftOpenViewModel(
        ICashSessionClient cashSessionClient,
        IDialogService dialogService,
        IToastService toastService)
    {
        _cashSessionClient = cashSessionClient;
        _dialogService = dialogService;
        _toastService = toastService;
        ConfirmCommand = new RelayCommand(async () => await ConfirmAsync(), () => CanConfirm);
        SkipCommand = new RelayCommand(() => _dialogService.Close());
    }

    public string OpeningCashText
    {
        get => _openingCashText;
        set
        {
            if (SetProperty(ref _openingCashText, value))
            {
                OnPropertyChanged(nameof(CanConfirm));
            }
        }
    }

    public string? Note
    {
        get => _note;
        set => SetProperty(ref _note, value);
    }

    public bool CanConfirm =>
        !_isSubmitting &&
        decimal.TryParse(OpeningCashText, NumberStyles.Number, CultureInfo.CurrentCulture, out var amount) &&
        amount >= 0m;

    public ICommand ConfirmCommand { get; }
    public ICommand SkipCommand { get; }

    private async Task ConfirmAsync()
    {
        if (!decimal.TryParse(OpeningCashText, NumberStyles.Number, CultureInfo.CurrentCulture, out var amount) || amount < 0m)
        {
            return;
        }

        _isSubmitting = true;
        OnPropertyChanged(nameof(CanConfirm));
        try
        {
            await _cashSessionClient.OpenAsync(amount, Note);
            _toastService.Show("Cash drawer session opened.", ToastTone.Success);
            _dialogService.Close();
        }
        catch (Exception ex)
        {
            _toastService.Show(
                DesktopErrorPresentation.ForException(ex, "The cash session could not be opened."),
                ToastTone.Warning);
        }
        finally
        {
            _isSubmitting = false;
            OnPropertyChanged(nameof(CanConfirm));
        }
    }
}
