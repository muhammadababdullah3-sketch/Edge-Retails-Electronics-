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

public sealed class TypedRestoreConfirmationViewModel : ViewModelBase
{
    private readonly Action<string> _confirm;
    private readonly Action _close;
    private string _enteredText = string.Empty;

    public TypedRestoreConfirmationViewModel(
        string title,
        string consequence,
        string requiredPhrase,
        string confirmText,
        Action<string> confirm,
        Action close)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(consequence);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredPhrase);
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmText);
        ArgumentNullException.ThrowIfNull(confirm);
        ArgumentNullException.ThrowIfNull(close);

        Title = title;
        Consequence = consequence;
        RequiredPhrase = requiredPhrase;
        ConfirmText = confirmText;
        _confirm = confirm;
        _close = close;
        ConfirmCommand = new RelayCommand(Confirm, () => CanConfirm);
        CancelCommand = new RelayCommand(_close);
    }

    public string Title { get; }
    public string Consequence { get; }
    public string RequiredPhrase { get; }
    public string ConfirmText { get; }
    public RelayCommand ConfirmCommand { get; }
    public ICommand CancelCommand { get; }
    public bool CanConfirm => string.Equals(EnteredText, RequiredPhrase, StringComparison.Ordinal);

    public string EnteredText
    {
        get => _enteredText;
        set
        {
            if (SetProperty(ref _enteredText, value))
            {
                OnPropertyChanged(nameof(CanConfirm));
                ConfirmCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private void Confirm()
    {
        if (!CanConfirm)
        {
            return;
        }

        _confirm(EnteredText);
        _close();
    }
}
