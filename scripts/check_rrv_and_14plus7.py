import json

path = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-20261011-005\ORIGINAL_REQUIREMENT_CROSSWALK.json"
with open(path, "r", encoding="utf-8") as f:
    cw = json.load(f)

reqs = cw.get("original_requirements", [])
print(f"Total original requirements: {len(reqs)}")
req_ids = [r["Id"] for r in reqs]
print("Req IDs:", req_ids)

# Check ER-RRV findings
task_reg_path = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\12-implementation-plan\2026-10-11\TASK_REGISTER.json"
with open(task_reg_path, "r", encoding="utf-8") as f:
    treg = json.load(f)

print("\nScanning TASK_REGISTER for ER-RRV:")
errvs = set()
for t in treg["tasks"]:
    for field in ["Task", "CompletionCriterion", "Title", "Description"]:
        val = t.get(field, "")
        if "ER-RRV" in val:
            import re
            matches = re.findall(r"ER-RRV-?\d+", val)
            errvs.update(matches)
print(f"Found ER-RRV references in TASK_REGISTER: {sorted(errvs)}")
