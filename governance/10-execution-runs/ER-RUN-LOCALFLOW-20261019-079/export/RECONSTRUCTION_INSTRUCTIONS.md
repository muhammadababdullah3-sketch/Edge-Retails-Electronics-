# Candidate Source Reconstruction Instructions for Run 079

- **Run ID**: `ER-RUN-LOCALFLOW-20261019-079`
- **Base Commit**: `3cd3b4b8f903adef6f6d7cf3c922c2ce184c2fc2`
- **Candidate Source Bundle SHA-256**: `6e667cb70e644db3a50202808799b95db3d4d71f7f3188023fbcd2d37babb251`
- **Candidate Tracked Patch SHA-256**: `6ceae4ab34c7e3d62ed141bceb7d6db4007f32c19e7d3cf1eb8586d643cd00a4`
- **Candidate Untracked Zip SHA-256**: `fd6fe3726a0ec8877541c94cb2c0af9215114ef1d663b79c3c6cc3719ee80c7d`

## Method A: Extract Source Bundle Directly (Recommended)
1. Verify bundle SHA-256: `6e667cb70e644db3a50202808799b95db3d4d71f7f3188023fbcd2d37babb251`.
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
