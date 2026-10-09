using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Application.Features.Warranty;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Operations;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.Warranty;
using EdgeRetails.Infrastructure;
using EdgeRetails.Infrastructure.Persistence;
using EdgeRetails.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EdgeRetails.UnitTests;

public sealed class OperationOutcomeAuthorityTests
{
    private readonly Phase2TestDoubles _fakes = new();

    private OperationStatusQueryHandler CreateOperationStatusQueryHandler() =>
        new(
            _fakes.Sales,
            _fakes.Purchasing,
            _fakes.SupplierAccounts,
            _fakes.Inventory,
            _fakes.Expenses,
            _fakes.Warranty,
            _fakes.Quotations,
            _fakes.Thaka,
            _fakes.OutcomeLedger);

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
            _fakes.UnitOfWork,
            _fakes.OperationLock);

    [Fact]
    public async Task OperationStatus_UsesCanonicalOutcomeAuthority()
    {
        // Arrange
        var handler = CreateOperationStatusQueryHandler();
        var clientOpId = Guid.NewGuid();
        var saleId = Guid.NewGuid();
        var invoiceNumber = "INV-CANONICAL-001";
        var fingerprint = OperationPayloadFingerprint.ComputeSha256("test-sale-payload");

        await _fakes.OutcomeLedger.RecordSuccessAsync(
            clientOpId,
            "Sale",
            saleId,
            invoiceNumber,
            payloadFingerprint: fingerprint);

        // Act
        var result = await handler.HandleAsync(new OperationStatusQuery(clientOpId), CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);
        var status = result.Value;
        Assert.True(status.Found);
        Assert.True(status.WasCommitted);
        Assert.Equal("Succeeded", status.Status);
        Assert.Equal("Sale", status.OperationType);
        Assert.Equal(saleId, status.EntityId);
        Assert.Equal(invoiceNumber, status.DocumentNumber);
        Assert.Equal(fingerprint, status.PayloadFingerprint);
    }

    [Fact]
    public async Task OperationStatus_LegacyBusinessTablesCannotContradictCanonicalOutcome()
    {
        // Arrange: Canonical ledger recorded operation as FAILED
        var handler = CreateOperationStatusQueryHandler();
        var clientOpId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        await _fakes.OutcomeLedger.RecordFailureAsync(
            clientOpId,
            "Sale",
            "payment.card_declined",
            "Payment processor declined transaction.",
            actorId: actorId);

        // A contradictory record exists in the legacy business table
        var conflictingSale = new Sale
        {
            Id = Guid.NewGuid(),
            ClientOperationId = clientOpId,
            InvoiceNumber = "INV-CONTRADICTORY-999",
            Status = SaleStatus.Completed,
            PaymentStatus = SalePaymentStatus.Paid,
            Subtotal = 500m,
            GrandTotal = 500m,
            CompletedAt = _fakes.Clock.UtcNow
        };
        _fakes.Sales.AddSale(conflictingSale);

        // Act: Query operation status
        var result = await handler.HandleAsync(new OperationStatusQuery(clientOpId), CancellationToken.None);

        // Assert: Canonical outcome MUST WIN; legacy tables cannot contradict it
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);
        var status = result.Value;
        Assert.True(status.Found);
        Assert.False(status.WasCommitted, "Committed record from legacy table must NOT override canonical failure");
        Assert.Equal("Failed", status.Status);
        Assert.Equal("payment.card_declined", status.ErrorCode);
        Assert.Equal("Payment processor declined transaction.", status.ErrorMessage);
        Assert.Null(status.DocumentNumber);
    }

    [Fact]
    public async Task UnknownOperation_ReturnsNotFound()
    {
        // Arrange
        var handler = CreateOperationStatusQueryHandler();
        var unknownOperationId = Guid.NewGuid();

        // Act
        var result = await handler.HandleAsync(new OperationStatusQuery(unknownOperationId), CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);
        var status = result.Value;
        Assert.Equal(unknownOperationId, status.ClientOperationId);
        Assert.False(status.Found);
        Assert.False(status.WasCommitted);
        Assert.Equal("NotFound", status.Status);
        Assert.Null(status.OperationType);
        Assert.Null(status.EntityId);
        Assert.Null(status.DocumentNumber);
    }

    [Fact]
    public async Task SuccessfulOperation_ReturnsCanonicalResultReference()
    {
        // Arrange
        var handler = CreateOperationStatusQueryHandler();

        var saleOpId = Guid.NewGuid();
        var saleId = Guid.NewGuid();
        await _fakes.OutcomeLedger.RecordSuccessAsync(saleOpId, "Sale", saleId, "INV-1001");

        var purchaseOpId = Guid.NewGuid();
        var purchaseId = Guid.NewGuid();
        await _fakes.OutcomeLedger.RecordSuccessAsync(purchaseOpId, "Purchase", purchaseId, "PUR-2001");

        var paymentOpId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        await _fakes.OutcomeLedger.RecordSuccessAsync(paymentOpId, "SupplierPayment", paymentId, "PAY-3001");

        var warrantyOpId = Guid.NewGuid();
        var claimId = Guid.NewGuid();
        await _fakes.OutcomeLedger.RecordSuccessAsync(warrantyOpId, "WarrantyClaim", claimId, "WCL-4001");

        // Act & Assert: Sale
        var saleRes = await handler.HandleAsync(new OperationStatusQuery(saleOpId), CancellationToken.None);
        Assert.True(saleRes.IsSuccess);
        Assert.NotNull(saleRes.Value);
        Assert.Equal(saleId, saleRes.Value.EntityId);
        Assert.Equal("INV-1001", saleRes.Value.DocumentNumber);
        Assert.Equal("Sale", saleRes.Value.OperationType);
        Assert.True(saleRes.Value.WasCommitted);

        // Act & Assert: Purchase
        var purRes = await handler.HandleAsync(new OperationStatusQuery(purchaseOpId), CancellationToken.None);
        Assert.True(purRes.IsSuccess);
        Assert.NotNull(purRes.Value);
        Assert.Equal(purchaseId, purRes.Value.EntityId);
        Assert.Equal("PUR-2001", purRes.Value.DocumentNumber);
        Assert.Equal("Purchase", purRes.Value.OperationType);
        Assert.True(purRes.Value.WasCommitted);

        // Act & Assert: Supplier Payment
        var payRes = await handler.HandleAsync(new OperationStatusQuery(paymentOpId), CancellationToken.None);
        Assert.True(payRes.IsSuccess);
        Assert.NotNull(payRes.Value);
        Assert.Equal(paymentId, payRes.Value.EntityId);
        Assert.Equal("PAY-3001", payRes.Value.DocumentNumber);
        Assert.Equal("SupplierPayment", payRes.Value.OperationType);
        Assert.True(payRes.Value.WasCommitted);

        // Act & Assert: Warranty Claim
        var wclRes = await handler.HandleAsync(new OperationStatusQuery(warrantyOpId), CancellationToken.None);
        Assert.True(wclRes.IsSuccess);
        Assert.NotNull(wclRes.Value);
        Assert.Equal(claimId, wclRes.Value.EntityId);
        Assert.Equal("WCL-4001", wclRes.Value.DocumentNumber);
        Assert.Equal("WarrantyClaim", wclRes.Value.OperationType);
        Assert.True(wclRes.Value.WasCommitted);
    }

    [Fact]
    public async Task SaleReplay_ReturnsOriginalOutcome()
    {
        // Arrange
        var saleHandler = CreateSaleHandler();
        var statusHandler = CreateOperationStatusQueryHandler();
        var customerId = Guid.NewGuid();
        _fakes.Parties.AddCustomer(new Customer { Id = customerId, Name = "Replay Customer", IsActive = true });

        var baseUnitId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var productUnitId = Guid.NewGuid();

        _fakes.Catalog.AddProduct(new Product
        {
            Id = productId,
            Name = "Monitor 4K",
            BaseUnitId = baseUnitId,
            DefaultSalePrice = 800m,
            IsActive = true
        });
        _fakes.Catalog.AddProductUnit(new ProductUnit
        {
            Id = productUnitId,
            ProductId = productId,
            UnitId = baseUnitId,
            FactorToBaseUnit = 1m,
            CanSell = true,
            IsActive = true
        });
        _fakes.Inventory.Balances[productId] = new StockBalance
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            SellableQty = 10m
        };
        var lot = new InventoryLot
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
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

        var clientOpId = Guid.NewGuid();
        var cashierUserId = Guid.NewGuid();
        var saleCommand = new CompleteSaleCommand(
            ClientOperationId: clientOpId,
            CustomerId: customerId,
            CashierUserId: cashierUserId,
            SessionId: null,
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Bank,
            AmountTendered: 800m,
            PaymentReference: "BANK-01",
            QuotationId: null,
            Lines: [new CompleteSaleLineInput(productId, productUnitId, 1m, 800m, [])]);

        // Act 1: Initial mutation
        var firstResult = await saleHandler.HandleAsync(saleCommand, CancellationToken.None);
        Assert.True(firstResult.IsSuccess, firstResult.Error?.Message);
        Assert.NotNull(firstResult.Value);
        Assert.False(firstResult.Value.WasExisting);
        var originalSaleId = firstResult.Value.SaleId;
        var originalInvoice = firstResult.Value.InvoiceNumber;

        // Act 2: Replay exact mutation
        var replayResult = await saleHandler.HandleAsync(saleCommand, CancellationToken.None);
        Assert.True(replayResult.IsSuccess, replayResult.Error?.Message);
        Assert.NotNull(replayResult.Value);
        Assert.True(replayResult.Value.WasExisting, "Replay must indicate existing execution");
        Assert.Equal(originalSaleId, replayResult.Value.SaleId);
        Assert.Equal(originalInvoice, replayResult.Value.InvoiceNumber);

        // Act 3: Query operation status authority
        var statusResult = await statusHandler.HandleAsync(new OperationStatusQuery(clientOpId), CancellationToken.None);
        Assert.True(statusResult.IsSuccess, statusResult.Error?.Message);
        Assert.NotNull(statusResult.Value);
        Assert.True(statusResult.Value.Found);
        Assert.True(statusResult.Value.WasCommitted);
        Assert.Equal("Succeeded", statusResult.Value.Status);
        Assert.Equal(originalSaleId, statusResult.Value.EntityId);
        Assert.Equal(originalInvoice, statusResult.Value.DocumentNumber);

        // Invariant: exactly one sale record exists
        Assert.Single(_fakes.Sales.Sales.Values, s => s.ClientOperationId == clientOpId);
    }

    [Fact]
    public async Task PurchaseReplay_ReturnsOriginalOutcome()
    {
        // Arrange
        var purchaseHandler = CreatePurchaseHandler();
        var statusHandler = CreateOperationStatusQueryHandler();

        var supplierId = Guid.NewGuid();
        _fakes.Parties.AddSupplier(new Supplier { Id = supplierId, Name = "Replay Supplier", DealerCode = "DLR-001", IsActive = true });

        var baseUnitId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var productUnitId = Guid.NewGuid();

        _fakes.Catalog.AddProduct(new Product
        {
            Id = productId,
            Name = "Router AX3000",
            BaseUnitId = baseUnitId,
            TrackingMode = TrackingMode.Quantity,
            IsActive = true
        });
        _fakes.Catalog.AddProductUnit(new ProductUnit
        {
            Id = productUnitId,
            ProductId = productId,
            UnitId = baseUnitId,
            FactorToBaseUnit = 1m,
            CanPurchase = true,
            IsActive = true
        });

        var clientOpId = Guid.NewGuid();
        var buyerId = Guid.NewGuid();
        var purchaseCommand = new CreatePurchaseCommand(
            SupplierId: supplierId,
            SupplierInvoiceNumber: "INV-SUPP-1234",
            PurchaseDate: _fakes.Clock.ShopDate,
            Note: "Replay purchase test",
            OtherCharges: 0m,
            SettlementMode: PurchaseSettlementMode.External,
            CreatedBy: buyerId,
            ClientOperationId: clientOpId,
            Lines: [new CreatePurchaseLineInput(productId, productUnitId, 5m, 100m, 120m, [])]);

        // Act 1: Initial mutation
        var firstResult = await purchaseHandler.HandleAsync(purchaseCommand, CancellationToken.None);
        Assert.True(firstResult.IsSuccess, firstResult.Error?.Message);
        Assert.NotNull(firstResult.Value);
        Assert.False(firstResult.Value.WasExisting);
        var originalPurchaseId = firstResult.Value.PurchaseId;
        var originalPurchaseNumber = firstResult.Value.PurchaseNumber;

        // Act 2: Replay exact mutation
        var replayResult = await purchaseHandler.HandleAsync(purchaseCommand, CancellationToken.None);
        Assert.True(replayResult.IsSuccess, replayResult.Error?.Message);
        Assert.NotNull(replayResult.Value);
        Assert.True(replayResult.Value.WasExisting, "Replay must indicate existing execution");
        Assert.Equal(originalPurchaseId, replayResult.Value.PurchaseId);
        Assert.Equal(originalPurchaseNumber, replayResult.Value.PurchaseNumber);

        // Act 3: Query operation status authority
        var statusResult = await statusHandler.HandleAsync(new OperationStatusQuery(clientOpId), CancellationToken.None);
        Assert.True(statusResult.IsSuccess, statusResult.Error?.Message);
        Assert.NotNull(statusResult.Value);
        Assert.True(statusResult.Value.Found);
        Assert.True(statusResult.Value.WasCommitted);
        Assert.Equal("Succeeded", statusResult.Value.Status);
        Assert.Equal(originalPurchaseId, statusResult.Value.EntityId);
        Assert.Equal(originalPurchaseNumber, statusResult.Value.DocumentNumber);

        // Invariant: exactly one purchase record exists
        Assert.Single(_fakes.Purchasing.Purchases.Values, p => p.ClientOperationId == clientOpId);
    }

    [Fact]
    public async Task SupplierPaymentReplay_ReturnsOriginalOutcome()
    {
        // Arrange
        var paymentHandler = CreateSupplierPaymentHandler();
        var statusHandler = CreateOperationStatusQueryHandler();

        var supplierId = Guid.NewGuid();
        _fakes.Parties.AddSupplier(new Supplier { Id = supplierId, Name = "Payment Supplier", IsActive = true });
        _fakes.SupplierAccounts.AddEntry(new SupplierAccountEntry
        {
            Id = Guid.NewGuid(),
            SupplierId = supplierId,
            Direction = SupplierAccountDirection.IncreasePayable,
            Amount = 2000m,
            EntryType = SupplierAccountEntryType.Purchase,
            ReferenceType = "PURCHASE",
            ReferenceId = Guid.NewGuid(),
            OccurredAt = _fakes.Clock.UtcNow
        });

        var clientOpId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var paymentCommand = new CreateSupplierPaymentCommand(
            ClientOperationId: clientOpId,
            SupplierId: supplierId,
            Amount: 500m,
            Purpose: SupplierPaymentPurpose.Settlement,
            Method: SupplierSettlementMethod.External,
            ExternalReference: "BANK-TRANSFER-500",
            ActorId: actorId,
            Note: "Settlement payment");

        // Act 1: Initial mutation
        var firstResult = await paymentHandler.HandleAsync(paymentCommand, CancellationToken.None);
        Assert.True(firstResult.IsSuccess, firstResult.Error?.Message);
        Assert.NotNull(firstResult.Value);
        Assert.False(firstResult.Value.WasExisting);
        var originalPaymentId = firstResult.Value.PaymentId;
        var originalPaymentNumber = firstResult.Value.PaymentNumber;

        // Act 2: Replay exact mutation
        var replayResult = await paymentHandler.HandleAsync(paymentCommand, CancellationToken.None);
        Assert.True(replayResult.IsSuccess, replayResult.Error?.Message);
        Assert.NotNull(replayResult.Value);
        Assert.True(replayResult.Value.WasExisting, "Replay must indicate existing execution");
        Assert.Equal(originalPaymentId, replayResult.Value.PaymentId);
        Assert.Equal(originalPaymentNumber, replayResult.Value.PaymentNumber);

        // Act 3: Query operation status authority
        var statusResult = await statusHandler.HandleAsync(new OperationStatusQuery(clientOpId), CancellationToken.None);
        Assert.True(statusResult.IsSuccess, statusResult.Error?.Message);
        Assert.NotNull(statusResult.Value);
        Assert.True(statusResult.Value.Found);
        Assert.True(statusResult.Value.WasCommitted);
        Assert.Equal("Succeeded", statusResult.Value.Status);
        Assert.Equal(originalPaymentId, statusResult.Value.EntityId);
        Assert.Equal(originalPaymentNumber, statusResult.Value.DocumentNumber);

        // Invariant: exactly one payment record exists
        Assert.Single(_fakes.SupplierAccounts.Payments.Values, p => p.ClientOperationId == clientOpId);
    }

    [Fact]
    public async Task WarrantyReplay_ReturnsOriginalOutcome()
    {
        // Arrange
        var claimHandler = CreateWarrantyClaimHandler();
        var statusHandler = CreateOperationStatusQueryHandler();

        var supplier = new Supplier { Id = Guid.CreateVersion7(), Name = "Warranty Supplier", DealerCode = "DLR-W01", IsActive = true };
        _fakes.Parties.AddSupplier(supplier);

        var customer = new Customer { Id = Guid.CreateVersion7(), Name = "Warranty Customer", IsActive = true };
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
            PurchaseNumber = "PUR-WARR-01",
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
            SerialNumber = "SN-W24-001",
            TrackingCode = "WTR-000001",
            Status = InventoryUnitStatus.Sold,
            AcquisitionCost = 250000m,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-30)
        };
        _fakes.Inventory.AddInventoryUnit(inventoryUnit);

        var sale = new Sale
        {
            Id = Guid.CreateVersion7(),
            InvoiceNumber = "INV-WARR-001",
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
            InventoryUnitId = inventoryUnit.Id
        };
        _fakes.Sales.AddSaleItemUnit(saleItemUnit);

        var clientOpId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var claimCommand = new CreateWarrantyClaimCommand(
            customer.Id,
            sale.Id,
            supplier.Id,
            actorId,
            [
                new WarrantyClaimItemInput(
                    product.Id,
                    1m,
                    "Screen flickers",
                    saleItem.Id,
                    DateOnly.FromDateTime(DateTime.UtcNow.AddDays(300)),
                    [new WarrantyClaimUnitInput(inventoryUnit.Id, "SN-W24-001")])
            ],
            clientOpId);

        // Act 1: Initial mutation
        var firstResult = await claimHandler.HandleAsync(claimCommand, CancellationToken.None);
        Assert.True(firstResult.IsSuccess, firstResult.Error?.Message);
        var originalClaimId = firstResult.Value;

        // Act 2: Replay exact mutation
        var replayResult = await claimHandler.HandleAsync(claimCommand, CancellationToken.None);
        Assert.True(replayResult.IsSuccess, replayResult.Error?.Message);
        Assert.Equal(originalClaimId, replayResult.Value);

        // Act 3: Query operation status authority
        var statusResult = await statusHandler.HandleAsync(new OperationStatusQuery(clientOpId), CancellationToken.None);
        Assert.True(statusResult.IsSuccess, statusResult.Error?.Message);
        Assert.NotNull(statusResult.Value);
        Assert.True(statusResult.Value.Found);
        Assert.True(statusResult.Value.WasCommitted);
        Assert.Equal("Succeeded", statusResult.Value.Status);
        Assert.Equal(originalClaimId, statusResult.Value.EntityId);

        // Invariant: exactly one claim record exists
        Assert.Single(_fakes.Warranty.Claims.Values, c => c.ClientOperationId == clientOpId);
    }

    [Fact]
    public async Task SameOperationIdDifferentPayload_IsRejected()
    {
        // 1. Sale mutation mismatch
        var saleHandler = CreateSaleHandler();
        var customerA = Guid.NewGuid();
        var customerB = Guid.NewGuid();
        _fakes.Parties.AddCustomer(new Customer { Id = customerA, Name = "Customer A", IsActive = true });
        _fakes.Parties.AddCustomer(new Customer { Id = customerB, Name = "Customer B", IsActive = true });

        var productId = Guid.NewGuid();
        var productUnitId = Guid.NewGuid();
        var baseUnitId = Guid.NewGuid();
        _fakes.Catalog.AddProduct(new Product { Id = productId, Name = "Item", BaseUnitId = baseUnitId, DefaultSalePrice = 10m, IsActive = true });
        _fakes.Catalog.AddProductUnit(new ProductUnit { Id = productUnitId, ProductId = productId, UnitId = baseUnitId, FactorToBaseUnit = 1m, CanSell = true, IsActive = true });
        _fakes.Inventory.Balances[productId] = new StockBalance { Id = Guid.NewGuid(), ProductId = productId, SellableQty = 100m };
        var lot = new InventoryLot { Id = Guid.NewGuid(), ProductId = productId, CreatedAt = _fakes.Clock.UtcNow };
        _fakes.Inventory.Lots.Add(lot);
        _fakes.Inventory.LotBucketBalances.Add(new InventoryLotBucketBalance { Id = Guid.NewGuid(), LotId = lot.Id, StockBucket = InventoryBucket.Sellable, Quantity = 100m });

        var saleOpId = Guid.NewGuid();
        var saleCmd1 = new CompleteSaleCommand(saleOpId, customerA, Guid.NewGuid(), null, 0m, SalePaymentMethod.Bank, 10m, "BANK-01", null, [new(productId, productUnitId, 1m, 10m, [])]);
        var saleRes1 = await saleHandler.HandleAsync(saleCmd1, CancellationToken.None);
        Assert.True(saleRes1.IsSuccess);

        // Same ClientOperationId, different customer
        var saleCmdDiff = new CompleteSaleCommand(saleOpId, customerB, Guid.NewGuid(), null, 0m, SalePaymentMethod.Bank, 10m, "BANK-01", null, [new(productId, productUnitId, 1m, 10m, [])]);
        var saleDiffRes = await saleHandler.HandleAsync(saleCmdDiff, CancellationToken.None);
        Assert.False(saleDiffRes.IsSuccess);
        Assert.NotNull(saleDiffRes.Error);
        Assert.Equal("idempotency.payload_mismatch", saleDiffRes.Error.Code);

        // 2. Query authority fingerprint mismatch
        var statusHandler = CreateOperationStatusQueryHandler();
        var queryOpId = Guid.NewGuid();
        var hashOriginal = OperationPayloadFingerprint.ComputeSha256("original-payload-body");
        var hashModified = OperationPayloadFingerprint.ComputeSha256("modified-payload-body");

        await _fakes.OutcomeLedger.RecordSuccessAsync(
            queryOpId,
            "Sale",
            Guid.NewGuid(),
            "INV-FP-1",
            payloadFingerprint: hashOriginal);

        var queryMismatchRes = await statusHandler.HandleAsync(
            new OperationStatusQuery(queryOpId, PayloadFingerprint: hashModified),
            CancellationToken.None);

        Assert.False(queryMismatchRes.IsSuccess);
        Assert.NotNull(queryMismatchRes.Error);
        Assert.Equal("idempotency.payload_mismatch", queryMismatchRes.Error.Code);
    }

    [Fact]
    public async Task UnauthorizedOperationLookup_IsRejected()
    {
        // Arrange
        var handler = CreateOperationStatusQueryHandler();
        var clientOpId = Guid.NewGuid();
        var ownerActorId = Guid.NewGuid();
        var attackerActorId = Guid.NewGuid();
        var ownerTerminalId = Guid.NewGuid();
        var attackerTerminalId = Guid.NewGuid();

        await _fakes.OutcomeLedger.RecordSuccessAsync(
            clientOpId,
            "Sale",
            Guid.NewGuid(),
            "INV-SECURE-007",
            actorId: ownerActorId,
            terminalId: ownerTerminalId);

        // Act 1: Query from a different actor
        var actorMismatch = await handler.HandleAsync(
            new OperationStatusQuery(clientOpId, ActorId: attackerActorId),
            CancellationToken.None);

        // Assert 1: Rejected with authorization.forbidden
        Assert.False(actorMismatch.IsSuccess);
        Assert.NotNull(actorMismatch.Error);
        Assert.Equal("authorization.forbidden", actorMismatch.Error.Code);

        // Act 2: Query from a different terminal
        var terminalMismatch = await handler.HandleAsync(
            new OperationStatusQuery(clientOpId, TerminalId: attackerTerminalId),
            CancellationToken.None);

        // Assert 2: Rejected with authorization.forbidden
        Assert.False(terminalMismatch.IsSuccess);
        Assert.NotNull(terminalMismatch.Error);
        Assert.Equal("authorization.forbidden", terminalMismatch.Error.Code);

        // Act 3: RequireIdentityScope enabled but query provided no actor
        var identityScopeMissing = await handler.HandleAsync(
            new OperationStatusQuery(clientOpId, RequireIdentityScope: true),
            CancellationToken.None);

        // Assert 3: Rejected
        Assert.False(identityScopeMissing.IsSuccess);
        Assert.NotNull(identityScopeMissing.Error);
        Assert.Equal("authorization.forbidden", identityScopeMissing.Error.Code);

        // Act 4: Authorized query with matching actor and terminal
        var authorizedResult = await handler.HandleAsync(
            new OperationStatusQuery(clientOpId, ActorId: ownerActorId, TerminalId: ownerTerminalId),
            CancellationToken.None);

        // Assert 4: Successfully recovered
        Assert.True(authorizedResult.IsSuccess, authorizedResult.Error?.Message);
        Assert.NotNull(authorizedResult.Value);
        Assert.True(authorizedResult.Value.Found);
        Assert.Equal("INV-SECURE-007", authorizedResult.Value.DocumentNumber);
    }

    private static ServiceProvider CreateDurableServiceProvider(
        string dbName,
        InMemoryDatabaseRoot root,
        Phase2TestDoubles? fakes = null)
    {
        var services = new ServiceCollection();
        var options = new DbContextOptionsBuilder<EdgeRetailsDbContext>()
            .UseInMemoryDatabase(dbName, root)
            .Options;

        services.AddSingleton(options);
        services.AddScoped(sp => new EdgeRetailsDbContext(sp.GetRequiredService<DbContextOptions<EdgeRetailsDbContext>>()));
        services.AddScoped<IOperationOutcomeLedger, EfOperationOutcomeLedger>();

        var testFakes = fakes ?? new Phase2TestDoubles();
        services.AddSingleton<ISalesRepository>(testFakes.Sales);
        services.AddSingleton<IPurchasingRepository>(testFakes.Purchasing);
        services.AddSingleton<ISupplierAccountRepository>(testFakes.SupplierAccounts);
        services.AddSingleton<IInventoryRepository>(testFakes.Inventory);
        services.AddSingleton<IExpenseRepository>(testFakes.Expenses);
        services.AddSingleton<IWarrantyRepository>(testFakes.Warranty);
        services.AddSingleton<IQuotationRepository>(testFakes.Quotations);
        services.AddSingleton<IThakaRepository>(testFakes.Thaka);

        services.AddScoped<OperationStatusQueryHandler>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void ProductionDI_UsesDurableOperationOutcomeLedger()
    {
        var services = new ServiceCollection();
        services.AddEdgeRetailsInfrastructure("Host=127.0.0.1;Port=5432;Database=edgeretails_prod;Username=postgres;Password=postgres");

        var descriptor = services.FirstOrDefault(s => s.ServiceType == typeof(IOperationOutcomeLedger));
        Assert.NotNull(descriptor);
        Assert.Equal(typeof(EfOperationOutcomeLedger), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);

        var inMemoryDescriptor = services.FirstOrDefault(s => s.ImplementationType == typeof(InMemoryOperationOutcomeLedger));
        Assert.Null(inMemoryDescriptor);
    }

    [Fact]
    public async Task OperationOutcome_SurvivesServiceRestart()
    {
        var root = new InMemoryDatabaseRoot();
        var dbName = "restart_test_" + Guid.NewGuid();
        var clientOpId = Guid.NewGuid();
        var entityId = Guid.NewGuid();
        var fingerprint = OperationPayloadFingerprint.ComputeSha256("restart-payload");
        var actorId = Guid.NewGuid();
        var terminalId = Guid.NewGuid();

        // Stage 1: Execute and commit to DbContext in provider 1
        using (var provider1 = CreateDurableServiceProvider(dbName, root))
        {
            using var scope = provider1.CreateScope();
            var ledger = scope.ServiceProvider.GetRequiredService<IOperationOutcomeLedger>();
            await ledger.RecordSuccessAsync(
                clientOpId,
                "Sale",
                entityId,
                "INV-RESTART-001",
                actorId: actorId,
                terminalId: terminalId,
                payloadFingerprint: fingerprint);
        } // provider1 and its DbContext disposed here

        // Stage 2: Create new provider / DbContext and query (simulating service restart)
        using (var provider2 = CreateDurableServiceProvider(dbName, root))
        {
            using var scope = provider2.CreateScope();
            var ledger = scope.ServiceProvider.GetRequiredService<IOperationOutcomeLedger>();
            var outcome = await ledger.GetOutcomeAsync(clientOpId);

            Assert.NotNull(outcome);
            Assert.Equal(clientOpId, outcome.ClientOperationId);
            Assert.Equal(OperationOutcomeState.Succeeded, outcome.State);
            Assert.Equal("Sale", outcome.OperationType);
            Assert.Equal(entityId, outcome.EntityId);
            Assert.Equal("INV-RESTART-001", outcome.DocumentNumber);
            Assert.True(outcome.WasCommitted);
            Assert.Equal(fingerprint, outcome.PayloadFingerprint);
            Assert.Equal(actorId, outcome.ActorId);
            Assert.Equal(terminalId, outcome.TerminalId);
        }
    }

    [Fact]
    public async Task SuccessfulOperation_RecoversAfterRestart()
    {
        var root = new InMemoryDatabaseRoot();
        var dbName = "restart_test_" + Guid.NewGuid();
        var clientOpId = Guid.NewGuid();
        var saleId = Guid.NewGuid();
        var invoice = "INV-SUCCESS-RESTART";
        var fingerprint = OperationPayloadFingerprint.ComputeSha256("success-payload");

        using (var provider1 = CreateDurableServiceProvider(dbName, root))
        {
            using var scope = provider1.CreateScope();
            var ledger = scope.ServiceProvider.GetRequiredService<IOperationOutcomeLedger>();
            await ledger.RecordSuccessAsync(
                clientOpId,
                "Sale",
                saleId,
                invoice,
                payloadFingerprint: fingerprint);
        }

        using (var provider2 = CreateDurableServiceProvider(dbName, root))
        {
            using var scope = provider2.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<OperationStatusQueryHandler>();
            var result = await handler.HandleAsync(
                new OperationStatusQuery(clientOpId, PayloadFingerprint: fingerprint),
                CancellationToken.None);

            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.NotNull(result.Value);
            var status = result.Value;
            Assert.True(status.Found);
            Assert.True(status.WasCommitted);
            Assert.Equal("Succeeded", status.Status);
            Assert.Equal("Sale", status.OperationType);
            Assert.Equal(saleId, status.EntityId);
            Assert.Equal(invoice, status.DocumentNumber);
            Assert.Equal(fingerprint, status.PayloadFingerprint);
        }
    }

    [Fact]
    public async Task PayloadMismatch_RemainsRejectedAfterRestart()
    {
        var root = new InMemoryDatabaseRoot();
        var dbName = "restart_test_" + Guid.NewGuid();
        var clientOpId = Guid.NewGuid();
        var fp1 = OperationPayloadFingerprint.ComputeSha256("payload-version-1");
        var fp2 = OperationPayloadFingerprint.ComputeSha256("payload-version-2");

        using (var provider1 = CreateDurableServiceProvider(dbName, root))
        {
            using var scope = provider1.CreateScope();
            var ledger = scope.ServiceProvider.GetRequiredService<IOperationOutcomeLedger>();
            await ledger.RecordSuccessAsync(
                clientOpId,
                "Sale",
                Guid.NewGuid(),
                "INV-FP-RESTART",
                payloadFingerprint: fp1);
        }

        using (var provider2 = CreateDurableServiceProvider(dbName, root))
        {
            using var scope = provider2.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<OperationStatusQueryHandler>();
            var result = await handler.HandleAsync(
                new OperationStatusQuery(clientOpId, PayloadFingerprint: fp2),
                CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.NotNull(result.Error);
            Assert.Equal("idempotency.payload_mismatch", result.Error.Code);
        }
    }

    [Fact]
    public async Task OutcomeUnknown_SurvivesRestart()
    {
        var root = new InMemoryDatabaseRoot();
        var dbName = "restart_test_" + Guid.NewGuid();
        var clientOpId = Guid.NewGuid();
        var fingerprint = OperationPayloadFingerprint.ComputeSha256("unknown-payload");

        using (var provider1 = CreateDurableServiceProvider(dbName, root))
        {
            using var scope = provider1.CreateScope();
            var ledger = scope.ServiceProvider.GetRequiredService<IOperationOutcomeLedger>();
            await ledger.RecordOutcomeUnknownAsync(
                clientOpId,
                "Sale",
                payloadFingerprint: fingerprint);
        }

        using (var provider2 = CreateDurableServiceProvider(dbName, root))
        {
            using var scope = provider2.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<OperationStatusQueryHandler>();
            var result = await handler.HandleAsync(
                new OperationStatusQuery(clientOpId),
                CancellationToken.None);

            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.NotNull(result.Value);
            var status = result.Value;
            Assert.True(status.Found);
            Assert.False(status.WasCommitted);
            Assert.Equal("OutcomeUnknown", status.Status);
            Assert.Equal("Sale", status.OperationType);
        }
    }

    [Fact]
    public async Task PendingOutcome_SurvivesRestart()
    {
        var root = new InMemoryDatabaseRoot();
        var dbName = "restart_test_" + Guid.NewGuid();
        var clientOpId = Guid.NewGuid();
        var fingerprint = OperationPayloadFingerprint.ComputeSha256("pending-payload");

        using (var provider1 = CreateDurableServiceProvider(dbName, root))
        {
            using var scope = provider1.CreateScope();
            var ledger = scope.ServiceProvider.GetRequiredService<IOperationOutcomeLedger>();
            await ledger.RecordPendingAsync(
                clientOpId,
                "Purchase",
                payloadFingerprint: fingerprint);
        }

        using (var provider2 = CreateDurableServiceProvider(dbName, root))
        {
            using var scope = provider2.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<OperationStatusQueryHandler>();
            var result = await handler.HandleAsync(
                new OperationStatusQuery(clientOpId),
                CancellationToken.None);

            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.NotNull(result.Value);
            var status = result.Value;
            Assert.True(status.Found);
            Assert.False(status.WasCommitted);
            Assert.Equal("Pending", status.Status);
            Assert.Equal("Purchase", status.OperationType);
        }
    }

    [Fact]
    public async Task FailedOutcome_SurvivesRestart()
    {
        var root = new InMemoryDatabaseRoot();
        var dbName = "restart_test_" + Guid.NewGuid();
        var clientOpId = Guid.NewGuid();
        var fingerprint = OperationPayloadFingerprint.ComputeSha256("failed-payload");

        using (var provider1 = CreateDurableServiceProvider(dbName, root))
        {
            using var scope = provider1.CreateScope();
            var ledger = scope.ServiceProvider.GetRequiredService<IOperationOutcomeLedger>();
            await ledger.RecordFailureAsync(
                clientOpId,
                "SupplierPayment",
                "payment.insufficient_funds",
                "Account balance is insufficient.",
                payloadFingerprint: fingerprint);
        }

        using (var provider2 = CreateDurableServiceProvider(dbName, root))
        {
            using var scope = provider2.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<OperationStatusQueryHandler>();
            var result = await handler.HandleAsync(
                new OperationStatusQuery(clientOpId),
                CancellationToken.None);

            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.NotNull(result.Value);
            var status = result.Value;
            Assert.True(status.Found);
            Assert.False(status.WasCommitted);
            Assert.Equal("Failed", status.Status);
            Assert.Equal("payment.insufficient_funds", status.ErrorCode);
            Assert.Equal("Account balance is insufficient.", status.ErrorMessage);
        }
    }

    [Fact]
    public async Task ConcurrentSameOperationId_CreatesSingleOutcome()
    {
        var root = new InMemoryDatabaseRoot();
        var dbName = "concurrent_test_" + Guid.NewGuid();
        var clientOpId = Guid.NewGuid();
        var entityId = Guid.NewGuid();

        using var provider = CreateDurableServiceProvider(dbName, root);

        var tasks = Enumerable.Range(0, 10).Select(async i =>
        {
            using var scope = provider.CreateScope();
            var ledger = scope.ServiceProvider.GetRequiredService<IOperationOutcomeLedger>();
            if (i % 2 == 0)
            {
                await ledger.RecordPendingAsync(clientOpId, "Sale");
            }
            else
            {
                await ledger.RecordSuccessAsync(clientOpId, "Sale", entityId, $"INV-{i}");
            }
        });

        await Task.WhenAll(tasks);

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var count = await db.OperationOutcomes.CountAsync(x => x.ClientOperationId == clientOpId);
            Assert.Equal(1, count);
        }
    }

    [Fact]
    public async Task SucceededOutcome_IsNotOverwrittenByLateFailureOrPendingWrite()
    {
        var root = new InMemoryDatabaseRoot();
        var dbName = "terminal_success_test_" + Guid.NewGuid();
        var clientOpId = Guid.NewGuid();
        var entityId = Guid.NewGuid();

        using var provider = CreateDurableServiceProvider(dbName, root);
        using var scope = provider.CreateScope();
        var ledger = scope.ServiceProvider.GetRequiredService<IOperationOutcomeLedger>();

        await ledger.RecordSuccessAsync(clientOpId, "Sale", entityId, "INV-TERMINAL");
        await ledger.RecordFailureAsync(clientOpId, "Sale", "sale.failed", "A stale request failed later.");
        await ledger.RecordPendingAsync(clientOpId, "Sale");
        await ledger.RecordOutcomeUnknownAsync(clientOpId, "Sale");

        var outcome = await ledger.GetOutcomeAsync(clientOpId);
        Assert.NotNull(outcome);
        Assert.Equal(OperationOutcomeState.Succeeded, outcome.State);
        Assert.True(outcome.WasCommitted);
        Assert.Equal(entityId, outcome.EntityId);
        Assert.Equal("INV-TERMINAL", outcome.DocumentNumber);
    }

    [Fact]
    public async Task ApiScopedStatus_DoesNotBackfillUnownedLegacyRows()
    {
        var root = new InMemoryDatabaseRoot();
        var dbName = "scoped_legacy_test_" + Guid.NewGuid();
        var clientOpId = Guid.NewGuid();
        var fakes = new Phase2TestDoubles();
        fakes.Sales.AddSale(new Sale
        {
            Id = Guid.NewGuid(),
            ClientOperationId = clientOpId,
            InvoiceNumber = "INV-UNSCOPED-LEGACY",
            Status = SaleStatus.Completed,
            PaymentStatus = SalePaymentStatus.Paid,
            GrandTotal = 100m,
            CompletedAt = DateTimeOffset.UtcNow
        });

        using var provider = CreateDurableServiceProvider(dbName, root, fakes);
        using var scope = provider.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<OperationStatusQueryHandler>();
        var actorId = Guid.NewGuid();
        var terminalId = Guid.NewGuid();

        var result = await handler.HandleAsync(
            new OperationStatusQuery(
                clientOpId,
                ActorId: actorId,
                TerminalId: terminalId,
                RequireIdentityScope: true,
                RequireCanonicalOutcome: true),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.False(result.Value.Found);
        Assert.False(result.Value.WasCommitted);
        Assert.Equal("OutcomeUnknown", result.Value.Status);
        Assert.Null(await scope.ServiceProvider.GetRequiredService<IOperationOutcomeLedger>().GetOutcomeAsync(clientOpId));
    }

    [Fact]
    public async Task LegacyCommittedOperation_BackfillsCanonicalOutcomeOnce()
    {
        var root = new InMemoryDatabaseRoot();
        var dbName = "backfill_test_" + Guid.NewGuid();
        var clientOpId = Guid.NewGuid();
        var fakes = new Phase2TestDoubles();

        var existingSale = new Sale
        {
            Id = Guid.NewGuid(),
            ClientOperationId = clientOpId,
            InvoiceNumber = "INV-BACKFILL-001",
            Status = SaleStatus.Completed,
            PaymentStatus = SalePaymentStatus.Paid,
            Subtotal = 1200m,
            GrandTotal = 1200m,
            CompletedAt = DateTimeOffset.UtcNow
        };
        fakes.Sales.AddSale(existingSale);

        using var provider = CreateDurableServiceProvider(dbName, root, fakes);

        // Verify ledger starts empty
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var initialCount = await db.OperationOutcomes.CountAsync(x => x.ClientOperationId == clientOpId);
            Assert.Equal(0, initialCount);
        }

        // Query status: this triggers legacy lookup and backfill
        using (var scope = provider.CreateScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<OperationStatusQueryHandler>();
            var result = await handler.HandleAsync(
                new OperationStatusQuery(clientOpId, PayloadFingerprint: "hash-legacy-123"),
                CancellationToken.None);

            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.NotNull(result.Value);
            Assert.True(result.Value.Found);
            Assert.Equal("Sale", result.Value.OperationType);
            Assert.Equal(existingSale.Id, result.Value.EntityId);
            Assert.Equal("INV-BACKFILL-001", result.Value.DocumentNumber);
        }

        // Verify canonical outcome is now permanently backfilled exactly once
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var outcomes = await db.OperationOutcomes.Where(x => x.ClientOperationId == clientOpId).ToListAsync();
            Assert.Single(outcomes);
            var canonical = outcomes[0];
            Assert.Equal(OperationOutcomeStatus.Succeeded, canonical.Status);
            Assert.Equal(existingSale.Id, canonical.ResultEntityId);
            Assert.Equal("INV-BACKFILL-001", canonical.DocumentNumber);
            Assert.True(canonical.WasCommitted);
            Assert.Equal("hash-legacy-123", canonical.PayloadFingerprint);
        }
    }

    [Fact]
    public async Task OperationStatus_DoesNotRequirePermanentLegacyTableScan()
    {
        var root = new InMemoryDatabaseRoot();
        var dbName = "legacy_scan_test_" + Guid.NewGuid();
        var clientOpId = Guid.NewGuid();
        var fakes = new Phase2TestDoubles();

        var sale = new Sale
        {
            Id = Guid.NewGuid(),
            ClientOperationId = clientOpId,
            InvoiceNumber = "INV-SCAN-001",
            Status = SaleStatus.Completed,
            PaymentStatus = SalePaymentStatus.Paid,
            Subtotal = 500m,
            GrandTotal = 500m,
            CompletedAt = DateTimeOffset.UtcNow
        };
        fakes.Sales.AddSale(sale);

        using var provider = CreateDurableServiceProvider(dbName, root, fakes);

        // Step 1: Initial query finds legacy and backfills into canonical ledger
        using (var scope = provider.CreateScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<OperationStatusQueryHandler>();
            var res1 = await handler.HandleAsync(new OperationStatusQuery(clientOpId), CancellationToken.None);
            Assert.True(res1.IsSuccess);
            Assert.NotNull(res1.Value);
            Assert.True(res1.Value.Found);
        }

        // Step 2: Clear legacy sales table completely (simulating archival, purge, or unavailability)
        fakes.Sales.Sales.Clear();

        // Step 3: Subsequent query must succeed from canonical authority without scanning legacy table
        using (var scope = provider.CreateScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<OperationStatusQueryHandler>();
            var res2 = await handler.HandleAsync(new OperationStatusQuery(clientOpId), CancellationToken.None);

            Assert.True(res2.IsSuccess, res2.Error?.Message);
            Assert.NotNull(res2.Value);
            Assert.True(res2.Value.Found, "Canonical outcome must be returned even when legacy table is empty");
            Assert.Equal(sale.Id, res2.Value.EntityId);
            Assert.Equal("INV-SCAN-001", res2.Value.DocumentNumber);
            Assert.Equal("Sale", res2.Value.OperationType);
        }
    }
}
