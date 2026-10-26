using System.Windows;
using System.Windows.Threading;

namespace EdgeRetails.Desktop.Services;

/// <summary>Phase 8 P8-CONC-01: marshal callbacks onto the WPF UI dispatcher.</summary>
public static class DesktopUiThread
{
    public static void Invoke(Action action, DispatcherPriority priority = DispatcherPriority.Normal)
    {
        ArgumentNullException.ThrowIfNull(action);
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action, priority);
    }
}
