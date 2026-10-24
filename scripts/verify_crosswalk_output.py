import json

with open("scripts/crosswalk_output.json", encoding="utf-8") as f:
    cw = json.load(f)

target_ids = ["U04", "U05", "U12", "U17", "U21", "U22", "U23", "P11-PLAN-CONCURRENCY-14"]
print(f"Total crosswalk entries: {len(cw)}")
for x in cw:
    if x["planned_test_id"] in target_ids:
        tid = x["planned_test_id"]
        status = x["coverage_status"]
        line = x["assertion_line"]
        rat = x["rationale"]
        print(f"{tid:25} | status: {status:32} | line: {line:3} | {rat[:55]}")
