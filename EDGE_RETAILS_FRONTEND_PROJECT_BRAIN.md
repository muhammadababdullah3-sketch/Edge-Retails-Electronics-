# EDGE RETAILS — FRONTEND PROJECT BRAIN
## READ THIS FIRST IN EVERY NEW CHAT

**Purpose:** This is the canonical handoff/context file for the Edge Retails desktop frontend.  
It tells a new ChatGPT session what the project is, what has already been built, what is currently in progress, what is still missing, what must not be broken, and what the next engineering steps are.

**Workspace:** `C:\Users\muham\OneDrive\Desktop\Point of Sale`  
**Solution:** `C:\Users\muham\OneDrive\Desktop\Point of Sale\EdgeRetails.sln`  
**Desktop project:** `src\EdgeRetails.Desktop`  
**Tests:** `tests\EdgeRetails.UnitTests`, `tests\EdgeRetails.IntegrationTests`  
**Last project-brain refresh:** 19 Sep 2026

---

# 1. PRODUCT

Edge Retails is a premium Windows desktop Point of Sale application for an electrical/electronics retail business.

The UI must feel:
- premium
- institutional
- modern
- precise
- friendly
- desktop-native
- operational, not decorative

Avoid:
- cheap generic POS styling
- mobile-first proportions
- gaming/neon UI
- random gradients
- giant cards/buttons
- excessive rounded corners
- hardcoded theme-breaking colors
- browser-style layout hacks

---

# 2. TECHNOLOGY LOCK

This project is a **native WPF desktop application**.

Required frontend stack:
- WPF
- C#
- XAML
- .NET 10 LTS
- MVVM
- Modular Monolith architecture

Do NOT convert the frontend to:
- React
- Electron
- WebView
- Blazor
- MAUI
- WinUI
- HTML/CSS browser UI

Planned/partial backend stack:
- PostgreSQL
- Npgsql
- EF Core
- Dapper for reporting
- Worker Service
- Serilog
- xUnit
- WiX Toolset + Burn

The current Sprint 1–3 frontend is mostly demo/in-memory state. Do not claim real PostgreSQL persistence unless actual backend integration has been implemented and verified.

---

# 3. DESIGN SOURCES

## Primary live Figma Design
https://www.figma.com/design/vkHv6qfZ0fSXRuMUAuhSHD/Untitled

Figma file key:
`vkHv6qfZ0fSXRuMUAuhSHD`

## Figma Make visual reference
https://www.figma.com/make/KDYEDc964VBsrmq0kmoskM/Design-POS-Frontend-Screens

Figma Make credits were exhausted, so treat the completed Make design as a visual reference only.

### Critical Figma rule
Never map a feature from the frame name alone.

Example:
- Node `16:17947` is named **"Complete thaka sale"**
- But its actual visible content is the **Normal Sale Complete Sale payment modal**
- Actual content, parent/context, labels and actions are stronger evidence than a misleading frame name.

Always verify:
1. node ID
2. live frame name
3. visible content
4. parent/context
5. actions/labels
6. originating screen

---

# 4. PRIMARY SCREEN MAP

The application has 17 planned primary production screens:

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

Dialogs, drawers and modals are not extra primary screens.

---

# 5. IMPORTANT FIGMA NODES ALREADY VERIFIED/USED

- `14:1070` — Normal Sale
- `14:2326` — Thaka Sale
- `14:2934` — Sales History
- `14:3291` — Sale / Invoice Detail
- `14:4034` — Sales Return
- `14:4538` — Thaka Projects
- `14:4836` — New Thaka
- `14:15332` — Thaka Workspace
- `14:15707` — Record Payment
- `14:16155` — Add Material
- `14:16631` — Final Settlement
- `16:17947` — actual content = Normal Sale Complete Sale modal

Before changing visual implementation, inspect the live node again.

---

# 6. FRONTEND DESIGN SYSTEM

Sprint 1 created the reusable WPF foundation.

Key resource files include:
- `Resources/Colors.xaml`
- `Brushes.xaml`
- `Gradients.xaml`
- `Typography.xaml`
- `Spacing.xaml`
- `Radii.xaml`
- `Shadows.xaml`
- `Buttons.xaml`
- `Inputs.xaml`
- `Tables.xaml`
- `Cards.xaml`
- `Tabs.xaml`
- `Badges.xaml`
- `NavigationIcons.xaml`
- `Themes/Light.xaml`
- `Themes/Dark.xaml`

Reusable controls/systems include:
- AppSidebar
- AppTopBar
- KpiCard
- SearchBox
- StatusBadge
- ModalHost
- DrawerHost
- ToastHost
- EmptyState
- LoadingState
- theme switching
- navigation
- session context
- live clock

Visual language:
- Brand: Violet / Indigo
- Success: Teal / Green
- Info: Blue
- Payment: Cyan / Teal
- Warning/Settlement: Amber
- Expense: Orange
- Danger: Red

Typography:
- Primary: Plus Jakarta Sans
- Numeric/financial emphasis: Space Grotesk

Layout targets:
- Primary: 1440×900
- Minimum: 1366×768
- Sidebar: ~232px expanded
- Topbar: ~56px
- Common radius: ~8px
- Transaction CTA: ~46–48px

Rules:
- no Canvas for ordinary layout
- use Grid/DockPanel/StackPanel/WrapPanel/UniformGrid
- use theme resources
- DataGrids are read-only unless intentional editing is explicitly required
- keep code-behind minimal and view-specific

---

# 7. SPRINT ROADMAP

## Sprint 1 — Foundation / Design System
Status: **IMPLEMENTED**

Built:
- shell
- sidebar
- topbar
- navigation foundation
- design tokens
- Light/Dark themes
- cards
- buttons
- inputs
- tables
- tabs
- badges
- modal/drawer/toast infrastructure
- reusable WPF resources/controls
- Sprint 1 QA screen in DEBUG only

Do not replace this foundation with feature-local duplicate styles.

## Sprint 2 — Login + Dashboard + POS Foundation
Status: **IMPLEMENTED, with important runtime fixes already applied**

Built:
- Login / user switch demo flow
- Dashboard
- POS / New Sale
- Normal Sale mode
- Thaka Material Issue mode foundation
- search/category/brand filters
- cart
- decimal quantity support
- mode switching
- theme/runtime integration

Known historical crashes that were fixed:
1. WPF DataGrid selection crash from synchronously setting selected item to null inside the selection lifecycle.
2. DataGrid edit-mode crash caused by TwoWay binding to read-only product properties.

Current protections that MUST remain:
- product DataGrid stays `IsReadOnly="True"`
- selected catalog item is normal selection state
- product add behavior is separate from synchronous SelectedItem nulling
- do not reintroduce these patterns

Dedicated Sprint 2 forensic tests exist:
`tests/EdgeRetails.UnitTests/Sprint2ForensicAuditTests.cs`

## Sprint 3 — Core Transactions
Status: **IN PROGRESS / CLOSE TO TECHNICAL VERIFICATION**

Scope:
- Complete Sale
- Sales History
- Sale Detail
- Sales Return
- Thaka Projects
- New Thaka
- Thaka Workspace
- Add Material
- Record Payment
- Final Settlement
- Settled/read-only state
- navigation and transaction states

Detailed current state is documented below.

## Sprint 4 — Purchases + Inventory
Do NOT start actual Sprint 4 implementation until Sprint 3 is technically verified.

Expected scope:
- New Purchase
- Purchase History
- Inventory
- Product Detail
- stock movement integration
- purchase-to-stock and return-to-stock behavior

A separate chat may perform Sprint 4 Figma/read-only planning while Sprint 3 is being finished, but should not edit the same shared source files concurrently.

## Later frontend sprints
Remaining primary modules:
- Expenses
- Customers
- Suppliers
- Reports
- Settings
- First Setup / License

Exact sprint grouping should be confirmed before coding. Do not invent backend behavior in frontend-only sprints.

---

# 8. SPRINT 3 — WHAT IS ALREADY BUILT

## Normal Sale / Complete Sale
Implemented:
- Complete Sale dialog
- `CompleteSaleViewModel`
- payment methods
- amount received
- validation
- processing state / double-submit protection
- transaction service
- sale completion callback
- cart clears only after successful completion callback
- Sales History uses the same singleton transaction service

Files:
- `ViewModels/CompleteSaleViewModel.cs`
- `Views/Dialogs/CompleteSaleDialog.xaml`
- `Services/ITransactionService.cs`
- `Services/DemoTransactionService.cs`

## Sales History
Implemented:
- real Sprint 3 View/ViewModel
- search and period filters
- shared transaction-service projection
- KPI calculation
- invoice open/detail state

Files:
- `ViewModels/SalesHistoryViewModel.cs`
- `Views/SalesHistoryView.xaml`

## Sale Detail
Implemented:
- transaction details
- immutable display projection
- return entry path

Files:
- `ViewModels/SaleDetailViewModel.cs`
- `Views/SaleDetailView.xaml`

## Sales Return
Implemented:
- return quantity validation
- eligible quantity
- returned quantity
- disposition model
- payment status and return status separated
- invoice-level discount return is deliberately blocked until allocation policy is decided

Files:
- `ViewModels/SalesReturnViewModel.cs`
- `Views/Dialogs/SalesReturnDialog.xaml`

## Thaka Projects / New Thaka
Implemented:
- project list
- KPIs
- Active/Settled filtering
- New Thaka dialog
- open-workspace navigation path
- workspace button bindings were corrected to call the parent page command

Files:
- `ViewModels/ThakaProjectsViewModel.cs`
- `ViewModels/NewThakaViewModel.cs`
- `ViewModels/ThakaProjectListItemViewModel.cs`
- `Views/ThakaProjectsView.xaml`
- `Views/Dialogs/NewThakaDialog.xaml`

## Thaka Workspace
Implemented:
- material ledger
- payment ledger
- KPIs
- Add Material action
- Record Payment action
- Final Settlement action
- settled read-only command state
- dynamic semantic StatusBadge

Files:
- `ViewModels/ThakaWorkspaceViewModel.cs`
- `Views/ThakaWorkspaceView.xaml`

## Shared Sprint 3 demo retail state
A new shared state service now exists:

`Services/DemoRetailState.cs`

It is intended to be the single in-memory source for:
- products
- mutable stock
- Thaka projects
- material ledgers
- payment ledgers
- settlement state

Recent integration work moved:
- POS product catalog toward shared products
- POS Thaka project list toward shared projects
- POS Thaka Material Issue to shared Thaka state
- Add Material to shared stock/state
- Record Payment to shared state
- Final Settlement to shared state
- Workspace ledgers to shared project-specific ledgers
- local sale stock decrement to the shared product state

---

# 9. LATEST VERIFIED TECHNICAL STATUS

Latest verified after the shared-state changes:

**Debug build:** PASS  
- 0 warnings
- 0 errors

**Unit tests:** PASS  
- 24 / 24

**Integration tests:** PASS  
- 1 / 1

Dedicated Sprint 3 forensic tests now exist:
`tests/EdgeRetails.UnitTests/Sprint3ForensicAuditTests.cs`

Important:
- Release build has not yet been rerun after the newest shared-state edits.
- final `dotnet format --verify-no-changes` has not yet been confirmed clean after the newest edits.
- final `git diff --check` has not yet been confirmed clean after the newest edits.
- full current-source runtime journey has not yet been completed after the newest edits.
- therefore Sprint 3 is NOT yet technically verified or locked.

---

# 10. SPRINT 3 — CURRENT REMAINING WORK

These are the main items a new chat must verify/fix before declaring Sprint 3 complete.

## A. Live Thaka project collection in POS
Current POS construction has used a read-only snapshot of active projects.

Need to verify/fix:
- creating a New Thaka while New Sale is already open should make it appear in the POS Thaka selector
- settling a project should remove/disable it from active POS selection
- project changes should propagate without recreating the entire app

Preferred direction:
- expose a live/observable active-project projection from `DemoRetailState`
- refresh POS project selection on `StateChanged`

## B. Shared product/stock consistency
Verify all screens use the same `DemoRetailState.Products`.

Must be true:
- POS sale decreases the shared stock after successful completion
- POS Thaka issue decreases the same stock
- Thaka Add Material decreases the same stock
- reopening dialogs must not restore fake stock
- stock quantity remains decimal

## C. Sale completion stock safety
Current sale is recorded through `DemoTransactionService` and stock is reduced in the completion callback.

Verify race/failure safety:
- stock must be validated again at final completion
- a sale must not be recorded successfully and then fail while applying stock
- if needed, move demo stock reservation/commit into one safe transaction boundary

## D. Thaka state propagation
Verify after:
- Add Material
- Record Payment
- Final Settlement

the following refresh immediately:
- project Material Value
- Paid
- Outstanding Balance
- Active/Settled KPI counts
- filters
- workspace banner
- command availability
- POS active project selection

## E. New Thaka continuity
Verify:
- new project is inserted into the shared store
- its material/payment ledgers are created
- Open Workspace works
- it appears in POS active Thaka selection
- it has no seed ledger belonging to another project

## F. Returns and stock
Current return flow tracks return state and disposition, but final shared-stock behavior must be reviewed.

Business intent:
- Sellable/Restock may return to available stock
- Damaged/Defective/Scrap must NOT return to sellable stock

Do not invent final backend inventory/accounting rules. If Sprint 3 only models intent, document the limitation clearly.

## G. Invoice-level discount return rule
This is intentionally unresolved.

Do not guess discount allocation for returns.

Current safe behavior:
- block return when invoice-level discount allocation cannot be calculated safely

Business decision required later:
- how invoice-level discount is distributed per line
- refund policy after discount
- accounting/cost reversal

## H. Encoding / formatting / diff gates
Need final cleanup:
- no mojibake such as `Â·`
- correct UTF-8/line endings
- no trailing whitespace
- `dotnet format --verify-no-changes --no-restore` PASS
- `git diff --check` PASS

## I. Final runtime verification
Must run the current final code, not stale binaries.

Required journey:
Login
→ Dashboard
→ New Sale
→ add/repeat products
→ decimal quantity
→ stock cap
→ discount
→ Complete Sale modal
→ payment
→ Sales History
→ Sale Detail
→ Return
→ Thaka Projects
→ New Thaka
→ Open Workspace
→ Add Material
→ Record Payment
→ Final Settlement
→ settled read-only state

Then verify:
- Light Mode
- Dark Mode
- 1366×768
- 1440×900
- sidebar collapse
- modal/drawer/toast
- keyboard shortcuts where applicable

## J. Windows Event Log
After final runtime test inspect:
- .NET Runtime
- Application Error

for `EdgeRetails.Desktop.exe`.

Do not confuse old known crashes with new failures.

## K. Figma final comparison
Compare final screens against the live verified nodes.

Do not claim Figma parity from code inspection alone.

## L. Final Sprint 3 report
Create/update:
`docs/Sprint3_Implementation_Report.md`

It must separate:
- VERIFIED COMPLETE
- IMPLEMENTED BUT NOT VERIFIED
- NOT IMPLEMENTED
- BLOCKED / NEEDS BUSINESS DECISION
- Known issues
- Sprint 4 handoff

---

# 11. SPRINT 3 COMPLETION GATE

Sprint 3 can be called **TECHNICALLY VERIFIED** only when all are true:

- final source builds Debug
- final source builds Release
- all unit tests pass
- all integration tests pass
- Sprint 3 forensic tests pass
- format verification passes
- git diff check passes
- runtime launches successfully
- required interactions are actually tested
- no new related Windows runtime crash remains
- Light/Dark verified
- 1366×768 verified
- 1440×900 verified
- live Figma comparison performed
- final implementation report written

Even then, do NOT call it LOCKED/FROZEN until the user gives explicit approval.

---

# 12. CRITICAL BUSINESS RULES

## Quantity
Business quantity must support decimal values.

Do not force inventory quantities to `int`.

Examples:
- pieces
- meters
- feet
- rolls
- divisible quantities

## Stock conceptual equation
Opening
+ Purchases
+ Sale Returns
- Local Sales
- Thaka Material
- Purchase Returns
- Damage
± Adjustments
= Current Stock

Not all of this is implemented yet.

## Thaka
Thaka Material Issue is NOT a normal cash sale.

Thaka actions:
- Add Material — Blue semantic
- Record Payment — Teal/Cyan semantic
- Final Settlement — Amber semantic

Settled project:
- remains historical
- becomes read-only
- cannot Add Material
- cannot Record Payment
- cannot silently edit financial state

---

# 13. UNRESOLVED BUSINESS DECISIONS — DO NOT INVENT

Do not invent rules for:
- costing method
- Thaka revenue recognition
- invoice-level discount allocation on returns
- Thaka overpayment
- reopening a settled Thaka
- return profit/cost reversal
- damaged/non-sellable backend stock bucket
- opening stock method
- purchase-return eligibility
- serial/IMEI lifecycle
- warranty lifecycle
- Other Charges allocation
- printer hardware specifics
- taxation/FBR

Use a safe UI boundary and label:
`BLOCKED / NEEDS BUSINESS DECISION`

---

# 14. CURRENT IMPORTANT FILES

Read these first when continuing Sprint 3:

## Architecture / shell
- `App.xaml`
- `App.xaml.cs`
- `Navigation/NavigationTarget.cs`
- `Navigation/PageViewModelFactory.cs`
- `ViewModels/ShellViewModel.cs`

## POS
- `ViewModels/NewSaleViewModel.cs`
- `ViewModels/PosProductItemViewModel.cs`
- `ViewModels/PosCartItemViewModel.cs`
- `Views/NewSaleView.xaml`
- `Views/NewSaleView.xaml.cs`

## Transactions
- `Services/ITransactionService.cs`
- `Services/DemoTransactionService.cs`
- `ViewModels/CompleteSaleViewModel.cs`
- `ViewModels/SalesHistoryViewModel.cs`
- `ViewModels/SaleDetailViewModel.cs`
- `ViewModels/SalesReturnViewModel.cs`

## Shared retail state
- `Services/DemoRetailState.cs`

## Thaka
- `ViewModels/ThakaProjectListItemViewModel.cs`
- `ViewModels/ThakaProjectsViewModel.cs`
- `ViewModels/NewThakaViewModel.cs`
- `ViewModels/ThakaWorkspaceViewModel.cs`
- `ViewModels/ThakaAddMaterialViewModel.cs`
- `ViewModels/ThakaRecordPaymentViewModel.cs`
- `ViewModels/ThakaFinalSettlementViewModel.cs`
- `Views/ThakaProjectsView.xaml`
- `Views/ThakaWorkspaceView.xaml`
- `Views/Dialogs/NewThakaDialog.xaml`
- `Views/Dialogs/ThakaAddMaterialDialog.xaml`
- `Views/Dialogs/ThakaRecordPaymentDialog.xaml`
- `Views/Dialogs/ThakaFinalSettlementDialog.xaml`

## Tests
- `tests/EdgeRetails.UnitTests/Sprint2ForensicAuditTests.cs`
- `tests/EdgeRetails.UnitTests/Sprint3ForensicAuditTests.cs`

## Docs
- `docs/Sprint3_Figma_Forensic_Report.md`
- `docs/Sprint3_Completion_Master_Plan.md`
- `docs/Edge_Retails_Backend_Architecture_Master_v1.md`

---

# 15. SAFETY / RECOVERY

Important backups created during development include:

- `C:\Users\muham\OneDrive\Desktop\EdgeRetails_Sprint3_PreCompletion_20260919_180337`
- older POS crash-fix backup:
  `C:\Users\muham\OneDrive\Desktop\EdgeRetails_POSCrashFix_Backup_20260919`

Do not destroy these.

Git working tree currently contains a large amount of uncommitted/untracked Sprint 1–3 frontend work.

Rules:
- do not run `git reset --hard`
- do not run `git clean`
- do not run destructive checkout/restore
- do not commit/push/freeze without explicit user approval
- inspect `git status` before major work

---

# 16. WORKING RULES FOR A NEW CHAT

A new ChatGPT session should:

1. Read this file completely.
2. Connect to Remote Desktop Commander.
3. Inspect the CURRENT workspace, because files may have changed since this document was written.
4. Run `git status --short`.
5. Re-read files relevant to the requested sprint before editing.
6. Never rely only on an old AI report.
7. Do not claim work is complete unless actually verified.
8. Keep fixes scoped.
9. Rebuild after meaningful code changes.
10. Add regression tests for bugs that were actually found.
11. Never reintroduce the two historical POS DataGrid crashes.
12. Verify Figma node content, not just frame names.
13. Do not let multiple chats edit the same shared files simultaneously without coordination.

---

# 17. EXACT NEXT ACTIONS FOR SPRINT 3

If Sprint 3 is still being completed, continue in this order:

1. Re-read current `DemoRetailState.cs`, `NewSaleViewModel.cs`, `ThakaProjectsViewModel.cs`, `ThakaWorkspaceViewModel.cs`, and all three Thaka action ViewModels.
2. Verify/fix live active-project synchronization in POS.
3. Verify shared product stock across Normal Sale, POS Thaka Issue and Add Material.
4. Verify final-sale stock transaction safety.
5. Verify New Thaka immediately appears everywhere it should.
6. Verify settlement immediately removes/locks the project where appropriate.
7. Review return disposition vs demo stock behavior.
8. Run Debug build.
9. Run all tests.
10. Run Release build.
11. Run format verification.
12. Run `git diff --check`.
13. Run the entire manual/runtime Sprint 3 journey.
14. Check Windows Event Log.
15. Compare live UI to Figma.
16. Write `docs/Sprint3_Implementation_Report.md`.
17. Ask the user for final visual/manual approval.
18. Only after approval may Sprint 3 be frozen/committed.

---

# 18. NEW CHAT STARTER

Recommended message to the next ChatGPT session:

> Read `C:\Users\muham\OneDrive\Desktop\Point of Sale\EDGE_RETAILS_FRONTEND_PROJECT_BRAIN.md` completely first. Then connect to Remote Desktop Commander, inspect the current workspace without assuming the document is perfectly current, tell me the present Sprint 3 state, and continue from the exact next unfinished step. Do not redo completed work and do not use destructive Git commands.

---

# 19. STATUS SUMMARY

Sprint 1:
**IMPLEMENTED**

Sprint 2:
**IMPLEMENTED; known POS runtime crashes fixed and regression-protected**

Sprint 3:
**MAJOR FEATURES IMPLEMENTED; shared-state integration and final forensic verification still require completion**

Sprint 4:
**DO NOT CODE YET; planning/read-only analysis is acceptable**

Backend:
**Architecture document exists; real persistence is not the same thing as current demo frontend state**

The project is not to be called production-ready yet.

---

## END OF PROJECT BRAIN
Update this file whenever a major sprint is completed, architecture changes, a critical bug is found/fixed, or the next-step roadmap changes.
