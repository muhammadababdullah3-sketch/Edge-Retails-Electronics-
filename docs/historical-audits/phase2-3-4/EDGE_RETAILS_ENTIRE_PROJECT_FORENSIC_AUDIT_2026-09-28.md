# Edge Retails — live project forensic audit and roadmap disposition

**Audit date:** 27–28 September 2026. **Mode:** read-only source, build, and test inspection; this report and its endpoint register are the only files created for the audit. **Workspace:** `C:\Users\muham\OneDrive\Desktop\Point of Sale`. **Verdict:** the current worktree is **not production-ready**. The Phase 3 Desktop cutover is visibly in progress, but there are also independently actionable stock/cost, recovery, security, and build defects. A roadmap phase label is not evidence that a defect has been corrected.

The user's roadmap is `C:\Users\muham\OneDrive\Desktop\Edge_Retails_Consolidated_Final_Implementation_Roadmap_2026-09-25.md`. Its architecture filename is not present at the named Desktop location. The repository's `docs/Architecture_Authority_Manifest.json` resolves to `docs/Edge_Retails_Final_Architecture_Report_v1.md`; the manifest's SHA-256 matches that file (`12344760c60124ddc2d1c0e54abfb7bc0e82b9b23a46e6463303ebfa530ab673`). The roadmap is treated as the user's **implementation plan**, not as proof of current behavior. Phase 1/2 certification claims and historical audit documents were checked against live source. Existing changes in this worktree predate this report; no application code was modified.

## A. Executive summary

The most urgent defects are a stock adjustment that records `Mode` but always applies a signed delta, a serialized scrap path that multiplies an already-total exact cost by quantity, and a Thaka issue path that can reuse one physical unit across lines. These can corrupt physical/financial books even after the planned Desktop cutover. The Release solution build currently fails in Desktop (`CS0246` for `System.Net.Http` types). The canonical operation outcome ledger can conceal cross-type operation-ID reuse, while the status API can reject a legitimate committed sale because the recorded outcome has no terminal ID. Sessions remain GUID bearer IDs without expiry/binding or PIN abuse controls. The scheduled backup job never invokes its backup handler.

**Disposition for the user's question:** section T separates defects that the **explicit remaining roadmap work** should close from defects **not actually specified by that work** and needing their own tickets/tests. A listed phase is a planned remedy, not a current pass. Bugs in already-declared-complete Phase 1/2 work are placed in the second group unless the roadmap explicitly reopens that exact rule.

## B. Complete solution structure and inspection boundary

`EdgeRetails.sln` contains eleven C# projects: `Domain`, `Application`, `Infrastructure`, `Desktop`, `Server`, `Worker`, `UnitTests`, `IntegrationTests`, `Desktop.PerformanceTests`, `PerformanceTests`, and `CrashTestHost`. The workspace inventory contains 508 C# source/test files, 83 Desktop XAML files, 33 PowerShell scripts, 18 Server controllers, and 105 attributed HTTP actions. `src/EdgeRetails.AdminPortal` is empty; no Vendor backend implementation was located. The latest migration sequence includes `Phase1SemanticProductIdentity`, `Phase2DurableOperationOutcome`, and `Phase2OutboxLeaseFencing`. See [the generated live endpoint register](EDGE_RETAILS_LIVE_ENDPOINT_REGISTER_2026-09-27.md) for every attributed controller route and direct controller auth/permission annotation.

This is a **source-evidenced audit of the current working tree**, not a claim that every executable branch has been dynamically exercised. PostgreSQL integration, physical printer/scanner, restore, installer, Windows service recovery, and hostile crash drills were unavailable in this run. The external frozen architecture file named by the roadmap was unavailable; the manifest-pinned repository report and roadmap supplied the authority comparison. This limit matters particularly for migration drift, lock behavior, and production failure recovery.

## C. Runtime architecture map

| Process | Actual composition and authority | Canonical target / finding |
|---|---|---|
| Desktop | `Desktop/Services/BackendRuntime.cs:255-306` creates a loopback `DesktopApiClient` and `RemoteApplicationGateway`, **and** calls `AddEdgeRetailsInfrastructure(connectionString)`; `MainViewModel.cs:68` passes its scope factory; `Navigation/PageViewModelFactory.cs:58-104,290-296` constructs local business services/handlers. | Roadmap §§10.2, 12.1 and Phase 3 exit gate require API-only Desktop. Cutover is partial, so Server middleware is not yet the sole mutation authority. |
| Server | `Server/Program.cs` composes Infrastructure, controllers, protocol/maintenance/terminal/session middleware. It defaults to `http://127.0.0.1:7150` only absent URL configuration. | §10.6 requires rejecting nonloopback bind, not merely defaulting to loopback. Startup coordinator is registered but not invoked. |
| Worker | `Worker/Program.cs` composes Infrastructure, locks, heartbeat, outbox and scheduled backup. State root defaults to LocalApplicationData; `ScheduledBackupJob` is inert. | §§10.3, 11.6, 12.9 require shared ProgramData, guarded jobs, real backups. |
| Database | PostgreSQL via EF Core writes and Dapper/EF read services. Domain models are in-process in all three runtime projects via Infrastructure references. | Desktop direct DB composition preserves a second authority route until Phase 3 cutover. |

## D. Dependency/layering matrix

| Layer | Project reference direction | Assessment |
|---|---|---|
| Domain | No application/infrastructure dependency | Clean model ownership, but some domain invariants depend on handler enforcement. |
| Application | Domain | Command handlers enforce many transactional rules; several exact-unit and idempotency invariants are inconsistent across handlers. |
| Infrastructure | Application + Domain | EF/Dapper, production services, DI composition; `InfrastructureServiceCollectionExtensions.cs:63-79` registers simulated print engines and `DefaultProductionAuthorization` for every host. |
| Desktop | Application + Infrastructure | Transitional architecture breach: local handler/repository route remains active. WPF print engines are overridden in `App.xaml.cs`, so a blanket claim of simulated Desktop printing would be wrong. |
| Server/Worker | Application + Infrastructure | Intended authority hosts, but composition is shared rather than process-specific and Worker job guard is not established. |

## E. Business authority register

| Data/rule | Intended owner | Actual write/read path | Conflict |
|---|---|---|---|
| Product, ProductUnit, SupplierProduct, tracking identity | Server application + catalog tables | Catalog handlers and EF repositories; Desktop also constructs `BackendProductManagementService` locally (`PageViewModelFactory.cs:64-68`). | Parallel runtime authority until cutover. |
| Sale, return, exchange, draft | Server sales handlers, transaction and outcome ledger | Server actions exist, but Desktop local transaction service remains selectable (`PageViewModelFactory.cs:290-296`). | Session/terminal middleware can be bypassed by local service route. |
| Physical inventory and cost | Inventory handlers + stock balances/lots/units/movements | Stock adjustment, condition, Thaka and stocktake handlers mutate same ledgers. | Cross-handler exact-unit and cost invariants disagree (F01–F04). |
| Session and permissions | Human principal + fail-closed backend authorization | `UserSessionAuthenticationMiddleware`, handler permission checks; `DefaultProductionAuthorization` is permissive for production operations. | Weak credential and sensitive-operation boundary (F08–F09). |
| Operation outcome | `system.operation_outcomes` | `EfOperationOutcomeLedger`, `OperationStatusQueryHandler` | Global ID uniqueness is not consistently applied before each business mutation (F05–F06). |
| Maintenance, backup, printing | Server/Worker durable state, Desktop physical engine | Two maintenance signals, LocalApplicationData state root, simulated shared DI, no-op scheduler. | Roadmap §§10.3/10.13/12.7–12.9 unfinished. |
| Business date/reporting | Configured shop timezone + immutable transaction dates | `CreatePurchaseHandler`, `ExpenseHandlers`, `ThakaHandlers` accept caller dates; `BusinessOperationsReadServices.ToOffset` uses host local timezone. | Date boundaries can diverge from shop authority (F13). |

## F. Business rule register

| Rule | Enforcement evidence | Result |
|---|---|---|
| Physical SKU/tracking distinguishes serialized vs individual piece | `Domain/Inventory/InventoryModels.cs`, `Application/Features/Purchasing/ReceiveProductIntakeHandler.cs`; new Phase 1 identity tests | Implemented in current source; do not repeat old “all pieces need serial” finding. |
| Serialized sale/return select exact sold units | `CompleteSaleHandler`, `SaleReturnHandler.cs:552-559` warranty guards | Basic rule implemented; exchange alternate return path lacks same guard (F10). |
| Stock adjustment mode determines target versus delta | `StockAdjustmentHandlers.cs:310,539,657` | Broken: mode persisted, not used in arithmetic (F01). |
| Scrap loss equals selected units' actual carrying value | `InventoryConditionHandlers.cs:133-143`; `InventoryCostAllocator.cs:110` | Broken for multiple selected units (F02). |
| One inventory unit can be issued once per Thaka operation | `ThakaHandlers.cs:329,351-410` checks within each line only | Broken across lines (F04). |
| ClientOperationId identifies one immutable intent/outcome | `EfOperationOutcomeLedger.cs:149-249`; business handlers check own table | Broken across operation types (F05). |
| Same draft-conversion operation can be safely replayed | `PosDraftHandlers.cs:329,372,389` | Fails after committed conversion (F07). |
| Login token expires, binds client/security epoch, PIN protected | `Domain/Identity/IdentityModels.cs:53`; `AuthenticateUserHandler`; `UserSessionAuthenticationMiddleware` | Missing required fields/control (F08). |
| Daily backup creates verified artifact | `ScheduledBackupJob.cs:25-55` | Not implemented (F11). |

## G. End-to-end business flow register

| Workflow (input → authority → writes → read/recovery) | Status and principal gap |
|---|---|
| Setup/login: Setup/Auth API → setup and identity handlers → Shop/User/Role/UserSession → session middleware | Bootstrap routes exist; session lifetime/abuse and local-client binding incomplete (F08). |
| Catalog identity: Desktop ProductManagement or Catalog API → catalog handlers → Product/ProductUnit/SupplierProduct → search/POS | Identity grammar implemented; Desktop dual authority remains (F09). |
| Purchase → receipt: `CreatePurchaseHandler` → Purchase/Items, optionally immediate stock/units/lots; `ReceiveProductIntakeHandler` handles physical intake | `ReceiveStockImmediately=true` default and Desktop's `BackendPurchasingInventoryService.cs:260` does not override it; one-product receiving can be bypassed (F14). |
| Sticker: received unit → document source → Desktop local WPF engine or shared simulated engine | Physical Desktop override exists; server-side snapshot/outbox/unknown outcome not fully wired (F15). |
| POS search/cart/sale: barcode resolver and `CompleteSaleHandler` → Sale/Items/Unit links/stock/cost/cash/outcome | Strong exact-unit checks in main handler; cross-type operation-ID collision/recovery defects remain (F05–F06). |
| Draft hold/resume/convert: `PosDraftHandlers` → draft → sale | Committed conversion replay returns draft state error (F07). |
| Sale return/exchange: respective handlers → return/exchange, restored or replacement stock, refund/cash | Ordinary return now guards active/terminal warranty; exchange helper does not (F10). |
| Purchase return: `PurchaseReturnHandler` → supplier credit, stock/provenance | Source purchase-item check exists; PostgreSQL concurrency was not executed. |
| Inventory condition/adjustment/stocktake: handlers → balances, unit states, movements, costs | Condition and adjustment defects F01–F03. Stocktake has active-product block and separate reconciliation logic. |
| Warranty/custody/replacement: warranty handlers → claim/case/units/stock → provenance | Main claim path validates active/terminal unit state and optional purchase source; alternate exchange overlap remains. |
| Supplier khata: supplier account handlers → payment/refund/ledger | Outcome support present, but global operation identity must be fixed. |
| Thaka project/material/payment/reversal/settlement → inventory and project ledger | Unit conversion and duplicate exact-unit issue F04; separate transaction tests needed. |
| Cash/expenses/reporting → cash session/movement, expense, report read model | Cash close lacks API route; net-profit calculation and timezone flaw F12–F13. |
| Backup/restore → maintenance/key/protector/history | Manual services exist; scheduler does no work, restore/rotation drills unrun (F11/F16). |
| Outbox dispatch → leased claim → effect handler → settlement | Lease/fencing exists (old “no lease” report is stale); no producer `Enqueue(` call found outside repository/interface and lease has no renewal (F15). |

## H. Cross-domain mutation side-effect matrix

| Command | Financial | Inventory/provenance | Audit/outcome/physical effect | Concern |
|---|---|---|---|---|
| Complete sale | sale totals, tender/cash, cost | sellable decrement, unit Sold, lot consumption | movement, operation outcome, receipt path | F05/F06; receipt producer not proven. |
| Return/exchange | refund or differential | restore/reissue unit and lot | return/exchange link, outcome | F10 alternate warranty check. |
| Purchase/intake | supplier payable/cost | lot/unit/stock increment | sequence, label snapshot | F14 immediate stock path. |
| Condition/scrap | inventory loss | bucket transfer, unit status, cost | movement | F02/F03. |
| Adjustment/stocktake | loss/gain | stock, units/lots | movement, outcome | F01; hostile target-zero case. |
| Thaka issue/reversal | project material charge/cost | unit issue/restock | challan, movement, outcome | F04. |
| Backup/restore/print | none directly | restore can replace DB; print external | maintenance/outbox/history | F09/F11/F15/F16. |

## I. Database/schema/constraint/migration review

EF configurations map 82 tables across `audit`, `catalog`, `finance`, `identity`, `inventory`, `parties`, `purchasing`, `sales`, `system`, `thaka`, and `warranty`. The current migrations contain durable operation outcome and outbox lease/fence changes. Business tables have per-table operation-ID uniqueness and the outcome ledger has a global operation-ID uniqueness constraint, but that does **not** stop two different business tables from committing the same ID: `RecordSuccessAsync` leaves an already-succeeded outcome untouched (F05). InventoryUnit provenance fields and purchase-origin source checks exist in `Domain/Inventory/InventoryModels.cs:186-237`. Several invariants are handler-only: one unit per Thaka issue, terminal/outcome binding, stock-adjustment mode. Database uniqueness/check constraints therefore cannot rescue these paths. No isolated `EDGE_RETAILS_TEST_DB` was configured; **applied migration order, schema drift, PostgreSQL lock behavior, and destructive migration safety were not certified**. Do not point tests at an operational shop DB.

## J. API/security/permission review

The companion endpoint register enumerates **all 105 attributed routes** in 18 controllers, with direct controller-level auth and permission checks; handlers may add checks. It is a route inventory, not a blanket authorization certification. Missing lifecycle API coverage relative to roadmap §11.1 and the current handlers: Warranty API has dashboard/timeline/search/claims but no full custody/repair/reject/replacement lifecycle; Inventory API exposes stocktake create/post but not start/count/review/cancel; Finance API has cash open/movement but no close; Sales API exposes quotation reads but not create/update/issue/cancel. This contradicts the Phase 2 “all functions exposed” exit gate (F17). Server defaults to loopback but configurable URLs can bind elsewhere without a rejection guard. Anonymous terminal registration and account listing, a four-digit PIN without persistent throttle/lockout, and long-lived GUID session bearer IDs compound the risk (F08). `DefaultProductionAuthorization` returns success for sensitive production operations (F09). `SystemController.Health` is liveness only; `/ready` checks database/maintenance, but not all startup/license/worker/disk/backup gates.

## K. Desktop/MVVM/UX architecture review

The canonical navigation/screen family is represented by live XAML/ViewModels: FirstSetup, Login, Dashboard, POS, SalesHistory/Detail, ProductManagement/Detail, NewPurchase/PurchaseHistory/Detail, Inventory, Expenses, Customers, Suppliers, ThakaProjects/Workspace, Warranty, Reports, and Settings (`Desktop/Views`, `Desktop/ViewModels`, `Navigation/NavigationTarget.cs`). There are also Stocktake, serialized intake and several dialogs/viewmodels. Thus an older report claiming NewSale or ProductManagement absent is stale. Presence does not certify the screen's API-only behavior or UX. `PageViewModelFactory.cs` chooses local `Backend*Service` objects whenever it receives the Infrastructure scope factory, as production `MainViewModel` does. POS uses some remote services, but the page factory still preserves local transaction, purchasing, catalog, Thaka, reporting, settings, and workflow paths. Roadmap §12.2 screen-by-screen cutover remains open. UI scanner timing, focus, empty/error states, print hardware output, and accessibility were source-reviewed but not physically exercised; no visual/interaction pass can be certified from this run.

## L. Concurrency, replay, recovery and error handling

Per-operation locks, transaction runner, outcome ledger, inventory row locks, stocktake product block, and outbox claim/fencing are present. The gaps are **identity across different handlers** (F05), **status lookup after commit** (F06), **draft conversion replay** (F07), **duplicate exact unit within one multi-line Thaka command** (F04), and **physical effect lease expiry** (F15). `OutboxProcessor` uses a fixed two-minute claim without renewal while effect handlers run; after expiry a second worker may repeat an external effect before first settles. Fencing protects settlement, not a printer already actuated. This is a risk requiring a controlled slow-effect drill, not proof of double printing today because no enqueue producer was found. API error contracts exist, but source-level inconsistent failures on replay/identity reduce their operational value.

## M. Accounting and ledger integrity

`BusinessOperationsReadServices.cs:390` calculates `netProfit = grossProfit - expenseTotal`, omitting recognized inventory losses already recorded by scrap/adjustment/stocktake paths; reported profit can therefore exceed economic profit (F12). Its period conversion uses the host timezone (`:641-642`), while several command dates arrive from caller input. Thaka material price ignores ProductUnit factor (F04); serialized scrap misstates loss (F02). Supplier account, cash, and tender handlers have transaction paths, but ledger reconciliation against a real migrated PostgreSQL database was not run. Treat accounting totals as uncertified pending golden trace and loss/return/thaka reconciliation.

## N. Inventory and exact-unit provenance

The new `TrackingMode.IndividualPiece` and serialized identity grammar are real. `ReceiveProductIntakeHandler` has exact-unit purchase provenance, and return/warranty handlers use source links. Defects concentrate in alternate mutations: F01 target/delta confusion, F02 scrap cost, F03 condition transfer of arbitrary FIFO lots rather than selected units' lots, F04 duplicate Thaka unit. `InventoryConditionHandlers.cs:98` rounds supplied quantity before serialized whole-number validation at `:214`, so a fractional command close enough to an integer may pass if rounding yields a whole number; this should be rejected on original input. `TransferBucketAsync` at `:123` takes product/bucket/quantity and cannot receive selected lot IDs. The findings require balance/lot/unit/movement invariant tests, not just returned result tests.

## O. Warranty, supplier khata and Thaka

Ordinary `SaleReturnHandler.cs:552-559` now blocks units with active or terminal warranty resolution; `CommercialExchangeHandler.PrepareSerializedReturnUnitsAsync` (`:867-912`) checks sold/original/prior-return/status but lacks those warranty checks (F10). Warranty claim creation accepts an optional source purchase item and validates it when present; old claims that it unconditionally requires the purchase item are stale. Thaka's issue handler validates line-level unit lists but not all lines combined, and charges per entered unit at the product's base default price instead of applying `ProductUnit.FactorToBaseUnit` (`ThakaHandlers.cs:301-357`). Supplier khata and Thaka payment/reversal operations use operation IDs, but cross-type collision remains a shared risk.

## P. Printing, backup, operations

Desktop overrides simulated print engines with WPF engines, so physical desktop output is implemented at composition level. Shared Infrastructure DI still binds simulated production and sticker engines (`InfrastructureServiceCollectionExtensions.cs:63,79`), and no outbox enqueue producer was found. The Worker backup scheduler logs “Starting” and advances `_lastRunUtc` without invoking `CreateBackupHandler.HandleAsync` (`ScheduledBackupJob.cs:25-55`), so a configured automatic backup creates no artifact (F11). `AesGcmBackupProtector` has a format marker but no manifest key identifier/version that selects an old key after rotation (F16). Production state roots can diverge between ProgramData and LocalApplicationData. `ProductionStartupCoordinator` is registered (`InfrastructureServiceCollectionExtensions.cs:248`) but no runtime invocation was found. These are Phase 1/3 operational gates, not a reason to assume a restore is safe.

## Q. Test architecture and coverage

The unit suite run with `dotnet test tests/EdgeRetails.UnitTests/EdgeRetails.UnitTests.csproj --no-restore` produced **662 pass, 11 fail, 0 skip (673 total)**. All 11 failures are Windows ACL hardening `UnauthorizedAccessException` in `FileProductionMaintenanceIntegrityKeyProvider.HardenWindowsDirectoryAcl` or `FileProductionMaintenanceBarrier.HardenWindowsDirectoryAcl` in this execution environment; this does not prove those tests pass on a supported machine. Integration tests require `EDGE_RETAILS_TEST_DB`, absent here; no operational DB was touched. The Release build `dotnet build EdgeRetails.sln -c Release --no-restore -v:minimal` failed with eight `CS0246` errors for `HttpClient`/`HttpMethod`/`HttpRequestMessage`/`HttpResponseMessage`/`DelegatingHandler` in `Desktop/Services/DesktopApiClient.cs` (F00). The file imports `System.Net.Http.Json`, but not `System.Net.Http`. New Phase 1/2 unit/integration tests exist, including outcome/lease/physical receiving tests; their presence does not cover the cross-handler counterexamples above. Add database-backed regression tests for every F01–F07/F10/F12–F13 invariant and a slow external-effect lease test; run build and unit/integration/hardware gates again after fixes.

## R. Deployment, startup and configuration

Server host URL configuration can override the loopback default (`Server/Program.cs`). Worker and Infrastructure production state default to LocalApplicationData (`Worker/Program.cs`, `InfrastructureServiceCollectionExtensions.cs:261-269`), contrary to the shared `%ProgramData%\EdgeRetails` authority in roadmap §10.3. Server and Worker do not call `ProductionStartupCoordinator`; Desktop calls `BackendRuntime.CheckStartupAsync`, which checks a narrower startup state. Durable maintenance state exists, while HTTP maintenance middleware also reads `EDGE_RETAILS_MAINTENANCE_MODE` and gates POSTs; the two signals can disagree. No installer/service/upgrade-operation certification was run. A live binding, restart, disk-full, secret rotation, migration rollback and backup/restore rehearsal remains required by roadmap Phase 6.

## S. Dead, legacy and duplicate authority inventory

`TerminalHandlers.cs:129-133` actively enforces `MaxTerminals`; `Application/Production/Licensing/LicenseContracts.cs:11` still carries it, conflicting with roadmap §2.1's single-machine commercial model. The AdminPortal directory is empty, and Vendor backend/Portal are Phase 4/5 planned, not present. Legacy Desktop `Backend*Service` local mutation paths remain active while remote equivalents are being introduced; remove only after proven cutover, not by deleting code speculatively. Demo services and placeholder UI remain in Desktop source; inspect their runtime reachability during Phase 3 screen closure. Old audit documents and `docs/Phase2_Shop_Server_API_Parity_Matrix_2026-09-26.md` contain now-stale counts/claims (17 vs 18 controllers; past test count; “complete” lifecycle APIs), so they should not be used as current evidence.

## T. Roadmap disposition — what will and will not be solved

**Explicitly covered by the user's remaining roadmap, if the exit gates are actually implemented and tested:**

| Findings / gap | Roadmap clause | Required acceptance evidence |
|---|---|---|
| F09 Desktop direct DB/handler authority | §§10.2, 12.1–12.2; Phase 3 exit | Desktop production DI cannot resolve DbContext/handler; every screen operation routes through Server; process test asserts no local DB mutation. |
| F11 no-op scheduled backup; F16 backup/restore/key lifecycle | §§12.8–12.9, Phase 6 restore drills | Real encrypted artifact, checksum/manifest/history, restore across restart/key rotation, retention, failure alarms. |
| F15 print/outbox operational wiring | §§11.5–11.6, 12.7, Phase 6 physical hardware drills | Durable snapshot/enqueue and terminal print outcome; simulated effect impossible in production; slow-effect and crash recovery. |
| F17 API lifecycle parity | §11.1 and Phase 2 exit (already declared complete, so reopen as a concrete regression); §12.2 | Controller contract for every handler workflow and Desktop call, authenticated integration tests. |
| F08 session/PIN/local-client/loopback hardening | §§10.5–10.12, Phase 6 security drills (Phase 1 declared complete, so reopen exact gap) | Token hash, expiry/epoch/binding, persistent PIN throttle/audit, nonloopback rejection and pen test. |
| F18 ProgramData/startup/maintenance split | §§10.3–10.4, 10.13, 11.6, 12.10 | One shared state root/barrier; Server/Worker startup gate and restart drill. |
| F19 legacy terminal quota | §§10.1, 13.2 | No active `MaxTerminals` denial in single-machine runtime; migration/license test. |
| F14 legacy immediate purchase receiving | Phase 1C/§8 one-product intake and §12.2 screen cutover | Desktop purchase posts financial purchase only; per-product physical receipt separately confirms exact units/labels. Phase 1C was declared done; treat current route as regression until proved intentional. |
| F00 Release build | Phase 6 §15.1 build gate | `dotnet build EdgeRetails.sln -c Release` succeeds in clean checkout. |

**Not resolved merely by following the written roadmap; create dedicated correction tickets even if a broad “hostile certification” phase might detect them:**

| Finding | Why not automatically closed | Specific fix/test |
|---|---|---|
| F01 stock adjustment mode ignored | No roadmap rule states target-versus-delta arithmetic. | Implement mode-specific target delta, including zero target; persisted before/after balance test. |
| F02/F03 serialized scrap/condition cost and lot mismatch | Exact-unit work describes provenance generally, but does not prescribe this alternate transfer/cost algorithm. | Reject fractional raw input; move selected lot quantities and remove exact total once; two-unit unequal-cost fixture. |
| F04 Thaka price factor and cross-line physical unit reuse | Thaka preservation is broad; these two counterexamples are absent. | Apply unit factor to authoritative charge; global selected-unit uniqueness plus DB/invariant test. |
| F05 cross-type operation-ID collision | “Central ledger” exists already; roadmap does not define immutable type/payload/actor claim enforcement at mutation entry. | Claim/validate global intent before side effects; reject mismatch; multi-handler collision test. |
| F06 committed operation status rejected | Outcome-ledger clause does not specify terminal-null scoping/backfill. | Record trusted terminal consistently or define safe identity scope; lost-response sale/status integration test. |
| F07 draft-conversion replay | Draft API parity does not imply idempotent converted-draft replay. | Check operation outcome before Open-state guard; duplicate-submit/lost-response test. |
| F10 exchange bypasses warranty guard | Sale return rule is fixed, but alternate exchange helper differs. | Centralize eligibility and test active/terminal warranty exchange. |
| F12 net profit excludes stock losses; F13 date timezone | Reporting API availability does not correct financial definition or shop-time authority. | Profit reconciliation including recognized losses; shop-zone date boundary tests. |

## U. Findings register (evidence, failure path, priority)

Severity means observed source impact in this worktree, not a production incident count. “Confirmed” means a direct code/build trace; “risk” means an unexercised but plausible failure path.

| ID | Severity / confidence | Exact reference and failure path | Remediation |
|---|---|---|---|
| F00 | **High / confirmed build** | `Desktop/Services/DesktopApiClient.cs` lacks `using System.Net.Http`; Release solution build reports eight CS0246 errors in that file. | Fix imports/project compile, run clean Release build. |
| F01 | **Critical / confirmed code** | `Application/Features/Inventory/StockAdjustmentHandlers.cs:123,310,539,657`: only positive base quantity accepted; mode stored but both branches use signed supplied quantity. Physical target 10 with prior 10 becomes 20, target 0 rejected. Canonical report `docs/Edge_Retails_Final_Architecture_Report_v1.md:489` says target physical count is authoritative. | Compute delta from target when mode means physical count; test zero, increase, decrease, concurrent update. |
| F02 | **Critical / confirmed code** | `Application/Features/Inventory/InventoryConditionHandlers.cs:133-143` sums two selected unit costs and passes total as `exactCost`; `Infrastructure/Services/InventoryCostAllocator.cs:110` multiplies it by quantity. Costs 100+200, qty 2 → attempted removal of 600 instead of 300; it either fails insufficient-value validation or misstates cost if the pool has enough value. | Clarify allocator parameter per-unit vs total; pass exact total once; reconcile ledger. |
| F03 | **High / confirmed code, DB effect unrun** | `InventoryConditionHandlers.cs:98,123-128,214`: round first, then whole test; generic FIFO `TransferBucketAsync(product,buckets,qty)` cannot target selected units' lots. | Validate raw quantity and selected-lot mapping; test unequal lots and fraction. |
| F04 | **Critical / confirmed code** | `Thaka/ThakaHandlers.cs:301-357,371-412`: default base price used for every ProductUnit; `Distinct` only within each line, while lines are prepared before consumption. Same unit in two distinct lines can decrement stock/cost twice. | Factor-aware price and command-wide unique unit set; invariant at persistence boundary. |
| F05 | **High / confirmed code** | `Sales/CompleteSaleHandler.cs:140` and `Sales/SaleReturnHandler.cs:123` check only own business tables; `Infrastructure/Repositories/EfOperationOutcomeLedger.cs:206-231` silently returns on existing Succeeded outcome regardless operation type/payload. | Global immutable intent claim before mutation; reject cross-type/payload/actor reuse. |
| F06 | **High / confirmed code** | `CompleteSaleHandler.cs:154-164,513-523` records outcomes without terminal; `OperationStatusQueryHandler.cs:83-87` rejects non-null queried terminal against null outcome; `Server/Controllers/OperationsController.cs` passes trusted terminal. | Persist terminal from request execution context or adjust safe scoping; integration replay/status test. |
| F07 | **High / confirmed code** | `Sales/PosDraftHandlers.cs:329,372,389` requires Open before delegating sale, then marks Converted. Same operation after lost response gets draft-not-open, not original sale. | Query outcome before draft-state check; return committed sale result. |
| F08 | **High / confirmed security gap** | `Domain/Identity/IdentityModels.cs:53` session has GUID ID/start/end/revoked only; `AuthenticateUserHandler` four-digit PIN has no durable abuse control; `Server/Middleware/UserSessionAuthenticationMiddleware.cs` accepts session GUID without expiry/client binding; `Server/Program.cs` permits configured nonloopback binding. | Implement roadmap §§10.5–10.12 as one security boundary with tests. |
| F09 | **High / confirmed authority** | `Desktop/Services/BackendRuntime.cs:305-306`, `Navigation/PageViewModelFactory.cs:58-104,290-296` preserve local DB/handlers; `InfrastructureServiceCollectionExtensions.cs:64` production authorization defaults permissive. | Finish API-only process DI; fail-closed production authorization; remove local mutation path after coverage. |
| F10 | **High / confirmed code** | `Sales/SaleReturnHandler.cs:552-559` guards active/terminal warranty; `Sales/CommercialExchangeHandler.cs:867-912` alternate serialized return helper does not. | Share exact-unit return eligibility routine; test exchange under active/terminal claim. |
| F11 | **High / confirmed code** | `Worker/Jobs/ScheduledBackupJob.cs:25-55` checks handler and directory but never calls handler, logs start, sets last-run. | Execute/verify backup; persist schedule state and alert on failure. |
| F12 | **Medium / confirmed code** | `Infrastructure/Services/BusinessOperationsReadServices.cs:390` net profit subtracts expenses only, omitting recognized inventory loss; canonical report `docs/Edge_Retails_Final_Architecture_Report_v1.md:1381-1384` explicitly subtracts recognized inventory losses. | Define accounting formula and reconcile movement loss with report. |
| F13 | **Medium / confirmed code** | `BusinessOperationsReadServices.cs:641-642` uses host zone; `Purchasing/CreatePurchaseHandler.cs:332`, `Finance/ExpenseHandlers.cs:119`, `Thaka/ThakaHandlers.cs:121` persist caller dates. | Derive authoritative shop date/server time; test DST and boundary cutoffs. |
| F14 | **Medium / confirmed alternate path** | `Purchasing/CreatePurchaseHandler.cs:39,231,269,371` defaults `ReceiveStockImmediately=true`; Desktop `BackendPurchasingInventoryService.cs:260` omits override, whereas `ReceiveProductIntakeHandler` is separate. | Separate purchase from physical receipt in Desktop path and contract. |
| F15 | **Medium / risk + source gap** | `Application/Production/Outbox/OutboxProcessor.cs` fixed two-minute lease without renewal; `rg Enqueue\(` finds only interface/repository, while `InfrastructureServiceCollectionExtensions.cs:63,79` provides simulated host engines. | Wire snapshot producer; handle unknown physical outcome and lease renewal/worker fencing; crash drill. |
| F16 | **Medium / risk** | `Infrastructure/Production/Backup/AesGcmBackupProtector.cs` uses `ERBAK002` format but no old-key selector; restore under key rotation not verified. | Version/key-ID manifest and multi-key restore rehearsal. |
| F17 | **High / confirmed parity gap** | `Server/Controllers/{Warranty,Inventory,Finance,Sales}Controller.cs` lacks the lifecycle actions enumerated in section J; Phase 2 parity document's controller count/test claims are stale. | Complete routes/contracts/auth/errors for all handler workflows. |
| F18 | **Medium / confirmed authority** | `InfrastructureServiceCollectionExtensions.cs:261-269`, `Worker/Program.cs` default LocalApplicationData; startup coordinator only registered at `:248`, not invoked; HTTP maintenance env flag and durable barrier differ. | Shared ProgramData state and one startup/maintenance gate. |
| F19 | **Low / confirmed legacy** | `Terminals/TerminalHandlers.cs:129-133` enforces `MaxTerminals`; `Production/Licensing/LicenseContracts.cs:11` carries it, contrary to roadmap §2.1. | Remove active quota during licensing V2 cutover with migration/regression. |

**Count:** 3 Critical, 10 High, 6 Medium, 1 Low (20 total). F15/F16 are risk findings; the remainder are direct source/build gaps. No finding relies solely on an old report.

### Scorecard (0–10, current worktree)

| Area | Score | Principal evidence |
|---|---:|---|
| Domain identity/provenance | 7 | New exact-unit model and tests; alternate mutation gaps. |
| Catalog/purchasing | 6 | Identity grammar present; immediate receipt compatibility path. |
| Inventory integrity | 3 | F01–F04 can diverge balance, unit and cost. |
| Sales/returns/warranty | 5 | Main path stronger; exchange/draft recovery gaps. |
| Finance/reporting | 4 | Loss omitted and time authority differs. |
| Session/authorization | 3 | F08/F09. |
| API parity | 5 | 105 actions, missing lifecycles. |
| Desktop cutover | 4 | Local scope/handlers active. |
| Replay/outcome/recovery | 4 | Ledger exists; F05–F07. |
| Outbox/physical effects | 4 | Lease/fencing exists; producer and slow-effect recovery unproven. |
| Backup/restore | 3 | Inert scheduler; restore drill unavailable. |
| Schema/migrations | 5 | Migrations present; no isolated PostgreSQL certification. |
| Build/tests | 4 | Release build fails; 662/673 unit green, integration unrun. |
| Deployment/startup | 3 | State root/startup/loopback gates unfinished. |

### Prioritized remediation plan

1. **Block release now:** fix F00 build; add regression tests for F01/F02/F04/F05; repair these corruption/replay paths before further Desktop cutover. These are small, falsifiable changes with balance/lot/ledger assertions.
2. **Close security and recovery boundary:** F06–F10, F17. Establish one immutable operation claim, trustworthy session/terminal context and API-only mutation route; exercise lost-response and alternate exchange cases.
3. **Reconcile accounting and intake:** F03/F12–F14 with unequal-cost lot fixtures, exact-unit return/Thaka trace, shop timezone and zero-target stocktake/adjustment cases.
4. **Finish Phase 3 operations:** F11/F15/F16/F18, real backup/restore and printer drills, Worker failover, state-root and startup gates, followed by installer/service verification.
5. **Cut legacy authority and certify:** F19, update stale parity/audit docs after code passes; run clean Release build, all unit/integration/performance suites against isolated DB, migration/restore/security/physical hardware drills, and reconcile Golden Trace values. Do not treat a green test count alone as production certification.

**Final verdict:** architectural direction is clear and several prior problems are genuinely fixed, but the present code has independently actionable correctness defects beyond the remaining roadmap tasks. The roadmap-covered group should be verified at its actual exit gates; the separate defect group needs explicit tickets, fixes and regression tests. Until the Critical/High findings and Release build are closed, the current tree should not be released as a production POS backend.
