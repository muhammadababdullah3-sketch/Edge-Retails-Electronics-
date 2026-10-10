# Edge Retails — Phase 1 financial identity remediation report

Date: 8 October 2026, Asia/Karachi. Authority: Master Forensic Remediation Prompt v2. **Terminal verdict: `PHASE1_BLOCKED_EXTERNAL_OWNER`.** Narrow frontend corrections passed the executed gates; full F03/F04/F07 certification is not established.

`DEPLOYMENT_HELD_BY_BACKEND_GOVERNANCE`. No production database connection/write, migration, startup, installed binary/configuration/shortcut replacement, service operation, commit or push occurred in this execution. Phase 2/3/4 were not implemented. Source changes below are not installed production corrections.

## A. Worktree preflight

Branch `tracking-remediation-20261002`, HEAD `97956831e01d7ed6b4e2f1051b15e8ec272e8e4b`, unchanged. Existing dirty work was retained. Evidence directory: `scratch/frontend-phase1-20261008/`.

`preflight-git-status.txt`, `preflight-tracked-diff.patch` and `preflight-source-hashes.json` retain the incoming worktree and 948 source/test/script/document hashes. `changed-from-preflight.json` records exactly nine changed existing paths, all authorized frontend files. Three new test files and the scratch-owned test runner are in the 13-file candidate manifest. No pre-existing protected backend, tracking, migration, business adapter, warranty or test-harness source hash changed.

Material audit drift: current source includes newer exact-warranty and receipt-void migrations, and Pass 5's checkpoint describes unfinished D13/prospective source work. The October 8 audit's installed/schema evidence is historical and was not re-inspected here. No protected migration was applied, including in the isolated fixture.

Ownership and F01–F17 matrix are in `docs/EDGE_RETAILS_17_FINDING_REMEDIATION_ROADMAP.md`. Root owned customer/expense/stock files; Agent A owned supplier files, then handed them back terminally. Agent B owned the new isolated finance test file. Agent C reviewed concurrency and fixture safety read-only. Separate Agent D performed final independent review. No overlapping source-file edits occurred.

## B. F03 — partial frontend correction; externally blocked certification

Changed `src/EdgeRetails.Desktop/ViewModels/SuppliersViewModel.cs`, `Views/SupplierDetailView.xaml` and `Views/Dialogs/SupplierEditDialog.xaml`.

SupplierDetailViewModel now separates editable draft fields from `FinancialSubmission`: original operation ID, amount, settlement method, external reference and note are captured and retained. Setters no longer reset unresolved IDs. `PostPaymentAsync` and `ReceiveRefundAsync` retry the original snapshot. One atomic gate covers payment, advance, refund and both reversal actions. An unresolved action blocks a different action. Reversals preserve their originally submitted reason. Explicit local Close/Edit commands reject closing a pending/unresolved financial workspace.

Five new supplier frontend cases prove same-dialog original-payload/ID retry after draft edit, shared gate/input freeze, conservative conflict retention and supplier-editor submission snapshots. These use controlled services; they are not ledger certification.

Real PostgreSQL characterization separately proves one persisted payment/refund/account entry/audit for the same-ID replay in the narrow scenarios below. It deliberately discards the first committed handler result and replays from a fresh scope; it does not inject a transport failure through Desktop.

**Not delivered:** durable supplier payload, process-restart discovery/recovery, navigation-wide close interception or complete authoritative reconciliation. The visible warning states restart recovery is unavailable. Local Close protection cannot make a memory-only intent durable. Exceptions conservatively retain unknown state; confirmed-rejection resolution is not complete.

## C. F04 — workflow coverage and limits

| Workflow | Existing identity/replay | This execution | Remaining status |
|---|---|---|---|
| Sale, stocktake, physical intake, newer khata, restore | Existing supported durable intent patterns | Preserved; relevant existing frontend regressions pass | No new certification of all production workflows |
| Expense | In-memory adapter ID/payload; business adapter protected by Pass 5 | Expense VM immutable request + gate + unresolved presentation | Adapter durability/full-payload rejection `BLOCKED_EXTERNAL_OWNER` |
| Supplier financial actions | In-memory IDs; incomplete payment scoped reconciliation | Original VM snapshot/IDs retained across field edits and same-dialog retry | Restart and scoped backend reconciliation `BLOCKED_EXTERNAL_OWNER` |
| Stock delta | Same-VM GUID; supported backend replay/status | Atomic entry, immutable retry request, prevents resubmit after confirmation | Durable store integration/reconstruction not implemented; restart/stock ledger hostile proof NOT_RUN |
| New purchase | VM GUID; backend replay/status and invoice uniqueness | Inspected; unchanged | Durable immutable request, deep line snapshot and single-flight remain frontend work; not represented as externally impossible |
| Warranty | Instance IDs; active protected source/contracts | Inspected; unchanged | Protected owner decision required |
| Product aggregate | In-memory fingerprint/ID; backend canonical actor/fingerprint checks | Inspected; unchanged | Durable integration/reconstruction and pre-lookup atomic save gate remain frontend work |

Existing `ClientOperationIntentStore` intentionally stores ID and payload SHA256 only; it does not reconstruct submitted payload/status after restart. It was not modified. A protected reconstructible-request extension and its lifecycle still need coordinated implementation and testing. This report does not claim that adding a hash-only ID would satisfy restart recovery.

## D. F07 — exact guarded command scope

| ViewModel / operation | Protection and evidence |
|---|---|
| CustomerEditViewModel.SaveAsync | Atomic Interlocked gate before awaits; local submitted-field capture; pending fields frozen; completed VM cannot save again |
| SupplierEditViewModel.SaveAsync | Atomic gate, field capture/freeze and busy presentation |
| ExpenseEditViewModel.SaveAsync | Atomic gate, immutable submitted expense retained on uncertainty; original same-dialog retry; completed VM cannot save again |
| StockAdjustmentViewModel.ApplyAsync | Atomic gate, immutable submitted adjustment, same ID/payload on uncertain retry; completed VM cannot apply again |
| SupplierDetail payment/advance/refund/reversals | One shared atomic gate, original payload/reversal reason retained; unresolved different action blocked |

Underlying RelayCommand was not globally changed. Direct method/programmatic entry cannot bypass these gates, even though RelayCommand.Execute itself does not consult CanExecute. Dialog inputs bind to pending edit availability; customer/expense/stock and supplier views expose visible submission state. Finally releases transient gates without retiring unresolved financial snapshots.

Five new editor tests plus five supplier tests passed. Broader purchase/product mutation paths remain uncorrected; no universal single-flight claim. Enter/click UI input, actual process restart, close/reopen state retention and concurrent PostgreSQL stock effects were not executed.

## E. Backend dependency register

| ID | Required existing authority / observed limitation | Minimal owner decision and affected proof |
|---|---|---|
| F03/F04 payment | SupplierAccountHandlers payment replay compares supplier and amount, not all method/purpose/reference/note/actor/session semantics | Backend finance owner approves canonical fingerprint and replay scope; test same ID + changed fields/owner against real PG |
| F03 reconciliation | Payment canonical record omits terminal required by authenticated public status scope; refund has no equivalent canonical outcome ledger integration | Backend outcome owner approves scoped committed/rejected/unknown resolution using existing authority; test actor/terminal and restart reconciliation |
| F04 expense | Posted expense replay lacks full submitted-payload comparison; RemoteBackendBusinessOperationsService is Pass 5-owned | Finance/Pass 5 owner handoff and canonical payload equivalence; restart and changed-payload PG tests |
| F04 warranty | Pass 5 D13 source and warranty authority actively protected | Warranty owner approves frontend adapter/pending-state boundaries before integration |
| F04 common store | SHA-only storage cannot reconstruct original submitted request | Frontend coordinated protected-payload and status lifecycle design, tested restart/corruption/scope; no new backend truth invented |

Correction to an early analyst hypothesis: **current refund replay already compares method, actor, source/reference and note** in SupplierAccountHandlers. Real changed-note and other-authorized-actor rejection passed. Refund should not be described as having the payment handler's incomplete fingerprint. Its canonical public outcome integration remains a separate limitation.

## F. Executed tests and failures

Exact test/build commands and logs are retained in the evidence paths below; filters and counts are based on terminal TRX, not compilation alone.

| Gate | Result | Environment / evidence |
|---|---|---|
| Initial Desktop build during supplier implementation | FAIL, 17 IDE0011 errors; no certification | `initial-build.log`; braces subsequently corrected |
| Initial new frontend tests | Compilation FAIL; 0 tests executed | `initial-submission-tests.log`; missing IO import/required fixture IDs and braces corrected |
| Corrected focused new frontend tests | 10 passed / 0 failed / 0 skipped | `submission-tests-attempt02.log` + TRX; included in later frozen 25, do not add twice |
| Frozen frontend/recovery regression filter | 25 passed / 0 failed / 0 skipped | `frozen-frontend-regressions.log`, `tests/frozen-frontend-regressions.trx` |
| Frozen prior UI Pass 1/2/3 regressions | 30 passed / 0 failed / 0 skipped | `frozen-ui-regressions.log`, `tests/frozen-ui-regressions.trx` |
| Owned model schema bootstrap | 1 passed / 0 failed / 0 skipped | `postgres-model-fixture/model-bootstrap.trx`; infrastructure, not a business case |
| Narrow PostgreSQL finance replay | 4 passed / 0 failed / 0 skipped | `postgres-model-fixture/test-results/` TRX and `commands.log` |
| Desktop Release build | PASS, 0 warnings / 0 errors | `frozen-release-build.log`; source build only |
| Owned changed-path git diff --check | PASS, exit 0 | `owned-diff-check.log` |
| Whole inherited worktree diff --check | FAIL, exit 2 | `diff-check.log`; pre-existing unchanged Buttons/Tables/SalesHistory/BusinessOperationsReadServices whitespace; not concealed or fixed |

Frozen frontend breakdown: new editor 5, new supplier 5, existing intent store 6, physical intake 2, sale 1, stocktake 1, khata 4, stock same-instance identity 1. UI breakdown: geometry 8, focus/accessibility 13, responsive 9. These checks do not render a live operational desktop.

Commands:

```powershell
dotnet test tests/EdgeRetails.Desktop.PerformanceTests/EdgeRetails.Desktop.PerformanceTests.csproj --no-build --no-restore --filter 'FullyQualifiedName~Phase1EditorSubmissionSafetyTests|FullyQualifiedName~Phase1SupplierSubmissionSafetyTests|FullyQualifiedName~StockAdjustmentOperationIdentityTests|FullyQualifiedName~Phase3SaleDurableIntentTests|FullyQualifiedName~Phase3StocktakeDurableIntentTests|FullyQualifiedName~Phase3PhysicalIntakeDurableIntentTests|FullyQualifiedName~Phase3ThakaDurableIntentTests|FullyQualifiedName~Phase3ClientOperationIntentStoreTests' --logger 'trx;LogFileName=frozen-frontend-regressions.trx' --results-directory scratch/frontend-phase1-20261008/tests --verbosity quiet
dotnet test tests/EdgeRetails.UnitTests/EdgeRetails.UnitTests.csproj --no-restore --filter 'FullyQualifiedName~FrontendPass1GeometryBaselineTests|FullyQualifiedName~FrontendPass2FocusAccessibilityTests|FullyQualifiedName~FrontendPass3ResponsiveLayoutTests' --logger 'trx;LogFileName=frozen-ui-regressions.trx' --results-directory scratch/frontend-phase1-20261008/tests --verbosity quiet
& scratch/frontend-phase1-20261008/Invoke-OwnedModelFixture.ps1 -Port 55648 -TestFilter 'FullyQualifiedName~Phase1FrontendRecoveryContractPostgresTests&FullyQualifiedName!~OwnedModelFixtureBootstrap' -EvidenceDirectory scratch/frontend-phase1-20261008/postgres-model-fixture
dotnet build src/EdgeRetails.Desktop/EdgeRetails.Desktop.csproj --configuration Release --no-restore --verbosity quiet
```

### Isolated PostgreSQL evidence and boundaries

Provider PostgreSQL 18 / Npgsql. New disposable cluster `EdgeRetailsMasterPg_86914d2369f44412ab60919d426d5749`, loopback port 55648, database exactly `edge_retails_master_test`. Destination guard rejects other database names, production default and non-owned roots; existing harness verifies actual SHOW data_directory. Process-only environment restored. The scratch runner derives from the existing owned runner but replaces migration commands with one explicit EnsureCreated bootstrap. Protected runner source is unchanged.

**No migrations were applied.** Model-defined finance uniqueness, amount precision/checks and foreign keys are present. Migration-only SQL/triggers are omitted: no schema, tracking, warranty or global inventory certification is claimed. The independent fixture review accepted only sequential External supplier payments/refunds and Bank expense characterization.

Measured facts: supplier advance25 gives balance -25; opening payable100/settlement25 gives75; advance100/refund25 gives -75; each replay has one intended payment/refund, account entry and audit, with no External cash movement. Refund changed note and other authorized actor are rejected. Expense25.50 retains one expense/audit, original amount/description, no Bank cash movement. Actual transport faults, Desktop restart and stock/purchase hostile tests remain NOT_RUN, rather than passed.

Runner terminal result: ProviderVerified=true, CompletionStatus=PASS, ExitCode=0, CleanupPass=true. Owned PostgreSQL stopped, status exit3 confirmed no process, and only verified owned temporary root was deleted. This is not production service restart or database cleanup.

## G. Candidate freeze

`candidate-manifest.json` freezes nine edited existing frontend files, three new test files and the owned scratch runner: 13 SHA256 identities. Tests above ran after freeze; hash checks before/after show 13 checked, zero mismatches. Independent reviewer verified 948 preflight file hashes and the exact nine existing-file changes. No installed artifact was replaced.

## H. Independent verification

Separate Agent D accepted only the narrow partial candidate, with terminal verdict `PHASE1_BLOCKED_EXTERNAL_OWNER`. Returned findings are retained at `scratch/frontend-phase1-20261008/independent-validator.md`.

The reviewer challenged memory-only lifetime, absent reconstruction/status lifecycle, payment/expense backend guarantees, incomplete broader command coverage, confirmed-rejection handling and limited PostgreSQL schema/testing. No candidate-introduced duplicate posting or protected-source change was demonstrated. This does not establish that all failure paths are safe.

## I. Roadmap status

- F03: partial frontend snapshot/gate fix tested; complete restart/reconciliation **blocked by external owner**.
- F04: coverage/dependency map complete; durability/reconstruction **not corrected** across listed gaps; protected dependencies and frontend implementation remain.
- F07: named customer/supplier/expense/stock/supplier-finance gates tested; universal mutation/restart coverage **partial**.
- F05/F06/F08/F09/F10/F13/F14/F17: Phase 2 planned only.
- F11/F12/F15/F16: Phase 3 planned only.
- F01/F02: Phase 4 parked; release alignment and production rollout not authorized.

## J. Final verdict and stop

**`PHASE1_BLOCKED_EXTERNAL_OWNER`**. The required financial replay/reconciliation authority cannot be completely corrected inside this frontend-owned scope. Specific partial implementation and NOT_RUN gates above remain visible; none are hidden behind unrelated passed tests.

**`DEPLOYMENT_HELD_BY_BACKEND_GOVERNANCE`**. Stop here. Complete ownership decisions, durable payload/lifecycle integration and the missing hostile restart/stock/purchase proofs within Phase 1 before any certification. Do not automatically begin another remediation phase or deploy this source candidate.
