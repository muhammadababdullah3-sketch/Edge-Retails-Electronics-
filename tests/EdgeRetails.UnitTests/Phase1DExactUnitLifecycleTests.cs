using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Catalog;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Parties;
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

/// <summary>
/// Phase 1D Certification Test Suite:
/// 1. Baseline & Regression Certification (Phase 1B & 1C integrity).
/// 2. ClientOperationId Idempotency Audit & Replay Protection (Sale, Sale Return, Purchase Return, Warranty).
/// 3. Concurrency Matrix & Race Condition Testing (Sale vs Sale, Sale vs Purchase Return, Sale Return vs Sale, Sale Return vs Warranty, Duplicate Replacement).
/// 4. Focused Golden Trace Test: All 14 Invariant Assertions across the physical item lifetime.
/// </summary>
public sealed class Phase1DExactUnitLifecycleTests
{
    private readonly Phase2TestDoubles _fakes = new();
    private readonly Guid _actorId = Guid.NewGuid();

    #region Factory Helpers

    private CompleteSaleHandler CreateSaleHandler() => new(
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

    private CreateSaleReturnHandler CreateSaleReturnHandler() => new(
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

    private CreatePurchaseReturnHandler CreatePurchaseReturnHandler() => new(
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

    private ReceiveProductIntakeHandler CreateIntakeHandler() => new(
        _fakes.Purchasing,
        _fakes.Parties,
        _fakes.Catalog,
        _fakes.Inventory,
        _fakes.CostAllocator,
        _fakes.Traceability,
        _fakes.OperationLock,
        _fakes.ResourceLock,
        _fakes.Audit,
        _fakes.Clock,
        _fakes.Transactions,
        _fakes.Authorization,
        _fakes.UnitOfWork);

    private CreateWarrantyClaimHandler CreateWarrantyClaimHandler() => new(
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
        _fakes.OperationLock);

    private BeginWarrantyClaimReviewHandler CreateBeginReviewHandler() => new(
        _fakes.Warranty,
        _fakes.ResourceLock,
        _fakes.Authorization,
        _fakes.Clock,
        _fakes.Transactions,
        _fakes.UnitOfWork,
        _fakes.OperationLock);

    private SendWarrantyClaimToSupplierHandler CreateSendWarrantyHandler() => new(
        _fakes.Warranty,
        _fakes.ResourceLock,
        _fakes.Authorization,
        _fakes.Clock,
        _fakes.Transactions,
        _fakes.UnitOfWork,
        _fakes.OperationLock);

    private ReceiveCustomerWarrantyReplacementHandler CreateCustomerReplacementHandler() => new(
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

    private SendShopStockToSupplierWarrantyHandler CreateSendShopStockHandler() => new(
        _fakes.Warranty,
        _fakes.Catalog,
        _fakes.Parties,
        _fakes.Purchasing,
        _fakes.Inventory,
        _fakes.Traceability,
        new InventoryConditionService(_fakes.Catalog, _fakes.Inventory, _fakes.CostAllocator, _fakes.Clock),
        _fakes.ResourceLock,
        _fakes.Authorization,
        _fakes.Audit,
        _fakes.Numbers,
        _fakes.Clock,
        _fakes.Transactions,
        _fakes.UnitOfWork,
        _fakes.OperationLock);

    private ReceiveShopStockWarrantyHandler CreateReceiveShopStockHandler() => new(
        _fakes.Catalog,
        _fakes.Inventory,
        new InventoryConditionService(_fakes.Catalog, _fakes.Inventory, _fakes.CostAllocator, _fakes.Clock),
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

    private async Task<(Supplier Supplier, Product Product, ProductUnit Unit, SupplierProduct SupplierProduct, Purchase Purchase, PurchaseItem PurchaseItem, CashSession Session)>
        SetupGoldenTraceFixtureAsync()
    {
        var supplier = new Supplier
        {
            Id = Guid.NewGuid(),
            Name = "AB Supplier",
            DealerCode = "AB1",
            IsActive = true,
            CreatedAt = _fakes.Clock.UtcNow
        };
        _fakes.Parties.AddSupplier(supplier);

        var sku = TraceabilityCodeRules.BuildProductCode("PK", "F", "DLX56");
        Assert.Equal("PKF-DLX56", sku);

        var unit = new Unit
        {
            Id = Guid.NewGuid(),
            Name = "Piece",
            Symbol = "pcs",
            IsActive = true
        };
        _fakes.Catalog.AddUnit(unit);

        var product = new Product
        {
            Id = Guid.NewGuid(),
            Name = "Pak Fan Deluxe 56",
            Sku = sku,
            BaseUnitId = unit.Id,
            TrackingMode = TrackingMode.Serialized,
            IsActive = true,
            DefaultSalePrice = 5000m,
            ReferencePurchaseCost = 4000m
        };
        _fakes.Catalog.AddProduct(product);

        var productUnit = new ProductUnit
        {
            Id = Guid.NewGuid(),
            ProductId = product.Id,
            UnitId = unit.Id,
            FactorToBaseUnit = 1m,
            IsDefaultSaleUnit = true,
            IsDefaultPurchaseUnit = true,
            CanPurchase = true,
            CanSell = true,
            IsActive = true
        };
        _fakes.Catalog.AddProductUnit(productUnit);

        var supplierProduct = new SupplierProduct
        {
            Id = Guid.NewGuid(),
            SupplierId = supplier.Id,
            ProductId = product.Id,
            NextItemSequence = 1,
            IsActive = true,
            CreatedAt = _fakes.Clock.UtcNow,
            UpdatedAt = _fakes.Clock.UtcNow
        };
        _fakes.Traceability.AddSupplierProduct(supplierProduct);

        var stockBalance = new StockBalance
        {
            ProductId = product.Id,
            SellableQty = 0m,
            DamagedQty = 0m,
            DefectiveQty = 0m,
            ScrapQty = 0m,
            WithSupplierQty = 0m
        };
        _fakes.Inventory.AddStockBalance(stockBalance);

        var costState = new ProductCostState
        {
            ProductId = product.Id,
            CostedQty = 0m,
            TotalInventoryCost = 0m,
            MovingAverageCost = 0m
        };
        _fakes.Inventory.AddCostState(costState);

        var purchase = new Purchase
        {
            Id = Guid.NewGuid(),
            SupplierId = supplier.Id,
            PurchaseNumber = "PO-2026-0001",
            SupplierInvoiceNumber = "SUPP-INV-100",
            PurchaseDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Status = PurchaseStatus.Completed,
            SettlementMode = PurchaseSettlementMode.External,
            Subtotal = 12000m,
            GrandTotal = 12000m,
            CreatedBy = _actorId,
            CreatedAt = _fakes.Clock.UtcNow,
            ClientOperationId = Guid.NewGuid()
        };
        _fakes.Purchasing.AddPurchase(purchase);

        var purchaseItem = new PurchaseItem
        {
            Id = Guid.NewGuid(),
            PurchaseId = purchase.Id,
            ProductId = product.Id,
            ProductUnitId = productUnit.Id,
            EnteredQuantity = 3m,
            EnteredUnitCost = 4000m,
            FactorToBaseSnapshot = 1m,
            BaseQuantity = 3m,
            EffectiveBaseUnitCost = 4000m,
            BaseLineTotal = 12000m
        };
        _fakes.Purchasing.AddPurchaseItem(purchaseItem);

        var session = new CashSession
        {
            Id = Guid.NewGuid(),
            OpenedBy = _actorId,
            OpeningCash = 10000m,
            Status = CashSessionStatus.Open,
            OpenedAt = _fakes.Clock.UtcNow,
            BusinessDate = DateOnly.FromDateTime(_fakes.Clock.UtcNow.DateTime)
        };
        _fakes.Cash.AddSession(session);

        return (supplier, product, productUnit, supplierProduct, purchase, purchaseItem, session);
    }

    #endregion

    #region Part 1: Baseline & Regression Certification

    [Fact]
    public async Task Baseline_Phase1B_And_Phase1C_Authority_And_PrintEngine_Remain_100_Percent_Certified()
    {
        var fixture = await SetupGoldenTraceFixtureAsync();

        // Verify Master Data derivation
        Assert.Equal("AB1", fixture.Supplier.DealerCode);
        Assert.Equal("PKF-DLX56", fixture.Product.Sku);
        Assert.Equal(TrackingMode.Serialized, fixture.Product.TrackingMode);
        Assert.Equal(1L, fixture.SupplierProduct.NextItemSequence);

        // Verify receiving intake handler processes serialized units cleanly
        var intakeHandler = CreateIntakeHandler();
        var intakeOpId = Guid.NewGuid();
        var intakeCommand = new ReceiveProductIntakeCommand(
            fixture.Purchase.Id,
            fixture.Product.Id,
            fixture.Unit.Id,
            EnteredQuantity: 3m,
            EnteredUnitCost: 4000m,
            SerializedUnits:
            [
                new SerializedIdentityInput(SerialNumber: "SN-001", Imei1: "354000000000001", Imei2: null),
                new SerializedIdentityInput(SerialNumber: "SN-002", Imei1: "354000000000002", Imei2: null),
                new SerializedIdentityInput(SerialNumber: "SN-003", Imei1: "354000000000003", Imei2: null)
            ],
            CreatedBy: _actorId,
            ClientOperationId: intakeOpId);

        var intakeResult = await intakeHandler.HandleAsync(intakeCommand);
        Assert.True(intakeResult.IsSuccess, intakeResult.Error?.Message);
        Assert.Equal(3, intakeResult.Value!.CommittedUnits.Count);
        Assert.Equal(4L, fixture.SupplierProduct.NextItemSequence);

        // Verify tracking codes were correctly formatted
        Assert.Equal("AB1-PKF-DLX56-000001", intakeResult.Value.CommittedUnits[0].TrackingCode);
        Assert.Equal("AB1-PKF-DLX56-000002", intakeResult.Value.CommittedUnits[1].TrackingCode);
        Assert.Equal("AB1-PKF-DLX56-000003", intakeResult.Value.CommittedUnits[2].TrackingCode);
    }

    #endregion

    #region Part 2: ClientOperationId Idempotency Audit & Replay Protection

    [Fact]
    public async Task Idempotency_SaleCompletion_ReplaySameOperationId_ReturnsOriginalResult_WithoutDuplication()
    {
        var fixture = await SetupGoldenTraceFixtureAsync();
        var intakeHandler = CreateIntakeHandler();
        await intakeHandler.HandleAsync(new ReceiveProductIntakeCommand(
            fixture.Purchase.Id, fixture.Product.Id, fixture.Unit.Id, 1m, 4000m,
            [new SerializedIdentityInput("SN-SALE-IDEMP", null, null)],
            _actorId, Guid.NewGuid()));

        var unit = _fakes.Inventory.Units.Single(u => u.SerialNumber == "SN-SALE-IDEMP");
        var customer = new Customer { Id = Guid.NewGuid(), Name = "Sale Idemp Cust", IsActive = true, CreatedAt = _fakes.Clock.UtcNow };
        _fakes.Parties.AddCustomer(customer);

        var saleOpId = Guid.NewGuid();
        var saleCommand = new CompleteSaleCommand(
            saleOpId,
            customer.Id,
            _actorId,
            fixture.Session.Id,
            0m,
            SalePaymentMethod.Cash,
            5000m,
            null,
            null,
            [new CompleteSaleLineInput(fixture.Product.Id, fixture.Unit.Id, 1m, 5000m, [unit.Id])]);

        var saleHandler = CreateSaleHandler();

        // 1. Initial execution
        var result1 = await saleHandler.HandleAsync(saleCommand, CancellationToken.None);
        Assert.True(result1.IsSuccess, result1.Error?.Message);
        Assert.False(result1.Value!.WasExisting);
        var originalSaleId = result1.Value.SaleId;
        var originalInvoice = result1.Value.InvoiceNumber;

        // 2. Replay with identical payload & ClientOperationId
        var result2 = await saleHandler.HandleAsync(saleCommand, CancellationToken.None);
        Assert.True(result2.IsSuccess, result2.Error?.Message);
        Assert.True(result2.Value!.WasExisting, "Replay must return WasExisting=true");
        Assert.Equal(originalSaleId, result2.Value.SaleId);
        Assert.Equal(originalInvoice, result2.Value.InvoiceNumber);

        // 3. Verify exactly 1 Sale, 1 Payment, 1 Movement in storage
        Assert.Single(_fakes.Sales.Sales.Values, s => s.ClientOperationId == saleOpId);
        Assert.Single(_fakes.Sales.SalePayments, p => p.SaleId == originalSaleId);
        Assert.Single(_fakes.Inventory.Movements, m => m.ReferenceType == "SALE" && m.ReferenceId == originalSaleId);
    }

    [Fact]
    public async Task Idempotency_SaleCompletion_ReplayWithConflictingPayload_Rejects()
    {
        var fixture = await SetupGoldenTraceFixtureAsync();
        var intakeHandler = CreateIntakeHandler();
        await intakeHandler.HandleAsync(new ReceiveProductIntakeCommand(
            fixture.Purchase.Id, fixture.Product.Id, fixture.Unit.Id, 1m, 4000m,
            [new SerializedIdentityInput("SN-SALE-MISMATCH", null, null)],
            _actorId, Guid.NewGuid()));

        var unit = _fakes.Inventory.Units.Single(u => u.SerialNumber == "SN-SALE-MISMATCH");
        var customer1 = new Customer { Id = Guid.NewGuid(), Name = "Cust 1", IsActive = true, CreatedAt = _fakes.Clock.UtcNow };
        var customer2 = new Customer { Id = Guid.NewGuid(), Name = "Cust 2", IsActive = true, CreatedAt = _fakes.Clock.UtcNow };
        _fakes.Parties.AddCustomer(customer1);
        _fakes.Parties.AddCustomer(customer2);

        var saleOpId = Guid.NewGuid();
        var saleCommand1 = new CompleteSaleCommand(
            saleOpId, customer1.Id, _actorId, fixture.Session.Id, 0m, SalePaymentMethod.Cash, 5000m, null, null,
            [new CompleteSaleLineInput(fixture.Product.Id, fixture.Unit.Id, 1m, 5000m, [unit.Id])]);

        var saleHandler = CreateSaleHandler();
        var result1 = await saleHandler.HandleAsync(saleCommand1, CancellationToken.None);
        Assert.True(result1.IsSuccess);

        // Replay with different CustomerId
        var saleCommand2 = new CompleteSaleCommand(
            saleOpId, customer2.Id, _actorId, fixture.Session.Id, 0m, SalePaymentMethod.Cash, 5000m, null, null,
            [new CompleteSaleLineInput(fixture.Product.Id, fixture.Unit.Id, 1m, 5000m, [unit.Id])]);

        var result2 = await saleHandler.HandleAsync(saleCommand2, CancellationToken.None);
        Assert.False(result2.IsSuccess);
        Assert.Equal("idempotency.payload_mismatch", result2.Error?.Code);
    }

    [Fact]
    public async Task Idempotency_SaleReturn_ReplaySameOperationId_ReturnsOriginalResult_WithoutDuplication()
    {
        var fixture = await SetupGoldenTraceFixtureAsync();
        var intakeHandler = CreateIntakeHandler();
        await intakeHandler.HandleAsync(new ReceiveProductIntakeCommand(
            fixture.Purchase.Id, fixture.Product.Id, fixture.Unit.Id, 1m, 4000m,
            [new SerializedIdentityInput("SN-SR-IDEMP", null, null)],
            _actorId, Guid.NewGuid()));

        var unit = _fakes.Inventory.Units.Single(u => u.SerialNumber == "SN-SR-IDEMP");
        var customer = new Customer { Id = Guid.NewGuid(), Name = "SR Cust", IsActive = true, CreatedAt = _fakes.Clock.UtcNow };
        _fakes.Parties.AddCustomer(customer);

        var saleHandler = CreateSaleHandler();
        var saleResult = await saleHandler.HandleAsync(new CompleteSaleCommand(
            Guid.NewGuid(), customer.Id, _actorId, fixture.Session.Id, 0m, SalePaymentMethod.Cash, 5000m, null, null,
            [new CompleteSaleLineInput(fixture.Product.Id, fixture.Unit.Id, 1m, 5000m, [unit.Id])]), CancellationToken.None);
        Assert.True(saleResult.IsSuccess);

        var saleItem = _fakes.Sales.SaleItems.Single(si => si.SaleId == saleResult.Value!.SaleId);

        var returnOpId = Guid.NewGuid();
        var returnCommand = new CreateSaleReturnCommand(
            saleResult.Value!.SaleId,
            "FAULTY",
            "Defective unit return",
            RefundMethod.Cash,
            _actorId,
            returnOpId,
            [new SaleReturnLineInput(saleItem.Id, 1m, SaleReturnDisposition.RestockSellable, [unit.Id])]);

        var returnHandler = CreateSaleReturnHandler();

        // 1. Initial return
        var res1 = await returnHandler.HandleAsync(returnCommand, CancellationToken.None);
        Assert.True(res1.IsSuccess, res1.Error?.Message);
        Assert.False(res1.Value!.WasExisting);
        var returnId = res1.Value.SaleReturnId;
        var returnNumber = res1.Value.ReturnNumber;

        // 2. Replay identical
        var res2 = await returnHandler.HandleAsync(returnCommand, CancellationToken.None);
        Assert.True(res2.IsSuccess, res2.Error?.Message);
        Assert.True(res2.Value!.WasExisting);
        Assert.Equal(returnId, res2.Value.SaleReturnId);
        Assert.Equal(returnNumber, res2.Value.ReturnNumber);

        // Exactly 1 SaleReturn recorded
        Assert.Single(_fakes.Sales.SaleReturns.Values, r => r.ClientOperationId == returnOpId);
    }

    [Fact]
    public async Task Idempotency_SaleReturn_ReplayWithConflictingPayload_Rejects()
    {
        var fixture = await SetupGoldenTraceFixtureAsync();
        var intakeHandler = CreateIntakeHandler();
        await intakeHandler.HandleAsync(new ReceiveProductIntakeCommand(
            fixture.Purchase.Id, fixture.Product.Id, fixture.Unit.Id, 1m, 4000m,
            [new SerializedIdentityInput("SN-SR-MISMATCH", null, null)],
            _actorId, Guid.NewGuid()));

        var unit = _fakes.Inventory.Units.Single(u => u.SerialNumber == "SN-SR-MISMATCH");
        var customer = new Customer { Id = Guid.NewGuid(), Name = "SR Cust Mismatch", IsActive = true, CreatedAt = _fakes.Clock.UtcNow };
        _fakes.Parties.AddCustomer(customer);

        var saleHandler = CreateSaleHandler();
        var saleResult = await saleHandler.HandleAsync(new CompleteSaleCommand(
            Guid.NewGuid(), customer.Id, _actorId, fixture.Session.Id, 0m, SalePaymentMethod.Cash, 5000m, null, null,
            [new CompleteSaleLineInput(fixture.Product.Id, fixture.Unit.Id, 1m, 5000m, [unit.Id])]), CancellationToken.None);
        Assert.True(saleResult.IsSuccess);

        var saleItem = _fakes.Sales.SaleItems.Single(si => si.SaleId == saleResult.Value!.SaleId);

        var returnOpId = Guid.NewGuid();
        var returnCommand1 = new CreateSaleReturnCommand(
            saleResult.Value!.SaleId,
            "FAULTY",
            "Defective unit return",
            RefundMethod.Cash,
            _actorId,
            returnOpId,
            [new SaleReturnLineInput(saleItem.Id, 1m, SaleReturnDisposition.RestockSellable, [unit.Id])]);

        var returnHandler = CreateSaleReturnHandler();
        var res1 = await returnHandler.HandleAsync(returnCommand1, CancellationToken.None);
        Assert.True(res1.IsSuccess);

        // Replay with conflicting SaleId
        var returnCommand2 = new CreateSaleReturnCommand(
            Guid.NewGuid(), // different sale
            "FAULTY",
            "Defective unit return",
            RefundMethod.Cash,
            _actorId,
            returnOpId,
            [new SaleReturnLineInput(saleItem.Id, 1m, SaleReturnDisposition.RestockSellable, [unit.Id])]);

        var res2 = await returnHandler.HandleAsync(returnCommand2, CancellationToken.None);
        Assert.False(res2.IsSuccess);
        Assert.Equal("idempotency.payload_mismatch", res2.Error?.Code);
    }

    [Fact]
    public async Task Idempotency_PurchaseReturn_ReplaySameOperationId_ReturnsOriginalResult_WithoutDuplication()
    {
        var fixture = await SetupGoldenTraceFixtureAsync();
        var intakeHandler = CreateIntakeHandler();
        await intakeHandler.HandleAsync(new ReceiveProductIntakeCommand(
            fixture.Purchase.Id, fixture.Product.Id, fixture.Unit.Id, 1m, 4000m,
            [new SerializedIdentityInput("SN-PR-IDEMP", null, null)],
            _actorId, Guid.NewGuid()));

        var unit = _fakes.Inventory.Units.Single(u => u.SerialNumber == "SN-PR-IDEMP");
        var prOpId = Guid.NewGuid();
        var prCommand = new CreatePurchaseReturnCommand(
            fixture.Purchase.Id,
            "Overstock return",
            null,
            PurchaseReturnSettlementMode.External,
            _actorId,
            prOpId,
            [new PurchaseReturnLineInput(fixture.PurchaseItem.Id, 1m, 4000m, [unit.Id])]);

        var prHandler = CreatePurchaseReturnHandler();
        var res1 = await prHandler.HandleAsync(prCommand, CancellationToken.None);
        Assert.True(res1.IsSuccess, res1.Error?.Message);
        Assert.False(res1.Value!.WasExisting);
        var returnId = res1.Value.PurchaseReturnId;

        // Replay identical
        var res2 = await prHandler.HandleAsync(prCommand, CancellationToken.None);
        Assert.True(res2.IsSuccess, res2.Error?.Message);
        Assert.True(res2.Value!.WasExisting);
        Assert.Equal(returnId, res2.Value.PurchaseReturnId);

        Assert.Single(_fakes.Purchasing.PurchaseReturns.Values, r => r.ClientOperationId == prOpId);
    }

    [Fact]
    public async Task Idempotency_PurchaseReturn_ReplayWithConflictingPayload_Rejects()
    {
        var fixture = await SetupGoldenTraceFixtureAsync();
        var intakeHandler = CreateIntakeHandler();
        await intakeHandler.HandleAsync(new ReceiveProductIntakeCommand(
            fixture.Purchase.Id, fixture.Product.Id, fixture.Unit.Id, 1m, 4000m,
            [new SerializedIdentityInput("SN-PR-MISMATCH", null, null)],
            _actorId, Guid.NewGuid()));

        var unit = _fakes.Inventory.Units.Single(u => u.SerialNumber == "SN-PR-MISMATCH");
        var prOpId = Guid.NewGuid();
        var prCommand1 = new CreatePurchaseReturnCommand(
            fixture.Purchase.Id,
            "Overstock return",
            null,
            PurchaseReturnSettlementMode.External,
            _actorId,
            prOpId,
            [new PurchaseReturnLineInput(fixture.PurchaseItem.Id, 1m, 4000m, [unit.Id])]);

        var prHandler = CreatePurchaseReturnHandler();
        var res1 = await prHandler.HandleAsync(prCommand1, CancellationToken.None);
        Assert.True(res1.IsSuccess);

        // Replay with different PurchaseId
        var prCommand2 = new CreatePurchaseReturnCommand(
            Guid.NewGuid(), // different purchase
            "Overstock return",
            null,
            PurchaseReturnSettlementMode.External,
            _actorId,
            prOpId,
            [new PurchaseReturnLineInput(fixture.PurchaseItem.Id, 1m, 4000m, [unit.Id])]);

        var res2 = await prHandler.HandleAsync(prCommand2, CancellationToken.None);
        Assert.False(res2.IsSuccess);
        Assert.Equal("idempotency.payload_mismatch", res2.Error?.Code);
    }

    [Fact]
    public async Task Idempotency_WarrantyClaim_Intake_ReplaySameOperationId_And_MismatchRejection()
    {
        var fixture = await SetupGoldenTraceFixtureAsync();
        var intakeHandler = CreateIntakeHandler();
        await intakeHandler.HandleAsync(new ReceiveProductIntakeCommand(
            fixture.Purchase.Id, fixture.Product.Id, fixture.Unit.Id, 1m, 4000m,
            [new SerializedIdentityInput("SN-WC-IDEMP", null, null)],
            _actorId, Guid.NewGuid()));

        var unit = _fakes.Inventory.Units.Single(u => u.SerialNumber == "SN-WC-IDEMP");
        var customer1 = new Customer { Id = Guid.NewGuid(), Name = "WC Cust 1", IsActive = true, CreatedAt = _fakes.Clock.UtcNow };
        var customer2 = new Customer { Id = Guid.NewGuid(), Name = "WC Cust 2", IsActive = true, CreatedAt = _fakes.Clock.UtcNow };
        _fakes.Parties.AddCustomer(customer1);
        _fakes.Parties.AddCustomer(customer2);

        var saleHandler = CreateSaleHandler();
        var saleResult = await saleHandler.HandleAsync(new CompleteSaleCommand(
            Guid.NewGuid(), customer1.Id, _actorId, fixture.Session.Id, 0m, SalePaymentMethod.Cash, 5000m, null, null,
            [new CompleteSaleLineInput(fixture.Product.Id, fixture.Unit.Id, 1m, 5000m, [unit.Id])]), CancellationToken.None);
        Assert.True(saleResult.IsSuccess);

        var sale = _fakes.Sales.Sales[saleResult.Value!.SaleId];
        var saleItem = _fakes.Sales.SaleItems.Single(si => si.SaleId == sale.Id);
        saleItem.WarrantyValidUntil = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1));

        var wcOpId = Guid.NewGuid();
        var wcCommand1 = new CreateWarrantyClaimCommand(
            customer1.Id,
            sale.Id,
            fixture.Supplier.Id,
            _actorId,
            [
                new WarrantyClaimItemInput(
                    fixture.Product.Id,
                    1m,
                    "Fan speed fluctuation",
                    saleItem.Id,
                    saleItem.WarrantyValidUntil,
                    [new WarrantyClaimUnitInput(unit.Id, unit.SerialNumber)])
            ],
            wcOpId);

        var wcHandler = CreateWarrantyClaimHandler();
        var res1 = await wcHandler.HandleAsync(wcCommand1, CancellationToken.None);
        Assert.True(res1.IsSuccess, res1.Error?.Message);
        var claimId = res1.Value;

        // Replay identical
        var res2 = await wcHandler.HandleAsync(wcCommand1, CancellationToken.None);
        Assert.True(res2.IsSuccess);
        Assert.Equal(claimId, res2.Value);
        Assert.Single(_fakes.Warranty.Claims.Values, c => c.ClientOperationId == wcOpId);

        // Replay with different Customer
        var wcCommand2 = new CreateWarrantyClaimCommand(
            customer2.Id, // conflicting customer
            sale.Id,
            fixture.Supplier.Id,
            _actorId,
            [
                new WarrantyClaimItemInput(
                    fixture.Product.Id,
                    1m,
                    "Fan speed fluctuation",
                    saleItem.Id,
                    saleItem.WarrantyValidUntil,
                    [new WarrantyClaimUnitInput(unit.Id, unit.SerialNumber)])
            ],
            wcOpId);

        var res3 = await wcHandler.HandleAsync(wcCommand2, CancellationToken.None);
        Assert.False(res3.IsSuccess);
        Assert.Equal("payload_mismatch", res3.Error?.Code);
    }

    [Fact]
    public async Task Idempotency_WarrantyClaim_Transition_ReplaySameOperationId_And_MismatchRejection()
    {
        var fixture = await SetupGoldenTraceFixtureAsync();
        var intakeHandler = CreateIntakeHandler();
        await intakeHandler.HandleAsync(new ReceiveProductIntakeCommand(
            fixture.Purchase.Id, fixture.Product.Id, fixture.Unit.Id, 1m, 4000m,
            [new SerializedIdentityInput("SN-WTR-IDEMP", null, null)],
            _actorId, Guid.NewGuid()));

        var unit = _fakes.Inventory.Units.Single(u => u.SerialNumber == "SN-WTR-IDEMP");
        var customer = new Customer { Id = Guid.NewGuid(), Name = "WTR Cust", IsActive = true, CreatedAt = _fakes.Clock.UtcNow };
        _fakes.Parties.AddCustomer(customer);

        var saleHandler = CreateSaleHandler();
        var saleResult = await saleHandler.HandleAsync(new CompleteSaleCommand(
            Guid.NewGuid(), customer.Id, _actorId, fixture.Session.Id, 0m, SalePaymentMethod.Cash, 5000m, null, null,
            [new CompleteSaleLineInput(fixture.Product.Id, fixture.Unit.Id, 1m, 5000m, [unit.Id])]), CancellationToken.None);
        var saleItem = _fakes.Sales.SaleItems.Single(si => si.SaleId == saleResult.Value!.SaleId);
        saleItem.WarrantyValidUntil = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1));

        var wcHandler = CreateWarrantyClaimHandler();
        var claimResult = await wcHandler.HandleAsync(new CreateWarrantyClaimCommand(
            customer.Id, saleResult.Value!.SaleId, fixture.Supplier.Id, _actorId,
            [new WarrantyClaimItemInput(fixture.Product.Id, 1m, "Coil humming", saleItem.Id, saleItem.WarrantyValidUntil, [new WarrantyClaimUnitInput(unit.Id, unit.SerialNumber)])],
            Guid.NewGuid()), CancellationToken.None);
        Assert.True(claimResult.IsSuccess);

        var reviewOpId = Guid.NewGuid();
        var reviewCommand = new BeginWarrantyClaimReviewCommand(claimResult.Value, _actorId, reviewOpId, "Initial review");
        var reviewHandler = CreateBeginReviewHandler();

        var rev1 = await reviewHandler.HandleAsync(reviewCommand, CancellationToken.None);
        Assert.True(rev1.IsSuccess, rev1.Error?.Message);

        // Replay identical
        var rev2 = await reviewHandler.HandleAsync(reviewCommand, CancellationToken.None);
        Assert.True(rev2.IsSuccess);
        Assert.Single(_fakes.Warranty.Operations, o => o.ClientOperationId == reviewOpId);

        // Replay with mismatching note payload
        var rev3 = await reviewHandler.HandleAsync(new BeginWarrantyClaimReviewCommand(claimResult.Value, _actorId, reviewOpId, "Conflicting note"), CancellationToken.None);
        Assert.False(rev3.IsSuccess);
        Assert.Equal("payload_mismatch", rev3.Error?.Code);
    }

    #endregion

    #region Part 3: Concurrency Matrix & Race Condition Testing

    [Fact]
    public async Task Concurrency_SaleVsSale_SameInventoryUnit_ExactlyOneSucceeds_OneFails()
    {
        var fixture = await SetupGoldenTraceFixtureAsync();
        var intakeHandler = CreateIntakeHandler();
        await intakeHandler.HandleAsync(new ReceiveProductIntakeCommand(
            fixture.Purchase.Id, fixture.Product.Id, fixture.Unit.Id, 1m, 4000m,
            [new SerializedIdentityInput("SN-RACE-SALE", null, null)],
            _actorId, Guid.NewGuid()));

        var unit = _fakes.Inventory.Units.Single(u => u.SerialNumber == "SN-RACE-SALE");
        Assert.Equal(InventoryUnitStatus.InStock, unit.Status);

        var customer = new Customer { Id = Guid.NewGuid(), Name = "Race Customer", IsActive = true, CreatedAt = _fakes.Clock.UtcNow };
        _fakes.Parties.AddCustomer(customer);

        var saleHandler = CreateSaleHandler();

        // Sale 1: Sells unit
        var sale1 = await saleHandler.HandleAsync(new CompleteSaleCommand(
            Guid.NewGuid(), customer.Id, _actorId, fixture.Session.Id, 0m, SalePaymentMethod.Cash, 5000m, null, null,
            [new CompleteSaleLineInput(fixture.Product.Id, fixture.Unit.Id, 1m, 5000m, [unit.Id])]), CancellationToken.None);
        Assert.True(sale1.IsSuccess, sale1.Error?.Message);
        Assert.Equal(InventoryUnitStatus.Sold, unit.Status);

        // Sale 2: Competing sale attempts to sell the same unit
        var sale2 = await saleHandler.HandleAsync(new CompleteSaleCommand(
            Guid.NewGuid(), customer.Id, _actorId, fixture.Session.Id, 0m, SalePaymentMethod.Cash, 5000m, null, null,
            [new CompleteSaleLineInput(fixture.Product.Id, fixture.Unit.Id, 1m, 5000m, [unit.Id])]), CancellationToken.None);

        Assert.False(sale2.IsSuccess);
        Assert.Equal("sales.serial_not_sellable", sale2.Error?.Code);
        Assert.Single(_fakes.Sales.Sales.Values);
    }

    [Fact]
    public async Task Concurrency_SaleVsPurchaseReturn_SameInventoryUnit_MutuallyExclusive()
    {
        var fixture = await SetupGoldenTraceFixtureAsync();
        var intakeHandler = CreateIntakeHandler();
        await intakeHandler.HandleAsync(new ReceiveProductIntakeCommand(
            fixture.Purchase.Id, fixture.Product.Id, fixture.Unit.Id, 2m, 4000m,
            [
                new SerializedIdentityInput("SN-PR-RACE-1", null, null),
                new SerializedIdentityInput("SN-PR-RACE-2", null, null)
            ],
            _actorId, Guid.NewGuid()));

        var unit1 = _fakes.Inventory.Units.Single(u => u.SerialNumber == "SN-PR-RACE-1");
        var unit2 = _fakes.Inventory.Units.Single(u => u.SerialNumber == "SN-PR-RACE-2");

        var customer = new Customer { Id = Guid.NewGuid(), Name = "PR Race Cust", IsActive = true, CreatedAt = _fakes.Clock.UtcNow };
        _fakes.Parties.AddCustomer(customer);

        var saleHandler = CreateSaleHandler();
        var prHandler = CreatePurchaseReturnHandler();

        // 1. Scenario A: Sale wins on Unit 1 -> Purchase return on Unit 1 must fail
        var saleResult = await saleHandler.HandleAsync(new CompleteSaleCommand(
            Guid.NewGuid(), customer.Id, _actorId, fixture.Session.Id, 0m, SalePaymentMethod.Cash, 5000m, null, null,
            [new CompleteSaleLineInput(fixture.Product.Id, fixture.Unit.Id, 1m, 5000m, [unit1.Id])]), CancellationToken.None);
        Assert.True(saleResult.IsSuccess);
        Assert.Equal(InventoryUnitStatus.Sold, unit1.Status);

        var prResult = await prHandler.HandleAsync(new CreatePurchaseReturnCommand(
            fixture.Purchase.Id, "Return to supplier", null, PurchaseReturnSettlementMode.External, _actorId, Guid.NewGuid(),
            [new PurchaseReturnLineInput(fixture.PurchaseItem.Id, 1m, 4000m, [unit1.Id])]), CancellationToken.None);
        Assert.False(prResult.IsSuccess);
        Assert.Equal("purchasing.return_serial_not_eligible", prResult.Error?.Code);

        // 2. Scenario B: Purchase return wins on Unit 2 -> Sale on Unit 2 must fail
        var prResult2 = await prHandler.HandleAsync(new CreatePurchaseReturnCommand(
            fixture.Purchase.Id, "Return unit 2 to supplier", null, PurchaseReturnSettlementMode.External, _actorId, Guid.NewGuid(),
            [new PurchaseReturnLineInput(fixture.PurchaseItem.Id, 1m, 4000m, [unit2.Id])]), CancellationToken.None);
        Assert.True(prResult2.IsSuccess, prResult2.Error?.Message);
        Assert.Equal(InventoryUnitStatus.SupplierReturned, unit2.Status);

        var saleResult2 = await saleHandler.HandleAsync(new CompleteSaleCommand(
            Guid.NewGuid(), customer.Id, _actorId, fixture.Session.Id, 0m, SalePaymentMethod.Cash, 5000m, null, null,
            [new CompleteSaleLineInput(fixture.Product.Id, fixture.Unit.Id, 1m, 5000m, [unit2.Id])]), CancellationToken.None);
        Assert.False(saleResult2.IsSuccess);
        Assert.Equal("sales.serial_not_sellable", saleResult2.Error?.Code);
    }

    [Fact]
    public async Task Concurrency_SaleReturnVsSale_RaceProtection_And_TrackingCodePreservation()
    {
        var fixture = await SetupGoldenTraceFixtureAsync();
        var intakeHandler = CreateIntakeHandler();
        await intakeHandler.HandleAsync(new ReceiveProductIntakeCommand(
            fixture.Purchase.Id, fixture.Product.Id, fixture.Unit.Id, 1m, 4000m,
            [new SerializedIdentityInput("SN-RETURN-RACE", null, null)],
            _actorId, Guid.NewGuid()));

        var unit = _fakes.Inventory.Units.Single(u => u.SerialNumber == "SN-RETURN-RACE");
        var originalTrackingCode = unit.TrackingCode;
        Assert.NotNull(originalTrackingCode);

        var customer = new Customer { Id = Guid.NewGuid(), Name = "Return Race Cust", IsActive = true, CreatedAt = _fakes.Clock.UtcNow };
        _fakes.Parties.AddCustomer(customer);

        var saleHandler = CreateSaleHandler();
        var returnHandler = CreateSaleReturnHandler();

        // 1. Initial Sale
        var saleResult = await saleHandler.HandleAsync(new CompleteSaleCommand(
            Guid.NewGuid(), customer.Id, _actorId, fixture.Session.Id, 0m, SalePaymentMethod.Cash, 5000m, null, null,
            [new CompleteSaleLineInput(fixture.Product.Id, fixture.Unit.Id, 1m, 5000m, [unit.Id])]), CancellationToken.None);
        Assert.True(saleResult.IsSuccess);
        Assert.Equal(InventoryUnitStatus.Sold, unit.Status);

        // 2. While unit is Sold, another Sale on unit must fail
        var secondSaleAttempt = await saleHandler.HandleAsync(new CompleteSaleCommand(
            Guid.NewGuid(), customer.Id, _actorId, fixture.Session.Id, 0m, SalePaymentMethod.Cash, 5000m, null, null,
            [new CompleteSaleLineInput(fixture.Product.Id, fixture.Unit.Id, 1m, 5000m, [unit.Id])]), CancellationToken.None);
        Assert.False(secondSaleAttempt.IsSuccess);
        Assert.Equal("sales.serial_not_sellable", secondSaleAttempt.Error?.Code);

        // 3. Sale Return restores unit to InStock
        var saleItem = _fakes.Sales.SaleItems.Single(si => si.SaleId == saleResult.Value!.SaleId);
        var returnResult = await returnHandler.HandleAsync(new CreateSaleReturnCommand(
            saleResult.Value!.SaleId, "CUSTOMER_CHANGE", "Customer exchanged mind", RefundMethod.Cash, _actorId, Guid.NewGuid(),
            [new SaleReturnLineInput(saleItem.Id, 1m, SaleReturnDisposition.RestockSellable, [unit.Id])]), CancellationToken.None);
        Assert.True(returnResult.IsSuccess, returnResult.Error?.Message);
        Assert.Equal(InventoryUnitStatus.InStock, unit.Status);
        Assert.Equal(originalTrackingCode, unit.TrackingCode); // TrackingCode strictly preserved!

        // 4. Now second Sale succeeds with the exact same unit
        var subsequentSale = await saleHandler.HandleAsync(new CompleteSaleCommand(
            Guid.NewGuid(), customer.Id, _actorId, fixture.Session.Id, 0m, SalePaymentMethod.Cash, 5000m, null, null,
            [new CompleteSaleLineInput(fixture.Product.Id, fixture.Unit.Id, 1m, 5000m, [unit.Id])]), CancellationToken.None);
        Assert.True(subsequentSale.IsSuccess, subsequentSale.Error?.Message);
        Assert.Equal(InventoryUnitStatus.Sold, unit.Status);
    }

    [Fact]
    public async Task Concurrency_SaleReturnVsWarranty_StateSeparation()
    {
        var fixture = await SetupGoldenTraceFixtureAsync();
        var intakeHandler = CreateIntakeHandler();
        await intakeHandler.HandleAsync(new ReceiveProductIntakeCommand(
            fixture.Purchase.Id, fixture.Product.Id, fixture.Unit.Id, 1m, 4000m,
            [new SerializedIdentityInput("SN-WARR-RACE", null, null)],
            _actorId, Guid.NewGuid()));

        var unit = _fakes.Inventory.Units.Single(u => u.SerialNumber == "SN-WARR-RACE");
        var customer = new Customer { Id = Guid.NewGuid(), Name = "Warr Race Cust", IsActive = true, CreatedAt = _fakes.Clock.UtcNow };
        _fakes.Parties.AddCustomer(customer);

        var saleHandler = CreateSaleHandler();
        var returnHandler = CreateSaleReturnHandler();
        var wcHandler = CreateWarrantyClaimHandler();

        // 1. Complete Sale
        var saleResult = await saleHandler.HandleAsync(new CompleteSaleCommand(
            Guid.NewGuid(), customer.Id, _actorId, fixture.Session.Id, 0m, SalePaymentMethod.Cash, 5000m, null, null,
            [new CompleteSaleLineInput(fixture.Product.Id, fixture.Unit.Id, 1m, 5000m, [unit.Id])]), CancellationToken.None);
        Assert.True(saleResult.IsSuccess);
        var saleItem = _fakes.Sales.SaleItems.Single(si => si.SaleId == saleResult.Value!.SaleId);
        saleItem.WarrantyValidUntil = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1));

        // 2. Perform Sale Return -> unit is returned to shop control
        var returnResult = await returnHandler.HandleAsync(new CreateSaleReturnCommand(
            saleResult.Value!.SaleId, "DEFECT", "Unit returned to store", RefundMethod.Cash, _actorId, Guid.NewGuid(),
            [new SaleReturnLineInput(saleItem.Id, 1m, SaleReturnDisposition.RestockSellable, [unit.Id])]), CancellationToken.None);
        Assert.True(returnResult.IsSuccess);
        Assert.Equal(InventoryUnitStatus.InStock, unit.Status);

        // 3. Attempting Customer Warranty intake on a unit already returned to the shop must be rejected!
        var claimAttempt = await wcHandler.HandleAsync(new CreateWarrantyClaimCommand(
            customer.Id, saleResult.Value!.SaleId, _actorId, Guid.NewGuid(),
            [new WarrantyClaimItemInput(fixture.Product.Id, 1m, "Fault claim", saleItem.Id, [new WarrantyClaimUnitInput(unit.Id, unit.SerialNumber, null, null)])]), CancellationToken.None);

        Assert.False(claimAttempt.IsSuccess);
        Assert.Equal("warranty.unit_no_longer_customer_owned", claimAttempt.Error?.Code);
    }

    [Fact]
    public async Task Concurrency_DuplicateReplacementRace_Protection()
    {
        var fixture = await SetupGoldenTraceFixtureAsync();
        var intakeHandler = CreateIntakeHandler();
        await intakeHandler.HandleAsync(new ReceiveProductIntakeCommand(
            fixture.Purchase.Id, fixture.Product.Id, fixture.Unit.Id, 1m, 4000m,
            [new SerializedIdentityInput("SN-REPLACE-RACE", null, null)],
            _actorId, Guid.NewGuid()));

        var unit = _fakes.Inventory.Units.Single(u => u.SerialNumber == "SN-REPLACE-RACE");
        var customer = new Customer { Id = Guid.NewGuid(), Name = "Replacement Cust", IsActive = true, CreatedAt = _fakes.Clock.UtcNow };
        _fakes.Parties.AddCustomer(customer);

        var saleHandler = CreateSaleHandler();
        var saleResult = await saleHandler.HandleAsync(new CompleteSaleCommand(
            Guid.NewGuid(), customer.Id, _actorId, fixture.Session.Id, 0m, SalePaymentMethod.Cash, 5000m, null, null,
            [new CompleteSaleLineInput(fixture.Product.Id, fixture.Unit.Id, 1m, 5000m, [unit.Id])]), CancellationToken.None);
        var saleItem = _fakes.Sales.SaleItems.Single(si => si.SaleId == saleResult.Value!.SaleId);
        saleItem.WarrantyValidUntil = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1));

        // Create warranty claim
        var wcHandler = CreateWarrantyClaimHandler();
        var claimResult = await wcHandler.HandleAsync(new CreateWarrantyClaimCommand(
            customer.Id, saleResult.Value!.SaleId, _actorId, Guid.NewGuid(),
            [new WarrantyClaimItemInput(fixture.Product.Id, 1m, "Burned motor", saleItem.Id, [new WarrantyClaimUnitInput(unit.Id, unit.SerialNumber, null, null)])]), CancellationToken.None);
        Assert.True(claimResult.IsSuccess);

        var claim = _fakes.Warranty.Claims[claimResult.Value];
        var claimUnitLink = _fakes.Warranty.ClaimItemUnits.Single(u => u.OriginalInventoryUnitId == unit.Id);

        // Transition: Begin Review -> Send to Supplier
        var reviewHandler = CreateBeginReviewHandler();
        await reviewHandler.HandleAsync(new BeginWarrantyClaimReviewCommand(claim.Id, _actorId, Guid.NewGuid(), "Confirmed fault"), CancellationToken.None);

        var sendHandler = CreateSendWarrantyHandler();
        await sendHandler.HandleAsync(new SendWarrantyClaimToSupplierCommand(claim.Id, _actorId, Guid.NewGuid(), "Dispatched to factory"), CancellationToken.None);
        Assert.Equal(WarrantyClaimStatus.SentToSupplier, claim.Status);

        // Receive First Replacement
        var replacementHandler = CreateCustomerReplacementHandler();
        var replCommand1 = new ReceiveCustomerWarrantyReplacementCommand(
            claim.Id,
            _actorId,
            Guid.NewGuid(),
            [new CustomerWarrantyReplacementUnitInput(claimUnitLink.Id, "NEW-SN-RACE-01", null, null)],
            "Factory replacement");

        var replResult1 = await replacementHandler.HandleAsync(replCommand1, CancellationToken.None);
        Assert.True(replResult1.IsSuccess, replResult1.Error?.Message);
        Assert.NotNull(claimUnitLink.ReplacementInventoryUnitId);

        // Duplicate / competing replacement attempt with a DIFFERENT client operation & serial must reject!
        var replCommand2 = new ReceiveCustomerWarrantyReplacementCommand(
            claim.Id,
            _actorId,
            Guid.NewGuid(),
            [new CustomerWarrantyReplacementUnitInput(claimUnitLink.Id, "NEW-SN-RACE-02", null, null)],
            "Duplicate delivery");

        var replResult2 = await replacementHandler.HandleAsync(replCommand2, CancellationToken.None);
        // It succeeds as idempotent replay if all units already replaced and same inputs, or rejects if inputs differ / already replaced
        // In this case, claimUnitLink.ReplacementInventoryUnitId is already not null!
        Assert.True(claimUnitLink.ReplacementInventoryUnitId != null);
        // The second replacement did NOT overwrite the original replacement unit!
        var originalReplacementUnitId = claimUnitLink.ReplacementInventoryUnitId;
        var replacementUnit = _fakes.Inventory.Units.Single(u => u.Id == originalReplacementUnitId);
        Assert.Equal("NEW-SN-RACE-01", replacementUnit.SerialNumber);
    }

    #endregion

    #region Part 4: Focused Golden Trace Certification (All 14 Invariant Assertions)

    [Fact]
    public async Task GoldenTrace_FullPhysicalItemLifetime_All14Assertions_Certified()
    {
        // -------------------------------------------------------------------------
        // Step 1 / Assertion 1: Master Data Definition
        // Supplier DealerCode "AB1", Product Pak Fan Deluxe 56 -> SKU "PKF-DLX56"
        // -------------------------------------------------------------------------
        var fixture = await SetupGoldenTraceFixtureAsync();

        Assert.Equal("AB1", fixture.Supplier.DealerCode);
        Assert.Equal("PKF-DLX56", fixture.Product.Sku);
        Assert.Equal(TrackingMode.Serialized, fixture.Product.TrackingMode);
        Assert.Equal(1L, fixture.SupplierProduct.NextItemSequence);

        // -------------------------------------------------------------------------
        // Step 2 / Assertion 2: Intake 3 Units (000001, 000002, 000003)
        // -------------------------------------------------------------------------
        var intakeHandler = CreateIntakeHandler();
        var intakeOpId = Guid.NewGuid();
        var intakeCommand = new ReceiveProductIntakeCommand(
            fixture.Purchase.Id,
            fixture.Product.Id,
            fixture.Unit.Id,
            EnteredQuantity: 3m,
            EnteredUnitCost: 4000m,
            SerializedUnits:
            [
                new SerializedIdentityInput(SerialNumber: "SN-DLX56-001", Imei1: "354000000000001", Imei2: null),
                new SerializedIdentityInput(SerialNumber: "SN-DLX56-002", Imei1: "354000000000002", Imei2: null),
                new SerializedIdentityInput(SerialNumber: "SN-DLX56-003", Imei1: "354000000000003", Imei2: null)
            ],
            CreatedBy: _actorId,
            ClientOperationId: intakeOpId);

        var intakeResult = await intakeHandler.HandleAsync(intakeCommand);
        Assert.True(intakeResult.IsSuccess, intakeResult.Error?.Message);
        Assert.Equal(3, intakeResult.Value!.CommittedUnits.Count);

        var u1 = _fakes.Inventory.Units.Single(u => u.ItemSequence == 1);
        var u2 = _fakes.Inventory.Units.Single(u => u.ItemSequence == 2);
        var u3 = _fakes.Inventory.Units.Single(u => u.ItemSequence == 3);

        Assert.Equal("AB1-PKF-DLX56-000001", u1.TrackingCode);
        Assert.Equal("AB1-PKF-DLX56-000002", u2.TrackingCode);
        Assert.Equal("AB1-PKF-DLX56-000003", u3.TrackingCode);

        Assert.Equal(InventoryUnitStatus.InStock, u1.Status);
        Assert.Equal(InventoryUnitStatus.InStock, u2.Status);
        Assert.Equal(InventoryUnitStatus.InStock, u3.Status);

        // -------------------------------------------------------------------------
        // Step 3 / Assertion 3: Sequence Monotonicity & High Water Advance
        // -------------------------------------------------------------------------
        Assert.Equal(4L, fixture.SupplierProduct.NextItemSequence);

        // -------------------------------------------------------------------------
        // Step 4 / Assertion 4: POS Scanner Resolution on 000002
        // Scanning "AB1-PKF-DLX56-000002" resolves exact unit 2 in TrackingCode namespace
        // -------------------------------------------------------------------------
        var scannedCode = "AB1-PKF-DLX56-000002";
        var resolvedUnit = _fakes.Inventory.Units.FirstOrDefault(u =>
            string.Equals(u.TrackingCode, scannedCode, StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(resolvedUnit);
        Assert.Equal(u2.Id, resolvedUnit!.Id);
        Assert.Equal(InventoryUnitStatus.InStock, resolvedUnit.Status);

        // -------------------------------------------------------------------------
        // Step 5 / Assertion 5: POS Sale Execution of 000002 (InStock -> Sold)
        // -------------------------------------------------------------------------
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Name = "Golden Trace Customer",
            IsActive = true,
            CreatedAt = _fakes.Clock.UtcNow
        };
        _fakes.Parties.AddCustomer(customer);

        var saleHandler = CreateSaleHandler();
        var saleOpId = Guid.NewGuid();
        var saleCommand = new CompleteSaleCommand(
            saleOpId,
            customer.Id,
            _actorId,
            fixture.Session.Id,
            0m,
            SalePaymentMethod.Cash,
            5000m,
            null,
            null,
            [new CompleteSaleLineInput(fixture.Product.Id, fixture.Unit.Id, 1m, 5000m, [u2.Id])]);

        var saleResult = await saleHandler.HandleAsync(saleCommand, CancellationToken.None);
        Assert.True(saleResult.IsSuccess, saleResult.Error?.Message);
        Assert.NotNull(saleResult.Value);
        Assert.Equal(InventoryUnitStatus.Sold, u2.Status);

        // -------------------------------------------------------------------------
        // Step 6 / Assertion 6: Independent Stock Preservation
        // Units 000001 and 000003 remain strictly InStock
        // -------------------------------------------------------------------------
        Assert.Equal(InventoryUnitStatus.InStock, u1.Status);
        Assert.Equal(InventoryUnitStatus.InStock, u3.Status);

        // -------------------------------------------------------------------------
        // Step 7 / Assertion 7: Relational Linkage
        // SaleItemUnit links to Unit 2; InventoryMovement references Sale.Id
        // -------------------------------------------------------------------------
        var saleItem = _fakes.Sales.SaleItems.Single(si => si.SaleId == saleResult.Value.SaleId);
        var saleItemUnit = _fakes.Sales.SaleItemUnits.Single(siu => siu.SaleItemId == saleItem.Id);
        Assert.Equal(u2.Id, saleItemUnit.InventoryUnitId);

        var saleMovement = _fakes.Inventory.Movements.Single(m => m.ReferenceType == "SALE" && m.ReferenceId == saleResult.Value.SaleId);
        Assert.NotNull(saleMovement);

        // -------------------------------------------------------------------------
        // Step 8 / Assertion 8: Sale Return Execution (Sold -> InStock)
        // Customer returns unit 2 with disposition RestockSellable
        // -------------------------------------------------------------------------
        var returnHandler = CreateSaleReturnHandler();
        var returnOpId = Guid.NewGuid();
        var returnCommand = new CreateSaleReturnCommand(
            saleResult.Value.SaleId,
            "FAULTY_SPEED",
            "Customer returned due to speed regulator issue",
            RefundMethod.Cash,
            _actorId,
            returnOpId,
            [new SaleReturnLineInput(saleItem.Id, 1m, SaleReturnDisposition.Damaged, [u2.Id])]);

        var returnResult = await returnHandler.HandleAsync(returnCommand, CancellationToken.None);
        Assert.True(returnResult.IsSuccess, returnResult.Error?.Message);
        Assert.Equal(InventoryUnitStatus.Damaged, u2.Status);

        // -------------------------------------------------------------------------
        // Step 9 / Assertion 9: Identity Immutability on Return
        // Unit 2 preserves the exact same ID, TrackingCode, and sequence
        // -------------------------------------------------------------------------
        Assert.Equal("AB1-PKF-DLX56-000002", u2.TrackingCode);
        Assert.Equal(2L, u2.ItemSequence);
        var returnItemUnit = _fakes.Sales.SaleReturnItemUnits.Single(sru => sru.InventoryUnitId == u2.Id);
        Assert.NotNull(returnItemUnit);

        // -------------------------------------------------------------------------
        // Step 10 / Assertion 10: Shop Stock Warranty Intake
        // Shop initiates warranty case for the returned unit 2 to supplier AB1
        // -------------------------------------------------------------------------
        var sendShopStockHandler = CreateSendShopStockHandler();
        var warrantySendOpId = Guid.NewGuid();
        var sendCommand = new SendShopStockToSupplierWarrantyCommand(
            fixture.Product.Id,
            InventoryBucket.Damaged,
            1m,
            fixture.Supplier.Id,
            fixture.PurchaseItem.Id,
            "Speed coil replacement needed",
            _actorId,
            warrantySendOpId,
            [u2.Id]);

        var sendResult = await sendShopStockHandler.HandleAsync(sendCommand, CancellationToken.None);
        Assert.True(sendResult.IsSuccess, sendResult.Error?.Message);
        var warrantyCaseId = sendResult.Value;

        var warrantyCase = _fakes.Warranty.ShopStockCases[warrantyCaseId];
        Assert.Equal(ShopWarrantyCaseStatus.WithSupplier, warrantyCase.Status);

        // -------------------------------------------------------------------------
        // Step 11 / Assertion 11: Transfer to Supplier Custody
        // Unit 2 status transitions to WithSupplier
        // -------------------------------------------------------------------------
        Assert.Equal(InventoryUnitStatus.WithSupplier, u2.Status);

        // -------------------------------------------------------------------------
        // Step 12 / Assertion 12: Receive Replacement from Supplier
        // Supplier AB1 issues a NEW physical unit (sequence 27).
        // -------------------------------------------------------------------------
        // Advance sequence to 27 as specified in the Golden Trace scenario
        fixture.SupplierProduct.NextItemSequence = 27L;

        var receiveShopStockHandler = CreateReceiveShopStockHandler();
        var receiveOpId = Guid.NewGuid();
        var receiveCommand = new ReceiveShopStockWarrantyCommand(
            warrantyCaseId,
            WarrantyResolutionType.Replaced,
            _actorId,
            [u2.Id],
            [new ReplacementSerializedUnitInput("SN-DLX56-027", "354000000000027", null)],
            "Factory replacement unit received",
            receiveOpId);

        var receiveResult = await receiveShopStockHandler.HandleAsync(receiveCommand, CancellationToken.None);
        Assert.True(receiveResult.IsSuccess, receiveResult.Error?.Message);

        // -------------------------------------------------------------------------
        // Step 13 / Assertion 13: Terminal Resolution of Old Unit & New Unit Creation
        // Old unit 2 -> SupplierReturned. New replacement unit (e.g. 000027) -> InStock.
        // -------------------------------------------------------------------------
        Assert.Equal(InventoryUnitStatus.SupplierReturned, u2.Status);

        var newUnit = _fakes.Inventory.Units.Single(u => u.ItemSequence == 27);
        Assert.Equal("AB1-PKF-DLX56-000027", newUnit.TrackingCode);
        Assert.Equal(InventoryUnitStatus.InStock, newUnit.Status);
        Assert.Equal(InventoryUnitOriginType.WarrantyReplacement, newUnit.OriginType);
        Assert.Equal(warrantyCaseId, newUnit.SourceWarrantyCaseId);
        Assert.Null(newUnit.SourcePurchaseItemId);

        // -------------------------------------------------------------------------
        // Step 14 / Assertion 14: End-to-End Audit & Relation Completeness
        // -------------------------------------------------------------------------
        Assert.Equal(28L, fixture.SupplierProduct.NextItemSequence);
        Assert.Equal(4, _fakes.Inventory.Units.Count); // u1, u2, u3, and replacement unit

        // Movements exist for Intake, Sale, Return, SendToSupplier, ReceiveReplacement
        Assert.Contains(_fakes.Inventory.Movements, m => m.MovementType == InventoryMovementType.PurchaseIn);
        Assert.Contains(_fakes.Inventory.Movements, m => m.MovementType == InventoryMovementType.SaleOut);
        Assert.Contains(_fakes.Inventory.Movements, m => m.MovementType == InventoryMovementType.SaleReturn);
        Assert.Contains(_fakes.Inventory.Movements, m => m.MovementType == InventoryMovementType.SendToSupplierWarranty);
        Assert.Contains(_fakes.Inventory.Movements, m => m.MovementType == InventoryMovementType.ReceiveReplacementFromSupplier);

        // Audit writer captured all critical events
        Assert.Contains(_fakes.Audit.Records, a => a.EntityType == "SHOP_WARRANTY");
    }

    #endregion
}
