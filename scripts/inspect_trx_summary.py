import json

with open("artifacts/governance_w3/raw/trx_summary.json", encoding="utf-8") as f:
    data = json.load(f)

print("=== UNIT TESTS (16) ===")
for t in data["unit_tests.trx"]["tests"]:
    print(f"  {t['test_name']}")
    print(f"    testId: {t['test_id']}")
    print(f"    executionId: {t['execution_id']}")

print("\n=== DESKTOP TESTS (10) ===")
for t in data["desktop_tests.trx"]["tests"]:
    print(f"  {t['test_name']}")
    print(f"    testId: {t['test_id']}")
    print(f"    executionId: {t['execution_id']}")

print("\n=== INTEGRATION TESTS (20) ===")
for t in data["integration_tests.trx"]["tests"]:
    print(f"  {t['test_name']}")
    print(f"    testId: {t['test_id']}")
    print(f"    executionId: {t['execution_id']}")
