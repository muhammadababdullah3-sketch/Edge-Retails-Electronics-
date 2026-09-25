# Edge Retails Point of Sale
## Complete Frontend Forensic UI / UX / Visual Quality Audit

**Audit date:** 2026-09-24  
**Repository:** `muhammadababdullah3-sketch/Edge-Retails-Electronics-`  
**Branch:** `main`  
**Audited commit:** `8dd615b39aa1315393d57c9b94c5693d0ee12470`  
**Frontend project:** `src/EdgeRetails.Desktop`  
**Technology:** WPF / .NET 10  
**Git state during audit:** READ-ONLY / untouched  
**Runtime visual verification:** `BLOCKED_ENVIRONMENT`  
**Static source forensic audit:** `COMPLETE`

---

# Executive Summary

The Edge Retails desktop frontend already has a substantial shared WPF design system: centralized colors, brushes, gradients, spacing, radii, typography, buttons, inputs, tables, cards, tabs, badges, tooltips, scrollbars, reusable status/empty/loading/error controls, a shared shell, navigation controls, drawer host, modal host and toast host.

The main problem is not the absence of a design system. The main problem is **incomplete enforcement of that design system**. Many screens use the shared styles correctly, while several later or workflow-heavy screens fall back to raw WPF controls, one-off dimensions, local styles and hard-coded values. That creates visual drift, keyboard inconsistencies, dark-theme risk, table-alignment inconsistencies and screen-specific layout defects.

No source evidence supports a claim that the global `Button.Base` template itself vertically mis-centers text. Its `ContentPresenter` uses template-bound horizontal/vertical content alignment and the base style centers both axes. The user's reported “button text slightly above/below center” class of issue therefore **cannot be conclusively validated from static XAML alone**. It requires rendered runtime inspection at real DPI/font conditions. However, several local button families bypass `Button.Base`, suppress the standard focus visual, and do not replace it with a keyboard focus state. Those are verified defects.

The highest-impact verified issues are:

1. **Supplier Detail has a structural width contradiction:** `DrawerHost` is fixed at 480 DIPs, while `SupplierDetailView` declares `MinWidth="760"`. `SuppliersViewModel` explicitly opens Supplier Detail through the drawer service. The child therefore requires at least 280 DIPs more width than its host.
2. **Warranty's main table is structurally wider than its available pane:** fixed columns alone total 1,090 DIPs before the star-sized Product column, while the left pane is roughly 767 DIPs at the minimum window with expanded navigation and roughly 819 DIPs at the default 1440 width. Horizontal scrolling/overflow is unavoidable.
3. **Generic modal keyboard/focus behavior is incomplete:** the modal host and dialog service do not implement generic focus capture, focus trap, focus restoration, Escape or default/cancel semantics. Among the 24 overlay dialog XAML files inspected, none declares `IsDefault="True"` or `IsCancel="True"`. Only `CompleteSaleDialog` adds explicit initial focus plus Enter/Escape handling in code-behind.
4. **The application minimum window size is DPI-hostile:** `MinWidth="1366"` and `MinHeight="768"` are WPF device-independent units. At 125% scaling, that minimum corresponds to approximately 1708 x 960 physical pixels; at 150% it corresponds to approximately 2049 x 1152 physical pixels. This is incompatible with many common scaled laptop displays.
5. **The visual system is opt-in rather than enforced:** shared TextBox/ComboBox/DataGrid/Tab styles are keyed, and there are no complete shared visual families for DatePicker, CheckBox, RadioButton and PasswordBox. Multiple production screens therefore render native/default controls beside custom themed controls.
6. **Keyboard focus disappears on multiple local button families:** Dashboard, POS, Thaka Projects and Login define local button styles that set `FocusVisualStyle="{x:Null}"` but provide hover/pressed states without a replacement keyboard-focus trigger.

The audit found **0 CRITICAL**, **6 HIGH**, **10 MEDIUM**, and **2 LOW** deduplicated root-cause findings. Runtime rendering remains the only major evidence gap.

---

# Audit Scope

This audit covers the existing Edge Retails Point of Sale desktop frontend only. It does not cover the future Vendor Admin Portal and does not redesign backend/database/domain architecture.

Inspected areas include:

- WPF project structure and project configuration
- application resource merge order
- themes and theme switching
- spacing/dimension tokens
- typography resources
- color/contrast resources
- gradients and shadows
- button/input/table/card/tab/badge/tooltip/scrollbar resources
- reusable controls
- shell, sidebar and top bar
- navigation targets and route-to-ViewModel mapping
- routed screens
- internal detail screens
- dialogs and overlay infrastructure
- production print preview window
- static resizing/DPI constraints
- keyboard/focus behavior visible in source
- cross-screen spacing and control-dimension comparisons
- DataGrid numeric alignment
- loading/empty/error presentation consistency
- hard-coded visual values
- encoding/text corruption

No source files, Git refs, commits, working files or backend logic were modified.

---

# Repository / Branch Audited

Repository identity was confirmed as:

`muhammadababdullah3-sketch/Edge-Retails-Electronics-`

The repository had only one branch in the audited state:

`main`

Audited HEAD:

`8dd615b39aa1315393d57c9b94c5693d0ee12470`

Commit title:

`fix: complete runtime and deployment closure patch`

The frontend is located at:

`src/EdgeRetails.Desktop`

---

# Frontend Architecture Map

## Shell / host

- `MainWindow.xaml`
- `Views/ShellView.xaml`
- `Controls/AppSidebar.xaml`
- `Controls/AppTopBar.xaml`
- `Controls/DrawerHost.xaml`
- `Controls/ModalHost.xaml`
- `Controls/ToastHost.xaml`

## Navigation

- `Navigation/NavigationTarget.cs`
- `Navigation/NavigationService.cs`
- `Navigation/PageViewModelFactory.cs`
- `ViewModels/ShellViewModel.cs`

Top-level production navigation targets:

- Dashboard
- POS
- Sales History
- Thaka / Projects
- Purchases
- Product Management
- Inventory
- Expenses
- Customers
- Suppliers
- Warranty
- Reports
- Settings

`ThakaWorkspace` is an internal navigation target. Detail views and workflow dialogs are entered from their parent screens.

## Shared resource system

- `Resources/Colors.xaml`
- `Resources/Brushes.xaml`
- `Resources/Gradients.xaml`
- `Resources/Spacing.xaml`
- `Resources/Radii.xaml`
- `Resources/Shadows.xaml`
- `Resources/Typography.xaml`
- `Resources/NavigationIcons.xaml`
- `Resources/Themes/Light.xaml`
- `Resources/Themes/Dark.xaml`
- `Resources/Buttons.xaml`
- `Resources/Inputs.xaml`
- `Resources/Tables.xaml`
- `Resources/Cards.xaml`
- `Resources/Tabs.xaml`
- `Resources/Badges.xaml`
- `Resources/Tooltips.xaml`
- `Resources/ScrollBars.xaml`

## Reusable controls

- SearchBox
- KpiCard
- StatusBadge
- EmptyState
- LoadingState
- InlineError
- AppSidebar
- AppTopBar
- DrawerHost
- ModalHost
- ToastHost
- FinancialTrendChart

This architecture is a good foundation. The primary systemic defect is that much of it is keyed and optional, so individual screens can silently bypass it.

---

# Complete Screen Inventory

| Screen / Surface | View | Entry mechanism | Audit status |
|---|---|---|---|
| First Setup | `Views/FirstSetupView.xaml` | pre-login/root content | Minor findings |
| Login | `Views/LoginView.xaml` | pre-shell/root content | Needs correction |
| Dashboard | `Views/DashboardView.xaml` | top-level navigation | Needs correction |
| POS | `Views/PosView.xaml` | top-level navigation | Needs correction |
| Sales History | `Views/SalesHistoryView.xaml` | top-level navigation | Minor findings |
| Sale Detail | `Views/SaleDetailView.xaml` | internal detail flow | Minor findings |
| Thaka Projects | `Views/ThakaProjectsView.xaml` | top-level navigation | Needs correction |
| Thaka Workspace | `Views/ThakaWorkspaceView.xaml` | internal navigation | Needs correction |
| Purchase History | `Views/PurchaseHistoryView.xaml` | top-level Purchases | Needs correction |
| New Purchase | `Views/NewPurchaseView.xaml` | internal purchase flow | Needs correction |
| Purchase Detail | `Views/PurchaseDetailView.xaml` | internal detail flow | Minor findings |
| Product Management | `Views/ProductManagementView.xaml` | top-level navigation | Needs correction |
| Product Detail | `Views/ProductDetailView.xaml` | internal detail flow | Needs correction |
| Inventory | `Views/InventoryView.xaml` | top-level navigation | Needs correction |
| Expenses | `Views/ExpensesView.xaml` | top-level navigation | Needs correction |
| Customers | `Views/CustomersView.xaml` | top-level navigation | Minor findings |
| Customer Detail | `Views/CustomerDetailView.xaml` | 480-DIP drawer | Minor findings |
| Suppliers | `Views/SuppliersView.xaml` | top-level navigation | Minor findings |
| Supplier Detail / Khata | `Views/SupplierDetailView.xaml` | 480-DIP drawer | **Major findings** |
| Warranty | `Views/WarrantyView.xaml` | top-level navigation | **Major findings** |
| Reports | `Views/ReportsView.xaml` | top-level navigation | Needs correction |
| Settings | `Views/SettingsView.xaml` | top-level navigation | Needs correction |
| Print Preview | `Production/Printing/ProductionPrintPreviewWindow.xaml` | production window | Needs correction |

Debug-only Sprint 1 verification/overlay views were inspected as design-system evidence but are excluded from production quality counts.

---

# Shared Design System Findings

The shared system establishes a clear canonical vocabulary:

- standard control/button height: 35
- compact control height: 28
- compact action button: 31.5
- primary action: 46
- primary purchase action: 48
- large search: 44
- table header: 35
- table row: 38.5
- drawer width: 480
- top bar: 56
- expanded sidebar: 232
- collapsed sidebar: 72
- canonical page padding token: `24,20`
- primary page title token: 24 / SemiBold
- standard body typography: 14
- secondary typography: 13
- caption typography: 11

However, production screens frequently bypass these tokens with local values such as 12, 14, 16, 17.5, 18, 20, 21, 22 and 24 for semantically similar gaps and 30, 32, 34, 35, 36, 38, 42, 44, 46 and 48 for interactive control heights.

Not every difference is wrong. POS legitimately needs denser geometry than a settings form. The defect is that there is no consistently declared semantic compact/standard/dense family for every component, so equivalent usages are hard to distinguish from accidental drift.

---

# Global Root Causes

## Root Cause A: design system is keyed/opt-in

`Input.TextBox`, `Input.ComboBox`, `Table.DataGrid`, `Tabs.Control` and other major styles are keyed. WPF controls that omit `Style=...` therefore fall back to native WPF visuals.

This is the source of many screen-specific differences in Warranty, Supplier Detail, New Purchase, Reports, Inventory, Expenses, Product Management and dialogs.

## Root Cause B: incomplete shared component coverage

No complete shared design-family equivalent was found for:

- DatePicker
- CheckBox
- RadioButton
- PasswordBox

Screens either rely on default WPF rendering or implement local visual properties.

## Root Cause C: late workflow screens are visually less integrated

Warranty and Supplier Detail / Khata contain the strongest concentration of raw WPF controls, raw DataGrids and local values. Their business workflows are extensive, but their presentation integration lags the shared visual system.

## Root Cause D: layout contracts are not validated against host contracts

The clearest example is a 760-DIP minimum-width child inserted into a 480-DIP drawer. The shared host and child layout were developed without a single enforced width contract.

## Root Cause E: runtime accessibility behavior is not centralized

Dialog content is swapped by `DialogService`, but focus lifecycle and keyboard semantics are left to each individual dialog. Most dialogs do not implement them.

---

# Validated Finding Register

## UI-001 — Supplier Detail cannot fit its actual DrawerHost

**Severity:** HIGH  
**Scope:** LOCAL with shared-host dependency  
**Screen:** Supplier Detail / Khata  
**Component:** Drawer layout  
**Files:**
- `src/EdgeRetails.Desktop/Resources/Spacing.xaml`
- `src/EdgeRetails.Desktop/Controls/DrawerHost.xaml`
- `src/EdgeRetails.Desktop/Views/SupplierDetailView.xaml`
- `src/EdgeRetails.Desktop/ViewModels/SuppliersViewModel.cs`

**Observed:**
`Dimension.Drawer.Width` is 480. `DrawerHost` fixes its content surface to that width. `SupplierDetailView` sets `MinWidth="760"`. `SuppliersViewModel.OpenSupplier` opens `SupplierDetailViewModel` through `_drawerService.Show(...)`.

**Evidence:**
The child minimum is 280 DIPs wider than the host before accounting for host borders/padding.

**Expected:**
The Supplier Detail layout must be designed for the drawer width, or the host contract must support the wider workspace.

**Root Cause:**
Host/child width contract mismatch.

**Recommended Fix Direction:**
Choose one canonical interaction model. Either redesign Supplier Detail as a true 480-DIP drawer layout, or introduce a wider/full-detail surface deliberately. Do not simply hide overflow.

**Confidence:** HIGH

---

## UI-002 — Warranty table is structurally wider than its available pane

**Severity:** HIGH  
**Scope:** LOCAL  
**Screen:** Warranty  
**Component:** Main warranty DataGrid / two-column workspace  
**File:** `src/EdgeRetails.Desktop/Views/WarrantyView.xaml`

**Observed:**
The main grid divides the workflow area into `2.4*` for the table and `1*` for the action panel. The table's fixed-width columns total **1,090 DIPs before the star-sized Product column**.

At `MainWindow.MinWidth=1366`, expanded sidebar=232 and Warranty outer margin=48, the remaining workspace is roughly 1,086 DIPs. A 2.4:1 split gives the table pane only about **767 DIPs**. At the default 1440 window width it is only about **819 DIPs**.

**Expected:**
Core warranty identity/status columns should be scannable without mandatory large horizontal scrolling at the minimum/default production window.

**Root Cause:**
A wide fixed-column grid was inserted into a split-pane layout without a width budget.

**Recommended Fix Direction:**
Rework the column budget and/or information architecture. Move lower-priority fields to details, use star/min widths intentionally, or change the workspace layout. Do not solve by shrinking text below readable sizes.

**Confidence:** HIGH

---

## UI-003 — Generic modal focus, Escape and default/cancel contract is missing

**Severity:** HIGH  
**Scope:** GLOBAL / MULTI-SCREEN  
**Component:** ModalHost / DialogService / all overlay dialogs  
**Files:**
- `Controls/ModalHost.xaml`
- `Services/DialogService.cs`
- `Views/Dialogs/*.xaml`

**Observed:**
`ModalHost` is non-focusable and only displays a scrim + scrollable ContentControl. `DialogService` manages content/open state but does not capture previous focus, place focus inside a newly opened dialog, trap focus, restore focus on close, or implement Escape.

Among the 24 overlay dialog XAML files inspected, none declares `IsDefault="True"` or `IsCancel="True"`.

`CompleteSaleDialog` is the one verified exception at code-behind level: it focuses the amount field on load and handles Enter/Escape explicitly.

**Expected:**
Every modal interaction should have a predictable keyboard lifecycle: focus enters modal content, remains within it, Escape/cancel works where allowed, a default action is intentional, and focus returns to the invoking control.

**Root Cause:**
Keyboard/focus behavior is decentralized per dialog instead of implemented by shared modal infrastructure.

**Recommended Fix Direction:**
Implement a shared modal focus contract and allow dialogs to opt into/default action policies rather than duplicating code-behind.

**Confidence:** HIGH

---

## UI-004 — Shared visual system is optional, causing native-control fallback

**Severity:** HIGH  
**Scope:** GLOBAL  
**Component:** TextBox / ComboBox / DataGrid / TabControl and missing control families  
**Files:** `Resources/Inputs.xaml`, `Resources/Tables.xaml`, `Resources/Tabs.xaml`, multiple production views

**Observed:**
Shared styles exist, but they are keyed. Many controls omit a style and therefore render using native WPF visuals.

Verified examples include:

- Inventory: 3 unstyled ComboBoxes
- New Purchase: unstyled TextBoxes, ComboBoxes and DatePicker
- Product Management: 2 unstyled ComboBoxes
- Purchase History: unstyled supplier ComboBox
- Reports: unstyled DatePicker + month/year ComboBoxes
- Supplier Detail: 4 unstyled TextBoxes, 1 ComboBox, native TabControl, 4 raw DataGrids
- Warranty: 16 raw TextBoxes, 1 ComboBox, 2 raw DataGrids
- Expense Edit dialog: raw ComboBoxes and DatePicker
- Product Edit dialog: raw ComboBoxes and CheckBoxes
- Settings Receipt: raw CheckBoxes
- Sales Return: raw RadioButtons

**Expected:**
Equivalent control classes should inherit the intended visual system by default, with explicit variants for exceptions.

**Root Cause:**
Opt-in keyed styles plus incomplete shared families for DatePicker/CheckBox/RadioButton/PasswordBox.

**Recommended Fix Direction:**
Create complete shared control families and introduce safe implicit/base styling or enforce explicit style usage consistently.

**Confidence:** HIGH

---

## UI-005 — Keyboard focus visual disappears on multiple local button families

**Severity:** HIGH  
**Scope:** MULTI-SCREEN  
**Screens:** Dashboard, POS, Thaka Projects, Login  
**Component:** Local custom Button styles

**Observed:**
Several local styles set `FocusVisualStyle="{x:Null}"`, define hover/pressed behavior, but have no `IsKeyboardFocused` or `IsKeyboardFocusWithin` replacement state.

Verified styles:

- Dashboard: `Button.Compact.ThakaNew`
- Dashboard: `Button.CardFooter.Link`
- Dashboard: `Button.ThakaRow`
- POS: `Pos.StepperButton`
- POS: `Pos.RemoveButton`
- Thaka Projects: `Button.FilterTab`
- Thaka Projects: `Button.ViewModeToggle`
- Login: `Style.KeypadButton`

The shared `Button.Base` does provide a keyboard-focus border. These local styles bypass that protection.

**Expected:**
Keyboard users must be able to see which action currently has focus.

**Root Cause:**
Local button templates do not derive from the shared button base and suppress WPF's default focus visual.

**Recommended Fix Direction:**
Derive local variants from a focus-capable shared base or add equivalent focus triggers.

**Confidence:** HIGH

---

## UI-006 — Main window minimum size is incompatible with common DPI-scaled screens

**Severity:** HIGH  
**Scope:** GLOBAL  
**Component:** Main window sizing  
**File:** `src/EdgeRetails.Desktop/MainWindow.xaml`

**Observed:**
`MinWidth="1366"` and `MinHeight="768"` are specified in WPF DIPs.

Approximate physical-pixel requirement:

- 100%: 1366 x 768
- 125%: 1708 x 960
- 150%: 2049 x 1152

A physical 1366x768 laptop running Windows scaling above 100% cannot present the application's declared minimum client window without desktop overflow.

**Expected:**
The UI should remain operable at common Windows DPI/scaling settings.

**Root Cause:**
The minimum size appears to have been chosen from physical-resolution thinking instead of device-independent layout constraints.

**Recommended Fix Direction:**
Define a realistic DIP minimum and make dense screens responsive within it. Validate at 100/125/150/175/200%.

**Confidence:** HIGH for static sizing math; runtime behavior still requires execution.

---

## UI-007 — Light-theme muted/subtle text contrast is weak for small text

**Severity:** MEDIUM  
**Scope:** GLOBAL  
**Component:** Theme typography colors  
**Files:** `Resources/Colors.xaml`, `Resources/Themes/Light.xaml`

**Observed:**
Measured against white:

- `Color.Light.TextMuted #6F8098` ≈ **4.03:1**
- `Color.Light.TextSubtle #8D9CB0` ≈ **2.79:1**

These brushes are used on 10–13px labels, captions, sidebar roles, helper text and status text.

**Expected:**
Small operational text should remain comfortably readable, particularly in dense POS workflows.

**Root Cause:**
Muted/subtle palette was tuned visually but not strongly enough for the smallest production text sizes.

**Recommended Fix Direction:**
Strengthen small-text semantic brushes or reserve the lighter brush for nonessential/larger decorative text.

**Confidence:** HIGH

---

## UI-008 — Encoding corruption is visible in production XAML text

**Severity:** MEDIUM  
**Scope:** MULTI-SCREEN  
**Component:** UI labels / close buttons / loading copy

**Observed:**
Verified mojibake examples include:

- `ProductManagementView.xaml`: `Loading authoritative catalogâ€¦`
- multiple dialogs where the intended close symbol is stored as `âœ•`

Affected verified dialog files include Catalog Reference Manager, Exact Unit Picker, POS Drafts, Price Check, Product Edit, Serialized Purchase Intake, Serialized Stocktake Scan and Stocktake.

Other dialog files correctly use `✕`, confirming this is inconsistent encoding rather than an intentional glyph choice.

**Expected:**
Readable `…` and `✕` characters, or vector/icon resources.

**Root Cause:**
UTF-8 text appears to have been decoded/re-saved through an incompatible encoding path in selected files.

**Recommended Fix Direction:**
Normalize source encoding and replace symbol text with stable Unicode or shared vector icons.

**Confidence:** HIGH

---

## UI-009 — Numeric/currency table alignment is inconsistent

**Severity:** MEDIUM  
**Scope:** MULTI-SCREEN  
**Component:** DataGrid numeric columns  
**Files:** `Resources/Tables.xaml` plus many views

**Observed:**
`Table.Cell` defaults to left alignment. Sale Detail and Sales History explicitly right-align financial columns and use `Font.Numeric`. Many other screens do not.

Verified non-overridden examples include:

- Expenses: AMOUNT
- Inventory: STOCK, MIN STOCK, COST, SALE PRICE, QTY
- New Purchase: QTY, COST, SALE PRICE, TOTAL
- Product Detail: QTY, TOTAL, UNIT PRICE, LINE TOTAL
- Product Management: SALE PRICE, MIN STOCK
- Purchase Detail: QTY, COST, TOTAL
- Purchase History: TOTAL
- Supplier Detail: Effect, Running Balance, Amount
- Thaka Projects: Material Value, Paid, Balance
- Thaka Workspace: Quantity, Rate, Total Value, Amount

**Expected:**
Quantities and monetary values should follow one numeric scanning convention across data-heavy screens.

**Root Cause:**
No shared numeric DataGrid column/cell style is applied consistently.

**Recommended Fix Direction:**
Introduce/reuse a numeric cell style with right alignment and numeric font; apply by semantic column type.

**Confidence:** HIGH

---

## UI-010 — Page-title typography token exists but is not enforced

**Severity:** MEDIUM  
**Scope:** MULTI-SCREEN  
**Component:** Typography hierarchy  
**File:** `Resources/Typography.xaml` plus production views

**Observed:**
`Text.PageTitle` defines 24px / SemiBold. Production screens mostly hand-code titles instead.

Examples:

- Customers: 24 Bold
- Expenses: 24 Bold
- Inventory: 24 Bold
- New Purchase: 24 Bold
- Product Management: 24 Bold
- Purchase History: 24 Bold
- Reports: 24 Bold
- Sales History: 24 Bold
- Settings: 24 Bold
- Warranty: 26 Bold
- Thaka Projects: 22 Bold

The token is visibly used in the Sprint 1 verification/placeholder tooling but not consistently by production pages.

**Expected:**
Equivalent page titles should derive from a canonical page-title style, with deliberate exceptions documented as variants.

**Root Cause:**
Typography resource exists but production pages predate or bypass its adoption.

**Recommended Fix Direction:**
Adopt the shared typography styles or create explicit screen-type variants.

**Confidence:** HIGH

---

## UI-011 — Cross-screen width and gutter rhythm drifts without a declared semantic system

**Severity:** MEDIUM  
**Scope:** MULTI-SCREEN  
**Component:** Page containers / gutters / max widths

**Observed:**
Examples from equivalent operational pages:

- Customers / Expenses / Inventory / Purchase History: outer padding 20, max width 1320
- Product Management: outer padding 20, max width 1380
- Reports: outer padding 20, max width 1360
- Dashboard: outer padding 17.5
- Warranty: outer margin 24
- POS: outer margin 14
- Sale Detail: max width 1240

POS density is plausibly intentional, and detail screens can justifiably differ. Product Management vs other large list pages and Reports vs equivalent management pages have no obvious declared container variant.

**Expected:**
A small number of named page-density/container variants rather than independent near-values.

**Root Cause:**
Page geometry is defined locally instead of through shared page-layout tokens/components.

**Recommended Fix Direction:**
Define semantic page shells such as standard, dense POS, wide-table and detail. Migrate only semantically equivalent pages.

**Confidence:** MEDIUM-HIGH

---

## UI-012 — Shared loading/error components exist but state presentation is fragmented

**Severity:** MEDIUM  
**Scope:** MULTI-SCREEN  
**Component:** Loading / error / empty states

**Observed:**
Reusable `LoadingState` and `InlineError` controls exist, but the routed production views inspected do not consistently use them. Product Management hand-builds loading and error Borders. Empty-state usage is much more common on Customers, Expenses, Inventory, Purchase History and Product Management, but several data-heavy screens have no equivalent reusable empty/error presentation in XAML.

**Expected:**
Equivalent asynchronous/data states should look and behave consistently.

**Root Cause:**
State controls were created but adoption is incomplete.

**Recommended Fix Direction:**
Standardize state presentation patterns after verifying each ViewModel's actual state contract. Do not invent loading states where the workflow does not have one.

**Confidence:** MEDIUM

---

## UI-013 — SearchBox has a 44-DIP canonical contract but is repeatedly overridden to 36

**Severity:** MEDIUM  
**Scope:** MULTI-SCREEN  
**Component:** SearchBox

**Observed:**
`SearchBox.xaml` declares `Height="44"`; `Spacing.xaml` also defines `Dimension.Search.Large=44`. Multiple management/list pages explicitly set SearchBox height to 36.

Examples include Customers, Inventory, Product Management and Purchase History.

**Expected:**
If 36 is an intended compact search field, it should be an explicit shared compact variant/token rather than a local override.

**Root Cause:**
Only a large SearchBox contract is defined while screens require at least two density modes.

**Recommended Fix Direction:**
Add a canonical compact SearchBox variant and migrate equivalent pages.

**Confidence:** HIGH

---

## UI-014 — Production Print Preview is visually outside the application theme system

**Severity:** MEDIUM  
**Scope:** LOCAL  
**Screen:** Production Print Preview  
**File:** `Production/Printing/ProductionPrintPreviewWindow.xaml`

**Observed:**
The preview window uses raw Window/Grid/DocumentViewer/Button controls with fixed widths/margins and no Edge Retails app background, typography, button style, border/radius or theme integration.

It does correctly use `IsCancel="True"` and `IsDefault="True"`, which is stronger keyboard behavior than the custom overlay dialogs.

**Expected:**
A production-facing window should visually belong to the application and remain coherent in dark mode.

**Root Cause:**
Print preview was implemented as an isolated production utility rather than through the shared shell/design resources.

**Recommended Fix Direction:**
Theme the window while preserving native DocumentViewer behavior and its correct default/cancel semantics.

**Confidence:** HIGH

---

## UI-015 — Navigation icon semantics are incomplete

**Severity:** MEDIUM  
**Scope:** LOCAL / navigation  
**Component:** Sidebar icon mapping  
**Files:** `Resources/NavigationIcons.xaml`, `ViewModels/ShellViewModel.cs`

**Observed:**
The shared icon dictionary contains no dedicated Warranty or Product Management icon key. `ShellViewModel` maps:

- Product Management -> `Icon.Nav.Inventory`
- Warranty -> `Icon.Nav.Suppliers`

This makes distinct navigation concepts visually reuse unrelated/adjacent symbols.

**Expected:**
Primary navigation destinations should have semantically distinguishable icons where icons are part of the navigation language.

**Root Cause:**
Navigation icon library did not expand with later canonical screens.

**Recommended Fix Direction:**
Add dedicated icon resources consistent with the existing stroke/bounding-box system.

**Confidence:** HIGH

---

## UI-016 — Hard-coded visual values proliferate despite an existing token system

**Severity:** MEDIUM  
**Scope:** GLOBAL  
**Component:** Spacing, dimensions, typography

**Observed:**
The codebase contains a mature token dictionary, yet production XAML repeatedly introduces direct values for semantically reusable relationships.

Frequently observed spacing values include 4, 5, 6, 7, 8, 10, 12, 14, 16, 18, 20, 21, 22 and 24. Control heights include 30, 31.5, 32, 34, 35, 36, 38, 42, 44, 46 and 48.

The problem is not the count of values alone. It is that many are used for the same relationship without a named variant.

**Expected:**
Reusable semantic values should be named; truly local geometry should remain local.

**Root Cause:**
Design tokens were introduced but migration/enforcement is incomplete.

**Recommended Fix Direction:**
Inventory repeated semantic values and add tokens only where reuse is real. Avoid turning every literal into a token.

**Confidence:** HIGH

---

## UI-017 — Theme gradient keys are duplicated in base and theme dictionaries

**Severity:** LOW  
**Scope:** GLOBAL maintainability  
**Component:** ResourceDictionary key ownership  
**Files:** `Resources/Gradients.xaml`, `Resources/Themes/Light.xaml`, `Resources/Themes/Dark.xaml`

**Observed:**
Keys such as `Gradient.Sidebar.Header`, `Gradient.Sidebar.Active`, `Gradient.Sidebar.Footer`, `Gradient.Surface.Card` and KPI gradients exist in both the base gradients dictionary and theme dictionaries.

Because App.xaml merges Gradients before the active theme, the theme dictionary overrides those keys. ThemeService later swaps the active theme dictionary correctly.

**Expected:**
Resource ownership should be unambiguous so future edits do not accidentally modify a shadowed key.

**Root Cause:**
Base/fallback and theme-specific gradients overlap.

**Recommended Fix Direction:**
Clarify fallback ownership or remove redundant definitions after confirming no design-time dependency.

**Confidence:** HIGH

---

## UI-018 — Screen-local semantic colors bypass theme tokens in POS

**Severity:** LOW  
**Scope:** LOCAL  
**Screen:** POS  
**Component:** segmented-mode gradients

**Observed:**
`PosView.xaml` defines local hard-coded gradients such as teal/green and purple values instead of referencing centralized semantic color resources.

**Expected:**
Semantic brand/mode colors that must survive theme changes should ideally be controlled centrally.

**Root Cause:**
POS received local visual tuning after the shared palette was established.

**Recommended Fix Direction:**
Promote only genuinely reusable POS semantic colors to resources. Keep purely local decorative values local if theme behavior is verified.

**Confidence:** MEDIUM

---

# Button / Control Findings

## Shared button centering

The global `Button.Base` template is statically well-formed for centering:

- `HorizontalContentAlignment=Center`
- `VerticalContentAlignment=Center`
- ContentPresenter binds both alignment properties
- standard padding is horizontal-only (`14,0`)

Therefore this audit does **not** claim a verified global vertical-centering bug in `Button.Base`.

Potential optical baseline problems can still occur because of font metrics, fractional device scaling and local templates. Those require runtime screenshots at the actual DPI.

## Verified control drift

The more defensible control defect is inconsistent adoption of shared styles, not a proven bad global Button template.

Particularly problematic are raw/native ComboBoxes, DatePickers, CheckBoxes, RadioButtons, PasswordBoxes and raw DataGrids.

---

# Typography Findings

Canonical resources are coherent, but production adoption is partial.

Most production page titles use local 24/Bold rather than `Text.PageTitle` 24/SemiBold. Warranty uses 26/Bold; Thaka Projects uses 22/Bold. Detail/drawer titles use 17–18. The differences are not all incorrect, but they are not expressed as named hierarchy variants.

Warranty also contains 9px timeline timestamps and multiple 10px helper/footer texts using muted colors. Combined with the light-theme muted/subtle contrast issue, this produces a real readability risk.

---

# Spacing Findings

The strongest spacing issue is not any individual 2px mismatch. It is the absence of page-level semantic spacing contracts.

Examples of equivalent-looking values include:

- dialog padding: commonly 22, but New Thaka uses 24, Purchase Return uses 21/21,14, Complete Sale uses multiple independent paddings
- page padding/margins: 14, 17.5, 20 and 24
- header-to-section gaps: 14, 16 and 18 across similar CRUD/list pages
- control-to-control gaps: 6, 7, 8, 10, 12 and 14

Some of these are intentional optical tuning. The recommended remediation is not blanket normalization. It is to define named layout variants and remove unexplained local deviations.

---

# Alignment Findings

Primary alignment findings:

- numeric DataGrid columns do not share one alignment convention
- page/container widths are not consistently aligned across equivalent management screens
- native controls inserted beside custom controls have different internal text baselines/padding
- several local icon-only buttons have no shared bounding-box/focus contract

The sidebar itself is comparatively disciplined: icon cells, active indicator and text columns are explicitly positioned. Runtime optical alignment is still required before declaring its icon centering perfect.

---

# DataGrid Findings

Positive findings:

- shared `Table.DataGrid` enables row and column virtualization
- shared table row/header/cell styles are coherent
- Sales History and Sale Detail explicitly format key numeric columns with numeric font and right alignment

Defects:

- Warranty bypasses `Table.DataGrid`
- Supplier Detail bypasses `Table.DataGrid` across four tables
- many money/quantity columns remain left-aligned on otherwise shared tables
- several list pages wrap DataGrid content inside page-level ScrollViewers; this should be runtime-tested with large datasets because nested scrolling can affect keyboard scrolling and virtualization behavior

The nested-scroll concern is a **runtime risk**, not promoted to a fully validated defect without execution evidence.

---

# Form Findings

The design system provides good TextBox/ComboBox templates, but form quality varies depending on adoption.

Strongly styled forms:

- Settings core text inputs
- many purpose-built dialogs
- Thaka payment/material dialogs

Weakly integrated forms:

- Warranty workflow side panel
- Supplier Detail financial event form
- New Purchase
- Product Edit mixed controls
- Expense Edit mixed controls
- Reports date/month/year selector group

The mixed styled/native rendering is particularly risky in dark theme.

---

# Navigation Findings

Positive:

- selected navigation state has dedicated gradient + left indicator
- navigation labels have tooltips and `AutomationProperties.Name`
- sidebar collapse is explicit and keyboard command exists (`Ctrl+B`)

Findings:

- Warranty reuses the Suppliers icon
- Product Management reuses Inventory icon
- local navigation-related button styles on Thaka screens can lose keyboard focus indication

No source evidence of route/title mismatch was found in the primary top-level navigation mapping.

---

# Dialog Findings

24 overlay dialog XAML surfaces were inspected.

Main systemic issue: modal behavior is visual, not fully keyboard-modal.

Common dialog geometry is reasonably clustered around 22px padding, but several dialogs use 21/24 and specialized internal spacing. The differences should be reviewed after the modal/focus contract is fixed.

Encoding corruption affects close glyphs in multiple dialogs.

`CompleteSaleDialog` is the strongest keyboard-aware overlay dialog because it explicitly focuses the amount field and handles Enter/Escape.

The native print preview window, conversely, correctly uses WPF `IsDefault` and `IsCancel` semantics.

---

# DPI / Window Findings

`MainWindow` uses WPF DIPs but sets a 1366x768 minimum. This is the primary verified DPI/resizing defect.

Other runtime resize risks:

- Warranty wide table in split pane
- Supplier Detail width mismatch in drawer
- fixed-width POS cart (420) combined with minimum window constraints
- multiple wide list/table screens with fixed column widths
- modal sizes up to approximately 980 x 720/780 inside a modal host that adds 24px margin and may itself be constrained by scaled windows

These secondary items should be exercised at 100%, 125%, 150%, 175% and 200% once runtime access is available.

---

# Accessibility Findings

Verified:

- several local button templates suppress focus visuals without replacements
- generic overlay dialogs lack shared focus trapping/restoration/default/cancel semantics
- small muted/subtle text has weak light-theme contrast
- some icon buttons use textual close glyphs rather than stable icon geometry

Positive:

- sidebar items expose `AutomationProperties.Name`
- top-bar sidebar toggle and switch-user buttons expose accessible names/tooltips
- shared Button/TextBox/ComboBox templates provide focus-border behavior when actually used

Accessibility maturity is therefore uneven rather than absent.

---

# Cross-Screen Consistency Findings

Most classic CRUD/list pages share a recognizable structure:

page title/subtitle -> optional KPIs -> filter/search card -> DataGrid card.

Customers, Expenses, Inventory and Purchase History are relatively aligned around 20px page padding and 1320 max width.

Drift becomes strongest in:

- Product Management (1380 max width)
- Reports (1360)
- Warranty (24 margin, raw controls, 26 title, split workspace)
- Supplier Detail (native tabs/tables, incompatible drawer min width)
- Dashboard (17.5 outer padding and bespoke local button families)
- POS (intentionally dense but heavily local)

The overall design language is recognizable, but it is not fully governed.

---

# Hard-Coded Value Analysis

Canonical tokens exist, but local values remain widespread.

Actual recurring dimension families discovered include:

**Page/section spacing:** 12 / 14 / 16 / 17.5 / 18 / 20 / 21 / 22 / 24  
**Control heights:** 28 / 30 / 31.5 / 32 / 34 / 35 / 36 / 38 / 42 / 44 / 46 / 48  
**Page max widths:** 1240 / 1320 / 1360 / 1380  
**Dialog widths:** approximately 440 / 460 / 480 / 500 / 520 / 560 / 620 / 760 / 780 / 860 / 920 / 980

Many dialog width differences are justified by content. The page/container and equivalent control-size differences need semantic naming to distinguish design intent from drift.

---

# Design-System Drift Analysis

## Canonical-looking values

- standard button/input: 35
- compact input: 28
- large search: 44
- standard table row: 38.5
- standard table header: 35
- standard card radius: 10
- standard control radius: 8
- main page title: 24
- common list-page outer padding: 20 in current production pages, despite the separate `Padding.Page.Standard=24,20` token

## Likely intentional variants

- POS dense spacing and fixed cart column
- large transaction completion actions 46/48
- compact text actions
- dialog widths driven by content

## Likely drift / missing variants

- SearchBox 44 default vs repeated 36 overrides
- 24/SemiBold page-title token vs production 24/Bold + 22/26 variants
- 1320 vs 1360 vs 1380 max widths across comparable page types
- raw native controls beside themed controls
- numeric columns aligned differently across tables
- late workflow screens not using shared DataGrid/Input/Tab styles

---

# Severity Breakdown

| Severity | Count | Notes |
|---|---:|---|
| CRITICAL | 0 | No statically verified defect proven to make an essential workflow impossible or dangerously misleading |
| HIGH | 6 | Drawer width contradiction, Warranty table width, modal keyboard contract, opt-in visual system, missing focus visuals, DPI-hostile minimum window |
| MEDIUM | 10 | contrast, encoding, numeric alignment, typography, spacing/container drift, state fragmentation, SearchBox dimension drift, print preview theming, icon semantics, hard-coded visual values |
| LOW | 2 | duplicate gradient ownership, local POS hard-coded semantic gradients |

Severity reflects user/workflow impact. Scope and reuse should also drive implementation priority.

---

# Global vs Local Findings

## Global

- design system opt-in enforcement gap
- modal keyboard/focus contract
- minimum window DPI sizing
- muted/subtle text contrast
- typography-token adoption
- hard-coded value proliferation
- resource ownership duplication

## Multi-screen

- local button focus loss
- mojibake symbols
- numeric table alignment
- page/container rhythm drift
- loading/error state fragmentation
- SearchBox 36/44 inconsistency

## Local but high impact

- Supplier Detail 760 inside 480 drawer
- Warranty table width budget failure
- Warranty visual-system detachment
- production Print Preview visual isolation

---

# Cross-Layer Dependencies

No backend redesign is recommended by this report.

The audit does not identify a verified backend contract defect that must be changed to correct the visual findings above.

Some loading/error-state standardization should first confirm each ViewModel's real state model, but that is a presentation integration task, not a request to alter business authority.

---

# Runtime Verification Status

`RUNTIME_VISUAL_VERIFICATION = BLOCKED_ENVIRONMENT`

The repository was available through GitHub source access, but a local runnable Windows/WPF rendering environment was not available in this audit session. A local clone/runtime mirror attempt was not available due environment/network restrictions.

Therefore the following were **not claimed as pixel-verified**:

- exact optical vertical centering of button labels
- ClearType/font-metric baseline behavior
- actual hover/pressed interpolation appearance
- monitor-specific DPI clipping
- live keyboard tab order across every screen
- real long-name/large-number wrapping
- nested ScrollViewer/DataGrid behavior with thousands of rows
- native control appearance under live dark theme

Static source evidence was used only where it proves the layout/style relationship directly.

---

# Recommended Fix Order

## P0 — Structural production layout/accessibility blockers

1. Resolve Supplier Detail 760-vs-480 drawer contradiction.
2. Rework Warranty table/pane width budget.
3. Fix global modal focus lifecycle and keyboard contract.
4. Rework minimum window sizing for DPI-scaled displays.

## P1 — Global shared-system enforcement

1. Close the keyed-style/native-fallback gap.
2. Add/standardize DatePicker, CheckBox, RadioButton and PasswordBox families.
3. Restore keyboard focus visuals to every local Button template.
4. Add shared numeric DataGrid cell/column alignment conventions.
5. Correct light-theme small-text contrast.

## P2 — High-impact cross-screen alignment

1. Integrate Warranty with shared Input/Table/Card typography styles.
2. Integrate Supplier Detail with shared Tabs/Tables/Inputs after its host layout is fixed.
3. Normalize SearchBox large/compact contracts.
4. Adopt named page-title/container variants.
5. Normalize state presentation patterns.
6. fix mojibake globally.

## P3 — Screen-specific medium corrections

1. Reports native selectors
2. New Purchase mixed native controls
3. Product Management selectors/encoding/width variant
4. Inventory filter controls
5. Purchase History supplier selector
6. Settings CheckBoxes
7. production Print Preview theming
8. navigation icon semantics

## P4 — Micro-polish after runtime capture

1. button optical centering
2. icon baseline/optical size
3. 1–4px spacing drift
4. typography baseline differences
5. radius/shadow micro-consistency
6. long-content truncation/wrapping refinements

Global defects should be fixed before patching dozens of local usages.

---

# Post-Fix Verification Strategy

After remediation, run a dedicated visual regression pass using the same audited commit lineage plus the frontend fixes.

Required verification matrix:

- Windows scaling: 100%, 125%, 150%, 175%, 200%
- window states: minimum supported size, default, maximized
- themes: Light, Dark, System
- keyboard-only navigation through sidebar, forms, dialogs and drawers
- Enter/Escape/default/cancel semantics
- focus restoration after dialog close
- long product/customer/supplier names
- large Rs. monetary values
- long serial/IMEI/tracking identifiers
- empty/search-no-results/error/loading states
- DataGrids with large datasets
- row selection + action buttons
- supplier drawer workflows
- warranty main grid and right-side action workflow
- print preview in light/dark theme

Capture before/after screenshots at identical window/DPI settings. Compare shared components first, then individual screens.

---

# Screen Quality Matrix

| Screen | Visual consistency | Spacing | Alignment | Typography | Controls | Tables | Responsive/DPI | Accessibility | Highest severity |
|---|---|---|---|---|---|---|---|---|---|
| First Setup | Mostly consistent | Minor | Minor | Minor | Mixed PasswordBox | N/A | Not runtime verified | Minor | MEDIUM |
| Login | Mostly consistent | Minor | Minor | Mostly consistent | Local keypad | N/A | Not runtime verified | Missing keypad focus | HIGH |
| Dashboard | Mostly consistent | Custom dense | Mostly consistent | Local | Local buttons | N/A | Not runtime verified | Missing focus states | HIGH |
| POS | Mostly consistent | Dense/custom | Mostly consistent | Custom | Local controls | Mixed | Not runtime verified | Missing local button focus | HIGH |
| Sales History | Good | Mostly consistent | Good | Good | Shared | Good numeric handling | Not runtime verified | Minor | LOW/MEDIUM |
| Sale Detail | Good | Custom detail | Good | Good | Shared | Good numeric handling | Not runtime verified | Minor | LOW/MEDIUM |
| Thaka Projects | Mostly consistent | Custom | Mostly consistent | 22px title outlier | Local tabs/buttons | Numeric drift | Not runtime verified | Missing focus states | HIGH |
| Thaka Workspace | Mostly consistent | 20 | Numeric drift | Mostly consistent | Shared | Numeric drift | Not runtime verified | Minor | MEDIUM |
| Purchase History | Mostly consistent | 20 | Mostly consistent | 24 Bold | Raw ComboBox | Numeric drift | Not runtime verified | Minor | MEDIUM |
| New Purchase | Mixed | 20 | Numeric drift | 24 Bold | Multiple raw controls | Numeric drift | Not runtime verified | Minor | MEDIUM |
| Purchase Detail | Mostly consistent | Detail-specific | Mostly consistent | Detail-specific | Shared | Numeric drift | Not runtime verified | Minor | MEDIUM |
| Product Management | Mixed | 20 / 1380 | Mostly consistent | 24 Bold | Raw ComboBoxes | Numeric drift | Not runtime verified | Encoding defect | MEDIUM |
| Product Detail | Mostly consistent | 20 / 1320 family | Numeric drift | Detail-specific | Shared | Numeric drift | Not runtime verified | Minor | MEDIUM |
| Inventory | Mostly consistent | 20 / 1320 | Numeric drift | 24 Bold | Raw ComboBoxes | Numeric drift | Not runtime verified | Minor | MEDIUM |
| Expenses | Mostly consistent | 20 / 1320 | Amount drift | 24 Bold | Raw ComboBox | Numeric drift | Not runtime verified | Minor | MEDIUM |
| Customers | Good | 20 / 1320 | Mostly consistent | 24 Bold | Shared | Minor numeric semantics | Not runtime verified | Minor | MEDIUM |
| Customer Detail | Mostly consistent | Drawer-specific | Mostly consistent | 17 Bold | Shared-ish | N/A | 480 drawer | Minor | MEDIUM |
| Suppliers | Good | 20 / 1320 | Mostly consistent | 24 Bold | Shared | Minor numeric semantics | Not runtime verified | Minor | MEDIUM |
| Supplier Detail | Major inconsistency | Drawer-specific | Host conflict | Local | Raw | Raw | **760 inside 480** | Modal/drawer context | **HIGH** |
| Warranty | Major inconsistency | 24 | **Wide-table overflow** | 26 + 9/10px text | Raw | Raw | **Major static risk** | Contrast/state issues | **HIGH** |
| Reports | Mixed | 20 / 1360 | Mostly consistent | 24 Bold | Native selectors | N/A | Not runtime verified | Minor | MEDIUM |
| Settings | Mostly consistent | 20 | Mostly consistent | local 19/24 hierarchy | Mixed CheckBoxes | Shared | Not runtime verified | Minor | MEDIUM |
| Print Preview | Visual island | 12 | Native | Native | Native | DocumentViewer | Not runtime verified | Good default/cancel | MEDIUM |

---

# Final Auditor Questions

**Did we inspect every production screen discovered in the repository?**  
YES, static-source level.

**Did we inspect shared ResourceDictionaries?**  
YES.

**Did we compare equivalent components across screens?**  
YES.

**Did we inspect button templates deeply?**  
YES. The shared button-centering defect was not statically proven; local focus defects were.

**Did we inspect actual ContentPresenter alignment?**  
YES for shared and major local button templates.

**Did we audit typography globally?**  
YES.

**Did we audit major DataGrid families?**  
YES.

**Did we test long content at runtime?**  
NO. `BLOCKED_ENVIRONMENT`; static width/wrapping risks were recorded instead.

**Did we inspect forms?**  
YES.

**Did we inspect dialogs?**  
YES, all discovered overlay dialog XAML files plus code-behind keyboard behavior sampling/verification.

**Did we inspect error/empty/loading states?**  
YES at source level.

**Did we inspect window resizing?**  
YES statically; runtime resize verification blocked.

**Did we inspect DPI scaling where possible?**  
YES through WPF DIP constraint analysis; runtime multi-monitor verification blocked.

**Did we identify local vs systemic causes?**  
YES.

**Did we deduplicate findings?**  
YES. Findings are root-cause-first.

**Did we preserve Git?**  
YES. Git was not modified.

**Did we avoid backend redesign?**  
YES.

**Did we avoid fixing before audit completion?**  
YES.

---

# Final Status

`STATIC_FORENSIC_AUDIT = COMPLETE`

`RUNTIME_VISUAL_VERIFICATION = BLOCKED_ENVIRONMENT`

`GIT_MODIFIED = NO`

`BACKEND_MODIFIED = NO`

`FIXES_IMPLEMENTED = NO`

The next engineering phase should start from the P0/P1 root causes above rather than patching individual screens in arbitrary order.
