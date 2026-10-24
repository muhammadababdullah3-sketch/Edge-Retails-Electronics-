import json
with open(r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\12-implementation-plan\2026-10-11\TASK_REGISTER.json", "r", encoding="utf-8") as f:
    d = json.load(f)
for t in d["tasks"][119:125]:
    print("---", t["TaskId"], "---")
    for k, v in t.items():
        print(f"  {k}: {v}")
