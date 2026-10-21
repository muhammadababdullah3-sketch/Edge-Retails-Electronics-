import json

pi_path = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\13-prerequisite-resolution\2026-10-11\PLANNED_TEST_INVENTORY.json"
with open(pi_path, encoding="utf-8") as f:
    d = json.load(f)

target_ids = ["U04", "U05", "U12", "U17", "U21", "U22", "U23", "P11-PLAN-CONCURRENCY-14"]
for t in d.get("tests", []):
    if t.get("TestId") in target_ids:
        print("="*60)
        print("TestId:       ", t.get("TestId"))
        print("Group:        ", t.get("Group"))
        print("ProposedMethod:", t.get("ProposedMethod"))
        print("Setup:        ", t.get("Setup"))
        print("Action:       ", t.get("Action"))
        print("Assertion:    ", t.get("IndependentAssertion"))
