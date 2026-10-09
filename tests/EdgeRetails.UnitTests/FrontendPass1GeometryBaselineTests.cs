using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace EdgeRetails.UnitTests;

public sealed class FrontendPass1GeometryBaselineTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void SpacingTokens_DefineHarmonizedGeometryBaseline()
    {
        var doc = LoadDesktopXaml("Resources", "Spacing.xaml");

        var standardHeight = doc.Descendants()
            .FirstOrDefault(e => (string?)e.Attribute(Xaml + "Key") == "Dimension.Control.Standard");
        Assert.NotNull(standardHeight);
        Assert.Equal("36", standardHeight.Value);

        var compactHeight = doc.Descendants()
            .FirstOrDefault(e => (string?)e.Attribute(Xaml + "Key") == "Dimension.Control.Compact");
        Assert.NotNull(compactHeight);
        Assert.Equal("32", compactHeight.Value);

        var compactAction = doc.Descendants()
            .FirstOrDefault(e => (string?)e.Attribute(Xaml + "Key") == "Dimension.Button.CompactAction");
        Assert.NotNull(compactAction);
        Assert.Equal("32", compactAction.Value);

        var standardPadding = doc.Descendants()
            .FirstOrDefault(e => (string?)e.Attribute(Xaml + "Key") == "Padding.Input.Standard");
        Assert.NotNull(standardPadding);
        Assert.Equal("12,0", standardPadding.Value);

        var compactPadding = doc.Descendants()
            .FirstOrDefault(e => (string?)e.Attribute(Xaml + "Key") == "Padding.Input.Compact");
        Assert.NotNull(compactPadding);
        Assert.Equal("8,0", compactPadding.Value);
    }

    [Fact]
    public void Inputs_DefineStandardAndCompactVisualFamilyParity()
    {
        var doc = LoadDesktopXaml("Resources", "Inputs.xaml");

        var styles = doc.Descendants(Presentation + "Style")
            .Where(e => e.Attribute(Xaml + "Key") != null)
            .ToDictionary(e => (string)e.Attribute(Xaml + "Key")!);

        // TextBox
        Assert.True(styles.ContainsKey("Input.TextBox"));
        var tb = styles["Input.TextBox"];
        Assert.Equal("{StaticResource Dimension.Control.Standard}", GetSetterValue(tb, "Height"));
        Assert.Equal("{StaticResource Padding.Input.Standard}", GetSetterValue(tb, "Padding"));
        Assert.Equal("14", GetSetterValue(tb, "FontSize"));
        Assert.Equal("Center", GetSetterValue(tb, "VerticalContentAlignment"));
        Assert.Equal("True", GetSetterValue(tb, "SnapsToDevicePixels"));

        // TextBox Compact
        Assert.True(styles.ContainsKey("Input.TextBox.Compact"));
        var tbc = styles["Input.TextBox.Compact"];
        Assert.Equal("{StaticResource Dimension.Control.Compact}", GetSetterValue(tbc, "Height"));
        Assert.Equal("13", GetSetterValue(tbc, "FontSize"));
        Assert.Equal("{StaticResource Padding.Input.Compact}", GetSetterValue(tbc, "Padding"));

        // TextBox Embedded
        Assert.True(styles.ContainsKey("Input.TextBox.Embedded"));
        var tbe = styles["Input.TextBox.Embedded"];
        Assert.Equal("Auto", GetSetterValue(tbe, "Height"));
        Assert.Equal("0", GetSetterValue(tbe, "MinHeight"));
        Assert.Equal("0", GetSetterValue(tbe, "Padding"));
        Assert.Equal("0", GetSetterValue(tbe, "Margin"));
        Assert.Equal("Center", GetSetterValue(tbe, "VerticalAlignment"));
        Assert.Equal("Center", GetSetterValue(tbe, "VerticalContentAlignment"));
        Assert.Equal("0", GetSetterValue(tbe, "BorderThickness"));
        Assert.Equal("Transparent", GetSetterValue(tbe, "Background"));

        // ComboBox
        Assert.True(styles.ContainsKey("Input.ComboBox"));
        var cb = styles["Input.ComboBox"];
        Assert.Equal("{StaticResource Dimension.Control.Standard}", GetSetterValue(cb, "Height"));
        Assert.Equal("{StaticResource Padding.Input.Standard}", GetSetterValue(cb, "Padding"));
        Assert.Equal("14", GetSetterValue(cb, "FontSize"));

        // ComboBox Compact
        Assert.True(styles.ContainsKey("Input.ComboBox.Compact"));
        var cbc = styles["Input.ComboBox.Compact"];
        Assert.Equal("{StaticResource Dimension.Control.Compact}", GetSetterValue(cbc, "Height"));
        Assert.Equal("13", GetSetterValue(cbc, "FontSize"));
        Assert.Equal("{StaticResource Padding.Input.Compact}", GetSetterValue(cbc, "Padding"));

        // PasswordBox
        Assert.True(styles.ContainsKey("Input.PasswordBox"));
        var pb = styles["Input.PasswordBox"];
        Assert.Equal("{StaticResource Dimension.Control.Standard}", GetSetterValue(pb, "Height"));
        Assert.Equal("{StaticResource Padding.Input.Standard}", GetSetterValue(pb, "Padding"));
        Assert.Equal("14", GetSetterValue(pb, "FontSize"));
        Assert.Equal("Center", GetSetterValue(pb, "VerticalContentAlignment"));
        Assert.Equal("True", GetSetterValue(pb, "SnapsToDevicePixels"));

        // PasswordBox Compact
        Assert.True(styles.ContainsKey("Input.PasswordBox.Compact"));
        var pbc = styles["Input.PasswordBox.Compact"];
        Assert.Equal("{StaticResource Dimension.Control.Compact}", GetSetterValue(pbc, "Height"));
        Assert.Equal("13", GetSetterValue(pbc, "FontSize"));
        Assert.Equal("{StaticResource Padding.Input.Compact}", GetSetterValue(pbc, "Padding"));

        // DatePicker
        Assert.True(styles.ContainsKey("Input.DatePicker"));
        var dp = styles["Input.DatePicker"];
        Assert.Equal("{StaticResource Dimension.Control.Standard}", GetSetterValue(dp, "Height"));
        Assert.Equal("{StaticResource Padding.Input.Standard}", GetSetterValue(dp, "Padding"));
        Assert.Equal("14", GetSetterValue(dp, "FontSize"));
        Assert.Equal("Center", GetSetterValue(dp, "VerticalContentAlignment"));

        // DatePicker Compact
        Assert.True(styles.ContainsKey("Input.DatePicker.Compact"));
        var dpc = styles["Input.DatePicker.Compact"];
        Assert.Equal("{StaticResource Dimension.Control.Compact}", GetSetterValue(dpc, "Height"));
        Assert.Equal("13", GetSetterValue(dpc, "FontSize"));
        Assert.Equal("{StaticResource Padding.Input.Compact}", GetSetterValue(dpc, "Padding"));
    }

    [Fact]
    public void Inputs_ImplicitStyles_AreProperlyDeclared()
    {
        var doc = LoadDesktopXaml("Resources", "Inputs.xaml");

        var implicitStyles = doc.Descendants(Presentation + "Style")
            .Where(e => e.Attribute(Xaml + "Key") == null)
            .ToDictionary(e => (string)e.Attribute("TargetType")!, e => (string)e.Attribute("BasedOn")!);

        Assert.Equal("{StaticResource Input.TextBox}", implicitStyles["TextBox"]);
        Assert.Equal("{StaticResource Input.PasswordBox}", implicitStyles["PasswordBox"]);
        Assert.Equal("{StaticResource Input.ComboBox}", implicitStyles["ComboBox"]);
        Assert.Equal("{StaticResource Input.CheckBox}", implicitStyles["CheckBox"]);
        Assert.Equal("{StaticResource Input.RadioButton}", implicitStyles["RadioButton"]);
        Assert.Equal("{StaticResource Input.DatePicker}", implicitStyles["DatePicker"]);
    }

    [Fact]
    public void SearchBox_EliminatesBaselineAndHorizontalJump()
    {
        var doc = LoadDesktopXaml("Controls", "SearchBox.xaml");

        var input = doc.Descendants(Presentation + "TextBox")
            .FirstOrDefault(e => (string?)e.Attribute(Xaml + "Name") == "Input");
        Assert.NotNull(input);

        Assert.Equal("{StaticResource Input.TextBox.Embedded}", (string?)input.Attribute("Style"));
        Assert.Equal("14", (string?)input.Attribute("FontSize"));
        Assert.Equal("Center", (string?)input.Attribute("VerticalAlignment"));
        Assert.Equal("Center", (string?)input.Attribute("VerticalContentAlignment"));

        var placeholder = doc.Descendants(Presentation + "TextBox")
            .FirstOrDefault(e => (string?)e.Attribute("Text") == "{Binding Placeholder, ElementName=Root}");
        Assert.NotNull(placeholder);

        Assert.Equal("14", (string?)placeholder.Attribute("FontSize"));
        Assert.Equal(input.Name, placeholder.Name);
        Assert.Equal("{StaticResource Input.TextBox.Placeholder}", (string?)placeholder.Descendants(Presentation + "Style").Single().Attribute("BasedOn"));
        var styles = LoadDesktopXaml("Resources", "Inputs.xaml");
        var watermark = styles.Descendants(Presentation + "Style").Single(e => (string?)e.Attribute(Xaml + "Key") == "Input.TextBox.Placeholder");
        Assert.Equal("{StaticResource Input.TextBox.Embedded}", (string?)watermark.Attribute("BasedOn"));
        Assert.Equal("False", GetSetterValue(watermark, "IsHitTestVisible"));
        Assert.Equal("Center", (string?)placeholder.Attribute("VerticalAlignment"));
        Assert.Equal("0", (string?)placeholder.Attribute("Margin"));
        Assert.Equal("0", (string?)placeholder.Attribute("Padding"));

        var parentGrid = (XElement?)input.Parent;
        Assert.NotNull(parentGrid);
        Assert.Same(parentGrid, placeholder.Parent);
        Assert.Equal("Center", (string?)parentGrid.Attribute("VerticalAlignment"));
    }

    [Fact]
    public void ReportsView_UsesCanonicalCompactStylesForProductionSelectors()
    {
        var doc = LoadDesktopXaml("Views", "ReportsView.xaml");

        var datePicker = Assert.Single(doc.Descendants(Presentation + "DatePicker"),
            e => ((string?)e.Attribute("SelectedDate"))?.Contains("SelectedDate") == true);
        Assert.Equal("{StaticResource Input.DatePicker.Compact}", (string?)datePicker.Attribute("Style"));
        Assert.Null(datePicker.Attribute("Height"));

        var reportCombos = doc.Descendants(Presentation + "ComboBox")
            .Where(e => ((string?)e.Attribute("SelectedItem"))?.Contains("SelectedMonth") == true
                     || ((string?)e.Attribute("SelectedItem"))?.Contains("SelectedYear") == true)
            .ToArray();
        Assert.Equal(2, reportCombos.Length);
        Assert.All(reportCombos, combo =>
        {
            Assert.Equal("{StaticResource Input.ComboBox.Compact}", (string?)combo.Attribute("Style"));
            Assert.Null(combo.Attribute("Height"));
        });
    }

    [Fact]
    public void CompleteSaleDialog_CurrencyPrefixAndAmount_AreHarmonized()
    {
        var doc = LoadDesktopXaml("Views", "Dialogs", "CompleteSaleDialog.xaml");

        var amountInput = doc.Descendants(Presentation + "TextBox")
            .FirstOrDefault(e => (string?)e.Attribute(Xaml + "Name") == "AmountReceivedTextBox");
        Assert.NotNull(amountInput);

        Assert.Equal("{StaticResource Input.TextBox.Embedded}", (string?)amountInput.Attribute("Style"));
        Assert.Equal("18", (string?)amountInput.Attribute("FontSize"));
        Assert.Equal("Bold", (string?)amountInput.Attribute("FontWeight"));
        Assert.Equal("{StaticResource Font.Numeric}", (string?)amountInput.Attribute("FontFamily"));
        Assert.Equal("Center", (string?)amountInput.Attribute("VerticalAlignment"));

        var parentGrid = (XElement?)amountInput.Parent;
        Assert.NotNull(parentGrid);

        var prefixTextBlock = parentGrid.Descendants(Presentation + "TextBlock")
            .FirstOrDefault(e => (string?)e.Attribute("Text") == "Rs.");
        Assert.NotNull(prefixTextBlock);

        Assert.Equal("18", (string?)prefixTextBlock.Attribute("FontSize"));
        Assert.Equal("Bold", (string?)prefixTextBlock.Attribute("FontWeight"));
        Assert.Equal("{StaticResource Font.Numeric}", (string?)prefixTextBlock.Attribute("FontFamily"));
        Assert.Equal("Center", (string?)prefixTextBlock.Attribute("VerticalAlignment"));

        var containerBorder = (XElement?)parentGrid.Parent;
        Assert.NotNull(containerBorder);
        Assert.Equal("46", (string?)containerBorder.Attribute("Height"));
        Assert.Equal("True", (string?)containerBorder.Attribute("SnapsToDevicePixels"));
    }

    [Fact]
    public void PosView_DiscountInput_UsesEmbeddedStyleWithoutClipping()
    {
        var doc = LoadDesktopXaml("Views", "PosView.xaml");

        var discountInput = doc.Descendants(Presentation + "TextBox")
            .FirstOrDefault(e => ((string?)e.Attribute("Text"))?.Contains("DiscountAmountText") == true);
        Assert.NotNull(discountInput);

        Assert.Equal("{StaticResource Input.TextBox.Embedded}", (string?)discountInput.Attribute("Style"));
        Assert.Equal("Right", (string?)discountInput.Attribute("TextAlignment"));
        Assert.Equal("Center", (string?)discountInput.Attribute("VerticalAlignment"));

        var parentGrid = (XElement?)discountInput.Parent;
        Assert.NotNull(parentGrid);
        var containerBorder = (XElement?)parentGrid.Parent;
        Assert.NotNull(containerBorder);
        Assert.Equal("28", (string?)containerBorder.Attribute("Height"));
        Assert.Equal("True", (string?)containerBorder.Attribute("SnapsToDevicePixels"));
    }

    [Fact]
    public void SalesReturnDialog_StepperInput_UsesEmbeddedStyleWithoutClipping()
    {
        var doc = LoadDesktopXaml("Views", "Dialogs", "SalesReturnDialog.xaml");

        var stepperInput = doc.Descendants(Presentation + "TextBox")
            .FirstOrDefault(e => ((string?)e.Attribute("Text"))?.Contains("ReturnQuantityText") == true);
        Assert.NotNull(stepperInput);

        Assert.Equal("{StaticResource Input.TextBox.Embedded}", (string?)stepperInput.Attribute("Style"));
        Assert.Equal("Center", (string?)stepperInput.Attribute("TextAlignment"));
        Assert.Equal("Center", (string?)stepperInput.Attribute("VerticalAlignment"));

        var containerBorder = (XElement?)stepperInput.Parent;
        Assert.NotNull(containerBorder);
        Assert.Equal("28", (string?)containerBorder.Attribute("Height"));
        Assert.Equal("True", (string?)containerBorder.Attribute("SnapsToDevicePixels"));
    }

    private static string? GetSetterValue(XElement styleElement, string propertyName)
    {
        return (string?)styleElement.Elements(Presentation + "Setter")
            .FirstOrDefault(s => (string?)s.Attribute("Property") == propertyName)?
            .Attribute("Value");
    }

    private static XDocument LoadDesktopXaml(params string[] pathParts)
    {
        var baseDir = new DirectoryInfo(AppContext.BaseDirectory);
        while (baseDir != null && !File.Exists(Path.Combine(baseDir.FullName, "EdgeRetails.sln")))
        {
            baseDir = baseDir.Parent;
        }

        Assert.NotNull(baseDir);
        var path = Path.Combine(new[] { baseDir.FullName, "src", "EdgeRetails.Desktop" }.Concat(pathParts).ToArray());
        Assert.True(File.Exists(path), $"File not found: {path}");
        return XDocument.Load(path);
    }
}
