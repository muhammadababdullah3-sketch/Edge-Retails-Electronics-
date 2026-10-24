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
/// Cable cutting and continuous measurement view model for Desktop POS.
/// Prioritizes continuous eligible coils first, enforces explicit segmented/split-cut
/// confirmation, and issues remnant warnings when remaining length falls below 5 meters.
/// </summary>
public sealed class CableCutViewModel : ViewModelBase
{
    private readonly RemotePosWorkflowService _workflowService;
    private readonly IDialogService? _dialogService;
    private readonly Action<PackContainerDto, decimal, bool>? _onConfirmed;
    private readonly Guid _productId;
    private decimal _requestedCutLength;
    private bool _allowSplitCuts;
    private PackContainerDto? _selectedCoil;
    private bool _isLoading;
    private string? _errorMessage;
    private string? _remnantWarning;
    private string? _splitCutNotice;
    private bool _isConfirmed;

    public CableCutViewModel(
        RemotePosWorkflowService workflowService,
        Guid productId,
        string productName,
        decimal requestedCutLength,
        string unitOfMeasure = "m",
        IDialogService? dialogService = null,
        Action<PackContainerDto, decimal, bool>? onConfirmed = null)
    {
        _workflowService = workflowService ?? throw new ArgumentNullException(nameof(workflowService));
        _dialogService = dialogService;
        _onConfirmed = onConfirmed;
        _productId = productId;
        ProductName = productName;
        _requestedCutLength = requestedCutLength;
        UnitOfMeasure = unitOfMeasure;

        Coils = new ObservableCollection<PackContainerDto>();
        ConfirmCommand = new RelayCommand(Confirm, () => CanConfirm);
        CancelCommand = new RelayCommand(Cancel);
        RefreshCommand = new RelayCommand(async () => await LoadCoilsAsync());
    }

    public Guid ProductId => _productId;
    public string ProductName { get; }
    public string UnitOfMeasure { get; }

    public decimal RequestedCutLength
    {
        get => _requestedCutLength;
        set
        {
            if (SetProperty(ref _requestedCutLength, value))
            {
                EvaluateSelection();
                OnPropertyChanged(nameof(CanConfirm));
            }
        }
    }

    public bool AllowSplitCuts
    {
        get => _allowSplitCuts;
        set
        {
            if (SetProperty(ref _allowSplitCuts, value))
            {
                EvaluateSelection();
                OnPropertyChanged(nameof(CanConfirm));
                ((RelayCommand)ConfirmCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public ObservableCollection<PackContainerDto> Coils { get; }

    public PackContainerDto? SelectedCoil
    {
        get => _selectedCoil;
        set
        {
            if (SetProperty(ref _selectedCoil, value))
            {
                EvaluateSelection();
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

    public string? RemnantWarning
    {
        get => _remnantWarning;
        set => SetProperty(ref _remnantWarning, value);
    }

    public string? SplitCutNotice
    {
        get => _splitCutNotice;
        set => SetProperty(ref _splitCutNotice, value);
    }

    public bool IsConfirmed => _isConfirmed;

    public bool CanConfirm =>
        SelectedCoil is not null &&
        RequestedCutLength > 0 &&
        !IsLoading &&
        (SelectedCoil.ContinuousLength >= RequestedCutLength || AllowSplitCuts);

    public ICommand ConfirmCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand RefreshCommand { get; }

    public async Task LoadCoilsAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        ErrorMessage = null;
        RemnantWarning = null;
        SplitCutNotice = null;
        Coils.Clear();

        try
        {
            var response = await _workflowService.GetPackContainersAsync(
                _productId,
                conditionFilter: null,
                minContinuousLength: null,
                pageNumber: 1,
                pageSize: 50,
                cancellationToken: cancellationToken);

            // Prioritize continuous eligible coils (coils with enough continuous length shown first)
            var sorted = response.Items
                .OrderBy(c => c.ContinuousLength >= _requestedCutLength ? 0 : 1)
                .ThenBy(c => string.Equals(c.State, "Open", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(c => c.ContinuousLength)
                .ToList();

            foreach (var item in sorted)
            {
                Coils.Add(item);
            }

            // Auto-select the first coil with continuous length >= requested
            var eligibleContinuous = Coils.FirstOrDefault(c => c.ContinuousLength >= _requestedCutLength);
            if (eligibleContinuous is not null)
            {
                SelectedCoil = eligibleContinuous;
            }
            else if (Coils.Count > 0)
            {
                SelectedCoil = Coils[0];
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to load available coils: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void SelectCoil(PackContainerDto coil)
    {
        SelectedCoil = coil;
    }

    private void EvaluateSelection()
    {
        RemnantWarning = null;
        SplitCutNotice = null;

        if (SelectedCoil is null)
        {
            return;
        }

        // Check if cut requires split cuts
        if (SelectedCoil.ContinuousLength < _requestedCutLength)
        {
            if (!AllowSplitCuts)
            {
                SplitCutNotice = $"Selected coil has {SelectedCoil.ContinuousLength}{UnitOfMeasure} continuous length, which is less than requested {_requestedCutLength}{UnitOfMeasure}. Split cut checkbox confirmation is required.";
            }
            else
            {
                SplitCutNotice = $"Segmented split cut confirmed: multiple cuts will fulfill {_requestedCutLength}{UnitOfMeasure}.";
            }
        }

        // Check remnant warning: triggers when remaining length falls below 5m
        var remainingAfter = SelectedCoil.ContinuousLength - _requestedCutLength;
        if (remainingAfter > 0 && remainingAfter < 5m)
        {
            RemnantWarning = $"Warning: Cut leaves a short remnant of {remainingAfter:F2}{UnitOfMeasure} (< 5m). Remnant pricing or restock rules apply.";
        }
    }

    public void Confirm()
    {
        if (SelectedCoil is null)
        {
            ErrorMessage = "Please select a coil first.";
            return;
        }

        if (SelectedCoil.ContinuousLength < _requestedCutLength && !AllowSplitCuts)
        {
            ErrorMessage = "Split cut confirmation is required when requested cut exceeds continuous coil capacity.";
            return;
        }

        _isConfirmed = true;
        _onConfirmed?.Invoke(SelectedCoil, RequestedCutLength, AllowSplitCuts);
        _dialogService?.Close();
    }

    public void Cancel()
    {
        _dialogService?.Close();
    }
}
