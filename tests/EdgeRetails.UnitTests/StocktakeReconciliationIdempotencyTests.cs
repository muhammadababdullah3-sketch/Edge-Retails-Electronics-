using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Infrastructure;
using EdgeRetails.Infrastructure.Persistence;
using EdgeRetails.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EdgeRetails.UnitTests;

public sealed class StocktakeReconciliationIdempotencyTests
{
    private readonly Phase2TestDoubles _fakes = new();

    private CreateStocktakeHandler CreateCreateStocktakeHandler() =>
        new(_fakes.Inventory, _fakes.Transactions, _fakes.UnitOfWork, _fakes.Clock, null, _fakes.OutcomeLedger);

    private StartStocktakeHandler CreateStartStocktakeHandler() =>
        new(_fakes.Catalog, _fakes.Inventory, _fakes.ResourceLock, _fakes.Transactions, _fakes.UnitOfWork, _fakes.Clock);

    private RecordStocktakeCountHandler CreateRecordStocktakeCountHandler() =>
        new(_fakes.Catalog, _fakes.Inventory, _fakes.Transactions, _fakes.UnitOfWork, _fakes.Clock);

    private RecordSerializedStocktakeHandler CreateRecordSerializedStocktakeHandler() =>
        new(_fakes.Catalog, _fakes.Inventory, _fakes.Transactions, _fakes.UnitOfWork, _fakes.Clock, _fakes.OperationLock, _fakes.OutcomeLedger);

    private ReviewStocktakeHandler CreateReviewStocktakeHandler() =>
        new(_fakes.Inventory, _fakes.Transactions, _fakes.UnitOfWork, _fakes.Clock);

    private PostStocktakeHandler CreatePostStocktakeHandler() =>
        new(_fakes.Catalog, _fakes.Inventory, _fakes.CostAllocator, _fakes.Transactions, _fakes.UnitOfWork, _fakes.Clock, _fakes.OperationLock, _fakes.OutcomeLedger);

    private Product SeedQuantityProduct(string name, string sku, decimal initialQty, decimal unitCost)
    {
        var unit = new Unit { Id = Guid.CreateVersion7(), Name = "Piece", Symbol = "pc" };
        var product = new Product
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            Sku = sku,
            BaseUnitId = unit.Id,
            TrackingMode = TrackingMode.Quantity,
            DefaultSalePrice = unitCost * 1.5m,
            ReferencePurchaseCost = unitCost,
            IsActive = true
        };
        _fakes.Catalog.Products[product.Id] = product;

        var pu = new ProductUnit
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            UnitId = unit.Id,
            FactorToBaseUnit = 1m,
            IsDefaultPurchaseUnit = true,
            IsDefaultSaleUnit = true,
            CanPurchase = true,
            CanSell = true
        };
        _fakes.Catalog.AddProductUnit(pu);

        _fakes.Inventory.AddStockBalance(new StockBalance
        {
            ProductId = product.Id,
            SellableQty = initialQty
        });

        _fakes.Inventory.AddCostState(new ProductCostState
        {
            ProductId = product.Id,
            CostedQty = initialQty,
            TotalInventoryCost = initialQty * unitCost,
            MovingAverageCost = unitCost,
            LastPurchaseCost = unitCost,
            Version = 1
        });

        var lot = new InventoryLot
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            ReceivedQuantity = initialQty,
            OriginalUnitCost = unitCost,
            EffectiveUnitCost = unitCost,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _fakes.Inventory.AddLot(lot);

        _fakes.Inventory.AddLotBucketBalance(new InventoryLotBucketBalance
        {
            LotId = lot.Id,
            StockBucket = InventoryBucket.Sellable,
            Quantity = initialQty
        });

        return product;
    }

    private (Product product, InventoryUnit unit) SeedSerializedProduct(string name, string sku, decimal unitCost)
    {
        var unitType = new Unit { Id = Guid.CreateVersion7(), Name = "Piece", Symbol = "pc" };
        var product = new Product
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            Sku = sku,
            BaseUnitId = unitType.Id,
            TrackingMode = TrackingMode.Serialized,
            SerialTrackingEnabled = true,
            DefaultSalePrice = unitCost * 1.5m,
            ReferencePurchaseCost = unitCost,
            IsActive = true
        };
        _fakes.Catalog.Products[product.Id] = product;

        var pu = new ProductUnit
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            UnitId = unitType.Id,
            FactorToBaseUnit = 1m,
            IsDefaultPurchaseUnit = true,
            IsDefaultSaleUnit = true,
            CanPurchase = true,
            CanSell = true
        };
        _fakes.Catalog.AddProductUnit(pu);

        _fakes.Inventory.AddStockBalance(new StockBalance
        {
            ProductId = product.Id,
            SellableQty = 1m
        });

        _fakes.Inventory.AddCostState(new ProductCostState
        {
            ProductId = product.Id,
            CostedQty = 1m,
            TotalInventoryCost = unitCost,
            MovingAverageCost = unitCost,
            LastPurchaseCost = unitCost,
            Version = 1
        });

        var lot = new InventoryLot
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            ReceivedQuantity = 1m,
            OriginalUnitCost = unitCost,
            EffectiveUnitCost = unitCost,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _fakes.Inventory.AddLot(lot);

        _fakes.Inventory.AddLotBucketBalance(new InventoryLotBucketBalance
        {
            LotId = lot.Id,
            StockBucket = InventoryBucket.Sellable,
            Quantity = 1m
        });

        var invUnit = new InventoryUnit
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            InventoryLotId = lot.Id,
            SerialNumber = $"SN-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
            Status = InventoryUnitStatus.InStock,
            AcquisitionCost = unitCost,
            CreatedAt = DateTimeOffset.UtcNow,
            Version = 1
        };
        _fakes.Inventory.AddInventoryUnit(invUnit);

        return (product, invUnit);
    }

    private (Product product, InventoryUnit unit) SeedContainerProduct(string name, string sku, decimal initialQty, decimal unitCost)
    {
        var unitType = new Unit { Id = Guid.CreateVersion7(), Name = "Meter", Symbol = "m" };
        var product = new Product
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            Sku = sku,
            BaseUnitId = unitType.Id,
            TrackingMode = TrackingMode.Container,
            DefaultSalePrice = unitCost * 1.5m,
            ReferencePurchaseCost = unitCost,
            IsActive = true
        };
        _fakes.Catalog.Products[product.Id] = product;

        var pu = new ProductUnit
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            UnitId = unitType.Id,
            FactorToBaseUnit = 1m,
            IsDefaultPurchaseUnit = true,
            IsDefaultSaleUnit = true,
            CanPurchase = true,
            CanSell = true
        };
        _fakes.Catalog.AddProductUnit(pu);

        _fakes.Inventory.AddStockBalance(new StockBalance
        {
            ProductId = product.Id,
            SellableQty = initialQty
        });

        _fakes.Inventory.AddCostState(new ProductCostState
        {
            ProductId = product.Id,
            CostedQty = initialQty,
            TotalInventoryCost = initialQty * unitCost,
            MovingAverageCost = unitCost,
            LastPurchaseCost = unitCost,
            Version = 1
        });

        var lot = new InventoryLot
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            ReceivedQuantity = initialQty,
            OriginalUnitCost = unitCost,
            EffectiveUnitCost = unitCost,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _fakes.Inventory.AddLot(lot);

        _fakes.Inventory.AddLotBucketBalance(new InventoryLotBucketBalance
        {
            LotId = lot.Id,
            StockBucket = InventoryBucket.Sellable,
            Quantity = initialQty
        });

        var container = new InventoryUnit
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            InventoryLotId = lot.Id,
            TrackingCode = $"COIL-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
            Status = InventoryUnitStatus.InStock,
            InitialBaseQuantity = initialQty,
            RemainingBaseQuantity = initialQty,
            AcquisitionCost = initialQty * unitCost,
            CreatedAt = DateTimeOffset.UtcNow,
            Version = 1
        };
        _fakes.Inventory.AddInventoryUnit(container);

        return (product, container);
    }

    [Fact]
    public async Task Stocktake_Reconciliation_IsIdempotent()
    {
        var product = SeedQuantityProduct("Test Bulb 9W", "BULB-9W", 10m, 50m);
        var actorId = Guid.CreateVersion7();
        var clientOpId = Guid.CreateVersion7();

        var createHandler = CreateCreateStocktakeHandler();
        var startHandler = CreateStartStocktakeHandler();
        var countHandler = CreateRecordStocktakeCountHandler();
        var reviewHandler = CreateReviewStocktakeHandler();
        var postHandler = CreatePostStocktakeHandler();

        var createRes = await createHandler.HandleAsync(
            new CreateStocktakeCommand(StocktakeScope.FullShop, null, actorId, "Idempotency test"),
            CancellationToken.None);
        Assert.True(createRes.IsSuccess);
        var stocktakeId = createRes.Value;

        var startRes = await startHandler.HandleAsync(new StartStocktakeCommand(stocktakeId), CancellationToken.None);
        Assert.True(startRes.IsSuccess);

        var countRes = await countHandler.HandleAsync(
            new RecordStocktakeCountCommand(stocktakeId, product.Id, 12m, actorId, "Surplus count"),
            CancellationToken.None);
        Assert.True(countRes.IsSuccess);

        var reviewRes = await reviewHandler.HandleAsync(new ReviewStocktakeCommand(stocktakeId), CancellationToken.None);
        Assert.True(reviewRes.IsSuccess);

        var postCmd = new PostStocktakeCommand(
            StocktakeId: stocktakeId,
            ActorId: actorId,
            PositiveVarianceUnitCosts: new Dictionary<Guid, decimal> { [product.Id] = 50m },
            ClientOperationId: clientOpId);

        // First execution
        var res1 = await postHandler.HandleAsync(postCmd, CancellationToken.None);
        Assert.True(res1.IsSuccess, res1.Error?.Message);
        Assert.Equal(12m, _fakes.Inventory.Balances[product.Id].SellableQty);
        Assert.Single(_fakes.Inventory.Movements);

        // Idempotent replay with same ClientOperationId
        var res2 = await postHandler.HandleAsync(postCmd, CancellationToken.None);
        Assert.True(res2.IsSuccess, res2.Error?.Message);
        Assert.Equal(12m, _fakes.Inventory.Balances[product.Id].SellableQty);
        Assert.Single(_fakes.Inventory.Movements);

        var outcome = await _fakes.OutcomeLedger.GetOutcomeAsync(clientOpId, CancellationToken.None);
        Assert.NotNull(outcome);
        Assert.Equal(OperationOutcomeState.Succeeded, outcome.State);
    }

    [Fact]
    public async Task Stocktake_ResponseLost_ReplayDoesNotApplyAdjustmentTwice()
    {
        var product = SeedQuantityProduct("Test Cable 10m", "CABLE-10M", 20m, 100m);
        var actorId = Guid.CreateVersion7();
        var clientOpId = Guid.CreateVersion7();

        var createHandler = CreateCreateStocktakeHandler();
        var startHandler = CreateStartStocktakeHandler();
        var countHandler = CreateRecordStocktakeCountHandler();
        var reviewHandler = CreateReviewStocktakeHandler();
        var postHandler = CreatePostStocktakeHandler();

        var createRes = await createHandler.HandleAsync(
            new CreateStocktakeCommand(StocktakeScope.FullShop, null, actorId, "Shortage test"),
            CancellationToken.None);
        var stocktakeId = createRes.Value;

        await startHandler.HandleAsync(new StartStocktakeCommand(stocktakeId), CancellationToken.None);
        await countHandler.HandleAsync(
            new RecordStocktakeCountCommand(stocktakeId, product.Id, 15m, actorId, "5 missing"),
            CancellationToken.None);
        await reviewHandler.HandleAsync(new ReviewStocktakeCommand(stocktakeId), CancellationToken.None);

        var postCmd = new PostStocktakeCommand(
            StocktakeId: stocktakeId,
            ActorId: actorId,
            PositiveVarianceUnitCosts: null,
            ClientOperationId: clientOpId);

        // Initial post succeeds (stock drops from 20 to 15)
        var res1 = await postHandler.HandleAsync(postCmd, CancellationToken.None);
        Assert.True(res1.IsSuccess);
        Assert.Equal(15m, _fakes.Inventory.Balances[product.Id].SellableQty);

        // Client response lost, retry with same clientOpId
        var resReplay = await postHandler.HandleAsync(postCmd, CancellationToken.None);
        Assert.True(resReplay.IsSuccess);

        // Stock must STILL be 15, NOT 10 (not subtracted twice)
        Assert.Equal(15m, _fakes.Inventory.Balances[product.Id].SellableQty);
        Assert.Single(_fakes.Inventory.Movements);
    }

    [Fact]
    public async Task Stocktake_SameOperationIdDifferentPayload_IsRejected()
    {
        var product1 = SeedQuantityProduct("Product Alpha", "SKU-A", 10m, 100m);
        var actorId = Guid.CreateVersion7();
        var clientOpId = Guid.CreateVersion7();

        var createHandler = CreateCreateStocktakeHandler();
        var startHandler = CreateStartStocktakeHandler();
        var countHandler = CreateRecordStocktakeCountHandler();
        var reviewHandler = CreateReviewStocktakeHandler();
        var postHandler = CreatePostStocktakeHandler();

        // Stocktake 1
        var createRes1 = await createHandler.HandleAsync(
            new CreateStocktakeCommand(StocktakeScope.FullShop, null, actorId, "Stocktake 1"),
            CancellationToken.None);
        var stocktakeId1 = createRes1.Value;
        await startHandler.HandleAsync(new StartStocktakeCommand(stocktakeId1), CancellationToken.None);
        await countHandler.HandleAsync(
            new RecordStocktakeCountCommand(stocktakeId1, product1.Id, 8m, actorId, "Count 8"),
            CancellationToken.None);
        await reviewHandler.HandleAsync(new ReviewStocktakeCommand(stocktakeId1), CancellationToken.None);

        var postCmd1 = new PostStocktakeCommand(stocktakeId1, actorId, null, clientOpId);
        var res1 = await postHandler.HandleAsync(postCmd1, CancellationToken.None);
        Assert.True(res1.IsSuccess);

        // Create Stocktake 2 with different parameters
        var product2 = SeedQuantityProduct("Product Beta", "SKU-B", 20m, 200m);
        var createRes2 = await createHandler.HandleAsync(
            new CreateStocktakeCommand(StocktakeScope.FullShop, null, actorId, "Stocktake 2"),
            CancellationToken.None);
        var stocktakeId2 = createRes2.Value;
        await startHandler.HandleAsync(new StartStocktakeCommand(stocktakeId2), CancellationToken.None);
        await countHandler.HandleAsync(
            new RecordStocktakeCountCommand(stocktakeId2, product1.Id, 8m, actorId, "Same count P1"),
            CancellationToken.None);
        await countHandler.HandleAsync(
            new RecordStocktakeCountCommand(stocktakeId2, product2.Id, 20m, actorId, "Count P2"),
            CancellationToken.None);
        await reviewHandler.HandleAsync(new ReviewStocktakeCommand(stocktakeId2), CancellationToken.None);

        // Attempt to post Stocktake 2 reusing the SAME ClientOperationId
        var postCmd2 = new PostStocktakeCommand(stocktakeId2, actorId, null, clientOpId);
        var res2 = await postHandler.HandleAsync(postCmd2, CancellationToken.None);

        Assert.False(res2.IsSuccess);
        Assert.Equal("idempotency.payload_mismatch", res2.Error?.Code);
    }

    [Fact]
    public async Task Stocktake_OutcomeSurvivesServiceRestart()
    {
        var root = new InMemoryDatabaseRoot();
        var dbName = "stocktake_restart_" + Guid.NewGuid();
        var clientOpId = Guid.NewGuid();
        var stocktakeId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var productId = Guid.NewGuid();

        var fingerprint = OperationPayloadFingerprint.ComputeSha256("stocktake-session-payload-123");

        // Provider 1: Record successful stocktake reconciliation outcome in durable EfOperationOutcomeLedger
        var options = new DbContextOptionsBuilder<EdgeRetailsDbContext>()
            .UseInMemoryDatabase(dbName, root)
            .Options;

        await using (var db1 = new EdgeRetailsDbContext(options))
        {
            var ledger1 = new EfOperationOutcomeLedger(db1);
            await ledger1.RecordSuccessAsync(
                clientOpId,
                "Stocktake",
                stocktakeId,
                stocktakeId.ToString("D"),
                actorId: actorId,
                payloadFingerprint: fingerprint);
        }

        // Provider 2: Simulating service restart with fresh DbContext instance against same store
        await using (var db2 = new EdgeRetailsDbContext(options))
        {
            var ledger2 = new EfOperationOutcomeLedger(db2);
            var outcome = await ledger2.GetOutcomeAsync(clientOpId);

            Assert.NotNull(outcome);
            Assert.Equal(OperationOutcomeState.Succeeded, outcome.State);
            Assert.Equal("Stocktake", outcome.OperationType);
            Assert.Equal(stocktakeId, outcome.EntityId);
            Assert.Equal(fingerprint, outcome.PayloadFingerprint);
            Assert.Equal(actorId, outcome.ActorId);
            Assert.True(outcome.WasCommitted);
        }
    }

    [Fact]
    public async Task Stocktake_DoesNotDuplicateInventoryMovement()
    {
        var product = SeedQuantityProduct("Test Switch 16A", "SW-16A", 30m, 80m);
        var actorId = Guid.CreateVersion7();
        var clientOpId = Guid.CreateVersion7();

        var createHandler = CreateCreateStocktakeHandler();
        var startHandler = CreateStartStocktakeHandler();
        var countHandler = CreateRecordStocktakeCountHandler();
        var reviewHandler = CreateReviewStocktakeHandler();
        var postHandler = CreatePostStocktakeHandler();

        var createRes = await createHandler.HandleAsync(
            new CreateStocktakeCommand(StocktakeScope.FullShop, null, actorId, "Movement deduplication test"),
            CancellationToken.None);
        var stocktakeId = createRes.Value;

        await startHandler.HandleAsync(new StartStocktakeCommand(stocktakeId), CancellationToken.None);
        await countHandler.HandleAsync(
            new RecordStocktakeCountCommand(stocktakeId, product.Id, 26m, actorId, "4 short"),
            CancellationToken.None);
        await reviewHandler.HandleAsync(new ReviewStocktakeCommand(stocktakeId), CancellationToken.None);

        var postCmd = new PostStocktakeCommand(stocktakeId, actorId, null, clientOpId);

        // Initial post
        var res1 = await postHandler.HandleAsync(postCmd, CancellationToken.None);
        Assert.True(res1.IsSuccess);
        Assert.Single(_fakes.Inventory.Movements);
        Assert.Equal(clientOpId, _fakes.Inventory.Movements.First().CorrelationId);

        // Replay
        var res2 = await postHandler.HandleAsync(postCmd, CancellationToken.None);
        Assert.True(res2.IsSuccess);

        // Movement count must remain exactly 1
        Assert.Single(_fakes.Inventory.Movements);
    }

    [Fact]
    public async Task SerializedStocktake_DoesNotTransitionSameUnitTwice()
    {
        var (product, unit) = SeedSerializedProduct("Test Smartphone 5G", "PHONE-5G", 1200m);
        var actorId = Guid.CreateVersion7();
        var clientOpId = Guid.CreateVersion7();

        var createHandler = CreateCreateStocktakeHandler();
        var startHandler = CreateStartStocktakeHandler();
        var serializedCountHandler = CreateRecordSerializedStocktakeHandler();
        var reviewHandler = CreateReviewStocktakeHandler();
        var postHandler = CreatePostStocktakeHandler();

        var createRes = await createHandler.HandleAsync(
            new CreateStocktakeCommand(StocktakeScope.FullShop, null, actorId, "Serialized unit missing"),
            CancellationToken.None);
        var stocktakeId = createRes.Value;

        await startHandler.HandleAsync(new StartStocktakeCommand(stocktakeId), CancellationToken.None);

        // Unit not found (missing)
        var countRes = await serializedCountHandler.HandleAsync(
            new RecordSerializedStocktakeCommand(stocktakeId, product.Id, [], [], actorId, "Missing in stocktake"),
            CancellationToken.None);
        Assert.True(countRes.IsSuccess);

        await reviewHandler.HandleAsync(new ReviewStocktakeCommand(stocktakeId), CancellationToken.None);

        var postCmd = new PostStocktakeCommand(stocktakeId, actorId, null, clientOpId);

        // First post: unit transitioned from InStock to Missing
        var res1 = await postHandler.HandleAsync(postCmd, CancellationToken.None);
        Assert.True(res1.IsSuccess);

        var unitAfterPost = _fakes.Inventory.Units.First(u => u.Id == unit.Id);
        Assert.Equal(InventoryUnitStatus.Missing, unitAfterPost.Status);
        Assert.Equal(2, unitAfterPost.Version);
        Assert.Equal(0m, _fakes.Inventory.Balances[product.Id].SellableQty);
        Assert.Single(_fakes.Inventory.Movements);

        // Replay: unit must NOT transition again or fail with state error
        var res2 = await postHandler.HandleAsync(postCmd, CancellationToken.None);
        Assert.True(res2.IsSuccess);

        var unitAfterReplay = _fakes.Inventory.Units.First(u => u.Id == unit.Id);
        Assert.Equal(InventoryUnitStatus.Missing, unitAfterReplay.Status);
        Assert.Equal(2, unitAfterReplay.Version); // Version not incremented again
        Assert.Equal(0m, _fakes.Inventory.Balances[product.Id].SellableQty); // Balance not decremented again
        Assert.Single(_fakes.Inventory.Movements); // No second movement
    }

    [Fact]
    public async Task Stocktake_StaleState_FailsClosed()
    {
        var product = SeedQuantityProduct("Test Adapter 20W", "ADAPT-20W", 10m, 40m);
        var actorId = Guid.CreateVersion7();
        var clientOpId = Guid.CreateVersion7();

        var createHandler = CreateCreateStocktakeHandler();
        var startHandler = CreateStartStocktakeHandler();
        var countHandler = CreateRecordStocktakeCountHandler();
        var reviewHandler = CreateReviewStocktakeHandler();
        var postHandler = CreatePostStocktakeHandler();

        var createRes = await createHandler.HandleAsync(
            new CreateStocktakeCommand(StocktakeScope.FullShop, null, actorId, "Stale state test"),
            CancellationToken.None);
        var stocktakeId = createRes.Value;

        await startHandler.HandleAsync(new StartStocktakeCommand(stocktakeId), CancellationToken.None);
        await countHandler.HandleAsync(
            new RecordStocktakeCountCommand(stocktakeId, product.Id, 8m, actorId, "Count 8"),
            CancellationToken.None);
        await reviewHandler.HandleAsync(new ReviewStocktakeCommand(stocktakeId), CancellationToken.None);

        // Concurrent mutation alters inventory balance before post
        _fakes.Inventory.Balances[product.Id].SellableQty = 15m;

        var postCmd = new PostStocktakeCommand(stocktakeId, actorId, null, clientOpId);
        var res = await postHandler.HandleAsync(postCmd, CancellationToken.None);

        // Fails closed on concurrency conflict
        Assert.False(res.IsSuccess);
        Assert.Equal("inventory.stocktake_concurrency_conflict", res.Error?.Code);

        // Authoritative stock remains 15m (unmodified by stocktake)
        Assert.Equal(15m, _fakes.Inventory.Balances[product.Id].SellableQty);
        Assert.Empty(_fakes.Inventory.Movements);

        // Failure is recorded in the outcome ledger
        var outcome = await _fakes.OutcomeLedger.GetOutcomeAsync(clientOpId, CancellationToken.None);
        Assert.NotNull(outcome);
        Assert.Equal(OperationOutcomeState.Failed, outcome.State);
        Assert.Equal("inventory.stocktake_concurrency_conflict", outcome.ErrorCode);
    }

    [Fact]
    public void Stocktake_ProductionUsesDurableOutcomeLedger()
    {
        var services = new ServiceCollection();
        services.AddEdgeRetailsInfrastructure("Host=127.0.0.1;Port=5432;Database=edgeretails_prod;Username=postgres;Password=postgres");

        var outcomeLedgerDescriptor = services.FirstOrDefault(s => s.ServiceType == typeof(IOperationOutcomeLedger));
        Assert.NotNull(outcomeLedgerDescriptor);
        Assert.Equal(typeof(EfOperationOutcomeLedger), outcomeLedgerDescriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, outcomeLedgerDescriptor.Lifetime);

        var handlerDescriptor = services.FirstOrDefault(s => s.ServiceType == typeof(PostStocktakeHandler));
        Assert.NotNull(handlerDescriptor);
        Assert.Equal(ServiceLifetime.Scoped, handlerDescriptor.Lifetime);

        // Verify constructor accepts IOperationOutcomeLedger
        var ctor = typeof(PostStocktakeHandler).GetConstructors().First();
        var parameterTypes = ctor.GetParameters().Select(p => p.ParameterType).ToArray();
        Assert.Contains(typeof(IOperationOutcomeLedger), parameterTypes);
    }

    [Fact]
    public async Task SerializedStocktake_ContainerMeasurementAndLooseStock_AuthoritativeCountAndReplayConflict()
    {
        var (product, container) = SeedContainerProduct("Heavy Duty Cable 4mm", "CBL-4MM", 100m, 2.50m);
        var actorId = Guid.CreateVersion7();
        var clientOpId = Guid.CreateVersion7();

        var createHandler = CreateCreateStocktakeHandler();
        var startHandler = CreateStartStocktakeHandler();
        var serializedCountHandler = CreateRecordSerializedStocktakeHandler();
        var reviewHandler = CreateReviewStocktakeHandler();
        var postHandler = CreatePostStocktakeHandler();

        var createRes = await createHandler.HandleAsync(
            new CreateStocktakeCommand(StocktakeScope.FullShop, null, actorId, "Container and loose stock reconciliation"),
            CancellationToken.None);
        Assert.True(createRes.IsSuccess);
        var stocktakeId = createRes.Value;

        var startRes = await startHandler.HandleAsync(new StartStocktakeCommand(stocktakeId), CancellationToken.None);
        Assert.True(startRes.IsSuccess);

        // 1. Initial count: Container measured remaining = 55.0m, Separate loose stock = 12.0m. Total counted = 67.0m.
        var measured = new Dictionary<Guid, decimal> { [container.Id] = 55.0m };
        var countCmd1 = new RecordSerializedStocktakeCommand(
            stocktakeId,
            product.Id,
            [container.Id],
            [],
            actorId,
            "Partial container plus loose cut ends",
            ClientOperationId: clientOpId,
            MeasuredContainerQuantities: measured,
            MinimumSellableIncrement: 1m,
            SeparateLooseQuantity: 12.0m);

        var countRes1 = await serializedCountHandler.HandleAsync(countCmd1, CancellationToken.None);
        Assert.True(countRes1.IsSuccess);

        var item = await _fakes.Inventory.GetStocktakeItemForUpdateAsync(stocktakeId, product.Id, CancellationToken.None);
        Assert.NotNull(item);
        Assert.Equal(67.0m, item.CountedSellableQty);
        Assert.Equal(100.0m, item.ExpectedSellableQty);
        Assert.Equal(-33.0m, item.VarianceQty);
        Assert.NotNull(item.ReviewNote);
        Assert.Contains("[Loose stock counted: 12]", item.ReviewNote);

        // 2. Exact same measurement replay under same ClientOperationId preserves identity (idempotent replay)
        var replayRes = await serializedCountHandler.HandleAsync(countCmd1, CancellationToken.None);
        Assert.True(replayRes.IsSuccess);

        // 3. Changed measurement under same ClientOperationId triggers conflict
        var changedMeasurementCmd = countCmd1 with
        {
            MeasuredContainerQuantities = new Dictionary<Guid, decimal> { [container.Id] = 60.0m }
        };
        var conflictRes1 = await serializedCountHandler.HandleAsync(changedMeasurementCmd, CancellationToken.None);
        Assert.False(conflictRes1.IsSuccess);
        Assert.Equal("inventory.stocktake_operation_conflict", conflictRes1.Error?.Code);

        // Changed loose count under same ClientOperationId also triggers conflict
        var changedLooseCmd = countCmd1 with
        {
            SeparateLooseQuantity = 15.0m
        };
        var conflictRes2 = await serializedCountHandler.HandleAsync(changedLooseCmd, CancellationToken.None);
        Assert.False(conflictRes2.IsSuccess);
        Assert.Equal("inventory.stocktake_operation_conflict", conflictRes2.Error?.Code);

        // 4. Negative loose count fails closed
        var negativeLooseCmd = countCmd1 with
        {
            ClientOperationId = Guid.CreateVersion7(),
            SeparateLooseQuantity = -5.0m
        };
        var negRes = await serializedCountHandler.HandleAsync(negativeLooseCmd, CancellationToken.None);
        Assert.False(negRes.IsSuccess);
        Assert.Equal("inventory.stocktake_loose_quantity_negative", negRes.Error?.Code);

        // 5. Post stocktake and verify authoritative balance & movement variance effects
        var reviewRes = await reviewHandler.HandleAsync(new ReviewStocktakeCommand(stocktakeId), CancellationToken.None);
        Assert.True(reviewRes.IsSuccess);

        var postOpId = Guid.CreateVersion7();
        var postRes = await postHandler.HandleAsync(
            new PostStocktakeCommand(stocktakeId, actorId, null, postOpId),
            CancellationToken.None);
        Assert.True(postRes.IsSuccess);

        // Authoritative stock balance updated to 67.0m
        Assert.Equal(67.0m, _fakes.Inventory.Balances[product.Id].SellableQty);

        // Authoritative movement recorded with -33.0m shrinkage
        var movement = _fakes.Inventory.Movements.Single(m => m.ProductId == product.Id);
        Assert.Equal(InventoryMovementType.PhysicalCountCorrection, movement.MovementType);
        Assert.Equal(82.50m, movement.RecognizedLossAmount); // 33m * 2.50

        var effect = _fakes.Inventory.MovementEffects.Single(e => e.MovementId == movement.Id);
        Assert.Equal(-33.0m, effect.QuantityDelta);
        Assert.Equal(100.0m, effect.QuantityBefore);
        Assert.Equal(67.0m, effect.QuantityAfter);
    }
}
