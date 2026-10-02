using EdgeRetails.Desktop.Services;
using EdgeRetails.Application.Production.Printing;
using EdgeRetails.Desktop.Production.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;

namespace EdgeRetails.Desktop.PerformanceTests;

public sealed class MasterProductLabelTests
{
    // NEW_COVERAGE: real product-label printing must have its own API/engine entrypoint, not a fabricated unit.
    [Fact]
    public void ProductLabelPrint_ExposesDistinctSupportedEntrypoint()
    {
        Assert.NotNull(typeof(RemotePhysicalStickerPrintService).GetMethod("PrintProductAsync"));
    }

    [Fact]
    public async Task ProductLabelPrint_UsesCommittedProductUnitBarcodeWithoutPhysicalUnitEngine()
    {
        var id = Guid.NewGuid();
        var document = new ProductLabelDocument(id, Guid.NewGuid(), "12W LED Bulb", "Philips", "12W", "PHB-LED12", "Piece", "BULB-12W", 500m, true);
        var handler = new ProductHttpHandler(document);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:7150") };
        using var api = new DesktopApiClient(http);
        var engine = new ProductEngine(new(true, null, null));
        var result = await new RemotePhysicalStickerPrintService(api, engine).PrintProductAsync(id, "Labels", true);
        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.Succeeded);
        Assert.Equal(document, engine.Document);
        Assert.Equal(1, engine.ProductCalls);
        Assert.Equal(0, engine.PhysicalCalls);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task ProductLabelPrint_PrinterUnavailableRetainsProductIdentityWithoutUsingPhysicalEngine()
    {
        var document = new ProductLabelDocument(Guid.NewGuid(), Guid.NewGuid(), "Cable", "Pakistan Cables", "2.5mm", "PCC-C25", "Metre", null, 175m);
        using var http = new HttpClient(new ProductHttpHandler(document)) { BaseAddress = new Uri("http://127.0.0.1:7150") };
        using var api = new DesktopApiClient(http);
        var engine = new ProductEngine(new(false, "print.printer_unavailable", "Printer unavailable."));
        var result = await new RemotePhysicalStickerPrintService(api, engine).PrintProductAsync(document.ProductUnitId, null, false);
        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.Succeeded);
        Assert.Equal("print.printer_unavailable", result.Value.ErrorCode);
        Assert.Null(engine.Document!.UnitBarcode);
        Assert.Equal(document.ProductUnitId, engine.Document.ProductUnitId);
        Assert.Equal(0, engine.PhysicalCalls);
    }

    [Fact]
    public void QuantityProductLabelWithoutUnitBarcodeDoesNotManufactureTrackingCode()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var document = new ProductLabelDocument(Guid.NewGuid(), Guid.NewGuid(), "2.5mm Cable", "Pakistan Cables", "2.5mm", "PCC-C25", "Metre", null, 175m);
                var page = WpfPhysicalStickerPrintEngine.CreateProductFixedDocument(document, 1).Pages.Single().Child;
                page.Measure(new Size(page.Width, page.Height));
                page.Arrange(new Rect(0, 0, page.Width, page.Height));
                var descendants = Descendants(page).ToArray();
                Assert.DoesNotContain(descendants, x => x is Canvas);
                Assert.Contains(descendants.OfType<TextBlock>(), x => x.Text == "Product: PCC-C25");
                Assert.Contains(descendants.OfType<TextBlock>(), x => x.Text == "Unit: Metre");
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }

    private sealed class ProductHttpHandler(ProductLabelDocument document) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (request.Method == HttpMethod.Get)
            {
                Assert.Equal($"/api/printing/product-labels/{document.ProductUnitId:D}", request.RequestUri!.AbsolutePath);
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(document) };
            }
            Assert.Equal("/api/printing/product-labels/receipts", request.RequestUri!.AbsolutePath);
            var receipt = await request.Content!.ReadFromJsonAsync<ProductLabelPrintReceipt>(cancellationToken);
            Assert.Equal(document.ProductUnitId, receipt!.ProductUnitId);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new StickerPrintReceiptResult(true, null)) };
        }
    }

    private sealed class ProductEngine(PrintJobResult result) : IPhysicalStickerPrintEngine, IProductLabelPrintEngine
    {
        public ProductLabelDocument? Document { get; private set; }
        public int ProductCalls { get; private set; }
        public int PhysicalCalls { get; private set; }
        public Task<PrintJobResult> PrintProductLabelAsync(ProductLabelDocument document, string? printerName = null, int copies = 1, CancellationToken cancellationToken = default)
        {
            ProductCalls++;
            Document = document;
            Assert.Equal(1, copies);
            return Task.FromResult(result);
        }
        public Task<PrintJobResult> PrintStickerAsync(PhysicalItemStickerDocument document, string? printerName = null, int copies = 1, CancellationToken cancellationToken = default)
        {
            PhysicalCalls++;
            throw new InvalidOperationException("A quantity/length product must not be represented as a physical inventory unit.");
        }
    }
}
