using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;

namespace EdgeRetails.Desktop.PerformanceTests;

// NEW_COVERAGE: shared resource contracts and offscreen WPF layout. No live application,
// credentials, operational database, service, or existing test concurrency is changed.
public sealed class MasterSharedInputTemplateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmbeddedInput_DisabledAndReadonlyStates_RetainGeometryAndPlaceholder(bool readOnly)
    {
        OnSta(() =>
        {
            var resources = RuntimeResources();
            var input = new TextBox { Resources = resources, Style = (Style)resources["Input.TextBox.Embedded"], Text = "Read value" };
            Layout(input, 300, 36);
            var host = Assert.IsType<ScrollViewer>(input.Template.FindName("PART_ContentHost", input));
            var initialHeight = host.ActualHeight;
            input.IsReadOnly = readOnly;
            input.IsEnabled = readOnly;
            Layout(input, 300, 36);
            var expected = Assert.IsType<SolidColorBrush>(resources[readOnly ? "Brush.Text.Subtle" : "Brush.Disabled.Text"]);
            Assert.Equal(expected.Color, Assert.IsType<SolidColorBrush>(input.Foreground).Color);
            Assert.Equal(readOnly ? 1d : 0.75d, input.Opacity);
            Assert.Equal(initialHeight, host.ActualHeight);
            Assert.Equal("Read value", input.Text);
            Assert.Equal(readOnly, input.IsReadOnly);
            var placeholder = new TextBox { Resources = resources, Style = (Style)resources["Input.TextBox.Placeholder"], Text = "Placeholder" };
            Layout(placeholder, 300, 36);
            Assert.False(placeholder.IsHitTestVisible);
            Assert.True(placeholder.IsReadOnly);
            Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(placeholder.Background).Color);
            Assert.Equal(1d, placeholder.Opacity);
        });
    }

    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData("Input.TextBox")]
    [InlineData("Input.PasswordBox")]
    public void ContentHost_RespectsVerticalContentAlignment(string key)
    {
        var style = Style("Inputs.xaml", key);
        var host = style.Descendants(Presentation + "ScrollViewer")
            .Single(e => (string?)e.Attribute(Xaml + "Name") == "PART_ContentHost");
        Assert.Equal("{TemplateBinding VerticalContentAlignment}", (string?)host.Attribute("VerticalAlignment"));
    }

    [Theory]
    [InlineData(false, 28d)]
    [InlineData(false, 36d)]
    [InlineData(false, 48d)]
    [InlineData(true, 36d)]
    [InlineData(true, 48d)]
    public void SingleLineContentHost_IsCenteredAtCompactStandardAndLargeSizes(bool password, double height)
    {
        OnSta(() =>
        {
            var resources = RuntimeResources();
            Control input = password ? new PasswordBox { Password = "1234" } : new TextBox { Text = "Catalog / PK-000001" };
            input.Resources = resources;
            input.Style = (Style)resources[password ? "Input.PasswordBox" : "Input.TextBox"];
            input.Height = height;
            Layout(input, 300, height);
            var host = Assert.IsType<ScrollViewer>(input.Template.FindName("PART_ContentHost", input));
            var offset = host.TranslatePoint(new Point(), input).Y;
            Assert.InRange(Math.Abs(offset + host.ActualHeight / 2 - height / 2), 0, 0.75);
            Assert.True(host.ActualHeight < height - 4, "Single-line content must not stretch to the top of the input.");
        });
    }

    [Fact]
    public void MultilineInput_CanRetainAnExplicitTopAlignment()
    {
        OnSta(() =>
        {
            var resources = RuntimeResources();
            var input = new TextBox
            {
                Resources = resources,
                Style = (Style)resources["Input.TextBox"],
                Text = "First line\nSecond line",
                AcceptsReturn = true,
                VerticalContentAlignment = VerticalAlignment.Top,
                Height = 96
            };
            Layout(input, 300, 96);
            var host = Assert.IsType<ScrollViewer>(input.Template.FindName("PART_ContentHost", input));
            Assert.Equal(VerticalAlignment.Top, host.VerticalAlignment);
            Assert.InRange(host.TranslatePoint(new Point(), input).Y, 0, 2);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SearchPlaceholderAndTypedText_HaveTheSameVerticalBaseline(bool compact)
    {
        OnSta(() =>
        {
            // Instantiate the actual SearchBox XAML as an offscreen UserControl to keep
            // application-global resources and the installed authenticated UI untouched.
            var source = XDocument.Load(Path.Combine(RepositoryRoot(), "src", "EdgeRetails.Desktop", "Controls", "SearchBox.xaml")).Root!;
            source.Attribute(Xaml + "Class")!.Remove();
            var root = new XElement(Presentation + "Grid",
                new XAttribute(XNamespace.Xmlns + "x", Xaml),
                new XElement(Presentation + "Grid.Resources", ResourceSource()), source);
            var grid = (Grid)XamlReader.Parse(root.ToString());
            var search = Assert.IsType<UserControl>(grid.Children[0]);
            // Source binding expects IsCompact on its UserControl; set explicit height
            // here and verify both authoritative height tokens without changing source.
            var height = (double)grid.Resources[compact ? "Dimension.Search.Compact" : "Dimension.Search.Large"];
            search.Height = height;
            var input = Assert.IsType<TextBox>(search.FindName("Input"));
            Layout(grid, 380, height);
            var placeholder = Assert.IsType<TextBox>(search.FindName("PlaceholderInput"));
            placeholder.Text = "Find exact unit";
            placeholder.Visibility = Visibility.Visible;
            input.Text = "Find exact unit";
            Layout(grid, 380, height);
            var host = Assert.IsType<ScrollViewer>(input.Template.FindName("PART_ContentHost", input));
            var typedCenter = host.TranslatePoint(new Point(), search).Y + host.ActualHeight / 2;
            var placeholderHost = Assert.IsType<ScrollViewer>(placeholder.Template.FindName("PART_ContentHost", placeholder));
            var placeholderCenter = placeholderHost.TranslatePoint(new Point(), search).Y + placeholderHost.ActualHeight / 2;
            Assert.InRange(Math.Abs(typedCenter - placeholderCenter), 0, 0.75);
            Assert.InRange(Math.Abs(typedCenter - search.ActualHeight / 2), 0, 0.75);
            Assert.True(host.ActualHeight < search.ActualHeight - 4, "Typed search text must remain vertically centered within its outer hit target.");
        });
    }

    [Theory]
    [InlineData("Dimension.Control.Standard")]
    [InlineData("Dimension.Control.Compact")]
    [InlineData("Dimension.Button.CompactAction")]
    [InlineData("Dimension.Button.Standard")]
    [InlineData("Dimension.Button.PrimaryAction")]
    [InlineData("Dimension.Button.PrimaryPurchaseAction")]
    [InlineData("Dimension.Search.Large")]
    [InlineData("Dimension.Search.Compact")]
    [InlineData("Dimension.Table.Header")]
    [InlineData("Dimension.Table.Row")]
    public void SharedControlHeightTokens_UseAnIntegerFourPointRhythm(string key)
    {
        var value = double.Parse(Resource("Spacing.xaml", key).Value, CultureInfo.InvariantCulture);
        Assert.True(value >= 28, $"{key} must retain a usable control hit target.");
        Assert.Equal(0d, value % 4);
    }

    [Fact]
    public void PrimaryActions_RetainLargerHitTargets()
    {
        double Token(string key) => double.Parse(Resource("Spacing.xaml", key).Value, CultureInfo.InvariantCulture);
        Assert.True(Token("Dimension.Button.PrimaryAction") > Token("Dimension.Button.Standard"));
        Assert.True(Token("Dimension.Button.PrimaryPurchaseAction") >= Token("Dimension.Button.PrimaryAction"));
    }

    [Fact]
    public void SharedInputPadding_UsesIntegerFourPointGeometry()
    {
        var parts = Resource("Spacing.xaml", "Padding.Input.Standard").Value.Split(',');
        foreach (var part in parts)
        {
            Assert.Equal(0d, double.Parse(part, CultureInfo.InvariantCulture) % 4);
        }
    }

    [Fact]
    public void DropdownItems_UseSharedControlHeightAndIntegerPadding()
    {
        var setters = Style("Inputs.xaml", "Input.ComboBoxItem").Elements(Presentation + "Setter").ToArray();
        Assert.Contains(setters, e => (string?)e.Attribute("Property") == "MinHeight" &&
            (string?)e.Attribute("Value") == "{StaticResource Dimension.Control.Standard}");
        var padding = setters.Single(e => (string?)e.Attribute("Property") == "Padding").Attribute("Value")!.Value;
        Assert.Equal("{StaticResource Padding.Input.Standard}", padding);
        foreach (var part in Resource("Spacing.xaml", "Padding.Input.Standard").Value.Split(','))
        {
            Assert.Equal(0d, double.Parse(part, CultureInfo.InvariantCulture) % 4);
        }
    }

    [Theory]
    [InlineData("Table.Cell")]
    [InlineData("Table.Row")]
    public void DataGridCellsAndRows_HaveVisibleBrandedKeyboardFocus(string key)
    {
        var style = Style("Tables.xaml", key);
        var focus = style.Descendants(Presentation + "Trigger")
            .SingleOrDefault(e => (string?)e.Attribute("Property") == "IsKeyboardFocusWithin" && (string?)e.Attribute("Value") == "True");
        Assert.NotNull(focus);
        var thickness = style.Elements(Presentation + "Setter").Single(x => (string?)x.Attribute("Property") == "BorderThickness");
        Assert.Equal(key == "Table.Cell" ? "0" : "0,0,0,1", (string?)thickness.Attribute("Value"));
        Assert.DoesNotContain(focus.Elements(Presentation + "Setter"), x => (string?)x.Attribute("Property") == "BorderThickness");
        if (key == "Table.Cell")
        {
            var indicator = style.Descendants(Presentation + "Border").Single(x => (string?)x.Attribute(Xaml + "Name") == "CellFocusIndicator");
            Assert.Equal("1.5", (string?)indicator.Attribute("BorderThickness"));
            Assert.Equal("4", (string?)indicator.Attribute("CornerRadius"));
            Assert.Equal("2", (string?)indicator.Attribute("Margin"));
            Assert.Contains(focus.Elements(Presentation + "Setter"), x => (string?)x.Attribute("TargetName") == "CellFocusIndicator" &&
                (string?)x.Attribute("Property") == "BorderBrush" && (string?)x.Attribute("Value") == "{DynamicResource Brush.Focus.Ring}");
        }
        else
        {
            Assert.Contains(focus.Elements(Presentation + "Setter"), x => (string?)x.Attribute("Property") == "BorderBrush" &&
                (string?)x.Attribute("Value") == "{DynamicResource Brush.Selection.Border}");
        }
    }

    [Theory]
    [InlineData("Inputs.xaml", "Input.TextBox")]
    [InlineData("Inputs.xaml", "Input.PasswordBox")]
    [InlineData("Inputs.xaml", "Input.ComboBox")]
    [InlineData("Buttons.xaml", "Button.Base")]
    public void ExistingInputAndButtonBrandedFocus_RemainsVisible(string file, string key)
    {
        Assert.Contains(Style(file, key).Descendants(Presentation + "Trigger"), trigger =>
            ((string?)trigger.Attribute("Property") is "IsKeyboardFocused" or "IsKeyboardFocusWithin") &&
            (string?)trigger.Attribute("Value") == "True" &&
            trigger.Elements(Presentation + "Setter").Any(e =>
                (string?)e.Attribute("Property") == "BorderBrush" &&
                (string?)e.Attribute("Value") == "{DynamicResource Brush.Focus.Ring}"));
    }

    [Theory]
    [InlineData("Input.DatePicker")]
    [InlineData("Input.ComboBoxItem")]
    public void RemainingSharedInputs_HaveBrandedKeyboardFocus(string key)
    {
        var focus = Style("Inputs.xaml", key).Descendants(Presentation + "Trigger")
            .SingleOrDefault(e => (string?)e.Attribute("Property") == "IsKeyboardFocusWithin" && (string?)e.Attribute("Value") == "True");
        Assert.NotNull(focus);
        Assert.Contains(focus.Elements(Presentation + "Setter"), e =>
            (string?)e.Attribute("Property") == "BorderBrush" && (string?)e.Attribute("Value") == "{DynamicResource Brush.Focus.Ring}");
    }

    private static XElement Style(string file, string key) => Resource(file, key);
    private static XElement Resource(string file, string key) => XDocument.Load(ResourcePath(file)).Root!
        .Elements().Single(e => (string?)e.Attribute(Xaml + "Key") == key);

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EdgeRetails.sln")))
            {
                return directory.FullName;
            }
        }
        throw new InvalidOperationException("The test requires the repository resources beside the built test project.");
    }

    private static string ResourcePath(string file) => Path.Combine(RepositoryRoot(), "src", "EdgeRetails.Desktop", "Resources", file);

    private static XElement ResourceSource()
    {
        var root = new XElement(Presentation + "ResourceDictionary",
            new XAttribute(XNamespace.Xmlns + "x", Xaml),
            new XAttribute(XNamespace.Xmlns + "sys", "clr-namespace:System;assembly=System.Runtime"));
        var merged = new XElement(Presentation + "ResourceDictionary.MergedDictionaries");
        foreach (var file in new[] { "Colors.xaml", "Brushes.xaml", "Gradients.xaml", "Spacing.xaml", "Radii.xaml", "Shadows.xaml", "Typography.xaml", "NavigationIcons.xaml", "Brand.xaml", "Themes/Light.xaml", "Buttons.xaml", "Inputs.xaml", "Tables.xaml" })
        {
            var dictionary = new XElement(XDocument.Load(ResourcePath(file)).Root!);
            foreach (var declaration in dictionary.Attributes().Where(x => x.IsNamespaceDeclaration &&
                         x.Value.StartsWith("clr-namespace:EdgeRetails.Desktop", StringComparison.Ordinal) &&
                         !x.Value.Contains(";assembly=", StringComparison.Ordinal)))
            {
                declaration.Value += ";assembly=EdgeRetails.Desktop";
            }
            merged.Add(dictionary);
        }
        root.Add(merged);
        return root;
    }

    private static ResourceDictionary RuntimeResources() => (ResourceDictionary)XamlReader.Parse(ResourceSource().ToString());

    private static void Layout(FrameworkElement control, double width, double height)
    {
        control.Measure(new Size(width, height));
        control.Arrange(new Rect(0, 0, width, height));
        control.UpdateLayout();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T result)
            {
                yield return result;
            }
            foreach (var descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Offscreen WPF layout did not complete in 30 seconds.");
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
