using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Catalog;
using EdgeRetails.Application.Features.Parties;
using EdgeRetails.Application.Features.Warranty;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Application.Gateways;
using EdgeRetails.Application.Production.Outbox;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Operations;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.SystemConfiguration;
using EdgeRetails.Domain.Warranty;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EdgeRetails.IntegrationTests;

[Collection("Phase2PostgresIntegration")]
public sealed class Phase2PostgresCertificationTests
{
    [Fact]
    public async Task ReadApiKeysetCursors_ExecuteOnPostgresWithoutGaps()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var suffix = Guid.NewGuid().ToString("N");
        var customerAName = $"Cursor-{suffix}-Customer-A";
        var customerBName = $"Cursor-{suffix}-Customer-B";
        var supplierAName = $"Cursor-{suffix}-Supplier-A";
        var supplierBName = $"Cursor-{suffix}-Supplier-B";

        await using (var setupScope = provider.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            db.Customers.AddRange(
                new Customer { Name = customerAName, IsActive = true, CreatedAt = DateTimeOffset.UtcNow },
                new Customer { Name = customerBName, IsActive = true, CreatedAt = DateTimeOffset.UtcNow });
            db.Suppliers.AddRange(
                new Supplier { Name = supplierAName, IsActive = true, CreatedAt = DateTimeOffset.UtcNow },
                new Supplier { Name = supplierBName, IsActive = true, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();

            var productA = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db);
            var productB = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db);
            var products = await db.Products.Where(x => x.Id == productA.ProductId || x.Id == productB.ProductId).ToListAsync();
            products.Single(x => x.Id == productA.ProductId).Name = $"Cursor-{suffix}-Product-A";
            products.Single(x => x.Id == productB.ProductId).Name = $"Cursor-{suffix}-Product-B";
            await db.SaveChangesAsync();
        }

        await using var readScope = provider.CreateAsyncScope();
        var reads = readScope.ServiceProvider.GetRequiredService<IPartyDirectoryReadService>();
        var customersFirst = await reads.GetCustomersAsync($"Cursor-{suffix}-Customer", 1);
        Assert.Single(customersFirst);
        Assert.Equal(customerAName, customersFirst[0].Name);
        var customersSecond = await reads.GetCustomersAsync(
            $"Cursor-{suffix}-Customer",
            1,
            beforeName: customersFirst[0].Name,
            beforeCustomerId: customersFirst[0].CustomerId);
        Assert.Single(customersSecond);
        Assert.Equal(customerBName, customersSecond[0].Name);

        var suppliersFirst = await reads.GetSuppliersAsync($"Cursor-{suffix}-Supplier", 1);
        Assert.Single(suppliersFirst);
        Assert.Equal(supplierAName, suppliersFirst[0].Name);
        var suppliersSecond = await reads.GetSuppliersAsync(
            $"Cursor-{suffix}-Supplier",
            1,
            beforeName: suppliersFirst[0].Name,
            beforeSupplierId: suppliersFirst[0].SupplierId);
        Assert.Single(suppliersSecond);
        Assert.Equal(supplierBName, suppliersSecond[0].Name);

        var productsRead = readScope.ServiceProvider.GetRequiredService<IProductManagementReadService>();
        var productsFirst = await productsRead.GetProductsPageAsync(
            new ProductManagementPageQuery(IncludeInactive: true, Search: $"Cursor-{suffix}-Product", PageSize: 1),
            CancellationToken.None);
        Assert.Single(productsFirst);
        Assert.Equal($"Cursor-{suffix}-Product-A", productsFirst[0].Name);
        var productsSecond = await productsRead.GetProductsPageAsync(
            new ProductManagementPageQuery(
                IncludeInactive: true,
                Search: $"Cursor-{suffix}-Product",
                PageSize: 1,
                BeforeName: productsFirst[0].Name,
                BeforeProductId: productsFirst[0].ProductId),
            CancellationToken.None);
        Assert.Single(productsSecond);
        Assert.Equal($"Cursor-{suffix}-Product-B", productsSecond[0].Name);
    }

    [Fact]
    public async Task WarrantyMergedQueueCursor_IncludesWorkKindOnPostgres()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var suffix = Guid.NewGuid().ToString("N");
        var createdAt = DateTimeOffset.UtcNow;

        await using (var setupScope = provider.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var product = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db);
            var customer = await Phase2PostgresTestHarness.SeedCustomerAsync(db, $"WarrantyCursor-{suffix}");
            var claim = new WarrantyClaim
            {
                ClaimNumber = $"WC-{suffix}-CLAIM",
                CustomerId = customer.Id,
                ClientOperationId = Guid.NewGuid(),
                Status = WarrantyClaimStatus.Received,
                CurrentCustody = WarrantyCustody.WithShop,
                ReceivedAt = createdAt,
                CreatedBy = product.ActorId,
                CreatedAt = createdAt
            };
            var shopCase = new ShopStockWarrantyCase
            {
                CaseNumber = $"WC-{suffix}-SHOP",
                ProductId = product.ProductId,
                BaseQuantity = 1m,
                SupplierId = product.SupplierId,
                FaultDescription = "Pagination cursor certification",
                Status = ShopWarrantyCaseStatus.Open,
                CreatedAt = createdAt,
                CreatedBy = product.ActorId
            };
            db.WarrantyClaims.Add(claim);
            db.ShopStockWarrantyCases.Add(shopCase);
            await db.SaveChangesAsync();
        }

        await using var readScope = provider.CreateAsyncScope();
        var reads = readScope.ServiceProvider.GetRequiredService<IWarrantyReadService>();
        var firstPage = await reads.GetDashboardAsync($"WC-{suffix}", 1);
        var first = Assert.Single(firstPage.Rows);
        Assert.Equal(WarrantyWorkKind.ShopStock, first.Kind);

        var secondPage = await reads.GetDashboardAsync(
            $"WC-{suffix}",
            1,
            first.CreatedAt,
            first.WorkId,
            beforeWorkKind: first.Kind);
        var second = Assert.Single(secondPage.Rows);
        Assert.Equal(WarrantyWorkKind.CustomerClaim, second.Kind);
        Assert.NotEqual(first.WorkId, second.WorkId);
    }

    // =========================================================================
    // GROUP 1: OPERATION OUTCOME REAL POSTGRESQL CONCURRENCY & PERSISTENCE
    // =========================================================================

    [Fact]
    public async Task OperationOutcome_UniqueClientOperationId_Race_PersistsSingleRowAndRecovers()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var setupScope = provider.CreateAsyncScope();
        var setupDb = setupScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var actorId = await IntegrationIdentitySeeder.CreateActorAsync(setupDb);

        var clientOpId = Guid.CreateVersion7();
        var payloadFingerprint = OperationPayloadFingerprint.ComputeSha256("{\"test\":\"payload\"}");
        var entityId = Guid.NewGuid();
        var barrier = new Barrier(2);

        var task1 = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            var ledger = scope.ServiceProvider.GetRequiredService<IOperationOutcomeLedger>();
            barrier.SignalAndWait();
            await ledger.RecordSuccessAsync(
                clientOpId,
                "TestOperation",
                entityId,
                "DOC-001",
                actorId: actorId,
                payloadFingerprint: payloadFingerprint);
        });

        var task2 = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            var ledger = scope.ServiceProvider.GetRequiredService<IOperationOutcomeLedger>();
            barrier.SignalAndWait();
            await ledger.RecordSuccessAsync(
                clientOpId,
                "TestOperation",
                entityId,
                "DOC-001",
                actorId: actorId,
                payloadFingerprint: payloadFingerprint);
        });

        await Task.WhenAll(task1, task2);

        // Verify exactly one row in PostgreSQL system.operation_outcomes
        await using var verifyScope = provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var outcomes = await verifyDb.OperationOutcomes
            .Where(x => x.ClientOperationId == clientOpId)
            .ToListAsync();

        Assert.Single(outcomes);
        Assert.Equal(OperationOutcomeStatus.Succeeded, outcomes[0].Status);
        Assert.Equal(payloadFingerprint, outcomes[0].PayloadFingerprint);
        Assert.Equal(entityId, outcomes[0].ResultEntityId);
        Assert.Equal("DOC-001", outcomes[0].DocumentNumber);
    }

    [Fact]
    public async Task OperationOutcome_PayloadMismatch_AfterRestart_FailsClosedInPostgres()
    {
        var clientOpId = Guid.CreateVersion7();
        var originalFingerprint = OperationPayloadFingerprint.ComputeSha256("{\"order\":1}");
        var differentFingerprint = OperationPayloadFingerprint.ComputeSha256("{\"order\":2}");
        var entityId = Guid.NewGuid();

        // Scope 1: Record original outcome
        {
            await using var provider1 = Phase2PostgresTestHarness.BuildProvider();
            await using var scope1 = provider1.CreateAsyncScope();
            var db1 = scope1.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var actorId = await IntegrationIdentitySeeder.CreateActorAsync(db1);
            var ledger1 = scope1.ServiceProvider.GetRequiredService<IOperationOutcomeLedger>();

            await ledger1.RecordSuccessAsync(
                clientOpId,
                "TestOrder",
                entityId,
                "ORD-101",
                actorId: actorId,
                payloadFingerprint: originalFingerprint);
        }

        // Scope 2: Fresh provider simulating process restart - query with different payload
        {
            await using var provider2 = Phase2PostgresTestHarness.BuildProvider();
            await using var scope2 = provider2.CreateAsyncScope();
            var handler2 = ActivatorUtilities.CreateInstance<OperationStatusQueryHandler>(scope2.ServiceProvider);

            var query = new OperationStatusQuery(
                clientOpId,
                PayloadFingerprint: differentFingerprint);

            var result = await handler2.HandleAsync(query, CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.Equal("idempotency.payload_mismatch", result.Error?.Code);
        }
    }

    [Fact]
    public async Task OperationOutcome_CanonicalRecovery_AcrossDisposedContexts_RecoversCommittedOutcome()
    {
        var clientOpId = Guid.CreateVersion7();
        var payloadFingerprint = OperationPayloadFingerprint.ComputeSha256("{\"data\":\"recovery-test\"}");
        var entityId = Guid.NewGuid();

        // Scope 1: Record outcome
        {
            await using var provider1 = Phase2PostgresTestHarness.BuildProvider();
            await using var scope1 = provider1.CreateAsyncScope();
            var db1 = scope1.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var actorId = await IntegrationIdentitySeeder.CreateActorAsync(db1);
            var ledger1 = scope1.ServiceProvider.GetRequiredService<IOperationOutcomeLedger>();

            await ledger1.RecordSuccessAsync(
                clientOpId,
                "Sale",
                entityId,
                "INV-9999",
                actorId: actorId,
                payloadFingerprint: payloadFingerprint);
        }

        // Scope 2: Fresh provider - recovers original outcome
        {
            await using var provider2 = Phase2PostgresTestHarness.BuildProvider();
            await using var scope2 = provider2.CreateAsyncScope();
            var handler2 = ActivatorUtilities.CreateInstance<OperationStatusQueryHandler>(scope2.ServiceProvider);

            var query = new OperationStatusQuery(
                clientOpId,
                PayloadFingerprint: payloadFingerprint);

            var result = await handler2.HandleAsync(query, CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.True(result.Value!.Found);
            Assert.True(result.Value.WasCommitted);
            Assert.Equal("Sale", result.Value.OperationType);
            Assert.Equal(entityId, result.Value.EntityId);
            Assert.Equal("INV-9999", result.Value.DocumentNumber);
            Assert.Equal("Succeeded", result.Value.EffectiveStatus);
        }
    }

    [Fact]
    public async Task OperationOutcome_SuccessIsTerminal_AgainstLateFailureAndPendingWrites_InPostgres()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var ledger = scope.ServiceProvider.GetRequiredService<IOperationOutcomeLedger>();
        var clientOpId = Guid.CreateVersion7();
        var entityId = Guid.CreateVersion7();

        await ledger.RecordSuccessAsync(clientOpId, "Sale", entityId, "INV-TERMINAL-PG");
        await ledger.RecordFailureAsync(clientOpId, "Sale", "sale.failed", "A stale request failed later.");
        await ledger.RecordPendingAsync(clientOpId, "Sale");
        await ledger.RecordOutcomeUnknownAsync(clientOpId, "Sale");

        var outcome = await ledger.GetOutcomeAsync(clientOpId);
        Assert.NotNull(outcome);
        Assert.Equal(OperationOutcomeState.Succeeded, outcome.State);
        Assert.True(outcome.WasCommitted);
        Assert.Equal(entityId, outcome.EntityId);
        Assert.Equal("INV-TERMINAL-PG", outcome.DocumentNumber);
    }

    // =========================================================================
    // GROUP 2: PHYSICAL RECEIVING REAL POSTGRESQL CONCURRENCY & REPLAY
    // =========================================================================

    [Fact]
    public async Task PhysicalReceiving_ConcurrentSameClientOperationId_AllowsExactlyOneIntakeAndNoDuplicateUnits()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var setupScope = provider.CreateAsyncScope();
        var setupDb = setupScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(setupDb);
        var fixture = await Phase2PostgresTestHarness.SeedSerializedProductAsync(setupDb);

        var clientOpId = Guid.CreateVersion7();
        var serials = new[]
        {
            "SN-RACE-A-" + Guid.NewGuid().ToString("N")[..8],
            "SN-RACE-B-" + Guid.NewGuid().ToString("N")[..8]
        };

        // 1. Create a purchase without immediate receiving
        var purchaseHandler = ActivatorUtilities.CreateInstance<CreatePurchaseHandler>(setupScope.ServiceProvider);
        var purchaseResult = await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                fixture.SupplierId,
                "INV-RECV-RACE-" + Guid.NewGuid().ToString("N")[..8],
                DateOnly.FromDateTime(DateTime.UtcNow),
                null,
                0m,
                PurchaseSettlementMode.External,
                fixture.ActorId,
                Guid.CreateVersion7(),
                [new CreatePurchaseLineInput(fixture.ProductId, fixture.ProductUnitId, 2m, 2000m, 4000m, serials.Select(s => new SerializedIdentityInput(s)).ToList())],
                InitialPaymentAmount: 0m,
                ReceiveStockImmediately: false),
            CancellationToken.None);

        Assert.True(purchaseResult.IsSuccess, purchaseResult.Error?.Message);
        var purchaseId = purchaseResult.Value!.PurchaseId;

        var intakeCommand = new ReceiveProductIntakeCommand(
            purchaseId,
            fixture.ProductId,
            fixture.ProductUnitId,
            2m,
            2000m,
            serials.Select(s => new SerializedIdentityInput(s)).ToList(),
            fixture.ActorId,
            clientOpId,
            "Physical receiving race test");

        var barrier = new Barrier(2);

        var task1 = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            var handler = ActivatorUtilities.CreateInstance<ReceiveProductIntakeHandler>(scope.ServiceProvider);
            barrier.SignalAndWait();
            return await handler.HandleAsync(intakeCommand, CancellationToken.None);
        });

        var task2 = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            var handler = ActivatorUtilities.CreateInstance<ReceiveProductIntakeHandler>(scope.ServiceProvider);
            barrier.SignalAndWait();
            return await handler.HandleAsync(intakeCommand, CancellationToken.None);
        });

        var results = await Task.WhenAll(task1, task2);

        // Both callers must succeed (one created, other recovered)
        Assert.True(results[0].IsSuccess, results[0].Error?.Message);
        Assert.True(results[1].IsSuccess, results[1].Error?.Message);

        var units1 = results[0].Value!.CommittedUnits;
        var units2 = results[1].Value!.CommittedUnits;
        Assert.Equal(2, units1.Count);
        Assert.Equal(2, units2.Count);

        // The returned unit IDs must be identical
        Assert.Equal(units1.Select(u => u.Id).OrderBy(x => x), units2.Select(u => u.Id).OrderBy(x => x));

        // Verify in PostgreSQL: exactly 2 units, 1 movement, exactly 2 balance
        await using var verifyScope = provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        var dbUnits = await verifyDb.InventoryUnits
            .Where(x => x.ProductId == fixture.ProductId)
            .ToListAsync();
        Assert.Equal(2, dbUnits.Count);

        var movements = await verifyDb.InventoryMovements
            .Where(x => x.ProductId == fixture.ProductId && x.MovementType == InventoryMovementType.PurchaseIn)
            .ToListAsync();
        Assert.Single(movements);

        var balance = await verifyDb.StockBalances.SingleAsync(x => x.ProductId == fixture.ProductId);
        Assert.Equal(2m, balance.SellableQty);

        // Verify SupplierProduct.NextItemSequence advanced by exactly 2
        var sp = await verifyDb.SupplierProducts.SingleAsync(x => x.SupplierId == fixture.SupplierId && x.ProductId == fixture.ProductId);
        Assert.Equal(3, sp.NextItemSequence); // started at 1, +2 units = 3
    }

    // =========================================================================
    // GROUP 3: STOCKTAKE REAL POSTGRESQL CONCURRENCY & REPLAY
    // =========================================================================

    [Fact]
    public async Task Stocktake_ConcurrentPost_AppliesReconciliationExactlyOnce_NoDoubleAdjustment()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var setupScope = provider.CreateAsyncScope();
        var setupDb = setupScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(setupDb);
        var fixture = await Phase2PostgresTestHarness.SeedQuantityProductAsync(setupDb, defaultSalePrice: 100m);

        // 1. Initial purchase of 10 units
        var purchaseHandler = ActivatorUtilities.CreateInstance<CreatePurchaseHandler>(setupScope.ServiceProvider);
        await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                fixture.SupplierId,
                "INV-STK-RACE-" + Guid.NewGuid().ToString("N")[..8],
                DateOnly.FromDateTime(DateTime.UtcNow),
                null,
                0m,
                PurchaseSettlementMode.External,
                fixture.ActorId,
                Guid.CreateVersion7(),
                [new CreatePurchaseLineInput(fixture.ProductId, fixture.ProductUnitId, 10m, 50m, 100m, [])],
                InitialPaymentAmount: 0m),
            CancellationToken.None);

        setupDb.ChangeTracker.Clear();
        var initStock = await setupDb.StockBalances.SingleAsync(x => x.ProductId == fixture.ProductId);
        Assert.Equal(10m, initStock.SellableQty);

        // 2. Create category, associate product, start, count, and review stocktake: Counted = 8 (Shortage of 2)
        var category = new Category
        {
            Id = Guid.NewGuid(),
            Name = "Category-" + Guid.NewGuid().ToString("N")[..8],
            IdentitySymbol = ("CAT" + Random.Shared.Next(1000, 9999)).ToUpperInvariant()
        };
        setupDb.Categories.Add(category);
        var productToUpdate = await setupDb.Products.SingleAsync(x => x.Id == fixture.ProductId);
        productToUpdate.CategoryId = category.Id;
        await setupDb.SaveChangesAsync();

        var stocktake = new Stocktake
        {
            Id = Guid.CreateVersion7(),
            Scope = StocktakeScope.Category,
            CategoryId = category.Id,
            Status = StocktakeStatus.Draft,
            CreatedBy = fixture.ActorId,
            CreatedAt = DateTimeOffset.UtcNow
        };
        setupDb.Stocktakes.Add(stocktake);
        await setupDb.SaveChangesAsync();

        var startHandler = ActivatorUtilities.CreateInstance<StartStocktakeHandler>(setupScope.ServiceProvider);
        var startResult = await startHandler.HandleAsync(new StartStocktakeCommand(stocktake.Id), CancellationToken.None);
        Assert.True(startResult.IsSuccess, startResult.Error?.Message);

        var countHandler = ActivatorUtilities.CreateInstance<RecordStocktakeCountHandler>(setupScope.ServiceProvider);
        var countResult = await countHandler.HandleAsync(
            new RecordStocktakeCountCommand(stocktake.Id, fixture.ProductId, 8m, fixture.ActorId, "Count 8"),
            CancellationToken.None);
        Assert.True(countResult.IsSuccess, countResult.Error?.Message);

        var reviewHandler = ActivatorUtilities.CreateInstance<ReviewStocktakeHandler>(setupScope.ServiceProvider);
        var reviewResult = await reviewHandler.HandleAsync(new ReviewStocktakeCommand(stocktake.Id), CancellationToken.None);
        Assert.True(reviewResult.IsSuccess, reviewResult.Error?.Message);

        // 3. Post concurrently with same ClientOperationId
        var clientOpId = Guid.CreateVersion7();
        var postCommand = new PostStocktakeCommand(stocktake.Id, fixture.ActorId, null, clientOpId);
        var barrier = new Barrier(2);

        var task1 = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            var handler = ActivatorUtilities.CreateInstance<PostStocktakeHandler>(scope.ServiceProvider);
            barrier.SignalAndWait();
            return await handler.HandleAsync(postCommand, CancellationToken.None);
        });

        var task2 = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            var handler = ActivatorUtilities.CreateInstance<PostStocktakeHandler>(scope.ServiceProvider);
            barrier.SignalAndWait();
            return await handler.HandleAsync(postCommand, CancellationToken.None);
        });

        var results = await Task.WhenAll(task1, task2);

        // Both must report success (one executed, one replayed idempotently)
        Assert.True(results[0].IsSuccess, results[0].Error?.Message);
        Assert.True(results[1].IsSuccess, results[1].Error?.Message);

        // 4. Verify in PostgreSQL: sellable quantity is 8 (NOT 6 from double subtraction!)
        await using var verifyScope = provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        var finalStock = await verifyDb.StockBalances.SingleAsync(x => x.ProductId == fixture.ProductId);
        Assert.Equal(8m, finalStock.SellableQty);

        // Exactly one PhysicalCountCorrection movement
        var countCorrections = await verifyDb.InventoryMovements
            .Where(x => x.ProductId == fixture.ProductId && x.MovementType == InventoryMovementType.PhysicalCountCorrection)
            .ToListAsync();
        Assert.Single(countCorrections);
    }

    // =========================================================================
    // GROUP 4: OUTBOX REAL POSTGRESQL CONCURRENCY & LEASING
    // =========================================================================

    [Fact]
    public async Task Outbox_ConcurrentWorkers_ClaimBatch_OnlyOneClaimsMessage()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var setupScope = provider.CreateAsyncScope();
        var setupDb = setupScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var outboxRepo = setupScope.ServiceProvider.GetRequiredService<IOutboxRepository>();

        var msgId = Guid.NewGuid();
        var msg = new OutboxMessage
        {
            Id = msgId,
            EffectType = "TestConcurrencyEffect",
            SourceType = "Test",
            SourceId = Guid.NewGuid().ToString(),
            PayloadJson = "{}",
            IdempotencyKey = "outbox_race_" + Guid.NewGuid().ToString("N"),
            Status = OutboxMessageStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            AttemptCount = 0
        };

        outboxRepo.Enqueue(msg);
        await setupDb.SaveChangesAsync();

        var barrier = new Barrier(2);

        var task1 = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();
            barrier.SignalAndWait();
            return await repo.TryClaimMessageAsync(msgId, "Worker-1", TimeSpan.FromMinutes(2));
        });

        var task2 = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();
            barrier.SignalAndWait();
            return await repo.TryClaimMessageAsync(msgId, "Worker-2", TimeSpan.FromMinutes(2));
        });

        var results = await Task.WhenAll(task1, task2);

        // Exactly one worker must win the claim
        var claimCount = results.Count(r => r);
        Assert.Equal(1, claimCount);

        // In PostgreSQL, status is Processing and LeaseExpiresAt is in future
        await using var verifyScope = provider.CreateAsyncScope();
        var verifyRepo = verifyScope.ServiceProvider.GetRequiredService<IOutboxRepository>();
        var inDb = await verifyRepo.GetByIdAsync(msgId);
        Assert.NotNull(inDb);
        Assert.Equal(OutboxMessageStatus.Processing, inDb.Status);
        Assert.NotNull(inDb.NextAttemptAt);
        Assert.True(inDb.NextAttemptAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Outbox_ExpiredLease_ReclaimedByAnotherWorker_InPostgres()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var setupScope = provider.CreateAsyncScope();
        var setupDb = setupScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var outboxRepo = setupScope.ServiceProvider.GetRequiredService<IOutboxRepository>();

        var msgId = Guid.NewGuid();
        // Create a message in Processing state whose lease has EXPIRED in the past
        var msg = new OutboxMessage
        {
            Id = msgId,
            EffectType = "TestCrashRecoveryEffect",
            SourceType = "Test",
            SourceId = Guid.NewGuid().ToString(),
            PayloadJson = "{}",
            IdempotencyKey = "outbox_crash_" + Guid.NewGuid().ToString("N"),
            Status = OutboxMessageStatus.Processing,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-5), // Expired!
            AttemptCount = 1
        };

        outboxRepo.Enqueue(msg);
        await setupDb.SaveChangesAsync();

        // Worker B claims the expired message
        await using var workerScope = provider.CreateAsyncScope();
        var workerRepo = workerScope.ServiceProvider.GetRequiredService<IOutboxRepository>();
        var claimed = await workerRepo.TryClaimMessageAsync(msgId, "Worker-Recovery", TimeSpan.FromMinutes(5));

        Assert.True(claimed, "Worker should successfully claim the message with an expired lease.");

        var afterClaim = await workerRepo.GetByIdAsync(msgId);
        Assert.NotNull(afterClaim);
        Assert.Equal(OutboxMessageStatus.Processing, afterClaim.Status);
        Assert.True(afterClaim.NextAttemptAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Outbox_ExpiredWorkerCannotSettleReclaimedLease_InPostgres()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var messageId = Guid.NewGuid();
        var oldLeaseToken = Guid.NewGuid();
        var newLeaseToken = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using (var setupScope = provider.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var repository = setupScope.ServiceProvider.GetRequiredService<IOutboxRepository>();
            repository.Enqueue(new OutboxMessage
            {
                Id = messageId,
                EffectType = "FencedLeaseEffect",
                SourceType = "Test",
                SourceId = messageId.ToString("N"),
                PayloadJson = "{}",
                IdempotencyKey = "fenced_lease_" + Guid.NewGuid().ToString("N"),
                Status = OutboxMessageStatus.Processing,
                LeaseOwner = "Worker-A",
                LeaseToken = oldLeaseToken,
                CreatedAt = now.AddMinutes(-5),
                NextAttemptAt = now.AddSeconds(-1),
                AttemptCount = 1
            });
            await db.SaveChangesAsync();
        }

        await using (var workerScope = provider.CreateAsyncScope())
        {
            var repository = workerScope.ServiceProvider.GetRequiredService<IOutboxRepository>();
            Assert.True(await repository.TryClaimMessageAsync(
                messageId,
                "Worker-B",
                newLeaseToken,
                TimeSpan.FromMinutes(2)));
        }

        await using (var staleWorkerScope = provider.CreateAsyncScope())
        {
            var repository = staleWorkerScope.ServiceProvider.GetRequiredService<IOutboxRepository>();
            await repository.MarkFailedAsync(messageId, "stale failure", DateTimeOffset.UtcNow, oldLeaseToken);
            await repository.MarkCompletedAsync(messageId, DateTimeOffset.UtcNow, oldLeaseToken);
        }

        await using (var verifyScope = provider.CreateAsyncScope())
        {
            var repository = verifyScope.ServiceProvider.GetRequiredService<IOutboxRepository>();
            var message = await repository.GetByIdAsync(messageId);
            Assert.NotNull(message);
            Assert.Equal(OutboxMessageStatus.Processing, message.Status);
            Assert.Equal("Worker-B", message.LeaseOwner);
            Assert.Equal(newLeaseToken, message.LeaseToken);
            Assert.Null(message.CompletedAt);
            Assert.Equal(1, message.AttemptCount);
        }

        await using (var currentWorkerScope = provider.CreateAsyncScope())
        {
            var repository = currentWorkerScope.ServiceProvider.GetRequiredService<IOutboxRepository>();
            await repository.MarkCompletedAsync(messageId, DateTimeOffset.UtcNow, newLeaseToken);
            var message = await repository.GetByIdAsync(messageId);
            Assert.NotNull(message);
            Assert.Equal(OutboxMessageStatus.Completed, message.Status);
            Assert.Null(message.LeaseToken);
            Assert.Null(message.LeaseOwner);
        }
    }
}
