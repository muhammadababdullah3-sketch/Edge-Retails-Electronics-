using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EdgeRetails.Application.Features.Catalog;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Infrastructure.Persistence;
using EdgeRetails.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace EdgeRetails.IntegrationTests.Phase11;

/// <summary>Catalog V3 integration tests. All connections require an explicitly attested owned PostgreSQL 18 target.</summary>
[Collection("Phase11CatalogSeed")]
public sealed class CatalogV3SeedLifecyclePostgresTests
{
    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("FAIL-CLOSED: EDGE_RETAILS_TEST_DB must select an owned disposable PostgreSQL 18 target.");
        }

        Phase11DurablePostingIntegrationTests.AssertDisposablePostgresTargetGuard(connectionString, performLiveProbe: true);
        return connectionString;
    }

    private static EdgeRetailsDbContext CreateDb(string connectionString)
    {
        var options = new DbContextOptionsBuilder<EdgeRetailsDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "system"))
            .Options;
        return new EdgeRetailsDbContext(options);
    }

    [Fact]
    public async Task CAT107_DisposableGuardRejectsProductionAndConfirmsOwnedPostgres18()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Phase11DurablePostingIntegrationTests.AssertDisposablePostgresTargetGuard(
                "Host=127.0.0.1;Port=5432;Database=edgeretails_test_bad;Username=er_provisioner;Password=not-used",
                performLiveProbe: false));

        var connectionString = GetConnectionString();
        await using var db = CreateDb(connectionString);
        await db.Database.OpenConnectionAsync();
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await using var version = new NpgsqlCommand("SHOW server_version_num", connection);
        var versionNumber = int.Parse((string)(await version.ExecuteScalarAsync())!);
        Assert.InRange(versionNumber, 180000, 189999);
    }

    [Fact]
    public async Task ProductionMigrations_CreateCanonicalSchemaAndClassRefConstraint()
    {
        var connectionString = GetConnectionString();
        await using var db = CreateDb(connectionString);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261021120000_Phase11CatalogCanonicalIntegration");
        await migrator.MigrateAsync("20261021160049_Phase11ThakaCreditRefundTable");
        await migrator.MigrateAsync("20261021160100_Phase11CatalogV3RelationalIntegrity");

        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.Contains("20261021160049_Phase11ThakaCreditRefundTable", applied);
        Assert.Contains("20261021160100_Phase11CatalogV3RelationalIntegrity", applied);

        await db.Database.OpenConnectionAsync();
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await using var fk = new NpgsqlCommand("""
            SELECT count(*) FROM pg_constraint c
            JOIN pg_namespace child_ns ON child_ns.oid=c.connamespace
            JOIN pg_class child_table ON child_table.oid=c.conrelid
            JOIN pg_class parent_table ON parent_table.oid=c.confrelid
            JOIN pg_namespace parent_ns ON parent_ns.oid=parent_table.relnamespace
            WHERE c.contype='f' AND child_ns.nspname='catalog' AND child_table.relname='products'
              AND parent_ns.nspname='catalog_v3' AND parent_table.relname='product_classes'
              AND c.confdeltype='r'
            """, connection);
        Assert.Equal(1, Convert.ToInt32(await fk.ExecuteScalarAsync()));
    }

    /// <summary>
    /// Negative control for the dry-run no-write oracle. A green assertion is meaningless unless the
    /// oracle can actually observe a mutation, so this proves the persistent-state fingerprint changes
    /// when an insert is committed and returns to the pristine digest once it is rolled back.
    /// </summary>
    [Fact]
    public async Task DryRun_NoWriteOracle_IsNotVacuous_DetectsCommittedMutation()
    {
        var connectionString = GetConnectionString();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        string pristine = await TaxonomyV3Importer.CapturePersistentStateFingerprintAsync(conn, null, CancellationToken.None);

        // A read-only repeat of the capture must be stable; otherwise the oracle is noisy and any
        // "unchanged" verdict would be meaningless.
        string repeat = await TaxonomyV3Importer.CapturePersistentStateFingerprintAsync(conn, null, CancellationToken.None);
        Assert.Equal(pristine, repeat);

        await using (var tx = await conn.BeginTransactionAsync())
        {
            await using var insert = new NpgsqlCommand(
                "INSERT INTO catalog_v3.divisions(code, name) VALUES('ZZ-ORACLE-PROBE','Oracle Probe')", conn, tx);
            await insert.ExecuteNonQueryAsync();
            await tx.CommitAsync();
        }

        string afterWrite = await TaxonomyV3Importer.CapturePersistentStateFingerprintAsync(conn, null, CancellationToken.None);
        Assert.NotEqual(pristine, afterWrite);

        await using (var cleanup = new NpgsqlCommand(
            "DELETE FROM catalog_v3.divisions WHERE code='ZZ-ORACLE-PROBE'", conn))
        {
            Assert.Equal(1, await cleanup.ExecuteNonQueryAsync());
        }

        string restored = await TaxonomyV3Importer.CapturePersistentStateFingerprintAsync(conn, null, CancellationToken.None);
        Assert.Equal(pristine, restored);
    }

    [Fact]
    public async Task DryRun_WritesZeroBytes_And_CalculatesAccurateDiff()
    {
        var importer = new TaxonomyV3Importer(GetConnectionString());
        var dryRun = await importer.ExecuteDryRunAsync("ER-RUN-CATALOG-CORRECTION-20261021-107");

        Assert.Equal("APPLICATION_DRY_RUN", dryRun.ExecutionMode);

        // Authoritative oracles: the session really was READ ONLY for the whole measured window,
        // and persistent state (schema surface, FKs, sequences, full rows) is byte-identical.
        Assert.True(dryRun.ReadOnlyTransactionVerified,
            $"Session did not hold a read-only transaction (violations={string.Join(";", dryRun.InvariantViolations)})");
        Assert.True(dryRun.PersistentStateUnchanged,
            $"Persistent state changed during the dry run (violations={string.Join(";", dryRun.InvariantViolations)})");
        Assert.True(dryRun.NoWriteVerified,
            $"NoWriteVerified is derived from the two oracles above (violations={string.Join(";", dryRun.InvariantViolations)})");

        // LSN is explicitly diagnostic only: cluster-wide WAL can advance from unrelated activity,
        // hint-bit logging or checkpointing. It is reported, never asserted as proof.
        _ = dryRun.LsnUnchangedDiagnostic;

        Assert.True(dryRun.IsSafeToApply, string.Join(",", dryRun.InvariantViolations));
        Assert.Equal(9, SumRows(dryRun, "divisions"));
        Assert.Equal(65, SumRows(dryRun, "departments"));
        Assert.Equal(732, SumRows(dryRun, "product_classes"));
        Assert.Equal(42, SumRows(dryRun, "class_redirects"));
        Assert.Equal(0, dryRun.PlannedDeletes);
        Assert.Equal(64, dryRun.ManifestSha256.Length);
        Assert.Equal(64, dryRun.TargetFingerprint.Length);
    }

    private static int SumRows(TaxonomyDryRunResult result, string entity) =>
        result.PlannedTaxonomyOperations[$"{entity}_to_insert"] +
        result.PlannedTaxonomyOperations[$"{entity}_to_update"] +
        result.PlannedTaxonomyOperations[$"{entity}_unchanged"];

    [Fact]
    public async Task FirstRun_And_RepeatRunIdempotency_PreservesExactCounts()
    {
        var importer = new TaxonomyV3Importer(GetConnectionString());
        var seed1 = await importer.ApplySeedAsync();
        Assert.True(seed1.Success);
        Assert.True(seed1.PreviewApplyParityAchieved);

        var seed2 = await importer.ApplySeedAsync();
        Assert.True(seed2.Success);
        Assert.Equal(0, seed2.ActualDeletes);
        Assert.True(seed2.PreviewApplyParityAchieved);
    }

    [Fact]
    public async Task ProductClassForeignKey_RejectsInvalidClassAndReferencedDelete()
    {
        var connectionString = GetConnectionString();
        var importer = new TaxonomyV3Importer(connectionString);
        await importer.ApplySeedAsync();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using (var ensureUnit = new NpgsqlCommand("""
            INSERT INTO catalog.units (id, name, symbol, dimension, factor_to_reference, conversion_kind, is_active, display_decimal_places)
            VALUES ('00000000-0000-0000-0000-000000000001', 'Piece', 'PCS', 1, 1.0, 'FIXED', true, 0)
            ON CONFLICT (id) DO NOTHING;
            """, connection))
        {
            await ensureUnit.ExecuteNonQueryAsync();
        }

        var classRef = "A01-01";
        var productId = Guid.NewGuid();
        await using (var insertProduct = new NpgsqlCommand("""
            INSERT INTO catalog.products(id, name, base_unit_id, tracking_mode, serial_tracking_enabled,
                imei_tracking_enabled, reference_purchase_cost, default_sale_price, minimum_stock_level,
                default_warranty_months, attributes_schema_version, is_active, version, class_ref)
            SELECT @id, 'CAT107 FK fixture', u.id, 1, false, false, NULL, 0, 0, 0, 1, true, 1, @class_ref
            FROM catalog.units u ORDER BY u.id LIMIT 1
            """, connection))
        {
            insertProduct.Parameters.AddWithValue("id", productId);
            insertProduct.Parameters.AddWithValue("class_ref", "INVALID-CLASS-REF");
            var invalidReference = await Assert.ThrowsAsync<PostgresException>(() => insertProduct.ExecuteNonQueryAsync());
            Assert.Equal("23503", invalidReference.SqlState);
        }

        await using (var insertProduct = new NpgsqlCommand("""
            INSERT INTO catalog.products(id, name, base_unit_id, tracking_mode, serial_tracking_enabled,
                imei_tracking_enabled, reference_purchase_cost, default_sale_price, minimum_stock_level,
                default_warranty_months, attributes_schema_version, is_active, version, class_ref)
            SELECT @id, 'CAT107 valid FK fixture', u.id, 1, false, false, NULL, 0, 0, 0, 1, true, 1, @class_ref
            FROM catalog.units u ORDER BY u.id LIMIT 1
            """, connection))
        {
            insertProduct.Parameters.AddWithValue("id", productId);
            insertProduct.Parameters.AddWithValue("class_ref", classRef);
            Assert.Equal(1, await insertProduct.ExecuteNonQueryAsync());
        }

        await using var deleteClass = new NpgsqlCommand("DELETE FROM catalog_v3.product_classes WHERE class_ref=@class_ref", connection);
        deleteClass.Parameters.AddWithValue("class_ref", classRef);
        var restricted = await Assert.ThrowsAsync<PostgresException>(() => deleteClass.ExecuteNonQueryAsync());
        Assert.True(restricted.SqlState is "23503" or "23001", $"Expected 23503 or 23001, got {restricted.SqlState}");

        await using var cleanup = new NpgsqlCommand("DELETE FROM catalog.products WHERE id=@id", connection);
        cleanup.Parameters.AddWithValue("id", productId);
        Assert.Equal(1, await cleanup.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task DestructiveRollbackAgainstReferencedOperationalData_IsRejected()
    {
        var connectionString = GetConnectionString();
        await new TaxonomyV3Importer(connectionString).ApplySeedAsync();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using (var ensureUnit = new NpgsqlCommand("""
            INSERT INTO catalog.units (id, name, symbol, dimension, factor_to_reference, conversion_kind, is_active, display_decimal_places)
            VALUES ('00000000-0000-0000-0000-000000000001', 'Piece', 'PCS', 1, 1.0, 'FIXED', true, 0)
            ON CONFLICT (id) DO NOTHING;
            """, connection))
        {
            await ensureUnit.ExecuteNonQueryAsync();
        }

        var productId = Guid.NewGuid();
        await using var insert = new NpgsqlCommand("""
            INSERT INTO catalog.products(id, name, base_unit_id, tracking_mode, serial_tracking_enabled,
                imei_tracking_enabled, reference_purchase_cost, default_sale_price, minimum_stock_level,
                default_warranty_months, attributes_schema_version, is_active, version, class_ref)
            SELECT @id, 'CAT107 rollback fixture', u.id, 1, false, false, NULL, 0, 0, 0, 1, true, 1, 'A01-01'
            FROM catalog.units u ORDER BY u.id LIMIT 1
            """, connection);
        insert.Parameters.AddWithValue("id", productId);
        Assert.Equal(1, await insert.ExecuteNonQueryAsync());
        var rejected = await new TaxonomyV3Importer(connectionString).ValidateDestructiveRollbackRejectionAsync("A01-01");
        Assert.True(rejected);
        await using var cleanup = new NpgsqlCommand("DELETE FROM catalog.products WHERE id=@id", connection);
        cleanup.Parameters.AddWithValue("id", productId);
        Assert.Equal(1, await cleanup.ExecuteNonQueryAsync());
    }
}
