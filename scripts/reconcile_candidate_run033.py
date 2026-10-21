import json
import os
import hashlib
import subprocess

inv_path = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-20261018-025\export\CANDIDATE_FILE_INVENTORY.json"
with open(inv_path, "r", encoding="utf-8") as f:
    inv = json.load(f)

print(f"Total files in Run 025 inventory: {len(inv['files'])}")

diffs = []
missing = []
identical = []

for entry in inv["files"]:
    path = entry["path"]
    old_hash = entry["sha256"]
    old_size = entry["size_bytes"]
    if not os.path.exists(path):
        missing.append(path)
        continue
    with open(path, "rb") as f:
        data = f.read()
    new_hash = hashlib.sha256(data).hexdigest().lower()
    new_size = len(data)
    if new_hash == old_hash.lower():
        identical.append(path)
    else:
        diffs.append((path, old_hash, new_hash, old_size, new_size, entry["is_tracked"]))

print(f"Identical: {len(identical)}")
print(f"Missing: {len(missing)}")
print(f"Modified: {len(diffs)}")

print("\nModified files vs Run 025:")
for d in diffs:
    print(f"  {d[0]} (tracked={d[5]}):")
    print(f"    Old: {d[1]} ({d[3]} bytes)")
    print(f"    New: {d[2]} ({d[4]} bytes)")

if missing:
    print("\nMissing files:")
    for m in missing:
        print(f"  {m}")
