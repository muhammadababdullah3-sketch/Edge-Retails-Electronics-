using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EdgeRetails.Application.Production.Printing;
using EdgeRetails.Desktop.Production.Printing;

namespace EdgeRetails.Desktop.PerformanceTests;

// NEW_COVERAGE: required content and barcode/page containment for real tracking-code lengths.
public sealed class Phase3StickerLayoutTests
{
    [Fact]
    public void Sticker_ContentAndBarcodeFitInsidePage() => OnSta(() =>
    {
        var document = new PhysicalItemStickerDocument(Guid.NewGuid(), "DE1-GFF-FAN18-000001", "GFC", "Pedestal Fan", "18 inch", "GFF-FAN18", 1, "SERIAL-1", null, null, "Edge Retails", 15000m, false, DateTimeOffset.UnixEpoch);
        var visual = WpfPhysicalStickerPrintEngine.CreateStickerVisual(document);
        visual.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var texts = Descendants(visual).OfType<TextBlock>().Select(x => x.Text).ToArray();
        Assert.Contains(texts, x => x.Contains(document.ProductCode));
        Assert.Contains(texts, x => x.Contains(document.CompanyName));
        var page = WpfPhysicalStickerPrintEngine.CreateFixedDocument(document, 1).Pages.Single().Child;
        Assert.True(visual.DesiredSize.Width <= page.Width, "Label exceeds physical page width.");
        Assert.True(visual.DesiredSize.Height <= page.Height, "Label exceeds physical page height.");
        var barcode = Descendants(visual).OfType<Canvas>().Single();
        Assert.True(barcode.Width <= visual.ActualWidth || barcode.Width <= visual.DesiredSize.Width - 16, "Barcode exceeds label width.");
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var item in Descendants(child))
            {
                yield return item;
            }
        }
    }
    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (error is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }
    }
}
