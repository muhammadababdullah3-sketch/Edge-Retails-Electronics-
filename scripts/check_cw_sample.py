import json

path = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-20261011-005\ORIGINAL_REQUIREMENT_CROSSWALK.json"
with open(path, "r", encoding="utf-8") as f:
    cw = json.load(f)

print("R01 mapping sample:")
for r in cw.get("original_remediation_tasks", [])[:5]:
    print(" ", r["Id"], r.get("MappedPrerequisiteIds"))

print("CAP-01 mapping sample:")
for c in cw.get("original_capability_gates", [])[:5]:
    print(" ", c["Id"], c.get("Title"))

print("GATE01 sample:")
for g in cw.get("original_final21_gates", [])[:5]:
    print(" ", g["Id"], g.get("Title"))
