import os
import json

path = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-20261011-005\ORIGINAL_REQUIREMENT_CROSSWALK.json"

with open(path, "r", encoding="utf-8") as f:
    cw = json.load(f)

print("Keys:", list(cw.keys()))
for k in ["original_requirements", "original_remediation_tasks", "original_capability_gates", "original_final21_gates", "canonical_planning_tasks"]:
    items = cw.get(k, [])
    print(f"{k}: {len(items)} items")
    if items:
        print("  Sample item keys:", list(items[0].keys()))
        print("  Sample item:", {k: v for k, v in items[0].items() if k in ["Id", "TaskId", "CanonicalTaskId", "Title", "Name", "Task"]})
