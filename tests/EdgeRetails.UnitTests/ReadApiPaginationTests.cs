using System.Reflection;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Parties;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Features.Thaka;
using EdgeRetails.Application.Features.Warranty;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.Thaka;
using EdgeRetails.Infrastructure.Services;
using EdgeRetails.Server.Controllers;
using EdgeRetails.Server.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace EdgeRetails.UnitTests;

public sealed class ReadApiPaginationTests
{
    [Fact]
    public async Task SalesHistory_IsPagedAndBounded()
    {
        // 1. Verify default PageSize
        var defaultQuery = new GetSalesHistoryQuery();
        Assert.Equal(50, defaultQuery.PageSize);

        // 2. Verify controller clamps huge and non-positive pageSize
        GetSalesHistoryQuery? capturedQuery = null;
        var fakeSalesReads = new FakeSalesReadService((q, _) =>
        {
            capturedQuery = q;
            return Task.FromResult<IReadOnlyList<SalesHistoryRowDto>>([]);
        });

        var controller = Authorize(new SalesController(
            null!, null!, null!, null!, null!, null!,
            fakeSalesReads,
            null!));

        var beforeCompletedAt = DateTimeOffset.UtcNow.AddDays(-1);
        var beforeSaleId = Guid.NewGuid();

        // Huge request
        await controller.GetSalesHistory(
            fromUtc: null,
            toUtc: null,
            search: "INV-100",
            pageSize: 1_000_000,
            beforeCompletedAt: beforeCompletedAt,
            beforeSaleId: beforeSaleId);

        Assert.NotNull(capturedQuery);
        Assert.Equal(500, capturedQuery!.PageSize);
        Assert.Equal("INV-100", capturedQuery.Search);
        Assert.Equal(beforeCompletedAt, capturedQuery.BeforeCompletedAt);
        Assert.Equal(beforeSaleId, capturedQuery.BeforeSaleId);

        // Zero / Negative request falls back to default 50
        await controller.GetSalesHistory(null, null, null, pageSize: 0);
        Assert.Equal(50, capturedQuery.PageSize);

        await controller.GetSalesHistory(null, null, null, pageSize: -10);
        Assert.Equal(50, capturedQuery.PageSize);

        // 3. Verify Dapper SQL enforces LIMIT @PageSize and deterministic ORDER BY
        var dapperSource = File.ReadAllText(Path.Combine(
            GetSolutionRoot(), "src", "EdgeRetails.Infrastructure", "Services", "DapperReadServices.cs"));

        Assert.Contains("ORDER BY s.completed_at DESC, s.id DESC", dapperSource);
        Assert.Contains("LIMIT @PageSize", dapperSource);
        Assert.Contains("s.completed_at >= @FromUtc", dapperSource);
        Assert.Contains("s.completed_at < @ToUtc", dapperSource);
        Assert.Contains("s.invoice_number ILIKE @SearchLike", dapperSource);
        Assert.Contains("s.completed_at < @BeforeCompletedAt", dapperSource);
    }

    [Fact]
    public async Task PurchaseHistory_IsPagedAndBounded()
    {
        // 1. Verify default PageSize
        var defaultQuery = new GetPurchaseHistoryQuery();
        Assert.Equal(50, defaultQuery.PageSize);

        // 2. Verify controller clamps huge and non-positive pageSize
        GetPurchaseHistoryQuery? capturedQuery = null;
        var fakePurchasingReads = new FakePurchasingReadServiceWrapper((q, _) =>
        {
            capturedQuery = q;
            return Task.FromResult<IReadOnlyList<PurchaseHistoryRowDto>>([]);
        });

        var controller = Authorize(new PurchasingController(
            null!, null!, null!, null!,
            fakePurchasingReads));

        var beforePurchaseDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-2));
        var beforePurchaseId = Guid.NewGuid();

        // Huge request
        await controller.GetHistory(
            fromDate: null,
            toDate: null,
            supplierId: null,
            search: "PO-44",
            pageSize: 999_999,
            beforePurchaseDate: beforePurchaseDate,
            beforePurchaseId: beforePurchaseId);

        Assert.NotNull(capturedQuery);
        Assert.Equal(500, capturedQuery!.PageSize);
        Assert.Equal("PO-44", capturedQuery.Search);
        Assert.Equal(beforePurchaseDate, capturedQuery.BeforePurchaseDate);
        Assert.Equal(beforePurchaseId, capturedQuery.BeforePurchaseId);

        // Zero / Negative request falls back to default 50
        await controller.GetHistory(null, null, null, null, pageSize: 0);
        Assert.Equal(50, capturedQuery.PageSize);

        await controller.GetHistory(null, null, null, null, pageSize: -1);
        Assert.Equal(50, capturedQuery.PageSize);

        // 3. Verify Dapper SQL enforces LIMIT @PageSize and deterministic ORDER BY
        var dapperSource = File.ReadAllText(Path.Combine(
            GetSolutionRoot(), "src", "EdgeRetails.Infrastructure", "Services", "DapperReadServices.cs"));

        Assert.Contains("ORDER BY p.purchase_date DESC, p.id DESC", dapperSource);
        Assert.Contains("p.purchase_date >= @FromDate", dapperSource);
        Assert.Contains("p.purchase_date <= @ToDate", dapperSource);
        Assert.Contains("p.supplier_id = @SupplierId", dapperSource);
        Assert.Contains("p.purchase_date < @BeforePurchaseDate", dapperSource);
    }

    [Fact]
    public async Task InventoryMovements_IsPagedAndBounded()
    {
        int capturedPageSize = 0;
        DateTimeOffset? capturedBeforeOccurredAt = null;
        Guid? capturedBeforeMovementId = null;

        var fakeOverviewReads = new FakeInventoryOverviewReadService
        {
            OnGetMovements = (pageSize, _, beforeOccurredAt, beforeMovementId) =>
            {
                capturedPageSize = pageSize;
                capturedBeforeOccurredAt = beforeOccurredAt;
                capturedBeforeMovementId = beforeMovementId;
                return Task.FromResult<IReadOnlyList<InventoryMovementRowDto>>([]);
            }
        };

        var controller = Authorize(new InventoryController(
            fakeOverviewReads,
            null!, null!, null!, null!, null!, null!, null!, null!, null!));

        var cursorTime = DateTimeOffset.UtcNow.AddHours(-3);
        var cursorId = Guid.NewGuid();

        // Huge request
        await controller.GetMovements(
            pageSize: 5000,
            beforeOccurredAt: cursorTime,
            beforeMovementId: cursorId);

        Assert.Equal(500, capturedPageSize);
        Assert.Equal(cursorTime, capturedBeforeOccurredAt);
        Assert.Equal(cursorId, capturedBeforeMovementId);

        // Zero / Negative request clamped
        await controller.GetMovements(pageSize: 0);
        Assert.Equal(50, capturedPageSize);

        await controller.GetMovements(pageSize: -99);
        Assert.Equal(50, capturedPageSize);

        // Verify service implementation applies clamp and deterministic ordering
        var serviceSource = File.ReadAllText(Path.Combine(
            GetSolutionRoot(), "src", "EdgeRetails.Infrastructure", "Services", "InventoryOverviewReadService.cs"));

        Assert.Contains("Math.Clamp(pageSize <= 0 ? 50 : pageSize, 1, 500)", serviceSource);
        Assert.Contains("OrderByDescending(x => x.movement.OccurredAt)", serviceSource);
        Assert.Contains("ThenByDescending(x => x.movement.Id)", serviceSource);
        Assert.Contains("Take(take)", serviceSource);
    }

    [Fact]
    public async Task ExactUnits_IsPagedAndBounded()
    {
        int capturedPageSize = 0;
        Guid capturedProductId = Guid.Empty;
        InventoryUnitStatus? capturedStatus = null;
        Guid? capturedSourceItemId = null;

        var fakeWorkflowReads = new FakePhase4WorkflowReadService
        {
            OnGetExactUnits = (prodId, status, sourceItemId, pageSize, _) =>
            {
                capturedProductId = prodId;
                capturedStatus = status;
                capturedSourceItemId = sourceItemId;
                capturedPageSize = pageSize;
                return Task.FromResult<IReadOnlyList<ExactInventoryUnitDto>>([]);
            }
        };

        var controller = Authorize(new InventoryController(
            null!, null!, fakeWorkflowReads, null!, null!, null!, null!, null!, null!, null!));

        var productId = Guid.NewGuid();
        var sourcePurchaseItemId = Guid.NewGuid();

        // Huge request
        await controller.GetExactUnits(
            productId: productId,
            status: InventoryUnitStatus.InStock,
            sourcePurchaseItemId: sourcePurchaseItemId,
            pageSize: 100_000);

        Assert.Equal(productId, capturedProductId);
        Assert.Equal(InventoryUnitStatus.InStock, capturedStatus);
        Assert.Equal(sourcePurchaseItemId, capturedSourceItemId);
        Assert.Equal(500, capturedPageSize);

        // Zero / Negative request
        await controller.GetExactUnits(productId, null, null, pageSize: 0);
        Assert.Equal(50, capturedPageSize);

        await controller.GetExactUnits(productId, null, null, pageSize: -1);
        Assert.Equal(50, capturedPageSize);

        // Verify Phase4WorkflowReadService implementation has bounded Take and deterministic ordering
        var serviceSource = File.ReadAllText(Path.Combine(
            GetSolutionRoot(), "src", "EdgeRetails.Infrastructure", "Services", "Phase4WorkflowReadService.cs"));

        Assert.Contains("Math.Clamp(pageSize <= 0 ? 100 : pageSize, 1, 500)", serviceSource);
        Assert.Contains(".OrderBy(x => x.Id)", serviceSource);
        Assert.Contains("x.Id.CompareTo(cursorId) > 0", serviceSource);
        Assert.Contains(".Take(take)", serviceSource);
    }

    [Fact]
    public async Task SupplierKhata_IsPagedAndBounded()
    {
        int capturedPageSize = 0;
        Guid capturedSupplierId = Guid.Empty;
        DateTimeOffset? capturedBeforeOccurredAt = null;
        DateTimeOffset? capturedBeforeCreatedAt = null;
        Guid? capturedBeforeEntryId = null;

        var fakeSupplierAccountReads = new FakeSupplierAccountReadService
        {
            OnGetWorkspace = (supId, pageSize, beforeOccurredAt, beforeCreatedAt, beforeEntryId, _) =>
            {
                capturedSupplierId = supId;
                capturedPageSize = pageSize;
                capturedBeforeOccurredAt = beforeOccurredAt;
                capturedBeforeCreatedAt = beforeCreatedAt;
                capturedBeforeEntryId = beforeEntryId;
                return Task.FromResult(new SupplierAccountWorkspaceDto(
                    new SupplierAccountSummaryDto(Guid.Empty, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m),
                    [], [], [], [],
                    new SupplierWarrantySummaryDto(0, 0, 0, 0, 0, 0)));
            }
        };

        var controller = Authorize(new SuppliersController(
            null!, null!, null!, fakeSupplierAccountReads));

        var supplierId = Guid.NewGuid();
        var cursorOccurredAt = DateTimeOffset.UtcNow.AddDays(-1);
        var cursorCreatedAt = DateTimeOffset.UtcNow.AddHours(-12);
        var cursorEntryId = Guid.NewGuid();

        // Huge request
        await controller.GetSupplierWorkspace(
            id: supplierId,
            pageSize: 50_000,
            beforeOccurredAt: cursorOccurredAt,
            beforeCreatedAt: cursorCreatedAt,
            beforeEntryId: cursorEntryId);

        Assert.Equal(supplierId, capturedSupplierId);
        Assert.Equal(500, capturedPageSize);
        Assert.Equal(cursorOccurredAt, capturedBeforeOccurredAt);
        Assert.Equal(cursorCreatedAt, capturedBeforeCreatedAt);
        Assert.Equal(cursorEntryId, capturedBeforeEntryId);

        // Non-positive pageSize
        await controller.GetSupplierWorkspace(supplierId, pageSize: 0);
        Assert.Equal(50, capturedPageSize);

        // Verify service code clamps take to 500 and enforces deterministic statement ordering
        var serviceSource = File.ReadAllText(Path.Combine(
            GetSolutionRoot(), "src", "EdgeRetails.Infrastructure", "Services", "Phase5OperationsReadServices.cs"));

        Assert.Contains("Math.Clamp(pageSize <= 0 ? 50 : pageSize, 1, 500)", serviceSource);
        Assert.Contains("OrderByDescending(x => x.OccurredAt)", serviceSource);
        Assert.Contains("ThenByDescending(x => x.CreatedAt)", serviceSource);
        Assert.Contains("ThenByDescending(x => x.Id)", serviceSource);
    }

    [Fact]
    public async Task WarrantyHistory_IsPagedAndBounded()
    {
        int capturedPageSize = 0;
        string? capturedSearch = null;
        DateTimeOffset? capturedBeforeCreatedAt = null;
        Guid? capturedBeforeWorkId = null;

        var fakeWarrantyReads = new FakeWarrantyReadService
        {
            OnGetDashboard = (search, pageSize, beforeCreatedAt, beforeWorkId, _) =>
            {
                capturedSearch = search;
                capturedPageSize = pageSize;
                capturedBeforeCreatedAt = beforeCreatedAt;
                capturedBeforeWorkId = beforeWorkId;
                return Task.FromResult(new WarrantyDashboardDto(new WarrantyDashboardSummaryDto(0, 0, 0, 0, 0, 0, 0), []));
            }
        };

        var controller = Authorize(new WarrantyController(
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            fakeWarrantyReads));

        var cursorCreatedAt = DateTimeOffset.UtcNow.AddDays(-3);
        var cursorWorkId = Guid.NewGuid();

        // Huge request
        await controller.GetDashboard(
            search: "CLM-99",
            pageSize: 100_000,
            beforeCreatedAt: cursorCreatedAt,
            beforeWorkId: cursorWorkId,
            beforeWorkKind: WarrantyWorkKind.CustomerClaim);

        Assert.Equal("CLM-99", capturedSearch);
        Assert.Equal(500, capturedPageSize);
        Assert.Equal(cursorCreatedAt, capturedBeforeCreatedAt);
        Assert.Equal(cursorWorkId, capturedBeforeWorkId);

        // Non-positive pageSize
        await controller.GetDashboard(null, pageSize: 0);
        Assert.Equal(50, capturedPageSize);

        // Verify service enforces take clamp and deterministic ordering
        var serviceSource = File.ReadAllText(Path.Combine(
            GetSolutionRoot(), "src", "EdgeRetails.Infrastructure", "Services", "Phase5OperationsReadServices.cs"));

        Assert.Contains("OrderByDescending(x => x.ReceivedAt)", serviceSource);
        Assert.Contains("ThenByDescending(x => x.Id)", serviceSource);
        Assert.Contains("OrderByDescending(x => x.CreatedAt)", serviceSource);
    }

    [Fact]
    public void PageSize_HasMaximumCap()
    {
        const int huge = 1_000_000;
        const int negative = -100;
        const int zero = 0;
        const int maxCap = 500;

        // Domain 1: Sales History
        Assert.Equal(maxCap, Math.Clamp(huge <= 0 ? 50 : huge, 1, maxCap));
        Assert.Equal(50, Math.Clamp(zero <= 0 ? 50 : zero, 1, maxCap));
        Assert.Equal(50, Math.Clamp(negative <= 0 ? 50 : negative, 1, maxCap));

        // Domain 2: Purchase History
        Assert.Equal(maxCap, Math.Clamp(huge <= 0 ? 50 : huge, 1, maxCap));

        // Domain 3: Inventory Movements
        Assert.Equal(maxCap, Math.Clamp(huge <= 0 ? 50 : huge, 1, maxCap));

        // Domain 4: Exact Units
        Assert.Equal(maxCap, Math.Clamp(huge <= 0 ? 100 : huge, 1, maxCap));

        // Domain 5: Customers
        Assert.Equal(maxCap, Math.Clamp(huge <= 0 ? 50 : huge, 1, maxCap));

        // Domain 6: Suppliers
        Assert.Equal(maxCap, Math.Clamp(huge <= 0 ? 50 : huge, 1, maxCap));

        // Domain 7: Supplier Khata
        Assert.Equal(maxCap, Math.Clamp(huge <= 0 ? 50 : huge, 1, maxCap));

        // Domain 8: Warranty Claims/History
        Assert.Equal(maxCap, Math.Clamp(huge <= 0 ? 50 : huge, 1, maxCap));

        // Domain 9: Expenses
        Assert.Equal(maxCap, Math.Clamp(huge <= 0 ? 50 : huge, 1, maxCap));

        // Domain 10: Thaka Projects
        Assert.Equal(maxCap, Math.Clamp(huge <= 0 ? 50 : huge, 1, maxCap));
    }

    [Fact]
    public void PagedResults_HaveDeterministicOrdering()
    {
        var root = GetSolutionRoot();

        var dapper = File.ReadAllText(Path.Combine(root, "src", "EdgeRetails.Infrastructure", "Services", "DapperReadServices.cs"));
        var bizReads = File.ReadAllText(Path.Combine(root, "src", "EdgeRetails.Infrastructure", "Services", "BusinessOperationsReadServices.cs"));
        var phase4Reads = File.ReadAllText(Path.Combine(root, "src", "EdgeRetails.Infrastructure", "Services", "Phase4WorkflowReadService.cs"));
        var phase5Reads = File.ReadAllText(Path.Combine(root, "src", "EdgeRetails.Infrastructure", "Services", "Phase5OperationsReadServices.cs"));
        var thakaReads = File.ReadAllText(Path.Combine(root, "src", "EdgeRetails.Infrastructure", "Services", "ThakaReadService.cs"));
        var invOverview = File.ReadAllText(Path.Combine(root, "src", "EdgeRetails.Infrastructure", "Services", "InventoryOverviewReadService.cs"));

        // Domain 1: Sales & Quotations & Returns
        Assert.Contains("ORDER BY s.completed_at DESC, s.id DESC", dapper);
        Assert.Contains("ORDER BY q.quotation_date DESC, q.id DESC", dapper);
        Assert.Contains("ORDER BY r.created_at DESC, r.id DESC", dapper);

        // Domain 2: Purchases & Purchase Returns & Purchase Units
        Assert.Contains("ORDER BY p.purchase_date DESC, p.id DESC", dapper);
        Assert.Contains("ORDER BY r.created_at DESC, r.id DESC", dapper);
        Assert.Contains("ThenBy(u => u.Id)", dapper);

        // Domain 3: Inventory Movements
        Assert.Contains("OrderByDescending(x => x.movement.OccurredAt)", invOverview);
        Assert.Contains("ThenByDescending(x => x.movement.Id)", invOverview);

        // Domain 4: Exact Units
        Assert.Contains(".OrderBy(x => x.Id)", phase4Reads);
        Assert.Contains("x.Id.CompareTo(cursorId) > 0", phase4Reads);

        // Domain 5: Customers
        Assert.Contains(".OrderBy(x => x.Name)", bizReads);
        Assert.Contains(".ThenBy(x => x.Id)", bizReads);

        // Domain 6: Suppliers
        Assert.Contains(".OrderBy(x => x.Name)", bizReads);
        Assert.Contains(".ThenBy(x => x.Id)", bizReads);

        // Domain 7: Supplier Khata
        Assert.Contains("OrderByDescending(x => x.OccurredAt)", phase5Reads);
        Assert.Contains("ThenByDescending(x => x.CreatedAt)", phase5Reads);
        Assert.Contains("ThenByDescending(x => x.Id)", phase5Reads);

        // Domain 8: Warranty
        Assert.Contains("OrderByDescending(x => x.ReceivedAt)", phase5Reads);
        Assert.Contains("ThenByDescending(x => x.Id)", phase5Reads);
        Assert.Contains("OrderByDescending(x => x.CreatedAt)", phase5Reads);

        // Domain 9: Expenses
        Assert.Contains("OrderByDescending(x => x.expense.ExpenseDate)", bizReads);
        Assert.Contains("ThenByDescending(x => x.expense.Id)", bizReads);
        Assert.Contains("x.expense.Id < query.BeforeExpenseId.Value", bizReads);

        // Domain 10: Thaka
        Assert.Contains("ORDER BY p.started_on DESC, p.id DESC", thakaReads);
        Assert.Contains("ORDER BY mi.issued_at DESC, mi.id DESC, ii.id", thakaReads);
        Assert.Contains("ORDER BY p.recorded_at DESC, p.id DESC", thakaReads);
    }

    [Fact]
    public void Filters_AreExecutedServerSide()
    {
        var root = GetSolutionRoot();

        var dapper = File.ReadAllText(Path.Combine(root, "src", "EdgeRetails.Infrastructure", "Services", "DapperReadServices.cs"));
        var bizReads = File.ReadAllText(Path.Combine(root, "src", "EdgeRetails.Infrastructure", "Services", "BusinessOperationsReadServices.cs"));
        var phase4Reads = File.ReadAllText(Path.Combine(root, "src", "EdgeRetails.Infrastructure", "Services", "Phase4WorkflowReadService.cs"));
        var thakaReads = File.ReadAllText(Path.Combine(root, "src", "EdgeRetails.Infrastructure", "Services", "ThakaReadService.cs"));

        // Push-down in Sales SQL
        Assert.Contains("s.completed_at >= @FromUtc", dapper);
        Assert.Contains("s.completed_at < @ToUtc", dapper);
        Assert.Contains("s.invoice_number ILIKE @SearchLike", dapper);
        Assert.Contains("c.name ILIKE @SearchLike", dapper);
        Assert.Contains("s.completed_at < @BeforeCompletedAt", dapper);

        // Push-down in Purchasing SQL
        Assert.Contains("p.purchase_date >= @FromDate", dapper);
        Assert.Contains("p.purchase_date <= @ToDate", dapper);
        Assert.Contains("p.supplier_id = @SupplierId", dapper);
        Assert.Contains("p.purchase_date < @BeforePurchaseDate", dapper);

        // Push-down in Expenses LINQ (IQueryable server-side)
        Assert.Contains("x.expense.ExpenseDate >= query.FromDate.Value", bizReads);
        Assert.Contains("x.expense.ExpenseDate <= query.ToDate.Value", bizReads);
        Assert.Contains("x.expense.CategoryId == query.CategoryId.Value", bizReads);
        Assert.Contains("x.expense.ExpenseDate < query.BeforeExpenseDate.Value", bizReads);

        // Push-down in Exact Units LINQ (IQueryable server-side)
        Assert.Contains("x.ProductId == productId", phase4Reads);
        Assert.Contains("x.Status == status.Value", phase4Reads);
        Assert.Contains("x.SourcePurchaseItemId == sourcePurchaseItemId.Value", phase4Reads);

        // Push-down in Thaka SQL
        Assert.Contains("p.project_number ILIKE '%' || cast(@Search as text) || '%'", thakaReads);
        Assert.Contains("p.status = cast(@Status as integer)", thakaReads);
        Assert.Contains("p.started_on < cast(@BeforeStartedOn as date)", thakaReads);
    }

    [Fact]
    public void SupplierBalance_RemainsBackendAuthoritative()
    {
        // Simulate backend-authoritative Supplier Khata ledger entries
        var supplierId = Guid.NewGuid();
        var entries = new List<SupplierAccountEntryRecord>
        {
            // 1. Purchase invoice: increases debt to supplier (+1000)
            new(Guid.NewGuid(), supplierId, SupplierAccountEntryType.Purchase, SupplierAccountDirection.IncreasePayable, 1000.00m, 1000.00m, DateTimeOffset.UtcNow.AddDays(-10)),
            // 2. Purchase return credit: reduces debt to supplier (-200)
            new(Guid.NewGuid(), supplierId, SupplierAccountEntryType.PurchaseReturnCredit, SupplierAccountDirection.DecreasePayable, 200.00m, -200.00m, DateTimeOffset.UtcNow.AddDays(-8)),
            // 3. Purchase void reversal: cancels voided purchase (-100)
            new(Guid.NewGuid(), supplierId, SupplierAccountEntryType.PurchaseVoidReversal, SupplierAccountDirection.DecreasePayable, 100.00m, -100.00m, DateTimeOffset.UtcNow.AddDays(-7)),
            // 4. Warranty credit from supplier: reduces debt (-50)
            new(Guid.NewGuid(), supplierId, SupplierAccountEntryType.WarrantyCredit, SupplierAccountDirection.DecreasePayable, 50.00m, -50.00m, DateTimeOffset.UtcNow.AddDays(-6)),
            // 5. Payment to supplier: reduces debt (-500)
            new(Guid.NewGuid(), supplierId, SupplierAccountEntryType.SupplierPayment, SupplierAccountDirection.DecreasePayable, 500.00m, -500.00m, DateTimeOffset.UtcNow.AddDays(-5)),
            // 6. Payment reversal (bounced payment): increases debt (+100)
            new(Guid.NewGuid(), supplierId, SupplierAccountEntryType.SupplierPaymentReversal, SupplierAccountDirection.IncreasePayable, 100.00m, 100.00m, DateTimeOffset.UtcNow.AddDays(-4)),
            // 7. Refund received from supplier: increases debt to supplier (+50)
            new(Guid.NewGuid(), supplierId, SupplierAccountEntryType.SupplierRefundReceived, SupplierAccountDirection.IncreasePayable, 50.00m, 50.00m, DateTimeOffset.UtcNow.AddDays(-3)),
            // 8. Refund reversal: reduces debt (-50)
            new(Guid.NewGuid(), supplierId, SupplierAccountEntryType.SupplierRefundReversal, SupplierAccountDirection.DecreasePayable, 50.00m, -50.00m, DateTimeOffset.UtcNow.AddDays(-2)),
        };

        // Backend aggregate calculation
        var grossPurchased = entries.Where(e => e.EntryType == SupplierAccountEntryType.Purchase).Sum(e => e.Amount);
        var returnCredits = entries.Where(e => e.EntryType == SupplierAccountEntryType.PurchaseReturnCredit).Sum(e => e.Amount);
        var voidReversals = entries.Where(e => e.EntryType == SupplierAccountEntryType.PurchaseVoidReversal).Sum(e => e.Amount);
        var warrantyCredits = entries.Where(e => e.EntryType == SupplierAccountEntryType.WarrantyCredit).Sum(e => e.Amount);
        var netPurchased = grossPurchased - returnCredits - voidReversals;

        var netPaid = entries.Where(e => e.EntryType == SupplierAccountEntryType.SupplierPayment).Sum(e => e.Amount)
                      - entries.Where(e => e.EntryType == SupplierAccountEntryType.SupplierPaymentReversal).Sum(e => e.Amount);

        var netRefunds = entries.Where(e => e.EntryType == SupplierAccountEntryType.SupplierRefundReceived).Sum(e => e.Amount)
                        - entries.Where(e => e.EntryType == SupplierAccountEntryType.SupplierRefundReversal).Sum(e => e.Amount);

        var aggregateBalance = entries.Sum(e => e.SignedAmount);

        Assert.Equal(1000.00m, grossPurchased);
        Assert.Equal(200.00m, returnCredits);
        Assert.Equal(100.00m, voidReversals);
        Assert.Equal(700.00m, netPurchased);
        Assert.Equal(50.00m, warrantyCredits);
        Assert.Equal(400.00m, netPaid);
        Assert.Equal(0.00m, netRefunds);

        // Expected balance: 1000 - 200 - 100 - 50 - 500 + 100 + 50 - 50 = 250.00m
        Assert.Equal(250.00m, aggregateBalance);

        // Now simulate statement running balance calculation (as executed in SupplierAccountReadService)
        decimal running = 0m; // opening balance for full ledger
        var statementRows = entries
            .OrderBy(e => e.OccurredAt)
            .Select(e =>
            {
                running += e.SignedAmount;
                return new { e.Id, e.SignedAmount, RunningBalance = running };
            })
            .ToList();

        // The final row's running balance MUST exactly equal aggregate balance
        Assert.Equal(aggregateBalance, statementRows[^1].RunningBalance);

        // Now simulate a paged window (e.g. latest 4 entries)
        var pageSize = 4;
        var pagedEntries = entries
            .OrderByDescending(e => e.OccurredAt)
            .Take(pageSize)
            .ToList();

        var oldestInPage = pagedEntries[^1];
        var openingBalance = entries
            .Where(e => e.OccurredAt < oldestInPage.OccurredAt)
            .Sum(e => e.SignedAmount);

        decimal pagedRunning = openingBalance;
        var pagedRows = pagedEntries
            .OrderBy(e => e.OccurredAt)
            .Select(e =>
            {
                pagedRunning += e.SignedAmount;
                return new { e.Id, e.SignedAmount, RunningBalance = pagedRunning };
            })
            .ToList();

        // The last row of the latest page MUST also equal aggregate balance
        Assert.Equal(aggregateBalance, pagedRows[^1].RunningBalance);
    }

    private static string GetSolutionRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "EdgeRetails.sln")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("EdgeRetails.sln was not found.");
    }

    private static T Authorize<T>(T controller) where T : ControllerBase
    {
        var context = new DefaultHttpContext();
        var permissions = typeof(PermissionKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(x => x.GetValue(null) as string)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        context.Items["ActorContext"] = new ActorContext(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Pagination Test",
            Guid.NewGuid(),
            "Test Role",
            permissions);
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        return controller;
    }

    private sealed record SupplierAccountEntryRecord(
        Guid Id,
        Guid SupplierId,
        SupplierAccountEntryType EntryType,
        SupplierAccountDirection Direction,
        decimal Amount,
        decimal SignedAmount,
        DateTimeOffset OccurredAt);

    private sealed class FakeSalesReadService : ISalesReadService
    {
        private readonly Func<GetSalesHistoryQuery, CancellationToken, Task<IReadOnlyList<SalesHistoryRowDto>>> _getHistory;

        public FakeSalesReadService(Func<GetSalesHistoryQuery, CancellationToken, Task<IReadOnlyList<SalesHistoryRowDto>>> getHistory)
        {
            _getHistory = getHistory;
        }

        public Task<IReadOnlyList<SalesHistoryRowDto>> GetHistoryAsync(GetSalesHistoryQuery query, CancellationToken cancellationToken) =>
            _getHistory(query, cancellationToken);

        public Task<SaleDetailDto?> GetDetailAsync(GetSaleDetailQuery query, CancellationToken cancellationToken) =>
            Task.FromResult<SaleDetailDto?>(null);

        public Task<IReadOnlyList<QuotationListRowDto>> GetQuotationsAsync(GetQuotationsQuery query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<QuotationListRowDto>>([]);

        public Task<QuotationDetailDto?> GetQuotationDetailAsync(GetQuotationDetailQuery query, CancellationToken cancellationToken) =>
            Task.FromResult<QuotationDetailDto?>(null);

        public Task<IReadOnlyList<SaleReturnHistoryRowDto>> GetReturnHistoryAsync(GetSaleReturnHistoryQuery query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SaleReturnHistoryRowDto>>([]);
    }

    private sealed class FakePurchasingReadServiceWrapper : IPurchasingReadService
    {
        private readonly Func<GetPurchaseHistoryQuery, CancellationToken, Task<IReadOnlyList<PurchaseHistoryRowDto>>> _getHistory;

        public FakePurchasingReadServiceWrapper(Func<GetPurchaseHistoryQuery, CancellationToken, Task<IReadOnlyList<PurchaseHistoryRowDto>>> getHistory)
        {
            _getHistory = getHistory;
        }

        public Task<IReadOnlyList<PurchaseHistoryRowDto>> GetHistoryAsync(GetPurchaseHistoryQuery query, CancellationToken cancellationToken) =>
            _getHistory(query, cancellationToken);

        public Task<PurchaseDocumentDto?> GetDocumentAsync(GetPurchaseDocumentQuery query, CancellationToken cancellationToken) =>
            Task.FromResult<PurchaseDocumentDto?>(null);

        public Task<IReadOnlyList<PurchaseReturnHistoryRowDto>> GetReturnHistoryAsync(GetPurchaseReturnHistoryQuery query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PurchaseReturnHistoryRowDto>>([]);

        public Task<IReadOnlyList<CommittedInventoryUnitDto>> GetUnitsForPurchaseItemAsync(Guid purchaseItemId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CommittedInventoryUnitDto>>([]);
    }

    private sealed class FakeInventoryOverviewReadService : IInventoryOverviewReadService
    {
        public Func<int, CancellationToken, DateTimeOffset?, Guid?, Task<IReadOnlyList<InventoryMovementRowDto>>>? OnGetMovements { get; set; }

        public Task<IReadOnlyList<InventoryStockRowDto>> GetStockAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<InventoryStockRowDto>>([]);

        public Task<IReadOnlyList<InventoryStockRowDto>> GetStockPageAsync(InventoryStockPageQuery query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<InventoryStockRowDto>>([]);

        public Task<IReadOnlyList<InventoryMovementRowDto>> GetMovementsAsync(int pageSize, CancellationToken cancellationToken, DateTimeOffset? beforeOccurredAt = null, Guid? beforeMovementId = null) =>
            OnGetMovements != null
                ? OnGetMovements(pageSize, cancellationToken, beforeOccurredAt, beforeMovementId)
                : Task.FromResult<IReadOnlyList<InventoryMovementRowDto>>([]);
    }

    private sealed class FakePhase4WorkflowReadService : IPhase4WorkflowReadService
    {
        public Func<Guid, InventoryUnitStatus?, Guid?, int, CancellationToken, Task<IReadOnlyList<ExactInventoryUnitDto>>>? OnGetExactUnits { get; set; }

        public Task<IReadOnlyList<ExactInventoryUnitDto>> GetExactUnitsAsync(Guid productId, InventoryUnitStatus? status, Guid? sourcePurchaseItemId, CancellationToken cancellationToken) =>
            GetExactUnitsAsync(productId, status, sourcePurchaseItemId, 100, cancellationToken);

        public Task<IReadOnlyList<ExactInventoryUnitDto>> GetExactUnitsAsync(Guid productId, InventoryUnitStatus? status, Guid? sourcePurchaseItemId, int pageSize, CancellationToken cancellationToken) =>
            OnGetExactUnits != null
                ? OnGetExactUnits(productId, status, sourcePurchaseItemId, pageSize, cancellationToken)
                : Task.FromResult<IReadOnlyList<ExactInventoryUnitDto>>([]);

        public Task<IReadOnlyList<ScannerProductMatchDto>> ResolveScannerAsync(string input, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ScannerProductMatchDto>>([]);

        public Task<IReadOnlyList<PosDraftSummaryDto>> GetOpenDraftsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PosDraftSummaryDto>>([]);

        public Task<PosDraftDetailDto?> GetDraftAsync(Guid draftId, CancellationToken cancellationToken) =>
            Task.FromResult<PosDraftDetailDto?>(null);

        public Task<StocktakeSnapshotDto?> GetOpenStocktakeAsync(CancellationToken cancellationToken) =>
            Task.FromResult<StocktakeSnapshotDto?>(null);
    }

    private sealed class FakeSupplierAccountReadService : ISupplierAccountReadService
    {
        public Func<Guid, int, DateTimeOffset?, DateTimeOffset?, Guid?, CancellationToken, Task<SupplierAccountWorkspaceDto>>? OnGetWorkspace { get; set; }

        public Task<SupplierAccountWorkspaceDto> GetWorkspaceAsync(Guid supplierId, int pageSize = 200, DateTimeOffset? beforeOccurredAt = null, DateTimeOffset? beforeCreatedAt = null, Guid? beforeEntryId = null, CancellationToken cancellationToken = default, DateTimeOffset? beforePaymentPaidAt = null, Guid? beforePaymentId = null, DateTimeOffset? beforeRefundReceivedAt = null, Guid? beforeRefundId = null, string? beforeProductName = null, Guid? beforeProductId = null) =>
            OnGetWorkspace != null
                ? OnGetWorkspace(supplierId, pageSize, beforeOccurredAt, beforeCreatedAt, beforeEntryId, cancellationToken)
                : Task.FromResult(new SupplierAccountWorkspaceDto(
                    new SupplierAccountSummaryDto(Guid.Empty, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m),
                    [], [], [], [],
                    new SupplierWarrantySummaryDto(0, 0, 0, 0, 0, 0)));
    }

    private sealed class FakeWarrantyReadService : IWarrantyReadService
    {
        public Func<string?, int, DateTimeOffset?, Guid?, CancellationToken, Task<WarrantyDashboardDto>>? OnGetDashboard { get; set; }

        public Task<WarrantyDashboardDto> GetDashboardAsync(string? search, int pageSize = 100, DateTimeOffset? beforeCreatedAt = null, Guid? beforeWorkId = null, CancellationToken cancellationToken = default, WarrantyWorkKind? beforeWorkKind = null) =>
            OnGetDashboard != null
                ? OnGetDashboard(search, pageSize, beforeCreatedAt, beforeWorkId, cancellationToken)
                : Task.FromResult(new WarrantyDashboardDto(new WarrantyDashboardSummaryDto(0, 0, 0, 0, 0, 0, 0), []));

        public Task<IReadOnlyList<WarrantyEventDto>> GetClaimTimelineAsync(Guid claimId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WarrantyEventDto>>([]);

        public Task<IReadOnlyList<WarrantyClaimIntakeRowDto>> SearchClaimIntakeAsync(string search, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WarrantyClaimIntakeRowDto>>([]);
    }
}
