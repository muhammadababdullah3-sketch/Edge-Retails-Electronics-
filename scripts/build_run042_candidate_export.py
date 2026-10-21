"""Build and verify candidate export package for ER-RUN-MON-W4-20261018-042."""
import hashlib
import json
import os
import shutil
from pathlib import Path

WORKSPACE = Path(r"C:\Users\muham\OneDrive\Desktop\Point of Sale")
RUN_DIR = Path(r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-MON-W4-20261018-042")
EXPORT_DIR = RUN_DIR / "export"
EXPORT_DIR.mkdir(parents=True, exist_ok=True)

RUN041_EXPORT_DIR = Path(r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-MON-W4-20261018-041\export")

def sha256_file(p: Path) -> str:
    h = hashlib.sha256()
    with open(p, "rb") as f:
        while chunk := f.read(65536):
            h.update(chunk)
    return h.hexdigest().lower()

# Copy candidate export archives from Run 041
for fn in [
    "candidate_source_bundle.zip",
    "candidate_tracked.patch",
    "candidate_untracked.zip",
    "CANDIDATE_FILE_INVENTORY.json",
    "CANDIDATE_DRIFT_AND_PROVENANCE_AUDIT.json",
    "CANDIDATE_DRIFT_AND_PROVENANCE_AUDIT.md",
    "RECONSTRUCTION_INSTRUCTIONS.md"
]:
    src = RUN041_EXPORT_DIR / fn
    dst = EXPORT_DIR / fn
    shutil.copy2(src, dst)
    print(f"Copied {fn} -> {dst}")

bundle_sha = sha256_file(EXPORT_DIR / "candidate_source_bundle.zip")
patch_sha = sha256_file(EXPORT_DIR / "candidate_tracked.patch")
untracked_sha = sha256_file(EXPORT_DIR / "candidate_untracked.zip")

print(f"candidate_source_bundle.zip SHA256: {bundle_sha}")
print(f"candidate_tracked.patch SHA256: {patch_sha}")
print(f"candidate_untracked.zip SHA256: {untracked_sha}")

# Write Verify-CandidateExport.ps1
verify_ps1 = f'''# Verify-CandidateExport.ps1
# Independent verification of Run 042 candidate source bundle
$expectedBundleSha = "{bundle_sha}"
$expectedPatchSha = "{patch_sha}"
$expectedUntrackedSha = "{untracked_sha}"

$bundlePath = Join-Path $PSScriptRoot "candidate_source_bundle.zip"
$patchPath = Join-Path $PSScriptRoot "candidate_tracked.patch"
$untrackedPath = Join-Path $PSScriptRoot "candidate_untracked.zip"

$bHash = (Get-FileHash -Path $bundlePath -Algorithm SHA256).Hash.ToLowerInvariant()
$pHash = (Get-FileHash -Path $patchPath -Algorithm SHA256).Hash.ToLowerInvariant()
$uHash = (Get-FileHash -Path $untrackedPath -Algorithm SHA256).Hash.ToLowerInvariant()

$errors = 0
if ($bHash -ne $expectedBundleSha) {{
    Write-Error "Bundle hash mismatch: expected $expectedBundleSha, got $bHash"
    $errors++
}}
if ($pHash -ne $expectedPatchSha) {{
    Write-Error "Patch hash mismatch: expected $expectedPatchSha, got $pHash"
    $errors++
}}
if ($uHash -ne $expectedUntrackedSha) {{
    Write-Error "Untracked hash mismatch: expected $expectedUntrackedSha, got $uHash"
    $errors++
}}

if ($errors -eq 0) {{
    Write-Host "SUCCESS: Candidate export archives verified 100% bit-for-bit!" -ForegroundColor Green
    exit 0
}} else {{
    Write-Error "FAILED: $errors mismatch(es) detected"
    exit 1
}}
'''

(EXPORT_DIR / "Verify-CandidateExport.ps1").write_text(verify_ps1, encoding="utf-8")
print(f"Wrote {EXPORT_DIR / 'Verify-CandidateExport.ps1'}")
