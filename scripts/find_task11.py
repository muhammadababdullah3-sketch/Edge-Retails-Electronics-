import os
import re

gov_root = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance"
matches = set()

for root, dirs, files in os.walk(gov_root):
    for f in files:
        if f.endswith(".md") or f.endswith(".json"):
            fpath = os.path.join(root, f)
            try:
                with open(fpath, "r", encoding="utf-8-sig") as fp:
                    content = fp.read()
                    found = re.findall(r"TASK-11[A-I]-\d+", content)
                    matches.update(found)
            except Exception:
                pass

print("Unique TASK-11 tasks found:", sorted(matches))
