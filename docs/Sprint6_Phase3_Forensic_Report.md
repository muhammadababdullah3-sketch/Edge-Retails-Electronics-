# Sprint 6 Phase 3 — Final Journey Forensic Report

**Date:** 2026-09-20
**Status:** CLOSED — FRONTEND AUTOMATED / FORENSIC LEVEL
**Manual visual QA:** deferred
**Backend attachment:** deferred
**Git:** untouched

## Scope

Phase 3 closes Sprint 6 by exercising the completed frontend as one operational system rather than as isolated screens.

The audit covered:
- Login / User Switch lifecycle.
- navigation and authorization boundaries.
- modal/drawer lifecycle.
- Sales and Sale Returns.
- Thaka material issue.
- Purchases and Purchase Returns.
- Stock Adjustment and canonical stock-truth reconciliation.
- Expenses.
- Customers and Suppliers.
- Reports aggregation.
- reusable missing-state foundation.
- final source/static hygiene.

## Delivered

### Real Login / User Switch journey

The Shell now exposes a real **Switch user** action in the top bar.

Switch User performs a clean session boundary:
1. closes DialogHost,
2. closes DrawerHost,
3. disposes the active Shell subscription,
4. resets NavigationService current page state,
5. clears cached page ViewModels and their subscriptions,
6. returns MainViewModel to Login/User Switch.

The Login/User Switch screen therefore remains one of the canonical 17 primary screens; no new screen was introduced.

### Cross-module state verification

An isolated WPF runtime harness executed real shared demo services and verified the following sequence:

1. baseline Inventory stock-truth audit.
2. create a saved Customer.
3. create a saved Supplier.
4. record a Local Sale.
5. apply the Sale stock mutation.
6. record a sellable Sale Return.
7. apply return stock mutation.
8. save a Purchase.
9. record a Purchase Return.
10. issue Thaka material.
11. record Stock Adjustment.
12. save an Expense.
13. refresh Customer/Supplier metrics.
14. rebuild Reports snapshot.
15. verify inventory movement references.
16. verify Dialog/Drawer disposal lifecycle.
17. verify Shell -> Login User Switch lifecycle.

Stock truth remained reconciled after:
- Local Sale,
- Sale Return,
- Purchase,
- Purchase Return,
- Thaka issue,
- Stock Adjustment.

Runtime delta verification:
- Net Sales delta: Rs. 375.00.
- Purchases delta: Rs. 1,075.00.
- Expense delta: Rs. 1,234.00.
- Thaka material delta: Rs. 150.00.
- Customer Local Sales: Rs. 375.00.
- Supplier Total Purchases: Rs. 1,075.00.

Result:
`SPRINT6_PHASE3_RUNTIME_SMOKE=PASS`

The same journey smoke passed against Debug and Release frontend binaries.

## Forensic findings resolved

### S6-P3-F01 — Shell had no real User Switch route
**Severity:** High

Login already supported account selection, but an authenticated user had no explicit Shell action to return to Login/User Switch.

**Resolution:** Added SwitchUserCommand and top-bar Switch action with clean session teardown/reset.

### S6-P3-F02 — Session boundary needed explicit navigation/page-cache reset
**Severity:** High

Cached pages are intentional for subscription lifetime safety, but cached session state must not survive a user switch.

**Resolution:** NavigationService.Reset() and PageViewModelFactory.ClearCachedPages() are invoked during Switch User.

### S6-P3-F03 — Source corruption from tool-preview text
**Severity:** High

CustomersView.xaml, ExpensesView.xaml and SuppliersView.xaml contained accidental literal tool-preview lines such as `[Reading ...]` / `[executed on device: ...]`.

**Resolution:** Removed only the accidental preview garbage, preserved the XAML content, scanned the Desktop source tree for recurrence, and rebuilt successfully.

### S6-P3-F04 — Overlay replacement/close lifecycle required proof
**Severity:** Medium

A final lifecycle audit was required to ensure replaced or closed modal/drawer content does not retain disposable state.

**Resolution:** Runtime smoke verified replaced and closed Dialog/Drawer contents are disposed and hosts return to closed/null state.

## Missing / empty / error / loading state result

Reusable controls remain:
- EmptyState,
- LoadingState,
- InlineError.

Operational list screens consuming EmptyState:
- Customers,
- Suppliers,
- Expenses,
- Purchase History,
- Inventory.

Synchronous local/demo operations do not show artificial loading states where there is no asynchronous wait. LoadingState remains the shared primitive for future backend/async attachment. Inline validation/error messaging continues to be used by transactional forms and dialogs.

## Final static forensic result

- canonical primary screens present: 17/17.
- missing primary screens: 0.
- accidental tool-preview garbage: 0.
- unresolved TODO/FIXME/NotImplemented markers: 0.
- XAML XML parse errors: 0.
- Canvas usage: 0.
- web/React runtime residue: 0.
- View-level Color.Light.* theme leaks: 0.
- direct Desktop Npgsql/DbContext/Dapper/ConnectionString/HttpClient coupling: 0.
- Switch User lifecycle contract: verified.
- Navigation reset contract: verified.
- Page-cache reset contract: verified.
- Dialog disposal contract: verified.
- Drawer disposal contract: verified.
- operational EmptyState consumers checked: 5.
- result: `PHASE3_FINAL_STATIC_AUDIT=PASS`.

## Automated regression suite

Phase 3 added permanent forensic tests covering:
- Shell Switch User command and top-bar action.
- MainViewModel Switch User cleanup.
- NavigationService reset.
- PageViewModelFactory cache clearing.
- Dialog/Drawer disposal behavior.
- Reporting aggregation dependencies.
- Customer/Supplier metric event wiring.
- inventory movement kinds and stock-truth audit availability.
- reusable EmptyState usage.
- tool-preview source corruption prevention.
- supporting actions remaining overlays.
- canonical 17 primary screen contract.

Current workspace unit count at final re-verification:
- Debug: 224/224 PASS.
- Release: 224/224 PASS.

The increase from the original Sprint 6 count is caused by later Sprint 7/8 tests now coexisting in the same solution.

## Final Phase 3 gate

- Sprint 6 touched-scope formatter verify-no-changes: PASS.
- Full solution Debug build: PASS, 0 warnings, 0 errors.
- Debug unit tests: 224/224 PASS.
- Debug DB-independent architecture integration: 1/1 PASS.
- Debug runtime journey smoke: PASS.
- Full solution Release build: PASS, 0 warnings, 0 errors.
- Release unit tests: 224/224 PASS.
- Release DB-independent architecture integration: 1/1 PASS.
- Release runtime journey smoke: PASS.
- Final static forensic audit: PASS, 0 findings.

## Current workspace coexistence

Later Sprint 7/8 implementation is now present in the same workspace. During final Sprint 6 re-verification this exposed two current Desktop printing compile errors and one later-sprint safety-test contract mismatch.

Minimal closure-hygiene corrections were made without changing Sprint 6 business behavior:
- WPF preview owner now uses `System.Windows.Application.Current`.
- Print ticket validation uses the actual `PrintTicketScope.JobScope` enum member.
- Sprint 8 maintenance-barrier test now asserts the intended `ProductionMaintenanceException` and its `RestoreCutover` state.

Global repository formatting still contains later-sprint whitespace/EOL debt outside Sprint 6. Sprint 6 touched files pass formatter verification.

## Closure

**Sprint 6 Phase 3 is CLOSED.**

This also closes Sprint 6 at frontend automated/forensic level.
