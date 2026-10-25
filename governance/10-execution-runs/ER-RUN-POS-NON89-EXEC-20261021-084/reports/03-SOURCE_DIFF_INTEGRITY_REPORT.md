# Stage 03: Source Diff Integrity Report
**Run ID**: ER-RUN-POS-NON89-EXEC-20261021-084 | **Baseline**: ER-RUN-POS-NON89-EXEC-20261021-083

## Files Modified for LF01 and LF13 Execution
1. `src/EdgeRetails.Application/Features/Sales/SaleReturnHandler.cs` (rejects unsupported refund methods / store credit)
2. `src/EdgeRetails.Desktop/Services/BackendTransactionService.cs` (rejects store credit and other in refund mapping)
3. `src/EdgeRetails.Desktop/Services/DemoTransactionService.cs` (rejects store credit and unapproved methods)
4. `src/EdgeRetails.Desktop/ViewModels/SalesReturnViewModel.cs` (blocks return processing on unapproved refund methods)
5. `src/EdgeRetails.Desktop/Services/DemoBusinessDirectoryService.cs` (clamps per-project balances to Math.Max(0m, balance))
6. `tests/EdgeRetails.UnitTests/Phase11Wave3ApiAndDesktopCutoverTests.cs` (behavioral tests for LF01, LF13, and THAKA-AUD-04)
7. `tests/EdgeRetails.Desktop.PerformanceTests/Phase11Wave3DesktopCutoverTests.cs` (behavioral tests for LF01 and LF13)
