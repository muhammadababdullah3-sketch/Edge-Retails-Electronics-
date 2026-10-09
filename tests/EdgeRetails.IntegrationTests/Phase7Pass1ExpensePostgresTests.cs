using System.Text.Json;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace EdgeRetails.IntegrationTests;

[Collection("Phase2PostgresIntegration")]
public sealed class Phase7Pass1ExpensePostgresTests
{
    [Fact]
    public async Task CashExpense_PostVoidAndRepeatedVoid_PersistExactlyOneCompensationAndRestore10000()
    {
        await using var fixture = await ExpenseFixture.CreateAsync();
        var expenseId = await fixture.PostAsync();
        CashMovement original;
        await using (var scope = fixture.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var expense = await db.Expenses.AsNoTracking().SingleAsync(x => x.Id == expenseId);
            Assert.Equal(ExpenseStatus.Posted, expense.Status);
            Assert.Equal(1000m, expense.Amount);
            Assert.Equal(fixture.PostOperationId, expense.ClientOperationId);
            original = await db.CashMovements.AsNoTracking().SingleAsync(x => x.SourceId == expenseId);
            AssertMovement(original, fixture.SessionAId, expenseId, fixture.ActorId,
                CashMovementType.ExpenseCashOut, CashMovementDirection.Out);
            Assert.Equal(9000m, await ExpectedCashAsync(db, fixture.SessionAId));
        }

        var correlation = Guid.NewGuid();
        await fixture.VoidAsync(expenseId, correlation);
        string committed;
        await using (var scope = fixture.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var expense = await db.Expenses.AsNoTracking().SingleAsync(x => x.Id == expenseId);
            Assert.Equal(ExpenseStatus.Voided, expense.Status);
            Assert.Equal(fixture.ActorId, expense.VoidedBy);
            Assert.NotNull(expense.VoidedAt);
            Assert.Equal("Pass 1 expense correction", expense.VoidReason);
            Assert.Equal(1, expense.Version);
            var rows = await db.CashMovements.AsNoTracking().Where(x => x.SourceId == expenseId).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(Assert.Single(rows, x => x.Id == original.Id)));
            AssertMovement(Assert.Single(rows, x => x.Direction == CashMovementDirection.In), fixture.SessionAId,
                expenseId, fixture.ActorId, CashMovementType.ManualCashIn, CashMovementDirection.In);
            Assert.Equal(0m, rows.Sum(x => x.SignedAmount));
            Assert.Equal(10000m, await ExpectedCashAsync(db, fixture.SessionAId));
            var audits = await db.BusinessAuditEvents.AsNoTracking().Where(x => x.EntityId == expenseId).ToListAsync();
            Assert.Equal(2, audits.Count);
            Assert.Equal(fixture.PostOperationId, Assert.Single(audits, x => x.Action == "EXPENSE_POSTED").CorrelationId);
            var audit = Assert.Single(audits, x => x.Action == "EXPENSE_VOIDED");
            Assert.Equal(correlation, audit.CorrelationId);
            Assert.Equal(fixture.ActorId, audit.ActorId);
            Assert.Equal("EXPENSE", audit.EntityType);
            committed = await SnapshotAsync(db, expenseId);
        }

        await fixture.VoidAsync(expenseId, correlation);
        await fixture.VoidAsync(expenseId, Guid.NewGuid());
        await using var final = fixture.Provider.CreateAsyncScope();
        Assert.Equal(committed, await SnapshotAsync(final.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>(), expenseId));
    }

    [Fact]
    public async Task CashExpense_FromClosedSessionA_VoidCompensatesOnlyCurrentSessionB()
    {
        await using var fixture = await ExpenseFixture.CreateAsync();
        var expenseId = await fixture.PostAsync();
        await fixture.CloseAsync(fixture.SessionAId, 9000m);
        string closedA;
        string originalOut;
        await using (var scope = fixture.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var session = await db.CashSessions.AsNoTracking().SingleAsync(x => x.Id == fixture.SessionAId);
            Assert.Equal(CashSessionStatus.Closed, session.Status);
            Assert.Equal(9000m, session.ExpectedClosingCash);
            Assert.Equal(9000m, session.CountedClosingCash);
            Assert.Equal(0m, session.Difference);
            closedA = JsonSerializer.Serialize(session);
            originalOut = JsonSerializer.Serialize(await db.CashMovements.AsNoTracking().SingleAsync(x => x.SourceId == expenseId));
        }
        var sessionB = await fixture.OpenAsync(2000m);
        var correlation = Guid.NewGuid();
        await fixture.VoidAsync(expenseId, correlation);
        await using var verify = fixture.Provider.CreateAsyncScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Equal(closedA, JsonSerializer.Serialize(await verifyDb.CashSessions.AsNoTracking().SingleAsync(x => x.Id == fixture.SessionAId)));
        Assert.Equal(originalOut, JsonSerializer.Serialize(await verifyDb.CashMovements.AsNoTracking().SingleAsync(x => x.SourceId == expenseId && x.Direction == CashMovementDirection.Out)));
        Assert.Equal(1, await verifyDb.CashMovements.CountAsync(x => x.CashSessionId == fixture.SessionAId));
        var compensation = await verifyDb.CashMovements.AsNoTracking().SingleAsync(x => x.SourceId == expenseId && x.Direction == CashMovementDirection.In);
        AssertMovement(compensation, sessionB, expenseId, fixture.ActorId, CashMovementType.ManualCashIn, CashMovementDirection.In);
        Assert.Equal(9000m, await ExpectedCashAsync(verifyDb, fixture.SessionAId));
        Assert.Equal(3000m, await ExpectedCashAsync(verifyDb, sessionB));
        Assert.Equal(ExpenseStatus.Voided, (await verifyDb.Expenses.SingleAsync(x => x.Id == expenseId)).Status);
        var audit = await verifyDb.BusinessAuditEvents.SingleAsync(x => x.EntityId == expenseId && x.Action == "EXPENSE_VOIDED");
        Assert.Equal(correlation, audit.CorrelationId);
        Assert.Equal(fixture.ActorId, audit.ActorId);
    }

    [Fact]
    public async Task CashExpense_NoCurrentOpenSession_VoidFailsWithZeroPersistedPartialEffects()
    {
        await using var fixture = await ExpenseFixture.CreateAsync();
        var expenseId = await fixture.PostAsync();
        await fixture.CloseAsync(fixture.SessionAId, 9000m);
        string before;
        await using (var scope = fixture.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            Assert.False(await db.CashSessions.AnyAsync(x => x.Status == CashSessionStatus.Open));
            before = await SnapshotAsync(db, expenseId);
        }
        await using (var scope = fixture.Provider.CreateAsyncScope())
        {
            var result = await ActivatorUtilities.CreateInstance<VoidExpenseHandler>(scope.ServiceProvider)
                .HandleAsync(new VoidExpenseCommand(expenseId, fixture.ActorId, Guid.NewGuid(), "Pass 1 expense correction"), default);
            Assert.False(result.IsSuccess);
            Assert.Equal("cash.session_required", result.Error!.Code);
        }
        await using var final = fixture.Provider.CreateAsyncScope();
        var finalDb = final.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Equal(before, await SnapshotAsync(finalDb, expenseId));
        Assert.Equal(ExpenseStatus.Posted, (await finalDb.Expenses.SingleAsync(x => x.Id == expenseId)).Status);
        Assert.False(await finalDb.BusinessAuditEvents.AnyAsync(x => x.EntityId == expenseId && x.Action == "EXPENSE_VOIDED"));
        Assert.False(await finalDb.CashMovements.AnyAsync(x => x.SourceId == expenseId && x.Direction == CashMovementDirection.In));
    }

    [Fact]
    public async Task CashExpense_VoidFailureAfterActualSqlFlush_RollsBackExpenseCashAndAuditThenRetryCommitsOnce()
    {
        await using var fixture = await ExpenseFixture.CreateAsync();
        var expenseId = await fixture.PostAsync();
        string before;
        await using (var scope = fixture.Provider.CreateAsyncScope())
        {
            before = await SnapshotAsync(scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>(), expenseId);
        }
        var correlation = Guid.NewGuid();
        await using (var scope = fixture.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var failing = new FlushThenFailUnitOfWork(db, expenseId);
            var handler = ActivatorUtilities.CreateInstance<VoidExpenseHandler>(scope.ServiceProvider, failing);
            var exception = await Assert.ThrowsAsync<InjectedExpenseCommitException>(() => handler.HandleAsync(
                new VoidExpenseCommand(expenseId, fixture.ActorId, correlation, "Pass 1 expense correction"), default));
            Assert.Equal("Injected isolated expense failure after expense, cash and audit SQL flush.", exception.Message);
            Assert.True(failing.ObservedFlushedState);
            Assert.Null(db.Database.CurrentTransaction);
        }
        await using (var scope = fixture.Provider.CreateAsyncScope())
        {
            Assert.Equal(before, await SnapshotAsync(scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>(), expenseId));
            Assert.Null(await scope.ServiceProvider.GetRequiredService<IOperationOutcomeLedger>().GetOutcomeAsync(correlation));
        }
        await fixture.VoidAsync(expenseId, correlation);
        await fixture.VoidAsync(expenseId, correlation);
        await using var final = fixture.Provider.CreateAsyncScope();
        var finalDb = final.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Equal(ExpenseStatus.Voided, (await finalDb.Expenses.SingleAsync(x => x.Id == expenseId)).Status);
        Assert.Equal(1, await finalDb.CashMovements.CountAsync(x => x.SourceId == expenseId && x.Direction == CashMovementDirection.In));
        Assert.Equal(1, await finalDb.BusinessAuditEvents.CountAsync(x => x.EntityId == expenseId && x.Action == "EXPENSE_VOIDED"));
        Assert.Equal(10000m, await ExpectedCashAsync(finalDb, fixture.SessionAId));
        var committedOutcome = await final.ServiceProvider.GetRequiredService<IOperationOutcomeLedger>().GetOutcomeAsync(correlation);
        Assert.NotNull(committedOutcome);
        Assert.True(committedOutcome.WasCommitted);
        Assert.Equal("ExpenseVoid", committedOutcome.OperationType);
    }

    [Fact]
    public async Task VoidIntent_ObservedConcurrentReplay_DurableStatusAndChangedPayloadRejection()
    {
        await using var fixture = await ExpenseFixture.CreateAsync();
        var expenseId = await fixture.PostAsync();
        var operation = Guid.NewGuid();
        var command = new VoidExpenseCommand(expenseId, fixture.ActorId, operation, "Concurrent correction");
        await using var winner = fixture.Provider.CreateAsyncScope();
        var winnerDb = winner.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        await using var transaction = await winnerDb.Database.BeginTransactionAsync();
        Assert.True((await winner.ServiceProvider.GetRequiredService<VoidExpenseHandler>().HandleAsync(command, default)).IsSuccess);
        await using var loser = fixture.Provider.CreateAsyncScope();
        var loserDb = loser.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        await loserDb.Database.OpenConnectionAsync();
        var application = "expense-replay-" + Guid.NewGuid().ToString("N");
        await loserDb.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('application_name', {application}, false)");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var replay = loser.ServiceProvider.GetRequiredService<VoidExpenseHandler>().HandleAsync(command, cancellation.Token);
        try
        {
            await using var observer = new NpgsqlConnection(Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB"));
            await observer.OpenAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var bits = unchecked((ulong)BitConverter.ToInt64(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes($"operation:{operation:D}")), 0));
            while (true)
            {
                await using var wait = new NpgsqlCommand("""
                    SELECT EXISTS (SELECT 1 FROM pg_stat_activity a JOIN pg_locks l ON l.pid=a.pid
                    WHERE a.application_name=@name AND a.wait_event='advisory' AND l.locktype='advisory'
                    AND NOT l.granted AND l.objsubid=1 AND l.classid::bigint=@high AND l.objid::bigint=@low)
                    """, observer);
                wait.Parameters.AddWithValue("name", application);
                wait.Parameters.AddWithValue("high", (long)(bits >> 32));
                wait.Parameters.AddWithValue("low", (long)(bits & uint.MaxValue));
                if ((bool)(await wait.ExecuteScalarAsync(timeout.Token))!)
                {
                    break;
                }
                await Task.Delay(50, timeout.Token);
            }
            Assert.False(replay.IsCompleted);
        }
        finally
        {
            await transaction.CommitAsync();
            try { await replay.WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (TimeoutException)
            {
                cancellation.Cancel();
                await replay.WaitAsync(TimeSpan.FromSeconds(10));
                throw;
            }
        }
        Assert.True((await replay).IsSuccess);
        await using var verify = fixture.Provider.CreateAsyncScope();
        var db = verify.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var mismatch = await verify.ServiceProvider.GetRequiredService<VoidExpenseHandler>().HandleAsync(
            command with { Reason = "Different correction" }, default);
        Assert.Equal("idempotency.payload_mismatch", mismatch.Error?.Code);
        Assert.Equal(1, await db.CashMovements.CountAsync(x => x.SourceId == expenseId && x.Direction == CashMovementDirection.In));
        Assert.Equal(1, await db.BusinessAuditEvents.CountAsync(x => x.EntityId == expenseId && x.Action == "EXPENSE_VOIDED"));
        Assert.Equal(1, await db.OperationOutcomes.CountAsync(x => x.ClientOperationId == operation));
        var outcome = await verify.ServiceProvider.GetRequiredService<IOperationOutcomeLedger>().GetOutcomeAsync(operation);
        Assert.NotNull(outcome);
        Assert.Equal(OperationOutcomeState.Succeeded, outcome.State);
        Assert.True(outcome.WasCommitted);
        Assert.Equal(expenseId, outcome.EntityId);
        Assert.Equal("ExpenseVoid", outcome.OperationType);
        Assert.Equal(fixture.ActorId, outcome.ActorId);
        Assert.False(string.IsNullOrWhiteSpace(outcome.PayloadFingerprint));
        Assert.Equal(fixture.PostOperationId, (await db.Expenses.AsNoTracking().SingleAsync(x => x.Id == expenseId)).ClientOperationId);
        var status = await verify.ServiceProvider.GetRequiredService<OperationStatusQueryHandler>().HandleAsync(
            new(operation, ActorId: fixture.ActorId, PayloadFingerprint: outcome.PayloadFingerprint,
                RequireIdentityScope: true, RequireCanonicalOutcome: true), default);
        Assert.True(status.IsSuccess, status.Error?.ToString());
        Assert.Equal("Succeeded", status.Value!.Status);
        Assert.Equal("ExpenseVoid", status.Value.OperationType);
        Assert.Equal(expenseId, status.Value.EntityId);
        Assert.True(status.Value.WasCommitted);
    }

    [Fact]
    public async Task VoidIdentity_RejectsPostingAndFailedVoidKeepsDurableIntent()
    {
        await using var fixture = await ExpenseFixture.CreateAsync();
        var expenseId = await fixture.PostAsync();
        var operation = Guid.NewGuid();
        await fixture.VoidAsync(expenseId, operation);
        await using (var scope = fixture.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var before = await SnapshotAsync(db, expenseId);
            var post = await scope.ServiceProvider.GetRequiredService<PostExpenseHandler>().HandleAsync(
                new(operation, fixture.CategoryId, null, new DateOnly(2026, 10, 7), 1000m,
                    ExpensePaymentMethod.Bank, "Forbidden reuse", null, fixture.ActorId), default);
            Assert.Equal("idempotency.payload_mismatch", post.Error?.Code);
            Assert.False(await db.Expenses.AnyAsync(x => x.ClientOperationId == operation));
            Assert.Equal(before, await SnapshotAsync(db, expenseId));
        }
        var failedOperation = Guid.NewGuid();
        var command = new VoidExpenseCommand(Guid.NewGuid(), fixture.ActorId, failedOperation, "Missing expense");
        await using (var scope = fixture.Provider.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<VoidExpenseHandler>().HandleAsync(command, default);
            Assert.Equal("expense.not_found", result.Error?.Code);
        }
        await using var verify = fixture.Provider.CreateAsyncScope();
        var outcomes = verify.ServiceProvider.GetRequiredService<IOperationOutcomeLedger>();
        var beforeOutcome = JsonSerializer.Serialize(await outcomes.GetOutcomeAsync(failedOperation));
        Assert.Equal(OperationOutcomeState.Failed, (await outcomes.GetOutcomeAsync(failedOperation))!.State);
        var mismatch = await verify.ServiceProvider.GetRequiredService<VoidExpenseHandler>().HandleAsync(
            command with { Reason = "Different intent" }, default);
        Assert.Equal("idempotency.payload_mismatch", mismatch.Error?.Code);
        Assert.Equal(beforeOutcome, JsonSerializer.Serialize(await outcomes.GetOutcomeAsync(failedOperation)));
    }

    [Fact]
    public async Task FailureSettlement_LockGapPreservesCompetingCommittedFailure()
    {
        await using var fixture = await ExpenseFixture.CreateAsync();
        var operation = Guid.NewGuid();
        string? winningEvidence = null;
        await using var scope = fixture.Provider.CreateAsyncScope();
        var runner = new FailureHandoffRunner(scope.ServiceProvider.GetRequiredService<ITransactionRunner>(), async () =>
        {
            await using var winner = fixture.Provider.CreateAsyncScope();
            var transactions = winner.ServiceProvider.GetRequiredService<ITransactionRunner>();
            await transactions.ExecuteAsync(async ct =>
            {
                await winner.ServiceProvider.GetRequiredService<IOperationLock>().AcquireAsync(operation, ct);
                var ledger = winner.ServiceProvider.GetRequiredService<IOperationOutcomeLedger>();
                await ledger.RecordFailureAsync(operation, "ExpenseVoid", "winning.failed", "Competing intent failed",
                    actorId: fixture.ActorId, payloadFingerprint: "competing-intent", cancellationToken: ct);
                winningEvidence = JsonSerializer.Serialize(await ledger.GetOutcomeAsync(operation, ct));
                return true;
            }, default);
        });
        var handler = ActivatorUtilities.CreateInstance<VoidExpenseHandler>(scope.ServiceProvider, runner);
        var result = await handler.HandleAsync(new(Guid.NewGuid(), fixture.ActorId, operation, "Original missing expense"), default);
        Assert.Equal("idempotency.payload_mismatch", result.Error?.Code);
        Assert.True(runner.HandoffExecuted);
        await using var verify = fixture.Provider.CreateAsyncScope();
        Assert.NotNull(winningEvidence);
        Assert.Equal(winningEvidence, JsonSerializer.Serialize(await verify.ServiceProvider.GetRequiredService<IOperationOutcomeLedger>().GetOutcomeAsync(operation)));
        var db = verify.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.False(await db.BusinessAuditEvents.AnyAsync(x => x.CorrelationId == operation));
        Assert.False(await db.Expenses.AnyAsync(x => x.ClientOperationId == operation));
    }

    private sealed class FailureHandoffRunner(ITransactionRunner inner, Func<Task> handoff) : ITransactionRunner
    {
        public bool HandoffExecuted { get; private set; }
        public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
        {
            var result = await inner.ExecuteAsync(operation, cancellationToken);
            if (!HandoffExecuted && result is IResult { IsSuccess: false })
            {
                HandoffExecuted = true;
                await handoff();
            }
            return result;
        }
    }

    private static void AssertMovement(CashMovement movement, Guid sessionId, Guid expenseId, Guid actorId,
        CashMovementType type, CashMovementDirection direction)
    {
        Assert.Equal(sessionId, movement.CashSessionId);
        Assert.Equal(expenseId, movement.SourceId);
        Assert.Equal("EXPENSE", movement.SourceType);
        Assert.Equal(actorId, movement.ActorId);
        Assert.Equal(type, movement.MovementType);
        Assert.Equal(direction, movement.Direction);
        Assert.Equal(1000m, movement.Amount);
    }

    private static async Task<decimal> ExpectedCashAsync(EdgeRetailsDbContext db, Guid sessionId)
    {
        var session = await db.CashSessions.AsNoTracking().SingleAsync(x => x.Id == sessionId);
        var movements = await db.CashMovements.AsNoTracking().Where(x => x.CashSessionId == sessionId).ToListAsync();
        return session.OpeningCash + movements.Sum(x => x.SignedAmount);
    }

    private static async Task<string> SnapshotAsync(EdgeRetailsDbContext db, Guid expenseId) => JsonSerializer.Serialize(new
    {
        Expense = await db.Expenses.AsNoTracking().SingleAsync(x => x.Id == expenseId),
        Movements = await db.CashMovements.AsNoTracking().Where(x => x.SourceId == expenseId).OrderBy(x => x.Id).ToListAsync(),
        Audit = await db.BusinessAuditEvents.AsNoTracking().Where(x => x.EntityId == expenseId).OrderBy(x => x.Id).ToListAsync(),
        Sessions = await db.CashSessions.AsNoTracking()
            .Where(x => db.CashMovements.Any(m => m.SourceId == expenseId && m.CashSessionId == x.Id))
            .OrderBy(x => x.Id).ToListAsync()
    });

    private sealed class InjectedExpenseCommitException() : Exception(
        "Injected isolated expense failure after expense, cash and audit SQL flush.");

    private sealed class FlushThenFailUnitOfWork(EdgeRetailsDbContext db, Guid expenseId) : IUnitOfWork
    {
        public bool ObservedFlushedState { get; private set; }

        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        {
            await db.SaveChangesAsync(cancellationToken);
            ObservedFlushedState = await db.Expenses.AsNoTracking().AnyAsync(x => x.Id == expenseId && x.Status == ExpenseStatus.Voided, cancellationToken)
                && await db.CashMovements.AsNoTracking().CountAsync(x => x.SourceId == expenseId && x.Direction == CashMovementDirection.In, cancellationToken) == 1
                && await db.BusinessAuditEvents.AsNoTracking().CountAsync(x => x.EntityId == expenseId && x.Action == "EXPENSE_VOIDED", cancellationToken) == 1;
            throw new InjectedExpenseCommitException();
        }
    }

    private sealed class ExpenseFixture : IAsyncDisposable
    {
        public required ServiceProvider Provider { get; init; }
        public Guid ActorId { get; private set; }
        public Guid CategoryId { get; private set; }
        public Guid SessionAId { get; private set; }
        public Guid PostOperationId { get; } = Guid.NewGuid();
        private readonly List<Guid> _sessions = [];

        public static async Task<ExpenseFixture> CreateAsync()
        {
            var fixture = new ExpenseFixture { Provider = Phase2PostgresTestHarness.BuildProvider() };
            try
            {
                await using var scope = fixture.Provider.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
                // The attested runner owns the entire isolated database, and this
                // collection is serial. Seal any previous test's open session
                // before arranging this test; never delete its cash history.
                await CloseSessionSetAsync(db, null);
                fixture.ActorId = await IntegrationIdentitySeeder.CreateActorAsync(db);
                var category = new ExpenseCategory { Name = "Pass1Expense-" + Guid.NewGuid().ToString("N"), IsActive = true };
                db.ExpenseCategories.Add(category);
                await db.SaveChangesAsync();
                fixture.CategoryId = category.Id;
                fixture.SessionAId = await fixture.OpenAsync(10000m);
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        private static async Task CloseSessionSetAsync(EdgeRetailsDbContext db, IReadOnlyCollection<Guid>? sessionIds)
        {
            var query = db.CashSessions.Where(x => x.Status == CashSessionStatus.Open);
            if (sessionIds is not null)
            {
                query = query.Where(x => sessionIds.Contains(x.Id));
            }
            var sessions = await query.ToListAsync();
            foreach (var session in sessions)
            {
                var movements = await db.CashMovements.AsNoTracking().Where(x => x.CashSessionId == session.Id).ToListAsync();
                var cashIn = movements.Where(x => x.Direction == CashMovementDirection.In).Sum(x => x.Amount);
                var cashOut = movements.Where(x => x.Direction == CashMovementDirection.Out).Sum(x => x.Amount);
                session.Close(cashIn, cashOut, Math.Max(0m, session.OpeningCash + cashIn - cashOut),
                    session.OpenedBy, DateTimeOffset.UtcNow);
            }
            if (sessions.Count > 0)
            {
                await db.SaveChangesAsync();
            }
            db.ChangeTracker.Clear();
        }

        public async Task<Guid> OpenAsync(decimal amount)
        {
            await using var scope = Provider.CreateAsyncScope();
            var result = await ActivatorUtilities.CreateInstance<OpenCashSessionHandler>(scope.ServiceProvider)
                .HandleAsync(new OpenCashSessionCommand(amount, ActorId, "Owned Pass 1 certification"), default);
            Assert.True(result.IsSuccess, result.Error?.ToString());
            _sessions.Add(result.Value);
            return result.Value;
        }

        public async Task<Guid> PostAsync()
        {
            await using var scope = Provider.CreateAsyncScope();
            var result = await ActivatorUtilities.CreateInstance<PostExpenseHandler>(scope.ServiceProvider).HandleAsync(
                new PostExpenseCommand(PostOperationId, CategoryId, null, DateOnly.FromDateTime(DateTime.UtcNow),
                    1000m, ExpensePaymentMethod.Cash, "Owned numerical expense", "P7", ActorId), default);
            Assert.True(result.IsSuccess, result.Error?.ToString());
            return result.Value!.ExpenseId;
        }

        public async Task VoidAsync(Guid expenseId, Guid correlation)
        {
            await using var scope = Provider.CreateAsyncScope();
            var result = await ActivatorUtilities.CreateInstance<VoidExpenseHandler>(scope.ServiceProvider).HandleAsync(
                new VoidExpenseCommand(expenseId, ActorId, correlation, "Pass 1 expense correction"), default);
            Assert.True(result.IsSuccess, result.Error?.ToString());
        }

        public async Task CloseAsync(Guid sessionId, decimal counted)
        {
            await using var scope = Provider.CreateAsyncScope();
            var result = await ActivatorUtilities.CreateInstance<CloseCashSessionHandler>(scope.ServiceProvider).HandleAsync(
                new CloseCashSessionCommand(sessionId, counted, ActorId, "Owned certification closure"), default);
            Assert.True(result.IsSuccess, result.Error?.ToString());
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var scope = Provider.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
                // After assertions, seal only this fixture's sessions using the
                // canonical closing calculation; preserve every cash movement.
                await CloseSessionSetAsync(db, _sessions);
            }
            finally
            {
                await Provider.DisposeAsync();
            }
        }
    }
}
