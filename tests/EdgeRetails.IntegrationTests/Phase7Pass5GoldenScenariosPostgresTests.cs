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
public sealed class Phase7Pass5GoldenScenariosPostgresTests
{
    private static async Task<OwnedCashSession> CreateOwnedCashSessionAsync(
        EdgeRetailsDbContext db,
        Guid actorId,
        decimal openingCash = 10000m)
    {
        // A fixture may never adopt or close another test's open business session.
        var session = await Phase2PostgresTestHarness.SeedOpenCashSessionAsync(db, actorId, openingCash);
        return new OwnedCashSession(db, session);
    }

    private sealed class OwnedCashSession(EdgeRetailsDbContext db, CashSession session) : IAsyncDisposable
    {
        public CashSession Session { get; } = session;

        public async ValueTask DisposeAsync()
        {
            // Direct cleanup avoids replaying pending tracked mutations after a failed assertion/command.
            await db.CashSessions.Where(x => x.Id == Session.Id && x.Status == CashSessionStatus.Open)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(x => x.Status, CashSessionStatus.Closed)
                    .SetProperty(x => x.ClosedAt, DateTimeOffset.UtcNow));
        }
    }

    private sealed record GoldenEndpoint(
        decimal OwnedQty,
        decimal InventoryValue,
        decimal Cash,
        decimal NetSales,
        decimal NetCOGS,
        decimal Expenses,
        decimal InventoryLoss,
        decimal WarrantyRecoveryGain,
        decimal NetProfit);

    private static async Task<decimal> SupplierPositionAsync(EdgeRetailsDbContext db, Guid supplierId)
    {
        return await db.SupplierAccountEntries.AsNoTracking()
            .Where(x => x.SupplierId == supplierId)
            .SumAsync(x => x.Direction == SupplierAccountDirection.IncreasePayable ? x.Amount : -x.Amount);
    }

    private static async Task<decimal> SessionCashAsync(EdgeRetailsDbContext db, Guid sessionId)
    {
        var session = await db.CashSessions.AsNoTracking().SingleAsync(x => x.Id == sessionId);
        var movements = await db.CashMovements.AsNoTracking().Where(x => x.CashSessionId == sessionId).ToListAsync();
        return session.OpeningCash + movements.Sum(x => x.SignedAmount);
    }

    private sealed class ScenarioClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow => utcNow;
        public DateOnly ShopDate => DateOnly.FromDateTime(utcNow.UtcDateTime);
    }

    private static async Task AssertGoldenEndpointAsync(
        IServiceProvider services,
        Guid productId,
        Guid actorId,
        Guid cashSessionId,
        GoldenEndpoint expected)
    {
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        var balance = await db.StockBalances.AsNoTracking().SingleOrDefaultAsync(x => x.ProductId == productId);
        var costState = await db.ProductCostStates.AsNoTracking().SingleOrDefaultAsync(x => x.ProductId == productId);
        var ownedQty = balance is null ? 0m : (balance.SellableQty + balance.DamagedQty + balance.DefectiveQty + balance.WithSupplierQty);
        var invValue = costState?.TotalInventoryCost ?? 0m;
        var cash = await SessionCashAsync(db, cashSessionId);

        var clock = services.GetRequiredService<IClock>();
        var today = clock.ShopDate;
        var reporting = services.GetRequiredService<IReportingReadService>();
        var snapshot = await reporting.GetSnapshotAsync(ReportingPeriodKind.Daily, today, today.Month, today.Year, default);

        Assert.Equal(expected.OwnedQty, ownedQty);
        Assert.Equal(expected.InventoryValue, invValue);
        Assert.Equal(expected.Cash, cash);
        Assert.Equal(expected.NetSales, snapshot.NetSales);
        Assert.Equal(expected.NetCOGS, snapshot.NetCOGS);
        Assert.Equal(expected.Expenses, snapshot.Expenses);
        Assert.Equal(expected.InventoryLoss, snapshot.InventoryLoss);
        Assert.Equal(expected.WarrantyRecoveryGain, snapshot.WarrantyRecoveryGain);
        Assert.Equal(expected.NetProfit, snapshot.NetProfit);
    }

    [Fact]
    public async Task GoldenScenarioA_SerializedFullLifecycle_IntakeSaleReturnExchangeWarrantyTerminalSale()
    {
        var clock = new ScenarioClock(new DateTimeOffset(2035, 1, 10, 10, 0, 0, TimeSpan.Zero));
        await using var provider = Phase2PostgresTestHarness.BuildProvider(clock);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);

        var seed = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db, defaultSalePrice: 5000m, defaultWarrantyMonths: 12);
        await using var ownedCashSession = await CreateOwnedCashSessionAsync(db, seed.ActorId, openingCash: 10000m);
        var cashSession = ownedCashSession.Session;

        // 1. INTAKE: Receive 3 serialized units (Unit 1 @3000, Unit 2 @3200, Unit 3 @3400)
        async Task<InventoryUnit> ReceiveUnitAsync(decimal cost, string serialNumber)
        {
            var prior = await db.InventoryUnits.Where(x => x.ProductId == seed.ProductId).Select(x => x.Id).ToArrayAsync();
            var purchase = await services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new CreatePurchaseCommand(
                seed.SupplierId, "P5-GOLDEN-A-" + Guid.NewGuid(), clock.ShopDate, null, 0m,
                PurchaseSettlementMode.External, seed.ActorId, Guid.NewGuid(),
                [new CreatePurchaseLineInput(seed.ProductId, seed.ProductUnitId, 1m, cost, 5000m,
                    [new SerializedIdentityInput(serialNumber)])],
                InitialPaymentAmount: 0m), default);
            Assert.True(purchase.IsSuccess, purchase.Error?.Message);
            db.ChangeTracker.Clear();
            return await db.InventoryUnits.SingleAsync(x => x.ProductId == seed.ProductId && !prior.Contains(x.Id));
        }

        var unit1 = await ReceiveUnitAsync(3000m, "SN-GA-1");
        var unit2 = await ReceiveUnitAsync(3200m, "SN-GA-2");
        var unit3 = await ReceiveUnitAsync(3400m, "SN-GA-3");

        var costState1 = await db.ProductCostStates.AsNoTracking().SingleAsync(x => x.ProductId == seed.ProductId);
        Assert.Equal(3m, costState1.CostedQty);
        Assert.Equal(9600m, costState1.TotalInventoryCost); // 3000 + 3200 + 3400
        Assert.Equal(3m, (await db.StockBalances.AsNoTracking().SingleAsync(x => x.ProductId == seed.ProductId)).SellableQty);

        var supplierPayment = await services.GetRequiredService<CreateSupplierPaymentHandler>().HandleAsync(
            new CreateSupplierPaymentCommand(seed.SupplierId, 2000m, SupplierPaymentPurpose.Settlement,
                SupplierSettlementMethod.CashDrawer, seed.ActorId, Guid.NewGuid(), Note: "Golden A receipt liability settlement"), default);
        Assert.True(supplierPayment.IsSuccess, supplierPayment.Error?.Message);
        db.ChangeTracker.Clear();
        Assert.Equal(9600m, await db.SupplierAccountEntries.Where(x => x.SupplierId == seed.SupplierId && x.EntryType == SupplierAccountEntryType.Purchase).SumAsync(x => x.Amount));
        Assert.Equal(7600m, await SupplierPositionAsync(db, seed.SupplierId));

        // 2. SALE: Complete cash sale of Unit 1
        var sale1 = await services.GetRequiredService<CompleteSaleHandler>().HandleAsync(new CompleteSaleCommand(
            Guid.NewGuid(), null, seed.ActorId, cashSession.Id, 0m, SalePaymentMethod.Cash, 5000m, "CASH-1", null,
            [new CompleteSaleLineInput(seed.ProductId, seed.ProductUnitId, 1m, 5000m, [unit1.Id])]), default);
        Assert.True(sale1.IsSuccess, sale1.Error?.Message);
        Assert.NotNull(sale1.Value);
        db.ChangeTracker.Clear();

        Assert.Equal(InventoryUnitStatus.Sold, (await db.InventoryUnits.AsNoTracking().SingleAsync(x => x.Id == unit1.Id)).Status);
        var costState2 = await db.ProductCostStates.AsNoTracking().SingleAsync(x => x.ProductId == seed.ProductId);
        Assert.Equal(2m, costState2.CostedQty);
        Assert.Equal(6600m, costState2.TotalInventoryCost); // 9600 - 3000

        // 3. SALE RETURN: Customer returns Unit 1 due to defect
        var sold1Item = await db.SaleItems.AsNoTracking().SingleAsync(x => x.SaleId == sale1.Value!.SaleId);
        var return1 = await services.GetRequiredService<CreateSaleReturnHandler>().HandleAsync(new CreateSaleReturnCommand(
            sale1.Value.SaleId, "DEFECTIVE", "Defect discovered by customer", RefundMethod.Cash,
            seed.ActorId, Guid.NewGuid(),
            [new SaleReturnLineInput(sold1Item.Id, 1m, SaleReturnDisposition.Defective, [unit1.Id])]), default);
        Assert.True(return1.IsSuccess, return1.Error?.Message);
        db.ChangeTracker.Clear();

        Assert.Equal(InventoryUnitStatus.Defective, (await db.InventoryUnits.AsNoTracking().SingleAsync(x => x.Id == unit1.Id)).Status);
        var costState3 = await db.ProductCostStates.AsNoTracking().SingleAsync(x => x.ProductId == seed.ProductId);
        Assert.Equal(3m, costState3.CostedQty);
        Assert.Equal(9600m, costState3.TotalInventoryCost); // 3000 carrying value restored

        // 4. COMMERCIAL EXCHANGE: Customer purchases Unit 2, then exchanges it for Unit 3
        var sale2 = await services.GetRequiredService<CompleteSaleHandler>().HandleAsync(new CompleteSaleCommand(
            Guid.NewGuid(), null, seed.ActorId, cashSession.Id, 0m, SalePaymentMethod.Cash, 5000m, "CASH-2", null,
            [new CompleteSaleLineInput(seed.ProductId, seed.ProductUnitId, 1m, 5000m, [unit2.Id])]), default);
        Assert.True(sale2.IsSuccess, sale2.Error?.Message);
        Assert.NotNull(sale2.Value);
        db.ChangeTracker.Clear();

        var sold2Item = await db.SaleItems.AsNoTracking().SingleAsync(x => x.SaleId == sale2.Value!.SaleId);
        var exchangeIntent = new CommercialExchangeCommand(
            Guid.NewGuid(), sale2.Value.SaleId, seed.ActorId, null, null,
            "UPGRADE", "Exchanging Unit 2 for Unit 3",
            [new SaleReturnLineInput(sold2Item.Id, 1m, SaleReturnDisposition.RestockSellable, [unit2.Id])],
            [new CompleteSaleLineInput(seed.ProductId, seed.ProductUnitId, 1m, 5000m, [unit3.Id])],
            0m, SalePaymentMethod.Other, 0m, "EXCHANGE-BALANCED");

        var exchangeResult = await services.GetRequiredService<CommercialExchangeHandler>().HandleAsync(exchangeIntent, default);
        Assert.True(exchangeResult.IsSuccess, exchangeResult.Error?.Message);
        Assert.NotNull(exchangeResult.Value);
        Assert.Equal(0m, exchangeResult.Value!.NetDifference);
        db.ChangeTracker.Clear();

        Assert.Equal(InventoryUnitStatus.InStock, (await db.InventoryUnits.AsNoTracking().SingleAsync(x => x.Id == unit2.Id)).Status);
        Assert.Equal(InventoryUnitStatus.Sold, (await db.InventoryUnits.AsNoTracking().SingleAsync(x => x.Id == unit3.Id)).Status);

        // 5. DAMAGE: Transfer condition of Unit 2 from Sellable to Damaged
        var damageTransfer = await services.GetRequiredService<TransferInventoryConditionHandler>().HandleAsync(
            new TransferInventoryConditionCommand(
                seed.ProductId, InventoryBucket.Sellable, InventoryBucket.Damaged, 1m, seed.ActorId,
                "Warehouse handling damage", InventoryUnitIds: [unit2.Id]), default);
        Assert.True(damageTransfer.IsSuccess, damageTransfer.Error?.Message);
        db.ChangeTracker.Clear();

        Assert.Equal(InventoryUnitStatus.Damaged, (await db.InventoryUnits.AsNoTracking().SingleAsync(x => x.Id == unit2.Id)).Status);
        var stockAfterDamage = await db.StockBalances.AsNoTracking().SingleAsync(x => x.ProductId == seed.ProductId);
        Assert.Equal(0m, stockAfterDamage.SellableQty);
        Assert.Equal(1m, stockAfterDamage.DamagedQty);
        Assert.Equal(1m, stockAfterDamage.DefectiveQty); // unit 1

        // 6. SHOP WARRANTY SEND: Send Unit 2 to supplier for warranty repair
        var unit2Lot = await db.InventoryLots.AsNoTracking().SingleAsync(x => x.Id == unit2.InventoryLotId!.Value);
        var warrantySend = await services.GetRequiredService<SendShopStockToSupplierWarrantyHandler>().HandleAsync(
            new SendShopStockToSupplierWarrantyCommand(
                seed.ProductId, InventoryBucket.Damaged, 1m, seed.SupplierId, unit2Lot.PurchaseItemId!.Value,
                "Factory warranty repair for damaged unit", seed.ActorId, Guid.NewGuid(), [unit2.Id]), default);
        Assert.True(warrantySend.IsSuccess, warrantySend.Error?.Message);
        var warrantyCaseId = warrantySend.Value;
        db.ChangeTracker.Clear();

        Assert.Equal(InventoryUnitStatus.WithSupplier, (await db.InventoryUnits.AsNoTracking().SingleAsync(x => x.Id == unit2.Id)).Status);
        var warrantyCase = await db.ShopStockWarrantyCases.AsNoTracking().SingleAsync(x => x.Id == warrantyCaseId);
        Assert.Equal(ShopWarrantyCaseStatus.WithSupplier, warrantyCase.Status);

        // 7. WARRANTY RESOLUTION: Supplier returns repaired Unit 2
        var warrantyReceive = await services.GetRequiredService<ReceiveShopStockWarrantyHandler>().HandleAsync(
            new ReceiveShopStockWarrantyCommand(
                warrantyCaseId, WarrantyResolutionType.Repaired, seed.ActorId,
                [unit2.Id], null, "Unit repaired successfully by manufacturer", Guid.NewGuid()), default);
        Assert.True(warrantyReceive.IsSuccess, warrantyReceive.Error?.Message);
        db.ChangeTracker.Clear();

        Assert.Equal(InventoryUnitStatus.InStock, (await db.InventoryUnits.AsNoTracking().SingleAsync(x => x.Id == unit2.Id)).Status);
        var warrantyCaseClosed = await db.ShopStockWarrantyCases.AsNoTracking().SingleAsync(x => x.Id == warrantyCaseId);
        Assert.Equal(ShopWarrantyCaseStatus.Closed, warrantyCaseClosed.Status);

        // 8. TERMINAL SALE: Final sale of the repaired Unit 2
        var finalSale = await services.GetRequiredService<CompleteSaleHandler>().HandleAsync(new CompleteSaleCommand(
            Guid.NewGuid(), null, seed.ActorId, cashSession.Id, 0m, SalePaymentMethod.Cash, 5000m, "CASH-FINAL", null,
            [new CompleteSaleLineInput(seed.ProductId, seed.ProductUnitId, 1m, 5000m, [unit2.Id])]), default);
        Assert.True(finalSale.IsSuccess, finalSale.Error?.Message);
        db.ChangeTracker.Clear();

        Assert.Equal(InventoryUnitStatus.Sold, (await db.InventoryUnits.AsNoTracking().SingleAsync(x => x.Id == unit2.Id)).Status);
        var finalStock = await db.StockBalances.AsNoTracking().SingleAsync(x => x.ProductId == seed.ProductId);
        Assert.Equal(0m, finalStock.SellableQty);
        Assert.Equal(1m, finalStock.DefectiveQty); // Unit 1 remains defective in inventory
        await AssertGoldenEndpointAsync(services, seed.ProductId, seed.ActorId, cashSession.Id,
            new GoldenEndpoint(1m, 3000m, 18000m, 10000m, 6600m, 0m, 0m, 0m, 3400m));
        Assert.Equal(7600m, await SupplierPositionAsync(db, seed.SupplierId));
        Assert.Equal(3000m, (await db.InventoryUnits.AsNoTracking().SingleAsync(x => x.Id == unit1.Id)).AcquisitionCost);
        Assert.Equal(1, await db.ShopWarrantyResolutionAllocations.CountAsync(x => x.ResolutionMovementId ==
            db.InventoryMovements.Where(m => m.ReferenceId == warrantyCaseId && m.MovementType == InventoryMovementType.ReceiveRepairedFromSupplier).Select(m => m.Id).Single()));
        Assert.True(await db.BusinessAuditEvents.AnyAsync(x => x.EntityId == warrantyCaseId));
    }

    [Fact]
    public async Task GoldenScenarioB_PurchaseExpenseVoidPurchaseReturnSupplierRefund_ReconcilesOneChain()
    {
        var clock = new ScenarioClock(new DateTimeOffset(2035, 1, 11, 10, 0, 0, TimeSpan.Zero));
        await using var provider = Phase2PostgresTestHarness.BuildProvider(clock);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        var seed = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);
        await using var ownedCashSession = await CreateOwnedCashSessionAsync(db, seed.ActorId);
        var session = ownedCashSession.Session;

        var purchase = await services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new CreatePurchaseCommand(
            seed.SupplierId, "GOLDEN-B-" + Guid.NewGuid(), clock.ShopDate, null, 0m,
            PurchaseSettlementMode.External, seed.ActorId, Guid.NewGuid(),
            [new CreatePurchaseLineInput(seed.ProductId, seed.ProductUnitId, 10m, 100m, 150m, [])],
            InitialPaymentAmount: 0m), default);
        Assert.True(purchase.IsSuccess, purchase.Error?.Message);
        var payment = await services.GetRequiredService<CreateSupplierPaymentHandler>().HandleAsync(new CreateSupplierPaymentCommand(
            seed.SupplierId, 1000m, SupplierPaymentPurpose.Settlement, SupplierSettlementMethod.CashDrawer,
            seed.ActorId, Guid.NewGuid(), Note: "Settle the Golden B receipt"), default);
        Assert.True(payment.IsSuccess, payment.Error?.Message);
        db.ChangeTracker.Clear();
        Assert.Equal(0m, await SupplierPositionAsync(db, seed.SupplierId));

        var category = new ExpenseCategory { Name = "Golden B expense " + Guid.NewGuid(), IsActive = true };
        db.ExpenseCategories.Add(category);
        await db.SaveChangesAsync();
        var expense = await services.GetRequiredService<PostExpenseHandler>().HandleAsync(new PostExpenseCommand(
            Guid.NewGuid(), category.Id, null, DateOnly.FromDateTime(DateTime.UtcNow), 60m,
            ExpensePaymentMethod.Cash, "Receiving delivery expense", "GOLDEN-B", seed.ActorId), default);
        Assert.True(expense.IsSuccess, expense.Error?.Message);
        db.ChangeTracker.Clear();
        Assert.Equal(8940m, await SessionCashAsync(db, session.Id));
        var expenseVoid = await services.GetRequiredService<VoidExpenseHandler>().HandleAsync(new VoidExpenseCommand(
            expense.Value!.ExpenseId, seed.ActorId, Guid.NewGuid(), "Delivery charge was entered twice"), default);
        Assert.True(expenseVoid.IsSuccess, expenseVoid.Error?.Message);
        db.ChangeTracker.Clear();
        Assert.Equal(9000m, await SessionCashAsync(db, session.Id));
        Assert.Equal(ExpenseStatus.Voided, (await db.Expenses.AsNoTracking().SingleAsync(x => x.Id == expense.Value.ExpenseId)).Status);

        var item = await db.PurchaseItems.AsNoTracking().SingleAsync(x => x.PurchaseId == purchase.Value!.PurchaseId);
        var returned = await services.GetRequiredService<CreatePurchaseReturnHandler>().HandleAsync(new CreatePurchaseReturnCommand(
            purchase.Value!.PurchaseId, "Return surplus delivery", "Golden B original purchase source",
            PurchaseReturnSettlementMode.External, seed.ActorId, Guid.NewGuid(),
            [new PurchaseReturnLineInput(item.Id, 2m, 100m, [])]), default);
        Assert.True(returned.IsSuccess, returned.Error?.Message);
        db.ChangeTracker.Clear();
        Assert.Equal(200m, returned.Value!.SupplierReturnValue);
        Assert.Equal(200m, returned.Value.InventoryCostRemoved);
        Assert.Equal(-200m, await SupplierPositionAsync(db, seed.SupplierId));
        Assert.Equal(9000m, await SessionCashAsync(db, session.Id));

        var refund = await services.GetRequiredService<CreateSupplierRefundHandler>().HandleAsync(new CreateSupplierRefundCommand(
            seed.SupplierId, 200m, SupplierSettlementMethod.CashDrawer, seed.ActorId, Guid.NewGuid(),
            Note: "Settle the exact Golden B return credit"), default);
        Assert.True(refund.IsSuccess, refund.Error?.Message);
        db.ChangeTracker.Clear();
        await AssertGoldenEndpointAsync(services, seed.ProductId, seed.ActorId, session.Id,
            new GoldenEndpoint(8m, 800m, 9200m, 0m, 0m, 0m, 0m, 0m, 0m));
        Assert.Equal(0m, await SupplierPositionAsync(db, seed.SupplierId));
        var entries = await db.SupplierAccountEntries.AsNoTracking().Where(x => x.SupplierId == seed.SupplierId).ToListAsync();
        Assert.Equal(4, entries.Count);
        Assert.Equal(1000m, Assert.Single(entries, x => x.EntryType == SupplierAccountEntryType.Purchase).Amount);
        Assert.Equal(1000m, Assert.Single(entries, x => x.EntryType == SupplierAccountEntryType.SupplierPayment).Amount);
        Assert.Equal(200m, Assert.Single(entries, x => x.EntryType == SupplierAccountEntryType.PurchaseReturnCredit).Amount);
        Assert.Equal(200m, Assert.Single(entries, x => x.EntryType == SupplierAccountEntryType.SupplierRefundReceived).Amount);
        var expenseCash = await db.CashMovements.AsNoTracking().Where(x => x.SourceId == expense.Value.ExpenseId).ToListAsync();
        Assert.Equal(2, expenseCash.Count);
        Assert.Equal(60m, Assert.Single(expenseCash, x => x.Direction == CashMovementDirection.Out).Amount);
        Assert.Equal(60m, Assert.Single(expenseCash, x => x.Direction == CashMovementDirection.In).Amount);
        Assert.Equal(2, await db.BusinessAuditEvents.CountAsync(x => x.EntityId == expense.Value.ExpenseId));
    }

    [Fact]
    public async Task GoldenScenarioC_TrackedSaleCustomerWarrantyReplacementHandover_ReconcilesOneChain()
    {
        var clock = new ScenarioClock(new DateTimeOffset(2035, 1, 12, 10, 0, 0, TimeSpan.Zero));
        await using var provider = Phase2PostgresTestHarness.BuildProvider(clock);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var seed = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db, defaultSalePrice: 1000m, defaultWarrantyMonths: 12);
        var customer = await Phase2PostgresTestHarness.SeedCustomerAsync(db, "Golden C owner");
        await using var ownedCashSession = await CreateOwnedCashSessionAsync(db, seed.ActorId);
        var session = ownedCashSession.Session;
        var serial = "GOLDEN-C-" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        var purchase = await services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new CreatePurchaseCommand(
            seed.SupplierId, "GOLDEN-C-" + Guid.NewGuid(), clock.ShopDate, null, 0m,
            PurchaseSettlementMode.External, seed.ActorId, Guid.NewGuid(),
            [new CreatePurchaseLineInput(seed.ProductId, seed.ProductUnitId, 1m, 600m, 1000m, [new SerializedIdentityInput(serial)])],
            InitialPaymentAmount: 0m), default);
        Assert.True(purchase.IsSuccess, purchase.Error?.Message);
        db.ChangeTracker.Clear();
        var original = await db.InventoryUnits.AsNoTracking().SingleAsync(x => x.ProductId == seed.ProductId);
        var sale = await services.GetRequiredService<CompleteSaleHandler>().HandleAsync(new CompleteSaleCommand(
            Guid.NewGuid(), customer.Id, seed.ActorId, session.Id, 0m, SalePaymentMethod.Cash, 1000m, "GOLDEN-C", null,
            [new CompleteSaleLineInput(seed.ProductId, seed.ProductUnitId, 1m, 1000m, [original.Id])]), default);
        Assert.True(sale.IsSuccess, sale.Error?.Message);
        db.ChangeTracker.Clear();
        var saleItem = await db.SaleItems.AsNoTracking().SingleAsync(x => x.SaleId == sale.Value!.SaleId);
        var claim = await services.GetRequiredService<CreateWarrantyClaimHandler>().HandleAsync(new CreateWarrantyClaimCommand(
            customer.Id, sale.Value!.SaleId, seed.SupplierId, seed.ActorId,
            [new WarrantyClaimItemInput(seed.ProductId, 1m, "Customer-owned factory defect", saleItem.Id,
                saleItem.WarrantyValidUntil, [new WarrantyClaimUnitInput(original.Id, serial)])], Guid.NewGuid()), default);
        Assert.True(claim.IsSuccess, claim.Error?.Message);
        var review = await services.GetRequiredService<BeginWarrantyClaimReviewHandler>().HandleAsync(
            new BeginWarrantyClaimReviewCommand(claim.Value, seed.ActorId, Guid.NewGuid()), default);
        Assert.True(review.IsSuccess, review.Error?.Message);
        var send = await services.GetRequiredService<SendWarrantyClaimToSupplierHandler>().HandleAsync(
            new SendWarrantyClaimToSupplierCommand(claim.Value, seed.ActorId, Guid.NewGuid()), default);
        Assert.True(send.IsSuccess, send.Error?.Message);
        db.ChangeTracker.Clear();
        var claimUnitId = await db.WarrantyClaimItemUnits.Where(x => x.OriginalInventoryUnitId == original.Id).Select(x => x.Id).SingleAsync();
        var replacementSerial = "GOLDEN-C-REPLACEMENT-" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        var receipt = await services.GetRequiredService<ReceiveCustomerWarrantyReplacementHandler>().HandleAsync(
            new ReceiveCustomerWarrantyReplacementCommand(claim.Value, seed.ActorId, Guid.NewGuid(),
                [new CustomerWarrantyReplacementUnitInput(claimUnitId, replacementSerial, null, null)], "Customer replacement receipt"), default);
        Assert.True(receipt.IsSuccess, receipt.Error?.Message);
        db.ChangeTracker.Clear();
        var replacement = await db.InventoryUnits.AsNoTracking().SingleAsync(x => x.SerialNumber == replacementSerial);
        Assert.Equal(InventoryUnitStatus.WarrantyCustomerHeld, replacement.Status);
        Assert.Equal(InventoryUnitOriginType.WarrantyReplacement, replacement.OriginType);
        Assert.Equal(original.SupplierProductId, replacement.SupplierProductId);
        Assert.NotEqual(original.Id, replacement.Id);
        Assert.NotEqual(original.TrackingCode, replacement.TrackingCode);
        var handover = await services.GetRequiredService<HandoverWarrantyItemHandler>().HandleAsync(
            new HandoverWarrantyItemCommand(claim.Value, seed.ActorId, Guid.NewGuid(), "Returned replacement to its customer owner"), default);
        Assert.True(handover.IsSuccess, handover.Error?.Message);
        db.ChangeTracker.Clear();
        Assert.Equal(InventoryUnitStatus.WarrantyCustomerHandedOver,
            (await db.InventoryUnits.AsNoTracking().SingleAsync(x => x.Id == replacement.Id)).Status);
        Assert.Equal(WarrantyClaimStatus.Closed, (await db.WarrantyClaims.AsNoTracking().SingleAsync(x => x.Id == claim.Value)).Status);
        var relationship = await db.WarrantyClaimItemUnits.AsNoTracking().SingleAsync(x => x.Id == claimUnitId);
        Assert.Equal(original.Id, relationship.OriginalInventoryUnitId);
        Assert.Equal(replacement.Id, relationship.ReplacementInventoryUnitId);
        Assert.Equal(original.TrackingCode, (await db.InventoryUnits.AsNoTracking().SingleAsync(x => x.Id == original.Id)).TrackingCode);
        await AssertGoldenEndpointAsync(services, seed.ProductId, seed.ActorId, session.Id,
            new GoldenEndpoint(0m, 0m, 11000m, 1000m, 600m, 0m, 0m, 0m, 400m));
        Assert.Equal(600m, await SupplierPositionAsync(db, seed.SupplierId));
        Assert.False(await db.SupplierAccountEntries.AnyAsync(x => x.SupplierId == seed.SupplierId && x.EntryType == SupplierAccountEntryType.WarrantyCredit));
        Assert.True(await db.WarrantyClaimEvents.AnyAsync(x => x.ClaimId == claim.Value));
    }

    [Theory]
    [InlineData(TrackingMode.Quantity)]
    [InlineData(TrackingMode.Length)]
    public async Task GoldenScenarioD_MixedSuppliersLaterMwaCreditScrapRepair_ReconcilesOneChain(TrackingMode mode)
    {
        var clock = new ScenarioClock(new DateTimeOffset(2035, mode == TrackingMode.Length ? 2 : 3, 15, 10, 0, 0, TimeSpan.Zero));
        await using var provider = Phase2PostgresTestHarness.BuildProvider(clock);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        var seed = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);
        var supplierB = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);
        (await db.Products.SingleAsync(x => x.Id == seed.ProductId)).TrackingMode = mode;
        await db.SaveChangesAsync();
        await using var ownedCashSession = await CreateOwnedCashSessionAsync(db, seed.ActorId);
        var session = ownedCashSession.Session;

        async Task<Guid> PurchaseAsync(Guid supplierId, decimal quantity, decimal cost)
        {
            var result = await services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new CreatePurchaseCommand(
                supplierId, "GOLDEN-D-" + Guid.NewGuid(), clock.ShopDate, null, 0m,
                PurchaseSettlementMode.External, seed.ActorId, Guid.NewGuid(),
                [new CreatePurchaseLineInput(seed.ProductId, seed.ProductUnitId, quantity, cost, 150m, [])],
                InitialPaymentAmount: 0m), default);
            Assert.True(result.IsSuccess, result.Error?.Message);
            db.ChangeTracker.Clear();
            return result.Value!.PurchaseId;
        }

        var purchaseA = await PurchaseAsync(seed.SupplierId, 10m, 10m);
        await PurchaseAsync(supplierB.SupplierId, 10m, 30m);
        var sourceItem = await db.PurchaseItems.AsNoTracking().SingleAsync(x => x.PurchaseId == purchaseA);
        var sourceLot = await db.InventoryLots.AsNoTracking().SingleAsync(x => x.PurchaseItemId == sourceItem.Id);
        var damage = await services.GetRequiredService<TransferInventoryConditionHandler>().HandleAsync(
            new TransferInventoryConditionCommand(seed.ProductId, InventoryBucket.Sellable, InventoryBucket.Damaged,
                3m, seed.ActorId, "Golden D source A warranty custody", TargetLotId: sourceLot.Id), default);
        Assert.True(damage.IsSuccess, damage.Error?.Message);

        async Task<Guid> SendAsync()
        {
            var result = await services.GetRequiredService<SendShopStockToSupplierWarrantyHandler>().HandleAsync(
                new SendShopStockToSupplierWarrantyCommand(seed.ProductId, InventoryBucket.Damaged, 1m,
                    seed.SupplierId, sourceItem.Id, "Golden D source A", seed.ActorId, Guid.NewGuid(), []), default);
            Assert.True(result.IsSuccess, result.Error?.Message);
            return result.Value;
        }

        var creditCase = await SendAsync();
        var scrapCase = await SendAsync();
        var repairCase = await SendAsync();
        db.ChangeTracker.Clear();
        var beforeLaterPurchase = await db.ProductCostStates.AsNoTracking().SingleAsync(x => x.ProductId == seed.ProductId);
        Assert.Equal(20m, beforeLaterPurchase.CostedQty);
        Assert.Equal(400m, beforeLaterPurchase.TotalInventoryCost);
        Assert.Equal(20m, beforeLaterPurchase.MovingAverageCost);
        Assert.Equal(3m, (await db.StockBalances.AsNoTracking().SingleAsync(x => x.ProductId == seed.ProductId)).WithSupplierQty);
        await PurchaseAsync(supplierB.SupplierId, 20m, 90m);
        var afterLaterPurchase = await db.ProductCostStates.AsNoTracking().SingleAsync(x => x.ProductId == seed.ProductId);
        Assert.Equal(40m, afterLaterPurchase.CostedQty);
        Assert.Equal(2200m, afterLaterPurchase.TotalInventoryCost);
        Assert.Equal(55m, afterLaterPurchase.MovingAverageCost);

        foreach (var entry in new[] { (creditCase, WarrantyResolutionType.Credited), (scrapCase, WarrantyResolutionType.Scrapped), (repairCase, WarrantyResolutionType.Repaired) })
        {
            var result = await services.GetRequiredService<ReceiveShopStockWarrantyHandler>().HandleAsync(
                new ReceiveShopStockWarrantyCommand(entry.Item1, entry.Item2, seed.ActorId, null, null,
                    Note: null, ClientOperationId: Guid.NewGuid(), SupplierCreditAmount: entry.Item2 == WarrantyResolutionType.Credited ? 100m : null,
                    ResolvedQuantity: 1m), default);
            Assert.True(result.IsSuccess, result.Error?.Message);
        }
        db.ChangeTracker.Clear();
        var caseIds = new List<Guid?> { creditCase, scrapCase, repairCase };
        var sends = await db.ShopWarrantySendAllocations.AsNoTracking().Where(x => caseIds.Contains(x.CaseId)).ToListAsync();
        Assert.Equal(3, sends.Count);
        Assert.All(sends, x => { Assert.Equal(sourceLot.Id, x.OriginalInventoryLotId); Assert.Equal(20m, x.SendTimeMwaUnitCostSnapshot); });
        var sendIds = sends.Select(x => x.Id).ToArray();
        var resolutions = await db.ShopWarrantyResolutionAllocations.AsNoTracking().Where(x => sendIds.Contains(x.SendAllocationId)).ToListAsync();
        Assert.Equal(3, resolutions.Count);
        var credited = Assert.Single(resolutions, x => x.ResolutionOutcome == WarrantyResolutionType.Credited);
        Assert.Equal(55m, credited.ActualResolvedCarryingValue);
        Assert.Equal(100m, credited.SupplierCreditAmount);
        Assert.Equal(55m, credited.ResolutionTimeMwaUnitCostSnapshot);
        Assert.Equal(55m, Assert.Single(resolutions, x => x.ResolutionOutcome == WarrantyResolutionType.Scrapped).ActualResolvedCarryingValue);
        Assert.Equal(0m, Assert.Single(resolutions, x => x.ResolutionOutcome == WarrantyResolutionType.Repaired).ActualResolvedCarryingValue);
        Assert.Equal(ShopWarrantyCaseStatus.Closed, (await db.ShopStockWarrantyCases.AsNoTracking().SingleAsync(x => x.Id == creditCase)).Status);
        Assert.Equal(ShopWarrantyCaseStatus.WrittenOff, (await db.ShopStockWarrantyCases.AsNoTracking().SingleAsync(x => x.Id == scrapCase)).Status);
        Assert.Equal(ShopWarrantyCaseStatus.Closed, (await db.ShopStockWarrantyCases.AsNoTracking().SingleAsync(x => x.Id == repairCase)).Status);
        var stock = await db.StockBalances.AsNoTracking().SingleAsync(x => x.ProductId == seed.ProductId);
        Assert.Equal(38m, stock.SellableQty);
        Assert.Equal(1m, stock.ScrapQty);
        Assert.Equal(0m, stock.WithSupplierQty);
        await AssertGoldenEndpointAsync(services, seed.ProductId, seed.ActorId, session.Id,
            new GoldenEndpoint(38m, 2090m, 10000m, 0m, 0m, 0m, 55m, 45m, -10m));
        Assert.Equal(0m, await SupplierPositionAsync(db, seed.SupplierId));
        Assert.Equal(2100m, await SupplierPositionAsync(db, supplierB.SupplierId));
        Assert.False(await db.SupplierAccountEntries.AnyAsync(x => x.SupplierId == supplierB.SupplierId && x.EntryType == SupplierAccountEntryType.WarrantyCredit));
        Assert.Equal(3, await db.BusinessAuditEvents.CountAsync(x => caseIds.Contains(x.EntityId) &&
            (x.Action == "SHOP_WARRANTY_RESOLVED" || x.Action == "SHOP_WARRANTY_CREDITED")));
    }

    [Fact]
    public async Task SupportingScenario_LengthCutThakaConditionWarrantyCreditTerminalSale()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);

        var seed = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db, defaultSalePrice: 25m);
        var customer = await Phase2PostgresTestHarness.SeedCustomerAsync(db, "Contractor Ali");
        (await db.Products.SingleAsync(x => x.Id == seed.ProductId)).TrackingMode = TrackingMode.Length;
        (await db.Units.SingleAsync(x => x.Id == seed.UnitId)).DisplayDecimalPlaces = 4;
        await db.SaveChangesAsync();

        await using var ownedCashSession = await CreateOwnedCashSessionAsync(db, seed.ActorId, openingCash: 10000m);
        var cashSession = ownedCashSession.Session;

        // 1. INTAKE: Intake 100m @10/m + 50m @16/m
        var purchase1 = await services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new CreatePurchaseCommand(
            seed.SupplierId, "P5-LOT1-" + Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), null, 0m,
            PurchaseSettlementMode.External, seed.ActorId, Guid.NewGuid(),
            [new CreatePurchaseLineInput(seed.ProductId, seed.ProductUnitId, 100m, 10m, 25m, [])]), default);
        Assert.True(purchase1.IsSuccess, purchase1.Error?.Message);

        var purchase2 = await services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new CreatePurchaseCommand(
            seed.SupplierId, "P5-LOT2-" + Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), null, 0m,
            PurchaseSettlementMode.External, seed.ActorId, Guid.NewGuid(),
            [new CreatePurchaseLineInput(seed.ProductId, seed.ProductUnitId, 50m, 16m, 25m, [])]), default);
        Assert.True(purchase2.IsSuccess, purchase2.Error?.Message);
        db.ChangeTracker.Clear();

        var costState1 = await db.ProductCostStates.AsNoTracking().SingleAsync(x => x.ProductId == seed.ProductId);
        Assert.Equal(150m, costState1.CostedQty);
        Assert.Equal(1800m, costState1.TotalInventoryCost); // 100*10 + 50*16 = 1800m
        Assert.Equal(12m, costState1.TotalInventoryCost / costState1.CostedQty); // MWA = 12m/unit

        // 2. CUT & SELL 30m
        var saleCut = await services.GetRequiredService<CompleteSaleHandler>().HandleAsync(new CompleteSaleCommand(
            Guid.NewGuid(), customer.Id, seed.ActorId, cashSession.Id, 0m, SalePaymentMethod.Cash, 750m, "CUT-SALE", null,
            [new CompleteSaleLineInput(seed.ProductId, seed.ProductUnitId, 30m, 25m, [])]), default);
        Assert.True(saleCut.IsSuccess, saleCut.Error?.Message);
        db.ChangeTracker.Clear();

        var costState2 = await db.ProductCostStates.AsNoTracking().SingleAsync(x => x.ProductId == seed.ProductId);
        Assert.Equal(120m, costState2.CostedQty);
        Assert.Equal(1440m, costState2.TotalInventoryCost); // 120 * 12 = 1440m
        Assert.Equal(120m, (await db.StockBalances.AsNoTracking().SingleAsync(x => x.ProductId == seed.ProductId)).SellableQty);

        // 3. THAKA ISSUE 20m (Issue A: 15m, Issue B: 5m)
        // Thaka issue removes 20m from store CostedQty (15m + 5m)
        var thakaProject = await services.GetRequiredService<CreateThakaProjectHandler>().HandleAsync(
            new CreateThakaProjectCommand(customer.Id, "Site Installation", "Civic Center 1", null,
                DateOnly.FromDateTime(DateTime.UtcNow), seed.ActorId, Guid.NewGuid(), Guid.NewGuid()), default);
        Assert.True(thakaProject.IsSuccess, thakaProject.Error?.Message);
        var projectId = thakaProject.Value;

        var issueA = await services.GetRequiredService<IssueThakaMaterialHandler>().HandleAsync(
            new IssueThakaMaterialCommand(Guid.NewGuid(), projectId, seed.ActorId, "First roll",
                [new IssueThakaMaterialLineInput(seed.ProductId, seed.ProductUnitId, 15m, 25m, [])]), default);
        Assert.True(issueA.IsSuccess, issueA.Error?.Message);

        var issueB = await services.GetRequiredService<IssueThakaMaterialHandler>().HandleAsync(
            new IssueThakaMaterialCommand(Guid.NewGuid(), projectId, seed.ActorId, "Second small cut",
                [new IssueThakaMaterialLineInput(seed.ProductId, seed.ProductUnitId, 5m, 25m, [])]), default);
        Assert.True(issueB.IsSuccess, issueB.Error?.Message);
        db.ChangeTracker.Clear();

        Assert.Equal(100m, (await db.StockBalances.AsNoTracking().SingleAsync(x => x.ProductId == seed.ProductId)).SellableQty);

        // 4. THAKA RETURN 5m (Reverse Issue B - restores 5m to store CostedQty)
        var returnThaka = await services.GetRequiredService<ReverseThakaMaterialHandler>().HandleAsync(
            new ReverseThakaMaterialCommand(Guid.NewGuid(), projectId, issueB.Value!.MaterialIssueId, "Unused 5m returned", seed.ActorId), default);
        Assert.True(returnThaka.IsSuccess, returnThaka.Error?.Message);
        db.ChangeTracker.Clear();

        Assert.Equal(105m, (await db.StockBalances.AsNoTracking().SingleAsync(x => x.ProductId == seed.ProductId)).SellableQty);

        // 5. CONDITION TRANSFER 10m to Defective (internal bucket move, CostedQty stays 105m)
        var defectTransfer = await services.GetRequiredService<TransferInventoryConditionHandler>().HandleAsync(
            new TransferInventoryConditionCommand(seed.ProductId, InventoryBucket.Sellable, InventoryBucket.Defective, 10m, seed.ActorId, "Insulation defect"), default);
        Assert.True(defectTransfer.IsSuccess, defectTransfer.Error?.Message);
        db.ChangeTracker.Clear();

        var stockAfterDefect = await db.StockBalances.AsNoTracking().SingleAsync(x => x.ProductId == seed.ProductId);
        Assert.Equal(95m, stockAfterDefect.SellableQty);
        Assert.Equal(10m, stockAfterDefect.DefectiveQty);

        // 6. SHOP WARRANTY SEND 10m (custody move to supplier, CostedQty stays 105m)
        var purchaseItem1 = await db.PurchaseItems.AsNoTracking().FirstAsync(x => x.PurchaseId == purchase1.Value!.PurchaseId);
        var warrantySend = await services.GetRequiredService<SendShopStockToSupplierWarrantyHandler>().HandleAsync(
            new SendShopStockToSupplierWarrantyCommand(
                seed.ProductId, InventoryBucket.Defective, 10m, seed.SupplierId, purchaseItem1.Id,
                "Vendor defect inspection", seed.ActorId, Guid.NewGuid(), []), default);
        Assert.True(warrantySend.IsSuccess, warrantySend.Error?.Message);
        var warrantyCaseId = warrantySend.Value;
        db.ChangeTracker.Clear();

        var stockAfterSend = await db.StockBalances.AsNoTracking().SingleAsync(x => x.ProductId == seed.ProductId);
        Assert.Equal(0m, stockAfterSend.DefectiveQty);
        Assert.Equal(10m, stockAfterSend.WithSupplierQty);

        // 7. SHOP WARRANTY CREDIT: Supplier approves 150m credit note (10m * 15m credit)
        // Resolution05: Derecognizes 10m at current MWA (12m/unit) = 120m carrying value.
        // CostedQty derecognized from 105m to 95m (95m sellable remaining in store).
        // Carrying value reduces from 1260m (105*12) to 1140m (95*12).
        var warrantyCredit = await services.GetRequiredService<ReceiveShopStockWarrantyHandler>().HandleAsync(
            new ReceiveShopStockWarrantyCommand(
                warrantyCaseId, WarrantyResolutionType.Credited, seed.ActorId, null, null,
                "Manufacturer credit note CN-991", Guid.NewGuid(), SupplierCreditAmount: 150m,
                SupplierReference: "CN-991", ResolvedQuantity: 10m), default);
        Assert.True(warrantyCredit.IsSuccess, warrantyCredit.Error?.Message);
        db.ChangeTracker.Clear();

        var costState3 = await db.ProductCostStates.AsNoTracking().SingleAsync(x => x.ProductId == seed.ProductId);
        Assert.Equal(95m, costState3.CostedQty);
        Assert.Equal(1140m, costState3.TotalInventoryCost); // 95m * 12m = 1140m

        // 8. TERMINAL SALE OF REMAINING 95m SELLABLE
        var terminalSale = await services.GetRequiredService<CompleteSaleHandler>().HandleAsync(new CompleteSaleCommand(
            Guid.NewGuid(), customer.Id, seed.ActorId, cashSession.Id, 0m, SalePaymentMethod.Cash, 2375m, "FINAL-95M", null,
            [new CompleteSaleLineInput(seed.ProductId, seed.ProductUnitId, 95m, 25m, [])]), default);
        Assert.True(terminalSale.IsSuccess, terminalSale.Error?.Message);
        db.ChangeTracker.Clear();

        var finalStock = await db.StockBalances.AsNoTracking().SingleAsync(x => x.ProductId == seed.ProductId);
        Assert.Equal(0m, finalStock.SellableQty);
        Assert.Equal(0m, finalStock.DefectiveQty);
        Assert.Equal(0m, finalStock.WithSupplierQty);

        var finalCostState = await db.ProductCostStates.AsNoTracking().SingleAsync(x => x.ProductId == seed.ProductId);
        Assert.Equal(0m, finalCostState.CostedQty);
        Assert.Equal(0m, finalCostState.TotalInventoryCost);
    }

    [Fact]
    public async Task SupportingScenario_MixedBasketDiscountExchangeSupplierReturnCashPayment()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);

        // Products:
        // Product 1: Serialized Smartphone (Cost 1000m, Sale 1500m)
        var p1 = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db, defaultSalePrice: 1500m);
        // Product 2: Bulk Accessories (Cost 50m, Sale 100m)
        var p2 = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db, defaultSalePrice: 100m);
        var customer = await Phase2PostgresTestHarness.SeedCustomerAsync(db, "VIP Corporate Client");
        await using var ownedCashSession = await CreateOwnedCashSessionAsync(db, p1.ActorId, openingCash: 10000m);
        var cashSession = ownedCashSession.Session;

        // 1. INTAKE:
        // Receive 2 serialized units of P1 on credit (InitialPaymentAmount = 0m, payable = 2000m)
        var purchaseP1 = await services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new CreatePurchaseCommand(
            p1.SupplierId, "P5-MIX-P1-" + Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), null, 0m,
            PurchaseSettlementMode.External, p1.ActorId, Guid.NewGuid(),
            [new CreatePurchaseLineInput(p1.ProductId, p1.ProductUnitId, 2m, 1000m, 1500m,
                [new SerializedIdentityInput("MIX-PHONE-1"), new SerializedIdentityInput("MIX-PHONE-2")])],
            InitialPaymentAmount: 0m), default);
        Assert.True(purchaseP1.IsSuccess, purchaseP1.Error?.Message);

        // Receive 20 units of P2
        var purchaseP2 = await services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new CreatePurchaseCommand(
            p2.SupplierId, "P5-MIX-P2-" + Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), null, 0m,
            PurchaseSettlementMode.External, p1.ActorId, Guid.NewGuid(),
            [new CreatePurchaseLineInput(p2.ProductId, p2.ProductUnitId, 20m, 50m, 100m, [])]), default);
        Assert.True(purchaseP2.IsSuccess, purchaseP2.Error?.Message);
        db.ChangeTracker.Clear();

        var unitsP1 = await db.InventoryUnits.Where(x => x.ProductId == p1.ProductId).OrderBy(x => x.TrackingCode).ToListAsync();
        Assert.Equal(2, unitsP1.Count);

        // 2. MULTI-LINE SALE WITH INVOICE DISCOUNT:
        // Line 1: Phone 1 @1500m
        // Line 2: 5 Bulk accessories @100m = 500m
        // Gross total = 2000m. Invoice discount = 100m. Net total = 1900m.
        // Proportional discount allocation (AwayFromZero): Line 1 = 75m, Line 2 = 25m.
        var multiSale = await services.GetRequiredService<CompleteSaleHandler>().HandleAsync(new CompleteSaleCommand(
            Guid.NewGuid(), customer.Id, p1.ActorId, cashSession.Id, 100m, SalePaymentMethod.Cash, 1900m, "MIXED-BASKET", null,
            [
                new CompleteSaleLineInput(p1.ProductId, p1.ProductUnitId, 1m, 1500m, [unitsP1[0].Id]),
                new CompleteSaleLineInput(p2.ProductId, p2.ProductUnitId, 5m, 100m, [])
            ]), default);
        Assert.True(multiSale.IsSuccess, multiSale.Error?.Message);
        Assert.NotNull(multiSale.Value);
        db.ChangeTracker.Clear();

        var saleItems = await db.SaleItems.Where(x => x.SaleId == multiSale.Value!.SaleId).OrderBy(x => x.ProductId == p1.ProductId ? 0 : 1).ToListAsync();
        Assert.Equal(2, saleItems.Count);
        Assert.Equal(75m, saleItems[0].AllocatedInvoiceDiscount);
        Assert.Equal(1425m, saleItems[0].NetLineTotal);
        Assert.Equal(25m, saleItems[1].AllocatedInvoiceDiscount);
        Assert.Equal(475m, saleItems[1].NetLineTotal);
        Assert.Equal(1900m, multiSale.Value.GrandTotal);

        // 3. COMMERCIAL EXCHANGE OF SERIALIZED LINE:
        // Customer exchanges Phone 1 (net returned 1425m) for Phone 2 (sale 1500m)
        // Net difference = 1500 - 1425 = 75m
        var exchange = await services.GetRequiredService<CommercialExchangeHandler>().HandleAsync(new CommercialExchangeCommand(
            Guid.NewGuid(), multiSale.Value.SaleId, p1.ActorId, customer.Id, cashSession.Id,
            "UPGRADE", "Customer preferred Phone 2 color",
            [new SaleReturnLineInput(saleItems[0].Id, 1m, SaleReturnDisposition.RestockSellable, [unitsP1[0].Id])],
            [new CompleteSaleLineInput(p1.ProductId, p1.ProductUnitId, 1m, 1500m, [unitsP1[1].Id])],
            0m, SalePaymentMethod.Cash, 75m, "CASH-DIFF"), default);
        Assert.True(exchange.IsSuccess, exchange.Error?.Message);
        Assert.NotNull(exchange.Value);
        Assert.Equal(75m, exchange.Value!.NetDifference);
        db.ChangeTracker.Clear();

        Assert.Equal(InventoryUnitStatus.InStock, (await db.InventoryUnits.AsNoTracking().SingleAsync(x => x.Id == unitsP1[0].Id)).Status);
        Assert.Equal(InventoryUnitStatus.Sold, (await db.InventoryUnits.AsNoTracking().SingleAsync(x => x.Id == unitsP1[1].Id)).Status);

        // 4. SUPPLIER RETURN OF BULK LOT:
        // Return 5 units of bulk accessories to supplier
        var p2Item = await db.PurchaseItems.SingleAsync(x => x.ProductId == p2.ProductId);
        var supplierReturn = await services.GetRequiredService<CreatePurchaseReturnHandler>().HandleAsync(
            new CreatePurchaseReturnCommand(
                purchaseP2.Value!.PurchaseId, "Excess inventory return", "Approved by supplier rep",
                PurchaseReturnSettlementMode.External, p1.ActorId, Guid.NewGuid(),
                [new PurchaseReturnLineInput(p2Item.Id, 5m, 50m, [])]), default);
        Assert.True(supplierReturn.IsSuccess, supplierReturn.Error?.Message);
        db.ChangeTracker.Clear();

        Assert.Equal(10m, (await db.StockBalances.AsNoTracking().SingleAsync(x => x.ProductId == p2.ProductId)).SellableQty);

        // 5. MANUAL CASH OUT:
        // Store manager pays 200m cash for office cleaning supplies
        var manualCash = await services.GetRequiredService<RecordManualCashMovementHandler>().HandleAsync(
            new RecordManualCashMovementCommand(CashMovementDirection.Out, 200m, p1.ActorId, "Office supplies purchase", null), default);
        Assert.True(manualCash.IsSuccess, manualCash.Error?.Message);
        db.ChangeTracker.Clear();

        var movement = await db.CashMovements.AsNoTracking().SingleAsync(x => x.Id == manualCash.Value);
        Assert.Equal(-200m, movement.SignedAmount);

        // 6. SUPPLIER PAYMENT & SUPPLIER REFUND:
        // Pay supplier 500m from cash drawer to reduce accounts payable on P1 (payable was 2000m)
        var supplierPayment = await services.GetRequiredService<CreateSupplierPaymentHandler>().HandleAsync(
            new CreateSupplierPaymentCommand(
                p1.SupplierId, 500m, SupplierPaymentPurpose.Settlement,
                SupplierSettlementMethod.CashDrawer, p1.ActorId, Guid.NewGuid(), Note: "Partial invoice payment"), default);
        Assert.True(supplierPayment.IsSuccess, supplierPayment.Error?.Message);

        var paymentEntry = await db.SupplierAccountEntries.AsNoTracking().FirstOrDefaultAsync(x => x.ReferenceId == supplierPayment.Value!.PaymentId);
        Assert.NotNull(paymentEntry);
        Assert.Equal(SupplierAccountDirection.DecreasePayable, paymentEntry.Direction);

        // Supplier prepayment advance and refund:
        // Pay an advance of 200m to Supplier 2 (creating a 200m credit)
        var supplierAdvance = await services.GetRequiredService<CreateSupplierPaymentHandler>().HandleAsync(
            new CreateSupplierPaymentCommand(
                p2.SupplierId, 200m, SupplierPaymentPurpose.Advance,
                SupplierSettlementMethod.CashDrawer, p1.ActorId, Guid.NewGuid(), Note: "Advance prepayment for upcoming parts"), default);
        Assert.True(supplierAdvance.IsSuccess, supplierAdvance.Error?.Message);

        // Supplier 2 provides a 100m cash refund from the advance credit
        var supplierRefund = await services.GetRequiredService<CreateSupplierRefundHandler>().HandleAsync(
            new CreateSupplierRefundCommand(
                p2.SupplierId, 100m, SupplierSettlementMethod.CashDrawer, p1.ActorId, Guid.NewGuid(), Note: "Promotional rebate refund"), default);
        Assert.True(supplierRefund.IsSuccess, supplierRefund.Error?.Message);
        db.ChangeTracker.Clear();

        var refundEntry = await db.SupplierAccountEntries.AsNoTracking().FirstOrDefaultAsync(x => x.ReferenceId == supplierRefund.Value!.RefundId);
        Assert.NotNull(refundEntry);
        Assert.Equal(SupplierAccountDirection.IncreasePayable, refundEntry.Direction);
    }

    [Fact]
    public async Task SupportingScenario_MissingRefusalFoundRecoveryNegativeRejection()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);

        var seed = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db, defaultSalePrice: 1000m);
        await using var ownedCashSession = await CreateOwnedCashSessionAsync(db, seed.ActorId, openingCash: 10000m);
        var cashSession = ownedCashSession.Session;

        // 1. INTAKE: Receive Unit 1 (@600) and Unit 2 (@600)
        async Task<InventoryUnit> ReceiveUnitAsync(string serial)
        {
            var prior = await db.InventoryUnits.Where(x => x.ProductId == seed.ProductId).Select(x => x.Id).ToArrayAsync();
            var purchase = await services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new CreatePurchaseCommand(
                seed.SupplierId, "P5-GOLD-D-" + Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), null, 0m,
                PurchaseSettlementMode.External, seed.ActorId, Guid.NewGuid(),
                [new CreatePurchaseLineInput(seed.ProductId, seed.ProductUnitId, 1m, 600m, 1000m,
                    [new SerializedIdentityInput(serial)])]), default);
            Assert.True(purchase.IsSuccess, purchase.Error?.Message);
            db.ChangeTracker.Clear();
            return await db.InventoryUnits.SingleAsync(x => x.ProductId == seed.ProductId && !prior.Contains(x.Id));
        }

        var unit1 = await ReceiveUnitAsync("SN-GD-1");
        var unit2 = await ReceiveUnitAsync("SN-GD-2");

        // 2. SHORTAGE -> MISSING: Adjust Unit 1 as Lost/Missing
        var missingOperationId = Guid.NewGuid();
        var markMissing = await services.GetRequiredService<CreateStockAdjustmentHandler>().HandleAsync(
            new CreateStockAdjustmentCommand(
                StockAdjustmentMode.Delta, StockAdjustmentReason.Lost,
                [new StockAdjustmentItemCommand(seed.ProductId, null, StockAdjustmentDirection.Decrease, InventoryBucket.Sellable, 1m, 600m, InventoryUnitIds: [unit1.Id])],
                seed.ActorId, missingOperationId, "Lost during warehouse transfer"), default);
        Assert.True(markMissing.IsSuccess, markMissing.Error?.Message);
        db.ChangeTracker.Clear();

        var missingUnit = await db.InventoryUnits.AsNoTracking().SingleAsync(x => x.Id == unit1.Id);
        Assert.Equal(InventoryUnitStatus.Missing, missingUnit.Status);

        var missingMovement = await db.InventoryMovements.AsNoTracking().SingleAsync(x => x.CorrelationId == missingOperationId);
        Assert.Equal(600m, missingMovement.RecognizedLossAmount);

        // 3. ATTEMPTS TO SELL / EXCHANGE / RETURN MISSING UNIT (MUST ALL FAIL CLOSED)
        // Attempt A: Sale of Missing Unit 1
        var refuseSale = await services.GetRequiredService<CompleteSaleHandler>().HandleAsync(new CompleteSaleCommand(
            Guid.NewGuid(), null, seed.ActorId, cashSession.Id, 0m, SalePaymentMethod.Cash, 1000m, "REFUSE", null,
            [new CompleteSaleLineInput(seed.ProductId, seed.ProductUnitId, 1m, 1000m, [unit1.Id])]), default);
        Assert.False(refuseSale.IsSuccess);
        Assert.Equal("sales.serial_not_sellable", refuseSale.Error?.Code);

        // Attempt B: Commercial Exchange replacement with Missing Unit 1
        var validSaleOfUnit2 = await services.GetRequiredService<CompleteSaleHandler>().HandleAsync(new CompleteSaleCommand(
            Guid.NewGuid(), null, seed.ActorId, cashSession.Id, 0m, SalePaymentMethod.Cash, 1000m, "SALE-UNIT2", null,
            [new CompleteSaleLineInput(seed.ProductId, seed.ProductUnitId, 1m, 1000m, [unit2.Id])]), default);
        Assert.True(validSaleOfUnit2.IsSuccess, validSaleOfUnit2.Error?.Message);
        Assert.NotNull(validSaleOfUnit2.Value);
        db.ChangeTracker.Clear();

        var sold2 = await db.SaleItems.AsNoTracking().SingleAsync(x => x.SaleId == validSaleOfUnit2.Value!.SaleId);
        var refuseExchange = await services.GetRequiredService<CommercialExchangeHandler>().HandleAsync(new CommercialExchangeCommand(
            Guid.NewGuid(), validSaleOfUnit2.Value.SaleId, seed.ActorId, null, null, "EXCHANGE", "Refusal probe",
            [new SaleReturnLineInput(sold2.Id, 1m, SaleReturnDisposition.RestockSellable, [unit2.Id])],
            [new CompleteSaleLineInput(seed.ProductId, seed.ProductUnitId, 1m, 1000m, [unit1.Id])],
            0m, SalePaymentMethod.Other, 0m, "REPAIR"), default);
        Assert.False(refuseExchange.IsSuccess);
        Assert.Equal("sales.serial_not_sellable", refuseExchange.Error?.Code);

        // Attempt C: Sale Return of Missing Unit 1 (not sold on this sale)
        var refuseReturn = await services.GetRequiredService<CreateSaleReturnHandler>().HandleAsync(new CreateSaleReturnCommand(
            validSaleOfUnit2.Value.SaleId, "WRONG", "Return attempt", RefundMethod.Cash, seed.ActorId, Guid.NewGuid(),
            [new SaleReturnLineInput(sold2.Id, 1m, SaleReturnDisposition.RestockSellable, [unit1.Id])]), default);
        Assert.False(refuseReturn.IsSuccess);

        // Invariant check: Unit 1 remains firmly Missing with zero mutations
        db.ChangeTracker.Clear();
        Assert.Equal(InventoryUnitStatus.Missing, (await db.InventoryUnits.AsNoTracking().SingleAsync(x => x.Id == unit1.Id)).Status);

        // 4. FOUND RECOVERY: Unit 1 is found in warehouse
        var foundCommand = new FoundInventoryUnitCommand(
            seed.ProductId, unit1.Id, missingMovement.Id, InventoryBucket.Sellable,
            "Found in misplaced aisle 4 shelf B", seed.ActorId, Guid.NewGuid());
        var foundResult = await services.GetRequiredService<FoundInventoryUnitHandler>().HandleAsync(foundCommand, default);
        Assert.True(foundResult.IsSuccess, foundResult.Error?.Message);
        Assert.NotNull(foundResult.Value);
        Assert.Equal(600m, foundResult.Value!.RestoredInventoryValue);
        Assert.Equal(600m, foundResult.Value.InventoryLossRecoveryGain);
        db.ChangeTracker.Clear();

        var recoveredUnit = await db.InventoryUnits.AsNoTracking().SingleAsync(x => x.Id == unit1.Id);
        Assert.Equal(InventoryUnitStatus.InStock, recoveredUnit.Status);

        // 5. SALE OF RECOVERED UNIT: Now unit 1 can be cleanly sold
        var saleRecovered = await services.GetRequiredService<CompleteSaleHandler>().HandleAsync(new CompleteSaleCommand(
            Guid.NewGuid(), null, seed.ActorId, cashSession.Id, 0m, SalePaymentMethod.Cash, 1000m, "RECOVERED-SALE", null,
            [new CompleteSaleLineInput(seed.ProductId, seed.ProductUnitId, 1m, 1000m, [unit1.Id])]), default);
        Assert.True(saleRecovered.IsSuccess, saleRecovered.Error?.Message);
        db.ChangeTracker.Clear();

        Assert.Equal(InventoryUnitStatus.Sold, (await db.InventoryUnits.AsNoTracking().SingleAsync(x => x.Id == unit1.Id)).Status);

        // 6. NEGATIVE STOCK & MONEY GUARDS:
        // Attempt to complete sale with negative amount tendered
        var negativeTendered = await services.GetRequiredService<CompleteSaleHandler>().HandleAsync(new CompleteSaleCommand(
            Guid.NewGuid(), null, seed.ActorId, cashSession.Id, 0m, SalePaymentMethod.Cash, -500m, "NEGATIVE", null,
            [new CompleteSaleLineInput(seed.ProductId, seed.ProductUnitId, 1m, 1000m, [])]), default);
        Assert.False(negativeTendered.IsSuccess);

        // Attempt manual cash movement with negative amount
        var negativeCash = await services.GetRequiredService<RecordManualCashMovementHandler>().HandleAsync(
            new RecordManualCashMovementCommand(CashMovementDirection.In, -100m, seed.ActorId, "Invalid negative cash", null), default);
        Assert.False(negativeCash.IsSuccess);
    }
}
