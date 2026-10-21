# STAGE 5: Original Task Crosswalk

**Run ID:** `ER-RUN-LOCALFLOW-20261019-075`
**Authority:** `OWNER-UPDATE-20261018-019`
**Predecessor Review Reference:** `run074-review/ROOT_REVIEW.md`
**Timestamp UTC:** `2026-10-19T12:00:00Z`

---

| Corrective Seam ID | Domain & Focus | Implementation File | Verification Test Oracle | Status |
|---|---|---|---|---|
| **J2 (LF03)** | Finance: Supplier UI command handled rejection & token matching | `src/EdgeRetails.Desktop/ViewModels/SuppliersViewModel.cs` | `Targeted_J2_SupplierDetailViewModel_Command_DeterministicRejection_HandledWithoutUIEscape_ClearsMatchingAction_AllowsFollowUp` | **REPAIRED & VERIFIED** |
| **J1 (LF04)** | Finance: Real adapter pre-POST rejection vs committed retry | `src/EdgeRetails.Desktop/Services/RemoteBackendBusinessOperationsService.cs`, `ExpensesViewModel.cs` | `Targeted_J1_Expenses_RealAdapter_CommittedPostMissingReadback_RetrySingleCashEffect_And_PrePostValidation_NoPost` | **IMPLEMENTED & VERIFIED** |
| **J3 (LF08)** | Inventory: Stock adjustment mismatch editor recovery | `src/EdgeRetails.Desktop/ViewModels/StockAdjustmentViewModel.cs` | `Targeted_J3_StockAdjustmentViewModel_PayloadMismatch_LeavesEditorUsable_And_RestoringOriginal_ReusesId` | **IMPLEMENTED & VERIFIED** |
| **J4 (LF11)** | Sales: Warranty unit swap fingerprint & partial claim | `src/EdgeRetails.Desktop/ViewModels/WarrantyViewModel.cs` | `Targeted_J4_WarrantyViewModel_ChangedUnit_ChangesFingerprint_SortedPermutationStable_NextPartialClaimAllowedAfterReconciliation` | **IMPLEMENTED & VERIFIED** |
| **J5 (LF10)** | Inventory: Exact units 201 units, cap exceeded & cursor cycle | `src/EdgeRetails.Desktop/Services/RemoteStocktakeWorkflowService.cs` | `Targeted_J5_RemoteStocktakeWorkflowService_ExactUnits_Unit201_Succeeds_And_CapExceeded_Or_CursorCycle_Throws` | **IMPLEMENTED & VERIFIED** |
| **LF01** | Sales: Store credit elimination & bank refund choice | `SalesReturnViewModel.cs`, `SalesReturnDialog.xaml` | `SalesReturnOperationIdentityTests.cs` | PRESERVED |
| **LF02** | Sales: Cart line aggregation & duplicate unit guard | `PosCartItemViewModel.cs`, `PosViewModel.cs` | `Phase1DPosResolutionAndExactUnitSaleTests.cs` | PRESERVED |
| **LF05** | Purchasing: Purchase return classifier adoption | `PurchaseReturnViewModel.cs` | `PurchaseReturn_ColdRestart_ReusesOperationId_OnSamePayload` | PRESERVED |
| **LF06** | Finance: Supplier payment enum validation | `SupplierAccountHandlers.cs` | `Phase1SupplierSubmissionSafetyTests.cs` | PRESERVED |
| **LF07** | Finance: Cash close route SalesPosUse authorization | `FinanceController.cs`, `CashSessionHandlers.cs` | `Phase2StocktakeThakaCashSessionBehavioralTests.cs` | PRESERVED |
| **LF09** | Inventory: 51st pack container targeted paging | `PackPickerViewModel.cs`, `CableCutViewModel.cs` | `Phase11PackStocktakeAndRemnantBehavioralTests.cs` | PRESERVED |
| **LF12** | Desktop: Main known rejection unfreeze | `DesktopOperationRejectionClassifier.cs` | `DesktopOperationRejectionClassifierTests.cs` | PRESERVED |
| **LF13** | Finance: Customer directory balance with credit refunds | `BusinessOperationsReadServices.cs` | `THAKA_AUD_06`, `THAKA_AUD_07` | PRESERVED |
| **LF14** | Reporting: Net material value in Thaka activity reports | `ReportingQueries.cs` | `Phase7Pass5FinanceTests.cs` | PRESERVED |
| **LF15** | Identity: Strict supervisor posting preflight predicate | `PostingController.cs` | `Phase11PostingConsumerCallerIntegrationTests.cs` | PRESERVED |
