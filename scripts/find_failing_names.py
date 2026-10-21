import json

with open("artifacts/governance_w4/raw/trx_summary.json", encoding="utf-8") as f:
    s = json.load(f)

for trx in ["safety_guard_pg_tests.trx", "unit_tests.trx"]:
    print(f"=== {trx} ===")
    for t in s[trx]["tests"]:
        name = t["test_name"]
        if any(k in name for k in ["Attest", "Custody", "Thaka", "SupplierPosting", "Forensic", "PurchasingAndKhata"]):
            print(" ", name)
