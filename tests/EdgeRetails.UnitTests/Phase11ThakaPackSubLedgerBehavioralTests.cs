using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Features.Thaka;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.Thaka;
using Xunit;

namespace EdgeRetails.UnitTests;

public sealed class Phase11ThakaPackSubLedgerBehavioralTests
{
    private readonly Phase2TestDoubles _fakes = new();

    private CompleteSaleHandler CreateSaleHandler() =>
        new(
            _fakes.Sales,
            _fakes.Quotations,
            _fakes.Parties,
            _fakes.Catalog,
            _fakes.Inventory,
            _fakes.CostAllocator,
            _fakes.Cash,
            _fakes.OperationLock,
            _fakes.ResourceLock,
            _fakes.Audit,
            _fakes.ReceiptSnapshots,
            _fakes.Numbers,
            _fakes.Clock,
            _fakes.Transactions,
            _fakes.Authorization,
            _fakes.UnitOfWork,
            outcomeLedger: _fakes.OutcomeLedger,
            packAllocations: _fakes.PackAllocations);

    private CreateThakaProjectHandler CreateProjectHandler() =>
        new(
            _fakes.Thaka,
            _fakes.Parties,
            _fakes.Numbers,
            _fakes.Audit,
            _fakes.Clock,
            _fakes.Transactions,
            _fakes.Authorization,
            _fakes.UnitOfWork,
            _fakes.OperationLock,
            _fakes.OutcomeLedger);

    private IssueThakaMaterialHandler CreateIssueHandler() =>
        new(
            _fakes.Thaka,
            _fakes.Parties,
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
            _fakes.UnitOfWork,
            _fakes.OutcomeLedger,
            _fakes.PackAllocations);

    private ReverseThakaMaterialHandler CreateReverseHandler() =>
        new(
            _fakes.Thaka,
            _fakes.Inventory,
            _fakes.CostAllocator,
            _fakes.OperationLock,
            _fakes.ResourceLock,
            _fakes.Numbers,
            _fakes.Audit,
            _fakes.Clock,
            _fakes.Transactions,
            _fakes.Authorization,
            _fakes.UnitOfWork,
            _fakes.OutcomeLedger);

    private SettleThakaHandler CreateSettleHandler() =>
        new(
            _fakes.Thaka,
            _fakes.CashMovements,
            _fakes.OperationLock,
            _fakes.ResourceLock,
            _fakes.Numbers,
            _fakes.Audit,
            _fakes.Clock,
            _fakes.Transactions,
            _fakes.Authorization,
            _fakes.UnitOfWork,
            _fakes.OutcomeLedger);

    private ReopenThakaHandler CreateReopenHandler() =>
        new(
            _fakes.Thaka,
            _fakes.ResourceLock,
            _fakes.Audit,
            _fakes.Clock,
            _fakes.Transactions,
            _fakes.Authorization,
            _fakes.UnitOfWork);

    private RecordThakaPaymentHandler CreateRecordPaymentHandler() =>
        new(
            _fakes.Thaka,
            _fakes.CashMovements,
            _fakes.OperationLock,
            _fakes.ResourceLock,
            _fakes.Numbers,
            _fakes.Audit,
            _fakes.Clock,
            _fakes.Transactions,
            _fakes.Authorization,
            _fakes.UnitOfWork,
            _fakes.OutcomeLedger);

    private ReverseThakaPaymentHandler CreateReversePaymentHandler() =>
        new(
            _fakes.Thaka,
            _fakes.CashMovements,
            _fakes.OperationLock,
            _fakes.ResourceLock,
            _fakes.Numbers,
            _fakes.Audit,
            _fakes.Clock,
            _fakes.Transactions,
            _fakes.Authorization,
            _fakes.UnitOfWork,
            _fakes.OutcomeLedger);

    private RefundThakaCreditHandler CreateRefundCreditHandler() =>
        new(
            _fakes.Thaka,
            _fakes.CashMovements,
            _fakes.OperationLock,
            _fakes.ResourceLock,
            _fakes.Numbers,
            _fakes.Audit,
            _fakes.Clock,
            _fakes.Transactions,
            _fakes.Authorization,
            _fakes.UnitOfWork,
            _fakes.OutcomeLedger);

    private Customer SeedCustomer()
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Name = "Al-Rahman Electrical Contractors",
            Phone = "03001234567",
            IsActive = true
        };
        _fakes.Parties.Customers[customer.Id] = customer;
        return customer;
    }

    private ThakaProject SeedProject(Customer customer)
    {
        var project = new ThakaProject
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            ProjectName = "Plaza Wiring Phase 1",
            Status = ThakaProjectStatus.Active,
            StartedOn = DateOnly.FromDateTime(DateTime.UtcNow),
            CreatedBy = Guid.NewGuid(),
            CreatedAt = _fakes.Clock.UtcNow
        };
        _fakes.Thaka.Projects[project.Id] = project;
        return project;
    }

    [Fact]
    public async Task P11_IMP_079_ProductUnit_BaseConversion_And_LineCharge_CalculatesAccurately()
    {
        var customer = SeedCustomer();
        var project = SeedProject(customer);
        var actorId = Guid.NewGuid();

        var productId = Guid.NewGuid();
        var product = new Product
        {
            Id = productId,
            Name = "Copper Wire 2.5mm Single Core",
            Sku = "WIRE-2.5-SC",
            TrackingMode = TrackingMode.Quantity,
            DefaultSalePrice = 15m,
            ReferencePurchaseCost = 10m,
            IsActive = true
        };
        _fakes.Catalog.AddProduct(product);

        var coilUnitId = Guid.NewGuid();
        var coilUnit = new ProductUnit
        {
            Id = coilUnitId,
            ProductId = productId,
            UnitId = Guid.NewGuid(),
            FactorToBaseUnit = 90m,
            CanUseInThaka = true,
            IsActive = true
        };
        _fakes.Catalog.AddProductUnit(coilUnit);

        // Seed stock: 300 meters sellable
        _fakes.Inventory.Balances[productId] = new StockBalance { ProductId = productId, SellableQty = 300m };
        _fakes.Inventory.CostStates[productId] = new ProductCostState
        {
            ProductId = productId,
            CostedQty = 300m,
            TotalInventoryCost = 3000m,
            MovingAverageCost = 10m
        };

        var handler = CreateIssueHandler();

        // Stale price check fails closed
        var staleCmd = new IssueThakaMaterialCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            ActorId: actorId,
            Note: "Wiring materials",
            Lines:
            [
                new IssueThakaMaterialLineInput(
                    productId,
                    coilUnitId,
                    EnteredQuantity: 2m, // 2 coils = 180m
                    ExpectedUnitCharge: 12m, // wrong/stale price (current is 15m)
                    InventoryUnitIds: [])
            ]);

        var staleResult = await handler.HandleAsync(staleCmd, CancellationToken.None);
        Assert.False(staleResult.IsSuccess);
        Assert.Equal("thaka.price_stale", staleResult.Error?.Code);

        // Correct price succeeds with converted base quantity (2 * 90m = 180m) and line charge (180 * 15 = 2700)
        var validCmd = new IssueThakaMaterialCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            ActorId: actorId,
            Note: "Wiring materials",
            Lines:
            [
                new IssueThakaMaterialLineInput(
                    productId,
                    coilUnitId,
                    EnteredQuantity: 2m,
                    ExpectedUnitCharge: 15m,
                    InventoryUnitIds: [])
            ]);

        var validResult = await handler.HandleAsync(validCmd, CancellationToken.None);
        Assert.True(validResult.IsSuccess, validResult.Error?.Message);
        Assert.Equal(2700m, validResult.Value!.TotalCharge); // 180m * 15m
        Assert.Equal(1800m, validResult.Value.TotalCost); // 180m * 10m MWA cost

        // Stock decreased by 180m (300 - 180 = 120m remaining)
        Assert.Equal(120m, _fakes.Inventory.Balances[productId].SellableQty);
    }

    [Fact]
    public async Task P11_IMP_080_CommandWide_ExactIdentity_Uniqueness_RejectsDuplicatesAcrossLines()
    {
        var customer = SeedCustomer();
        var project = SeedProject(customer);
        var actorId = Guid.NewGuid();

        var productId = Guid.NewGuid();
        var product = new Product
        {
            Id = productId,
            Name = "Distribution Circuit Breaker 63A",
            Sku = "MCB-63A",
            TrackingMode = TrackingMode.Serialized,
            DefaultSalePrice = 500m,
            ReferencePurchaseCost = 350m,
            IsActive = true
        };
        _fakes.Catalog.AddProduct(product);

        var baseUnit = new ProductUnit
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            UnitId = Guid.NewGuid(),
            FactorToBaseUnit = 1m,
            CanUseInThaka = true,
            IsActive = true
        };
        _fakes.Catalog.AddProductUnit(baseUnit);

        var unitId = Guid.NewGuid();
        var duplicateUnit = new InventoryUnit
        {
            Id = unitId,
            ProductId = productId,
            TrackingCode = "MCB-001",
            SerialNumber = "SN-MCB-001",
            Status = InventoryUnitStatus.InStock,
            AcquisitionCost = 350m,
            InventoryLotId = Guid.NewGuid()
        };
        _fakes.Inventory.Units.Add(duplicateUnit);
        _fakes.Inventory.Balances[productId] = new StockBalance { ProductId = productId, SellableQty = 10m };
        _fakes.Inventory.CostStates[productId] = new ProductCostState { ProductId = productId, CostedQty = 10m, TotalInventoryCost = 3500m, MovingAverageCost = 350m };

        var handler = CreateIssueHandler();

        // Attempting to duplicate unitId across lines
        var duplicateCmd = new IssueThakaMaterialCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            ActorId: actorId,
            Note: "Duplicate test",
            Lines:
            [
                new IssueThakaMaterialLineInput(
                    productId,
                    baseUnit.Id,
                    EnteredQuantity: 1m,
                    ExpectedUnitCharge: 500m,
                    InventoryUnitIds: [unitId]),
                new IssueThakaMaterialLineInput(
                    productId,
                    baseUnit.Id,
                    EnteredQuantity: 1m,
                    ExpectedUnitCharge: 500m,
                    InventoryUnitIds: [unitId])
            ]);

        var result = await handler.HandleAsync(duplicateCmd, CancellationToken.None);
        Assert.False(result.IsSuccess);
        // Duplicate line or duplicate unit id fails closed
        Assert.True(result.Error?.Code is "thaka.issue_duplicate_line" or "thaka.serial_selection_invalid");
    }

    [Fact]
    public async Task P11_IMP_081_Partial_CrossPack_Coil_MaterialIssue_IntegratesPackSubLedger()
    {
        var customer = SeedCustomer();
        var project = SeedProject(customer);
        var actorId = Guid.NewGuid();

        var productId = Guid.NewGuid();
        var product = new Product
        {
            Id = productId,
            Name = "Submersible Flexible Cable 4mm",
            Sku = "CBL-4MM-SUB",
            TrackingMode = TrackingMode.Container,
            DefaultSalePrice = 100m,
            ReferencePurchaseCost = 70m,
            IsActive = true
        };
        _fakes.Catalog.AddProduct(product);

        var meterUnit = new ProductUnit
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            UnitId = Guid.NewGuid(),
            FactorToBaseUnit = 1m,
            CanUseInThaka = true,
            IsActive = true
        };
        _fakes.Catalog.AddProductUnit(meterUnit);

        var lotId = Guid.NewGuid();
        _fakes.Inventory.Lots.Add(new InventoryLot
        {
            Id = lotId,
            ProductId = productId,
            ReceivedQuantity = 140m,
            OriginalUnitCost = 70m,
            EffectiveUnitCost = 70m,
            CreatedAt = _fakes.Clock.UtcNow
        });
        _fakes.Inventory.LotBucketBalances.Add(new InventoryLotBucketBalance
        {
            LotId = lotId,
            StockBucket = InventoryBucket.Sellable,
            Quantity = 140m
        });

        var coil1 = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            TrackingCode = "COIL-01",
            Status = InventoryUnitStatus.InStock,
            InitialBaseQuantity = 100m,
            RemainingBaseQuantity = 40m, // Open coil with 40m remaining
            PackState = PackState.Open,
            AcquisitionCost = 7000m, // $70/m
            InventoryLotId = lotId,
            CreatedAt = _fakes.Clock.UtcNow
        };
        var coil2 = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            TrackingCode = "COIL-02",
            Status = InventoryUnitStatus.InStock,
            InitialBaseQuantity = 100m,
            RemainingBaseQuantity = 100m, // Sealed coil with 100m remaining
            PackState = PackState.Sealed,
            AcquisitionCost = 7000m, // $70/m
            InventoryLotId = lotId,
            CreatedAt = _fakes.Clock.UtcNow.AddMinutes(1)
        };
        _fakes.Inventory.Units.Add(coil1);
        _fakes.Inventory.Units.Add(coil2);

        _fakes.Inventory.Balances[productId] = new StockBalance { ProductId = productId, SellableQty = 140m };
        _fakes.Inventory.CostStates[productId] = new ProductCostState
        {
            ProductId = productId,
            CostedQty = 140m,
            TotalInventoryCost = 9800m,
            MovingAverageCost = 70m
        };

        var handler = CreateIssueHandler();

        // Test 1: Request 60m continuous cable without AllowSplitCuts.
        // Candidate 1 (40m) is insufficient. Candidate 2 (100m) is sufficient continuous segment!
        var continuousCmd = new IssueThakaMaterialCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            ActorId: actorId,
            Note: "Pump motor connection continuous lead",
            Lines:
            [
                new IssueThakaMaterialLineInput(
                    productId,
                    meterUnit.Id,
                    EnteredQuantity: 60m,
                    ExpectedUnitCharge: 100m,
                    InventoryUnitIds: [],
                    AllowSplitCuts: false)
            ]);

        var contResult = await handler.HandleAsync(continuousCmd, CancellationToken.None);
        Assert.True(contResult.IsSuccess, contResult.Error?.Message);
        Assert.Equal(6000m, contResult.Value!.TotalCharge);

        // Coil2 had 100m, now 40m remaining, PackState = Open
        Assert.Equal(40m, coil2.RemainingBaseQuantity);
        Assert.Equal(PackState.Open, coil2.PackState);

        // Coil1 (40m) was not touched because continuous segment was fulfilled by coil2
        Assert.Equal(40m, coil1.RemainingBaseQuantity);

        // Test 2: Request 70m with AllowSplitCuts: false when remaining coils are 40m and 40m -> fails
        var failCmd = new IssueThakaMaterialCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            ActorId: actorId,
            Note: "Too long continuous request",
            Lines:
            [
                new IssueThakaMaterialLineInput(
                    productId,
                    meterUnit.Id,
                    EnteredQuantity: 70m,
                    ExpectedUnitCharge: 100m,
                    InventoryUnitIds: [],
                    AllowSplitCuts: false)
            ]);

        var failResult = await handler.HandleAsync(failCmd, CancellationToken.None);
        Assert.False(failResult.IsSuccess);
        Assert.Equal("sales.cable_split_required", failResult.Error?.Code);

        // Test 3: Request 70m with AllowSplitCuts: true -> succeeds across both coils (40m from coil1, 30m from coil2)
        var splitCmd = new IssueThakaMaterialCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            ActorId: actorId,
            Note: "Split cuts accepted",
            Lines:
            [
                new IssueThakaMaterialLineInput(
                    productId,
                    meterUnit.Id,
                    EnteredQuantity: 70m,
                    ExpectedUnitCharge: 100m,
                    InventoryUnitIds: [],
                    AllowSplitCuts: true)
            ]);

        var splitResult = await handler.HandleAsync(splitCmd, CancellationToken.None);
        Assert.True(splitResult.IsSuccess, splitResult.Error?.Message);

        // Invariant: PackAllocations generated with ThakaIssue operation type
        var packAllocs = _fakes.Inventory.PackAllocations
            .Where(a => a.OperationType == PackAllocationOperationTypes.ThakaIssue)
            .ToList();
        Assert.NotEmpty(packAllocs);
    }

    [Fact]
    public async Task P11_IMP_082_ThakaReturn_Correction_CustodyLineage_RestoresStockAndCostWithoutDrift()
    {
        var customer = SeedCustomer();
        var project = SeedProject(customer);
        var actorId = Guid.NewGuid();

        var productId = Guid.NewGuid();
        var product = new Product
        {
            Id = productId,
            Name = "Heavy Duty Circuit Breaker 100A",
            Sku = "BRK-100A",
            TrackingMode = TrackingMode.Serialized,
            DefaultSalePrice = 1200m,
            ReferencePurchaseCost = 800m,
            IsActive = true
        };
        _fakes.Catalog.AddProduct(product);

        var pieceUnit = new ProductUnit
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            UnitId = Guid.NewGuid(),
            FactorToBaseUnit = 1m,
            CanUseInThaka = true,
            IsActive = true
        };
        _fakes.Catalog.AddProductUnit(pieceUnit);

        var lotId = Guid.NewGuid();
        _fakes.Inventory.Lots.Add(new InventoryLot
        {
            Id = lotId,
            ProductId = productId,
            ReceivedQuantity = 1m,
            OriginalUnitCost = 800m,
            EffectiveUnitCost = 800m,
            CreatedAt = _fakes.Clock.UtcNow
        });
        _fakes.Inventory.LotBucketBalances.Add(new InventoryLotBucketBalance
        {
            LotId = lotId,
            StockBucket = InventoryBucket.Sellable,
            Quantity = 1m
        });

        var unitId = Guid.NewGuid();
        var unit = new InventoryUnit
        {
            Id = unitId,
            ProductId = productId,
            TrackingCode = "BRK-SN-001",
            SerialNumber = "SN-BRK-001",
            Status = InventoryUnitStatus.InStock,
            AcquisitionCost = 800m,
            InventoryLotId = lotId,
            SourcePurchaseItemId = Guid.NewGuid()
        };
        _fakes.Inventory.Units.Add(unit);
        _fakes.Inventory.Balances[productId] = new StockBalance { ProductId = productId, SellableQty = 1m };
        _fakes.Inventory.CostStates[productId] = new ProductCostState
        {
            ProductId = productId,
            CostedQty = 1m,
            TotalInventoryCost = 800m,
            MovingAverageCost = 800m
        };

        var issueHandler = CreateIssueHandler();
        var issueCmd = new IssueThakaMaterialCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            ActorId: actorId,
            Note: "Site distribution board unit",
            Lines:
            [
                new IssueThakaMaterialLineInput(
                    productId,
                    pieceUnit.Id,
                    EnteredQuantity: 1m,
                    ExpectedUnitCharge: 1200m,
                    InventoryUnitIds: [unitId])
            ]);

        var issueResult = await issueHandler.HandleAsync(issueCmd, CancellationToken.None);
        Assert.True(issueResult.IsSuccess, issueResult.Error?.Message);
        Assert.Equal(0m, _fakes.Inventory.Balances[productId].SellableQty);
        Assert.Equal(InventoryUnitStatus.IssuedThaka, unit.Status);

        // Perform Reversal / Return
        var reverseHandler = CreateReverseHandler();
        var reverseCmd = new ReverseThakaMaterialCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            MaterialIssueId: issueResult.Value!.MaterialIssueId,
            Reason: "Project cancelled breaker specs changed",
            ActorId: actorId);

        var reverseResult = await reverseHandler.HandleAsync(reverseCmd, CancellationToken.None);
        Assert.True(reverseResult.IsSuccess, reverseResult.Error?.Message);
        Assert.Equal(1200m, reverseResult.Value!.ReversedCharge);
        Assert.Equal(800m, reverseResult.Value.RestoredCost);

        // Verification 1: Sellable quantity restored to 1m
        Assert.Equal(1m, _fakes.Inventory.Balances[productId].SellableQty);

        // Verification 2: Unit returned to InStock status
        Assert.Equal(InventoryUnitStatus.InStock, unit.Status);

        // Verification 3: Reversal is idempotent against repeated call
        var repeatResult = await reverseHandler.HandleAsync(reverseCmd, CancellationToken.None);
        Assert.True(repeatResult.IsSuccess);
        Assert.True(repeatResult.Value!.WasExisting);

        // Verification 4: Distinct operation reversal on already reversed issue fails closed
        var doubleCmd = new ReverseThakaMaterialCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            MaterialIssueId: issueResult.Value.MaterialIssueId,
            Reason: "Attempt second reversal",
            ActorId: actorId);

        var doubleResult = await reverseHandler.HandleAsync(doubleCmd, CancellationToken.None);
        Assert.False(doubleResult.IsSuccess);
        Assert.Equal("thaka.issue_already_reversed", doubleResult.Error?.Code);
    }

    [Fact]
    public async Task P11_IMP_083_Thaka_Sale_Stocktake_IssueRaces_And_Replay_PreservesIntegrity()
    {
        var customer = SeedCustomer();
        var project = SeedProject(customer);
        var actorId = Guid.NewGuid();

        var productId = Guid.NewGuid();
        var product = new Product
        {
            Id = productId,
            Name = "Industrial Relay 24V",
            Sku = "RELAY-24V",
            TrackingMode = TrackingMode.Quantity,
            DefaultSalePrice = 250m,
            ReferencePurchaseCost = 150m,
            IsActive = true
        };
        _fakes.Catalog.AddProduct(product);

        var unit = new ProductUnit
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            UnitId = Guid.NewGuid(),
            FactorToBaseUnit = 1m,
            CanUseInThaka = true,
            CanSell = true,
            IsDefaultSaleUnit = true,
            IsActive = true
        };
        _fakes.Catalog.AddProductUnit(unit);

        _fakes.Inventory.Balances[productId] = new StockBalance { ProductId = productId, SellableQty = 10m };
        _fakes.Inventory.CostStates[productId] = new ProductCostState
        {
            ProductId = productId,
            CostedQty = 10m,
            TotalInventoryCost = 1500m,
            MovingAverageCost = 150m
        };

        var lot = new InventoryLot
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            SourceMovementId = Guid.NewGuid(),
            ReceivedQuantity = 10m,
            OriginalUnitCost = 150m,
            EffectiveUnitCost = 150m,
            CreatedAt = _fakes.Clock.UtcNow
        };
        _fakes.Inventory.Lots.Add(lot);
        _fakes.Inventory.LotBucketBalances.Add(new InventoryLotBucketBalance
        {
            Id = Guid.NewGuid(),
            LotId = lot.Id,
            StockBucket = InventoryBucket.Sellable,
            Quantity = 10m
        });

        var handler = CreateIssueHandler();

        // 1. Stocktake in progress blocks issue
        _fakes.Inventory.BlockedStocktakeProducts.Add(productId);
        var blockedCmd = new IssueThakaMaterialCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            ActorId: actorId,
            Note: "During stocktake",
            Lines:
            [
                new IssueThakaMaterialLineInput(
                    productId,
                    unit.Id,
                    EnteredQuantity: 2m,
                    ExpectedUnitCharge: 250m,
                    InventoryUnitIds: [])
            ]);

        var blockedResult = await handler.HandleAsync(blockedCmd, CancellationToken.None);
        Assert.False(blockedResult.IsSuccess);
        Assert.Equal("inventory.stocktake_blocks_product", blockedResult.Error?.Code);

        // Unblock stocktake
        _fakes.Inventory.BlockedStocktakeProducts.Clear();

        // 2. Insufficient stock check fails closed
        var overCmd = new IssueThakaMaterialCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            ActorId: actorId,
            Note: "Over sellable quantity",
            Lines:
            [
                new IssueThakaMaterialLineInput(
                    productId,
                    unit.Id,
                    EnteredQuantity: 15m, // Only 10m in stock
                    ExpectedUnitCharge: 250m,
                    InventoryUnitIds: [])
            ]);

        var overResult = await handler.HandleAsync(overCmd, CancellationToken.None);
        Assert.False(overResult.IsSuccess);
        Assert.Equal("thaka.insufficient_stock", overResult.Error?.Code);

        // 3. Successful issue + Idempotent replay
        var opId = Guid.NewGuid();
        var issueCmd = new IssueThakaMaterialCommand(
            ClientOperationId: opId,
            ProjectId: project.Id,
            ActorId: actorId,
            Note: "Initial material issue",
            Lines:
            [
                new IssueThakaMaterialLineInput(
                    productId,
                    unit.Id,
                    EnteredQuantity: 4m,
                    ExpectedUnitCharge: 250m,
                    InventoryUnitIds: [])
            ]);

        var firstResult = await handler.HandleAsync(issueCmd, CancellationToken.None);
        Assert.True(firstResult.IsSuccess, firstResult.Error?.Message);
        Assert.False(firstResult.Value!.WasExisting);
        Assert.Equal(6m, _fakes.Inventory.Balances[productId].SellableQty);

        // Replay same ClientOperationId
        var replayResult = await handler.HandleAsync(issueCmd, CancellationToken.None);
        Assert.True(replayResult.IsSuccess);
        Assert.True(replayResult.Value!.WasExisting);
        Assert.Equal(firstResult.Value.MaterialIssueId, replayResult.Value.MaterialIssueId);
        // Balance remains unchanged at 6m (no double debit)
        Assert.Equal(6m, _fakes.Inventory.Balances[productId].SellableQty);

        // 4. Competing Sale consumes remaining 6m stock
        var cashierId = Guid.NewGuid();
        var session = new CashSession
        {
            Id = Guid.NewGuid(),
            OpenedBy = cashierId,
            OpeningCash = 1000m,
            Status = CashSessionStatus.Open,
            OpenedAt = DateTimeOffset.UtcNow,
            BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow)
        };
        _fakes.Cash.AddSession(session);

        var saleHandler = CreateSaleHandler();
        var saleCmd = new CompleteSaleCommand(
            ClientOperationId: Guid.NewGuid(),
            CustomerId: customer.Id,
            CashierUserId: cashierId,
            SessionId: session.Id,
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Cash,
            AmountTendered: 1500m,
            PaymentReference: null,
            QuotationId: null,
            Lines: [
                new CompleteSaleLineInput(
                    productId,
                    unit.Id,
                    EnteredQuantity: 6m,
                    ExpectedUnitPrice: 250m,
                    InventoryUnitIds: [])
            ]);

        var saleResult = await saleHandler.HandleAsync(saleCmd, CancellationToken.None);
        Assert.True(saleResult.IsSuccess, saleResult.Error?.Message);
        Assert.Equal(0m, _fakes.Inventory.Balances[productId].SellableQty);

        // 5. Subsequent Thaka issue fails due to stock exhausted by competing Sale
        var exhaustedCmd = new IssueThakaMaterialCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            ActorId: actorId,
            Note: "Attempt issue after sale consumed stock",
            Lines: [
                new IssueThakaMaterialLineInput(
                    productId,
                    unit.Id,
                    EnteredQuantity: 1m,
                    ExpectedUnitCharge: 250m,
                    InventoryUnitIds: [])
            ]);
        var exhaustedResult = await handler.HandleAsync(exhaustedCmd, CancellationToken.None);
        Assert.False(exhaustedResult.IsSuccess);
        Assert.Equal("thaka.insufficient_stock", exhaustedResult.Error?.Code);

        // 6. Active Stocktake blocks competing Sale fail-closed
        _fakes.Inventory.BlockedStocktakeProducts.Add(productId);
        var blockedSaleCmd = new CompleteSaleCommand(
            ClientOperationId: Guid.NewGuid(),
            CustomerId: customer.Id,
            CashierUserId: cashierId,
            SessionId: session.Id,
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Cash,
            AmountTendered: 250m,
            PaymentReference: null,
            QuotationId: null,
            Lines: [
                new CompleteSaleLineInput(
                    productId,
                    unit.Id,
                    EnteredQuantity: 1m,
                    ExpectedUnitPrice: 250m,
                    InventoryUnitIds: [])
            ]);
        var blockedSaleResult = await saleHandler.HandleAsync(blockedSaleCmd, CancellationToken.None);
        Assert.False(blockedSaleResult.IsSuccess);
        Assert.Equal("inventory.stocktake_blocks_product", blockedSaleResult.Error?.Code);
        _fakes.Inventory.BlockedStocktakeProducts.Clear();
    }

    [Fact]
    public async Task THAKA_AUD_01_ZeroBalanceFinalSettlement_SucceedsWithoutPhantomPayment()
    {
        var customer = SeedCustomer();
        var project = SeedProject(customer);
        var actorId = Guid.NewGuid();

        // Project has 0 gross material charges and 0 payments (remaining balance is exactly 0m)
        var settleHandler = CreateSettleHandler();
        var settleCmd = new SettleThakaCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            SettlementDiscount: 0m,
            FinalPaymentAmount: 0m,
            PaymentMethod: ThakaPaymentMethod.Cash,
            PaymentReference: null,
            ActorId: actorId);

        var result = await settleHandler.HandleAsync(settleCmd, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(ThakaProjectStatus.Settled, project.Status);
        Assert.Equal(0m, result.Value!.FinalPaymentAmount);
        Assert.Equal(0m, result.Value.SettlementDiscount);
        // Verify no phantom payment was added to the repository
        Assert.DoesNotContain(_fakes.Thaka.Payments, p => p.ProjectId == project.Id);
    }

    [Fact]
    public async Task THAKA_AUD_02_SettledProjectReopen_RestoresActiveStatusAndPermitsOperations()
    {
        var customer = SeedCustomer();
        var project = SeedProject(customer);
        var actorId = Guid.NewGuid();

        // 1. Settle project
        var settleHandler = CreateSettleHandler();
        var settleCmd = new SettleThakaCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            SettlementDiscount: 0m,
            FinalPaymentAmount: 0m,
            PaymentMethod: ThakaPaymentMethod.Cash,
            PaymentReference: null,
            ActorId: actorId);

        var settleResult = await settleHandler.HandleAsync(settleCmd, CancellationToken.None);
        Assert.True(settleResult.IsSuccess, settleResult.Error?.Message);
        Assert.Equal(ThakaProjectStatus.Settled, project.Status);

        // 2. Reopen project
        var reopenHandler = CreateReopenHandler();
        var reopenCmd = new ReopenThakaCommand(
            ProjectId: project.Id,
            Reason: "Supplemental work order requested by client",
            ActorId: actorId,
            CorrelationId: Guid.NewGuid());

        var reopenResult = await reopenHandler.HandleAsync(reopenCmd, CancellationToken.None);
        Assert.True(reopenResult.IsSuccess, reopenResult.Error?.Message);
        Assert.Equal(ThakaProjectStatus.Active, project.Status);
    }

    private (Guid ProductId, Guid ProductUnitId, Guid UnitId) SeedSellableUnit(decimal salePrice = 1000m, decimal cost = 600m)
    {
        var productId = Guid.NewGuid();
        var product = new Product
        {
            Id = productId,
            Name = "General Electrical Wire",
            Sku = $"GEN-{Guid.NewGuid():N}"[..12],
            TrackingMode = TrackingMode.Quantity,
            DefaultSalePrice = salePrice,
            ReferencePurchaseCost = cost,
            IsActive = true
        };
        _fakes.Catalog.AddProduct(product);

        var unit = new ProductUnit
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            UnitId = Guid.NewGuid(),
            FactorToBaseUnit = 1m,
            CanUseInThaka = true,
            CanSell = true,
            IsDefaultSaleUnit = true,
            IsActive = true
        };
        _fakes.Catalog.AddProductUnit(unit);

        _fakes.Inventory.Balances[productId] = new StockBalance { ProductId = productId, SellableQty = 100m };
        _fakes.Inventory.CostStates[productId] = new ProductCostState
        {
            ProductId = productId,
            CostedQty = 100m,
            TotalInventoryCost = cost * 100m,
            MovingAverageCost = cost
        };

        var lot = new InventoryLot
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            SourceMovementId = Guid.NewGuid(),
            ReceivedQuantity = 100m,
            OriginalUnitCost = cost,
            EffectiveUnitCost = cost,
            CreatedAt = _fakes.Clock.UtcNow
        };
        _fakes.Inventory.Lots.Add(lot);
        _fakes.Inventory.LotBucketBalances.Add(new InventoryLotBucketBalance
        {
            Id = Guid.NewGuid(),
            LotId = lot.Id,
            StockBucket = InventoryBucket.Sellable,
            Quantity = 100m
        });

        return (productId, unit.Id, Guid.Empty);
    }

    [Fact]
    public async Task THAKA_AUD_03_PaymentReversal_ReversesPaymentAndRestoresProjectBalance()
    {
        var customer = SeedCustomer();
        var project = SeedProject(customer);
        var actorId = Guid.NewGuid();

        // 1. Issue material worth 2,000 Rs
        var (productId, productUnitId, _) = SeedSellableUnit(salePrice: 1000m, cost: 600m);
        var issueHandler = CreateIssueHandler();
        var issueCmd = new IssueThakaMaterialCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            ActorId: actorId,
            Note: "Initial materials for wiring",
            Lines:
            [
                new IssueThakaMaterialLineInput(
                    productId,
                    productUnitId,
                    EnteredQuantity: 2m,
                    ExpectedUnitCharge: 1000m,
                    InventoryUnitIds: [])
            ]);

        var issueResult = await issueHandler.HandleAsync(issueCmd, CancellationToken.None);
        Assert.True(issueResult.IsSuccess, issueResult.Error?.Message);

        // 2. Establish an open cash drawer session
        var session = new CashSession
        {
            Id = Guid.NewGuid(),
            OpenedBy = actorId,
            OpeningCash = 5000m,
            Status = CashSessionStatus.Open,
            OpenedAt = DateTimeOffset.UtcNow,
            BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow)
        };
        _fakes.Cash.AddSession(session);

        // 3. Record payment of 1,000 Rs (balance goes from 2000 to 1000)
        var paymentHandler = CreateRecordPaymentHandler();
        var paymentOpId = Guid.NewGuid();
        var paymentCmd = new RecordThakaPaymentCommand(
            ClientOperationId: paymentOpId,
            ProjectId: project.Id,
            Amount: 1000m,
            PaymentMethod: ThakaPaymentMethod.Cash,
            Reference: "ADV-001",
            Note: "Interim progress payment",
            ActorId: actorId);

        var paymentResult = await paymentHandler.HandleAsync(paymentCmd, CancellationToken.None);
        Assert.True(paymentResult.IsSuccess, paymentResult.Error?.Message);
        var paymentId = paymentResult.Value!.PaymentId;

        // 4. Reverse payment
        var reversePaymentHandler = CreateReversePaymentHandler();
        var reverseCmd = new ReverseThakaPaymentCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            PaymentId: paymentId,
            Reason: "Cheque bounce / customer requested cash refund",
            ActorId: actorId);

        var reverseResult = await reversePaymentHandler.HandleAsync(reverseCmd, CancellationToken.None);
        Assert.True(reverseResult.IsSuccess, reverseResult.Error?.Message);
        Assert.Equal(1000m, reverseResult.Value!.Amount);
        Assert.False(reverseResult.Value.WasExisting);

        // Verify reversal record exists in repository
        Assert.Single(_fakes.Thaka.PaymentReversals, r => r.PaymentId == paymentId);

        // Verify compensating cash outflow was recorded
        var reversalMovement = _fakes.Cash.Movements
            .FirstOrDefault(m => m.MovementType == CashMovementType.ThakaPaymentReversalCashOut);
        Assert.NotNull(reversalMovement);
        Assert.Equal(1000m, reversalMovement.Amount);
    }

    [Fact]
    public async Task THAKA_AUD_04_NegativeBalance_SignedNetReconciliation_RejectsSettlementWhenCreditExists()
    {
        var customer = SeedCustomer();
        var project = SeedProject(customer);
        var actorId = Guid.NewGuid();

        // 1. Issue material worth 1,000 Rs
        var (productId, productUnitId, _) = SeedSellableUnit(salePrice: 1000m, cost: 600m);
        var issueHandler = CreateIssueHandler();
        var issueCmd = new IssueThakaMaterialCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            ActorId: actorId,
            Note: "Initial material issue",
            Lines:
            [
                new IssueThakaMaterialLineInput(
                    productId,
                    productUnitId,
                    EnteredQuantity: 1m,
                    ExpectedUnitCharge: 1000m,
                    InventoryUnitIds: [])
            ]);

        var issueResult = await issueHandler.HandleAsync(issueCmd, CancellationToken.None);
        Assert.True(issueResult.IsSuccess, issueResult.Error?.Message);
        var issueId = issueResult.Value!.MaterialIssueId;

        // 2. Establish open cash drawer session
        var session = new CashSession
        {
            Id = Guid.NewGuid(),
            OpenedBy = actorId,
            OpeningCash = 5000m,
            Status = CashSessionStatus.Open,
            OpenedAt = DateTimeOffset.UtcNow,
            BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow)
        };
        _fakes.Cash.AddSession(session);

        // 3. Record payment of 1,000 Rs (balance becomes 0m)
        var paymentHandler = CreateRecordPaymentHandler();
        var paymentCmd = new RecordThakaPaymentCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            Amount: 1000m,
            PaymentMethod: ThakaPaymentMethod.Cash,
            Reference: "PAY-FULL",
            Note: "Full settlement payment in advance",
            ActorId: actorId);

        var paymentResult = await paymentHandler.HandleAsync(paymentCmd, CancellationToken.None);
        Assert.True(paymentResult.IsSuccess, paymentResult.Error?.Message);

        // 4. Paid-First Material Reversal: Customer returns all material
        var reverseHandler = CreateReverseHandler();
        var reverseCmd = new ReverseThakaMaterialCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            MaterialIssueId: issueId,
            Reason: "Full material return after payment",
            ActorId: actorId);

        var reverseResult = await reverseHandler.HandleAsync(reverseCmd, CancellationToken.None);
        Assert.True(reverseResult.IsSuccess, reverseResult.Error?.Message);

        // 5. Attempt final settlement while customer has net credit balance (-1000 Rs)
        var settleHandler = CreateSettleHandler();
        var settleCmd = new SettleThakaCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            SettlementDiscount: 0m,
            FinalPaymentAmount: 0m,
            PaymentMethod: ThakaPaymentMethod.Cash,
            PaymentReference: null,
            ActorId: actorId);

        var settleResult = await settleHandler.HandleAsync(settleCmd, CancellationToken.None);

        // Must fail closed with thaka.project_has_credit_balance
        Assert.False(settleResult.IsSuccess);
        Assert.Equal("thaka.project_has_credit_balance", settleResult.Error?.Code);
        Assert.Equal(ThakaProjectStatus.Active, project.Status);
    }

    [Fact]
    public async Task THAKA_AUD_05_OperatorChoice_CreditRetention_PreservesLiabilityAndConsumesOnSubsequentIssues()
    {
        var customer = SeedCustomer();
        var project = SeedProject(customer);
        var actorId = Guid.NewGuid();

        // 1. Issue material worth 1,000 Rs
        var (productId1, unitId1, _) = SeedSellableUnit(salePrice: 1000m, cost: 600m);
        var issueHandler = CreateIssueHandler();
        var issueCmd1 = new IssueThakaMaterialCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            ActorId: actorId,
            Note: "Initial material issue",
            Lines:
            [
                new IssueThakaMaterialLineInput(productId1, unitId1, 1m, 1000m, [])
            ]);

        var issueResult1 = await issueHandler.HandleAsync(issueCmd1, CancellationToken.None);
        Assert.True(issueResult1.IsSuccess, issueResult1.Error?.Message);

        // 2. Open cash session and record payment of 1,000 Rs
        var session = new CashSession
        {
            Id = Guid.NewGuid(),
            OpenedBy = actorId,
            OpeningCash = 5000m,
            Status = CashSessionStatus.Open,
            OpenedAt = DateTimeOffset.UtcNow,
            BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow)
        };
        _fakes.Cash.AddSession(session);

        var paymentHandler = CreateRecordPaymentHandler();
        var paymentCmd = new RecordThakaPaymentCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            Amount: 1000m,
            PaymentMethod: ThakaPaymentMethod.Cash,
            Reference: "PAY-001",
            Note: "Advance payment",
            ActorId: actorId);

        var paymentResult = await paymentHandler.HandleAsync(paymentCmd, CancellationToken.None);
        Assert.True(paymentResult.IsSuccess, paymentResult.Error?.Message);

        // 3. Paid-First Material Reversal: Customer returns all 1,000 Rs material
        var reverseHandler = CreateReverseHandler();
        var reverseCmd = new ReverseThakaMaterialCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            MaterialIssueId: issueResult1.Value!.MaterialIssueId,
            Reason: "Full material return",
            ActorId: actorId);

        var reverseResult = await reverseHandler.HandleAsync(reverseCmd, CancellationToken.None);
        Assert.True(reverseResult.IsSuccess, reverseResult.Error?.Message);

        // 4. Operator choice: RETAIN CREDIT in the same project (per OWNER-UPDATE-20261019-001)
        // No cash movement occurs. Credit of 1,000 Rs is preserved as signed liability.
        Assert.DoesNotContain(_fakes.Cash.Movements, m => m.MovementType == CashMovementType.ThakaCreditRefundCashOut);

        // Attempting settlement while in credit fails closed
        var settleHandler = CreateSettleHandler();
        var earlySettleResult = await settleHandler.HandleAsync(new SettleThakaCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            SettlementDiscount: 0m,
            FinalPaymentAmount: 0m,
            PaymentMethod: ThakaPaymentMethod.Cash,
            PaymentReference: null,
            ActorId: actorId), CancellationToken.None);

        Assert.False(earlySettleResult.IsSuccess);
        Assert.Equal("thaka.project_has_credit_balance", earlySettleResult.Error?.Code);

        // 5. Subsequent material issue 1: 600 Rs to same project automatically consumes 600 Rs of the credit
        var (productId2, unitId2, _) = SeedSellableUnit(salePrice: 600m, cost: 400m);
        var issueCmd2 = new IssueThakaMaterialCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            ActorId: actorId,
            Note: "Subsequent material consuming credit part 1",
            Lines:
            [
                new IssueThakaMaterialLineInput(productId2, unitId2, 1m, 600m, [])
            ]);

        var issueResult2 = await issueHandler.HandleAsync(issueCmd2, CancellationToken.None);
        Assert.True(issueResult2.IsSuccess, issueResult2.Error?.Message);

        // 6. Subsequent material issue 2: 400 Rs to same project consumes remaining 400 Rs of credit
        var (productId3, unitId3, _) = SeedSellableUnit(salePrice: 400m, cost: 250m);
        var issueCmd3 = new IssueThakaMaterialCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            ActorId: actorId,
            Note: "Subsequent material consuming credit part 2",
            Lines:
            [
                new IssueThakaMaterialLineInput(productId3, unitId3, 1m, 400m, [])
            ]);

        var issueResult3 = await issueHandler.HandleAsync(issueCmd3, CancellationToken.None);
        Assert.True(issueResult3.IsSuccess, issueResult3.Error?.Message);

        // Total gross charges = 600 + 400 = 1000 Rs. Payments = 1000 Rs. Net balance = 0.00 Rs.
        // Final settlement now succeeds at 0.00 Rs without phantom payment
        var finalSettleResult = await settleHandler.HandleAsync(new SettleThakaCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            SettlementDiscount: 0m,
            FinalPaymentAmount: 0m,
            PaymentMethod: ThakaPaymentMethod.Cash,
            PaymentReference: null,
            ActorId: actorId), CancellationToken.None);

        Assert.True(finalSettleResult.IsSuccess, finalSettleResult.Error?.Message);
        Assert.Equal(ThakaProjectStatus.Settled, project.Status);
    }

    [Fact]
    public async Task THAKA_AUD_06_OperatorChoice_AuthorizedFullCreditRefund_EmitsCompensatingCashOutAndDischargesCredit()
    {
        var customer = SeedCustomer();
        var project = SeedProject(customer);
        var actorId = Guid.NewGuid();

        // 1. Issue material worth 1,000 Rs and record payment of 1,000 Rs
        var (productId, unitId, _) = SeedSellableUnit(salePrice: 1000m, cost: 600m);
        var issueHandler = CreateIssueHandler();
        var issueResult = await issueHandler.HandleAsync(new IssueThakaMaterialCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            ActorId: actorId,
            Note: "Wiring materials",
            Lines: [new IssueThakaMaterialLineInput(productId, unitId, 1m, 1000m, [])]), CancellationToken.None);
        Assert.True(issueResult.IsSuccess);

        var session = new CashSession
        {
            Id = Guid.NewGuid(),
            OpenedBy = actorId,
            OpeningCash = 5000m,
            Status = CashSessionStatus.Open,
            OpenedAt = DateTimeOffset.UtcNow,
            BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow)
        };
        _fakes.Cash.AddSession(session);

        var paymentHandler = CreateRecordPaymentHandler();
        var paymentResult = await paymentHandler.HandleAsync(new RecordThakaPaymentCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            Amount: 1000m,
            PaymentMethod: ThakaPaymentMethod.Cash,
            Reference: "PAY-1000",
            Note: "Advance payment",
            ActorId: actorId), CancellationToken.None);
        Assert.True(paymentResult.IsSuccess);

        // 2. Reverse material (balance becomes -1,000 Rs, customer has 1,000 Rs credit)
        var reverseHandler = CreateReverseHandler();
        var reverseResult = await reverseHandler.HandleAsync(new ReverseThakaMaterialCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            MaterialIssueId: issueResult.Value!.MaterialIssueId,
            Reason: "Full return",
            ActorId: actorId), CancellationToken.None);
        Assert.True(reverseResult.IsSuccess);

        // 3. Operator choice: AUTHORIZED REFUND (per OWNER-UPDATE-20261019-001)
        var refundHandler = CreateRefundCreditHandler();
        var refundOpId = Guid.NewGuid();
        var refundCmd = new RefundThakaCreditCommand(
            ClientOperationId: refundOpId,
            ProjectId: project.Id,
            Amount: 1000m,
            Reason: "Customer requested cash return for cancelled project",
            ActorId: actorId);

        var refundResult = await refundHandler.HandleAsync(refundCmd, CancellationToken.None);
        Assert.True(refundResult.IsSuccess, refundResult.Error?.Message);
        Assert.Equal(1000m, refundResult.Value!.RefundedAmount);
        Assert.Equal(0m, refundResult.Value.RemainingCredit);
        Assert.False(refundResult.Value.WasExisting);

        // Verify credit refund entity was persisted in repository
        Assert.Single(_fakes.Thaka.CreditRefunds, r => r.ClientOperationId == refundOpId && r.Amount == 1000m);

        // Verify compensating cash drawer outflow was recorded
        var cashOutflow = _fakes.Cash.Movements
            .FirstOrDefault(m => m.MovementType == CashMovementType.ThakaCreditRefundCashOut);
        Assert.NotNull(cashOutflow);
        Assert.Equal(1000m, cashOutflow.Amount);
        Assert.Equal(CashMovementDirection.Out, cashOutflow.Direction);

        // 4. Project balance is now exactly 0.00 Rs; settlement at zero balance succeeds!
        var settleHandler = CreateSettleHandler();
        var settleResult = await settleHandler.HandleAsync(new SettleThakaCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            SettlementDiscount: 0m,
            FinalPaymentAmount: 0m,
            PaymentMethod: ThakaPaymentMethod.Cash,
            PaymentReference: null,
            ActorId: actorId), CancellationToken.None);

        Assert.True(settleResult.IsSuccess, settleResult.Error?.Message);
        Assert.Equal(ThakaProjectStatus.Settled, project.Status);
    }

    [Fact]
    public async Task THAKA_AUD_07_OperatorChoice_AuthorizedPartialCreditRefund_SupportsMultiStageDischarge()
    {
        var customer = SeedCustomer();
        var project = SeedProject(customer);
        var actorId = Guid.NewGuid();

        // 1. Establish project with 1,000 Rs credit balance
        var (productId, unitId, _) = SeedSellableUnit(salePrice: 1000m, cost: 600m);
        var issueHandler = CreateIssueHandler();
        var issueResult = await issueHandler.HandleAsync(new IssueThakaMaterialCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            ActorId: actorId,
            Note: "Initial issue",
            Lines: [new IssueThakaMaterialLineInput(productId, unitId, 1m, 1000m, [])]), CancellationToken.None);

        var session = new CashSession
        {
            Id = Guid.NewGuid(),
            OpenedBy = actorId,
            OpeningCash = 5000m,
            Status = CashSessionStatus.Open,
            OpenedAt = DateTimeOffset.UtcNow,
            BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow)
        };
        _fakes.Cash.AddSession(session);

        var paymentHandler = CreateRecordPaymentHandler();
        await paymentHandler.HandleAsync(new RecordThakaPaymentCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            Amount: 1000m,
            PaymentMethod: ThakaPaymentMethod.Cash,
            Reference: "PAY-1000",
            Note: "Full payment",
            ActorId: actorId), CancellationToken.None);

        var reverseHandler = CreateReverseHandler();
        await reverseHandler.HandleAsync(new ReverseThakaMaterialCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            MaterialIssueId: issueResult.Value!.MaterialIssueId,
            Reason: "Full return",
            ActorId: actorId), CancellationToken.None);

        var refundHandler = CreateRefundCreditHandler();

        // 2. Multi-stage discharge: First partial refund of 400 Rs
        var refund1 = await refundHandler.HandleAsync(new RefundThakaCreditCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            Amount: 400m,
            Reason: "Partial refund phase 1",
            ActorId: actorId), CancellationToken.None);

        Assert.True(refund1.IsSuccess, refund1.Error?.Message);
        Assert.Equal(400m, refund1.Value!.RefundedAmount);
        Assert.Equal(600m, refund1.Value.RemainingCredit);

        // Settlement still rejected while 600 Rs credit remains
        var settleHandler = CreateSettleHandler();
        var midSettle = await settleHandler.HandleAsync(new SettleThakaCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            SettlementDiscount: 0m,
            FinalPaymentAmount: 0m,
            PaymentMethod: ThakaPaymentMethod.Cash,
            PaymentReference: null,
            ActorId: actorId), CancellationToken.None);
        Assert.False(midSettle.IsSuccess);
        Assert.Equal("thaka.project_has_credit_balance", midSettle.Error?.Code);

        // 3. Second partial refund of remaining 600 Rs
        var refund2 = await refundHandler.HandleAsync(new RefundThakaCreditCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            Amount: 600m,
            Reason: "Partial refund phase 2 remainder",
            ActorId: actorId), CancellationToken.None);

        Assert.True(refund2.IsSuccess, refund2.Error?.Message);
        Assert.Equal(600m, refund2.Value!.RefundedAmount);
        Assert.Equal(0m, refund2.Value.RemainingCredit);

        // Total outflows = 2 movements (400 + 600 = 1000)
        var outflows = _fakes.Cash.Movements
            .Where(m => m.MovementType == CashMovementType.ThakaCreditRefundCashOut)
            .ToList();
        Assert.Equal(2, outflows.Count);
        Assert.Equal(1000m, outflows.Sum(m => m.Amount));

        // 4. Settlement now succeeds at 0.00 Rs
        var finalSettle = await settleHandler.HandleAsync(new SettleThakaCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            SettlementDiscount: 0m,
            FinalPaymentAmount: 0m,
            PaymentMethod: ThakaPaymentMethod.Cash,
            PaymentReference: null,
            ActorId: actorId), CancellationToken.None);
        Assert.True(finalSettle.IsSuccess, finalSettle.Error?.Message);
        Assert.Equal(ThakaProjectStatus.Settled, project.Status);
    }

    [Fact]
    public async Task THAKA_AUD_08_CreditRefund_SecurityAndBoundaryGuards_FailClosed()
    {
        var customer = SeedCustomer();
        var project = SeedProject(customer);
        var actorId = Guid.NewGuid();

        // Establish project with 500 Rs credit balance
        var (productId, unitId, _) = SeedSellableUnit(salePrice: 500m, cost: 300m);
        var issueHandler = CreateIssueHandler();
        var issueResult = await issueHandler.HandleAsync(new IssueThakaMaterialCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            ActorId: actorId,
            Note: "Initial issue",
            Lines: [new IssueThakaMaterialLineInput(productId, unitId, 1m, 500m, [])]), CancellationToken.None);

        var session = new CashSession
        {
            Id = Guid.NewGuid(),
            OpenedBy = actorId,
            OpeningCash = 5000m,
            Status = CashSessionStatus.Open,
            OpenedAt = DateTimeOffset.UtcNow,
            BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow)
        };
        _fakes.Cash.AddSession(session);

        var paymentHandler = CreateRecordPaymentHandler();
        await paymentHandler.HandleAsync(new RecordThakaPaymentCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            Amount: 500m,
            PaymentMethod: ThakaPaymentMethod.Cash,
            Reference: "PAY-500",
            Note: "Payment",
            ActorId: actorId), CancellationToken.None);

        var reverseHandler = CreateReverseHandler();
        await reverseHandler.HandleAsync(new ReverseThakaMaterialCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            MaterialIssueId: issueResult.Value!.MaterialIssueId,
            Reason: "Full return",
            ActorId: actorId), CancellationToken.None);

        var refundHandler = CreateRefundCreditHandler();

        // Guard 1: Over-refund (> 500 Rs available) fails closed
        var overRefund = await refundHandler.HandleAsync(new RefundThakaCreditCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            Amount: 600m,
            Reason: "Exceeds credit",
            ActorId: actorId), CancellationToken.None);
        Assert.False(overRefund.IsSuccess);
        Assert.Equal("thaka.refund_exceeds_credit", overRefund.Error?.Code);

        // Guard 2: Non-positive amount fails closed
        var zeroRefund = await refundHandler.HandleAsync(new RefundThakaCreditCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            Amount: 0m,
            Reason: "Zero refund",
            ActorId: actorId), CancellationToken.None);
        Assert.False(zeroRefund.IsSuccess);
        Assert.Equal("thaka.refund_invalid", zeroRefund.Error?.Code);

        // Guard 3: Empty operation ID fails closed
        var emptyOpRefund = await refundHandler.HandleAsync(new RefundThakaCreditCommand(
            ClientOperationId: Guid.Empty,
            ProjectId: project.Id,
            Amount: 100m,
            Reason: "Empty op ID",
            ActorId: actorId), CancellationToken.None);
        Assert.False(emptyOpRefund.IsSuccess);
        Assert.Equal("thaka.refund_invalid", emptyOpRefund.Error?.Code);

        // Guard 4: Blank reason fails closed
        var blankReason = await refundHandler.HandleAsync(new RefundThakaCreditCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            Amount: 100m,
            Reason: "   ",
            ActorId: actorId), CancellationToken.None);
        Assert.False(blankReason.IsSuccess);
        Assert.Equal("thaka.refund_reason_required", blankReason.Error?.Code);

        // Guard 5: Missing or closed cash session fails closed
        session.Status = CashSessionStatus.Closed;
        var closedSessionRefund = await refundHandler.HandleAsync(new RefundThakaCreditCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            Amount: 100m,
            Reason: "During closed drawer",
            ActorId: actorId), CancellationToken.None);
        Assert.False(closedSessionRefund.IsSuccess);
        Assert.Equal("cash.session_required", closedSessionRefund.Error?.Code);
    }

    [Fact]
    public async Task THAKA_AUD_09_CreditRefund_IdempotentReplay_And_DoubleDischargePrevention()
    {
        var customer = SeedCustomer();
        var project = SeedProject(customer);
        var actorId = Guid.NewGuid();

        // Establish project with 1,000 Rs credit balance
        var (productId, unitId, _) = SeedSellableUnit(salePrice: 1000m, cost: 600m);
        var issueHandler = CreateIssueHandler();
        var issueResult = await issueHandler.HandleAsync(new IssueThakaMaterialCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            ActorId: actorId,
            Note: "Initial issue",
            Lines: [new IssueThakaMaterialLineInput(productId, unitId, 1m, 1000m, [])]), CancellationToken.None);

        var session = new CashSession
        {
            Id = Guid.NewGuid(),
            OpenedBy = actorId,
            OpeningCash = 5000m,
            Status = CashSessionStatus.Open,
            OpenedAt = DateTimeOffset.UtcNow,
            BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow)
        };
        _fakes.Cash.AddSession(session);

        var paymentHandler = CreateRecordPaymentHandler();
        await paymentHandler.HandleAsync(new RecordThakaPaymentCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            Amount: 1000m,
            PaymentMethod: ThakaPaymentMethod.Cash,
            Reference: "PAY-1000",
            Note: "Full payment",
            ActorId: actorId), CancellationToken.None);

        var reverseHandler = CreateReverseHandler();
        await reverseHandler.HandleAsync(new ReverseThakaMaterialCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            MaterialIssueId: issueResult.Value!.MaterialIssueId,
            Reason: "Full return",
            ActorId: actorId), CancellationToken.None);

        var refundHandler = CreateRefundCreditHandler();
        var clientOpId = Guid.NewGuid();
        var refundCmd = new RefundThakaCreditCommand(
            ClientOperationId: clientOpId,
            ProjectId: project.Id,
            Amount: 1000m,
            Reason: "Full customer refund",
            ActorId: actorId);

        // 1. Initial refund execution succeeds
        var firstRefund = await refundHandler.HandleAsync(refundCmd, CancellationToken.None);
        Assert.True(firstRefund.IsSuccess, firstRefund.Error?.Message);
        Assert.False(firstRefund.Value!.WasExisting);
        Assert.Equal(1, _fakes.Cash.Movements.Count(m => m.MovementType == CashMovementType.ThakaCreditRefundCashOut));

        // 2. Idempotent replay with IDENTICAL clientOperationId succeeds without re-executing cash movement
        var replayRefund = await refundHandler.HandleAsync(refundCmd, CancellationToken.None);
        Assert.True(replayRefund.IsSuccess, replayRefund.Error?.Message);
        Assert.True(replayRefund.Value!.WasExisting);
        Assert.Equal(firstRefund.Value.RefundId, replayRefund.Value.RefundId);
        // Cash movement count MUST remain exactly 1 (no duplicate cash outflow on replay)
        Assert.Equal(1, _fakes.Cash.Movements.Count(m => m.MovementType == CashMovementType.ThakaCreditRefundCashOut));

        // 3. Distinct clientOperationId attempting to refund again fails closed (credit already discharged)
        var secondDistinctRefund = await refundHandler.HandleAsync(new RefundThakaCreditCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            Amount: 1000m,
            Reason: "Duplicate refund attempt",
            ActorId: actorId), CancellationToken.None);
        Assert.False(secondDistinctRefund.IsSuccess);
        Assert.Equal("thaka.no_credit_balance", secondDistinctRefund.Error?.Code);
    }

    [Fact]
    public async Task THAKA_AUD_10_SequentialDischarge_CreditRefundFirst_Then_PaymentReversalRefusal()
    {
        var customer = SeedCustomer();
        var project = SeedProject(customer);
        var actorId = Guid.NewGuid();

        // 1. Issue material worth 100 Rs
        var (productId, unitId, _) = SeedSellableUnit(salePrice: 100m, cost: 60m);
        var issueHandler = CreateIssueHandler();
        var issueResult = await issueHandler.HandleAsync(new IssueThakaMaterialCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            ActorId: actorId,
            Note: "Initial 100 Rs issue",
            Lines: [new IssueThakaMaterialLineInput(productId, unitId, 1m, 100m, [])]), CancellationToken.None);
        Assert.True(issueResult.IsSuccess);

        // 2. Open cash session and record payment of 100 Rs
        var session = new CashSession
        {
            Id = Guid.NewGuid(),
            OpenedBy = actorId,
            OpeningCash = 5000m,
            Status = CashSessionStatus.Open,
            OpenedAt = DateTimeOffset.UtcNow,
            BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow)
        };
        _fakes.Cash.AddSession(session);

        var paymentHandler = CreateRecordPaymentHandler();
        var paymentResult = await paymentHandler.HandleAsync(new RecordThakaPaymentCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            Amount: 100m,
            PaymentMethod: ThakaPaymentMethod.Cash,
            Reference: "PAY-100",
            Note: "Full payment",
            ActorId: actorId), CancellationToken.None);
        Assert.True(paymentResult.IsSuccess);
        var paymentId = paymentResult.Value!.PaymentId;

        // 3. Reverse material: customer returned 100 Rs material -> customer has 100 Rs credit balance
        var reverseHandler = CreateReverseHandler();
        var reverseResult = await reverseHandler.HandleAsync(new ReverseThakaMaterialCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            MaterialIssueId: issueResult.Value!.MaterialIssueId,
            Reason: "Full return",
            ActorId: actorId), CancellationToken.None);
        Assert.True(reverseResult.IsSuccess);

        // 4. Operator issues authorized credit refund of 100 Rs -> succeeds, 100 Rs cash disbursed
        var refundHandler = CreateRefundCreditHandler();
        var refundResult = await refundHandler.HandleAsync(new RefundThakaCreditCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            Amount: 100m,
            Reason: "Authorized credit refund",
            ActorId: actorId), CancellationToken.None);
        Assert.True(refundResult.IsSuccess);
        Assert.Equal(100m, refundResult.Value!.RefundedAmount);

        // 5. Sequential Double-Discharge Attack: Attempt to reverse original 100 Rs payment
        // Gross payments collected was 100, but 100 was already discharged via credit refund.
        // Net payments collected is 0m. Reversing 100m payment MUST FAIL CLOSED with thaka.payment_already_refunded!
        var reversePaymentHandler = CreateReversePaymentHandler();
        var reversePaymentResult = await reversePaymentHandler.HandleAsync(new ReverseThakaPaymentCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            PaymentId: paymentId,
            Reason: "Attempt reversal after credit refund",
            ActorId: actorId), CancellationToken.None);

        Assert.False(reversePaymentResult.IsSuccess);
        Assert.Equal("thaka.payment_already_refunded", reversePaymentResult.Error?.Code);

        // Invariant: Total cash disbursed to customer is strictly 100m, never 200m!
        var totalDisbursed = _fakes.Cash.Movements
            .Where(m => m.Direction == CashMovementDirection.Out)
            .Sum(m => m.Amount);
        Assert.Equal(100m, totalDisbursed);
    }

    [Fact]
    public async Task THAKA_AUD_11_SequentialDischarge_PaymentReversalFirst_Then_CreditRefundRefusal()
    {
        var customer = SeedCustomer();
        var project = SeedProject(customer);
        var actorId = Guid.NewGuid();

        // 1. Issue material worth 100 Rs
        var (productId, unitId, _) = SeedSellableUnit(salePrice: 100m, cost: 60m);
        var issueHandler = CreateIssueHandler();
        var issueResult = await issueHandler.HandleAsync(new IssueThakaMaterialCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            ActorId: actorId,
            Note: "Initial 100 Rs issue",
            Lines: [new IssueThakaMaterialLineInput(productId, unitId, 1m, 100m, [])]), CancellationToken.None);
        Assert.True(issueResult.IsSuccess);

        // 2. Open cash session and record payment of 100 Rs
        var session = new CashSession
        {
            Id = Guid.NewGuid(),
            OpenedBy = actorId,
            OpeningCash = 5000m,
            Status = CashSessionStatus.Open,
            OpenedAt = DateTimeOffset.UtcNow,
            BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow)
        };
        _fakes.Cash.AddSession(session);

        var paymentHandler = CreateRecordPaymentHandler();
        var paymentResult = await paymentHandler.HandleAsync(new RecordThakaPaymentCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            Amount: 100m,
            PaymentMethod: ThakaPaymentMethod.Cash,
            Reference: "PAY-100",
            Note: "Full payment",
            ActorId: actorId), CancellationToken.None);
        Assert.True(paymentResult.IsSuccess);
        var paymentId = paymentResult.Value!.PaymentId;

        // 3. Reverse material: 100 Rs material returned
        var reverseHandler = CreateReverseHandler();
        await reverseHandler.HandleAsync(new ReverseThakaMaterialCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            MaterialIssueId: issueResult.Value!.MaterialIssueId,
            Reason: "Full return",
            ActorId: actorId), CancellationToken.None);

        // 4. Reverse payment FIRST -> succeeds, 100 Rs disbursed as payment reversal
        var reversePaymentHandler = CreateReversePaymentHandler();
        var reversePaymentResult = await reversePaymentHandler.HandleAsync(new ReverseThakaPaymentCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            PaymentId: paymentId,
            Reason: "Payment reversal first",
            ActorId: actorId), CancellationToken.None);
        Assert.True(reversePaymentResult.IsSuccess);

        // 5. Subsequent attempt to refund credit of 100 Rs MUST FAIL CLOSED because payment reversal reduced
        // net collected payments to 0, leaving 0 credit balance available!
        var refundHandler = CreateRefundCreditHandler();
        var refundResult = await refundHandler.HandleAsync(new RefundThakaCreditCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            Amount: 100m,
            Reason: "Attempt refund after payment reversal",
            ActorId: actorId), CancellationToken.None);

        Assert.False(refundResult.IsSuccess);
        Assert.Equal("thaka.no_credit_balance", refundResult.Error?.Code);

        // Invariant: Total cash disbursed to customer is strictly 100m, never 200m!
        var totalDisbursed = _fakes.Cash.Movements
            .Where(m => m.Direction == CashMovementDirection.Out)
            .Sum(m => m.Amount);
        Assert.Equal(100m, totalDisbursed);
    }

    [Fact]
    public async Task THAKA_AUD_12_SequentialDischarge_PartialCreditRefund_Plus_PaymentReversal_EnforcesConservation()
    {
        var customer = SeedCustomer();
        var project = SeedProject(customer);
        var actorId = Guid.NewGuid();

        // 1. Issue material worth 150 Rs
        var (productId, unitId, _) = SeedSellableUnit(salePrice: 150m, cost: 90m);
        var issueHandler = CreateIssueHandler();
        var issueResult = await issueHandler.HandleAsync(new IssueThakaMaterialCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            ActorId: actorId,
            Note: "150 Rs issue",
            Lines: [new IssueThakaMaterialLineInput(productId, unitId, 1m, 150m, [])]), CancellationToken.None);

        var session = new CashSession
        {
            Id = Guid.NewGuid(),
            OpenedBy = actorId,
            OpeningCash = 5000m,
            Status = CashSessionStatus.Open,
            OpenedAt = DateTimeOffset.UtcNow,
            BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow)
        };
        _fakes.Cash.AddSession(session);

        // 2. Customer makes Payment 1 (100 Rs) and Payment 2 (50 Rs) -> Total paid = 150 Rs
        var paymentHandler = CreateRecordPaymentHandler();
        var paymentResult1 = await paymentHandler.HandleAsync(new RecordThakaPaymentCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            Amount: 100m,
            PaymentMethod: ThakaPaymentMethod.Cash,
            Reference: "PAY-1",
            Note: "First payment",
            ActorId: actorId), CancellationToken.None);
        var payment1Id = paymentResult1.Value!.PaymentId;

        var paymentResult2 = await paymentHandler.HandleAsync(new RecordThakaPaymentCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            Amount: 50m,
            PaymentMethod: ThakaPaymentMethod.Cash,
            Reference: "PAY-2",
            Note: "Second payment",
            ActorId: actorId), CancellationToken.None);
        var payment2Id = paymentResult2.Value!.PaymentId;

        // 3. Customer returns all 150 Rs material -> Credit balance becomes 150 Rs
        var reverseHandler = CreateReverseHandler();
        await reverseHandler.HandleAsync(new ReverseThakaMaterialCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            MaterialIssueId: issueResult.Value!.MaterialIssueId,
            Reason: "Full return",
            ActorId: actorId), CancellationToken.None);

        // 4. Operator issues partial credit refund of 60 Rs -> Remaining collected payments = 150 - 60 = 90 Rs
        var refundHandler = CreateRefundCreditHandler();
        var refundResult1 = await refundHandler.HandleAsync(new RefundThakaCreditCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            Amount: 60m,
            Reason: "Partial refund 60 Rs",
            ActorId: actorId), CancellationToken.None);
        Assert.True(refundResult1.IsSuccess);

        // 5. Reversing Payment 1 (100 Rs) now fails closed because remaining collected is only 90 Rs (100 > 90)
        var reversePaymentHandler = CreateReversePaymentHandler();
        var failedReversal = await reversePaymentHandler.HandleAsync(new ReverseThakaPaymentCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            PaymentId: payment1Id,
            Reason: "Reversal exceeding remaining collected",
            ActorId: actorId), CancellationToken.None);
        Assert.False(failedReversal.IsSuccess);
        Assert.Equal("thaka.payment_already_refunded", failedReversal.Error?.Code);

        // 6. Reversing Payment 2 (50 Rs) succeeds because 50 <= 90 Rs!
        var successReversal = await reversePaymentHandler.HandleAsync(new ReverseThakaPaymentCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            PaymentId: payment2Id,
            Reason: "Valid reversal of payment 2 (50 Rs <= 90 Rs)",
            ActorId: actorId), CancellationToken.None);
        Assert.True(successReversal.IsSuccess);

        // Remaining collected is now 90 - 50 = 40 Rs.
        // Payment 1 (100 Rs) still cannot be reversed (100 > 40)
        var failedReversalAgain = await reversePaymentHandler.HandleAsync(new ReverseThakaPaymentCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            PaymentId: payment1Id,
            Reason: "Reversal still exceeding remaining collected",
            ActorId: actorId), CancellationToken.None);
        Assert.False(failedReversalAgain.IsSuccess);
        Assert.Equal("thaka.payment_already_refunded", failedReversalAgain.Error?.Code);

        // 7. Customer receives remaining credit refund of 40 Rs
        var refundResult2 = await refundHandler.HandleAsync(new RefundThakaCreditCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            Amount: 40m,
            Reason: "Remaining credit refund 40 Rs",
            ActorId: actorId), CancellationToken.None);
        Assert.True(refundResult2.IsSuccess);

        // 8. Strict mathematical conservation verification:
        // Gross payments = 100 + 50 = 150 Rs
        // Total cash disbursed = 60 (refund 1) + 50 (reversal 2) + 40 (refund 2) = 150 Rs == 150 Rs!
        var totalDisbursed = _fakes.Cash.Movements
            .Where(m => m.Direction == CashMovementDirection.Out)
            .Sum(m => m.Amount);
        Assert.Equal(150m, totalDisbursed);
    }

    /// <summary>
    /// THAKA_AUD_13 Unit Semantics: In-memory simulation using fake locks (FakeResourceLock / FakeOperationLock
    /// return completed tasks). Proves application handler conservation invariant under Task.WhenAll.
    /// Real PostgreSQL 18 competing transaction row-lock serialization is proven in Phase11ThakaPostgresTransactionIntegrationTests.
    /// </summary>
    [Fact]
    public async Task THAKA_AUD_13_ConcurrentCompetingDischarges_ParallelCreditRefundAndPaymentReversal_ConservesCash()
    {
        var customer = SeedCustomer();
        var project = SeedProject(customer);
        var actorId = Guid.NewGuid();

        var (productId, unitId, _) = SeedSellableUnit(salePrice: 100m, cost: 60m);
        var issueHandler = CreateIssueHandler();
        var issueResult = await issueHandler.HandleAsync(new IssueThakaMaterialCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            ActorId: actorId,
            Note: "100 Rs issue",
            Lines: [new IssueThakaMaterialLineInput(productId, unitId, 1m, 100m, [])]), CancellationToken.None);

        var session = new CashSession
        {
            Id = Guid.NewGuid(),
            OpenedBy = actorId,
            OpeningCash = 5000m,
            Status = CashSessionStatus.Open,
            OpenedAt = DateTimeOffset.UtcNow,
            BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow)
        };
        _fakes.Cash.AddSession(session);

        var paymentHandler = CreateRecordPaymentHandler();
        var paymentResult = await paymentHandler.HandleAsync(new RecordThakaPaymentCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            Amount: 100m,
            PaymentMethod: ThakaPaymentMethod.Cash,
            Reference: "PAY-100",
            Note: "Full payment",
            ActorId: actorId), CancellationToken.None);
        var paymentId = paymentResult.Value!.PaymentId;

        var reverseHandler = CreateReverseHandler();
        await reverseHandler.HandleAsync(new ReverseThakaMaterialCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            MaterialIssueId: issueResult.Value!.MaterialIssueId,
            Reason: "Full return",
            ActorId: actorId), CancellationToken.None);

        // Competing concurrent race between refund and payment reversal
        var refundHandler = CreateRefundCreditHandler();
        var reversePaymentHandler = CreateReversePaymentHandler();

        var refundTask = refundHandler.HandleAsync(new RefundThakaCreditCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            Amount: 100m,
            Reason: "Parallel refund attempt",
            ActorId: actorId), CancellationToken.None);

        var reversalTask = reversePaymentHandler.HandleAsync(new ReverseThakaPaymentCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            PaymentId: paymentId,
            Reason: "Parallel reversal attempt",
            ActorId: actorId), CancellationToken.None);

        await Task.WhenAll(refundTask, reversalTask);

        var rRefund = await refundTask;
        var rReversal = await reversalTask;

        // Exactly one must succeed, or if both sequentially checked, total disbursed cannot exceed 100m!
        var totalDisbursed = _fakes.Cash.Movements
            .Where(m => m.Direction == CashMovementDirection.Out)
            .Sum(m => m.Amount);
        Assert.True(totalDisbursed <= 100m, $"Total disbursed {totalDisbursed} exceeded payments collected 100m!");
    }

    [Fact]
    public async Task THAKA_AUD_14_CreditRetention_AcrossRestartAndRefresh_PreservesSignedLiabilityWithoutCashMovement()
    {
        var customer = SeedCustomer();
        var project = SeedProject(customer);
        var actorId = Guid.NewGuid();

        var (productId, unitId, _) = SeedSellableUnit(salePrice: 500m, cost: 300m);
        var issueHandler = CreateIssueHandler();
        var issueResult = await issueHandler.HandleAsync(new IssueThakaMaterialCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            ActorId: actorId,
            Note: "500 Rs issue",
            Lines: [new IssueThakaMaterialLineInput(productId, unitId, 1m, 500m, [])]), CancellationToken.None);

        var session = new CashSession
        {
            Id = Guid.NewGuid(),
            OpenedBy = actorId,
            OpeningCash = 5000m,
            Status = CashSessionStatus.Open,
            OpenedAt = DateTimeOffset.UtcNow,
            BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow)
        };
        _fakes.Cash.AddSession(session);

        var paymentHandler = CreateRecordPaymentHandler();
        await paymentHandler.HandleAsync(new RecordThakaPaymentCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            Amount: 500m,
            PaymentMethod: ThakaPaymentMethod.Cash,
            Reference: "PAY-500",
            Note: "Payment",
            ActorId: actorId), CancellationToken.None);

        var reverseHandler = CreateReverseHandler();
        await reverseHandler.HandleAsync(new ReverseThakaMaterialCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            MaterialIssueId: issueResult.Value!.MaterialIssueId,
            Reason: "Full return",
            ActorId: actorId), CancellationToken.None);

        // Operator choice: Retain credit in project.
        // Zero cash movements recorded
        Assert.DoesNotContain(_fakes.Cash.Movements, m => m.Direction == CashMovementDirection.Out);

        // Simulate restart / fresh query: read gross material charges and collected payments
        var grossCharges = await _fakes.Thaka.GetGrossMaterialChargesAsync(project.Id, CancellationToken.None);
        var paymentsCollected = await _fakes.Thaka.GetPaymentsCollectedAsync(project.Id, CancellationToken.None);
        var netBalance = grossCharges - paymentsCollected;

        // Unclamped negative signed liability is strictly -500m
        Assert.Equal(0m, grossCharges);
        Assert.Equal(500m, paymentsCollected);
        Assert.Equal(-500m, netBalance);

        // Settlement fails closed while credit balance exists
        var settleHandler = CreateSettleHandler();
        var settleResult = await settleHandler.HandleAsync(new SettleThakaCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            SettlementDiscount: 0m,
            FinalPaymentAmount: 0m,
            PaymentMethod: ThakaPaymentMethod.Cash,
            PaymentReference: null,
            ActorId: actorId), CancellationToken.None);
        Assert.False(settleResult.IsSuccess);
        Assert.Equal("thaka.project_has_credit_balance", settleResult.Error?.Code);
    }

    [Fact]
    public async Task THAKA_AUD_15_CreditRefund_DeniedPermissions_FailsClosedWithoutDrawerMovement()
    {
        var customer = SeedCustomer();
        var project = SeedProject(customer);
        var actorId = Guid.NewGuid();

        var (productId, unitId, _) = SeedSellableUnit(salePrice: 500m, cost: 300m);
        var issueHandler = CreateIssueHandler();
        var issueResult = await issueHandler.HandleAsync(new IssueThakaMaterialCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            ActorId: actorId,
            Note: "500 Rs issue",
            Lines: [new IssueThakaMaterialLineInput(productId, unitId, 1m, 500m, [])]), CancellationToken.None);

        var session = new CashSession
        {
            Id = Guid.NewGuid(),
            OpenedBy = actorId,
            OpeningCash = 5000m,
            Status = CashSessionStatus.Open,
            OpenedAt = DateTimeOffset.UtcNow,
            BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow)
        };
        _fakes.Cash.AddSession(session);

        var paymentHandler = CreateRecordPaymentHandler();
        await paymentHandler.HandleAsync(new RecordThakaPaymentCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            Amount: 500m,
            PaymentMethod: ThakaPaymentMethod.Cash,
            Reference: "PAY-500",
            Note: "Payment",
            ActorId: actorId), CancellationToken.None);

        var reverseHandler = CreateReverseHandler();
        await reverseHandler.HandleAsync(new ReverseThakaMaterialCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            MaterialIssueId: issueResult.Value!.MaterialIssueId,
            Reason: "Full return",
            ActorId: actorId), CancellationToken.None);

        // Deny permission for refund
        _fakes.Authorization.IsAuthorized = false;

        var refundHandler = CreateRefundCreditHandler();
        var refundResult = await refundHandler.HandleAsync(new RefundThakaCreditCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            Amount: 500m,
            Reason: "Unauthorized refund attempt",
            ActorId: actorId), CancellationToken.None);

        Assert.False(refundResult.IsSuccess);
        Assert.Equal("auth.forbidden", refundResult.Error?.Code);

        // Invariant: Zero cash outflow recorded
        Assert.DoesNotContain(_fakes.Cash.Movements, m => m.Direction == CashMovementDirection.Out);
        Assert.Empty(_fakes.Thaka.CreditRefunds);

        // Restore permission for other tests
        _fakes.Authorization.IsAuthorized = true;
    }

    /// <summary>
    /// THAKA_AUD_16 Unit Semantics: In-memory simulation where TransactionRunner throws upon entry (pre-mutation abort).
    /// Proves handler fails closed when transaction runner refuses before operation.
    /// Real PostgreSQL 18 post-mutation failure injection (throwing AFTER credit refund and cash movement mutations
    /// immediately before commit to prove 100% atomic DB rollback) is proven in Phase11ThakaPostgresTransactionIntegrationTests.
    /// </summary>
    [Fact]
    public async Task THAKA_AUD_16_CreditRefund_AtomicRollback_OnFailure_PreservesCreditAndState()
    {
        var customer = SeedCustomer();
        var project = SeedProject(customer);
        var actorId = Guid.NewGuid();

        var (productId, unitId, _) = SeedSellableUnit(salePrice: 500m, cost: 300m);
        var issueHandler = CreateIssueHandler();
        var issueResult = await issueHandler.HandleAsync(new IssueThakaMaterialCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            ActorId: actorId,
            Note: "500 Rs issue",
            Lines: [new IssueThakaMaterialLineInput(productId, unitId, 1m, 500m, [])]), CancellationToken.None);

        var session = new CashSession
        {
            Id = Guid.NewGuid(),
            OpenedBy = actorId,
            OpeningCash = 5000m,
            Status = CashSessionStatus.Open,
            OpenedAt = DateTimeOffset.UtcNow,
            BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow)
        };
        _fakes.Cash.AddSession(session);

        var paymentHandler = CreateRecordPaymentHandler();
        await paymentHandler.HandleAsync(new RecordThakaPaymentCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            Amount: 500m,
            PaymentMethod: ThakaPaymentMethod.Cash,
            Reference: "PAY-500",
            Note: "Payment",
            ActorId: actorId), CancellationToken.None);

        var reverseHandler = CreateReverseHandler();
        await reverseHandler.HandleAsync(new ReverseThakaMaterialCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            MaterialIssueId: issueResult.Value!.MaterialIssueId,
            Reason: "Full return",
            ActorId: actorId), CancellationToken.None);

        // Simulate transaction abort during refund
        _fakes.Transactions.ShouldFail = true;

        var refundHandler = CreateRefundCreditHandler();
        await Assert.ThrowsAsync<InvalidOperationException>(() => refundHandler.HandleAsync(new RefundThakaCreditCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            Amount: 500m,
            Reason: "Refund during transaction crash",
            ActorId: actorId), CancellationToken.None));

        // Restore transaction runner
        _fakes.Transactions.ShouldFail = false;

        // Invariant: No credit refund persisted, credit balance remains intact
        Assert.Empty(_fakes.Thaka.CreditRefunds);
        var paymentsCollected = await _fakes.Thaka.GetPaymentsCollectedAsync(project.Id, CancellationToken.None);
        Assert.Equal(500m, paymentsCollected);
    }

    [Fact]
    public async Task THAKA_AUD_17_CreditRefund_ResponseLossRecovery_IdempotentReplayReturnsCommittedOutcome()
    {
        var customer = SeedCustomer();
        var project = SeedProject(customer);
        var actorId = Guid.NewGuid();

        var (productId, unitId, _) = SeedSellableUnit(salePrice: 300m, cost: 180m);
        var issueHandler = CreateIssueHandler();
        var issueResult = await issueHandler.HandleAsync(new IssueThakaMaterialCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            ActorId: actorId,
            Note: "300 Rs issue",
            Lines: [new IssueThakaMaterialLineInput(productId, unitId, 1m, 300m, [])]), CancellationToken.None);

        var session = new CashSession
        {
            Id = Guid.NewGuid(),
            OpenedBy = actorId,
            OpeningCash = 5000m,
            Status = CashSessionStatus.Open,
            OpenedAt = DateTimeOffset.UtcNow,
            BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow)
        };
        _fakes.Cash.AddSession(session);

        var paymentHandler = CreateRecordPaymentHandler();
        await paymentHandler.HandleAsync(new RecordThakaPaymentCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            Amount: 300m,
            PaymentMethod: ThakaPaymentMethod.Cash,
            Reference: "PAY-300",
            Note: "Payment",
            ActorId: actorId), CancellationToken.None);

        var reverseHandler = CreateReverseHandler();
        await reverseHandler.HandleAsync(new ReverseThakaMaterialCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: project.Id,
            MaterialIssueId: issueResult.Value!.MaterialIssueId,
            Reason: "Full return",
            ActorId: actorId), CancellationToken.None);

        var refundHandler = CreateRefundCreditHandler();
        var refundOpId = Guid.NewGuid();
        var refundCmd = new RefundThakaCreditCommand(
            ClientOperationId: refundOpId,
            ProjectId: project.Id,
            Amount: 300m,
            Reason: "Customer refund with simulated response loss",
            ActorId: actorId);

        // 1. First execution succeeds and commits outcome to ledger
        var firstResult = await refundHandler.HandleAsync(refundCmd, CancellationToken.None);
        Assert.True(firstResult.IsSuccess);
        Assert.False(firstResult.Value!.WasExisting);
        Assert.Equal(300m, firstResult.Value.RefundedAmount);
        Assert.Equal(1, _fakes.Cash.Movements.Count(m => m.MovementType == CashMovementType.ThakaCreditRefundCashOut));

        // 2. Client experiences response loss, reconnects, and replays identical command
        var replayedResult = await refundHandler.HandleAsync(refundCmd, CancellationToken.None);
        Assert.True(replayedResult.IsSuccess);
        Assert.True(replayedResult.Value!.WasExisting);
        Assert.Equal(firstResult.Value.RefundId, replayedResult.Value.RefundId);
        Assert.Equal(firstResult.Value.RefundNumber, replayedResult.Value.RefundNumber);
        Assert.Equal(300m, replayedResult.Value.RefundedAmount);

        // Invariant: Cash movement count and repository entities remain strictly 1 (no duplicate cash-out or refund row)
        Assert.Equal(1, _fakes.Cash.Movements.Count(m => m.MovementType == CashMovementType.ThakaCreditRefundCashOut));
        Assert.Single(_fakes.Thaka.CreditRefunds);
    }
}

