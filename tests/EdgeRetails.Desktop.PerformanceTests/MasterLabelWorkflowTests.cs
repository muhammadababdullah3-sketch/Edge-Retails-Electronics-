using EdgeRetails.Desktop.ViewModels;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Production.Printing;
using EdgeRetails.Domain.Catalog;
using System.Reflection;
using System.Collections.ObjectModel;

namespace EdgeRetails.Desktop.PerformanceTests;

public sealed class MasterLabelWorkflowTests
{
    // NEW_COVERAGE: production services must be reachable from normal Desktop workflows.
    [Fact]
    public void CommittedIntakeExposesAllSelectedAndReprintPdfCommands()
    {
        Assert.NotNull(typeof(PhysicalIntakeViewModel).GetProperty("ExportAllPdfCommand"));
        Assert.NotNull(typeof(PhysicalIntakeViewModel).GetProperty("ExportSelectedPdfCommand"));
        Assert.NotNull(typeof(PhysicalIntakeViewModel).GetProperty("ReprintSelectedPdfCommand"));
    }

    [Fact]
    public void ProductDetailExposesRealProductLabelPrintReprintAndExportCommands()
    {
        Assert.NotNull(typeof(ProductDetailViewModel).GetProperty("PrintProductLabelCommand"));
        Assert.NotNull(typeof(ProductDetailViewModel).GetProperty("ReprintProductLabelCommand"));
        Assert.NotNull(typeof(ProductDetailViewModel).GetProperty("ExportProductLabelPdfCommand"));
    }

    [Fact]
    public void ProductPrintUsesExistingUnitAndReportsUnrecordedAuditWithoutResubmission()
    {
        var service = new Labels { PrintResult = new(true, null, null, AuditPersisted: false) };
        var product = Product();
        using var view = Detail(product, service);
        view.PrintProductLabelCommand.Execute(null);
        Assert.Equal(product.BackendProductUnitId, service.PrintedUnit);
        Assert.Equal(1, service.PrintCalls);
        Assert.Contains("audit receipt could not be recorded", view.LabelStatusMessage);
        view.ReprintProductLabelCommand.Execute(null);
        Assert.True(service.IsReprint);
        Assert.Equal(2, service.PrintCalls); // Only the explicit user reprint invokes it again.
    }

    [Fact]
    public void ProductPdfExportNeverInventsAnExactUnit()
    {
        var service = new Labels();
        var product = Product();
        using var view = Detail(product, service);
        view.ExportProductLabelPdfCommand.Execute(null);
        Assert.Empty(service.ExactIds);
        Assert.Equal(new[] { product.BackendProductUnitId!.Value }, service.ProductIds);
        Assert.Equal(0, service.PrintCalls);
        Assert.Contains("saved", view.LabelStatusMessage);
    }

    [Fact]
    public void CancelledFolderSelectionDoesNotCallExport()
    {
        var service = new Labels();
        using var view = Detail(Product(), service, () => null);
        view.ExportProductLabelPdfCommand.Execute(null);
        Assert.Equal(0, service.ExportCalls);
        Assert.True(view.ExportProductLabelPdfCommand.CanExecute(null));
    }

    [Fact]
    public void IntakeSelectionAndReprintPreserveExactlyTheCommittedIds()
    {
        var service = new Labels();
        var view = Intake(service);
        var all = view.CommittedUnits.Select(unit => unit.Id).ToArray();
        view.CommittedUnits[1].IsSelected = false;
        view.ExportSelectedPdfCommand.Execute(null);
        Assert.Equal(new[] { all[0] }, service.ExactIds);
        Assert.Empty(service.ProductIds);
        view.ReprintSelectedPdfCommand.Execute(null);
        Assert.True(service.IsReprint);
        Assert.Equal(new[] { all[0] }, service.ExactIds);
        view.ExportAllPdfCommand.Execute(null);
        Assert.False(service.IsReprint);
        Assert.Equal(all, service.ExactIds);
        Assert.Equal(all, view.CommittedUnits.Select(unit => unit.Id));
    }

    [Fact]
    public void QuantityIntakeExportsProductUnitOnlyAfterReceiptAndNeverCreatesTrackingUnits()
    {
        var service = new Labels();
        var view = Intake(service, bulk: true);
        Assert.Empty(view.CommittedUnits);
        Assert.True(view.ExportAllPdfCommand.CanExecute(null));
        view.ExportAllPdfCommand.Execute(null);
        Assert.Empty(service.ExactIds);
        Assert.Single(service.ProductIds);
        Assert.Empty(view.CommittedUnits);
    }

    [Fact]
    public void IntakeExportFailureKeepsCommittedIdentitiesAndShowsRetryContext()
    {
        var service = new Labels { FailExport = true };
        var view = Intake(service);
        var before = view.CommittedUnits.Select(unit => (unit.Id, unit.ItemSequence, unit.TrackingCode)).ToArray();
        view.ExportAllPdfCommand.Execute(null);
        Assert.Equal(before, view.CommittedUnits.Select(unit => (unit.Id, unit.ItemSequence, unit.TrackingCode)));
        Assert.Contains("remain committed", view.PrintStatusMessage);
        Assert.True(view.ExportAllPdfCommand.CanExecute(null));
        Assert.Equal(0, service.PrintCalls);
    }

    private static PosProductItemViewModel Product(TrackingMode mode = TrackingMode.IndividualPiece) => new("product", "GFC Fan", "GFCC-18", "GFC", "Fans", 2m, 6000m,
        backendProductId: Guid.NewGuid(), backendProductUnitId: Guid.NewGuid(), trackingMode: mode);

    private static ProductDetailViewModel Detail(PosProductItemViewModel product, Labels service, Func<string?>? picker = null) =>
        new(product, new Dialog(), new Toast(), () => { }, labelService: service, chooseExportFolder: picker ?? (() => "owned-label-fixture"));

    private static PhysicalIntakeViewModel Intake(Labels service, bool bulk = false)
    {
        var product = Product(bulk ? TrackingMode.Quantity : TrackingMode.IndividualPiece);
        var item = new PurchaseItemRecord { BackendPurchaseItemId = Guid.NewGuid(), BackendProductUnitId = product.BackendProductUnitId,
            Product = product, PurchasedQuantity = 2m, ReceivedQuantity = 2m, Cost = 4000m };
        var purchase = new PurchaseRecord { BackendPurchaseId = Guid.NewGuid(), BackendSupplierId = Guid.NewGuid(),
            PurchaseNumber = "PO-1", InvoiceNumber = "INV-1", Supplier = "Jamalsons", SupplierCode = "JA001", Items = [item] };
        var backend = DispatchProxy.Create<IBackendPurchasingInventoryService, MasterPhysicalIntakeTests.BackendProxy>();
        var proxy = (MasterPhysicalIntakeTests.BackendProxy)(object)backend;
        proxy.Purchase = purchase;
        proxy.Existing = bulk ? [] : Enumerable.Range(1, 2).Select(i => new CommittedInventoryUnitDto(
            Guid.NewGuid(), $"JA001-GFCC-18-{i:D6}", i, null, null, null, 4000m)).ToArray();
        return new(purchase, item, new Dialog(), new Toast(), backend, () => Guid.NewGuid(),
            labelService: service, chooseExportFolder: () => "owned-label-fixture");
    }

    private sealed class Labels : IBackendLabelService
    {
        public int PrintCalls;
        public int ExportCalls;
        public Guid PrintedUnit;
        public bool IsReprint;
        public bool FailExport;
        public IReadOnlyList<Guid> ExactIds = [];
        public IReadOnlyList<Guid> ProductIds = [];
        public PrintJobResult PrintResult = new(true, null, null);
        public Task<Result<PrintJobResult>> PrintProductLabelAsync(Guid productUnitId, string? printerName,
            bool isReprint, CancellationToken cancellationToken = default)
        {
            PrintCalls++; PrintedUnit = productUnitId; IsReprint = isReprint;
            return Task.FromResult(Result<PrintJobResult>.Success(PrintResult));
        }
        public Task<Result<IReadOnlyList<string>>> ExportLabelPdfAsync(IReadOnlyList<Guid> inventoryUnitIds,
            IReadOnlyList<Guid> productUnitIds, string outputDirectory, bool isReprint = false, CancellationToken cancellationToken = default)
        {
            ExportCalls++; ExactIds = inventoryUnitIds.ToArray(); ProductIds = productUnitIds.ToArray(); IsReprint = isReprint;
            return Task.FromResult(FailExport
                ? Result<IReadOnlyList<string>>.Failure("printing.pdf_export_failed", "Owned fixture failure")
                : Result<IReadOnlyList<string>>.Success(["owned-label-fixture/Pilot_Label_Manifest.pdf"]));
        }
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
