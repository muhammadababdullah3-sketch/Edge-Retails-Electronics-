using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Purchasing;
using Xunit;

namespace EdgeRetails.UnitTests;

public sealed class Phase7Pass1IntegrityTests
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
            _fakes.UnitOfWork,
            outcomeLedger: _fakes.OutcomeLedger, physicalUnitCreationAuthority: _fakes.PhysicalUnits);

    private ReceiveProductIntakeHandler CreateIntakeHandler() =>
        new(
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
            _fakes.UnitOfWork,
            physicalUnitCreationAuthority: _fakes.PhysicalUnits);

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
            _fakes.UnitOfWork,
            cash: _fakes.Cash,
            outcomeLedger: new InMemoryOperationOutcomeLedger(),
            refunds: new CreateSupplierRefundHandler(_fakes.SupplierAccounts, _fakes.Parties, _fakes.Cash,
                _fakes.OperationLock, _fakes.ResourceLock, _fakes.Authorization, _fakes.Numbers,
                _fakes.Clock, _fakes.Audit, _fakes.Transactions, _fakes.UnitOfWork));

    private VoidPurchaseHandler CreateVoidPurchaseHandler() =>
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
            _fakes.UnitOfWork,
            cash: _fakes.Cash);

    private PostExpenseHandler CreatePostExpenseHandler() =>
        new(
            _fakes.Expenses,
            _fakes.CashMovements,
            _fakes.OperationLock,
            _fakes.Numbers,
            _fakes.Audit,
            _fakes.Clock,
            _fakes.Transactions,
            _fakes.Authorization,
            _fakes.UnitOfWork,
            _fakes.OutcomeLedger);

    private VoidExpenseHandler CreateVoidExpenseHandler() =>
        new(
            _fakes.Expenses,
            _fakes.CashMovements,
            _fakes.Audit,
            _fakes.Clock,
            _fakes.Transactions,
            _fakes.Authorization,
            _fakes.UnitOfWork,
            _fakes.OperationLock,
            _fakes.OutcomeLedger);

    private (Supplier Supplier, Product Product, ProductUnit Unit) SeedQuantityProduct(string sku = "QTY-P1", decimal cost = 100m, decimal price = 150m)
    {
        var supplier = new Supplier { Id = Guid.CreateVersion7(), Name = "Supplier P7", DealerCode = "SP7", IsActive = true };
        _fakes.Parties.AddSupplier(supplier);

        var unit = new Unit { Id = Guid.CreateVersion7(), Name = "Piece", Symbol = "pc" };
        var product = new Product
        {
            Id = Guid.CreateVersion7(),
            Name = "Pass 1 Widget",
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

    private (Supplier Supplier, Product Product, ProductUnit Unit) SeedSerializedProduct(string sku = "SER-P1", decimal cost = 1000m, decimal price = 1500m)
    {
        var supplier = new Supplier { Id = Guid.CreateVersion7(), Name = "Serial Supplier P7", DealerCode = "SSP", IsActive = true };
        _fakes.Parties.AddSupplier(supplier);

        var unit = new Unit { Id = Guid.CreateVersion7(), Name = "Piece", Symbol = "pc" };
        var product = new Product
        {
            Id = Guid.CreateVersion7(),
            Name = "Serialized Widget",
            Sku = sku,
            BaseUnitId = unit.Id,
            TrackingMode = TrackingMode.Serialized,
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

    private CashSession OpenCashSession(Guid cashierId)
    {
        var session = new CashSession
        {
            Id = Guid.CreateVersion7(),
            BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow),
            OpenedBy = cashierId,
            OpenedAt = _fakes.Clock.UtcNow,
            OpeningCash = 10000m,
            Status = CashSessionStatus.Open
        };
        _fakes.Cash.AddSession(session);
        return session;
    }

    private ExpenseCategory SeedExpenseCategory()
    {
        var category = new ExpenseCategory
        {
            Id = Guid.CreateVersion7(),
            Name = "Office Supplies",
            IsActive = true
        };
        _fakes.Expenses.AddCategory(category);
        return category;
    }

    // 1. Mandatory Focused Test: Void unreceived deferred purchase with zero payment succeeds (D-VOID-1)
    [Fact]
    public async Task D_VOID_1_VoidUnreceivedPurchase_WithZeroPayment_Succeeds()
    {
        var (supplier, product, unit) = SeedQuantityProduct("SKU-UNREC-1");
        var purchaseHandler = CreatePurchaseHandler();
        var voidHandler = CreateVoidPurchaseHandler();
        var actorId = Guid.CreateVersion7();

        var purchaseResult = await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                supplier.Id,
                "INV-UNREC-01",
                DateOnly.FromDateTime(DateTime.UtcNow),
                "Deferred order",
                0m,
                PurchaseSettlementMode.External,
                actorId,
                Guid.CreateVersion7(),
                [
                    new CreatePurchaseLineInput(product.Id, unit.Id, 10m, 100m, 150m, [])
                ],
                InitialPaymentAmount: 0m,
                ReceiveStockImmediately: false),
            CancellationToken.None);

        Assert.True(purchaseResult.IsSuccess, purchaseResult.Error?.Message);
        var purchaseId = purchaseResult.Value!.PurchaseId;

        // Verify purchase is completed and no lots exist
        var purchase = await _fakes.Purchasing.GetPurchaseForUpdateAsync(purchaseId, CancellationToken.None);
        Assert.NotNull(purchase);
        Assert.Equal(PurchaseStatus.Completed, purchase!.Status);
        Assert.Equal(1000m, await _fakes.SupplierAccounts.GetCurrentBalanceAsync(supplier.Id, CancellationToken.None));

        // Act: Void unreceived purchase
        var voidResult = await voidHandler.HandleAsync(
            new VoidPurchaseCommand(purchaseId, Guid.CreateVersion7(), actorId, "Vendor cancelled before shipment"),
            CancellationToken.None);

        Assert.True(voidResult.IsSuccess, voidResult.Error?.Message);

        // Assert: Purchase status is Voided
        Assert.Equal(PurchaseStatus.Voided, purchase.Status);

        // Assert: Supplier payable reconciled back to 0
        var payable = await _fakes.SupplierAccounts.GetCurrentBalanceAsync(supplier.Id, CancellationToken.None);
        Assert.Equal(0m, payable);
    }

    // 2. Mandatory Focused Test: Void unreceived purchase creates zero stock/lot/physical effects (D-VOID-1)
    [Fact]
    public async Task D_VOID_1_VoidUnreceivedPurchase_CreatesZeroStockOrLotOrPhysicalEffects()
    {
        var (supplier, product, unit) = SeedQuantityProduct("SKU-UNREC-2");
        var purchaseHandler = CreatePurchaseHandler();
        var voidHandler = CreateVoidPurchaseHandler();
        var actorId = Guid.CreateVersion7();

        var purchaseResult = await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                supplier.Id,
                "INV-UNREC-02",
                DateOnly.FromDateTime(DateTime.UtcNow),
                "Deferred order",
                0m,
                PurchaseSettlementMode.External,
                actorId,
                Guid.CreateVersion7(),
                [
                    new CreatePurchaseLineInput(product.Id, unit.Id, 5m, 200m, 300m, [])
                ],
                InitialPaymentAmount: 0m,
                ReceiveStockImmediately: false),
            CancellationToken.None);

        Assert.True(purchaseResult.IsSuccess);
        var purchaseId = purchaseResult.Value!.PurchaseId;

        var movementsBefore = _fakes.Inventory.Movements.Count;
        var unitsBefore = _fakes.Inventory.Units.Count;
        var lotsBefore = _fakes.Inventory.Lots.Count;

        // Void the unreceived purchase
        var voidResult = await voidHandler.HandleAsync(
            new VoidPurchaseCommand(purchaseId, Guid.CreateVersion7(), actorId, "Customer cancelled PO"),
            CancellationToken.None);

        Assert.True(voidResult.IsSuccess, voidResult.Error?.Message);

        // Zero movements, zero units, zero lots created
        Assert.Equal(movementsBefore, _fakes.Inventory.Movements.Count);
        Assert.Equal(unitsBefore, _fakes.Inventory.Units.Count);
        Assert.Equal(lotsBefore, _fakes.Inventory.Lots.Count);

        // Stock balance remains null or 0
        var stock = await _fakes.Inventory.GetStockBalanceForUpdateAsync(product.Id, CancellationToken.None);
        Assert.True(stock == null || stock.SellableQty == 0m);
    }

    // 3. Mandatory Focused Test: Partial-received purchase full void fails closed
    [Fact]
    public async Task D_VOID_1_PartialReceivedPurchase_FullVoid_FailsClosed()
    {
        var (supplier, product, unit) = SeedQuantityProduct("SKU-PARTIAL-1");
        var purchaseHandler = CreatePurchaseHandler();
        var intakeHandler = CreateIntakeHandler();
        var voidHandler = CreateVoidPurchaseHandler();
        var actorId = Guid.CreateVersion7();

        var purchaseResult = await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                supplier.Id,
                "INV-PART-01",
                DateOnly.FromDateTime(DateTime.UtcNow),
                "Deferred order",
                0m,
                PurchaseSettlementMode.External,
                actorId,
                Guid.CreateVersion7(),
                [
                    new CreatePurchaseLineInput(product.Id, unit.Id, 10m, 100m, 150m, [])
                ],
                InitialPaymentAmount: 0m,
                ReceiveStockImmediately: false),
            CancellationToken.None);

        Assert.True(purchaseResult.IsSuccess);
        var purchaseId = purchaseResult.Value!.PurchaseId;

        // Intake 4 units out of 10
        var intakeResult = await intakeHandler.HandleAsync(
            new ReceiveProductIntakeCommand(
                purchaseId,
                product.Id,
                unit.Id,
                4m,
                100m,
                [],
                actorId,
                Guid.CreateVersion7()),
            CancellationToken.None);

        Assert.True(intakeResult.IsSuccess, intakeResult.Error?.Message);

        // Attempt to full void the partially-received purchase
        var voidResult = await voidHandler.HandleAsync(
            new VoidPurchaseCommand(purchaseId, Guid.CreateVersion7(), actorId, "Try to void partial PO"),
            CancellationToken.None);

        // Must FAIL CLOSED
        Assert.False(voidResult.IsSuccess);
        Assert.Equal("purchasing.void_partial_receipt_forbidden", voidResult.Error!.Code);

        // Purchase status remains Completed
        var purchase = await _fakes.Purchasing.GetPurchaseForUpdateAsync(purchaseId, CancellationToken.None);
        Assert.Equal(PurchaseStatus.Completed, purchase!.Status);
    }

    // Resolution01 C02: default void retains actual payment and its cash history.
    [Fact]
    public async Task D_VOID_2_ZeroReceiptPurchase_WithCashDrawerPayment_PreservesActualPayment()
    {
        var (supplier, product, unit) = SeedQuantityProduct("SKU-VOID-FIN-1");
        var purchaseHandler = CreatePurchaseHandler();
        var voidHandler = CreateVoidPurchaseHandler();
        var actorId = Guid.CreateVersion7();

        var session = OpenCashSession(actorId);

        // Scenario A: Purchase 10,000, Initial CashDrawer payment 4,000, Zero receipt
        var purchaseResult = await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                supplier.Id,
                "INV-FIN-01",
                DateOnly.FromDateTime(DateTime.UtcNow),
                "Advance PO",
                0m,
                PurchaseSettlementMode.CashDrawer,
                actorId,
                Guid.CreateVersion7(),
                [
                    new CreatePurchaseLineInput(product.Id, unit.Id, 100m, 100m, 150m, [])
                ],
                InitialPaymentAmount: 4000m,
                InitialPaymentMethod: SupplierSettlementMethod.CashDrawer,
                ReceiveStockImmediately: false),
            CancellationToken.None);

        Assert.True(purchaseResult.IsSuccess, purchaseResult.Error?.Message);
        var purchaseId = purchaseResult.Value!.PurchaseId;

        // Before void:
        // Supplier liability: +10,000; Payment: -4,000; Outstanding = 6,000
        var balanceBefore = await _fakes.SupplierAccounts.GetCurrentBalanceAsync(supplier.Id, CancellationToken.None);
        Assert.Equal(6000m, balanceBefore);

        // Cash drawer has 1 movement: SupplierPaymentCashOut 4,000
        var initialCashMovement = _fakes.Cash.Movements.Single();
        Assert.Equal(CashMovementType.SupplierPaymentCashOut, initialCashMovement.MovementType);
        Assert.Equal(CashMovementDirection.Out, initialCashMovement.Direction);
        Assert.Equal(4000m, initialCashMovement.Amount);

        // Act: Void the purchase
        var voidResult = await voidHandler.HandleAsync(
            new VoidPurchaseCommand(purchaseId, Guid.CreateVersion7(), actorId, "Void mistaken order with advance"),
            CancellationToken.None);

        Assert.True(voidResult.IsSuccess, voidResult.Error?.Message);

        // Assert After Void:
        // Liability is voided; the actual advance remains supplier credit.
        var balanceAfter = await _fakes.SupplierAccounts.GetCurrentBalanceAsync(supplier.Id, CancellationToken.None);
        Assert.Equal(-4000m, balanceAfter);

        Assert.Same(initialCashMovement, Assert.Single(_fakes.Cash.Movements));

        // No new cash event is implied by voiding a liability.
        var netCashDrawer = _fakes.Cash.Movements.Sum(m => m.Direction == CashMovementDirection.In ? m.Amount : -m.Amount);
        Assert.Equal(-4000m, netCashDrawer);

        // No implicit payment reversal.
        var purchaseVoid = _fakes.Purchasing.PurchaseVoids.Values.Single(v => v.PurchaseId == purchaseId);
        Assert.Null(purchaseVoid.CashDrawerReversalAmount);

        // Historical payment remains posted.
        var payment = _fakes.SupplierAccounts.Payments.Values.Single();
        Assert.Equal(SupplierSettlementStatus.Posted, payment.Status);

        Assert.Empty(_fakes.SupplierAccounts.PaymentReversals);
        Assert.Empty(_fakes.SupplierAccounts.Refunds);
    }

    // Resolution01 C02: default void does not require an open cash drawer.
    [Fact]
    public async Task D_VOID_2_DefaultVoid_WithNoActiveSession_PreservesActualPayment()
    {
        var (supplier, product, unit) = SeedQuantityProduct("SKU-VOID-NOCASH");
        var purchaseHandler = CreatePurchaseHandler();
        var voidHandler = CreateVoidPurchaseHandler();
        var actorId = Guid.CreateVersion7();

        var session = OpenCashSession(actorId);

        var purchaseResult = await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                supplier.Id,
                "INV-NOCASH-01",
                DateOnly.FromDateTime(DateTime.UtcNow),
                "Advance PO",
                0m,
                PurchaseSettlementMode.CashDrawer,
                actorId,
                Guid.CreateVersion7(),
                [
                    new CreatePurchaseLineInput(product.Id, unit.Id, 10m, 100m, 150m, [])
                ],
                InitialPaymentAmount: 500m,
                InitialPaymentMethod: SupplierSettlementMethod.CashDrawer,
                ReceiveStockImmediately: false),
            CancellationToken.None);

        Assert.True(purchaseResult.IsSuccess);
        var purchaseId = purchaseResult.Value!.PurchaseId;

        // Close the session so no active session exists
        session.Status = CashSessionStatus.Closed;

        // Act: Attempt to void
        var voidResult = await voidHandler.HandleAsync(
            new VoidPurchaseCommand(purchaseId, Guid.CreateVersion7(), actorId, "Void without active drawer"),
            CancellationToken.None);

        Assert.True(voidResult.IsSuccess, voidResult.Error?.Message);

        // Purchase liability is voided, actual payment remains.
        var purchase = await _fakes.Purchasing.GetPurchaseForUpdateAsync(purchaseId, CancellationToken.None);
        Assert.Equal(PurchaseStatus.Voided, purchase!.Status);

        // Payment is still Posted
        var payment = _fakes.SupplierAccounts.Payments.Values.Single();
        Assert.Equal(SupplierSettlementStatus.Posted, payment.Status);

        var purchaseVoid = Assert.Single(_fakes.Purchasing.PurchaseVoids.Values);
        Assert.Null(purchaseVoid.CashDrawerReversalAmount);
        Assert.Empty(_fakes.SupplierAccounts.PaymentReversals);
        Assert.Empty(_fakes.SupplierAccounts.Refunds);
        Assert.Equal(-500m, await _fakes.SupplierAccounts.GetCurrentBalanceAsync(supplier.Id, CancellationToken.None));
        var originalCash = Assert.Single(_fakes.Cash.Movements);
        Assert.Equal(CashMovementType.SupplierPaymentCashOut, originalCash.MovementType);
        Assert.Equal(CashMovementDirection.Out, originalCash.Direction);
        Assert.Equal(500m, originalCash.Amount);
    }

    // 6. Mandatory Focused Test: Expense posting behavior remains unchanged
    [Fact]
    public async Task D_EXP_1_ExpensePosting_BehaviorRemainsUnchanged()
    {
        var category = SeedExpenseCategory();
        var postHandler = CreatePostExpenseHandler();
        var actorId = Guid.CreateVersion7();

        var session = OpenCashSession(actorId);

        var result = await postHandler.HandleAsync(
            new PostExpenseCommand(
                Guid.CreateVersion7(),
                category.Id,
                null,
                DateOnly.FromDateTime(DateTime.UtcNow),
                250m,
                ExpensePaymentMethod.Cash,
                "Printer ink",
                "REF-01",
                actorId),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var expense = _fakes.Expenses.Expenses.Values.Single();
        Assert.Equal(250m, expense.Amount);
        Assert.Equal(ExpensePaymentMethod.Cash, expense.PaymentMethod);

        var move = _fakes.Cash.Movements.Single();
        Assert.Equal(CashMovementType.ExpenseCashOut, move.MovementType);
        Assert.Equal(CashMovementDirection.Out, move.Direction);
        Assert.Equal(250m, move.Amount);
    }

    // 7. Mandatory Focused Test: Cash expense void creates exactly one CashIn (D-EXP-1 Scenario B)
    [Fact]
    public async Task D_EXP_1_CashExpenseVoid_CreatesExactlyOneCompensatingCashIn()
    {
        var category = SeedExpenseCategory();
        var postHandler = CreatePostExpenseHandler();
        var voidHandler = CreateVoidExpenseHandler();
        var actorId = Guid.CreateVersion7();

        var session = OpenCashSession(actorId);

        var postResult = await postHandler.HandleAsync(
            new PostExpenseCommand(
                Guid.CreateVersion7(),
                category.Id,
                null,
                DateOnly.FromDateTime(DateTime.UtcNow),
                1000m,
                ExpensePaymentMethod.Cash,
                "Office repair",
                "REF-REP",
                actorId),
            CancellationToken.None);

        Assert.True(postResult.IsSuccess);
        var expenseId = postResult.Value!.ExpenseId;

        // Act: Void the expense
        var voidResult = await voidHandler.HandleAsync(
            new VoidExpenseCommand(expenseId, actorId, Guid.CreateVersion7(), "Incorrectly recorded repair"),
            CancellationToken.None);

        Assert.True(voidResult.IsSuccess, voidResult.Error?.Message);

        // Status is Voided
        var expense = _fakes.Expenses.Expenses.Values.Single();
        Assert.Equal(ExpenseStatus.Voided, expense.Status);

        // Exactly 2 movements: 1 ExpenseCashOut 1000, 1 ManualCashIn 1000
        Assert.Equal(2, _fakes.Cash.Movements.Count);
        var voidCashMove = _fakes.Cash.Movements.Last();
        Assert.Equal(CashMovementType.ManualCashIn, voidCashMove.MovementType);
        Assert.Equal(CashMovementDirection.In, voidCashMove.Direction);
        Assert.Equal(1000m, voidCashMove.Amount);
        Assert.Equal("EXPENSE", voidCashMove.SourceType);
        Assert.Equal(expenseId, voidCashMove.SourceId);

        // Net drawer delta across original + reversal = -1000 + 1000 = 0 (Scenario B)
        var netCash = _fakes.Cash.Movements.Sum(m => m.Direction == CashMovementDirection.In ? m.Amount : -m.Amount);
        Assert.Equal(0m, netCash);
    }

    // 8. Mandatory Focused Test: Expense void without active cash session fails atomically
    [Fact]
    public async Task D_EXP_1_ExpenseVoid_WithoutActiveCashSession_FailsAtomically()
    {
        var category = SeedExpenseCategory();
        var postHandler = CreatePostExpenseHandler();
        var voidHandler = CreateVoidExpenseHandler();
        var actorId = Guid.CreateVersion7();

        var session = OpenCashSession(actorId);

        var postResult = await postHandler.HandleAsync(
            new PostExpenseCommand(
                Guid.CreateVersion7(),
                category.Id,
                null,
                DateOnly.FromDateTime(DateTime.UtcNow),
                300m,
                ExpensePaymentMethod.Cash,
                "Snacks",
                null,
                actorId),
            CancellationToken.None);

        Assert.True(postResult.IsSuccess);
        var expenseId = postResult.Value!.ExpenseId;

        // Close the session
        session.Status = CashSessionStatus.Closed;

        // Attempt void
        var voidResult = await voidHandler.HandleAsync(
            new VoidExpenseCommand(expenseId, actorId, Guid.CreateVersion7(), "Mistaken entry"),
            CancellationToken.None);

        Assert.False(voidResult.IsSuccess);
        Assert.Equal("cash.session_required", voidResult.Error!.Code);

        // Expense is STILL Posted
        var expense = _fakes.Expenses.Expenses.Values.Single();
        Assert.Equal(ExpenseStatus.Posted, expense.Status);

        // No new cash movements
        Assert.Single(_fakes.Cash.Movements);
    }

    // 9. Mandatory Focused Test: Repeated expense void creates no second CashIn
    [Fact]
    public async Task D_EXP_1_RepeatedExpenseVoid_CreatesNoSecondCashIn()
    {
        var category = SeedExpenseCategory();
        var postHandler = CreatePostExpenseHandler();
        var voidHandler = CreateVoidExpenseHandler();
        var actorId = Guid.CreateVersion7();

        OpenCashSession(actorId);

        var postResult = await postHandler.HandleAsync(
            new PostExpenseCommand(
                Guid.CreateVersion7(),
                category.Id,
                null,
                DateOnly.FromDateTime(DateTime.UtcNow),
                500m,
                ExpensePaymentMethod.Cash,
                "Cleaning",
                null,
                actorId),
            CancellationToken.None);

        Assert.True(postResult.IsSuccess);
        var expenseId = postResult.Value!.ExpenseId;

        // First void
        var void1 = await voidHandler.HandleAsync(
            new VoidExpenseCommand(expenseId, actorId, Guid.CreateVersion7(), "First void"),
            CancellationToken.None);
        Assert.True(void1.IsSuccess);
        Assert.Equal(2, _fakes.Cash.Movements.Count);

        // Second void (idempotent replay)
        var void2 = await voidHandler.HandleAsync(
            new VoidExpenseCommand(expenseId, actorId, Guid.CreateVersion7(), "Second void attempt"),
            CancellationToken.None);
        Assert.True(void2.IsSuccess);

        // Still exactly 2 cash movements
        Assert.Equal(2, _fakes.Cash.Movements.Count);
    }

    // 10 & 11. Mandatory Focused Test: Purchase Return CashDrawer settlement creates exactly one CashIn and balanced Khata (D-RET-1 Scenario C)
    [Fact]
    public async Task D_RET_1_PurchaseReturnCashDrawer_CreatesCashIn_AndBalancesSupplierKhata()
    {
        var (supplier, product, unit) = SeedQuantityProduct("SKU-RET-CASH", cost: 100m);
        var purchaseHandler = CreatePurchaseHandler();
        var returnHandler = CreatePurchaseReturnHandler();
        var actorId = Guid.CreateVersion7();

        OpenCashSession(actorId);

        // Purchase 10 units at 100 = 1,000 (fully received)
        var purchaseResult = await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                supplier.Id,
                "INV-RET-01",
                DateOnly.FromDateTime(DateTime.UtcNow),
                "Initial stock purchase",
                0m,
                PurchaseSettlementMode.External,
                actorId,
                Guid.CreateVersion7(),
                [
                    new CreatePurchaseLineInput(product.Id, unit.Id, 10m, 100m, 150m, [])
                ],
                InitialPaymentAmount: 1000m, InitialPaymentMethod: SupplierSettlementMethod.External),
            CancellationToken.None);

        Assert.True(purchaseResult.IsSuccess);
        var purchaseId = purchaseResult.Value!.PurchaseId;
        var purchaseItem = _fakes.Purchasing.PurchaseItems.Single(i => i.PurchaseId == purchaseId);

        // AUTHORIZED_ASSERTION_ALIGNMENT C01: real payment authorizes a refund.
        Assert.Equal(0m, await _fakes.SupplierAccounts.GetCurrentBalanceAsync(supplier.Id, CancellationToken.None));
        Assert.Equal(1000m, Assert.Single(_fakes.SupplierAccounts.Payments.Values).Amount);
        var initialCashMovementsCount = _fakes.Cash.Movements.Count;

        // Scenario C: Return 10 units with Return Value = 1,000, SettlementMode = CashDrawer
        var returnResult = await returnHandler.HandleAsync(
            new CreatePurchaseReturnCommand(
                purchaseId,
                "Defective batch returned for immediate cash refund",
                null,
                PurchaseReturnSettlementMode.CashDrawer,
                actorId,
                Guid.CreateVersion7(),
                [
                    new PurchaseReturnLineInput(purchaseItem.Id, 10m, 100m, [])
                ]),
            CancellationToken.None);

        Assert.True(returnResult.IsSuccess, returnResult.Error?.Message);

        // 1. Canonical SupplierRefund receives CashIn of 1,000.
        Assert.Equal(initialCashMovementsCount + 1, _fakes.Cash.Movements.Count);
        var returnCashMove = _fakes.Cash.Movements.Last();
        Assert.Equal(CashMovementType.SupplierRefundCashIn, returnCashMove.MovementType);
        Assert.Equal(CashMovementDirection.In, returnCashMove.Direction);
        Assert.Equal(1000m, returnCashMove.Amount);
        var refundFact = Assert.Single(_fakes.SupplierAccounts.Refunds.Values);
        Assert.Equal(returnResult.Value!.PurchaseReturnId, refundFact.ReferenceId);
        Assert.Equal("PurchaseReturn", refundFact.ReferenceType);
        Assert.Equal(refundFact.Id, returnCashMove.SourceId);
        Assert.Equal("SUPPLIER_REFUND", returnCashMove.SourceType);

        // 2. Supplier Khata balancing:
        // PurchaseReturnCredit (DecreasePayable 1,000)
        // SupplierRefundReceived (IncreasePayable 1,000)
        // Return credit and monetary refund net to zero after the actual payment.
        var returnCredit = _fakes.SupplierAccounts.Entries.FirstOrDefault(e => e.EntryType == SupplierAccountEntryType.PurchaseReturnCredit);
        Assert.NotNull(returnCredit);
        Assert.Equal(1000m, returnCredit!.Amount);
        Assert.Equal(SupplierAccountDirection.DecreasePayable, returnCredit.Direction);

        var refundReceived = _fakes.SupplierAccounts.Entries.FirstOrDefault(e => e.EntryType == SupplierAccountEntryType.SupplierRefundReceived);
        Assert.NotNull(refundReceived);
        Assert.Equal(1000m, refundReceived!.Amount);
        Assert.Equal(SupplierAccountDirection.IncreasePayable, refundReceived.Direction);
        Assert.Equal(refundFact.Id, refundReceived.ReferenceId);

        var finalBalance = await _fakes.SupplierAccounts.GetCurrentBalanceAsync(supplier.Id, CancellationToken.None);
        Assert.Equal(0m, finalBalance);
    }

    // 12. Mandatory Focused Test: Purchase Return CashDrawer without active session fails atomically
    [Fact]
    public async Task D_RET_1_PurchaseReturnCashDrawer_WithoutActiveSession_FailsAtomically()
    {
        var (supplier, product, unit) = SeedQuantityProduct("SKU-RET-NOCASH");
        var purchaseHandler = CreatePurchaseHandler();
        var returnHandler = CreatePurchaseReturnHandler();
        var actorId = Guid.CreateVersion7();

        // Purchase with External settlement
        var purchaseResult = await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                supplier.Id,
                "INV-RET-NOCASH",
                DateOnly.FromDateTime(DateTime.UtcNow),
                "Stock purchase",
                0m,
                PurchaseSettlementMode.External,
                actorId,
                Guid.CreateVersion7(),
                [
                    new CreatePurchaseLineInput(product.Id, unit.Id, 5m, 100m, 150m, [])
                ], InitialPaymentAmount: 500m, InitialPaymentMethod: SupplierSettlementMethod.External),
            CancellationToken.None);

        Assert.True(purchaseResult.IsSuccess);
        var purchaseId = purchaseResult.Value!.PurchaseId;
        var purchaseItem = _fakes.Purchasing.PurchaseItems.Single(i => i.PurchaseId == purchaseId);

        // Ensure NO cash session is open
        Assert.Null(await _fakes.Cash.GetOpenSessionForUpdateAsync(CancellationToken.None));

        // Attempt CashDrawer purchase return without open session
        var returnResult = await returnHandler.HandleAsync(
            new CreatePurchaseReturnCommand(
                purchaseId,
                "Return for cash without open drawer",
                null,
                PurchaseReturnSettlementMode.CashDrawer,
                actorId,
                Guid.CreateVersion7(),
                [
                    new PurchaseReturnLineInput(purchaseItem.Id, 2m, 100m, [])
                ]),
            CancellationToken.None);

        Assert.False(returnResult.IsSuccess);
        Assert.Equal("cash.session_required", returnResult.Error!.Code);

        // Atomicity: No return created, stock remains 5
        Assert.Empty(_fakes.Purchasing.PurchaseReturns);
        var stock = await _fakes.Inventory.GetStockBalanceForUpdateAsync(product.Id, CancellationToken.None);
        Assert.Equal(5m, stock!.SellableQty);
    }

    // 13 & 14. Mandatory Focused Tests: P7-N01 Upper Bound Cases A, B, C, D
    [Fact]
    public async Task P7_N01_ReturnQuantityLimits_EnforceReceivedMinusAlreadyReturned()
    {
        var (supplier, product, unit) = SeedQuantityProduct("SKU-P7-N01");
        var purchaseHandler = CreatePurchaseHandler();
        var intakeHandler = CreateIntakeHandler();
        var returnHandler = CreatePurchaseReturnHandler();
        var actorId = Guid.CreateVersion7();

        // Deferred purchase: Ordered = 10, Received = 0 initially
        var purchaseResult = await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                supplier.Id,
                "INV-N01-01",
                DateOnly.FromDateTime(DateTime.UtcNow),
                "P7-N01 verification PO",
                0m,
                PurchaseSettlementMode.External,
                actorId,
                Guid.CreateVersion7(),
                [
                    new CreatePurchaseLineInput(product.Id, unit.Id, 10m, 100m, 150m, [])
                ],
                ReceiveStockImmediately: false),
            CancellationToken.None);

        Assert.True(purchaseResult.IsSuccess);
        var purchaseId = purchaseResult.Value!.PurchaseId;
        var purchaseItem = _fakes.Purchasing.PurchaseItems.Single(i => i.PurchaseId == purchaseId);

        // CASE D: Ordered = 10, Received = 0, Returned = 0, Requested = 1 -> REJECT
        var caseDResult = await returnHandler.HandleAsync(
            new CreatePurchaseReturnCommand(
                purchaseId,
                "Case D return attempt",
                null,
                PurchaseReturnSettlementMode.External,
                actorId,
                Guid.CreateVersion7(),
                [
                    new PurchaseReturnLineInput(purchaseItem.Id, 1m, 100m, [])
                ]),
            CancellationToken.None);

        Assert.False(caseDResult.IsSuccess);
        Assert.Equal("purchasing.return_exceeds_received", caseDResult.Error!.Code);

        // Now intake 4 units: Received = 4
        var intakeResult = await intakeHandler.HandleAsync(
            new ReceiveProductIntakeCommand(
                purchaseId,
                product.Id,
                unit.Id,
                4m,
                100m,
                [],
                actorId,
                Guid.CreateVersion7()),
            CancellationToken.None);
        Assert.True(intakeResult.IsSuccess);

        // CASE A: Ordered = 10, Received = 4, Returned = 0, Requested = 5 -> REJECT
        var caseAResult = await returnHandler.HandleAsync(
            new CreatePurchaseReturnCommand(
                purchaseId,
                "Case A return attempt",
                null,
                PurchaseReturnSettlementMode.External,
                actorId,
                Guid.CreateVersion7(),
                [
                    new PurchaseReturnLineInput(purchaseItem.Id, 5m, 100m, [])
                ]),
            CancellationToken.None);

        Assert.False(caseAResult.IsSuccess);
        Assert.Equal("purchasing.return_exceeds_received", caseAResult.Error!.Code);

        // Return 1 unit successfully: Returned = 1
        var return1Result = await returnHandler.HandleAsync(
            new CreatePurchaseReturnCommand(
                purchaseId,
                "Legit 1 unit return",
                null,
                PurchaseReturnSettlementMode.External,
                actorId,
                Guid.CreateVersion7(),
                [
                    new PurchaseReturnLineInput(purchaseItem.Id, 1m, 100m, [])
                ]),
            CancellationToken.None);
        Assert.True(return1Result.IsSuccess);

        // CASE B: Ordered = 10, Received = 4, Returned = 1, Requested = 4 (MaxReturnable = 4 - 1 = 3) -> REJECT
        var caseBResult = await returnHandler.HandleAsync(
            new CreatePurchaseReturnCommand(
                purchaseId,
                "Case B return attempt",
                null,
                PurchaseReturnSettlementMode.External,
                actorId,
                Guid.CreateVersion7(),
                [
                    new PurchaseReturnLineInput(purchaseItem.Id, 4m, 100m, [])
                ]),
            CancellationToken.None);

        Assert.False(caseBResult.IsSuccess);
        Assert.Equal("purchasing.return_exceeds_received", caseBResult.Error!.Code);

        // Intake remaining 6 units: Total Received = 10. Prior Returned = 1.
        var intake2Result = await intakeHandler.HandleAsync(
            new ReceiveProductIntakeCommand(
                purchaseId,
                product.Id,
                unit.Id,
                6m,
                100m,
                [],
                actorId,
                Guid.CreateVersion7()),
            CancellationToken.None);
        Assert.True(intake2Result.IsSuccess);

        // Return another 2 units: Prior Returned = 1 + 2 = 3.
        var return2Result = await returnHandler.HandleAsync(
            new CreatePurchaseReturnCommand(
                purchaseId,
                "Legit 2 unit return",
                null,
                PurchaseReturnSettlementMode.External,
                actorId,
                Guid.CreateVersion7(),
                [
                    new PurchaseReturnLineInput(purchaseItem.Id, 2m, 100m, [])
                ]),
            CancellationToken.None);
        Assert.True(return2Result.IsSuccess);

        // CASE C: Ordered = 10, Received = 10, Returned = 3, Requested = 7 (MaxReturnable = 10 - 3 = 7) -> ACCEPT
        var caseCResult = await returnHandler.HandleAsync(
            new CreatePurchaseReturnCommand(
                purchaseId,
                "Case C return attempt",
                null,
                PurchaseReturnSettlementMode.External,
                actorId,
                Guid.CreateVersion7(),
                [
                    new PurchaseReturnLineInput(purchaseItem.Id, 7m, 100m, [])
                ]),
            CancellationToken.None);

        Assert.True(caseCResult.IsSuccess, caseCResult.Error?.Message);
    }

    // 15. Mandatory Focused Test: Exact-unit / Container return provenance remains intact
    [Fact]
    public async Task PhysicalProductSafety_ExactUnitValidationPreserved()
    {
        var (supplier, product, unit) = SeedSerializedProduct("SER-PROV-1");
        var purchaseHandler = CreatePurchaseHandler();
        var returnHandler = CreatePurchaseReturnHandler();
        var actorId = Guid.CreateVersion7();

        // Create received purchase with 1 serialized unit
        var purchaseResult = await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                supplier.Id,
                "INV-SER-01",
                DateOnly.FromDateTime(DateTime.UtcNow),
                "Serialized order",
                0m,
                PurchaseSettlementMode.External,
                actorId,
                Guid.CreateVersion7(),
                [
                    new CreatePurchaseLineInput(
                        product.Id,
                        unit.Id,
                        1m,
                        1000m,
                        1500m,
                        [new SerializedIdentityInput("SER-PROV-SN1", "860000000000001", null)])
                ]),
            CancellationToken.None);

        Assert.True(purchaseResult.IsSuccess);
        var purchaseId = purchaseResult.Value!.PurchaseId;
        var purchaseItem = _fakes.Purchasing.PurchaseItems.Single(i => i.PurchaseId == purchaseId);
        var validInventoryUnit = _fakes.Inventory.Units.Single();

        // Attempt return with an invalid/random InventoryUnitId
        var randomUnitId = Guid.CreateVersion7();
        var invalidReturnResult = await returnHandler.HandleAsync(
            new CreatePurchaseReturnCommand(
                purchaseId,
                "Attempt return with fabricated unit id",
                null,
                PurchaseReturnSettlementMode.External,
                actorId,
                Guid.CreateVersion7(),
                [
                    new PurchaseReturnLineInput(purchaseItem.Id, 1m, 1000m, [randomUnitId])
                ]),
            CancellationToken.None);

        Assert.False(invalidReturnResult.IsSuccess);
        Assert.Equal("purchasing.return_serial_not_eligible", invalidReturnResult.Error!.Code);

        // Attempt return with the legitimate InventoryUnitId
        var validReturnResult = await returnHandler.HandleAsync(
            new CreatePurchaseReturnCommand(
                purchaseId,
                "Legitimate return of serialized unit",
                null,
                PurchaseReturnSettlementMode.External,
                actorId,
                Guid.CreateVersion7(),
                [
                    new PurchaseReturnLineInput(purchaseItem.Id, 1m, 1000m, [validInventoryUnit.Id])
                ]),
            CancellationToken.None);

        Assert.True(validReturnResult.IsSuccess, validReturnResult.Error?.Message);
        Assert.Equal(InventoryUnitStatus.SupplierReturned, validInventoryUnit.Status);
    }
}
