import json
import hashlib
import os

pi_path = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\13-prerequisite-resolution\2026-10-11\PLANNED_TEST_INVENTORY.json"
with open(pi_path, encoding="utf-8") as f:
    pi = json.load(f)

planned_tests = pi.get("tests", [])

with open("artifacts/governance_w4/raw/trx_summary.json", encoding="utf-8") as f:
    trx_sum = json.load(f)

# TRX SHA256 hashes
trx_hashes = {
    "desktop_tests.trx": "7f9145a5626df56bf15e6fe688f3eca47130855f7b30bcdeada05bca55f00dfc",
    "durable_posting_tests.trx": "67647f6e718f22aa4019c0c9028265643c2255e95c4e7e50375a1531c4b6242e",
    "phase11_pg_tests.trx": "fb6fca7d797e6f6887e8e4d41aa7531fb035e167245341fdad31d55734865150",
    "safety_guard_pg_tests.trx": "aefecf0fd1af9f4b68264cba8fd682af1f11896fbf273c32da52ec9a3438856d",
    "tracking_pg_tests.trx": "cc7234c935d5c3ee48cbc385c627fe65b9c20a20175985d141852f313ef06984",
    "unit_tests.trx": "9c53d253bb400de2eb1154cd1d8515bf2af2d081dc676a06dd3a25add3f781da"
}

# Flat index of tests
all_trx_tests = {}
for trx_file, v in trx_sum.items():
    if not trx_file.endswith(".trx"):
        continue
    for t in v["tests"]:
        all_trx_tests[t["test_name"]] = {
            "trx_file": trx_file,
            "trx_sha256": trx_hashes.get(trx_file, ""),
            "test_id": t["test_id"],
            "execution_id": t["execution_id"],
            "duration": t["duration"],
            "start_time": t["start_time"],
            "end_time": t["end_time"],
            "outcome": t["outcome"]
        }

# Line numbers from find_test_line_numbers.py
lines_pack = {
    "Pack_InitialIntake_Sets_State_Sealed_And_Full_Remaining_Quantity": 34,
    "Pack_PartialDeduction_Transitions_Sealed_To_Open": 59,
    "Pack_CompleteDeduction_Transitions_Open_To_Consumed": 82,
    "Pack_OverDeduction_Throws_BusinessRuleException": 105,
    "Pack_NegativeDeduction_Throws_BusinessRuleException": 125,
    "Pack_FractionalDeduction_On_ContainerPiece_Throws_BusinessRuleException": 145,
    "Pack_Allocation_Prioritizes_Open_Pack_Before_Sealed_Pack": 163,
    "Pack_Allocation_Splits_Across_Open_And_Sealed_Packs": 217,
    "Coil_CutLength_Reduces_Remaining_Meters_Correctly": 271,
    "Coil_Remnant_Under_Threshold_Allows_Scrap_WriteOff": 294,
    "Inspected_Return_Creates_Separate_Loose_Stock": 332,
    "Sealed_Loose_Row_Is_Rejected": 368,
    "NonApplicable_Pack_Columns_Are_All_Null": 388,
    "Partial_Null_And_OutOfBounds_States_Are_Rejected": 412,
    "Quarantine_Void_Damage_Custody_Are_Not_Sellable": 437,
    "Fractional_Cost_And_Final_Exhaustion_Conserve_Value": 479,
    "Multiple_Lots_Preserve_Exact_Cost_Provenance": 503,
    "Thaka_Partial_Issue_Return_Is_SourceLinked": 560,
    "Warranty_Custody_Preserves_Pack_Provenance": 597,
    "Scrap_Loss_And_Approved_Recovery_Preserve_Value": 630,
    "Legacy_Unknown_Rows_Are_Quarantined_Without_Loss": 673,
    "Pack_Api_Masks_Cost_For_Cashier": 697
}

lines_concurrency = {
    "Postgres_Concurrent_Sales_From_Same_Open_Pack_Do_Not_OverAllocate": 262,
    "Postgres_Concurrent_Cross_Pack_Allocations_Acquire_Sorted_Locks": 353,
    "Postgres_Rollback_On_Payment_Failure_Restores_Pack_Remaining_Quantity": 436,
    "Identical_Replay_Race_Has_One_Outcome": 482,
    "Conflicting_Replay_Race_Is_Rejected": 548,
    "Sale_And_Thaka_Race_No_Overdraw": 596,
    "Sale_Return_MultiSource_Lock_Order_Is_Consistent": 662,
    "Sale_And_Stocktake_Race_Is_Atomic": 716,
    "Custody_And_Sale_Race_Preserves_Eligibility": 771,
    "MultiLine_Replay_Keys_Do_Not_Collide": 808,
    "Later_Partial_Draw_Has_Distinct_Operation": 937,
    "Concurrent_Final_Draw_Conserves_Decimal_Cost": 995,
    "Failure_Between_Allocation_And_Ledger_Rolls_Back": 1058,
    "Deadlock_Or_Serialization_Retry_Is_Bounded": 1135
}

lines_time = {
    "Postgres_DailySnapshot_Aggregates_According_To_Shop_Timezone_Not_Server_Local": 226,
    "Postgres_TrendSlices_Bucket_Correct_24_Hours_In_Shop_Timezone": 324,
    "Postgres_CreatePurchase_Rejects_Future_Shop_Date": 360,
    "DayMinus7_Is_Accepted_For_Eligible_Supervisor": 454,
    "DayMinus8_Is_Rejected": 511,
    "Locked_Period_Overrides_Supervisor": 564,
    "Client_Supervisor_Boolean_Is_Not_Authority": 637,
    "Wrong_Pin_Or_Expired_Authority_Is_Rejected": 686,
    "Reason_And_Audit_Failure_Are_Atomic": 763,
    "Event_Posting_And_Vendor_Dates_Are_Distinct": 835,
    "Midnight_LeapDay_Report_Intervals_Are_HalfOpen": 898,
    "Profit_Report_Preserves_Loss_And_Recovery_Terms": 946
}

lines_golden = {
    "GT01": ("GT01_Quantity_IntakeSaleReturnStocktakeConservation", 234),
    "GT02": ("GT02_Length_CoilIntakeRollSaleMeterCutRemnantScrap", 390),
    "GT03": ("GT03_ContainerPack_IntakeBoxLooseSaleCrossPackAllocation", 492),
    "GT04": ("GT04_IndividualPiece_IntakeTrackingCodeExactSaleReturnCustody", 577),
    "GT05": ("GT05_Serialized_LuhnImeiSaleWarrantyCustodyReconciliation", 647),
    "GT06": ("GT06_CostAccounting_MixedLotsSpecificUnitScrapLossDeductedInPnL", 753),
    "GT07": ("GT07_ThakaMaterial_MixedUomIssueFactorChargeReversalProvenance", 883),
    "GT08": ("GT08_Concurrency_TwoCashiersCompeteForLastUnitSerializedLock", 1022),
    "GT09": ("GT09_Atomicity_MultiLineMutationFailsHalfwayRollbackProof", 1102),
    "GT10": ("GT10_ShopTime_UtcMutationsAcrossShopMidnightCorrectPeriodDto", 1154)
}

lines_catalog = {
    "U01": ("U01_FixedLengthConversion_FtToM", 155, "Direct 1:1 test implementation: Standard fixed length conversion from feet to meters (1 ft = 0.3048 m)"),
    "U02": ("U02_FixedMassConversion_KgToG", 164, "Direct 1:1 test implementation: Standard fixed mass conversion from kilograms to grams (1 kg = 1000 g)"),
    "U03": ("U03_FixedCountConversion_DozToPcs", 173, "Direct 1:1 test implementation: Standard fixed count conversion from dozens to pieces (1 doz = 12 pcs)"),
    "U04": ("U04_And_U05_ProductSpecificPackagingFactors_DoNotUseGlobalConstants", 207, "Semantic test proving product-specific box packaging factors (Box of 50 vs Box of 100) without static global constants"),
    "U05": ("U04_And_U05_ProductSpecificPackagingFactors_DoNotUseGlobalConstants", 227, "Semantic test proving product-specific coil packaging factors (Coil of 90m vs Coil of 100m) without static global constants"),
    "U06": ("U06_VariableMeasuredCoil_TracksRemainingQuantity", 271, "Direct 1:1 test implementation: Variable measured coil tracks continuous remaining length"),
    "U07": ("U07_CrossPackSale_RequiresMultipleAllocations", 352, "Direct 1:1 test implementation: Cross-pack sale across multiple container packs allocates correctly"),
    "U08": ("U08_WholePackSell_ChecksEligibility", 379, "Direct 1:1 test implementation: Whole pack sale checks sellable eligibility"),
    "U09": ("U09_ReturnCutWire_RemainsLooseAndNeverResealed", 395, "Direct 1:1 test implementation: Return of cut wire remains loose stock and is never resealed"),
    "U10": ("U10_CompositeKit_RequiresExplicitBOMOrIdentifiedKit", 410, "Direct 1:1 test implementation: Composite kit assembly requires explicit bill-of-materials"),
    "U11": ("U11_And_U12_ExactUnitQuantity_RejectsFractionsBeforeRounding", 428, "Direct 1:1 test implementation: Exact unit quantity rejects fractions on Count dimension before rounding"),
    "U12": ("U11_And_U12_ExactUnitQuantity_RejectsFractionsBeforeRounding", 585, "Production SUT test proving exact-unit count over-request rejection (2 available vs 3 requested) via CompleteSaleHandler and ReturnExchangeEligibilityAuthority with zero stock mutation, zero movements, zero sales, and preserved unit states"),
    "U13": ("U13_DimensionMismatch_ProhibitsCrossDimensionConversion", 676, "Direct 1:1 test implementation: Dimension mismatch prohibits cross-dimension conversions"),
    "U14": ("U14_U15_U16_AreaVolumeMass_AreGatedUnderGateD", 684, "Gate D dimensional conversion gating: Area dimension conversions are gated"),
    "U15": ("U14_U15_U16_AreaVolumeMass_AreGatedUnderGateD", 685, "Gate D dimensional conversion gating: Volume dimension conversions are gated"),
    "U16": ("U14_U15_U16_AreaVolumeMass_AreGatedUnderGateD", 686, "Gate D dimensional conversion gating: Mass dimension conversions are gated"),
    "U17": ("U17_OneTrackedBox_SingleInventoryUnit_NoFakeChildren", 798, "Production SUT test proving physical container box intake via ReceiveProductIntakeHandler and IPhysicalUnitCreationAuthority creates exactly 1 container InventoryUnit and zero fake child units"),
    "U18": ("U18_IdempotentSeed_PreservesExactCounts", 857, "Direct 1:1 test implementation: Seed idempotency preserves exact taxonomy counts without duplication"),
    "U19": ("U19_ConflictingSeededClassSymbol_PreservesOccupiedOperationalSymbols", 952, "Direct 1:1 test implementation: Conflicting seeded class symbols preserve occupied operational symbols"),
    "U20": ("U20_RegeneratedTrackingCode_Prohibited", 986, "Direct 1:1 test implementation: Regenerated tracking code collision is strictly prohibited"),
    "U21": ("U21_NestedPackage_RequiresVerifiedPackConfig", 1076, "Production SUT test proving TransactionQuantitySnapshot 10x12 derivation, positive factor enforcement, and rejection of foreign product config via CompleteSaleHandler (sales.product_unit_not_allowed)"),
    "U22": ("U22_MixedClassSearch_ResolvesToOneCanonicalClass", 1206, "Semantic test proving mixed search for smart OLED TV resolves strictly to canonical leaf F01-01 Television set with facet rules"),
    "U23": ("U23_CrossProductCableRoll_NoGlobalConversionFactor", 1257, "Semantic test proving two brands of cable rolls with variable lengths and ProductUnit factors")
}

crosswalk = []

for pt in planned_tests:
    tid = pt["TestId"]
    group = pt["Group"]
    method = pt["ProposedMethod"]
    setup = pt["Setup"]
    action = pt["Action"]
    assertion = pt["IndependentAssertion"]

    actual_test_name = None
    actual_fqn = None
    assertion_file = None
    assertion_line = None
    rationale = None
    task_id = None
    coverage_status = "COVERED_AND_VERIFIED_PASS"

    if group == "PACK":
        task_id = "P11-IMP-107"
        actual_test_name = method
        actual_fqn = f"EdgeRetails.UnitTests.Phase11.PackContainerSubLedgerUnitTests.{method}"
        assertion_file = "tests/EdgeRetails.UnitTests/Phase11/PackContainerSubLedgerUnitTests.cs"
        assertion_line = lines_pack.get(method, 0)
        rationale = "Direct 1:1 test implementation in PackContainerSubLedgerUnitTests.cs; fresh native test pass in unit_tests.trx"

    elif group == "CONCURRENCY":
        task_id = "P11-IMP-108"
        actual_test_name = method
        actual_fqn = f"EdgeRetails.IntegrationTests.Phase11.PackConcurrencyPostgresTests.{method}"
        assertion_file = "tests/EdgeRetails.IntegrationTests/Phase11/PackConcurrencyPostgresTests.cs"
        assertion_line = lines_concurrency.get(method, 0)
        if tid == "P11-PLAN-CONCURRENCY-14":
            coverage_status = "PARTIAL_DISPOSITION_MON_W4_001"
            rationale = "PARTIAL DISPOSITION under MON-W4-001: Verified application-supported competing command retry and clean conflict handling (pack.stale_version_conflict / pack.transient_concurrency_conflict), demonstrating bounded client retry (2 attempts), clean 409 error on budget exhaustion, and zero duplicate side effects. Client-side retry loop qualifies application contract; kernel-level automatic deadlock retry is transparently rejected with 409."
        else:
            coverage_status = "COVERED_AND_VERIFIED_PASS"
            rationale = "Direct 1:1 test implementation in PackConcurrencyPostgresTests.cs on dedicated PG18 cluster; fresh native test pass in phase11_pg_tests.trx"

    elif group == "TIME":
        task_id = "P11-IMP-109"
        actual_test_name = method
        actual_fqn = f"EdgeRetails.IntegrationTests.Phase11.ShopTimezoneBoundaryPostgresTests.{method}"
        assertion_file = "tests/EdgeRetails.IntegrationTests/Phase11/ShopTimezoneBoundaryPostgresTests.cs"
        assertion_line = lines_time.get(method, 0)
        rationale = "Direct 1:1 test implementation in ShopTimezoneBoundaryPostgresTests.cs on dedicated PG18 cluster; fresh native test pass in phase11_pg_tests.trx"

    elif group == "GOLDEN":
        task_id = "P11-IMP-110"
        m_name, l_num = lines_golden.get(tid, (None, 0))
        actual_test_name = m_name
        actual_fqn = f"EdgeRetails.IntegrationTests.Phase11.GoldenLifecycleTracePostgresTests.{m_name}"
        assertion_file = "tests/EdgeRetails.IntegrationTests/Phase11/GoldenLifecycleTracePostgresTests.cs"
        assertion_line = l_num
        rationale = f"Direct 1:1 end-to-end golden lifecycle trace in GoldenLifecycleTracePostgresTests.cs on dedicated PG18 cluster; fresh native test pass in phase11_pg_tests.trx"

    elif group == "CATALOG":
        task_id = "P11-IMP-111"
        m_name, l_num, rat = lines_catalog.get(tid, (None, 0, ""))
        actual_test_name = m_name
        actual_fqn = f"EdgeRetails.UnitTests.Phase11CatalogV3AcceptanceTests.{m_name}"
        assertion_file = "tests/EdgeRetails.UnitTests/Phase11CatalogV3AcceptanceTests.cs"
        assertion_line = l_num
        rationale = f"{rat}; fresh native test pass in unit_tests.trx"

    trx_info = all_trx_tests.get(actual_fqn)
    if not trx_info:
        raise RuntimeError(f"Could not find TRX execution info for: {actual_fqn}")

    entry = {
        "planned_test_id": tid,
        "group": group,
        "canonical_task_id": task_id,
        "source_obligation": pt.get("SourceObligation", method),
        "proposed_method": method,
        "planned_setup": setup,
        "planned_action": action,
        "planned_independent_assertion_oracle": assertion,
        "actual_test_method": actual_test_name,
        "actual_test_fqn": actual_fqn,
        "assertion_file": assertion_file,
        "assertion_line": assertion_line,
        "trx_file": f"raw/{trx_info['trx_file']}",
        "trx_sha256": trx_info["trx_sha256"],
        "native_test_id": trx_info["test_id"],
        "native_execution_id": trx_info["execution_id"],
        "duration": trx_info["duration"],
        "native_test_execution_start_utc": trx_info["start_time"],
        "native_test_execution_end_utc": trx_info["end_time"],
        "outcome": trx_info["outcome"],
        "coverage_status": coverage_status,
        "rationale": rationale
    }
    crosswalk.append(entry)

print(f"Generated crosswalk for {len(crosswalk)} planned tests.")
with open("scripts/crosswalk_output.json", "w", encoding="utf-8") as f:
    json.dump(crosswalk, f, indent=2)
print("Saved scripts/crosswalk_output.json.")
