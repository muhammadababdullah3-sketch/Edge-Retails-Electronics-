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

public sealed class Phase1DWarrantyReplacementLifecycleTests
{
    private readonly Phase2TestDoubles _fakes = new();
    private readonly InventoryConditionService _conditions;

    public Phase1DWarrantyReplacementLifecycleTests()
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
            _fakes.UnitOfWork);

    private BeginWarrantyClaimReviewHandler CreateBeginReviewHandler() =>
        new(
            _fakes.Warranty,
            _fakes.ResourceLock,
            _fakes.Authorization,
            _fakes.Clock,
            _fakes.Transactions,
            _fakes.UnitOfWork,
            _fakes.OperationLock);

    private SendWarrantyClaimToSupplierHandler CreateSendClaimToSupplierHandler() =>
        new(
            _fakes.Warranty,
            _fakes.ResourceLock,
            _fakes.Authorization,
            _fakes.Clock,
            _fakes.Transactions,
            _fakes.UnitOfWork,
            _fakes.OperationLock);

    private MarkWarrantySupplierProcessingHandler CreateSupplierProcessingHandler() =>
        new(
            _fakes.Warranty,
            _fakes.ResourceLock,
            _fakes.Authorization,
            _fakes.Clock,
            _fakes.Transactions,
            _fakes.UnitOfWork,
            _fakes.OperationLock);

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
            _fakes.UnitOfWork);

    private RecordWarrantyResolutionHandler CreateResolutionHandler() =>
        new(
            _fakes.Warranty,
            _fakes.ResourceLock,
            _fakes.Authorization,
            _fakes.Clock,
            _fakes.Transactions,
            _fakes.UnitOfWork,
            _fakes.OperationLock);

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
            _fakes.UnitOfWork);

    private (Supplier Supplier, Customer Customer, Product Product, ProductUnit Unit, InventoryUnit UnitEntity, Sale Sale, SaleItem SaleItem, SupplierProduct SupplierProduct)
        SeedSoldSerializedFixture(string dealerCode = "MT1", string sku = "S24-ULTRA", long nextSequence = 2)
    {
        var supplier = new Supplier
        {
            Id = Guid.CreateVersion7(),
            Name = "Mega Tech",
            DealerCode = dealerCode,
            IsActive = true
        };
        _fakes.Parties.AddSupplier(supplier);

        var customer = new Customer
        {
            Id = Guid.CreateVersion7(),
            Name = "John Customer",
            Phone = "03001234567",
            IsActive = true
        };
        _fakes.Parties.AddCustomer(customer);

        var unit = new Unit { Id = Guid.CreateVersion7(), Name = "Piece", Symbol = "pc" };
        var product = new Product
        {
            Id = Guid.CreateVersion7(),
            Name = "Galaxy S24 Ultra",
            Sku = sku,
            BaseUnitId = unit.Id,
            TrackingMode = TrackingMode.Serialized,
            SerialTrackingEnabled = true,
            ImeiTrackingEnabled = true,
            DefaultWarrantyMonths = 12,
            DefaultSalePrice = 300000m,
            ReferencePurchaseCost = 250000m,
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

        var supplierProduct = new SupplierProduct
        {
            Id = Guid.CreateVersion7(),
            SupplierId = supplier.Id,
            ProductId = product.Id,
            NextItemSequence = nextSequence,
            IsActive = true
        };
        _fakes.Traceability.AddSupplierProduct(supplierProduct);

        var purchase = new Purchase
        {
            Id = Guid.CreateVersion7(),
            SupplierId = supplier.Id,
            PurchaseNumber = "PUR-SEED-01",
            SupplierInvoiceNumber = "SINV-001",
            PurchaseDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-60)),
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-60)
        };
        _fakes.Purchasing.AddPurchase(purchase);

        var purchaseItem = new PurchaseItem
        {
            Id = Guid.CreateVersion7(),
            PurchaseId = purchase.Id,
            ProductId = product.Id,
            ProductUnitId = pu.Id,
            BaseQuantity = 1m,
            EnteredUnitCost = 250000m,
            EffectiveBaseUnitCost = 250000m,
            BaseLineTotal = 250000m,
            EffectiveLineCost = 250000m
        };
        _fakes.Purchasing.AddPurchaseItem(purchaseItem);

        var trackingCode = TraceabilityCodeRules.BuildTrackingCode(dealerCode, sku, 1);
        var inventoryUnit = new InventoryUnit
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            SupplierProductId = supplierProduct.Id,
            SourcePurchaseItemId = purchaseItem.Id,
            ItemSequence = 1,
            SerialNumber = "SN-S24-001",
            Imei1 = "860123456789016",
            TrackingCode = trackingCode,
            SupplierCodeSnapshot = dealerCode,
            ProductSkuSnapshot = sku,
            Status = InventoryUnitStatus.Sold,
            AcquisitionCost = 250000m,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-30)
        };
        _fakes.Inventory.AddInventoryUnit(inventoryUnit);

        var sale = new Sale
        {
            Id = Guid.CreateVersion7(),
            InvoiceNumber = "INV-SALE-001",
            CustomerId = customer.Id,
            CashierUserId = Guid.CreateVersion7(),
            GrandTotal = 300000m,
            CompletedAt = DateTimeOffset.UtcNow.AddDays(-30),
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-30),
            Status = SaleStatus.Completed
        };
        _fakes.Sales.AddSale(sale);

        var saleItem = new SaleItem
        {
            Id = Guid.CreateVersion7(),
            SaleId = sale.Id,
            ProductId = product.Id,
            ProductUnitId = pu.Id,
            BaseQuantity = 1m,
            UnitPrice = 300000m,
            GrossLineTotal = 300000m,
            NetLineTotal = 300000m,
            WarrantyValidUntil = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(335))
        };
        _fakes.Sales.AddSaleItem(saleItem);

        var saleItemUnit = new SaleItemUnit
        {
            SaleItemId = saleItem.Id,
            InventoryUnitId = inventoryUnit.Id,
            WarrantyValidUntil = saleItem.WarrantyValidUntil
        };
        _fakes.Sales.AddSaleItemUnit(saleItemUnit);

        return (supplier, customer, product, pu, inventoryUnit, sale, saleItem, supplierProduct);
    }

    // 1. WarrantyClaim_ExactUnit_LinksOriginalSaleAndInventoryUnit
    [Fact]
    public async Task WarrantyClaim_ExactUnit_LinksOriginalSaleAndInventoryUnit()
    {
        var (supplier, customer, product, _, unitEntity, sale, saleItem, _) = SeedSoldSerializedFixture();
        var handler = CreateClaimHandler();
        var actorId = Guid.CreateVersion7();

        var result = await handler.HandleAsync(
            new CreateWarrantyClaimCommand(
                customer.Id,
                sale.Id,
                supplier.Id,
                actorId,
                [
                    new WarrantyClaimItemInput(
                        product.Id,
                        1m,
                        "Display line defect",
                        saleItem.Id,
                        saleItem.WarrantyValidUntil,
                        [new WarrantyClaimUnitInput(unitEntity.Id, unitEntity.SerialNumber)])
                ],
                Guid.CreateVersion7()),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var claimId = result.Value;

        var claim = await _fakes.Warranty.GetClaimForUpdateAsync(claimId, CancellationToken.None);
        Assert.NotNull(claim);
        Assert.Equal(sale.Id, claim!.OriginalSaleId);
        Assert.Equal(customer.Id, claim.CustomerId);
        Assert.Equal(supplier.Id, claim.SupplierId);
        Assert.Equal(WarrantyClaimStatus.Received, claim.Status);
        Assert.Equal(WarrantyCustody.WithShop, claim.CurrentCustody);

        var claimItems = await _fakes.Warranty.GetClaimItemsAsync(claimId, CancellationToken.None);
        Assert.Single(claimItems);
        var item = claimItems[0];
        Assert.Equal(saleItem.Id, item.OriginalSaleItemId);
        Assert.Equal(product.Id, item.ProductId);
        Assert.Equal(1m, item.Quantity);

        var claimUnits = await _fakes.Warranty.GetClaimUnitsAsync(claimId, CancellationToken.None);
        Assert.Single(claimUnits);
        var claimUnit = claimUnits[0];
        Assert.Equal(unitEntity.Id, claimUnit.OriginalInventoryUnitId);
        Assert.Equal(unitEntity.Id, claimUnit.ActiveOriginalInventoryUnitId);
        Assert.Contains(unitEntity.TrackingCode!, claimUnit.OriginalIdentitySnapshot!);
        Assert.Contains(unitEntity.SerialNumber!, claimUnit.OriginalIdentitySnapshot!);
    }

    // 2. WarrantyClaim_PreservesOriginalTrackingCode
    [Fact]
    public async Task WarrantyClaim_PreservesOriginalTrackingCode()
    {
        var (supplier, customer, product, _, unitEntity, sale, saleItem, _) = SeedSoldSerializedFixture();
        var originalTrackingCode = unitEntity.TrackingCode;
        var originalSerial = unitEntity.SerialNumber;
        var originalImei = unitEntity.Imei1;
        var actorId = Guid.CreateVersion7();

        var claimResult = await CreateClaimHandler().HandleAsync(
            new CreateWarrantyClaimCommand(
                customer.Id,
                sale.Id,
                supplier.Id,
                actorId,
                [
                    new WarrantyClaimItemInput(
                        product.Id,
                        1m,
                        "Faulty microphone",
                        saleItem.Id,
                        saleItem.WarrantyValidUntil,
                        [new WarrantyClaimUnitInput(unitEntity.Id, originalSerial)])
                ],
                Guid.CreateVersion7()),
            CancellationToken.None);

        Assert.True(claimResult.IsSuccess);
        var claimId = claimResult.Value;

        // Progress claim: UnderReview -> SentToSupplier
        await CreateBeginReviewHandler().HandleAsync(
            new BeginWarrantyClaimReviewCommand(claimId, actorId, Guid.CreateVersion7(), "Reviewed"),
            CancellationToken.None);
        await CreateSendClaimToSupplierHandler().HandleAsync(
            new SendWarrantyClaimToSupplierCommand(claimId, actorId, Guid.CreateVersion7(), "Sent to OEM"),
            CancellationToken.None);

        // Verify original unit identity invariants
        var storedUnit = _fakes.Inventory.Units.Single(u => u.Id == unitEntity.Id);
        Assert.Equal(originalTrackingCode, storedUnit.TrackingCode);
        Assert.Equal(originalSerial, storedUnit.SerialNumber);
        Assert.Equal(originalImei, storedUnit.Imei1);
        Assert.Equal(1, storedUnit.ItemSequence);
    }

    // 3. Repair_PreservesOriginalInventoryUnit
    [Fact]
    public async Task Repair_PreservesOriginalInventoryUnit()
    {
        var (supplier, customer, product, _, unitEntity, sale, saleItem, supplierProduct) = SeedSoldSerializedFixture();
        var actorId = Guid.CreateVersion7();
        var initialUnitsCount = _fakes.Inventory.Units.Count;
        var initialSequence = supplierProduct.NextItemSequence;

        var claimResult = await CreateClaimHandler().HandleAsync(
            new CreateWarrantyClaimCommand(
                customer.Id,
                sale.Id,
                supplier.Id,
                actorId,
                [
                    new WarrantyClaimItemInput(
                        product.Id,
                        1m,
                        "Broken speaker module",
                        saleItem.Id,
                        saleItem.WarrantyValidUntil,
                        [new WarrantyClaimUnitInput(unitEntity.Id, unitEntity.SerialNumber)])
                ],
                Guid.CreateVersion7()),
            CancellationToken.None);

        Assert.True(claimResult.IsSuccess);
        var claimId = claimResult.Value;

        await CreateBeginReviewHandler().HandleAsync(new BeginWarrantyClaimReviewCommand(claimId, actorId, Guid.CreateVersion7()), CancellationToken.None);
        await CreateSendClaimToSupplierHandler().HandleAsync(new SendWarrantyClaimToSupplierCommand(claimId, actorId, Guid.CreateVersion7()), CancellationToken.None);
        await CreateSupplierProcessingHandler().HandleAsync(new MarkWarrantySupplierProcessingCommand(claimId, actorId, Guid.CreateVersion7()), CancellationToken.None);

        // Resolve as Repaired
        var resResult = await CreateResolutionHandler().HandleAsync(
            new RecordWarrantyResolutionCommand(claimId, WarrantyResolutionType.Repaired, actorId, Guid.CreateVersion7(), "Speaker replaced by repair center"),
            CancellationToken.None);

        Assert.True(resResult.IsSuccess, resResult.Error?.Message);

        var claim = await _fakes.Warranty.GetClaimForUpdateAsync(claimId, CancellationToken.None);
        Assert.Equal(WarrantyClaimStatus.ReadyForCustomer, claim!.Status);
        Assert.Equal(WarrantyCustody.WithShop, claim.CurrentCustody);

        // Handover to customer
        var handoverResult = await CreateHandoverHandler().HandleAsync(
            new HandoverWarrantyItemCommand(claimId, actorId, Guid.CreateVersion7(), "Repaired unit returned to customer"),
            CancellationToken.None);

        Assert.True(handoverResult.IsSuccess);
        Assert.Equal(WarrantyClaimStatus.Closed, claim.Status);
        Assert.Equal(WarrantyCustody.WithCustomer, claim.CurrentCustody);

        // Invariants: NO new unit created, sequence untouched
        Assert.Equal(initialUnitsCount, _fakes.Inventory.Units.Count);
        Assert.Equal(initialSequence, supplierProduct.NextItemSequence);
        var unitAfter = _fakes.Inventory.Units.Single(u => u.Id == unitEntity.Id);
        Assert.Equal("MT1-S24-ULTRA-000001", unitAfter.TrackingCode);
    }

    // 4. RejectedClaim_DoesNotAllocateReplacement
    [Fact]
    public async Task RejectedClaim_DoesNotAllocateReplacement()
    {
        var (supplier, customer, product, _, unitEntity, sale, saleItem, supplierProduct) = SeedSoldSerializedFixture();
        var actorId = Guid.CreateVersion7();
        var initialUnitsCount = _fakes.Inventory.Units.Count;
        var initialSequence = supplierProduct.NextItemSequence;

        var claimResult = await CreateClaimHandler().HandleAsync(
            new CreateWarrantyClaimCommand(
                customer.Id,
                sale.Id,
                supplier.Id,
                actorId,
                [
                    new WarrantyClaimItemInput(
                        product.Id,
                        1m,
                        "Water damage reported by customer",
                        saleItem.Id,
                        saleItem.WarrantyValidUntil,
                        [new WarrantyClaimUnitInput(unitEntity.Id, unitEntity.SerialNumber)])
                ],
                Guid.CreateVersion7()),
            CancellationToken.None);

        Assert.True(claimResult.IsSuccess);
        var claimId = claimResult.Value;

        await CreateBeginReviewHandler().HandleAsync(new BeginWarrantyClaimReviewCommand(claimId, actorId, Guid.CreateVersion7()), CancellationToken.None);
        await CreateSendClaimToSupplierHandler().HandleAsync(new SendWarrantyClaimToSupplierCommand(claimId, actorId, Guid.CreateVersion7()), CancellationToken.None);
        await CreateSupplierProcessingHandler().HandleAsync(new MarkWarrantySupplierProcessingCommand(claimId, actorId, Guid.CreateVersion7()), CancellationToken.None);

        // Resolve as Rejected
        var rejectResult = await CreateResolutionHandler().HandleAsync(
            new RecordWarrantyResolutionCommand(claimId, WarrantyResolutionType.Rejected, actorId, Guid.CreateVersion7(), "Physical liquid ingress voided warranty"),
            CancellationToken.None);

        Assert.True(rejectResult.IsSuccess, rejectResult.Error?.Message);

        // Invariants: no replacement unit created, sequence NOT incremented
        Assert.Equal(initialUnitsCount, _fakes.Inventory.Units.Count);
        Assert.Equal(initialSequence, supplierProduct.NextItemSequence);

        var item = (await _fakes.Warranty.GetClaimItemsAsync(claimId, CancellationToken.None)).Single();
        Assert.Equal(WarrantyResolutionType.Rejected, item.ResolutionType);
        Assert.Null(item.ReplacementProductId);
        Assert.Null(item.ReplacementReference);
    }

    // 5. ScrappedUnit_CannotReturnToSellableStock
    [Fact]
    public async Task ScrappedUnit_CannotReturnToSellableStock()
    {
        var supplier = new Supplier { Id = Guid.CreateVersion7(), Name = "Monitor Supplier", DealerCode = "MS1", IsActive = true };
        _fakes.Parties.AddSupplier(supplier);

        var unit = new Unit { Id = Guid.CreateVersion7(), Name = "Piece", Symbol = "pc" };
        var product = new Product
        {
            Id = Guid.CreateVersion7(),
            Name = "Monitor OLED",
            Sku = "MON-OLED",
            BaseUnitId = unit.Id,
            TrackingMode = TrackingMode.Serialized,
            SerialTrackingEnabled = true,
            DefaultSalePrice = 120000m,
            ReferencePurchaseCost = 90000m,
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

        var supplierProduct = new SupplierProduct { Id = Guid.CreateVersion7(), SupplierId = supplier.Id, ProductId = product.Id, NextItemSequence = 2, IsActive = true };
        _fakes.Traceability.AddSupplierProduct(supplierProduct);

        var purchase = new Purchase { Id = Guid.CreateVersion7(), SupplierId = supplier.Id, PurchaseNumber = "PUR-SCRAP", SupplierInvoiceNumber = "SINV-SCRAP", PurchaseDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-20)), CreatedAt = DateTimeOffset.UtcNow.AddDays(-20) };
        _fakes.Purchasing.AddPurchase(purchase);

        var purchaseItem = new PurchaseItem { Id = Guid.CreateVersion7(), PurchaseId = purchase.Id, ProductId = product.Id, ProductUnitId = pu.Id, BaseQuantity = 1m, EnteredUnitCost = 90000m, EffectiveBaseUnitCost = 90000m, BaseLineTotal = 90000m, EffectiveLineCost = 90000m };
        _fakes.Purchasing.AddPurchaseItem(purchaseItem);

        var lot = new InventoryLot { Id = Guid.CreateVersion7(), ProductId = product.Id, PurchaseItemId = purchaseItem.Id, ReceivedQuantity = 1m, OriginalUnitCost = 90000m, EffectiveUnitCost = 90000m, CreatedAt = DateTimeOffset.UtcNow.AddDays(-20) };
        _fakes.Inventory.AddLot(lot);
        _fakes.Inventory.AddLotBucketBalance(new InventoryLotBucketBalance { LotId = lot.Id, StockBucket = InventoryBucket.Defective, Quantity = 1m });

        var defectiveUnit = new InventoryUnit
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            InventoryLotId = lot.Id,
            SupplierProductId = supplierProduct.Id,
            SourcePurchaseItemId = purchaseItem.Id,
            SerialNumber = "SN-MON-SCRAP",
            TrackingCode = "MS1-MON-OLED-000001",
            Status = InventoryUnitStatus.Defective,
            AcquisitionCost = 90000m,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _fakes.Inventory.AddInventoryUnit(defectiveUnit);
        _fakes.Inventory.AddStockBalance(new StockBalance { ProductId = product.Id, DefectiveQty = 1m });
        _fakes.Inventory.AddCostState(new ProductCostState { ProductId = product.Id, CostedQty = 1m, TotalInventoryCost = 90000m, MovingAverageCost = 90000m, LastPurchaseCost = 90000m, Version = 1 });

        var actorId = Guid.CreateVersion7();

        // 1. Send defective unit to supplier warranty
        var sendResult = await CreateSendShopStockHandler().HandleAsync(
            new SendShopStockToSupplierWarrantyCommand(
                product.Id,
                InventoryBucket.Defective,
                1m,
                supplier.Id,
                SourcePurchaseItemId: null,
                "Cracked panel from transit",
                actorId,
                Guid.CreateVersion7(),
                [defectiveUnit.Id]),
            CancellationToken.None);

        Assert.True(sendResult.IsSuccess, sendResult.Error?.Message);
        var caseId = sendResult.Value;

        // 2. Receive supplier decision: Scrapped
        var scrapResult = await CreateReceiveShopStockHandler().HandleAsync(
            new ReceiveShopStockWarrantyCommand(
                caseId,
                WarrantyResolutionType.Scrapped,
                actorId,
                OriginalInventoryUnitIds: [defectiveUnit.Id],
                ReplacementUnits: null,
                Note: "Confirmed total loss - scrapped at supplier factory",
                ClientOperationId: Guid.CreateVersion7()),
            CancellationToken.None);

        Assert.True(scrapResult.IsSuccess, scrapResult.Error?.Message);

        var warrantyCase = await _fakes.Warranty.GetShopStockCaseAsync(caseId, CancellationToken.None);
        Assert.Equal(ShopWarrantyCaseStatus.WrittenOff, warrantyCase!.Status);
        Assert.Equal(InventoryUnitStatus.Scrapped, defectiveUnit.Status);

        // 3. Invariant: Scrapped unit cannot be transferred back to Sellable
        var invalidTransfer = await _conditions.TransferAsync(
            new TransferInventoryConditionCommand(
                product.Id,
                InventoryBucket.Scrap,
                InventoryBucket.Sellable,
                1m,
                actorId,
                "Illegal restore attempt",
                InventoryUnitIds: [defectiveUnit.Id]),
            CancellationToken.None);

        Assert.False(invalidTransfer.IsSuccess);
        Assert.Equal("inventory.invalid_condition_transition", invalidTransfer.Error?.Code);
    }

    // 6. Replacement_CreatesNewInventoryUnit
    [Fact]
    public async Task Replacement_CreatesNewInventoryUnit()
    {
        var (supplier, customer, product, _, unitEntity, sale, saleItem, _) = SeedSoldSerializedFixture();
        var actorId = Guid.CreateVersion7();

        var claimResult = await CreateClaimHandler().HandleAsync(
            new CreateWarrantyClaimCommand(
                customer.Id,
                sale.Id,
                supplier.Id,
                actorId,
                [
                    new WarrantyClaimItemInput(
                        product.Id,
                        1m,
                        "Hardware motherboard failure",
                        saleItem.Id,
                        saleItem.WarrantyValidUntil,
                        [new WarrantyClaimUnitInput(unitEntity.Id, unitEntity.SerialNumber)])
                ],
                Guid.CreateVersion7()),
            CancellationToken.None);

        var claimId = claimResult.Value;
        var claimUnit = (await _fakes.Warranty.GetClaimUnitsAsync(claimId, CancellationToken.None)).Single();

        await CreateBeginReviewHandler().HandleAsync(new BeginWarrantyClaimReviewCommand(claimId, actorId, Guid.CreateVersion7()), CancellationToken.None);
        await CreateSendClaimToSupplierHandler().HandleAsync(new SendWarrantyClaimToSupplierCommand(claimId, actorId, Guid.CreateVersion7()), CancellationToken.None);

        // Receive replacement
        var repResult = await CreateReceiveCustomerReplacementHandler().HandleAsync(
            new ReceiveCustomerWarrantyReplacementCommand(
                claimId,
                actorId,
                Guid.CreateVersion7(),
                [new CustomerWarrantyReplacementUnitInput(claimUnit.Id, "SN-S24-BRAND-NEW", "860123456789099", null)],
                "OEM provided replacement device"),
            CancellationToken.None);

        Assert.True(repResult.IsSuccess, repResult.Error?.Message);

        // Assert: A brand new InventoryUnit entity exists
        var replacementUnit = _fakes.Inventory.Units.Single(u => u.SerialNumber == "SN-S24-BRAND-NEW");
        Assert.NotEqual(unitEntity.Id, replacementUnit.Id);
        Assert.Equal(product.Id, replacementUnit.ProductId);
        Assert.Equal(InventoryUnitOriginType.WarrantyReplacement, replacementUnit.OriginType);
        Assert.Equal(InventoryUnitStatus.WarrantyCustomerHeld, replacementUnit.Status);
        Assert.Equal(claimUnit.ClaimItemId, replacementUnit.SourceWarrantyClaimItemId);
    }

    // 7. Replacement_CreatesNewTrackingCode
    [Fact]
    public async Task Replacement_CreatesNewTrackingCode()
    {
        var (supplier, customer, product, _, unitEntity, sale, saleItem, supplierProduct) = SeedSoldSerializedFixture(
            dealerCode: "MT1",
            sku: "S24-ULTRA",
            nextSequence: 27);
        var actorId = Guid.CreateVersion7();

        var claimResult = await CreateClaimHandler().HandleAsync(
            new CreateWarrantyClaimCommand(
                customer.Id,
                sale.Id,
                supplier.Id,
                actorId,
                [
                    new WarrantyClaimItemInput(
                        product.Id,
                        1m,
                        "Faulty mainboard",
                        saleItem.Id,
                        saleItem.WarrantyValidUntil,
                        [new WarrantyClaimUnitInput(unitEntity.Id, unitEntity.SerialNumber)])
                ],
                Guid.CreateVersion7()),
            CancellationToken.None);

        var claimId = claimResult.Value;
        var claimUnit = (await _fakes.Warranty.GetClaimUnitsAsync(claimId, CancellationToken.None)).Single();

        await CreateBeginReviewHandler().HandleAsync(new BeginWarrantyClaimReviewCommand(claimId, actorId, Guid.CreateVersion7()), CancellationToken.None);
        await CreateSendClaimToSupplierHandler().HandleAsync(new SendWarrantyClaimToSupplierCommand(claimId, actorId, Guid.CreateVersion7()), CancellationToken.None);

        var repResult = await CreateReceiveCustomerReplacementHandler().HandleAsync(
            new ReceiveCustomerWarrantyReplacementCommand(
                claimId,
                actorId,
                Guid.CreateVersion7(),
                [new CustomerWarrantyReplacementUnitInput(claimUnit.Id, "SN-S24-NEW-027", "860123456789027", null)],
                "Unit replaced"),
            CancellationToken.None);

        Assert.True(repResult.IsSuccess, repResult.Error?.Message);

        var replacementUnit = _fakes.Inventory.Units.Single(u => u.SerialNumber == "SN-S24-NEW-027");
        // Must receive NEW tracking code: MT1-S24-ULTRA-000027, NOT old MT1-S24-ULTRA-000001
        Assert.Equal("MT1-S24-ULTRA-000027", replacementUnit.TrackingCode);
        Assert.NotEqual(unitEntity.TrackingCode, replacementUnit.TrackingCode);
        Assert.Equal(27, replacementUnit.ItemSequence);
    }

    // 8. Replacement_AdvancesCorrectSupplierProductSequence
    [Fact]
    public async Task Replacement_AdvancesCorrectSupplierProductSequence()
    {
        var (supplierA, customer, product, _, unitEntity, sale, saleItem, supplierProductA) = SeedSoldSerializedFixture(
            dealerCode: "SUA",
            sku: "S24-ULTRA",
            nextSequence: 5);

        // Add a second supplier product for another supplier to ensure isolated sequence advancement
        var supplierB = new Supplier { Id = Guid.CreateVersion7(), Name = "Supplier B", DealerCode = "SUB", IsActive = true };
        _fakes.Parties.AddSupplier(supplierB);
        var supplierProductB = new SupplierProduct { Id = Guid.CreateVersion7(), SupplierId = supplierB.Id, ProductId = product.Id, NextItemSequence = 100, IsActive = true };
        _fakes.Traceability.AddSupplierProduct(supplierProductB);

        var actorId = Guid.CreateVersion7();

        var claimResult = await CreateClaimHandler().HandleAsync(
            new CreateWarrantyClaimCommand(
                customer.Id,
                sale.Id,
                supplierA.Id,
                actorId,
                [
                    new WarrantyClaimItemInput(
                        product.Id,
                        1m,
                        "Hardware defect",
                        saleItem.Id,
                        saleItem.WarrantyValidUntil,
                        [new WarrantyClaimUnitInput(unitEntity.Id, unitEntity.SerialNumber)])
                ],
                Guid.CreateVersion7()),
            CancellationToken.None);

        var claimId = claimResult.Value;
        var claimUnit = (await _fakes.Warranty.GetClaimUnitsAsync(claimId, CancellationToken.None)).Single();

        await CreateBeginReviewHandler().HandleAsync(new BeginWarrantyClaimReviewCommand(claimId, actorId, Guid.CreateVersion7()), CancellationToken.None);
        await CreateSendClaimToSupplierHandler().HandleAsync(new SendWarrantyClaimToSupplierCommand(claimId, actorId, Guid.CreateVersion7()), CancellationToken.None);

        var repResult = await CreateReceiveCustomerReplacementHandler().HandleAsync(
            new ReceiveCustomerWarrantyReplacementCommand(
                claimId,
                actorId,
                Guid.CreateVersion7(),
                [new CustomerWarrantyReplacementUnitInput(claimUnit.Id, "SN-ADVANCE-05", "860123456789055", null)],
                "Replaced by Supplier A"),
            CancellationToken.None);

        Assert.True(repResult.IsSuccess, repResult.Error?.Message);

        // Supplier A's sequence monotonically advanced 5 -> 6
        Assert.Equal(6, supplierProductA.NextItemSequence);
        // Supplier B's sequence untouched
        Assert.Equal(100, supplierProductB.NextItemSequence);

        var replacementUnit = _fakes.Inventory.Units.Single(u => u.SerialNumber == "SN-ADVANCE-05");
        Assert.Equal(5, replacementUnit.ItemSequence);
        Assert.Equal("SUA-S24-ULTRA-000005", replacementUnit.TrackingCode);
    }

    // 9. Replacement_LinksOriginalAndNewInventoryUnits
    [Fact]
    public async Task Replacement_LinksOriginalAndNewInventoryUnits()
    {
        var (supplier, customer, product, _, unitEntity, sale, saleItem, _) = SeedSoldSerializedFixture();
        var actorId = Guid.CreateVersion7();

        var claimResult = await CreateClaimHandler().HandleAsync(
            new CreateWarrantyClaimCommand(
                customer.Id,
                sale.Id,
                supplier.Id,
                actorId,
                [
                    new WarrantyClaimItemInput(
                        product.Id,
                        1m,
                        "Defective screen",
                        saleItem.Id,
                        saleItem.WarrantyValidUntil,
                        [new WarrantyClaimUnitInput(unitEntity.Id, unitEntity.SerialNumber)])
                ],
                Guid.CreateVersion7()),
            CancellationToken.None);

        var claimId = claimResult.Value;
        var claimUnit = (await _fakes.Warranty.GetClaimUnitsAsync(claimId, CancellationToken.None)).Single();

        await CreateBeginReviewHandler().HandleAsync(new BeginWarrantyClaimReviewCommand(claimId, actorId, Guid.CreateVersion7()), CancellationToken.None);
        await CreateSendClaimToSupplierHandler().HandleAsync(new SendWarrantyClaimToSupplierCommand(claimId, actorId, Guid.CreateVersion7()), CancellationToken.None);

        var repResult = await CreateReceiveCustomerReplacementHandler().HandleAsync(
            new ReceiveCustomerWarrantyReplacementCommand(
                claimId,
                actorId,
                Guid.CreateVersion7(),
                [new CustomerWarrantyReplacementUnitInput(claimUnit.Id, "SN-S24-REPL-REL", "860123456789088", null)],
                "Replacement delivered"),
            CancellationToken.None);

        Assert.True(repResult.IsSuccess);

        var replacementUnit = _fakes.Inventory.Units.Single(u => u.SerialNumber == "SN-S24-REPL-REL");

        // Verify relational link in claim unit
        var updatedClaimUnit = (await _fakes.Warranty.GetClaimUnitsAsync(claimId, CancellationToken.None)).Single();
        Assert.Equal(unitEntity.Id, updatedClaimUnit.OriginalInventoryUnitId);
        Assert.Equal(replacementUnit.Id, updatedClaimUnit.ReplacementInventoryUnitId);
        Assert.Contains(replacementUnit.TrackingCode!, updatedClaimUnit.ReplacementIdentitySnapshot!);

        // ClaimItem reflects resolution
        var claimItem = (await _fakes.Warranty.GetClaimItemsAsync(claimId, CancellationToken.None)).Single();
        Assert.Equal(WarrantyResolutionType.Replaced, claimItem.ResolutionType);
        Assert.Equal(product.Id, claimItem.ReplacementProductId);
        Assert.Equal(replacementUnit.TrackingCode, claimItem.ReplacementReference);
    }

    // 10. Replacement_NewSerialImei_Validated
    [Fact]
    public async Task Replacement_NewSerialImei_Validated()
    {
        var (supplier, customer, product, _, unitEntity, sale, saleItem, _) = SeedSoldSerializedFixture();
        var actorId = Guid.CreateVersion7();

        var claimResult = await CreateClaimHandler().HandleAsync(
            new CreateWarrantyClaimCommand(
                customer.Id,
                sale.Id,
                supplier.Id,
                actorId,
                [
                    new WarrantyClaimItemInput(
                        product.Id,
                        1m,
                        "Faulty device",
                        saleItem.Id,
                        saleItem.WarrantyValidUntil,
                        [new WarrantyClaimUnitInput(unitEntity.Id, unitEntity.SerialNumber)])
                ],
                Guid.CreateVersion7()),
            CancellationToken.None);

        var claimId = claimResult.Value;
        var claimUnit = (await _fakes.Warranty.GetClaimUnitsAsync(claimId, CancellationToken.None)).Single();

        await CreateBeginReviewHandler().HandleAsync(new BeginWarrantyClaimReviewCommand(claimId, actorId, Guid.CreateVersion7()), CancellationToken.None);
        await CreateSendClaimToSupplierHandler().HandleAsync(new SendWarrantyClaimToSupplierCommand(claimId, actorId, Guid.CreateVersion7()), CancellationToken.None);

        // a) Missing Serial when serial tracking enabled
        var missingSerial = await CreateReceiveCustomerReplacementHandler().HandleAsync(
            new ReceiveCustomerWarrantyReplacementCommand(
                claimId,
                actorId,
                Guid.CreateVersion7(),
                [new CustomerWarrantyReplacementUnitInput(claimUnit.Id, null, "860123456789091", null)],
                "No serial"),
            CancellationToken.None);

        Assert.False(missingSerial.IsSuccess);
        Assert.Equal("warranty.replacement_serial_required", missingSerial.Error?.Code);

        // b) Missing IMEI1 when IMEI tracking enabled
        var missingImei = await CreateReceiveCustomerReplacementHandler().HandleAsync(
            new ReceiveCustomerWarrantyReplacementCommand(
                claimId,
                actorId,
                Guid.CreateVersion7(),
                [new CustomerWarrantyReplacementUnitInput(claimUnit.Id, "SN-NEW-VALID", null, null)],
                "No imei"),
            CancellationToken.None);

        Assert.False(missingImei.IsSuccess);
        Assert.Equal("warranty.replacement_imei_required", missingImei.Error?.Code);

        // c) Duplicate Serial/IMEI already in inventory history
        _fakes.Inventory.ExistingSerials.Add("SN-EXISTING-ALREADY");
        var dupIdentity = await CreateReceiveCustomerReplacementHandler().HandleAsync(
            new ReceiveCustomerWarrantyReplacementCommand(
                claimId,
                actorId,
                Guid.CreateVersion7(),
                [new CustomerWarrantyReplacementUnitInput(claimUnit.Id, "SN-EXISTING-ALREADY", "860123456789099", null)],
                "Duplicate serial"),
            CancellationToken.None);

        Assert.False(dupIdentity.IsSuccess);
        Assert.Equal("warranty.replacement_identity_exists", dupIdentity.Error?.Code);

        // d) Valid unique identity succeeds
        var validResult = await CreateReceiveCustomerReplacementHandler().HandleAsync(
            new ReceiveCustomerWarrantyReplacementCommand(
                claimId,
                actorId,
                Guid.CreateVersion7(),
                [new CustomerWarrantyReplacementUnitInput(claimUnit.Id, "SN-NEW-UNIQUE-01", "860123456789099", null)],
                "Valid replacement"),
            CancellationToken.None);

        Assert.True(validResult.IsSuccess, validResult.Error?.Message);
    }

    // 11. OriginalTrackingCode_RemainsHistorical
    [Fact]
    public async Task OriginalTrackingCode_RemainsHistorical()
    {
        var (supplier, customer, product, _, unitEntity, sale, saleItem, _) = SeedSoldSerializedFixture();
        var actorId = Guid.CreateVersion7();
        var originalTrackingCode = unitEntity.TrackingCode;

        var claimResult = await CreateClaimHandler().HandleAsync(
            new CreateWarrantyClaimCommand(
                customer.Id,
                sale.Id,
                supplier.Id,
                actorId,
                [
                    new WarrantyClaimItemInput(
                        product.Id,
                        1m,
                        "Defect",
                        saleItem.Id,
                        saleItem.WarrantyValidUntil,
                        [new WarrantyClaimUnitInput(unitEntity.Id, unitEntity.SerialNumber)])
                ],
                Guid.CreateVersion7()),
            CancellationToken.None);

        var claimId = claimResult.Value;
        var claimUnit = (await _fakes.Warranty.GetClaimUnitsAsync(claimId, CancellationToken.None)).Single();

        await CreateBeginReviewHandler().HandleAsync(new BeginWarrantyClaimReviewCommand(claimId, actorId, Guid.CreateVersion7()), CancellationToken.None);
        await CreateSendClaimToSupplierHandler().HandleAsync(new SendWarrantyClaimToSupplierCommand(claimId, actorId, Guid.CreateVersion7()), CancellationToken.None);

        await CreateReceiveCustomerReplacementHandler().HandleAsync(
            new ReceiveCustomerWarrantyReplacementCommand(
                claimId,
                actorId,
                Guid.CreateVersion7(),
                [new CustomerWarrantyReplacementUnitInput(claimUnit.Id, "SN-S24-REP-HIST", "860123456789077", null)],
                "Replaced"),
            CancellationToken.None);

        await CreateHandoverHandler().HandleAsync(
            new HandoverWarrantyItemCommand(claimId, actorId, Guid.CreateVersion7(), "Handover to customer"),
            CancellationToken.None);

        // Original unit still authoritative in history
        var originalInDb = _fakes.Inventory.Units.Single(u => u.Id == unitEntity.Id);
        Assert.Equal(originalTrackingCode, originalInDb.TrackingCode);
        Assert.Equal(1, originalInDb.ItemSequence);
        Assert.Equal("SN-S24-001", originalInDb.SerialNumber);

        // Replacement has distinct new code
        var repInDb = _fakes.Inventory.Units.Single(u => u.SerialNumber == "SN-S24-REP-HIST");
        Assert.Equal("MT1-S24-ULTRA-000002", repInDb.TrackingCode);
        Assert.Equal(2, repInDb.ItemSequence);
    }

    // 12. CrossCaseReplacementUnitSubstitution_IsRejected
    [Fact]
    public async Task CrossCaseReplacementUnitSubstitution_IsRejected()
    {
        var supplier = new Supplier { Id = Guid.CreateVersion7(), Name = "Monitor Supplier", DealerCode = "MS1", IsActive = true };
        _fakes.Parties.AddSupplier(supplier);

        var unit = new Unit { Id = Guid.CreateVersion7(), Name = "Piece", Symbol = "pc" };
        var product = new Product
        {
            Id = Guid.CreateVersion7(),
            Name = "Monitor 4K",
            Sku = "MON-4K",
            BaseUnitId = unit.Id,
            TrackingMode = TrackingMode.Serialized,
            SerialTrackingEnabled = true,
            DefaultSalePrice = 80000m,
            ReferencePurchaseCost = 60000m,
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

        var supplierProduct = new SupplierProduct { Id = Guid.CreateVersion7(), SupplierId = supplier.Id, ProductId = product.Id, NextItemSequence = 3, IsActive = true };
        _fakes.Traceability.AddSupplierProduct(supplierProduct);

        var purchase = new Purchase { Id = Guid.CreateVersion7(), SupplierId = supplier.Id, PurchaseNumber = "PUR-CROSS", SupplierInvoiceNumber = "SINV-CROSS", PurchaseDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-20)), CreatedAt = DateTimeOffset.UtcNow.AddDays(-20) };
        _fakes.Purchasing.AddPurchase(purchase);

        var purchaseItem = new PurchaseItem { Id = Guid.CreateVersion7(), PurchaseId = purchase.Id, ProductId = product.Id, ProductUnitId = pu.Id, BaseQuantity = 2m, EnteredUnitCost = 60000m, EffectiveBaseUnitCost = 60000m, BaseLineTotal = 120000m, EffectiveLineCost = 120000m };
        _fakes.Purchasing.AddPurchaseItem(purchaseItem);

        var lot = new InventoryLot { Id = Guid.CreateVersion7(), ProductId = product.Id, PurchaseItemId = purchaseItem.Id, ReceivedQuantity = 2m, OriginalUnitCost = 60000m, EffectiveUnitCost = 60000m, CreatedAt = DateTimeOffset.UtcNow.AddDays(-20) };
        _fakes.Inventory.AddLot(lot);
        _fakes.Inventory.AddLotBucketBalance(new InventoryLotBucketBalance { LotId = lot.Id, StockBucket = InventoryBucket.Damaged, Quantity = 2m });

        var unitA = new InventoryUnit
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            InventoryLotId = lot.Id,
            SupplierProductId = supplierProduct.Id,
            SourcePurchaseItemId = purchaseItem.Id,
            SerialNumber = "SN-MON-CASE-A",
            TrackingCode = "MS1-MON-4K-000001",
            Status = InventoryUnitStatus.Damaged,
            AcquisitionCost = 60000m,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var unitB = new InventoryUnit
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            InventoryLotId = lot.Id,
            SupplierProductId = supplierProduct.Id,
            SourcePurchaseItemId = purchaseItem.Id,
            SerialNumber = "SN-MON-CASE-B",
            TrackingCode = "MS1-MON-4K-000002",
            Status = InventoryUnitStatus.Damaged,
            AcquisitionCost = 60000m,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _fakes.Inventory.AddInventoryUnit(unitA);
        _fakes.Inventory.AddInventoryUnit(unitB);
        _fakes.Inventory.AddStockBalance(new StockBalance { ProductId = product.Id, DamagedQty = 2m });
        _fakes.Inventory.AddCostState(new ProductCostState { ProductId = product.Id, CostedQty = 2m, TotalInventoryCost = 120000m, MovingAverageCost = 60000m, LastPurchaseCost = 60000m, Version = 1 });

        var actorId = Guid.CreateVersion7();

        // 1. Send Case A with Unit A
        var sendA = await CreateSendShopStockHandler().HandleAsync(
            new SendShopStockToSupplierWarrantyCommand(
                product.Id,
                InventoryBucket.Damaged,
                1m,
                supplier.Id,
                SourcePurchaseItemId: null,
                "Case A panel failure",
                actorId,
                Guid.CreateVersion7(),
                [unitA.Id]),
            CancellationToken.None);
        Assert.True(sendA.IsSuccess);
        var caseAId = sendA.Value;

        // 2. Send Case B with Unit B
        var sendB = await CreateSendShopStockHandler().HandleAsync(
            new SendShopStockToSupplierWarrantyCommand(
                product.Id,
                InventoryBucket.Damaged,
                1m,
                supplier.Id,
                SourcePurchaseItemId: null,
                "Case B backlight bleed",
                actorId,
                Guid.CreateVersion7(),
                [unitB.Id]),
            CancellationToken.None);
        Assert.True(sendB.IsSuccess);
        var caseBId = sendB.Value;

        // 3. Attempt cross-case substitution: Try to receive resolution for Case B using Unit A!
        var crossSubstitution = await CreateReceiveShopStockHandler().HandleAsync(
            new ReceiveShopStockWarrantyCommand(
                caseBId,
                WarrantyResolutionType.Replaced,
                actorId,
                OriginalInventoryUnitIds: [unitA.Id], // Illegal substitution: Unit A belongs to Case A, not Case B!
                ReplacementUnits: [new ReplacementSerializedUnitInput("SN-SUB-NEW", "860123456789071", null)],
                Note: "Attempting to substitute unit A into case B",
                ClientOperationId: Guid.CreateVersion7()),
            CancellationToken.None);

        Assert.False(crossSubstitution.IsSuccess);
        Assert.Equal("warranty.case_unit_mismatch", crossSubstitution.Error?.Code);
    }

    // 13. OpeningStockWarranty_UsesExistingEligibilityRules
    [Fact]
    public async Task OpeningStockWarranty_UsesExistingEligibilityRules()
    {
        var supplier = new Supplier { Id = Guid.CreateVersion7(), Name = "Opening Supplier", DealerCode = "OPS", IsActive = true };
        _fakes.Parties.AddSupplier(supplier);

        var customer = new Customer { Id = Guid.CreateVersion7(), Name = "Opening Customer", Phone = "03009999999", IsActive = true };
        _fakes.Parties.AddCustomer(customer);

        var unit = new Unit { Id = Guid.CreateVersion7(), Name = "Piece", Symbol = "pc" };
        var product = new Product
        {
            Id = Guid.CreateVersion7(),
            Name = "Opening Phone",
            Sku = "OP-PHONE",
            BaseUnitId = unit.Id,
            TrackingMode = TrackingMode.Serialized,
            SerialTrackingEnabled = true,
            ImeiTrackingEnabled = true,
            DefaultWarrantyMonths = 12,
            DefaultSalePrice = 150000m,
            ReferencePurchaseCost = 120000m,
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

        var supplierProduct = new SupplierProduct { Id = Guid.CreateVersion7(), SupplierId = supplier.Id, ProductId = product.Id, NextItemSequence = 2, IsActive = true };
        _fakes.Traceability.AddSupplierProduct(supplierProduct);

        var adjustmentItem = new StockAdjustmentItem
        {
            Id = Guid.CreateVersion7(),
            StockAdjustmentId = Guid.CreateVersion7(),
            ProductId = product.Id,
            ProductUnitId = pu.Id,
            Direction = StockAdjustmentDirection.Increase,
            TargetBucket = InventoryBucket.Sellable,
            BaseQuantity = 1m,
            SupplierId = supplier.Id,
            SupplierProductId = supplierProduct.Id
        };
        _fakes.Inventory.AddStockAdjustmentItem(adjustmentItem);

        // Created from Opening Stock: OriginType = StockAdjustment, SourcePurchaseItemId = null!
        var openingUnit = new InventoryUnit
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            SupplierProductId = supplierProduct.Id,
            OriginType = InventoryUnitOriginType.StockAdjustment,
            SourceStockAdjustmentItemId = adjustmentItem.Id,
            SourcePurchaseItemId = null,
            ItemSequence = 1,
            TrackingCode = "OPS-OP-PHONE-000001",
            SupplierCodeSnapshot = "OPS",
            ProductSkuSnapshot = "OP-PHONE",
            SerialNumber = "SN-OPENING-001",
            Imei1 = "860123456789001",
            Status = InventoryUnitStatus.Sold,
            AcquisitionCost = 120000m,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-30)
        };
        _fakes.Inventory.AddInventoryUnit(openingUnit);

        var sale = new Sale
        {
            Id = Guid.CreateVersion7(),
            InvoiceNumber = "INV-OP-01",
            CustomerId = customer.Id,
            CashierUserId = Guid.CreateVersion7(),
            GrandTotal = 150000m,
            CompletedAt = DateTimeOffset.UtcNow.AddDays(-30),
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-30),
            Status = SaleStatus.Completed
        };
        _fakes.Sales.AddSale(sale);

        var saleItem = new SaleItem
        {
            Id = Guid.CreateVersion7(),
            SaleId = sale.Id,
            ProductId = product.Id,
            ProductUnitId = pu.Id,
            BaseQuantity = 1m,
            UnitPrice = 150000m,
            GrossLineTotal = 150000m,
            NetLineTotal = 150000m,
            WarrantyValidUntil = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(335)) // Active warranty
        };
        _fakes.Sales.AddSaleItem(saleItem);

        var saleItemUnit = new SaleItemUnit
        {
            SaleItemId = saleItem.Id,
            InventoryUnitId = openingUnit.Id,
            WarrantyValidUntil = saleItem.WarrantyValidUntil
        };
        _fakes.Sales.AddSaleItemUnit(saleItemUnit);

        var handler = CreateClaimHandler();
        var actorId = Guid.CreateVersion7();

        // Standard eligibility: Active warranty, matching customer, unit sold -> SUCCEEDS!
        var claimResult = await handler.HandleAsync(
            new CreateWarrantyClaimCommand(
                customer.Id,
                sale.Id,
                supplier.Id,
                actorId,
                [
                    new WarrantyClaimItemInput(
                        product.Id,
                        1m,
                        "Defect on opening stock item",
                        saleItem.Id,
                        saleItem.WarrantyValidUntil,
                        [new WarrantyClaimUnitInput(openingUnit.Id, openingUnit.SerialNumber)])
                ],
                Guid.CreateVersion7()),
            CancellationToken.None);

        Assert.True(claimResult.IsSuccess, claimResult.Error?.Message);
        var claim = await _fakes.Warranty.GetClaimForUpdateAsync(claimResult.Value, CancellationToken.None);
        Assert.NotNull(claim);
        Assert.Equal(supplier.Id, claim!.SupplierId);

        // Verification of existing eligibility rules:
        // Rule: Customer mismatch is rejected
        var wrongCustomerId = Guid.CreateVersion7();
        _fakes.Parties.AddCustomer(new Customer { Id = wrongCustomerId, Name = "Wrong Customer", Phone = "03001111111", IsActive = true });
        var mismatchResult = await handler.HandleAsync(
            new CreateWarrantyClaimCommand(
                wrongCustomerId,
                sale.Id,
                supplier.Id,
                actorId,
                [
                    new WarrantyClaimItemInput(
                        product.Id,
                        1m,
                        "Mismatch attempt",
                        saleItem.Id,
                        saleItem.WarrantyValidUntil,
                        [new WarrantyClaimUnitInput(openingUnit.Id, openingUnit.SerialNumber)])
                ],
                Guid.CreateVersion7()),
            CancellationToken.None);

        Assert.False(mismatchResult.IsSuccess);
        Assert.Equal("warranty.customer_sale_mismatch", mismatchResult.Error?.Code);
    }

    // 14. ShopStockWarranty_RemainsSeparateFromCustomerClaim
    [Fact]
    public async Task ShopStockWarranty_RemainsSeparateFromCustomerClaim()
    {
        var (supplier, customer, product, _, unitEntity, sale, saleItem, supplierProduct) = SeedSoldSerializedFixture();
        var actorId = Guid.CreateVersion7();

        // Create Customer Warranty Claim
        var claimResult = await CreateClaimHandler().HandleAsync(
            new CreateWarrantyClaimCommand(
                customer.Id,
                sale.Id,
                supplier.Id,
                actorId,
                [
                    new WarrantyClaimItemInput(
                        product.Id,
                        1m,
                        "Customer issue",
                        saleItem.Id,
                        saleItem.WarrantyValidUntil,
                        [new WarrantyClaimUnitInput(unitEntity.Id, unitEntity.SerialNumber)])
                ],
                Guid.CreateVersion7()),
            CancellationToken.None);
        Assert.True(claimResult.IsSuccess);
        var customerClaimId = claimResult.Value;

        // Invariant: Customer claim CANNOT be processed by Shop Stock handler
        var shopReceiveOnCustomerClaim = await CreateReceiveShopStockHandler().HandleAsync(
            new ReceiveShopStockWarrantyCommand(
                customerClaimId, // Passing Customer Claim ID to Shop Stock handler
                WarrantyResolutionType.Repaired,
                actorId,
                OriginalInventoryUnitIds: [unitEntity.Id],
                ReplacementUnits: null,
                Note: "Invalid crossover attempt",
                ClientOperationId: Guid.CreateVersion7()),
            CancellationToken.None);

        Assert.False(shopReceiveOnCustomerClaim.IsSuccess);
        Assert.Equal("warranty.shop_case_not_found", shopReceiveOnCustomerClaim.Error?.Code);

        // Create Shop Stock Case
        var damagedLotId = Guid.CreateVersion7();
        _fakes.Inventory.AddLot(new InventoryLot
        {
            Id = damagedLotId,
            ProductId = product.Id,
            OriginalUnitCost = 4000m,
            EffectiveUnitCost = 4000m,
            CreatedAt = DateTimeOffset.UtcNow
        });
        _fakes.Inventory.AddLotBucketBalance(new InventoryLotBucketBalance { LotId = damagedLotId, StockBucket = InventoryBucket.Damaged, Quantity = 1m });
        _fakes.Inventory.AddStockBalance(new StockBalance { ProductId = product.Id, DamagedQty = 1m });

        var shopDamagedUnit = new InventoryUnit
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            InventoryLotId = damagedLotId,
            SupplierProductId = supplierProduct.Id,
            SerialNumber = "SN-SHOP-DEFECT",
            TrackingCode = "MT1-S24-ULTRA-000099",
            Status = InventoryUnitStatus.Damaged,
            AcquisitionCost = 250000m,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _fakes.Inventory.AddInventoryUnit(shopDamagedUnit);

        var sendShopResult = await CreateSendShopStockHandler().HandleAsync(
            new SendShopStockToSupplierWarrantyCommand(
                product.Id,
                InventoryBucket.Damaged,
                1m,
                supplier.Id,
                SourcePurchaseItemId: null,
                "Shop stock transit damage",
                actorId,
                Guid.CreateVersion7(),
                [shopDamagedUnit.Id]),
            CancellationToken.None);
        Assert.True(sendShopResult.IsSuccess, sendShopResult.Error?.Message);
        var shopCaseId = sendShopResult.Value;

        // Invariant: Shop Stock case CANNOT be processed by Customer Claim handlers
        var customerReviewOnShopCase = await CreateBeginReviewHandler().HandleAsync(
            new BeginWarrantyClaimReviewCommand(shopCaseId, actorId, Guid.CreateVersion7()),
            CancellationToken.None);

        Assert.False(customerReviewOnShopCase.IsSuccess);
        Assert.Equal("warranty.claim_not_found", customerReviewOnShopCase.Error?.Code);

        var customerHandoverOnShopCase = await CreateHandoverHandler().HandleAsync(
            new HandoverWarrantyItemCommand(shopCaseId, actorId, Guid.CreateVersion7()),
            CancellationToken.None);

        Assert.False(customerHandoverOnShopCase.IsSuccess);
        Assert.Equal("warranty.claim_not_found", customerHandoverOnShopCase.Error?.Code);
    }
}
