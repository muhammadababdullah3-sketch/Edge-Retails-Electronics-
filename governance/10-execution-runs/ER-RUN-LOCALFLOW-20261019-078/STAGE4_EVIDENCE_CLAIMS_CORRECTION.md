# STAGE 4: Precise Evidence Claims Correction and Test Qualifications

**Run ID:** `ER-RUN-LOCALFLOW-20261019-078`
**Authority:** `OWNER-UPDATE-20261018-019` & `OWNER-UPDATE-20261019-007`
**Timestamp UTC:** `2026-10-19T12:00:00Z`

---

## 1. Evidence Qualifications & Seams

- **LF06 Behavioral Tests**: 4 named unit tests in `Phase2PurchasingAndKhataBehavioralTests.cs` exercise production `CreateSupplierPaymentHandler`, `CreateSupplierRefundHandler`, `SupplierAccountAdjustmentHandler`, and `ReverseSupplierRefundHandler` with in-memory doubles. Proves undefined purpose/method rejected with `supplier.payment_invalid`, undefined refund method rejected with `supplier.refund_invalid`, undefined adjustment direction rejected with `supplier.adjustment_invalid`, and missing refund reversal parameters rejected with `supplier.refund_reversal_invalid`.
- **LF02 Behavioral Tests**: 4 named unit tests in `Phase1DPosResolutionAndExactUnitSaleTests.cs` exercise production `CompleteSaleHandler` with in-memory doubles. Proves distinct serialized units and distinct price overrides succeed on separate lines, duplicate units reject with `sales.serial_selected_twice`, and duplicate commodity lines reject with `sales.duplicate_line`.
- **Targeted J1**: Client operation ID reuse across readback timeouts and pre-POST validation rejection. Single server ledger cash effect qualified as UNPROVEN / OPEN.
- **Targeted J2**: Full UI Command Boundary (`ICommand.Execute` on WPF Dispatcher). Verifies no unhandled exceptions escape the async void boundary.
- **Targeted J3**: ViewModel + Mock Service Seam for stock adjustment retry and cancellation.
- **Targeted J4**: Production ViewModel Execution Boundary for warranty claim lifecycle.
- **Targeted J5**: Service + Fake HTTP Paging Seam for 201 units cursor pagination.
- **Build Evidence**: 12 projects built with 0 warnings and 0 errors (`build.log`).
