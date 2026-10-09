using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Catalog;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace EdgeRetails.IntegrationTests;

// NEW_COVERAGE. The existing owned runner attests isolated PostgreSQL; no operational DB.
[Collection("Phase2PostgresIntegration")]
public sealed class Phase7Pass4PostgresTests(ITestOutputHelper output)
{
    // NEW_COVERAGE: capture the current incomplete writer's original deferred refusal.
    // After integration this remains an explicit older-writer omission attack,
    // not positive evidence for the current return writer.
    [Fact]
    public async Task D13_DeferredBulkReturnOldWriterRefusalCapturesOriginalCommitErrorAndRollback()
    {
        await using var f = await Fixture.CreateAsync(TrackingMode.Quantity, 3m, 3m, rawCost: 30m);
        Assert.True((await f.ReceiveAsync(1m)).IsSuccess);
        await using var scope = f.Provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        var price = (await db.Products.SingleAsync(x => x.Id == f.ProductId)).DefaultSalePrice * 3m;
        var sale = await services.GetRequiredService<CompleteSaleHandler>().HandleAsync(new CompleteSaleCommand(
            Guid.NewGuid(), null, f.ActorId, null, 0m, SalePaymentMethod.Bank, price, "D13 deferred proof", null,
            [new(f.ProductId, f.ProductUnitId, 1m, price, [])]), default);
        Assert.True(sale.IsSuccess, sale.Error?.Message);
        db.ChangeTracker.Clear();
        var sold = await db.SaleItems.SingleAsync(x => x.SaleId == sale.Value!.SaleId);
        var operationId = Guid.NewGuid();
        var before = await ReturnProofSnapshotAsync(db, f.ProductId);
        var oldWriter = new OmitReturnSourceFactsUnitOfWork(db);
        // HARNESS_CORRECTION: canonical collaborators may flush before the injected unit of work.
        // Omit the new fact at tracking time so every flush exercises the older-writer attack.
        db.ChangeTracker.Tracked += (_, e) =>
        {
            if (e.Entry.Entity is EdgeRetails.Domain.Warranty.SaleReturnSourceAllocation && e.Entry.State == EntityState.Added)
            {
                e.Entry.State = EntityState.Detached;
                oldWriter.RecordOmission();
            }
        };
        var handler = ActivatorUtilities.CreateInstance<CreateSaleReturnHandler>(services, oldWriter);
        Guid returnId;
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            // EfTransactionRunner sees this existing transaction and cannot mask caller-owned COMMIT.
            var result = await handler.HandleAsync(new CreateSaleReturnCommand(sale.Value!.SaleId, "DEFECTIVE",
                "original deferred return refusal", RefundMethod.Bank, f.ActorId, operationId,
                [new(sold.Id, 3m, SaleReturnDisposition.RestockSellable, [])]), default);
            Assert.True(result.IsSuccess, result.Error?.Message);
            returnId = result.Value!.SaleReturnId;
            var returnItem = await db.SaleReturnItems.AsNoTracking().SingleAsync(x => x.SaleReturnId == returnId);
            Assert.Equal(3m, returnItem.BaseQuantity);
            Assert.False(await db.SaleReturnSourceAllocations.AsNoTracking().AnyAsync(x => x.SaleReturnItemId == returnItem.Id));
            Assert.NotEqual(before, await ReturnProofSnapshotAsync(db, f.ProductId));
            var refusal = await Assert.ThrowsAsync<PostgresException>(() => transaction.CommitAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, refusal.SqlState);
            Assert.Equal("PASS5_PROVENANCE_REQUIRED: ambiguous or missing bulk sold-source allocation", refusal.MessageText);
            Assert.Contains("assert_sold_item_source", refusal.Where, StringComparison.Ordinal);
            output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                Evidence = "D13_ORIGINAL_DEFERRED_RETURN_REFUSAL", Provider = "PostgreSQL 18 / Npgsql",
                refusal.SqlState, refusal.MessageText, refusal.Detail, refusal.SchemaName, refusal.TableName,
                refusal.ConstraintName, refusal.Where, TransactionPoint = "caller-owned deferred COMMIT",
                TriggerMapping = "sales.return_items / ct_return_items_source_authority (source mapping; server fields may be null)",
                ClientOperationId = operationId, ActorId = f.ActorId, SaleId = sale.Value.SaleId,
                SaleItemId = sold.Id, SaleReturnId = returnId, SaleReturnItemId = returnItem.Id,
                OriginalSaleMovementId = sold.InventoryMovementId,
                MissingSourceFacts = true, OmittedNewSourceFacts = oldWriter.OmittedFacts,
                ProvisionalReturnQuantity = returnItem.BaseQuantity
            }));
        }
        await using var read = f.Provider.CreateAsyncScope();
        var fresh = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Equal(before, await ReturnProofSnapshotAsync(fresh, f.ProductId));
        Assert.False(await fresh.SaleReturns.AnyAsync(x => x.Id == returnId));
        Assert.False(await fresh.OperationOutcomes.AnyAsync(x => x.ClientOperationId == operationId));
        output.WriteLine("D13_DEFERRED_RETURN_ROLLBACK_VERIFIED: return/items/lots/effects/stock/cost/audit/outcome/outbox unchanged");
    }

    private static async Task<string> ReturnProofSnapshotAsync(EdgeRetailsDbContext db, Guid productId)
    {
        db.ChangeTracker.Clear();
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            Returns = await db.SaleReturns.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            Items = await db.SaleReturnItems.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            Allocations = await db.SaleReturnSourceAllocations.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            Lots = await db.InventoryLots.AsNoTracking().Where(x => x.ProductId == productId).OrderBy(x => x.Id).ToArrayAsync(),
            Balances = await db.InventoryLotBucketBalances.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            Movements = await db.InventoryMovements.AsNoTracking().Where(x => x.ProductId == productId).OrderBy(x => x.Id).ToArrayAsync(),
            Effects = await db.InventoryMovementEffects.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            Stock = await db.StockBalances.AsNoTracking().Where(x => x.ProductId == productId).OrderBy(x => x.Id).ToArrayAsync(),
            Cost = await db.ProductCostStates.AsNoTracking().Where(x => x.ProductId == productId).OrderBy(x => x.Id).ToArrayAsync(),
            Audit = await db.BusinessAuditEvents.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            Outcomes = await db.OperationOutcomes.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            Outbox = await db.OutboxMessages.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync()
        });
    }

    private sealed class OmitReturnSourceFactsUnitOfWork(EdgeRetailsDbContext db) : IUnitOfWork
    {
        public int OmittedFacts { get; private set; }
        public void RecordOmission() => OmittedFacts++;
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            foreach (var entry in db.ChangeTracker.Entries<EdgeRetails.Domain.Warranty.SaleReturnSourceAllocation>()
                .Where(x => x.State == EntityState.Added).ToArray())
            {
                entry.State = EntityState.Detached;
                OmittedFacts++;
            }
            return db.SaveChangesAsync(cancellationToken);
        }
    }

    [Theory]
    [InlineData(TrackingMode.Container, SaleReturnDisposition.RestockSellable)]
    [InlineData(TrackingMode.Container, SaleReturnDisposition.Scrap)]
    [InlineData(TrackingMode.Quantity, SaleReturnDisposition.RestockSellable)]
    [InlineData(TrackingMode.Quantity, SaleReturnDisposition.Scrap)]
    public async Task H2_ReturnedLotsDoNotBecomePurchaseReceiptsOrAcquiredCost(
        TrackingMode mode, SaleReturnDisposition disposition)
    {
        await using var f = await Fixture.CreateAsync(mode, 3m, 3m, rawCost: 30m);
        Assert.True((await f.ReceiveAsync(1m)).IsSuccess);
        await using var scope = f.Provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        var units = await db.InventoryUnits.Where(x => x.ProductId == f.ProductId).ToListAsync();
        var ids = units.Select(x => x.Id).ToArray();
        var originalIdentity = units.Select(x => (x.Id, x.TrackingCode, x.ItemSequence, x.AcquisitionCost)).ToArray();
        var price = (await db.Products.SingleAsync(x => x.Id == f.ProductId)).DefaultSalePrice * 3m;
        var sale = await services.GetRequiredService<CompleteSaleHandler>().HandleAsync(new CompleteSaleCommand(
            Guid.NewGuid(), null, f.ActorId, null, 0m, SalePaymentMethod.Bank, price, "H2", null,
            [new(f.ProductId, f.ProductUnitId, 1m, price, ids)]), default);
        Assert.True(sale.IsSuccess, sale.Error?.Message);
        db.ChangeTracker.Clear();
        var sold = await db.SaleItems.SingleAsync(x => x.SaleId == sale.Value!.SaleId);
        Assert.Equal(30m, sold.TotalCostSnapshot);
        var returned = await services.GetRequiredService<CreateSaleReturnHandler>().HandleAsync(new CreateSaleReturnCommand(
            sale.Value!.SaleId, "DEFECTIVE", "H2 returned receipt provenance", RefundMethod.Bank, f.ActorId,
            Guid.NewGuid(), [new(sold.Id, 3m, disposition, ids)]), default);
        Assert.True(returned.IsSuccess, returned.Error?.Message);
        db.ChangeTracker.Clear();
        var inventory = services.GetRequiredService<IInventoryRepository>();
        var receivedBefore = await inventory.GetPurchaseItemReceivedBaseQuantityAsync(f.ItemId, default);
        var carryingBefore = await inventory.GetPurchaseItemReceivedCarryingValueAsync(f.ItemId, mode == TrackingMode.Container, default);
        var returnLot = await (from lot in db.InventoryLots
                               join movement in db.InventoryMovements on lot.SourceMovementId equals movement.Id
                               where lot.PurchaseItemId == f.ItemId && movement.MovementType == InventoryMovementType.SaleReturn
                               select lot).SingleAsync();
        Assert.Equal(3m, returnLot.ReceivedQuantity);
        Assert.Equal(disposition == SaleReturnDisposition.Scrap ? 0m : 10m, returnLot.EffectiveUnitCost);

        var remainingCommand = f.Intake(2m);
        var remaining = await f.ReceiveAsync(remainingCommand);
        Assert.True(remaining.IsSuccess, remaining.Error?.Code + ": " + remaining.Error?.Message);
        Assert.Equal(3m, receivedBefore);
        Assert.Equal(30m, carryingBefore);
        var replay = await f.ReceiveAsync(remainingCommand);
        Assert.True(replay.IsSuccess, replay.Error?.Message);
        Assert.True(replay.Value!.WasExisting);
        db.ChangeTracker.Clear();
        var item = await db.PurchaseItems.SingleAsync(x => x.Id == f.ItemId);
        Assert.Equal(3m, item.EnteredQuantity);
        Assert.Equal(3m, item.FactorToBaseSnapshot);
        Assert.Equal(9m, item.BaseQuantity);
        Assert.Equal(90m, item.EffectiveLineCost);
        Assert.Equal(9m, await inventory.GetPurchaseItemReceivedBaseQuantityAsync(f.ItemId, default));
        Assert.Equal(90m, await inventory.GetPurchaseItemReceivedCarryingValueAsync(f.ItemId, mode == TrackingMode.Container, default));
        Assert.Equal(2, await db.InventoryMovements.CountAsync(x => x.ProductId == f.ProductId && x.MovementType == InventoryMovementType.PurchaseIn));
        Assert.Equal(1, await db.InventoryMovements.CountAsync(x => x.ProductId == f.ProductId && x.MovementType == InventoryMovementType.SaleReturn));
        Assert.Equal(3, await db.InventoryLots.CountAsync(x => x.PurchaseItemId == f.ItemId));
        var stock = await db.StockBalances.SingleAsync(x => x.ProductId == f.ProductId);
        Assert.Equal(disposition == SaleReturnDisposition.Scrap ? 6m : 9m, stock.SellableQty);
        Assert.Equal(disposition == SaleReturnDisposition.Scrap ? 3m : 0m, stock.ScrapQty);
        var cost = await db.ProductCostStates.SingleAsync(x => x.ProductId == f.ProductId);
        Assert.Equal(disposition == SaleReturnDisposition.Scrap ? 60m : 90m, cost.TotalInventoryCost);
        Assert.Equal(disposition == SaleReturnDisposition.Scrap ? 6m : 9m, cost.CostedQty);
        var persisted = await db.InventoryUnits.Where(x => x.ProductId == f.ProductId).ToListAsync();
        if (mode == TrackingMode.Container)
        {
            Assert.Equal(3, persisted.Count);
            Assert.Equal(90m, persisted.Sum(x => x.AcquisitionCost));
            Assert.Equal(originalIdentity, persisted.Where(x => ids.Contains(x.Id)).Select(x => (x.Id, x.TrackingCode, x.ItemSequence, x.AcquisitionCost)).ToArray());
            Assert.Equal(disposition == SaleReturnDisposition.Scrap ? InventoryUnitStatus.Scrapped : InventoryUnitStatus.InStock,
                persisted.Single(x => x.Id == ids[0]).Status);
        }
        else
        {
            Assert.Empty(persisted);
        }
    }

    [Theory]
    [InlineData(true, 1, false, false)]
    [InlineData(false, 1, false, false)]
    [InlineData(false, 3, false, false)]
    [InlineData(true, 1, true, false)]
    [InlineData(false, 1, true, false)]
    [InlineData(true, 1, false, true)]
    [InlineData(false, 3, false, true)]
    public async Task H1_ContainerResidualPreservesWholePackTransitionsAndRejectsCorruption(
        bool immediate, int count, bool corrupt, bool corruptAfterReturn)
    {
        await using var f = await Fixture.CreateAsync(TrackingMode.Container, 90m, count,
            rawCost: 100m, immediate: immediate);
        if (!immediate)
        {
            Assert.True((await f.ReceiveAsync(1m)).IsSuccess);
            if (count > 1)
            {
                Assert.True((await f.ReceiveAsync(count - 1m)).IsSuccess);
            }
        }

        await using var scope = f.Provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        var units = await db.InventoryUnits.Where(x => x.ProductId == f.ProductId)
            .OrderBy(x => x.ItemSequence).ToListAsync();
        Assert.Equal(count, units.Count);
        Assert.Equal(100m * count, units.Sum(x => x.AcquisitionCost));
        Assert.All(await db.InventoryLots.Where(x => x.ProductId == f.ProductId).ToListAsync(),
            lot => Assert.Equal(1.111111m, lot.EffectiveUnitCost));
        Assert.Equal(100m * count, (await db.ProductCostStates.SingleAsync(x => x.ProductId == f.ProductId)).TotalInventoryCost);
        var identity = units.Select(x => (x.Id, x.TrackingCode, x.ItemSequence, x.AcquisitionCost)).ToArray();
        var inventory = services.GetRequiredService<IInventoryRepository>();
        if (corrupt)
        {
            units[0].AcquisitionCost += 0.01m;
            await db.SaveChangesAsync();
            var error = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                inventory.GetPhysicalUnitBaseQuantitySnapshotAsync(units[0], default));
            Assert.Equal("inventory.physical_cost_reconciliation_required", error.Code);
            Assert.All(units, unit => Assert.Equal(InventoryUnitStatus.InStock, unit.Status));
            Assert.Equal(90m * count, (await db.StockBalances.SingleAsync(x => x.ProductId == f.ProductId)).SellableQty);
            Assert.False(await db.SaleItems.AnyAsync(x => x.ProductId == f.ProductId));
            return;
        }

        foreach (var unit in units)
        {
            Assert.Equal(90m, await inventory.GetPhysicalUnitBaseQuantitySnapshotAsync(unit, default));
        }
        var unitIds = units.Select(x => x.Id).ToArray();
        var price = (await db.Products.SingleAsync(x => x.Id == f.ProductId)).DefaultSalePrice * 90m;
        var sale = await services.GetRequiredService<CompleteSaleHandler>().HandleAsync(new CompleteSaleCommand(
            Guid.NewGuid(), null, f.ActorId, null, 0m, SalePaymentMethod.Bank, price * count, "H1", null,
            [new(f.ProductId, f.ProductUnitId, count, price, unitIds)]), default);
        Assert.True(sale.IsSuccess, sale.Error?.Message);
        db.ChangeTracker.Clear();
        var soldItem = await db.SaleItems.SingleAsync(x => x.SaleId == sale.Value!.SaleId);
        Assert.Equal(100m * count, soldItem.TotalCostSnapshot);
        Assert.Equal(0m, (await db.StockBalances.SingleAsync(x => x.ProductId == f.ProductId)).SellableQty);
        Assert.Equal(0m, (await db.ProductCostStates.SingleAsync(x => x.ProductId == f.ProductId)).TotalInventoryCost);
        var returned = await services.GetRequiredService<CreateSaleReturnHandler>().HandleAsync(new CreateSaleReturnCommand(
            sale.Value!.SaleId, "DEFECTIVE", "H1 intact pack", RefundMethod.Bank, f.ActorId, Guid.NewGuid(),
            [new(soldItem.Id, 90m * count, SaleReturnDisposition.RestockSellable, unitIds)]), default);
        Assert.True(returned.IsSuccess, returned.Error?.Message);
        db.ChangeTracker.Clear();
        Assert.Equal(100m * count, (await db.ProductCostStates.SingleAsync(x => x.ProductId == f.ProductId)).TotalInventoryCost);
        var returnedUnits = await db.InventoryUnits.Where(x => x.ProductId == f.ProductId).ToListAsync();
        if (corruptAfterReturn)
        {
            returnedUnits[0].AcquisitionCost += 0.01m;
            await db.SaveChangesAsync();
            var error = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                inventory.GetPhysicalUnitBaseQuantitySnapshotAsync(returnedUnits[0], default));
            Assert.Equal("inventory.physical_cost_reconciliation_required", error.Code);
            Assert.All(returnedUnits, unit => Assert.Equal(InventoryUnitStatus.InStock, unit.Status));
            Assert.Equal(90m * count, (await db.StockBalances.SingleAsync(x => x.ProductId == f.ProductId)).SellableQty);
            Assert.False(await db.InventoryMovements.AnyAsync(x => x.ProductId == f.ProductId && x.MovementType == InventoryMovementType.MarkDamaged));
            return;
        }
        foreach (var unit in returnedUnits)
        {
            Assert.Equal(90m, await inventory.GetPhysicalUnitBaseQuantitySnapshotAsync(unit, default));
        }
        var transfer = await services.GetRequiredService<TransferInventoryConditionHandler>().HandleAsync(
            new TransferInventoryConditionCommand(f.ProductId, InventoryBucket.Sellable, InventoryBucket.Damaged,
                90m * count, f.ActorId, "H1 intact pack", InventoryUnitIds: unitIds), default);
        Assert.True(transfer.IsSuccess, transfer.Error?.Message);
        db.ChangeTracker.Clear();
        var stock = await db.StockBalances.SingleAsync(x => x.ProductId == f.ProductId);
        Assert.Equal(0m, stock.SellableQty);
        Assert.Equal(90m * count, stock.DamagedQty);
        Assert.Equal(100m * count, (await db.ProductCostStates.SingleAsync(x => x.ProductId == f.ProductId)).TotalInventoryCost);
        var persisted = await db.InventoryUnits.Where(x => x.ProductId == f.ProductId).OrderBy(x => x.ItemSequence).ToListAsync();
        Assert.Equal(identity, persisted.Select(x => (x.Id, x.TrackingCode, x.ItemSequence, x.AcquisitionCost)).ToArray());
        Assert.All(persisted, unit => Assert.Equal(InventoryUnitStatus.Damaged, unit.Status));
    }

    [Theory]
    [InlineData(1, 1)] [InlineData(1, 2)] [InlineData(1, 3)]
    [InlineData(2, 1)] [InlineData(2, 2)] [InlineData(2, 3)]
    public async Task G05_ContainerReceiptPersistsExactCountAndFrozenBase(int count, int factor)
    {
        await using var f = await Fixture.CreateAsync(TrackingMode.Container, factor, 2m);
        var result = await f.ReceiveAsync(count);
        Assert.True(result.IsSuccess, result.Error?.Message);
        await using var scope = f.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", db.Database.ProviderName);
        Assert.Equal(count, await db.InventoryUnits.CountAsync(x => x.ProductId == f.ProductId));
        Assert.Equal((decimal)(count * factor), (await db.StockBalances.SingleAsync(x => x.ProductId == f.ProductId)).SellableQty);
        Assert.Equal((decimal)(count * factor), (await db.InventoryLots.SingleAsync(x => x.ProductId == f.ProductId)).ReceivedQuantity);
        var item = await db.PurchaseItems.SingleAsync(x => x.Id == f.ItemId);
        Assert.Equal((decimal)factor, item.FactorToBaseSnapshot);
        Assert.Equal(2m * factor, item.BaseQuantity);
        var pair = await db.SupplierProducts.SingleAsync(x => x.ProductId == f.ProductId && x.SupplierId == f.SupplierId);
        Assert.Equal(count + 1L, pair.NextItemSequence);
        var units = await db.InventoryUnits.Where(x => x.ProductId == f.ProductId).OrderBy(x => x.ItemSequence).ToArrayAsync();
        Assert.Equal(count, units.Select(x => x.TrackingCode).Distinct().Count());
        Assert.Equal(Enumerable.Range(1, count).Select(x => (long?)x), units.Select(x => x.ItemSequence));
        Assert.All(units, x => { Assert.False(string.IsNullOrWhiteSpace(x.TrackingCode)); Assert.Equal(f.ItemId, x.SourcePurchaseItemId); });
        var ids = units.Select(x => x.Id).ToArray();
        // Optional Container manufacturer identities are absent; TrackingCode
        // physical authority is persisted on InventoryUnit, not manufacturer claims.
        Assert.Empty(await db.InventoryUnitIdentityClaims.Where(x => ids.Contains(x.InventoryUnitId)).ToArrayAsync());
    }

    [Theory]
    [InlineData("0.5", "catalog.container_quantity_whole")]
    [InlineData("1.5", "catalog.container_quantity_whole")]
    [InlineData("100001", "purchasing.physical_unit_count_out_of_range")]
    [InlineData("2147483648", "purchasing.physical_unit_count_out_of_range")]
    public async Task G05_DirectInvalidCountPersistsZeroBusinessEffects(string count, string code)
    {
        await using var f = await Fixture.CreateAsync(TrackingMode.Container, 2m, 3_000_000_000m);
        var result = await f.ReceiveAsync(decimal.Parse(count, System.Globalization.CultureInfo.InvariantCulture));
        Assert.False(result.IsSuccess);
        Assert.Equal(code, result.Error!.Code);
        await f.AssertNoInventoryAsync();
    }

    [Fact]
    public async Task G05_DirectInvalidFrozenContainerFactorPersistsZeroEffects()
    {
        await using var f = await Fixture.CreateAsync(TrackingMode.Container, 2m);
        await f.MutateItemAsync(x => { x.FactorToBaseSnapshot = 1.5m; x.BaseQuantity = x.EnteredQuantity * 1.5m; });
        var result = await f.ReceiveAsync(1m);
        Assert.False(result.IsSuccess);
        Assert.Equal("catalog.container_conversion_whole", result.Error!.Code);
        await f.AssertNoInventoryAsync();
    }

    [Fact]
    public async Task G03_FrozenTenRollsReceiveFourThenSixAndReplayConservesState()
    {
        await using var f = await Fixture.CreateAsync(TrackingMode.Length, 90m);
        await using (var scope = f.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            (await db.ProductUnits.SingleAsync(x => x.Id == f.ProductUnitId)).FactorToBaseUnit = 100m;
            await db.SaveChangesAsync();
        }
        var firstCommand = f.Intake(4m);
        var first = await f.ReceiveAsync(firstCommand);
        Assert.True(first.IsSuccess, first.Error?.Message);
        var last = await f.ReceiveAsync(6m);
        Assert.True(last.IsSuccess, last.Error?.Message);
        var replay = await f.ReceiveAsync(firstCommand);
        Assert.True(replay.IsSuccess, replay.Error?.Message);
        Assert.True(replay.Value!.WasExisting);
        Assert.Equal(360m, replay.Value.BaseQuantity);
        await using var read = f.Provider.CreateAsyncScope();
        var persisted = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var item = await persisted.PurchaseItems.SingleAsync(x => x.Id == f.ItemId);
        Assert.Equal(90m, item.FactorToBaseSnapshot); Assert.Equal(900m, item.BaseQuantity);
        Assert.Equal(900m, await persisted.InventoryLots.Where(x => x.PurchaseItemId == f.ItemId).SumAsync(x => x.ReceivedQuantity));
        Assert.Equal(900m, (await persisted.StockBalances.SingleAsync(x => x.ProductId == f.ProductId)).SellableQty);
        Assert.Equal(2, await persisted.InventoryMovements.CountAsync(x => x.ProductId == f.ProductId));
        Assert.Equal(item.EffectiveLineCost, (await persisted.ProductCostStates.SingleAsync(x => x.ProductId == f.ProductId)).TotalInventoryCost);
    }

    [Fact]
    public async Task G03_NineRollReceiptRemainsPartialAndWrongUnitCannotMutate()
    {
        await using var f = await Fixture.CreateAsync(TrackingMode.Length, 90m);
        var wrong = await f.ReceiveAsync(f.Intake(1m) with { ProductUnitId = f.BaseProductUnitId });
        Assert.Equal("purchasing.intake_unit_mismatch", wrong.Error!.Code);
        await f.AssertNoInventoryAsync();
        Assert.True((await f.ReceiveAsync(9m)).IsSuccess);
        await using var read = f.Provider.CreateAsyncScope();
        var db = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Equal(810m, await db.InventoryLots.Where(x => x.PurchaseItemId == f.ItemId).SumAsync(x => x.ReceivedQuantity));
        Assert.Equal(900m, (await db.PurchaseItems.SingleAsync(x => x.Id == f.ItemId)).BaseQuantity);
        Assert.True((await f.ReceiveAsync(1m)).IsSuccess);
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(901)]
    public async Task G03_InvalidOrderedSnapshotPersistsZeroEffects(decimal invalidBase)
    {
        await using var f = await Fixture.CreateAsync(TrackingMode.Length, 90m);
        if (invalidBase <= 0m)
        {
            var rejected = await Assert.ThrowsAsync<DbUpdateException>(() => f.MutateItemAsync(x => x.BaseQuantity = invalidBase));
            var postgres = Assert.IsType<PostgresException>(rejected.InnerException);
            Assert.Equal(PostgresErrorCodes.CheckViolation, postgres.SqlState);
            Assert.Equal("ck_purchase_item_values", postgres.ConstraintName);
            await using var read = f.Provider.CreateAsyncScope();
            Assert.Equal(900m, (await read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>()
                .PurchaseItems.SingleAsync(x => x.Id == f.ItemId)).BaseQuantity);
            await f.AssertNoInventoryAsync();
            return;
        }
        await f.MutateItemAsync(x => x.BaseQuantity = invalidBase);
        var result = await f.ReceiveAsync(4m);
        Assert.Equal("purchasing.purchase_snapshot_invalid", result.Error!.Code);
        await f.AssertNoInventoryAsync();
    }

    [Fact]
    public async Task G03_ConcurrentReceiptShowsActualLockWaitAndCannotExceedOutstanding()
    {
        await using var f = await Fixture.CreateAsync(TrackingMode.Length, 90m);
        using var gate = new FlushGate();
        var winner = Task.Run(async () =>
        {
            await using var scope = f.Provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var handler = ActivatorUtilities.CreateInstance<ReceiveProductIntakeHandler>(scope.ServiceProvider, new GateUnitOfWork(db, gate));
            return await handler.HandleAsync(f.Intake(6m));
        });
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var pid = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loser = Task.Run(async () =>
        {
            await using var scope = f.Provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            await db.Database.OpenConnectionAsync();
            pid.SetResult(((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID);
            return await scope.ServiceProvider.GetRequiredService<ReceiveProductIntakeHandler>().HandleAsync(f.Intake(6m));
        });
        try { await AssertLockWaitAsync(await pid.Task.WaitAsync(TimeSpan.FromSeconds(10))); }
        finally { gate.Release.TrySetResult(true); }
        await Task.WhenAll(winner, loser).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True((await winner).IsSuccess);
        Assert.Equal("purchasing.intake_exceeds_ordered", (await loser).Error!.Code);
        await using var read = f.Provider.CreateAsyncScope();
        var dbRead = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Equal(540m, (await dbRead.StockBalances.SingleAsync(x => x.ProductId == f.ProductId)).SellableQty);
        Assert.Equal(1, await dbRead.InventoryLots.CountAsync(x => x.PurchaseItemId == f.ItemId));
        Assert.Equal(1, await dbRead.InventoryMovements.CountAsync(x => x.ProductId == f.ProductId));
    }

    [Fact]
    public async Task G03_FailureAfterActualPhysicalSqlFlushRollsBackAllBusinessEffects()
    {
        await using var f = await Fixture.CreateAsync(TrackingMode.IndividualPiece, 1m);
        await using var scope = f.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var fail = new FlushThenFail(db);
        var handler = ActivatorUtilities.CreateInstance<ReceiveProductIntakeHandler>(scope.ServiceProvider, fail);
        var result = await Assert.ThrowsAsync<BusinessRuleException>(() => handler.HandleAsync(f.Intake(4m)));
        Assert.True(fail.Flushed);
        Assert.Equal("pass4.after_sql_flush", result.Code);
        await f.AssertNoInventoryAsync();
        await using var read = f.Provider.CreateAsyncScope();
        var persisted = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Equal(10m, (await persisted.PurchaseItems.SingleAsync(x => x.Id == f.ItemId)).BaseQuantity);
        Assert.Empty(await persisted.PurchaseItemUnits.Where(x => x.PurchaseItemId == f.ItemId).ToListAsync());
    }

    [Fact]
    public async Task G04_LandedCostPersists11000AcrossMovementLotUnitsAndPoolAndRejectsOverride()
    {
        await using var f = await Fixture.CreateAsync(TrackingMode.IndividualPiece, 1m, charges: 1000m);
        var forged = await f.ReceiveAsync(f.Intake(10m) with { EnteredUnitCost = 1000.00001m });
        Assert.Equal("purchasing.receipt_cost_override_not_allowed", forged.Error!.Code);
        await f.AssertNoInventoryAsync();
        var command = f.Intake(10m);
        Assert.True((await f.ReceiveAsync(command)).IsSuccess);
        Assert.True((await f.ReceiveAsync(command)).Value!.WasExisting);
        await using var read = f.Provider.CreateAsyncScope();
        var db = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Equal(1100m, (await db.InventoryMovements.SingleAsync(x => x.ProductId == f.ProductId)).UnitCostSnapshot);
        var lot = await db.InventoryLots.SingleAsync(x => x.ProductId == f.ProductId);
        Assert.Equal(1100m, lot.OriginalUnitCost); Assert.Equal(1100m, lot.EffectiveUnitCost);
        Assert.Equal(11000m, await db.InventoryUnits.Where(x => x.ProductId == f.ProductId).SumAsync(x => x.AcquisitionCost));
        Assert.Equal(10, await db.InventoryUnits.CountAsync(x => x.ProductId == f.ProductId));
        var state = await db.ProductCostStates.SingleAsync(x => x.ProductId == f.ProductId);
        Assert.Equal(11000m, state.TotalInventoryCost); Assert.Equal(1100m, state.LastPurchaseCost);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task G04_LineDivisionResidualConservedForPartialAndImmediateReceipt(bool immediate)
    {
        await using var f = await Fixture.CreateAsync(TrackingMode.IndividualPiece, 1m, 3m, 1m, 1m, immediate);
        if (!immediate)
        {
            Assert.True((await f.ReceiveAsync(1m)).IsSuccess);
            Assert.True((await f.ReceiveAsync(2m)).IsSuccess);
        }
        await using var read = f.Provider.CreateAsyncScope();
        var db = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var item = await db.PurchaseItems.SingleAsync(x => x.Id == f.ItemId);
        Assert.Equal(4m, item.EffectiveLineCost); Assert.Equal(1.333333m, item.EffectiveBaseUnitCost);
        Assert.Equal(4m, (await db.ProductCostStates.SingleAsync(x => x.ProductId == f.ProductId)).TotalInventoryCost);
        Assert.Equal(4m, await db.InventoryUnits.Where(x => x.ProductId == f.ProductId).SumAsync(x => x.AcquisitionCost));
        var lots = await db.InventoryLots.Where(x => x.ProductId == f.ProductId).ToArrayAsync();
        Assert.All(lots, x => Assert.Equal(1.333333m, x.OriginalUnitCost));
        // Section140 fixes unit/moving cost at numeric18,6. Explicitly assert its
        // represented value separately from the exact landed pool/physical total.
        if (immediate)
        {
            Assert.Equal(1.333333m, Assert.Single(lots).EffectiveUnitCost);
            Assert.Equal(3.999999m, lots.Sum(x => x.ReceivedQuantity * x.EffectiveUnitCost));
        }
        else
        {
            Assert.Equal(1.333333m, Assert.Single(lots, x => x.ReceivedQuantity == 1m).EffectiveUnitCost);
            Assert.Equal(1.333334m, Assert.Single(lots, x => x.ReceivedQuantity == 2m).EffectiveUnitCost);
            Assert.Equal(4.000001m, lots.Sum(x => x.ReceivedQuantity * x.EffectiveUnitCost));
        }
        Assert.All(await db.InventoryMovements.Where(x => x.ProductId == f.ProductId).ToArrayAsync(),
            x => Assert.Equal(1.333333m, x.UnitCostSnapshot));
    }

    [Theory]
    [InlineData(TrackingMode.Quantity)] [InlineData(TrackingMode.IndividualPiece)]
    public async Task G04_ExistingPartialOrderRecoversPersistedPriorCostAfterBucketExhaustionAndReplay(TrackingMode mode)
    {
        await using var f = await Fixture.CreateAsync(mode, 1m, 3m, 1m, 1m);
        Assert.True((await f.ReceiveAsync(1m)).IsSuccess);
        Guid priorLotId;
        await using (var seed = f.Provider.CreateAsyncScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var lot = await db.InventoryLots.SingleAsync(x => x.PurchaseItemId == f.ItemId);
            priorLotId = lot.Id;
            // Controlled historical fixture emulates the earlier rounded per-base policy.
            lot.EffectiveUnitCost = 1.3333m;
            foreach (var unit in await db.InventoryUnits.Where(x => x.SourcePurchaseItemId == f.ItemId).ToArrayAsync())
            {
                unit.AcquisitionCost = 1.3333m;
            }
            var state = await db.ProductCostStates.SingleAsync(x => x.ProductId == f.ProductId);
            state.TotalInventoryCost = 1.3333m; state.MovingAverageCost = 1.3333m;
            // Transfer prior allocation out of Sellable: provenance must remain authority.
            var balance = await db.InventoryLotBucketBalances.SingleAsync(x => x.LotId == lot.Id);
            balance.Quantity = 0m;
            db.InventoryLotBucketBalances.Add(new EdgeRetails.Domain.Inventory.InventoryLotBucketBalance
            { LotId = lot.Id, StockBucket = EdgeRetails.Domain.Inventory.InventoryBucket.Damaged, Quantity = 1m });
            var stock = await db.StockBalances.SingleAsync(x => x.ProductId == f.ProductId);
            stock.SellableQty = 0m; stock.DamagedQty = 1m;
            await db.SaveChangesAsync();
        }
        var finalCommand = f.Intake(2m);
        Assert.True((await f.ReceiveAsync(finalCommand)).IsSuccess);
        Assert.True((await f.ReceiveAsync(finalCommand)).Value!.WasExisting);
        await using var read = f.Provider.CreateAsyncScope();
        var persisted = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Equal(4m, (await persisted.ProductCostStates.SingleAsync(x => x.ProductId == f.ProductId)).TotalInventoryCost);
        var lots = await persisted.InventoryLots.Where(x => x.PurchaseItemId == f.ItemId).ToArrayAsync();
        Assert.Equal(2, lots.Length);
        Assert.Equal(1.3333m, Assert.Single(lots, x => x.Id == priorLotId).EffectiveUnitCost);
        Assert.Equal(1.33335m, Assert.Single(lots, x => x.Id != priorLotId).EffectiveUnitCost);
        Assert.Equal(4m, lots.Sum(x => x.ReceivedQuantity * x.EffectiveUnitCost));
        Assert.All(lots, x => Assert.Equal(1.333333m, x.OriginalUnitCost));
        Assert.Equal(2, await persisted.InventoryMovements.CountAsync(x => x.ProductId == f.ProductId));
        if (mode == TrackingMode.IndividualPiece)
        {
            Assert.Equal(4m, await persisted.InventoryUnits.Where(x => x.SourcePurchaseItemId == f.ItemId).SumAsync(x => x.AcquisitionCost));
        }
    }

    [Fact]
    public async Task G02_UnreceivedOrderLocksFactorDespiteNoInventoryHistory()
    {
        await using var f = await Fixture.CreateAsync(TrackingMode.Length, 90m);
        await f.AssertNoInventoryAsync();
        await using var scope = f.Provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ConfigureProductUnitsHandler>().HandleAsync(f.Configure(100m), default);
        Assert.Equal("catalog.product_unit_factor_locked", result.Error!.Code);
        await using var read = f.Provider.CreateAsyncScope();
        var db = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Equal(90m, (await db.ProductUnits.SingleAsync(x => x.Id == f.ProductUnitId)).FactorToBaseUnit);
        Assert.Equal(90m, (await db.PurchaseItems.SingleAsync(x => x.Id == f.ItemId)).FactorToBaseSnapshot);
    }

    [Fact]
    public async Task G04_ConsumedLegacyLotStillFundsRemainingReceiptAndConservesRecognizedPlusCarryingCost()
    {
        await using var f = await Fixture.CreateAsync(TrackingMode.Quantity, 1m, 3m, 1m, 1m);
        Assert.True((await f.ReceiveAsync(1m)).IsSuccess);
        await using (var seed = f.Provider.CreateAsyncScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var lot = await db.InventoryLots.SingleAsync(x => x.PurchaseItemId == f.ItemId);
            lot.EffectiveUnitCost = 1.3333m;
            (await db.InventoryLotBucketBalances.SingleAsync(x => x.LotId == lot.Id)).Quantity = 0m;
            (await db.StockBalances.SingleAsync(x => x.ProductId == f.ProductId)).SellableQty = 0m;
            var state = await db.ProductCostStates.SingleAsync(x => x.ProductId == f.ProductId);
            state.CostedQty = 0m; state.TotalInventoryCost = 0m; state.MovingAverageCost = 0m;
            var movement = new EdgeRetails.Domain.Inventory.InventoryMovement
            {
                ProductId = f.ProductId, MovementType = EdgeRetails.Domain.Inventory.InventoryMovementType.SaleOut,
                ReferenceType = "PASS4_LEGACY_CONSUMPTION", ActorId = f.ActorId,
                CorrelationId = Guid.NewGuid(), OccurredAt = DateTimeOffset.UtcNow, UnitCostSnapshot = 1.3333m
            };
            db.InventoryMovements.Add(movement);
            db.InventoryMovementEffects.Add(new EdgeRetails.Domain.Inventory.InventoryMovementEffect
            { MovementId = movement.Id, StockBucket = EdgeRetails.Domain.Inventory.InventoryBucket.Sellable,
                QuantityDelta = -1m, QuantityBefore = 1m, QuantityAfter = 0m });
            db.InventoryLotConsumptions.Add(new EdgeRetails.Domain.Inventory.InventoryLotConsumption
            { LotId = lot.Id, MovementId = movement.Id, Quantity = 1m,
                UnitCostSnapshot = 1.3333m, TotalCostSnapshot = 1.3333m });
            await db.SaveChangesAsync();
            Assert.Equal(1.3333m, await seed.ServiceProvider.GetRequiredService<IInventoryRepository>()
                .GetPurchaseItemReceivedCarryingValueAsync(f.ItemId, false, default));
        }
        var command = f.Intake(2m);
        Assert.True((await f.ReceiveAsync(command)).IsSuccess);
        Assert.True((await f.ReceiveAsync(command)).Value!.WasExisting);
        await using var read = f.Provider.CreateAsyncScope();
        var persisted = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var carrying = (await persisted.ProductCostStates.SingleAsync(x => x.ProductId == f.ProductId)).TotalInventoryCost;
        var consumed = await persisted.InventoryLotConsumptions.Join(persisted.InventoryLots,
            x => x.LotId, x => x.Id, (consumption, lot) => new { consumption.TotalCostSnapshot, lot.PurchaseItemId })
            .Where(x => x.PurchaseItemId == f.ItemId).SumAsync(x => x.TotalCostSnapshot);
        Assert.Equal(2.6667m, carrying); Assert.Equal(1.3333m, consumed); Assert.Equal(4m, carrying + consumed);
        Assert.Equal(2m, (await persisted.StockBalances.SingleAsync(x => x.ProductId == f.ProductId)).SellableQty);
        Assert.Equal(3m, await persisted.InventoryLots.Where(x => x.PurchaseItemId == f.ItemId).SumAsync(x => x.ReceivedQuantity));
        Assert.Equal(3, await persisted.InventoryMovements.CountAsync(x => x.ProductId == f.ProductId));
    }

    [Fact]
    public async Task G04_LowCostLargeBulkOrderConservesFiveWithoutNegativeFinalCost()
    {
        await using var f = await Fixture.CreateAsync(TrackingMode.Quantity, 1m, 100_000m, 0.00005m);
        Assert.True((await f.ReceiveAsync(99_999m)).IsSuccess);
        Assert.True((await f.ReceiveAsync(1m)).IsSuccess);
        await using var read = f.Provider.CreateAsyncScope();
        var db = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Equal(5m, (await db.ProductCostStates.SingleAsync(x => x.ProductId == f.ProductId)).TotalInventoryCost);
        Assert.Equal(5m, (await db.PurchaseItems.SingleAsync(x => x.Id == f.ItemId)).EffectiveLineCost);
        Assert.Equal(100_000m, (await db.StockBalances.SingleAsync(x => x.ProductId == f.ProductId)).SellableQty);
        Assert.Empty(await db.InventoryUnits.Where(x => x.ProductId == f.ProductId).ToListAsync());
        Assert.All(await db.InventoryLots.Where(x => x.ProductId == f.ProductId).ToArrayAsync(), x => Assert.True(x.EffectiveUnitCost >= 0));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task G02_FirstOrderRacingFactorMutationUsesOneLockedTruth(bool orderWins)
    {
        await using var f = await Fixture.CreateAsync(TrackingMode.Quantity, 2m, createOrder: false);
        using var gate = new FlushGate();
        var pid = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<Result<CreatePurchaseResult>> Purchase(bool winner)
        {
            await using var scope = f.Provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            if (!winner) { await db.Database.OpenConnectionAsync(); pid.SetResult(((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID); }
            var handler = winner ? ActivatorUtilities.CreateInstance<CreatePurchaseHandler>(scope.ServiceProvider, new GateUnitOfWork(db, gate))
                : scope.ServiceProvider.GetRequiredService<CreatePurchaseHandler>();
            return await handler.HandleAsync(f.Order(), default);
        }
        async Task<Result> Configure(bool winner)
        {
            await using var scope = f.Provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            if (!winner) { await db.Database.OpenConnectionAsync(); pid.SetResult(((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID); }
            var handler = winner ? ActivatorUtilities.CreateInstance<ConfigureProductUnitsHandler>(scope.ServiceProvider, new GateUnitOfWork(db, gate))
                : scope.ServiceProvider.GetRequiredService<ConfigureProductUnitsHandler>();
            return await handler.HandleAsync(f.Configure(3m), default);
        }
        Task<Result<CreatePurchaseResult>> purchaseTask;
        Task<Result> configureTask;
        if (orderWins)
        {
            purchaseTask = Task.Run(() => Purchase(true)); await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
            configureTask = Task.Run(() => Configure(false));
        }
        else
        {
            configureTask = Task.Run(() => Configure(true)); await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
            purchaseTask = Task.Run(() => Purchase(false));
        }
        try { await AssertLockWaitAsync(await pid.Task.WaitAsync(TimeSpan.FromSeconds(10))); }
        finally { gate.Release.TrySetResult(true); }
        await Task.WhenAll(purchaseTask, configureTask).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True((await purchaseTask).IsSuccess, (await purchaseTask).Error?.Message);
        Assert.Equal(!orderWins, (await configureTask).IsSuccess);
        if (orderWins)
        {
            Assert.Equal("catalog.product_unit_factor_locked", (await configureTask).Error!.Code);
        }

        await using var read = f.Provider.CreateAsyncScope();
        var persisted = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var item = await persisted.PurchaseItems.SingleAsync(x => x.ProductId == f.ProductId);
        Assert.Equal(orderWins ? 2m : 3m, item.FactorToBaseSnapshot);
        Assert.Equal(item.FactorToBaseSnapshot, (await persisted.ProductUnits.SingleAsync(x => x.Id == f.ProductUnitId)).FactorToBaseUnit);
        Assert.Equal(10m * item.FactorToBaseSnapshot, item.BaseQuantity);
        Assert.Empty(await persisted.InventoryMovements.Where(x => x.ProductId == f.ProductId).ToListAsync());
    }

    private static async Task AssertLockWaitAsync(int pid)
    {
        await using var connection = new NpgsqlConnection(Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB"));
        await connection.OpenAsync();
        for (var i = 0; i < 100; i++)
        {
            await using var command = new NpgsqlCommand("SELECT wait_event_type FROM pg_stat_activity WHERE pid = @pid", connection);
            command.Parameters.AddWithValue("pid", pid);
            if (await command.ExecuteScalarAsync() is string value && value == "Lock")
            {
                return;
            }

            await Task.Delay(20);
        }
        Assert.Fail("Competing operation never demonstrated an actual PostgreSQL lock wait.");
    }
    private sealed class FlushGate : IDisposable
    {
        public TaskCompletionSource<bool> Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Dispose() => Release.TrySetResult(true);
    }
    private sealed class GateUnitOfWork(EdgeRetailsDbContext db, FlushGate gate) : IUnitOfWork
    {
        public async Task<int> SaveChangesAsync(CancellationToken ct)
        {
            var count = await db.SaveChangesAsync(ct); gate.Reached.TrySetResult(true);
            await gate.Release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct); return count;
        }
    }
    private sealed class FlushThenFail(EdgeRetailsDbContext db) : IUnitOfWork
    {
        public bool Flushed { get; private set; }
        public async Task<int> SaveChangesAsync(CancellationToken ct)
        {
            await db.SaveChangesAsync(ct); Flushed = true;
            throw new BusinessRuleException("pass4.after_sql_flush", "Owned failure after real SQL flush.");
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public ServiceProvider Provider { get; } = Phase2PostgresTestHarness.BuildProvider();
        public Guid ProductId { get; private set; }
        public Guid ProductUnitId { get; private set; }
        public Guid BaseUnitId { get; private set; }
        public Guid PackUnitId { get; private set; }
        public Guid BaseProductUnitId { get; private set; }
        public Guid SupplierId { get; private set; }
        public Guid ActorId { get; private set; }
        public Guid PurchaseId { get; private set; }
        public Guid ItemId { get; private set; }
        private decimal _quantity, _rawCost, _charges;
        private int _initialClaimCount;
        public static async Task<Fixture> CreateAsync(TrackingMode mode, decimal factor, decimal quantity = 10m,
            decimal rawCost = 1000m, decimal charges = 0m, bool immediate = false, bool createOrder = true)
        {
            var f = new Fixture { _quantity = quantity, _rawCost = rawCost, _charges = charges };
            await using var scope = f.Provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
            var seeded = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);
            f.ProductId = seeded.ProductId; f.SupplierId = seeded.SupplierId; f.ActorId = seeded.ActorId;
            f.BaseProductUnitId = seeded.ProductUnitId; f.BaseUnitId = seeded.UnitId;
            var product = await db.Products.SingleAsync(x => x.Id == f.ProductId); product.TrackingMode = mode;
            var pack = new Unit { Name = "Pass4 Pack-" + Guid.NewGuid().ToString("N"), Symbol = "p4-" + Guid.NewGuid().ToString("N")[..8] };
            var mapping = new ProductUnit { ProductId = f.ProductId, UnitId = pack.Id, FactorToBaseUnit = factor, CanPurchase = true, CanSell = true };
            f.ProductUnitId = mapping.Id; f.PackUnitId = pack.Id;
            db.Units.Add(pack); db.ProductUnits.Add(mapping); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            f._initialClaimCount = await db.InventoryUnitIdentityClaims.CountAsync();
            if (createOrder)
            {
                // Oversized direct-intake hostile cases start from a controlled persisted legacy order.
                var orderQuantity = quantity > 100_000m && mode == TrackingMode.Container ? 10m : quantity;
                var order = await scope.ServiceProvider.GetRequiredService<CreatePurchaseHandler>().HandleAsync(f.Order(orderQuantity, immediate), default);
                Assert.True(order.IsSuccess, order.Error?.Message); f.PurchaseId = order.Value!.PurchaseId;
                var item = await db.PurchaseItems.SingleAsync(x => x.PurchaseId == f.PurchaseId); f.ItemId = item.Id;
                if (orderQuantity != quantity) { item.EnteredQuantity = quantity; item.BaseQuantity = quantity * factor;
                    item.EffectiveLineCost = item.BaseQuantity * item.EffectiveBaseUnitCost; await db.SaveChangesAsync(); }
            }
            return f;
        }
        public CreatePurchaseCommand Order(decimal? quantity = null, bool immediate = false) => new(SupplierId,
            "P4-" + Guid.NewGuid().ToString("N"), DateOnly.FromDateTime(DateTime.UtcNow), null, _charges,
            PurchaseSettlementMode.External, ActorId, Guid.NewGuid(),
            [new(ProductId, ProductUnitId, quantity ?? _quantity, _rawCost, 150m, [])], ReceiveStockImmediately: immediate);
        public ConfigureProductUnitsCommand Configure(decimal factor) => new(ProductId,
            [new(BaseUnitId, 1m, true, true, true, true, true), new(PackUnitId, factor, true, true, true, false, false)], ActorId);
        public ReceiveProductIntakeCommand Intake(decimal quantity) => new(PurchaseId, ProductId, ProductUnitId, quantity, null, [], ActorId, Guid.NewGuid());
        public Task<Result<ReceiveProductIntakeResult>> ReceiveAsync(decimal quantity) => ReceiveAsync(Intake(quantity));
        public async Task<Result<ReceiveProductIntakeResult>> ReceiveAsync(ReceiveProductIntakeCommand command)
        {
            await using var scope = Provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ReceiveProductIntakeHandler>().HandleAsync(command);
        }
        public async Task MutateItemAsync(Action<PurchaseItem> mutation)
        {
            await using var scope = Provider.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            mutation(await db.PurchaseItems.SingleAsync(x => x.Id == ItemId)); await db.SaveChangesAsync();
        }
        public async Task AssertNoInventoryAsync()
        {
            await using var scope = Provider.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            Assert.Empty(await db.InventoryMovements.Where(x => x.ProductId == ProductId).ToListAsync());
            Assert.Empty(await db.InventoryLots.Where(x => x.ProductId == ProductId).ToListAsync());
            Assert.Empty(await db.InventoryUnits.Where(x => x.ProductId == ProductId).ToListAsync());
            Assert.Empty(await db.StockBalances.Where(x => x.ProductId == ProductId).ToListAsync());
            Assert.Empty(await db.ProductCostStates.Where(x => x.ProductId == ProductId).ToListAsync());
            Assert.Empty(await db.SupplierProducts.Where(x => x.ProductId == ProductId).ToListAsync());
            Assert.Equal(_initialClaimCount, await db.InventoryUnitIdentityClaims.CountAsync());
        }
        public ValueTask DisposeAsync() => Provider.DisposeAsync();
    }
}
