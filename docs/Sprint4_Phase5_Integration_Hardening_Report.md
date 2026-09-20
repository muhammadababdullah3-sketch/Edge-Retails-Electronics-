# Sprint 4 Phase 5 — Integration Hardening Report

**Product:** Edge Retails
**Date:** 2026-09-19
**Phase:** Integration Hardening / Cross-Module Stock Reconciliation
**Status:** COMPLETE
**Manual visual QA:** Deferred by product owner
**Git cleanup / commit / freeze:** Deferred by product owner

## Objective

Phase 5 hardens the shared stock truth across POS, Sales Return, Thaka, Purchases and Inventory.
No module owns an independent current-stock figure. All operational flows reference the same shared product objects and every controlled stock mutation is projected into the Inventory movement ledger.

## Implemented

- Added canonical Opening Balance inventory movements for every shared product.
- Added automatic baseline creation for products added after Inventory service initialization.
- Added programmatic `AuditStockTruth()` reconciliation.
- Reconciliation compares baseline + post-baseline movement deltas against the live shared product stock.
- InventoryViewModel tracks `StockTruthIssueCount` and `IsStockTruthHealthy`.
- Local Sale mutations continue to write SALE_OUT movements with the real invoice reference.
- Thaka material issue continues to write THAKA_OUT movements.
- Sellable Sale Return writes SALE_RETURN_IN and increases sellable stock.
- Damaged / defective / other non-sellable Sale Return is recorded without increasing sellable stock.
- Purchase intake writes PURCHASE_IN against the same shared product object.
- Purchase Return writes PURCHASE_RETURN_OUT.
- Stock Adjustment writes ADJUSTMENT_IN / ADJUSTMENT_OUT and cannot produce negative sellable stock.
- Purchase Return eligible quantity is now capped by both receipt eligibility and current sellable stock.

## Purchase Return auditability

Purchase Return no longer exists only as a mutation on the purchase-line projection.
A separate `PurchaseReturnRecord` history is maintained with:
- unique return number,
- original purchase number,
- timestamp,
- reason and note,
- returned product lines,
- returned quantity,
- unit cost,
- return value.

Inventory movements reference the purchase-return number, while the movement reason retains the source purchase number.

## Checkout hardening

Complete Sale now executes a live stock pre-commit validation immediately before transaction recording.
If any cart quantity exceeds the current shared product stock, transaction recording is blocked and the checkout remains open with a validation message.
This closes the practical single-process gap where stock could change after the checkout modal was opened.
## Automated verification

- Debug build: PASS, 0 warnings, 0 errors.
- Debug unit tests: 49 / 49 PASS.
- Debug integration tests: 1 / 1 PASS.
- Release build: PASS, 0 warnings, 0 errors.
- Release unit tests: 49 / 49 PASS.
- Release integration tests: 1 / 1 PASS.

## Runtime cross-module smoke

A temporary runtime harness was created outside the repository, executed, and then deleted.
The harness used the real Desktop shared services and verified reconciliation after each operation:

1. canonical baseline
2. local sale
3. Thaka material issue
4. purchase intake
5. purchase return
6. stock adjustment
7. sellable sale return
8. damaged / non-sellable sale return

Every checkpoint returned zero reconciliation issues.
Final runtime marker: `PHASE5_RUNTIME_SMOKE=PASS`.

## Boundary / business decision retained

General-retail purchase-lot consumption attribution (for example FIFO/LIFO attribution of a later sale to one exact historical purchase receipt) is not invented in this phase.
The current safe rule uses recorded receipt usage/returns and caps Purchase Return eligibility by current sellable stock.
Exact lot attribution belongs with the future costing / inventory-lot policy; serialized electronics can later use explicit unit-level linkage.

## Next

Proceed to Sprint 4 Phase 6 final gates:
Debug/Release verification, complete automated test run, formatter verification, WPF resource/static hygiene checks, final Sprint 4 implementation report, and closure classification.
Manual visual QA remains separately deferred.
