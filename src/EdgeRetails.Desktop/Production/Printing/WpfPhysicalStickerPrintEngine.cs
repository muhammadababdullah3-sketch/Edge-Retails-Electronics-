using System.Globalization;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using EdgeRetails.Application.Production;
using EdgeRetails.Application.Production.Printing;

namespace EdgeRetails.Desktop.Production.Printing;

public sealed class WpfPhysicalStickerPrintEngine : IPhysicalStickerPrintEngine, IProductLabelPrintEngine
{
    private readonly Func<string?, (PrintQueue? Queue, PrintJobResult? Error)>? _queueResolver;
    private readonly Action<PrintQueue?, DocumentPaginator, string>? _documentPrinter;

    public WpfPhysicalStickerPrintEngine()
        : this(null, null)
    {
    }

    internal WpfPhysicalStickerPrintEngine(
        Func<string?, (PrintQueue? Queue, PrintJobResult? Error)>? queueResolver,
        Action<PrintQueue?, DocumentPaginator, string>? documentPrinter)
    {
        _queueResolver = queueResolver;
        _documentPrinter = documentPrinter;
    }

    public Task<PrintJobResult> PrintStickerAsync(
        PhysicalItemStickerDocument document,
        string? printerName = null,
        int copies = 1,
        CancellationToken cancellationToken = default) =>
        PrintDocumentAsync(() => CreateFixedDocument(document, copies), "Physical Sticker " + document.TrackingCode, printerName, cancellationToken);

    public Task<PrintJobResult> PrintProductLabelAsync(
        ProductLabelDocument document,
        string? printerName = null,
        int copies = 1,
        CancellationToken cancellationToken = default) =>
        PrintDocumentAsync(() => CreateProductFixedDocument(document, copies), "Product Label " + document.ProductCode, printerName, cancellationToken);

    private Task<PrintJobResult> PrintDocumentAsync(
        Func<FixedDocument> documentFactory,
        string jobName,
        string? printerName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var submissionStarted = false;
        try
        {
            PrintQueue? queue = null;
            IDisposable? cleanup = null;

            if (_queueResolver is not null)
            {
                var (resolvedQueue, error) = _queueResolver(printerName);
                if (error is not null)
                {
                    return Task.FromResult(error);
                }

                queue = resolvedQueue;
            }
            else
            {
                var server = new LocalPrintServer();
                cleanup = server;
                try
                {
                    queue = string.IsNullOrWhiteSpace(printerName)
                        ? LocalPrintServer.GetDefaultPrintQueue()
                        : server.GetPrintQueue(printerName);
                }
                catch (Exception ex)
                {
                    server.Dispose();
                    return Task.FromResult(new PrintJobResult(
                        false,
                        "print.printer_not_found",
                        $"Printer '{(string.IsNullOrWhiteSpace(printerName) ? "Default" : printerName)}' was not found: {ex.Message}"));
                }

                if (queue is null)
                {
                    server.Dispose();
                    return Task.FromResult(new PrintJobResult(
                        false,
                        "print.printer_not_found",
                        $"Printer '{(string.IsNullOrWhiteSpace(printerName) ? "Default" : printerName)}' was not found."));
                }
            }

            using (cleanup)
            using (queue)
            {
                if (queue is not null)
                {
                    queue.Refresh();
                    if (queue.IsOffline || queue.IsInError)
                    {
                        return Task.FromResult(new PrintJobResult(
                            false,
                            "print.printer_unavailable",
                            $"The target printer '{queue.Name}' is offline or reporting an error."));
                    }
                }

                var fixedDoc = documentFactory();

                if (queue is not null)
                {
                    var capabilities = queue.GetPrintCapabilities();
                    var area = capabilities.PageImageableArea;
                    if (area is not null && (area.ExtentWidth < fixedDoc.Pages[0].Child.Width || area.ExtentHeight < fixedDoc.Pages[0].Child.Height))
                    {
                        return Task.FromResult(new PrintJobResult(false, "print.media_too_small",
                            "The selected printer media cannot contain this label. Choose compatible media or export the PDF."));
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                submissionStarted = true;

                if (_documentPrinter is not null)
                {
                    _documentPrinter(queue, fixedDoc.DocumentPaginator, jobName);
                }
                else
                {
                    var dialog = new PrintDialog { PrintQueue = queue };
                    dialog.PrintDocument(fixedDoc.DocumentPaginator, jobName);
                }

                return Task.FromResult(new PrintJobResult(true, null, null));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PrintSystemException ex)
        {
            return Task.FromResult(submissionStarted
                ? new PrintJobResult(false, "print.outcome_unknown", $"Windows reported a spooler error after print submission: {ex.Message}")
                : new PrintJobResult(false, "print.spooler_failed", $"Windows could not prepare the printer: {ex.Message}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(submissionStarted
                ? new PrintJobResult(false, "print.outcome_unknown", $"Print submission error: {ex.Message}")
                : new PrintJobResult(false, "print.failed", $"Sticker could not be printed: {ex.Message}"));
        }
    }

    public static FrameworkElement CreateStickerVisual(PhysicalItemStickerDocument document)
    {
        // Same physical size as the vector PDF export: 274 x 150 pt (96/72 WPF scale).
        var container = new Border
        {
            Width = VectorLabelPdfExporter.LabelWidth * 96 / 72,
            Background = Brushes.White,
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 6, 10, 6)
        };
        var stack = new StackPanel { Orientation = Orientation.Vertical };
        void Add(string text, double size, bool bold = false)
        {
            stack.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = size,
                FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 1, 0, 1)
            });
        }
        Add(document.ProductName, 12, true);
        Add(document.CompanyName + (string.IsNullOrWhiteSpace(document.ModelName) ? "" : " | " + document.ModelName), 11);
        Add("Product: " + document.ProductCode, 11);
        if (!string.IsNullOrWhiteSpace(document.SerialNumber))
        {
            Add("SN: " + document.SerialNumber, 10);
        }
        if (!string.IsNullOrWhiteSpace(document.Imei1))
        {
            Add("IMEI: " + document.Imei1, 10);
        }
        if (!string.IsNullOrWhiteSpace(document.Imei2))
        {
            Add("IMEI2: " + document.Imei2, 10);
        }
        stack.Children.Add(RenderBarcodeCanvas(document.BarcodeModules, 40));
        Add(document.TrackingCode, 12, true);
        if (document.RetailSalePrice.HasValue)
        {
            Add($"Rs. {document.RetailSalePrice.Value:N2}" + (document.IsReprint ? " | REPRINT" : ""), 10);
        }
        else if (document.IsReprint)
        {
            Add("REPRINT", 10);
        }
        container.Child = stack;
        return container;
    }

    private static Canvas RenderBarcodeCanvas(string modules, double barHeight)
    {
        var canvas = new Canvas
        {
            Height = barHeight,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        if (string.IsNullOrEmpty(modules))
        {
            return canvas;
        }

        var totalWidth = VectorLabelPdfExporter.LabelWidth * 96 / 72 - 24;
        var moduleWidth = totalWidth / (modules.Length + 20);
        if (moduleWidth < 2.0 / 3)
        {
            throw new ArgumentException("Barcode exceeds this supported label size.");
        }
        canvas.Width = totalWidth;

        var i = 0;
        while (i < modules.Length)
        {
            if (modules[i] == '1')
            {
                var runLength = 1;
                while (i + runLength < modules.Length && modules[i + runLength] == '1')
                {
                    runLength++;
                }

                var rect = new Rectangle
                {
                    Width = runLength * moduleWidth,
                    Height = barHeight,
                    Fill = Brushes.Black
                };
                Canvas.SetLeft(rect, (i + 10) * moduleWidth);
                Canvas.SetTop(rect, 0);
                canvas.Children.Add(rect);

                i += runLength;
            }
            else
            {
                i++;
            }
        }

        return canvas;
    }

    public static FixedDocument CreateProductFixedDocument(ProductLabelDocument document, int copies)
    {
        var fixedDoc = new FixedDocument();
        for (var i = 0; i < Math.Max(1, copies); i++)
        {
            var page = new FixedPage { Width = VectorLabelPdfExporter.LabelWidth * 96 / 72, Height = VectorLabelPdfExporter.LabelHeight * 96 / 72 };
            var stack = new StackPanel { Margin = new Thickness(10, 6, 10, 6), Width = page.Width - 20 };
            foreach (var value in new[] { document.ProductName, document.CompanyName + " | " + document.ModelName, "Product: " + document.ProductCode, "Unit: " + document.UnitName })
            {
                stack.Children.Add(new TextBlock { Text = value, FontSize = 12, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) });
            }
            if (!string.IsNullOrWhiteSpace(document.UnitBarcode))
            {
                stack.Children.Add(RenderBarcodeCanvas(Code128Encoder.EncodeToModules(document.UnitBarcode), 40));
                stack.Children.Add(new TextBlock { Text = document.UnitBarcode, FontSize = 12, TextAlignment = TextAlignment.Center });
            }
            if (document.RetailSalePrice.HasValue)
            {
                stack.Children.Add(new TextBlock { Text = $"Rs. {document.RetailSalePrice.Value:N2}", FontSize = 11, TextAlignment = TextAlignment.Center });
            }
            if (document.IsReprint)
            {
                stack.Children.Add(new TextBlock { Text = "REPRINT", FontSize = 10, TextAlignment = TextAlignment.Center });
            }
            stack.Measure(new Size(page.Width, double.PositiveInfinity));
            if (stack.DesiredSize.Height > page.Height)
            {
                throw new ArgumentException("Product label exceeds this supported physical page.");
            }
            page.Children.Add(stack);
            var content = new PageContent();
            ((System.Windows.Markup.IAddChild)content).AddChild(page);
            fixedDoc.Pages.Add(content);
        }
        return fixedDoc;
    }

    public static FixedDocument CreateFixedDocument(PhysicalItemStickerDocument document, int copies)
    {
        var fixedDoc = new FixedDocument();
        const double stickerWidth = VectorLabelPdfExporter.LabelWidth * 96 / 72;
        const double stickerHeight = VectorLabelPdfExporter.LabelHeight * 96 / 72;

        for (var c = 0; c < Math.Max(1, copies); c++)
        {
            var page = new FixedPage
            {
                Width = stickerWidth,
                Height = stickerHeight
            };

            var visual = CreateStickerVisual(document);
            visual.Measure(new Size(stickerWidth, double.PositiveInfinity));
            if (visual.DesiredSize.Width > stickerWidth || visual.DesiredSize.Height > stickerHeight)
            {
                throw new ArgumentException("Label content exceeds this supported physical page; no content was clipped.");
            }
            page.Children.Add(visual);

            var pageContent = new PageContent();
            ((System.Windows.Markup.IAddChild)pageContent).AddChild(page);
            fixedDoc.Pages.Add(pageContent);
        }

        return fixedDoc;
    }
}
