using System.Text;
using System.Text.Json;
using EdgeRetails.Application.Production.Printing;

namespace EdgeRetails.UnitTests;

// NEW_COVERAGE: synthetic fixtures test renderer only; these are not operational pilot exports.
public sealed class MasterLabelPdfTests
{
    [Fact]
    public void Export_DeterministicCommittedIdentitiesWithExactManifestPagination()
    {
        var units = Enumerable.Range(1, 13).Select(Unit).Reverse().ToArray();
        var product = new ProductLabelDocument(Guid.Parse("22222222-2222-2222-2222-222222222222"), Guid.NewGuid(),
            "12W LED Bulb", "Philips", "12W", "PHB-LED12", "Piece", "BULB-12W", 500m);
        var result = VectorLabelPdfExporter.Export(units, [product]);
        var repeated = VectorLabelPdfExporter.Export(units.Reverse().ToArray(), [product]);
        Assert.Equal(result.ExactUnitPdf, repeated.ExactUnitPdf);
        Assert.Equal(result.Manifest, repeated.Manifest);
        Assert.Equal(14, result.Manifest.Count);
        var exact = result.Manifest.Where(x => x.InventoryUnitId.HasValue).ToArray();
        Assert.Equal(13, exact.Length);
        Assert.All(exact, x => Assert.Equal(x.TrackingCode, x.BarcodePayload));
        Assert.Equal(2, exact.Last().PageNumber);
        Assert.Equal(3, exact.Last().LabelIndex);
        Assert.Null(result.Manifest.Last().TrackingCode);
        Assert.Equal("BULB-12W", result.Manifest.Last().BarcodePayload);
        var pdf = Encoding.ASCII.GetString(result.ExactUnitPdf);
        Assert.StartsWith("%PDF-1.4", pdf);
        Assert.Contains("/Count 2", pdf);
        Assert.Contains("Product: GFF-FAN18", pdf);
        Assert.Contains("DE1-GFF-FAN18-000013", pdf);
        Assert.All(exact, x => Assert.Equal("PUR-1", x.PurchaseNumber));
        var qa = Environment.GetEnvironmentVariable("EDGE_RETAILS_LABEL_QA_OUTPUT");
        if (!string.IsNullOrWhiteSpace(qa))
        {
            Directory.CreateDirectory(qa);
            File.WriteAllBytes(Path.Combine(qa, "renderer_exact_fixture.pdf"), result.ExactUnitPdf);
            File.WriteAllBytes(Path.Combine(qa, "renderer_product_fixture.pdf"), result.ProductPdf);
            File.WriteAllBytes(Path.Combine(qa, "renderer_manifest_fixture.pdf"), result.ManifestPdf);
            File.WriteAllText(Path.Combine(qa, "renderer_fixture_manifest.json"), JsonSerializer.Serialize(result.Manifest));
        }
    }

    [Fact]
    public void Export_RejectsDuplicateAndMissingIdentityWithoutInventingTrackingCode()
    {
        var unit = Unit(1);
        Assert.Throws<ArgumentException>(() => VectorLabelPdfExporter.Export([unit, unit], []));
        Assert.Throws<ArgumentException>(() => VectorLabelPdfExporter.Export([unit with { TrackingCode = "" }], []));
        Assert.Throws<ArgumentException>(() => VectorLabelPdfExporter.Export([unit with { InventoryUnitId = Guid.Empty }], []));
    }

    [Fact]
    public void Export_ProductWithoutBarcodeNeverManufacturesPhysicalTrackingCode()
    {
        var product = new ProductLabelDocument(Guid.NewGuid(), Guid.NewGuid(), "2.5mm Cable", "Pakistan Cables", "2.5mm",
            "PCC-C25", "Metre", null, 175m);
        var result = VectorLabelPdfExporter.Export([], [product]);
        Assert.Null(result.Manifest.Single().TrackingCode);
        Assert.Null(result.Manifest.Single().InventoryUnitId);
        Assert.Null(result.Manifest.Single().BarcodePayload);
        Assert.Contains("Unit: Metre", Encoding.ASCII.GetString(result.ProductPdf));
    }

    [Fact]
    public void Export_ProductReprintMarksDocumentAndRetainsUnitBarcodeAndProductIdentity()
    {
        var product = new ProductLabelDocument(Guid.NewGuid(), Guid.NewGuid(), "LED Bulb", "Philips", "12W", "PHB-LED12", "Piece", "BULB-12W", 500m, true);
        var result = VectorLabelPdfExporter.Export([], [product]);
        Assert.Contains("REPRINT", Encoding.ASCII.GetString(result.ProductPdf));
        Assert.Equal(product.ProductUnitId, result.Manifest.Single().ProductUnitId);
        Assert.Equal("BULB-12W", result.Manifest.Single().BarcodePayload);
        Assert.Null(result.Manifest.Single().TrackingCode);
    }

    [Fact]
    public void Export_RejectsTextAndBarcodeThatCannotFitInsteadOfClipping()
    {
        Assert.Throws<ArgumentException>(() => VectorLabelPdfExporter.Export([Unit(1) with { TrackingCode = new string('A', 100) }], []));
        Assert.Throws<ArgumentException>(() => VectorLabelPdfExporter.Export([Unit(1) with { ProductName = new string('A', 200) }], []));
        Assert.Throws<ArgumentException>(() => VectorLabelPdfExporter.Export([Unit(1) with { ProductName = "Fan\nHidden" }], []));
    }

    private static PhysicalItemStickerDocument Unit(int sequence) => new(
        Guid.Parse($"11111111-1111-1111-1111-{sequence:D12}"), $"DE1-GFF-FAN18-{sequence:D6}", "GFC", "18 inch Pedestal Fan", "FAN18", "GFF-FAN18", sequence,
        $"SN-{sequence}", null, null, "Edge Retails", 15000m, false, DateTimeOffset.UnixEpoch, "Demo Supplier", "DE1", "PUR-1", Guid.Parse("33333333-3333-3333-3333-333333333333"), Guid.Parse("44444444-4444-4444-4444-444444444444"));
}
