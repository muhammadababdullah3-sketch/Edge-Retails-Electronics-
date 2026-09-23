using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using EdgeRetails.Application.Production.Printing;

namespace EdgeRetails.Desktop.Production.Printing;

public sealed class WpfProductionPrintEngine : IProductionPrintEngine
{
    public Task<PrintJobResult> PrintAsync(ProductionDocument document, PrinterProfile profile, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var submissionStarted = false;
        try
        {
            using var server = new LocalPrintServer();
            using var queue = server.GetPrintQueue(profile.PrinterName);
            queue.Refresh();
            if (queue.IsOffline || queue.IsInError)
            {
                return Task.FromResult(new PrintJobResult(false, "print.printer_unavailable", "The configured printer is offline or reporting an error."));
            }

            var capabilities = queue.GetPrintCapabilities();
            var media = WpfPrintTicketFactory.MatchMedia(capabilities, profile.Paper);
            if (media is null)
            {
                return Task.FromResult(new PrintJobResult(false, "print.media_not_supported", WpfPrintTicketFactory.UnsupportedMediaMessage(profile.Paper)));
            }

            if (capabilities.MaxCopyCount is int maxCopies && profile.Copies > maxCopies)
            {
                return Task.FromResult(new PrintJobResult(false, "print.copy_count_not_supported", $"The configured printer supports at most {maxCopies} copies per print job."));
            }

            var ticketResult = WpfPrintTicketFactory.CreateValidated(queue, media, profile.Copies);
            if (!ticketResult.Supported || ticketResult.Ticket is null)
            {
                return Task.FromResult(new PrintJobResult(false, ticketResult.ErrorCode ?? "print.ticket_not_supported", ticketResult.Message));
            }

            var ticket = ticketResult.Ticket;
            var ticketCapabilities = queue.GetPrintCapabilities(ticket);
            var flow = ProductionFlowDocumentFactory.Create(document, profile.Paper, media, ticketCapabilities.PageImageableArea);
            if (profile.ShowPreviewBeforePrint)
            {
                var preview = new ProductionPrintPreviewWindow(flow, document.DocumentNumber) { Owner = System.Windows.Application.Current?.MainWindow };
                if (preview.ShowDialog() != true)
                {
                    return Task.FromResult(new PrintJobResult(false, "print.cancelled", "Printing was cancelled from preview."));
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var dialog = new PrintDialog { PrintQueue = queue, PrintTicket = ticket };
            var printDoc = ProductionFlowDocumentFactory.Create(document, profile.Paper, media, ticketCapabilities.PageImageableArea);
            submissionStarted = true;
            dialog.PrintDocument(((IDocumentPaginatorSource)printDoc).DocumentPaginator, $"Edge Retails {document.DocumentNumber}");
            return Task.FromResult(new PrintJobResult(true, null, null));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (PrintSystemException)
        {
            return Task.FromResult(submissionStarted
                ? new PrintJobResult(false, "print.outcome_unknown", "Windows reported a spooler error after print submission started. Check the physical printer before using explicit reprint.")
                : new PrintJobResult(false, "print.spooler_failed", "Windows could not prepare the configured printer for this document."));
        }
        catch
        {
            return Task.FromResult(submissionStarted
                ? new PrintJobResult(false, "print.outcome_unknown", "Print submission started but Windows did not confirm the final outcome. Check the physical printer before using explicit reprint.")
                : new PrintJobResult(false, "print.failed", "The document could not be prepared for printing. Check the printer configuration and try again."));
        }
    }
}

internal static class WpfPrintTicketFactory
{
    private const double ThermalToleranceMm = 2.5;

    public static PageMediaSize? MatchMedia(PrintCapabilities capabilities, PaperKind paper)
    {
        IEnumerable<PageMediaSize> supported = capabilities.PageMediaSizeCapability is { } advertised ? advertised : Enumerable.Empty<PageMediaSize>();
        if (paper == PaperKind.A4)
        {
            return supported.FirstOrDefault(x => x.PageMediaSizeName == PageMediaSizeName.ISOA4)
                ?? supported.FirstOrDefault(x => NearMm(x.Width, 210) && NearMm(x.Height, 297));
        }

        var target = paper == PaperKind.Thermal58Mm ? 58d : 80d;
        return supported
            .Where(x => x.Width is not null)
            .OrderBy(x => Math.Abs(ToMm(x.Width!.Value) - target))
            .FirstOrDefault(x => Math.Abs(ToMm(x.Width!.Value) - target) <= ThermalToleranceMm);
    }

    public static ValidatedTicketResult CreateValidated(PrintQueue queue, PageMediaSize media, int copies)
    {
        var baseTicket = queue.UserPrintTicket?.Clone() ?? queue.DefaultPrintTicket?.Clone() ?? new PrintTicket();
        var requested = new PrintTicket
        {
            PageMediaSize = media,
            PageOrientation = PageOrientation.Portrait,
            CopyCount = copies
        };
        var validation = queue.MergeAndValidatePrintTicket(baseTicket, requested, PrintTicketScope.JobScope);
        var ticket = validation.ValidatedPrintTicket;
        if (!SameMedia(ticket.PageMediaSize, media))
        {
            return new(false, null, "print.media_substituted", "The printer driver substituted a different paper size. Configure the requested paper in Windows printer preferences and try again.");
        }

        if (ticket.PageOrientation != PageOrientation.Portrait)
        {
            return new(false, null, "print.orientation_substituted", "The printer driver could not accept portrait orientation for this profile.");
        }

        if (ticket.CopyCount != copies)
        {
            return new(false, null, "print.copy_count_substituted", "The printer driver changed the requested copy count. Reduce copies or correct the Windows printer configuration.");
        }

        return new(true, ticket, null, null);
    }

    private static bool SameMedia(PageMediaSize? actual, PageMediaSize requested)
    {
        if (actual is null)
        {
            return false;
        }

        if (actual.PageMediaSizeName is not null && requested.PageMediaSizeName is not null && actual.PageMediaSizeName == requested.PageMediaSizeName)
        {
            return true;
        }

        if (actual.Width is null || requested.Width is null)
        {
            return false;
        }

        if (Math.Abs(ToMm(actual.Width.Value) - ToMm(requested.Width.Value)) > 0.5)
        {
            return false;
        }

        if (actual.Height is null || requested.Height is null)
        {
            return actual.Height is null && requested.Height is null;
        }

        return Math.Abs(ToMm(actual.Height.Value) - ToMm(requested.Height.Value)) <= 0.5;
    }

    internal sealed record ValidatedTicketResult(bool Supported, PrintTicket? Ticket, string? ErrorCode, string? Message);

    public static string UnsupportedMediaMessage(PaperKind paper) => paper switch
    {
        PaperKind.Thermal58Mm => "The selected printer driver does not advertise a 58 mm receipt/roll media size. Configure the correct 58 mm paper in Windows printer preferences and try again.",
        PaperKind.Thermal80Mm => "The selected printer driver does not advertise an 80 mm receipt/roll media size. Configure the correct 80 mm paper in Windows printer preferences and try again.",
        _ => "The selected printer driver does not advertise ISO A4 media. Configure A4 paper in Windows printer preferences and try again."
    };

    private static bool NearMm(double? value, double target) => value is not null && Math.Abs(ToMm(value.Value) - target) <= ThermalToleranceMm;
    private static double ToMm(double dip) => dip * 25.4d / 96d;
}

internal static class ProductionFlowDocumentFactory
{
    public static FlowDocument Create(ProductionDocument document, PaperKind paper, PageMediaSize? selectedMedia = null, PageImageableArea? imageableArea = null)
    {
        var thermal = paper is PaperKind.Thermal58Mm or PaperKind.Thermal80Mm;
        var width = selectedMedia?.Width ?? paper switch
        {
            PaperKind.Thermal58Mm => Mm(58),
            PaperKind.Thermal80Mm => Mm(80),
            _ => Mm(210)
        };
        var height = selectedMedia?.Height ?? (paper == PaperKind.A4 ? Mm(297) : double.NaN);
        var baseMargin = thermal ? Mm(3) : Mm(12);
        var left = baseMargin;
        var top = baseMargin;
        var right = baseMargin;
        var bottom = baseMargin;
        if (imageableArea is not null)
        {
            left = Math.Max(left, imageableArea.OriginWidth);
            top = Math.Max(top, imageableArea.OriginHeight);
            if (!double.IsNaN(width))
            {
                right = Math.Max(right, Math.Max(0, width - imageableArea.OriginWidth - imageableArea.ExtentWidth));
            }

            if (!double.IsNaN(height))
            {
                bottom = Math.Max(bottom, Math.Max(0, height - imageableArea.OriginHeight - imageableArea.ExtentHeight));
            }
        }
        var flow = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = thermal ? 10 : 11,
            PagePadding = new Thickness(left, top, right, bottom),
            ColumnGap = 0,
            PageWidth = width,
            PageHeight = height
        };

        flow.Blocks.Add(Centered(document.ShopName, 16, FontWeights.Bold));
        if (!string.IsNullOrWhiteSpace(document.ShopAddress))
        {
            flow.Blocks.Add(Centered(document.ShopAddress!, 9, FontWeights.Normal));
        }

        if (!string.IsNullOrWhiteSpace(document.ShopPhone))
        {
            flow.Blocks.Add(Centered(document.ShopPhone!, 9, FontWeights.Normal));
        }

        flow.Blocks.Add(Centered(document.Title, thermal ? 12 : 16, FontWeights.SemiBold));
        if (!string.IsNullOrWhiteSpace(document.CopyLabel))
        {
            var copy = Centered(document.CopyLabel!, thermal ? 12 : 14, FontWeights.Bold);
            copy.TextDecorations = TextDecorations.Underline;
            flow.Blocks.Add(copy);
        }
        flow.Blocks.Add(ParagraphOf($"No: {document.DocumentNumber}"));
        flow.Blocks.Add(ParagraphOf($"Date: {document.IssuedAt:yyyy-MM-dd HH:mm zzz}"));
        if (!string.IsNullOrWhiteSpace(document.PartyName))
        {
            flow.Blocks.Add(ParagraphOf($"Party: {document.PartyName}"));
        }

        var table = new Table { CellSpacing = 0 };
        table.Columns.Add(new TableColumn { Width = new GridLength(thermal ? 1 : 3, GridUnitType.Star) });
        table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Auto) });
        if (!thermal)
        {
            table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Auto) });
        }

        table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Auto) });
        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        group.Rows.Add(thermal ? ThermalRow("Item", "Qty", "Total", true) : A4Row("Item", "Qty", "Unit Price", "Total", true));

        foreach (var line in document.Lines)
        {
            var description = line.TrackingReference is null ? line.Description : $"{line.Description}\n{line.TrackingReference}";
            var quantity = $"{line.Quantity:0.###}{(string.IsNullOrWhiteSpace(line.Unit) ? "" : " " + line.Unit)}";
            group.Rows.Add(thermal
                ? ThermalRow(description, quantity, line.LineTotal.ToString("N2"), false)
                : A4Row(description, quantity, line.UnitPrice.ToString("N2"), line.LineTotal.ToString("N2"), false));
        }
        flow.Blocks.Add(table);
        foreach (var total in document.Totals)
        {
            var paragraph = ParagraphOf($"{total.Label}: {total.Amount:N2}");
            paragraph.TextAlignment = TextAlignment.Right;
            if (total.Emphasized)
            {
                paragraph.FontWeight = FontWeights.Bold;
            }

            flow.Blocks.Add(paragraph);
        }
        foreach (var note in document.Notes)
        {
            flow.Blocks.Add(ParagraphOf(note));
        }

        if (!string.IsNullOrWhiteSpace(document.Footer))
        {
            flow.Blocks.Add(Centered(document.Footer!, 9, FontWeights.Normal));
        }

        return flow;
    }

    private static TableRow ThermalRow(string left, string middle, string right, bool header)
    {
        var row = new TableRow { FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal };
        row.Cells.Add(Cell(left, TextAlignment.Left));
        row.Cells.Add(Cell(middle, TextAlignment.Right));
        row.Cells.Add(Cell(right, TextAlignment.Right));
        return row;
    }

    private static TableRow A4Row(string item, string quantity, string unitPrice, string total, bool header)
    {
        var row = new TableRow { FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal };
        row.Cells.Add(Cell(item, TextAlignment.Left));
        row.Cells.Add(Cell(quantity, TextAlignment.Right));
        row.Cells.Add(Cell(unitPrice, TextAlignment.Right));
        row.Cells.Add(Cell(total, TextAlignment.Right));
        return row;
    }

    private static TableCell Cell(string text, TextAlignment alignment)
        => new(new Paragraph(new Run(text)) { TextAlignment = alignment, Margin = new Thickness(0, 2, 0, 2) });
    private static Paragraph ParagraphOf(string text) => new(new Run(text)) { Margin = new Thickness(0, 2, 0, 2) };
    private static Paragraph Centered(string text, double size, FontWeight weight) => new(new Run(text)) { TextAlignment = TextAlignment.Center, FontSize = size, FontWeight = weight, Margin = new Thickness(0, 2, 0, 2) };
    private static double Mm(double mm) => mm * 96d / 25.4d;
}
