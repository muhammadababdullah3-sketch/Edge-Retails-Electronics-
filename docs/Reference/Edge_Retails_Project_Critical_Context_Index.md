# Edge Retails — Critical Project Context Index

This file preserves the small set of chat-derived project facts that are worth carrying into future development chats.

## Source hierarchy

1. **Current Figma Design** is the visual source of truth.
2. **Edge_Retails_Frontend_Master_Spec_v1.1_Corrected.md** is the canonical frontend architecture and screen behavior specification.
3. **Edge_Retails_Figma_Forensic_Design_Brief.md** defines the forensic UI/UX polish rules for translating the design into production WPF.
4. **Edge_Retails_Final_Tech_Stack_Summary.md** is the canonical technology and architecture decision record.
5. **WPF workspace code** is the production implementation source of truth.
6. Old React/Figma-generated prototype files are reference-only and are not production code.

## Figma references

Current Figma Design:
https://www.figma.com/design/vkHv6qfZ0fSXRuMUAuhSHD/Untitled?node-id=0-1&t=ppXTqtu3oxEwh1HG-1

Figma Make visual reference:
https://www.figma.com/make/KDYEDc964VBsrmq0kmoskM/Design-POS-Frontend-Screens

Figma Make is a secondary visual reference only. The current Figma Design remains primary.

## Production stack

- Windows Desktop
- WPF + C# + XAML
- .NET 10 LTS
- MVVM
- Modular Monolith
- PostgreSQL local primary database
- Npgsql
- EF Core for normal persistence
- Dapper/raw SQL for heavy reports
- .NET Worker Service
- Serilog
- xUnit
- WiX Toolset + Burn
- Local signed license validation
- Encrypted cloud backup
- No mandatory online licensing server in V1
- No FBR integration in V1

## Canonical screen architecture

There are exactly 17 primary full screens:

01. First Setup / License
02. Login / User Switch
03. Dashboard
04. POS / New Sale
05. Sales History
06. Sale Detail
07. Thaka / Projects
08. Thaka Workspace
09. New Purchase
10. Purchase History
11. Inventory
12. Product Detail
13. Expenses
14. Customers
15. Suppliers
16. Reports
17. Settings

Supporting actions remain dialogs, drawers, overlays, popovers, dropdowns or toasts. Do not create screen 18/19 without explicitly changing the product architecture.

## Design implementation workflow

Figma Design → forensic visual extraction → WPF design tokens/resources → reusable WPF controls → screen XAML → ViewModels/commands → navigation/dialogs → backend/domain wiring → build/test → Figma comparison → manual acceptance → freeze sprint.

Native WPF/XAML is required. Do not ship screenshots or React/web UI as the desktop implementation.

## Core visual contract

- Primary frame: 1440×900
- Minimum supported visual target: 1366×768
- Sidebar: 232 expanded / 72 collapsed
- Top bar: 56–64 depending on the exact audited component
- Brand: #635BFF
- Light app background: #F6F9FC
- Primary text: #0A2540
- Border: #E6EBF1
- Plus Jakarta Sans primary typography
- Space Grotesk used sparingly for KPI/numeric emphasis
- Crisp layered design, not glassmorphism
- Visual texture comes from restrained gradients, borders, semantic tints and shadows, not bitmap/noise textures

## Important business rules

- Local sales and Thaka remain separate until Thaka revenue-recognition policy is decided.
- Inventory equation must account for purchases, local sales, Thaka material, sale returns, purchase returns, damage and adjustments.
- Inventory purchases are not operating expenses.
- Dashboard Today Profit means local gross profit; expenses are separate.
- Decimal quantity support is mandatory.
- Stock must never be silently edited. Adjustments require a movement and reason.
- Sale returns require disposition such as sellable/restock versus damaged/defective/non-sellable.
- Purchase returns must respect eligible return quantity.
- Settled Thaka becomes read-only unless an explicitly privileged reopen workflow is later approved.

## Intentionally unresolved domain decisions

Do not invent these during implementation:

- Inventory costing method: weighted average vs FIFO vs another approved method
- Thaka revenue recognition
- Opening-stock workflow
- Purchase Other Charges allocation
- Serial/IMEI full lifecycle
- Warranty lifecycle
- Damaged/non-sellable stock bucket UI
- Thaka overpayment handling
- Reopening settled Thaka
- Receipt/invoice numbering and printer specifics
- Return cost/profit reversal details

## Sprint 1 workspace status

Sprint 1 foundation was migrated into:
`C:\Users\muham\OneDrive\Desktop\Point of Sale`

Safety branch created before migration:
`backup/pre-sprint1-migration`

Sprint 1 includes the WPF resource system, colors, gradients, shadows, typography, buttons, cards, shell, sidebar/topbar, navigation foundation, MVVM primitives, modal/drawer/toast infrastructure, theme service, live clock, loading/empty states and placeholder routing.

The real Windows workspace has already passed restore, Debug build, Release build, unit tests, integration tests and format verification after migration. Final sprint freeze still requires visual/manual acceptance against Figma.

## What can be ignored from the old chat

The following are not canonical production sources and do not need to be carried forward once this index and the canonical docs are saved:

- React/Vite prototype source files generated during visual experiments
- duplicated copies of the corrected master spec inside prototype folders
- old Figma MCP rate-limit notes
- obsolete WinUI-vs-WPF exploration once WPF was finalized
- duplicate Sprint 1 ZIPs/manifests after successful workspace migration
- temporary forensic transfer artifacts
