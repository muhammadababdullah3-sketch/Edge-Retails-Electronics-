using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace EdgeRetails.UnitTests;

public class FrontendPass3ResponsiveLayoutTests
{
    private static readonly XNamespace XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace XNs = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string FindRepoRoot()
    {
        var current = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "EdgeRetails.sln")))
            {
                return current;
            }
            var parent = Directory.GetParent(current);
            if (parent == null)
            {
                break;
            }
            current = parent.FullName;
        }
        throw new InvalidOperationException("Could not find repository root containing EdgeRetails.sln.");
    }

    private static XDocument LoadXaml(string relativePath)
    {
        var fullPath = Path.Combine(FindRepoRoot(), relativePath);
        Assert.True(File.Exists(fullPath), $"Expected file not found: {fullPath}");
        return XDocument.Load(fullPath);
    }

    [Fact]
    public void PosView_MainLayout_HasResponsiveCatalogAndCartColumns()
    {
        var doc = LoadXaml("src/EdgeRetails.Desktop/Views/PosView.xaml");

        var colDefs = doc.Descendants(XamlNs + "Grid")
            .Where(g => g.Attribute("Margin")?.Value == "14")
            .SelectMany(g => g.Elements(XamlNs + "Grid.ColumnDefinitions"))
            .SelectMany(cd => cd.Elements(XamlNs + "ColumnDefinition"))
            .ToList();

        Assert.Equal(3, colDefs.Count);

        // Column 0: Catalog
        var catalogCol = colDefs[0];
        Assert.Equal("1.6*", catalogCol.Attribute("Width")?.Value);
        Assert.Equal("380", catalogCol.Attribute("MinWidth")?.Value);

        // Column 1: Gap
        var gapCol = colDefs[1];
        Assert.Equal("14", gapCol.Attribute("Width")?.Value);

        // Column 2: Cart
        var cartCol = colDefs[2];
        Assert.Equal("*", cartCol.Attribute("Width")?.Value);
        Assert.Equal("320", cartCol.Attribute("MinWidth")?.Value);
        Assert.Equal("420", cartCol.Attribute("MaxWidth")?.Value);
    }

    [Fact]
    public void PosView_CatalogDataGrid_ColumnsHaveResponsiveMinWidths()
    {
        var doc = LoadXaml("src/EdgeRetails.Desktop/Views/PosView.xaml");

        var dataGrid = doc.Descendants(XamlNs + "DataGrid")
            .FirstOrDefault(d => d.Attribute("ItemsSource")?.Value == "{Binding FilteredProducts}");
        Assert.NotNull(dataGrid);

        var columns = dataGrid.Descendants(XamlNs + "DataGrid.Columns").Elements().ToList();
        Assert.Equal(4, columns.Count);

        // Product column
        var productCol = columns[0];
        Assert.Equal("Product", productCol.Attribute("Header")?.Value);
        Assert.Equal("1.8*", productCol.Attribute("Width")?.Value);
        Assert.Equal("130", productCol.Attribute("MinWidth")?.Value);

        // Brand column
        var brandCol = columns[1];
        Assert.Equal("Brand", brandCol.Attribute("Header")?.Value);
        Assert.Equal("1.1*", brandCol.Attribute("Width")?.Value);
        Assert.Equal("75", brandCol.Attribute("MinWidth")?.Value);

        // Stock column
        var stockCol = columns[2];
        Assert.Equal("Stock", stockCol.Attribute("Header")?.Value);
        Assert.Equal("135", stockCol.Attribute("Width")?.Value);

        // Price column
        var priceCol = columns[3];
        Assert.Equal("Price", priceCol.Attribute("Header")?.Value);
        Assert.Equal("130", priceCol.Attribute("Width")?.Value);
    }

    [Fact]
    public void PosView_FiltersAndActionsBar_UsesResponsiveWrapLayout()
    {
        var doc = LoadXaml("src/EdgeRetails.Desktop/Views/PosView.xaml");

        var filterBar = doc.Descendants(XamlNs + "Grid")
            .FirstOrDefault(g => g.Attribute("Grid.Row")?.Value == "1" && g.Elements(XamlNs + "Grid.RowDefinitions").Any());
        Assert.NotNull(filterBar);

        // Check ComboBoxes use Input.ComboBox.Compact
        var comboBoxes = filterBar.Descendants(XamlNs + "ComboBox").ToList();
        Assert.True(comboBoxes.Count >= 2);
        foreach (var cb in comboBoxes)
        {
            Assert.Equal("{StaticResource Input.ComboBox.Compact}", cb.Attribute("Style")?.Value);
            Assert.Equal("120", cb.Attribute("Width")?.Value);
        }

        // Check operational buttons are inside a WrapPanel to prevent narrow clipping
        var wrapPanels = filterBar.Descendants(XamlNs + "WrapPanel").ToList();
        Assert.True(wrapPanels.Count >= 2);

        var actionsWrap = wrapPanels.FirstOrDefault(wp => wp.Descendants(XamlNs + "Button").Any(b => b.Attribute("Content")?.Value == "Price Check"));
        Assert.NotNull(actionsWrap);

        var buttonContents = actionsWrap.Descendants(XamlNs + "Button")
            .Select(b => b.Attribute("Content")?.Value)
            .ToList();
        Assert.Contains("Price Check", buttonContents);
        Assert.Contains("Save Draft", buttonContents);
        Assert.Contains("Hold", buttonContents);
        Assert.Contains("Drafts", buttonContents);
    }

    [Fact]
    public void Table_DataGrid_HasExplicitScrollbarVisibility()
    {
        var doc = LoadXaml("src/EdgeRetails.Desktop/Resources/Tables.xaml");

        var tableGridStyle = doc.Descendants(XamlNs + "Style")
            .FirstOrDefault(s => s.Attribute(XNs + "Key")?.Value == "Table.DataGrid");
        Assert.NotNull(tableGridStyle);

        var setters = tableGridStyle.Elements(XamlNs + "Setter").ToList();

        var hScrollSetter = setters.FirstOrDefault(s => s.Attribute("Property")?.Value == "ScrollViewer.HorizontalScrollBarVisibility");
        Assert.NotNull(hScrollSetter);
        Assert.Equal("Auto", hScrollSetter.Attribute("Value")?.Value);

        var vScrollSetter = setters.FirstOrDefault(s => s.Attribute("Property")?.Value == "ScrollViewer.VerticalScrollBarVisibility");
        Assert.NotNull(vScrollSetter);
        Assert.Equal("Auto", vScrollSetter.Attribute("Value")?.Value);
    }

    [Fact]
    public void ProductManagementView_SearchFilterBar_IsResponsive()
    {
        var doc = LoadXaml("src/EdgeRetails.Desktop/Views/ProductManagementView.xaml");

        var dataGrid = doc.Descendants(XamlNs + "DataGrid")
            .FirstOrDefault(d => d.Attribute("ItemsSource")?.Value == "{Binding FilteredProducts}");
        Assert.NotNull(dataGrid);
        Assert.Equal("Auto", dataGrid.Attribute("ScrollViewer.HorizontalScrollBarVisibility")?.Value);

        // SearchBox column is flexible
        var cardGrid = doc.Descendants(XamlNs + "Border")
            .Where(b => b.Attribute("Style")?.Value == "{StaticResource Card.Default}")
            .SelectMany(b => b.Elements(XamlNs + "Grid"))
            .FirstOrDefault();
        Assert.NotNull(cardGrid);

        var colDefs = cardGrid.Element(XamlNs + "Grid.ColumnDefinitions")?.Elements(XamlNs + "ColumnDefinition").ToList();
        Assert.NotNull(colDefs);
        Assert.True(colDefs.Count >= 6);

        var searchCol = colDefs[0];
        Assert.Equal("*", searchCol.Attribute("Width")?.Value);
        Assert.Equal("220", searchCol.Attribute("MinWidth")?.Value);
        Assert.Equal("360", searchCol.Attribute("MaxWidth")?.Value);
    }

    [Fact]
    public void ProductManagementView_HeaderActions_WrapBelowHeading()
    {
        var doc = LoadXaml("src/EdgeRetails.Desktop/Views/ProductManagementView.xaml");
        var header = doc.Descendants(XamlNs + "Grid")
            .FirstOrDefault(g => g.Attribute("Margin")?.Value == "0,0,0,16");

        Assert.NotNull(header);
        var rows = header.Element(XamlNs + "Grid.RowDefinitions")?
            .Elements(XamlNs + "RowDefinition").ToList();
        Assert.NotNull(rows);
        Assert.Equal(2, rows.Count);

        var actions = header.Elements(XamlNs + "WrapPanel")
            .FirstOrDefault(p => p.Attribute("Grid.Row")?.Value == "1");
        Assert.NotNull(actions);
        var buttons = actions.Elements(XamlNs + "Button").ToArray();
        Assert.Equal(6, buttons.Length);
        Assert.Equal(new[]
        {
            "{Binding ManageCompaniesCommand}",
            "{Binding ManageCategoriesCommand}",
            "{Binding ManageUnitsCommand}",
            "{Binding MoreProductsCommand}",
            "{Binding RefreshCommand}",
            "{Binding AddProductCommand}"
        }, buttons.Select(button => button.Attribute("Command")?.Value));
        var more = Assert.Single(buttons, button => button.Attribute("Content")?.Value == "More products");
        Assert.Equal("{Binding HasMoreProducts}", more.Attribute("IsEnabled")?.Value);
    }

    [Fact]
    public void InventoryView_FiltersBar_UsesResponsiveWrapAnd36DIPGeometry()
    {
        var doc = LoadXaml("src/EdgeRetails.Desktop/Views/InventoryView.xaml");

        // Tabs are inside a WrapPanel
        var tabWrap = doc.Descendants(XamlNs + "WrapPanel")
            .FirstOrDefault(wp => wp.Descendants(XamlNs + "Button").Any(b => b.Attribute("Content")?.Value == "All Stock"));
        Assert.NotNull(tabWrap);

        // ComboBoxes declare 36 DIP height
        var filterCombos = doc.Descendants(XamlNs + "ComboBox")
            .Where(cb => cb.Attribute("ItemsSource")?.Value != null)
            .ToList();
        Assert.True(filterCombos.Count >= 3);
        foreach (var cb in filterCombos)
        {
            Assert.Equal("36", cb.Attribute("Height")?.Value);
        }

        var pageHeader = doc.Descendants(XamlNs + "Grid")
            .FirstOrDefault(g => g.Attribute("Margin")?.Value == "0,0,0,16");
        Assert.NotNull(pageHeader);
        var actionWrap = pageHeader.Elements(XamlNs + "WrapPanel")
            .FirstOrDefault(p => p.Attribute("Grid.Row")?.Value == "1");
        Assert.NotNull(actionWrap);
        Assert.Equal(3, actionWrap.Elements(XamlNs + "Button").Count());
    }

    [Fact]
    public void NewPurchaseView_InputGeometry_PreservesStandardContracts()
    {
        var doc = LoadXaml("src/EdgeRetails.Desktop/Views/NewPurchaseView.xaml");

        var headerCard = doc.Descendants(XamlNs + "Border")
            .FirstOrDefault(b => b.Attribute("Style")?.Value == "{StaticResource Card.Default}");
        Assert.NotNull(headerCard);

        // Assert no ad-hoc Height="35" or Padding="10,6"
        var textBoxes = headerCard.Descendants(XamlNs + "TextBox").ToList();
        Assert.True(textBoxes.Count >= 2);
        foreach (var tb in textBoxes)
        {
            Assert.Null(tb.Attribute("Height"));
            Assert.Null(tb.Attribute("Padding"));
        }

        var comboBoxes = headerCard.Descendants(XamlNs + "ComboBox").ToList();
        Assert.Single(comboBoxes);
        Assert.Null(comboBoxes[0].Attribute("Height"));

        var datePickers = headerCard.Descendants(XamlNs + "DatePicker").ToList();
        Assert.Single(datePickers);
        Assert.Null(datePickers[0].Attribute("Height"));
    }

    [Fact]
    public void MainWindow_And_Shell_EnforceDpiSnappingAndLayoutRounding()
    {
        var doc = LoadXaml("src/EdgeRetails.Desktop/MainWindow.xaml");

        var window = doc.Root;
        Assert.NotNull(window);
        Assert.Equal("True", window.Attribute("UseLayoutRounding")?.Value);
        Assert.Equal("True", window.Attribute("SnapsToDevicePixels")?.Value);
        Assert.Equal("1024", window.Attribute("MinWidth")?.Value);
        Assert.Equal("640", window.Attribute("MinHeight")?.Value);
    }
}
