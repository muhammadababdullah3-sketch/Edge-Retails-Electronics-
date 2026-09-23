using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Thaka;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Thaka;
using Xunit;

namespace EdgeRetails.UnitTests;

public sealed class Phase2StocktakeThakaCashSessionBehavioralTests
{
    private readonly Phase2TestDoubles _fakes = new();
    private readonly CashMovementService _cashMovementService;

    public Phase2StocktakeThakaCashSessionBehavioralTests()
    {
        _cashMovementService = new CashMovementService(_fakes.Cash, _fakes.Clock);
    }

    private OpenCashSessionHandler CreateOpenCashSessionHandler() =>
        new(_fakes.Cash, _fakes.Clock, _fakes.Transactions, _fakes.UnitOfWork);

    private RecordManualCashMovementHandler CreateRecordManualCashMovementHandler() =>
        new(_cashMovementService, _fakes.Transactions, _fakes.UnitOfWork);

    private CloseCashSessionHandler CreateCloseCashSessionHandler() =>
        new(_fakes.Cash, _fakes.Clock, _fakes.Transactions, _fakes.UnitOfWork);

    private CreateStocktakeHandler CreateCreateStocktakeHandler() =>
        new(_fakes.Inventory, _fakes.Transactions, _fakes.UnitOfWork, _fakes.Clock);

    private StartStocktakeHandler CreateStartStocktakeHandler() =>
        new(_fakes.Catalog, _fakes.Inventory, _fakes.ResourceLock, _fakes.Transactions, _fakes.UnitOfWork, _fakes.Clock);

    private RecordStocktakeCountHandler CreateRecordStocktakeCountHandler() =>
        new(_fakes.Catalog, _fakes.Inventory, _fakes.Transactions, _fakes.UnitOfWork, _fakes.Clock);

    private ReviewStocktakeHandler CreateReviewStocktakeHandler() =>
        new(_fakes.Inventory, _fakes.Transactions, _fakes.UnitOfWork, _fakes.Clock);

    private PostStocktakeHandler CreatePostStocktakeHandler() =>
        new(_fakes.Catalog, _fakes.Inventory, _fakes.CostAllocator, _fakes.Transactions, _fakes.UnitOfWork, _fakes.Clock);

    private CancelStocktakeHandler CreateCancelStocktakeHandler() =>
        new(_fakes.Inventory, _fakes.Transactions, _fakes.UnitOfWork, _fakes.Clock);

    private CreateThakaProjectHandler CreateCreateThakaHandler() =>
        new(
            _fakes.Thaka,
            _fakes.Parties,
            _fakes.Numbers,
            _fakes.Audit,
            _fakes.Clock,
            _fakes.Transactions,
            _fakes.Authorization,
            _fakes.UnitOfWork);

    private IssueThakaMaterialHandler CreateIssueThakaMaterialHandler() =>
        new(
            _fakes.Thaka,
            _fakes.Catalog,
            _fakes.Inventory,
            _fakes.CostAllocator,
            _fakes.OperationLock,
            _fakes.ResourceLock,
            _fakes.Numbers,
            _fakes.Audit,
            _fakes.Clock,
            _fakes.Transactions,
            _fakes.Authorization,
            _fakes.UnitOfWork);

    private RecordThakaPaymentHandler CreateRecordThakaPaymentHandler() =>
        new(
            _fakes.Thaka,
            _cashMovementService,
            _fakes.OperationLock,
            _fakes.ResourceLock,
            _fakes.Numbers,
            _fakes.Audit,
            _fakes.Clock,
            _fakes.Transactions,
            _fakes.Authorization,
            _fakes.UnitOfWork);

    [Fact]
    public async Task CashSession_Open_ManualMovements_And_Close_CalculatesDiscrepancy()
    {
        var openHandler = CreateOpenCashSessionHandler();
        var manualHandler = CreateRecordManualCashMovementHandler();
        var closeHandler = CreateCloseCashSessionHandler();
        var actorId = Guid.CreateVersion7();

        // 1. Open Session with 10,000 float
        var openResult = await openHandler.HandleAsync(
            new OpenCashSessionCommand(10000m, actorId, "Morning opening float"),
            CancellationToken.None);

        Assert.True(openResult.IsSuccess, openResult.Error?.Message);
        var sessionId = openResult.Value;

        Assert.Single(_fakes.Cash.Sessions);
        Assert.Equal(10000m, _fakes.Cash.Sessions.Values.First().OpeningCash);

        // 2. Record Manual Cash Out of 500 (e.g. Office tea/refreshments)
        var outResult = await manualHandler.HandleAsync(
            new RecordManualCashMovementCommand(
                CashMovementDirection.Out,
                500m,
                actorId,
                "Office tea & snacks",
                null),
            CancellationToken.None);

        Assert.True(outResult.IsSuccess, outResult.Error?.Message);

        // 3. Record Manual Cash In of 1,000 (e.g. Owner injection)
        var inResult = await manualHandler.HandleAsync(
            new RecordManualCashMovementCommand(
                CashMovementDirection.In,
                1000m,
                actorId,
                "Petty cash top-up",
                null),
            CancellationToken.None);

        Assert.True(inResult.IsSuccess, inResult.Error?.Message);

        // Expected cash in drawer = 10000 - 500 + 1000 = 10500
        // Cashier counts 10,450 (50 shortage)
        var closeResult = await closeHandler.HandleAsync(
            new CloseCashSessionCommand(
                sessionId,
                CountedCash: 10450m,
                ActorId: actorId,
                Note: "Closing shift 1"),
            CancellationToken.None);

        Assert.True(closeResult.IsSuccess, closeResult.Error?.Message);
        Assert.NotNull(closeResult.Value);
        Assert.Equal(10000m, closeResult.Value!.OpeningCash);
        Assert.Equal(1000m, closeResult.Value.CashIn);
        Assert.Equal(500m, closeResult.Value.CashOut);
        Assert.Equal(10500m, closeResult.Value.ExpectedCash);
        Assert.Equal(10450m, closeResult.Value.CountedCash);
        Assert.Equal(-50m, closeResult.Value.Difference); // 50 shortage

        var closedSession = await _fakes.Cash.GetSessionForUpdateAsync(sessionId, CancellationToken.None);
        Assert.Equal(CashSessionStatus.Closed, closedSession!.Status);
    }

    [Fact]
    public async Task Thaka_ProjectLifecycle_Create_IssueMaterial_RecordPayment_MaintainsAuthoritativeAccounting()
    {
        var customer = new Customer { Id = Guid.CreateVersion7(), Name = "Contractor Aslam", Phone = "03211234567", IsActive = true };
        _fakes.Parties.AddCustomer(customer);

        var unit = new Unit { Id = Guid.CreateVersion7(), Name = "Meter", Symbol = "m" };
        var product = new Product
        {
            Id = Guid.CreateVersion7(),
            Name = "Copper Wire 4mm",
            Sku = "WIRE-4MM",
            BaseUnitId = unit.Id,
            TrackingMode = TrackingMode.Quantity,
            DefaultSalePrice = 150m,
            ReferencePurchaseCost = 100m,
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
            CanSell = true,
            CanUseInThaka = true
        };
        _fakes.Catalog.AddProductUnit(pu);

        _fakes.Inventory.AddStockBalance(new StockBalance
        {
            ProductId = product.Id,
            SellableQty = 500m
        });

        _fakes.Inventory.AddCostState(new ProductCostState
        {
            ProductId = product.Id,
            CostedQty = 500m,
            TotalInventoryCost = 50000m,
            MovingAverageCost = 100m,
            LastPurchaseCost = 100m,
            Version = 1
        });

        var lot = new InventoryLot
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            ReceivedQuantity = 500m,
            OriginalUnitCost = 100m,
            EffectiveUnitCost = 100m,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _fakes.Inventory.AddLot(lot);

        _fakes.Inventory.AddLotBucketBalance(new InventoryLotBucketBalance
        {
            LotId = lot.Id,
            StockBucket = InventoryBucket.Sellable,
            Quantity = 500m
        });

        var createProjectHandler = CreateCreateThakaHandler();
        var issueHandler = CreateIssueThakaMaterialHandler();
        var payHandler = CreateRecordThakaPaymentHandler();
        var actorId = Guid.CreateVersion7();

        // 1. Create Thaka Project
        var projectResult = await createProjectHandler.HandleAsync(
            new CreateThakaProjectCommand(
                customer.Id,
                "Gulberg Commercial Plaza Wiring",
                "Site engineer contact: 03009998877",
                "Project init",
                DateOnly.FromDateTime(DateTime.UtcNow),
                actorId,
                Guid.CreateVersion7()),
            CancellationToken.None);

        Assert.True(projectResult.IsSuccess, projectResult.Error?.Message);
        var projectId = projectResult.Value;

        // 2. Issue 100 meters of wire @ 150/m = 15,000
        var issueResult = await issueHandler.HandleAsync(
            new IssueThakaMaterialCommand(
                Guid.CreateVersion7(),
                projectId,
                actorId,
                "First batch wiring delivery",
                [
                    new IssueThakaMaterialLineInput(product.Id, pu.Id, 100m, 150m, [])
                ]),
            CancellationToken.None);

        Assert.True(issueResult.IsSuccess, issueResult.Error?.Message);
        Assert.Equal(15000m, issueResult.Value!.TotalCharge);

        // Inventory stock decreased from 500 to 400
        Assert.Equal(400m, _fakes.Inventory.Balances[product.Id].SellableQty);

        // Project gross charges are 15,000
        var charges = await _fakes.Thaka.GetGrossMaterialChargesAsync(projectId, CancellationToken.None);
        Assert.Equal(15000m, charges);

        // 3. Open Cash session and record payment of 10,000
        var session = new CashSession
        {
            Id = Guid.CreateVersion7(),
            BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow),
            OpenedBy = actorId,
            OpeningCash = 5000m,
            Status = CashSessionStatus.Open,
            OpenedAt = DateTimeOffset.UtcNow
        };
        _fakes.Cash.AddSession(session);

        var payResult = await payHandler.HandleAsync(
            new RecordThakaPaymentCommand(
                Guid.CreateVersion7(),
                projectId,
                10000m,
                ThakaPaymentMethod.Cash,
                null,
                "Partial project milestone cash payment",
                actorId),
            CancellationToken.None);

        Assert.True(payResult.IsSuccess, payResult.Error?.Message);

        var collected = await _fakes.Thaka.GetPaymentsCollectedAsync(projectId, CancellationToken.None);
        Assert.Equal(10000m, collected);

        // Outstanding project balance = 15,000 - 10,000 = 5,000
        var outstanding = charges - collected;
        Assert.Equal(5000m, outstanding);
    }

    [Fact]
    public async Task Stocktake_Lifecycle_Create_And_Cancel_Succeeds()
    {
        var createHandler = CreateCreateStocktakeHandler();
        var cancelHandler = CreateCancelStocktakeHandler();
        var actorId = Guid.CreateVersion7();

        var createResult = await createHandler.HandleAsync(
            new CreateStocktakeCommand(StocktakeScope.FullShop, null, actorId, "Annual stocktake"),
            CancellationToken.None);

        Assert.True(createResult.IsSuccess, createResult.Error?.Message);
        var stocktakeId = createResult.Value;

        var cancelResult = await cancelHandler.HandleAsync(
            new CancelStocktakeCommand(stocktakeId),
            CancellationToken.None);

        Assert.True(cancelResult.IsSuccess, cancelResult.Error?.Message);
        var stocktake = await _fakes.Inventory.GetStocktakeForUpdateAsync(stocktakeId, CancellationToken.None);
        Assert.NotNull(stocktake);
        Assert.Equal(StocktakeStatus.Cancelled, stocktake.Status);
    }

    [Fact]
    public async Task Stocktake_CannotCreate_WhenAnotherStocktakeIsOpen()
    {
        var createHandler = CreateCreateStocktakeHandler();
        var actorId = Guid.CreateVersion7();

        // Seed an active counting stocktake
        var activeStocktake = new Stocktake
        {
            Id = Guid.CreateVersion7(),
            Scope = StocktakeScope.FullShop,
            Status = StocktakeStatus.Counting,
            CreatedBy = actorId,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _fakes.Inventory.AddStocktake(activeStocktake);

        var result = await createHandler.HandleAsync(
            new CreateStocktakeCommand(StocktakeScope.FullShop, null, actorId, "Conflicting stocktake"),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("inventory.stocktake_already_open", result.Error?.Code);
    }

    [Fact]
    public async Task Stocktake_CategoryScope_RequiresCategoryId()
    {
        var createHandler = CreateCreateStocktakeHandler();
        var actorId = Guid.CreateVersion7();

        var result = await createHandler.HandleAsync(
            new CreateStocktakeCommand(StocktakeScope.Category, null, actorId, "Missing category id"),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("inventory.stocktake_category_required", result.Error?.Code);
    }
}
