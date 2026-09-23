# Edge Retails — Sprint 6 Master Implementation Plan

**Date:** 2026-09-20
**Status:** SPRINT 6 CLOSED — ALL 3 PHASES COMPLETE
**Scope:** Frontend completion hardening after the 17-screen implementation
**Backend attachment:** DEFERRED and developed separately
**Manual visual QA:** deferred by product owner
**Git:** untouched

## Purpose

Sprint 6 does not add a Screen 18. It hardens the completed 17-screen WPF frontend so setup, authorization, global states, responsive desktop behavior, themes and cross-screen operational journeys are safe before the separately designed backend is attached.

## Phase 1 — Setup, authorization and global-state foundation

- First Setup / License three-step flow.
- Setup -> Login lifecycle.
- PBKDF2 demo identity credentials with no plaintext persisted PIN.
- Owner PIN transient-state safety.
- Role-based frontend navigation.
- Owner full access, Manager configured subset, Cashier limited subset.
- Unknown / malformed roles fail closed.
- Permission Required dialog.
- Reusable confirmation dialog.
- Empty, Loading and Inline Error state controls.
- Debug-only setup preview gate.
- NavigationService as authoritative authorization boundary.
- Event subscription/lifetime hardening.
- Sales-flow corrections requested during Phase 1:
  - Normal Sale <-> Thaka mode switch is non-destructive.
  - no Thaka switch confirmation dialog.
  - real Walk-in / saved customer selection.
  - recipient identity carried into checkout.
  - Cash / Bank / Other checkout bodies differentiated.
  - Edge logo and application icon integrated.

**Status:** CLOSED at frontend automated/forensic level.

## Phase 2 — Responsive desktop + theme hardening

- Audit all 17 primary screens at the canonical 1440x900 target and minimum 1366x768 contract.
- Remove layout assumptions that can clip critical actions at minimum size.
- Prefer Grid/Star/Auto/ScrollViewer behavior over brittle absolute dimensions.
- Audit dialogs/drawers for viewport-safe maximum sizing and scrolling.
- Verify expanded 232 / collapsed 72 sidebar behavior does not starve content.
- Audit hard-coded light-only colors and convert UI surfaces to theme-aware resources where appropriate.
- Validate Light / Dark / System theme propagation through all primary screens and supporting overlays.
- Preserve Figma hierarchy and density without inventing a mobile/tablet layout.
- Add automated responsive/theme forensic tests.
- No backend attachment.

**Status:** CLOSED at frontend automated/forensic level.

## Phase 3 — Final frontend journey hardening + Sprint 6 closure

- Cross-screen operational journey audit from Login through every primary module.
- Verify overlays/drawers/dialogs return cleanly to their owning screen.
- Verify permissions cannot be bypassed by internal navigation.
- Verify shared demo-state mutations remain coherent across Sales, Thaka, Purchases, Inventory, Expenses, Customers, Suppliers and Reports.
- Recheck missing/empty/error/loading states.
- Final static forensic scan.
- Formatter verification.
- Debug/Release frontend matrix.
- DB-independent architecture integration gate.
- Produce Sprint 6 forensic closure report.
- Backend attachment remains a later integration stage.

**Status:** CLOSED at frontend automated/forensic level.

## Phase 1 authoritative gate

- Formatter verify-no-changes: PASS for Desktop and UnitTests.
- Debug Desktop build: PASS, 0 warnings, 0 errors.
- Debug unit tests: 107/107 PASS.
- Debug DB-independent architecture integration: 1/1 PASS.
- Release Desktop build: PASS, 0 warnings, 0 errors.
- Release unit tests: 107/107 PASS.
- Release DB-independent architecture integration: 1/1 PASS.
- Phase 1 runtime smoke: PASS.
- Sales-flow runtime smoke: PASS.
- Refined static forensic audit: PASS.
- Manual visual QA: deferred.
- Git: untouched.

## Phase 2 authoritative gate

- 17/17 primary screens audited against 1440x900 target and 1366x768 minimum.
- Primary fixed-width overflow findings at 1366 minimum: 0.
- All 17 primary screens have contained scrolling: verified.
- ModalHost viewport fallback scrolling: implemented and runtime verified.
- View-level Color.Light.* leaks: 0.
- Login theme surfaces migrated to semantic Light/Dark resources.
- Dashboard light-only theme debt reduced to intentional semantic/shadow accents.
- New Sale mode surfaces and borders use semantic theme resources.
- System theme follows Windows preference changes while System mode is active.
- Theme dictionary swaps use assembly-qualified pack URIs.
- Phase 2 static forensic audit: PASS, 0 findings.
- Formatter verify-no-changes: PASS for Desktop and UnitTests.
- Debug Desktop build: PASS, 0 warnings, 0 errors.
- Debug unit tests: 116/116 PASS.
- Debug DB-independent architecture integration: 1/1 PASS.
- Debug WPF responsive/theme runtime smoke: PASS.
- Release Desktop build: PASS, 0 warnings, 0 errors.
- Release unit tests: 116/116 PASS.
- Release DB-independent architecture integration: 1/1 PASS.
- Release WPF responsive/theme runtime smoke: PASS.
- Manual visual QA: deferred.
- Git: untouched.

## Phase 3 authoritative gate

- Real Shell -> Login/User Switch flow implemented and runtime verified.
- Switch User closes dialogs/drawers, disposes overlay content, resets navigation and clears cached page ViewModels.
- Cross-module runtime journey verified across Local Sales, Sale Returns, Thaka, Purchases, Purchase Returns, Stock Adjustment, Expenses, Customers, Suppliers, Inventory and Reports.
- Inventory stock-truth audit remained reconciled after every mutation in the runtime journey.
- Reporting deltas matched the actual runtime mutations.
- Customer Local Sales and Supplier Total Purchases matched the underlying sale/return and purchase/return records.
- DialogService and DrawerService replacement/close disposal verified.
- Five operational list surfaces consume the reusable EmptyState control.
- EmptyState, LoadingState and InlineError global controls remain available; synchronous demo screens do not fabricate loading states where no asynchronous operation exists.
- Accidental tool-preview garbage found in CustomersView.xaml, ExpensesView.xaml and SuppliersView.xaml was removed.
- Exactly the canonical 17 primary screens remain.
- Supporting Permission/Confirmation/Customer selection actions remain overlays, not extra primary screens.
- Phase 3 final static forensic audit: PASS, 0 findings.
- Sprint 6 touched-scope formatter verify-no-changes: PASS.
- Current full solution Debug build: PASS, 0 warnings, 0 errors.
- Current Debug unit tests: 224/224 PASS.
- Debug DB-independent architecture integration: 1/1 PASS.
- Debug Phase 3 runtime journey smoke: PASS.
- Current full solution Release build: PASS, 0 warnings, 0 errors.
- Current Release unit tests: 224/224 PASS.
- Release DB-independent architecture integration: 1/1 PASS.
- Release Phase 3 runtime journey smoke: PASS.
- Manual visual QA: deferred.
- Git: untouched.

## Current workspace coexistence note

Later Sprint 7/8 work now exists in the same workspace, so Sprint 6 closure evidence is intentionally scoped to Sprint 6 code/contracts while still checking the current solution build and regression suite.

At the final re-verification checkpoint:
- Full solution Debug build: PASS, 0 warnings, 0 errors.
- Full solution Release build: PASS, 0 warnings, 0 errors.
- Current unit suite: 224/224 PASS in Debug and Release.
- Architecture integration gate: 1/1 PASS in Debug and Release.
- Global repository formatter still sees whitespace/EOL debt in later-sprint files that are outside Sprint 6; Sprint 6 touched-scope formatter passes.
- Later-sprint backend adapter files mean a current-workspace claim of “zero Desktop backend coupling” is no longer an appropriate Sprint 6 closure metric.

## Sprint 6 closure

Sprint 6 is CLOSED at frontend automated/forensic level.

The canonical 17-screen frontend implementation has completed its automated code, responsive/theme, authorization, lifecycle and cross-module journey hardening.

Still deferred by product decision:
- manual side-by-side visual QA against Figma,
- backend/database attachment,
- final backend-driven licensing/setup persistence,
- Git cleanup/commit/freeze.
