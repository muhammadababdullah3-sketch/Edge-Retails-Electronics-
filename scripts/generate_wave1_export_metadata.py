import os
import hashlib

EXPORT_DIR = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-20261015-008\export"

def sha256_file(filepath):
    h = hashlib.sha256()
    with open(filepath, "rb") as f:
        while chunk := f.read(65536):
            h.update(chunk)
    return h.hexdigest().lower()

patch_file = os.path.join(EXPORT_DIR, "candidate_tracked.patch")
untracked_file = os.path.join(EXPORT_DIR, "candidate_untracked.zip")
bundle_file = os.path.join(EXPORT_DIR, "candidate_source_bundle.zip")

p_sha = sha256_file(patch_file)
u_sha = sha256_file(untracked_file)
b_sha = sha256_file(bundle_file)

verify_ps1 = f'''# Verify-CandidateExport.ps1
$expectedPatch = "{p_sha}"
$expectedUntracked = "{u_sha}"
$expectedBundle = "{b_sha}"

$p = (Get-FileHash (Join-Path $PSScriptRoot "candidate_tracked.patch") -Algorithm SHA256).Hash.ToLowerInvariant()
$u = (Get-FileHash (Join-Path $PSScriptRoot "candidate_untracked.zip") -Algorithm SHA256).Hash.ToLowerInvariant()
$b = (Get-FileHash (Join-Path $PSScriptRoot "candidate_source_bundle.zip") -Algorithm SHA256).Hash.ToLowerInvariant()

$valid = $true
if ($p -ne $expectedPatch.ToLowerInvariant()) {{ Write-Error "Patch hash mismatch!"; $valid = $false }}
if ($u -ne $expectedUntracked.ToLowerInvariant()) {{ Write-Error "Untracked zip hash mismatch!"; $valid = $false }}
if ($b -ne $expectedBundle.ToLowerInvariant()) {{ Write-Error "Source bundle hash mismatch!"; $valid = $false }}

if ($valid) {{
    Write-Host "All Candidate Export Checksums match verified immutable manifest!" -ForegroundColor Green
}} else {{
    exit 1
}}
'''

with open(os.path.join(EXPORT_DIR, "Verify-CandidateExport.ps1"), "w", encoding="utf-8") as f:
    f.write(verify_ps1)

reconstruction_md = f'''# Phase 12 Wave 1 Candidate Reconstruction Instructions

- **Artifact ID**: `ER-P12-W1-MASTER-20261015-001`
- **Run ID**: `ER-RUN-20261015-008`
- **Governance Event**: `ER-CHANGE-20261015-027`
- **Standing Authorities**: `OWNER-UPDATE-20261013-007`, `OWNER-UPDATE-20261014-001`, `OWNER-UPDATE-20261014-002`, `OWNER-UPDATE-20261011-002`
- **Base Commit**: `3cd3b4b8f903adef6f6d7cf3c922c2ce184c2fc2`
- **Branch**: `feat/phase11-subledger-implementation`
- **Packaging Date UTC**: `2026-10-15`

### Verified Checksums:
- `candidate_tracked.patch`: `{p_sha}`
- `candidate_untracked.zip`: `{u_sha}`
- `candidate_source_bundle.zip`: `{b_sha}`

---

## Reconstruction Steps

To reproduce the exact candidate working tree from base commit `3cd3b4b8f903adef6f6d7cf3c922c2ce184c2fc2`:

1. **Checkout base commit**:
   ```bash
   git checkout 3cd3b4b8f903adef6f6d7cf3c922c2ce184c2fc2
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
   dotnet test tests/EdgeRetails.UnitTests/EdgeRetails.UnitTests.csproj -c Release --filter "FullyQualifiedName~Phase12"
   dotnet test tests/EdgeRetails.IntegrationTests/EdgeRetails.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~Phase12"
   dotnet test EdgeRetails.sln -c Release
   ```
'''

with open(os.path.join(EXPORT_DIR, "RECONSTRUCTION_INSTRUCTIONS.md"), "w", encoding="utf-8") as f:
    f.write(reconstruction_md)

print("Export verification scripts and reconstruction instructions written.")
