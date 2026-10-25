# Phase 11 remediation — owner decisions (executed defaults)

**Run:** ER-RUN-POS-PHASE11-REMEDIATION-IMPLEMENTATION  
**Status:** Defaults applied for implementation; formal sign-off recorded here for D05 crosswalk.

## 1. Desktop commercial exchange (plan §3.2)

**Decision:** **Option A (minimal)** — `BackendTransactionService.ExecuteCommercialExchangeAsync` delegates to `IApplicationGateway` / `POST /api/sales/exchange`. Dedicated WPF exchange UI remains a follow-up; API path is certified.

## 2. Thaka credit refund posting type (plan §2.2)

**Decision:** Extend `PostingOperationTypes` with **`ThakaCreditRefund`** and validate in `RefundThakaCreditHandler` (fail closed on locked periods).

## 3. Demo / preview mode (plan §3.3)

**Decision:** **Fail closed** for pack/container/provenance returns in `DemoTransactionService` (no backend).
