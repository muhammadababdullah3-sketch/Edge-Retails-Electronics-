import os
import json

gov_root = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body"
rpath = os.path.join(gov_root, r"governance\10-execution-runs\ER-RUN-20261014-003\tasks\P11-IMP-098\RESULT.json")
with open(rpath, "r", encoding="utf-8-sig") as f:
    d = json.load(f)

print("W3 P11-IMP-098 keys:", list(d.keys()))
for k, v in d.items():
    print(f"  {k}: {type(v).__name__} = {str(v)[:100]}")
