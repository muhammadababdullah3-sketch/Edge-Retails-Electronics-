# Edge Retails — workspace, backend architecture, and frontend audit

**Audit date:** 21 September 2026. **Baseline:** working tree based on commit `8d4f273`, including modified, untracked, and ignored source files. **Verdict:** substantial backend implementation, but **not ready for a production shop or multi-terminal rollout**.

This report assesses the actual executable paths, not just architecture documents or the existence of classes. The highest risks are apparent success without durable business effects, duplicate transactions after uncertain outcomes, incomplete authorization, and production services that are not connected to the application.

## 1. Scope and evidence limits

Reviewed the solution/project structure, startup and dependency injection, business commands, persistence configurations and migrations, identity/session handling, sales and purchasing adapters, inventory and unit conversions, warranty and supplier-account implementation, reporting, backup/restore infrastructure, printing, worker, navigation/view models/XAML, tests, and release/installer scripts. Inventory found 318 C# files, 73 XAML files, and nine project files under source/tests; the project count includes temporary WPF projects. Generated build directories were excluded from source inspection. No applicable `AGENTS.md` was found in the workspace scan.

This is a broad static audit with executed build/test/model checks and deeper tracing of critical flows. It is not an exhaustive proof that every defect has been found. No live WPF interaction, printer output, clean-machine installation, database restore, concurrent till exercise, or production database inspection was performed. Findings distinguish confirmed source defects from architectural risks requiring runtime confirmation. Existing application code was not changed.

The canonical design is [the final architecture report][canonical]. Earlier audit reports are historical evidence, not proof of the current implementation. In particular, current code now contains POS drafts, supplier accounts, tracking codes, and improved warranty eligibility checks; older claims that those implementations are entirely absent are stale.

## 2. Executed verification

| Check | Observed result | Meaning |
|---|---|---|
| `dotnet build EdgeRetails.sln --no-restore --verbosity minimal` | PASS; zero warnings/errors | Current local Debug solution compiles |
| `dotnet build EdgeRetails.sln -c Release --no-restore --verbosity minimal` | PASS; zero warnings/errors | Current local Release solution compiles |
| `dotnet test EdgeRetails.sln --no-restore --verbosity minimal` | Unit: 249 passed; integration: 1 passed, 11 failed | Overall command failed; integration failures were configuration/gate failures |
| `dotnet ef migrations has-pending-model-changes --project src/EdgeRetails.Infrastructure --startup-project src/EdgeRetails.Infrastructure --no-build` | PASS; no pending model changes | EF model matches migration snapshot; does not prove live schema compatibility |
| `git check-ignore -v` on backup source | Both source directories match `.gitignore:266` | Backup source is hidden from normal Git discovery |
| Explicit ignored-source inventory | 16 backup C# files ignored and untracked | A local build includes files that normal staging omits |

The 11 integration failures comprise nine tests requiring `EDGE_RETAILS_TEST_DB`, one backup test requiring `EDGE_RETAILS_TEST_DB_HOST` and companion configuration, and one live-cutover test whose disposable-environment gate is not armed. They do **not** establish transaction or restore failures. They leave PostgreSQL behavior unverified in this audit. No destructive test gate was enabled.

No package vulnerability assessment was performed. Compilation is not a security, performance, installation, or user-workflow certification.

## 3. Architecture as implemented

```text
WPF App startup
  -> BackendRuntime (only when EDGE_RETAILS_DB is present)
     -> scoped Desktop backend adapters
        -> Application handlers / read-service interfaces
           -> Domain models and rules
           -> Infrastructure EF Core / Dapper / Npgsql
              -> PostgreSQL

  -> Demo services when backend runtime is absent
  -> Some demo-only pages even when backend runtime is present

Worker -> timer + log message; no production job processing

Production startup / licensing / printing / backup components
  -> substantial standalone code and tests
  -> incomplete runtime composition and UI attachment
```

This is a layered desktop modular monolith with a directly connected database, not a web frontend/API architecture. An HTTP service or microservices are not inherently required. However, direct database access makes the Windows account, deployed binaries, connection credentials, and PostgreSQL permissions part of the trust boundary. Application role checks alone cannot protect data from a client holding broader database privileges.

Useful foundations already present:

- Domain/Application/Infrastructure separation and explicit composition roots.
- Transaction rollback when an application `Result` fails, plus change-tracker cleanup.
- PostgreSQL advisory operation/resource locks and row locking for many business mutations.
- Unique business operation IDs, document numbers, monetary precision, and several database invariants.
- Server-side price/stock validation and historical receipt/cost snapshots.
- Quantity versus serialized inventory separation; current serialized conversion rejects fractional exact quantities before rounding.
- PIN hashing with salts and constant-time comparison.
- Backup protection, restore staging validation, signed-license validation, and print-outcome states implemented as components.
- Release evidence gates and model-drift checks exist as scripts.

The principal architectural problem is inconsistent enforcement and incomplete wiring, not an absence of all backend structure.

## 4. Prioritized findings

**Priority:** P1 = address before the affected production workflow ships; P2 = important correctness, reliability, scale, or operational hardening. A P1 architectural gap does not imply a demonstrated remote exploit. Confidence labels describe the evidence, not the business priority.

### A01 — P1 — Backup source is excluded from normal version control

**Confirmed.** [`.gitignore:266`][ignore] uses `Backup*/`, which matches the Application and Infrastructure `Production/Backup` directories. Sixteen implementation files are ignored and untracked. Other code imports their namespaces, so a normally staged version of this workspace can compile locally yet fail on a clean checkout.

**Fix:** narrow the ignore rule to intended generated directories and explicitly include these source folders. **Acceptance:** fresh checkout on another directory/machine builds and runs the backup unit tests without copying local files.

### A02 — P1 — Missing production configuration silently selects demo behavior

**Confirmed.** [BackendRuntime:108][runtime] returns null when `EDGE_RETAILS_DB` is absent. [App startup][app] continues into MainViewModel, which selects demo identity and business services. Only the preview switches are debug-gated; the null-runtime fallback remains in Release.

**Impact:** an installed app can appear usable while transactions live only in memory. **Fix:** use an explicit runtime mode; Release should show a configuration/recovery screen when required backend configuration is missing. **Acceptance:** launching Release without database configuration cannot reach a demo selling workflow.

### A03 — P1 — Production startup safeguards are not on the actual startup path

**Confirmed.** [BackendRuntime:80][runtime] invokes the older database-readiness/setup flow. [ProductionStartupCoordinator][startup] and the stronger migration-compatibility probe are not registered/called by the actual composition root. The active readiness implementation checks connectivity and pending migrations, not all ahead/diverged-history cases. License, production diagnostics/toolchain readiness, and session recovery are therefore not enforced through this path. The coordinator also accepts a maintenance barrier but does not inspect it in `RunAsync`.

**Fix:** assemble one production startup pipeline and implement its missing adapters; retain a safe recovery UI for blocked startup. **Acceptance:** missing/invalid license, incompatible history, and recovery-required maintenance state prevent normal business entry in the real Release app.

### A04 — P1 — Frontend retries discard backend idempotency

**Confirmed.** [BackendTransactionService:101][transactions] creates a fresh operation ID for each attempt. It then reads the sale after the handler commits and throws if that read fails. [CompleteSaleViewModel:390][completeui] presents the exception and permits another attempt. Purchase creation has the same new-ID/post-commit-read pattern in [BackendPurchasingInventoryService:167][purchaseadapter]. Returns also generate new IDs.

**Reproduction:** commit a sale, interrupt its detail read, and press Complete again. The second attempt has a different deduplication key and may create another sale if stock remains. **Fix:** persist one operation ID and immutable request per business intent; expose an unknown-outcome state and reconcile by that ID before retrying. **Acceptance:** fault injection after commit produces exactly one sale/payment/stock effect and a recoverable UI result.

### A05 — P1 — Business authorization does not establish an authenticated session

**Confirmed boundary gap.** [ApplicationPermissionAuthorizer][identity] accepts a caller-supplied actor ID and permission key, then loads permissions. It does not validate a current session, expiry, revocation, or actor/session correspondence. [The sale adapter][transactions] passes null for the session even though login obtains a session ID.

**Impact:** handlers can run for a supplied authorized actor without proving that actor is logged in; logout/revocation cannot govern these calls. This is an in-process/direct-database trust issue, not evidence of an exposed network endpoint. **Fix:** pass a validated execution context and enforce active session and actor identity at command/query entry. **Acceptance:** ended, revoked, missing, and mismatched sessions cannot read protected data or mutate business state.

### A06 — P1 — Authorization is inconsistent across command families

**Confirmed.** [CashSessionHandlers][cash], [StocktakeHandlers][stocktake], [ProductUnitHandlers][units], and [TransferInventoryConditionHandler:321][condition] lack the permission authorizer used by sales/purchasing/warranty handlers. TransferInventoryConditionHandler is registered in the production service collection. Query services likewise have no common authenticated boundary.

**Fix:** introduce a mandatory command/query authorization policy, including explicit system/bootstrap exceptions, and cover each public entry point. **Acceptance:** unauthorized callers cannot adjust stock, change unit/barcode definitions, open/close cash, or read restricted financial data by invoking application services directly.

### A07 — P1 — Deactivating a role does not remove its effective permissions

**Confirmed.** [IdentityReadRepository:87][identityrepo] checks user activity and permission activity but does not require the user's Role to be active. Login rejects an inactive role, while later command authorization obtains permissions from this weaker query.

**Fix:** require an active role during every effective-permission resolution, alongside session validation. **Acceptance:** deactivate a role after login and verify its next protected operation is denied.

### A08 — P1 — Four-digit login has no backend attempt limiter

**Confirmed.** [AuthenticateUserHandler][identity] validates a four-digit PIN and verifies its hash, but has no persisted failed-attempt counter, cooldown, lockout, or failed-login audit. Hashing is present and should be retained.

**Fix:** add an account/terminal-aware retry policy, safe recovery, and security-event audit. **Acceptance:** repeated incorrect PIN attempts are throttled across restarts and concurrent login requests; successful login resets the intended state.

### A09 — P1 — Unknown payment enum values bypass payment rules

**Confirmed static path.** [CompleteSaleHandler:675][sale] validates Cash, Bank, and Other but returns success for an undefined enum value with nonnegative tender. Cash drawer posting only occurs for Cash. [SalePaymentConfiguration:187][saleconfig] has amount checks but no allowed-method constraint.

**Impact:** a direct command with an invalid method can be recorded as a paid sale without the intended payment validation. **Fix:** reject undefined methods and enforce allowed values in the schema; apply the same review to other boundary enums. **Acceptance:** an out-of-range method rolls back the entire sale with a validation error.

### A10 — P2 — POS draft completion cannot replay its successful result

**Confirmed.** [CompletePosDraftHandler:329][drafts] rejects a non-open draft before reaching CompleteSale's operation-ID replay. After a successful conversion with a lost response, retrying the same command returns `draft_not_open`, not the committed sale. Save/cancel also allow omitted expected versions, weakening stale-edit protection for callers that omit them.

**Fix:** persist the converted sale/operation relationship and reconcile an identical replay before open-state validation; require versions for edits of existing drafts. **Acceptance:** identical completion retry returns the original sale, while changed-payload reuse and stale edits fail explicitly.

### A11 — P1 — Maintenance coordination is local to a Windows profile, not the shared database

**Confirmed topology gap; race behavior needs live testing.** [Infrastructure registration:139][di] defaults maintenance state to LocalApplicationData. [The file barrier][barrier] coordinates access to those local files. Another terminal—or a Worker running as a different account—can use a different barrier while sharing PostgreSQL. [EfTransactionRunner][platform] performs a point-in-time check before beginning its transaction; it does not retain a shared write lease through commit.

**Fix:** define a database-wide maintenance/write coordination protocol with draining/fencing and a consistent recovery identity. Local files may remain supplemental state. **Acceptance:** simultaneous writes from two terminals and a service account cannot cross restore/cutover fencing; a writer already in flight is handled deterministically.

### A12 — P2 — Canonical database durability checks are not implemented

**Confirmed implementation gap.** [NpgsqlDatabaseReadinessProbe][dbprobe] reads database name and server version. Searches found no source/startup checks for the canonical `fsync`, `synchronous_commit`, or `full_page_writes` policy. This does not establish that the installed database is configured unsafely; its settings were not inspected.

**Fix:** implement the agreed durability/read-write readiness checks and safe diagnostic failure states. **Acceptance:** an isolated database deliberately violating each required setting cannot enter production-write mode.

### A13 — P2 — Audit immutability is enforced only through selected EF save paths

**Confirmed boundary limitation.** [EdgeRetailsDbContext][dbcontext] rejects tracked modifications/deletions of BusinessAuditEvent in two save overrides. No database grant/revoke or audit trigger enforcement was found in the inspected source/migration/SQL files. Raw SQL or a sufficiently privileged database client can bypass this application check; deployed database privileges remain unverified.

**Fix:** deploy a least-privilege runtime role and database-level protection for append-only records; cover all relevant save overloads and avoid implying tamper-proof storage. **Acceptance:** runtime credentials cannot update/delete audit rows through direct SQL.

### A14 — P1 — Sale commit and durable print intent are not atomic

**Confirmed.** [CompleteSaleHandler][sale] saves the sale without an associated persistent print intent. [PrintDocumentHandler][printing] creates a separate job later through a file-backed store. A crash between business commit and job creation leaves no pending receipt request to recover. Receipt snapshots are useful but do not provide a dispatch queue.

**Fix:** write an outbox/print-intent row in the sale transaction, then dispatch after commit. Reprinting must never re-execute the sale. **Acceptance:** terminate the process immediately after sale commit; restart identifies the sale's pending/unknown print outcome without another financial write.

### A15 — P2 — JSON print storage has cross-instance and growth limits

**Confirmed design limitation.** [JsonPrintJobStore:13][printstore] rewrites a whole JSON list under an instance-local semaphore and rejects stores above 4 MiB. Separate instances/processes do not share that lock, and no archival/removal path is exposed in the implementation.

**Impact:** concurrent read/modify/write can lose updates; sustained use eventually hits the size ceiling. **Fix:** use transactional job storage with optimistic transitions and retention, or explicitly constrain and enforce a single writer with bounded archival. **Acceptance:** concurrent transitions from separate processes preserve both jobs, and a volume test demonstrates retention beyond the current ceiling.

### A16 — P1 — Worker has no operational jobs

**Confirmed.** [Worker.cs][worker] logs once per second; [Worker Program][workerprogram] registers only that worker. No backup scheduling, print dispatch, retention, job retry, or health-heartbeat processing is connected.

**Fix:** implement the required hosted job services with durable state, cancellation, bounded retries, and observable status. **Acceptance:** an installed worker performs the configured backup/dispatch tasks and exposes their last success/failure, including restart recovery.

### A17 — P1 — Required cash-session workflow is unavailable to the operator

**Confirmed.** [The sale handler][sale] requires an open cash session for cash sales. Open/close handlers exist in [CashSessionHandlers][cash], but they are absent from the main DI registrations and Desktop usage/navigation. First setup does not provide a substitute cash-opening workflow.

**Impact:** a newly configured shop cannot complete a normal cash sale through the reviewed UI unless a cash session is established elsewhere. **Fix:** wire authenticated opening, closing, count/reconciliation, and recovery actions. **Acceptance:** an empty installed shop can open the drawer, sell for cash, close it, and reconcile the expected amount through the UI.

### A18 — P1 — Serialized electronics workflows stop at the frontend

**Confirmed.** [NewSaleViewModel:467][pos] rejects all serialized backend cart items pending unit selection. [BackendPurchasingInventoryService:177][purchaseadapter] rejects serialized intake and similarly blocks serialized returns. Backend handlers support exact identities, so this is a missing workflow rather than missing inventory primitives.

**Fix:** provide serial/IMEI intake, exact-unit selection, duplicate validation, and exact-unit return controls. **Acceptance:** receive, sell, return, and trace a serialized item end to end without direct database editing.

### A19 — P1 — Product creation and stock adjustment are unavailable in backend mode

**Confirmed.** [InventoryViewModel:206][inventoryui] explicitly blocks Add Product and stock adjustment when the backend adapter is present. The UI's demo editors cannot establish production catalog/stock data.

**Fix:** implement authenticated catalog create/edit and audited stock-adjustment flows, including units, tracking policy, provenance, and opening balances. **Acceptance:** first-run operators can configure and stock an item, then sell it using only production UI paths.

### A20 — P1 — Dashboard displays fabricated operational figures in backend mode

**Confirmed.** [DashboardViewModel:49][dashboard] initializes fixed sales/profit/expense/project values and connection/backup status to healthy, then always calls `LoadDemoData`. The [page factory][pages] supplies no backend dashboard provider.

**Fix:** supply one authoritative dashboard query and explicit loading, stale, empty, and unavailable states. **Acceptance:** figures reconcile to database documents and visibly change after business operations; unavailable health is never rendered as confirmed healthy.

### A21 — P1 — Settings presents successful edits that do not update production state

**Confirmed.** [SettingsViewModel:267][settings] always uses DemoSettingsState. Shop/receipt/user/category/unit saves mutate that state. User PIN input is required in the editor but is not passed to `SaveUser`. License, backup, restore, and diagnostics remain previews. The [production receipt snapshot provider][receipts] reads PostgreSQL, so the shop/receipt settings shown as saved can differ from future receipts.

**Fix:** replace each settings action with an authorized persisted backend contract; disable unattached actions clearly until implemented. **Acceptance:** changes survive restart, affect subsequent receipts/login as intended, and write the appropriate audit event.

### A22 — P1 — Receipt controls do not invoke production printing

**Confirmed.** [SaleDetailViewModel:424][saledetail] displays a preview toast. CompleteSale collects PrintReceipt, but [BackendTransactionService][transactions] does not invoke the production print handler. The production document-source router also requires adapters that are not assembled by the current DI composition.

**Fix:** connect committed documents to the print-intent/dispatch flow and truthful job status. **Acceptance:** check and uncheck Print Receipt, simulate a printer failure, and retry/reprint without adding another sale.

### A23 — P1 — History screens silently truncate business results

**Confirmed.** [BackendTransactionService:138][transactions] loads only `PageSize: 200`, then details for those rows; [BackendPurchasingInventoryService:125][purchaseadapter] does the same for purchases. [SalesHistoryViewModel][saleshistory] applies period/search filters and calculates totals on its local list.

**Impact:** after more than 200 sales, a period total or search can omit valid documents without saying it is partial. **Fix:** propagate server-side filter/cursor/count contracts; query aggregate totals independently of loaded rows. **Acceptance:** a dataset of at least 1,000 documents returns older matches and correct period totals across page boundaries.

### A24 — P2 — Read paths are not ready for a large shop database

**Confirmed query shapes; latency not benchmarked.** History adapters issue a detail read per row. [PosCatalogReadService][catalogread] loads all sellable catalog entries. [BusinessOperationsReadServices][businessreads] materializes large customer/sales/return collections, and reporting performs multiple sequential queries for each time slice. [SalesHistoryView.xaml][historyview] uses a scrolling StackPanel-based layout that also merits virtualization verification.

**Fix:** fetch summary DTO pages, load details on demand, aggregate in SQL, debounce/cancel search, and verify bounded virtualized rendering. **Acceptance:** benchmark realistic large datasets with query counts, execution plans, memory, and UI interaction latency recorded; establish limits before declaring performance complete.

### A25 — P1 — POS compares quantities in different units

**Confirmed.** [PosCatalogReadService:43][catalogread] returns `stock.SellableQty` in base units while returning a selected sale-unit symbol and converted price. The gateway does not carry the conversion factor. [PosCartItemViewModel:49][cart] clamps entered sale-unit quantity against that base-unit stock number.

**Example:** 24 individual pieces with a sale unit of one 12-piece box are shown as 24 available boxes rather than two. The backend's base-quantity validation still prevents actual overselling, but the operator sees false availability and avoidable checkout failures. Factors below one can instead block valid quantities. **Fix:** carry base quantity, selected-unit quantity, and factor explicitly. **Acceptance:** factors of 12, 1, and 0.5 yield matching UI/backend availability and totals.

### A26 — P2 — Frontend quantity and money precision differ from the domain

**Confirmed.** [PosCartItemViewModel][cart] rounds quantities to two decimals and displays prices/line totals with `N0`; domain quantity precision is six decimals, and backend money rounding uses AwayFromZero while the UI's default `Math.Round` uses a different midpoint rule.

**Impact:** valid fine-grained quantities cannot be entered consistently, cents are hidden, and midpoint totals can disagree. **Fix:** define shared unit-aware precision and monetary rounding/display contracts. **Acceptance:** fractional cable quantities, non-integer prices, and midpoint inputs display and submit exactly the amount accepted by the backend.

### A27 — P1 — New backend modules lack the required UI and composition paths

**Confirmed.** [NavigationTarget][navigation] and the [page factory][pages] contain no dedicated Warranty target. Searches found no Desktop consumption of POS draft, quotation, supplier-payment/refund, or stocktake handlers. Some of those handlers also lack registrations. Supplier profile pages do not amount to the canonical Khata operations/queues.

**Fix:** implement capability-by-capability contracts for POS hold/resume, quotations, supplier account operations, warranty queues, and stocktake; test actual screen-to-handler calls. **Acceptance:** every promoted V1 workflow in canonical sections 209–232 has an executable operator path and permission-denial path, not just a model and migration.

### A28 — P1 — Window shutdown can deadlock on asynchronous sign-out

**Confirmed blocking path; actual hang depends on asynchronous completion.** [App.OnExit][app] calls MainViewModel.Dispose. [MainViewModel:212][main] synchronously waits on SignOutAsync with GetResult; [BackendIdentityService][backendidentity] uses ordinary awaits. On the WPF dispatcher, a continuation needing that same context cannot resume while Dispose blocks it. Similar sync wrappers exist in the transaction adapter.

**Fix:** perform bounded asynchronous shutdown before exiting; remove sync-over-async application calls and handle unavailable database cleanup explicitly. **Acceptance:** close the app while sign-out is delayed or disconnected; the window/process exits within a defined timeout with recoverable session state.

### A29 — P2 — Reporting uses terminal timezone rather than shop timezone

**Confirmed.** [SystemClock][platform] resolves the shop date in Asia/Karachi, while [ReportingReadService:468][businessreads] constructs reporting boundaries from `TimeZoneInfo.Local`. Desktop history uses local timestamps/dates too.

**Impact:** tills configured in another timezone can disagree about daily sales and closing-period totals. **Fix:** use one shop timezone service and UTC query boundaries, converting only for display. **Acceptance:** two terminals with different Windows timezones produce identical shop-day reports around midnight.

### A30 — P2 — Connectivity and stock-health indicators do not reflect checked state

**Confirmed.** [UserSessionContext][login] exposes a fixed IsOnline value. [InventoryViewModel:285][inventoryui] sets StockTruthIssueCount to zero after loading rather than running reconciliation; cached inventory refresh is conditioned on the initial load flag. There is no shared reconnect/revalidation state machine connected to these screens.

**Fix:** distinguish unknown, healthy, stale, disconnected, and reconciliation-failed states; refresh after relevant mutations and before reconnect writes. **Acceptance:** an external till change/disconnect cannot leave an apparently verified, current stock-health indicator indefinitely.

### A31 — P1 — Passing tests do not cover the production application boundary

**Confirmed coverage gap.** [Unit test project][unittests] references Domain/Application/Infrastructure, not Desktop. Many forensic tests inspect source text. Production DI, WPF workflow wiring, the unknown-outcome retry path, and shutdown behavior are therefore not established by the 249 passing tests. PostgreSQL tests could not execute their business assertions in this audit. No `.github` workflow directory was present; external CI configuration was not inspected.

**Fix:** add composition resolution tests for required production capabilities, adapter failure-injection tests, and a small live desktop/database workflow suite. Provision isolated PostgreSQL in an automated lane; keep restore cutover separately gated. **Acceptance:** a clean checkout executes meaningful database tests and fails when demo services enter production composition or required handlers are unwired.

### A32 — P1 — Release cleanup accepts an unchecked recursive deletion target

**Confirmed static defect; not executed.** [Publish-Release.ps1:31][publish] recursively deletes `Join-Path $root $Output`. Output is caller-configurable, and no containment/non-root check precedes deletion. A mistaken `.` or parent-traversal output can target the workspace or another directory once preflight passes.

**Fix:** resolve and validate the final absolute directory against a dedicated artifacts root; reject workspace root, parents, traversal escapes, and unsafe links. **Acceptance:** safe-path tests reject these inputs without deleting anything and allow only the intended artifact subdirectory.

### A33 — P2 — Operational error handling is not assembled into the running desktop

**Confirmed integration gap.** Startup is async-void with no encompassing failure boundary in [App][app]. Adapter/UI failures commonly show exception messages in toasts or validation text. Production diagnostics/audit abstractions exist, but implementations/registration for production authorization and audit sinks are missing from the active composition. The Worker logs liveness rather than job outcomes.

**Fix:** add a central error/correlation/logging policy with safe user messages, full local diagnostic detail, and supported collection/rotation. **Acceptance:** a database timeout, failed startup composition, and post-commit read failure each produce a useful correlated diagnostic record and an accurate operator recovery state.

### A34 — P1 — POS Thaka mode still executes demo mutations with a backend catalog

**Confirmed.** [NewSaleViewModel:73 and 511][pos] always builds its Thaka project picker from DemoRetailState and calls `_retailState.IssueMaterialBatch`. Mode switching is not blocked in backend mode. The separate backend Thaka workspace does not change this POS command path.

**Impact:** the POS can issue real catalog items into a demo project, show success, clear the cart, and make no corresponding PostgreSQL issue/stock/account entry. **Fix:** route POS project selection and material issue through the backend Thaka service, or disable this mode until attached. **Acceptance:** material issuance from POS and from the Thaka workspace produces the same authoritative records and stock movement, surviving restart.

## 5. Capability coverage matrix

| Capability | Backend implementation | Reachable production frontend | Audit assessment |
|---|---|---|---|
| Setup and login | Persistent handlers/repositories | Backend path exists | Needs session security, throttling, startup integration |
| Quantity sales and returns | Transactional handlers and reads | Connected | Retry identity, pagination, units, printing gaps |
| Serialized sales/purchases/returns | Exact-unit handler support | Explicitly blocked | Not operational end to end |
| Purchasing | Handler, provenance, costs, supplier entries | Quantity path connected | New-ID retry and history limits |
| Cash drawer | Open/manual/close handlers | No reviewed operator path | Cash-sale blocker on fresh shop |
| Catalog maintenance | Models/unit handlers | Production Add Product blocked | Bootstrap/maintenance incomplete |
| Stocktake/condition adjustment | Substantial handlers | Not attached to required UI paths | Authorization and composition gaps |
| Thaka | Backend services and workspace | Partially connected | POS mode still demo-backed |
| Customers/supplier profiles/expenses | Persistent handlers and reads | Connected adapters | Broad reads and authorization boundary review needed |
| Supplier Khata | Ledger/payment/refund/reversal code | Operations not connected | Backend presence is not workflow completion |
| Warranty | Claims, transitions, replacement/recovery code | Dedicated screen missing | Eligibility improvements present; live concurrency unverified |
| POS draft/quotation | Models and handlers | No reviewed Desktop use | Replay and workflow gaps |
| Reports | Backend read service | Connected | Timezone, aggregation/query-volume gaps |
| Dashboard/settings | Supporting data exists elsewhere | Demo-backed | Misleading production UI |
| Printing | Handler/engine/state components | Preview controls | Missing composition and transactional intent |
| Backup/restore/license | Substantial components | Settings previews | Ignored source, runtime wiring, recovery validation pending |
| Worker/deployment | Worker template; WiX/release scripts | Not a proven installed lifecycle | Job composition and clean-machine proof missing |

## 6. Design decisions and evidence still required

These are unresolved architecture/deployment questions, not assertions that the current database has already failed:

1. **Trust and topology:** define standalone versus LAN deployment, whether terminals are trusted, credential storage, runtime versus migration/maintenance privileges, transport requirements, and server-owned maintenance authority. Keep the modular monolith unless a separate service is justified by that trust boundary.
2. **Transaction retry contract:** define stable command identity, payload mismatch handling, retention, and how an operator resolves an uncertain outcome. Do not add blind database retries before this contract is enforced end to end.
3. **Canonical lock order:** handlers use different resource families. Demonstrate a single documented acquisition hierarchy and adversarial concurrency tests for sale/return/stocktake/warranty/supplier settlement. Existing locks are useful but not a proof of deadlock freedom.
4. **Backup/recovery operation:** establish actual schedule, retention, key recovery/escrow, restore permissions, target identity, write fencing, recovery time/data-loss objectives, and a rehearsed disposable restore. Component tests cannot establish recoverability of a shop deployment.
5. **Money and ledger reconciliation:** reconcile supplier entries to purchases/returns/voids/payments/refunds/warranty credits, stock balances to movements/lots/unit states, and cash totals to cash movements on a representative database.
6. **Migration policy:** six migrations currently exist and the model matches their snapshot. Whether to squash or retain this chain depends on deployed database history. Multiple migrations alone are not a defect; do not rewrite history without confirming that authority.
7. **Deployment closure:** the MSI contains application binaries, and the bundle chains that MSI. Prove PostgreSQL provisioning/configuration, runtime credentials, controlled migrations, worker installation, printer setup, signed release policy, upgrade/uninstall data preservation, and first-run support on a clean machine. Installer lifecycle was not exercised here.
8. **UI validation:** keyboard-only checkout, focus restoration, error recovery, screen-reader labels, high DPI, small displays, theme contrast, and large-list virtualization require live WPF checks. Source inspection does not certify visual/accessibility quality.

## 7. Recommended implementation sequence

### Gate 1 — Prevent misleading or duplicate business outcomes

Resolve A01–A04, A20–A22, and A34 first. Make production mode explicit, retain business operation IDs across uncertain results, stop demo data/actions appearing authoritative, and put required source under version control. Hide or truthfully disable incomplete production capabilities while they are being attached.

### Gate 2 — Establish one backend enforcement boundary

Resolve A05–A09, A11–A13, and A17. Use an authenticated execution context, complete command/query authorization, active-role enforcement, login protection, validated enums, database-wide maintenance policy, least privilege, and a usable cash workflow.

### Gate 3 — Finish shop workflows and correctness

Resolve A10, A18–A19, A23, A25–A30. Complete serialized/catalog/draft/Khata/warranty/stocktake screens, correct unit/precision/timezone contracts, use accurate paginated totals, and eliminate blocking shutdown. Retest each feature from the screen through PostgreSQL rather than only constructing handlers in isolation.

### Gate 4 — Make production services operable

Resolve A14–A16, A24, A31–A33. Add transactional print intent and dispatch, worker jobs, diagnostics, safe release paths, benchmarked reads, and automated clean-checkout/database validation. Rehearse installation, upgrade, backup, restore, and reconnect on an isolated environment.

## 8. Minimum release acceptance scenarios

- Fresh checkout builds with no local-only source and passes all required composition checks.
- Missing configuration cannot enter a selling screen backed by demo state.
- First setup → authenticated login → product creation → purchase → cash opening → sale → return → closing works through the app.
- Serialized intake → exact-unit sale → return/warranty → supplier resolution preserves unit/lot provenance.
- A lost response after commit and a process restart do not duplicate sale, purchase, refund, payment, or stock effects.
- Revoked session, inactive role, and unauthorized command/query are rejected at the backend boundary.
- More than 200 records do not alter search completeness or reporting totals.
- Non-base units, six-decimal quantities, midpoint prices, and shop-midnight dates reconcile between UI, documents, and database.
- Printing failure affects only print state; restart recovers committed-document print intent.
- Two tills and a Worker respect one maintenance authority during recovery; no simulated/demo fallback appears after connectivity loss.
- Backup restore on a disposable environment passes integrity, schema, ledger, stock, and key-recovery checks.
- Clean-machine installation and upgrade preserve business data and provide working required dependencies.

**Release recommendation:** retain the current architecture as a useful foundation, but treat the workspace as an incomplete production integration. Build success and a synchronized EF model are verified; trustworthy end-to-end shop operation is not.

<!-- Evidence links are appended below using absolute workspace paths. -->
