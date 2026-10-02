using System.Globalization;
using System.Text;

namespace EdgeRetails.Application.Production.Printing;

/// <summary>Deterministic vector labels. Identity is supplied by committed Server document sources only.</summary>
public static class VectorLabelPdfExporter
{
    public const double PageWidth = 595.28;
    public const double PageHeight = 841.89;
    public const double LabelWidth = 274;
    public const double LabelHeight = 150;
    public const int LabelsPerPage = 10;
    private const double Margin = 18;

    public static LabelExportBundle Export(IReadOnlyList<PhysicalItemStickerDocument> exactUnits, IReadOnlyList<ProductLabelDocument> products)
    {
        ArgumentNullException.ThrowIfNull(exactUnits);
        ArgumentNullException.ThrowIfNull(products);
        if (exactUnits.Select(x => x.InventoryUnitId).Distinct().Count() != exactUnits.Count ||
            products.Select(x => x.ProductUnitId).Distinct().Count() != products.Count)
        {
            throw new ArgumentException("Each selected unit must appear only once in an export.");
        }
        var exact = exactUnits.OrderBy(x => x.ProductCode, StringComparer.Ordinal).ThenBy(x => x.SupplierCode, StringComparer.Ordinal)
            .ThenBy(x => x.ItemSequence).ThenBy(x => x.InventoryUnitId).ToArray();
        var productLabels = products.OrderBy(x => x.ProductCode, StringComparer.Ordinal).ThenBy(x => x.UnitName, StringComparer.Ordinal)
            .ThenBy(x => x.ProductUnitId).ToArray();
        var manifest = new List<LabelManifestRecord>();
        var exactPdf = Labels(exact.Length, (page, slot, content) =>
        {
            var d = exact[page * LabelsPerPage + slot];
            if (d.InventoryUnitId == Guid.Empty || string.IsNullOrWhiteSpace(d.TrackingCode) || string.IsNullOrWhiteSpace(d.ProductCode) || d.ItemSequence <= 0)
            {
                throw new ArgumentException("A committed tracking identity and product code are required for an exact-unit label.");
            }
            DrawLabel(content, slot, d.ProductName, d.CompanyName, d.ModelName, d.ProductCode, d.TrackingCode,
                d.SerialNumber is not null ? "SN: " + d.SerialNumber : d.Imei1 is not null ? "IMEI: " + d.Imei1 : null,
                d.RetailSalePrice, d.IsReprint ? "REPRINT" : null);
            manifest.Add(new(d.InventoryUnitId, null, d.ProductName, d.ProductCode, d.SupplierName, d.SupplierCode, d.TrackingCode,
                d.SerialNumber, d.PurchaseNumber, d.SourcePurchaseItemId, d.SupplierProductId, d.BarcodePayload,
                "Pilot_Exact_Unit_Labels.pdf", page + 1, slot + 1));
        });
        var productPdf = Labels(productLabels.Length, (page, slot, content) =>
        {
            var d = productLabels[page * LabelsPerPage + slot];
            if (d.ProductUnitId == Guid.Empty || string.IsNullOrWhiteSpace(d.ProductCode))
            {
                throw new ArgumentException("A committed product unit and product code are required for a product label.");
            }
            DrawLabel(content, slot, d.ProductName, d.CompanyName, d.ModelName, d.ProductCode, d.UnitBarcode,
                "Unit: " + d.UnitName, d.RetailSalePrice, d.IsReprint ? "REPRINT" : null);
            manifest.Add(new(null, d.ProductUnitId, d.ProductName, d.ProductCode, null, null, null, null, null, null, null,
                d.UnitBarcode, "Pilot_Product_Labels.pdf", page + 1, slot + 1));
        });
        return new(exactPdf, productPdf, Manifest(manifest), manifest);
    }

    private static byte[] Labels(int count, Action<int, int, StringBuilder> draw)
    {
        var pages = new List<string>();
        for (var page = 0; page < Math.Max(1, (count + LabelsPerPage - 1) / LabelsPerPage); page++)
        {
            var content = new StringBuilder();
            if (count == 0)
            {
                Text(content, "No labels selected", Margin, PageHeight - 40, 12);
            }
            for (var slot = 0; slot < LabelsPerPage && page * LabelsPerPage + slot < count; slot++)
            {
                draw(page, slot, content);
            }
            Text(content, $"Labels: {count} | Page {page + 1}", Margin, 16, 8);
            pages.Add(content.ToString());
        }
        return Pdf(pages);
    }

    private static void DrawLabel(StringBuilder b, int slot, string name, string company, string? model, string productCode,
        string? barcode, string? serial, decimal? price, string? marker)
    {
        var x = Margin + slot % 2 * (LabelWidth + 11);
        var y = PageHeight - Margin - (slot / 2 + 1) * (LabelHeight + 9);
        b.AppendLine($"0.5 w {N(x)} {N(y)} {N(LabelWidth)} {N(LabelHeight)} re S");
        var cursor = y + LabelHeight - 15;
        foreach (var line in Wrap(name, 42, 2))
        {
            Text(b, line, x + 8, cursor, 10);
            cursor -= 12;
        }
        Text(b, string.Join(" | ", new[] { company, model }.Where(v => !string.IsNullOrWhiteSpace(v))), x + 8, cursor, 9);
        cursor -= 12;
        Text(b, "Product: " + productCode, x + 8, cursor, 9);
        cursor -= 12;
        if (!string.IsNullOrWhiteSpace(serial))
        {
            Text(b, serial, x + 8, cursor, 8);
        }
        if (!string.IsNullOrWhiteSpace(barcode))
        {
            var modules = Code128Encoder.EncodeToModules(barcode);
            var moduleWidth = (LabelWidth - 16) / (modules.Length + 20); // ten-module quiet zone at each edge
            if (moduleWidth < 0.5)
            {
                throw new ArgumentException("The barcode is too long for this label size. Choose a larger supported label.");
            }
            var bx = x + 8 + moduleWidth * 10;
            for (var i = 0; i < modules.Length; i++)
            {
                if (modules[i] == '1')
                {
                    b.AppendLine($"{N(bx + i * moduleWidth)} {N(y + 35)} {N(moduleWidth)} 32 re f");
                }
            }
            Text(b, barcode, x + 8, y + 22, 9);
        }
        var footer = string.Join(" | ", new[] { price.HasValue ? $"Rs. {price.Value.ToString("0.00", CultureInfo.InvariantCulture)}" : null, marker }.Where(v => v is not null));
        Text(b, footer, x + 8, y + 9, 8);
    }

    private static byte[] Manifest(IReadOnlyList<LabelManifestRecord> records)
    {
        var pages = new List<string>();
        for (var start = 0; start < Math.Max(1, records.Count); start += 7)
        {
            var b = new StringBuilder();
            Text(b, "Edge Retails - committed label manifest", Margin, PageHeight - 35, 14, PageWidth - 36);
            var y = PageHeight - 60;
            foreach (var r in records.Skip(start).Take(7))
            {
                foreach (var line in new[] { r.ProductName + " | " + r.ProductCode, r.DocumentName + $" | page {r.PageNumber} label {r.LabelIndex}",
                    "Tracking: " + (r.TrackingCode ?? "not applicable") + " | SN: " + (r.SerialNumber ?? "none"),
                    "Supplier: " + (r.SupplierName ?? "none") + " | " + (r.SupplierCode ?? "none"),
                    "Purchase: " + (r.PurchaseNumber ?? "none") + " | Unit: " + (r.InventoryUnitId?.ToString() ?? r.ProductUnitId?.ToString()),
                    "PurchaseItem: " + (r.SourcePurchaseItemId?.ToString() ?? "none") })
                {
                    Text(b, line, Margin, y, 8, PageWidth - 36);
                    y -= 13;
                }
                y -= 18;
            }
            Text(b, $"Manifest records: {records.Count} | Page {pages.Count + 1}", Margin, 16, 8);
            pages.Add(b.ToString());
        }
        return Pdf(pages);
    }

    private static IReadOnlyList<string> Wrap(string value, int width, int maxLines)
    {
        var lines = new List<string>();
        var remaining = value.Trim();
        while (remaining.Length > width)
        {
            var split = remaining.LastIndexOf(' ', width);
            if (split <= 0)
            {
                split = width;
            }
            lines.Add(remaining[..split]);
            remaining = remaining[split..].TrimStart();
        }
        lines.Add(remaining);
        if (lines.Count > maxLines)
        {
            throw new ArgumentException("Product name exceeds the label's supported text area.");
        }
        return lines;
    }

    private static void Text(StringBuilder b, string value, double x, double y, double size, double maxWidth = LabelWidth - 16)
    {
        if (value.Any(ch => ch < 32 || ch > 126))
        {
            throw new ArgumentException("This label layout requires printable Latin text. No unsupported characters were replaced.");
        }
        if (value.Length * size * 0.6 > maxWidth)
        {
            throw new ArgumentException("Label text exceeds the printable width; no text was truncated.");
        }
        var escaped = value.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
        b.AppendLine($"BT /F1 {N(size)} Tf {N(x)} {N(y)} Td ({escaped}) Tj ET");
    }

    private static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static byte[] Pdf(IReadOnlyList<string> pages)
    {
        using var stream = new MemoryStream();
        var offsets = new List<long> { 0 };
        void Write(string text) => stream.Write(Encoding.ASCII.GetBytes(text));
        void Object(int id, string body) { offsets.Add(stream.Position); Write($"{id} 0 obj\n{body}\nendobj\n"); }
        Write("%PDF-1.4\n");
        Object(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Object(2, $"<< /Type /Pages /Count {pages.Count} /Kids [{string.Join(" ", Enumerable.Range(0, pages.Count).Select(i => $"{4 + i * 2} 0 R"))}] >>");
        Object(3, "<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>");
        for (var i = 0; i < pages.Count; i++)
        {
            Object(4 + i * 2, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(PageWidth)} {N(PageHeight)}] /Resources << /Font << /F1 3 0 R >> >> /Contents {5 + i * 2} 0 R >>");
            Object(5 + i * 2, $"<< /Length {Encoding.ASCII.GetByteCount(pages[i])} >>\nstream\n{pages[i]}endstream");
        }
        var xref = stream.Position;
        Write($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1))
        {
            Write($"{offset:0000000000} 00000 n \n");
        }
        Write($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return stream.ToArray();
    }
}
