using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.IntegrationTests;

[Collection("Phase2PostgresIntegration")]
public sealed class Phase7Pass5CommercialPostgresTests
{
    [Fact]
    public async Task ContainerExchangeRestoresExactOriginalResidualAndPreservesOtherRefundOnReplay()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var seed = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db);
        (await db.Products.SingleAsync(x => x.Id == seed.ProductId)).TrackingMode = TrackingMode.Container;
        (await db.ProductUnits.SingleAsync(x => x.Id == seed.ProductUnitId)).FactorToBaseUnit = 90m;
        await db.SaveChangesAsync();

        async Task<InventoryUnit> ReceiveAsync(decimal cost)
        {
            var prior = await db.InventoryUnits.Where(x => x.ProductId == seed.ProductId).Select(x => x.Id).ToArrayAsync();
            var purchase = await services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new CreatePurchaseCommand(
                seed.SupplierId, "P5-EXCHANGE-" + Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), null, 0m,
                PurchaseSettlementMode.External, seed.ActorId, Guid.NewGuid(),
                [new CreatePurchaseLineInput(seed.ProductId, seed.ProductUnitId, 1m, cost, 500000m,
                    [new SerializedIdentityInput("P5-" + Guid.NewGuid())])]), default);
            Assert.True(purchase.IsSuccess, purchase.Error?.Message);
            db.ChangeTracker.Clear();
            return await db.InventoryUnits.SingleAsync(x => x.ProductId == seed.ProductId && !prior.Contains(x.Id));
        }

        var original = await ReceiveAsync(100m);
        var replacement = await ReceiveAsync(150m);
        (await db.Products.SingleAsync(x => x.Id == seed.ProductId)).DefaultSalePrice = 6000m;
        await db.SaveChangesAsync();
        var sale = await services.GetRequiredService<CompleteSaleHandler>().HandleAsync(new CompleteSaleCommand(
            Guid.NewGuid(), null, seed.ActorId, null, 0m, SalePaymentMethod.Bank, 540000m, "P5", null,
            [new CompleteSaleLineInput(seed.ProductId, seed.ProductUnitId, 1m, 540000m, [original.Id])]), default);
        Assert.True(sale.IsSuccess, sale.Error?.Message);
        db.ChangeTracker.Clear();
        var sold = await db.SaleItems.SingleAsync(x => x.SaleId == sale.Value!.SaleId);
        (await db.Products.SingleAsync(x => x.Id == seed.ProductId)).DefaultSalePrice = 5000m;
        await db.SaveChangesAsync();
        var intent = new CommercialExchangeCommand(Guid.NewGuid(), sale.Value!.SaleId, seed.ActorId, null, null,
            "EXCHANGE", "Exact original carrying value", [new SaleReturnLineInput(sold.Id, 90m,
                SaleReturnDisposition.RestockSellable, [original.Id])],
            [new CompleteSaleLineInput(seed.ProductId, seed.ProductUnitId, 1m, 450000m, [replacement.Id])],
            0m, SalePaymentMethod.Other, 0m, "OTHER-REFUND");
        var result = await services.GetRequiredService<CommercialExchangeHandler>().HandleAsync(intent, default);
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(-90000m, result.Value!.NetDifference);
        var replay = await services.GetRequiredService<CommercialExchangeHandler>().HandleAsync(intent, default);
        Assert.True(replay.IsSuccess, replay.Error?.Message);
        Assert.True(replay.Value!.WasExisting);
        Assert.Equal(result.Value.SaleReturnId, replay.Value.SaleReturnId);

        await using var read = provider.CreateAsyncScope();
        var persisted = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var state = await persisted.ProductCostStates.SingleAsync(x => x.ProductId == seed.ProductId);
        Assert.Equal(100m, state.TotalInventoryCost);
        Assert.Equal(90m, state.CostedQty);
        Assert.Equal(90m, (await persisted.StockBalances.SingleAsync(x => x.ProductId == seed.ProductId)).SellableQty);
        var restored = await persisted.InventoryUnits.SingleAsync(x => x.Id == original.Id);
        Assert.Equal(InventoryUnitStatus.InStock, restored.Status);
        Assert.Equal(original.TrackingCode, restored.TrackingCode);
        Assert.Equal(original.ItemSequence, restored.ItemSequence);
        Assert.Equal(100m, restored.AcquisitionCost);
        Assert.Equal(InventoryUnitStatus.Sold, (await persisted.InventoryUnits.SingleAsync(x => x.Id == replacement.Id)).Status);
        Assert.Equal(2, await persisted.InventoryUnits.CountAsync(x => x.ProductId == seed.ProductId));
        var returned = await persisted.SaleReturns.SingleAsync(x => x.ClientOperationId == intent.ClientOperationId);
        Assert.Equal(RefundMethod.Other, returned.RefundMethod);
        Assert.Equal(540000m, returned.RefundAmount);
        Assert.Single(await persisted.Sales.Where(x => x.ClientOperationId == intent.ClientOperationId).ToListAsync());
        Assert.Empty(await persisted.CashMovements.Where(x => x.ActorId == seed.ActorId).ToListAsync());
    }

    // NEW_COVERAGE: real production DI/transaction, fresh-scope persisted facts.
    [Theory]
    [InlineData(CashMovementDirection.In)]
    [InlineData(CashMovementDirection.Out)]
    public async Task ManualCashCommitsMovementAndAuditTogether(CashMovementDirection direction)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var (actor, session) = await ArrangeAsync(provider);
        try
        {
            Guid movementId;
            await using (var scope = provider.CreateAsyncScope())
            {
                var result = await scope.ServiceProvider.GetRequiredService<RecordManualCashMovementHandler>()
                    .HandleAsync(new(direction, 1.005m, actor, " Verified owner float ", null), default);
                Assert.True(result.IsSuccess, result.Error?.Message);
                movementId = result.Value;
            }
            await using var read = provider.CreateAsyncScope();
            var db = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var movement = Assert.Single(await db.CashMovements.AsNoTracking().Where(x => x.ActorId == actor).ToListAsync());
            Assert.Equal(movementId, movement.Id);
            Assert.Equal(session, movement.CashSessionId);
            Assert.Equal(1.01m, movement.Amount);
            Assert.Equal(direction, movement.Direction);
            Assert.Equal("Verified owner float", movement.Reason);
            Assert.Null(movement.SourceType);
            Assert.Null(movement.SourceId);
            var audit = Assert.Single(await db.BusinessAuditEvents.AsNoTracking().Where(x => x.ActorId == actor).ToListAsync());
            Assert.Equal(movementId, audit.EntityId);
            Assert.Equal(movementId, audit.CorrelationId);
            Assert.Equal(direction == CashMovementDirection.In ? "MANUAL_CASH_IN" : "MANUAL_CASH_OUT", audit.Action);
            var expected = 100m + (direction == CashMovementDirection.In ? 1.01m : -1.01m);
            Assert.Equal(expected, (await db.CashSessions.SingleAsync(x => x.Id == session)).OpeningCash + movement.SignedAmount);
        }
        finally { await CloseAsync(provider, session); }
    }

    [Fact]
    public async Task FailureAfterActualSqlFlushRollsBackMovementAndAudit()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var (actor, session) = await ArrangeAsync(provider);
        try
        {
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
                var handler = ActivatorUtilities.CreateInstance<RecordManualCashMovementHandler>(scope.ServiceProvider, new FlushThenThrow(db));
                await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(new(CashMovementDirection.In, 5m, actor, "Rollback proof", null), default));
            }
            await using var read = provider.CreateAsyncScope();
            var persisted = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            Assert.Empty(await persisted.CashMovements.Where(x => x.ActorId == actor).ToListAsync());
            Assert.Empty(await persisted.BusinessAuditEvents.Where(x => x.ActorId == actor).ToListAsync());
            Assert.Equal(100m, (await persisted.CashSessions.SingleAsync(x => x.Id == session)).OpeningCash);
        }
        finally { await CloseAsync(provider, session); }
    }

    [Theory]
    [InlineData(" ", "1")]
    [InlineData("Below precision", "0.004")]
    public async Task InvalidManualCashLeavesNoPersistedFacts(string reason, string amountText)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var (actor, session) = await ArrangeAsync(provider);
        try
        {
            await using (var scope = provider.CreateAsyncScope())
            {
                var result = await scope.ServiceProvider.GetRequiredService<RecordManualCashMovementHandler>()
                    .HandleAsync(new(CashMovementDirection.In, decimal.Parse(amountText, System.Globalization.CultureInfo.InvariantCulture), actor, reason, null), default);
                Assert.False(result.IsSuccess);
            }
            await using var read = provider.CreateAsyncScope();
            var db = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            Assert.Empty(await db.CashMovements.Where(x => x.ActorId == actor).ToListAsync());
            Assert.Empty(await db.BusinessAuditEvents.Where(x => x.ActorId == actor).ToListAsync());
        }
        finally { await CloseAsync(provider, session); }
    }

    private static async Task<(Guid Actor, Guid Session)> ArrangeAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        // This collection and database are owned by the isolated runner.
        foreach (var open in await db.CashSessions.Where(x => x.Status == CashSessionStatus.Open).ToListAsync())
        {
            open.Status = CashSessionStatus.Closed;
            open.ClosedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync();
        var actor = await IntegrationIdentitySeeder.CreateActorAsync(db);
        var session = await Phase2PostgresTestHarness.SeedOpenCashSessionAsync(db, actor, 100m);
        return (actor, session.Id);
    }

    private static async Task CloseAsync(ServiceProvider provider, Guid session)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var row = await db.CashSessions.SingleAsync(x => x.Id == session);
        row.Status = CashSessionStatus.Closed;
        row.ClosedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private sealed class FlushThenThrow(EdgeRetailsDbContext db) : IUnitOfWork
    {
        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        {
            await db.SaveChangesAsync(cancellationToken);
            Assert.True(await db.CashMovements.AnyAsync(x => x.Reason == "Rollback proof", cancellationToken));
            throw new InvalidOperationException("Owned test fault after provisional SQL flush.");
        }
    }

    [Fact]
    public async Task MissingUnit_SaleAndCommercialExchangeAreRefusedWithoutBusinessMutation()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        Guid productId, productUnitId, supplierId, actorId;
        Guid missingUnitId, soldUnitId;
        decimal unitPrice = 5000m;
        decimal cost = 100m;

        // 1. Arrange: Seed serialized product and receive 2 physical units via purchase
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
            var seed = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db, unitPrice);
            productId = seed.ProductId;
            productUnitId = seed.ProductUnitId;
            supplierId = seed.SupplierId;
            actorId = seed.ActorId;

            var purchase = await scope.ServiceProvider.GetRequiredService<CreatePurchaseHandler>().HandleAsync(
                new CreatePurchaseCommand(
                    supplierId,
                    "P5-ARRANGE-" + Guid.NewGuid(),
                    DateOnly.FromDateTime(DateTime.UtcNow),
                    null,
                    0m,
                    PurchaseSettlementMode.External,
                    actorId,
                    Guid.NewGuid(),
                    [new CreatePurchaseLineInput(
                        productId,
                        productUnitId,
                        2m,
                        cost,
                        unitPrice,
                        [new SerializedIdentityInput("P5-UNIT-A-" + Guid.NewGuid()),
                         new SerializedIdentityInput("P5-UNIT-B-" + Guid.NewGuid())])]),
                default);
            Assert.True(purchase.IsSuccess, purchase.Error?.Message);

            var units = await db.InventoryUnits.AsNoTracking().Where(x => x.ProductId == productId).OrderBy(x => x.Id).ToArrayAsync();
            Assert.Equal(2, units.Length);
            missingUnitId = units[0].Id;
            soldUnitId = units[1].Id;
        }

        // 2. Post explicit shortage for Unit A (Lost) -> transitions to InventoryUnitStatus.Missing
        var missingOperationId = Guid.NewGuid();
        await using (var scope = provider.CreateAsyncScope())
        {
            var adjustmentHandler = scope.ServiceProvider.GetRequiredService<CreateStockAdjustmentHandler>();
            var shortage = await adjustmentHandler.HandleAsync(
                new CreateStockAdjustmentCommand(
                    StockAdjustmentMode.Delta,
                    StockAdjustmentReason.Lost,
                    [new StockAdjustmentItemCommand(
                        productId,
                        null,
                        StockAdjustmentDirection.Decrease,
                        InventoryBucket.Sellable,
                        1m,
                        null,
                        InventoryUnitIds: [missingUnitId])],
                    actorId,
                    missingOperationId),
                default);
            Assert.True(shortage.IsSuccess, shortage.Error?.Message);
        }

        // 3. Complete legitimate sale for Unit B (InStock -> Sold) to use in exchange scenarios
        Guid legitimateSaleId;
        Guid legitimateSaleItemId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var saleHandler = scope.ServiceProvider.GetRequiredService<CompleteSaleHandler>();
            var legitimateSale = await saleHandler.HandleAsync(
                new CompleteSaleCommand(
                    Guid.NewGuid(),
                    null,
                    actorId,
                    null,
                    0m,
                    SalePaymentMethod.Bank,
                    unitPrice,
                    "Legitimate sale of Unit B",
                    null,
                    [new CompleteSaleLineInput(productId, productUnitId, 1m, unitPrice, [soldUnitId])]),
                default);
            Assert.True(legitimateSale.IsSuccess, legitimateSale.Error?.Message);
            legitimateSaleId = legitimateSale.Value!.SaleId;

            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            legitimateSaleItemId = (await db.SaleItems.SingleAsync(x => x.SaleId == legitimateSaleId)).Id;
        }

        // Snapshot database state before refusal attempts
        await using (var snapshotScope = provider.CreateAsyncScope())
        {
            var db = snapshotScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var missingUnit = await db.InventoryUnits.SingleAsync(x => x.Id == missingUnitId);
            Assert.Equal(InventoryUnitStatus.Missing, missingUnit.Status);
            var soldUnit = await db.InventoryUnits.SingleAsync(x => x.Id == soldUnitId);
            Assert.Equal(InventoryUnitStatus.Sold, soldUnit.Status);
        }

        int baselineSalesCount, baselineSaleItemsCount, baselineMovementsCount, baselineReturnsCount;
        long missingUnitVersionBefore, soldUnitVersionBefore;
        await using (var countScope = provider.CreateAsyncScope())
        {
            var db = countScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            baselineSalesCount = await db.Sales.CountAsync();
            baselineSaleItemsCount = await db.SaleItems.CountAsync();
            baselineMovementsCount = await db.InventoryMovements.CountAsync();
            baselineReturnsCount = await db.SaleReturns.CountAsync();
            missingUnitVersionBefore = (await db.InventoryUnits.SingleAsync(x => x.Id == missingUnitId)).Version;
            soldUnitVersionBefore = (await db.InventoryUnits.SingleAsync(x => x.Id == soldUnitId)).Version;
        }

        // 4. REFUSAL VECTOR A: Attempting to SELL the Missing unit MUST FAIL with sales.serial_not_sellable
        var saleMissingOpId = Guid.NewGuid();
        await using (var scope = provider.CreateAsyncScope())
        {
            var saleHandler = scope.ServiceProvider.GetRequiredService<CompleteSaleHandler>();
            var refusedSale = await saleHandler.HandleAsync(
                new CompleteSaleCommand(
                    saleMissingOpId,
                    null,
                    actorId,
                    null,
                    0m,
                    SalePaymentMethod.Bank,
                    unitPrice,
                    "Attempt to sell Missing unit",
                    null,
                    [new CompleteSaleLineInput(productId, productUnitId, 1m, unitPrice, [missingUnitId])]),
                default);

            Assert.False(refusedSale.IsSuccess);
            Assert.Equal("sales.serial_not_sellable", refusedSale.Error?.Code);
        }

        // Verify zero mutation after refused sale
        await using (var verifyScope = provider.CreateAsyncScope())
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            Assert.Equal(baselineSalesCount, await db.Sales.CountAsync());
            Assert.Equal(baselineSaleItemsCount, await db.SaleItems.CountAsync());
            Assert.Equal(baselineMovementsCount, await db.InventoryMovements.CountAsync());
            Assert.False(await db.Sales.AnyAsync(x => x.ClientOperationId == saleMissingOpId));

            var missingUnit = await db.InventoryUnits.SingleAsync(x => x.Id == missingUnitId);
            Assert.Equal(InventoryUnitStatus.Missing, missingUnit.Status);
            Assert.Equal(missingUnitVersionBefore, missingUnit.Version);
        }

        // 5. REFUSAL VECTOR B: Attempting to use Missing unit as REPLACEMENT in CommercialExchange MUST FAIL
        var exchangeReplacementOpId = Guid.NewGuid();
        await using (var scope = provider.CreateAsyncScope())
        {
            var exchangeHandler = scope.ServiceProvider.GetRequiredService<CommercialExchangeHandler>();
            var refusedExchange = await exchangeHandler.HandleAsync(
                new CommercialExchangeCommand(
                    exchangeReplacementOpId,
                    legitimateSaleId,
                    actorId,
                    null,
                    null,
                    "EXCHANGE_ATTEMPT",
                    "Attempting Missing unit as replacement",
                    [new SaleReturnLineInput(legitimateSaleItemId, 1m, SaleReturnDisposition.RestockSellable, [soldUnitId])],
                    [new CompleteSaleLineInput(productId, productUnitId, 1m, unitPrice, [missingUnitId])],
                    0m,
                    SalePaymentMethod.Other,
                    0m,
                    "REFUND"),
                default);

            Assert.False(refusedExchange.IsSuccess);
            Assert.Equal("sales.serial_not_sellable", refusedExchange.Error?.Code);
        }

        // Verify zero mutation after refused exchange replacement
        await using (var verifyScope = provider.CreateAsyncScope())
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            Assert.Equal(baselineSalesCount, await db.Sales.CountAsync());
            Assert.Equal(baselineReturnsCount, await db.SaleReturns.CountAsync());
            Assert.Equal(baselineMovementsCount, await db.InventoryMovements.CountAsync());
            Assert.False(await db.SaleReturns.AnyAsync(x => x.ClientOperationId == exchangeReplacementOpId));

            var missingUnit = await db.InventoryUnits.SingleAsync(x => x.Id == missingUnitId);
            Assert.Equal(InventoryUnitStatus.Missing, missingUnit.Status);
            Assert.Equal(missingUnitVersionBefore, missingUnit.Version);

            var soldUnit = await db.InventoryUnits.SingleAsync(x => x.Id == soldUnitId);
            Assert.Equal(InventoryUnitStatus.Sold, soldUnit.Status);
            Assert.Equal(soldUnitVersionBefore, soldUnit.Version);
        }

        // 6. REFUSAL VECTOR C: Attempting to RETURN the Missing unit in CreateSaleReturnHandler MUST FAIL
        var returnMissingOpId = Guid.NewGuid();
        await using (var scope = provider.CreateAsyncScope())
        {
            var returnHandler = scope.ServiceProvider.GetRequiredService<CreateSaleReturnHandler>();
            var refusedReturn = await returnHandler.HandleAsync(
                new CreateSaleReturnCommand(
                    legitimateSaleId,
                    "RETURN_MISSING",
                    "Testing missing return refusal",
                    RefundMethod.Other,
                    actorId,
                    returnMissingOpId,
                    [new SaleReturnLineInput(legitimateSaleItemId, 1m, SaleReturnDisposition.RestockSellable, [missingUnitId])]),
                default);

            Assert.False(refusedReturn.IsSuccess);
            Assert.Contains(refusedReturn.Error?.Code, new[] { "sales.return_serial_not_original", "sales.return_serial_not_eligible" });
        }

        // 7. Verify final invariant: DB facts remain perfectly intact
        await using (var finalScope = provider.CreateAsyncScope())
        {
            var db = finalScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            Assert.Equal(baselineSalesCount, await db.Sales.CountAsync());
            Assert.Equal(baselineReturnsCount, await db.SaleReturns.CountAsync());
            Assert.Equal(baselineMovementsCount, await db.InventoryMovements.CountAsync());

            var missingUnit = await db.InventoryUnits.SingleAsync(x => x.Id == missingUnitId);
            Assert.Equal(InventoryUnitStatus.Missing, missingUnit.Status);
            Assert.Equal(missingUnitVersionBefore, missingUnit.Version);

            var balance = await db.StockBalances.SingleAsync(x => x.ProductId == productId);
            Assert.Equal(0m, balance.SellableQty);
        }
    }
}
