# Phase 3 Legacy Category Upgrade Correction — 2026-09-29

## Authority and proven defect

The user explicitly requires the released 1.0.3 migration `20260925142150_Phase1SemanticProductIdentity` to remain immutable. Canonical architecture §79 likewise freezes released migrations. The 1.0.3 binary adds non-null `catalog.categories.identity_symbol` with the shared default `''`, then creates a unique index. PostgreSQL 18.6 rejects an existing database with two categories at that index (`23505`). The controlled production attempt rolled back fully: migration history remains at 13 and the new column is absent. An isolated PostgreSQL 18.6 / Npgsql rehearsal independently reproduced the same failure and rollback.

## Forward recovery design

The immutable migration cannot reach a later corrective migration while two legacy categories remain in `catalog.categories`. The approved recovery therefore has two explicit forward steps:

1. After a verified custom-format PostgreSQL backup and restore rehearsal, run `scripts/Invoke-Phase3LegacyCategoryPreMigration.ps1` against the exact production target while Server and Worker are stopped. It must fail closed unless the target is PostgreSQL 18, migration history is exactly the known 13-migration baseline, and no category-dependent rows exist. In one transaction, stage all legacy category IDs, names, and active states in `system.phase3_legacy_category_hold`, verify the staged copy, then remove only those category rows. This leaves the released migration source and history untouched. The staging table makes a power loss between the steps recoverable.
2. Apply the approved EF chain, including new append-only `20260929100000_Phase3LegacyCategoryUpgradeRecovery`. The released migration executes against an empty category table. The new migration restores the staged rows with valid deterministic unique symbols, verifies preservation, and drops the staging table inside its EF transaction. It is a forward-only data recovery migration. On a fresh database without staged rows, it is a no-op; it also handles an existing single category that passed the released migration with an empty symbol.

The symbol scheme uses the first ASCII letter of the uppercase name (fallback `C`) followed by the three-character base-36 ordinal within that letter, ordered by name using the `C` collation and then by ID. `Fan` and `Fridge` become `F000` and `F001`. It must fail closed on overflow, duplicate, invalid, or missing data. No product, stocktake, SKU, or other business rows may be altered by the pre-step. The pre-step rejects any category references; a different upgrade plan is required for such a database.

## Mandatory pre-production proof

1. Keep both installed application services stopped and disabled. Do not retry the operational migration yet.
2. On isolated PostgreSQL 18 / Npgsql, reproduce two existing-category failure cases from the deployed 13-migration baseline. For each, verify a custom backup, restore it to a fresh database, run the pre-step on that clone, apply the immutable migration chain plus the new forward recovery migration, and prove exact ID/name/active-state preservation, valid unique symbols, schema constraints, and no pending migrations.
3. Verify zero-to-latest migration separately, then `dotnet ef migrations has-pending-model-changes` against current source to distinguish model drift from database pending state.
4. Run protected Phase 1 and Phase 2 PostgreSQL regressions, full Phase 3 and repository regressions, and clean Debug/Release builds. Classify any test/harness changes. Do not weaken assertions or skip required gates.
5. Publish and install the corrected Release package using existing approved deployment assets. Match source, published, and installed Infrastructure DLLs. Take and verify a new operational backup immediately before the pre-step. Apply only the approved forward chain against the same ProgramData authority.
6. Verify migration history and schema, installed Server loopback readiness HTTP 200, `scripts/Test-EdgeRetailsServices.ps1`, Worker Running/Auto/recovery, and installed Desktop API-only startup smoke.
7. Freeze the workspace after all terminal gates and obtain a fresh independent read-only certification verdict. Phase 4 remains paused until this recovery is green.
