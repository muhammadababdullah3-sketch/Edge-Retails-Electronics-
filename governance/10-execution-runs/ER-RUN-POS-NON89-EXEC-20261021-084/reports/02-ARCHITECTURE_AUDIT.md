# STAGE 1: Reproduction of Root Findings and Execution of LF01 and LF13
**Run ID:** `ER-RUN-POS-NON89-EXEC-20261021-084`
**Authority:** `OWNER-UPDATE-20261018-019 / OWNER-UPDATE-20261019-001 / OWNER-UPDATE-20261019-007 / OWNER-UPDATE-20261020-023`
**Timestamp UTC:** `2026-10-21T12:00:00Z`

## 1. Finding LF01 Root Cause & Execution
- **Defect**: Retail returns allowed selecting or submitting unsupported refund methods (`RefundMethod.Other`) which pretended that a store credit liability ledger existed when no redeemable store credit system exists in retail POS.
- **Fix**:
  - `SaleReturnHandler.cs`: Added server-side validation ensuring `command.RefundMethod` is either `RefundMethod.Cash` or `RefundMethod.Bank`. Any other method returns failure `sales.unsupported_refund_method`.
  - `BackendTransactionService.cs`: Rejects "Store Credit" and "Other" strings in `MapRefundMethod`.
  - `DemoTransactionService.cs`: Validates `RefundMethod` in `RecordReturn`, rejecting store credit.
  - `SalesReturnViewModel.cs`: Validates refund method and blocks return processing when unapproved methods are selected.

## 2. Finding LF13 Root Cause & Execution
- **Defect**: Customer directory project summaries allowed negative balances on individual projects (due to excess advance payments or returns) to cross-project net against other projects, masking outstanding liabilities.
- **Fix**:
  - `DemoBusinessDirectoryService.cs`: Clamped per-project balance to `Math.Max(0m, project.Balance)` in `RefreshCustomerMetrics`.
  - Verified parity with canonical `PartyDirectoryReadService.cs:417` (`Balance = Math.Max(0m, Issues - Reversals - Settlements - Payments + PaymentReversals + CreditRefunds)`).

## 3. THAKA-AUD-04 Conservation Interlock
- Confirmed that reversing an original payment that has already been refunded via credit refund fails closed with `thaka.payment_already_refunded`, preventing double discharge.
