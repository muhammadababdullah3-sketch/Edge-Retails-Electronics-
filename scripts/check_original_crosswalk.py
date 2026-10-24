import json
with open(r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\12-implementation-plan\2026-10-11\ORIGINAL_REQUIREMENT_CROSSWALK.json", "r", encoding="utf-8") as f:
    d = json.load(f)
print("Crosswalk keys:", list(d.keys()))
for k in d.keys():
    v = d[k]
    print(f"  {k}: type={type(v).__name__}, len={len(v) if isinstance(v, (list, dict)) else 0}")
