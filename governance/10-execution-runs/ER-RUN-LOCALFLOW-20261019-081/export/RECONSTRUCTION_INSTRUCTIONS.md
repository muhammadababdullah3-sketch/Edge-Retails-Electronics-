# Candidate Source Reconstruction Instructions for Run 081

- **Run ID**: `ER-RUN-LOCALFLOW-20261019-081`
- **Base Commit**: `3cd3b4b8f903adef6f6d7cf3c922c2ce184c2fc2`
- **Candidate Source Bundle SHA-256**: `8e03832459a86fdeea780d34047c5835a41b54bf0eef55fa1387b0624a671c71`
- **Candidate Tracked Patch SHA-256**: `4551d1ba3db06bd77d4ff8e9d86762f60f72decd5aa086a312c8878a34e412cb`
- **Candidate Untracked Zip SHA-256**: `c99cb97d0d106d07c45d2f458119e76b3e3fa0bf72c5d36b0ca8d8adb584f688`

## Method A: Extract Source Bundle Directly (Recommended)
1. Verify bundle SHA-256: `8e03832459a86fdeea780d34047c5835a41b54bf0eef55fa1387b0624a671c71`.
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
