# Sprint 9 Master Implementation Roadmap
## Canonical Frontend ↔ Backend Reconciliation & V1 Production Readiness

**Date:** 2026-09-22  
**Workspace:** `C:\Users\muham\OneDrive\Desktop\Point of Sale`  
**Primary forensic source:** `docs/Frontend_Backend_Forensic_Verification_2026-09-22.md`  
**Canonical authority:** `docs/Edge_Retails_Final_Architecture_Report_v1.md`  
**Authority manifest:** `docs/Architecture_Authority_Manifest.json`  
**Navigation contract:** `V1-19-SCREENS-SECTION-209`

## Sprint 9 Mission

Sprint 9 is not a cosmetic frontend sprint and not a QA-only sprint.
Its mission is to rebase the Desktop frontend onto the current canonical backend architecture,
eliminate stale/demo authority, expose the already-implemented backend capabilities through coherent workflows,
then harden and certify the resulting V1 production candidate.

The canonical screen contract is exactly 19 full screens:
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

## Primary Forensic Blockers

The Sprint 9 implementation must close the confirmed architecture gaps:
- superseded 17-screen navigation model and stale `New Sale` identity;
- missing Product Management and Warranty full screens;
- POS Thaka mutation through `DemoRetailState`;
- incomplete exact-unit / TrackingCode / Serial / IMEI workflows;
- Product and Inventory responsibility overlap;
- missing Supplier Khata / Accounts Payable frontend;
- production Dashboard demo operational truth;
- production Settings demo/preview authority;
- incomplete canonical POS workspace;
- UI error handling that collapses stable backend error codes into exception text;
- ClientOperationId generated too late inside adapters;
- production printing engine not wired into sale workflows;
- permissions mapped to the old surface model;
- stale tests that enforce 17 screens or demo-state behavior;
- stale frontend documentation;
- report refresh race/cancellation gaps;
- missing explicit Shop BusinessDate boundary;
- partial First Setup license presentation.

## Phase 1 - Authority and Navigation Rebase

### Objectives
- make the canonical 19-screen contract the only active frontend architecture;
- rename `NewSale` identity to `POS` across navigation, shell, factory, permissions, tests and docs;
- add Product Management and Warranty navigation/composition;
- replace obsolete 17-screen architecture guards;
- align permission mapping with the new surface model.

### Phase 1 Exit Gate
- exactly 19 canonical primary screens;
- no canonical `New Sale` identity remains;
- Product Management and Warranty routes exist;
- stale 17-screen tests are removed/replaced;
- frontend architecture docs no longer conflict with the canonical authority.

## Phase 2 - Eliminate Production Demo Authority

### Objectives
- replace Dashboard demo KPIs and health flags with authoritative backend reads/diagnostics;
- remove POS Thaka `DemoRetailState` mutation and use real Thaka handlers/services;
- replace authoritative Settings demo state with real providers;
- isolate Demo* providers to explicit debug/design-preview composition only;
- show unavailable/failed backend truth honestly instead of optimistic demo values.

### Phase 2 Exit Gate
- no production business state comes from `DemoRetailState`, `DemoSettingsState`, or equivalent Demo* singleton authority;
- production Dashboard values are backend-derived or explicitly unavailable;
- production Settings actions persist through real backend/application services.

## Phase 3 - Product, Inventory and Traceability Vertical

### Product Management Authority
- SKU, name, brand, model and category;
- base/product units;
- tracking policy;
- selling price and minimum stock;
- catalog/warranty attributes;
- active state;
- SupplierProduct links.

### Inventory Authority
- stock balances, lots and movements;
- physical units and condition buckets;
- cost/provenance;
- stocktake and authorized adjustments;
- warranty stock effects;
- exact physical-item history.

### Traceability Requirements
- TrackingCode search;
- Serial / IMEI search;
- InventoryUnitId selection;
- exact-unit picker;
- supplier provenance;
- purchase origin;
- sale linkage;
- warranty and physical-item history;
- serialized adjustment / stocktake workflow.

### Phase 3 Exit Gate
- Product CRUD cannot mutate stock;
- Inventory cannot create Product;
- Product Management and Inventory have separate canonical responsibilities;
- serialized/exact physical items are searchable and traceable end-to-end.

## Phase 4 - POS, Purchase and Exact-Unit Workflow Rebuild

### POS Workspace
- universal search and scanner precedence;
- TrackingCode / Serial / IMEI scanning;
- Price Check;
- Draft / Hold / Resume / Recent / Cancel Draft;
- quotation load;
- customer quick search;
- controlled price override and discount;
- normal and serialized sale;
- serialized Thaka material issue;
- CompleteSale;
- receipt/printing orchestration.

### Purchasing
- serialized purchase intake;
- exact physical-unit creation;
- supplier provenance;
- exact-unit purchase return;
- canonical non-serialized flows retained.

### Operation Identity
Generate ClientOperationId at the user-intent/workflow boundary and retain the same ID across retry,
unknown-outcome resolution, and replay instead of generating a new ID inside each adapter submission.

### Phase 4 Exit Gate
- no canonical POS/Purchase/Thaka flow is blocked by missing exact-unit UI;
- Draft/PriceCheck/UniversalSearch reach backend handlers;
- serialized sale, purchase, purchase return and Thaka issue are operational;
- unknown-outcome retry preserves ClientOperationId.

## Phase 5 - Supplier Khata and Warranty Vertical

### Supplier Khata / Accounts Payable
- payable/credit summary;
- payments;
- advances;
- refunds and reversals;
- purchases and purchase returns;
- chronological statement;
- supplied products;
- warranty context;
- backend append-only financial semantics preserved.

### Warranty Screen
Required modes:
- Customer Warranty Claims;
- Shop Stock Warranty Cases;
- All.

Required queues/workflows:
- open claims;
- with supplier;
- ready for customer;
- shop-stock cases;
- claim/case detail;
- custody transitions;
- send/receive supplier;
- replacement;
- customer handover;
- monetary credit;
- supplier warranty summary;
- physical-item history.

### Phase 5 Exit Gate
- Supplier Khata is reachable through Suppliers without duplicate truth;
- Warranty is a dedicated full operational screen;
- exact-unit provenance is preserved through warranty transitions.

## Phase 6 - Production Hardening and V1 Certification

### Typed Error Boundary
Preserve stable backend error information through the Desktop boundary:
- Error.Code;
- safe parameters;
- correlation identity;
- retryability;
- recovery/action semantics;
- localized/operator-facing message.

UI behavior must not depend on matching human English exception text.

### Business Time
Introduce one explicit Shop Time Zone / BusinessDate boundary.
Use workstation clock only as metadata where canonical backend rules require authoritative BusinessDate.

### Reports
- propagate cancellation;
- invalidate superseded requests;
- prevent stale response overwrite;
- release database resources promptly.

### Printing
Wire the existing production WPF print engine into real document workflows.
Represent external-side-effect certainty explicitly:
- CONFIRMED;
- OUTCOME_UNKNOWN;
- ACTION_REQUIRED.

### Settings / Operations
- real license integration;
- real backup and restore;
- real diagnostics;
- real database/worker status;
- workstation printer configuration separated from global receipt-template authority.

### Release Hardening
- performance and memory review;
- keyboard workflow verification;
- DPI/resolution verification;
- crash/restart/recovery scenarios;
- installer/release smoke;
- final canonical architecture evidence.

## Mandatory Sprint 9 Regression Guards

The final test suite must permanently guard:
- exactly 19 canonical full screens;
- Product Management / Inventory separation;
- dedicated Warranty screen;
- no canonical `New Sale` identity;
- no production POS Thaka `DemoRetailState`;
- no production Settings/Dashboard Demo* authority;
- POS Draft / PriceCheck / UniversalSearch adapters;
- exact-unit Sale / Purchase Return / Thaka workflows;
- Supplier Khata adapters;
- stable Error.Code propagation;
- retained ClientOperationId across retry/outcome resolution;
- production receipt reaches print orchestration;
- superseded report refresh is cancelled/invalidated;
- no stale 17-screen assertions.

## Preserve Existing Positive Controls

Do not rewrite working backend-aligned behavior without evidence:
- fail-closed production startup;
- DB readiness and pending-migration checks;
- persistent login, sessions and permissions;
- backend sign-out / user switch;
- normal non-serialized CompleteSale;
- Sales History / Sale Detail reads;
- Purchase History and ordinary non-serialized purchase;
- Expenses, Customer CRUD and basic Supplier CRUD;
- existing Reports backend path;
- existing BackendThakaService;
- existing WPF production print infrastructure;
- existing POS Draft handlers;
- existing Supplier Account handlers;
- existing Warranty backend handlers.

## Implementation Order

Canonical authority → Navigation/Permissions → Remove Demo Truth → Product/Inventory/Traceability →
POS/Purchase → Supplier Khata/Warranty → Error/Idempotency/BusinessDate →
Printing/Settings/Diagnostics → Full Regression → V1 Certification.

## Final Definition of Done

Sprint 9 is complete only when:
- Canonical Navigation: PASS (19/19)
- Frontend ↔ Backend Alignment: PASS
- Production Demo Authority: NONE
- Product Management: PASS
- Inventory Authority: PASS
- Exact Unit / Traceability: PASS
- POS Workspace: PASS
- POS Draft / Hold / Resume: PASS
- Price Check: PASS
- Serialized Sale: PASS
- Serialized Purchase: PASS
- Serialized Purchase Return: PASS
- Serialized Thaka: PASS
- Supplier Khata: PASS
- Warranty: PASS
- Typed Error Boundary: PASS
- ClientOperationId / Retry: PASS
- BusinessDate Boundary: PASS
- Report Cancellation: PASS
- Production Printing: PASS
- License Integration: PASS
- Backup / Restore Integration: PASS
- Diagnostics: PASS
- Permissions: PASS
- Architecture Tests: PASS
- Unit / Integration / PostgreSQL tests: PASS
- Migration Sync: PASS
- Debug Build: 0 warnings / 0 errors
- Release Build: 0 warnings / 0 errors
- Release Smoke: PASS
- Canonical V1 Release Blockers: NONE

**Target final state:** Sprint 9 COMPLETE, V1 Frontend/Backend Alignment COMPLETE, V1 Release Certification PASS.