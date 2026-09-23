using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Purchasing;
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

public sealed class Phase2TransactionalPostgresTests
{
    [Fact]
    public async Task Purchase_Unpaid_IncreasesPayable_And_EstablishesCostAndLots()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();

        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var fixture = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db, defaultSalePrice: 200m);

        var purchaseHandler = ActivatorUtilities.CreateInstance<CreatePurchaseHandler>(services);
        var clientOp = Guid.CreateVersion7();
        var purchaseResult = await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                fixture.SupplierId,
                "INV-" + Guid.NewGuid().ToString("N")[..8],
                DateOnly.FromDateTime(DateTime.UtcNow),
                "Unpaid purchase",
                0m,
                PurchaseSettlementMode.External,
                fixture.ActorId,
                clientOp,
                [
                    new CreatePurchaseLineInput(
                        fixture.ProductId,
                        fixture.ProductUnitId,
                        10m,
                        100m,
                        200m,
                        [])
                ],
                InitialPaymentAmount: 0m),
            CancellationToken.None);

        Assert.True(purchaseResult.IsSuccess, purchaseResult.Error?.Message);

        db.ChangeTracker.Clear();

        // 1. Stock Balance
        var stock = await db.StockBalances.SingleAsync(x => x.ProductId == fixture.ProductId);
        Assert.Equal(10m, stock.SellableQty);

        // 2. Cost State
        var cost = await db.ProductCostStates.SingleAsync(x => x.ProductId == fixture.ProductId);
        Assert.Equal(10m, cost.CostedQty);
        Assert.Equal(1000m, cost.TotalInventoryCost);
        Assert.Equal(100m, cost.LastPurchaseCost);

        // 3. Lot Bucket Balance
        var lot = await db.InventoryLots.SingleAsync(x => x.ProductId == fixture.ProductId);
        var lotBucket = await db.InventoryLotBucketBalances.SingleAsync(x => x.LotId == lot.Id && x.StockBucket == InventoryBucket.Sellable);
        Assert.Equal(10m, lotBucket.Quantity);

        // 4. Supplier Account Entry
        var entries = await db.SupplierAccountEntries.Where(x => x.SupplierId == fixture.SupplierId).ToListAsync();
        Assert.Single(entries);
        var entry = entries[0];
        Assert.Equal(SupplierAccountEntryType.Purchase, entry.EntryType);
        Assert.Equal(SupplierAccountDirection.IncreasePayable, entry.Direction);
        Assert.Equal(1000m, entry.Amount);
    }

    [Fact]
    public async Task Purchase_PartialPayment_ReducesPayable_And_RecordsSupplierPayment()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();

        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var fixture = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db, defaultSalePrice: 150m);

        var purchaseHandler = ActivatorUtilities.CreateInstance<CreatePurchaseHandler>(services);
        var clientOp = Guid.CreateVersion7();
        var purchaseResult = await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                fixture.SupplierId,
                "INV-" + Guid.NewGuid().ToString("N")[..8],
                DateOnly.FromDateTime(DateTime.UtcNow),
                "Partial payment purchase",
                0m,
                PurchaseSettlementMode.External,
                fixture.ActorId,
                clientOp,
                [
                    new CreatePurchaseLineInput(
                        fixture.ProductId,
                        fixture.ProductUnitId,
                        10m,
                        100m,
                        150m,
                        [])
                ],
                InitialPaymentAmount: 400m,
                InitialPaymentMethod: SupplierSettlementMethod.External,
                InitialPaymentExternalReference: "BANK-TRANSFER-101"),
            CancellationToken.None);

        Assert.True(purchaseResult.IsSuccess, purchaseResult.Error?.Message);

        db.ChangeTracker.Clear();

        var entries = await db.SupplierAccountEntries
            .Where(x => x.SupplierId == fixture.SupplierId)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync();

        Assert.Equal(2, entries.Count);
        Assert.Equal(SupplierAccountEntryType.Purchase, entries[0].EntryType);
        Assert.Equal(1000m, entries[0].Amount);

        Assert.Equal(SupplierAccountEntryType.SupplierPayment, entries[1].EntryType);
        Assert.Equal(400m, entries[1].Amount);

        var netPayable = entries.Sum(e => e.Direction == SupplierAccountDirection.IncreasePayable ? e.Amount : -e.Amount);
        Assert.Equal(600m, netPayable);

        var payment = await db.SupplierPayments.SingleAsync(x => x.SupplierId == fixture.SupplierId);
        Assert.Equal(400m, payment.Amount);
        Assert.Equal(SupplierPaymentPurpose.Settlement, payment.Purpose);
    }

    [Fact]
    public async Task Purchase_FullPayment_ResultsInZeroPayable()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();

        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var fixture = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db, defaultSalePrice: 150m);

        var purchaseHandler = ActivatorUtilities.CreateInstance<CreatePurchaseHandler>(services);
        var purchaseResult = await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                fixture.SupplierId,
                "INV-" + Guid.NewGuid().ToString("N")[..8],
                DateOnly.FromDateTime(DateTime.UtcNow),
                "Full payment purchase",
                0m,
                PurchaseSettlementMode.External,
                fixture.ActorId,
                Guid.CreateVersion7(),
                [
                    new CreatePurchaseLineInput(
                        fixture.ProductId,
                        fixture.ProductUnitId,
                        5m,
                        100m,
                        150m,
                        [])
                ],
                InitialPaymentAmount: 500m),
            CancellationToken.None);

        Assert.True(purchaseResult.IsSuccess, purchaseResult.Error?.Message);

        db.ChangeTracker.Clear();
        var entries = await db.SupplierAccountEntries.Where(x => x.SupplierId == fixture.SupplierId).ToListAsync();
        var netPayable = entries.Sum(e => e.Direction == SupplierAccountDirection.IncreasePayable ? e.Amount : -e.Amount);
        Assert.Equal(0m, netPayable);
    }

    [Fact]
    public async Task Purchase_Serialized_AllocatesSequence_TrackingCode_And_LotBalances()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();

        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var fixture = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db);

        var serial1 = "SN-POSTGRES-1-" + Guid.NewGuid().ToString("N")[..8];
        var serial2 = "SN-POSTGRES-2-" + Guid.NewGuid().ToString("N")[..8];

        var purchaseHandler = ActivatorUtilities.CreateInstance<CreatePurchaseHandler>(services);
        var purchaseResult = await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                fixture.SupplierId,
                "INV-SER-" + Guid.NewGuid().ToString("N")[..8],
                DateOnly.FromDateTime(DateTime.UtcNow),
                "Serialized intake",
                0m,
                PurchaseSettlementMode.External,
                fixture.ActorId,
                Guid.CreateVersion7(),
                [
                    new CreatePurchaseLineInput(
                        fixture.ProductId,
                        fixture.ProductUnitId,
                        2m,
                        3000m,
                        5000m,
                        [
                            new SerializedIdentityInput(serial1),
                            new SerializedIdentityInput(serial2)
                        ])
                ],
                InitialPaymentAmount: 0m),
            CancellationToken.None);

        Assert.True(purchaseResult.IsSuccess, purchaseResult.Error?.Message);

        db.ChangeTracker.Clear();

        var supplierProduct = await db.SupplierProducts.SingleAsync(x => x.SupplierId == fixture.SupplierId && x.ProductId == fixture.ProductId);
        Assert.Equal(3L, supplierProduct.NextItemSequence); // 1 and 2 consumed, next is 3

        var units = await db.InventoryUnits.Where(x => x.ProductId == fixture.ProductId).OrderBy(x => x.ItemSequence).ToListAsync();
        Assert.Equal(2, units.Count);
        Assert.Equal(1L, units[0].ItemSequence);
        Assert.Equal(2L, units[1].ItemSequence);
        Assert.NotNull(units[0].SupplierCodeSnapshot);
        Assert.StartsWith(units[0].SupplierCodeSnapshot!, units[0].TrackingCode);
        Assert.Equal(InventoryUnitStatus.InStock, units[0].Status);
        Assert.Equal(3000m, units[0].AcquisitionCost);
    }

    [Fact]
    public async Task PurchaseReturn_ReducesStock_And_DecreasesPayable_WithoutAutoCashMovement()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();

        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var fixture = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db, defaultSalePrice: 150m);

        var purchaseHandler = ActivatorUtilities.CreateInstance<CreatePurchaseHandler>(services);
        var returnHandler = ActivatorUtilities.CreateInstance<CreatePurchaseReturnHandler>(services);

        var purchaseResult = await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                fixture.SupplierId,
                "INV-RET-" + Guid.NewGuid().ToString("N")[..8],
                DateOnly.FromDateTime(DateTime.UtcNow),
                "Initial purchase",
                0m,
                PurchaseSettlementMode.External,
                fixture.ActorId,
                Guid.CreateVersion7(),
                [
                    new CreatePurchaseLineInput(
                        fixture.ProductId,
                        fixture.ProductUnitId,
                        10m,
                        100m,
                        150m,
                        [])
                ],
                InitialPaymentAmount: 0m),
            CancellationToken.None);

        Assert.True(purchaseResult.IsSuccess, purchaseResult.Error?.Message);
        var purchaseId = purchaseResult.Value!.PurchaseId;

        db.ChangeTracker.Clear();
        var purchaseItem = await db.PurchaseItems.SingleAsync(x => x.PurchaseId == purchaseId);

        // Return 4 units
        var returnResult = await returnHandler.HandleAsync(
            new CreatePurchaseReturnCommand(
                purchaseId,
                "Defective batch",
                null,
                PurchaseReturnSettlementMode.External,
                fixture.ActorId,
                Guid.CreateVersion7(),
                [
                    new PurchaseReturnLineInput(purchaseItem.Id, 4m, null, [])
                ]),
            CancellationToken.None);

        Assert.True(returnResult.IsSuccess, returnResult.Error?.Message);
        Assert.Equal(400m, returnResult.Value!.SupplierReturnValue);

        db.ChangeTracker.Clear();

        // Stock decreased to 6
        var stock = await db.StockBalances.SingleAsync(x => x.ProductId == fixture.ProductId);
        Assert.Equal(6m, stock.SellableQty);

        // Payable decreased from 1000 to 600
        var entries = await db.SupplierAccountEntries.Where(x => x.SupplierId == fixture.SupplierId).ToListAsync();
        var netPayable = entries.Sum(e => e.Direction == SupplierAccountDirection.IncreasePayable ? e.Amount : -e.Amount);
        Assert.Equal(600m, netPayable);

        // Over-return should fail
        var overReturn = await returnHandler.HandleAsync(
            new CreatePurchaseReturnCommand(
                purchaseId,
                "Excess return",
                null,
                PurchaseReturnSettlementMode.External,
                fixture.ActorId,
                Guid.CreateVersion7(),
                [
                    new PurchaseReturnLineInput(purchaseItem.Id, 7m, null, [])
                ]),
            CancellationToken.None);

        Assert.False(overReturn.IsSuccess);
        Assert.Equal("purchasing.return_exceeds_original", overReturn.Error?.Code);
    }

    [Fact]
    public async Task PurchaseVoid_Unpaid_ReversesStockCostAndPayable_WithoutSequenceRollback()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();

        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var fixture = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db);

        var purchaseHandler = ActivatorUtilities.CreateInstance<CreatePurchaseHandler>(services);
        var voidHandler = ActivatorUtilities.CreateInstance<VoidPurchaseHandler>(services);

        var serial = "SN-VOID-" + Guid.NewGuid().ToString("N")[..8];
        var purchaseResult = await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                fixture.SupplierId,
                "INV-VOID-" + Guid.NewGuid().ToString("N")[..8],
                DateOnly.FromDateTime(DateTime.UtcNow),
                "Void test",
                0m,
                PurchaseSettlementMode.External,
                fixture.ActorId,
                Guid.CreateVersion7(),
                [
                    new CreatePurchaseLineInput(
                        fixture.ProductId,
                        fixture.ProductUnitId,
                        1m,
                        4000m,
                        6000m,
                        [new SerializedIdentityInput(serial)])
                ],
                InitialPaymentAmount: 0m),
            CancellationToken.None);

        Assert.True(purchaseResult.IsSuccess, purchaseResult.Error?.Message);
        var purchaseId = purchaseResult.Value!.PurchaseId;

        db.ChangeTracker.Clear();
        var seqBeforeVoid = (await db.SupplierProducts.SingleAsync(x => x.SupplierId == fixture.SupplierId && x.ProductId == fixture.ProductId)).NextItemSequence;

        var voidResult = await voidHandler.HandleAsync(
            new VoidPurchaseCommand(purchaseId, Guid.CreateVersion7(), fixture.ActorId, "Voided due to clerical error"),
            CancellationToken.None);

        Assert.True(voidResult.IsSuccess, voidResult.Error?.Message);

        db.ChangeTracker.Clear();

        // Sequence must NOT roll back
        var seqAfterVoid = (await db.SupplierProducts.SingleAsync(x => x.SupplierId == fixture.SupplierId && x.ProductId == fixture.ProductId)).NextItemSequence;
        Assert.Equal(seqBeforeVoid, seqAfterVoid);

        // Unit status must be ReceiptVoided
        var unit = await db.InventoryUnits.SingleAsync(x => x.ProductId == fixture.ProductId);
        Assert.Equal(InventoryUnitStatus.ReceiptVoided, unit.Status);

        // Stock and cost are 0
        var stock = await db.StockBalances.SingleAsync(x => x.ProductId == fixture.ProductId);
        Assert.Equal(0m, stock.SellableQty);

        var cost = await db.ProductCostStates.SingleAsync(x => x.ProductId == fixture.ProductId);
        Assert.Equal(0m, cost.TotalInventoryCost);

        // Payable is 0
        var entries = await db.SupplierAccountEntries.Where(x => x.SupplierId == fixture.SupplierId).ToListAsync();
        var netPayable = entries.Sum(e => e.Direction == SupplierAccountDirection.IncreasePayable ? e.Amount : -e.Amount);
        Assert.Equal(0m, netPayable);
    }

    [Fact]
    public async Task SupplierPayment_Advance_CreatesSupplierCredit_And_SettlementRejectsOverpayment()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();

        var supplier = await Phase2PostgresTestHarness.SeedSupplierAsync(db, "Khata Supplier");
        var actorId = await IntegrationIdentitySeeder.CreateActorAsync(db);

        var paymentHandler = ActivatorUtilities.CreateInstance<CreateSupplierPaymentHandler>(services);
        var reverseHandler = ActivatorUtilities.CreateInstance<ReverseSupplierPaymentHandler>(services);

        // 1. Settlement without existing payable must fail
        var invalidSettlement = await paymentHandler.HandleAsync(
            new CreateSupplierPaymentCommand(
                supplier.Id,
                5000m,
                SupplierPaymentPurpose.Settlement,
                SupplierSettlementMethod.External,
                actorId,
                Guid.CreateVersion7(),
                null,
                "Invalid settlement attempt"),
            CancellationToken.None);

        Assert.False(invalidSettlement.IsSuccess);
        Assert.Equal("supplier.payment_exceeds_payable", invalidSettlement.Error?.Code);

        // 2. Advance payment succeeds and creates negative payable (Supplier Credit)
        var advanceResult = await paymentHandler.HandleAsync(
            new CreateSupplierPaymentCommand(
                supplier.Id,
                5000m,
                SupplierPaymentPurpose.Advance,
                SupplierSettlementMethod.External,
                actorId,
                Guid.CreateVersion7(),
                null,
                "Advance payment"),
            CancellationToken.None);

        Assert.True(advanceResult.IsSuccess, advanceResult.Error?.Message);
        var paymentId = advanceResult.Value!.PaymentId;

        db.ChangeTracker.Clear();

        var entries = await db.SupplierAccountEntries.Where(x => x.SupplierId == supplier.Id).ToListAsync();
        var netPayable = entries.Sum(e => e.Direction == SupplierAccountDirection.IncreasePayable ? e.Amount : -e.Amount);
        Assert.Equal(-5000m, netPayable);

        // 3. Reverse the advance payment
        var reverseResult = await reverseHandler.HandleAsync(
            new ReverseSupplierPaymentCommand(
                paymentId,
                "Bank transaction reversed",
                actorId,
                Guid.CreateVersion7()),
            CancellationToken.None);

        Assert.True(reverseResult.IsSuccess, reverseResult.Error?.Message);

        db.ChangeTracker.Clear();
        var entriesAfterRev = await db.SupplierAccountEntries.Where(x => x.SupplierId == supplier.Id).ToListAsync();
        var netPayableAfterRev = entriesAfterRev.Sum(e => e.Direction == SupplierAccountDirection.IncreasePayable ? e.Amount : -e.Amount);
        Assert.Equal(0m, netPayableAfterRev);
    }

    [Fact]
    public async Task SaleReturn_RestoresStockAndCost_And_EnforcesReturnLimits()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();

        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var fixture = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db, defaultSalePrice: 150m);

        var purchaseHandler = ActivatorUtilities.CreateInstance<CreatePurchaseHandler>(services);
        var saleHandler = ActivatorUtilities.CreateInstance<CompleteSaleHandler>(services);
        var returnHandler = ActivatorUtilities.CreateInstance<CreateSaleReturnHandler>(services);

        // Purchase 10 at 100
        await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                fixture.SupplierId,
                "INV-SALE-RET",
                DateOnly.FromDateTime(DateTime.UtcNow),
                null,
                0m,
                PurchaseSettlementMode.External,
                fixture.ActorId,
                Guid.CreateVersion7(),
                [new CreatePurchaseLineInput(fixture.ProductId, fixture.ProductUnitId, 10m, 100m, 150m, [])],
                InitialPaymentAmount: 0m),
            CancellationToken.None);

        // Sell 5 at 150
        var saleResult = await saleHandler.HandleAsync(
            new CompleteSaleCommand(
                Guid.CreateVersion7(),
                null,
                fixture.ActorId,
                null,
                0m,
                SalePaymentMethod.Bank,
                750m,
                "BANK-REF",
                null,
                [new CompleteSaleLineInput(fixture.ProductId, fixture.ProductUnitId, 5m, 150m, [])]),
            CancellationToken.None);

        Assert.True(saleResult.IsSuccess, saleResult.Error?.Message);
        var saleId = saleResult.Value!.SaleId;

        db.ChangeTracker.Clear();
        var saleItem = await db.SaleItems.SingleAsync(x => x.SaleId == saleId);

        // Return 2 units
        var returnResult = await returnHandler.HandleAsync(
            new CreateSaleReturnCommand(
                saleId,
                "RETURN",
                "Customer changed mind",
                RefundMethod.Bank,
                fixture.ActorId,
                Guid.CreateVersion7(),
                [new SaleReturnLineInput(saleItem.Id, 2m, SaleReturnDisposition.RestockSellable, [])]),
            CancellationToken.None);

        Assert.True(returnResult.IsSuccess, returnResult.Error?.Message);

        db.ChangeTracker.Clear();

        // Stock restored from 5 to 7
        var stock = await db.StockBalances.SingleAsync(x => x.ProductId == fixture.ProductId);
        Assert.Equal(7m, stock.SellableQty);

        // Cost restored: 500 remaining + 200 returned = 700
        var cost = await db.ProductCostStates.SingleAsync(x => x.ProductId == fixture.ProductId);
        Assert.Equal(7m, cost.CostedQty);
        Assert.Equal(700m, cost.TotalInventoryCost);

        // Attempting to return 4 more units must fail (only 3 remain eligible)
        var overReturn = await returnHandler.HandleAsync(
            new CreateSaleReturnCommand(
                saleId,
                "RETURN",
                "Over return attempt",
                RefundMethod.Bank,
                fixture.ActorId,
                Guid.CreateVersion7(),
                [new SaleReturnLineInput(saleItem.Id, 4m, SaleReturnDisposition.RestockSellable, [])]),
            CancellationToken.None);

        Assert.False(overReturn.IsSuccess);
        Assert.Equal("sales.return_exceeds_original", overReturn.Error?.Code);
    }

    [Fact]
    public async Task CommercialExchange_ExecutesReturnAndSaleAtomically_WithDifferenceSettlement()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();

        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var fixtureA = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db, defaultSalePrice: 100m);
        var fixtureB = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db, defaultSalePrice: 150m);

        var purchaseHandler = ActivatorUtilities.CreateInstance<CreatePurchaseHandler>(services);
        var saleHandler = ActivatorUtilities.CreateInstance<CompleteSaleHandler>(services);
        var exchangeHandler = ActivatorUtilities.CreateInstance<CommercialExchangeHandler>(services);

        // Purchase A (10 at 80) and B (10 at 120)
        await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                fixtureA.SupplierId,
                "INV-EXCH-A",
                DateOnly.FromDateTime(DateTime.UtcNow),
                null,
                0m,
                PurchaseSettlementMode.External,
                fixtureA.ActorId,
                Guid.CreateVersion7(),
                [new CreatePurchaseLineInput(fixtureA.ProductId, fixtureA.ProductUnitId, 10m, 80m, 100m, [])],
                InitialPaymentAmount: 0m),
            CancellationToken.None);

        await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                fixtureB.SupplierId,
                "INV-EXCH-B",
                DateOnly.FromDateTime(DateTime.UtcNow),
                null,
                0m,
                PurchaseSettlementMode.External,
                fixtureB.ActorId,
                Guid.CreateVersion7(),
                [new CreatePurchaseLineInput(fixtureB.ProductId, fixtureB.ProductUnitId, 10m, 120m, 150m, [])],
                InitialPaymentAmount: 0m),
            CancellationToken.None);

        // Initial Sale of Item A (2 units = 200m)
        var initialSale = await saleHandler.HandleAsync(
            new CompleteSaleCommand(
                Guid.CreateVersion7(),
                null,
                fixtureA.ActorId,
                null,
                0m,
                SalePaymentMethod.Bank,
                200m,
                "INITIAL-SALE",
                null,
                [new CompleteSaleLineInput(fixtureA.ProductId, fixtureA.ProductUnitId, 2m, 100m, [])]),
            CancellationToken.None);

        Assert.True(initialSale.IsSuccess);
        var originalSaleId = initialSale.Value!.SaleId;

        db.ChangeTracker.Clear();
        var saleItemA = await db.SaleItems.SingleAsync(x => x.SaleId == originalSaleId);

        // Commercial Exchange: Return 1 unit of A (refund 100m) and Purchase 1 unit of B (price 150m).
        // Net difference = +50m customer payment
        var exchangeOp = Guid.CreateVersion7();
        var exchangeResult = await exchangeHandler.HandleAsync(
            new CommercialExchangeCommand(
                exchangeOp,
                originalSaleId,
                fixtureA.ActorId,
                null,
                null,
                "DEFECT_SWAP",
                "Swapping item A for upgraded item B",
                [new SaleReturnLineInput(saleItemA.Id, 1m, SaleReturnDisposition.RestockSellable, [])],
                [new CompleteSaleLineInput(fixtureB.ProductId, fixtureB.ProductUnitId, 1m, 150m, [])],
                0m,
                SalePaymentMethod.Bank,
                50m,
                "EXCHANGE-DIFF-PAYMENT"),
            CancellationToken.None);

        Assert.True(exchangeResult.IsSuccess, exchangeResult.Error?.Message);
        Assert.Equal(150m, exchangeResult.Value!.ReplacementGrandTotal);
        Assert.Equal(100m, exchangeResult.Value.ReturnRefundAmount);
        Assert.Equal(50m, exchangeResult.Value.NetDifference);

        db.ChangeTracker.Clear();

        // Product A stock: 10 - 2 sold + 1 returned = 9
        var stockA = await db.StockBalances.SingleAsync(x => x.ProductId == fixtureA.ProductId);
        Assert.Equal(9m, stockA.SellableQty);

        // Product B stock: 10 - 1 sold in exchange = 9
        var stockB = await db.StockBalances.SingleAsync(x => x.ProductId == fixtureB.ProductId);
        Assert.Equal(9m, stockB.SellableQty);
    }

    [Fact]
    public async Task CustomerWarranty_And_Replacement_Lifecycle_Invariants()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();

        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var fixture = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db, defaultSalePrice: 8000m);
        var customer = await Phase2PostgresTestHarness.SeedCustomerAsync(db, "Warranty Customer");

        var purchaseHandler = ActivatorUtilities.CreateInstance<CreatePurchaseHandler>(services);
        var saleHandler = ActivatorUtilities.CreateInstance<CompleteSaleHandler>(services);
        var claimHandler = ActivatorUtilities.CreateInstance<CreateWarrantyClaimHandler>(services);
        var reviewHandler = ActivatorUtilities.CreateInstance<BeginWarrantyClaimReviewHandler>(services);
        var sendToSupplierHandler = ActivatorUtilities.CreateInstance<SendWarrantyClaimToSupplierHandler>(services);
        var supplierProcessingHandler = ActivatorUtilities.CreateInstance<MarkWarrantySupplierProcessingHandler>(services);
        var receiveReplacementHandler = ActivatorUtilities.CreateInstance<ReceiveCustomerWarrantyReplacementHandler>(services);
        var handoverHandler = ActivatorUtilities.CreateInstance<HandoverWarrantyItemHandler>(services);

        var serialOriginal = "SN-WARR-ORIG-" + Guid.NewGuid().ToString("N")[..8];
        var serialReplacement = "SN-WARR-REPL-" + Guid.NewGuid().ToString("N")[..8];

        // 1. Purchase
        await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                fixture.SupplierId,
                "INV-WARR-01",
                DateOnly.FromDateTime(DateTime.UtcNow),
                null,
                0m,
                PurchaseSettlementMode.External,
                fixture.ActorId,
                Guid.CreateVersion7(),
                [
                    new CreatePurchaseLineInput(
                        fixture.ProductId,
                        fixture.ProductUnitId,
                        1m,
                        5000m,
                        8000m,
                        [new SerializedIdentityInput(serialOriginal)])
                ],
                InitialPaymentAmount: 0m),
            CancellationToken.None);

        db.ChangeTracker.Clear();
        var unitOriginal = await db.InventoryUnits.SingleAsync(x => x.ProductId == fixture.ProductId);

        // 2. Sale
        var saleResult = await saleHandler.HandleAsync(
            new CompleteSaleCommand(
                Guid.CreateVersion7(),
                null,
                fixture.ActorId,
                customer.Id,
                0m,
                SalePaymentMethod.Bank,
                8000m,
                "BANK-WARR",
                null,
                [new CompleteSaleLineInput(fixture.ProductId, fixture.ProductUnitId, 1m, 8000m, [unitOriginal.Id])]),
            CancellationToken.None);

        Assert.True(saleResult.IsSuccess);
        var saleId = saleResult.Value!.SaleId;

        db.ChangeTracker.Clear();
        var saleItem = await db.SaleItems.SingleAsync(x => x.SaleId == saleId);

        // 3. Create Claim
        var claimResult = await claimHandler.HandleAsync(
            new CreateWarrantyClaimCommand(
                customer.Id,
                saleId,
                fixture.SupplierId,
                fixture.ActorId,
                [
                    new WarrantyClaimItemInput(
                        fixture.ProductId,
                        1m,
                        "Screen flickering",
                        saleItem.Id,
                        DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)),
                        [new WarrantyClaimUnitInput(unitOriginal.Id, serialOriginal)])
                ], Guid.CreateVersion7()),
            CancellationToken.None);

        Assert.True(claimResult.IsSuccess, claimResult.Error?.Message);
        var claimId = claimResult.Value;

        db.ChangeTracker.Clear();
        var claimItem = await db.WarrantyClaimItems.SingleAsync(x => x.ClaimId == claimId);
        var claimUnit = await db.WarrantyClaimItemUnits.SingleAsync(x => x.ClaimItemId == claimItem.Id);

        // Custody transitions — each user intent has its own durable operation identity.
        await reviewHandler.HandleAsync(
            new BeginWarrantyClaimReviewCommand(claimId, fixture.ActorId, Guid.CreateVersion7(), "Under review"),
            CancellationToken.None);
        await sendToSupplierHandler.HandleAsync(
            new SendWarrantyClaimToSupplierCommand(claimId, fixture.ActorId, Guid.CreateVersion7(), "Sent via courier"),
            CancellationToken.None);
        await supplierProcessingHandler.HandleAsync(
            new MarkWarrantySupplierProcessingCommand(claimId, fixture.ActorId, Guid.CreateVersion7(), "Supplier accepted unit for replacement"),
            CancellationToken.None);

        // 4. Receive Customer Replacement Unit
        var receiveResult = await receiveReplacementHandler.HandleAsync(
            new ReceiveCustomerWarrantyReplacementCommand(
                claimId,
                fixture.ActorId,
                Guid.CreateVersion7(),
                [new CustomerWarrantyReplacementUnitInput(claimUnit.Id, serialReplacement, null, null)],
                "Supplier replacement received"),
            CancellationToken.None);

        Assert.True(receiveResult.IsSuccess, receiveResult.Error?.Message);

        db.ChangeTracker.Clear();

        // Check new replacement unit: must be WarrantyCustomerHeld
        var replacementUnit = await db.InventoryUnits.SingleAsync(x => x.SerialNumber == serialReplacement.ToUpperInvariant());
        Assert.Equal(InventoryUnitStatus.WarrantyCustomerHeld, replacementUnit.Status);

        // Invariant check: Customer-owned replacement contributes ZERO to shop SellableQty and ZERO to shop cost
        var stock = await db.StockBalances.SingleAsync(x => x.ProductId == fixture.ProductId);
        Assert.Equal(0m, stock.SellableQty);

        var cost = await db.ProductCostStates.SingleAsync(x => x.ProductId == fixture.ProductId);
        Assert.Equal(0m, cost.TotalInventoryCost);

        // Verify claim is ReadyForCustomer after receiving replacement
        var claim = await db.WarrantyClaims.SingleAsync(x => x.Id == claimId);
        Assert.Equal(WarrantyClaimStatus.ReadyForCustomer, claim.Status);
        Assert.Equal(WarrantyCustody.WithShop, claim.CurrentCustody);

        // 5. Customer Handover
        var handoverResult = await handoverHandler.HandleAsync(
            new HandoverWarrantyItemCommand(
                claimId,
                fixture.ActorId,
                Guid.CreateVersion7(),
                "Replacement unit handed over to customer"),
            CancellationToken.None);

        Assert.True(handoverResult.IsSuccess, handoverResult.Error?.Message);

        db.ChangeTracker.Clear();
        var handedOverUnit = await db.InventoryUnits.SingleAsync(x => x.SerialNumber == serialReplacement.ToUpperInvariant());
        Assert.Equal(InventoryUnitStatus.WarrantyCustomerHandedOver, handedOverUnit.Status);
    }
}
