using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Inventory;
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

public sealed class Phase11WarrantyAndCustodyBehavioralTests
{
    private readonly Phase2TestDoubles _fakes = new();
    private readonly InventoryConditionService _conditions;

    public Phase11WarrantyAndCustodyBehavioralTests()
    {
        _conditions = new InventoryConditionService(
            _fakes.Catalog,
            _fakes.Inventory,
            _fakes.CostAllocator,
            _fakes.Clock);
    }

    private CreateWarrantyClaimHandler CreateClaimHandler() =>
        new(
            _fakes.Catalog,
            _fakes.Parties,
            _fakes.Sales,
            _fakes.Purchasing,
            _fakes.Inventory,
            _fakes.Traceability,
            _fakes.Warranty,
            _fakes.ResourceLock,
            _fakes.Authorization,
            _fakes.Numbers,
            _fakes.Clock,
            _fakes.Transactions,
            _fakes.UnitOfWork,
            _fakes.OperationLock,
            _fakes.OutcomeLedger);

    private ReceiveCustomerWarrantyReplacementHandler CreateReceiveCustomerReplacementHandler() =>
        new(
            _fakes.Warranty,
            _fakes.Catalog,
            _fakes.Inventory,
            _fakes.Traceability,
            _fakes.Parties,
            _fakes.OperationLock,
            _fakes.ResourceLock,
            _fakes.Authorization,
            _fakes.Clock,
            _fakes.Transactions,
            _fakes.UnitOfWork,
            _fakes.PhysicalUnits);

    private HandoverWarrantyItemHandler CreateHandoverHandler() =>
        new(
            _fakes.Warranty,
            _fakes.Inventory,
            _fakes.ResourceLock,
            _fakes.Authorization,
            _fakes.Clock,
            _fakes.Transactions,
            _fakes.UnitOfWork,
            _fakes.OperationLock);

    private SendShopStockToSupplierWarrantyHandler CreateSendShopStockHandler() =>
        new(
            _fakes.Warranty,
            _fakes.Catalog,
            _fakes.Parties,
            _fakes.Purchasing,
            _fakes.Inventory,
            _fakes.Traceability,
            _conditions,
            _fakes.ResourceLock,
            _fakes.Authorization,
            _fakes.Audit,
            _fakes.Numbers,
            _fakes.Clock,
            _fakes.Transactions,
            _fakes.UnitOfWork,
            _fakes.OperationLock);

    private ReceiveShopStockWarrantyHandler CreateReceiveShopStockHandler() =>
        new(
            _fakes.Catalog,
            _fakes.Inventory,
            _conditions,
            _fakes.CostAllocator,
            _fakes.Warranty,
            _fakes.Traceability,
            _fakes.Parties,
            _fakes.SupplierAccounts,
            _fakes.OperationLock,
            _fakes.ResourceLock,
            _fakes.Authorization,
            _fakes.Numbers,
            _fakes.Audit,
            _fakes.Clock,
            _fakes.Transactions,
            _fakes.UnitOfWork,
            _fakes.PhysicalUnits);

    [Fact]
    public async Task P11_IMP_084_PackAndSegment_WarrantySourceAllocation_RejectsMismatchAndEnforcesProvenance()
    {
        // Arrange: Seed Supplier, Customer, Container Product, and Purchase/Sale
        var supplier = new Supplier { Id = Guid.NewGuid(), Name = "Cable Co", DealerCode = "CC1", IsActive = true };
        _fakes.Parties.AddSupplier(supplier);
        var customer = new Customer { Id = Guid.NewGuid(), Name = "Electrician Bob", IsActive = true };
        _fakes.Parties.AddCustomer(customer);

        var product = new Product
        {
            Id = Guid.NewGuid(),
            Name = "Armored Cable 50m Drum",
            Sku = "CBL-50M",
            TrackingMode = TrackingMode.Container,
            DefaultWarrantyMonths = 12,
            IsActive = true
        };
        _fakes.Catalog.AddProduct(product);

        var supplierProduct = new SupplierProduct
        {
            Id = Guid.NewGuid(),
            SupplierId = supplier.Id,
            ProductId = product.Id,
            NextItemSequence = 2,
            IsActive = true
        };
        _fakes.Traceability.AddSupplierProduct(supplierProduct);

        var purchase = new Purchase
        {
            Id = Guid.NewGuid(),
            SupplierId = supplier.Id,
            Status = PurchaseStatus.Completed
        };
        _fakes.Purchasing.Purchases[purchase.Id] = purchase;

        var purchaseItem = new PurchaseItem
        {
            Id = Guid.NewGuid(),
            PurchaseId = purchase.Id,
            ProductId = product.Id,
            BaseQuantity = 1m
        };
        _fakes.Purchasing.PurchaseItems.Add(purchaseItem);

        var drumUnit = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            ProductId = product.Id,
            SupplierProductId = supplierProduct.Id,
            SourcePurchaseItemId = purchaseItem.Id,
            TrackingCode = "CC1-CBL-000001",
            Status = InventoryUnitStatus.Sold,
            InitialBaseQuantity = 50m,
            RemainingBaseQuantity = 0m,
            PackState = PackState.Consumed,
            AcquisitionCost = 1000m
        };
        _fakes.Inventory.Units.Add(drumUnit);

        var sale = new Sale
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            Status = SaleStatus.Completed
        };
        _fakes.Sales.AddSale(sale);

        var saleItem = new SaleItem
        {
            Id = Guid.NewGuid(),
            SaleId = sale.Id,
            ProductId = product.Id,
            BaseQuantity = 50m,
            WarrantyValidUntil = _fakes.Clock.ShopDate.AddMonths(12)
        };
        _fakes.Sales.AddSaleItem(saleItem);

        // Link unit to sale item
        _fakes.Sales.AddSaleItemUnit(new SaleItemUnit
        {
            SaleItemId = saleItem.Id,
            InventoryUnitId = drumUnit.Id,
            WarrantyValidUntil = saleItem.WarrantyValidUntil
        });

        var handler = CreateClaimHandler();
        var actorId = Guid.NewGuid();

        // 1. Quantity mismatch on container claim: claim 25m when whole container 50m was sold
        var mismatchCmd = new CreateWarrantyClaimCommand(
            customer.Id,
            sale.Id,
            actorId,
            Guid.NewGuid(),
            new[]
            {
                new WarrantyClaimItemInput(
                    product.Id,
                    Quantity: 25m, // Mismatch: must match sold container quantity 50m
                    FaultDescription: "Insulation breakdown",
                    OriginalSaleItemId: saleItem.Id,
                    Units: new[] { new WarrantyClaimUnitInput(drumUnit.Id, drumUnit.TrackingCode) })
            });

        var mismatchResult = await handler.HandleAsync(mismatchCmd, CancellationToken.None);
        Assert.False(mismatchResult.IsSuccess);
        Assert.Equal("warranty.container_quantity_mismatch", mismatchResult.Error?.Code);

        // 2. Unit not sold on item: submit a different random unit
        var foreignUnit = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            ProductId = product.Id,
            SupplierProductId = supplierProduct.Id,
            Status = InventoryUnitStatus.Sold,
            TrackingCode = "CC1-CBL-999999"
        };
        _fakes.Inventory.Units.Add(foreignUnit);

        var foreignCmd = new CreateWarrantyClaimCommand(
            customer.Id,
            sale.Id,
            actorId,
            Guid.NewGuid(),
            new[]
            {
                new WarrantyClaimItemInput(
                    product.Id,
                    Quantity: 50m,
                    FaultDescription: "Insulation fault",
                    OriginalSaleItemId: saleItem.Id,
                    Units: new[] { new WarrantyClaimUnitInput(foreignUnit.Id, foreignUnit.TrackingCode) })
            });

        var foreignResult = await handler.HandleAsync(foreignCmd, CancellationToken.None);
        Assert.False(foreignResult.IsSuccess);
        Assert.Equal("warranty.unit_not_sold_on_item", foreignResult.Error?.Code);

        // 3. Exact matching container claim succeeds
        var validCmd = new CreateWarrantyClaimCommand(
            customer.Id,
            sale.Id,
            actorId,
            Guid.NewGuid(),
            new[]
            {
                new WarrantyClaimItemInput(
                    product.Id,
                    Quantity: 50m,
                    FaultDescription: "Insulation breakdown at 30m mark",
                    OriginalSaleItemId: saleItem.Id,
                    Units: new[] { new WarrantyClaimUnitInput(drumUnit.Id, drumUnit.TrackingCode) })
            });

        var validResult = await handler.HandleAsync(validCmd, CancellationToken.None);
        Assert.True(validResult.IsSuccess, validResult.Error?.Message);
        Assert.NotEqual(Guid.Empty, validResult.Value);

        var createdClaim = await _fakes.Warranty.GetClaimAsync(validResult.Value, CancellationToken.None);
        Assert.NotNull(createdClaim);
        Assert.Equal(WarrantyCustody.WithShop, createdClaim!.CurrentCustody);
        Assert.Equal(supplier.Id, createdClaim.SupplierId);
    }

    [Fact]
    public async Task P11_IMP_085_CustomerVsShopCustody_ReplacementExcludesFromShopValuation()
    {
        // Arrange
        var supplier = new Supplier { Id = Guid.NewGuid(), Name = "Phone Maker", DealerCode = "PM1", IsActive = true };
        _fakes.Parties.AddSupplier(supplier);
        var customer = new Customer { Id = Guid.NewGuid(), Name = "Alice", IsActive = true };
        _fakes.Parties.AddCustomer(customer);

        var product = new Product
        {
            Id = Guid.NewGuid(),
            Name = "Smartphone X",
            Sku = "SP-X",
            TrackingMode = TrackingMode.Serialized,
            SerialTrackingEnabled = true,
            DefaultWarrantyMonths = 12,
            IsActive = true
        };
        _fakes.Catalog.AddProduct(product);

        var supplierProduct = new SupplierProduct
        {
            Id = Guid.NewGuid(),
            SupplierId = supplier.Id,
            ProductId = product.Id,
            NextItemSequence = 2,
            IsActive = true
        };
        _fakes.Traceability.AddSupplierProduct(supplierProduct);

        var originalUnit = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            ProductId = product.Id,
            SupplierProductId = supplierProduct.Id,
            TrackingCode = "PM1-SPX-000001",
            SerialNumber = "SN-OLD-001",
            Status = InventoryUnitStatus.Sold,
            AcquisitionCost = 500m
        };
        _fakes.Inventory.Units.Add(originalUnit);

        var claim = new WarrantyClaim
        {
            Id = Guid.NewGuid(),
            ClaimNumber = "WC-2026-001",
            CustomerId = customer.Id,
            SupplierId = supplier.Id,
            Status = WarrantyClaimStatus.SentToSupplier,
            CurrentCustody = WarrantyCustody.WithSupplier,
            ClientOperationId = Guid.NewGuid()
        };
        _fakes.Warranty.AddClaim(claim);

        var claimItem = new WarrantyClaimItem
        {
            Id = Guid.NewGuid(),
            ClaimId = claim.Id,
            ProductId = product.Id,
            Quantity = 1m,
            FaultDescription = "Screen defect"
        };
        _fakes.Warranty.AddClaimItem(claimItem);

        var claimUnit = new WarrantyClaimItemUnit
        {
            Id = Guid.NewGuid(),
            ClaimItemId = claimItem.Id,
            OriginalInventoryUnitId = originalUnit.Id,
            OriginalIdentitySnapshot = "PM1-SPX-000001 | SN-OLD-001"
        };
        _fakes.Warranty.AddClaimItemUnit(claimUnit);

        // Pre-replacement shop stock state: 0 sellable, 0 costed
        _fakes.Inventory.Balances[product.Id] = new StockBalance { ProductId = product.Id, SellableQty = 0m };
        _fakes.Inventory.CostStates[product.Id] = new ProductCostState
        {
            ProductId = product.Id,
            CostedQty = 0m,
            TotalInventoryCost = 0m,
            MovingAverageCost = 500m
        };

        var receiveHandler = CreateReceiveCustomerReplacementHandler();
        var receiveCmd = new ReceiveCustomerWarrantyReplacementCommand(
            claim.Id,
            Guid.NewGuid(),
            Guid.NewGuid(),
            new[] { new CustomerWarrantyReplacementUnitInput(claimUnit.Id, "SN-NEW-999", null, null) },
            "Received replacement from vendor");

        var receiveResult = await receiveHandler.HandleAsync(receiveCmd, CancellationToken.None);
        Assert.True(receiveResult.IsSuccess, receiveResult.Error?.Message);

        // Invariant 1: Unit created in WarrantyCustomerHeld status (customer-owned custody)
        Assert.NotNull(claimUnit.ReplacementInventoryUnitId);
        var replacementUnit = _fakes.Inventory.Units.First(u => u.Id == claimUnit.ReplacementInventoryUnitId);
        Assert.Equal(InventoryUnitStatus.WarrantyCustomerHeld, replacementUnit.Status);
        Assert.Null(replacementUnit.InventoryLotId);

        // Invariant 2: Shop stock balance and valuation remain completely zero (not shop inventory)
        Assert.Equal(0m, _fakes.Inventory.Balances[product.Id].SellableQty);
        Assert.Equal(0m, _fakes.Inventory.CostStates[product.Id].CostedQty);
        Assert.Equal(0m, _fakes.Inventory.CostStates[product.Id].TotalInventoryCost);

        // Handover to customer
        var handoverHandler = CreateHandoverHandler();
        var handoverCmd = new HandoverWarrantyItemCommand(
            claim.Id,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Delivered replacement to customer");

        var handoverResult = await handoverHandler.HandleAsync(handoverCmd, CancellationToken.None);
        Assert.True(handoverResult.IsSuccess, handoverResult.Error?.Message);
        Assert.Equal(InventoryUnitStatus.WarrantyCustomerHandedOver, replacementUnit.Status);
    }

    [Fact]
    public async Task P11_IMP_086_ShopStockWarranty_FreezesSendMwa_And_CreditsSupplierAccountWithoutDoubledGain()
    {
        // Arrange: Shop has 1 damaged unit sent to supplier warranty
        var supplier = new Supplier { Id = Guid.NewGuid(), Name = "Hardware Distributor", DealerCode = "HD1", IsActive = true };
        _fakes.Parties.AddSupplier(supplier);

        var product = new Product
        {
            Id = Guid.NewGuid(),
            Name = "Router AC1200",
            Sku = "RTR-AC1200",
            TrackingMode = TrackingMode.Serialized,
            SerialTrackingEnabled = true,
            IsActive = true
        };
        _fakes.Catalog.AddProduct(product);

        var lotId = Guid.NewGuid();
        var lot = new InventoryLot
        {
            Id = lotId,
            ProductId = product.Id,
            OriginalUnitCost = 100m,
            EffectiveUnitCost = 100m,
            CreatedAt = _fakes.Clock.UtcNow
        };
        _fakes.Inventory.Lots.Add(lot);
        _fakes.Inventory.LotBucketBalances.Add(new InventoryLotBucketBalance
        {
            LotId = lotId,
            StockBucket = InventoryBucket.Damaged,
            Quantity = 1m
        });

        _fakes.Inventory.Balances[product.Id] = new StockBalance
        {
            ProductId = product.Id,
            DamagedQty = 1m,
            WithSupplierQty = 0m,
            Version = 1
        };

        _fakes.Inventory.CostStates[product.Id] = new ProductCostState
        {
            ProductId = product.Id,
            CostedQty = 1m,
            TotalInventoryCost = 100m,
            MovingAverageCost = 100m,
            Version = 1
        };

        var supplierProduct = new SupplierProduct
        {
            Id = Guid.NewGuid(),
            SupplierId = supplier.Id,
            ProductId = product.Id,
            NextItemSequence = 2,
            IsActive = true
        };
        _fakes.Traceability.AddSupplierProduct(supplierProduct);

        var unit = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            ProductId = product.Id,
            SupplierProductId = supplierProduct.Id,
            InventoryLotId = lotId,
            TrackingCode = "HD1-RTR-000001",
            SerialNumber = "SN-RTR-001",
            Status = InventoryUnitStatus.Damaged,
            AcquisitionCost = 100m
        };
        _fakes.Inventory.Units.Add(unit);

        var actorId = Guid.NewGuid();
        var sendHandler = CreateSendShopStockHandler();

        // 1. Send shop stock to supplier
        var sendCmd = new SendShopStockToSupplierWarrantyCommand(
            product.Id,
            InventoryBucket.Damaged,
            BaseQuantity: 1m,
            supplier.Id,
            SourcePurchaseItemId: null,
            FaultDescription: "Failed firmware chip",
            ActorId: actorId,
            ClientOperationId: Guid.NewGuid(),
            InventoryUnitIds: new[] { unit.Id });

        var sendResult = await sendHandler.HandleAsync(sendCmd, CancellationToken.None);
        Assert.True(sendResult.IsSuccess, sendResult.Error?.Message);
        var caseId = sendResult.Value;

        // Invariant 1: Inventory transferred to WithSupplier bucket
        Assert.Equal(0m, _fakes.Inventory.Balances[product.Id].DamagedQty);
        Assert.Equal(1m, _fakes.Inventory.Balances[product.Id].WithSupplierQty);
        Assert.Equal(InventoryUnitStatus.WithSupplier, unit.Status);

        // Invariant 2: Frozen send-time MWA allocation snapshot recorded
        var sendAllocs = await _fakes.Warranty.GetShopWarrantySendAllocationsByCaseIdAsync(caseId, CancellationToken.None);
        Assert.Single(sendAllocs);
        var sendAlloc = sendAllocs[0];
        Assert.Equal(100m, sendAlloc.SendTimeMwaUnitCostSnapshot);
        Assert.Equal(100m, sendAlloc.SendTimeCarryingValueSnapshot);
        Assert.Equal(100m, sendAlloc.SourceUnitCostSnapshot);

        // 2. Receive Supplier Credit of $120 ($100 carrying cost derecognized + $20 recovery gain)
        var receiveHandler = CreateReceiveShopStockHandler();
        var creditCmd = new ReceiveShopStockWarrantyCommand(
            caseId,
            WarrantyResolutionType.Credited,
            OriginalInventoryUnitIds: new[] { unit.Id },
            ReplacementUnits: null,
            Note: "Supplier approved credit note for full replacement value",
            ActorId: actorId,
            ClientOperationId: Guid.NewGuid(),
            SupplierCreditAmount: 120m,
            SupplierReference: "CN-2026-999");

        var creditResult = await receiveHandler.HandleAsync(creditCmd, CancellationToken.None);
        Assert.True(creditResult.IsSuccess, creditResult.Error?.Message);

        // Invariant 3: WITH_SUPPLIER inventory reduced to 0 and carrying cost derecognized
        Assert.Equal(0m, _fakes.Inventory.Balances[product.Id].WithSupplierQty);
        Assert.Equal(0m, _fakes.Inventory.CostStates[product.Id].TotalInventoryCost);
        Assert.Equal(0m, _fakes.Inventory.CostStates[product.Id].CostedQty);

        // Invariant 4: Supplier account credited $120 payable reduction
        var supplierEntries = _fakes.SupplierAccounts.Entries.Where(e => e.SupplierId == supplier.Id).ToList();
        Assert.Single(supplierEntries);
        var creditEntry = supplierEntries[0];
        Assert.Equal(SupplierAccountEntryType.WarrantyCredit, creditEntry.EntryType);
        Assert.Equal(SupplierAccountDirection.DecreasePayable, creditEntry.Direction);
        Assert.Equal(120m, creditEntry.Amount);

        // Invariant 5: Recovery difference is exactly $20 ($120 credit - $100 carrying cost)
        var warrantyCase = await _fakes.Warranty.GetShopStockCaseAsync(caseId, CancellationToken.None);
        Assert.NotNull(warrantyCase);
        Assert.Equal(100m, warrantyCase!.InventoryCarryingCostResolved);
        Assert.Equal(120m, warrantyCase.SupplierCreditAmount);
        Assert.Equal(20m, warrantyCase.RecoveryDifference);
    }

    [Fact]
    public async Task P11_IMP_087_HostileCases_TerminalResolutionAndMixedSuppliers_Rejected()
    {
        var supplierA = new Supplier { Id = Guid.NewGuid(), Name = "Supplier Alpha", DealerCode = "SA1", IsActive = true };
        var supplierB = new Supplier { Id = Guid.NewGuid(), Name = "Supplier Beta", DealerCode = "SB1", IsActive = true };
        _fakes.Parties.AddSupplier(supplierA);
        _fakes.Parties.AddSupplier(supplierB);

        var customer = new Customer { Id = Guid.NewGuid(), Name = "Test Customer", IsActive = true };
        _fakes.Parties.AddCustomer(customer);

        var product = new Product
        {
            Id = Guid.NewGuid(),
            Name = "Tracked Device",
            Sku = "DEV-01",
            TrackingMode = TrackingMode.Serialized,
            SerialTrackingEnabled = true,
            DefaultWarrantyMonths = 12,
            IsActive = true
        };
        _fakes.Catalog.AddProduct(product);

        var spA = new SupplierProduct { Id = Guid.NewGuid(), SupplierId = supplierA.Id, ProductId = product.Id, NextItemSequence = 2, IsActive = true };
        var spB = new SupplierProduct { Id = Guid.NewGuid(), SupplierId = supplierB.Id, ProductId = product.Id, NextItemSequence = 2, IsActive = true };
        _fakes.Traceability.AddSupplierProduct(spA);
        _fakes.Traceability.AddSupplierProduct(spB);

        var unitA = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            ProductId = product.Id,
            SupplierProductId = spA.Id,
            TrackingCode = "SA1-DEV-000001",
            SerialNumber = "SN-A-01",
            Status = InventoryUnitStatus.Sold,
            AcquisitionCost = 200m
        };
        var unitB = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            ProductId = product.Id,
            SupplierProductId = spB.Id,
            TrackingCode = "SB1-DEV-000001",
            SerialNumber = "SN-B-01",
            Status = InventoryUnitStatus.Sold,
            AcquisitionCost = 200m
        };
        _fakes.Inventory.Units.Add(unitA);
        _fakes.Inventory.Units.Add(unitB);

        var sale = new Sale { Id = Guid.NewGuid(), CustomerId = customer.Id, Status = SaleStatus.Completed };
        _fakes.Sales.AddSale(sale);

        var saleItem = new SaleItem
        {
            Id = Guid.NewGuid(),
            SaleId = sale.Id,
            ProductId = product.Id,
            BaseQuantity = 2m,
            WarrantyValidUntil = _fakes.Clock.ShopDate.AddMonths(12)
        };
        _fakes.Sales.AddSaleItem(saleItem);

        _fakes.Sales.AddSaleItemUnit(new SaleItemUnit { SaleItemId = saleItem.Id, InventoryUnitId = unitA.Id, WarrantyValidUntil = saleItem.WarrantyValidUntil });
        _fakes.Sales.AddSaleItemUnit(new SaleItemUnit { SaleItemId = saleItem.Id, InventoryUnitId = unitB.Id, WarrantyValidUntil = saleItem.WarrantyValidUntil });

        var handler = CreateClaimHandler();

        // Hostile Case 1: Mixed suppliers in a single claim must fail closed
        var mixedCmd = new CreateWarrantyClaimCommand(
            customer.Id,
            sale.Id,
            Guid.NewGuid(),
            Guid.NewGuid(),
            new[]
            {
                new WarrantyClaimItemInput(
                    product.Id,
                    Quantity: 2m,
                    FaultDescription: "Both units failing",
                    OriginalSaleItemId: saleItem.Id,
                    Units: new[]
                    {
                        new WarrantyClaimUnitInput(unitA.Id, unitA.TrackingCode),
                        new WarrantyClaimUnitInput(unitB.Id, unitB.TrackingCode)
                    })
            });

        var mixedResult = await handler.HandleAsync(mixedCmd, CancellationToken.None);
        Assert.False(mixedResult.IsSuccess);
        Assert.Equal("warranty.mixed_supplier_claim", mixedResult.Error?.Code);

        // Hostile Case 2: Unit already terminally resolved cannot be claimed again
        var pastClosedClaim = new WarrantyClaim
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            SupplierId = supplierA.Id,
            Status = WarrantyClaimStatus.Closed,
            CurrentCustody = WarrantyCustody.WithCustomer
        };
        _fakes.Warranty.AddClaim(pastClosedClaim);

        var pastClaimItem = new WarrantyClaimItem
        {
            Id = Guid.NewGuid(),
            ClaimId = pastClosedClaim.Id,
            ProductId = product.Id,
            Quantity = 1m,
            FaultDescription = "Old resolved claim"
        };
        _fakes.Warranty.AddClaimItem(pastClaimItem);

        _fakes.Warranty.AddClaimItemUnit(new WarrantyClaimItemUnit
        {
            Id = Guid.NewGuid(),
            ClaimItemId = pastClaimItem.Id,
            OriginalInventoryUnitId = unitA.Id,
            OriginalIdentitySnapshot = unitA.TrackingCode
        });

        var terminallyResolvedCmd = new CreateWarrantyClaimCommand(
            customer.Id,
            sale.Id,
            Guid.NewGuid(),
            Guid.NewGuid(),
            new[]
            {
                new WarrantyClaimItemInput(
                    product.Id,
                    Quantity: 1m,
                    FaultDescription: "Attempt to claim previously replaced unit",
                    OriginalSaleItemId: saleItem.Id,
                    Units: new[] { new WarrantyClaimUnitInput(unitA.Id, unitA.TrackingCode) })
            });

        var termResult = await handler.HandleAsync(terminallyResolvedCmd, CancellationToken.None);
        Assert.False(termResult.IsSuccess);
        Assert.Equal("warranty.unit_terminally_resolved", termResult.Error?.Code);
    }
}
