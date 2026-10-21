import json
import hashlib
from pathlib import Path

run14_dir = Path(r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-20261017-014")
m_p = run14_dir / "EVIDENCE_MANIFEST.json"
manifest = json.loads(m_p.read_text(encoding="utf-8"))

errors = []
for entry in manifest["artifacts"]:
    rel_path = entry["path"]
    expected_sha = entry["sha256"]
    actual_file = run14_dir / rel_path
    if not actual_file.exists():
        errors.append(f"Missing file: {rel_path}")
        continue
    h = hashlib.sha256()
    with open(actual_file, "rb") as f:
        while chunk := f.read(65536):
            h.update(chunk)
    actual_sha = h.hexdigest().lower()
    if actual_sha != expected_sha:
        errors.append(f"Hash mismatch for {rel_path}: {actual_sha} vs {expected_sha}")

if errors:
    print("ERRORS FOUND:")
    for e in errors:
        print(e)
else:
    count = len(manifest["artifacts"])
    print(f"ALL {count} ARTIFACTS VERIFIED MATCHING EXACT HASHES!")
