using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Features.Thaka;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Infrastructure.Persistence;
using EdgeRetails.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.IntegrationTests;

[Collection("TrackingManufacturerIdentityPg")]
public sealed class TrackingFinalChallengePostgresTests
{
    [Theory]
    [InlineData(TrackingMode.Serialized, 1)]
    [InlineData(TrackingMode.IndividualPiece, 1)]
    [InlineData(TrackingMode.Container, 2)]
    public async Task ThakaReversal_RestoresExactQuantityCostAndIdentityOnce(TrackingMode mode, int factor)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        var seed = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db);
        (await db.Products.SingleAsync(x => x.Id == seed.ProductId)).TrackingMode = mode;
        (await db.ProductUnits.SingleAsync(x => x.Id == seed.ProductUnitId)).FactorToBaseUnit = factor;
        await db.SaveChangesAsync();
        var unit = await ReceiveAsync(services, db, seed.ProductId, seed.ProductUnitId, seed.SupplierId, seed.ActorId, 100m);
        var customer = await Phase2PostgresTestHarness.SeedCustomerAsync(db);
        var project = await services.GetRequiredService<CreateThakaProjectHandler>().HandleAsync(new CreateThakaProjectCommand(
            customer.Id, "Challenge reversal", null, null, DateOnly.FromDateTime(DateTime.UtcNow), seed.ActorId, Guid.NewGuid(), Guid.NewGuid()), default);
        Assert.True(project.IsSuccess, project.Error?.Message);
        var issued = await services.GetRequiredService<IssueThakaMaterialHandler>().HandleAsync(new IssueThakaMaterialCommand(
            Guid.NewGuid(), project.Value, seed.ActorId, null,
            [new IssueThakaMaterialLineInput(seed.ProductId, seed.ProductUnitId, 1m, 5000m, [unit.Id])]), default);
        Assert.True(issued.IsSuccess, issued.Error?.Message);
        var intent = new ReverseThakaMaterialCommand(Guid.NewGuid(), project.Value, issued.Value!.MaterialIssueId, "Exact reversal", seed.ActorId);
        var reversed = await services.GetRequiredService<ReverseThakaMaterialHandler>().HandleAsync(intent, default);
        Assert.True(reversed.IsSuccess, reversed.Error?.Message);
        var replay = await services.GetRequiredService<ReverseThakaMaterialHandler>().HandleAsync(intent, default);
        Assert.True(replay.IsSuccess, replay.Error?.Message);
        Assert.Equal(reversed.Value!.ReversalId, replay.Value!.ReversalId);
        db.ChangeTracker.Clear();
        var restored = await db.InventoryUnits.SingleAsync(x => x.Id == unit.Id);
        Assert.Equal(unit.TrackingCode, restored.TrackingCode);
        Assert.Equal(InventoryUnitStatus.InStock, restored.Status);
        Assert.Equal((decimal)factor, (await db.StockBalances.SingleAsync(x => x.ProductId == seed.ProductId)).SellableQty);
        Assert.Equal((decimal)factor, (await db.InventoryLotBucketBalances.SingleAsync(x => x.LotId == restored.InventoryLotId && x.StockBucket == InventoryBucket.Sellable)).Quantity);
        Assert.Equal(100m, (await db.ProductCostStates.SingleAsync(x => x.ProductId == seed.ProductId)).TotalInventoryCost);
        Assert.Equal((decimal)factor, await services.GetRequiredService<IInventoryRepository>().GetPhysicalUnitBaseQuantitySnapshotAsync(restored, default));
        Assert.Equal(1, await db.ThakaMaterialReversals.CountAsync(x => x.MaterialIssueId == issued.Value.MaterialIssueId));
        Assert.Equal(1, await db.InventoryUnits.CountAsync(x => x.ProductId == seed.ProductId));
    }

    [Theory]
    [InlineData(TrackingMode.Serialized, 1, false)]
    [InlineData(TrackingMode.IndividualPiece, 1, false)]
    [InlineData(TrackingMode.Container, 2, false)]
    [InlineData(TrackingMode.IndividualPiece, 1, true)]
    [InlineData(TrackingMode.Container, 2, true)]
    public async Task Exchange_RequiresExactUnitsAndPreservesBothLegs(TrackingMode mode, int factor, bool missingIds)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var seed = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db);
        (await db.Products.SingleAsync(x => x.Id == seed.ProductId)).TrackingMode = mode;
        (await db.ProductUnits.SingleAsync(x => x.Id == seed.ProductUnitId)).FactorToBaseUnit = factor;
        await db.SaveChangesAsync();
        var original = await ReceiveAsync(services, db, seed.ProductId, seed.ProductUnitId, seed.SupplierId, seed.ActorId, 100m);
        var replacement = await ReceiveAsync(services, db, seed.ProductId, seed.ProductUnitId, seed.SupplierId, seed.ActorId, 150m);
        var customer = await Phase2PostgresTestHarness.SeedCustomerAsync(db);
        var sale = await services.GetRequiredService<CompleteSaleHandler>().HandleAsync(new CompleteSaleCommand(
            Guid.NewGuid(), customer.Id, seed.ActorId, null, 0m, SalePaymentMethod.Bank, 5000m * factor, "BANK", null,
            [new CompleteSaleLineInput(seed.ProductId, seed.ProductUnitId, 1m, 5000m * factor, [original.Id])]), default);
        Assert.True(sale.IsSuccess, sale.Error?.Message);
        db.ChangeTracker.Clear();
        var item = await db.SaleItems.SingleAsync(x => x.SaleId == sale.Value!.SaleId);
        var intent = new CommercialExchangeCommand(Guid.NewGuid(), sale.Value!.SaleId, seed.ActorId, null, customer.Id,
            "EXCHANGE", "Exact swap", [new SaleReturnLineInput(item.Id, factor, SaleReturnDisposition.RestockSellable, [original.Id])],
            [new CompleteSaleLineInput(seed.ProductId, seed.ProductUnitId, 1m, 5000m * factor, missingIds ? [] : [replacement.Id])],
            0m, SalePaymentMethod.Bank, 0m, "EVEN");
        var exchanged = await services.GetRequiredService<CommercialExchangeHandler>().HandleAsync(intent, default);
        if (missingIds)
        {
            Assert.Equal("sales.serial_count_mismatch", exchanged.Error?.Code);
            db.ChangeTracker.Clear();
            Assert.Equal(InventoryUnitStatus.Sold, (await db.InventoryUnits.SingleAsync(x => x.Id == original.Id)).Status);
            Assert.Equal(InventoryUnitStatus.InStock, (await db.InventoryUnits.SingleAsync(x => x.Id == replacement.Id)).Status);
            Assert.False(await db.SaleReturns.AnyAsync(x => x.SaleId == sale.Value.SaleId));
            Assert.Equal(150m, (await db.ProductCostStates.SingleAsync(x => x.ProductId == seed.ProductId)).TotalInventoryCost);
            return;
        }
        Assert.True(exchanged.IsSuccess, exchanged.Error?.Message);
        var replay = await services.GetRequiredService<CommercialExchangeHandler>().HandleAsync(intent, default);
        Assert.True(replay.IsSuccess, replay.Error?.Message);
        Assert.Equal(exchanged.Value!.SaleId, replay.Value!.SaleId);
        db.ChangeTracker.Clear();
        var returned = await db.InventoryUnits.SingleAsync(x => x.Id == original.Id);
        var sold = await db.InventoryUnits.SingleAsync(x => x.Id == replacement.Id);
        Assert.Equal(InventoryUnitStatus.InStock, returned.Status);
        Assert.Equal(InventoryUnitStatus.Sold, sold.Status);
        Assert.Equal(original.TrackingCode, returned.TrackingCode);
        Assert.Equal(replacement.TrackingCode, sold.TrackingCode);
        Assert.Equal((decimal)factor, (await db.StockBalances.SingleAsync(x => x.ProductId == seed.ProductId)).SellableQty);
        Assert.Equal(100m, (await db.ProductCostStates.SingleAsync(x => x.ProductId == seed.ProductId)).TotalInventoryCost);
        Assert.Equal((decimal)factor, (await db.InventoryLotBucketBalances.SingleAsync(x => x.LotId == returned.InventoryLotId && x.StockBucket == InventoryBucket.Sellable)).Quantity);
        Assert.Equal((decimal)factor, await services.GetRequiredService<IInventoryRepository>().GetPhysicalUnitBaseQuantitySnapshotAsync(returned, default));
        var replacementItem = await db.SaleItems.SingleAsync(x => x.SaleId == exchanged.Value.SaleId);
        Assert.Equal(replacement.Id, (await db.SaleItemUnits.SingleAsync(x => x.SaleItemId == replacementItem.Id)).InventoryUnitId);
        Assert.Equal(150m, replacementItem.TotalCostSnapshot);
        Assert.Equal(2, await db.InventoryUnits.CountAsync(x => x.ProductId == seed.ProductId));
        Assert.Equal(3, (await db.SupplierProducts.SingleAsync(x => x.ProductId == seed.ProductId)).NextItemSequence);
    }

    [Fact]
    public async Task FreeExactAdjustment_DoesNotRemovePaidUnitCarryingValue()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        var seed = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db);
        var freeReceipt = await services.GetRequiredService<CreateStockAdjustmentHandler>().HandleAsync(new CreateStockAdjustmentCommand(
            StockAdjustmentMode.Delta, StockAdjustmentReason.Other,
            [new StockAdjustmentItemCommand(seed.ProductId, seed.ProductUnitId, StockAdjustmentDirection.Increase,
                InventoryBucket.Sellable, 1m, 0m, seed.SupplierId, [new SerializedAdjustmentUnitCommand("FREE-" + Guid.NewGuid())])], seed.ActorId, Guid.NewGuid()), default);
        Assert.True(freeReceipt.IsSuccess, freeReceipt.Error?.Message);
        db.ChangeTracker.Clear();
        var free = await db.InventoryUnits.SingleAsync(x => x.ProductId == seed.ProductId);
        var paid = await ReceiveAsync(services, db, seed.ProductId, seed.ProductUnitId, seed.SupplierId, seed.ActorId, 100m);
        async Task RemoveAsync(Guid id)
        {
            var result = await services.GetRequiredService<CreateStockAdjustmentHandler>().HandleAsync(new CreateStockAdjustmentCommand(
                StockAdjustmentMode.Delta, StockAdjustmentReason.Other,
                [new StockAdjustmentItemCommand(seed.ProductId, seed.ProductUnitId, StockAdjustmentDirection.Decrease,
                    InventoryBucket.Sellable, 1m, null, InventoryUnitIds: [id])], seed.ActorId, Guid.NewGuid()), default);
            Assert.True(result.IsSuccess, result.Error?.Message);
            db.ChangeTracker.Clear();
        }
        await RemoveAsync(free.Id);
        Assert.Equal(100m, (await db.ProductCostStates.SingleAsync(x => x.ProductId == seed.ProductId)).TotalInventoryCost);
        Assert.Equal(InventoryUnitStatus.InStock, (await db.InventoryUnits.SingleAsync(x => x.Id == paid.Id)).Status);
        await RemoveAsync(paid.Id);
        Assert.Equal(0m, (await db.ProductCostStates.SingleAsync(x => x.ProductId == seed.ProductId)).TotalInventoryCost);
    }

    [Theory]
    [InlineData(TrackingMode.Serialized, 1)]
    [InlineData(TrackingMode.IndividualPiece, 1)]
    [InlineData(TrackingMode.Container, 1)]
    [InlineData(TrackingMode.Container, 2)]
    public async Task CatalogScannerDraftAndStocktake_ReadContractsClassifyPhysicalModes(TrackingMode mode, int factor)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        var seed = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db);
        var product = await db.Products.SingleAsync(x => x.Id == seed.ProductId);
        product.TrackingMode = mode;
        var category = new Category { Name = "Read challenge-" + Guid.NewGuid(), IdentitySymbol = "R" + Guid.NewGuid().ToString("N")[..3].ToUpperInvariant() };
        product.CategoryId = category.Id;
        db.Categories.Add(category);
        var productUnit = await db.ProductUnits.SingleAsync(x => x.Id == seed.ProductUnitId);
        productUnit.FactorToBaseUnit = factor;
        productUnit.IsDefaultSaleUnit = true;
        var barcode = "CHALLENGE-" + Guid.NewGuid();
        db.ProductUnitBarcodes.Add(new ProductUnitBarcode { ProductUnitId = productUnit.Id, Barcode = barcode });
        await db.SaveChangesAsync();
        var unit = await ReceiveAsync(services, db, seed.ProductId, seed.ProductUnitId, seed.SupplierId, seed.ActorId, 100m);
        var catalog = await new PosCatalogReadService(db).GetSellableCatalogAsync(product.Sku, 200, default);
        Assert.True(Assert.Single(catalog, x => x.ProductId == seed.ProductId).IsSerialized);
        var reads = services.GetRequiredService<IPhase4WorkflowReadService>();
        var purchaseRow = Assert.Single(await new PurchaseCatalogReadService(db).GetPurchasableCatalogAsync(default), x => x.ProductId == seed.ProductId);
        Assert.True(purchaseRow.IsSerialized);
        Assert.Equal(mode, purchaseRow.TrackingMode);
        var purchased = await db.PurchaseItems.SingleAsync(x => x.Id == unit.SourcePurchaseItemId);
        var document = await services.GetRequiredService<IPurchasingReadService>().GetDocumentAsync(new GetPurchaseDocumentQuery(purchased.PurchaseId), default);
        Assert.True(Assert.Single(document!.Items).IsSerialized);
        Assert.Equal(mode, Assert.Single(document.Items).TrackingMode);
        var inventoryRow = Assert.Single(await new InventoryOverviewReadService(db).GetStockPageAsync(new InventoryStockPageQuery(Search: product.Sku, PageSize: 500), default), x => x.ProductId == seed.ProductId);
        Assert.True(inventoryRow.IsSerialized);
        Assert.Equal(mode, inventoryRow.TrackingMode);
        var thakaRow = Assert.Single(await services.GetRequiredService<IThakaReadService>().GetMaterialCatalogAsync(default), x => x.ProductId == seed.ProductId);
        Assert.True(thakaRow.IsSerialized);
        Assert.Equal(mode, thakaRow.TrackingMode);
        Assert.Equal((decimal)factor, thakaRow.FactorToBaseUnit);
        Assert.True(Assert.Single(await reads.ResolveScannerAsync(product.Sku!, default)).IsSerialized);
        Assert.True(Assert.Single(await reads.ResolveScannerAsync(barcode, default)).IsSerialized);
        Assert.True(Assert.Single(await reads.ResolveScannerAsync(unit.TrackingCode!, default)).IsSerialized);
        Assert.Equal(unit.Id, Assert.Single(await reads.GetExactUnitsAsync(seed.ProductId, InventoryUnitStatus.InStock, null, default)).InventoryUnitId);
        var missing = await services.GetRequiredService<SavePosDraftHandler>().HandleAsync(new SavePosDraftCommand(
            null, null, null, seed.ActorId, null, null, [new SavePosDraftItemInput(seed.ProductId, seed.ProductUnitId, 1m)]), default);
        Assert.Equal("sales.draft_exact_unit_required", missing.Error?.Code);
        var saved = await services.GetRequiredService<SavePosDraftHandler>().HandleAsync(new SavePosDraftCommand(
            null, null, null, seed.ActorId, null, null, [new SavePosDraftItemInput(seed.ProductId, seed.ProductUnitId, 1m, unit.Id)]), default);
        Assert.True(saved.IsSuccess, saved.Error?.Message);
        var draft = await reads.GetDraftAsync(saved.Value!.DraftId, default);
        Assert.Equal(unit.Id, Assert.Single(draft!.Items).SelectedUnit!.InventoryUnitId);
        var stocktake = await services.GetRequiredService<CreateStocktakeHandler>().HandleAsync(new CreateStocktakeCommand(
            StocktakeScope.Category, category.Id, seed.ActorId, "Read contract", Guid.NewGuid()), default);
        Assert.True(stocktake.IsSuccess, stocktake.Error?.Message);
        try
        {
            Assert.True((await services.GetRequiredService<StartStocktakeHandler>().HandleAsync(new StartStocktakeCommand(stocktake.Value, seed.ActorId, Guid.NewGuid()), default)).IsSuccess);
            var snapshot = await reads.GetOpenStocktakeAsync(default);
            Assert.True(Assert.Single(snapshot!.Items, x => x.ProductId == seed.ProductId).IsSerialized);
        }
        finally
        {
            Assert.True((await services.GetRequiredService<CancelStocktakeHandler>().HandleAsync(new CancelStocktakeCommand(stocktake.Value, seed.ActorId, Guid.NewGuid()), default)).IsSuccess);
        }
    }

    private static async Task<InventoryUnit> ReceiveAsync(IServiceProvider services, EdgeRetailsDbContext db,
        Guid productId, Guid productUnitId, Guid supplierId, Guid actorId, decimal cost)
    {
        var priorIds = await db.InventoryUnits.Where(x => x.ProductId == productId).Select(x => x.Id).ToArrayAsync();
        var result = await services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new CreatePurchaseCommand(
            supplierId, "CHALLENGE-" + Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), null, 0m,
            PurchaseSettlementMode.External, actorId, Guid.NewGuid(),
            [new CreatePurchaseLineInput(productId, productUnitId, 1m, cost, 5000m, [new SerializedIdentityInput("CHALLENGE-" + Guid.NewGuid())])]), default);
        Assert.True(result.IsSuccess, result.Error?.Message);
        db.ChangeTracker.Clear();
        return await db.InventoryUnits.SingleAsync(x => x.ProductId == productId && !priorIds.Contains(x.Id));
    }
}
