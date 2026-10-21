# Stage 02: Architecture Audit and LF14 Causal Analysis
**Run ID**: `ER-RUN-LOCALFLOW-20261019-082` | **Focus**: `LF14 (Thaka Reversal Reporting Policy - Option B)`

## Causal Defect Analysis
1. `ReportingQueries.cs`: `ReportingThakaActivityDto` lacked reversal breakdown properties.
2. `BusinessOperationsReadServices.cs`: Reversal-only projects were dropped because querying only collected IDs from `ThakaMaterialIssues`.
3. `ReportsView.xaml`: UI card had no data binding for reversal metrics.
4. Causal fix implements Option B: contract extension, union querying of projects, net material computation, and desktop summary formatting.
