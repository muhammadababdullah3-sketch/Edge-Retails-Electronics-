import os
import shutil
import hashlib
import xml.etree.ElementTree as ET
import json
import csv

repo_root = os.path.abspath(".")
src_dir = os.path.join(repo_root, "artifacts", "governance_w3")
gov_run_dir = "C:/Users/muham/OneDrive/Desktop/Edge Retails Goverence Body/governance/10-execution-runs/ER-RUN-20261014-003"

os.makedirs(gov_run_dir, exist_ok=True)

def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        while chunk := f.read(65536):
            h.update(chunk)
    return h.hexdigest()

# 1. Define all files to transfer (source_path, dest_rel_path)
transfer_manifest = []

# Task files
for t in ["P11-IMP-098", "P11-IMP-099", "P11-IMP-100", "P11-IMP-101", "P11-IMP-102", "P11-IMP-103", "P11-IMP-104", "P11-IMP-105"]:
    transfer_manifest.append((os.path.join(src_dir, "tasks", t, "RESULT.json"), f"tasks/{t}/RESULT.json"))
    transfer_manifest.append((os.path.join(src_dir, "tasks", t, "NOTES.md"), f"tasks/{t}/NOTES.md"))

# Export files
for ef in ["CANDIDATE_EXPORT_MANIFEST.json", "candidate_source_bundle.zip", "candidate_tracked.patch", "candidate_untracked.zip", "RECONSTRUCTION_INSTRUCTIONS.md", "Verify-CandidateExport.ps1"]:
    transfer_manifest.append((os.path.join(src_dir, ef), f"export/{ef}"))

# Raw test and reconstruction verification files
for rf in ["unit_tests.trx", "desktop_tests.trx", "integration_tests.trx", "unit_tests_full_regression.trx", "desktop_full_294.trx", "trx_summary.json", "reconstruction_verification.log"]:
    transfer_manifest.append((os.path.join(src_dir, "raw", rf), f"raw/{rf}"))

# Review files
transfer_manifest.append((os.path.join(src_dir, "W3_FINDINGS.json"), "review/W3_FINDINGS.json"))
transfer_manifest.append((os.path.join(src_dir, "W3_FULL_SCAN_MATRIX.json"), "review/W3_FULL_SCAN_MATRIX.json"))
transfer_manifest.append((os.path.join(src_dir, "HONEST_REVIEW.md"), "review/HONEST_REVIEW.md"))
transfer_manifest.append((os.path.join(src_dir, "PROVENANCE_ADDENDUM.md"), "review/PROVENANCE_ADDENDUM.md"))

# Delivery files
transfer_manifest.append((os.path.join(src_dir, "W3_ACCEPTANCE.json"), "delivery/W3_ACCEPTANCE.json"))
transfer_manifest.append((os.path.join(src_dir, "W4_HANDOFF.md"), "delivery/W4_HANDOFF.md"))
transfer_manifest.append((os.path.join(src_dir, "PROVENANCE_ADDENDUM.md"), "delivery/PROVENANCE_ADDENDUM.md"))

# Root documentation
transfer_manifest.append((os.path.join(src_dir, "HONEST_REVIEW.md"), "HONEST_REVIEW.md"))
transfer_manifest.append((os.path.join(src_dir, "W4_HANDOFF.md"), "W4_HANDOFF.md"))
transfer_manifest.append((os.path.join(src_dir, "PROVENANCE_ADDENDUM.md"), "PROVENANCE_ADDENDUM.md"))

# First generate pre-manifest entries for hashing
file_entries = []
for src_path, rel_path in transfer_manifest:
    if not os.path.exists(src_path):
        raise FileNotFoundError(f"Source file missing: {src_path}")
    size = os.path.getsize(src_path)
    h = sha256_file(src_path)
    file_entries.append({
        "path": rel_path.replace("\\", "/"),
        "size_bytes": size,
        "sha256": h
    })

# Add placeholder for EVIDENCE_MANIFEST.json and EVIDENCE_INDEX.csv
evidence_manifest_path = os.path.join(src_dir, "EVIDENCE_MANIFEST.json")
evidence_index_path = os.path.join(src_dir, "EVIDENCE_INDEX.csv")

# 2. Build and write initial EVIDENCE_INDEX.csv
def write_index(entries, out_path):
    with open(out_path, "w", newline="", encoding="utf-8") as f:
        writer = csv.writer(f)
        writer.writerow(["relative_path", "size_bytes", "sha256"])
        for e in entries:
            writer.writerow([e["path"], e["size_bytes"], e["sha256"]])

write_index(file_entries, evidence_index_path)

# 3. Build and write initial EVIDENCE_MANIFEST.json
manifest_doc = {
    "artifact_id": "ER-RUN-20261014-003-EVIDENCE-MANIFEST",
    "run_id": "ER-RUN-20261014-003",
    "predecessor_runs": ["ER-RUN-20261014-001", "ER-RUN-20261014-002"],
    "authority": "OWNER-UPDATE-20261013-007",
    "continuation_authority": "OWNER-UPDATE-20261013-008/002 / OWNER-UPDATE-20261014-001 / OWNER-UPDATE-20261014-002",
    "governance_event": "ER-CHANGE-20261014-017",
    "governance_dispatch": "ER-DISPATCH-20261014-011",
    "timestamp_utc": "2026-10-14T15:20:00Z",
    "base_commit": "3cd3b4b8f903adef6f6d7cf3c922c2ce184c2fc2",
    "branch": "feat/phase11-subledger-implementation",
    "verdict": "SUBMITTED_FOR_GOVERNANCE_REVIEW",
    "database_target_isolation": "VERIFIED_DEDICATED_TEST_TARGET",
    "database_isolation_policy": (
        "Test suites are strictly configured and verified to target only the"
        " dedicated disposable PostgreSQL 18 cluster on loopback port 55437"
        " (database: edgeretails_test_run20261012_001). Zero operational"
        " database authorization or credentials exist in test configuration;"
        " connection strings to port 5432 or production hosts are prohibited"
        " and fail-closed by configuration. No network counter telemetry was"
        " instrumented to prove machine-wide zero connection attempts."
    ),
    "packaging_scope_demarcation": (
        "Frozen software candidate under test is bound by base commit"
        " 3cd3b4b8f903adef6f6d7cf3c922c2ce184c2fc2, candidate_tracked.patch"
        " (SHA256:"
        " e949c57f4872f4fba5b08cbb6293ee86009bb4a270f11349b2dd17a47a8c4dea),"
        " candidate_untracked.zip (SHA256:"
        " 96e33d1f38ba7ee31bd01cff315819413bd20f422c1c79e0f8db2dfc748c103d),"
        " and candidate_source_bundle.zip (SHA256:"
        " ee2ff882fcd1ebaea9e86e575f84b5c264827266e3945ae426fb781372545929)."
        " Zero runtime or test files modified after export. Packaging scripts"
        " and PROVENANCE_ADDENDUM.md are post-export tooling."
    ),
    "total_files": len(file_entries) + 2,
    "summary_counts": {
        "tasks": 8,
        "task_files": 16,
        "export_files": 6,
        "raw_test_runs_and_logs": 7,
        "review_files": 4,
        "delivery_files": 3,
        "root_docs": 3,
        "manifest_and_index": 2
    },
    "test_totals": {
        "filtered_wave3_unit_tests": {
            "passed": 20,
            "failed": 0,
            "skipped": 0,
            "trx": "raw/unit_tests.trx"
        },
        "desktop_cutover_tests": {
            "passed": 26,
            "failed": 0,
            "skipped": 0,
            "trx": "raw/desktop_tests.trx"
        },
        "postgres18_durable_integration_tests": {
            "passed": 21,
            "failed": 0,
            "skipped": 0,
            "trx": "raw/integration_tests.trx"
        },
        "full_regression_tests": {
            "passed": 1301,
            "failed": 0,
            "skipped": 0,
            "trx": "raw/unit_tests_full_regression.trx"
        },
        "desktop_full_suite": {
            "passed": 294,
            "failed": 0,
            "skipped": 0,
            "total": 294,
            "disposition": "REG-W3-001 resolved at root cause (294 passed, 0 failed)",
            "trx": "raw/desktop_full_294.trx"
        },
        "isolated_reconstruction": {
            "status": "PASSED",
            "base_commit": "3cd3b4b8f903adef6f6d7cf3c922c2ce184c2fc2",
            "build_errors": 0,
            "build_warnings": 0,
            "unit_tests_passed": 20,
            "log": "raw/reconstruction_verification.log"
        }
    },
    "files": file_entries
}

with open(evidence_manifest_path, "w", encoding="utf-8") as f:
    json.dump(manifest_doc, f, indent=2)

# Now include delivery/EVIDENCE_MANIFEST.json, delivery/EVIDENCE_INDEX.csv, and root copies in transfer
transfer_manifest.append((evidence_manifest_path, "delivery/EVIDENCE_MANIFEST.json"))
transfer_manifest.append((evidence_index_path, "delivery/EVIDENCE_INDEX.csv"))
transfer_manifest.append((evidence_manifest_path, "EVIDENCE_MANIFEST.json"))
transfer_manifest.append((evidence_index_path, "EVIDENCE_INDEX.csv"))

print(f"=== Beginning Synchronized Delivery to {gov_run_dir} ===")

copied_records = []
for src_file, dest_rel in transfer_manifest:
    dest_file = os.path.join(gov_run_dir, dest_rel)
    os.makedirs(os.path.dirname(dest_file), exist_ok=True)
    shutil.copy2(src_file, dest_file)
    src_hash = sha256_file(src_file)
    dest_hash = sha256_file(dest_file)
    if src_hash != dest_hash:
        raise ValueError(f"Hash mismatch for {dest_rel}: {src_hash} vs {dest_hash}")
    size = os.path.getsize(dest_file)
    copied_records.append((dest_rel, size, dest_hash))
    print(f"[COPIED & VERIFIED] {dest_rel} ({size} bytes, sha256: {dest_hash[:16]}...)")

print("\n=== Readback Verification of Delivered Artifacts ===")

for dest_rel, size, expected_hash in copied_records:
    dest_file = os.path.join(gov_run_dir, dest_rel)
    if not os.path.exists(dest_file):
        raise FileNotFoundError(f"Readback failure: {dest_file} missing")
    actual_hash = sha256_file(dest_file)
    if actual_hash != expected_hash:
        raise ValueError(f"Readback hash corruption on {dest_rel}")

    # Specific readback validations
    if dest_rel.endswith(".json"):
        with open(dest_file, "r", encoding="utf-8") as jf:
            json.load(jf)
    elif dest_rel.endswith(".trx"):
        tree = ET.parse(dest_file)
        root = tree.getroot()
        counters = None
        for elem in root.iter():
            if elem.tag.endswith("Counters"):
                counters = elem.attrib
                break
        print(f"  TRX Readback {dest_rel}: total={counters.get('total')} passed={counters.get('passed')} failed={counters.get('failed')}")

print(f"\n=== All {len(copied_records)} Files Successfully Delivered and Verified byte-for-byte in {gov_run_dir} ===")
