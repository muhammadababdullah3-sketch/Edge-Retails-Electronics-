import os
import json

gov_root = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body"
w3_tasks_dir = os.path.join(gov_root, r"governance\10-execution-runs\ER-RUN-20261014-003\tasks")
for i in range(98, 106):
    tid = f"P11-IMP-{i:03d}"
    with open(os.path.join(w3_tasks_dir, tid, "RESULT.json"), "r", encoding="utf-8-sig") as f:
        d = json.load(f)
    tests = d.get("verified_test_executions", [])
    src = d.get("target_artifacts", [])
    print(f"{tid}: status={d.get('status')}, tests={len(tests)}, src={len(src)}")
