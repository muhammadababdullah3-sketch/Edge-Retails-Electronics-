import os
import re

gov_root = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance"
matches = {}

for root, dirs, files in os.walk(gov_root):
    for f in files:
        if f.endswith(".md") or f.endswith(".json"):
            fpath = os.path.join(root, f)
            try:
                with open(fpath, "r", encoding="utf-8-sig") as fp:
                    content = fp.read()
                    found = re.findall(r"ER-RRV-?\d+", content)
                    if found:
                        matches[os.path.relpath(fpath, gov_root)] = set(found)
            except Exception:
                pass

print(f"Files with ER-RRV references: {len(matches)}")
for p, fset in sorted(matches.items())[:15]:
    print(f"  {p}: {sorted(fset)}")
