# CAT-107 — Disposable PostgreSQL 18 Rehearsal Result

**Run:** `ER-RUN-CATALOG-CORRECTION-20261021-107`  
**Latest evidence directory:** `test-results/pg18-20261022-113154-201/`  
**Status of CAT-107 overall:** **OPEN** (this document records test evidence only, not acceptance)

> ⚠️ **Superseded claim:** an earlier version of this file reported "9/9 PASS" obtained with
> `--no-data-checksums`, `wal_log_hints = off`, `autovacuum = off` and an in-method `CHECKPOINT`.
> Those crutches were disallowed and have been removed. See `CORRECTION_NOTICE.md`.
> The evidence below is from a **default-configuration** PostgreSQL 18 cluster.

## Attestation

- Dedicated loopback port `55437` (`127.0.0.1`), owned disposable cluster under `%TEMP%\EdgeRetailsCatalogPg_*`.
- Engine: PostgreSQL 18 (`server_version_num` asserted in range 180000–189999).
- Separate roles: migration admin `er_cat_admin`, runtime/test `er_provisioner`.
- **Default** PostgreSQL configuration: data page checksums, `wal_log_hints` and `autovacuum` unchanged.
- No artificial `VACUUM`/`CHECKPOINT` quiescence between migration and test execution.
- Protected port `5432` and server port `7150`: listener counts recorded, **zero connection attempts**.
- Clean fast shutdown, temp directory purged, environment variables restored (`cleanup_pass: true`).

## Test results — 75 / 75 passed (TRX verified per-test, 0 failed, 0 skipped)

TRX evidence file: `test-results/pg18-20261022-113154-201/muham_ALI_2026-10-22_16_33_15_net10.0.trx`

### 1. Catalog V3 Seed Lifecycle Tests (7 / 7 passed)
- `CAT107_DisposableGuardRejectsProductionAndConfirmsOwnedPostgres18`
- `ProductionMigrations_CreateCanonicalSchemaAndClassRefConstraint`
- `DryRun_NoWriteOracle_IsNotVacuous_DetectsCommittedMutation`
- `DryRun_WritesZeroBytes_And_CalculatesAccurateDiff`
- `FirstRun_And_RepeatRunIdempotency_PreservesExactCounts`
- `ProductClassForeignKey_RejectsInvalidClassAndReferencedDelete`
- `DestructiveRollbackAgainstReferencedOperationalData_IsRejected`

### 2. Catalog Reference Upgrade API Contract Tests (3 / 3 passed)
- `UpgradeFixedUnits_WithoutSession_Returns_401_Unauthorized`
- `UpgradeFixedUnits_WithoutInventoryManagePermission_Returns_403_Forbidden`
- `UpgradeFixedUnits_InitialExecution_Returns_200_OK_And_Replay_Returns_AlreadyApplied`

### 3. Catalog Mutation Route Authorization Matrix Tests (61 / 61 passed)
- `MatrixCoversEveryMutatingControllerAction` (Coverage assertion against reflection discovery of all 15 mutating routes)
- `MutatingRoute_WithoutSession_Returns_401` (15 theories across all mutating routes)
- `MutatingRoute_WithoutInventoryManagePermission_Returns_403` (15 theories across all mutating routes with validly-bound request bodies)
- `MutatingRoute_WithInventoryManagePermission_IsNotDenied` (15 theories verifying authorized caller is not denied by permission guard)
- `MutatingRoute_UnprivilegedCallerWithMalformedBody_NeverSucceeds` (15 theories verifying unprivileged callers never succeed regardless of model binding/validation order)

### 4. Catalog V3 Import Atomicity & Idempotency Tests (4 / 4 passed)
- `Apply_SimulatedInterruption_RollsBackEveryWrittenRow`: Mid-transaction interruption after upserting divisions, departments, and classes completely rolls back; persistent state fingerprint and row version (`xmin`) digests match pre-run exactly.
- `Apply_SimulatedFailureBeforeCommit_RollsBackEntireTransaction`: Pre-commit failure seam rolls back entire transaction; persistent state and row version digests match pre-run exactly.
- `Apply_SecondRun_PreservesExactPersistentStateAndZeroNetMutations`: Repeating apply against already-seeded database preserves exact persistent state (fingerprint identical), records 0 deletes, satisfies preview-apply parity, and dry-run diff confirms 0 inserts, 0 updates, 848 unchanged.
- `Apply_CompetingConcurrentRuns_SerializeAndLeaveExactState`: Two concurrent applies against same database serialize via transaction-scoped advisory locks / serializable isolation without corrupting data or leaving orphaned classes/redirects.

## Supporting suites

| Suite | Result |
| :--- | :--- |
| `EdgeRetails.UnitTests` (full) | 1419 / 1419 passed (includes 6 unit authorization tests) |
| `EdgeRetails.Desktop.PerformanceTests` | 62 / 62 passed (includes WPF STA LOGIN-SCROLL layout invariants at heights 640–1080px and non-vacuity detection) |
| `dotnet ef migrations has-pending-model-changes` | clean (exit 0) |

## Oracle design (what actually proves "zero writes")

The dry run is bracketed by a deterministic `CapturePersistentStateFingerprintAsync` digest over
schema surface, foreign keys, sequence positions and full row contents of every affected table.
`SHOW transaction_read_only` is read from inside the measured transaction. Both must hold for
`NoWriteVerified`; each failure appends an explicit invariant violation.
`LsnUnchangedDiagnostic` is reported and never asserted.

Test `DryRun_NoWriteOracle_IsNotVacuous_DetectsCommittedMutation` exists specifically so this oracle cannot pass vacuously.

## Progress on Owner-Identified Open Criteria

1. **Route Matrix Coverage**: **CLOSED**. All 15 mutating routes covered with 4 independent assertions each, plus reflection coverage assertion.
2. **Concurrency and Failure Modes**: **PROVEN**. Atomicity rollback under mid-transaction interruption and pre-commit failure, repeat-run idempotency, and concurrent run serialization directly proven on PostgreSQL 18.
3. **Fail-Open Unit Handlers**: **CLOSED for catalog**. `ConfigureProductUnitsHandler` and `SetProductUnitBarcodeHandler` fail closed on null authorizer. Outside catalog sites audited and catalogued in `FAIL_OPEN_AUTHORIZER_AUDIT.md`.
4. **LOGIN-SCROLL Requirement**: **CLOSED**. Real WPF STA assertions verify account list scrolling inside bounded viewport while login shell, PIN keypad, and sign-in button remain visible across all window heights >= 640px.
5. **Deterministic Manifest Details**: **NARROWED & DOCUMENTED**. Stale claim regarding aliases/ambiguous terms corrected in `MANIFEST_FINDINGS.md`. Two genuine defects recorded: `AppliedManifestSha256` ignoring applied extras, and preview vs apply target fingerprint key differences.
6. **Distinct Root / D05 Review**: **OPEN**. Independent root / D05 review of frozen candidate required before phase credit.

CAT-107 remains **OPEN** pending the above remaining items.
