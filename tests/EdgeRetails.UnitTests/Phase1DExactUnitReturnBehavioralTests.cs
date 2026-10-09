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

public sealed class Phase1DExactUnitReturnBehavioralTests
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
            _fakes.UnitOfWork,
            _fakes.Warranty);

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
            _fakes.UnitOfWork, outcomeLedger: _fakes.OutcomeLedger, physicalUnitCreationAuthority: _fakes.PhysicalUnits);

    private CreatePurchaseReturnHandler CreatePurchaseReturnHandler() =>
        new(
            _fakes.Purchasing,
            _fakes.Inventory,
            _fakes.CostAllocator,
            _fakes.SupplierAccounts,
            _fakes.CashMovements,
            _fakes.OperationLock,
            _fakes.ResourceLock,
            _fakes.Audit,
            _fakes.Numbers,
            _fakes.Clock,
            _fakes.Transactions,
            _fakes.Authorization,
            _fakes.UnitOfWork);

    private CreateWarrantyClaimHandler CreateWarrantyClaimHandler() =>
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
            _fakes.UnitOfWork);

    private (Supplier Supplier, Product Product, ProductUnit ProductUnit) SeedSupplierAndProduct(
        string dealerCode = "AB1",
        string supplierName = "Alpha Supplier",
        string sku = "DLX56")
    {
        var supplier = new Supplier
        {
            Id = Guid.CreateVersion7(),
            DealerCode = dealerCode,
            Name = supplierName,
            IsActive = true
        };
        _fakes.Parties.AddSupplier(supplier);

        var unit = new Unit { Id = Guid.CreateVersion7(), Name = "Piece", Symbol = "pc" };
        var product = new Product
        {
            Id = Guid.CreateVersion7(),
            Name = "Deluxe Electronics 56",
            Sku = sku,
            BaseUnitId = unit.Id,
            TrackingMode = TrackingMode.Serialized,
            SerialTrackingEnabled = true,
            ImeiTrackingEnabled = false,
            DefaultSalePrice = 1200m,
            ReferencePurchaseCost = 800m,
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

    private Customer SeedCustomer(string name = "John Doe")
    {
        var customer = new Customer
        {
            Id = Guid.CreateVersion7(),
            Name = name,
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

    private async Task<(Purchase Purchase, PurchaseItem Item, InventoryUnit Unit)> SeedReceivedSerializedUnitAsync(
        Supplier supplier,
        Product product,
        ProductUnit pu,
        string trackingCode = "AB1-PKF-DLX56-000002",
        long itemSequence = 2,
        string serialNumber = "SN-DLX-002",
        decimal cost = 800m)
    {
        var purchaseHandler = CreatePurchaseHandler();
        var actorId = Guid.CreateVersion7();

        var purchaseResult = await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                supplier.Id,
                $"INV-{Guid.NewGuid():N}"[..10],
                DateOnly.FromDateTime(DateTime.UtcNow),
                "Initial inventory intake",
                0m,
                PurchaseSettlementMode.External,
                actorId,
                Guid.CreateVersion7(),
                [
                    new CreatePurchaseLineInput(
                        product.Id,
                        pu.Id,
                        1m,
                        cost,
                        1200m,
                        [
                            new SerializedIdentityInput(
                                SerialNumber: serialNumber,
                                Imei1: null,
                                Imei2: null)
                        ])
                ]),
            CancellationToken.None);

        Assert.True(purchaseResult.IsSuccess, purchaseResult.Error?.Message);

        var purchase = _fakes.Purchasing.Purchases[purchaseResult.Value!.PurchaseId];
        var item = _fakes.Purchasing.PurchaseItems.Single(i => i.PurchaseId == purchase.Id);
        var unit = _fakes.Inventory.Units.Single(u => u.SourcePurchaseItemId == item.Id);

        // Explicitly set tracking code and sequence to the exact test canonical format
        unit.TrackingCode = trackingCode;
        unit.ItemSequence = itemSequence;

        return (purchase, item, unit);
    }

    private async Task<(Sale Sale, SaleItem Item, InventoryUnit Unit)> SeedSoldSerializedUnitAsync(
        Supplier supplier,
        Product product,
        ProductUnit pu,
        Customer customer,
        CashSession session,
        Guid cashierId,
        string trackingCode = "AB1-PKF-DLX56-000002",
        long itemSequence = 2,
        string serialNumber = "SN-DLX-002",
        decimal salePrice = 1200m)
    {
        var (_, _, unit) = await SeedReceivedSerializedUnitAsync(
            supplier, product, pu, trackingCode, itemSequence, serialNumber);

        var saleHandler = CreateSaleHandler();
        var saleResult = await saleHandler.HandleAsync(
            new CompleteSaleCommand(
                Guid.CreateVersion7(),
                customer.Id,
                cashierId,
                session.Id,
                InvoiceDiscount: 0m,
                PaymentMethod: SalePaymentMethod.Cash,
                AmountTendered: salePrice,
                PaymentReference: null,
                QuotationId: null,
                Lines:
                [
                    new CompleteSaleLineInput(
                        product.Id,
                        pu.Id,
                        1m,
                        salePrice,
                        [unit.Id])
                ]),
            CancellationToken.None);

        Assert.True(saleResult.IsSuccess, saleResult.Error?.Message);

        var sale = _fakes.Sales.Sales[saleResult.Value!.SaleId];
        var saleItem = _fakes.Sales.SaleItems.Single(i => i.SaleId == sale.Id);

        // Add warranty snapshots on sale item and unit for warranty eligibility
        saleItem.WarrantyValidUntil = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(365));
        var saleUnit = _fakes.Sales.SaleItemUnits.Single(u => u.SaleItemId == saleItem.Id);
        saleUnit.WarrantyValidUntil = saleItem.WarrantyValidUntil;

        return (sale, saleItem, unit);
    }

    [Fact]
    public async Task SaleReturn_UsesOriginalInventoryUnit()
    {
        // Arrange
        var (supplier, product, pu) = SeedSupplierAndProduct();
        var customer = SeedCustomer();
        var cashierId = Guid.CreateVersion7();
        var session = OpenCashSession(cashierId);
        var (sale, saleItem, soldUnit) = await SeedSoldSerializedUnitAsync(
            supplier, product, pu, customer, session, cashierId,
            trackingCode: "AB1-PKF-DLX56-000002", itemSequence: 2);

        var originalUnitCount = _fakes.Inventory.Units.Count;
        var returnHandler = CreateSaleReturnHandler();

        // Act
        var returnResult = await returnHandler.HandleAsync(
            new CreateSaleReturnCommand(
                sale.Id,
                "CUSTOMER_MIND_CHANGED",
                "Customer returned unit",
                RefundMethod.Cash,
                cashierId,
                Guid.CreateVersion7(),
                [
                    new SaleReturnLineInput(saleItem.Id, 1m, SaleReturnDisposition.RestockSellable, [soldUnit.Id])
                ]),
            CancellationToken.None);

        // Assert
        Assert.True(returnResult.IsSuccess, returnResult.Error?.Message);

        // Verify SaleReturnItemUnit points to the exact original InventoryUnit
        var returnItem = _fakes.Sales.SaleReturnItems.Single(r => r.SaleReturnId == returnResult.Value!.SaleReturnId);
        var returnItemUnit = _fakes.Sales.SaleReturnItemUnits.Single(u => u.SaleReturnItemId == returnItem.Id);
        Assert.Equal(soldUnit.Id, returnItemUnit.InventoryUnitId);

        // Verify no duplicate/new InventoryUnit was created
        Assert.Equal(originalUnitCount, _fakes.Inventory.Units.Count);
        var updatedUnit = _fakes.Inventory.Units.Single(u => u.Id == soldUnit.Id);
        Assert.Same(soldUnit, updatedUnit);
        Assert.Equal(soldUnit.Id, updatedUnit.Id);
    }

    [Fact]
    public async Task SaleReturn_PreservesTrackingCode()
    {
        // Arrange
        var (supplier, product, pu) = SeedSupplierAndProduct();
        var customer = SeedCustomer();
        var cashierId = Guid.CreateVersion7();
        var session = OpenCashSession(cashierId);
        const string expectedTrackingCode = "AB1-PKF-DLX56-000002";
        const long expectedItemSequence = 2;
        const string expectedSerialNumber = "SN-DLX-002";

        var (sale, saleItem, soldUnit) = await SeedSoldSerializedUnitAsync(
            supplier, product, pu, customer, session, cashierId,
            trackingCode: expectedTrackingCode, itemSequence: expectedItemSequence, serialNumber: expectedSerialNumber);

        var returnHandler = CreateSaleReturnHandler();

        // Act
        var returnResult = await returnHandler.HandleAsync(
            new CreateSaleReturnCommand(
                sale.Id,
                "WRONG_COLOR",
                "Returned for restock",
                RefundMethod.Cash,
                cashierId,
                Guid.CreateVersion7(),
                [
                    new SaleReturnLineInput(saleItem.Id, 1m, SaleReturnDisposition.RestockSellable, [soldUnit.Id])
                ]),
            CancellationToken.None);

        // Assert
        Assert.True(returnResult.IsSuccess, returnResult.Error?.Message);

        var returnedUnit = _fakes.Inventory.Units.Single(u => u.Id == soldUnit.Id);
        Assert.Equal(expectedTrackingCode, returnedUnit.TrackingCode);
        Assert.Equal(expectedItemSequence, returnedUnit.ItemSequence);
        Assert.Equal(expectedSerialNumber, returnedUnit.SerialNumber);
    }

    [Fact]
    public async Task SameExactUnit_CannotBeReturnedTwice()
    {
        // Arrange
        var (supplier, product, pu) = SeedSupplierAndProduct();
        var customer = SeedCustomer();
        var cashierId = Guid.CreateVersion7();
        var session = OpenCashSession(cashierId);
        var (sale, saleItem, soldUnit) = await SeedSoldSerializedUnitAsync(
            supplier, product, pu, customer, session, cashierId);

        var returnHandler = CreateSaleReturnHandler();

        // Act 1: First return succeeds
        var firstReturn = await returnHandler.HandleAsync(
            new CreateSaleReturnCommand(
                sale.Id,
                "RETURN_ONCE",
                "First return",
                RefundMethod.Cash,
                cashierId,
                Guid.CreateVersion7(),
                [
                    new SaleReturnLineInput(saleItem.Id, 1m, SaleReturnDisposition.RestockSellable, [soldUnit.Id])
                ]),
            CancellationToken.None);
        Assert.True(firstReturn.IsSuccess, firstReturn.Error?.Message);

        // Act 2: Second attempt to return the exact same physical unit must fail
        var secondReturn = await returnHandler.HandleAsync(
            new CreateSaleReturnCommand(
                sale.Id,
                "RETURN_TWICE",
                "Duplicate return attempt",
                RefundMethod.Cash,
                cashierId,
                Guid.CreateVersion7(),
                [
                    new SaleReturnLineInput(saleItem.Id, 1m, SaleReturnDisposition.RestockSellable, [soldUnit.Id])
                ]),
            CancellationToken.None);

        // Assert
        Assert.False(secondReturn.IsSuccess);
        Assert.True(
            secondReturn.Error?.Code == "sales.return_serial_already_returned" ||
            secondReturn.Error?.Code == "sales.return_exceeds_original" ||
            secondReturn.Error?.Code == "sales.return_serial_not_eligible",
            $"Expected return rejection but got {secondReturn.Error?.Code}: {secondReturn.Error?.Message}");
    }

    [Fact]
    public async Task RestockReturn_ReactivatesSameInventoryUnit()
    {
        // Arrange
        var (supplier, product, pu) = SeedSupplierAndProduct();
        var customer = SeedCustomer();
        var cashierId = Guid.CreateVersion7();
        var session = OpenCashSession(cashierId);
        var (sale, saleItem, soldUnit) = await SeedSoldSerializedUnitAsync(
            supplier, product, pu, customer, session, cashierId);

        Assert.Equal(InventoryUnitStatus.Sold, soldUnit.Status);
        Assert.NotNull(soldUnit.InventoryLotId);

        var returnHandler = CreateSaleReturnHandler();

        // Act: Restock return
        var returnResult = await returnHandler.HandleAsync(
            new CreateSaleReturnCommand(
                sale.Id,
                "RESTOCK",
                "Reactivate unit",
                RefundMethod.Cash,
                cashierId,
                Guid.CreateVersion7(),
                [
                    new SaleReturnLineInput(saleItem.Id, 1m, SaleReturnDisposition.RestockSellable, [soldUnit.Id])
                ]),
            CancellationToken.None);

        // Assert
        Assert.True(returnResult.IsSuccess, returnResult.Error?.Message);
        var reactivated = _fakes.Inventory.Units.Single(u => u.Id == soldUnit.Id);
        Assert.Equal(InventoryUnitStatus.InStock, reactivated.Status);
        Assert.NotNull(reactivated.InventoryLotId);
        Assert.Equal(1m, _fakes.Inventory.Balances[product.Id].SellableQty);

        // Resell the exact same reactivated unit at POS
        var saleHandler = CreateSaleHandler();
        var resellResult = await saleHandler.HandleAsync(
            new CompleteSaleCommand(
                Guid.CreateVersion7(),
                customer.Id,
                cashierId,
                session.Id,
                InvoiceDiscount: 0m,
                PaymentMethod: SalePaymentMethod.Cash,
                AmountTendered: 1200m,
                PaymentReference: null,
                QuotationId: null,
                Lines:
                [
                    new CompleteSaleLineInput(product.Id, pu.Id, 1m, 1200m, [reactivated.Id])
                ]),
            CancellationToken.None);

        Assert.True(resellResult.IsSuccess, resellResult.Error?.Message);
        Assert.Equal(InventoryUnitStatus.Sold, reactivated.Status);
    }

    [Fact]
    public async Task DamagedReturn_DoesNotBecomeSellable()
    {
        // Arrange
        var (supplier, product, pu) = SeedSupplierAndProduct();
        var customer = SeedCustomer();
        var cashierId = Guid.CreateVersion7();
        var session = OpenCashSession(cashierId);
        var (sale, saleItem, soldUnit) = await SeedSoldSerializedUnitAsync(
            supplier, product, pu, customer, session, cashierId);

        var returnHandler = CreateSaleReturnHandler();

        // Act: Damaged disposition
        var returnResult = await returnHandler.HandleAsync(
            new CreateSaleReturnCommand(
                sale.Id,
                "DEFECTIVE_SCREEN",
                "Screen cracked upon return",
                RefundMethod.Cash,
                cashierId,
                Guid.CreateVersion7(),
                [
                    new SaleReturnLineInput(saleItem.Id, 1m, SaleReturnDisposition.Damaged, [soldUnit.Id])
                ]),
            CancellationToken.None);

        // Assert
        Assert.True(returnResult.IsSuccess, returnResult.Error?.Message);
        var damagedUnit = _fakes.Inventory.Units.Single(u => u.Id == soldUnit.Id);
        Assert.Equal(InventoryUnitStatus.Damaged, damagedUnit.Status);

        // Sellable stock must NOT have increased
        Assert.Equal(0m, _fakes.Inventory.Balances[product.Id].SellableQty);
        Assert.Equal(1m, _fakes.Inventory.Balances[product.Id].DamagedQty);

        // Attempting to sell this damaged unit at POS must be rejected
        var saleHandler = CreateSaleHandler();
        var sellAttempt = await saleHandler.HandleAsync(
            new CompleteSaleCommand(
                Guid.CreateVersion7(),
                customer.Id,
                cashierId,
                session.Id,
                InvoiceDiscount: 0m,
                PaymentMethod: SalePaymentMethod.Cash,
                AmountTendered: 1200m,
                PaymentReference: null,
                QuotationId: null,
                Lines:
                [
                    new CompleteSaleLineInput(product.Id, pu.Id, 1m, 1200m, [damagedUnit.Id])
                ]),
            CancellationToken.None);

        Assert.False(sellAttempt.IsSuccess);
        Assert.Equal("sales.serial_not_sellable", sellAttempt.Error?.Code);
    }

    [Fact]
    public async Task PurchaseReturn_RequiresOriginalSupplierProvenance()
    {
        // Arrange: Unit without SourcePurchaseItemId (e.g. warranty replacement origin)
        var (supplier, product, pu) = SeedSupplierAndProduct();
        var (purchase, purchaseItem, _) = await SeedReceivedSerializedUnitAsync(supplier, product, pu);

        var unprovenancedUnit = new InventoryUnit
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            OriginType = InventoryUnitOriginType.WarrantyReplacement,
            SourcePurchaseItemId = null,
            TrackingCode = "AB1-DLX56-ORPHAN",
            Status = InventoryUnitStatus.InStock,
            AcquisitionCost = 800m,
            InventoryLotId = Guid.CreateVersion7(),
            CreatedAt = DateTimeOffset.UtcNow
        };
        _fakes.Inventory.AddInventoryUnit(unprovenancedUnit);

        var returnHandler = CreatePurchaseReturnHandler();

        // Act
        var result = await returnHandler.HandleAsync(
            new CreatePurchaseReturnCommand(
                purchase.Id,
                "RETURN_ORPHAN",
                "Attempt to return unprovenanced unit",
                PurchaseReturnSettlementMode.External,
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                [
                    new PurchaseReturnLineInput(purchaseItem.Id, 1m, null, [unprovenancedUnit.Id])
                ]),
            CancellationToken.None);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal("purchasing.return_requires_supplier_provenance", result.Error?.Code);
    }

    [Fact]
    public async Task PurchaseReturn_WrongSupplier_IsRejected()
    {
        // Arrange: Two distinct suppliers supplying the same product
        var (supplierA, product, pu) = SeedSupplierAndProduct("AB1", "Supplier Alpha", "SKU-SHARED");
        var supplierB = new Supplier
        {
            Id = Guid.CreateVersion7(),
            DealerCode = "AB2",
            Name = "Supplier Beta",
            IsActive = true
        };
        _fakes.Parties.AddSupplier(supplierB);

        // Unit A received from Supplier A
        var (purchaseA, itemA, unitA) = await SeedReceivedSerializedUnitAsync(
            supplierA, product, pu, trackingCode: "AB1-SHARED-000001", itemSequence: 1);

        // Unit B received from Supplier B
        var (purchaseB, itemB, _) = await SeedReceivedSerializedUnitAsync(
            supplierB, product, pu, trackingCode: "AB2-SHARED-000001", itemSequence: 1, serialNumber: "SN-SUPPLIER-B");

        var returnHandler = CreatePurchaseReturnHandler();

        // Act: Attempt to return unitA against purchaseB (Supplier B)
        var crossSupplierReturn = await returnHandler.HandleAsync(
            new CreatePurchaseReturnCommand(
                purchaseB.Id,
                "WRONG_SUPPLIER_RETURN",
                "Returning Unit A to Supplier B",
                PurchaseReturnSettlementMode.External,
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                [
                    new PurchaseReturnLineInput(itemB.Id, 1m, null, [unitA.Id])
                ]),
            CancellationToken.None);

        // Assert: Must reject wrong supplier
        Assert.False(crossSupplierReturn.IsSuccess);
        Assert.Equal("purchasing.return_wrong_supplier", crossSupplierReturn.Error?.Code);

        // Unit A must remain InStock
        Assert.Equal(InventoryUnitStatus.InStock, _fakes.Inventory.Units.Single(u => u.Id == unitA.Id).Status);
    }

    [Fact]
    public async Task SupplierReturnedUnit_CannotBeSold()
    {
        // Arrange
        var (supplier, product, pu) = SeedSupplierAndProduct();
        var (purchase, purchaseItem, unit) = await SeedReceivedSerializedUnitAsync(supplier, product, pu);

        var returnHandler = CreatePurchaseReturnHandler();
        var cashierId = Guid.CreateVersion7();
        var session = OpenCashSession(cashierId);
        var customer = SeedCustomer();

        // Act: Return unit to supplier
        var returnResult = await returnHandler.HandleAsync(
            new CreatePurchaseReturnCommand(
                purchase.Id,
                "DEFECTIVE_BATCH",
                "Returned to supplier",
                PurchaseReturnSettlementMode.External,
                cashierId,
                Guid.CreateVersion7(),
                [
                    new PurchaseReturnLineInput(purchaseItem.Id, 1m, null, [unit.Id])
                ]),
            CancellationToken.None);

        Assert.True(returnResult.IsSuccess, returnResult.Error?.Message);
        Assert.Equal(InventoryUnitStatus.SupplierReturned, _fakes.Inventory.Units.Single(u => u.Id == unit.Id).Status);

        // Attempt POS sale of supplier-returned unit
        var saleHandler = CreateSaleHandler();
        var saleAttempt = await saleHandler.HandleAsync(
            new CompleteSaleCommand(
                Guid.CreateVersion7(),
                customer.Id,
                cashierId,
                session.Id,
                InvoiceDiscount: 0m,
                PaymentMethod: SalePaymentMethod.Cash,
                AmountTendered: 1200m,
                PaymentReference: null,
                QuotationId: null,
                Lines:
                [
                    new CompleteSaleLineInput(product.Id, pu.Id, 1m, 1200m, [unit.Id])
                ]),
            CancellationToken.None);

        // Assert: Rejected from sale
        Assert.False(saleAttempt.IsSuccess);
        Assert.Equal("sales.serial_not_sellable", saleAttempt.Error?.Code);
    }

    [Fact]
    public async Task PurchaseReturn_DoesNotAllocateNewSequence()
    {
        // Arrange
        var (supplier, product, pu) = SeedSupplierAndProduct();
        const string initialTracking = "AB1-PKF-DLX56-000005";
        const long initialSequence = 5;

        var (purchase, purchaseItem, unit) = await SeedReceivedSerializedUnitAsync(
            supplier, product, pu, trackingCode: initialTracking, itemSequence: initialSequence);

        var initialUnitCount = _fakes.Inventory.Units.Count;
        var returnHandler = CreatePurchaseReturnHandler();

        // Act
        var returnResult = await returnHandler.HandleAsync(
            new CreatePurchaseReturnCommand(
                purchase.Id,
                "EXCESS_STOCK",
                "Return to vendor",
                PurchaseReturnSettlementMode.External,
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                [
                    new PurchaseReturnLineInput(purchaseItem.Id, 1m, null, [unit.Id])
                ]),
            CancellationToken.None);

        // Assert
        Assert.True(returnResult.IsSuccess, returnResult.Error?.Message);

        // Unit count must not increase (no new InventoryUnit inserted)
        Assert.Equal(initialUnitCount, _fakes.Inventory.Units.Count);

        // Sequence must NOT be recycled or changed; tracking code remains historical
        var returnedUnit = _fakes.Inventory.Units.Single(u => u.Id == unit.Id);
        Assert.Equal(initialSequence, returnedUnit.ItemSequence);
        Assert.Equal(initialTracking, returnedUnit.TrackingCode);
        Assert.Equal(InventoryUnitStatus.SupplierReturned, returnedUnit.Status);
    }

    [Fact]
    public async Task ConcurrentReturnAndSale_ProducesOneValidOutcome()
    {
        // Arrange: Unit in stock
        var (supplier, product, pu) = SeedSupplierAndProduct();
        var (purchase, purchaseItem, unit) = await SeedReceivedSerializedUnitAsync(supplier, product, pu);

        var cashierId = Guid.CreateVersion7();
        var session = OpenCashSession(cashierId);
        var customer = SeedCustomer();

        var saleHandler = CreateSaleHandler();
        var purchaseReturnHandler = CreatePurchaseReturnHandler();

        // Simulate concurrent race where PurchaseReturn wins first
        var returnResult = await purchaseReturnHandler.HandleAsync(
            new CreatePurchaseReturnCommand(
                purchase.Id,
                "RETURN_RACE",
                "Winner of race",
                PurchaseReturnSettlementMode.External,
                cashierId,
                Guid.CreateVersion7(),
                [
                    new PurchaseReturnLineInput(purchaseItem.Id, 1m, null, [unit.Id])
                ]),
            CancellationToken.None);

        Assert.True(returnResult.IsSuccess, returnResult.Error?.Message);

        // Second serialized operation (POS Sale) must fail closed
        var saleResult = await saleHandler.HandleAsync(
            new CompleteSaleCommand(
                Guid.CreateVersion7(),
                customer.Id,
                cashierId,
                session.Id,
                InvoiceDiscount: 0m,
                PaymentMethod: SalePaymentMethod.Cash,
                AmountTendered: 1200m,
                PaymentReference: null,
                QuotationId: null,
                Lines:
                [
                    new CompleteSaleLineInput(product.Id, pu.Id, 1m, 1200m, [unit.Id])
                ]),
            CancellationToken.None);

        Assert.False(saleResult.IsSuccess);
        Assert.Equal("sales.serial_not_sellable", saleResult.Error?.Code);

        // Exactly one valid state: unit is SupplierReturned, never double-processed
        Assert.Equal(InventoryUnitStatus.SupplierReturned, _fakes.Inventory.Units.Single(u => u.Id == unit.Id).Status);
        Assert.Equal(0m, _fakes.Inventory.Balances[product.Id].SellableQty);
    }

    [Fact]
    public async Task ConcurrentSaleReturnAndWarrantyTransition_IsSerializedOrRejected()
    {
        // Scenario A: SaleReturn commits first -> Warranty claim is rejected
        {
            var fakesA = new Phase2TestDoubles();
            var supplier = new Supplier { Id = Guid.CreateVersion7(), DealerCode = "AB1", Name = "Supplier A", IsActive = true };
            fakesA.Parties.AddSupplier(supplier);
            var customer = new Customer { Id = Guid.CreateVersion7(), Name = "Cust A", IsActive = true };
            fakesA.Parties.AddCustomer(customer);
            var unit = new Unit { Id = Guid.CreateVersion7(), Name = "Piece", Symbol = "pc" };
            var product = new Product { Id = Guid.CreateVersion7(), Name = "Gadget", Sku = "G1", BaseUnitId = unit.Id, TrackingMode = TrackingMode.Serialized, DefaultSalePrice = 100m, ReferencePurchaseCost = 50m, IsActive = true };
            fakesA.Catalog.Products[product.Id] = product;
            var pu = new ProductUnit { Id = Guid.CreateVersion7(), ProductId = product.Id, UnitId = unit.Id, FactorToBaseUnit = 1m, CanSell = true, CanPurchase = true, IsDefaultSaleUnit = true, IsDefaultPurchaseUnit = true };
            fakesA.Catalog.AddProductUnit(pu);

            var invLot = new InventoryLot { Id = Guid.CreateVersion7(), ProductId = product.Id, ReceivedQuantity = 1m, EffectiveUnitCost = 50m };
            fakesA.Inventory.AddLot(invLot);
            fakesA.Inventory.AddLotBucketBalance(new InventoryLotBucketBalance { LotId = invLot.Id, StockBucket = InventoryBucket.Sellable, Quantity = 1m });
            fakesA.Inventory.AddStockBalance(new StockBalance { ProductId = product.Id, SellableQty = 1m });

            var spA = new SupplierProduct
            {
                Id = Guid.CreateVersion7(),
                SupplierId = supplier.Id,
                ProductId = product.Id,
                IsActive = true
            };
            fakesA.Traceability.AddSupplierProduct(spA);

            var invUnit = new InventoryUnit { Id = Guid.CreateVersion7(), ProductId = product.Id, InventoryLotId = invLot.Id, Status = InventoryUnitStatus.InStock, TrackingCode = "AB1-G1-000001", ItemSequence = 1, SupplierProductId = spA.Id };
            fakesA.Inventory.AddInventoryUnit(invUnit);

            var cashier = Guid.CreateVersion7();
            var session = new CashSession { Id = Guid.CreateVersion7(), OpenedBy = cashier, Status = CashSessionStatus.Open, BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow) };
            fakesA.Cash.AddSession(session);

            var saleHandler = new CompleteSaleHandler(fakesA.Sales, fakesA.Quotations, fakesA.Parties, fakesA.Catalog, fakesA.Inventory, fakesA.CostAllocator, fakesA.Cash, fakesA.OperationLock, fakesA.ResourceLock, fakesA.Audit, fakesA.ReceiptSnapshots, fakesA.Numbers, fakesA.Clock, fakesA.Transactions, fakesA.Authorization, fakesA.UnitOfWork);
            var saleRes = await saleHandler.HandleAsync(new CompleteSaleCommand(Guid.CreateVersion7(), customer.Id, cashier, session.Id, 0m, SalePaymentMethod.Cash, 100m, null, null, [new CompleteSaleLineInput(product.Id, pu.Id, 1m, 100m, [invUnit.Id])]), CancellationToken.None);
            Assert.True(saleRes.IsSuccess);

            var saleItem = fakesA.Sales.SaleItems.Single();
            saleItem.WarrantyValidUntil = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(180));
            fakesA.Sales.SaleItemUnits.Single().WarrantyValidUntil = saleItem.WarrantyValidUntil;

            // Step 1: SaleReturn executes
            var returnHandler = new CreateSaleReturnHandler(fakesA.Sales, fakesA.Inventory, fakesA.CostAllocator, fakesA.Cash, fakesA.OperationLock, fakesA.ResourceLock, fakesA.Audit, fakesA.Numbers, fakesA.Clock, fakesA.Transactions, fakesA.Authorization, fakesA.UnitOfWork, fakesA.Warranty);
            var returnRes = await returnHandler.HandleAsync(new CreateSaleReturnCommand(saleRes.Value!.SaleId, "RETURN", null, RefundMethod.Cash, cashier, Guid.CreateVersion7(), [new SaleReturnLineInput(saleItem.Id, 1m, SaleReturnDisposition.RestockSellable, [invUnit.Id])]), CancellationToken.None);
            Assert.True(returnRes.IsSuccess);

            // Step 2: Customer Warranty Claim arrives afterwards for the returned unit
            var claimHandler = new CreateWarrantyClaimHandler(fakesA.Catalog, fakesA.Parties, fakesA.Sales, fakesA.Purchasing, fakesA.Inventory, fakesA.Traceability, fakesA.Warranty, fakesA.ResourceLock, fakesA.Authorization, fakesA.Numbers, fakesA.Clock, fakesA.Transactions, fakesA.UnitOfWork);
            var claimRes = await claimHandler.HandleAsync(new CreateWarrantyClaimCommand(customer.Id, saleRes.Value.SaleId, supplier.Id, cashier, [new WarrantyClaimItemInput(product.Id, 1m, "Faulty screen", saleItem.Id, saleItem.WarrantyValidUntil, [new WarrantyClaimUnitInput(invUnit.Id, invUnit.TrackingCode)])], Guid.CreateVersion7()), CancellationToken.None);

            // Assert: Warranty claim rejected because unit is no longer customer-held (already returned)
            Assert.False(claimRes.IsSuccess);
            Assert.Equal("warranty.unit_no_longer_customer_owned", claimRes.Error?.Code);
        }

        // Scenario B: Warranty Claim commits first (active custody) -> SaleReturn is rejected
        {
            var fakesB = new Phase2TestDoubles();
            var supplier = new Supplier { Id = Guid.CreateVersion7(), DealerCode = "AB1", Name = "Supplier B", IsActive = true };
            fakesB.Parties.AddSupplier(supplier);
            var customer = new Customer { Id = Guid.CreateVersion7(), Name = "Cust B", IsActive = true };
            fakesB.Parties.AddCustomer(customer);
            var unit = new Unit { Id = Guid.CreateVersion7(), Name = "Piece", Symbol = "pc" };
            var product = new Product { Id = Guid.CreateVersion7(), Name = "Gadget", Sku = "G2", BaseUnitId = unit.Id, TrackingMode = TrackingMode.Serialized, DefaultSalePrice = 100m, ReferencePurchaseCost = 50m, IsActive = true };
            fakesB.Catalog.Products[product.Id] = product;
            var pu = new ProductUnit { Id = Guid.CreateVersion7(), ProductId = product.Id, UnitId = unit.Id, FactorToBaseUnit = 1m, CanSell = true, CanPurchase = true, IsDefaultSaleUnit = true, IsDefaultPurchaseUnit = true };
            fakesB.Catalog.AddProductUnit(pu);

            var invLot = new InventoryLot { Id = Guid.CreateVersion7(), ProductId = product.Id, ReceivedQuantity = 1m, EffectiveUnitCost = 50m };
            fakesB.Inventory.AddLot(invLot);
            fakesB.Inventory.AddLotBucketBalance(new InventoryLotBucketBalance { LotId = invLot.Id, StockBucket = InventoryBucket.Sellable, Quantity = 1m });
            fakesB.Inventory.AddStockBalance(new StockBalance { ProductId = product.Id, SellableQty = 1m });

            var spB = new SupplierProduct
            {
                Id = Guid.CreateVersion7(),
                SupplierId = supplier.Id,
                ProductId = product.Id,
                IsActive = true
            };
            fakesB.Traceability.AddSupplierProduct(spB);

            var invUnit = new InventoryUnit { Id = Guid.CreateVersion7(), ProductId = product.Id, InventoryLotId = invLot.Id, Status = InventoryUnitStatus.InStock, TrackingCode = "AB1-G2-000001", ItemSequence = 1, SupplierProductId = spB.Id };
            fakesB.Inventory.AddInventoryUnit(invUnit);

            var cashier = Guid.CreateVersion7();
            var session = new CashSession { Id = Guid.CreateVersion7(), OpenedBy = cashier, Status = CashSessionStatus.Open, BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow) };
            fakesB.Cash.AddSession(session);

            var saleHandler = new CompleteSaleHandler(fakesB.Sales, fakesB.Quotations, fakesB.Parties, fakesB.Catalog, fakesB.Inventory, fakesB.CostAllocator, fakesB.Cash, fakesB.OperationLock, fakesB.ResourceLock, fakesB.Audit, fakesB.ReceiptSnapshots, fakesB.Numbers, fakesB.Clock, fakesB.Transactions, fakesB.Authorization, fakesB.UnitOfWork);
            var saleRes = await saleHandler.HandleAsync(new CompleteSaleCommand(Guid.CreateVersion7(), customer.Id, cashier, session.Id, 0m, SalePaymentMethod.Cash, 100m, null, null, [new CompleteSaleLineInput(product.Id, pu.Id, 1m, 100m, [invUnit.Id])]), CancellationToken.None);
            Assert.True(saleRes.IsSuccess);

            var saleItem = fakesB.Sales.SaleItems.Single();
            saleItem.WarrantyValidUntil = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(180));
            fakesB.Sales.SaleItemUnits.Single().WarrantyValidUntil = saleItem.WarrantyValidUntil;

            // Step 1: Warranty Claim arrives first and creates active custody
            var claimHandler = new CreateWarrantyClaimHandler(fakesB.Catalog, fakesB.Parties, fakesB.Sales, fakesB.Purchasing, fakesB.Inventory, fakesB.Traceability, fakesB.Warranty, fakesB.ResourceLock, fakesB.Authorization, fakesB.Numbers, fakesB.Clock, fakesB.Transactions, fakesB.UnitOfWork);
            var claimRes = await claimHandler.HandleAsync(new CreateWarrantyClaimCommand(customer.Id, saleRes.Value!.SaleId, supplier.Id, cashier, [new WarrantyClaimItemInput(product.Id, 1m, "Device dead", saleItem.Id, saleItem.WarrantyValidUntil, [new WarrantyClaimUnitInput(invUnit.Id, invUnit.TrackingCode)])], Guid.CreateVersion7()), CancellationToken.None);
            Assert.True(claimRes.IsSuccess, claimRes.Error?.Message);

            // Step 2: SaleReturn attempted for unit currently in active warranty claim
            var returnHandler = new CreateSaleReturnHandler(fakesB.Sales, fakesB.Inventory, fakesB.CostAllocator, fakesB.Cash, fakesB.OperationLock, fakesB.ResourceLock, fakesB.Audit, fakesB.Numbers, fakesB.Clock, fakesB.Transactions, fakesB.Authorization, fakesB.UnitOfWork, fakesB.Warranty);
            var returnRes = await returnHandler.HandleAsync(new CreateSaleReturnCommand(saleRes.Value.SaleId, "RETURN_WARRANTY_UNIT", null, RefundMethod.Cash, cashier, Guid.CreateVersion7(), [new SaleReturnLineInput(saleItem.Id, 1m, SaleReturnDisposition.RestockSellable, [invUnit.Id])]), CancellationToken.None);

            // Assert: SaleReturn rejected because unit has active warranty claim
            Assert.False(returnRes.IsSuccess);
            Assert.Equal("sales.return_unit_active_warranty", returnRes.Error?.Code);
        }
    }
}
