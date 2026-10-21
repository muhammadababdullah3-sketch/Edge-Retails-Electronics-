# Candidate Drift and Provenance Audit: Run 075 vs Run 074

- **Run ID**: `ER-RUN-LOCALFLOW-20261019-075`
- **Predecessor Baseline**: `ER-RUN-LOCALFLOW-20261019-074`
- **Base Commit**: `3cd3b4b8f903adef6f6d7cf3c922c2ce184c2fc2`
- **Candidate Files**: `329` (`160` tracked, `169` untracked)
- **Identical Files**: `327`
- **Modified Files**: `2`
- **New Files**: `0`

## Intentional Modifications Summary

1. `src/EdgeRetails.Desktop/ViewModels/SuppliersViewModel.cs`:
   - **Root Review J2 Causal Repair**: Handled deterministic business rejections cleanly inside ViewModel without `throw;` escaping out of `async void` RelayCommands to the WPF dispatcher.
   - **Action Token Matching**: Replaced loose string matching with exact `actionToken` tracking (`"payment:" + purpose`, `"refund"`, `"payment-reversal:" + id`, `"refund-reversal:" + id`), ensuring attributable pending submission state and `_unresolvedFinancialAction` clear immediately upon proven no-commit business rejections while preserving 409/unknown pending recovery state.

2. `tests/EdgeRetails.Desktop.PerformanceTests/Phase12Wave3DurableClientOwnershipTests.cs`:
   - **J2 Handled UI Behavior**: Updated test `SupplierPayment_DefinitiveRejection_ClearsIntent_While_401_Retains` to assert clean handled execution, completed intent, `HasUnresolvedFinancialOperation == false`, and warning toast.
   - **Targeted Test Oracles (J1–J5)**:
     - `Targeted_J1_Expenses_RealAdapter_CommittedPostMissingReadback_RetrySingleCashEffect_And_PrePostValidation_NoPost`: Real adapter pre-POST validation rejection sends 0 POSTs and clears intent immediately; committed POST + missing readback retains intent, retry sends matching operation ID with single cash effect.
     - `Targeted_J2_SupplierDetailViewModel_Command_DeterministicRejection_HandledWithoutUIEscape_ClearsMatchingAction_AllowsFollowUp`: Direct command/UI entry exercises Settlement, Advance, Refund, and Payment Reversal deterministic rejections without escaping UI boundary, clears matching action token, allows follow-up, and verifies 409/conflict preserves intent and unresolved state.
     - `Targeted_J3_StockAdjustmentViewModel_PayloadMismatch_LeavesEditorUsable_And_RestoringOriginal_ReusesId`: Payload mismatch throws `InvalidOperationException` inside `ApplyAsync`, leaves editor usable (`CanEdit == true`), keeps old journal intact, and restoring original values reuses original operation ID.
     - `Targeted_J4_WarrantyViewModel_ChangedUnit_ChangesFingerprint_SortedPermutationStable_NextPartialClaimAllowedAfterReconciliation`: Serialized units canonical fingerprint changes on unit swap, remains identical on permutation, and subsequent partial claim is allowed after prior submission outcome reconciliation.
     - `Targeted_J5_RemoteStocktakeWorkflowService_ExactUnits_Unit201_Succeeds_And_CapExceeded_Or_CursorCycle_Throws`: 201 units query across cursor pages succeeds; 50-page cap (10,000 units) and cursor cycle explicitly throw typed `BackendOperationException`.

## Preserved Source Modules (LF01..LF15)
All 15 master local flow remediations from Run 074/073 are 100% preserved bit-for-bit without regression.
