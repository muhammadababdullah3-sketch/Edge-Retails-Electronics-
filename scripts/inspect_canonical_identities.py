import json
from pathlib import Path

ev = Path(r"C:\p11rb005\evidence")
d = json.loads((ev / "phase11-canonical-identities-20261023-001.json").read_text(encoding="utf-8"))

print("Capability gate names (16):")
for g in d["capability_gate_names"]:
    print(" ", g)

print("\nFinal21 identities (21):")
for f in d["final21_identities"]:
    print(" ", f)
