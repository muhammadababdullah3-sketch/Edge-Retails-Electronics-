using System.Collections.ObjectModel;
using System.Reflection;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Desktop.ViewModels;
using EdgeRetails.Domain.Catalog;

namespace EdgeRetails.Desktop.PerformanceTests;

public sealed class MasterPhysicalIntakeTests
{
    [Fact]
    public void PartialReceiptDoesNotCloseOutstandingIntake()
    {
        var viewModel = Create(existingCount: 1, purchasedQuantity: 3m);
        Assert.True(viewModel.HasExistingUnits);
        Assert.False(viewModel.IntakeCompleted);
        Assert.True(viewModel.CanExecuteIntake);
        Assert.Equal(2m, viewModel.EnteredQuantity);
    }

    [Fact]
    public void CompletedReceiptRemainsPrintableWithoutAnotherReceipt()
    {
        var viewModel = Create(existingCount: 3, purchasedQuantity: 3m);
        Assert.True(viewModel.IntakeCompleted);
        Assert.False(viewModel.CanExecuteIntake);
        Assert.Equal(3, viewModel.CommittedUnits.Count);
        Assert.True(viewModel.PrintAllCommand.CanExecute(null));
    }

    [Fact]
    public void FailedAuthorityLoadCannotSilentlyEnableReceipt()
    {
        var viewModel = Create(existingCount: 0, purchasedQuantity: 3m, failLoad: true);
        Assert.False(viewModel.CanExecuteIntake);
        Assert.True(viewModel.HasValidationMessage);
    }

    [Fact]
    public void ExactPieceIntakeRejectsFractionalQuantityBeforeMutation()
    {
        var viewModel = Create(existingCount: 0, purchasedQuantity: 3m);
        viewModel.EnteredQuantity = 1.5m;
        Assert.False(viewModel.CanExecuteIntake);
    }

    [Theory]
    [InlineData(TrackingMode.Quantity, 10, 4, 6)]
    [InlineData(TrackingMode.Length, 12.5, 4.25, 8.25)]
    public void BulkPendingReceiptUsesLotsNotUnitCount(TrackingMode mode, double ordered, double received, double pending)
    {
        var viewModel = Create(0, (decimal)ordered, mode: mode, receivedQuantity: (decimal)received);
        Assert.Equal((decimal)pending, viewModel.EnteredQuantity);
        Assert.True(viewModel.CanExecuteIntake);
        Assert.Empty(viewModel.CommittedUnits);
        Assert.Empty(viewModel.SerialRows);
        Assert.Contains($"Tracking policy: {mode}", viewModel.TrackingPolicyInfo);
    }

    [Fact]
    public void IntakeClearlyDisplaysFixedSupplierPurchaseProductAndTotals()
    {
        var viewModel = Create(1, 3m);
        Assert.Contains("Purchase: PO-1", viewModel.PurchaseInfo);
        Assert.Contains("Supplier: Jamalsons", viewModel.PurchaseInfo);
        Assert.Contains("SupplierCode: JA001", viewModel.PurchaseInfo);
        Assert.Contains("ProductCode: GFCC-18", viewModel.ProductInfo);
        Assert.Contains("Received: 1", viewModel.ReceiptProgressInfo);
        Assert.Contains("Pending: 2", viewModel.ReceiptProgressInfo);
    }

    [Fact]
    public void PieceIdentityCountUsesPurchaseUnitConversion()
    {
        var viewModel = Create(0, 3m, factor: 2m);
        Assert.Equal(6, viewModel.SerialRows.Count);
        viewModel.EnteredQuantity = 0.5m;
        Assert.True(viewModel.CanExecuteIntake);
        Assert.Single(viewModel.SerialRows);
    }

    [Fact]
    public void ContainerIdentityCountUsesContainersNotTheirContents()
    {
        var viewModel = Create(0, 3m, mode: TrackingMode.Container, factor: 100m);
        viewModel.EnteredQuantity = 0.5m;
        Assert.False(viewModel.CanExecuteIntake);
        viewModel.EnteredQuantity = 2m;
        Assert.True(viewModel.CanExecuteIntake);
        Assert.Empty(viewModel.SerialRows);
    }

    [Fact]
    public void MissingCanonicalReceiptMetadataFailsClosed()
    {
        var viewModel = Create(0, 3m, omitAuthority: true);
        Assert.False(viewModel.CanExecuteIntake);
        Assert.True(viewModel.HasValidationMessage);
    }

    private static PhysicalIntakeViewModel Create(int existingCount, decimal purchasedQuantity, bool failLoad = false,
        TrackingMode mode = TrackingMode.Serialized, decimal? receivedQuantity = null, decimal factor = 1m,
        bool omitAuthority = false)
    {
        var itemId = Guid.NewGuid();
        var product = new PosProductItemViewModel("product", "GFC fan", "GFCC-18", "GFC", "Fans", 0m, 6000m,
            backendProductId: Guid.NewGuid(), backendProductUnitId: Guid.NewGuid(), isSerialized: mode == TrackingMode.Serialized,
            factorToBaseUnit: factor, trackingMode: mode);
        var item = new PurchaseItemRecord
        {
            BackendPurchaseItemId = itemId, BackendProductUnitId = product.BackendProductUnitId,
            Product = product, PurchasedQuantity = purchasedQuantity,
            ReceivedQuantity = omitAuthority ? null : receivedQuantity ?? existingCount, Cost = 4000m
        };
        var purchase = new PurchaseRecord
        {
            BackendPurchaseId = Guid.NewGuid(), BackendSupplierId = Guid.NewGuid(),
            PurchaseNumber = "PO-1", InvoiceNumber = "INV-1", Supplier = "Jamalsons", SupplierCode = omitAuthority ? null : "JA001", Items = [item]
        };
        var existing = Enumerable.Range(1, existingCount).Select(i => new CommittedInventoryUnitDto(
            Guid.NewGuid(), $"JA001-GFCC-18-{i:D6}", i, null, null, null, 4000m)).ToArray();
        var backend = DispatchProxy.Create<IBackendPurchasingInventoryService, BackendProxy>();
        var proxy = (BackendProxy)(object)backend;
        proxy.Purchase = purchase;
        proxy.Existing = existing;
        proxy.FailLoad = failLoad;
        return new PhysicalIntakeViewModel(purchase, item, new Dialog(), new Toast(), backend, () => Guid.NewGuid());
    }

    public class BackendProxy : DispatchProxy
    {
        public PurchaseRecord Purchase = null!;
        public IReadOnlyList<CommittedInventoryUnitDto> Existing = [];
        public bool FailLoad;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            nameof(IBackendPurchasingInventoryService.GetUnitsForPurchaseItemAsync) => FailLoad
                ? Task.FromException<IReadOnlyList<CommittedInventoryUnitDto>>(new InvalidOperationException("Load failed"))
                : Task.FromResult(Existing),
            nameof(IBackendPurchasingInventoryService.GetPurchaseAsync) => FailLoad
                ? Task.FromException<PurchaseRecord?>(new InvalidOperationException("Load failed"))
                : Task.FromResult<PurchaseRecord?>(Purchase),
            _ => throw new NotSupportedException(method?.Name)
        };
    }

    private sealed class Dialog : IDialogService
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public bool IsOpen => false;
        public object? Content => null;
        public void Show(object content) { }
        public void Close() { }
    }

    private sealed class Toast : IToastService
    {
        public ReadOnlyObservableCollection<ToastMessage> Messages { get; } = new(new ObservableCollection<ToastMessage>());
        public void Show(string message, ToastTone tone = ToastTone.Neutral, TimeSpan? duration = null) { }
    }
}
