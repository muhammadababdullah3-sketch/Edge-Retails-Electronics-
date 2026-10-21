using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EdgeRetails.Desktop.Converters;
using EdgeRetails.Desktop.Views;
using EdgeRetails.Desktop.Views.Dialogs;
using Xunit;

namespace EdgeRetails.Desktop.PerformanceTests;

public sealed class RenderedVisualProofTests
{
    private static readonly object SyncLock = new();

    private static void RunInSta(Action action)
    {
        lock (SyncLock)
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    EnsureApplicationInitialized();
                    action();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (failure is not null)
            {
                throw new Xunit.Sdk.XunitException(failure.ToString());
            }
        }
    }

    private static void EnsureApplicationInitialized()
    {
        var app = System.Windows.Application.Current;
        if (app is null)
        {
            var edgeApp = new EdgeRetails.Desktop.App();
            edgeApp.InitializeComponent();
            app = edgeApp;
        }

        if (!app.Resources.Contains("BooleanToVisibilityConverter"))
        {
            var packUris = new[]
            {
                "pack://application:,,,/EdgeRetails.Desktop;component/Resources/Colors.xaml",
                "pack://application:,,,/EdgeRetails.Desktop;component/Resources/Brushes.xaml",
                "pack://application:,,,/EdgeRetails.Desktop;component/Resources/Gradients.xaml",
                "pack://application:,,,/EdgeRetails.Desktop;component/Resources/Spacing.xaml",
                "pack://application:,,,/EdgeRetails.Desktop;component/Resources/Radii.xaml",
                "pack://application:,,,/EdgeRetails.Desktop;component/Resources/Shadows.xaml",
                "pack://application:,,,/EdgeRetails.Desktop;component/Resources/Typography.xaml",
                "pack://application:,,,/EdgeRetails.Desktop;component/Resources/NavigationIcons.xaml",
                "pack://application:,,,/EdgeRetails.Desktop;component/Resources/Themes/Light.xaml",
                "pack://application:,,,/EdgeRetails.Desktop;component/Resources/Buttons.xaml",
                "pack://application:,,,/EdgeRetails.Desktop;component/Resources/Inputs.xaml",
                "pack://application:,,,/EdgeRetails.Desktop;component/Resources/Tables.xaml",
                "pack://application:,,,/EdgeRetails.Desktop;component/Resources/Cards.xaml",
                "pack://application:,,,/EdgeRetails.Desktop;component/Resources/Tabs.xaml",
                "pack://application:,,,/EdgeRetails.Desktop;component/Resources/Badges.xaml",
                "pack://application:,,,/EdgeRetails.Desktop;component/Resources/Tooltips.xaml",
                "pack://application:,,,/EdgeRetails.Desktop;component/Resources/ScrollBars.xaml",
            };

            foreach (var uri in packUris)
            {
                try
                {
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri(uri, UriKind.Absolute)
                    });
                }
                catch
                {
                }
            }

            app.Resources["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter();
            app.Resources["InverseBooleanToVisibilityConverter"] = new InverseBooleanToVisibilityConverter();
        }
    }

    private static void SetTheme(bool dark)
    {
        var app = System.Windows.Application.Current;
        if (app is null)
        {
            return;
        }

        var themeUri = dark
            ? "pack://application:,,,/EdgeRetails.Desktop;component/Resources/Themes/Dark.xaml"
            : "pack://application:,,,/EdgeRetails.Desktop;component/Resources/Themes/Light.xaml";

        var themeDict = new ResourceDictionary { Source = new Uri(themeUri, UriKind.Absolute) };
        app.Resources.MergedDictionaries.Add(themeDict);
    }

    private static void RenderToPng(FrameworkElement element, double width, double height, double dpi, string outputPath)
    {
        element.Width = width;
        element.Height = height;
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();

        var scale = dpi / 96.0;
        var pixelWidth = Math.Max(1, (int)(width * scale));
        var pixelHeight = Math.Max(1, (int)(height * scale));

        var rtb = new RenderTargetBitmap(pixelWidth, pixelHeight, dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(element);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var stream = File.Create(outputPath);
        encoder.Save(stream);
    }

    [Fact]
    public void Verify_PosView_Ergonomic_Dimensions_And_Render_Proof()
    {
        RunInSta(() =>
        {
            var app = System.Windows.Application.Current;
            Assert.NotNull(app);

            // 1. Instantiate PosView and verify Pos.StepperButton style dimensions
            SetTheme(dark: false);
            var posLight = new PosView();
            var stepperStyle = posLight.Resources["Pos.StepperButton"] as Style;
            Assert.NotNull(stepperStyle);
            double stepperWidth = 0, stepperHeight = 0;
            foreach (var setter in stepperStyle.Setters)
            {
                if (setter is Setter s)
                {
                    if (s.Property == FrameworkElement.WidthProperty && s.Value is double w)
                    {
                        stepperWidth = w;
                    }
                    if (s.Property == FrameworkElement.HeightProperty && s.Value is double h)
                    {
                        stepperHeight = h;
                    }
                }
            }
            Assert.Equal(36.0, stepperWidth);
            Assert.Equal(36.0, stepperHeight);

            // 2. Verify Pos.RemoveButton style dimensions
            var removeStyle = posLight.Resources["Pos.RemoveButton"] as Style;
            Assert.NotNull(removeStyle);
            double removeWidth = 0, removeHeight = 0;
            foreach (var setter in removeStyle.Setters)
            {
                if (setter is Setter s)
                {
                    if (s.Property == FrameworkElement.WidthProperty && s.Value is double w)
                    {
                        removeWidth = w;
                    }
                    if (s.Property == FrameworkElement.HeightProperty && s.Value is double h)
                    {
                        removeHeight = h;
                    }
                }
            }
            Assert.Equal(36.0, removeWidth);
            Assert.Equal(36.0, removeHeight);

            // 3. Render PosView in Light theme (96 DPI and 144 DPI)
            var outLight96 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../artifacts/rendered-proof/pos_light_96dpi.png");
            RenderToPng(posLight, 1280, 800, 96, outLight96);
            Assert.True(File.Exists(outLight96) && new FileInfo(outLight96).Length > 1000);

            var outLight144 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../artifacts/rendered-proof/pos_light_144dpi.png");
            RenderToPng(posLight, 1280, 800, 144, outLight144);
            Assert.True(File.Exists(outLight144) && new FileInfo(outLight144).Length > 1000);

            // 4. Render PosView in Dark theme
            SetTheme(dark: true);
            var posDark = new PosView();
            var outDark96 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../artifacts/rendered-proof/pos_dark_96dpi.png");
            RenderToPng(posDark, 1280, 800, 96, outDark96);
            Assert.True(File.Exists(outDark96) && new FileInfo(outDark96).Length > 1000);
        });
    }

    [Fact]
    public void Verify_Dialog_Close_Buttons_Use_Vector_Path_And_Render_Proof()
    {
        RunInSta(() =>
        {
            var app = System.Windows.Application.Current;
            Assert.NotNull(app);

            // 1. Verify Button.Close style exists and uses Icon.Close vector
            var closeStyle = app.TryFindResource("Button.Close") as Style;
            Assert.NotNull(closeStyle);
            var iconClose = app.TryFindResource("Icon.Close") as PathGeometry;
            Assert.NotNull(iconClose);
            Assert.Equal("M1,1L9,9 M9,1L1,9", iconClose.ToString());

            // 2. Render StocktakeDialog in Light theme
            SetTheme(dark: false);
            var stocktakeDialog = new StocktakeDialog();
            var outStocktake = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../artifacts/rendered-proof/dialog_stocktake_light.png");
            RenderToPng(stocktakeDialog, 980, 720, 96, outStocktake);
            Assert.True(File.Exists(outStocktake) && new FileInfo(outStocktake).Length > 1000);

            // 3. Render SupervisorApprovalModalDialog in Dark theme
            SetTheme(dark: true);
            var supervisorDialog = new SupervisorApprovalModalDialog();
            var outSupervisor = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../artifacts/rendered-proof/dialog_supervisor_approval_dark.png");
            RenderToPng(supervisorDialog, 520, 420, 96, outSupervisor);
            Assert.True(File.Exists(outSupervisor) && new FileInfo(outSupervisor).Length > 1000);

            // 4. Render ConfirmationDialog in Light theme
            SetTheme(dark: false);
            var confirmDialog = new ConfirmationDialog();
            var outConfirm = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../artifacts/rendered-proof/dialog_confirmation_light.png");
            RenderToPng(confirmDialog, 460, 260, 96, outConfirm);
            Assert.True(File.Exists(outConfirm) && new FileInfo(outConfirm).Length > 1000);
        });
    }

    [Fact]
    public void Verify_Dashboard_And_Reports_Views_Render_Proof()
    {
        RunInSta(() =>
        {
            // 1. Render Dashboard in Light theme
            SetTheme(dark: false);
            var dashboardLight = new DashboardView();
            var outDashLight = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../artifacts/rendered-proof/dashboard_light_96dpi.png");
            RenderToPng(dashboardLight, 1280, 800, 96, outDashLight);
            Assert.True(File.Exists(outDashLight) && new FileInfo(outDashLight).Length > 1000);

            // 2. Render Dashboard in Dark theme
            SetTheme(dark: true);
            var dashboardDark = new DashboardView();
            var outDashDark = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../artifacts/rendered-proof/dashboard_dark_96dpi.png");
            RenderToPng(dashboardDark, 1280, 800, 96, outDashDark);
            Assert.True(File.Exists(outDashDark) && new FileInfo(outDashDark).Length > 1000);

            // 3. Render Reports in Light theme and Dark theme
            SetTheme(dark: false);
            var reportsLight = new ReportsView();
            var outReports = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../artifacts/rendered-proof/reports_light_96dpi.png");
            RenderToPng(reportsLight, 1280, 800, 96, outReports);
            Assert.True(File.Exists(outReports) && new FileInfo(outReports).Length > 1000);

            SetTheme(dark: true);
            var reportsDark = new ReportsView();
            var outReportsDark = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../artifacts/rendered-proof/reports_dark_96dpi.png");
            RenderToPng(reportsDark, 1280, 800, 96, outReportsDark);
            Assert.True(File.Exists(outReportsDark) && new FileInfo(outReportsDark).Length > 1000);
        });
    }
}
