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
}
