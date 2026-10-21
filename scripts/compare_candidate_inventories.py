import json

inv007_path = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-20261015-007\export\CANDIDATE_FILE_INVENTORY.json"
inv009_path = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-20261015-009\export\CANDIDATE_FILE_INVENTORY.json"

with open(inv007_path, "r", encoding="utf-8") as f:
    d007 = json.load(f)

with open(inv009_path, "r", encoding="utf-8") as f:
    d009 = json.load(f)

files007 = set(x["path"] for x in d007["files"])
files009 = set(x["path"] for x in d009["files"])

in_007_not_009 = files007 - files009
in_009_not_007 = files009 - files007

print(f"Files in 007: {len(files007)}")
print(f"Files in 009: {len(files009)}")
print(f"\nFiles in 007 but not in 009 ({len(in_007_not_009)}):")
for f in sorted(in_007_not_009):
    print("  ", f)

print(f"\nFiles in 009 but not in 007 ({len(in_009_not_007)}):")
for f in sorted(in_009_not_007):
    print("  ", f)
