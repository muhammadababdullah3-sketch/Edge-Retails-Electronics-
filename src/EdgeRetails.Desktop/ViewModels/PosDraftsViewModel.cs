using System.Collections.ObjectModel;
using System.Windows.Input;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class PosDraftsViewModel : ViewModelBase
{
    private readonly IBackendWorkflowReadService _service;
    private readonly IDialogService _dialogService;
    private readonly IToastService? _toastService;
    private readonly Action<Guid> _resume;
    private bool _isLoading;
    private string? _message;

    public PosDraftsViewModel(
        IBackendWorkflowReadService service,
        IDialogService dialogService,
        Action<Guid> resume,
        IToastService? toastService = null)
    {
        _service = service;
        _dialogService = dialogService;
        _resume = resume;
        _toastService = toastService;
        Drafts = [];
        ResumeCommand = new RelayCommand<BackendPosDraftSummary>(Resume);
        CancelDraftCommand = new RelayCommand<BackendPosDraftSummary>(async x => await CancelAsync(x));
        CloseCommand = new RelayCommand(_dialogService.Close);
        RefreshCommand = new RelayCommand(async () => await LoadAsync(), () => !IsLoading);
        _ = LoadAsync();
    }

    public ObservableCollection<BackendPosDraftSummary> Drafts { get; }
    public ICommand ResumeCommand { get; }
    public ICommand CancelDraftCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand RefreshCommand { get; }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                ((RelayCommand)RefreshCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public string? Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    private async Task LoadAsync()
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        Message = null;
        try
        {
            var rows = await _service.GetOpenDraftsAsync();
            Drafts.Clear();
            foreach (var row in rows)
            {
                Drafts.Add(row);
            }

            if (rows.Count == 0)
            {
                Message = "No open POS drafts.";
            }
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Resume(BackendPosDraftSummary? draft)
    {
        if (draft is null)
        {
            return;
        }

        _dialogService.Close();
        _resume(draft.DraftId);
    }

    private async Task CancelAsync(BackendPosDraftSummary? draft)
    {
        if (draft is null || IsLoading)
        {
            return;
        }

        IsLoading = true;
        try
        {
            await _service.CancelDraftAsync(draft.DraftId, draft.Version);
            Drafts.Remove(draft);
            _toastService?.Show($"Draft {draft.DraftNumber} cancelled.", ToastTone.Success);
        }
        catch (BackendOperationException ex)
        {
            Message = $"{ex.Code}: {ex.Message}";
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }
}
