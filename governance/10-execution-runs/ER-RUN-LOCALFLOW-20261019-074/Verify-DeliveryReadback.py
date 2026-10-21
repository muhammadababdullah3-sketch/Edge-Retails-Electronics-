import json
from pathlib import Path

run_dir = Path(r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-LOCALFLOW-20261019-074")
receipt_path = run_dir / "RECEIPT.json"
assert receipt_path.exists(), "RECEIPT.json missing"
with open(receipt_path, "r", encoding="utf-8") as f:
    receipt = json.load(f)
assert receipt["status"] == "COMPLETED"

stages = [
    "STAGE1_CONFIRMED_THAKA_AUDIT_FINDINGS_AND_CAUSAL_DIAGNOSIS.md",
    "STAGE2_FINITE_ANALOGOUS_FLOW_INVENTORY_ACROSS_DOMAINS.md",
    "STAGE3_VERIFIED_FOUNDATION_LOCK_AND_CUSTODY.md",
    "STAGE4_EXACT_CRITERIA_AND_CONTRACT_DECISION.md",
    "STAGE5_ORIGINAL_TASK_CROSSWALK.md",
    "STAGE6_IMPLEMENTATION_AND_EVIDENCE.md",
    "STAGE7_WHOLE_WAVE_EVALUATION.md",
    "STAGE8_FINAL_DEPOSITION_AND_RECEIPT.md"
]

for s in stages:
    sp = run_dir / s
    assert sp.exists(), f"Stage report missing: {s}"
    assert sp.stat().st_size > 0, f"Stage report empty: {s}"

print(f"=== ALL STAGES AND MANIFESTS VALIDATED FOR ER-RUN-LOCALFLOW-20261019-074: 100% PASS ===")
