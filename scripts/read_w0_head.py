path = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\01-local-evidence\docs\roadmap\phase11\EDGE_RETAILS_PHASE11_W0_PREEXECUTION_TASK_REGISTER_2026-10-10.md"
with open(path, "r", encoding="utf-8") as f:
    lines = f.readlines()
print(f"Total lines: {len(lines)}")
for l in lines[:40]:
    print(l.rstrip())
