using System.Text.Json;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.IntegrationTests;

[Collection("Phase2PostgresIntegration")]
public sealed class TrackingPurchaseReplayPostgresTests
{
    [Fact]
    public async Task Canonical_purchase_replay_binds_payload_and_preserves_all_business_rows()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var setup = provider.CreateAsyncScope();
        var db = setup.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var product = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);
        var command = new CreatePurchaseCommand(product.SupplierId, "REPLAY-" + Guid.NewGuid().ToString("N"),
            new(2026, 10, 7), "Replay", 0m, PurchaseSettlementMode.External, product.ActorId, Guid.NewGuid(),
            [new(product.ProductId, product.ProductUnitId, 2m, 10m, 15m, [])], InitialPaymentAmount: 0m);
        var first = await setup.ServiceProvider.GetRequiredService<CreatePurchaseHandler>().HandleAsync(command, default);
        Assert.True(first.IsSuccess, first.Error?.ToString());
        var purchaseId = first.Value!.PurchaseId;
        var before = await SnapshotAsync(db, purchaseId, product.ProductId, command.ClientOperationId);
        var changed = new[]
        {
            command with { Lines = [command.Lines[0] with { EnteredQuantity = 3m }] },
            command with { ReceiveStockImmediately = false },
            command with { PurchaseDate = command.PurchaseDate.AddDays(1) },
            command with { InitialPaymentAmount = 1m },
            command with { OtherCharges = 1m }
        };
        foreach (var altered in changed)
        {
            await using var scope = provider.CreateAsyncScope();
            Assert.Equal("idempotency.payload_mismatch", (await scope.ServiceProvider.GetRequiredService<CreatePurchaseHandler>()
                .HandleAsync(altered, default)).Error?.Code);
        }
        await using var replayScope = provider.CreateAsyncScope();
        var replay = await replayScope.ServiceProvider.GetRequiredService<CreatePurchaseHandler>().HandleAsync(command, default);
        Assert.True(replay.IsSuccess, replay.Error?.ToString());
        Assert.True(replay.Value!.WasExisting);
        Assert.Equal(purchaseId, replay.Value.PurchaseId);
        Assert.Equal(before, await SnapshotAsync(replayScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>(),
            purchaseId, product.ProductId, command.ClientOperationId));
    }

    [Fact]
    public async Task Normalized_physical_identity_replay_does_not_allocate_again()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var setup = provider.CreateAsyncScope();
        var db = setup.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var product = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db);
        var serial = "REPLAY-" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        var command = new CreatePurchaseCommand(product.SupplierId, "IDENTITY-" + Guid.NewGuid().ToString("N"),
            new(2026, 10, 7), null, 0m, PurchaseSettlementMode.External, product.ActorId, Guid.NewGuid(),
            [new(product.ProductId, product.ProductUnitId, 1m, 100m, 150m, [new(serial)])]);
        var first = await setup.ServiceProvider.GetRequiredService<CreatePurchaseHandler>().HandleAsync(command, default);
        Assert.True(first.IsSuccess, first.Error?.ToString());
        var before = await SnapshotAsync(db, first.Value!.PurchaseId, product.ProductId, command.ClientOperationId);
        await using var replayScope = provider.CreateAsyncScope();
        var replay = await replayScope.ServiceProvider.GetRequiredService<CreatePurchaseHandler>().HandleAsync(command with
        {
            Lines = [command.Lines[0] with { SerializedUnits = [new(" " + serial.ToLowerInvariant() + " ")] }]
        }, default);
        Assert.True(replay.IsSuccess, replay.Error?.ToString());
        Assert.True(replay.Value!.WasExisting);
        var verify = replayScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Equal(before, await SnapshotAsync(verify, first.Value.PurchaseId, product.ProductId, command.ClientOperationId));
        Assert.Equal(1, await verify.InventoryUnits.CountAsync(x => x.ProductId == product.ProductId));
    }

    [Fact]
    public async Task Failed_purchase_keeps_original_intent_and_status_after_changed_retry()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var setup = provider.CreateAsyncScope();
        var db = setup.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var product = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);
        var command = new CreatePurchaseCommand(Guid.NewGuid(), "MISSING", new(2026, 10, 7), null, 0m,
            PurchaseSettlementMode.External, product.ActorId, Guid.NewGuid(),
            [new(product.ProductId, product.ProductUnitId, 1m, 10m, 15m, [])]);
        Assert.Equal("purchasing.supplier_not_active", (await setup.ServiceProvider.GetRequiredService<CreatePurchaseHandler>()
            .HandleAsync(command, default)).Error?.Code);
        await using var verify = provider.CreateAsyncScope();
        var ledger = verify.ServiceProvider.GetRequiredService<IOperationOutcomeLedger>();
        var original = await ledger.GetOutcomeAsync(command.ClientOperationId);
        Assert.NotNull(original);
        Assert.Equal(OperationOutcomeState.Failed, original.State);
        var before = JsonSerializer.Serialize(original);
        Assert.Equal("idempotency.payload_mismatch", (await verify.ServiceProvider.GetRequiredService<CreatePurchaseHandler>()
            .HandleAsync(command with { OtherCharges = 1m }, default)).Error?.Code);
        Assert.Equal(before, JsonSerializer.Serialize(await ledger.GetOutcomeAsync(command.ClientOperationId)));
        Assert.False(await verify.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>().Purchases
            .AnyAsync(x => x.ClientOperationId == command.ClientOperationId));
    }

    private static async Task<string> SnapshotAsync(EdgeRetailsDbContext db, Guid purchase, Guid product, Guid operation) =>
        JsonSerializer.Serialize(new
        {
            Purchase = await db.Purchases.AsNoTracking().SingleAsync(x => x.Id == purchase),
            Items = await db.PurchaseItems.AsNoTracking().Where(x => x.PurchaseId == purchase).OrderBy(x => x.Id).ToListAsync(),
            Lots = await db.InventoryLots.AsNoTracking().Where(x => x.ProductId == product).OrderBy(x => x.Id).ToListAsync(),
            Units = await db.InventoryUnits.AsNoTracking().Where(x => x.ProductId == product).OrderBy(x => x.Id).ToListAsync(),
            Balance = await db.StockBalances.AsNoTracking().Where(x => x.ProductId == product).ToListAsync(),
            Cost = await db.ProductCostStates.AsNoTracking().Where(x => x.ProductId == product).ToListAsync(),
            Account = await db.SupplierAccountEntries.AsNoTracking().Where(x => x.ClientOperationId == operation).OrderBy(x => x.Id).ToListAsync(),
            Audit = await db.BusinessAuditEvents.AsNoTracking().Where(x => x.CorrelationId == operation).OrderBy(x => x.Id).ToListAsync(),
            Outcome = await db.OperationOutcomes.AsNoTracking().SingleAsync(x => x.ClientOperationId == operation),
            Sequence = await db.SupplierProducts.AsNoTracking().Where(x => x.ProductId == product).OrderBy(x => x.Id).ToListAsync()
        });
}
