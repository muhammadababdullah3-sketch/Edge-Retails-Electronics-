using System.Xml.Linq;

namespace EdgeRetails.UnitTests;

public sealed class RecoveryUiContractTests
{
    [Fact]
    public void Owner_and_pin_inputs_center_their_content()
    {
        var document = XDocument.Load(RecoverySource("MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        foreach (var name in new[] { "OwnerPicker", "NewPin", "ConfirmPin" })
        {
            var control = Assert.Single(document.Descendants(),
                element => (string?)element.Attribute(xaml + "Name") == name);
            Assert.Equal("Center", (string?)control.Attribute("VerticalContentAlignment"));
            Assert.Equal("32", (string?)control.Attribute("Height"));
        }

        Assert.Equal(2, document.Descendants(presentation + "PasswordBox").Count());
    }

    [Fact]
    public void Successful_recovery_shows_a_confirmation_after_pin_inputs_are_cleared()
    {
        var source = File.ReadAllText(RecoverySource("MainWindow.xaml.cs"));

        Assert.Contains("if (response.IsSuccessStatusCode)", source, StringComparison.Ordinal);
        Assert.Contains("recoverySucceeded = true;", source, StringComparison.Ordinal);
        Assert.Contains("NewPin.Clear();", source, StringComparison.Ordinal);
        Assert.Contains("ConfirmPin.Clear();", source, StringComparison.Ordinal);
        Assert.Contains("if (recoverySucceeded)", source, StringComparison.Ordinal);
        Assert.Contains("MessageBox.Show(this,", source, StringComparison.Ordinal);
        Assert.Contains("\"PIN recovery completed\"", source, StringComparison.Ordinal);
        Assert.True(source.IndexOf("MessageBox.Show(this,", StringComparison.Ordinal) >
                    source.LastIndexOf("ConfirmPin.Clear();", StringComparison.Ordinal));
    }

    private static string RecoverySource(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EdgeRetails.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "src", "EdgeRetails.Recovery", name);
    }
}
