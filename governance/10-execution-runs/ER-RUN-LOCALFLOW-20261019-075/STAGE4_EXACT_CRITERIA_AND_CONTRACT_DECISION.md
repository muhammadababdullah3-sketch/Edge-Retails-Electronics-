# STAGE 4: Exact Criteria & Contract Decisions

**Run ID:** `ER-RUN-LOCALFLOW-20261019-075`
**Authority:** `OWNER-UPDATE-20261018-019`
**Predecessor Review Mandate:** `run074-review/DRAFT_PROMPT.md`
**Timestamp UTC:** `2026-10-19T12:00:00Z`

---

## 1. Causal Repair & Targeted Oracles (J1 through J5)

### 1.1 J2: Supplier ViewModel Handled UI Rejection & Exact Action Token Contract
- **Root Cause**: Binds async lambdas to `RelayCommand` at UI entry. Deterministic rejection catches previously called `throw;`, causing unhandled exceptions to escape to the WPF dispatcher. In addition, `BeginFinancialSubmission("payment:" + purpose)` did not match cleanup `if (_unresolvedFinancialAction == "payment")`.
- **Contract**:
  1. Catch blocks for business rejections in `PostPaymentAsync`, `PostRefundAsync`, `ReversePaymentAsync`, and `ReverseRefundAsync` show warning toasts, release attributable submission state, clear matching `_unresolvedFinancialAction == actionToken`, and return cleanly without `throw;`.
  2. Transient errors (HTTP 409, 5xx, network timeouts) do NOT clear `_unresolvedFinancialAction` or pending intents, preserving retry capability.

### 1.2 J1: Expense Pre-POST Validation vs Committed Readback Retry Contract
- **Contract**:
  1. Pre-POST validation errors (stale category, invalid payment method) throw typed `ExpensePrePostValidationException`. The ViewModel catches this as a proven no-commit event, immediately completing the operation intent, resetting `_submittedExpense = null`, and unlocking the editor (`CanEdit == true`). Zero HTTP POST requests are sent.
  2. Committed POST followed by readback miss throws generic exception. Operation intent is retained. Follow-up retry sends an HTTP POST with the exact same `OperationId`, ensuring single cash effect.

### 1.3 J3: Stock Adjustment Mismatch Recovery Contract
- **Contract**:
  1. An unsubmitted attempt is NOT assigned to `_submittedAdjustment` prior to `GetOrCreate` intent verification.
  2. If a payload mismatch throws `InvalidOperationException`, `_submittedAdjustment` remains `null` and the editor remains unlocked (`CanEdit == true`).
  3. Restoring original input values allows `ApplyAsync` to proceed, reusing the original journaled operation ID.

### 1.4 J4: Warranty Claim Canonical Fingerprint & Lifecycle Contract
- **Contract**:
  1. Canonical fingerprint encodes sorted serialized physical unit IDs: `{customerId:D}|{productId:D}|{quantity}|{faultText}|{sortedUnitIds}`.
  2. Swapping serialized units A and B on the same sale item produces distinct fingerprints.
  3. Permuting unit selection order produces identical fingerprints (permutation stable).
  4. Subsequent partial claim for a remaining unit is permitted after reconciling prior submission outcome (not awaiting total lifecycle closure).

### 1.5 J5: Exact Units Cursor Pagination Completeness Contract
- **Contract**:
  1. Traversing exact units via `GetExactUnitsAsync` supports multi-page results (verified with 201 units across 2 pages).
  2. Exceeding the 50-page cap (10,000 units) throws `BackendOperationException("inventory.exact_units_page_limit_exceeded")`.
  3. Cursor cycle detection (full page with zero new units) throws `BackendOperationException("inventory.exact_units_cursor_cycle")`.
