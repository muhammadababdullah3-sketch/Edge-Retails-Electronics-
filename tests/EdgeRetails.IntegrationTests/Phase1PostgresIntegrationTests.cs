using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EdgeRetails.IntegrationTests;

[Collection("Phase2PostgresIntegration")]
public sealed class Phase1PostgresIntegrationTests
{
    private static ServiceProvider BuildProvider()
        // HARNESS_CORRECTION: use the owned, initialized high-water authority.
        => Phase2PostgresTestHarness.BuildProvider();

    [Fact]
    public async Task StockAdjustment_And_Items_Persist_With_Relational_Integrity_And_RESTRICT_Foreign_Keys()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        var actorId = await IntegrationIdentitySeeder.CreateActorAsync(db);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var unit = new Unit { Name = "Unit-" + suffix, Symbol = "u-" + suffix, DisplayDecimalPlaces = 0 };
        var product = new Product
        {
            Name = "Product-" + suffix,
            Sku = ("SKU-" + suffix).ToUpperInvariant(),
            BaseUnitId = unit.Id,
            TrackingMode = TrackingMode.Quantity,
            DefaultSalePrice = 1000m,
            IsActive = true
        };
        db.Units.Add(unit);
        db.Products.Add(product);
        await db.SaveChangesAsync();

        var adjustment = new StockAdjustment
        {
            AdjustmentNumber = "ADJ-TEST-" + suffix,
            Mode = StockAdjustmentMode.Delta,
            Reason = StockAdjustmentReason.Other,
            Status = StockAdjustmentStatus.Posted,
            ActorId = actorId,
            OccurredAt = DateTimeOffset.UtcNow,
            CorrelationId = Guid.NewGuid(),
            Version = 1,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.StockAdjustments.Add(adjustment);

        var item = new StockAdjustmentItem
        {
            StockAdjustmentId = adjustment.Id,
            ProductId = product.Id,
            Direction = StockAdjustmentDirection.Increase,
            TargetBucket = InventoryBucket.Sellable,
            BaseQuantity = 10m,
            UnitCostSnapshot = 500m,
            TotalCostSnapshot = 5000m,
            ReasonDetails = "Initial test intake"
        };
        db.StockAdjustmentItems.Add(item);

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // Query back and verify persistence
        var loadedAdjustment = await db.StockAdjustments
            .AsNoTracking()
            .SingleAsync(x => x.Id == adjustment.Id);
        Assert.Equal("ADJ-TEST-" + suffix, loadedAdjustment.AdjustmentNumber);
        Assert.Equal(StockAdjustmentStatus.Posted, loadedAdjustment.Status);

        var loadedItem = await db.StockAdjustmentItems
            .AsNoTracking()
            .SingleAsync(x => x.Id == item.Id);
        Assert.Equal(10m, loadedItem.BaseQuantity);
        Assert.Equal(500m, loadedItem.UnitCostSnapshot);
        Assert.Equal(5000m, loadedItem.TotalCostSnapshot);

        // Attempting to delete the parent product must fail due to ON DELETE RESTRICT
        var prodToDelete = await db.Products.FindAsync(product.Id);
        db.Products.Remove(prodToDelete!);
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public async Task InventoryUnit_Origin_Provenance_Constraint_Enforces_StockAdjustment_Rules()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var unit = new Unit { Name = "Unit-" + suffix, Symbol = "u-" + suffix, DisplayDecimalPlaces = 0 };
        var product = new Product
        {
            Name = "Serialized Product-" + suffix,
            Sku = ("SER-" + suffix).ToUpperInvariant(),
            BaseUnitId = unit.Id,
            TrackingMode = TrackingMode.Serialized,
            SerialTrackingEnabled = true,
            DefaultSalePrice = 5000m,
            IsActive = true
        };
        var supplier = new Supplier { Name = "Supplier-" + suffix, DealerCode = TraceabilityCodeRules.BuildDealerCode("SP", Random.Shared.Next(1000, 999999)), IsActive = true };
        var supplierProduct = new SupplierProduct { SupplierId = supplier.Id, ProductId = product.Id, NextItemSequence = 2 };

        db.Units.Add(unit);
        db.Products.Add(product);
        db.Suppliers.Add(supplier);
        db.SupplierProducts.Add(supplierProduct);

        var actorId = await IntegrationIdentitySeeder.CreateActorAsync(db);

        var adjustment = new StockAdjustment
        {
            AdjustmentNumber = "ADJ-CHK-" + suffix,
            Mode = StockAdjustmentMode.Delta,
            Reason = StockAdjustmentReason.Other,
            Status = StockAdjustmentStatus.Posted,
            ActorId = actorId,
            OccurredAt = DateTimeOffset.UtcNow,
            CorrelationId = Guid.NewGuid(),
            Version = 1,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.StockAdjustments.Add(adjustment);

        var adjItem = new StockAdjustmentItem
        {
            StockAdjustmentId = adjustment.Id,
            ProductId = product.Id,
            Direction = StockAdjustmentDirection.Increase,
            TargetBucket = InventoryBucket.Sellable,
            BaseQuantity = 1m,
            UnitCostSnapshot = 2500m,
            TotalCostSnapshot = 2500m,
            SupplierId = supplier.Id,
            SupplierProductId = supplierProduct.Id
        };
        db.StockAdjustmentItems.Add(adjItem);
        await db.SaveChangesAsync();

        // 1. Valid StockAdjustment origin unit persists cleanly
        var validUnit = new InventoryUnit
        {
            ProductId = product.Id,
            SupplierProductId = supplierProduct.Id,
            OriginType = InventoryUnitOriginType.StockAdjustment,
            SourceStockAdjustmentItemId = adjItem.Id,
            SourcePurchaseItemId = null,
            ItemSequence = 1,
            TrackingCode = $"SP-SER-{suffix}-000001",
            SerialNumber = "SN-VAL-" + suffix,
            Status = InventoryUnitStatus.InStock,
            AcquisitionCost = 2500m,
            CreatedAt = DateTimeOffset.UtcNow,
            Version = 1
        };
        // HARNESS_CORRECTION: current identity authority requires claim + owner;
        // repository insertion establishes both without bypassing provenance checks.
        var inventory = scope.ServiceProvider.GetRequiredService<IInventoryRepository>();
        inventory.AddInventoryUnit(validUnit);
        await db.SaveChangesAsync();

        // 2. Invalid OriginType = StockAdjustment with missing SourceStockAdjustmentItemId must fail CHECK constraint
        var invalidUnitMissingSource = new InventoryUnit
        {
            ProductId = product.Id,
            SupplierProductId = supplierProduct.Id,
            OriginType = InventoryUnitOriginType.StockAdjustment,
            SourceStockAdjustmentItemId = null, // Invalid!
            ItemSequence = 2,
            TrackingCode = $"SP-SER-{suffix}-000002",
            SerialNumber = "SN-INV1-" + suffix,
            Status = InventoryUnitStatus.InStock,
            AcquisitionCost = 2500m,
            CreatedAt = DateTimeOffset.UtcNow,
            Version = 1
        };
        inventory.AddInventoryUnit(invalidUnitMissingSource);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        db.ChangeTracker.Clear();

        // 3. Invalid OriginType = Purchase with SourceStockAdjustmentItemId must fail CHECK constraint
        var invalidUnitMixed = new InventoryUnit
        {
            ProductId = product.Id,
            SupplierProductId = supplierProduct.Id,
            OriginType = InventoryUnitOriginType.Purchase,
            SourcePurchaseItemId = Guid.NewGuid(), // Fake purchase item
            SourceStockAdjustmentItemId = adjItem.Id, // Mixed! Invalid!
            ItemSequence = 3,
            TrackingCode = $"SP-SER-{suffix}-000003",
            SerialNumber = "SN-INV2-" + suffix,
            Status = InventoryUnitStatus.InStock,
            AcquisitionCost = 2500m,
            CreatedAt = DateTimeOffset.UtcNow,
            Version = 1
        };
        inventory.AddInventoryUnit(invalidUnitMixed);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task StockAdjustment_Positive_And_Negative_Full_Cycle_On_Postgres()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();

        var actorId = await IntegrationIdentitySeeder.CreateActorAsync(db);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var unit = new Unit { Name = "Piece-" + suffix, Symbol = "pc-" + suffix, DisplayDecimalPlaces = 0 };
        var product = new Product
        {
            Name = "Air Conditioner-" + suffix,
            Sku = ("AC-" + suffix).ToUpperInvariant(),
            BaseUnitId = unit.Id,
            TrackingMode = TrackingMode.Serialized,
            SerialTrackingEnabled = true,
            ImeiTrackingEnabled = false,
            ReferencePurchaseCost = 80000m,
            DefaultSalePrice = 95000m,
            IsActive = true
        };
        var supplier = new Supplier { Name = "Gree Pakistan-" + suffix, DealerCode = TraceabilityCodeRules.BuildDealerCode("GP", Random.Shared.Next(1000, 999999)), IsActive = true };

        db.Units.Add(unit);
        db.Products.Add(product);
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();

        var handler = ActivatorUtilities.CreateInstance<CreateStockAdjustmentHandler>(services);

        var serial1 = "SN-AC-1-" + suffix;
        var serial2 = "SN-AC-2-" + suffix;

        // 1. Positive serialized intake via stock adjustment
        var intakeCommand = new CreateStockAdjustmentCommand(
            StockAdjustmentMode.Delta,
            StockAdjustmentReason.OpeningStock,
            [
                new StockAdjustmentItemCommand(
                    product.Id,
                    null,
                    StockAdjustmentDirection.Increase,
                    InventoryBucket.Sellable,
                    2m,
                    78000m,
                    supplier.Id,
                    [
                        new SerializedAdjustmentUnitCommand(serial1),
                        new SerializedAdjustmentUnitCommand(serial2)
                    ],
                    ReasonDetails: "Initial opening stock audit")
            ],
            actorId,
            Guid.NewGuid(),
            Note: "Opening stock 2026");

        var intakeResult = await handler.HandleAsync(intakeCommand, CancellationToken.None);
        Assert.True(intakeResult.IsSuccess, intakeResult.Error?.Message);

        db.ChangeTracker.Clear();

        // Verify database state after positive adjustment
        var balance = await db.StockBalances.SingleAsync(x => x.ProductId == product.Id);
        Assert.Equal(2m, balance.SellableQty);

        var costState = await db.ProductCostStates.SingleAsync(x => x.ProductId == product.Id);
        Assert.Equal(2m, costState.CostedQty);
        Assert.Equal(156000m, costState.TotalInventoryCost);
        Assert.Equal(78000m, costState.MovingAverageCost);

        var lots = await db.InventoryLots.Where(x => x.ProductId == product.Id).ToListAsync();
        Assert.Single(lots);
        var lot = lots[0];
        Assert.Equal(2m, lot.ReceivedQuantity);
        Assert.Equal(78000m, lot.OriginalUnitCost);
        Assert.Null(lot.PurchaseItemId);

        var lotBalances = await db.InventoryLotBucketBalances.Where(x => x.LotId == lot.Id).ToListAsync();
        Assert.Single(lotBalances);
        Assert.Equal(2m, lotBalances[0].Quantity);
        Assert.Equal(InventoryBucket.Sellable, lotBalances[0].StockBucket);

        var units = await db.InventoryUnits.Where(x => x.ProductId == product.Id).OrderBy(x => x.ItemSequence).ToListAsync();
        Assert.Equal(2, units.Count);
        Assert.All(units, u =>
        {
            Assert.Equal(InventoryUnitOriginType.StockAdjustment, u.OriginType);
            Assert.NotNull(u.SourceStockAdjustmentItemId);
            Assert.Null(u.SourcePurchaseItemId);
            Assert.Equal(lot.Id, u.InventoryLotId);
            Assert.Equal(InventoryUnitStatus.InStock, u.Status);
            Assert.Equal(78000m, u.AcquisitionCost);
        });

        // HARNESS_CORRECTION + ASSERTION_CHANGE: damage is a recoverable
        // condition transfer; the explicit subsequent Scrap operation recognizes loss.
        // Preserve the original exact-unit disposition and carrying-value assertions.
        var unitToWriteOff = units[0];
        var conditionHandler = ActivatorUtilities.CreateInstance<TransferInventoryConditionHandler>(services);
        var damageResult = await conditionHandler.HandleAsync(new TransferInventoryConditionCommand(
            product.Id, InventoryBucket.Sellable, InventoryBucket.Damaged, 1m, actorId,
            "Damaged in warehouse transit", InventoryUnitIds: [unitToWriteOff.Id], CorrelationId: Guid.NewGuid()), CancellationToken.None);
        Assert.True(damageResult.IsSuccess, damageResult.Error?.Message);
        db.ChangeTracker.Clear();
        var writeOffResult = await conditionHandler.HandleAsync(new TransferInventoryConditionCommand(
            product.Id, InventoryBucket.Damaged, InventoryBucket.Scrap, 1m, actorId,
            "Write-off damaged unit", InventoryUnitIds: [unitToWriteOff.Id], CorrelationId: Guid.NewGuid()), CancellationToken.None);
        Assert.True(writeOffResult.IsSuccess, writeOffResult.Error?.Message);

        db.ChangeTracker.Clear();

        // Verify database state after negative adjustment
        var balanceAfter = await db.StockBalances.SingleAsync(x => x.ProductId == product.Id);
        Assert.Equal(1m, balanceAfter.SellableQty);

        var costStateAfter = await db.ProductCostStates.SingleAsync(x => x.ProductId == product.Id);
        Assert.Equal(1m, costStateAfter.CostedQty);
        Assert.Equal(78000m, costStateAfter.TotalInventoryCost);
        Assert.Equal(78000m, costStateAfter.MovingAverageCost);

        var lotBalancesAfter = await db.InventoryLotBucketBalances.Where(x => x.LotId == lot.Id).ToListAsync();
        Assert.Equal(1m, Assert.Single(lotBalancesAfter, x => x.StockBucket == InventoryBucket.Sellable).Quantity);
        Assert.Equal(0m, Assert.Single(lotBalancesAfter, x => x.StockBucket == InventoryBucket.Damaged).Quantity);
        Assert.Equal(1m, Assert.Single(lotBalancesAfter, x => x.StockBucket == InventoryBucket.Scrap).Quantity);
        Assert.Equal(2m, lotBalancesAfter.Sum(x => x.Quantity));

        var consumptions = await db.InventoryLotConsumptions.Where(x => x.LotId == lot.Id).ToListAsync();
        Assert.Empty(consumptions);
        var scrapMovement = await db.InventoryMovements.SingleAsync(x => x.Id == writeOffResult.Value);
        Assert.Equal(78000m, scrapMovement.RecognizedLossAmount);
        Assert.Equal(unitToWriteOff.Id, (await db.InventoryMovementUnits.SingleAsync(x => x.MovementId == scrapMovement.Id)).InventoryUnitId);

        var writtenOffUnit = await db.InventoryUnits.SingleAsync(x => x.Id == unitToWriteOff.Id);
        Assert.Equal(InventoryUnitStatus.Scrapped, writtenOffUnit.Status);

        var remainingUnit = await db.InventoryUnits.SingleAsync(x => x.Id == units[1].Id);
        Assert.Equal(InventoryUnitStatus.InStock, remainingUnit.Status);

        // Verify zero purchases and zero supplier account entries exist
        var purchaseCount = await db.Purchases.CountAsync(x => x.SupplierId == supplier.Id);
        Assert.Equal(0, purchaseCount);

        var supplierEntryCount = await db.SupplierAccountEntries.CountAsync(x => x.SupplierId == supplier.Id);
        Assert.Equal(0, supplierEntryCount);
    }
}
