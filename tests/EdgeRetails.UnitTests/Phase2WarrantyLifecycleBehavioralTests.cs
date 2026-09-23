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

public sealed class Phase2WarrantyLifecycleBehavioralTests
{
    private readonly Phase2TestDoubles _fakes = new();
    private readonly InventoryConditionService _conditions;

    public Phase2WarrantyLifecycleBehavioralTests()
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

    private CancelWarrantyClaimHandler CreateCancellationHandler() =>
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

    private (Supplier Supplier, Customer Customer, Product Product, ProductUnit Unit, InventoryUnit UnitEntity, Sale Sale, SaleItem SaleItem) SeedSoldSerializedFixture()
    {
        var supplier = new Supplier { Id = Guid.CreateVersion7(), Name = "Mega Tech", DealerCode = "MT1", IsActive = true };
        _fakes.Parties.AddSupplier(supplier);

        var customer = new Customer { Id = Guid.CreateVersion7(), Name = "John Customer", Phone = "03001234567", IsActive = true };
        _fakes.Parties.AddCustomer(customer);

        var unit = new Unit { Id = Guid.CreateVersion7(), Name = "Piece", Symbol = "pc" };
        var product = new Product
        {
            Id = Guid.CreateVersion7(),
            Name = "Galaxy S24",
            Sku = "S24-ULTRA",
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
            NextItemSequence = 2
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

        var inventoryUnit = new InventoryUnit
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            SupplierProductId = supplierProduct.Id,
            SourcePurchaseItemId = purchaseItem.Id,
            SerialNumber = "SN-S24-001",
            Imei1 = "860123456789016",
            TrackingCode = "MT1-S24-ULTRA-000001",
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
            WarrantyValidUntil = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(335)) // Active warranty
        };
        _fakes.Sales.AddSaleItem(saleItem);

        var saleItemUnit = new SaleItemUnit
        {
            SaleItemId = saleItem.Id,
            InventoryUnitId = inventoryUnit.Id
        };
        _fakes.Sales.AddSaleItemUnit(saleItemUnit);

        return (supplier, customer, product, pu, inventoryUnit, sale, saleItem);
    }

    [Fact]
    public async Task CustomerWarrantyClaim_Creation_And_DuplicateActiveClaim_Prevention()
    {
        var (supplier, customer, product, _, unitEntity, sale, saleItem) = SeedSoldSerializedFixture();
        var claimHandler = CreateClaimHandler();
        var actorId = Guid.CreateVersion7();

        // 1. Create initial claim
        var claimResult = await claimHandler.HandleAsync(
            new CreateWarrantyClaimCommand(
                customer.Id,
                sale.Id,
                supplier.Id,
                actorId,
                [
                    new WarrantyClaimItemInput(
                        product.Id,
                        1m,
                        "Screen flickering artifact",
                        saleItem.Id,
                        DateOnly.FromDateTime(DateTime.UtcNow.AddDays(335)),
                        [new WarrantyClaimUnitInput(unitEntity.Id, "SN-S24-001")])
                ],
                Guid.NewGuid()),
            CancellationToken.None);

        Assert.True(claimResult.IsSuccess, claimResult.Error?.Message);
        var claimId = claimResult.Value;

        // Claim status is Received and Custody is WithShop
        var claim = await _fakes.Warranty.GetClaimForUpdateAsync(claimId, CancellationToken.None);
        Assert.NotNull(claim);
        Assert.Equal(WarrantyClaimStatus.Received, claim!.Status);
        Assert.Equal(WarrantyCustody.WithShop, claim.CurrentCustody);

        // 2. Attempting a second claim on the same unit while active MUST fail
        var dupResult = await claimHandler.HandleAsync(
            new CreateWarrantyClaimCommand(
                customer.Id,
                sale.Id,
                supplier.Id,
                actorId,
                [
                    new WarrantyClaimItemInput(
                        product.Id,
                        1m,
                        "Duplicate claim attempt",
                        saleItem.Id,
                        DateOnly.FromDateTime(DateTime.UtcNow.AddDays(335)),
                        [new WarrantyClaimUnitInput(unitEntity.Id, "SN-S24-001")])
                ],
                Guid.NewGuid()),
            CancellationToken.None);

        Assert.False(dupResult.IsSuccess);
        Assert.Equal("warranty.active_claim_exists", dupResult.Error?.Code);
    }

    [Fact]
    public async Task CustomerWarrantyClaim_SameClientOperationId_ReplaysWithoutDuplicate()
    {
        var (supplier, customer, product, _, unitEntity, sale, saleItem) = SeedSoldSerializedFixture();
        var claimHandler = CreateClaimHandler();
        var actorId = Guid.CreateVersion7();
        var clientOperationId = Guid.CreateVersion7();

        var command = new CreateWarrantyClaimCommand(
            customer.Id,
            sale.Id,
            supplier.Id,
            actorId,
            [
                new WarrantyClaimItemInput(
                    product.Id,
                    1m,
                    "Replay-safe warranty claim",
                    saleItem.Id,
                    DateOnly.FromDateTime(DateTime.UtcNow.AddDays(335)),
                    [new WarrantyClaimUnitInput(unitEntity.Id, "SN-S24-001")])
            ],
            clientOperationId);

        var first = await claimHandler.HandleAsync(command, CancellationToken.None);
        Assert.True(first.IsSuccess, first.Error?.Message);

        var replay = await claimHandler.HandleAsync(command, CancellationToken.None);
        Assert.True(replay.IsSuccess, replay.Error?.Message);
        Assert.Equal(first.Value, replay.Value);
        Assert.Single(_fakes.Warranty.Claims);
        Assert.Single(_fakes.Warranty.ClaimItems);
        Assert.Single(_fakes.Warranty.ClaimEvents);
    }

    [Fact]
    public async Task CustomerWarrantyClaim_FullCustodyFlow_And_ReplacementHandover()
    {
        var (supplier, customer, product, _, unitEntity, sale, saleItem) = SeedSoldSerializedFixture();
        var claimHandler = CreateClaimHandler();
        var beginReviewHandler = CreateBeginReviewHandler();
        var sendToSupplierHandler = CreateSendClaimToSupplierHandler();
        var supplierProcessingHandler = CreateSupplierProcessingHandler();
        var receiveReplacementHandler = CreateReceiveCustomerReplacementHandler();
        var handoverHandler = CreateHandoverHandler();
        var actorId = Guid.CreateVersion7();

        // 1. Create claim
        var claimResult = await claimHandler.HandleAsync(
            new CreateWarrantyClaimCommand(
                customer.Id,
                sale.Id,
                supplier.Id,
                actorId,
                [
                    new WarrantyClaimItemInput(
                        product.Id,
                        1m,
                        "Dead pixels on screen",
                        saleItem.Id,
                        DateOnly.FromDateTime(DateTime.UtcNow.AddDays(335)),
                        [new WarrantyClaimUnitInput(unitEntity.Id, "SN-S24-001")])
                ],
                Guid.NewGuid()),
            CancellationToken.None);

        Assert.True(claimResult.IsSuccess);
        var claimId = claimResult.Value;
        var claimItem = _fakes.Warranty.ClaimItems.Single(i => i.ClaimId == claimId);
        var claimItemUnit = _fakes.Warranty.ClaimItemUnits.Single(u => u.ClaimItemId == claimItem.Id);

        // 2. Begin review -> UnderReview
        var rev = await beginReviewHandler.HandleAsync(new BeginWarrantyClaimReviewCommand(claimId, actorId, Guid.CreateVersion7(), "Visual inspection confirmed"), CancellationToken.None);
        Assert.True(rev.IsSuccess);

        // 3. Send to supplier -> SentToSupplier, Custody = WithSupplier
        var send = await sendToSupplierHandler.HandleAsync(new SendWarrantyClaimToSupplierCommand(claimId, actorId, Guid.CreateVersion7(), "Shipped to official supplier center"), CancellationToken.None);
        Assert.True(send.IsSuccess);
        var claimAfterSend = await _fakes.Warranty.GetClaimForUpdateAsync(claimId, CancellationToken.None);
        Assert.Equal(WarrantyCustody.WithSupplier, claimAfterSend!.CurrentCustody);

        // 4. Supplier processing -> SupplierProcessing
        var proc = await supplierProcessingHandler.HandleAsync(new MarkWarrantySupplierProcessingCommand(claimId, actorId, Guid.CreateVersion7(), "Supplier processing ticket #8892"), CancellationToken.None);
        Assert.True(proc.IsSuccess);

        // 5. Receive replacement unit from supplier -> creates new tracking code and WarrantyCustomerHeld status
        var repResult = await receiveReplacementHandler.HandleAsync(
            new ReceiveCustomerWarrantyReplacementCommand(
                claimId,
                actorId,
                Guid.CreateVersion7(),
                [
                    new CustomerWarrantyReplacementUnitInput(
                        claimItemUnit.Id,
                        "SN-S24-REPLACEMENT",
                        "860123456789024",
                        null)
                ],
                "Supplier replaced whole device with new unit"),
            CancellationToken.None);

        Assert.True(repResult.IsSuccess, repResult.Error?.Message);

        var replacementUnit = _fakes.Inventory.Units.Single(u => u.SerialNumber == "SN-S24-REPLACEMENT");
        Assert.Equal(InventoryUnitStatus.WarrantyCustomerHeld, replacementUnit.Status);
        Assert.Equal("MT1-S24-ULTRA-000002", replacementUnit.TrackingCode);

        // Invariant: Customer-held units do NOT add to shop stock balance or inventory cost
        Assert.False(_fakes.Inventory.Balances.ContainsKey(product.Id));

        // 6. Handover to customer -> Closed, Custody = WithCustomer, status = WarrantyCustomerHandedOver
        var handoverResult = await handoverHandler.HandleAsync(
            new HandoverWarrantyItemCommand(claimId, actorId, Guid.CreateVersion7(), "Customer received brand new sealed replacement"),
            CancellationToken.None);

        Assert.True(handoverResult.IsSuccess, handoverResult.Error?.Message);

        var finalClaim = await _fakes.Warranty.GetClaimForUpdateAsync(claimId, CancellationToken.None);
        Assert.Equal(WarrantyClaimStatus.Closed, finalClaim!.Status);
        Assert.Equal(WarrantyCustody.WithCustomer, finalClaim.CurrentCustody);
        Assert.Equal(InventoryUnitStatus.WarrantyCustomerHandedOver, replacementUnit.Status);
    }

    [Fact]
    public async Task ShopStockWarranty_SendToSupplier_And_ReceiveCredit_CalculatesRecoveryDifference()
    {
        var supplier = new Supplier { Id = Guid.CreateVersion7(), Name = "Direct Importer", DealerCode = "DI1", IsActive = true };
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

        var supplierProduct = new SupplierProduct
        {
            Id = Guid.CreateVersion7(),
            SupplierId = supplier.Id,
            ProductId = product.Id,
            NextItemSequence = 2
        };
        _fakes.Traceability.AddSupplierProduct(supplierProduct);

        var purchase = new Purchase
        {
            Id = Guid.CreateVersion7(),
            SupplierId = supplier.Id,
            PurchaseNumber = "PUR-MON-01",
            SupplierInvoiceNumber = "SINV-MON",
            PurchaseDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30)),
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-30)
        };
        _fakes.Purchasing.AddPurchase(purchase);

        var purchaseItem = new PurchaseItem
        {
            Id = Guid.CreateVersion7(),
            PurchaseId = purchase.Id,
            ProductId = product.Id,
            ProductUnitId = pu.Id,
            BaseQuantity = 1m,
            EnteredUnitCost = 60000m,
            EffectiveBaseUnitCost = 60000m,
            BaseLineTotal = 60000m,
            EffectiveLineCost = 60000m
        };
        _fakes.Purchasing.AddPurchaseItem(purchaseItem);

        var lot = new InventoryLot
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            PurchaseItemId = purchaseItem.Id,
            ReceivedQuantity = 1m,
            OriginalUnitCost = 60000m,
            EffectiveUnitCost = 60000m,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-30)
        };
        _fakes.Inventory.AddLot(lot);

        _fakes.Inventory.AddLotBucketBalance(new InventoryLotBucketBalance
        {
            LotId = lot.Id,
            StockBucket = InventoryBucket.Damaged,
            Quantity = 1m
        });

        var damagedUnit = new InventoryUnit
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            InventoryLotId = lot.Id,
            SupplierProductId = supplierProduct.Id,
            SourcePurchaseItemId = purchaseItem.Id,
            SerialNumber = "SN-MON-DEFECT",
            TrackingCode = "DI1-MON-4K-000001",
            Status = InventoryUnitStatus.Damaged,
            AcquisitionCost = 60000m,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _fakes.Inventory.AddInventoryUnit(damagedUnit);

        _fakes.Inventory.AddStockBalance(new StockBalance
        {
            ProductId = product.Id,
            DamagedQty = 1m
        });

        _fakes.Inventory.AddCostState(new ProductCostState
        {
            ProductId = product.Id,
            CostedQty = 1m,
            TotalInventoryCost = 60000m,
            MovingAverageCost = 60000m,
            LastPurchaseCost = 60000m,
            Version = 1
        });

        var sendHandler = CreateSendShopStockHandler();
        var receiveHandler = CreateReceiveShopStockHandler();
        var actorId = Guid.CreateVersion7();

        // 1. Send damaged unit to supplier
        var sendResult = await sendHandler.HandleAsync(
            new SendShopStockToSupplierWarrantyCommand(
                product.Id,
                InventoryBucket.Damaged,
                1m,
                supplier.Id,
                SourcePurchaseItemId: null,
                "Factory panel vertical line defect",
                actorId,
                Guid.CreateVersion7(),
                [damagedUnit.Id]),
            CancellationToken.None);

        Assert.True(sendResult.IsSuccess, sendResult.Error?.Message);
        var caseId = sendResult.Value;

        var warrantyCase = await _fakes.Warranty.GetShopStockCaseAsync(caseId, CancellationToken.None);
        Assert.NotNull(warrantyCase);
        Assert.Equal(ShopWarrantyCaseStatus.WithSupplier, warrantyCase!.Status);
        Assert.Equal(InventoryUnitStatus.WithSupplier, damagedUnit.Status);

        // 2. Receive supplier resolution: Credit of 60000
        var receiveResult = await receiveHandler.HandleAsync(
            new ReceiveShopStockWarrantyCommand(
                caseId,
                WarrantyResolutionType.Credited,
                actorId,
                OriginalInventoryUnitIds: [damagedUnit.Id],
                ReplacementUnits: null,
                Note: "Full supplier credit issued on Khata",
                ClientOperationId: Guid.CreateVersion7(),
                SupplierCreditAmount: 60000m,
                SupplierReference: "CR-9921"),
            CancellationToken.None);

        Assert.True(receiveResult.IsSuccess, receiveResult.Error?.Message);

        // Khata WARRANTY_CREDIT created (DECREASE_PAYABLE 60000)
        var khataEntry = _fakes.SupplierAccounts.Entries.Last();
        Assert.Equal(SupplierAccountEntryType.WarrantyCredit, khataEntry.EntryType);
        Assert.Equal(SupplierAccountDirection.DecreasePayable, khataEntry.Direction);
        Assert.Equal(60000m, khataEntry.Amount);

        // Carrying value removed
        Assert.Equal(0m, _fakes.Inventory.CostStates[product.Id].CostedQty);
        Assert.Equal(0m, _fakes.Inventory.CostStates[product.Id].TotalInventoryCost);
    }

    [Fact]
    public async Task CustomerWarrantyLifecycle_ReplaySameClientOperationId_DoesNotDuplicateSideEffects()
    {
        var (supplier, customer, product, _, unitEntity, sale, saleItem) = SeedSoldSerializedFixture();
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
                        "Unknown-outcome replay customer claim",
                        saleItem.Id,
                        DateOnly.FromDateTime(DateTime.UtcNow.AddDays(335)),
                        [new WarrantyClaimUnitInput(unitEntity.Id, "SN-S24-001")])
                ],
                Guid.CreateVersion7()),
            CancellationToken.None);

        Assert.True(claimResult.IsSuccess, claimResult.Error?.Message);
        var claimId = claimResult.Value;
        var claimItemUnitId = _fakes.Warranty.ClaimItemUnits.Single().Id;
        var initialClaimEventCount = _fakes.Warranty.ClaimEvents.Count;

        var reviewId = Guid.CreateVersion7();
        await BeginReviewAndReplayAsync(claimId, actorId, reviewId);
        Assert.True(_fakes.Warranty.ClaimEvents.Count == initialClaimEventCount + 1);
        Assert.Single(_fakes.Warranty.Operations, x => x.ClientOperationId == reviewId);

        var sendId = Guid.CreateVersion7();
        await SendClaimAndReplayAsync(claimId, actorId, sendId);
        Assert.True(_fakes.Warranty.ClaimEvents.Count == initialClaimEventCount + 2);
        Assert.Single(_fakes.Warranty.Operations, x => x.ClientOperationId == sendId);

        var processingId = Guid.CreateVersion7();
        await SupplierProcessingAndReplayAsync(claimId, actorId, processingId);
        Assert.True(_fakes.Warranty.ClaimEvents.Count == initialClaimEventCount + 3);
        Assert.Single(_fakes.Warranty.Operations, x => x.ClientOperationId == processingId);

        var replacementId = Guid.CreateVersion7();
        var replacement = new CustomerWarrantyReplacementUnitInput(
            claimItemUnitId,
            "SN-S24-REPLAY",
            "860123456789032",
            null);
        var replacementCommand = new ReceiveCustomerWarrantyReplacementCommand(
            claimId,
            actorId,
            replacementId,
            [replacement],
            "Commit then recover the same customer replacement outcome.");

        var replacementFirst = await CreateReceiveCustomerReplacementHandler().HandleAsync(
            replacementCommand,
            CancellationToken.None);
        Assert.True(replacementFirst.IsSuccess, replacementFirst.Error?.Message);
        var unitsAfterFirst = _fakes.Inventory.Units.Count;
        var eventsAfterFirst = _fakes.Warranty.ClaimEvents.Count;
        var operationsAfterFirst = _fakes.Warranty.Operations.Count;

        // Semantic unknown-outcome simulation: the first transaction committed, but the caller
        // discards the response and retries the same user intent with the same operation ID.
        var replacementReplay = await CreateReceiveCustomerReplacementHandler().HandleAsync(
            replacementCommand,
            CancellationToken.None);

        Assert.True(replacementReplay.IsSuccess, replacementReplay.Error?.Message);
        Assert.Equal(unitsAfterFirst, _fakes.Inventory.Units.Count);
        Assert.Equal(eventsAfterFirst, _fakes.Warranty.ClaimEvents.Count);
        Assert.Equal(operationsAfterFirst, _fakes.Warranty.Operations.Count);
        Assert.Single(_fakes.Inventory.Units, x => x.SerialNumber == "SN-S24-REPLAY");
        Assert.Single(_fakes.Warranty.Operations, x => x.ClientOperationId == replacementId);

        var handoverId = Guid.CreateVersion7();
        var eventsBeforeHandover = _fakes.Warranty.ClaimEvents.Count;
        await HandoverAndReplayAsync(claimId, actorId, handoverId);
        var finalClaim = await _fakes.Warranty.GetClaimForUpdateAsync(claimId, CancellationToken.None);
        Assert.Equal(WarrantyClaimStatus.Closed, finalClaim!.Status);
        Assert.True(_fakes.Warranty.ClaimEvents.Count == eventsBeforeHandover + 1);
        Assert.Single(_fakes.Warranty.Operations, x => x.ClientOperationId == handoverId);
    }

    [Fact]
    public async Task CustomerWarrantyResolution_ReplaySameClientOperationId_DoesNotDuplicateTimeline()
    {
        var (supplier, customer, product, _, unitEntity, sale, saleItem) = SeedSoldSerializedFixture();
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
                        "Resolution replay",
                        saleItem.Id,
                        DateOnly.FromDateTime(DateTime.UtcNow.AddDays(335)),
                        [new WarrantyClaimUnitInput(unitEntity.Id, "SN-S24-001")])
                ],
                Guid.CreateVersion7()),
            CancellationToken.None);

        Assert.True(claimResult.IsSuccess, claimResult.Error?.Message);
        var claimId = claimResult.Value;
        var reviewId = Guid.CreateVersion7();
        await BeginReviewAndReplayAsync(claimId, actorId, reviewId);
        var sendId = Guid.CreateVersion7();
        await SendClaimAndReplayAsync(claimId, actorId, sendId);
        var processingId = Guid.CreateVersion7();
        await SupplierProcessingAndReplayAsync(claimId, actorId, processingId);

        var resolutionId = Guid.CreateVersion7();
        var command = new RecordWarrantyResolutionCommand(
            claimId,
            WarrantyResolutionType.Repaired,
            actorId,
            resolutionId,
            "Commit then recover the same repaired outcome.");

        var first = await CreateResolutionHandler().HandleAsync(command, CancellationToken.None);
        Assert.True(first.IsSuccess, first.Error?.Message);
        var eventCount = _fakes.Warranty.ClaimEvents.Count;
        var operationCount = _fakes.Warranty.Operations.Count;

        var replay = await CreateResolutionHandler().HandleAsync(command, CancellationToken.None);
        Assert.True(replay.IsSuccess, replay.Error?.Message);
        Assert.Equal(eventCount, _fakes.Warranty.ClaimEvents.Count);
        Assert.Equal(operationCount, _fakes.Warranty.Operations.Count);
        Assert.Single(_fakes.Warranty.ClaimEvents, x => x.EventType == "RESOLVED_REPAIRED");
        Assert.Single(_fakes.Warranty.Operations, x => x.ClientOperationId == resolutionId);
    }

    [Fact]
    public async Task CustomerWarrantyCancellation_ReplaySameClientOperationId_DoesNotDuplicateCancellation()
    {
        var (supplier, customer, _, _, unitEntity, sale, saleItem) = SeedSoldSerializedFixture();
        var actorId = Guid.CreateVersion7();
        var claimResult = await CreateClaimHandler().HandleAsync(
            new CreateWarrantyClaimCommand(
                customer.Id,
                sale.Id,
                supplier.Id,
                actorId,
                [
                    new WarrantyClaimItemInput(
                        ProductId: _fakes.Catalog.Products.Values.Single().Id,
                        Quantity: 1m,
                        FaultDescription: "Cancellation replay",
                        OriginalSaleItemId: saleItem.Id,
                        WarrantyValidUntil: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(335)),
                        Units: [new WarrantyClaimUnitInput(unitEntity.Id, "SN-S24-001")])
                ],
                Guid.CreateVersion7()),
            CancellationToken.None);

        Assert.True(claimResult.IsSuccess, claimResult.Error?.Message);
        var cancellationId = Guid.CreateVersion7();
        var command = new CancelWarrantyClaimCommand(
            claimResult.Value,
            actorId,
            cancellationId,
            "Commit then recover the same cancellation outcome.");

        var first = await CreateCancellationHandler().HandleAsync(command, CancellationToken.None);
        Assert.True(first.IsSuccess, first.Error?.Message);
        var eventCount = _fakes.Warranty.ClaimEvents.Count;

        var replay = await CreateCancellationHandler().HandleAsync(command, CancellationToken.None);
        Assert.True(replay.IsSuccess, replay.Error?.Message);

        var claim = await _fakes.Warranty.GetClaimForUpdateAsync(claimResult.Value, CancellationToken.None);
        Assert.Equal(WarrantyClaimStatus.Cancelled, claim!.Status);
        Assert.Equal(eventCount, _fakes.Warranty.ClaimEvents.Count);
        Assert.Single(_fakes.Warranty.ClaimEvents, x => x.EventType == "CLAIM_CANCELLED");
        Assert.Single(_fakes.Warranty.Operations, x => x.ClientOperationId == cancellationId);
    }

    [Fact]
    public async Task WarrantyOperation_PayloadMismatch_IsRejectedWithoutSecondMutation()
    {
        var (supplier, customer, _, _, unitEntity, sale, saleItem) = SeedSoldSerializedFixture();
        var actorId = Guid.CreateVersion7();
        var claimResult = await CreateClaimHandler().HandleAsync(
            new CreateWarrantyClaimCommand(
                customer.Id,
                sale.Id,
                supplier.Id,
                actorId,
                [
                    new WarrantyClaimItemInput(
                        _fakes.Catalog.Products.Values.Single().Id,
                        1m,
                        "Payload mismatch",
                        saleItem.Id,
                        DateOnly.FromDateTime(DateTime.UtcNow.AddDays(335)),
                        [new WarrantyClaimUnitInput(unitEntity.Id, "SN-S24-001")])
                ],
                Guid.CreateVersion7()),
            CancellationToken.None);

        Assert.True(claimResult.IsSuccess, claimResult.Error?.Message);
        var operationId = Guid.CreateVersion7();
        var first = await CreateBeginReviewHandler().HandleAsync(
            new BeginWarrantyClaimReviewCommand(claimResult.Value, actorId, operationId, "Original payload"),
            CancellationToken.None);
        Assert.True(first.IsSuccess, first.Error?.Message);

        var mismatch = await CreateBeginReviewHandler().HandleAsync(
            new BeginWarrantyClaimReviewCommand(claimResult.Value, actorId, operationId, "Changed payload"),
            CancellationToken.None);

        Assert.False(mismatch.IsSuccess);
        Assert.Equal("payload_mismatch", mismatch.Error?.Code);
        Assert.Single(_fakes.Warranty.ClaimEvents, x => x.EventType == "UNDER_REVIEW");
        Assert.Single(_fakes.Warranty.Operations, x => x.ClientOperationId == operationId);
    }

    [Theory]
    [InlineData(WarrantyResolutionType.Repaired)]
    [InlineData(WarrantyResolutionType.Rejected)]
    [InlineData(WarrantyResolutionType.Scrapped)]
    [InlineData(WarrantyResolutionType.Replaced)]
    [InlineData(WarrantyResolutionType.Credited)]
    public async Task ShopStockWarranty_EachReceiveOutcome_ReplaySameClientOperationId_IsExactlyOnce(
        WarrantyResolutionType resolution)
    {
        var fixture = SeedSerializedShopWarrantyFixture();
        var actorId = Guid.CreateVersion7();
        var sendOperationId = Guid.CreateVersion7();
        var sendCommand = new SendShopStockToSupplierWarrantyCommand(
            fixture.Product.Id,
            InventoryBucket.Damaged,
            1m,
            fixture.Supplier.Id,
            SourcePurchaseItemId: null,
            "Shop warranty idempotency fixture",
            actorId,
            sendOperationId,
            [fixture.Unit.Id]);

        var sendFirst = await CreateSendShopStockHandler().HandleAsync(sendCommand, CancellationToken.None);
        Assert.True(sendFirst.IsSuccess, sendFirst.Error?.Message);
        var sendReplay = await CreateSendShopStockHandler().HandleAsync(sendCommand, CancellationToken.None);
        Assert.True(sendReplay.IsSuccess, sendReplay.Error?.Message);
        Assert.Equal(sendFirst.Value, sendReplay.Value);
        Assert.Single(_fakes.Warranty.ShopStockCases);
        Assert.Single(_fakes.Warranty.Operations, x => x.ClientOperationId == sendOperationId);

        var replacementInputs = resolution == WarrantyResolutionType.Replaced
            ? new List<ReplacementSerializedUnitInput> { new("SN-SHOP-REPLACEMENT", "860123456789041", null) }
            : null;
        var receiveOperationId = Guid.CreateVersion7();
        var receiveCommand = new ReceiveShopStockWarrantyCommand(
            sendFirst.Value,
            resolution,
            actorId,
            OriginalInventoryUnitIds: [fixture.Unit.Id],
            replacementInputs,
            "Commit then recover the same shop-stock resolution outcome.",
            receiveOperationId,
            SupplierCreditAmount: resolution == WarrantyResolutionType.Credited ? 60000m : null,
            SupplierReference: "SUP-REPLAY-01");

        var receiveFirst = await CreateReceiveShopStockHandler().HandleAsync(receiveCommand, CancellationToken.None);
        Assert.True(receiveFirst.IsSuccess, receiveFirst.Error?.Message);

        var movementCount = _fakes.Inventory.Movements.Count;
        var movementUnitCount = _fakes.Inventory.MovementUnits.Count;
        var unitCount = _fakes.Inventory.Units.Count;
        var supplierEntryCount = _fakes.SupplierAccounts.Entries.Count;
        var operationCount = _fakes.Warranty.Operations.Count;

        var receiveReplay = await CreateReceiveShopStockHandler().HandleAsync(receiveCommand, CancellationToken.None);
        Assert.True(receiveReplay.IsSuccess, receiveReplay.Error?.Message);

        Assert.Equal(movementCount, _fakes.Inventory.Movements.Count);
        Assert.Equal(movementUnitCount, _fakes.Inventory.MovementUnits.Count);
        Assert.Equal(unitCount, _fakes.Inventory.Units.Count);
        Assert.Equal(supplierEntryCount, _fakes.SupplierAccounts.Entries.Count);
        Assert.Equal(operationCount, _fakes.Warranty.Operations.Count);
        Assert.Single(_fakes.Warranty.Operations, x => x.ClientOperationId == receiveOperationId);

        var shopCase = await _fakes.Warranty.GetShopStockCaseAsync(sendFirst.Value, CancellationToken.None);
        Assert.NotNull(shopCase);
        Assert.Equal(
            resolution == WarrantyResolutionType.Scrapped
                ? ShopWarrantyCaseStatus.WrittenOff
                : ShopWarrantyCaseStatus.Closed,
            shopCase!.Status);

        if (resolution == WarrantyResolutionType.Replaced)
        {
            var replacement = _fakes.Inventory.Units.Single(x => x.SerialNumber == "SN-SHOP-REPLACEMENT");
            Assert.Equal(InventoryUnitOriginType.WarrantyReplacement, replacement.OriginType);
            Assert.Equal(fixture.Product.Id, replacement.ProductId);
            Assert.Equal(fixture.SupplierProduct.Id, replacement.SupplierProductId);
            Assert.Equal(shopCase.Id, replacement.SourceWarrantyCaseId);
            Assert.Equal(InventoryUnitStatus.InStock, replacement.Status);
            Assert.Single(_fakes.Inventory.Units, x => x.SerialNumber == "SN-SHOP-REPLACEMENT");
        }

        if (resolution == WarrantyResolutionType.Credited)
        {
            var credit = _fakes.SupplierAccounts.Entries.Single();
            Assert.Equal(SupplierAccountEntryType.WarrantyCredit, credit.EntryType);
            Assert.Equal(receiveOperationId, credit.ClientOperationId);
            Assert.Equal(60000m, credit.Amount);
        }
    }

    private async Task BeginReviewAndReplayAsync(Guid claimId, Guid actorId, Guid operationId)
    {
        var command = new BeginWarrantyClaimReviewCommand(claimId, actorId, operationId, "same review intent");
        var first = await CreateBeginReviewHandler().HandleAsync(command, CancellationToken.None);
        Assert.True(first.IsSuccess, first.Error?.Message);
        var replay = await CreateBeginReviewHandler().HandleAsync(command, CancellationToken.None);
        Assert.True(replay.IsSuccess, replay.Error?.Message);
    }

    private async Task SendClaimAndReplayAsync(Guid claimId, Guid actorId, Guid operationId)
    {
        var command = new SendWarrantyClaimToSupplierCommand(claimId, actorId, operationId, "same send intent");
        var first = await CreateSendClaimToSupplierHandler().HandleAsync(command, CancellationToken.None);
        Assert.True(first.IsSuccess, first.Error?.Message);
        var replay = await CreateSendClaimToSupplierHandler().HandleAsync(command, CancellationToken.None);
        Assert.True(replay.IsSuccess, replay.Error?.Message);
    }

    private async Task SupplierProcessingAndReplayAsync(Guid claimId, Guid actorId, Guid operationId)
    {
        var command = new MarkWarrantySupplierProcessingCommand(claimId, actorId, operationId, "same processing intent");
        var first = await CreateSupplierProcessingHandler().HandleAsync(command, CancellationToken.None);
        Assert.True(first.IsSuccess, first.Error?.Message);
        var replay = await CreateSupplierProcessingHandler().HandleAsync(command, CancellationToken.None);
        Assert.True(replay.IsSuccess, replay.Error?.Message);
    }

    private async Task HandoverAndReplayAsync(Guid claimId, Guid actorId, Guid operationId)
    {
        var command = new HandoverWarrantyItemCommand(claimId, actorId, operationId, "same handover intent");
        var first = await CreateHandoverHandler().HandleAsync(command, CancellationToken.None);
        Assert.True(first.IsSuccess, first.Error?.Message);
        var replay = await CreateHandoverHandler().HandleAsync(command, CancellationToken.None);
        Assert.True(replay.IsSuccess, replay.Error?.Message);
    }

    private (Supplier Supplier, Product Product, SupplierProduct SupplierProduct, InventoryUnit Unit) SeedSerializedShopWarrantyFixture()
    {
        var supplier = new Supplier
        {
            Id = Guid.CreateVersion7(),
            Name = "Replay Supplier",
            DealerCode = "RS1",
            IsActive = true
        };
        _fakes.Parties.AddSupplier(supplier);

        var unit = new Unit { Id = Guid.CreateVersion7(), Name = "Piece", Symbol = "pc" };
        var product = new Product
        {
            Id = Guid.CreateVersion7(),
            Name = "Replay Monitor",
            Sku = "MON-REPLAY",
            BaseUnitId = unit.Id,
            TrackingMode = TrackingMode.Serialized,
            SerialTrackingEnabled = true,
            ImeiTrackingEnabled = true,
            DefaultSalePrice = 80000m,
            ReferencePurchaseCost = 60000m,
            IsActive = true
        };
        _fakes.Catalog.Products[product.Id] = product;

        var productUnit = new ProductUnit
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
        _fakes.Catalog.AddProductUnit(productUnit);

        var supplierProduct = new SupplierProduct
        {
            Id = Guid.CreateVersion7(),
            SupplierId = supplier.Id,
            ProductId = product.Id,
            NextItemSequence = 2
        };
        _fakes.Traceability.AddSupplierProduct(supplierProduct);

        var purchase = new Purchase
        {
            Id = Guid.CreateVersion7(),
            SupplierId = supplier.Id,
            PurchaseNumber = "PUR-REPLAY",
            SupplierInvoiceNumber = "SINV-REPLAY",
            PurchaseDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30)),
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-30)
        };
        _fakes.Purchasing.AddPurchase(purchase);

        var purchaseItem = new PurchaseItem
        {
            Id = Guid.CreateVersion7(),
            PurchaseId = purchase.Id,
            ProductId = product.Id,
            ProductUnitId = productUnit.Id,
            BaseQuantity = 1m,
            EnteredUnitCost = 60000m,
            EffectiveBaseUnitCost = 60000m,
            BaseLineTotal = 60000m,
            EffectiveLineCost = 60000m
        };
        _fakes.Purchasing.AddPurchaseItem(purchaseItem);

        var lot = new InventoryLot
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            PurchaseItemId = purchaseItem.Id,
            ReceivedQuantity = 1m,
            OriginalUnitCost = 60000m,
            EffectiveUnitCost = 60000m,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-30)
        };
        _fakes.Inventory.AddLot(lot);
        _fakes.Inventory.AddLotBucketBalance(new InventoryLotBucketBalance
        {
            LotId = lot.Id,
            StockBucket = InventoryBucket.Damaged,
            Quantity = 1m
        });

        var inventoryUnit = new InventoryUnit
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            InventoryLotId = lot.Id,
            SupplierProductId = supplierProduct.Id,
            SourcePurchaseItemId = purchaseItem.Id,
            SerialNumber = "SN-SHOP-DEFECT",
            Imei1 = "860123456789040",
            TrackingCode = "RS1-MON-REPLAY-000001",
            Status = InventoryUnitStatus.Damaged,
            AcquisitionCost = 60000m,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _fakes.Inventory.AddInventoryUnit(inventoryUnit);

        _fakes.Inventory.AddStockBalance(new StockBalance
        {
            ProductId = product.Id,
            DamagedQty = 1m
        });
        _fakes.Inventory.AddCostState(new ProductCostState
        {
            ProductId = product.Id,
            CostedQty = 1m,
            TotalInventoryCost = 60000m,
            MovingAverageCost = 60000m,
            LastPurchaseCost = 60000m,
            Version = 1
        });

        return (supplier, product, supplierProduct, inventoryUnit);
    }
}
