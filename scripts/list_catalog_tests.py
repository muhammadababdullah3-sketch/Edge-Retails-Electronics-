import json

with open("artifacts/governance_w4/raw/trx_summary.json", encoding="utf-8") as f:
    d = json.load(f)

for t in d["unit_tests.trx"]["tests"]:
    if "Phase11CatalogV3AcceptanceTests" in t["test_name"]:
        print(t["test_name"])
