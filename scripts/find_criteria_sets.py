import os
import sys
from pathlib import Path

gov_root = Path(r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body")
evidence_root = Path(r"C:\p11rb005\evidence")

def search_files(root, pattern):
    found = []
    for p in root.rglob("*"):
        if p.is_file() and p.suffix in [".json", ".md", ".txt"]:
            try:
                txt = p.read_text(encoding="utf-8", errors="ignore")
                if pattern in txt:
                    found.append((p, txt.count(pattern)))
            except Exception:
                pass
    return found

print("=== Search for R01-R72 or R01 ===")
for p, c in search_files(evidence_root, "R01")[:5]:
    print(f"  {p.name}: count {c}")
for p, c in search_files(gov_root / "governance", "R01")[:5]:
    print(f"  {p.relative_to(gov_root)}: count {c}")

print("\n=== Search for CAT-01 or CAT-07 ===")
for p, c in search_files(evidence_root, "CAT-01")[:5]:
    print(f"  {p.name}: count {c}")

print("\n=== Search for ER-RRV-01 or ER-RRV-15 ===")
for p, c in search_files(evidence_root, "ER-RRV-01")[:5]:
    print(f"  {p.name}: count {c}")

print("\n=== Search for 125 supplemental or P11-IMP ===")
for p, c in search_files(evidence_root, "P11-IMP")[:5]:
    print(f"  {p.name}: count {c}")
