# EDGE RETAILS — PHASE 7 PASS 2 FINAL HOSTILE CERTIFICATION AND LOCK

Date: 2026-10-04. Workspace: `C:\Users\muham\OneDrive\Desktop\Point of Sale`.

## 1. Executive verdict

**PASS2_CERTIFICATION_FAILED — NOT LOCKED.** All revised execution gates and the second fresh independent read-only review have terminal results. Reviewer `/root/p7_pass2_fresh_final_validator_v2` independently returned **FAIL for lock**, reconciled source identity and actual results, and identified no remaining in-scope production defect. Required PostgreSQL verification is not green: **185 executed, 181 passed, 4 failed, 0 skipped** in one revised integrated run, including the established adjacent regression. The inherited static Thaka input edge in section39 is separately recorded without claiming an executed reproduction.

The bounded stock-adjustment correction addressed real PostgreSQL-proven F01/D-ADJ-1 defects. The first fresh validator also identified fractional physical target normalization; six new real-PG RED cases proved it, the same handler received a raw whole-count guard, and every affected final gate was freshly rerun. All four remaining failures expose an inherited Thaka detail-read dependency. It is preserved, documented, and not repaired outside the authorized scope. No Pass 3 work starts. Current evidence root: `artifacts/phase7-pass2-hostile-20261004/final-v2/`; prior attempts and first independent failure remain intact.

## 2. Authority chain

Read in the prescribed order: Phase7 Final Defect Ownership and Boundary Freeze; Pass2 Step1 Deep Forensic Audit; Pass1 Final Certification and Closure; prior Pass2 Final Certification and Closure; current production/test diffs; Tracking Final Certification and Freeze; Tracking independent final review; live implementations/tests. Current task authority is the attachment `0f2a4105-ca66-400e-9972-654d9773b8ed\Pasted text.txt`. No applicable ancestor or nested AGENTS.md was found.

Frozen findings: F01, D-ADJ-1, F02, F03, F04, P7-N02, P7-N03 only. Pass1 and Tracking remain protected. No migration, frontend, licensing, AdminPortal, Phase8/12 or later-pass implementation is authorized.

## 3. Current branch / HEAD

Branch: `tracking-remediation-20261002`. HEAD: `97956831e01d7ed6b4e2f1051b15e8ec272e8e4b`. Both preserved. No commit, push, reset, clean, branch switch, merge, rebase or tag.

## 4. Final working-tree candidate identity

The candidate includes the actual uncommitted working tree, not HEAD alone. `artifacts/phase7-pass2-hostile-20261004/execution-candidate-v2-manifest.csv` captures **810 files** before revised final verification. `final-v2/post-regression-source-check.csv` verifies **810 matches / 0 drift**. Existing background dotnet processes were identified as idle build servers, not running build/test/EF commands. All command sessions have terminal results before the second fresh final validator. The first independently rejected candidate and its810-file manifest are preserved in `candidate1-freeze/` and `independent-candidate1-failure.md`.

## 5. SHA-256 source manifest

`EDGE_RETAILS_PHASE7_PASS2_FINAL_SOURCE_MANIFEST.sha256`, SHA256 **BAC968434BE8CFEDAD56D93CD33EF6D5EF046424CF78FF740D1BBE996067543C**, binds branch, HEAD, relative path, current SHA256, Pass2Intentional YES/NO, and classification. It covers Domain/Application/Infrastructure, both test projects, Desktop and the prior certified project's other source/project/script/resource files. Generated bin/obj files are excluded.

The actual before-Pass2 authority is the Pass1 **806-file certified manifest**, independently supported for the production targets by `scratch_manifest_pre.json`. Exactly **800 existing hashes match**, six declared files changed, and four Pass2 test files were added. This does not misclassify inherited HEAD differences as Pass2 edits.

After the independent final response, root performed only documentation/evidence completion and a read-only candidate check: **810/810 match, zero drift, manifest digest/branch/HEAD unchanged; git diff --check exit0**. Evidence: `final-v2/post-independent-source-check.csv`, `post-independent-final-state.json` and `post-independent-command-record.json`. This identity check does not replace independent certification or turn failed tests into passes.

## 6. Diff classification

Complete revised file tables: `final-v2/source-classification.csv` (810 rows) and `final-v2/every-changed-file-classification.csv` (Git modified/untracked entries). A=inherited before Pass2; B=intentional Pass2 production; C=intentional Pass2 tests; D=generated evidence; E=unexpected drift. Source classifications: **A800 / B4 / C6 / E0**. Paths with inherited Tracking changes can also contain the later B/C delta; the historical baseline hashes establish the boundary.

| Production file | Classification | Actual Pass2 scope |
|---|---|---|
| Domain/Inventory/InventoryModels.cs | B | Scrapped/IssuedThaka cost participation false |
| Application/Features/Inventory/StockAdjustmentHandlers.cs | B | Target-current, Container authority, physical removal; this certification's bounded correction |
| Application/Features/Inventory/InventoryConditionHandlers.cs | B | Raw whole quantity and exact per-unit scrap loss |
| Application/Features/Thaka/ThakaHandlers.cs | B | BaseQuantity charge; command-wide duplicate IDs |

| Test file | Classification | Change |
|---|---|---|
| Phase1CanonicalSchemaDomainAlignmentTests.cs | C | ASSERTION_CHANGE: exact IssuedThaka boolean follows approved P7-N03 |
| TrackingAdjustmentReplayTests.cs | C | HARNESS_CORRECTION: valid Container ProductUnit; assertions unchanged |
| Phase7Pass2IntegrityTests.cs | C | NEW_COVERAGE inherited Pass2 file; entered target input corrected50→1, assertions unchanged |
| Phase7Pass2PostgresTests.cs | C | NEW_COVERAGE inherited seven PostgreSQL tests |
| Phase7Pass2HostileNumericPostgresTests.cs | C | NEW_COVERAGE: 48 persisted hostile vectors, including six raw target admission cases |
| Phase7Pass2HostileConcurrencyPostgresTests.cs | C | NEW_COVERAGE: seven real overlaps and four real SQL rollback vectors |

| Evidence/artifact | Classification | Treatment |
|---|---|---|
| Prior Pass2 closure | D | Preserved and explicitly annotated preliminary; raw old results retained |
| Final hostile report/source manifest | D | Current evidence authority; failed lock result |
| Owned artifacts/phase7-pass2-hostile-20261004 | D | Command logs, TRXs, ledgers, hashes, classifications, failed attempts, snapshots |
| independent-candidate-v2-final.md and post-independent-* evidence | D | Actual returned independent failure and final read-only identity check; added after review, outside source manifest |

Unexpected production/test source table: **EMPTY**. Inherited Desktop, Tracking, Pass1 and migration changes are A, not newly introduced changes.

## 7. Test-diff integrity review

Independent specialist `/root/p7_pass2_test_audit` inspected all existing test diffs and baseline hashes. No assertion weakening, deletion of hostile cases, caught/ignored failures, skips or concurrency weakening identified. IssuedThaka True→False is an **ASSERTION_CHANGE**, explicitly justified by the approved economic contract; it retains an exact boolean assertion. Tracking replay fixture's reconstructed old contents matched baseline SHA256 `BF54B6872B4269CD138F600AABB209BA658637ADAC1EA2B5042378CE9ECDCBB0` in memory.

All new tests are **NEW_COVERAGE**. Compile/style fixes, valid distinct UOMs for cross-line duplicate admission, task draining, and zero-factor constraint admission are **HARNESS_CORRECTION**. Zero-factor PostgreSQL CHECK rejection is explicitly asserted; invalid metadata is not installed to make a fixture proceed. No SKIP_CHANGE. Container unit input50→1 follows the frozen entered-count grammar and retains final50 and both removed statuses.

Numeric project summary reads use the canonical paginated service before/after reversal (50000/0), with retained failing detail-service assertion after persisted numeric proofs. This ordering/API correction does not hide the detail failure; it remains a terminal failed test. All 18 physical fractional/selection rejections compare broad fresh persisted snapshots, including claims, sequences, issue links, reversals, audit and success outcomes. Legitimate failed diagnostic outcomes are separately asserted, never mistaken for business commits.

## 8. F01 certification

Core persisted proof passes: stock10→target6→stock6/value600/loss400; independent10→0→stock0/value0/loss1000;10→10 keeps stock/cost/lots and creates no movement. The RED run reproduced a zero-detail CHECK violation for the equal target, missing loss, and missing target-factor conversion. Surgical correction uses `targetBase = RoundQuantity(entered * factor)`, skips zero detail rows under the existing schema, and records actual allocator-returned loss/snapshot only for negative SetPhysicalCount.

The first independent review rejected fractional physical target admission before normalization. New three-mode decrease/no-op cases target1.000000001/2.000000001 produced six real PostgreSQL RED failures before the guard. The current guard checks raw `IsWhole(item.BaseQuantity)` before conversion, rounding and no-op. **6/6 now pass** with exact error and broad persisted-state equality. Both failed review and RED evidence are preserved; the revised candidate underwent all mandatory regressions again.

Physical positive count fails closed. Serialized/IndividualPiece/Container target0 requires exact IDs, scraps originals and leaves zero inventory value. Quantity/Length selected factor2 converts target3 to base6. Delta remains requested base delta and its historical loss behavior is unchanged. The second fresh independent reviewer confirms the raw-target defect resolved. Overall status is BLOCKED_DEPENDENCY because required final gates fail; no partial lock is declared.

## 9. D-ADJ-1 certification

Mandatory Container ProductUnit authority passes missing, other-product, inactive, zero-factor database refusal and changed-factor mismatch cases. Each business rejection preserves stock/lots/status/value/history/project/audit/claims/sequence. Immutable received quantity controls negative removal, with no factor1 fallback.

## 10. F02 certification

Serialized, IndividualPiece and Container exact mixed costs10000+12000 remove22000 and recognize loss22000, with zero remaining costed quantity/value. Repeat fails without another loss. One Container50 with acquisition10000 separately proves per-base200, persisted loss10000 and value0. Exact-lot authority is preserved.

## 11. F03 certification

**18/18 new real-PG cases pass**: all three physical modes × raw1.5,2.25,1.0000001, too few IDs, too many IDs and duplicate IDs. Every rejection compares fresh persisted business rows. `QuantityMath.IsWhole` is exact equality with truncation; no tolerance is invented. Raw validation precedes six-place rounding. Container selected immutable sum must equal requested base quantity.

## 12. F04 certification

Persisted core proves entered2×factor50=base100, issue charge50000 at500/base, exact carrying cost22000, item/header amounts and canonical summary value50000. After ProductUnit factor changes to25, reversal restores original100/22000 from historical snapshots, project charge/cost net0 and summary value0. The retained canonical detail-read challenge fails on inherited DTO materialization. This test is **FAIL**, not an assumed arithmetic PASS.

## 13. P7-N02 certification

Actual existing-unit duplicate rejection passes same-line, same-product cross-line with distinct valid UOMs, and inconsistent-product cases. No first-line business effect survives; only explicit Failed/WasCommitted=false outcome is allowed. Valid all-unique selection succeeds with exactly two links and zero remaining stock/value. Production command-wide flatten/distinct check remains before preparation/mutation.

## 14. P7-N03 certification

Exact policy flags agree with derecognition: Scrapped is terminal with no cost-pool contribution, though physical Scrap quantity remains represented; IssuedThaka is outside inventory cost state and retained in project material issue cost. Exact issue/reversal pool and project values conserve22000. Required canonical detail-read verification still fails, preventing complete certification.

## 15. Container 3x50 numeric proof

Fresh PG: count3/base150/value31000 → entered target2 → count2/base100/value21000; physical delta−1/base delta−50; selected cost10000 recorded as loss. Selected original becomes Scrapped; remaining originals InStock. All original IDs, TrackingCodes, ItemSequences and acquisition costs persist unchanged; SupplierProduct sequence unchanged; no split/replacement unit.

## 16. Exact Scrap 22,000 proof

All three physical modes: two different actual purchase receipts produce acquisition10000 and12000. Damaged→Scrap retains physical bucket quantity, removes22000 from cost pool, records22000 loss, leaves costed quantity/value0. Repeat is unchanged. Separate factor50/acquisition10000 case proves total10000, not an averaged or multiplied whole-unit cost.

## 17. Thaka 2x50 numeric proof

Persisted issue item: EnteredQuantity2, FactorToBaseSnapshot50, BaseQuantity100, UnitCharge500, LineCharge50000, TotalCostSnapshot22000. Header agrees. Canonical paginated project read reports50000. Inventory sellable/lot/costed qty and value become0; both original units IssuedThaka.

## 18. Thaka issue/reversal symmetry

Despite subsequent mutable factor25, reversal row has ReversedCharge50000/RestoredCost22000. Fresh issue header minus reversal is0/0; inventory/lot quantity100, value22000; original statuses return InStock; TrackingCode/ID/ItemSequence and SupplierProduct sequence unchanged. Core assertions execute before the retained failing detail read, explicitly visible in final TRX output. Existing protected physical reversal cases also pass.

## 19. Transaction atomicity

Canonical production DI, Npgsql repositories, EF transaction runner, SQL row/advisory locks and outcome ledger are exercised against attested owned PostgreSQL. Failures are not validated with fake transactions or SQLite. Snapshot assertions cover stock, cost, lots/buckets/consumptions, physical units/claims, sequence, movement/effects/links, adjustment header/details, project/issues/items/links/reversals, audit and outcomes.

## 20. Failure rollback

**4/4 pass**: SetPhysicalCount, condition Scrap, Thaka Issue and Thaka Reversal. Test-only scoped IUnitOfWork decorator executes actual `DbContext.SaveChangesAsync`, reads the newly flushed operation movement inside the actual transaction, then throws. Fresh snapshots equal before state; no success outcome/audit survives. No production failure hook, process kill or hardware durability claim.

## 21. PostgreSQL concurrency matrix

| Race | Observed terminal operations | Persisted result | Entire case |
|---|---|---|---|
| A two decreases8 against10 | one succeeds, loser insufficient stock | stock2/value200, one consumption | PASS |
| B SetCount0 vs sale | count succeeds, sale fails | stock/value0, loss100, no sale | PASS |
| C damage vs sale | damage succeeds, sale fails | one Damaged original, value100 | PASS |
| D scrap vs warranty custody | scrap succeeds, warranty fails | Scrap1/value0/loss100, no warranty custody | PASS |
| E two projects same exact unit | one issue succeeds, other insufficient stock | original IssuedThaka, stock/value0; net issue cost100/charge150 | FAIL: detail-read dependency |
| F Thaka vs purchase return | issue succeeds, return rejected | original IssuedThaka; stock/value0; net cost100/charge150 | FAIL: detail-read dependency |
| G reversal vs new issue | both succeed in legal serial lifecycle | one restoration then original reissue, net cost100/charge150 | FAIL: detail-read dependency |

Seven actual contender backend lock waits are observed with `pg_blocking_pids` bound to the precise winner backend, held after SQL flush and before commit. Winner/contender commands both terminate before assertions; cleanup drains started tasks even on observation failure. No test claims every schedule is covered. In revised E/F/G, the retained detail-read helper runs LAST: second-project emptiness, return/supplier/audit leakage, issue-link counts and outcomes execute before the failing detail read. Core assertions pass but cases remain terminal failures; never counted as complete PASS. Concurrency **4/7**, rollback **4/4**.

## 22. PostgreSQL targeted test matrix

Provider explicitly **PostgreSQL 18 / Npgsql**, actual18.6/server_version_num180006.

| Fresh group | Total | Passed | Failed | Skipped |
|---|---:|---:|---:|---:|
| Existing Pass2 PG | 7 | 7 | 0 | 0 |
| New hostile numeric | 48 | 47 | 1 | 0 |
| New concurrency+rollback | 11 | 8 | 3 | 0 |
| Pass1 PG | 23 | 23 | 0 | 0 |
| Tracking PG | 83 | 83 | 0 | 0 |
| Protected sequence PG | 12 | 12 | 0 | 0 |
| Adjacent purchase/Tracking/lot regression (inside integrated run) | 1 | 1 | 0 | 0 |
| Fresh distinct integrated final scenarios | **185** | **181** | **4** | **0** |

Functional55 cases:54 PASS/1 FAIL. Concurrency7:4 PASS/3 FAIL. Rollback4:4 PASS. Individual result CSV and raw TRX are retained, including stdout and errors. Neither subassertions nor repeated historical runs inflate total passed counts. All185 actual results, executed counters and distinct execution IDs agree; errors/aborts/timeouts/notExecuted are zero.

The explicit **Requirement → exact test name → real PostgreSQL → persisted assertions → terminal result** matrix is `final-v2/required-gate-test-matrix.csv`,185 rows, including each theory vector. Its assertions column describes intended/executed checks; terminal Failed is retained whenever the later dependency fails. This matrix, actual stdout/errors and source must be read together. Build/unit/supporting authority requirements map separately to sections23–37 and `all-final-command-records.json`; they never substitute for required PostgreSQL cases.

## 23. Pass 1 non-regression

Fresh focused units22/22 and real PostgreSQL23/23, zero failed/skipped. VoidPurchase, Expense and PurchaseReturn handlers exactly match the Pass1 certified hashes. All existing Pass1 PG/unit tests preserved. No Pass1 production behavior reopened.

## 24. Tracking unit non-regression

Fresh expanded focused selection **156/156**, zero failed/skipped, including ArchitectureDrift, MasterIdentityGovernance, AdjustmentReplay, receiving, warranty and exact-unit lifecycle consumers. This preserves and expands the earlier106-case focused scope; counts overlap full units and are not additive.

## 25. Tracking PostgreSQL non-regression

Fresh **83/83** Tracking-class cases; zero failed/skipped. Eight established classes run without reduction. The adjacent Phase2Transactional purchase/TrackingCode/lot case included by older broad Tracking filters also passes1/1 inside the revised integrated run, preserving prior confidence. Migration checkpoint, golden traces, claims/master races and replay classes remain protected.

## 26. Protected sequence/custody

Fresh focused units **56/56**, protected PG **12/12**. Identity-preserving numerical cases pin sequence rows and original tracking identifiers. No machine high-water, custody authority, sequence allocation or dealer/SKU snapshot source modification in Pass2.

## 27. PhysicalUnitCreationAuthority integrity

SHA256 **AD1CE80D3EDE1537C0C9282778513FCA0D05763D5E9A6DC9EC7C07B343E164B4** matches both Tracking703-file and Pass1806-file certified baseline. Its dirty status against HEAD is inherited. TrackingCode, identity normalization/claims, ItemSequence, custody/high-water and immutable quantity/provenance source match the before-Pass2 baseline. ThakaReversalHandlers hash **703308688A1AB8EFEE499392210CE932770A4F36D56B6BF4FC638ABD31C1CF6C** also matches.

## 28. Full UnitTests

Fresh Release **910 total /910 passed /0 failed /0 skipped**, terminal exit0. `full-unit.trx`, individual CSV and log provide real counters/results; no cached or assumed PASS. All intended unit fixture changes are described in section7.

## 29. IntegrationTests build

Fresh revised Release project build: terminal exit0; **0 warnings /0 errors**,3.86s. Actual log `final-v2/integration-build.log`.

## 30. Debug build

Fresh revised solution Debug: terminal exit0; **0 warnings /0 errors**,19.95s. Actual log `final-v2/debug-build.log`.

## 31. Release build

Fresh revised solution Release: terminal exit0; **0 warnings /0 errors**,11.89s. Actual log `final-v2/release-build.log`.

## 32. EF model drift

Canonical Infrastructure project/startup/context Release `dotnet ef migrations has-pending-model-changes`: exit0, `No changes have been made to the model since the last migration.` Executed inside process-local isolated owned DB authority, not the operational fallback. Model drift NONE. Revised integrated final cluster retains actual command records.

## 33. Migrations from zero

Fresh canonical EF applies all21 current migrations from zero in the final integrated owned cluster. `TrackingMigrationRehearsalPostgresTests.FromZero_AllMigrationsAndTrackingDatabaseConstraintsMatchModel` passes actual applied chain equality, zero pending migrations, and model-derived constraints/indexes/FKs. Eight additional pre-Tracking checkpoint upgrade/rejection cases pass. No EnsureCreated, manual schema patch, new migration or snapshot regeneration.

## 34. Migration count

Current count **21**; exact chain in `migration-chain.txt` and PG command log, ends `20261002101709_TrackingManufacturerIdentityAuthorityV1`. Existing migration/snapshot/designer bytes match Pass1 baseline. **New Pass2 migration: NONE.**

## 35. Database safety/cleanup

Every run uses inspected existing `Invoke-MasterRemediationPostgresRehearsal.ps1`: fresh `%TEMP%\EdgeRetailsMasterPg_<GUID>` owned cluster, loopback55640, actual data-directory attestation, restricted ACL, withheld random credentials and owned high-water/custody. Provider18/Npgsql attested. Operational `edge_retails_prod` is untouched; no shop backup/export/runtime mutation.

All attempts, including RED/compiler failures and raw-target RED, have terminal results and **CleanupPass=true**. Revised final integrated runner exit1 reflects failed tests, not environment failure; shutdown exit0, subsequent `pg_ctl status` expected3, PID absent and guarded root deletion verified. No database task remains running.

| Final command family | Exit | Passed | Failed | Skipped | Provider | Completion |
|---|---:|---:|---:|---:|---|---|
| Revised integrated PG tests (includes adjacent) | 1 | 181 | 4 | 0 | PostgreSQL18/Npgsql | FAIL |
| Focused Pass1 units | 0 | 22 | 0 | 0 | N/A unit | PASS |
| Focused Tracking/exact units | 0 | 156 | 0 | 0 | N/A unit | PASS |
| Focused sequence/custody units | 0 | 56 | 0 | 0 | N/A unit | PASS |
| Full units | 0 | 910 | 0 | 0 | N/A unit | PASS |
| Integration/Debug/Release builds, each | 0 | 1 | 0 | 0 | N/A build | PASS |
| Canonical EF update/model, revised final run | 0 | 1 | 0 | 0 | PostgreSQL18/Npgsql | PASS |
| Git diff --check | 0 | 1 | 0 | 0 | N/A | PASS |

Exact full revised commands, exit/count/provider/completion fields are in `final-v2/all-final-command-records.json`, `final-v2/final-command-records.json`, and `final-v2/postgresql/terminal-result.json`/`commands.log`. Failed early fixture compilation, initial production RED and independent-review raw-target RED are retained separately, never represented as final passing gates.

## 36. Frontend zero-touch

No Pass2 Desktop/frontend change. Existing inherited Desktop source exactly matches Pass1 baseline. No GUI, runtime services, installer, registry or firewall operation.

## 37. Pass 1 handler zero-touch

VoidPurchaseHandler, ExpenseHandlers, PurchaseReturnHandler protected bytes unchanged. Inherited HEAD differences predate Pass2. No protected handler was edited to make tests green.

## 38. Final adversarial validator

SECOND FRESH READ-ONLY REVIEW COMPLETE: **FAIL FOR LOCK / PASS2_CERTIFICATION_FAILED / NOT LOCKED**. Reviewer `/root/p7_pass2_fresh_final_validator_v2` independently recomputed810/810 hashes, verified A800/B4/C6, reconciled185 distinct raw TRX results with the exact requirement matrix, and confirmed the six raw-target cases green after preserved six-case RED evidence. No remaining in-scope production defect was identified. Four inherited Thaka detail-read failures remain mandatory failures. The actual returned review is preserved verbatim in `artifacts/phase7-pass2-hostile-20261004/independent-candidate-v2-final.md`; root saved the response, and the reviewer did not write files or execute builds/tests/database commands.

The first independent context returned FAIL, found raw physical target admission and independently verified810 hashes/current executed evidence. Its actual failed review is preserved in `independent-candidate1-failure.md`; source/report snapshot in `candidate1-freeze/`. It was not reused to certify the correction. Six real-PG RED admissions were followed by the raw guard, six green cases and a complete revised mandatory regression/build/EF/migration execution. Revised terminal census reports0 active certification tasks before the separate second fresh context.

The following §56 challenge matrix incorporates the second review's actual adjudication and limits; it is not a passing independent verdict:

| # | Answer | Revised evidence / limit |
|---:|:---:|---|
| 1 target-current | YES | Entered target converted then current subtracted; raw physical guard first; current numeric and six raw admission cases pass. |
| 2 Delta unchanged | YES | Delta retains base delta/direction; raw guard only SetPhysicalCount; exact numeric Delta regression. |
| 3 zero valid | YES | All physical modes with exact IDs and Quantity zero cases pass. |
| 4 anonymous positive physical identity | NO | SetCount positive fails closed; frozen permitted Delta authority remains unchanged. |
| 5 Container factor1 fallback | NO | Explicit ProductUnit required/validated; rejection snapshots and immutable factor cases. |
| 6 immutable Container removal | YES | Historical quantity checks and exact lot removal; original code/sequence retained. |
| 7 exact Scrap value | YES | Three-mode mixed22000 and separate factor50/acquisition10000 persisted loss/pool. |
| 8 fractional physical rounding admission | YES, inherited Thaka edge | Source-derived Container admission gap OOS-THAKA-RAWCOUNT-02, not PostgreSQL-reproduced. NO in revised F01/F03 paths: condition raw IsWhole18 cases and target raw IsWhole before rounding/no-op, six new cases. |
| 9 selected physical count mismatch admitted | NO | Exact count/duplicates and immutable Container sum; broad rejection snapshots. |
| 10 BaseQuantity Thaka economics | YES | Header/item100/50000/22000 and canonical summary50000→0 execute. |
| 11 duplicate ID across lines admitted | NO | Command-wide guard; real same-line/cross-line/incorrect product rejections. |
| 12 double Scrapped value | NO | Pool0/loss22000 and repeated-scrap unchanged snapshot. |
| 13 double IssuedThaka inventory value | NO | False policy flag, pool0 and exact project issue value; final read dependency remains. |
| 14 exact reversal restoration | YES | Original100/22000 and netproject0, sequence unchanged despite factor edit; retained final detail read fails. |
| 15 concurrent double consumption | NO | In observed actual blocked schedules, core stock/identity/ledger/link/outcome checks execute; E/F/G terminal FAIL still prevents complete concurrency certification. |
| 16 rollback partial state | NO | Four actual SQL-flush faults with fresh broad equal snapshots and no success outcome/audit. |
| 17 Pass1 regression | NO | Current22 units/23PG and protected handler hashes match. |
| 18 Tracking regression | NO | Current156 units/83PG+adjacent1 and authority baseline. |
| 19 sequence/custody regression | NO | Current56 units/12PG and original identity/sequence assertions. |
| 20 Tracking authority changed | NO | Baseline protected bytes preserved; inherited HEAD diff separately classified. |
| 21 migration appeared | NO | None new; modelnone, actual chain21, protected migration/snapshot hashes. |
| 22 frontend changed | NO | BeforePass2 Desktop bytes preserved. |
| 23 existing tests weakened | NO | Independent quality reviews; exact boolean canonical correction; no deleted assertions or skips. |
| 24 actual completed commands | YES | Revised raw TRX/logs/terminal records; no active task at census. |
| 25 precise final candidate hash-bound | YES |810/810 postregression matches; revised manifestBAC968434BE8CFEDAD56D93CD33EF6D5EF046424CF78FF740D1BBE996067543C. |

These risk answers concern the reviewed source and observed cases; they are not an exhaustive scheduling/process-crash certificate.

## 39. Remaining findings

**OUT_OF_SCOPE_NEW_FINDING OOS-THAKA-READ-01**, Medium, owner Thaka read-contract maintenance outside Pass2: `ThakaReadService.cs` materials projection (`issued_at`) returns provider DateTime while `ThakaMaterialLedgerRowDto` constructor expects DateTimeOffset. Actual PG throws deterministic Dapper materialization InvalidOperationException once material exists. Four retained final tests fail on this dependency. Both source files are unchanged from Pass1: read serviceSHA `AE2B7C055CB96F3CA9755EBB7E46A937BB096EAD8D0797419F1BA6EF3B7C1364`, queriesSHA `5CBA523FC0A55428AD715410C7A2162ED0ED131FFA141E8F0602B84CC1F00888`.

Exact terminal failed cases (full class-qualified names and exceptions retained in the185-row matrix/TRX):

- `F04_P7N03_Thaka_ContainerTwoTimes50_ExactProjectValueAndReversal_DespiteMutableUom`
- `E_TwoThakaProjectsSameUnit_ObservedLockWait_OnlyOneIssueAndCost`
- `F_ThakaIssueVsPurchaseReturn_ObservedLockWait_OnlyOneDerecognition`
- `G_ReversalVsNewIssue_ObservedLockWait_RestoredOriginalCanBeIssuedOnce`

This does not erase the executed persisted arithmetic/conservation evidence. It prevents claiming complete green certification. No out-of-scope repair, exception suppression, test deletion or DTO/test type substitution is performed. No environment blocker. The second independent review confirms this dependency and NOT LOCKED verdict.

**OUT_OF_SCOPE_NEW_FINDING OOS-THAKA-RAWCOUNT-02**, Low, owner Thaka input validation outside the seven Pass2 findings. Independent static review observes `TransactionQuantitySnapshot.Create` rounding entered quantity to six places (`CatalogModels.cs:201`) while `ProductUnit.ToBaseQuantity` validates whole converted base quantity (`CatalogModels.cs:163`). A raw Container count1.0000001 with current factor10000000 converts to whole base10000001 and rounded entered count1. One selected original Container with immutable historical quantity10000001 can pass both the one-ID count and historical quantity checks in `ThakaHandlers.cs:389–416`. Values fit declared precision. This is an inherited source-derived admission gap, **NOT an executed PostgreSQL reproduction**. No value loss, identity replacement or partial physical consumption is established. No repair is authorized or made; this does not invalidate revised F01/F03 raw guards. It is recorded as a separate future-owner finding, not a new in-scope surgical blocker.

The first review's F01 raw-target blocker is resolved in source and six real PostgreSQL cases, independently confirmed by the second fresh review. That review identifies no remaining in-scope production defect. The explicit §60 statuses below match the review and withhold partial lock because shared required final PostgreSQL/independent gates have not passed:

| Finding | Current allowed status | Dependency |
|---|---|---|
| F01 | BLOCKED_DEPENDENCY | Shared required final certification gates; raw-target defect corrected and regression passed |
| D-ADJ-1 | BLOCKED_DEPENDENCY | Shared required final certification gates; focused numeric/authority proofs passed |
| F02 | BLOCKED_DEPENDENCY | Shared required final certification gates; focused exact-loss proofs passed |
| F03 | BLOCKED_DEPENDENCY | Shared required final certification gates;18 rejection vectors passed |
| F04 | BLOCKED_DEPENDENCY | Concrete unchanged Thaka detail-read failure plus shared final gates |
| P7-N02 | BLOCKED_DEPENDENCY | Shared required final certification gates; four duplicate/valid vectors passed |
| P7-N03 | BLOCKED_DEPENDENCY | Concrete unchanged Thaka detail-read failure plus shared final gates |

## 40. Formal lock statement

**PASS2_CERTIFICATION_FAILED. PASS2 NOT LOCKED.** Full revised units and protected regressions pass; full required revised PostgreSQL certification fails4 cases. Second fresh independent read-only review is complete and returns FAIL for lock. No partial certification or lock is inferred from focused passes. Git history mutation NONE; working-tree approved Pass2 implementation changes YES. No Pass3 audit or implementation begins. Execution stops at this failed certification boundary; the inherited read-contract repair requires separate authority.
