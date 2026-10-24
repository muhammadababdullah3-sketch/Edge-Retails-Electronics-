using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using EdgeRetails.Application.Contracts.Packs;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

/// <summary>
/// Pack container picker dialog view model for POS cashiers.
/// Displays available physical containers sorted FIFO (open packs first),
/// handles barcode scanning, and enforces capacity checks with stale capacity alerts.
/// </summary>
public sealed class PackPickerViewModel : ViewModelBase
{
    private readonly RemotePosWorkflowService _workflowService;
    private readonly IDialogService? _dialogService;
    private readonly Action<PackContainerDto>? _onConfirmed;
    private readonly Guid _productId;
    private readonly decimal _requestedQuantity;
    private PackContainerDto? _selectedContainer;
    private bool _isLoading;
    private string? _errorMessage;
    private string? _staleCapacityPrompt;
    private bool _isConfirmed;

    public PackPickerViewModel(
        RemotePosWorkflowService workflowService,
        Guid productId,
        string productName,
        decimal requestedQuantity,
        string unitOfMeasure = "pcs",
        IDialogService? dialogService = null,
        Action<PackContainerDto>? onConfirmed = null)
    {
        _workflowService = workflowService ?? throw new ArgumentNullException(nameof(workflowService));
        _dialogService = dialogService;
        _onConfirmed = onConfirmed;
        _productId = productId;
        ProductName = productName;
        _requestedQuantity = requestedQuantity;
        UnitOfMeasure = unitOfMeasure;

        Containers = new ObservableCollection<PackContainerDto>();
        SelectContainerCommand = new RelayCommand(SelectContainerAction);
        ConfirmCommand = new RelayCommand(Confirm, () => CanConfirm);
        CancelCommand = new RelayCommand(Cancel);
        RefreshCommand = new RelayCommand(async () => await LoadContainersAsync());
    }

    public Guid ProductId => _productId;
    public string ProductName { get; }
    public decimal RequestedQuantity => _requestedQuantity;
    public string UnitOfMeasure { get; }

    public ObservableCollection<PackContainerDto> Containers { get; }

    public PackContainerDto? SelectedContainer
    {
        get => _selectedContainer;
        set
        {
            if (SetProperty(ref _selectedContainer, value))
            {
                OnPropertyChanged(nameof(CanConfirm));
                ((RelayCommand)ConfirmCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    public string? StaleCapacityPrompt
    {
        get => _staleCapacityPrompt;
        set => SetProperty(ref _staleCapacityPrompt, value);
    }

    public bool IsConfirmed => _isConfirmed;
    public bool CanConfirm => SelectedContainer is not null && !IsLoading;

    public ICommand SelectContainerCommand { get; }
    public ICommand ConfirmCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand RefreshCommand { get; }

    public async Task LoadContainersAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        ErrorMessage = null;
        StaleCapacityPrompt = null;
        Containers.Clear();

        try
        {
            var response = await _workflowService.GetPackContainersAsync(
                _productId,
                conditionFilter: null,
                minContinuousLength: null,
                pageNumber: 1,
                pageSize: 50,
                cancellationToken: cancellationToken);

            // Sort open containers FIFO (consume open packs before breaking sealed ones)
            var sorted = response.Items
                .OrderBy(c => string.Equals(c.State, "Open", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(c => c.RemainingQuantity)
                .ToList();

            foreach (var item in sorted)
            {
                Containers.Add(item);
            }

            // Auto-select the first FIFO open container that satisfies requested quantity
            var bestFit = Containers.FirstOrDefault(c => c.RemainingQuantity >= _requestedQuantity);
            if (bestFit is not null)
            {
                SelectedContainer = bestFit;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to load pack containers: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void SelectContainerAction()
    {
        // Parameterless relay command hook
    }

    public void SelectContainer(PackContainerDto? container)
    {
        if (container is null)
        {
            return;
        }

        if (container.RemainingQuantity < _requestedQuantity)
        {
            StaleCapacityPrompt = $"Selected container '{container.TrackingCode}' has only {container.RemainingQuantity} {container.UnitOfMeasure} remaining (requested {_requestedQuantity}). Please re-select an open container.";
        }
        else
        {
            StaleCapacityPrompt = null;
        }

        SelectedContainer = container;
    }

    public void PromptStaleCapacity(string trackingCode, decimal remainingNow)
    {
        StaleCapacityPrompt = $"Container '{trackingCode}' capacity has changed to {remainingNow} (stale capacity). Please re-select an open container.";
        SelectedContainer = null;
    }

    public void Confirm()
    {
        if (SelectedContainer is null)
        {
            return;
        }

        if (SelectedContainer.RemainingQuantity < _requestedQuantity)
        {
            StaleCapacityPrompt = $"Selected container '{SelectedContainer.TrackingCode}' capacity is insufficient. Please re-select an open container.";
            return;
        }

        _isConfirmed = true;
        _onConfirmed?.Invoke(SelectedContainer);
        _dialogService?.Close();
    }

    public void Cancel()
    {
        _dialogService?.Close();
    }
}
