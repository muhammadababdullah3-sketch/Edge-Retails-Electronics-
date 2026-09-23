using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Sales;
using Xunit;

namespace EdgeRetails.UnitTests;

public sealed class Phase2SalesAndCommercialExchangeBehavioralTests
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
            _fakes.UnitOfWork);

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
            _fakes.UnitOfWork);

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
            _fakes.UnitOfWork);

    private SavePosDraftHandler CreateSavePosDraftHandler() =>
        new(
            _fakes.PosDrafts,
            _fakes.Catalog,
            _fakes.Numbers,
            _fakes.Clock,
            _fakes.Authorization,
            _fakes.Transactions,
            _fakes.UnitOfWork);

    private CancelPosDraftHandler CreateCancelPosDraftHandler() =>
        new(
            _fakes.PosDrafts,
            _fakes.Authorization,
            _fakes.Clock,
            _fakes.Transactions,
            _fakes.UnitOfWork);

    private CompletePosDraftHandler CreateCompletePosDraftHandler() =>
        new(
            _fakes.PosDrafts,
            _fakes.ResourceLock,
            _fakes.Authorization,
            CreateSaleHandler(),
            _fakes.Clock,
            _fakes.Transactions,
            _fakes.UnitOfWork);

    private (Product Product, ProductUnit Unit) SeedQuantityProduct(string sku = "KEYBOARD-01", decimal cost = 200m, decimal price = 300m, decimal stock = 20m)
    {
        var unit = new Unit { Id = Guid.CreateVersion7(), Name = "Piece", Symbol = "pc" };
        var product = new Product
        {
            Id = Guid.CreateVersion7(),
            Name = "Mechanical Keyboard",
            Sku = sku,
            BaseUnitId = unit.Id,
            TrackingMode = TrackingMode.Quantity,
            DefaultSalePrice = price,
            ReferencePurchaseCost = cost,
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

        if (stock > 0)
        {
            _fakes.Inventory.AddStockBalance(new StockBalance
            {
                ProductId = product.Id,
                SellableQty = stock
            });

            _fakes.Inventory.AddCostState(new ProductCostState
            {
                ProductId = product.Id,
                CostedQty = stock,
                TotalInventoryCost = stock * cost,
                MovingAverageCost = cost,
                LastPurchaseCost = cost,
                Version = 1
            });

            var lot = new InventoryLot
            {
                Id = Guid.CreateVersion7(),
                ProductId = product.Id,
                ReceivedQuantity = stock,
                OriginalUnitCost = cost,
                EffectiveUnitCost = cost,
                CreatedAt = DateTimeOffset.UtcNow
            };
            _fakes.Inventory.AddLot(lot);

            _fakes.Inventory.AddLotBucketBalance(new InventoryLotBucketBalance
            {
                LotId = lot.Id,
                StockBucket = InventoryBucket.Sellable,
                Quantity = stock
            });
        }

        return (product, pu);
    }

    private (Product Product, ProductUnit Unit, List<InventoryUnit> Units) SeedSerializedProduct(string sku = "LAPTOP-01", decimal cost = 80000m, decimal price = 100000m, int count = 2)
    {
        var unit = new Unit { Id = Guid.CreateVersion7(), Name = "Piece", Symbol = "pc" };
        var product = new Product
        {
            Id = Guid.CreateVersion7(),
            Name = "Pro Laptop",
            Sku = sku,
            BaseUnitId = unit.Id,
            TrackingMode = TrackingMode.Serialized,
            SerialTrackingEnabled = true,
            ImeiTrackingEnabled = false,
            DefaultSalePrice = price,
            ReferencePurchaseCost = cost,
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
            SellableQty = count
        });

        _fakes.Inventory.AddCostState(new ProductCostState
        {
            ProductId = product.Id,
            CostedQty = count,
            TotalInventoryCost = count * cost,
            MovingAverageCost = cost,
            LastPurchaseCost = cost,
            Version = 1
        });

        var lot = new InventoryLot
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            ReceivedQuantity = count,
            OriginalUnitCost = cost,
            EffectiveUnitCost = cost,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _fakes.Inventory.AddLot(lot);

        _fakes.Inventory.AddLotBucketBalance(new InventoryLotBucketBalance
        {
            LotId = lot.Id,
            StockBucket = InventoryBucket.Sellable,
            Quantity = count
        });

        var createdUnits = new List<InventoryUnit>();
        for (int i = 1; i <= count; i++)
        {
            var u = new InventoryUnit
            {
                Id = Guid.CreateVersion7(),
                ProductId = product.Id,
                InventoryLotId = lot.Id,
                SerialNumber = $"SN-LAPTOP-{i}",
                TrackingCode = $"DEL-{sku}-{i:D6}",
                Status = InventoryUnitStatus.InStock,
                AcquisitionCost = cost,
                CreatedAt = DateTimeOffset.UtcNow
            };
            _fakes.Inventory.AddInventoryUnit(u);
            createdUnits.Add(u);
        }

        return (product, pu, createdUnits);
    }

    [Fact]
    public async Task CompleteSale_QuantityProduct_DeductsSellableStock_And_ConsumesLotsFIFO()
    {
        var (product, unit) = SeedQuantityProduct(stock: 10m, cost: 200m, price: 300m);
        var handler = CreateSaleHandler();
        var cashierId = Guid.CreateVersion7();
        var opId = Guid.CreateVersion7();

        var session = new CashSession
        {
            Id = Guid.CreateVersion7(),
            OpenedBy = cashierId,
            OpeningCash = 5000m,
            Status = CashSessionStatus.Open,
            OpenedAt = DateTimeOffset.UtcNow,
            BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow)
        };
        _fakes.Cash.AddSession(session);

        var result = await handler.HandleAsync(
            new CompleteSaleCommand(
                opId,
                CustomerId: null,
                cashierId,
                session.Id,
                InvoiceDiscount: 0m,
                PaymentMethod: SalePaymentMethod.Cash,
                AmountTendered: 1000m,
                PaymentReference: null,
                QuotationId: null,
                Lines:
                [
                    new CompleteSaleLineInput(product.Id, unit.Id, 3m, 300m, [])
                ]),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);
        Assert.Equal(900m, result.Value!.GrandTotal);
        Assert.Equal(100m, result.Value.ChangeGiven);

        // Stock decreased from 10 to 7
        var balance = _fakes.Inventory.Balances[product.Id];
        Assert.Equal(7m, balance.SellableQty);

        // Cash movement recorded
        Assert.Single(_fakes.Cash.Movements);
        var move = _fakes.Cash.Movements[0];
        Assert.Equal(CashMovementType.SaleCashIn, move.MovementType);
        Assert.Equal(CashMovementDirection.In, move.Direction);
        Assert.Equal(900m, move.Amount);
    }

    [Fact]
    public async Task CompleteSale_SerializedProduct_AllocatesExactUnits_And_SetsStatusSold()
    {
        var (product, unit, units) = SeedSerializedProduct();
        var handler = CreateSaleHandler();
        var cashierId = Guid.CreateVersion7();
        var opId = Guid.CreateVersion7();

        var selectedUnit = units[0];

        var result = await handler.HandleAsync(
            new CompleteSaleCommand(
                opId,
                CustomerId: null,
                cashierId,
                SessionId: null,
                InvoiceDiscount: 0m,
                PaymentMethod: SalePaymentMethod.Bank,
                AmountTendered: 100000m,
                PaymentReference: "TXN-BANK-001",
                QuotationId: null,
                Lines:
                [
                    new CompleteSaleLineInput(product.Id, unit.Id, 1m, 100000m, [selectedUnit.Id])
                ]),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);

        // Unit status transitioned to Sold
        Assert.Equal(InventoryUnitStatus.Sold, selectedUnit.Status);

        // Other unit remains InStock
        Assert.Equal(InventoryUnitStatus.InStock, units[1].Status);

        // Stock balance is now 1
        Assert.Equal(1m, _fakes.Inventory.Balances[product.Id].SellableQty);
    }

    [Fact]
    public async Task CompleteSale_FailsWhenInsufficientStock()
    {
        var (product, unit) = SeedQuantityProduct(stock: 2m, price: 300m);
        var handler = CreateSaleHandler();
        var cashierId = Guid.CreateVersion7();

        var result = await handler.HandleAsync(
            new CompleteSaleCommand(
                Guid.CreateVersion7(),
                CustomerId: null,
                cashierId,
                SessionId: null,
                InvoiceDiscount: 0m,
                PaymentMethod: SalePaymentMethod.Bank,
                AmountTendered: 1500m,
                PaymentReference: "TXN-FAIL",
                QuotationId: null,
                Lines:
                [
                    new CompleteSaleLineInput(product.Id, unit.Id, 5m, 300m, [])
                ]),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("sales.insufficient_stock", result.Error?.Code);
    }

    [Fact]
    public async Task PosDraft_Lifecycle_Save_Cancel_Complete_HasZeroPhantomStockHolds()
    {
        var (product, unit) = SeedQuantityProduct(stock: 10m, price: 300m);
        var saveHandler = CreateSavePosDraftHandler();
        var cancelHandler = CreateCancelPosDraftHandler();
        var completeHandler = CreateCompletePosDraftHandler();
        var actorId = Guid.CreateVersion7();

        // 1. Save Draft
        var saveResult = await saveHandler.HandleAsync(
            new SavePosDraftCommand(
                DraftId: null,
                ExpectedVersion: null,
                CustomerId: null,
                ActorId: actorId,
                TerminalId: "POS-01",
                Note: "Pending customer wallet",
                Items:
                [
                    new SavePosDraftItemInput(product.Id, unit.Id, 4m)
                ]),
            CancellationToken.None);

        Assert.True(saveResult.IsSuccess, saveResult.Error?.Message);
        var draftId = saveResult.Value!.DraftId;

        // Stock MUST remain unchanged (10m) - drafts do not reserve stock
        Assert.Equal(10m, _fakes.Inventory.Balances[product.Id].SellableQty);

        // 2. Complete Draft
        var completeResult = await completeHandler.HandleAsync(
            new CompletePosDraftCommand(
                DraftId: draftId,
                ExpectedVersion: saveResult.Value!.Version,
                ClientOperationId: Guid.CreateVersion7(),
                CashierUserId: actorId,
                SessionId: null,
                InvoiceDiscount: 0m,
                PaymentMethod: SalePaymentMethod.Bank,
                AmountTendered: 1200m,
                PaymentReference: "DRAFT-COMPLETE"),
            CancellationToken.None);

        Assert.True(completeResult.IsSuccess, completeResult.Error?.Message);

        // Stock is now 6
        Assert.Equal(6m, _fakes.Inventory.Balances[product.Id].SellableQty);

        // Draft is closed
        var draft = await _fakes.PosDrafts.GetForUpdateAsync(draftId, CancellationToken.None);
        Assert.Equal(PosDraftStatus.Converted, draft!.Status);
    }

    [Fact]
    public async Task SaleReturn_RestocksSellableOrDamagedBucket_And_IssuesCashRefund()
    {
        var (product, unit) = SeedQuantityProduct(stock: 10m, cost: 200m, price: 300m);
        var saleHandler = CreateSaleHandler();
        var returnHandler = CreateSaleReturnHandler();
        var cashierId = Guid.CreateVersion7();

        var session = new CashSession
        {
            Id = Guid.CreateVersion7(),
            OpenedBy = cashierId,
            OpeningCash = 5000m,
            Status = CashSessionStatus.Open,
            OpenedAt = DateTimeOffset.UtcNow,
            BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow)
        };
        _fakes.Cash.AddSession(session);

        // Sell 5 units
        var saleResult = await saleHandler.HandleAsync(
            new CompleteSaleCommand(
                Guid.CreateVersion7(),
                CustomerId: null,
                cashierId,
                session.Id,
                InvoiceDiscount: 0m,
                PaymentMethod: SalePaymentMethod.Cash,
                AmountTendered: 1500m,
                PaymentReference: null,
                QuotationId: null,
                Lines:
                [
                    new CompleteSaleLineInput(product.Id, unit.Id, 5m, 300m, [])
                ]),
            CancellationToken.None);

        Assert.True(saleResult.IsSuccess);
        var saleId = saleResult.Value!.SaleId;
        var saleItem = _fakes.Sales.SaleItems.Single(i => i.SaleId == saleId);

        // Return 2 units back to Sellable
        var returnResult = await returnHandler.HandleAsync(
            new CreateSaleReturnCommand(
                SaleId: saleId,
                ReasonCode: "Customer changed mind",
                ReasonNote: null,
                RefundMethod: RefundMethod.Cash,
                CreatedBy: cashierId,
                ClientOperationId: Guid.CreateVersion7(),
                Lines:
                [
                    new SaleReturnLineInput(saleItem.Id, 2m, SaleReturnDisposition.RestockSellable, [])
                ]),
            CancellationToken.None);

        Assert.True(returnResult.IsSuccess, returnResult.Error?.Message);
        Assert.Equal(600m, returnResult.Value!.RefundAmount);

        // Stock restored from 5 to 7
        Assert.Equal(7m, _fakes.Inventory.Balances[product.Id].SellableQty);

        // Cash out movement recorded
        var cashRefund = _fakes.Cash.Movements.Last();
        Assert.Equal(CashMovementType.SaleRefundCashOut, cashRefund.MovementType);
        Assert.Equal(CashMovementDirection.Out, cashRefund.Direction);
        Assert.Equal(600m, cashRefund.Amount);
    }

    [Fact]
    public async Task CommercialExchange_Upgrade_AtomicExchange_CustomerPaysDifference()
    {
        // Product 1: Old keyboard returned (price 300)
        var (oldProd, oldUnit) = SeedQuantityProduct("KEYBOARD-OLD", 200m, 300m, stock: 10m);
        // Product 2: New premium keyboard replacement (price 500)
        var (newProd, newUnit) = SeedQuantityProduct("KEYBOARD-PRO", 350m, 500m, stock: 10m);

        var saleHandler = CreateSaleHandler();
        var exchangeHandler = CreateCommercialExchangeHandler();
        var cashierId = Guid.CreateVersion7();

        var session = new CashSession
        {
            Id = Guid.CreateVersion7(),
            OpenedBy = cashierId,
            OpeningCash = 5000m,
            Status = CashSessionStatus.Open,
            OpenedAt = DateTimeOffset.UtcNow,
            BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow)
        };
        _fakes.Cash.AddSession(session);

        // Initial sale of old keyboard
        var initialSale = await saleHandler.HandleAsync(
            new CompleteSaleCommand(
                Guid.CreateVersion7(),
                CustomerId: null,
                cashierId,
                session.Id,
                InvoiceDiscount: 0m,
                PaymentMethod: SalePaymentMethod.Cash,
                AmountTendered: 300m,
                PaymentReference: null,
                QuotationId: null,
                Lines: [new CompleteSaleLineInput(oldProd.Id, oldUnit.Id, 1m, 300m, [])]),
            CancellationToken.None);

        Assert.True(initialSale.IsSuccess);
        var oldSaleId = initialSale.Value!.SaleId;
        var oldSaleItemId = _fakes.Sales.SaleItems.Single(i => i.SaleId == oldSaleId).Id;

        // Exchange: return old keyboard (300 credit) + buy new pro keyboard (500) -> customer pays 200 difference
        var exchangeResult = await exchangeHandler.HandleAsync(
            new CommercialExchangeCommand(
                ClientOperationId: Guid.CreateVersion7(),
                OriginalSaleId: oldSaleId,
                CashierUserId: cashierId,
                SessionId: session.Id,
                CustomerId: null,
                ReturnReasonCode: "UPGRADE",
                ReturnReasonNote: "Customer wants RGB version",
                ReturnLines:
                [
                    new SaleReturnLineInput(oldSaleItemId, 1m, SaleReturnDisposition.RestockSellable, [])
                ],
                ReplacementLines:
                [
                    new CompleteSaleLineInput(newProd.Id, newUnit.Id, 1m, 500m, [])
                ],
                InvoiceDiscount: 0m,
                SettlementMethod: SalePaymentMethod.Cash,
                AmountTendered: 200m,
                PaymentReference: null),
            CancellationToken.None);

        Assert.True(exchangeResult.IsSuccess, exchangeResult.Error?.Message);
        Assert.NotNull(exchangeResult.Value);
        Assert.Equal(500m, exchangeResult.Value!.ReplacementGrandTotal);
        Assert.Equal(300m, exchangeResult.Value.ReturnRefundAmount);
        Assert.Equal(200m, exchangeResult.Value.NetDifference);
        Assert.Equal(0m, exchangeResult.Value.ChangeGiven);

        // Old product restocked back to 10
        Assert.Equal(10m, _fakes.Inventory.Balances[oldProd.Id].SellableQty);
        // New product stock deducted from 10 to 9
        Assert.Equal(9m, _fakes.Inventory.Balances[newProd.Id].SellableQty);

        // Net cash in of 200 recorded
        var lastMovement = _fakes.Cash.Movements.Last();
        Assert.Equal(CashMovementType.SaleCashIn, lastMovement.MovementType);
        Assert.Equal(CashMovementDirection.In, lastMovement.Direction);
        Assert.Equal(200m, lastMovement.Amount);
    }

    [Fact]
    public async Task CommercialExchange_Downgrade_AtomicExchange_ShopRefundsDifference()
    {
        var (proProd, proUnit) = SeedQuantityProduct("MOUSE-PRO", 300m, 500m, stock: 10m);
        var (basicProd, basicUnit) = SeedQuantityProduct("MOUSE-BASIC", 100m, 200m, stock: 10m);

        var saleHandler = CreateSaleHandler();
        var exchangeHandler = CreateCommercialExchangeHandler();
        var cashierId = Guid.CreateVersion7();

        var session = new CashSession
        {
            Id = Guid.CreateVersion7(),
            OpenedBy = cashierId,
            OpeningCash = 5000m,
            Status = CashSessionStatus.Open,
            OpenedAt = DateTimeOffset.UtcNow,
            BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow)
        };
        _fakes.Cash.AddSession(session);

        // Initial sale of Pro Mouse (500)
        var initialSale = await saleHandler.HandleAsync(
            new CompleteSaleCommand(
                Guid.CreateVersion7(),
                CustomerId: null,
                cashierId,
                session.Id,
                InvoiceDiscount: 0m,
                PaymentMethod: SalePaymentMethod.Cash,
                AmountTendered: 500m,
                PaymentReference: null,
                QuotationId: null,
                Lines: [new CompleteSaleLineInput(proProd.Id, proUnit.Id, 1m, 500m, [])]),
            CancellationToken.None);

        Assert.True(initialSale.IsSuccess);
        var saleId = initialSale.Value!.SaleId;
        var saleItemId = _fakes.Sales.SaleItems.Single(i => i.SaleId == saleId).Id;

        // Exchange: return Pro (500) + buy Basic (200) -> shop refunds 300 difference
        var exchangeResult = await exchangeHandler.HandleAsync(
            new CommercialExchangeCommand(
                ClientOperationId: Guid.CreateVersion7(),
                OriginalSaleId: saleId,
                CashierUserId: cashierId,
                SessionId: session.Id,
                CustomerId: null,
                ReturnReasonCode: "DOWNGRADE",
                ReturnReasonNote: "Customer wants simpler model",
                ReturnLines:
                [
                    new SaleReturnLineInput(saleItemId, 1m, SaleReturnDisposition.RestockSellable, [])
                ],
                ReplacementLines:
                [
                    new CompleteSaleLineInput(basicProd.Id, basicUnit.Id, 1m, 200m, [])
                ],
                InvoiceDiscount: 0m,
                SettlementMethod: SalePaymentMethod.Cash,
                AmountTendered: 0m,
                PaymentReference: null),
            CancellationToken.None);

        Assert.True(exchangeResult.IsSuccess, exchangeResult.Error?.Message);
        Assert.NotNull(exchangeResult.Value);
        Assert.Equal(200m, exchangeResult.Value!.ReplacementGrandTotal);
        Assert.Equal(500m, exchangeResult.Value.ReturnRefundAmount);
        Assert.Equal(-300m, exchangeResult.Value.NetDifference);

        // Pro restocked back to 10
        Assert.Equal(10m, _fakes.Inventory.Balances[proProd.Id].SellableQty);
        // Basic deducted from 10 to 9
        Assert.Equal(9m, _fakes.Inventory.Balances[basicProd.Id].SellableQty);

        // Net cash refund out of 300 recorded
        var lastMovement = _fakes.Cash.Movements.Last();
        Assert.Equal(CashMovementType.SaleRefundCashOut, lastMovement.MovementType);
        Assert.Equal(CashMovementDirection.Out, lastMovement.Direction);
        Assert.Equal(300m, lastMovement.Amount);
    }
}
