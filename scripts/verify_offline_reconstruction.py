import os
import shutil
import subprocess
import zipfile
import json
import hashlib
from datetime import datetime, timezone

BASE_COMMIT = "3cd3b4b8f903adef6f6d7cf3c922c2ce184c2fc2"
SOURCE_REPO = r"C:\Users\muham\OneDrive\Desktop\Point of Sale"
RUN_DIR = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-20261015-009"
EXPORT_DIR = os.path.join(RUN_DIR, "export")
TEMP_DIR = r"C:\Users\muham\AppData\Local\Temp\er_reconstruct_test_009"

def log(msg):
    print(f"[{datetime.now(timezone.utc).isoformat()}] {msg}")

log("Starting Offline Candidate Reconstruction Verification...")

if os.path.exists(TEMP_DIR):
    log(f"Cleaning existing temp dir: {TEMP_DIR}")
    shutil.rmtree(TEMP_DIR, ignore_errors=True)

os.makedirs(TEMP_DIR, exist_ok=True)

# 1. Clone or git worktree/checkout from source repo to temp dir
log("Creating git clone at base commit...")
subprocess.run(["git", "clone", "--no-checkout", SOURCE_REPO, TEMP_DIR], check=True)
subprocess.run(["git", "checkout", BASE_COMMIT], cwd=TEMP_DIR, check=True)

# 2. Apply candidate_tracked.patch
patch_path = os.path.join(EXPORT_DIR, "candidate_tracked.patch")
log(f"Applying patch: {patch_path}")
patch_res = subprocess.run(["git", "apply", "--whitespace=nowarn", patch_path], cwd=TEMP_DIR, capture_output=True, text=True)
if patch_res.returncode != 0:
    log(f"Patch apply failed: {patch_res.stderr}")
    raise RuntimeError(f"Patch apply error: {patch_res.stderr}")
log("Patch applied cleanly.")

# 3. Extract candidate_untracked.zip
untracked_path = os.path.join(EXPORT_DIR, "candidate_untracked.zip")
log(f"Extracting untracked bundle: {untracked_path}")
with zipfile.ZipFile(untracked_path, "r") as zf:
    zf.extractall(TEMP_DIR)
log("Untracked assets extracted.")

# 4. Verify Build
log("Executing dotnet build EdgeRetails.sln in reconstructed workspace...")
build_res = subprocess.run(["dotnet", "build", "EdgeRetails.sln", "-c", "Release"], cwd=TEMP_DIR, capture_output=True, text=True)
log(f"Build Exit Code: {build_res.returncode}")

build_record = {
    "artifact_id": "ER-RUN-20261015-009-RECONSTRUCTION-RECORD",
    "timestamp_utc": datetime.now(timezone.utc).isoformat(),
    "base_commit": BASE_COMMIT,
    "reconstruction_dir": TEMP_DIR,
    "patch_applied": "candidate_tracked.patch",
    "patch_status": "CLEAN" if patch_res.returncode == 0 else "FAILED",
    "untracked_extracted": "candidate_untracked.zip",
    "build_command": "dotnet build EdgeRetails.sln -c Release",
    "build_exit_code": build_res.returncode,
    "build_status": "PASSED" if build_res.returncode == 0 else "FAILED",
    "build_stdout_tail": build_res.stdout.strip().splitlines()[-5:] if build_res.stdout else []
}

record_path = os.path.join(EXPORT_DIR, "RECONSTRUCTION_VERIFICATION_RECORD.json")
with open(record_path, "w", encoding="utf-8") as f:
    json.dump(build_record, f, indent=2)

log(f"Reconstruction record deposited: {record_path}")
if build_res.returncode == 0:
    log("SUCCESS: Reconstructed workspace built with 0 errors!")
else:
    log(f"FAILURE: Build output: {build_res.stdout}\n{build_res.stderr}")

# Clean up temp dir to maintain zero disk footprint
shutil.rmtree(TEMP_DIR, ignore_errors=True)
log("Temp reconstruction directory cleaned up.")
