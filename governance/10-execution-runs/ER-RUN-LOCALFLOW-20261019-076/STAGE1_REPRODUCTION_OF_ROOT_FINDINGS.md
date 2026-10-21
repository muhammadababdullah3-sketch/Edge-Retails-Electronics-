# STAGE 1: Reproduction of Root Findings C1–C7 and Canonical Run 007 Findings (LF01–LF15)

**Run ID:** `ER-RUN-LOCALFLOW-20261019-076`
**Authority:** `OWNER-UPDATE-20261018-019` / `OWNER-UPDATE-20261019-001` / `OWNER-UPDATE-20261019-007`
**Predecessor Review:** `governance/14-execution-environment/2026-10-19-monitor/run075-root-review/ROOT_REVIEW.md`
**Timestamp UTC:** `2026-10-19T12:00:00Z`

---

## 1. Executive Summary & Root Review Findings C1–C7

In Run 075, the distinct root review issued `CHANGES_REQUIRED` on evidence claims while accepting the J2 source repair in `SuppliersViewModel.cs`. Here is the exact reproduction and resolution of findings C1 through C7:

### Finding C1: J1 Client Operation-ID Reuse vs Server-Side Ledger Effect
- **Root Diagnosis**: Run 075 claimed that test oracle J1 proved a single durable cash effect on the server ledger based solely on a fake `HttpMessageHandler` asserting `clientOperationId` reuse.
- **Run 076 Reproduction & Correction**:
  - Tested test oracle `Targeted_J1_Expenses_RealAdapter_CommittedPostMissingReadback_OperationIdReuse_And_PrePostValidation_NoPost`.
  - Proved:
    1. Pre-POST validation rejection: Category validation rejection sends 0 network POST requests, clears client intent, and leaves `CanEdit == true`.
    2. Committed POST with missing readback: Retains client intent in `ClientOperationIntentStore`. Subsequent retry sends exactly 2 POSTs and reuses the original `clientOperationId`.
  - **Correction**: Formally qualified in report and test documentation that a single server-side cash effect on the database ledger remains **UNPROVEN / OPEN** without raw isolated PostgreSQL queries.

### Finding C2: J2 Command Boundary Execution & Refund Reversal
- **Root Diagnosis**: Run 075 J2 tests obtained private methods via reflection and called `Invoke()` directly, skipping `ICommand.Execute` execution, and omitted `ReverseRefundCommand` / `ReverseRefundAsync`.
- **Run 076 Reproduction & Correction**:
  - Source file `SuppliersViewModel.cs` already contains clean handling of `ReverseRefundAsync` clearing action token `"refund-reversal:" + id`.
  - Added explicit test coverage in `Targeted_J2` for `ReverseRefundAsync` verifying that deterministic rejection clears `"refund-reversal:<id>"`.
  - Added direct invocation of `((ICommand)vm.PostSettlementCommand).Execute(null)`, `PostAdvanceCommand`, `ReceiveRefundCommand`, `ReversePaymentCommand`, and `ReverseRefundCommand`.
  - Verified: Deterministic rejections handled inside `async void` delegates do **NOT** throw escaping unhandled exceptions across the WPF command boundary.

### Finding C3: J4 Production Warranty Identity and Fingerprint
- **Root Diagnosis**: Run 075 J4 used a test-local `BuildPayload` helper function instead of exercising production `WarrantyViewModel` canonical serialization.
- **Run 076 Reproduction & Correction**:
  - Refactored `Targeted_J4` to instantiate production `WarrantyViewModel` directly with real dependencies.
  - Set `SelectedIntakeRow` and UI text inputs (`ClaimIntakeUnitIdsText`).
  - Exercised production `CreateClaimAsync` and `CreateClaimCommand`.
  - Verified:
    1. Permuting serialized unit order (`UnitB, UnitA` vs `UnitA, UnitB`) produces identical canonical fingerprint, reusing operation ID on retry.
    2. Swapping a unit (`UnitA, UnitC`) produces a different fingerprint, caught by production code which shows a warning toast without mutating pending intent.
    3. Completing claim 1 allows the next legitimate partial claim on the remaining unit.

### Finding C4: Historical Integration TRX Counter Provenance
- **Root Diagnosis**: Pack and PostgreSQL read contract TRX files dated 18 Oct were included in Run 075 without contemporaneous timestamps.
- **Run 076 Reproduction & Correction**:
  - Verified port 5432 is operational (PID 5364) and port 55437 is inactive.
  - Fail-closed policy applied: No integration tests were run against operational database.
  - Pack (14 cases) and PostgreSQL (5 cases) TRX files are deposited and cataloged explicitly as `HISTORICAL_PRIOR_EVIDENCE_RECONCILED` with original 18 Oct timestamps clearly distinguished from Run 076 contemporaneous executions.

### Finding C5: Missing Raw Build Output
- **Root Diagnosis**: Run 075 claimed 12 projects built with 0 warnings / 0 errors without depositing raw build output.
- **Run 076 Reproduction & Correction**:
  - Executed `dotnet build` capturing raw output into `build.log`.
  - Deposited `build.log` (7,330 bytes, sha256: `2a5ebe854f3ca197d4086e6b5f909fb54cafa978860efffca5304686b930844c`).
  - Confirmed 0 Warnings and 0 Errors across all 12 projects.

### Finding C6: Timestamp Provenance Distinction
- **Root Diagnosis**: Receipt and stage files stated 12:00Z, while filesystem test timestamps were local PKT time.
- **Run 076 Reproduction & Correction**:
  - Formally documented that `2026-10-19T12:00:00Z` is the canonical fixed timestamp used for bit-for-bit reproducible ZIP archive generation.
  - Contemporaneous test runs and build events are documented with actual runtime timestamps.

### Finding C7: Seam Attribution & Canonical LF01–LF15 Audit Scope
- **Root Diagnosis**: J3 and J5 were presented without explicit qualification of mock/adapter seams.
- **Run 076 Reproduction & Correction**:
  - Explicitly qualified J3 as testing `StockAdjustmentViewModel.ApplyAsync` through a mock backend proxy seam (`StockAdjustmentMockProxy`).
  - Explicitly qualified J5 as testing `RemoteStocktakeWorkflowService` unit cursor paging through a fake HTTP message handler seam.
  - Cataloged all 15 canonical Run 007 findings (LF01–LF15) without substitute IDs, preserving unresolved items as OPEN.
