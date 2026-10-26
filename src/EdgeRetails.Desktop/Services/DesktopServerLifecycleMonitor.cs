using System.Windows.Threading;

namespace EdgeRetails.Desktop.Services;

/// <summary>Phase 8 P8-LIFE-01: detect single-host server drain while Desktop is running.</summary>
public sealed class DesktopServerLifecycleMonitor : IDisposable
{
    private readonly DesktopApiClient _apiClient;
    private readonly DispatcherTimer _timer;
    private bool _drainNotified;

    public DesktopServerLifecycleMonitor(DesktopApiClient apiClient)
    {
        _apiClient = apiClient;
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(45)
        };
        _timer.Tick += OnTimerTick;
    }

    public event EventHandler? ServerDrainingDetected;

    public void Start()
    {
        _drainNotified = false;
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
    }

    private async void OnTimerTick(object? sender, EventArgs e)
    {
        if (_drainNotified)
        {
            return;
        }

        try
        {
            var lifecycle = await _apiClient.GetAsync<ServerLifecycleSnapshot>(
                "/api/system/lifecycle",
                CancellationToken.None);
            if (!lifecycle.IsDraining)
            {
                return;
            }

            _drainNotified = true;
            _timer.Stop();
            ServerDrainingDetected?.Invoke(this, EventArgs.Empty);
        }
        catch (DesktopApiException)
        {
            // Transient network failures should not force logout.
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
    }

    private sealed record ServerLifecycleSnapshot(bool IsDraining);
}
