import os
import sys
import shutil
import subprocess
import zipfile
import hashlib
import json
import tempfile

repo_root = os.path.abspath(".")
print(f"Repo root: {repo_root}")

# 1. Identify tracked modified files
tracked_proc = subprocess.run(["git", "diff", "--name-only", "HEAD"], capture_output=True, text=True, check=True)
tracked_files = [line.strip() for line in tracked_proc.stdout.splitlines() if line.strip()]
print(f"Tracked modified files: {len(tracked_files)}")

# 2. Identify untracked files
untracked_proc = subprocess.run(["git", "ls-files", "--others", "--exclude-standard"], capture_output=True, text=True, check=True)
all_untracked = [line.strip() for line in untracked_proc.stdout.splitlines() if line.strip()]

# Filter untracked to source/test/asset files needed for candidate
candidate_untracked = [f for f in all_untracked if f.startswith("src/") or f.startswith("tests/")]
print(f"Candidate untracked files: {len(candidate_untracked)}")

# 3. Create temp directory for clean reconstruction
temp_dir = tempfile.mkdtemp(prefix="er_clean_recon_")
print(f"Reconstruction temp dir: {temp_dir}")

try:
    # 4. Generate candidate_tracked.patch
    patch_path = os.path.join(temp_dir, "candidate_tracked.patch")
    with open(patch_path, "wb") as pf:
        subprocess.run(["git", "diff", "HEAD"], stdout=pf, check=True)
    patch_size = os.path.getsize(patch_path)
    with open(patch_path, "rb") as pf:
        patch_sha256 = hashlib.sha256(pf.read()).hexdigest()
    print(f"Generated patch: {patch_size} bytes, sha256: {patch_sha256[:12]}...")

    # 5. Generate candidate_untracked.zip
    untracked_zip_path = os.path.join(temp_dir, "candidate_untracked.zip")
    with zipfile.ZipFile(untracked_zip_path, "w", zipfile.ZIP_DEFLATED) as zf:
        for f in candidate_untracked:
            zf.write(os.path.join(repo_root, f), arcname=f)
    untracked_size = os.path.getsize(untracked_zip_path)
    with open(untracked_zip_path, "rb") as zf:
        untracked_sha256 = hashlib.sha256(zf.read()).hexdigest()
    print(f"Generated untracked zip: {untracked_size} bytes, sha256: {untracked_sha256[:12]}...")

    # 6. Extract base commit files into target reconstruction workspace
    target_ws = os.path.join(temp_dir, "workspace")
    os.makedirs(target_ws, exist_ok=True)
    print("Extracting base commit archive to target workspace...")
    archive_proc = subprocess.Popen(["git", "archive", "HEAD"], stdout=subprocess.PIPE)
    tar_proc = subprocess.Popen(["tar", "-x", "-C", target_ws], stdin=archive_proc.stdout)
    archive_proc.stdout.close()
    tar_proc.communicate()
    if tar_proc.returncode != 0:
        raise RuntimeError("Failed to extract git archive of HEAD")

    # 7. Apply patch
    print("Applying candidate_tracked.patch to reconstructed workspace...")
    apply_res = subprocess.run(
        ["git", "apply", "--whitespace=nowarn", patch_path],
        cwd=target_ws,
        capture_output=True,
        text=True
    )
    if apply_res.returncode != 0:
        print(f"Patch apply failed: {apply_res.stderr}")
        raise RuntimeError("git apply failed")
    print("Patch applied cleanly!")

    # 8. Unzip untracked files
    print("Expanding candidate_untracked.zip into reconstructed workspace...")
    with zipfile.ZipFile(untracked_zip_path, "r") as zf:
        zf.extractall(target_ws)
    print("Untracked files expanded successfully!")

    # 9. Verify file equivalence for all candidate files
    print("Verifying file-by-file equivalence...")
    inventory = []
    mismatches = []
    all_candidate_files = tracked_files + candidate_untracked
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

        # Check text normalization
        norm_orig = orig_bytes.replace(b"\r\n", b"\n")
        norm_recon = recon_bytes.replace(b"\r\n", b"\n")
        norm_match = (norm_orig == norm_recon)

        inventory.append({
            "path": f,
            "size_bytes": orig_len,
            "sha256": orig_h,
            "reconstructed_size_bytes": recon_len,
            "reconstructed_sha256": recon_h,
            "exact_sha256_match": orig_h == recon_h,
            "normalized_content_match": norm_match,
            "status": "TRACKED_MODIFIED" if f in tracked_files else "UNTRACKED_CANDIDATE"
        })
        if orig_h != recon_h and not norm_match:
            mismatches.append(f"Content mismatch on {f}: normalized content differs")
        elif orig_h != recon_h:
            pass # CRLF line ending difference from git diff/apply

    print(f"Candidate file count: {len(all_candidate_files)}")
    print(f"Content semantic equivalence check: {len(mismatches)} mismatches")
    if mismatches:
        for m in mismatches[:10]:
            print("  ", m)
        raise RuntimeError("File equivalence failed!")

    # 10. Execute dotnet build Release in reconstructed workspace
    print("Executing 'dotnet build EdgeRetails.sln -c Release' in reconstructed workspace...")
    build_res = subprocess.run(
        ["dotnet", "build", "EdgeRetails.sln", "-c", "Release"],
        cwd=target_ws,
        capture_output=True,
        text=True
    )
    print(f"Reconstruction build exit code: {build_res.returncode}")
    if build_res.returncode != 0:
        print("Build output:")
        print(build_res.stdout[-2000:])
        print(build_res.stderr[-2000:])
        raise RuntimeError("Reconstruction build failed!")
    print("Reconstruction build SUCCEEDED 100%!")

finally:
    print("Cleaning up temp directory...")
    shutil.rmtree(temp_dir, ignore_errors=True)
    print("Clean up completed.")
