using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Domain.Identity;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace EdgeRetails.IntegrationTests;

[Collection("Phase2PostgresIntegration")]
public sealed class Phase7Pass5FinancePostgresTests
{
    [Theory]
    [InlineData(SupplierAccountDirection.IncreasePayable)]
    [InlineData(SupplierAccountDirection.DecreasePayable)]
    public async Task SupplierOpening_PersistsDirectionContextAndOneReplayFact(SupplierAccountDirection direction)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var intent = (await OpeningAsync(provider)) with { Direction = direction };
        var first = await OpenAsync(provider, intent);
        Assert.True(first.IsSuccess, first.Error?.Message);
        var replay = await OpenAsync(provider, intent);
        Assert.True(replay.IsSuccess, replay.Error?.Message);
        Assert.True(replay.Value!.WasExisting);
        Assert.Equal(first.Value!.EntryId, replay.Value.EntryId);
        var conflict = await OpenAsync(provider, intent with { Direction = direction == SupplierAccountDirection.IncreasePayable
            ? SupplierAccountDirection.DecreasePayable : SupplierAccountDirection.IncreasePayable });
        Assert.Equal("idempotency.payload_mismatch", conflict.Error?.Code);
        await using var read = provider.CreateAsyncScope();
        var db = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var row = Assert.Single(await db.SupplierAccountEntries.Where(x => x.SupplierId == intent.SupplierId).ToListAsync());
        Assert.Equal(SupplierAccountEntryType.OpeningBalance, row.EntryType);
        Assert.Equal(direction, row.Direction);
        Assert.Equal(100.01m, row.Amount);
        Assert.Equal(intent.EffectiveAt.ToUniversalTime(), row.OccurredAt);
        Assert.Equal(intent.ActorId, row.ActorId);
        Assert.Equal("SupplierOpeningBalance", row.ReferenceType);
        Assert.Equal(intent.ClientOperationId, row.ReferenceId);
        using var metadata = System.Text.Json.JsonDocument.Parse(row.Note!);
        Assert.Equal("Approved opening", metadata.RootElement.GetProperty("Reason").GetString());
        Assert.Equal("CUTOVER-01", metadata.RootElement.GetProperty("CutoverReference").GetString());
        Assert.Equal(row.SignedAmount, await read.ServiceProvider.GetRequiredService<ISupplierAccountRepository>().GetCurrentBalanceAsync(intent.SupplierId, default));
        var audit = await db.BusinessAuditEvents.SingleAsync(x => x.CorrelationId == intent.ClientOperationId);
        Assert.Equal(row.Id, audit.EntityId);
        Assert.Equal("SUPPLIER_OPENING_BALANCE_POSTED", audit.Action);
        Assert.Empty(await db.CashMovements.Where(x => x.ActorId == intent.ActorId).ToListAsync());
        Assert.Empty(await db.SupplierPayments.Where(x => x.SupplierId == intent.SupplierId).ToListAsync());
        Assert.Empty(await db.SupplierRefunds.Where(x => x.SupplierId == intent.SupplierId).ToListAsync());
    }

    [Fact]
    public async Task SupplierOpening_ActualPermissionDenialPostsNothing()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var intent = await OpeningAsync(provider);
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var role = new Role { Name = "C29 denied-" + Guid.NewGuid(), IsActive = true };
            db.Roles.Add(role);
            (await db.Users.SingleAsync(x => x.Id == intent.ActorId)).RoleId = role.Id;
            await db.SaveChangesAsync();
        }
        var result = await OpenAsync(provider, intent);
        Assert.Equal("authorization.denied", result.Error?.Code);
        await AssertNoOpeningAsync(provider, intent);
    }

    [Fact]
    public async Task SupplierOpening_ActualSqlFlushFailureRollsBackEntryAuditAndOutcome()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var intent = await OpeningAsync(provider);
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var failure = new OpeningFlushThenThrow(db);
            var handler = ActivatorUtilities.CreateInstance<SupplierOpeningBalanceHandler>(scope.ServiceProvider, failure);
            await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(intent, default));
            Assert.True(failure.Flushed);
        }
        await AssertNoOpeningAsync(provider, intent);
    }

    [Fact]
    public async Task SupplierOpening_ConcurrentRetryWaitsForDatabaseLockAndPostsOnce()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var intent = await OpeningAsync(provider);
        await using var winnerScope = provider.CreateAsyncScope();
        var winnerDb = winnerScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        await using var transaction = await winnerDb.Database.BeginTransactionAsync();
        var winner = await winnerScope.ServiceProvider.GetRequiredService<SupplierOpeningBalanceHandler>().HandleAsync(intent, default);
        Assert.True(winner.IsSuccess, winner.Error?.Message);
        await using var contenderScope = provider.CreateAsyncScope();
        var contenderDb = contenderScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        await contenderDb.Database.OpenConnectionAsync();
        var application = "p5-c29-" + Guid.NewGuid().ToString("N");
        await contenderDb.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('application_name', {application}, false)");
        var contender = contenderScope.ServiceProvider.GetRequiredService<SupplierOpeningBalanceHandler>().HandleAsync(intent, default);
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
        Assert.Equal(winner.Value!.EntryId, replay.Value.EntryId);
        await using var read = provider.CreateAsyncScope();
        var db = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Single(await db.SupplierAccountEntries.Where(x => x.SupplierId == intent.SupplierId).ToListAsync());
        Assert.Single(await db.BusinessAuditEvents.Where(x => x.CorrelationId == intent.ClientOperationId).ToListAsync());
    }

    private static async Task<SupplierOpeningBalanceCommand> OpeningAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var seed = await Phase2PostgresTestHarness.SeedQuantityProductAsync(scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>());
        return new(seed.SupplierId, SupplierAccountDirection.IncreasePayable, 100.005m,
            new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.FromHours(5)), "Approved opening", seed.ActorId, Guid.NewGuid(), "CUTOVER-01");
    }

    private static async Task<EdgeRetails.Application.Common.Result<SupplierOpeningBalanceResult>> OpenAsync(ServiceProvider provider, SupplierOpeningBalanceCommand intent)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SupplierOpeningBalanceHandler>().HandleAsync(intent, default);
    }

    private static async Task AssertNoOpeningAsync(ServiceProvider provider, SupplierOpeningBalanceCommand intent)
    {
        await using var read = provider.CreateAsyncScope();
        var db = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Empty(await db.SupplierAccountEntries.Where(x => x.SupplierId == intent.SupplierId).ToListAsync());
        Assert.Empty(await db.BusinessAuditEvents.Where(x => x.CorrelationId == intent.ClientOperationId).ToListAsync());
        Assert.False(await db.OperationOutcomes.AnyAsync(x => x.ClientOperationId == intent.ClientOperationId));
        Assert.Empty(await db.CashMovements.Where(x => x.ActorId == intent.ActorId).ToListAsync());
    }

    private sealed class OpeningFlushThenThrow(EdgeRetailsDbContext db) : IUnitOfWork
    {
        public bool Flushed { get; private set; }
        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        {
            await db.SaveChangesAsync(cancellationToken);
            Flushed = true;
            throw new InvalidOperationException("C29 test-only SQL flush failure");
        }
    }

    // NEW_COVERAGE: persisted expense midpoint, independent fresh-scope reload.
    [Fact]
    public async Task ExpenseMidpointPersistsCanonicalMoneyAndNoBankCashEffect()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        Guid actor, categoryId;
        await using (var seed = provider.CreateAsyncScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            actor = await IntegrationIdentitySeeder.CreateActorAsync(db);
            var category = new ExpenseCategory { Name = "Pass5-" + Guid.NewGuid().ToString("N") };
            db.ExpenseCategories.Add(category);
            await db.SaveChangesAsync();
            categoryId = category.Id;
        }
        var operation = Guid.NewGuid();
        Guid expenseId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<PostExpenseHandler>().HandleAsync(
                new(operation, categoryId, null, DateOnly.FromDateTime(DateTime.UtcNow), 1.005m, ExpensePaymentMethod.Bank, "Midpoint proof", null, actor), default);
            Assert.True(result.IsSuccess, result.Error?.Message);
            expenseId = result.Value!.ExpenseId;
        }
        await using var read = provider.CreateAsyncScope();
        var persisted = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var expense = await persisted.Expenses.AsNoTracking().SingleAsync(x => x.Id == expenseId);
        Assert.Equal(1.01m, expense.Amount);
        Assert.Equal(operation, expense.ClientOperationId);
        Assert.Equal(ExpensePaymentMethod.Bank, expense.PaymentMethod);
        Assert.Empty(await persisted.CashMovements.Where(x => x.SourceId == expenseId).ToListAsync());
        Assert.Single(await persisted.BusinessAuditEvents.Where(x => x.EntityId == expenseId && x.Action == "EXPENSE_POSTED").ToListAsync());
    }
}
