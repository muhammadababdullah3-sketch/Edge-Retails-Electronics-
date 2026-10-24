import os
import json

path = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-20261015-006\review\PLANNED_81_CROSSWALK.json"
with open(path, "r", encoding="utf-8") as f:
    cw = json.load(f)

tests = cw["tests"]
print(f"Total planned tests in PLANNED_81_CROSSWALK: {len(tests)}")
print("Sample test:", tests[0])
