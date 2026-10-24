import re

path = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\11-submitted-reviews\2026-10-11\remediation001\GOVERNANCE_REVIEW.md"
with open(path, "r", encoding="utf-8") as f:
    text = f.read()

# find all ER-RRV sections
sections = re.findall(r"(ER-RRV-\d+.*?\n)(?=ER-RRV|\Z)", text, re.DOTALL)
print(f"Total ER-RRV sections: {len(sections)}")
for s in sections[:5]:
    lines = s.strip().splitlines()
    print(" ", lines[0])
