using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EdgeRetails.IntegrationTests;

public sealed class Phase2ReconciliationPostgresTests
{
    [Fact]
    public async Task ClientOperationId_Replay_ReturnsCommittedResult_WithoutDuplicateEffects()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();

        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var fixture = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db, defaultSalePrice: 150m);

        var purchaseHandler = ActivatorUtilities.CreateInstance<CreatePurchaseHandler>(services);
        var clientOp = Guid.CreateVersion7();

        var command = new CreatePurchaseCommand(
            fixture.SupplierId,
            "INV-REPLAY-01",
            DateOnly.FromDateTime(DateTime.UtcNow),
            "Idempotency replay test",
            0m,
            PurchaseSettlementMode.External,
            fixture.ActorId,
            clientOp,
            [new CreatePurchaseLineInput(fixture.ProductId, fixture.ProductUnitId, 5m, 100m, 150m, [])],
            InitialPaymentAmount: 0m);

        // First execution
        var result1 = await purchaseHandler.HandleAsync(command, CancellationToken.None);
        Assert.True(result1.IsSuccess);
        Assert.False(result1.Value!.WasExisting);

        // Second execution with SAME clientOp
        var result2 = await purchaseHandler.HandleAsync(command, CancellationToken.None);
        Assert.True(result2.IsSuccess);
        Assert.True(result2.Value!.WasExisting);
        Assert.Equal(result1.Value.PurchaseId, result2.Value.PurchaseId);

        db.ChangeTracker.Clear();

        // Exactly one purchase
        var purchases = await db.Purchases.Where(x => x.ClientOperationId == clientOp).ToListAsync();
        Assert.Single(purchases);

        // Exactly one stock balance of 5
        var stock = await db.StockBalances.SingleAsync(x => x.ProductId == fixture.ProductId);
        Assert.Equal(5m, stock.SellableQty);

        // Exactly one supplier entry
        var entries = await db.SupplierAccountEntries.Where(x => x.ClientOperationId == clientOp).ToListAsync();
        Assert.Single(entries);
    }

    [Fact]
    public async Task TransactionRollback_OnFailure_LeavesZeroOrphanRecords()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();

        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var fixture = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db, defaultSalePrice: 150m);

        var purchaseHandler = ActivatorUtilities.CreateInstance<CreatePurchaseHandler>(services);
        var clientOp = Guid.CreateVersion7();

        // Pass invalid negative initial payment to force failure during transaction execution
        var invalidCommand = new CreatePurchaseCommand(
            fixture.SupplierId,
            "INV-FAIL-01",
            DateOnly.FromDateTime(DateTime.UtcNow),
            null,
            0m,
            PurchaseSettlementMode.External,
            fixture.ActorId,
            clientOp,
            [new CreatePurchaseLineInput(fixture.ProductId, fixture.ProductUnitId, 10m, 100m, 150m, [])],
            InitialPaymentAmount: -500m); // invalid negative payment

        var result = await purchaseHandler.HandleAsync(invalidCommand, CancellationToken.None);
        Assert.False(result.IsSuccess);

        db.ChangeTracker.Clear();

        // 0 Purchases
        var purchases = await db.Purchases.Where(x => x.ClientOperationId == clientOp).ToListAsync();
        Assert.Empty(purchases);

        // 0 Stock Balances
        var stock = await db.StockBalances.FirstOrDefaultAsync(x => x.ProductId == fixture.ProductId);
        Assert.Null(stock);

        // 0 Inventory Movements
        var movements = await db.InventoryMovements.Where(x => x.CorrelationId == clientOp).ToListAsync();
        Assert.Empty(movements);

        // 0 Supplier Account Entries
        var entries = await db.SupplierAccountEntries.Where(x => x.ClientOperationId == clientOp).ToListAsync();
        Assert.Empty(entries);
    }

    [Fact]
    public async Task StockReconciliation_AcrossAllBuckets_MatchesMovementLedgerExactly()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();

        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var fixture = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db, defaultSalePrice: 200m);

        var purchaseHandler = ActivatorUtilities.CreateInstance<CreatePurchaseHandler>(services);
        var saleHandler = ActivatorUtilities.CreateInstance<CompleteSaleHandler>(services);
        var saleReturnHandler = ActivatorUtilities.CreateInstance<CreateSaleReturnHandler>(services);
        var adjustHandler = ActivatorUtilities.CreateInstance<CreateStockAdjustmentHandler>(services);
        var purchaseReturnHandler = ActivatorUtilities.CreateInstance<CreatePurchaseReturnHandler>(services);

        // 1. Purchase 100 units
        var pResult = await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                fixture.SupplierId,
                "INV-RECON-100",
                DateOnly.FromDateTime(DateTime.UtcNow),
                null,
                0m,
                PurchaseSettlementMode.External,
                fixture.ActorId,
                Guid.CreateVersion7(),
                [new CreatePurchaseLineInput(fixture.ProductId, fixture.ProductUnitId, 100m, 100m, 200m, [])],
                InitialPaymentAmount: 0m),
            CancellationToken.None);
        Assert.True(pResult.IsSuccess);
        var purchaseId = pResult.Value!.PurchaseId;

        // 2. Sale 30 units
        var sResult = await saleHandler.HandleAsync(
            new CompleteSaleCommand(
                Guid.CreateVersion7(),
                null,
                fixture.ActorId,
                null,
                0m,
                SalePaymentMethod.Bank,
                6000m,
                "BANK",
                null,
                [new CompleteSaleLineInput(fixture.ProductId, fixture.ProductUnitId, 30m, 200m, [])]),
            CancellationToken.None);
        Assert.True(sResult.IsSuccess);
        var saleId = sResult.Value!.SaleId;

        db.ChangeTracker.Clear();
        var saleItem = await db.SaleItems.SingleAsync(x => x.SaleId == saleId);

        // 3. Sale Return 5 units
        var srResult = await saleReturnHandler.HandleAsync(
            new CreateSaleReturnCommand(
                saleId,
                "RETURN",
                "Return test",
                RefundMethod.Bank,
                fixture.ActorId,
                Guid.CreateVersion7(),
                [new SaleReturnLineInput(saleItem.Id, 5m, SaleReturnDisposition.RestockSellable, [])]),
            CancellationToken.None);
        Assert.True(srResult.IsSuccess);

        // 4. Stock Adjustment: Transfer 10 units from Sellable to Damaged
        var adjResult = await adjustHandler.HandleAsync(
            new CreateStockAdjustmentCommand(
                StockAdjustmentMode.Delta,
                StockAdjustmentReason.Damaged,
                [
                    new StockAdjustmentItemCommand(
                        fixture.ProductId,
                        fixture.ProductUnitId,
                        StockAdjustmentDirection.Decrease,
                        InventoryBucket.Sellable,
                        10m,
                        100m,
                        fixture.SupplierId,
                        null,
                        null,
                        "Damaged in store"),
                    new StockAdjustmentItemCommand(
                        fixture.ProductId,
                        fixture.ProductUnitId,
                        StockAdjustmentDirection.Increase,
                        InventoryBucket.Damaged,
                        10m,
                        100m,
                        fixture.SupplierId,
                        null,
                        null,
                        "Moved to Damaged bucket")
                ],
                fixture.ActorId,
                Guid.CreateVersion7(),
                "Water damage during storage"),
            CancellationToken.None);
        Assert.True(adjResult.IsSuccess);

        db.ChangeTracker.Clear();
        var pItem = await db.PurchaseItems.SingleAsync(x => x.PurchaseId == purchaseId);

        // 5. Purchase Return 5 units from Sellable
        var prResult = await purchaseReturnHandler.HandleAsync(
            new CreatePurchaseReturnCommand(
                purchaseId,
                "Return surplus to supplier",
                null,
                PurchaseReturnSettlementMode.External,
                fixture.ActorId,
                Guid.CreateVersion7(),
                [new PurchaseReturnLineInput(pItem.Id, 5m, null, [])]),
            CancellationToken.None);
        Assert.True(prResult.IsSuccess);

        db.ChangeTracker.Clear();

        // Authoritative reconciliation:
        // Sellable: 100 (purchase) - 30 (sale) + 5 (sale return) - 10 (damage decrease) - 5 (purchase return) = 60
        // Damaged: 10
        // Defective: 0
        // WithSupplier: 0
        // Scrap: 0
        var stock = await db.StockBalances.SingleAsync(x => x.ProductId == fixture.ProductId);
        Assert.Equal(60m, stock.SellableQty);
        Assert.Equal(10m, stock.DamagedQty);
        Assert.Equal(0m, stock.DefectiveQty);
        Assert.Equal(0m, stock.WithSupplierQty);
        Assert.Equal(0m, stock.ScrapQty);
    }

    [Fact]
    public async Task SupplierKhata_And_Cash_Reconciliation()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();

        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var fixture = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db, defaultSalePrice: 200m);
        var session = await Phase2PostgresTestHarness.SeedOpenCashSessionAsync(db, fixture.ActorId, openingCash: 10000m);

        var purchaseHandler = ActivatorUtilities.CreateInstance<CreatePurchaseHandler>(services);
        var paymentHandler = ActivatorUtilities.CreateInstance<CreateSupplierPaymentHandler>(services);
        var refundHandler = ActivatorUtilities.CreateInstance<CreateSupplierRefundHandler>(services);
        var cashHandler = ActivatorUtilities.CreateInstance<RecordManualCashMovementHandler>(services);

        // 1. Purchase 5,000 (unpaid) -> Payable = +5,000
        await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                fixture.SupplierId,
                "INV-KHATA-01",
                DateOnly.FromDateTime(DateTime.UtcNow),
                null,
                0m,
                PurchaseSettlementMode.External,
                fixture.ActorId,
                Guid.CreateVersion7(),
                [new CreatePurchaseLineInput(fixture.ProductId, fixture.ProductUnitId, 50m, 100m, 200m, [])],
                InitialPaymentAmount: 0m),
            CancellationToken.None);

        // 2. Supplier Cash Drawer Payment 2,000 -> Payable = +3,000, Cash = 10,000 - 2,000 = 8,000
        await paymentHandler.HandleAsync(
            new CreateSupplierPaymentCommand(
                fixture.SupplierId,
                2000m,
                SupplierPaymentPurpose.Settlement,
                SupplierSettlementMethod.CashDrawer,
                fixture.ActorId,
                Guid.CreateVersion7(),
                null,
                "Cash drawer partial settlement"),
            CancellationToken.None);

        // 3. Manual Cash In +1,000 -> Cash = 8,000 + 1,000 = 9,000
        await cashHandler.HandleAsync(
            new RecordManualCashMovementCommand(
                CashMovementDirection.In,
                1000m,
                fixture.ActorId,
                "Cash replenishment",
                null),
            CancellationToken.None);

        db.ChangeTracker.Clear();

        // Verify Khata Balance
        var entries = await db.SupplierAccountEntries.Where(x => x.SupplierId == fixture.SupplierId).ToListAsync();
        var netPayable = entries.Sum(e => e.Direction == SupplierAccountDirection.IncreasePayable ? e.Amount : -e.Amount);
        Assert.Equal(3000m, netPayable);

        // Verify Cash Movements
        var movements = await db.CashMovements.Where(x => x.CashSessionId == session.Id).ToListAsync();
        var netCashMovement = movements.Sum(m => m.Direction == CashMovementDirection.In ? m.Amount : -m.Amount);
        var expectedCash = session.OpeningCash + netCashMovement;
        Assert.Equal(9000m, expectedCash);
    }
}
