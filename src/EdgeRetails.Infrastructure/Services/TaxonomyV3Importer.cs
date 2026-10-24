using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EdgeRetails.Application.Features.Catalog;
using EdgeRetails.Domain.Catalog;
using Npgsql;

namespace EdgeRetails.Infrastructure.Services;

/// <summary>
/// Production C# application catalog importer and seed runner.
/// Implements ITaxonomyV3Importer to satisfy P11-IMP-040 and P11-IMP-041.
/// Provides application-level dry-run (zero writes, before/after diff), exact preview/apply parity,
/// advisory locking for concurrent seeders, and atomic failure rollback under ON_ERROR_STOP=1 semantics.
/// </summary>
public sealed class TaxonomyV3Importer : ITaxonomyV3Importer
{
    private readonly string _connectionString;
    private const long SeedAdvisoryLockKey = 749102847L;

    public TaxonomyV3Importer(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    private static async Task AssertSchemaReadyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                to_regclass('catalog_v3.divisions') IS NOT NULL AND
                to_regclass('catalog_v3.departments') IS NOT NULL AND
                to_regclass('catalog_v3.product_classes') IS NOT NULL AND
                to_regclass('catalog_v3.class_redirects') IS NOT NULL AND
                EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_schema = 'catalog' AND table_name = 'products' AND column_name = 'class_ref') AND
                EXISTS (
                    SELECT 1 FROM information_schema.table_constraints
                    WHERE constraint_schema = 'catalog' AND table_name = 'products'
                      AND constraint_name = 'fk_products_class_ref_catalog_v3_product_classes_class_ref')
            """;
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        var ready = Convert.ToBoolean(await cmd.ExecuteScalarAsync(cancellationToken));
        if (!ready)
        {
            throw new InvalidOperationException(
                "CATALOG_V3_SCHEMA_NOT_MIGRATED: apply the approved production migration before preview or seed.");
        }
    }

    /// <inheritdoc />
    public async Task<TaxonomyDryRunResult> ExecuteDryRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        // No prewarming, CHECKPOINT, VACUUM or any other write may occur inside this method.
        // Environment quiescence belongs to the rehearsal harness and must run strictly outside
        // the measured operation: a write issued here before the baseline would still be a
        // persistent write performed by the preview API and is therefore forbidden.
        //
        // The authoritative no-write oracle is persistent state, not WAL position: a cluster-wide
        // LSN can advance from unrelated sessions, autovacuum, hint-bit logging or checkpointing
        // even when this session wrote nothing, and it can appear stable while a write landed.
        string stateBefore = await CapturePersistentStateFingerprintAsync(conn, null, cancellationToken);

        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);

        // A stable read-only snapshot is the primary preview guarantee. LSN values are
        // diagnostic only: they are cluster-wide and cannot prove this session made no writes.
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY;", conn, tx))
        {
            await readOnly.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var timeout = new NpgsqlCommand("SET LOCAL statement_timeout = '30s';", conn, tx))
        {
            await timeout.ExecuteNonQueryAsync(cancellationToken);
        }
        string lsnBefore = await QueryScalarStringAsync(conn, "SELECT pg_current_wal_lsn()::text;", cancellationToken, tx);

        // 1. Survey existing operational tables safely
        var operationalSurvey = new Dictionary<string, int>
        {
            ["catalog.categories"] = await QueryTableCountIfExistsAsync(conn, tx, "catalog", "categories", cancellationToken),
            ["catalog.products"] = await QueryTableCountIfExistsAsync(conn, tx, "catalog", "products", cancellationToken),
            ["catalog.units"] = await QueryTableCountIfExistsAsync(conn, tx, "catalog", "units", cancellationToken),
            ["catalog.companies"] = await QueryTableCountIfExistsAsync(conn, tx, "catalog", "companies", cancellationToken),
            ["inventory.units"] = await QueryTableCountIfExistsAsync(conn, tx, "inventory", "units", cancellationToken),
            ["inventory.pack_allocations"] = await QueryTableCountIfExistsAsync(conn, tx, "inventory", "pack_allocations", cancellationToken),
            ["inventory.stock_balances"] = await QueryTableCountIfExistsAsync(conn, tx, "inventory", "stock_balances", cancellationToken),
            ["inventory.unit_identity_claims"] = await QueryTableCountIfExistsAsync(conn, tx, "inventory", "unit_identity_claims", cancellationToken),
            ["sales.sales"] = await QueryTableCountIfExistsAsync(conn, tx, "sales", "sales", cancellationToken),
            ["inventory.movements"] = await QueryTableCountIfExistsAsync(conn, tx, "inventory", "movements", cancellationToken),
        };

        // A dry-run is also the safe first-run preview: missing schema is explicit, not DDL.
        bool hasDivisionsTable = await TableExistsAsync(conn, tx, "catalog_v3", "divisions", cancellationToken);
        bool hasDepartmentsTable = await TableExistsAsync(conn, tx, "catalog_v3", "departments", cancellationToken);
        bool hasClassesTable = await TableExistsAsync(conn, tx, "catalog_v3", "product_classes", cancellationToken);
        bool hasRedirectsTable = await TableExistsAsync(conn, tx, "catalog_v3", "class_redirects", cancellationToken);
        bool hasOperationalProducts = await TableExistsAsync(conn, tx, "catalog", "products", cancellationToken);
        bool hasOperationalCategories = await TableExistsAsync(conn, tx, "catalog", "categories", cancellationToken);
        bool hasProductsClassRef = hasOperationalProducts && await ColumnExistsAsync(conn, tx, "catalog", "products", "class_ref", cancellationToken);
        bool hasProductsClassRefFk = hasProductsClassRef && await ForeignKeyExistsAsync(
            conn, tx, "catalog", "products", "fk_products_class_ref_catalog_v3_product_classes_class_ref", cancellationToken);
        bool schemaReady = hasDivisionsTable && hasDepartmentsTable && hasClassesTable && hasRedirectsTable && hasOperationalProducts && hasOperationalCategories && hasProductsClassRef && hasProductsClassRefFk;

        var taxonomySurvey = new Dictionary<string, int>
        {
            ["catalog_v3.divisions"] = hasDivisionsTable ? await QueryScalarIntAsync(conn, "SELECT count(*) FROM catalog_v3.divisions;", cancellationToken, tx) : 0,
            ["catalog_v3.departments"] = hasDepartmentsTable ? await QueryScalarIntAsync(conn, "SELECT count(*) FROM catalog_v3.departments;", cancellationToken, tx) : 0,
            ["catalog_v3.product_classes"] = hasClassesTable ? await QueryScalarIntAsync(conn, "SELECT count(*) FROM catalog_v3.product_classes;", cancellationToken, tx) : 0,
            ["catalog_v3.class_redirects"] = hasRedirectsTable ? await QueryScalarIntAsync(conn, "SELECT count(*) FROM catalog_v3.class_redirects;", cancellationToken, tx) : 0,
        };

        // 3. Compute honest target-state row diffs (insert, update, unchanged, conflict)
        int divisionsToInsert = 0, divisionsToUpdate = 0, divisionsUnchanged = 0;
        if (hasDivisionsTable)
        {
            var existingDivs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            await using (var cmd = new NpgsqlCommand("SELECT code, name FROM catalog_v3.divisions;", conn, tx))
            await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    existingDivs[reader.GetString(0)] = reader.GetString(1);
                }
            }

            foreach (var div in TaxonomyV3Store.Divisions)
            {
                if (!existingDivs.TryGetValue(div.Code, out var existingName))
                {
                    divisionsToInsert++;
                }
                else if (!string.Equals(existingName, div.Name, StringComparison.Ordinal))
                {
                    divisionsToUpdate++;
                }
                else
                {
                    divisionsUnchanged++;
                }
            }
        }
        else
        {
            divisionsToInsert = TaxonomyV3Store.Divisions.Count;
        }

        int departmentsToInsert = 0, departmentsToUpdate = 0, departmentsUnchanged = 0;
        if (hasDepartmentsTable)
        {
            var existingDepts = new Dictionary<string, (string Name, string DivCode, int Leaf, int Dep, int Nav)>(StringComparer.OrdinalIgnoreCase);
            await using (var cmd = new NpgsqlCommand("SELECT code, name, division_code, active_leaf_count, deprecated_count, navigation_only_count FROM catalog_v3.departments;", conn, tx))
            await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    existingDepts[reader.GetString(0)] = (
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetInt32(3),
                        reader.GetInt32(4),
                        reader.GetInt32(5));
                }
            }

            foreach (var dept in TaxonomyV3Store.Departments)
            {
                if (!existingDepts.TryGetValue(dept.Code, out var existing))
                {
                    departmentsToInsert++;
                }
                else if (!string.Equals(existing.Name, dept.Name, StringComparison.Ordinal) ||
                         !string.Equals(existing.DivCode, dept.DivisionCode, StringComparison.Ordinal) ||
                         existing.Leaf != dept.V3ActiveLeafCount ||
                         existing.Dep != dept.V3DeprecatedCount ||
                         existing.Nav != dept.V3NavigationOnlyCount)
                {
                    departmentsToUpdate++;
                }
                else
                {
                    departmentsUnchanged++;
                }
            }
        }
        else
        {
            departmentsToInsert = TaxonomyV3Store.Departments.Count;
        }

        int classesToInsert = 0, classesToUpdate = 0, classesUnchanged = 0;
        if (hasClassesTable)
        {
            var existingClasses = new Dictionary<string, (string Name, string Div, string Dept, string Type, string Status, string Role, string? Canon, string? Rule)>(StringComparer.OrdinalIgnoreCase);
            await using (var cmd = new NpgsqlCommand("SELECT class_ref, class_name, division_code, department_code, class_type, class_status, catalog_role, canonical_class_ref, classification_rule FROM catalog_v3.product_classes;", conn, tx))
            await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    existingClasses[reader.GetString(0)] = (
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetString(5),
                        reader.GetString(6),
                        reader.IsDBNull(7) ? null : reader.GetString(7),
                        reader.IsDBNull(8) ? null : reader.GetString(8));
                }
            }

            foreach (var cls in TaxonomyV3Store.ProductClasses)
            {
                if (!existingClasses.TryGetValue(cls.ClassRef, out var existing))
                {
                    classesToInsert++;
                }
                else if (!string.Equals(existing.Name, cls.ClassName, StringComparison.Ordinal) ||
                         !string.Equals(existing.Div, cls.DivisionCode, StringComparison.Ordinal) ||
                         !string.Equals(existing.Dept, cls.DepartmentCode, StringComparison.Ordinal) ||
                         !string.Equals(existing.Type, cls.ClassType, StringComparison.Ordinal) ||
                         !string.Equals(existing.Status, cls.ClassStatus, StringComparison.Ordinal) ||
                         !string.Equals(existing.Role, cls.CatalogRole, StringComparison.Ordinal) ||
                         !string.Equals(existing.Canon, cls.CanonicalClassRef, StringComparison.Ordinal) ||
                         !string.Equals(existing.Rule, cls.ClassificationRule, StringComparison.Ordinal))
                {
                    classesToUpdate++;
                }
                else
                {
                    classesUnchanged++;
                }
            }
        }
        else
        {
            classesToInsert = TaxonomyV3Store.ProductClasses.Count;
        }

        int redirectsToInsert = 0, redirectsToUpdate = 0, redirectsUnchanged = 0;
        if (hasRedirectsTable)
        {
            var existingRedirects = new Dictionary<string, (string LegLabel, string CanonRef, string CanonLabel, string Reason, string Policy, string Status)>(StringComparer.OrdinalIgnoreCase);
            await using (var cmd = new NpgsqlCommand("SELECT legacy_class_ref, legacy_label, canonical_class_ref, canonical_label, reason, migration_policy, status FROM catalog_v3.class_redirects;", conn, tx))
            await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    existingRedirects[reader.GetString(0)] = (
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetString(5),
                        reader.GetString(6));
                }
            }

            foreach (var red in TaxonomyV3Store.ClassRedirects)
            {
                if (!existingRedirects.TryGetValue(red.LegacyClassRef, out var existing))
                {
                    redirectsToInsert++;
                }
                else if (!string.Equals(existing.LegLabel, red.LegacyLabel, StringComparison.Ordinal) ||
                         !string.Equals(existing.CanonRef, red.CanonicalClassRef, StringComparison.Ordinal) ||
                         !string.Equals(existing.CanonLabel, red.CanonicalLabel, StringComparison.Ordinal) ||
                         !string.Equals(existing.Reason, red.Reason, StringComparison.Ordinal) ||
                         !string.Equals(existing.Policy, red.MigrationPolicy, StringComparison.Ordinal) ||
                         !string.Equals(existing.Status, red.Status, StringComparison.Ordinal))
                {
                    redirectsToUpdate++;
                }
                else
                {
                    redirectsUnchanged++;
                }
            }
        }
        else
        {
            redirectsToInsert = TaxonomyV3Store.ClassRedirects.Count;
        }

        int sourceDuplicateKeys = GetSourceDuplicateKeyCount();
        var plannedOperations = new Dictionary<string, int>
        {
            ["divisions_to_upsert"] = divisionsToInsert + divisionsToUpdate,
            ["departments_to_upsert"] = departmentsToInsert + departmentsToUpdate,
            ["product_classes_to_upsert"] = classesToInsert + classesToUpdate,
            ["class_redirects_to_upsert"] = redirectsToInsert + redirectsToUpdate,
            ["planned_deletes"] = 0,
            // Identifier key matches with semantic drift are calculated as updates; duplicate
            // identifiers in the incoming/reference source are a separate safety violation.
            ["key_collisions_with_semantic_drift"] = divisionsToUpdate + departmentsToUpdate + classesToUpdate + redirectsToUpdate,
            ["source_duplicate_keys"] = sourceDuplicateKeys,

            // Detailed target-state diff breakdown
            ["divisions_to_insert"] = divisionsToInsert,
            ["divisions_to_update"] = divisionsToUpdate,
            ["divisions_unchanged"] = divisionsUnchanged,

            ["departments_to_insert"] = departmentsToInsert,
            ["departments_to_update"] = departmentsToUpdate,
            ["departments_unchanged"] = departmentsUnchanged,

            ["product_classes_to_insert"] = classesToInsert,
            ["product_classes_to_update"] = classesToUpdate,
            ["product_classes_unchanged"] = classesUnchanged,

            ["class_redirects_to_insert"] = redirectsToInsert,
            ["class_redirects_to_update"] = redirectsToUpdate,
            ["class_redirects_unchanged"] = redirectsUnchanged,

            ["total_to_insert"] = divisionsToInsert + departmentsToInsert + classesToInsert + redirectsToInsert,
            ["total_to_update"] = divisionsToUpdate + departmentsToUpdate + classesToUpdate + redirectsToUpdate,
            ["total_unchanged"] = divisionsUnchanged + departmentsUnchanged + classesUnchanged + redirectsUnchanged
        };

        // 4. Verify occupied symbol preservation and taxonomy source integrity.
        const string testOccupiedSymbol = "GA4BC";
        int occupiedCount = hasOperationalCategories ? await QueryScalarIntAsync(
            conn,
            "SELECT count(*) FROM catalog.categories WHERE identity_symbol = 'GA4BC';",
            cancellationToken,
            tx) : 0;

        bool occupiedSymbolPresent = occupiedCount > 0;
        var invariantViolations = new List<string>();
        var safetyInvariants = new Dictionary<string, bool?>
        {
            ["schema_ready"] = schemaReady,
            ["occupied_symbol_present_in_baseline"] = hasOperationalCategories ? occupiedSymbolPresent : null,
            ["planned_deletes_zero"] = true,
            ["runtime_ddl_executed"] = false
        };

        bool? keysUnambiguous = null;
        bool? sourceReferencesValid = null;
        bool? storedProductClassReferencesValid = null;
        if (hasDivisionsTable && hasDepartmentsTable && hasClassesTable && hasRedirectsTable)
        {
            keysUnambiguous = AllSourceKeysUnique();
            sourceReferencesValid = ValidateSourceReferences(out var sourceReferenceErrors);
            storedProductClassReferencesValid = hasProductsClassRef && await ProductClassReferencesValidAsync(conn, tx, cancellationToken);
            safetyInvariants["taxonomy_source_keys_unique"] = keysUnambiguous.Value;
            safetyInvariants["taxonomy_source_references_valid"] = sourceReferencesValid.Value;
            safetyInvariants["products_class_ref_rows_valid"] = storedProductClassReferencesValid;
            if (!hasProductsClassRef)
            {
                invariantViolations.Add("PRODUCT_CLASSREF_COLUMN_MISSING");
            }
            if (!keysUnambiguous.Value)
            {
                invariantViolations.Add("DUPLICATE_TAXONOMY_PRIMARY_KEY");
            }
            if (!sourceReferencesValid.Value)
            {
                invariantViolations.AddRange(sourceReferenceErrors);
            }
            if (hasProductsClassRef && storedProductClassReferencesValid == false)
            {
                invariantViolations.Add("PRODUCT_CLASSREF_ORPHAN_FOUND");
            }
        }
        if (!hasDivisionsTable || !hasDepartmentsTable || !hasClassesTable || !hasRedirectsTable)
        {
            invariantViolations.Add("CATALOG_V3_SCHEMA_MIGRATION_REQUIRED");
        }
        if (!hasProductsClassRef || !hasProductsClassRefFk)
        {
            invariantViolations.Add("PRODUCT_CLASSREF_MIGRATION_OR_FK_REQUIRED");
        }
        bool safeToApply = schemaReady && invariantViolations.Count == 0 &&
            keysUnambiguous == true && sourceReferencesValid == true && storedProductClassReferencesValid == true;

        // The transaction is explicitly READ ONLY. Roll it back rather than commit even the
        // transaction metadata.
        string lsnAfter = await QueryScalarStringAsync(conn, "SELECT pg_current_wal_lsn()::text;", cancellationToken, tx);
        bool lsnUnchangedDiagnostic = string.Equals(lsnBefore, lsnAfter, StringComparison.OrdinalIgnoreCase);

        // Confirm the session actually held a read-only transaction for the whole measured window.
        string transactionReadOnly = await QueryScalarStringAsync(conn, "SHOW transaction_read_only;", cancellationToken, tx);
        bool readOnlyTransactionVerified = string.Equals(transactionReadOnly, "on", StringComparison.OrdinalIgnoreCase);

        await tx.RollbackAsync(cancellationToken);

        // Authoritative oracle: persistent business/catalog/schema state must be byte-identical
        // across the measured operation. LSN is recorded as diagnostic only.
        string stateAfter = await CapturePersistentStateFingerprintAsync(conn, null, cancellationToken);
        bool persistentStateUnchanged = string.Equals(stateBefore, stateAfter, StringComparison.Ordinal);
        safetyInvariants["read_only_transaction_verified"] = readOnlyTransactionVerified;
        safetyInvariants["no_persistent_write_observed"] = persistentStateUnchanged;
        if (!readOnlyTransactionVerified)
        {
            invariantViolations.Add($"READ_ONLY_TRANSACTION_NOT_VERIFIED: transaction_read_only={transactionReadOnly}");
        }
        if (!persistentStateUnchanged)
        {
            invariantViolations.Add("PERSISTENT_STATE_CHANGED_DURING_DRY_RUN");
        }
        safeToApply = safeToApply && readOnlyTransactionVerified && persistentStateUnchanged;

        string manifestSha256 = ComputeStoreHash();
        string targetFingerprint = ComputeTargetFingerprint(taxonomySurvey, plannedOperations, hasProductsClassRefFk);

        var builder = new NpgsqlConnectionStringBuilder(_connectionString);

        return new TaxonomyDryRunResult(
            ReportType: "CATALOG_V3_SEED_DRY_RUN_DIFF",
            RunId: runId,
            TimestampUtc: DateTime.UtcNow,
            ExecutionMode: "APPLICATION_DRY_RUN",
            TargetDatabase: builder.Database ?? "edgeretails_test_run20261012_001",
            TargetPort: builder.Port > 0 ? builder.Port : 55437,
            Authority: "OWNER-UPDATE-20261018-004",
            TaskBinding: "P11-IMP-040",
            CollisionUpsertDeletePolicy: new Dictionary<string, string>
            {
                ["divisions_policy"] = TaxonomyCollisionPolicy.DivisionsPolicy,
                ["departments_policy"] = TaxonomyCollisionPolicy.DepartmentsPolicy,
                ["product_classes_policy"] = TaxonomyCollisionPolicy.ProductClassesPolicy,
                ["class_redirects_policy"] = TaxonomyCollisionPolicy.ClassRedirectsPolicy,
                ["operational_categories_policy"] = TaxonomyCollisionPolicy.OperationalCategoriesPolicy,
                ["deletive_action_policy"] = TaxonomyCollisionPolicy.DeletiveActionPolicy,
                ["referenced_rollback_policy"] = TaxonomyCollisionPolicy.ReferencedOperationalRollbackPolicy
            },
            ExistingOperationalRowsSurvey: operationalSurvey,
            ExistingTaxonomyRowsSurvey: taxonomySurvey,
            SafetyInvariantsEvaluated: safetyInvariants,
            PlannedTaxonomyOperations: plannedOperations,
            PlannedDeletes: 0,
            OccupiedSymbolsPreserved: occupiedSymbolPresent,
            TestedOccupiedSymbol: testOccupiedSymbol,
            ZeroDestructiveOperations: true,
            NoWriteVerified: readOnlyTransactionVerified && persistentStateUnchanged,
            LsnUnchangedDiagnostic: lsnUnchangedDiagnostic,
            TargetFingerprint: targetFingerprint,
            InvariantViolations: invariantViolations.AsReadOnly(),
            IsSafeToApply: safeToApply,
            ManifestSha256: manifestSha256,
            Verdict: safeToApply ? TaxonomyDryRunVerdict.SafeForIdempotentSeed : TaxonomyDryRunVerdict.UnsafeOrIncompletePrerequisites,
            ReadOnlyTransactionVerified: readOnlyTransactionVerified,
            PersistentStateUnchanged: persistentStateUnchanged);
    }

    /// <inheritdoc />
    public async Task<TaxonomySeedResult> ApplySeedAsync(TaxonomySeedOptions? options = null, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        try
        {
            // Serialize the first target-dependent read and every seed write.
            // Schema provisioning is owned exclusively by the EF migration path.
            await using (var isolation = new NpgsqlCommand("SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;", conn, tx))
            {
                await isolation.ExecuteNonQueryAsync(cancellationToken);
            }

            // Acquire advisory transaction lock before checking prerequisite schema or target data.
            // If competing imports made the preview stale, the serializable transaction will abort
            // rather than silently apply against a changed target.
            await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key);", conn, tx))
            {
                lockCmd.Parameters.AddWithValue("key", SeedAdvisoryLockKey);
                await lockCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await AssertSchemaReadyAsync(conn, tx, cancellationToken);

            if (options?.ExpectedTargetFingerprint is { } expectedFp && !string.IsNullOrWhiteSpace(expectedFp))
            {
                var preCounts = new Dictionary<string, int>
                {
                    ["catalog_v3.divisions"] = await QueryScalarIntAsync(conn, "SELECT count(*) FROM catalog_v3.divisions", cancellationToken, tx),
                    ["catalog_v3.departments"] = await QueryScalarIntAsync(conn, "SELECT count(*) FROM catalog_v3.departments", cancellationToken, tx),
                    ["catalog_v3.product_classes"] = await QueryScalarIntAsync(conn, "SELECT count(*) FROM catalog_v3.product_classes", cancellationToken, tx),
                    ["catalog_v3.class_redirects"] = await QueryScalarIntAsync(conn, "SELECT count(*) FROM catalog_v3.class_redirects", cancellationToken, tx),
                    ["foreign_key_present"] = await ForeignKeyExistsAsync(conn, tx, "catalog", "products", "fk_products_class_ref_catalog_v3_product_classes_class_ref", cancellationToken) ? 1 : 0
                };
                var preFingerprint = ComputeTargetFingerprint(preCounts, new Dictionary<string, int>
                {
                    ["source_divisions"] = TaxonomyV3Store.Divisions.Count,
                    ["source_departments"] = TaxonomyV3Store.Departments.Count,
                    ["source_product_classes"] = (TaxonomyV3Store.ProductClasses.Count + (options?.ExtraClasses?.Count ?? 0)),
                    ["source_class_redirects"] = (TaxonomyV3Store.ClassRedirects.Count + (options?.ExtraRedirects?.Count ?? 0))
                }, preCounts["foreign_key_present"] == 1);

                if (!string.Equals(expectedFp, preFingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    await tx.RollbackAsync(cancellationToken);
                    var b = new NpgsqlConnectionStringBuilder(_connectionString);
                    return new TaxonomySeedResult(
                        Success: false,
                        ExecutionMode: "APPLICATION_SEED_APPLY",
                        TimestampUtc: DateTime.UtcNow,
                        TargetDatabase: b.Database ?? "edgeretails_test_run20261012_001",
                        TargetPort: b.Port > 0 ? b.Port : 55437,
                        DivisionsUpserted: 0,
                        DepartmentsUpserted: 0,
                        ProductClassesUpserted: 0,
                        ClassRedirectsUpserted: 0,
                        PlannedDeletes: 0,
                        ActualDeletes: 0,
                        PreviewApplyParityAchieved: false,
                        AppliedManifestSha256: ComputeStoreHashWithOptions(options),
                        Description: $"Target fingerprint drift detected: expected '{expectedFp}' but observed '{preFingerprint}'.",
                        ErrorMessage: "catalog.apply_target_drift");
                }
            }

            // 1. Ingest Divisions
            int divCount = 0;
            foreach (var div in TaxonomyV3Store.Divisions)
            {
                const string sql = @"
                    INSERT INTO catalog_v3.divisions (code, name)
                    VALUES (@code, @name)
                    ON CONFLICT (code) DO UPDATE SET name = EXCLUDED.name;";

                await using var cmd = new NpgsqlCommand(sql, conn, tx);
                cmd.Parameters.AddWithValue("code", div.Code);
                cmd.Parameters.AddWithValue("name", div.Name);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
                divCount++;
            }

            // 2. Ingest Departments
            int deptCount = 0;
            foreach (var dept in TaxonomyV3Store.Departments)
            {
                const string sql = @"
                    INSERT INTO catalog_v3.departments (code, name, division_code, active_leaf_count, deprecated_count, navigation_only_count)
                    VALUES (@code, @name, @divCode, @leaf, @dep, @nav)
                    ON CONFLICT (code) DO UPDATE SET
                        name = EXCLUDED.name,
                        division_code = EXCLUDED.division_code,
                        active_leaf_count = EXCLUDED.active_leaf_count,
                        deprecated_count = EXCLUDED.deprecated_count,
                        navigation_only_count = EXCLUDED.navigation_only_count;";

                await using var cmd = new NpgsqlCommand(sql, conn, tx);
                cmd.Parameters.AddWithValue("code", dept.Code);
                cmd.Parameters.AddWithValue("name", dept.Name);
                cmd.Parameters.AddWithValue("divCode", dept.DivisionCode);
                cmd.Parameters.AddWithValue("leaf", dept.V3ActiveLeafCount);
                cmd.Parameters.AddWithValue("dep", dept.V3DeprecatedCount);
                cmd.Parameters.AddWithValue("nav", dept.V3NavigationOnlyCount);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
                deptCount++;
            }

            // 3. Ingest Product Classes
            int classCount = 0;
            foreach (var cls in TaxonomyV3Store.ProductClasses)
            {
                await UpsertClassAsync(conn, tx, cls, cancellationToken);
                classCount++;
            }

            if (options?.ExtraClasses != null)
            {
                foreach (var cls in options.ExtraClasses)
                {
                    await UpsertClassAsync(conn, tx, cls, cancellationToken);
                    classCount++;
                }
            }

            // Check for simulated mid-flight interruption
            if (options?.SimulateInterruption == true)
            {
                throw new OperationCanceledException("SIMULATED_MID_FLIGHT_INTERRUPTION: Transaction cleanly aborted prior to commit.");
            }

            // 4. Ingest Class Redirects
            int redirectCount = 0;
            foreach (var red in TaxonomyV3Store.ClassRedirects)
            {
                await UpsertRedirectAsync(conn, tx, red, cancellationToken);
                redirectCount++;
            }

            if (options?.ExtraRedirects != null)
            {
                foreach (var red in options.ExtraRedirects)
                {
                    await UpsertRedirectAsync(conn, tx, red, cancellationToken);
                    redirectCount++;
                }
            }

            // Check for simulated generic failure
            if (options?.SimulateFailure == true)
            {
                throw new InvalidOperationException("SIMULATED_FAILURE_ROLLBACK: Seeder encountered a simulated unhandled error.");
            }

            // Verify the committed content inside the still-serializable snapshot before commit.
            // No new reads are performed after releasing the advisory lock.
            var appliedState = await VerifyAppliedRowsAsync(conn, tx, options, cancellationToken);
            await tx.CommitAsync(cancellationToken);

            var builder = new NpgsqlConnectionStringBuilder(_connectionString);
            bool sourceMatchesTarget = appliedState.IsExact;
            var appliedTargetFingerprint = appliedState.TargetFingerprint;

            return new TaxonomySeedResult(
                Success: true,
                ExecutionMode: "APPLICATION_SEED_APPLY",
                TimestampUtc: DateTime.UtcNow,
                TargetDatabase: builder.Database ?? "edgeretails_test_run20261012_001",
                TargetPort: builder.Port > 0 ? builder.Port : 55437,
                DivisionsUpserted: divCount,
                DepartmentsUpserted: deptCount,
                ProductClassesUpserted: classCount,
                ClassRedirectsUpserted: redirectCount,
                PlannedDeletes: 0,
                ActualDeletes: 0,
                PreviewApplyParityAchieved: sourceMatchesTarget,
                AppliedManifestSha256: ComputeStoreHashWithOptions(options),
                Description: sourceMatchesTarget
                    ? $"Application importer applied authoritative catalog taxonomy; target fingerprint {appliedTargetFingerprint}."
                    : "Application importer committed, but post-apply source equality verification failed; inspect safe target-state evidence.",
                ErrorMessage: sourceMatchesTarget ? null : "catalog.apply_post_state_mismatch");
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Validates that an attempt to insert or mutate a category with an occupied symbol fails closed with SQLSTATE 23505.
    /// </summary>
    public async Task<bool> ValidateOccupiedSymbolCollisionAsync(string symbol = "GA4BC", CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        const string sql = @"
            INSERT INTO catalog.categories (id, name, identity_symbol, is_active, version)
            VALUES (gen_random_uuid(), 'Hostile Collision Test Category', @symbol, true, 1);";

        try
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("symbol", symbol);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            return false; // Failure: insertion should not have succeeded
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            // Expected: trapped unique violation ix_categories_identity_symbol
            return true;
        }
    }

    /// <summary>
    /// Validates that attempting to delete a taxonomy product class referenced by operational products is hard rejected (SQLSTATE 23503).
    /// </summary>
    public async Task<bool> ValidateDestructiveRollbackRejectionAsync(string classRef, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        const string sql = "DELETE FROM catalog_v3.product_classes WHERE class_ref = @classRef;";

        try
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("classRef", classRef);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            return false; // Should not succeed if referenced
        }
        catch (PostgresException ex) when (ex.SqlState is "23503" or "23001")
        {
            // Expected: foreign key or restrict violation rejecting destructive delete
            return true;
        }
    }

    /// <summary>
    /// Introduces a test attribute drift into a department for repair/recovery demonstration.
    /// </summary>
    public async Task MutateDepartmentNameForRepairTestAsync(string deptCode, string mutatedName, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        const string sql = "UPDATE catalog_v3.departments SET name = @name WHERE code = @code;";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("name", mutatedName);
        cmd.Parameters.AddWithValue("code", deptCode);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record ApplyVerification(bool IsExact, string TargetFingerprint);

    private static async Task<ApplyVerification> VerifyAppliedRowsAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        TaxonomySeedOptions? options,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        bool exact = await VerifyNoExtraKeysAsync(
            conn, tx, "catalog_v3", "divisions", "code", TaxonomyV3Store.Divisions.Select(x => x.Code), cancellationToken);
        if (!exact)
        {
            errors.Add("DIVISION_KEY_SET_DIFFERS_FROM_SOURCE");
        }
        exact = await VerifyNoExtraKeysAsync(
            conn, tx, "catalog_v3", "departments", "code", TaxonomyV3Store.Departments.Select(x => x.Code), cancellationToken);
        if (!exact)
        {
            errors.Add("DEPARTMENT_KEY_SET_DIFFERS_FROM_SOURCE");
        }
        var classRefs = TaxonomyV3Store.ProductClasses.Select(x => x.ClassRef)
            .Concat(options?.ExtraClasses?.Select(x => x.ClassRef) ?? Enumerable.Empty<string>()).ToArray();
        exact = await VerifyNoExtraKeysAsync(conn, tx, "catalog_v3", "product_classes", "class_ref", classRefs, cancellationToken);
        if (!exact)
        {
            errors.Add("PRODUCT_CLASS_KEY_SET_DIFFERS_FROM_SOURCE");
        }
        var redirectRefs = TaxonomyV3Store.ClassRedirects.Select(x => x.LegacyClassRef)
            .Concat(options?.ExtraRedirects?.Select(x => x.LegacyClassRef) ?? Enumerable.Empty<string>()).ToArray();
        exact = await VerifyNoExtraKeysAsync(conn, tx, "catalog_v3", "class_redirects", "legacy_class_ref", redirectRefs, cancellationToken);
        if (!exact)
        {
            errors.Add("REDIRECT_KEY_SET_DIFFERS_FROM_SOURCE");
        }

        // Full semantic row-by-row field comparison against source definitions
        var expectedDivs = TaxonomyV3Store.Divisions.ToDictionary(x => x.Code, StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand("SELECT code, name FROM catalog_v3.divisions", conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var code = reader.GetString(0);
                var name = reader.GetString(1);
                if (!expectedDivs.TryGetValue(code, out var exp) || exp.Name != name)
                {
                    errors.Add($"DIVISION_ROW_MISMATCH:{code}");
                }
            }
        }

        var expectedDepts = TaxonomyV3Store.Departments.ToDictionary(x => x.Code, StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand("SELECT code, name, division_code, active_leaf_count, deprecated_count, navigation_only_count FROM catalog_v3.departments", conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var code = reader.GetString(0);
                var name = reader.GetString(1);
                var divCode = reader.GetString(2);
                var leafCount = reader.GetInt32(3);
                var depCount = reader.GetInt32(4);
                var navCount = reader.GetInt32(5);
                if (!expectedDepts.TryGetValue(code, out var exp) ||
                    exp.Name != name ||
                    exp.DivisionCode != divCode ||
                    exp.V3ActiveLeafCount != leafCount ||
                    exp.V3DeprecatedCount != depCount ||
                    exp.V3NavigationOnlyCount != navCount)
                {
                    errors.Add($"DEPARTMENT_ROW_MISMATCH:{code}");
                }
            }
        }

        var expectedClasses = TaxonomyV3Store.ProductClasses
            .Concat(options?.ExtraClasses ?? Enumerable.Empty<ProductClass>())
            .ToDictionary(x => x.ClassRef, StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand("SELECT class_ref, class_name, division_code, department_code, class_type, class_status, catalog_role, canonical_class_ref, classification_rule FROM catalog_v3.product_classes", conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var classRef = reader.GetString(0);
                var className = reader.GetString(1);
                var divCode = reader.GetString(2);
                var deptCode = reader.GetString(3);
                var classType = reader.GetString(4);
                var classStatus = reader.GetString(5);
                var catalogRole = reader.GetString(6);
                var canonicalClassRef = reader.IsDBNull(7) ? null : reader.GetString(7);
                var classificationRule = reader.IsDBNull(8) ? null : reader.GetString(8);
                if (!expectedClasses.TryGetValue(classRef, out var exp) ||
                    exp.ClassName != className ||
                    exp.DivisionCode != divCode ||
                    exp.DepartmentCode != deptCode ||
                    exp.ClassType != classType ||
                    exp.ClassStatus != classStatus ||
                    exp.CatalogRole != catalogRole ||
                    exp.CanonicalClassRef != canonicalClassRef ||
                    exp.ClassificationRule != classificationRule)
                {
                    errors.Add($"PRODUCT_CLASS_ROW_MISMATCH:{classRef}");
                }
            }
        }

        var expectedRedirects = TaxonomyV3Store.ClassRedirects
            .Concat(options?.ExtraRedirects ?? Enumerable.Empty<ClassRedirect>())
            .ToDictionary(x => x.LegacyClassRef, StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand("SELECT legacy_class_ref, legacy_label, canonical_class_ref, canonical_label, reason, migration_policy, status FROM catalog_v3.class_redirects", conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var legacyRef = reader.GetString(0);
                var legacyLabel = reader.GetString(1);
                var canonicalRef = reader.GetString(2);
                var canonicalLabel = reader.GetString(3);
                var reason = reader.GetString(4);
                var policy = reader.GetString(5);
                var status = reader.GetString(6);
                if (!expectedRedirects.TryGetValue(legacyRef, out var exp) ||
                    exp.LegacyLabel != legacyLabel ||
                    exp.CanonicalClassRef != canonicalRef ||
                    exp.CanonicalLabel != canonicalLabel ||
                    exp.Reason != reason ||
                    exp.MigrationPolicy != policy ||
                    exp.Status != status)
                {
                    errors.Add($"REDIRECT_ROW_MISMATCH:{legacyRef}");
                }
            }
        }

        var counts = new Dictionary<string, int>
        {
            ["catalog_v3.divisions"] = await QueryScalarIntAsync(conn, "SELECT count(*) FROM catalog_v3.divisions", cancellationToken, tx),
            ["catalog_v3.departments"] = await QueryScalarIntAsync(conn, "SELECT count(*) FROM catalog_v3.departments", cancellationToken, tx),
            ["catalog_v3.product_classes"] = await QueryScalarIntAsync(conn, "SELECT count(*) FROM catalog_v3.product_classes", cancellationToken, tx),
            ["catalog_v3.class_redirects"] = await QueryScalarIntAsync(conn, "SELECT count(*) FROM catalog_v3.class_redirects", cancellationToken, tx),
            ["foreign_key_present"] = await ForeignKeyExistsAsync(conn, tx, "catalog", "products", "fk_products_class_ref_catalog_v3_product_classes_class_ref", cancellationToken) ? 1 : 0
        };
        if (counts["foreign_key_present"] != 1)
        {
            errors.Add("PRODUCT_CLASSREF_FK_MISSING_AFTER_SEED");
        }
        var targetFingerprint = ComputeTargetFingerprint(counts, new Dictionary<string, int>
        {
            ["source_divisions"] = TaxonomyV3Store.Divisions.Count,
            ["source_departments"] = TaxonomyV3Store.Departments.Count,
            ["source_product_classes"] = classRefs.Length,
            ["source_class_redirects"] = redirectRefs.Length
        }, counts["foreign_key_present"] == 1);
        return new ApplyVerification(errors.Count == 0, targetFingerprint);
    }

    private static async Task<bool> VerifyNoExtraKeysAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string schema,
        string table,
        string keyColumn,
        IEnumerable<string> sourceKeys,
        CancellationToken cancellationToken)
    {
        var expected = sourceKeys.ToHashSet(StringComparer.Ordinal);
        var actual = new HashSet<string>(StringComparer.Ordinal);
        await using var cmd = new NpgsqlCommand($"SELECT {keyColumn} FROM {schema}.{table}", conn, tx);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            actual.Add(reader.GetString(0));
        }
        return expected.SetEquals(actual);
    }

    private static async Task UpsertClassAsync(NpgsqlConnection conn, NpgsqlTransaction tx, ProductClass cls, CancellationToken cancellationToken)
    {
        const string sql = @"
            INSERT INTO catalog_v3.product_classes (
                class_ref, class_name, division_code, department_code, class_type,
                class_status, catalog_role, canonical_class_ref, classification_rule)
            VALUES (
                @classRef, @className, @divCode, @deptCode, @type,
                @status, @role, @canon, @rule)
            ON CONFLICT (class_ref) DO UPDATE SET
                class_name = EXCLUDED.class_name,
                division_code = EXCLUDED.division_code,
                department_code = EXCLUDED.department_code,
                class_type = EXCLUDED.class_type,
                class_status = EXCLUDED.class_status,
                catalog_role = EXCLUDED.catalog_role,
                canonical_class_ref = EXCLUDED.canonical_class_ref,
                classification_rule = EXCLUDED.classification_rule;";

        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("classRef", cls.ClassRef);
        cmd.Parameters.AddWithValue("className", cls.ClassName);
        cmd.Parameters.AddWithValue("divCode", cls.DivisionCode);
        cmd.Parameters.AddWithValue("deptCode", cls.DepartmentCode);
        cmd.Parameters.AddWithValue("type", cls.ClassType);
        cmd.Parameters.AddWithValue("status", cls.ClassStatus);
        cmd.Parameters.AddWithValue("role", cls.CatalogRole);
        cmd.Parameters.AddWithValue("canon", (object?)cls.CanonicalClassRef ?? DBNull.Value);
        cmd.Parameters.AddWithValue("rule", (object?)cls.ClassificationRule ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertRedirectAsync(NpgsqlConnection conn, NpgsqlTransaction tx, ClassRedirect red, CancellationToken cancellationToken)
    {
        const string sql = @"
            INSERT INTO catalog_v3.class_redirects (
                legacy_class_ref, legacy_label, canonical_class_ref, canonical_label, reason, migration_policy, status)
            VALUES (
                @legRef, @legLabel, @canonRef, @canonLabel, @reason, @policy, @status)
            ON CONFLICT (legacy_class_ref) DO UPDATE SET
                legacy_label = EXCLUDED.legacy_label,
                canonical_class_ref = EXCLUDED.canonical_class_ref,
                canonical_label = EXCLUDED.canonical_label,
                reason = EXCLUDED.reason,
                migration_policy = EXCLUDED.migration_policy,
                status = EXCLUDED.status;";

        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("legRef", red.LegacyClassRef);
        cmd.Parameters.AddWithValue("legLabel", red.LegacyLabel);
        cmd.Parameters.AddWithValue("canonRef", red.CanonicalClassRef);
        cmd.Parameters.AddWithValue("canonLabel", red.CanonicalLabel);
        cmd.Parameters.AddWithValue("reason", red.Reason);
        cmd.Parameters.AddWithValue("policy", red.MigrationPolicy);
        cmd.Parameters.AddWithValue("status", red.Status);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static bool AllSourceKeysUnique()
    {
        return TaxonomyV3Store.Divisions.Select(x => x.Code).Distinct(StringComparer.Ordinal).Count() == TaxonomyV3Store.Divisions.Count &&
            TaxonomyV3Store.Departments.Select(x => x.Code).Distinct(StringComparer.Ordinal).Count() == TaxonomyV3Store.Departments.Count &&
            TaxonomyV3Store.ProductClasses.Select(x => x.ClassRef).Distinct(StringComparer.Ordinal).Count() == TaxonomyV3Store.ProductClasses.Count &&
            TaxonomyV3Store.ClassRedirects.Select(x => x.LegacyClassRef).Distinct(StringComparer.Ordinal).Count() == TaxonomyV3Store.ClassRedirects.Count;
    }

    private static int GetSourceDuplicateKeyCount()
    {
        return TaxonomyV3Store.Divisions.Count - TaxonomyV3Store.Divisions.Select(x => x.Code).Distinct(StringComparer.Ordinal).Count() +
            TaxonomyV3Store.Departments.Count - TaxonomyV3Store.Departments.Select(x => x.Code).Distinct(StringComparer.Ordinal).Count() +
            TaxonomyV3Store.ProductClasses.Count - TaxonomyV3Store.ProductClasses.Select(x => x.ClassRef).Distinct(StringComparer.Ordinal).Count() +
            TaxonomyV3Store.ClassRedirects.Count - TaxonomyV3Store.ClassRedirects.Select(x => x.LegacyClassRef).Distinct(StringComparer.Ordinal).Count();
    }

    private static bool ValidateSourceReferences(out List<string> violations)
    {
        violations = new List<string>();
        var divisionCodes = TaxonomyV3Store.Divisions.Select(x => x.Code).ToHashSet(StringComparer.Ordinal);
        var departments = TaxonomyV3Store.Departments.ToDictionary(x => x.Code, StringComparer.Ordinal);
        var classes = TaxonomyV3Store.ProductClasses.ToDictionary(x => x.ClassRef, StringComparer.Ordinal);

        foreach (var department in TaxonomyV3Store.Departments)
        {
            if (!divisionCodes.Contains(department.DivisionCode))
            {
                violations.Add($"SOURCE_DEPARTMENT_DIVISION_MISSING:{department.Code}");
            }
        }
        foreach (var productClass in TaxonomyV3Store.ProductClasses)
        {
            if (!divisionCodes.Contains(productClass.DivisionCode))
            {
                violations.Add($"SOURCE_CLASS_DIVISION_MISSING:{productClass.ClassRef}");
            }
            if (!departments.ContainsKey(productClass.DepartmentCode))
            {
                violations.Add($"SOURCE_CLASS_DEPARTMENT_MISSING:{productClass.ClassRef}");
            }
            if (productClass.CanonicalClassRef is not null && !classes.ContainsKey(productClass.CanonicalClassRef))
            {
                violations.Add($"SOURCE_CLASS_CANONICAL_TARGET_MISSING:{productClass.ClassRef}");
            }
        }
        foreach (var redirect in TaxonomyV3Store.ClassRedirects)
        {
            if (!classes.ContainsKey(redirect.CanonicalClassRef))
            {
                violations.Add($"SOURCE_REDIRECT_TARGET_MISSING:{redirect.LegacyClassRef}");
            }
        }
        return violations.Count == 0;
    }

    private static async Task<bool> ColumnExistsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string schema, string table, string column, CancellationToken cancellationToken)
    {
        const string sql = "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema=@schema AND table_name=@table AND column_name=@column);";
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("schema", schema);
        cmd.Parameters.AddWithValue("table", table);
        cmd.Parameters.AddWithValue("column", column);
        return Convert.ToBoolean(await cmd.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<bool> ForeignKeyExistsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string schema, string table, string name, CancellationToken cancellationToken)
    {
        const string sql = "SELECT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE constraint_schema=@schema AND table_name=@table AND constraint_name=@name AND constraint_type='FOREIGN KEY');";
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("schema", schema);
        cmd.Parameters.AddWithValue("table", table);
        cmd.Parameters.AddWithValue("name", name);
        return Convert.ToBoolean(await cmd.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<bool> ProductClassReferencesValidAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken cancellationToken)
    {
        const string sql = "SELECT NOT EXISTS (SELECT 1 FROM catalog.products p LEFT JOIN catalog_v3.product_classes c ON c.class_ref=p.class_ref WHERE p.class_ref IS NOT NULL AND c.class_ref IS NULL);";
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        return Convert.ToBoolean(await cmd.ExecuteScalarAsync(cancellationToken));
    }

    private static string ComputeTargetFingerprint(IReadOnlyDictionary<string, int> surveys, IReadOnlyDictionary<string, int> operations, bool productClassForeignKeyPresent)
    {
        var builder = new StringBuilder();
        builder.Append("target-v1|").Append(productClassForeignKeyPresent).Append('|');
        foreach (var row in surveys.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            builder.Append(row.Key).Append('=').Append(row.Value).Append('|');
        }
        foreach (var row in operations.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            builder.Append(row.Key).Append('=').Append(row.Value).Append('|');
        }
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static async Task<bool> TableExistsAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string schema, string table, CancellationToken cancellationToken)
    {
        const string sql = "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = @schema AND table_name = @table);";
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("schema", schema);
        cmd.Parameters.AddWithValue("table", table);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToBoolean(result);
    }

    private static async Task<int> QueryTableCountIfExistsAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string schema, string table, CancellationToken cancellationToken)
    {
        bool exists = await TableExistsAsync(conn, tx, schema, table, cancellationToken);
        if (!exists)
        {
            return 0;
        }

        return await QueryScalarIntAsync(conn, $"SELECT count(*) FROM {schema}.{table};", cancellationToken, tx);
    }

    private static async Task<int> QueryScalarIntAsync(NpgsqlConnection conn, string sql, CancellationToken cancellationToken, NpgsqlTransaction? tx = null)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    private static async Task<string> QueryScalarStringAsync(NpgsqlConnection conn, string sql, CancellationToken cancellationToken, NpgsqlTransaction? tx = null)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result?.ToString() ?? string.Empty;
    }

    /// <summary>
    /// Captures a deterministic digest of everything the preview could conceivably mutate:
    /// catalog/catalog_v3 schema objects, foreign keys, sequence positions and the full contents
    /// of the operational and taxonomy tables the importer reads. Executed strictly with SELECT
    /// statements so the capture itself cannot write. Two identical digests bracketing the
    /// measured call are the authoritative no-write oracle.
    /// </summary>
    internal static async Task<string> CapturePersistentStateFingerprintAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();

        // Schema surface: every relation and column in the affected schemas.
        sb.Append("SCHEMA=");
        sb.Append(await QueryScalarStringAsync(conn, """
            SELECT COALESCE(md5(string_agg(x, '|' ORDER BY x)), 'EMPTY') FROM (
              SELECT format('%s.%s.%s.%s', table_schema, table_name, column_name, data_type) AS x
              FROM information_schema.columns
              WHERE table_schema IN ('catalog', 'catalog_v3', 'inventory', 'sales', 'thaka')
            ) s;
            """, cancellationToken, tx));

        // Foreign keys and their delete behaviour.
        sb.Append("|FK=");
        sb.Append(await QueryScalarStringAsync(conn, """
            SELECT COALESCE(md5(string_agg(x, '|' ORDER BY x)), 'EMPTY') FROM (
              SELECT format('%s.%s.%s.%s', cn.nspname, ct.relname, c.conname, c.confdeltype) AS x
              FROM pg_constraint c
              JOIN pg_class ct ON ct.oid = c.conrelid
              JOIN pg_namespace cn ON cn.oid = ct.relnamespace
              WHERE c.contype = 'f' AND cn.nspname IN ('catalog', 'catalog_v3', 'inventory', 'sales', 'thaka')
            ) s;
            """, cancellationToken, tx));

        // Sequence positions. pg_sequences exposes last_value; information_schema.sequences does not.
        sb.Append("|SEQ=");
        sb.Append(await QueryScalarStringAsync(conn, """
            SELECT COALESCE(md5(string_agg(x, '|' ORDER BY x)), 'EMPTY') FROM (
              SELECT format('%s.%s=%s', schemaname, sequencename, COALESCE(last_value::text, 'NULL')) AS x
              FROM pg_catalog.pg_sequences
              WHERE schemaname IN ('catalog', 'catalog_v3', 'inventory', 'sales', 'thaka', 'system')
            ) s;
            """, cancellationToken, tx));

        // Full row contents for every table the dry run surveys or plans to mutate. Ordering is by
        // the row text itself so the digest never depends on a column name that may not exist.
        foreach (var (schema, table) in new (string Schema, string Table)[]
                 {
                     ("catalog", "categories"),
                     ("catalog", "products"),
                     ("catalog", "units"),
                     ("catalog", "companies"),
                     ("catalog_v3", "divisions"),
                     ("catalog_v3", "departments"),
                     ("catalog_v3", "product_classes"),
                     ("catalog_v3", "class_redirects"),
                 })
        {
            if (!await TableExistsAsync(conn, tx, schema, table, cancellationToken))
            {
                sb.Append($"|{schema}.{table}=ABSENT");
                continue;
            }
            sb.Append($"|{schema}.{table}={await QueryScalarStringAsync(conn,
                $"SELECT COALESCE(md5(string_agg(t::text, '|' ORDER BY t::text)), 'EMPTY') FROM {schema}.{table} t;",
                cancellationToken, tx)}");
        }

        return sb.ToString();
    }

    private static string ComputeStoreHash() => ComputeStoreHashWithOptions(null);

    private static string ComputeStoreHashWithOptions(TaxonomySeedOptions? options)
    {
        var sb = new StringBuilder();

        // 1. Divisions (9)
        foreach (var div in TaxonomyV3Store.Divisions)
        {
            sb.Append($"DIV:{div.Code}:{div.Name}|");
        }

        // 2. Departments (65)
        foreach (var dep in TaxonomyV3Store.Departments)
        {
            sb.Append($"DEPT:{dep.Code}:{dep.Name}:{dep.DivisionCode}:{dep.DivisionName}:{dep.V3ActiveLeafCount}:{dep.V3DeprecatedCount}:{dep.V3NavigationOnlyCount}|");
        }

        // 3. Product Classes (732)
        foreach (var cls in TaxonomyV3Store.ProductClasses)
        {
            sb.Append($"CLASS:{cls.ClassRef}:{cls.ClassName}:{cls.DivisionCode}:{cls.DepartmentCode}:{cls.ClassType}:{cls.ClassStatus}:{cls.CatalogRole}:{cls.CanonicalClassRef}:{cls.ClassificationRule}|");
        }

        if (options?.ExtraClasses != null)
        {
            foreach (var cls in options.ExtraClasses.OrderBy(x => x.ClassRef, StringComparer.Ordinal))
            {
                sb.Append($"EXTRA_CLASS:{cls.ClassRef}:{cls.ClassName}:{cls.DivisionCode}:{cls.DepartmentCode}:{cls.ClassType}:{cls.ClassStatus}:{cls.CatalogRole}:{cls.CanonicalClassRef}:{cls.ClassificationRule}|");
            }
        }

        // 4. Class Redirects (42)
        foreach (var red in TaxonomyV3Store.ClassRedirects)
        {
            sb.Append($"REDIR:{red.LegacyClassRef}:{red.LegacyLabel}:{red.CanonicalClassRef}:{red.CanonicalLabel}:{red.Reason}:{red.MigrationPolicy}:{red.Status}|");
        }

        if (options?.ExtraRedirects != null)
        {
            foreach (var red in options.ExtraRedirects.OrderBy(x => x.LegacyClassRef, StringComparer.Ordinal))
            {
                sb.Append($"EXTRA_REDIR:{red.LegacyClassRef}:{red.LegacyLabel}:{red.CanonicalClassRef}:{red.CanonicalLabel}:{red.Reason}:{red.MigrationPolicy}:{red.Status}|");
            }
        }

        // 5. Unit Definitions (51)
        foreach (var unit in TaxonomyV3Store.UnitDefinitions)
        {
            // FactorToReference is a decimal. Interpolating it under the ambient culture would emit
            // a comma decimal separator on e.g. de-DE and silently change this hash, so a manifest
            // produced on one host would not match the manifest verified on another. It is formatted
            // invariantly, and PhysicalDimension is projected to its numeric value rather than its
            // name so a rename of an enum member cannot silently alter an already-recorded manifest.
            sb.Append($"UNIT:{unit.LocalCode}:{unit.Name}:{(int)unit.Dimension}:{FormatInvariant(unit.FactorToReference)}:{unit.ConversionKind}:{unit.VerifiedUneceRec20Code}:{unit.IsActiveSupported}|");
        }

        // 6. Facet Rules
        foreach (var facet in TaxonomyV3Store.FacetRules)
        {
            sb.Append($"FACET:{facet.ClassRef}:{facet.FacetName}:{facet.ValueType}:{facet.SuggestedEnumValues}:{facet.Applicability}:{facet.VariantIdentityPolicy}:{facet.Requiredness}|");
        }

        // 7. Attribute Profiles
        foreach (var attr in TaxonomyV3Store.AttributeProfiles)
        {
            sb.Append($"ATTR:{attr.ProfileCode}:{attr.DepartmentCode}:{attr.AttributeKey}:{attr.AttributeName}:{attr.ValueType}:{attr.UnitSymbol}:{attr.EnumSuggestedValues}:{attr.NumericMin}:{attr.NumericMax}:{attr.VariantRole}:{attr.EffectiveRequiredness}|");
        }

        // 8. Search Aliases
        foreach (var alias in TaxonomyV3Store.SearchAliases)
        {
            sb.Append($"ALIAS:{alias.TargetScope}:{alias.TargetRef}:{alias.Keyword}:{alias.Language}:{alias.LookupBehavior}:{alias.AmbiguityPolicy}|");
        }

        // 9. Ambiguous Terms
        foreach (var amb in TaxonomyV3Store.AmbiguousTerms)
        {
            var candidates = string.Join(",", amb.CandidateScopes);
            sb.Append($"AMB:{amb.QueryToken}:{candidates}:{amb.Reason}:{amb.ResolutionRule}|");
        }

        // 10. Collision Policies
        sb.Append($"POL:DIV:{TaxonomyCollisionPolicy.DivisionsPolicy}|");
        sb.Append($"POL:DEPT:{TaxonomyCollisionPolicy.DepartmentsPolicy}|");
        sb.Append($"POL:CLASS:{TaxonomyCollisionPolicy.ProductClassesPolicy}|");
        sb.Append($"POL:REDIR:{TaxonomyCollisionPolicy.ClassRedirectsPolicy}|");
        sb.Append($"POL:OP_CAT:{TaxonomyCollisionPolicy.OperationalCategoriesPolicy}|");
        sb.Append($"POL:DEL:{TaxonomyCollisionPolicy.DeletiveActionPolicy}|");
        sb.Append($"POL:ROLLBACK:{TaxonomyCollisionPolicy.ReferencedOperationalRollbackPolicy}|");

        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Renders a <see cref="decimal"/> in a culture-independent form so a manifest hash computed on
    /// one host locale is byte-identical to one computed on another.
    /// </summary>
    private static string FormatInvariant(decimal value) => value.ToString(CultureInfo.InvariantCulture);
}
