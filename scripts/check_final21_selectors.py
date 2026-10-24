import json

with open("artifacts/governance_w4/raw/trx_summary.json", encoding="utf-8") as f:
    d = json.load(f)

selectors = [
    ("GATE01", "unit_tests.trx", "FullyQualifiedName~Phase11", "Phase11"),
    ("GATE02", "desktop_tests.trx", "FullyQualifiedName~Phase11Wave3DesktopCutoverTests", "Phase11Wave3DesktopCutoverTests"),
    ("GATE03", "phase11_pg_tests.trx", "FullyQualifiedName~Phase11.", "Phase11."),
    ("GATE04", "phase11_pg_tests.trx", "FullyQualifiedName~GoldenLifecycleTracePostgresTests", "GoldenLifecycleTracePostgresTests"),
    ("GATE05", "unit_tests.trx", "FullyQualifiedName~PackContainerSubLedgerUnitTests", "PackContainerSubLedgerUnitTests"),
    ("GATE06", "unit_tests.trx", "FullyQualifiedName~Phase1BProductIdentityAuthorityTests", "Phase1BProductIdentityAuthorityTests"),
    ("GATE07", "unit_tests.trx", "FullyQualifiedName~Phase2PurchasingAndKhataBehavioralTests", "Phase2PurchasingAndKhataBehavioralTests"),
    ("GATE08", "safety_guard_pg_tests.trx", "FullyQualifiedName~Phase3ProductionSafetyPostgresTests", "Phase3ProductionSafetyPostgresTests"),
    ("GATE09", "unit_tests.trx", "FullyQualifiedName~Phase4UnknownOutcomeAndRevalidationTests", "Phase4UnknownOutcomeAndRevalidationTests"),
    ("GATE10", "desktop_tests.trx", "FullyQualifiedName~Phase234AsyncReadTests", "Phase234AsyncReadTests"),
    ("GATE11", "tracking_pg_tests.trx", "FullyQualifiedName~Tracking", "Tracking"),
    ("GATE12", "safety_guard_pg_tests.trx", "FullyQualifiedName~CustodyBinding", "CustodyBinding"),
    ("GATE13", "unit_tests.trx", "FullyQualifiedName~Phase11WarrantyAndCustodyBehavioralTests", "Phase11WarrantyAndCustodyBehavioralTests"),
    ("GATE14", "unit_tests.trx", "FullyQualifiedName~Phase2PurchasingAndKhataBehavioralTests", "Phase2PurchasingAndKhataBehavioralTests"),
    ("GATE15", "unit_tests.trx", "All UnitTests", ""),
    ("GATE19", "safety_guard_pg_tests.trx", "FullyQualifiedName~EFModelSnapshot_HasZeroPendingModelChanges_And_MatchesAppliedMigrations", "EFModelSnapshot_HasZeroPendingModelChanges_And_MatchesAppliedMigrations"),
    ("GATE20", "safety_guard_pg_tests.trx", "FullyQualifiedName~MigrationHistory_VerifiesAllHistoricalAndPhase11MigrationHashes", "MigrationHistory_VerifiesAllHistoricalAndPhase11MigrationHashes")
]

for gate_id, trx_file, sel_display, substring in selectors:
    tests = d[trx_file]["tests"]
    if substring:
        matching = [t for t in tests if substring in t["test_name"]]
    else:
        matching = tests
    passed = [t for t in matching if t["outcome"] == "Passed"]
    print(f"{gate_id:8} | {trx_file:24} | Total matched: {len(matching):4} | Passed: {len(passed):4} | Selector: {sel_display}")
