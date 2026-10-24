import os
import json

path = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-20261015-006\raw\trx_summary.json"
with open(path, "r", encoding="utf-8") as f:
    trx = json.load(f)

print("TRX summary files:")
for k, v in trx.items():
    print(f"  {k}: {v['count']} tests")

all_tests = {}
for k, v in trx.items():
    for t in v["tests"]:
        all_tests[t["test_name"]] = (k, t["execution_id"], t["test_id"])

print(f"Total unique test names: {len(all_tests)}")
