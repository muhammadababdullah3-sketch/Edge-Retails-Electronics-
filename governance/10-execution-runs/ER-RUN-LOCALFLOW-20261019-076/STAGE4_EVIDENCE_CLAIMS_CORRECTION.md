# STAGE 4: Precise Evidence Claims Correction (J1–J5, Build, Timestamps)

**Run ID:** `ER-RUN-LOCALFLOW-20261019-076`
**Authority:** `OWNER-UPDATE-20261018-019` & `OWNER-UPDATE-20261019-007`
**Timestamp UTC:** `2026-10-19T12:00:00Z`

---

## 1. Targeted Test Oracles J1–J5 Exact Corrections

| Test Oracle | Run 075 Root Finding | Run 076 Precise Correction & Evidence | Seam / Boundary Qualification |
|---|---|---|---|
| **Targeted_J1** | C1: Over-claimed server single cash effect without raw ledger query. | Renamed and qualified as client operation-ID reuse and pre-POST validation rejection. Proves 0 POST requests on category rejection, intent retention after readback miss, and matching ID on retry. Server-side single durable cash effect marked **UNPROVEN / OPEN**. | Real HTTP client adapter + intent store seam. |
| **Targeted_J2** | C2: Reflected private methods, skipped `ICommand.Execute`, omitted refund reversal. | Added `ReverseRefundCommand` and `ReverseRefundAsync` with `"refund-reversal:<id>"` cleanup. Added `ICommand.Execute` invocations across all 5 RelayCommands, proving no unhandled exceptions escape the async void boundary. | Full UI Command Boundary (`ICommand.Execute` on WPF Dispatcher). |
| **Targeted_J3** | C7: Mock service seam not explicitly qualified. | Documented as testing `StockAdjustmentViewModel.ApplyAsync` with mock backend proxy. Proves payload mismatch throws `InvalidOperationException`, leaves `CanEdit == true`, and restoring values reuses operation ID. | ViewModel + Mock Service Seam. |
| **Targeted_J4** | C3: Used test-local `BuildPayload` duplicate helper. | Refactored to instantiate production `WarrantyViewModel` with real dependencies. Directly executes production `CreateClaimAsync` and `CreateClaimCommand`, verifying canonical sorting stability, fingerprint mismatch rejection, and partial claim lifecycle after outcome reconciliation. | Production ViewModel Execution Boundary. |
| **Targeted_J5** | C7: Fake HTTP pagination seam not explicitly qualified. | Documented as testing `RemoteStocktakeWorkflowService` unit cursor paging through fake HTTP handler. Verifies 201 units paging across cursor pages and typed `BackendOperationException` on 50-page cap or cycle. | Service + Fake HTTP Paging Seam. |

---

## 2. Compilation & Test Evidence Provenance

1. **Solution Compilation (`build.log`):**
   - Deposited raw build output: `test-results/build.log` (7,330 bytes, sha256: `2a5ebe854f3ca197d4086e6b5f909fb54cafa978860efffca5304686b930844c`).
   - Verified 0 Warnings and 0 Errors across all 12 projects.
2. **Contemporaneous Test Runs:**
   - Desktop Performance & Safety: 409 passed out of 409 (`run076_desktop_performance_safety_tests.trx`, 25 s duration).
   - Unit Tests: 1,374 passed out of 1,374 (`run076_unit_tests.trx`, 17 s duration).
3. **Historical TRX Reconciliation:**
   - Pack Concurrency (14 cases): Dated 18 Oct 21:50 local, deposited as `run044_historical_thaka_pack_subledger_tests.trx`.
   - PostgreSQL Read Contract (5 cases): Dated 18 Oct 06:24 local, deposited as `run044_historical_postgresql_read_contract_tests.trx`.
   - Explicitly classified as historical prior evidence, resolving Finding C4.
4. **Timestamp Chronology:**
   - Fixed timestamp `2026-10-19T12:00:00Z` applied to all ZIP archives for bit-for-bit reproducibility.
   - Contemporaneous test executions and deposition events recorded with actual live UTC timestamps.
