# STAGE 5: Canonical LF01–LF15 Crosswalk to Source Paths, Assertions, and Evidence

**Run ID:** `ER-RUN-LOCALFLOW-20261019-077`
**Register Source:** `governance/10-execution-runs/ER-RUN-LOCALPOS-DEEP-AUDIT-20261019-007/FINDINGS.json`
**Timestamp UTC:** `2026-10-19T12:00:00Z`

---

## Complete Finding Crosswalk

| Canonical ID | Source File(s) | Observable Assertion / Behavior | Run 077 Deposited Evidence | Status |
|---|---|---|---|---|
| **LF01** | `SalesReturnDialog.xaml`, `SalesReturnHandler.cs` | Disallows unbacked store credit; enforces operator choice same-project credit or authorized refund. | `run077_unit_tests.trx` | **PARTIALLY_REMEDIATED / OPEN** |
| **LF02** | `CompleteSaleHandler.cs:233`, `BackendTransactionService.cs:108` | Cart accepts distinct serialized units of same SKU on separate lines and with distinct price overrides. | `run077_unit_tests.trx` (4 named LF02 unit tests) | **CLOSED_AND_VERIFIED** |
| **LF03** | `SuppliersViewModel.cs:302-306` | Eliminates escaping exceptions from async void RelayCommands; clears matching action tokens (`payment:Settlement`, `payment:Advance`, `refund`, `payment-reversal:<id>`, `refund-reversal:<id>`). | `run077_desktop_performance_safety_tests.trx` (Targeted_J2) | **CLOSED_AND_VERIFIED** |
| **LF04** | `ExpensesViewModel.cs:190-235` | Category pre-POST validation rejection sends 0 POSTs, completes intent, unlocks editor (`CanEdit == true`). | `run077_desktop_performance_safety_tests.trx` (Targeted_J1 Part A) | **CLOSED_AND_VERIFIED** |
| **LF05** | `RemoteBackendBusinessOperationsService.cs:66` | Broad 409 conflict preserves pending intent while clearing proven no-commit business rejections. | `run077_desktop_performance_safety_tests.trx` (Targeted_J2 Part 4) | **CLOSED_AND_VERIFIED** |
| **LF06** | `SupplierAccountHandlers.cs:70` | Enum membership validated before financial state mutations. | `run077_unit_tests.trx` | **CLOSED_IN_CANDIDATE** |
| **LF07** | `CashSessionHandlers.cs:409`, `FinanceController.cs:163` | Explicit action permission predicate mapping for session close. | `run077_unit_tests.trx` | **CLOSED_IN_CANDIDATE** |
| **LF08** | `StockAdjustmentViewModel.cs:11-93` | Journaled operation ID recovered; payload mismatch leaves editor usable (`CanEdit == true`). | `run077_desktop_performance_safety_tests.trx` (Targeted_J3) | **CLOSED_AND_VERIFIED** |
| **LF09** | `PackPickerViewModel.cs:109`, `CableCutViewModel.cs:148` | Paging and traversal before availability decisions; 50-pack limit does not hide suitable 51st pack. | `run077_desktop_performance_safety_tests.trx` | **CLOSED_IN_CANDIDATE** |
| **LF10** | `RemoteStocktakeWorkflowService.cs:402` | Cursor pagination discovers units beyond 200; 50-page cap and cycle throw typed exception. | `run077_desktop_performance_safety_tests.trx` (Targeted_J5) | **CLOSED_AND_VERIFIED** |
| **LF11** | `WarrantyViewModel.cs:415-445` | Canonical sorting preserves unit fingerprint; permutation stable; next partial claim allowed after outcome reconciliation. | `run077_desktop_performance_safety_tests.trx` (Targeted_J4) | **CLOSED_AND_VERIFIED** |
| **LF12** | `StockAdjustmentViewModel.cs:86-121` | Definitive rejection leaves editor usable for correction without re-submitting invalid state. | `run077_desktop_performance_safety_tests.trx` (Targeted_J3 Part 1) | **CLOSED_AND_VERIFIED** |
| **LF13** | `BusinessOperationsReadServices.cs:218-227` | Net project balance accounting without cross-project netting. | `run077_unit_tests.trx` | **PARTIALLY_REMEDIATED / OPEN** |
| **LF14** | `BusinessOperationsReadServices.cs:404-507` | Reporting activity presentation for reversal-only projects. | Preserved without inventing policy | **OPEN** |
| **LF15** | `PostingController.cs:157`, `ShopPostingValidationService.cs:145` | Supervisor override permission alignment without weakening application authorization. | Preserved without weakening application authorization | **OPEN** |
