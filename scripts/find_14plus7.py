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
                    if "14+7" in content or "14 + 7" in content or "original 14" in content:
                        matches[os.path.relpath(fpath, gov_root)] = True
            except Exception:
                pass

print(f"Files mentioning 14+7 or original 14: {len(matches)}")
for p in sorted(matches.keys())[:15]:
    print(f"  {p}")
