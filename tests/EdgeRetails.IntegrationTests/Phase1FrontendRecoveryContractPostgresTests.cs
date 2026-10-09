using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace EdgeRetails.IntegrationTests;

// Contract characterization only: discarding a committed handler response models client uncertainty.
// These tests do not claim transport fault injection, Desktop restart, or durable UI recovery coverage.
[Collection("Phase2PostgresIntegration")]
public sealed class Phase1FrontendRecoveryContractPostgresTests
{
    [Fact]
    public async Task OwnedModelFixtureBootstrap_NoMigrationsApplied()
    {
        await using var provider = BuildOwnedProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        // Model fixture only. Migration SQL/triggers are deliberately not applied or certified.
        await db.Database.EnsureCreatedAsync();
        Assert.True(await db.Database.CanConnectAsync());
    }

    private static ServiceProvider BuildOwnedProvider()
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB")
            ?? throw new InvalidOperationException("Owned isolated test destination is required."));
        if (connection.Database != "edge_retails_master_test" || connection.Host != "127.0.0.1"
            || connection.Port < 55000 || connection.Port > 55999)
        {
            throw new InvalidOperationException("Only the disposable model fixture is authorized; shop databases are prohibited.");
        }
        return Phase2PostgresTestHarness.BuildProvider();
    }
    [Theory]
    [InlineData(SupplierPaymentPurpose.Advance)]
    [InlineData(SupplierPaymentPurpose.Settlement)]
    public async Task SupplierPayment_DiscardedCommittedResponse_ReplayHasOnePaymentAndBalanceEffect(SupplierPaymentPurpose purpose)
    {
        await using var provider = BuildOwnedProvider();
        var seed = await SeedAsync(provider);
        if (purpose == SupplierPaymentPurpose.Settlement)
        {
            await using var openingScope = provider.CreateAsyncScope();
            var opening = await openingScope.ServiceProvider.GetRequiredService<SupplierOpeningBalanceHandler>().HandleAsync(
                new(seed.SupplierId, SupplierAccountDirection.IncreasePayable, 100m, DateTimeOffset.UtcNow,
                    "Phase1 isolated payable fixture", seed.ActorId, Guid.NewGuid()), default);
            Assert.True(opening.IsSuccess, opening.Error?.Message);
        }
        var command = new CreateSupplierPaymentCommand(seed.SupplierId, 25m, purpose,
            SupplierSettlementMethod.External, seed.ActorId, Guid.NewGuid(), "P1-REFERENCE", "Original intended payment");
        await using (var submit = provider.CreateAsyncScope())
        {
            var discarded = await submit.ServiceProvider.GetRequiredService<CreateSupplierPaymentHandler>().HandleAsync(command, default);
            Assert.True(discarded.IsSuccess, discarded.Error?.Message);
        }
        await using (var replayScope = provider.CreateAsyncScope())
        {
            var replay = await replayScope.ServiceProvider.GetRequiredService<CreateSupplierPaymentHandler>().HandleAsync(command, default);
            Assert.True(replay.IsSuccess, replay.Error?.Message);
            Assert.True(replay.Value!.WasExisting);
        }
        await using var read = provider.CreateAsyncScope();
        var db = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var payment = Assert.Single(await db.SupplierPayments.AsNoTracking().Where(x => x.ClientOperationId == command.ClientOperationId).ToListAsync());
        Assert.Equal(25m, payment.Amount);
        Assert.Equal(command.Note, payment.Note);
        var entry = Assert.Single(await db.SupplierAccountEntries.AsNoTracking().Where(x => x.ClientOperationId == command.ClientOperationId).ToListAsync());
        Assert.Equal(payment.Id, entry.ReferenceId);
        Assert.Equal(SupplierAccountDirection.DecreasePayable, entry.Direction);
        Assert.Equal(25m, entry.Amount);
        Assert.Equal(purpose == SupplierPaymentPurpose.Settlement ? 75m : -25m,
            await read.ServiceProvider.GetRequiredService<ISupplierAccountRepository>().GetCurrentBalanceAsync(seed.SupplierId, default));
        Assert.Single(await db.BusinessAuditEvents.Where(x => x.CorrelationId == command.ClientOperationId).ToListAsync());
        Assert.Empty(await db.CashMovements.Where(x => x.SourceId == payment.Id).ToListAsync());
    }

    [Fact]
    public async Task SupplierRefund_DiscardedCommittedResponse_ReplayAndRejectedChangedNoteOrOwnerLeaveOneEffect()
    {
        await using var provider = BuildOwnedProvider();
        var seed = await SeedAsync(provider);
        Guid otherActor;
        await using (var arrange = provider.CreateAsyncScope())
        {
            var db = arrange.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            otherActor = await IntegrationIdentitySeeder.CreateActorAsync(db);
            var advance = await arrange.ServiceProvider.GetRequiredService<CreateSupplierPaymentHandler>().HandleAsync(
                new(seed.SupplierId, 100m, SupplierPaymentPurpose.Advance, SupplierSettlementMethod.External,
                    seed.ActorId, Guid.NewGuid(), Note: "Refund credit fixture"), default);
            Assert.True(advance.IsSuccess, advance.Error?.Message);
        }
        var command = new CreateSupplierRefundCommand(seed.SupplierId, 25m, SupplierSettlementMethod.External,
            seed.ActorId, Guid.NewGuid(), "P1-REFUND", Note: "Original refund");
        await using (var submit = provider.CreateAsyncScope())
        {
            var discarded = await submit.ServiceProvider.GetRequiredService<CreateSupplierRefundHandler>().HandleAsync(command, default);
            Assert.True(discarded.IsSuccess, discarded.Error?.Message);
        }
        await using (var replayScope = provider.CreateAsyncScope())
        {
            var replay = await replayScope.ServiceProvider.GetRequiredService<CreateSupplierRefundHandler>().HandleAsync(command, default);
            Assert.True(replay.IsSuccess, replay.Error?.Message);
            Assert.True(replay.Value!.WasExisting);
        }
        foreach (var mismatch in new[] { command with { Note = "Changed after response loss" }, command with { ActorId = otherActor } })
        {
            await using var mismatchScope = provider.CreateAsyncScope();
            var rejected = await mismatchScope.ServiceProvider.GetRequiredService<CreateSupplierRefundHandler>().HandleAsync(mismatch, default);
            Assert.False(rejected.IsSuccess);
            Assert.Equal("idempotency.payload_mismatch", rejected.Error?.Code);
        }
        await using var read = provider.CreateAsyncScope();
        var persisted = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var refund = Assert.Single(await persisted.SupplierRefunds.AsNoTracking().Where(x => x.ClientOperationId == command.ClientOperationId).ToListAsync());
        Assert.Equal(command.Note, refund.Note);
        Assert.Equal(command.ActorId, refund.ActorId);
        var entry = Assert.Single(await persisted.SupplierAccountEntries.AsNoTracking().Where(x => x.ClientOperationId == command.ClientOperationId).ToListAsync());
        Assert.Equal(refund.Id, entry.ReferenceId);
        Assert.Equal(25m, entry.Amount);
        Assert.Equal(SupplierAccountDirection.IncreasePayable, entry.Direction);
        Assert.Equal(-75m, await read.ServiceProvider.GetRequiredService<ISupplierAccountRepository>().GetCurrentBalanceAsync(seed.SupplierId, default));
        Assert.Single(await persisted.BusinessAuditEvents.Where(x => x.CorrelationId == command.ClientOperationId).ToListAsync());
        Assert.Empty(await persisted.CashMovements.Where(x => x.SourceId == refund.Id).ToListAsync());
    }

    [Fact]
    public async Task Expense_DiscardedCommittedResponse_ReplayHasOneExpenseAuditAndNoBankCashEffect()
    {
        await using var provider = BuildOwnedProvider();
        Guid actor, categoryId;
        await using (var arrange = provider.CreateAsyncScope())
        {
            var db = arrange.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            actor = await IntegrationIdentitySeeder.CreateActorAsync(db);
            var category = new ExpenseCategory { Name = "Phase1-" + Guid.NewGuid().ToString("N") };
            db.ExpenseCategories.Add(category);
            await db.SaveChangesAsync();
            categoryId = category.Id;
        }
        var command = new PostExpenseCommand(Guid.NewGuid(), categoryId, null, DateOnly.FromDateTime(DateTime.UtcNow),
            25.50m, ExpensePaymentMethod.Bank, "Original expense", "P1-EXPENSE", actor);
        await using (var submit = provider.CreateAsyncScope())
        {
            var discarded = await submit.ServiceProvider.GetRequiredService<PostExpenseHandler>().HandleAsync(command, default);
            Assert.True(discarded.IsSuccess, discarded.Error?.Message);
        }
        await using (var replayScope = provider.CreateAsyncScope())
        {
            var replay = await replayScope.ServiceProvider.GetRequiredService<PostExpenseHandler>().HandleAsync(command, default);
            Assert.True(replay.IsSuccess, replay.Error?.Message);
            Assert.True(replay.Value!.WasExisting);
        }
        await using var read = provider.CreateAsyncScope();
        var persisted = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var expense = Assert.Single(await persisted.Expenses.AsNoTracking().Where(x => x.ClientOperationId == command.ClientOperationId).ToListAsync());
        Assert.Equal(command.Amount, expense.Amount);
        Assert.Equal(command.Description, expense.Description);
        Assert.Single(await persisted.BusinessAuditEvents.Where(x => x.CorrelationId == command.ClientOperationId && x.Action == "EXPENSE_POSTED").ToListAsync());
        Assert.Empty(await persisted.CashMovements.Where(x => x.SourceId == expense.Id).ToListAsync());
    }

    private static async Task<QuantityProductFixture> SeedAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        return await Phase2PostgresTestHarness.SeedQuantityProductAsync(scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>());
    }
}
