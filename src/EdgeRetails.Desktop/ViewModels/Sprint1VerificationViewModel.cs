using System.Collections.ObjectModel;
using System.Windows.Input;
using EdgeRetails.Desktop.Controls;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class Sprint1VerificationViewModel : ViewModelBase
{
    private readonly IThemeService _themeService;
    private readonly IDialogService _dialogService;
    private readonly IDrawerService _drawerService;
    private readonly IToastService _toastService;

    private string _searchText = string.Empty;
    private string? _selectedCategory;

    public Sprint1VerificationViewModel(
        IThemeService themeService,
        IDialogService dialogService,
        IDrawerService drawerService,
        IToastService toastService)
    {
        ArgumentNullException.ThrowIfNull(themeService);
        ArgumentNullException.ThrowIfNull(dialogService);
        ArgumentNullException.ThrowIfNull(drawerService);
        ArgumentNullException.ThrowIfNull(toastService);

        _themeService = themeService;
        _dialogService = dialogService;
        _drawerService = drawerService;
        _toastService = toastService;

        Categories = new ReadOnlyCollection<string>(
            new List<string> { "All", "Lighting", "Switches", "Cables" });

        SelectedCategory = Categories[0];

        SampleRows = new ReadOnlyCollection<Sprint1SampleRow>(
            new List<Sprint1SampleRow>
            {
                new("LED Bulb 12W", "Lighting", 48, "Rs. 650", "In Stock", BadgeTone.Success),
                new("2-Gang Switch", "Switches", 14, "Rs. 420", "Low Stock", BadgeTone.Warning),
                new("Copper Cable 7/29", "Cables", 0, "Rs. 8,900", "Out of Stock", BadgeTone.Danger),
                new("Smart Breaker 63A", "Switches", 22, "Rs. 3,250", "Active", BadgeTone.Info),
            });

        ApplyLightThemeCommand = new RelayCommand(() => _themeService.ApplyTheme(AppTheme.Light));
        ApplyDarkThemeCommand = new RelayCommand(() => _themeService.ApplyTheme(AppTheme.Dark));

        ShowModalCommand = new RelayCommand(
            () => _dialogService.Show(
                new Sprint1OverlayPreviewViewModel(
                    "Sprint 1 Modal",
                    "This modal confirms that the shared modal host can render content above the application shell.",
                    _dialogService.Close)));

        ShowDrawerCommand = new RelayCommand(
            () => _drawerService.Show(
                new Sprint1OverlayPreviewViewModel(
                    "Sprint 1 Drawer",
                    "This drawer confirms that the shared right-side drawer host can render content correctly.",
                    _drawerService.Close)));

        ShowToastCommand = new RelayCommand(
            () => _toastService.Show(
                "Sprint 1 toast is working.",
                ToastTone.Success));
    }

    public IReadOnlyList<string> Categories { get; }

    public IReadOnlyList<Sprint1SampleRow> SampleRows { get; }

    public ICommand ApplyLightThemeCommand { get; }

    public ICommand ApplyDarkThemeCommand { get; }

    public ICommand ShowModalCommand { get; }

    public ICommand ShowDrawerCommand { get; }

    public ICommand ShowToastCommand { get; }

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }

    public string? SelectedCategory
    {
        get => _selectedCategory;
        set => SetProperty(ref _selectedCategory, value);
    }
}

public sealed record Sprint1SampleRow(
    string Product,
    string Category,
    int Stock,
    string Price,
    string Status,
    BadgeTone StatusTone);
