import os
import json

gov_root = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body"
w1_tasks_dir = os.path.join(gov_root, r"governance\10-execution-runs\ER-RUN-20261011-005\tasks")
for i in [1, 10, 20, 27]:
    tid = f"P11-IMP-{i:03d}"
    with open(os.path.join(w1_tasks_dir, tid, "RESULT.json"), "r", encoding="utf-8-sig") as f:
        d = json.load(f)
    print(f"\n{tid}: status={d.get('status')}")
    print("  evidence_paths:", d.get("evidence_paths", [])[:2])
    print("  assertions:", d.get("assertions", [])[:1])
