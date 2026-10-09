using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Reporting;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Features.Warranty;
using EdgeRetails.Domain.Audit;
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
public sealed class Phase7Pass3PostgresTests
{
    // =========================================================================
    // F05: VoidPurchase Real PostgreSQL Replay & Compensation
    // =========================================================================

    [Fact]
    public async Task F05_VoidPurchase_Postgres_DurableReplay_And_OutcomeIntegrity()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);

        var fixture = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db, defaultSalePrice: 200m);
        await using var ownedCashSession = await CreateOwnedCashSessionAsync(db, fixture.ActorId);
        var session = ownedCashSession.Session;

        // 1. Create completed purchase with cash settlement
        var purchaseHandler = services.GetRequiredService<CreatePurchaseHandler>();
        var purchaseOpId = Guid.CreateVersion7();
        var purchaseRes = await purchaseHandler.HandleAsync(new CreatePurchaseCommand(
            fixture.SupplierId,
            "PO-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            DateOnly.FromDateTime(DateTime.UtcNow),
            "Test cash purchase",
            0m,
            PurchaseSettlementMode.CashDrawer,
            fixture.ActorId,
            purchaseOpId,
            [new CreatePurchaseLineInput(fixture.ProductId, fixture.ProductUnitId, 5m, 100m, 200m, [])],
            InitialPaymentAmount: 500m,
            InitialPaymentMethod: SupplierSettlementMethod.CashDrawer,
            ReceiveStockImmediately: true), default);

        Assert.True(purchaseRes.IsSuccess, purchaseRes.Error?.Message);
        var purchaseId = purchaseRes.Value!.PurchaseId;

        // Verify purchase completed
        var purchase = await db.Purchases.AsNoTracking().SingleAsync(x => x.Id == purchaseId);
        Assert.Equal(PurchaseStatus.Completed, purchase.Status);

        var voidHandler = services.GetRequiredService<VoidPurchaseHandler>();
        var voidOpId = Guid.CreateVersion7();

        // 2. Void purchase call 1
        var voidRes1 = await voidHandler.HandleAsync(new VoidPurchaseCommand(
            purchaseId,
            voidOpId,
            fixture.ActorId,
            "Clerical error cancel"), default);

        Assert.True(voidRes1.IsSuccess, voidRes1.Error?.Message);
        Assert.False(voidRes1.Value!.WasExisting);

        // Verify DB state
        var dbPurchase = await db.Purchases.AsNoTracking().SingleAsync(x => x.Id == purchaseId);
        Assert.Equal(PurchaseStatus.Voided, dbPurchase.Status);

        var outcomes = await db.OperationOutcomes.AsNoTracking().Where(x => x.ClientOperationId == voidOpId).ToListAsync();
        Assert.Single(outcomes);
        Assert.Equal("VoidPurchase", outcomes[0].OperationType);
        Assert.False(string.IsNullOrWhiteSpace(outcomes[0].PayloadFingerprint));

        var entriesBeforeReplay = await db.SupplierAccountEntries.AsNoTracking().CountAsync();
        var cashMovementsBeforeReplay = await db.CashMovements.AsNoTracking().CountAsync();

        // 3. Replay with same ClientOperationId and identical payload
        var voidRes2 = await voidHandler.HandleAsync(new VoidPurchaseCommand(
            purchaseId,
            voidOpId,
            fixture.ActorId,
            "Clerical error cancel"), default);

        Assert.True(voidRes2.IsSuccess, voidRes2.Error?.Message);
        Assert.True(voidRes2.Value!.WasExisting);
        Assert.Equal(voidRes1.Value.PurchaseVoidId, voidRes2.Value.PurchaseVoidId);

        // Verify zero duplicate rows created
        Assert.Equal(entriesBeforeReplay, await db.SupplierAccountEntries.AsNoTracking().CountAsync());
        Assert.Equal(cashMovementsBeforeReplay, await db.CashMovements.AsNoTracking().CountAsync());

        // 4. Replay with same ClientOperationId and different payload -> Fails with payload mismatch
        var voidRes3 = await voidHandler.HandleAsync(new VoidPurchaseCommand(
            purchaseId,
            voidOpId,
            fixture.ActorId,
            "Different reason completely"), default);

        Assert.False(voidRes3.IsSuccess);
        Assert.Equal("idempotency.payload_mismatch", voidRes3.Error?.Code);
    }

    // =========================================================================
    // F06: CommercialExchange Real PostgreSQL Replay & Atomic Commit
    // =========================================================================

    [Fact]
    public async Task F06_CommercialExchange_Postgres_DurableReplay_And_AtomicCommit()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);

        var oldProduct = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db, defaultSalePrice: 300m);
        var newProduct = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db, defaultSalePrice: 500m);
        await using var ownedCashSession = await CreateOwnedCashSessionAsync(db, oldProduct.ActorId);
        var session = ownedCashSession.Session;

        // Stock products
        await SeedStockViaPurchaseAsync(services, oldProduct, 10m, 200m);
        await SeedStockViaPurchaseAsync(services, newProduct, 10m, 350m);

        // Initial sale of old product (qty = 1, price = 300)
        var saleHandler = services.GetRequiredService<CompleteSaleHandler>();
        var saleRes = await saleHandler.HandleAsync(new CompleteSaleCommand(
            Guid.CreateVersion7(),
            null,
            oldProduct.ActorId,
            session.Id,
            0m,
            SalePaymentMethod.Cash,
            300m,
            null,
            null,
            [new CompleteSaleLineInput(oldProduct.ProductId, oldProduct.ProductUnitId, 1m, 300m, [])]), default);

        Assert.True(saleRes.IsSuccess, saleRes.Error?.Message);
        var oldSaleId = saleRes.Value!.SaleId;
        var oldSaleItem = await db.SaleItems.AsNoTracking().SingleAsync(x => x.SaleId == oldSaleId);

        var exchangeHandler = services.GetRequiredService<CommercialExchangeHandler>();
        var exchangeOpId = Guid.CreateVersion7();

        var exchangeCmd = new CommercialExchangeCommand(
            exchangeOpId,
            oldSaleId,
            oldProduct.ActorId,
            session.Id,
            null,
            "CUSTOMER_EXCHANGE",
            "Upgrade to pro product",
            [new SaleReturnLineInput(oldSaleItem.Id, 1m, SaleReturnDisposition.RestockSellable, [])],
            [new CompleteSaleLineInput(newProduct.ProductId, newProduct.ProductUnitId, 1m, 500m, [])],
            0m,
            SalePaymentMethod.Cash,
            200m,
            null);

        // 1. First execution
        var exchangeRes1 = await exchangeHandler.HandleAsync(exchangeCmd, default);
        Assert.True(exchangeRes1.IsSuccess, exchangeRes1.Error?.Message);
        Assert.False(exchangeRes1.Value!.WasExisting);
        Assert.Equal(500m, exchangeRes1.Value.ReplacementGrandTotal);
        Assert.Equal(300m, exchangeRes1.Value.ReturnRefundAmount);
        Assert.Equal(200m, exchangeRes1.Value.NetDifference);

        // Verify outcome record in PostgreSQL
        var outcome = await db.OperationOutcomes.AsNoTracking().SingleAsync(x => x.ClientOperationId == exchangeOpId);
        Assert.Equal("CommercialExchange", outcome.OperationType);
        Assert.False(string.IsNullOrWhiteSpace(outcome.PayloadFingerprint));

        var salesCountBefore = await db.Sales.AsNoTracking().CountAsync();
        var returnsCountBefore = await db.SaleReturns.AsNoTracking().CountAsync();

        // 2. Replay with same ClientOperationId and identical payload
        var exchangeRes2 = await exchangeHandler.HandleAsync(exchangeCmd, default);
        Assert.True(exchangeRes2.IsSuccess, exchangeRes2.Error?.Message);
        Assert.True(exchangeRes2.Value!.WasExisting);
        Assert.Equal(exchangeRes1.Value.SaleId, exchangeRes2.Value.SaleId);
        Assert.Equal(exchangeRes1.Value.SaleReturnId, exchangeRes2.Value.SaleReturnId);

        // Verify zero extra rows
        Assert.Equal(salesCountBefore, await db.Sales.AsNoTracking().CountAsync());
        Assert.Equal(returnsCountBefore, await db.SaleReturns.AsNoTracking().CountAsync());

        // 3. Replay with different payload -> Fails with payload mismatch
        var exchangeCmdModified = new CommercialExchangeCommand(
            exchangeOpId,
            oldSaleId,
            oldProduct.ActorId,
            session.Id,
            null,
            "DIFFERENT_REASON",
            "Modified reason",
            [new SaleReturnLineInput(oldSaleItem.Id, 1m, SaleReturnDisposition.RestockSellable, [])],
            [new CompleteSaleLineInput(newProduct.ProductId, newProduct.ProductUnitId, 1m, 500m, [])],
            0m,
            SalePaymentMethod.Cash,
            200m,
            null);

        var exchangeRes3 = await exchangeHandler.HandleAsync(exchangeCmdModified, default);
        Assert.False(exchangeRes3.IsSuccess);
        Assert.Equal("idempotency.payload_mismatch", exchangeRes3.Error?.Code);
    }

    // =========================================================================
    // F10: CommercialExchange Real PostgreSQL Warranty Custody Guard
    // =========================================================================

    [Fact]
    public async Task F10_CommercialExchange_Postgres_Rejects_Unit_Under_Active_WarrantyClaim()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);

        var fixture = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db, defaultSalePrice: 1000m);
        var replacement = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db, defaultSalePrice: 1200m);
        await using var ownedCashSession = await CreateOwnedCashSessionAsync(db, fixture.ActorId);
        var session = ownedCashSession.Session;
        var customer = await Phase2PostgresTestHarness.SeedCustomerAsync(db);

        // Receive serialized unit
        var purchaseHandler = services.GetRequiredService<CreatePurchaseHandler>();
        var purchaseRes = await purchaseHandler.HandleAsync(new CreatePurchaseCommand(
            fixture.SupplierId,
            "PO-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            DateOnly.FromDateTime(DateTime.UtcNow),
            null,
            0m,
            PurchaseSettlementMode.External,
            fixture.ActorId,
            Guid.CreateVersion7(),
            [new CreatePurchaseLineInput(fixture.ProductId, fixture.ProductUnitId, 1m, 600m, 1000m, [new SerializedIdentityInput("SN-" + Guid.NewGuid().ToString("N")[..8])])],
            ReceiveStockImmediately: true), default);
        Assert.True(purchaseRes.IsSuccess, purchaseRes.Error?.Message);

        var unit = await db.InventoryUnits.AsNoTracking().SingleAsync(x => x.ProductId == fixture.ProductId);

        // Stock replacement product
        await SeedStockViaPurchaseAsync(services, replacement, 5m, 800m);

        // Sell the serialized unit
        var saleHandler = services.GetRequiredService<CompleteSaleHandler>();
        var saleRes = await saleHandler.HandleAsync(new CompleteSaleCommand(
            Guid.CreateVersion7(),
            customer.Id,
            fixture.ActorId,
            session.Id,
            0m,
            SalePaymentMethod.Cash,
            1000m,
            null,
            null,
            [new CompleteSaleLineInput(fixture.ProductId, fixture.ProductUnitId, 1m, 1000m, [unit.Id])]), default);
        Assert.True(saleRes.IsSuccess, saleRes.Error?.Message);
        var saleId = saleRes.Value!.SaleId;
        var saleItem = await db.SaleItems.AsNoTracking().SingleAsync(x => x.SaleId == saleId);

        // Create active warranty claim on this unit
        var claimHandler = services.GetRequiredService<CreateWarrantyClaimHandler>();
        var claimRes = await claimHandler.HandleAsync(new CreateWarrantyClaimCommand(
            customer.Id,
            saleId,
            null,
            fixture.ActorId,
            [new WarrantyClaimItemInput(fixture.ProductId, 1m, "Display flicker defect", saleItem.Id, [new WarrantyClaimUnitInput(unit.Id)])],
            Guid.CreateVersion7()), default);
        Assert.True(claimRes.IsSuccess, claimRes.Error?.Message);

        // Attempt commercial exchange returning this unit -> Must fail closed!
        var exchangeHandler = services.GetRequiredService<CommercialExchangeHandler>();
        var exchangeRes = await exchangeHandler.HandleAsync(new CommercialExchangeCommand(
            Guid.CreateVersion7(),
            saleId,
            fixture.ActorId,
            session.Id,
            customer.Id,
            "DEFECT",
            "Exchange warranty unit",
            [new SaleReturnLineInput(saleItem.Id, 1m, SaleReturnDisposition.RestockSellable, [unit.Id])],
            [new CompleteSaleLineInput(replacement.ProductId, replacement.ProductUnitId, 1m, 1200m, [])],
            0m,
            SalePaymentMethod.Cash,
            200m,
            null), default);

        Assert.False(exchangeRes.IsSuccess);
        Assert.Equal("sales.return_unit_active_warranty", exchangeRes.Error?.Code);
    }

    // =========================================================================
    // F07: POS Draft Real PostgreSQL Replay Integrity
    // =========================================================================

    [Fact]
    public async Task F07_CompletePosDraft_Postgres_Replay_RecoversCommittedSale()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);

        var fixture = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db, defaultSalePrice: 250m);
        await using var ownedCashSession = await CreateOwnedCashSessionAsync(db, fixture.ActorId);
        var session = ownedCashSession.Session;
        await SeedStockViaPurchaseAsync(services, fixture, 10m, 150m);

        // 1. Save Draft
        var saveDraftHandler = services.GetRequiredService<SavePosDraftHandler>();
        var saveRes = await saveDraftHandler.HandleAsync(new SavePosDraftCommand(
            null,
            null,
            null,
            fixture.ActorId,
            "TERM-01",
            "Customer browsing accessories",
            [new SavePosDraftItemInput(fixture.ProductId, fixture.ProductUnitId, 2m)]), default);
        Assert.True(saveRes.IsSuccess, saveRes.Error?.Message);
        var draftId = saveRes.Value!.DraftId;

        var completeDraftHandler = services.GetRequiredService<CompletePosDraftHandler>();
        var opId = Guid.CreateVersion7();

        var completeCmd = new CompletePosDraftCommand(
            draftId,
            saveRes.Value.Version,
            opId,
            fixture.ActorId,
            session.Id,
            0m,
            SalePaymentMethod.Cash,
            500m,
            null);

        // 2. Complete Draft Call 1 -> Converts draft and creates sale
        var completeRes1 = await completeDraftHandler.HandleAsync(completeCmd, default);
        Assert.True(completeRes1.IsSuccess, completeRes1.Error?.Message);
        Assert.False(completeRes1.Value!.WasExisting);

        // Confirm draft is Converted in PostgreSQL
        var draft = await db.PosDrafts.AsNoTracking().SingleAsync(x => x.Id == draftId);
        Assert.Equal(PosDraftStatus.Converted, draft.Status);

        // 3. Network timeout / Replay with same ClientOperationId
        var completeRes2 = await completeDraftHandler.HandleAsync(completeCmd, default);
        Assert.True(completeRes2.IsSuccess, completeRes2.Error?.Message);
        Assert.True(completeRes2.Value!.WasExisting);
        Assert.Equal(completeRes1.Value.SaleId, completeRes2.Value.SaleId);
        Assert.Equal(completeRes1.Value.InvoiceNumber, completeRes2.Value.InvoiceNumber);
    }

    // =========================================================================
    // F12: Net Profit Recognized Inventory Loss in PostgreSQL
    // =========================================================================

    [Fact]
    public async Task F12_ReportingReadService_Postgres_RecognizedLossDeducted_From_NetProfit()
    {
        // Reporting is shop-wide. Give this fixture its own day so other tests'
        // committed sales and losses cannot contaminate its exact 200/50 proof.
        var clock = new ReportingFixtureClock(new DateTimeOffset(2037, 4, 19, 12, 0, 0, TimeSpan.Zero));
        await using var provider = Phase2PostgresTestHarness.BuildProvider(clock);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);

        var fixture = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db, defaultSalePrice: 500m);
        await using var ownedCashSession = await CreateOwnedCashSessionAsync(db, fixture.ActorId);
        var session = ownedCashSession.Session;
        await SeedStockViaPurchaseAsync(services, fixture, 10m, 300m);

        // Make a sale
        var saleHandler = services.GetRequiredService<CompleteSaleHandler>();
        var saleRes = await saleHandler.HandleAsync(new CompleteSaleCommand(
            Guid.CreateVersion7(),
            null,
            fixture.ActorId,
            session.Id,
            0m,
            SalePaymentMethod.Cash,
            500m,
            null,
            null,
            [new CompleteSaleLineInput(fixture.ProductId, fixture.ProductUnitId, 1m, 500m, [])]), default);
        Assert.True(saleRes.IsSuccess, saleRes.Error?.Message);

        // Record recognized loss via movement (e.g. scrap of 150m)
        var lossMovement = new InventoryMovement
        {
            ProductId = fixture.ProductId,
            MovementType = InventoryMovementType.WriteOffToScrap,
            ReferenceType = "SCRAP",
            ReferenceId = Guid.CreateVersion7(),
            UnitCostSnapshot = 150m,
            RecognizedLossAmount = 150m,
            ActorId = fixture.ActorId,
            OccurredAt = clock.UtcNow,
            CorrelationId = Guid.CreateVersion7(),
            Reason = "DAMAGED_IN_STORE"
        };
        db.InventoryMovements.Add(lossMovement);
        await db.SaveChangesAsync();

        var reporting = services.GetRequiredService<IReportingReadService>();
        var today = clock.ShopDate;
        var snapshot = await reporting.GetSnapshotAsync(
            ReportingPeriodKind.Daily,
            today,
            today.Month,
            today.Year,
            default);

        // GrossProfit = NetSales (500) - COGS (300) = 200
        // NetProfit = GrossProfit (200) - Expenses (0) - RecognizedLoss (150) = 50
        Assert.Equal(200m, snapshot.GrossProfit);
        Assert.Equal(50m, snapshot.NetProfit);
    }

    // =========================================================================
    // F14: EF / ChangeTracker Append-Only Ledger Guard in PostgreSQL
    // =========================================================================

    [Fact]
    public async Task F14_EdgeRetailsDbContext_Postgres_AppendOnlyGuardEnforced()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();

        var supplier = await Phase2PostgresTestHarness.SeedSupplierAsync(db);
        var actorId = await IntegrationIdentitySeeder.CreateActorAsync(db);

        // 1. Insert is allowed
        var entry = new SupplierAccountEntry
        {
            EntryNumber = "SAE-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            SupplierId = supplier.Id,
            EntryType = SupplierAccountEntryType.Purchase,
            Direction = SupplierAccountDirection.IncreasePayable,
            Amount = 1000m,
            ReferenceType = "Purchase",
            ReferenceId = Guid.CreateVersion7(),
            OccurredAt = DateTimeOffset.UtcNow,
            ActorId = actorId,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.SupplierAccountEntries.Add(entry);
        await db.SaveChangesAsync();

        // 2. Modifying an existing entry via EF must throw InvalidOperationException
        entry.Amount = 9999m;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("append-only", ex.Message);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static readonly SemaphoreSlim SessionLock = new(1, 1);

    private sealed record ReportingFixtureClock(DateTimeOffset UtcNow) : IClock
    {
        public DateOnly ShopDate => DateOnly.FromDateTime(UtcNow.UtcDateTime);
    }

    private static async Task<OwnedCashSession> CreateOwnedCashSessionAsync(EdgeRetailsDbContext db, Guid actorId)
    {
        await SessionLock.WaitAsync();
        try
        {
            Assert.False(await db.CashSessions.AnyAsync(x => x.Status == CashSessionStatus.Open),
                "Cash-session fixture requires a clean singleton scope; an earlier fixture left its session open.");
            var session = await Phase2PostgresTestHarness.SeedOpenCashSessionAsync(db, actorId, openingCash: 10000m);
            return new OwnedCashSession(db, session);
        }
        finally
        {
            SessionLock.Release();
        }
    }

    private sealed class OwnedCashSession(EdgeRetailsDbContext db, CashSession session) : IAsyncDisposable
    {
        public CashSession Session { get; } = session;

        public async ValueTask DisposeAsync()
        {
            await db.CashSessions.Where(x => x.Id == Session.Id && x.Status == CashSessionStatus.Open)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(x => x.Status, CashSessionStatus.Closed)
                    .SetProperty(x => x.ClosedAt, DateTimeOffset.UtcNow));
        }
    }

    private static async Task SeedStockViaPurchaseAsync(
        IServiceProvider services,
        QuantityProductFixture fixture,
        decimal qty,
        decimal unitCost)
    {
        var purchaseHandler = services.GetRequiredService<CreatePurchaseHandler>();
        var res = await purchaseHandler.HandleAsync(new CreatePurchaseCommand(
            fixture.SupplierId,
            "PO-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            DateOnly.FromDateTime(DateTime.UtcNow),
            null,
            0m,
            PurchaseSettlementMode.External,
            fixture.ActorId,
            Guid.CreateVersion7(),
            [new CreatePurchaseLineInput(fixture.ProductId, fixture.ProductUnitId, qty, unitCost, unitCost * 1.5m, [])],
            ReceiveStockImmediately: true), default);

        Assert.True(res.IsSuccess, res.Error?.Message);
    }
}
