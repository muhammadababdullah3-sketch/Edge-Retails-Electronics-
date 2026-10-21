# Candidate Source Reconstruction Instructions

- **Run ID**: `ER-RUN-LOCALFLOW-20261019-074`
- **Base Commit**: `3cd3b4b8f903adef6f6d7cf3c922c2ce184c2fc2`
- **Candidate Source Bundle SHA-256**: `51baba8db3ef51de88054f74742dc73b844618aa63bf7b9994a4a89851320a67`

## Method A: Extract Source Bundle Directly (Recommended)
1. Verify bundle SHA-256: `51baba8db3ef51de88054f74742dc73b844618aa63bf7b9994a4a89851320a67`.
2. Extract `candidate_source_bundle.zip` into repository root:
   ```bash
   unzip -o candidate_source_bundle.zip -d /path/to/Point-of-Sale
   ```
3. Build and test:
   ```bash
   dotnet build
   dotnet test
   ```

## Method B: Apply Tracked Patch + Extract Untracked Zip
1. Reset working tree to base commit `3cd3b4b8f903adef6f6d7cf3c922c2ce184c2fc2`:
   ```bash
   git checkout 3cd3b4b8f903adef6f6d7cf3c922c2ce184c2fc2
   git clean -fd
   ```
2. Apply tracked patch:
   ```bash
   git apply --binary candidate_tracked.patch
   ```
3. Extract untracked files:
   ```bash
   unzip -o candidate_untracked.zip -d /path/to/Point-of-Sale
   ```
