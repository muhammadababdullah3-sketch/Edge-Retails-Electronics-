# STAGE 1: Reproduction of Root Findings and LF14 Causal Remediation

**Run ID:** `ER-RUN-LOCALFLOW-20261019-082`
**Authority:** `OWNER-UPDATE-20261018-019` / `OWNER-UPDATE-20261019-001` / `OWNER-UPDATE-20261019-007`
**Predecessor Review:** `governance/10-execution-runs/ER-RUN-LOCALFLOW-20261019-081/STAGE1_REPRODUCTION_OF_ROOT_FINDINGS.md`
**Timestamp UTC:** `2026-10-19T16:00:00Z`

---

## 1. Executive Summary & LF14 Remediation

In Run 081, canonical finding **LF14** (*Reversal-only reporting activity policy*) remained `REMAINS_OPEN` pending owner policy authorization between:
- Option A: Net Material Policy (collapse reversals into MaterialValue, showing 0 or negative value).
- Option B: Explicit Reversals (DTO and Desktop UI separate reversal column/text representation).
- Option C: Filter Reversal-Only (hide projects with 0 issues).

The owner explicitly authorized **Option B** (*Explicit Reversals*).

### LF14 Root Causal Defects Under Option B
1. **Contract Inadequacy in DTO**:
   - Location: `src/EdgeRetails.Application/Features/Reporting/ReportingQueries.cs`
   - Defect: `ReportingThakaActivityDto` lacked explicit fields for reversal count and reversal amount.
2. **Exclusion of Reversal-Only Projects**:
   - Location: `src/EdgeRetails.Infrastructure/Services/BusinessOperationsReadServices.cs`
   - Defect: `ReportingReadService.GetSnapshotAsync` only queried project IDs from `ThakaMaterialIssues`, completely dropping projects that had reversals but no issues in the current reporting period.
3. **Desktop Presentation Inadequacy**:
   - Location: `src/EdgeRetails.Desktop/Views/ReportsView.xaml` & `DemoReportingService.cs`
   - Defect: Presentation card only rendered `IssueCountDisplay` and `MaterialValueDisplay`, providing no mechanism to display explicit reversal activity.

### Causal Fix Implemented in Run 082
1. Extended `ReportingThakaActivityDto` with `ReversalCount`, `ReversalAmount`, and `NetMaterialValue`.
2. Updated `ReportingReadService.GetSnapshotAsync` to query `ThakaMaterialReversals`, include reversal-only projects via union of IDs, compute net material value, and order descending by net material value.
3. Updated `ReportThakaActivityItem` and `ReportsView.xaml` to render `ActivitySummaryDisplay` (`0 issues · 1 reversal (-Rs. 5,000.00)`) and `NetMaterialValueDisplay`.
4. Verified with named behavioral test oracles in `Phase11Wave3ApiAndDesktopCutoverTests.cs` and `Phase11Wave3DesktopCutoverTests.cs`.
