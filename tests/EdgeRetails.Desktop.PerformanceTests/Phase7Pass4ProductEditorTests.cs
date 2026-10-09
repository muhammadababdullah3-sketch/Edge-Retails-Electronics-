using System.Reflection;
using System.IO;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Desktop.ViewModels;
using EdgeRetails.Domain.Catalog;

namespace EdgeRetails.Desktop.PerformanceTests;

// NEW_COVERAGE: executes the real WPF view model, without installed services or business data.
public sealed class Phase7Pass4ProductEditorTests
{
    [Theory]
    [InlineData(TrackingMode.IndividualPiece, false, false)]
    [InlineData(TrackingMode.IndividualPiece, true, false)]
    [InlineData(TrackingMode.Container, false, true)]
    [InlineData(TrackingMode.Container, false, false)]
    public void EditPreservesOptionalIdentityFlags(TrackingMode mode, bool serial, bool imei)
    {
        var product = Product(mode, serial, imei);
        var view = View(product);
        Assert.True(view.IsSerialized);
        Assert.Equal(serial, view.SerialTrackingEnabled);
        Assert.Equal(imei, view.ImeiTrackingEnabled);
        view.SelectedTrackingMode = mode == TrackingMode.Container ? TrackingMode.IndividualPiece : TrackingMode.Container;
        Assert.Equal(serial, view.SerialTrackingEnabled);
        Assert.Equal(imei, view.ImeiTrackingEnabled);
        view.SelectedTrackingMode = TrackingMode.Quantity;
        Assert.False(view.SerialTrackingEnabled);
        Assert.False(view.ImeiTrackingEnabled);
        Assert.False(view.IsSerialized);
        view.SelectedTrackingMode = TrackingMode.Serialized;
        Assert.True(view.SerialTrackingEnabled || view.ImeiTrackingEnabled);
        Assert.Equal(5, view.TrackingModes.Count);
    }

    [Fact]
    public void OffPageHistoricalSupplierLinkIsRetainedById()
    {
        var supplier = Guid.NewGuid();
        var product = Product(TrackingMode.Container, false, false) with
        {
            SupplierProducts = [new(Guid.NewGuid(), supplier, "Historical supplier", true, 7)]
        };
        var view = View(product);
        var row = Assert.Single(view.SupplierLinks);
        Assert.Equal(supplier, row.Supplier.Id);
        Assert.True(row.IsLinked);
    }

    [Fact]
    public async Task SerializedWithoutManufacturerPolicyCannotSubmit()
    {
        var service = DispatchProxy.Create<IBackendProductManagementService, ProductServiceProxy>();
        var proxy = (ProductServiceProxy)(object)service;
        var product = Product(TrackingMode.Serialized, false, false);
        var view = new ProductEditViewModel(product, Snapshot(product), service, new CapturingToast(), () => { }, () => Task.CompletedTask);
        await (Task)typeof(ProductEditViewModel).GetMethod("SaveAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null)!;
        Assert.True(view.HasValidationMessage);
        Assert.Equal(0, proxy.Mutations);
    }

    [Fact]
    public async Task LegacyAttributesAndOptionalPhysicalPolicyRoundTripIntoSave()
    {
        var service = DispatchProxy.Create<IBackendProductManagementService, ProductServiceProxy>();
        var proxy = (ProductServiceProxy)(object)service;
        var product = Product(TrackingMode.IndividualPiece, false, true) with { AttributesJson = "{\"legacy_vendor_field\":\"text\"}" };
        proxy.SavedProduct = product;
        var view = new ProductEditViewModel(product, Snapshot(product), service, new CapturingToast(), () => { }, () => Task.CompletedTask);
        await (Task)typeof(ProductEditViewModel).GetMethod("SaveAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null)!;
        Assert.Equal(1, proxy.Mutations);
        Assert.Equal(product.AttributesJson, proxy.Request!.AttributesJson);
        Assert.Equal(1, proxy.Request.AttributesSchemaVersion);
        Assert.Equal(TrackingMode.IndividualPiece, proxy.Request.TrackingMode);
        Assert.False(proxy.Request.SerialTrackingEnabled);
        Assert.True(proxy.Request.ImeiTrackingEnabled);
    }

    [Fact]
    public async Task RefreshFailureAfterCommittedSaveDoesNotInviteDuplicateSave()
    {
        var toast = new CapturingToast();
        var closed = false;
        var service = DispatchProxy.Create<IBackendProductManagementService, ProductServiceProxy>();
        var proxy = (ProductServiceProxy)(object)service;
        var product = Product(TrackingMode.Container, false, false);
        proxy.SavedProduct = product;
        var view = new ProductEditViewModel(product, Snapshot(product), service, toast,
            () => closed = true, () => Task.FromException(new IOException("Synthetic refresh failure")));
        var save = (Task)typeof(ProductEditViewModel).GetMethod("SaveAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null)!;
        await save;
        Assert.Equal(1, proxy.Mutations);
        Assert.True(closed);
        Assert.False(view.HasValidationMessage);
        Assert.DoesNotContain(toast.Captured, x => x.Tone == ToastTone.Danger);
        Assert.Contains(toast.Captured, x => x.Text.Contains("Product saved", StringComparison.Ordinal));
    }

    private static ProductEditViewModel View(BackendProductManagementItem p) => new(p, Snapshot(p),
        DispatchProxy.Create<IBackendProductManagementService, ProductServiceProxy>(), new CapturingToast(), () => { }, () => Task.CompletedTask);

    private static BackendProductManagementSnapshot Snapshot(BackendProductManagementItem p) => new([p],
        [new(p.CategoryId!.Value, "Category", "C")], [new(p.BaseUnitId, "Piece", "Pcs", 0, true)], []);

    private static BackendProductManagementItem Product(TrackingMode mode, bool serial, bool imei)
    {
        var unit = Guid.NewGuid();
        return new(Guid.NewGuid(), "TEST-PRODUCT", "Product", null, null, Guid.NewGuid(), "Category", unit, "Piece",
            mode, serial, imei, 1, 2, 0, 0, null, 1, true, 4,
            [new(Guid.NewGuid(), unit, "Piece", "Pcs", 1, true, true, true, true, true, true)], []);
    }

    public class ProductServiceProxy : DispatchProxy
    {
        public BackendProductManagementItem? SavedProduct;
        public int Mutations;
        public BackendProductCatalogRequest? Request;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "GetProductBySkuAsync" => (object)Task.FromResult<BackendProductManagementItem?>(null),
            "UpdateProductAsync" => Save((BackendProductCatalogRequest)args![2]!),
            "CreateProductAsync" => Save((BackendProductCatalogRequest)args![0]!),
            _ => throw new NotSupportedException(method.Name)
        };
        private Task<BackendProductManagementItem> Save(BackendProductCatalogRequest request) { Request = request; Mutations++; return Task.FromResult(SavedProduct!); }
    }

    private sealed class CapturingToast : IToastService
    {
        public System.Collections.ObjectModel.ReadOnlyObservableCollection<ToastMessage> Messages { get; } = new(new());
        public List<(string Text, ToastTone Tone)> Captured { get; } = [];
        public void Show(string message, ToastTone tone = ToastTone.Neutral, TimeSpan? duration = null) => Captured.Add((message, tone));
    }
}
