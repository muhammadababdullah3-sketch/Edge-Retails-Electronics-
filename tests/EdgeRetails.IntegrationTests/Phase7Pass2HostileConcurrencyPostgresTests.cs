using System.Text.Json;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Features.Thaka;
using EdgeRetails.Application.Features.Warranty;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.Thaka;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace EdgeRetails.IntegrationTests;

[Collection("Phase2PostgresIntegration")]
public sealed class Phase7Pass2HostileConcurrencyPostgresTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ThreeNegativeAdjustments_ObservedFanout_OnlyOneConsumesEightOfTen()
    {
        await using var f = await Fixture.CreateAsync(physical: false, quantity: 10m);
        var intents = Enumerable.Range(0, 3).Select(_ => f.Intent(Operation.Decrease, 8m)).ToArray();
        var gate = new FlushControl();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var winner = Task.Run(() => RunAsync(f, intents[0], gate, cancellationToken: cancellation.Token));
        var contenders = new List<Task<EventResult>>();
        try
        {
            await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(25));
            var pids = new List<int>();
            for (var index = 1; index < 3; index++)
            {
                var intent = intents[index];
                var pid = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                contenders.Add(Task.Run(() => RunAsync(f, intent, contenderPid: pid, cancellationToken: cancellation.Token)));
                pids.Add(await pid.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            }
            await using var observer = new NpgsqlConnection(Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB"));
            await observer.OpenAsync(cancellation.Token);
            using var observeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (true)
            {
                await using var query = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE pid=ANY(@pids) AND wait_event_type='Lock' AND @winner=ANY(pg_blocking_pids(pid))", observer);
                query.Parameters.AddWithValue("pids", pids.ToArray());
                query.Parameters.AddWithValue("winner", gate.ProcessId);
                if ((long)(await query.ExecuteScalarAsync(observeTimeout.Token))! == 2)
                {
                    break;
                }
                await Task.Delay(20, observeTimeout.Token);
            }
            Assert.All(contenders, x => Assert.False(x.IsCompleted));
        }
        finally
        {
            gate.Release.TrySetResult(true);
            var all = Task.WhenAll(contenders.Prepend(winner));
            try { await all.WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (TimeoutException)
            {
                await cancellation.CancelAsync();
                await all.WaitAsync(TimeSpan.FromSeconds(30));
                throw;
            }
        }
        Assert.True((await winner).Success);
        Assert.All(await Task.WhenAll(contenders), x => Assert.Equal("inventory.insufficient_stock", x.Error));
        await AssertInventoryAsync(f, 2m, 0m, 0m, 200m, 2m, null);
        await using var verify = f.Provider.CreateAsyncScope();
        var db = verify.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Equal(1, await db.StockAdjustments.CountAsync(x => x.ActorId == f.ActorId));
        Assert.Equal(1, await db.StockAdjustmentItems.CountAsync(x => x.ProductId == f.ProductId));
        foreach (var intent in intents)
        {
            await AssertOutcomeAsync(db, intent, intent == intents[0]);
        }
    }

    [Fact]
    public async Task A_TwoNegativeAdjustments_ObservedLockWait_OnlyOneConsumesEightOfTen()
    {
        await using var f = await Fixture.CreateAsync(physical: false, quantity: 10m);
        var winner = f.Intent(Operation.Decrease, 8m);
        var loser = f.Intent(Operation.Decrease, 8m);
        var results = await RaceAsync(f, winner, loser);
        Assert.True(results.Winner.Success, results.Winner.Error);
        Assert.False(results.Loser.Success);
        Assert.Equal("inventory.insufficient_stock", results.Loser.Error);
        await AssertInventoryAsync(f, 2m, 0m, 0m, 200m, 2m, null);
        await using var scope = f.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Single(await db.StockAdjustments.Where(x => x.ActorId == f.ActorId).ToListAsync());
        Assert.Single(await db.StockAdjustmentItems.Where(x => x.ProductId == f.ProductId).ToListAsync());
        var movement = await db.InventoryMovements.SingleAsync(x => x.CorrelationId == winner.Id);
        Assert.Equal(8m, await db.InventoryLotConsumptions.Where(x => x.MovementId == movement.Id).SumAsync(x => x.Quantity));
        await AssertOutcomeAsync(db, winner, true);
        await AssertOutcomeAsync(db, loser, false);
    }

    [Fact]
    public async Task B_SetPhysicalCountZeroVsSale_ObservedLockWait_SaleCannotConsumeMissingStock()
    {
        await using var f = await Fixture.CreateAsync(physical: false);
        var winner = f.Intent(Operation.SetZero);
        var loser = f.Intent(Operation.Sale);
        var results = await RaceAsync(f, winner, loser);
        Assert.True(results.Winner.Success, results.Winner.Error);
        Assert.False(results.Loser.Success);
        await AssertInventoryAsync(f, 0m, 0m, 0m, 0m, 0m, null);
        await using var scope = f.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Empty(await db.Sales.Where(x => x.CashierUserId == f.ActorId).ToListAsync());
        var adjustmentMovement = await db.InventoryMovements.SingleAsync(x => x.CorrelationId == winner.Id);
        Assert.Equal(100m, adjustmentMovement.RecognizedLossAmount);
        await AssertOutcomeAsync(db, winner, true);
        await AssertOutcomeAsync(db, loser, false);
    }

    [Fact]
    public async Task C_ConditionTransferVsSale_ObservedLockWait_ExactUnitHasOneCustody()
    {
        await using var f = await Fixture.CreateAsync(physical: true);
        var winner = f.Intent(Operation.Damage);
        var loser = f.Intent(Operation.Sale);
        var results = await RaceAsync(f, winner, loser);
        Assert.True(results.Winner.Success, results.Winner.Error);
        Assert.False(results.Loser.Success);
        await AssertInventoryAsync(f, 0m, 1m, 0m, 100m, 1m, InventoryUnitStatus.Damaged);
        await using var scope = f.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Empty(await db.Sales.Where(x => x.CashierUserId == f.ActorId).ToListAsync());
        Assert.Single(await db.InventoryMovements.Where(x => x.ProductId == f.ProductId && x.MovementType == InventoryMovementType.MarkDamaged).ToListAsync());
        Assert.Equal(0m, await db.InventoryMovements.Where(x => x.ProductId == f.ProductId).SumAsync(x => x.RecognizedLossAmount));
    }

    [Fact]
    public async Task D_ScrapVsWarrantyCustody_ObservedLockWait_CannotSendScrappedUnit()
    {
        await using var f = await Fixture.CreateAsync(physical: true);
        var damage = await RunAsync(f, f.Intent(Operation.Damage));
        Assert.True(damage.Success, damage.Error);
        var winner = f.Intent(Operation.Scrap);
        var loser = f.Intent(Operation.WarrantySend);
        var results = await RaceAsync(f, winner, loser);
        Assert.True(results.Winner.Success, results.Winner.Error);
        Assert.False(results.Loser.Success);
        await AssertInventoryAsync(f, 0m, 0m, 1m, 0m, 0m, InventoryUnitStatus.Scrapped);
        await using var scope = f.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Empty(await db.ShopStockWarrantyCases.Where(x => x.ProductId == f.ProductId).ToListAsync());
        Assert.Empty(await db.WarrantyOperations.Where(x => x.ClientOperationId == loser.Id).ToListAsync());
        Assert.Equal(100m, (await db.InventoryMovements.SingleAsync(x => x.Id == results.Winner.EntityId)).RecognizedLossAmount);
        Assert.False(InventoryUnitAccountingPolicy.GetRule(InventoryUnitStatus.Scrapped).ContributesToProductCostState);
    }

    [Fact]
    public async Task E_TwoThakaProjectsSameUnit_ObservedLockWait_OnlyOneIssueAndCost()
    {
        await using var f = await Fixture.CreateAsync(physical: true);
        var winner = f.Intent(Operation.Issue);
        var loser = f.Intent(Operation.Issue, project: f.SecondProjectId);
        var results = await RaceAsync(f, winner, loser);
        Assert.True(results.Winner.Success, results.Winner.Error);
        Assert.False(results.Loser.Success);
        await AssertInventoryAsync(f, 0m, 0m, 0m, 0m, 0m, InventoryUnitStatus.IssuedThaka);
        await using var scope = f.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Empty(await db.ThakaMaterialIssues.Where(x => x.ProjectId == f.SecondProjectId).ToListAsync());
        await AssertOutcomeAsync(db, winner, true);
        await AssertOutcomeAsync(db, loser, false);
        await AssertProjectAsync(f, 1, 0, 100m, 150m);
    }

    [Fact]
    public async Task F_ThakaIssueVsPurchaseReturn_ObservedLockWait_OnlyOneDerecognition()
    {
        await using var f = await Fixture.CreateAsync(physical: true);
        var winner = f.Intent(Operation.Issue);
        var loser = f.Intent(Operation.PurchaseReturn);
        var results = await RaceAsync(f, winner, loser);
        Assert.True(results.Winner.Success, results.Winner.Error);
        Assert.False(results.Loser.Success);
        await AssertInventoryAsync(f, 0m, 0m, 0m, 0m, 0m, InventoryUnitStatus.IssuedThaka);
        await using var scope = f.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Empty(await db.PurchaseReturns.Where(x => x.PurchaseId == f.PurchaseId).ToListAsync());
        Assert.Empty(await db.SupplierAccountEntries.Where(x => x.ClientOperationId == loser.Id).ToListAsync());
        Assert.Empty(await db.BusinessAuditEvents.Where(x => x.CorrelationId == loser.Id).ToListAsync());
        await AssertOutcomeAsync(db, loser, false);
        await AssertProjectAsync(f, 1, 0, 100m, 150m);
    }

    [Fact]
    public async Task G_ReversalVsNewIssue_ObservedLockWait_RestoredOriginalCanBeIssuedOnce()
    {
        await using var f = await Fixture.CreateAsync(physical: true);
        var original = await RunAsync(f, f.Intent(Operation.Issue));
        Assert.True(original.Success, original.Error);
        f.OriginalIssueId = original.EntityId;
        var winner = f.Intent(Operation.Reverse);
        var loser = f.Intent(Operation.Issue);
        var results = await RaceAsync(f, winner, loser);
        Assert.True(results.Winner.Success, results.Winner.Error);
        Assert.True(results.Loser.Success, results.Loser.Error);
        await AssertInventoryAsync(f, 0m, 0m, 0m, 0m, 0m, InventoryUnitStatus.IssuedThaka);
        await using var scope = f.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Equal(2, await db.ThakaMaterialIssueUnits.CountAsync(x => x.InventoryUnitId == f.UnitId));
        await AssertOutcomeAsync(db, winner, true);
        await AssertOutcomeAsync(db, loser, true);
        await AssertProjectAsync(f, 2, 1, 100m, 150m);
    }

    [Theory]
    [InlineData("SetZero")]
    [InlineData("Scrap")]
    [InlineData("Issue")]
    [InlineData("Reverse")]
    public async Task ActualSqlFlushThenFault_RollsBackStockLotsExactIdentityCostProjectLossAuditAndOutcome(string operationName)
    {
        await using var f = await Fixture.CreateAsync(physical: true);
        var operation = Enum.Parse<Operation>(operationName);
        if (operation == Operation.Scrap)
        {
            var damaged = await RunAsync(f, f.Intent(Operation.Damage));
            Assert.True(damaged.Success, damaged.Error);
        }
        if (operation == Operation.Reverse)
        {
            var issued = await RunAsync(f, f.Intent(Operation.Issue));
            Assert.True(issued.Success, issued.Error);
            f.OriginalIssueId = issued.EntityId;
        }
        var before = await SnapshotAsync(f);
        var fault = new FlushControl { ThrowAfterFlush = true };
        var intent = f.Intent(operation);
        await Assert.ThrowsAsync<InjectedFlushException>(() => RunAsync(f, intent, fault));
        Assert.True(fault.Flushed, "The failure must occur after actual SQL SaveChanges inside the real transaction.");
        Assert.True(fault.ObservedMutation, "The same transaction must read flushed stock/cost/movement changes before the injected fault.");
        Assert.Equal(before, await SnapshotAsync(f));
        await using var scope = f.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Empty(await db.OperationOutcomes.Where(x => x.ClientOperationId == intent.Id).ToListAsync());
        Assert.Empty(await db.BusinessAuditEvents.Where(x => x.CorrelationId == intent.Id).ToListAsync());
        output.WriteLine($"{operation}: actual SQL flush observed, then injected exception; fresh-context stock, lot, units/claims, exact value, loss, project ledger, audits and outcomes equal BEFORE.");
    }

    private async Task<(EventResult Winner, EventResult Loser)> RaceAsync(Fixture f, Intent winner, Intent loser)
    {
        var gate = new FlushControl();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var contenderPid = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var winningTask = Task.Run(() => RunAsync(f, winner, gate, cancellationToken: cancellation.Token));
        Task<EventResult>? losingTask = null;
        try
        {
            await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(25));
            losingTask = Task.Run(() => RunAsync(f, loser, contenderPid: contenderPid, cancellationToken: cancellation.Token));
            var pid = await contenderPid.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await using var observer = new NpgsqlConnection(Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB"));
            await observer.OpenAsync();
            var observed = false;
            for (var attempt = 0; attempt < 200; attempt++)
            {
                await using var query = new NpgsqlCommand("SELECT wait_event_type = 'Lock' AND @winner = ANY(pg_blocking_pids(pid)) FROM pg_stat_activity WHERE pid=@pid", observer);
                query.Parameters.AddWithValue("pid", pid);
                query.Parameters.AddWithValue("winner", gate.ProcessId);
                if (await query.ExecuteScalarAsync() is true)
                {
                    observed = true;
                    break;
                }
                await Task.Delay(20);
            }
            Assert.True(observed, "Contender must demonstrate a real PostgreSQL lock wait behind an uncommitted flushed winner.");
        }
        finally
        {
            gate.Release.TrySetResult(true);
            // Even a failed observation must drain both database operations;
            // an exception must not leave an unobserved contender in the suite.
            var all = losingTask is null ? Task.WhenAll(winningTask) : Task.WhenAll(winningTask, losingTask);
            try
            {
                await all.WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (TimeoutException)
            {
                await cancellation.CancelAsync();
                await all.WaitAsync(TimeSpan.FromSeconds(30));
                throw;
            }
        }
        var w = await winningTask.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(losingTask);
        var l = await losingTask.WaitAsync(TimeSpan.FromSeconds(30));
        output.WriteLine($"Observed PostgreSQL lock wait: {winner.Operation}={w.Success}/{w.Error}; {loser.Operation}={l.Success}/{l.Error}; both tasks completed before persisted assertions.");
        return (w, l);
    }

    private enum Operation { Decrease, SetZero, Damage, Scrap, Sale, WarrantySend, Issue, PurchaseReturn, Reverse }
    private sealed record Intent(Operation Operation, Guid Id, decimal Quantity, Guid ProjectId);
    private sealed record EventResult(bool Success, Guid EntityId, string? Error);
    private static EventResult Convert<T>(Result<T> result, Func<T, Guid> id) =>
        new(result.IsSuccess, result.IsSuccess ? id(result.Value!) : Guid.Empty, result.Error?.Code);

    private static async Task<EventResult> RunAsync(Fixture f, Intent i, FlushControl? control = null, TaskCompletionSource<int>? contenderPid = null, CancellationToken cancellationToken = default)
    {
        await using var scope = f.Provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        await db.Database.OpenConnectionAsync(cancellationToken);
        var pid = ((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID;
        contenderPid?.TrySetResult(pid);
        if (control is not null)
        {
            control.ProcessId = pid;
        }
        var uow = control is null ? services.GetRequiredService<IUnitOfWork>() : new ControlledUnitOfWork(db, f, i, control);
        T Handler<T>() where T : class => ActivatorUtilities.CreateInstance<T>(services, uow);
        var ids = f.Physical ? new[] { f.UnitId } : Array.Empty<Guid>();
        switch (i.Operation)
        {
            case Operation.SetZero:
            case Operation.Decrease:
                return Convert(await Handler<CreateStockAdjustmentHandler>().HandleAsync(new CreateStockAdjustmentCommand(
                    i.Operation == Operation.SetZero ? StockAdjustmentMode.SetPhysicalCount : StockAdjustmentMode.Delta,
                    StockAdjustmentReason.PhysicalCountCorrection,
                    [new StockAdjustmentItemCommand(f.ProductId, f.ProductUnitId, StockAdjustmentDirection.Decrease,
                        InventoryBucket.Sellable, i.Operation == Operation.SetZero ? 0m : i.Quantity, null, InventoryUnitIds: ids)],
                    f.ActorId, i.Id), cancellationToken), x => x);
            case Operation.Damage:
            case Operation.Scrap:
                return Convert(await Handler<TransferInventoryConditionHandler>().HandleAsync(new TransferInventoryConditionCommand(
                    f.ProductId, i.Operation == Operation.Damage ? InventoryBucket.Sellable : InventoryBucket.Damaged,
                    i.Operation == Operation.Damage ? InventoryBucket.Damaged : InventoryBucket.Scrap,
                    i.Quantity, f.ActorId, "Hostile Pass 2 certification", ReferenceId: i.Id, InventoryUnitIds: ids), cancellationToken), x => x);
            case Operation.Sale:
                return Convert(await Handler<CompleteSaleHandler>().HandleAsync(new CompleteSaleCommand(i.Id, null, f.ActorId,
                    null, 0m, SalePaymentMethod.Bank, 150m * i.Quantity, "owned test settlement", null,
                    [new CompleteSaleLineInput(f.ProductId, f.ProductUnitId, i.Quantity, 150m, ids)]), cancellationToken), x => x.SaleId);
            case Operation.WarrantySend:
                return Convert(await Handler<SendShopStockToSupplierWarrantyHandler>().HandleAsync(new SendShopStockToSupplierWarrantyCommand(
                    f.ProductId, InventoryBucket.Damaged, i.Quantity, f.SupplierId, f.PurchaseItemId,
                    "Owned test damaged unit", f.ActorId, i.Id, ids), cancellationToken), x => x);
            case Operation.Issue:
                return Convert(await Handler<IssueThakaMaterialHandler>().HandleAsync(new IssueThakaMaterialCommand(i.Id,
                    i.ProjectId, f.ActorId, "Hostile Pass 2 issue", [new IssueThakaMaterialLineInput(f.ProductId,
                        f.ProductUnitId, i.Quantity, 150m, ids)]), cancellationToken), x => x.MaterialIssueId);
            case Operation.PurchaseReturn:
                return Convert(await Handler<CreatePurchaseReturnHandler>().HandleAsync(new CreatePurchaseReturnCommand(
                    f.PurchaseId, "Owned test return", null, PurchaseReturnSettlementMode.External, f.ActorId, i.Id,
                    [new PurchaseReturnLineInput(f.PurchaseItemId, i.Quantity, null, ids)]), cancellationToken), x => x.PurchaseReturnId);
            case Operation.Reverse:
                return Convert(await Handler<ReverseThakaMaterialHandler>().HandleAsync(new ReverseThakaMaterialCommand(
                    i.Id, f.ProjectId, f.OriginalIssueId, "Owned test material restoration", f.ActorId), cancellationToken), x => x.ReversalId);
            default:
                throw new ArgumentOutOfRangeException(nameof(i));
        }
    }

    private static async Task AssertOutcomeAsync(EdgeRetailsDbContext db, Intent intent, bool succeeded)
    {
        var outcome = await db.OperationOutcomes.SingleAsync(x => x.ClientOperationId == intent.Id);
        Assert.Equal(succeeded, outcome.Status == EdgeRetails.Domain.Operations.OperationOutcomeStatus.Succeeded);
        Assert.Equal(succeeded, outcome.WasCommitted);
    }

    private static async Task AssertInventoryAsync(Fixture f, decimal sellable, decimal damaged, decimal scrap,
        decimal value, decimal costed, InventoryUnitStatus? status)
    {
        await using var scope = f.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var balance = await db.StockBalances.SingleAsync(x => x.ProductId == f.ProductId);
        Assert.Equal(sellable, balance.SellableQty);
        Assert.Equal(damaged, balance.DamagedQty);
        Assert.Equal(scrap, balance.ScrapQty);
        Assert.Equal(0m, balance.DefectiveQty);
        Assert.Equal(0m, balance.WithSupplierQty);
        var costs = await db.ProductCostStates.SingleAsync(x => x.ProductId == f.ProductId);
        Assert.Equal(value, costs.TotalInventoryCost);
        Assert.Equal(costed, costs.CostedQty);
        var lots = await db.InventoryLots.Where(x => x.ProductId == f.ProductId).Select(x => x.Id).ToArrayAsync();
        var buckets = await db.InventoryLotBucketBalances.Where(x => lots.Contains(x.LotId)).ToListAsync();
        Assert.All(buckets, x => Assert.True(x.Quantity >= 0m));
        Assert.Equal(sellable, buckets.Where(x => x.StockBucket == InventoryBucket.Sellable).Sum(x => x.Quantity));
        Assert.Equal(damaged, buckets.Where(x => x.StockBucket == InventoryBucket.Damaged).Sum(x => x.Quantity));
        Assert.Equal(scrap, buckets.Where(x => x.StockBucket == InventoryBucket.Scrap).Sum(x => x.Quantity));
        if (status.HasValue)
        {
            var unit = Assert.Single(await db.InventoryUnits.Where(x => x.ProductId == f.ProductId).ToListAsync());
            Assert.Equal(f.UnitId, unit.Id);
            Assert.Equal(status.Value, unit.Status);
            Assert.Equal(f.TrackingCode, unit.TrackingCode);
            Assert.Equal(f.ItemSequence, unit.ItemSequence);
            Assert.Equal(f.SupplierProductId, unit.SupplierProductId);
            Assert.Equal(f.SerialNumber, unit.SerialNumber);
            Assert.Equal(f.PurchaseItemId, unit.SourcePurchaseItemId);
            Assert.Equal(100m, unit.AcquisitionCost);
            Assert.Equal(f.SequenceNext, (await db.SupplierProducts.SingleAsync(x => x.Id == f.SupplierProductId)).NextItemSequence);
        }
    }

    private static async Task AssertProjectAsync(Fixture f, int issueCount, int reversalCount, decimal effectiveCost, decimal effectiveCharge)
    {
        await using var scope = f.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var issues = await db.ThakaMaterialIssues.Where(x => x.ProjectId == f.ProjectId).ToListAsync();
        var reversals = await db.ThakaMaterialReversals.Where(x => x.ProjectId == f.ProjectId).ToListAsync();
        Assert.Equal(issueCount, issues.Count);
        Assert.Equal(reversalCount, reversals.Count);
        Assert.Equal(effectiveCost, issues.Sum(x => x.TotalCost) - reversals.Sum(x => x.RestoredCost));
        Assert.Equal(effectiveCharge, issues.Sum(x => x.TotalCharge) - reversals.Sum(x => x.ReversedCharge));
        var detail = await scope.ServiceProvider.GetRequiredService<IThakaReadService>().GetProjectAsync(f.ProjectId, default);
        Assert.NotNull(detail);
        Assert.Equal(effectiveCharge, detail.Project.MaterialValue);
        Assert.Equal(issueCount, detail.Materials.Count);
        Assert.Equal(reversalCount, detail.Materials.Count(x => x.IsReversed));
    }

    private static async Task<string> SnapshotAsync(Fixture f)
    {
        await using var scope = f.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var lots = await db.InventoryLots.AsNoTracking().Where(x => x.ProductId == f.ProductId).OrderBy(x => x.Id).ToArrayAsync();
        var lotIds = lots.Select(x => x.Id).ToArray();
        var moves = await db.InventoryMovements.AsNoTracking().Where(x => x.ProductId == f.ProductId).OrderBy(x => x.Id).ToArrayAsync();
        var moveIds = moves.Select(x => x.Id).ToArray();
        var issues = await db.ThakaMaterialIssues.AsNoTracking().Where(x => x.ProjectId == f.ProjectId || x.ProjectId == f.SecondProjectId).OrderBy(x => x.Id).ToArrayAsync();
        var issueIds = issues.Select(x => x.Id).ToArray();
        var items = await db.ThakaMaterialIssueItems.AsNoTracking().Where(x => issueIds.Contains(x.MaterialIssueId)).OrderBy(x => x.Id).ToArrayAsync();
        var itemIds = items.Select(x => x.Id).ToArray();
        return JsonSerializer.Serialize(new
        {
            lots, moves, issues, items,
            Stock = await db.StockBalances.AsNoTracking().Where(x => x.ProductId == f.ProductId).OrderBy(x => x.Id).ToArrayAsync(),
            Cost = await db.ProductCostStates.AsNoTracking().Where(x => x.ProductId == f.ProductId).OrderBy(x => x.Id).ToArrayAsync(),
            Buckets = await db.InventoryLotBucketBalances.AsNoTracking().Where(x => lotIds.Contains(x.LotId)).OrderBy(x => x.Id).ToArrayAsync(),
            Units = await db.InventoryUnits.AsNoTracking().Where(x => x.ProductId == f.ProductId).OrderBy(x => x.Id).ToArrayAsync(),
            Claims = await db.InventoryUnitIdentityClaims.AsNoTracking().Where(x => x.InventoryUnitId == f.UnitId).OrderBy(x => x.Id).ToArrayAsync(),
            Sequence = await db.SupplierProducts.AsNoTracking().Where(x => x.Id == f.SupplierProductId).OrderBy(x => x.Id).ToArrayAsync(),
            Effects = await db.InventoryMovementEffects.AsNoTracking().Where(x => moveIds.Contains(x.MovementId)).OrderBy(x => x.Id).ToArrayAsync(),
            Links = await db.InventoryMovementUnits.AsNoTracking().Where(x => moveIds.Contains(x.MovementId)).OrderBy(x => x.Id).ToArrayAsync(),
            Consumptions = await db.InventoryLotConsumptions.AsNoTracking().Where(x => moveIds.Contains(x.MovementId)).OrderBy(x => x.Id).ToArrayAsync(),
            Adjustments = await db.StockAdjustments.AsNoTracking().Where(x => x.ActorId == f.ActorId).OrderBy(x => x.Id).ToArrayAsync(),
            AdjustmentItems = await db.StockAdjustmentItems.AsNoTracking().Where(x => x.ProductId == f.ProductId).OrderBy(x => x.Id).ToArrayAsync(),
            Project = await db.ThakaProjects.AsNoTracking().Where(x => x.Id == f.ProjectId || x.Id == f.SecondProjectId).OrderBy(x => x.Id).ToArrayAsync(),
            IssueUnits = await db.ThakaMaterialIssueUnits.AsNoTracking().Where(x => itemIds.Contains(x.MaterialIssueItemId)).OrderBy(x => x.Id).ToArrayAsync(),
            Reversals = await db.ThakaMaterialReversals.AsNoTracking().Where(x => x.ProjectId == f.ProjectId).OrderBy(x => x.Id).ToArrayAsync(),
            Audit = await db.BusinessAuditEvents.AsNoTracking().Where(x => x.ActorId == f.ActorId).OrderBy(x => x.Id).ToArrayAsync(),
            Outcomes = await db.OperationOutcomes.AsNoTracking().Where(x => x.ActorUserId == f.ActorId).OrderBy(x => x.ClientOperationId).ToArrayAsync()
        });
    }

    private sealed class InjectedFlushException : Exception { }
    private sealed class FlushControl
    {
        public TaskCompletionSource<bool> Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ThrowAfterFlush { get; init; }
        public bool Flushed { get; set; }
        public bool ObservedMutation { get; set; }
        public int ProcessId { get; set; }
    }

    private sealed class ControlledUnitOfWork(EdgeRetailsDbContext db, Fixture f, Intent intent, FlushControl control) : IUnitOfWork
    {
        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        {
            var count = await db.SaveChangesAsync(cancellationToken);
            control.Flushed = true;
            // The canonical outcome ledger may flush the scoped context before
            // IUnitOfWork is reached. Read the actual operation row rather than
            // relying on Added state surviving that earlier ambient SQL flush.
            control.ObservedMutation =
                await db.InventoryMovements.AsNoTracking().AnyAsync(x => x.ProductId == f.ProductId &&
                    (x.CorrelationId == intent.Id || x.ReferenceId == intent.Id), cancellationToken) &&
                await db.StockBalances.AsNoTracking().AnyAsync(x => x.ProductId == f.ProductId, cancellationToken);
            Assert.True(control.ObservedMutation, "Actual SQL flush did not persist the expected transaction-local inventory movement.");
            if (control.ThrowAfterFlush)
            {
                throw new InjectedFlushException();
            }
            control.Reached.TrySetResult(true);
            await control.Release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            return count;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required ServiceProvider Provider { get; init; }
        public required bool Physical { get; init; }
        public required Guid ProductId { get; init; }
        public required Guid ProductUnitId { get; init; }
        public required Guid SupplierId { get; init; }
        public required Guid ActorId { get; init; }
        public required Guid PurchaseId { get; init; }
        public required Guid PurchaseItemId { get; init; }
        public required Guid ProjectId { get; init; }
        public required Guid SecondProjectId { get; init; }
        public Guid UnitId { get; init; }
        public Guid? SupplierProductId { get; init; }
        public string? TrackingCode { get; init; }
        public string? SerialNumber { get; init; }
        public long ItemSequence { get; init; }
        public long SequenceNext { get; init; }
        public Guid OriginalIssueId { get; set; }
        public Intent Intent(Operation operation, decimal quantity = 1m, Guid? project = null) => new(operation, Guid.NewGuid(), quantity, project ?? ProjectId);

        public static async Task<Fixture> CreateAsync(bool physical, decimal quantity = 1m)
        {
            var provider = Phase2PostgresTestHarness.BuildProvider();
            try
            {
                await using var scope = provider.CreateAsyncScope();
                var services = scope.ServiceProvider;
                var db = services.GetRequiredService<EdgeRetailsDbContext>();
                await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
                Guid productId, productUnitId, supplierId, actorId;
                if (physical)
                {
                    var seed = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db, defaultSalePrice: 150m);
                    productId = seed.ProductId; productUnitId = seed.ProductUnitId; supplierId = seed.SupplierId; actorId = seed.ActorId;
                }
                else
                {
                    var seed = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db, defaultSalePrice: 150m);
                    productId = seed.ProductId; productUnitId = seed.ProductUnitId; supplierId = seed.SupplierId; actorId = seed.ActorId;
                }
                var serial = "P7-HOSTILE-" + Guid.NewGuid().ToString("N");
                var purchase = await services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new CreatePurchaseCommand(
                    supplierId, "P7-" + Guid.NewGuid().ToString("N"), DateOnly.FromDateTime(DateTime.UtcNow), null,
                    0m, PurchaseSettlementMode.External, actorId, Guid.NewGuid(),
                    [new CreatePurchaseLineInput(productId, productUnitId, quantity, 100m, 150m,
                        physical ? [new SerializedIdentityInput(serial)] : [])], InitialPaymentAmount: 0m, ReceiveStockImmediately: true), default);
                Assert.True(purchase.IsSuccess, purchase.Error?.Message);
                Assert.NotNull(purchase.Value);
                var purchaseId = purchase.Value.PurchaseId;
                var item = await db.PurchaseItems.SingleAsync(x => x.PurchaseId == purchaseId);
                var customer = await Phase2PostgresTestHarness.SeedCustomerAsync(db);
                var project = new ThakaProject { ProjectNumber = "P7A-" + Guid.NewGuid().ToString("N"), CustomerId = customer.Id,
                    ProjectName = "Hostile project A", Status = ThakaProjectStatus.Active, CreatedBy = actorId, CreatedAt = DateTimeOffset.UtcNow };
                var secondProject = new ThakaProject { ProjectNumber = "P7B-" + Guid.NewGuid().ToString("N"), CustomerId = customer.Id,
                    ProjectName = "Hostile project B", Status = ThakaProjectStatus.Active, CreatedBy = actorId, CreatedAt = DateTimeOffset.UtcNow };
                db.ThakaProjects.AddRange(project, secondProject);
                await db.SaveChangesAsync();
                var unit = physical ? await db.InventoryUnits.SingleAsync(x => x.ProductId == productId) : null;
                var sequence = physical ? (await db.SupplierProducts.SingleAsync(x => x.Id == unit!.SupplierProductId)).NextItemSequence : 0;
                return new Fixture { Provider = provider, Physical = physical, ProductId = productId, ProductUnitId = productUnitId,
                    SupplierId = supplierId, ActorId = actorId, PurchaseId = purchaseId, PurchaseItemId = item.Id,
                    ProjectId = project.Id, SecondProjectId = secondProject.Id, UnitId = unit?.Id ?? Guid.Empty,
                    SupplierProductId = unit?.SupplierProductId, TrackingCode = unit?.TrackingCode, SerialNumber = unit?.SerialNumber,
                    ItemSequence = unit?.ItemSequence ?? 0, SequenceNext = sequence };
            }
            catch
            {
                await provider.DisposeAsync();
                throw;
            }
        }

        public ValueTask DisposeAsync() => Provider.DisposeAsync();
    }
}
