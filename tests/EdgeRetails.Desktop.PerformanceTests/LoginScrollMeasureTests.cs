using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EdgeRetails.Desktop.ViewModels;
using EdgeRetails.Desktop.Views;
using Xunit;
using Xunit.Abstractions;

namespace EdgeRetails.Desktop.PerformanceTests;

/// <summary>
/// Measurement harness for the LOGIN-SCROLL account viewport. Diagnostic scaffolding used to derive
/// the real fixed chrome height and to confirm the outer login scroll never engages.
/// </summary>
public sealed class LoginScrollMeasureTests(ITestOutputHelper output)
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
        EnsureApplicationInitializedForTests();
    }

    /// <summary>
    /// Merges the theme resource dictionaries into the WPF application so a view can be laid out
    /// outside a real window. Exposed for reuse by <c>LoginScrollLayoutTests</c>; both suites must
    /// measure under the same resources or their numbers are not comparable.
    /// </summary>
    internal static void EnsureApplicationInitializedForTests()
    {
        var app = System.Windows.Application.Current;
        if (app is null)
        {
            var edgeApp = new EdgeRetails.Desktop.App();
            edgeApp.InitializeComponent();
            app = edgeApp;
        }

        foreach (var name in new[]
                 {
                     "Colors", "Brushes", "Gradients", "Spacing", "Radii", "Shadows",
                     "Typography", "NavigationIcons", "Buttons", "Inputs", "Tables", "Cards",
                     "Tabs", "Badges", "Tooltips", "ScrollBars",
                 })
        {
            try
            {
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri(
                        $"pack://application:,,,/EdgeRetails.Desktop;component/Resources/{name}.xaml",
                        UriKind.Absolute)
                });
            }
            catch
            {
            }
        }

        try
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(
                    "pack://application:,,,/EdgeRetails.Desktop;component/Resources/Themes/Light.xaml",
                    UriKind.Absolute)
            });
        }
        catch
        {
        }
    }

    [Fact]
    public void Measure_Outer_Scroll_And_Keypad_Visibility()
    {
        RunInSta(() =>
        {
            foreach (var windowHeight in new[] { 600.0, 640.0, 700.0, 768.0, 900.0, 1080.0 })
            {
                foreach (var count in new[] { 1, 12, 40 })
                {
                    var view = new LoginView
                    {
                        DataContext = new FixedAccountsViewModel(count),
                        Width = 1024,
                        Height = windowHeight,
                    };

                    view.Measure(new Size(1024, windowHeight));
                    view.Arrange(new Rect(0, 0, 1024, windowHeight));
                    view.UpdateLayout();

                    var scrollers = FindAll<ScrollViewer>(view).ToList();
                    var outer = scrollers.FirstOrDefault(s => s.Width is not 342);
                    var accounts = scrollers.FirstOrDefault(s => s.Width is 342);
                    var items = FindAll<ItemsControl>(view).FirstOrDefault();
                    var keypad = FindAll<Grid>(view).FirstOrDefault(g => g.Width is 342 && g.RowDefinitions.Count == 7);
                    var signIn = FindAll<Button>(view).FirstOrDefault(b => b.Width is 342 && b.Height is 42);

                    output.WriteLine(
                        $"win={windowHeight} n={count} " +
                        $"OUTERscrollable={outer?.ScrollableHeight:F1} OUTERviewport={outer?.ViewportHeight:F1} " +
                        $"ACCviewport={accounts?.ActualHeight:F1} ACCextent={accounts?.ExtentHeight:F1} " +
                        $"ACCscrollable={accounts?.ScrollableHeight:F1} " +
                        $"keypadBottom={(keypad is null ? "n/a" : Bottom(keypad, view).ToString("F1"))} " +
                        $"signInBottom={(signIn is null ? "n/a" : Bottom(signIn, view).ToString("F1"))} " +
                        $"items={items?.Items.Count} viewH={view.ActualHeight:F1}");
                }
            }
        });
    }

    private static double Bottom(FrameworkElement element, Visual root)
    {
        var bounds = element.TransformToAncestor(root)
            .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        return bounds.Bottom;
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindAll<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private sealed class FixedAccountsViewModel(int count) : ViewModelBase
    {
        public ObservableCollection<LoginAccountItemViewModel> Accounts { get; } =
            new(Enumerable.Range(1, count).Select(i => new LoginAccountItemViewModel(
                i.ToString(), $"Synthetic User {i}", "Cashier", $"U{i}", $"U{i}", true, i == 1)));
    }
}
