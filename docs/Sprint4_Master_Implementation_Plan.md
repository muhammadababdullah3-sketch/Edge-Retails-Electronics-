# Edge Retails — Sprint 4 Master Implementation Plan

**Date:** 2026-09-19
**Status:** IMPLEMENTATION CLOSED — manual visual QA, backend attachment and git freeze deferred
**Scope:** Purchases + Inventory + Product Detail + controlled stock operations

## Live Figma authority
- Purchase History: `14:5212`
- New Purchase: `14:6514`
- Inventory: `14:6814`
- Product Detail / Edit Product: `14:14561`
- Inventory Stock Adjustment: `14:14964`
- Purchase Return: `16:18524`

## Core business rules
- Purchase is an inventory intake workflow, not an operating expense.
- Current stock is never directly edited.
- Every stock mutation must be explainable by a movement.
- Purchase Return is capped by eligible return quantity.
- Purchase Return never rewrites the original purchase record.
- Inventory Value stays hidden until costing policy is formally approved.
- Decimal quantity support remains mandatory.
- Existing Sprint 1–3 flows must keep working.

## Phase 0 — Safety and contract
- Preserve current source; no destructive git operations.
- Read live Figma nodes before implementing each surface.
- Reuse existing shell, tokens, buttons, tables, cards and overlay hosts.
- Keep Purchases and Inventory inside the existing 17-screen contract.
## Phase 1 — Shared purchase/inventory demo state
- Create one purchase/inventory service over the existing shared product stock.
- Add immutable purchase records and purchase line records.
- Add inventory movement records with before/after quantities.
- Seed deterministic Purchase History data without corrupting current POS stock.
- Support PURCHASE_IN, PURCHASE_RETURN_OUT and ADJUSTMENT_IN/OUT first.
- Expose movement history for Inventory and Product Detail.

## Phase 2 — Purchases screens
- Map sidebar Purchases to real Purchase History.
- Implement Purchase History search/date/supplier filters and summary.
- Implement New Purchase as Screen 09 within the Purchases flow.
- Save Purchase must validate supplier, invoice and line quantities.
- Save Purchase must increase stock and write PURCHASE_IN movements.
- Purchase Detail remains a right-side drawer.
- Purchase Return uses the verified Figma flow and eligible quantity columns.
- Return processing reduces sellable stock and writes PURCHASE_RETURN_OUT.

## Phase 3 — Inventory screen
- Implement Total Products, Low Stock and Out of Stock KPIs.
- Implement All Stock / Low Stock / Out of Stock / Stock Movements tabs.
- Implement category, brand and stock-status filters.
- Inventory table shows product, brand, stock, minimum stock, cost and sale price.
- Do not expose an editable Current Stock field.
- Movement rows use compact semantic direction indicators.
## Phase 4 — Product Detail and stock operations
- Product Detail opens from Inventory without creating an extra primary nav item.
- Tabs: Overview, Stock Movement, Purchases and Sales.
- Show current stock, purchase cost, sale price and minimum stock.
- Optional electronics fields remain optional: model, warranty, serial/IMEI, color, variant.
- Add/Edit Product is an overlay/dialog.
- Stock Adjustment is a controlled dialog with reason and note.
- Adjustment cannot produce negative sellable stock.
- Every adjustment writes an inventory movement.

## Phase 5 — Integration hardening
- Preserve local sale and Thaka stock caps.
- Reconcile Sprint 3 stock mutations with the Sprint 4 movement projection.
- Ensure one visible stock figure is shared across POS, Thaka, Purchases and Inventory.
- Keep original purchases immutable after save.
- Keep purchase return history separately auditable.
- Add Sprint4ForensicAuditTests for navigation, stock truth and return eligibility.

## Phase 6 — Gates
- Debug build, all automated tests and Release build must pass.
- No Canvas, no web/React residue and no unresolved WPF resource references.
- Manual visual QA may be deferred separately, but code/integration gates must be explicit.
- Write Sprint4_Implementation_Report.md before implementation closure.
- Do not perform git cleanup, commit or freeze unless the product owner asks.

## Progress Update — Phase 4 Complete

**Date:** 2026-09-19
**Status:** Phase 4 implementation complete; manual visual QA deferred.

Completed:
- Purchase Detail opens in the shared right-side DrawerHost.
- Purchase Return opens as a ModalHost workflow from Purchase Detail.
- Purchase Return exposes purchased, used, returned, eligible and return quantity.
- Return processing enforces eligible quantity and current sellable-stock caps.
- Product Detail remains inside the Inventory primary flow and does not add a new nav screen.
- Product Detail tabs: Overview, Stock Movement, Purchases and Sales.
- Sales tab now reads the shared sales transaction ledger, not placeholder copy.
- Add Product / Edit Product use the shared DialogHost.
- Product editing cannot directly mutate Current Stock.
- Duplicate SKU is rejected across add/edit flows.
- Stock Adjustment is an audited modal workflow with reason/note and negative-stock protection.
- Product metadata is editable while Product ID remains stable.
- Shared stock-mutation bridge now records Local Sale, Sale Return and Thaka material mutations for Inventory movement projection.

Verification:
- Debug build: PASS, 0 warnings, 0 errors.
- Debug tests: 42 unit + 1 integration PASS.
- Release build: PASS, 0 warnings, 0 errors.
- Release tests: 42 unit + 1 integration PASS.

Phase 5 integration hardening and cross-module stock reconciliation is completed below. Next: Phase 6 final gates.

## Interim Status — Phase 5 Complete (2026-09-19)
- Phases 0–5 implementation: COMPLETE.
- Shared stock integration across POS / Thaka / Purchases / Inventory: HARDENED.
- Canonical opening-balance stock reconciliation: IMPLEMENTED.
- Purchase-return audit trail: IMPLEMENTED.
- Checkout live-stock pre-commit validation: IMPLEMENTED.
- Debug and Release builds: PASS with 0 warnings and 0 errors.
- Automated tests: 49 unit + 1 integration PASS in Debug and Release.
- Runtime cross-module stock smoke: PASS with 0 reconciliation issues at every checkpoint.
- Phase 6 final gates and Sprint 4 final implementation report: PENDING.
- Manual UI / visual QA: DEFERRED by product owner.
- Git cleanup / commit / freeze: DEFERRED by product owner.

## Phase 6 — Final Gates Complete (2026-09-19)

- Formatter verify-no-changes: PASS.
- Debug build: PASS, 0 warnings, 0 errors.
- Debug tests: 49 unit + 1 integration PASS.
- Release build: PASS, 0 warnings, 0 errors.
- Release tests: 49 unit + 1 integration PASS.
- Web / React residue: 0.
- Canvas usage: 0.
- Unresolved Sprint 4 implementation markers: 0.
- Missing custom WPF resource keys: 0.
- Desktop XAML XML parse errors: 0.
- Direct editable stock bindings: 0.
- Inventory Value UI occurrences: 0.
- Direct Desktop DB/API-client coupling markers: 0.
- Final report: `docs/Sprint4_Implementation_Report.md`.

### Frontend / backend boundary
Backend design remains separate. Sprint 4 uses temporary frontend/demo state services only so the UI flows can be executed and tested. No backend/database attachment is part of this closure. When the overall frontend is complete, the real backend should be attached behind stable contracts without redesigning these screens.

### Closure
Sprint 4 implementation is CLOSED at automated code/integration level.
Manual visual QA remains deferred. Git cleanup/commit/freeze remains deferred.
