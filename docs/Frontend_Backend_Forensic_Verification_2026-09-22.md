# Edge Retails Frontend vs Backend Architecture
## Deep Forensic Verification Report

**Date:** 2026-09-22  
**Workspace:** `C:\Users\muham\OneDrive\Desktop\Point of Sale`  
**Audit mode:** Read-only source inspection; only this requested report file was created.  
**Canonical authority:** `docs/Edge_Retails_Final_Architecture_Report_v1.md`  
**Authority manifest:** `docs/Architecture_Authority_Manifest.json`  
**Navigation authority:** `V1-19-SCREENS-SECTION-209`

> This report verifies the live Desktop frontend against the current canonical backend architecture.
> Historical frontend specs, sprint notes and tests are treated as evidence only where they do not conflict with the canonical architecture.

## Verification Boundary

The audit inspected canonical architecture, Desktop navigation/composition, ViewModels, backend adapters,
Application handlers, stale frontend documentation and structural/unit tests.
No implementation source file was edited.
No migration was applied.
No build or test command was executed because the requested audit was read-only and those commands can write `bin/obj` or temporary artifacts.

Therefore:
- source/architecture contradictions below are **CONFIRMED**;
- runtime behavior not directly provable from source is marked as requiring execution evidence;
- this report does not claim a green build, green CI, or successful hardware/device workflow.
## Executive Verdict

**Current frontend is NOT aligned with the 2026-09-22 canonical backend architecture.**

The dominant pattern is architectural lag rather than a completely missing frontend:
the Desktop application has real backend adapters for several established flows,
but they are attached to an older 17-screen frontend skeleton while the canonical backend now requires exactly 19 full screens
and materially richer POS, Product, Inventory, Supplier Khata, Warranty, identity and operational semantics.

The application is currently a **hybrid production shell**:
- backend startup and identity are substantially real;
- several ordinary CRUD/reporting flows use backend services;
- several production-facing surfaces still use demo state, preview behavior, or block canonical operations;
- some old tests actively encode superseded frontend assumptions.

This creates a larger risk than a clearly labeled prototype because an operator can move between authoritative and non-authoritative surfaces inside the same shell.

## Severity Legend

- **CRITICAL**: can split source-of-truth, omit a mandatory V1 operational surface, or present non-authoritative production state.
- **HIGH**: canonical contract is materially incomplete or recovery/security/identity semantics are weakened.
- **MEDIUM**: resource, time, UX consistency, or maintainability issue that can become operationally significant.
- **POSITIVE CONTROL**: verified implementation that already points in the correct architectural direction.
# 1. Canonical Authority Is Unambiguous

The manifest declares:
- architecture version: `EdgeRetails-Backend-V1-2026-09-22-InternationalAuditRemediated`;
- canonical file: `docs/Edge_Retails_Final_Architecture_Report_v1.md`;
- navigation contract: `V1-19-SCREENS-SECTION-209`;
- international remediation authority: Section 233.1.

Section 209 explicitly supersedes the previous navigation contract and requires exactly 19 full screens:
1. First Setup / License
2. Login / User Switch
3. Dashboard
4. POS
5. Sales History
6. Sale Detail
7. Thaka / Projects
8. Thaka Workspace
9. New Purchase
10. Purchase History
11. Product Management
12. Product Detail
13. Inventory
14. Expenses
15. Customers
16. Suppliers
17. Warranty
18. Reports
19. Settings

It also explicitly states that **New Sale is renamed to POS everywhere**, Product Management and Inventory are separate surfaces,
Warranty is dedicated, and Supplier Khata stays inside Suppliers.
# 2. CRITICAL - Live Navigation Still Implements the Superseded Frontend Contract

**Evidence**
- `src/EdgeRetails.Desktop/Navigation/NavigationTarget.cs` contains `NewSale`, not `POS`.
- It has no `ProductManagement` target.
- It has no `Warranty` target.
- `src/EdgeRetails.Desktop/ViewModels/ShellViewModel.cs` creates a sidebar item titled `New Sale`.
- Shell navigation has no Product Management or Warranty item.
- `PageViewModelFactory.cs` has no Product Management or Warranty composition path.

Search verification across `src/EdgeRetails.Desktop` returned zero `ProductManagement` matches and zero `Warranty` matches.

**Canonical conflict**
Section 209 requires POS, Product Management, and Warranty as part of the 19-screen V1 contract.

**Verdict:** CONFIRMED RELEASE-BLOCKING ARCHITECTURAL DRIFT.

This is not merely a label mismatch.
Two mandatory V1 management surfaces are absent and the POS surface remains modeled through the old NewSale identity.
# 3. CRITICAL - POS Thaka Mode Bypasses the Backend Authority

**Evidence**
`src/EdgeRetails.Desktop/ViewModels/NewSaleViewModel.cs` owns:
- `private readonly DemoRetailState _retailState = DemoRetailState.Instance;`
- Thaka project data sourced from that demo state.
- `IssueMaterialToThaka()` calls `_retailState.IssueMaterialBatch(...)`.

Exact search evidence places `IssueMaterialBatch` at approximately lines 536-538.

At the same time, `BackendThakaService.cs` exists and calls the real Application handlers.

**Risk**
A production session can use real backend identity/catalog/normal-sale infrastructure while the POS Thaka action mutates in-memory demo state.
That is a source-of-truth split.

**Canonical conflict**
Authoritative Thaka material mutation must flow through existing backend handlers, locking, identity and audit rules.

**Verdict:** CONFIRMED CRITICAL.
Production POS must never silently mutate `DemoRetailState` for authoritative business activity.
# 4. CRITICAL - Serialized / Exact-Unit Frontend Is Not Operationally Complete

The backend has exact-unit support and enforcement, but the Desktop workflow does not expose the required complete operator path.

**Verified backend capability**
- POS draft handler supports `SelectedInventoryUnitId`.
- serialized draft lines require exact physical-unit selection.
- inventory adjustment handlers enforce Serial/IMEI identity and exact InventoryUnit rules.
- purchase/warranty/thaka backend architecture already defines traceability and exact-unit behavior.

**Verified Desktop gaps**
- Desktop-wide search returned zero `TrackingCode` matches.
- `NewSaleViewModel.ValidateCartStockForCommit()` blocks serialized backend products with:
  `Serialized unit selection is required...`
- `BackendPurchasingInventoryService.CreatePurchaseAsync()` explicitly rejects serialized purchases before saving.
- serialized purchase return is explicitly rejected until exact-unit selection exists.
- `BackendThakaService` rejects serialized Thaka issue because exact-unit selection is missing.

**Verdict:** CONFIRMED CRITICAL.
The backend identity model exists, but the operator cannot complete the canonical end-to-end serialized workflows through the Desktop UI.
# 5. CRITICAL - Product Management Is Missing and Inventory Owns the Wrong Responsibilities

Sections 192-204 permanently separate Product Management from Inventory Management.

**Canonical Product authority**
SKU, name/brand/model, category, base/product units, tracking policy, selling price,
minimum stock, catalog/warranty attributes, active state and SupplierProduct links.

**Canonical Inventory authority**
stock balances, lots, physical units, movements, condition buckets, cost/provenance,
stocktake, warranty effects and exact physical-item history.

**Live frontend evidence**
- no Product Management screen/target exists;
- `InventoryViewModel` opens Product Detail and exposes Add Product;
- production Add Product is blocked with `Product creation backend flow is not attached...`;
- `ProductDetailViewModel` exposes Edit Product inside the old Inventory composition;
- production Product Edit and Stock Adjustment are explicitly blocked.

**Verdict:** CONFIRMED CRITICAL.
The current UI stretches the old Inventory surface instead of implementing the canonical Product/Inventory boundary.
# 6. CRITICAL - Supplier Khata / Accounts Payable Frontend Is Missing

Section 215 states Supplier Accounts Payable is no longer deferred.
Sections 216-220 define an append-only Supplier Khata/subledger and operational read/write flows.

**Backend evidence**
`src/EdgeRetails.Application/Features/Finance/SupplierAccountHandlers.cs` contains
`CreateSupplierPaymentHandler` and the Supplier account command infrastructure.

**Desktop evidence**
- Desktop-wide search returned zero `SupplierPayment` matches.
- `IBackendBusinessOperationsService` exposes supplier list/save only.
- `SuppliersViewModel` provides supplier CRUD/list/detail editing, not Khata operations.
- no payment/advance/refund/account statement adapter is exposed by the current Desktop business service.

**Canonical supplier workspace additionally requires**
payable/credit truth, payments, advances, refunds, purchases/returns, products supplied, and warranty context.

**Verdict:** CONFIRMED CRITICAL.
The canonical backend feature exists but is not surfaced through the Desktop application.
# 7. CRITICAL - Dedicated Warranty Screen Is Completely Absent

Section 221 makes Warranty a dedicated full operational screen.

Required modes include:
- Customer Warranty Claims
- Shop Stock Warranty Cases
- All

Required queues/read models include open claims, with-supplier, ready-for-customer,
claim detail, shop-stock cases, supplier warranty summaries and physical-item history.

**Backend evidence**
`src/EdgeRetails.Application/Features/Warranty/WarrantyHandlers.cs` exists and Desktop-external backend tests exercise Warranty domain/schema behavior.

**Desktop evidence**
A full Desktop content search for `Warranty` returned zero matches.
There is no Warranty View, ViewModel, navigation target, page factory route, or permission mapping.

**Verdict:** CONFIRMED CRITICAL.
This is a missing mandatory V1 operational surface, not a cosmetic omission.
# 8. CRITICAL - Dashboard Displays Demo Operational Truth in Production Composition

`DashboardViewModel.cs` documents itself as using Figma-exact demo data.

Verified defaults include:
- database connected = `true`;
- backup up-to-date = `true`;
- Today Sales = `Rs. 84,500`;
- Today Profit = `Rs. 13,400`;
- Expenses = `Rs. 3,200`;
- Low Stock = `12 Items`;
- hardcoded Thaka KPIs.

The constructor calls `LoadDemoData()` (search evidence around line 106),
and refresh calls the same method again.

`PageViewModelFactory` creates Dashboard without a backend dashboard read service.

**Risk**
A successfully authenticated production operator can see non-authoritative financial/health information presented as current shop truth.

**Verdict:** CONFIRMED CRITICAL.
Production dashboard values and health indicators must come from authoritative reads/diagnostics or be explicitly unavailable, never optimistic demo defaults.
# 9. CRITICAL - Settings Is Still Predominantly a Demo/Preview Authority

`SettingsViewModel.cs` declares:
`private readonly DemoSettingsState _state = DemoSettingsState.Instance;`
(search evidence around line 267).

Verified preview-only behavior includes:
- Shop settings persisted to demo state.
- Receipt settings persisted to demo state.
- Users/Categories/Units use demo state.
- License import validates local file shape but says backend licensing is not attached.
- Backup says command preview only.
- Restore says backend restore is not attached.
- Diagnostics says backend health provider is not attached.
- Database/worker/license displays are sourced from demo state.

Section 50 also separates global receipt-template authority from local workstation printer configuration.

**Verdict:** CONFIRMED CRITICAL.
Theme preference may remain client-local, but authoritative configuration, licensing, diagnostics, users/permissions and backup/restore cannot use demo state in production.
# 10. HIGH - POS Workspace Contract Is Far Behind Sections 210-214

Canonical POS is not merely a cart form.
It owns:
- universal search;
- exact physical-unit scan;
- Price Check;
- Draft/Hold;
- resume/recent/cancel drafts;
- quotation loading;
- customer quick search;
- controlled price override;
- exact TrackingCode/Serial/IMEI scan;
- final completion through CompleteSale.

**Desktop verification**
Search across Desktop returned:
- `SavePosDraft`: zero matches;
- `PriceCheck`: zero matches;
- `TrackingCode`: zero matches.

Meanwhile `SavePosDraftHandler` physically exists in Application.

**Verdict:** CONFIRMED HIGH.
The correct implementation strategy is a POS workspace refactor, not a series of cosmetic patches to the old NewSale surface.
# 11. HIGH - Stable Error Codes Are Collapsed into English Exception Messages

Section 233.1(O) requires stable machine-readable error codes and forbids UI logic from depending on localized/human message text.

Current Desktop backend adapters repeatedly do:
`result.Error?.Message`
then throw `InvalidOperationException`.

Verified examples exist in:
- `BackendTransactionService`;
- `BackendPurchasingInventoryService`;
- `BackendBusinessOperationsService`;
- `BackendThakaService`;
- `BackendIdentityService`;
- `BackendSetupService`.

ViewModels then commonly show `ex.Message` directly.

**Impact**
The client loses reliable classification for conflict, stale state, retryability,
permission failure, exact-unit selection, unknown outcome, and localized operator messaging.

**Verdict:** CONFIRMED HIGH.
Desktop needs a typed operation/error envelope preserving `Error.Code`, safe parameters, correlation identity and recovery semantics.
# 12. HIGH - ClientOperationId Ownership Is at the Wrong Layer

Section 225 and Section 233.1 require the same operation identity to survive unknown outcomes and retries.

Desktop-wide search returned zero explicit `ClientOperationId` references.

Verified adapter behavior:
- `BackendTransactionService` creates a fresh `Guid.CreateVersion7()` inside CompleteSaleCommand around line 101.
- Sale return creates another fresh operation id around line 273.
- purchasing/business adapters similarly generate operation IDs at submission time.

**Risk**
If a command commits but the response is lost, a UI retry can create a new operation identity instead of resolving/replaying the original intent.

**Verdict:** CONFIRMED HIGH ARCHITECTURAL WEAKNESS.
Operation identity should be created and retained at the user-intent/workflow boundary until the outcome is known.
# 13. HIGH - Production Printing Infrastructure Exists but Sale Workflow Does Not Use It

Positive evidence:
`src/EdgeRetails.Desktop/Production/Printing/WpfProductionPrintEngine.cs`,
printer profile validation and a production preview window exist.

However exact Desktop search for `WpfProductionPrintEngine` finds only its declaration,
not an application composition/injection usage.

`SaleDetailViewModel.PrintReceipt()` currently shows:
`Printer integration is deferred to Sprint 8`
(search evidence around line 427).

**Verdict:** CONFIRMED HIGH.
This is primarily a wiring/composition/document-generation gap, not a need to rebuild the print engine from zero.

The final flow must also obey Section 233.1 external-side-effect certainty:
printing can become CONFIRMED, OUTCOME_UNKNOWN, or ACTION_REQUIRED without falsifying business completion.

# 14. HIGH - Permission Mapping Is Still Based on the Old Surface Model

`FrontendPermissionService.cs` maps old `NavigationTarget.NewSale`/Inventory/Supplier surfaces,
with no Product Management or Warranty target.
Section 224 requires granular POS, draft, price-check, Supplier account and Warranty permissions.

**Verdict:** CONFIRMED HIGH.
# 15. HIGH - Old Tests Encode Superseded Architecture

`tests/EdgeRetails.UnitTests/Sprint6Phase3ForensicAuditTests.cs`
contains `Phase3_DesktopStillHasExactlySeventeenPrimaryViewFiles`
and explicitly executes `Assert.Equal(17, expected.Length)` around line 192.

The canonical architecture requires 19 screens.

Older Sprint 3 forensic tests also assert demo-state POS relationships such as
`DemoRetailState.Instance` and `_retailState.IssueMaterialBatch`.

**Verdict:** CONFIRMED HIGH.
Some old tests are now historical constraints rather than valid architecture guards.

# 16. HIGH - Frontend Documentation Is Stale

`EDGE_RETAILS_FRONTEND_PROJECT_BRAIN.md` still declares a 17-screen primary map.
`docs/Reference/Edge_Retails_Frontend_Master_Spec_v1.1_Corrected.md` still declares a canonical full-screen count of 17.

Section 233.1(A) makes the current canonical architecture the only narrative business authority.

**Verdict:** CONFIRMED HIGH DOCUMENTATION DRIFT.
These files must be explicitly superseded or rebuilt before further architecture-driven frontend implementation.

# 17. MEDIUM/HIGH - Reports Request Lifecycle Has Race and Cancellation Gaps

`ReportsViewModel.Refresh()` uses fire-and-forget `_ = RefreshBackendAsync()`
around lines 76 and 221.
No operation-level cancellation/generation guard is visible.

Section 233.1(G) requires long-running reports to propagate cancellation and release DB resources promptly.

**Risk:** rapid period changes can overlap requests and allow stale response overwrite.
# 18. MEDIUM - Business Date / Workstation Clock Boundary Needs Consolidation

Canonical finance uses authoritative `BusinessDate` derived from Shop Time Zone.
Terminal/workstation clock is metadata, not business authority.

Desktop code still contains direct `DateTime.Today` / `DateTime.Now` usage in production-facing paths,
including transaction/day filtering and Thaka project date construction.

This proves the frontend lacks one explicit shop-business-date boundary.

# 19. MEDIUM - First Setup License Presentation Is Partial

`FirstSetupViewModel` marks license state as:
`File selected · Verification pending`
and Ready state remains `Verification Pending`.

Selection validates file existence and extension before moving on.
Backend shop bootstrap is real, but license authority/presentation remains incomplete.

# 20. Verified Positive Controls

The audit also found implementation that should be preserved:
1. Production startup fails closed when backend configuration/readiness fails.
2. Startup checks DB readiness/pending migrations.
3. Backend login uses persistent users, sessions and permissions.
4. User switch/sign-out supports backend session termination.
5. Normal non-serialized Sale completion reaches `CompleteSaleHandler`.
6. Sales History/Sale Detail have backend read paths.
7. Purchase History and ordinary non-serialized purchase flows have backend adapters.
8. Expenses, Customer CRUD, basic Supplier CRUD and Reports have backend paths.
9. Dedicated `BackendThakaService` exists.
10. Production WPF printing infrastructure exists.
11. Missing POS/Supplier/Warranty backend command infrastructure is already substantially present.
# 21. Root-Cause Classification

The primary root cause is **frontend architecture staleness after backend architecture expansion**.

The frontend was originally shaped around a 17-screen design.
Later canonical changes added:
- Product Management as a separate authority/screen;
- Warranty as a separate operational screen;
- Supplier Khata/AP inside Suppliers;
- POS as a richer draft/scan/price-check workspace;
- exact physical-unit/TrackingCode workflows;
- stronger idempotency/error/time/diagnostic rules.

New backend adapters were then attached progressively to the old skeleton.

This explains the observed hybrid:
some screens are live, some operations are blocked, some flows still use demo state,
backend handlers exist without Desktop consumers, and old tests/docs still describe the superseded model.

# 22. Recommended Remediation - Six Long-Run Phases

## Phase 1 - Authority and Navigation Rebase
- reconcile/supersede stale frontend brain/master spec;
- replace 17-screen tests with canonical 19-screen architecture tests;
- rename New Sale identity to POS;
- add Product Management and Warranty composition/navigation;
- update permission surface mapping.

**Exit gate:** one canonical 19-screen contract only.
## Phase 2 - Eliminate Production Demo Authority
- replace Dashboard demo truth with authoritative reads;
- replace authoritative Settings demo state with real providers;
- remove POS Thaka `DemoRetailState` mutation;
- isolate Demo* providers to explicit debug/design preview composition.

**Exit gate:** production business state never comes from Demo* singleton authority.

## Phase 3 - Product, Inventory and Traceability Vertical
- implement Product Management/Product Detail under catalog authority;
- make Inventory stock/provenance/physical-item authority only;
- wire ProductUnit/SupplierProduct management;
- implement physical-item history and TrackingCode/Serial/IMEI exact search;
- expose canonical inventory buckets/value projections;
- wire authorized stocktake/adjustment exact-unit flows.

**Exit gate:** Product CRUD cannot mutate stock; Inventory cannot create Product; serialized identity is end-to-end operational.

## Phase 4 - POS and Purchase Exact-Unit Vertical
- POS Draft/Hold/Resume/Cancel;
- Price Check and universal scanner precedence;
- quotation load/customer quick search;
- serialized POS exact-unit picker;
- serialized Purchase intake;
- exact-unit Purchase Return;
- serialized Thaka issue;
- controlled price override/discount.

**Exit gate:** no canonical POS/Purchase flow is blocked because exact-unit UI is absent.
## Phase 5 - Supplier Khata and Warranty Vertical
- Supplier Khata summary/chronological statement;
- payments, advances, refunds and reversals;
- supplier product/warranty context;
- dedicated Warranty screen and queues;
- claim/case detail and custody transitions;
- replacement, handover, supplier send/receive and monetary credit;
- preserve exact-unit provenance and backend concurrency.

**Exit gate:** Sections 215-225 are reachable through Desktop without duplicate truth.

## Phase 6 - Production Hardening and Release Gate
- stable UI error-code/localization boundary;
- workflow-owned ClientOperationId and unknown-outcome recovery;
- Shop BusinessDate/time-zone provider;
- production print wiring/outcome certainty;
- real license/backup/restore/diagnostics;
- report cancellation/stale-response protection;
- read/performance review;
- canonical architecture + integration/runtime evidence.

**Exit gate:** no stale architecture guard or preview/deferred production blocker remains.

# 23. Mandatory Regression Tests to Add/Replace
- exactly 19 canonical full screens;
- Product Management/Inventory separation;
- Warranty full screen;
- no canonical `New Sale` identity;
- no production POS Thaka `DemoRetailState`;
- no production Settings/Dashboard Demo* authority;
- POS Draft/PriceCheck/UniversalSearch adapters;
- exact-unit Sale/Purchase Return/Thaka;
- Supplier Khata adapters;
- stable error-code propagation;
- retained ClientOperationId across retry/outcome resolution;
- production receipt reaches print orchestration;
- superseded report refresh is cancelled/invalidated;
- no stale 17-screen assertions.
# 24. Final Forensic Conclusion

The earlier audit is **substantially verified**.
The most severe findings survived direct re-inspection and several were strengthened by exact source evidence.

The frontend is operating on a **superseded presentation architecture**
while the backend has already moved to a richer canonical V1 model.

The safest next move is to rebase the frontend around Sections 209-225 and 233.1,
then attach the already-existing backend capabilities through one coherent presentation/adaptor boundary.

**Release posture from source evidence: NOT READY for canonical V1 production sign-off.**

This is an architecture/source verdict only.
A later implementation pass must still run build, unit, architecture, integration, migration and device/hardware gates.

# 25. Evidence Index

Primary evidence includes:
- `docs/Architecture_Authority_Manifest.json`
- `docs/Edge_Retails_Final_Architecture_Report_v1.md`
- `EDGE_RETAILS_FRONTEND_PROJECT_BRAIN.md`
- `docs/Reference/Edge_Retails_Frontend_Master_Spec_v1.1_Corrected.md`
- Desktop navigation/page factory/shell;
- POS, Dashboard, Inventory, Product Detail, Suppliers, Settings, Reports, Sale Detail and Setup ViewModels;
- Backend Transaction/Purchasing/Business/Thaka/Permission services;
- Application POS Draft, Supplier Account and Warranty handlers;
- Sprint 3 / Sprint 6 frontend forensic tests.

Key exact searches:
- Desktop ProductManagement: 0
- Desktop Warranty: 0
- Desktop SavePosDraft: 0
- Desktop PriceCheck: 0
- Desktop TrackingCode: 0
- Desktop SupplierPayment: 0
- Desktop ClientOperationId: 0
- Application SavePosDraftHandler: present
- Application CreateSupplierPaymentHandler: present
- Application Warranty implementation: present

---
**End of forensic verification report.**
