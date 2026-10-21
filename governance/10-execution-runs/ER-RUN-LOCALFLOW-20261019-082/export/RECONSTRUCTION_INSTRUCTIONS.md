# Candidate Source Reconstruction Instructions for Run 082

- **Run ID**: `ER-RUN-LOCALFLOW-20261019-082`
- **Base Commit**: `3cd3b4b8f903adef6f6d7cf3c922c2ce184c2fc2`
- **Candidate Source Bundle SHA-256**: `eb9f12e05b9d0920dd27829d0a91fc914296e6f7ee6d159efde6161c1054faf5`
- **Candidate Tracked Patch SHA-256**: `de74fadde4164cba95e804ecfc53e0534bd3e3916b755e01dd612e94dc5aefdb`
- **Candidate Untracked Zip SHA-256**: `74d6119320995d66248e3fe35e0a5567bbbc23bd866f58a5bc2d7fc1bd2df6a2`

## Method A: Extract Source Bundle Directly (Recommended)
1. Verify bundle SHA-256: `eb9f12e05b9d0920dd27829d0a91fc914296e6f7ee6d159efde6161c1054faf5`.
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
