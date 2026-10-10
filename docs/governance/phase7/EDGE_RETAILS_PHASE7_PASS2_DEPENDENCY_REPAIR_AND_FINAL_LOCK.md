# EDGE RETAILS — PHASE 7 PASS 2 DEPENDENCY REPAIR AND FINAL LOCK

Date: 2026-10-04. Workspace `C:\Users\muham\OneDrive\Desktop\Point of Sale`. Authority: attachment 8d9e7bad-fe4c-4ad1-866c-eb777e11c408. **PASS2_CERTIFIED_CLOSED_LOCKED.** All mandatory execution gates and fresh independent final review PASS. Evidence root `artifacts/phase7-pass2-dependency-20261004`.

## 1. Previous failed certification summary

The prior failed candidate manifest BAC968434BE8CFEDAD56D93CD33EF6D5EF046424CF78FF740D1BBE996067543C covered 810 files. Integrated real PostgreSQL185executed/181 passed / 4 failed / 0 skipped; concurrency4/7. All four terminal failures were inherited Thaka detail-read timestamp materialization, while earlier persisted business assertions executed. The failed hostile report, both failed validators, original manifest, REDs and185-run TRX remain intact. This report does not overwrite failure history.

## 2. Authorized dependency repair

Only OOS-THAKA-READ-01 is authorized. Seven Pass2 findings F01/D-ADJ-1/F02/F03/F04/P7-N02/P7-N03 remain unchanged. Separate raw-count finding is OUT_OF_SCOPE_RETAINED. Read-only AgentsA/B investigated contract/schema; AgentC authored only new regression file; AgentA performed bounded test/diff integrity reviews. AgentD returned a usage-limit error before any execution; lead resumed required commands. A different fresh read-only final validator must adjudicate after all mandatory tasks terminal. No operational DB access, frontend/Tracking authority mutation, later pass implementation or Git history mutation.

## 3. Timestamp schema authority

Canonical architecture specifies business timestamptz. Domain issue/payment properties DateTimeOffset; issue/payment writers use IClock.UtcNow=DateTimeOffset.UtcNow. Creation migration20260920164958_Sprint7ProductionCutover and current EF snapshot map `thaka.material_issues.issued_at` and `thaka.payments.recorded_at` to non-null timestamp with time zone, without default/conversion. Before production edit, actual owned PG18.6/180006 probe passed: both columns timestamptz/no default/NOT NULL/precision6, EF CLR DateTimeOffset. Native GetFieldType/GetValue yield DateTime/Utc; typed DateTimeOffset yields zero offset with identical microsecond ticks under Asia/Karachi session. Raw attestation in `read-contract-red-valid-fixture/test-results/*.trx`. Schema valid; no migration justified.

## 4. DTO/read contract authority

Public Thaka material IssuedAt and payment RecordedAt remain DateTimeOffset. Neighboring Sale/Purchase/Warranty public read models follow this convention; working Sales/Purchase/InventoryProvenance use private DateTime provider rows and explicit UTC DTO conversion. API returns canonical detail DTO; local/remote Desktop consume .LocalDateTime. No public DTO, API, serialization or consumer source changed. Static authority and primary references retained in `timestamp-forensic-authority.md`.

## 5. Root cause

SQL directly selected raw timestamptz, without cast/COALESCE/view conversion. Npgsql10.0.3 default field type is UTC DateTime. Dapper2.1.86 uses reader.GetFieldType to select positional record constructor, which cannot bind DateTime to DateTimeOffset absent a handler. Original direct DTO queries fail constructor selection even for empty material results. No global handler/legacy timestamp switch existed. This is a transport/public-constructor mismatch, not schema drift. [Npgsql timestamp documentation](https://www.npgsql.org/doc/types/datetime.html) and pinned package/source references are recorded in the forensic authority.

## 6. Production fix

Only ThakaReadService changes: private material/payment SQL row records use DateTime; every selected field maps unchanged into existing public DTOs. Helper requires KindUtc then constructs DateTimeOffset from the typed UTC value. NonUTC values fail explicitly rather than being relabeled. No strings, parsing, invented offset, local-machine timezone conversion, exception suppression, empty-list fallback, row skipping or global Dapper registration. SQL/order/project/catalog/connection behavior unchanged. Both timestamps share this single detail-read dependency; payment coverage prevents simply exposing the next identical boundary failure after materials are repaired.

## 7. Files changed

Baseline 810/810 hash verification PASS before edits. Branch `tracking-remediation-20261002`; HEAD `97956831e01d7ed6b4e2f1051b15e8ec272e8e4b`, preserved.

| File / scope | Dependency classification | Change |
|---|---|---|
| src/EdgeRetails.Infrastructure/Services/ThakaReadService.cs | B production | Private typed transport rows and UTC DTO mapping |
| tests/EdgeRetails.IntegrationTests/Phase7Pass2ThakaReadContractPostgresTests.cs | C test |5NEW_COVERAGE facts |
| Other809files from failed candidate | A unchanged | All prior Pass2/Pass1/Tracking/frontend/migration source preserved |
| This report, newmanifest, dependency evidence directory | D documentation/evidence | New audit history; historical files preserved |

Four original Pass2 production targets, PUCA/Tracking authority, Pass1 handlers, Desktop/Recovery and migrations unchanged relative to failed candidate. Exact production diff saved `authorized-production-repair.diff`; classification/source hashes in evidence CSVs. No unexpected source drift established.

## 8. Focused read-contract tests

Five unconditional real-PG facts: actual schema/native provider authority; empty detail; one material row; multiple material rows before/after reversal; payment-only detail. Existing owned harness attests actual cluster directory before seeding; canonical production DI/handlers and IThakaReadService used. Fixed UTC microsecond timestamp, one-microsecond ordering difference, zerooffset, JSON typed roundtrip and nonUTC session verified. All financial/identity/text/enum/reversal values map exactly.

First attempt0/5: 3 actual constructor failures, 2 new fixture receipt varchar40 errors. HARNESS_CORRECTION only receipt text to P7RP-+GuidN(37characters); assertions unchanged. Validfixture RED1 passed / 4 failed / 0 skipped: independent schema/native proof PASS and four detail cases actualRED. After fix focused5/5PASS, terminal exit 0/provider/cleanupPASS. Both attempts preserved. Snapshot equality covers project/issues/items/materialreversals/payments/stock/cost/units; inventory assertions additionally check sellable lot bucket sum. This is not an exhaustive database immutability proof. Payment is narrow persisted EF read fixture, not payment mutation certification; multiple rows are two single-item issues; reversed payments not separately covered.

## 9. Four prior failed cases rerun

Fresh exact-name selection **4 executed / 4 passed / 0 failed / 0 skipped**, terminal exit 0; Provider=PostgreSQL18/Npgsql; cleanupPASS (`prior-four-green/terminal-result.json`). Canonical final detail read retained in all four tests; no exception swallowed or alternate read substituted:

- F04_P7N03_Thaka_ContainerTwoTimes50_ExactProjectValueAndReversal_DespiteMutableUom
- E_TwoThakaProjectsSameUnit_ObservedLockWait_OnlyOneIssueAndCost
- F_ThakaIssueVsPurchaseReturn_ObservedLockWait_OnlyOneDerecognition
- G_ReversalVsNewIssue_ObservedLockWait_RestoredOriginalCanBeIssuedOnce

## 10. Full Pass 2 PostgreSQL certification

Fresh terminal integrated result **190 executed / 190 passed / 0 failed / 0 skipped**,190 actual rows/distinct execution IDs, 0 errors/aborts/timeouts; exit0. **Provider = PostgreSQL 18 / Npgsql**, actual18.6/180006. Existing Pass2 7/7; hostile numeric48/48; sevenraces7/7; fourrollback4/4; newread5/5; Pass1 23/23; Tracking83/83; sequence12/12; adjacent1/1. Pass2functional55/55 plus5read=60/60; the inherited seven findings remain unchanged. Actual counters, raw TRX, individual results and exact190-row requirement/test/persisted-assertion matrix retained under `final/`.

Existing owned runner starts fresh restricted-ACL loopback cluster, with random withheld credentials and process-local environment, runs canonical EF/migrations/test gates, verifies terminal shutdown and guarded cleanup. ProviderVerified=true/CleanupPass=true; stop exit0, status expected3, PID absent and owned-root deletion verified. Operational edge_retails_prod untouched. Every command has Command/ExitCode/Passed/Failed/Skipped/DatabaseProvider/CompletionStatus records. Focused historical runs are not added to integrated totals.

## 11. Concurrency 7/7

**Fresh 7/7 terminal PASS**, zero failed/skipped. All seven actual PG lock-wait races unchanged, including E/F/G retained detail reads. Intermediate assertion success is not substituted for terminal case success. Winner/contender tasks drain before teardown; observed blocking pins exact winner backend. No claim of every possible schedule.

## 12. Rollback 4/4

**Fresh 4/4 terminal PASS**, zero failed/skipped. Four unchanged real SQL-flush faults and fresh persisted rollback snapshots complete successfully: SetCount, Scrap, Thaka issue, reversal; no successoutcome/audit survives injected failure. No production failure hooks.

## 13. Numeric conservation

Exact prior numeric case and integrated rerun PASS confirm Thaka2×50=base100/charge50000/cost22000, mutable-factor reversal restores100/22000, projectnet0. New one/multiple material detail tests also PASS exact timestamp/numeric/reversal mapping. Integrated Container3×50→target2/count2/base100/loss10000, unchangedoriginalidentity/sequence; mixedScrap10000+12000=22000poolreduction/loss proofs PASS. Canonical final detail read now terminates successfully.

## 14. Pass 1 regression

PASS: fresh focused units22/22 and integrated realPG23/23,0 failed/skipped. Protected source unchanged.

## 15. Tracking regression

PASS: fresh focused units156/156 and realPG83/83, plus adjacent purchase/Tracking/lot1/1 inside190 total;0 failed/skipped. Authority source unchanged.

## 16. Sequence/custody regression

PASS: fresh focused units56/56 and protected realPG12/12;0 failed/skipped; no authority source change.

## 17. Full UnitTests

PASS: current focusedPass2 units16/16; fullUnitTests910 total / 910 passed / 0 failed / 0 skipped, terminal exit 0. Actual TRX/logs retained; focusedcounts overlapfullsuite, never additive.

## 18. Debug/Release

PASS: fresh IntegrationTestsRelease build terminal exit 0/0 warnings / 0 errors(6.88s); solutionDebug terminal exit 0/0 warnings / 0 errors(61.13s); solutionRelease terminal exit 0/0 warnings / 0 errors(23.07s). Raw logs and8exact final command records retained under `final/`; no active build/test/EF commands before independent review.

## 19. EF model

PASS: integrated canonical EF model command terminal exit 0/no changes; complete command record/runner cleanupPASS. Process-local owned authority prevents production fallback.

## 20. Migrations from zero

PASS: integrated owned EF update terminal exit 0; actual chain21 migrations applied fromzero. Protected TrackingMigrationRehearsal9/9 checks actual migration ID equality/no pending plus model-derived constraints/indexes/FKs and eight checkpoint paths. Chain retained `final/migration-chain.txt`. No newmigration/schema/snapshot edit.

## 21. Test-integrity review

Independent bounded AgentA review found no falsegreen/testweakening in5newfacts or fixture correction. Original tests—including exact four failed cases,185prior cases,7races,4rollback—unchanged. No ASSERTION_CHANGE/CONCURRENCY_CHANGE/SKIP_CHANGE in this repair. NEW_COVERAGE+one HARNESS_CORRECTION only. Bounded source review confirms allotherfields/SQL preserved and nonUTC rejects rather than reinterpretation. Fresh independent final review independently confirms no test weakening or blocking source/test correction.

## 22. Final source manifest

New `EDGE_RETAILS_PHASE7_PASS2_DEPENDENCY_FINAL_SOURCE_MANIFEST.sha256`, SHA256 **31A09DAC9C4B501D49F7A8E4168290037BC09B2676C00091AFA940E77F19BE1A**,811 files; branch/HEAD/relativepaths/hashes/dependencyintent/classification. A809 / B1 / C1 relative to failed 810-file candidate; its original manifest remains untouched. Candidate captured before final regressions; post-regression 811/811 live hash matches / zero drift. Manifestdigest/branch/HEAD unchanged; gitdiffcheckexit0. Terminalcensus: 0 active build/test/EF commands, PGexit0/cleanupPASS and all8unit/buildcommands terminal PASS. Evidence `final/post-regression-source-check.csv`, `all-final-command-records.json`, `pre-independent-terminal-census.json`.

Two evidence-only capture attempts preceded terminal PASS: WindowsPowerShell treated inherited Git line-ending notices as native stderr errors; capture was corrected to consume output and evaluate actual Git exit. Its ConvertFrom-Json array wrapper differed under WindowsPowerShell5, so direct assignment restored correct8record count. No source/test/assertion/gate modification; raw gate files unaffected. These diagnostic/capture attempts do not count as successful certification commands. Final capture verifies every mandatory status rather than assuming a count.

After independent PASS and documentation finalization, final read-only rehash again confirms811/811 matches/zero drift, both old/new manifest digests and branch/HEAD unchanged. No source/test/build/database work followed independent review. Evidence: `final/post-independent-source-check.csv`, `post-independent-final-state.json`, `post-independent-command-record.json`.

## 23. Independent validator

**PASS FOR LOCK** from NEW fresh read-only `/root/pass2_dependency_fresh_final_validator` (SOL6.1/High, forknone). Dispatched only after all final commands terminal, verified cleanup and sourcefreeze. Reviewer independently recomputed811 live hashes/complete corpus, old809 unchanged+1readchange+1newtest, exact190-row TRX/matrix equality and185 oldcases+5new cases. Actual four historical failures reconcile to currentPassed; all7race/4rollback cases terminalPASS. Reviewer checked timestamp authority/publiccontract, absence of suppression/shortcuts, testintegrity, allseven protected findings, unit/build/EF/migration/protected source evidence, unchangedbranch/HEAD and0activecommands. **No blocking findings or source/test corrections remain.**

Actual returned finalresponse is preserved verbatim in `artifacts/phase7-pass2-dependency-20261004/independent-final-validator.md`; root saved it, reviewer wrote nofiles and ran no build/test/database command. Prior validator conclusions were not used as authority. The report's pending placeholders were finalized only using this actual independentPASS; no lead self-certification.

## 24. Retained OOS-THAKA-RAWCOUNT-02

**OUT_OF_SCOPE_RETAINED**, Low, inherited static Thaka rawContainer admission gap; source-derived, not PG-reproduced; no observed value/identity loss. No repair, redesign or hostile reproduction bundled here. §56 broad fractional-admission answer remains qualified YES for inherited edge, NO for already-certified F01/F03 guard paths. Retention does not masquerade as a fixed finding.

## 25. Formal Pass 2 lock

**PASS2_CERTIFIED_CLOSED_LOCKED.** All mandatory final gates have terminalPASS and fresh independentPASS; no focused pass is substituted for full certification. Critical blockers0; High blockers0; blocked required gates0; unexecuted required gates0. Full required PostgreSQL190/190, all required regressions/builds/EF/migrations PASS. Seven finding statuses:

| Finding | Final status |
|---|---|
| F01 | CERTIFIED / CLOSED / LOCKED |
| D-ADJ-1 | CERTIFIED / CLOSED / LOCKED |
| F02 | CERTIFIED / CLOSED / LOCKED |
| F03 | CERTIFIED / CLOSED / LOCKED |
| F04 | CERTIFIED / CLOSED / LOCKED |
| P7-N02 | CERTIFIED / CLOSED / LOCKED |
| P7-N03 | CERTIFIED / CLOSED / LOCKED |

PROGRAM PHASE 7 — PASS 2

```text
STEP 1 FORENSIC AUDIT: COMPLETE / FROZEN
STEP 2 IMPLEMENTATION: COMPLETE
ADJACENT CERTIFICATION DEPENDENCY: OOS-THAKA-READ-01 FIXED
REAL POSTGRESQL CERTIFICATION: PASS
CONCURRENCY: PASS
ROLLBACK: PASS
PASS 1 NON-REGRESSION: PASS
TRACKING NON-REGRESSION: PASS
PROTECTED SEQUENCE / CUSTODY: PASS
MIGRATIONS: NONE NEW
FRONTEND: UNTOUCHED
PASS 2: CERTIFIED / CLOSED / LOCKED
```

Lock binds the811-file working-tree manifest, not HEAD alone. OOS-THAKA-RAWCOUNT-02 remains explicitly OUT_OF_SCOPE_RETAINED. No Git history mutation; no unexpected source drift. No deployment or operational runtime change. Execution stops here; no Pass3 audit or implementation begins. Locked semantics may reopen only for proven regression, security/data integrity defect, approved architecture-version change or new hostile evidence invalidating this certification, per existing lock authority.
