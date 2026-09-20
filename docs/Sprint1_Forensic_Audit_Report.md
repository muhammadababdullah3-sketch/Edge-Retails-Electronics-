# Edge Retails Sprint 1 — Forensic Audit & Remediation Report

**Scope:** WPF Sprint 1 foundation only  
**Status after remediation:** **STATIC FORENSIC PASS**  
**Runtime/build status:** Pending target Windows device reconnect

## Transparency note

The temporary offline sandbox retained the Sprint 1 manifest but not the earlier generated staging files. I therefore rebuilt the Sprint 1 staging package from the code prepared in this chat, then performed the forensic audit on that rebuilt package. The real Windows repository was not touched while the device was offline.

## What was audited

The scan covered the Sprint 1 design-system resources, themes, shell, sidebar, top bar, navigation, MVVM primitives, search/KPI/badge controls, DataGrid foundation, modal/drawer/toast hosts, loading/empty states, theme service, live clock, session abstraction, and application composition root.

The audit checked:
- XAML XML validity;
- WPF-specific invalid/suspicious properties;
- `x:Class` and code-behind pairing;
- merged ResourceDictionary source existence;
- StaticResource/DynamicResource key integrity;
- ResourceDictionary load ordering;
- light/dark semantic theme parity;
- duplicated resource keys;
- DataTemplate namescope binding hazards;
- hard-coded colors outside token/theme dictionaries;
- navigation route/icon parity;
- code-behind event-handler leakage;
- Canvas / React / Tailwind contamination;
- accidental database/business logic in the Desktop foundation;
- minimum desktop resolution;
- Figma-derived shell/token invariants.

## Issues found in the prepared Sprint 1 code and fixes applied

### BLOCKER-01 — Unsupported WPF `TextBlock.CharacterSpacing`
The earlier sidebar title used `CharacterSpacing`, which belongs to WinUI/UWP rather than classic WPF `TextBlock`. With warnings/errors locked down, this would block XAML compilation.

**Fix:** removed the property. Typography now uses WPF-native font weight/size rendering only.

**Status:** Fixed.

### BLOCKER-02 — Invalid `Grid.Padding`
The earlier collapsed/expanded sidebar footer used `Padding` directly on `Grid`. WPF `Grid` does not expose a `Padding` property.

**Fix:** replaced it with a valid layout approach using `Margin` inside the footer surface.

**Status:** Fixed.

### HIGH-01 — DataTemplate namescope binding risk in sidebar navigation
The earlier navigation item template used `ElementName=Root` to reach the outer `UserControl`. `DataTemplate` introduces a separate namescope, so those bindings can fail at runtime even when the XAML parses.

**Fix:** navigation command and collapsed-state bindings now use `RelativeSource AncestorType=UserControl`, which crosses the template boundary safely.

**Status:** Fixed.

### HIGH-02 — Minimum window size did not match the locked desktop target
The earlier window allowed `1180×700`, while the project contract requires **1366×768 minimum** and **1440×900 primary**.

**Fix:** `MainWindow` now enforces `MinWidth=1366`, `MinHeight=768`, with `1440×900` default.

**Status:** Fixed.

### HIGH-03 — Sidebar collapse state existed in code but had no invocation path
`ToggleSidebarCommand` existed, but nothing could execute it.

**Fix:** added a non-invasive `Ctrl+B` Window input binding. This preserves the expanded Figma visual while making the 232px ↔ 72px shell state functional. A visible collapse affordance can be designed later if the Figma source adds one.

**Status:** Fixed.

### MEDIUM-01 — Collapsed sidebar footer could overflow
The original footer retained expanded-state fixed columns after the sidebar shrank to 72px.

**Fix:** split the footer into explicit expanded and collapsed layouts. The collapsed state renders a centered 28px avatar without hidden fixed-column debt.

**Status:** Fixed.

### MEDIUM-02 — Online state was modeled but not respected visually
`ISessionContext.IsOnline` existed, but green status dots were always visible.

**Fix:** top-bar and sidebar status indicators now bind to `IsOnline`.

**Status:** Fixed.

### MEDIUM-03 — Theme service was implemented but not composed
The earlier `ThemeService` existed but was not instantiated or made available to the shell.

**Fix:** it is now composed in `App.OnStartup`, applied deterministically to Light at startup, and exposed through `ShellViewModel` for later Appearance settings.

**Status:** Fixed.

### MEDIUM-04 — KPI semantic tints leaked as raw hex values inside controls
Several KPI icon wells/backgrounds used hard-coded visual colors, weakening dark-mode correctness and violating the no-magic-color rule.

**Fix:** moved KPI tint/background values into Light/Dark semantic theme dictionaries. `KpiCard` now consumes theme resources only.

**Status:** Fixed.

### MEDIUM-05 — Toast collection could grow without an upper UI bound
Repeated notifications could accumulate until their delay completed.

**Fix:** the toast service now caps visible messages at four and evicts the oldest before adding another.

**Status:** Fixed.

### LOW-01 — Compiler-version-sensitive collection expressions
The previous code used collection expressions in a few infrastructure locations. .NET 10 supports modern C#, but foundation code benefits from conservative syntax when the project is configured with warnings-as-errors.

**Fix:** changed critical initialization sites to explicit `List<T>` / `new()` forms.

**Status:** Fixed.

### VISUAL-01 — Settings navigation icon fidelity
The forensic pass re-read the current Figma Settings icon (`14:270`) and replaced the temporary approximation with the exact vector path and exact Figma offset. The brand mark was also re-verified against Figma: outer hex path + 4px center dot match the source.

**Status:** Fixed from current Figma source.

## Static verification after fixes

- **30 XAML files** parsed successfully.
- **43 C# files** passed structural brace/namespace checks.
- **190 resource keys** discovered.
- **335 resource references** checked.
- Missing resource references: **0**
- Missing merged dictionaries: **0**
- `x:Class` / code-behind pairing errors: **0**
- Known invalid WPF patterns: **0**
- Magic hex colors outside approved token/theme files: **0**
- DataTemplate outer-namescope `ElementName=Root` hazards: **0**
- Duplicate resource keys within any dictionary: **0**
- StaticResource load-order violations: **0**
- Light theme semantic keys: **43**
- Dark theme semantic keys: **43**
- Theme parity differences: **0**
- Navigation icons declared: **11**
- Navigation icons referenced: **11**
- Navigation parity errors: **0**
- `Canvas` usage: **0**
- React/Tailwind residue: **0**
- Database dependencies in Sprint 1 Desktop foundation: **0**
- Sale/stock/business transaction logic leaking into Sprint 1: **0**

## Figma contract re-verified

The repaired foundation preserves the current implementation contract:
- brand `#635BFF`;
- app background `#F6F9FC`;
- primary text `#0A2540`;
- border `#E6EBF1`;
- sidebar expanded `232`;
- sidebar collapsed `72`;
- top bar `56`;
- standard control `35`;
- large search `44`;
- drawer `480`;
- Plus Jakarta Sans primary;
- Space Grotesk KPI/numeric emphasis;
- exact current navigation vector geometry for the audited shell icons.

## Remaining verification, not a known code defect

The package is **not marked Sprint 1 complete/frozen yet** because the offline sandbox has no .NET/WPF compiler and the target Windows device is offline.

When the device reconnects the mandatory second run is:
1. inspect Git working tree;
2. merge these files into the real Desktop project;
3. `dotnet format --verify-no-changes`;
4. Debug build with warnings-as-errors;
5. tests;
6. Release build;
7. launch WPF;
8. compare shell and primitives against Figma screenshots;
9. fix any compiler/runtime/rendering/DPI differences;
10. manual user review;
11. freeze Sprint 1 only after that pass.

**Current verdict:** code-level/static foundation is clean enough to proceed to target-device integration, but it is not yet runtime-verified.
