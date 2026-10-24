import os
import json

runs = [
    ("W1", r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-20261011-005\tasks", 1, 27),
    ("W2", r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-20261012-005\tasks", 28, 97),
    ("W3", r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-20261014-003\tasks", 98, 105),
    ("W4", r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-20261015-006\tasks", 106, 119),
]

for wave, tdir, start, end in runs:
    task_id = f"P11-IMP-{start:03d}"
    rpath = os.path.join(tdir, task_id, "RESULT.json")
    with open(rpath, "r", encoding="utf-8-sig") as f:
        d = json.load(f)
    print(f"--- {wave} {task_id} keys ---")
    for k, v in d.items():
        if isinstance(v, (list, dict)):
            print(f"  {k}: {type(v).__name__} len={len(v)}")
        else:
            print(f"  {k}: {str(v)[:70]}")
