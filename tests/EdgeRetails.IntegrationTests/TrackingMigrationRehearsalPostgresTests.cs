using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;
using Xunit.Abstractions;

namespace EdgeRetails.IntegrationTests;

[Collection("TrackingManufacturerIdentityPg")]
public sealed class TrackingMigrationRehearsalPostgresTests(ITestOutputHelper output)
{
    private const string TrackingMigration = "20261002101709_TrackingManufacturerIdentityAuthorityV1";

    [Fact]
    public async Task FromZero_AllMigrationsAndTrackingDatabaseConstraintsMatchModel()
    {
        await WithOwnedDatabaseAsync(async db =>
        {
            var expected = db.Database.GetMigrations().ToArray();
            await db.Database.MigrateAsync();
            Assert.Equal(expected, (await db.Database.GetAppliedMigrationsAsync()).ToArray());
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            foreach (var migration in expected)
            {
                output.WriteLine("APPLIED " + migration);
            }
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            await db.Database.OpenConnectionAsync();
            await using var indexes = new NpgsqlCommand("SELECT schemaname || '.' || indexname FROM pg_indexes", connection);
            var actualIndexes = new HashSet<string>(StringComparer.Ordinal);
            await using (var reader = await indexes.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    actualIndexes.Add(reader.GetString(0));
                }
            }
            await using var constraints = new NpgsqlCommand("SELECT n.nspname || '.' || c.conname FROM pg_constraint c JOIN pg_namespace n ON n.oid=c.connamespace", connection);
            var actualConstraints = new HashSet<string>(StringComparer.Ordinal);
            await using (var reader = await constraints.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    actualConstraints.Add(reader.GetString(0));
                }
            }
            var missing = new List<string>();
            foreach (var table in db.GetService<IDesignTimeModel>().Model.GetRelationalModel().Tables)
            {
                foreach (var index in table.Indexes)
                {
                    var key = table.Schema + "." + index.Name;
                    if (!actualIndexes.Contains(key))
                    {
                        missing.Add("INDEX " + key);
                    }
                }
                foreach (var foreignKey in table.ForeignKeyConstraints)
                {
                    var key = table.Schema + "." + foreignKey.Name;
                    if (!actualConstraints.Contains(key))
                    {
                        missing.Add("FK " + key);
                    }
                }
                foreach (var check in table.CheckConstraints)
                {
                    var key = table.Schema + "." + check.Name;
                    if (!actualConstraints.Contains(key))
                    {
                        missing.Add("CHECK " + key);
                    }
                }
            }
            Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
            Assert.Contains("inventory.ix_unit_identity_claims_identifier_type_normalized_value", actualIndexes);
            Assert.Contains("inventory.ix_unit_identity_claims_inventory_unit_id_identifier_slot", actualIndexes);
            Assert.Contains("inventory.fk_unit_identity_claims_units_inventory_unit_id", actualConstraints);
            Assert.Contains("inventory.pk_unit_identity_ownership", actualIndexes);
            Assert.Contains("inventory.ix_unit_identity_ownership_inventory_unit_identity_claim_id", actualIndexes);
            Assert.Contains("inventory.fk_unit_identity_ownership_unit_identity_claims_inventory_unit~", actualConstraints);
        });
    }

    [Fact]
    public async Task OwnershipUpgrade_BackfillsOnlyNonReceiptVoidedStatusesAndRetainsHistory()
    {
        await WithOwnedDatabaseAsync(async db =>
        {
            var migrations = db.Database.GetMigrations().ToArray();
            var previous = migrations[Array.IndexOf(migrations, TrackingMigration) - 1];
            await db.GetService<IMigrator>().MigrateAsync(previous);
            var seed = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);
            var adjustment = new StockAdjustment
            {
                AdjustmentNumber = "OWNER-BACKFILL",
                ActorId = seed.ActorId,
                CorrelationId = Guid.NewGuid(),
                CreatedAt = DateTimeOffset.UtcNow,
                OccurredAt = DateTimeOffset.UtcNow
            };
            var item = new StockAdjustmentItem
            {
                StockAdjustmentId = adjustment.Id,
                ProductId = seed.ProductId,
                BaseQuantity = 6m
            };
            db.StockAdjustments.Add(adjustment);
            db.StockAdjustmentItems.Add(item);
            await db.SaveChangesAsync();

            var statuses = new[]
            {
                InventoryUnitStatus.ReceiptVoided,
                InventoryUnitStatus.SupplierReturned,
                InventoryUnitStatus.Sold,
                InventoryUnitStatus.Missing,
                InventoryUnitStatus.WarrantyCustomerHeld,
                InventoryUnitStatus.Scrapped
            };
            var units = statuses.Select((status, index) => new InventoryUnit
            {
                ProductId = seed.ProductId,
                OriginType = InventoryUnitOriginType.StockAdjustment,
                SourceStockAdjustmentItemId = item.Id,
                SerialNumber = "OWNERSHIP-" + index + "-" + Guid.NewGuid().ToString("N"),
                Status = status,
                CreatedAt = DateTimeOffset.UtcNow,
                Version = 1
            }).ToArray();
            db.InventoryUnits.AddRange(units);
            await db.SaveChangesAsync();
            var evidenceBefore = units.Select(x => (x.Id, x.SerialNumber)).ToArray();
            db.ChangeTracker.Clear();

            await db.Database.MigrateAsync();
            Assert.Equal(statuses.Length,
                await db.InventoryUnitIdentityClaims.CountAsync(x => x.IdentifierType == ManufacturerIdentifierType.Serial));
            Assert.Equal(statuses.Length - 1, await db.InventoryUnitIdentityOwnerships.CountAsync());
            foreach (var unit in units)
            {
                var claim = await db.InventoryUnitIdentityClaims.SingleAsync(x => x.InventoryUnitId == unit.Id);
                var hasOwner = await db.InventoryUnitIdentityOwnerships.AnyAsync(x =>
                    x.InventoryUnitIdentityClaimId == claim.Id);
                Assert.Equal(unit.Status != InventoryUnitStatus.ReceiptVoided, hasOwner);
            }
            var evidenceAfter = await db.InventoryUnits.AsNoTracking().OrderBy(x => x.Id)
                .Select(x => new { x.Id, x.SerialNumber }).ToArrayAsync();
            Assert.Equal(evidenceBefore.OrderBy(x => x.Id), evidenceAfter.Select(x => (x.Id, x.SerialNumber)));
        });
    }

    [Fact]
    public async Task OwnershipUpgrade_FailsClosedOnActiveSerialVersusImeiCollision()
    {
        await WithOwnedDatabaseAsync(async db =>
        {
            var migrations = db.Database.GetMigrations().ToArray();
            var trackingIndex = Array.IndexOf(migrations, TrackingMigration);
            var previous = migrations[trackingIndex - 1];
            await db.GetService<IMigrator>().MigrateAsync(previous);
            var seed = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);
            var adjustment = new StockAdjustment
            {
                AdjustmentNumber = "CROSS-TYPE-COLLISION",
                ActorId = seed.ActorId,
                CorrelationId = Guid.NewGuid(),
                CreatedAt = DateTimeOffset.UtcNow,
                OccurredAt = DateTimeOffset.UtcNow
            };
            var item = new StockAdjustmentItem
            {
                StockAdjustmentId = adjustment.Id,
                ProductId = seed.ProductId,
                BaseQuantity = 2m
            };
            db.StockAdjustments.Add(adjustment);
            db.StockAdjustmentItems.Add(item);
            await db.SaveChangesAsync();
            var shared = "860123456789012";
            db.InventoryUnits.AddRange(
                new InventoryUnit
                {
                    ProductId = seed.ProductId,
                    OriginType = InventoryUnitOriginType.StockAdjustment,
                    SourceStockAdjustmentItemId = item.Id,
                    SerialNumber = shared,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Version = 1
                },
                new InventoryUnit
                {
                    ProductId = seed.ProductId,
                    OriginType = InventoryUnitOriginType.StockAdjustment,
                    SourceStockAdjustmentItemId = item.Id,
                    Imei1 = "860-123-456-789-012",
                    CreatedAt = DateTimeOffset.UtcNow,
                    Version = 1
                });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            await db.GetService<IMigrator>().MigrateAsync(TrackingMigration);
            var failure = await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync());
            Assert.Contains("ReceiptVoid ownership migration blocked:", failure.MessageText);
            Assert.Contains(TrackingMigration, await db.Database.GetAppliedMigrationsAsync());
            Assert.DoesNotContain(migrations[^1], await db.Database.GetAppliedMigrationsAsync());
            await db.Database.OpenConnectionAsync();
            await using var tableExists = new NpgsqlCommand(
                "SELECT to_regclass('inventory.unit_identity_ownership') IS NULL",
                (NpgsqlConnection)db.Database.GetDbConnection());
            Assert.Equal(true, await tableExists.ExecuteScalarAsync());
            Assert.Equal(2, await db.InventoryUnits.CountAsync());
        });
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("serial-collision")]
    [InlineData("serial-control")]
    [InlineData("serial-format")]
    [InlineData("serial-unicode")]
    [InlineData("imei-invalid")]
    [InlineData("imei-cross-slot")]
    [InlineData("imei-same-unit")]
    public async Task LegacyUpgrade_ActualPreTrackingSchemaBackfillsOrRollsBackFailClosed(string scenario)
    {
        await WithOwnedDatabaseAsync(async db =>
        {
            var migrations = db.Database.GetMigrations().ToArray();
            var previous = migrations[Array.IndexOf(migrations, TrackingMigration) - 1];
            output.WriteLine("LEGACY_CHECKPOINT " + previous);
            await db.GetService<IMigrator>().MigrateAsync(previous);
            var seed = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);
            var adjustment = new StockAdjustment { AdjustmentNumber = "LEGACY", ActorId = seed.ActorId,
                CorrelationId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow, OccurredAt = DateTimeOffset.UtcNow };
            var item = new StockAdjustmentItem { StockAdjustmentId = adjustment.Id, ProductId = seed.ProductId, BaseQuantity = 1m };
            db.StockAdjustments.Add(adjustment);
            db.StockAdjustmentItems.Add(item);
            await db.SaveChangesAsync();
            var legacy = new List<InventoryUnit>();
            void Add(string? serial, string? imei1 = null, string? imei2 = null) => legacy.Add(new InventoryUnit
            {
                ProductId = seed.ProductId, OriginType = InventoryUnitOriginType.StockAdjustment,
                SourceStockAdjustmentItemId = item.Id, SerialNumber = serial, Imei1 = imei1, Imei2 = imei2,
                CreatedAt = DateTimeOffset.UtcNow, Version = 1
            });
            switch (scenario)
            {
                case "valid":
                    Add(" \t\u00A0 ");
                    Add("\tab-c12\t", "860-123-456-789-01", "86012345678902");
                    break;
                case "serial-collision": Add(" ab-c "); Add("AB-C"); break;
                case "serial-control": Add("AB\u0001C"); break;
                case "serial-format": Add("AB\u200BC"); break;
                case "serial-unicode": Add("ａｂ-c"); break;
                case "imei-invalid": Add(null, "invalid"); break;
                case "imei-cross-slot": Add(null, "86012345678901"); Add(null, null, "860-123-456-789-01"); break;
                case "imei-same-unit": Add(null, "86012345678901", "860-123-456-789-01"); break;
            }
            db.InventoryUnits.AddRange(legacy); // Deliberate historical fixtures, before claims exist.
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            if (scenario == "valid")
            {
                await db.Database.MigrateAsync();
                var claims = await db.InventoryUnitIdentityClaims.OrderBy(x => x.IdentifierSlot).ToListAsync();
                Assert.Equal(3, claims.Count);
                Assert.All(claims, x => Assert.Equal(legacy[1].Id, x.InventoryUnitId));
                Assert.All(claims, x => Assert.Equal(1, x.NormalizationVersion));
                Assert.Equal(ManufacturerIdentifierSlot.Serial, claims[0].IdentifierSlot);
                Assert.Equal("\tab-c12\t", claims[0].RawValue);
                Assert.Equal(IdentityNormalizationRules.NormalizeSerialNumber(claims[0].RawValue), claims[0].NormalizedValue);
                Assert.Equal(ManufacturerIdentifierSlot.Imei1, claims[1].IdentifierSlot);
                Assert.Equal("860-123-456-789-01", claims[1].RawValue);
                Assert.Equal("86012345678901", claims[1].NormalizedValue);
                Assert.Equal(ManufacturerIdentifierSlot.Imei2, claims[2].IdentifierSlot);
            }
            else
            {
                var failure = await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync());
                Assert.Contains("Tracking manufacturer identity cutover blocked:", failure.MessageText);
                Assert.DoesNotContain(TrackingMigration, await db.Database.GetAppliedMigrationsAsync());
                await db.Database.OpenConnectionAsync();
                await using var exists = new NpgsqlCommand("SELECT to_regclass('inventory.unit_identity_claims') IS NULL", (NpgsqlConnection)db.Database.GetDbConnection());
                Assert.Equal(true, await exists.ExecuteScalarAsync());
            }
            var retained = await db.InventoryUnits.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
            Assert.Equal(legacy.OrderBy(x => x.Id).Select(x => (x.Id, x.SerialNumber, x.Imei1, x.Imei2)),
                retained.Select(x => (x.Id, x.SerialNumber, x.Imei1, x.Imei2)));
        });
    }

    private static async Task WithOwnedDatabaseAsync(Func<EdgeRetailsDbContext, Task> test)
    {
        await using var attested = Phase2PostgresTestHarness.BuildProvider();
        var name = "edge_retails_tracking_rehearsal_" + Guid.NewGuid().ToString("N");
        var builder = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB")) { Database = "postgres" };
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
            // Name is generated locally after server/root attestation. No arbitrary database is accepted.
            await using var drop = new NpgsqlCommand($"DROP DATABASE {name} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
