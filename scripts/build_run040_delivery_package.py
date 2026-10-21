"""Generate delivery manifests and readback verification scripts for ER-RUN-MON-W4-20261018-040."""
import hashlib
import json
import os
from datetime import datetime, timezone
from pathlib import Path

RUN_DIR = Path(r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-MON-W4-20261018-040")
BUNDLE_HASH = "c2475b92d3120db301de87461cb70838efe3285b7ce65808d766f3551af0cce9"

EXCLUDE_FROM_PAYLOAD = {
    "DELIVERY_INDEX.json",
    "EVIDENCE_MANIFEST.json",
    "Verify-DeliveryReadback.py",
    "Verify-DeliveryReadback.ps1"
}

def sha256_file(p: Path) -> str:
    h = hashlib.sha256()
    with open(p, "rb") as f:
        while chunk := f.read(65536):
            h.update(chunk)
    return h.hexdigest().lower()

# Gather all files
payload_files = []
evidence_files = []

for root, _, filenames in os.walk(RUN_DIR):
    for fn in filenames:
        if fn in EXCLUDE_FROM_PAYLOAD:
            continue
        full_p = Path(root) / fn
        rel_p = full_p.relative_to(RUN_DIR).as_posix()
        size_b = full_p.stat().st_size
        sha = sha256_file(full_p)

        payload_files.append({
            "path": rel_p,
            "sha256": sha,
            "size_bytes": size_b
        })

        # Role name from filename
        role_name = rel_p.replace("/", "_").replace(".", "_").replace("-", "_").upper()
        evidence_files.append({
            "path": rel_p,
            "sha256": sha,
            "size_bytes": size_b,
            "role": role_name
        })

payload_files.sort(key=lambda x: x["path"])
evidence_files.sort(key=lambda x: x["path"])

now_utc = datetime.now(timezone.utc).isoformat()

# 1. DELIVERY_INDEX.json
delivery_index = {
    "artifact_id": "DELIVERY_INDEX",
    "run_id": "ER-RUN-MON-W4-20261018-040",
    "authority": "OWNER-UPDATE-20261018-004",
    "timestamp_utc": now_utc,
    "total_payload_files": len(payload_files),
    "candidate_bundle_hash": BUNDLE_HASH,
    "delivery_status": "READY_FOR_ROOT_REVIEW",
    "payload_files": payload_files
}

index_file = RUN_DIR / "DELIVERY_INDEX.json"
index_file.write_text(json.dumps(delivery_index, indent=2), encoding="utf-8")
print(f"[OK] Wrote {index_file} ({len(payload_files)} payload files)")

# 2. EVIDENCE_MANIFEST.json
evidence_manifest = {
    "schema_version": 1,
    "run_id": "ER-RUN-MON-W4-20261018-040",
    "authority": "OWNER-UPDATE-20261018-004",
    "timestamp_utc": now_utc,
    "total_hashed_files": len(evidence_files),
    "candidate_bundle_hash": BUNDLE_HASH,
    "files": evidence_files
}

manifest_file = RUN_DIR / "EVIDENCE_MANIFEST.json"
manifest_file.write_text(json.dumps(evidence_manifest, indent=2), encoding="utf-8")
print(f"[OK] Wrote {manifest_file} ({len(evidence_files)} files)")

# 3. Verify-DeliveryReadback.py
readback_py = f'''# Verify-DeliveryReadback.py
# Independent 100% SHA-256 verification of all delivered payloads in ER-RUN-MON-W4-20261018-040
import os
import sys
import json
import hashlib
from pathlib import Path

script_dir = Path(__file__).parent.resolve()
manifest_path = script_dir / "DELIVERY_INDEX.json"

if not manifest_path.exists():
    print(f"Error: DELIVERY_INDEX.json not found at {{manifest_path}}")
    sys.exit(1)

with open(manifest_path, "r", encoding="utf-8") as f:
    manifest = json.load(f)

payloads = manifest.get("payload_files", [])
print(f"Verifying {{len(payloads)}} delivered payload files for {{manifest.get('run_id')}}...")

mismatches = []
missing = []
passed = 0

for item in payloads:
    rel_path = item["path"]
    expected_sha = item["sha256"].lower()
    expected_size = item["size_bytes"]
    full_path = script_dir / rel_path

    if not full_path.exists():
        missing.append(rel_path)
        continue

    actual_size = full_path.stat().st_size
    if actual_size != expected_size:
        mismatches.append(f"{{rel_path}}: Size mismatch (expected {{expected_size}}, got {{actual_size}})")
        continue

    h = hashlib.sha256()
    with open(full_path, "rb") as f:
        while chunk := f.read(65536):
            h.update(chunk)
    actual_sha = h.hexdigest().lower()

    if actual_sha != expected_sha:
        mismatches.append(f"{{rel_path}}: SHA256 mismatch (expected {{expected_sha}}, got {{actual_sha}})")
    else:
        passed += 1

print(f"Results: {{passed}} passed, {{len(mismatches)}} mismatches, {{len(missing)}} missing.")

if mismatches or missing:
    if missing:
        print("Missing files:")
        for m in missing:
            print(f"  MISSING: {{m}}")
    if mismatches:
        print("Hash/size mismatches:")
        for m in mismatches:
            print(f"  MISMATCH: {{m}}")
    sys.exit(1)
else:
    print("SUCCESS: 100% bit-for-bit verified against DELIVERY_INDEX.json.")
    sys.exit(0)
'''

readback_py_file = RUN_DIR / "Verify-DeliveryReadback.py"
readback_py_file.write_text(readback_py, encoding="utf-8")
print(f"[OK] Wrote {readback_py_file}")

# 4. Verify-DeliveryReadback.ps1
readback_ps1 = f'''# Verify-DeliveryReadback.ps1
# Independent 100% SHA-256 verification of all delivered payloads in ER-RUN-MON-W4-20261018-040
$ErrorActionPreference = "Stop"
$scriptDir = $PSScriptRoot
$manifestPath = Join-Path $scriptDir "DELIVERY_INDEX.json"

if (-not (Test-Path $manifestPath)) {{
    Write-Error "DELIVERY_INDEX.json not found at $manifestPath"
    exit 1
}}

$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$payloads = $manifest.payload_files
Write-Host "Verifying $($payloads.Count) delivered payload files for $($manifest.run_id)..." -ForegroundColor Cyan

$passed = 0
$failed = 0

foreach ($item in $payloads) {{
    $fullPath = Join-Path $scriptDir $item.path
    if (-not (Test-Path $fullPath)) {{
        Write-Error "MISSING: $($item.path)"
        $failed++
        continue
    }}

    $actualHash = (Get-FileHash -Path $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $expectedHash = $item.sha256.ToLowerInvariant()

    if ($actualHash -ne $expectedHash) {{
        Write-Error "MISMATCH: $($item.path) (Expected: $expectedHash, Got: $actualHash)"
        $failed++
    }} else {{
        $passed++
    }}
}}

$color = "Green"
if ($failed -gt 0) {{ $color = "Red" }}
Write-Host "Results: $passed passed, $failed failed." -ForegroundColor $color
if ($failed -gt 0) {{
    exit 1
}} else {{
    Write-Host "SUCCESS: 100% bit-for-bit verified against DELIVERY_INDEX.json." -ForegroundColor Green
    exit 0
}}
'''

readback_ps1_file = RUN_DIR / "Verify-DeliveryReadback.ps1"
readback_ps1_file.write_text(readback_ps1, encoding="utf-8")
print(f"[OK] Wrote {readback_ps1_file}")
