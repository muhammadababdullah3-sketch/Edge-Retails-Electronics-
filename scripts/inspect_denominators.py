import json
from pathlib import Path

ev = Path(r"C:\p11rb005\evidence")
d = json.loads((ev / "phase11-closure-reconciliation-and-crosswalk-20261023-001.json").read_text(encoding="utf-8"))

print("=== DENOMINATORS ===")
print(json.dumps(d["denominators"], indent=2))

print("\n=== SUPPLEMENTAL NAMESPACES ===")
print(json.dumps(d["supplemental_namespaces"], indent=2))
