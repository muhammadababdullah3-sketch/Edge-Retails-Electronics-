using System.Text.Json;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Operations;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace EdgeRetails.IntegrationTests;

[Collection("Phase2PostgresIntegration")]
public sealed class Phase7Pass1ReturnPostgresTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task D_RET_1_CashDrawerReturnAndRetry_PersistOneCashAndBalancedKhataEffect(bool container)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var fixture = await SeedAsync(provider, container, received: 4m, openSession: true);
        try
        {
            var ids = container ? fixture.UnitIds.Take(1).ToArray() : [];
            var intent = Return(fixture, 1m, ids);
            var first = await ExecuteAsync(provider, intent);
            Assert.True(first.IsSuccess, first.Error?.Message);
            Assert.Equal(1000m, first.Value!.SupplierReturnValue);
            Assert.Equal(1000m, first.Value.InventoryCostRemoved);
            await using var verify = provider.CreateAsyncScope();
            var db = verify.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            await AssertCommittedReturnAsync(db, fixture, intent, first.Value.PurchaseReturnId, 1m);
            var committed = await BusinessStateAsync(db, fixture);
            // A new production scope proves replay reads the committed database,
            // rather than returning a tracked entity from the original request.
            var retry = await ExecuteAsync(provider, intent);
            Assert.True(retry.IsSuccess, retry.Error?.Message);
            Assert.True(retry.Value!.WasExisting);
            Assert.Equal(first.Value.PurchaseReturnId, retry.Value.PurchaseReturnId);
            Assert.Equal(committed, await BusinessStateAsync(db, fixture));
            await AssertCommittedReturnAsync(db, fixture, intent, first.Value.PurchaseReturnId, 1m);
        }
        finally { await CloseSessionsAsync(provider); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task D_RET_1_NoOpenSession_FailsWithoutStockLotUnitLedgerCashOrCostEffects(bool container)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var fixture = await SeedAsync(provider, container, received: 4m, openSession: false);
        await using var verify = provider.CreateAsyncScope();
        var db = verify.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var before = await BusinessStateAsync(db, fixture);
        var intent = Return(fixture, 1m, container ? fixture.UnitIds.Take(1).ToArray() : []);
        var result = await ExecuteAsync(provider, intent);
        Assert.Equal("cash.session_required", result.Error?.Code);
        Assert.Equal(before, await BusinessStateAsync(db, fixture));
        Assert.False(await db.PurchaseReturns.AnyAsync(x => x.ClientOperationId == intent.ClientOperationId));
        await AssertFailureOutcomeAsync(db, intent.ClientOperationId, "cash.session_required");
    }

    [Theory]
    [InlineData(4, 0, 5, false)]
    [InlineData(4, 1, 4, false)]
    [InlineData(10, 3, 7, true)]
    [InlineData(0, 0, 1, false)]
    public async Task P7_N01_ActualIntake_ReturnCapIsReceivedMinusReturned(
        int received, int priorReturned, int attempted, bool succeeds)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var fixture = await SeedAsync(provider, container: false, received, openSession: true);
        try
        {
            if (priorReturned > 0)
            {
                var prior = await ExecuteAsync(provider, Return(fixture, priorReturned, []));
                Assert.True(prior.IsSuccess, prior.Error?.Message);
            }
            await using var verify = provider.CreateAsyncScope();
            var db = verify.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            Assert.Equal(10m, (await db.PurchaseItems.SingleAsync(x => x.Id == fixture.ItemId)).BaseQuantity);
            Assert.Equal((decimal)received, await verify.ServiceProvider.GetRequiredService<IInventoryRepository>()
                .GetPurchaseItemReceivedBaseQuantityAsync(fixture.ItemId, default));
            Assert.Equal((decimal)priorReturned, await db.PurchaseReturnItems.Where(x => x.PurchaseItemId == fixture.ItemId)
                .SumAsync(x => x.BaseQuantity));
            var before = await BusinessStateAsync(db, fixture);
            var intent = Return(fixture, attempted, []);
            var result = await ExecuteAsync(provider, intent);
            Assert.Equal(succeeds, result.IsSuccess);
            if (!succeeds)
            {
                Assert.Equal("purchasing.return_exceeds_received", result.Error?.Code);
                Assert.Equal(before, await BusinessStateAsync(db, fixture));
                await AssertFailureOutcomeAsync(db, intent.ClientOperationId, "purchasing.return_exceeds_received");
            }
            else
            {
                db.ChangeTracker.Clear();
                Assert.Equal(7m, (await db.PurchaseReturnItems.SingleAsync(x => x.PurchaseReturnId == result.Value!.PurchaseReturnId)).BaseQuantity);
                Assert.Equal(10m, await db.PurchaseReturnItems.Where(x => x.PurchaseItemId == fixture.ItemId).SumAsync(x => x.BaseQuantity));
                Assert.Equal(0m, (await db.StockBalances.SingleAsync(x => x.ProductId == fixture.ProductId)).SellableQty);
                Assert.Equal(0m, await LotQuantityAsync(db, fixture.ProductId));
                Assert.Equal(0m, (await db.ProductCostStates.SingleAsync(x => x.ProductId == fixture.ProductId)).TotalInventoryCost);
                Assert.Equal(10000m, await db.CashMovements.Where(x => x.CashSessionId == fixture.SessionId).SumAsync(x => x.Amount));
                Assert.Equal(0m, await SupplierBalanceAsync(db, fixture.SupplierId));
                Assert.Equal(2, await db.PurchaseReturns.CountAsync(x => x.PurchaseId == fixture.PurchaseId));
                Assert.Equal(4, await db.SupplierAccountEntries.CountAsync(x => x.SupplierId == fixture.SupplierId &&
                    (x.EntryType == SupplierAccountEntryType.PurchaseReturnCredit || x.EntryType == SupplierAccountEntryType.SupplierRefundReceived)));
            }
        }
        finally { await CloseSessionsAsync(provider); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentReturns_ObservedDatabaseLockWait_CannotExceedReceivedOrReturnExactUnitTwice(bool container)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var fixture = await SeedAsync(provider, container, received: container ? 2m : 4m, openSession: true);
        try
        {
            var attempted = container ? 2m : 3m;
            var ids = container ? fixture.UnitIds : [];
            var firstIntent = Return(fixture, attempted, ids);
            var secondIntent = Return(fixture, attempted, ids);
            await using var firstScope = provider.CreateAsyncScope();
            var firstDb = firstScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            await using var firstTransaction = await firstDb.Database.BeginTransactionAsync();
            var first = await firstScope.ServiceProvider.GetRequiredService<CreatePurchaseReturnHandler>().HandleAsync(firstIntent, default);
            Assert.True(first.IsSuccess, first.Error?.Message);
            await using var secondScope = provider.CreateAsyncScope();
            var secondDb = secondScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            await secondDb.Database.OpenConnectionAsync();
            var application = "phase7-return-" + Guid.NewGuid().ToString("N");
            await secondDb.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('application_name', {application}, false)");
            var contender = secondScope.ServiceProvider.GetRequiredService<CreatePurchaseReturnHandler>().HandleAsync(secondIntent, default);
            try
            {
                await AssertLockWaitAsync(application);
                Assert.False(contender.IsCompleted);
            }
            finally { await firstTransaction.CommitAsync(); }
            var second = await contender.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(second.IsSuccess);
            Assert.Equal("purchasing.return_exceeds_received", second.Error?.Code);
            await using var verify = provider.CreateAsyncScope();
            var db = verify.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            await AssertCommittedReturnAsync(db, fixture, firstIntent, first.Value!.PurchaseReturnId, attempted);
            Assert.Equal(1, await db.PurchaseReturns.CountAsync(x => x.PurchaseId == fixture.PurchaseId));
            Assert.Equal(attempted * fixture.Factor, await db.PurchaseReturnItems.Where(x => x.PurchaseItemId == fixture.ItemId).SumAsync(x => x.BaseQuantity));
            Assert.False(await db.StockBalances.AnyAsync(x => x.ProductId == fixture.ProductId && x.SellableQty < 0m));
            Assert.False(await (from balance in db.InventoryLotBucketBalances join lot in db.InventoryLots on balance.LotId equals lot.Id
                where lot.ProductId == fixture.ProductId && balance.Quantity < 0m select balance).AnyAsync());
            Assert.Equal(ids.Length, await (from link in db.PurchaseReturnItemUnits join item in db.PurchaseReturnItems
                on link.PurchaseReturnItemId equals item.Id where item.PurchaseItemId == fixture.ItemId select link).CountAsync());
            await AssertFailureOutcomeAsync(db, secondIntent.ClientOperationId, "purchasing.return_exceeds_received");
        }
        finally { await CloseSessionsAsync(provider); }
    }

    [Fact]
    public async Task Return_SaveThenThrowTestUnitOfWork_RollsBackPersistedBusinessCashLedgerAndSuccessOutcome()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var fixture = await SeedAsync(provider, container: true, received: 2m, openSession: true);
        try
        {
            await using var verify = provider.CreateAsyncScope();
            var verifyDb = verify.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var before = await BusinessStateAsync(verifyDb, fixture);
            var intent = Return(fixture, 1m, fixture.UnitIds.Take(1).ToArray());
            await using var failing = provider.CreateAsyncScope();
            var db = failing.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var injection = new SaveThenThrowUnitOfWork(db);
            var handler = ActivatorUtilities.CreateInstance<CreatePurchaseReturnHandler>(failing.ServiceProvider, injection);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(intent, default));
            Assert.Equal("phase7.return.test_only_after_save_failure", exception.Message);
            Assert.True(injection.SavedBeforeFailure);
            Assert.Equal(before, await BusinessStateAsync(verifyDb, fixture));
            Assert.False(await verifyDb.OperationOutcomes.AnyAsync(x => x.ClientOperationId == intent.ClientOperationId));
        }
        finally { await CloseSessionsAsync(provider); }
    }

    private static async Task<Fixture> SeedAsync(ServiceProvider provider, bool container, decimal received, bool openSession)
    {
        await CloseSessionsAsync(provider);
        try
        {
            return await SeedCoreAsync(provider, container, received, openSession);
        }
        catch
        {
            await CloseSessionsAsync(provider);
            throw;
        }
    }

    private static async Task<Fixture> SeedCoreAsync(ServiceProvider provider, bool container, decimal received, bool openSession)
    {
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        var seed = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);
        var factor = container ? 2m : 1m;
        if (container)
        {
            (await db.Products.SingleAsync(x => x.Id == seed.ProductId)).TrackingMode = TrackingMode.Container;
            (await db.ProductUnits.SingleAsync(x => x.Id == seed.ProductUnitId)).FactorToBaseUnit = factor;
            await db.SaveChangesAsync();
        }
        Guid? sessionId = openSession ? (await Phase2PostgresTestHarness.SeedOpenCashSessionAsync(db, seed.ActorId, 10000m)).Id : null;
        var purchase = await services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new CreatePurchaseCommand(
            seed.SupplierId, "P7-RET-" + Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), "Owned certification", 0m,
            PurchaseSettlementMode.External, seed.ActorId, Guid.NewGuid(),
            [new CreatePurchaseLineInput(seed.ProductId, seed.ProductUnitId, 10m, 1000m, 1500m, [])],
            InitialPaymentAmount: 10000m, InitialPaymentMethod: SupplierSettlementMethod.External,
            ReceiveStockImmediately: false), default);
        Assert.True(purchase.IsSuccess, purchase.Error?.Message);
        var purchaseId = purchase.Value!.PurchaseId;
        db.ChangeTracker.Clear();
        var itemId = await db.PurchaseItems.Where(x => x.PurchaseId == purchaseId).Select(x => x.Id).SingleAsync();
        if (received > 0m)
        {
            // Real deferred intake, not a direct stock/received-quantity seed.
            var intake = await services.GetRequiredService<ReceiveProductIntakeHandler>().HandleAsync(new ReceiveProductIntakeCommand(
                purchaseId, seed.ProductId, seed.ProductUnitId, received, 1000m, [], seed.ActorId, Guid.NewGuid()), default);
            Assert.True(intake.IsSuccess, intake.Error?.Message);
        }
        db.ChangeTracker.Clear();
        var units = await db.InventoryUnits.Where(x => x.ProductId == seed.ProductId).OrderBy(x => x.Id).ToArrayAsync();
        var ids = units.Select(x => x.Id).ToArray();
        var origins = units.ToDictionary(x => x.Id, x => new PhysicalOrigin(x.TrackingCode, x.ItemSequence,
            x.SupplierProductId, x.InventoryLotId, x.SupplierCodeSnapshot, x.ProductSkuSnapshot));
        Assert.Equal(container ? decimal.ToInt32(received) : 0, ids.Length);
        Assert.Equal(0m, await SupplierBalanceAsync(db, seed.SupplierId));
        var actualPayment = await db.SupplierPayments.SingleAsync(x => x.SupplierId == seed.SupplierId);
        Assert.Equal(10000m, actualPayment.Amount);
        Assert.Equal(SupplierSettlementStatus.Posted, actualPayment.Status);
        Assert.Equal(received * factor, await services.GetRequiredService<IInventoryRepository>().GetPurchaseItemReceivedBaseQuantityAsync(itemId, default));
        return new Fixture(purchaseId, itemId, seed.ProductId, seed.ProductUnitId, seed.SupplierId, seed.ActorId, factor, received, sessionId, ids, origins);
    }

    private static CreatePurchaseReturnCommand Return(Fixture fixture, decimal entered, Guid[] ids) => new(
        fixture.PurchaseId, "Owned Phase7 return", null, PurchaseReturnSettlementMode.CashDrawer, fixture.ActorId, Guid.NewGuid(),
        [new PurchaseReturnLineInput(fixture.ItemId, entered, 1000m, ids)]);

    private static async Task<EdgeRetails.Application.Common.Result<CreatePurchaseReturnResult>> ExecuteAsync(
        ServiceProvider provider, CreatePurchaseReturnCommand intent)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CreatePurchaseReturnHandler>().HandleAsync(intent, default);
    }

    private static async Task AssertCommittedReturnAsync(EdgeRetailsDbContext db, Fixture fixture,
        CreatePurchaseReturnCommand intent, Guid returnId, decimal entered)
    {
        db.ChangeTracker.Clear();
        var header = await db.PurchaseReturns.SingleAsync(x => x.Id == returnId);
        Assert.Equal(fixture.PurchaseId, header.PurchaseId);
        Assert.Equal(intent.ClientOperationId, header.ClientOperationId);
        Assert.Equal(PurchaseReturnStatus.Completed, header.Status);
        Assert.Equal(PurchaseReturnSettlementMode.CashDrawer, header.SettlementMode);
        Assert.Equal(entered * 1000m, header.SupplierReturnValue);
        Assert.Equal(entered * 1000m, header.InventoryCostRemoved);
        var item = await db.PurchaseReturnItems.SingleAsync(x => x.PurchaseReturnId == returnId);
        Assert.Equal(fixture.ItemId, item.PurchaseItemId);
        Assert.Equal(fixture.ProductUnitId, item.ProductUnitId);
        Assert.Equal(fixture.Factor, item.FactorToBaseSnapshot);
        Assert.Equal(entered * fixture.Factor, item.BaseQuantity);
        Assert.Equal((fixture.Received - entered) * fixture.Factor, (await db.StockBalances.SingleAsync(x => x.ProductId == fixture.ProductId)).SellableQty);
        Assert.Equal((fixture.Received - entered) * fixture.Factor, await LotQuantityAsync(db, fixture.ProductId));
        var cost = await db.ProductCostStates.SingleAsync(x => x.ProductId == fixture.ProductId);
        Assert.Equal((fixture.Received - entered) * fixture.Factor, cost.CostedQty);
        Assert.Equal((fixture.Received - entered) * 1000m, cost.TotalInventoryCost);
        var refundFact = await db.SupplierRefunds.SingleAsync(x => x.ClientOperationId == intent.ClientOperationId);
        Assert.Equal("PurchaseReturn", refundFact.ReferenceType);
        Assert.Equal(returnId, refundFact.ReferenceId);
        Assert.Equal(fixture.SupplierId, refundFact.SupplierId);
        Assert.Equal(SupplierSettlementMethod.CashDrawer, refundFact.Method);
        var cash = await db.CashMovements.SingleAsync(x => x.SourceType == "SUPPLIER_REFUND" && x.SourceId == refundFact.Id);
        Assert.Equal(CashMovementType.SupplierRefundCashIn, cash.MovementType);
        Assert.Equal(CashMovementDirection.In, cash.Direction);
        Assert.Equal(fixture.SessionId, cash.CashSessionId);
        Assert.Equal(entered * 1000m, cash.Amount);
        Assert.Equal(cash.Amount, refundFact.Amount);
        var entries = await db.SupplierAccountEntries.Where(x => x.ClientOperationId == intent.ClientOperationId).ToListAsync();
        Assert.Equal(2, entries.Count);
        var credit = Assert.Single(entries, x => x.EntryType == SupplierAccountEntryType.PurchaseReturnCredit);
        var refund = Assert.Single(entries, x => x.EntryType == SupplierAccountEntryType.SupplierRefundReceived);
        Assert.Equal(SupplierAccountDirection.DecreasePayable, credit.Direction);
        Assert.Equal(SupplierAccountDirection.IncreasePayable, refund.Direction);
        Assert.Equal(returnId, credit.ReferenceId);
        Assert.Equal(refundFact.Id, refund.ReferenceId);
        Assert.Equal("SupplierRefund", refund.ReferenceType);
        Assert.Equal(cash.Amount, credit.Amount);
        Assert.Equal(cash.Amount, refund.Amount);
        Assert.All(entries, x => { Assert.Equal(fixture.SupplierId, x.SupplierId); Assert.Equal(intent.ClientOperationId, x.ClientOperationId); Assert.Equal(fixture.ActorId, x.ActorId); });
        Assert.Equal(0m, entries.Sum(x => x.SignedAmount));
        Assert.Equal(0m, await SupplierBalanceAsync(db, fixture.SupplierId));
        var session = await db.CashSessions.SingleAsync(x => x.Id == fixture.SessionId);
        var cashRows = await db.CashMovements.Where(x => x.CashSessionId == session.Id).ToListAsync();
        Assert.Single(cashRows);
        Assert.Equal(10000m + entered * 1000m, session.OpeningCash + cashRows.Sum(x => x.SignedAmount));
        var movement = await db.InventoryMovements.SingleAsync(x => x.ReferenceType == "PURCHASE_RETURN" && x.ReferenceId == returnId);
        Assert.Equal(intent.ClientOperationId, movement.CorrelationId);
        Assert.Equal(-entered * fixture.Factor, (await db.InventoryMovementEffects.SingleAsync(x => x.MovementId == movement.Id)).QuantityDelta);
        Assert.Equal(entered * fixture.Factor, await db.InventoryLotConsumptions.Where(x => x.MovementId == movement.Id).SumAsync(x => x.Quantity));
        var audit = await db.BusinessAuditEvents.SingleAsync(x => x.Action == "PURCHASE_RETURN_COMPLETED" && x.EntityId == returnId);
        Assert.Equal(intent.ClientOperationId, audit.CorrelationId);
        var outcome = await db.OperationOutcomes.SingleAsync(x => x.ClientOperationId == intent.ClientOperationId);
        Assert.Equal(OperationOutcomeStatus.Succeeded, outcome.Status);
        Assert.True(outcome.WasCommitted);
        Assert.Equal(returnId, outcome.ResultEntityId);
        foreach (var id in fixture.UnitIds)
        {
            var unit = await db.InventoryUnits.SingleAsync(x => x.Id == id);
            var origin = fixture.Origins[id];
            Assert.Equal(origin.TrackingCode, unit.TrackingCode);
            Assert.Equal(origin.ItemSequence, unit.ItemSequence);
            Assert.Equal(origin.SupplierProductId, unit.SupplierProductId);
            Assert.Equal(origin.LotId, unit.InventoryLotId);
            Assert.Equal(origin.Dealer, unit.SupplierCodeSnapshot);
            Assert.Equal(origin.Sku, unit.ProductSkuSnapshot);
            var selected = intent.Lines[0].InventoryUnitIds.Contains(id);
            Assert.Equal(selected ? InventoryUnitStatus.SupplierReturned : InventoryUnitStatus.InStock, unit.Status);
            Assert.Equal(fixture.ItemId, unit.SourcePurchaseItemId);
            Assert.NotNull(unit.InventoryLotId);
            var lot = await db.InventoryLots.SingleAsync(x => x.Id == unit.InventoryLotId);
            Assert.Equal(fixture.ItemId, lot.PurchaseItemId);
            var pair = await db.SupplierProducts.SingleAsync(x => x.Id == unit.SupplierProductId);
            Assert.Equal(fixture.SupplierId, pair.SupplierId);
            Assert.Equal(fixture.ProductId, pair.ProductId);
            Assert.Equal(1000m, unit.AcquisitionCost);
            Assert.Equal(fixture.Factor, await new EdgeRetails.Infrastructure.Repositories.InventoryRepository(db)
                .GetPhysicalUnitBaseQuantitySnapshotAsync(unit, default));
            Assert.Equal(unit.TrackingCode, TraceabilityCodeRules.BuildTrackingCode(unit.SupplierCodeSnapshot!, unit.ProductSkuSnapshot!, unit.ItemSequence!.Value));
            Assert.Equal(selected ? 1 : 0, await db.PurchaseReturnItemUnits.CountAsync(x => x.InventoryUnitId == id));
            Assert.Equal(selected ? 1 : 0, await db.InventoryMovementUnits.CountAsync(x => x.MovementId == movement.Id && x.InventoryUnitId == id));
        }
        if (fixture.UnitIds.Length > 0)
        {
            Assert.Equal(fixture.UnitIds.Length + 1L, (await db.SupplierProducts.SingleAsync(x => x.ProductId == fixture.ProductId)).NextItemSequence);
        }
    }

    private static async Task<string> BusinessStateAsync(EdgeRetailsDbContext db, Fixture fixture)
    {
        db.ChangeTracker.Clear();
        var lots = await db.InventoryLots.Where(x => x.ProductId == fixture.ProductId).OrderBy(x => x.Id).ToArrayAsync();
        var lotIds = lots.Select(x => x.Id).ToArray();
        var movements = await db.InventoryMovements.Where(x => x.ProductId == fixture.ProductId).OrderBy(x => x.Id).ToArrayAsync();
        var movementIds = movements.Select(x => x.Id).ToArray();
        return JsonSerializer.Serialize(new
        {
            Purchase = await db.Purchases.Where(x => x.Id == fixture.PurchaseId).ToArrayAsync(),
            Returns = await db.PurchaseReturns.Where(x => x.PurchaseId == fixture.PurchaseId).OrderBy(x => x.Id).ToArrayAsync(),
            ReturnItems = await db.PurchaseReturnItems.Where(x => x.PurchaseItemId == fixture.ItemId).OrderBy(x => x.Id).ToArrayAsync(),
            Stock = await db.StockBalances.Where(x => x.ProductId == fixture.ProductId).ToArrayAsync(),
            Cost = await db.ProductCostStates.Where(x => x.ProductId == fixture.ProductId).ToArrayAsync(),
            Lots = lots,
            LotBalances = await db.InventoryLotBucketBalances.Where(x => lotIds.Contains(x.LotId)).OrderBy(x => x.Id).ToArrayAsync(),
            Units = await db.InventoryUnits.Where(x => x.ProductId == fixture.ProductId).OrderBy(x => x.Id).ToArrayAsync(),
            Claims = await db.InventoryUnitIdentityClaims.Where(x => fixture.UnitIds.Contains(x.InventoryUnitId)).OrderBy(x => x.Id).ToArrayAsync(),
            Movement = movements,
            Effects = await db.InventoryMovementEffects.Where(x => movementIds.Contains(x.MovementId)).OrderBy(x => x.Id).ToArrayAsync(),
            MovementUnits = await db.InventoryMovementUnits.Where(x => movementIds.Contains(x.MovementId)).OrderBy(x => x.Id).ToArrayAsync(),
            Consumptions = await db.InventoryLotConsumptions.Where(x => lotIds.Contains(x.LotId)).OrderBy(x => x.Id).ToArrayAsync(),
            Ledger = await db.SupplierAccountEntries.Where(x => x.SupplierId == fixture.SupplierId).OrderBy(x => x.Id).ToArrayAsync(),
            Payments = await db.SupplierPayments.Where(x => x.SupplierId == fixture.SupplierId).OrderBy(x => x.Id).ToArrayAsync(),
            Refunds = await db.SupplierRefunds.Where(x => x.SupplierId == fixture.SupplierId).OrderBy(x => x.Id).ToArrayAsync(),
            Cash = await db.CashMovements.OrderBy(x => x.Id).ToArrayAsync(),
            Audit = await db.BusinessAuditEvents.Where(x => x.ActorId == fixture.ActorId).OrderBy(x => x.Id).ToArrayAsync()
        });
    }

    private static Task<decimal> SupplierBalanceAsync(EdgeRetailsDbContext db, Guid supplierId) => db.SupplierAccountEntries
        .Where(x => x.SupplierId == supplierId).SumAsync(x => x.Direction == SupplierAccountDirection.IncreasePayable ? x.Amount : -x.Amount);

    private static Task<decimal> LotQuantityAsync(EdgeRetailsDbContext db, Guid productId) =>
        (from balance in db.InventoryLotBucketBalances join lot in db.InventoryLots on balance.LotId equals lot.Id
         where lot.ProductId == productId && balance.StockBucket == InventoryBucket.Sellable select balance.Quantity).SumAsync();

    private static async Task AssertFailureOutcomeAsync(EdgeRetailsDbContext db, Guid operationId, string error)
    {
        var outcome = await db.OperationOutcomes.SingleAsync(x => x.ClientOperationId == operationId);
        Assert.Equal(OperationOutcomeStatus.Failed, outcome.Status);
        Assert.False(outcome.WasCommitted);
        Assert.Equal(error, outcome.ErrorCode);
        Assert.Null(outcome.ResultEntityId);
    }

    private static async Task CloseSessionsAsync(ServiceProvider provider)
    {
        // The attested database belongs to the rehearsal runner. Collection
        // serialization permits closing leftover owned fixture sessions only.
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        foreach (var session in await db.CashSessions.Where(x => x.Status == CashSessionStatus.Open).ToListAsync())
        {
            var rows = await db.CashMovements.Where(x => x.CashSessionId == session.Id).ToListAsync();
            var cashIn = rows.Where(x => x.Direction == CashMovementDirection.In).Sum(x => x.Amount);
            var cashOut = rows.Where(x => x.Direction == CashMovementDirection.Out).Sum(x => x.Amount);
            session.Close(cashIn, cashOut, session.OpeningCash + cashIn - cashOut, session.OpenedBy, DateTimeOffset.UtcNow);
        }
        await db.SaveChangesAsync();
    }

    private static async Task AssertLockWaitAsync(string applicationName)
    {
        await using var observer = new NpgsqlConnection(Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB"));
        await observer.OpenAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            await using var query = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE application_name=@name AND wait_event_type='Lock')", observer);
            query.Parameters.AddWithValue("name", applicationName);
            if ((bool)(await query.ExecuteScalarAsync(timeout.Token))!)
            {
                return;
            }
            await Task.Delay(50, timeout.Token);
        }
    }

    private sealed class SaveThenThrowUnitOfWork(EdgeRetailsDbContext db) : IUnitOfWork
    {
        public bool SavedBeforeFailure { get; private set; }
        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        {
            await db.SaveChangesAsync(cancellationToken);
            SavedBeforeFailure = true;
            throw new InvalidOperationException("phase7.return.test_only_after_save_failure");
        }
    }

    private sealed record Fixture(Guid PurchaseId, Guid ItemId, Guid ProductId, Guid ProductUnitId, Guid SupplierId,
        Guid ActorId, decimal Factor, decimal Received, Guid? SessionId, Guid[] UnitIds, Dictionary<Guid, PhysicalOrigin> Origins);

    private sealed record PhysicalOrigin(string? TrackingCode, long? ItemSequence, Guid? SupplierProductId,
        Guid? LotId, string? Dealer, string? Sku);
}
