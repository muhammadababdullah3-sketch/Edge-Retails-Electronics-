import os
import json
import hashlib
import zipfile
import subprocess
from datetime import datetime, timezone

POS_DIR = r"C:\Users\muham\OneDrive\Desktop\Point of Sale"
GOV_RUN_DIR = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-20261015-008"
EXPORT_DIR = os.path.join(GOV_RUN_DIR, "export")

os.makedirs(EXPORT_DIR, exist_ok=True)

now_iso = datetime.now(timezone.utc).isoformat()

# Tracked Wave 1 modified files
TRACKED_FILES = [
    "src/EdgeRetails.Domain/Operations/OperationOutcome.cs",
    "src/EdgeRetails.Application/Features/Terminals/OperationOutcomeLedger.cs",
    "src/EdgeRetails.Infrastructure/Persistence/Configurations/OperationOutcomeConfiguration.cs",
    "src/EdgeRetails.Infrastructure/Persistence/Migrations/EdgeRetailsDbContextModelSnapshot.cs",
    "src/EdgeRetails.Infrastructure/Repositories/EfOperationOutcomeLedger.cs",
    "tests/EdgeRetails.IntegrationTests/Phase7Pass4AggregatePostgresTests.cs"
]

# Untracked Wave 1 new files
UNTRACKED_FILES = [
    "src/EdgeRetails.Application/Common/CanonicalPayloadSerializer.cs",
    "src/EdgeRetails.Application/Exceptions/IdempotencyExceptions.cs",
    "src/EdgeRetails.Infrastructure/Persistence/Migrations/20261015103137_Phase12OperationOutcomeResponsePayload.cs",
    "src/EdgeRetails.Infrastructure/Persistence/Migrations/20261015103137_Phase12OperationOutcomeResponsePayload.Designer.cs",
    "tests/EdgeRetails.UnitTests/Phase12CanonicalPayloadTests.cs",
    "tests/EdgeRetails.IntegrationTests/Phase12Wave1PostgresTests.cs"
]

ALL_WAVE1_FILES = TRACKED_FILES + UNTRACKED_FILES

def sha256_file(filepath):
    h = hashlib.sha256()
    with open(filepath, "rb") as f:
        while chunk := f.read(65536):
            h.update(chunk)
    return h.hexdigest()

def sha256_bytes(b):
    return hashlib.sha256(b).hexdigest()

print("Generating candidate patch and archives...")

# 1. candidate_tracked.patch
patch_path = os.path.join(EXPORT_DIR, "candidate_tracked.patch")
git_diff_cmd = ["git", "diff", "--binary", "3cd3b4b8f903adef6f6d7cf3c922c2ce184c2fc2", "--"] + TRACKED_FILES
patch_output = subprocess.check_output(git_diff_cmd, cwd=POS_DIR)
with open(patch_path, "wb") as f:
    f.write(patch_output)

# 2. candidate_untracked.zip
untracked_zip_path = os.path.join(EXPORT_DIR, "candidate_untracked.zip")
with zipfile.ZipFile(untracked_zip_path, "w", zipfile.ZIP_DEFLATED) as zf:
    for rel_path in UNTRACKED_FILES:
        full_path = os.path.join(POS_DIR, rel_path.replace("/", os.sep))
        zf.write(full_path, rel_path)

# 3. candidate_source_bundle.zip
source_bundle_path = os.path.join(EXPORT_DIR, "candidate_source_bundle.zip")
with zipfile.ZipFile(source_bundle_path, "w", zipfile.ZIP_DEFLATED) as zf:
    for rel_path in ALL_WAVE1_FILES:
        full_path = os.path.join(POS_DIR, rel_path.replace("/", os.sep))
        zf.write(full_path, rel_path)

# 4. CANDIDATE_FILE_INVENTORY.json
inventory = []
for rel_path in ALL_WAVE1_FILES:
    full_path = os.path.join(POS_DIR, rel_path.replace("/", os.sep))
    size = os.path.getsize(full_path)
    file_sha = sha256_file(full_path)
    inventory.append({
        "path": rel_path,
        "is_tracked": rel_path in TRACKED_FILES,
        "size_bytes": size,
        "sha256": file_sha
    })

inventory_data = {
    "run_id": "ER-RUN-20261015-008",
    "timestamp_utc": now_iso,
    "total_files": len(inventory),
    "tracked_files_count": len(TRACKED_FILES),
    "untracked_files_count": len(UNTRACKED_FILES),
    "files": inventory
}
inv_path = os.path.join(EXPORT_DIR, "CANDIDATE_FILE_INVENTORY.json")
with open(inv_path, "w", encoding="utf-8") as f:
    json.dump(inventory_data, f, indent=2)

print("Inventory and archives written successfully.")
