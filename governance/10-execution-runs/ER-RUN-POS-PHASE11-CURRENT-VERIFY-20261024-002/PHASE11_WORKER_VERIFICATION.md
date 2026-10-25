# Phase 11 current-candidate worker verification

**Disposition: WORKER_VERIFIED_WITH_ACCEPTANCE_BLOCKERS; NOT FORMALLY CLOSED.**

## Candidate and scope

- Repository: `C:\Users\muham\OneDrive\Desktop\Point of Sale`
- Branch: `feat/phase11-subledger-implementation`
- Base commit inspected: `6b5bbd04f35aa1bce3362751185b60d3cabfc8e6`
- Verification run: `ER-RUN-POS-PHASE11-CURRENT-VERIFY-20261024-002`
- Owner request: resolve reproducible current-candidate issues, verify the code and close Phase 11. No production database, deployment, or external-chat action was requested.
- This run includes the working-tree fixes listed below. The test results are bound to those exact after-bytes in `MANIFEST.json`; the base commit alone is not the tested candidate.

## Source findings and repairs

1. **Ambiguous stocktake POST responses could discard a durable operation intent.** `RemoteStocktakeWorkflowService` used a broad status classification that treated 4xx responses as final. It now uses the shared `DesktopOperationRejectionClassifier` and retains intent for ambiguous responses. New tests cover 408, 409, and 429, a failed recovery lookup, persisted pending intent, and retry with the same operation ID.
2. **The PostgreSQL rehearsal runner accepted ports rejected by the live test guard.** Its range now matches the dedicated 55437–55438 ports. The runner resolves the actual .NET SDK executable when `dotnet` is absent from PATH, restricts integration selection to the relevant catalog and Phase 11 classes, and removes only its own credential-bearing bootstrap files on both success and failure while retaining failed-run logs.
3. **LF14 reversal-only activity lacked real-provider proof.** A PG18 test now issues material in an earlier period and reverses it in the selected period, then verifies the signed activity row (`IssueCount=0`, `ReversalCount=1`, reversal amount 5000, signed net -5000). The activity row is verified; the headline `ThakaMaterial` aggregate remains zero-clamped in `BusinessOperationsReadServices.GetSnapshotAsync`.
4. **The Thaka transaction race test could double-subtract a refund in its expected-value calculation.** Its expected DTO is now derived from persisted issue, reversal, discount, payment, payment reversal and refund ledgers.
5. **Catalog lifecycle tests were sensitive to EF provider-cache warnings after broad fixture construction.** The seed test applies the same specific warning handling used by neighboring production/test setup; this does not suppress unrelated warnings globally.
6. **A catalog UI test relied on a fixed 350 ms delay.** It now waits on a call signal, making the oracle deterministic under suite load.
7. **A Phase 12 path test depended on write access to the machine-wide ProgramData directory.** It now uses and verifies a dedicated temporary common-application-data root. This is test isolation only and does not change Phase 12 source behavior or closure status.

## Verification results

Commands used .NET SDK 10.0.401. Test outputs and terminal results are preserved under `evidence/`.

| Verification | Result | Evidence |
|---|---:|---|
| Unit suite | 1,419 passed, 0 failed | `evidence/unit-final/phase11-current-unit.trx` |
| Full Desktop performance test project with isolated state directory | 486 passed, 0 failed | `evidence/desktop-current-final/desktop-current.trx` |
| Selected catalog and Phase 11 integration classes on owned PostgreSQL 18 | 146 passed, 0 failed | `evidence/pg18-optionb-clamp-final/muham_ALI_2026-10-24_10_25_56_net10.0.trx` |
| EF migrations and model-sync check | PASS | `evidence/pg18-cleanup-final/catalog-postgres-rehearsal.log` and `terminal-result.json` |
| PG18 startup, ownership/routing, shutdown and stopped-state check | PASS | same PG18 terminal result; loopback `127.0.0.1:55437`, owned temp data directory, `pg_ctl status` returned stopped |
| Generated credential-file cleanup | PASS | same PG18 runner log/terminal result; `admin.pw` removed; failed-run logs retained |
| PowerShell parse and `git diff --check` | PASS | observed during final harness verification |

The three passing test selections contain 2,051 test executions (1,419 unit + 486 Desktop + 146 selected PG18); the latest PG18 result is also a rerun of that same 146-test selection on the strengthened LF14 test bytes. Earlier failed/intermediate attempts remain in this run directory as raw history and are not counted as passes. Two failed owned temp roots from this run had only their generated `admin.pw` files removed after ownership markers, `pg_ctl` stopped state and absence of `postmaster.pid` were verified; their failure logs and data directories were retained. No operational PostgreSQL endpoint was used. Ports 5432 and 7150 were not contacted.

## Independent read-only review

Two separate read-only Codex reviewers inspected the changed stocktake/LF14 paths before the final PostgreSQL rerun. They agree that the stocktake classifier correction is sound and the provider-backed activity-row case is covered. Their review preceded the current-byte rerun and did not cover all 125 original Phase 11 task criteria or issue formal D05 acceptance. The addendum below binds the strengthened LF14 oracle and result.

## Closure decision

I verified the listed source paths and test slices as the implementation worker. **I did not mark Phase 11 closed.** One acceptance condition remains unsupported by this worker evidence:

- Distinct D05 acceptance: no independent whole-Phase 11 review/decision is bound to the exact current working-tree candidate. A source author/worker cannot issue that distinct acceptance. Existing parent-candidate reviews do not bind this after-byte candidate.

Accordingly the accurate result is **implementation/test slices verified; formal Phase 11 closure pending complete original-scope reconciliation and candidate-bound distinct D05 review**. This does not reopen Phase 12, authorize Phase 8/9 work, or certify deployment.

## Addendum — fresh current-byte PostgreSQL rerun (2026-10-24)

After the report above was drafted, the LF14 PostgreSQL test was strengthened to assert the current Option B headline behavior alongside its signed activity row: the reversal-only row remains `IssueCount=0`, `ReversalCount=1`, `ReversalAmount=5000`, `SignedNet=-5000`, while the existing snapshot headline remains `ThakaMaterial=0`. The owned PostgreSQL 18 rehearsal was rerun on those after-bytes.

- Raw result: `evidence/pg18-optionb-clamp-final/muham_ALI_2026-10-24_10_25_56_net10.0.trx`; 146 passed, 0 failed.
- The same TRX records `PackConcurrencyPostgresTests.Deadlock_Or_Serialization_Retry_Is_Bounded` as Passed. Reading the test source confirms that it creates and observes a real PostgreSQL `40001` serialization failure, then checks bounded application retries, clean exhaustion, and no duplicate side effects. The method does not force a deadlock; no deadlock-specific proof is claimed.
- `evidence/pg18-optionb-clamp-final/terminal-result.json` records owned PostgreSQL 18 at loopback port 55437, migration/model-sync success, clean shutdown/stopped-state proof, and removal of the generated password file. Port 5432 and 7150 were not used.
- This verifies the selected Option B behavior: a provider-translated reversal-only activity row retains signed net while the headline remains clamped to zero. No report policy change is proposed; this focused proof does not establish the other Phase 11 gates.

## Original-criteria reconciliation status

Independent read-only source audits recovered the original 16 capability gates and four terminal conditions from the preserved Sept. 28 authority, and the original Final21 identity from the preserved Section 16 artifact. They found that the current run does not map all original 125 task rows, all 16 gates, and all Final21 criteria to exact candidate bytes and raw results. Several historical task-register copies also assign different meanings to P11-IMP-108; the preserved original/register and current concurrency source support the hostile concurrency interpretation, and this run now proves the current P11-PLAN-CONCURRENCY-14 serialization-failure case. The second meaning (PIN hashing) is not the controlling 108 criterion in the preserved Sept. 28 authority. The run still lacks complete 125-row/Final21 evidence, owner-reconciliation proof, full IntegrationTests gate, Solution Debug/Release builds, immutable migration-hash comparison, and candidate-bound distinct whole-Phase11 D05 acceptance. Thus worker verification is updated to **current-byte source/test slices verified; Phase 11 NOT formally closed**.

## Evidence integrity

- `BASELINE.json` records the starting commit, branch, initial tracked status and protected database-variable handling.
- `evidence/before/` holds byte-exact baselines for the changed files.
- `MANIFEST.json` hashes every deposited run artifact except the manifest itself; `MANIFEST.sha256` hashes the manifest.
- The run retains failed attempts and intermediate results. No previous run or owner source was overwritten.
