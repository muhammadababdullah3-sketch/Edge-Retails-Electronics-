import os
import json

gov_root = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body"

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
            data = json.load(f)
            receipts[tid] = data

print(f"Loaded {len(receipts)} predecessor task receipts.")
