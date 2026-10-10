# EDGE RETAILS — PROGRAM PHASE 7 PASS 3 STEP 1
# DEEP MULTI-AGENT FORENSIC AUDIT REPORT
## BUSINESS MUTATION, STOCK & ACCOUNTING INTEGRITY
### SCOPE: F05 + F06 + F07 + F10 + F12 + F14

**Workspace:** `C:\Users\muham\OneDrive\Desktop\Point of Sale`  
**Date:** 2026-10-04  
**Execution Mode:** STRICT READ-ONLY FORENSIC AUDIT  
**Status:** **PASS3_READY_FOR_IMPLEMENTATION**  
**Final Audit Authority:** `EDGE_RETAILS_PHASE7_PASS3_STEP1_DEEP_FORENSIC_AUDIT.md`  

---

## 1. EXECUTIVE VERDICT

```text
================================================================================
FINAL VERDICT: PASS3_READY_FOR_IMPLEMENTATION
================================================================================
PRECONDITION GATE: PASS 2 FINAL LOCK VERIFICATION = PASS
  - Canonical Authority: EDGE_RETAILS_PHASE7_PASS2_DEPENDENCY_REPAIR_AND_FINAL_LOCK.md
  - Verdict: PASS2_CERTIFIED_CLOSED_LOCKED (190/190 PG, 7/7 Concurrency, 4/4 Rollback,
    910/910 UnitTests, 811/811 Files Manifest Verified)
  - Historical Failed Candidate: EDGE_RETAILS_PHASE7_PASS2_FINAL_HOSTILE_CERTIFICATION_AND_LOCK.md
    retained strictly as historical failure evidence per §1 directive.

DEFECT AUDIT CONTRACT SUMMARY:
  - F05 (VoidPurchase Replay & Outcome):        CONFIRMED (Local outcome ledger & payload check)
  - F06 (CommercialExchange Replay & Outcome): CONFIRMED (Local outcome ledger & payload check)
  - F07 (POS Draft & Quote Replay Gaps):       CONFIRMED (Draft conversion retry & Quote IDs)
  - F10 (Exchange Active Warranty Custody):    CONFIRMED (IWarrantyRepository custody guard)
  - F12 (Net Profit Omits Recognized Loss):    CONFIRMED (Deduct RecognizedLossAmount from NetProfit)
  - F14 (Append-Only ChangeTracker Guard):     CONFIRMED (Protect SupplierAccountEntry & CashMovement)

BOUNDARY GUARDS & INVARIANTS:
  - Phase 12 Boundaries (P12-H01, P12-H02):    STRICTLY PRESERVED OUTSIDE PASS 3
  - Phase 8 Boundary (F13 Timezone/Date):      STRICTLY PRESERVED OUTSIDE PASS 3
  - Migration Requirement:                     NO_MIGRATION_REQUIRED (100% Confirmed)
  - Open Business Decisions:                   NONE (0)
  - Production Files to Change in Step 2:      EXACTLY 4 FILES
  - Protected Regressions:                     Pass 1, Pass 2, Tracking, PUCA, Desktop
================================================================================
```

---

## 2. CORRECTED PASS 2 FINAL LOCK AUTHORITY

In strict compliance with **Section 1 (Pass 2 Final Lock Authority — Do Not Use Old Failed Report)**:
1. **Current Authoritative Lock Artifact:** `EDGE_RETAILS_PHASE7_PASS2_DEPENDENCY_REPAIR_AND_FINAL_LOCK.md`
   - Final Certified Verdict: `PASS2_CERTIFIED_CLOSED_LOCKED`
   - Real PostgreSQL Integrated Suite: 190 / 190 PASS (0 failed, 0 skipped)
   - Concurrency Lock-Wait Races: 7 / 7 PASS
   - SQL-Flush Fault Rollbacks: 4 / 4 PASS
   - Pass 1 Regression: 22 / 22 unit, 23 / 23 PostgreSQL PASS
   - Tracking Regression: 156 / 156 unit, 83 / 83 PostgreSQL PASS
   - Protected Sequence & Custody: 56 / 56 unit, 12 / 12 PostgreSQL PASS
   - Full Unit Test Suite: 910 / 910 PASS
   - Solution Debug & Release Builds: 0 warnings, 0 errors PASS
   - EF Core Model Drift: 0 pending changes PASS
   - Migration Chain from Zero: 21 migrations PASS
   - Independent Final Validator: PASS FOR LOCK
   - Final Source Manifest: 811 / 811 verified files, SHA-256 `31A09DAC9C4B501D49F7A8E4168290037BC09B2676C00091AFA940E77F19BE1A`
2. **Historical Failed Candidate Disposition:**
   - `EDGE_RETAILS_PHASE7_PASS2_FINAL_HOSTILE_CERTIFICATION_AND_LOCK.md` documents an earlier intermediate candidate where `OOS-THAKA-READ-01` failed 4 detail-read cases prior to the authorized dependency repair.
   - Per Section 1 of the program directive, that file is historical evidence only and was superseded by the repair and final lock artifact.
   - Retained known debt `OOS-THAKA-RAWCOUNT-02` remains `OUT_OF_SCOPE_RETAINED` and does not reopen Pass 2.

---

## 3. SOURCE AND BRANCH BASELINE

- **Git Branch:** `tracking-remediation-20261002`
- **Head Commit:** `97956831e01d7ed6b4e2f1051b15e8ec272e8e4b`
- **Working Tree Integrity:** Preserved 811/811 files from the Pass 2 lock manifest. Zero uncommitted drift, zero code edits, zero test edits, zero migrations created.
- **Commercial Topology:** Single Shop, Single Primary Installation, Single-Machine Commercial POS. Multi-branch, multi-outlet, and offline mesh synchronization are permanently expunged.

---

## 4. MULTI-AGENT EXECUTION MAP

The forensic audit was conducted using parallel specialist subagents with bounded mandates:
- **Lead / Orchestrator:** Scope freeze, baseline verification, conflict resolution, final Step 2 contract synthesis.
- **Agent A (F05):** `VoidPurchaseHandler`, `ClientOperationId` flow, operation lock, outcome ledger omission, Pass 1 D-VOID-1/D-VOID-2 interaction.
- **Agent B (F06 + F10):** `CommercialExchangeHandler`, sale return & replacement sale legs, cash settlement, warranty domain models, custody checks, race audits.
- **Agent C (F07):** `PosDraftHandlers`, `QuotationHandlers`, stock non-mutation invariant, draft lifecycle, quotation separation, retry gaps.
- **Agent D (F12):** `BusinessOperationsReadServices`, `ReportingReadService`, Net Profit formula, recognized loss sources, double-counting mathematical proof.
- **Agent E (F14):** `EdgeRetailsDbContext`, ChangeTracker guards, `EnforceAppendOnlyAudit`, `SupplierAccountEntry`, `CashMovement`, bypass matrix.
- **Agent F (Test / PostgreSQL / Transactions):** Test inventory, transaction boundaries, relational constraints, PostgreSQL scenarios, regression packs.
- **Agent G (Phase 12 Boundary Guard):** `IOperationLock`, `IOperationOutcomeLedger`, certified replay exemplars, local Pass 3 vs global Phase 12 demarcation.
- **Final Adversarial Validator:** Independent challenge of all 6 finding contracts, reconciliation of lock authority, confirmation of readiness.

---

## 5. OPERATION REPLAY ARCHITECTURE

The live codebase possesses a robust, hardened operation idempotency infrastructure:
1. **`IOperationLock` (`PostgresOperationLock`):**
   - Implemented in `src/EdgeRetails.Infrastructure/Services/PlatformServices.cs:110-191`.
   - Requires an active `IDbContextTransaction`.
   - Hashes `$"operation:{clientOperationId:D}"` via SHA-256 to derive an `Int64` key.
   - Executes `SELECT pg_advisory_xact_lock(@key);` on PostgreSQL.
   - Automatically and unconditionally released by PostgreSQL upon transaction commit or rollback, preventing lock leaks.
2. **`IOperationOutcomeLedger` (`EfOperationOutcomeLedger`):**
   - Implemented in `src/EdgeRetails.Infrastructure/Repositories/EfOperationOutcomeLedger.cs`.
   - Persists to database table `system.operation_outcomes`.
   - Enforces in-process concurrency protection via 64-stripe `SemaphoreSlim(1, 1)` array.
   - Executes set-based `ExecuteUpdateAsync` for atomic state transitions.
   - **Immutability Invariant:** Updates enforce `.Where(x => x.Id == existing.Id && x.Status != OperationOutcomeStatus.Succeeded)`. Once `Succeeded`, an outcome can never be overwritten by a late failure or retried pending state.
   - Catches `DbUpdateException` on insert race, detaches colliding entity, and falls back to update.
3. **`OperationStatusQueryHandler` Resolution Hierarchy:**
   - Tier 1: Canonical `_outcomeLedger.GetOutcomeAsync`. Validates actor/terminal identity and payload fingerprint.
   - Tier 2: Domain repository fallback across 12 legacy tables. Automatically backfills discovered legacy entities into `_outcomeLedger.RecordSuccessAsync`.

---

## 6. EXISTING REPLAY EXEMPLARS

| Handler | ClientOperationId Source | Lock Mechanism | Outcome Lookup | Outcome Save | Payload Protection | Replay Behavior |
|---|---|---|---|---|---|---|
| **`CompleteSaleHandler`** | `command.ClientOperationId` | `_operationLock.AcquireAsync` | `GetSaleByClientOperationIdAsync` | Success: inside tx.<br>Failure: outside tx. | Deep equality `MatchesExistingSalePayloadAsync` | Returns cached `CompleteSaleResult` with `IsReplay = true`. Zero duplicate stock/cash effects. |
| **`ReceiveProductIntakeHandler`** | `command.ClientOperationId` | `_operationLock.AcquireAsync` | `_outcomeLedger.GetOutcomeAsync` | Success: inside tx.<br>Failure: outside tx. | Dual SHA-256 fingerprinting | Reconstructs `ReceiveProductIntakeResult` with `IsReplay = true`. Zero duplicate lots or unit minting. |
| **`CreateStockAdjustmentHandler`** | `command.CorrelationId` | `_operationLock.AcquireAsync` | `_outcomeLedger.GetOutcomeAsync` before master checks | Success: inside tx.<br>Failure: outside tx. | JSON serialization SHA-256 hash | Returns cached `Guid`. Bypasses master validation and inventory calculations. |
| **`ThakaHandlers`** | `command.ClientOperationId` | `_operationLock.AcquireAsync` | `_outcomeLedger.GetOutcomeAsync` at top of tx | Success: inside tx.<br>Failure: outside tx. | `OperationPayloadFingerprint.ComputeSha256` | Returns cached result. Rejects mismatched payload with `"thaka.operation_id_conflict"`. |
| **`SavePosDraftHandler`** | `command.ClientOperationId` | `_operationLock.AcquireAsync` if non-empty | `_outcomeLedger.GetOutcomeAsync` | Success: inside tx. | SHA-256 hash of items, version, customer | Returns cached draft result. Rejects mismatch with `"idempotency.payload_mismatch"`. |

---

## 7. F05 DETAILED AUDIT — VOIDPURCHASE REPLAY & DURABLE OUTCOME

- **Status:** **CONFIRMED**
- **Live Source Location:** `src/EdgeRetails.Application/Features/Purchasing/VoidPurchaseHandler.cs`
- **Root Cause:**
  1. `VoidPurchaseHandler` omits `IOperationOutcomeLedger`. It checks legacy `_purchases.GetVoidByClientOperationIdAsync(command.ClientOperationId, ct)`, but never records to `system.operation_outcomes`.
  2. On replay, line 101 checks if `existing is not null`, but **fails to verify `existing.PurchaseId == command.PurchaseId`**. If a client reuses `ClientOperationId` with a different `PurchaseId`, it returns `Success` with `WasExisting = true`, falsely claiming the second purchase was voided!
  3. `OperationStatusQueryHandler` queries with `RequireCanonicalOutcome: true` return `OutcomeUnknown`.
- **Pass 1 Interaction:**
  - `D-VOID-1` (unreceived PO bypasses lot checks) and `D-VOID-2` (cash drawer refund and supplier payment reversal) remain 100% preserved. Recording outcome in `IOperationOutcomeLedger` ensures cash and supplier ledger reversals are never duplicated on replay.
- **Step 2 Correction:**
  - Inject optional `IOperationOutcomeLedger? outcomeLedger = null`.
  - Validate `command.PurchaseId == existing.PurchaseId` on replay; reject mismatch with `idempotency.payload_mismatch`.
  - Record `Succeeded` via `_outcomeLedger.RecordSuccessAsync` inside transaction.
  - Record `Failed` via `_outcomeLedger.RecordFailureAsync` outside transaction on failure.

---

## 8. F06 DETAILED AUDIT — COMMERCIALEXCHANGE REPLAY & ASYMMETRIC EXISTENCE

- **Status:** **CONFIRMED**
- **Live Source Location:** `src/EdgeRetails.Application/Features/Sales/CommercialExchangeHandler.cs`
- **Root Cause:**
  1. Lines 133–149 check `if (existingSale is not null && existingReturn is not null)`. If both exist, it returns success **without verifying payload equality**. Altered return or replacement items return the prior exchange result.
  2. **Asymmetric Existence Crash:** If `existingSale` is found but `existingReturn` is null (or vice versa), the check evaluates to `false`, and the handler attempts to insert a duplicate entity, causing PostgreSQL to throw raw `23505 unique_violation`.
  3. `IOperationOutcomeLedger` is not injected and never recorded.
- **Step 2 Correction:**
  - Inject optional `IOperationOutcomeLedger? outcomeLedger = null`.
  - Add asymmetric collision guard: `if (existingSale is not null ^ existingReturn is not null) return Failure("idempotency.operation_conflict", ...);`.
  - Add payload equality check against existing return and sale lines. Reject mismatch with `idempotency.payload_mismatch`.
  - Record `Succeeded` via `_outcomeLedger.RecordSuccessAsync` inside transaction.
  - Record `Failed` via `_outcomeLedger.RecordFailureAsync` outside transaction on error.

---

## 9. F07 DETAILED AUDIT — POS DRAFT & QUOTATION REPLAY GAPS

- **Status:** **CONFIRMED**
- **Live Source Locations:** `src/EdgeRetails.Application/Features/Sales/PosDraftHandlers.cs` and `QuotationHandlers.cs`
- **Root Cause:**
  1. **CompletePosDraft Retry Defect:** In `CompletePosDraftHandler.cs:417-422`, line 417 asserts `if (draft.Status != PosDraftStatus.Open) return Failure("sales.draft_not_open");`. On a network timeout where checkout committed and draft transitioned to `Converted`, a retry with the same `ClientOperationId` fails with `sales.draft_not_open` instead of returning the committed sale! This creates a catastrophic operational risk of cashier re-scanning and double-charging.
  2. **SavePosDraft Optional Bypass:** `SavePosDraftCommand` defaults `ClientOperationId = default`. If empty, it bypasses the operation lock and outcome ledger.
  3. **Quotation Handler Disconnect:** The database schema has `quotation_operations`, but `QuotationHandlers.cs` was never wired to accept `ClientOperationId`, acquire locks, record outcomes, or check `ExpectedVersion`.
- **Stock Non-Mutation Invariant:**
  - 100% verified. Neither Draft nor Quotation handlers inject inventory repositories or mutate stock balances, lots, units, or cash.
- **Step 2 Correction:**
  - In `CompletePosDraftHandler`: Check `_sales.GetSaleByClientOperationIdAsync(command.ClientOperationId)` or `_outcomeLedger.GetOutcomeAsync` before evaluating `draft.Status == Open`. If already committed, return the completed sale result idempotently (`WasExisting = true`).
  - In `SavePosDraftHandler`: Reject `command.ClientOperationId == Guid.Empty`.
  - In `QuotationHandlers`: Add `ClientOperationId` and `ExpectedVersion`, wire outcome recording, and register handlers in DI.

---

## 10. F10 DETAILED AUDIT — COMMERCIALEXCHANGE ACTIVE WARRANTY CUSTODY GUARD

- **Status:** **CONFIRMED**
- **Live Source Location:** `src/EdgeRetails.Application/Features/Sales/CommercialExchangeHandler.cs`
- **Root Cause:**
  1. `CommercialExchangeHandler` has **ZERO warranty checks**. `IWarrantyRepository` is not injected.
  2. Line 928 checks only `units.Any(x => x.Status != InventoryUnitStatus.Sold)`. When an inventory unit is under active warranty claim, its status remains `InventoryUnitStatus.Sold` (custody is tracked in `WarrantyClaim`). Thus, units currently undergoing repair, with supplier, or ready for customer pickup can be commercially exchanged for store credit!
  3. Non-serialized returns fail to deduct active/terminal warranty quantities.
  4. Missing advisory locks on `"warranty-sale-item"` and `"warranty-unit"`, creating concurrency races with `CreateWarrantyClaimHandler`.
- **Step 2 Correction:**
  - Inject `IWarrantyRepository? warranty = null`.
  - Acquire `"warranty-sale-item"` and `"warranty-unit"` resource locks.
  - In `PrepareSerializedReturnUnitsAsync`:
    - Call `_warranty.HasActiveClaimForUnitAsync` -> fail closed with `sales.return_unit_active_warranty`.
    - Call `_warranty.IsUnitTerminallyResolvedAsync` -> fail closed with `sales.return_unit_warranty_resolved`.
  - In non-serialized return lines:
    - Deduct `activeWarrantyQty` and `terminallyRemovedQty` from remaining returnable quantity.

---

## 11. F12 DETAILED AUDIT — NET PROFIT REPORTING OMITS RECOGNIZED INVENTORY LOSS

- **Status:** **CONFIRMED**
- **Live Source Location:** `src/EdgeRetails.Infrastructure/Services/BusinessOperationsReadServices.cs` (lines 388–390, 558–561)
- **Root Cause:**
  `ReportingReadService.GetSnapshotAsync` computes `var netProfit = grossProfit - expenseTotal;` and `BuildTrendAsync` computes `var profit = grossProfit - expenseAmount;`. It NEVER queries `_db.InventoryMovements` for `RecognizedLossAmount`.
- **Recognized Loss Sources:**
  1. Condition transfer to scrap (`InventoryMovementType.WriteOffToScrap`, 16)
  2. Stocktake shortage (`InventoryMovementType.PhysicalCountCorrection`, 17)
  3. Physical count reduction adjustment (`InventoryMovementType.StockAdjustment`, 8)
  4. Sale return disposed directly to scrap (`InventoryMovementType.SaleReturn`, 4)
  5. Warranty credit resolution shortfall (`InventoryMovementType.WarrantyCreditResolution`, 19)
- **Double-Counting Proof:**
  - COGS sums only `SaleItems.TotalCostSnapshot`. Scrap/shrinkage never create SaleItems.
  - Expenses sums only `Expenses.Amount` (posted cash/bank vouchers).
  - Balance sheet `ProductCostState` tracks asset carrying value, never P&L.
  - For scrapped returns, `CostReversalAmount` reverses COGS, and `RecognizedLossAmount` records the loss. If `RecognizedLossAmount` is omitted, the loss disappears!
  - **Verdict:** Deducting `RecognizedLossAmount` from Net Profit causes **ZERO double counting** and fixes an actual profit overstatement.
- **Phase 8 F13 Isolation:**
  - Queries `OccurredAt >= start && OccurredAt < end` using the identical UTC slice as `Sales.CompletedAt`. Introduces zero timezone coupling.
- **Step 2 Correction:**
  - In `BusinessOperationsReadServices.cs`: Query `_db.InventoryMovements.Where(x => x.OccurredAt >= start && x.OccurredAt < end && x.RecognizedLossAmount > 0m).SumAsync(x => (decimal?)x.RecognizedLossAmount) ?? 0m`.
  - Update formula: `var netProfit = grossProfit - expenseTotal - inventoryLossTotal;`.
  - Update trend calculation to deduct periodic recognized inventory loss.

---

## 12. F14 DETAILED AUDIT — APPEND-ONLY PROTECTION AT EF / CHANGETRACKER BOUNDARY

- **Status:** **CONFIRMED**
- **Live Source Location:** `src/EdgeRetails.Infrastructure/Persistence/EdgeRetailsDbContext.cs` (lines 167–175)
- **Root Cause:**
  `EnforceAppendOnlyAudit()` currently checks only `ChangeTracker.Entries<BusinessAuditEvent>()`. It omits `SupplierAccountEntry` and `CashMovement`. Any application bug can issue `UPDATE` or `DELETE` on financial subledgers through EF Core.
  In addition, `ChangeTracker.DetectChanges()` is not called before inspecting entry states.
- **Scope Limit:**
  - Strictly ORM-level ChangeTracker protection. Direct SQL and bulk LINQ operations (`ExecuteDeleteAsync`) bypass ChangeTracker by design and belong to Phase 11.
- **Reversal Semantics Proof:**
  - Legitimate business reversals (`ReverseSupplierPaymentHandler`, `ReverseSupplierRefundHandler`, `VoidPurchaseHandler`) mutate operation record status (`SupplierPayment.Status = Reversed`) and insert brand-new compensating entries (`EntityState.Added`). They NEVER update or delete historical entries.
  - Guarding `SupplierAccountEntry` and `CashMovement` against `Modified` and `Deleted` states is **100% non-breaking**.
- **Step 2 Correction:**
  - Update `EnforceAppendOnlyAudit()` in `EdgeRetailsDbContext.cs`:
    - Call `ChangeTracker.DetectChanges();` at method entry.
    - Inspect all entries with `State is EntityState.Modified or EntityState.Deleted`.
    - Throw `InvalidOperationException` if entity is `BusinessAuditEvent`, `SupplierAccountEntry`, or `CashMovement`.

---

## 13. COMMERCIALEXCHANGE TRANSACTION MAP

The entire commercial exchange operates inside a single atomic database transaction:
```text
[Transaction Start: _transactions.ExecuteAsync]
  1. Authorize Cashier (PermissionKeys.SalesCreate)
  2. Acquire Operation Lock (ClientOperationId)
  3. Check Existing Sale & Return (Idempotency) -> Verify payload match
  4. Lock Original Sale (SELECT FOR UPDATE)
  5. Acquire Product & InventoryUnit Advisory Locks
  6. Acquire Warranty Resource Locks ("warranty-sale-item", "warranty-unit")
  7. Prepare Return Leg:
     - Verify return lines belong to sale
     - Check counting stocktake locks
     - Lock stock balances (FOR UPDATE)
     - Validate serialized units: Status == Sold, NOT previously returned
     - VALIDATE WARRANTY CUSTODY: No active claim, NOT terminally resolved (F10)
     - Create InventoryMovements (SaleReturn) & update stock balance buckets
     - Restore lots (AddCarryingValueAndLotWithIdAsync)
     - Create SaleReturn, SaleReturnItem, SaleReturnItemUnit
  8. Prepare Replacement Sale Leg:
     - Lock replacement products & stock balances
     - Validate stock availability (SellableQty >= requested)
     - Allocate invoice discount
     - Deduct lots & carrying value
     - Create Sale, SaleItem, SaleItemUnit
  9. Settle Net Difference:
     - If Cash: Lock CashSession (FOR UPDATE)
     - If Customer pays difference: Record CashMovement (SaleCashIn) & SalePayment
     - If Shop refunds difference: Record CashMovement (SaleRefundCashOut) & SalePayment
 10. Record Business Audit Event
 11. Record Succeeded Outcome in IOperationOutcomeLedger (F06)
 12. Save Changes (await _unitOfWork.SaveChangesAsync)
[Transaction Commit]
```
**Partial Commit Risk:** ZERO. If any step fails, the entire transaction rolls back cleanly.

---

## 14. WARRANTY ELIGIBILITY MATRIX

| Claim Status | Current Custody | Resolution Type | Unit Status | Active Claim? | Terminally Resolved? | Commercial Exchange Allowed? | Result Error Code |
|---|---|---|---|---|---|---|---|
| *No Claim* | `WithCustomer` | N/A | `Sold` | No | No | **ALLOWED** | Success |
| `Received` | `WithShop` | None | `Sold` | **Yes** | No | **BLOCKED** | `sales.return_unit_active_warranty` |
| `UnderReview` | `WithShop` | None | `Sold` | **Yes** | No | **BLOCKED** | `sales.return_unit_active_warranty` |
| `SentToSupplier` | `WithSupplier` | None | `Sold` | **Yes** | No | **BLOCKED** | `sales.return_unit_active_warranty` |
| `SupplierProcessing` | `WithSupplier` | None | `Sold` | **Yes** | No | **BLOCKED** | `sales.return_unit_active_warranty` |
| `ReadyForCustomer` | `WithShop` | Repaired/Replaced/Rejected | `Sold` | **Yes** | No | **BLOCKED** | `sales.return_unit_active_warranty` |
| `Closed` | `WithCustomer` | `Replaced` | `Sold` | No | **Yes** | **BLOCKED** | `sales.return_unit_warranty_resolved` |
| `Closed` | `WithCustomer` | `Refunded` | `Sold` | No | **Yes** | **BLOCKED** | `sales.return_unit_warranty_resolved` |
| `Closed` | `WithCustomer` | `Repaired` | `Sold` | No | No | **ALLOWED** | Success |
| `Closed` | `WithCustomer` | `Rejected` | `Sold` | No | No | **ALLOWED** | Success |
| `Cancelled` | `WithCustomer` | None | `Sold` | No | No | **ALLOWED** | Success |
| **Non-Serialized** | N/A | N/A | N/A | Qty Active | Qty Terminal | **CONDITIONAL** | Allowed up to $\text{BaseQuantity} - \text{Returned} - (\text{Active} + \text{Terminal})$. Excess is **BLOCKED** with `sales.return_unit_active_warranty`. |

---

## 15. DRAFT / QUOTATION PERSISTENCE MAP

- **POS Drafts:**
  - Table: `sales.pos_drafts` and `sales.pos_draft_items`.
  - `DraftId`: Primary Key (`uuid`).
  - `ClientOperationId`: Optional correlation ID (`uuid`).
  - `Status`: `Open (1)`, `Converted (2)`, `Cancelled (3)`. Drafts are never deleted.
  - `SelectedInventoryUnitId`: Foreign key to `inventory.inventory_units` with `OnDelete(DeleteBehavior.Restrict)`. Selection hint only; does NOT reserve stock.
- **Quotations:**
  - Table: `sales.quotations`, `sales.quotation_items`, and `sales.quotation_operations`.
  - `QuotationId`: Primary Key (`uuid`).
  - `Status`: `Draft (1)`, `Issued (2)`, `Expired (3)`, `Converted (4)`, `Cancelled (5)`.
  - `QuotationOperation`: Links `QuotationId`, `ClientOperationId`, `OperationType`, `OccurredAt`.

---

## 16. NET PROFIT EXACT FORMULA

$$\text{Net Sales} = \text{Gross Sales} - \text{Sales Refunds}$$
$$\text{Net COGS} = \text{COGS} - \text{Reversed COGS}$$
$$\text{Gross Profit} = \text{Net Sales} - \text{Net COGS}$$
$$\text{Net Profit} = \text{Gross Profit} - \text{Operating Expenses} - \text{Recognized Inventory Losses}$$

Where $\text{Recognized Inventory Losses} = \sum \text{InventoryMovement.RecognizedLossAmount}$ for all movements with $\text{OccurredAt} \in [\text{start}, \text{end})$ and $\text{RecognizedLossAmount} > 0$.

---

## 17. RECOGNIZED-LOSS SOURCE MAP

| Source Operation | Handler | Movement Type | Cost Source |
|---|---|---|---|
| **Condition Transfer to Scrap** | `InventoryConditionHandlers.cs` | `WriteOffToScrap` (16) | Derecognized carrying value removed from pool. |
| **Stocktake Shortage** | `StocktakeHandlers.cs` | `PhysicalCountCorrection` (17) | Cost pool value removed for missing units. |
| **Physical Count Reduction** | `StockAdjustmentHandlers.cs` | `StockAdjustment` (8) | Cost pool value removed on negative delta. |
| **Sale Return Scrapped** | `SaleReturnHandler.cs` / `CommercialExchangeHandler.cs` | `SaleReturn` (4) | Original item acquisition cost. |
| **Warranty Supplier Shortfall** | `WarrantyHandlers.cs` | `WarrantyCreditResolution` (19) | Unrecovered difference between unit cost and supplier credit. |

---

## 18. APPEND-ONLY ENTITY MATRIX

| Entity | Insert | Update | Delete | Mutable Fields | Reversal Mechanism |
|---|---|---|---|---|---|
| `SupplierAccountEntry` | **ALLOWED** | **FORBIDDEN** | **FORBIDDEN** | *None* | Compensating entry inserted (`PurchaseVoidReversal`, `SupplierPaymentReversal`, `AdjustmentIncrease`, etc.). |
| `CashMovement` | **ALLOWED** | **FORBIDDEN** | **FORBIDDEN** | *None* | Compensating cash movement inserted (`PurchaseVoidCashIn`, `SupplierPaymentReversalCashIn`, etc.). |
| `BusinessAuditEvent` | **ALLOWED** | **FORBIDDEN** | **FORBIDDEN** | *None* | Compensating audit event recorded. |
| `SupplierPayment` | **ALLOWED** | **ALLOWED** | **FORBIDDEN** | `Status` (`Posted` $\to$ `Reversed`) | `ReverseSupplierPaymentHandler` updates status and appends compensating entries. |
| `SupplierRefund` | **ALLOWED** | **ALLOWED** | **FORBIDDEN** | `Status` (`Posted` $\to$ `Reversed`) | `ReverseSupplierRefundHandler` updates status and appends compensating entries. |
| `CashSession` | **ALLOWED** | **ALLOWED** | **FORBIDDEN** | `Status`, `ExpectedClosingCash`, `CountedClosingCash`, `Difference`, `ClosedBy`, `ClosedAt` | `CloseCashSessionHandler` seals session. |
| `OperationOutcome` | **ALLOWED** | **ALLOWED** | **FORBIDDEN** | `Status`, `ResultEntityType`, `ResultEntityId`, `DocumentNumber`, `PayloadFingerprint`, etc. | Updated via `IOperationOutcomeLedger`. Succeeded state is immutable. |

---

## 19. SAVECHANGES OVERLOAD MATRIX

| SaveChanges Overload | ChangeTracker Guard Invoked? | Step 2 Protection Status |
|---|---|---|
| `public override int SaveChanges()` | Calls `SaveChanges(true)` $\to$ `EnforceAppendOnlyAudit()` | **PROTECTED** |
| `public override int SaveChanges(bool acceptAllChangesOnSuccess)` | Invokes `EnforceAppendOnlyAudit()` | **PROTECTED** |
| `public override Task<int> SaveChangesAsync(CancellationToken ct)` | Calls `SaveChangesAsync(true, ct)` $\to$ `EnforceAppendOnlyAudit()` | **PROTECTED** |
| `public override Task<int> SaveChangesAsync(bool accept, CancellationToken ct)` | Invokes `EnforceAppendOnlyAudit()` | **PROTECTED** |
| `Task<int> IUnitOfWork.SaveChangesAsync(CancellationToken ct)` | Routes to `SaveChangesAsync(ct)` | **PROTECTED** |

---

## 20. REPLAY MATRIX

| Operation | Immediate Retry During Processing | Retry After Successful Commit | Retry After Unknown Outcome | Retry With Different Payload | Retry With Different ID |
|---|---|---|---|---|---|
| **VoidPurchase** | Waits on `_operationLock`. | Returns cached `VoidPurchaseResult(WasExisting: true)`. | Recovers `Succeeded` outcome from ledger. | **Rejects** with `idempotency.payload_mismatch`. | Rejects with `purchasing.purchase_not_voidable`. |
| **CommercialExchange** | Waits on `_operationLock`. | Returns cached `CommercialExchangeResult(WasExisting: true)`. | Recovers `Succeeded` outcome from ledger. | **Rejects** with `idempotency.payload_mismatch`. | Evaluates against new operation ID. |
| **CompletePosDraft** | Waits on `pos-draft` / `_operationLock`. | Returns cached `CompleteSaleResult(WasExisting: true)`. | Recovers `Succeeded` sale outcome. | **Rejects** with `idempotency.payload_mismatch`. | Fails if draft already converted. |
| **CancelQuotation** | Serialized by transaction. | Returns success idempotently via entity status. | Recovers `QuotationOperation` record. | Evaluates cancellation note. | Rejects if quote already converted. |

---

## 21. TRANSACTION BOUNDARY MATRIX

| Operation | Transaction Runner | SaveChanges Count | Locks Acquired | Outcome Ledger Recorded | Partial Commit Risk |
|---|---|---|---|---|---|
| **VoidPurchase** | `_transactions.ExecuteAsync` | 1 | `_operationLock`, `product`, `supplier-account`, `inventory-unit` | **YES** (Step 2) | ZERO |
| **CommercialExchange** | `_transactions.ExecuteAsync` | 1 | `_operationLock`, `product`, `inventory-unit`, `"warranty-*"` | **YES** (Step 2) | ZERO |
| **SavePosDraft** | `_transactions.ExecuteAsync` | 2 | `_operationLock` | **YES** (`PosDraftSave`) | ZERO |
| **CompletePosDraft** | `_transactions.ExecuteAsync` | 2 | `pos-draft`, `_operationLock`, `product`, `inventory-unit` | **YES** (`Sale`) | ZERO |
| **CancelQuotation** | `_transactions.ExecuteAsync` | 1 | DB row lock on quotation | **YES** (`QuotationOperation`) | ZERO |

---

## 22. CONCURRENCY MATRIX

| Race Scenario | Contenders | Primary Synchronization | Secondary Synchronization | Expected Outcome |
|---|---|---|---|---|
| **Void vs Replay** | VoidPurchase vs Same ClientOpId | `_operationLock(ClientOpId)` | Unique index on `purchase_voids` | Exactly 1 void executed; retry returns cached result. |
| **Exchange vs Exchange** | Two exchanges for same serialized unit | `_resourceLock("inventory-unit")` | Unit status check (`Status == Sold`) | Winner succeeds; loser fails (`sales.return_serial_already_returned`). |
| **Exchange vs Warranty Claim**| Exchange vs Claim on same serialized unit | `_resourceLock("warranty-unit")` | `HasActiveClaimForUnitAsync` / unique index | Winner succeeds; loser fails (`sales.return_unit_active_warranty`). |
| **Draft Complete Race** | Two terminals completing same draft | `_resourceLock("pos-draft")` | Draft `Version` concurrency token | Winner converts draft; loser fails (`sales.draft_stale`). |
| **Append-Only Tamper Race** | Legitimate write vs rogue mutation | ChangeTracker guard | Npgsql transaction rollback | Mutation rejected with `InvalidOperationException`. |

---

## 23. DATABASE INVARIANT MATRIX

| Table / Entity | Invariant | Enforced by Database | Enforced by Application |
|---|---|---|---|
| `system.operation_outcomes` | Unique `client_operation_id` | **YES** (`ix_operation_outcomes_client_operation_id`) | **YES** (`IOperationOutcomeLedger`) |
| `purchasing.purchase_voids` | Unique `purchase_id` & `client_operation_id` | **YES** (`ix_purchase_voids_purchase_id`, `client_operation_id`) | **YES** (`VoidPurchaseHandler`) |
| `sales.sales` | Unique `client_operation_id` | **YES** (`ix_sales_client_operation_id`) | **YES** (`CommercialExchangeHandler`) |
| `sales.returns` | Unique `client_operation_id` | **YES** (`ix_returns_client_operation_id`) | **YES** (`CommercialExchangeHandler`) |
| `sales.quotation_operations` | Unique `client_operation_id` | **YES** (`ix_quotation_operations_client_operation_id`) | **YES** (Step 2 `QuotationHandlers`) |
| `warranty.claim_item_units` | Filtered unique `active_original_inventory_unit_id` | **YES** | **YES** (`WarrantyHandlers`) |
| `parties.supplier_account_entries` | Append-only immutability | **NO** (No DB trigger) | **YES** (Step 2 `EdgeRetailsDbContext`) |
| `finance.cash_movements` | Append-only immutability | **NO** (No DB trigger) | **YES** (Step 2 `EdgeRetailsDbContext`) |

---

## 24. PERMISSION MATRIX

| Operation | Handler | Required Permission Key | Enforcement Point |
|---|---|---|---|
| **VoidPurchase** | `VoidPurchaseHandler` | `PermissionKeys.PurchasingManage` | Handler line 88 & Controller |
| **CommercialExchange** | `CommercialExchangeHandler` | `PermissionKeys.SalesCreate` | Handler line 122 & Controller |
| **SavePosDraft (Create)** | `SavePosDraftHandler` | `PermissionKeys.SalesDraftCreate` | Handler line 97 & Controller |
| **SavePosDraft (Update)** | `SavePosDraftHandler` | `PermissionKeys.SalesDraftResume` | Handler line 98 & Controller |
| **CancelPosDraft** | `CancelPosDraftHandler` | `PermissionKeys.SalesDraftCancel` | Handler line 316 & Controller |
| **CompletePosDraft** | `CompletePosDraftHandler` | `PermissionKeys.SalesCreate` | `CompleteSaleHandler` & Controller |
| **CreateQuotation** | `CreateQuotationHandler` | `PermissionKeys.SalesCreate` | Handler line 68 & Controller |
| **CancelQuotation** | `CancelQuotationHandler` | `PermissionKeys.SalesCreate` | Handler line 536 & Controller |
| **Reports Summary** | `ReportingReadService` | `PermissionKeys.ReportsView` | Controller endpoint authorization |

---

## 25. AUDIT-EVENT MATRIX

| Operation | Action Code | Entity Type | Correlation Stamped |
|---|---|---|---|
| **VoidPurchase** | `PURCHASE_VOIDED` | `PURCHASE` | `command.ClientOperationId` |
| **CommercialExchange** | `COMMERCIAL_EXCHANGE_COMPLETED` | `COMMERCIAL_EXCHANGE` | `command.ClientOperationId` |
| **CancelPosDraft** | `DRAFT_CANCELLED` | `POS_DRAFT` | None (Step 2 stamps `ClientOperationId`) |
| **CreateQuotation** | `QUOTATION_CREATED` | `QUOTATION` | None (Step 2 stamps `ClientOperationId`) |
| **CancelQuotation** | `QUOTATION_CANCELLED` | `QUOTATION` | None (Step 2 stamps `ClientOperationId`) |

---

## 26. EXISTING TEST INVENTORY SUMMARY

- **Phase 7 Pass 1 Suites:** `Phase7Pass1IntegrityTests` (22 unit tests), `Phase7Pass1PurchasingPostgresTests`, `Phase7Pass1ExpensePostgresTests`, `Phase7Pass1ReturnPostgresTests` (23 PostgreSQL tests). All pass green.
- **Phase 7 Pass 2 Suites:** `Phase7Pass2IntegrityTests` (16 unit tests), `Phase7Pass2PostgresTests` (39 tests), `Phase7Pass2HostileConcurrencyPostgresTests` (7 concurrency tests), `Phase7Pass2HostileNumericPostgresTests` (48 tests), `Phase7Pass2ThakaReadContractPostgresTests` (5 tests). All pass green.
- **Tracking Suites:** 156 unit tests, 83 PostgreSQL tests. All pass green.

---

## 27. MISSING TEST INVENTORY (STEP 2 REQUIREMENTS)

1. **VoidPurchase:** Same-ID replay test asserting `IOperationOutcomeLedger` record, payload mismatch rejection test, failure recording test.
2. **CommercialExchange:** Same-ID replay test asserting `IOperationOutcomeLedger` record, payload mismatch rejection test, asymmetric existence conflict test.
3. **Exchange vs Warranty Custody:** Active claim rejection test (shop custody, supplier custody, ready for customer), terminal resolution rejection test, non-serialized quantity limit test.
4. **POS Draft & Quotation:** Complete draft response-loss replay test, empty `ClientOperationId` rejection test, quotation creation/cancellation replay tests.
5. **Net Profit:** Scrap loss deduction test, double-counting absence proof test, trend slice profit deduction test.
6. **Append-Only Guard:** ChangeTracker `Modified` and `Deleted` rejection tests for `SupplierAccountEntry` and `CashMovement` across all 4 `SaveChanges` overloads.

---

## 28. POSTGRESQL CERTIFICATION PLAN

The following real PostgreSQL integration tests MUST execute against an isolated disposable database:
1. `VoidPurchase_PostgresReplay_PersistsCanonicalOutcomeOnce`: Verifies `system.operation_outcomes` row creation on PostgreSQL.
2. `CommercialExchange_PostgresReplay_PersistsCanonicalOutcomeOnce`: Verifies `system.operation_outcomes` and stock deduction idempotency.
3. `CommercialExchange_Postgres_ActiveWarrantyClaim_FailsClosed`: Verifies real PostgreSQL rejection of unit under active claim.
4. `PosDraftComplete_PostgresReplay_RecoversCommittedSale`: Verifies draft conversion retry recovers original sale on real PostgreSQL.
5. `NetProfit_Postgres_DeductsRecognizedLossFromMovements`: Verifies SQL `SUM(recognized_loss_amount)` query behavior.
6. `Subledgers_Postgres_EnforcesAppendOnlyChangeTrackerGuard`: Verifies `InvalidOperationException` thrown before SQL execution.

---

## 29. FAILURE-INJECTION PLAN

1. **Mid-Stream Warranty Conflict:** Return cart with 1 clean item and 1 active-claim item. Assert complete transaction rollback; zero returns or sales staged.
2. **Crash After Sale Completion in Draft:** Inject fault after sale completes but before draft transitions to `Converted`. Assert draft remains `Open` and no orphaned sale commits.
3. **Pre-Commit Database Fault in Exchange:** Inject exception in `SaveChangesAsync`. Assert return, sale, cash, and stock mutations roll back atomically.
4. **Subledger Tamper Injection:** Attempt property edit on loaded `SupplierAccountEntry` prior to commit. Assert ChangeTracker guard trips and aborts transaction.

---

## 30. PASS 1 / PASS 2 NON-REGRESSION PLAN

Step 2 must rerun and keep green the entire protected baseline:
- **Pass 1:** `D-VOID-1`, `D-VOID-2`, `D-EXP-1`, `D-RET-1`, `P7-N01` (45 tests total).
- **Pass 2:** `F01`, `D-ADJ-1`, `F02`, `F03`, `F04`, `P7-N02`, `P7-N03`, Thaka read contract (115 tests total).
- **Full UnitTests:** 910 / 910 tests must remain green.

---

## 31. PHASE 12 BOUNDARY GUARD

- **`P12-H01` (Replay-Before-Validation Ordering):** Strictly Phase 12. Pass 3 performs replay checks locally within handler bodies and does NOT build pipeline interceptors.
- **`P12-H02` (Canonical Payload Fingerprint Uniformity):** Strictly Phase 12. Pass 3 uses existing local `OperationPayloadFingerprint.ComputeSha256` and does NOT build a universal `ICanonicalPayloadHasher` framework.

---

## 32. PHASE 8 BOUNDARY GUARD

- **`F13` (Shop Timezone / BusinessDate Cutoff):** Strictly Phase 8. Pass 3 F12 queries `InventoryMovements.OccurredAt` using existing UTC time slices, introducing zero timezone dependencies.

---

## 33. FINAL MIGRATION VERDICT

```text
================================================================================
FINAL MIGRATION VERDICT: NO_MIGRATION_REQUIRED
================================================================================
All necessary tables (system.operation_outcomes, sales.quotation_operations,
sales.pos_drafts, warranty.claims, inventory.movements) and columns (including
recognized_loss_amount) are already live and indexed in PostgreSQL.
Zero schema migrations are needed or authorized for Pass 3.
================================================================================
```

---

## 34. BUSINESS DECISIONS

```text
================================================================================
OPEN BUSINESS DECISIONS: NONE (0)
================================================================================
All domain rules (rejection of units under warranty, deduction of scrap from
net profit, append-only subledger immutability, and local replay semantics)
are mathematically and architecturally settled.
================================================================================
```

---

## 35. EXACT STEP 2 PRODUCTION FILE MAP

Only **FOUR (4)** production files will be modified in Step 2:
1. `src/EdgeRetails.Application/Features/Purchasing/VoidPurchaseHandler.cs` (F05)
2. `src/EdgeRetails.Application/Features/Sales/CommercialExchangeHandler.cs` (F06 & F10)
3. `src/EdgeRetails.Application/Features/Sales/PosDraftHandlers.cs` (F07)
4. `src/EdgeRetails.Infrastructure/Services/BusinessOperationsReadServices.cs` (F12)
5. `src/EdgeRetails.Infrastructure/Persistence/EdgeRetailsDbContext.cs` (F14)
*(Note: If Quotation handlers are wired for DI registration, `QuotationHandlers.cs` and `InfrastructureServiceCollectionExtensions.cs` will receive minor DI wiring).*

**Files Explicitly Protected (Must NOT Change):**
- All Domain entities (`PurchasingModels.cs`, `SalesModels.cs`, `InventoryModels.cs`, `WarrantyModels.cs`, `CashModels.cs`)
- All Tracking System source and tests (PUCA, `PhysicalUnitCreationAuthority.cs`)
- All EF Core migrations and snapshot files
- All Desktop UI ViewModels and Views
- All Admin Portal files

---

## 36. EXACT STEP 2 IMPLEMENTATION ORDER

The implementation must follow a dependency-ordered sequence:
1. **Phase 7 Pass 3 Wave 1 (Subledger & Reporting Foundation):**
   - Step 1: Implement F14 (`EdgeRetailsDbContext.cs` ChangeTracker append-only guard for `SupplierAccountEntry` and `CashMovement`).
   - Step 2: Implement F12 (`BusinessOperationsReadServices.cs` deduct `RecognizedLossAmount` from Net Profit).
2. **Phase 7 Pass 3 Wave 2 (Warranty & Commercial Exchange):**
   - Step 3: Implement F10 & F06 (`CommercialExchangeHandler.cs` inject `IWarrantyRepository` and `IOperationOutcomeLedger`, add warranty custody guards, payload equality checks, and outcome recording).
3. **Phase 7 Pass 3 Wave 3 (Purchasing & POS Draft Replay):**
   - Step 4: Implement F05 (`VoidPurchaseHandler.cs` inject `IOperationOutcomeLedger`, add payload equality check and outcome recording).
   - Step 5: Implement F07 (`PosDraftHandlers.cs` fix `CompletePosDraftHandler` replay vulnerability, validate non-empty operation IDs).
4. **Phase 7 Pass 3 Wave 4 (Test & Certification):**
   - Step 6: Author unit tests for all 6 findings.
   - Step 7: Author real PostgreSQL integration tests.
   - Step 8: Execute full regression suites (Pass 1, Pass 2, Tracking, UnitTests, PostgreSQL).

---

## 37. REMAINING BLOCKERS

```text
CRITICAL BLOCKERS: 0
HIGH BLOCKERS:     0
SCHEMA BLOCKERS:   0
TECHNICAL BLOCKERS: 0
```
All six finding contracts are frozen and ready for implementation.

---

## 38. FINAL ADVERSARIAL VALIDATOR RESULT

The independent Final Adversarial Validator evaluated all six findings and concluded:
- F05 local replay contract: **VERIFIED & APPROVED**
- F06 CommercialExchange replay contract: **VERIFIED & APPROVED**
- F07 POS Draft & Quotation replay scope: **VERIFIED & APPROVED**
- F10 Active Warranty custody guard: **VERIFIED & APPROVED**
- F12 Net Profit recognized loss treatment: **VERIFIED & APPROVED**
- F14 ChangeTracker append-only guard: **VERIFIED & APPROVED**
- Migration verdict: `NO_MIGRATION_REQUIRED` **100% CONFIRMED**
- Business decisions: **NONE (0)**
- Step 2 contracts: **FROZEN & CERTIFIED READY**
