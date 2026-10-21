import os
import json
import re

gov_root = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body"

# Load Task Register
task_reg_path = os.path.join(gov_root, r"governance\12-implementation-plan\2026-10-11\TASK_REGISTER.json")
with open(task_reg_path, "r", encoding="utf-8") as f:
    task_reg = json.load(f)["tasks"]

print(f"Loaded {len(task_reg)} canonical tasks.")

# Load TRX summary to get test details
trx_path = r"artifacts\governance_w4_run006\raw\trx_summary.json"
with open(trx_path, "r", encoding="utf-8") as f:
    trx_summary = json.load(f)

all_trx_tests = {}
for trx_file, v in trx_summary.items():
    if not trx_file.endswith(".trx"):
        continue
    for t in v["tests"]:
        all_trx_tests[t["test_name"]] = {
            "trx_file": trx_file,
            "test_id": t["test_id"],
            "execution_id": t["execution_id"],
            "duration": t["duration"],
            "outcome": t["outcome"]
        }
print(f"Loaded {len(all_trx_tests)} TRX test executions.")

# Load predecessor task receipts
runs = [
    ("W1", os.path.join(gov_root, r"governance\10-execution-runs\ER-RUN-20261011-005\tasks"), 1, 27),
    ("W2", os.path.join(gov_root, r"governance\10-execution-runs\ER-RUN-20261012-005\tasks"), 28, 97),
    ("W3", os.path.join(gov_root, r"governance\10-execution-runs\ER-RUN-20261014-003\tasks"), 98, 105),
    ("W4", os.path.join(gov_root, r"governance\10-execution-runs\ER-RUN-20261015-006\tasks"), 106, 119),
]

receipts = {}
for wave, tdir, start, end in runs:
    for i in range(start, end + 1):
        tid = f"P11-IMP-{i:03d}"
        rpath = os.path.join(tdir, tid, "RESULT.json")
        if not os.path.exists(rpath):
            rpath = os.path.join(tdir, f"{tid}.json")
        with open(rpath, "r", encoding="utf-8-sig") as f:
            receipts[tid] = json.load(f)

print(f"Loaded {len(receipts)} predecessor receipts.")
