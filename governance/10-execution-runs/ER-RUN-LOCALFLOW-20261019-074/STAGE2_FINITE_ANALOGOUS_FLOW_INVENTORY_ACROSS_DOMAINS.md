# STAGE 2: Finite Analogous Flow Inventory Across Domains

**Run ID:** `ER-RUN-LOCALFLOW-20261019-074`
**Authority:** `OWNER-UPDATE-20261018-019`
**Timestamp UTC:** `2026-10-19T12:31:41.590273+00:00`
**Coordination Authority:** Autonomous Lead Coordinator / Orchestrator (`session-e143b7ec-1d5c-4ad8-ad06-4bbe1dd20736`)

---

## 1. Objective & Governance Mandate
Under `OWNER019-C4` and the corrective mandate in `continue1050/REVIEW.md`, this document establishes an exhaustive, source-verified inventory of all analogous local-flow operations across EdgeRetails business domains (Catalog, Inventory, Sales, Finance, Purchasing, Thaka).

Every route, HTTP method, and CQRS handler class is strictly verified against active ASP.NET Core controller source code (`src/EdgeRetails.Server/Controllers/`) and Application handler definitions (`src/EdgeRetails.Application/Features/`).

---

## 2. Source-Verified Domain Flow Matrix

| Domain | Action / Local Flow | HTTP Endpoint | Verified Handler Class | Controller Source | Cash Drawer Movement | Idempotency / Outcome Ledger | Status |
|---|---|---|---|---|---|---|---|
| **Thaka** | Issue Material | `POST /api/thaka/material-issue` | `IssueThakaMaterialHandler` | `ThakaController.cs` | None (Project Khata) | `ClientOperationId` in `IOperationOutcomeLedger` | Verified |
| **Thaka** | Reverse Material | `POST /api/thaka/material-reversal` | `ReverseThakaMaterialHandler` | `ThakaController.cs` | Operator choice: same-project credit retention or authorized refund (`OWNER019-C1/C2`) | `ClientOperationId` in `IOperationOutcomeLedger` | Verified |
| **Thaka** | Record Payment | `POST /api/thaka/payments` | `RecordThakaPaymentHandler` | `ThakaController.cs` | `ThakaPaymentCashIn` (Direction `In`) | `ClientOperationId` in `IOperationOutcomeLedger` | Verified |
| **Thaka** | Reverse Payment | `POST /api/thaka/payment-reversal` | `ReverseThakaPaymentHandler` | `ThakaController.cs` | `ThakaPaymentReversalCashOut` (Direction `Out`, guarded by `NetPaymentsCollected >= Amount`) | `ClientOperationId` in `IOperationOutcomeLedger` | Verified (`OWNER019-C2`) |
| **Thaka** | Refund Credit | `POST /api/thaka/credit-refund` | `RefundThakaCreditHandler` | `ThakaController.cs` | `ThakaCreditRefundCashOut` (Direction `Out`, requires active cash session) | `ClientOperationId` in `IOperationOutcomeLedger` | Verified (`OWNER019-C2`) |
| **Thaka** | Final Settlement | `POST /api/thaka/settlement` | `SettleThakaHandler` | `ThakaController.cs` | `ThakaSettlementCashIn` (if net balance > 0; fails if credit exists) | `ClientOperationId` in `IOperationOutcomeLedger` | Verified (`THAKA-AUD-01`) |
| **Thaka** | Reopen Project | `POST /api/thaka/projects/{id:guid}/reopen` | `ReopenThakaHandler` | `ThakaController.cs` | None | `ClientOperationId` in `IOperationOutcomeLedger` | Verified (`THAKA-AUD-02`) |
| **Thaka** | Set Suspension | `POST /api/thaka/projects/{id:guid}/suspension` | `SetThakaSuspensionHandler` | `ThakaController.cs` | None | `ClientOperationId` in `IOperationOutcomeLedger` | Verified |
| **Finance** | Open Cash Session | `POST /api/finance/cash-session/open` | `OpenCashSessionHandler` | `FinanceController.cs` | Drawer Opening Float | `ClientOperationId` in `IOperationOutcomeLedger` | Verified |
| **Finance** | Close Cash Session | `POST /api/finance/cash-session/close` | `CloseCashSessionHandler` | `FinanceController.cs` | Drawer Closing Count & Variance Calculation | `ClientOperationId` in `IOperationOutcomeLedger` | Verified (`OWNER019-C4`, `LF07`) |
| **Finance** | Record Expense | `POST /api/expenses` | `PostExpenseHandler` | `ExpensesController.cs` | `ExpenseCashOut` (Direction `Out`) | `ClientOperationId` in `IOperationOutcomeLedger` | Verified (`J1`, `LF04`) |
| **Finance** | Void Expense | `POST /api/expenses/{id:guid}/void` | `VoidExpenseHandler` | `ExpensesController.cs` | Compensating Cash Adjustment | `ClientOperationId` in `IOperationOutcomeLedger` | Verified |
| **Finance** | Supplier Payment | `POST /api/finance/supplier-payment` | `CreateSupplierPaymentHandler` | `FinanceController.cs` | `SupplierPaymentCashOut` (Direction `Out`) | `ClientOperationId` in `IOperationOutcomeLedger` | Verified (`J2`, `LF03`) |
| **Finance** | Supplier Refund | `POST /api/finance/supplier-refund` | `CreateSupplierRefundHandler` | `FinanceController.cs` | `SupplierRefundCashIn` (Direction `In`) | `ClientOperationId` in `IOperationOutcomeLedger` | Verified (`J2`, `LF03`) |
| **Sales** | Complete Sale | `POST /api/sales/complete` | `CompleteSaleHandler` | `SalesController.cs` | `SaleCashIn` (Direction `In`) | `ClientOperationId` in `IOperationOutcomeLedger` | Verified |
| **Sales** | Complete POS Draft | `POST /api/sales/drafts/complete` | `CompletePosDraftHandler` | `SalesController.cs` | `SaleCashIn` (Direction `In`) | `ClientOperationId` in `IOperationOutcomeLedger` | Verified |
| **Sales** | Commercial Return | `POST /api/sales/returns` / `POST /api/sales/return` | `CreateSaleReturnHandler` | `SalesController.cs` | `SaleReturnCashOut` (Direction `Out`) | `ClientOperationId` in `IOperationOutcomeLedger` | Verified (`LF01`) |
| **Sales** | Commercial Exchange | `POST /api/sales/commercial-exchange` | `CommercialExchangeHandler` | `SalesController.cs` | Net Inflow or Outflow depending on balance | `ClientOperationId` in `IOperationOutcomeLedger` | Verified |
| **Sales** | Warranty Claim | `POST /api/warranty/claims` | `CreateWarrantyClaimHandler` | `WarrantyController.cs` | None | `ClientOperationId` in `IOperationOutcomeLedger` | Verified (`J4`, `LF11`) |
| **Purchasing** | Intake Goods | `POST /api/purchasing/intake` | `ReceiveProductIntakeHandler` | `PurchasingController.cs` | None (Supplier Khata Liability) | `ClientOperationId` in `IOperationOutcomeLedger` | Verified |
| **Purchasing** | Purchase Return | `POST /api/purchasing/return` | `CreatePurchaseReturnHandler` | `PurchasingController.cs` | Supplier Account Credit Debit | `ClientOperationId` in `IOperationOutcomeLedger` | Verified (`LF05`) |
| **Purchasing** | Create Purchase | `POST /api/purchasing/create` | `CreatePurchaseHandler` | `PurchasingController.cs` | None (Accounts Payable Ledger) | `ClientOperationId` in `IOperationOutcomeLedger` | Verified |
| **Purchasing** | Void Purchase | `POST /api/purchasing/void` | `VoidPurchaseHandler` | `PurchasingController.cs` | Inventory & Payable Reversal | `ClientOperationId` in `IOperationOutcomeLedger` | Verified |
| **Inventory** | Commit Stocktake | `POST /api/inventory/stocktake/commit` | `CommitStocktakeHandler` | `InventoryController.cs` | None | `ClientOperationId` in `IOperationOutcomeLedger` | Verified |
| **Inventory** | Stock Adjustment | `POST /api/inventory/adjustments` | `CreateStockAdjustmentHandler` | `InventoryController.cs` | None | `ClientOperationId` in `IOperationOutcomeLedger` | Verified (`J3`, `LF08`) |
| **Inventory** | Exact Units Paging | `GET /api/inventory/exact-units` | `GetExactInventoryUnitsQueryHandler` | `InventoryController.cs` | None | Read Cursor Query | Verified (`J5`, `LF10`) |

---

## 3. Structural & Invariant Dispositions
1. **Cash Drawer Outflow Invariant**:
   All operations that disburse cash (`RefundThakaCreditHandler`, `ReverseThakaPaymentHandler`, `CreateSaleReturnHandler`, `PostExpenseHandler`, `CreateSupplierPaymentHandler`) require an active cash drawer session via `ICashRepository.GetOpenSessionForUpdateAsync`. In the absence of an open session, operations fail closed with `"cash.session_required"`.
