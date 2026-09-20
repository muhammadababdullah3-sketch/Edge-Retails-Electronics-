using System.Windows.Input;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class Sprint1OverlayPreviewViewModel : ViewModelBase
{
    public Sprint1OverlayPreviewViewModel(
        string title,
        string message,
        Action closeAction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentNullException.ThrowIfNull(closeAction);

        Title = title;
        Message = message;
        CloseCommand = new RelayCommand(closeAction);
    }

    public string Title { get; }

    public string Message { get; }

    public ICommand CloseCommand { get; }
}
