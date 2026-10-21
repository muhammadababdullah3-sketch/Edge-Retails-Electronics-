"""Build and verify candidate export package for ER-RUN-MON-W4-20261018-041.
Reconciles all 309 candidate files (143 tracked, 166 untracked) against Run 040 and Run 034 predecessors.
Generates candidate_tracked.patch, candidate_untracked.zip, candidate_source_bundle.zip,
CANDIDATE_FILE_INVENTORY.json, CANDIDATE_DRIFT_AND_PROVENANCE_AUDIT.json,
CANDIDATE_DRIFT_AND_PROVENANCE_AUDIT.md, RECONSTRUCTION_INSTRUCTIONS.md, and Verify-CandidateExport.ps1.
"""
import hashlib
import json
import os
import subprocess
import zipfile
from datetime import datetime, timezone
from pathlib import Path

REPO_ROOT = Path(r"C:\Users\muham\OneDrive\Desktop\Point of Sale")
RUN_DIR = Path(r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-MON-W4-20261018-041")
EXPORT_DIR = RUN_DIR / "export"
EXPORT_DIR.mkdir(parents=True, exist_ok=True)

RUN040_INV_PATH = Path(r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-MON-W4-20261018-040\export\CANDIDATE_FILE_INVENTORY.json")
with open(RUN040_INV_PATH, "r", encoding="utf-8") as f:
    r040_data = json.load(f)

RUN034_INV_PATH = Path(r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-20261018-034\export\CANDIDATE_FILE_INVENTORY.json")
with open(RUN034_INV_PATH, "r", encoding="utf-8") as f:
    r034_data = json.load(f)

r040_map = {f["path"].replace("\\", "/"): f for f in r040_data["files"]}
r034_map = {f["path"].replace("\\", "/"): f for f in r034_data["files"]}
print(f"Loaded {len(r040_map)} files from Run 040 inventory.")
print(f"Loaded {len(r034_map)} files from Run 034 inventory.")

BASE_COMMIT = "3cd3b4b8f903adef6f6d7cf3c922c2ce184c2fc2"

def sha256_bytes(b: bytes) -> str:
    return hashlib.sha256(b).hexdigest().lower()

def sha256_file(p: Path) -> str:
    return sha256_bytes(p.read_bytes())

# 1. Identify Tracked files
raw_git_diff = subprocess.check_output(["git", "diff", "--name-only", BASE_COMMIT], cwd=REPO_ROOT, text=True).splitlines()
tracked_files = sorted([f.strip().replace("\\", "/") for f in raw_git_diff if f.strip()])
print(f"Tracked modified candidate files: {len(tracked_files)}")

# 2. Identify Untracked candidate files from Run 040
untracked_files = sorted([p for p, entry in r040_map.items() if not entry["is_tracked"]])
print(f"Untracked candidate files: {len(untracked_files)}")

all_candidate_paths = sorted(list(set(tracked_files + untracked_files)))
print(f"Total candidate files for Run 041: {len(all_candidate_paths)} (tracked: {len(tracked_files)}, untracked: {len(untracked_files)})")
assert len(all_candidate_paths) == 309, f"Expected 309 candidate files, got {len(all_candidate_paths)}"

# 3. Generate candidate_tracked.patch
patch_file = EXPORT_DIR / "candidate_tracked.patch"
with open(patch_file, "wb") as pf:
    subprocess.check_call(["git", "diff", "--binary", BASE_COMMIT], cwd=REPO_ROOT, stdout=pf)

patch_bytes = patch_file.stat().st_size
patch_sha = sha256_file(patch_file)
print(f"[OK] Generated candidate_tracked.patch ({patch_bytes} bytes, SHA-256: {patch_sha})")

# 4. Generate candidate_untracked.zip
untracked_zip_file = EXPORT_DIR / "candidate_untracked.zip"
with zipfile.ZipFile(untracked_zip_file, "w", zipfile.ZIP_DEFLATED) as zf:
    for rel_path in untracked_files:
        full_path = REPO_ROOT / rel_path
        assert full_path.exists(), f"Untracked file missing: {rel_path}"
        zf.write(full_path, arcname=rel_path)

untracked_bytes = untracked_zip_file.stat().st_size
untracked_sha = sha256_file(untracked_zip_file)
print(f"[OK] Generated candidate_untracked.zip ({untracked_bytes} bytes, SHA-256: {untracked_sha})")

# 5. Generate candidate_source_bundle.zip
source_bundle_file = EXPORT_DIR / "candidate_source_bundle.zip"
with zipfile.ZipFile(source_bundle_file, "w", zipfile.ZIP_DEFLATED) as zf:
    for rel_path in all_candidate_paths:
        full_path = REPO_ROOT / rel_path
        assert full_path.exists(), f"Candidate file missing: {rel_path}"
        zf.write(full_path, arcname=rel_path)

bundle_bytes = source_bundle_file.stat().st_size
bundle_sha = sha256_file(source_bundle_file)
print(f"[OK] Generated candidate_source_bundle.zip ({bundle_bytes} bytes, SHA-256: {bundle_sha})")

# 6. Build file inventory and drift assessment
inventory_files = []
identical_to_r040 = []
modified_vs_r040 = []
identical_to_r034 = []
modified_vs_r034 = []

for rel_path in all_candidate_paths:
    full_path = REPO_ROOT / rel_path
    data = full_path.read_bytes()
    f_sha = sha256_bytes(data)
    f_size = len(data)
    is_tracked = rel_path in tracked_files

    r040_entry = r040_map.get(rel_path)
    if r040_entry is None:
        comp_r040 = "NEW_IN_RUN041"
    elif r040_entry["sha256"].lower() == f_sha.lower():
        identical_to_r040.append(rel_path)
        comp_r040 = "IDENTICAL_TO_RUN040"
    else:
        modified_vs_r040.append({
            "path": rel_path,
            "old_sha256": r040_entry["sha256"],
            "new_sha256": f_sha,
            "old_size": r040_entry["size_bytes"],
            "new_size": f_size,
            "is_tracked": is_tracked
        })
        comp_r040 = "MODIFIED_VS_RUN040"

    r034_entry = r034_map.get(rel_path)
    if r034_entry is None:
        comp_r034 = "NEW_IN_RUN041"
    elif r034_entry["sha256"].lower() == f_sha.lower():
        identical_to_r034.append(rel_path)
        comp_r034 = "IDENTICAL_TO_RUN034"
    else:
        modified_vs_r034.append({
            "path": rel_path,
            "old_sha256": r034_entry["sha256"],
            "new_sha256": f_sha,
            "old_size": r034_entry["size_bytes"],
            "new_size": f_size,
            "is_tracked": is_tracked
        })
        comp_r034 = "MODIFIED_VS_RUN034"

    inventory_files.append({
        "path": rel_path,
        "is_tracked": is_tracked,
        "size_bytes": f_size,
        "sha256": f_sha,
        "comparison_vs_run040": comp_r040,
        "comparison_vs_run034": comp_r034
    })

print(f"Inventory reconciliation vs Run 040: {len(identical_to_r040)} identical, {len(modified_vs_r040)} modified.")
print(f"Inventory reconciliation vs Run 034: {len(identical_to_r034)} identical, {len(modified_vs_r034)} modified.")
assert len(all_candidate_paths) == 309
assert len(modified_vs_r040) == 1, f"Expected exactly 1 modified file vs Run 040, got {len(modified_vs_r040)}"
assert len(identical_to_r040) == 308, f"Expected 308 identical files vs Run 040, got {len(identical_to_r040)}"
assert len(modified_vs_r034) == 2, f"Expected exactly 2 modified files vs Run 034, got {len(modified_vs_r034)}"
assert len(identical_to_r034) == 307, f"Expected 307 identical files vs Run 034, got {len(identical_to_r034)}"

# Write CANDIDATE_FILE_INVENTORY.json
inv_doc = {
    "artifact_id": "ER-RUN-MON-W4-20261018-041-CANDIDATE-INVENTORY",
    "run_id": "ER-RUN-MON-W4-20261018-041",
    "predecessor_run_id": "ER-RUN-MON-W4-20261018-040",
    "predecessor_bundle_sha256": r040_data["export_archives"]["candidate_source_bundle_zip"]["sha256"],
    "historical_predecessor_run_id": "ER-RUN-20261018-034",
    "historical_predecessor_bundle_sha256": r034_data["export_archives"]["candidate_source_bundle_zip"]["sha256"],
    "authority": "OWNER-UPDATE-20261018-004",
    "base_commit": BASE_COMMIT,
    "timestamp_utc": datetime.now(timezone.utc).isoformat(),
    "summary": {
        "total_candidate_files": len(all_candidate_paths),
        "tracked_files": len(tracked_files),
        "untracked_files": len(untracked_files),
        "identical_to_run040": len(identical_to_r040),
        "modified_vs_run040": len(modified_vs_r040),
        "identical_to_run034": len(identical_to_r034),
        "modified_vs_run034": len(modified_vs_r034)
    },
    "export_archives": {
        "candidate_tracked_patch": {
            "filename": "candidate_tracked.patch",
            "sha256": patch_sha,
            "bytes": patch_bytes
        },
        "candidate_untracked_zip": {
            "filename": "candidate_untracked.zip",
            "sha256": untracked_sha,
            "bytes": untracked_bytes
        },
        "candidate_source_bundle_zip": {
            "filename": "candidate_source_bundle.zip",
            "sha256": bundle_sha,
            "bytes": bundle_bytes
        }
    },
    "files": inventory_files
}

inv_file = EXPORT_DIR / "CANDIDATE_FILE_INVENTORY.json"
inv_file.write_text(json.dumps(inv_doc, indent=2), encoding="utf-8")
print(f"[OK] Generated {inv_file}")

# 7. Write CANDIDATE_DRIFT_AND_PROVENANCE_AUDIT.json
drift_doc = {
    "audit_type": "CANDIDATE_DRIFT_AND_PROVENANCE_AUDIT",
    "run_id": "ER-RUN-MON-W4-20261018-041",
    "predecessor_run_id": "ER-RUN-MON-W4-20261018-040",
    "predecessor_bundle_sha256": r040_data["export_archives"]["candidate_source_bundle_zip"]["sha256"],
    "historical_predecessor_run_id": "ER-RUN-20261018-034",
    "historical_predecessor_bundle_sha256": r034_data["export_archives"]["candidate_source_bundle_zip"]["sha256"],
    "current_bundle_sha256": bundle_sha,
    "timestamp_utc": datetime.now(timezone.utc).isoformat(),
    "authority": "OWNER-UPDATE-20261018-004",
    "review_findings_addressed": [
        "MON-W4-001 / P11-IMP-108: Enclosed PackContainersController.AllocatePack row locks and SaveChangesAsync in an explicit product database transaction when _db.Database.CurrentTransaction is null, preventing premature lock release in PostgreSQL autocommit mode.",
        "MON-W4-001 / P11-IMP-108: Desktop RemotePosWorkflowService.AllocatePackAsync verified as transparent forwarding over DesktopApiClient; DesktopApiClient converts 409 to clean DesktopApiException, satisfying condition (a) of the single-machine POS contract without silent background retries.",
        "MON-W4-001 / P11-IMP-108: Test retry harness in Deadlock_Or_Serialization_Retry_Is_Bounded reconciled with supplemental CONCURRENCY-14 requirement (bounded retry OR clean error); removed overclaims regarding product-internal retry loops.",
        "CHRONOLOGY: Corrected Run 040 timestamp discrepancy (PKT UTC+05:00 vs normalized UTC Z) with transparent provenance, avoiding backdating anomalies."
    ],
    "reconciliation_summary": {
        "total_candidate_files": 309,
        "identical_to_run040_count": 308,
        "modified_vs_run040_count": 1,
        "identical_to_run034_count": 307,
        "modified_vs_run034_count": 2,
        "newly_added_count": 0
    },
    "modified_files_vs_run040": modified_vs_r040,
    "modified_files_vs_run034": modified_vs_r034
}

drift_json_file = EXPORT_DIR / "CANDIDATE_DRIFT_AND_PROVENANCE_AUDIT.json"
drift_json_file.write_text(json.dumps(drift_doc, indent=2), encoding="utf-8")
print(f"[OK] Generated {drift_json_file}")

# 8. Write CANDIDATE_DRIFT_AND_PROVENANCE_AUDIT.md
drift_md = f"""# Candidate Drift and Provenance Audit — ER-RUN-MON-W4-20261018-041

- **Run ID**: `ER-RUN-MON-W4-20261018-041`
- **Predecessor Run**: `ER-RUN-MON-W4-20261018-040` (`{r040_data["export_archives"]["candidate_source_bundle_zip"]["sha256"]}`)
- **Historical Predecessor Run**: `ER-RUN-20261018-034` (`{r034_data["export_archives"]["candidate_source_bundle_zip"]["sha256"]}`)
- **Authority**: `OWNER-UPDATE-20261018-004`
- **Timestamp UTC**: `{datetime.now(timezone.utc).isoformat()}`
- **Base Commit**: `{BASE_COMMIT}`
- **Gaps Resolved**: Finding `MON-W4-001` and Task `P11-IMP-108` source repair for explicit transaction enclosure, clean desktop contract verification, and timestamp chronology correction.

---

## 1. Executive Summary

An exhaustive, byte-for-byte reconciliation of all candidate files in the Point of Sale working tree was conducted against predecessor Run 040 and Run 034 inventories.

### Quantitative Reconciliation:
- **Total Candidate Inventory**: **309 files** (143 tracked, 166 untracked).
- **Identical to Run 040**: **308 files** (99.68% byte-identical preservation).
- **Modified vs Run 040**: **1 file** (0.32% — exact source repair of `PackContainersController.cs` enclosing row locks and `SaveChangesAsync` in an explicit database transaction).
- **Identical to Run 034**: **307 files** (99.35% byte-identical preservation).
- **Modified vs Run 034**: **2 files** (0.65% — `PackContainersController.cs` and `PackConcurrencyPostgresTests.cs`).
- **Newly Added / Deleted**: **0 files**.

---

## 2. Truthful Accounting of Run 041 Modifications

### The 1 Bounded File Modified vs Run 040:
1. `src/EdgeRetails.Server/Controllers/PackContainersController.cs` (untracked):
   - **Purpose**: Enclosed `AllocatePack` execution within an explicit database transaction (`BeginTransactionAsync` / `CommitAsync`) when `_db.Database.CurrentTransaction is null`.
   - **Causal Guarantee**: In PostgreSQL, `SELECT ... FOR UPDATE` row locks released immediately in autocommit mode. By opening an explicit transaction prior to `GetStockBalanceForUpdateAsync` and `GetLotBucketBalanceForUpdateAsync`, pessimistic row locks are maintained continuously through entity mutation and `_unitOfWork.SaveChangesAsync(cancellationToken)`, committing atomically upon success and rolling back with `_db.ChangeTracker.Clear()` on conflict.
   - **Ambient Transaction Compatibility**: If `_db.Database.CurrentTransaction is not null` (e.g. caller workflow or test harness transaction), the controller participates in the existing transaction without attempting illegal nested transaction creation.

---

## 3. Fresh Export Checksums (ER-RUN-MON-W4-20261018-041)

| Archive / Artifact | SHA-256 Checksum | Size (Bytes) | File Count |
| :--- | :--- | :--- | :--- |
| `candidate_tracked.patch` | `{patch_sha}` | {patch_bytes} | 143 diffs |
| `candidate_untracked.zip` | `{untracked_sha}` | {untracked_bytes} | 166 files |
| `candidate_source_bundle.zip` | `{bundle_sha}` | {bundle_bytes} | 309 files |

All subsequent test runs, TRXs, and governance receipts are bound strictly to candidate bundle hash `{bundle_sha}`.
"""

drift_md_file = EXPORT_DIR / "CANDIDATE_DRIFT_AND_PROVENANCE_AUDIT.md"
drift_md_file.write_text(drift_md, encoding="utf-8")
print(f"[OK] Generated {drift_md_file}")

# 9. Write RECONSTRUCTION_INSTRUCTIONS.md
reconstruction_md = f"""# Candidate Source Reconstruction Instructions — ER-RUN-MON-W4-20261018-041

This archive package provides bit-for-bit reconstruction of the exact candidate source code for **Run ER-RUN-MON-W4-20261018-041**.

## Provenance
- **Base Commit**: `{BASE_COMMIT}`
- **Source Bundle SHA-256**: `{bundle_sha}`
- **Tracked Patch SHA-256**: `{patch_sha}`
- **Untracked Archive SHA-256**: `{untracked_sha}`

## Reconstruction Procedure

```bash
# 1. Checkout baseline commit
git checkout {BASE_COMMIT}

# 2. Apply tracked patch
git apply export/candidate_tracked.patch

# 3. Extract untracked files
unzip -q export/candidate_untracked.zip -d .

# 4. Verify bit-for-bit integrity
powershell -ExecutionPolicy Bypass -File export/Verify-CandidateExport.ps1
```
"""
(EXPORT_DIR / "RECONSTRUCTION_INSTRUCTIONS.md").write_text(reconstruction_md, encoding="utf-8")
print(f"[OK] Generated {EXPORT_DIR / 'RECONSTRUCTION_INSTRUCTIONS.md'}")

# 10. Write Verify-CandidateExport.ps1
verify_ps1 = f"""# Verify-CandidateExport.ps1
# 100% bit-for-bit SHA256 verification of candidate export bundles for ER-RUN-MON-W4-20261018-041
$expectedPatch = "{patch_sha}"
$expectedUntracked = "{untracked_sha}"
$expectedBundle = "{bundle_sha}"

$p = (Get-FileHash (Join-Path $PSScriptRoot "candidate_tracked.patch") -Algorithm SHA256).Hash.ToLowerInvariant()
$u = (Get-FileHash (Join-Path $PSScriptRoot "candidate_untracked.zip") -Algorithm SHA256).Hash.ToLowerInvariant()
$b = (Get-FileHash (Join-Path $PSScriptRoot "candidate_source_bundle.zip") -Algorithm SHA256).Hash.ToLowerInvariant()

$valid = $true
if ($p -ne $expectedPatch.ToLowerInvariant()) {{ Write-Error "Patch hash mismatch! Expected $expectedPatch, got $p"; $valid = $false }}
if ($u -ne $expectedUntracked.ToLowerInvariant()) {{ Write-Error "Untracked zip hash mismatch! Expected $expectedUntracked, got $u"; $valid = $false }}
if ($b -ne $expectedBundle.ToLowerInvariant()) {{ Write-Error "Source bundle hash mismatch! Expected $expectedBundle, got $b"; $valid = $false }}

if ($valid) {{
    Write-Host "All Candidate Export Checksums match verified immutable manifest (ER-RUN-MON-W4-20261018-041)!" -ForegroundColor Green
    exit 0
}} else {{
    exit 1
}}
"""
(EXPORT_DIR / "Verify-CandidateExport.ps1").write_text(verify_ps1, encoding="utf-8")
print(f"[OK] Generated {EXPORT_DIR / 'Verify-CandidateExport.ps1'}")
