# Edge Retails — final remediation and certification closure

Date: 2026-10-08. **Final verdict: SOURCE_REMEDIATION_PARTIAL_OWNER_DEPENDENCIES.**

The source candidate has verified fixes for six findings, seven remain partial, three require external owner work and one is held at release governance. This does not certify production. Preserve **PHASE1_BLOCKED_EXTERNAL_OWNER**, **DEPLOYMENT_HELD_BY_BACKEND_GOVERNANCE** and the prior Phase234 partial decision.

The companion [17-finding matrix](EDGE_RETAILS_17_FINDING_FINAL_STATUS_MATRIX.md) gives each finding's prior and final status, exact change, ownership, tests and dependency. [External owner handoff](EDGE_RETAILS_EXTERNAL_OWNER_HANDOFF.md) specifies contracts/files/hostile tests needed to close all remaining owner dependencies.

## Live baseline and change boundaries

Rechecked branch `tracking-remediation-20261002` and HEAD `97956831e01d7ed6b4e2f1051b15e8ec272e8e4b`. The working tree is substantially dirty from before this continuation, including protected Pass5/tracking source, scripts and tests; do not interpret current dirty files as all created by this remediation. `scratch/frontend-phase234-20261008/preflight-status.txt`, `preflight.patch`, and `preflight-hashes.json` are the incoming snapshot. Baseline included 953 tracked source/test/script/doc identities. Comparison shows 19 already tracked Desktop files changed during the authorized Phase2/3 work and no drift in those 953 files outside the allowed Desktop paths. New tests and `PurchaseCommittedReadbackException.cs` are separately included in the candidate manifests. This preserves rather than resets/stashes/rebases inherited work.

No Domain/Application/Server/Infrastructure/Worker source, schema or migration was modified by this continuation. `RemoteBackendBusinessOperationsService.cs`, warranty-owned source, tracking authority and Phase1 fixes remain protected. Independent reviewers accepted Phase2/3 only as partial frontend remediation and found no introduced defect. Their decisions and the final review are recorded in `scratch/frontend-phase234-20261008/independent-phase2-phase3-decisions.txt` and `independent-final-decision.txt`.

## Phase results

Phase2 candidate: 20 files, 20 focused tests passed; frozen regression run 45 passed = 20 new + 25 Phase1 preservation. Findings F06/F09/F10/F13/F14/F17 are verified at their bounded UI scope. F05 stays partial and F08 blocked; therefore Phase2 full certification was rejected. Detailed traces and limits are in `EDGE_RETAILS_PHASE2_3_4_REMEDIATION_REPORT.md`.

Phase3 candidate: 25 source/test files; final corrected frozen filter ran 52 passed = 27 Phase234 + 25 Phase1 preservation, 0 failed/skipped. The earlier intermediate 37-test run is not used as full preservation proof. F11/F12/F15/F16 remain blocked/partial as in the matrix; disabling unsupported production filters and cursor guards prevent false authority/completeness but do not provide the missing server contracts.

Phase4: Desktop, Server, Worker Release builds each succeeded with 0 warnings/errors. Khata UI failure/retry tests passed 2/2. Geometry/focus/responsive checks passed 30/30. PostgreSQL18 owned-cluster rehearsal passed its EnsureCreated bootstrap and 3 selected tests (2 Khata read/timestamp cases plus the existing expense void compensation case); exact result and verified shutdown/cleanup are in `postgres-model-fixture-attempt02/terminal-result.json`. The PostgreSQL test used the owned temporary database at 127.0.0.1:55649, `edge_retails_master_test`. Attempt01 compile failure and cleanup are retained separately; only the new test's missing empty SerializedUnits constructor argument was repaired before attempt02. No migration was applied. `EnsureCreated` evidence does not certify migration-only triggers or deployed schema.

Candidate DLL hashes and independent file rechecks: `candidate-assembly-identities.json`, `final-hash-verification.json`; all 30 Phase4 source entries, 25 Phase3 entries, 5 candidate assembly identities and 5 installed file identities matched at independent review. The Phase4 source list includes the two Khata UI tests. Build output and provisional release manifest are under `scratch/frontend-phase234-20261008/`.

## Release evidence and production hold

Fresh installed observations are in `installed-readonly-identities.json`: Desktop installed artifact is timestamped October 6; installed Server/Worker evidence is October 1. Server and Worker Infrastructure installed files have distinct hashes. The candidate was built to source `bin/Release` outputs and was not copied into installed paths. No operational shop DB connection/read, service control, app registration, normal Desktop shortcut change, production write, migration, import, deployment or rollout approval occurred. This follows the explicit instruction that no approved operational data source was available.

`PROVISIONAL_RELEASE_MANIFEST.json` records source revision, dirty-worktree candidate, five candidate assembly identities, current 24 source migration identities, capabilities and dependencies. Active Pass5 is explicitly NOT_FROZEN and installed schema is NOT_INSPECTED. Protocol 1.0.0 alone does not prove feature compatibility. Thus F01 remains DEPLOYMENT_HELD and no installed-production fix is certified. F02 is source verified partial: backend UTC/microsecond mapping plus frontend unavailable/retry behavior pass their bounded tests, but installed behavior is not certified.

## Final counts and required owner actions

Status totals: **6 FIXED_VERIFIED, 7 PARTIAL, 3 BLOCKED_EXTERNAL_OWNER, 1 DEPLOYMENT_HELD**. These correspond to the allowed per-finding status values and sum to 17. Full source certification is not claimed.

Priority closures are: agree and implement durable full-payload finance operation replay/reconciliation and restart recovery (F03/F04/F07); authorize expense Void recovery and warranty ownership (F05/F08); publish Brand/category query authority and complete pagination (F11/F12); supply bounded complete-ledger continuation (F15); define remaining operational diagnostics (F16); freeze Pass5 and certify installed/schema compatibility (F01/F02). Exact contract questions and hostile test acceptance conditions are detailed in the owner handoff document.

Final independent review rehashed all 40 final source/candidate/installed entries with zero mismatch, confirmed test/build/cleanup evidence and upheld **SOURCE_REMEDIATION_PARTIAL_OWNER_DEPENDENCIES**. The detailed reviewer record is `independent-final-decision.txt`. No deployment follows from this report. No Git commit/push/reset/stash/rebase was performed.
