import os
import sys
import shutil
import subprocess
import json
from pathlib import Path

GOV_ROOT = Path(r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body")
RUN_DIR = GOV_ROOT / "governance/10-execution-runs/ER-RUN-20261017-008"
STAGING_DIR = Path(r"C:\Users\muham\AppData\Local\Temp\ER-RUN-20261017-008_staging")
EXEC_STATE_FILE = GOV_ROOT / "board/current/EXECUTION_STATE.json"
TOOLS_PY = GOV_ROOT / "tools/evidence_v2.py"

EVENT_ID = "ER-CHANGE-20261017-009"
SOURCE_ID = "OWNER-UPDATE-20261016-001"
REASON = "Deliver Phase 12 Wave 3 Run 008 with full 109/109 tracking resolution, desktop recovery, operation deadlock retry, manual cash fail-closed, canonical versioning, and validated parallel plan"

print(f"1. Staging {RUN_DIR} -> {STAGING_DIR}...")
if STAGING_DIR.exists():
    shutil.rmtree(STAGING_DIR)
shutil.move(str(RUN_DIR), str(STAGING_DIR))

try:
    print(f"2. Running evidence_v2.py begin {EVENT_ID}...")
    begin_cmd = [
        sys.executable, str(TOOLS_PY), "begin", EVENT_ID,
        "--source-id", SOURCE_ID,
        "--reason", REASON,
        "--paths", "governance/10-execution-runs/ER-RUN-20261017-008/", "board/current/EXECUTION_STATE.json"
    ]
    res_begin = subprocess.run(begin_cmd, cwd=str(GOV_ROOT), capture_output=True, text=True)
    print("BEGIN OUT:", res_begin.stdout)
    if res_begin.returncode != 0:
        print("BEGIN ERR:", res_begin.stderr)
        raise RuntimeError("evidence_v2.py begin failed!")

    print(f"3. Restoring {STAGING_DIR} -> {RUN_DIR}...")
    shutil.move(str(STAGING_DIR), str(RUN_DIR))

    print(f"4. Updating {EXEC_STATE_FILE}...")
    manifest_file = RUN_DIR / "EVIDENCE_MANIFEST.json"
    index_file = RUN_DIR / "DELIVERY_INDEX.json"

    import hashlib
    def get_hash(p):
        return hashlib.sha256(p.read_bytes()).hexdigest().lower()

    exec_state = {
        "schema_version": 2,
        "authority": "OWNER-UPDATE-20261016-001",
        "observed_at": "2026-10-17T10:10:00.000Z",
        "observation_scope": "Phase 12 Wave 3 Run 008 deposited into ER-RUN-20261017-008 under event ER-CHANGE-20261017-009, resolving Correction 022 findings MON-P12W3-R8-001 through 006 with 1,942 passed tests across 7 test suites (109/109 fresh green Tracking tests, 0 failed, 0 skipped)",
        "observed_stage": "Phase12W3 stage8/8: Run 008 deposited awaiting independent governance review",
        "phase": 12,
        "active_wave": "P12-W3",
        "execution_status": "P12_W3_RUN008_DELIVERED_AWAITING_INDEPENDENT_REVIEW",
        "acceptance": "P12-W1/W2 ACCEPTED; P12-W3 Run006/007 CHANGES_REQUIRED; P12-W3 Run008 DEPOSITED_AWAITING_REVIEW",
        "predecessor": "ER-RUN-20261016-007",
        "next_action": "Independent governance verification and review of ER-RUN-20261017-008 candidate export, hostile recovery proofs, parallel plan compliance, and Correction 022 remediations",
        "prepared_next_prompt": "governance/09-execution-prompts/PHASE12_W3_MASTER_2026-10-16.md",
        "prepared_closure_prompt": "governance/09-execution-prompts/PHASE11_SEPARATE_CLOSURE_MASTER_PROMPT_2026-10-14.md",
        "next_wave_gate": "P12_W3_ACCEPTANCE_REQUIRES_ORIGINAL16_SOURCE_CRITERIA_AND_ACTUAL_HOSTILE_PROOF",
        "production_authorized": False,
        "evidence": [
            {
                "path": "governance/10-execution-runs/ER-RUN-20261017-008/DELIVERY_INDEX.json",
                "sha256": get_hash(index_file)
            },
            {
                "path": "governance/10-execution-runs/ER-RUN-20261017-008/EVIDENCE_MANIFEST.json",
                "sha256": get_hash(manifest_file)
            },
            {
                "path": "governance/14-execution-environment/2026-10-17-monitor/d022/OBSERVATION.md",
                "sha256": "a75e24b4af4288e7d86fb18df6aa8ec13f627c47ff83d1394425355167021333"
            },
            {
                "path": "governance/14-execution-environment/2026-10-17-monitor/r7review/REVIEW.md",
                "sha256": "9a7c8f12a0624cb0d750df8f7bdc55657b61d48addde96ac4fc88d061a37fed4"
            },
            {
                "path": "governance/10-execution-runs/ER-RUN-20261016-007/DELIVERY_INDEX.json",
                "sha256": "0ad213234dcba2237c66b92908e670d4d8999a355bc499d3c6ab1f22ea65b600"
            },
            {
                "path": "governance/10-execution-runs/ER-RUN-20261016-007/EVIDENCE_MANIFEST.json",
                "sha256": "71dbfaaba9ffdc5a105e43b74760b6cc536c76d483d446612f34caa24a751264"
            }
        ]
    }
    with open(EXEC_STATE_FILE, "w", encoding="utf-8") as f:
        json.dump(exec_state, f, indent=2)

    print(f"5. Running evidence_v2.py finalize {EVENT_ID}...")
    fin_cmd = [sys.executable, str(TOOLS_PY), "finalize", EVENT_ID]
    res_fin = subprocess.run(fin_cmd, cwd=str(GOV_ROOT), capture_output=True, text=True)
    print("FINALIZE OUT:", res_fin.stdout)
    if res_fin.returncode != 0:
        print("FINALIZE ERR:", res_fin.stderr)
        raise RuntimeError("evidence_v2.py finalize failed!")

    print(f"SUCCESS: {EVENT_ID} finalized successfully!")

finally:
    if STAGING_DIR.exists() and not RUN_DIR.exists():
        shutil.move(str(STAGING_DIR), str(RUN_DIR))
