"""
Build Candidate Export Package for Run 076
ER-RUN-LOCALFLOW-20261019-076
Preserves all 328 files from Run 075, with exactly 1 modified test file
(Phase12Wave3DurableClientOwnershipTests.cs) resolving C1, C2, and C3 root review findings.
Uses fixed reproducible timestamp 2026-10-19 12:00:00 UTC for ZIP packaging.
"""

import os
import sys
import json
import hashlib
import zipfile
import subprocess
from pathlib import Path

REPO_ROOT = Path(r"C:\Users\muham\OneDrive\Desktop\Point of Sale")
GOV_BODY_ROOT = Path(r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body")
BASE_COMMIT = "3cd3b4b8f903adef6f6d7cf3c922c2ce184c2fc2"
FIXED_ZIP_DT = (2026, 10, 19, 12, 0, 0)

EXPORT_DIR_POS = REPO_ROOT / "governance" / "10-execution-runs" / "ER-RUN-LOCALFLOW-20261019-076" / "export"
EXPORT_DIR_GOV = GOV_BODY_ROOT / "governance" / "10-execution-runs" / "ER-RUN-LOCALFLOW-20261019-076" / "export"

EXPORT_DIR_POS.mkdir(parents=True, exist_ok=True)
EXPORT_DIR_GOV.mkdir(parents=True, exist_ok=True)

def sha256_bytes(b: bytes) -> str:
    return hashlib.sha256(b).hexdigest()

def sha256_file(p: Path) -> str:
    h = hashlib.sha256()
    with open(p, "rb") as f:
        while chunk := f.read(131072):
            h.update(chunk)
    return h.hexdigest()

# 1. Load Run 075 Candidate Inventory for baseline drift comparison
r075_inv_path = GOV_BODY_ROOT / "governance" / "10-execution-runs" / "ER-RUN-LOCALFLOW-20261019-075" / "export" / "CANDIDATE_FILE_INVENTORY.json"
with open(r075_inv_path, "r", encoding="utf-8") as f:
    r075_manifest = json.load(f)

r075_map = {entry["path"]: entry for entry in r075_manifest["files"]}

# 2. Candidate Tracked Files
tracked_out = subprocess.check_output(
    ["git", "diff", "--name-only", BASE_COMMIT],
    cwd=REPO_ROOT,
    text=True
)
tracked_files = sorted([line.strip() for line in tracked_out.splitlines() if line.strip()])

# 3. Candidate Untracked Files from Run 075 inventory
untracked_files = sorted([entry["path"] for entry in r075_manifest["files"] if not entry["is_tracked"]])

all_candidate_paths = sorted(tracked_files + untracked_files)
assert len(all_candidate_paths) == 329, f"Expected 329 candidate files, got {len(all_candidate_paths)}"
assert len(tracked_files) == 160, f"Expected 160 tracked files, got {len(tracked_files)}"
assert len(untracked_files) == 169, f"Expected 169 untracked files, got {len(untracked_files)}"

# 4. Generate candidate_tracked.patch
patch_bytes = subprocess.check_output(
    ["git", "diff", "--binary", BASE_COMMIT],
    cwd=REPO_ROOT
)
patch_sha = sha256_bytes(patch_bytes)
patch_size = len(patch_bytes)

for out_dir in [EXPORT_DIR_POS, EXPORT_DIR_GOV]:
    (out_dir / "candidate_tracked.patch").write_bytes(patch_bytes)

print(f"[OK] Generated candidate_tracked.patch ({patch_size} bytes, sha256: {patch_sha})")

# 5. Pack candidate_untracked.zip with fixed timestamps
untracked_zip_path = EXPORT_DIR_POS / "candidate_untracked.zip"
with zipfile.ZipFile(untracked_zip_path, "w", compression=zipfile.ZIP_DEFLATED) as zf:
    for rel_path in untracked_files:
        full_path = REPO_ROOT / rel_path
        data = full_path.read_bytes()
        zinfo = zipfile.ZipInfo(filename=rel_path.replace("\\", "/"), date_time=FIXED_ZIP_DT)
        zinfo.compress_type = zipfile.ZIP_DEFLATED
        zinfo.external_attr = 0o644 << 16
        zf.writestr(zinfo, data)

untracked_bytes = untracked_zip_path.read_bytes()
untracked_sha = sha256_bytes(untracked_bytes)
untracked_size = len(untracked_bytes)

(EXPORT_DIR_GOV / "candidate_untracked.zip").write_bytes(untracked_bytes)
print(f"[OK] Generated candidate_untracked.zip ({untracked_size} bytes, sha256: {untracked_sha})")

# 6. Pack candidate_source_bundle.zip with fixed timestamps
bundle_zip_path = EXPORT_DIR_POS / "candidate_source_bundle.zip"
with zipfile.ZipFile(bundle_zip_path, "w", compression=zipfile.ZIP_DEFLATED) as zf:
    for rel_path in all_candidate_paths:
        full_path = REPO_ROOT / rel_path
        data = full_path.read_bytes()
        zinfo = zipfile.ZipInfo(filename=rel_path.replace("\\", "/"), date_time=FIXED_ZIP_DT)
        zinfo.compress_type = zipfile.ZIP_DEFLATED
        zinfo.external_attr = 0o644 << 16
        zf.writestr(zinfo, data)

bundle_bytes = bundle_zip_path.read_bytes()
bundle_sha = sha256_bytes(bundle_bytes)
bundle_size = len(bundle_bytes)

(EXPORT_DIR_GOV / "candidate_source_bundle.zip").write_bytes(bundle_bytes)
print(f"[OK] Generated candidate_source_bundle.zip ({bundle_size} bytes, sha256: {bundle_sha})")

# 7. Inventory and Drift comparison vs Run 075
inventory_files = []
identical_to_r075 = []
modified_vs_r075 = []
new_in_r076 = []

for rel_path in all_candidate_paths:
    full_path = REPO_ROOT / rel_path
    data = full_path.read_bytes()
    f_sha = sha256_bytes(data)
    f_size = len(data)
    is_tracked = rel_path in tracked_files

    r075_entry = r075_map.get(rel_path)
    if r075_entry is None:
        new_in_r076.append(rel_path)
        comp_r075 = "NEW_IN_RUN076"
    elif r075_entry["sha256"].lower() == f_sha.lower():
        identical_to_r075.append(rel_path)
        comp_r075 = "IDENTICAL_TO_RUN075"
    else:
        modified_vs_r075.append({
            "path": rel_path,
            "old_sha256": r075_entry["sha256"],
            "new_sha256": f_sha,
            "old_size": r075_entry["size_bytes"],
            "new_size": f_size,
            "is_tracked": is_tracked
        })
        comp_r075 = "MODIFIED_VS_RUN075"

    inventory_files.append({
        "path": rel_path,
        "sha256": f_sha,
        "size_bytes": f_size,
        "is_tracked": is_tracked,
        "comparison_to_run075": comp_r075
    })

inventory_manifest = {
    "run_id": "ER-RUN-LOCALFLOW-20261019-076",
    "predecessor_run_id": "ER-RUN-LOCALFLOW-20261019-075",
    "base_commit": BASE_COMMIT,
    "timestamp_utc": "2026-10-19T12:00:00Z",
    "total_candidate_files": len(inventory_files),
    "tracked_candidate_files": len(tracked_files),
    "untracked_candidate_files": len(untracked_files),
    "drift_vs_run075": {
        "identical_count": len(identical_to_r075),
        "modified_count": len(modified_vs_r075),
        "new_count": len(new_in_r076),
        "modified_files": modified_vs_r075,
        "new_files": new_in_r076
    },
    "archives": {
        "candidate_tracked_patch": {
            "file": "candidate_tracked.patch",
            "bytes": patch_size,
            "sha256": patch_sha
        },
        "candidate_untracked_zip": {
            "file": "candidate_untracked.zip",
            "bytes": untracked_size,
            "sha256": untracked_sha
        },
        "candidate_source_bundle": {
            "file": "candidate_source_bundle.zip",
            "bytes": bundle_size,
            "sha256": bundle_sha
        }
    },
    "files": inventory_files
}

for out_dir in [EXPORT_DIR_POS, EXPORT_DIR_GOV]:
    with open(out_dir / "CANDIDATE_FILE_INVENTORY.json", "w", encoding="utf-8") as f:
        json.dump(inventory_manifest, f, indent=2)

print(f"[OK] Generated CANDIDATE_FILE_INVENTORY.json ({len(inventory_files)} files, {len(modified_vs_r075)} modified vs Run 075)")

# 8. Drift and Provenance Audit JSON and Markdown
drift_audit_json = {
    "audit_run": "ER-RUN-LOCALFLOW-20261019-076",
    "baseline_run": "ER-RUN-LOCALFLOW-20261019-075",
    "historical_run": "ER-RUN-LOCALFLOW-20261019-074",
    "base_commit": BASE_COMMIT,
    "total_files": len(inventory_files),
    "summary": {
        "identical": len(identical_to_r075),
        "modified": len(modified_vs_r075),
        "new": len(new_in_r076)
    },
    "intentional_modifications": [
        {
            "path": "tests/EdgeRetails.Desktop.PerformanceTests/Phase12Wave3DurableClientOwnershipTests.cs",
            "category": "Root Review Findings C1, C2, and C3 Test Corrections",
            "justification": "Reconcile test oracles with distinct root review: (C1) Qualify Targeted J1 as client operation-ID reuse without over-claiming server-side single cash effect; (C2) Add ReverseRefundCommand and ReverseRefundAsync coverage to Targeted J2 and execute all 5 commands through ICommand.Execute to verify no unhandled exceptions escape the async void command boundary; (C3) Refactor Targeted J4 to instantiate production WarrantyViewModel and call production methods directly rather than using a test-local helper."
        }
    ],
    "preserved_components": {
        "suppliers_view_model": "src/EdgeRetails.Desktop/ViewModels/SuppliersViewModel.cs preserved 100% bit-for-bit from Run 075 accepted J2 causal fix",
        "untracked_components_preserved_count": len(untracked_files),
        "total_identical_files_count": len(identical_to_r075)
    }
}

for out_dir in [EXPORT_DIR_POS, EXPORT_DIR_GOV]:
    with open(out_dir / "CANDIDATE_DRIFT_AND_PROVENANCE_AUDIT.json", "w", encoding="utf-8") as f:
        json.dump(drift_audit_json, f, indent=2)

drift_audit_md = f"""# Candidate Drift and Provenance Audit: Run 076 vs Run 075

- **Run ID**: `ER-RUN-LOCALFLOW-20261019-076`
- **Predecessor Baseline**: `ER-RUN-LOCALFLOW-20261019-075`
- **Base Commit**: `{BASE_COMMIT}`
- **Candidate Files**: `{len(inventory_files)}` (`{len(tracked_files)}` tracked, `{len(untracked_files)}` untracked)
- **Identical Files**: `{len(identical_to_r075)}`
- **Modified Files**: `{len(modified_vs_r075)}`
- **New Files**: `{len(new_in_r076)}`

## Intentional Modifications Summary

1. `tests/EdgeRetails.Desktop.PerformanceTests/Phase12Wave3DurableClientOwnershipTests.cs`:
   - **C1 Resolution (Targeted J1)**: Qualified test oracle as client-side operation ID reuse and intent retention across readback timeout, while explicitly stating that single durable server ledger cash effect is UNPROVEN / OPEN without raw server database query assertions.
   - **C2 Resolution (Targeted J2)**: Added coverage for `ReverseRefundCommand` and `ReverseRefundAsync` with `"refund-reversal:<id>"` action token cleanup. Added explicit `ICommand.Execute` invocations across all 5 RelayCommands (`PostSettlementCommand`, `PostAdvanceCommand`, `ReceiveRefundCommand`, `ReversePaymentCommand`, `ReverseRefundCommand`) verifying that deterministic rejections in async void delegates do NOT throw unhandled exceptions across the command boundary.
   - **C3 Resolution (Targeted J4)**: Eliminated test-local `BuildPayload` helper. Directly instantiated production `WarrantyViewModel`, set `SelectedIntakeRow` and UI input properties, and called production `CreateClaimAsync` and `CreateClaimCommand`, proving canonical unit sorting, fingerprint mismatch rejection, and subsequent partial claim lifecycle.

## Preserved Source Modules (LF01..LF15)
All 15 master local flow remediations and the Run 075 J2 causal repair in `src/EdgeRetails.Desktop/ViewModels/SuppliersViewModel.cs` are 100% preserved bit-for-bit without change.
"""

for out_dir in [EXPORT_DIR_POS, EXPORT_DIR_GOV]:
    (out_dir / "CANDIDATE_DRIFT_AND_PROVENANCE_AUDIT.md").write_text(drift_audit_md, encoding="utf-8")

# 9. Reconstruction Instructions
recon_md = f"""# Candidate Source Reconstruction Instructions for Run 076

- **Run ID**: `ER-RUN-LOCALFLOW-20261019-076`
- **Base Commit**: `{BASE_COMMIT}`
- **Candidate Source Bundle SHA-256**: `{bundle_sha}`
- **Candidate Tracked Patch SHA-256**: `{patch_sha}`
- **Candidate Untracked Zip SHA-256**: `{untracked_sha}`

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
1. Reset working tree to base commit `{BASE_COMMIT}`:
   ```bash
   git checkout {BASE_COMMIT}
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

for out_dir in [EXPORT_DIR_POS, EXPORT_DIR_GOV]:
    (out_dir / "RECONSTRUCTION_INSTRUCTIONS.md").write_text(recon_md, encoding="utf-8")

# 10. Verification Script Verify-CandidateExport.ps1
verify_ps1 = f"""# Automated Candidate Export Verification for ER-RUN-LOCALFLOW-20261019-076
param(
    [string]$RepoRoot = "$PSScriptRoot\\..\\..\\..\\..\\..\\Point of Sale",
    [string]$ExportDir = "$PSScriptRoot"
)

$ErrorActionPreference = "Stop"
Write-Host "=== CANDIDATE EXPORT VERIFICATION: ER-RUN-LOCALFLOW-20261019-076 ===" -ForegroundColor Cyan

$invPath = Join-Path $ExportDir "CANDIDATE_FILE_INVENTORY.json"
if (-not (Test-Path $invPath)) {{
    throw "Inventory file not found: $invPath"
}}

$inv = Get-Content $invPath -Raw | ConvertFrom-Json
Write-Host "Verifying archive hashes from manifest..." -ForegroundColor Yellow

$patchPath = Join-Path $ExportDir "candidate_tracked.patch"
$untrackedPath = Join-Path $ExportDir "candidate_untracked.zip"
$bundlePath = Join-Path $ExportDir "candidate_source_bundle.zip"

$actualPatchSha = (Get-FileHash -Path $patchPath -Algorithm SHA256).Hash.ToLowerInvariant()
$actualUntrackedSha = (Get-FileHash -Path $untrackedPath -Algorithm SHA256).Hash.ToLowerInvariant()
$actualBundleSha = (Get-FileHash -Path $bundlePath -Algorithm SHA256).Hash.ToLowerInvariant()

if ($actualPatchSha -ne "{patch_sha}".ToLowerInvariant()) {{
    throw "Tracked patch SHA mismatch! Expected: {patch_sha}, Actual: $actualPatchSha"
}}
Write-Host "  [OK] candidate_tracked.patch SHA256 matches: $actualPatchSha" -ForegroundColor Green

if ($actualUntrackedSha -ne "{untracked_sha}".ToLowerInvariant()) {{
    throw "Untracked zip SHA mismatch! Expected: {untracked_sha}, Actual: $actualUntrackedSha"
}}
Write-Host "  [OK] candidate_untracked.zip SHA256 matches: $actualUntrackedSha" -ForegroundColor Green

if ($actualBundleSha -ne "{bundle_sha}".ToLowerInvariant()) {{
    throw "Source bundle SHA mismatch! Expected: {bundle_sha}, Actual: $actualBundleSha"
}}
Write-Host "  [OK] candidate_source_bundle.zip SHA256 matches: $actualBundleSha" -ForegroundColor Green

Write-Host "Verifying 329 candidate files on disk..." -ForegroundColor Yellow
$checked = 0
$mismatches = @()

foreach ($entry in $inv.files) {{
    $rel = $entry.path
    $expectedSha = $entry.sha256.ToLowerInvariant()
    $full = Join-Path $RepoRoot $rel
    if (-not (Test-Path $full)) {{
        $mismatches += "MISSING: $rel"
        continue
    }}
    $actualSha = (Get-FileHash -Path $full -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualSha -ne $expectedSha) {{
        $mismatches += "HASH_MISMATCH: $rel (Expected: $expectedSha, Actual: $actualSha)"
    }}
    $checked++
}}

if ($mismatches.Count -gt 0) {{
    $mismatches | ForEach-Object {{ Write-Host "  [FAIL] $_" -ForegroundColor Red }}
    throw "$($mismatches.Count) file mismatches detected!"
}}

Write-Host "  [OK] All $checked candidate files verified against workspace on disk!" -ForegroundColor Green
Write-Host "=== CANDIDATE EXPORT VERIFICATION PASSED ===" -ForegroundColor Green
"""

for out_dir in [EXPORT_DIR_POS, EXPORT_DIR_GOV]:
    (out_dir / "Verify-CandidateExport.ps1").write_text(verify_ps1, encoding="utf-8")

print(f"[OK] Generated Verify-CandidateExport.ps1 in both export directories.")
print(f"=== Candidate Export Package Build Complete for Run 076 ===")
print(f"Candidate Source Bundle SHA-256: {bundle_sha}")
print(f"Candidate Tracked Patch SHA-256: {patch_sha}")
print(f"Candidate Untracked Zip SHA-256: {untracked_sha}")
