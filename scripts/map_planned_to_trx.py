import json

pi_path = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\13-prerequisite-resolution\2026-10-11\PLANNED_TEST_INVENTORY.json"
with open(pi_path, encoding="utf-8") as f:
    pi = json.load(f)

planned_tests = pi.get("tests", [])

with open("artifacts/governance_w4/raw/trx_summary.json", encoding="utf-8") as f:
    trx_sum = json.load(f)

all_trx_tests = {}
for trx_file, v in trx_sum.items():
    if not trx_file.endswith(".trx"):
        continue
    for t in v["tests"]:
        all_trx_tests[t["test_name"]] = {
            "trx_file": trx_file,
            "test_id": t["test_id"],
            "execution_id": t["execution_id"],
            "duration": t["duration"],
            "start_time": t["start_time"],
            "end_time": t["end_time"],
            "outcome": t["outcome"]
        }

print(f"Group breakdown in planned_tests:")
groups = {}
for pt in planned_tests:
    g = pt["Group"]
    groups[g] = groups.get(g, []) + [pt]

for g, items in groups.items():
    print(f"\n=== Group {g}: {len(items)} items ===")
    for item in items:
        tid = item["TestId"]
        method = item["ProposedMethod"]
        selector = item.get("ProposedFullyQualifiedSelector", "")
        clean_sel = selector.replace("FullyQualifiedName=", "")

        # Check direct match
        matched = clean_sel if clean_sel in all_trx_tests else None

        if not matched:
            # Check by method
            candidates = [fqn for fqn in all_trx_tests if fqn.endswith("." + method) or (method + "(" in fqn)]
            if candidates:
                matched = candidates[0]

        if not matched:
            # Check partial prefix / keyword
            prefix = tid.replace("P11-PLAN-", "") # e.g. "GT01", "U01"
            candidates = [fqn for fqn in all_trx_tests if fqn.split(".")[-1].startswith(prefix + "_") or fqn.split(".")[-1].startswith(prefix + "(") or fqn.split(".")[-1] == prefix]
            if candidates:
                matched = candidates[0]

        status = f"MATCH: {matched} ({all_trx_tests[matched]['trx_file']})" if matched else "NO MATCH"
        print(f"  {tid:18} | {method:45} | {status}")
