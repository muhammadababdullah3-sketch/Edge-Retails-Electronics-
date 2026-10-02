using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
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
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EdgeRetails.IntegrationTests;

[Collection("Phase2PostgresIntegration")]
public sealed class Phase1HostileGoldenTracePostgresTests
{
    // =========================================================================
    // 1. FULL GOLDEN TRACE ON REAL POSTGRESQL (PURCHASE -> SALE -> RETURN -> WARRANTY -> REPLACEMENT)
    // =========================================================================

    [Fact]
    public async Task GoldenTrace_FullPhysicalLifecycle_OnRealPostgreSql()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();

        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var fixture = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db, defaultSalePrice: 10000m);
        var customer = await Phase2PostgresTestHarness.SeedCustomerAsync(db, "GoldenTrace Customer");

        var purchaseHandler = ActivatorUtilities.CreateInstance<CreatePurchaseHandler>(services);
        var saleHandler = ActivatorUtilities.CreateInstance<CompleteSaleHandler>(services);
        var returnHandler = ActivatorUtilities.CreateInstance<CreateSaleReturnHandler>(services);
        var claimHandler = ActivatorUtilities.CreateInstance<CreateWarrantyClaimHandler>(services);
        var reviewHandler = ActivatorUtilities.CreateInstance<BeginWarrantyClaimReviewHandler>(services);
        var sendToSupplierHandler = ActivatorUtilities.CreateInstance<SendWarrantyClaimToSupplierHandler>(services);
        var supplierProcessingHandler = ActivatorUtilities.CreateInstance<MarkWarrantySupplierProcessingHandler>(services);
        var receiveReplacementHandler = ActivatorUtilities.CreateInstance<ReceiveCustomerWarrantyReplacementHandler>(services);
        var handoverHandler = ActivatorUtilities.CreateInstance<HandoverWarrantyItemHandler>(services);

        var serialReturnUnit = "SN-GT-RET-" + Guid.NewGuid().ToString("N")[..8];
        var serialWarrantyUnit = "SN-GT-WAR-" + Guid.NewGuid().ToString("N")[..8];
        var serialReplacementUnit = "SN-GT-REPL-" + Guid.NewGuid().ToString("N")[..8];

        // 1. Purchase & Receive 2 Serialized Units
        var purchaseResult = await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                fixture.SupplierId,
                "INV-GT-001",
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
                        2m,
                        6000m,
                        10000m,
                        [
                            new SerializedIdentityInput(serialReturnUnit),
                            new SerializedIdentityInput(serialWarrantyUnit)
                        ])
                ],
                InitialPaymentAmount: 0m),
            CancellationToken.None);

        Assert.True(purchaseResult.IsSuccess, purchaseResult.Error?.Message);

        db.ChangeTracker.Clear();
        var receivedUnits = await db.InventoryUnits
            .AsNoTracking()
            .Where(x => x.ProductId == fixture.ProductId)
            .OrderBy(x => x.ItemSequence)
            .ToListAsync();

        Assert.Equal(2, receivedUnits.Count);
        Assert.All(receivedUnits, x => Assert.Equal(InventoryUnitStatus.InStock, x.Status));
        Assert.All(receivedUnits, x => Assert.False(string.IsNullOrWhiteSpace(x.TrackingCode)));
        Assert.All(receivedUnits, x => Assert.Equal(6000m, x.AcquisitionCost));

        var unitToReturn = receivedUnits.Single(x => x.SerialNumber == serialReturnUnit.ToUpperInvariant());
        var unitToClaim = receivedUnits.Single(x => x.SerialNumber == serialWarrantyUnit.ToUpperInvariant());

        // 2. Exact-Unit Sale (Sell both units to customer)
        var saleResult = await saleHandler.HandleAsync(
            new CompleteSaleCommand(
                Guid.CreateVersion7(),
                null,
                fixture.ActorId,
                customer.Id,
                0m,
                SalePaymentMethod.Bank,
                20000m,
                "BANK-GT",
                null,
                [
                    new CompleteSaleLineInput(
                        fixture.ProductId,
                        fixture.ProductUnitId,
                        2m,
                        10000m,
                        [unitToReturn.Id, unitToClaim.Id])
                ]),
            CancellationToken.None);

        Assert.True(saleResult.IsSuccess, saleResult.Error?.Message);
        var saleId = saleResult.Value!.SaleId;

        db.ChangeTracker.Clear();
        var soldUnits = await db.InventoryUnits
            .AsNoTracking()
            .Where(x => x.ProductId == fixture.ProductId)
            .ToListAsync();
        Assert.All(soldUnits, x => Assert.Equal(InventoryUnitStatus.Sold, x.Status));

        var saleItem = await db.SaleItems.AsNoTracking().SingleAsync(x => x.SaleId == saleId);

        // 3. Sale Return: Unit 1 returned as Damaged
        var returnResult = await returnHandler.HandleAsync(
            new CreateSaleReturnCommand(
                saleId,
                "DEFECTIVE",
                "Damaged by customer",
                RefundMethod.Bank,
                fixture.ActorId,
                Guid.CreateVersion7(),
                [
                    new SaleReturnLineInput(
                        saleItem.Id,
                        1m,
                        SaleReturnDisposition.Damaged,
                        [unitToReturn.Id])
                ]),
            CancellationToken.None);

        Assert.True(returnResult.IsSuccess, returnResult.Error?.Message);

        db.ChangeTracker.Clear();
        var returnedUnit = await db.InventoryUnits.AsNoTracking().SingleAsync(x => x.Id == unitToReturn.Id);
        Assert.Equal(InventoryUnitStatus.Damaged, returnedUnit.Status);

        // 4. Warranty Claim on Unit 2
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
                        "Hardware malfunction",
                        saleItem.Id,
                        DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)),
                        [new WarrantyClaimUnitInput(unitToClaim.Id, serialWarrantyUnit)])
                ],
                Guid.CreateVersion7()),
            CancellationToken.None);

        Assert.True(claimResult.IsSuccess, claimResult.Error?.Message);
        var claimId = claimResult.Value;

        db.ChangeTracker.Clear();
        var claimItem = await db.WarrantyClaimItems.AsNoTracking().SingleAsync(x => x.ClaimId == claimId);
        var claimUnitLink = await db.WarrantyClaimItemUnits.AsNoTracking().SingleAsync(x => x.ClaimItemId == claimItem.Id);

        // Custody transitions
        await reviewHandler.HandleAsync(new BeginWarrantyClaimReviewCommand(claimId, fixture.ActorId, Guid.CreateVersion7(), "Under review"), CancellationToken.None);
        await sendToSupplierHandler.HandleAsync(new SendWarrantyClaimToSupplierCommand(claimId, fixture.ActorId, Guid.CreateVersion7(), "Sent to supplier"), CancellationToken.None);
        await supplierProcessingHandler.HandleAsync(new MarkWarrantySupplierProcessingCommand(claimId, fixture.ActorId, Guid.CreateVersion7(), "Supplier replacement approved"), CancellationToken.None);

        // 5. Warranty Replacement
        var receiveReplResult = await receiveReplacementHandler.HandleAsync(
            new ReceiveCustomerWarrantyReplacementCommand(
                claimId,
                fixture.ActorId,
                Guid.CreateVersion7(),
                [new CustomerWarrantyReplacementUnitInput(claimUnitLink.Id, serialReplacementUnit, null, null)],
                "Supplier replacement received"),
            CancellationToken.None);

        Assert.True(receiveReplResult.IsSuccess, receiveReplResult.Error?.Message);

        db.ChangeTracker.Clear();
        var replacementUnit = await db.InventoryUnits.AsNoTracking()
            .SingleAsync(x => x.SerialNumber == serialReplacementUnit.ToUpperInvariant());
        Assert.Equal(InventoryUnitStatus.WarrantyCustomerHeld, replacementUnit.Status);
        Assert.Equal(InventoryUnitOriginType.WarrantyReplacement, replacementUnit.OriginType);
        Assert.NotEqual(unitToClaim.Id, replacementUnit.Id);
        Assert.NotEqual(unitToClaim.TrackingCode, replacementUnit.TrackingCode);

        // 6. Customer Handover
        var handoverResult = await handoverHandler.HandleAsync(
            new HandoverWarrantyItemCommand(claimId, fixture.ActorId, Guid.CreateVersion7(), "Handed over to customer"),
            CancellationToken.None);

        Assert.True(handoverResult.IsSuccess, handoverResult.Error?.Message);

        db.ChangeTracker.Clear();
        var finalReplacementUnit = await db.InventoryUnits.AsNoTracking().SingleAsync(x => x.Id == replacementUnit.Id);
        Assert.Equal(InventoryUnitStatus.WarrantyCustomerHandedOver, finalReplacementUnit.Status);

        // Final Provenance Check: Relational links intact in PostgreSQL
        var finalClaimLink = await db.WarrantyClaimItemUnits.AsNoTracking().SingleAsync(x => x.ClaimItemId == claimItem.Id);
        Assert.Equal(replacementUnit.Id, finalClaimLink.ReplacementInventoryUnitId);
        Assert.Equal(unitToClaim.Id, finalClaimLink.OriginalInventoryUnitId);
    }

    // =========================================================================
    // 2. HOSTILE A: CONCURRENT DOUBLE-SALE OF SAME UNIT (EXACTLY ONE SUCCEEDS)
    // =========================================================================

    [Fact]
    public async Task Concurrent_DoubleSale_SameUnit_ExactlyOneSucceeds_OnRealPostgreSql()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var setupScope = provider.CreateAsyncScope();
        var setupDb = setupScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(setupDb);
        var fixture = await Phase2PostgresTestHarness.SeedSerializedProductAsync(setupDb, defaultSalePrice: 5000m);
        var serial = "SN-RACE-SALE-" + Guid.NewGuid().ToString("N")[..8];

        var purchaseHandler = ActivatorUtilities.CreateInstance<CreatePurchaseHandler>(setupScope.ServiceProvider);
        await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                fixture.SupplierId,
                "INV-DBL-SALE",
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
                        3000m,
                        5000m,
                        [new SerializedIdentityInput(serial)])
                ],
                InitialPaymentAmount: 0m),
            CancellationToken.None);

        setupDb.ChangeTracker.Clear();
        var unit = await setupDb.InventoryUnits.SingleAsync(x => x.SerialNumber == serial.ToUpperInvariant());
        Assert.Equal(InventoryUnitStatus.InStock, unit.Status);

        var barrier = new Barrier(2);

        var task1 = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            var handler = ActivatorUtilities.CreateInstance<CompleteSaleHandler>(scope.ServiceProvider);
            barrier.SignalAndWait();
            return await handler.HandleAsync(
                new CompleteSaleCommand(
                    Guid.CreateVersion7(),
                    null,
                    fixture.ActorId,
                    null,
                    0m,
                    SalePaymentMethod.Bank,
                    5000m,
                    "BANK",
                    null,
                    [new CompleteSaleLineInput(fixture.ProductId, fixture.ProductUnitId, 1m, 5000m, [unit.Id])]),
                CancellationToken.None);
        });

        var task2 = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            var handler = ActivatorUtilities.CreateInstance<CompleteSaleHandler>(scope.ServiceProvider);
            barrier.SignalAndWait();
            return await handler.HandleAsync(
                new CompleteSaleCommand(
                    Guid.CreateVersion7(),
                    null,
                    fixture.ActorId,
                    null,
                    0m,
                    SalePaymentMethod.Bank,
                    5000m,
                    "BANK",
                    null,
                    [new CompleteSaleLineInput(fixture.ProductId, fixture.ProductUnitId, 1m, 5000m, [unit.Id])]),
                CancellationToken.None);
        });

        var results = await Task.WhenAll(task1, task2);

        // Exactly one succeeds, one fails closed
        var successCount = results.Count(r => r.IsSuccess);
        var failureCount = results.Count(r => !r.IsSuccess);

        Assert.Equal(1, successCount);
        Assert.Equal(1, failureCount);

        var failedResult = results.Single(r => !r.IsSuccess);
        Assert.Equal("sales.serial_not_sellable", failedResult.Error?.Code);

        // Verify PostgreSQL row state
        await using var verifyScope = provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        var verifyUnit = await verifyDb.InventoryUnits.AsNoTracking().SingleAsync(x => x.Id == unit.Id);
        Assert.Equal(InventoryUnitStatus.Sold, verifyUnit.Status);

        var stock = await verifyDb.StockBalances.AsNoTracking().SingleAsync(x => x.ProductId == fixture.ProductId);
        Assert.Equal(0m, stock.SellableQty);
    }

    // =========================================================================
    // 3. HOSTILE B: SALE RESPONSE-LOSS REPLAY (SAFE IDEMPOTENCY)
    // =========================================================================

    [Fact]
    public async Task Sale_ResponseLoss_Replay_Preserves_OriginalSale_Without_Duplicate_Stock_Movement_OnRealPostgreSql()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();

        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var fixture = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db, defaultSalePrice: 4000m);
        var serial = "SN-REPLAY-SALE-" + Guid.NewGuid().ToString("N")[..8];

        var purchaseHandler = ActivatorUtilities.CreateInstance<CreatePurchaseHandler>(services);
        await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                fixture.SupplierId,
                "INV-REPLAY-01",
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
                        2500m,
                        4000m,
                        [new SerializedIdentityInput(serial)])
                ],
                InitialPaymentAmount: 0m),
            CancellationToken.None);

        db.ChangeTracker.Clear();
        var unit = await db.InventoryUnits.SingleAsync(x => x.SerialNumber == serial.ToUpperInvariant());

        var clientOpId = Guid.CreateVersion7();
        var saleCommand = new CompleteSaleCommand(
            clientOpId,
            null,
            fixture.ActorId,
            null,
            0m,
            SalePaymentMethod.Bank,
            4000m,
            "BANK-REPLAY",
            null,
            [new CompleteSaleLineInput(fixture.ProductId, fixture.ProductUnitId, 1m, 4000m, [unit.Id])]);

        var saleHandler = ActivatorUtilities.CreateInstance<CompleteSaleHandler>(services);

        // First attempt (commits)
        var result1 = await saleHandler.HandleAsync(saleCommand, CancellationToken.None);
        Assert.True(result1.IsSuccess, result1.Error?.Message);
        Assert.False(result1.Value!.WasExisting);
        var saleId = result1.Value.SaleId;

        // Second attempt (simulating response loss retry with same ClientOperationId)
        var result2 = await saleHandler.HandleAsync(saleCommand, CancellationToken.None);
        Assert.True(result2.IsSuccess, result2.Error?.Message);
        Assert.True(result2.Value!.WasExisting);
        Assert.Equal(saleId, result2.Value.SaleId);

        // Verify database: exactly 1 sale row, 1 sale item, and 1 sale movement (no duplicate decrement)
        db.ChangeTracker.Clear();
        var sales = await db.Sales.Where(x => x.ClientOperationId == clientOpId).ToListAsync();
        Assert.Single(sales);

        var saleMovements = await db.InventoryMovements
            .Where(x => x.ProductId == fixture.ProductId && x.MovementType == InventoryMovementType.SaleOut)
            .ToListAsync();
        Assert.Single(saleMovements);
    }

    // =========================================================================
    // 4. HOSTILE C: SALE VS PURCHASE RETURN COMPETING ON SAME UNIT
    // =========================================================================

    [Fact]
    public async Task Sale_Vs_PurchaseReturn_Competing_SameUnit_FailsSafely_OnRealPostgreSql()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var setupScope = provider.CreateAsyncScope();
        var setupDb = setupScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(setupDb);
        var fixture = await Phase2PostgresTestHarness.SeedSerializedProductAsync(setupDb, defaultSalePrice: 7000m);
        var serial = "SN-RACE-SALE-PRETURN-" + Guid.NewGuid().ToString("N")[..8];

        var purchaseHandler = ActivatorUtilities.CreateInstance<CreatePurchaseHandler>(setupScope.ServiceProvider);
        var purchaseResult = await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                fixture.SupplierId,
                "INV-COMPETE-01",
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
                        4500m,
                        7000m,
                        [new SerializedIdentityInput(serial)])
                ],
                InitialPaymentAmount: 0m),
            CancellationToken.None);

        Assert.True(purchaseResult.IsSuccess);
        var purchaseId = purchaseResult.Value!.PurchaseId;

        setupDb.ChangeTracker.Clear();
        var unit = await setupDb.InventoryUnits.SingleAsync(x => x.SerialNumber == serial.ToUpperInvariant());
        var purchaseItem = await setupDb.PurchaseItems.SingleAsync(x => x.PurchaseId == purchaseId);

        var barrier = new Barrier(2);

        // Task 1: Complete Sale
        var taskSale = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            var handler = ActivatorUtilities.CreateInstance<CompleteSaleHandler>(scope.ServiceProvider);
            barrier.SignalAndWait();
            return await handler.HandleAsync(
                new CompleteSaleCommand(
                    Guid.CreateVersion7(),
                    null,
                    fixture.ActorId,
                    null,
                    0m,
                    SalePaymentMethod.Bank,
                    7000m,
                    "BANK",
                    null,
                    [new CompleteSaleLineInput(fixture.ProductId, fixture.ProductUnitId, 1m, 7000m, [unit.Id])]),
                CancellationToken.None);
        });

        // Task 2: Purchase Return to supplier
        var taskReturn = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            var handler = ActivatorUtilities.CreateInstance<CreatePurchaseReturnHandler>(scope.ServiceProvider);
            barrier.SignalAndWait();
            return await handler.HandleAsync(
                new CreatePurchaseReturnCommand(
                    purchaseId,
                    "DEFECTIVE",
                    "Return to supplier",
                    PurchaseReturnSettlementMode.External,
                    fixture.ActorId,
                    Guid.CreateVersion7(),
                    [new PurchaseReturnLineInput(purchaseItem.Id, 1m, 4500m, [unit.Id])]),
                CancellationToken.None);
        });

        var saleRes = await taskSale;
        var returnRes = await taskReturn;

        // Exactly one must succeed, the other must fail closed
        Assert.True(saleRes.IsSuccess ^ returnRes.IsSuccess);

        await using var verifyScope = provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var finalUnit = await verifyDb.InventoryUnits.AsNoTracking().SingleAsync(x => x.Id == unit.Id);

        // The unit must either be Sold OR SupplierReturned, never both
        Assert.True(finalUnit.Status == InventoryUnitStatus.Sold || finalUnit.Status == InventoryUnitStatus.SupplierReturned);
    }

    // =========================================================================
    // 5. HOSTILE D: SALE RETURN VS WARRANTY CONFLICT (PREVENTS CONTRADICTION)
    // =========================================================================

    [Fact]
    public async Task SaleReturn_Vs_Warranty_Conflict_PreventsContradictoryState_OnRealPostgreSql()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();

        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var fixture = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db, defaultSalePrice: 6000m);
        var customer = await Phase2PostgresTestHarness.SeedCustomerAsync(db, "Conflict Customer");
        var serial = "SN-CONFLICT-" + Guid.NewGuid().ToString("N")[..8];

        var purchaseHandler = ActivatorUtilities.CreateInstance<CreatePurchaseHandler>(services);
        var saleHandler = ActivatorUtilities.CreateInstance<CompleteSaleHandler>(services);
        var returnHandler = ActivatorUtilities.CreateInstance<CreateSaleReturnHandler>(services);
        var claimHandler = ActivatorUtilities.CreateInstance<CreateWarrantyClaimHandler>(services);

        // Purchase & Sell
        await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                fixture.SupplierId,
                "INV-CONF-01",
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
                        3500m,
                        6000m,
                        [new SerializedIdentityInput(serial)])
                ],
                InitialPaymentAmount: 0m),
            CancellationToken.None);

        db.ChangeTracker.Clear();
        var unit = await db.InventoryUnits.SingleAsync(x => x.SerialNumber == serial.ToUpperInvariant());

        var saleResult = await saleHandler.HandleAsync(
            new CompleteSaleCommand(
                Guid.CreateVersion7(),
                null,
                fixture.ActorId,
                customer.Id,
                0m,
                SalePaymentMethod.Bank,
                6000m,
                "BANK",
                null,
                [new CompleteSaleLineInput(fixture.ProductId, fixture.ProductUnitId, 1m, 6000m, [unit.Id])]),
            CancellationToken.None);

        var saleId = saleResult.Value!.SaleId;
        db.ChangeTracker.Clear();
        var saleItem = await db.SaleItems.SingleAsync(x => x.SaleId == saleId);

        // Active warranty claim is filed
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
                        "Faulty battery",
                        saleItem.Id,
                        DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)),
                        [new WarrantyClaimUnitInput(unit.Id, serial)])
                ],
                Guid.CreateVersion7()),
            CancellationToken.None);

        Assert.True(claimResult.IsSuccess, claimResult.Error?.Message);

        // Attempting a Sale Return while unit is under active warranty claim MUST fail closed
        var returnResult = await returnHandler.HandleAsync(
            new CreateSaleReturnCommand(
                saleId,
                "DEFECTIVE",
                "Customer wants refund during active warranty",
                RefundMethod.Bank,
                fixture.ActorId,
                Guid.CreateVersion7(),
                [new SaleReturnLineInput(saleItem.Id, 1m, SaleReturnDisposition.RestockSellable, [unit.Id])]),
            CancellationToken.None);

        Assert.False(returnResult.IsSuccess);
        Assert.Equal("sales.return_unit_active_warranty", returnResult.Error?.Code);

        // Invariant: Unit status remains Sold under warranty, NOT restocked
        db.ChangeTracker.Clear();
        var verifyUnit = await db.InventoryUnits.AsNoTracking().SingleAsync(x => x.Id == unit.Id);
        Assert.Equal(InventoryUnitStatus.Sold, verifyUnit.Status);
    }

    // =========================================================================
    // 6. HOSTILE E: WARRANTY REPLACEMENT REPLAY (PRESERVES SINGLE UNIT & SEQUENCE)
    // =========================================================================

    [Fact]
    public async Task Warranty_Replacement_Replay_Preserves_Single_Unit_And_Sequence_OnRealPostgreSql()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();

        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var fixture = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db, defaultSalePrice: 8000m);
        var customer = await Phase2PostgresTestHarness.SeedCustomerAsync(db, "Replacement Customer");

        var purchaseHandler = ActivatorUtilities.CreateInstance<CreatePurchaseHandler>(services);
        var saleHandler = ActivatorUtilities.CreateInstance<CompleteSaleHandler>(services);
        var claimHandler = ActivatorUtilities.CreateInstance<CreateWarrantyClaimHandler>(services);
        var reviewHandler = ActivatorUtilities.CreateInstance<BeginWarrantyClaimReviewHandler>(services);
        var sendToSupplierHandler = ActivatorUtilities.CreateInstance<SendWarrantyClaimToSupplierHandler>(services);
        var supplierProcessingHandler = ActivatorUtilities.CreateInstance<MarkWarrantySupplierProcessingHandler>(services);
        var receiveReplacementHandler = ActivatorUtilities.CreateInstance<ReceiveCustomerWarrantyReplacementHandler>(services);

        var serialOriginal = "SN-WARR-ORIG-REP-" + Guid.NewGuid().ToString("N")[..8];
        var serialReplacement = "SN-WARR-REPL-REP-" + Guid.NewGuid().ToString("N")[..8];

        await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                fixture.SupplierId,
                "INV-REP-01",
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
                        4000m,
                        8000m,
                        [new SerializedIdentityInput(serialOriginal)])
                ],
                InitialPaymentAmount: 0m),
            CancellationToken.None);

        db.ChangeTracker.Clear();
        var unitOriginal = await db.InventoryUnits.SingleAsync(x => x.SerialNumber == serialOriginal.ToUpperInvariant());

        var saleResult = await saleHandler.HandleAsync(
            new CompleteSaleCommand(
                Guid.CreateVersion7(),
                null,
                fixture.ActorId,
                customer.Id,
                0m,
                SalePaymentMethod.Bank,
                8000m,
                "BANK",
                null,
                [new CompleteSaleLineInput(fixture.ProductId, fixture.ProductUnitId, 1m, 8000m, [unitOriginal.Id])]),
            CancellationToken.None);

        var saleId = saleResult.Value!.SaleId;
        db.ChangeTracker.Clear();
        var saleItem = await db.SaleItems.SingleAsync(x => x.SaleId == saleId);

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
                        "Faulty display",
                        saleItem.Id,
                        DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)),
                        [new WarrantyClaimUnitInput(unitOriginal.Id, serialOriginal)])
                ],
                Guid.CreateVersion7()),
            CancellationToken.None);

        var claimId = claimResult.Value;
        db.ChangeTracker.Clear();
        var claimItem = await db.WarrantyClaimItems.SingleAsync(x => x.ClaimId == claimId);
        var claimUnit = await db.WarrantyClaimItemUnits.SingleAsync(x => x.ClaimItemId == claimItem.Id);

        await reviewHandler.HandleAsync(new BeginWarrantyClaimReviewCommand(claimId, fixture.ActorId, Guid.CreateVersion7(), "Under review"), CancellationToken.None);
        await sendToSupplierHandler.HandleAsync(new SendWarrantyClaimToSupplierCommand(claimId, fixture.ActorId, Guid.CreateVersion7(), "To supplier"), CancellationToken.None);
        await supplierProcessingHandler.HandleAsync(new MarkWarrantySupplierProcessingCommand(claimId, fixture.ActorId, Guid.CreateVersion7(), "Supplier approved"), CancellationToken.None);

        var clientOpId = Guid.CreateVersion7();
        var replCommand = new ReceiveCustomerWarrantyReplacementCommand(
            claimId,
            fixture.ActorId,
            clientOpId,
            [new CustomerWarrantyReplacementUnitInput(claimUnit.Id, serialReplacement, null, null)],
            "Replacement received");

        // First call
        var result1 = await receiveReplacementHandler.HandleAsync(replCommand, CancellationToken.None);
        Assert.True(result1.IsSuccess, result1.Error?.Message);

        db.ChangeTracker.Clear();
        var spAfterFirst = await db.SupplierProducts.AsNoTracking().SingleAsync(x => x.SupplierId == fixture.SupplierId && x.ProductId == fixture.ProductId);
        var seqAfterFirst = spAfterFirst.NextItemSequence;

        // Second call (replay with same ClientOperationId)
        var result2 = await receiveReplacementHandler.HandleAsync(replCommand, CancellationToken.None);
        Assert.True(result2.IsSuccess, result2.Error?.Message);

        db.ChangeTracker.Clear();
        var spAfterSecond = await db.SupplierProducts.AsNoTracking().SingleAsync(x => x.SupplierId == fixture.SupplierId && x.ProductId == fixture.ProductId);

        // Sequence must NOT advance a second time
        Assert.Equal(seqAfterFirst, spAfterSecond.NextItemSequence);

        // Exactly one replacement unit row in database
        var replUnits = await db.InventoryUnits
            .AsNoTracking()
            .Where(x => x.SerialNumber == serialReplacement.ToUpperInvariant())
            .ToListAsync();
        Assert.Single(replUnits);
    }

    // =========================================================================
    // 7. HOSTILE F: WARRANTY REPLACEMENT CONCURRENCY (ALLOCATES EXACTLY ONE)
    // =========================================================================

    [Fact]
    public async Task Warranty_Replacement_Concurrency_Allocates_Single_Canonical_Replacement_OnRealPostgreSql()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var setupScope = provider.CreateAsyncScope();
        var setupDb = setupScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(setupDb);
        var fixture = await Phase2PostgresTestHarness.SeedSerializedProductAsync(setupDb, defaultSalePrice: 9000m);
        var customer = await Phase2PostgresTestHarness.SeedCustomerAsync(setupDb, "Concurrent Replacement Customer");

        var purchaseHandler = ActivatorUtilities.CreateInstance<CreatePurchaseHandler>(setupScope.ServiceProvider);
        var saleHandler = ActivatorUtilities.CreateInstance<CompleteSaleHandler>(setupScope.ServiceProvider);
        var claimHandler = ActivatorUtilities.CreateInstance<CreateWarrantyClaimHandler>(setupScope.ServiceProvider);
        var reviewHandler = ActivatorUtilities.CreateInstance<BeginWarrantyClaimReviewHandler>(setupScope.ServiceProvider);
        var sendToSupplierHandler = ActivatorUtilities.CreateInstance<SendWarrantyClaimToSupplierHandler>(setupScope.ServiceProvider);
        var supplierProcessingHandler = ActivatorUtilities.CreateInstance<MarkWarrantySupplierProcessingHandler>(setupScope.ServiceProvider);

        var serialOriginal = "SN-WARR-ORIG-CONC-" + Guid.NewGuid().ToString("N")[..8];
        var serialReplacement = "SN-WARR-REPL-CONC-" + Guid.NewGuid().ToString("N")[..8];

        await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                fixture.SupplierId,
                "INV-CONC-01",
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
                        9000m,
                        [new SerializedIdentityInput(serialOriginal)])
                ],
                InitialPaymentAmount: 0m),
            CancellationToken.None);

        setupDb.ChangeTracker.Clear();
        var unitOriginal = await setupDb.InventoryUnits.SingleAsync(x => x.SerialNumber == serialOriginal.ToUpperInvariant());

        var saleResult = await saleHandler.HandleAsync(
            new CompleteSaleCommand(
                Guid.CreateVersion7(),
                null,
                fixture.ActorId,
                customer.Id,
                0m,
                SalePaymentMethod.Bank,
                9000m,
                "BANK",
                null,
                [new CompleteSaleLineInput(fixture.ProductId, fixture.ProductUnitId, 1m, 9000m, [unitOriginal.Id])]),
            CancellationToken.None);

        var saleId = saleResult.Value!.SaleId;
        setupDb.ChangeTracker.Clear();
        var saleItem = await setupDb.SaleItems.SingleAsync(x => x.SaleId == saleId);

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
                        "Board defect",
                        saleItem.Id,
                        DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)),
                        [new WarrantyClaimUnitInput(unitOriginal.Id, serialOriginal)])
                ],
                Guid.CreateVersion7()),
            CancellationToken.None);

        var claimId = claimResult.Value;
        setupDb.ChangeTracker.Clear();
        var claimItem = await setupDb.WarrantyClaimItems.SingleAsync(x => x.ClaimId == claimId);
        var claimUnit = await setupDb.WarrantyClaimItemUnits.SingleAsync(x => x.ClaimItemId == claimItem.Id);

        await reviewHandler.HandleAsync(new BeginWarrantyClaimReviewCommand(claimId, fixture.ActorId, Guid.CreateVersion7(), "Under review"), CancellationToken.None);
        await sendToSupplierHandler.HandleAsync(new SendWarrantyClaimToSupplierCommand(claimId, fixture.ActorId, Guid.CreateVersion7(), "To supplier"), CancellationToken.None);
        await supplierProcessingHandler.HandleAsync(new MarkWarrantySupplierProcessingCommand(claimId, fixture.ActorId, Guid.CreateVersion7(), "Supplier approved"), CancellationToken.None);

        var clientOpId = Guid.CreateVersion7();
        var replCommand = new ReceiveCustomerWarrantyReplacementCommand(
            claimId,
            fixture.ActorId,
            clientOpId,
            [new CustomerWarrantyReplacementUnitInput(claimUnit.Id, serialReplacement, null, null)],
            "Concurrent replacement receive");

        var barrier = new Barrier(2);

        var task1 = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            var handler = ActivatorUtilities.CreateInstance<ReceiveCustomerWarrantyReplacementHandler>(scope.ServiceProvider);
            barrier.SignalAndWait();
            return await handler.HandleAsync(replCommand, CancellationToken.None);
        });

        var task2 = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            var handler = ActivatorUtilities.CreateInstance<ReceiveCustomerWarrantyReplacementHandler>(scope.ServiceProvider);
            barrier.SignalAndWait();
            return await handler.HandleAsync(replCommand, CancellationToken.None);
        });

        var results = await Task.WhenAll(task1, task2);

        // Both calls should succeed (one creates, other recovers original outcome)
        Assert.True(results[0].IsSuccess, results[0].Error?.Message);
        Assert.True(results[1].IsSuccess, results[1].Error?.Message);

        await using var verifyScope = provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        // Exactly 1 replacement unit row in database
        var replUnits = await verifyDb.InventoryUnits
            .AsNoTracking()
            .Where(x => x.SerialNumber == serialReplacement.ToUpperInvariant())
            .ToListAsync();
        Assert.Single(replUnits);
    }
}
