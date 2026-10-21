"""
Build Candidate Export Package for Run 083
ER-RUN-POS-NON89-EXEC-20261021-083
Preserves all candidate files from Run 082, with LF14 Option B presentation refinements and test proofs in:
- src/EdgeRetails.Desktop/Services/DemoReportingService.cs (ReportThakaActivityItem separate counts and amounts formatting)
- src/EdgeRetails.Desktop/Views/ReportsView.xaml (Option B layout with Net caption)
- tests/EdgeRetails.UnitTests/Phase11Wave3ApiAndDesktopCutoverTests.cs (LF14 linked issue fixtures & wholly reversal-only period test)
- tests/EdgeRetails.Desktop.PerformanceTests/Phase11Wave3DesktopCutoverTests.cs (LF14 deep WPF visual tree assertions)
Uses fixed reproducible timestamp 2026-10-21 12:00:00 UTC for deterministic ZIP packaging.
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
FIXED_ZIP_DT = (2026, 10, 21, 12, 0, 0)

EXPORT_DIR_POS = REPO_ROOT / "governance" / "10-execution-runs" / "ER-RUN-POS-NON89-EXEC-20261021-083" / "export"
EXPORT_DIR_GOV = GOV_BODY_ROOT / "governance" / "10-execution-runs" / "ER-RUN-POS-NON89-EXEC-20261021-083" / "export"

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

# 1. Load Run 082 Candidate Inventory for baseline drift comparison
r082_inv_path = REPO_ROOT / "governance" / "10-execution-runs" / "ER-RUN-LOCALFLOW-20261019-082" / "export" / "CANDIDATE_FILE_INVENTORY.json"
with open(r082_inv_path, "r", encoding="utf-8") as f:
    r082_manifest = json.load(f)

r082_map = {entry["path"]: entry for entry in r082_manifest["files"]}

# 2. Candidate Tracked Files
tracked_out = subprocess.check_output(
    ["git", "diff", "--name-only", BASE_COMMIT],
    cwd=REPO_ROOT,
    text=True
)
tracked_files = sorted([line.strip() for line in tracked_out.splitlines() if line.strip()])

# 3. Candidate Untracked Files from Run 082 inventory
untracked_files = sorted([entry["path"] for entry in r082_manifest["files"] if not entry["is_tracked"]])

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

# 7. Inventory and Drift comparison vs Run 082
inventory_files = []
identical_to_r082 = []
modified_vs_r082 = []
new_in_r083 = []

for rel_path in all_candidate_paths:
    full_path = REPO_ROOT / rel_path
    data = full_path.read_bytes()
    f_sha = sha256_bytes(data)
    f_size = len(data)
    is_tracked = rel_path in tracked_files

    r082_entry = r082_map.get(rel_path)
    if r082_entry is None:
        new_in_r083.append(rel_path)
        comp_r082 = "NEW_IN_RUN083"
    elif r082_entry["sha256"].lower() == f_sha.lower():
        identical_to_r082.append(rel_path)
        comp_r082 = "IDENTICAL_TO_RUN082"
    else:
        modified_vs_r082.append({
            "path": rel_path,
            "old_sha256": r082_entry["sha256"],
            "new_sha256": f_sha,
            "old_size": r082_entry["size_bytes"],
            "new_size": f_size,
            "is_tracked": is_tracked
        })
        comp_r082 = "MODIFIED_VS_RUN082"

    inventory_files.append({
        "path": rel_path,
        "sha256": f_sha,
        "size_bytes": f_size,
        "is_tracked": is_tracked,
        "comparison_to_run082": comp_r082
    })

inventory_manifest = {
    "run_id": "ER-RUN-POS-NON89-EXEC-20261021-083",
    "predecessor_run_id": "ER-RUN-LOCALFLOW-20261019-082",
    "base_commit": BASE_COMMIT,
    "timestamp_utc": "2026-10-21T12:00:00Z",
    "total_candidate_files": len(inventory_files),
    "tracked_candidate_files": len(tracked_files),
    "untracked_candidate_files": len(untracked_files),
    "drift_vs_run082": {
        "identical_count": len(identical_to_r082),
        "modified_count": len(modified_vs_r082),
        "new_count": len(new_in_r083),
        "modified_files": modified_vs_r082,
        "new_files": new_in_r083
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

print(f"[OK] Generated CANDIDATE_FILE_INVENTORY.json ({len(inventory_files)} files, {len(modified_vs_r082)} modified vs Run 082, {len(new_in_r083)} new)")
