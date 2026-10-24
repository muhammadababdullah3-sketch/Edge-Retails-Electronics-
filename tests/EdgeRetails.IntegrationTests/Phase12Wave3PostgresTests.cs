using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Catalog;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Application.Features.Thaka;
using EdgeRetails.Application.Features.Warranty;
using EdgeRetails.Application.Production.Outbox;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Operations;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.SystemConfiguration;
using EdgeRetails.Domain.Thaka;
using EdgeRetails.Domain.Warranty;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EdgeRetails.IntegrationTests;

[Collection("Phase2PostgresIntegration")]
public sealed class Phase12Wave3PostgresTests
{
    static Phase12Wave3PostgresTests()
    {
        var connStr = Phase2PostgresTestHarness.ResolveConnectionString();
        Phase11DurablePostingIntegrationTests.AssertDisposablePostgresTargetGuard(connStr, performLiveProbe: true);
    }

    private static async Task<(QuantityProductFixture Product, Guid TerminalId)> SetupStockAndTerminalAsync(
        ServiceProvider provider,
        decimal initialStock = 20m)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var product = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db, defaultSalePrice: 200m);

        var purchase = await scope.ServiceProvider.GetRequiredService<CreatePurchaseHandler>().HandleAsync(
            new CreatePurchaseCommand(
                product.SupplierId,
                "PURCHASE-W3-" + Guid.NewGuid().ToString("N")[..8],
                DateOnly.FromDateTime(DateTime.UtcNow),
                null,
                0m,
                PurchaseSettlementMode.External,
                product.ActorId,
                Guid.NewGuid(),
                [new(product.ProductId, product.ProductUnitId, initialStock, 100m, 200m, [])]),
            default);

        Assert.True(purchase.IsSuccess, purchase.Error?.ToString());

        var terminal = new Terminal
        {
            Name = "POS-W3-" + Guid.NewGuid().ToString("N")[..8],
            TerminalCode = "W3-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant(),
            Status = TerminalStatus.Active,
            RegisteredAt = DateTimeOffset.UtcNow
        };
        db.Terminals.Add(terminal);
        await db.SaveChangesAsync();

        var existingSession = await db.CashSessions.FirstOrDefaultAsync(x => x.Status == CashSessionStatus.Open);
        if (existingSession is null)
        {
            var openHandler = scope.ServiceProvider.GetRequiredService<OpenCashSessionHandler>();
            var openRes = await openHandler.HandleAsync(new OpenCashSessionCommand(1000m, product.ActorId, "POS Opening Drawer"), default);
            Assert.True(openRes.IsSuccess, openRes.Error?.ToString());
        }

        return (product, terminal.Id);
    }

    // =========================================================================
    // Task P12-W3-003 / P12-W3-012: Purchasing Hostile Replay & Invariant Proof
    // =========================================================================

    [Fact]
    public async Task Postgres_Purchasing_SameOperationId_ReplaySucceeds_DifferentPayloadFails_UnitConserved()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var product = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db, defaultSalePrice: 150m);
        var handler = scope.ServiceProvider.GetRequiredService<CreatePurchaseHandler>();

        var clientOpId = Guid.NewGuid();
        var command = new CreatePurchaseCommand(
            product.SupplierId,
            "PURCHASE-P12W3-" + Guid.NewGuid().ToString("N")[..8],
            DateOnly.FromDateTime(DateTime.UtcNow),
            null,
            0m,
            PurchaseSettlementMode.External,
            product.ActorId,
            clientOpId,
            [new(product.ProductId, product.ProductUnitId, 5m, 80m, 150m, [])]);

        // First execution succeeds
        var res1 = await handler.HandleAsync(command, default);
        Assert.True(res1.IsSuccess, res1.Error?.ToString());
        Assert.False(res1.Value!.WasExisting);

        // Immediate replay with same ClientOperationId returns committed result (WasExisting = true)
        var res2 = await handler.HandleAsync(command, default);
        Assert.True(res2.IsSuccess, res2.Error?.ToString());
        Assert.True(res2.Value!.WasExisting);
        Assert.Equal(res1.Value.PurchaseId, res2.Value.PurchaseId);

        // Hostile replay with different payload is rejected with idempotency.payload_mismatch
        var changedCommand = command with
        {
            Lines = [new(product.ProductId, product.ProductUnitId, 10m, 80m, 150m, [])]
        };
        var res3 = await handler.HandleAsync(changedCommand, default);
        Assert.False(res3.IsSuccess);
        Assert.Equal("idempotency.payload_mismatch", res3.Error?.Code);

        // Stock conservation check: only initial 5 units were added
        var balance = await db.StockBalances.AsNoTracking().FirstOrDefaultAsync(x => x.ProductId == product.ProductId);
        Assert.NotNull(balance);
        Assert.Equal(5m, balance.SellableQty);
    }

    // =========================================================================
    // Task P12-W3-004 / P12-W3-012: Sales Return Replay & Exact Dual-Leg Conservation
    // =========================================================================

    [Fact]
    public async Task Postgres_SalesReturn_ConcurrentReplay_ExactlyOneApplies_StockAndRefundConserved()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var (product, terminalId) = await SetupStockAndTerminalAsync(provider, initialStock: 10m);

        // Complete sale of 3 units
        var saleOpId = Guid.NewGuid();
        CompleteSaleResult saleResult;
        Guid saleItemId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var saleHandler = scope.ServiceProvider.GetRequiredService<CompleteSaleHandler>();
            var saleCmd = new CompleteSaleCommand(
                ClientOperationId: saleOpId,
                CustomerId: null,
                CashierUserId: product.ActorId,
                SessionId: null,
                InvoiceDiscount: 0m,
                PaymentMethod: SalePaymentMethod.Cash,
                AmountTendered: 600m,
                PaymentReference: "Cash Sale W3",
                QuotationId: null,
                Lines: [new CompleteSaleLineInput(product.ProductId, product.ProductUnitId, 3m, 200m, [])],
                RegisteredTerminalId: terminalId);
            var res = await saleHandler.HandleAsync(saleCmd, default);
            Assert.True(res.IsSuccess, res.Error?.ToString());
            saleResult = res.Value!;

            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            saleItemId = await db.SaleItems.Where(x => x.SaleId == saleResult.SaleId).Select(x => x.Id).FirstAsync();
        }

        // Return 1 unit
        var returnOpId = Guid.NewGuid();
        var returnCmd = new CreateSaleReturnCommand(
            SaleId: saleResult.SaleId,
            ReasonCode: "RETURN",
            ReasonNote: "Customer returned 1 unit",
            RefundMethod: RefundMethod.Cash,
            CreatedBy: product.ActorId,
            ClientOperationId: returnOpId,
            Lines: [new SaleReturnLineInput(saleItemId, 1m, SaleReturnDisposition.RestockSellable, [])]);

        // Deterministic critical-boundary orchestration:
        // Task 1 acquires the PostgreSQL transaction advisory lock (SELECT pg_advisory_xact_lock) and pauses.
        // Task 2 enters CreateSaleReturnHandler and blocks on the same advisory lock.
        // We verify via pg_locks that Task 2 is actively waiting (locktype = 'advisory' AND NOT granted).
        // Then Task 1 is released to complete and commit, after which Task 2 unblocks and executes guarded replay.
        var lockHoldingHook = new HoldingOperationLock();

        var task1 = Task.Run(async () =>
        {
            await using var scope1 = provider.CreateAsyncScope();
            var innerLock = scope1.ServiceProvider.GetRequiredService<IOperationLock>();
            lockHoldingHook.SetInner(innerLock);
            var returnHandler1 = ActivatorUtilities.CreateInstance<CreateSaleReturnHandler>(scope1.ServiceProvider, lockHoldingHook);
            return await returnHandler1.HandleAsync(returnCmd, default);
        });

        // Await Task 1 acquiring the advisory lock inside its transaction
        await lockHoldingHook.LockAcquired.Task;

        // Start Task 2 - it will enter CreateSaleReturnHandler and block on the advisory lock held by Task 1
        var task2 = Task.Run(async () =>
        {
            await using var scope2 = provider.CreateAsyncScope();
            var returnHandler2 = scope2.ServiceProvider.GetRequiredService<CreateSaleReturnHandler>();
            return await returnHandler2.HandleAsync(returnCmd, default);
        });

        // Deterministically observe Task 2 waiting on the advisory lock in PostgreSQL pg_locks
        var waitingLockObserved = false;
        for (int i = 0; i < 50; i++)
        {
            await Task.Delay(50);
            await using var obsScope = provider.CreateAsyncScope();
            var obsCtx = obsScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var conn = obsCtx.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
            {
                await obsCtx.Database.OpenConnectionAsync();
            }
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted;";
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            if (count > 0)
            {
                waitingLockObserved = true;
                break;
            }
        }

        Assert.True(waitingLockObserved, "Task 2 must be deterministically observed in pg_locks waiting on the advisory lock held by Task 1.");

        // Release Task 1 to commit its return
        lockHoldingHook.ReleaseLock.TrySetResult();

        var (res1, res2) = (await task1, await task2);

        Assert.True(res1.IsSuccess, res1.Error?.ToString());
        Assert.True(res2.IsSuccess, res2.Error?.ToString());
        Assert.Equal(res1.Value!.SaleReturnId, res2.Value!.SaleReturnId);
        // Both race attempts converge on the exact same return entity with distinct winner and replayer branches
        var winnerReplayerObserved = (!res1.Value.WasExisting && res2.Value.WasExisting) ||
                                     (res1.Value.WasExisting && !res2.Value.WasExisting);
        Assert.True(
            winnerReplayerObserved,
            $"Expected one winner (WasExisting=false) and one replayer (WasExisting=true), got res1={res1.Value.WasExisting}, res2={res2.Value.WasExisting}");

        // Verify stock conservation and ledger entries in database: initial 10 - 3 sold + 1 returned = 8 sellable
        await using (var verifyScope = provider.CreateAsyncScope())
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var balance = await db.StockBalances.AsNoTracking().FirstOrDefaultAsync(x => x.ProductId == product.ProductId);
            Assert.NotNull(balance);
            Assert.Equal(8m, balance.SellableQty);

            // Exactly one return record exists in database with 200m refund amount
            var returns = await db.SaleReturns.Where(r => r.ClientOperationId == returnOpId).ToListAsync();
            Assert.Single(returns);
            Assert.Equal(res1.Value.SaleReturnId, returns[0].Id);
            Assert.Equal(200m, returns[0].RefundAmount);

            // Exactly one restock movement exists in database
            var movements = await db.InventoryMovements.Where(m => m.ReferenceId == res1.Value.SaleReturnId).ToListAsync();
            Assert.Single(movements);
            Assert.Equal(InventoryMovementType.SaleReturn, movements[0].MovementType);

            // Exactly one cash refund movement exists in database for 200m
            var cashMovements = await db.CashMovements.Where(m => m.SourceId == res1.Value.SaleReturnId).ToListAsync();
            Assert.Single(cashMovements);
            Assert.Equal(CashMovementType.SaleRefundCashOut, cashMovements[0].MovementType);
            Assert.Equal(CashMovementDirection.Out, cashMovements[0].Direction);
            Assert.Equal(200m, cashMovements[0].Amount);

            // Exactly one outcome ledger entry exists
            var outcomes = await db.OperationOutcomes.Where(o => o.ClientOperationId == returnOpId).ToListAsync();
            Assert.Single(outcomes);
            Assert.Equal(OperationOutcomeStatus.Succeeded, outcomes[0].Status);
        }
    }

    // =========================================================================
    // Task P12-W3-005 / P12-W3-012: Inventory Stocktake Replay After Post
    // =========================================================================

    [Fact]
    public async Task Postgres_Stocktake_ReplayAfterPost_ReturnsCommitted_DifferentPayloadRejected()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var (product, _) = await SetupStockAndTerminalAsync(provider, initialStock: 10m);

        // Cancel any lingering open stocktakes to ensure clean test baseline
        await using (var cancelScope = provider.CreateAsyncScope())
        {
            var db = cancelScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var openStocktakes = await db.Stocktakes
                .Where(s => s.Status != StocktakeStatus.Posted && s.Status != StocktakeStatus.Cancelled)
                .ToListAsync();
            foreach (var st in openStocktakes)
            {
                st.Status = StocktakeStatus.Cancelled;
            }
            if (openStocktakes.Count > 0)
            {
                await db.SaveChangesAsync();
            }
        }

        Guid stocktakeId;
        var createOpId = Guid.NewGuid();
        Guid categoryId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var cat = new Category
            {
                Name = "Stocktake Cat " + Guid.NewGuid().ToString("N")[..6],
                IdentitySymbol = ("C" + Guid.NewGuid().ToString("N")[..5]).ToUpperInvariant(),
                IsActive = true
            };
            db.Categories.Add(cat);
            var prod = await db.Products.FirstAsync(p => p.Id == product.ProductId);
            prod.CategoryId = cat.Id;
            await db.SaveChangesAsync();
            categoryId = cat.Id;

            var createHandler = scope.ServiceProvider.GetRequiredService<CreateStocktakeHandler>();
            var createCmd = new CreateStocktakeCommand(
                StocktakeScope.Category,
                categoryId,
                product.ActorId,
                "W3 Count",
                createOpId);

            var res = await createHandler.HandleAsync(createCmd, default);
            Assert.True(res.IsSuccess, res.Error?.ToString());
            stocktakeId = res.Value;

            var startHandler = scope.ServiceProvider.GetRequiredService<StartStocktakeHandler>();
            var startRes = await startHandler.HandleAsync(new StartStocktakeCommand(stocktakeId, product.ActorId, Guid.NewGuid()), default);
            Assert.True(startRes.IsSuccess, startRes.Error?.ToString());

            var countHandler = scope.ServiceProvider.GetRequiredService<RecordStocktakeCountHandler>();
            var countCmd = new RecordStocktakeCountCommand(
                stocktakeId,
                product.ProductId,
                10m,
                product.ActorId,
                "Counted 10",
                Guid.NewGuid());
            var countRes = await countHandler.HandleAsync(countCmd, default);
            Assert.True(countRes.IsSuccess, countRes.Error?.ToString());

            var reviewHandler = scope.ServiceProvider.GetRequiredService<ReviewStocktakeHandler>();
            var reviewRes = await reviewHandler.HandleAsync(new ReviewStocktakeCommand(stocktakeId, product.ActorId, Guid.NewGuid()), default);
            Assert.True(reviewRes.IsSuccess, reviewRes.Error?.ToString());
        }

        // Post stocktake
        var postOpId = Guid.NewGuid();
        var postCmd = new PostStocktakeCommand(stocktakeId, product.ActorId, null, postOpId);

        await using (var scope = provider.CreateAsyncScope())
        {
            var postHandler = scope.ServiceProvider.GetRequiredService<PostStocktakeHandler>();
            var post1 = await postHandler.HandleAsync(postCmd, default);
            Assert.True(post1.IsSuccess, post1.Error?.ToString());

            // P12-H01: Replay after status is Posted returns success and does NOT fail with stocktake_not_in_review
            var post2 = await postHandler.HandleAsync(postCmd, default);
            Assert.True(post2.IsSuccess, post2.Error?.ToString());

            // Payload conflict on different stocktake rejected
            var conflictingCmd = new PostStocktakeCommand(Guid.NewGuid(), product.ActorId, null, postOpId);
            var conflictRes = await postHandler.HandleAsync(conflictingCmd, default);
            Assert.False(conflictRes.IsSuccess);
            Assert.Equal("idempotency.payload_mismatch", conflictRes.Error?.Code);
        }
    }

    // =========================================================================
    // Task P12-W3-006 / P12-W3-012: Thaka Material Issue Replay & Stock Conservation
    // =========================================================================

    [Fact]
    public async Task Postgres_ThakaMaterialIssue_ReplaySucceeds_StockConserved()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var (product, _) = await SetupStockAndTerminalAsync(provider, initialStock: 15m);

        // Create named customer and Thaka project
        Guid projectId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var customer = new Customer
            {
                Name = "Thaka Customer W3",
                IsActive = true,
                IsWalkIn = false
            };
            db.Customers.Add(customer);
            await db.SaveChangesAsync();

            var projectHandler = scope.ServiceProvider.GetRequiredService<CreateThakaProjectHandler>();
            var projectCmd = new CreateThakaProjectCommand(
                customer.Id,
                "Project W3 Site Alpha",
                "Site 101",
                "Foundation issue",
                DateOnly.FromDateTime(DateTime.UtcNow),
                product.ActorId,
                Guid.NewGuid());
            var projectRes = await projectHandler.HandleAsync(projectCmd, default);
            Assert.True(projectRes.IsSuccess, projectRes.Error?.ToString());
            projectId = projectRes.Value;
        }

        // Issue material
        var issueOpId = Guid.NewGuid();
        var issueCmd = new IssueThakaMaterialCommand(
            issueOpId,
            projectId,
            product.ActorId,
            "Initial timber issue",
            [new(product.ProductId, product.ProductUnitId, 4m, 200m, [])]);

        await using (var scope = provider.CreateAsyncScope())
        {
            var issueHandler = scope.ServiceProvider.GetRequiredService<IssueThakaMaterialHandler>();
            var res1 = await issueHandler.HandleAsync(issueCmd, default);
            Assert.True(res1.IsSuccess, res1.Error?.ToString());
            Assert.False(res1.Value!.WasExisting);

            // Replay with same ClientOperationId
            var res2 = await issueHandler.HandleAsync(issueCmd, default);
            Assert.True(res2.IsSuccess, res2.Error?.ToString());
            Assert.True(res2.Value!.WasExisting);
            Assert.Equal(res1.Value.MaterialIssueId, res2.Value.MaterialIssueId);
        }

        // Stock conservation check: 15 initial - 4 issued = 11 sellable
        await using (var verifyScope = provider.CreateAsyncScope())
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var balance = await db.StockBalances.AsNoTracking().FirstOrDefaultAsync(x => x.ProductId == product.ProductId);
            Assert.NotNull(balance);
            Assert.Equal(11m, balance.SellableQty);
        }
    }

    // =========================================================================
    // Task P12-W3-007 / P12-W3-012: Supplier Payment Replay & Context Invariant Proof
    // =========================================================================

    [Fact]
    public async Task Postgres_SupplierPayment_SameOperationId_ReplaySucceeds_DifferentPayloadMismatched()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var product = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db, defaultSalePrice: 100m);
        var paymentHandler = scope.ServiceProvider.GetRequiredService<CreateSupplierPaymentHandler>();

        var opId = Guid.NewGuid();
        var cmd = new CreateSupplierPaymentCommand(
            product.SupplierId,
            500m,
            SupplierPaymentPurpose.Advance,
            SupplierSettlementMethod.External,
            product.ActorId,
            opId,
            "CHK-W3-001",
            "Payment for intake");

        // First payment execution
        var res1 = await paymentHandler.HandleAsync(cmd, default);
        Assert.True(res1.IsSuccess, res1.Error?.ToString());
        Assert.False(res1.Value!.WasExisting);

        // Replay with identical intent returns committed result
        var res2 = await paymentHandler.HandleAsync(cmd, default);
        Assert.True(res2.IsSuccess, res2.Error?.ToString());
        Assert.True(res2.Value!.WasExisting);
        Assert.Equal(res1.Value.PaymentId, res2.Value.PaymentId);

        // Conflict on different amount
        var conflictingCmd = cmd with { Amount = 750m };
        var res3 = await paymentHandler.HandleAsync(conflictingCmd, default);
        Assert.False(res3.IsSuccess);
        Assert.Equal("idempotency.payload_mismatch", res3.Error?.Code);
    }

    // =========================================================================
    // Task P12-W3-009 / P12-W3-012: Catalog Product Save Replay & Pre-Lock Protection
    // =========================================================================

    [Fact]
    public async Task Postgres_CatalogProductSave_PreLockReplay_RecoversVersionedProduct()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var product = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db, defaultSalePrice: 150m);
        var handler = scope.ServiceProvider.GetRequiredService<SaveProductAggregateHandler>();

        var category = await db.Categories.AsNoTracking().FirstAsync();

        var opId = Guid.NewGuid();
        var actorId = product.ActorId;

        var command = new SaveProductAggregateCommand(
            ActorId: actorId,
            ProductId: null,
            ExpectedVersion: null,
            Product: new ProductCatalogInput(
                Name: "Catalog Product W3-" + Guid.NewGuid().ToString("N")[..6],
                Sku: "SKU-W3-" + Guid.NewGuid().ToString("N")[..6],
                Brand: "BrandAlpha",
                Model: "ModelBeta",
                CategoryId: category.Id,
                BaseUnitId: product.UnitId,
                DefaultSalePrice: 250m),
            Units: [new ProductUnitInput(product.UnitId, 1m, true, true, false, true, true)],
            LinkedSupplierIds: [],
            ClientOperationId: opId);

        // First execution creates product
        var res1 = await handler.HandleAsync(command, default);
        Assert.True(res1.IsSuccess, res1.Error?.ToString());

        // Replay returns committed result
        var res2 = await handler.HandleAsync(command, default);
        Assert.True(res2.IsSuccess, res2.Error?.ToString());
        Assert.Equal(res1.Value!.ProductId, res2.Value!.ProductId);

        // Changed price parameter returns idempotency.payload_mismatch
        var changedCmd = command with
        {
            Product = command.Product with { DefaultSalePrice = 300m }
        };
        var res3 = await handler.HandleAsync(changedCmd, default);
        Assert.False(res3.IsSuccess);
        Assert.Equal("idempotency.payload_mismatch", res3.Error?.Code);
    }

    // =========================================================================
    // Task P12-W3-008 / P12-W3-012: Warranty Claim Creation Replay
    // =========================================================================

    [Fact]
    public async Task Postgres_WarrantyClaim_SameOperationId_ReplayRecoversClaim()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var (product, terminalId) = await SetupStockAndTerminalAsync(provider, initialStock: 5m);

        Guid customerId;
        Guid saleId;
        Guid saleItemId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var customer = new Customer
            {
                Name = "Warranty Customer W3",
                IsActive = true,
                IsWalkIn = false
            };
            db.Customers.Add(customer);

            var prod = await db.Products.FirstAsync(p => p.Id == product.ProductId);
            prod.DefaultWarrantyMonths = 12;
            await db.SaveChangesAsync();
            customerId = customer.Id;

            var saleHandler = scope.ServiceProvider.GetRequiredService<CompleteSaleHandler>();
            var saleRes = await saleHandler.HandleAsync(new CompleteSaleCommand(
                ClientOperationId: Guid.NewGuid(),
                CustomerId: customerId,
                CashierUserId: product.ActorId,
                SessionId: null,
                InvoiceDiscount: 0m,
                PaymentMethod: SalePaymentMethod.Cash,
                AmountTendered: 200m,
                PaymentReference: "Warranty Base Sale",
                QuotationId: null,
                Lines: [new CompleteSaleLineInput(product.ProductId, product.ProductUnitId, 1m, 200m, [])],
                RegisteredTerminalId: terminalId), default);
            Assert.True(saleRes.IsSuccess, saleRes.Error?.ToString());
            saleId = saleRes.Value!.SaleId;
            saleItemId = await db.SaleItems.Where(x => x.SaleId == saleId).Select(x => x.Id).FirstAsync();
        }

        var opId = Guid.NewGuid();
        var command = new CreateWarrantyClaimCommand(
            customerId,
            saleId,
            product.ActorId,
            opId,
            [new WarrantyClaimItemInput(product.ProductId, 1m, "Cracked display", saleItemId, null)]);

        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateWarrantyClaimHandler>();
            var res1 = await handler.HandleAsync(command, default);
            Assert.True(res1.IsSuccess, res1.Error?.ToString());

            // Replay with identical intent returns committed result
            var res2 = await handler.HandleAsync(command, default);
            Assert.True(res2.IsSuccess, res2.Error?.ToString());
            Assert.Equal(res1.Value, res2.Value);

            // Replay with different payload returns payload_mismatch
            var changedCmd = new CreateWarrantyClaimCommand(
                customerId,
                saleId,
                product.ActorId,
                opId,
                [new WarrantyClaimItemInput(product.ProductId, 1m, "Cracked display and battery", saleItemId, null)]);
            var res3 = await handler.HandleAsync(changedCmd, default);
            Assert.False(res3.IsSuccess);
            Assert.Equal("idempotency.payload_mismatch", res3.Error?.Code);
        }
    }

    // =========================================================================
    // Task P12-W3-011 / P12-W3-014: Transactional Outbox Physical Boundary
    // =========================================================================

    [Fact]
    public async Task Postgres_TransactionalOutbox_PhysicalEffectDecoupling_CommitWithoutPhysicalFailure()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var (product, terminalId) = await SetupStockAndTerminalAsync(provider, initialStock: 10m);

        // 1. Production caller proof: CompleteSaleHandler with IOutboxWriter enqueues outbox message atomically
        var saleOpId = Guid.NewGuid();
        CompleteSaleResult saleResult;
        await using (var scope = provider.CreateAsyncScope())
        {
            var saleHandler = scope.ServiceProvider.GetRequiredService<CompleteSaleHandler>();
            var saleCmd = new CompleteSaleCommand(
                ClientOperationId: saleOpId,
                CustomerId: null,
                CashierUserId: product.ActorId,
                SessionId: null,
                InvoiceDiscount: 0m,
                PaymentMethod: SalePaymentMethod.Cash,
                AmountTendered: 200m,
                PaymentReference: "Outbox Physical Test",
                QuotationId: null,
                Lines: [new CompleteSaleLineInput(product.ProductId, product.ProductUnitId, 1m, 200m, [])],
                RegisteredTerminalId: terminalId);

            var res = await saleHandler.HandleAsync(saleCmd, default);
            Assert.True(res.IsSuccess, res.Error?.ToString());
            saleResult = res.Value!;
        }

        // Verify database state: Sale committed, stock decremented, and outbox message enqueued atomically
        Guid outboxMessageId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var committedSale = await db.Sales.FirstOrDefaultAsync(s => s.Id == saleResult.SaleId);
            Assert.NotNull(committedSale);

            var stock = await db.StockBalances.FirstOrDefaultAsync(b => b.ProductId == product.ProductId);
            Assert.NotNull(stock);
            Assert.Equal(9m, stock.SellableQty); // 10 - 1 sold

            var outboxMsg = await db.OutboxMessages.FirstOrDefaultAsync(m => m.SourceId == saleResult.SaleId.ToString("D"));
            Assert.NotNull(outboxMsg);
            Assert.Equal(OutboxMessageStatus.Pending, outboxMsg.Status);
            Assert.Equal(PrintOutboxEffectHandler.TypeName, outboxMsg.EffectType);
            Assert.Equal(0, outboxMsg.AttemptCount);
            Assert.Equal("print-sale-" + saleOpId.ToString("N"), outboxMsg.IdempotencyKey);
            outboxMessageId = outboxMsg.Id;
            outboxMsg.CreatedAt = DateTimeOffset.UtcNow.AddYears(-1);
            await db.SaveChangesAsync();
        }

        // 2. Dispatcher transient error: Simulated printer disconnect/paper jam throws exception
        // Processor marks message as Failed, increments attempt count, schedules retry
        // Crucially: Financial and stock records in database remain fully committed and unrolled back!
        await using (var scope = provider.CreateAsyncScope())
        {
            var outboxRepo = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();
            var mockHandler = new ThrowingPrintHandler(new InvalidOperationException("PRINTER_PAPER_OUT: Device thermal head jammed"));
            var processor = new OutboxProcessor(outboxRepo, [mockHandler], "test-worker-1");

            var processed = await processor.ProcessPendingAsync(200, default);
            Assert.Equal(0, processed);
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var updatedMsg = await db.OutboxMessages.FirstAsync(m => m.Id == outboxMessageId);
            // In accordance with Outbox retry policy, transient failure increments AttemptCount and keeps Pending with NextAttemptAt
            Assert.Equal(OutboxMessageStatus.Pending, updatedMsg.Status);
            Assert.Equal(1, updatedMsg.AttemptCount);
            Assert.NotNull(updatedMsg.NextAttemptAt);
            Assert.NotNull(updatedMsg.LastError);
            Assert.Contains("PRINTER_PAPER_OUT", updatedMsg.LastError);

            // Assert strict decoupling: Sale still committed, stock still decremented!
            var saleStillCommitted = await db.Sales.AnyAsync(s => s.Id == saleResult.SaleId);
            Assert.True(saleStillCommitted);
            var stockStillDecremented = await db.StockBalances.FirstAsync(b => b.ProductId == product.ProductId);
            Assert.Equal(9m, stockStillDecremented.SellableQty);
        }

        // 3. Dispatcher NonRetryableOutboxEffectException: Terminal unknown outcome escalates to ActionRequired
        await using (var scope = provider.CreateAsyncScope())
        {
            var outboxRepo = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();
            var nonRetryableHandler = new ThrowingPrintHandler(
                new NonRetryableOutboxEffectException("Print outcome unknown: [print.outcome_unknown] Ambiguous spooler response"));
            var processor = new OutboxProcessor(outboxRepo, [nonRetryableHandler], "test-worker-2");

            // Reset status to Pending and NextAttemptAt to now so processor can claim it
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var msgToReset = await db.OutboxMessages.FirstAsync(m => m.Id == outboxMessageId);
            msgToReset.Status = OutboxMessageStatus.Pending;
            msgToReset.NextAttemptAt = DateTimeOffset.UtcNow;
            msgToReset.CreatedAt = DateTimeOffset.UtcNow.AddYears(-1);
            msgToReset.LeaseOwner = null;
            msgToReset.LeaseToken = null;
            await db.SaveChangesAsync();

            var processed = await processor.ProcessPendingAsync(200, default);
            Assert.Equal(0, processed);

            var escalatedMsg = await db.OutboxMessages.FirstAsync(m => m.Id == outboxMessageId);
            Assert.Equal(OutboxMessageStatus.ActionRequired, escalatedMsg.Status);
            Assert.Contains("Non-retryable outbox effect error", escalatedMsg.LastError);

            // Strict decoupling holds
            var saleStillCommitted = await db.Sales.AnyAsync(s => s.Id == saleResult.SaleId);
            Assert.True(saleStillCommitted);
        }

        // 4. Successful print dispatch: Processor completes message
        await using (var scope = provider.CreateAsyncScope())
        {
            var outboxRepo = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();
            var successHandler = new SuccessfulPrintHandler();
            var processor = new OutboxProcessor(outboxRepo, [successHandler], "test-worker-3");

            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var msgToReset = await db.OutboxMessages.FirstAsync(m => m.Id == outboxMessageId);
            msgToReset.Status = OutboxMessageStatus.Pending;
            msgToReset.NextAttemptAt = DateTimeOffset.UtcNow;
            msgToReset.CreatedAt = DateTimeOffset.UtcNow.AddYears(-1);
            msgToReset.LeaseOwner = null;
            msgToReset.LeaseToken = null;
            await db.SaveChangesAsync();

            var processed = await processor.ProcessPendingAsync(200, default);
            Assert.True(processed >= 1);

            var completedMsg = await db.OutboxMessages.FirstAsync(m => m.Id == outboxMessageId);
            Assert.Equal(OutboxMessageStatus.Completed, completedMsg.Status);
            Assert.NotNull(completedMsg.CompletedAt);
        }

        // 4. Failed business-transaction atomic enqueue rollback negative (MON-P12W3-R7-004):
        // When a business transaction rolls back, any outbox messages enqueued inside it are rolled back atomically!
        var failedOpId = Guid.NewGuid();
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var txRunner = scope.ServiceProvider.GetRequiredService<ITransactionRunner>();
            var outboxRepo = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await txRunner.ExecuteAsync<bool>(async ct =>
                {
                    outboxRepo.Enqueue(new OutboxMessage
                    {
                        Id = Guid.NewGuid(),
                        EffectType = "PrintDocument",
                        SourceType = "Sale",
                        SourceId = Guid.NewGuid().ToString("D"),
                        PayloadJson = "{}",
                        IdempotencyKey = "print-sale-" + failedOpId.ToString("N"),
                        CreatedAt = DateTimeOffset.UtcNow,
                        AttemptCount = 0,
                        Status = OutboxMessageStatus.Pending
                    });
                    await db.SaveChangesAsync(ct);
                    throw new InvalidOperationException("BUSINESS_TRANSACTION_FAILED_SIMULATION");
                }, default);
            });
            Assert.Equal("BUSINESS_TRANSACTION_FAILED_SIMULATION", ex.Message);
        }

        // Verify outbox message was rolled back atomically: zero orphan messages with this idempotency key
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var orphanMsg = await db.OutboxMessages.FirstOrDefaultAsync(m => m.IdempotencyKey == "print-sale-" + failedOpId.ToString("N"));
            Assert.Null(orphanMsg);
        }
    }

    // =========================================================================
    // Task P12-W3-001 / P12-W3-009: Manual Cash Movement Idempotency Proof
    // =========================================================================

    [Fact]
    public async Task Postgres_ManualCashMovement_SameOperationId_ReplaySucceeds_DifferentPayloadMismatched()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var (product, _) = await SetupStockAndTerminalAsync(provider, initialStock: 5m);

        // Ensure open cash session
        Guid sessionId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var cashRepo = scope.ServiceProvider.GetRequiredService<ICashRepository>();
            var existingSession = await cashRepo.GetOpenSessionForUpdateAsync(default);
            if (existingSession is not null)
            {
                sessionId = existingSession.Id;
            }
            else
            {
                var openHandler = scope.ServiceProvider.GetRequiredService<OpenCashSessionHandler>();
                var openRes = await openHandler.HandleAsync(new OpenCashSessionCommand(500m, product.ActorId, "Opening Drawer"), default);
                Assert.True(openRes.IsSuccess, openRes.Error?.ToString());
                sessionId = openRes.Value;
            }
        }

        var opId = Guid.NewGuid();
        var cmd = new RecordManualCashMovementCommand(
            Direction: CashMovementDirection.In,
            Amount: 150m,
            ActorId: product.ActorId,
            Reason: "Owner drawer injection",
            Note: "Petty cash top-up",
            ClientOperationId: opId);

        Guid movementId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<RecordManualCashMovementHandler>();
            var res1 = await handler.HandleAsync(cmd, default);
            Assert.True(res1.IsSuccess, res1.Error?.ToString());
            movementId = res1.Value;

            // Replay with identical intent returns committed movement ID
            var res2 = await handler.HandleAsync(cmd, default);
            Assert.True(res2.IsSuccess, res2.Error?.ToString());
            Assert.Equal(movementId, res2.Value);

            // Replay with different payload returns idempotency.payload_mismatch
            var conflictingCmd = cmd with { Amount = 300m };
            var res3 = await handler.HandleAsync(conflictingCmd, default);
            Assert.False(res3.IsSuccess);
            Assert.Equal("idempotency.payload_mismatch", res3.Error?.Code);
        }

        // Verify database: exactly 1 cash movement recorded for this opId
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var movements = await db.CashMovements.Where(m => m.Id == movementId).ToListAsync();
            Assert.Single(movements);
            Assert.Equal(150m, movements[0].Amount);
        }
    }

    // =========================================================================
    // Task P12-W3-013 / P12-W3-014: Process Crash & Recovery Invariants
    // =========================================================================

    [Fact]
    public async Task Postgres_CrashRecovery_ProcessDiesBeforeCommit_RollbackFailClosed_ClientRetriesSuccessfully()
    {
        var dbConn = Phase2PostgresTestHarness.ResolveConnectionString();
        var hostDll = FindCrashHostDll();

        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var (product, terminalId) = await SetupStockAndTerminalAsync(provider, initialStock: 10m);

        var opId = Guid.NewGuid();

        // Launch owned test child process executing production CompleteSaleHandler with CrashBeforeCommitInterceptor
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{hostDll}\" --scenario crash-before-commit --db \"{dbConn}\" --op-id \"{opId}\" --user-id \"{product.ActorId}\" --product-id \"{product.ProductId}\" --unit-id \"{product.ProductUnitId}\" --terminal-id \"{terminalId}\" --qty 1 --amount 200",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stderrTask = process.StandardError.ReadToEndAsync();
        var ready = false;
        try
        {
            while (!process.HasExited)
            {
                var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60));
                if (line is null)
                {
                    break;
                }
                if (line.Contains("READY:CRASH_BEFORE_COMMIT", StringComparison.Ordinal))
                {
                    ready = true;
                    break;
                }
            }
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            var error = await stderrTask;
            throw new Xunit.Sdk.XunitException($"Crash host timed out. Stderr:\n{error}");
        }

        Assert.True(ready, "Child process reached uncommitted state before termination.");

        // Wait for process to fully exit after CrashBeforeCommitInterceptor terminates it
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }
        await process.WaitForExitAsync();

        // 1. MON-P12W3-R7-002: Comprehensive PostgreSQL fail-closed rollback verification
        // (zero sales, zero payments, zero movements, stock conserved at initial 10m, zero outbox, zero outcome ledger)
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var saleExists = await db.Sales.AnyAsync(s => s.ClientOperationId == opId);
            Assert.False(saleExists, "Sale must not exist after uncommitted process crash.");

            var paymentExists = await db.SalePayments.AnyAsync(p => p.Reference == $"Crash Host Sale {opId:N}");
            Assert.False(paymentExists, "Payment must not exist after uncommitted process crash.");

            var itemExists = await db.SaleItems.AnyAsync(i => db.Sales.Any(s => s.Id == i.SaleId && s.ClientOperationId == opId));
            Assert.False(itemExists, "SaleItem must not exist after uncommitted process crash.");

            var movementExists = await db.InventoryMovements.AnyAsync(m => db.Sales.Any(s => s.Id == m.ReferenceId && s.ClientOperationId == opId));
            Assert.False(movementExists, "InventoryMovement must not exist after uncommitted process crash.");

            var stock = await db.StockBalances.FirstAsync(b => b.ProductId == product.ProductId);
            Assert.Equal(10m, stock.SellableQty); // Initial stock completely untouched!

            var outboxExists = await db.OutboxMessages.AnyAsync(m => m.IdempotencyKey == $"print-sale-{opId:N}");
            Assert.False(outboxExists, "OutboxMessage must not exist after uncommitted process crash.");

            var outcomeExists = await db.OperationOutcomes.AnyAsync(o => o.ClientOperationId == opId);
            Assert.False(outcomeExists, "OperationOutcome must not exist after uncommitted process crash.");
        }

        // 2. Client retries with the EXACT SAME immutable intent, context, cashier, lines, amount, and ClientOperationId
        await using (var scope = provider.CreateAsyncScope())
        {
            var saleHandler = scope.ServiceProvider.GetRequiredService<CompleteSaleHandler>();
            var saleCmd = new CompleteSaleCommand(
                ClientOperationId: opId,
                CustomerId: null,
                CashierUserId: product.ActorId,
                SessionId: null,
                InvoiceDiscount: 0m,
                PaymentMethod: SalePaymentMethod.Cash,
                AmountTendered: 200m,
                PaymentReference: $"Crash Host Sale {opId:N}",
                QuotationId: null,
                Lines: [new CompleteSaleLineInput(product.ProductId, product.ProductUnitId, 1m, 200m, [])],
                RegisteredTerminalId: terminalId);

            var retryRes = await saleHandler.HandleAsync(saleCmd, default);
            Assert.True(retryRes.IsSuccess, retryRes.Error?.ToString());
            Assert.False(retryRes.Value!.WasExisting);

            // Verify comprehensive conservation in database after retry
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var committedSale = await db.Sales.FirstOrDefaultAsync(s => s.ClientOperationId == opId);
            Assert.NotNull(committedSale);
            Assert.Equal(opId, committedSale.ClientOperationId);
            Assert.Equal(200m, committedSale.GrandTotal);

            var committedPayment = await db.SalePayments.FirstOrDefaultAsync(p => p.SaleId == committedSale.Id);
            Assert.NotNull(committedPayment);
            Assert.Equal(200m, committedPayment.AppliedAmount);

            var committedMovement = await db.InventoryMovements.FirstOrDefaultAsync(m => m.ReferenceId == committedSale.Id);
            Assert.NotNull(committedMovement);
            Assert.Equal(InventoryMovementType.SaleOut, committedMovement.MovementType);

            var updatedStock = await db.StockBalances.FirstAsync(b => b.ProductId == product.ProductId);
            Assert.Equal(9m, updatedStock.SellableQty); // 10 initial - 1 sold = 9 sellable

            var outboxMsg = await db.OutboxMessages.FirstOrDefaultAsync(m => m.SourceId == committedSale.Id.ToString("D"));
            Assert.NotNull(outboxMsg);
            Assert.Equal($"print-sale-{opId:N}", outboxMsg.IdempotencyKey);

            var outcomeLedger = await db.OperationOutcomes.FirstOrDefaultAsync(o => o.ClientOperationId == opId);
            Assert.NotNull(outcomeLedger);
            Assert.Equal(OperationOutcomeStatus.Succeeded, outcomeLedger.Status);
            Assert.Equal(committedSale.Id, outcomeLedger.ResultEntityId);
        }
    }

    [Fact]
    public async Task Postgres_CrashRecovery_ProcessDiesAfterCommit_BeforeResponse_ClientReplayRecoversCommittedSale()
    {
        var dbConn = Phase2PostgresTestHarness.ResolveConnectionString();
        var hostDll = FindCrashHostDll();

        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var (product, terminalId) = await SetupStockAndTerminalAsync(provider, initialStock: 10m);

        var opId = Guid.NewGuid();

        // Launch owned test child process configured to commit then crash before returning HTTP response
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{hostDll}\" --scenario crash-after-commit --db \"{dbConn}\" --op-id \"{opId}\" --user-id \"{product.ActorId}\" --product-id \"{product.ProductId}\" --terminal-id \"{terminalId}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            var timedOutOutput = await stdoutTask;
            var timedOutError = await stderrTask;
            throw new Xunit.Sdk.XunitException($"Crash host timed out. Output:\n{timedOutOutput}\nStderr:\n{timedOutError}");
        }

        var output = await stdoutTask;
        Assert.True(
            output.Contains("READY:COMMITTED_BEFORE_ACK", StringComparison.Ordinal),
            $"Child process must commit before crashing. Output:\n{output}");

        // Reconnect and verify the sale committed in PostgreSQL survived process crash
        Guid committedSaleId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var existingSale = await db.Sales.FirstOrDefaultAsync(s => s.ClientOperationId == opId);
            Assert.NotNull(existingSale);
            committedSaleId = existingSale.Id;
        }

        // Client reconnects/retries with the exact same ClientOperationId
        await using (var scope = provider.CreateAsyncScope())
        {
            var salesRepo = scope.ServiceProvider.GetRequiredService<ISalesRepository>();
            var recoveredSale = await salesRepo.GetSaleByClientOperationIdAsync(opId, default);
            Assert.NotNull(recoveredSale);
            Assert.Equal(committedSaleId, recoveredSale.Id);
            Assert.Equal(opId, recoveredSale.ClientOperationId);

            // Replay through CompleteSaleHandler recovers committed sale with WasExisting = true
            var saleHandler = scope.ServiceProvider.GetRequiredService<CompleteSaleHandler>();
            var replayCmd = new CompleteSaleCommand(
                ClientOperationId: opId,
                CustomerId: null,
                CashierUserId: product.ActorId,
                SessionId: null,
                InvoiceDiscount: 0m,
                PaymentMethod: SalePaymentMethod.Cash,
                AmountTendered: 200m,
                PaymentReference: "Crash Host Sale",
                QuotationId: null,
                Lines: [new CompleteSaleLineInput(product.ProductId, product.ProductUnitId, 1m, 200m, [])],
                RegisteredTerminalId: terminalId);

            var replayRes = await saleHandler.HandleAsync(replayCmd, default);
            Assert.True(replayRes.IsSuccess, replayRes.Error?.ToString());
            Assert.True(replayRes.Value!.WasExisting);
            Assert.Equal(committedSaleId, replayRes.Value.SaleId);
        }
    }

    [Fact]
    public async Task Postgres_DeadlockRollback_SameIdRetry_TransactionSucceedsWithConservedState()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var (p1, _) = await SetupStockAndTerminalAsync(provider, initialStock: 10m);
        var (p2, _) = await SetupStockAndTerminalAsync(provider, initialStock: 10m);

        var id1 = p1.ProductId;
        var id2 = p2.ProductId;

        var t1AcquiredFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var t2AcquiredFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var deadlockDetected = false;
        var retryOpSucceeded = false;

        // Transaction 1 locks p1, signals t1AcquiredFirst, waits for t2 to lock p2, then attempts to lock p2
        var task1 = Task.Run(async () =>
        {
            await using var s = provider.CreateAsyncScope();
            var ctx = s.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

            try
            {
                await using var tx = await ctx.Database.BeginTransactionAsync();

                await ctx.Database.ExecuteSqlRawAsync(
                    "SELECT * FROM inventory.stock_balances WHERE product_id = {0} FOR UPDATE", id1);
                t1AcquiredFirst.TrySetResult();

                await t2AcquiredFirst.Task;
                await Task.Delay(100);

                await ctx.Database.ExecuteSqlRawAsync(
                    "SELECT * FROM inventory.stock_balances WHERE product_id = {0} FOR UPDATE", id2);

                var balance = await ctx.StockBalances.FirstAsync(b => b.ProductId == id1);
                balance.SellableQty -= 1m;
                await ctx.SaveChangesAsync();
                await tx.CommitAsync();
                return true;
            }
            catch (Npgsql.PostgresException ex) when (ex.SqlState == "40P01")
            {
                deadlockDetected = true;
                // Wait for conflicting transaction 2 to release locks
                await Task.Delay(1500);

                await using var retryScope = provider.CreateAsyncScope();
                var retryCtx = retryScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
                await using var retryTx = await retryCtx.Database.BeginTransactionAsync();

                var balance = await retryCtx.StockBalances.FirstAsync(b => b.ProductId == id1);
                balance.SellableQty -= 1m;
                await retryCtx.SaveChangesAsync();
                await retryTx.CommitAsync();

                retryOpSucceeded = true;
                return true;
            }
        });

        // Transaction 2 waits for T1 to lock p1, locks p2, signals t2AcquiredFirst, then attempts to lock p1
        var task2 = Task.Run(async () =>
        {
            await using var s = provider.CreateAsyncScope();
            var ctx = s.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

            await t1AcquiredFirst.Task;

            try
            {
                await using var tx = await ctx.Database.BeginTransactionAsync();

                await ctx.Database.ExecuteSqlRawAsync(
                    "SELECT * FROM inventory.stock_balances WHERE product_id = {0} FOR UPDATE", id2);
                t2AcquiredFirst.TrySetResult();

                await Task.Delay(100);

                // Creates deadlock cycle with transaction 1
                await ctx.Database.ExecuteSqlRawAsync(
                    "SELECT * FROM inventory.stock_balances WHERE product_id = {0} FOR UPDATE", id1);

                var balance = await ctx.StockBalances.FirstAsync(b => b.ProductId == id2);
                balance.SellableQty -= 1m;
                await ctx.SaveChangesAsync();
                await tx.CommitAsync();
                return true;
            }
            catch (Npgsql.PostgresException ex) when (ex.SqlState == "40P01")
            {
                deadlockDetected = true;
                // Wait for conflicting transaction 1 to release locks
                await task1;

                await using var retryScope = provider.CreateAsyncScope();
                var retryCtx = retryScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
                await using var retryTx = await retryCtx.Database.BeginTransactionAsync();

                var balance = await retryCtx.StockBalances.FirstAsync(b => b.ProductId == id2);
                balance.SellableQty -= 1m;
                await retryCtx.SaveChangesAsync();
                await retryTx.CommitAsync();

                retryOpSucceeded = true;
                return true;
            }
        });

        var results = await Task.WhenAll(task1, task2);
        Assert.True(results[0], "Task 1 must complete successfully (directly or after retry).");
        Assert.True(results[1], "Task 2 must complete successfully (directly or after retry).");
        Assert.True(deadlockDetected, "PostgreSQL deadlock 40P01 must be detected deterministically.");
        Assert.True(retryOpSucceeded, "The transaction victimized by 40P01 must succeed upon same-ID retry.");

        // Verify conserved state: exactly 1 unit deducted from each product
        await using (var verifyScope = provider.CreateAsyncScope())
        {
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var b1 = await verifyDb.StockBalances.FirstAsync(b => b.ProductId == id1);
            var b2 = await verifyDb.StockBalances.FirstAsync(b => b.ProductId == id2);
            Assert.Equal(9m, b1.SellableQty);
            Assert.Equal(9m, b2.SellableQty);
        }
    }

    [Fact]
    public async Task Postgres_DeadlockRollback_ProductionCompleteSale_SameIdRetry_RecoversAuthoritativeOutcome()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var (p1, terminalId) = await SetupStockAndTerminalAsync(provider, initialStock: 10m);
        var (p2, _) = await SetupStockAndTerminalAsync(provider, initialStock: 10m);

        var id1 = p1.ProductId;
        var id2 = p2.ProductId;
        var opId1 = Guid.NewGuid();
        var opId2 = Guid.NewGuid();

        var saleCmd1 = new CompleteSaleCommand(
            ClientOperationId: opId1,
            CustomerId: null,
            CashierUserId: p1.ActorId,
            SessionId: null,
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Cash,
            AmountTendered: 200m,
            PaymentReference: "Deadlock Recovery Sale 1",
            QuotationId: null,
            Lines: [new CompleteSaleLineInput(p1.ProductId, p1.ProductUnitId, 1m, 200m, [])],
            RegisteredTerminalId: terminalId);
        var fingerprint1 = CompleteSaleCanonicalPayload.ComputeFingerprint(saleCmd1);

        var saleCmd2 = new CompleteSaleCommand(
            ClientOperationId: opId2,
            CustomerId: null,
            CashierUserId: p2.ActorId,
            SessionId: null,
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Cash,
            AmountTendered: 200m,
            PaymentReference: "Deadlock Recovery Sale 2",
            QuotationId: null,
            Lines: [new CompleteSaleLineInput(p2.ProductId, p2.ProductUnitId, 1m, 200m, [])],
            RegisteredTerminalId: terminalId);
        var fingerprint2 = CompleteSaleCanonicalPayload.ComputeFingerprint(saleCmd2);

        var t1AcquiredFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var t2AcquiredFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var deadlockEncountered = false;

        // Production CompleteSaleHandler participation in 40P01 deadlock cycle:
        // Transaction 1 locks p1, signals T2, then calls CompleteSaleHandler selling p2 (which requires p2's balance locked by T2).
        var task1 = Task.Run(async () =>
        {
            await using var s = provider.CreateAsyncScope();
            var ctx = s.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var saleHandler = s.ServiceProvider.GetRequiredService<CompleteSaleHandler>();

            try
            {
                await using var tx = await ctx.Database.BeginTransactionAsync();
                await ctx.Database.ExecuteSqlRawAsync("SET LOCAL deadlock_timeout = '50ms';");
                await ctx.Database.ExecuteSqlRawAsync(
                    "SELECT * FROM inventory.stock_balances WHERE product_id = {0} FOR UPDATE", id1);
                t1AcquiredFirst.TrySetResult();

                await t2AcquiredFirst.Task;
                await Task.Delay(50);

                var res = await saleHandler.HandleAsync(saleCmd2, default);
                if (res.IsSuccess)
                {
                    await tx.CommitAsync();
                    return (Success: true, OpId: opId2, Cmd: saleCmd2);
                }
                await tx.RollbackAsync();
                return (Success: false, OpId: opId2, Cmd: saleCmd2);
            }
            catch (Exception ex) when (IsDeadlockException(ex))
            {
                deadlockEncountered = true;
                return (Success: false, OpId: opId2, Cmd: saleCmd2);
            }
        });

        // Transaction 2 locks p2, waits for T1, then calls CompleteSaleHandler selling p1 (which requires p1's balance locked by T1).
        // This causes the real production CompleteSaleHandler to enter the 40P01 deadlock victim transaction!
        var task2 = Task.Run(async () =>
        {
            await using var s = provider.CreateAsyncScope();
            var ctx = s.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var saleHandler = s.ServiceProvider.GetRequiredService<CompleteSaleHandler>();

            await t1AcquiredFirst.Task;

            try
            {
                await using var tx = await ctx.Database.BeginTransactionAsync();
                await ctx.Database.ExecuteSqlRawAsync("SET LOCAL deadlock_timeout = '50ms';");
                await ctx.Database.ExecuteSqlRawAsync(
                    "SELECT * FROM inventory.stock_balances WHERE product_id = {0} FOR UPDATE", id2);
                t2AcquiredFirst.TrySetResult();

                await Task.Delay(50);

                var res = await saleHandler.HandleAsync(saleCmd1, default);
                if (res.IsSuccess)
                {
                    await tx.CommitAsync();
                    return (Success: true, OpId: opId1, Cmd: saleCmd1);
                }
                await tx.RollbackAsync();
                return (Success: false, OpId: opId1, Cmd: saleCmd1);
            }
            catch (Exception ex) when (IsDeadlockException(ex))
            {
                deadlockEncountered = true;
                return (Success: false, OpId: opId1, Cmd: saleCmd1);
            }
        });

        var (r1, r2) = (await task1, await task2);
        Assert.True(deadlockEncountered, "PostgreSQL 40P01 deadlock must be detected by engine inside CompleteSaleHandler.");
        Assert.True(r1.Success ^ r2.Success, "Exactly one transaction must succeed and one must be rolled back by 40P01 deadlock.");

        var victim = !r1.Success ? r1 : r2;
        var winner = r1.Success ? r1 : r2;

        // Assert that the transaction rolled back by 40P01 rolled back all partial effects and outcome claim for victim
        await using (var verifyScope = provider.CreateAsyncScope())
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var preOutcome = await db.OperationOutcomes.FirstOrDefaultAsync(o => o.ClientOperationId == victim.OpId);
            Assert.Null(preOutcome);
            var preSale = await db.Sales.FirstOrDefaultAsync(s => s.ClientOperationId == victim.OpId);
            Assert.Null(preSale);

            var winnerOutcome = await db.OperationOutcomes.FirstOrDefaultAsync(o => o.ClientOperationId == winner.OpId);
            Assert.NotNull(winnerOutcome);
            Assert.Equal(OperationOutcomeStatus.Succeeded, winnerOutcome.Status);
        }

        // Production mutation handler retry with bound ClientOperationId:
        // Client classifies 40P01 as retryable and retries CompleteSaleHandler with exact same failed ClientOperationId and intent
        await using (var retryScope = provider.CreateAsyncScope())
        {
            var saleHandler = retryScope.ServiceProvider.GetRequiredService<CompleteSaleHandler>();
            var retryResult = await saleHandler.HandleAsync(victim.Cmd, default);
            Assert.True(retryResult.IsSuccess, retryResult.Error?.ToString());
            Assert.False(retryResult.Value!.WasExisting);

            // Replay with identical ClientOperationId recovers authoritative outcome idempotently
            var replayResult = await saleHandler.HandleAsync(victim.Cmd, default);
            Assert.True(replayResult.IsSuccess, replayResult.Error?.ToString());
            Assert.True(replayResult.Value!.WasExisting);
            Assert.Equal(retryResult.Value.SaleId, replayResult.Value.SaleId);

            // Verify outcome ledger recording in PostgreSQL
            var db = retryScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var outcome = await db.OperationOutcomes.FirstOrDefaultAsync(o => o.ClientOperationId == victim.OpId);
            Assert.NotNull(outcome);
            Assert.Equal(OperationOutcomeStatus.Succeeded, outcome.Status);
            Assert.Equal(retryResult.Value.SaleId, outcome.ResultEntityId);
        }
    }

    [Fact]
    public async Task Postgres_CrashRecovery_ServerHttpCrashAfterCommit_BeforeResponse_ClientReplaysSuccessfully()
    {
        var dbConn = Phase2PostgresTestHarness.ResolveConnectionString();
        var hostDll = FindCrashHostDll();

        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var (product, terminalId) = await SetupStockAndTerminalAsync(provider, initialStock: 10m);

        var opId = Guid.NewGuid();
        var port = 7198;

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{hostDll}\" --scenario server-http-crash-after-commit --port {port} --db \"{dbConn}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stderrTask = process.StandardError.ReadToEndAsync();
        var listening = false;
        try
        {
            while (!process.HasExited)
            {
                var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60));
                if (line is null)
                {
                    break;
                }
                if (line.Contains($"READY:SERVER_LISTENING:{port}", StringComparison.Ordinal))
                {
                    listening = true;
                    break;
                }
            }
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            var err = await stderrTask;
            throw new Xunit.Sdk.XunitException($"Server host failed to start. Stderr:\n{err}");
        }

        Assert.True(listening, "Kestrel server must be listening on loopback port.");

        var payload = new
        {
            clientOperationId = opId,
            cashierUserId = product.ActorId,
            invoiceDiscount = 0m,
            paymentMethod = "Cash",
            amountTendered = 200m,
            paymentReference = "HTTP Pipeline Crash Test",
            lines = new[]
            {
                new
                {
                    productId = product.ProductId,
                    productUnitId = product.ProductUnitId,
                    enteredQuantity = 1m,
                    expectedUnitPrice = 200m,
                    inventoryUnitIds = Array.Empty<Guid>()
                }
            },
            registeredTerminalId = terminalId
        };

        var json = JsonSerializer.Serialize(payload);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        Exception? clientException = null;
        HttpResponseMessage? clientResponse = null;
        try
        {
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            clientResponse = await client.PostAsync($"http://127.0.0.1:{port}/api/sales/complete", content);
        }
        catch (Exception ex)
        {
            // Expected: Server terminates child process immediately after commit and before returning HTTP response
            clientException = ex;
        }

        // Prove genuine client response loss: client received an exception / zero response bytes due to server crash post-commit
        Assert.NotNull(clientException);
        Assert.Null(clientResponse);

        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            var err = await stderrTask;
            throw new Xunit.Sdk.XunitException($"Server host did not exit after HTTP commit. Stderr:\n{err}");
        }

        // 1. Verify that despite the connection drop, the sale was committed durably to PostgreSQL
        Guid committedSaleId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var sale = await db.Sales.FirstOrDefaultAsync(s => s.ClientOperationId == opId);
            Assert.NotNull(sale);
            committedSaleId = sale.Id;

            var outbox = await db.OutboxMessages.FirstOrDefaultAsync(m => m.SourceId == sale.Id.ToString("D"));
            Assert.NotNull(outbox);
        }

        // 2. Client retries with the exact same ClientOperationId
        await using (var scope = provider.CreateAsyncScope())
        {
            var saleHandler = scope.ServiceProvider.GetRequiredService<CompleteSaleHandler>();
            var replayCmd = new CompleteSaleCommand(
                ClientOperationId: opId,
                CustomerId: null,
                CashierUserId: product.ActorId,
                SessionId: null,
                InvoiceDiscount: 0m,
                PaymentMethod: SalePaymentMethod.Cash,
                AmountTendered: 200m,
                PaymentReference: "HTTP Pipeline Crash Test",
                QuotationId: null,
                Lines: [new CompleteSaleLineInput(product.ProductId, product.ProductUnitId, 1m, 200m, [])],
                RegisteredTerminalId: terminalId);

            var replayRes = await saleHandler.HandleAsync(replayCmd, default);
            Assert.True(replayRes.IsSuccess, replayRes.Error?.ToString());
            Assert.True(replayRes.Value!.WasExisting);
            Assert.Equal(committedSaleId, replayRes.Value.SaleId);

            // Stock conserved: 10 initial - 1 committed = 9 sellable
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var balance = await db.StockBalances.FirstAsync(b => b.ProductId == product.ProductId);
            Assert.Equal(9m, balance.SellableQty);
        }
    }

    [Fact]
    public async Task Postgres_CrashRecovery_DesktopJournalCrashAfterSubmit_RestartRecoversPendingIntent()
    {
        var dbConn = Phase2PostgresTestHarness.ResolveConnectionString();
        var hostDll = FindCrashHostDll();
        var tempStateDir = Path.Combine(Path.GetTempPath(), "EdgeRetailsDesktopTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempStateDir);

        try
        {
            await using var provider = Phase2PostgresTestHarness.BuildProvider();
            var (product, terminalId) = await SetupStockAndTerminalAsync(provider, initialStock: 10m);

            var opId = Guid.NewGuid();

            // 1. Desktop process records intent to FileClientOperationIntentStore, sends request to PostgreSQL, commits, and crashes before journal completion
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{hostDll}\" --scenario desktop-journal-crash-after-submit --state-dir \"{tempStateDir}\" --op-id \"{opId}\" --db \"{dbConn}\" --user-id \"{product.ActorId}\" --product-id \"{product.ProductId}\" --unit-id \"{product.ProductUnitId}\" --terminal-id \"{terminalId}\" --qty 1 --amount 200",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var process = new Process { StartInfo = psi })
            {
                process.Start();
                var stdOutTask = process.StandardOutput.ReadToEndAsync();
                var stdErrTask = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                var stdOut = await stdOutTask;
                var stdErr = await stdErrTask;
                Assert.True(stdOut.Contains("READY:DESKTOP_SALE_COMMITTED"), $"stdout: '{stdOut}' | stderr: '{stdErr}' | exit: {process.ExitCode}");
            }

            // Verify journal file exists on disk with pending intent
            var journalFile = Path.Combine(tempStateDir, "operation-intents.json");
            Assert.True(File.Exists(journalFile), "Desktop journal file must exist on disk after crash.");
            var journalJson = await File.ReadAllTextAsync(journalFile);
            Assert.Contains(opId.ToString("D"), journalJson);

            // Verify PostgreSQL already committed the sale durably before client journal completion
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
                var sale = await db.Sales.FirstOrDefaultAsync(s => s.ClientOperationId == opId);
                Assert.NotNull(sale);
                var outbox = await db.OutboxMessages.FirstOrDefaultAsync(m => m.SourceId == sale.Id.ToString("D"));
                Assert.NotNull(outbox);
            }

            // 2. Desktop process cold restart: reads pending intent from journal, queries authoritative status / replays with SAME immutable intent/context/ID,
            // verifies durable existing result (WasExisting == true), and ONLY THEN marks journal intent complete
            var recoverPsi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{hostDll}\" --scenario desktop-journal-recover --state-dir \"{tempStateDir}\" --op-id \"{opId}\" --db \"{dbConn}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var recoverProcess = new Process { StartInfo = recoverPsi })
            {
                recoverProcess.Start();
                var recoverOutTask = recoverProcess.StandardOutput.ReadToEndAsync();
                var recoverErrTask = recoverProcess.StandardError.ReadToEndAsync();
                await Task.WhenAll(recoverOutTask, recoverErrTask);
                await recoverProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

                var recoverOut = await recoverOutTask;
                var recoverErr = await recoverErrTask;
                Assert.True(recoverProcess.ExitCode == 0, $"Recover failed with exit code {recoverProcess.ExitCode}. Output: {recoverOut}\nError: {recoverErr}");
                Assert.Contains("SUCCESS:DESKTOP_JOURNAL_COMPLETED", recoverOut);
            }

            // 3. Verify stock & outcome conservation: exactly 1 sale committed, exactly 1 deduction (10 initial - 1 = 9 sellable)
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
                var sales = await db.Sales.Where(s => s.ClientOperationId == opId).ToListAsync();
                Assert.Single(sales);

                var balance = await db.StockBalances.FirstAsync(b => b.ProductId == product.ProductId);
                Assert.Equal(9m, balance.SellableQty);

                var outcome = await db.OperationOutcomes.FirstOrDefaultAsync(o => o.ClientOperationId == opId);
                Assert.NotNull(outcome);
                Assert.Equal(OperationOutcomeStatus.Succeeded, outcome.Status);
                Assert.Equal(sales[0].Id, outcome.ResultEntityId);

                // Additional idempotent replay confirms durable outcome
                var saleHandler = scope.ServiceProvider.GetRequiredService<CompleteSaleHandler>();
                var replayCmd = new CompleteSaleCommand(
                    ClientOperationId: opId,
                    CustomerId: null,
                    CashierUserId: product.ActorId,
                    SessionId: null,
                    InvoiceDiscount: 0m,
                    PaymentMethod: SalePaymentMethod.Cash,
                    AmountTendered: 200m,
                    PaymentReference: $"Desktop Crash Test Sale {opId:N}",
                    QuotationId: null,
                    Lines: [new CompleteSaleLineInput(product.ProductId, product.ProductUnitId, 1m, 200m, [])],
                    RegisteredTerminalId: terminalId);

                var replayRes = await saleHandler.HandleAsync(replayCmd, default);
                Assert.True(replayRes.IsSuccess, replayRes.Error?.ToString());
                Assert.True(replayRes.Value!.WasExisting);
                Assert.Equal(sales[0].Id, replayRes.Value.SaleId);
            }
        }
        finally
        {
            if (Directory.Exists(tempStateDir))
            {
                try { Directory.Delete(tempStateDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task Postgres_CrashRecovery_DesktopExpenseCrashAfterSubmit_RestartRecoversPendingIntent()
    {
        var dbConn = Phase2PostgresTestHarness.ResolveConnectionString();
        var hostDll = FindCrashHostDll();
        var tempStateDir = Path.Combine(Path.GetTempPath(), "EdgeRetailsDesktopExpenseTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempStateDir);

        try
        {
            await using var provider = Phase2PostgresTestHarness.BuildProvider();
            var (actorId, category, subcategory, sessionId) = await SetupExpensePrerequisitesAsync(provider);

            var opId = Guid.NewGuid();
            var amount = 150m;

            // 1. Desktop process records intent to FileClientOperationIntentStore, sends request to PostgreSQL, commits, and crashes on readback before journal completion
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{hostDll}\" --scenario desktop-expense-crash-after-submit --state-dir \"{tempStateDir}\" --op-id \"{opId}\" --db \"{dbConn}\" --user-id \"{actorId}\" --category \"{category.Name}\" --subcategory \"{subcategory.Name}\" --amount {amount.ToString(System.Globalization.CultureInfo.InvariantCulture)} --payment-method CASH --note \"Crash test expense {opId:N}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var process = new Process { StartInfo = psi })
            {
                process.Start();
                var stdOutTask = process.StandardOutput.ReadToEndAsync();
                var stdErrTask = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                var stdOut = await stdOutTask;
                var stdErr = await stdErrTask;
                Assert.True(stdOut.Contains("READY:DESKTOP_EXPENSE_COMMITTED"), $"stdout: '{stdOut}' | stderr: '{stdErr}' | exit: {process.ExitCode}");
            }

            // Verify journal file exists on disk with pending intent
            var journalFile = Path.Combine(tempStateDir, "operation-intents.json");
            Assert.True(File.Exists(journalFile), "Desktop journal file must exist on disk after crash.");
            var journalJson = await File.ReadAllTextAsync(journalFile);
            Assert.Contains(opId.ToString("D"), journalJson);
            Assert.Contains("expense:create", journalJson);

            // Verify PostgreSQL already committed the expense, cash movement, and OperationOutcome durably before client journal completion
            Guid committedExpenseId;
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
                var expense = await db.Expenses.FirstOrDefaultAsync(e => e.ClientOperationId == opId);
                Assert.NotNull(expense);
                Assert.Equal(amount, expense.Amount);
                Assert.Equal(ExpenseStatus.Posted, expense.Status);
                Assert.Equal(category.Id, expense.CategoryId);
                Assert.Equal(subcategory.Id, expense.SubcategoryId);
                committedExpenseId = expense.Id;

                var cashMovement = await db.CashMovements.FirstOrDefaultAsync(m => m.SourceId == expense.Id);
                Assert.NotNull(cashMovement);
                Assert.Equal(CashMovementType.ExpenseCashOut, cashMovement.MovementType);
                Assert.Equal(CashMovementDirection.Out, cashMovement.Direction);
                Assert.Equal(amount, cashMovement.Amount);
                Assert.Equal(sessionId, cashMovement.CashSessionId);

                var outcome = await db.OperationOutcomes.FirstOrDefaultAsync(o => o.ClientOperationId == opId);
                Assert.NotNull(outcome);
                Assert.Equal(OperationOutcomeStatus.Succeeded, outcome.Status);
                Assert.Equal(expense.Id, outcome.ResultEntityId);
            }

            // 2. Desktop process cold restart: reads pending intent from journal, queries authoritative status / replays with SAME immutable intent/context/ID,
            // verifies durable existing result (WasExisting == true), and ONLY THEN marks journal intent complete
            var recoverPsi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{hostDll}\" --scenario desktop-expense-recover --state-dir \"{tempStateDir}\" --op-id \"{opId}\" --db \"{dbConn}\" --user-id \"{actorId}\" --category \"{category.Name}\" --subcategory \"{subcategory.Name}\" --amount {amount.ToString(System.Globalization.CultureInfo.InvariantCulture)} --payment-method CASH --note \"Crash test expense {opId:N}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var recoverProcess = new Process { StartInfo = recoverPsi })
            {
                recoverProcess.Start();
                var recoverOutTask = recoverProcess.StandardOutput.ReadToEndAsync();
                var recoverErrTask = recoverProcess.StandardError.ReadToEndAsync();
                await Task.WhenAll(recoverOutTask, recoverErrTask);
                await recoverProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

                var recoverOut = await recoverOutTask;
                var recoverErr = await recoverErrTask;
                Assert.True(recoverProcess.ExitCode == 0, $"Recover failed with exit code {recoverProcess.ExitCode}. Output: {recoverOut}\nError: {recoverErr}");
                Assert.Contains("SUCCESS:DESKTOP_EXPENSE_COMPLETED", recoverOut);
            }

            // 3. Verify conservation: exactly 1 expense committed, exactly 1 cash deduction, journal completed
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
                var expenses = await db.Expenses.Where(e => e.ClientOperationId == opId).ToListAsync();
                Assert.Single(expenses);
                Assert.Equal(committedExpenseId, expenses[0].Id);

                var cashMovements = await db.CashMovements.Where(m => m.SourceId == committedExpenseId).ToListAsync();
                Assert.Single(cashMovements);
                Assert.Equal(amount, cashMovements[0].Amount);

                var outcomes = await db.OperationOutcomes.Where(o => o.ClientOperationId == opId).ToListAsync();
                Assert.Single(outcomes);
                Assert.Equal(OperationOutcomeStatus.Succeeded, outcomes[0].Status);

                // Verify journal file has zero pending intents for opId
                var finalJournalJson = await File.ReadAllTextAsync(journalFile);
                Assert.DoesNotContain(opId.ToString("D"), finalJournalJson);

                // Hostile replay: same ClientOperationId with conflicting payload (e.g. amount tampered to 999m) must fail closed
                var postHandler = scope.ServiceProvider.GetRequiredService<PostExpenseHandler>();
                var tamperedCmd = new PostExpenseCommand(
                    ClientOperationId: opId,
                    CategoryId: category.Id,
                    SubcategoryId: subcategory.Id,
                    ExpenseDate: DateOnly.FromDateTime(DateTime.Today),
                    Amount: 999m,
                    PaymentMethod: ExpensePaymentMethod.Cash,
                    Description: subcategory.Name,
                    Reference: $"Crash test expense {opId:N}",
                    ActorId: actorId);

                var tamperedResult = await postHandler.HandleAsync(tamperedCmd, default);
                Assert.False(tamperedResult.IsSuccess);
                Assert.Equal("idempotency.payload_mismatch", tamperedResult.Error?.Code);

                // Verify DB remains strictly conserved after hostile replay
                Assert.Single(await db.Expenses.Where(e => e.ClientOperationId == opId).ToListAsync());
                Assert.Single(await db.CashMovements.Where(m => m.SourceId == committedExpenseId).ToListAsync());
            }
        }
        finally
        {
            if (Directory.Exists(tempStateDir))
            {
                try { Directory.Delete(tempStateDir, recursive: true); } catch { }
            }
        }
    }

    private static async Task<(Guid ActorId, ExpenseCategory Category, ExpenseSubcategory Subcategory, Guid CashSessionId)> SetupExpensePrerequisitesAsync(
        ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var actorId = await IntegrationIdentitySeeder.CreateActorAsync(db);

        var categoryName = "CrashExpense-" + Guid.NewGuid().ToString("N");
        var category = new ExpenseCategory { Name = categoryName, IsActive = true };
        db.ExpenseCategories.Add(category);
        await db.SaveChangesAsync();

        var subcategoryName = "Stationery";
        var subcategory = new ExpenseSubcategory { CategoryId = category.Id, Name = subcategoryName, IsActive = true };
        db.ExpenseSubcategories.Add(subcategory);
        await db.SaveChangesAsync();

        var openSessions = await db.CashSessions.Where(s => s.Status == CashSessionStatus.Open).ToListAsync();
        foreach (var s in openSessions)
        {
            var movements = await db.CashMovements.AsNoTracking().Where(x => x.CashSessionId == s.Id).ToListAsync();
            var cashIn = movements.Where(x => x.Direction == CashMovementDirection.In).Sum(x => x.Amount);
            var cashOut = movements.Where(x => x.Direction == CashMovementDirection.Out).Sum(x => x.Amount);
            s.Close(cashIn, cashOut, Math.Max(0m, s.OpeningCash + cashIn - cashOut), s.OpenedBy, DateTimeOffset.UtcNow);
        }
        if (openSessions.Count > 0)
        {
            await db.SaveChangesAsync();
        }

        var openHandler = ActivatorUtilities.CreateInstance<OpenCashSessionHandler>(scope.ServiceProvider);
        var openResult = await openHandler.HandleAsync(new OpenCashSessionCommand(5000m, actorId, "Crash test open session"), default);
        Assert.True(openResult.IsSuccess, openResult.Error?.ToString());

        return (actorId, category, subcategory, openResult.Value);
    }

    private static string FindCrashHostDll()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "EdgeRetails.CrashTestHost.dll"),
            Path.Combine(baseDir, "..", "..", "..", "..", "EdgeRetails.CrashTestHost", "bin", "Release", "net10.0-windows", "EdgeRetails.CrashTestHost.dll"),
            Path.Combine(baseDir, "..", "..", "..", "..", "EdgeRetails.CrashTestHost", "bin", "Release", "net10.0", "EdgeRetails.CrashTestHost.dll"),
            Path.Combine(baseDir, "..", "..", "..", "..", "EdgeRetails.CrashTestHost", "bin", "Debug", "net10.0-windows", "EdgeRetails.CrashTestHost.dll"),
            Path.Combine(baseDir, "..", "..", "..", "..", "EdgeRetails.CrashTestHost", "bin", "Debug", "net10.0", "EdgeRetails.CrashTestHost.dll")
        };

        foreach (var c in candidates)
        {
            var fullPath = Path.GetFullPath(c);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
        }

        throw new FileNotFoundException($"Could not locate EdgeRetails.CrashTestHost.dll in any expected location from {baseDir}");
    }

    private sealed class ThrowingPrintHandler : IOutboxEffectHandler
    {
        private readonly Exception _exception;
        public ThrowingPrintHandler(Exception exception) => _exception = exception;
        public string EffectType => PrintOutboxEffectHandler.TypeName;
        public Task ExecuteAsync(OutboxMessage message, CancellationToken cancellationToken = default) =>
            throw _exception;
    }

    private sealed class SuccessfulPrintHandler : IOutboxEffectHandler
    {
        public string EffectType => PrintOutboxEffectHandler.TypeName;
        public Task ExecuteAsync(OutboxMessage message, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class HoldingOperationLock : IOperationLock
    {
        private IOperationLock? _inner;
        public TaskCompletionSource LockAcquired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseLock { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void SetInner(IOperationLock inner) => _inner = inner;

        public async Task AcquireAsync(Guid clientOperationId, CancellationToken cancellationToken)
        {
            if (_inner is not null)
            {
                await _inner.AcquireAsync(clientOperationId, cancellationToken);
            }
            LockAcquired.TrySetResult();
            await ReleaseLock.Task;
        }
    }

    private static bool IsDeadlockException(Exception ex)
    {
        if (ex is Npgsql.PostgresException pg && pg.SqlState == "40P01")
        {
            return true;
        }
        if (ex.InnerException is not null && IsDeadlockException(ex.InnerException))
        {
            return true;
        }
        return false;
    }

    // =========================================================================
    // MON-P12W3-R9-001: Legacy Warranty Evaluator Deterministic Tests
    // =========================================================================

    [Fact]
    public async Task Postgres_WarrantyClaim_LegacyReconcile_OmittedSupplierRecovers_ExplicitSupplierMismatchRejects()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var (product, terminalId) = await SetupStockAndTerminalAsync(provider, initialStock: 5m);

        Guid customerId;
        Guid saleId;
        Guid saleItemId;
        var existingSupplierId = product.SupplierId;
        var otherSupplierId = Guid.NewGuid();

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var customer = new Customer
            {
                Name = "Warranty Customer Legacy",
                IsActive = true,
                IsWalkIn = false
            };
            db.Customers.Add(customer);

            var prod = await db.Products.FirstAsync(p => p.Id == product.ProductId);
            prod.DefaultWarrantyMonths = 12;
            await db.SaveChangesAsync();

            customerId = customer.Id;

            var saleHandler = scope.ServiceProvider.GetRequiredService<CompleteSaleHandler>();
            var saleRes = await saleHandler.HandleAsync(new CompleteSaleCommand(
                ClientOperationId: Guid.NewGuid(),
                CustomerId: customerId,
                CashierUserId: product.ActorId,
                SessionId: null,
                InvoiceDiscount: 0m,
                PaymentMethod: SalePaymentMethod.Cash,
                AmountTendered: 200m,
                PaymentReference: "Warranty Legacy Sale",
                QuotationId: null,
                Lines: [new CompleteSaleLineInput(product.ProductId, product.ProductUnitId, 1m, 200m, [])],
                RegisteredTerminalId: terminalId), default);
            Assert.True(saleRes.IsSuccess, saleRes.Error?.ToString());
            saleId = saleRes.Value!.SaleId;
            saleItemId = await db.SaleItems.Where(x => x.SaleId == saleId).Select(x => x.Id).FirstAsync();
        }

        var opId = Guid.NewGuid();
        // Create initial warranty claim with explicit supplier
        var initialCmd = new CreateWarrantyClaimCommand(
            CustomerId: customerId,
            OriginalSaleId: saleId,
            SupplierId: existingSupplierId,
            ActorId: product.ActorId,
            Items: [new WarrantyClaimItemInput(product.ProductId, 1m, "Dead screen", saleItemId, null)],
            ClientOperationId: opId);

        Guid claimId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateWarrantyClaimHandler>();
            var res = await handler.HandleAsync(initialCmd, default);
            Assert.True(res.IsSuccess, res.Error?.ToString());
            claimId = res.Value;

            // Remove outcome ledger entry to simulate legacy pre-lock state (claim exists in DB, no outcome ledger row)
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var outcome = await db.OperationOutcomes.FirstAsync(o => o.ClientOperationId == opId);
            db.OperationOutcomes.Remove(outcome);
            await db.SaveChangesAsync();
        }

        // Test 1: Replay with omitted/derived supplier (SupplierId: null) matches existing claim and recovers post-lock
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateWarrantyClaimHandler>();
            var omittedSupplierCmd = new CreateWarrantyClaimCommand(
                CustomerId: customerId,
                OriginalSaleId: saleId,
                SupplierId: null,
                ActorId: product.ActorId,
                Items: [new WarrantyClaimItemInput(product.ProductId, 1m, "Dead screen", saleItemId, null)],
                ClientOperationId: opId);

            var replayRes = await handler.HandleAsync(omittedSupplierCmd, default);
            Assert.True(replayRes.IsSuccess, replayRes.Error?.ToString());
            Assert.Equal(claimId, replayRes.Value);

            // Again delete outcome ledger row to test post-lock explicit supplier mismatch
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var outcome = await db.OperationOutcomes.FirstOrDefaultAsync(o => o.ClientOperationId == opId);
            if (outcome is not null)
            {
                db.OperationOutcomes.Remove(outcome);
                await db.SaveChangesAsync();
            }
        }

        // Test 2: Replay with changed explicit supplier rejects fail-closed with idempotency.payload_mismatch
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateWarrantyClaimHandler>();
            var changedSupplierCmd = new CreateWarrantyClaimCommand(
                CustomerId: customerId,
                OriginalSaleId: saleId,
                SupplierId: otherSupplierId,
                ActorId: product.ActorId,
                Items: [new WarrantyClaimItemInput(product.ProductId, 1m, "Dead screen", saleItemId, null)],
                ClientOperationId: opId);

            var mismatchRes = await handler.HandleAsync(changedSupplierCmd, default);
            Assert.False(mismatchRes.IsSuccess);
            Assert.Equal("idempotency.payload_mismatch", mismatchRes.Error?.Code);
        }
    }

    [Fact]
    public async Task Postgres_WarrantyClaim_LegacyReconcile_PostLock_MissingSaleItemOrUnitMismatchRejects()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var (product, terminalId) = await SetupStockAndTerminalAsync(provider, initialStock: 5m);

        Guid customerId;
        Guid saleId;
        Guid saleItemId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var customer = new Customer
            {
                Name = "Warranty Customer PostLock",
                IsActive = true,
                IsWalkIn = false
            };
            db.Customers.Add(customer);

            var prod = await db.Products.FirstAsync(p => p.Id == product.ProductId);
            prod.DefaultWarrantyMonths = 12;
            await db.SaveChangesAsync();
            customerId = customer.Id;

            var saleHandler = scope.ServiceProvider.GetRequiredService<CompleteSaleHandler>();
            var saleRes = await saleHandler.HandleAsync(new CompleteSaleCommand(
                ClientOperationId: Guid.NewGuid(),
                CustomerId: customerId,
                CashierUserId: product.ActorId,
                SessionId: null,
                InvoiceDiscount: 0m,
                PaymentMethod: SalePaymentMethod.Cash,
                AmountTendered: 200m,
                PaymentReference: "Warranty PostLock Sale",
                QuotationId: null,
                Lines: [new CompleteSaleLineInput(product.ProductId, product.ProductUnitId, 1m, 200m, [])],
                RegisteredTerminalId: terminalId), default);
            Assert.True(saleRes.IsSuccess, saleRes.Error?.ToString());
            saleId = saleRes.Value!.SaleId;
            saleItemId = await db.SaleItems.Where(x => x.SaleId == saleId).Select(x => x.Id).FirstAsync();
        }

        var opId = Guid.NewGuid();
        var command = new CreateWarrantyClaimCommand(
            CustomerId: customerId,
            OriginalSaleId: saleId,
            SupplierId: null,
            ActorId: product.ActorId,
            Items: [new WarrantyClaimItemInput(product.ProductId, 1m, "Flickering screen", saleItemId, null)],
            ClientOperationId: opId);

        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateWarrantyClaimHandler>();
            var res = await handler.HandleAsync(command, default);
            Assert.True(res.IsSuccess, res.Error?.ToString());

            // Remove outcome ledger row to force legacy evaluation in post-lock path
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var outcome = await db.OperationOutcomes.FirstAsync(o => o.ClientOperationId == opId);
            db.OperationOutcomes.Remove(outcome);
            await db.SaveChangesAsync();
        }

        // Test: Changed Sale Item ID in post-lock legacy path is rejected fail-closed
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateWarrantyClaimHandler>();
            var wrongSaleItemCmd = new CreateWarrantyClaimCommand(
                CustomerId: customerId,
                OriginalSaleId: saleId,
                SupplierId: null,
                ActorId: product.ActorId,
                Items: [new WarrantyClaimItemInput(product.ProductId, 1m, "Flickering screen", Guid.NewGuid(), null)],
                ClientOperationId: opId);

            var resWrongItem = await handler.HandleAsync(wrongSaleItemCmd, default);
            Assert.False(resWrongItem.IsSuccess);
            Assert.Equal("idempotency.payload_mismatch", resWrongItem.Error?.Code);
        }
    }

    [Fact]
    public async Task Postgres_WarrantyClaim_LegacyReconcile_BlankOrNullFingerprint_ReplayRecovers_MismatchesReject_MissingEntityFailsClosed()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var (product, terminalId) = await SetupStockAndTerminalAsync(provider, initialStock: 5m);

        Guid customerId;
        Guid saleId;
        Guid saleItemId;
        var existingSupplierId = product.SupplierId;
        Guid unitId;
        Guid otherUnitId;

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var customer = new Customer
            {
                Name = "Warranty Legacy Replay Customer",
                IsActive = true,
                IsWalkIn = false
            };
            db.Customers.Add(customer);

            var prod = await db.Products.FirstAsync(p => p.Id == product.ProductId);
            prod.DefaultWarrantyMonths = 12;
            await db.SaveChangesAsync();
            customerId = customer.Id;

            var saleHandler = scope.ServiceProvider.GetRequiredService<CompleteSaleHandler>();
            var saleRes = await saleHandler.HandleAsync(new CompleteSaleCommand(
                ClientOperationId: Guid.NewGuid(),
                CustomerId: customerId,
                CashierUserId: product.ActorId,
                SessionId: null,
                InvoiceDiscount: 0m,
                PaymentMethod: SalePaymentMethod.Cash,
                AmountTendered: 200m,
                PaymentReference: "Warranty Blank Fingerprint Sale",
                QuotationId: null,
                Lines: [new CompleteSaleLineInput(product.ProductId, product.ProductUnitId, 1m, 200m, [])],
                RegisteredTerminalId: terminalId), default);
            Assert.True(saleRes.IsSuccess, saleRes.Error?.ToString());
            saleId = saleRes.Value!.SaleId;
            saleItemId = await db.SaleItems.Where(x => x.SaleId == saleId).Select(x => x.Id).FirstAsync();
        }

        var opId = Guid.NewGuid();
        var initialCmd = new CreateWarrantyClaimCommand(
            CustomerId: customerId,
            OriginalSaleId: saleId,
            SupplierId: existingSupplierId,
            ActorId: product.ActorId,
            Items: [new WarrantyClaimItemInput(product.ProductId, 1m, "Dead screen", saleItemId, null)],
            ClientOperationId: opId);

        Guid claimId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateWarrantyClaimHandler>();
            var res = await handler.HandleAsync(initialCmd, default);
            Assert.True(res.IsSuccess, res.Error?.ToString());
            claimId = res.Value;

            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var claimItemId = await db.WarrantyClaimItems.Where(x => x.ClaimId == claimId).Select(x => x.Id).FirstAsync();

            var unit1 = new InventoryUnit
            {
                ProductId = product.ProductId,
                OriginType = InventoryUnitOriginType.WarrantyReplacement,
                SourceWarrantyClaimItemId = claimItemId,
                Status = InventoryUnitStatus.Sold,
                CreatedAt = DateTimeOffset.UtcNow
            };
            var unit2 = new InventoryUnit
            {
                ProductId = product.ProductId,
                OriginType = InventoryUnitOriginType.WarrantyReplacement,
                SourceWarrantyClaimItemId = claimItemId,
                Status = InventoryUnitStatus.Sold,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.InventoryUnits.AddRange(unit1, unit2);
            await db.SaveChangesAsync();
            unitId = unit1.Id;
            otherUnitId = unit2.Id;

            // Seed a persisted unit so unit identity matching can be validated
            db.WarrantyClaimItemUnits.Add(new WarrantyClaimItemUnit
            {
                ClaimItemId = claimItemId,
                OriginalInventoryUnitId = unitId,
                ActiveOriginalInventoryUnitId = unitId,
                OriginalIdentitySnapshot = "SN-001"
            });

            // Set outcome to blank fingerprint to test early outcome path evaluation with blank fingerprint
            var outcome = await db.OperationOutcomes.FirstAsync(o => o.ClientOperationId == opId);
            outcome.PayloadFingerprint = "";
            await db.SaveChangesAsync();
        }

        var matchingReplayCmd = initialCmd with
        {
            Items = [new WarrantyClaimItemInput(product.ProductId, 1m, "Dead screen", saleItemId, null, [new WarrantyClaimUnitInput(unitId, "SN-001")])]
        };

        // Sub-test 1: Blank fingerprint identical replay returns original claim ID
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateWarrantyClaimHandler>();
            var replayRes = await handler.HandleAsync(matchingReplayCmd, default);
            Assert.True(replayRes.IsSuccess, replayRes.Error?.ToString());
            Assert.Equal(claimId, replayRes.Value);
        }

        // Sub-test 2: Null fingerprint identical replay returns original claim ID
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var outcome = await db.OperationOutcomes.FirstAsync(o => o.ClientOperationId == opId);
            outcome.PayloadFingerprint = null;
            await db.SaveChangesAsync();

            var handler = scope.ServiceProvider.GetRequiredService<CreateWarrantyClaimHandler>();
            var replayRes = await handler.HandleAsync(matchingReplayCmd, default);
            Assert.True(replayRes.IsSuccess, replayRes.Error?.ToString());
            Assert.Equal(claimId, replayRes.Value);
        }

        // Sub-test 3: Changed SaleItemId rejects fail-closed with idempotency.payload_mismatch
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateWarrantyClaimHandler>();
            var changedSaleItemCmd = initialCmd with
            {
                Items = [new WarrantyClaimItemInput(product.ProductId, 1m, "Dead screen", Guid.NewGuid(), null, [new WarrantyClaimUnitInput(unitId, "SN-001")])]
            };
            var res = await handler.HandleAsync(changedSaleItemCmd, default);
            Assert.False(res.IsSuccess);
            Assert.Equal("idempotency.payload_mismatch", res.Error?.Code);
        }

        // Sub-test 4: Changed Unit rejects fail-closed with idempotency.payload_mismatch
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateWarrantyClaimHandler>();
            var changedUnitCmd = initialCmd with
            {
                Items = [new WarrantyClaimItemInput(product.ProductId, 1m, "Dead screen", saleItemId, null, [new WarrantyClaimUnitInput(otherUnitId, "SN-002")])]
            };
            var res = await handler.HandleAsync(changedUnitCmd, default);
            Assert.False(res.IsSuccess);
            Assert.Equal("idempotency.payload_mismatch", res.Error?.Code);
        }

        // Sub-test 5: Changed explicit SupplierId rejects fail-closed with idempotency.payload_mismatch
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateWarrantyClaimHandler>();
            var changedSupplierCmd = initialCmd with
            {
                SupplierId = Guid.NewGuid()
            };
            var res = await handler.HandleAsync(changedSupplierCmd, default);
            Assert.False(res.IsSuccess);
            Assert.Equal("idempotency.payload_mismatch", res.Error?.Code);
        }

        // Sub-test 6: Missing entity fails closed with idempotency.legacy_claim_missing
        var missingEntityOpId = Guid.NewGuid();
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            db.OperationOutcomes.Add(new OperationOutcome
            {
                ClientOperationId = missingEntityOpId,
                OperationType = "WarrantyClaim",
                ResultEntityId = Guid.NewGuid(), // does not exist in WarrantyClaims table
                ResultEntityType = "WarrantyClaim",
                Status = OperationOutcomeStatus.Succeeded,
                WasCommitted = true,
                PayloadFingerprint = null,
                CompletedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();

            var handler = scope.ServiceProvider.GetRequiredService<CreateWarrantyClaimHandler>();
            var missingEntityCmd = initialCmd with
            {
                ClientOperationId = missingEntityOpId
            };
            var res = await handler.HandleAsync(missingEntityCmd, default);
            Assert.False(res.IsSuccess);
            Assert.Equal("idempotency.legacy_claim_missing", res.Error?.Code);
        }

        // Sub-test 7: Preserved Failed outcome returns original error without entity lookup
        var failedOpId = Guid.NewGuid();
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            db.OperationOutcomes.Add(new OperationOutcome
            {
                ClientOperationId = failedOpId,
                OperationType = "WarrantyClaim",
                ResultEntityId = null,
                Status = OperationOutcomeStatus.Failed,
                ErrorCode = "warranty.hardware_unsupported",
                ErrorMessage = "Original hardware error message",
                WasCommitted = false,
                PayloadFingerprint = null,
                CompletedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();

            var handler = scope.ServiceProvider.GetRequiredService<CreateWarrantyClaimHandler>();
            var failedCmd = initialCmd with
            {
                ClientOperationId = failedOpId
            };
            var res = await handler.HandleAsync(failedCmd, default);
            Assert.False(res.IsSuccess);
            Assert.Equal("warranty.hardware_unsupported", res.Error?.Code);
            Assert.Equal("Original hardware error message", res.Error?.Message);
        }
    }

    [Fact]
    public async Task Postgres_WarrantyClaim_LegacyReconcile_TwoIdenticalLinesWithDistinctSaleItems_ReorderedReplaySucceedsWithoutRowReuse()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var (product, terminalId) = await SetupStockAndTerminalAsync(provider, initialStock: 10m);

        Guid customerId;
        Guid saleId;
        Guid saleItemId1;
        Guid saleItemId2;

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var customer = new Customer
            {
                Name = "Warranty Multiline Customer",
                IsActive = true,
                IsWalkIn = false
            };
            db.Customers.Add(customer);

            var prod = await db.Products.FirstAsync(p => p.Id == product.ProductId);
            prod.DefaultWarrantyMonths = 12;

            var unitBox = new Unit { Name = $"Box Unit {Guid.NewGuid():N}", Symbol = $"B{Guid.NewGuid():N}"[..4], DisplayDecimalPlaces = 0, IsActive = true };
            db.Units.Add(unitBox);
            var prodUnitBox = new ProductUnit
            {
                ProductId = product.ProductId,
                UnitId = unitBox.Id,
                FactorToBaseUnit = 1m,
                CanSell = true,
                CanPurchase = true,
                IsActive = true
            };
            db.ProductUnits.Add(prodUnitBox);

            await db.SaveChangesAsync();
            customerId = customer.Id;

            // Complete sale with 2 lines for the same product using distinct product units, giving 2 distinct sale items
            var saleHandler = scope.ServiceProvider.GetRequiredService<CompleteSaleHandler>();
            var saleRes = await saleHandler.HandleAsync(new CompleteSaleCommand(
                ClientOperationId: Guid.NewGuid(),
                CustomerId: customerId,
                CashierUserId: product.ActorId,
                SessionId: null,
                InvoiceDiscount: 0m,
                PaymentMethod: SalePaymentMethod.Cash,
                AmountTendered: 400m,
                PaymentReference: "Warranty Multiline Sale",
                QuotationId: null,
                Lines: [
                    new CompleteSaleLineInput(product.ProductId, product.ProductUnitId, 1m, 200m, []),
                    new CompleteSaleLineInput(product.ProductId, prodUnitBox.Id, 1m, 200m, [])
                ],
                RegisteredTerminalId: terminalId), default);
            Assert.True(saleRes.IsSuccess, saleRes.Error?.ToString());
            saleId = saleRes.Value!.SaleId;

            var saleItems = await db.SaleItems.Where(x => x.SaleId == saleId).OrderBy(x => x.Id).ToListAsync();
            Assert.Equal(2, saleItems.Count);
            saleItemId1 = saleItems[0].Id;
            saleItemId2 = saleItems[1].Id;
        }

        var opId = Guid.NewGuid();
        var initialCmd = new CreateWarrantyClaimCommand(
            CustomerId: customerId,
            OriginalSaleId: saleId,
            SupplierId: product.SupplierId,
            ActorId: product.ActorId,
            Items: [
                new WarrantyClaimItemInput(product.ProductId, 1m, "Cracked bezel", saleItemId1, null),
                new WarrantyClaimItemInput(product.ProductId, 1m, "Cracked bezel", saleItemId2, null)
            ],
            ClientOperationId: opId);

        Guid claimId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateWarrantyClaimHandler>();
            var res = await handler.HandleAsync(initialCmd, default);
            Assert.True(res.IsSuccess, res.Error?.ToString());
            claimId = res.Value;

            // Update outcome to blank fingerprint
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var outcome = await db.OperationOutcomes.FirstAsync(o => o.ClientOperationId == opId);
            outcome.PayloadFingerprint = "";
            await db.SaveChangesAsync();
        }

        // Test A: Reordered replay (saleItemId2 first, then saleItemId1) succeeds and returns original claimId
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateWarrantyClaimHandler>();
            var reorderedCmd = initialCmd with
            {
                Items = [
                    new WarrantyClaimItemInput(product.ProductId, 1m, "Cracked bezel", saleItemId2, null),
                    new WarrantyClaimItemInput(product.ProductId, 1m, "Cracked bezel", saleItemId1, null)
                ]
            };

            var replayRes = await handler.HandleAsync(reorderedCmd, default);
            Assert.True(replayRes.IsSuccess, replayRes.Error?.ToString());
            Assert.Equal(claimId, replayRes.Value);
        }

        // Test B: Replay where second line specifies different SaleItem fails due to no row reuse
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateWarrantyClaimHandler>();
            var nonMatchingSaleItemCmd = initialCmd with
            {
                Items = [
                    new WarrantyClaimItemInput(product.ProductId, 1m, "Cracked bezel", saleItemId1, null),
                    new WarrantyClaimItemInput(product.ProductId, 1m, "Cracked bezel", Guid.NewGuid(), null)
                ]
            };

            var mismatchRes = await handler.HandleAsync(nonMatchingSaleItemCmd, default);
            Assert.False(mismatchRes.IsSuccess);
            Assert.Equal("idempotency.payload_mismatch", mismatchRes.Error?.Code);
        }
    }
}
