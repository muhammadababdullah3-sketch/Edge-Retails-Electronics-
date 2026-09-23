# Sprint 6 Phase 1 — Forensic Implementation Report

**Date:** 2026-09-20
**Status:** CLOSED — FRONTEND AUTOMATED / FORENSIC LEVEL
**Manual visual QA:** deferred
**Backend attachment:** deferred
**Git:** untouched

## Delivered

Phase 1 now provides a complete First Setup / License frontend lifecycle, shared demo identity, permission UX, confirmation UX and global missing-state foundation.

The phase also absorbed the requested Sales Terminal corrections because they affect the same pre-backend frontend completion contract:
- Thaka mode switching no longer clears a prepared sale cart.
- Thaka mode switching no longer asks for a destructive confirmation.
- Change Customer is a real searchable customer picker.
- Walk-in Customer is an explicit selectable state.
- saved customer name and phone flow into checkout.
- checkout recipient resolves to the actual selected customer or Walk-in Customer.
- Cash, Bank Transfer and Other use distinct payment-detail bodies.
- Bank/Other payment references are retained in the demo transaction record.
- uploaded Edge brand mark is used in Login, First Setup and sidebar branding.
- Windows application icon is configured.

## Forensic findings resolved

### S6-P1-F01 — Unknown roles inherited Cashier permissions
**Severity:** High

The original permission service treated every non-Owner/non-Manager role as Cashier.

**Resolution:** Only explicit Cashier receives Cashier routes. Blank, malformed and unknown roles fail closed.

### S6-P1-F02 — Internal navigation could bypass Shell authorization
**Severity:** High

The Shell checked permissions, while Dashboard/nested ViewModels could call INavigationService directly.

**Resolution:** NavigationService itself now checks the active session role before page creation. Denied routes do not instantiate the protected page and surface Permission Required.

### S6-P1-F03 — Setup PIN could survive Back navigation
**Severity:** High

A transient Owner PIN entered on Shop Setup could remain in the ViewModel/PasswordBox after returning to License.

**Resolution:** leaving Shop Setup backwards clears both transient ViewModel PIN state and PasswordBox UI. Successful setup does the same.

### S6-P1-F04 — Successful login retained transient PIN
**Severity:** High

LoginViewModel retained the successful four-digit PIN after creating the session.

**Resolution:** successful authentication clears PIN before success callbacks are invoked.

### S6-P1-F05 — Shared-state event subscriptions had incomplete lifetimes
**Severity:** Medium

Repeated Inventory / Purchase History navigation and repeated Product Detail opening could accumulate subscriptions.

**Resolution:**
- Inventory and Purchase History are cached by the page factory.
- Product Detail is disposed on close/replacement.
- subscriber ViewModels implement IDisposable and unsubscribe.
- PageViewModelFactory owns and disposes cached subscriber lifetimes.
- MainViewModel disposes the page factory at shutdown.

## Security / boundary result

- PBKDF2 SHA-256 demo credential derivation retained.
- random salt used when configuring Owner.
- FixedTimeEquals used during verification.
- no plaintext persisted PIN credential introduced.
- setup preview switch remains DEBUG-only.
- direct Npgsql/DbContext/Dapper/ConnectionString/HttpClient coupling in Desktop: 0.
- backend remains separately designed.

## Static forensic result

- unresolved TODO/FIXME/NotImplemented markers: 0.
- Canvas usage: 0.
- web/React runtime files: 0.
- XAML XML errors: 0.
- missing custom resource keys: 0.
- plaintext PIN exposure risks: 0.
- subscriber lifetime issues in audited shared-state ViewModels: 0.
- authoritative navigation guard occurs before page creation: verified.
- unknown roles fail closed: verified.
- setup PIN clear hook: verified.
- login PIN clears before callback: verified.

## Runtime smokes

Sales flow:
- prepared cart survives Normal Sale -> Thaka -> Normal Sale.
- saved customer flows into checkout recipient.
- Walk-in reset works.
- Bank reference persists.
- Other reference persists.
- result: SALES_FLOW_RUNTIME_SMOKE=PASS.

Phase 1 security/lifecycle:
- unknown/blank roles denied.
- direct navigation guard blocks protected page creation.
- Cashier New Sale allowed.
- setup PIN does not survive Back.
- configured Owner PBKDF2 PIN verifies.
- successful login clears transient PIN before callback.
- result: SPRINT6_PHASE1_RUNTIME_SMOKE=PASS.

## Final frontend gate

- Desktop formatter verify-no-changes: PASS.
- UnitTests formatter verify-no-changes: PASS.
- Debug Desktop build with frozen dependencies: PASS, 0 warnings, 0 errors.
- Debug unit tests: 107/107 PASS.
- Debug DB-independent architecture integration: 1/1 PASS.
- Release Desktop build with frozen dependencies: PASS, 0 warnings, 0 errors.
- Release unit tests: 107/107 PASS.
- Release DB-independent architecture integration: 1/1 PASS.

## Separate backend workstream

A current full solution build is independently blocked by two backend implementation-contract mismatches in Infrastructure. Sprint 6 did not modify or attempt to repair them because backend development is explicitly separate until frontend completion.

## Closure

Sprint 6 Phase 1 is CLOSED at frontend automated/forensic level.

Next active phase: **Phase 2 — Responsive Desktop + Theme Hardening**.
