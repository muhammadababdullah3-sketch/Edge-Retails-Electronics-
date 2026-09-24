using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Domain.Inventory;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class StocktakeLineViewModel : ViewModelBase
{
    private string _countedQuantityText = string.Empty;

    public StocktakeLineViewModel(BackendStocktakeLine row)
    {
        Row = row;
        if (row.CountedSellableQty is decimal counted)
        {
            _countedQuantityText = counted.ToString("0.######", CultureInfo.InvariantCulture);
        }
    }

    public BackendStocktakeLine Row { get; }
    public Guid ProductId => Row.ProductId;
    public string ProductName => Row.ProductName;
    public string Sku => Row.Sku;
    public bool IsSerialized => Row.IsSerialized;
    public bool IsQuantityTracked => !Row.IsSerialized;
    public decimal ExpectedSellableQty => Row.ExpectedSellableQty;
    public decimal? CountedSellableQty => Row.CountedSellableQty;
    public decimal VarianceQty => Row.VarianceQty;
    public string ExpectedDisplay => ExpectedSellableQty.ToString("0.##");
    public string CountStatusDisplay => CountedSellableQty is null
        ? "Not counted"
        : $"Counted {CountedSellableQty:0.##} · Δ {VarianceQty:+0.##;-0.##;0}";

    public string CountedQuantityText
    {
        get => _countedQuantityText;
        set => SetProperty(ref _countedQuantityText, value ?? string.Empty);
    }

    public bool TryGetQuantity(out decimal quantity)
    {
        if (decimal.TryParse(
                CountedQuantityText,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out quantity) ||
            decimal.TryParse(
                CountedQuantityText,
                NumberStyles.Number,
                CultureInfo.CurrentCulture,
                out quantity))
        {
            return quantity >= 0m;
        }

        quantity = 0m;
        return false;
    }
}

public sealed class StocktakeViewModel : ViewModelBase
{
    private readonly IBackendWorkflowReadService _service;
    private readonly IDialogService _dialogService;
    private readonly IToastService _toastService;
    private readonly Action? _completed;
    private BackendStocktakeSnapshot? _snapshot;
    private bool _isProcessing;
    private string? _message;

    public StocktakeViewModel(
        IBackendWorkflowReadService service,
        IDialogService dialogService,
        IToastService toastService,
        Action? completed = null)
    {
        _service = service;
        _dialogService = dialogService;
        _toastService = toastService;
        _completed = completed;
        Lines = [];

        StartCommand = new RelayCommand(
            async () => await StartAsync(),
            () => !IsProcessing && _snapshot is null);
        RecordQuantityCommand = new RelayCommand<StocktakeLineViewModel>(
            async line => await RecordQuantityAsync(line),
            line => !IsProcessing && IsCounting && line?.IsQuantityTracked == true);
        ScanSerializedCommand = new RelayCommand<StocktakeLineViewModel>(
            OpenSerializedScan,
            line => !IsProcessing && IsCounting && line?.IsSerialized == true);
        ReviewCommand = new RelayCommand(
            async () => await ReviewAsync(),
            () => !IsProcessing && IsCounting && Lines.Count > 0 &&
                  Lines.All(x => x.CountedSellableQty is not null));
        PostCommand = new RelayCommand(
            async () => await PostAsync(),
            () => !IsProcessing && IsReview);
        CancelStocktakeCommand = new RelayCommand(
            async () => await CancelStocktakeAsync(),
            () => !IsProcessing && _snapshot is not null);
        CloseCommand = new RelayCommand(_dialogService.Close, () => !IsProcessing);
        RefreshCommand = new RelayCommand(
            async () => await LoadAsync(),
            () => !IsProcessing);

        _ = LoadAsync();
    }

    public ObservableCollection<StocktakeLineViewModel> Lines { get; }
    public bool HasStocktake => _snapshot is not null;
    public bool IsCounting => _snapshot?.Status == StocktakeStatus.Counting;
    public bool IsReview => _snapshot?.Status == StocktakeStatus.Review;
    public string StatusDisplay => _snapshot?.Status.ToString() ?? "No open stocktake";
    public string StocktakeIdDisplay => _snapshot is null
        ? "Start a Full Shop physical count."
        : _snapshot.StocktakeId.ToString("D");
    public string? Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    public bool IsProcessing
    {
        get => _isProcessing;
        private set
        {
            if (SetProperty(ref _isProcessing, value))
            {
                RefreshCommands();
            }
        }
    }

    public ICommand StartCommand { get; }
    public ICommand RecordQuantityCommand { get; }
    public ICommand ScanSerializedCommand { get; }
    public ICommand ReviewCommand { get; }
    public ICommand PostCommand { get; }
    public ICommand CancelStocktakeCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand RefreshCommand { get; }

    private async Task LoadAsync()
    {
        if (IsProcessing)
        {
            return;
        }

        IsProcessing = true;
        try
        {
            ApplySnapshot(await _service.GetOpenStocktakeAsync());
            Message = _snapshot is null
                ? "No open stocktake. Start Full Shop count when ready."
                : null;
        }
        catch (Exception ex)
        {
            SetError(ex);
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private async Task StartAsync()
    {
        IsProcessing = true;
        try
        {
            ApplySnapshot(await _service.StartFullShopStocktakeAsync(
                "Desktop full-shop stocktake"));
            _toastService.Show("Full Shop stocktake started.", ToastTone.Success);
        }
        catch (Exception ex)
        {
            SetError(ex);
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private async Task RecordQuantityAsync(StocktakeLineViewModel? line)
    {
        if (line is null || _snapshot is null)
        {
            return;
        }

        if (!line.TryGetQuantity(out var quantity))
        {
            Message = $"{line.ProductName}: enter a valid non-negative physical quantity.";
            return;
        }

        IsProcessing = true;
        try
        {
            await _service.RecordStocktakeCountAsync(
                _snapshot.StocktakeId,
                line.ProductId,
                quantity,
                null);
            await ReloadWithoutGuardAsync();
        }
        catch (Exception ex)
        {
            SetError(ex);
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private void OpenSerializedScan(StocktakeLineViewModel? line)
    {
        if (line is null || _snapshot is null)
        {
            return;
        }

        _dialogService.ShowNested(new SerializedStocktakeScanViewModel(
            _snapshot,
            line,
            _service,
            _dialogService,
            ReloadWithoutGuardAsync));
    }

    private async Task ReviewAsync()
    {
        if (_snapshot is null)
        {
            return;
        }

        IsProcessing = true;
        try
        {
            await _service.ReviewStocktakeAsync(_snapshot.StocktakeId);
            await ReloadWithoutGuardAsync();
            _toastService.Show("Stocktake moved to Review.", ToastTone.Success);
        }
        catch (Exception ex)
        {
            SetError(ex);
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private async Task PostAsync()
    {
        if (_snapshot is null)
        {
            return;
        }

        IsProcessing = true;
        try
        {
            await _service.PostStocktakeAsync(_snapshot.StocktakeId);
            _toastService.Show("Stocktake posted through inventory authority.", ToastTone.Success);
            _completed?.Invoke();
            _dialogService.Close();
        }
        catch (Exception ex)
        {
            SetError(ex);
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private async Task CancelStocktakeAsync()
    {
        if (_snapshot is null)
        {
            return;
        }

        IsProcessing = true;
        try
        {
            await _service.CancelStocktakeAsync(_snapshot.StocktakeId);
            _toastService.Show("Stocktake cancelled.", ToastTone.Success);
            _completed?.Invoke();
            _dialogService.Close();
        }
        catch (Exception ex)
        {
            SetError(ex);
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private async Task ReloadWithoutGuardAsync()
    {
        ApplySnapshot(await _service.GetOpenStocktakeAsync());
    }

    private void ApplySnapshot(BackendStocktakeSnapshot? snapshot)
    {
        _snapshot = snapshot;
        Lines.Clear();
        if (snapshot is not null)
        {
            foreach (var line in snapshot.Items)
            {
                Lines.Add(new StocktakeLineViewModel(line));
            }
        }

        OnPropertyChanged(nameof(HasStocktake));
        OnPropertyChanged(nameof(IsCounting));
        OnPropertyChanged(nameof(IsReview));
        OnPropertyChanged(nameof(StatusDisplay));
        OnPropertyChanged(nameof(StocktakeIdDisplay));
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        ((RelayCommand)StartCommand).NotifyCanExecuteChanged();
        ((RelayCommand)ReviewCommand).NotifyCanExecuteChanged();
        ((RelayCommand)PostCommand).NotifyCanExecuteChanged();
        ((RelayCommand)CancelStocktakeCommand).NotifyCanExecuteChanged();
        ((RelayCommand)CloseCommand).NotifyCanExecuteChanged();
        ((RelayCommand)RefreshCommand).NotifyCanExecuteChanged();
    }

    private void SetError(Exception ex)
    {
        Message = ex is BackendOperationException backend
            ? $"{backend.Code}: {backend.Message}"
            : ex.Message;
        _toastService.Show(Message, ToastTone.Danger);
    }
}
