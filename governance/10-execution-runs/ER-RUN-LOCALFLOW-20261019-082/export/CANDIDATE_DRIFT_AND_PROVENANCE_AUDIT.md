# Candidate Drift and Provenance Audit: Run 082 vs Run 081

- **Run ID**: `ER-RUN-LOCALFLOW-20261019-082`
- **Predecessor Baseline**: `ER-RUN-LOCALFLOW-20261019-081`
- **Base Commit**: `3cd3b4b8f903adef6f6d7cf3c922c2ce184c2fc2`
- **Candidate Files**: `369` (`200` tracked, `169` untracked)
- **Identical Files**: `355`
- **Modified Files**: `8`
- **New Files**: `6`

## Intentional Modifications Summary (LF14 Option B Implementation)

1. `src/EdgeRetails.Application/Features/Reporting/ReportingQueries.cs`:
   - Extended `ReportingThakaActivityDto` record contract with optional parameters: `ReversalCount = 0`, `ReversalAmount = 0m`, `NetMaterialValue = 0m`.

2. `src/EdgeRetails.Infrastructure/Services/BusinessOperationsReadServices.cs`:
   - In `ReportingReadService.GetSnapshotAsync`, extracted count and sum of `ReversedCharge` from `ThakaMaterialReversals`.
   - Included reversal-only projects via `thakaIssueRaw.Select(x => x.ProjectId).Union(thakaReversalRaw.Keys).Distinct()`.
   - Populated explicit reversal metrics and calculated `netMaterialValue = issueAmount - reversalAmount`.
   - Sorted activity descending by `NetMaterialValue`.
   - Updated snapshot metric `thakaTotal` to subtract reversals.

3. `src/EdgeRetails.Desktop/Services/DemoReportingService.cs`:
   - Extended `ReportThakaActivityItem` with reversal tracking and formatted presentation properties (`ActivitySummaryDisplay`, `NetMaterialValueDisplay`, `HasReversals`).

4. `src/EdgeRetails.Desktop/Services/BackendBusinessOperationsService.cs`:
   - Mapped reversal metrics from `ReportingThakaActivityDto` to `ReportThakaActivityItem`.

5. `src/EdgeRetails.Desktop/Services/RemoteBackendBusinessOperationsService.cs`:
   - Mapped reversal metrics across remote HTTP API boundary to `ReportThakaActivityItem`.

6. `src/EdgeRetails.Desktop/Views/ReportsView.xaml`:
   - Updated Thaka activity card template data-binding to `ActivitySummaryDisplay` and `NetMaterialValueDisplay`.

7. `tests/EdgeRetails.UnitTests/Phase11Wave3ApiAndDesktopCutoverTests.cs`:
   - Added dedicated named behavioral test oracle `LF14_ReportingReadService_ReversalOnlyProject_IncludedWithExplicitReversalBreakdown`.

8. `tests/EdgeRetails.Desktop.PerformanceTests/Phase11Wave3DesktopCutoverTests.cs`:
   - Added dedicated named behavioral test oracle `LF14_ReportThakaActivityItem_PresentationFormatting_RendersSeparateReversalDetails`.

## Preserved Source Modules
All other components from Run 081 (including LF02, LF03, LF04, LF05, LF06, LF07, LF08, LF09, LF10, LF11, LF12, LF15) are 100% preserved bit-for-bit without change.
