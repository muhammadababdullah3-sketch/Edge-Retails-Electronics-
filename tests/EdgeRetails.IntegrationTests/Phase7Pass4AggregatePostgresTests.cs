using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Catalog;
using EdgeRetails.Application.Features.Parties;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Operations;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace EdgeRetails.IntegrationTests;

[Collection("Phase2PostgresIntegration")]
public sealed class Phase7Pass4AggregatePostgresTests
{
    // NEW_COVERAGE. The shared harness attests the isolated loopback data directory;
    // the coordinated owned runner is responsible for cluster/database cleanup.
    [Fact]
    public async Task G14_LegacyAttributesRemainEditableAndPersistThroughAggregate()
    {
        await using var fixture = await Fixture.CreateAsync();
        var command = await fixture.CreateCommandAsync();
        command = command with { Product = command.Product with { AttributesSchemaVersion = 1, AttributesJson = "{\"legacy_vendor_spec\":\"text\",\"wattage\":\"old description\"}" } };
        var created = await fixture.SaveAsync(command);
        Assert.True(created.IsSuccess, created.Error?.Message);
        var updated = await fixture.SaveAsync(command with { ProductId = created.Value!.ProductId, ExpectedVersion = created.Value.Version, ClientOperationId = Guid.NewGuid(), Product = command.Product with { Name = "Edited legacy product" } });
        Assert.True(updated.IsSuccess, updated.Error?.Message);
        await fixture.InspectAsync(async db =>
        {
            var product = await db.Products.SingleAsync(x => x.Id == created.Value.ProductId);
            Assert.Equal("Edited legacy product", product.Name);
            Assert.Equal(1, product.AttributesSchemaVersion);
            Assert.Equal(command.Product.AttributesJson, product.AttributesJson);
            Assert.Single(await db.ProductUnits.Where(x => x.ProductId == product.Id).ToListAsync());
            Assert.Single(await db.SupplierProducts.Where(x => x.ProductId == product.Id).ToListAsync());
        });
    }
    [Theory]
    [InlineData("units")]
    [InlineData("supplier")]
    [InlineData("savedThenThrow")]
    [InlineData("outcome")]
    public async Task G01_CreateFailureRollsBackProductMappingsLinksAndOutcome(string failure)
    {
        await using var fixture = await Fixture.CreateAsync();
        var command = await fixture.CreateCommandAsync();
        if (failure == "units")
        {
            command = command with { Units = [command.Units[0] with { FactorToBaseUnit = 2m }] };
        }

        if (failure == "supplier")
        {
            command = command with { LinkedSupplierIds = [fixture.Seed.SupplierId, Guid.NewGuid()] };
        }

        if (failure is "savedThenThrow" or "outcome")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.SaveAsync(command, failure));
        }
        else
        {
            var result = await fixture.SaveAsync(command);
            Assert.False(result.IsSuccess);
            Assert.Equal(failure == "units" ? "catalog.base_unit_required" : "catalog.supplier_unavailable", result.Error?.Code);
        }
        await fixture.InspectAsync(async db =>
        {
            Assert.False(await db.Products.AnyAsync(x => x.Sku == command.Product.Sku));
            Assert.Equal(1, await db.ProductUnits.CountAsync(x => x.UnitId == fixture.Seed.UnitId));
            Assert.False(await db.OperationOutcomes.AnyAsync(x => x.ClientOperationId == command.ClientOperationId));
            Assert.Equal(0, await db.SupplierProducts.CountAsync(x => x.SupplierId == fixture.Seed.SupplierId));
        });
    }

    [Fact]
    public async Task G01_UpdateFailurePreservesPriorProductUnitAndSupplierLinkState()
    {
        await using var fixture = await Fixture.CreateAsync();
        var createdCommand = await fixture.CreateCommandAsync();
        var created = await fixture.SaveAsync(createdCommand);
        Assert.True(created.IsSuccess, created.Error?.Message);
        Guid unitId = default, linkId = default;
        long linkVersion = 0;
        await fixture.InspectAsync(async db =>
        {
            unitId = (await db.ProductUnits.SingleAsync(x => x.ProductId == created.Value!.ProductId)).Id;
            var link = await db.SupplierProducts.SingleAsync(x => x.ProductId == created.Value!.ProductId);
            linkId = link.Id;
            link.NextItemSequence = 42;
            linkVersion = link.Version;
            await db.SaveChangesAsync();
        });
        var update = createdCommand with
        {
            ProductId = created.Value!.ProductId, ExpectedVersion = created.Value.Version, ClientOperationId = Guid.NewGuid(),
            Product = createdCommand.Product with { Name = "Attempted replacement", DefaultSalePrice = 999m },
            Units = [createdCommand.Units[0] with { CanSell = false, IsDefaultSaleUnit = false }],
            LinkedSupplierIds = []
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.SaveAsync(update, "savedThenThrow"));
        await fixture.InspectAsync(async db =>
        {
            var product = await db.Products.SingleAsync(x => x.Id == created.Value.ProductId);
            Assert.Equal(createdCommand.Product.Name, product.Name);
            Assert.Equal(createdCommand.Product.DefaultSalePrice, product.DefaultSalePrice);
            Assert.Equal(created.Value.Version, product.Version);
            var unit = await db.ProductUnits.SingleAsync(x => x.Id == unitId);
            Assert.True(unit.CanSell);
            Assert.True(unit.IsDefaultSaleUnit);
            var link = await db.SupplierProducts.SingleAsync(x => x.Id == linkId);
            Assert.True(link.IsActive);
            Assert.Equal(linkVersion, link.Version);
            Assert.Equal(42, link.NextItemSequence);
            Assert.False(await db.OperationOutcomes.AnyAsync(x => x.ClientOperationId == update.ClientOperationId));
        });
    }

    [Fact]
    public async Task G01_LostResponseReplayAndConcurrentSameIdHaveOnePersistedBusinessEffect()
    {
        await using var fixture = await Fixture.CreateAsync();
        var command = await fixture.CreateCommandAsync();
        var concurrent = await ProveRaceAsync<ProductMutationResult>(
            (reached, release) => fixture.SaveRaceAsync(command, reached, release),
            pid => fixture.SaveAsync(command, pid: pid));
        Assert.All(concurrent, x => Assert.True(x.IsSuccess, x.Error?.Message));
        Assert.Equal(concurrent[0].Value!.ProductId, concurrent[1].Value!.ProductId);
        // Discard both successful responses: a new scoped request must recover from the durable outcome.
        var replay = await fixture.SaveAsync(command);
        Assert.True(replay.IsSuccess, replay.Error?.Message);
        Assert.Equal(concurrent[0].Value!.ProductId, replay.Value!.ProductId);
        Assert.Equal(command.Product.Sku, replay.Value.Sku);
        var mismatch = await fixture.SaveAsync(command with { Product = command.Product with { ReferencePurchaseCost = 73m } });
        Assert.Equal("idempotency.payload_mismatch", mismatch.Error?.Code);
        await fixture.InspectAsync(async db =>
        {
            var product = Assert.Single(await db.Products.Where(x => x.Sku == command.Product.Sku).ToListAsync());
            Assert.Equal(replay.Value.ProductId, product.Id);
            var mapping = Assert.Single(await db.ProductUnits.Where(x => x.ProductId == product.Id).ToListAsync());
            Assert.Equal(1m, mapping.FactorToBaseUnit);
            var pair = Assert.Single(await db.SupplierProducts.Where(x => x.ProductId == product.Id).ToListAsync());
            Assert.Equal(1, pair.Version);
            Assert.Equal(1, pair.NextItemSequence);
            var outcome = Assert.Single(await db.OperationOutcomes.Where(x => x.ClientOperationId == command.ClientOperationId).ToListAsync());
            Assert.Equal(OperationOutcomeStatus.Succeeded, outcome.Status);
            Assert.True(outcome.WasCommitted);
            Assert.Equal(product.Id, outcome.ResultEntityId);
            Assert.Equal(ProductAggregatePayloadFingerprint.Compute(command), outcome.PayloadFingerprint);
        });
    }

    [Fact]
    public async Task G01_ConcurrentBusinessUseCannotReadAnUncommittedPartialAggregate()
    {
        await using var fixture = await Fixture.CreateAsync();
        var command = await fixture.CreateCommandAsync();
        var staged = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var save = fixture.SavePausedAsync(command, staged, release);
        var productId = await staged.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            await fixture.InspectAsync(async db =>
            {
                Assert.False(await db.Products.AnyAsync(x => x.Id == productId));
                Assert.False(await db.ProductUnits.AnyAsync(x => x.ProductId == productId));
                Assert.False(await db.SupplierProducts.AnyAsync(x => x.ProductId == productId));
            });
            var attempt = await fixture.OrderAsync(productId, Guid.NewGuid(), immediate: false);
            Assert.False(attempt.IsSuccess);
        }
        finally { release.TrySetResult(); }
        var committed = await save.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(committed.IsSuccess, committed.Error?.Message);
        Guid productUnitId = default;
        await fixture.InspectAsync(async db => productUnitId = (await db.ProductUnits.SingleAsync(x => x.ProductId == productId)).Id);
        var completedOrder = await fixture.OrderAsync(productId, productUnitId, immediate: false);
        Assert.True(completedOrder.IsSuccess, completedOrder.Error?.Message);
    }

    [Fact]
    public async Task G06_ManualManualFirstLinkRaceCommitsExactlyOnePair()
    {
        await using var fixture = await Fixture.CreateAsync();
        var results = await ProveRaceAsync<Guid>(
            (reached, release) => fixture.LinkAsync(true, reached: reached, release: release),
            pid => fixture.LinkAsync(true, pid: pid));
        Assert.All(results, x => Assert.True(x.IsSuccess, x.Error?.Message));
        Assert.Equal(results[0].Value, results[1].Value);
        await fixture.InspectAsync(async db =>
        {
            var pair = Assert.Single(await db.SupplierProducts.Where(x => x.ProductId == fixture.Seed.ProductId).ToListAsync());
            Assert.Equal(results[0].Value, pair.Id);
            Assert.Equal(2, pair.Version);
            Assert.Equal(1, pair.NextItemSequence);
        });
    }

    [Fact]
    public async Task G06_ManualPhysicalReceiptFirstLinkRaceHasOnePairAndPreservesCursor()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.InspectAsync(async db =>
        {
            var product = await db.Products.SingleAsync(x => x.Id == fixture.Seed.ProductId);
            product.TrackingMode = TrackingMode.IndividualPiece;
            await db.SaveChangesAsync();
            await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        });
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pid = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var manual = fixture.LinkAsync(true, reached: reached, release: release);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var receipt = fixture.OrderAsync(fixture.Seed.ProductId, fixture.Seed.ProductUnitId, true, pid);
        try { await AssertLockWaitAsync(await pid.Task.WaitAsync(TimeSpan.FromSeconds(10))); }
        finally { release.TrySetResult(); }
        var manualResult = await manual.WaitAsync(TimeSpan.FromSeconds(20));
        var receiptResult = await receipt.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(manualResult.IsSuccess, manualResult.Error?.Message);
        Assert.True(receiptResult.IsSuccess, receiptResult.Error?.Message);
        Guid pairId = default;
        long cursor = 0, version = 0;
        await fixture.InspectAsync(async db =>
        {
            var pair = Assert.Single(await db.SupplierProducts.Where(x => x.ProductId == fixture.Seed.ProductId).ToListAsync());
            pairId = pair.Id; cursor = pair.NextItemSequence; version = pair.Version;
            Assert.Equal(manualResult.Value, pair.Id);
            Assert.True(cursor >= 2);
            var unit = Assert.Single(await db.InventoryUnits.Where(x => x.ProductId == fixture.Seed.ProductId).ToListAsync());
            Assert.Equal(pair.Id, unit.SupplierProductId);
            Assert.Equal(1, unit.ItemSequence);
        });
        var deactivated = await fixture.LinkAsync(false, version);
        Assert.True(deactivated.IsSuccess, deactivated.Error?.Message);
        var stale = await fixture.LinkAsync(true, version);
        Assert.Equal("concurrency.stale_supplier_product", stale.Error?.Code);
        var reactivated = await fixture.LinkAsync(true, version + 1);
        Assert.True(reactivated.IsSuccess, reactivated.Error?.Message);
        await fixture.InspectAsync(async db =>
        {
            var pair = await db.SupplierProducts.SingleAsync(x => x.ProductId == fixture.Seed.ProductId);
            Assert.Equal(pairId, pair.Id);
            Assert.Equal(cursor, pair.NextItemSequence);
            Assert.Equal(version + 2, pair.Version);
            Assert.True(pair.IsActive);
        });
    }

    [Theory]
    [InlineData("Abdullah", null, "AB")]
    [InlineData("عبداللہ", "UR", "UR")]
    [InlineData("A", "XY", "XY")]
    public async Task G13_PrefixPersistsReplaysRejectsChangedIntentAndCodeSurvivesRename(string name, string? explicitPrefix, string expectedPrefix)
    {
        await using var fixture = await Fixture.CreateAsync();
        var command = new SaveSupplierCommand(null, name, null, "Lahore", null, true, fixture.Seed.ActorId,
            Guid.NewGuid(), ExplicitDealerPrefix: explicitPrefix, ClientOperationId: Guid.NewGuid());
        var results = await TogetherAsync(() => fixture.SupplierAsync(command), () => fixture.SupplierAsync(command));
        Assert.All(results, x => Assert.True(x.IsSuccess, x.Error?.Message));
        Assert.Equal(results[0].Value, results[1].Value);
        var replay = await fixture.SupplierAsync(command);
        Assert.Equal(results[0].Value, replay.Value);
        var mismatch = await fixture.SupplierAsync(command with { ExplicitDealerPrefix = "ZZ" });
        Assert.Equal("idempotency.payload_mismatch", mismatch.Error?.Code);
        string dealerCode = string.Empty;
        await fixture.InspectAsync(async db =>
        {
            var supplier = await db.Suppliers.SingleAsync(x => x.Id == replay.Value);
            dealerCode = supplier.DealerCode!;
            Assert.StartsWith(expectedPrefix, dealerCode);
            Assert.True(long.Parse(dealerCode[2..]) >= 1);
            Assert.Equal(name, supplier.Name);
            var outcome = Assert.Single(await db.OperationOutcomes.Where(x => x.ClientOperationId == command.ClientOperationId).ToListAsync());
            Assert.Equal(replay.Value, outcome.ResultEntityId);
            Assert.Equal(OperationOutcomeStatus.Succeeded, outcome.Status);
        });
        var renamed = await fixture.SupplierAsync(command with { SupplierId = replay.Value, Name = "Renamed supplier", ExplicitDealerPrefix = "QQ", ClientOperationId = Guid.NewGuid() });
        Assert.True(renamed.IsSuccess, renamed.Error?.Message);
        await fixture.InspectAsync(async db => Assert.Equal(dealerCode, (await db.Suppliers.SingleAsync(x => x.Id == replay.Value)).DealerCode));
    }

    private static async Task<Result<T>[]> ProveRaceAsync<T>(
        Func<TaskCompletionSource, TaskCompletionSource, Task<Result<T>>> winner,
        Func<TaskCompletionSource<int>, Task<Result<T>>> competitor)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pid = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = winner(reached, release);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var second = competitor(pid);
        try { await AssertLockWaitAsync(await pid.Task.WaitAsync(TimeSpan.FromSeconds(10))); }
        finally { release.TrySetResult(); }
        return await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(40));
    }

    private static async Task AssertLockWaitAsync(int pid)
    {
        await using var connection = new NpgsqlConnection(Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB"));
        await connection.OpenAsync();
        for (var i = 0; i < 100; i++)
        {
            await using var query = new NpgsqlCommand("SELECT wait_event_type FROM pg_stat_activity WHERE pid=@pid", connection);
            query.Parameters.AddWithValue("pid", pid);
            if (await query.ExecuteScalarAsync() is string value && value == "Lock") { return; }
            await Task.Delay(20);
        }
        Assert.Fail("Competing operation never demonstrated an actual PostgreSQL lock wait.");
    }
    private static async Task<T[]> TogetherAsync<T>(params Func<Task<T>>[] operations)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var tasks = operations.Select(async operation =>
        {
            if (Interlocked.Increment(ref started) == operations.Length) { gate.SetResult(); }
            await gate.Task;
            return await operation();
        }).ToArray();
        return await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(40));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public ServiceProvider Provider { get; private set; } = null!;
        public QuantityProductFixture Seed { get; private set; } = null!;
        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture { Provider = Phase2PostgresTestHarness.BuildProvider() };
            try
            {
                await fixture.InspectAsync(async db =>
                {
                    Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", db.Database.ProviderName);
                    await db.Database.OpenConnectionAsync();
                    Assert.StartsWith("18.", ((NpgsqlConnection)db.Database.GetDbConnection()).ServerVersion);
                    fixture.Seed = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);
                });
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }
        public async Task<SaveProductAggregateCommand> CreateCommandAsync()
        {
            var category = new Category { Name = "Pass4 aggregate " + Guid.NewGuid().ToString("N"), IdentitySymbol = "P4" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant(), IsActive = true };
            await InspectAsync(async db => { db.Categories.Add(category); await db.SaveChangesAsync(); });
            return new(Seed.ActorId, null, null,
                new ProductCatalogInput("Pass4 aggregate " + Guid.NewGuid().ToString("N"), "P4-" + Guid.NewGuid().ToString("N").ToUpperInvariant(),
                    CategoryId: category.Id, BaseUnitId: Seed.UnitId, ReferencePurchaseCost: 50m, DefaultSalePrice: 150m),
                [new ProductUnitInput(Seed.UnitId, 1m, true, true, true, true, true)], [Seed.SupplierId], Guid.NewGuid());
        }
        public async Task InspectAsync(Func<EdgeRetailsDbContext, Task> inspect)
        {
            await using var scope = Provider.CreateAsyncScope();
            await inspect(scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>());
        }
        public async Task<Result<ProductMutationResult>> SaveAsync(SaveProductAggregateCommand command, string? failure = null, TaskCompletionSource<int>? pid = null)
        {
            await using var scope = Provider.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var db = services.GetRequiredService<EdgeRetailsDbContext>();
            if (pid is not null) { await db.Database.OpenConnectionAsync(); pid.SetResult(((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID); }
            var handler = failure is null ? services.GetRequiredService<SaveProductAggregateHandler>() : ActivatorUtilities.CreateInstance<SaveProductAggregateHandler>(services,
                failure == "savedThenThrow" ? new SaveThenThrow(db) : new PassThroughSave(db),
                failure == "outcome" ? new FailingOutcome(services.GetRequiredService<IOperationOutcomeLedger>()) : services.GetRequiredService<IOperationOutcomeLedger>());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            return await handler.HandleAsync(command, timeout.Token);
        }
        public async Task<Result<ProductMutationResult>> SaveRaceAsync(SaveProductAggregateCommand command, TaskCompletionSource reached, TaskCompletionSource release)
        {
            await using var scope = Provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var handler = ActivatorUtilities.CreateInstance<SaveProductAggregateHandler>(scope.ServiceProvider, new PausedAnySave(db, reached, release));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            return await handler.HandleAsync(command, timeout.Token);
        }
        public async Task<Result<ProductMutationResult>> SavePausedAsync(SaveProductAggregateCommand command, TaskCompletionSource<Guid> staged, TaskCompletionSource release)
        {
            await using var scope = Provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var handler = ActivatorUtilities.CreateInstance<SaveProductAggregateHandler>(scope.ServiceProvider, new PausedSave(db, staged, release));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            return await handler.HandleAsync(command, timeout.Token);
        }
        public async Task<Result<Guid>> LinkAsync(bool active, long? expected = null, TaskCompletionSource? reached = null, TaskCompletionSource? release = null, TaskCompletionSource<int>? pid = null)
        {
            await using var scope = Provider.CreateAsyncScope();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            if (pid is not null) { await db.Database.OpenConnectionAsync(); pid.SetResult(((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID); }
            var handler = reached is null ? scope.ServiceProvider.GetRequiredService<SetSupplierProductActiveHandler>() :
                ActivatorUtilities.CreateInstance<SetSupplierProductActiveHandler>(scope.ServiceProvider, new PausedAnySave(db, reached, release!));
            return await handler.HandleAsync(
                new SetSupplierProductActiveCommand(Seed.ActorId, Seed.ProductId, Seed.SupplierId, active, expected), timeout.Token);
        }
        public async Task<Result<CreatePurchaseResult>> OrderAsync(Guid productId, Guid productUnitId, bool immediate, TaskCompletionSource<int>? pid = null)
        {
            await using var scope = Provider.CreateAsyncScope();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            if (pid is not null) { var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>(); await db.Database.OpenConnectionAsync(); pid.SetResult(((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID); }
            return await scope.ServiceProvider.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new CreatePurchaseCommand(
                Seed.SupplierId, "P4-" + Guid.NewGuid().ToString("N"), DateOnly.FromDateTime(DateTime.UtcNow), null, 0m,
                PurchaseSettlementMode.External, Seed.ActorId, Guid.NewGuid(),
                [new CreatePurchaseLineInput(productId, productUnitId, 1m, 50m, 150m, [])], ReceiveStockImmediately: immediate), timeout.Token);
        }
        public async Task<Result<Guid>> SupplierAsync(SaveSupplierCommand command)
        {
            await using var scope = Provider.CreateAsyncScope();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            return await scope.ServiceProvider.GetRequiredService<SaveSupplierHandler>().HandleAsync(command, timeout.Token);
        }
        public ValueTask DisposeAsync() => Provider.DisposeAsync();
    }

    private sealed class PassThroughSave(EdgeRetailsDbContext db) : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
    }
    private sealed class PausedAnySave(EdgeRetailsDbContext db, TaskCompletionSource reached, TaskCompletionSource release) : IUnitOfWork
    {
        public async Task<int> SaveChangesAsync(CancellationToken token)
        {
            var result = await db.SaveChangesAsync(token);
            reached.TrySetResult();
            await release.Task.WaitAsync(token);
            return result;
        }
    }
    private sealed class SaveThenThrow(EdgeRetailsDbContext db) : IUnitOfWork
    {
        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        {
            await db.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException("Injected failure after real aggregate rows were flushed inside the transaction.");
        }
    }
    private sealed class PausedSave(EdgeRetailsDbContext db, TaskCompletionSource<Guid> staged, TaskCompletionSource release) : IUnitOfWork
    {
        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        {
            var productId = db.ChangeTracker.Entries<Product>().Single(x => x.State == EntityState.Added).Entity.Id;
            var result = await db.SaveChangesAsync(cancellationToken);
            staged.SetResult(productId);
            await release.Task.WaitAsync(cancellationToken);
            return result;
        }
    }
    private sealed class FailingOutcome(IOperationOutcomeLedger inner) : IOperationOutcomeLedger
    {
        public Task<OperationOutcomeRecord?> GetOutcomeAsync(Guid id, CancellationToken cancellationToken = default) => inner.GetOutcomeAsync(id, cancellationToken);
        public Task RecordPendingAsync(Guid id, string type, Guid? actorId = null, Guid? terminalId = null, Guid? sessionId = null, string? payloadFingerprint = null, CancellationToken cancellationToken = default) => inner.RecordPendingAsync(id, type, actorId, terminalId, sessionId, payloadFingerprint, cancellationToken);
        public Task RecordSuccessAsync(Guid id, string type, Guid entityId, string? documentNumber = null, Guid? actorId = null, Guid? terminalId = null, Guid? sessionId = null, string? payloadFingerprint = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Injected outcome failure after real aggregate rows were flushed inside the transaction.");
        public Task RecordFailureAsync(Guid id, string type, string errorCode, string errorMessage, Guid? actorId = null, Guid? terminalId = null, Guid? sessionId = null, string? payloadFingerprint = null, CancellationToken cancellationToken = default) => inner.RecordFailureAsync(id, type, errorCode, errorMessage, actorId, terminalId, sessionId, payloadFingerprint, cancellationToken);
        public Task RecordOutcomeUnknownAsync(Guid id, string type, Guid? actorId = null, Guid? terminalId = null, Guid? sessionId = null, string? payloadFingerprint = null, CancellationToken cancellationToken = default) => inner.RecordOutcomeUnknownAsync(id, type, actorId, terminalId, sessionId, payloadFingerprint, cancellationToken);
    }
}
