# scripts/finalize_change_event_041.py
import sys
from pathlib import Path
import json

GOV_ROOT = Path(r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body")
sys.path.insert(0, str(GOV_ROOT / "tools"))
from evidence_v2 import adjudicate_scope, finish, event

target = event(GOV_ROOT, "ER-CHANGE-20261018-041")

# Clean up INCOMPLETE_EVIDENCE.json from prior failed run
incomplete = target / "INCOMPLETE_EVIDENCE.json"
if incomplete.exists():
    incomplete.unlink()
    print("Removed stale INCOMPLETE_EVIDENCE.json")

# Adjudicate scope exclusions for staging and parallel event paths
excluded_paths = [
    "governance/10-execution-runs/ER-RUN-20261018-032-staging/",
    "governance/10-execution-runs/ER-RUN-CONE-WAVE-VERIFY-20261018-040/",
    "governance/15-reviews/2026-10-18-central-waves040/"
]

print("Adjudicating scope...")
adjudicate_scope(
    GOV_ROOT,
    "ER-CHANGE-20261018-041",
    excluded_paths,
    "Exclude temporary staging path and parallel ER-CHANGE-20261018-040 CONE wave review artifacts"
)
print("Scope adjudication created.")

# Finalize event
print("Finalizing event ER-CHANGE-20261018-041...")
result = finish(GOV_ROOT, "ER-CHANGE-20261018-041")
print("SUCCESS:", json.dumps(result, indent=2))
