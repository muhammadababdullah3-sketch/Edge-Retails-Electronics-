# Sprint 6 Phase 2 — Responsive Desktop & Theme Forensic Report

**Date:** 2026-09-20
**Status:** CLOSED — FRONTEND AUTOMATED / FORENSIC LEVEL
**Manual visual QA:** deferred
**Backend attachment:** deferred
**Git:** untouched

## Scope

Phase 2 hardens the completed 17-screen WPF frontend for the canonical 1440x900 desktop target, minimum 1366x768 window contract, modal/drawer viewport safety, and Light/Dark/System theme correctness.

No new primary screen was introduced.

## Delivered

### Responsive desktop contract

- MainWindow remains 1440x900 with enforced minimum 1366x768.
- All 17 primary screens were audited.
- All 17 primary screens have contained scrolling.
- No primary screen hard-forces a numeric width at or above the 1366px minimum viewport.
- Shell keeps an Auto sidebar column plus Star content column.
- Sidebar width remains binding-driven and supports expanded/collapsed behavior.
- ModalHost now wraps modal content in a viewport ScrollViewer with automatic horizontal/vertical fallback and 24px safe margin.
- Dialog audit found no dialog Width or MinWidth at or above the application minimum width.

### Theme hardening

- Added theme-specific Login ambient background and glow resources to Light.xaml and Dark.xaml.
- Login surface, text, border, hover and selection states migrated from light-only hardcodes to semantic resources where appropriate.
- Intentional brand/danger shadows and white-on-brand content remain static by design.
- Dashboard light-only surface/text/border values were migrated to semantic theme resources.
- Dashboard command banner now uses theme-aware Gradient.Surface.Card.
- Thaka and Low Stock dashboard headers use semantic brand/warning theme resources.
- New Sale Normal/Thaka mode subheaders, selection surfaces, cart borders and disabled state use semantic resources.
- Removed obsolete New Sale local theme aliases:
  - Brush.Cart.BorderNormal
  - Brush.Cart.BorderThaka
  - Brush.Thaka.Purple
- Complete Sale no longer declares unused Color.Light.* local aliases.
- View-level Color.Light.* leaks: 0.

### System theme behavior

The previous System theme implementation resolved Windows Light/Dark only when ApplyTheme(System) was called.

Resolution:
- ThemeService subscribes to Microsoft.Win32.SystemEvents.UserPreferenceChanged.
- While CurrentTheme is System, Windows preference changes trigger a re-resolution.
- ThemeService implements IDisposable and unsubscribes from the static SystemEvents publisher.
- App owns and disposes ThemeService on shutdown.

### Theme dictionary URI robustness

Runtime WPF smoke exposed that relative runtime theme URIs were host-dependent.

Resolution:
- runtime theme replacement now uses assembly-qualified pack URIs:
  pack://application:,,,/EdgeRetails.Desktop;component/Resources/Themes/...
- this works both in the EdgeRetails executable and when the Desktop assembly is hosted by an external WPF harness.

## Forensic findings resolved

### S6-P2-F01 — Modal content could exceed viewport
**Severity:** High

ModalHost centered content without a viewport fallback. Large dialogs could be clipped at the minimum window size.

**Resolution:** ModalHost now supplies automatic horizontal/vertical scrolling with a 24px viewport margin.

### S6-P2-F02 — Login contained light-only visual surfaces
**Severity:** Medium

Login contained hard-coded light backgrounds, borders, text and ambient gradients.

**Resolution:** light-sensitive values were moved to semantic theme resources and dedicated Light/Dark login gradients.

### S6-P2-F03 — Dashboard contained concentrated light-only theme debt
**Severity:** Medium

Dashboard contained multiple hard-coded light surfaces, borders, text and selection states.

**Resolution:** semantic theme resources now drive the affected surfaces, while intentional functional accent colors remain.

### S6-P2-F04 — New Sale mode surfaces were partially light-only
**Severity:** Medium

Normal/Thaka mode surfaces and cart borders contained local light-oriented values.

**Resolution:** semantic theme resources now drive those states and obsolete local aliases were removed.

### S6-P2-F05 — System theme did not live-follow Windows
**Severity:** Medium

System mode resolved the OS preference only at ApplyTheme time.

**Resolution:** Windows preference changes now re-resolve the active System theme.

### S6-P2-F06 — Runtime theme URI was host-dependent
**Severity:** Medium

Relative ResourceDictionary URI replacement failed when EdgeRetails.Desktop was hosted by an external WPF application.

**Resolution:** assembly-qualified pack URIs are now used.

## Static forensic result

- primary screens audited: 17.
- Phase 2 static issues: 0.
- primary screens without contained scrolling: 0.
- primary fixed-width overflow >= 1366: 0.
- dialogs forcing Width/MinWidth >= 1366: 0.
- view-level Color.Light.* leaks: 0.
- Canvas usage: 0.
- frontend web/React runtime residue: 0.
- XAML XML parse errors: 0.
- dead New Sale theme aliases: 0.
- result: PHASE2_FINAL_STATIC_AUDIT=PASS.

## Automated regression suite

Sprint 6 Phase 2 added forensic tests for:
- canonical MainWindow desktop dimensions.
- contained scrolling on all 17 primary screens.
- fixed-width overflow prevention.
- ModalHost viewport fallback.
- adaptive Shell/sidebar structure.
- zero view-level Color.Light.* references.
- Windows System-theme live-follow contract.
- ThemeService disposal.
- assembly-qualified runtime theme dictionary URI.
- New Sale semantic mode resources.
- Login/Dashboard theme-aware surfaces.

Current unit total:
- Debug: 116/116 PASS.
- Release: 116/116 PASS.

## WPF runtime smoke

The Phase 2 WPF harness loads the real EdgeRetails App resources and Desktop views.

Verified:
- Dark theme applies and resolves a dark application background.
- Light theme applies and resolves a different application background.
- System theme remains selected and resolves to Light or Dark.
- Login measures within 1366x768.
- Dashboard measures within the minimum shell-content viewport.
- New Sale measures within the minimum shell-content viewport.
- ModalHost exposes Auto horizontal and vertical scrolling in the real visual tree.

Debug result:
SPRINT6_PHASE2_RUNTIME_SMOKE=PASS

Release result:
SPRINT6_PHASE2_RUNTIME_SMOKE=PASS

## Final frontend gate

- Desktop formatter verify-no-changes: PASS.
- UnitTests formatter verify-no-changes: PASS.
- Debug Desktop build: PASS, 0 warnings, 0 errors.
- Debug unit tests: 116/116 PASS.
- Debug DB-independent architecture integration: 1/1 PASS.
- Debug WPF responsive/theme runtime smoke: PASS.
- Release Desktop build: PASS, 0 warnings, 0 errors.
- Release unit tests: 116/116 PASS.
- Release DB-independent architecture integration: 1/1 PASS.
- Release WPF responsive/theme runtime smoke: PASS.
- Phase 2 final static forensic audit: PASS, 0 findings.

## Full solution status at closure

- Full solution Debug build: PASS, 0 warnings, 0 errors.
- Full solution Release build is independently blocked by an Application-layer compile error in VoidPurchaseHandler.cs where local variable `cashSession` is used before declaration.
- Sprint 6 did not modify that backend/Application flow.
- Backend attachment remains deferred until frontend completion.

## Closure

Sprint 6 Phase 2 is CLOSED at frontend automated/forensic level.

Next phase is **Sprint 6 Phase 3 — Final Frontend Journey Hardening + Sprint 6 Closure**.

Phase 3 is READY but intentionally not partially started in this checkpoint.
