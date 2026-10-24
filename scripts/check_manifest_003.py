import json
path = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-20261014-003\delivery\EVIDENCE_MANIFEST.json"
with open(path, "r", encoding="utf-8") as f:
    d = json.load(f)
print("EVIDENCE_MANIFEST keys:", list(d.keys()))
for k, v in d.items():
    if isinstance(v, (list, dict)):
        print(f"  {k}: {type(v).__name__} len={len(v)}")
    else:
        print(f"  {k}: {v}")
