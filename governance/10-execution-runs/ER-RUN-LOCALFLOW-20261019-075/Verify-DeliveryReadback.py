# Delivery Readback Verification for ER-RUN-LOCALFLOW-20261019-075 (Python)
import hashlib
import json
import sys
from pathlib import Path

delivery_dir = Path(__file__).parent.resolve()
manifest_path = delivery_dir / "EVIDENCE_MANIFEST.json"

if not manifest_path.exists():
    print(f"ERROR: Missing {manifest_path}")
    sys.exit(1)

with open(manifest_path, "r", encoding="utf-8") as f:
    manifest = json.load(f)

count = 0
for rel_path, meta in manifest["artifacts"].items():
    if rel_path == "EVIDENCE_MANIFEST.json":
        continue
    full_path = delivery_dir / rel_path
    if not full_path.exists():
        print(f"ERROR: Missing {rel_path}")
        sys.exit(1)
    actual_hash = hashlib.sha256(full_path.read_bytes()).hexdigest().lower()
    if actual_hash != meta["sha256"].lower():
        print(f"ERROR: Hash mismatch on {rel_path}: expected {meta['sha256']}, got {actual_hash}")
        sys.exit(1)
    count += 1

print(f"[PASS] All {count} delivered artifacts verified bit-for-bit against EVIDENCE_MANIFEST.json!")
print("=== DELIVERY READBACK VERIFICATION COMPLETE: 100% OK ===")
