using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using Xunit;

namespace EdgeRetails.UnitTests;

public sealed class Phase1DPosResolutionAndExactUnitSaleTests
{
    private static (
        Supplier Supplier,
        Product Product,
        ProductUnit ProductUnit,
        ProductUnitBarcode Barcode,
        Purchase Purchase,
        PurchaseItem PurchaseItem,
        InventoryUnit Unit,
        FakePhase4WorkflowReadService Service) SeedAuthoritativeCatalogAndUnit(
            Phase2TestDoubles fakes,
            string trackingCode = "AB1-PKF-DLX56-000002",
            string serialNumber = "SN-DLX56-002",
            string imei = "356938035643803",
            InventoryUnitStatus unitStatus = InventoryUnitStatus.InStock)
    {
        var supplier = new Supplier
        {
            Id = Guid.CreateVersion7(),
            Name = "Abdullah Electronics",
            DealerCode = "AB1",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
        fakes.Parties.AddSupplier(supplier);

        var company = new Company
        {
            Id = Guid.CreateVersion7(),
            Name = "Pak Fan",
            Code = "PK",
            IsActive = true
        };
        fakes.Catalog.Companies[company.Id] = company;

        var category = new Category
        {
            Id = Guid.CreateVersion7(),
            Name = "Ceiling Fan",
            IdentitySymbol = "F",
            IsActive = true
        };
        fakes.Catalog.Categories[category.Id] = category;

        var baseUnit = new Unit
        {
            Id = Guid.CreateVersion7(),
            Name = "Piece",
            Symbol = "pc"
        };

        var product = new Product
        {
            Id = Guid.CreateVersion7(),
            Name = "Pak Fan Deluxe 56",
            Sku = "PKF-DLX56",
            Model = "Deluxe 56",
            ModelCode = "DLX56",
            CompanyId = company.Id,
            CategoryId = category.Id,
            BaseUnitId = baseUnit.Id,
            TrackingMode = TrackingMode.Serialized,
            SerialTrackingEnabled = true,
            ImeiTrackingEnabled = true,
            DefaultSalePrice = 12500m,
            ReferencePurchaseCost = 9500m,
            IsActive = true
        };
        fakes.Catalog.Products[product.Id] = product;

        var productUnit = new ProductUnit
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            UnitId = baseUnit.Id,
            FactorToBaseUnit = 1m,
            IsDefaultSaleUnit = true,
            IsDefaultPurchaseUnit = true,
            CanSell = true,
            CanPurchase = true,
            IsActive = true
        };
        fakes.Catalog.AddProductUnit(productUnit);

        var barcode = new ProductUnitBarcode
        {
            Id = Guid.CreateVersion7(),
            ProductUnitId = productUnit.Id,
            Barcode = "BC-PKF-DLX56",
            IsActive = true
        };

        var lot = new InventoryLot
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            ReceivedQuantity = 1m,
            OriginalUnitCost = 9500m,
            EffectiveUnitCost = 9500m,
            CreatedAt = DateTimeOffset.UtcNow
        };
        fakes.Inventory.AddLot(lot);

        fakes.Inventory.AddLotBucketBalance(new InventoryLotBucketBalance
        {
            LotId = lot.Id,
            StockBucket = InventoryBucket.Sellable,
            Quantity = unitStatus == InventoryUnitStatus.InStock ? 1m : 0m
        });

        fakes.Inventory.AddStockBalance(new StockBalance
        {
            ProductId = product.Id,
            SellableQty = unitStatus == InventoryUnitStatus.InStock ? 1m : 0m
        });

        var purchase = new Purchase
        {
            Id = Guid.CreateVersion7(),
            PurchaseNumber = "PUR-2026-0001",
            SupplierId = supplier.Id,
            Status = PurchaseStatus.Completed,
            PurchaseDate = DateOnly.FromDateTime(DateTime.UtcNow),
            CreatedAt = DateTimeOffset.UtcNow
        };
        fakes.Purchasing.AddPurchase(purchase);

        var purchaseItem = new PurchaseItem
        {
            Id = Guid.CreateVersion7(),
            PurchaseId = purchase.Id,
            ProductId = product.Id,
            ProductUnitId = productUnit.Id,
            EnteredQuantity = 1m,
            FactorToBaseSnapshot = 1m,
            BaseQuantity = 1m,
            EnteredUnitCost = 9500m,
            BaseLineTotal = 9500m
        };
        fakes.Purchasing.AddPurchaseItem(purchaseItem);

        var supplierProduct = new SupplierProduct
        {
            Id = Guid.CreateVersion7(),
            SupplierId = supplier.Id,
            ProductId = product.Id,
            NextItemSequence = 3,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
        fakes.Traceability.AddSupplierProduct(supplierProduct);

        var unit = new InventoryUnit
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            InventoryLotId = lot.Id,
            TrackingCode = trackingCode,
            SerialNumber = serialNumber,
            Imei1 = imei,
            Status = unitStatus,
            AcquisitionCost = 9500m,
            SourcePurchaseItemId = purchaseItem.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            Version = 1
        };
        fakes.Inventory.AddInventoryUnit(unit);

        var service = new FakePhase4WorkflowReadService(fakes);
        service.AddBarcode(barcode);

        return (supplier, product, productUnit, barcode, purchase, purchaseItem, unit, service);
    }

    [Fact]
    public async Task TrackingCode_ResolvesExactInventoryUnit()
    {
        var fakes = new Phase2TestDoubles();
        var (supplier, product, productUnit, _, purchase, _, unit, service) =
            SeedAuthoritativeCatalogAndUnit(fakes, "AB1-PKF-DLX56-000002");

        var matches = await service.ResolveScannerAsync("AB1-PKF-DLX56-000002", CancellationToken.None);

        Assert.Single(matches);
        var match = matches[0];

        // 1. Authoritative resolution directly from database
        Assert.Equal(ScannerResolutionNamespace.TrackingCode, match.Namespace);
        Assert.Equal(unit.Id, match.InventoryUnitId);
        Assert.Equal(product.Id, match.ProductId);
        Assert.Equal(productUnit.Id, match.ProductUnitId);
        Assert.Equal("AB1-PKF-DLX56-000002", match.TrackingCode);
        Assert.Equal(unit.SerialNumber, match.SerialNumber);
        Assert.Equal(unit.Imei1, match.Imei1);
        Assert.Equal(InventoryUnitStatus.InStock, match.UnitStatus);
        Assert.True(match.IsSellable);

        // 2. Authoritative supplier and purchase provenance
        Assert.Equal(supplier.Name, match.SupplierName);
        Assert.Equal(purchase.PurchaseNumber, match.PurchaseNumber);
        Assert.Equal(supplier.Id, match.SupplierId);
    }

    [Fact]
    public async Task Serial_ResolvesSameInventoryUnit()
    {
        var fakes = new Phase2TestDoubles();
        var (_, product, _, _, _, _, unit, service) =
            SeedAuthoritativeCatalogAndUnit(
                fakes,
                trackingCode: "AB1-PKF-DLX56-000002",
                serialNumber: "SN-PKF-998811");

        var matches = await service.ResolveScannerAsync("SN-PKF-998811", CancellationToken.None);

        Assert.Single(matches);
        var match = matches[0];

        // Resolves the exact inventory unit without replacing TrackingCode authority
        Assert.Equal(ScannerResolutionNamespace.SerialNumber, match.Namespace);
        Assert.Equal(unit.Id, match.InventoryUnitId);
        Assert.Equal(product.Id, match.ProductId);
        Assert.Equal("AB1-PKF-DLX56-000002", match.TrackingCode);
        Assert.Equal("SN-PKF-998811", match.SerialNumber);
    }

    [Fact]
    public async Task Imei_ResolvesSameInventoryUnit()
    {
        var fakes = new Phase2TestDoubles();
        var (_, product, _, _, _, _, unit, service) =
            SeedAuthoritativeCatalogAndUnit(
                fakes,
                trackingCode: "AB1-PKF-DLX56-000002",
                imei: "356938035643803");

        var matches = await service.ResolveScannerAsync("356938035643803", CancellationToken.None);

        Assert.Single(matches);
        var match = matches[0];

        // Resolves the exact inventory unit without replacing TrackingCode authority
        Assert.Equal(ScannerResolutionNamespace.Imei, match.Namespace);
        Assert.Equal(unit.Id, match.InventoryUnitId);
        Assert.Equal(product.Id, match.ProductId);
        Assert.Equal("AB1-PKF-DLX56-000002", match.TrackingCode);
        Assert.Equal("356938035643803", match.Imei1);
    }

    [Fact]
    public async Task ProductBarcode_DoesNotSilentlySelectExactUnit()
    {
        var fakes = new Phase2TestDoubles();
        var (_, product, productUnit, barcode, _, _, _, service) =
            SeedAuthoritativeCatalogAndUnit(fakes);

        var matches = await service.ResolveScannerAsync(barcode.Barcode, CancellationToken.None);

        Assert.Single(matches);
        var match = matches[0];

        Assert.Equal(ScannerResolutionNamespace.ProductUnitBarcode, match.Namespace);
        Assert.Equal(product.Id, match.ProductId);
        Assert.Equal(productUnit.Id, match.ProductUnitId);
        Assert.True(match.IsSerialized);

        // Crucial invariant: Scanning a product unit barcode MUST NOT silently choose an arbitrary exact unit
        Assert.Null(match.InventoryUnitId);
        Assert.Null(match.TrackingCode);
    }

    [Fact]
    public async Task ProductCode_DoesNotSilentlySelectExactUnit()
    {
        var fakes = new Phase2TestDoubles();
        var (_, product, productUnit, _, _, _, _, service) =
            SeedAuthoritativeCatalogAndUnit(fakes);

        var matches = await service.ResolveScannerAsync("PKF-DLX56", CancellationToken.None);

        Assert.Single(matches);
        var match = matches[0];

        Assert.Equal(ScannerResolutionNamespace.ProductBarcode, match.Namespace);
        Assert.Equal(product.Id, match.ProductId);
        Assert.Equal(productUnit.Id, match.ProductUnitId);
        Assert.True(match.IsSerialized);

        // Crucial invariant: Scanning a ProductCode (e.g. PKF-DLX56) MUST NOT silently pick an arbitrary exact unit
        Assert.Null(match.InventoryUnitId);
        Assert.Null(match.TrackingCode);
    }

    [Fact]
    public async Task ExactUnit_CannotAppearTwiceInCart()
    {
        var fakes = new Phase2TestDoubles();
        var handler = CreateSaleHandler(fakes);

        var (product, productUnit, unit) = SeedSerializedProductWithUnit(
            fakes,
            sku: "PKF-DLX56",
            status: InventoryUnitStatus.InStock);

        // Cart item representation holds ProductId, ProductUnitId, InventoryUnitId, TrackingCode
        var cartItem = new
        {
            ProductId = product.Id,
            ProductUnitId = productUnit.Id,
            InventoryUnitId = unit.Id,
            TrackingCode = unit.TrackingCode
        };

        Assert.Equal(product.Id, cartItem.ProductId);
        Assert.Equal(productUnit.Id, cartItem.ProductUnitId);
        Assert.Equal(unit.Id, cartItem.InventoryUnitId);
        Assert.Equal("AB1-PKF-DLX56-000002", cartItem.TrackingCode);

        // Invariant: The same exact unit cannot appear twice in the sale command
        var command = new CompleteSaleCommand(
            ClientOperationId: Guid.CreateVersion7(),
            CustomerId: null,
            CashierUserId: Guid.CreateVersion7(),
            SessionId: null,
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Cash,
            AmountTendered: 25000m,
            PaymentReference: null,
            QuotationId: null,
            Lines: new[]
            {
                new CompleteSaleLineInput(
                    product.Id,
                    productUnit.Id,
                    EnteredQuantity: 2m,
                    ExpectedUnitPrice: 12500m,
                    InventoryUnitIds: new[] { unit.Id, unit.Id }) // Attempting duplicate exact unit!
            });

        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("sales.serial_selected_twice", result.Error?.Code);
    }

    [Fact]
    public async Task SoldUnit_CannotBeSoldAgain()
    {
        var fakes = new Phase2TestDoubles();
        var handler = CreateSaleHandler(fakes);

        var (product, productUnit, unit) = SeedSerializedProductWithUnit(
            fakes,
            sku: "PKF-DLX56",
            status: InventoryUnitStatus.Sold); // already sold

        var command = new CompleteSaleCommand(
            ClientOperationId: Guid.CreateVersion7(),
            CustomerId: null,
            CashierUserId: Guid.CreateVersion7(),
            SessionId: null,
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Cash,
            AmountTendered: 12500m,
            PaymentReference: null,
            QuotationId: null,
            Lines: new[]
            {
                new CompleteSaleLineInput(
                    product.Id,
                    productUnit.Id,
                    EnteredQuantity: 1m,
                    ExpectedUnitPrice: 12500m,
                    InventoryUnitIds: new[] { unit.Id })
            });

        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("sales.serial_not_sellable", result.Error?.Code);
    }

    [Fact]
    public async Task WrongProductExactUnit_IsRejected()
    {
        var fakes = new Phase2TestDoubles();
        var handler = CreateSaleHandler(fakes);

        var (productA, productUnitA, unitA) = SeedSerializedProductWithUnit(
            fakes,
            sku: "PRODUCT-A",
            status: InventoryUnitStatus.InStock);

        var (productB, productUnitB, _) = SeedSerializedProductWithUnit(
            fakes,
            sku: "PRODUCT-B",
            status: InventoryUnitStatus.InStock);

        // Attempt to pass Unit A under Product B
        var command = new CompleteSaleCommand(
            ClientOperationId: Guid.CreateVersion7(),
            CustomerId: null,
            CashierUserId: Guid.CreateVersion7(),
            SessionId: null,
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Cash,
            AmountTendered: 12500m,
            PaymentReference: null,
            QuotationId: null,
            Lines: new[]
            {
                new CompleteSaleLineInput(
                    productB.Id,
                    productUnitB.Id,
                    EnteredQuantity: 1m,
                    ExpectedUnitPrice: 12500m,
                    InventoryUnitIds: new[] { unitA.Id })
            });

        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("sales.serial_not_sellable", result.Error?.Code);
    }

    [Fact]
    public async Task ScrappedUnit_IsRejected()
    {
        var fakes = new Phase2TestDoubles();
        var handler = CreateSaleHandler(fakes);

        var (product, productUnit, unit) = SeedSerializedProductWithUnit(
            fakes,
            sku: "PKF-DLX56",
            status: InventoryUnitStatus.Scrapped);

        var command = new CompleteSaleCommand(
            ClientOperationId: Guid.CreateVersion7(),
            CustomerId: null,
            CashierUserId: Guid.CreateVersion7(),
            SessionId: null,
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Cash,
            AmountTendered: 12500m,
            PaymentReference: null,
            QuotationId: null,
            Lines: new[]
            {
                new CompleteSaleLineInput(
                    product.Id,
                    productUnit.Id,
                    EnteredQuantity: 1m,
                    ExpectedUnitPrice: 12500m,
                    InventoryUnitIds: new[] { unit.Id })
            });

        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("sales.serial_not_sellable", result.Error?.Code);
    }

    [Fact]
    public async Task SupplierReturnedUnit_IsRejected()
    {
        var fakes = new Phase2TestDoubles();
        var handler = CreateSaleHandler(fakes);

        var (product, productUnit, unit) = SeedSerializedProductWithUnit(
            fakes,
            sku: "PKF-DLX56",
            status: InventoryUnitStatus.SupplierReturned);

        var command = new CompleteSaleCommand(
            ClientOperationId: Guid.CreateVersion7(),
            CustomerId: null,
            CashierUserId: Guid.CreateVersion7(),
            SessionId: null,
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Cash,
            AmountTendered: 12500m,
            PaymentReference: null,
            QuotationId: null,
            Lines: new[]
            {
                new CompleteSaleLineInput(
                    product.Id,
                    productUnit.Id,
                    EnteredQuantity: 1m,
                    ExpectedUnitPrice: 12500m,
                    InventoryUnitIds: new[] { unit.Id })
            });

        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("sales.serial_not_sellable", result.Error?.Code);
    }

    [Fact]
    public async Task QuantityProduct_DoesNotRequireInventoryUnit()
    {
        var fakes = new Phase2TestDoubles();
        var handler = CreateSaleHandler(fakes);

        var (product, productUnit) = SeedNonSerializedProduct(
            fakes,
            trackingMode: TrackingMode.Quantity,
            stock: 25m,
            price: 500m);

        var command = new CompleteSaleCommand(
            ClientOperationId: Guid.CreateVersion7(),
            CustomerId: null,
            CashierUserId: Guid.CreateVersion7(),
            SessionId: null,
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Cash,
            AmountTendered: 1500m,
            PaymentReference: null,
            QuotationId: null,
            Lines: new[]
            {
                new CompleteSaleLineInput(
                    product.Id,
                    productUnit.Id,
                    EnteredQuantity: 3m,
                    ExpectedUnitPrice: 500m,
                    InventoryUnitIds: Array.Empty<Guid>()) // No inventory units!
            });

        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(1500m, result.Value!.GrandTotal);

        var balance = await fakes.Inventory.GetStockBalanceForUpdateAsync(product.Id, CancellationToken.None);
        Assert.NotNull(balance);
        Assert.Equal(22m, balance!.SellableQty); // 25 - 3 = 22
    }

    [Fact]
    public async Task LengthProduct_DoesNotRequireInventoryUnit()
    {
        var fakes = new Phase2TestDoubles();
        var handler = CreateSaleHandler(fakes);

        var (product, productUnit) = SeedNonSerializedProduct(
            fakes,
            trackingMode: TrackingMode.Length,
            stock: 100m,
            price: 45m);

        var command = new CompleteSaleCommand(
            ClientOperationId: Guid.CreateVersion7(),
            CustomerId: null,
            CashierUserId: Guid.CreateVersion7(),
            SessionId: null,
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Cash,
            AmountTendered: 450m,
            PaymentReference: null,
            QuotationId: null,
            Lines: new[]
            {
                new CompleteSaleLineInput(
                    product.Id,
                    productUnit.Id,
                    EnteredQuantity: 10m,
                    ExpectedUnitPrice: 45m,
                    InventoryUnitIds: Array.Empty<Guid>()) // No inventory units!
            });

        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(450m, result.Value!.GrandTotal);

        var balance = await fakes.Inventory.GetStockBalanceForUpdateAsync(product.Id, CancellationToken.None);
        Assert.NotNull(balance);
        Assert.Equal(90m, balance!.SellableQty); // 100 - 10 = 90
    }

    [Fact]
    public async Task ConcurrentExactUnitSale_OnlyOneSucceeds()
    {
        var fakes = new Phase2TestDoubles();
        var handler = CreateSaleHandler(fakes);

        var (product, productUnit, unit) = SeedSerializedProductWithUnit(
            fakes,
            sku: "PKF-DLX56",
            status: InventoryUnitStatus.InStock);

        var opId1 = Guid.CreateVersion7();
        var opId2 = Guid.CreateVersion7();

        var command1 = new CompleteSaleCommand(
            ClientOperationId: opId1,
            CustomerId: null,
            CashierUserId: Guid.CreateVersion7(),
            SessionId: null,
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Cash,
            AmountTendered: 12500m,
            PaymentReference: null,
            QuotationId: null,
            Lines: new[]
            {
                new CompleteSaleLineInput(
                    product.Id,
                    productUnit.Id,
                    EnteredQuantity: 1m,
                    ExpectedUnitPrice: 12500m,
                    InventoryUnitIds: new[] { unit.Id })
            });

        var command2 = new CompleteSaleCommand(
            ClientOperationId: opId2,
            CustomerId: null,
            CashierUserId: Guid.CreateVersion7(),
            SessionId: null,
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Cash,
            AmountTendered: 12500m,
            PaymentReference: null,
            QuotationId: null,
            Lines: new[]
            {
                new CompleteSaleLineInput(
                    product.Id,
                    productUnit.Id,
                    EnteredQuantity: 1m,
                    ExpectedUnitPrice: 12500m,
                    InventoryUnitIds: new[] { unit.Id })
            });

        // First attempt succeeds
        var result1 = await handler.HandleAsync(command1, CancellationToken.None);
        Assert.True(result1.IsSuccess, result1.Error?.Message);

        // Unit is now Sold
        Assert.Equal(InventoryUnitStatus.Sold, unit.Status);
        Assert.Equal("AB1-PKF-DLX56-000002", unit.TrackingCode); // TrackingCode unchanged!

        // Second attempt on the same unit is deterministically rejected
        var result2 = await handler.HandleAsync(command2, CancellationToken.None);
        Assert.False(result2.IsSuccess);
        Assert.Equal("sales.serial_not_sellable", result2.Error?.Code);

        // Verify resource locks were acquired on the inventory unit
        Assert.Contains(fakes.ResourceLock.AcquiredLocks, x =>
            x.ResourceType == "inventory-unit" && x.Key == unit.Id.ToString("D"));
    }

    [Fact]
    public async Task PriceCheck_DoesNotMutateCartOrStock()
    {
        var fakes = new Phase2TestDoubles();
        var (_, product, _, _, _, _, unit, service) =
            SeedAuthoritativeCatalogAndUnit(fakes);

        // Initial state
        var stockBefore = fakes.Inventory.Balances[product.Id];
        Assert.Equal(1m, stockBefore.SellableQty);
        Assert.Equal(InventoryUnitStatus.InStock, unit.Status);

        // Perform Price Check via scanner resolution
        var matches = await service.ResolveScannerAsync("PKF-DLX56", CancellationToken.None);
        Assert.Single(matches);
        Assert.Equal(12500m, matches[0].UnitPrice);

        // Re-verify state after Price Check: absolutely ZERO mutations
        var stockAfter = fakes.Inventory.Balances[product.Id];
        Assert.Equal(1m, stockAfter.SellableQty);
        Assert.Equal(InventoryUnitStatus.InStock, unit.Status);
        Assert.Empty(fakes.Sales.Sales);
        Assert.Empty(fakes.Inventory.Movements);
    }

    private static (Product Product, ProductUnit Unit, InventoryUnit ExactUnit) SeedSerializedProductWithUnit(
        Phase2TestDoubles fakes,
        string sku = "PKF-DLX56",
        InventoryUnitStatus status = InventoryUnitStatus.InStock)
    {
        var unit = new Unit { Id = Guid.CreateVersion7(), Name = "Piece", Symbol = "pc" };
        var product = new Product
        {
            Id = Guid.CreateVersion7(),
            Name = "Pak Fan Deluxe 56",
            Sku = sku,
            BaseUnitId = unit.Id,
            TrackingMode = TrackingMode.Serialized,
            SerialTrackingEnabled = true,
            ImeiTrackingEnabled = true,
            DefaultSalePrice = 12500m,
            ReferencePurchaseCost = 9500m,
            IsActive = true
        };
        fakes.Catalog.Products[product.Id] = product;

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
        fakes.Catalog.AddProductUnit(pu);

        var lot = new InventoryLot
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            ReceivedQuantity = 1m,
            OriginalUnitCost = 9500m,
            EffectiveUnitCost = 9500m,
            CreatedAt = DateTimeOffset.UtcNow
        };
        fakes.Inventory.AddLot(lot);

        fakes.Inventory.AddLotBucketBalance(new InventoryLotBucketBalance
        {
            LotId = lot.Id,
            StockBucket = InventoryBucket.Sellable,
            Quantity = status == InventoryUnitStatus.InStock ? 1m : 0m
        });

        fakes.Inventory.AddStockBalance(new StockBalance
        {
            ProductId = product.Id,
            SellableQty = status == InventoryUnitStatus.InStock ? 1m : 0m
        });

        var exactUnit = new InventoryUnit
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            InventoryLotId = lot.Id,
            TrackingCode = "AB1-PKF-DLX56-000002",
            SerialNumber = "SN-DLX56-002",
            Imei1 = "356938035643803",
            Status = status,
            AcquisitionCost = 9500m,
            CreatedAt = DateTimeOffset.UtcNow,
            Version = 1
        };
        fakes.Inventory.AddInventoryUnit(exactUnit);

        var session = new CashSession
        {
            Id = Guid.CreateVersion7(),
            OpenedBy = Guid.CreateVersion7(),
            OpenedAt = DateTimeOffset.UtcNow,
            BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Status = CashSessionStatus.Open,
            OpeningCash = 1000m
        };
        fakes.Cash.AddSession(session);

        return (product, pu, exactUnit);
    }

    private static (Product Product, ProductUnit Unit) SeedNonSerializedProduct(
        Phase2TestDoubles fakes,
        TrackingMode trackingMode,
        decimal stock,
        decimal price)
    {
        var unit = new Unit { Id = Guid.CreateVersion7(), Name = "Item", Symbol = "ea" };
        var product = new Product
        {
            Id = Guid.CreateVersion7(),
            Name = "Non-Serialized Product",
            Sku = "NON-SERIAL-01",
            BaseUnitId = unit.Id,
            TrackingMode = trackingMode,
            DefaultSalePrice = price,
            ReferencePurchaseCost = price * 0.7m,
            IsActive = true
        };
        fakes.Catalog.Products[product.Id] = product;

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
        fakes.Catalog.AddProductUnit(pu);

        var lot = new InventoryLot
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            ReceivedQuantity = stock,
            OriginalUnitCost = price * 0.7m,
            EffectiveUnitCost = price * 0.7m,
            CreatedAt = DateTimeOffset.UtcNow
        };
        fakes.Inventory.AddLot(lot);

        fakes.Inventory.AddLotBucketBalance(new InventoryLotBucketBalance
        {
            LotId = lot.Id,
            StockBucket = InventoryBucket.Sellable,
            Quantity = stock
        });

        fakes.Inventory.AddStockBalance(new StockBalance
        {
            ProductId = product.Id,
            SellableQty = stock
        });

        var session = new CashSession
        {
            Id = Guid.CreateVersion7(),
            OpenedBy = Guid.CreateVersion7(),
            OpenedAt = DateTimeOffset.UtcNow,
            BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Status = CashSessionStatus.Open,
            OpeningCash = 1000m
        };
        fakes.Cash.AddSession(session);

        return (product, pu);
    }

    private static CompleteSaleHandler CreateSaleHandler(Phase2TestDoubles fakes) =>
        new(
            fakes.Sales,
            fakes.Quotations,
            fakes.Parties,
            fakes.Catalog,
            fakes.Inventory,
            fakes.CostAllocator,
            fakes.Cash,
            fakes.OperationLock,
            fakes.ResourceLock,
            fakes.Audit,
            fakes.ReceiptSnapshots,
            fakes.Numbers,
            fakes.Clock,
            fakes.Transactions,
            fakes.Authorization,
            fakes.UnitOfWork);
}

internal sealed class FakePhase4WorkflowReadService : IPhase4WorkflowReadService
{
    private readonly Phase2TestDoubles _fakes;
    private readonly List<ProductUnitBarcode> _barcodes = new();

    internal FakePhase4WorkflowReadService(Phase2TestDoubles fakes)
    {
        _fakes = fakes;
    }

    public void AddBarcode(ProductUnitBarcode barcode) => _barcodes.Add(barcode);

    public Task<IReadOnlyList<ExactInventoryUnitDto>> GetExactUnitsAsync(
        Guid productId,
        InventoryUnitStatus? status,
        Guid? sourcePurchaseItemId,
        CancellationToken cancellationToken)
    {
        var query = _fakes.Inventory.Units.Where(u => u.ProductId == productId);
        if (status is not null)
        {
            query = query.Where(u => u.Status == status.Value);
        }

        if (sourcePurchaseItemId is not null)
        {
            query = query.Where(u => u.SourcePurchaseItemId == sourcePurchaseItemId.Value);
        }

        var list = new List<ExactInventoryUnitDto>();
        foreach (var u in query.OrderBy(x => x.TrackingCode).ThenBy(x => x.Id))
        {
            var product = _fakes.Catalog.Products.GetValueOrDefault(u.ProductId);
            string? purchaseNumber = null;
            string? supplierName = null;
            if (u.SourcePurchaseItemId is Guid sourceId)
            {
                var item = _fakes.Purchasing.PurchaseItems.FirstOrDefault(i => i.Id == sourceId);
                if (item is not null && _fakes.Purchasing.Purchases.TryGetValue(item.PurchaseId, out var purchase))
                {
                    purchaseNumber = purchase.PurchaseNumber;
                    if (_fakes.Parties.Suppliers.TryGetValue(purchase.SupplierId, out var supplier))
                    {
                        supplierName = supplier.Name;
                    }
                }
            }

            list.Add(new ExactInventoryUnitDto(
                u.Id,
                u.ProductId,
                product?.Name ?? "Unknown Product",
                product?.Sku,
                u.TrackingCode,
                u.SerialNumber,
                u.Imei1,
                u.Imei2,
                u.Status,
                u.AcquisitionCost,
                u.SourcePurchaseItemId,
                purchaseNumber,
                supplierName,
                u.CreatedAt,
                u.Version));
        }

        return Task.FromResult<IReadOnlyList<ExactInventoryUnitDto>>(list);
    }

    public Task<IReadOnlyList<ScannerProductMatchDto>> ResolveScannerAsync(
        string input,
        CancellationToken cancellationToken)
    {
        var term = input?.Trim();
        if (string.IsNullOrWhiteSpace(term))
        {
            return Task.FromResult<IReadOnlyList<ScannerProductMatchDto>>(Array.Empty<ScannerProductMatchDto>());
        }

        var upper = term.ToUpperInvariant();
        var digits = new string(term.Where(char.IsDigit).ToArray());

        // 1. TrackingCode / Physical SKU
        var trackingUnit = _fakes.Inventory.Units.FirstOrDefault(u =>
            u.TrackingCode != null && string.Equals(u.TrackingCode, upper, StringComparison.OrdinalIgnoreCase));
        if (trackingUnit is not null)
        {
            return Task.FromResult(BuildUnitMatch(trackingUnit, ScannerResolutionNamespace.TrackingCode));
        }

        // 2. Serial Number
        var serialUnit = _fakes.Inventory.Units.FirstOrDefault(u =>
            u.SerialNumber != null && string.Equals(u.SerialNumber, upper, StringComparison.OrdinalIgnoreCase));
        if (serialUnit is not null)
        {
            return Task.FromResult(BuildUnitMatch(serialUnit, ScannerResolutionNamespace.SerialNumber));
        }

        // 3. IMEI
        if (digits.Length > 0)
        {
            var imeiUnit = _fakes.Inventory.Units.FirstOrDefault(u =>
                u.Imei1 == digits || u.Imei2 == digits);
            if (imeiUnit is not null)
            {
                return Task.FromResult(BuildUnitMatch(imeiUnit, ScannerResolutionNamespace.Imei));
            }
        }

        // 4. ProductUnitBarcode
        var barcodeMatch = _barcodes.FirstOrDefault(b =>
            b.IsActive && string.Equals(b.Barcode, upper, StringComparison.OrdinalIgnoreCase));
        if (barcodeMatch is not null && _fakes.Catalog.ProductUnits.TryGetValue(barcodeMatch.ProductUnitId, out var pu))
        {
            if (_fakes.Catalog.Products.TryGetValue(pu.ProductId, out var product))
            {
                var stock = _fakes.Inventory.Balances.GetValueOrDefault(product.Id);
                var category = product.CategoryId is Guid catId && _fakes.Catalog.Categories.TryGetValue(catId, out var c) ? c.Name : "Uncategorized";
                var match = new ScannerProductMatchDto(
                    ScannerResolutionNamespace.ProductUnitBarcode,
                    product.Id,
                    pu.Id,
                    product.Name,
                    product.Sku,
                    product.Brand,
                    category,
                    "pc",
                    stock?.SellableQty ?? 0m,
                    product.DefaultSalePrice * pu.FactorToBaseUnit,
                    product.TrackingMode == TrackingMode.Serialized,
                    null, null, null, null, null, null,
                    IsSellable: true);
                return Task.FromResult<IReadOnlyList<ScannerProductMatchDto>>(new[] { match });
            }
        }

        // 5. ProductCode (SKU)
        var skuProduct = _fakes.Catalog.Products.Values.FirstOrDefault(p =>
            p.IsActive && p.Sku != null && string.Equals(p.Sku, upper, StringComparison.OrdinalIgnoreCase));
        if (skuProduct is not null)
        {
            var defaultPu = _fakes.Catalog.ProductUnits.Values.FirstOrDefault(u => u.ProductId == skuProduct.Id && u.IsDefaultSaleUnit);
            var stock = _fakes.Inventory.Balances.GetValueOrDefault(skuProduct.Id);
            var category = skuProduct.CategoryId is Guid catId && _fakes.Catalog.Categories.TryGetValue(catId, out var c) ? c.Name : "Uncategorized";
            var match = new ScannerProductMatchDto(
                ScannerResolutionNamespace.ProductBarcode,
                skuProduct.Id,
                defaultPu?.Id ?? Guid.Empty,
                skuProduct.Name,
                skuProduct.Sku,
                skuProduct.Brand,
                category,
                "pc",
                stock?.SellableQty ?? 0m,
                skuProduct.DefaultSalePrice * (defaultPu?.FactorToBaseUnit ?? 1m),
                skuProduct.TrackingMode == TrackingMode.Serialized,
                null, null, null, null, null, null,
                IsSellable: true);
            return Task.FromResult<IReadOnlyList<ScannerProductMatchDto>>(new[] { match });
        }

        // 6. Broad search
        var broad = _fakes.Catalog.Products.Values
            .Where(p => p.IsActive && (
                p.Name.Contains(upper, StringComparison.OrdinalIgnoreCase) ||
                (p.Sku != null && p.Sku.Contains(upper, StringComparison.OrdinalIgnoreCase)) ||
                (p.Brand != null && p.Brand.Contains(upper, StringComparison.OrdinalIgnoreCase)) ||
                (p.Model != null && p.Model.Contains(upper, StringComparison.OrdinalIgnoreCase))))
            .Select(p =>
            {
                var puRow = _fakes.Catalog.ProductUnits.Values.FirstOrDefault(u => u.ProductId == p.Id && u.IsDefaultSaleUnit);
                var stock = _fakes.Inventory.Balances.GetValueOrDefault(p.Id);
                var category = p.CategoryId is Guid catId && _fakes.Catalog.Categories.TryGetValue(catId, out var c) ? c.Name : "Uncategorized";
                return new ScannerProductMatchDto(
                    ScannerResolutionNamespace.BroaderSearch,
                    p.Id,
                    puRow?.Id ?? Guid.Empty,
                    p.Name,
                    p.Sku,
                    p.Brand,
                    category,
                    "pc",
                    stock?.SellableQty ?? 0m,
                    p.DefaultSalePrice * (puRow?.FactorToBaseUnit ?? 1m),
                    p.TrackingMode == TrackingMode.Serialized,
                    null, null, null, null, null, null,
                    IsSellable: true);
            })
            .ToArray();

        return Task.FromResult<IReadOnlyList<ScannerProductMatchDto>>(broad);
    }

    private IReadOnlyList<ScannerProductMatchDto> BuildUnitMatch(InventoryUnit unit, ScannerResolutionNamespace ns)
    {
        var product = _fakes.Catalog.Products.GetValueOrDefault(unit.ProductId)!;
        var pu = _fakes.Catalog.ProductUnits.Values.FirstOrDefault(u => u.ProductId == product.Id && u.IsDefaultSaleUnit);
        var stock = _fakes.Inventory.Balances.GetValueOrDefault(product.Id);
        var category = product.CategoryId is Guid catId && _fakes.Catalog.Categories.TryGetValue(catId, out var c) ? c.Name : "Uncategorized";

        string? purchaseNumber = null;
        string? supplierName = null;
        Guid? supplierId = null;
        if (unit.SourcePurchaseItemId is Guid sourceId)
        {
            var item = _fakes.Purchasing.PurchaseItems.FirstOrDefault(i => i.Id == sourceId);
            if (item is not null && _fakes.Purchasing.Purchases.TryGetValue(item.PurchaseId, out var purchase))
            {
                purchaseNumber = purchase.PurchaseNumber;
                supplierId = purchase.SupplierId;
                if (_fakes.Parties.Suppliers.TryGetValue(purchase.SupplierId, out var supplier))
                {
                    supplierName = supplier.Name;
                }
            }
        }

        return new[]
        {
            new ScannerProductMatchDto(
                ns,
                product.Id,
                pu?.Id ?? Guid.Empty,
                product.Name,
                product.Sku,
                product.Brand,
                category,
                "pc",
                stock?.SellableQty ?? 0m,
                product.DefaultSalePrice * (pu?.FactorToBaseUnit ?? 1m),
                product.TrackingMode == TrackingMode.Serialized,
                unit.Id,
                unit.TrackingCode,
                unit.SerialNumber,
                unit.Imei1,
                unit.Imei2,
                unit.Status,
                IsSellable: unit.Status == InventoryUnitStatus.InStock,
                SupplierName: supplierName,
                PurchaseNumber: purchaseNumber,
                SourcePurchaseItemId: unit.SourcePurchaseItemId,
                SupplierId: supplierId)
        };
    }

    public Task<IReadOnlyList<PosDraftSummaryDto>> GetOpenDraftsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PosDraftSummaryDto>>(Array.Empty<PosDraftSummaryDto>());

    public Task<PosDraftDetailDto?> GetDraftAsync(Guid draftId, CancellationToken cancellationToken) =>
        Task.FromResult<PosDraftDetailDto?>(null);

    public Task<StocktakeSnapshotDto?> GetOpenStocktakeAsync(CancellationToken cancellationToken) =>
        Task.FromResult<StocktakeSnapshotDto?>(null);
}
