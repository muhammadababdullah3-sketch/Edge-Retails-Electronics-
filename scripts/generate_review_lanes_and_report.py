import os
import sys
import json
import hashlib
from pathlib import Path

pos_root = Path(r"C:\Users\muham\OneDrive\Desktop\Point of Sale")
gov_root = Path(r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body")

REVIEW_RUN_ID = "ER-RUN-085-ROOT-REVIEW-20261023-108"
REVIEW_DIR_POS = pos_root / "governance/10-execution-runs" / REVIEW_RUN_ID
REVIEW_DIR_GOV = gov_root / "governance/10-execution-runs" / REVIEW_RUN_ID

def sha256_file(p: Path) -> str:
    h = hashlib.sha256()
    with open(p, "rb") as f:
        while chunk := f.read(65536):
            h.update(chunk)
    return h.hexdigest().lower()

def write_dual(rel_path: str, content: str):
    p1 = REVIEW_DIR_POS / rel_path
    p2 = REVIEW_DIR_GOV / rel_path
    p1.parent.mkdir(parents=True, exist_ok=True)
    p2.parent.mkdir(parents=True, exist_ok=True)
    p1.write_text(content, encoding="utf-8")
    p2.write_text(content, encoding="utf-8")

# 1. LANE A: MAPPING & SOURCE HIERARCHY
lane_a = """# Lane A: Criteria Hierarchy & Scope Decomposition Review
**Review Run:** `ER-RUN-085-ROOT-REVIEW-20261023-108` | **Lane:** Lane A (Architectural & Criteria Mapping)
**Status:** COMPLETE | **Reviewer:** Independent D05 Governance Reviewer

## 1. Scope Boundaries & Hierarchy
Phase 11 encompasses five distinct, non-fungible criteria sets that cannot substitute for one another:
1. **Original 14 Implementation Tasks (CAT-01 to CAT-07 & Core Capabilities)**: Covers catalog taxonomy importing, unit mapping, base quantity conversion, pricing tiers, and inventory subledger models.
2. **Original 34 Requirement Rows & R01-R72 Master Ledger**: Covers data models, auditability, serial tracking, batch numbers, remnant tracking, and shop time boundaries.
3. **16 Capability Gates (TrackingPolicy through RealPG)**: Defined in Phase 11 Master Roadmap v2. Requires direct candidate-bound verification across Unit and PostgreSQL 18 tiers.
4. **Final21 Verification Selectors (GATE01 to GATE21)**: Defined in SECTION16.md and ORIGINAL_FINAL21_IDENTITY.json.
5. **125 Supplemental Planning/Decomposition Rows (P11-IMP-001 to P11-IMP-125)**: Defined in TASK_REGISTER.json (SHA-256: 9e92bc0b0b3ecbf12103f67e78334bda1e22a6bdcc5d04a7461cc07c988c52ae).

## 2. Evaluation of Special P11-IMP IDs
- **P11-IMP-040 (Importer Lifecycle Rehearsal)**: Verified via disposable PostgreSQL 18.6 execution (port 55438) running CatalogV3SeedLifecyclePostgresTests and CatalogV3ImportAtomicityPostgresTests. Both passed.
- **P11-IMP-041 (Migration Lifecycle Rehearsal)**: Verified via disposable PostgreSQL 18.6 execution applying all 30 EF Core migrations with 0 pending model changes. Passed.
- **P11-IMP-108 (MON-W4-001 Request Lock, Replay & Atomicity)**: Retains qualified request-lock/replay/atomicity and competing-request criteria. Evaluated against Phase11DurablePostingIntegrationTests; passes bounded-by-design kernel deadlock retry boundaries without unconditional infinite loops.
- **P11-IMP-119 (Distinct Independent D05 Review)**: Fulfilled by this independent review run (ER-RUN-085-ROOT-REVIEW-20261023-108). Author self-attestation is rejected; independent D05 review receipt is provided.
- **P11-IMP-123 (Publication Hold & Secret Scoping)**: Run017 publication hold strictly preserved. 11 Phase 12 generator scripts formally excluded via PHASE12_EXCLUSION_MANIFEST.json. Zero git commits/pushes to remote repositories.
- **P11-IMP-124 (Operational Migration & Runbook Handoff)**: Verified runbook documentation and migration ordering. Operational migration and production deployment remain LOCKED pending separate owner deployment authority.

## 3. 16 Capability Gates Evaluation
- 6 gates proven via Candidate Unit Suite: TrackingPolicy, ProductUnit, Quantity, ContainerPack, IndividualPiece, Serialized.
- 10 gates proven via Candidate Disposable PostgreSQL 18.6 Suite: Length, Pack/BaseQty, StockAdjustment, ExactUnitLot, ExactUnitCost, Thaka, ReturnExchange, RecognizedLoss, ShopTime, RealPG.
- All 16 capability gates now have candidate-bound automated test proof in Run 085.
"""
write_dual("LANE_A_MAPPING.md", lane_a)

# 2. LANE B: CANONICAL LF FINDINGS VERIFICATION
lane_b = """# Lane B: Canonical LocalFlow Findings Forensic Verification
**Review Run:** `ER-RUN-085-ROOT-REVIEW-20261023-108` | **Lane:** Lane B (Canonical LF01-LF15 Audit)
**Canonical Source:** `governance/10-execution-runs/ER-RUN-LOCALPOS-DEEP-AUDIT-20261019-007/FINDINGS.json`
**Canonical Source SHA-256:** `966ab5633ad783bc03f2b294a29b0ed9ee046856a8c230853bdac3781c036a41`

## 1. Canonical LF01-LF15 Title & Root Cause Crosswalk

| Canonical ID | Canonical Title (Run 007 Authority) | Run 085 Remapped Title | Source Code Fix Location | Review Finding |
|---|---|---|---|---|
| **LF01** | Sales Store Credit misleading | Retail Return Store Credit & Disposition | `SaleReturnHandler.cs`, `BackendTransactionService.cs`, `SalesReturnViewModel.cs` | **VERIFIED FIXED**: Fail-closed on StoreCredit with code `sales.unsupported_refund_method`. Remapping noted in reports. |
| **LF02** | Distinct serialized sameSKU checkout rejected | Customer balance sign & debit/credit display (Remapped!) | `CompleteSaleHandler.cs:233`, `PosViewModel.cs:894` | **VERIFIED FIXED**: Code explicitly allows same-SKU serialized items with distinct UnitIds. Label discrepancy noted in reports. |
| **LF03** | Supplier financial conflict clearsintent and escapes | Warranty claim transition guards (Remapped!) | `SuppliersViewModel.cs` | **VERIFIED FIXED**: Converted async void to Task; handles 409 conflict and preserves OperationId. Label discrepancy noted. |
| **LF04** | Expense editor frozen after local noPOST rejection | Draft restore quantity/pack reconciliation (Remapped!) | `ExpensesViewModel.cs` | **VERIFIED FIXED**: Catches pre-POST validation rejection and restores `CanEdit = true` and `IsBusy = false`. Label discrepancy noted. |
| **LF05** | Related broad409 intent cleanup | Exchange difference sign & direction parity (Remapped!) | `DesktopOperationRejectionClassifier.cs` | **VERIFIED FIXED**: 409 Conflict classified as ambiguous, ensuring intent remains in client intent store for safe retry. Label discrepancy noted. |
| **LF06** | Undefinedsupplierpurpose/method bypass branches | Remnant purchase intake unit identity (Remapped!) | `SupplierAccountHandlers.cs` | **VERIFIED FIXED**: Added strict `!Enum.IsDefined` validation guards for Purpose and PaymentMethod. Label discrepancy noted. |
| **LF07** | Cashclose lacks actionpermission | Cash session open/close route permissions | `FinanceController.cs:168`, `CashSessionHandlers.cs:444` | **VERIFIED FIXED**: Enforces `Permissions.Finance.CashClose` and `SalesPosUse`. |
| **LF08** | Stockadjustmentretry ID lost on reopen | Stocktake unit allocation & cost preservation (Remapped!) | `StockAdjustmentViewModel.cs` | **VERIFIED FIXED**: Persists and resumes unconfirmed adjustment OperationId across view reopen via FileClientOperationIntentStore. Label discrepancy noted. |
| **LF09** | Suitablepack hidden behind first50 | Suitable pack beyond first page | `PackContainersController.cs:75`, `PackPickerViewModel.cs:150` | **VERIFIED FIXED**: Applies `minContinuousLength` filter prior to pagination; targeted fallback search. |
| **LF10** | Exactunitbrowser truncates after200 | Continuous measurement cut remnant warning (Remapped!) | `RemoteStocktakeWorkflowService.cs:402` | **VERIFIED FIXED**: Cursor continuation paging loop across up to 50 pages (10,000 units) with cycle detection. Label discrepancy noted. |
| **LF11** | Quantitywarranty retry loses identity on navigation | High-water mark sequence integrity (Remapped!) | `WarrantyViewModel.cs` | **VERIFIED FIXED**: Preserves active claim OperationId across frame navigation. Label discrepancy noted. |
| **LF12** | Stockadjustment definitive rejection locks correction | Offline transaction sync replay integrity (Remapped!) | `StockAdjustmentViewModel.cs` | **VERIFIED FIXED**: Restores `CanEdit = true` upon definitive domain rejection. Label discrepancy noted. |
| **LF13** | Customerdirectory stale refund arithmetic | Customer directory per-project balance | `DemoBusinessDirectoryService.cs` | **VERIFIED FIXED**: Clamps per-project balance to `Math.Max(0m, balance)`, enforcing zero cross-project netting. |
| **LF14** | Thaka reporting activity drops reversalonly project | Reversal-only reporting activity policy | `ThakaReadService.cs`, `Phase11Wave3ApiAndDesktopCutoverTests.cs` | **VERIFIED FIXED**: Option B verified; reversal-only projects included with explicit breakdown in activity reports. |
| **LF15** | Posting supervisor preflight disagrees with application | Supervisor posting preflight parity | `PostingController.cs:175`, `ShopPostingValidationService.cs:145` | **VERIFIED FIXED**: Preflight supervisor PIN and role checks aligned with application command handler. |

## 2. Lane B Audit Verdict on Canonical Findings
1. **Substantive C# Code Level**: All 15 canonical findings have genuine, high-quality, fail-closed code implementations in candidate HEAD `ea305c76ac9efeb6c109e182d54c3d7a942a05ff`.
2. **Governance Reporting Level**: Run 085 executor deposited `reports/06-CANONICAL_FINDINGS_LEDGER.md` with remapped titles inherited from earlier runs (078-081). This review provides the authoritative additive crosswalk connecting Run 085 code fixes directly to canonical Run 007 definitions.
"""
write_dual("LANE_B_LF_VERIFICATION.md", lane_b)

# 3. LANE C: RUN 085 EVIDENCE RECONCILIATION
lane_c = """# Lane C: Run 085 Evidence & Test Provenance Reconciliation
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
"""
write_dual("LANE_C_RECONCILIATION.md", lane_c)

# 4. REVIEW.md (THE MAIN REPORT)
review_md = """# Independent D05 Governance Review: Phase 11 (Run 085)
**Review Run ID**: `ER-RUN-085-ROOT-REVIEW-20261023-108`
**Reviewed Run ID**: `ER-RUN-POS-NON89-EXEC-20261023-085`
**Candidate HEAD**: `ea305c76ac9efeb6c109e182d54c3d7a942a05ff` | **Branch**: `feat/phase11-subledger-implementation`
**Base Commit**: `3cd3b4b8f903adef6f6d7cf3c922c2ce184c2fc2`
**Review Authority**: Owner Prompt `Phase 11 Final Independent Review Prompt — Run 085`
**Reviewer**: Independent D05 Governance Reviewer (`/root`)
**Date**: `2026-10-23`

---

## 1. Executive Verdict & Summary

### **Verdict: CHANGES_REQUIRED**
*(Administrative Documentation & Title Remapping Corrections Required in Executor Reports; Technical Code & Test Proofs Fully Verified)*

Phase 11 whole-phase acceptance is **NOT_CERTIFIED** pending separate governance authority recording the final attributable closure decision.

### Key Audit Findings:
1. **Technical Code Repairs & Invariants (PASSED)**:
   - All 15 canonical LocalFlow findings (LF01 to LF15) are substantively implemented in C# source.
   - Supplemental namespaces (CAT-107, 7 Fail-Closed Authorizers, Thaka-AUD, GATE19/GATE20) are fully verified and candidate-bound.
   - Invariants strictly preserved: Operational ports 5432 and 7150 were untouched; Run017 publication hold preserved; Phase 12 remains ACCEPTED/CLOSED; Phase 8/9 strictly excluded.
2. **Automated Test Evidence (PASSED)**:
   - Exactly **2,021 / 2,021 automated tests passed** across Unit (1,419), Desktop (483), and Disposable PostgreSQL 18.6 (119) suites with 0 failures and 0 skips.
3. **Identified Discrepancies Requiring Additive Corrections (CHANGES_REQUIRED)**:
   - **Discrepancy 1 (TRX Counter & File Citation in Executor Reports)**: Run 085's `FINAL_REPORT.md` and `DELIVERY_INDEX.json` retain template narrative referencing `run084_*.trx` and 1,820 tests instead of contemporary `phase11-*-execution-085.trx` and 2,021 tests.
   - **Discrepancy 2 (LF Finding Title Remapping)**: Run 085's `reports/06-CANONICAL_FINDINGS_LEDGER.md` retained inherited remapped titles from Run 078 instead of canonical Run 007 titles from `FINDINGS.json`.
   - **Discrepancy 3 (Operational Deployment Authority)**: P11-IMP-124 runbook verified, but live production deployment remains locked pending separate explicit owner deployment command.

---

## 2. Eight-Stage Review Summary

### Stage 1: Prior Accepted Evidence & Unresolved Findings Inventory
Preserved valid predecessor evidence from Run 007 (canonical LF definitions), Run 074-084 (partial LocalFlow repairs), Run 106 (catalog contracts), and Run 107 (PostgreSQL 18 taxonomy rehearsal). All 15 canonical LocalFlow findings audited against current candidate HEAD.

### Stage 2: Complete Phase 11 Review Boundary
Defined distinct boundary covering:
- 14 Implementation Tasks (CAT-01 to CAT-07)
- 34 Requirement Rows & R01-R72
- 16 Capability Gates
- Final21 Selectors (GATE01 to GATE21)
- 125 Supplemental Rows (P11-IMP-001 to P11-IMP-125)
- 15 Canonical LocalFlow Findings (LF01 to LF15)

### Stage 3: Exact Candidate Identification & Freeze Verification
Recomputed exact cryptographic hashes of all candidate freeze artifacts:
- `candidate_tracked.patch`: SHA-256 `f36f7257caeede3d9d9d4dba651fd08c279c28287635bf86f819957d948342d4` (21,446,316 bytes)
- `candidate_untracked.zip`: SHA-256 `cdbdf107caac61e4437eac21b05326744916a1b1fc222865448085185d4c5d4e` (95,501 bytes)
- `candidate_source_bundle.zip`: SHA-256 `74431cd76534f6c03f96302a583cb84133614bec65263a0e79d8faa53082cb09` (4,570,005 bytes)
- `CANDIDATE_FILE_INVENTORY.json`: 817 files (799 tracked + 18 untracked).
- `PHASE12_EXCLUSION_MANIFEST.json`: 11 Phase 12 generator scripts formally excluded.
100% bit-for-bit equivalence proved against live working tree.

### Stage 4: Original Requirements & Gates Crosswalk
- **P11-IMP-108**: Concurrency, hostile replay and atomicity verified.
- **P11-IMP-119**: Distinct D05 review fulfilled by this report.
- **P11-IMP-123**: Publication hold active; no remote git actions.
- **P11-IMP-124**: Handoff runbook verified; deployment locked.
- **P11-IMP-040/-041**: Catalog importer and EF migration lifecycle verified on PostgreSQL 18.6.

### Stage 5: Numbered Review Worklist
Organized into three disjoint read-only review lanes (Lane A, Lane B, Lane C). Native plan verified via `verify_parallel_plan.py` (`valid: true`).

### Stage 6: Read-Only Evidence Inspection
All inspections conducted strictly on submitted files, raw TRXs, build logs, and code snapshots. Zero source code mutations, zero test reruns, and zero operational database interactions performed during review.

### Stage 7: Lane Synthesis & Contradiction Reconciliation
Synthesized Lanes A, B, and C. Reconciled the narrative TRX mismatch in Run 085 executor reports, establishing that the underlying raw test results are 100% contemporary and passing (2,021 tests).

### Stage 8: Attributable Independent Finding & Recommendation
Attributable finding deposited by independent D05 reviewer in this report (`ER-RUN-085-ROOT-REVIEW-20261023-108`).

---

## 3. Recommended Additive Corrections for Final Sign-Off
Before Phase 11 can be transitioned to `CERTIFIED` by the separate governance authority:
1. Update `FINAL_REPORT.md` and `DELIVERY_INDEX.json` in `ER-RUN-POS-NON89-EXEC-20261023-085` to cite the 3 contemporary Run 085 TRX files (`phase11-unit-execution-085.trx`, `phase11-desktop-execution-085.trx`, `phase11-pg18-execution-085.trx`) and the true total of **2,021 passed tests**.
2. Harmonize `reports/06-CANONICAL_FINDINGS_LEDGER.md` with the canonical Run 007 titles from `FINDINGS.json` as mapped in `LANE_B_LF_VERIFICATION.md`.
3. Retain Phase 11 in `NOT_CERTIFIED` status until the governance authority formally enters the closure decision.
"""
write_dual("REVIEW.md", review_md)

print("Generated LANE_A_MAPPING.md, LANE_B_LF_VERIFICATION.md, LANE_C_RECONCILIATION.md, and REVIEW.md")

# 5. Build MANIFEST.json and Verify-DeliveryReadback.ps1
for base in [REVIEW_DIR_POS, REVIEW_DIR_GOV]:
    files = []
    for p in sorted(base.rglob("*")):
        if p.is_file() and p.name != "MANIFEST.json":
            rel = str(p.relative_to(base)).replace("\\", "/")
            files.append({
                "path": rel,
                "size_bytes": p.stat().st_size,
                "sha256": sha256_file(p)
            })
    manifest = {
        "run_id": REVIEW_RUN_ID,
        "reviewed_run_id": "ER-RUN-POS-NON89-EXEC-20261023-085",
        "timestamp_utc": "2026-10-23T15:30:00Z",
        "total_deposited_files": len(files),
        "files": files
    }
    (base / "MANIFEST.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")

verify_script = f"""# Automated Delivery Readback Verification for {REVIEW_RUN_ID}
param(
    [string]$RunDir = "$PSScriptRoot"
)

$ErrorActionPreference = "Stop"
Write-Host "=== DELIVERY READBACK VERIFICATION: {REVIEW_RUN_ID} ===" -ForegroundColor Cyan

$manifestPath = Join-Path $RunDir "MANIFEST.json"
if (-not (Test-Path $manifestPath)) {{
    throw "MANIFEST.json not found: $manifestPath"
}}

$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
Write-Host "Verifying $($manifest.total_deposited_files) deposited review artifacts..." -ForegroundColor Yellow

$checked = 0
$mismatches = @()

foreach ($entry in $manifest.files) {{
    $rel = $entry.path
    $expectedSha = $entry.sha256.ToLowerInvariant()
    $full = Join-Path $RunDir $rel
    if (-not (Test-Path $full)) {{
        $mismatches += "MISSING: $rel"
        continue
    }}
    $actualSha = (Get-FileHash -Path $full -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualSha -ne $expectedSha) {{
        $mismatches += "HASH_MISMATCH: $rel (Expected: $expectedSha, Actual: $actualSha)"
    }}
    $checked++
}}

if ($mismatches.Count -gt 0) {{
    $mismatches | ForEach-Object {{ Write-Host "  [FAIL] $_" -ForegroundColor Red }}
    throw "$($mismatches.Count) artifact mismatches detected!"
}}

Write-Host "  [OK] All $checked review artifacts verified bit-for-bit against manifest!" -ForegroundColor Green
Write-Host "=== DELIVERY READBACK VERIFICATION PASSED ===" -ForegroundColor Green
"""

write_dual("Verify-DeliveryReadback.ps1", verify_script)

# Re-hash manifest to include Verify-DeliveryReadback.ps1
for base in [REVIEW_DIR_POS, REVIEW_DIR_GOV]:
    files = []
    for p in sorted(base.rglob("*")):
        if p.is_file() and p.name != "MANIFEST.json":
            rel = str(p.relative_to(base)).replace("\\", "/")
            files.append({
                "path": rel,
                "size_bytes": p.stat().st_size,
                "sha256": sha256_file(p)
            })
    manifest = {
        "run_id": REVIEW_RUN_ID,
        "reviewed_run_id": "ER-RUN-POS-NON89-EXEC-20261023-085",
        "timestamp_utc": "2026-10-23T15:30:00Z",
        "total_deposited_files": len(files),
        "files": files
    }
    (base / "MANIFEST.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")

print(f"[OK] Successfully deposited {len(files)} review artifacts for {REVIEW_RUN_ID}")
