using System.Reflection;
using System.Text.Json;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.IntegrationTests;

[Collection("Phase2PostgresIntegration")]
public sealed class TrackingMultilineRollbackPostgresTests
{
    [Fact]
    public async Task Line_two_cost_failure_after_line_one_sql_flush_rolls_back_every_business_effect()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var seeds = new[] { await Phase2PostgresTestHarness.SeedQuantityProductAsync(db), await Phase2PostgresTestHarness.SeedQuantityProductAsync(db) };
        foreach (var seed in seeds)
        {
            var purchase = await scope.ServiceProvider.GetRequiredService<CreatePurchaseHandler>().HandleAsync(
                new(seed.SupplierId, "MULTI-" + Guid.NewGuid().ToString("N"), new(2026, 10, 7), null, 0m,
                    PurchaseSettlementMode.External, seed.ActorId, Guid.NewGuid(), [new(seed.ProductId, seed.ProductUnitId, 10m, 100m, 150m, [])]), default);
            Assert.True(purchase.IsSuccess, purchase.Error?.ToString());
        }
        var products = seeds.Select(x => x.ProductId).ToArray();
        var before = await SnapshotAsync(db, products);
        var operation = Guid.NewGuid();
        var costs = DispatchProxy.Create<IInventoryCostAllocator, CostFailureProxy>();
        var failure = (CostFailureProxy)costs;
        failure.Initialize(scope.ServiceProvider.GetRequiredService<IInventoryCostAllocator>(), db, products[0]);
        var handler = ActivatorUtilities.CreateInstance<CreateStockAdjustmentHandler>(scope.ServiceProvider, costs);
        var command = new CreateStockAdjustmentCommand(StockAdjustmentMode.Delta, StockAdjustmentReason.PhysicalCountCorrection,
            seeds.Select(x => new StockAdjustmentItemCommand(x.ProductId, x.ProductUnitId, StockAdjustmentDirection.Decrease,
                InventoryBucket.Sellable, 2m, null)).ToArray(), seeds[0].ActorId, operation);
        await Assert.ThrowsAsync<InjectedSecondLineFailure>(() => handler.HandleAsync(command, default));
        Assert.True(failure.ObservedFirstLineFlushed);
        await using var verify = provider.CreateAsyncScope();
        var verified = verify.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Equal(before, await SnapshotAsync(verified, products));
        Assert.False(await verified.StockAdjustments.AnyAsync(x => x.CorrelationId == operation));
        Assert.False(await verified.OperationOutcomes.AnyAsync(x => x.ClientOperationId == operation));
        Assert.False(await verified.BusinessAuditEvents.AnyAsync(x => x.CorrelationId == operation));
    }

    private sealed class InjectedSecondLineFailure : Exception;
    public class CostFailureProxy : DispatchProxy
    {
        private IInventoryCostAllocator _inner = null!;
        private EdgeRetailsDbContext _db = null!;
        private Guid _firstProduct;
        private int _consumptions;
        public bool ObservedFirstLineFlushed { get; private set; }
        internal void Initialize(IInventoryCostAllocator inner, EdgeRetailsDbContext db, Guid firstProduct)
        {
            _inner = inner;
            _db = db;
            _firstProduct = firstProduct;
        }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IInventoryCostAllocator.ConsumeBucketAsync) && ++_consumptions == 2)
            {
                return FailSecondAsync((CancellationToken)args![^1]!);
            }
            return method.Invoke(_inner, args);
        }
        private async Task FailSecondAsync(CancellationToken cancellationToken)
        {
            await _db.SaveChangesAsync(cancellationToken);
            ObservedFirstLineFlushed = await _db.StockBalances.AsNoTracking().AnyAsync(x => x.ProductId == _firstProduct && x.SellableQty == 8m, cancellationToken)
                && await _db.InventoryLotConsumptions.AsNoTracking().AnyAsync(x => _db.InventoryMovements.Any(m => m.Id == x.MovementId && m.ProductId == _firstProduct), cancellationToken);
            Assert.True(ObservedFirstLineFlushed);
            throw new InjectedSecondLineFailure();
        }
    }

    private static async Task<string> SnapshotAsync(EdgeRetailsDbContext db, Guid[] products) => JsonSerializer.Serialize(new
    {
        Stock = await db.StockBalances.AsNoTracking().Where(x => products.Contains(x.ProductId)).OrderBy(x => x.ProductId).ToListAsync(),
        Costs = await db.ProductCostStates.AsNoTracking().Where(x => products.Contains(x.ProductId)).OrderBy(x => x.ProductId).ToListAsync(),
        Lots = await db.InventoryLots.AsNoTracking().Where(x => products.Contains(x.ProductId)).OrderBy(x => x.Id).ToListAsync(),
        Buckets = await db.InventoryLotBucketBalances.AsNoTracking().Where(x => db.InventoryLots.Any(l => l.Id == x.LotId && products.Contains(l.ProductId))).OrderBy(x => x.Id).ToListAsync(),
        Moves = await db.InventoryMovements.AsNoTracking().Where(x => products.Contains(x.ProductId)).OrderBy(x => x.Id).ToListAsync(),
        Consumptions = await db.InventoryLotConsumptions.AsNoTracking().Where(x => db.InventoryMovements.Any(m => m.Id == x.MovementId && products.Contains(m.ProductId))).OrderBy(x => x.Id).ToListAsync(),
        Items = await db.StockAdjustmentItems.AsNoTracking().Where(x => products.Contains(x.ProductId)).OrderBy(x => x.Id).ToListAsync()
    });
}
