# WORKER RESULT: Lane A — Thaka Lifecycle, Payments, Reopening, Reversal & Credit Refund

- **Worker ID**: `worker-lf-074-a`
- **Run ID**: `ER-RUN-LOCALFLOW-20261019-074`
- **Directives Covered**: `OWNER019-C1` (Credit Retention / Projections & Operator Choice), `OWNER019-C2` (Authorized Credit Refund & Cash Outflow), `THAKA-AUD-01` through `THAKA-AUD-17`.
- **Status**: COMPLETE & VERIFIED (100% Pass)

## 1. Scope & Execution Summary
Lane A executes the core financial and customer sub-ledger logic governing Thaka custom tailoring projects. Building directly upon predecessor Run 073, Lane A preserves the definitive human owner policy decision preserved in `governance/00-source/later-owner-updates/OWNER-UPDATE-20261019-001.md`:
*"Operator ko choice: isi project mein credit rakhe ya authorized refund de (recommended)"*.

## 2. Technical Implementations
1. **Operator Choice — Same-Project Credit Retention (`OWNER019-C1`)**:
   - Negative net balance (`MaterialValue - Paid < 0`) is preserved on the project as a signed liability.
   - Moves zero cash out of the drawer at the moment of material reversal.
   - Persists across restarts, shutdowns, and crashes.
   - Credit is automatically consumed when subsequent materials are issued to the same project.
   - Attempting final settlement while in credit fails closed (`thaka.project_has_credit_balance`).
   - Verified in behavioral test `THAKA_AUD_05`.

2. **Operator Choice — Authorized Full/Partial Credit Refund (`OWNER019-C2`)**:
   - Implemented `RefundThakaCreditHandler.cs` with atomic operation outcome ledger idempotency (`IClientOperationIntentStore`).
   - Dispatches compensating cash drawer outflow `CashMovementType.ThakaCreditRefundCashOut` (Direction `Out`).
   - Validates that available credit exists and requested refund amount <= available credit.
   - Requires authorization and explicit reason.
   - Reduces customer credit toward zero, restoring project balance for zero-balance settlement.
   - Disallows payment-reversal substitution.
   - Enforces sequential conservation invariant in `ReverseThakaPaymentHandler.cs`: `GrossPaid - GrossPaymentReversals >= GrossCreditRefunds`. Reverses fail closed with `thaka.payment_already_refunded` if funds were already discharged via credit refunds.
   - Verified in behavioral tests `THAKA_AUD_06`, `THAKA_AUD_07`, `THAKA_AUD_08`, `THAKA_AUD_09`, `THAKA_AUD_12`, `THAKA_AUD_13`, `THAKA_AUD_14`.

3. **Signed Balance Preservation**:
   - Eliminated artificial clamping across Dapper queries in `ThakaReadService.cs` (`GREATEST(0, ...)`) and `ThakaProjectListItemViewModel.cs` (`Math.Max(0m, ...)`).
   - Formatted credit balances as `(Rs. X) [Credit]`.
