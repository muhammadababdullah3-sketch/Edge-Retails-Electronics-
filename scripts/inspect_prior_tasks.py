import os
import json

runs = [
    ("W1", r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-20261011-005\tasks", 1, 27),
    ("W2", r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-20261012-005\tasks", 28, 97),
    ("W3", r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-20261014-003\tasks", 98, 105),
    ("W4", r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-20261015-006\tasks", 106, 119),
]

all_tasks = {}
for wave, tdir, start, end in runs:
    for i in range(start, end + 1):
        task_id = f"P11-IMP-{i:03d}"
        tpath = os.path.join(tdir, task_id)
        rpath = os.path.join(tpath, "RESULT.json")
        if not os.path.exists(rpath):
            rpath = os.path.join(tdir, f"{task_id}.json")
        if os.path.exists(rpath):
            with open(rpath, "r", encoding="utf-8-sig") as f:
                d = json.load(f)
                all_tasks[task_id] = (wave, d)
        else:
            print(f"MISSING: {task_id} in {tdir}")

print(f"Total collected tasks from prior runs: {len(all_tasks)} of 119")
