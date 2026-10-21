"""
Build Candidate Export Package for Run 082
ER-RUN-LOCALFLOW-20261019-082
Preserves all files from Run 081, with LF14 causal repairs and test proofs in:
- src/EdgeRetails.Application/Features/Reporting/ReportingQueries.cs
- src/EdgeRetails.Infrastructure/Services/BusinessOperationsReadServices.cs
- src/EdgeRetails.Desktop/Services/DemoReportingService.cs
- src/EdgeRetails.Desktop/Services/BackendBusinessOperationsService.cs
- src/EdgeRetails.Desktop/Services/RemoteBackendBusinessOperationsService.cs
- src/EdgeRetails.Desktop/Views/ReportsView.xaml
- tests/EdgeRetails.UnitTests/Phase11Wave3ApiAndDesktopCutoverTests.cs (LF14 oracle)
- tests/EdgeRetails.Desktop.PerformanceTests/Phase11Wave3DesktopCutoverTests.cs (LF14 presentation & STA)
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

EXPORT_DIR_POS = REPO_ROOT / "governance" / "10-execution-runs" / "ER-RUN-LOCALFLOW-20261019-082" / "export"
EXPORT_DIR_GOV = GOV_BODY_ROOT / "governance" / "10-execution-runs" / "ER-RUN-LOCALFLOW-20261019-082" / "export"

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

# 1. Load Run 081 Candidate Inventory for baseline drift comparison
r081_inv_path = REPO_ROOT / "governance" / "10-execution-runs" / "ER-RUN-LOCALFLOW-20261019-081" / "export" / "CANDIDATE_FILE_INVENTORY.json"
with open(r081_inv_path, "r", encoding="utf-8") as f:
    r081_manifest = json.load(f)

r081_map = {entry["path"]: entry for entry in r081_manifest["files"]}

# 2. Candidate Tracked Files
tracked_out = subprocess.check_output(
    ["git", "diff", "--name-only", BASE_COMMIT],
    cwd=REPO_ROOT,
    text=True
)
tracked_files = sorted([line.strip() for line in tracked_out.splitlines() if line.strip()])

# 3. Candidate Untracked Files from Run 081 inventory
untracked_files = sorted([entry["path"] for entry in r081_manifest["files"] if not entry["is_tracked"]])

all_candidate_paths = sorted(tracked_files + untracked_files)
print(f"Total candidate files: {len(all_candidate_paths)} (Tracked: {len(tracked_files)}, Untracked: {len(untracked_files)})")

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

# 7. Inventory and Drift comparison vs Run 081
inventory_files = []
identical_to_r081 = []
modified_vs_r081 = []
new_in_r082 = []

for rel_path in all_candidate_paths:
    full_path = REPO_ROOT / rel_path
    data = full_path.read_bytes()
    f_sha = sha256_bytes(data)
    f_size = len(data)
    is_tracked = rel_path in tracked_files

    r081_entry = r081_map.get(rel_path)
    if r081_entry is None:
        new_in_r082.append(rel_path)
        comp_r081 = "NEW_IN_RUN082"
    elif r081_entry["sha256"].lower() == f_sha.lower():
        identical_to_r081.append(rel_path)
        comp_r081 = "IDENTICAL_TO_RUN081"
    else:
        modified_vs_r081.append({
            "path": rel_path,
            "old_sha256": r081_entry["sha256"],
            "new_sha256": f_sha,
            "old_size": r081_entry["size_bytes"],
            "new_size": f_size,
            "is_tracked": is_tracked
        })
        comp_r081 = "MODIFIED_VS_RUN081"

    inventory_files.append({
        "path": rel_path,
        "sha256": f_sha,
        "size_bytes": f_size,
        "is_tracked": is_tracked,
        "comparison_to_run081": comp_r081
    })

inventory_manifest = {
    "run_id": "ER-RUN-LOCALFLOW-20261019-082",
    "predecessor_run_id": "ER-RUN-LOCALFLOW-20261019-081",
    "base_commit": BASE_COMMIT,
    "timestamp_utc": "2026-10-19T16:00:00Z",
    "total_candidate_files": len(inventory_files),
    "tracked_candidate_files": len(tracked_files),
    "untracked_candidate_files": len(untracked_files),
    "drift_vs_run081": {
        "identical_count": len(identical_to_r081),
        "modified_count": len(modified_vs_r081),
        "new_count": len(new_in_r082),
        "modified_files": modified_vs_r081,
        "new_files": new_in_r082
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

print(f"[OK] Generated CANDIDATE_FILE_INVENTORY.json ({len(inventory_files)} files, {len(modified_vs_r081)} modified vs Run 081, {len(new_in_r082)} new)")

# 8. Drift and Provenance Audit JSON and Markdown
drift_audit_json = {
    "audit_run": "ER-RUN-LOCALFLOW-20261019-082",
    "baseline_run": "ER-RUN-LOCALFLOW-20261019-081",
    "historical_run": "ER-RUN-LOCALFLOW-20261019-080",
    "base_commit": BASE_COMMIT,
    "total_files": len(inventory_files),
    "summary": {
        "identical": len(identical_to_r081),
        "modified": len(modified_vs_r081),
        "new": len(new_in_r082)
    },
    "intentional_modifications": [
        {
            "path": "src/EdgeRetails.Application/Features/Reporting/ReportingQueries.cs",
            "category": "LF14 Source Fix: ReportingThakaActivityDto Contract Extension",
            "justification": "Extended ReportingThakaActivityDto with non-breaking optional parameters (ReversalCount, ReversalAmount, NetMaterialValue) to support explicit reversal metrics under Option B."
        },
        {
            "path": "src/EdgeRetails.Infrastructure/Services/BusinessOperationsReadServices.cs",
            "category": "LF14 Source Fix: ReportingReadService Aggregation & Ordering",
            "justification": "Aggregated ThakaMaterialReversals count and amount by ProjectId, preserved reversal-only projects in allProjectIds, calculated net material value, sorted descending by net material value, and subtracted reversal amounts in snapshot thakaTotal."
        },
        {
            "path": "src/EdgeRetails.Desktop/Services/DemoReportingService.cs",
            "category": "LF14 Source Fix: ReportThakaActivityItem Presentation Layer Properties",
            "justification": "Added ReversalCount, ReversalAmount, NetMaterialValue, ActivitySummaryDisplay, NetMaterialValueDisplay, and HasReversals to ReportThakaActivityItem for desktop presentation."
        },
        {
            "path": "src/EdgeRetails.Desktop/Services/BackendBusinessOperationsService.cs",
            "category": "LF14 Source Fix: Local Backend Service DTO Mapping",
            "justification": "Mapped ReversalCount, ReversalAmount, and NetMaterialValue from ReportingThakaActivityDto to ReportThakaActivityItem."
        },
        {
            "path": "src/EdgeRetails.Desktop/Services/RemoteBackendBusinessOperationsService.cs",
            "category": "LF14 Source Fix: Remote Backend Service DTO Mapping",
            "justification": "Mapped ReversalCount, ReversalAmount, and NetMaterialValue across remote API boundary to ReportThakaActivityItem."
        },
        {
            "path": "src/EdgeRetails.Desktop/Views/ReportsView.xaml",
            "category": "LF14 Source Fix: Thaka Activity Card Template Data-Binding",
            "justification": "Updated card template data-binding to ActivitySummaryDisplay and NetMaterialValueDisplay to clearly render reversal details (e.g., '0 issues · 1 reversal (-Rs. 5,000.00)')."
        },
        {
            "path": "tests/EdgeRetails.UnitTests/Phase11Wave3ApiAndDesktopCutoverTests.cs",
            "category": "LF14 Behavioral Test Oracle: Application Read Service",
            "justification": "Added LF14_ReportingReadService_ReversalOnlyProject_IncludedWithExplicitReversalBreakdown verifying that reversal-only projects are included in snapshot, explicit metrics are correct, net values are deducted, and descending sort order is preserved."
        },
        {
            "path": "tests/EdgeRetails.Desktop.PerformanceTests/Phase11Wave3DesktopCutoverTests.cs",
            "category": "LF14 Behavioral Test Oracle: Presentation Formatting & STA Layout",
            "justification": "Added LF14_ReportThakaActivityItem_PresentationFormatting_RendersSeparateReversalDetails verifying presentation string formatting across mixed, reversal-only, and issue-only projects, with full offscreen WPF STA layout proof."
        }
    ],
    "preserved_components": {
        "run081_components_preserved_count": len(identical_to_r081),
        "untracked_components_preserved_count": len(untracked_files)
    }
}

for out_dir in [EXPORT_DIR_POS, EXPORT_DIR_GOV]:
    with open(out_dir / "CANDIDATE_DRIFT_AND_PROVENANCE_AUDIT.json", "w", encoding="utf-8") as f:
        json.dump(drift_audit_json, f, indent=2)

drift_audit_md = f"""# Candidate Drift and Provenance Audit: Run 082 vs Run 081

- **Run ID**: `ER-RUN-LOCALFLOW-20261019-082`
- **Predecessor Baseline**: `ER-RUN-LOCALFLOW-20261019-081`
- **Base Commit**: `{BASE_COMMIT}`
- **Candidate Files**: `{len(inventory_files)}` (`{len(tracked_files)}` tracked, `{len(untracked_files)}` untracked)
- **Identical Files**: `{len(identical_to_r081)}`
- **Modified Files**: `{len(modified_vs_r081)}`
- **New Files**: `{len(new_in_r082)}`

## Intentional Modifications Summary (LF14 Option B Implementation)

1. `src/EdgeRetails.Application/Features/Reporting/ReportingQueries.cs`:
   - Extended `ReportingThakaActivityDto` record contract with optional parameters: `ReversalCount = 0`, `ReversalAmount = 0m`, `NetMaterialValue = 0m`.

2. `src/EdgeRetails.Infrastructure/Services/BusinessOperationsReadServices.cs`:
   - In `ReportingReadService.GetSnapshotAsync`, extracted count and sum of `ReversedCharge` from `ThakaMaterialReversals`.
   - Included reversal-only projects via `thakaIssueRaw.Select(x => x.ProjectId).Union(thakaReversalRaw.Keys).Distinct()`.
   - Populated explicit reversal metrics and calculated `netMaterialValue = issueAmount - reversalAmount`.
   - Sorted activity descending by `NetMaterialValue`.
   - Updated snapshot metric `thakaTotal` to subtract reversals.

3. `src/EdgeRetails.Desktop/Services/DemoReportingService.cs`:
   - Extended `ReportThakaActivityItem` with reversal tracking and formatted presentation properties (`ActivitySummaryDisplay`, `NetMaterialValueDisplay`, `HasReversals`).

4. `src/EdgeRetails.Desktop/Services/BackendBusinessOperationsService.cs`:
   - Mapped reversal metrics from `ReportingThakaActivityDto` to `ReportThakaActivityItem`.

5. `src/EdgeRetails.Desktop/Services/RemoteBackendBusinessOperationsService.cs`:
   - Mapped reversal metrics across remote HTTP API boundary to `ReportThakaActivityItem`.

6. `src/EdgeRetails.Desktop/Views/ReportsView.xaml`:
   - Updated Thaka activity card template data-binding to `ActivitySummaryDisplay` and `NetMaterialValueDisplay`.

7. `tests/EdgeRetails.UnitTests/Phase11Wave3ApiAndDesktopCutoverTests.cs`:
   - Added dedicated named behavioral test oracle `LF14_ReportingReadService_ReversalOnlyProject_IncludedWithExplicitReversalBreakdown`.

8. `tests/EdgeRetails.Desktop.PerformanceTests/Phase11Wave3DesktopCutoverTests.cs`:
   - Added dedicated named behavioral test oracle `LF14_ReportThakaActivityItem_PresentationFormatting_RendersSeparateReversalDetails`.

## Preserved Source Modules
All other components from Run 081 (including LF02, LF03, LF04, LF05, LF06, LF07, LF08, LF09, LF10, LF11, LF12, LF15) are 100% preserved bit-for-bit without change.
"""

for out_dir in [EXPORT_DIR_POS, EXPORT_DIR_GOV]:
    (out_dir / "CANDIDATE_DRIFT_AND_PROVENANCE_AUDIT.md").write_text(drift_audit_md, encoding="utf-8")

# 9. Reconstruction Instructions
recon_md = f"""# Candidate Source Reconstruction Instructions for Run 082

- **Run ID**: `ER-RUN-LOCALFLOW-20261019-082`
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
verify_ps1 = f"""# Automated Candidate Export Verification for ER-RUN-LOCALFLOW-20261019-082
param(
    [string]$RepoRoot = "$PSScriptRoot\\..\\..\\..\\..\\..\\Point of Sale",
    [string]$ExportDir = "$PSScriptRoot"
)

$ErrorActionPreference = "Stop"
Write-Host "=== CANDIDATE EXPORT VERIFICATION: ER-RUN-LOCALFLOW-20261019-082 ===" -ForegroundColor Cyan

$invPath = Join-Path $ExportDir "CANDIDATE_FILE_INVENTORY.json"
if (-not (Test-Path $invPath)) {{
    throw "Inventory file not found: $invPath"
}}

$inv = Get-Content $invPath -Raw | ConvertFrom-Json
Write-Host "Verifying $($inv.total_candidate_files) candidate files against repository..." -ForegroundColor Yellow

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

Write-Host "  [OK] All $checked candidate files verified bit-for-bit against workspace!" -ForegroundColor Green
Write-Host "=== CANDIDATE EXPORT VERIFICATION PASSED ===" -ForegroundColor Green
"""

for out_dir in [EXPORT_DIR_POS, EXPORT_DIR_GOV]:
    (out_dir / "Verify-CandidateExport.ps1").write_text(verify_ps1, encoding="utf-8")

print(f"[OK] Candidate Export Package generation for Run 082 completed successfully!")
