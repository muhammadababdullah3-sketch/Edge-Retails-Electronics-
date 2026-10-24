import json
with open(r"artifacts\governance_w4_run006\W4_DELIVERY_INDEX.json", "r", encoding="utf-8") as f:
    d = json.load(f)

print("W4_DELIVERY_INDEX keys:", list(d.keys()))
for k, v in d.items():
    if isinstance(v, (list, dict)):
        print(f"  {k}: {type(v).__name__} len={len(v)}")
    else:
        print(f"  {k}: {v}")
