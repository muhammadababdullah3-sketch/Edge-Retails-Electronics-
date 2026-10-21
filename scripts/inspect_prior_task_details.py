import os
import json

gov_root = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body"

runs = [
    ("W1", os.path.join(gov_root, r"governance\10-execution-runs\ER-RUN-20261011-005\tasks"), 1, 27),
    ("W2", os.path.join(gov_root, r"governance\10-execution-runs\ER-RUN-20261012-005\tasks"), 28, 97),
    ("W3", os.path.join(gov_root, r"governance\10-execution-runs\ER-RUN-20261014-003\tasks"), 98, 105),
    ("W4", os.path.join(gov_root, r"governance\10-execution-runs\ER-RUN-20261015-006\tasks"), 106, 119),
]

for wave, tdir, start, end in runs:
    for i in range(start, end + 1):
        tid = f"P11-IMP-{i:03d}"
        rpath = os.path.join(tdir, tid, "RESULT.json")
        if not os.path.exists(rpath):
            rpath = os.path.join(tdir, f"{tid}.json")
        with open(rpath, "r", encoding="utf-8-sig") as f:
            d = json.load(f)

        # Check source paths
        src = d.get("source_paths") or d.get("target_artifacts") or d.get("affected_files") or d.get("evidence_paths") or []
        # Check tests
        tests = d.get("verified_test_executions") or []
        native_proof = d.get("native_proof")

        # print summary for a few
        if i in [1, 27, 28, 97, 98, 105, 106, 108, 119]:
            print(f"{wave} {tid}: src_type={type(src).__name__} len={len(src) if isinstance(src, list) else 1}, tests_len={len(tests)}, native_proof={bool(native_proof)}")
