import os
import json

gov_root = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body"
task_reg_path = os.path.join(gov_root, r"governance\12-implementation-plan\2026-10-11\TASK_REGISTER.json")

with open(task_reg_path, "r", encoding="utf-8") as f:
    task_reg = json.load(f)

tasks = task_reg["tasks"]
print(f"Loaded {len(tasks)} canonical tasks from canonical TASK_REGISTER.json")

# Check all TaskIds
ids = [t["TaskId"] for t in tasks]
print(f"First 5: {ids[:5]}, Last 5: {ids[-5:]}")
assert len(ids) == 125
for i in range(1, 126):
    expected_id = f"P11-IMP-{i:03d}"
    assert ids[i-1] == expected_id, f"Mismatch at index {i-1}: expected {expected_id}, got {ids[i-1]}"
print("All 125 TaskIds match canonical P11-IMP-001 to P11-IMP-125 sequentially!")
