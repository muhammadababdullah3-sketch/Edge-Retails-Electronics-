# STAGE 7: Bounded Correction Batch and Raw Evidence

**Run ID:** `ER-RUN-LOCALFLOW-20261019-082`
**Authority:** `OWNER-UPDATE-20261018-019` / `OWNER-UPDATE-20261019-001` / `OWNER-UPDATE-20261019-007`

## Causal Modifications & Test Oracles
1. `ReportingQueries.cs`: Added `ReversalCount`, `ReversalAmount`, and `NetMaterialValue` to `ReportingThakaActivityDto`.
2. `BusinessOperationsReadServices.cs`: Reversal aggregation, union inclusion of reversal-only projects, descending sort by net material value.
3. `DemoReportingService.cs` & `ReportsView.xaml`: Formatted display properties and card binding.
4. `Phase11Wave3ApiAndDesktopCutoverTests.cs`: `LF14_ReportingReadService_ReversalOnlyProject_IncludedWithExplicitReversalBreakdown` (PASS).
5. `Phase11Wave3DesktopCutoverTests.cs`: `LF14_ReportThakaActivityItem_PresentationFormatting_RendersSeparateReversalDetails` (PASS).
