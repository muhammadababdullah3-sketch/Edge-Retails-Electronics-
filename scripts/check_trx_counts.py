import json
with open(r"artifacts\governance_w4_run006\raw\trx_summary.json", "r", encoding="utf-8") as f:
    d = json.load(f)
total = 0
for k, v in d.items():
    print(f"{k}: count={v['count']}, passed={v.get('passed', v['count'])}")
    total += v['count']
print(f"Total: {total}")
