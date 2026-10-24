# Candidate Source Reconstruction Instructions for Run 106

- **Run ID**: `ER-RUN-CATALOG-EXEC-20261021-106`
- **Base Commit**: `3cd3b4b8f903adef6f6d7cf3c922c2ce184c2fc2`
- **Candidate Source Bundle SHA-256**: `f9f39fd9fa8afbb7c70cc85d3e72d60fad16c9d7152b5ca7e9f6b9d5538c1a79`
- **Candidate Tracked Patch SHA-256**: `ccf51c2479954da7aaf9c5a6999dfbde2180082d0571ccfef2b8659daed56712`
- **Candidate Untracked Zip SHA-256**: `dda1ada90cfd30f69b8f697b3442a321670ba21a1476d9cd6bae164de5e18fb7`

## Method A: Extract Source Bundle Directly (Recommended)
1. Verify bundle SHA-256: `f9f39fd9fa8afbb7c70cc85d3e72d60fad16c9d7152b5ca7e9f6b9d5538c1a79`.
2. Extract `candidate_source_bundle.zip` into repository root.
3. Build and test: `dotnet build`, `dotnet test`.

## Method B: Apply Tracked Patch + Extract Untracked Zip
1. Reset working tree to base commit `3cd3b4b8f903adef6f6d7cf3c922c2ce184c2fc2`.
2. Apply tracked patch: `git apply --binary candidate_tracked.patch`.
3. Extract untracked files from `candidate_untracked.zip`.
