using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Posting;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Reporting;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Application.Features.Thaka;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Identity;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Posting;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.Thaka;
using EdgeRetails.Infrastructure.Repositories;
using EdgeRetails.Infrastructure.Services;
using Xunit;

namespace EdgeRetails.UnitTests;

public sealed class Phase11PostingConsumerCallerIntegrationTests
{
    private static readonly TimeZoneInfo KarachiZone = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "Pakistan Standard Time" : "Asia/Karachi");

    private static readonly Pbkdf2PinCredentialService PinService = new();

    private sealed class CallerTestIdentityRepository : IIdentityReadRepository
    {
        private readonly Dictionary<Guid, User> _users = new();
        private readonly Dictionary<Guid, Role> _roles = new();
        private readonly Dictionary<Guid, HashSet<string>> _permissions = new();

        public void AddUser(User user, string roleName, IEnumerable<string> permissions)
        {
            _users[user.Id] = user;
            var role = new Role { Id = user.RoleId, Name = roleName, IsActive = true };
            _roles[user.RoleId] = role;
            _permissions[user.Id] = new HashSet<string>(permissions, StringComparer.OrdinalIgnoreCase);
        }

        public Task<IReadOnlyList<User>> GetActiveUsersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<User>>(_users.Values.Where(u => u.Status == UserStatus.Active).ToArray());

        public Task<User?> GetUserAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult(_users.GetValueOrDefault(userId));

        public Task<Role?> GetRoleAsync(Guid roleId, CancellationToken cancellationToken) =>
            Task.FromResult(_roles.GetValueOrDefault(roleId));

        public Task<IReadOnlyList<Role>> GetRolesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Role>>(_roles.Values.ToArray());

        public Task<IReadOnlyList<string>> GetRolePermissionKeysAsync(Guid roleId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<IReadOnlyList<Permission>> GetPermissionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Permission>>(Array.Empty<Permission>());

        public Task<IReadOnlySet<string>> GetEffectivePermissionKeysAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<string>>(_permissions.GetValueOrDefault(userId) ?? new HashSet<string>());
    }

    private sealed class CallerTestBusinessAuditWriter : IBusinessAuditWriter
    {
        public List<(string action, string entityType, Guid? entityId, Guid actorId, Guid correlationId, string? summary)> Records { get; } = new();

        public void Record(string action, string entityType, Guid? entityId, Guid actorId, Guid correlationId, string? summary = null)
        {
            Records.Add((action, entityType, entityId, actorId, correlationId, summary));
        }
    }

    private static (User User, string Pin) CreateUser(string roleName, string pin, IEnumerable<string> permissions, CallerTestIdentityRepository repo)
    {
        var cred = PinService.Hash(pin);
        var roleId = Guid.NewGuid();
        var user = new User
        {
            Id = Guid.NewGuid(),
            DisplayName = $"{roleName} User",
            RoleId = roleId,
            PinHash = cred.Hash,
            PinSalt = cred.Salt,
            PinIterations = cred.Iterations,
            PinAlgorithm = cred.Algorithm,
            Status = UserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Version = 1
        };
        repo.AddUser(user, roleName, permissions);
        return (user, pin);
    }

    private readonly Phase2TestDoubles _fakes = new();
    private readonly CallerTestIdentityRepository _identity = new();
    private readonly InMemoryPeriodLockRepository _periodLocks = new();
    private readonly PostingAuditRepository _postingAudits = new();
    private readonly CallerTestBusinessAuditWriter _businessAudit = new();
    private readonly ShopClock _shopClock;
    private readonly ShopPostingValidationService _postingValidation;

    public Phase11PostingConsumerCallerIntegrationTests()
    {
        // Pinned to 2026-10-12 10:00:00 PKT (05:00:00 UTC)
        var utcTimestamp = new DateTimeOffset(2026, 10, 12, 5, 0, 0, TimeSpan.Zero);
        _shopClock = new ShopClock(KarachiZone, () => utcTimestamp);
        _postingValidation = new ShopPostingValidationService(
            _shopClock,
            _periodLocks,
            _postingAudits,
            _identity,
            PinService,
            _businessAudit);
    }

    private CashSession OpenCashSession(Guid cashierId, decimal openingCash = 5000m)
    {
        var session = new CashSession
        {
            Id = Guid.CreateVersion7(),
            OpenedBy = cashierId,
            OpeningCash = openingCash,
            Status = CashSessionStatus.Open,
            OpenedAt = _shopClock.UtcNow,
            BusinessDate = _shopClock.ShopDate
        };
        _fakes.Cash.AddSession(session);
        return session;
    }

    private (Product Product, ProductUnit Unit) SeedQuantityProduct(
        string sku = "TEST-SKU-01",
        decimal cost = 200m,
        decimal price = 300m,
        decimal stock = 20m)
    {
        var unit = new Unit { Id = Guid.CreateVersion7(), Name = "Piece", Symbol = "pc" };
        var product = new Product
        {
            Id = Guid.CreateVersion7(),
            Name = "Test Product " + sku,
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
                CreatedAt = _shopClock.UtcNow.AddDays(-10)
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

    [Fact]
    public async Task PostExpenseHandler_BackdateWithin7Days_WithoutSupervisor_FailsClosed()
    {
        var cashier = CreateUser("Cashier", "1111", new[] { PermissionKeys.ExpensesManage }, _identity);
        OpenCashSession(cashier.User.Id);

        var handler = new PostExpenseHandler(
            _fakes.Expenses,
            _fakes.CashMovements,
            _fakes.OperationLock,
            _fakes.Numbers,
            _fakes.Audit,
            _shopClock,
            _fakes.Transactions,
            _fakes.Authorization,
            _fakes.UnitOfWork,
            _fakes.OutcomeLedger,
            postingValidation: _postingValidation);

        var category = new ExpenseCategory { Id = Guid.NewGuid(), Name = "Utilities", IsActive = true };
        _fakes.Expenses.AddCategory(category);

        var command = new PostExpenseCommand(
            ClientOperationId: Guid.NewGuid(),
            CategoryId: category.Id,
            SubcategoryId: null,
            ExpenseDate: new DateOnly(2026, 10, 10), // 2 days ago
            Amount: 500m,
            PaymentMethod: ExpensePaymentMethod.Cash,
            Description: "Electric bill",
            Reference: "REF-001",
            ActorId: cashier.User.Id,
            SupervisorApproval: null);

        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("posting.supervisor_authorization_required", result.Error?.Code);
    }

    [Fact]
    public async Task PostExpenseHandler_BackdateWithin7Days_WithValidSupervisor_Succeeds()
    {
        var cashier = CreateUser("Cashier", "1111", new[] { PermissionKeys.ExpensesManage }, _identity);
        var supervisor = CreateUser("Supervisor", "8888", new[] { PermissionKeys.SupervisorPostingOverride }, _identity);
        OpenCashSession(cashier.User.Id);

        var handler = new PostExpenseHandler(
            _fakes.Expenses,
            _fakes.CashMovements,
            _fakes.OperationLock,
            _fakes.Numbers,
            _fakes.Audit,
            _shopClock,
            _fakes.Transactions,
            _fakes.Authorization,
            _fakes.UnitOfWork,
            _fakes.OutcomeLedger,
            postingValidation: _postingValidation);

        var category = new ExpenseCategory { Id = Guid.NewGuid(), Name = "Rent", IsActive = true };
        _fakes.Expenses.AddCategory(category);

        var command = new PostExpenseCommand(
            ClientOperationId: Guid.NewGuid(),
            CategoryId: category.Id,
            SubcategoryId: null,
            ExpenseDate: new DateOnly(2026, 10, 10), // 2 days ago
            Amount: 12000m,
            PaymentMethod: ExpensePaymentMethod.Cash,
            Description: "Shop Rent",
            Reference: "RENT-OCT",
            ActorId: cashier.User.Id,
            SupervisorApproval: new SupervisorApprovalRequest(
                SupervisorId: supervisor.User.Id,
                SupervisorPin: "8888",
                Reason: "Rent paid on weekend, entered Monday",
                RequestedAtUtc: _shopClock.UtcNow));

        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);

        // Verify posting audit record was created atomically
        var audits = await _postingAudits.GetEntriesByEntityAsync(result.Value.ExpenseId);
        Assert.Single(audits);
        Assert.Equal(PostingOperationTypes.Expense, audits[0].OperationType);
        Assert.Equal(new DateOnly(2026, 10, 10), audits[0].PostingDate);
        Assert.Equal(supervisor.User.Id, audits[0].SupervisorId);
    }

    [Fact]
    public async Task PostExpenseHandler_CallerBooleanOverride_FailsClosedUnconditionally()
    {
        var cashier = CreateUser("Cashier", "1111", new[] { PermissionKeys.ExpensesManage }, _identity);
        OpenCashSession(cashier.User.Id);

        var handler = new PostExpenseHandler(
            _fakes.Expenses,
            _fakes.CashMovements,
            _fakes.OperationLock,
            _fakes.Numbers,
            _fakes.Audit,
            _shopClock,
            _fakes.Transactions,
            _fakes.Authorization,
            _fakes.UnitOfWork,
            _fakes.OutcomeLedger,
            postingValidation: _postingValidation);

        var category = new ExpenseCategory { Id = Guid.NewGuid(), Name = "Supplies", IsActive = true };
        _fakes.Expenses.AddCategory(category);

        var command = new PostExpenseCommand(
            ClientOperationId: Guid.NewGuid(),
            CategoryId: category.Id,
            SubcategoryId: null,
            ExpenseDate: new DateOnly(2026, 10, 10),
            Amount: 300m,
            PaymentMethod: ExpensePaymentMethod.Cash,
            Description: "Office Supplies",
            Reference: null,
            ActorId: cashier.User.Id,
            CallerSuppliedBooleanOverride: true); // Forbidden override attempt

        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("posting.caller_boolean_authority_rejected", result.Error?.Code);
    }

    [Fact]
    public async Task PostExpenseHandler_PeriodLocked_FailsClosedEvenWithSupervisor()
    {
        var cashier = CreateUser("Cashier", "1111", new[] { PermissionKeys.ExpensesManage }, _identity);
        var supervisor = CreateUser("Supervisor", "8888", new[] { PermissionKeys.SupervisorPostingOverride }, _identity);
        OpenCashSession(cashier.User.Id);

        _periodLocks.AddPeriodLock(new PeriodLock
        {
            Id = Guid.NewGuid(),
            PeriodName = "Audit Freeze October Week 1",
            PeriodStartDate = new DateOnly(2026, 10, 1),
            PeriodEndDate = new DateOnly(2026, 10, 7),
            IsLocked = true,
            LockedAt = _shopClock.UtcNow,
            LockedBy = Guid.NewGuid(),
            LockReason = "Statutory Audit"
        });

        var handler = new PostExpenseHandler(
            _fakes.Expenses,
            _fakes.CashMovements,
            _fakes.OperationLock,
            _fakes.Numbers,
            _fakes.Audit,
            _shopClock,
            _fakes.Transactions,
            _fakes.Authorization,
            _fakes.UnitOfWork,
            _fakes.OutcomeLedger,
            postingValidation: _postingValidation);

        var category = new ExpenseCategory { Id = Guid.NewGuid(), Name = "Cleaning", IsActive = true };
        _fakes.Expenses.AddCategory(category);

        var command = new PostExpenseCommand(
            ClientOperationId: Guid.NewGuid(),
            CategoryId: category.Id,
            SubcategoryId: null,
            ExpenseDate: new DateOnly(2026, 10, 6), // In locked period
            Amount: 200m,
            PaymentMethod: ExpensePaymentMethod.Cash,
            Description: "Cleaning supplies",
            Reference: null,
            ActorId: cashier.User.Id,
            SupervisorApproval: new SupervisorApprovalRequest(
                SupervisorId: supervisor.User.Id,
                SupervisorPin: "8888",
                Reason: "Trying to post into closed period",
                RequestedAtUtc: _shopClock.UtcNow));

        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("posting.accounting_period_locked", result.Error?.Code);
    }

    [Fact]
    public async Task CreateStockAdjustmentHandler_BackdateWithin7Days_WithoutSupervisor_FailsClosed()
    {
        var cashier = CreateUser("Cashier", "1111", new[] { PermissionKeys.InventoryManage }, _identity);
        var handler = new CreateStockAdjustmentHandler(
            _fakes.Catalog,
            _fakes.Inventory,
            _fakes.CostAllocator,
            _fakes.Parties,
            _fakes.Traceability,
            _fakes.ResourceLock,
            _fakes.Audit,
            _shopClock,
            _fakes.Transactions,
            _fakes.Authorization,
            _fakes.UnitOfWork,
            _fakes.OutcomeLedger,
            _fakes.PhysicalUnits,
            _fakes.OperationLock,
            postingValidation: _postingValidation);

        var (product, pu) = SeedQuantityProduct("ADJ-SW-01", stock: 10m);

        var command = new CreateStockAdjustmentCommand(
            Mode: StockAdjustmentMode.Delta,
            Reason: StockAdjustmentReason.Other,
            Items: new[]
            {
                new StockAdjustmentItemCommand(
                    ProductId: product.Id,
                    ProductUnitId: pu.Id,
                    Direction: StockAdjustmentDirection.Increase,
                    TargetBucket: InventoryBucket.Sellable,
                    BaseQuantity: 2m,
                    UnitCostSnapshot: 200m)
            },
            ActorId: cashier.User.Id,
            CorrelationId: Guid.NewGuid(),
            Note: "Found 2 extra on top shelf",
            PostingDate: new DateOnly(2026, 10, 8), // 4 days ago
            SupervisorApproval: null);

        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("posting.supervisor_authorization_required", result.Error?.Code);
    }

    [Fact]
    public async Task CreateStockAdjustmentHandler_CallerBooleanOverride_FailsClosed()
    {
        var cashier = CreateUser("Cashier", "1111", new[] { PermissionKeys.InventoryManage }, _identity);
        var handler = new CreateStockAdjustmentHandler(
            _fakes.Catalog,
            _fakes.Inventory,
            _fakes.CostAllocator,
            _fakes.Parties,
            _fakes.Traceability,
            _fakes.ResourceLock,
            _fakes.Audit,
            _shopClock,
            _fakes.Transactions,
            _fakes.Authorization,
            _fakes.UnitOfWork,
            _fakes.OutcomeLedger,
            _fakes.PhysicalUnits,
            _fakes.OperationLock,
            postingValidation: _postingValidation);

        var (product, pu) = SeedQuantityProduct("ADJ-RT-01", stock: 10m);

        var command = new CreateStockAdjustmentCommand(
            Mode: StockAdjustmentMode.Delta,
            Reason: StockAdjustmentReason.Other,
            Items: new[]
            {
                new StockAdjustmentItemCommand(
                    ProductId: product.Id,
                    ProductUnitId: pu.Id,
                    Direction: StockAdjustmentDirection.Decrease,
                    TargetBucket: InventoryBucket.Sellable,
                    BaseQuantity: 1m,
                    UnitCostSnapshot: 200m)
            },
            ActorId: cashier.User.Id,
            CorrelationId: Guid.NewGuid(),
            Note: "Damaged box",
            PostingDate: new DateOnly(2026, 10, 8),
            CallerSuppliedBooleanOverride: true);

        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("posting.caller_boolean_authority_rejected", result.Error?.Code);
    }

    [Fact]
    public async Task CompleteSaleHandler_BackdateWithin7Days_WithoutSupervisor_FailsClosed()
    {
        var cashier = CreateUser("Cashier", "1111", new[] { PermissionKeys.SalesCreate, PermissionKeys.SalesPosUse }, _identity);
        var session = OpenCashSession(cashier.User.Id);

        var handler = new CompleteSaleHandler(
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
            _shopClock,
            _fakes.Transactions,
            _fakes.Authorization,
            _fakes.UnitOfWork,
            outcomeLedger: _fakes.OutcomeLedger,
            postingValidation: _postingValidation);

        var (product, pu) = SeedQuantityProduct("USB-01", cost: 25m, price: 50m, stock: 100m);

        var command = new CompleteSaleCommand(
            ClientOperationId: Guid.NewGuid(),
            CustomerId: null,
            CashierUserId: cashier.User.Id,
            SessionId: session.Id,
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Cash,
            AmountTendered: 50m,
            PaymentReference: null,
            QuotationId: null,
            Lines: new[]
            {
                new CompleteSaleLineInput(
                    ProductId: product.Id,
                    ProductUnitId: pu.Id,
                    EnteredQuantity: 1m,
                    ExpectedUnitPrice: 50m,
                    InventoryUnitIds: Array.Empty<Guid>())
            },
            PostingDate: new DateOnly(2026, 10, 10), // 2 days ago without supervisor
            SupervisorApproval: null);

        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("posting.supervisor_authorization_required", result.Error?.Code);
    }

    [Fact]
    public async Task CompleteSaleHandler_BackdateWithin7Days_WithValidSupervisor_Succeeds()
    {
        var cashier = CreateUser("Cashier", "1111", new[] { PermissionKeys.SalesCreate, PermissionKeys.SalesPosUse }, _identity);
        var supervisor = CreateUser("Supervisor", "8888", new[] { PermissionKeys.SupervisorPostingOverride }, _identity);
        var session = OpenCashSession(cashier.User.Id);

        var handler = new CompleteSaleHandler(
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
            _shopClock,
            _fakes.Transactions,
            _fakes.Authorization,
            _fakes.UnitOfWork,
            outcomeLedger: _fakes.OutcomeLedger,
            postingValidation: _postingValidation);

        var (product, pu) = SeedQuantityProduct("HDMI-01", cost: 40m, price: 80m, stock: 50m);

        var command = new CompleteSaleCommand(
            ClientOperationId: Guid.NewGuid(),
            CustomerId: null,
            CashierUserId: cashier.User.Id,
            SessionId: session.Id,
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Cash,
            AmountTendered: 80m,
            PaymentReference: null,
            QuotationId: null,
            Lines: new[]
            {
                new CompleteSaleLineInput(
                    ProductId: product.Id,
                    ProductUnitId: pu.Id,
                    EnteredQuantity: 1m,
                    ExpectedUnitPrice: 80m,
                    InventoryUnitIds: Array.Empty<Guid>())
            },
            PostingDate: new DateOnly(2026, 10, 10), // 2 days ago
            SupervisorApproval: new SupervisorApprovalRequest(
                SupervisorId: supervisor.User.Id,
                SupervisorPin: "8888",
                Reason: "Offline manual register reconciliation",
                RequestedAtUtc: _shopClock.UtcNow));

        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);

        // Verify posting audit
        var audits = await _postingAudits.GetEntriesByEntityAsync(result.Value.SaleId);
        Assert.Single(audits);
        Assert.Equal(PostingOperationTypes.Sale, audits[0].OperationType);
        Assert.Equal(new DateOnly(2026, 10, 10), audits[0].PostingDate);
        Assert.Equal(supervisor.User.Id, audits[0].SupervisorId);
    }

    [Fact]
    public async Task CompleteSaleHandler_CallerBooleanOverride_FailsClosed()
    {
        var cashier = CreateUser("Cashier", "1111", new[] { PermissionKeys.SalesCreate, PermissionKeys.SalesPosUse }, _identity);
        var session = OpenCashSession(cashier.User.Id);

        var handler = new CompleteSaleHandler(
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
            _shopClock,
            _fakes.Transactions,
            _fakes.Authorization,
            _fakes.UnitOfWork,
            outcomeLedger: _fakes.OutcomeLedger,
            postingValidation: _postingValidation);

        var (product, pu) = SeedQuantityProduct("VGA-01", cost: 20m, price: 40m, stock: 20m);

        var command = new CompleteSaleCommand(
            ClientOperationId: Guid.NewGuid(),
            CustomerId: null,
            CashierUserId: cashier.User.Id,
            SessionId: session.Id,
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Cash,
            AmountTendered: 40m,
            PaymentReference: null,
            QuotationId: null,
            Lines: new[]
            {
                new CompleteSaleLineInput(
                    ProductId: product.Id,
                    ProductUnitId: pu.Id,
                    EnteredQuantity: 1m,
                    ExpectedUnitPrice: 40m,
                    InventoryUnitIds: Array.Empty<Guid>())
            },
            PostingDate: new DateOnly(2026, 10, 10),
            CallerSuppliedBooleanOverride: true); // Forbidden override attempt

        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("posting.caller_boolean_authority_rejected", result.Error?.Code);
    }

    [Fact]
    public void ReportingPeriodBoundary_IndependentOfHostLocalTimeZone()
    {
        // Prove that boundary service accurately computes Asia/Karachi (UTC+5) boundaries
        // regardless of machine host timezone.
        var boundaryService = new ShopPeriodBoundaryService(_shopClock);

        // Daily boundary for 2026-10-12
        var (dailyStart, dailyEnd) = boundaryService.GetDailyUtcBoundary(new DateOnly(2026, 10, 12));
        Assert.Equal(new DateTimeOffset(2026, 10, 11, 19, 0, 0, TimeSpan.Zero), dailyStart);
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 19, 0, 0, TimeSpan.Zero), dailyEnd);
        Assert.Equal(TimeSpan.FromHours(24), dailyEnd - dailyStart);

        // Monthly boundary for 2026 October (31 days)
        var (monthlyStart, monthlyEnd) = boundaryService.GetMonthlyUtcBoundary(2026, 10);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 19, 0, 0, TimeSpan.Zero), monthlyStart);
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 19, 0, 0, TimeSpan.Zero), monthlyEnd);
        Assert.Equal(TimeSpan.FromDays(31), monthlyEnd - monthlyStart);

        // Yearly boundary for 2026 (365 days)
        var (yearlyStart, yearlyEnd) = boundaryService.GetYearlyUtcBoundary(2026);
        Assert.Equal(new DateTimeOffset(2025, 12, 31, 19, 0, 0, TimeSpan.Zero), yearlyStart);
        Assert.Equal(new DateTimeOffset(2026, 12, 31, 19, 0, 0, TimeSpan.Zero), yearlyEnd);
        Assert.Equal(TimeSpan.FromDays(365), yearlyEnd - yearlyStart);
    }
}
