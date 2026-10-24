# Lane C: Run 085 Evidence & Test Provenance Reconciliation
**Review Run:** `ER-RUN-085-ROOT-REVIEW-20261023-108` | **Lane:** Lane C (TRX, Build & Export Verification)
**Reviewed Target:** `governance/10-execution-runs/ER-RUN-POS-NON89-EXEC-20261023-085`

## 1. Raw Test Results Provenance & Bit-for-Bit Hash Verification
Three raw TRX files were deposited in `test-results/` and mirrored in `tests/`:

1. `phase11-unit-execution-085.trx`:
   - SHA-256: `8171ca6e4828f1e23394b87912906232ef946bfe82e3b7970af5d2db79fb5b7c`
   - Size: 2,102,483 bytes
   - Total Tests: 1,419 | Passed: 1,419 | Failed: 0 | Skipped: 0
   - Execution Window: 2026-10-23T14:42:39 to 2026-10-23T14:43:15 (+05:00)

2. `phase11-desktop-execution-085.trx`:
   - SHA-256: `eca4efd1c5bd094b0ca3a98f5d976f7a6dbbf131a29608ba317ded62d2fcdf12`
   - Size: 800,503 bytes
   - Total Tests: 483 | Passed: 483 | Failed: 0 | Skipped: 0
   - Execution Window: 2026-10-23T14:46:14 to 2026-10-23T14:46:57 (+05:00)

3. `phase11-pg18-execution-085.trx`:
   - SHA-256: `89c324f6c00ee1d9deb1e9a5855ab0272f474ead4e0e4e19791b13ff6ea47c8d`
   - Size: 316,288 bytes
   - Total Tests: 119 | Passed: 119 | Failed: 0 | Skipped: 0
   - Execution Window: 2026-10-23T15:07:55 to 2026-10-23T15:08:44 (+05:00)

**Combined Automated Test Results**: Exactly **2,021 / 2,021 PASSED (100% pass rate, 0 failed, 0 skipped)**.

## 2. Narrative Mismatch Reconciliation
- **Defect in Run 085 Submission**: `FINAL_REPORT.md` and `DELIVERY_INDEX.json` in `ER-RUN-POS-NON89-EXEC-20261023-085` copied narrative text from Run 084 referencing `run084_*.trx` and 1,820 tests (1,399 unit + 421 desktop).
- **Forensic Investigation**:
  1. The raw TRX files present in `ER-RUN-POS-NON89-EXEC-20261023-085/test-results/` are genuinely contemporary Run 085 executions (timestamps: Oct 23, 2026, 14:42 to 15:08).
  2. The actual executed tests total 2,021 (1,419 unit + 483 desktop + 119 PG18).
  3. The deposition generation script (`deposit_run085_governance.py`) inherited the old hardcoded report text from `deposit_run084_governance.py`.
- **Reviewer Resolution**: This is an administrative documentation discrepancy in the executor's summary report, not a test or code defect. The actual underlying tests are 100% contemporary and passing. We record this additive correction in `CHECKS.json` without modifying the executor's immutable submission.

## 3. Candidate Export Bundle & Secret Exclusion
- `candidate_tracked.patch`: SHA-256 `f36f7257caeede3d9d9d4dba651fd08c279c28287635bf86f819957d948342d4` (21,446,316 bytes).
- `candidate_untracked.zip`: SHA-256 `cdbdf107caac61e4437eac21b05326744916a1b1fc222865448085185d4c5d4e` (95,501 bytes, fixed timestamp 2026-10-23 12:00:00).
- `candidate_source_bundle.zip`: SHA-256 `74431cd76534f6c03f96302a583cb84133614bec65263a0e79d8faa53082cb09` (4,570,005 bytes).
- `CANDIDATE_FILE_INVENTORY.json`: 817 files total (799 tracked + 18 untracked).
- `PHASE12_EXCLUSION_MANIFEST.json`: 11 Phase 12 generator scripts formally excluded from candidate freeze.
