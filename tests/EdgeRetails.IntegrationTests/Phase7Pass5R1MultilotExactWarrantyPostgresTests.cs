using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Warranty;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit.Abstractions;

namespace EdgeRetails.IntegrationTests;

[Collection("Phase2PostgresIntegration")]
public sealed class Phase7Pass5R1MultilotExactWarrantyPostgresTests(ITestOutputHelper output)
{
    // NEW_COVERAGE; HARNESS_CORRECTION: pin the proven historical schema so the
    // original refusal remains executable after the additive correction. Assertions unchanged.
    [Theory]
    [InlineData(TrackingMode.Serialized)]
    [InlineData(TrackingMode.IndividualPiece)]
    public async Task H02CurrentMultiPurchaseItemSendDeferredRefusalRollsBack(TrackingMode mode)
    {
        await using var historical = await HistoricalDatabase.CreateAsync();
        var provider = historical.Provider;
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        await db.Database.OpenConnectionAsync();
        await using (var definition = new NpgsqlCommand(
            "SELECT pg_get_functiondef('warranty.assert_shop_case_source(uuid)'::regprocedure)",
            (NpgsqlConnection)db.Database.GetDbConnection()))
        {
            var text = Assert.IsType<string>(await definition.ExecuteScalarAsync());
            output.WriteLine("H02_HISTORICAL_LIVE_FUNCTION_SHA256=" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
            output.WriteLine("H02_HISTORICAL_LIVE_FUNCTION_BEGIN\n" + text + "H02_HISTORICAL_LIVE_FUNCTION_END");
        }
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var seed = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db);
        var product = await db.Products.SingleAsync(x => x.Id == seed.ProductId);
        product.TrackingMode = mode;
        product.SerialTrackingEnabled = mode == TrackingMode.Serialized;
        await db.SaveChangesAsync();
        // HARNESS_CORRECTION: persist valid receipts in the pinned historical
        // schema. The current receipt handler requires the later owner table.
        await SeedHistoricalReceiptsAsync(db, seed, mode);
        var units = await db.InventoryUnits.AsNoTracking().Where(x => x.ProductId == seed.ProductId).ToArrayAsync();
        Assert.Equal(2, units.Length);
        Assert.Equal(2, units.Select(x => x.InventoryLotId).Distinct().Count());
        Assert.Equal(2, units.Select(x => x.SourcePurchaseItemId).Distinct().Count());
        var ids = units.Select(x => x.Id).ToArray();
        var damaged = await services.GetRequiredService<TransferInventoryConditionHandler>().HandleAsync(
            new TransferInventoryConditionCommand(seed.ProductId, InventoryBucket.Sellable, InventoryBucket.Damaged,
                2m, seed.ActorId, "R1 exact source proof", InventoryUnitIds: ids), default);
        Assert.True(damaged.IsSuccess, damaged.Error?.Message);
        var before = await SnapshotAsync(db, seed.ProductId);
        var operation = Guid.NewGuid();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var result = await services.GetRequiredService<SendShopStockToSupplierWarrantyHandler>().HandleAsync(
                new SendShopStockToSupplierWarrantyCommand(seed.ProductId, InventoryBucket.Damaged, 2m,
                    seed.SupplierId, null, "R1 multi-source fault", seed.ActorId, operation, ids), default);
            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.Equal(2, await db.ShopWarrantySendAllocations.CountAsync(x => x.CaseId == result.Value));
            var error = await Assert.ThrowsAsync<PostgresException>(() => transaction.CommitAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
            Assert.Equal("PASS5_PROVENANCE_REQUIRED: case original lot/purchase/supplier/send lineage mismatch", error.MessageText);
            output.WriteLine(JsonSerializer.Serialize(new
            {
                Provider = "PostgreSQL 18 / Npgsql", mode, error.SqlState, error.MessageText,
                error.Where, error.ConstraintName, TransactionBoundary = "caller-owned deferred COMMIT",
                ClientOperationId = operation, CaseId = result.Value,
                OriginalPurchaseItems = units.Select(x => x.SourcePurchaseItemId),
                OriginalLots = units.Select(x => x.InventoryLotId)
            }));
        }
        await using var read = provider.CreateAsyncScope();
        Assert.Equal(before, await SnapshotAsync(read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>(), seed.ProductId));
        output.WriteLine("H02_CURRENT_RED_COMPLETE_ROLLBACK_VERIFIED");
    }

    [Fact]
    public async Task H02ForwardMigrationPreservesTablesColumnsAndForeignKeysAndCapturesLiveFunctions()
    {
        await using var historical = await HistoricalDatabase.CreateAsync();
        await using var scope = historical.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        await db.Database.OpenConnectionAsync();
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        const string shapeSql = """
            SELECT jsonb_build_object(
              'columns', (SELECT jsonb_agg(to_jsonb(c) ORDER BY table_schema,table_name,ordinal_position)
                FROM information_schema.columns c WHERE table_schema NOT IN ('pg_catalog','information_schema')),
              'foreignKeys', (SELECT jsonb_agg(jsonb_build_object('schema',n.nspname,'table',t.relname,'name',c.conname,
                'definition',pg_get_constraintdef(c.oid)) ORDER BY n.nspname,t.relname,c.conname)
                FROM pg_constraint c JOIN pg_class t ON t.oid=c.conrelid JOIN pg_namespace n ON n.oid=t.relnamespace
                WHERE c.contype='f' AND n.nspname NOT IN ('pg_catalog','information_schema'))
            )::text
            """;
        const string functionSql = "SELECT pg_get_functiondef('warranty.assert_shop_case_source(uuid)'::regprocedure)";
        async Task<string> ReadAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            return Assert.IsType<string>(await command.ExecuteScalarAsync());
        }
        var beforeShape = await ReadAsync(shapeSql);
        var beforeFunction = await ReadAsync(functionSql);
        var migrations = db.Database.GetMigrations().ToArray();
        const string target = "20261007063406_Phase7Pass5ExactWarrantyMultiSourceAuthority";
        Assert.Contains(target, migrations);
        var targetIndex = Array.IndexOf(migrations, target);
        Assert.True(targetIndex > 0);
        Assert.Equal("20261006083906_Phase7Pass5WarrantySourceAuthority", migrations[targetIndex - 1]);
        await db.GetService<IMigrator>().MigrateAsync(target);
        Assert.Equal(beforeShape, await ReadAsync(shapeSql));
        var afterFunction = await ReadAsync(functionSql);
        Assert.NotEqual(beforeFunction, afterFunction);
        Assert.Equal(migrations.Take(targetIndex + 1), await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(migrations.Skip(targetIndex + 1), await db.Database.GetPendingMigrationsAsync());
        var triggerCount = await ReadAsync("""
            SELECT count(*)::text FROM pg_trigger
            WHERE NOT tgisinternal AND (tgname LIKE 'tr_h02_%' OR tgname LIKE 'ct_h02_%')
            """);
        Assert.Equal("11", triggerCount);
        var triggerDefinitions = await ReadAsync("""
            SELECT jsonb_agg(jsonb_build_object('name',t.tgname,'definition',pg_get_triggerdef(t.oid),
              'deferred',t.tgdeferrable,'initiallyDeferred',t.tginitdeferred) ORDER BY t.tgname)::text
            FROM pg_trigger t WHERE NOT t.tgisinternal AND (t.tgname LIKE 'tr_h02_%' OR t.tgname LIKE 'ct_h02_%')
            """);
        var helperDefinitions = await ReadAsync("""
            SELECT jsonb_agg(jsonb_build_object('name',p.proname,'definition',pg_get_functiondef(p.oid)) ORDER BY p.proname)::text
            FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
            WHERE n.nspname='warranty' AND p.proname LIKE 'h02_%'
            """);
        output.WriteLine("H02_LIVE_TRIGGER_DEFINITIONS=" + triggerDefinitions);
        output.WriteLine("H02_LIVE_HELPER_DEFINITIONS=" + helperDefinitions);
        output.WriteLine("Provider = PostgreSQL 18 / Npgsql");
        output.WriteLine("H02_BEFORE_FUNCTION_SHA256=" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(beforeFunction))));
        output.WriteLine("H02_AFTER_FUNCTION_SHA256=" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(afterFunction))));
        output.WriteLine("H02_BEFORE_FUNCTION_BEGIN\n" + beforeFunction + "H02_BEFORE_FUNCTION_END");
        output.WriteLine("H02_AFTER_FUNCTION_BEGIN\n" + afterFunction + "H02_AFTER_FUNCTION_END");
        output.WriteLine("H02_TABLE_COLUMN_FOREIGN_KEY_SHAPE_UNCHANGED=" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(beforeShape))));
        await db.GetService<IMigrator>().MigrateAsync();
        Assert.Equal(migrations, (await db.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    private static async Task SeedHistoricalReceiptsAsync(EdgeRetailsDbContext db, SerializedProductFixture seed, TrackingMode mode)
    {
        var product = await db.Products.SingleAsync(x => x.Id == seed.ProductId);
        var supplier = await db.Suppliers.SingleAsync(x => x.Id == seed.SupplierId);
        var now = DateTimeOffset.UtcNow;
        var pair = new SupplierProduct { SupplierId = supplier.Id, ProductId = product.Id, NextItemSequence = 3,
            IsActive = true, CreatedAt = now, UpdatedAt = now, Version = 1 };
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.SupplierProducts.Add(pair);
        for (var i = 0; i < 2; i++)
        {
            var invoice = "R1-HISTORICAL-" + Guid.NewGuid().ToString("N");
            var purchase = new Purchase { SupplierId = supplier.Id, PurchaseNumber = invoice,
                SupplierInvoiceNumber = invoice, NormalizedSupplierInvoiceNumber = PurchaseMath.NormalizeSupplierInvoiceNumber(invoice),
                PurchaseDate = DateOnly.FromDateTime(now.UtcDateTime), Subtotal = 100m, GrandTotal = 100m,
                CreatedBy = seed.ActorId, CreatedAt = now, ClientOperationId = Guid.NewGuid(), Version = 1 };
            var item = new PurchaseItem { PurchaseId = purchase.Id, ProductId = product.Id, ProductUnitId = seed.ProductUnitId,
                ProductNameSnapshot = product.Name, SkuSnapshot = product.Sku, EnteredQuantity = 1m, FactorToBaseSnapshot = 1m,
                BaseQuantity = 1m, EnteredUnitCost = 100m, BaseLineTotal = 100m, EffectiveBaseUnitCost = 100m,
                EffectiveLineCost = 100m, SalePriceAtPurchase = 150m };
            var receipt = new InventoryMovement { ProductId = product.Id, MovementType = InventoryMovementType.PurchaseIn,
                ReferenceType = "PURCHASE", ReferenceId = purchase.Id, UnitCostSnapshot = 100m,
                ActorId = seed.ActorId, OccurredAt = now, CorrelationId = purchase.ClientOperationId };
            var lot = new InventoryLot { ProductId = product.Id, PurchaseItemId = item.Id, SourceMovementId = receipt.Id,
                ReceivedQuantity = 1m, OriginalUnitCost = 100m, EffectiveUnitCost = 100m, CreatedAt = now };
            var serial = mode == TrackingMode.Serialized ? "R1-HISTORICAL-" + Guid.NewGuid().ToString("N").ToUpperInvariant() : null;
            var unit = new InventoryUnit { ProductId = product.Id, SupplierProductId = pair.Id, ItemSequence = i + 1,
                TrackingCode = TraceabilityCodeRules.BuildTrackingCode(supplier.DealerCode!, product.Sku!, i + 1),
                SupplierCodeSnapshot = supplier.DealerCode, ProductSkuSnapshot = product.Sku, SerialNumber = serial,
                OriginType = InventoryUnitOriginType.Purchase, SourcePurchaseItemId = item.Id, InventoryLotId = lot.Id,
                Status = InventoryUnitStatus.InStock, AcquisitionCost = 100m, CreatedAt = now, Version = 1 };
            db.Purchases.Add(purchase);
            db.PurchaseItems.Add(item);
            db.InventoryMovements.Add(receipt);
            db.InventoryMovementEffects.Add(new InventoryMovementEffect { MovementId = receipt.Id,
                StockBucket = InventoryBucket.Sellable, QuantityDelta = 1m, QuantityBefore = i, QuantityAfter = i + 1 });
            db.InventoryLots.Add(lot);
            db.InventoryLotBucketBalances.Add(new InventoryLotBucketBalance { LotId = lot.Id, StockBucket = InventoryBucket.Sellable, Quantity = 1m });
            db.InventoryUnits.Add(unit);
            db.PurchaseItemUnits.Add(new PurchaseItemUnit { PurchaseItemId = item.Id, InventoryUnitId = unit.Id });
            db.InventoryMovementUnits.Add(new InventoryMovementUnit { MovementId = receipt.Id,
                InventoryUnitId = unit.Id, FromStatus = null, ToStatus = InventoryUnitStatus.InStock });
            if (serial is not null)
            {
                db.InventoryUnitIdentityClaims.Add(new InventoryUnitIdentityClaim { InventoryUnitId = unit.Id,
                    IdentifierType = ManufacturerIdentifierType.Serial, IdentifierSlot = ManufacturerIdentifierSlot.Serial,
                    RawValue = serial, NormalizedValue = serial, NormalizationVersion = 1, CreatedAt = now });
            }
        }
        db.StockBalances.Add(new StockBalance { ProductId = product.Id, SellableQty = 2m, Version = 1 });
        db.ProductCostStates.Add(new ProductCostState { ProductId = product.Id, CostedQty = 2m, TotalInventoryCost = 200m,
            MovingAverageCost = 100m, LastPurchaseCost = 100m, LastPurchaseAt = now });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        db.ChangeTracker.Clear();
    }

    private sealed class HistoricalDatabase(NpgsqlConnection admin, string name, ServiceProvider provider) : IAsyncDisposable
    {
        public ServiceProvider Provider { get; } = provider;

        public static async Task<HistoricalDatabase> CreateAsync()
        {
            // Attest the runner before creating a generated database on its owned cluster.
            await using var attested = Phase2PostgresTestHarness.BuildProvider();
            var original = Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB");
            var builder = new NpgsqlConnectionStringBuilder(original) { Database = "postgres" };
            var admin = new NpgsqlConnection(builder.ConnectionString);
            await admin.OpenAsync();
            var name = "edge_retails_h02_history_" + Guid.NewGuid().ToString("N");
            try
            {
                await using (var create = new NpgsqlCommand($"CREATE DATABASE {name}", admin))
                {
                    await create.ExecuteNonQueryAsync();
                }
                builder.Database = name;
                await using (var db = new EdgeRetailsDbContext(new DbContextOptionsBuilder<EdgeRetailsDbContext>()
                    .UseNpgsql(builder.ConnectionString, x => x.MigrationsHistoryTable("__ef_migrations_history", "system")).Options))
                {
                    await db.GetService<IMigrator>().MigrateAsync("20261006083906_Phase7Pass5WarrantySourceAuthority");
                }
                // Collection serialization protects this short provider-construction override.
                Environment.SetEnvironmentVariable("EDGE_RETAILS_TEST_DB", builder.ConnectionString);
                return new HistoricalDatabase(admin, name, Phase2PostgresTestHarness.BuildProvider());
            }
            catch
            {
                await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)", admin);
                await drop.ExecuteNonQueryAsync();
                await admin.DisposeAsync();
                throw;
            }
            finally
            {
                Environment.SetEnvironmentVariable("EDGE_RETAILS_TEST_DB", original);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE {name} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
            await admin.DisposeAsync();
        }
    }

    private static async Task<string> SnapshotAsync(EdgeRetailsDbContext db, Guid productId)
    {
        db.ChangeTracker.Clear();
        return JsonSerializer.Serialize(new
        {
            Units = await db.InventoryUnits.AsNoTracking().Where(x => x.ProductId == productId).OrderBy(x => x.Id).ToArrayAsync(),
            Cases = await db.ShopStockWarrantyCases.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            Sends = await db.ShopWarrantySendAllocations.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            Resolutions = await db.ShopWarrantyResolutionAllocations.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            Stock = await db.StockBalances.AsNoTracking().Where(x => x.ProductId == productId).ToArrayAsync(),
            Costs = await db.ProductCostStates.AsNoTracking().Where(x => x.ProductId == productId).ToArrayAsync(),
            Lots = await db.InventoryLots.AsNoTracking().Where(x => x.ProductId == productId).OrderBy(x => x.Id).ToArrayAsync(),
            LotBalances = await db.InventoryLotBucketBalances.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            Movements = await db.InventoryMovements.AsNoTracking().Where(x => x.ProductId == productId).OrderBy(x => x.Id).ToArrayAsync(),
            Effects = await db.InventoryMovementEffects.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            Links = await db.InventoryMovementUnits.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            Audit = await db.BusinessAuditEvents.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            Outcomes = await db.OperationOutcomes.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            Outbox = await db.OutboxMessages.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync()
        });
    }
}
