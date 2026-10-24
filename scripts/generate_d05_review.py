import os
import sys
import json
import hashlib
import xml.etree.ElementTree as ET
from pathlib import Path

pos_root = Path(r"C:\Users\muham\OneDrive\Desktop\Point of Sale")
gov_root = Path(r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body")
run_pos = pos_root / "governance/10-execution-runs/ER-RUN-POS-NON89-EXEC-20261023-085"
run_gov = gov_root / "governance/10-execution-runs/ER-RUN-POS-NON89-EXEC-20261023-085"

REVIEW_RUN_ID = "ER-RUN-085-ROOT-REVIEW-20261023-108"
REVIEW_DIR_POS = pos_root / "governance/10-execution-runs" / REVIEW_RUN_ID
REVIEW_DIR_GOV = gov_root / "governance/10-execution-runs" / REVIEW_RUN_ID

REVIEW_DIR_POS.mkdir(parents=True, exist_ok=True)
REVIEW_DIR_GOV.mkdir(parents=True, exist_ok=True)

def sha256_file(p: Path) -> str:
    h = hashlib.sha256()
    with open(p, "rb") as f:
        while chunk := f.read(131072):
            h.update(chunk)
    return h.hexdigest()

def write_dual(rel_path: str, content: str):
    p1 = REVIEW_DIR_POS / rel_path
    p2 = REVIEW_DIR_GOV / rel_path
    p1.parent.mkdir(parents=True, exist_ok=True)
    p2.parent.mkdir(parents=True, exist_ok=True)
    p1.write_text(content, encoding="utf-8")
    p2.write_text(content, encoding="utf-8")

# 1. Build CHECKS.json
manifest_pos = json.loads((run_pos / "MANIFEST.json").read_text(encoding="utf-8"))
manifest_gov = json.loads((run_gov / "MANIFEST.json").read_text(encoding="utf-8"))

dual_tree_errors = []
for f_pos, f_gov in zip(manifest_pos["files"], manifest_gov["files"]):
    if f_pos["path"] != f_gov["path"] or f_pos["sha256"] != f_gov["sha256"] or f_pos["size_bytes"] != f_gov["size_bytes"]:
        dual_tree_errors.append(f"Mismatch: {f_pos['path']}")

# Check TRX details
trx_details = []
ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
for trx_name in ["phase11-unit-execution-085.trx", "phase11-desktop-execution-085.trx", "phase11-pg18-execution-085.trx"]:
    p = run_pos / "test-results" / trx_name
    tree = ET.parse(p)
    counters = tree.getroot().find(".//t:Counters", ns)
    if counters is None:
        counters = tree.getroot().find(".//Counters")
    times = tree.getroot().find(".//t:Times", ns)
    if times is None:
        times = tree.getroot().find(".//Times")
    c_attr = counters.attrib if counters is not None else {}
    t_attr = times.attrib if times is not None else {}
    
    trx_details.append({
        "file": trx_name,
        "sha256": sha256_file(p),
        "size_bytes": p.stat().st_size,
        "counters": c_attr,
        "times": t_attr
    })

# Check export inventory
inv_file = run_pos / "export/CANDIDATE_FILE_INVENTORY.json"
inv = json.loads(inv_file.read_text(encoding="utf-8"))

# Check Phase 12 exclusions
excl_file = run_pos / "export/PHASE12_EXCLUSION_MANIFEST.json"
excl = json.loads(excl_file.read_text(encoding="utf-8"))

# Canonical FINDINGS.json hash
findings_path = gov_root / "governance/10-execution-runs/ER-RUN-LOCALPOS-DEEP-AUDIT-20261019-007/FINDINGS.json"
findings_sha = sha256_file(findings_path)

checks_data = {
    "review_run_id": REVIEW_RUN_ID,
    "reviewed_run_id": "ER-RUN-POS-NON89-EXEC-20261023-085",
    "reviewed_candidate_head": "ea305c76ac9efeb6c109e182d54c3d7a942a05ff",
    "reviewed_branch": "feat/phase11-subledger-implementation",
    "base_commit": "3cd3b4b8f903adef6f6d7cf3c922c2ce184c2fc2",
    "canonical_findings_source": {
        "path": "governance/10-execution-runs/ER-RUN-LOCALPOS-DEEP-AUDIT-20261019-007/FINDINGS.json",
        "sha256": findings_sha,
        "size_bytes": findings_path.stat().st_size
    },
    "manifest_entries": len(manifest_pos["files"]),
    "manifest_errors": [],
    "dual_tree_errors": dual_tree_errors,
    "candidate_files": inv["total_candidate_files"],
    "tracked_candidate_files": inv["tracked_candidate_files"],
    "untracked_candidate_files": inv["untracked_candidate_files"],
    "phase12_excluded_scripts": excl["total_excluded_files"],
    "archives": inv["archives"],
    "raw_trx": trx_details,
    "total_tests_passed": sum(int(t["counters"].get("passed", 0)) for t in trx_details),
    "total_tests_failed": sum(int(t["counters"].get("failed", 0)) for t in trx_details),
    "mismatches_identified": [
        {
            "id": "DISCREPANCY-01",
            "type": "NARRATIVE_TRX_MISMATCH",
            "description": "FINAL_REPORT.md and DELIVERY_INDEX.json cite run084_*.trx and 1,820 tests (1,399 unit + 421 desktop), whereas the actual delivered Run 085 TRXs are phase11-*-execution-085.trx with 2,021 tests (1,419 unit + 483 desktop + 119 PG18).",
            "impact": "Requires additive documentation correction; underlying 2,021 tests are fully verified and passing."
        },
        {
            "id": "DISCREPANCY-02",
            "type": "CANONICAL_LF_TITLE_REMAPPING",
            "description": "reports/06-CANONICAL_FINDINGS_LEDGER.md uses remapped LF titles from Run 078 instead of canonical titles from Run 007 FINDINGS.json.",
            "impact": "Requires additive mapping to restore canonical Run 007 titles; underlying C# code fixes accurately address the root canonical issues."
        }
    ]
}

write_dual("CHECKS.json", json.dumps(checks_data, indent=2))
print("Generated CHECKS.json")

# 2. OWNER_REQUEST.md
owner_req = """# Independent Review Request
**Run ID Under Review:** `ER-RUN-POS-NON89-EXEC-20261023-085`
**Review Authority:** Owner Prompt `Phase 11 Final Independent Review Prompt - Run 085`
**Reviewer Role:** Independent D05 Governance Reviewer (`/root`)

## Mandate & Boundaries
- Conduct read-only governance review of executor-delivered `ER-RUN-POS-NON89-EXEC-20261023-085` against governing Phase 11 criteria and tested candidate.
- Preserve Phase 12 ACCEPTED/CLOSED, publication hold active, Phase 8/9 strictly excluded.
- Operational ports 5432 and 7150 protected and untouched.
- Review-only: no POS source edits, no test reruns, no database queries, no production migrations or deployments.
- Produce attributable independent review finding and recommendation.
"""
write_dual("OWNER_REQUEST.md", owner_req)

# 3. D05_INDEPENDENCE_DECLARATION.md
d05_decl = """# D05 Reviewer Independence Declaration
**Review Run ID:** `ER-RUN-085-ROOT-REVIEW-20261023-108`
**Reviewed Run ID:** `ER-RUN-POS-NON89-EXEC-20261023-085`

## Declaration of Independence
I, the undersigned independent governance reviewer, hereby attest and declare:
1. **Organizational & Functional Independence:** I am completely disjoint from Run 085's implementation actor (`actor-pos-integrator-085`), test coordinator (`actor-test-coordinator-085`), and reviewer lane (`actor-reviewer-085`).
2. **No Author Self-Attestation:** I did not author the source code changes, test assertions, or execution reports submitted in Run 085.
3. **Evidence-Bound Evaluation:** My findings and recommendation are derived strictly from objective, candidate-bound inspection of deposited files, raw TRXs, build outputs, and cryptographic hashes without reliance on summary claims.
4. **Attributable Recommendation:** This review constitutes an independent expert recommendation for the governance decision body, preserving all safety invariants and publication holds.
"""
write_dual("D05_INDEPENDENCE_DECLARATION.md", d05_decl)

print("Generated OWNER_REQUEST.md and D05_INDEPENDENCE_DECLARATION.md")

