import os
import sys
import shutil
import subprocess
import zipfile
import hashlib
import json
import tempfile
import re

repo_root = os.path.abspath(".")
print(f"Repo root: {repo_root}")

# 1. Tracked modified files
tracked_proc = subprocess.run(["git", "diff", "--name-only", "HEAD"], capture_output=True, text=True, check=True)
tracked_files = sorted([line.strip() for line in tracked_proc.stdout.splitlines() if line.strip()])
print(f"Tracked modified files: {len(tracked_files)}")

# 2. Candidate untracked files: src/, tests/, and scripts/
untracked_proc = subprocess.run(["git", "ls-files", "--others", "--exclude-standard"], capture_output=True, text=True, check=True)
all_untracked = sorted([line.strip() for line in untracked_proc.stdout.splitlines() if line.strip()])
candidate_untracked = [f for f in all_untracked if f.startswith("src/") or f.startswith("tests/") or f.startswith("scripts/")]
print(f"Candidate untracked files (src, tests, scripts): {len(candidate_untracked)}")

all_candidate_files = sorted(tracked_files + candidate_untracked)
print(f"Total candidate files: {len(all_candidate_files)}")

temp_dir = tempfile.mkdtemp(prefix="er_recon_verify_")
print(f"Reconstruction temp dir: {temp_dir}")

try:
    patch_path = os.path.join(temp_dir, "candidate_tracked.patch")
    with open(patch_path, "wb") as pf:
        subprocess.run(["git", "diff", "HEAD"], stdout=pf, check=True)
    patch_size = os.path.getsize(patch_path)
    with open(patch_path, "rb") as pf:
        patch_sha256 = hashlib.sha256(pf.read()).hexdigest()
    print(f"Generated patch: {patch_size} bytes, sha256: {patch_sha256[:12]}...")

    untracked_zip_path = os.path.join(temp_dir, "candidate_untracked.zip")
    with zipfile.ZipFile(untracked_zip_path, "w", zipfile.ZIP_DEFLATED) as zf:
        for f in candidate_untracked:
            zf.write(os.path.join(repo_root, f), arcname=f)
    untracked_size = os.path.getsize(untracked_zip_path)
    with open(untracked_zip_path, "rb") as zf:
        untracked_sha256 = hashlib.sha256(zf.read()).hexdigest()
    print(f"Generated untracked zip: {untracked_size} bytes, sha256: {untracked_sha256[:12]}...")

    target_ws = os.path.join(temp_dir, "workspace")
    os.makedirs(target_ws, exist_ok=True)
    archive_proc = subprocess.Popen(["git", "archive", "HEAD"], stdout=subprocess.PIPE)
    tar_proc = subprocess.Popen(["tar", "-x", "-C", target_ws], stdin=archive_proc.stdout)
    archive_proc.stdout.close()
    tar_proc.communicate()
    if tar_proc.returncode != 0:
        raise RuntimeError("Failed to extract git archive of HEAD")

    apply_res = subprocess.run(
        ["git", "apply", "--whitespace=nowarn", patch_path],
        cwd=target_ws,
        capture_output=True,
        text=True
    )
    if apply_res.returncode != 0:
        raise RuntimeError(f"git apply failed: {apply_res.stderr}")
    print("Patch applied cleanly!")

    with zipfile.ZipFile(untracked_zip_path, "r") as zf:
        zf.extractall(target_ws)
    print("Untracked files expanded successfully!")

    inventory = []
    exact_match_count = 0
    crlf_normalized_count = 0
    mismatches = []

    for f in all_candidate_files:
        orig_file = os.path.join(repo_root, f)
        recon_file = os.path.join(target_ws, f)
        if not os.path.exists(recon_file):
            mismatches.append(f"Missing in recon: {f}")
            continue

        with open(orig_file, "rb") as rf:
            orig_bytes = rf.read()
            orig_h = hashlib.sha256(orig_bytes).hexdigest()
        with open(recon_file, "rb") as rf:
            recon_bytes = rf.read()
            recon_h = hashlib.sha256(recon_bytes).hexdigest()

        orig_len = os.path.getsize(orig_file)
        recon_len = os.path.getsize(recon_file)

        norm_orig = orig_bytes.replace(b"\r\n", b"\n")
        norm_recon = recon_bytes.replace(b"\r\n", b"\n")
        norm_match = (norm_orig == norm_recon)

        exact_match = (orig_h == recon_h)
        if exact_match:
            exact_match_count += 1
            prov = "EXACT_BINARY_MATCH"
        elif norm_match:
            crlf_normalized_count += 1
            prov = "CRLF_TEXT_NORMALIZED_MATCH (Windows line-ending from git diff/patch)"
        else:
            mismatches.append(f"Content mismatch on {f}")
            prov = "CONTENT_MISMATCH"

        inventory.append({
            "path": f,
            "category": "TRACKED_MODIFIED" if f in tracked_files else "UNTRACKED_CANDIDATE",
            "original_size_bytes": orig_len,
            "original_sha256": orig_h,
            "reconstructed_size_bytes": recon_len,
            "reconstructed_sha256": recon_h,
            "exact_binary_match": exact_match,
            "crlf_normalized_match": norm_match,
            "provenance": prov
        })

    print(f"Inventory total: {len(inventory)}")
    print(f"Exact binary matches: {exact_match_count}")
    print(f"CRLF normalized matches: {crlf_normalized_count}")
    print(f"Content mismatches: {len(mismatches)}")
    if mismatches:
        raise RuntimeError("File equivalence failed!")

    print("Executing 'dotnet build EdgeRetails.sln -c Release' in reconstructed workspace...")
    build_release = subprocess.run(
        ["dotnet", "build", "EdgeRetails.sln", "-c", "Release"],
        cwd=target_ws,
        capture_output=True,
        text=True
    )
    print(f"Release build exit code: {build_release.returncode}")
    w_rel = int(re.search(r"(\d+)\s+Warning\(s\)", build_release.stdout).group(1)) if re.search(r"(\d+)\s+Warning\(s\)", build_release.stdout) else 0
    e_rel = int(re.search(r"(\d+)\s+Error\(s\)", build_release.stdout).group(1)) if re.search(r"(\d+)\s+Error\(s\)", build_release.stdout) else 0
    print(f"Release build warnings: {w_rel}, errors: {e_rel}")

    print("Executing 'dotnet build EdgeRetails.sln -c Debug' in reconstructed workspace...")
    build_debug = subprocess.run(
        ["dotnet", "build", "EdgeRetails.sln", "-c", "Debug"],
        cwd=target_ws,
        capture_output=True,
        text=True
    )
    print(f"Debug build exit code: {build_debug.returncode}")
    w_deb = int(re.search(r"(\d+)\s+Warning\(s\)", build_debug.stdout).group(1)) if re.search(r"(\d+)\s+Warning\(s\)", build_debug.stdout) else 0
    e_deb = int(re.search(r"(\d+)\s+Error\(s\)", build_debug.stdout).group(1)) if re.search(r"(\d+)\s+Error\(s\)", build_debug.stdout) else 0
    print(f"Debug build warnings: {w_deb}, errors: {e_deb}")

    if build_release.returncode != 0 or build_debug.returncode != 0:
        raise RuntimeError("Reconstructed builds failed!")

    print("Reconstruction verification SUCCEEDED 100% for both Debug and Release!")

finally:
    shutil.rmtree(temp_dir, ignore_errors=True)
    print("Clean up completed.")
