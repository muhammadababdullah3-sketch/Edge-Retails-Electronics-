# Worker Documentation: Read-Only Finance Reviewer

- **Worker ID**: `/coordinator/run075_finance_review`
- **Role**: Read-Only Domain Auditor for Financial Sub-Ledger Contracts (LF03, LF04, LF06, LF07, LF13, LF14)
- **Constraint**: Strict read-only audit. No test execution, no database writes, no UI manipulation.
- **Findings**:
  1. `SuppliersViewModel.cs`: Verified that `PostPaymentAsync`, `PostRefundAsync`, `ReversePaymentAsync`, and `ReverseRefundAsync` catch `DesktopApiException` when `DesktopOperationRejectionClassifier.IsAuthoritativeDeterministicBusinessRejection(ex)` is true, display warning toast, clear matching `_unresolvedFinancialAction`, and return cleanly without `throw;`.
  2. Verified action tokens match exactly between `BeginFinancialSubmission` and catch block cleanup.
  3. Verified `ExpensesViewModel.cs` pre-POST validation separation correctly distinguishes `ExpensePrePostValidationException` (0 commits, intent completed) from transport/readback timeouts (intent retained).
  4. Confirmed customer directory balance calculation in `BusinessOperationsReadServices.cs` accounts for credit refunds correctly.
