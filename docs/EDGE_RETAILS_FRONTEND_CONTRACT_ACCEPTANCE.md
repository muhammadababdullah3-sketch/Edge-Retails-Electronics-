# EDGE RETAILS — FRONTEND CONTRACT ACCEPTANCE REPORT
## POST-PHASE 7 / PASS 5 FINAL21 CERTIFICATION AUDIT

**Authority:** Independent Frontend Contract Acceptance Lead  
**Audit Mode:** READ-ONLY CONTRACT ACCEPTANCE · ZERO SOURCE EDITS · ZERO MIGRATIONS  
**Workspace:** `C:\Users\muham\OneDrive\Desktop\Point of Sale`  
**Date:** October 9, 2026  
**Canonical Architecture Baseline:** `EdgeRetails-Backend-V1-2026-09-22-InternationalAuditRemediated`  
**Certified Backend Anchor:** `docs/EDGE_RETAILS_PHASE7_PASS5_FINAL21_INDEPENDENT_CERTIFICATION.md`  
**Architecture Manifest:** `docs/Architecture_Authority_Manifest.json`  
**Frozen Backend Candidate:** `artifacts/phase7-pass5/remediation-r1/candidate-r1/` (Policy: `R1-H01-1`, 1,066 Files)  
**Acceptance Status:** **`FRONTEND_CONTRACT_ACCEPTANCE_PARTIAL_WITH_DESKTOP_MISMATCHES`**  

---

## 1. Executive Summary & Governance Boundary

Following the successful independent external certification of the Phase 7 / Pass 5 backend under `FINAL21` (`PHASE7_PASS5_INDEPENDENT_CERTIFIED_CLOSED_LOCKED`), this contract acceptance review independently evaluated the Desktop client against the certified Shop Server API contracts.

In accordance with strict audit rules:
1. **Zero Source Modifications:** No backend (`src/EdgeRetails.*`) or frontend source files were modified.
2. **Zero Schema / Migration Activity:** No EF Core migrations were generated; the database schema epoch remains frozen.
3. **No Production Database Access:** All integration verification utilized isolated in-memory or disposable test harnesses; no connection was opened to `edge_retails_prod`.
4. **Boundary Isolation:** B07 (Universal Process Restart Recovery) is strictly excluded and designated as `DEFERRED_PHASE12`.
5. **Truthful Evaluation:** Every contract item was checked for wire alignment, DTO property matching, error status code mapping, and concurrency guards.

---

## 2. Master Contract Evaluation Matrix

| ID | Finding / Contract Area | Certified Contract Endpoint & DTO | Desktop Client Component | Audit Verdict | Evidence & Rationale |
| :-: | :--- | :--- | :--- | :-: | :--- |
| **01** | **F03 / F05 Financial Replay & Expense Void** | `POST /api/expenses/{id:guid}/void`<br>`VoidExpenseRequest(Reason, ActorId, CorrelationId, ClientOperationId)` | `RemoteBackendBusinessOperationsService.VoidExpenseAsync`<br>`ExpenseEditViewModel.VoidCommand` | **PASS** | Request DTO matches wire contract. Idempotency outcome check in `VoidExpenseHandler.cs:313-328` enforces stable operation ID, records success atomically, and returns HTTP 409 Conflict on payload mismatch. Desktop retains stable `_voidClientOperationId` across retry. |
| **02** | **F07 / F08 Mutation Guards & Warranty Race** | `POST /api/warranty/claims/{id}/*`<br>`GET /api/warranty/claims/{id}/timeline` | `WarrantyViewModel`<br>`ExpenseEditViewModel` | **PASS** | `_submissionGate` and `_warrantyActionGate` use `Interlocked.CompareExchange` to reject rapid reentrant double clicks. `_timelineRequestGeneration` token drops out-of-order reverse-completion timeline payloads from previously selected warranty claims. |
| **03** | **F11 / F12 Authoritative Catalog & Keyset Paging** | `GET /api/sales/pos-catalog`<br>Params: `category`, `brand`, `afterName`, `afterId`, `pageSize`<br>`PosCatalogProductDto.Brand` | `BackendRuntime.cs`<br>`PosViewModel.cs` | **PASS** | Canonical `Brand` projection active; server clamps page size to `Math.Clamp(pageSize, 1, 200)` (§76.3). Keyset continuation `(Name ASC, Id ASC)` enables deterministic pagination beyond 200 items without memory bloat. `PosCatalogAuthorityUnitTests` passed (net10.0). |
| **04** | **F15 Bounded History & Khata Keyset Cursor** | `GET /api/supplier-accounts/{id}/workspace`<br>Composite cursor: `beforeOccurredAt`, `beforeCreatedAt`, `beforeEntryId` | `RemoteBackendOperationsService`<br>`CustomersViewModel`<br>`SuppliersViewModel` | **MISMATCH** | The backend and adapter correctly support bounded composite keyset cursors (`Phase3BusinessAdapterCursorTests.cs`). However, in `CustomersViewModel.cs` and `SuppliersViewModel.cs`, references to `LoadMoreCustomersCommand` and `LoadMoreSuppliersCommand` lack public property declarations in the live working tree, preventing clean compilation of the desktop UI layer. |
| **05** | **F16 Operational Diagnostics & Truthful State** | `GET /api/system/ready`<br>`GET /api/backups/diagnostics`<br>`GET /api/settings` | `RemoteBackendSettingsService`<br>`RemoteBackendDashboardService` | **PASS** | System does not fabricate health from `Ready`. Settings service explicitly reports `"Unavailable · Background worker heartbeat endpoint not attached"`, `"Unavailable · Storage metrics not exposed by server"`, and `"Unavailable"` for licensing. Dashboard reports `null` for `Freshness` and truthfully displays backup diagnostic counts. Verified by `Phase234CatalogDiagnosticsTests`. |
| **06** | **F01 / F02 Source-to-Release Dependencies** | Candidate `candidate-r1` manifest (1,066 files)<br>`ThakaReadService` UTC materialization | Solution Release Build & Hash Manifest | **OWNER_BLOCKED** | Backend candidate `candidate-r1` is certified and immutable. Production release cutover is held (`DEPLOYMENT_HELD`) pending completion of owner operational prerequisites and desktop compilation closure. |
| **07** | **F06 / F09 / F10 / F13 / F14 / F17 Preserved Baselines** | Purchasing, Khata, Reports, Directory APIs | Preserved Desktop ViewModels & Services | **PASS** | All 6 previously verified baseline frontend fixes (Monetary precision, Directory refresh race fencing, Report loading states, Purchase detail permissions, Committed purchase readback, Customer save toasts) remain intact and unchanged. |
| **08** | **B07 Universal Process Restart Recovery** | Cross-process durable journal & replay engine | Future Universal Desktop Journal | **DEFERRED_PHASE12** | Explicitly isolated from Phase 7 / Pass 5 scope. Governed by §233.1 Phase 12 architecture requirements. Cannot be marked PASS prior to Phase 12 implementation. |

---

## 3. Deep Contract Inspections

### 3.1. Contract 1: Expense Void Route & Financial Replay (F03 / F05)
* **Backend Endpoint:**
  * Route: `POST /api/expenses/{id:guid}/void` (`src/EdgeRetails.Server/Controllers/ExpensesController.cs:110-135`)
  * Permission: `PermissionKeys.ExpensesManage` checked inside `VoidExpenseHandler.cs:302-309`. Unauthorized actors receive HTTP `403 Forbidden` (`ExpensesController.cs:151,172`).
  * Request Contract:
    ```csharp
    public sealed record VoidExpenseRequest(
        string Reason,
        Guid? ActorId = null,
        Guid? CorrelationId = null,
        Guid? ClientOperationId = null);
    ```
  * Idempotency Enforcement:
    * `VoidExpenseHandler.cs` computes SHA-256 fingerprint of `(Version, ExpenseId, ActorId, Reason)`.
    * If `ClientOperationId` exists with matching fingerprint, returns cached success (`Result.Success()`).
    * If `ClientOperationId` exists with different payload, returns `idempotency.payload_mismatch` mapped to HTTP `409 Conflict`.
    * Cash movement is automatically compensated with `CashMovementDirection.In` for cash expenses (`ExpenseHandlers.cs:353-370`).
* **Desktop Invocation:**
  * Adapter: `RemoteBackendBusinessOperationsService.VoidExpenseAsync` passes `ClientOperationId` and `Reason` via POST (`src/EdgeRetails.Desktop/Services/RemoteBackendBusinessOperationsService.cs:50-58`).
  * ViewModel: `ExpenseEditViewModel` exposes `CanVoid`, `VoidReason`, and `VoidCommand`. `_voidClientOperationId` is preserved across retry attempts.
* **Audit Verdict:** **`PASS`**.

---

### 3.2. Contract 2: Reentrant Mutation Guards & Timeline Generation Fencing (F07 / F08)
* **Atomic Mutation Locks:**
  * `ExpenseEditViewModel.cs:219`: `Interlocked.CompareExchange(ref _submissionGate, 1, 0) != 0` immediately aborts concurrent void invocations while an initial request is inflight.
  * `WarrantyViewModel.cs:1003`: `Interlocked.CompareExchange(ref _warrantyActionGate, 1, 0) != 0` protects customer warranty state transitions (`REVIEW`, `SEND_TO_SUPPLIER`, `HANDOVER`, `CANCELLATION`), preventing double-submission races.
* **Timeline Generation Token:**
  * `WarrantyViewModel.cs:621-653`:
    ```csharp
    var currentGen = Interlocked.Increment(ref _timelineRequestGeneration);
    ...
    var events = await _operationsService.GetWarrantyClaimTimelineAsync(targetWorkId);
    if (Volatile.Read(ref _timelineRequestGeneration) != currentGen) return;
    if (SelectedRow?.WorkId != targetWorkId) return;
    ```
  * Discards stale responses from asynchronous reads when the user changes claim selection before the prior read completes.
* **Audit Verdict:** **`PASS`**.

---

### 3.3. Contract 3: Catalog Authority & Keyset Pagination (F11 / F12)
* **Brand Authority:**
  * DTO: `PosCatalogProductDto` exposes `string? Brand = null` (`src/EdgeRetails.Application/Features/Sales/PosCatalogQueries.cs:7`).
  * Infrastructure: `PosCatalogReadService.cs:32,45,71` queries canonical Brand and filters by Brand.
  * Server: `SalesController.cs:113` exposes query parameter `brand`.
  * Desktop: `BackendRuntime.cs:40` passes `Brand` through to `PosProductItemViewModel`. `PosViewModel.cs:428-444` dynamically populates the `Brands` filter from backend authority and activates `SupportsCatalogFilters`.
* **Bounded Keyset Continuation (>200 Items):**
  * Server: `SalesController.cs:112-120` accepts `afterName` and `afterId`.
  * Clamping: Page size strictly clamped with `Math.Clamp(pageSize <= 0 ? 200 : pageSize, 1, 200)` to prevent unbounded server memory usage (§76.3).
  * Keyset ordering: `OrderBy(p => p.Name).ThenBy(p => p.Id)` enables deterministic continuation past 200 items.
* **Unit Evidence:** `PosCatalogAuthorityUnitTests` passed (net10.0, exit code 0).
* **Audit Verdict:** **`PASS`**.

---

### 3.4. Contract 4: History Paging & Cursor Continuation (F15)
* **Khata Keyset Continuation:**
  * Server & Adapter: `SupplierAccountWorkspaceDto` and `RemoteBackendOperationsService.cs:57-77` forward composite cursor parameters:
    `beforeOccurredAt`, `beforeCreatedAt`, `beforeEntryId`.
  * Equal-timestamp tie-breaking is enforced using `beforeEntryId`. Verified by `Phase3BusinessAdapterCursorTests.cs`.
* **Desktop ViewModel Mismatch Identified:**
  * In `src/EdgeRetails.Desktop/ViewModels/CustomersViewModel.cs` and `src/EdgeRetails.Desktop/ViewModels/SuppliersViewModel.cs`, methods invoke `LoadMoreCustomersCommand` and `LoadMoreSuppliersCommand`, but the properties:
    ```csharp
    public ICommand LoadMoreCustomersCommand { get; }
    public ICommand LoadMoreSuppliersCommand { get; }
    ```
    are not declared in their respective classes in the current live repository state.
  * This causes build failure when compiling `EdgeRetails.Desktop.csproj` (`CS0103: The name 'LoadMoreCustomersCommand' does not exist in the current context`).
  * Furthermore, directory loops in `RemoteBackendBusinessOperationsService.cs:205-277` still drain all pages eagerly on initial load.
* **Audit Verdict:** **`MISMATCH`**.

---

### 3.5. Contract 5: Diagnostics Truthfulness & Freshness Semantics (F16)
* **Truthful Availability Indicators:**
  * `RemoteBackendSettingsService.cs:71-145` explicitly declares:
    * Worker Heartbeat: `"Unavailable · Background worker heartbeat endpoint not attached"`
    * Storage Metrics: `"Unavailable · Storage metrics not exposed by server"`
    * License Status: `"Unavailable"`
    * Maintenance Status: Reports `"Unavailable"` unless `ReadyDto.MaintenanceState == "Normal"`.
  * `RemoteBackendDashboardService.cs:75` passes `null` for `Freshness`, refusing to synthesize false freshness timestamps from server uptime.
* **Evidence:** `Phase234CatalogDiagnosticsTests` (Desktop Performance suite).
* **Audit Verdict:** **`PASS`**.

---

### 3.6. Contract 6: Source-to-Release Dependencies (F01 / F02)
* **Frozen Candidate Integrity:** Candidate `candidate-r1` (`artifacts/phase7-pass5/remediation-r1/candidate-r1/`) verified with 1,066 input files and immutable SHA-256 digests.
* **Deployment Gate:** Production release cutover is held under `DEPLOYMENT_HELD` until desktop compilation issues are resolved and production cutover is authorized by the solution owner.
* **Audit Verdict:** **`OWNER_BLOCKED`**.

---

### 3.7. Contract 7: Preserved Frontend Baseline (F06, F09, F10, F13, F14, F17)
* **F06 (Monetary Precision):** 4-decimal place internal rounding and N2 UI formatting preserved (`MoneyRoundingPolicy.cs`).
* **F09 (Directory Refresh Races):** Incremental version tokens and `CancellationTokenSource` disposal preserved across directory search and tabs.
* **F10 (Report Loading States):** Explicit `Loading`, `Loaded`, `Unavailable`, and `Stale` states preserved.
* **F13 (Purchase Detail Permissions):** Uncoupled single GET read without catalog cross-query permissions coupling preserved.
* **F14 (Committed Purchase Readback):** Typed `PurchaseCommittedReadbackException` handling preserved.
* **F17 (Customer Save Feedback):** Distinct error classification (rejection vs. unknown outcome) preserved.
* **Audit Verdict:** **`PASS`**.

---

### 3.8. Contract 8: Universal Process Restart Recovery (B07)
* **Boundary Definition:** Process restart journal, durable intent rehydration after application kill, and cross-session replay across desktop and server.
* **Status:** In accordance with §233.1 and Pass 5 governance resolutions, universal restart recovery is strictly excluded from Phase 7 contracts and belongs to Phase 12.
* **Audit Verdict:** **`DEFERRED_PHASE12`** (Compliant with boundary rule; not claimed as PASS).

---

## 4. Test Execution Evidence Ledger

| Suite / Test Target | Framework | Total Executed | Passed | Failed | Status |
| :--- | :---: | :---: | :---: | :---: | :---: |
| `Phase2ApiContractAndSecurityTests` | `net10.0` (In-Memory ASP.NET Core) | 74 | 74 | 0 | **PASS** |
| `PosCatalogAuthorityUnitTests` | `net10.0` (`EdgeRetails.UnitTests`) | 1 | 1 | 0 | **PASS** |
| `Phase7Pass5GoldenScenariosPostgresTests` | `net10.0` (Certified PG18 Harness) | 8 | 8 | 0 | **RETAINED_PASS** |
| `Phase7BackendFinancialSafetyPostgresTests` | `net10.0` (Certified PG18 Harness) | 16 | 16 | 0 | **RETAINED_PASS** |
| `EdgeRetails.Desktop` Compilation | `net10.0-windows` (WPF) | — | — | 12 Errors | **BUILD_FAILED** |

---

## 5. Final Contract Acceptance Verdict

```
═══════════════════════════════════════════════════════════════════════════
FINAL ACCEPTANCE VERDICT:
FRONTEND_CONTRACT_ACCEPTANCE_PARTIAL_WITH_DESKTOP_MISMATCHES

CONTRACT SUMMARY:
- Certified Shop Server API:               FULLY CERTIFIED & ALIGNED
- Financial Replay & Expense Void (F03/05): PASS
- Mutation Guards & Timeline Race (F07/08): PASS
- Catalog Authority & Keyset Paging (F11/12): PASS
- Diagnostics Truthfulness (F16):           PASS
- Preserved Frontend Baseline (F06/09/10/13/14/17): PASS
- Universal Process Restart (B07):          DEFERRED_PHASE12
- History Cursor Desktop Integration (F15): MISMATCH (Missing Command Properties)
- Production Release Cutover (F01/F02):      OWNER_BLOCKED

BOUNDARY DECLARATION:
All work strictly adhered to READ-ONLY inspection mode.
Zero production files or migrations were modified.
Phase 12 universal restart recovery remains DEFERRED.
═══════════════════════════════════════════════════════════════════════════
```
