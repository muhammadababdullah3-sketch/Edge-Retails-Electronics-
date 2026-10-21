# STAGE 1: Confirmed Audit Findings & Causal Diagnosis

**Run ID:** `ER-RUN-LOCALFLOW-20261019-074`
**Governing Authority:** `OWNER-UPDATE-20261018-019`
**Definitive Owner Policy Decision:** `OWNER-UPDATE-20261019-001`
**Corrective Review Mandate:** `continue1050/REVIEW.md`
**Sustained Lineage:** `17Oct007`
**Active Session:** `session-e143b7ec-1d5c-4ad8-ad06-4bbe1dd20736`
**Predecessor Runs:** `ER-RUN-LOCALFLOW-20261019-073`, `ER-RUN-LOCALFLOW-20261018-072`, `ER-RUN-MON-W4-20261018-044`
**Timestamp UTC:** `2026-10-19T12:31:41.590273+00:00`
**Timestamp Local (PKT):** `2026-10-19T14:45:00+05:00`

---

## 1. Executive Summary & Root Mandate

Under active governance authority `OWNER-UPDATE-20261018-019`, human owner policy decision `OWNER-UPDATE-20261019-001`, and the corrective review prompt `continue1050/REVIEW.md`, Run 074 executes the authoritative master corrective join. It preserves all completed source fixes (LF01..LF15) and definitively closes the remaining state/proof seams (J1..J6) across the single-shop, single-machine POS boundary.

Run 074 accomplishes:
1. **J1 (LF04 Causal Separation)**: Distinguishes true pre-POST typed validation rejections (`ExpensePrePostValidationException`) from downstream unknown outcomes or post-commit readback misses.
2. **J2 (LF03 Attributable State Release)**: Cleans up attributable rejected action state (`_pendingSubmission = null`, reversal reason dictionary, `_unresolvedFinancialAction`) in `SupplierDetailViewModel` on proven no-commit business rejections without clearing unrelated actions or losing intent on 409/transport failures.
3. **J3 (LF08 Attempt Unlock)**: Prevents `StockAdjustmentViewModel` from freezing local unsubmitted attempts on local `GetOrCreate` payload mismatch, allowing correction back to original without clearing the old journal.
4. **J4 (LF11 Full Semantic Request Fingerprint)**: Expands canonical fingerprint in `WarrantyViewModel` to include the full semantic request (customerId, productId, quantity, fault, sorted exact unit IDs) preventing silent fingerprint reuse when swapping units.
5. **J5 (LF10 Explicit Paging Cap)**: Replaces silent truncation with explicit page cap (50 pages / 10,000 units) and cursor cycle detection in `RemoteStocktakeWorkflowService`.
6. **J6 (Whole Wave Acceptance)**: Preserves original boundaries and proof dispositions with 100% test pass proof under disposable PostgreSQL 18 custody.

---

## 2. Detailed Audit Findings & Causal Resolution

### 2.1 J1 (LF04): Expense Pre-POST Validation vs Downstream Ambiguous Outcomes
- **Defect**: In `ExpensesViewModel.cs`, line 224 caught generic `InvalidOperationException` as a proven no-commit event, clearing `_submittedExpense` and completing the intent store entry. However, `RemoteBackendBusinessOperationsService.cs` threw `InvalidOperationException` for unknown server outcomes (HTTP 5xx / timeout) and post-commit readback misses. On a transient readback miss, retrying would generate a new client operation ID and post a duplicate expense.
- **Root Cause**: Overloaded use of generic `InvalidOperationException` across pre-mutation validation and post-mutation recovery.
- **Resolution**: Defined a specialized typed exception `ExpensePrePostValidationException : InvalidOperationException`. Pre-POST category and payment method validation in `RemoteBackendBusinessOperationsService.cs` throws `ExpensePrePostValidationException`. `ExpensesViewModel.cs` catches only `ExpensePrePostValidationException` and authoritative business rejections as no-commit, preserving snapshot and intent on downstream ambiguous failures.

### 2.2 J2 (LF03): Supplier Action State Lifecycle Release
- **Defect**: When `SupplierDetailViewModel` caught a deterministic business rejection, `_operationIntents.Complete` was called, but `_pendingSubmission`, `_unresolvedFinancialAction`, and the reversal reason dictionary remained populated. The UI remained frozen in a submitting state.
- **Root Cause**: Missing attributable action cleanup in the ViewModel exception catch block.
- **Resolution**: In `PostPaymentAsync`, `ReceiveRefundAsync`, `ReversePaymentAsync`, and `ReverseRefundAsync`, on deterministic business rejection, cleared attributable action state (`_pendingSubmission = null`, removed entry from `_paymentReversalReasons` / `_refundReversalReasons`, reset `_unresolvedFinancialAction` if matching, and updated `HasUnresolvedFinancialOperation` / `FinancialOperationStatus`).

### 2.3 J3 (LF08): Stock Adjustment Payload Mismatch Attempt Unlock
- **Defect**: In `StockAdjustmentViewModel.cs`, `_submittedAdjustment = submitted` was set *before* calling `_operationIntents.GetOrCreate(operationKey, canonicalPayload)`. If `GetOrCreate` threw `InvalidOperationException` due to an existing pending intent with a different payload, `_submittedAdjustment` remained set, freezing the dialog and preventing input correction.
- **Root Cause**: Premature assignment of unsubmitted local attempt to active submission state.
- **Resolution**: Deferred setting `_submittedAdjustment` until after `GetOrCreate` succeeds. On payload mismatch `InvalidOperationException`, the editor remains unlocked so the operator can restore values matching the original pending intent.

### 2.4 J4 (LF11): Warranty Claim Semantic Request Fingerprint
- **Defect**: In `WarrantyViewModel.cs`, canonical payload was computed as `{customerId:D}|{quantity}|{ClaimIntakeFaultText?.Trim()}`, omitting `productId` and serialized unit IDs. If an operator submitted a claim for Unit A, encountered a network timeout, and subsequently changed the selection to Unit B on the same sale item, the identical fingerprint would cause silent reuse of Unit A's operation ID.
- **Root Cause**: Incomplete canonical payload representation.
- **Resolution**: Expanded canonical payload to: `{customerId:D}|{SelectedIntakeRow.ProductId:D}|{quantity}|{ClaimIntakeFaultText?.Trim()}|{unitPayload}`, where `unitPayload` is the comma-separated sorted list of inventory unit IDs.

### 2.5 J5 (LF10): Exact Units Cursor Pagination Explicit Cap & Cycle Detection
- **Defect**: In `RemoteStocktakeWorkflowService.cs`, `LoadExactUnitsAsync` exited the pagination loop after 50 pages or if a page returned 0 new items, silently returning a truncated list.
- **Root Cause**: Silent termination without signalling truncation.
- **Resolution**: Replaced silent break with explicit exceptions: throws `inventory.exact_units_page_limit_exceeded` if results exceed 50 pages (10,000 units), and throws `inventory.exact_units_cursor_cycle` if a full page yields 0 new items.
