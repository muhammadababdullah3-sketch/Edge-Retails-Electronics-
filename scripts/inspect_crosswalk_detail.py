import json
from pathlib import Path

ev = Path(r"C:\p11rb005\evidence")
d = json.loads((ev / "phase11-closure-reconciliation-and-crosswalk-20261023-001.json").read_text(encoding="utf-8"))

for k, v in d.items():
    if isinstance(v, dict):
        print(f"Key: {k} (dict with {len(v)} keys)")
    elif isinstance(v, list):
        print(f"Key: {k} (list with {len(v)} items)")
    else:
        print(f"Key: {k} = {v}")

if "canonical_crosswalk" in d:
    cw = d["canonical_crosswalk"]
    print("\nCanonical Crosswalk sections:", list(cw.keys()))
