import json
with open(r"artifacts\governance_w4_run006\review\16_CAPABILITY_GATES_ADJUDICATION.json", "r", encoding="utf-8") as f:
    cg = json.load(f)
print("16 CAPABILITY GATES: type =", type(cg).__name__, "len =", len(cg) if isinstance(cg, (list, dict)) else 0)
if isinstance(cg, list):
    print("Sample:", cg[0]["gate_id"] if "gate_id" in cg[0] else cg[0].keys())
elif isinstance(cg, dict):
    print("Keys:", list(cg.keys())[:5])

with open(r"artifacts\governance_w4_run006\review\FINAL_21_ADJUDICATION.json", "r", encoding="utf-8") as f:
    f21 = json.load(f)
print("FINAL 21: type =", type(f21).__name__, "len =", len(f21) if isinstance(f21, (list, dict)) else 0)
if isinstance(f21, list):
    print("Sample:", f21[0]["gate_id"] if "gate_id" in f21[0] else f21[0].keys())
elif isinstance(f21, dict):
    print("Keys:", list(f21.keys())[:5])
