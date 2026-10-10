# Edge Retails frontend against the current backend/database

Read-only audit — 8 October 2026, Asia/Karachi

## Result and limits

**17 findings: 4 High, 11 Medium, 2 Low. No Critical defect was established by this review.** This is not a claim that no Critical defect exists elsewhere. Findings distinguish installed-build evidence, historical runtime evidence, and code-path findings that were not exercised against the operational system.

No application code, configuration, service, shortcut, database row or schema was changed. The only new file is this requested audit report. No sale, payment, login, terminal registration, backup, restore, print, migration or operational write test was executed. Database inspection used an explicit `BEGIN READ ONLY` transaction, `default_transaction_read_only=on`, and a ten-second statement timeout. No customer/supplier names, credentials or transaction row contents were exported.

The normal desktop application was not launched for inspection: its startup calls terminal registration, which is a write. Consequently this pass does not certify live visual layout, physical printer behaviour, cashier sessions, populated-data performance or all business state transitions. Current source was traced across frontend adapters/view models and their relevant controller/domain contracts. Selected installed assembly metadata and IL references were read directly without loading/running the application. Source references identify the reviewable implementation; they must not be mistaken for proof that every source change is deployed.

## Current authority and delivery evidence

| Item | Evidence observed on 8 October |
|---|---|
| Shop database | `edge_retails_prod`, PostgreSQL 18.6; transaction read-only reported `on` |
| Server service | Running from `C:/Program Files/Edge Retails/server/EdgeRetails.Server.exe` |
| Worker service | Running from `C:/Program Files/Edge Retails/worker/EdgeRetails.Worker.exe` |
| Production frontend | `artifacts/production/desktop/EdgeRetails.Desktop.dll`, modified 6 October 15:41:35 |
| Frontend identity | Production DLL hash equals `pending-supplier-ui/EdgeRetails.Desktop.dll`; differs from `pending-customer-khata/desktop/EdgeRetails.Desktop.dll` |
| Installed Infrastructure | Modified 1 October 10:22:24; hash differs from staged Infrastructure |
| Desktop shortcut | `Edge Retails - Latest Build.lnk` targets the production desktop executable, empty arguments, production working directory |
| Latest correction package | `STAGED_NOT_APPLIED`; not the binary launched by that shortcut |
| Installed suspension/card capability | Production DLL lacks `OpenOnClick`, `SetCustomerSuspensionAsync` and `ToggleSuspensionCommand` symbols |
| Readiness | `Ready`, connection true, pending migrations false, maintenance Normal — relative to the installed assembly |
| Version endpoint | Server version, protocol version and minimum protocol version all `1.0.0` |
| Migration history | 20 entries; last `20260930065058_Phase3COwnerPinAuthorizationConsumption` |
| Latest source schema | Two additional required migrations: TrackingManufacturerIdentityAuthorityV1 and Phase7Pass5WarrantySourceAuthority |

Read-only exact database counts:

| Data | Count |
|---|---:|
| Products / stock balances / lots / physical units | 0 / 0 / 0 / 0 |
| Sales / purchases | 0 / 0 |
| Customers / suppliers / khatas | 2 / 1 / 1 |
| Khata material issues / payments | 0 / 0 |

The single khata has status 1 (Active). Both customers are active; one is Walk-in. These counts establish that the configured shop database currently contains no inventory to display. The preview's eight demo products are not evidence of missing persisted products. No data deletion or alternate real inventory source was established.

## Findings index

| ID | Severity | Finding | Evidence classification |
|---|---|---|---|
| F01 | High | Frontend/backend/schema delivery is not aligned; requested latest capabilities are absent from the operational app | Installed hashes, symbols, readiness and schema metadata |
| F02 | High | Reported khata workspace mapping failure remains an undeployed backend correction | Historical runtime event + unchanged installed binary; not freshly reproduced |
| F03 | High | Supplier editor discards unresolved payment/refund identities when input changes | Installed setter IL references + source trace |
| F04 | High | Recovery identity is not durable across several finance/inventory workflows | Installed state-field metadata + source trace; crash scenarios not run |
| F05 | Medium | Posted expense Edit has no successful authoritative path; void/correction is not exposed | Frontend/backend contract conflict |
| F06 | Medium | Monetary display rounds fractional totals needed for exact-payment checkout | Installed report formatting IL + checkout/cart source |
| F07 | Medium | Save/apply/payment commands permit reentrant submissions and editable pending payloads | Source command/async path inspection |
| F08 | Medium | Warranty timeline can display a previous claim's events under the new selection | Source race; installed method present |
| F09 | Medium | Customer/supplier refresh can overwrite a newer search result | Source race |
| F10 | Medium | Failed financial report loads retain zero/stale financial results without a persistent unavailable state | Source state/error handling |
| F11 | Medium | Production POS brand column/filter is not backed by brand data | Source DTO-to-view trace |
| F12 | Medium | POS category browsing filters only the first capped catalog result | Source read/filter contract |
| F13 | Medium | Purchase detail has a hidden InventoryManage permission dependency | Adapter/controller permission trace; conditional role scenario |
| F14 | Medium | Confirmed purchase creation can be presented as rejected after display readback fails | Source end-to-end commit/readback trace |
| F15 | Medium | Several screens load complete histories and purchase detail performs serial per-product reads | Source performance/read path |
| F16 | Low | Dashboard/settings operational diagnostics remain unimplemented in their adapters | Explicit source feature gap |
| F17 | Low | Customer save failure is described as a detail-load failure | Source error copy |

## Detailed evidence and impact

### F01 — High: operational release mismatch

The stable shortcut launches the 15:41 frontend, while the corrected customer/khata package is a later, different binary. The installed backend is older still. Current production frontend metadata confirms that the shared whole-card behaviour and suspension methods are absent. Thus the prior code-level fixes do not establish that the user can use those capabilities in the operational application.

`src/EdgeRetails.Desktop/Services/BackendRuntime.cs:110`, `CheckStartupAsync`, checks protocol/readiness. `src/EdgeRetails.Server/Controllers/SystemController.cs:94`, `Version`, advertises the constant `1.0.0`; it does not advertise a build hash or required feature capabilities. The live endpoint returns that same identity. Compatibility therefore cannot establish that this exact frontend feature set is implemented by the installed backend.

The old server's Ready result also cannot establish compatibility with the staged assembly's two newer migrations. Replacing only the desktop, or interpreting Ready as proof that the latest package is installed, leaves features unavailable or produces contract/readiness failures. Remediation: one coordinated, identifiable release, a schema/capability compatibility gate, and installed-binary acceptance checks. This audit does not perform that rollout.

### F02 — High: khata workspace backend failure, still awaiting delivery

The prior Windows Application event at **6 October 15:25:47** identifies a Dapper constructor mismatch for `ThakaMaterialLedgerRowDto`: Npgsql `DateTime` versus the public `DateTimeOffset` record parameter. It was recorded in `docs/EDGE_RETAILS_CUSTOMER_KHATA_SUSPENSION_AND_WORKSPACE_CORRECTION_2026-10-06.md`. The installed Infrastructure DLL remains dated 1 October and differs from the corrected package.

Reviewable correction: `src/EdgeRetails.Infrastructure/Services/ThakaReadService.cs`, `GetProjectAsync`, uses internal database row types and explicit timestamp conversion. Frontend error/loading correction: `src/EdgeRetails.Desktop/ViewModels/ThakaWorkspaceViewModel.cs:333`, `RefreshBackendAsync`. Those improvements belong to the staged release, not the production DLL identified above.

Impact: workspace refresh fails and a connection-oriented message can obscure a mapping fault. Zero-initialized/old UI financial cards must not be used as evidence that the failed read succeeded. **The fault was not re-triggered today** because no authenticated operational session was created. This is an evidenced historical defect with no observed deployment of its correction, not a new runtime reproduction.

### F03 — High: supplier input editing destroys unresolved operation identity

`src/EdgeRetails.Desktop/ViewModels/SuppliersViewModel.cs:210`, `SupplierDetailViewModel.TransactionAmount`, and the method/reference/note setters call `ResetPendingEntryOperations`. At line 534, that method clears settlement, advance and refund IDs. Read-only IL decoding of the installed DLL confirms that `set_TransactionAmount` and `set_ExternalReference` call this reset method; its installed pending fields are also present.

`PostPaymentAsync` at line 311 keeps an ID after uncertain failure, but changing a note/reference/amount immediately clears it. Scenario: an advance commits, its response is lost, the user edits the reference and presses Post Advance again. A new ID is generated. The backend can legitimately accept this as a second operation; the original idempotency guarantee no longer protects the user. No payment was posted to demonstrate this on the shop database.

Remediation: retain the submitted identity and immutable payload until its outcome is reconciled; separate a genuinely new payment from retrying an unknown one; expose pending-operation status. Do not clear an uncertain ID merely because a field changed.

### F04 — High: durable recovery is inconsistent between workflows

Durable `ClientOperationIntentStore.cs` exists and is used by sale/stocktake/physical intake/restore and newer khata paths. However several other paths keep IDs only in a view-model or adapter instance:

- Expense posting: `RemoteBackendBusinessOperationsService.cs:14` and `PostExpenseAsync:30`, `_pendingExpenseOperationId`.
- Supplier settlement/refund: `SuppliersViewModel.cs:311`, `PostPaymentAsync` / `ReceiveRefundAsync`, nullable instance fields.
- Delta adjustment: `StockAdjustmentViewModel.cs:15`, `_clientOperationId`, used by `ApplyAsync:48`.
- New purchase: `NewPurchaseViewModel.cs:152`, `_clientOperationId`, used by `SavePurchaseAsync:416`.
- Warranty: `WarrantyViewModel.cs:123` clears pending identities on selection change; lifecycle/receipt IDs are instance fields.
- Product aggregate: `RemoteProductManagementService.cs:11`, `_pendingAggregateOperations`, used by create/update at lines 123/174.

Installed metadata independently confirms the expense/supplier/warranty/adjustment/product pending fields and the absence of an intent-store field in the installed business adapter. Closing/reopening a dialog or restarting the process loses these identities. After a commit-plus-response-loss, repeating an expense, advance or stock delta under a new ID can post a second business operation. Purchase invoice uniqueness and product identity/version guards may reject some repeated creations; they are **not** evidence that every listed workflow duplicates data.

Remediation: durable per-workflow pending intents, immutable submitted payloads, authenticated reconciliation, and explicit operator recovery after restart. Existing strong recovery paths should be reused, not replaced with blanket retries.

### F05 — Medium: expense editing conflicts with immutable posting

`ExpensesView.xaml:69` binds Edit to `EditExpenseCommand`; `ExpensesViewModel.cs:185` opens the editable dialog. `ExpenseEditViewModel.SaveAsync:103` then rejects an existing backend expense at line 131: posted expenses are immutable and require void/correction. The remote business service exposes posting, not the corrective UI workflow.

The backend already has `ExpensesController.cs:110`, POST `{id}/void`, and `VoidExpenseHandler`. The frontend leads the user into an edit form that cannot complete and gives no equivalent void/correction action on that screen. Remediation: replace the misleading action with view plus authorized void/correction, retaining the audit trail. Do not enable in-place editing of posted financial rows.

### F06 — Medium: fractional values hidden at exact-payment checkout

`CompleteSaleViewModel.cs:130`, `TotalToPayDisplay`, and line 552's exact-payment message format the amount with `N0`. Cart unit/line values (`PosCartItemViewModel.cs:136`) and product prices (`PosProductItemViewModel.cs:169`) do the same. Reports use `ReportsViewModel.cs:423`, `Currency`; installed IL confirms the literal `N0` format there. Supplier ledger boxes already use `N2`, making precision inconsistent across screens.

Scenario: a legitimate decimal total of 100.50 can be displayed as a whole rupee while the payment validation still requires the exact decimal value. This obscures the amount the operator must enter and rounds report differences away. It does not prove the backend stores rounded money. Remediation: consistent presentation of the backend's monetary precision, especially exact-amount instructions, without altering accounting amounts.

### F07 — Medium: pending actions do not universally prevent reentry

`CustomerEditViewModel` (`CustomersViewModel.cs:39/73`), `SupplierEditViewModel` (`SuppliersViewModel.cs:43/90`) and `ExpenseEditViewModel` (`ExpensesViewModel.cs:49/103`) use async RelayCommand handlers without a busy guard. `StockAdjustmentViewModel.cs:34/48` and supplier financial commands at `SuppliersViewModel.cs:168` similarly allow additional invocations during a pending request.

Backend idempotency can coalesce an identical ID; it does not make concurrent UI mutations, field changes, shared pending-field clearing or duplicated callbacks safe. Expense adapter lookups await before accessing shared mutable pending fields, increasing the opportunity for overlapping calls. Remediation: single-flight command execution, disabled/edit-frozen submitted fields and a finally-based busy release. Test same-payload clicks and payload changes separately. No operational duplicate-click test was run.

### F08 — Medium: warranty timeline selection race

`WarrantyViewModel.cs:118`, `SelectedRow`, fires `LoadTimelineAsync`. At line 621, that method clears the timeline, captures a selection, awaits its events and appends them without a request generation, cancellation or post-await selection check.

Scenario: select A, then B; B finishes first, A finishes later. A's events can be appended under B's selected claim, or mix with B's history. Installed metadata contains this method, and the reviewed source has no identity check. Remediation: cancel/version timeline loads and replace events only if both selected work ID and kind still match. This was traced, not reproduced by writing warranty records.

### F09 — Medium: directory refresh and search use separate ordering controls

`CustomersViewModel.cs:303`, `RefreshBackendAsync`, replaces the directory after its await without checking `_searchVersion`. `ScheduleSearchAsync:335` does use a search generation, but the independent initial/post-save refresh can still finish afterward and overwrite a newer search result. The supplier directory follows the same refresh/search pattern in `SuppliersViewModel`.

Scenario: initial unfiltered request is slow; a filtered search completes; the original refresh replaces it even though the search box still contains the newer term. Mutation-triggered refreshes can also be skipped by `_backendLoading` rather than queued. Remediation: one generation/cancellation policy shared by all directory read paths; schedule a follow-up refresh after a skipped mutation refresh.

### F10 — Medium: failed reports look zero or remain stale

`ReportsViewModel.cs:60` constructs a default empty snapshot; money displays at line 181 immediately format its default zeros. `RefreshBackendAsync:236` reports an error through a toast but does not invalidate the snapshot or expose a persistent load-failed/unavailable state. A later failure retains the previous successful snapshot; the filter controls can now describe another requested period while the old results remain.

The snapshot's own period label remains old, which mitigates but does not remove the inconsistency. On a first failure, zero financial cards are not evidence of zero sales/profit. Remediation: distinguish unavailable from empty; retain old data only with a persistent stale/as-of label and its exact period, or clear it. Dashboard's nullable/unavailable handling provides an existing better model.

### F11 — Medium: production POS brand feature is cosmetic

`PosView.xaml:263` offers a Brand filter and line 349 a Brand column. `PosViewModel.LoadBackendCatalogAsync:1437` assigns every row `brand: "—"` at line 1469 and resets available brands to an empty set at line 1486. `Application/Features/Sales/PosCatalogQueries.cs`, `PosCatalogProductDto`, has no brand field, nor does the gateway projection.

Demo products do contain brands, explaining why this behaves more completely in previews. The actual database has no products today, so this populated-data symptom was not reproduced. Remediation: add the authoritative brand projection/filter contract or explicitly remove the unsupported operational filter; never fill it from demo data.

### F12 — Medium: category browsing is restricted to a capped local subset

`PosViewModel.LoadBackendCatalogAsync` requests 200 rows through `BackendPosCatalogGateway.LoadAsync` in `BackendRuntime.cs`. The API contract supports search and page size, but not a category cursor. Categories are rebuilt from those returned rows, and `ApplyFilters` filters that local subset.

On a shop with more than 200 matching catalog rows, category browsing cannot enumerate every matching product and off-page categories may not be offered. Text search can locate other rows but does not make this category browser complete. Remediation: authoritative category filtering with pagination, or explicitly present a search-only catalog and its bounded results. This is a latent populated-data limitation; the current zero-product database does not exhibit it.

### F13 — Medium: purchasing readback unexpectedly needs inventory permission

`RemotePurchasingInventoryService.cs:137`, `GetPurchaseAsync`, reads the purchase document and then calls `/api/inventory/stock` for each product. `PurchasingController.GetHistory/GetDocument` use PurchasingManage, whereas `InventoryController.GetStock` checks InventoryManage around line 87.

A role allowed to manage purchases but not inventory can open the purchasing screen yet fail to load a populated detail document through this adapter. The same readback is used after purchase creation, connecting this permission gap to F14. This is a conditional granular-permission scenario, not a claim that the current owner account is denied. Remediation: build purchase detail from authorized canonical purchase projections, make optional inventory enrichment independently unavailable, or specify/enforce the combined permission deliberately.

### F14 — Medium: successful purchase posting becomes a failed UI save

`RemotePurchasingInventoryService.CreatePurchaseAsync:170` receives the committed `CreatePurchaseResult`, then requires `GetPurchaseAsync` at lines 209–210. A readback exception/missing document escapes as a save failure. `NewPurchaseViewModel.SavePurchaseAsync:416` catches it and uses the rejection fallback at line 487.

The canonical purchase may already exist while the dialog remains open and the user is told creation was rejected. Retrying unchanged in the same dialog keeps its ID, but it is only in memory. The backend's unique `(SupplierId, NormalizedSupplierInvoiceNumber)` index in `PurchasingConfigurations.cs:39` mitigates duplicate same-invoice creation; this finding does not assume that uniqueness is absent.

Remediation: preserve the confirmed document ID/number, report committed-but-display-unavailable, and offer refresh/detail navigation without re-posting. `RemoteProductManagementService.ReadCommittedAggregateAsync` already distinguishes confirmed mutation from failed display refresh.

### F15 — Medium: eager histories and serial read amplification

`RemoteBackendBusinessOperationsService.cs:205/232/258`, `GetAllExpensesAsync/GetAllCustomersAsync/GetAllSuppliersAsync`, fetch every 500-row page and accumulate it, even where the public method accepts a requested page size. `RemoteBackendOperationsService.GetSupplierWorkspaceAsync` drains all four sections before returning. It can repeatedly fetch completed sections while unfinished sections advance. `RemotePurchasingInventoryService.GetPurchaseAsync` performs two awaited reads per distinct product before displaying the document.

This is O(history size) network/materialization work and approximately 2N enrichment requests for an N-product purchase. Directory/expense lists also lack advancing-cursor validation in these full-drain loops. This does **not** establish an infinite loop with the current correct backend; a stalled/ignored cursor would be an additional failure condition. Remediation: bounded UI paging, server aggregates for KPI cards, scoped/batched enrichment and explicit cursor progress validation. Current empty inventory cannot supply a representative performance measurement.

### F16 — Low: important operational status remains a feature gap

`RemoteBackendDashboardService.cs:65` always reports backup status unavailable. `RemoteBackendSettingsService.cs:117` onward supplies fixed unavailable database-size/worker/license/backup/maintenance diagnostics rather than obtaining those authoritative states. This is honest unavailable presentation, not a false green health indication.

The consequence is that the user cannot diagnose whether backup/worker tasks are current from these screens. Remediation: connect supported protected diagnostics or clearly label/remove nonfunctional controls. Do not invent status from the existence of a UI preview or an API Ready response.

### F17 — Low: save failures are described as load failures

`CustomersViewModel.cs:96`, `CustomerEditViewModel.SaveAsync` catch, falls back to “Customer details could not be loaded.” It is the save operation that failed. Remediation: operation-specific messages that distinguish rejected, unknown outcome and confirmed-but-refresh-failed states. This matters particularly when deciding whether it is safe to retry.

## Module/workflow review coverage

| Module | Paths compared and principal outcome |
|---|---|
| Startup / launcher / authentication | Shortcut, installed hashes/metadata, BackendRuntime version/readiness/registration boundary, DesktopApiClient context and loopback constraints, permission routing; F01. No login/registration executed. |
| Dashboard | Report/khata/readiness adapters, unavailable states, project projection and navigation; F01/F16. |
| POS / checkout | Catalog DTO/gateway, filters, customer adapter, complete-sale submitted request/processing/outcome handling, money display; F06/F11/F12. Existing checkout freezes submitted requests and handles unknown outcomes; not all workflows share that strength. |
| Sales history / printing | Paged history adapter/date bounds, canonical document/local durable print-job boundary. No new definite printing defect established; physical printer behaviour not tested. |
| Thaka | Installed capability versus staged status/read mapping, authoritative customer/project guards and recovery semantics; F01/F02. Source correction is not reclassified as a new source bug. |
| Purchasing | Lookup paging, create/result/detail readback, returns/void reconciliation, controller permissions and invoice uniqueness; F04/F13/F14/F15. |
| Product management | Aggregate mutation IDs, committed readback fallback, paging/version paths; F04. Commit fallback is a positive control. |
| Inventory | Delta adjustment, exact-unit/stocktake wiring, physical intake intent references; F04/F07. Durable physical intake/stocktake paths exist; no live stock mutation tested. |
| Expenses | Posting/readback, list loading, edit UI versus backend void and immutable posting; F04/F05/F07/F15. |
| Customers | Directory/search, edit/save, active-status preservation source versus installed missing suspension capability; F01/F07/F09/F17. |
| Suppliers / khata | Four-section workspace paging, payments/refunds/reversal IDs, editable payload resetting pending identity; F03/F04/F07/F09/F15. |
| Warranty | Queue selection, timeline read, lifecycle/receipt pending IDs and selection reset; F04/F08. |
| Reports | Snapshot period/state, refresh/error transitions, formatting; F06/F10. |
| Settings / backup / restore | Diagnostic projection, cashier recovery references, backup/restore service separation and durable prepare-intent path; F16. No backup or restore invoked. |

This table is a workflow review record, not a claim that every function and hostile path in the entire project was dynamically certified. Installed binary bodies were not exhaustively decompiled. Visual layouts were not re-rendered in this read-only pass, and historical UI test passes are not presented as fresh operational acceptance evidence.

## Prioritized remediation plan — recommendations only

1. **P0: establish one identifiable operational release (F01/F02).** Resolve the source/schema compatibility and deliver frontend/backend together through the previously prepared, approved rollout. Verify the installed khata read and requested suspension/card capabilities. Do not work around pending migrations or seed preview inventory.
2. **P1: preserve financial/inventory recovery identity (F03/F04/F07).** Freeze unresolved supplier payloads, use durable operation intents across the listed gaps, and prevent reentry. Reconcile before permitting a replacement intent. This can prevent double payment or stock adjustment after response loss.
3. **P1: make confirmed writes and failed reads distinct (F10/F13/F14).** Fix purchasing readback composition and commit messaging; mark unavailable/stale reports persistently.
4. **P2: correct user-facing workflows (F05/F06/F08/F09/F11/F12).** Add authoritative expense correction, consistent money precision, timeline/directory generations and real catalog filter contracts.
5. **P2: bound reads and complete diagnostics (F15/F16/F17).** Add paging/batching/progress checks and truthful, operation-specific support information.

Required regression scenarios before any future remediation is certified:

- Supplier advance commit followed by response loss, note/reference edit, drawer close and process restart: one canonical posting, old identity retained.
- Expense/delta adjustment response loss and dialog restart: no second financial/stock movement.
- Customer/supplier/expense concurrent click and edited pending payload: single-flight behaviour with clear recovery.
- Posted expense correction: authorized void/replacement, audit history retained.
- Decimal checkout totals and exact non-cash payment; displayed amount equals authoritative amount.
- Warranty A/B history requests completed in reverse order; only selected claim events displayed.
- Directory initial/refresh/search requests completed out of order; newest query wins.
- Report first-load failure and failed period change; unavailable or explicit stale values, never implied zero success.
- Purchasing-only role without InventoryManage; permitted document still readable without unintended authority widening.
- Purchase commit succeeds, display readback fails; confirmed document remains known, no rejected-sale style retry invitation.
- More than 200 POS products and more than 500 directory/history rows; category/filter completeness and bounded reads.
- Current installed release capability/schema acceptance and physical printing, using an approved test setup.

Existing tests include durable sale/stocktake/physical-intake/khata coverage, expense authoritative readback, adapter cursors, UI geometry/focus and backend warranty/expense tests. They were inspected by name/source as relevant; **no tests were executed this pass**, and this report does not claim that every scenario above is absent from the repository or already covered. Operational-write, restart, mixed-role and populated-data scenarios remain unexecuted here.
