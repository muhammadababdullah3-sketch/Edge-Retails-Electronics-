# Sprint 5 Phase 1 — Expenses, Customers & Suppliers

**Date:** 2026-09-19
**Status:** COMPLETE
**Backend:** intentionally separate
**Manual visual QA:** deferred
**Git:** untouched

## Delivered

Phase 1 implements primary screens 13, 14 and 15 using WPF/MVVM and the existing shared shell.

### Expenses
Expenses remain an operating-expense domain and are not mixed with Purchases.
The screen includes period and category filters, Today / This Month / Top Category KPIs, a compact expense table, and Add/Edit Expense modal flows.
Expense fields follow live Figma: Category, Subcategory, Amount, Date, Payment Method, Staff Member and Note.

### Customers
The customer directory uses existing frontend Sales and Thaka state rather than isolated fake totals.
Local Sales and Last Sale are derived from the sales ledger. Active Thaka is derived from active project state.
Add/Edit uses a modal and customer information uses the shared right-side DrawerHost.

### Suppliers
Supplier purchase metrics are derived from the existing Purchase History state.
Add/Edit uses a modal and supplier information uses the shared right-side DrawerHost.
New supplier names are registered into the existing New Purchase supplier source.

## Frontend state boundary

`DemoBusinessDirectoryService` is a temporary frontend execution/state adapter.
It contains no direct DB or HTTP/API access and is designed so the separately developed backend can replace temporary state sources later without redesigning Phase 1 screens.

## Quality gates

- Full formatter verify-no-changes: PASS.
- Debug build: 0 warnings / 0 errors.
- Debug tests: 60 / 60 unit + 1 / 1 integration.
- Release build: 0 warnings / 0 errors.
- Release tests: 60 / 60 unit + 1 / 1 integration.
- Runtime state smoke: PASS.
- Repeated-navigation event-subscription growth prevented by caching the three page ViewModels.
- No Canvas or React/Tailwind production implementation was introduced.

## Next

Sprint 5 Phase 2 implements Reports from the existing frontend ledgers and state.
It must not invent a final costing methodology or treat Thaka material issuance as recognized sales revenue.
