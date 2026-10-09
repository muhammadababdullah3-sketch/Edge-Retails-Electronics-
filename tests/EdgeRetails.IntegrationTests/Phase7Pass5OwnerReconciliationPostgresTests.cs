using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Catalog;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Reporting;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Features.Thaka;
using EdgeRetails.Application.Features.Warranty;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.Thaka;
using EdgeRetails.Domain.Warranty;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EdgeRetails.IntegrationTests;

[Collection("Phase2PostgresIntegration")]
public sealed class Phase7Pass5OwnerReconciliationPostgresTests
{
    // HARNESS_CORRECTION: fixtures own the exact session they create and close.
    private sealed class OwnedCashSession(EdgeRetailsDbContext db, CashSession session) : IAsyncDisposable
    {
        public Guid Id => session.Id;
        public decimal OpeningCash => session.OpeningCash;

        public async ValueTask DisposeAsync()
        {
            db.ChangeTracker.Clear();
            var owned = await db.CashSessions.SingleOrDefaultAsync(x => x.Id == session.Id && x.Status == CashSessionStatus.Open);
            if (owned is not null)
            {
                owned.Status = CashSessionStatus.Closed;
                owned.ClosedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();
            }
        }
    }

    private static async Task<OwnedCashSession> EnsureSingleOpenCashSessionAsync(
        EdgeRetailsDbContext db,
        Guid actorId,
        decimal openingCash = 10000m)
    {
        Assert.False(await db.CashSessions.AnyAsync(x => x.Status == CashSessionStatus.Open),
            "Owner fixture requires an empty open-session boundary; a foreign leaked session must be corrected by its owner.");
        return new OwnedCashSession(db, await Phase2PostgresTestHarness.SeedOpenCashSessionAsync(db, actorId, openingCash));
    }

    [Fact]
    public async Task OwnerReconciliation_StockAndCarryingValue_ExactConservationAcrossMovementsAndBuckets()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);

        // Seed 1 Serialized product and 1 Bulk Quantity product
        var serializedSeed = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db, defaultSalePrice: 6000m);
        var bulkSeed = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db, defaultSalePrice: 200m);
        await using var cashSession = await EnsureSingleOpenCashSessionAsync(db, serializedSeed.ActorId, openingCash: 10000m);

        // 1. Purchase Serialized: 2 units @3000m
        var purchaseSerialized = await services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new CreatePurchaseCommand(
            serializedSeed.SupplierId, "RECON-P-SER-" + Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), null, 0m,
            PurchaseSettlementMode.External, serializedSeed.ActorId, Guid.NewGuid(),
            [new CreatePurchaseLineInput(serializedSeed.ProductId, serializedSeed.ProductUnitId, 2m, 3000m, 6000m,
                [new SerializedIdentityInput("RECON-SN-1"), new SerializedIdentityInput("RECON-SN-2")])],
            InitialPaymentAmount: 0m), default);
        Assert.True(purchaseSerialized.IsSuccess, purchaseSerialized.Error?.Message);

        // 2. Purchase Bulk: 50 units @100m
        var purchaseBulk = await services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new CreatePurchaseCommand(
            bulkSeed.SupplierId, "RECON-P-BULK-" + Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), null, 0m,
            PurchaseSettlementMode.External, bulkSeed.ActorId, Guid.NewGuid(),
            [new CreatePurchaseLineInput(bulkSeed.ProductId, bulkSeed.ProductUnitId, 50m, 100m, 200m, [])],
            InitialPaymentAmount: 0m), default);
        Assert.True(purchaseBulk.IsSuccess, purchaseBulk.Error?.Message);
        db.ChangeTracker.Clear();

        var unit1 = await db.InventoryUnits.SingleAsync(x => x.ProductId == serializedSeed.ProductId && x.SerialNumber == "RECON-SN-1");

        // 3. Sale 1 Serialized unit & 10 Bulk units
        var sale = await services.GetRequiredService<CompleteSaleHandler>().HandleAsync(new CompleteSaleCommand(
            Guid.NewGuid(), null, serializedSeed.ActorId, cashSession.Id, 0m, SalePaymentMethod.Cash, 8000m, "RECON-SALE", null,
            [
                new CompleteSaleLineInput(serializedSeed.ProductId, serializedSeed.ProductUnitId, 1m, 6000m, [unit1.Id]),
                new CompleteSaleLineInput(bulkSeed.ProductId, bulkSeed.ProductUnitId, 10m, 200m, [])
            ]), default);
        Assert.True(sale.IsSuccess, sale.Error?.Message);
        db.ChangeTracker.Clear();

        // 4. Condition Transfer 5 Bulk units to Damaged
        var conditionTransfer = await services.GetRequiredService<TransferInventoryConditionHandler>().HandleAsync(
            new TransferInventoryConditionCommand(
                bulkSeed.ProductId, InventoryBucket.Sellable, InventoryBucket.Damaged, 5m, bulkSeed.ActorId,
                "Quality inspection defect"), default);
        Assert.True(conditionTransfer.IsSuccess, conditionTransfer.Error?.Message);
        db.ChangeTracker.Clear();

        // AUDIT RECONCILIATION CHECKS:
        // A. Serialized Product Reconciliation:
        var serBalance = await db.StockBalances.AsNoTracking().SingleAsync(x => x.ProductId == serializedSeed.ProductId);
        var serCost = await db.ProductCostStates.AsNoTracking().SingleAsync(x => x.ProductId == serializedSeed.ProductId);
        var serInStockUnits = await db.InventoryUnits.AsNoTracking().Where(x => x.ProductId == serializedSeed.ProductId && x.Status == InventoryUnitStatus.InStock).ToListAsync();

        Assert.Equal(1m, serBalance.SellableQty);
        Assert.Equal(1m, serCost.CostedQty);
        Assert.Equal(3000m, serCost.TotalInventoryCost);
        Assert.Single(serInStockUnits);
        Assert.Equal(3000m, serInStockUnits.Sum(x => x.AcquisitionCost));

        // Movement Effect conservation check for Serialized:
        var serEffects = await db.InventoryMovementEffects.AsNoTracking()
            .Where(e => db.InventoryMovements.Any(m => m.Id == e.MovementId && m.ProductId == serializedSeed.ProductId && e.StockBucket == InventoryBucket.Sellable))
            .SumAsync(e => e.QuantityDelta);
        Assert.Equal(serBalance.SellableQty, serEffects);

        // B. Bulk Product Reconciliation:
        var bulkBalance = await db.StockBalances.AsNoTracking().SingleAsync(x => x.ProductId == bulkSeed.ProductId);
        var bulkCost = await db.ProductCostStates.AsNoTracking().SingleAsync(x => x.ProductId == bulkSeed.ProductId);

        // 50 received - 10 sold - 5 damaged = 35 sellable, 5 damaged. Total owned = 40.
        Assert.Equal(35m, bulkBalance.SellableQty);
        Assert.Equal(5m, bulkBalance.DamagedQty);
        Assert.Equal(40m, bulkCost.CostedQty);
        Assert.Equal(4000m, bulkCost.TotalInventoryCost); // 40 * 100m

        // Movement Effect conservation check across all buckets for Bulk:
        var bulkSellableDelta = await (from e in db.InventoryMovementEffects
                                      join m in db.InventoryMovements on e.MovementId equals m.Id
                                      where m.ProductId == bulkSeed.ProductId && e.StockBucket == InventoryBucket.Sellable
                                      select e.QuantityDelta).SumAsync();
        Assert.Equal(35m, bulkSellableDelta);

        var bulkDamagedDelta = await (from e in db.InventoryMovementEffects
                                     join m in db.InventoryMovements on e.MovementId equals m.Id
                                     where m.ProductId == bulkSeed.ProductId && e.StockBucket == InventoryBucket.Damaged
                                     select e.QuantityDelta).SumAsync();
        Assert.Equal(5m, bulkDamagedDelta);

        // Invariant: Unexplained stock difference is exactly 0.00
        Assert.Equal(0m, serBalance.SellableQty - serEffects);
        Assert.Equal(0m, bulkBalance.SellableQty - bulkSellableDelta);
        Assert.Equal(0m, bulkBalance.DamagedQty - bulkDamagedDelta);
    }

    [Fact]
    public async Task OwnerReconciliation_CashDrawerAndSupplierLiability_ExactConservationAcrossMovementsAndLedger()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);

        var fixture = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db, defaultSalePrice: 150m);
        await using var session = await EnsureSingleOpenCashSessionAsync(db, fixture.ActorId, openingCash: 5000m);

        // 1. Credit Purchase of 100 units @100m = 10,000m payable
        var purchase = await services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new CreatePurchaseCommand(
            fixture.SupplierId, "RECON-KHATA-" + Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), null, 0m,
            PurchaseSettlementMode.External, fixture.ActorId, Guid.NewGuid(),
            [new CreatePurchaseLineInput(fixture.ProductId, fixture.ProductUnitId, 100m, 100m, 150m, [])],
            InitialPaymentAmount: 0m), default);
        Assert.True(purchase.IsSuccess, purchase.Error?.Message);
        db.ChangeTracker.Clear();

        // 2. Cash Sale of 20 units @150m = 3,000m into cash drawer
        var sale = await services.GetRequiredService<CompleteSaleHandler>().HandleAsync(new CompleteSaleCommand(
            Guid.NewGuid(), null, fixture.ActorId, session.Id, 0m, SalePaymentMethod.Cash, 3000m, "RECON-CASH-SALE", null,
            [new CompleteSaleLineInput(fixture.ProductId, fixture.ProductUnitId, 20m, 150m, [])]), default);
        Assert.True(sale.IsSuccess, sale.Error?.Message);
        db.ChangeTracker.Clear();

        // 3. Manual Cash In +500m
        var manualIn = await services.GetRequiredService<RecordManualCashMovementHandler>().HandleAsync(
            new RecordManualCashMovementCommand(CashMovementDirection.In, 500m, fixture.ActorId, "Till float top up", null), default);
        Assert.True(manualIn.IsSuccess, manualIn.Error?.Message);

        // 4. Manual Cash Out -200m
        var manualOut = await services.GetRequiredService<RecordManualCashMovementHandler>().HandleAsync(
            new RecordManualCashMovementCommand(CashMovementDirection.Out, 200m, fixture.ActorId, "Office cleaning supplies", null), default);
        Assert.True(manualOut.IsSuccess, manualOut.Error?.Message);

        // 5. Supplier Payment from Cash Drawer: 2,500m
        var supplierPay = await services.GetRequiredService<CreateSupplierPaymentHandler>().HandleAsync(
            new CreateSupplierPaymentCommand(
                fixture.SupplierId, 2500m, SupplierPaymentPurpose.Settlement,
                SupplierSettlementMethod.CashDrawer, fixture.ActorId, Guid.NewGuid(), Note: "Partial invoice payment"), default);
        Assert.True(supplierPay.IsSuccess, supplierPay.Error?.Message);
        db.ChangeTracker.Clear();

        // AUDIT RECONCILIATION CHECKS:
        // A. Cash Drawer Reconciliation:
        // Expected Cash = Opening(5000) + Sale(3000) + ManualIn(500) - ManualOut(200) - SupplierPayment(2500) = 5800m
        var movements = await db.CashMovements.AsNoTracking().Where(x => x.CashSessionId == session.Id).ToListAsync();
        var netMovements = movements.Sum(x => x.SignedAmount);
        var expectedCash = session.OpeningCash + netMovements;

        Assert.Equal(5800m, expectedCash);
        Assert.Equal(800m, netMovements);

        // B. Supplier Accounts Payable Reconciliation:
        // Initial Purchase: +10,000m payable
        // Supplier Payment: -2,500m payable
        // Net Outstanding Payable = 7,500m
        var saeEntries = await db.SupplierAccountEntries.AsNoTracking().Where(x => x.SupplierId == fixture.SupplierId).ToListAsync();
        var netPayable = saeEntries.Sum(e => e.Direction == SupplierAccountDirection.IncreasePayable ? e.Amount : -e.Amount);
        Assert.Equal(7500m, netPayable);

        var calculatedBalance = await services.GetRequiredService<ISupplierAccountRepository>().GetCurrentBalanceAsync(fixture.SupplierId, default);
        Assert.Equal(7500m, calculatedBalance);

        // Invariant: Unexplained cash or payable difference is exactly 0.00
        Assert.Equal(0m, netPayable - 7500m);
    }

    [Fact]
    public async Task OwnerReconciliation_ProfitAndLossRecovery_ExactEquationAndZeroDiscrepancy()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var readService = services.GetRequiredService<IReportingReadService>();
        var baseline = await readService.GetSnapshotAsync(ReportingPeriodKind.Daily, today, today.Month, today.Year, default);

        var fixture = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db, defaultSalePrice: 200m);
        await using var cashSession = await EnsureSingleOpenCashSessionAsync(db, fixture.ActorId, openingCash: 10000m);

        // 1. Purchase 20 units @100m
        var purchase = await services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new CreatePurchaseCommand(
            fixture.SupplierId, "RECON-PROFIT-" + Guid.NewGuid(), today, null, 0m,
            PurchaseSettlementMode.External, fixture.ActorId, Guid.NewGuid(),
            [new CreatePurchaseLineInput(fixture.ProductId, fixture.ProductUnitId, 20m, 100m, 200m, [])]), default);
        Assert.True(purchase.IsSuccess, purchase.Error?.Message);

        // 2. Sale 10 units @200m -> NetSales = +2000m, COGS = +1000m, GrossProfit = +1000m
        var sale = await services.GetRequiredService<CompleteSaleHandler>().HandleAsync(new CompleteSaleCommand(
            Guid.NewGuid(), null, fixture.ActorId, cashSession.Id, 0m, SalePaymentMethod.Cash, 2000m, "PROFIT-SALE", null,
            [new CompleteSaleLineInput(fixture.ProductId, fixture.ProductUnitId, 10m, 200m, [])]), default);
        Assert.True(sale.IsSuccess, sale.Error?.Message);

        // 3. Operational Expense: -150m
        var expenseCategory = new ExpenseCategory { Name = "ReconCategory-" + Guid.NewGuid().ToString("N"), IsActive = true };
        db.ExpenseCategories.Add(expenseCategory);
        await db.SaveChangesAsync();

        var expense = await services.GetRequiredService<PostExpenseHandler>().HandleAsync(new PostExpenseCommand(
            Guid.NewGuid(), expenseCategory.Id, null, today, 150m, ExpensePaymentMethod.Cash,
            "Stationery and printer ink", "Office", fixture.ActorId), default);
        Assert.True(expense.IsSuccess, expense.Error?.Message);

        // 4. Recognized Inventory Loss: Transfer 2 units Sellable -> Damaged -> Scrap (canonical path) -> Loss = 2 * 100 = 200m
        var toDamaged = await services.GetRequiredService<TransferInventoryConditionHandler>().HandleAsync(
            new TransferInventoryConditionCommand(
                fixture.ProductId, InventoryBucket.Sellable, InventoryBucket.Damaged, 2m, fixture.ActorId,
                "Damaged in transit"), default);
        Assert.True(toDamaged.IsSuccess, toDamaged.Error?.Message);

        var scrap = await services.GetRequiredService<TransferInventoryConditionHandler>().HandleAsync(
            new TransferInventoryConditionCommand(
                fixture.ProductId, InventoryBucket.Damaged, InventoryBucket.Scrap, 2m, fixture.ActorId,
                "Broken in transport - write off to scrap"), default);
        Assert.True(scrap.IsSuccess, scrap.Error?.Message);
        db.ChangeTracker.Clear();

        // 5. Query Snapshot after events
        var snapshot = await readService.GetSnapshotAsync(ReportingPeriodKind.Daily, today, today.Month, today.Year, default);

        var deltaNetSales = snapshot.NetSales - baseline.NetSales;
        var deltaNetCogs = snapshot.NetCOGS - baseline.NetCOGS;
        var deltaGrossProfit = snapshot.GrossProfit - baseline.GrossProfit;
        var deltaExpenses = snapshot.Expenses - baseline.Expenses;
        var deltaRecognizedLoss = snapshot.InventoryLoss - baseline.InventoryLoss;
        var deltaNetProfit = snapshot.NetProfit - baseline.NetProfit;

        // Verify Authoritative Reporting Equation:
        // deltaGrossProfit = deltaNetSales - deltaNetCogs = 2000 - 1000 = 1000
        Assert.Equal(2000m, deltaNetSales);
        Assert.Equal(1000m, deltaNetCogs);
        Assert.Equal(1000m, deltaGrossProfit);

        // deltaExpenses = 150m
        Assert.Equal(150m, deltaExpenses);

        // deltaRecognizedLoss = 200m
        Assert.Equal(200m, deltaRecognizedLoss);

        // deltaNetProfit = GrossProfit(1000) - Expenses(150) - Loss(200) + RecoveryGain(0) + WarrantyGain(0) = 650m
        var expectedNetProfit = deltaGrossProfit - deltaExpenses - deltaRecognizedLoss;
        Assert.Equal(650m, deltaNetProfit);
        Assert.Equal(expectedNetProfit, deltaNetProfit);

        // Invariant: Unexplained profit variance is exactly 0.00
        Assert.Equal(0m, deltaNetProfit - expectedNetProfit);
    }

    // NEW_COVERAGE: one coherent owner chain, with independent primitive oracle,
    // actual nonzero recovery gains, and separate real Thaka economics in all periods.
    [Theory]
    [InlineData(ReportingPeriodKind.Daily)]
    [InlineData(ReportingPeriodKind.Monthly)]
    [InlineData(ReportingPeriodKind.Yearly)]
    public async Task OwnerReconciliation_AllPrimitiveLinesWithNonzeroRecoveriesAndThaka(ReportingPeriodKind period)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var reports = services.GetRequiredService<IReportingReadService>();
        var baseline = await reports.GetSnapshotAsync(period, today, today.Month, today.Year, default);
        var bulk = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db, defaultSalePrice: 200m);
        var exact = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db, defaultSalePrice: 200m);
        await using var drawer = await EnsureSingleOpenCashSessionAsync(db, bulk.ActorId, 1000m);
        var customer = await Phase2PostgresTestHarness.SeedCustomerAsync(db, "H04 owner customer");
        var purchaseBulk = await services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new(
            bulk.SupplierId, "H04-B-" + Guid.NewGuid(), today, null, 0m, PurchaseSettlementMode.External,
            bulk.ActorId, Guid.NewGuid(), [new CreatePurchaseLineInput(bulk.ProductId, bulk.ProductUnitId, 20m, 100m, 200m, [])],
            InitialPaymentAmount: 0m), default);
        Assert.True(purchaseBulk.IsSuccess, purchaseBulk.Error?.Message);
        var purchaseExact = await services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new(
            exact.SupplierId, "H04-E-" + Guid.NewGuid(), today, null, 0m, PurchaseSettlementMode.External,
            exact.ActorId, Guid.NewGuid(), [new CreatePurchaseLineInput(exact.ProductId, exact.ProductUnitId, 2m, 100m, 200m,
                [new SerializedIdentityInput("H04-" + Guid.NewGuid()), new SerializedIdentityInput("H04-" + Guid.NewGuid())])],
            InitialPaymentAmount: 0m), default);
        Assert.True(purchaseExact.IsSuccess, purchaseExact.Error?.Message);
        db.ChangeTracker.Clear();
        var originals = await db.InventoryUnits.Where(x => x.ProductId == exact.ProductId).OrderBy(x => x.Id).ToArrayAsync();
        var originalMissingLot = originals[0].InventoryLotId;
        var saleOperation = Guid.NewGuid();
        var sale = await services.GetRequiredService<CompleteSaleHandler>().HandleAsync(new(
            saleOperation, customer.Id, bulk.ActorId, drawer.Id, 0m, SalePaymentMethod.Cash, 1000m, "H04 sale", null,
            [new CompleteSaleLineInput(bulk.ProductId, bulk.ProductUnitId, 5m, 200m, [])]), default);
        Assert.True(sale.IsSuccess, sale.Error?.Message);
        db.ChangeTracker.Clear();
        var sold = await db.SaleItems.SingleAsync(x => x.SaleId == sale.Value!.SaleId);
        var returnOperation = Guid.NewGuid();
        var returned = await services.GetRequiredService<CreateSaleReturnHandler>().HandleAsync(new(
            sale.Value!.SaleId, "OTHER", "H04 genuine one-unit return", RefundMethod.Cash, bulk.ActorId, returnOperation,
            [new SaleReturnLineInput(sold.Id, 1m, SaleReturnDisposition.RestockSellable, [])]), default);
        Assert.True(returned.IsSuccess, returned.Error?.Message);
        var category = new ExpenseCategory { Name = "H04-" + Guid.NewGuid(), IsActive = true };
        db.ExpenseCategories.Add(category);
        await db.SaveChangesAsync();
        var expenseOperation = Guid.NewGuid();
        var expense = await services.GetRequiredService<PostExpenseHandler>().HandleAsync(new(
            expenseOperation, category.Id, null, today, 150m, ExpensePaymentMethod.Cash, "H04 real operating expense", null, bulk.ActorId), default);
        Assert.True(expense.IsSuccess, expense.Error?.Message);
        var damage = await services.GetRequiredService<TransferInventoryConditionHandler>().HandleAsync(new(
            bulk.ProductId, InventoryBucket.Sellable, InventoryBucket.Damaged, 2m, bulk.ActorId, "H04 damage"), default);
        Assert.True(damage.IsSuccess, damage.Error?.Message);
        var scrap = await services.GetRequiredService<TransferInventoryConditionHandler>().HandleAsync(new(
            bulk.ProductId, InventoryBucket.Damaged, InventoryBucket.Scrap, 2m, bulk.ActorId, "H04 scrap"), default);
        Assert.True(scrap.IsSuccess, scrap.Error?.Message);
        var missingOperation = Guid.NewGuid();
        var missing = await services.GetRequiredService<CreateStockAdjustmentHandler>().HandleAsync(new(
            StockAdjustmentMode.Delta, StockAdjustmentReason.Lost,
            [new StockAdjustmentItemCommand(exact.ProductId, null, StockAdjustmentDirection.Decrease,
                InventoryBucket.Sellable, 1m, null, InventoryUnitIds: [originals[0].Id])], exact.ActorId, missingOperation), default);
        Assert.True(missing.IsSuccess, missing.Error?.Message);
        db.ChangeTracker.Clear();
        var missingMovement = await db.InventoryMovements.SingleAsync(x => x.CorrelationId == missingOperation);
        var foundOperation = Guid.NewGuid();
        var found = await services.GetRequiredService<FoundInventoryUnitHandler>().HandleAsync(new(
            exact.ProductId, originals[0].Id, missingMovement.Id, InventoryBucket.Sellable,
            "H04 physically found", exact.ActorId, foundOperation), default);
        Assert.True(found.IsSuccess, found.Error?.Message);
        var warrantyDamage = await services.GetRequiredService<TransferInventoryConditionHandler>().HandleAsync(new(
            exact.ProductId, InventoryBucket.Sellable, InventoryBucket.Damaged, 1m, exact.ActorId,
            "H04 warranty defect", InventoryUnitIds: [originals[1].Id]), default);
        Assert.True(warrantyDamage.IsSuccess, warrantyDamage.Error?.Message);
        var sent = await services.GetRequiredService<SendShopStockToSupplierWarrantyHandler>().HandleAsync(new(
            exact.ProductId, InventoryBucket.Damaged, 1m, exact.SupplierId, null, "H04 actual credit",
            exact.ActorId, Guid.NewGuid(), [originals[1].Id]), default);
        Assert.True(sent.IsSuccess, sent.Error?.Message);
        var creditOperation = Guid.NewGuid();
        var credited = await services.GetRequiredService<ReceiveShopStockWarrantyHandler>().HandleAsync(new(
            sent.Value, WarrantyResolutionType.Credited, exact.ActorId, [originals[1].Id], null,
            "H04 nonzero recovery", creditOperation, SupplierCreditAmount: 140m, ResolvedQuantity: 1m), default);
        Assert.True(credited.IsSuccess, credited.Error?.Message);
        var supplierPayment = await services.GetRequiredService<CreateSupplierPaymentHandler>().HandleAsync(new(
            bulk.SupplierId, 300m, SupplierPaymentPurpose.Settlement, SupplierSettlementMethod.CashDrawer,
            bulk.ActorId, Guid.NewGuid(), Note: "H04 payable reduction"), default);
        Assert.True(supplierPayment.IsSuccess, supplierPayment.Error?.Message);
        var project = await services.GetRequiredService<CreateThakaProjectHandler>().HandleAsync(new(
            customer.Id, "H04 actual installation", "Owner test site", null, today, bulk.ActorId, Guid.NewGuid(), Guid.NewGuid()), default);
        Assert.True(project.IsSuccess, project.Error?.Message);
        var issueA = await services.GetRequiredService<IssueThakaMaterialHandler>().HandleAsync(new(
            Guid.NewGuid(), project.Value, bulk.ActorId, "H04 retained material",
            [new IssueThakaMaterialLineInput(bulk.ProductId, bulk.ProductUnitId, 2m, 200m, [])]), default);
        Assert.True(issueA.IsSuccess, issueA.Error?.Message);
        var issueB = await services.GetRequiredService<IssueThakaMaterialHandler>().HandleAsync(new(
            Guid.NewGuid(), project.Value, bulk.ActorId, "H04 unused material",
            [new IssueThakaMaterialLineInput(bulk.ProductId, bulk.ProductUnitId, 1m, 200m, [])]), default);
        Assert.True(issueB.IsSuccess, issueB.Error?.Message);
        var reversal = await services.GetRequiredService<ReverseThakaMaterialHandler>().HandleAsync(new(
            Guid.NewGuid(), project.Value, issueB.Value!.MaterialIssueId, "H04 genuine material return", bulk.ActorId), default);
        Assert.True(reversal.IsSuccess, reversal.Error?.Message);
        var thakaPayment = await services.GetRequiredService<RecordThakaPaymentHandler>().HandleAsync(new(
            Guid.NewGuid(), project.Value, 150m, ThakaPaymentMethod.Cash, "H04 receipt", null, bulk.ActorId), default);
        Assert.True(thakaPayment.IsSuccess, thakaPayment.Error?.Message);
        var settlement = await services.GetRequiredService<SettleThakaHandler>().HandleAsync(new(
            Guid.NewGuid(), project.Value, 50m, 200m, ThakaPaymentMethod.Cash, "H04 final receipt", bulk.ActorId), default);
        Assert.True(settlement.IsSuccess, settlement.Error?.Message);
        var manualIn = await services.GetRequiredService<RecordManualCashMovementHandler>().HandleAsync(new(
            CashMovementDirection.In, 100m, bulk.ActorId, "H04 owned drawer float", null), default);
        Assert.True(manualIn.IsSuccess, manualIn.Error?.Message);
        var manualOut = await services.GetRequiredService<RecordManualCashMovementHandler>().HandleAsync(new(
            CashMovementDirection.Out, 50m, bulk.ActorId, "H04 owned drawer correction", null), default);
        Assert.True(manualOut.IsSuccess, manualOut.Error?.Message);

        db.ChangeTracker.Clear();
        // Independent oracle starts from persisted primitive facts, never a report DTO,
        // case.RecoveryDifference, or mutation-handler result amount.
        var productIds = new[] { bulk.ProductId, exact.ProductId };
        var movements = await db.InventoryMovements.AsNoTracking().Where(x => productIds.Contains(x.ProductId)).ToArrayAsync();
        var movementIds = movements.Select(x => x.Id).ToArray();
        var consumptions = await db.InventoryLotConsumptions.AsNoTracking().Where(x => movementIds.Contains(x.MovementId)).ToArrayAsync();
        var purchaseItems = await db.PurchaseItems.AsNoTracking().Where(x => productIds.Contains(x.ProductId)).ToArrayAsync();
        var saleItems = await db.SaleItems.AsNoTracking().Where(x => x.SaleId == sale.Value!.SaleId).ToArrayAsync();
        var returnItems = await db.SaleReturnItems.AsNoTracking().Where(x => x.SaleReturnId == returned.Value!.SaleReturnId).ToArrayAsync();
        var returnFact = await db.SaleReturns.AsNoTracking().SingleAsync(x => x.Id == returned.Value!.SaleReturnId);
        var netSales = saleItems.Sum(x => x.NetLineTotal) - returnFact.RefundAmount;
        var netCogs = saleItems.Sum(x => x.TotalCostSnapshot) - returnItems.Sum(x => x.CostReversalAmount);
        var expenseFact = await db.Expenses.AsNoTracking().SingleAsync(x => x.Id == expense.Value!.ExpenseId);
        var expenseValue = expenseFact.Amount;
        var scrapCost = Assert.Single(movements, x => x.Id == scrap.Value).RecognizedLossAmount;
        var missingCost = consumptions.Where(x => x.MovementId == missingMovement.Id).Sum(x => x.TotalCostSnapshot);
        var inventoryLoss = scrapCost + missingCost;
        var foundFact = Assert.Single(movements, x => x.CorrelationId == foundOperation);
        Assert.Equal("InventoryLossRecoveryGain", foundFact.ReferenceType);
        Assert.Equal(missingMovement.Id, foundFact.ReferenceId);
        Assert.Equal(missingCost, missingMovement.RecognizedLossAmount);
        var recoveryValue = missingCost;
        var creditMovement = Assert.Single(movements, x => x.CorrelationId == creditOperation);
        var creditCost = consumptions.Where(x => x.MovementId == creditMovement.Id).Sum(x => x.TotalCostSnapshot);
        var creditEntry = await db.SupplierAccountEntries.AsNoTracking().SingleAsync(x => x.ReferenceId == creditMovement.Id);
        Assert.Equal("WarrantyResolution", creditEntry.ReferenceType);
        Assert.Equal(creditOperation, creditEntry.ClientOperationId);
        var warrantyGain = creditEntry.Amount - creditCost;
        var primitiveProfit = netSales - netCogs - expenseValue - inventoryLoss + recoveryValue + warrantyGain;
        Assert.Equal(800m, netSales);
        Assert.Equal(400m, netCogs);
        Assert.Equal(150m, expenseValue);
        Assert.Equal(300m, inventoryLoss);
        Assert.Equal(100m, recoveryValue);
        Assert.Equal(40m, warrantyGain);
        Assert.Equal(90m, primitiveProfit);
        Assert.Equal(scrapCost, Assert.Single(movements, x => x.Id == scrap.Value).RecognizedLossAmount);

        var issues = await db.ThakaMaterialIssues.AsNoTracking().Where(x => x.ProjectId == project.Value).ToArrayAsync();
        var issueIds = issues.Select(x => x.Id).ToArray();
        var issueItems = await db.ThakaMaterialIssueItems.AsNoTracking().Where(x => issueIds.Contains(x.MaterialIssueId)).ToArrayAsync();
        var reversed = await db.ThakaMaterialReversals.AsNoTracking().SingleAsync(x => x.ProjectId == project.Value);
        var retainedCharge = issueItems.Sum(x => x.LineCharge) - reversed.ReversedCharge;
        var retainedCost = issueItems.Sum(x => x.TotalCostSnapshot) - reversed.RestoredCost;
        var settled = await db.ThakaSettlements.AsNoTracking().SingleAsync(x => x.ProjectId == project.Value);
        var thakaCollected = await db.ThakaPayments.AsNoTracking().Where(x => x.ProjectId == project.Value).SumAsync(x => x.Amount);
        Assert.Equal(400m, retainedCharge);
        Assert.Equal(200m, retainedCost);
        Assert.Equal(350m, thakaCollected);
        Assert.Equal(50m, settled.SettlementDiscount);
        Assert.Equal(0m, retainedCharge - settled.SettlementDiscount - thakaCollected);
        Assert.Equal(150m, retainedCharge - retainedCost - settled.SettlementDiscount);
        Assert.Equal(ThakaProjectStatus.Settled, (await db.ThakaProjects.SingleAsync(x => x.Id == project.Value)).Status);

        var receiptQuantity = purchaseItems.Sum(x => x.BaseQuantity);
        var netSoldQuantity = saleItems.Sum(x => x.BaseQuantity) - returnItems.Sum(x => x.BaseQuantity);
        var retainedIssueQuantity = issueItems.Sum(x => x.BaseQuantity) -
            issueItems.Where(x => x.MaterialIssueId == reversed.MaterialIssueId).Sum(x => x.BaseQuantity);
        var primitiveOwned = receiptQuantity - netSoldQuantity - 2m - 1m - retainedIssueQuantity - 1m + 1m;
        // receipt - net sale - scrap - credited unit - Thaka - Missing + Found
        Assert.Equal(13m, primitiveOwned);
        var primitiveCarrying = purchaseItems.Sum(x => x.EffectiveLineCost) - netCogs - scrapCost - creditCost - retainedCost - missingCost + recoveryValue;
        Assert.Equal(1300m, primitiveCarrying);
        var states = await db.ProductCostStates.AsNoTracking().Where(x => productIds.Contains(x.ProductId)).ToArrayAsync();
        Assert.Equal(primitiveOwned, states.Sum(x => x.CostedQty));
        Assert.Equal(primitiveCarrying, states.Sum(x => x.TotalInventoryCost));
        var balances = await db.StockBalances.AsNoTracking().Where(x => productIds.Contains(x.ProductId)).ToArrayAsync();
        Assert.Equal(primitiveOwned, balances.Sum(x => x.SellableQty + x.DamagedQty + x.DefectiveQty + x.WithSupplierQty));
        var effects = await db.InventoryMovementEffects.AsNoTracking().Where(x => movementIds.Contains(x.MovementId)).ToArrayAsync();
        foreach (var balance in balances)
        {
            var ids = movements.Where(x => x.ProductId == balance.ProductId).Select(x => x.Id).ToHashSet();
            foreach (var bucket in Enum.GetValues<InventoryBucket>())
            {
                Assert.Equal(balance.Get(bucket), effects.Where(x => ids.Contains(x.MovementId) && x.StockBucket == bucket).Sum(x => x.QuantityDelta));
            }
        }
        var foundUnit = await db.InventoryUnits.AsNoTracking().SingleAsync(x => x.Id == originals[0].Id);
        Assert.Equal(InventoryUnitStatus.InStock, foundUnit.Status);
        Assert.Equal(100m, foundUnit.AcquisitionCost);
        Assert.NotEqual(originalMissingLot, foundUnit.InventoryLotId);
        Assert.Equal(InventoryUnitStatus.SupplierReturned, (await db.InventoryUnits.AsNoTracking().SingleAsync(x => x.Id == originals[1].Id)).Status);
        var warrantyCase = await db.ShopStockWarrantyCases.AsNoTracking().SingleAsync(x => x.Id == sent.Value);
        Assert.Equal(ShopWarrantyCaseStatus.Closed, warrantyCase.Status);
        var supplierIds = new[] { bulk.SupplierId, exact.SupplierId };
        var entries = await db.SupplierAccountEntries.AsNoTracking().Where(x => supplierIds.Contains(x.SupplierId)).ToArrayAsync();
        var primitivePayable = purchaseItems.Sum(x => x.EffectiveLineCost) - 300m - creditEntry.Amount;
        Assert.Equal(1760m, primitivePayable);
        Assert.Equal(primitivePayable, entries.Sum(x => x.Direction == SupplierAccountDirection.IncreasePayable ? x.Amount : -x.Amount));
        var cash = await db.CashMovements.AsNoTracking().Where(x => x.CashSessionId == drawer.Id).ToArrayAsync();
        var primitiveCash = drawer.OpeningCash + saleItems.Sum(x => x.NetLineTotal) - returnFact.RefundAmount - expenseValue - 300m + thakaCollected + 100m - 50m;
        Assert.Equal(1750m, primitiveCash);
        Assert.Equal(primitiveCash, drawer.OpeningCash + cash.Sum(x => x.SignedAmount));
        foreach (var operation in new[] { saleOperation, returnOperation, expenseOperation, missingOperation, foundOperation, creditOperation })
        {
            Assert.True(await db.BusinessAuditEvents.AnyAsync(x => x.CorrelationId == operation), "Missing business audit for " + operation);
        }

        var snapshot = await reports.GetSnapshotAsync(period, today, today.Month, today.Year, default);
        Assert.Equal(netSales, snapshot.NetSales - baseline.NetSales);
        Assert.Equal(netCogs, snapshot.NetCOGS - baseline.NetCOGS);
        Assert.Equal(netSales - netCogs, snapshot.GrossProfit - baseline.GrossProfit);
        Assert.Equal(expenseValue, snapshot.Expenses - baseline.Expenses);
        Assert.Equal(inventoryLoss, snapshot.InventoryLoss - baseline.InventoryLoss);
        Assert.Equal(recoveryValue, snapshot.InventoryLossRecoveryGain - baseline.InventoryLossRecoveryGain);
        Assert.Equal(warrantyGain, snapshot.WarrantyRecoveryGain - baseline.WarrantyRecoveryGain);
        Assert.Equal(primitiveProfit, snapshot.NetProfit - baseline.NetProfit);
        Assert.Equal(retainedCharge, snapshot.ThakaMaterial - baseline.ThakaMaterial);
        Assert.Equal(0m, snapshot.NetProfit - baseline.NetProfit - primitiveProfit);
    }
}
