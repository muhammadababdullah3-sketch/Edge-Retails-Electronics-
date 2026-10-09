using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Inventory;

namespace EdgeRetails.UnitTests;

// NEW_COVERAGE. Relational rollback and conservation require the PostgreSQL tests.
public sealed class Phase7Pass5InventoryRecoveryTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("wrong_unit")]
    [InlineData("wrong_product")]
    [InlineData("wrong_source")]
    [InlineData("duplicate_link")]
    [InlineData("duplicate_effect")]
    [InlineData("negative_loss")]
    [InlineData("scrap")]
    [InlineData("quantity")]
    [InlineData("cost")]
    public void FoundRecovery_RequiresExactSourceIdentityAndConditionWithoutNegativeLoss(string vector)
    {
        var source = new MissingRecoverySource(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1m, 0.004m, 0.01m, Guid.NewGuid());
        var movement = new InventoryMovement { ProductId = source.ProductId, ReferenceType = FoundInventoryUnitHandler.RecoveryReferenceType,
            ReferenceId = source.SourceMovementId, MovementType = InventoryMovementType.StockAdjustment, ActorId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(), Reason = "Physically found", UnitCostSnapshot = 0.004m };
        var link = new InventoryMovementUnit { MovementId = movement.Id, InventoryUnitId = source.UnitId,
            FromStatus = InventoryUnitStatus.Missing, ToStatus = InventoryUnitStatus.Damaged };
        var effect = new InventoryMovementEffect { MovementId = movement.Id, StockBucket = InventoryBucket.Damaged,
            QuantityDelta = 1m, QuantityBefore = 0m, QuantityAfter = 1m };
        var evidence = new InventoryMovementEvidence(movement, [link], [effect], []);
        switch (vector)
        {
            case "wrong_unit": link.InventoryUnitId = Guid.NewGuid(); break;
            case "wrong_product": movement.ProductId = Guid.NewGuid(); break;
            case "wrong_source": movement.ReferenceId = Guid.NewGuid(); break;
            case "duplicate_link": evidence = evidence with { Units = [link, link] }; break;
            case "duplicate_effect": evidence = evidence with { Effects = [effect, effect] }; break;
            case "negative_loss": movement.RecognizedLossAmount = -0.01m; break;
            case "scrap": effect.StockBucket = InventoryBucket.Scrap; link.ToStatus = InventoryUnitStatus.Scrapped; break;
            case "quantity": effect.QuantityDelta = 2m; effect.QuantityAfter = 2m; break;
            case "cost": movement.UnitCostSnapshot = 100m; break;
        }
        if (vector == "valid")
        {
            FoundRecoveryAuthority.ValidateFound(evidence, source);
            Assert.Equal(0.004m, source.RemovedValue); Assert.Equal(0.01m, source.RecognizedLoss);
            Assert.Equal(InventoryUnitStatus.Damaged, link.ToStatus);
        }
        else
        {
            Assert.Throws<EdgeRetails.Domain.Common.BusinessRuleException>(() => FoundRecoveryAuthority.ValidateFound(evidence, source));
        }
    }

    [Fact]
    public void Missing12_HasNoActiveAccountingAndDoesNotRedefineRetainedScrap()
    {
        Assert.Equal(12, (int)InventoryUnitStatus.Missing);
        InventoryUnitAccountingPolicy.ValidateAllStatusesCovered();
        var missing = InventoryUnitAccountingPolicy.GetRule(InventoryUnitStatus.Missing);
        Assert.False(missing.ContributesToStockBalance); Assert.Null(missing.AuthoritativeBucket);
        Assert.False(missing.ContributesToProductCostState); Assert.False(missing.MayBeSold); Assert.False(missing.MayBeReturned);
        var scrap = InventoryUnitAccountingPolicy.GetRule(InventoryUnitStatus.Scrapped);
        Assert.Equal(8, (int)scrap.Status); Assert.True(scrap.ContributesToStockBalance);
        Assert.Equal(InventoryBucket.Scrap, scrap.AuthoritativeBucket); Assert.False(scrap.ContributesToProductCostState);
    }

    [Theory]
    [InlineData("lost", HistoricalMissingClassification.CONCLUSIVE)]
    [InlineData("free", HistoricalMissingClassification.CONCLUSIVE)]
    [InlineData("stocktake", HistoricalMissingClassification.CONCLUSIVE)]
    [InlineData("retained_scrap", HistoricalMissingClassification.NOT_MISSING)]
    [InlineData("not_scrapped", HistoricalMissingClassification.NOT_MISSING)]
    [InlineData("other", HistoricalMissingClassification.AMBIGUOUS)]
    [InlineData("unposted", HistoricalMissingClassification.AMBIGUOUS)]
    [InlineData("later_transition", HistoricalMissingClassification.AMBIGUOUS)]
    [InlineData("grouped", HistoricalMissingClassification.AMBIGUOUS)]
    [InlineData("missing_value", HistoricalMissingClassification.AMBIGUOUS)]
    [InlineData("wrong_lot", HistoricalMissingClassification.AMBIGUOUS)]
    [InlineData("wrong_effect", HistoricalMissingClassification.AMBIGUOUS)]
    [InlineData("wrong_loss", HistoricalMissingClassification.AMBIGUOUS)]
    public void HistoricalClassifier_IsConservativeRepeatableAndNeverMutatesHistory(
        string vector, HistoricalMissingClassification expected)
    {
        var amount = vector == "free" ? 0m : 100m;
        var unit = new InventoryUnit { ProductId = Guid.NewGuid(), InventoryLotId = Guid.NewGuid(), Status = InventoryUnitStatus.Scrapped,
            TrackingCode = "P5-HISTORY-000001", ItemSequence = 1, AcquisitionCost = amount };
        var adjustment = new StockAdjustment { ActorId = Guid.NewGuid(), CorrelationId = Guid.NewGuid(), Reason = StockAdjustmentReason.Lost };
        var item = new StockAdjustmentItem { StockAdjustmentId = adjustment.Id, ProductId = unit.ProductId,
            Direction = StockAdjustmentDirection.Decrease, TargetBucket = InventoryBucket.Sellable, BaseQuantity = 1m, TotalCostSnapshot = amount };
        var movement = new InventoryMovement { ProductId = unit.ProductId, ActorId = adjustment.ActorId,
            CorrelationId = adjustment.CorrelationId, MovementType = InventoryMovementType.StockAdjustment,
            ReferenceType = "STOCK_ADJUSTMENT", ReferenceId = item.Id, RecognizedLossAmount = amount, OccurredAt = DateTimeOffset.UtcNow };
        var link = new InventoryMovementUnit { MovementId = movement.Id, InventoryUnitId = unit.Id,
            FromStatus = InventoryUnitStatus.InStock, ToStatus = InventoryUnitStatus.Scrapped };
        var effect = new InventoryMovementEffect { MovementId = movement.Id, StockBucket = InventoryBucket.Sellable,
            QuantityDelta = -1m, QuantityBefore = 1m, QuantityAfter = 0m };
        var consumed = new InventoryLotConsumption { MovementId = movement.Id, LotId = unit.InventoryLotId.Value,
            Quantity = 1m, UnitCostSnapshot = amount, TotalCostSnapshot = amount };
        var stocktake = new Stocktake { Status = StocktakeStatus.Posted, PostedAt = movement.OccurredAt };
        var counted = new StocktakeItem { StocktakeId = stocktake.Id, ProductId = unit.ProductId, ExpectedSellableQty = 1m, CountedSellableQty = 0m };
        var check = new StocktakeUnitCheck { StocktakeItemId = counted.Id, InventoryUnitId = unit.Id, Result = StocktakeUnitCheckResult.Missing };
        var evidence = new HistoricalMissingEvidence(unit, [movement], [link], [effect], [consumed], [adjustment], [item], [], [], []);
        switch (vector)
        {
            case "stocktake":
            case "unposted":
                movement.ReferenceType = "STOCKTAKE"; movement.ReferenceId = stocktake.Id;
                movement.MovementType = InventoryMovementType.PhysicalCountCorrection;
                if (vector == "unposted") { stocktake.Status = StocktakeStatus.Review; stocktake.PostedAt = null; }
                evidence = evidence with { Stocktakes = [stocktake], StocktakeItems = [counted], StocktakeChecks = [check] };
                break;
            case "retained_scrap":
                evidence = evidence with { Effects = [effect, new() { MovementId = movement.Id, StockBucket = InventoryBucket.Scrap, QuantityDelta = 1m }] };
                break;
            case "not_scrapped": unit.Status = InventoryUnitStatus.InStock; break;
            case "other": adjustment.Reason = StockAdjustmentReason.Other; break;
            case "later_transition":
                var later = new InventoryMovement { ProductId = unit.ProductId, OccurredAt = movement.OccurredAt.AddSeconds(1) };
                evidence = evidence with { Movements = [movement, later], UnitLinks = [link, new() { MovementId = later.Id,
                    InventoryUnitId = unit.Id, FromStatus = InventoryUnitStatus.Scrapped, ToStatus = InventoryUnitStatus.InStock }] };
                break;
            case "grouped": evidence = evidence with { UnitLinks = [link, new() { MovementId = movement.Id, InventoryUnitId = Guid.NewGuid(), ToStatus = InventoryUnitStatus.Scrapped }] }; break;
            case "missing_value": evidence = evidence with { Consumptions = [] }; break;
            case "wrong_lot": consumed.LotId = Guid.NewGuid(); break;
            case "wrong_effect": effect.QuantityDelta = -2m; break;
            case "wrong_loss": movement.RecognizedLossAmount = 99m; break;
        }
        var before = System.Text.Json.JsonSerializer.Serialize(evidence);
        var classifier = new HistoricalMissingClassifier();
        var first = classifier.Classify(evidence);
        Assert.Equal(expected, first.Classification);
        Assert.Equal(first, classifier.Classify(evidence));
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(evidence));
    }

    [Theory]
    [InlineData(StockAdjustmentDirection.Increase)]
    [InlineData(StockAdjustmentDirection.Decrease)]
    public async Task DamagedAdjustment_RequiresNeutralConditionAuthority(StockAdjustmentDirection direction)
    {
        var (handler, inventory, product, audit) = BulkFixture();
        var result = await handler.HandleAsync(new(StockAdjustmentMode.Delta, StockAdjustmentReason.Damaged,
            [new(product.Id, null, direction, InventoryBucket.Sellable, 1m, 150m)], Guid.NewGuid(), Guid.NewGuid()), default);
        Assert.Equal("inventory.condition_transfer_required", result.Error?.Code);
        Assert.Equal(20m, inventory.Balances[product.Id].SellableQty);
        Assert.Equal(3000m, inventory.CostStates[product.Id].TotalInventoryCost);
        Assert.Empty(inventory.StockAdjustments);
        Assert.Empty(inventory.Movements);
        Assert.Empty(audit.Records);
    }

    [Theory]
    [InlineData(StockAdjustmentReason.OpeningStock, InventoryBucket.Sellable, null, "inventory.cost_basis_required")]
    [InlineData(StockAdjustmentReason.Other, InventoryBucket.Sellable, null, "inventory.cost_basis_required")]
    [InlineData(StockAdjustmentReason.OpeningStock, InventoryBucket.Scrap, 100, "inventory.scrap_zero_carrying_required")]
    public async Task PositiveAdjustment_RejectsUnprovenOrScrapCostBasis(
        StockAdjustmentReason reason, InventoryBucket bucket, int? cost, string code)
    {
        var (handler, inventory, product, audit) = BulkFixture();
        var result = await handler.HandleAsync(new(StockAdjustmentMode.Delta, reason,
            [new(product.Id, null, StockAdjustmentDirection.Increase, bucket, 1m, cost)], Guid.NewGuid(), Guid.NewGuid()), default);
        Assert.Equal(code, result.Error?.Code);
        Assert.Equal(20m, inventory.Balances[product.Id].SellableQty);
        Assert.Equal(0m, inventory.Balances[product.Id].ScrapQty);
        Assert.Equal(3000m, inventory.CostStates[product.Id].TotalInventoryCost);
        Assert.Empty(inventory.StockAdjustments);
        Assert.Empty(inventory.Movements);
        Assert.Empty(audit.Records);
    }

    private static (CreateStockAdjustmentHandler Handler, FakeInventoryRepository Inventory, Product Product,
        FakeBusinessAuditWriter Audit) BulkFixture()
    {
        var catalog = new FakeCatalogRepository();
        var inventory = new FakeInventoryRepository();
        var product = new Product { Name = "Pass5 guards", Sku = "P5-GUARD", TrackingMode = TrackingMode.Quantity, IsActive = true };
        catalog.Products[product.Id] = product;
        inventory.Balances[product.Id] = new StockBalance { ProductId = product.Id, SellableQty = 20m };
        inventory.CostStates[product.Id] = new ProductCostState
        {
            ProductId = product.Id, CostedQty = 20m, TotalInventoryCost = 3000m, MovingAverageCost = 150m
        };
        var lot = new InventoryLot { ProductId = product.Id, ReceivedQuantity = 20m, OriginalUnitCost = 150m, EffectiveUnitCost = 150m };
        inventory.Lots.Add(lot);
        inventory.LotBucketBalances.Add(new() { LotId = lot.Id, StockBucket = InventoryBucket.Sellable, Quantity = 20m });
        var audit = new FakeBusinessAuditWriter();
        var handler = new CreateStockAdjustmentHandler(catalog, inventory, new FakeInventoryCostAllocator(inventory),
            new FakePartyRepository(), new FakeTraceabilityRepository(), new FakeResourceLock(), audit,
            new FakeClock(DateTimeOffset.UtcNow), new FakeTransactionRunner(), new FakePermissionAuthorizer(),
            new FakeUnitOfWork(), new InMemoryOperationOutcomeLedger(), operationLock: new FakeOperationLock());
        return (handler, inventory, product, audit);
    }

    [Theory]
    [InlineData(0, null, InventoryBucket.Sellable)]
    [InlineData(null, 0, InventoryBucket.Sellable)]
    [InlineData(null, 20, InventoryBucket.Sellable)]
    [InlineData(0, null, InventoryBucket.Scrap)]
    public async Task PositiveAdjustment_PreservesExplicitFreeReferenceAndZeroScrapSemantics(
        int? explicitCost, int? referenceCost, InventoryBucket bucket)
    {
        var (handler, inventory, product, audit) = BulkFixture();
        product.ReferencePurchaseCost = referenceCost;
        var result = await handler.HandleAsync(new(StockAdjustmentMode.Delta, StockAdjustmentReason.OpeningStock,
            [new(product.Id, null, StockAdjustmentDirection.Increase, bucket, 1m, explicitCost)], Guid.NewGuid(), Guid.NewGuid()), default);
        Assert.True(result.IsSuccess, result.Error?.Message);
        var cost = explicitCost ?? referenceCost!.Value;
        Assert.Equal(bucket == InventoryBucket.Scrap ? 20m : 21m, inventory.CostStates[product.Id].CostedQty);
        Assert.Equal(3000m + cost, inventory.CostStates[product.Id].TotalInventoryCost);
        Assert.Equal(bucket == InventoryBucket.Scrap ? 1m : 0m, inventory.Balances[product.Id].ScrapQty);
        Assert.Equal(bucket == InventoryBucket.Sellable ? 21m : 20m, inventory.Balances[product.Id].SellableQty);
        Assert.Equal(0m, Assert.Single(inventory.Movements).RecognizedLossAmount);
        Assert.Single(audit.Records);
    }

    [Theory]
    [InlineData(StockAdjustmentMode.Delta, StockAdjustmentReason.Lost, 6, 150)]
    [InlineData(StockAdjustmentMode.Delta, StockAdjustmentReason.Other, 20, 150)]
    [InlineData(StockAdjustmentMode.SetPhysicalCount, StockAdjustmentReason.PhysicalCountCorrection, 14, 150)]
    [InlineData(StockAdjustmentMode.Delta, StockAdjustmentReason.Lost, 6, 0)]
    public async Task NegativeAdjustment_UsesActualAllocatorValueAndReplaysOnce(
        StockAdjustmentMode mode, StockAdjustmentReason reason, int quantity, int cost)
    {
        var catalog = new FakeCatalogRepository();
        var inventory = new FakeInventoryRepository();
        var product = new Product { Name = "A01", Sku = "P5-A01", TrackingMode = TrackingMode.Quantity, IsActive = true };
        catalog.Products[product.Id] = product;
        inventory.Balances[product.Id] = new StockBalance { ProductId = product.Id, SellableQty = 20m };
        inventory.CostStates[product.Id] = new ProductCostState
        {
            ProductId = product.Id, CostedQty = 20m, TotalInventoryCost = 20m * cost, MovingAverageCost = cost
        };
        var lot = new InventoryLot { ProductId = product.Id, OriginalUnitCost = cost, EffectiveUnitCost = cost, ReceivedQuantity = 20m };
        inventory.Lots.Add(lot);
        inventory.LotBucketBalances.Add(new() { LotId = lot.Id, StockBucket = InventoryBucket.Sellable, Quantity = 20m });
        var audit = new FakeBusinessAuditWriter();
        var handler = new CreateStockAdjustmentHandler(catalog, inventory, new FakeInventoryCostAllocator(inventory),
            new FakePartyRepository(), new FakeTraceabilityRepository(), new FakeResourceLock(), audit,
            new FakeClock(DateTimeOffset.UtcNow), new FakeTransactionRunner(), new FakePermissionAuthorizer(),
            new FakeUnitOfWork(), new InMemoryOperationOutcomeLedger(), operationLock: new FakeOperationLock());
        var command = new CreateStockAdjustmentCommand(mode, reason,
            [new(product.Id, null, StockAdjustmentDirection.Decrease, InventoryBucket.Sellable, quantity, 9999m)],
            Guid.NewGuid(), Guid.NewGuid());
        var first = await handler.HandleAsync(command, default);
        Assert.True(first.IsSuccess, first.Error?.Message);
        var replay = await handler.HandleAsync(command, default);
        Assert.True(replay.IsSuccess, replay.Error?.Message);
        Assert.Equal(first.Value, replay.Value);
        var removed = mode == StockAdjustmentMode.SetPhysicalCount ? 20m - quantity : quantity;
        var movement = Assert.Single(inventory.Movements);
        var item = Assert.Single(inventory.StockAdjustmentItems);
        Assert.Equal(removed * cost, movement.RecognizedLossAmount);
        Assert.Equal(removed * cost, item.TotalCostSnapshot);
        Assert.Equal((decimal)cost, movement.UnitCostSnapshot);
        Assert.Equal((decimal)cost, item.UnitCostSnapshot);
        Assert.Equal(20m - removed, inventory.Balances[product.Id].SellableQty);
        Assert.Equal((20m - removed) * cost, inventory.CostStates[product.Id].TotalInventoryCost);
        Assert.Single(audit.Records);
    }
}
