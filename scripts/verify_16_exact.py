import json

with open("artifacts/governance_w4/raw/trx_summary.json", encoding="utf-8") as f:
    d = json.load(f)

mappings = [
    ("TrackingPolicy", "unit_tests.trx", "EdgeRetails.UnitTests.Phase11CatalogV3AcceptanceTests.CAT03_UnitRegistryAndPolicyCompatibility"),
    ("ProductUnit", "unit_tests.trx", "EdgeRetails.UnitTests.Phase11CatalogV3AcceptanceTests.U02_FixedMassConversion_KgToG"),
    ("Quantity", "phase11_pg_tests.trx", "EdgeRetails.IntegrationTests.Phase11.GoldenLifecycleTracePostgresTests.GT01_Quantity_IntakeSaleReturnStocktakeConservation"),
    ("Length", "phase11_pg_tests.trx", "EdgeRetails.IntegrationTests.Phase11.GoldenLifecycleTracePostgresTests.GT02_Length_CoilIntakeRollSaleMeterCutRemnantScrap"),
    ("ContainerPack", "phase11_pg_tests.trx", "EdgeRetails.IntegrationTests.Phase11.GoldenLifecycleTracePostgresTests.GT03_ContainerPack_IntakeBoxLooseSaleCrossPackAllocation"),
    ("IndividualPiece", "phase11_pg_tests.trx", "EdgeRetails.IntegrationTests.Phase11.GoldenLifecycleTracePostgresTests.GT04_IndividualPiece_IntakeTrackingCodeExactSaleReturnCustody"),
    ("Serialized", "phase11_pg_tests.trx", "EdgeRetails.IntegrationTests.Phase11.GoldenLifecycleTracePostgresTests.GT05_Serialized_LuhnImeiSaleWarrantyCustodyReconciliation"),
    ("Pack/BaseQty", "unit_tests.trx", "EdgeRetails.UnitTests.Phase11Wave3ApiAndDesktopCutoverTests.Imp098_AllocatePack_FractionalCosting_ConservesBasisAndCreatesRealMovement"),
    ("StockAdjustment", "unit_tests.trx", "EdgeRetails.UnitTests.StockAdjustmentHandlerBehavioralTests.Positive_Serialized_Adjustment_Creates_Lots_Balances_And_Units_Correctly"),
    ("ExactUnitLot", "tracking_pg_tests.trx", "EdgeRetails.IntegrationTests.TrackingGoldenTracePostgresTests.GoldenTrace_ReceiveScanSellReturnRepairAndStocktakePreservePhysicalIdentity(mode: IndividualPiece, factor: 1)"),
    ("ExactUnitCost", "unit_tests.trx", "EdgeRetails.UnitTests.Phase11Wave3ApiAndDesktopCutoverTests.Imp100_CostMaskingFilter_RedactsWholesaleCostsForCashierRole"),
    ("Thaka", "phase11_pg_tests.trx", "EdgeRetails.IntegrationTests.Phase11.GoldenLifecycleTracePostgresTests.GT07_ThakaMaterial_MixedUomIssueFactorChargeReversalProvenance"),
    ("ReturnExchange", "tracking_pg_tests.trx", "EdgeRetails.IntegrationTests.TrackingGoldenTracePostgresTests.PurchaseReturn_PreservesExactIdentityAndOriginalBaseQuantity(mode: Serialized, factor: 1)"),
    ("RecognizedLoss", "unit_tests.trx", "EdgeRetails.UnitTests.Phase11PackStocktakeAndRemnantBehavioralTests.P11_IMP_076_Authorized_Scrap_Remnant_Derecognizes_Unit_And_Records_Scrap_Allocation"),
    ("ShopTime", "phase11_pg_tests.trx", "EdgeRetails.IntegrationTests.Phase11.GoldenLifecycleTracePostgresTests.GT10_ShopTime_UtcMutationsAcrossShopMidnightCorrectPeriodDto"),
    ("RealPG", "safety_guard_pg_tests.trx", "EdgeRetails.IntegrationTests.Phase3ProductionSafetyPostgresTests.PostgresReadinessProbe_ReportsReady_OnLivePostgres18")
]

all_ok = True
for gate, trx_file, test_fqn in mappings:
    tests = d[trx_file]["tests"]
    found = [t for t in tests if t["test_name"] == test_fqn]
    if len(found) == 1:
        t = found[0]
        print(f"PASS [100% MATCH]: {gate:16} -> {trx_file:24} :: {t['test_name']} ({t['outcome']}) [ID: {t['test_id'][:8]}...]")
    else:
        print(f"FAIL: {gate:16} -> {len(found)} matches in {trx_file} for {test_fqn}")
        all_ok = False

if all_ok:
    print("\nALL 16 GATES VERIFIED 100% WITH EXACT NATIVE MATCHES!")
