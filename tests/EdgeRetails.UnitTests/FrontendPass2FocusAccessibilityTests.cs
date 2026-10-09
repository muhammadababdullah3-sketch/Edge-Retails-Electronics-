using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace EdgeRetails.UnitTests;

public sealed class FrontendPass2FocusAccessibilityTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Buttons_FocusVisual_Styles_Defined()
    {
        var doc = LoadDesktopXaml("Resources", "Buttons.xaml");

        var styles = doc.Descendants(Presentation + "Style")
            .Where(e => e.Attribute(Xaml + "Key") != null)
            .ToDictionary(e => (string)e.Attribute(Xaml + "Key")!);

        // Button standard focus visual
        Assert.True(styles.ContainsKey("FocusVisual.Button"));
        var fvButton = styles["FocusVisual.Button"];
        var border = fvButton.Descendants(Presentation + "Border").FirstOrDefault();
        Assert.NotNull(border);
        Assert.Equal("{DynamicResource Brush.Focus.Ring}", (string?)border.Attribute("BorderBrush"));
        Assert.Equal("2", (string?)border.Attribute("BorderThickness"));
        Assert.Equal("10", (string?)border.Attribute("CornerRadius"));
        Assert.Equal("-3", (string?)border.Attribute("Margin"));

        // Button primary action focus visual
        Assert.True(styles.ContainsKey("FocusVisual.Button.PrimaryAction"));
        var fvPrimary = styles["FocusVisual.Button.PrimaryAction"];
        var primaryBorder = fvPrimary.Descendants(Presentation + "Border").FirstOrDefault();
        Assert.NotNull(primaryBorder);
        Assert.Equal("{DynamicResource Brush.Focus.Ring}", (string?)primaryBorder.Attribute("BorderBrush"));
        Assert.Equal("2", (string?)primaryBorder.Attribute("BorderThickness"));
        Assert.Equal("12", (string?)primaryBorder.Attribute("CornerRadius"));

        // Button text focus visual
        Assert.True(styles.ContainsKey("FocusVisual.Button.Text"));
        var fvText = styles["FocusVisual.Button.Text"];
        var textBorder = fvText.Descendants(Presentation + "Border").FirstOrDefault();
        Assert.NotNull(textBorder);
        Assert.Equal("{DynamicResource Brush.Focus.Ring}", (string?)textBorder.Attribute("BorderBrush"));
        Assert.Equal("1.5", (string?)textBorder.Attribute("BorderThickness"));
        Assert.Equal("4", (string?)textBorder.Attribute("CornerRadius"));
    }

    [Fact]
    public void Buttons_FocusVisibility_Contracts()
    {
        var doc = LoadDesktopXaml("Resources", "Buttons.xaml");

        var styles = doc.Descendants(Presentation + "Style")
            .Where(e => e.Attribute(Xaml + "Key") != null)
            .ToDictionary(e => (string)e.Attribute(Xaml + "Key")!);

        // Button.Base sets FocusVisualStyle and focus trigger
        var baseBtn = styles["Button.Base"];
        Assert.Equal("{StaticResource FocusVisual.Button}", GetSetterValue(baseBtn, "FocusVisualStyle"));
        var baseTrigger = baseBtn.Descendants(Presentation + "Trigger")
            .FirstOrDefault(t => (string?)t.Attribute("Property") == "IsKeyboardFocused" && (string?)t.Attribute("Value") == "True");
        Assert.NotNull(baseTrigger);
        Assert.Equal("{DynamicResource Brush.Focus.Ring}", GetSetterValueFromTrigger(baseTrigger, "BorderBrush"));

        // Button.Primary has white border focus trigger for high contrast on gradient
        var primaryBtn = styles["Button.Primary"];
        var primaryTrigger = primaryBtn.Descendants(Presentation + "Trigger")
            .FirstOrDefault(t => (string?)t.Attribute("Property") == "IsKeyboardFocused" && (string?)t.Attribute("Value") == "True");
        Assert.NotNull(primaryTrigger);
        Assert.Equal("{StaticResource Brush.White}", GetSetterValueFromTrigger(primaryTrigger, "BorderBrush"));

        // Button.Neutral has focus trigger
        var neutralBtn = styles["Button.Neutral"];
        var neutralTrigger = neutralBtn.Descendants(Presentation + "Trigger")
            .FirstOrDefault(t => (string?)t.Attribute("Property") == "IsKeyboardFocused" && (string?)t.Attribute("Value") == "True");
        Assert.NotNull(neutralTrigger);
        Assert.Equal("{DynamicResource Brush.Focus.Ring}", GetSetterValueFromTrigger(neutralTrigger, "BorderBrush"));

        // Compact semantic action family has high-contrast white focus indicators
        foreach (var key in new[] { "Button.Info.Compact", "Button.Payment.Compact", "Button.Warning.Compact", "Button.Expense" })
        {
            Assert.True(styles.ContainsKey(key), $"Missing style {key}");
            var btn = styles[key];
            var trigger = btn.Descendants(Presentation + "Trigger")
                .FirstOrDefault(t => (string?)t.Attribute("Property") == "IsKeyboardFocused" && (string?)t.Attribute("Value") == "True");
            Assert.NotNull(trigger);
            Assert.Equal("{StaticResource Brush.White}", GetSetterValueFromTrigger(trigger, "BorderBrush"));
        }

        // Primary action buttons use primary action focus visual
        Assert.Equal("{StaticResource FocusVisual.Button.PrimaryAction}", GetSetterValue(styles["Button.Success.PrimaryAction"], "FocusVisualStyle"));
        Assert.Equal("{StaticResource FocusVisual.Button.PrimaryAction}", GetSetterValue(styles["Button.Info.PrimaryAction"], "FocusVisualStyle"));

        // Text buttons use text focus visual and hover/focus highlight
        var textBtn = styles["Button.Text.Primary"];
        Assert.Equal("{StaticResource FocusVisual.Button.Text}", GetSetterValue(textBtn, "FocusVisualStyle"));
        var textTrigger = textBtn.Descendants(Presentation + "Trigger")
            .FirstOrDefault(t => (string?)t.Attribute("Property") == "IsKeyboardFocused" && (string?)t.Attribute("Value") == "True");
        Assert.NotNull(textTrigger);
        Assert.Equal("{DynamicResource Brush.Focus.Ring}", GetSetterValueFromTrigger(textTrigger, "BorderBrush"));
        Assert.Equal("{DynamicResource Brush.Hover.Subtle}", GetSetterValueFromTrigger(textTrigger, "Background"));
    }

    [Fact]
    public void Inputs_CheckBox_FocusAndChecked_States_Distinct()
    {
        var doc = LoadDesktopXaml("Resources", "Inputs.xaml");

        var styles = doc.Descendants(Presentation + "Style")
            .Where(e => e.Attribute(Xaml + "Key") != null)
            .ToDictionary(e => (string)e.Attribute(Xaml + "Key")!);

        Assert.True(styles.ContainsKey("Input.CheckBox"));
        var cb = styles["Input.CheckBox"];

        // FocusRing border element exists in template
        var focusRing = cb.Descendants(Presentation + "Border")
            .FirstOrDefault(b => (string?)b.Attribute(Xaml + "Name") == "FocusRing");
        Assert.NotNull(focusRing);
        Assert.Equal("24", (string?)focusRing.Attribute("Width"));
        Assert.Equal("24", (string?)focusRing.Attribute("Height"));
        Assert.Equal("2", (string?)focusRing.Attribute("BorderThickness"));
        Assert.Equal("6", (string?)focusRing.Attribute("CornerRadius"));

        // Keyboard focus trigger sets FocusRing BorderBrush
        var kbdTrigger = cb.Descendants(Presentation + "Trigger")
            .FirstOrDefault(t => (string?)t.Attribute("Property") == "IsKeyboardFocused" && (string?)t.Attribute("Value") == "True");
        Assert.NotNull(kbdTrigger);
        var focusRingSetter = kbdTrigger.Elements(Presentation + "Setter")
            .FirstOrDefault(s => (string?)s.Attribute("TargetName") == "FocusRing" && (string?)s.Attribute("Property") == "BorderBrush");
        Assert.NotNull(focusRingSetter);
        Assert.Equal("{DynamicResource Brush.Focus.Ring}", (string?)focusRingSetter.Attribute("Value"));

        // MultiTrigger for Checked + KeyboardFocused ensures distinct visual state
        var multiTrigger = cb.Descendants(Presentation + "MultiTrigger").FirstOrDefault();
        Assert.NotNull(multiTrigger);
        var conditions = multiTrigger.Descendants(Presentation + "Condition").ToList();
        Assert.Contains(conditions, c => (string?)c.Attribute("Property") == "IsChecked" && (string?)c.Attribute("Value") == "True");
        Assert.Contains(conditions, c => (string?)c.Attribute("Property") == "IsKeyboardFocused" && (string?)c.Attribute("Value") == "True");

        var boxBorderSetter = multiTrigger.Elements(Presentation + "Setter")
            .FirstOrDefault(s => (string?)s.Attribute("TargetName") == "Box" && (string?)s.Attribute("Property") == "BorderBrush");
        Assert.NotNull(boxBorderSetter);
        Assert.Equal("{DynamicResource Brush.Surface.Default}", (string?)boxBorderSetter.Attribute("Value"));

        // Hover does NOT set FocusRing
        var mouseOverTrigger = cb.Descendants(Presentation + "Trigger")
            .FirstOrDefault(t => (string?)t.Attribute("Property") == "IsMouseOver" && (string?)t.Attribute("Value") == "True");
        Assert.NotNull(mouseOverTrigger);
        Assert.DoesNotContain(mouseOverTrigger.Elements(Presentation + "Setter"),
            s => (string?)s.Attribute("TargetName") == "FocusRing");
    }

    [Fact]
    public void Inputs_RadioButton_FocusAndChecked_States_Distinct()
    {
        var doc = LoadDesktopXaml("Resources", "Inputs.xaml");

        var styles = doc.Descendants(Presentation + "Style")
            .Where(e => e.Attribute(Xaml + "Key") != null)
            .ToDictionary(e => (string)e.Attribute(Xaml + "Key")!);

        Assert.True(styles.ContainsKey("Input.RadioButton"));
        var rb = styles["Input.RadioButton"];

        // FocusRing border element exists in template with circular radius
        var focusRing = rb.Descendants(Presentation + "Border")
            .FirstOrDefault(b => (string?)b.Attribute(Xaml + "Name") == "FocusRing");
        Assert.NotNull(focusRing);
        Assert.Equal("24", (string?)focusRing.Attribute("Width"));
        Assert.Equal("24", (string?)focusRing.Attribute("Height"));
        Assert.Equal("2", (string?)focusRing.Attribute("BorderThickness"));
        Assert.Equal("12", (string?)focusRing.Attribute("CornerRadius"));

        // Keyboard focus trigger sets FocusRing BorderBrush
        var kbdTrigger = rb.Descendants(Presentation + "Trigger")
            .FirstOrDefault(t => (string?)t.Attribute("Property") == "IsKeyboardFocused" && (string?)t.Attribute("Value") == "True");
        Assert.NotNull(kbdTrigger);

        // MultiTrigger for Checked + KeyboardFocused ensures distinct visual state
        var multiTrigger = rb.Descendants(Presentation + "MultiTrigger").FirstOrDefault();
        Assert.NotNull(multiTrigger);
        var circleBorderSetter = multiTrigger.Elements(Presentation + "Setter")
            .FirstOrDefault(s => (string?)s.Attribute("TargetName") == "Circle" && (string?)s.Attribute("Property") == "BorderBrush");
        Assert.NotNull(circleBorderSetter);
        Assert.Equal("{DynamicResource Brush.Surface.Default}", (string?)circleBorderSetter.Attribute("Value"));
    }

    [Fact]
    public void Inputs_FocusRing_Tokens_UsedAcrossInputControls()
    {
        var doc = LoadDesktopXaml("Resources", "Inputs.xaml");

        var styles = doc.Descendants(Presentation + "Style")
            .Where(e => e.Attribute(Xaml + "Key") != null)
            .ToDictionary(e => (string)e.Attribute(Xaml + "Key")!);

        // TextBox uses Brush.Focus.Ring
        var tbTrigger = styles["Input.TextBox"].Descendants(Presentation + "Trigger")
            .FirstOrDefault(t => (string?)t.Attribute("Property") == "IsKeyboardFocused" && (string?)t.Attribute("Value") == "True");
        Assert.NotNull(tbTrigger);
        Assert.Equal("{DynamicResource Brush.Focus.Ring}", GetSetterValueFromTrigger(tbTrigger, "BorderBrush"));

        // PasswordBox uses Brush.Focus.Ring
        var pbTrigger = styles["Input.PasswordBox"].Descendants(Presentation + "Trigger")
            .FirstOrDefault(t => (string?)t.Attribute("Property") == "IsKeyboardFocused" && (string?)t.Attribute("Value") == "True");
        Assert.NotNull(pbTrigger);
        Assert.Equal("{DynamicResource Brush.Focus.Ring}", GetSetterValueFromTrigger(pbTrigger, "BorderBrush"));

        // ComboBox uses Brush.Focus.Ring
        var cbTrigger = styles["Input.ComboBox"].Descendants(Presentation + "Trigger")
            .FirstOrDefault(t => (string?)t.Attribute("Property") == "IsKeyboardFocusWithin" && (string?)t.Attribute("Value") == "True");
        Assert.NotNull(cbTrigger);
        Assert.Equal("{DynamicResource Brush.Focus.Ring}", GetSetterValueFromTrigger(cbTrigger, "BorderBrush"));

        // DatePicker uses Brush.Focus.Ring
        var dpTrigger = styles["Input.DatePicker"].Descendants(Presentation + "Trigger")
            .FirstOrDefault(t => (string?)t.Attribute("Property") == "IsKeyboardFocusWithin" && (string?)t.Attribute("Value") == "True");
        Assert.NotNull(dpTrigger);
        Assert.Equal("{DynamicResource Brush.Focus.Ring}", GetSetterValueFromTrigger(dpTrigger, "BorderBrush"));
    }

    [Fact]
    public void EmbeddedTextBox_PreservesPass1Geometry()
    {
        var doc = LoadDesktopXaml("Resources", "Inputs.xaml");

        var styles = doc.Descendants(Presentation + "Style")
            .Where(e => e.Attribute(Xaml + "Key") != null)
            .ToDictionary(e => (string)e.Attribute(Xaml + "Key")!);

        var embedded = styles["Input.TextBox.Embedded"];
        Assert.Equal("Auto", GetSetterValue(embedded, "Height"));
        Assert.Equal("0", GetSetterValue(embedded, "MinHeight"));
        Assert.Equal("0", GetSetterValue(embedded, "Padding"));
        Assert.Equal("0", GetSetterValue(embedded, "Margin"));
        Assert.Equal("0", GetSetterValue(embedded, "BorderThickness"));
    }

    [Fact]
    public void EmbeddedTextBox_ProductionConsumersRetainSharedFocusArchitecture()
    {
        foreach (var (view, consumer) in new[]
        {
            (new[] { "Views", "Dialogs", "CompleteSaleDialog.xaml" }, "AmountReceivedTextBox"),
            (new[] { "Views", "PosView.xaml" }, "DiscountAmountText"),
            (new[] { "Views", "Dialogs", "SalesReturnDialog.xaml" }, "ReturnQuantityText")
        })
        {
            var doc = LoadDesktopXaml(view);
            var textBox = doc.Descendants(Presentation + "TextBox")
                .FirstOrDefault(e => string.Join(" ", e.Attributes().Select(a => a.Value)).Contains(consumer));

            Assert.NotNull(textBox);
            Assert.Equal("{StaticResource Input.TextBox.Embedded}", (string?)textBox.Attribute("Style"));
            Assert.NotEqual("{x:Null}", (string?)textBox.Attribute("FocusVisualStyle"));

            var host = textBox.Ancestors(Presentation + "Border").FirstOrDefault();
            Assert.NotNull(host);
            var focusTrigger = host.Descendants(Presentation + "DataTrigger")
                .FirstOrDefault(t => ((string?)t.Attribute("Binding"))?.Contains("IsKeyboardFocused") == true);
            Assert.NotNull(focusTrigger);
            Assert.Equal("{DynamicResource Brush.Focus.Ring}", GetSetterValueFromTrigger(focusTrigger, "BorderBrush"));
        }

        var searchBox = LoadDesktopXaml("Controls", "SearchBox.xaml");
        var searchFocus = searchBox.Descendants(Presentation + "DataTrigger")
            .FirstOrDefault(t => ((string?)t.Attribute("Binding"))?.Contains("ElementName=Input") == true);
        Assert.NotNull(searchFocus);
        Assert.Equal("{DynamicResource Brush.Focus.Ring}", GetSetterValueFromTrigger(searchFocus, "BorderBrush"));
    }

    [Fact]
    public void CompleteSale_CustomControlsKeepKeyboardFocusVisibleWithoutLayoutJitter()
    {
        var doc = LoadDesktopXaml("Views", "Dialogs", "CompleteSaleDialog.xaml");
        var resources = doc.Descendants(Presentation + "Style")
            .Where(e => e.Attribute(Xaml + "Key") != null)
            .ToDictionary(e => (string)e.Attribute(Xaml + "Key")!);

        var receipt = resources["ReceiptCheckBoxStyle"];
        var receiptFocus = receipt.Descendants(Presentation + "Trigger")
            .FirstOrDefault(t => (string?)t.Attribute("Property") == "IsKeyboardFocused");
        Assert.NotNull(receiptFocus);
        Assert.Equal("{DynamicResource Brush.Focus.Ring}", GetSetterValueFromTrigger(receiptFocus, "BorderBrush"));
        Assert.NotNull(receipt.Descendants(Presentation + "Border").FirstOrDefault(b => (string?)b.Attribute(Xaml + "Name") == "FocusRing"));
        Assert.DoesNotContain(receiptFocus.Elements(Presentation + "Setter"), s => (string?)s.Attribute("Property") == "BorderThickness");

        var quickCash = resources["QuickCashChipStyle"];
        Assert.Equal("{StaticResource FocusVisual.Button.Text}", GetSetterValue(quickCash, "FocusVisualStyle"));
        Assert.Contains(quickCash.Descendants(Presentation + "Trigger"), t => (string?)t.Attribute("Property") == "IsKeyboardFocused");

        var complete = doc.Descendants(Presentation + "Button")
            .FirstOrDefault(b => (string?)b.Attribute("IsDefault") == "True");
        Assert.NotNull(complete);
        Assert.Equal("{StaticResource FocusVisual.Button.PrimaryAction}", (string?)complete.Attribute("FocusVisualStyle"));
        var mainFocusTrigger = complete.Descendants(Presentation + "Trigger")
            .FirstOrDefault(t => (string?)t.Attribute("Property") == "IsKeyboardFocused");
        Assert.Null(mainFocusTrigger);
    }

    [Fact]
    public void SalesReturnCustomRadioCardsRetainKeyboardFocusVisual()
    {
        var doc = LoadDesktopXaml("Views", "Dialogs", "SalesReturnDialog.xaml");
        var radioButtons = doc.Descendants(Presentation + "RadioButton").ToList();

        Assert.Equal(4, radioButtons.Count);
        Assert.All(radioButtons, radio => Assert.Equal(
            "{StaticResource FocusVisual.Button.PrimaryAction}",
            (string?)radio.Attribute("FocusVisualStyle")));
    }

    [Fact]
    public void ProductionActionControlsDoNotSuppressKeyboardFocusVisuals()
    {
        var pos = LoadDesktopXaml("Views", "PosView.xaml");
        foreach (var styleKey in new[] { "Pos.StepperButton", "Pos.RemoveButton" })
        {
            var style = pos.Descendants(Presentation + "Style")
                .First(e => (string?)e.Attribute(Xaml + "Key") == styleKey);
            Assert.Equal("{StaticResource FocusVisual.Button.Text}", GetSetterValue(style, "FocusVisualStyle"));
        }
        var posComplete = pos.Descendants(Presentation + "Button")
            .First(button => ((string?)button.Attribute("Command"))?.Contains("CompleteSaleCommand") == true);
        Assert.Equal("{StaticResource FocusVisual.Button.PrimaryAction}", (string?)posComplete.Attribute("FocusVisualStyle"));

        var salesReturn = LoadDesktopXaml("Views", "Dialogs", "SalesReturnDialog.xaml");
        foreach (var styleKey in new[] { "Stepper.Button", "Button.CloseCross" })
        {
            var style = salesReturn.Descendants(Presentation + "Style")
                .First(e => (string?)e.Attribute(Xaml + "Key") == styleKey);
            Assert.Equal("{StaticResource FocusVisual.Button.Text}", GetSetterValue(style, "FocusVisualStyle"));
        }
        Assert.Contains(salesReturn.Descendants(Presentation + "Run"), run =>
            ((string?)run.Attribute("Text"))?.Contains("Binding Brand, Mode=OneWay") == true);
        Assert.Contains(salesReturn.Descendants(Presentation + "Run"), run =>
            ((string?)run.Attribute("Text"))?.Contains("Binding Sku, Mode=OneWay") == true);
        Assert.DoesNotContain(pos.Descendants(Presentation + "Setter"), setter =>
            (string?)setter.Attribute("Property") == "FocusVisualStyle" && (string?)setter.Attribute("Value") == "{x:Null}");
        Assert.DoesNotContain(salesReturn.Descendants(Presentation + "Setter"), setter =>
            (string?)setter.Attribute("Property") == "FocusVisualStyle" && (string?)setter.Attribute("Value") == "{x:Null}");
    }

    [Fact]
    public void Tables_Cell_ActiveCellFocusIndicator_ZeroJitter()
    {
        var doc = LoadDesktopXaml("Resources", "Tables.xaml");

        var styles = doc.Descendants(Presentation + "Style")
            .Where(e => e.Attribute(Xaml + "Key") != null)
            .ToDictionary(e => (string)e.Attribute(Xaml + "Key")!);

        // Table.Cell has stable BorderThickness of 0
        var cellStyle = styles["Table.Cell"];
        Assert.Equal("0", GetSetterValue(cellStyle, "BorderThickness"));

        // Zero jitter: Table.Cell does not change BorderThickness in triggers
        var cellTriggers = cellStyle.Descendants(Presentation + "Trigger").ToList();
        Assert.DoesNotContain(cellTriggers, t => t.Elements(Presentation + "Setter")
            .Any(s => (string?)s.Attribute("Property") == "BorderThickness" && (string?)s.Attribute("Value") != "0"));

        // Cell template has CellFocusIndicator overlay
        var cellFocusIndicator = cellStyle.Descendants(Presentation + "Border")
            .FirstOrDefault(b => (string?)b.Attribute(Xaml + "Name") == "CellFocusIndicator");
        Assert.NotNull(cellFocusIndicator);
        Assert.Equal("1.5", (string?)cellFocusIndicator.Attribute("BorderThickness"));
        Assert.Equal("4", (string?)cellFocusIndicator.Attribute("CornerRadius"));
        var cellKeyboardFocusTrigger = cellStyle.Descendants(Presentation + "Trigger")
            .FirstOrDefault(t => (string?)t.Attribute("Property") == "IsKeyboardFocusWithin" && (string?)t.Attribute("Value") == "True");
        Assert.NotNull(cellKeyboardFocusTrigger);
        Assert.Contains(cellKeyboardFocusTrigger.Elements(Presentation + "Setter"), setter =>
            (string?)setter.Attribute("TargetName") == "CellFocusIndicator" &&
            (string?)setter.Attribute("Property") == "BorderBrush" &&
            (string?)setter.Attribute("Value") == "{DynamicResource Brush.Focus.Ring}");

        // Table.Row has stable BorderThickness 0,0,0,1 with no jitter on focus
        var rowStyle = styles["Table.Row"];
        Assert.Equal("0,0,0,1", GetSetterValue(rowStyle, "BorderThickness"));
        var rowFocusTrigger = rowStyle.Descendants(Presentation + "Trigger")
            .FirstOrDefault(t => (string?)t.Attribute("Property") == "IsKeyboardFocusWithin" && (string?)t.Attribute("Value") == "True");
        Assert.NotNull(rowFocusTrigger);
        // Does NOT set BorderThickness to 1 (which would cause 1px content jitter)
        Assert.DoesNotContain(rowFocusTrigger.Elements(Presentation + "Setter"),
            s => (string?)s.Attribute("Property") == "BorderThickness");
    }

    [Fact]
    public void Themes_FocusRing_Brushes_Parity()
    {
        var brushesDoc = LoadDesktopXaml("Resources", "Brushes.xaml");
        var lightDoc = LoadDesktopXaml("Resources", "Themes", "Light.xaml");
        var darkDoc = LoadDesktopXaml("Resources", "Themes", "Dark.xaml");

        var fallbackBrush = brushesDoc.Descendants(Presentation + "SolidColorBrush")
            .FirstOrDefault(b => (string?)b.Attribute(Xaml + "Key") == "Brush.Focus.Ring");
        Assert.NotNull(fallbackBrush);

        var lightBrush = lightDoc.Descendants(Presentation + "SolidColorBrush")
            .FirstOrDefault(b => (string?)b.Attribute(Xaml + "Key") == "Brush.Focus.Ring");
        Assert.NotNull(lightBrush);

        var darkBrush = darkDoc.Descendants(Presentation + "SolidColorBrush")
            .FirstOrDefault(b => (string?)b.Attribute(Xaml + "Key") == "Brush.Focus.Ring");
        Assert.NotNull(darkBrush);
        Assert.Equal("#8C7BFF", (string?)darkBrush.Attribute("Color"));
    }

    [Fact]
    public void ShellNavigation_Modal_FocusTrap_And_Contracts()
    {
        var modalDoc = LoadDesktopXaml("Controls", "ModalHost.xaml");
        var sidebarDoc = LoadDesktopXaml("Controls", "AppSidebar.xaml");
        var shellDoc = LoadDesktopXaml("Views", "ShellView.xaml");

        // ModalPresenter cycles tab navigation
        var modalPresenter = modalDoc.Descendants(Presentation + "ContentControl")
            .FirstOrDefault(c => (string?)c.Attribute(Xaml + "Name") == "ModalPresenter");
        Assert.NotNull(modalPresenter);
        Assert.Equal("Cycle", GetAttributeValue(modalPresenter, "KeyboardNavigation.TabNavigation"));

        // Sidebar continues tab navigation
        Assert.Equal("Continue", GetAttributeValue(sidebarDoc.Root, "KeyboardNavigation.TabNavigation"));

        // Non-modal shell page content must allow traversal out to shell controls.
        var shellContent = shellDoc.Descendants(Presentation + "ContentControl")
            .FirstOrDefault(c => ((string?)c.Attribute("Content"))?.Contains("CurrentPage") == true);
        Assert.NotNull(shellContent);
        Assert.Equal("Continue", GetAttributeValue(shellContent, "KeyboardNavigation.TabNavigation"));
    }

    private static string? GetAttributeValue(XElement? element, string localName)
    {
        return element?.Attributes().FirstOrDefault(a => a.Name.LocalName == localName)?.Value;
    }

    private static string? GetSetterValue(XElement styleElement, string propertyName)
    {
        return (string?)styleElement.Elements(Presentation + "Setter")
            .FirstOrDefault(s => (string?)s.Attribute("Property") == propertyName)?
            .Attribute("Value");
    }

    private static string? GetSetterValueFromTrigger(XElement triggerElement, string propertyName)
    {
        return (string?)triggerElement.Elements(Presentation + "Setter")
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
