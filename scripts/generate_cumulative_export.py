import os
import subprocess
import zipfile
import json
import hashlib
from datetime import datetime, timezone

POS_DIR = r"C:\Users\muham\OneDrive\Desktop\Point of Sale"
RUN_DIR = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-20261015-009"
EXPORT_DIR = os.path.join(RUN_DIR, "export")
os.makedirs(EXPORT_DIR, exist_ok=True)

BASE_COMMIT = "3cd3b4b8f903adef6f6d7cf3c922c2ce184c2fc2"

def sha256_file(filepath):
    h = hashlib.sha256()
    with open(filepath, "rb") as f:
        while chunk := f.read(65536):
            h.update(chunk)
    return h.hexdigest().lower()

# 1. Tracked modified files
tracked_files = subprocess.check_output(
    ["git", "diff", "--name-only", BASE_COMMIT],
    cwd=POS_DIR, text=True
).splitlines()
tracked_files = [f.strip() for f in tracked_files if f.strip()]

# 2. Untracked files in src/, tests/, and scripts/
untracked_status = subprocess.check_output(
    ["git", "status", "--porcelain"],
    cwd=POS_DIR, text=True
).splitlines()

untracked_files = []
for line in untracked_status:
    if line.startswith("?? "):
        rel_entry = line[3:].strip()
        full_entry = os.path.join(POS_DIR, rel_entry)
        if os.path.isdir(full_entry):
            for root, dirs, files in os.walk(full_entry):
                for fname in files:
                    full_p = os.path.join(root, fname)
                    rel_p = os.path.relpath(full_p, POS_DIR).replace(os.sep, "/")
                    if rel_p.startswith("src/") or rel_p.startswith("tests/") or rel_p.startswith("scripts/") or rel_p.endswith(".sln"):
                        untracked_files.append(rel_p)
        else:
            rel_p = rel_entry.replace(os.sep, "/")
            if rel_p.startswith("src/") or rel_p.startswith("tests/") or rel_p.startswith("scripts/") or rel_p.endswith(".sln"):
                untracked_files.append(rel_p)

tracked_files = sorted(list(set(tracked_files)))
untracked_files = sorted(list(set(untracked_files)))
all_candidate_files = sorted(list(set(tracked_files + untracked_files)))

print(f"Tracked modified candidate files: {len(tracked_files)}")
print(f"Untracked candidate files: {len(untracked_files)}")
print(f"Total cumulative candidate files: {len(all_candidate_files)}")

# 3. Generate candidate_tracked.patch
patch_path = os.path.join(EXPORT_DIR, "candidate_tracked.patch")
patch_bytes = subprocess.check_output(
    ["git", "diff", "--binary", BASE_COMMIT, "--"] + tracked_files,
    cwd=POS_DIR
)
with open(patch_path, "wb") as f:
    f.write(patch_bytes)

# 4. Generate candidate_untracked.zip
untracked_zip_path = os.path.join(EXPORT_DIR, "candidate_untracked.zip")
with zipfile.ZipFile(untracked_zip_path, "w", zipfile.ZIP_DEFLATED) as zf:
    for rel_path in untracked_files:
        full_path = os.path.join(POS_DIR, rel_path.replace("/", os.sep))
        zf.write(full_path, rel_path)

# 5. Generate candidate_source_bundle.zip
source_bundle_path = os.path.join(EXPORT_DIR, "candidate_source_bundle.zip")
with zipfile.ZipFile(source_bundle_path, "w", zipfile.ZIP_DEFLATED) as zf:
    for rel_path in all_candidate_files:
        full_path = os.path.join(POS_DIR, rel_path.replace("/", os.sep))
        zf.write(full_path, rel_path)

# 6. Generate CANDIDATE_FILE_INVENTORY.json
inventory = []
for rel_path in all_candidate_files:
    full_path = os.path.join(POS_DIR, rel_path.replace("/", os.sep))
    size = os.path.getsize(full_path)
    sha = sha256_file(full_path)
    inventory.append({
        "path": rel_path,
        "is_tracked": rel_path in tracked_files,
        "size_bytes": size,
        "sha256": sha
    })

inventory_data = {
    "artifact_id": "ER-RUN-20261015-009-CANDIDATE-INVENTORY",
    "run_id": "ER-RUN-20261015-009",
    "base_commit": BASE_COMMIT,
    "timestamp_utc": datetime.now(timezone.utc).isoformat(),
    "total_files": len(inventory),
    "tracked_files_count": len(tracked_files),
    "untracked_files_count": len(untracked_files),
    "files": inventory
}
with open(os.path.join(EXPORT_DIR, "CANDIDATE_FILE_INVENTORY.json"), "w", encoding="utf-8") as f:
    json.dump(inventory_data, f, indent=2)

# 7. Checksums of exported archives
patch_sha = sha256_file(patch_path)
untracked_sha = sha256_file(untracked_zip_path)
bundle_sha = sha256_file(source_bundle_path)

verify_ps1 = f'''# Verify-CandidateExport.ps1
# 100% bit-for-bit SHA256 verification of candidate export bundles
$expectedPatch = "{patch_sha}"
$expectedUntracked = "{untracked_sha}"
$expectedBundle = "{bundle_sha}"

$p = (Get-FileHash (Join-Path $PSScriptRoot "candidate_tracked.patch") -Algorithm SHA256).Hash.ToLowerInvariant()
$u = (Get-FileHash (Join-Path $PSScriptRoot "candidate_untracked.zip") -Algorithm SHA256).Hash.ToLowerInvariant()
$b = (Get-FileHash (Join-Path $PSScriptRoot "candidate_source_bundle.zip") -Algorithm SHA256).Hash.ToLowerInvariant()

$valid = $true
if ($p -ne $expectedPatch.ToLowerInvariant()) {{ Write-Error "Patch hash mismatch!"; $valid = $false }}
if ($u -ne $expectedUntracked.ToLowerInvariant()) {{ Write-Error "Untracked zip hash mismatch!"; $valid = $false }}
if ($b -ne $expectedBundle.ToLowerInvariant()) {{ Write-Error "Source bundle hash mismatch!"; $valid = $false }}

if ($valid) {{
    Write-Host "All Candidate Export Checksums match verified immutable manifest!" -ForegroundColor Green
    exit 0
}} else {{
    exit 1
}}
'''
with open(os.path.join(EXPORT_DIR, "Verify-CandidateExport.ps1"), "w", encoding="utf-8") as f:
    f.write(verify_ps1)

reconstruction_md = f'''# Phase 12 Wave 1 & Cumulative Foundation Candidate Reconstruction Instructions

- **Artifact ID**: `ER-RUN-20261015-009-RECONSTRUCTION`
- **Run ID**: `ER-RUN-20261015-009`
- **Governance Event**: `ER-CHANGE-20261015-033`
- **Authority**: `OWNER-UPDATE-20261015-001`, `OWNER-UPDATE-20261014-001`, `OWNER-UPDATE-20261013-008`, `board/WORKFLOW_V2.md`
- **Base Commit**: `{BASE_COMMIT}`
- **Branch**: `feat/phase11-subledger-implementation`
- **Packaging Timestamp UTC**: `{datetime.now(timezone.utc).isoformat()}`
- **Total Cumulative Candidate Files**: `{len(all_candidate_files)}` (`{len(tracked_files)}` tracked modified, `{len(untracked_files)}` untracked)

### Export Checksums (SHA-256):
- `candidate_tracked.patch`: `{patch_sha}`
- `candidate_untracked.zip`: `{untracked_sha}`
- `candidate_source_bundle.zip`: `{bundle_sha}`

---

## Reconstruction Steps

To reproduce the exact candidate working tree from base commit `{BASE_COMMIT}`:

1. **Checkout base commit**:
   ```bash
   git checkout {BASE_COMMIT}
   ```

2. **Apply Tracked Modifications Patch**:
   ```bash
   git apply --whitespace=nowarn candidate_tracked.patch
   ```

3. **Extract Untracked Assets**:
   ```powershell
   Expand-Archive -Path candidate_untracked.zip -DestinationPath . -Force
   ```

4. **Verify Checksums Against Inventory**:
   ```powershell
   .\\Verify-CandidateExport.ps1
   ```

5. **Verify Solution Builds (0 Warnings, 0 Errors)**:
   ```bash
   dotnet build EdgeRetails.sln -c Release
   dotnet build EdgeRetails.sln -c Debug
   ```

6. **Run Verification Test Suites (Targeting disposable PG18 on loopback port 55437)**:
   ```bash
   dotnet test tests/EdgeRetails.UnitTests/EdgeRetails.UnitTests.csproj -c Release
   dotnet test tests/EdgeRetails.Desktop.PerformanceTests/EdgeRetails.Desktop.PerformanceTests.csproj -c Release
   dotnet test tests/EdgeRetails.IntegrationTests/EdgeRetails.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~Phase12"
   dotnet test tests/EdgeRetails.IntegrationTests/EdgeRetails.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~Phase11"
   ```
'''
with open(os.path.join(EXPORT_DIR, "RECONSTRUCTION_INSTRUCTIONS.md"), "w", encoding="utf-8") as f:
    f.write(reconstruction_md)

print("Cumulative candidate export (including scripts/) generated successfully.")
