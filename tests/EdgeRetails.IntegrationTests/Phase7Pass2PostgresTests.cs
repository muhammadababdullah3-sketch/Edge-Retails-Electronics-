using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Thaka;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Thaka;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace EdgeRetails.IntegrationTests;

[Collection("Phase2PostgresIntegration")]
public sealed class Phase7Pass2PostgresTests(ITestOutputHelper output)
{
    [Fact]
    public async Task F01_StockAdjustment_SetPhysicalCount_QuantityProduct_ReducesStockAndCost()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);

        var fixture = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db, defaultSalePrice: 150m);

        // Seed 10 units @ 100m via purchase pipeline
        var purchaseHandler = services.GetRequiredService<CreatePurchaseHandler>();
        var purchase = await purchaseHandler.HandleAsync(new CreatePurchaseCommand(
            fixture.SupplierId,
            "PUR-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            DateOnly.FromDateTime(DateTime.UtcNow),
            null,
            0m,
            PurchaseSettlementMode.External,
            fixture.ActorId,
            Guid.NewGuid(),
            [new CreatePurchaseLineInput(fixture.ProductId, fixture.ProductUnitId, 10m, 100m, 150m, [])],
            ReceiveStockImmediately: true), default);
        Assert.True(purchase.IsSuccess, purchase.Error?.Message);

        var handler = services.GetRequiredService<CreateStockAdjustmentHandler>();
        var operationId = Guid.NewGuid();
        var command = new CreateStockAdjustmentCommand(
            StockAdjustmentMode.SetPhysicalCount,
            StockAdjustmentReason.PhysicalCountCorrection,
            [
                new StockAdjustmentItemCommand(
                    fixture.ProductId,
                    null,
                    StockAdjustmentDirection.Decrease,
                    InventoryBucket.Sellable,
                    4m, // Target 4 base units (delta = 4 - 10 = -6)
                    null)
            ],
            fixture.ActorId,
            operationId);

        var result = await handler.HandleAsync(command, CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error?.Message);

        // Verification in fresh scope against real PostgreSQL
        await using var verifyScope = provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var balance = await verifyDb.StockBalances.SingleAsync(x => x.ProductId == fixture.ProductId);
        Assert.Equal(4m, balance.SellableQty);

        var costState = await verifyDb.ProductCostStates.SingleAsync(x => x.ProductId == fixture.ProductId);
        Assert.Equal(4m, costState.CostedQty);
        Assert.Equal(400m, costState.TotalInventoryCost);

        output.WriteLine("F01 Quantity SetPhysicalCount verified on real PostgreSQL: stock reduced from 10 to 4, cost pool from 1000 to 400.");
    }

    [Fact]
    public async Task F01_StockAdjustment_SetPhysicalCount_SerializedProduct_NegativeDeltaTransitionsToMissing()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);

        var fixture = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db, defaultSalePrice: 5000m);

        // Seed 2 serialized units @ 3000m via purchase pipeline
        var purchaseHandler = services.GetRequiredService<CreatePurchaseHandler>();
        var purchase = await purchaseHandler.HandleAsync(new CreatePurchaseCommand(
            fixture.SupplierId,
            "PUR-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            DateOnly.FromDateTime(DateTime.UtcNow),
            null,
            0m,
            PurchaseSettlementMode.External,
            fixture.ActorId,
            Guid.NewGuid(),
            [new CreatePurchaseLineInput(fixture.ProductId, fixture.ProductUnitId, 2m, 3000m, 5000m,
                [new SerializedIdentityInput("SN-PG-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()),
                 new SerializedIdentityInput("SN-PG-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant())])],
            ReceiveStockImmediately: true), default);
        Assert.True(purchase.IsSuccess, purchase.Error?.Message);

        var units = await db.InventoryUnits.Where(x => x.ProductId == fixture.ProductId).OrderBy(x => x.ItemSequence).ToListAsync();
        Assert.Equal(2, units.Count);
        var unit1 = units[0];
        var unit2 = units[1];

        var handler = services.GetRequiredService<CreateStockAdjustmentHandler>();
        var command = new CreateStockAdjustmentCommand(
            StockAdjustmentMode.SetPhysicalCount,
            StockAdjustmentReason.PhysicalCountCorrection,
            [
                new StockAdjustmentItemCommand(
                    fixture.ProductId,
                    null,
                    StockAdjustmentDirection.Decrease,
                    InventoryBucket.Sellable,
                    1m, // Target 1, so 1 unit removed
                    null,
                    InventoryUnitIds: [unit1.Id])
            ],
            fixture.ActorId,
            Guid.NewGuid());

        var result = await handler.HandleAsync(command, CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error?.Message);

        await using var verifyScope = provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        var balance = await verifyDb.StockBalances.SingleAsync(x => x.ProductId == fixture.ProductId);
        Assert.Equal(1m, balance.SellableQty);

        var costState = await verifyDb.ProductCostStates.SingleAsync(x => x.ProductId == fixture.ProductId);
        Assert.Equal(1m, costState.CostedQty);
        Assert.Equal(3000m, costState.TotalInventoryCost);

        var persistedUnit1 = await verifyDb.InventoryUnits.SingleAsync(x => x.Id == unit1.Id);
        Assert.Equal(InventoryUnitStatus.Missing, persistedUnit1.Status);

        var persistedUnit2 = await verifyDb.InventoryUnits.SingleAsync(x => x.Id == unit2.Id);
        Assert.Equal(InventoryUnitStatus.InStock, persistedUnit2.Status);

        output.WriteLine("F01 Serialized SetPhysicalCount verified on real PostgreSQL: unit1 transitioned to Missing, unit2 remains InStock.");
    }

    [Fact]
    public async Task D_ADJ_1_ContainerAdjustment_WithoutProductUnit_FailsValidation()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var suffix = Guid.NewGuid().ToString("N").ToUpperInvariant();
        var unit = new Unit
        {
            Name = "Unit-" + suffix,
            Symbol = "U-" + suffix[..6],
            DisplayDecimalPlaces = 0
        };
        db.Units.Add(unit);
        var actorId = await IntegrationIdentitySeeder.CreateActorAsync(db);

        var product = new Product
        {
            Name = "Container Pack Real DB",
            Sku = "CONT-DB-" + suffix[..8],
            TrackingMode = TrackingMode.Container,
            BaseUnitId = unit.Id,
            DefaultSalePrice = 500m,
            IsActive = true
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();

        var handler = scope.ServiceProvider.GetRequiredService<CreateStockAdjustmentHandler>();
        var command = new CreateStockAdjustmentCommand(
            StockAdjustmentMode.Delta,
            StockAdjustmentReason.PhysicalCountCorrection,
            [
                new StockAdjustmentItemCommand(
                    product.Id,
                    null, // Null ProductUnitId for Container tracking mode
                    StockAdjustmentDirection.Increase,
                    InventoryBucket.Sellable,
                    1m,
                    100m)
            ],
            actorId,
            Guid.NewGuid());

        var result = await handler.HandleAsync(command, CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal("catalog.product_unit_required", result.Error?.Code);

        output.WriteLine("D-ADJ-1 Container missing product unit validation verified on real PostgreSQL.");
    }

    [Fact]
    public async Task F02_ConditionTransfer_ToScrap_RemovesExactCarryingValue_AndRecordsLoss()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);

        var fixture = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db, defaultSalePrice: 10000m);

        // Receive 1 serialized unit @ 7500m cost via purchase
        var purchaseHandler = services.GetRequiredService<CreatePurchaseHandler>();
        var purchase = await purchaseHandler.HandleAsync(new CreatePurchaseCommand(
            fixture.SupplierId,
            "PUR-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            DateOnly.FromDateTime(DateTime.UtcNow),
            null,
            0m,
            PurchaseSettlementMode.External,
            fixture.ActorId,
            Guid.NewGuid(),
            [new CreatePurchaseLineInput(fixture.ProductId, fixture.ProductUnitId, 1m, 7500m, 10000m,
                [new SerializedIdentityInput("SN-DMG-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant())])],
            ReceiveStockImmediately: true), default);
        Assert.True(purchase.IsSuccess, purchase.Error?.Message);

        var unit = await db.InventoryUnits.SingleAsync(x => x.ProductId == fixture.ProductId);

        // Step 1: Mark Damaged (Sellable -> Damaged)
        await using (var step1Scope = provider.CreateAsyncScope())
        {
            var handler1 = step1Scope.ServiceProvider.GetRequiredService<TransferInventoryConditionHandler>();
            var toDamaged = await handler1.HandleAsync(new TransferInventoryConditionCommand(
                fixture.ProductId,
                InventoryBucket.Sellable,
                InventoryBucket.Damaged,
                1m,
                fixture.ActorId,
                "Damaged in transit",
                InventoryUnitIds: [unit.Id]), CancellationToken.None);
            Assert.True(toDamaged.IsSuccess, toDamaged.Error?.Message);
        }

        // Step 2: Write off to Scrap (Damaged -> Scrap)
        await using (var step2Scope = provider.CreateAsyncScope())
        {
            var handler2 = step2Scope.ServiceProvider.GetRequiredService<TransferInventoryConditionHandler>();
            var toScrap = await handler2.HandleAsync(new TransferInventoryConditionCommand(
                fixture.ProductId,
                InventoryBucket.Damaged,
                InventoryBucket.Scrap,
                1m,
                fixture.ActorId,
                "Real PostgreSQL scrap writeoff",
                InventoryUnitIds: [unit.Id]), CancellationToken.None);
            Assert.True(toScrap.IsSuccess, toScrap.Error?.Message);
        }

        await using var verifyScope = provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        var persistedUnit = await verifyDb.InventoryUnits.SingleAsync(x => x.Id == unit.Id);
        Assert.Equal(InventoryUnitStatus.Scrapped, persistedUnit.Status);

        var costState = await verifyDb.ProductCostStates.SingleAsync(x => x.ProductId == fixture.ProductId);
        Assert.Equal(0m, costState.TotalInventoryCost);
        Assert.Equal(0m, costState.CostedQty);

        var movement = await verifyDb.InventoryMovements.SingleAsync(x => x.ProductId == fixture.ProductId && x.MovementType == InventoryMovementType.WriteOffToScrap);
        Assert.Equal(7500m, movement.RecognizedLossAmount);

        output.WriteLine("F02 Condition transfer to Scrap verified on real PostgreSQL: exact acquisition cost derecognized and loss recorded.");
    }

    [Fact]
    public async Task F03_ConditionTransfer_SerializedProduct_FractionalQuantity_Rejected()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);

        var fixture = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db, defaultSalePrice: 1000m);
        db.StockBalances.Add(new StockBalance
        {
            ProductId = fixture.ProductId,
            SellableQty = 10m
        });
        await db.SaveChangesAsync();

        var handler = services.GetRequiredService<TransferInventoryConditionHandler>();

        var result = await handler.HandleAsync(new TransferInventoryConditionCommand(
            fixture.ProductId,
            InventoryBucket.Sellable,
            InventoryBucket.Damaged,
            1.5m, // Fractional quantity forbidden for serialized
            fixture.ActorId,
            "Fractional damage attempt",
            InventoryUnitIds: [Guid.NewGuid()]), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("inventory.serialized_quantity_whole", result.Error?.Code);
        output.WriteLine("F03 Fractional serialized transfer rejection verified on real PostgreSQL.");
    }

    [Fact]
    public async Task F04_Thaka_IssueMaterial_ScalesChargeAndCostByBaseQuantity()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);

        var fixture = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db, defaultSalePrice: 200m);
        var customer = await Phase2PostgresTestHarness.SeedCustomerAsync(db);

        // Add distinct Unit for pack to satisfy unique constraint ix_product_units_product_id_unit_id
        var packUnitDef = new Unit
        {
            Name = "Pack-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            Symbol = "PK-" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant(),
            DisplayDecimalPlaces = 0
        };
        db.Units.Add(packUnitDef);
        await db.SaveChangesAsync();

        // Add ProductUnit with pack factor = 5
        var packUnit = new ProductUnit
        {
            ProductId = fixture.ProductId,
            UnitId = packUnitDef.Id,
            FactorToBaseUnit = 5m,
            CanUseInThaka = true,
            IsActive = true
        };
        db.ProductUnits.Add(packUnit);
        await db.SaveChangesAsync();

        // Seed 50 base units @ 40m cost via purchase
        var purchaseHandler = services.GetRequiredService<CreatePurchaseHandler>();
        var purchase = await purchaseHandler.HandleAsync(new CreatePurchaseCommand(
            fixture.SupplierId,
            "PUR-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            DateOnly.FromDateTime(DateTime.UtcNow),
            null,
            0m,
            PurchaseSettlementMode.External,
            fixture.ActorId,
            Guid.NewGuid(),
            [new CreatePurchaseLineInput(fixture.ProductId, fixture.ProductUnitId, 50m, 40m, 200m, [])],
            ReceiveStockImmediately: true), default);
        Assert.True(purchase.IsSuccess, purchase.Error?.Message);

        var project = new ThakaProject
        {
            ProjectNumber = "PRJ-PG-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            CustomerId = customer.Id,
            ProjectName = "Real Postgres Thaka Substation",
            Status = ThakaProjectStatus.Active,
            CreatedBy = fixture.ActorId,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.ThakaProjects.Add(project);
        await db.SaveChangesAsync();

        var issueHandler = services.GetRequiredService<IssueThakaMaterialHandler>();
        var operationId = Guid.NewGuid();
        // Issue 2 packs (= 10 base units) @ authoritative charge 200/base unit
        var issueCommand = new IssueThakaMaterialCommand(
            operationId,
            project.Id,
            fixture.ActorId,
            "Real PostgreSQL Issue",
            [
                new IssueThakaMaterialLineInput(fixture.ProductId, packUnit.Id, 2m, 200m, [])
            ]);

        var issueResult = await issueHandler.HandleAsync(issueCommand, CancellationToken.None);
        Assert.True(issueResult.IsSuccess, issueResult.Error?.Message);

        // Charge = 10 base units * 200 = 2000
        Assert.Equal(2000m, issueResult.Value!.TotalCharge);
        // Cost = 10 base units * 40 = 400
        Assert.Equal(400m, issueResult.Value!.TotalCost);

        await using var verifyScope = provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        var balance = await verifyDb.StockBalances.SingleAsync(x => x.ProductId == fixture.ProductId);
        Assert.Equal(40m, balance.SellableQty);

        var costState = await verifyDb.ProductCostStates.SingleAsync(x => x.ProductId == fixture.ProductId);
        Assert.Equal(40m, costState.CostedQty);
        Assert.Equal(1600m, costState.TotalInventoryCost);

        // Reverse the issue
        var reversalHandler = verifyScope.ServiceProvider.GetRequiredService<ReverseThakaMaterialHandler>();
        var reversalCommand = new ReverseThakaMaterialCommand(
            Guid.NewGuid(),
            project.Id,
            issueResult.Value.MaterialIssueId,
            "Material returned from site",
            fixture.ActorId);

        var reversalResult = await reversalHandler.HandleAsync(reversalCommand, CancellationToken.None);
        Assert.True(reversalResult.IsSuccess, reversalResult.Error?.Message);

        await using var postRevScope = provider.CreateAsyncScope();
        var postRevDb = postRevScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        var restoredBalance = await postRevDb.StockBalances.SingleAsync(x => x.ProductId == fixture.ProductId);
        Assert.Equal(50m, restoredBalance.SellableQty);

        var restoredCost = await postRevDb.ProductCostStates.SingleAsync(x => x.ProductId == fixture.ProductId);
        Assert.Equal(50m, restoredCost.CostedQty);
        Assert.Equal(2000m, restoredCost.TotalInventoryCost);

        output.WriteLine("F04 Thaka issue and reversal symmetry verified on real PostgreSQL: base quantity scaling and exact reversal balance.");
    }

    [Fact]
    public async Task P7_N02_Thaka_IssueMaterial_DuplicatePhysicalUnitsAcrossLines_Fails()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var fixtureA = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db, defaultSalePrice: 1000m);
        var fixtureB = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db, defaultSalePrice: 1000m);
        var customer = await Phase2PostgresTestHarness.SeedCustomerAsync(db);

        var project = new ThakaProject
        {
            ProjectNumber = "PRJ-PG-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            CustomerId = customer.Id,
            ProjectName = "Duplicate Test Project",
            Status = ThakaProjectStatus.Active,
            CreatedBy = fixtureA.ActorId,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.ThakaProjects.Add(project);
        await db.SaveChangesAsync();

        var sharedUnitId = Guid.NewGuid();
        var issueHandler = scope.ServiceProvider.GetRequiredService<IssueThakaMaterialHandler>();
        var command = new IssueThakaMaterialCommand(
            Guid.NewGuid(),
            project.Id,
            fixtureA.ActorId,
            "Duplicate line check",
            [
                new IssueThakaMaterialLineInput(fixtureA.ProductId, fixtureA.ProductUnitId, 1m, 1000m, [sharedUnitId]),
                new IssueThakaMaterialLineInput(fixtureB.ProductId, fixtureB.ProductUnitId, 1m, 1000m, [sharedUnitId])
            ]);

        var result = await issueHandler.HandleAsync(command, CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal("thaka.serial_selection_invalid", result.Error?.Code);

        await using var verifyScope = provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Empty(await verifyDb.ThakaMaterialIssues.Where(x => x.ProjectId == project.Id).ToListAsync());

        output.WriteLine("P7-N02 Cross-line duplicate physical unit validation verified on real PostgreSQL: transaction safely rolled back.");
    }
}
