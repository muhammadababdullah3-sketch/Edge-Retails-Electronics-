using System;
using System.Collections.Generic;
using EdgeRetails.Domain.Catalog;

namespace EdgeRetails.Application.Features.Catalog;

/// <summary>
/// Explicit collision, upsert, and delete policies for Catalog V3 taxonomy and operational entities.
/// </summary>
public static class TaxonomyCollisionPolicy
{
    public const string DivisionsPolicy = "ON CONFLICT (code) DO UPDATE SET name = EXCLUDED.name";
    public const string DepartmentsPolicy = "ON CONFLICT (code) DO UPDATE SET name = EXCLUDED.name, division_code = EXCLUDED.division_code, active_leaf_count = EXCLUDED.active_leaf_count, deprecated_count = EXCLUDED.deprecated_count, navigation_only_count = EXCLUDED.navigation_only_count";
    public const string ProductClassesPolicy = "ON CONFLICT (class_ref) DO UPDATE SET class_name = EXCLUDED.class_name, division_code = EXCLUDED.division_code, department_code = EXCLUDED.department_code, class_type = EXCLUDED.class_type, class_status = EXCLUDED.class_status, catalog_role = EXCLUDED.catalog_role, canonical_class_ref = EXCLUDED.canonical_class_ref, classification_rule = EXCLUDED.classification_rule";
    public const string ClassRedirectsPolicy = "ON CONFLICT (legacy_class_ref) DO UPDATE SET legacy_label = EXCLUDED.legacy_label, canonical_class_ref = EXCLUDED.canonical_class_ref, canonical_label = EXCLUDED.canonical_label, reason = EXCLUDED.reason, migration_policy = EXCLUDED.migration_policy, status = EXCLUDED.status";
    public const string OperationalCategoriesPolicy = "IMMUTABLE_PRESERVATION: Unique index ix_categories_identity_symbol and SaveCategoryHandler enforce zero collision with occupied symbols (e.g. GA4BC, ELEC). Any attempt to insert an occupied symbol fails closed with SQLSTATE 23505.";
    public const string DeletiveActionPolicy = "ZERO_DELETE: Seeding introduces zero DELETE statements. All operational entities and historical product class relationships are preserved without truncation or cascading.";
    public const string ReferencedOperationalRollbackPolicy = "DESTRUCTIVE_ROLLBACK_REJECTED: Deletion or destructive rollback of taxonomy classes actively referenced by operational products or inventory is strictly rejected fail-closed via foreign key constraints (SQLSTATE 23503).";
}

/// <summary>
/// Computed before/after dry-run change report produced by the application importer.
/// </summary>
public sealed record TaxonomyDryRunResult(
    string ReportType,
    string RunId,
    DateTime TimestampUtc,
    string ExecutionMode,
    string TargetDatabase,
    int TargetPort,
    string Authority,
    string TaskBinding,
    IReadOnlyDictionary<string, string> CollisionUpsertDeletePolicy,
    IReadOnlyDictionary<string, int> ExistingOperationalRowsSurvey,
    IReadOnlyDictionary<string, int> ExistingTaxonomyRowsSurvey,
    IReadOnlyDictionary<string, bool?> SafetyInvariantsEvaluated,
    IReadOnlyDictionary<string, int> PlannedTaxonomyOperations,
    int PlannedDeletes,
    bool OccupiedSymbolsPreserved,
    string TestedOccupiedSymbol,
    bool ZeroDestructiveOperations,
    bool NoWriteVerified,
    bool LsnUnchangedDiagnostic,
    string TargetFingerprint,
    IReadOnlyList<string> InvariantViolations,
    bool IsSafeToApply,
    string ManifestSha256,
    string Verdict);

/// <summary>
/// Execution result from applying the catalog seed via the application importer.
/// </summary>
public sealed record TaxonomySeedResult(
    bool Success,
    string ExecutionMode,
    DateTime TimestampUtc,
    string TargetDatabase,
    int TargetPort,
    int DivisionsUpserted,
    int DepartmentsUpserted,
    int ProductClassesUpserted,
    int ClassRedirectsUpserted,
    int PlannedDeletes,
    int ActualDeletes,
    bool PreviewApplyParityAchieved,
    string AppliedManifestSha256,
    string Description,
    string? ErrorMessage = null);

/// <summary>
/// Execution options for seeding, supporting upgrade and interruption tests.
/// </summary>
public sealed record TaxonomySeedOptions(
    IReadOnlyList<ProductClass>? ExtraClasses = null,
    IReadOnlyList<ClassRedirect>? ExtraRedirects = null,
    bool SimulateInterruption = false,
    bool SimulateFailure = false);

/// <summary>
/// Application service contract for Catalog V3 seeding and dry-run execution.
/// </summary>
public interface ITaxonomyV3Importer
{
    /// <summary>Calculates a read-only preview against an already migrated schema.</summary>
    Task<TaxonomyDryRunResult> ExecuteDryRunAsync(string runId, CancellationToken cancellationToken = default);

    /// <summary>Applies taxonomy seed data idempotently under an advisory transaction lock.</summary>
    Task<TaxonomySeedResult> ApplySeedAsync(TaxonomySeedOptions? options = null, CancellationToken cancellationToken = default);
}
