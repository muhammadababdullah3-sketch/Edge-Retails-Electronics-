using System.Windows.Threading;

namespace EdgeRetails.Desktop.Services;

public sealed class LiveClockService : ILiveClock
{
    private readonly DispatcherTimer _timer;

    public LiveClockService()
    {
        Now = DateTimeOffset.Now;

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };

        _timer.Tick += OnTimerTick;
        _timer.Start();
    }

    public event EventHandler<DateTimeOffset>? Tick;

    public DateTimeOffset Now { get; private set; }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        Now = DateTimeOffset.Now;
        Tick?.Invoke(this, Now);
    }
}
