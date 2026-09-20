using System.ComponentModel;

namespace EdgeRetails.Desktop.Services;

public interface IDrawerService : INotifyPropertyChanged
{
    bool IsOpen { get; }

    object? Content { get; }

    void Show(object content);

    void Close();
}
