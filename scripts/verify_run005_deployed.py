import json
import os
import hashlib

run_dir = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-20261015-005"
with open(os.path.join(run_dir, "W4_DELIVERY_INDEX.json"), encoding="utf-8") as f:
    idx = json.load(f)

print(f"Run ID: {idx['run_id']}")
print(f"Packaging Timestamp: {idx['packaging_timestamp_utc']}")
print(f"Total Payload Files: {idx['total_payload_files']}")
print(f"Total Payload Bytes: {idx['total_payload_bytes']}")

key_files = [
    "review/W4_ACCEPTANCE.json",
    "review/W4_FINDINGS.json",
    "review/HONEST_REVIEW.md",
    "review/PLANNED_81_CROSSWALK.json",
    "review/PLANNED_81_CROSSWALK.md",
    "review/16_CAPABILITY_GATES_ADJUDICATION.json",
    "review/FINAL_21_ADJUDICATION.json",
    "review/W4_FULL_SCAN_MATRIX.json",
    "review/PROVENANCE_ADDENDUM.md",
    "export/CANDIDATE_EXPORT_MANIFEST.json",
    "export/CANDIDATE_FILE_INVENTORY.json",
    "export/RECONSTRUCTION_VERIFICATION_LOG.json",
    "tasks/P11-IMP-108/RESULT.json",
    "tasks/P11-IMP-111/RESULT.json",
    "tasks/P11-IMP-119/RESULT.json"
]

for kf in key_files:
    p = os.path.join(run_dir, kf)
    if os.path.exists(p):
        sz = os.path.getsize(p)
        h = hashlib.sha256(open(p, "rb").read()).hexdigest()
        print(f"{kf:45} | {sz:7} B | {h}")
    else:
        print(f"MISSING: {kf}")
