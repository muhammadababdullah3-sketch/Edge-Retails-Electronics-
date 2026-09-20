using System.Collections.ObjectModel;
using System.Windows.Threading;

namespace EdgeRetails.Desktop.Services;

public sealed class ToastService : IToastService
{
    private static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(4);
    private const int MaximumVisibleToasts = 4;

    private readonly ObservableCollection<ToastMessage> _messages = new();
    private readonly Dispatcher _dispatcher;

    public ToastService()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        Messages = new ReadOnlyObservableCollection<ToastMessage>(_messages);
    }

    public ReadOnlyObservableCollection<ToastMessage> Messages { get; }

    public void Show(
        string message,
        ToastTone tone = ToastTone.Neutral,
        TimeSpan? duration = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        while (_messages.Count >= MaximumVisibleToasts)
        {
            _messages.RemoveAt(0);
        }

        var toast = new ToastMessage(Guid.NewGuid(), message, tone);
        _messages.Add(toast);

        _ = RemoveAfterDelayAsync(toast, duration ?? DefaultDuration);
    }

    private async Task RemoveAfterDelayAsync(
        ToastMessage toast,
        TimeSpan duration)
    {
        await Task.Delay(duration).ConfigureAwait(false);

        await _dispatcher.InvokeAsync(
            () => _messages.Remove(toast),
            DispatcherPriority.Background);
    }
}
