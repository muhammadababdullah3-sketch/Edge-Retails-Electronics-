using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Parties;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Features.Thaka;
using EdgeRetails.Application.Features.Warranty;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Warranty;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.IntegrationTests;

[Collection("TrackingManufacturerIdentityPg")]
public sealed class TrackingGoldenTracePostgresTests
{
    [Theory]
    [InlineData(TrackingMode.Serialized, 1)]
    [InlineData(TrackingMode.IndividualPiece, 1)]
    [InlineData(TrackingMode.Container, 1)]
    [InlineData(TrackingMode.Container, 2)]
    public async Task GoldenTrace_ReceiveScanSellReturnRepairAndStocktakePreservePhysicalIdentity(TrackingMode mode, int factor)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var seed = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db);
        var category = new Category { Name = "Golden-" + Guid.NewGuid(), IdentitySymbol = "G" + Guid.NewGuid().ToString("N")[..3].ToUpperInvariant() };
        db.Categories.Add(category);
        var product = await db.Products.SingleAsync(x => x.Id == seed.ProductId);
        product.TrackingMode = mode;
        product.CategoryId = category.Id;
        (await db.ProductUnits.SingleAsync(x => x.Id == seed.ProductUnitId)).FactorToBaseUnit = factor;
        await db.SaveChangesAsync();
        var supplier = await services.GetRequiredService<SaveSupplierHandler>().HandleAsync(new SaveSupplierCommand(
            null, "Golden Supplier " + Guid.NewGuid(), null, null, null, true, seed.ActorId, Guid.NewGuid()), default);
        Assert.True(supplier.IsSuccess, supplier.Error?.Message);
        var serial = "GOLDEN-" + Guid.NewGuid().ToString("N");
        var purchase = await services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new CreatePurchaseCommand(
            supplier.Value, "GOLDEN-" + Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), null, 0m,
            PurchaseSettlementMode.External, seed.ActorId, Guid.NewGuid(),
            [new CreatePurchaseLineInput(seed.ProductId, seed.ProductUnitId, 1m, 100m, 5000m, [new SerializedIdentityInput(serial)])]), default);
        Assert.True(purchase.IsSuccess, purchase.Error?.Message);
        db.ChangeTracker.Clear();
        var unit = await db.InventoryUnits.AsNoTracking().SingleAsync(x => x.ProductId == seed.ProductId);
        var identity = (unit.Id, unit.TrackingCode, unit.ItemSequence, unit.SupplierCodeSnapshot, unit.ProductSkuSnapshot,
            unit.SupplierProductId, unit.SourcePurchaseItemId);
        Assert.Equal(1, unit.ItemSequence);
        Assert.Equal(InventoryUnitOriginType.Purchase, unit.OriginType);
        Assert.Equal(unit.Id, (await db.InventoryUnitIdentityClaims.SingleAsync(x => x.NormalizedValue == serial.ToUpperInvariant())).InventoryUnitId);
        var reads = services.GetRequiredService<IPhase4WorkflowReadService>();
        var trackingMatch = Assert.Single(await reads.ResolveScannerAsync(unit.TrackingCode!, default));
        Assert.Equal(ScannerResolutionNamespace.TrackingCode, trackingMatch.Namespace);
        var serialMatch = Assert.Single(await reads.ResolveScannerAsync(" " + serial.ToLowerInvariant() + " ", default));
        Assert.Equal(ScannerResolutionNamespace.SerialNumber, serialMatch.Namespace);
        var customer = await Phase2PostgresTestHarness.SeedCustomerAsync(db);
        var sale = await services.GetRequiredService<CompleteSaleHandler>().HandleAsync(new CompleteSaleCommand(
            Guid.NewGuid(), customer.Id, seed.ActorId, null, 0m, SalePaymentMethod.Bank, 5000m * factor, "BANK", null,
            [new CompleteSaleLineInput(seed.ProductId, seed.ProductUnitId, 1m, 5000m * factor, [unit.Id])]), default);
        Assert.True(sale.IsSuccess, sale.Error?.Message);
        db.ChangeTracker.Clear();
        Assert.Equal(InventoryUnitStatus.Sold, (await db.InventoryUnits.SingleAsync(x => x.Id == unit.Id)).Status);
        var saleItem = await db.SaleItems.SingleAsync(x => x.SaleId == sale.Value!.SaleId);
        var returned = await services.GetRequiredService<CreateSaleReturnHandler>().HandleAsync(new CreateSaleReturnCommand(
            sale.Value!.SaleId, "DEFECTIVE", "Golden trace", RefundMethod.Bank, seed.ActorId, Guid.NewGuid(),
            [new SaleReturnLineInput(saleItem.Id, factor, SaleReturnDisposition.Damaged, [unit.Id])]), default);
        Assert.True(returned.IsSuccess, returned.Error?.Message);
        db.ChangeTracker.Clear();
        Assert.Equal(InventoryUnitStatus.Damaged, (await db.InventoryUnits.SingleAsync(x => x.Id == unit.Id)).Status);
        var sent = await services.GetRequiredService<SendShopStockToSupplierWarrantyHandler>().HandleAsync(new SendShopStockToSupplierWarrantyCommand(
            seed.ProductId, InventoryBucket.Damaged, factor, supplier.Value, unit.SourcePurchaseItemId, "Fault", seed.ActorId, Guid.NewGuid(), [unit.Id]), default);
        Assert.True(sent.IsSuccess, sent.Error?.Message);
        var repaired = await services.GetRequiredService<ReceiveShopStockWarrantyHandler>().HandleAsync(new ReceiveShopStockWarrantyCommand(
            sent.Value, WarrantyResolutionType.Repaired, seed.ActorId, [unit.Id], null, null, Guid.NewGuid()), default);
        Assert.True(repaired.IsSuccess, repaired.Error?.Message);
        db.ChangeTracker.Clear();
        var finalUnit = await db.InventoryUnits.SingleAsync(x => x.Id == unit.Id);
        Assert.Equal(InventoryUnitStatus.InStock, finalUnit.Status);
        Assert.Equal(identity, (finalUnit.Id, finalUnit.TrackingCode, finalUnit.ItemSequence, finalUnit.SupplierCodeSnapshot,
            finalUnit.ProductSkuSnapshot, finalUnit.SupplierProductId, finalUnit.SourcePurchaseItemId));
        Assert.Equal(1, await db.InventoryUnits.CountAsync(x => x.ProductId == seed.ProductId));
        Assert.Equal(2, (await db.SupplierProducts.SingleAsync(x => x.ProductId == seed.ProductId)).NextItemSequence);
        Assert.Equal((decimal)factor, (await db.StockBalances.SingleAsync(x => x.ProductId == seed.ProductId)).SellableQty);
        var stocktake = await services.GetRequiredService<CreateStocktakeHandler>().HandleAsync(new CreateStocktakeCommand(
            StocktakeScope.Category, category.Id, seed.ActorId, "Golden trace", Guid.NewGuid()), default);
        Assert.True(stocktake.IsSuccess, stocktake.Error?.Message);
        Assert.True((await services.GetRequiredService<StartStocktakeHandler>().HandleAsync(new StartStocktakeCommand(stocktake.Value, seed.ActorId, Guid.NewGuid()), default)).IsSuccess);
        var counted = await services.GetRequiredService<RecordSerializedStocktakeHandler>().HandleAsync(new RecordSerializedStocktakeCommand(
            stocktake.Value, seed.ProductId, [unit.Id], [], seed.ActorId, null, Guid.NewGuid()), default);
        Assert.True(counted.IsSuccess, counted.Error?.Message);
        db.ChangeTracker.Clear();
        var stocktakeItem = await db.StocktakeItems.SingleAsync(x => x.StocktakeId == stocktake.Value && x.ProductId == seed.ProductId);
        var check = await db.StocktakeUnitChecks.SingleAsync(x => x.StocktakeItemId == stocktakeItem.Id);
        Assert.Equal(unit.Id, check.InventoryUnitId);
        Assert.Equal(unit.TrackingCode, check.IdentitySnapshot);
        Assert.Equal(StocktakeUnitCheckResult.ExpectedAndFound, check.Result);
        Assert.Equal((decimal)factor, stocktakeItem.CountedSellableQty);
        Assert.True((await services.GetRequiredService<CancelStocktakeHandler>().HandleAsync(new CancelStocktakeCommand(stocktake.Value, seed.ActorId, Guid.NewGuid()), default)).IsSuccess);
    }

    [Fact]
    public async Task ContainerPack_SaleAndReturnPreserveBaseQuantityCostAndExactIdentity()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var seed = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db);
        (await db.Products.SingleAsync(x => x.Id == seed.ProductId)).TrackingMode = TrackingMode.Container;
        var packUnit = new Unit { Name = "Pack-" + Guid.NewGuid(), Symbol = "PK", DisplayDecimalPlaces = 0 };
        var productPack = new ProductUnit { ProductId = seed.ProductId, UnitId = packUnit.Id, FactorToBaseUnit = 2m, CanPurchase = true, CanSell = true };
        db.Units.Add(packUnit);
        db.ProductUnits.Add(productPack);
        await db.SaveChangesAsync();
        var purchase = await services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new CreatePurchaseCommand(
            seed.SupplierId, "PACK-" + Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), null, 0m,
            PurchaseSettlementMode.External, seed.ActorId, Guid.NewGuid(),
            [new CreatePurchaseLineInput(seed.ProductId, productPack.Id, 1m, 100m, 5000m,
                [new SerializedIdentityInput("PACK-" + Guid.NewGuid())])]), default);
        Assert.True(purchase.IsSuccess, purchase.Error?.Message);
        db.ChangeTracker.Clear();
        var physical = await db.InventoryUnits.SingleAsync(x => x.ProductId == seed.ProductId);
        Assert.Equal(100m, physical.AcquisitionCost);
        Assert.Equal(2m, (await db.StockBalances.SingleAsync(x => x.ProductId == seed.ProductId)).SellableQty);
        var customer = await Phase2PostgresTestHarness.SeedCustomerAsync(db);
        var wrongPack = await services.GetRequiredService<CompleteSaleHandler>().HandleAsync(new CompleteSaleCommand(
            Guid.NewGuid(), customer.Id, seed.ActorId, null, 0m, SalePaymentMethod.Bank, 5000m, "BANK", null,
            [new CompleteSaleLineInput(seed.ProductId, seed.ProductUnitId, 1m, 5000m, [physical.Id])]), default);
        Assert.Equal("sales.container_quantity_mismatch", wrongPack.Error?.Code);
        var sale = await services.GetRequiredService<CompleteSaleHandler>().HandleAsync(new CompleteSaleCommand(
            Guid.NewGuid(), customer.Id, seed.ActorId, null, 0m, SalePaymentMethod.Bank, 10000m, "BANK", null,
            [new CompleteSaleLineInput(seed.ProductId, productPack.Id, 1m, 10000m, [physical.Id])]), default);
        Assert.True(sale.IsSuccess, sale.Error?.Message);
        db.ChangeTracker.Clear();
        var item = await db.SaleItems.SingleAsync(x => x.SaleId == sale.Value!.SaleId);
        var returned = await services.GetRequiredService<CreateSaleReturnHandler>().HandleAsync(new CreateSaleReturnCommand(
            sale.Value!.SaleId, "DEFECTIVE", "Pack return", RefundMethod.Bank, seed.ActorId, Guid.NewGuid(),
            [new SaleReturnLineInput(item.Id, 2m, SaleReturnDisposition.RestockSellable, [physical.Id])]), default);
        Assert.True(returned.IsSuccess, returned.Error?.Message);
        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.InventoryUnits.CountAsync(x => x.ProductId == seed.ProductId));
        Assert.Equal(physical.TrackingCode, (await db.InventoryUnits.SingleAsync(x => x.Id == physical.Id)).TrackingCode);
        var stock = await db.StockBalances.SingleAsync(x => x.ProductId == seed.ProductId);
        Assert.Equal(2m, stock.SellableQty);
        Assert.Equal(100m, (await db.ProductCostStates.SingleAsync(x => x.ProductId == seed.ProductId)).TotalInventoryCost);
        var lotId = (await db.InventoryUnits.SingleAsync(x => x.Id == physical.Id)).InventoryLotId;
        Assert.Equal(2m, (await db.InventoryLotBucketBalances.SingleAsync(x => x.LotId == lotId && x.StockBucket == InventoryBucket.Sellable)).Quantity);
    }

    [Theory]
    [InlineData(TrackingMode.Serialized)]
    [InlineData(TrackingMode.IndividualPiece)]
    [InlineData(TrackingMode.Container)]
    public async Task ThakaIssue_PreservesOriginalIdentityAndChangesExactUnitCustody(TrackingMode mode)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var seed = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db);
        (await db.Products.SingleAsync(x => x.Id == seed.ProductId)).TrackingMode = mode;
        await db.SaveChangesAsync();
        var receipt = await services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new CreatePurchaseCommand(
            seed.SupplierId, "THAKA-" + Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), null, 0m,
            PurchaseSettlementMode.External, seed.ActorId, Guid.NewGuid(),
            [new CreatePurchaseLineInput(seed.ProductId, seed.ProductUnitId, 1m, 100m, 5000m,
                [new SerializedIdentityInput("THAKA-" + Guid.NewGuid())])]), default);
        Assert.True(receipt.IsSuccess, receipt.Error?.Message);
        db.ChangeTracker.Clear();
        var unit = await db.InventoryUnits.SingleAsync(x => x.ProductId == seed.ProductId);
        var tracking = unit.TrackingCode;
        var customer = await Phase2PostgresTestHarness.SeedCustomerAsync(db);
        var project = await services.GetRequiredService<CreateThakaProjectHandler>().HandleAsync(new CreateThakaProjectCommand(
            customer.Id, "Tracking project", null, null, DateOnly.FromDateTime(DateTime.UtcNow), seed.ActorId, Guid.NewGuid(), Guid.NewGuid()), default);
        Assert.True(project.IsSuccess, project.Error?.Message);
        var intent = new IssueThakaMaterialCommand(Guid.NewGuid(), project.Value, seed.ActorId, null,
            [new IssueThakaMaterialLineInput(seed.ProductId, seed.ProductUnitId, 1m, 5000m, [unit.Id])]);
        var issue = await services.GetRequiredService<IssueThakaMaterialHandler>().HandleAsync(intent, default);
        Assert.True(issue.IsSuccess, issue.Error?.Message);
        var retry = await services.GetRequiredService<IssueThakaMaterialHandler>().HandleAsync(intent, default);
        Assert.True(retry.IsSuccess, retry.Error?.Message);
        Assert.Equal(issue.Value!.MaterialIssueId, retry.Value!.MaterialIssueId);
        db.ChangeTracker.Clear();
        var issued = await db.InventoryUnits.SingleAsync(x => x.Id == unit.Id);
        Assert.Equal(InventoryUnitStatus.IssuedThaka, issued.Status);
        Assert.Equal(tracking, issued.TrackingCode);
        Assert.Equal(1, await db.InventoryUnits.CountAsync(x => x.ProductId == seed.ProductId));
        Assert.Equal(1, await db.ThakaMaterialIssueUnits.CountAsync(x => x.InventoryUnitId == unit.Id));
        Assert.Equal(0m, (await db.StockBalances.SingleAsync(x => x.ProductId == seed.ProductId)).SellableQty);
    }

    [Theory]
    [InlineData(TrackingMode.Serialized, 1)]
    [InlineData(TrackingMode.IndividualPiece, 1)]
    [InlineData(TrackingMode.Container, 1)]
    [InlineData(TrackingMode.Container, 2)]
    public async Task PurchaseReturn_PreservesExactIdentityAndOriginalBaseQuantity(TrackingMode mode, int factor)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        var seed = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db);
        (await db.Products.SingleAsync(x => x.Id == seed.ProductId)).TrackingMode = mode;
        (await db.ProductUnits.SingleAsync(x => x.Id == seed.ProductUnitId)).FactorToBaseUnit = factor;
        await db.SaveChangesAsync();
        var purchase = await services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new CreatePurchaseCommand(
            seed.SupplierId, "RETURN-" + Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), null, 0m,
            PurchaseSettlementMode.External, seed.ActorId, Guid.NewGuid(),
            [new CreatePurchaseLineInput(seed.ProductId, seed.ProductUnitId, 1m, 100m, 5000m,
                [new SerializedIdentityInput("RETURN-" + Guid.NewGuid())])]), default);
        Assert.True(purchase.IsSuccess, purchase.Error?.Message);
        db.ChangeTracker.Clear();
        var unit = await db.InventoryUnits.SingleAsync(x => x.ProductId == seed.ProductId);
        var item = await db.PurchaseItems.SingleAsync(x => x.PurchaseId == purchase.Value!.PurchaseId);
        var result = await services.GetRequiredService<CreatePurchaseReturnHandler>().HandleAsync(new CreatePurchaseReturnCommand(
            purchase.Value!.PurchaseId, "Tracking return", null, PurchaseReturnSettlementMode.External, seed.ActorId, Guid.NewGuid(),
            [new PurchaseReturnLineInput(item.Id, 1m, null, [unit.Id])]), default);
        Assert.True(result.IsSuccess, result.Error?.Message);
        db.ChangeTracker.Clear();
        var returned = await db.InventoryUnits.SingleAsync(x => x.Id == unit.Id);
        Assert.Equal(InventoryUnitStatus.SupplierReturned, returned.Status);
        Assert.Equal(unit.TrackingCode, returned.TrackingCode);
        Assert.Equal(1, await db.InventoryUnits.CountAsync(x => x.ProductId == seed.ProductId));
        Assert.Equal(0m, (await db.StockBalances.SingleAsync(x => x.ProductId == seed.ProductId)).SellableQty);
        Assert.Equal(0m, (await db.ProductCostStates.SingleAsync(x => x.ProductId == seed.ProductId)).TotalInventoryCost);
        Assert.Equal(100m, result.Value!.InventoryCostRemoved);
    }

    [Fact]
    public async Task ExactConditionTransfer_MovesSelectedUnitLotRatherThanAnotherFifoLot()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        var seed = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db);
        async Task<InventoryUnit> ReceiveAsync()
        {
            var serial = "LOT-" + Guid.NewGuid();
            var receipt = await services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new CreatePurchaseCommand(
                seed.SupplierId, serial, DateOnly.FromDateTime(DateTime.UtcNow), null, 0m,
                PurchaseSettlementMode.External, seed.ActorId, Guid.NewGuid(),
                [new CreatePurchaseLineInput(seed.ProductId, seed.ProductUnitId, 1m, 100m, 5000m, [new SerializedIdentityInput(serial)])]), default);
            Assert.True(receipt.IsSuccess, receipt.Error?.Message);
            db.ChangeTracker.Clear();
            return await db.InventoryUnits.SingleAsync(x => x.SerialNumber == serial.ToUpperInvariant());
        }
        var first = await ReceiveAsync();
        var selected = await ReceiveAsync();
        var transferred = await services.GetRequiredService<TransferInventoryConditionHandler>().HandleAsync(new TransferInventoryConditionCommand(
            seed.ProductId, InventoryBucket.Sellable, InventoryBucket.Damaged, 1m, seed.ActorId, "Exact lot", InventoryUnitIds: [selected.Id]), default);
        Assert.True(transferred.IsSuccess, transferred.Error?.Message);
        db.ChangeTracker.Clear();
        Assert.Equal(1m, (await db.InventoryLotBucketBalances.SingleAsync(x => x.LotId == first.InventoryLotId && x.StockBucket == InventoryBucket.Sellable)).Quantity);
        Assert.Equal(0m, (await db.InventoryLotBucketBalances.SingleAsync(x => x.LotId == selected.InventoryLotId && x.StockBucket == InventoryBucket.Sellable)).Quantity);
        Assert.Equal(1m, (await db.InventoryLotBucketBalances.SingleAsync(x => x.LotId == selected.InventoryLotId && x.StockBucket == InventoryBucket.Damaged)).Quantity);
        Assert.Equal(InventoryUnitStatus.InStock, (await db.InventoryUnits.SingleAsync(x => x.Id == first.Id)).Status);
        Assert.Equal(InventoryUnitStatus.Damaged, (await db.InventoryUnits.SingleAsync(x => x.Id == selected.Id)).Status);
    }

    [Theory]
    [InlineData(TrackingMode.Serialized, 1)]
    [InlineData(TrackingMode.IndividualPiece, 1)]
    [InlineData(TrackingMode.Container, 2)]
    public async Task StocktakeMissing_PostMarksMissingExistingExactUnitAndItsWholeQuantity(TrackingMode mode, int factor)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        var seed = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db);
        var category = new Category { Name = "Missing-" + Guid.NewGuid(), IdentitySymbol = "M" + Guid.NewGuid().ToString("N")[..3].ToUpperInvariant() };
        db.Categories.Add(category);
        var product = await db.Products.SingleAsync(x => x.Id == seed.ProductId);
        product.TrackingMode = mode;
        product.CategoryId = category.Id;
        (await db.ProductUnits.SingleAsync(x => x.Id == seed.ProductUnitId)).FactorToBaseUnit = factor;
        await db.SaveChangesAsync();
        var receipt = await services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new CreatePurchaseCommand(
            seed.SupplierId, "MISSING-" + Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), null, 0m,
            PurchaseSettlementMode.External, seed.ActorId, Guid.NewGuid(),
            [new CreatePurchaseLineInput(seed.ProductId, seed.ProductUnitId, 1m, 100m, 5000m, [new SerializedIdentityInput("MISSING-" + Guid.NewGuid())])]), default);
        Assert.True(receipt.IsSuccess, receipt.Error?.Message);
        db.ChangeTracker.Clear();
        var unit = await db.InventoryUnits.SingleAsync(x => x.ProductId == seed.ProductId);
        var created = await services.GetRequiredService<CreateStocktakeHandler>().HandleAsync(new CreateStocktakeCommand(
            StocktakeScope.Category, category.Id, seed.ActorId, "Missing unit", Guid.NewGuid()), default);
        Assert.True(created.IsSuccess, created.Error?.Message);
        try
        {
            var started = await services.GetRequiredService<StartStocktakeHandler>().HandleAsync(new StartStocktakeCommand(created.Value, seed.ActorId, Guid.NewGuid()), default);
            Assert.True(started.IsSuccess, started.Error?.Message);
            var counted = await services.GetRequiredService<RecordSerializedStocktakeHandler>().HandleAsync(new RecordSerializedStocktakeCommand(
                created.Value, seed.ProductId, [], [], seed.ActorId, "Missing", Guid.NewGuid()), default);
            Assert.True(counted.IsSuccess, counted.Error?.Message);
            var reviewed = await services.GetRequiredService<ReviewStocktakeHandler>().HandleAsync(new ReviewStocktakeCommand(created.Value, seed.ActorId, Guid.NewGuid()), default);
            Assert.True(reviewed.IsSuccess, reviewed.Error?.Message);
            var intent = new PostStocktakeCommand(created.Value, seed.ActorId, ClientOperationId: Guid.NewGuid());
            var posted = await services.GetRequiredService<PostStocktakeHandler>().HandleAsync(intent, default);
            Assert.True(posted.IsSuccess, posted.Error?.Message);
            Assert.True((await services.GetRequiredService<PostStocktakeHandler>().HandleAsync(intent, default)).IsSuccess);
            db.ChangeTracker.Clear();
            var missing = await db.InventoryUnits.SingleAsync(x => x.Id == unit.Id);
            Assert.Equal(InventoryUnitStatus.Missing, missing.Status);
            Assert.Equal(unit.TrackingCode, missing.TrackingCode);
            Assert.Equal(1, await db.InventoryUnits.CountAsync(x => x.ProductId == seed.ProductId));
            Assert.Equal(0m, (await db.StockBalances.SingleAsync(x => x.ProductId == seed.ProductId)).SellableQty);
            Assert.Equal(0m, (await db.ProductCostStates.SingleAsync(x => x.ProductId == seed.ProductId)).TotalInventoryCost);
        }
        finally
        {
            db.ChangeTracker.Clear();
            var stocktake = await db.Stocktakes.AsNoTracking().SingleAsync(x => x.Id == created.Value);
            if (stocktake.Status is not (StocktakeStatus.Posted or StocktakeStatus.Cancelled))
            {
                await services.GetRequiredService<CancelStocktakeHandler>().HandleAsync(new CancelStocktakeCommand(created.Value, seed.ActorId, Guid.NewGuid()), default);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContainerCreditAndLegacyCost_ConserveWholePackOrRequireReconciliation(bool legacyCost)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        var seed = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db);
        (await db.Products.SingleAsync(x => x.Id == seed.ProductId)).TrackingMode = TrackingMode.Container;
        (await db.ProductUnits.SingleAsync(x => x.Id == seed.ProductUnitId)).FactorToBaseUnit = 2m;
        await db.SaveChangesAsync();
        var receipt = await services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new CreatePurchaseCommand(
            seed.SupplierId, "CREDIT-" + Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), null, 0m,
            PurchaseSettlementMode.External, seed.ActorId, Guid.NewGuid(),
            [new CreatePurchaseLineInput(seed.ProductId, seed.ProductUnitId, 1m, 100m, 5000m, [new SerializedIdentityInput("CREDIT-" + Guid.NewGuid())])]), default);
        Assert.True(receipt.IsSuccess, receipt.Error?.Message);
        db.ChangeTracker.Clear();
        var unit = await db.InventoryUnits.SingleAsync(x => x.ProductId == seed.ProductId);
        Assert.Equal(100m, unit.AcquisitionCost);
        if (legacyCost)
        {
            // Owned fixture only: reproduce historical per-base cost on a pack
            // while preserving its committed identity and authoritative lot.
            unit.AcquisitionCost = 50m;
            await db.SaveChangesAsync();
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => services.GetRequiredService<IInventoryRepository>()
                .GetPhysicalUnitBaseQuantitySnapshotAsync(unit, default));
            Assert.Equal("inventory.physical_cost_reconciliation_required", ex.Code);
            db.ChangeTracker.Clear();
            Assert.Equal(50m, (await db.InventoryUnits.SingleAsync(x => x.Id == unit.Id)).AcquisitionCost);
            Assert.Equal(2m, (await db.StockBalances.SingleAsync(x => x.ProductId == seed.ProductId)).SellableQty);
            Assert.Equal(100m, (await db.ProductCostStates.SingleAsync(x => x.ProductId == seed.ProductId)).TotalInventoryCost);
            return;
        }
        var damaged = await services.GetRequiredService<TransferInventoryConditionHandler>().HandleAsync(new TransferInventoryConditionCommand(
            seed.ProductId, InventoryBucket.Sellable, InventoryBucket.Damaged, 2m, seed.ActorId, "Pack defect", InventoryUnitIds: [unit.Id]), default);
        Assert.True(damaged.IsSuccess, damaged.Error?.Message);
        var sent = await services.GetRequiredService<SendShopStockToSupplierWarrantyHandler>().HandleAsync(new SendShopStockToSupplierWarrantyCommand(
            seed.ProductId, InventoryBucket.Damaged, 2m, seed.SupplierId, unit.SourcePurchaseItemId, "Pack fault", seed.ActorId, Guid.NewGuid(), [unit.Id]), default);
        Assert.True(sent.IsSuccess, sent.Error?.Message);
        var intent = new ReceiveShopStockWarrantyCommand(sent.Value, WarrantyResolutionType.Credited, seed.ActorId,
            [unit.Id], null, null, Guid.NewGuid(), 100m, "PACK-CREDIT");
        var credited = await services.GetRequiredService<ReceiveShopStockWarrantyHandler>().HandleAsync(intent, default);
        Assert.True(credited.IsSuccess, credited.Error?.Message);
        Assert.True((await services.GetRequiredService<ReceiveShopStockWarrantyHandler>().HandleAsync(intent, default)).IsSuccess);
        db.ChangeTracker.Clear();
        var resolved = await db.InventoryUnits.SingleAsync(x => x.Id == unit.Id);
        Assert.Equal(InventoryUnitStatus.SupplierReturned, resolved.Status);
        Assert.Equal(unit.TrackingCode, resolved.TrackingCode);
        Assert.Equal(1, await db.InventoryUnits.CountAsync(x => x.ProductId == seed.ProductId));
        Assert.Equal(0m, (await db.StockBalances.SingleAsync(x => x.ProductId == seed.ProductId)).WithSupplierQty);
        Assert.Equal(0m, (await db.ProductCostStates.SingleAsync(x => x.ProductId == seed.ProductId)).TotalInventoryCost);
        Assert.Equal(1, await db.WarrantyOperations.CountAsync(x => x.ClientOperationId == intent.ClientOperationId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NegativeContainerAdjustment_WrongQuantityOrMissingLotFailsWithoutEffects(bool missingLot)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        var seed = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db);
        (await db.Products.SingleAsync(x => x.Id == seed.ProductId)).TrackingMode = TrackingMode.Container;
        var pack = new ProductUnit { ProductId = seed.ProductId, UnitId = seed.UnitId, FactorToBaseUnit = 2m, CanPurchase = true, CanSell = true };
        var packMeasure = new Unit { Name = "Negative pack-" + Guid.NewGuid(), Symbol = "NP" + Guid.NewGuid().ToString("N")[..8] };
        pack.UnitId = packMeasure.Id;
        db.Units.Add(packMeasure);
        db.ProductUnits.Add(pack);
        await db.SaveChangesAsync();
        var receipt = await services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new CreatePurchaseCommand(
            seed.SupplierId, "NEGATIVE-" + Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), null, 0m,
            PurchaseSettlementMode.External, seed.ActorId, Guid.NewGuid(),
            [new CreatePurchaseLineInput(seed.ProductId, pack.Id, 1m, 100m, 5000m, [new SerializedIdentityInput("NEGATIVE-" + Guid.NewGuid())])]), default);
        Assert.True(receipt.IsSuccess, receipt.Error?.Message);
        db.ChangeTracker.Clear();
        var unit = await db.InventoryUnits.SingleAsync(x => x.ProductId == seed.ProductId);
        if (missingLot)
        {
            (await db.InventoryLotBucketBalances.SingleAsync(x => x.LotId == unit.InventoryLotId && x.StockBucket == InventoryBucket.Sellable)).Quantity = 0m;
            await db.SaveChangesAsync();
        }
        var result = await services.GetRequiredService<CreateStockAdjustmentHandler>().HandleAsync(new CreateStockAdjustmentCommand(
            StockAdjustmentMode.Delta, StockAdjustmentReason.Other,
            [new StockAdjustmentItemCommand(seed.ProductId, missingLot ? pack.Id : seed.ProductUnitId,
                StockAdjustmentDirection.Decrease, InventoryBucket.Sellable, missingLot ? 2m : 1m,
                null, InventoryUnitIds: [unit.Id])], seed.ActorId, Guid.NewGuid()), default);
        Assert.Equal(missingLot ? "inventory.exact_unit_lot_insufficient" : "inventory.physical_quantity_mismatch", result.Error?.Code);
        db.ChangeTracker.Clear();
        Assert.Equal(InventoryUnitStatus.InStock, (await db.InventoryUnits.SingleAsync(x => x.Id == unit.Id)).Status);
        Assert.Equal(2m, (await db.StockBalances.SingleAsync(x => x.ProductId == seed.ProductId)).SellableQty);
        Assert.Equal(100m, (await db.ProductCostStates.SingleAsync(x => x.ProductId == seed.ProductId)).TotalInventoryCost);
        Assert.False(await db.StockAdjustmentItems.AnyAsync(x => x.ProductId == seed.ProductId));
        Assert.Equal(1, await db.InventoryUnits.CountAsync(x => x.ProductId == seed.ProductId));
    }

    [Theory]
    [InlineData(TrackingMode.Quantity)]
    [InlineData(TrackingMode.Length)]
    public async Task NonPhysicalModes_ReceivingDoesNotAllocateAnInventoryUnit(TrackingMode mode)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var seed = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);
        (await db.Products.SingleAsync(x => x.Id == seed.ProductId)).TrackingMode = mode;
        await db.SaveChangesAsync();
        var purchase = await scope.ServiceProvider.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new CreatePurchaseCommand(
            seed.SupplierId, "NONPHYSICAL-" + Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), null, 0m,
            PurchaseSettlementMode.External, seed.ActorId, Guid.NewGuid(),
            [new CreatePurchaseLineInput(seed.ProductId, seed.ProductUnitId, 1m, 100m, 150m, [])]), default);
        Assert.True(purchase.IsSuccess, purchase.Error?.Message);
        Assert.False(await db.InventoryUnits.AnyAsync(x => x.ProductId == seed.ProductId));
        Assert.Equal(1m, (await db.StockBalances.SingleAsync(x => x.ProductId == seed.ProductId)).SellableQty);
    }
}
