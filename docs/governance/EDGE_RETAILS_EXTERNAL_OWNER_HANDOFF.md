# Edge Retails — external owner handoff

Date: 2026-10-08. This is a scoped request for contracts/clearances required to close the remaining findings. It does not authorize backend edits, migrations, production DB access or deployment. Source file names below are interface touchpoints and ownership boundaries, not permission for this client task to edit them.

## Finance operation identity and restart recovery — F03, F04, F07

**Required owner decision/contract:** define one durable operation lifecycle that stores operation ID, actor/owner scope, operation kind, complete canonical payload fingerprint, result/commit state and reconciliation status. Specify what an identical retry returns, how changed-payload/cross-owner replay is rejected, and how a process restart reconstructs pending intents before any retry. Define handling for response loss after commit and for a stale/unknown result. State which operation store is authoritative and its retention/cleanup policy.

**Frontend/backend touchpoints:** frontend submitted payload and ViewModels include SupplierDetailViewModel, ExpenseEditViewModel, StockAdjustmentViewModel, NewPurchaseViewModel, ProductEditViewModel, CompleteSaleViewModel and their existing client operation store. Protected backend contracts/handlers include `src/EdgeRetails.Application/Features/Finance/SupplierAccountHandlers.cs`, `ExpenseHandlers.cs`, `src/EdgeRetails.Application/Features/Inventory/StockAdjustmentHandlers.cs`, purchasing create/return handlers and the existing operation/reconciliation APIs. `RemoteBackendBusinessOperationsService.cs` is Pass5-owned and must be changed only by its owner.

**Required hostile evidence:** commit then drop response then restart/reconstruct/reconcile; identical full-payload replay returns one original result; changed payload with same ID rejected; cross-owner replay rejected; concurrent duplicate requests produce one ledger posting; verify supplier, expense, stock adjustment, purchase and product mutation paths. Run using disposable PostgreSQL18 and the approved canonical migration rehearsal. Assert exact ledger rows, actor/owner, quantities/balances and audit trail.

**Why open:** current frontend protection is in-memory for several workflows, existing store is not sufficient to reconstruct every canonical request, and earlier evidence does not prove full payload and owner checks for every operation. A client-side gate cannot promise backend exactly-once behavior across process crash.

## Posted expense Void workflow and recovery — F05

**Required owner decision/contract:** approve the posted-expense correction experience and define whether the supported action is Void, reversal or replacement; provide the canonical recovery identity/lifecycle and allowed reason/audit fields. Confirm permissions and compensation semantics.

**Touchpoints:** `src/EdgeRetails.Desktop/ViewModels/ExpensesViewModel.cs`, `Views/ExpensesView.xaml`, existing expense Void endpoint/handler in `src/EdgeRetails.Application/Features/Finance/ExpenseHandlers.cs` and server permission contract. Do not edit the handler/API as part of this frontend owner request.

**Required hostile evidence:** click/double-submit and concurrent Void; response-loss then restart/retry; repeat same operation; changed payload/actor rejected; exactly one reversal/compensation; original immutable expense and audit record remain; cash/bank totals match. Include permission-denied and unavailable/reconciliation states.

**Why open:** current screen truthfully exposes posted expense as read-only View and the existing isolated PG post/void/repeated-void test passes, but the UI Void path and durable uncertain-outcome recovery are not implemented or authorized by the existing frontend contract.

## Warranty timeline ownership and selection race — F08

**Required owner decision:** explicitly clear the owner of warranty timeline frontend files and the API adapter boundary, including whether request cancellation/identity checks may be added without changing warranty authority.

**Touchpoints:** `src/EdgeRetails.Desktop/ViewModels/WarrantyViewModel.cs` and its service interface/adapter. Warranty handlers, source authority and migrations remain protected.

**Required hostile evidence:** select claim A then B; complete B first and A last; only B events may display. Also test cancellation ignored, A error after B success, refresh/disposal, rapid repeated selection and current-claim mutation authorization. No timeline history may be attached to the wrong claim.

**Why open:** required owner clearance was unresolved. Warranty source was deliberately left untouched and the A/B test was not run.

## Canonical Brand and catalog query completeness — F11/F12

**Required owner contract:** expose an authoritative Brand projection and query/filter semantics; specify category filters, stable ordering, continuation token/page size, snapshot/completeness behavior, permissions and inactive-product behavior. Confirm how the API proves end-of-results.

**Touchpoints:** `src/EdgeRetails.Application/Features/Purchasing/PurchaseCatalogQueries.cs`, `src/EdgeRetails.Application/Features/Sales/PosCatalogQueries.cs`, the server catalog read controller/handler and desktop catalog DTO/gateway. The server API owner should identify the canonical brand/category interfaces and permissions. Do not source brands from DemoRetailState.

**Required hostile evidence:** >200 synthetic products across multiple pages; no missing/duplicate IDs; stable result set/order through continuation; empty/repeated/forward cursor rejection; filters match canonical category/brand memberships; unauthorized access denied; production UI displays complete count only when server signals completion. Include stale catalog and retry cases.

**Why open:** current POS safely disables unsupported production filters and clearly labels the search limit of 200. It does not establish authoritative brands or complete category browsing beyond that cap.

## Bounded supplier/customer/finance history reads — F15

**Required owner contract:** expose continuation/page metadata for every history section, stable ordering/keyset cursor and consistent snapshot policy. Define whether screen initially loads a page and how the user requests older history, while preserving complete ledger access.

**Touchpoints:** relevant application read DTOs/handlers and `RemoteBackendBusinessOperationsService.cs` (Pass5 owner only), then Supplier/Customer/Khata workspace ViewModels and services after the contract is frozen.

**Required hostile evidence:** >500 records, page boundaries and stable sort, duplicate/empty identity rejection, cursor must strictly advance, last page completion, transient failure/retry without duplicate or loss, request count bounded per visible page, totals remain authoritative, and no silent truncation. Large purchase-detail request count must remain one canonical document request.

**Why open:** supplier cursor guards and purchase N+1 removal pass, but the workspace still drains full history and the protected adapter is unchanged. Frontend-only caps would hide ledger entries and are unsafe.

## Authoritative worker/license/database diagnostics — F16

**Required owner contract:** define separate diagnostic fields, freshness/as-of, error and permission semantics for Worker, license, database size and backup freshness. Define what explicit healthy states mean and whether any field may be absent.

**Touchpoints:** Server diagnostics/readiness DTO/controller/provider and desktop RemoteBackendSettingsService / RemoteBackendDashboardService. Avoid deriving those states from generic Ready.

**Required hostile evidence:** each supported field healthy/unhealthy/stale/unavailable, permission denied, timeout, partial response and old observation timestamp. UI must show unavailable/stale honestly and never infer freshness.

**Why open:** current UI uses authorized backup evidence and explicit maintenance state only. Worker/license/database size and backup freshness policy have no identified authoritative source.

## Release/schema authority and installed Khata delivery — F01/F02

**Required owner action:** freeze the Pass5 source and schema identity; publish required migration IDs/capabilities and a supported compatibility window. Provide an approved read-only installed schema evidence source and certify exact Desktop/Server/Worker build set. Confirm the deployed Thaka timestamp mapping binary and rollback compatibility.

**Touchpoints:** release owner, database/migration owner and Server/Worker packaging owner. Architecture manifest and active Pass5 documentation remain authoritative; the provisional manifest in `scratch/frontend-phase234-20261008/PROVISIONAL_RELEASE_MANIFEST.json` must not be promoted while Pass5 moves.

**Required hostile evidence:** exact artifacts and hashes; source-to-installed identity comparison; schema migration inventory from approved source; migration rehearsal only against owned disposable PG18; Khata empty/populated, UTC-offset/microsecond and timezone read tests against release binaries; rollback and feature capability matrix.

**Why open:** source timestamp mapping and controlled frontend retry behavior pass, but installed DB schema was not inspected, Pass5 is unfrozen, and the operational DB is not authorized for this assessment.

## Clearance process

For each handoff, the owner should return the named decision/contract and authorized source files, then freeze the contract version. The frontend may implement only the cleared client portion. Re-run the listed hostile gates and independent hash review on the resulting candidate. Until then the matrix statuses and production hold remain unchanged.
