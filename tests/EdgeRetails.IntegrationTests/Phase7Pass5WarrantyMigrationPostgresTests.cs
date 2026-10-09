using System.Text.Json;
using EdgeRetails.Application.Production;
using EdgeRetails.Application.Production.Backup;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Warranty;
using EdgeRetails.Infrastructure.Persistence;
using EdgeRetails.Infrastructure.Production.Backup;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Xunit.Abstractions;

namespace EdgeRetails.IntegrationTests;

[Collection("TrackingManufacturerIdentityPg")]
public sealed class Phase7Pass5WarrantyMigrationPostgresTests(ITestOutputHelper output)
{
    private const string PreviousMigration = "20261002101709_TrackingManufacturerIdentityAuthorityV1";
    private const string WarrantyMigrationSuffix = "_Phase7Pass5WarrantySourceAuthority";
    private const string ExactMigrationSuffix = "_Phase7Pass5ExactWarrantyMultiSourceAuthority";
    private static readonly string[] NewTables =
    [
        "shop_send_allocations",
        "shop_resolution_allocations",
        "claim_source_allocations",
        "sale_return_source_allocations"
    ];

    [Fact]
    public async Task FromZero_AppliesWarrantySourceSchemaConstraintsAndMatchesEfModel()
    {
        await WithOwnedDatabaseAsync(async db =>
        {
            var migrations = db.Database.GetMigrations().ToArray();
            var latest = Assert.Single(migrations, x => x.EndsWith(WarrantyMigrationSuffix, StringComparison.Ordinal));
            // HARNESS_CORRECTION: locate the historical boundary by name; newer
            // forward migrations must not turn this predecessor proof into a tail assumption.
            var previousIndex = Array.IndexOf(migrations, PreviousMigration);
            Assert.True(previousIndex >= 0);
            Assert.Equal(latest, migrations[previousIndex + 1]);
            Assert.EndsWith(ExactMigrationSuffix, migrations[previousIndex + 2], StringComparison.Ordinal);

            await db.Database.MigrateAsync();
            Assert.Equal(migrations, (await db.Database.GetAppliedMigrationsAsync()).ToArray());
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.False(db.Database.HasPendingModelChanges());
            output.WriteLine("APPLIED " + latest);

            foreach (var table in NewTables)
            {
                Assert.True(await TableExistsAsync(db, "warranty." + table), table);
                Assert.True(await ConstraintCountAsync(db, table, 'f') >= 3, table + " requires owner/source/event FKs");
                Assert.True(await ConstraintCountAsync(db, table, 'c') >= 1, table + " requires value checks");
                Assert.True(await UniqueIndexCountAsync(db, table) >= 1, table + " requires replay/allocation uniqueness");
                Assert.True(await TriggerExistsAsync(db, "tr_" + table + "_append_only", false, false),
                    table + " requires PostgreSQL append-only enforcement");
            }

            foreach (var trigger in new[]
            {
                "ct_shop_stock_cases_source_authority",
                "ct_shop_send_allocations_source_authority",
                "ct_shop_resolution_allocations_source_authority",
                "ct_claim_items_source_authority",
                "ct_claims_source_authority",
                "ct_return_items_source_authority",
                "ct_shop_lot_custody_authority",
                "ct_claim_source_allocations_source_authority",
                "ct_sale_return_source_allocations_source_authority"
            })
            {
                Assert.True(await TriggerExistsAsync(db, trigger, true, true), trigger + " must be deferred to commit");
            }

            foreach (var function in new[]
            {
                "enforce_provenance_append_only",
                "assert_shop_case_source",
                "validate_shop_source_authority",
                "assert_sold_item_source",
                "validate_sold_source_authority"
            })
            {
                Assert.True(await FunctionExistsAsync(db, function), function);
            }

            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            await db.Database.OpenConnectionAsync();
            var actualIndexes = await CatalogNamesAsync(connection,
                "SELECT schemaname || '.' || indexname FROM pg_indexes");
            var actualConstraints = await CatalogNamesAsync(connection,
                "SELECT n.nspname || '.' || c.conname FROM pg_constraint c JOIN pg_namespace n ON n.oid=c.connamespace");
            var missing = new List<string>();
            foreach (var table in db.GetService<IDesignTimeModel>().Model.GetRelationalModel().Tables)
            {
                foreach (var index in table.Indexes)
                {
                    if (!actualIndexes.Contains(table.Schema + "." + index.Name))
                    {
                        missing.Add("INDEX " + table.Schema + "." + index.Name);
                    }
                }
                foreach (var foreignKey in table.ForeignKeyConstraints)
                {
                    if (!actualConstraints.Contains(table.Schema + "." + foreignKey.Name))
                    {
                        missing.Add("FK " + table.Schema + "." + foreignKey.Name);
                    }
                }
                foreach (var check in table.CheckConstraints)
                {
                    if (!actualConstraints.Contains(table.Schema + "." + check.Name))
                    {
                        missing.Add("CHECK " + table.Schema + "." + check.Name);
                    }
                }
            }
            Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HistoricalExactGraph_OldWriterExemptionRequiresCompletePhysicalQuantity(bool completeGraph)
    {
        await WithOwnedDatabaseAsync(async db =>
        {
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            var fixture = await SeedPreWarrantyLegacyCaseAsync(db);
            var lot = await db.InventoryLots.SingleAsync(x => x.Id == fixture.LotId);
            var source = new SupplierProduct { SupplierId = fixture.SupplierId, ProductId = fixture.ProductId,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
            db.SupplierProducts.Add(source);
            for (var index = 0; index < 4; index++)
            {
                var unit = new InventoryUnit { ProductId = fixture.ProductId, SupplierProductId = source.Id,
                    InventoryLotId = fixture.LotId, SourcePurchaseItemId = fixture.PurchaseItemId,
                    Status = index < 2 ? InventoryUnitStatus.WithSupplier : InventoryUnitStatus.Damaged,
                    AcquisitionCost = 50m, CreatedAt = DateTimeOffset.UtcNow };
                db.InventoryUnits.Add(unit);
                db.InventoryMovementUnits.Add(new InventoryMovementUnit { MovementId = lot.SourceMovementId,
                    InventoryUnitId = unit.Id, ToStatus = InventoryUnitStatus.InStock });
                if (index < (completeGraph ? 2 : 1))
                {
                    db.InventoryMovementUnits.Add(new InventoryMovementUnit { MovementId = fixture.SendMovementId,
                        InventoryUnitId = unit.Id, FromStatus = InventoryUnitStatus.Damaged,
                        ToStatus = InventoryUnitStatus.WithSupplier });
                }
            }
            await db.SaveChangesAsync();
            await db.Database.MigrateAsync();
            var warrantyCase = await db.ShopStockWarrantyCases.SingleAsync(x => x.Id == fixture.CaseId);
            warrantyCase.SupplierReference = "old exact writer";
            if (completeGraph)
            {
                await db.SaveChangesAsync();
                Assert.Equal("old exact writer", await db.ShopStockWarrantyCases.AsNoTracking()
                    .Where(x => x.Id == fixture.CaseId).Select(x => x.SupplierReference).SingleAsync());
            }
            else
            {
                var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
                AssertProvenanceRequired(Assert.IsType<PostgresException>(error.InnerException));
            }
            Assert.Equal(0, await AllocationCountAsync(db));
        });
    }

    [Fact]
    public async Task UpgradedTypedFacts_CanonicalProtectedBackupRestorePreservesHistoryDataAndGuards()
    {
        await WithOwnedDatabaseAsync(async db =>
        {
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            var fixture = await SeedPreWarrantyLegacyCaseAsync(db);
            await db.Database.MigrateAsync();
            var allocationId = await InsertSendAllocationAsync(db, fixture, fixture.LotId, 2m);
            var before = await LegacySnapshotAsync(db, fixture);
            var history = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
            var builder = new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString());
            var tools = Environment.GetEnvironmentVariable("EDGE_RETAILS_PG_BIN")
                ?? throw new InvalidOperationException("Owned PostgreSQL tools authority is required.");
            var ownedRoot = Environment.GetEnvironmentVariable("EDGE_RETAILS_MASTER_PG_RUN_ROOT")
                ?? throw new InvalidOperationException("Owned rehearsal root is required.");
            var directory = Path.Combine(ownedRoot, "wb-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var runtimeRole = "er_warranty_runtime_" + Guid.NewGuid().ToString("N");
            var runtimePassword = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            await db.Database.OpenConnectionAsync();
            var adminConnection = (NpgsqlConnection)db.Database.GetDbConnection();
            await using (var format = new NpgsqlCommand("SELECT format('CREATE ROLE %I LOGIN PASSWORD %L', @role, @password)", adminConnection))
            {
                format.Parameters.AddWithValue("role", runtimeRole);
                format.Parameters.AddWithValue("password", runtimePassword);
                var createRoleSql = (string)(await format.ExecuteScalarAsync())!;
                await using var createRole = new NpgsqlCommand(createRoleSql, adminConnection);
                await createRole.ExecuteNonQueryAsync();
            }
            foreach (var schema in new[] { "system", "identity", "parties", "catalog", "inventory", "sales",
                "purchasing", "thaka", "warranty", "finance", "audit", "reporting" })
            {
                await using var grant = new NpgsqlCommand($"GRANT USAGE ON SCHEMA {schema} TO {runtimeRole}; " +
                    $"GRANT SELECT ON ALL TABLES IN SCHEMA {schema} TO {runtimeRole}; " +
                    $"GRANT SELECT ON ALL SEQUENCES IN SCHEMA {schema} TO {runtimeRole}", adminConnection);
                await grant.ExecuteNonQueryAsync();
            }
            var runtime = new PostgresConnectionDescriptor(builder.Host!, builder.Port, builder.Database!,
                runtimeRole, new SensitiveString(runtimePassword));
            var maintenance = new PostgresMaintenanceDescriptor(builder.Host!, builder.Port, builder.Username!,
                new SensitiveString(builder.Password!));
            var keys = new RehearsalKeys();
            var sessions = new HmacRestoreSessionStore(Path.Combine(directory, "journal"), keys);
            var barrier = new FileProductionMaintenanceBarrier(Path.Combine(directory, "maintenance"), keys);
            var engine = new PostgresBackupEngine(Path.Combine(tools, "pg_dump.exe"), Path.Combine(tools, "pg_restore.exe"),
                Path.Combine(tools, "psql.exe"), Path.Combine(tools, "createdb.exe"), new AesGcmBackupProtector(keys),
                sessions, new RehearsalMaintenanceProvider(maintenance), barrier,
                new CanonicalRestoreStagingValidator(Path.Combine(tools, "psql.exe"),
                    [new EdgeRetailsEfRestoreCompatibilityProbe(), new EdgeRetailsBusinessRestoreCompatibilityProbe(Path.Combine(tools, "psql.exe"))]),
                new HmacBackupManifestAuthenticator(keys));
            RestoreSessionToken? token = null;
            try
            {
                var backup = await engine.CreateAsync(new BackupCreateRequest(runtime, directory, "pass5-resolution05",
                    history[^1], new BackupRetentionPolicy(null, null), "owned-warranty-rehearsal"));
                Assert.True(File.Exists(backup.FullPath));
                Assert.True(File.Exists(backup.FullPath + ".manifest.json"));
                Assert.Equal(64, backup.Manifest.Sha256.Length);
                token = await engine.PrepareRestoreAsync(new RestorePrepareRequest(runtime, backup.FullPath,
                    backup.FullPath + ".manifest.json", "owned-warranty-rehearsal"));
                var session = Assert.IsType<RestoreSessionRecord>(await sessions.GetAsync(token.RestoreId));
                Assert.Equal(RestoreSessionState.Prepared, session.State);
                Assert.Equal(backup.Manifest.Sha256, session.VerifiedSha256);
                Assert.NotEqual(builder.Database, session.StagingDatabase);
                builder.Database = session.StagingDatabase;
                builder.Pooling = false;
                await using var restored = new EdgeRetailsDbContext(new DbContextOptionsBuilder<EdgeRetailsDbContext>()
                    .UseNpgsql(builder.ConnectionString, x => x.MigrationsHistoryTable("__ef_migrations_history", "system")).Options);
                Assert.Equal(history, (await restored.Database.GetAppliedMigrationsAsync()).ToArray());
                Assert.Empty(await restored.Database.GetPendingMigrationsAsync());
                Assert.False(restored.Database.HasPendingModelChanges());
                Assert.Equal(before, await LegacySnapshotAsync(restored, fixture));
                var originalFact = await db.ShopWarrantySendAllocations.AsNoTracking().SingleAsync();
                var restoredFact = await restored.ShopWarrantySendAllocations.AsNoTracking().SingleAsync();
                Assert.Equal(JsonSerializer.Serialize(originalFact), JsonSerializer.Serialize(restoredFact));
                await restored.Database.OpenConnectionAsync();
                await using var mutation = new NpgsqlCommand("DELETE FROM warranty.shop_send_allocations WHERE id=@id",
                    (NpgsqlConnection)restored.Database.GetDbConnection());
                mutation.Parameters.AddWithValue("id", allocationId);
                AssertAppendOnly(await Assert.ThrowsAsync<PostgresException>(() => mutation.ExecuteNonQueryAsync()));
                output.WriteLine("CANONICAL_BACKUP_RESTORE_VERIFIED Provider=PostgreSQL 18 / Npgsql SHA256=" + session.VerifiedSha256);
            }
            finally
            {
                if (token is not null)
                {
                    await engine.DiscardPreparedRestoreAsync(runtime, token);
                    Assert.Equal(RestoreSessionState.Discarded, (await sessions.GetAsync(token.RestoreId))!.State);
                    Assert.Equal(ProductionMaintenanceState.Normal, await barrier.GetStateAsync());
                }
                await using var dropRole = new NpgsqlCommand($"DROP OWNED BY {runtimeRole}; DROP ROLE {runtimeRole}", adminConnection);
                await dropRole.ExecuteNonQueryAsync();
                if (Directory.Exists(directory))
                {
                    try { Directory.Delete(directory, recursive: true); } catch { }
                }
            }
        });
    }

    private sealed class RehearsalKeys : IBackupEncryptionKeyProvider, IRestoreJournalIntegrityKeyProvider,
        IProductionMaintenanceIntegrityKeyProvider
    {
        public Task<byte[]> GetKeyAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray());
        Task<byte[]> IRestoreJournalIntegrityKeyProvider.GetIntegrityKeyAsync(CancellationToken cancellationToken)
            => Task.FromResult(Enumerable.Range(33, 32).Select(x => (byte)x).ToArray());
        Task<byte[]> IProductionMaintenanceIntegrityKeyProvider.GetIntegrityKeyAsync(CancellationToken cancellationToken)
            => Task.FromResult(Enumerable.Range(65, 32).Select(x => (byte)x).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedSourceCapacity_RefusesCustodyReductionAndOldLotRetargeting(bool retarget)
    {
        await WithOwnedDatabaseAsync(async db =>
        {
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            var fixture = await SeedPreWarrantyLegacyCaseAsync(db);
            var otherLot = new InventoryLot { ProductId = fixture.ProductId, SourceMovementId = fixture.SendMovementId,
                ReceivedQuantity = 2m, OriginalUnitCost = 50m, EffectiveUnitCost = 50m, CreatedAt = DateTimeOffset.UtcNow };
            db.InventoryLots.Add(otherLot);
            await db.SaveChangesAsync();
            await db.Database.MigrateAsync();
            await InsertSendAllocationAsync(db, fixture, fixture.LotId, 2m);
            var before = await LegacySnapshotAsync(db, fixture);
            await db.Database.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(retarget
                ? "UPDATE inventory.lot_bucket_balances SET lot_id=@other WHERE lot_id=@lot AND stock_bucket=4"
                : "UPDATE inventory.lot_bucket_balances SET quantity=1 WHERE lot_id=@lot AND stock_bucket=4",
                (NpgsqlConnection)db.Database.GetDbConnection());
            command.Parameters.AddWithValue("lot", fixture.LotId);
            if (retarget)
            {
                command.Parameters.AddWithValue("other", otherLot.Id);
            }
            var refusal = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, refusal.SqlState);
            Assert.StartsWith("PASS5_PROVENANCE_CAPACITY:", refusal.MessageText, StringComparison.Ordinal);
            Assert.Equal(before, await LegacySnapshotAsync(db, fixture));
            Assert.Equal(1, await AllocationCountAsync(db));
        });
    }

    [Fact]
    public async Task TypedSource_RefusesFkValidWrongPurchaseLineageAndApplicationHistoryMutation()
    {
        await WithOwnedDatabaseAsync(async db =>
        {
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            var fixture = await SeedPreWarrantyLegacyCaseAsync(db);
            var otherLot = new InventoryLot { ProductId = fixture.ProductId, SourceMovementId = fixture.SendMovementId,
                ReceivedQuantity = 2m, OriginalUnitCost = 50m, EffectiveUnitCost = 50m, CreatedAt = DateTimeOffset.UtcNow };
            db.InventoryLots.Add(otherLot);
            await db.SaveChangesAsync();
            await db.Database.MigrateAsync();
            AssertProvenanceRequired(await Assert.ThrowsAsync<PostgresException>(() =>
                InsertSendAllocationAsync(db, fixture, otherLot.Id, 2m)));
            Assert.Equal(0, await AllocationCountAsync(db));
            await InsertSendAllocationAsync(db, fixture, fixture.LotId, 2m);
            var allocation = await db.ShopWarrantySendAllocations.SingleAsync();
            allocation.BaseQuantity = 1m;
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            Assert.Contains("append-only", error.Message, StringComparison.Ordinal);
            db.ChangeTracker.Clear();
            Assert.Equal(2m, await db.ShopWarrantySendAllocations.Select(x => x.BaseQuantity).SingleAsync());
        });
    }

    private sealed class RehearsalMaintenanceProvider(PostgresMaintenanceDescriptor descriptor) : IPostgresMaintenanceConnectionProvider
    {
        public Task<PostgresMaintenanceDescriptor> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(descriptor);
    }

    [Fact]
    public async Task ExistingTrackingSchema_ForwardUpgradePreservesLegacyRowsAndRefusesOldBulkWriters()
    {
        await WithOwnedDatabaseAsync(async db =>
        {
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            var fixture = await SeedPreWarrantyLegacyCaseAsync(db);
            var previousHistory = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
            Assert.Equal(PreviousMigration, previousHistory[^1]);
            var before = await LegacySnapshotAsync(db, fixture);
            Assert.False(await TableExistsAsync(db, "warranty.shop_send_allocations"));

            await db.Database.MigrateAsync();
            var history = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
            // HARNESS_CORRECTION: preserve the named historical prefix and require
            // both warranty successors, then all current forward authority.
            Assert.Equal(previousHistory, history.Take(previousHistory.Length));
            Assert.EndsWith(WarrantyMigrationSuffix, history[previousHistory.Length], StringComparison.Ordinal);
            Assert.EndsWith(ExactMigrationSuffix, history[previousHistory.Length + 1], StringComparison.Ordinal);
            Assert.Equal(db.Database.GetMigrations(), history);
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Equal(before, await LegacySnapshotAsync(db, fixture));
            Assert.Equal(0, await AllocationCountAsync(db));

            // A historical bulk case has no provable case-to-lot allocation. The
            // forward migration preserves it but must not invent a backfill.
            await using (var transaction = await db.Database.BeginTransactionAsync())
            {
                var warrantyCase = await db.ShopStockWarrantyCases.SingleAsync(x => x.Id == fixture.CaseId);
                warrantyCase.Status = ShopWarrantyCaseStatus.Closed;
                warrantyCase.ClosedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();
                var refusal = await Assert.ThrowsAsync<PostgresException>(() => transaction.CommitAsync());
                AssertProvenanceRequired(refusal);
            }
            db.ChangeTracker.Clear();
            Assert.Equal(before, await LegacySnapshotAsync(db, fixture));

            // Simulate an installed older bulk writer: case, movement and bucket
            // transfer are written, but the new typed allocation is absent.
            var oldCaseId = Guid.NewGuid();
            var oldMovementId = Guid.NewGuid();
            await using (var transaction = await db.Database.BeginTransactionAsync())
            {
                var oldCase = new ShopStockWarrantyCase
                {
                    Id = oldCaseId,
                    CaseNumber = "OLD-WRITER-" + Guid.NewGuid().ToString("N"),
                    ProductId = fixture.ProductId,
                    SupplierId = fixture.SupplierId,
                    SourcePurchaseItemId = fixture.PurchaseItemId,
                    BaseQuantity = 1m,
                    Status = ShopWarrantyCaseStatus.WithSupplier,
                    FaultDescription = "pre-allocation binary",
                    CreatedBy = fixture.ActorId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    SentAt = DateTimeOffset.UtcNow
                };
                var oldMovement = new InventoryMovement
                {
                    Id = oldMovementId,
                    ProductId = fixture.ProductId,
                    MovementType = InventoryMovementType.SendToSupplierWarranty,
                    ReferenceType = "SHOP_WARRANTY",
                    ReferenceId = oldCaseId,
                    ActorId = fixture.ActorId,
                    OccurredAt = DateTimeOffset.UtcNow,
                    CorrelationId = Guid.NewGuid(),
                    Reason = "SUPPLIER_WARRANTY"
                };
                db.ShopStockWarrantyCases.Add(oldCase);
                db.InventoryMovements.Add(oldMovement);
                db.InventoryMovementEffects.AddRange(
                    new InventoryMovementEffect { MovementId = oldMovementId, StockBucket = InventoryBucket.Damaged,
                        QuantityDelta = -1m, QuantityBefore = 2m, QuantityAfter = 1m },
                    new InventoryMovementEffect { MovementId = oldMovementId, StockBucket = InventoryBucket.WithSupplier,
                        QuantityDelta = 1m, QuantityBefore = 2m, QuantityAfter = 3m });
                var stock = await db.StockBalances.SingleAsync(x => x.ProductId == fixture.ProductId);
                stock.Transfer(InventoryBucket.Damaged, InventoryBucket.WithSupplier, 1m);
                var damaged = await db.InventoryLotBucketBalances.SingleAsync(x =>
                    x.LotId == fixture.LotId && x.StockBucket == InventoryBucket.Damaged);
                var withSupplier = await db.InventoryLotBucketBalances.SingleAsync(x =>
                    x.LotId == fixture.LotId && x.StockBucket == InventoryBucket.WithSupplier);
                damaged.Quantity -= 1m;
                withSupplier.Quantity += 1m;
                await db.SaveChangesAsync();
                var refusal = await Assert.ThrowsAsync<PostgresException>(() => transaction.CommitAsync());
                AssertProvenanceRequired(refusal);
            }
            db.ChangeTracker.Clear();
            Assert.False(await db.ShopStockWarrantyCases.AsNoTracking().AnyAsync(x => x.Id == oldCaseId));
            Assert.False(await db.InventoryMovements.AsNoTracking().AnyAsync(x => x.Id == oldMovementId));
            Assert.Equal(before, await LegacySnapshotAsync(db, fixture));
            Assert.Equal(0, await AllocationCountAsync(db));
        });
    }

    [Fact]
    public async Task TypedSendAllocation_RejectsBadForeignKeyAndQuantityThenRemainsAppendOnly()
    {
        await WithOwnedDatabaseAsync(async db =>
        {
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            var fixture = await SeedPreWarrantyLegacyCaseAsync(db);
            await db.Database.MigrateAsync();
            Assert.Equal(0, await AllocationCountAsync(db));

            var invalidLot = await Assert.ThrowsAsync<PostgresException>(() =>
                InsertSendAllocationAsync(db, fixture, Guid.NewGuid(), 2m));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, invalidLot.SqlState);
            var invalidQuantity = await Assert.ThrowsAsync<PostgresException>(() =>
                InsertSendAllocationAsync(db, fixture, fixture.LotId, -1m));
            Assert.Equal(PostgresErrorCodes.CheckViolation, invalidQuantity.SqlState);
            Assert.Equal(0, await AllocationCountAsync(db));

            var allocationId = await InsertSendAllocationAsync(db, fixture, fixture.LotId, 2m);
            Assert.Equal(1, await AllocationCountAsync(db));
            await db.Database.OpenConnectionAsync();
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();

            await using (var update = new NpgsqlCommand(
                "UPDATE warranty.shop_send_allocations SET base_quantity = 1 WHERE id = @id", connection))
            {
                update.Parameters.AddWithValue("id", allocationId);
                AssertAppendOnly(await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync()));
            }
            await using (var delete = new NpgsqlCommand(
                "DELETE FROM warranty.shop_send_allocations WHERE id = @id", connection))
            {
                delete.Parameters.AddWithValue("id", allocationId);
                AssertAppendOnly(await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync()));
            }
            Assert.Equal(1, await AllocationCountAsync(db));
            var stored = await db.ShopWarrantySendAllocations.AsNoTracking().SingleAsync();
            Assert.Equal(fixture.CaseId, stored.CaseId);
            Assert.Equal(fixture.LotId, stored.OriginalInventoryLotId);
            Assert.Equal(2m, stored.BaseQuantity);
            Assert.Equal(50m, stored.SourceUnitCostSnapshot);
            Assert.Equal(100m, stored.SendTimeCarryingValueSnapshot);
        });
    }

    [Fact]
    public async Task TypedSendAllocation_FailedSqlTransactionRollsBackProvisionalFact()
    {
        await WithOwnedDatabaseAsync(async db =>
        {
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            var fixture = await SeedPreWarrantyLegacyCaseAsync(db);
            await db.Database.MigrateAsync();
            var before = await LegacySnapshotAsync(db, fixture);

            await db.Database.OpenConnectionAsync();
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            await using (var transaction = await db.Database.BeginTransactionAsync())
            {
                await InsertSendAllocationAsync(db, fixture, fixture.LotId, 2m,
                    (NpgsqlTransaction)transaction.GetDbTransaction());
                await using var fault = new NpgsqlCommand("SELECT 1 / 0", connection,
                    (NpgsqlTransaction)transaction.GetDbTransaction());
                var error = await Assert.ThrowsAsync<PostgresException>(() => fault.ExecuteScalarAsync());
                Assert.Equal(PostgresErrorCodes.DivisionByZero, error.SqlState);
            }
            Assert.Equal(0, await AllocationCountAsync(db));
            Assert.Equal(before, await LegacySnapshotAsync(db, fixture));
        });
    }

    private sealed record LegacyFixture(Guid ProductId, Guid SupplierId, Guid ActorId,
        Guid PurchaseItemId, Guid LotId, Guid CaseId, Guid SendMovementId, Guid SendOperationId);

    private static async Task<LegacyFixture> SeedPreWarrantyLegacyCaseAsync(EdgeRetailsDbContext db)
    {
        var seed = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);
        var now = DateTimeOffset.UtcNow;
        var purchase = new Purchase
        {
            PurchaseNumber = "LEGACY-PURCHASE-" + Guid.NewGuid().ToString("N"),
            SupplierId = seed.SupplierId,
            SupplierInvoiceNumber = "LEGACY-" + Guid.NewGuid().ToString("N"),
            PurchaseDate = DateOnly.FromDateTime(now.UtcDateTime),
            Subtotal = 200m,
            GrandTotal = 200m,
            CreatedBy = seed.ActorId,
            CreatedAt = now,
            ClientOperationId = Guid.NewGuid()
        };
        purchase.NormalizedSupplierInvoiceNumber = PurchaseMath.NormalizeSupplierInvoiceNumber(purchase.SupplierInvoiceNumber);
        var item = new PurchaseItem
        {
            PurchaseId = purchase.Id,
            ProductId = seed.ProductId,
            ProductUnitId = seed.ProductUnitId,
            ProductNameSnapshot = "Legacy warranty product",
            EnteredQuantity = 4m,
            FactorToBaseSnapshot = 1m,
            BaseQuantity = 4m,
            EnteredUnitCost = 50m,
            BaseLineTotal = 200m,
            EffectiveBaseUnitCost = 50m,
            EffectiveLineCost = 200m,
            SalePriceAtPurchase = 100m
        };
        var warrantyCase = new ShopStockWarrantyCase
        {
            CaseNumber = "LEGACY-WARRANTY-" + Guid.NewGuid().ToString("N"),
            ProductId = seed.ProductId,
            SupplierId = seed.SupplierId,
            SourcePurchaseItemId = item.Id,
            BaseQuantity = 2m,
            Status = ShopWarrantyCaseStatus.WithSupplier,
            FaultDescription = "historical bulk case with conclusive one-lot source",
            CreatedBy = seed.ActorId,
            CreatedAt = now,
            SentAt = now.AddMinutes(2)
        };
        var receipt = new InventoryMovement
        {
            ProductId = seed.ProductId,
            MovementType = InventoryMovementType.PurchaseIn,
            ReferenceType = "PURCHASE",
            ReferenceId = purchase.Id,
            ActorId = seed.ActorId,
            OccurredAt = now,
            CorrelationId = purchase.ClientOperationId
        };
        var damaged = new InventoryMovement
        {
            ProductId = seed.ProductId,
            MovementType = InventoryMovementType.MarkDamaged,
            ReferenceType = "LEGACY_CONDITION",
            ReferenceId = Guid.NewGuid(),
            ActorId = seed.ActorId,
            OccurredAt = now.AddMinutes(1),
            CorrelationId = Guid.NewGuid()
        };
        var send = new InventoryMovement
        {
            ProductId = seed.ProductId,
            MovementType = InventoryMovementType.SendToSupplierWarranty,
            ReferenceType = "SHOP_WARRANTY",
            ReferenceId = warrantyCase.Id,
            ActorId = seed.ActorId,
            OccurredAt = now.AddMinutes(2),
            CorrelationId = Guid.NewGuid()
        };
        var lot = new InventoryLot
        {
            ProductId = seed.ProductId,
            SourceMovementId = receipt.Id,
            PurchaseItemId = item.Id,
            ReceivedQuantity = 4m,
            OriginalUnitCost = 50m,
            EffectiveUnitCost = 50m,
            CreatedAt = now
        };
        db.Purchases.Add(purchase);
        db.PurchaseItems.Add(item);
        db.ShopStockWarrantyCases.Add(warrantyCase);
        db.InventoryMovements.AddRange(receipt, damaged, send);
        db.InventoryLots.Add(lot);
        db.InventoryMovementEffects.AddRange(
            new InventoryMovementEffect { MovementId = receipt.Id, StockBucket = InventoryBucket.Sellable,
                QuantityDelta = 4m, QuantityBefore = 0m, QuantityAfter = 4m },
            new InventoryMovementEffect { MovementId = damaged.Id, StockBucket = InventoryBucket.Sellable,
                QuantityDelta = -4m, QuantityBefore = 4m, QuantityAfter = 0m },
            new InventoryMovementEffect { MovementId = damaged.Id, StockBucket = InventoryBucket.Damaged,
                QuantityDelta = 4m, QuantityBefore = 0m, QuantityAfter = 4m },
            new InventoryMovementEffect { MovementId = send.Id, StockBucket = InventoryBucket.Damaged,
                QuantityDelta = -2m, QuantityBefore = 4m, QuantityAfter = 2m },
            new InventoryMovementEffect { MovementId = send.Id, StockBucket = InventoryBucket.WithSupplier,
                QuantityDelta = 2m, QuantityBefore = 0m, QuantityAfter = 2m });
        db.InventoryLotBucketBalances.AddRange(
            new InventoryLotBucketBalance { LotId = lot.Id, StockBucket = InventoryBucket.Damaged, Quantity = 2m },
            new InventoryLotBucketBalance { LotId = lot.Id, StockBucket = InventoryBucket.WithSupplier, Quantity = 2m });
        db.StockBalances.Add(new StockBalance { ProductId = seed.ProductId, DamagedQty = 2m, WithSupplierQty = 2m });
        db.ProductCostStates.Add(new ProductCostState
        {
            ProductId = seed.ProductId,
            CostedQty = 4m,
            TotalInventoryCost = 200m,
            MovingAverageCost = 50m,
            LastPurchaseCost = 50m,
            LastPurchaseAt = now
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new(seed.ProductId, seed.SupplierId, seed.ActorId, item.Id, lot.Id,
            warrantyCase.Id, send.Id, send.CorrelationId);
    }

    private static async Task<string> LegacySnapshotAsync(EdgeRetailsDbContext db, LegacyFixture fixture)
    {
        db.ChangeTracker.Clear();
        return JsonSerializer.Serialize(new
        {
            Case = await db.ShopStockWarrantyCases.AsNoTracking().SingleAsync(x => x.Id == fixture.CaseId),
            Lot = await db.InventoryLots.AsNoTracking().SingleAsync(x => x.Id == fixture.LotId),
            LotBalances = await db.InventoryLotBucketBalances.AsNoTracking()
                .Where(x => x.LotId == fixture.LotId).OrderBy(x => x.StockBucket).ToArrayAsync(),
            PurchaseItem = await db.PurchaseItems.AsNoTracking().SingleAsync(x => x.Id == fixture.PurchaseItemId),
            Movements = await db.InventoryMovements.AsNoTracking()
                .Where(x => x.ProductId == fixture.ProductId).OrderBy(x => x.Id).ToArrayAsync(),
            Effects = await (from effect in db.InventoryMovementEffects.AsNoTracking()
                join movement in db.InventoryMovements.AsNoTracking() on effect.MovementId equals movement.Id
                where movement.ProductId == fixture.ProductId
                orderby effect.Id
                select effect).ToArrayAsync(),
            Stock = await db.StockBalances.AsNoTracking().SingleAsync(x => x.ProductId == fixture.ProductId),
            Cost = await db.ProductCostStates.AsNoTracking().SingleAsync(x => x.ProductId == fixture.ProductId)
        });
    }

    private static async Task<Guid> InsertSendAllocationAsync(EdgeRetailsDbContext db, LegacyFixture fixture,
        Guid lotId, decimal quantity, NpgsqlTransaction? transaction = null)
    {
        await db.Database.OpenConnectionAsync();
        var id = Guid.NewGuid();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO warranty.shop_send_allocations
                (id, case_id, original_inventory_lot_id, send_movement_id, base_quantity,
                 source_unit_cost_snapshot, send_time_mwa_unit_cost_snapshot,
                 send_time_carrying_value_snapshot, client_operation_id, actor_id, occurred_at)
            VALUES (@id, @case_id, @lot_id, @movement_id, @quantity, 50, 50, 100,
                    @operation_id, @actor_id, @occurred_at)
            """,
            (NpgsqlConnection)db.Database.GetDbConnection(), transaction);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("case_id", fixture.CaseId);
        command.Parameters.AddWithValue("lot_id", lotId);
        command.Parameters.AddWithValue("movement_id", fixture.SendMovementId);
        command.Parameters.AddWithValue("quantity", quantity);
        command.Parameters.AddWithValue("operation_id", fixture.SendOperationId);
        command.Parameters.AddWithValue("actor_id", fixture.ActorId);
        command.Parameters.AddWithValue("occurred_at", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync();
        return id;
    }

    private static void AssertProvenanceRequired(PostgresException error)
    {
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.StartsWith("PASS5_PROVENANCE_REQUIRED:", error.MessageText, StringComparison.Ordinal);
    }

    private static void AssertAppendOnly(PostgresException error)
    {
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.StartsWith("PASS5_PROVENANCE_APPEND_ONLY:", error.MessageText, StringComparison.Ordinal);
    }

    private static async Task<int> AllocationCountAsync(EdgeRetailsDbContext db) =>
        await db.ShopWarrantySendAllocations.AsNoTracking().CountAsync();

    private static async Task<bool> TableExistsAsync(EdgeRetailsDbContext db, string name)
    {
        await db.Database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT to_regclass(@name) IS NOT NULL",
            (NpgsqlConnection)db.Database.GetDbConnection());
        command.Parameters.AddWithValue("name", name);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<int> ConstraintCountAsync(EdgeRetailsDbContext db, string table, char type)
    {
        await db.Database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*) FROM pg_constraint c
            JOIN pg_class t ON t.oid = c.conrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            WHERE n.nspname = 'warranty' AND t.relname = @table AND c.contype = @type
            """, (NpgsqlConnection)db.Database.GetDbConnection());
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("type", type.ToString());
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<int> UniqueIndexCountAsync(EdgeRetailsDbContext db, string table)
    {
        await db.Database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*) FROM pg_index i
            JOIN pg_class t ON t.oid = i.indrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            WHERE n.nspname = 'warranty' AND t.relname = @table
              AND i.indisunique AND NOT i.indisprimary
            """, (NpgsqlConnection)db.Database.GetDbConnection());
        command.Parameters.AddWithValue("table", table);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<bool> TriggerExistsAsync(EdgeRetailsDbContext db, string trigger,
        bool deferrable, bool initiallyDeferred)
    {
        await db.Database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*) FROM pg_trigger t
            JOIN pg_class c ON c.oid = t.tgrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname IN ('warranty', 'sales', 'inventory') AND t.tgname = @name AND NOT t.tgisinternal
              AND t.tgdeferrable = @deferrable AND t.tginitdeferred = @initially_deferred
            """, (NpgsqlConnection)db.Database.GetDbConnection());
        command.Parameters.AddWithValue("name", trigger);
        command.Parameters.AddWithValue("deferrable", deferrable);
        command.Parameters.AddWithValue("initially_deferred", initiallyDeferred);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
    }

    private static async Task<bool> FunctionExistsAsync(EdgeRetailsDbContext db, string function)
    {
        await db.Database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*) FROM pg_proc p
            JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = 'warranty' AND p.proname = @name
            """, (NpgsqlConnection)db.Database.GetDbConnection());
        command.Parameters.AddWithValue("name", function);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) >= 1;
    }

    private static async Task<HashSet<string>> CatalogNamesAsync(NpgsqlConnection connection, string sql)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }
        return names;
    }

    private static async Task WithOwnedDatabaseAsync(Func<EdgeRetailsDbContext, Task> test)
    {
        await using var attested = Phase2PostgresTestHarness.BuildProvider();
        var name = "edge_retails_warranty_migration_" + Guid.NewGuid().ToString("N");
        var builder = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB"))
        {
            Database = "postgres"
        };
        await using var admin = new NpgsqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {name}", admin))
        {
            await create.ExecuteNonQueryAsync();
        }
        try
        {
            builder.Database = name;
            await using var db = new EdgeRetailsDbContext(new DbContextOptionsBuilder<EdgeRetailsDbContext>()
                .UseNpgsql(builder.ConnectionString, x => x.MigrationsHistoryTable("__ef_migrations_history", "system")).Options);
            await test(db);
        }
        finally
        {
            // Generated name is scoped to the attested disposable runner, never an operator database.
            await using var drop = new NpgsqlCommand($"DROP DATABASE {name} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
