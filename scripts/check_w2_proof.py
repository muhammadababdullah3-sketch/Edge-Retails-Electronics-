import os
import json

gov_root = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body"
rpath = os.path.join(gov_root, r"governance\10-execution-runs\ER-RUN-20261012-005\tasks\P11-IMP-028\RESULT.json")
with open(rpath, "r", encoding="utf-8-sig") as f:
    d = json.load(f)
print("native_proof:", d.get("native_proof"))
print("code_symbols:", d.get("code_symbols"))
print("keys:", list(d.keys()))
