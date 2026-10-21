import json

with open("artifacts/governance_w4/raw/trx_summary.json", encoding="utf-8") as f:
    d = json.load(f)

unit_tests = d["unit_tests.trx"]["tests"]

print(f"Total unit tests: {len(unit_tests)}")

keywords = ["CatalogV3", "PackContainer", "StockAdjustment", "CostMasking", "AllocatePack", "Remnant", "TrackingMode"]
for kw in keywords:
    print(f"\n--- Matches for '{kw}' ---")
    matches = [t["test_name"] for t in unit_tests if kw.lower() in t["test_name"].lower()]
    for m in matches[:10]:
        print(f"  {m}")
    if len(matches) > 10:
        print(f"  ... and {len(matches) - 10} more")
