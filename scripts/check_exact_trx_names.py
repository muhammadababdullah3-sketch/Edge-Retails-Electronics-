import json

trx_path = r"artifacts\governance_w4_run006\raw\trx_summary.json"
with open(trx_path, "r", encoding="utf-8") as f:
    trx_summary = json.load(f)

for trx_file in ["phase11_pg_tests.trx", "safety_guard_pg_tests.trx", "unit_tests.trx"]:
    tests = trx_summary[trx_file]["tests"]
    print(f"--- {trx_file} ---")
    for t in tests[:3]:
        print(" ", t["test_name"])
