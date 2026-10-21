import os
import zipfile
import subprocess
import hashlib
import json
from datetime import datetime

repo_root = os.path.abspath(".")
output_dir = os.path.join(repo_root, "artifacts", "governance_w3")
os.makedirs(output_dir, exist_ok=True)

# 1. Generate candidate_tracked.patch
patch_path = os.path.join(output_dir, "candidate_tracked.patch")
with open(patch_path, "wb") as pf:
    subprocess.check_call(["git", "diff", "HEAD"], cwd=repo_root, stdout=pf)

print(f"Generated {patch_path} ({os.path.getsize(patch_path)} bytes)")

# 2. Collect untracked files
cmd = ["git", "status", "--porcelain"]
output = subprocess.check_output(cmd, cwd=repo_root, text=True)

untracked_files = []
ignored_prefixes = (
    "artifacts/",
    ".vs/",
    "bin/",
    "obj/",
    ".claude/",
    ".git/",
)
ignored_extensions = (".trx", ".log", ".tmp")

for line in output.splitlines():
    if line.startswith("?? "):
        path = line[3:].strip()
        rel_norm = path.replace("\\", "/")
        if any(rel_norm.startswith(p) for p in ignored_prefixes):
            continue
        if any(rel_norm.endswith(ext) for ext in ignored_extensions):
            continue
        full = os.path.join(repo_root, path)
        if os.path.isfile(full):
            untracked_files.append(rel_norm)
        elif os.path.isdir(full):
            for root, _, files in os.walk(full):
                for f in files:
                    rel = os.path.relpath(os.path.join(root, f), repo_root).replace("\\", "/")
                    if any(rel.startswith(p) for p in ignored_prefixes):
                        continue
                    if any(rel.endswith(ext) for ext in ignored_extensions):
                        continue
                    untracked_files.append(rel)

untracked_files = sorted(list(set(untracked_files)))
print(f"Found {len(untracked_files)} untracked files to package.")

# 3. Create candidate_untracked.zip
untracked_zip_path = os.path.join(output_dir, "candidate_untracked.zip")
with zipfile.ZipFile(untracked_zip_path, "w", zipfile.ZIP_DEFLATED) as zf:
    for f in untracked_files:
        full_path = os.path.join(repo_root, f)
        if os.path.exists(full_path):
            zf.write(full_path, arcname=f)

print(f"Created {untracked_zip_path} ({os.path.getsize(untracked_zip_path)} bytes)")

# 4. Create candidate_source_bundle.zip
source_bundle_path = os.path.join(output_dir, "candidate_source_bundle.zip")
with zipfile.ZipFile(source_bundle_path, "w", zipfile.ZIP_DEFLATED) as zf:
    if os.path.exists(patch_path):
        zf.write(patch_path, arcname="candidate_tracked.patch")
    for f in untracked_files:
        full_path = os.path.join(repo_root, f)
        if os.path.exists(full_path):
            zf.write(full_path, arcname=os.path.join("untracked", f))

print(f"Created {source_bundle_path} ({os.path.getsize(source_bundle_path)} bytes)")

def sha256_file(filepath):
    h = hashlib.sha256()
    with open(filepath, "rb") as f:
        while chunk := f.read(65536):
            h.update(chunk)
    return h.hexdigest()

patch_hash = sha256_file(patch_path)
untracked_hash = sha256_file(untracked_zip_path)
bundle_hash = sha256_file(source_bundle_path)

# 5. Write CANDIDATE_EXPORT_MANIFEST.json
manifest = {
    "artifact_id": "ER-RUN-20261014-002-CANDIDATE-EXPORT-MANIFEST",
    "run_id": "ER-RUN-20261014-002",
    "authority": "OWNER-UPDATE-20261013-007",
    "continuation_authority": "OWNER-UPDATE-20261013-008/002 / OWNER-UPDATE-20261014-001 / OWNER-UPDATE-20261014-002",
    "governance_dispatch": "ER-DISPATCH-20261014-010",
    "timestamp_utc": "2026-10-14T14:55:00Z",
    "base_commit": "3cd3b4b8f903adef6f6d7cf3c922c2ce184c2fc2",
    "branch": "feat/phase11-subledger-implementation",
    "candidate_artifacts": [
        {
            "name": "candidate_tracked.patch",
            "size_bytes": os.path.getsize(patch_path),
            "sha256": patch_hash,
            "format": "unified_diff",
            "description": "Git diff patch capturing all tracked file modifications for Phase 11 Wave 3 under revision ER-RUN-20261014-002 against base commit 3cd3b4b8f903adef6f6d7cf3c922c2ce184c2fc2"
        },
        {
            "name": "candidate_untracked.zip",
            "size_bytes": os.path.getsize(untracked_zip_path),
            "sha256": untracked_hash,
            "format": "zip_archive",
            "file_count": len(untracked_files),
            "description": "Zip archive containing all new untracked source, contracts, view models, and behavioral tests under revision ER-RUN-20261014-002"
        },
        {
            "name": "candidate_source_bundle.zip",
            "size_bytes": os.path.getsize(source_bundle_path),
            "sha256": bundle_hash,
            "format": "zip_archive",
            "description": "Composite self-contained bundle with tracked patch and untracked tree for clean offline reconstruction under revision ER-RUN-20261014-002"
        }
    ]
}

manifest_path = os.path.join(output_dir, "CANDIDATE_EXPORT_MANIFEST.json")
with open(manifest_path, "w", encoding="utf-8") as f:
    json.dump(manifest, f, indent=2)

print(f"Updated {manifest_path}")

# 6. Write RECONSTRUCTION_INSTRUCTIONS.md
reconstruction_md = f"""# Phase 11 Wave 3 Candidate Reconstruction Instructions

Artifact ID: `ER-RUN-20261014-002-RECONSTRUCTION`
Run ID: `ER-RUN-20261014-002`
Authority: `OWNER-UPDATE-20261013-007`, `OWNER-UPDATE-20261013-008/002`, `OWNER-UPDATE-20261014-001`, `OWNER-UPDATE-20261014-002`
Governance Event: `ER-CHANGE-20261014-016`
Governance Dispatch: `ER-DISPATCH-20261014-010`
Base Commit: `3cd3b4b8f903adef6f6d7cf3c922c2ce184c2fc2`
Date: 2026-10-14
Tracked Patch SHA256: `{patch_hash}`
Untracked Zip SHA256: `{untracked_hash}`
Source Bundle SHA256: `{bundle_hash}`

---

## Reconstruction Steps

To reproduce the exact candidate working tree from true base commit `3cd3b4b8f903adef6f6d7cf3c922c2ce184c2fc2`:

1. **Clone repository and checkout base commit**:
   ```bash
   git checkout 3cd3b4b8f903adef6f6d7cf3c922c2ce184c2fc2
   ```

2. **Apply Tracked Modifications Patch**:
   ```bash
   git apply candidate_tracked.patch
   ```

3. **Extract Untracked Assets**:
   ```powershell
   Expand-Archive -Path candidate_untracked.zip -DestinationPath . -Force
   ```

4. **Verify SHA256 Checksums**:
   ```powershell
   Get-FileHash candidate_tracked.patch, candidate_untracked.zip, candidate_source_bundle.zip -Algorithm SHA256
   ```

5. **Verify Solution Build**:
   ```bash
   dotnet build EdgeRetails.sln -c Release
   ```

6. **Run Verification Test Suites**:
   ```bash
   dotnet test tests/EdgeRetails.UnitTests/EdgeRetails.UnitTests.csproj -c Release --filter "FullyQualifiedName~Phase11Wave3"
   dotnet test tests/EdgeRetails.Desktop.PerformanceTests/EdgeRetails.Desktop.PerformanceTests.csproj -c Release --filter "FullyQualifiedName~Phase11Wave3"
   dotnet test tests/EdgeRetails.IntegrationTests/EdgeRetails.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~Phase11DurablePostingIntegrationTests"
   ```
"""

reconstruction_path = os.path.join(output_dir, "RECONSTRUCTION_INSTRUCTIONS.md")
with open(reconstruction_path, "w", encoding="utf-8") as f:
    f.write(reconstruction_md)

print(f"Updated {reconstruction_path}")
