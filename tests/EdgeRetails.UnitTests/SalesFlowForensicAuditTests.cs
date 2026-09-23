namespace EdgeRetails.UnitTests;

public sealed class SalesFlowForensicAuditTests
{
    private static string SolutionRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "EdgeRetails.sln")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new InvalidOperationException("EdgeRetails.sln was not found.");
    }

    private static string DesktopFile(params string[] parts) =>
        Path.Combine([SolutionRoot(), "src", "EdgeRetails.Desktop", .. parts]);

    private static string ReadDesktop(params string[] parts) =>
        File.ReadAllText(DesktopFile(parts));

    [Fact]
    public void ModeSwitch_IsDirectAndNeverClearsPreparedCart()
    {
        var vm = ReadDesktop("ViewModels", "PosViewModel.cs");
        var view = ReadDesktop("Views", "PosView.xaml");

        var start = vm.IndexOf("public void RequestModeChange", StringComparison.Ordinal);
        var end = vm.IndexOf("public void AddToCart", start, StringComparison.Ordinal);
        var switchMethod = vm[start..end];

        Assert.Contains("Mode = targetMode;", switchMethod);
        Assert.DoesNotContain("CartItems.Clear", switchMethod);
        Assert.DoesNotContain("IsModeSwitchConfirmationOpen", vm);
        Assert.DoesNotContain("ConfirmModeSwitchCommand", vm);
        Assert.DoesNotContain("Clear &amp; Switch", view);
        Assert.DoesNotContain("Switch Sale Mode?", view);
    }

    [Fact]
    public void CustomerChange_UsesRealSavedCustomerPicker()
    {
        var vm = ReadDesktop("ViewModels", "PosViewModel.cs");
        var picker = ReadDesktop("ViewModels", "SaleCustomerPickerViewModel.cs");
        var dialog = ReadDesktop("Views", "Dialogs", "SaleCustomerPickerDialog.xaml");
        var app = ReadDesktop("App.xaml");

        Assert.Contains("_businessDirectory.Customers", vm);
        Assert.Contains("new SaleCustomerPickerViewModel", vm);
        Assert.Contains("ApplyCustomerSelection", vm);
        Assert.Contains("UseWalkInCommand", picker);
        Assert.Contains("customer.Phone.Contains", picker);
        Assert.Contains("customer.Name.Contains", picker);
        Assert.Contains("Walk-in Customer", dialog);
        Assert.Contains("SaleCustomerPickerViewModel", app);
        Assert.Contains("<dialogs:SaleCustomerPickerDialog />", app);
    }

    [Fact]
    public void SelectedCustomerNameAndPhoneFlowIntoCheckout()
    {
        var vm = ReadDesktop("ViewModels", "PosViewModel.cs");
        var checkout = ReadDesktop("ViewModels", "CompleteSaleViewModel.cs");

        Assert.Contains("CustomerName = customer?.Name ?? \"Walk-in Customer\"", vm);
        Assert.Contains("CustomerPhone = customer?.Phone ?? string.Empty", vm);
        Assert.Contains("customerName: CustomerName", vm);
        Assert.Contains("customerPhone: CustomerPhone", vm);
        Assert.Contains("RecipientName", checkout);
        Assert.Contains("RecipientSubtitle", checkout);
    }

    [Fact]
    public void CompleteSale_HasDistinctCashBankAndOtherBodies()
    {
        var view = ReadDesktop("Views", "Dialogs", "CompleteSaleDialog.xaml");
        var vm = ReadDesktop("ViewModels", "CompleteSaleViewModel.cs");

        Assert.Contains("Visibility=\"{Binding IsCash", view);
        Assert.Contains("Visibility=\"{Binding IsBank", view);
        Assert.Contains("Visibility=\"{Binding IsOther", view);
        Assert.Contains("Bank Reference / Transaction ID (optional)", vm);
        Assert.Contains("Payment Note / Reference (optional)", vm);
        Assert.Contains("Quick:", view);
        Assert.Contains("Bank Transfer", view);
        Assert.Contains("Other Payment", view);
    }

    [Fact]
    public void PaymentReference_IsPreservedOnRecordedSale()
    {
        var contract = ReadDesktop("Services", "ITransactionService.cs");
        var checkout = ReadDesktop("ViewModels", "CompleteSaleViewModel.cs");
        var demo = ReadDesktop("Services", "DemoTransactionService.cs");

        Assert.Contains("public string PaymentReference", contract);
        Assert.Contains("PaymentReference = PaymentReference", checkout);
        Assert.Contains("PaymentReference = request.PaymentReference", demo);
    }

    [Fact]
    public void RecipientBlockUsesResolvedCustomerIdentity()
    {
        var view = ReadDesktop("Views", "Dialogs", "CompleteSaleDialog.xaml");

        Assert.Contains("Text=\"RECIPIENT\"", view);
        Assert.Contains("Text=\"{Binding RecipientName}\"", view);
        Assert.Contains("Text=\"{Binding RecipientSubtitle}\"", view);
    }

    [Fact]
    public void CanonicalLogoIsUsedAcrossPrimaryBrandSurfaces()
    {
        var project = ReadDesktop("EdgeRetails.Desktop.csproj");
        var window = ReadDesktop("MainWindow.xaml");
        var sidebar = ReadDesktop("Controls", "AppSidebar.xaml");
        var login = ReadDesktop("Views", "LoginView.xaml");
        var setup = ReadDesktop("Views", "FirstSetupView.xaml");

        Assert.True(File.Exists(DesktopFile("Assets", "EdgeLogo.png")));
        Assert.True(File.Exists(DesktopFile("Assets", "EdgeRetails.ico")));
        Assert.True(File.Exists(DesktopFile("Assets", "EdgeAppIcon.png")));
        Assert.Contains("<ApplicationIcon>Assets\\EdgeRetails.ico</ApplicationIcon>", project);
        Assert.Contains("Icon=\"Assets/EdgeAppIcon.png\"", window);
        Assert.Contains("Source=\"/Assets/EdgeLogo.png\"", sidebar);
        Assert.Contains("Source=\"/Assets/EdgeLogo.png\"", login);
        Assert.Contains("Source=\"/Assets/EdgeLogo.png\"", setup);
    }

    [Fact]
    public void SalesFlowChangesRemainBackendAgnostic()
    {
        foreach (var source in new[]
        {
            ReadDesktop("ViewModels", "PosViewModel.cs"),
            ReadDesktop("ViewModels", "SaleCustomerPickerViewModel.cs"),
            ReadDesktop("ViewModels", "CompleteSaleViewModel.cs")
        })
        {
            Assert.DoesNotContain("DbContext", source);
            Assert.DoesNotContain("Npgsql", source);
            Assert.DoesNotContain("HttpClient", source);
            Assert.DoesNotContain("ConnectionString", source);
        }
    }
}
