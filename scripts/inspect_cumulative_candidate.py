import os
import subprocess
import json
import hashlib

POS_DIR = r"C:\Users\muham\OneDrive\Desktop\Point of Sale"
BASE_COMMIT = "3cd3b4b8f903adef6f6d7cf3c922c2ce184c2fc2"

def sha256_file(filepath):
    h = hashlib.sha256()
    with open(filepath, "rb") as f:
        while chunk := f.read(65536):
            h.update(chunk)
    return h.hexdigest().lower()

# Get tracked modified/added files relative to base commit
git_diff_files = subprocess.check_output(
    ["git", "diff", "--name-only", BASE_COMMIT],
    cwd=POS_DIR, text=True
).splitlines()

# Get untracked files
git_untracked_files = subprocess.check_output(
    ["git", "status", "--porcelain"],
    cwd=POS_DIR, text=True
).splitlines()

untracked_candidates = []
for line in git_untracked_files:
    if line.startswith("?? "):
        path = line[3:].strip()
        # Only include solution source, tests, migrations, contracts, views, etc.
        # Exclude temporary scratch scripts and review docs unless relevant
        if path.startswith("src/") or path.startswith("tests/") or path.endswith(".sln"):
            untracked_candidates.append(path)

print(f"Tracked modified files relative to base commit: {len(git_diff_files)}")
print(f"Untracked candidate files in src/ and tests/: {len(untracked_candidates)}")

all_files = sorted(list(set(git_diff_files + untracked_candidates)))
print(f"Total cumulative candidate files: {len(all_files)}")
for f in all_files[:20]:
    print("  ", f)
