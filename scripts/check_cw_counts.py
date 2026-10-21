import json

path = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-20261011-005\ORIGINAL_REQUIREMENT_CROSSWALK.json"
with open(path, "r", encoding="utf-8") as f:
    cw = json.load(f)

print("Original requirements:", len(cw.get("original_requirements", [])))
print("Original remediation tasks:", len(cw.get("original_remediation_tasks", [])))
print("Original capability gates:", len(cw.get("original_capability_gates", [])))
print("Original final21 gates:", len(cw.get("original_final21_gates", [])))
print("Canonical planning tasks:", len(cw.get("canonical_planning_tasks", [])))
print("Planned test obligations:", len(cw.get("planned_test_obligations", [])))
