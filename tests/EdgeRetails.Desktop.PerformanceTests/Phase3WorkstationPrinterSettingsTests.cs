using System.Collections.ObjectModel;
using System.ComponentModel;
using EdgeRetails.Application.Production.Printing;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Desktop.ViewModels;

namespace EdgeRetails.Desktop.PerformanceTests;

public sealed class Phase3WorkstationPrinterSettingsTests
{
    [Fact]
    public async Task SettingsEnumeratesWindowsPrinterAdapterAndPersistsSelectedReceiptMedia()
    {
        var printerSettings = new FakeWorkstationPrinterSettings(["Counter Receipt", "Office Laser"]);
        using var viewModel = new SettingsViewModel(
            new TestThemeService(),
            new TestDialogService(),
            new TestToastService(),
            workstationPrinterSettings: printerSettings);

        await WaitUntilAsync(() => viewModel.Printers.Count == 2 && viewModel.Printer == "Counter Receipt");

        Assert.Equal(new[] { "Counter Receipt", "Office Laser" }, viewModel.Printers);
        Assert.Equal(new[] { "80mm", "58mm" }, viewModel.PaperSizes);

        viewModel.PaperSize = "58mm";
        viewModel.SaveReceiptCommand.Execute(null);
        await WaitUntilAsync(() => printerSettings.SavedProfile is not null);

        Assert.Equal("Counter Receipt", printerSettings.SavedProfile!.PrinterName);
        Assert.Equal(PaperKind.Thermal58Mm, printerSettings.SavedProfile.Paper);

        using var restartedViewModel = new SettingsViewModel(
            new TestThemeService(),
            new TestDialogService(),
            new TestToastService(),
            workstationPrinterSettings: printerSettings);
        await WaitUntilAsync(() => restartedViewModel.Printer == "Counter Receipt" && restartedViewModel.PaperSize == "58mm");
        Assert.Equal("58mm", restartedViewModel.PaperSize);
    }

    [Fact]
    public async Task SettingsSurfacesUnsupportedMediaAndDoesNotPersistInvalidProfile()
    {
        var printerSettings = new FakeWorkstationPrinterSettings(["Counter Receipt"])
        {
            ValidationResult = new PrinterProfileValidationResult(
                false,
                "print.media_not_supported",
                "The printer does not support 58mm receipt media.")
        };
        using var viewModel = new SettingsViewModel(
            new TestThemeService(),
            new TestDialogService(),
            new TestToastService(),
            workstationPrinterSettings: printerSettings);

        await WaitUntilAsync(() => viewModel.Printer == "Counter Receipt");
        viewModel.PaperSize = "58mm";
        viewModel.SaveReceiptCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.HasPrinterSettingsError);

        Assert.Equal("The printer does not support 58mm receipt media.", viewModel.PrinterSettingsError);
        Assert.Null(printerSettings.SavedProfile);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (!predicate() && DateTime.UtcNow < timeout)
        {
            await Task.Delay(10);
        }

        Assert.True(predicate(), "Workstation printer settings did not reach the expected state before timeout.");
    }

    private sealed class FakeWorkstationPrinterSettings(IReadOnlyList<string> printerNames) : IWorkstationPrinterSettings
    {
        public PrinterProfile? SavedProfile { get; private set; }
        public PrinterProfileValidationResult ValidationResult { get; init; } = PrinterProfileValidationResult.Valid;

        public Task<IReadOnlyList<string>> GetInstalledPrinterNamesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(printerNames);

        public Task<PrinterProfile?> GetReceiptProfileAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(SavedProfile);

        public Task<PrinterProfileValidationResult> ValidateReceiptProfileAsync(
            PrinterProfile profile,
            CancellationToken cancellationToken = default)
            => Task.FromResult(ValidationResult);

        public Task SaveReceiptProfileAsync(PrinterProfile profile, CancellationToken cancellationToken = default)
        {
            SavedProfile = profile;
            return Task.CompletedTask;
        }
    }

    private sealed class TestThemeService : IThemeService
    {
        public event EventHandler<AppTheme>? ThemeChanged;
        public AppTheme CurrentTheme => AppTheme.System;
        public AppTheme ResolvedTheme => AppTheme.Light;
        public void ApplyTheme(AppTheme theme) => ThemeChanged?.Invoke(this, theme);
    }

    private sealed class TestDialogService : IDialogService
    {
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public bool IsOpen => false;
        public object? Content => null;
        public void Show(object content) { }
        public void Close() { }
    }

    private sealed class TestToastService : IToastService
    {
        private readonly ObservableCollection<ToastMessage> _messages = [];
        public ReadOnlyObservableCollection<ToastMessage> Messages { get; }
        public TestToastService() => Messages = new ReadOnlyObservableCollection<ToastMessage>(_messages);
        public void Show(string message, ToastTone tone = ToastTone.Neutral, TimeSpan? duration = null) { }
    }
}
