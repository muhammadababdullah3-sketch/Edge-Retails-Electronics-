import os
import json
import hashlib
from datetime import datetime, timezone

RUN_DIR = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-20261015-010"
BASE_COMMIT = "3cd3b4b8f903adef6f6d7cf3c922c2ce184c2fc2"

def sha256_file(filepath):
    h = hashlib.sha256()
    with open(filepath, "rb") as f:
        while chunk := f.read(65536):
            h.update(chunk)
    return h.hexdigest().lower()

artifact_rel_paths = [
    "CLOSURE_AND_BACKDATE_POLICY_ADDENDUM.md",
    "LEGACY_FINGERPRINT_SECURITY_AUDIT.md",
    "PHASE12_WAVE1_DEFECT_REMEDIATION.json",
    "PHASE12_WAVE1_RECOVERY_REPORT.md",
    "PHASE12_WAVE1_TASK_RECEIPTS.json",
    "export/BUILD_LOGS.json",
    "export/CANDIDATE_FILE_INVENTORY.json",
    "export/RECONSTRUCTION_INSTRUCTIONS.md",
    "export/RECONSTRUCTION_VERIFICATION_RECORD.json",
    "export/Verify-CandidateExport.ps1",
    "export/candidate_source_bundle.zip",
    "export/candidate_tracked.patch",
    "export/candidate_untracked.zip",
    "raw/RAW_TEST_EXECUTION_LEDGER.json",
    "raw/desktop_tests.log",
    "raw/desktop_tests.trx",
    "raw/phase11_durable_posting_tests.log",
    "raw/phase11_durable_posting_tests.trx",
    "raw/phase11_pg_tests.log",
    "raw/phase11_pg_tests.trx",
    "raw/phase12_initial_failed_attempt.log",
    "raw/phase12_wave1_pg_tests.log",
    "raw/phase12_wave1_pg_tests.trx",
    "raw/safety_guard_pg_tests.log",
    "raw/safety_guard_pg_tests.trx",
    "raw/tracking_pg_tests.log",
    "raw/tracking_pg_tests.trx",
    "raw/unit_tests.log",
    "raw/unit_tests.trx"
]

artifacts = []
for rel_path in sorted(artifact_rel_paths):
    full_path = os.path.join(RUN_DIR, rel_path.replace("/", os.sep))
    if not os.path.exists(full_path):
        raise FileNotFoundError(f"Missing artifact: {full_path}")
    size = os.path.getsize(full_path)
    sha = sha256_file(full_path)
    artifacts.append({
        "path": rel_path,
        "size_bytes": size,
        "sha256": sha
    })

manifest = {
    "artifact_id": "ER-RUN-20261015-010-EVIDENCE-MANIFEST",
    "run_id": "ER-RUN-20261015-010",
    "governance_event": "ER-CHANGE-20261015-036",
    "authority": "OWNER-UPDATE-20261015-001",
    "timestamp_utc": datetime.now(timezone.utc).isoformat(),
    "base_commit": BASE_COMMIT,
    "branch": "feat/phase11-subledger-implementation",
    "total_deposited_artifacts": len(artifacts),
    "core_governance_reports_count": 5,
    "export_artifacts_count": 8,
    "raw_test_artifacts_count": 16,
    "artifacts": artifacts
}

manifest_path = os.path.join(RUN_DIR, "EVIDENCE_MANIFEST.json")
with open(manifest_path, "w", encoding="utf-8") as f:
    json.dump(manifest, f, indent=2)

manifest_hash = sha256_file(manifest_path)
print(f"EVIDENCE_MANIFEST.json created. SHA256: {manifest_hash}")

delivery_index = {
    "delivery_id": "ER-RUN-20261015-010-DELIVERY",
    "run_id": "ER-RUN-20261015-010",
    "title": "Phase 12 Wave 1 Core Server Idempotency Authority Engine Recovery & Verification Delivery",
    "authority": "OWNER-UPDATE-20261015-001",
    "timestamp_utc": datetime.now(timezone.utc).isoformat(),
    "base_commit": BASE_COMMIT,
    "status": "PHASE12_WAVE1_VERIFIED_AND_DEPOSITED_AWAITING_INDEPENDENT_REVIEW",
    "summary": {
        "active_phase": "Phase 12",
        "wave": "Wave 1: Core Server Idempotency Authority Engine",
        "total_test_suites": 7,
        "total_tests_passed": 1755,
        "total_tests_failed": 0,
        "cumulative_candidate_files": 228,
        "offline_reconstruction_status": "PASSED_ZERO_WARNINGS_ZERO_ERRORS",
        "defect_h01_tamper_rejection": "ELIMINATED_AND_VERIFIED",
        "defect_h02_race_elimination": "ELIMINATED_AND_VERIFIED",
        "defect_b07_response_caching": "ELIMINATED_AND_VERIFIED",
        "mon_p12w1_001_original_task_meanings": "RESOLVED_EXACT_20_TASKS_RESTORED",
        "mon_p12w1_002_production_transaction_integration": "RESOLVED_COMPLETE_SALE_HANDLER_AND_FAILURE_ROLLBACK_PROVEN",
        "mon_p12w1_003_operation_type_binding": "RESOLVED_FAIL_CLOSED_CHECK_AND_TEST_PROVEN",
        "mon_p12w1_004_report_code_reconciliation": "RESOLVED_SIGNATURES_TEST_NAMES_AND_LEGACY_RUNTIME_TEST_PROVEN",
        "phase11_residual_108": "PARTIAL_RETAINED",
        "phase11_residual_119": "EXTERNAL_BLOCKER_RETAINED"
    },
    "key_deliverables": {
        "execution_report": "PHASE12_WAVE1_RECOVERY_REPORT.md",
        "policy_addendum": "CLOSURE_AND_BACKDATE_POLICY_ADDENDUM.md",
        "legacy_fingerprint_audit": "LEGACY_FINGERPRINT_SECURITY_AUDIT.md",
        "defect_remediation": "PHASE12_WAVE1_DEFECT_REMEDIATION.json",
        "task_receipts": "PHASE12_WAVE1_TASK_RECEIPTS.json",
        "candidate_inventory": "export/CANDIDATE_FILE_INVENTORY.json",
        "candidate_patch": "export/candidate_tracked.patch",
        "candidate_untracked_zip": "export/candidate_untracked.zip",
        "candidate_source_bundle": "export/candidate_source_bundle.zip",
        "reconstruction_verification": "export/RECONSTRUCTION_VERIFICATION_RECORD.json",
        "build_logs": "export/BUILD_LOGS.json",
        "raw_test_ledger": "raw/RAW_TEST_EXECUTION_LEDGER.json",
        "evidence_manifest": "EVIDENCE_MANIFEST.json"
    }
}

index_path = os.path.join(RUN_DIR, "DELIVERY_INDEX.json")
with open(index_path, "w", encoding="utf-8") as f:
    json.dump(delivery_index, f, indent=2)

index_hash = sha256_file(index_path)
print(f"DELIVERY_INDEX.json created. SHA256: {index_hash}")

# Author Verify-Wave1Readback.ps1
verify_ps1 = f'''# Verify-Wave1Readback.ps1
# Bit-for-bit SHA256 readback verification for ER-RUN-20261015-010
$runDir = $PSScriptRoot
$manifestPath = Join-Path $runDir "EVIDENCE_MANIFEST.json"
$indexPath = Join-Path $runDir "DELIVERY_INDEX.json"

$expectedManifestHash = "{manifest_hash}"
$expectedIndexHash = "{index_hash}"

Write-Host "Verifying ER-RUN-20261015-010 Delivery Manifest & Artifacts..." -ForegroundColor Cyan

$mHash = (Get-FileHash $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
$iHash = (Get-FileHash $indexPath -Algorithm SHA256).Hash.ToLowerInvariant()

$errors = 0
if ($mHash -ne $expectedManifestHash.ToLowerInvariant()) {{
    Write-Error "EVIDENCE_MANIFEST.json hash mismatch!"
    $errors++
}} else {{
    Write-Host "[OK] EVIDENCE_MANIFEST.json verified: $mHash" -ForegroundColor Green
}}

if ($iHash -ne $expectedIndexHash.ToLowerInvariant()) {{
    Write-Error "DELIVERY_INDEX.json hash mismatch!"
    $errors++
}} else {{
    Write-Host "[OK] DELIVERY_INDEX.json verified: $iHash" -ForegroundColor Green
}}

$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$count = 0
foreach ($art in $manifest.artifacts) {{
    $artPath = Join-Path $runDir ($art.path -replace '/', [IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path $artPath)) {{
        Write-Error "Missing artifact: $($art.path)"
        $errors++
        continue
    }}
    $actualHash = (Get-FileHash $artPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $art.sha256.ToLowerInvariant()) {{
        Write-Error "Hash mismatch for $($art.path)! Expected: $($art.sha256), Actual: $actualHash"
        $errors++
    }} else {{
        $count++
    }}
}}

if ($errors -eq 0) {{
    Write-Host "`nSUCCESS: All $count artifacts verified with 100% bit-for-bit SHA256 match!" -ForegroundColor Green
    exit 0
}} else {{
    Write-Host "`nFAILURE: $errors errors detected during readback verification." -ForegroundColor Red
    exit 1
}}
'''

with open(os.path.join(RUN_DIR, "Verify-Wave1Readback.ps1"), "w", encoding="utf-8") as f:
    f.write(verify_ps1)

# Author Verify-Wave1Readback.py
verify_py = f'''# Verify-Wave1Readback.py
# Bit-for-bit SHA256 readback verification for ER-RUN-20261015-010
import os
import json
import hashlib
import sys

RUN_DIR = os.path.dirname(os.path.abspath(__file__))
MANIFEST_PATH = os.path.join(RUN_DIR, "EVIDENCE_MANIFEST.json")
INDEX_PATH = os.path.join(RUN_DIR, "DELIVERY_INDEX.json")

EXPECTED_MANIFEST_HASH = "{manifest_hash}"
EXPECTED_INDEX_HASH = "{index_hash}"

def sha256_file(filepath):
    h = hashlib.sha256()
    with open(filepath, "rb") as f:
        while chunk := f.read(65536):
            h.update(chunk)
    return h.hexdigest().lower()

print("Verifying ER-RUN-20261015-010 Delivery Manifest & Artifacts (Python)...")

errors = 0
m_hash = sha256_file(MANIFEST_PATH)
i_hash = sha256_file(INDEX_PATH)

if m_hash != EXPECTED_MANIFEST_HASH.lower():
    print(f"ERROR: EVIDENCE_MANIFEST.json hash mismatch! Expected {{EXPECTED_MANIFEST_HASH}}, got {{m_hash}}")
    errors += 1
else:
    print(f"[OK] EVIDENCE_MANIFEST.json verified: {{m_hash}}")

if i_hash != EXPECTED_INDEX_HASH.lower():
    print(f"ERROR: DELIVERY_INDEX.json hash mismatch! Expected {{EXPECTED_INDEX_HASH}}, got {{i_hash}}")
    errors += 1
else:
    print(f"[OK] DELIVERY_INDEX.json verified: {{i_hash}}")

with open(MANIFEST_PATH, "r", encoding="utf-8") as f:
    manifest = json.load(f)

count = 0
for art in manifest["artifacts"]:
    art_path = os.path.join(RUN_DIR, art["path"].replace("/", os.sep))
    if not os.path.exists(art_path):
        print(f"ERROR: Missing artifact: {{art['path']}}")
        errors += 1
        continue
    actual_hash = sha256_file(art_path)
    if actual_hash != art["sha256"].lower():
        print(f"ERROR: Hash mismatch for {{art['path']}}! Expected {{art['sha256']}}, got {{actual_hash}}")
        errors += 1
    else:
        count += 1

if errors == 0:
    print(f"\\nSUCCESS: All {{count}} artifacts verified with 100% bit-for-bit SHA256 match!")
    sys.exit(0)
else:
    print(f"\\nFAILURE: {{errors}} errors detected during readback verification.")
    sys.exit(1)
'''

with open(os.path.join(RUN_DIR, "Verify-Wave1Readback.py"), "w", encoding="utf-8") as f:
    f.write(verify_py)

print("Verification scripts generated successfully.")
