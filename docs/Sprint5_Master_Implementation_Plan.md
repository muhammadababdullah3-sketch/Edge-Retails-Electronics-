# Edge Retails — Sprint 5 Master Implementation Plan

**Date:** 2026-09-19
**Status:** COMPLETE — FORENSICALLY CLOSED
**Scope:** Final operational frontend screens 13–17
**Backend attachment:** DEFERRED until overall frontend completion
**Primary rule:** WPF frontend only. Temporary demo/state adapters are allowed; no direct DB/API coupling.

## Live Figma authority

Phase 1:
- Expenses: `14:7621`
- Expense content: `14:7727`
- Add Expense: `14:7901`
- Expense form: `14:8192`
- Edit Expense: `15:17108`
- Customers: `14:8267`
- Customer content: `14:8373`
- Customer information drawer: `14:8725`
- Customer detail content: `14:8950`
- Add Customer: `14:8999`
- Customer form: `14:9225`
- Suppliers: `14:9280`
- Supplier content: `14:9386`
- Supplier information drawer: `14:9493`
- Supplier detail content: `14:9702`
- Add Supplier: `14:9750`
- Supplier form: `14:9960`

Phase 2:
- Reports: `14:10022`

Phase 3:
- Shop Settings: `14:10297`
- Receipt Settings: `14:10500`
- Users & Access: `14:10966`
- Categories & Units: `14:11379`
- Add Category: `14:11752`
- Add Unit: `14:12149`
- Backup Settings: `14:12554`
- Restore Backup: `14:12812`
- License Settings: `14:13141`
- Import New License: `14:13347`
- Appearance Settings: `14:13589`
- Database Settings: `14:13782`

## Three-phase execution

### Phase 1 — Expenses + Customers + Suppliers
- Replace sidebar placeholders with real WPF screens.
- Shared frontend business-state service for expenses and business contacts.
- Expenses: Today / This Week / This Month, category filter, KPIs, add/edit modal.
- Expense fields: Category, Subcategory, Amount, Date, Payment Method, Staff Member, Note.
- Customers: search name/phone/project, Local Sales, Active Thaka, Last Sale.
- Add/Edit Customer modal: Name, Phone, Address, Notes.
- Customer information remains a right-side drawer.
- Suppliers: search supplier/phone/city, Total Purchases, Last Purchase, City.
- Add/Edit Supplier modal: Supplier Name, Phone, City, Address, Notes.
- Supplier information remains a right-side drawer.
- Customer metrics derive from existing frontend sales/Thaka state where possible.
- Supplier purchase summaries derive from existing frontend purchase state.
- No backend attachment.

### Phase 2 — Reports
- One Reports primary screen only.
- Daily / Monthly / Yearly tabs.
- Follow the latest locked backend reporting contract: Moving Weighted Average for quantity/length costing and sale-time cost snapshots for historical profit.
- Net Local Sales = completed local sales minus successful sale refunds in the selected report period.
- Gross Profit = Net Local Sales - Net Local COGS.
- Net Profit = Gross Profit - Operating Expenses.
- Thaka material revenue is recognized at material issue time but remains visually and analytically separate from Local Sales.
- Daily KPI set: Net Local Sales, Gross Profit, Expenses, Net Profit, Purchases, Thaka Material.
- Daily averages: Average Invoice Value, Average Gross Profit per Invoice, Sales Count, Average Items per Invoice.
- Daily graph: Hourly Sales + Hourly Gross Profit.
- Monthly KPI set uses the same six financial cards.
- Monthly averages: Average Daily Sales, Average Daily Net Profit, Average Daily Expenses, Average Invoice Value.
- Monthly graph: Daily Sales + Daily Net Profit + Daily Expenses.
- Monthly includes Expense Category Breakdown and separate Thaka Activity.
- Yearly metrics: Total Net Sales, Net Profit, Expenses, Thaka Material.
- Yearly averages: Average Monthly Sales, Average Monthly Net Profit, Average Monthly Expenses.
- Yearly graph: Jan-Dec Sales + Net Profit + Expenses.
- Missing chart periods are zero-filled and daily averages include zero-sale calendar days.
- Reports are read-only and use current frontend ledgers/state until the separately designed backend query layer is attached.
- No direct DB, Dapper, HTTP, API or reporting-server coupling is introduced in the frontend phase.

### Phase 3 — Settings
- One Settings primary screen with internal settings navigation.
- Shop, Receipt, Users & Access, Categories & Units, Backup, License, Appearance, Database.
- Categories/Units add/edit/deactivate; no permanent delete when referenced.
- Owner/Manager/Cashier permission matrix based on canonical spec.
- Backup/License/Database sections are frontend status/configuration shells until backend/worker integration.
- Appearance uses existing theme service.
- No PostgreSQL credentials exposed in normal user UI.

## Business boundaries preserved
- Purchases are not Expenses.
- Local Sales and Thaka stay separate.
- Inventory Value remains hidden in the V1 frontend even though Moving Weighted Average costing is now locked.
- Reports use the locked Moving Weighted Average / historical cost-snapshot rules and must not invent alternative FIFO-style calculations.
- Backend remains separately designed and attaches after frontend completion.
- Supporting actions remain dialogs/drawers, not new primary screens.
- The canonical application remains exactly 17 full screens.

## Verification per phase
- Figma node read before implementation.
- Debug build with 0 warnings/errors.
- Automated tests.
- Release build.
- Formatter verification.
- No Canvas/web/React production residue.
- Manual visual QA may remain deferred separately.
- Git cleanup/commit/freeze remains deferred unless explicitly requested.

## Progress Update — Phase 1 Complete (2026-09-19)

**Status:** COMPLETE at automated code/integration level.
**Manual visual QA:** deferred.
**Backend attachment:** deferred until overall frontend completion.

Completed:
- Expenses sidebar placeholder replaced with real WPF screen.
- Customers sidebar placeholder replaced with real WPF screen.
- Suppliers sidebar placeholder replaced with real WPF screen.
- Expense Today / This Week / This Month filtering and category filtering.
- Expense Today, This Month and Top Category KPIs.
- Add/Edit Expense modal using the live Figma field contract.
- Customers search across name / phone / active project.
- Customer Local Sales derived from the existing frontend sales ledger.
- Customer Active Thaka derived from the existing frontend Thaka state.
- Customer Last Sale derived from the existing frontend sales ledger.
- Add/Edit Customer modal.
- Customer information right-side drawer.
- Suppliers search across supplier / phone / city.
- Supplier Total Purchases and Last Purchase derived from existing frontend purchase history.
- Add/Edit Supplier modal.
- Supplier information right-side drawer.
- Newly added supplier names flow into the New Purchase supplier source.
- Phase 1 page ViewModels are cached by the page factory to prevent duplicate singleton-event subscriptions on repeated navigation.
- No direct backend/database/API coupling introduced.

Verification:
- Formatter verify-no-changes: PASS.
- Debug build: PASS, 0 warnings, 0 errors.
- Debug tests: 60 unit + 1 integration PASS.
- Release build: PASS, 0 warnings, 0 errors.
- Release tests: 60 unit + 1 integration PASS.
- Runtime Phase 1 state smoke: PASS.
- Duplicate customer phone guard verified during runtime smoke.
- Git untouched.

Next active phase: **Phase 2 — Reports**.

## Progress Update — Phase 2 Complete (2026-09-20)

**Status:** COMPLETE at automated code/integration level.
**Manual visual QA:** deferred.
**Backend executable attachment:** deferred until overall frontend completion.
**Git:** untouched.

Completed:
- Reports sidebar placeholder replaced with the real WPF Reports screen.
- Live Figma Reports frame `14:10022` / content `14:10128` used as visual authority.
- Daily / Monthly / Yearly report modes.
- Daily date selector, Monthly month/year selectors, Yearly year selector.
- Net Local Sales, Gross Profit, Expenses, Net Profit, Purchases and Thaka Material metrics.
- Yearly Net Sales, Net Profit, Expenses and Thaka Material summary.
- Daily Average Invoice Value, Average Gross Profit per Invoice, Sales Count and Average Items per Invoice.
- Monthly Average Daily Sales, Average Daily Net Profit, Average Daily Expenses and Average Invoice Value.
- Yearly Average Monthly Sales, Average Monthly Net Profit, Average Monthly Expenses and Sales Count.
- Analytical WPF graph control implemented without Canvas or a third-party chart package.
- Daily graph: Hourly Sales + Hourly Gross Profit.
- Monthly graph: Daily Sales + Daily Net Profit + Daily Expenses.
- Yearly graph: Jan-Dec Sales + Net Profit + Expenses.
- Monthly Expense Category Breakdown.
- Monthly Thaka Activity kept separate from Local Sales.
- Missing hours/days/months zero-filled.
- Sale returns reduce report-period revenue.
- RESTOCK_SELLABLE returns reverse original cost snapshot COGS.
- DAMAGED / DEFECTIVE / SCRAP returns do not restore sellable COGS.
- Sale items now capture historical Unit Cost Snapshot.
- Frontend purchase cost state aligned to landed-cost allocation + Moving Weighted Average.
- Purchase base invoice cost remains separate from landed/effective cost, preventing Other Charges from double counting.
- Reports page ViewModel cached to avoid duplicate singleton subscriptions.
- Dashboard remains chart-free.
- No direct DB, Dapper, HTTP/API or analytics-server coupling introduced.

Verification:
- Formatter verify-no-changes: PASS.
- Debug build: PASS, 0 warnings, 0 errors.
- Debug tests: 72 unit + 1 integration PASS.
- Release build: PASS, 0 warnings, 0 errors.
- Release tests: 72 unit + 1 integration PASS.
- Runtime reporting smoke: PASS.
- Runtime Daily snapshot: Net Sales Rs. 37,050; Gross Profit Rs. 8,670; Expenses Rs. 11,700; Net Profit Rs. -3,030.
- Runtime trend lengths: Daily 24 hours; Monthly all calendar days; Yearly 12 months.
- Runtime purchase MWA smoke: product cost moved from Rs. 350 to Rs. 356.52 after landed-cost purchase intake, with Purchase Subtotal Rs. 4,000 and Total Rs. 4,100, confirming Other Charges were not double-counted.

Next active phase: **Phase 3 — Settings**.

## Progress Update — Phase 3 Complete (2026-09-20)

**Status:** COMPLETE at automated code/integration level.
**Manual visual QA:** deferred.
**Backend/worker attachment:** deferred until frontend completion/backend integration.
**Git:** untouched.

Completed:
- Settings placeholder replaced with one real WPF Settings primary screen.
- Eight internal sections: Shop, Receipt, Users & Access, Categories & Units, Backup, License, Appearance, Database.
- Shop and receipt configuration frontend state.
- Add/Edit user workflow with Owner/Manager/Cashier roles.
- Last-active-Owner safety rule.
- Masked transient PIN input; no persistent plaintext PIN in frontend state.
- Canonical permission matrix.
- Add/Edit/Deactivate/Reactivate Categories and Units.
- No permanent Category/Unit delete path.
- Category rename migrates referenced product category strings.
- Unit rename migrates name/symbol/plural aliases including existing Pcs products.
- Backup/Restore UI with confirmation shell.
- License import file picker and frontend validation shell.
- Light/Dark/System wired to existing theme service.
- Database diagnostics shell with no credentials exposed.
- Deferred backend/worker/license statuses explicitly labeled Preview / Integration Pending.

Verification before final forensic gate:
- Debug build: PASS, 0 warnings, 0 errors.
- Debug tests: 88 unit + 1 integration PASS.
- Phase 3 runtime smoke: PASS.
- Forensic runtime smoke: PASS.

## Sprint 5 Forensic Closure (2026-09-20)

Full report: `docs/Sprint5_Forensic_Audit_Report.md`

Resolved findings:
1. Deferred backend systems falsely appearing live.
2. Visible PIN field.
3. Category rename reference split.
4. Piece/pc/Pcs unit alias migration failure.
5. License arbitrary-path workflow.
6. Invalid Auto-style WPF margin layout.
7. Stale Appearance theme selector.
8. Outdated costing-policy wording in Sprint 5 plan.

No production TODO/FIXME/NotImplemented residue found.
No direct Npgsql/DbContext/Dapper/connection-string frontend coupling found.
No Canvas/React/web production residue found in audited Sprint 5 surfaces.

All discovered Sprint 5 in-scope forensic findings are resolved.
Remaining setup/permission/missing-state/responsive/theme hardening moves to Sprint 6.

## Authoritative Sprint 5 Final Gate

- Formatter verify-no-changes: PASS.
- Debug: 0 warnings, 0 errors; 88 unit + 1 integration PASS.
- Release: 0 warnings, 0 errors; 88 unit + 1 integration PASS.
- Forensic runtime smoke: PASS.
- Sprint 5 is forensically closed at automated level.
- Manual visual QA and Git remain deferred.
