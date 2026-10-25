# Phase 11 Worker Verification Handoff

**Disposition: NOT FORMALLY CLOSED.** This is a source-bound worker review and evidence receipt, not the distinct D05 acceptance or a governance closure decision.

## Candidate and method

- POS repository HEAD at review: `6b5bbd04f35aa1bce3362751185b60d3cabfc8e6` (`feat/phase11-subledger-implementation`).
- Parent candidate: `ea305c76ac9efeb6c109e182d54c3d7a942a05ff`.
- The current commit changes 56 source/test files relative to the prior reviewed candidate. A review or test result tied to the parent does not certify this candidate.
- No tracked source file was changed by this verification. Test outputs and this receipt are the only new artifacts in this run folder.
- Tests used .NET SDK 10.0.401 and PostgreSQL 18. Disposable clusters used loopback and ports 55437/55438, the repository test role `er_provisioner`, unique temporary data directories and ownership markers. Each cluster was stopped and `pg_ctl status` verified stopped. No operational database port was contacted.

## Results bound to this candidate

- Unit suite: **1,419 passed, 0 failed** (`evidence/unit/unit-current.trx`).
- Phase 11 desktop cutover slice: **35 passed, 0 failed** (`evidence/desktop-phase11/phase11-desktop-current.trx`).
- Catalog PostgreSQL selection: **119 passed, 0 failed** on the first guarded run (`evidence/pg18-catalog/*.trx`); migrations and model-sync check passed.
- Expanded Phase 11 PostgreSQL selection: **142 passed, 3 failed of 145** (`evidence/pg18-phase11-allowed-port/*.trx`). The three failures were investigated below.
- Catalog lifecycle class, rerun alone: **passed** on PostgreSQL 18 (`evidence/pg18-catalog-seed-isolated/*.trx`). Its two failures in the expanded run were EF Core `ManyServiceProvidersCreatedWarning` exceptions after other test classes had created many internal providers; the isolated class did not reproduce them.
- Thaka refund/material-issue race, rerun alone: **1 passed** (`evidence/pg18-thaka-oracle-isolated/*.trx`). The broader run also captured the alternate race outcome and the test's incorrect expected balance.
- Full desktop suite without a state override: **469 passed, 14 failed of 483**. Four tests directly failed with `UnauthorizedAccessException` writing under `C:\ProgramData\EdgeRetails\Desktop`; a supplier gate assertion also failed immediately after the same persistence setup path. The other nine failures were assertion/timeouts in expense, stock identity, editor retry, and Phase 7 intake tests under the shared full-suite run. With a unique temporary `EDGE_RETAILS_STATE_DIR`, excluding the single Phase 12 test that explicitly asserts the machine-root path, the suite passed **482/482** (`evidence/desktop-isolated-final/*.trx`); all 14 earlier failures disappeared in that run. The combined catalog-filter test failed once in the isolated full-suite run but passed alone (1/1), so its fixed 350 ms wait remains load-sensitive. These adjacent desktop results are not substitutes for Phase 11 acceptance.

## Findings from current source and raw test output

1. **Thaka race test oracle double-subtracts a credit refund.** `ThakaReadService` calculates `Paid` as payments minus payment reversals minus `credit_refunds`, then calculates `Balance` from that already-net `Paid` value (`src/EdgeRetails.Infrastructure/Services/ThakaReadService.cs`, `GetProjectAsync`, lines 339–344). The race assertion separately reads refund rows and subtracts them from `Paid` again (`tests/EdgeRetails.IntegrationTests/Phase11/ThakaPostgresTransactionIntegrationTests.cs`, lines 281–286). When both refund and issue succeed, raw output was `Expected: 900.00, Actual: 400.00`; 400 follows the SQL's own `MaterialValue - Paid` equation. When issue wins and the refund is rejected, the same test passes. This is an order-dependent test defect; it is not evidence of a product balance defect. The test must use an invariant that does not subtract the refund twice, then rerun both concurrency outcomes.
2. **The PostgreSQL runner's accepted port range disagrees with the live fail-closed guard.** `scripts/Invoke-CatalogPostgresRehearsal.ps1` accepts 55000–55999 (line 4), while `Phase11DurablePostingIntegrationTests.AssertDisposablePostgresTargetGuard` accepts only 55437 or 55438 (lines 105–108). The attempted 55439 run initialized and migrated its owned cluster, then the tests rejected the port. This produced 68 guard failures in the broad selection. That attempt contacted no operational database; the owned server stopped and was verified stopped. Use 55437/55438 and align the runner's validation with the test guard.
3. **Catalog lifecycle tests are sensitive to test ordering.** In the expanded run, two catalog lifecycle tests turned EF's process-wide `ManyServiceProvidersCreatedWarning` into exceptions. The class passed in isolation, while the catalog selection passed in the first run. Avoid creating a fresh EF internal service provider per fixture or run the tests in separate processes; do not waive the warning globally.
4. **The combined POS catalog-filter test uses a fixed 350 ms wait.** It missed the combined search call once in the full desktop run under concurrent load; the exact test passed alone. Replace timing-only synchronization with a deterministic signal/condition before treating this as a product regression.
5. **The prior D05 review is bound to the parent hash `ea305c7`, not current HEAD `6b5bbd0`.** There is one source commit between those candidates and it changes 56 source/test files. The prior review cannot serve as current-candidate D05 acceptance. The worktree history and raw results show progress occurred in a large batch; the finishing loop is being prolonged by stale candidate binding, cross-run evidence mismatch, and tests whose harness/oracle can fail for non-product reasons.

## Closure decision

The current test evidence supports the tested unit, desktop cutover, catalog and most Phase 11 PostgreSQL paths. It does **not** support formal Phase 11 closure yet: the PostgreSQL suite has an unresolved race-test oracle that fails on one valid interleaving, the runner/guard mismatch makes arbitrary approved-range ports unusable, and no distinct D05 acceptance is bound to current HEAD. Correct those test/harness defects, rerun the current candidate with raw results and the same source hash, obtain the required independent D05 review on that exact hash, then update the formal closure record. No closure or application defect waiver is asserted here.

## Fresh blocker check

At 2026-10-24 review time, `.git/edge-governance-workflow.lock` was absent in both the POS repository and the governance-body workspace. The previously reported stale lock is therefore not the current code-verification blocker. The current blockers are the candidate-bound D05 review and the concrete test/harness defects above.
## Evidence index

- `BASELINE.json` — candidate, parent, branch and starting environment snapshot.
- `evidence/unit/` — unit command log and TRX.
- `evidence/desktop/` — unmodified-environment desktop command log and TRX.
- `evidence/desktop-isolated/` — temporary state-dir diagnostic run.
- `evidence/desktop-targeted/` — isolated catalog filter rerun.
- `evidence/desktop-phase11/` — Phase 11 desktop cutover slice.
- `evidence/desktop-isolated-final/` — 482-test isolated desktop run.
- `evidence/pg18-catalog/` — guarded catalog run, 119 tests.
- `evidence/pg18-phase11/` — invalid-port guard probe; preserved, not counted as application tests.
- `evidence/pg18-phase11-allowed-port/` — expanded 145-test run; disposable server stopped after tests.
- `evidence/pg18-catalog-seed-isolated/` — catalog lifecycle class isolated rerun.
- `evidence/pg18-thaka-oracle-isolated/` — Thaka race test isolated rerun.
- `evidence/Invoke-Phase11DisposablePostgresTests.ps1`, `Invoke-Phase11DisposablePostgresTests-AllowedPort.ps1`, `Invoke-CatalogSeedPostgresTests-Isolated.ps1`, `Invoke-ThakaBalanceOracleTest.ps1` — preserved test-runner inputs and their modifications from the repository runner.
