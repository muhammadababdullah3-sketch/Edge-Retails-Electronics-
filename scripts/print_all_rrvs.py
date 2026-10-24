import re

path = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\11-submitted-reviews\2026-10-11\remediation001\GOVERNANCE_REVIEW.md"
with open(path, "r", encoding="utf-8") as f:
    text = f.read()

for m in re.finditer(r"(ER-RRV-\d+[^.\n]+)", text):
    print(m.group(1))
