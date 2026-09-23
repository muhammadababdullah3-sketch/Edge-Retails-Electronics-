using EdgeRetails.Desktop.ViewModels;

namespace EdgeRetails.Desktop.Services;

public sealed class DialogService : ViewModelBase, IDialogService
{
    private readonly Stack<object> _nestedParents = new();
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
        ClearNestedParents();

        if (!ReferenceEquals(Content, content))
        {
            DisposeContent(Content);
        }

        Content = content;
        IsOpen = true;
    }

    public void ShowNested(object content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (Content is not null)
        {
            _nestedParents.Push(Content);
        }

        Content = content;
        IsOpen = true;
    }

    public void Close()
    {
        var content = Content;
        DisposeContent(content);

        if (_nestedParents.Count > 0)
        {
            Content = _nestedParents.Pop();
            IsOpen = true;
            return;
        }

        IsOpen = false;
        Content = null;
    }

    private void ClearNestedParents()
    {
        while (_nestedParents.Count > 0)
        {
            DisposeContent(_nestedParents.Pop());
        }
    }

    private static void DisposeContent(object? content)
    {
        if (content is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}

public static class DialogServicePhase4Extensions
{
    public static void ShowNested(this IDialogService service, object content)
    {
        if (service is DialogService concrete)
        {
            concrete.ShowNested(content);
            return;
        }

        service.Show(content);
    }
}
