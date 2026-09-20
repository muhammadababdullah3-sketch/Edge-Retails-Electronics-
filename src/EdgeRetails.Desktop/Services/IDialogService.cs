using System.ComponentModel;

namespace EdgeRetails.Desktop.Services;

public interface IDialogService : INotifyPropertyChanged
{
    bool IsOpen { get; }

    object? Content { get; }

    void Show(object content);

    void Close();
}
