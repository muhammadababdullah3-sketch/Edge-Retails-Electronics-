# CAT Correction 107 — Continuation Record

Observed: 2026-10-22. This note records a bounded continuation of the approved CAT-107 work. It does not change CAT-106, authorize Phase8/9, establish batch acceptance, or authorize operations against live databases.

## Source and migration state

- Existing-shop upgrade preparation is in the working tree as `CatalogReferenceUpgradeHandler` plus `POST api/catalog/upgrade/fixed-units`; it seeds supported fixed Count/Length units only. Categories remain excluded pending an owner-approved operational mapping.
- A durable replay marker now uses the same-transaction audit event (`CATALOG_REFERENCE_UPGRADE` / `FIXED_UNITS_SEEDED`) keyed by the singleton installation ID. The handler checks it after taking the existing `FOR UPDATE` installation row lock, writes it alongside unit inserts, and the route explicitly checks `InventoryManage`.
- Focused unit tests cover first-run/replay and conflicting symbol behavior; `CatalogReferenceUpgradeHandlerTests` passed 2/2. Route-level authorization integration and concurrent-call verification are still missing. Do not execute this endpoint on a live shop or claim end-to-end concurrency proof.
- The active Infrastructure migration directory contains `20261021120000_Phase11CatalogCanonicalIntegration.cs` and its designer. The migration adds nullable product ClassRef and unit conversion fields; it does not create `catalog_v3` taxonomy tables and does not add the required ClassRef FK.
- Neither the separately scoped `20261021160049_Phase11ThakaCreditRefundTable` prerequisite nor the `20261021133157_Phase11CatalogV3RelationalIntegrity` migration is present in the active migration directory. Related candidate/manual bytes remain preserved under the pre-existing governance `before/` and `attempts/` paths; do not treat them as deployed/active migrations.
- `dotnet ef migrations has-pending-model-changes --project src/EdgeRetails.Infrastructure/EdgeRetails.Infrastructure.csproj --startup-project src/EdgeRetails.Infrastructure/EdgeRetails.Infrastructure.csproj` returned exit code 1 with “Changes have been made to the model since the last migration. Add a new migration.” The variant using `EdgeRetails.Server` as startup project built but failed because that startup project does not reference `Microsoft.EntityFrameworkCore.Design`; it is a tooling configuration failure, not a model-sync result.

## Importer review

- Current dry-run source opens a RepeatableRead transaction, marks it `READ ONLY`, explicitly checks migration prerequisites, and no longer executes schema DDL in dry-run.
- Apply sets Serializable isolation and acquires the advisory transaction lock before validating schema and reading target-dependent state.
- The manifest hash still omits canonical source fields/entities (notably some class/redirect properties, search aliases, and ambiguous terms). `VerifyAppliedRowsAsync` verifies target key sets and FK presence but not every persisted row field. Consequently, `PreviewApplyParityAchieved` is not yet evidence of full semantic parity. Do not claim the manifest is complete or the post-apply proof is exact.

## Validation and environment

Passed:

- `dotnet build tests/EdgeRetails.UnitTests/EdgeRetails.UnitTests.csproj --no-restore -v:minimal` — exit 0, 0 warnings, 0 errors.
- `dotnet test tests/EdgeRetails.UnitTests/EdgeRetails.UnitTests.csproj --no-build --no-restore --filter FullyQualifiedName~CatalogV3UnitAndTaxonomyTests -v:minimal` — 8 passed, 0 failed.
- After adding focused one-time fixed-unit upgrade tests: `dotnet build tests/EdgeRetails.UnitTests/EdgeRetails.UnitTests.csproj --no-restore -v:minimal` — exit 0, 0 warnings/errors; `dotnet test tests/EdgeRetails.UnitTests/EdgeRetails.UnitTests.csproj --no-build --no-restore --filter FullyQualifiedName~CatalogReferenceUpgradeHandlerTests -v:minimal` — 2 passed, 0 failed.
- Rebuilt `tests/EdgeRetails.IntegrationTests/EdgeRetails.IntegrationTests.csproj --no-restore -v:minimal` after the setup repository interface change — exit 0, 0 warnings/errors.
- `dotnet test tests/EdgeRetails.Desktop.PerformanceTests/EdgeRetails.Desktop.PerformanceTests.csproj --no-restore --filter FullyQualifiedName~Imp104_ProductManagement_TaxonomyTree_HierarchyAndRedirectNotice_WiredToCatalog -v:minimal` — 1 passed, 0 failed.
- `dotnet build tests/EdgeRetails.IntegrationTests/EdgeRetails.IntegrationTests.csproj --no-restore -v:minimal` — exit 0, 0 warnings, 0 errors.
- `git diff --check` — exit 0; line-ending normalization warnings only.

Blocked, failed closed:

- `dotnet test tests/EdgeRetails.IntegrationTests/EdgeRetails.IntegrationTests.csproj --no-build --no-restore --filter FullyQualifiedName~CAT107_DisposableGuardRejectsProductionAndConfirmsOwnedPostgres18 -v:minimal` — 1 test failed because `EDGE_RETAILS_TEST_DB` is unset. The guard threw before opening a database connection.
- Port probe found listeners on 5432 and 7150 (prohibited) and none on 55437 (required). No PostgreSQL connection, migration, write, or integration database operation was performed.
- Do not run the current rehearsal script as-is: its 55640 port conflicts with the integration guard, and its test-role grants do not establish schema privileges.

## Remaining work

1. Add a durable one-time marker to the explicit existing-shop upgrade with concurrency-safe lock/update behavior, add handler repeat-run/conflict tests, and add an authenticated route integration test. Preserve existing unit IDs/history and fail closed on symbol/name/conversion conflicts.
2. Add the distinct Thaka credit-refund prerequisite migration and reconcile only its modeled table/index/check/FK with the snapshot, without absorbing unrelated dirty model changes.
3. Add the separate CAT relational migration that creates taxonomy tables and the restrictive ClassRef FK, with explicit fail-closed handling of pre-existing conflicting schemas/rows and protective `Down` behavior. Preserve legacy NULL ClassRef rows.
4. Make the manifest hash cover all canonical taxonomy entities and attributes; make apply verification compare full persisted semantic rows, not just key sets/counts. Add tests for stale target drift, non-key manifest changes, preview read-only behavior on missing prerequisites, and exact post-state parity.
5. Run PG18 integration only after a separately attested disposable target conforms to the existing guard (55437, explicit test DB env routing, owned cluster/process/data directory/database/roles). Keep ports 5432/7150 untouched. If setup cannot be safely proven, keep PG integration blocked.
6. Perform the planned distinct read-only review and dual-tree evidence readback before any CAT batch acceptance. Phase11 D05 and Phase8/9 remain separate and unaccepted.
