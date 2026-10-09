using System.Text.Json;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace EdgeRetails.IntegrationTests;

[Collection("Phase2PostgresIntegration")]
public sealed class TrackingPosDraftReplayPostgresTests
{
    [Fact]
    public async Task Concurrent_different_operations_for_one_draft_commit_only_one_sale()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var command = await ArrangeAsync(provider);
        await using var winner = provider.CreateAsyncScope();
        var db = winner.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        Assert.True((await winner.ServiceProvider.GetRequiredService<CompletePosDraftHandler>().HandleAsync(command, default)).IsSuccess);
        var winnerPid = ((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID;
        await using var contender = provider.CreateAsyncScope();
        var contenderDb = contender.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        await contenderDb.Database.OpenConnectionAsync();
        var contenderPid = ((NpgsqlConnection)contenderDb.Database.GetDbConnection()).ProcessID;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var otherOperation = Guid.NewGuid();
        var attempt = contender.ServiceProvider.GetRequiredService<CompletePosDraftHandler>().HandleAsync(
            command with { ClientOperationId = otherOperation }, cancellation.Token);
        try
        {
            await using var observer = new NpgsqlConnection(Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB"));
            await observer.OpenAsync(cancellation.Token);
            using var observeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (true)
            {
                await using var query = new NpgsqlCommand("SELECT wait_event_type='Lock' AND @winner=ANY(pg_blocking_pids(pid)) FROM pg_stat_activity WHERE pid=@pid", observer);
                query.Parameters.AddWithValue("pid", contenderPid);
                query.Parameters.AddWithValue("winner", winnerPid);
                if (await query.ExecuteScalarAsync(observeTimeout.Token) is true)
                {
                    break;
                }
                await Task.Delay(20, observeTimeout.Token);
            }
            Assert.False(attempt.IsCompleted);
        }
        finally
        {
            await transaction.CommitAsync();
            try { await attempt.WaitAsync(TimeSpan.FromSeconds(20)); }
            catch (TimeoutException)
            {
                await cancellation.CancelAsync();
                await attempt.WaitAsync(TimeSpan.FromSeconds(10));
                throw;
            }
        }
        Assert.False((await attempt).IsSuccess);
        await using var verify = provider.CreateAsyncScope();
        var verified = verify.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Equal(1, await verified.Sales.CountAsync(x => x.CashierUserId == command.CashierUserId));
        Assert.False(await verified.OperationOutcomes.AnyAsync(x => x.ClientOperationId == otherOperation));
        var draft = await verified.PosDrafts.AsNoTracking().SingleAsync(x => x.Id == command.DraftId);
        Assert.Equal(PosDraftStatus.Converted, draft.Status);
        Assert.Equal(command.ExpectedVersion + 1, draft.Version);
        var product = await verified.PosDraftItems.Where(x => x.DraftId == command.DraftId).Select(x => x.ProductId).SingleAsync();
        Assert.Equal(8m, (await verified.StockBalances.SingleAsync(x => x.ProductId == product)).SellableQty);
    }

    [Fact]
    public async Task Draft_replay_is_bound_to_original_draft_version_and_payload()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var command = await ArrangeAsync(provider);
        await using var firstScope = provider.CreateAsyncScope();
        var first = await firstScope.ServiceProvider.GetRequiredService<CompletePosDraftHandler>().HandleAsync(command, default);
        Assert.True(first.IsSuccess, first.Error?.ToString());
        var db = firstScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var original = await db.PosDrafts.AsNoTracking().SingleAsync(x => x.Id == command.DraftId);
        var other = new PosDraft { DraftNumber = "OTHER-" + Guid.NewGuid().ToString("N"), CreatedBy = command.CashierUserId,
            Status = PosDraftStatus.Open, Version = command.ExpectedVersion };
        db.PosDrafts.Add(other);
        var originalItems = await db.PosDraftItems.AsNoTracking().Where(x => x.DraftId == command.DraftId).ToListAsync();
        foreach (var item in originalItems)
        {
            db.PosDraftItems.Add(new PosDraftItem { DraftId = other.Id, ProductId = item.ProductId, ProductUnitId = item.ProductUnitId,
                EnteredQuantity = item.EnteredQuantity, BaseQuantity = item.BaseQuantity, FactorToBaseSnapshot = item.FactorToBaseSnapshot,
                DisplayedUnitPriceSnapshot = item.DisplayedUnitPriceSnapshot });
        }
        await db.SaveChangesAsync();
        var before = await SnapshotAsync(db, command);
        var attempts = new[]
        {
            command with { DraftId = other.Id },
            command with { ExpectedVersion = command.ExpectedVersion + 1 },
            command with { AmountTendered = command.AmountTendered + 1m },
            command with { ClientOperationId = Guid.NewGuid() }
        };
        foreach (var altered in attempts)
        {
            await using var scope = provider.CreateAsyncScope();
            var rejected = await scope.ServiceProvider.GetRequiredService<CompletePosDraftHandler>().HandleAsync(altered, default);
            Assert.False(rejected.IsSuccess);
        }
        await using var replayScope = provider.CreateAsyncScope();
        var replay = await replayScope.ServiceProvider.GetRequiredService<CompletePosDraftHandler>().HandleAsync(
            command with { PaymentReference = "Retry reference retains winner" }, default);
        Assert.True(replay.IsSuccess, replay.Error?.ToString());
        Assert.True(replay.Value!.WasExisting);
        Assert.Equal(first.Value!.SaleId, replay.Value.SaleId);
        var verify = replayScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Equal(before, await SnapshotAsync(verify, command));
        Assert.Equal(PosDraftStatus.Open, (await verify.PosDrafts.AsNoTracking().SingleAsync(x => x.Id == other.Id)).Status);
        Assert.Equal(original.Version, (await verify.PosDrafts.AsNoTracking().SingleAsync(x => x.Id == command.DraftId)).Version);
        var ordinarySale = new CompleteSaleCommand(command.ClientOperationId, null, command.CashierUserId, null,
            0m, SalePaymentMethod.Bank, 300m, "Other reference", null,
            originalItems.Select(x => new CompleteSaleLineInput(x.ProductId, x.ProductUnitId, x.EnteredQuantity, x.DisplayedUnitPriceSnapshot, [])).ToArray());
        Assert.Equal("idempotency.payload_mismatch", (await replayScope.ServiceProvider.GetRequiredService<CompleteSaleHandler>()
            .HandleAsync(ordinarySale, default)).Error?.Code);
        Assert.Equal(before, await SnapshotAsync(verify, command));
        // Arrange a legacy outcome without source evidence. A retry must not bless it.
        var legacy = await verify.OperationOutcomes.SingleAsync(x => x.ClientOperationId == command.ClientOperationId);
        legacy.PayloadFingerprint = null;
        await verify.SaveChangesAsync();
        var legacyBefore = await SnapshotAsync(verify, command);
        await using var legacyScope = provider.CreateAsyncScope();
        Assert.Equal("idempotency.payload_mismatch", (await legacyScope.ServiceProvider.GetRequiredService<CompletePosDraftHandler>()
            .HandleAsync(command, default)).Error?.Code);
        Assert.Equal(legacyBefore, await SnapshotAsync(legacyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>(), command));
    }

    [Fact]
    public async Task Draft_conversion_sql_flush_failure_rolls_back_sale_outcome_and_draft_together()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var command = await ArrangeAsync(provider);
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var handler = ActivatorUtilities.CreateInstance<CompletePosDraftHandler>(scope.ServiceProvider, new FlushThenFail(db));
            await Assert.ThrowsAsync<InjectedDraftFailure>(() => handler.HandleAsync(command, default));
        }
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            Assert.False(await db.Sales.AnyAsync(x => x.ClientOperationId == command.ClientOperationId));
            Assert.False(await db.OperationOutcomes.AnyAsync(x => x.ClientOperationId == command.ClientOperationId));
            var draft = await db.PosDrafts.AsNoTracking().SingleAsync(x => x.Id == command.DraftId);
            Assert.Equal(PosDraftStatus.Open, draft.Status);
            Assert.Equal(command.ExpectedVersion, draft.Version);
            var product = await db.PosDraftItems.Where(x => x.DraftId == command.DraftId).Select(x => x.ProductId).SingleAsync();
            Assert.Equal(10m, (await db.StockBalances.SingleAsync(x => x.ProductId == product)).SellableQty);
            Assert.False(await db.BusinessAuditEvents.AnyAsync(x => x.CorrelationId == command.ClientOperationId));
        }
        await using var retry = provider.CreateAsyncScope();
        Assert.True((await retry.ServiceProvider.GetRequiredService<CompletePosDraftHandler>().HandleAsync(command, default)).IsSuccess);
    }

    private sealed class InjectedDraftFailure : Exception;
    private sealed class FlushThenFail(EdgeRetailsDbContext db) : IUnitOfWork
    {
        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        {
            await db.SaveChangesAsync(cancellationToken);
            Assert.Contains(db.ChangeTracker.Entries<PosDraft>(), x => x.Entity.Status == PosDraftStatus.Converted);
            throw new InjectedDraftFailure();
        }
    }

    private static async Task<CompletePosDraftCommand> ArrangeAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var product = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);
        var purchase = await scope.ServiceProvider.GetRequiredService<CreatePurchaseHandler>().HandleAsync(
            new(product.SupplierId, "DRAFT-STOCK-" + Guid.NewGuid().ToString("N"), new(2026, 10, 7), null, 0m,
                PurchaseSettlementMode.External, product.ActorId, Guid.NewGuid(),
                [new(product.ProductId, product.ProductUnitId, 10m, 100m, 150m, [])]), default);
        Assert.True(purchase.IsSuccess, purchase.Error?.ToString());
        var draft = await scope.ServiceProvider.GetRequiredService<SavePosDraftHandler>().HandleAsync(
            new(null, null, null, product.ActorId, "CERT", "Replay", [new(product.ProductId, product.ProductUnitId, 2m)]), default);
        Assert.True(draft.IsSuccess, draft.Error?.ToString());
        return new(draft.Value!.DraftId, draft.Value.Version, Guid.NewGuid(), product.ActorId, null, 0m,
            SalePaymentMethod.Bank, 300m, "Original reference");
    }

    private static async Task<string> SnapshotAsync(EdgeRetailsDbContext db, CompletePosDraftCommand command) => JsonSerializer.Serialize(new
    {
        Draft = await db.PosDrafts.AsNoTracking().SingleAsync(x => x.Id == command.DraftId),
        Sales = await db.Sales.AsNoTracking().Where(x => x.CashierUserId == command.CashierUserId).OrderBy(x => x.Id).ToListAsync(),
        Payments = await db.SalePayments.AsNoTracking().Where(x => db.Sales.Any(s => s.Id == x.SaleId && s.CashierUserId == command.CashierUserId)).OrderBy(x => x.Id).ToListAsync(),
        Stock = await db.StockBalances.AsNoTracking().Where(x => db.PosDraftItems.Any(i => i.DraftId == command.DraftId && i.ProductId == x.ProductId)).ToListAsync(),
        Outcome = await db.OperationOutcomes.AsNoTracking().SingleAsync(x => x.ClientOperationId == command.ClientOperationId),
        Audit = await db.BusinessAuditEvents.AsNoTracking().Where(x => x.CorrelationId == command.ClientOperationId).OrderBy(x => x.Id).ToListAsync()
    });
}
