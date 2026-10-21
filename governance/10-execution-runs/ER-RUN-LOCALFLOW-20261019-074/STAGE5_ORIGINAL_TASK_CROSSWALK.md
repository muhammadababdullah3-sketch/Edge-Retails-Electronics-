# STAGE 5: Original Task Crosswalk

**Run ID:** `ER-RUN-LOCALFLOW-20261019-074`
**Authority:** `OWNER-UPDATE-20261018-019`
**Corrective Mandate:** `continue1050/REVIEW.md`
**Timestamp UTC:** `2026-10-19T12:31:41.590273+00:00`

---

| Corrective Seam ID | Original Domain & Flow | Implementation File | Verification Proof | Disposition |
|---|---|---|---|---|
| **J1 (LF04)** | Finance: Expense pre-POST validation separation | `RemoteBackendBusinessOperationsService.cs`, `ExpensesViewModel.cs` | `Phase12Wave3DurableClientOwnershipTests.cs` | RESOLVED & VERIFIED |
| **J2 (LF03)** | Finance: Supplier detail action state release on business rejection | `SuppliersViewModel.cs` | `SupplierPayment_DefinitiveRejection_ClearsIntent_While_401_Retains` | RESOLVED & VERIFIED |
| **J3 (LF08)** | Inventory: Stock adjustment unsubmitted attempt unlock on mismatch | `StockAdjustmentViewModel.cs` | `StockAdjustmentHandlerBehavioralTests.cs` | RESOLVED & VERIFIED |
| **J4 (LF11)** | Sales: Warranty claim full semantic request canonical fingerprint | `WarrantyViewModel.cs` | `Phase11WarrantyAndCustodyBehavioralTests.cs` | RESOLVED & VERIFIED |
| **J5 (LF10)** | Inventory: Exact units cursor paging explicit cap & cycle detection | `RemoteStocktakeWorkflowService.cs` | `Sprint9Phase4ExactUnitWorkflowTests.cs` | RESOLVED & VERIFIED |
| **J6 (LF01)** | Sales: Store credit elimination & bank refund choice | `SalesReturnViewModel.cs`, `SalesReturnDialog.xaml` | `SalesReturnOperationIdentityTests.cs` | PRESERVED & VERIFIED |
| **J6 (LF02)** | Sales: Cart line aggregation & duplicate unit guard | `PosCartItemViewModel.cs`, `PosViewModel.cs` | `Phase1DPosResolutionAndExactUnitSaleTests.cs` | PRESERVED & VERIFIED |
| **J6 (LF05)** | Purchasing: Purchase return classifier adoption | `PurchaseReturnViewModel.cs` | `PurchaseReturn_ColdRestart_ReusesOperationId_OnSamePayload` | PRESERVED & VERIFIED |
| **J6 (LF06)** | Finance: Supplier payment enum validation | `SupplierAccountHandlers.cs` | `Phase1SupplierSubmissionSafetyTests.cs` | PRESERVED & VERIFIED |
| **J6 (LF07)** | Finance: Cash close route SalesPosUse authorization | `FinanceController.cs`, `CashSessionHandlers.cs` | `Phase2StocktakeThakaCashSessionBehavioralTests.cs` | PRESERVED & VERIFIED |
| **J6 (LF09)** | Inventory: 51st pack container targeted paging | `PackPickerViewModel.cs`, `CableCutViewModel.cs` | `Phase11PackStocktakeAndRemnantBehavioralTests.cs` | PRESERVED & VERIFIED |
| **J6 (LF12)** | Desktop: Main known rejection unfreeze | `DesktopOperationRejectionClassifier.cs` | `DesktopOperationRejectionClassifierTests.cs` | PRESERVED & VERIFIED |
| **J6 (LF13)** | Finance: Customer directory balance calculation with credit refunds | `BusinessOperationsReadServices.cs` | `THAKA_AUD_06`, `THAKA_AUD_07` | PRESERVED & VERIFIED |
| **J6 (LF14)** | Reporting: Net material value in Thaka activity reports | `BusinessOperationsReadServices.cs`, `ReportingQueries.cs` | `Phase7Pass5FinanceTests.cs` | PRESERVED & VERIFIED |
| **J6 (LF15)** | Identity: Strict supervisor posting preflight predicate | `PostingController.cs` | `Phase11PostingConsumerCallerIntegrationTests.cs` | PRESERVED & VERIFIED |
