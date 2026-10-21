path = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\01-local-evidence\docs\roadmap\phase11\EDGE_RETAILS_PHASE11_W0_PREEXECUTION_TASK_REGISTER_2026-10-10.md"
with open(path, "r", encoding="utf-8") as f:
    text = f.read()

import re
matches = re.findall(r"^(?:#+|\*|-)\s*(TASK-[^\n]+|CAT-[^\n]+)", text, re.MULTILINE)
print("Tasks found:", len(matches))
for m in matches[:25]:
    print(" ", m)
