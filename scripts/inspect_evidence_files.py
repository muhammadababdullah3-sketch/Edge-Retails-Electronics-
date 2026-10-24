import json
from pathlib import Path

ev = Path(r"C:\p11rb005\evidence")

def inspect_json(name):
    p = ev / name
    if p.exists():
        d = json.loads(p.read_text(encoding="utf-8"))
        print(f"=== {name} ===")
        if isinstance(d, dict):
            print("  Keys:", list(d.keys())[:10])
            for k in list(d.keys())[:5]:
                val = d[k]
                if isinstance(val, list):
                    print(f"    {k}: list of len {len(val)}")
                elif isinstance(val, dict):
                    print(f"    {k}: dict with keys {list(val.keys())[:5]}")
                else:
                    print(f"    {k}: {str(val)[:80]}")
        elif isinstance(d, list):
            print(f"  List of len {len(d)}")

inspect_json("phase11-canonical-identities-20261023-001.json")
inspect_json("phase11-closure-reconciliation-and-crosswalk-20261023-001.json")
inspect_json("phase11-final21-identity-evidence-join-20261023-001.json")
inspect_json("phase11-p11imp-direct-test-identity-join-20261023-002.json")
