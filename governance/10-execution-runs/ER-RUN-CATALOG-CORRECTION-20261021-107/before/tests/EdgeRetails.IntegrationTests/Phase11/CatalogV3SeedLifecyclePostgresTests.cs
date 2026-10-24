using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EdgeRetails.Application.Features.Catalog;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Infrastructure.Services;
using Npgsql;
using Xunit;

namespace EdgeRetails.IntegrationTests.Phase11;

/// <summary>
/// Comprehensive Postgres integration tests for Catalog V3 Seed Lifecycle, Upgrade, Concurrency, and Conservation.
/// Satisfies P11-IMP-040 and P11-IMP-041 under Authority OWNER-UPDATE-20261018-004.
/// Strictly executed against owned disposable PostgreSQL 18 on dedicated loopback port 55437.
/// </summary>
[Collection("Phase11CatalogSeed")]
public sealed class CatalogV3SeedLifecyclePostgresTests
{
    private static string GetConnectionString()
    {
        var envConn = Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB");
        if (!string.IsNullOrWhiteSpace(envConn))
        {
            Phase11DurablePostingIntegrationTests.AssertDisposablePostgresTargetGuard(envConn, performLiveProbe: false);
            return envConn;
        }

        var clusterDir = @"C:\Users\muham\EdgeRetailsDisposable\ER-RUN-20261011-005";
        var credFile = Path.Combine(clusterDir, "provpw.tmp");
        if (File.Exists(credFile))
        {
            var pw = File.ReadAllText(credFile).Trim();
            if (!string.IsNullOrWhiteSpace(pw))
            {
                var connStr = $"Host=127.0.0.1;Port=55437;Database=edgeretails_test_run20261012_001;Username=er_provisioner;Password={pw}";
                Phase11DurablePostingIntegrationTests.AssertDisposablePostgresTargetGuard(connStr, performLiveProbe: false);
                return connStr;
            }
        }

        throw new InvalidOperationException("FAIL-CLOSED: Disposable PostgreSQL 18 connection string could not be resolved.");
    }

    [Fact]
    public async Task DryRun_WritesZeroBytes_And_CalculatesAccurateDiff()
    {
        var connStr = GetConnectionString();
        var importer = new TaxonomyV3Importer(connStr);

        var dryRun = await importer.ExecuteDryRunAsync("ER-RUN-20261018-034");

        Assert.Equal("APPLICATION_DRY_RUN", dryRun.ExecutionMode);
        Assert.Equal("SAFE_FOR_IDEMPOTENT_SEED", dryRun.Verdict);
        Assert.True(dryRun.IsSafeToApply);
        Assert.True(dryRun.NoWriteVerified);
        Assert.True(dryRun.OccupiedSymbolsPreserved);
        Assert.True(dryRun.SafetyInvariantsEvaluated["occupied_symbol_present_in_baseline"]);
        Assert.Equal(9, dryRun.PlannedTaxonomyOperations["divisions_to_upsert"]);
        Assert.Equal(65, dryRun.PlannedTaxonomyOperations["departments_to_upsert"]);
        Assert.Equal(732, dryRun.PlannedTaxonomyOperations["product_classes_to_upsert"]);
        Assert.Equal(42, dryRun.PlannedTaxonomyOperations["class_redirects_to_upsert"]);
        Assert.Equal(0, dryRun.PlannedDeletes);

        var outDir = @"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-20261018-034\seed";
        Directory.CreateDirectory(outDir);
        var jsonOptions = new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower
        };
        var json = System.Text.Json.JsonSerializer.Serialize(dryRun, jsonOptions);
        File.WriteAllText(Path.Combine(outDir, "DRY_RUN_DIFF.json"), json);
    }

    [Fact]
    public async Task FirstRun_And_RepeatRunIdempotency_PreservesExactCounts()
    {
        var connStr = GetConnectionString();
        var importer = new TaxonomyV3Importer(connStr);

        // First Run
        var seed1 = await importer.ApplySeedAsync();
        Assert.True(seed1.Success);
        Assert.Equal("APPLICATION_SEED_APPLY", seed1.ExecutionMode);
        Assert.Equal(9, seed1.DivisionsUpserted);
        Assert.Equal(65, seed1.DepartmentsUpserted);
        Assert.Equal(732, seed1.ProductClassesUpserted);
        Assert.Equal(42, seed1.ClassRedirectsUpserted);
        Assert.True(seed1.PreviewApplyParityAchieved);

        // Repeat Run (Idempotency)
        var seed2 = await importer.ApplySeedAsync();
        Assert.True(seed2.Success);
        Assert.Equal(9, seed2.DivisionsUpserted);
        Assert.Equal(65, seed2.DepartmentsUpserted);
        Assert.Equal(732, seed2.ProductClassesUpserted);
        Assert.Equal(42, seed2.ClassRedirectsUpserted);
        Assert.Equal(0, seed2.ActualDeletes);
    }

    [Fact]
    public async Task UpgradeScenario_AppliesNewClasses_And_PreservesHistory()
    {
        var connStr = GetConnectionString();
        var importer = new TaxonomyV3Importer(connStr);

        var extraClasses = new List<ProductClass>
        {
            new ProductClass("A01-UPG-01", "Subsea & Marine Flexible Power Cable", "A", "A01", "LEAF", "ACTIVE_PROPOSAL", "LEAF")
        };

        var extraRedirects = new List<ClassRedirect>
        {
            new ClassRedirect("LEG-A01-SUBSEA", "Legacy Subsea Cable", "A01-UPG-01", "Subsea & Marine Flexible Power Cable", "UPGRADE", "CANONICAL_REDIRECT", "ACTIVE")
        };

        var upgradeResult = await importer.ApplySeedAsync(new TaxonomySeedOptions(ExtraClasses: extraClasses, ExtraRedirects: extraRedirects));
        Assert.True(upgradeResult.Success);
        Assert.Equal(733, upgradeResult.ProductClassesUpserted);
        Assert.Equal(43, upgradeResult.ClassRedirectsUpserted);

        // Clean up test fixtures to restore canonical state
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("DELETE FROM catalog_v3.class_redirects WHERE legacy_class_ref = 'LEG-A01-SUBSEA'; DELETE FROM catalog_v3.product_classes WHERE class_ref = 'A01-UPG-01';", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task OccupiedSymbolCollision_PreservesOccupiedOperationalSymbols_FailClosed()
    {
        var connStr = GetConnectionString();
        var importer = new TaxonomyV3Importer(connStr);

        bool collisionHandled = await importer.ValidateOccupiedSymbolCollisionAsync("GA4BC");
        Assert.True(collisionHandled, "Collision on occupied symbol GA4BC must fail closed with SQLSTATE 23505.");
    }

    [Fact]
    public async Task MidFlightInterruption_RecoversCleanly_And_Resumable()
    {
        var connStr = GetConnectionString();
        var importer = new TaxonomyV3Importer(connStr);

        // Execute simulated interruption mid-stream
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await importer.ApplySeedAsync(new TaxonomySeedOptions(SimulateInterruption: true));
        });

        // Prove clean resumability and recovery
        var resumeResult = await importer.ApplySeedAsync();
        Assert.True(resumeResult.Success);
        Assert.Equal(732, resumeResult.ProductClassesUpserted);
        Assert.Equal(42, resumeResult.ClassRedirectsUpserted);
    }

    [Fact]
    public async Task ConcurrentSeeders_HandleAdvisoryLocks_WithoutDeadlock()
    {
        var connStr = GetConnectionString();
        var importer1 = new TaxonomyV3Importer(connStr);
        var importer2 = new TaxonomyV3Importer(connStr);
        var importer3 = new TaxonomyV3Importer(connStr);

        // Run 3 competing seeders concurrently
        var task1 = Task.Run(() => importer1.ApplySeedAsync());
        var task2 = Task.Run(() => importer2.ApplySeedAsync());
        var task3 = Task.Run(() => importer3.ApplySeedAsync());

        var results = await Task.WhenAll(task1, task2, task3);

        foreach (var r in results)
        {
            Assert.True(r.Success);
            Assert.Equal(732, r.ProductClassesUpserted);
            Assert.Equal(42, r.ClassRedirectsUpserted);
        }
    }

    [Fact]
    public async Task ActiveRepair_RestoresMutatedAttributes_ToCanonical()
    {
        var connStr = GetConnectionString();
        var importer = new TaxonomyV3Importer(connStr);

        // 1. Mutate department A01 description
        const string mutatedName = "MUTATED_TEST_NAME_FOR_REPAIR";
        await importer.MutateDepartmentNameForRepairTestAsync("A01", mutatedName);

        // Verify mutation applied
        await using (var conn = new NpgsqlConnection(connStr))
        {
            await conn.OpenAsync();
            await using var verifyCmd = new NpgsqlCommand("SELECT name FROM catalog_v3.departments WHERE code = 'A01';", conn);
            var readBack = (string?)await verifyCmd.ExecuteScalarAsync();
            Assert.Equal(mutatedName, readBack);
        }

        // 2. Re-apply seed to repair
        var repairResult = await importer.ApplySeedAsync();
        Assert.True(repairResult.Success);

        // 3. Verify department A01 restored to canonical name
        await using (var conn = new NpgsqlConnection(connStr))
        {
            await conn.OpenAsync();
            await using var verifyCmd = new NpgsqlCommand("SELECT name FROM catalog_v3.departments WHERE code = 'A01';", conn);
            var readBack = (string?)await verifyCmd.ExecuteScalarAsync();
            Assert.Equal("Building Wires & Power Cables", readBack);
        }
    }

    [Fact]
    public async Task ForeignKeyFailure_RollsBackAtomically_LeavingZeroDirtyRows()
    {
        var connStr = GetConnectionString();

        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        int initialRedirectCount;
        await using (var countCmd = new NpgsqlCommand("SELECT count(*) FROM catalog_v3.class_redirects;", conn))
        {
            initialRedirectCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync());
        }

        // Attempt invalid redirect to non-existent class
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            const string malformedSql = @"
                INSERT INTO catalog_v3.class_redirects (legacy_class_ref, legacy_label, canonical_class_ref, canonical_label, reason, migration_policy, status)
                VALUES ('MALFORMED_TEST_034', 'Malformed Label', 'NON_EXIST_REF_99', 'Non-existent', 'TEST', 'POLICY', 'FAIL');";

            await using var cmd = new NpgsqlCommand(malformedSql, conn, tx);
            await cmd.ExecuteNonQueryAsync();
            await tx.CommitAsync();
            Assert.Fail("Expected foreign key violation SQLSTATE 23503.");
        }
        catch (PostgresException ex) when (ex.SqlState == "23503")
        {
            await tx.RollbackAsync();
        }

        // Verify zero dirty rows remain
        int postRedirectCount;
        await using (var countCmd = new NpgsqlCommand("SELECT count(*) FROM catalog_v3.class_redirects;", conn))
        {
            postRedirectCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync());
        }

        Assert.Equal(initialRedirectCount, postRedirectCount);
    }

    [Fact]
    public async Task DestructiveRollbackAgainstReferencedOperationalData_IsRejected()
    {
        var connStr = GetConnectionString();
        var importer = new TaxonomyV3Importer(connStr);

        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        // 1. Ensure production schema and foreign key constraints are established
        await importer.EnsureSchemaAsync(conn);

        // Link first product to A01-01
        const string linkSql = @"
            UPDATE catalog.products
            SET class_ref = 'A01-01'
            WHERE id = (SELECT id FROM catalog.products ORDER BY id LIMIT 1);";

        await using var linkCmd = new NpgsqlCommand(linkSql, conn);
        await linkCmd.ExecuteNonQueryAsync();

        // 2. Attempt destructive deletion of referenced class A01-01
        bool rejected = await importer.ValidateDestructiveRollbackRejectionAsync("A01-01");
        Assert.True(rejected, "Destructive rollback of taxonomy class referenced by operational products must be rejected with SQLSTATE 23503.");

        // 3. Clean up operational fixture link
        const string unlinkSql = @"
            UPDATE catalog.products
            SET class_ref = NULL
            WHERE class_ref = 'A01-01';";

        await using var unlinkCmd = new NpgsqlCommand(unlinkSql, conn);
        await unlinkCmd.ExecuteNonQueryAsync();
    }
}
