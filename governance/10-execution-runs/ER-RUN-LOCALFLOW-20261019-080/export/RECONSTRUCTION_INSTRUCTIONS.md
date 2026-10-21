# Candidate Source Reconstruction Instructions for Run 080

- **Run ID**: `ER-RUN-LOCALFLOW-20261019-080`
- **Base Commit**: `3cd3b4b8f903adef6f6d7cf3c922c2ce184c2fc2`
- **Candidate Source Bundle SHA-256**: `52412da410104e56e700a0076207bbad0428a9fa4b253b214fbd5f18ef7c921b`
- **Candidate Tracked Patch SHA-256**: `e89b20722b763eac4caf1e53e8e5765c27b6bb84d9684b062def2264782ba23a`
- **Candidate Untracked Zip SHA-256**: `1787ede2829f4e3fa1d07324d1ee3d0f0c8417e5ebc0939d274ea111e9d9953f`

## Method A: Extract Source Bundle Directly (Recommended)
1. Verify bundle SHA-256: `52412da410104e56e700a0076207bbad0428a9fa4b253b214fbd5f18ef7c921b`.
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
