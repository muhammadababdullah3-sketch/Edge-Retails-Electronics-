# Stage 5: Canonical Frontend Redesign Crosswalk (FE-RD-01..42 & DS-01..14)

- **Run ID**: `ER-RUN-FE-FINAL-20261020-001`
- **Stage**: 5 — Canonical Requirements Crosswalk
- **Date**: 2026-10-20

## 1. Design System Tokens (DS-01 through DS-14)

| Token Area | Resource File | Verification Evidence |
| :--- | :--- | :--- |
| `DS-01: Colors` | `Colors.xaml`, `Colors.Light.xaml`, `Colors.Dark.xaml` | Semantic color tokens defined for brand, neutral, surface, text, and status. |
| `DS-02: Brushes` | `Brushes.xaml` | Dynamic and static brushes bound to theme colors, including `Brush.DarkInk` and `Brush.Warning.Foreground`. |
| `DS-03: Gradients` | `Gradients.xaml` | 18 linear gradients mathematically verified to pass WCAG 2.2 AA along gradient vector. |
| `DS-04: Spacing` | `Spacing.xaml` | 4px grid spacing primitives (4, 8, 12, 16, 20, 24, 32, 48 DIP). |
| `DS-05: Radii` | `Radii.xaml` | Border corner radius hierarchy (4px, 6px, 8px, 12px, 16px, 999px pill). |
| `DS-06: Shadows` | `Shadows.xaml` | Multi-layered drop shadows with subtle ambient and directional elevation. |
| `DS-07: Typography`| `Typography.xaml` | Space Grotesk tabular typography for figures, Inter for UI prose. |
| `DS-08: Icons` | `NavigationIcons.xaml` | Pure vector `PathGeometry` icons; 0 raw unicode text glyphs. |
| `DS-09: Buttons` | `Buttons.xaml` | Standard, compact, warning, payment, and vector close button styles. |
| `DS-10: Inputs` | `Inputs.xaml` | Text boxes, search boxes, combo boxes, password boxes with 3px focus rings. |
| `DS-11: Tables` | `Tables.xaml` | High-performance virtualized DataGrid styling with numeric alignment. |
| `DS-12: Cards` | `Cards.xaml` | Flat, raised, and interactive card templates with elevation borders. |
| `DS-13: Tabs` | `Tabs.xaml` | Segmented tab controls with active pill indicators. |
| `DS-14: Badges` | `Badges.xaml` | Status badges for success, warning, danger, info, and neutral states. |

## 2. Redesign Rows (FE-RD-01 through FE-RD-42)

All 42 redesign rows have been audited, implemented in XAML/C#, and verified:
- **Workstation Shell (FE-RD-01..04)**: TopBar, Sidebar, ModalHost, DrawerHost.
- **Cashier POS Grid (FE-RD-05..10)**: Fast catalog search, touch steppers, payment keypad, Complete Sale CTA.
- **Sales & Returns (FE-RD-11..14)**: Commercial exchange history, receipt reprint, return authorization.
- **Catalog & Inventory (FE-RD-15..20)**: Virtualized product list, barcode scanner intake, stock adjustments, full stocktake.
- **Purchasing & Intake (FE-RD-21..25)**: Supplier purchase orders, physical intake, serialized unit tracking.
- **Thaka & Projects (FE-RD-26..30)**: Multi-stage custom manufacturing, material allocations, final settlement.
- **Financial Khata & Expenses (FE-RD-31..36)**: Expense tracking, cash drawer sessions, customer & supplier subledgers.
- **Owner Dashboard & Reports (FE-RD-37..39)**: Real-time KPIs, analytical charts, tabular data export.
- **Dialogs & Approvals (FE-RD-40..42)**: Supervisor backdate authorizations, confirmation modals, vector close buttons.
