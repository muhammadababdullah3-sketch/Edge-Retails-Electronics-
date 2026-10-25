using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Features.Warranty;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.Warranty;
using Xunit;

namespace EdgeRetails.UnitTests;

public sealed class Phase11ReturnExchangePackSubLedgerTests
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

    private CreateSaleReturnHandler CreateSaleReturnHandler() =>
        new(
            _fakes.Sales,
            _fakes.Inventory,
            _fakes.CostAllocator,
            _fakes.Cash,
            _fakes.OperationLock,
            _fakes.ResourceLock,
            _fakes.Audit,
            _fakes.Numbers,
            _fakes.Clock,
            _fakes.Transactions,
            _fakes.Authorization,
            _fakes.UnitOfWork,
            warranty: _fakes.Warranty,
            outcomeLedger: _fakes.OutcomeLedger,
            catalog: _fakes.Catalog,
            physicalUnits: _fakes.PhysicalUnits,
            packAllocations: _fakes.PackAllocations,
            traceability: _fakes.Traceability,
            parties: _fakes.Parties,
            eligibility: _fakes.Eligibility);

    private CommercialExchangeHandler CreateCommercialExchangeHandler() =>
        new(
            _fakes.Sales,
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
            warranty: _fakes.Warranty,
            outcomeLedger: _fakes.OutcomeLedger,
            packAllocations: _fakes.PackAllocations,
            physicalUnits: _fakes.PhysicalUnits,
            traceability: _fakes.Traceability,
            eligibility: _fakes.Eligibility);

    private (Supplier Supplier, Product CableProduct, ProductUnit CableUnit, SupplierProduct SupplierProduct) SeedCableProduct()
    {
        var supplier = new Supplier
        {
            Id = Guid.CreateVersion7(),
            DealerCode = "CAB",
            Name = "Cable Corp",
            IsActive = true
        };
        _fakes.Parties.AddSupplier(supplier);

        var meterUnit = new Unit { Id = Guid.CreateVersion7(), Name = "Meter", Symbol = "m" };
        var cableProduct = new Product
        {
            Id = Guid.CreateVersion7(),
            Name = "Cat6 Copper Cable",
            Sku = "CAT6-50M",
            BaseUnitId = meterUnit.Id,
            TrackingMode = TrackingMode.Container,
            SerialTrackingEnabled = false,
            ImeiTrackingEnabled = false,
            DefaultSalePrice = 15m,
            ReferencePurchaseCost = 10m,
            IsActive = true
        };
        _fakes.Catalog.Products[cableProduct.Id] = cableProduct;

        var pu = new ProductUnit
        {
            Id = Guid.CreateVersion7(),
            ProductId = cableProduct.Id,
            UnitId = meterUnit.Id,
            FactorToBaseUnit = 1m,
            IsDefaultPurchaseUnit = true,
            IsDefaultSaleUnit = true,
            CanPurchase = true,
            CanSell = true
        };
        _fakes.Catalog.AddProductUnit(pu);

        var sp = new SupplierProduct
        {
            Id = Guid.CreateVersion7(),
            SupplierId = supplier.Id,
            ProductId = cableProduct.Id,
            NextItemSequence = 1,
            IsActive = true,
            CreatedAt = _fakes.Clock.UtcNow,
            UpdatedAt = _fakes.Clock.UtcNow,
            Version = 1
        };
        _fakes.Traceability.AddSupplierProduct(sp);

        return (supplier, cableProduct, pu, sp);
    }

    private Customer SeedCustomer()
    {
        var customer = new Customer
        {
            Id = Guid.CreateVersion7(),
            Name = "Jane Electrician",
            IsActive = true
        };
        _fakes.Parties.AddCustomer(customer);
        return customer;
    }

    private CashSession OpenCashSession(Guid cashierId, decimal openingCash = 5000m)
    {
        var session = new CashSession
        {
            Id = Guid.CreateVersion7(),
            OpenedBy = cashierId,
            OpeningCash = openingCash,
            Status = CashSessionStatus.Open,
            OpenedAt = DateTimeOffset.UtcNow,
            BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow)
        };
        _fakes.Cash.AddSession(session);
        return session;
    }

    [Fact]
    public Task SaleReturn_ContinuousCoilReturn_LeavesOriginalCoilUntouched_AndCreatesSeparateLooseUnit() =>
        VerifyContinuousCoilReturnAsync(10m, 70m, 20m, 430m, false);

    [Fact]
    public Task SaleReturn_FractionalCarryingAmount_RoundTripsThroughFutureLooseSaleAndReplay() =>
        VerifyContinuousCoilReturnAsync(33.333333m, 233.333333m, 66.666667m, 1433.333334m, true);

    private async Task VerifyContinuousCoilReturnAsync(decimal rate, decimal soldCost, decimal returnedCost,
        decimal initialCarryingCost, bool verifyFutureSale)
    {
        // 1. Setup Cable Product (50m reel)
        var (supplier, product, pu, sp) = SeedCableProduct();
        var customer = SeedCustomer();
        var cashierId = Guid.CreateVersion7();
        var session = OpenCashSession(cashierId);

        // Seed stock balance & cost state
        var stock = new StockBalance { ProductId = product.Id, SellableQty = 43m };
        _fakes.Inventory.Balances[product.Id] = stock;
        var costState = new ProductCostState
        {
            ProductId = product.Id,
            CostedQty = 43m,
            TotalInventoryCost = initialCarryingCost,
            MovingAverageCost = decimal.Round(initialCarryingCost / 43m, 6)
        };
        _fakes.Inventory.CostStates[product.Id] = costState;

        // Seed continuous coil unit with 43m remaining in stock
        var coilUnit = new InventoryUnit
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            SupplierProductId = sp.Id,
            SourcePurchaseItemId = Guid.CreateVersion7(),
            Status = InventoryUnitStatus.InStock,
            TrackingCode = "CAB-PKF-CAT6-000001",
            ItemSequence = 1,
            InitialBaseQuantity = 50m,
            RemainingBaseQuantity = 43m,
            PackState = PackState.Open,
            IsLooseStock = false,
            AcquisitionCost = initialCarryingCost + soldCost
        };
        _fakes.Inventory.Units.Add(coilUnit);

        var lot = new InventoryLot
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            PurchaseItemId = coilUnit.SourcePurchaseItemId,
            SourceMovementId = Guid.CreateVersion7(),
            ReceivedQuantity = 50m,
            OriginalUnitCost = rate,
            EffectiveUnitCost = rate,
            CreatedAt = _fakes.Clock.UtcNow
        };
        coilUnit.InventoryLotId = lot.Id;
        _fakes.Inventory.Lots.Add(lot);
        _fakes.Inventory.LotBucketBalances.Add(new InventoryLotBucketBalance
        {
            Id = Guid.CreateVersion7(),
            LotId = lot.Id,
            StockBucket = InventoryBucket.Sellable,
            Quantity = 43m
        });

        // Seed Sale: 7m sold from the 50m coil
        var sale = new Sale
        {
            Id = Guid.CreateVersion7(),
            InvoiceNumber = "INV-0001",
            CustomerId = customer.Id,
            CashierUserId = cashierId,
            Subtotal = 105m,
            GrandTotal = 105m,
            Status = SaleStatus.Completed,
            CompletedAt = _fakes.Clock.UtcNow
        };
        _fakes.Sales.AddSale(sale);

        var saleItem = new SaleItem
        {
            Id = Guid.CreateVersion7(),
            SaleId = sale.Id,
            ProductId = product.Id,
            ProductUnitId = pu.Id,
            FactorToBaseSnapshot = 1m,
            EnteredQuantity = 7m,
            BaseQuantity = 7m,
            UnitPrice = 15m,
            GrossLineTotal = 105m,
            NetLineTotal = 105m,
            UnitCostSnapshot = rate,
            TotalCostSnapshot = soldCost
        };
        _fakes.Sales.AddSaleItem(saleItem);

        var saleItemUnit = new SaleItemUnit
        {
            SaleItemId = saleItem.Id,
            InventoryUnitId = coilUnit.Id,
            UnitCostSnapshot = rate
        };
        _fakes.Sales.AddSaleItemUnit(saleItemUnit);
        var sourceConsumptionId = Guid.CreateVersion7();
        _fakes.Sales.SoldSourceCapacities.Add(new SoldSourceCapacity(
            SaleConsumptionId: sourceConsumptionId,
            OriginalLotId: lot.Id,
            PurchaseItemId: lot.PurchaseItemId,
            SupplierId: supplier.Id,
            SoldQuantity: 7m,
            RemainingQuantity: 7m));

        // 2. Return 2m of the 7m cut
        var returnHandler = CreateSaleReturnHandler();
        var returnCommand = new CreateSaleReturnCommand(
                sale.Id,
                ReasonCode: "SURPLUS",
                ReasonNote: "Returned 2m unused segment",
                RefundMethod: RefundMethod.Cash,
                CreatedBy: cashierId,
                ClientOperationId: Guid.CreateVersion7(),
                Lines: new[]
                {
                    new SaleReturnLineInput(
                        saleItem.Id,
                        BaseQuantity: 2m,
                        Disposition: SaleReturnDisposition.RestockSellable,
                        InventoryUnitIds: Array.Empty<Guid>())
                });
        var returnResult = await returnHandler.HandleAsync(returnCommand, CancellationToken.None);

        Assert.True(returnResult.IsSuccess, returnResult.Error?.Message);

        // 3. Verify Continuous Coil Invariants (P11-IMP-065 & P11-IMP-066)
        // Invariant A: Original continuous coil in stock remains completely untouched at 43m
        Assert.Equal(43m, coilUnit.RemainingBaseQuantity);
        Assert.Equal(50m, coilUnit.InitialBaseQuantity);
        Assert.Equal(PackState.Open, coilUnit.PackState);
        Assert.False(coilUnit.IsLooseStock);

        // Invariant B: A new loose unit is created for the returned 2m segment
        var createdLooseUnits = _fakes.Inventory.Units
            .Where(u => u.Id != coilUnit.Id && u.ProductId == product.Id)
            .ToList();
        Assert.Single(createdLooseUnits);

        var looseUnit = createdLooseUnits[0];
        Assert.Equal(2m, looseUnit.InitialBaseQuantity);
        Assert.Equal(2m, looseUnit.RemainingBaseQuantity);
        Assert.Equal(PackState.Open, looseUnit.PackState);
        Assert.True(looseUnit.IsLooseStock);
        Assert.Equal(InventoryUnitStatus.InStock, looseUnit.Status);

        // Invariant C: PackAllocation recorded with OperationType == ReturnRestock
        var restockAllocations = _fakes.Inventory.PackAllocations
            .Where(a => a.OperationType == PackAllocationOperationTypes.ReturnRestock)
            .ToList();
        Assert.Single(restockAllocations);
        var alloc = restockAllocations[0];
        Assert.Equal(looseUnit.Id, alloc.PackUnitId);
        Assert.Equal(2m, alloc.AllocatedBaseQuantity);
        Assert.Equal(decimal.Round(returnedCost / 2m, 6, MidpointRounding.AwayFromZero), alloc.UnitCostSnapshot);
        Assert.Equal(returnedCost, alloc.TotalCostSnapshot);
        Assert.Equal(returnedCost, looseUnit.AcquisitionCost);
        Assert.Equal(initialCarryingCost + returnedCost, costState.TotalInventoryCost);

        // Invariant D: Stock balance restored by 2m
        Assert.Equal(45m, stock.SellableQty);

        var returnedSource = Assert.Single(_fakes.Sales.ReturnSourceAllocations);
        Assert.Equal(sourceConsumptionId, returnedSource.SaleConsumptionId);
        Assert.Equal(2m, returnedSource.BaseQuantity);
        var restoredLot = Assert.Single(_fakes.Inventory.Lots, x => x.Id == looseUnit.InventoryLotId);
        Assert.Equal(lot.PurchaseItemId, restoredLot.PurchaseItemId);
        Assert.Equal(restoredLot.PurchaseItemId, looseUnit.SourcePurchaseItemId);
        if (verifyFutureSale)
        {
            product.DefaultSalePrice = 100m;
            var command = new CompleteSaleCommand(Guid.NewGuid(), customer.Id, cashierId, null, 0m,
                SalePaymentMethod.Bank, 200m, "PRECISION-ROUNDTRIP", null,
                [new CompleteSaleLineInput(product.Id, pu.Id, 2m, 100m, [looseUnit.Id])]);
            var sold = await CreateSaleHandler().HandleAsync(command, default);
            Assert.True(sold.IsSuccess, sold.Error?.Message);
            Assert.Equal(initialCarryingCost, costState.TotalInventoryCost);
            Assert.Equal(0m, looseUnit.RemainingBaseQuantity);
            var replay = await CreateSaleHandler().HandleAsync(command, default);
            Assert.True(replay.IsSuccess, replay.Error?.Message);
            var returnReplay = await returnHandler.HandleAsync(returnCommand, default);
            Assert.True(returnReplay.IsSuccess, returnReplay.Error?.Message);
            Assert.Equal(initialCarryingCost, costState.TotalInventoryCost);
            Assert.Equal(2, _fakes.Inventory.Units.Count(x => x.ProductId == product.Id));
        }
    }

    [Fact]
    public async Task SaleReturn_FullPackReturn_RestoresOriginalUnitToSealed()
    {
        // 1. Setup Cable Product (50m reel)
        var (supplier, product, pu, sp) = SeedCableProduct();
        var customer = SeedCustomer();
        var cashierId = Guid.CreateVersion7();
        var session = OpenCashSession(cashierId);

        var stock = new StockBalance { ProductId = product.Id, SellableQty = 0m };
        _fakes.Inventory.Balances[product.Id] = stock;
        var costState = new ProductCostState
        {
            ProductId = product.Id,
            CostedQty = 0m,
            TotalInventoryCost = 0m,
            MovingAverageCost = 10m
        };
        _fakes.Inventory.CostStates[product.Id] = costState;

        // Seed 50m continuous coil unit in Sold state
        var coilUnit = new InventoryUnit
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            SupplierProductId = sp.Id,
            SourcePurchaseItemId = Guid.CreateVersion7(),
            Status = InventoryUnitStatus.Sold,
            TrackingCode = "CAB-PKF-CAT6-000002",
            ItemSequence = 2,
            InitialBaseQuantity = 50m,
            RemainingBaseQuantity = null,
            PackState = PackState.Consumed,
            IsLooseStock = false
        };
        _fakes.Inventory.Units.Add(coilUnit);

        var lot = new InventoryLot
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            PurchaseItemId = coilUnit.SourcePurchaseItemId,
            SourceMovementId = Guid.CreateVersion7(),
            ReceivedQuantity = 50m,
            OriginalUnitCost = 10m,
            EffectiveUnitCost = 10m,
            CreatedAt = _fakes.Clock.UtcNow
        };
        _fakes.Inventory.Lots.Add(lot);

        var sale = new Sale
        {
            Id = Guid.CreateVersion7(),
            InvoiceNumber = "INV-0002",
            CustomerId = customer.Id,
            CashierUserId = cashierId,
            Subtotal = 750m,
            GrandTotal = 750m,
            Status = SaleStatus.Completed,
            CompletedAt = _fakes.Clock.UtcNow
        };
        _fakes.Sales.AddSale(sale);

        var saleItem = new SaleItem
        {
            Id = Guid.CreateVersion7(),
            SaleId = sale.Id,
            ProductId = product.Id,
            ProductUnitId = pu.Id,
            FactorToBaseSnapshot = 1m,
            EnteredQuantity = 50m,
            BaseQuantity = 50m,
            UnitPrice = 15m,
            GrossLineTotal = 750m,
            NetLineTotal = 750m,
            UnitCostSnapshot = 10m,
            TotalCostSnapshot = 500m
        };
        _fakes.Sales.AddSaleItem(saleItem);

        var saleItemUnit = new SaleItemUnit
        {
            SaleItemId = saleItem.Id,
            InventoryUnitId = coilUnit.Id,
            UnitCostSnapshot = 10m
        };
        _fakes.Sales.AddSaleItemUnit(saleItemUnit);

        // 2. Return the entire 50m sealed reel
        var returnHandler = CreateSaleReturnHandler();
        var returnResult = await returnHandler.HandleAsync(
            new CreateSaleReturnCommand(
                sale.Id,
                ReasonCode: "RETURN_FULL_REEL",
                ReasonNote: "Customer returned entire sealed reel",
                RefundMethod: RefundMethod.Cash,
                CreatedBy: cashierId,
                ClientOperationId: Guid.CreateVersion7(),
                Lines: new[]
                {
                    new SaleReturnLineInput(
                        saleItem.Id,
                        BaseQuantity: 50m,
                        Disposition: SaleReturnDisposition.RestockSellable,
                        InventoryUnitIds: new[] { coilUnit.Id })
                }),
            CancellationToken.None);

        Assert.True(returnResult.IsSuccess, returnResult.Error?.Message);

        // 3. Verify Full Pack Invariants (P11-IMP-067 & P11-IMP-068)
        // Original unit restored to InStock, Sealed, full capacity
        Assert.Equal(InventoryUnitStatus.InStock, coilUnit.Status);
        Assert.Equal(50m, coilUnit.RemainingBaseQuantity);
        Assert.Equal(50m, coilUnit.InitialBaseQuantity);
        Assert.Equal(PackState.Sealed, coilUnit.PackState);
        Assert.False(coilUnit.IsLooseStock);

        // Stock balance restored
        Assert.Equal(50m, stock.SellableQty);

        // Pack allocation ReturnRestock recorded
        var restockAlloc = _fakes.Inventory.PackAllocations
            .Single(a => a.OperationType == PackAllocationOperationTypes.ReturnRestock);
        Assert.Equal(coilUnit.Id, restockAlloc.PackUnitId);
        Assert.Equal(50m, restockAlloc.AllocatedBaseQuantity);
        Assert.Equal(PackState.Sealed, restockAlloc.PackStateAfter);
    }

    [Fact]
    public async Task CommercialExchange_ContinuousCoilReturnLeg_CreatesSeparateLooseUnit_AndLeavesOriginalCoilUntouched()
    {
        // 1. Setup Cable Product & Replacement Product
        var (supplier, cableProduct, cablePu, sp) = SeedCableProduct();
        var customer = SeedCustomer();
        var cashierId = Guid.CreateVersion7();
        var session = OpenCashSession(cashierId);

        // Replacement product (standard piece)
        var pieceUnit = new Unit { Id = Guid.CreateVersion7(), Name = "Piece", Symbol = "pc" };
        var toolProduct = new Product
        {
            Id = Guid.CreateVersion7(),
            Name = "Crimping Tool",
            Sku = "TOOL-CRIMP",
            BaseUnitId = pieceUnit.Id,
            TrackingMode = TrackingMode.Quantity,
            SerialTrackingEnabled = false,
            ImeiTrackingEnabled = false,
            DefaultSalePrice = 30m,
            ReferencePurchaseCost = 15m,
            IsActive = true
        };
        _fakes.Catalog.Products[toolProduct.Id] = toolProduct;

        var toolPu = new ProductUnit
        {
            Id = Guid.CreateVersion7(),
            ProductId = toolProduct.Id,
            UnitId = pieceUnit.Id,
            FactorToBaseUnit = 1m,
            IsDefaultPurchaseUnit = true,
            IsDefaultSaleUnit = true,
            CanPurchase = true,
            CanSell = true
        };
        _fakes.Catalog.AddProductUnit(toolPu);

        // Seed stock & lots for cable and tool
        var cableStock = new StockBalance { ProductId = cableProduct.Id, SellableQty = 43m };
        _fakes.Inventory.Balances[cableProduct.Id] = cableStock;
        var cableCostState = new ProductCostState
        {
            ProductId = cableProduct.Id,
            CostedQty = 43m,
            TotalInventoryCost = 430m,
            MovingAverageCost = 10m
        };
        _fakes.Inventory.CostStates[cableProduct.Id] = cableCostState;

        var toolStock = new StockBalance { ProductId = toolProduct.Id, SellableQty = 10m };
        _fakes.Inventory.Balances[toolProduct.Id] = toolStock;
        var toolCostState = new ProductCostState
        {
            ProductId = toolProduct.Id,
            CostedQty = 10m,
            TotalInventoryCost = 150m,
            MovingAverageCost = 15m
        };
        _fakes.Inventory.CostStates[toolProduct.Id] = toolCostState;

        var toolLot = new InventoryLot
        {
            Id = Guid.CreateVersion7(),
            ProductId = toolProduct.Id,
            SourceMovementId = Guid.CreateVersion7(),
            ReceivedQuantity = 10m,
            OriginalUnitCost = 15m,
            EffectiveUnitCost = 15m,
            CreatedAt = _fakes.Clock.UtcNow
        };
        _fakes.Inventory.Lots.Add(toolLot);
        _fakes.Inventory.LotBucketBalances.Add(new InventoryLotBucketBalance
        {
            Id = Guid.CreateVersion7(),
            LotId = toolLot.Id,
            StockBucket = InventoryBucket.Sellable,
            Quantity = 10m
        });

        // Seed continuous coil unit in stock at 43m
        var coilUnit = new InventoryUnit
        {
            Id = Guid.CreateVersion7(),
            ProductId = cableProduct.Id,
            SupplierProductId = sp.Id,
            SourcePurchaseItemId = Guid.CreateVersion7(),
            Status = InventoryUnitStatus.InStock,
            TrackingCode = "CAB-PKF-CAT6-000003",
            ItemSequence = 3,
            InitialBaseQuantity = 50m,
            RemainingBaseQuantity = 43m,
            PackState = PackState.Open,
            IsLooseStock = false
        };
        _fakes.Inventory.Units.Add(coilUnit);

        var cableLot = new InventoryLot
        {
            Id = Guid.CreateVersion7(),
            ProductId = cableProduct.Id,
            PurchaseItemId = coilUnit.SourcePurchaseItemId,
            SourceMovementId = Guid.CreateVersion7(),
            ReceivedQuantity = 50m,
            OriginalUnitCost = 10m,
            EffectiveUnitCost = 10m,
            CreatedAt = _fakes.Clock.UtcNow
        };
        coilUnit.InventoryLotId = cableLot.Id;
        _fakes.Inventory.Lots.Add(cableLot);
        _fakes.Inventory.LotBucketBalances.Add(new InventoryLotBucketBalance
        {
            Id = Guid.CreateVersion7(),
            LotId = cableLot.Id,
            StockBucket = InventoryBucket.Sellable,
            Quantity = 43m
        });

        // Original sale of 7m cable
        var sale = new Sale
        {
            Id = Guid.CreateVersion7(),
            InvoiceNumber = "INV-0003",
            CustomerId = customer.Id,
            CashierUserId = cashierId,
            Subtotal = 105m,
            GrandTotal = 105m,
            Status = SaleStatus.Completed,
            CompletedAt = _fakes.Clock.UtcNow
        };
        _fakes.Sales.AddSale(sale);

        var saleItem = new SaleItem
        {
            Id = Guid.CreateVersion7(),
            SaleId = sale.Id,
            ProductId = cableProduct.Id,
            ProductUnitId = cablePu.Id,
            FactorToBaseSnapshot = 1m,
            EnteredQuantity = 7m,
            BaseQuantity = 7m,
            UnitPrice = 15m,
            GrossLineTotal = 105m,
            NetLineTotal = 105m,
            UnitCostSnapshot = 10m,
            TotalCostSnapshot = 70m
        };
        _fakes.Sales.AddSaleItem(saleItem);

        var saleItemUnit = new SaleItemUnit
        {
            SaleItemId = saleItem.Id,
            InventoryUnitId = coilUnit.Id,
            UnitCostSnapshot = 10m
        };
        _fakes.Sales.AddSaleItemUnit(saleItemUnit);
        _fakes.Sales.SoldSourceCapacities.Add(new SoldSourceCapacity(
            Guid.CreateVersion7(), cableLot.Id, cableLot.PurchaseItemId, supplier.Id, 7m, 7m));

        // 2. Perform Commercial Exchange:
        // Return 2m of cable (refund = 30m)
        // Buy 1 Crimping Tool (total = 30m)
        // Net Difference = 0m
        var exchangeHandler = CreateCommercialExchangeHandler();
        var exchangeResult = await exchangeHandler.HandleAsync(
            new CommercialExchangeCommand(
                ClientOperationId: Guid.CreateVersion7(),
                OriginalSaleId: sale.Id,
                CashierUserId: cashierId,
                SessionId: session.Id,
                CustomerId: customer.Id,
                ReturnReasonCode: "EXCHANGE",
                ReturnReasonNote: "Exchanging 2m surplus cable for crimping tool",
                ReturnLines: new[]
                {
                    new SaleReturnLineInput(
                        saleItem.Id,
                        BaseQuantity: 2m,
                        Disposition: SaleReturnDisposition.RestockSellable,
                        InventoryUnitIds: Array.Empty<Guid>())
                },
                ReplacementLines: new[]
                {
                    new CompleteSaleLineInput(
                        toolProduct.Id,
                        toolPu.Id,
                        EnteredQuantity: 1m,
                        ExpectedUnitPrice: 30m,
                        InventoryUnitIds: Array.Empty<Guid>())
                },
                InvoiceDiscount: 0m,
                SettlementMethod: SalePaymentMethod.Cash,
                AmountTendered: 0m,
                PaymentReference: null),
            CancellationToken.None);

        Assert.True(exchangeResult.IsSuccess, exchangeResult.Error?.Message);
        Assert.Equal(30m, exchangeResult.Value!.ReturnRefundAmount);
        Assert.Equal(30m, exchangeResult.Value!.ReplacementGrandTotal);
        Assert.Equal(0m, exchangeResult.Value!.NetDifference);

        // 3. Verify Continuous Coil Invariants in Commercial Exchange (P11-IMP-069)
        // Invariant A: Original coil unit remains untouched at 43m
        Assert.Equal(43m, coilUnit.RemainingBaseQuantity);
        Assert.Equal(PackState.Open, coilUnit.PackState);
        Assert.False(coilUnit.IsLooseStock);

        // Invariant B: New loose unit of 2m created
        var createdLooseUnits = _fakes.Inventory.Units
            .Where(u => u.Id != coilUnit.Id && u.ProductId == cableProduct.Id)
            .ToList();
        Assert.Single(createdLooseUnits);

        var looseUnit = createdLooseUnits[0];
        Assert.Equal(2m, looseUnit.InitialBaseQuantity);
        Assert.Equal(2m, looseUnit.RemainingBaseQuantity);
        Assert.Equal(PackState.Open, looseUnit.PackState);
        Assert.True(looseUnit.IsLooseStock);

        // Invariant C: ReturnRestock audit allocation exists
        var restockAlloc = _fakes.Inventory.PackAllocations
            .Single(a => a.OperationType == PackAllocationOperationTypes.ReturnRestock);
        Assert.Equal(looseUnit.Id, restockAlloc.PackUnitId);
        Assert.Equal(2m, restockAlloc.AllocatedBaseQuantity);

        // Invariant D: Cable stock increased by 2m, Tool stock decreased by 1m
        Assert.Equal(45m, cableStock.SellableQty);
        Assert.Equal(9m, toolStock.SellableQty);
    }

    [Fact]
    public async Task SaleReturn_WithActiveWarrantyClaim_FailsWithActiveWarrantyError()
    {
        // 1. Setup Cable Product
        var (supplier, product, pu, sp) = SeedCableProduct();
        var customer = SeedCustomer();
        var cashierId = Guid.CreateVersion7();
        var session = OpenCashSession(cashierId);

        var stock = new StockBalance { ProductId = product.Id, SellableQty = 43m };
        _fakes.Inventory.Balances[product.Id] = stock;
        var costState = new ProductCostState
        {
            ProductId = product.Id,
            CostedQty = 43m,
            TotalInventoryCost = 430m,
            MovingAverageCost = 10m
        };
        _fakes.Inventory.CostStates[product.Id] = costState;

        var coilUnit = new InventoryUnit
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            SupplierProductId = sp.Id,
            SourcePurchaseItemId = Guid.CreateVersion7(),
            Status = InventoryUnitStatus.InStock,
            TrackingCode = "CAB-PKF-CAT6-000004",
            ItemSequence = 4,
            InitialBaseQuantity = 50m,
            RemainingBaseQuantity = 43m,
            PackState = PackState.Open,
            IsLooseStock = false
        };
        _fakes.Inventory.Units.Add(coilUnit);

        var lot = new InventoryLot
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            PurchaseItemId = coilUnit.SourcePurchaseItemId,
            SourceMovementId = Guid.CreateVersion7(),
            ReceivedQuantity = 50m,
            OriginalUnitCost = 10m,
            EffectiveUnitCost = 10m,
            CreatedAt = _fakes.Clock.UtcNow
        };
        coilUnit.InventoryLotId = lot.Id;
        _fakes.Inventory.Lots.Add(lot);
        _fakes.Inventory.LotBucketBalances.Add(new InventoryLotBucketBalance
        {
            Id = Guid.CreateVersion7(),
            LotId = lot.Id,
            StockBucket = InventoryBucket.Sellable,
            Quantity = 43m
        });

        var sale = new Sale
        {
            Id = Guid.CreateVersion7(),
            InvoiceNumber = "INV-0004",
            CustomerId = customer.Id,
            CashierUserId = cashierId,
            Subtotal = 105m,
            GrandTotal = 105m,
            Status = SaleStatus.Completed,
            CompletedAt = _fakes.Clock.UtcNow
        };
        _fakes.Sales.AddSale(sale);

        var saleItem = new SaleItem
        {
            Id = Guid.CreateVersion7(),
            SaleId = sale.Id,
            ProductId = product.Id,
            ProductUnitId = pu.Id,
            FactorToBaseSnapshot = 1m,
            EnteredQuantity = 7m,
            BaseQuantity = 7m,
            UnitPrice = 15m,
            GrossLineTotal = 105m,
            NetLineTotal = 105m,
            UnitCostSnapshot = 10m,
            TotalCostSnapshot = 70m
        };
        _fakes.Sales.AddSaleItem(saleItem);

        var saleItemUnit = new SaleItemUnit
        {
            SaleItemId = saleItem.Id,
            InventoryUnitId = coilUnit.Id,
            UnitCostSnapshot = 10m
        };
        _fakes.Sales.AddSaleItemUnit(saleItemUnit);
        // Record active warranty claim on 6m of the 7m sale item
        var claim = new WarrantyClaim
        {
            Id = Guid.CreateVersion7(),
            Status = WarrantyClaimStatus.Received,
            CustomerId = customer.Id,
            CreatedAt = _fakes.Clock.UtcNow
        };
        _fakes.Warranty.Claims[claim.Id] = claim;
        _fakes.Warranty.ClaimItems.Add(new WarrantyClaimItem
        {
            Id = Guid.CreateVersion7(),
            ClaimId = claim.Id,
            OriginalSaleItemId = saleItem.Id,
            Quantity = 6m
        });

        // Attempting to return 2m exceeds remaining available (7 - 6 = 1m available)
        var returnHandler = CreateSaleReturnHandler();
        var result = await returnHandler.HandleAsync(
            new CreateSaleReturnCommand(
                sale.Id,
                ReasonCode: "SURPLUS",
                ReasonNote: "Return 2m",
                RefundMethod: RefundMethod.Cash,
                CreatedBy: cashierId,
                ClientOperationId: Guid.CreateVersion7(),
                Lines: new[]
                {
                    new SaleReturnLineInput(
                        saleItem.Id,
                        BaseQuantity: 2m,
                        Disposition: SaleReturnDisposition.RestockSellable,
                        InventoryUnitIds: Array.Empty<Guid>())
                }),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("sales.return_unit_active_warranty", result.Error?.Code);
    }

    [Fact]
    public async Task CommercialExchange_WithActiveWarrantyClaim_FailsWithActiveWarrantyError()
    {
        // 1. Setup Cable Product & Replacement Product
        var (supplier, cableProduct, cablePu, sp) = SeedCableProduct();
        var customer = SeedCustomer();
        var cashierId = Guid.CreateVersion7();
        var session = OpenCashSession(cashierId);

        var pieceUnit = new Unit { Id = Guid.CreateVersion7(), Name = "Piece", Symbol = "pc" };
        var toolProduct = new Product
        {
            Id = Guid.CreateVersion7(),
            Name = "Crimping Tool",
            Sku = "TOOL-CRIMP",
            BaseUnitId = pieceUnit.Id,
            TrackingMode = TrackingMode.Quantity,
            DefaultSalePrice = 30m,
            ReferencePurchaseCost = 15m,
            IsActive = true
        };
        _fakes.Catalog.Products[toolProduct.Id] = toolProduct;

        var toolPu = new ProductUnit
        {
            Id = Guid.CreateVersion7(),
            ProductId = toolProduct.Id,
            UnitId = pieceUnit.Id,
            FactorToBaseUnit = 1m,
            CanSell = true
        };
        _fakes.Catalog.AddProductUnit(toolPu);

        var cableStock = new StockBalance { ProductId = cableProduct.Id, SellableQty = 43m };
        _fakes.Inventory.Balances[cableProduct.Id] = cableStock;
        var cableCostState = new ProductCostState
        {
            ProductId = cableProduct.Id,
            CostedQty = 43m,
            TotalInventoryCost = 430m,
            MovingAverageCost = 10m
        };
        _fakes.Inventory.CostStates[cableProduct.Id] = cableCostState;

        var coilUnit = new InventoryUnit
        {
            Id = Guid.CreateVersion7(),
            ProductId = cableProduct.Id,
            SupplierProductId = sp.Id,
            SourcePurchaseItemId = Guid.CreateVersion7(),
            Status = InventoryUnitStatus.InStock,
            TrackingCode = "CAB-PKF-CAT6-000005",
            ItemSequence = 5,
            InitialBaseQuantity = 50m,
            RemainingBaseQuantity = 43m,
            PackState = PackState.Open,
            IsLooseStock = false
        };
        _fakes.Inventory.Units.Add(coilUnit);

        var sale = new Sale
        {
            Id = Guid.CreateVersion7(),
            InvoiceNumber = "INV-0005",
            CustomerId = customer.Id,
            CashierUserId = cashierId,
            Subtotal = 105m,
            GrandTotal = 105m,
            Status = SaleStatus.Completed,
            CompletedAt = _fakes.Clock.UtcNow
        };
        _fakes.Sales.AddSale(sale);

        var saleItem = new SaleItem
        {
            Id = Guid.CreateVersion7(),
            SaleId = sale.Id,
            ProductId = cableProduct.Id,
            ProductUnitId = cablePu.Id,
            FactorToBaseSnapshot = 1m,
            EnteredQuantity = 7m,
            BaseQuantity = 7m,
            UnitPrice = 15m,
            GrossLineTotal = 105m,
            NetLineTotal = 105m,
            UnitCostSnapshot = 10m,
            TotalCostSnapshot = 70m
        };
        _fakes.Sales.AddSaleItem(saleItem);

        var saleItemUnit = new SaleItemUnit
        {
            SaleItemId = saleItem.Id,
            InventoryUnitId = coilUnit.Id,
            UnitCostSnapshot = 10m
        };
        _fakes.Sales.AddSaleItemUnit(saleItemUnit);

        // Active warranty claim on 6m
        var claim = new WarrantyClaim
        {
            Id = Guid.CreateVersion7(),
            Status = WarrantyClaimStatus.Received,
            CustomerId = customer.Id,
            CreatedAt = _fakes.Clock.UtcNow
        };
        _fakes.Warranty.Claims[claim.Id] = claim;
        _fakes.Warranty.ClaimItems.Add(new WarrantyClaimItem
        {
            Id = Guid.CreateVersion7(),
            ClaimId = claim.Id,
            OriginalSaleItemId = saleItem.Id,
            Quantity = 6m
        });

        // Exchange return leg should fail with identical rule (P11-IMP-063 & P11-IMP-064)
        var exchangeHandler = CreateCommercialExchangeHandler();
        var result = await exchangeHandler.HandleAsync(
            new CommercialExchangeCommand(
                ClientOperationId: Guid.CreateVersion7(),
                OriginalSaleId: sale.Id,
                CashierUserId: cashierId,
                SessionId: session.Id,
                CustomerId: customer.Id,
                ReturnReasonCode: "EXCHANGE",
                ReturnReasonNote: "Exchange blocked by warranty",
                ReturnLines: new[]
                {
                    new SaleReturnLineInput(
                        saleItem.Id,
                        BaseQuantity: 2m,
                        Disposition: SaleReturnDisposition.RestockSellable,
                        InventoryUnitIds: Array.Empty<Guid>())
                },
                ReplacementLines: new[]
                {
                    new CompleteSaleLineInput(
                        toolProduct.Id,
                        toolPu.Id,
                        EnteredQuantity: 1m,
                        ExpectedUnitPrice: 30m,
                        InventoryUnitIds: Array.Empty<Guid>())
                },
                InvoiceDiscount: 0m,
                SettlementMethod: SalePaymentMethod.Cash,
                AmountTendered: 0m,
                PaymentReference: null),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("sales.return_unit_active_warranty", result.Error?.Code);
    }

    [Fact]
    public async Task SaleReturn_LooseRemnant_DamagedDisposition_CreatesDamagedUnitAndRestoresToDamagedBucket()
    {
        // 1. Setup Cable Product
        var (supplier, product, pu, sp) = SeedCableProduct();
        var customer = SeedCustomer();
        var cashierId = Guid.CreateVersion7();
        var session = OpenCashSession(cashierId);

        var stock = new StockBalance { ProductId = product.Id, SellableQty = 43m, DamagedQty = 0m };
        _fakes.Inventory.Balances[product.Id] = stock;
        var costState = new ProductCostState
        {
            ProductId = product.Id,
            CostedQty = 43m,
            TotalInventoryCost = 430m,
            MovingAverageCost = 10m
        };
        _fakes.Inventory.CostStates[product.Id] = costState;

        var coilUnit = new InventoryUnit
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            SupplierProductId = sp.Id,
            SourcePurchaseItemId = Guid.CreateVersion7(),
            Status = InventoryUnitStatus.InStock,
            TrackingCode = "CAB-PKF-CAT6-000006",
            ItemSequence = 6,
            InitialBaseQuantity = 50m,
            RemainingBaseQuantity = 43m,
            PackState = PackState.Open,
            IsLooseStock = false
        };
        _fakes.Inventory.Units.Add(coilUnit);

        var lot = new InventoryLot
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            PurchaseItemId = coilUnit.SourcePurchaseItemId,
            SourceMovementId = Guid.CreateVersion7(),
            ReceivedQuantity = 50m,
            OriginalUnitCost = 10m,
            EffectiveUnitCost = 10m,
            CreatedAt = _fakes.Clock.UtcNow
        };
        coilUnit.InventoryLotId = lot.Id;
        _fakes.Inventory.Lots.Add(lot);
        _fakes.Inventory.LotBucketBalances.Add(new InventoryLotBucketBalance
        {
            Id = Guid.CreateVersion7(),
            LotId = lot.Id,
            StockBucket = InventoryBucket.Sellable,
            Quantity = 43m
        });

        var sale = new Sale
        {
            Id = Guid.CreateVersion7(),
            InvoiceNumber = "INV-0006",
            CustomerId = customer.Id,
            CashierUserId = cashierId,
            Subtotal = 105m,
            GrandTotal = 105m,
            Status = SaleStatus.Completed,
            CompletedAt = _fakes.Clock.UtcNow
        };
        _fakes.Sales.AddSale(sale);

        var saleItem = new SaleItem
        {
            Id = Guid.CreateVersion7(),
            SaleId = sale.Id,
            ProductId = product.Id,
            ProductUnitId = pu.Id,
            FactorToBaseSnapshot = 1m,
            EnteredQuantity = 7m,
            BaseQuantity = 7m,
            UnitPrice = 15m,
            GrossLineTotal = 105m,
            NetLineTotal = 105m,
            UnitCostSnapshot = 10m,
            TotalCostSnapshot = 70m
        };
        _fakes.Sales.AddSaleItem(saleItem);

        var saleItemUnit = new SaleItemUnit
        {
            SaleItemId = saleItem.Id,
            InventoryUnitId = coilUnit.Id,
            UnitCostSnapshot = 10m
        };
        _fakes.Sales.AddSaleItemUnit(saleItemUnit);
        _fakes.Sales.SoldSourceCapacities.Add(new SoldSourceCapacity(
            Guid.CreateVersion7(), lot.Id, lot.PurchaseItemId, supplier.Id, 7m, 7m));

        // 2. Return 2m with Damaged disposition
        var returnHandler = CreateSaleReturnHandler();
        var returnResult = await returnHandler.HandleAsync(
            new CreateSaleReturnCommand(
                sale.Id,
                ReasonCode: "DAMAGED_RETURN",
                ReasonNote: "Cable damaged during installation",
                RefundMethod: RefundMethod.Cash,
                CreatedBy: cashierId,
                ClientOperationId: Guid.CreateVersion7(),
                Lines: new[]
                {
                    new SaleReturnLineInput(
                        saleItem.Id,
                        BaseQuantity: 2m,
                        Disposition: SaleReturnDisposition.Damaged,
                        InventoryUnitIds: Array.Empty<Guid>(),
                        ConditionInspectionResult: "Sheath torn, conductors exposed")
                }),
            CancellationToken.None);

        Assert.True(returnResult.IsSuccess, returnResult.Error?.Message);

        // 3. Verify loose unit created with Damaged status
        var createdLooseUnits = _fakes.Inventory.Units
            .Where(u => u.Id != coilUnit.Id && u.ProductId == product.Id)
            .ToList();
        Assert.Single(createdLooseUnits);

        var looseUnit = createdLooseUnits[0];
        Assert.Equal(2m, looseUnit.InitialBaseQuantity);
        Assert.Equal(2m, looseUnit.RemainingBaseQuantity);
        Assert.Equal(InventoryUnitStatus.Damaged, looseUnit.Status);
        Assert.True(looseUnit.IsLooseStock);

        // Verify damaged bucket updated
        Assert.Equal(2m, stock.DamagedQty);
        Assert.Equal(43m, stock.SellableQty);
    }

    [Fact]
    public async Task P11_IMP_062_Sale_Source_Race_And_Hostile_Selection_Tests()
    {
        // 1. Setup Cable Product (50m reel)
        var (supplier, product, pu, sp) = SeedCableProduct();
        var customer = SeedCustomer();
        var cashierId = Guid.CreateVersion7();
        var session = OpenCashSession(cashierId);

        var stock = new StockBalance { ProductId = product.Id, SellableQty = 50m };
        _fakes.Inventory.Balances[product.Id] = stock;
        var costState = new ProductCostState
        {
            ProductId = product.Id,
            CostedQty = 50m,
            TotalInventoryCost = 500m,
            MovingAverageCost = 10m
        };
        _fakes.Inventory.CostStates[product.Id] = costState;

        var coilUnit = new InventoryUnit
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            SupplierProductId = sp.Id,
            SourcePurchaseItemId = Guid.CreateVersion7(),
            Status = InventoryUnitStatus.InStock,
            TrackingCode = "CAB-PKF-CAT6-000062",
            ItemSequence = 62,
            InitialBaseQuantity = 50m,
            RemainingBaseQuantity = 50m,
            PackState = PackState.Sealed,
            IsLooseStock = false
        };
        _fakes.Inventory.Units.Add(coilUnit);

        var lot = new InventoryLot
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            PurchaseItemId = coilUnit.SourcePurchaseItemId,
            SourceMovementId = Guid.CreateVersion7(),
            ReceivedQuantity = 50m,
            OriginalUnitCost = 10m,
            EffectiveUnitCost = 10m,
            CreatedAt = _fakes.Clock.UtcNow
        };
        _fakes.Inventory.Lots.Add(lot);
        _fakes.Inventory.LotBucketBalances.Add(new InventoryLotBucketBalance
        {
            Id = Guid.CreateVersion7(),
            LotId = lot.Id,
            StockBucket = InventoryBucket.Sellable,
            Quantity = 50m
        });
        coilUnit.InventoryLotId = lot.Id;
        lot.PurchaseItemId = coilUnit.SourcePurchaseItemId;

        // Tool product for pure quantity stock verification
        var pieceUnit = new Unit { Id = Guid.CreateVersion7(), Name = "Piece", Symbol = "pc" };
        var toolProduct = new Product
        {
            Id = Guid.CreateVersion7(),
            Name = "Crimping Tool",
            Sku = "TOOL-CRIMP",
            BaseUnitId = pieceUnit.Id,
            TrackingMode = TrackingMode.Quantity,
            DefaultSalePrice = 30m,
            ReferencePurchaseCost = 15m,
            IsActive = true
        };
        _fakes.Catalog.Products[toolProduct.Id] = toolProduct;

        var toolPu = new ProductUnit
        {
            Id = Guid.CreateVersion7(),
            ProductId = toolProduct.Id,
            UnitId = pieceUnit.Id,
            FactorToBaseUnit = 1m,
            IsDefaultSaleUnit = true,
            CanSell = true
        };
        _fakes.Catalog.AddProductUnit(toolPu);

        var toolStock = new StockBalance { ProductId = toolProduct.Id, SellableQty = 5m };
        _fakes.Inventory.Balances[toolProduct.Id] = toolStock;

        var saleHandler = CreateSaleHandler();

        // 2. Insufficient stock check fails closed (requesting 10 units when only 5 in stock)
        var overCmd = new CompleteSaleCommand(
            ClientOperationId: Guid.CreateVersion7(),
            CustomerId: customer.Id,
            CashierUserId: cashierId,
            SessionId: session.Id,
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Cash,
            AmountTendered: 300m,
            PaymentReference: null,
            QuotationId: null,
            Lines: [
                new CompleteSaleLineInput(
                    toolProduct.Id,
                    toolPu.Id,
                    EnteredQuantity: 10m,
                    ExpectedUnitPrice: 30m,
                    InventoryUnitIds: [])
            ]);
        var overResult = await saleHandler.HandleAsync(overCmd, CancellationToken.None);
        Assert.False(overResult.IsSuccess);
        Assert.Equal("sales.insufficient_stock", overResult.Error?.Code);
        Assert.Equal(5m, toolStock.SellableQty);

        // 2b. Continuous cable split required check fails closed
        var cableSplitCmd = new CompleteSaleCommand(
            ClientOperationId: Guid.CreateVersion7(),
            CustomerId: customer.Id,
            CashierUserId: cashierId,
            SessionId: session.Id,
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Cash,
            AmountTendered: 900m,
            PaymentReference: null,
            QuotationId: null,
            Lines: [
                new CompleteSaleLineInput(
                    product.Id,
                    pu.Id,
                    EnteredQuantity: 60m,
                    ExpectedUnitPrice: 15m,
                    InventoryUnitIds: [],
                    AllowSplitCuts: false)
            ]);
        var cableSplitResult = await saleHandler.HandleAsync(cableSplitCmd, CancellationToken.None);
        Assert.False(cableSplitResult.IsSuccess);
        Assert.Equal("sales.cable_split_required", cableSplitResult.Error?.Code);
        Assert.Equal(50m, stock.SellableQty);

        // 3. Stale manual selection fails closed (selecting nonexistent/already sold unit)
        var staleUnitId = Guid.CreateVersion7();
        var staleCmd = new CompleteSaleCommand(
            ClientOperationId: Guid.CreateVersion7(),
            CustomerId: customer.Id,
            CashierUserId: cashierId,
            SessionId: session.Id,
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Cash,
            AmountTendered: 150m,
            PaymentReference: null,
            QuotationId: null,
            Lines: [
                new CompleteSaleLineInput(
                    product.Id,
                    pu.Id,
                    EnteredQuantity: 10m,
                    ExpectedUnitPrice: 15m,
                    InventoryUnitIds: [staleUnitId])
            ]);
        var staleResult = await saleHandler.HandleAsync(staleCmd, CancellationToken.None);
        Assert.False(staleResult.IsSuccess);
        Assert.Equal("sales.serial_not_sellable", staleResult.Error?.Code);
        Assert.Equal(50m, stock.SellableQty);

        // 4. First valid sale of 20m succeeds
        var saleOpId1 = Guid.CreateVersion7();
        var validSaleCmd1 = new CompleteSaleCommand(
            ClientOperationId: saleOpId1,
            CustomerId: customer.Id,
            CashierUserId: cashierId,
            SessionId: session.Id,
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Cash,
            AmountTendered: 300m,
            PaymentReference: null,
            QuotationId: null,
            Lines: [
                new CompleteSaleLineInput(
                    product.Id,
                    pu.Id,
                    EnteredQuantity: 20m,
                    ExpectedUnitPrice: 15m,
                    InventoryUnitIds: [coilUnit.Id])
            ]);
        var saleResult1 = await saleHandler.HandleAsync(validSaleCmd1, CancellationToken.None);
        Assert.True(saleResult1.IsSuccess, saleResult1.Error?.Message);
        Assert.False(saleResult1.Value!.WasExisting);
        Assert.Equal(30m, stock.SellableQty);
        Assert.Equal(30m, coilUnit.RemainingBaseQuantity);
        Assert.Equal(PackState.Open, coilUnit.PackState);

        // 5. Idempotent Replay of first sale: returns original sale result without double deduction
        var replayResult = await saleHandler.HandleAsync(validSaleCmd1, CancellationToken.None);
        Assert.True(replayResult.IsSuccess);
        Assert.True(replayResult.Value!.WasExisting);
        Assert.Equal(saleResult1.Value.SaleId, replayResult.Value.SaleId);
        Assert.Equal(30m, stock.SellableQty);
        Assert.Equal(30m, coilUnit.RemainingBaseQuantity);

        // 6. Overlapping Competing Sale 2 consumes remaining 30m of the coil
        var saleOpId2 = Guid.CreateVersion7();
        var validSaleCmd2 = new CompleteSaleCommand(
            ClientOperationId: saleOpId2,
            CustomerId: customer.Id,
            CashierUserId: cashierId,
            SessionId: session.Id,
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Cash,
            AmountTendered: 450m,
            PaymentReference: null,
            QuotationId: null,
            Lines: [
                new CompleteSaleLineInput(
                    product.Id,
                    pu.Id,
                    EnteredQuantity: 30m,
                    ExpectedUnitPrice: 15m,
                    InventoryUnitIds: [coilUnit.Id])
            ]);
        var saleResult2 = await saleHandler.HandleAsync(validSaleCmd2, CancellationToken.None);
        Assert.True(saleResult2.IsSuccess, saleResult2.Error?.Message);
        Assert.False(saleResult2.Value!.WasExisting);
        Assert.Equal(0m, stock.SellableQty);
        Assert.Equal(0m, coilUnit.RemainingBaseQuantity);
        Assert.Equal(PackState.Consumed, coilUnit.PackState);

        // 7. Third sale fails due to stock exhausted
        var saleCmd3 = new CompleteSaleCommand(
            ClientOperationId: Guid.CreateVersion7(),
            CustomerId: customer.Id,
            CashierUserId: cashierId,
            SessionId: session.Id,
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Cash,
            AmountTendered: 75m,
            PaymentReference: null,
            QuotationId: null,
            Lines: [
                new CompleteSaleLineInput(
                    product.Id,
                    pu.Id,
                    EnteredQuantity: 5m,
                    ExpectedUnitPrice: 15m,
                    InventoryUnitIds: [])
            ]);
        var saleResult3 = await saleHandler.HandleAsync(saleCmd3, CancellationToken.None);
        Assert.False(saleResult3.IsSuccess);
        Assert.Equal("inventory.pack_no_eligible_units", saleResult3.Error?.Code);

        // 8. Mathematical Quantity and Cost Conservation
        // Initial: 50m @ 10/m = 500m
        // Sale 1: 20m sold (COGS = 200m)
        // Sale 2: 30m sold (COGS = 300m)
        // Remaining Qty = 50 - 20 - 30 = 0m
        // Remaining Cost = 500 - 200 - 300 = 0m
        Assert.Equal(0m, stock.SellableQty);
        Assert.Equal(0m, costState.TotalInventoryCost);
    }

    [Fact]
    public async Task P11_IMP_070_Return_Exchange_Repeat_Race_And_Overreturn_Cases()
    {
        // 1. Setup Cable Product (50m reel)
        var (supplier, product, pu, sp) = SeedCableProduct();
        var customer = SeedCustomer();
        var cashierId = Guid.CreateVersion7();
        var session = OpenCashSession(cashierId);

        var stock = new StockBalance { ProductId = product.Id, SellableQty = 40m };
        _fakes.Inventory.Balances[product.Id] = stock;
        var costState = new ProductCostState
        {
            ProductId = product.Id,
            CostedQty = 40m,
            TotalInventoryCost = 400m,
            MovingAverageCost = 10m
        };
        _fakes.Inventory.CostStates[product.Id] = costState;

        var coilUnit = new InventoryUnit
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            SupplierProductId = sp.Id,
            SourcePurchaseItemId = Guid.CreateVersion7(),
            Status = InventoryUnitStatus.InStock,
            TrackingCode = "CAB-PKF-CAT6-000070",
            ItemSequence = 70,
            InitialBaseQuantity = 50m,
            RemainingBaseQuantity = 40m,
            PackState = PackState.Open,
            IsLooseStock = false
        };
        _fakes.Inventory.Units.Add(coilUnit);

        var lot = new InventoryLot
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            PurchaseItemId = coilUnit.SourcePurchaseItemId,
            SourceMovementId = Guid.CreateVersion7(),
            ReceivedQuantity = 50m,
            OriginalUnitCost = 10m,
            EffectiveUnitCost = 10m,
            CreatedAt = _fakes.Clock.UtcNow
        };
        _fakes.Inventory.Lots.Add(lot);
        _fakes.Inventory.LotBucketBalances.Add(new InventoryLotBucketBalance
        {
            Id = Guid.CreateVersion7(),
            LotId = lot.Id,
            StockBucket = InventoryBucket.Sellable,
            Quantity = 40m
        });
        coilUnit.InventoryLotId = lot.Id;

        // Sale of 10m from the coil
        var sale = new Sale
        {
            Id = Guid.CreateVersion7(),
            InvoiceNumber = "INV-0070",
            CustomerId = customer.Id,
            CashierUserId = cashierId,
            Subtotal = 150m,
            GrandTotal = 150m,
            Status = SaleStatus.Completed,
            CompletedAt = _fakes.Clock.UtcNow
        };
        _fakes.Sales.AddSale(sale);

        var saleItem = new SaleItem
        {
            Id = Guid.CreateVersion7(),
            SaleId = sale.Id,
            ProductId = product.Id,
            ProductUnitId = pu.Id,
            FactorToBaseSnapshot = 1m,
            EnteredQuantity = 10m,
            BaseQuantity = 10m,
            UnitPrice = 15m,
            GrossLineTotal = 150m,
            NetLineTotal = 150m,
            UnitCostSnapshot = 10m,
            TotalCostSnapshot = 100m
        };
        _fakes.Sales.AddSaleItem(saleItem);

        var saleItemUnit = new SaleItemUnit
        {
            SaleItemId = saleItem.Id,
            InventoryUnitId = coilUnit.Id,
            UnitCostSnapshot = 10m
        };
        _fakes.Sales.AddSaleItemUnit(saleItemUnit);
        _fakes.Sales.SoldSourceCapacities.Add(new SoldSourceCapacity(
            Guid.CreateVersion7(), lot.Id, lot.PurchaseItemId, supplier.Id, 10m, 10m));

        var returnHandler = CreateSaleReturnHandler();

        // 2. Over-return rejection: Attempting to return 15m on a 10m sale fails closed
        var overReturnCmd = new CreateSaleReturnCommand(
            sale.Id,
            ReasonCode: "SURPLUS",
            ReasonNote: "Over return attempt",
            RefundMethod: RefundMethod.Cash,
            CreatedBy: cashierId,
            ClientOperationId: Guid.CreateVersion7(),
            Lines: [
                new SaleReturnLineInput(
                    saleItem.Id,
                    BaseQuantity: 15m,
                    Disposition: SaleReturnDisposition.RestockSellable,
                    InventoryUnitIds: [])
            ]);
        var overReturnResult = await returnHandler.HandleAsync(overReturnCmd, CancellationToken.None);
        Assert.False(overReturnResult.IsSuccess);
        Assert.Equal("sales.return_exceeds_original", overReturnResult.Error?.Code);
        Assert.Equal(40m, stock.SellableQty);

        // 3. Valid First Return: Return 4m of the 10m cut
        var returnOpId1 = Guid.CreateVersion7();
        var validReturnCmd1 = new CreateSaleReturnCommand(
            sale.Id,
            ReasonCode: "SURPLUS",
            ReasonNote: "Valid 4m return",
            RefundMethod: RefundMethod.Cash,
            CreatedBy: cashierId,
            ClientOperationId: returnOpId1,
            Lines: [
                new SaleReturnLineInput(
                    saleItem.Id,
                    BaseQuantity: 4m,
                    Disposition: SaleReturnDisposition.RestockSellable,
                    InventoryUnitIds: [])
            ]);
        var returnResult1 = await returnHandler.HandleAsync(validReturnCmd1, CancellationToken.None);
        Assert.True(returnResult1.IsSuccess, returnResult1.Error?.Message);
        Assert.False(returnResult1.Value!.WasExisting);
        Assert.Equal(60m, returnResult1.Value.RefundAmount);
        Assert.Equal(44m, stock.SellableQty); // 40 + 4 = 44m

        // 4. Same-Result Replay: Submitting identical return operation returns existing result without double restock or double refund
        var replayReturnResult = await returnHandler.HandleAsync(validReturnCmd1, CancellationToken.None);
        Assert.True(replayReturnResult.IsSuccess);
        Assert.True(replayReturnResult.Value!.WasExisting);
        Assert.Equal(returnResult1.Value.SaleReturnId, replayReturnResult.Value.SaleReturnId);
        Assert.Equal(60m, replayReturnResult.Value.RefundAmount);
        Assert.Equal(44m, stock.SellableQty); // Zero double restock

        // 5. Custody conflict: Active warranty claim on 5m of remaining 6m sold quantity
        var claim = new WarrantyClaim
        {
            Id = Guid.CreateVersion7(),
            Status = WarrantyClaimStatus.Received,
            CustomerId = customer.Id,
            CreatedAt = _fakes.Clock.UtcNow
        };
        _fakes.Warranty.Claims[claim.Id] = claim;
        _fakes.Warranty.ClaimItems.Add(new WarrantyClaimItem
        {
            Id = Guid.CreateVersion7(),
            ClaimId = claim.Id,
            OriginalSaleItemId = saleItem.Id,
            Quantity = 5m
        });

        // Returning 3m conflicts with active warranty custody (10 - 4 returned = 6 remaining, 5 in warranty -> only 1m returnable)
        var custodyConflictCmd = new CreateSaleReturnCommand(
            sale.Id,
            ReasonCode: "SURPLUS",
            ReasonNote: "Custody conflict with warranty",
            RefundMethod: RefundMethod.Cash,
            CreatedBy: cashierId,
            ClientOperationId: Guid.CreateVersion7(),
            Lines: [
                new SaleReturnLineInput(
                    saleItem.Id,
                    BaseQuantity: 3m,
                    Disposition: SaleReturnDisposition.RestockSellable,
                    InventoryUnitIds: [])
            ]);
        var conflictResult = await returnHandler.HandleAsync(custodyConflictCmd, CancellationToken.None);
        Assert.False(conflictResult.IsSuccess);
        Assert.Equal("sales.return_unit_active_warranty", conflictResult.Error?.Code);
        Assert.Equal(44m, stock.SellableQty); // Stock untouched

        // 6. Excess competing return exceeding sold limit fails closed
        // Clear warranty claim to test cumulative boundary
        _fakes.Warranty.Claims.Clear();
        _fakes.Warranty.ClaimItems.Clear();

        var excessCmd = new CreateSaleReturnCommand(
            sale.Id,
            ReasonCode: "SURPLUS",
            ReasonNote: "Excess 8m return (only 6m remaining)",
            RefundMethod: RefundMethod.Cash,
            CreatedBy: cashierId,
            ClientOperationId: Guid.CreateVersion7(),
            Lines: [
                new SaleReturnLineInput(
                    saleItem.Id,
                    BaseQuantity: 8m,
                    Disposition: SaleReturnDisposition.RestockSellable,
                    InventoryUnitIds: [])
            ]);
        var excessResult = await returnHandler.HandleAsync(excessCmd, CancellationToken.None);
        Assert.False(excessResult.IsSuccess);
        Assert.Equal("sales.return_exceeds_original", excessResult.Error?.Code);
        Assert.Equal(44m, stock.SellableQty); // Zero double restock
    }

    [Fact]
    public async Task Container_loose_return_requires_condition_inspection_when_damaged()
    {
        var (supplier, product, pu, _) = SeedCableProduct();
        var customer = SeedCustomer();
        var cashierId = Guid.CreateVersion7();
        _ = OpenCashSession(cashierId);
        _fakes.Inventory.Balances[product.Id] = new StockBalance { ProductId = product.Id, SellableQty = 40m };

        var sale = new Sale
        {
            Id = Guid.CreateVersion7(),
            InvoiceNumber = "INV-DMG",
            CustomerId = customer.Id,
            CashierUserId = cashierId,
            Status = SaleStatus.Completed,
            CompletedAt = _fakes.Clock.UtcNow
        };
        _fakes.Sales.AddSale(sale);
        var saleItem = new SaleItem
        {
            Id = Guid.CreateVersion7(),
            SaleId = sale.Id,
            ProductId = product.Id,
            ProductUnitId = pu.Id,
            FactorToBaseSnapshot = 1m,
            EnteredQuantity = 5m,
            BaseQuantity = 5m,
            UnitPrice = 15m,
            GrossLineTotal = 75m,
            NetLineTotal = 75m,
            UnitCostSnapshot = 10m,
            TotalCostSnapshot = 50m
        };
        _fakes.Sales.AddSaleItem(saleItem);
        var coilUnitId = Guid.CreateVersion7();
        _fakes.Sales.AddSaleItemUnit(new SaleItemUnit
        {
            SaleItemId = saleItem.Id,
            InventoryUnitId = coilUnitId,
            UnitCostSnapshot = 10m
        });
        _fakes.Sales.SoldSourceCapacities.Add(new SoldSourceCapacity(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            supplier.Id,
            5m,
            5m));

        var input = new SaleReturnLineInput(
            saleItem.Id,
            BaseQuantity: 2m,
            Disposition: SaleReturnDisposition.Damaged,
            InventoryUnitIds: [],
            ReturnSourceContainerId: coilUnitId,
            ConditionInspectionResult: null);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            _fakes.Eligibility.PrepareReturnLineAsync(input, sale.Id, CancellationToken.None));

        Assert.Equal("sales.return_condition_inspection_required", ex.Code);
    }
}
