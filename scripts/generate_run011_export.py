import os
import sys
import subprocess
import zipfile
import json
import hashlib
import tempfile
import shutil
import re
from datetime import datetime, timezone

POS_DIR = r"C:\Users\muham\OneDrive\Desktop\Point of Sale"
RUN_DIR = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-20261015-011"
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
    "artifact_id": "ER-RUN-20261015-011-CANDIDATE-INVENTORY",
    "run_id": "ER-RUN-20261015-011",
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

- **Artifact ID**: `ER-RUN-20261015-011-RECONSTRUCTION`
- **Run ID**: `ER-RUN-20261015-011`
- **Governance Event**: `ER-CHANGE-20261015-038`
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

print("Export bundles generated. Starting reconstruction verification in disposable temp directory...")

# 8. Test Reconstruction
temp_dir = tempfile.mkdtemp(prefix="er_reconstruct_test_011_")
target_ws = os.path.join(temp_dir, "workspace")
os.makedirs(target_ws, exist_ok=True)

try:
    print(f"Extracting base commit archive to {target_ws}...")
    archive_proc = subprocess.Popen(["git", "archive", BASE_COMMIT], cwd=POS_DIR, stdout=subprocess.PIPE)
    tar_proc = subprocess.Popen(["tar", "-x", "-C", target_ws], stdin=archive_proc.stdout)
    archive_proc.stdout.close()
    tar_proc.communicate()
    if tar_proc.returncode != 0:
        raise RuntimeError("Failed to extract git archive of base commit")

    print("Applying candidate_tracked.patch...")
    apply_res = subprocess.run(
        ["git", "apply", "--whitespace=nowarn", patch_path],
        cwd=target_ws,
        capture_output=True,
        text=True
    )
    if apply_res.returncode != 0:
        print(f"Patch apply failed: {apply_res.stderr}")
        raise RuntimeError("git apply failed")

    print("Extracting candidate_untracked.zip...")
    with zipfile.ZipFile(untracked_zip_path, "r") as zf:
        zf.extractall(target_ws)

    print("Building solution in reconstructed workspace...")
    build_res = subprocess.run(
        ["dotnet", "build", "EdgeRetails.sln", "-c", "Release"],
        cwd=target_ws,
        capture_output=True,
        text=True
    )
    print(f"Reconstruction build exit code: {build_res.returncode}")
    if build_res.returncode != 0:
        print(build_res.stdout[-2000:])
        print(build_res.stderr[-2000:])
        raise RuntimeError("Reconstruction build failed")

    stdout_tail = [line.strip() for line in build_res.stdout.splitlines() if line.strip()][-10:]

    recon_record = {
        "artifact_id": "ER-RUN-20261015-011-RECONSTRUCTION-RECORD",
        "timestamp_utc": datetime.now(timezone.utc).isoformat(),
        "base_commit": BASE_COMMIT,
        "reconstruction_dir": target_ws,
        "patch_applied": "candidate_tracked.patch",
        "patch_status": "CLEAN",
        "untracked_extracted": "candidate_untracked.zip",
        "build_command": "dotnet build EdgeRetails.sln -c Release",
        "build_exit_code": build_res.returncode,
        "build_status": "PASSED",
        "build_stdout_tail": stdout_tail
    }
    with open(os.path.join(EXPORT_DIR, "RECONSTRUCTION_VERIFICATION_RECORD.json"), "w", encoding="utf-8") as f:
        json.dump(recon_record, f, indent=2)

    print("Reconstruction verification succeeded!")
finally:
    shutil.rmtree(temp_dir, ignore_errors=True)

# 9. Build Gates Execution & BUILD_LOGS.json
print("Capturing solution build logs for Gates 16, 17, 18...")
def run_and_capture(cmd, cwd=POS_DIR):
    start_time = datetime.now(timezone.utc).isoformat()
    proc = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True)
    end_time = datetime.now(timezone.utc).isoformat()

    warning_match = re.search(r"(\d+)\s+Warning\(s\)", proc.stdout)
    error_match = re.search(r"(\d+)\s+Error\(s\)", proc.stdout)

    warnings = int(warning_match.group(1)) if warning_match else 0
    errors = int(error_match.group(1)) if error_match else 0

    return {
        "command": " ".join(cmd),
        "working_directory": os.path.abspath(cwd),
        "start_time_utc": start_time,
        "end_time_utc": end_time,
        "exit_code": proc.returncode,
        "warning_count": warnings,
        "error_count": errors,
        "stdout": proc.stdout,
        "stderr": proc.stderr,
        "status": "SUCCESS" if proc.returncode == 0 else "FAILED"
    }

gate16 = run_and_capture(["dotnet", "build", "tests/EdgeRetails.IntegrationTests/EdgeRetails.IntegrationTests.csproj", "-c", "Release"])
gate17 = run_and_capture(["dotnet", "build", "EdgeRetails.sln", "-c", "Debug"])
gate18 = run_and_capture(["dotnet", "build", "EdgeRetails.sln", "-c", "Release"])

build_logs = {
    "artifact_id": "ER-RUN-20261015-011-BUILD-LOGS",
    "timestamp_utc": datetime.now(timezone.utc).isoformat(),
    "builds": {
        "GATE16_IntegrationTests_Release": gate16,
        "GATE17_Solution_Debug": gate17,
        "GATE18_Solution_Release": gate18
    }
}
with open(os.path.join(EXPORT_DIR, "BUILD_LOGS.json"), "w", encoding="utf-8") as f:
    json.dump(build_logs, f, indent=2)

print("Export generation and verification complete!")
