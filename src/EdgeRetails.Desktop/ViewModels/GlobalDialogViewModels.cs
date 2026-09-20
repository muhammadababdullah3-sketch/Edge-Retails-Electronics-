using System.Windows.Input;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class PermissionRequiredViewModel : ViewModelBase
{
    private readonly Action _close;

    public PermissionRequiredViewModel(string message, Action close)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentNullException.ThrowIfNull(close);

        Message = message;
        _close = close;
        CloseCommand = new RelayCommand(_close);
    }

    public string Title => "Permission Required";
    public string Message { get; }
    public ICommand CloseCommand { get; }
}

public sealed class ConfirmationDialogViewModel : ViewModelBase
{
    private readonly Action _confirm;
    private readonly Action _cancel;

    public ConfirmationDialogViewModel(
        string title,
        string consequence,
        string confirmText,
        Action confirm,
        Action cancel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(consequence);
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmText);
        ArgumentNullException.ThrowIfNull(confirm);
        ArgumentNullException.ThrowIfNull(cancel);

        Title = title;
        Consequence = consequence;
        ConfirmText = confirmText;
        _confirm = confirm;
        _cancel = cancel;

        ConfirmCommand = new RelayCommand(Confirm);
        CancelCommand = new RelayCommand(_cancel);
    }

    public string Title { get; }
    public string Consequence { get; }
    public string ConfirmText { get; }
    public ICommand ConfirmCommand { get; }
    public ICommand CancelCommand { get; }

    private void Confirm()
    {
        _confirm();
        _cancel();
    }
}
