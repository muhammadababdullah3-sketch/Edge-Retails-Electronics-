# Phase 11 Wave 3 Handoff Specification

- **Run ID**: ER-RUN-20261012-005
- **Prior Submission**: ER-RUN-20261012-004
- **Authoring Authority**: OWNER-UPDATE-20261013-002
- **Recovery Mandate**: ER-P11-W2-RECOVERY-20261013-001
- **Correction Mandate**: OWNER-MONITOR-20261013-011 (`governance/14-execution-environment/2026-10-13-monitor/1137/CORRECTION.md`)
- **Continuation Mandate**: OWNER-RESUME-NATIVE-CONTROL-20261013 (`governance/14-execution-environment/2026-10-13-monitor/resume-native-control/CORRECTION.md`)
- **Evidence Anchor**: ER-CHANGE-20261013-024
- **Authoritative System Clock**: 2026-10-13T08:30:00Z (13:30:00+05:00 PKT)
- **Candidate Hash**: `SHA256:c8f54aec40f9a516972820fa2161facaf050bdb5245da32da2d2152a225fd964`
- **Wave 2 Status**: `SUBMITTED_FOR_GOVERNANCE_REVIEW` (Verified by Executor across all 70 Tasks with fresh test execution, reconstructable export, and real PostgreSQL 18 proofs)
- **Wave 3 Status**: `LOCKED_PENDING_ACTIVATION` (Gate CLOSED; Group 10 Remains Strictly Inactive until Owner Governance Activation)

---

## 1. Mandatory Scope Boundary & Invariant Notice

> **CRITICAL GOVERNANCE MANDATE**:
> Do NOT execute Wave 3 tasks inside the Wave 2 recovery run. This handoff specification defines the formal boundary, verified server-side prerequisites, API contracts, and canonical scope for Wave 3 execution. Wave 3 will be activated exclusively under a dedicated owner governance activation prompt.

---

## 2. Upstream Wave 2 Deliverables & Foundation

Wave 2 has completed, verified, and locked the core domain, application, and persistence layers for the subledger and posting authorities across Groups 2 through 9 (all 70 tasks, `P11-IMP-028` through `P11-IMP-097`):

1. **Financial Mutation Handlers Wired**:
   - `CompleteSaleHandler`: Wired to `IShopPostingValidationService`, enforcing 7-day backdating window, supervisor cryptographic PBKDF2 PIN verification, open cash session validation, and atomic append-only posting audit.
   - `CreateSaleReturnHandler`: Wired to `IShopPostingValidationService`, validating original invoice posting dates, supervisor authorizations, active warranty conflict checks, and remnant cable subledger allocations.
   - `CommercialExchangeHandler`: Wired to `IShopPostingValidationService`, enforcing posting date verification and atomic balance settlement.
   - `CreatePurchaseHandler` & `ReceiveProductIntakeHandler`: Enforcing triple date separation (`EventTimestampUtc`, `PostingDate`, `SupplierDocumentDate`) and idempotent replay.
   - `CreateStockAdjustmentHandler` & `StocktakeHandlers`: Wired to fail-closed posting validation, rejecting caller-boolean overrides with `posting.caller_boolean_authority_rejected`.
   - `IssueThakaMaterialHandler` & `ReverseThakaMaterialHandler`: Wired with `PostingOperationTypes.Thaka` for atomic material custody tracking and mutual exclusion with sales.
   - `PostExpenseHandler`: Wired to `IShopPostingValidationService`, enforcing closed accounting period restrictions.

2. **Timezone & Reporting Authority**:
   - `ReportingReadService` and all query endpoints fully decoupled from host-timezone authority (`TimeZoneInfo.Local`).
   - Bound to `IShopPeriodBoundaryService` pegged to `Asia/Karachi` (`Pakistan Standard Time`), generating exact inclusive-start and exclusive-end UTC ranges.

3. **Pack & Remnant Subledger**:
   - Real-time container state transitions (`Sealed` -> `Open` -> `Consumed`) and remnant allocation rules enforced fail-closed.
   - Continuous cable length requirements enforced by default; split cuts rejected unless `AllowSplitCuts: true`.

4. **Test & Verification Proofs**:
   - 1,280 unit tests compiled and passing with 0 failures, freshly executed after all source edits (`raw/unit_tests.trx`).
   - 17 integration tests passing on dedicated disposable PostgreSQL 18 cluster (port 55437), proving atomic audits, period locks, append-only triggers, replay safety, concurrency, and exact mathematical quantity/cost conservation (`raw/integration_tests.trx`).
   - Reconstructable export verified with 0 mismatches (`export/Verify-CandidateExport.ps1`).

---

## 3. Wave 3 Scope: Group 10 (Desktop UI Cutover & API Integration)

Wave 3 encompasses tasks `P11-IMP-098` through `P11-IMP-105`:

### P11-IMP-098: Desktop API Contracts & Client DTO Synchronization
- **Scope**: Expose and synchronize HTTP/JSON DTO contracts for subledger posting validation, triple date fields, and supervisor approval requests across EdgeRetails API and Desktop clients.
- **Key Contracts**:
  - `PostingDate` (DateOnly ISO string: `YYYY-MM-DD`).
  - `SupplierDocumentDate` (DateOnly ISO string: `YYYY-MM-DD`).
  - `SupervisorApprovalRequest` (`SupervisorUserId`, `Pin`).
  - Rejection of caller-supplied boolean flags.

### P11-IMP-099: Cashier POS Sales Interface Integration
- **Scope**: Update Cashier POS sales view to display the current shop posting date, enforce cash session selection, and present a modal supervisor PIN prompt whenever a sale backdate within 7 calendar days is requested.
- **Fail-Closed Behavior**: If supervisor PIN is invalid or missing, transaction completion is aborted with user-facing message `posting.supervisor_authorization_required`.

### P11-IMP-100: Sales Return & Commercial Exchange Desktop Workflows
- **Scope**: Integrate exact physical unit identification and loose cable remnant selection dialogs in the Desktop return flow. Display original sale posting date and enforce supervisor authorization for out-of-policy returns.

### P11-IMP-101: Inventory Stock Adjustment & Stocktake Desktop Screen Integration
- **Scope**: Integrate variance review ledger, reason code pickers, and supervisor authorization dialogs for stock adjustments. Ensure Desktop triggers proper resource-locked adjustment operations without client-side calculation authority.

### P11-IMP-102: Purchasing Intake & Supplier Document Date Desktop Validation
- **Scope**: Integrate supplier document date picker and purchase order intake verification views, requiring distinct entry of vendor invoice date and shop stock receipt date.

### P11-IMP-103: Karigar Thaka Issue and Reversal Desktop Management Views
- **Scope**: Build or adapt Karigar Thaka workbench screens to display outstanding material custody, pack allocations, and reversal posting validation statuses.

### P11-IMP-104: Cashier Expense Recording and Shift Reconciliation UI
- **Scope**: Integrate expense entry modal with period lock indicators and shift cash drawer balance verification. Fails closed if expense posting date falls into a locked period.

### P11-IMP-105: Desktop Business Operations & Subledger Audit Reporting Screens
- **Scope**: Synchronize Desktop reporting dashboard date filters with `IShopPeriodBoundaryService`, ensuring daily, monthly, and annual revenue queries strictly align with Karachi shop boundaries rather than cashier workstation clock settings.

---

## 4. Wave 4 Forward Dependencies

The following tasks possess shared Wave 4 responsibilities and are tracked in the Task Register as `PARTIAL (W4 pending)`:
- System-wide end-to-end rehearsal against PostgreSQL 18 container with all 887 integration tests.
- High-concurrency stress testing and deadlock verification across distributed clients.
- Automated backup, failover, and disaster recovery drill verification.
- Final enterprise forensic audit trail ledger seal.

---

## 5. Wave 3 Activation Checklist

- [x] Wave 2 source modifications compiled cleanly (0 warnings, 0 errors).
- [x] Full regression test suite passed (1,280 unit tests, 17 integration tests) with fresh native timestamps.
- [x] Governance artifacts deposited in `governance/10-execution-runs/ER-RUN-20261012-005/`.
- [x] Task register reconciled across all 70 W2 tasks with exact code symbols, assertions, and fresh execution UUIDs.
- [x] Native TRX test results, build logs, and broad integration failure analysis preserved.
- [x] Port isolation verified (PostgreSQL port 5432 strictly untouched; dedicated test port 55437).
- [x] Reconstructable export verified across all 54 source files with 0 mismatches.
- [x] Wave 3 entry gate status: **CLOSED (LOCKED_PENDING_ACTIVATION)**.