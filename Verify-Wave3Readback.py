#!/usr/bin/env python3
import sys, json, hashlib
from pathlib import Path

RUN12_DIR = Path(r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-20261017-012")
MANIFEST_PATH = RUN12_DIR / "EVIDENCE_MANIFEST.json"

def sha256_file(p: Path) -> str:
    return hashlib.sha256(p.read_bytes()).hexdigest()

def main():
    print(f"Verifying readback for {RUN12_DIR.name}...")
    if not MANIFEST_PATH.exists():
        print(f"FAIL: {MANIFEST_PATH} not found")
        sys.exit(1)

    manifest = json.loads(MANIFEST_PATH.read_text(encoding="utf-8"))
    artifacts = manifest.get("artifacts", [])
    print(f"Checking {len(artifacts)} manifested artifacts against disk...")

    errors = 0
    checked = 0
    for item in artifacts:
        p = RUN12_DIR / item["path"]
        if not p.exists():
            print(f"MISSING: {item['path']}")
            errors += 1
            continue
        actual_sha = sha256_file(p)
        if actual_sha != item["sha256"]:
            print(f"MISMATCH: {item['path']} (expected {item['sha256']}, got {actual_sha})")
            errors += 1
        else:
            checked += 1

    if errors == 0:
        print(f"PASS: All {checked} artifacts match EVIDENCE_MANIFEST.json with 100% SHA-256 equality.")
        sys.exit(0)
    else:
        print(f"FAIL: {errors} errors found during readback verification.")
        sys.exit(1)

if __name__ == "__main__":
    main()
