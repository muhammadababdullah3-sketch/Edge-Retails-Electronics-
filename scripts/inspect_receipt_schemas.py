import os
import json

gov_root = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body"

runs = [
    ("W1", os.path.join(gov_root, r"governance\10-execution-runs\ER-RUN-20261011-005\tasks"), 1),
    ("W2", os.path.join(gov_root, r"governance\10-execution-runs\ER-RUN-20261012-005\tasks"), 28),
    ("W3", os.path.join(gov_root, r"governance\10-execution-runs\ER-RUN-20261014-003\tasks"), 98),
    ("W4", os.path.join(gov_root, r"governance\10-execution-runs\ER-RUN-20261015-006\tasks"), 106),
]

for wave, tdir, tid_num in runs:
    tid = f"P11-IMP-{tid_num:03d}"
    rpath = os.path.join(tdir, tid, "RESULT.json")
    if not os.path.exists(rpath):
        rpath = os.path.join(tdir, f"{tid}.json")
    with open(rpath, "r", encoding="utf-8-sig") as f:
        d = json.load(f)
    print(f"\n--- {wave} {tid} ---")
    for k in ["TaskId", "Task", "Status", "source_paths", "affected_files", "verified_test_executions", "tests", "native_proof", "primary_evidence", "execution_id"]:
        if k in d:
            v = d[k]
            if isinstance(v, list):
                print(f"  {k}: list of {len(v)} items")
            elif isinstance(v, dict):
                print(f"  {k}: dict with keys {list(v.keys())}")
            else:
                print(f"  {k}: {v}")
