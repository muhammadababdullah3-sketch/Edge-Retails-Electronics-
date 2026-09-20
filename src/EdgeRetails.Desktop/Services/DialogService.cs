using EdgeRetails.Desktop.ViewModels;

namespace EdgeRetails.Desktop.Services;

public sealed class DialogService : ViewModelBase, IDialogService
{
    private object? _content;
    private bool _isOpen;

    public bool IsOpen
    {
        get => _isOpen;
        private set => SetProperty(ref _isOpen, value);
    }

    public object? Content
    {
        get => _content;
        private set => SetProperty(ref _content, value);
    }

    public void Show(object content)
    {
        ArgumentNullException.ThrowIfNull(content);
        Content = content;
        IsOpen = true;
    }

    public void Close()
    {
        IsOpen = false;
        Content = null;
    }
}
