import json

with open("artifacts/governance_w4/raw/trx_summary.json", encoding="utf-8") as f:
    d = json.load(f)

for fname in ["tracking_pg_tests.trx", "durable_posting_tests.trx", "safety_guard_pg_tests.trx"]:
    print(f"\n=== {fname} ===")
    for t in d[fname]["tests"]:
        print(f"  {t['test_name']}")
