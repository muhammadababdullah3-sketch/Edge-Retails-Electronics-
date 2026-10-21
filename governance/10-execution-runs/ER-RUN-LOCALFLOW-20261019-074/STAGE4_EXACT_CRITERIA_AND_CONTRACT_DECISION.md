# STAGE 4: Exact Criteria & Contract Decisions

**Run ID:** `ER-RUN-LOCALFLOW-20261019-074`
**Authority:** `OWNER-UPDATE-20261018-019`
**Definitive Owner Policy Decision:** `OWNER-UPDATE-20261019-001`
**Corrective Review Mandate:** `continue1050/REVIEW.md`
**Timestamp UTC:** `2026-10-19T12:31:41.590273+00:00`

---

## 1. Corrective Seam Decisions (J1 through J6)

### 1.1 J1: Expense Pre-POST Validation Contract
- **Contract**: Rejections discovered *before* HTTP POST (category not found or not unique, payment method unsupported) prove no database mutation occurred. These throw `ExpensePrePostValidationException`.
- **ViewModel Catch Rule**: Catching `ExpensePrePostValidationException` safely resets `_submittedExpense = null`, completes the client operation intent in `_operationIntents`, and unlocks the UI. Downstream unconfirmed statuses (HTTP 5xx, timeouts, post-commit readback misses) throw generic `InvalidOperationException` or `DesktopApiException` which are NOT caught as no-commit, retaining intent and snapshot for idempotent retry.

### 1.2 J2: Supplier Action Lifecycle Release Contract
- **Contract**: When a supplier financial operation (Payment, Refund, Payment Reversal, Refund Reversal) is authoritatively rejected by backend business rules, only the attributable action state is released:
  - `_pendingSubmission = null`
  - Active reversal reason removed from `_paymentReversalReasons[paymentId]` or `_refundReversalReasons[refundId]`
  - If `_unresolvedFinancialAction` matches the current action, it is reset to `null`
  - `HasUnresolvedFinancialOperation` and `FinancialOperationStatus` are notified
  - Unrelated pending actions remain unaffected, and HTTP 409 or transport failures retain the locks.

### 1.3 J3: Stock Adjustment Mismatch Unlock Contract
- **Contract**: An unsubmitted local attempt in `StockAdjustmentViewModel` must NOT be promoted to `_submittedAdjustment` before verifying the existing intent store journal.
- If `GetOrCreate` throws `InvalidOperationException` due to payload mismatch, `_submittedAdjustment` remains `null`. The editor remains editable, allowing the operator to restore matching input values without clearing the pending journal.

### 1.4 J4: Warranty Claim Full Semantic Request Fingerprint Contract
- **Contract**: A warranty claim client operation intent must uniquely encode all semantic properties:
  - Format: `{customerId:D}|{productId:D}|{quantity}|{faultText}|{sortedUnitIds}`
  - Serialized unit IDs are sorted and joined to ensure deterministic hashing.
  - Swapping serialized units A and B on the same sale item produces distinct fingerprints, preventing silent collision and intent reuse.

### 1.5 J5: Exact Units Pagination Bounded Contract
- **Contract**: Paging through physical units via `LoadExactUnitsAsync` traverses up to 50 pages of 200 items (10,000 units total).
- If results exceed 50 pages and more units exist, it throws explicit `BackendOperationException("inventory.exact_units_page_limit_exceeded")`.
- If a full page returns zero newly observed units, it throws `BackendOperationException("inventory.exact_units_cursor_cycle")`.
- Replaces silent truncation with explicit error signalling.

### 1.6 J6: Preserved Wave Invariants
- LF01: Store credit removed from sales returns; Bank/Cash refund choice.
- LF02: Cart lines aggregated by `{ProductId, ProductUnitId}` with physical unit deduplication guard.
- LF05: Purchase return classifier adoption.
- LF06: Supplier payment purpose enum validation.
- LF07: Cash close route `SalesPosUse` authorization predicate.
- LF09: 51st pack container targeted paging.
- LF12: Main known rejection unfreeze.
- LF13: Customer directory balance calculation including cash credit refunds.
- LF14: Reporting queries include projects with reversals, calculating net material value.
- LF15: Strict supervisor posting preflight predicate requiring explicit permissions or ownership.
