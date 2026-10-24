import json
with open(r"artifacts\governance_w4_run006\review\16_CAPABILITY_GATES_ADJUDICATION.json", "r", encoding="utf-8") as f:
    cg = json.load(f)
print("16 CG keys:", cg.keys())
print("16 CG sample adjudication:", cg["adjudications"][0])

with open(r"artifacts\governance_w4_run006\review\FINAL_21_ADJUDICATION.json", "r", encoding="utf-8") as f:
    f21 = json.load(f)
print("F21 keys:", f21.keys())
print("F21 sample adjudication:", f21["adjudications"][0])
