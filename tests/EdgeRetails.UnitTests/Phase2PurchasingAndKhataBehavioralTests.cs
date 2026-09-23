using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Purchasing;
using Xunit;

namespace EdgeRetails.UnitTests;

public sealed class Phase2PurchasingAndKhataBehavioralTests
{
    private readonly Phase2TestDoubles _fakes = new();

    private CreatePurchaseHandler CreatePurchaseHandler() =>
        new(
            _fakes.Purchasing,
            _fakes.Parties,
            _fakes.Catalog,
            _fakes.Inventory,
            _fakes.CostAllocator,
            _fakes.Cash,
            _fakes.Traceability,
            _fakes.SupplierAccounts,
            _fakes.OperationLock,
            _fakes.ResourceLock,
            _fakes.Audit,
            _fakes.Numbers,
            _fakes.Clock,
            _fakes.Transactions,
            _fakes.Authorization,
            _fakes.UnitOfWork);

    private CreatePurchaseReturnHandler CreatePurchaseReturnHandler() =>
        new(
            _fakes.Purchasing,
            _fakes.Inventory,
            _fakes.CostAllocator,
            _fakes.SupplierAccounts,
            _fakes.OperationLock,
            _fakes.ResourceLock,
            _fakes.Audit,
            _fakes.Numbers,
            _fakes.Clock,
            _fakes.Transactions,
            _fakes.Authorization,
            _fakes.UnitOfWork);

    private VoidPurchaseHandler CreateVoidPurchaseHandler() =>
        new(
            _fakes.Purchasing,
            _fakes.Inventory,
            _fakes.CostAllocator,
            _fakes.SupplierAccounts,
            _fakes.OperationLock,
            _fakes.ResourceLock,
            _fakes.Audit,
            _fakes.Numbers,
            _fakes.Clock,
            _fakes.Transactions,
            _fakes.Authorization,
            _fakes.UnitOfWork);

    private CreateSupplierPaymentHandler CreateSupplierPaymentHandler() =>
        new(
            _fakes.SupplierAccounts,
            _fakes.Parties,
            _fakes.Cash,
            _fakes.OperationLock,
            _fakes.ResourceLock,
            _fakes.Authorization,
            _fakes.Numbers,
            _fakes.Clock,
            _fakes.Audit,
            _fakes.Transactions,
            _fakes.UnitOfWork);

    private ReverseSupplierPaymentHandler ReverseSupplierPaymentHandler() =>
        new(
            _fakes.SupplierAccounts,
            _fakes.Cash,
            _fakes.OperationLock,
            _fakes.ResourceLock,
            _fakes.Authorization,
            _fakes.Numbers,
            _fakes.Clock,
            _fakes.Audit,
            _fakes.Transactions,
            _fakes.UnitOfWork);

    private CreateSupplierRefundHandler CreateSupplierRefundHandler() =>
        new(
            _fakes.SupplierAccounts,
            _fakes.Parties,
            _fakes.Cash,
            _fakes.OperationLock,
            _fakes.ResourceLock,
            _fakes.Authorization,
            _fakes.Numbers,
            _fakes.Clock,
            _fakes.Audit,
            _fakes.Transactions,
            _fakes.UnitOfWork);

    private ReverseSupplierRefundHandler ReverseSupplierRefundHandler() =>
        new(
            _fakes.SupplierAccounts,
            _fakes.Cash,
            _fakes.OperationLock,
            _fakes.ResourceLock,
            _fakes.Authorization,
            _fakes.Numbers,
            _fakes.Clock,
            _fakes.Audit,
            _fakes.Transactions,
            _fakes.UnitOfWork);

    private SupplierAccountAdjustmentHandler CreateSupplierAccountAdjustmentHandler() =>
        new(
            _fakes.SupplierAccounts,
            _fakes.OperationLock,
            _fakes.ResourceLock,
            _fakes.Authorization,
            _fakes.Numbers,
            _fakes.Clock,
            _fakes.Audit,
            _fakes.Transactions,
            _fakes.UnitOfWork);

    private (Supplier Supplier, Product Product, ProductUnit Unit) SeedQuantityProduct(string sku = "CBL-001", decimal cost = 100m, decimal price = 150m)
    {
        var supplier = new Supplier { Id = Guid.CreateVersion7(), Name = "Alpha Tech", DealerCode = "AL1", IsActive = true };
        _fakes.Parties.AddSupplier(supplier);

        var unit = new Unit { Id = Guid.CreateVersion7(), Name = "Piece", Symbol = "pc" };
        var product = new Product
        {
            Id = Guid.CreateVersion7(),
            Name = "Charging Cable",
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

        return (supplier, product, pu);
    }

    private (Supplier Supplier, Product Product, ProductUnit Unit) SeedSerializedProduct(string sku = "IPHONE-15", decimal cost = 200000m, decimal price = 250000m)
    {
        var supplier = new Supplier { Id = Guid.CreateVersion7(), Name = "Mobile Distro", DealerCode = "MD1", IsActive = true };
        _fakes.Parties.AddSupplier(supplier);

        var unit = new Unit { Id = Guid.CreateVersion7(), Name = "Piece", Symbol = "pc" };
        var product = new Product
        {
            Id = Guid.CreateVersion7(),
            Name = "iPhone 15 Pro",
            Sku = sku,
            BaseUnitId = unit.Id,
            TrackingMode = TrackingMode.Serialized,
            SerialTrackingEnabled = true,
            ImeiTrackingEnabled = true,
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

        return (supplier, product, pu);
    }

    [Fact]
    public async Task QuantityPurchase_CreatesLots_StockBalance_And_KhataLiability()
    {
        var (supplier, product, unit) = SeedQuantityProduct();
        var handler = CreatePurchaseHandler();
        var actorId = Guid.CreateVersion7();
        var opId = Guid.CreateVersion7();

        var result = await handler.HandleAsync(
            new CreatePurchaseCommand(
                supplier.Id,
                "INV-1001",
                DateOnly.FromDateTime(DateTime.UtcNow),
                "Initial stock purchase",
                0m,
                PurchaseSettlementMode.External,
                actorId,
                opId,
                [
                    new CreatePurchaseLineInput(product.Id, unit.Id, 10m, 100m, 150m, [])
                ],
                InitialPaymentAmount: 0m),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);
        Assert.Equal(1000m, result.Value!.GrandTotal);

        // Check stock balance
        var balance = _fakes.Inventory.Balances[product.Id];
        Assert.Equal(10m, balance.SellableQty);

        // Check product cost state
        var costState = _fakes.Inventory.CostStates[product.Id];
        Assert.Equal(10m, costState.CostedQty);
        Assert.Equal(1000m, costState.TotalInventoryCost);
        Assert.Equal(100m, costState.MovingAverageCost);

        // Check lot created
        Assert.Single(_fakes.Inventory.Lots);
        Assert.Equal(10m, _fakes.Inventory.Lots[0].ReceivedQuantity);
        Assert.Equal(100m, _fakes.Inventory.Lots[0].OriginalUnitCost);

        // Check Khata entry
        Assert.Single(_fakes.SupplierAccounts.Entries);
        var khata = _fakes.SupplierAccounts.Entries[0];
        Assert.Equal(SupplierAccountEntryType.Purchase, khata.EntryType);
        Assert.Equal(SupplierAccountDirection.IncreasePayable, khata.Direction);
        Assert.Equal(1000m, khata.Amount);

        var balancePayable = await _fakes.SupplierAccounts.GetCurrentBalanceAsync(supplier.Id, CancellationToken.None);
        Assert.Equal(1000m, balancePayable);
    }

    [Fact]
    public async Task SerializedPurchase_AllocatesContiguousTrackingCodes_And_CreatesInventoryUnits()
    {
        var (supplier, product, unit) = SeedSerializedProduct();
        var handler = CreatePurchaseHandler();
        var actorId = Guid.CreateVersion7();
        var opId = Guid.CreateVersion7();

        var imei1A = "860123456789016";
        var imei1B = "860123456789024";

        var result = await handler.HandleAsync(
            new CreatePurchaseCommand(
                supplier.Id,
                "INV-SER-01",
                DateOnly.FromDateTime(DateTime.UtcNow),
                "Serialized intake",
                0m,
                PurchaseSettlementMode.External,
                actorId,
                opId,
                [
                    new CreatePurchaseLineInput(
                        product.Id,
                        unit.Id,
                        2m,
                        200000m,
                        250000m,
                        [
                            new SerializedIdentityInput("SN-A100", imei1A, null),
                            new SerializedIdentityInput("SN-A101", imei1B, null)
                        ])
                ],
                InitialPaymentAmount: 0m),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(400000m, result.Value!.GrandTotal);

        // Verify inventory units
        Assert.Equal(2, _fakes.Inventory.Units.Count);
        var unitA = _fakes.Inventory.Units[0];
        var unitB = _fakes.Inventory.Units[1];

        Assert.Equal(InventoryUnitStatus.InStock, unitA.Status);
        Assert.Equal(InventoryUnitStatus.InStock, unitB.Status);
        Assert.Equal("MD1-IPHONE-15-000001", unitA.TrackingCode);
        Assert.Equal("MD1-IPHONE-15-000002", unitB.TrackingCode);
        Assert.Equal(1L, unitA.ItemSequence);
        Assert.Equal(2L, unitB.ItemSequence);
        Assert.Equal(200000m, unitA.AcquisitionCost);
        Assert.Equal(200000m, unitB.AcquisitionCost);

        // Verify Khata
        var balancePayable = await _fakes.SupplierAccounts.GetCurrentBalanceAsync(supplier.Id, CancellationToken.None);
        Assert.Equal(400000m, balancePayable);
    }

    [Fact]
    public async Task Purchase_WithCashSettlement_RecordsSupplierPayment_And_CashMovement()
    {
        var (supplier, product, unit) = SeedQuantityProduct();
        var handler = CreatePurchaseHandler();
        var actorId = Guid.CreateVersion7();
        var opId = Guid.CreateVersion7();

        var session = new CashSession
        {
            Id = Guid.CreateVersion7(),
            OpenedBy = actorId,
            OpeningCash = 10000m,
            Status = CashSessionStatus.Open,
            OpenedAt = DateTimeOffset.UtcNow,
            BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow)
        };
        _fakes.Cash.AddSession(session);

        var result = await handler.HandleAsync(
            new CreatePurchaseCommand(
                supplier.Id,
                "INV-CASH-01",
                DateOnly.FromDateTime(DateTime.UtcNow),
                "Cash purchase",
                0m,
                PurchaseSettlementMode.CashDrawer,
                actorId,
                opId,
                [
                    new CreatePurchaseLineInput(product.Id, unit.Id, 10m, 100m, 150m, [])
                ]),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);

        // Two entries: PURCHASE (+1000) and SUPPLIER_PAYMENT (-1000)
        Assert.Equal(2, _fakes.SupplierAccounts.Entries.Count);
        Assert.Equal(SupplierAccountEntryType.Purchase, _fakes.SupplierAccounts.Entries[0].EntryType);
        Assert.Equal(SupplierAccountEntryType.SupplierPayment, _fakes.SupplierAccounts.Entries[1].EntryType);

        var balancePayable = await _fakes.SupplierAccounts.GetCurrentBalanceAsync(supplier.Id, CancellationToken.None);
        Assert.Equal(0m, balancePayable); // Settled immediately

        // Verify cash movement
        Assert.Single(_fakes.Cash.Movements);
        var cashMove = _fakes.Cash.Movements[0];
        Assert.Equal(CashMovementType.SupplierPaymentCashOut, cashMove.MovementType);
        Assert.Equal(CashMovementDirection.Out, cashMove.Direction);
        Assert.Equal(1000m, cashMove.Amount);
    }

    [Fact]
    public async Task PurchaseReturn_DecreasesPayable_And_RemovesInventory_UnderRemainingReturnEligibility()
    {
        var (supplier, product, unit) = SeedQuantityProduct();
        var purchaseHandler = CreatePurchaseHandler();
        var returnHandler = CreatePurchaseReturnHandler();
        var actorId = Guid.CreateVersion7();

        var purchaseResult = await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                supplier.Id,
                "INV-RET-01",
                DateOnly.FromDateTime(DateTime.UtcNow),
                "Initial purchase",
                0m,
                PurchaseSettlementMode.External,
                actorId,
                Guid.CreateVersion7(),
                [
                    new CreatePurchaseLineInput(product.Id, unit.Id, 10m, 100m, 150m, [])
                ],
                InitialPaymentAmount: 0m),
            CancellationToken.None);

        Assert.True(purchaseResult.IsSuccess);
        var purchaseId = purchaseResult.Value!.PurchaseId;
        var purchaseItemId = _fakes.Purchasing.PurchaseItems.Single(i => i.PurchaseId == purchaseId).Id;

        // Return 4 units
        var returnResult = await returnHandler.HandleAsync(
            new CreatePurchaseReturnCommand(
                purchaseId,
                "Damaged in transit",
                null,
                PurchaseReturnSettlementMode.External,
                actorId,
                Guid.CreateVersion7(),
                [
                    new PurchaseReturnLineInput(purchaseItemId, 4m, null, [])
                ]),
            CancellationToken.None);

        Assert.True(returnResult.IsSuccess, returnResult.Error?.Message);
        Assert.Equal(400m, returnResult.Value!.SupplierReturnValue);

        // Stock decreased from 10 to 6
        var balance = _fakes.Inventory.Balances[product.Id];
        Assert.Equal(6m, balance.SellableQty);

        // Khata net balance decreased to 600
        var payable = await _fakes.SupplierAccounts.GetCurrentBalanceAsync(supplier.Id, CancellationToken.None);
        Assert.Equal(600m, payable);

        // Second return of 7 units should fail because only 6 remain
        var overReturnResult = await returnHandler.HandleAsync(
            new CreatePurchaseReturnCommand(
                purchaseId,
                "Excess return attempt",
                null,
                PurchaseReturnSettlementMode.External,
                actorId,
                Guid.CreateVersion7(),
                [
                    new PurchaseReturnLineInput(purchaseItemId, 7m, null, [])
                ]),
            CancellationToken.None);

        Assert.False(overReturnResult.IsSuccess);
        Assert.Equal("purchasing.return_exceeds_original", overReturnResult.Error?.Code);
    }

    [Fact]
    public async Task PurchaseVoid_DecreasesPayable_And_ReversesInventory_WithoutRollingBackSequence()
    {
        var (supplier, product, unit) = SeedSerializedProduct();
        var purchaseHandler = CreatePurchaseHandler();
        var voidHandler = CreateVoidPurchaseHandler();
        var actorId = Guid.CreateVersion7();

        var purchaseResult = await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                supplier.Id,
                "INV-VOID-01",
                DateOnly.FromDateTime(DateTime.UtcNow),
                "Mistaken purchase",
                0m,
                PurchaseSettlementMode.External,
                actorId,
                Guid.CreateVersion7(),
                [
                    new CreatePurchaseLineInput(
                        product.Id,
                        unit.Id,
                        1m,
                        200000m,
                        250000m,
                        [
                            new SerializedIdentityInput("SN-VOID-1", "860123456789016", null)
                        ])
                ],
                InitialPaymentAmount: 0m),
            CancellationToken.None);

        Assert.True(purchaseResult.IsSuccess);
        var purchaseId = purchaseResult.Value!.PurchaseId;

        // Sequence after purchase is at least 1
        var supplierProduct = await _fakes.Traceability.GetSupplierProductAsync(supplier.Id, product.Id, CancellationToken.None);
        Assert.NotNull(supplierProduct);
        var seqBeforeVoid = supplierProduct!.NextItemSequence;

        // Void the purchase
        var voidResult = await voidHandler.HandleAsync(
            new VoidPurchaseCommand(purchaseId, Guid.CreateVersion7(), actorId, "Wrong supplier entered"),
            CancellationToken.None);

        Assert.True(voidResult.IsSuccess, voidResult.Error?.Message);

        // Sequence must NOT be rolled back
        Assert.Equal(seqBeforeVoid, supplierProduct.NextItemSequence);

        // Unit status must be Voided
        var voidedUnit = _fakes.Inventory.Units.Single();
        Assert.Equal(InventoryUnitStatus.ReceiptVoided, voidedUnit.Status);

        // Khata payable is 0
        var payable = await _fakes.SupplierAccounts.GetCurrentBalanceAsync(supplier.Id, CancellationToken.None);
        Assert.Equal(0m, payable);
    }

    [Fact]
    public async Task SupplierPayment_Settlement_And_Advance_HandlePayableCorrectly()
    {
        var (supplier, _, _) = SeedQuantityProduct();
        var paymentHandler = CreateSupplierPaymentHandler();
        var actorId = Guid.CreateVersion7();

        // Seed an initial purchase payable of 10,000
        _fakes.SupplierAccounts.AddEntry(new SupplierAccountEntry
        {
            Id = Guid.CreateVersion7(),
            SupplierId = supplier.Id,
            EntryType = SupplierAccountEntryType.Purchase,
            Direction = SupplierAccountDirection.IncreasePayable,
            Amount = 10000m,
            OccurredAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        });

        // 1. Settlement payment of 4,000
        var settleResult = await paymentHandler.HandleAsync(
            new CreateSupplierPaymentCommand(
                supplier.Id,
                4000m,
                SupplierPaymentPurpose.Settlement,
                SupplierSettlementMethod.External,
                actorId,
                Guid.CreateVersion7(),
                ExternalReference: "BANK-TXN-01",
                Note: "Partial settlement payment"),
            CancellationToken.None);

        Assert.True(settleResult.IsSuccess, settleResult.Error?.Message);
        Assert.Equal(6000m, await _fakes.SupplierAccounts.GetCurrentBalanceAsync(supplier.Id, CancellationToken.None));

        // 2. Advance payment of 10,000 (which exceeds the remaining 6000 payable) with Advance purpose
        var advanceResult = await paymentHandler.HandleAsync(
            new CreateSupplierPaymentCommand(
                supplier.Id,
                10000m,
                SupplierPaymentPurpose.Advance,
                SupplierSettlementMethod.External,
                actorId,
                Guid.CreateVersion7(),
                ExternalReference: "ADV-TXN-02",
                Note: "Quarterly stock advance"),
            CancellationToken.None);

        Assert.True(advanceResult.IsSuccess, advanceResult.Error?.Message);

        var balance = await _fakes.SupplierAccounts.GetCurrentBalanceAsync(supplier.Id, CancellationToken.None);
        Assert.Equal(-4000m, balance); // Supplier credit
    }

    [Fact]
    public async Task SupplierPaymentReversal_IncreasesPayable_And_ReversesCashMovement()
    {
        var (supplier, _, _) = SeedQuantityProduct();
        var paymentHandler = CreateSupplierPaymentHandler();
        var reverseHandler = ReverseSupplierPaymentHandler();
        var actorId = Guid.CreateVersion7();

        // Seed an initial payable of 10000
        _fakes.SupplierAccounts.AddEntry(new SupplierAccountEntry
        {
            Id = Guid.CreateVersion7(),
            SupplierId = supplier.Id,
            EntryType = SupplierAccountEntryType.Purchase,
            Direction = SupplierAccountDirection.IncreasePayable,
            Amount = 10000m,
            OccurredAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        });

        var session = new CashSession
        {
            Id = Guid.CreateVersion7(),
            OpenedBy = actorId,
            OpeningCash = 20000m,
            Status = CashSessionStatus.Open,
            OpenedAt = DateTimeOffset.UtcNow,
            BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow)
        };
        _fakes.Cash.AddSession(session);

        // Make 4000 cash payment
        var payResult = await paymentHandler.HandleAsync(
            new CreateSupplierPaymentCommand(
                supplier.Id,
                4000m,
                SupplierPaymentPurpose.Settlement,
                SupplierSettlementMethod.CashDrawer,
                actorId,
                Guid.CreateVersion7(),
                null,
                "Cash settlement"),
            CancellationToken.None);

        Assert.True(payResult.IsSuccess);
        var paymentId = payResult.Value!.PaymentId;
        Assert.Equal(6000m, await _fakes.SupplierAccounts.GetCurrentBalanceAsync(supplier.Id, CancellationToken.None));

        // Reverse the payment
        var revResult = await reverseHandler.HandleAsync(
            new ReverseSupplierPaymentCommand(paymentId, "Mistaken payment voucher", actorId, Guid.CreateVersion7()),
            CancellationToken.None);

        Assert.True(revResult.IsSuccess, revResult.Error?.Message);

        // Balance restored to 10000
        Assert.Equal(10000m, await _fakes.SupplierAccounts.GetCurrentBalanceAsync(supplier.Id, CancellationToken.None));

        // Cash reversal movement recorded
        Assert.Equal(2, _fakes.Cash.Movements.Count);
        var revCash = _fakes.Cash.Movements.Last();
        Assert.Equal(CashMovementType.SupplierPaymentReversalCashIn, revCash.MovementType);
        Assert.Equal(CashMovementDirection.In, revCash.Direction);
        Assert.Equal(4000m, revCash.Amount);
    }

    [Fact]
    public async Task SupplierRefund_IncreasesPayable_And_RecordsCashIn()
    {
        var (supplier, _, _) = SeedQuantityProduct();
        var refundHandler = CreateSupplierRefundHandler();
        var reverseRefundHandler = ReverseSupplierRefundHandler();
        var actorId = Guid.CreateVersion7();

        // Supplier owes us 5000 (negative payable)
        _fakes.SupplierAccounts.AddEntry(new SupplierAccountEntry
        {
            Id = Guid.CreateVersion7(),
            SupplierId = supplier.Id,
            EntryType = SupplierAccountEntryType.PurchaseReturnCredit,
            Direction = SupplierAccountDirection.DecreasePayable,
            Amount = 5000m,
            OccurredAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        });

        var session = new CashSession
        {
            Id = Guid.CreateVersion7(),
            OpenedBy = actorId,
            OpeningCash = 5000m,
            Status = CashSessionStatus.Open,
            OpenedAt = DateTimeOffset.UtcNow,
            BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow)
        };
        _fakes.Cash.AddSession(session);

        // Supplier refunds 3000 cash back to shop
        var refundResult = await refundHandler.HandleAsync(
            new CreateSupplierRefundCommand(
                supplier.Id,
                3000m,
                SupplierSettlementMethod.CashDrawer,
                actorId,
                Guid.CreateVersion7(),
                null,
                null,
                null,
                "Return cash refund"),
            CancellationToken.None);

        Assert.True(refundResult.IsSuccess, refundResult.Error?.Message);
        var refundId = refundResult.Value!.RefundId;

        // Balance increased from -5000 to -2000 (credit reduced)
        Assert.Equal(-2000m, await _fakes.SupplierAccounts.GetCurrentBalanceAsync(supplier.Id, CancellationToken.None));

        // CashIn movement recorded
        var move = _fakes.Cash.Movements.Last();
        Assert.Equal(CashMovementType.SupplierRefundCashIn, move.MovementType);
        Assert.Equal(CashMovementDirection.In, move.Direction);
        Assert.Equal(3000m, move.Amount);

        // Reverse the refund
        var revResult = await reverseRefundHandler.HandleAsync(
            new ReverseSupplierRefundCommand(refundId, "Bounced cash check/envelope", actorId, Guid.CreateVersion7()),
            CancellationToken.None);

        Assert.True(revResult.IsSuccess, revResult.Error?.Message);

        // Balance decreased back to -5000
        Assert.Equal(-5000m, await _fakes.SupplierAccounts.GetCurrentBalanceAsync(supplier.Id, CancellationToken.None));
    }

    [Fact]
    public async Task SupplierKhata_BalanceFormula_DerivedFromCompleteEventStream()
    {
        var (supplier, _, _) = SeedQuantityProduct();
        var adjHandler = CreateSupplierAccountAdjustmentHandler();
        var actorId = Guid.CreateVersion7();

        // Test manual adjustment increase & decrease
        var adjInc = await adjHandler.HandleAsync(
            new SupplierAccountAdjustmentCommand(
                supplier.Id,
                1500m,
                SupplierAccountDirection.IncreasePayable,
                "Prior invoice ledger correction",
                actorId,
                Guid.CreateVersion7()),
            CancellationToken.None);

        Assert.True(adjInc.IsSuccess);
        Assert.Equal(1500m, await _fakes.SupplierAccounts.GetCurrentBalanceAsync(supplier.Id, CancellationToken.None));

        var adjDec = await adjHandler.HandleAsync(
            new SupplierAccountAdjustmentCommand(
                supplier.Id,
                500m,
                SupplierAccountDirection.DecreasePayable,
                "Rebate settlement agreement",
                actorId,
                Guid.CreateVersion7()),
            CancellationToken.None);

        Assert.True(adjDec.IsSuccess);
        Assert.Equal(1000m, await _fakes.SupplierAccounts.GetCurrentBalanceAsync(supplier.Id, CancellationToken.None));
    }
}
