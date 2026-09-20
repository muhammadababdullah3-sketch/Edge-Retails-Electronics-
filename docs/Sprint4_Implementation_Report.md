# Edge Retails — Sprint 4 Implementation Report

**Date:** 2026-09-19
**Scope:** Purchases + Inventory + Product Detail + Controlled Stock Operations
**Implementation status:** CLOSED
**Manual UI / visual QA:** DEFERRED by product owner
**Git cleanup / commit / freeze:** DEFERRED by product owner
**Backend attachment:** DEFERRED until the frontend implementation is complete

## Closure statement

Sprint 4 frontend implementation is closed at the code, integration and automated-gate level.
This closure does not mean the entire Edge Retails frontend is complete and does not attach the separately designed backend.

Sprint 4 delivers the Purchases and Inventory workflows against temporary in-process frontend/demo state contracts. Those contracts exist to make the WPF flows executable and testable while the backend is designed separately.

## Delivered frontend surfaces

- Purchase History
- New Purchase
- Purchase Detail right-side drawer
- Purchase Return modal
- Inventory
- Product Detail
- Add Product / Edit Product modal
- Stock Adjustment modal

The implementation remains within the agreed 17-primary-screen contract. Supporting purchase/inventory detail and edit flows are overlays or nested flows rather than new sidebar destinations.

## Stock and inventory contract

Current Stock is not directly editable.
Operational stock is shared across the current frontend flows and every controlled mutation is represented in the inventory movement projection.

Implemented movement categories include:
- Opening Balance
- Purchase In
- Purchase Return Out
- Sale Out
- Sale Return In
- Thaka Out
- Adjustment In / Out
- non-sellable Damage/Return audit events

Inventory Value remains intentionally absent because the costing policy is not yet approved.
## Cross-module hardening

Sprint 4 Phase 5 hardened the frontend stock contract across POS, Sales Return, Thaka, Purchases and Inventory.

The frontend now provides:
- canonical opening-balance reconciliation,
- `AuditStockTruth()`,
- automatic baseline creation for newly added products,
- invoice-referenced local-sale stock movements,
- Thaka material stock movements,
- sellable and non-sellable sale-return handling,
- separately auditable purchase-return records,
- purchase-return eligibility capped by receipt eligibility and current sellable stock,
- live stock re-validation immediately before checkout transaction recording,
- negative-stock prevention for controlled adjustments.

A runtime cross-module smoke exercised real frontend services through baseline, local sale, Thaka issue, purchase intake, purchase return, stock adjustment, sellable return and damaged return.
Every checkpoint reported zero reconciliation issues.
Runtime marker: `PHASE5_RUNTIME_SMOKE=PASS`.

## Backend integration boundary

The backend is being designed separately and is intentionally **not attached in Sprint 4**.

No direct database or network/API client integration was introduced into the Desktop frontend for this work. A source audit found no Desktop usage of:
- `DbContext`
- `Npgsql`
- `HttpClient`
- `HttpRequest`
- `RestClient`
- `ApiClient`

`DemoRetailState`, `DemoTransactionService` and `DemoPurchaseInventoryService` are temporary frontend execution/state adapters, not the final persistence architecture.

When the frontend is complete, backend attachment should replace these temporary data/state sources behind stable application contracts rather than redesigning the screens.
The backend remains responsible for authoritative persistence, transactions, inventory-domain mutation rules, concurrency, audit durability and PostgreSQL storage.

## Phase 6 static hygiene

Final static audits returned:
- Web / React residue files: **0**
- WPF `Canvas` usage: **0**
- unresolved Sprint 4 TODO/FIXME/placeholder implementation markers: **0**
- missing custom StaticResource / DynamicResource keys: **0**
- XAML XML parse errors across the Desktop project: **0**
- direct editable stock bindings: **0**
- Inventory Value UI occurrences: **0**
- direct Desktop DB/API-client coupling markers: **0**

All Sprint 4 primary/dialog XAML surfaces parse successfully.
## Final formatter / build / test gates

`dotnet format EdgeRetails.sln --verify-no-changes --no-restore`
- PASS, exit code 0

Debug:
- Build: PASS
- Warnings: 0
- Errors: 0
- Unit tests: 49 / 49 PASS
- Integration tests: 1 / 1 PASS

Release:
- Build: PASS
- Warnings: 0
- Errors: 0
- Unit tests: 49 / 49 PASS
- Integration tests: 1 / 1 PASS

## Deferred items

The following are deliberately outside this implementation closure:
- manual Figma side-by-side visual QA,
- manual Light/Dark theme QA,
- manual 1440×900 and 1366×768 journey QA,
- backend/database attachment,
- final costing / purchase-lot attribution policy,
- git cleanup, commit and freeze.

The purchase-return safety rule does not invent FIFO/LIFO attribution. Exact receipt/lot consumption policy remains a backend/domain business decision.

## Sprint 4 result

**Sprint 4 implementation: CLOSED.**

Purchases, Inventory, Product Detail and controlled stock operations are implemented and pass the automated frontend code/integration gates.
The next frontend sprint can proceed without attaching the backend yet.
