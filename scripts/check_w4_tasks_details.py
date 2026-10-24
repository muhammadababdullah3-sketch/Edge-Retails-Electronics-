import os
import json

gov_root = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body"

runs = [
    ("W1", os.path.join(gov_root, r"governance\10-execution-runs\ER-RUN-20261011-005\tasks"), 1, 27),
    ("W2", os.path.join(gov_root, r"governance\10-execution-runs\ER-RUN-20261012-005\tasks"), 28, 97),
    ("W3", os.path.join(gov_root, r"governance\10-execution-runs\ER-RUN-20261014-003\tasks"), 98, 105),
    ("W4", os.path.join(gov_root, r"governance\10-execution-runs\ER-RUN-20261015-006\tasks"), 106, 119),
]

# load canonical task register
with open(os.path.join(gov_root, r"governance\12-implementation-plan\2026-10-11\TASK_REGISTER.json"), "r", encoding="utf-8") as f:
    task_reg = json.load(f)

print(f"Total canonical tasks in register: {len(task_reg['tasks'])}")

# Check W4 tasks 106-119
w4_tasks_dir = os.path.join(gov_root, r"governance\10-execution-runs\ER-RUN-20261015-006\tasks")
for i in range(106, 120):
    tid = f"P11-IMP-{i:03d}"
    with open(os.path.join(w4_tasks_dir, tid, "RESULT.json"), "r", encoding="utf-8-sig") as f:
        d = json.load(f)
    tests = d.get("verified_test_executions", [])
    print(f"{tid}: status={d.get('task_status')}, verified_tests_count={len(tests)}")
