using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Warranty;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Warranty;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.IntegrationTests;

[Collection("TrackingManufacturerIdentityPg")]
public sealed class TrackingCrossWorkflowPostgresTests
{
    [Theory]
    [InlineData("purchase", "warranty", false)]
    [InlineData("purchase", "adjustment", false)]
    [InlineData("warranty", "adjustment", false)]
    [InlineData("purchase", "intake", false)]
    [InlineData("purchase", "warranty", true)]
    [InlineData("purchase", "adjustment", true)]
    [InlineData("warranty", "adjustment", true)]
    [InlineData("purchase", "intake", true)]
    public async Task CrossWorkflowIdentityRace_OneOwnerAndLoserHasNoPartialBusinessEffect(string a, string b, bool crossSlotImei)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var first = await SeedAsync(provider, a);
        var second = await SeedAsync(provider, b);
        var serial = "RACE-" + Guid.NewGuid().ToString("N");
        var imei = NewImei();
        var identityA = new SerializedIdentityInput(serial, crossSlotImei ? imei : NewImei());
        var identityB = crossSlotImei
            ? new SerializedIdentityInput("OTHER-" + Guid.NewGuid().ToString("N"), NewImei(), imei)
            : new SerializedIdentityInput(" " + serial.ToUpperInvariant() + " ", NewImei());
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        async Task<(bool Success, string? Error)> RunAsync(Workflow target, SerializedIdentityInput identity)
        {
            await using var scope = provider.CreateAsyncScope();
            if (Interlocked.Increment(ref arrivals) == 2)
            {
                barrier.SetResult();
            }
            await barrier.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var services = scope.ServiceProvider;
            if (target.Kind == "purchase")
            {
                var result = await services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(Purchase(target.Seed, [identity]), default);
                return (result.IsSuccess, result.Error?.Code);
            }
            if (target.Kind == "intake")
            {
                var result = await services.GetRequiredService<ReceiveProductIntakeHandler>().HandleAsync(new ReceiveProductIntakeCommand(
                    target.Target, target.Seed.ProductId, target.Seed.ProductUnitId, 1m, 100m, [identity], target.Seed.ActorId, Guid.NewGuid()), default);
                return (result.IsSuccess, result.Error?.Code);
            }
            if (target.Kind == "warranty")
            {
                var result = await services.GetRequiredService<ReceiveShopStockWarrantyHandler>().HandleAsync(new ReceiveShopStockWarrantyCommand(
                    target.Target, WarrantyResolutionType.Replaced, target.Seed.ActorId, [target.OriginalUnit],
                    [new ReplacementSerializedUnitInput(identity.SerialNumber, identity.Imei1, identity.Imei2)], null, Guid.NewGuid()), default);
                return (result.IsSuccess, result.Error?.Code);
            }
            var adjusted = await services.GetRequiredService<CreateStockAdjustmentHandler>().HandleAsync(new CreateStockAdjustmentCommand(
                StockAdjustmentMode.Delta, StockAdjustmentReason.Other,
                [new StockAdjustmentItemCommand(target.Seed.ProductId, target.Seed.ProductUnitId, StockAdjustmentDirection.Increase,
                    InventoryBucket.Sellable, 1m, 100m, target.Seed.SupplierId,
                    [new SerializedAdjustmentUnitCommand(identity.SerialNumber, identity.Imei1, identity.Imei2)])], target.Seed.ActorId, Guid.NewGuid()), default);
            return (adjusted.IsSuccess, adjusted.Error?.Code);
        }
        var outcomes = await Task.WhenAll(RunAsync(first, identityA), RunAsync(second, identityB)).WaitAsync(TimeSpan.FromSeconds(45));
        Assert.Equal(1, outcomes.Count(x => x.Success));
        Assert.Contains(outcomes.Single(x => !x.Success).Error,
            new[] { "identity.already_exists", "purchasing.identity_already_exists", "inventory.identity_duplicate", "warranty.replacement_identity_exists" });
        await using var verify = provider.CreateAsyncScope();
        var db = verify.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var normalized = crossSlotImei ? imei : IdentityNormalizationRules.NormalizeSerialNumber(serial);
        Assert.Equal(1, await db.InventoryUnitIdentityClaims.CountAsync(x => x.NormalizedValue == normalized &&
            x.IdentifierType == (crossSlotImei ? ManufacturerIdentifierType.Imei : ManufacturerIdentifierType.Serial)));
        var targets = new[] { first, second };
        for (var i = 0; i < targets.Length; i++)
        {
            var target = targets[i];
            var won = outcomes[i].Success;
            var expectedUnits = (target.Kind == "warranty" ? 1 : 0) + (won ? 1 : 0);
            Assert.Equal(expectedUnits, await db.InventoryUnits.CountAsync(x => x.ProductId == target.Seed.ProductId));
            Assert.Equal(won ? 1m : 0m, await db.StockBalances.Where(x => x.ProductId == target.Seed.ProductId)
                .Select(x => x.SellableQty).SingleOrDefaultAsync());
            if (!won)
            {
                Assert.Equal(target.Kind == "warranty" ? 1 : 0, await db.InventoryLots.CountAsync(x => x.ProductId == target.Seed.ProductId));
                Assert.False(await db.StockAdjustmentItems.AnyAsync(x => x.ProductId == target.Seed.ProductId));
                if (target.Kind == "warranty")
                {
                    Assert.Equal(InventoryUnitStatus.WithSupplier, (await db.InventoryUnits.SingleAsync(x => x.Id == target.OriginalUnit)).Status);
                    Assert.False(await db.WarrantyOperations.AnyAsync(x => x.TargetId == target.Target && x.OperationType == WarrantyOperationType.ShopReceiveReplacement));
                }
            }
            var units = await db.InventoryUnits.Where(x => x.ProductId == target.Seed.ProductId).ToListAsync();
            Assert.Equal(units.Count, units.Select(x => x.TrackingCode).Distinct().Count());
            Assert.Equal(units.Count, units.Select(x => x.ItemSequence).Distinct().Count());
        }
    }

    private static async Task<Workflow> SeedAsync(ServiceProvider provider, string kind)
    {
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var seed = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db);
        (await db.Products.SingleAsync(x => x.Id == seed.ProductId)).ImeiTrackingEnabled = true;
        await db.SaveChangesAsync();
        Guid target = Guid.Empty;
        Guid original = Guid.Empty;
        if (kind is "intake" or "warranty")
        {
            var purchase = await services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(Purchase(seed,
                kind == "warranty" ? [new SerializedIdentityInput("OLD-" + Guid.NewGuid().ToString("N"), NewImei())] : [], kind == "warranty"), default);
            Assert.True(purchase.IsSuccess, purchase.Error?.Message);
            target = purchase.Value!.PurchaseId;
            if (kind == "warranty")
            {
                db.ChangeTracker.Clear();
                var unit = await db.InventoryUnits.SingleAsync(x => x.ProductId == seed.ProductId);
                original = unit.Id;
                var transfer = await services.GetRequiredService<TransferInventoryConditionHandler>().HandleAsync(new TransferInventoryConditionCommand(
                    seed.ProductId, InventoryBucket.Sellable, InventoryBucket.Damaged, 1m, seed.ActorId, "Defect", InventoryUnitIds: [unit.Id]), default);
                Assert.True(transfer.IsSuccess, transfer.Error?.Message);
                var send = await services.GetRequiredService<SendShopStockToSupplierWarrantyHandler>().HandleAsync(new SendShopStockToSupplierWarrantyCommand(
                    seed.ProductId, InventoryBucket.Damaged, 1m, seed.SupplierId, unit.SourcePurchaseItemId, "Fault", seed.ActorId, Guid.NewGuid(), [unit.Id]), default);
                Assert.True(send.IsSuccess, send.Error?.Message);
                target = send.Value;
            }
        }
        return new Workflow(kind, seed, target, original);
    }

    private static CreatePurchaseCommand Purchase(SerializedProductFixture seed, IReadOnlyList<SerializedIdentityInput> identities, bool receive = true) =>
        new(seed.SupplierId, "RACE-" + Guid.NewGuid().ToString("N"), DateOnly.FromDateTime(DateTime.UtcNow), null, 0m,
            PurchaseSettlementMode.External, seed.ActorId, Guid.NewGuid(),
            [new CreatePurchaseLineInput(seed.ProductId, seed.ProductUnitId, 1m, 100m, 5000m, identities)], ReceiveStockImmediately: receive);

    private static string NewImei() => "86" + Random.Shared.NextInt64(100000000000, 999999999999).ToString(System.Globalization.CultureInfo.InvariantCulture);
    private sealed record Workflow(string Kind, SerializedProductFixture Seed, Guid Target, Guid OriginalUnit);
}
