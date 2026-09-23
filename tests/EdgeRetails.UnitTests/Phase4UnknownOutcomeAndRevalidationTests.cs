using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Application.Gateways;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.SystemConfiguration;
using Xunit;

namespace EdgeRetails.UnitTests;

internal sealed class FakeTerminalRepository : ITerminalRepository
{
    public Dictionary<Guid, Terminal> Terminals { get; } = new();

    public Task<Terminal?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Terminals.TryGetValue(id, out var t) ? t : null);

    public Task<Terminal?> GetByCodeAsync(string terminalCode, CancellationToken cancellationToken = default) =>
        Task.FromResult(Terminals.Values.FirstOrDefault(t => string.Equals(t.TerminalCode, terminalCode, StringComparison.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<Terminal>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Terminal>>(Terminals.Values.ToList());

    public Task<int> GetActiveCountAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Terminals.Values.Count(t => t.Status == TerminalStatus.Active));

    public Task AddAsync(Terminal terminal, CancellationToken cancellationToken = default)
    {
        Terminals[terminal.Id] = terminal;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Terminal terminal, CancellationToken cancellationToken = default)
    {
        Terminals[terminal.Id] = terminal;
        return Task.CompletedTask;
    }
}

public sealed class Phase4UnknownOutcomeAndRevalidationTests
{
    private readonly Phase2TestDoubles _fakes = new();
    private readonly FakeTerminalRepository _terminals = new();

    private OperationStatusQueryHandler CreateOperationStatusQueryHandler() =>
        new(_fakes.Sales, _fakes.Purchasing, _fakes.SupplierAccounts);

    private AuthoritativeRevalidationHandler CreateAuthoritativeRevalidationHandler() =>
        new(
            _terminals,
            _fakes.Catalog,
            _fakes.Inventory,
            _fakes.Cash,
            _fakes.Parties,
            _fakes.SupplierAccounts,
            _fakes.Clock);

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

    // =========================================================================
    // Scope 2: OperationStatusQueryHandler Tests
    // =========================================================================

    [Fact]
    public async Task OperationStatusQueryHandler_FindsCommittedSale_ByClientOperationId()
    {
        // Arrange
        var handler = CreateOperationStatusQueryHandler();
        var clientOperationId = Guid.NewGuid();
        var saleId = Guid.NewGuid();
        var invoiceNumber = "INV-20260923-0001";

        var sale = new Sale
        {
            Id = saleId,
            InvoiceNumber = invoiceNumber,
            ClientOperationId = clientOperationId,
            GrandTotal = 450.00m,
            CompletedAt = _fakes.Clock.UtcNow
        };
        _fakes.Sales.AddSale(sale);

        // Act
        var result = await handler.HandleAsync(new OperationStatusQuery(clientOperationId), CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        var val = Assert.IsType<OperationStatusResult>(result.Value);
        Assert.True(val.Found);
        Assert.Equal(clientOperationId, val.ClientOperationId);
        Assert.Equal("Sale", val.OperationType);
        Assert.Equal(saleId, val.EntityId);
        Assert.Equal(invoiceNumber, val.DocumentNumber);
        Assert.True(val.WasCommitted);
    }

    [Fact]
    public async Task OperationStatusQueryHandler_FindsCommittedPurchase_ByClientOperationId()
    {
        // Arrange
        var handler = CreateOperationStatusQueryHandler();
        var clientOperationId = Guid.NewGuid();
        var purchaseId = Guid.NewGuid();
        var purchaseNumber = "PUR-20260923-0005";

        var purchase = new Purchase
        {
            Id = purchaseId,
            PurchaseNumber = purchaseNumber,
            ClientOperationId = clientOperationId,
            SupplierId = Guid.NewGuid(),
            SupplierInvoiceNumber = "SUPP-INV-99",
            GrandTotal = 1200.00m,
            CreatedAt = _fakes.Clock.UtcNow
        };
        _fakes.Purchasing.AddPurchase(purchase);

        // Act
        var result = await handler.HandleAsync(new OperationStatusQuery(clientOperationId), CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        var val = Assert.IsType<OperationStatusResult>(result.Value);
        Assert.True(val.Found);
        Assert.Equal(clientOperationId, val.ClientOperationId);
        Assert.Equal("Purchase", val.OperationType);
        Assert.Equal(purchaseId, val.EntityId);
        Assert.Equal(purchaseNumber, val.DocumentNumber);
        Assert.True(val.WasCommitted);
    }

    [Fact]
    public async Task OperationStatusQueryHandler_FindsCommittedSupplierPayment_ByClientOperationId()
    {
        // Arrange
        var handler = CreateOperationStatusQueryHandler();
        var clientOperationId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var paymentNumber = "PAY-20260923-0012";

        var payment = new SupplierPayment
        {
            Id = paymentId,
            PaymentNumber = paymentNumber,
            ClientOperationId = clientOperationId,
            SupplierId = Guid.NewGuid(),
            Amount = 750.00m,
            Purpose = SupplierPaymentPurpose.Settlement,
            Method = SupplierSettlementMethod.External,
            PaidAt = _fakes.Clock.UtcNow
        };
        _fakes.SupplierAccounts.AddPayment(payment);

        // Act
        var result = await handler.HandleAsync(new OperationStatusQuery(clientOperationId), CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        var val = Assert.IsType<OperationStatusResult>(result.Value);
        Assert.True(val.Found);
        Assert.Equal(clientOperationId, val.ClientOperationId);
        Assert.Equal("SupplierPayment", val.OperationType);
        Assert.Equal(paymentId, val.EntityId);
        Assert.Equal(paymentNumber, val.DocumentNumber);
        Assert.True(val.WasCommitted);
    }

    [Fact]
    public async Task OperationStatusQueryHandler_ReturnsFoundFalse_ForUnknownClientOperationId()
    {
        // Arrange
        var handler = CreateOperationStatusQueryHandler();
        var unknownOperationId = Guid.NewGuid();

        // Act
        var result = await handler.HandleAsync(new OperationStatusQuery(unknownOperationId), CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        var val = Assert.IsType<OperationStatusResult>(result.Value);
        Assert.Equal(unknownOperationId, val.ClientOperationId);
        Assert.False(val.Found);
        Assert.Null(val.OperationType);
        Assert.Null(val.EntityId);
        Assert.Null(val.DocumentNumber);
        Assert.False(val.WasCommitted);
    }

    [Fact]
    public async Task OperationStatusQueryHandler_RejectsGuidEmpty_WithValidationError()
    {
        // Arrange
        var handler = CreateOperationStatusQueryHandler();

        // Act
        var result = await handler.HandleAsync(new OperationStatusQuery(Guid.Empty), CancellationToken.None);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Equal("validation.client_operation_id_required", result.Error!.Code);
        Assert.Contains("ClientOperationId is required", result.Error.Message);
    }

    [Fact]
    public async Task OperationStatusQueryHandler_FindsCommittedSaleReturn_ByClientOperationId()
    {
        // Arrange
        var handler = CreateOperationStatusQueryHandler();
        var clientOperationId = Guid.NewGuid();
        var returnId = Guid.NewGuid();
        var returnNumber = "RET-20260923-0002";

        var saleReturn = new SaleReturn
        {
            Id = returnId,
            ReturnNumber = returnNumber,
            ClientOperationId = clientOperationId,
            SaleId = Guid.NewGuid(),
            RefundAmount = 150m,
            CreatedAt = _fakes.Clock.UtcNow
        };
        _fakes.Sales.AddReturn(saleReturn);

        // Act
        var result = await handler.HandleAsync(new OperationStatusQuery(clientOperationId), CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        var val = Assert.IsType<OperationStatusResult>(result.Value);
        Assert.True(val.Found);
        Assert.Equal("SaleReturn", val.OperationType);
        Assert.Equal(returnId, val.EntityId);
        Assert.Equal(returnNumber, val.DocumentNumber);
        Assert.True(val.WasCommitted);
    }

    [Fact]
    public async Task OperationStatusQueryHandler_FindsCommittedPurchaseReturn_ByClientOperationId()
    {
        // Arrange
        var handler = CreateOperationStatusQueryHandler();
        var clientOperationId = Guid.NewGuid();
        var returnId = Guid.NewGuid();
        var returnNumber = "PRET-20260923-0003";

        var purchaseReturn = new PurchaseReturn
        {
            Id = returnId,
            ReturnNumber = returnNumber,
            ClientOperationId = clientOperationId,
            PurchaseId = Guid.NewGuid(),
            Reason = "Defective shipment",
            SupplierReturnValue = 300m,
            CreatedAt = _fakes.Clock.UtcNow
        };
        _fakes.Purchasing.AddReturn(purchaseReturn);

        // Act
        var result = await handler.HandleAsync(new OperationStatusQuery(clientOperationId), CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        var val = Assert.IsType<OperationStatusResult>(result.Value);
        Assert.True(val.Found);
        Assert.Equal("PurchaseReturn", val.OperationType);
        Assert.Equal(returnId, val.EntityId);
        Assert.Equal(returnNumber, val.DocumentNumber);
        Assert.True(val.WasCommitted);
    }

    [Fact]
    public async Task OperationStatusQueryHandler_FindsCommittedPurchaseVoid_ByClientOperationId()
    {
        // Arrange
        var handler = CreateOperationStatusQueryHandler();
        var clientOperationId = Guid.NewGuid();
        var voidId = Guid.NewGuid();

        var purchaseVoid = new PurchaseVoid
        {
            Id = voidId,
            PurchaseId = Guid.NewGuid(),
            ClientOperationId = clientOperationId,
            Reason = "Entered by mistake",
            VoidedBy = Guid.NewGuid(),
            VoidedAt = _fakes.Clock.UtcNow
        };
        _fakes.Purchasing.AddVoid(purchaseVoid);

        // Act
        var result = await handler.HandleAsync(new OperationStatusQuery(clientOperationId), CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        var val = Assert.IsType<OperationStatusResult>(result.Value);
        Assert.True(val.Found);
        Assert.Equal("PurchaseVoid", val.OperationType);
        Assert.Equal(voidId, val.EntityId);
        Assert.Equal(voidId.ToString("D"), val.DocumentNumber);
        Assert.True(val.WasCommitted);
    }

    [Fact]
    public async Task OperationStatusQueryHandler_FindsCommittedSupplierRefund_ByClientOperationId()
    {
        // Arrange
        var handler = CreateOperationStatusQueryHandler();
        var clientOperationId = Guid.NewGuid();
        var refundId = Guid.NewGuid();
        var refundNumber = "REF-20260923-0004";

        var refund = new SupplierRefund
        {
            Id = refundId,
            RefundNumber = refundNumber,
            ClientOperationId = clientOperationId,
            SupplierId = Guid.NewGuid(),
            Amount = 200m,
            Method = SupplierSettlementMethod.External,
            ReceivedAt = _fakes.Clock.UtcNow
        };
        _fakes.SupplierAccounts.AddRefund(refund);

        // Act
        var result = await handler.HandleAsync(new OperationStatusQuery(clientOperationId), CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        var val = Assert.IsType<OperationStatusResult>(result.Value);
        Assert.True(val.Found);
        Assert.Equal("SupplierRefund", val.OperationType);
        Assert.Equal(refundId, val.EntityId);
        Assert.Equal(refundNumber, val.DocumentNumber);
        Assert.True(val.WasCommitted);
    }

    // =========================================================================
    // Scope 3: AuthoritativeRevalidationHandler Tests
    // =========================================================================

    [Fact]
    public async Task AuthoritativeRevalidationHandler_ReturnsCorrectCashSession_WhenOpen()
    {
        // Arrange
        var handler = CreateAuthoritativeRevalidationHandler();
        var terminal = new Terminal
        {
            Id = Guid.NewGuid(),
            TerminalCode = "TERM-01",
            Name = "Register 1",
            Status = TerminalStatus.Active
        };
        _terminals.Terminals[terminal.Id] = terminal;

        var openSessionId = Guid.NewGuid();
        var session = new CashSession
        {
            Id = openSessionId,
            Status = CashSessionStatus.Open,
            BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow),
            OpenedBy = Guid.NewGuid(),
            OpenedAt = _fakes.Clock.UtcNow,
            OpeningCash = 500m
        };
        _fakes.Cash.AddSession(session);

        // Act
        var result = await handler.HandleAsync(new AuthoritativeRevalidationQuery(terminal.Id), CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        var val = Assert.IsType<AuthoritativeRevalidationResult>(result.Value);
        Assert.True(val.CashSessionIsOpen);
        Assert.Equal(openSessionId, val.ActiveCashSessionId);
    }

    [Fact]
    public async Task AuthoritativeRevalidationHandler_ReturnsCorrectCashSession_WhenClosedOrNone()
    {
        // Arrange
        var handler = CreateAuthoritativeRevalidationHandler();
        var terminal = new Terminal
        {
            Id = Guid.NewGuid(),
            TerminalCode = "TERM-01",
            Name = "Register 1",
            Status = TerminalStatus.Active
        };
        _terminals.Terminals[terminal.Id] = terminal;

        // No open cash session (session is closed)
        var closedSession = new CashSession
        {
            Id = Guid.NewGuid(),
            Status = CashSessionStatus.Closed,
            BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow),
            OpenedBy = Guid.NewGuid(),
            OpenedAt = _fakes.Clock.UtcNow,
            OpeningCash = 500m
        };
        _fakes.Cash.AddSession(closedSession);

        // Act
        var result = await handler.HandleAsync(new AuthoritativeRevalidationQuery(terminal.Id), CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        var val = Assert.IsType<AuthoritativeRevalidationResult>(result.Value);
        Assert.False(val.CashSessionIsOpen);
        Assert.Null(val.ActiveCashSessionId);
    }

    [Fact]
    public async Task AuthoritativeRevalidationHandler_IdentifiesProductsLocked_ByActiveCountingStocktake()
    {
        // Arrange
        var handler = CreateAuthoritativeRevalidationHandler();
        var terminal = new Terminal
        {
            Id = Guid.NewGuid(),
            TerminalCode = "TERM-01",
            Name = "Register 1",
            Status = TerminalStatus.Active
        };
        _terminals.Terminals[terminal.Id] = terminal;

        var stocktakeId = Guid.NewGuid();
        var lockedProduct1 = Guid.NewGuid();
        var lockedProduct2 = Guid.NewGuid();

        var stocktake = new Stocktake
        {
            Id = stocktakeId,
            Status = StocktakeStatus.Counting,
            Scope = StocktakeScope.FullShop,
            CreatedBy = Guid.NewGuid(),
            CreatedAt = _fakes.Clock.UtcNow
        };
        _fakes.Inventory.AddStocktake(stocktake);

        _fakes.Inventory.AddStocktakeItem(new StocktakeItem
        {
            Id = Guid.NewGuid(),
            StocktakeId = stocktakeId,
            ProductId = lockedProduct1,
            ExpectedSellableQty = 10m
        });
        _fakes.Inventory.AddStocktakeItem(new StocktakeItem
        {
            Id = Guid.NewGuid(),
            StocktakeId = stocktakeId,
            ProductId = lockedProduct2,
            ExpectedSellableQty = 25m
        });

        // Act
        var result = await handler.HandleAsync(new AuthoritativeRevalidationQuery(terminal.Id), CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        var val = Assert.IsType<AuthoritativeRevalidationResult>(result.Value);
        Assert.NotNull(val.ActiveStocktakeLockedProductIds);
        Assert.Equal(2, val.ActiveStocktakeLockedProductIds.Count);
        Assert.Contains(lockedProduct1, val.ActiveStocktakeLockedProductIds);
        Assert.Contains(lockedProduct2, val.ActiveStocktakeLockedProductIds);
    }

    [Fact]
    public async Task AuthoritativeRevalidationHandler_ReturnsEmptyLockList_WhenNoStocktakeIsActive()
    {
        // Arrange
        var handler = CreateAuthoritativeRevalidationHandler();
        var terminal = new Terminal
        {
            Id = Guid.NewGuid(),
            TerminalCode = "TERM-01",
            Name = "Register 1",
            Status = TerminalStatus.Active
        };
        _terminals.Terminals[terminal.Id] = terminal;

        // Stocktake is in Review (not Counting) - should not lock active operations
        var reviewStocktake = new Stocktake
        {
            Id = Guid.NewGuid(),
            Status = StocktakeStatus.Review,
            Scope = StocktakeScope.FullShop,
            CreatedBy = Guid.NewGuid(),
            CreatedAt = _fakes.Clock.UtcNow
        };
        _fakes.Inventory.AddStocktake(reviewStocktake);

        _fakes.Inventory.AddStocktakeItem(new StocktakeItem
        {
            Id = Guid.NewGuid(),
            StocktakeId = reviewStocktake.Id,
            ProductId = Guid.NewGuid(),
            ExpectedSellableQty = 5m
        });

        // Act
        var result = await handler.HandleAsync(new AuthoritativeRevalidationQuery(terminal.Id), CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        var val = Assert.IsType<AuthoritativeRevalidationResult>(result.Value);
        Assert.Empty(val.ActiveStocktakeLockedProductIds);
    }

    [Fact]
    public async Task AuthoritativeRevalidationHandler_CorrectlyPopulatesMonitoredProductStates_PricesAndStock()
    {
        // Arrange
        var handler = CreateAuthoritativeRevalidationHandler();
        var terminal = new Terminal
        {
            Id = Guid.NewGuid(),
            TerminalCode = "TERM-01",
            Name = "Register 1",
            Status = TerminalStatus.Active
        };
        _terminals.Terminals[terminal.Id] = terminal;

        var product1Id = Guid.NewGuid();
        var product2Id = Guid.NewGuid();

        var product1 = new Product
        {
            Id = product1Id,
            Name = "iPhone 15 Pro",
            Sku = "IPH15-PRO-256",
            DefaultSalePrice = 1299.99m,
            IsActive = true,
            Version = 5
        };
        var product2 = new Product
        {
            Id = product2Id,
            Name = "Samsung Galaxy S24",
            Sku = "SGS24-128",
            DefaultSalePrice = 899.50m,
            IsActive = true,
            Version = 2
        };

        _fakes.Catalog.AddProduct(product1);
        _fakes.Catalog.AddProduct(product2);

        // Product 1 has stock balance; Product 2 has no stock balance (should default to 0m)
        _fakes.Inventory.Balances[product1Id] = new StockBalance
        {
            Id = Guid.NewGuid(),
            ProductId = product1Id,
            SellableQty = 18m
        };

        var query = new AuthoritativeRevalidationQuery(
            TerminalId: terminal.Id,
            MonitoredProductIds: [product1Id, product2Id]);

        // Act
        var result = await handler.HandleAsync(query, CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        var val = Assert.IsType<AuthoritativeRevalidationResult>(result.Value);
        var productStates = val.ProductStates;
        Assert.Equal(2, productStates.Count);

        var state1 = productStates.Single(p => p.ProductId == product1Id);
        Assert.Equal("iPhone 15 Pro", state1.Name);
        Assert.Equal("IPH15-PRO-256", state1.Sku);
        Assert.True(state1.IsActive);
        Assert.Equal(1299.99m, state1.BasePrice);
        Assert.Equal(18m, state1.SellableStock);
        Assert.Equal(5, state1.Version);

        var state2 = productStates.Single(p => p.ProductId == product2Id);
        Assert.Equal("Samsung Galaxy S24", state2.Name);
        Assert.Equal("SGS24-128", state2.Sku);
        Assert.True(state2.IsActive);
        Assert.Equal(899.50m, state2.BasePrice);
        Assert.Equal(0m, state2.SellableStock);
        Assert.Equal(2, state2.Version);
    }

    [Fact]
    public async Task AuthoritativeRevalidationHandler_CorrectlyPopulatesSupplierBalances()
    {
        // Arrange
        var handler = CreateAuthoritativeRevalidationHandler();
        var terminal = new Terminal
        {
            Id = Guid.NewGuid(),
            TerminalCode = "TERM-01",
            Name = "Register 1",
            Status = TerminalStatus.Active
        };
        _terminals.Terminals[terminal.Id] = terminal;

        var supplier1 = new Supplier
        {
            Id = Guid.NewGuid(),
            Name = "Alpha Electronics",
            IsActive = true
        };
        var supplier2 = new Supplier
        {
            Id = Guid.NewGuid(),
            Name = "Beta Distributors",
            IsActive = true
        };
        _fakes.Parties.AddSupplier(supplier1);
        _fakes.Parties.AddSupplier(supplier2);

        // Supplier 1: Payable 5000 - 1500 = 3500
        _fakes.SupplierAccounts.AddEntry(new SupplierAccountEntry
        {
            Id = Guid.NewGuid(),
            SupplierId = supplier1.Id,
            Direction = SupplierAccountDirection.IncreasePayable,
            Amount = 5000.00m,
            EntryType = SupplierAccountEntryType.Purchase
        });
        _fakes.SupplierAccounts.AddEntry(new SupplierAccountEntry
        {
            Id = Guid.NewGuid(),
            SupplierId = supplier1.Id,
            Direction = SupplierAccountDirection.DecreasePayable,
            Amount = 1500.00m,
            EntryType = SupplierAccountEntryType.SupplierPayment
        });

        // Supplier 2: Payable 2200
        _fakes.SupplierAccounts.AddEntry(new SupplierAccountEntry
        {
            Id = Guid.NewGuid(),
            SupplierId = supplier2.Id,
            Direction = SupplierAccountDirection.IncreasePayable,
            Amount = 2200.00m,
            EntryType = SupplierAccountEntryType.Purchase
        });

        // Act
        var result = await handler.HandleAsync(new AuthoritativeRevalidationQuery(terminal.Id), CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        var val = Assert.IsType<AuthoritativeRevalidationResult>(result.Value);
        var balances = val.SupplierBalances;
        Assert.Equal(2, balances.Count);

        var bal1 = balances.Single(s => s.SupplierId == supplier1.Id);
        Assert.Equal("Alpha Electronics", bal1.SupplierName);
        Assert.Equal(3500.00m, bal1.CurrentPayableBalance);

        var bal2 = balances.Single(s => s.SupplierId == supplier2.Id);
        Assert.Equal("Beta Distributors", bal2.SupplierName);
        Assert.Equal(2200.00m, bal2.CurrentPayableBalance);
    }

    [Fact]
    public async Task AuthoritativeRevalidationHandler_RejectsRevokedTerminals_WithTerminalRevoked()
    {
        // Arrange
        var handler = CreateAuthoritativeRevalidationHandler();
        var terminal = new Terminal
        {
            Id = Guid.NewGuid(),
            TerminalCode = "REVOKED-01",
            Name = "Decommissioned Terminal",
            Status = TerminalStatus.Revoked
        };
        _terminals.Terminals[terminal.Id] = terminal;

        // Act
        var result = await handler.HandleAsync(new AuthoritativeRevalidationQuery(terminal.Id), CancellationToken.None);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Equal("terminal.revoked", result.Error!.Code);
        Assert.Contains("Terminal has been revoked", result.Error.Message);
    }

    [Fact]
    public async Task AuthoritativeRevalidationHandler_RejectsUnknownTerminal_WithTerminalNotFound()
    {
        // Arrange
        var handler = CreateAuthoritativeRevalidationHandler();
        var unknownTerminalId = Guid.NewGuid();

        // Act
        var result = await handler.HandleAsync(new AuthoritativeRevalidationQuery(unknownTerminalId), CancellationToken.None);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Equal("terminal.not_found", result.Error!.Code);
        Assert.Contains("does not exist on the server", result.Error.Message);
    }

    [Fact]
    public async Task AuthoritativeRevalidationHandler_ReturnsServerUtcTime_AndCurrentProtocolVersion()
    {
        // Arrange
        var handler = CreateAuthoritativeRevalidationHandler();
        var expectedTime = new DateTimeOffset(2026, 9, 23, 14, 30, 0, TimeSpan.Zero);
        _fakes.Clock.UtcNow = expectedTime;

        var terminal = new Terminal
        {
            Id = Guid.NewGuid(),
            TerminalCode = "TERM-SYNC",
            Name = "Sync Terminal",
            Status = TerminalStatus.Active
        };
        _terminals.Terminals[terminal.Id] = terminal;

        // Act
        var result = await handler.HandleAsync(new AuthoritativeRevalidationQuery(terminal.Id), CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        var val = Assert.IsType<AuthoritativeRevalidationResult>(result.Value);
        Assert.Equal(expectedTime, val.ServerTimeUtc);
        Assert.Equal(TerminalProtocol.CurrentProtocolVersion, val.ServerProtocolVersion);
        Assert.Equal(TerminalStatus.Active, val.TerminalStatus);
    }

    [Fact]
    public async Task AuthoritativeRevalidationHandler_PopulatesSampleCatalogProducts_WhenMonitoredProductIdsNotProvided()
    {
        // Arrange
        var handler = CreateAuthoritativeRevalidationHandler();
        var terminal = new Terminal
        {
            Id = Guid.NewGuid(),
            TerminalCode = "TERM-ALL",
            Name = "Register All",
            Status = TerminalStatus.Active
        };
        _terminals.Terminals[terminal.Id] = terminal;

        var product = new Product
        {
            Id = Guid.NewGuid(),
            Name = "Generic Product",
            Sku = "GEN-01",
            DefaultSalePrice = 50m,
            IsActive = true,
            Version = 1
        };
        _fakes.Catalog.AddProduct(product);

        // Act (MonitoredProductIds = null)
        var result = await handler.HandleAsync(new AuthoritativeRevalidationQuery(terminal.Id), CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        var val = Assert.IsType<AuthoritativeRevalidationResult>(result.Value);
        Assert.Single(val.ProductStates);
        Assert.Equal(product.Id, val.ProductStates[0].ProductId);
    }

    // =========================================================================
    // Scope 1: Unknown-Outcome Replay Invariant Tests
    // =========================================================================

    [Fact]
    public async Task UnknownOutcomeReplay_SaleMutation_ReplayWithSameClientOperationId_ReturnsWasExistingTrueWithoutDuplicate()
    {
        // Arrange
        var saleHandler = CreateSaleHandler();
        var customerId = Guid.NewGuid();
        _fakes.Parties.AddCustomer(new Customer { Id = customerId, Name = "Valued Customer", IsActive = true });

        var baseUnitId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var productUnitId = Guid.NewGuid();

        _fakes.Catalog.AddProduct(new Product
        {
            Id = productId,
            Name = "Laptop Core i7",
            BaseUnitId = baseUnitId,
            DefaultSalePrice = 1500m,
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

        var clientOperationId = Guid.NewGuid();
        var cashierUserId = Guid.NewGuid();
        var saleCommand = new CompleteSaleCommand(
            ClientOperationId: clientOperationId,
            CustomerId: customerId,
            CashierUserId: cashierUserId,
            SessionId: null,
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Bank,
            AmountTendered: 1500m,
            PaymentReference: "BANK-TXN-101",
            QuotationId: null,
            Lines: [new CompleteSaleLineInput(productId, productUnitId, 1m, 1500m, [])]);

        // Act 1: Initial mutation commits
        var firstResult = await saleHandler.HandleAsync(saleCommand, CancellationToken.None);
        Assert.True(firstResult.IsSuccess);
        var firstVal = Assert.IsType<CompleteSaleResult>(firstResult.Value);
        Assert.False(firstVal.WasExisting, "First execution must have WasExisting = false");
        var committedSaleId = firstVal.SaleId;
        var committedInvoice = firstVal.InvoiceNumber;

        // Act 2: Client timed out / disconnected before receiving response, and replays exact mutation
        var replayResult = await saleHandler.HandleAsync(saleCommand, CancellationToken.None);

        // Assert: Replay returns committed transaction with WasExisting = true
        Assert.True(replayResult.IsSuccess);
        var replayVal = Assert.IsType<CompleteSaleResult>(replayResult.Value);
        Assert.True(replayVal.WasExisting, "Replay must return WasExisting = true");
        Assert.Equal(committedSaleId, replayVal.SaleId);
        Assert.Equal(committedInvoice, replayVal.InvoiceNumber);
        Assert.Equal(1500m, replayVal.GrandTotal);

        // Invariant: Zero duplicate records or duplicate inventory deductions
        Assert.Single(_fakes.Sales.Sales.Values, s => s.ClientOperationId == clientOperationId);
        Assert.Single(_fakes.Sales.SaleItems, i => i.SaleId == committedSaleId);
    }

    [Fact]
    public async Task UnknownOutcomeReplay_PurchaseMutation_ReplayWithSameClientOperationId_ReturnsWasExistingTrueWithoutDuplicate()
    {
        // Arrange
        var purchaseHandler = CreatePurchaseHandler();
        var supplierId = Guid.NewGuid();
        _fakes.Parties.AddSupplier(new Supplier
        {
            Id = supplierId,
            Name = "Direct Supplier Ltd",
            DealerCode = "DLR-SUPP-01",
            IsActive = true
        });

        var baseUnitId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var productUnitId = Guid.NewGuid();

        _fakes.Catalog.AddProduct(new Product
        {
            Id = productId,
            Name = "Monitor 27 inch",
            BaseUnitId = baseUnitId,
            DefaultSalePrice = 300m,
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

        var clientOperationId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var purchaseCommand = new CreatePurchaseCommand(
            SupplierId: supplierId,
            SupplierInvoiceNumber: "INV-SUPP-888",
            PurchaseDate: DateOnly.FromDateTime(DateTime.UtcNow),
            Note: "Inventory restock",
            OtherCharges: 0m,
            SettlementMode: PurchaseSettlementMode.External,
            CreatedBy: actorId,
            ClientOperationId: clientOperationId,
            Lines: [new CreatePurchaseLineInput(productId, productUnitId, 5m, 200m, 300m, [])],
            InitialPaymentAmount: 0m);

        // Act 1: Initial purchase execution
        var firstResult = await purchaseHandler.HandleAsync(purchaseCommand, CancellationToken.None);
        Assert.True(firstResult.IsSuccess);
        var firstVal = Assert.IsType<CreatePurchaseResult>(firstResult.Value);
        Assert.False(firstVal.WasExisting, "First execution must have WasExisting = false");
        var committedPurchaseId = firstVal.PurchaseId;
        var committedPurchaseNumber = firstVal.PurchaseNumber;

        // Act 2: Client unknown outcome replay with exact same ClientOperationId
        var replayResult = await purchaseHandler.HandleAsync(purchaseCommand, CancellationToken.None);

        // Assert
        Assert.True(replayResult.IsSuccess);
        var replayVal = Assert.IsType<CreatePurchaseResult>(replayResult.Value);
        Assert.True(replayVal.WasExisting, "Replay must return WasExisting = true");
        Assert.Equal(committedPurchaseId, replayVal.PurchaseId);
        Assert.Equal(committedPurchaseNumber, replayVal.PurchaseNumber);
        Assert.Equal(1000m, replayVal.GrandTotal);

        // Invariant: Exactly one purchase in repository
        Assert.Single(_fakes.Purchasing.Purchases.Values, p => p.ClientOperationId == clientOperationId);
    }

    [Fact]
    public async Task UnknownOutcomeReplay_SupplierPaymentMutation_ReplayWithSameClientOperationId_ReturnsWasExistingTrueWithoutDuplicate()
    {
        // Arrange
        var paymentHandler = CreateSupplierPaymentHandler();
        var supplierId = Guid.NewGuid();
        _fakes.Parties.AddSupplier(new Supplier
        {
            Id = supplierId,
            Name = "Parts Distributor",
            DealerCode = "DLR-PARTS-01",
            IsActive = true
        });

        _fakes.SupplierAccounts.AddEntry(new SupplierAccountEntry
        {
            Id = Guid.NewGuid(),
            SupplierId = supplierId,
            Direction = SupplierAccountDirection.IncreasePayable,
            Amount = 1000m,
            EntryType = SupplierAccountEntryType.OpeningBalance
        });

        var clientOperationId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var paymentCommand = new CreateSupplierPaymentCommand(
            SupplierId: supplierId,
            Amount: 600m,
            Purpose: SupplierPaymentPurpose.Settlement,
            Method: SupplierSettlementMethod.External,
            ActorId: actorId,
            ClientOperationId: clientOperationId,
            ExternalReference: "PAY-REF-554",
            Note: "Settlement payment");

        // Act 1: First payment execution
        var firstResult = await paymentHandler.HandleAsync(paymentCommand, CancellationToken.None);
        Assert.True(firstResult.IsSuccess);
        var firstVal = Assert.IsType<SupplierPaymentResult>(firstResult.Value);
        Assert.False(firstVal.WasExisting, "First execution must have WasExisting = false");
        var committedPaymentId = firstVal.PaymentId;
        var committedPaymentNumber = firstVal.PaymentNumber;

        // Act 2: Client replay with identical ClientOperationId
        var replayResult = await paymentHandler.HandleAsync(paymentCommand, CancellationToken.None);

        // Assert
        Assert.True(replayResult.IsSuccess);
        var replayVal = Assert.IsType<SupplierPaymentResult>(replayResult.Value);
        Assert.True(replayVal.WasExisting, "Replay must return WasExisting = true");
        Assert.Equal(committedPaymentId, replayVal.PaymentId);
        Assert.Equal(committedPaymentNumber, replayVal.PaymentNumber);

        // Invariant: Exactly one payment in repository
        Assert.Single(_fakes.SupplierAccounts.Payments.Values, p => p.ClientOperationId == clientOperationId);
    }
}
