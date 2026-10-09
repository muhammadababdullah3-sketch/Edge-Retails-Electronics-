using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Identity;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Text.Json;

namespace EdgeRetails.IntegrationTests;

[Collection("Phase2PostgresIntegration")]
public sealed class Phase7Pass5PurchaseReturnPostgresTests
{
    // NEW_COVERAGE: proves the missing durable refund fact before implementation.
    [Fact]
    public async Task FullyPaidImmediateCashReturnPersistsDistinctCanonicalRefund()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var fixture = await ArrangeAsync(provider, 100m, true);
        try
        {
            var intent = CashReturn(fixture);
            await using (var scope = provider.CreateAsyncScope())
            {
                var result = await scope.ServiceProvider.GetRequiredService<CreatePurchaseReturnHandler>().HandleAsync(intent, default);
                Assert.True(result.IsSuccess, result.Error?.Message);
            }
            await using var read = provider.CreateAsyncScope();
            var db = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var returned = await db.PurchaseReturns.SingleAsync(x => x.ClientOperationId == intent.ClientOperationId);
            var refund = Assert.Single(await db.SupplierRefunds.Where(x => x.ClientOperationId == intent.ClientOperationId).ToListAsync());
            Assert.Equal(fixture.SupplierId, refund.SupplierId);
            Assert.Equal(100m, refund.Amount);
            Assert.Equal(SupplierSettlementMethod.CashDrawer, refund.Method);
            Assert.Equal("PurchaseReturn", refund.ReferenceType);
            Assert.Equal(returned.Id, refund.ReferenceId);
            var cash = await db.CashMovements.SingleAsync(x => x.SourceId == refund.Id);
            Assert.Equal(CashMovementType.SupplierRefundCashIn, cash.MovementType);
            Assert.Equal("SUPPLIER_REFUND", cash.SourceType);
            Assert.Equal(CashMovementDirection.In, cash.Direction);
            Assert.Equal(100m, cash.Amount);
            Assert.Equal(fixture.SessionId, cash.CashSessionId);
            var entries = await db.SupplierAccountEntries.Where(x => x.ClientOperationId == intent.ClientOperationId).ToListAsync();
            Assert.Equal(2, entries.Count);
            Assert.Equal(returned.Id, Assert.Single(entries, x => x.EntryType == SupplierAccountEntryType.PurchaseReturnCredit).ReferenceId);
            Assert.Equal(refund.Id, Assert.Single(entries, x => x.EntryType == SupplierAccountEntryType.SupplierRefundReceived).ReferenceId);
            Assert.Equal(0m, entries.Sum(x => x.SignedAmount));
            Assert.Equal(0m, await BalanceAsync(db, fixture.SupplierId));
            Assert.Equal(0m, (await db.StockBalances.SingleAsync(x => x.ProductId == fixture.ProductId)).SellableQty);
            Assert.Equal(0m, (await db.ProductCostStates.SingleAsync(x => x.ProductId == fixture.ProductId)).TotalInventoryCost);
            Assert.Equal(2, await db.BusinessAuditEvents.CountAsync(x => x.CorrelationId == intent.ClientOperationId));
        }
        finally { await CloseAsync(provider, fixture.SessionId); }
    }

    [Theory]
    [InlineData(SupplierSettlementMethod.Bank)]
    [InlineData(SupplierSettlementMethod.Other)]
    public async Task NonDrawerRefundRetainsMethodAndHasNoCash(SupplierSettlementMethod method)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await ArrangeAsync(provider, 100m, false);
        var intent = CashReturn(f) with { SettlementMode = PurchaseReturnSettlementMode.External, ImmediateRefundMethod = method };
        var result = await ExecuteAsync(provider, intent);
        Assert.True(result.IsSuccess, result.Error?.Message);
        await using var read = provider.CreateAsyncScope();
        var db = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var refund = await db.SupplierRefunds.SingleAsync(x => x.ClientOperationId == intent.ClientOperationId);
        Assert.Equal(method, refund.Method);
        Assert.Null(refund.CashSessionId);
        Assert.Equal(result.Value!.PurchaseReturnId, refund.ReferenceId);
        Assert.Equal(100m, refund.Amount);
        Assert.Equal((int)method, await db.Database.SqlQuery<int>($"SELECT method AS \"Value\" FROM finance.supplier_refunds WHERE id={refund.Id}").SingleAsync());
        Assert.Empty(await db.CashMovements.Where(x => x.ActorId == f.ActorId).ToListAsync());
        Assert.Equal(0m, await BalanceAsync(db, f.SupplierId));
        Assert.Equal(SupplierSettlementStatus.Posted, (await db.SupplierPayments.SingleAsync(x => x.SupplierId == f.SupplierId)).Status);
    }

    [Fact]
    public async Task PartialPaymentRefundsOnlyApprovedAvailableCredit()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await ArrangeAsync(provider, 40m, false);
        var intent = CashReturn(f) with { SettlementMode = PurchaseReturnSettlementMode.External,
            ImmediateRefundMethod = SupplierSettlementMethod.Bank, ImmediateRefundAmount = 40m };
        var result = await ExecuteAsync(provider, intent);
        Assert.True(result.IsSuccess, result.Error?.Message);
        await using var read = provider.CreateAsyncScope();
        var db = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Equal(100m, (await db.PurchaseReturns.SingleAsync(x => x.Id == result.Value!.PurchaseReturnId)).SupplierReturnValue);
        Assert.Equal(40m, (await db.SupplierRefunds.SingleAsync(x => x.ClientOperationId == intent.ClientOperationId)).Amount);
        Assert.Equal(0m, await BalanceAsync(db, f.SupplierId));
        Assert.Empty(await db.CashMovements.Where(x => x.ActorId == f.ActorId).ToListAsync());
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(40, 40.01)]
    public async Task UnpaidOrExcessRefundCannotCreateAnyBusinessEffects(decimal paid, decimal requested)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await ArrangeAsync(provider, paid, true);
        try
        {
            var before = await SnapshotAsync(provider, f);
            var result = await ExecuteAsync(provider, CashReturn(f) with { ImmediateRefundAmount = requested });
            Assert.False(result.IsSuccess);
            Assert.Equal("supplier.refund_exceeds_credit", result.Error!.Code);
            Assert.Equal(before, await SnapshotAsync(provider, f));
        }
        finally { await CloseAsync(provider, f.SessionId); }
    }

    [Fact]
    public async Task UnpaidGoodsReturnWithoutMonetarySettlementCreatesCreditOnly()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await ArrangeAsync(provider, 0m, false);
        var result = await ExecuteAsync(provider, CashReturn(f) with { SettlementMode = PurchaseReturnSettlementMode.External });
        Assert.True(result.IsSuccess, result.Error?.Message);
        await using var read = provider.CreateAsyncScope();
        var db = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Empty(await db.SupplierRefunds.Where(x => x.SupplierId == f.SupplierId).ToListAsync());
        Assert.Empty(await db.CashMovements.Where(x => x.ActorId == f.ActorId).ToListAsync());
        Assert.Equal(0m, await BalanceAsync(db, f.SupplierId));
        Assert.Equal(0m, (await db.StockBalances.SingleAsync(x => x.ProductId == f.ProductId)).SellableQty);
    }

    [Fact]
    public async Task ResponseLossReplayAfterDrawerClosesKeepsOneEconomicResult()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await ArrangeAsync(provider, 100m, true);
        try
        {
            var intent = CashReturn(f);
            var first = await ExecuteAsync(provider, intent);
            Assert.True(first.IsSuccess, first.Error?.Message);
            await CloseAsync(provider, f.SessionId);
            var before = await SnapshotAsync(provider, f);
            var replay = await ExecuteAsync(provider, intent);
            Assert.True(replay.IsSuccess, replay.Error?.Message);
            Assert.True(replay.Value!.WasExisting);
            Assert.Equal(first.Value!.PurchaseReturnId, replay.Value.PurchaseReturnId);
            Assert.Equal(before, await SnapshotAsync(provider, f));
        }
        finally { await CloseAsync(provider, f.SessionId); }
    }

    [Theory]
    [InlineData("quantity")]
    [InlineData("amount")]
    [InlineData("reason")]
    public async Task ConflictingReplayIsRejectedWithoutChangingCommittedFacts(string changed)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await ArrangeAsync(provider, 100m, false);
        var intent = CashReturn(f) with { SettlementMode = PurchaseReturnSettlementMode.External, ImmediateRefundMethod = SupplierSettlementMethod.Other };
        Assert.True((await ExecuteAsync(provider, intent)).IsSuccess);
        var before = await SnapshotAsync(provider, f);
        var conflict = changed switch
        {
            "quantity" => intent with { Lines = [intent.Lines[0] with { EnteredQuantity = 9m }] },
            "amount" => intent with { ImmediateRefundAmount = 50m },
            _ => intent with { Reason = "Different approved reason" }
        };
        var rejected = await ExecuteAsync(provider, conflict);
        Assert.False(rejected.IsSuccess);
        Assert.Equal("idempotency.payload_mismatch", rejected.Error!.Code);
        Assert.Equal(before, await SnapshotAsync(provider, f));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureBeforeCommitOrAfterSqlFlushRollsBackAllFacts(bool flush)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await ArrangeAsync(provider, 100m, true);
        try
        {
            var intent = CashReturn(f);
            var before = await SnapshotAsync(provider, f);
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
                var injection = new ThrowingUnitOfWork(db, flush);
                var handler = ActivatorUtilities.CreateInstance<CreatePurchaseReturnHandler>(scope.ServiceProvider, injection);
                await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(intent, default));
                Assert.Equal(flush, injection.Flushed);
            }
            Assert.Equal(before, await SnapshotAsync(provider, f));
            await using var read = provider.CreateAsyncScope();
            Assert.False(await read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>().OperationOutcomes.AnyAsync(x => x.ClientOperationId == intent.ClientOperationId));
        }
        finally { await CloseAsync(provider, f.SessionId); }
    }

    [Fact]
    public async Task RefundPermissionDeniedThroughActualRoleLeavesNoPartialReturn()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await ArrangeAsync(provider, 100m, false);
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var role = new Role { Name = "C01 goods-only-" + Guid.NewGuid(), IsActive = true };
            db.Roles.Add(role);
            var permission = await db.Permissions.SingleAsync(x => x.Key == PermissionKeys.PurchasingManage);
            db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permission.Id });
            (await db.Users.SingleAsync(x => x.Id == f.ActorId)).RoleId = role.Id;
            await db.SaveChangesAsync();
        }
        var before = await SnapshotAsync(provider, f);
        var result = await ExecuteAsync(provider, CashReturn(f) with { SettlementMode = PurchaseReturnSettlementMode.External, ImmediateRefundMethod = SupplierSettlementMethod.Bank });
        Assert.False(result.IsSuccess);
        Assert.Equal("authorization.denied", result.Error!.Code);
        Assert.Equal(before, await SnapshotAsync(provider, f));
    }

    [Fact]
    public async Task ConcurrentSameOperationWaitsAndPostsOneReturnRefundAndCash()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await ArrangeAsync(provider, 100m, true);
        try
        {
            var intent = CashReturn(f);
            await using var winnerScope = provider.CreateAsyncScope();
            var winnerDb = winnerScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            await using var transaction = await winnerDb.Database.BeginTransactionAsync();
            var winner = await winnerScope.ServiceProvider.GetRequiredService<CreatePurchaseReturnHandler>().HandleAsync(intent, default);
            Assert.True(winner.IsSuccess, winner.Error?.Message);
            await using var contenderScope = provider.CreateAsyncScope();
            var contenderDb = contenderScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            await contenderDb.Database.OpenConnectionAsync();
            var application = "p5-c01-" + Guid.NewGuid().ToString("N");
            await contenderDb.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('application_name', {application}, false)");
            var contender = contenderScope.ServiceProvider.GetRequiredService<CreatePurchaseReturnHandler>().HandleAsync(intent, default);
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
            var replay = await contender.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(replay.IsSuccess, replay.Error?.Message);
            Assert.True(replay.Value!.WasExisting);
            await using var read = provider.CreateAsyncScope();
            var db = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var refund = await db.SupplierRefunds.SingleAsync(x => x.ClientOperationId == intent.ClientOperationId);
            Assert.Single(await db.PurchaseReturns.Where(x => x.ClientOperationId == intent.ClientOperationId).ToListAsync());
            Assert.Single(await db.CashMovements.Where(x => x.SourceId == refund.Id).ToListAsync());
            Assert.Equal(2, await db.SupplierAccountEntries.CountAsync(x => x.ClientOperationId == intent.ClientOperationId));
            Assert.Equal(2, await db.BusinessAuditEvents.CountAsync(x => x.CorrelationId == intent.ClientOperationId));
        }
        finally { await CloseAsync(provider, f.SessionId); }
    }

    private static async Task<EdgeRetails.Application.Common.Result<CreatePurchaseReturnResult>> ExecuteAsync(ServiceProvider provider, CreatePurchaseReturnCommand intent)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CreatePurchaseReturnHandler>().HandleAsync(intent, default);
    }

    [Fact]
    public async Task CompoundPostingUsesOneTransactionBoundaryAndCallerSave()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await ArrangeAsync(provider, 100m, false);
        var intent = CashReturn(f) with { SettlementMode = PurchaseReturnSettlementMode.External, ImmediateRefundMethod = SupplierSettlementMethod.Bank };
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var transactions = new CountingTransactions(services.GetRequiredService<ITransactionRunner>());
        var saves = new CountingSaves(services.GetRequiredService<IUnitOfWork>());
        var refund = ActivatorUtilities.CreateInstance<CreateSupplierRefundHandler>(services, transactions, saves);
        var handler = ActivatorUtilities.CreateInstance<CreatePurchaseReturnHandler>(services, transactions, saves, refund);
        var result = await handler.HandleAsync(intent, default);
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(1, transactions.Calls);
        Assert.Equal(1, saves.Calls);
        await using var read = provider.CreateAsyncScope();
        var db = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Single(await db.SupplierRefunds.Where(x => x.ClientOperationId == intent.ClientOperationId).ToListAsync());
    }

    private static async Task<string> SnapshotAsync(ServiceProvider provider, Fixture f)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var lots = await db.InventoryLots.Where(x => x.ProductId == f.ProductId).OrderBy(x => x.Id).ToArrayAsync();
        var lotIds = lots.Select(x => x.Id).ToArray();
        var movements = await db.InventoryMovements.Where(x => x.ProductId == f.ProductId).OrderBy(x => x.Id).ToArrayAsync();
        var movementIds = movements.Select(x => x.Id).ToArray();
        return JsonSerializer.Serialize(new
        {
            Returns = await db.PurchaseReturns.Where(x => x.PurchaseId == f.PurchaseId).OrderBy(x => x.Id).ToArrayAsync(),
            Items = await db.PurchaseReturnItems.Where(x => x.PurchaseItemId == f.ItemId).OrderBy(x => x.Id).ToArrayAsync(),
            Refunds = await db.SupplierRefunds.Where(x => x.SupplierId == f.SupplierId).OrderBy(x => x.Id).ToArrayAsync(),
            Payments = await db.SupplierPayments.Where(x => x.SupplierId == f.SupplierId).OrderBy(x => x.Id).ToArrayAsync(),
            Ledger = await db.SupplierAccountEntries.Where(x => x.SupplierId == f.SupplierId).OrderBy(x => x.Id).ToArrayAsync(),
            Cash = await db.CashMovements.Where(x => x.ActorId == f.ActorId).OrderBy(x => x.Id).ToArrayAsync(),
            Audit = await db.BusinessAuditEvents.Where(x => x.ActorId == f.ActorId).OrderBy(x => x.Id).ToArrayAsync(),
            Stock = await db.StockBalances.Where(x => x.ProductId == f.ProductId).ToArrayAsync(),
            Cost = await db.ProductCostStates.Where(x => x.ProductId == f.ProductId).ToArrayAsync(),
            Lots = lots,
            LotBalances = await db.InventoryLotBucketBalances.Where(x => lotIds.Contains(x.LotId)).OrderBy(x => x.Id).ToArrayAsync(),
            Movements = movements,
            Effects = await db.InventoryMovementEffects.Where(x => movementIds.Contains(x.MovementId)).OrderBy(x => x.Id).ToArrayAsync(),
            Consumptions = await db.InventoryLotConsumptions.Where(x => lotIds.Contains(x.LotId)).OrderBy(x => x.Id).ToArrayAsync()
        });
    }

    private sealed class CountingTransactions(ITransactionRunner inner) : ITransactionRunner
    {
        public int Calls { get; private set; }
        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
        {
            Calls++;
            return inner.ExecuteAsync(operation, cancellationToken);
        }
    }

    private sealed class CountingSaves(IUnitOfWork inner) : IUnitOfWork
    {
        public int Calls { get; private set; }
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return inner.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class ThrowingUnitOfWork(EdgeRetailsDbContext db, bool flush) : IUnitOfWork
    {
        public bool Flushed { get; private set; }
        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        {
            if (flush) { await db.SaveChangesAsync(cancellationToken); Flushed = true; }
            throw new InvalidOperationException("C01 test-only provisional SQL/commit failure");
        }
    }

    private static CreatePurchaseReturnCommand CashReturn(Fixture fixture) => new(
        fixture.PurchaseId, "C01 paid goods return", "Approved immediate refund", PurchaseReturnSettlementMode.CashDrawer,
        fixture.ActorId, Guid.NewGuid(), [new PurchaseReturnLineInput(fixture.ItemId, 10m, 10m, [])]);

    private static async Task<Fixture> ArrangeAsync(ServiceProvider provider, decimal paid, bool drawer)
    {
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        foreach (var session in await db.CashSessions.Where(x => x.Status == CashSessionStatus.Open).ToListAsync())
        {
            session.Status = CashSessionStatus.Closed;
            session.ClosedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync();
        var seed = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);
        var sessionId = drawer ? (Guid?)(await Phase2PostgresTestHarness.SeedOpenCashSessionAsync(db, seed.ActorId, 1000m)).Id : null;
        var purchase = await services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new CreatePurchaseCommand(
            seed.SupplierId, "C01-" + Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), null, 0m,
            PurchaseSettlementMode.External, seed.ActorId, Guid.NewGuid(),
            [new CreatePurchaseLineInput(seed.ProductId, seed.ProductUnitId, 10m, 10m, 20m, [])],
            InitialPaymentAmount: paid, InitialPaymentMethod: SupplierSettlementMethod.External), default);
        Assert.True(purchase.IsSuccess, purchase.Error?.Message);
        var purchaseId = purchase.Value!.PurchaseId;
        var itemId = await db.PurchaseItems.Where(x => x.PurchaseId == purchaseId).Select(x => x.Id).SingleAsync();
        return new(purchaseId, itemId, seed.ProductId, seed.SupplierId, seed.ActorId, sessionId);
    }

    private static Task<decimal> BalanceAsync(EdgeRetailsDbContext db, Guid supplierId) =>
        db.SupplierAccountEntries.Where(x => x.SupplierId == supplierId)
            .SumAsync(x => x.Direction == SupplierAccountDirection.IncreasePayable ? x.Amount : -x.Amount);

    private static async Task CloseAsync(ServiceProvider provider, Guid? sessionId)
    {
        if (sessionId is null) { return; }
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var session = await db.CashSessions.SingleAsync(x => x.Id == sessionId);
        session.Status = CashSessionStatus.Closed;
        session.ClosedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private sealed record Fixture(Guid PurchaseId, Guid ItemId, Guid ProductId, Guid SupplierId, Guid ActorId, Guid? SessionId);
}
