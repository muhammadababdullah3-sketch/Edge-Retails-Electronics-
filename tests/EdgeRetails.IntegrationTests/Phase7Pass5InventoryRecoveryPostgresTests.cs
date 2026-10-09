using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Reporting;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using EdgeRetails.Application.Features.Warranty;
using EdgeRetails.Domain.Warranty;
using Npgsql;

namespace EdgeRetails.IntegrationTests;

// NEW_COVERAGE: all arrangements and observations use the owned PostgreSQL fixture.
[Collection("Phase2PostgresIntegration")]
public sealed class Phase7Pass5InventoryRecoveryPostgresTests
{
    [Theory]
    [InlineData("inactive")]
    [InlineData("counting")]
    [InlineData("review")]
    public async Task FoundRecovery_RespectsInactiveProductAndActiveStocktakeGuards(string guard)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var fixture = await FoundFixtureAsync(provider, TrackingMode.Serialized, 1, 100m);
        var operation = await MissingForFoundAsync(provider, fixture, [fixture.Units[0].Id], InventoryBucket.Sellable);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        var source = await db.InventoryMovements.SingleAsync(x => x.CorrelationId == operation);
        Guid? stocktakeId = null;
        if (guard == "inactive")
        {
            (await db.Products.SingleAsync(x => x.Id == fixture.ProductId)).IsActive = false;
            await db.SaveChangesAsync();
        }
        else
        {
            var category = new Category { Name = "P5-found-guard-" + Guid.NewGuid(), IdentitySymbol = "G" + Guid.NewGuid().ToString("N")[..3].ToUpperInvariant() };
            db.Categories.Add(category);
            (await db.Products.SingleAsync(x => x.Id == fixture.ProductId)).CategoryId = category.Id;
            await db.SaveChangesAsync();
            var created = await services.GetRequiredService<CreateStocktakeHandler>().HandleAsync(new(StocktakeScope.Category,
                category.Id, fixture.ActorId, "Active count guards recovery", Guid.NewGuid()), default);
            Assert.True(created.IsSuccess, created.Error?.Message); stocktakeId = created.Value;
            Assert.True((await services.GetRequiredService<StartStocktakeHandler>().HandleAsync(new(created.Value, fixture.ActorId, Guid.NewGuid()), default)).IsSuccess);
            if (guard == "review")
            {
                Assert.True((await services.GetRequiredService<RecordSerializedStocktakeHandler>().HandleAsync(new(created.Value,
                    fixture.ProductId, [], [], fixture.ActorId, "Count awaiting approval", Guid.NewGuid()), default)).IsSuccess);
                Assert.True((await services.GetRequiredService<ReviewStocktakeHandler>().HandleAsync(new(created.Value, fixture.ActorId, Guid.NewGuid()), default)).IsSuccess);
            }
        }
        try
        {
            var before = await StateAsync(provider, fixture.ProductId, fixture.SupplierId, fixture.ActorId);
            var intent = new FoundInventoryUnitCommand(fixture.ProductId, fixture.Units[0].Id, source.Id, InventoryBucket.Sellable,
                "Guarded recovery", fixture.ActorId, Guid.NewGuid());
            var found = await services.GetRequiredService<FoundInventoryUnitHandler>().HandleAsync(intent, default);
            Assert.False(found.IsSuccess);
            Assert.Equal(guard == "inactive" ? "inventory.found_condition_unresolved" : "inventory.stocktake_in_progress", found.Error?.Code);
            Assert.Equal(before, await StateAsync(provider, fixture.ProductId, fixture.SupplierId, fixture.ActorId));
            if (guard == "inactive")
            {
                var damaged = await services.GetRequiredService<FoundInventoryUnitHandler>().HandleAsync(intent with { TargetCondition = InventoryBucket.Damaged }, default);
                Assert.True(damaged.IsSuccess, damaged.Error?.Message);
                db.ChangeTracker.Clear();
                Assert.Equal(InventoryUnitStatus.Damaged, (await db.InventoryUnits.SingleAsync(x => x.Id == fixture.Units[0].Id)).Status);
            }
        }
        finally
        {
            if (stocktakeId.HasValue)
            {
                Assert.True((await services.GetRequiredService<CancelStocktakeHandler>().HandleAsync(new(stocktakeId.Value, fixture.ActorId, Guid.NewGuid()), default)).IsSuccess);
            }
        }
    }

    [Fact]
    public async Task FoundRecovery_ConditionTransferCannotMasqueradeAsLotOrigin()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var fixture = await FoundFixtureAsync(provider, TrackingMode.Serialized, 1, 100m);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        var unit = fixture.Units[0];
        var transferred = await services.GetRequiredService<ITransactionRunner>().ExecuteAsync(async token =>
        {
            var result = await services.GetRequiredService<IInventoryConditionService>().TransferAsync(new(fixture.ProductId,
                InventoryBucket.Sellable, InventoryBucket.Damaged, 1m, fixture.ActorId, "Origin ambiguity probe", InventoryUnitIds: [unit.Id]), token);
            if (result.IsSuccess) { await services.GetRequiredService<IUnitOfWork>().SaveChangesAsync(token); }
            return result;
        }, default);
        Assert.True(transferred.IsSuccess, transferred.Error?.Message);
        var transfer = await db.InventoryMovements.SingleAsync(x => x.ProductId == fixture.ProductId && x.Reason == "Origin ambiguity probe");
        var missing = await MissingForFoundAsync(provider, fixture, [unit.Id], InventoryBucket.Damaged);
        var source = await db.InventoryMovements.SingleAsync(x => x.CorrelationId == missing);
        // NEW_COVERAGE: malformed lot provenance is confined to this isolated fixture.
        (await db.InventoryLots.SingleAsync(x => x.Id == unit.InventoryLotId)).SourceMovementId = transfer.Id;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var before = await StateAsync(provider, fixture.ProductId, fixture.SupplierId, fixture.ActorId);
        var found = await services.GetRequiredService<FoundInventoryUnitHandler>().HandleAsync(new(fixture.ProductId, unit.Id,
            source.Id, InventoryBucket.Damaged, "Cannot infer acquisition from a transfer", fixture.ActorId, Guid.NewGuid()), default);
        Assert.False(found.IsSuccess);
        Assert.Equal("inventory.found_source_requires_review", found.Error?.Code);
        Assert.Equal(before, await StateAsync(provider, fixture.ProductId, fixture.SupplierId, fixture.ActorId));
    }

    [Theory]
    [InlineData(TrackingMode.Serialized, 1)]
    [InlineData(TrackingMode.IndividualPiece, 1)]
    [InlineData(TrackingMode.Container, 90)]
    public async Task FoundRecovery_PostedStocktakeUsesFrozenSourceDespiteCatalogCostChanges(TrackingMode mode, int factor)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var fixture = await FoundFixtureAsync(provider, mode, factor, 100m);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        var category = new Category { Name = "P5-found-count-" + Guid.NewGuid(), IdentitySymbol = "F" + Guid.NewGuid().ToString("N")[..3].ToUpperInvariant() };
        db.Categories.Add(category);
        var product = await db.Products.SingleAsync(x => x.Id == fixture.ProductId);
        product.CategoryId = category.Id;
        await db.SaveChangesAsync();
        var created = await services.GetRequiredService<CreateStocktakeHandler>().HandleAsync(new(StocktakeScope.Category,
            category.Id, fixture.ActorId, "Exact shortage then recovery", Guid.NewGuid()), default);
        Assert.True(created.IsSuccess, created.Error?.Message);
        try
        {
            Assert.True((await services.GetRequiredService<StartStocktakeHandler>().HandleAsync(new(created.Value, fixture.ActorId, Guid.NewGuid()), default)).IsSuccess);
            Assert.True((await services.GetRequiredService<RecordSerializedStocktakeHandler>().HandleAsync(new(created.Value,
                fixture.ProductId, [], [], fixture.ActorId, "Both identities absent", Guid.NewGuid()), default)).IsSuccess);
            Assert.True((await services.GetRequiredService<ReviewStocktakeHandler>().HandleAsync(new(created.Value, fixture.ActorId, Guid.NewGuid()), default)).IsSuccess);
            Assert.True((await services.GetRequiredService<PostStocktakeHandler>().HandleAsync(new(created.Value, fixture.ActorId,
                ClientOperationId: Guid.NewGuid()), default)).IsSuccess);
            db.ChangeTracker.Clear();
            var source = await (from movement in db.InventoryMovements join link in db.InventoryMovementUnits on movement.Id equals link.MovementId
                where movement.ReferenceType == "STOCKTAKE" && movement.ReferenceId == created.Value && link.InventoryUnitId == fixture.Units[0].Id
                select movement).AsNoTracking().SingleAsync();
            var sourceJson = System.Text.Json.JsonSerializer.Serialize(source);
            (await db.Products.SingleAsync(x => x.Id == fixture.ProductId)).ReferencePurchaseCost = 9999m;
            await db.SaveChangesAsync();
            var found = await services.GetRequiredService<FoundInventoryUnitHandler>().HandleAsync(new(fixture.ProductId,
                fixture.Units[0].Id, source.Id, InventoryBucket.Defective, "Located; condition checked", fixture.ActorId, Guid.NewGuid()), default);
            Assert.True(found.IsSuccess, found.Error?.Code + ":" + found.Error?.Message);
            Assert.Equal(100m, found.Value!.RestoredInventoryValue); Assert.Equal(source.RecognizedLossAmount, found.Value.InventoryLossRecoveryGain);
            db.ChangeTracker.Clear();
            Assert.Equal(100m, (await db.ProductCostStates.SingleAsync(x => x.ProductId == fixture.ProductId)).TotalInventoryCost);
            Assert.Equal((decimal)factor, (await db.StockBalances.SingleAsync(x => x.ProductId == fixture.ProductId)).DefectiveQty);
            Assert.Equal(InventoryUnitStatus.Defective, (await db.InventoryUnits.SingleAsync(x => x.Id == fixture.Units[0].Id)).Status);
            Assert.Equal(InventoryUnitStatus.Missing, (await db.InventoryUnits.SingleAsync(x => x.Id == fixture.Units[1].Id)).Status);
            Assert.Equal(sourceJson, System.Text.Json.JsonSerializer.Serialize(await db.InventoryMovements.AsNoTracking().SingleAsync(x => x.Id == source.Id)));
        }
        finally
        {
            db.ChangeTracker.Clear();
            var status = (await db.Stocktakes.SingleAsync(x => x.Id == created.Value)).Status;
            if (status is StocktakeStatus.Draft or StocktakeStatus.Counting or StocktakeStatus.Review)
            {
                Assert.True((await services.GetRequiredService<CancelStocktakeHandler>().HandleAsync(new(created.Value, fixture.ActorId, Guid.NewGuid()), default)).IsSuccess);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FoundRecovery_FailureBeforeCommitAndAfterSqlFlushRestoresAllEconomicFacts(bool flush)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var fixture = await FoundFixtureAsync(provider, TrackingMode.Serialized, 1, 100m);
        var missing = await MissingForFoundAsync(provider, fixture, [fixture.Units[0].Id], InventoryBucket.Sellable);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        var source = await db.InventoryMovements.SingleAsync(x => x.CorrelationId == missing);
        var intent = new FoundInventoryUnitCommand(fixture.ProductId, fixture.Units[0].Id, source.Id, InventoryBucket.Damaged,
            "Found failure probe", fixture.ActorId, Guid.NewGuid());
        var before = await StateAsync(provider, fixture.ProductId, fixture.SupplierId, fixture.ActorId);
        var failingSave = new FoundFailingUnitOfWork(db, flush);
        var handler = new FoundInventoryUnitHandler(services.GetRequiredService<ICatalogRepository>(), services.GetRequiredService<IInventoryRepository>(),
            services.GetRequiredService<IWarrantyRepository>(), services.GetRequiredService<IInventoryCostAllocator>(),
            services.GetRequiredService<IApplicationPermissionAuthorizer>(), services.GetRequiredService<IOperationLock>(),
            services.GetRequiredService<IResourceLock>(), services.GetRequiredService<IOperationOutcomeLedger>(),
            services.GetRequiredService<IBusinessAuditWriter>(), services.GetRequiredService<IClock>(), services.GetRequiredService<ITransactionRunner>(), failingSave);
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(intent, default));
        Assert.Equal(flush, failingSave.Flushed);
        Assert.Equal(before, await StateAsync(provider, fixture.ProductId, fixture.SupplierId, fixture.ActorId));
        db.ChangeTracker.Clear();
        Assert.False(await db.OperationOutcomes.AnyAsync(x => x.ClientOperationId == intent.ClientOperationId));
        var retried = await services.GetRequiredService<FoundInventoryUnitHandler>().HandleAsync(intent, default);
        Assert.True(retried.IsSuccess, retried.Error?.Message); Assert.False(retried.Value!.WasExisting);
    }

    private sealed class FoundFailingUnitOfWork(EdgeRetailsDbContext db, bool flush) : IUnitOfWork
    {
        public bool Flushed { get; private set; }
        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        {
            if (flush)
            {
                await db.SaveChangesAsync(cancellationToken); Flushed = true;
            }
            throw new InvalidOperationException("Owned Found failure before commit");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FoundRecovery_RealDatabaseLockSerializesSameAndFreshOperationRetries(bool sameOperation)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var fixture = await FoundFixtureAsync(provider, TrackingMode.Serialized, 1, 100m);
        var missing = await MissingForFoundAsync(provider, fixture, [fixture.Units[0].Id], InventoryBucket.Sellable);
        await using var winnerScope = provider.CreateAsyncScope();
        var winnerDb = winnerScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var source = await winnerDb.InventoryMovements.SingleAsync(x => x.CorrelationId == missing);
        var intent = new FoundInventoryUnitCommand(fixture.ProductId, fixture.Units[0].Id, source.Id, InventoryBucket.Sellable,
            "Concurrent physical recovery", fixture.ActorId, Guid.NewGuid());
        await using var transaction = await winnerDb.Database.BeginTransactionAsync();
        var winner = await winnerScope.ServiceProvider.GetRequiredService<FoundInventoryUnitHandler>().HandleAsync(intent, default);
        Assert.True(winner.IsSuccess, winner.Error?.Message);
        await using var contenderScope = provider.CreateAsyncScope();
        var contenderDb = contenderScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        await contenderDb.Database.OpenConnectionAsync();
        var application = "p5-found-" + Guid.NewGuid().ToString("N");
        await contenderDb.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('application_name', {application}, false)");
        var contender = contenderScope.ServiceProvider.GetRequiredService<FoundInventoryUnitHandler>().HandleAsync(
            sameOperation ? intent : intent with { ClientOperationId = Guid.NewGuid() }, default);
        try
        {
            await using var observer = new NpgsqlConnection(Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB"));
            await observer.OpenAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (true)
            {
                await using var query = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE application_name=@name AND wait_event_type='Lock')", observer);
                query.Parameters.AddWithValue("name", application);
                if ((bool)(await query.ExecuteScalarAsync(timeout.Token))!) { break; }
                await Task.Delay(50, timeout.Token);
            }
            Assert.False(contender.IsCompleted);
        }
        finally { await transaction.CommitAsync(); }
        var second = await contender.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(sameOperation, second.IsSuccess);
        if (sameOperation)
        {
            Assert.True(second.Value!.WasExisting); Assert.Equal(winner.Value!.MovementId, second.Value.MovementId);
        }
        else
        {
            Assert.Equal("inventory.found_unit_not_missing", second.Error?.Code);
        }
        await using var read = provider.CreateAsyncScope();
        var db = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Single(await db.InventoryMovements.Where(x => x.ReferenceType == FoundInventoryUnitHandler.RecoveryReferenceType && x.ReferenceId == source.Id).ToArrayAsync());
        Assert.Single(await db.BusinessAuditEvents.Where(x => x.CorrelationId == intent.ClientOperationId).ToArrayAsync());
        Assert.Equal(200m, (await db.ProductCostStates.SingleAsync(x => x.ProductId == fixture.ProductId)).TotalInventoryCost);
        Assert.Equal(2m, (await db.StockBalances.SingleAsync(x => x.ProductId == fixture.ProductId)).SellableQty);
    }

    [Theory]
    [InlineData("wrong_product_child")]
    [InlineData("duplicate_recovery")]
    [InlineData("replay_reason")]
    public async Task FoundRecovery_MalformedAuthorityCannotBeHiddenByParentFilterOrReplay(string corruption)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var fixture = await FoundFixtureAsync(provider, TrackingMode.Serialized, 1, 100m);
        var operation = await MissingForFoundAsync(provider, fixture, fixture.Units.Select(x => x.Id).ToArray(), InventoryBucket.Sellable);
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var source = await (from link in db.InventoryMovementUnits join movement in db.InventoryMovements on link.MovementId equals movement.Id
            where movement.CorrelationId == operation && link.InventoryUnitId == fixture.Units[0].Id select movement).SingleAsync();
        var intent = new FoundInventoryUnitCommand(fixture.ProductId, fixture.Units[0].Id, source.Id, InventoryBucket.Sellable,
            "Authoritative recovery reason", fixture.ActorId, Guid.NewGuid());
        if (corruption == "wrong_product_child")
        {
            var other = await FoundFixtureAsync(provider, TrackingMode.Serialized, 1, 100m);
            var malformed = new InventoryMovement { ProductId = other.ProductId, ReferenceType = source.ReferenceType, ReferenceId = source.ReferenceId,
                MovementType = source.MovementType, ActorId = source.ActorId, CorrelationId = source.CorrelationId,
                OccurredAt = source.OccurredAt, Reason = source.Reason, UnitCostSnapshot = 100m, RecognizedLossAmount = 100m };
            db.InventoryMovements.Add(malformed);
            db.InventoryMovementUnits.Add(new() { MovementId = malformed.Id, InventoryUnitId = other.Units[0].Id,
                FromStatus = InventoryUnitStatus.InStock, ToStatus = InventoryUnitStatus.Missing });
            db.InventoryMovementEffects.Add(new() { MovementId = malformed.Id, StockBucket = InventoryBucket.Sellable,
                QuantityDelta = -1m, QuantityBefore = 1m, QuantityAfter = 0m });
            db.InventoryLotConsumptions.Add(new() { MovementId = malformed.Id, LotId = other.Units[0].InventoryLotId!.Value,
                Quantity = 1m, UnitCostSnapshot = 100m, TotalCostSnapshot = 100m, OccurredAt = source.OccurredAt });
        }
        else
        {
            var found = await scope.ServiceProvider.GetRequiredService<FoundInventoryUnitHandler>().HandleAsync(intent, default);
            Assert.True(found.IsSuccess, found.Error?.Message);
            if (corruption == "replay_reason")
            {
                (await db.InventoryMovements.SingleAsync(x => x.Id == found.Value!.MovementId)).Reason = "Contradictory persisted authority";
            }
            else
            {
                var duplicate = new InventoryMovement { ProductId = fixture.ProductId, ReferenceType = FoundInventoryUnitHandler.RecoveryReferenceType,
                    ReferenceId = source.Id, MovementType = InventoryMovementType.StockAdjustment, ActorId = fixture.ActorId,
                    CorrelationId = Guid.NewGuid(), OccurredAt = DateTimeOffset.UtcNow, Reason = "Conflicting second reference", UnitCostSnapshot = 100m };
                db.InventoryMovements.Add(duplicate);
                db.InventoryMovementUnits.Add(new() { MovementId = duplicate.Id, InventoryUnitId = fixture.Units[1].Id,
                    FromStatus = InventoryUnitStatus.Missing, ToStatus = InventoryUnitStatus.InStock });
                db.InventoryMovementEffects.Add(new() { MovementId = duplicate.Id, StockBucket = InventoryBucket.Sellable,
                    QuantityDelta = 1m, QuantityBefore = 1m, QuantityAfter = 2m });
            }
        }
        // NEW_COVERAGE: malformed history exists only in this owned fixture.
        await db.SaveChangesAsync();
        var before = await StateAsync(provider, fixture.ProductId, fixture.SupplierId, fixture.ActorId);
        var result = await scope.ServiceProvider.GetRequiredService<FoundInventoryUnitHandler>().HandleAsync(intent, default);
        Assert.False(result.IsSuccess);
        Assert.Equal("inventory.found_source_requires_review", result.Error?.Code);
        Assert.Equal(before, await StateAsync(provider, fixture.ProductId, fixture.SupplierId, fixture.ActorId));
    }

    [Fact]
    public async Task FoundRecovery_UnresolvedShopWarrantyCannotBecomeSellable()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var fixture = await FoundFixtureAsync(provider, TrackingMode.Serialized, 1, 100m);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        var unit = fixture.Units[0];
        var unit1 = fixture.Units[1];
        var transfers = await services.GetRequiredService<ITransactionRunner>().ExecuteAsync(async token =>
        {
            var res1 = await services.GetRequiredService<IInventoryConditionService>().TransferAsync(new(fixture.ProductId,
                InventoryBucket.Sellable, InventoryBucket.Damaged, 2m, fixture.ActorId, "Checked damaged", InventoryUnitIds: [unit.Id, unit1.Id]), token);
            if (!res1.IsSuccess) { return res1; }
            await services.GetRequiredService<IUnitOfWork>().SaveChangesAsync(token);
            var res2 = await services.GetRequiredService<IInventoryConditionService>().TransferAsync(new(fixture.ProductId,
                InventoryBucket.Damaged, InventoryBucket.WithSupplier, 1m, fixture.ActorId, "Buffer with supplier", InventoryUnitIds: [unit1.Id]), token);
            if (res2.IsSuccess) { await services.GetRequiredService<IUnitOfWork>().SaveChangesAsync(token); }
            return res2;
        }, default);
        Assert.True(transfers.IsSuccess, transfers.Error?.Message);
        var sent = await services.GetRequiredService<SendShopStockToSupplierWarrantyHandler>().HandleAsync(new(fixture.ProductId,
            InventoryBucket.Damaged, 1m, fixture.SupplierId, unit.SourcePurchaseItemId, "Supplier must resolve", fixture.ActorId,
            Guid.NewGuid(), [unit.Id]), default);
        Assert.True(sent.IsSuccess, sent.Error?.Message);
        var operation = await MissingForFoundAsync(provider, fixture, [unit.Id], InventoryBucket.WithSupplier);
        db.ChangeTracker.Clear();
        var source = await db.InventoryMovements.SingleAsync(x => x.CorrelationId == operation);
        var before = await StateAsync(provider, fixture.ProductId, fixture.SupplierId, fixture.ActorId);
        var found = await services.GetRequiredService<FoundInventoryUnitHandler>().HandleAsync(new(fixture.ProductId, unit.Id, source.Id,
            InventoryBucket.Sellable, "Found while case remains supplier-held", fixture.ActorId, Guid.NewGuid()), default);
        Assert.False(found.IsSuccess);
        Assert.Equal("inventory.found_condition_unresolved", found.Error?.Code);
        Assert.Equal(before, await StateAsync(provider, fixture.ProductId, fixture.SupplierId, fixture.ActorId));
    }

    private static async Task<Guid> MissingForFoundAsync(ServiceProvider provider, FoundFixture fixture, IReadOnlyList<Guid> ids, InventoryBucket bucket)
    {
        await using var scope = provider.CreateAsyncScope();
        var operation = Guid.NewGuid();
        var quantity = (decimal)ids.Count;
        var result = await scope.ServiceProvider.GetRequiredService<CreateStockAdjustmentHandler>().HandleAsync(
            new(StockAdjustmentMode.Delta, StockAdjustmentReason.Lost,
                [new(fixture.ProductId, null, StockAdjustmentDirection.Decrease, bucket, quantity, null, InventoryUnitIds: ids)],
                fixture.ActorId, operation), default);
        Assert.True(result.IsSuccess, result.Error?.Message);
        return operation;
    }

    [Theory]
    [InlineData(TrackingMode.Serialized, 1, "100", InventoryBucket.Sellable)]
    [InlineData(TrackingMode.IndividualPiece, 1, "100", InventoryBucket.Damaged)]
    [InlineData(TrackingMode.Container, 90, "100", InventoryBucket.Defective)]
    [InlineData(TrackingMode.Serialized, 1, "0", InventoryBucket.Damaged)]
    [InlineData(TrackingMode.IndividualPiece, 1, "0", InventoryBucket.Sellable)]
    [InlineData(TrackingMode.Container, 90, "0", InventoryBucket.Sellable)]
    [InlineData(TrackingMode.Serialized, 1, "0.004", InventoryBucket.Defective)]
    public async Task FoundRecovery_PartialAndRepeatedEpisodesPreserveExactIdentityValueAndAllocatedLoss(
        TrackingMode mode, int factor, string costText, InventoryBucket target)
    {
        var cost = decimal.Parse(costText, System.Globalization.CultureInfo.InvariantCulture);
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var fixture = await FoundFixtureAsync(provider, mode, factor, cost);
        var original = fixture.Units;
        var missingOperation = Guid.NewGuid();
        await using (var scope = provider.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<CreateStockAdjustmentHandler>().HandleAsync(
                new(StockAdjustmentMode.Delta, StockAdjustmentReason.Lost,
                    [new(fixture.ProductId, mode == TrackingMode.Container ? fixture.ProductUnitId : null, StockAdjustmentDirection.Decrease,
                        InventoryBucket.Sellable, 2m * factor, 9999m, InventoryUnitIds: original.Select(x => x.Id).ToArray())], fixture.ActorId, missingOperation), default);
            Assert.True(result.IsSuccess, result.Error?.Message);
        }
        await using var read = provider.CreateAsyncScope();
        var db = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var sources = await (from link in db.InventoryMovementUnits join movement in db.InventoryMovements on link.MovementId equals movement.Id
            where movement.CorrelationId == missingOperation select new { link.InventoryUnitId, Movement = movement }).AsNoTracking().ToArrayAsync();
        var preservedSources = System.Text.Json.JsonSerializer.Serialize(sources.OrderBy(x => x.InventoryUnitId));
        var selected = original[1];
        var source = sources.Single(x => x.InventoryUnitId == selected.Id).Movement;
        var command = new FoundInventoryUnitCommand(fixture.ProductId, selected.Id, source.Id, target, "Found physically, checked condition", fixture.ActorId, Guid.NewGuid());
        var missingState = await StateAsync(provider, fixture.ProductId, fixture.SupplierId, fixture.ActorId);
        await using (var refusal = provider.CreateAsyncScope())
        {
            var handler = refusal.ServiceProvider.GetRequiredService<FoundInventoryUnitHandler>();
            Assert.False((await handler.HandleAsync(command with { ActorId = Guid.NewGuid() }, default)).IsSuccess);
            Assert.False((await handler.HandleAsync(command with { SourceMissingMovementId = sources.Single(x => x.InventoryUnitId == original[0].Id).Movement.Id }, default)).IsSuccess);
            Assert.False((await handler.HandleAsync(command with { TargetCondition = InventoryBucket.Scrap }, default)).IsSuccess);
        }
        Assert.Equal(missingState, await StateAsync(provider, fixture.ProductId, fixture.SupplierId, fixture.ActorId));
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<FoundInventoryUnitHandler>();
            var found = await handler.HandleAsync(command, default);
            Assert.True(found.IsSuccess, found.Error?.Code + ":" + found.Error?.Message);
            Assert.Equal(cost, found.Value!.RestoredInventoryValue); Assert.Equal(source.RecognizedLossAmount, found.Value.InventoryLossRecoveryGain);
            var replay = await handler.HandleAsync(command, default);
            Assert.True(replay.IsSuccess, replay.Error?.Message); Assert.True(replay.Value!.WasExisting); Assert.Equal(found.Value.MovementId, replay.Value.MovementId);
            Assert.False((await handler.HandleAsync(command with { Reason = "Changed reason" }, default)).IsSuccess);
            Assert.False((await handler.HandleAsync(command with { ClientOperationId = Guid.NewGuid() }, default)).IsSuccess);
        }
        db.ChangeTracker.Clear();
        Assert.Equal(cost, (await db.ProductCostStates.SingleAsync(x => x.ProductId == fixture.ProductId)).TotalInventoryCost);
        Assert.Equal((decimal)factor, (await db.StockBalances.SingleAsync(x => x.ProductId == fixture.ProductId)).Get(target));
        Assert.Equal(InventoryUnitStatus.Missing, (await db.InventoryUnits.SingleAsync(x => x.Id == original[0].Id)).Status);
        Assert.Equal(FoundRecoveryAuthority.StatusFor(target), (await db.InventoryUnits.SingleAsync(x => x.Id == selected.Id)).Status);
        var secondMissingOperation = Guid.NewGuid();
        await using (var scope = provider.CreateAsyncScope())
        {
            var secondMissing = await scope.ServiceProvider.GetRequiredService<CreateStockAdjustmentHandler>().HandleAsync(
                new(StockAdjustmentMode.Delta, StockAdjustmentReason.Lost,
                    [new(fixture.ProductId, mode == TrackingMode.Container ? fixture.ProductUnitId : null, StockAdjustmentDirection.Decrease,
                        target, factor, 9999m, InventoryUnitIds: [selected.Id])], fixture.ActorId, secondMissingOperation), default);
            Assert.True(secondMissing.IsSuccess, secondMissing.Error?.Code + ":" + secondMissing.Error?.Message);
        }
        db.ChangeTracker.Clear();
        var secondSource = await db.InventoryMovements.AsNoTracking().SingleAsync(x => x.CorrelationId == secondMissingOperation);
        Assert.Equal(cost, (await db.InventoryLotConsumptions.SingleAsync(x => x.MovementId == secondSource.Id)).TotalCostSnapshot);
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<FoundInventoryUnitHandler>();
            Assert.False((await handler.HandleAsync(command with { ClientOperationId = Guid.NewGuid() }, default)).IsSuccess);
            var replayOld = await handler.HandleAsync(command, default);
            Assert.True(replayOld.IsSuccess); Assert.True(replayOld.Value!.WasExisting);
            var secondFound = await handler.HandleAsync(command with { SourceMissingMovementId = secondSource.Id, ClientOperationId = Guid.NewGuid() }, default);
            Assert.True(secondFound.IsSuccess, secondFound.Error?.Code + ":" + secondFound.Error?.Message);
            Assert.Equal(cost, secondFound.Value!.RestoredInventoryValue); Assert.Equal(secondSource.RecognizedLossAmount, secondFound.Value.InventoryLossRecoveryGain);
            var firstSource = sources.Single(x => x.InventoryUnitId == original[0].Id).Movement;
            var firstFound = await handler.HandleAsync(new(fixture.ProductId, original[0].Id, firstSource.Id, InventoryBucket.Sellable,
                "Found other unit independently", fixture.ActorId, Guid.NewGuid()), default);
            Assert.True(firstFound.IsSuccess, firstFound.Error?.Message);
            Assert.Equal(cost, firstFound.Value!.RestoredInventoryValue); Assert.Equal(firstSource.RecognizedLossAmount, firstFound.Value.InventoryLossRecoveryGain);
        }
        db.ChangeTracker.Clear();
        var current = await db.InventoryUnits.AsNoTracking().Where(x => x.ProductId == fixture.ProductId).OrderBy(x => x.Id).ToArrayAsync();
        Assert.Equal(original.Select(x => (x.Id, x.TrackingCode, x.SerialNumber, x.Imei1, x.Imei2, x.SupplierProductId, x.ItemSequence,
                x.AcquisitionCost, x.OriginType, x.SourcePurchaseItemId, x.SourceStockAdjustmentItemId, x.SupplierCodeSnapshot, x.ProductSkuSnapshot)),
            current.Select(x => (x.Id, x.TrackingCode, x.SerialNumber, x.Imei1, x.Imei2, x.SupplierProductId, x.ItemSequence,
                x.AcquisitionCost, x.OriginType, x.SourcePurchaseItemId, x.SourceStockAdjustmentItemId, x.SupplierCodeSnapshot, x.ProductSkuSnapshot)));
        Assert.Equal(2m * cost, (await db.ProductCostStates.SingleAsync(x => x.ProductId == fixture.ProductId)).TotalInventoryCost);
        Assert.Equal(2m * factor, (await db.ProductCostStates.SingleAsync(x => x.ProductId == fixture.ProductId)).CostedQty);
        var recoveries = await db.InventoryMovements.Where(x => x.ProductId == fixture.ProductId && x.ReferenceType == FoundInventoryUnitHandler.RecoveryReferenceType).ToArrayAsync();
        Assert.Equal(3, recoveries.Length); Assert.All(recoveries, x => Assert.Equal(0m, x.RecognizedLossAmount));
        Assert.Equal(preservedSources, System.Text.Json.JsonSerializer.Serialize(
            (await (from link in db.InventoryMovementUnits join movement in db.InventoryMovements on link.MovementId equals movement.Id
                where movement.CorrelationId == missingOperation select new { link.InventoryUnitId, Movement = movement }).AsNoTracking().ToArrayAsync()).OrderBy(x => x.InventoryUnitId)));
        Assert.Equal(3L, (await db.SupplierProducts.SingleAsync(x => x.ProductId == fixture.ProductId && x.SupplierId == fixture.SupplierId)).NextItemSequence);
        Assert.Equal(fixture.IdentityClaimCount, await db.InventoryUnitIdentityClaims.CountAsync(x => original.Select(u => u.Id).ToArray().Contains(x.InventoryUnitId)));
        Assert.Empty(await db.CashMovements.Where(x => x.ActorId == fixture.ActorId).ToArrayAsync());
        Assert.Empty(await db.SupplierRefunds.Where(x => x.SupplierId == fixture.SupplierId).ToArrayAsync());
        Assert.Equal(fixture.SupplierBalance, await read.ServiceProvider.GetRequiredService<ISupplierAccountRepository>().GetCurrentBalanceAsync(fixture.SupplierId, default));
        Assert.Empty(await (from item in db.SaleItems join sale in db.Sales on item.SaleId equals sale.Id
            where item.ProductId == fixture.ProductId select sale).ToArrayAsync());
    }

    private sealed record FoundFixture(Guid ProductId, Guid ProductUnitId, Guid ActorId, Guid SupplierId,
        InventoryUnit[] Units, int IdentityClaimCount, decimal SupplierBalance);

    private static async Task<FoundFixture> FoundFixtureAsync(ServiceProvider provider, TrackingMode mode, int factor, decimal cost)
    {
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var seed = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db, 300m);
        var product = await db.Products.SingleAsync(x => x.Id == seed.ProductId);
        product.TrackingMode = mode; product.SerialTrackingEnabled = mode == TrackingMode.Serialized;
        (await db.ProductUnits.SingleAsync(x => x.Id == seed.ProductUnitId)).FactorToBaseUnit = factor;
        await db.SaveChangesAsync();
        if (cost >= 1m)
        {
            var purchase = await services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(
                new(seed.SupplierId, "P5-FOUND-" + Guid.NewGuid(), DateOnly.FromDateTime(DateTime.Today), null, 0m,
                    PurchaseSettlementMode.External, seed.ActorId, Guid.NewGuid(),
                    [new(seed.ProductId, seed.ProductUnitId, 2m, cost, 300m,
                        [new SerializedIdentityInput(mode == TrackingMode.Serialized ? "P5-F1-" + Guid.NewGuid() : null),
                         new SerializedIdentityInput(mode == TrackingMode.Serialized ? "P5-F2-" + Guid.NewGuid() : null)])], InitialPaymentAmount: 0m), default);
            Assert.True(purchase.IsSuccess, purchase.Error?.Message);
        }
        else
        {
            var opening = await services.GetRequiredService<CreateStockAdjustmentHandler>().HandleAsync(
                new(StockAdjustmentMode.Delta, StockAdjustmentReason.OpeningStock,
                    [new(seed.ProductId, mode == TrackingMode.Container ? seed.ProductUnitId : null, StockAdjustmentDirection.Increase,
                        InventoryBucket.Sellable, 2m * factor, cost / factor, seed.SupplierId,
                        [new(mode == TrackingMode.Serialized ? "P5-F1-" + Guid.NewGuid() : null), new(mode == TrackingMode.Serialized ? "P5-F2-" + Guid.NewGuid() : null)])],
                    seed.ActorId, Guid.NewGuid()), default);
            Assert.True(opening.IsSuccess, opening.Error?.Message);
        }
        var original = await db.InventoryUnits.AsNoTracking().Where(x => x.ProductId == seed.ProductId).OrderBy(x => x.Id).ToArrayAsync();
        return new(seed.ProductId, seed.ProductUnitId, seed.ActorId, seed.SupplierId, original,
            await db.InventoryUnitIdentityClaims.CountAsync(x => original.Select(u => u.Id).ToArray().Contains(x.InventoryUnitId)),
            await services.GetRequiredService<ISupplierAccountRepository>().GetCurrentBalanceAsync(seed.SupplierId, default));
    }

    [Fact]
    public async Task FoundRecovery_ProductionCompositionOffersExplicitSourceCommand()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var handlerType = typeof(CreateStockAdjustmentHandler).Assembly.GetType(
            "EdgeRetails.Application.Features.Inventory.FoundInventoryUnitHandler");
        Assert.NotNull(handlerType);
        Assert.NotNull(scope.ServiceProvider.GetService(handlerType));
    }

    [Theory]
    [InlineData(TrackingMode.Quantity, "free")]
    [InlineData(TrackingMode.Length, "free")]
    [InlineData(TrackingMode.Quantity, "omitted")]
    [InlineData(TrackingMode.Length, "omitted")]
    [InlineData(TrackingMode.Quantity, "negative")]
    [InlineData(TrackingMode.Length, "negative")]
    public async Task PositiveStocktake_OnlyExplicitFreeZeroIsApproved(TrackingMode mode, string basis)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var seed = await SeedAsync(provider, [0m, 0m]);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        var product = await db.Products.SingleAsync(x => x.Id == seed.ProductId);
        var category = new Category { Name = "P5-free-" + Guid.NewGuid(), IdentitySymbol = "F" + Guid.NewGuid().ToString("N")[..3].ToUpperInvariant() };
        db.Categories.Add(category); product.CategoryId = category.Id; product.TrackingMode = mode; product.ReferencePurchaseCost = null;
        await db.SaveChangesAsync();
        var created = await services.GetRequiredService<CreateStocktakeHandler>().HandleAsync(new(StocktakeScope.Category, category.Id, seed.ActorId, "Free quantity correction", Guid.NewGuid()), default);
        Assert.True(created.IsSuccess, created.Error?.Message);
        try
        {
        Assert.True((await services.GetRequiredService<StartStocktakeHandler>().HandleAsync(new(created.Value, seed.ActorId, Guid.NewGuid()), default)).IsSuccess);
        Assert.True((await services.GetRequiredService<RecordStocktakeCountHandler>().HandleAsync(new(created.Value, seed.ProductId, 22m, seed.ActorId, "Two extra free units", Guid.NewGuid()), default)).IsSuccess);
        Assert.True((await services.GetRequiredService<ReviewStocktakeHandler>().HandleAsync(new(created.Value, seed.ActorId, Guid.NewGuid()), default)).IsSuccess);
        var before = await StateAsync(provider, seed.ProductId, seed.SupplierId, seed.ActorId);
        var operation = Guid.NewGuid();
        var command = new PostStocktakeCommand(created.Value, seed.ActorId,
            basis == "omitted" ? null : new Dictionary<Guid, decimal> { [seed.ProductId] = basis == "free" ? 0m : -1m }, operation);
        var posted = await services.GetRequiredService<PostStocktakeHandler>().HandleAsync(command, default);
        if (basis != "free")
        {
            Assert.False(posted.IsSuccess); Assert.Equal("inventory.stocktake_positive_cost_required", posted.Error?.Code);
            Assert.Equal(before, await StateAsync(provider, seed.ProductId, seed.SupplierId, seed.ActorId));
            return;
        }
        Assert.True(posted.IsSuccess, posted.Error?.Message);
        Assert.True((await services.GetRequiredService<PostStocktakeHandler>().HandleAsync(command, default)).IsSuccess);
        db.ChangeTracker.Clear();
        var movement = await db.InventoryMovements.SingleAsync(x => x.CorrelationId == operation);
        Assert.Equal("STOCKTAKE", movement.ReferenceType); Assert.Equal(created.Value, movement.ReferenceId);
        Assert.Equal(0m, movement.UnitCostSnapshot); Assert.Equal(0m, movement.RecognizedLossAmount);
        var effect = await db.InventoryMovementEffects.SingleAsync(x => x.MovementId == movement.Id);
        Assert.Equal(2m, effect.QuantityDelta); Assert.Equal(20m, effect.QuantityBefore); Assert.Equal(22m, effect.QuantityAfter);
        var lot = await db.InventoryLots.SingleAsync(x => x.SourceMovementId == movement.Id);
        Assert.Equal(0m, lot.EffectiveUnitCost); Assert.Equal(2m, lot.ReceivedQuantity);
        Assert.Equal(2m, (await db.InventoryLotBucketBalances.SingleAsync(x => x.LotId == lot.Id)).Quantity);
        var pool = await db.ProductCostStates.SingleAsync(x => x.ProductId == seed.ProductId);
        Assert.Equal(22m, pool.CostedQty); Assert.Equal(0m, pool.TotalInventoryCost);
        Assert.Equal(22m, (await db.StockBalances.SingleAsync(x => x.ProductId == seed.ProductId)).SellableQty);
        Assert.Empty(await db.CashMovements.Where(x => x.ActorId == seed.ActorId).ToArrayAsync());
        Assert.Empty(await db.SupplierAccountEntries.Where(x => x.SupplierId == seed.SupplierId).ToArrayAsync());
        Assert.Empty(await db.InventoryUnits.Where(x => x.ProductId == seed.ProductId).ToArrayAsync());
        }
        finally
        {
            db.ChangeTracker.Clear();
            var status = (await db.Stocktakes.SingleAsync(x => x.Id == created.Value)).Status;
            if (status is StocktakeStatus.Draft or StocktakeStatus.Counting or StocktakeStatus.Review)
            {
                Assert.True((await services.GetRequiredService<CancelStocktakeHandler>().HandleAsync(new(created.Value, seed.ActorId, Guid.NewGuid()), default)).IsSuccess);
            }
        }
    }

    [Theory]
    [InlineData(TrackingMode.Serialized, 1, "100", true)]
    [InlineData(TrackingMode.IndividualPiece, 1, "100", true)]
    [InlineData(TrackingMode.Container, 90, "90", true)]
    [InlineData(TrackingMode.Serialized, 1, "0", false)]
    [InlineData(TrackingMode.Serialized, 1, "0", true)]
    [InlineData(TrackingMode.Serialized, 1, "0.004", false)]
    [InlineData(TrackingMode.Serialized, 1, "0.004", true)]
    public async Task MissingSource_PreservesActualPrecisionAllocatedLossAndObservationBoundary(
        TrackingMode mode, int factor, string costText, bool stocktake)
    {
        var cost = decimal.Parse(costText, System.Globalization.CultureInfo.InvariantCulture);
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var seed = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db, 300m);
        var category = new Category { Name = "P5-source-" + Guid.NewGuid(), IdentitySymbol = "M" + Guid.NewGuid().ToString("N")[..3].ToUpperInvariant() };
        db.Categories.Add(category);
        var product = await db.Products.SingleAsync(x => x.Id == seed.ProductId);
        product.CategoryId = category.Id; product.TrackingMode = mode; product.SerialTrackingEnabled = mode == TrackingMode.Serialized;
        (await db.ProductUnits.SingleAsync(x => x.Id == seed.ProductUnitId)).FactorToBaseUnit = factor;
        await db.SaveChangesAsync();
        var opening = await services.GetRequiredService<CreateStockAdjustmentHandler>().HandleAsync(
            new(StockAdjustmentMode.Delta, StockAdjustmentReason.OpeningStock,
                [new(seed.ProductId, mode == TrackingMode.Container ? seed.ProductUnitId : null,
                    StockAdjustmentDirection.Increase, InventoryBucket.Sellable, 2m * factor, cost / factor,
                    seed.SupplierId, [new("P5-Z1-" + Guid.NewGuid()), new("P5-Z2-" + Guid.NewGuid())])],
                seed.ActorId, Guid.NewGuid()), default);
        Assert.True(opening.IsSuccess, opening.Error?.Message);
        db.ChangeTracker.Clear();
        var original = await db.InventoryUnits.AsNoTracking().Where(x => x.ProductId == seed.ProductId).OrderBy(x => x.Id).ToArrayAsync();
        var operation = Guid.NewGuid();
        if (stocktake)
        {
            var created = await services.GetRequiredService<CreateStocktakeHandler>().HandleAsync(new(StocktakeScope.Category, category.Id, seed.ActorId, "Missing precision", Guid.NewGuid()), default);
            Assert.True(created.IsSuccess, created.Error?.Message);
            Assert.True((await services.GetRequiredService<StartStocktakeHandler>().HandleAsync(new(created.Value, seed.ActorId, Guid.NewGuid()), default)).IsSuccess);
            Assert.True((await services.GetRequiredService<RecordSerializedStocktakeHandler>().HandleAsync(new(created.Value, seed.ProductId, [], [], seed.ActorId, "Missing observation", Guid.NewGuid()), default)).IsSuccess);
            db.ChangeTracker.Clear();
            Assert.All(await db.InventoryUnits.Where(x => x.ProductId == seed.ProductId).ToArrayAsync(), x => Assert.Equal(InventoryUnitStatus.InStock, x.Status));
            Assert.Equal(2m * factor, (await db.StockBalances.SingleAsync(x => x.ProductId == seed.ProductId)).SellableQty);
            Assert.Empty(await db.InventoryMovementUnits.Where(x => original.Select(u => u.Id).ToArray().Contains(x.InventoryUnitId) && x.ToStatus == InventoryUnitStatus.Missing).ToArrayAsync());
            Assert.True((await services.GetRequiredService<CancelStocktakeHandler>().HandleAsync(new(created.Value, seed.ActorId, Guid.NewGuid()), default)).IsSuccess);
            Assert.False((await services.GetRequiredService<PostStocktakeHandler>().HandleAsync(new(created.Value, seed.ActorId, ClientOperationId: Guid.NewGuid()), default)).IsSuccess);
            db.ChangeTracker.Clear();
            Assert.All(await db.InventoryUnits.Where(x => x.ProductId == seed.ProductId).ToArrayAsync(), x => Assert.Equal(InventoryUnitStatus.InStock, x.Status));
            Assert.Equal(cost * 2m, (await db.ProductCostStates.SingleAsync(x => x.ProductId == seed.ProductId)).TotalInventoryCost);
            created = await services.GetRequiredService<CreateStocktakeHandler>().HandleAsync(new(StocktakeScope.Category, category.Id, seed.ActorId, "Approved missing", Guid.NewGuid()), default);
            Assert.True(created.IsSuccess, created.Error?.Message);
            Assert.True((await services.GetRequiredService<StartStocktakeHandler>().HandleAsync(new(created.Value, seed.ActorId, Guid.NewGuid()), default)).IsSuccess);
            Assert.True((await services.GetRequiredService<RecordSerializedStocktakeHandler>().HandleAsync(new(created.Value, seed.ProductId, [], [], seed.ActorId, "Missing", Guid.NewGuid()), default)).IsSuccess);
            Assert.True((await services.GetRequiredService<ReviewStocktakeHandler>().HandleAsync(new(created.Value, seed.ActorId, Guid.NewGuid()), default)).IsSuccess);
            var beforePosting = await StateAsync(provider, seed.ProductId, seed.SupplierId, seed.ActorId);
            var failedOperation = Guid.NewGuid();
            var flushing = new AdjustmentFlushThenThrow(db);
            var failingPost = new PostStocktakeHandler(services.GetRequiredService<ICatalogRepository>(),
                services.GetRequiredService<IInventoryRepository>(), services.GetRequiredService<IInventoryCostAllocator>(),
                services.GetRequiredService<ITransactionRunner>(), flushing, services.GetRequiredService<IClock>(),
                services.GetRequiredService<IOperationLock>(), services.GetRequiredService<IOperationOutcomeLedger>());
            await Assert.ThrowsAsync<InvalidOperationException>(() => failingPost.HandleAsync(new(created.Value, seed.ActorId, ClientOperationId: failedOperation), default));
            Assert.True(flushing.Flushed);
            Assert.Equal(beforePosting, await StateAsync(provider, seed.ProductId, seed.SupplierId, seed.ActorId));
            db.ChangeTracker.Clear();
            Assert.Equal(StocktakeStatus.Review, (await db.Stocktakes.SingleAsync(x => x.Id == created.Value)).Status);
            Assert.False(await db.OperationOutcomes.AnyAsync(x => x.ClientOperationId == failedOperation));
            var command = new PostStocktakeCommand(created.Value, seed.ActorId, ClientOperationId: operation);
            var posted = await services.GetRequiredService<PostStocktakeHandler>().HandleAsync(command, default);
            Assert.True(posted.IsSuccess, posted.Error?.Message);
            Assert.True((await services.GetRequiredService<PostStocktakeHandler>().HandleAsync(command, default)).IsSuccess);
        }
        else
        {
            var command = new CreateStockAdjustmentCommand(StockAdjustmentMode.Delta, StockAdjustmentReason.Lost,
                [new(seed.ProductId, null, StockAdjustmentDirection.Decrease, InventoryBucket.Sellable, 2m, 9999m, InventoryUnitIds: original.Select(x => x.Id).Reverse().ToArray())], seed.ActorId, operation);
            var posted = await services.GetRequiredService<CreateStockAdjustmentHandler>().HandleAsync(command, default);
            Assert.True(posted.IsSuccess, posted.Error?.Message);
            Assert.True((await services.GetRequiredService<CreateStockAdjustmentHandler>().HandleAsync(command, default)).IsSuccess);
        }
        db.ChangeTracker.Clear();
        var movements = await db.InventoryMovements.Where(x => x.CorrelationId == operation).ToArrayAsync();
        Assert.Equal(2, movements.Length);
        Assert.Equal(decimal.Round(cost * 2m, 2, MidpointRounding.AwayFromZero), movements.Sum(x => x.RecognizedLossAmount));
        var allocations = new List<decimal>();
        foreach (var prior in original)
        {
            var link = await db.InventoryMovementUnits.SingleAsync(x => x.InventoryUnitId == prior.Id && x.ToStatus == InventoryUnitStatus.Missing);
            var movement = movements.Single(x => x.Id == link.MovementId);
            var consumption = await db.InventoryLotConsumptions.SingleAsync(x => x.MovementId == movement.Id);
            Assert.Equal(cost, consumption.TotalCostSnapshot); Assert.Equal((decimal)factor, consumption.Quantity);
            Assert.Single(await db.InventoryMovementEffects.Where(x => x.MovementId == movement.Id && x.QuantityDelta == -factor).ToArrayAsync());
            var current = await db.InventoryUnits.SingleAsync(x => x.Id == prior.Id);
            Assert.Equal(prior.TrackingCode, current.TrackingCode); Assert.Equal(prior.AcquisitionCost, current.AcquisitionCost);
            Assert.Equal(prior.InventoryLotId, current.InventoryLotId); Assert.Equal(prior.SupplierProductId, current.SupplierProductId);
            allocations.Add(movement.RecognizedLossAmount);
        }
        if (cost == 0.004m)
        {
            Assert.Equal(new decimal[] { 0.01m, 0m }, allocations);
        }
        var pool = await db.ProductCostStates.SingleAsync(x => x.ProductId == seed.ProductId);
        Assert.Equal(0m, pool.CostedQty); Assert.Equal(0m, pool.TotalInventoryCost);
        var balance = await db.StockBalances.SingleAsync(x => x.ProductId == seed.ProductId);
        Assert.Equal(0m, balance.SellableQty); Assert.Equal(0m, balance.ScrapQty);
    }

    [Theory]
    [InlineData(TrackingMode.Serialized, 1)]
    [InlineData(TrackingMode.IndividualPiece, 1)]
    [InlineData(TrackingMode.Container, 90)]
    public async Task MissingSource_LostPersistsOneActualEconomicSourcePerIdentity(TrackingMode mode, int factor)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        Guid productId, actorId, supplierId, productUnitId;
        InventoryUnit[] original;
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
            var seed = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db, 300m);
            productId = seed.ProductId; actorId = seed.ActorId; supplierId = seed.SupplierId; productUnitId = seed.ProductUnitId;
            var product = await db.Products.SingleAsync(x => x.Id == productId);
            product.TrackingMode = mode; product.SerialTrackingEnabled = mode == TrackingMode.Serialized;
            (await db.ProductUnits.SingleAsync(x => x.Id == productUnitId)).FactorToBaseUnit = factor;
            await db.SaveChangesAsync();
            var purchase = await scope.ServiceProvider.GetRequiredService<CreatePurchaseHandler>().HandleAsync(
                new(supplierId, "P5-MISSING-" + Guid.NewGuid(), DateOnly.FromDateTime(DateTime.Today), null, 0m,
                    PurchaseSettlementMode.External, actorId, Guid.NewGuid(),
                    [new(productId, productUnitId, 2m, 100m, 300m,
                        [new SerializedIdentityInput(mode == TrackingMode.Serialized ? "P5-M1-" + Guid.NewGuid() : null),
                         new SerializedIdentityInput(mode == TrackingMode.Serialized ? "P5-M2-" + Guid.NewGuid() : null)])],
                    InitialPaymentAmount: 0m), default);
            Assert.True(purchase.IsSuccess, purchase.Error?.Message);
            original = await db.InventoryUnits.AsNoTracking().Where(x => x.ProductId == productId).OrderBy(x => x.Id).ToArrayAsync();
        }
        var operation = Guid.NewGuid();
        var command = new CreateStockAdjustmentCommand(StockAdjustmentMode.Delta, StockAdjustmentReason.Lost,
            [new(productId, mode == TrackingMode.Container ? productUnitId : null, StockAdjustmentDirection.Decrease,
                InventoryBucket.Sellable, 2m * factor, 9999m, InventoryUnitIds: original.Select(x => x.Id).Reverse().ToArray())], actorId, operation);
        var before = await StateAsync(provider, productId, supplierId, actorId);
        await using (var refused = provider.CreateAsyncScope())
        {
            var handler = refused.ServiceProvider.GetRequiredService<CreateStockAdjustmentHandler>();
            Assert.False((await handler.HandleAsync(command with { ActorId = Guid.NewGuid(), CorrelationId = Guid.NewGuid() }, default)).IsSuccess);
            var ambiguous = await handler.HandleAsync(command with { Reason = StockAdjustmentReason.OpeningStock, CorrelationId = Guid.NewGuid() }, default);
            Assert.False(ambiguous.IsSuccess); Assert.Equal("inventory.exact_disposition_required", ambiguous.Error?.Code);
        }
        Assert.Equal(before, await StateAsync(provider, productId, supplierId, actorId));
        var failedOperation = Guid.NewGuid();
        await using (var failure = provider.CreateAsyncScope())
        {
            var services = failure.ServiceProvider;
            var flushing = new AdjustmentFlushThenThrow(services.GetRequiredService<EdgeRetailsDbContext>());
            var handler = new CreateStockAdjustmentHandler(
                services.GetRequiredService<ICatalogRepository>(), services.GetRequiredService<IInventoryRepository>(),
                services.GetRequiredService<IInventoryCostAllocator>(), services.GetRequiredService<IPartyRepository>(),
                services.GetRequiredService<ITraceabilityRepository>(), services.GetRequiredService<IResourceLock>(),
                services.GetRequiredService<IBusinessAuditWriter>(), services.GetRequiredService<IClock>(),
                services.GetRequiredService<ITransactionRunner>(), services.GetRequiredService<IApplicationPermissionAuthorizer>(),
                flushing, services.GetRequiredService<IOperationOutcomeLedger>(),
                services.GetRequiredService<IPhysicalUnitCreationAuthority>(), services.GetRequiredService<IOperationLock>());
            await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(command with { CorrelationId = failedOperation }, default));
            Assert.True(flushing.Flushed);
        }
        Assert.Equal(before, await StateAsync(provider, productId, supplierId, actorId));
        await using (var check = provider.CreateAsyncScope())
        {
            Assert.False(await check.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>().OperationOutcomes.AnyAsync(x => x.ClientOperationId == failedOperation));
        }
        await using (var scope = provider.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<CreateStockAdjustmentHandler>().HandleAsync(command, default);
            Assert.True(result.IsSuccess, result.Error?.Message);
            var replay = await scope.ServiceProvider.GetRequiredService<CreateStockAdjustmentHandler>().HandleAsync(command, default);
            Assert.True(replay.IsSuccess, replay.Error?.Message);
            Assert.Equal(result.Value, replay.Value);
            var conflict = await scope.ServiceProvider.GetRequiredService<CreateStockAdjustmentHandler>().HandleAsync(command with { Note = "Changed payload" }, default);
            Assert.False(conflict.IsSuccess); Assert.Equal("idempotency.payload_mismatch", conflict.Error?.Code);
        }
        await using var read = provider.CreateAsyncScope();
        var stored = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var movements = await stored.InventoryMovements.Where(x => x.CorrelationId == operation).ToArrayAsync();
        Assert.Equal(2, movements.Length);
        Assert.Equal(200m, movements.Sum(x => x.RecognizedLossAmount));
        foreach (var movement in movements)
        {
            var link = await stored.InventoryMovementUnits.SingleAsync(x => x.MovementId == movement.Id);
            Assert.Equal(12, (int)link.ToStatus);
            Assert.Equal(InventoryUnitStatus.InStock, link.FromStatus);
            var unit = await stored.InventoryUnits.SingleAsync(x => x.Id == link.InventoryUnitId);
            var prior = original.Single(x => x.Id == unit.Id);
            Assert.Equal(12, (int)unit.Status);
            Assert.Equal(prior.TrackingCode, unit.TrackingCode); Assert.Equal(prior.ItemSequence, unit.ItemSequence);
            Assert.Equal(prior.SupplierProductId, unit.SupplierProductId); Assert.Equal(prior.SerialNumber, unit.SerialNumber);
            Assert.Equal(prior.AcquisitionCost, unit.AcquisitionCost); Assert.Equal(prior.SourcePurchaseItemId, unit.SourcePurchaseItemId);
            var consumed = await stored.InventoryLotConsumptions.SingleAsync(x => x.MovementId == movement.Id);
            Assert.Equal(prior.InventoryLotId, consumed.LotId);
            Assert.Equal((decimal)factor, consumed.Quantity);
            Assert.Equal(100m, consumed.TotalCostSnapshot);
            Assert.Equal(100m, movement.RecognizedLossAmount);
            var effect = await stored.InventoryMovementEffects.SingleAsync(x => x.MovementId == movement.Id);
            Assert.Equal(-(decimal)factor, effect.QuantityDelta);
            Assert.Equal(effect.QuantityBefore - factor, effect.QuantityAfter);
            Assert.Equal("STOCK_ADJUSTMENT", movement.ReferenceType);
            Assert.True(await stored.StockAdjustmentItems.AnyAsync(x => x.Id == movement.ReferenceId && x.ProductId == productId));
        }
        var balance = await stored.StockBalances.SingleAsync(x => x.ProductId == productId);
        Assert.Equal(0m, balance.SellableQty); Assert.Equal(0m, balance.ScrapQty);
        var pool = await stored.ProductCostStates.SingleAsync(x => x.ProductId == productId);
        Assert.Equal(0m, pool.CostedQty); Assert.Equal(0m, pool.TotalInventoryCost);
        Assert.Equal(2, await stored.InventoryUnits.CountAsync(x => x.ProductId == productId));
        Assert.Equal(3L, (await stored.SupplierProducts.SingleAsync(x => x.SupplierId == supplierId && x.ProductId == productId)).NextItemSequence);
        Assert.Empty(await stored.CashMovements.Where(x => x.ActorId == actorId).ToArrayAsync());
        Assert.Empty(await stored.SupplierRefunds.Where(x => x.SupplierId == supplierId).ToArrayAsync());
        Assert.Equal(200m, await read.ServiceProvider.GetRequiredService<ISupplierAccountRepository>().GetCurrentBalanceAsync(supplierId, default));
    }

    [Theory]
    [InlineData("lost", HistoricalMissingClassification.CONCLUSIVE)]
    [InlineData("other", HistoricalMissingClassification.AMBIGUOUS)]
    [InlineData("retained_scrap", HistoricalMissingClassification.NOT_MISSING)]
    [InlineData("zero", HistoricalMissingClassification.CONCLUSIVE)]
    [InlineData("posted_stocktake", HistoricalMissingClassification.CONCLUSIVE)]
    [InlineData("unposted_stocktake", HistoricalMissingClassification.AMBIGUOUS)]
    [InlineData("grouped", HistoricalMissingClassification.AMBIGUOUS)]
    [InlineData("later_transition", HistoricalMissingClassification.AMBIGUOUS)]
    public async Task HistoricalClassifier_ReadsRealMixedHistoryWithoutWritingOrDerecognizingAgain(
        string history, HistoricalMissingClassification expected)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        Guid unitId, productId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var db = services.GetRequiredService<EdgeRetailsDbContext>();
            await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
            var fixture = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db, 300m);
            productId = fixture.ProductId;
            if (history == "zero")
            {
                var opening = await services.GetRequiredService<CreateStockAdjustmentHandler>().HandleAsync(
                    new(StockAdjustmentMode.Delta, StockAdjustmentReason.OpeningStock,
                        [new(productId, null, StockAdjustmentDirection.Increase, InventoryBucket.Sellable, 1m, 0m,
                            fixture.SupplierId, [new("P5-HISTORY-FREE-" + Guid.NewGuid())])], fixture.ActorId, Guid.NewGuid()), default);
                Assert.True(opening.IsSuccess, opening.Error?.Message);
            }
            else
            {
                var identities = new List<SerializedIdentityInput> { new("P5-HISTORY-" + Guid.NewGuid()) };
                if (history == "grouped") { identities.Add(new("P5-HISTORY-OTHER-" + Guid.NewGuid())); }
                var purchase = await services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(
                new(fixture.SupplierId, "P5-HISTORY-" + Guid.NewGuid(), DateOnly.FromDateTime(DateTime.Today), null,
                    0m, PurchaseSettlementMode.External, fixture.ActorId, Guid.NewGuid(),
                    [new(fixture.ProductId, fixture.ProductUnitId, identities.Count, 100m, 300m, identities)], InitialPaymentAmount: 0m), default);
                Assert.True(purchase.IsSuccess, purchase.Error?.Message);
            }
            unitId = (await db.InventoryUnits.OrderBy(x => x.Id).FirstAsync(x => x.ProductId == productId)).Id;
            if (history == "retained_scrap")
            {
                var disposition = await services.GetRequiredService<ITransactionRunner>().ExecuteAsync(async ct =>
                {
                    var damaged = await services.GetRequiredService<IInventoryConditionService>().TransferAsync(
                        new(productId, InventoryBucket.Sellable, InventoryBucket.Damaged, 1m, fixture.ActorId,
                            "Recoverable physical damage", InventoryUnitIds: [unitId]), ct);
                    if (!damaged.IsSuccess) { return damaged; }
                    await services.GetRequiredService<IUnitOfWork>().SaveChangesAsync(ct);
                    var transfer = await services.GetRequiredService<IInventoryConditionService>().TransferAsync(
                        new(productId, InventoryBucket.Damaged, InventoryBucket.Scrap, 1m, fixture.ActorId,
                            "Actual retained physical Scrap", InventoryUnitIds: [unitId]), ct);
                    if (transfer.IsSuccess) { await services.GetRequiredService<IUnitOfWork>().SaveChangesAsync(ct); }
                    return transfer;
                }, default);
                Assert.True(disposition.IsSuccess, disposition.Error?.Message);
            }
            else
            {
                if (history is "posted_stocktake" or "unposted_stocktake")
                {
                    var category = new Category { Name = "P5-legacy-count-" + Guid.NewGuid(), IdentitySymbol = "L" + Guid.NewGuid().ToString("N")[..3].ToUpperInvariant() };
                    db.Categories.Add(category);
                    (await db.Products.SingleAsync(x => x.Id == productId)).CategoryId = category.Id;
                    await db.SaveChangesAsync();
                    var created = await services.GetRequiredService<CreateStocktakeHandler>().HandleAsync(new(StocktakeScope.Category,
                        category.Id, fixture.ActorId, "Legacy exact absence fixture", Guid.NewGuid()), default);
                    Assert.True(created.IsSuccess, created.Error?.Message);
                    try
                    {
                        Assert.True((await services.GetRequiredService<StartStocktakeHandler>().HandleAsync(new(created.Value, fixture.ActorId, Guid.NewGuid()), default)).IsSuccess);
                        Assert.True((await services.GetRequiredService<RecordSerializedStocktakeHandler>().HandleAsync(new(created.Value,
                            productId, [], [], fixture.ActorId, "Absent identity", Guid.NewGuid()), default)).IsSuccess);
                        Assert.True((await services.GetRequiredService<ReviewStocktakeHandler>().HandleAsync(new(created.Value, fixture.ActorId, Guid.NewGuid()), default)).IsSuccess);
                        Assert.True((await services.GetRequiredService<PostStocktakeHandler>().HandleAsync(new(created.Value, fixture.ActorId,
                            ClientOperationId: Guid.NewGuid()), default)).IsSuccess);
                        if (history == "unposted_stocktake")
                        {
                            // Deliberately contradictory legacy approval only in this owned fixture.
                            var document = await db.Stocktakes.SingleAsync(x => x.Id == created.Value);
                            document.Status = StocktakeStatus.Cancelled; document.PostedAt = null;
                            await db.SaveChangesAsync();
                        }
                    }
                    finally
                    {
                        var status = (await db.Stocktakes.SingleAsync(x => x.Id == created.Value)).Status;
                        if (status is StocktakeStatus.Draft or StocktakeStatus.Counting or StocktakeStatus.Review)
                        {
                            Assert.True((await services.GetRequiredService<CancelStocktakeHandler>().HandleAsync(new(created.Value, fixture.ActorId, Guid.NewGuid()), default)).IsSuccess);
                        }
                    }
                }
                else
                {
                    var removal = await services.GetRequiredService<CreateStockAdjustmentHandler>().HandleAsync(
                    new(StockAdjustmentMode.Delta, StockAdjustmentReason.Lost,
                        [new(productId, null, StockAdjustmentDirection.Decrease, InventoryBucket.Sellable, 1m, null,
                            InventoryUnitIds: [unitId])], fixture.ActorId, Guid.NewGuid()), default);
                    Assert.True(removal.IsSuccess, removal.Error?.Message);
                    if (history == "other")
                    {
                        (await db.StockAdjustments.SingleAsync(x => x.Id == removal.Value)).Reason = StockAdjustmentReason.Other;
                    }
                }
                // HARNESS_CORRECTION: reproduce immutable pre-Missing12 history
                // only in this owned fixture, rather than invoking a legacy writer.
                (await db.InventoryUnits.SingleAsync(x => x.Id == unitId)).Status = InventoryUnitStatus.Scrapped;
                var shortageLink = await db.InventoryMovementUnits.SingleAsync(x => x.InventoryUnitId == unitId && x.ToStatus == InventoryUnitStatus.Missing);
                shortageLink.ToStatus = InventoryUnitStatus.Scrapped;
                if (history == "grouped")
                {
                    var otherUnit = await db.InventoryUnits.SingleAsync(x => x.ProductId == productId && x.Id != unitId);
                    db.InventoryMovementUnits.Add(new() { MovementId = shortageLink.MovementId, InventoryUnitId = otherUnit.Id,
                        FromStatus = InventoryUnitStatus.InStock, ToStatus = InventoryUnitStatus.Scrapped });
                }
                if (history == "later_transition")
                {
                    var source = await db.InventoryMovements.SingleAsync(x => x.Id == shortageLink.MovementId);
                    var later = new InventoryMovement { ProductId = productId, MovementType = InventoryMovementType.StockAdjustment,
                        ActorId = fixture.ActorId, CorrelationId = Guid.NewGuid(), OccurredAt = source.OccurredAt.AddSeconds(1), Reason = "Contradictory legacy transition" };
                    db.InventoryMovements.Add(later);
                    db.InventoryMovementUnits.Add(new() { MovementId = later.Id, InventoryUnitId = unitId,
                        FromStatus = InventoryUnitStatus.Scrapped, ToStatus = InventoryUnitStatus.InStock });
                }
                await db.SaveChangesAsync();
            }
        }
        var evidence = await HistoricalEvidenceAsync(provider, productId, unitId);
        var before = System.Text.Json.JsonSerializer.Serialize(evidence);
        var classifier = new HistoricalMissingClassifier();
        var classified = classifier.Classify(evidence);
        Assert.Equal(expected, classified.Classification);
        Assert.Equal(classified, classifier.Classify(evidence));
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(await HistoricalEvidenceAsync(provider, productId, unitId)));
        Assert.Equal(evidence.Unit.TrackingCode, (await HistoricalEvidenceAsync(provider, productId, unitId)).Unit.TrackingCode);
    }

    private static async Task<HistoricalMissingEvidence> HistoricalEvidenceAsync(ServiceProvider provider, Guid productId, Guid unitId)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var unit = await db.InventoryUnits.AsNoTracking().SingleAsync(x => x.Id == unitId);
        var movements = await db.InventoryMovements.AsNoTracking().Where(x => x.ProductId == productId).OrderBy(x => x.Id).ToArrayAsync();
        var ids = movements.Select(x => x.Id).ToArray();
        var items = await db.StockAdjustmentItems.AsNoTracking().Where(x => x.ProductId == productId).OrderBy(x => x.Id).ToArrayAsync();
        var adjustmentIds = items.Select(x => x.StockAdjustmentId).ToArray();
        return new(unit, movements,
            await db.InventoryMovementUnits.AsNoTracking().Where(x => ids.Contains(x.MovementId)).OrderBy(x => x.Id).ToArrayAsync(),
            await db.InventoryMovementEffects.AsNoTracking().Where(x => ids.Contains(x.MovementId)).OrderBy(x => x.Id).ToArrayAsync(),
            await db.InventoryLotConsumptions.AsNoTracking().Where(x => ids.Contains(x.MovementId)).OrderBy(x => x.Id).ToArrayAsync(),
            await db.StockAdjustments.AsNoTracking().Where(x => adjustmentIds.Contains(x.Id)).OrderBy(x => x.Id).ToArrayAsync(),
            items,
            await db.Stocktakes.AsNoTracking().Where(x => db.StocktakeItems.Any(i => i.StocktakeId == x.Id && i.ProductId == productId)).OrderBy(x => x.Id).ToArrayAsync(),
            await db.StocktakeItems.AsNoTracking().Where(x => x.ProductId == productId).OrderBy(x => x.Id).ToArrayAsync(),
            await db.StocktakeUnitChecks.AsNoTracking().Where(x => db.StocktakeItems.Any(i => i.Id == x.StocktakeItemId && i.ProductId == productId)).OrderBy(x => x.Id).ToArrayAsync());
    }

    [Theory]
    [InlineData(StockAdjustmentReason.Damaged, StockAdjustmentDirection.Decrease, InventoryBucket.Sellable, 150, null, "inventory.condition_transfer_required")]
    [InlineData(StockAdjustmentReason.Damaged, StockAdjustmentDirection.Increase, InventoryBucket.Sellable, 150, null, "inventory.condition_transfer_required")]
    [InlineData(StockAdjustmentReason.OpeningStock, StockAdjustmentDirection.Increase, InventoryBucket.Sellable, null, null, "inventory.cost_basis_required")]
    [InlineData(StockAdjustmentReason.Other, StockAdjustmentDirection.Increase, InventoryBucket.Sellable, null, null, "inventory.cost_basis_required")]
    [InlineData(StockAdjustmentReason.OpeningStock, StockAdjustmentDirection.Increase, InventoryBucket.Scrap, 100, null, "inventory.scrap_zero_carrying_required")]
    [InlineData(StockAdjustmentReason.OpeningStock, StockAdjustmentDirection.Increase, InventoryBucket.Scrap, null, 100, "inventory.scrap_zero_carrying_required")]
    public async Task AdjustmentGuards_RejectWithoutBusinessMutation(StockAdjustmentReason reason,
        StockAdjustmentDirection direction, InventoryBucket bucket, int? cost, int? referenceCost, string expectedCode)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var fixture = await SeedAsync(provider);
        await SetReferenceCostAsync(provider, fixture.ProductId, referenceCost);
        var before = await StateAsync(provider, fixture.ProductId, fixture.SupplierId, fixture.ActorId);
        await using (var scope = provider.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<CreateStockAdjustmentHandler>().HandleAsync(
                new(StockAdjustmentMode.Delta, reason,
                    [new(fixture.ProductId, null, direction, bucket, 1m, cost)], fixture.ActorId, Guid.NewGuid()), default);
            Assert.Equal(expectedCode, result.Error?.Code);
        }
        Assert.Equal(before, await StateAsync(provider, fixture.ProductId, fixture.SupplierId, fixture.ActorId));
    }

    [Theory]
    [InlineData(0, null, InventoryBucket.Sellable)]
    [InlineData(null, 0, InventoryBucket.Sellable)]
    [InlineData(null, 20, InventoryBucket.Sellable)]
    [InlineData(0, null, InventoryBucket.Scrap)]
    public async Task PositiveAdjustment_PersistsProvenCostOrExplicitFreeWithoutInventedFinancialFacts(
        int? cost, int? referenceCost, InventoryBucket bucket)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var fixture = await SeedAsync(provider);
        await SetReferenceCostAsync(provider, fixture.ProductId, referenceCost);
        var operation = Guid.NewGuid();
        await using (var scope = provider.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<CreateStockAdjustmentHandler>().HandleAsync(
                new(StockAdjustmentMode.Delta, StockAdjustmentReason.OpeningStock,
                    [new(fixture.ProductId, null, StockAdjustmentDirection.Increase, bucket, 1m, cost)], fixture.ActorId, operation), default);
            Assert.True(result.IsSuccess, result.Error?.Message);
        }
        await using var read = provider.CreateAsyncScope();
        var db = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var actualCost = cost ?? referenceCost!.Value;
        var movement = await db.InventoryMovements.SingleAsync(x => x.CorrelationId == operation);
        var lot = await db.InventoryLots.SingleAsync(x => x.SourceMovementId == movement.Id);
        Assert.Equal((decimal)actualCost, lot.OriginalUnitCost);
        Assert.Equal((decimal)actualCost, lot.EffectiveUnitCost);
        Assert.Null(lot.PurchaseItemId);
        Assert.Equal(1m, lot.ReceivedQuantity);
        Assert.Equal(1m, (await db.InventoryLotBucketBalances.SingleAsync(x => x.LotId == lot.Id && x.StockBucket == bucket)).Quantity);
        var pool = await db.ProductCostStates.SingleAsync(x => x.ProductId == fixture.ProductId);
        Assert.Equal(bucket == InventoryBucket.Scrap ? 20m : 21m, pool.CostedQty);
        Assert.Equal(3000m + actualCost, pool.TotalInventoryCost);
        var balance = await db.StockBalances.SingleAsync(x => x.ProductId == fixture.ProductId);
        Assert.Equal(bucket == InventoryBucket.Scrap ? 1m : 0m, balance.ScrapQty);
        Assert.Equal(bucket == InventoryBucket.Sellable ? 21m : 20m, balance.SellableQty);
        Assert.Equal(0m, movement.RecognizedLossAmount);
        Assert.Equal(3000m, await read.ServiceProvider.GetRequiredService<ISupplierAccountRepository>().GetCurrentBalanceAsync(fixture.SupplierId, default));
        Assert.Equal(2, await db.Purchases.CountAsync(x => x.SupplierId == fixture.SupplierId));
        Assert.Empty(await db.SupplierPayments.Where(x => x.SupplierId == fixture.SupplierId).ToListAsync());
        Assert.Empty(await db.SupplierRefunds.Where(x => x.SupplierId == fixture.SupplierId).ToListAsync());
        Assert.Empty(await db.CashMovements.Where(x => x.ActorId == fixture.ActorId).ToListAsync());
        Assert.Single(await db.BusinessAuditEvents.Where(x => x.CorrelationId == operation).ToListAsync());
    }

    [Fact]
    public async Task NeutralDamagedTransfer_RetainsQuantityValueAndRecordsNoLoss()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var fixture = await SeedAsync(provider);
        var before = await ProfitAsync(provider);
        Guid movementId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var result = await services.GetRequiredService<ITransactionRunner>().ExecuteAsync(async ct =>
            {
                var transfer = await services.GetRequiredService<IInventoryConditionService>().TransferAsync(
                    new(fixture.ProductId, InventoryBucket.Sellable, InventoryBucket.Damaged, 6m, fixture.ActorId,
                        "Recoverable damage condition"), ct);
                if (transfer.IsSuccess) { await services.GetRequiredService<IUnitOfWork>().SaveChangesAsync(ct); }
                return transfer;
            }, default);
            Assert.True(result.IsSuccess, result.Error?.Message);
            movementId = result.Value;
        }
        await using var read = provider.CreateAsyncScope();
        var db = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var balance = await db.StockBalances.SingleAsync(x => x.ProductId == fixture.ProductId);
        Assert.Equal(14m, balance.SellableQty);
        Assert.Equal(6m, balance.DamagedQty);
        var pool = await db.ProductCostStates.SingleAsync(x => x.ProductId == fixture.ProductId);
        Assert.Equal(20m, pool.CostedQty);
        Assert.Equal(3000m, pool.TotalInventoryCost);
        Assert.Equal(0m, (await db.InventoryMovements.SingleAsync(x => x.Id == movementId)).RecognizedLossAmount);
        var effects = await db.InventoryMovementEffects.Where(x => x.MovementId == movementId).ToListAsync();
        Assert.Equal(2, effects.Count);
        Assert.Equal(0m, effects.Sum(x => x.QuantityDelta));
        Assert.Equal(6m, await (from b in db.InventoryLotBucketBalances join l in db.InventoryLots on b.LotId equals l.Id
            where l.ProductId == fixture.ProductId && b.StockBucket == InventoryBucket.Damaged select b.Quantity).SumAsync());
        Assert.Equal(before.NetProfit, (await ProfitAsync(provider)).NetProfit);
        Assert.Empty(await db.CashMovements.Where(x => x.ActorId == fixture.ActorId).ToListAsync());
    }

    private static async Task SetReferenceCostAsync(ServiceProvider provider, Guid productId, int? reference)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        (await db.Products.SingleAsync(x => x.Id == productId)).ReferencePurchaseCost = reference;
        await db.SaveChangesAsync();
    }

    private static async Task<string> StateAsync(ServiceProvider provider, Guid productId, Guid supplierId, Guid actorId)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            Balance = await db.StockBalances.AsNoTracking().SingleAsync(x => x.ProductId == productId),
            Units = await db.InventoryUnits.AsNoTracking().Where(x => x.ProductId == productId).OrderBy(x => x.Id).ToArrayAsync(),
            Links = await (from link in db.InventoryMovementUnits join movement in db.InventoryMovements on link.MovementId equals movement.Id
                where movement.ProductId == productId orderby link.Id select link).AsNoTracking().ToArrayAsync(),
            Effects = await (from effect in db.InventoryMovementEffects join movement in db.InventoryMovements on effect.MovementId equals movement.Id
                where movement.ProductId == productId orderby effect.Id select effect).AsNoTracking().ToArrayAsync(),
            Consumptions = await (from consumption in db.InventoryLotConsumptions join movement in db.InventoryMovements on consumption.MovementId equals movement.Id
                where movement.ProductId == productId orderby consumption.Id select consumption).AsNoTracking().ToArrayAsync(),
            Pool = await db.ProductCostStates.AsNoTracking().SingleAsync(x => x.ProductId == productId),
            Lots = await db.InventoryLots.AsNoTracking().Where(x => x.ProductId == productId).OrderBy(x => x.Id).ToArrayAsync(),
            Buckets = await (from b in db.InventoryLotBucketBalances join l in db.InventoryLots on b.LotId equals l.Id
                where l.ProductId == productId orderby b.Id select b).AsNoTracking().ToArrayAsync(),
            Movements = await db.InventoryMovements.AsNoTracking().Where(x => x.ProductId == productId).OrderBy(x => x.Id).ToArrayAsync(),
            Adjustments = await db.StockAdjustments.AsNoTracking().Where(x => x.ActorId == actorId).OrderBy(x => x.Id).ToArrayAsync(),
            Audit = await db.BusinessAuditEvents.AsNoTracking().Where(x => x.ActorId == actorId).OrderBy(x => x.Id).ToArrayAsync(),
            Supplier = await db.SupplierAccountEntries.AsNoTracking().Where(x => x.SupplierId == supplierId).OrderBy(x => x.Id).ToArrayAsync(),
            Cash = await db.CashMovements.AsNoTracking().Where(x => x.ActorId == actorId).OrderBy(x => x.Id).ToArrayAsync()
        });
    }

    [Fact]
    public async Task NegativeAdjustment_ExplicitFreeStockRecognizesZeroLoss()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var fixture = await SeedAsync(provider, [0m, 0m]);
        var operation = Guid.NewGuid();
        await using (var scope = provider.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<CreateStockAdjustmentHandler>().HandleAsync(
                new(StockAdjustmentMode.Delta, StockAdjustmentReason.Lost,
                    [new(fixture.ProductId, null, StockAdjustmentDirection.Decrease, InventoryBucket.Sellable, 6m, 9999m)],
                    fixture.ActorId, operation), default);
            Assert.True(result.IsSuccess, result.Error?.Message);
        }
        await using var read = provider.CreateAsyncScope();
        var db = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var movement = await db.InventoryMovements.SingleAsync(x => x.CorrelationId == operation);
        Assert.Equal(0m, movement.RecognizedLossAmount);
        Assert.Equal(0m, movement.UnitCostSnapshot);
        var item = await db.StockAdjustmentItems.SingleAsync(x => x.Id == movement.ReferenceId);
        Assert.Equal(0m, item.TotalCostSnapshot);
        Assert.Equal(0m, item.UnitCostSnapshot);
        var pool = await db.ProductCostStates.SingleAsync(x => x.ProductId == fixture.ProductId);
        Assert.Equal(14m, pool.CostedQty);
        Assert.Equal(0m, pool.TotalInventoryCost);
        Assert.Equal(14m, (await db.StockBalances.SingleAsync(x => x.ProductId == fixture.ProductId)).SellableQty);
    }

    [Fact]
    public async Task NegativeAdjustment_RealSqlFlushFailureRollsBackLossQuantityValueAndReplayFact()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var fixture = await SeedAsync(provider);
        var operation = Guid.NewGuid();
        await using (var scope = provider.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var flushing = new AdjustmentFlushThenThrow(services.GetRequiredService<EdgeRetailsDbContext>());
            var handler = new CreateStockAdjustmentHandler(
                services.GetRequiredService<ICatalogRepository>(), services.GetRequiredService<IInventoryRepository>(),
                services.GetRequiredService<IInventoryCostAllocator>(), services.GetRequiredService<IPartyRepository>(),
                services.GetRequiredService<ITraceabilityRepository>(), services.GetRequiredService<IResourceLock>(),
                services.GetRequiredService<IBusinessAuditWriter>(), services.GetRequiredService<IClock>(),
                services.GetRequiredService<ITransactionRunner>(), services.GetRequiredService<IApplicationPermissionAuthorizer>(),
                flushing, services.GetRequiredService<IOperationOutcomeLedger>(),
                services.GetRequiredService<IPhysicalUnitCreationAuthority>(), services.GetRequiredService<IOperationLock>());
            await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(
                new(StockAdjustmentMode.Delta, StockAdjustmentReason.Lost,
                    [new(fixture.ProductId, null, StockAdjustmentDirection.Decrease, InventoryBucket.Sellable, 6m, null)],
                    fixture.ActorId, operation), default));
            Assert.True(flushing.Flushed);
        }
        await using var read = provider.CreateAsyncScope();
        var db = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.False(await db.StockAdjustments.AnyAsync(x => x.CorrelationId == operation));
        Assert.False(await db.StockAdjustmentItems.AnyAsync(x => x.ProductId == fixture.ProductId));
        Assert.False(await db.InventoryMovements.AnyAsync(x => x.CorrelationId == operation));
        Assert.False(await db.OperationOutcomes.AnyAsync(x => x.ClientOperationId == operation));
        Assert.False(await db.BusinessAuditEvents.AnyAsync(x => x.CorrelationId == operation));
        var pool = await db.ProductCostStates.SingleAsync(x => x.ProductId == fixture.ProductId);
        Assert.Equal(20m, pool.CostedQty);
        Assert.Equal(3000m, pool.TotalInventoryCost);
        Assert.Equal(20m, (await db.StockBalances.SingleAsync(x => x.ProductId == fixture.ProductId)).SellableQty);
        Assert.Equal(20m, await (from b in db.InventoryLotBucketBalances join l in db.InventoryLots on b.LotId equals l.Id
            where l.ProductId == fixture.ProductId && b.StockBucket == InventoryBucket.Sellable select b.Quantity).SumAsync());
        Assert.False(await (from c in db.InventoryLotConsumptions join l in db.InventoryLots on c.LotId equals l.Id
            where l.ProductId == fixture.ProductId select c).AnyAsync());
    }

    [Theory]
    [InlineData(StockAdjustmentReason.Lost, StockAdjustmentMode.Delta, 6, 900)]
    [InlineData(StockAdjustmentReason.Other, StockAdjustmentMode.Delta, 20, 3000)]
    [InlineData(StockAdjustmentReason.PhysicalCountCorrection, StockAdjustmentMode.SetPhysicalCount, 14, 900)]
    public async Task NegativeAdjustment_PersistsActualRemovalLossAndReconcilesProfit(
        StockAdjustmentReason reason, StockAdjustmentMode mode, int quantity, int expectedLoss)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var fixture = await SeedAsync(provider);
        var beforeProfit = await ProfitAsync(provider);
        var operation = Guid.NewGuid();
        var command = new CreateStockAdjustmentCommand(mode, reason,
            [new(fixture.ProductId, null, StockAdjustmentDirection.Decrease, InventoryBucket.Sellable,
                quantity, 9999m, ReasonDetails: "Approved physical correction")], fixture.ActorId, operation);
        Guid adjustment;
        await using (var scope = provider.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<CreateStockAdjustmentHandler>().HandleAsync(command, default);
            Assert.True(result.IsSuccess, result.Error?.Code + ":" + result.Error?.Message);
            adjustment = result.Value;
        }
        await using (var scope = provider.CreateAsyncScope())
        {
            var replay = await scope.ServiceProvider.GetRequiredService<CreateStockAdjustmentHandler>().HandleAsync(command, default);
            Assert.True(replay.IsSuccess, replay.Error?.Message);
            Assert.Equal(adjustment, replay.Value);
        }
        await using var read = provider.CreateAsyncScope();
        var db = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var movement = Assert.Single(await db.InventoryMovements.Where(x => x.CorrelationId == operation).ToListAsync());
        Assert.Equal((decimal)expectedLoss, movement.RecognizedLossAmount);
        var item = Assert.Single(await db.StockAdjustmentItems.Where(x => x.StockAdjustmentId == adjustment).ToListAsync());
        Assert.Equal((decimal)expectedLoss, item.TotalCostSnapshot);
        Assert.Equal(150m, item.UnitCostSnapshot);
        Assert.Equal(150m, movement.UnitCostSnapshot);
        Assert.Equal("STOCK_ADJUSTMENT", movement.ReferenceType);
        Assert.Equal(item.Id, movement.ReferenceId);
        var removedQuantity = mode == StockAdjustmentMode.SetPhysicalCount ? 20m - quantity : quantity;
        var pool = await db.ProductCostStates.SingleAsync(x => x.ProductId == fixture.ProductId);
        Assert.Equal(20m - removedQuantity, pool.CostedQty);
        Assert.Equal(3000m - expectedLoss, pool.TotalInventoryCost);
        Assert.Equal(20m - removedQuantity, (await db.StockBalances.SingleAsync(x => x.ProductId == fixture.ProductId)).SellableQty);
        var lotQuantity = await (from b in db.InventoryLotBucketBalances join l in db.InventoryLots on b.LotId equals l.Id
            where l.ProductId == fixture.ProductId && b.StockBucket == InventoryBucket.Sellable select b.Quantity).SumAsync();
        Assert.Equal(20m - removedQuantity, lotQuantity);
        Assert.Equal(removedQuantity, await db.InventoryLotConsumptions.Where(x => x.MovementId == movement.Id).SumAsync(x => x.Quantity));
        var effect = Assert.Single(await db.InventoryMovementEffects.Where(x => x.MovementId == movement.Id).ToListAsync());
        Assert.Equal(-removedQuantity, effect.QuantityDelta);
        Assert.Equal(20m, effect.QuantityBefore);
        Assert.Equal(20m - removedQuantity, effect.QuantityAfter);
        Assert.Single(await db.BusinessAuditEvents.Where(x => x.CorrelationId == operation).ToListAsync());
        Assert.Single(await db.OperationOutcomes.Where(x => x.ClientOperationId == operation).ToListAsync());
        Assert.Empty(await db.CashMovements.Where(x => x.ActorId == fixture.ActorId).ToListAsync());
        Assert.Empty(await db.SupplierRefunds.Where(x => x.SupplierId == fixture.SupplierId).ToListAsync());
        Assert.Equal(3000m, await read.ServiceProvider.GetRequiredService<ISupplierAccountRepository>().GetCurrentBalanceAsync(fixture.SupplierId, default));
        var afterProfit = await ProfitAsync(provider);
        Assert.Equal(beforeProfit.NetSales, afterProfit.NetSales);
        Assert.Equal(beforeProfit.GrossProfit, afterProfit.GrossProfit);
        Assert.Equal(beforeProfit.Expenses, afterProfit.Expenses);
        Assert.Equal(beforeProfit.NetProfit - expectedLoss, afterProfit.NetProfit);
    }

    private static async Task<(Guid ProductId, Guid ActorId, Guid SupplierId)> SeedAsync(ServiceProvider provider, decimal[]? costs = null)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var fixture = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db, defaultSalePrice: 300m);
        foreach (var cost in costs ?? [100m, 200m])
        {
            if (cost == 0m)
            {
                // Explicit free opening is the existing valid inventory authority;
                // a zero-total purchase cannot create a positive supplier obligation.
                var opening = await scope.ServiceProvider.GetRequiredService<CreateStockAdjustmentHandler>().HandleAsync(
                    new(StockAdjustmentMode.Delta, StockAdjustmentReason.OpeningStock,
                        [new(fixture.ProductId, null, StockAdjustmentDirection.Increase, InventoryBucket.Sellable, 10m, 0m)],
                        fixture.ActorId, Guid.NewGuid()), default);
                Assert.True(opening.IsSuccess, opening.Error?.Message);
                continue;
            }
            var purchase = await scope.ServiceProvider.GetRequiredService<CreatePurchaseHandler>().HandleAsync(
                new(fixture.SupplierId, "P5-A01-" + Guid.NewGuid(), DateOnly.FromDateTime(DateTime.Today), null, 0m,
                    PurchaseSettlementMode.External, fixture.ActorId, Guid.NewGuid(),
                    [new(fixture.ProductId, fixture.ProductUnitId, 10m, cost, 300m, [])], InitialPaymentAmount: 0m), default);
            Assert.True(purchase.IsSuccess, purchase.Error?.Message);
        }
        return (fixture.ProductId, fixture.ActorId, fixture.SupplierId);
    }

    private static async Task<ReportingSnapshotDto> ProfitAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var day = DateOnly.FromDateTime(DateTime.Today);
        return await scope.ServiceProvider.GetRequiredService<IReportingReadService>().GetSnapshotAsync(
            ReportingPeriodKind.Daily, day, day.Month, day.Year, default);
    }

    private sealed class AdjustmentFlushThenThrow(EdgeRetailsDbContext db) : IUnitOfWork
    {
        public bool Flushed { get; private set; }
        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        {
            await db.SaveChangesAsync(cancellationToken);
            Flushed = true;
            throw new InvalidOperationException("A01 test-only failure after actual SQL flush");
        }
    }
}
