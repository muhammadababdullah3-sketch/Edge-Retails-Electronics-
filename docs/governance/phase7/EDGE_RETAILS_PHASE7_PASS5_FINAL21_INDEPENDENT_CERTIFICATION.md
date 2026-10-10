# EDGE RETAILS — PHASE 7 / PASS 5 FINAL21
# INDEPENDENT EXTERNAL CERTIFICATION AUDIT REPORT

**Authority:** Independent External Certification Lead  
**Operating Mode:** ONE INDEPENDENT LEAD · ZERO SUBAGENTS · CERTIFICATION ONLY · READ-ONLY SOURCE  
**Workspace:** `C:\Users\muham\OneDrive\Desktop\Point of Sale`  
**Date:** October 9, 2026  
**Canonical Architecture Baseline:** `EdgeRetails-Backend-V1-2026-09-22-InternationalAuditRemediated`  
**Canonical Anchor Report:** `docs/Edge_Retails_Final_Architecture_Report_v1.md` (SHA256: `059DDB086F822A255BDA7CE4A7E49CC36E3F82F3BC8D6E6F1B7AF0FC40B8BAD6`)  
**Frozen Candidate Path:** `artifacts/phase7-pass5/remediation-r1/candidate-r1/`  
**Candidate Input Policy:** `R1-H01-1`  
**Database Authority:** Owned Disposable PostgreSQL 18.6 / Npgsql (Port 55000–65535, 127.0.0.1)  
**Final Certification Verdict:** **`PHASE7_PASS5_INDEPENDENT_CERTIFIED_CLOSED_LOCKED`**  

---

## 1. Reviewer Independence Declaration & Scope Boundary

This audit was conducted strictly by an independent certification role without builder self-approval, without subagent delegation, and without modifying any production source files, database schemas, or operational environments.

1. **Read-Only Source Enforcement:** Zero production business source files (`src/*`), historical migration files (`src/EdgeRetails.Infrastructure/Migrations/*`), or test fixtures were modified during this certification.
2. **PostgreSQL 18 Isolation:** All database verification executed against strictly owned, disposable, local PostgreSQL 18 instances running on dynamic high-range ports (55000–65535). No connection was established to `edge_retails_prod`, and no installed shop data was touched or mutated.
3. **No Premature Lifecycle Authority:** This certification explicitly restricts its scope to Backend Phase 7 / Pass 5 closure and prerequisites. It does **not** authorize Phase 12 (process restart recovery), Phase 13, or production deployment. Installed production services remain uncertified until physical release cutover.

---

## 2. Frozen Candidate Identity & Manifest Integrity

The candidate under certification is `candidate-r1`, produced under policy `R1-H01-1` to permanently supersede historical failed candidate `E19958C8AFED255F360F41035FC06723462CBFD5967A643606FA6AF49966B996` (from SOL 6.1).

### 2.1. Manifest Inventory Verification

| Artifact | Location | Inventory Count | Digest / Validation | Status |
| :--- | :--- | :---: | :--- | :---: |
| **Policy Definition** | `candidate-r1/policy.json` | 1 | Version `R1-H01-1`, complete input trees (`src`, `docs`, `scripts`, `build`, `database`, `installer`, `.config`, `tests`) | **VERIFIED** |
| **Source Inputs JSON** | `candidate-r1/source-inputs.json` | 801 | 801 discrete source files indexed with scope and reason | **VERIFIED** |
| **Source Hash Manifest** | `candidate-r1/source-manifest.sha256` | 801 | 801 SHA256 file hashes; byte-for-byte matches live tree | **VERIFIED** |
| **Harness Inputs JSON** | `candidate-r1/harness-inputs.json` | 265 | 265 test and harness files indexed | **VERIFIED** |
| **Harness Hash Manifest** | `candidate-r1/harness-manifest.sha256` | 265 | 265 SHA256 file hashes; byte-for-byte matches live tree | **VERIFIED** |
| **Total Frozen Candidate Files** | Combined Source + Harness | **1,066** | Complete repository tree coverage without extension filtering | **VERIFIED** |

### 2.2. 104-Reference Coverage Closure Verification

In accordance with gate H01, all file references declared in solution files (`.sln`), project files (`.csproj`, `.wixproj`), MSBuild targets (`.props`, `.targets`), application manifests, and PowerShell modules (`.ps1`, `.psm1`, `.psd1`) were evaluated for closure in `candidate-r1/reference-coverage.json`:

- **Total Reference Declarations Inspected:** **104**
- **File References Covered by Manifest:** **69**
- **Non-File / External / Dynamic References:** **35** (NuGet packages, Visual Studio solution folders)
- **Missing References:** **0**
- **Uncovered References:** **0**
- **H01 Reference Closure Verdict:** **PASS**

### 2.3. Key Canonical File Digests

- `docs/Edge_Retails_Final_Architecture_Report_v1.md`: `059DDB086F822A255BDA7CE4A7E49CC36E3F82F3BC8D6E6F1B7AF0FC40B8BAD6`
- `docs/Architecture_Authority_Manifest.json`: `C67A694813EBC4F103788FE8627A4A4A59F425CB7C187BE5236C3012F477C1B9`
- `src/EdgeRetails.Domain/Warranty/WarrantyClaim.cs`: `8DB3B56149BE7F11C84A9F2F76442657A49FF5BC03E48C847B0907106C59E3F1`
- `tests/EdgeRetails.IntegrationTests/Phase7Pass5GoldenScenariosPostgresTests.cs`: `C781C03EF16CB7197EBE704AB2623D8DD803F0CA977D742C1D78794983114D0B`
- `tests/EdgeRetails.IntegrationTests/Phase7Pass5OwnerReconciliationPostgresTests.cs`: `3E93D1A3E15AADFFC610C4A52C4D2B3115DE534CB1AF4150D5B7EEB3A54409AA`

---

## 3. Canonical Final21 Mandatory Gate Matrix

All 21 mandatory certification gates defined by canonical Phase 7 / Pass 5 authority were independently audited against actual test executions, TRX logs, source code, and database evidence.

| # | Gate Name | Target Suite / Evidence Artifact | Executed | Passed | Failed | Skipped | Status |
| :-: | :--- | :--- | :-: | :-: | :-: | :-: | :---: |
| **01** | Pass 5 Focused Unit | `EdgeRetails.UnitTests` (`Phase7Pass5*`) | 76 | 76 | 0 | 0 | **PASS** |
| **02** | Pass 5 Business Handlers | Catalog, Commercial, Finance, Recovery, Return Handlers | 76 | 76 | 0 | 0 | **PASS** |
| **03** | Pass 5 Core PostgreSQL | PostgreSQL 18 Integration Suite (`Phase7Pass5*`) | 176 | 176 | 0 | 0 | **PASS** |
| **04** | Golden Scenarios A–D | `Phase7Pass5GoldenScenariosPostgresTests.cs` | 8 | 8 | 0 | 0 | **PASS** |
| **05** | Owner Reconciliation | `Phase7Pass5OwnerReconciliationPostgresTests.cs` | 6 | 6 | 0 | 0 | **PASS** |
| **06** | Pass 1 Protected Invariants | Unit (22) + PG18 (23) Purchasing, Return, Cash Precision | 45 | 45 | 0 | 0 | **PASS** |
| **07** | Pass 2 Protected Concurrency | Unit (16) + PG18 (71) Concurrency, Locks, Thaka reads | 87 | 87 | 0 | 0 | **PASS** |
| **08** | Pass 3 Protected Replay | Unit (19) + PG18 (6) VoidPurchase, Exchange, Draft Replay | 25 | 25 | 0 | 0 | **PASS** |
| **09** | Pass 4 Protected Aggregates | Unit (95) + Desktop (14) + PG18 (54) Aggregates, UOM | 163 | 163 | 0 | 0 | **PASS** |
| **10** | Pass 4 H1/H2 Provenance | Container Sold Cost (H1) + Bulk Received (H2) Scrap/Restock | 11 | 11 | 0 | 0 | **PASS** |
| **11** | Tracking Identity Authority | Unit (158) + PG18 (85) Tracking, IMEI1/2, Serial Life-cycle | 243 | 243 | 0 | 0 | **PASS** |
| **12** | Sequence / Custody Locks | High-Water Sequences (56) + Master Sequences PG18 (13) | 69 | 69 | 0 | 0 | **PASS** |
| **13** | Exact Warranty Multi-Source | Matrix rows across 5 outcomes (Serialized + Quantity) | 157 | 157 | 0 | 0 | **PASS** |
| **14** | Supplier Khata / Cash Safety | Hostile Replay B01–B06 (16) + Supplier Ledger PG18 (43) | 59 | 59 | 0 | 0 | **PASS** |
| **15** | Full Unit Regression Suite | `EdgeRetails.UnitTests` (All Features) | 1,133 | 1,133 | 0 | 0 | **PASS** |
| **16** | IntegrationTests Build | `dotnet build tests/EdgeRetails.IntegrationTests/ -c Release` | 1 | 1 | 0 | 0 | **PASS** |
| **17** | Solution Debug Build | `dotnet build EdgeRetails.sln -c Debug` | 1 | 1 | 0 | 0 | **PASS** |
| **18** | Solution Release Build | `dotnet build EdgeRetails.sln -c Release` | 1 | 1 | 0 | 0 | **PASS** |
| **19** | EF Model Alignment | `dotnet ef migrations has-pending-model-changes` (Exit 0) | 1 | 1 | 0 | 0 | **PASS** |
| **20** | Migration Chain Hashes | 40 Historical Migrations Immutable + 2 Forward Pass 5 | 42 | 42 | 0 | 0 | **PASS** |
| **21** | Candidate Scope Rehash | 801 Source + 265 Harness + 104 References Checked | 1,066 | 1,066 | 0 | 0 | **PASS** |

---

## 4. Deep-Dive Investigation of Historical Suite Count Differences

The independent audit investigated the three historical count variances identified in prior certification logs:

### 4.1. Financial PostgreSQL Tests: 16 vs. 12
- **Investigation:** In `tests/EdgeRetails.IntegrationTests/Phase7BackendFinancialSafetyPostgresTests.cs`, there are exactly **16 test methods** decorated with `[Fact]`:
  1. `B01_SupplierPayment_ExactReplaySucceeds_AndMutatedFieldsFailWithPayloadMismatch`
  2. `B02_B03_PostExpense_ExactReplaySucceeds_MutatedFieldsFail_AndCanonicalOutcomeRecorded`
  3. `B04_SupplierRefund_ExactReplaySucceeds_MutatedFieldsFail_AndCanonicalOutcomeRecorded`
  4. `B05_ReverseSupplierPayment_ReplaySucceeds_CompetingOperationFails_AndOutcomeRecorded`
  5. `B05_ReverseSupplierRefund_ReplaySucceeds_CompetingOperationFails_AndOutcomeRecorded`
  6. `B06_AuthenticatedOperationStatus_EnforcesActorScope_AndPreventsCrossActorLeakage`
  7. `LegacyRowWithoutCanonicalOutcome_CannotBypassSemanticIdentityChecks_AndBackfillsOutcome`
  8. `SameTransaction_AtomicOutcomePersistence_RollsBackOutcomeOnFailure`
  9. `T04_CrossTerminal_AndCrossActor_OperationStatusQuery_RejectsMismatchedContext`
  10. `T05_ConcurrentSameId_SupplierPayment_ExecutesExactlyOnce`
  11. `T08_Commit_LostResponse_StatusLookup_ExactReplay_SingleCommittedEffect`
  12. `T09_ServerRestart_FreshDiContainer_SupportedOutcomeRecovered`
  13. `T10_Expense_PostAndVoid_SingleCompensation_ExactRestoredCashBalance`
  14. `T11_SupplierLedgerCashAuditOutcome_ReconciliationZeroVariance`
  15. `T12_CrossType_ClientOperationId_CollisionRejected`
  16. `T14_RetentionSafety_ReplayableOperationsRetainedWithinHorizon`
- **Root Cause of "12":** Prior runs used category filters focusing strictly on the primary finding proofs (B01–B06 and T04–T11), counting 12 tests. The complete file contains 16 tests.
- **Audit Conclusion:** **ZERO REGRESSION**. All 16 hostile financial safety tests pass against PostgreSQL 18.

### 4.2. Full Unit Tests: 1,163 vs. 1,133
- **Investigation:** 
  - `tests/EdgeRetails.UnitTests/EdgeRetails.UnitTests.csproj` contains **1,133 unit tests** (verified in `full-unit.trx` and `full-unit.json`: Total: 1133, Executed: 1133, Passed: 1133, Failed: 0, Skipped: 0).
  - The figure **1,163** represents the 1,133 unit tests plus 30 additional unit/performance tests distributed in companion test projects (such as `EdgeRetails.Desktop.PerformanceTests` and mock doubles).
- **Audit Conclusion:** **ZERO REGRESSION**. The core unit test project contains 1,133 tests; all 1,133 pass cleanly.

### 4.3. Affected PostgreSQL Tests: 15 vs. 367
- **Investigation:**
  - The count **15** refers specifically to the narrow suite in `Phase7Pass5InventoryRecoveryPostgresTests.cs`.
  - The count **367** is the broad affected regression filter executed during H02 verification:
    ```bash
    dotnet test tests\EdgeRetails.IntegrationTests\EdgeRetails.IntegrationTests.csproj -c Release \
      --filter "FullyQualifiedName~Tracking|FullyQualifiedName~Sequence|FullyQualifiedName~Custody|FullyQualifiedName~Warranty|FullyQualifiedName~Phase7Pass5InventoryRecovery"
    ```
    Verified in `h02-affected-postgres-regression-attempt03/terminal-result.json`: Total 367, Passed 367, Failed 0, Skipped 0.
- **Audit Conclusion:** **ZERO REGRESSION**. 15 is a sub-suite; 367 is the comprehensive affected regression suite.

---

## 5. PostgreSQL Safety & Isolation Verification

Verification of database safety mechanisms confirmed strict isolation from production assets:

1. **Safety Harness Enforcement (`Phase2PostgresTestHarness.cs`):**
   - Hard-coded assertion `AttestOwnedPostgresRoot` validates that connection host is `127.0.0.1`, port is between 55000 and 65535, database name starts with `edge_retails_`, and the run root is inside the temporary runner directory (`TempPath\EdgeRetailsMasterPg_*`).
   - Any attempt to target `edge_retails_prod` or port 5432 causes immediate `InvalidOperationException` termination.
2. **Canonical Migration Rehearsal:**
   - Full migration execution from empty database to head:
     `dotnet ef database update --project src/EdgeRetails.Infrastructure/EdgeRetails.Infrastructure.csproj`
   - Verified that all tables, foreign keys, and indexes are generated via EF migrations, not `EnsureCreated()`.
3. **Database Lifecycle Cleanup:**
   - Isolated clusters initialized (`initdb`), started (`pg_ctl start`), tested, shut down with fast timeout (`pg_ctl -w -m fast stop`), and verified terminated (`ExitCode: 3` on status check).

---

## 6. Golden Scenario & Owner Reconciliation Verification

### 6.1. Golden Scenarios A–D (Gate 04: 8/8 PASS)
Executed in [Phase7Pass5GoldenScenariosPostgresTests.cs](tests/EdgeRetails.IntegrationTests/Phase7Pass5GoldenScenariosPostgresTests.cs) as single continuous chains verifying `AssertGoldenEndpointAsync`:
- **Golden Scenario A:** Verified serialized intake of 3 units, sale of unit 1, defective return, commercial exchange of unit 2 for unit 3, condition transfer of unit 2 to Damaged, shop warranty dispatch, repaired resolution, and final terminal sale.
- **Golden Scenario B:** Verified purchase, supplier payment, cash expense, expense void, purchase return, and supplier refund reconciling to an exact **$0.00 net change**.
- **Golden Scenario C:** Verified customer tracked sale, warranty claim, replacement with newly generated identity, and customer handover preserving original tracking code linkage.
- **Golden Scenario D (Serialized & Quantity):** Verified multi-lot intake at differing costs ($50 and $60), custody send, and terminal disposition derecognizing at the store-wide MWA ($55 carrying value) under Governance Resolution 05.
- **Supporting Scenarios (3):** Verified length-cut Thaka conversion, mixed basket discount/exchange, and missing refusal recovery.

### 6.2. Independent Primitive Fact Owner Reconciliation (Gate 05: 6/6 PASS)
Executed in [Phase7Pass5OwnerReconciliationPostgresTests.cs](tests/EdgeRetails.IntegrationTests/Phase7Pass5OwnerReconciliationPostgresTests.cs):
- Raw tables (`InventoryMovements`, `InventoryLotConsumptions`, `PurchaseItems`, `SaleItems`, `SaleReturns`, `Expenses`, `SupplierAccountEntries`, `CashMovements`, `ThakaProjects`) queried directly.
- Mathematical equality proved against reporting models:
  $$\text{NetProfit} - \text{Baseline.NetProfit} - \text{PrimitiveProfit} = 0.00$$
- Unexplained variance: **$0.00 across Daily, Monthly, and Yearly periods**.
- Operating recovery gains independently proven:
  - `InventoryLossRecoveryGain`: **$100.00**
  - `WarrantyRecoveryGain`: **$40.00**

---

## 7. Bounded Contract Checks

Source code inspections across Server and Desktop confirmed alignment with canonical contracts:

| Verification Target | Component & File | Inspected Contract & Behavior | Audit Finding |
| :--- | :--- | :--- | :--- |
| **Expense Void Actual Route** | Server: `ExpensesController.cs` (L110)<br>Desktop: `RemoteBackendBusinessOperationsService.cs` (L57) | Route: `POST /api/expenses/{id:guid}/void`<br>Desktop dispatches exact matching route: `/api/expenses/{expenseId:D}/void` | **MATCH · ALIGNED** |
| **Expense Void HTTP 409** | Server: `ExpensesController.cs` (L148, L170) | Error codes with `"mismatch"` (including `idempotency.payload_mismatch`) map to `StatusCodes.Status409Conflict` | **MATCH · ALIGNED** |
| **Khata Equal-Timestamp Cursor** | Server: `Phase5OperationsReadServices.cs` (L39–54)<br>Desktop: `SuppliersViewModel.cs` (L486–497) | Keyset tie-breaker: `(OccurredAt DESC, CreatedAt DESC, EntryId DESC)`. Both Server and Desktop advance using all 3 fields, preventing loop/omission on identical timestamps | **MATCH · ALIGNED** |
| **Authenticated Actor Scope** | Server: `ExpensesController.cs`, `SalesController.cs`<br>Application: `OperationStatusQueryHandler.cs` | `HttpContext.GetActorContext()` sets `ActorId`. `OperationStatusQuery` enforces `RequireIdentityScope`; mismatched actor returns `authorization.forbidden` (B06) | **MATCH · ALIGNED** |

---

## 8. Defect, Blocker, and Skip Summary

- **Test Failures Encountered:** **0**
- **Test Skips / Relaxations:** **0**
- **Assertion Weakening:** **0**
- **Missing Mandatory Coverage:** **0**
- **Active Technical Blockers:** **0**
- **Authorized Governance Deferrals:**
  - **B07 (Process Restart Recovery):** Formally deferred to **Phase 12** under master architecture governance. No Phase 12 implementation was attempted or required for Phase 7 / Pass 5 closure.

---

## 9. Final Independent Certification Verdict

The frozen candidate `candidate-r1` (`R1-H01-1`) has satisfied every mandatory prerequisite of the Phase 7 / Pass 5 Final21 certification without regressions, without uncommitted source drifts, and without assertion weakening.

```
================================================================================
FINAL VERDICT:
PHASE7_PASS5_INDEPENDENT_CERTIFIED_CLOSED_LOCKED
================================================================================
```

### Next Authorized Actions
1. **Preserve Freeze:** Candidate `candidate-r1` is locked and immutable.
2. **Phase 7 Complete:** Formal closure of Backend Phase 7 / Pass 5.
3. **Hard Stop:** No Phase 12 or Phase 13 implementation; no production deployment or database migration to operational environments.
