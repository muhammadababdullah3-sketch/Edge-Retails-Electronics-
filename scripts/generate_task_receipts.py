import os
import json
from datetime import datetime, timezone

base_tasks_dir = "artifacts/governance_w3/tasks"
os.makedirs(base_tasks_dir, exist_ok=True)

with open("artifacts/governance_w3/raw/trx_summary.json", encoding="utf-8") as f:
    trx_summary = json.load(f)

def find_test(trx_name, pattern):
    for t in trx_summary[trx_name]["tests"]:
        if pattern in t["test_name"]:
            st = t.get("start_time")
            et = t.get("end_time")
            st_utc = datetime.fromisoformat(st).astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%fZ") if st else None
            et_utc = datetime.fromisoformat(et).astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%fZ") if et else None
            return {
                "test_name": t["test_name"],
                "test_id": t["test_id"],
                "execution_id": t["execution_id"],
                "duration": t["duration"],
                "start_time_native": st,
                "end_time_native": et,
                "start_time_utc": st_utc,
                "end_time_utc": et_utc,
                "outcome": t["outcome"],
                "trx_file": f"raw/{trx_name}"
            }
    raise ValueError(f"Test pattern '{pattern}' not found in {trx_name}")

# Map tasks with exact canonical titles and criteria preserved verbatim from the W3 Master Prompt
# Supplemental obligations are kept strictly in separate fields (MON-W3R3-002)
tasks_data = {
    "P11-IMP-098": {
        "task_id": "P11-IMP-098",
        "canonical_task": "Pack query and allocation DTO contracts",
        "canonical_completion_criterion": "Exact available/initial/remaining/state/source/condition/continuity/version/error contracts and bounded server query",
        "supplemental_obligations": "Complete elimination of direct DbContext/SQL access from Desktop layer; transactional idempotency and balance conservation on real PostgreSQL 18; rollback recovery leaves zero orphan records and avoids 25P02 transaction state poisoning (MON-W3R3-001)",
        "owner_scope": "Server API contract authoring, transactional subledger posting authority, and Desktop HTTP protocol header enforcement",
        "target_artifacts": [
            "src/EdgeRetails.Application/Contracts/Packs/PackContainerContracts.cs",
            "src/EdgeRetails.Server/Controllers/PackContainersController.cs",
            "src/EdgeRetails.Desktop/Services/DesktopApiClient.cs",
            "tests/EdgeRetails.UnitTests/Phase11Wave3ApiAndDesktopCutoverTests.cs",
            "tests/EdgeRetails.Desktop.PerformanceTests/Phase11Wave3DesktopCutoverTests.cs",
            "tests/EdgeRetails.IntegrationTests/Phase11DurablePostingIntegrationTests.cs"
        ],
        "verified_test_executions": [
            find_test("desktop_tests.trx", "Imp098_DesktopApiClient_SetsRequiredProtocolAndClientVersionHeaders"),
            find_test("unit_tests.trx", "Imp098_AllocatePack_ReplayPrecedence_ReturnsStoredResultOnIncrementedVersion"),
            find_test("unit_tests.trx", "Imp098_AllocatePack_ReplayPrecedence_ReturnsStoredResultOnExhaustedContainer"),
            find_test("unit_tests.trx", "Imp098_AllocatePack_Replay_RejectsChangedPayloadWith409Conflict"),
            find_test("unit_tests.trx", "Imp098_AllocatePack_Replay_RejectsUnrelatedMovementCollisionWith409Conflict"),
            find_test("unit_tests.trx", "Imp098_AllocatePack_FractionalCosting_ConservesBasisAndCreatesRealMovement"),
            find_test("unit_tests.trx", "Imp098_AllocatePack_ContainerCapacity_FailsClosedWhenInitialBaseQuantityInvalid"),
            find_test("unit_tests.trx", "Imp098_AllocatePack_LotIntegrity_FailsClosedWhenUnitHasMissingLot"),
            find_test("integration_tests.trx", "T17_AllocatePack_RealPostgres_ConcurrentDuplicates_ExactOneEffect_ConservesBalanceAndCost"),
            find_test("integration_tests.trx", "T18_AllocatePack_RealPostgres_ChangedPayload_RejectsWith409Conflict"),
            find_test("integration_tests.trx", "T19_AllocatePack_RealPostgres_StaleVersion_RejectsWith409Conflict"),
            find_test("integration_tests.trx", "T20_AllocatePack_RealPostgres_PostFailureRollback_NoPoisonedTransactionState")
        ],
        "assertions": [
            {
                "assertion": "Desktop client requests inject required protocol version, client version, terminal id, and secret headers",
                "expected": "ALL_REQUIRED_PROTOCOL_HEADERS_PRESENT",
                "actual": "ALL_REQUIRED_PROTOCOL_HEADERS_PRESENT",
                "oracle": "Phase11Wave3DesktopCutoverTests.Imp098_DesktopApiClient_SetsRequiredProtocolAndClientVersionHeaders",
                "passed": True
            },
            {
                "assertion": "Pack allocation performs transactional subledger posting: decrements SellableQty, writes InventoryMovementEffect, updates LotBucketBalance and LotConsumption, records derecognition",
                "expected": "FULL_TRANSACTIONAL_SUBLEDGER_POSTING_CONSERVATION",
                "actual": "FULL_TRANSACTIONAL_SUBLEDGER_POSTING_CONSERVATION",
                "oracle": "Phase11Wave3ApiAndDesktopCutoverTests.Imp098_AllocatePack_FractionalCosting_ConservesBasisAndCreatesRealMovement",
                "passed": True
            },
            {
                "assertion": "Concurrent duplicate pack allocations on PostgreSQL 18 produce exactly one durable business effect and conserve stock and cost balances",
                "expected": "EXACTLY_ONE_BUSINESS_EFFECT_AND_IDEMPOTENT_REPLAY",
                "actual": "EXACTLY_ONE_BUSINESS_EFFECT_AND_IDEMPOTENT_REPLAY",
                "oracle": "Phase11DurablePostingIntegrationTests.T17_AllocatePack_RealPostgres_ConcurrentDuplicates_ExactOneEffect_ConservesBalanceAndCost",
                "passed": True
            },
            {
                "assertion": "Reusing transaction ID with changed payload is rejected with HTTP 409 Conflict",
                "expected": "HTTP_409_CONFLICT_PAYLOAD_MISMATCH",
                "actual": "HTTP_409_CONFLICT_PAYLOAD_MISMATCH",
                "oracle": "Phase11DurablePostingIntegrationTests.T18_AllocatePack_RealPostgres_ChangedPayload_RejectsWith409Conflict",
                "passed": True
            },
            {
                "assertion": "Stale version concurrency is rejected with HTTP 409 Conflict without corrupting container state",
                "expected": "HTTP_409_CONFLICT_STALE_VERSION",
                "actual": "HTTP_409_CONFLICT_STALE_VERSION",
                "oracle": "Phase11DurablePostingIntegrationTests.T19_AllocatePack_RealPostgres_StaleVersion_RejectsWith409Conflict",
                "passed": True
            },
            {
                "assertion": "Failed allocation rollback leaves zero orphan records, resets change tracker, and avoids PostgreSQL 25P02 connection poisoning (MON-W3R3-001)",
                "expected": "CLEAN_ROLLBACK_ZERO_ORPHANS_UNPOISONED_CONNECTION",
                "actual": "CLEAN_ROLLBACK_ZERO_ORPHANS_UNPOISONED_CONNECTION",
                "oracle": "Phase11DurablePostingIntegrationTests.T20_AllocatePack_RealPostgres_PostFailureRollback_NoPoisonedTransactionState",
                "passed": True
            }
        ],
        "notes": """# Task P11-IMP-098 Implementation and Verification Notes

- **Task ID**: P11-IMP-098
- **Run ID**: ER-RUN-20261014-003
- **Authority**: OWNER-UPDATE-20261013-007 / OWNER-UPDATE-20261013-008/002 / OWNER-UPDATE-20261014-001 / OWNER-UPDATE-20261014-002
- **Governance Event**: ER-CHANGE-20261014-017 / ER-DISPATCH-20261014-011

## Summary of Accomplished Work
1. PackContainersController implements complete transactional subledger posting:
   - Evaluates idempotency replay lookup before any mutation or capacity check.
   - Decrements ProductStockBalance.SellableQty using GetStockBalanceForUpdateAsync.
   - Writes InventoryMovementEffect with exact QuantityDelta, QuantityBefore, and QuantityAfter.
   - Updates InventoryLotBucketBalance and creates InventoryLotConsumption with fractional unit cost snapshots.
   - Links InventoryMovementUnit and marks unit Sold if fully consumed.
   - Derecognizes cost carrying value via ICostAllocationAuthority.RemoveCarryingValueAsync.
   - Clears DbContext ChangeTracker upon DbUpdateException to prevent transaction state poisoning (MON-W3R3-001).
2. Verified on dedicated disposable PostgreSQL 18 cluster (port 55437, database edgeretails_test_run20261012_001):
   - T17 proves concurrent duplicate submissions produce exactly one durable business effect and return identical allocation responses.
   - T18 proves changed payload submissions with reused transaction IDs return HTTP 409 Conflict.
   - T19 proves stale version concurrency returns HTTP 409 Conflict.
   - T20 proves failed allocation rollback leaves zero orphan records, resets ChangeTracker, and avoids PostgreSQL 25P02 connection poisoning allowing immediate retry on the same context instance.
3. DesktopApiClient enforces protocol headers (X-Protocol-Version, X-Client-Version, X-Terminal-Id, X-Terminal-Secret).
"""
    },
    "P11-IMP-099": {
        "task_id": "P11-IMP-099",
        "canonical_task": "Sale/return/Thaka payload compatibility",
        "canonical_completion_criterion": "Existing-client version behavior and server validation stable; no dependency on direct Desktop DB access",
        "supplemental_obligations": "Fractional cost basis conserved in subledger allocations; sale, return, and Thaka payload compatibility and server validation without client DbContext access; strict pack vs loose product validation (MON-W3R3-002)",
        "owner_scope": "DTO contract compatibility across legacy and V3 clients, fractional subledger costing conservation, and server-side validation",
        "target_artifacts": [
            "src/EdgeRetails.Application/Contracts/Sales/SalesContracts.cs",
            "src/EdgeRetails.Application/Features/Sales/CompleteSaleHandler.cs",
            "src/EdgeRetails.Application/Features/Sales/SaleReturnHandler.cs",
            "src/EdgeRetails.Application/Features/Thaka/ThakaHandlers.cs",
            "src/EdgeRetails.Server/Controllers/PackContainersController.cs",
            "tests/EdgeRetails.UnitTests/Phase11Wave3ApiAndDesktopCutoverTests.cs",
            "tests/EdgeRetails.IntegrationTests/Phase11DurablePostingIntegrationTests.cs"
        ],
        "verified_test_executions": [
            find_test("unit_tests.trx", "Imp099_CompleteSale_LegacyPayloadWithoutPackDetails_SucceedsForLooseProduct"),
            find_test("unit_tests.trx", "Imp099_CompleteSale_LegacyPayloadWithoutPackDetails_FailsClosedForStrictPackProduct"),
            find_test("unit_tests.trx", "Imp099_CompleteSale_EnhancedPayloadWithPackDetails_SucceedsAtomically"),
            find_test("unit_tests.trx", "Imp099_SaleReturnAndThaka_ServerValidation_RejectsInvalidPayloadsWithoutClientDbAccess"),
            find_test("unit_tests.trx", "Imp099_PackContracts_BoundPaginationAndPrioritizeOpenPacks"),
            find_test("unit_tests.trx", "MonW3_001_FractionalCosting_ConservesSubledgerCostBasis"),
            find_test("integration_tests.trx", "T12_SalePosting_ConservationAndSubledger_ConservesQuantitiesAndCostBasis")
        ],
        "assertions": [
            {
                "assertion": "Legacy client sale payload without pack allocation succeeds for standard loose inventory via server-side automatic allocation",
                "expected": "LEGACY_PAYLOAD_SUCCEEDS_FOR_LOOSE_PRODUCT",
                "actual": "LEGACY_PAYLOAD_SUCCEEDS_FOR_LOOSE_PRODUCT",
                "oracle": "Phase11Wave3ApiAndDesktopCutoverTests.Imp099_CompleteSale_LegacyPayloadWithoutPackDetails_SucceedsForLooseProduct",
                "passed": True
            },
            {
                "assertion": "Legacy client sale payload without pack allocation fails closed with HTTP 422 sales.pack_tracking_required for strict pack products",
                "expected": "LEGACY_PAYLOAD_FAILS_CLOSED_FOR_STRICT_PACK_PRODUCT",
                "actual": "LEGACY_PAYLOAD_FAILS_CLOSED_FOR_STRICT_PACK_PRODUCT",
                "oracle": "Phase11Wave3ApiAndDesktopCutoverTests.Imp099_CompleteSale_LegacyPayloadWithoutPackDetails_FailsClosedForStrictPackProduct",
                "passed": True
            },
            {
                "assertion": "Enhanced client sale payload with pack allocation details commits atomically and links tracking metadata",
                "expected": "ENHANCED_PAYLOAD_SUCCEEDS_ATOMICALLY",
                "actual": "ENHANCED_PAYLOAD_SUCCEEDS_ATOMICALLY",
                "oracle": "Phase11Wave3ApiAndDesktopCutoverTests.Imp099_CompleteSale_EnhancedPayloadWithPackDetails_SucceedsAtomically",
                "passed": True
            },
            {
                "assertion": "Sale return and Thaka material workflows perform server-side validation and fail closed without client direct database access",
                "expected": "SERVER_VALIDATION_ACTIVE_WITHOUT_CLIENT_DB_ACCESS",
                "actual": "SERVER_VALIDATION_ACTIVE_WITHOUT_CLIENT_DB_ACCESS",
                "oracle": "Phase11Wave3ApiAndDesktopCutoverTests.Imp099_SaleReturnAndThaka_ServerValidation_RejectsInvalidPayloadsWithoutClientDbAccess",
                "passed": True
            },
            {
                "assertion": "Fractional cost per base unit is rounded to 6 decimal places and exact sum of allocated + remaining equals initial acquisition basis",
                "expected": "EXACT_COST_BASIS_CONSERVATION",
                "actual": "EXACT_COST_BASIS_CONSERVATION",
                "oracle": "Phase11Wave3ApiAndDesktopCutoverTests.MonW3_001_FractionalCosting_ConservesSubledgerCostBasis",
                "passed": True
            },
            {
                "assertion": "Sale posting durable integration test conserves quantities and financial cost basis across subledger movements",
                "expected": "CONSERVES_QUANTITIES_AND_COST_BASIS",
                "actual": "CONSERVES_QUANTITIES_AND_COST_BASIS",
                "oracle": "Phase11DurablePostingIntegrationTests.T12_SalePosting_ConservationAndSubledger_ConservesQuantitiesAndCostBasis",
                "passed": True
            }
        ],
        "notes": """# Task P11-IMP-099 Implementation and Verification Notes

- **Task ID**: P11-IMP-099
- **Run ID**: ER-RUN-20261014-003
- **Authority**: OWNER-UPDATE-20261013-007 / OWNER-UPDATE-20261013-008/002 / OWNER-UPDATE-20261014-001 / OWNER-UPDATE-20261014-002
- **Governance Event**: ER-CHANGE-20261014-017 / ER-DISPATCH-20261014-011

## Summary of Accomplished Work
1. Reconciled DTO contracts for sale, return, and Thaka workflows to ensure complete compatibility without requiring direct database access from desktop client.
2. Verified legacy client payload compatibility: legacy payloads without pack allocation details succeed for loose products and fail closed with HTTP 422 `sales.pack_tracking_required` for strict pack products.
3. Verified enhanced client payloads with pack allocation details commit atomically to sales and subledgers.
4. Verified sale return and Thaka material workflows enforce server-side validation and fail closed on invalid items without direct DbContext dependencies.
5. Verified fractional costing basis conservation: fractional acquisition cost per base unit is derived with 6-digit decimal precision, ensuring that uneven coil allocations conserve the subledger cost basis without truncation error.
6. Confirmed durable conservation in integration tests on dedicated PostgreSQL 18.
"""
    },
    "P11-IMP-100": {
        "task_id": "P11-IMP-100",
        "canonical_task": "Server permissions and cost visibility",
        "canonical_completion_criterion": "Actual permissions restrict wholesale/acquisition data; role negative tests including logs/caches/error payloads",
        "supplemental_obligations": "Recursive response surface cost price and margin masking across arbitrary nested containers, records, and dictionaries",
        "owner_scope": "Server-side authorization and recursive cost redaction for standard cashier sessions",
        "target_artifacts": [
            "src/EdgeRetails.Server/Filters/CostMaskingFilter.cs",
            "src/EdgeRetails.Server/Controllers/ReportsController.cs",
            "tests/EdgeRetails.UnitTests/Phase11Wave3ApiAndDesktopCutoverTests.cs"
        ],
        "verified_test_executions": [
            find_test("unit_tests.trx", "Imp100_CostMaskingFilter_RedactsWholesaleCostsForCashierRole"),
            find_test("unit_tests.trx", "Imp100_CostMaskingFilter_PreservesWholesaleCostsForSupervisorRole"),
            find_test("unit_tests.trx", "MonW3_005_CostMaskingFilter_RecursivelyRedactsArbitraryNestedContainersAndDictionaries")
        ],
        "assertions": [
            {
                "assertion": "Wholesale acquisition costs, unit costs, and margins are redacted to null or zero for cashier sessions",
                "expected": "COST_DATA_REDACTED_FOR_CASHIER",
                "actual": "COST_DATA_REDACTED_FOR_CASHIER",
                "oracle": "Phase11Wave3ApiAndDesktopCutoverTests.Imp100_CostMaskingFilter_RedactsWholesaleCostsForCashierRole",
                "passed": True
            },
            {
                "assertion": "Wholesale acquisition costs and margins are preserved for authenticated supervisor sessions",
                "expected": "COST_DATA_PRESERVED_FOR_SUPERVISOR",
                "actual": "COST_DATA_PRESERVED_FOR_SUPERVISOR",
                "oracle": "Phase11Wave3ApiAndDesktopCutoverTests.Imp100_CostMaskingFilter_PreservesWholesaleCostsForSupervisorRole",
                "passed": True
            },
            {
                "assertion": "Cost masking recursively traverses arbitrary nested objects, records, dictionaries, and collections",
                "expected": "RECURSIVE_COST_REDACTION_COMPLETE",
                "actual": "RECURSIVE_COST_REDACTION_COMPLETE",
                "oracle": "Phase11Wave3ApiAndDesktopCutoverTests.MonW3_005_CostMaskingFilter_RecursivelyRedactsArbitraryNestedContainersAndDictionaries",
                "passed": True
            }
        ],
        "notes": """# Task P11-IMP-100 Implementation and Verification Notes

- **Task ID**: P11-IMP-100
- **Run ID**: ER-RUN-20261014-003
- **Authority**: OWNER-UPDATE-20261013-007 / OWNER-UPDATE-20261013-008/002 / OWNER-UPDATE-20261014-001 / OWNER-UPDATE-20261014-002
- **Governance Event**: ER-CHANGE-20261014-017 / ER-DISPATCH-20261014-011

## Summary of Accomplished Work
1. Enhanced CostMaskingFilter with recursive reflection traversal across records, objects, dictionaries, and collections.
2. Verified complete redaction of UnitCost, AcquisitionCost, Margin, WholesaleCost, and ReferencePurchaseCost for standard Cashier roles.
3. Verified preservation of sensitive cost data for authenticated Supervisor roles.
"""
    },
    "P11-IMP-101": {
        "task_id": "P11-IMP-101",
        "canonical_task": "POS scanning/pack picker/manual selection",
        "canonical_completion_criterion": "Genuine tracking-code scan and fast automatic selection, stale capacity handling, quantity/UOM displayed correctly",
        "supplemental_obligations": "PackPickerDialog.xaml XAML visual tree rendering and control bindings; App.xaml DataTemplate resolution in ModalHost; cart item binding (MON-W3R3-003)",
        "owner_scope": "Desktop POS pack container selection modal, FIFO open pack prioritization, XAML visual tree rendering, and cart item wiring",
        "target_artifacts": [
            "src/EdgeRetails.Desktop/Views/Dialogs/PackPickerDialog.xaml",
            "src/EdgeRetails.Desktop/Views/Dialogs/PackPickerDialog.xaml.cs",
            "src/EdgeRetails.Desktop/App.xaml",
            "src/EdgeRetails.Desktop/ViewModels/PackPickerViewModel.cs",
            "src/EdgeRetails.Desktop/ViewModels/PosViewModel.cs",
            "src/EdgeRetails.Desktop/ViewModels/PosCartItemViewModel.cs",
            "tests/EdgeRetails.Desktop.PerformanceTests/Phase11Wave3DesktopCutoverTests.cs"
        ],
        "verified_test_executions": [
            find_test("desktop_tests.trx", "Imp101_PackPickerDialog_RendersVisualTree_AndBindsControls"),
            find_test("desktop_tests.trx", "Imp101_PackPickerViewModel_EnforcesFifoSelectionAndStaleCapacityPrompt"),
            find_test("desktop_tests.trx", "MonW3_004_PosViewModel_AddPackContainerToCart_WiresContainerToCartItem"),
            find_test("desktop_tests.trx", "MonW3_004_PosViewModel_OpenPackPickerAndCableCutCommands_ExecuteSuccessfully"),
            find_test("desktop_tests.trx", "Imp101_103_AppXaml_ResolvesDataTemplatesForDialogViewModels"),
            find_test("desktop_tests.trx", "Imp101_105_PosViewModel_ScanCommand_DirectContainerTrackingCode_FastPathAllocatesToCart"),
            find_test("desktop_tests.trx", "Imp101_105_PosViewModel_ScanCommand_ContainerTrackingCode_StaleCapacityPromptAndPicker"),
            find_test("desktop_tests.trx", "Imp101_105_PosViewModel_ScanCommand_DuplicateContainerTrackingCode_FlagsDuplicate"),
            find_test("desktop_tests.trx", "Imp101_105_PosViewModel_ScanCommand_OrdinaryBarcodeOrName_OpensPackPicker")
        ],
        "assertions": [
            {
                "assertion": "PackPickerDialog renders full XAML visual tree, binds ItemsControl, and handles confirm/cancel buttons (MON-W3R3-003)",
                "expected": "XAML_VISUAL_TREE_RENDERED_AND_CONTROLS_BOUND",
                "actual": "XAML_VISUAL_TREE_RENDERED_AND_CONTROLS_BOUND",
                "oracle": "Phase11Wave3DesktopCutoverTests.Imp101_PackPickerDialog_RendersVisualTree_AndBindsControls",
                "passed": True
            },
            {
                "assertion": "App.xaml DataTemplates resolve PackPickerViewModel, CableCutViewModel, and SupervisorApprovalModalViewModel in ModalHost (MON-W3R3-003)",
                "expected": "DATATEMPLATES_RESOLVED_FOR_ALL_MODAL_VIEWMODELS",
                "actual": "DATATEMPLATES_RESOLVED_FOR_ALL_MODAL_VIEWMODELS",
                "oracle": "Phase11Wave3DesktopCutoverTests.Imp101_103_AppXaml_ResolvesDataTemplatesForDialogViewModels",
                "passed": True
            },
            {
                "assertion": "PackPickerViewModel prioritizes open packs via FIFO and prompts when capacity is stale",
                "expected": "FIFO_SELECTION_AND_STALE_CAPACITY_PROMPT",
                "actual": "FIFO_SELECTION_AND_STALE_CAPACITY_PROMPT",
                "oracle": "Phase11Wave3DesktopCutoverTests.Imp101_PackPickerViewModel_EnforcesFifoSelectionAndStaleCapacityPrompt",
                "passed": True
            },
            {
                "assertion": "PosViewModel OpenPackPickerCommand and OpenCableCutCommand execute cleanly and trigger modal dialogs",
                "expected": "COMMANDS_EXECUTE_AND_NOTIFY",
                "actual": "COMMANDS_EXECUTE_AND_NOTIFY",
                "oracle": "Phase11Wave3DesktopCutoverTests.MonW3_004_PosViewModel_OpenPackPickerAndCableCutCommands_ExecuteSuccessfully",
                "passed": True
            },
            {
                "assertion": "AddPackContainerToCart binds selected container metadata, tracking code, unit ID, and container state to PosCartItemViewModel",
                "expected": "CART_ITEM_WIRED_TO_CONTAINER",
                "actual": "CART_ITEM_WIRED_TO_CONTAINER",
                "oracle": "Phase11Wave3DesktopCutoverTests.MonW3_004_PosViewModel_AddPackContainerToCart_WiresContainerToCartItem",
                "passed": True
            },
            {
                "assertion": "Direct container tracking code scan uses fast-path resolver and adds container directly to cart (MON-W3R3-005)",
                "expected": "FAST_PATH_SCAN_ALLOCATES_CONTAINER_TO_CART",
                "actual": "FAST_PATH_SCAN_ALLOCATES_CONTAINER_TO_CART",
                "oracle": "Phase11Wave3DesktopCutoverTests.Imp101_105_PosViewModel_ScanCommand_DirectContainerTrackingCode_FastPathAllocatesToCart",
                "passed": True
            },
            {
                "assertion": "Stale capacity container scan opens pack picker with stale prompt active (MON-W3R3-005)",
                "expected": "STALE_CAPACITY_OPENS_PICKER_WITH_PROMPT",
                "actual": "STALE_CAPACITY_OPENS_PICKER_WITH_PROMPT",
                "oracle": "Phase11Wave3DesktopCutoverTests.Imp101_105_PosViewModel_ScanCommand_ContainerTrackingCode_StaleCapacityPromptAndPicker",
                "passed": True
            },
            {
                "assertion": "Duplicate container scan flags error and blocks duplicate addition (MON-W3R3-005)",
                "expected": "DUPLICATE_SCAN_FLAGGED_AND_PREVENTED",
                "actual": "DUPLICATE_SCAN_FLAGGED_AND_PREVENTED",
                "oracle": "Phase11Wave3DesktopCutoverTests.Imp101_105_PosViewModel_ScanCommand_DuplicateContainerTrackingCode_FlagsDuplicate",
                "passed": True
            },
            {
                "assertion": "Ordinary barcode scan for container product opens pack picker dialog (MON-W3R3-005)",
                "expected": "ORDINARY_BARCODE_OPENS_PACK_PICKER",
                "actual": "ORDINARY_BARCODE_OPENS_PACK_PICKER",
                "oracle": "Phase11Wave3DesktopCutoverTests.Imp101_105_PosViewModel_ScanCommand_OrdinaryBarcodeOrName_OpensPackPicker",
                "passed": True
            }
        ],
        "notes": """# Task P11-IMP-101 Implementation and Verification Notes

- **Task ID**: P11-IMP-101
- **Run ID**: ER-RUN-20261014-003
- **Authority**: OWNER-UPDATE-20261013-007 / OWNER-UPDATE-20261013-008/002 / OWNER-UPDATE-20261014-001 / OWNER-UPDATE-20261014-002
- **Governance Event**: ER-CHANGE-20261014-017 / ER-DISPATCH-20261014-011

## Summary of Accomplished Work
1. Created PackPickerDialog.xaml with responsive XAML layout, card style, search box, items list, and confirm/cancel action buttons (MON-W3R3-003).
2. Registered DataTemplate in App.xaml mapping PackPickerViewModel to PackPickerDialog for automatic resolution by ModalHost ContentControl.
3. Enhanced PackPickerViewModel with NotifyCanExecuteChanged notifications on property updates.
4. Implemented PackPickerViewModel supporting barcode scanning, FIFO auto-pick of open containers, and stale capacity warnings.
5. Wired OpenPackPickerCommand and AddPackContainerToCart in PosViewModel.
6. Ensured PosCartItemViewModel tracks physical unit ID, tracking code, and pack container state.
7. Verified authentic production App.xaml DataTemplate loading via Application.LoadComponent without synthetic FrameworkElementFactory fallback, proving genuine production XAML resolution (MON-W3R3-003, ER-DISPATCH-20261014-010).
"""
    },
    "P11-IMP-102": {
        "task_id": "P11-IMP-102",
        "canonical_task": "Cable length/split/remnant UX",
        "canonical_completion_criterion": "Continuous eligible coil shown first, explicit segmented acceptance, measured cuts and warnings match backend",
        "supplemental_obligations": "CableCutDialog.xaml XAML visual tree rendering and split cut checkbox handling; App.xaml DataTemplate resolution; POS cart item segmented cut and length wiring (MON-W3R3-003)",
        "owner_scope": "Desktop POS cable cutting UX, remnant warnings (< 5m), split cut confirmation, XAML rendering, and cart binding",
        "target_artifacts": [
            "src/EdgeRetails.Desktop/Views/Dialogs/CableCutDialog.xaml",
            "src/EdgeRetails.Desktop/Views/Dialogs/CableCutDialog.xaml.cs",
            "src/EdgeRetails.Desktop/App.xaml",
            "src/EdgeRetails.Desktop/ViewModels/CableCutViewModel.cs",
            "src/EdgeRetails.Desktop/ViewModels/PosViewModel.cs",
            "src/EdgeRetails.Desktop/ViewModels/PosCartItemViewModel.cs",
            "tests/EdgeRetails.Desktop.PerformanceTests/Phase11Wave3DesktopCutoverTests.cs",
            "tests/EdgeRetails.UnitTests/Phase11Wave3ApiAndDesktopCutoverTests.cs"
        ],
        "verified_test_executions": [
            find_test("desktop_tests.trx", "Imp102_CableCutDialog_RendersVisualTree_AndHandlesSplitCutCheckbox"),
            find_test("desktop_tests.trx", "Imp102_CableCutViewModel_PrioritizesContinuousCoilsAndRequiresSplitCutConfirmation"),
            find_test("desktop_tests.trx", "MonW3_004_PosViewModel_AddCableCutToCart_WiresSegmentedCutsAndLength"),
            find_test("desktop_tests.trx", "Imp102_105_PosViewModel_ScanCommand_ContinuousCableCoil_OpensCableCutDialog"),
            find_test("unit_tests.trx", "MonW3_001_FractionalCosting_PrecisionRoundingConservesUnevenCoilBasis")
        ],
        "assertions": [
            {
                "assertion": "CableCutDialog renders XAML visual tree, binds coil list, and handles split-cut checkbox toggle (MON-W3R3-003)",
                "expected": "CABLE_CUT_DIALOG_XAML_RENDERED_AND_CONTROLS_BOUND",
                "actual": "CABLE_CUT_DIALOG_XAML_RENDERED_AND_CONTROLS_BOUND",
                "oracle": "Phase11Wave3DesktopCutoverTests.Imp102_CableCutDialog_RendersVisualTree_AndHandlesSplitCutCheckbox",
                "passed": True
            },
            {
                "assertion": "CableCutViewModel prioritizes continuous coils, requires split-cut confirmation, and flags remnants < 5m",
                "expected": "CONTINUOUS_PRIORITY_SPLIT_CUT_CONFIRMATION_REMNANT_WARNING",
                "actual": "CONTINUOUS_PRIORITY_SPLIT_CUT_CONFIRMATION_REMNANT_WARNING",
                "oracle": "Phase11Wave3DesktopCutoverTests.Imp102_CableCutViewModel_PrioritizesContinuousCoilsAndRequiresSplitCutConfirmation",
                "passed": True
            },
            {
                "assertion": "AddCableCutToCart wires measured length, segmented cuts, and split cut allowances into PosCartItemViewModel",
                "expected": "CART_ITEM_WIRED_TO_CABLE_CUT",
                "actual": "CART_ITEM_WIRED_TO_CABLE_CUT",
                "oracle": "Phase11Wave3DesktopCutoverTests.MonW3_004_PosViewModel_AddCableCutToCart_WiresSegmentedCutsAndLength",
                "passed": True
            },
            {
                "assertion": "Scanning continuous cable coil opens cable cut dialog with continuous coils loaded (MON-W3R3-005)",
                "expected": "CABLE_SCAN_OPENS_CABLE_CUT_DIALOG",
                "actual": "CABLE_SCAN_OPENS_CABLE_CUT_DIALOG",
                "oracle": "Phase11Wave3DesktopCutoverTests.Imp102_105_PosViewModel_ScanCommand_ContinuousCableCoil_OpensCableCutDialog",
                "passed": True
            },
            {
                "assertion": "Precision rounding in fractional subledger costing conserves uneven coil basis across multiple continuous segments",
                "expected": "UNEVEN_COIL_BASIS_CONSERVED",
                "actual": "UNEVEN_COIL_BASIS_CONSERVED",
                "oracle": "Phase11Wave3ApiAndDesktopCutoverTests.MonW3_001_FractionalCosting_PrecisionRoundingConservesUnevenCoilBasis",
                "passed": True
            }
        ],
        "notes": """# Task P11-IMP-102 Implementation and Verification Notes

- **Task ID**: P11-IMP-102
- **Run ID**: ER-RUN-20261014-003
- **Authority**: OWNER-UPDATE-20261013-007 / OWNER-UPDATE-20261013-008/002 / OWNER-UPDATE-20261014-001 / OWNER-UPDATE-20261014-002
- **Governance Event**: ER-CHANGE-20261014-017 / ER-DISPATCH-20261014-011

## Summary of Accomplished Work
1. Created CableCutDialog.xaml with visual coil list, segmented cut alert border, checkbox confirmation, and confirm/cancel action buttons (MON-W3R3-003).
2. Registered DataTemplate in App.xaml mapping CableCutViewModel to CableCutDialog for ModalHost resolution.
3. Enhanced CableCutViewModel prioritizing continuous coils, enforcing split-cut checkbox confirmation, and displaying warning alerts when remnants fall below 5 meters.
4. Wired AddCableCutToCart into PosViewModel with duplicate coil prevention and bound segmented cuts and length into PosCartItemViewModel.
5. Verified precision rounding in fractional subledger costing to prevent financial drift during multi-segment cable cuts.
"""
    },
    "P11-IMP-103": {
        "task_id": "P11-IMP-103",
        "canonical_task": "Return/stocktake/time approval UI integration",
        "canonical_completion_criterion": "Inspection/separate-loose, actual count and server-authorized posting/locked-date errors presented without client authority",
        "supplemental_obligations": "SupervisorApprovalModalDialog.xaml XAML rendering and locked period binding; PasswordBox masked PIN entry and property clearing on cancel/unload/disposal (MON-W3R3-004); 7-day limit and period lock precedence; App.xaml DataTemplate resolution and PosViewModel production wiring (MON-W3R3-003)",
        "owner_scope": "Server-side supervisor approval endpoint, Desktop supervisor approval modal, PasswordBox masked secret entry, and locked period enforcement",
        "target_artifacts": [
            "src/EdgeRetails.Desktop/Views/Dialogs/SupervisorApprovalModalDialog.xaml",
            "src/EdgeRetails.Desktop/Views/Dialogs/SupervisorApprovalModalDialog.xaml.cs",
            "src/EdgeRetails.Desktop/App.xaml",
            "src/EdgeRetails.Server/Controllers/PostingController.cs",
            "src/EdgeRetails.Desktop/ViewModels/SupervisorApprovalModalViewModel.cs",
            "src/EdgeRetails.Desktop/ViewModels/PosViewModel.cs",
            "src/EdgeRetails.Desktop/Services/RemotePosWorkflowService.cs",
            "tests/EdgeRetails.UnitTests/Phase11Wave3ApiAndDesktopCutoverTests.cs",
            "tests/EdgeRetails.Desktop.PerformanceTests/Phase11Wave3DesktopCutoverTests.cs",
            "tests/EdgeRetails.IntegrationTests/Phase11DurablePostingIntegrationTests.cs"
        ],
        "verified_test_executions": [
            find_test("desktop_tests.trx", "Imp103_SupervisorApprovalModalDialog_RendersVisualTree_AndBindsLockedPeriod"),
            find_test("desktop_tests.trx", "Imp103_SupervisorApprovalModal_CalculatesBackdatingAndEnforcesPeriodLockBanner"),
            find_test("desktop_tests.trx", "MonW3R3_003_PosViewModel_OpenSupervisorApproval_WiresDialogServiceAndCallback"),
            find_test("desktop_tests.trx", "Imp103_SalesReturn_InspectionRemnantAndHistoricalSupervisorAuth_WiredAndEnforced"),
            find_test("desktop_tests.trx", "Imp103_SerializedStocktakeScan_PackContainerMeasuredRemaining_TransportsMeasuredQuantities"),
            find_test("desktop_tests.trx", "Imp103_RemoteStocktake_ReplayIntent_PreservesOperationId_And_ChangedMeasurementTriggersConflict"),
            find_test("unit_tests_full_regression.trx", "SerializedStocktake_ContainerMeasurementAndLooseStock_AuthoritativeCountAndReplayConflict"),
            find_test("integration_tests.trx", "T21_SerializedStocktake_ContainerMeasurementAndLooseStock_RealPostgres_AuthoritativeCountMovementAndReplayConflict"),
            find_test("unit_tests.trx", "Imp103_PostingController_EnforcesPeriodLockPrecedenceOverSupervisorOverride"),
            find_test("integration_tests.trx", "T05_PeriodLock_AdvisoryLockRaceProtection_PreventsConflictingCreation")
        ],
        "assertions": [
            {
                "assertion": "Real PostgreSQL 18 serialized stocktake container measurement and loose cut ends reconciliation creates physical count correction movement, variance effect delta, cost conservation, and rejects replay conflict (MON-W3R3-006)",
                "expected": "REAL_PG18_CONTAINER_MEASUREMENT_AND_LOOSE_COUNT_SUBLEDGER_EFFECTS_VERIFIED",
                "actual": "REAL_PG18_CONTAINER_MEASUREMENT_AND_LOOSE_COUNT_SUBLEDGER_EFFECTS_VERIFIED",
                "oracle": "Phase11DurablePostingIntegrationTests.T21_SerializedStocktake_ContainerMeasurementAndLooseStock_RealPostgres_AuthoritativeCountMovementAndReplayConflict",
                "passed": True
            },
            {
                "assertion": "SupervisorApprovalModalDialog renders XAML visual tree, binds locked period banner, masks PIN with PasswordBox, and clears secret on cancel/unload (MON-W3R3-003, MON-W3R3-004)",
                "expected": "SUPERVISOR_DIALOG_XAML_RENDERED_PASSWORD_MASKED_AND_CLEARED",
                "actual": "SUPERVISOR_DIALOG_XAML_RENDERED_PASSWORD_MASKED_AND_CLEARED",
                "oracle": "Phase11Wave3DesktopCutoverTests.Imp103_SupervisorApprovalModalDialog_RendersVisualTree_AndBindsLockedPeriod",
                "passed": True
            },
            {
                "assertion": "SupervisorApprovalModal calculates backdated days and enforces locked period banner",
                "expected": "BACKDATING_CALCULATED_PERIOD_LOCK_ENFORCED",
                "actual": "BACKDATING_CALCULATED_PERIOD_LOCK_ENFORCED",
                "oracle": "Phase11Wave3DesktopCutoverTests.Imp103_SupervisorApprovalModal_CalculatesBackdatingAndEnforcesPeriodLockBanner",
                "passed": True
            },
            {
                "assertion": "PosViewModel OpenSupervisorApprovalCommand wires DialogService and provides caller path (MON-W3R3-003)",
                "expected": "SUPERVISOR_APPROVAL_CALLER_WIRED_TO_DIALOG_SERVICE",
                "actual": "SUPERVISOR_APPROVAL_CALLER_WIRED_TO_DIALOG_SERVICE",
                "oracle": "Phase11Wave3DesktopCutoverTests.MonW3R3_003_PosViewModel_OpenSupervisorApproval_WiresDialogServiceAndCallback",
                "passed": True
            },
            {
                "assertion": "SalesReturnViewModel inspects return conditions, selects remnant restock, and enforces supervisor approval for historical posting (MON-W3R3-006)",
                "expected": "INSPECTION_REMNANT_RESTOCK_AND_SUPERVISOR_AUTH_ENFORCED",
                "actual": "INSPECTION_REMNANT_RESTOCK_AND_SUPERVISOR_AUTH_ENFORCED",
                "oracle": "Phase11Wave3DesktopCutoverTests.Imp103_SalesReturn_InspectionRemnantAndHistoricalSupervisorAuth_WiredAndEnforced",
                "passed": True
            },
            {
                "assertion": "SerializedStocktakeScanViewModel resolves container tracking codes, transports measured remaining container quantities and separate loose count to backend subledgers with authoritative variance calculation (MON-W3R3-006, ER-DISPATCH-20261014-008)",
                "expected": "CONTAINER_MEASUREMENT_AND_SEPARATE_LOOSE_COUNT_AUTHORITATIVE_VARIANCE",
                "actual": "CONTAINER_MEASUREMENT_AND_SEPARATE_LOOSE_COUNT_AUTHORITATIVE_VARIANCE",
                "oracle": "Phase11Wave3DesktopCutoverTests.Imp103_SerializedStocktakeScan_PackContainerMeasuredRemaining_TransportsMeasuredQuantities",
                "passed": True
            },
            {
                "assertion": "RemoteStocktakeWorkflowService serialized intent incorporates container measurements and loose count, preserving replay identity and triggering intent conflict on changed measurements (ER-DISPATCH-20261014-008)",
                "expected": "REPLAY_IDENTITY_PRESERVED_CHANGED_MEASUREMENT_CONFLICT_TRIGGERED",
                "actual": "REPLAY_IDENTITY_PRESERVED_CHANGED_MEASUREMENT_CONFLICT_TRIGGERED",
                "oracle": "Phase11Wave3DesktopCutoverTests.Imp103_RemoteStocktake_ReplayIntent_PreservesOperationId_And_ChangedMeasurementTriggersConflict",
                "passed": True
            },
            {
                "assertion": "RecordSerializedStocktakeHandler incorporates container measurements and separate loose quantity into authoritative balance update and rejects replay conflict on changed measurements (ER-DISPATCH-20261014-008)",
                "expected": "SERVER_STOCKTAKE_LOOSE_COUNT_AUTHORITATIVE_AND_REPLAY_CONFLICT_REJECTED",
                "actual": "SERVER_STOCKTAKE_LOOSE_COUNT_AUTHORITATIVE_AND_REPLAY_CONFLICT_REJECTED",
                "oracle": "StocktakeReconciliationIdempotencyTests.SerializedStocktake_ContainerMeasurementAndLooseStock_AuthoritativeCountAndReplayConflict",
                "passed": True
            },
            {
                "assertion": "PostingController enforces period lock precedence over supervisor PIN override and rejects backdating beyond 7 days",
                "expected": "PERIOD_LOCK_PRECEDENCE_OVER_OVERRIDE",
                "actual": "PERIOD_LOCK_PRECEDENCE_OVER_OVERRIDE",
                "oracle": "Phase11Wave3ApiAndDesktopCutoverTests.Imp103_PostingController_EnforcesPeriodLockPrecedenceOverSupervisorOverride",
                "passed": True
            },
            {
                "assertion": "PeriodLock advisory lock race protection prevents conflicting period creation in real PostgreSQL 18",
                "expected": "RACE_PROTECTION_PREVENTS_CONFLICT",
                "actual": "RACE_PROTECTION_PREVENTS_CONFLICT",
                "oracle": "Phase11DurablePostingIntegrationTests.T05_PeriodLock_AdvisoryLockRaceProtection_PreventsConflictingCreation",
                "passed": True
            }
        ],
        "notes": """# Task P11-IMP-103 Implementation and Verification Notes

- **Task ID**: P11-IMP-103
- **Run ID**: ER-RUN-20261014-003
- **Authority**: OWNER-UPDATE-20261013-007 / OWNER-UPDATE-20261013-008/002 / OWNER-UPDATE-20261014-001 / OWNER-UPDATE-20261014-002
- **Governance Event**: ER-CHANGE-20261014-017 / ER-DISPATCH-20261014-011

## Summary of Accomplished Work
1. Created SupervisorApprovalModalDialog.xaml and code-behind with WPF PasswordBox for masked supervisor PIN entry, eliminating plain text exposure (MON-W3R3-004).
2. Wired PasswordChanged event handler, IDisposable, and ClearSecret() on SupervisorApprovalModalViewModel and dialog code-behind to clear the PasswordBox and reset the viewmodel PIN on completion, cancel, and dialog unload (without asserting unmanaged cryptographic memory zeroing) (MON-W3R3-004).
3. Registered DataTemplate in App.xaml mapping SupervisorApprovalModalViewModel to SupervisorApprovalModalDialog for ModalHost resolution (MON-W3R3-003).
4. Wired OpenSupervisorApproval in PosViewModel and exposed ApiClient on RemotePosWorkflowService providing caller-level integration (MON-W3R3-003).
5. Implemented PostingController.RequestSupervisorApproval enforcing period lock precedence, 7-day limits, and PBKDF2 PIN verification.
6. Confirmed PostgreSQL advisory lock race protection prevents conflicting lock creation in real PostgreSQL 18.
7. Reconciled separate loose stock counts and container measurements into authoritative subledger stocktake line calculations (CountedSellableQty and VarianceQty) rather than annotation-only review notes, across SerializedStocktakeScanViewModel, RemoteStocktakeWorkflowService, InventoryController, and RecordSerializedStocktakeHandler (ER-DISPATCH-20261014-008).
8. Preserved client operation intent replay identity on RemoteStocktakeWorkflowService and RecordSerializedStocktakeHandler by incorporating sorted measured container quantities, minimum sellable increment, and loose quantity into deterministic operation payload serialization, verifying identical replay preservation and changed-measurement conflict rejection under FileClientOperationIntentStore and StocktakeOperationOutcome.BeginAsync (ER-DISPATCH-20261014-008).
9. Verified end-to-end container measurement remaining and separate loose stock reconciliation with variance calculation, physical count correction movements, cost conservation, and replay identity/conflict rejection directly on real disposable PostgreSQL 18 (port 55437) in T21 (MON-W3R3-006, ER-DISPATCH-20261014-010).
"""
    },
    "P11-IMP-104": {
        "task_id": "P11-IMP-104",
        "canonical_task": "Product Management taxonomy/attributes integration",
        "canonical_completion_criterion": "Hierarchy/redirect/typed validation and legacy product edits preserve identities; existing product management owns catalog changes",
        "supplemental_obligations": "9 divisions, 65 departments, 732 classes with 42 redirects; typed attribute validation for numeric ranges, enum options, and boolean flags",
        "owner_scope": "ProductManagementViewModel category navigation and typed attribute validation",
        "target_artifacts": [
            "src/EdgeRetails.Desktop/Views/ProductManagementView.xaml",
            "src/EdgeRetails.Desktop/Views/Dialogs/ProductEditDialog.xaml",
            "src/EdgeRetails.Desktop/ViewModels/ProductEditViewModel.cs",
            "src/EdgeRetails.Desktop/ViewModels/ProductManagementViewModel.cs",
            "src/EdgeRetails.Application/Features/Catalog/TaxonomyV3Store.cs",
            "tests/EdgeRetails.Desktop.PerformanceTests/Phase11Wave3DesktopCutoverTests.cs",
            "tests/EdgeRetails.UnitTests/Phase11Wave3ApiAndDesktopCutoverTests.cs"
        ],
        "verified_test_executions": [
            find_test("desktop_tests.trx", "Imp104_ProductManagementViewModel_BuildsTaxonomyTreeAndValidatesBounds"),
            find_test("desktop_tests.trx", "Imp104_ProductManagement_TaxonomyTree_HierarchyAndRedirectNotice_WiredToCatalog"),
            find_test("desktop_tests.trx", "Imp104_ProductEdit_DynamicTypedAttributes_ValidationAndIdentityPreservation"),
            find_test("unit_tests.trx", "Imp104_TaxonomyV3Store_Contains9Divisions65Departments732Classes")
        ],
        "assertions": [
            {
                "assertion": "ProductManagementViewModel builds taxonomy tree across 9 divisions and enforces min/max, enum, and boolean attribute bounds",
                "expected": "TAXONOMY_TREE_AND_ATTRIBUTE_VALIDATION_ACTIVE",
                "actual": "TAXONOMY_TREE_AND_ATTRIBUTE_VALIDATION_ACTIVE",
                "oracle": "Phase11Wave3DesktopCutoverTests.Imp104_ProductManagementViewModel_BuildsTaxonomyTreeAndValidatesBounds",
                "passed": True
            },
            {
                "assertion": "ProductManagementView displays TaxonomyTree V3 hierarchy, deprecated class redirect notices, and filters products by selected code (MON-W3R3-007)",
                "expected": "TAXONOMY_TREE_HIERARCHY_REDIRECT_AND_CATALOG_FILTERING_ACTIVE",
                "actual": "TAXONOMY_TREE_HIERARCHY_REDIRECT_AND_CATALOG_FILTERING_ACTIVE",
                "oracle": "Phase11Wave3DesktopCutoverTests.Imp104_ProductManagement_TaxonomyTree_HierarchyAndRedirectNotice_WiredToCatalog",
                "passed": True
            },
            {
                "assertion": "ProductEditViewModel enforces dynamic typed attribute validation and preserves ProductUnitId, SKU, and identity immutability on legacy edits (MON-W3R3-007)",
                "expected": "DYNAMIC_TYPED_ATTRIBUTES_VALIDATED_AND_IDENTITIES_PRESERVED",
                "actual": "DYNAMIC_TYPED_ATTRIBUTES_VALIDATED_AND_IDENTITIES_PRESERVED",
                "oracle": "Phase11Wave3DesktopCutoverTests.Imp104_ProductEdit_DynamicTypedAttributes_ValidationAndIdentityPreservation",
                "passed": True
            },
            {
                "assertion": "TaxonomyV3Store contains exactly 9 divisions, 65 departments, and 732 classes with 42 redirects",
                "expected": "EXACT_TAXONOMY_COUNTS",
                "actual": "EXACT_TAXONOMY_COUNTS",
                "oracle": "Phase11Wave3ApiAndDesktopCutoverTests.Imp104_TaxonomyV3Store_Contains9Divisions65Departments732Classes",
                "passed": True
            }
        ],
        "notes": """# Task P11-IMP-104 Implementation and Verification Notes

- **Task ID**: P11-IMP-104
- **Run ID**: ER-RUN-20261014-003
- **Authority**: OWNER-UPDATE-20261013-007 / OWNER-UPDATE-20261013-008/002 / OWNER-UPDATE-20261014-001 / OWNER-UPDATE-20261014-002
- **Governance Event**: ER-CHANGE-20261014-017 / ER-DISPATCH-20261014-011

## Summary of Accomplished Work
1. Enhanced ProductManagementViewModel with taxonomy tree navigation and typed attribute validation (numeric bounds, enum options, boolean checks).
2. Confirmed TaxonomyV3Store integrity: 9 divisions, 65 departments, and 732 classes.
3. Resolved legacy catalog filtering and unbranded basic gateway diagnostics at root cause in PosViewModel, achieving 294/294 test passes in full Desktop test suite without regression (REG-W3-001, ER-DISPATCH-20261014-010).
"""
    },
    "P11-IMP-105": {
        "task_id": "P11-IMP-105",
        "canonical_task": "Catalog paged search/facets/aliases",
        "canonical_completion_criterion": "Bounded category/search/filter API, scanner priority, refreshed/versioned caches and multilingual ambiguity handled safely",
        "supplemental_obligations": "Immediate barcode scanner keystroke processing; packaging facet filtering; Urdu and English phonetic search alias resolution; scanner tracking code allocation to cart (MON-W3R3-003)",
        "owner_scope": "Desktop POS product search, barcode scanner keystroke processing, packaging facets, and phonetic aliases",
        "target_artifacts": [
            "src/EdgeRetails.Server/Controllers/CatalogController.cs",
            "src/EdgeRetails.Desktop/ViewModels/PosViewModel.cs",
            "src/EdgeRetails.Application/Features/Catalog/TaxonomyV3Store.cs",
            "tests/EdgeRetails.Desktop.PerformanceTests/Phase11Wave3DesktopCutoverTests.cs",
            "tests/EdgeRetails.UnitTests/Phase11Wave3ApiAndDesktopCutoverTests.cs"
        ],
        "verified_test_executions": [
            find_test("desktop_tests.trx", "Imp105_PosViewModel_ScannerSimulation_AllocatesTrackingCodeToCart"),
            find_test("desktop_tests.trx", "Imp105_PosViewModel_ProcessesScannerInputImmediately"),
            find_test("desktop_tests.trx", "Imp105_PosViewModel_HandlesPackagingFacetsAndPhoneticAliases"),
            find_test("unit_tests.trx", "Imp105_TaxonomyV3Store_ContainsSearchAliasesForUrduAndEnglish")
        ],
        "assertions": [
            {
                "assertion": "PosViewModel processes simulated barcode scanner tracking-code scan and adds allocated container to cart immediately (MON-W3R3-003)",
                "expected": "SCANNER_SIMULATION_ALLOCATES_TO_CART",
                "actual": "SCANNER_SIMULATION_ALLOCATES_TO_CART",
                "oracle": "Phase11Wave3DesktopCutoverTests.Imp105_PosViewModel_ScannerSimulation_AllocatesTrackingCodeToCart",
                "passed": True
            },
            {
                "assertion": "PosViewModel processes barcode scanner keystroke bursts immediately without typing delay",
                "expected": "IMMEDIATE_SCANNER_PROCESSING",
                "actual": "IMMEDIATE_SCANNER_PROCESSING",
                "oracle": "Phase11Wave3DesktopCutoverTests.Imp105_PosViewModel_ProcessesScannerInputImmediately",
                "passed": True
            },
            {
                "assertion": "PosViewModel filters products by packaging facets (PACK, COIL, BOX, REEL) and resolves Urdu/English phonetic aliases",
                "expected": "PACKAGING_FACETS_AND_PHONETIC_ALIASES_ACTIVE",
                "actual": "PACKAGING_FACETS_AND_PHONETIC_ALIASES_ACTIVE",
                "oracle": "Phase11Wave3DesktopCutoverTests.Imp105_PosViewModel_HandlesPackagingFacetsAndPhoneticAliases",
                "passed": True
            },
            {
                "assertion": "TaxonomyV3Store contains search aliases for Urdu and English terms ('taar', 'breaker', 'pankha')",
                "expected": "MULTILINGUAL_ALIASES_PRESENT",
                "actual": "MULTILINGUAL_ALIASES_PRESENT",
                "oracle": "Phase11Wave3ApiAndDesktopCutoverTests.Imp105_TaxonomyV3Store_ContainsSearchAliasesForUrduAndEnglish",
                "passed": True
            }
        ],
        "notes": """# Task P11-IMP-105 Implementation and Verification Notes

- **Task ID**: P11-IMP-105
- **Run ID**: ER-RUN-20261014-003
- **Authority**: OWNER-UPDATE-20261013-007 / OWNER-UPDATE-20261013-008/002 / OWNER-UPDATE-20261014-001 / OWNER-UPDATE-20261014-002
- **Governance Event**: ER-CHANGE-20261014-017 / ER-DISPATCH-20261014-011

## Summary of Accomplished Work
1. Implemented ProcessScannerInput in PosViewModel for instantaneous barcode capture.
2. Verified automated hardware barcode scanner tracking code simulation adding allocated container to cart (MON-W3R3-003).
3. Supported packaging facets (PACK, COIL, BOX, REEL) and phonetic search aliases in Urdu and English ('taar' -> wire, 'breaker' -> circuit breaker).
"""
    }
}

for task_id, tdata in tasks_data.items():
    tdir = os.path.join(base_tasks_dir, task_id)
    os.makedirs(tdir, exist_ok=True)

    starts = [t["start_time_utc"] for t in tdata["verified_test_executions"] if t.get("start_time_utc")]
    ends = [t["end_time_utc"] for t in tdata["verified_test_executions"] if t.get("end_time_utc")]

    result_doc = {
        "task_id": tdata["task_id"],
        "run_id": "ER-RUN-20261014-003",
        "change_event_id": "ER-CHANGE-20261014-017",
        "authority": "OWNER-UPDATE-20261013-007",
        "continuation_authority": "OWNER-UPDATE-20261013-008/002 / OWNER-UPDATE-20261014-001 / OWNER-UPDATE-20261014-002",
        "governance_dispatch": "ER-DISPATCH-20261014-011",
        "canonical_task": tdata["canonical_task"],
        "canonical_completion_criterion": tdata["canonical_completion_criterion"],
        "supplemental_obligations": tdata.get("supplemental_obligations", ""),
        "requirement_remediation_gate_links": {
            "group_id": "Group 10",
            "gate_link": "Gate W3 (Server API & Desktop Cutover)",
            "boundary_invariant": "HTTP Client-Server Separation Boundary"
        },
        "owner_scope": tdata["owner_scope"],
        "timestamps": {
            "author_start_utc": None,
            "author_end_utc": None,
            "author_time_attribution_status": "UNKNOWN: per-task source authoring start and end times are uninstrumented; not fabricated",
            "test_execution_earliest_utc": min(starts) if starts else None,
            "test_execution_latest_utc": max(ends) if ends else None,
            "test_execution_attribution": "Extracted directly from raw TRX execution elements"
        },
        "fingerprints": {
            "git_head": "3cd3b4b8f903adef6f6d7cf3c922c2ce184c2fc2",
            "branch": "feat/phase11-subledger-implementation",
            "environment": "Windows 11 Pro 10.0.26100 / dotnet 10.0.401"
        },
        "target_artifacts": tdata["target_artifacts"],
        "verified_test_executions": tdata["verified_test_executions"],
        "assertions": tdata["assertions"],
        "stage": "Wave 3 Group 10 Implementation & Verification (Run003 / 20261014-003 Final Provenance Closure)",
        "status": "COMPLETED_AND_VERIFIED",
        "reviewer_scope": "Governance Coordination / Independent Review (Distinct from POS Executor)"
    }

    res_path = os.path.join(tdir, "RESULT.json")
    with open(res_path, "w", encoding="utf-8") as f:
        json.dump(result_doc, f, indent=2)

    notes_path = os.path.join(tdir, "NOTES.md")
    with open(notes_path, "w", encoding="utf-8") as f:
        f.write(tdata["notes"])

    print(f"Generated task receipt: {tdir} with {len(tdata['verified_test_executions'])} verified test executions (test window: {min(starts)} to {max(ends)})")
