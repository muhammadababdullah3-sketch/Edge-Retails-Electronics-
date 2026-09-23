namespace EdgeRetails.UnitTests;

public sealed class Sprint3ForensicAuditTests
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
        Path.Combine(new[] { SolutionRoot(), "src", "EdgeRetails.Desktop" }.Concat(parts).ToArray());

    private static string ReadDesktop(params string[] parts) =>
        File.ReadAllText(DesktopFile(parts));

    [Fact]
    public void Sprint3Navigation_MapsRealTransactionScreens()
    {
        var factory = ReadDesktop("Navigation", "PageViewModelFactory.cs");
        var app = ReadDesktop("App.xaml");

        Assert.Contains("NavigationTarget.SalesHistory => new SalesHistoryViewModel", factory);
        Assert.Contains("NavigationTarget.ThakaProjects => GetOrCreateThakaProjects()", factory);
        Assert.Contains("NavigationTarget.ThakaWorkspace => CreateThakaWorkspace()", factory);
        Assert.Contains("DataType=\"{x:Type viewModels:SalesHistoryViewModel}\"", app);
        Assert.Contains("DataType=\"{x:Type viewModels:ThakaProjectsViewModel}\"", app);
        Assert.Contains("DataType=\"{x:Type viewModels:ThakaWorkspaceViewModel}\"", app);
    }

    [Fact]
    public void NormalSale_PreviewStateIsDebugOnly_AndThakaMutationIsNotOwnedByPos()
    {
        var pos = ReadDesktop("ViewModels", "PosViewModel.cs");

        Assert.Contains("DemoTransactionService.Instance", pos);
        Assert.Contains("DemoRetailState.Instance", pos);
        Assert.Contains("_retailState.Products", pos);
        Assert.Contains("_retailState.ApplyLocalSaleStock", pos);
        Assert.DoesNotContain("_retailState.IssueMaterialBatch", pos);
        Assert.Contains("#if DEBUG", pos);
        Assert.Contains("Thaka material issuance belongs exclusively to Thaka Workspace.", pos);
        Assert.DoesNotContain("new(\"THK-01\"", pos);
    }

    [Fact]
    public void SalesHistory_UsesSameTransactionService()
    {
        var history = ReadDesktop("ViewModels", "SalesHistoryViewModel.cs");
        var sale = ReadDesktop("ViewModels", "PosViewModel.cs");

        Assert.Contains("DemoTransactionService.Instance", history);
        Assert.Contains("DemoTransactionService.Instance", sale);
        Assert.Contains("_transactionService.GetAllTransactionsAsync()", history);
        Assert.Contains("RefreshFromTransactionServiceAsync()", history);
        Assert.Contains("ITransactionService _transactionService", history);
    }

    [Fact]
    public void ProductStock_IsDecimalAndCartIsStockCapped()
    {
        var product = ReadDesktop("ViewModels", "PosProductItemViewModel.cs");
        var cart = ReadDesktop("ViewModels", "PosCartItemViewModel.cs");

        Assert.Contains("private decimal _stock", product);
        Assert.Contains("public decimal Stock", product);
        Assert.Contains("public string Unit", product);
        Assert.Contains("Math.Clamp(Math.Round(value, 2), 0m, Product.Stock)", cart);
        Assert.Contains("Quantity < Product.Stock", cart);
    }

    [Fact]
    public void ThakaFlows_UseOneSharedRetailState()
    {
        var projects = ReadDesktop("ViewModels", "ThakaProjectsViewModel.cs");
        var workspace = ReadDesktop("ViewModels", "ThakaWorkspaceViewModel.cs");
        var material = ReadDesktop("ViewModels", "ThakaAddMaterialViewModel.cs");
        var payment = ReadDesktop("ViewModels", "ThakaRecordPaymentViewModel.cs");
        var settlement = ReadDesktop("ViewModels", "ThakaFinalSettlementViewModel.cs");

        Assert.Contains("DemoRetailState.Instance", projects);
        Assert.Contains("DemoRetailState.Instance", material);
        Assert.Contains("DemoRetailState.Instance", payment);
        Assert.Contains("DemoRetailState.Instance", settlement);
        Assert.Contains("GetMaterialLedger(_project)", workspace);
        Assert.Contains("GetPaymentLedger(_project)", workspace);
        Assert.DoesNotContain("SeedMaterialLedger", workspace);
        Assert.DoesNotContain("SeedPaymentLedger", workspace);
    }

    [Fact]
    public void ThakaMaterialAndPayments_MutateSharedState()
    {
        var material = ReadDesktop("ViewModels", "ThakaAddMaterialViewModel.cs");
        var payment = ReadDesktop("ViewModels", "ThakaRecordPaymentViewModel.cs");
        var settlement = ReadDesktop("ViewModels", "ThakaFinalSettlementViewModel.cs");

        Assert.Contains("_retailState.IssueMaterial(", material);
        Assert.Contains("_retailState.RecordPayment(", payment);
        Assert.Contains("_retailState.SettleProject(", settlement);
    }
    [Fact]
    public void ThakaProjectButtons_UseParentPageCommand()
    {
        var view = ReadDesktop("Views", "ThakaProjectsView.xaml");

        Assert.Contains("DataContext.OpenWorkspaceCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}", view);
        Assert.Contains("DataContext.OpenWorkspaceCommand, RelativeSource={RelativeSource AncestorType=DataGrid}", view);
        Assert.Contains("CommandParameter=\"{Binding}\"", view);
    }

    [Fact]
    public void ThakaWorkspace_StatusBadgeUsesSemanticTone()
    {
        var view = ReadDesktop("Views", "ThakaWorkspaceView.xaml");

        Assert.Contains("<controls:StatusBadge", view);
        Assert.Contains("Text=\"{Binding StatusDisplay}\"", view);
        Assert.Contains("Tone=\"{Binding StatusTone}\"", view);
    }

    [Fact]
    public void Sprint3Sources_ContainNoKnownMojibake()
    {
        var names = new[]
        {
            "PosViewModel.cs",
            "SalesHistoryViewModel.cs",
            "SaleDetailViewModel.cs",
            "SalesReturnViewModel.cs",
            "ThakaProjectsViewModel.cs",
            "ThakaWorkspaceViewModel.cs",
            "ThakaAddMaterialViewModel.cs",
            "ThakaRecordPaymentViewModel.cs",
            "ThakaFinalSettlementViewModel.cs"
        };

        var violations = new List<string>();
        foreach (var name in names)
        {
            var content = ReadDesktop("ViewModels", name);
            if (content.Contains("Â") || content.Contains("â€") || content.Contains("Ã"))
            {
                violations.Add(name);
            }
        }

        Assert.True(
            violations.Count == 0,
            $"Mojibake found in Sprint 3 sources: {string.Join(", ", violations)}");
    }
}
