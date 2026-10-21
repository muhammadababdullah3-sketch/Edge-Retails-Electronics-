# STAGE 1: Reproduction of Root Findings and LF14 Option B Execution
**Run ID:** `ER-RUN-POS-NON89-EXEC-20261021-083`
**Authority:** `OWNER-UPDATE-20261018-019 / OWNER-UPDATE-20261019-001 / OWNER-UPDATE-20261019-007 / OWNER-UPDATE-20261020-023`
**Timestamp UTC:** `2026-10-21T12:00:00Z`

## 1. Finding LF14 Root Cause & Execution
In predecessor Run 082, LF14 implemented initial Option B structure. In Run 083, the full presentation semantics requested by the owner were completed and verified:
1. **Explicit Breakdown Semantics**:
   - `ReportThakaActivityItem.ActivitySummaryDisplay` presents both gross issued facts (`IssueCountDisplay`, `MaterialValueDisplay` issued) and gross returned facts (`ReversalCountDisplay`, `ReversalAmountDisplay` returned).
   - `NetMaterialValueDisplay` formats negative values as `-$Rs. X.XX` and positive as `$Rs. X.XX`.
   - `ReportsView.xaml` renders a distinct "Net" caption stacked above the net material value.
2. **Relational Fixture Integrity**:
   - EF Core configuration in `ThakaConfigurations.cs:226` defines `HasOne().WithMany().HasForeignKey(r => r.MaterialIssueId)`.
   - Test fixtures in `Phase11Wave3ApiAndDesktopCutoverTests.cs` instantiate and link to persisted historical `ThakaMaterialIssue` instances.
3. **Headline KPI Clamp vs Row-Level Ledger Integrity**:
   - `snapshot.ThakaMaterial` in `BusinessOperationsReadServices.cs` clamps to 0 via `Round(Math.Max(0m, thakaTotal))` during wholly reversal periods.
   - Row-level `ThakaActivity` entries preserve negative net material value (e.g. `-5000m`), isolating headline KPI clamp without corrupting ledger rows.
