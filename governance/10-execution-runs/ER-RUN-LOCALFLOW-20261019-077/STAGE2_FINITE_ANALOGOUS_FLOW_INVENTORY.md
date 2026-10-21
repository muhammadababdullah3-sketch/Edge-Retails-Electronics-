# STAGE 2: Finite Analogous Flow Inventory (Canonical LF01–LF15)

**Run ID:** `ER-RUN-LOCALFLOW-20261019-077`
**Authority:** `OWNER-UPDATE-20261019-007` & `OWNER-UPDATE-20261018-019`
**Register Source:** `governance/10-execution-runs/ER-RUN-LOCALPOS-DEEP-AUDIT-20261019-007/FINDINGS.json`

---

## 1. Full Audit Register of Canonical Findings LF01–LF15

| Finding ID | Severity | Title | Current Candidate Source Status | Run 077 Test Proof & Seam | Disposition |
|---|---|---|---|---|---|
| **LF01** | P1 | Sales Store Credit misleading | `Views/Dialogs/SalesReturnDialog.xaml`, `BackendTransactionService.cs`, `SalesReturnHandler.cs` enforce operator choice: same-project credit or authorized refund (`OWNER019`/`OWNER001`). | Unit tests pass. Server-side ledger effect requires disposable DB proof. | **PARTIALLY_REMEDIATED / OPEN** |
| **LF02** | P2 | Distinct serialized sameSKU checkout rejected | `CompleteSaleHandler.cs:233`, `BackendTransactionService.cs:108`. Cart accepts distinct serialized units of same SKU on separate lines and with distinct price overrides. | `run077_unit_tests.trx` (4 named LF02 unit tests: `LF02_DistinctSerializedUnits_SameSku_MultipleLines_Succeeds`, `LF02_DistinctSerializedUnits_SameSku_WithDifferentPriceOverrides_Succeeds`, `LF02_SameSerializedUnit_RepeatedAcrossLines_RejectedWithSerialSelectedTwice`, `LF02_CommodityProduct_DuplicateLines_RejectedWithDuplicateLine`). | **CLOSED_AND_VERIFIED** |
| **LF03** | P1 | Supplier financial conflict clears intent and escapes | `SuppliersViewModel.cs:302-306`. Accepted J2 causal fix: no `throw;` on deterministic rejections; exact action tokens tracked (`payment:Settlement`, `payment:Advance`, `refund`, `payment-reversal:<id>`, `refund-reversal:<id>`). | `run077_desktop_performance_safety_tests.trx` (Targeted_J2). | **CLOSED_AND_VERIFIED** |
| **LF04** | P2 | Expense editor frozen after local noPOST rejection | `ExpensesViewModel.cs:190-235`, `RemoteBackendBusinessOperationsService.cs:80`. Pre-POST validation failure clears intent and unlocks editor (`CanEdit = true`). | `run077_desktop_performance_safety_tests.trx` (Targeted_J1 Part A). | **CLOSED_AND_VERIFIED** |
| **LF05** | P1 | Related broad 409 intent cleanup | `RemoteBackendBusinessOperationsService.cs:66`, `PurchaseReturnViewModel.cs:280`. Preserves conflicting/ambiguous intent on 409 while clearing provable no-commit rejections. | `run077_desktop_performance_safety_tests.trx` (Targeted_J2 Part 4). | **CLOSED_AND_VERIFIED** |
| **LF06** | P1 | Undefined supplier purpose/method bypass branches | `SupplierAccountHandlers.cs:70,189`. Validates enum membership before executing state mutations. | Unit tests pass (`Phase2PurchasingAndKhataBehavioralTests`). | **CLOSED_IN_CANDIDATE** |
| **LF07** | P1 | Cash close lacks action permission | `FinanceController.cs:163`, `CashSessionHandlers.cs:409`. Maps action permission predicates explicitly. | Unit tests pass (`Phase2StocktakeThakaCashSessionBehavioralTests`). | **CLOSED_IN_CANDIDATE** |
| **LF08** | P1 | Stock adjustment retry ID lost on reopen | `StockAdjustmentViewModel.cs:11-93`. Journaled operation ID recovered; payload mismatch leaves editor usable. | `run077_desktop_performance_safety_tests.trx` (Targeted_J3). | **CLOSED_AND_VERIFIED** |
| **LF09** | P2 | Suitable pack hidden behind first 50 | `PackPickerViewModel.cs:109`, `CableCutViewModel.cs:148`. Paging and filtering before availability decision. | Desktop performance suite passes (409 tests). | **CLOSED_IN_CANDIDATE** |
| **LF10** | P2 | Exact unit browser truncates after 200 | `RemoteStocktakeWorkflowService.cs:402`, `InventoryController.cs:129`. Cursor-based paging across full inventory. | `run077_desktop_performance_safety_tests.trx` (Targeted_J5). | **CLOSED_AND_VERIFIED** |
| **LF11** | P1 | Quantity warranty retry loses identity on navigation | `WarrantyViewModel.cs:415-445`, `WarrantyHandlers.cs:716`. Canonical sorting preserves fingerprint; partial claims permitted after outcome reconciliation. | `run077_desktop_performance_safety_tests.trx` (Targeted_J4). | **CLOSED_AND_VERIFIED** |
| **LF12** | P2 | Stock adjustment definitive rejection locks correction | `StockAdjustmentViewModel.cs:86-121`. Rejection leaves editor usable for correction without re-submitting invalid state. | `run077_desktop_performance_safety_tests.trx` (Targeted_J3 Part 1). | **CLOSED_AND_VERIFIED** |
| **LF13** | P2 | Customer directory stale refund arithmetic | `BusinessOperationsReadServices.cs:218-227`. Reconciles net project balance without netting different projects. | Unit tests pass. Server ledger query requires disposable DB proof. | **PARTIALLY_REMEDIATED / OPEN** |
| **LF14** | P2 | Thaka reporting activity drops reversal-only project | `BusinessOperationsReadServices.cs:404-507`. Source policy inconsistency on signed vs separate return presentation. | Preserved without inventing unauthorized policy. | **OPEN** |
| **LF15** | P2 | Posting supervisor preflight disagrees with application | `PostingController.cs:157`, `ShopPostingValidationService.cs:145`. Permission predicate alignment without weakening application authorization. | Preserved without weakening application authorization. | **OPEN** |

---

## 2. Scope Accounting
- **Total Canonical Findings Evaluated:** 15 (LF01 through LF15)
- **Closed and Verified with Targeted Test Oracles:** 7 (LF02, LF03, LF04, LF05, LF08, LF10, LF11, LF12)
- **Closed in Candidate Source (Validated by Unit/Desktop Regression):** 3 (LF06, LF07, LF09)
- **Partially Remediated / Open (Server-Side DB Proof Required):** 2 (LF01, LF13)
- **Open (Source Inconsistency / Policy Authorization Required):** 2 (LF14, LF15)
