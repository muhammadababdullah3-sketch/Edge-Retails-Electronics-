# STAGE 4: Precise Evidence Claims Correction and Test Qualifications

**Run ID:** `ER-RUN-LOCALFLOW-20261019-079`
**Authority:** `OWNER-UPDATE-20261018-019` & `OWNER-UPDATE-20261019-007`
**Timestamp UTC:** `2026-10-19T12:00:00Z`

---

## 1. Evidence Qualifications & Seams

- **LF07 Behavioral Tests**: 5 named unit tests in `Phase2StocktakeThakaCashSessionBehavioralTests.cs` exercise production `OpenCashSessionHandler` and `CloseCashSessionHandler` with in-memory test doubles and configurable `IApplicationPermissionAuthorizer`. Proves unauthorized open rejected with `auth.forbidden`, empty actor on open rejected with `auth.actor_required`, unauthorized close rejected with `auth.forbidden`, empty actor on close rejected with `auth.actor_required`, and empty session id on close rejected with `cash.session_id_required`.
- **LF06 Behavioral Tests**: 4 named unit tests in `Phase2PurchasingAndKhataBehavioralTests.cs` exercise production `CreateSupplierPaymentHandler`, `CreateSupplierRefundHandler`, `SupplierAccountAdjustmentHandler`, and `ReverseSupplierRefundHandler`.
- **LF02 Behavioral Tests**: 4 named unit tests in `Phase1DPosResolutionAndExactUnitSaleTests.cs` exercise production `CompleteSaleHandler`.
- **Targeted J1**: Client operation ID reuse across readback timeouts and pre-POST validation rejection. Single server ledger cash effect qualified as UNPROVEN / OPEN.
- **Targeted J2**: Full UI Command Boundary (`ICommand.Execute` on WPF Dispatcher). Verifies no unhandled exceptions escape the async void boundary.
- **Targeted J3**: ViewModel + Mock Service Seam for stock adjustment retry and cancellation.
- **Targeted J4**: Production ViewModel Execution Boundary for warranty claim lifecycle.
- **Targeted J5**: Service + Fake HTTP Paging Seam for 201 units cursor pagination.
- **Build Evidence**: 12 projects built with 0 warnings and 0 errors (`build.log`).
