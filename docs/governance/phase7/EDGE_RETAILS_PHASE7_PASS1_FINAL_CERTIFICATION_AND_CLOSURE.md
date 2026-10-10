# EDGE RETAILS — PROGRAM PHASE 7 / PASS 1
# FINAL CERTIFICATION AND CLOSURE

Date: 2026-10-03. Workspace: `C:\Users\muham\OneDrive\Desktop\Point of Sale`.

## 1. Final verdict

**PASS1_CERTIFIED_CLOSED**

The five specified Pass 1 fixes pass fresh real PostgreSQL certification, persisted accounting checks, required concurrency/rollback/retry scenarios, focused/full unit regression, protected Tracking regression, both builds and EF/model/migration verification. No production correction was needed. This certification added three integration-test files and evidence/report artifacts; all existing source and tests remain unchanged from the captured task baseline.

The lead synthesized this verdict after all specialist roles and the separate final independent validator returned. The validator's actual verdict is **PASS1_CERTIFIED_CLOSED**, with no remaining Pass 1 blocker. [Independent challenge and verdict](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/phase7-pass1-final-20261003/final-independent-validator.md>).

| Role | Agent / final evidence |
|---|---|
| Lead | `/root`: baseline, serialized execution, evidence custody and final report |
| A — purchasing; D — source/build/EF | `/root/p7_purchasing`: [final specialist report](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/phase7-pass1-final-20261003/purchasing-certifier.md>) |
| B — expense/cash; F — test evidence | `/root/p7_expense`: [expense report](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/phase7-pass1-final-20261003/expense-certifier.md>), [independent assertion/count review](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/phase7-pass1-final-20261003/test-evidence-certifier.md>) |
| C — return/khata; E — protected Tracking | `/root/tracking_readonly_certifier`: [return report](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/phase7-pass1-final-20261003/return-certifier.md>), [Tracking preservation report](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/phase7-pass1-final-20261003/tracking-nonregression-certifier.md>) |
| Final independent validator | `/root/p7_final_validator`: source/evidence challenge; no test authorship, build/test/DB execution or production edit |

The four-slot limit required specialists to perform the requested roles in waves. Builds and PostgreSQL execution were serialized by the lead; specialists owned separate new test files. This closes **Pass 1 only**, not Phase 7, the entire backend, deployment or production cutover. Pass 2 was not started.

## 2. Certified branch / HEAD

| Identity | Certified value |
|---|---|
| Branch | `tracking-remediation-20261002` |
| HEAD | `97956831e01d7ed6b4e2f1051b15e8ec272e8e4b` |
| Baseline source capture | `2026-10-03T13:51:52.7849500Z` |
| Final candidate manifest | 806 files; SHA256 `D4E392940526318EE3654429AD81E123F327A3CD553DC1C4AEBF8A80E867FD45` |
| Source-state basis | HEAD **plus the uncommitted working-tree hashes**; HEAD alone does not contain the certified work |
| Commit / push | NO / NO |

[Baseline state](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/phase7-pass1-final-20261003/baseline/state.json>) and [final source/branch check](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/phase7-pass1-final-20261003/final-state/source-check.json>) record the actual identity.

## 3. Source-state identity

Before any certification execution, branch, HEAD, `git status --short`, diff names/stat and source hashes were captured under [baseline evidence](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/phase7-pass1-final-20261003/baseline/status.txt>). Initial state was **54 modified tracked files, 21 untracked files, zero staged files and zero conflicts**. All inherited modifications and untracked work were preserved.

The prior Tracking freeze's 703-file manifest differs from the initial live source in exactly seven files declared by Pass 1 Step 2. The other **696 files match**. These are expected implementation changes, not unexpected drift:

| Existing file changed since Tracking freeze | Pass 1 ownership |
|---|---|
| `src/EdgeRetails.Application/Features/Purchasing/VoidPurchaseHandler.cs` | D-VOID-1 / D-VOID-2 |
| `src/EdgeRetails.Application/Features/Finance/ExpenseHandlers.cs` | D-EXP-1 |
| `src/EdgeRetails.Application/Features/Purchasing/PurchaseReturnHandler.cs` | D-RET-1 / P7-N01; previously modified Tracking consumer |
| `tests/EdgeRetails.UnitTests/Phase1DExactUnitLifecycleTests.cs` | Updated consumer constructor calls |
| `tests/EdgeRetails.UnitTests/Phase1DExactUnitReturnBehavioralTests.cs` | Updated consumer constructor calls |
| `tests/EdgeRetails.UnitTests/Phase2PurchasingAndKhataBehavioralTests.cs` | Updated consumer constructor calls |
| `tests/EdgeRetails.UnitTests/Phase2TestDoubles.cs` | Cash service test composition |

The already-existing untracked `Phase7Pass1IntegrityTests.cs` is included in the 803-file task baseline. The Step 2 report did not publish a separate Pass 1 byte manifest; this certificate binds the inspected current implementation to the newly captured hashes rather than inventing historical byte identity.

During this task **803/803 existing source/test/project/script/resource hashes remain identical**. Only these source additions enter the final 806-file manifest:

- [Phase7Pass1PurchasingPostgresTests.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/tests/EdgeRetails.IntegrationTests/Phase7Pass1PurchasingPostgresTests.cs>)
- [Phase7Pass1ExpensePostgresTests.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/tests/EdgeRetails.IntegrationTests/Phase7Pass1ExpensePostgresTests.cs>)
- [Phase7Pass1ReturnPostgresTests.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/tests/EdgeRetails.IntegrationTests/Phase7Pass1ReturnPostgresTests.cs>)

[Initial manifest](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/phase7-pass1-final-20261003/baseline/source-manifest.csv>), [declared seven-file delta](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/phase7-pass1-final-20261003/baseline/tracking-to-pass1-declared-delta.csv>) and [final manifest](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/phase7-pass1-final-20261003/certified-source-manifest.csv>) make these claims reviewable. Source drift: **NONE**. No production correction allowance was used.

## 4. Pass 1 defect closure matrix

| Defect | Exact production authority | Fresh persisted proof | Closure |
|---|---|---|---|
| D-VOID-1 | [VoidPurchaseHandler.HandleAsync](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/VoidPurchaseHandler.cs:72>); received authority/zero path at 171; partial guard at 177 | Deferred zero-receipt void, unchanged partial receipt, both overlapping void/intake winner orders | PASS |
| D-VOID-2 | Same handler; [cash compensation](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/VoidPurchaseHandler.cs:302>), payment reversal at 321/323, liability compensation at 370 | 10000/4000 ledger/cash lifecycle, preserved rows, repeated operation, no-session and two post-flush rollbacks | PASS |
| D-EXP-1 | [VoidExpenseHandler.HandleAsync](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Finance/ExpenseHandlers.cs:204>); repeated-state guard at 233; CashIn at 242; void/audit at 259/266 | 10000→9000→10000, repeat safety, sealed A/current B, no-session and post-flush rollback | PASS |
| D-RET-1 | [CreatePurchaseReturnHandler.HandleAsync](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/PurchaseReturnHandler.cs:88>); session at 125; CashIn at 431; balancing refund at 451 | Quantity and factor-2 Container cash settlement/replay, no-session preservation, competing returns and post-flush rollback | PASS |
| P7-N01 | [received-minus-returned guard](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/PurchaseReturnHandler.cs:281>), maximum at 293 | All four prescribed intake/return boundaries plus concurrent Quantity/Container returns | PASS |

The implementation retains the original-order ceiling and additionally enforces `max(0, Round(alreadyReceived - alreadyReturned))`. Received authority is actual persisted intake history, not a seeded order quantity or current stock approximation.

## 5. PostgreSQL environment

Only the existing approved [owned-cluster runner](<C:/Users/muham/OneDrive/Desktop/Point of Sale/scripts/Invoke-MasterRemediationPostgresRehearsal.ps1>) was executed. It creates a new temporary `%TEMP%\EdgeRetailsMasterPg_<GUID>\data` cluster with current-user/SYSTEM ACLs and random withheld credentials, refuses an occupied port and binds loopback. Sanitized final target: **127.0.0.1:55640 / edge_retails_master_test / er_master_admin**.

The runner sets process-local `EDGE_RETAILS_TEST_DB` and canonical `EDGE_RETAILS_DB` to the exact owned connection before migrations; it cannot silently select the operational design-time fallback. High-water, custody and other runtime paths also point into the owned fixture. It restores process environment afterwards.

[Phase2PostgresTestHarness.BuildProvider](<C:/Users/muham/OneDrive/Desktop/Point of Sale/tests/EdgeRetails.IntegrationTests/Phase2PostgresTestHarness.cs:32>) independently attests host, port, root prefix and actual `SHOW data_directory` against that newly owned root before fixtures/lock observers operate. Production DI, repositories, Npgsql, row/advisory locks and transaction runner are used. Integration class execution is serialized; scenarios use unique products/suppliers/actors/operation IDs. Fixture setup and cleanup seal only sessions within this attested isolated test database, retaining historical rows.

Final terminal **ProviderVerified=true, ExitCode=0, CleanupPass=true**. The owned server was stopped, `pg_ctl status` returned 3, `postmaster.pid` was absent, and bounded owned-root removal succeeded. No operational/shop database, backup or export was accessed. [Actual terminal records](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/phase7-pass1-final-20261003/postgresql-final/terminal-result.json>).

## 6. PostgreSQL version

**PostgreSQL 18.6**, actual server `server_version_num=180006`; Npgsql provider. [Server/target attestation](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/phase7-pass1-final-20261003/postgresql-final/commands.log:29>) was recorded before certification tests.

## 7. PostgreSQL test results

**119 executed / 119 passed / 0 failed / 0 skipped**, terminal exit 0; test duration 1m17s. Counters also report zero errors, timeouts, aborted, inconclusive, notRunnable and notExecuted cases. Individual rows, distinct IDs and test definitions agree with counters; every required scenario was executed.

| Selected class group | Total / passed | Failed / skipped |
|---|---|---|
| Phase7Pass1PurchasingPostgresTests | 8 / 8 | 0 / 0 |
| Phase7Pass1ExpensePostgresTests | 4 / 4 | 0 / 0 |
| Phase7Pass1ReturnPostgresTests | 11 / 11 | 0 / 0 |
| TrackingCrossWorkflowPostgresTests | 8 / 8 | 0 / 0 |
| TrackingCutoverPostgresTests | 7 / 7 | 0 / 0 |
| TrackingFinalChallengePostgresTests | 13 / 13 | 0 / 0 |
| TrackingGoldenTracePostgresTests | 22 / 22 | 0 / 0 |
| TrackingManufacturerIdentityPostgresTests | 2 / 2 | 0 / 0 |
| TrackingMasterRacePostgresTests | 9 / 9 | 0 / 0 |
| TrackingMigrationRehearsalPostgresTests | 9 / 9 | 0 / 0 |
| TrackingWorkflowPostgresTests | 13 / 13 | 0 / 0 |
| MasterSupplierProductSequencePostgresTests | 12 / 12 | 0 / 0 |
| Adjacent Phase2Transactional serialized purchase/TrackingCode/lot case | 1 / 1 | 0 / 0 |
| **Final total** | **119 / 119** | **0 / 0** |

There are **23 Pass 1 cases**, **83 Tracking cases**, **12 protected sequence cases** and **one adjacent existing regression** matched by `Tracking` in its method name. The extra case is not counted as a Tracking-class case. The table below enumerates all 23 new case vectors; retries and before/after assertions occur within their corresponding cases.

| Scenario | PostgreSQL | Persisted Assertions | Result |
|---|---|---|---|
| A1 — deferred ordered10/received0 unpaid void | 18.6 | Completed→Voided; one void/correlated audit; liability lifecycle0; stock/lot/unit/movement/cost rows and deltas0 | PASS |
| A2 — ordered10/received4 partial void | 18.6 | `purchasing.void_partial_receipt_forbidden`; complete persisted aggregate equal before/after, committed receipt retained | PASS |
| A3 — deferred10000, drawer initial payment4000, same-ID void twice | 18.6 | Initial +10000/-4000/payable6000/cash-4000; final four ledger entries/net0, one payment reversal, CashIn4000/net0, original IDs retained, replay unchanged | PASS |
| A4 — required drawer refund, no active session | 18.6 | `cash.session_required`; purchase Completed/payment Posted; no reversal/compensation/CashIn; complete aggregate unchanged | PASS |
| A5 — deferred paid void failure after actual SQL flush | 18.6 | Saved status/payment/reversal/ledger/cash/audit changes fully rolled back; original persisted aggregate restored | PASS |
| A5 — fully received physical paid void failure after actual SQL flush | 18.6 | Same financial rollback plus original stock/lot/cost, unit identity/claims/status and movement/item-unit links restored | PASS |
| A6 — void wins against overlapping intake4of10 | 18.6 | Loser backend observed waiting on actual DB lock; void succeeds, intake `purchasing.purchase_voided`; no receipt/inventory, ledger lifecycle0 | PASS |
| A6 — intake4of10 wins against overlapping void | 18.6 | Actual loser lock wait; intake succeeds, void partial-receipt rejection; stock/lot4, cost4000, exactly one receipt/success outcome; payable10000 | PASS |
| B1 — opening10000, cash expense1000, void and repeats | 18.6 | Original ExpenseCashOut1000 preserved; expected9000 then10000; one ManualCashIn1000, expense Voided/version/actor/reason, audit/source IDs; repeats change nothing | PASS |
| B2 — expense in closed A, active B | 18.6 | Closed A JSON/totals and original CashOut unchanged; compensation only B; B opening2000→expected3000; original expense/audit provenance retained | PASS |
| B3 — no current open session | 18.6 | `cash.session_required`; expense Posted; expense/cash/audit/session snapshot unchanged, no compensation or VOIDED audit | PASS |
| B4 — expense failure after actual SQL flush, then retry | 18.6 | Uncommitted Voided/cash/audit rows observed in PostgreSQL before injected exception; fresh context proves rollback; retry/repeat yields exactly one CashIn/audit and expected10000 | PASS |
| C1 — Quantity return1000 and identical operation replay | 18.6 | Return/header/item/operation/source IDs; stock/lot/cost reductions; CashIn1000/drawer11000; paired supplier entries net0; one audit/success outcome; replay unchanged | PASS |
| C1 — factor-2 Container return1000 and replay | 18.6 | Same finance plus exact original unit/TrackingCode/sequence/dealer/SKU/supplier/lot/received quantity2/acquisition cost1000; only selected unit SupplierReturned; allocation unchanged | PASS |
| C2 — Quantity CashDrawer return without session | 18.6 | `cash.session_required`; no return, inventory/lot/cost/movement/ledger/cash/audit effects; complete business snapshot unchanged | PASS |
| C2 — Container CashDrawer return without session | 18.6 | Same zero business effects including unchanged exact units, claims and lot provenance | PASS |
| C3 — A: ordered10, received4, prior0, attempt5 | 18.6 | Actual intake and authoritative received4; rejects `purchasing.return_exceeds_received`; complete business snapshot unchanged | PASS |
| C3 — B: ordered10, received4, prior1, attempt4 | 18.6 | Persisted prior return1; available3; rejects received limit; complete business snapshot unchanged | PASS |
| C3 — C: ordered10, received10, prior3, attempt7 | 18.6 | Accepts remaining7; cumulative returns10; stock/lot/cost0; two returns, four return/refund ledger entries and CashIn total10000; supplier balance remains10000 | PASS |
| C3 — D: ordered10, received0, prior0, attempt1 | 18.6 | Zero actual receipt history; rejects received limit; no fabricated return/inventory/ledger/cash | PASS |
| C4 — overlapping Quantity returns3+3 against received4 | 18.6 | First saved transaction held until contender observed in DB Lock wait; one success/one received-limit rejection; one return/cash/ledger settlement; stock/lot nonnegative | PASS |
| C4 — overlapping Container returns2+2 against received2 packs | 18.6 | Same lock proof and same exact IDs; only one return, no duplicate physical links/status effect, cash/ledger once, stock/lot nonnegative | PASS |
| C5 — Container return failure after actual SQL save | 18.6 | Return/stock/lot/unit/cost/ledger/cash/audit/success outcome roll back; complete business snapshot unchanged and no success outcome | PASS |

Method reference key, including every new method and its theory vectors:

| Key | Exact test method |
|---|---|
| A1 | [ZeroReceiptDeferredVoid_PersistsVoidedAuditAndLedgerWithoutInventingInventory](<C:/Users/muham/OneDrive/Desktop/Point of Sale/tests/EdgeRetails.IntegrationTests/Phase7Pass1PurchasingPostgresTests.cs:23>) |
| A2 | [PartialReceiptVoid_FailsDeterministicallyAndPreservesEveryCommittedAggregateRow](<C:/Users/muham/OneDrive/Desktop/Point of Sale/tests/EdgeRetails.IntegrationTests/Phase7Pass1PurchasingPostgresTests.cs:62>) |
| A3 | [CashPaidDeferredVoid_Reconciles10000And4000AndSameOperationReplayPersistsOnce](<C:/Users/muham/OneDrive/Desktop/Point of Sale/tests/EdgeRetails.IntegrationTests/Phase7Pass1PurchasingPostgresTests.cs:90>) |
| A4 | [CashPaidVoidWithoutOpenSession_FailsAndPreservesFullAggregate](<C:/Users/muham/OneDrive/Desktop/Point of Sale/tests/EdgeRetails.IntegrationTests/Phase7Pass1PurchasingPostgresTests.cs:170>) |
| A5 | [FailureAfterActualSqlFlush_RollsBackFinancialAuditAndPhysicalEffects(false / true)](<C:/Users/muham/OneDrive/Desktop/Point of Sale/tests/EdgeRetails.IntegrationTests/Phase7Pass1PurchasingPostgresTests.cs:193>) |
| A6 | [VoidVsPartialIntake_OverlappingPostgresLocksAllowOneAuthority(true / false)](<C:/Users/muham/OneDrive/Desktop/Point of Sale/tests/EdgeRetails.IntegrationTests/Phase7Pass1PurchasingPostgresTests.cs:213>) |
| B1 | [CashExpense_PostVoidAndRepeatedVoid_PersistExactlyOneCompensationAndRestore10000](<C:/Users/muham/OneDrive/Desktop/Point of Sale/tests/EdgeRetails.IntegrationTests/Phase7Pass1ExpensePostgresTests.cs:15>) |
| B2 | [CashExpense_FromClosedSessionA_VoidCompensatesOnlyCurrentSessionB](<C:/Users/muham/OneDrive/Desktop/Point of Sale/tests/EdgeRetails.IntegrationTests/Phase7Pass1ExpensePostgresTests.cs:69>) |
| B3 | [CashExpense_NoCurrentOpenSession_VoidFailsWithZeroPersistedPartialEffects](<C:/Users/muham/OneDrive/Desktop/Point of Sale/tests/EdgeRetails.IntegrationTests/Phase7Pass1ExpensePostgresTests.cs:106>) |
| B4 | [CashExpense_VoidFailureAfterActualSqlFlush_RollsBackExpenseCashAndAuditThenRetryCommitsOnce](<C:/Users/muham/OneDrive/Desktop/Point of Sale/tests/EdgeRetails.IntegrationTests/Phase7Pass1ExpensePostgresTests.cs:134>) |
| C1 | [D_RET_1_CashDrawerReturnAndRetry_PersistOneCashAndBalancedKhataEffect(false / true)](<C:/Users/muham/OneDrive/Desktop/Point of Sale/tests/EdgeRetails.IntegrationTests/Phase7Pass1ReturnPostgresTests.cs:22>) |
| C2 | [D_RET_1_NoOpenSession_FailsWithoutStockLotUnitLedgerCashOrCostEffects(false / true)](<C:/Users/muham/OneDrive/Desktop/Point of Sale/tests/EdgeRetails.IntegrationTests/Phase7Pass1ReturnPostgresTests.cs:53>) |
| C3 | [P7_N01_ActualIntake_ReturnCapIsReceivedMinusReturned(all four vectors)](<C:/Users/muham/OneDrive/Desktop/Point of Sale/tests/EdgeRetails.IntegrationTests/Phase7Pass1ReturnPostgresTests.cs:73>) |
| C4 | [ConcurrentReturns_ObservedDatabaseLockWait_CannotExceedReceivedOrReturnExactUnitTwice(false / true)](<C:/Users/muham/OneDrive/Desktop/Point of Sale/tests/EdgeRetails.IntegrationTests/Phase7Pass1ReturnPostgresTests.cs:123>) |
| C5 | [Return_SaveThenThrowTestUnitOfWork_RollsBackPersistedBusinessCashLedgerAndSuccessOutcome](<C:/Users/muham/OneDrive/Desktop/Point of Sale/tests/EdgeRetails.IntegrationTests/Phase7Pass1ReturnPostgresTests.cs:169>) |

Evidence: [119 individual results](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/phase7-pass1-final-20261003/postgresql-final/individual-results.csv>), [class counts](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/phase7-pass1-final-20261003/postgresql-final/class-results.csv>), [test output](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/phase7-pass1-final-20261003/postgresql-final/test-output.txt>), [terminal command log](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/phase7-pass1-final-20261003/postgresql-final/commands.log>) and actual TRX `postgresql-final/test-results/muham_ALI_2026-10-03_19_18_04_net10.0.trx`.

Failed attempts are retained, not represented as green: initial Debug had eight analyzer errors in new tests only; async accesses/braces were corrected. The first owned PG run had **119 total / 100 passed / 19 failed / 0 skipped**. All eleven return cases failed during setup because omitted `InitialPaymentAmount` invokes `CreatePurchaseHandler`'s full-payment default; the fixture intended unpaid10000 but created payable0. Its setup failure leaked an owned open session, causing four purchasing/four expense setup failures before target operations. Explicit initial payment0 and arrangement/failure cleanup corrected only the new fixtures; original business assertions remain intact. [First failed PG terminal](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/phase7-pass1-final-20261003/postgresql/terminal-result.json>) and [initial compiler failure](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/phase7-pass1-final-20261003/debug-build-first-compiler-failure.log>) remain reviewable. The final run used another newly initialized cluster.

## 8. Persisted accounting reconciliation

| Scenario | Persisted signed supplier effects | Persisted drawer effects | Final conservation |
|---|---|---|---|
| A — purchase10000, initial payment4000, received0, void | Purchase +10000; SupplierPayment -4000; PurchaseVoidReversal -10000; SupplierPaymentReversal +4000 | SupplierPaymentCashOut -4000; PurchaseVoidCashIn +4000 | Supplier lifecycle **0**; drawer lifecycle **0**; inventory **0** |
| B — cash expense1000, void | No supplier entry | ExpenseCashOut -1000; ManualCashIn +1000 | Cash lifecycle **0**; expected10000→9000→10000 |
| C — return value1000, CashDrawer | PurchaseReturnCredit -1000; SupplierRefundReceived +1000 | PurchaseReturnCashIn +1000 | Return supplier delta **0**; drawer **+1000**; inventory carrying value **-1000** |

Purchase compensation appends entries/reversal and marks the existing payment Reversed. It preserves original ledger/payment/cash IDs: four supplier entries, one payment reversal, one purchase void, two cash movements, with the declared operation IDs, amounts/directions and reference IDs. Current implementation anchors `PurchaseVoidCashIn` to **SourceType PURCHASE_VOID / SourceId Purchase.Id**; the liability entry references PurchaseVoid.Id and the payment entry references SupplierPaymentReversal.Id. These actual persisted parent/child anchors are explicitly checked; the report does not substitute the Step 1 illustrative void-ID cash anchor for live behavior.

Expense cash rows retain SourceType EXPENSE / SourceId Expense.Id and the original CashOut. Return cash/ledger rows reference PurchaseReturn.Id; return items reference the original purchase item/UOM snapshot and exact units. The cash-settled return leaves the original payable unchanged because goods credit and immediate cash refund balance each other; it must not falsely reduce debt and also receive cash.

## 9. Transaction atomicity

[EfTransactionRunner.ExecuteAsync](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Services/PlatformServices.cs:72>) uses the production ReadCommitted transaction: unsuccessful IResult rolls back and clears EF tracking at 93/94; success commits at 98; exception rolls back/clears at 103/104. Target repositories, cash, ledger, stock, units, audit and successful outcomes share the scoped context/transaction.

No-session failures verify complete relevant before/after persisted snapshots, not handler failure alone. Test-only `IUnitOfWork` decorators then call **actual DbContext.SaveChangesAsync against PostgreSQL**, inject failure before outer commit and verify rollback from fresh contexts. There are two purchasing variants (deferred and fully received physical), one expense variant observing flushed rows before exception, and one Container return variant. These exercise real SQL writes and rollback at meaningful financial/inventory/audit boundaries; no production failure hooks or mocked production repositories were introduced.

PurchaseReturn intentionally records a Failed, WasCommitted=false diagnostic OperationOutcome **after** business rollback on application failures. No return/entity/inventory/cash/ledger business effect is retained; that diagnostic is not a partial commit. Injected thrown return failure leaves no success outcome. Expense retry after injected failure commits compensation once. These tests certify transaction failure behavior, not process-kill, power-loss or hardware durability.

## 10. Concurrency proof

Both void/intake tests identify the contender PostgreSQL backend and observe its `pg_stat_activity` Lock wait before releasing the winner's post-SQL-flush gate. With void first, the second intake sees Voided and fails `purchasing.purchase_voided`; with intake4of10 first, full void fails `purchasing.void_partial_receipt_forbidden`. Exactly one operation succeeds in each tested overlap; persisted receipt/ledger/stock/cost/outcomes agree with the winner.

The intake uses a partial quantity deliberately: fully received untouched stock is legally voidable later, so two sequential full-receipt/void successes would be a different permitted lifecycle.

Concurrent return tests run distinct production scopes/operation IDs. The first return's saved rows/locks remain inside an open outer transaction until the contender's actual database Lock wait is observed, then commit. Quantity3+3 against received4 and Container2+2 against received2 packs each allow one return only. The Container contenders select the same exact IDs. Final quantity/lot balances are nonnegative, ledger/CashIn occur once, and no unit is returned twice. Existing locking was consumed, not redesigned. These are controlled real overlaps, not timing-only sleeps or an exhaustive claim about all possible schedules.

## 11. Retry/idempotency proof

Same VoidPurchase ClientOperationId twice returns the same PurchaseVoid with WasExisting=true and identical persisted aggregate. Same PurchaseReturn ClientOperationId twice returns the same return with unchanged stock/lot/cost/cash/ledger/unit/audit state and one successful outcome. Expense repeated void uses the existing Voided-state guard: same and different correlations create no second CashIn/audit; retry after an injected rollback succeeds once.

This certifies current same-operation/state-transition behavior. It does not implement or certify Phase 12 payload-fingerprint uniformity, replay-before-current-validation, or broader recovery architecture. In particular, the return's current session precheck precedes existing-return lookup; no stronger closed-session replay promise is made.

## 12. Tracking non-regression

**PASS**: 106/106 fresh relevant unit cases, including all three compiled ArchitectureDrift checks; 83/83 actual Tracking PostgreSQL cases and 12/12 protected sequence cases. The old 703-file manifest still has only the seven declared Pass 1 differences; protected authority source remains byte-identical.

PhysicalUnitCreationAuthority, TrackingCode construction, ItemSequence allocation, IdentityNormalizationRules, InventoryUnitIdentityClaim, DealerCode snapshots, machine high-water/custody and physical quantity/lot provenance were not edited. Exact return consumes immutable [received physical quantity snapshots](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/PurchaseReturnHandler.cs:625>) and transitions original identities. Factor-2 Container cases pin TrackingCode/ItemSequence/SupplierProduct/dealer/SKU/lot/source purchase item, quantity2 and whole acquisition cost1000; selected originals become SupplierReturned, others remain InStock, with no replacement allocation.

The required prior [Tracking freeze certificate](<C:/Users/muham/OneDrive/Desktop/Point of Sale/EDGE_RETAILS_TRACKING_SYSTEM_FINAL_CERTIFICATION_AND_FREEZE_2026-10-03.md>) and [latest independent adversarial final review](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/tracking-final-20261003/independent-certifier-final.md>) were read as protected dependencies. Their operational-data/UI acceptance limits are preserved. [Fresh Role E verification](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/phase7-pass1-final-20261003/tracking-nonregression-certifier.md>).

## 13. Focused test results

| Fresh unit selection | Total / passed | Failed / skipped |
|---|---|---|
| Phase7Pass1IntegrityTests | 13 / 13 | 0 / 0 |
| Phase2PurchasingAndKhataBehavioralTests | 9 / 9 | 0 / 0 |
| Combined focused Pass 1 | 22 / 22 | 0 / 0 |
| Relevant focused Tracking and receiving/adjustment/warranty consumers | 106 / 106 | 0 / 0 |
| TrackingArchitectureDriftTests (included in 106) | 3 / 3 | 0 / 0 |

[Focused Pass 1 log](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/phase7-pass1-final-20261003/focused-pass1.log>), [focused Tracking log](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/phase7-pass1-final-20261003/focused-tracking.log>) and actual TRXs under `unit-results/` provide current-session execution evidence. These counts overlap the full unit suite and are not added to 894.

Role F independently found no unit Skip, catch-ignore or weakened assertion. Existing Step 2 units use test doubles appropriately for behavioral regression, but their fake transaction runner cannot prove PostgreSQL rollback. Their success-only final quantity/limited physical assertions are supplemented by the mandatory deep PostgreSQL cases above. All 82 initially captured unit-file hashes remain unchanged. The Step 2 report's contradictory closure prose and inaccurate method names are superseded by live definitions and this fresh certificate; its old artifact was preserved.

## 14. Full UnitTests result

**894 total / 894 passed / 0 failed / 0 skipped**, fresh Release unit execution, duration21s. Actual TRX results/definitions/distinct IDs agree; no hidden errors, aborts or timeouts. [Full unit terminal log](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/phase7-pass1-final-20261003/full-unit.log>) and [individual unit results](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/phase7-pass1-final-20261003/unit-individual-results.csv>).

This is the current task's executed 894 count, not an unexecuted copy of the implementation report. These unit runs remain bound to unchanged existing production/unit source; subsequent corrections changed only the three newly added integration fixtures.

## 15. Debug build

Fresh final `dotnet build -c Debug`: **Build succeeded; 0 warnings; 0 errors; terminal exit0; 12.54s**. [Final actual completion log](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/phase7-pass1-final-20261003/debug-build.log>) and [command records](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/phase7-pass1-final-20261003/build-commands.json>). This build occurred after all new fixture corrections; the earlier compiler failure is retained separately.

## 16. Release build

Fresh final `dotnet build -c Release`: **Build succeeded; 0 warnings; 0 errors; terminal exit0; 12.47s**. [Final actual completion log](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/phase7-pass1-final-20261003/release-build.log>). Its source matches the 806-file manifest used for the final PostgreSQL run.

## 17. EF model check

**NONE** pending. Actual terminal output: `No changes have been made to the model since the last migration.` Correct Infrastructure project/startup/context and Release configuration were used inside the isolated runner:

```powershell
dotnet ef migrations has-pending-model-changes --project src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj --startup-project src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj --context EdgeRetailsDbContext --configuration Release
```

Exit0 is in the [final owned command log](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/phase7-pass1-final-20261003/postgresql-final/commands.log:84>). Canonical process-local owned DB authority was set before EF; no operational fallback was inspected.

## 18. Migration status

**NONE required / created / newly modified.** Existing snapshot, migration and designer hashes remain unchanged from the initial baseline. Snapshot SHA256 remains `F2C7D0611D5CCB982261344BC835B442029BB02531B153630891EB262E11BBE6`.

All **21 current migrations applied from zero** in the final fresh owned cluster, ending at `20261002101709_TrackingManufacturerIdentityAuthorityV1`; pending model changes NONE. The nine Tracking migration cases also pass model/constraint/FK/index checks from zero and eight synthetic actual pre-Tracking checkpoint upgrades/rejections. No operational historical dataset was used, and no manual schema patch was used to make Pass 1 pass.

The exact applied chain is recorded in commands.log:

```text
20260920094824_InitialProductionBaseline
20260920111318_Sprint7Phase1SetupIdentity
20260920164958_Sprint7ProductionCutover
20260921101001_Sprint8CanonicalReportingSchema
20260921143542_Sprint8FinalProductionAlignment
20260921152602_Sprint8WarrantyAlignment
20260922120000_Phase1CanonicalSchemaAlignment
20260922135055_Phase3ProductionSafetyOutbox
20260923071510_Phase4MultiTerminalSchema
20260923095632_Phase5WarrantyClaimClientOperationId
20260923110943_Phase5WarrantyLifecycleIdempotency
20260923111027_Phase5MovementHistoryOrderingIndex
20260923125420_Phase5PurchaseHistoryOrderingIndex
20260925142150_Phase1SemanticProductIdentity
20260927062206_Phase2DurableOperationOutcome
20260927121724_Phase2OutboxLeaseFencing
20260928150000_Phase3PosPriceOverride
20260929100000_Phase3LegacyCategoryUpgradeRecovery
20260930053030_Phase3COwnerPinRecoveryReplaySafety
20260930065058_Phase3COwnerPinAuthorizationConsumption
20261002101709_TrackingManufacturerIdentityAuthorityV1
```

Applying an inherited migration name does not authorize any new architectural scope. No model/snapshot/designer regeneration or migration creation occurred.

## 19. Git/diff status

Branch/HEAD unchanged; staged0; conflicts0. Final expected tree is **54 inherited tracked modifications, 25 untracked files**: original21 plus three integration files and this report. Tracked diff names/stat remain unchanged from the initial capture. `git diff --check` passes; LF/CRLF notices are Git conversion notices, not compiler warnings.

No reset, clean, discard, stash, branch switch, commit, push, merge, rebase or tag occurred. Evidence artifacts reside under the existing ignored artifacts directory. Final state/diff records are in [final-state evidence](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/phase7-pass1-final-20261003/final-state/git-status.txt>); source checks prove 803 original and 806 candidate hashes unchanged after execution. This certificate describes an uncommitted working-tree candidate.

## 20. Remaining Pass 1 blockers

**NONE.** All five defect gates, real PostgreSQL, persisted conservation/atomicity/concurrency/retry/provenance, focused/full units, Tracking, Debug/Release, EF, migration and source preservation pass. First-run test-fixture/compiler defects are corrected and retained as failed evidence, not hidden or misclassified as production fixes.

Out-of-scope observations, preserved without implementation:
- Phase 7 Pass 2 defects and F03/F05/F06/F07/F10/F12/F14/P7-N03 were not fixed or newly certified here.
- Broader settlement/replay/recovery, Phase 8/12 and desktop acceptance remain their existing handovers; current initial-payment/same-ID proof is not a universal workflow certificate.
- Legacy Unicode inventory is **NOT_RUN_ENVIRONMENT / REQUIRES_APPROVED_DATA_SOURCE**; no shop data access or zero-legacy-defect claim. It does not block this isolated certification.
- No production deployment, full UI acceptance, performance/SLA or process/power-loss durability certification is asserted.

## 21. Formal closure statement

```text
PROGRAM PHASE 7 — PASS 1

STEP 1 DEEP FORENSIC AUDIT: COMPLETE
STEP 2 IMPLEMENTATION: COMPLETE
REAL POSTGRESQL CERTIFICATION: COMPLETE
TRACKING NON-REGRESSION: PASS
MIGRATION: NONE
PASS 1 STATUS: CERTIFIED / CLOSED

FINAL VERDICT: PASS1_CERTIFIED_CLOSED
```

This closes **Pass 1 only**. Phase 7 remains open for its separately authorized work. Pass 2 was not started. No additional implementation is proposed by this certification.
