using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using EdgeRetails.Desktop.Converters;
using EdgeRetails.Desktop.Views;

namespace EdgeRetails.Desktop.PerformanceTests;

public sealed class Phase5WpfPerformanceTests
{
    [Fact]
    public void ProductionLargeDataViews_EnableRecyclingVirtualizationAtRuntime()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = System.Windows.Application.Current ?? new System.Windows.Application();

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

                AssertVirtualized(new ProductManagementView());
                AssertVirtualized(new SalesHistoryView());
                AssertVirtualized(new ThakaProjectsView());
                AssertVirtualized(new PurchaseHistoryView());
                AssertVirtualized(new InventoryView());
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

    private static void AssertVirtualized(FrameworkElement root)
    {
        var grids = FindDescendants<DataGrid>(root).Distinct().ToArray();
        Assert.NotEmpty(grids);

        foreach (var grid in grids)
        {
            Assert.True(grid.EnableRowVirtualization, $"{grid.Name} EnableRowVirtualization failed.");
            Assert.True(grid.EnableColumnVirtualization, $"{grid.Name} EnableColumnVirtualization failed.");
            Assert.True(VirtualizingPanel.GetIsVirtualizing(grid), $"{grid.Name} IsVirtualizing failed.");
            Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(grid));
            Assert.True(ScrollViewer.GetCanContentScroll(grid), $"{grid.Name} CanContentScroll failed.");
            Assert.True(grid.ItemsSource is null || grid.ItemsSource is IList || grid.ItemsSource is IEnumerable);
        }
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        if (root is T matchSelf)
        {
            yield return matchSelf;
        }

        if (root is Visual or System.Windows.Media.Media3D.Visual3D)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                foreach (var descendant in FindDescendants<T>(child))
                {
                    yield return descendant;
                }
            }
        }

        foreach (var logicalChild in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            foreach (var descendant in FindDescendants<T>(logicalChild))
            {
                yield return descendant;
            }
        }
    }
}
