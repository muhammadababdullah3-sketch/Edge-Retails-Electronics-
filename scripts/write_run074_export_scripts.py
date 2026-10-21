import json
from pathlib import Path

gov_export = Path(r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-LOCALFLOW-20261019-074\export")
local_export = Path(r"C:\Users\muham\OneDrive\Desktop\Point of Sale\governance\10-execution-runs\ER-RUN-LOCALFLOW-20261019-074\export")

inv_path = gov_export / "CANDIDATE_FILE_INVENTORY.json"
with open(inv_path, "r", encoding="utf-8") as f:
    inv = json.load(f)

bundle_sha = inv["archives"]["candidate_source_bundle"]["sha256"]
patch_sha = inv["archives"]["candidate_tracked_patch"]["sha256"]
untracked_sha = inv["archives"]["candidate_untracked_zip"]["sha256"]
base_commit = inv["base_commit"]
run_id = inv["run_id"]

recon_md = f"""# Candidate Source Reconstruction Instructions

- **Run ID**: `{run_id}`
- **Base Commit**: `{base_commit}`
- **Candidate Source Bundle SHA-256**: `{bundle_sha}`

## Method A: Extract Source Bundle Directly (Recommended)
1. Verify bundle SHA-256: `{bundle_sha}`.
2. Extract `candidate_source_bundle.zip` into repository root:
   ```bash
   unzip -o candidate_source_bundle.zip -d /path/to/Point-of-Sale
   ```
3. Build and test:
   ```bash
   dotnet build
   dotnet test
   ```

## Method B: Apply Tracked Patch + Extract Untracked Zip
1. Reset working tree to base commit `{base_commit}`:
   ```bash
   git checkout {base_commit}
   git clean -fd
   ```
2. Apply tracked patch:
   ```bash
   git apply --binary candidate_tracked.patch
   ```
3. Extract untracked files:
   ```bash
   unzip -o candidate_untracked.zip -d /path/to/Point-of-Sale
   ```
"""

verify_ps1 = f"""# Automated Candidate Export Verification for {run_id}
param(
    [string]$RepoRoot = "$PSScriptRoot\\..\\..\\..\\..\\..\\Point of Sale",
    [string]$ExportDir = "$PSScriptRoot"
)

$ErrorActionPreference = "Stop"
Write-Host "=== CANDIDATE EXPORT VERIFICATION: {run_id} ===" -ForegroundColor Cyan

$invPath = Join-Path $ExportDir "CANDIDATE_FILE_INVENTORY.json"
if (-not (Test-Path $invPath)) {{
    Write-Error "CANDIDATE_FILE_INVENTORY.json missing at $invPath"
}}

$inv = Get-Content $invPath -Raw | ConvertFrom-Json
Write-Host "Loaded inventory with $($inv.total_candidate_files) files."

# 1. Verify archive hashes
$bundlePath = Join-Path $ExportDir $inv.archives.candidate_source_bundle.file
$patchPath = Join-Path $ExportDir $inv.archives.candidate_tracked_patch.file
$untrackedPath = Join-Path $ExportDir $inv.archives.candidate_untracked_zip.file

function Get-FileSha256($path) {{
    $hasher = [System.Security.Cryptography.SHA256]::Create()
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $hash = $hasher.ComputeHash($bytes)
    return [System.BitConverter]::ToString($hash).Replace("-", "").ToLowerInvariant()
}}

$actualBundleHash = Get-FileSha256 $bundlePath
if ($actualBundleHash -ne $inv.archives.candidate_source_bundle.sha256) {{
    Write-Error "Bundle hash mismatch: expected $($inv.archives.candidate_source_bundle.sha256), got $actualBundleHash"
}}
Write-Host "[PASS] Source bundle SHA-256 matches: $actualBundleHash" -ForegroundColor Green

$actualPatchHash = Get-FileSha256 $patchPath
if ($actualPatchHash -ne $inv.archives.candidate_tracked_patch.sha256) {{
    Write-Error "Patch hash mismatch: expected $($inv.archives.candidate_tracked_patch.sha256), got $actualPatchHash"
}}
Write-Host "[PASS] Tracked patch SHA-256 matches: $actualPatchHash" -ForegroundColor Green

$actualUntrackedHash = Get-FileSha256 $untrackedPath
if ($actualUntrackedHash -ne $inv.archives.candidate_untracked_zip.sha256) {{
    Write-Error "Untracked zip hash mismatch: expected $($inv.archives.candidate_untracked_zip.sha256), got $actualUntrackedHash"
}}
Write-Host "[PASS] Untracked zip SHA-256 matches: $actualUntrackedHash" -ForegroundColor Green

# 2. Verify all files in repo match inventory
$verified = 0
foreach ($f in $inv.files) {{
    $filePath = Join-Path $RepoRoot $f.path
    if (-not (Test-Path $filePath)) {{
        Write-Error "Candidate file missing in repo: $($f.path)"
    }}
    $hash = Get-FileSha256 $filePath
    if ($hash -ne $f.sha256) {{
        Write-Error "Candidate file content hash mismatch: $($f.path)"
    }}
    $verified++
}}

Write-Host "[PASS] All $verified candidate files verified bit-for-bit against workspace!" -ForegroundColor Green
Write-Host "=== CANDIDATE EXPORT VERIFICATION COMPLETED: 100% VERIFIED ===" -ForegroundColor Cyan
"""

for d in [gov_export, local_export]:
    (d / "RECONSTRUCTION_INSTRUCTIONS.md").write_text(recon_md, encoding="utf-8")
    (d / "Verify-CandidateExport.ps1").write_text(verify_ps1, encoding="utf-8")

print("Wrote RECONSTRUCTION_INSTRUCTIONS.md and Verify-CandidateExport.ps1 to both export folders!")
