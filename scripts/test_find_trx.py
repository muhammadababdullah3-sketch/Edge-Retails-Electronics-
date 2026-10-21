import json

trx_path = r"artifacts\governance_w4_run006\raw\trx_summary.json"
with open(trx_path, "r", encoding="utf-8") as f:
    trx_summary = json.load(f)

for target in ["AttestOwnedPostgres", "CAT01", "Pack_InitialIntake", "U12", "T01_TripleDate", "GT01"]:
    matches = []
    for trx_file, v in trx_summary.items():
        if not trx_file.endswith(".trx"):
            continue
        for t in v["tests"]:
            if target in t["test_name"]:
                matches.append((t["test_name"], t["execution_id"]))
    print(f"Target '{target}': found {len(matches)} matches")
    if matches:
        print("  Sample:", matches[0][0][:80], "->", matches[0][1])
