import os
import json

gov_root = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body"
w2_tasks_dir = os.path.join(gov_root, r"governance\10-execution-runs\ER-RUN-20261012-005\tasks")
for i in [28, 50, 75, 97]:
    tid = f"P11-IMP-{i:03d}"
    with open(os.path.join(w2_tasks_dir, tid, "RESULT.json"), "r", encoding="utf-8-sig") as f:
        d = json.load(f)
    print(f"{tid}: status={d.get('w2_execution_status')}, native_proof={d.get('native_proof')[:60] if d.get('native_proof') else None}")
