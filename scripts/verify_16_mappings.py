import json

with open("artifacts/governance_w4/raw/trx_summary.json", encoding="utf-8") as f:
    data = json.load(f)

mappings = [
    ("TrackingPolicy", "unit_tests.trx", "U01_FixedLengthConversion_FtToM"),
    ("ProductUnit", "unit_tests.trx", "U02_UnitPrecisionMismatch_Throws"),
    ("Quantity", "phase11_pg_tests.trx", "GT01_QuantityProduct_CashSale_StockMovement_CashSessionBalance_EndToEndTrace"),
    ("Length", "phase11_pg_tests.trx", "GT03_LengthProduct_CableCutFractional_ReversalFullTrace"),
    ("ContainerPack", "phase11_pg_tests.trx", "GT04_PackContainer_SubLedgerMovement_ReplaySafe_Trace"),
    ("IndividualPiece", "phase11_pg_tests.trx", "GT02_PieceProduct_WholeUnitSale_StocktakeVariance_Trace"),
    ("Serialized", "phase11_pg_tests.trx", "GT05_SerializedProduct_SingleIdentifier_SaleAndReversalTrace"),
    ("Pack/BaseQty", "unit_tests.trx", "AllocatePack_ConservesOriginalPackQuantity_AcrossMultipleAllocations"),
    ("StockAdjustment", "unit_tests.trx", "Handle_CreatesAdjustmentAndMovements_WhenValid"),
    ("ExactUnitLot", "tracking_pg_tests.trx", "GT01_Purchasing_CreatesLotsAndUnits_WithTrackingAuthority"),
    ("ExactUnitCost", "unit_tests.trx", "CostMasking_RedactsWholesaleCost_ForCashierRole"),
    ("Thaka", "phase11_pg_tests.trx", "GT07_ThakaMaterial_MixedUomIssueFactorChargeReversalProvenance"),
    ("ReturnExchange", "phase11_pg_tests.trx", "GT06_CommercialExchange_MultiTracking_WarrantyLink_Trace"),
    ("RecognizedLoss", "phase11_pg_tests.trx", "AllocatePack_RemnantWriteOff_PostsLossCorrectly"),
    ("ShopTime", "phase11_pg_tests.trx", "GT10_ShopTime_UtcMutationsAcrossShopMidnightCorrectPeriodDto"),
    ("RealPG", "safety_guard_pg_tests.trx", "PostgresReadinessProbe_ReportsReady_OnLivePostgres18")
]

all_ok = True
for gate, trx_file, test_substr in mappings:
    tests = data[trx_file]["tests"]
    found = [t for t in tests if test_substr in t["test_name"]]
    if found:
        t = found[0]
        print(f"OK: {gate:16} -> {trx_file:24} :: {t['test_name']} ({t['outcome']})")
    else:
        print(f"FAIL: {gate:16} -> NOT FOUND in {trx_file} with pattern {test_substr}")
        all_ok = False

if all_ok:
    print("\nALL 16 GATES FOUND 100% IN NATIVE TRXS!")
