import json

tr_path = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\12-implementation-plan\2026-10-11\TASK_REGISTER.json"
with open(tr_path, encoding="utf-8") as f:
    tr_doc = json.load(f)

tasks = tr_doc.get("tasks", [])
print(f"TASK_REGISTER has {len(tasks)} tasks:")
for t in tasks:
    tid = t.get("TaskId", "")
    if tid.startswith("P11-IMP-10") or tid.startswith("P11-IMP-11") or tid.startswith("P11-IMP-12"):
        print(f"{tid}:")
        print(f"  Task:                {t.get('Task')}")
        print(f"  CompletionCriterion: {t.get('CompletionCriterion')}")
        print(f"  Keys:                {list(t.keys())}")

pi_path = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\13-prerequisite-resolution\2026-10-11\PLANNED_TEST_INVENTORY.json"
with open(pi_path, encoding="utf-8") as f:
    pi = json.load(f)

print("\nPLANNED_TEST_INVENTORY:")
print("Type:", type(pi))
if isinstance(pi, dict):
    print("Keys:", list(pi.keys()))
    for k in pi:
        val = pi[k]
        if isinstance(val, list):
            print(f"  Key '{k}': list of {len(val)} items")
            if val:
                print(f"    Sample item in '{k}':", json.dumps(val[0], indent=2))
        elif isinstance(val, dict):
            print(f"  Key '{k}': dict with keys {list(val.keys())[:5]}")
elif isinstance(pi, list):
    print(f"List of {len(pi)} items. Sample: {json.dumps(pi[0], indent=2)}")
