using System.Diagnostics;
using System.Security.Cryptography;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Infrastructure;
using EdgeRetails.Infrastructure.Persistence;
using EdgeRetails.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace EdgeRetails.IntegrationTests;

[CollectionDefinition("MasterSupplierSequence", DisableParallelization = true)]
public sealed class MasterSupplierSequenceCollection { }

[Collection("MasterSupplierSequence")]
public sealed class MasterSupplierProductSequencePostgresTests
{
    [Fact]
    public async Task PartialNextReceiptContinuesSamePairAndAuthoritativeReceivedQuantity()
    {
        await using var fixture = await Fixture.CreateAsync();
        var purchase = await fixture.OrderAsync(fixture.Seed.SupplierId, 3m);
        var first = await fixture.ReceiveAsync(purchase, 1m);
        var second = await fixture.ReceiveAsync(purchase, 2m);
        Assert.Equal(new long[] { 1, 2, 3 }, first.CommittedUnits.Concat(second.CommittedUnits).Select(x => x.ItemSequence));
        await using var scope = fixture.Provider.CreateAsyncScope();
        var reads = scope.ServiceProvider.GetRequiredService<IPurchasingReadService>();
        var document = await reads.GetDocumentAsync(new GetPurchaseDocumentQuery(purchase), default);
        Assert.NotNull(document);
        var line = Assert.Single(document.Items);
        Assert.Equal(3m, line.ReceivedBaseQuantity);
        Assert.Equal(TrackingMode.IndividualPiece, line.TrackingMode);
        Assert.Equal(fixture.SupplierCode, document.SupplierCode);
        Assert.False(line.SerialTrackingEnabled);
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Equal(3m, (await db.StockBalances.SingleAsync(x => x.ProductId == fixture.Seed.ProductId)).SellableQty);
        Assert.Equal(4, (await db.SupplierProducts.SingleAsync(x => x.SupplierId == fixture.Seed.SupplierId && x.ProductId == fixture.Seed.ProductId)).NextItemSequence);
    }

    [Fact]
    public async Task LaterPurchaseForSameSupplierProductContinuesOnePairSequence()
    {
        await using var fixture = await Fixture.CreateAsync();
        var firstPurchase = await fixture.OrderAsync(fixture.Seed.SupplierId, 2m);
        var first = await fixture.ReceiveAsync(firstPurchase, 2m);
        var nextPurchase = await fixture.OrderAsync(fixture.Seed.SupplierId, 1m);
        var next = await fixture.ReceiveAsync(nextPurchase, 1m);
        Assert.Equal(new long[] { 1, 2, 3 }, first.CommittedUnits.Concat(next.CommittedUnits).Select(x => x.ItemSequence));
        await fixture.AssertUnitProvenanceAsync(firstPurchase, fixture.Seed.SupplierId, first);
        await fixture.AssertUnitProvenanceAsync(nextPurchase, fixture.Seed.SupplierId, next);
        await using var scope = fixture.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Equal(1, await db.SupplierProducts.CountAsync(x => x.ProductId == fixture.Seed.ProductId));
        Assert.Equal(4, (await db.SupplierProducts.SingleAsync(x => x.ProductId == fixture.Seed.ProductId)).NextItemSequence);
    }

    [Fact]
    public async Task SameProductSecondSupplierHasSeparatePairPrefixAndSequence()
    {
        await using var fixture = await Fixture.CreateAsync();
        var other = await fixture.OtherSupplierAsync();
        var a = await fixture.OrderAsync(fixture.Seed.SupplierId, 2m);
        var b = await fixture.OrderAsync(other, 1m);
        var receivedA = await fixture.ReceiveAsync(a, 2m);
        var receivedB = await fixture.ReceiveAsync(b, 1m);
        Assert.Equal(new long[] { 1, 2 }, receivedA.CommittedUnits.Select(x => x.ItemSequence));
        Assert.Equal(1, Assert.Single(receivedB.CommittedUnits).ItemSequence);
        Assert.NotEqual(receivedA.DealerCode, receivedB.DealerCode);
        Assert.All(receivedA.CommittedUnits, unit => Assert.StartsWith(receivedA.DealerCode + "-", unit.TrackingCode));
        Assert.StartsWith(receivedB.DealerCode + "-", receivedB.CommittedUnits[0].TrackingCode);
        await fixture.AssertUnitProvenanceAsync(a, fixture.Seed.SupplierId, receivedA);
        await fixture.AssertUnitProvenanceAsync(b, other, receivedB);
        await using var scope = fixture.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Single(await db.Products.Where(x => x.Id == fixture.Seed.ProductId).ToListAsync());
        Assert.Equal(2, await db.SupplierProducts.CountAsync(x => x.ProductId == fixture.Seed.ProductId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentDistinctOperationsPreserveSameOrDifferentSupplierPairs(bool differentSupplier)
    {
        await using var fixture = await Fixture.CreateAsync();
        var supplierB = differentSupplier ? await fixture.OtherSupplierAsync() : fixture.Seed.SupplierId;
        var purchaseA = await fixture.OrderAsync(fixture.Seed.SupplierId, 1m);
        var purchaseB = await fixture.OrderAsync(supplierB, 1m);
        using var ready = new CountdownEvent(2);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ReceiveProductIntakeResult> Receive(Guid purchase) => Task.Run(async () =>
        {
            ready.Signal();
            await start.Task.WaitAsync(TimeSpan.FromSeconds(30));
            return await fixture.ReceiveAsync(purchase, 1m);
        });
        var firstTask = Receive(purchaseA);
        var secondTask = Receive(purchaseB);
        var callersReady = ready.Wait(TimeSpan.FromSeconds(30));
        start.SetResult();
        var receipts = await Task.WhenAll(firstTask, secondTask);
        Assert.True(callersReady);
        var units = receipts.SelectMany(x => x.CommittedUnits).ToArray();
        Assert.Equal(2, units.Select(x => x.Id).Distinct().Count());
        Assert.Equal(2, units.Select(x => x.TrackingCode).Distinct().Count());
        Assert.Equal(differentSupplier ? new long[] { 1, 1 } : [1, 2], units.Select(x => x.ItemSequence).Order().ToArray());
        await fixture.AssertUnitProvenanceAsync(purchaseA, fixture.Seed.SupplierId, receipts[0]);
        await fixture.AssertUnitProvenanceAsync(purchaseB, supplierB, receipts[1]);
    }

    [Fact]
    public async Task ResponseLossReplayAcrossDisposedScopesReturnsIdenticalCommittedUnits()
    {
        await using var fixture = await Fixture.CreateAsync();
        var purchase = await fixture.OrderAsync(fixture.Seed.SupplierId, 2m);
        var operation = Guid.NewGuid();
        var committed = await fixture.ReceiveAsync(purchase, 2m, operation);
        // Treat the first result as lost to the caller. A fresh service scope replays the same persisted operation.
        var replay = await fixture.ReceiveAsync(purchase, 2m, operation);
        Assert.True(replay.WasExisting);
        Assert.Equal(committed.CommittedUnits.Select(x => x.Id), replay.CommittedUnits.Select(x => x.Id));
        await using var scope = fixture.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Equal(2, await db.InventoryUnits.CountAsync(x => x.ProductId == fixture.Seed.ProductId));
        Assert.Equal(3, (await db.SupplierProducts.SingleAsync(x => x.SupplierId == fixture.Seed.SupplierId && x.ProductId == fixture.Seed.ProductId)).NextItemSequence);
    }

    [Fact]
    public async Task SameOperationIdConcurrentReplayAllocatesOnlyOneRange()
    {
        await using var fixture = await Fixture.CreateAsync();
        var purchase = await fixture.OrderAsync(fixture.Seed.SupplierId, 2m);
        var operation = Guid.NewGuid();
        var results = await Task.WhenAll(fixture.ReceiveAsync(purchase, 2m, operation), fixture.ReceiveAsync(purchase, 2m, operation));
        Assert.Equal(results[0].CommittedUnits.Select(x => x.Id), results[1].CommittedUnits.Select(x => x.Id));
        Assert.Equal(2, results[0].CommittedUnits.Count);
        await using var scope = fixture.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Equal(2, await db.InventoryUnits.CountAsync(x => x.ProductId == fixture.Seed.ProductId));
    }

    [Fact]
    public async Task FailedCommitRollsBackStockAndUnitsWithoutReusingReservedSequence()
    {
        await using var fixture = await Fixture.CreateAsync();
        var purchase = await fixture.OrderAsync(fixture.Seed.SupplierId, 1m);
        await using var failing = fixture.BuildProvider(failCommit: true);
        await using (var scope = failing.CreateAsyncScope())
        {
            var handler = ActivatorUtilities.CreateInstance<ReceiveProductIntakeHandler>(scope.ServiceProvider);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(fixture.Command(purchase, 1m), default));
            Assert.Equal("Injected isolated commit failure after reservation.", failure.Message);
        }
        await using (var scope = fixture.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            Assert.Equal(0, await db.InventoryUnits.CountAsync(x => x.ProductId == fixture.Seed.ProductId));
            Assert.Equal(0, await db.InventoryLots.CountAsync(x => x.ProductId == fixture.Seed.ProductId));
        }
        var retry = await fixture.ReceiveAsync(purchase, 1m);
        Assert.True(Assert.Single(retry.CommittedUnits).ItemSequence >= 2, "A reserved sequence must not be reused after rollback.");
    }

    [Fact]
    public async Task VoidReceiptPreservesSequenceAndNextPurchaseContinues()
    {
        await using var fixture = await Fixture.CreateAsync();
        var purchase = await fixture.OrderAsync(fixture.Seed.SupplierId, 1m);
        var first = await fixture.ReceiveAsync(purchase, 1m);
        await using (var scope = fixture.Provider.CreateAsyncScope())
        {
            var handler = ActivatorUtilities.CreateInstance<VoidPurchaseHandler>(scope.ServiceProvider);
            var result = await handler.HandleAsync(new VoidPurchaseCommand(purchase, Guid.NewGuid(), fixture.Seed.ActorId, "Owned isolated sequence test"), default);
            Assert.True(result.IsSuccess, result.Error?.Message);
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            Assert.Equal(InventoryUnitStatus.ReceiptVoided, (await db.InventoryUnits.SingleAsync(x => x.Id == first.CommittedUnits[0].Id)).Status);
        }
        var nextPurchase = await fixture.OrderAsync(fixture.Seed.SupplierId, 1m);
        var next = await fixture.ReceiveAsync(nextPurchase, 1m);
        Assert.Equal(2, Assert.Single(next.CommittedUnits).ItemSequence);
    }

    [Fact]
    public async Task ForeignProductUnitCannotChangeTheOrderedProduct()
    {
        await using var fixture = await Fixture.CreateAsync();
        var purchase = await fixture.OrderAsync(fixture.Seed.SupplierId, 1m);
        await using var scope = fixture.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var foreign = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);
        var handler = ActivatorUtilities.CreateInstance<ReceiveProductIntakeHandler>(scope.ServiceProvider);
        var result = await handler.HandleAsync(fixture.Command(purchase, 1m) with { ProductUnitId = foreign.ProductUnitId }, default);
        Assert.False(result.IsSuccess);
        Assert.Equal("purchasing.product_unit_not_allowed", result.Error?.Code);
        Assert.Equal(0, await db.InventoryUnits.CountAsync(x => x.ProductId == fixture.Seed.ProductId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualLowerDatabaseRestorePreservesHighWaterOrFailsClosedOnCorruption(bool corruptAuthority)
    {
        await using var fixture = await Fixture.CreateAsync();
        var purchase = await fixture.OrderAsync(fixture.Seed.SupplierId, 1m);
        var dump = Path.Combine(fixture.Root, "before-receipt.dump");
        await fixture.RunPgToolAsync("pg_dump.exe", fixture.Connection.Database!, "-Fc", "-f", dump);
        var dumpHash = SHA256.HashData(await File.ReadAllBytesAsync(dump));
        await using var sourceDb = new NpgsqlConnection(fixture.Connection.ConnectionString);
        await sourceDb.OpenAsync();
        await using var sourceHistory = new NpgsqlCommand("SELECT count(*) FROM system.__ef_migrations_history", sourceDb);
        var sourceHistoryCount = (long)(await sourceHistory.ExecuteScalarAsync())!;
        Assert.True(sourceHistoryCount > 0);
        var original = await fixture.ReceiveAsync(purchase, 1m);
        Assert.Equal(1, Assert.Single(original.CommittedUnits).ItemSequence);
        if (corruptAuthority)
        {
            File.WriteAllText(fixture.Manifest, "corrupted owned authority");
        }
        var restoredName = "edge_retails_master_restore_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(fixture.Connection.ConnectionString) { Database = "postgres" }.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {restoredName}", admin))
        {
            await create.ExecuteNonQueryAsync();
        }
        try
        {
            await fixture.RunPgToolAsync("pg_restore.exe", restoredName, "--no-owner", "--no-privileges", "--exit-on-error", dump);
            Assert.Equal(dumpHash, SHA256.HashData(await File.ReadAllBytesAsync(dump)));
            var restoredConnection = new NpgsqlConnectionStringBuilder(fixture.Connection.ConnectionString) { Database = restoredName };
            await using (var restoredMetadata = new NpgsqlConnection(restoredConnection.ConnectionString))
            {
                await restoredMetadata.OpenAsync();
                await using var history = new NpgsqlCommand("SELECT count(*) FROM system.__ef_migrations_history", restoredMetadata);
                Assert.Equal(sourceHistoryCount, (long)(await history.ExecuteScalarAsync())!);
            }
            if (corruptAuthority)
            {
                await using var restoredDb = new NpgsqlConnection(restoredConnection.ConnectionString);
                await restoredDb.OpenAsync();
                await using var count = new NpgsqlCommand("SELECT count(*) FROM inventory.units WHERE product_id = @product", restoredDb);
                count.Parameters.AddWithValue("product", fixture.Seed.ProductId);
                Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
                Assert.Throws<InvalidOperationException>(() =>
                {
                    using var rejectedProvider = fixture.BuildProvider(restoredConnection.ConnectionString);
                });
            }
            else
            {
                await using var restoredProvider = fixture.BuildProvider(restoredConnection.ConnectionString);
                await using var scope = restoredProvider.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
                Assert.Equal(0, await db.InventoryUnits.CountAsync(x => x.ProductId == fixture.Seed.ProductId));
                var handler = ActivatorUtilities.CreateInstance<ReceiveProductIntakeHandler>(scope.ServiceProvider);
                var result = await handler.HandleAsync(fixture.Command(purchase, 1m), default);
                Assert.True(result.IsSuccess, result.Error?.Message);
                var unit = Assert.Single(result.Value!.CommittedUnits);
                Assert.Equal(2, unit.ItemSequence);
                Assert.NotEqual(original.CommittedUnits[0].TrackingCode, unit.TrackingCode);
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var drop = new NpgsqlCommand($"DROP DATABASE {restoredName} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("edge-master-sequence-").FullName;
        public string Manifest => Path.Combine(Root, "highwater.manifest");
        public ServiceProvider Provider { get; private set; } = null!;
        public NpgsqlConnectionStringBuilder Connection { get; private set; } = null!;
        public QuantityProductFixture Seed { get; private set; } = null!;
        public string SupplierCode { get; private set; } = string.Empty;

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            try
            {
                fixture.Connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB")
                    ?? throw new InvalidOperationException("An owned isolated PostgreSQL connection is required."));
                if (fixture.Connection.Host != "127.0.0.1" || fixture.Connection.Port < 55000
                    || !(fixture.Connection.Database?.StartsWith("edge_retails_", StringComparison.Ordinal) ?? false))
                {
                    throw new InvalidOperationException("Sequence tests require an owned isolated loopback PostgreSQL cluster.");
                }
                await using var pg = new NpgsqlConnection(fixture.Connection.ConnectionString);
                await pg.OpenAsync();
                var ownedRoot = Environment.GetEnvironmentVariable("EDGE_RETAILS_MASTER_PG_RUN_ROOT")
                    ?? throw new InvalidOperationException("The approved runner must attest its owned PostgreSQL root.");
                var safePrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                    + Path.DirectorySeparatorChar + "EdgeRetailsMasterPg_";
                if (!Path.GetFullPath(ownedRoot).StartsWith(safePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("PostgreSQL ownership attestation escaped the approved temporary fixture root.");
                }
                await using var directory = new NpgsqlCommand("SHOW data_directory", pg);
                var actualDataDirectory = (string)(await directory.ExecuteScalarAsync())!;
                Assert.Equal(Path.GetFullPath(Path.Combine(ownedRoot, "data")), Path.GetFullPath(actualDataDirectory), ignoreCase: true);
                await using var version = new NpgsqlCommand("SHOW server_version_num", pg);
                var number = int.Parse((string)(await version.ExecuteScalarAsync())!);
                Assert.InRange(number, 180000, 189999);
                MachineSequenceHighWaterService.InitializeOwnedFixture(fixture.Manifest, new OwnedSequenceAuthorityCustody(fixture.Root));
                fixture.Provider = fixture.BuildProvider();
                await using var scope = fixture.Provider.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
                await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
                fixture.Seed = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);
                var product = await db.Products.SingleAsync(x => x.Id == fixture.Seed.ProductId);
                product.TrackingMode = TrackingMode.IndividualPiece;
                await db.SaveChangesAsync();
                fixture.SupplierCode = (await db.Suppliers.SingleAsync(x => x.Id == fixture.Seed.SupplierId)).DealerCode!;
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        public ServiceProvider BuildProvider(string? connection = null, bool failCommit = false)
        {
            var services = new ServiceCollection();
            services.AddEdgeRetailsInfrastructure(connection ?? Connection.ConnectionString);
            services.RemoveAll<ISequenceHighWaterService>();
            // Construct eagerly to prove invalid persisted authority is rejected before allocation.
            services.AddSingleton<ISequenceHighWaterService>(new MachineSequenceHighWaterService(Manifest, new OwnedSequenceAuthorityCustody(Root)));
            if (failCommit)
            {
                services.RemoveAll<IUnitOfWork>();
                services.AddScoped<IUnitOfWork>(provider => new FailingCommit(provider.GetRequiredService<EdgeRetailsDbContext>()));
            }
            return services.BuildServiceProvider();
        }

        public ReceiveProductIntakeCommand Command(Guid purchase, decimal quantity, Guid? operation = null) => new(
            purchase, Seed.ProductId, Seed.ProductUnitId, quantity, 50m, [], Seed.ActorId, operation ?? Guid.NewGuid(), "Owned sequence certification");

        public async Task<Guid> OrderAsync(Guid supplier, decimal quantity)
        {
            await using var scope = Provider.CreateAsyncScope();
            var handler = ActivatorUtilities.CreateInstance<CreatePurchaseHandler>(scope.ServiceProvider);
            var result = await handler.HandleAsync(new CreatePurchaseCommand(supplier, "MASTER-" + Guid.NewGuid().ToString("N"),
                DateOnly.FromDateTime(DateTime.UtcNow), null, 0m, PurchaseSettlementMode.External, Seed.ActorId, Guid.NewGuid(),
                [new CreatePurchaseLineInput(Seed.ProductId, Seed.ProductUnitId, quantity, 50m, 150m, [])],
                InitialPaymentAmount: 0m, ReceiveStockImmediately: false), default);
            Assert.True(result.IsSuccess, result.Error?.Message);
            return result.Value!.PurchaseId;
        }

        public async Task<ReceiveProductIntakeResult> ReceiveAsync(Guid purchase, decimal quantity, Guid? operation = null)
        {
            await using var scope = Provider.CreateAsyncScope();
            var handler = ActivatorUtilities.CreateInstance<ReceiveProductIntakeHandler>(scope.ServiceProvider);
            var result = await handler.HandleAsync(Command(purchase, quantity, operation), default);
            Assert.True(result.IsSuccess, result.Error?.Message);
            return result.Value!;
        }

        public async Task<Guid> OtherSupplierAsync()
        {
            await using var scope = Provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            return (await Phase2PostgresTestHarness.SeedSupplierAsync(db, "Second Master Supplier", "SM")).Id;
        }

        public async Task AssertUnitProvenanceAsync(Guid purchase, Guid supplier, ReceiveProductIntakeResult receipt)
        {
            await using var scope = Provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var pair = await db.SupplierProducts.SingleAsync(x => x.SupplierId == supplier && x.ProductId == Seed.ProductId);
            var item = await db.PurchaseItems.SingleAsync(x => x.PurchaseId == purchase && x.ProductId == Seed.ProductId);
            foreach (var identity in receipt.CommittedUnits)
            {
                var unit = await db.InventoryUnits.SingleAsync(x => x.Id == identity.Id);
                Assert.Equal(pair.Id, unit.SupplierProductId);
                Assert.Equal(item.Id, unit.SourcePurchaseItemId);
                Assert.Equal(receipt.DealerCode, unit.SupplierCodeSnapshot);
                Assert.Equal(receipt.ProductCode, unit.ProductSkuSnapshot);
                Assert.Equal(item.Id, (await db.InventoryLots.SingleAsync(x => x.Id == unit.InventoryLotId)).PurchaseItemId);
            }
        }

        public async Task RunPgToolAsync(string name, string database, params string[] extra)
        {
            var bin = Environment.GetEnvironmentVariable("EDGE_RETAILS_PG_BIN") ?? @"C:\Program Files\PostgreSQL\18\bin";
            var start = new ProcessStartInfo(Path.Combine(bin, name)) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
            foreach (var arg in new[] { "-h", Connection.Host!, "-p", Connection.Port.ToString(), "-U", Connection.Username!, "-d", database }.Concat(extra))
            {
                start.ArgumentList.Add(arg);
            }
            start.Environment["PGPASSWORD"] = Connection.Password;
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
                await Task.WhenAll(output, errors);
                Assert.True(process.ExitCode == 0, $"Owned PostgreSQL tool {name} failed with exit {process.ExitCode}; credentials are withheld.");
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                    await Task.WhenAll(output, errors);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Provider is not null)
            {
                await Provider.DisposeAsync();
            }
            var prefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar + "edge-master-sequence-";
            if (!Path.GetFullPath(Root).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Owned fixture escaped temporary root.");
            }
            Directory.Delete(Root, true);
        }
    }

    private sealed class FailingCommit(EdgeRetailsDbContext db) : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        {
            // Cost/lot creation saves earlier in the same transaction. Inject only
            // after the handler has reserved tracking sequence and staged identities.
            // The operation outcome ledger may flush staged identities within this
            // transaction first, making them Unchanged; they are still uncommitted.
            if (db.ChangeTracker.Entries<InventoryUnit>().Any())
            {
                throw new InvalidOperationException("Injected isolated commit failure after reservation.");
            }
            return db.SaveChangesAsync(cancellationToken);
        }
    }
}

