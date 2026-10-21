import json

with open("artifacts/governance_w4/raw/trx_summary.json", encoding="utf-8") as f:
    data = json.load(f)

for trx_name, val in data.items():
    print(f"=== {trx_name} ===")
    for t in val["tests"]:
        tname = t["test_name"]
        for kw in ["U02", "GT01", "GT02", "GT03", "GT04", "GT05", "GT06", "GT07", "GT10", "Pack", "Adjustment", "Lot", "Cost", "Loss", "Remnant"]:
            if kw.lower() in tname.lower():
                print(f"  [{kw}] {tname}")
                break
