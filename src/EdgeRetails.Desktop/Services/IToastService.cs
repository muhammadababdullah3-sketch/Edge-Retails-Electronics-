using System.Collections.ObjectModel;

namespace EdgeRetails.Desktop.Services;

public interface IToastService
{
    ReadOnlyObservableCollection<ToastMessage> Messages { get; }

    void Show(
        string message,
        ToastTone tone = ToastTone.Neutral,
        TimeSpan? duration = null);
}
