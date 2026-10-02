using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Production.Printing;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Infrastructure.Persistence;
using EdgeRetails.Infrastructure.Production.Printing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace EdgeRetails.IntegrationTests;

[Collection("Phase2PostgresIntegration")]
public sealed class MasterLabelSourcePostgresTests(ITestOutputHelper output)
{
    // NEW_COVERAGE: PostgreSQL-backed committed identity/provenance and repeat export immutability.
    [Fact]
    public async Task CommittedUnit_ReprintAndPdfPreserveIdentitySequenceStockAndPurchaseProvenance()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Contains("Npgsql", db.Database.ProviderName);
        var version = await db.Database.SqlQueryRaw<string>("SELECT current_setting('server_version') AS \"Value\"").SingleAsync();
        Assert.StartsWith("18", version);
        output.WriteLine("Provider = PostgreSQL 18 / Npgsql");
        var fixture = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db);
        // HARNESS_CORRECTION: the generic DB fixture uses a GUID in its display unit
        // name for uniqueness. Real label unit text is "Piece"; retain its unique
        // symbol/ID and the renderer's unchanged explicit overflow rejection.
        var labelUnit = await db.Units.SingleAsync(x => x.Id == fixture.UnitId);
        labelUnit.Name = "Piece";
        await db.SaveChangesAsync();
        var purchase = await ActivatorUtilities.CreateInstance<CreatePurchaseHandler>(services).HandleAsync(new CreatePurchaseCommand(
            fixture.SupplierId, "LABEL-" + Guid.NewGuid().ToString("N"), DateOnly.FromDateTime(DateTime.UtcNow), null, 0m,
            PurchaseSettlementMode.External, fixture.ActorId, Guid.CreateVersion7(),
            [new CreatePurchaseLineInput(fixture.ProductId, fixture.ProductUnitId, 2m, 4000m, 5000m,
                [new SerializedIdentityInput("LABEL-" + Guid.NewGuid().ToString("N")), new SerializedIdentityInput("LABEL-" + Guid.NewGuid().ToString("N"))])]), CancellationToken.None);
        Assert.True(purchase.IsSuccess, purchase.Error?.Message);
        var purchaseDocument = await services.GetRequiredService<IPurchasingReadService>().GetDocumentAsync(new GetPurchaseDocumentQuery(purchase.Value!.PurchaseId), CancellationToken.None);
        Assert.NotNull(purchaseDocument);
        Assert.False(string.IsNullOrWhiteSpace(purchaseDocument.SupplierCode));
        Assert.Equal(2m, purchaseDocument.Items.Single().ReceivedBaseQuantity);
        Assert.Equal(EdgeRetails.Domain.Catalog.TrackingMode.Serialized, purchaseDocument.Items.Single().TrackingMode);
        Assert.True(purchaseDocument.Items.Single().SerialTrackingEnabled);
        Assert.False(purchaseDocument.Items.Single().ImeiTrackingEnabled);
        db.ChangeTracker.Clear();
        var units = await db.InventoryUnits.AsNoTracking().Where(x => x.ProductId == fixture.ProductId).OrderBy(x => x.ItemSequence).ToListAsync();
        Assert.Equal(2, units.Count);
        var supplierProduct = await db.SupplierProducts.AsNoTracking().SingleAsync(x => x.ProductId == fixture.ProductId && x.SupplierId == fixture.SupplierId);
        var beforeSequence = supplierProduct.NextItemSequence;
        var beforeUnitVersions = units.Select(x => (x.Id, x.Version, x.TrackingCode, x.AcquisitionCost, x.InventoryLotId, x.Status)).ToArray();
        var beforeBalances = await db.StockBalances.AsNoTracking().Where(x => x.ProductId == fixture.ProductId).OrderBy(x => x.Id).ToListAsync();
        var beforeMovementCount = await db.InventoryMovements.CountAsync(x => x.ProductId == fixture.ProductId);
        var source = new EfPhysicalStickerDocumentSource(db);
        var documents = new List<PhysicalItemStickerDocument>();
        foreach (var unit in units)
        {
            var document = await source.LoadStickerDocumentAsync(unit.Id, true);
            Assert.Equal(unit.TrackingCode, document.BarcodePayload);
            Assert.Equal(unit.ProductSkuSnapshot, document.ProductCode);
            Assert.Equal(unit.SupplierCodeSnapshot, document.SupplierCode);
            Assert.Equal(unit.SourcePurchaseItemId, document.SourcePurchaseItemId);
            Assert.Equal(unit.SupplierProductId, document.SupplierProductId);
            Assert.False(string.IsNullOrWhiteSpace(document.PurchaseNumber));
            Assert.True(document.IsReprint);
            documents.Add(document);
        }
        var productLabel = await new EfProductLabelDocumentSource(db).LoadProductLabelAsync(fixture.ProductUnitId);
        var first = VectorLabelPdfExporter.Export(documents, [productLabel]);
        var second = VectorLabelPdfExporter.Export(documents, [productLabel]);
        Assert.Equal(first.ExactUnitPdf, second.ExactUnitPdf);
        Assert.Equal(2, first.Manifest.Count(x => x.InventoryUnitId.HasValue));
        Assert.Equal(beforeSequence, (await db.SupplierProducts.AsNoTracking().SingleAsync(x => x.Id == supplierProduct.Id)).NextItemSequence);
        var afterUnits = await db.InventoryUnits.AsNoTracking().Where(x => x.ProductId == fixture.ProductId).OrderBy(x => x.ItemSequence).ToListAsync();
        Assert.Equal(beforeUnitVersions, afterUnits.Select(x => (x.Id, x.Version, x.TrackingCode, x.AcquisitionCost, x.InventoryLotId, x.Status)).ToArray());
        Assert.Equal(2, afterUnits.Count);
        await using var restartedProvider = Phase2PostgresTestHarness.BuildProvider();
        await using var restartedScope = restartedProvider.CreateAsyncScope();
        var restartedDb = restartedScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var restartedDocument = await new EfPhysicalStickerDocumentSource(restartedDb).LoadStickerDocumentAsync(units[0].Id, true);
        Assert.Equal(documents[0].TrackingCode, restartedDocument.TrackingCode);
        Assert.Equal(documents[0].ItemSequence, restartedDocument.ItemSequence);
        Assert.Equal(beforeMovementCount, await restartedDb.InventoryMovements.CountAsync(x => x.ProductId == fixture.ProductId));
        var afterBalances = await restartedDb.StockBalances.AsNoTracking().Where(x => x.ProductId == fixture.ProductId).OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(beforeBalances), System.Text.Json.JsonSerializer.Serialize(afterBalances));
        output.WriteLine("LABEL_COMMITTED_SOURCE_REPRINT_IMMUTABILITY_PASS Units=2 Stock=Unchanged Sequence=Unchanged RestartRead=PASS");
    }
}
