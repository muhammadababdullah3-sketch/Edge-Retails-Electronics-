using System.Collections.ObjectModel;
using System.Reflection;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Desktop.ViewModels;
using EdgeRetails.Domain.Catalog;

namespace EdgeRetails.Desktop.PerformanceTests;

// NEW_COVERAGE: real NewPurchaseViewModel with controllable backend responses ignoring cancellation.
public sealed class Phase7Pass4LookupTests
{
    [Fact]
    public async Task RapidAThenB_IgnoresStaleProductAndSupplierResponses()
    {
        var (view, proxy, _) = View();
        await view.SearchProductsAsync(); await view.SearchSuppliersAsync();
        var productA = new TaskCompletionSource<PurchaseCatalogPageDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var supplierA = new TaskCompletionSource<BackendSupplierPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var productStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var supplierStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken productToken = default; CancellationToken supplierToken = default;
        var b = Product("B"); var supplierB = new BackendSupplierOption(Guid.NewGuid(), "B", "City");
        proxy.Catalog = (query, token) => {
            if (query.Search == "A") { productToken = token; productStarted.TrySetResult(); return productA.Task; }
            return Task.FromResult(new PurchaseCatalogPageDto([b], null, null));
        };
        proxy.Suppliers = (search, cursor, token) => {
            if (search == "A") { supplierToken = token; supplierStarted.TrySetResult(); return supplierA.Task; }
            return Task.FromResult(new BackendSupplierPage([supplierB], null, null));
        };
        view.SearchText = "A"; var oldProduct = view.SearchProductsAsync();
        view.SupplierSearchText = "A"; var oldSupplier = view.SearchSuppliersAsync();
        await Task.WhenAll(productStarted.Task, supplierStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));
        view.SearchText = "B"; await view.SearchProductsAsync();
        view.SupplierSearchText = "B"; await view.SearchSuppliersAsync();
        Assert.True(productToken.IsCancellationRequested); Assert.True(supplierToken.IsCancellationRequested);
        productA.SetResult(new([Product("A")], null, null));
        supplierA.SetResult(new([new(Guid.NewGuid(), "A")], null, null));
        await Task.WhenAll(oldProduct, oldSupplier);
        Assert.Equal(b.ProductUnitId, Assert.Single(view.FilteredProducts).BackendProductUnitId);
        Assert.Contains(supplierB.Id.ToString("D"), Assert.Single(view.Suppliers));
    }

    [Fact]
    public async Task PagingAndSearchRetainSupplierProductUnitAndDraft_DuplicateNamesRemainDistinct()
    {
        var (view, proxy, _) = View();
        await view.SearchProductsAsync(); await view.SearchSuppliersAsync();
        var first = Product("First"); var second = Product("Second");
        var supplier1 = new BackendSupplierOption(Guid.NewGuid(), "Same", "Same city");
        var supplier2 = new BackendSupplierOption(Guid.NewGuid(), "Same", "Same city");
        proxy.Catalog = (q, _) => Task.FromResult(q.AfterProductId is null ? new PurchaseCatalogPageDto([first], first.Name, first.ProductId) : new([second], null, null));
        proxy.Suppliers = (_, cursor, _) => Task.FromResult(cursor is null ? new BackendSupplierPage([supplier1], supplier1.Name, supplier1.Id) : new([supplier2], null, null));
        await view.SearchProductsAsync(); await view.SearchSuppliersAsync();
        view.SelectedSupplier = Assert.Single(view.Suppliers);
        view.SelectedProduct = Assert.Single(view.FilteredProducts);
        view.AddSelectedProductCommand.Execute(null);
        var draft = Assert.Single(view.Lines); draft.Quantity = 7;
        await view.SearchProductsAsync(true); await view.SearchSuppliersAsync(true);
        Assert.Equal(2, view.FilteredProducts.Count); Assert.Equal(2, view.Suppliers.Count);
        Assert.Equal(supplier1.Id, view.SelectedSupplierId);
        Assert.Equal(first.ProductUnitId, view.SelectedProduct.BackendProductUnitId);
        proxy.Catalog = (_, _) => Task.FromResult(new PurchaseCatalogPageDto([second], null, null));
        proxy.Suppliers = (_, _, _) => Task.FromResult(new BackendSupplierPage([supplier2], null, null));
        view.SearchText = "Second"; await view.SearchProductsAsync();
        view.SupplierSearchText = "other"; await view.SearchSuppliersAsync();
        Assert.Equal(supplier1.Id, view.SelectedSupplierId);
        Assert.Equal(first.ProductUnitId, view.SelectedProduct.BackendProductUnitId);
        Assert.Same(draft, Assert.Single(view.Lines)); Assert.Equal(7, draft.Quantity);
        Assert.Equal(73m, view.SelectedProduct.Stock);
    }

    [Fact]
    public async Task DeferredPhysicalOrderSavesSupplierIdWithoutManufacturerIds_AndDifferentUnitCannotMerge()
    {
        var saved = new TaskCompletionSource<PurchaseRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (view, proxy, toast) = View(saved);
        await view.SearchProductsAsync(); await view.SearchSuppliersAsync();
        var physical = Product("Physical") with { IsSerialized = true, SerialTrackingEnabled = true, TrackingMode = TrackingMode.Serialized };
        var supplier = new BackendSupplierOption(Guid.NewGuid(), "Same", "City");
        proxy.Catalog = (_, _) => Task.FromResult(new PurchaseCatalogPageDto([physical], null, null));
        proxy.Suppliers = (_, _, _) => Task.FromResult(new BackendSupplierPage([supplier], null, null));
        await view.SearchProductsAsync(); await view.SearchSuppliersAsync();
        view.SelectedSupplier = Assert.Single(view.Suppliers); view.InvoiceNumber = "ORDER";
        view.SelectedProduct = Assert.Single(view.FilteredProducts); view.AddSelectedProductCommand.Execute(null);
        Assert.False(view.RequiresSerializedIntake); Assert.True(view.CanSave);
        // Same display symbol must not merge distinct ProductUnit IDs.
        var alternate = physical with { ProductUnitId = Guid.NewGuid(), FactorToBaseUnit = 2 };
        proxy.Catalog = (_, _) => Task.FromResult(new PurchaseCatalogPageDto([alternate], null, null));
        await view.SearchProductsAsync();
        view.SelectedProduct = view.FilteredProducts.Single(x => x.BackendProductUnitId == alternate.ProductUnitId);
        view.AddSelectedProductCommand.Execute(null);
        Assert.Equal(1, Assert.Single(view.Lines).Quantity);
        view.SavePurchaseCommand.Execute(null);
        await saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(supplier.Id, proxy.SavedSupplierId); Assert.Single(proxy.SavedLines!);
        Assert.Empty(proxy.SavedLines![0].SerializedIdentities);
        Assert.Contains(toast.Captured, x => x.Tone == ToastTone.Success && x.Text.Contains("Purchase order"));
        Assert.DoesNotContain(toast.Captured, x => x.Tone == ToastTone.Success && x.Text.Contains("Stock updated"));
    }

    [Fact]
    public async Task NewQueryClearsOldCursorBeforeDebounce_AndRefreshesSelectedOffPageStock()
    {
        var (view, proxy, _) = View();
        await view.SearchProductsAsync(); await view.SearchSuppliersAsync();
        var selected = Product("Selected");
        var other = Product("Other");
        var supplier = new BackendSupplierOption(Guid.NewGuid(), "Supplier");
        var queries = new List<PurchaseCatalogPageQuery>();
        var supplierCursors = new List<Guid?>();
        proxy.Catalog = (q, _) => {
            queries.Add(q);
            if (q.ProductId == selected.ProductId)
            {
                return Task.FromResult(new PurchaseCatalogPageDto([selected with { SellableStock = 12m }], null, null));
            }
            return Task.FromResult(q.Search == "Other" ? new PurchaseCatalogPageDto([other], null, null)
                : new PurchaseCatalogPageDto([selected], selected.Name, selected.ProductId));
        };
        proxy.Suppliers = (_, cursor, _) => {
            supplierCursors.Add(cursor);
            return Task.FromResult(new BackendSupplierPage([supplier], supplier.Name, supplier.Id));
        };
        await view.SearchProductsAsync(); await view.SearchSuppliersAsync();
        view.SelectedProduct = Assert.Single(view.FilteredProducts);
        view.AddSelectedProductCommand.Execute(null);
        var draft = Assert.Single(view.Lines); draft.Quantity = 4; draft.Cost = 18;
        view.SelectedSupplier = Assert.Single(view.Suppliers);
        Assert.True(view.HasMoreProducts); Assert.True(view.HasMoreSuppliers);
        queries.Clear(); supplierCursors.Clear();
        view.SearchText = "Other"; view.SupplierSearchText = "Other";
        Assert.False(view.HasMoreProducts); Assert.False(view.HasMoreSuppliers);
        await view.SearchProductsAsync(true); await view.SearchSuppliersAsync(true);
        Assert.Empty(queries); Assert.Empty(supplierCursors);
        await view.SearchProductsAsync(); await view.SearchSuppliersAsync();
        Assert.All(queries, q => Assert.Null(q.AfterProductId));
        Assert.All(supplierCursors, cursor => Assert.Null(cursor));
        Assert.Contains(queries, q => q.ProductId == selected.ProductId && q.IncludeInactive);
        Assert.Equal(selected.ProductUnitId, view.SelectedProduct.BackendProductUnitId);
        Assert.Equal(12m, view.SelectedProduct.Stock);
        Assert.Equal(12m, draft.Product.Stock);
        Assert.Same(draft, Assert.Single(view.Lines));
        Assert.Equal(4m, draft.Quantity); Assert.Equal(18m, draft.Cost);
        Assert.Equal(supplier.Id, view.SelectedSupplierId);
        view.Dispose();
    }

    [Fact]
    public async Task ImmediateSerializedIntakeRequiresIdentities_ThenSubmitsExactIdsAndCommittedStockResult()
    {
        var saved = new TaskCompletionSource<PurchaseRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (view, proxy, toast) = View(saved);
        proxy.ReceivesStockImmediately = true;
        await view.SearchProductsAsync();
        await view.SearchSuppliersAsync();
        var physical = Product("Immediate") with
        {
            IsSerialized = true,
            SerialTrackingEnabled = true,
            TrackingMode = TrackingMode.Serialized
        };
        var supplier = new BackendSupplierOption(Guid.NewGuid(), "Supplier", "City");
        proxy.Catalog = (_, _) => Task.FromResult(new PurchaseCatalogPageDto([physical], null, null));
        proxy.Suppliers = (_, _, _) => Task.FromResult(new BackendSupplierPage([supplier], null, null));
        await view.SearchProductsAsync();
        await view.SearchSuppliersAsync();
        view.SelectedSupplier = Assert.Single(view.Suppliers);
        view.InvoiceNumber = "IMMEDIATE";
        view.SelectedProduct = Assert.Single(view.FilteredProducts);
        view.AddSelectedProductCommand.Execute(null);
        var line = Assert.Single(view.Lines);
        Assert.True(view.RequiresSerializedIntake);
        Assert.False(view.CanSave);
        Assert.False(view.SavePurchaseCommand.CanExecute(null));
        view.SavePurchaseCommand.Execute(null);
        Assert.Equal(0, proxy.Submissions);
        Assert.Empty(proxy.SavedLines ?? []);
        var identity = new BackendSerializedIdentityInput("SERIAL-EXACT", null, null);
        line.SetSerializedIdentities([identity]);
        Assert.True(view.CanSave);
        Assert.True(view.SavePurchaseCommand.CanExecute(null));
        view.SavePurchaseCommand.Execute(null);
        var record = await saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(record.StockReceivedImmediately);
        Assert.Equal(1, proxy.Submissions);
        Assert.Equal(supplier.Id, proxy.SavedSupplierId);
        Assert.Equal(physical.ProductUnitId, Assert.Single(proxy.SavedLines!).Product.BackendProductUnitId);
        Assert.Equal(identity, Assert.Single(proxy.SavedLines![0].SerializedIdentities));
        Assert.Contains(toast.Captured, x => x.Tone == ToastTone.Success && x.Text.Contains("Stock updated"));
        Assert.DoesNotContain(toast.Captured, x => x.Tone == ToastTone.Success && x.Text.Contains("Purchase order"));
    }

    [Fact]
    public async Task SameProductAndSameProductUnitExplicitlyMergeByAddingQuantity()
    {
        var (view, proxy, _) = View();
        await view.SearchProductsAsync();
        await view.SearchSuppliersAsync();
        var product = Product("Merge");
        proxy.Catalog = (_, _) => Task.FromResult(new PurchaseCatalogPageDto([product], null, null));
        await view.SearchProductsAsync();
        view.SelectedProduct = Assert.Single(view.FilteredProducts);
        view.AddSelectedProductCommand.Execute(null);
        var line = Assert.Single(view.Lines);
        line.Quantity = 3m;
        view.AddSelectedProductCommand.Execute(null);
        Assert.Same(line, Assert.Single(view.Lines));
        Assert.Equal(4m, line.Quantity);
        Assert.Equal(product.ProductId, line.Product.BackendProductId);
        Assert.Equal(product.ProductUnitId, line.Product.BackendProductUnitId);
        view.Dispose();
    }

    private static PurchaseCatalogProductDto Product(string name) => new(Guid.NewGuid(), Guid.NewGuid(), name,
        "SKU-" + name, "Category", "Piece", 73m, 1m, 2m, 1m, false, false, false);
    private static (NewPurchaseViewModel View, LookupProxy Proxy, Toast Toast) View(TaskCompletionSource<PurchaseRecord>? saved = null)
    {
        var backend = DispatchProxy.Create<ILookupBackend, LookupProxy>();
        var proxy = (LookupProxy)(object)backend; var toast = new Toast();
        return (new NewPurchaseViewModel(toast, saved: p => saved?.TrySetResult(p), backendService: backend), proxy, toast);
    }
    public interface ILookupBackend : IBackendPurchasingInventoryService, IBackendPurchaseLookupService { }
    public class LookupProxy : DispatchProxy
    {
        public Func<PurchaseCatalogPageQuery, CancellationToken, Task<PurchaseCatalogPageDto>> Catalog = (_, _) => Task.FromResult(new PurchaseCatalogPageDto([], null, null));
        public Func<string?, Guid?, CancellationToken, Task<BackendSupplierPage>> Suppliers = (_, _, _) => Task.FromResult(new BackendSupplierPage([], null, null));
        public Guid? SavedSupplierId; public IReadOnlyList<PurchaseDraftLine>? SavedLines;
        public bool ReceivesStockImmediately;
        public int Submissions;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch {
            "get_ReceivesStockImmediately" => ReceivesStockImmediately,
            "GetCatalogPageAsync" => Catalog((PurchaseCatalogPageQuery)args![0]!, (CancellationToken)args[1]!),
            "GetSupplierPageAsync" => Suppliers((string?)args![0], (Guid?)args[3], (CancellationToken)args[4]!),
            "CreatePurchaseAsync" => Save(args!),
            _ => throw new NotSupportedException(method.Name)
        };
        private Task<PurchaseRecord> Save(object?[] args) {
            Submissions++;
            SavedSupplierId = (Guid)args[0]!; SavedLines = (IReadOnlyList<PurchaseDraftLine>)args[5]!;
            return Task.FromResult(new PurchaseRecord { PurchaseNumber = "PO", Supplier = "Synthetic supplier", InvoiceNumber = "TEST", Items = [], StockReceivedImmediately = ReceivesStockImmediately });
        }
    }
    private sealed class Toast : IToastService
    {
        public ReadOnlyObservableCollection<ToastMessage> Messages { get; } = new(new());
        public List<(string Text, ToastTone Tone)> Captured { get; } = [];
        public void Show(string message, ToastTone tone = ToastTone.Neutral, TimeSpan? duration = null) => Captured.Add((message, tone));
    }
}
