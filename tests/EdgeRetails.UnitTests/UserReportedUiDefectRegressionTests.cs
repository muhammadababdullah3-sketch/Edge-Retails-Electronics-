using System.IO;
using System.Xml.Linq;
using Xunit;

namespace EdgeRetails.UnitTests;

public sealed class UserReportedUiDefectRegressionTests
{
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void PosSearch_UsesSameRendererAndDoesNotOverrideFocusStroke()
    {
        var doc = Load("Views/PosView.xaml");
        var input = doc.Descendants(P + "TextBox").Single(e => (string?)e.Attribute(X + "Name") == "SearchInput");
        var watermark = doc.Descendants(P + "TextBox").Single(e => (string?)e.Attribute(X + "Name") == "SearchPlaceholder");
        Assert.Equal("{StaticResource Input.TextBox.Embedded}", (string?)input.Attribute("Style"));
        Assert.Same(input.Parent, watermark.Parent);
        Assert.Equal((string?)input.Attribute("FontFamily"), (string?)watermark.Attribute("FontFamily"));
        Assert.Equal((string?)input.Attribute("FontSize"), (string?)watermark.Attribute("FontSize"));
        Assert.Equal("{StaticResource Input.TextBox.Placeholder}", (string?)watermark.Descendants(P + "Style").Single().Attribute("BasedOn"));
        var host = input.Ancestors(P + "Border").First();
        Assert.Null(host.Attribute("BorderBrush"));
        Assert.Contains(host.Descendants(P + "DataTrigger"), e => ((string?)e.Attribute("Binding"))?.Contains("IsKeyboardFocused") == true);
    }

    [Fact]
    public void CustomerRows_CannotInheritSingleLineButtonHeight()
    {
        var doc = Load("Views/Dialogs/SaleCustomerPickerDialog.xaml");
        var rows = doc.Descendants(P + "Button").Where(e => ((string?)e.Attribute("Command"))?.Contains("UseWalkInCommand") == true || ((string?)e.Attribute("Command"))?.Contains("SelectCustomerCommand") == true).ToArray();
        Assert.Equal(2, rows.Length);
        Assert.All(rows, row => { Assert.Equal("Auto", (string?)row.Attribute("Height")); Assert.True(double.Parse(row.Attribute("MinHeight")!.Value, System.Globalization.CultureInfo.InvariantCulture) >= 64); });
    }

    [Fact]
    public void PeriodPills_DoNotLetTemplateOrDerivedSelectionOverrideInteraction()
    {
        var doc = Load("Views/SalesHistoryView.xaml");
        var style = doc.Descendants(P + "Style").Single(e => (string?)e.Attribute(X + "Key") == "Button.FilterPill");
        Assert.Empty(style.Descendants(P + "ControlTemplate.Triggers"));
        var triggers = style.Element(P + "Style.Triggers")!.Elements().ToArray();
        var selectedIndex = Array.FindIndex(triggers, e => ((string?)e.Attribute("Binding"))?.Contains("Binding Tag") == true);
        var pressedIndex = Array.FindIndex(triggers, e => (string?)e.Attribute("Property") == "IsPressed");
        Assert.True(selectedIndex >= 0);
        Assert.True(pressedIndex > selectedIndex);
        foreach (var label in new[] { "Today", "Yesterday", "This Week", "This Month" })
        {
            var button = doc.Descendants(P + "Button").Single(e => (string?)e.Attribute("Content") == label);
            Assert.NotNull(button.Attribute("Tag"));
            Assert.Empty(button.Descendants(P + "DataTrigger"));
        }
    }

    private static XDocument Load(string relative)
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "EdgeRetails.sln"))) { folder = folder.Parent; }
        return XDocument.Load(Path.Combine(folder!.FullName, "src", "EdgeRetails.Desktop", relative));
    }
}
