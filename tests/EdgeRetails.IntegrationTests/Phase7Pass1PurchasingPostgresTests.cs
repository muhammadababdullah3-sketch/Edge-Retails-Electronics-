using System.Text.Json;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Operations;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace EdgeRetails.IntegrationTests;

[Collection("Phase2PostgresIntegration")]
public sealed class Phase7Pass1PurchasingPostgresTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ZeroReceiptDeferredVoid_PersistsVoidedAuditAndLedgerWithoutInventingInventory()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var scope = fixture.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var item = await db.PurchaseItems.SingleAsync(x => x.PurchaseId == fixture.PurchaseId);
        Assert.Equal(10m, item.BaseQuantity);
        await AssertNoInventoryAsync(db, fixture.Product.ProductId);
        var operationId = Guid.NewGuid();
        var result = await scope.ServiceProvider.GetRequiredService<VoidPurchaseHandler>().HandleAsync(
            new(fixture.PurchaseId, operationId, fixture.Product.ActorId, "Zero receipt certification"), default);
        Assert.True(result.IsSuccess, result.Error?.Message);

        await using var readScope = fixture.Provider.CreateAsyncScope();
        var persisted = readScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var purchase = await persisted.Purchases.SingleAsync(x => x.Id == fixture.PurchaseId);
        Assert.Equal(PurchaseStatus.Voided, purchase.Status);
        var purchaseVoid = await persisted.PurchaseVoids.SingleAsync(x => x.PurchaseId == fixture.PurchaseId);
        Assert.Equal(result.Value!.PurchaseVoidId, purchaseVoid.Id);
        Assert.Equal(operationId, purchaseVoid.ClientOperationId);
        Assert.Equal(fixture.Product.ActorId, purchaseVoid.VoidedBy);
        Assert.Null(purchaseVoid.CashDrawerReversalAmount);
        await AssertNoInventoryAsync(persisted, fixture.Product.ProductId);
        var entries = await persisted.SupplierAccountEntries.Where(x => x.SupplierId == fixture.Product.SupplierId).ToListAsync();
        Assert.Equal(2, entries.Count);
        Assert.Equal(0m, entries.Sum(x => x.SignedAmount));
        var reversal = Assert.Single(entries, x => x.EntryType == SupplierAccountEntryType.PurchaseVoidReversal);
        Assert.Equal(-10000m, reversal.SignedAmount);
        Assert.Equal(purchaseVoid.Id, reversal.ReferenceId);
        Assert.Equal(operationId, reversal.ClientOperationId);
        var audit = await persisted.BusinessAuditEvents.SingleAsync(x => x.CorrelationId == operationId);
        Assert.Equal("PURCHASE_VOIDED", audit.Action);
        Assert.Equal("PURCHASE", audit.EntityType);
        Assert.Equal(fixture.PurchaseId, audit.EntityId);
        Assert.Equal(fixture.Product.ActorId, audit.ActorId);
        output.WriteLine("D-VOID-1 persisted: ordered=10, received=0, inventory rows/delta=0, status=Voided, ledger lifecycle=0, correlated audit=1.");
    }

    [Fact]
    public async Task PartialReceiptVoid_FailsDeterministicallyAndPreservesEveryCommittedAggregateRow()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using (var intakeScope = fixture.Provider.CreateAsyncScope())
        {
            var intake = await intakeScope.ServiceProvider.GetRequiredService<ReceiveProductIntakeHandler>().HandleAsync(
                fixture.Intake(4m), default);
            Assert.True(intake.IsSuccess, intake.Error?.Message);
        }

        var before = await CaptureAggregateAsync(fixture);
        await using (var scope = fixture.Provider.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<VoidPurchaseHandler>().HandleAsync(
                fixture.Void(), default);
            Assert.False(result.IsSuccess);
            Assert.Equal("purchasing.void_partial_receipt_forbidden", result.Error!.Code);
        }
        Assert.Equal(before, await CaptureAggregateAsync(fixture));
        await using var readScope = fixture.Provider.CreateAsyncScope();
        var db = readScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Equal(4m, (await db.StockBalances.SingleAsync(x => x.ProductId == fixture.Product.ProductId)).SellableQty);
        Assert.Equal(4m, (await db.InventoryLots.SingleAsync(x => x.ProductId == fixture.Product.ProductId)).ReceivedQuantity);
        Assert.Equal(4000m, (await db.ProductCostStates.SingleAsync(x => x.ProductId == fixture.Product.ProductId)).TotalInventoryCost);
        output.WriteLine("Partial receipt rejection: ordered=10, intake=4, purchasing.void_partial_receipt_forbidden; complete persisted aggregate identical before/after.");
    }

    [Fact]
    public async Task CashPaidDeferredVoid_Reconciles10000And4000AndSameOperationReplayPersistsOnce()
    {
        await using var fixture = await Fixture.CreateAsync(cashPayment: true);
        Guid[] originalEntryIds;
        Guid paymentId;
        Guid originalCashId;
        await using (var scope = fixture.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var entries = await db.SupplierAccountEntries.Where(x => x.SupplierId == fixture.Product.SupplierId).ToListAsync();
            originalEntryIds = entries.Select(x => x.Id).ToArray();
            Assert.Equal(2, entries.Count);
            Assert.Equal(10000m, Assert.Single(entries, x => x.EntryType == SupplierAccountEntryType.Purchase).SignedAmount);
            Assert.Equal(-4000m, Assert.Single(entries, x => x.EntryType == SupplierAccountEntryType.SupplierPayment).SignedAmount);
            Assert.Equal(6000m, entries.Sum(x => x.SignedAmount));
            var payment = await db.SupplierPayments.SingleAsync(x => x.SupplierId == fixture.Product.SupplierId);
            paymentId = payment.Id;
            Assert.Equal(SupplierSettlementStatus.Posted, payment.Status);
            Assert.Equal(4000m, payment.Amount);
            Assert.Equal(fixture.PurchaseOperationId, payment.ClientOperationId);
            var originalCash = await db.CashMovements.SingleAsync(x => x.SourceId == payment.Id);
            originalCashId = originalCash.Id;
            Assert.Equal(CashMovementType.SupplierPaymentCashOut, originalCash.MovementType);
            Assert.Equal(CashMovementDirection.Out, originalCash.Direction);
            Assert.Equal(4000m, originalCash.Amount);
            Assert.Equal(fixture.SessionId, originalCash.CashSessionId);
        }

        var command = fixture.Void();
        Guid voidId;
        await using (var scope = fixture.Provider.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<VoidPurchaseHandler>().HandleAsync(command, default);
            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.False(result.Value!.WasExisting);
            voidId = result.Value.PurchaseVoidId;
        }
        var committed = await CaptureAggregateAsync(fixture);
        await using (var replayScope = fixture.Provider.CreateAsyncScope())
        {
            var replay = await replayScope.ServiceProvider.GetRequiredService<VoidPurchaseHandler>().HandleAsync(command, default);
            Assert.True(replay.IsSuccess, replay.Error?.Message);
            Assert.True(replay.Value!.WasExisting);
            Assert.Equal(voidId, replay.Value.PurchaseVoidId);
        }
        Assert.Equal(committed, await CaptureAggregateAsync(fixture));

        await using var readScope = fixture.Provider.CreateAsyncScope();
        var persisted = readScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var ledger = await persisted.SupplierAccountEntries.Where(x => x.SupplierId == fixture.Product.SupplierId).ToListAsync();
        Assert.Equal(3, ledger.Count);
        Assert.All(originalEntryIds, id => Assert.Contains(ledger, x => x.Id == id));
        Assert.Equal(-4000m, ledger.Sum(x => x.SignedAmount));
        Assert.Empty(await persisted.SupplierPaymentReversals.Where(x => x.SupplierPaymentId == paymentId).ToListAsync());
        Assert.DoesNotContain(ledger, x => x.EntryType == SupplierAccountEntryType.SupplierPaymentReversal);
        Assert.Empty(await persisted.SupplierRefunds.Where(x => x.SupplierId == fixture.Product.SupplierId).ToListAsync());
        var liabilityVoid = Assert.Single(ledger, x => x.EntryType == SupplierAccountEntryType.PurchaseVoidReversal);
        Assert.Equal(-10000m, liabilityVoid.SignedAmount);
        Assert.Equal(voidId, liabilityVoid.ReferenceId);
        Assert.Equal(SupplierSettlementStatus.Posted, (await persisted.SupplierPayments.SingleAsync(x => x.Id == paymentId)).Status);
        Assert.Null((await persisted.PurchaseVoids.SingleAsync(x => x.Id == voidId)).CashDrawerReversalAmount);
        var cash = await persisted.CashMovements.Where(x => x.CashSessionId == fixture.SessionId).ToListAsync();
        Assert.Single(cash);
        Assert.Contains(cash, x => x.Id == originalCashId);
        Assert.Equal(CashMovementType.SupplierPaymentCashOut, cash[0].MovementType);
        Assert.Equal(CashMovementDirection.Out, cash[0].Direction);
        Assert.Equal(4000m, cash[0].Amount);
        Assert.Equal(paymentId, cash[0].SourceId);
        Assert.Equal(-4000m, cash.Sum(x => x.Direction == CashMovementDirection.In ? x.Amount : -x.Amount));
        Assert.Equal(1, await persisted.BusinessAuditEvents.CountAsync(x => x.CorrelationId == command.ClientOperationId));
        await AssertNoInventoryAsync(persisted, fixture.Product.ProductId);
        output.WriteLine("Resolution01 C02: +10000 purchase -4000 actual payment -10000 void = supplier credit4000; cash -4000 retained; no invented reversal/refund; inventory0; replay changed no persisted row.");
    }

    [Fact]
    public async Task CashPaidVoidWithoutOpenSession_PreservesActualPaymentAndDoesNotRequireDrawer()
    {
        await using var fixture = await Fixture.CreateAsync(cashPayment: true);
        await fixture.CloseOwnedSessionAsync();
        await using var scope = fixture.Provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<VoidPurchaseHandler>().HandleAsync(fixture.Void(), default);
        Assert.True(result.IsSuccess, result.Error?.Message);
        await using var readScope = fixture.Provider.CreateAsyncScope();
        var db = readScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Equal(PurchaseStatus.Voided, (await db.Purchases.SingleAsync(x => x.Id == fixture.PurchaseId)).Status);
        var payment = await db.SupplierPayments.SingleAsync(x => x.SupplierId == fixture.Product.SupplierId);
        Assert.Equal(SupplierSettlementStatus.Posted, payment.Status);
        Assert.Empty(await db.SupplierPaymentReversals.Where(x => x.SupplierPaymentId == payment.Id).ToListAsync());
        var purchaseVoid = await db.PurchaseVoids.SingleAsync(x => x.PurchaseId == fixture.PurchaseId);
        Assert.Null(purchaseVoid.CashDrawerReversalAmount);
        var ledger = await db.SupplierAccountEntries.Where(x => x.SupplierId == fixture.Product.SupplierId).ToListAsync();
        Assert.Equal(3, ledger.Count);
        Assert.Equal(-4000m, ledger.Sum(x => x.SignedAmount));
        var cash = await db.CashMovements.SingleAsync(x => x.CashSessionId == fixture.SessionId);
        Assert.Equal(CashMovementType.SupplierPaymentCashOut, cash.MovementType);
        Assert.Equal(CashMovementDirection.Out, cash.Direction);
        Assert.Equal(4000m, cash.Amount);
        Assert.Equal(payment.Id, cash.SourceId);
        Assert.Empty(await db.SupplierRefunds.Where(x => x.SupplierId == fixture.Product.SupplierId).ToListAsync());
        Assert.Equal(1, await db.BusinessAuditEvents.CountAsync(x => x.CorrelationId == purchaseVoid.ClientOperationId));
        await AssertNoInventoryAsync(db, fixture.Product.ProductId);
        output.WriteLine("Resolution01 C02: default void succeeds without open drawer; supplier credit4000 and original cash/payment retained; no compensation/refund/reversal.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureAfterActualSqlFlush_RollsBackFinancialAuditAndPhysicalEffects(bool physicalReceipt)
    {
        await using var fixture = await Fixture.CreateAsync(cashPayment: true, physicalReceipt: physicalReceipt);
        var before = await CaptureAggregateAsync(fixture);
        await using var scope = fixture.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var failure = new FlushThenFailUnitOfWork(db);
        var handler = ActivatorUtilities.CreateInstance<VoidPurchaseHandler>(scope.ServiceProvider, failure);
        var command = fixture.Void();
        var result = await handler.HandleAsync(command, default);
        Assert.True(failure.Flushed, "Test failure boundary was never reached after real SQL SaveChanges.");
        Assert.False(result.IsSuccess);
        Assert.Equal("certification.after_sql_flush", result.Error!.Code);
        Assert.Equal(before, await CaptureAggregateAsync(fixture));
        output.WriteLine($"Rollback after actual PostgreSQL SaveChanges (physicalReceipt={physicalReceipt}): purchase, stock, lot, unit/claims, cost, ledger, payment reversal, cash, audit and outcome exactly unchanged.");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task VoidVsPartialIntake_OverlappingPostgresLocksAllowOneAuthority(bool voidWins)
    {
        await using var fixture = await Fixture.CreateAsync();
        using var gate = new FlushGate();
        var voidCommand = fixture.Void();
        var intakeCommand = fixture.Intake(4m);
        var loserPid = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<Result<VoidPurchaseResult>> voidTask;
        Task<Result<ReceiveProductIntakeResult>> intakeTask;

        async Task<Result<VoidPurchaseResult>> RunVoidAsync(bool winner)
        {
            await using var scope = fixture.Provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            await db.Database.OpenConnectionAsync();
            if (!winner)
            {
                loserPid.SetResult(((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID);
            }
            var handler = winner
                ? ActivatorUtilities.CreateInstance<VoidPurchaseHandler>(scope.ServiceProvider, new BlockingFlushUnitOfWork(db, gate))
                : scope.ServiceProvider.GetRequiredService<VoidPurchaseHandler>();
            return await handler.HandleAsync(voidCommand, default);
        }

        async Task<Result<ReceiveProductIntakeResult>> RunIntakeAsync(bool winner)
        {
            await using var scope = fixture.Provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            await db.Database.OpenConnectionAsync();
            if (!winner)
            {
                loserPid.SetResult(((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID);
            }
            var handler = winner
                ? ActivatorUtilities.CreateInstance<ReceiveProductIntakeHandler>(scope.ServiceProvider, new BlockingFlushUnitOfWork(db, gate))
                : scope.ServiceProvider.GetRequiredService<ReceiveProductIntakeHandler>();
            return await handler.HandleAsync(intakeCommand, default);
        }

        if (voidWins)
        {
            voidTask = Task.Run(() => RunVoidAsync(true));
            await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
            intakeTask = Task.Run(() => RunIntakeAsync(false));
        }
        else
        {
            intakeTask = Task.Run(() => RunIntakeAsync(true));
            await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
            voidTask = Task.Run(() => RunVoidAsync(false));
        }

        try
        {
            var pid = await loserPid.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await AssertPostgresLockWaitAsync(pid);
        }
        finally
        {
            gate.Release.Set();
        }
        await Task.WhenAll(voidTask, intakeTask).WaitAsync(TimeSpan.FromSeconds(30));
        var voidResult = await voidTask;
        var intakeResult = await intakeTask;
        Assert.Equal(voidWins, voidResult.IsSuccess);
        Assert.Equal(!voidWins, intakeResult.IsSuccess);
        Assert.Equal(1, new[] { voidResult.IsSuccess, intakeResult.IsSuccess }.Count(x => x));
        if (voidWins)
        {
            Assert.Equal("purchasing.purchase_voided", intakeResult.Error!.Code);
        }
        else
        {
            Assert.Equal("purchasing.void_partial_receipt_forbidden", voidResult.Error!.Code);
        }

        await using var readScope = fixture.Provider.CreateAsyncScope();
        var persisted = readScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Equal(voidWins ? PurchaseStatus.Voided : PurchaseStatus.Completed,
            (await persisted.Purchases.SingleAsync(x => x.Id == fixture.PurchaseId)).Status);
        Assert.Equal(voidWins ? 1 : 0, await persisted.PurchaseVoids.CountAsync(x => x.PurchaseId == fixture.PurchaseId));
        var entries = await persisted.SupplierAccountEntries.Where(x => x.SupplierId == fixture.Product.SupplierId).ToListAsync();
        Assert.Equal(voidWins ? 2 : 1, entries.Count);
        Assert.Equal(voidWins ? 0m : 10000m, entries.Sum(x => x.SignedAmount));
        Assert.Equal(voidWins ? 0 : 1, await persisted.InventoryMovements.CountAsync(x => x.ProductId == fixture.Product.ProductId));
        if (voidWins)
        {
            await AssertNoInventoryAsync(persisted, fixture.Product.ProductId);
        }
        else
        {
            Assert.Equal(4m, (await persisted.StockBalances.SingleAsync(x => x.ProductId == fixture.Product.ProductId)).SellableQty);
            var lot = await persisted.InventoryLots.SingleAsync(x => x.ProductId == fixture.Product.ProductId);
            Assert.Equal(4m, lot.ReceivedQuantity);
            Assert.Equal(4m, (await persisted.InventoryLotBucketBalances.SingleAsync(x => x.LotId == lot.Id)).Quantity);
            Assert.Equal(4000m, (await persisted.ProductCostStates.SingleAsync(x => x.ProductId == fixture.Product.ProductId)).TotalInventoryCost);
            var movement = await persisted.InventoryMovements.SingleAsync(x => x.ProductId == fixture.Product.ProductId);
            Assert.Equal(intakeCommand.ClientOperationId, movement.CorrelationId);
            Assert.Equal(4m, (await persisted.InventoryMovementEffects.SingleAsync(x => x.MovementId == movement.Id)).QuantityDelta);
        }
        Assert.Equal(voidWins ? 1 : 0, await persisted.BusinessAuditEvents.CountAsync(x => x.CorrelationId == voidCommand.ClientOperationId));
        Assert.Equal(0, await persisted.BusinessAuditEvents.CountAsync(x => x.CorrelationId == intakeCommand.ClientOperationId));
        var intakeOutcome = await persisted.OperationOutcomes.SingleAsync(x => x.ClientOperationId == intakeCommand.ClientOperationId);
        Assert.Equal(voidWins ? OperationOutcomeStatus.Failed : OperationOutcomeStatus.Succeeded, intakeOutcome.Status);
        if (!voidWins)
        {
            var receipt = await persisted.InventoryMovements.SingleAsync(x => x.ProductId == fixture.Product.ProductId);
            Assert.Equal(receipt.Id, intakeOutcome.ResultEntityId);
        }
        output.WriteLine($"Real overlapping PostgreSQL row/advisory lock wait observed; winner={(voidWins ? "VoidPurchase" : "ReceiveProductIntake(4 of 10)")}; loser rejected; no duplicate receipt, partial ledger or negative inventory.");
    }

    private static async Task AssertPostgresLockWaitAsync(int pid)
    {
        await using var connection = new NpgsqlConnection(Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB"));
        await connection.OpenAsync();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var query = new NpgsqlCommand("SELECT wait_event_type FROM pg_stat_activity WHERE pid = @pid", connection);
            query.Parameters.AddWithValue("pid", pid);
            if (await query.ExecuteScalarAsync() is string value && value == "Lock")
            {
                return;
            }
            await Task.Delay(20);
        }
        Assert.Fail("Concurrent loser did not demonstrate an actual PostgreSQL lock wait while the winner held an uncommitted mutation.");
    }

    private static async Task AssertNoInventoryAsync(EdgeRetailsDbContext db, Guid productId)
    {
        Assert.Empty(await db.StockBalances.Where(x => x.ProductId == productId).ToListAsync());
        Assert.Empty(await db.InventoryLots.Where(x => x.ProductId == productId).ToListAsync());
        Assert.Empty(await db.InventoryUnits.Where(x => x.ProductId == productId).ToListAsync());
        Assert.Empty(await db.InventoryMovements.Where(x => x.ProductId == productId).ToListAsync());
        Assert.Empty(await db.ProductCostStates.Where(x => x.ProductId == productId).ToListAsync());
    }

    private static async Task<string> CaptureAggregateAsync(Fixture fixture)
    {
        await using var scope = fixture.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var productId = fixture.Product.ProductId;
        var purchase = await db.Purchases.AsNoTracking().SingleAsync(x => x.Id == fixture.PurchaseId);
        var items = await db.PurchaseItems.AsNoTracking().Where(x => x.PurchaseId == fixture.PurchaseId).OrderBy(x => x.Id).ToArrayAsync();
        var itemIds = items.Select(x => x.Id).ToArray();
        var lots = await db.InventoryLots.AsNoTracking().Where(x => x.ProductId == productId).OrderBy(x => x.Id).ToArrayAsync();
        var lotIds = lots.Select(x => x.Id).ToArray();
        var movements = await db.InventoryMovements.AsNoTracking().Where(x => x.ProductId == productId).OrderBy(x => x.Id).ToArrayAsync();
        var movementIds = movements.Select(x => x.Id).ToArray();
        var units = await db.InventoryUnits.AsNoTracking().Where(x => x.ProductId == productId).OrderBy(x => x.Id).ToArrayAsync();
        var unitIds = units.Select(x => x.Id).ToArray();
        var payments = await db.SupplierPayments.AsNoTracking().Where(x => x.SupplierId == fixture.Product.SupplierId).OrderBy(x => x.Id).ToArrayAsync();
        var paymentIds = payments.Select(x => x.Id).ToArray();
        return JsonSerializer.Serialize(new
        {
            purchase, items, lots, movements, units, payments,
            Voids = await db.PurchaseVoids.AsNoTracking().Where(x => x.PurchaseId == fixture.PurchaseId).OrderBy(x => x.Id).ToArrayAsync(),
            Stock = await db.StockBalances.AsNoTracking().Where(x => x.ProductId == productId).OrderBy(x => x.Id).ToArrayAsync(),
            Cost = await db.ProductCostStates.AsNoTracking().Where(x => x.ProductId == productId).OrderBy(x => x.Id).ToArrayAsync(),
            LotBalances = await db.InventoryLotBucketBalances.AsNoTracking().Where(x => lotIds.Contains(x.LotId)).OrderBy(x => x.Id).ToArrayAsync(),
            Claims = await db.InventoryUnitIdentityClaims.AsNoTracking().Where(x => unitIds.Contains(x.InventoryUnitId)).OrderBy(x => x.Id).ToArrayAsync(),
            Effects = await db.InventoryMovementEffects.AsNoTracking().Where(x => movementIds.Contains(x.MovementId)).OrderBy(x => x.Id).ToArrayAsync(),
            MovementUnits = await db.InventoryMovementUnits.AsNoTracking().Where(x => movementIds.Contains(x.MovementId)).OrderBy(x => x.Id).ToArrayAsync(),
            ItemUnits = await db.PurchaseItemUnits.AsNoTracking().Where(x => itemIds.Contains(x.PurchaseItemId)).OrderBy(x => x.Id).ToArrayAsync(),
            Ledger = await db.SupplierAccountEntries.AsNoTracking().Where(x => x.SupplierId == fixture.Product.SupplierId).OrderBy(x => x.Id).ToArrayAsync(),
            Reversals = await db.SupplierPaymentReversals.AsNoTracking().Where(x => paymentIds.Contains(x.SupplierPaymentId)).OrderBy(x => x.Id).ToArrayAsync(),
            Cash = await db.CashMovements.AsNoTracking().Where(x => x.SourceId == fixture.PurchaseId || (x.SourceId != null && paymentIds.Contains(x.SourceId.Value))).OrderBy(x => x.Id).ToArrayAsync(),
            Sessions = await db.CashSessions.AsNoTracking().Where(x => x.Id == fixture.SessionId).OrderBy(x => x.Id).ToArrayAsync(),
            Audit = await db.BusinessAuditEvents.AsNoTracking().Where(x => x.EntityId == fixture.PurchaseId).OrderBy(x => x.Id).ToArrayAsync(),
            Outcomes = await db.OperationOutcomes.AsNoTracking().Where(x => x.ResultEntityId == fixture.PurchaseId || (x.ResultEntityId != null && movementIds.Contains(x.ResultEntityId.Value))).OrderBy(x => x.Id).ToArrayAsync()
        });
    }

    private sealed class FlushThenFailUnitOfWork(EdgeRetailsDbContext db) : IUnitOfWork
    {
        public bool Flushed { get; private set; }
        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        {
            await db.SaveChangesAsync(cancellationToken);
            Flushed = true;
            throw new BusinessRuleException("certification.after_sql_flush", "Owned test failure after actual transactional SQL flush.");
        }
    }

    private sealed class FlushGate : IDisposable
    {
        public TaskCompletionSource<bool> Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new(false);
        public void Dispose()
        {
            Release.Set();
            Release.Dispose();
        }
    }

    private sealed class BlockingFlushUnitOfWork(EdgeRetailsDbContext db, FlushGate gate) : IUnitOfWork
    {
        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        {
            var count = await db.SaveChangesAsync(cancellationToken);
            gate.Reached.TrySetResult(true);
            if (!gate.Release.Wait(TimeSpan.FromSeconds(30), cancellationToken))
            {
                throw new TimeoutException("Owned certification concurrency gate timed out.");
            }
            return count;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required ServiceProvider Provider { get; init; }
        public required QuantityProductFixture Product { get; init; }
        public required Guid PurchaseId { get; init; }
        public required Guid PurchaseOperationId { get; init; }
        public Guid? SessionId { get; init; }

        public VoidPurchaseCommand Void() => new(PurchaseId, Guid.NewGuid(), Product.ActorId, "Pass 1 purchasing certification");
        public ReceiveProductIntakeCommand Intake(decimal quantity) => new(PurchaseId, Product.ProductId, Product.ProductUnitId,
            quantity, 1000m, [], Product.ActorId, Guid.NewGuid(), "Pass 1 intake certification");

        public static async Task<Fixture> CreateAsync(bool cashPayment = false, bool physicalReceipt = false)
        {
            var provider = Phase2PostgresTestHarness.BuildProvider();
            try
            {
                return await SeedAsync(provider, cashPayment, physicalReceipt);
            }
            catch
            {
                // Setup assertions can fail before the fixture's await-using is
                // established. Close only sessions in the attested owned test
                // database, retain their history, and propagate the real failure.
                await using (var cleanupScope = provider.CreateAsyncScope())
                {
                    await CloseOpenTestSessionsAsync(cleanupScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>());
                }
                await provider.DisposeAsync();
                throw;
            }
        }

        private static async Task<Fixture> SeedAsync(ServiceProvider provider, bool cashPayment, bool physicalReceipt)
        {
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            // The integration assembly serializes fixtures, but older Tracking
            // cases or a failed preceding setup may leave the global session open.
            await CloseOpenTestSessionsAsync(db);
            await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
            QuantityProductFixture product;
            IReadOnlyList<SerializedIdentityInput> identities = [];
            if (physicalReceipt)
            {
                var physical = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db, defaultSalePrice: 6000m);
                product = new(physical.ProductId, physical.ProductUnitId, physical.SupplierId, physical.ActorId, physical.UnitId);
                identities = [new("P7-A-" + Guid.NewGuid().ToString("N")), new("P7-B-" + Guid.NewGuid().ToString("N"))];
            }
            else
            {
                product = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db, defaultSalePrice: 1500m);
            }
            Guid? sessionId = null;
            if (cashPayment)
            {
                sessionId = (await Phase2PostgresTestHarness.SeedOpenCashSessionAsync(db, product.ActorId)).Id;
            }
            var purchaseOperationId = Guid.NewGuid();
            var purchase = await scope.ServiceProvider.GetRequiredService<CreatePurchaseHandler>().HandleAsync(
                new(product.SupplierId, "P7-" + Guid.NewGuid().ToString("N"), DateOnly.FromDateTime(DateTime.UtcNow),
                    "Pass 1 PostgreSQL purchasing fixture", 0m, PurchaseSettlementMode.External, product.ActorId, purchaseOperationId,
                    [new(product.ProductId, product.ProductUnitId, physicalReceipt ? 2m : 10m, physicalReceipt ? 5000m : 1000m,
                        physicalReceipt ? 6000m : 1500m, identities)],
                    InitialPaymentAmount: cashPayment ? 4000m : 0m,
                    InitialPaymentMethod: cashPayment ? SupplierSettlementMethod.CashDrawer : null,
                    ReceiveStockImmediately: physicalReceipt), default);
            Assert.True(purchase.IsSuccess, purchase.Error?.Message);
            Assert.Equal(10000m, purchase.Value!.GrandTotal);
            return new() { Provider = provider, Product = product, PurchaseId = purchase.Value.PurchaseId,
                PurchaseOperationId = purchaseOperationId, SessionId = sessionId };
        }

        private static async Task CloseOpenTestSessionsAsync(EdgeRetailsDbContext db)
        {
            var sessions = await db.CashSessions.Where(x => x.Status == CashSessionStatus.Open).ToListAsync();
            foreach (var session in sessions)
            {
                var movements = await db.CashMovements.Where(x => x.CashSessionId == session.Id).ToListAsync();
                var cashIn = movements.Where(x => x.Direction == CashMovementDirection.In).Sum(x => x.Amount);
                var cashOut = movements.Where(x => x.Direction == CashMovementDirection.Out).Sum(x => x.Amount);
                session.Close(cashIn, cashOut, Math.Max(0m, session.OpeningCash + cashIn - cashOut), session.OpenedBy, DateTimeOffset.UtcNow);
            }
            if (sessions.Count > 0)
            {
                await db.SaveChangesAsync();
            }
            db.ChangeTracker.Clear();
        }

        public async Task CloseOwnedSessionAsync()
        {
            if (SessionId is null)
            {
                return;
            }
            await using var scope = Provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var session = await db.CashSessions.SingleAsync(x => x.Id == SessionId);
            if (session.Status == CashSessionStatus.Open)
            {
                var cash = await db.CashMovements.Where(x => x.CashSessionId == SessionId).ToListAsync();
                var cashIn = cash.Where(x => x.Direction == CashMovementDirection.In).Sum(x => x.Amount);
                var cashOut = cash.Where(x => x.Direction == CashMovementDirection.Out).Sum(x => x.Amount);
                session.Close(cashIn, cashOut, session.OpeningCash + cashIn - cashOut, Product.ActorId, DateTimeOffset.UtcNow);
                await db.SaveChangesAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await CloseOwnedSessionAsync();
            await Provider.DisposeAsync();
        }
    }
}
