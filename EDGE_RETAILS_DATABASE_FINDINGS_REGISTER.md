# Database Findings Register

Read-only audit findings. No fixes are implemented. Severity uses business consequence, not absence of a database trigger alone. Each finding includes evidence, consequence, ownership and remediation boundary.

## DB-001 — Committed financial and audit facts are mutable/deletable by SQL

- **Severity / domain / owner:** HIGH / Append-only / financial integrity / CURRENT_DATABASE_SCHEMA
- **Affected tables:** audit.business_events; finance.cash_movements; finance.supplier_account_entries; sales.sales; inventory.units
- **Affected entity/configuration:** EdgeRetailsDbContext append-only guard; ledger configurations
- **Affected migration:** none
- **Affected query/handler:** See affected entity/configuration above.
- **Current behavior / exact gap:** probe UPDATE sale header, cash movement, supplier entry and DELETE business audit row each affected one seeded row; every probe rolled back; no DB triggers/functions; restrictive FK does not prevent parent row updates/deletes when unreferenced.
- **Expected invariant / business consequence:** An authorized SQL role can rewrite transaction totals or remove audit/ledger facts without matching reversals. Direct test of granted runtime role not available.
- **Concurrency consequence:** SQL can bypass ChangeTracker append-only check; normal app concurrency/transactions do not apply to arbitrary SQL.
- **Data-repair consequence:** Potentially unrecoverable commercial history and report divergence.
- **Classification:** HIGH_RISK_DIRECT_SQL_INTEGRITY_GAP. Database guards/least-privilege policy direction.
- **Can schema represent correct state?** Yes for supported source paths; the gap concerns states permitted by direct SQL or noted model mismatch.
- **Migration likely required?** Likely if database enforcement is approved.
- **Recommended direction / tests / PostgreSQL proof:** CRITICAL Owner boundary: current schema and operational principal. Migration likely if DB-enforced immutability chosen; tests/proofs must use disposable PostgreSQL. No trigger mandate presumed.
- **Out-of-scope dependencies:** Phase 8 BusinessDate/time zone; Phase 9 backup/printing operational reliability; Phase12 replay uniformity; Pass5 Missing status and protected TrackingCode authority remain governed.

## DB-002 — Sale and settlement arithmetic is not enforced as a relational invariant

- **Severity / domain / owner:** HIGH / Sales / money / CURRENT_DATABASE_SCHEMA
- **Affected tables:** sales.sales; sales.sale_items; sales.sale_payments; sales.returns
- **Affected entity/configuration:** SalesConfigurations; sale/return handlers
- **Affected migration:** initial baseline
- **Affected query/handler:** See affected entity/configuration above.
- **Current behavior / exact gap:** Sale totals and each row have nonnegative checks, but database has no deferred aggregate assertion tying header to lines/payment. Current SQL harness proves individual rows mutable; it does not attempt inconsistent aggregate write.
- **Expected invariant / business consequence:** A direct insert/update can set a valid-looking header and payment that disagree with item economics; report reads header totals and line COGS separately.
- **Concurrency consequence:** Application transaction serializes and validates supported workflows, but direct SQL bypass is unguarded.
- **Data-repair consequence:** Net sales/profit/cash can disagree while each row satisfies local CHECK/FK.
- **Classification:** SHOULD_HAVE_DATABASE_CONSTRAINT or controlled DB write authority; evaluate constraint/transaction design.
- **Can schema represent correct state?** Yes for supported source paths; the gap concerns states permitted by direct SQL or noted model mismatch.
- **Migration likely required?** Likely if database enforcement is approved.
- **Recommended direction / tests / PostgreSQL proof:** HIGH Migration likely only after bounded proof/design. Ensure exchanges/multi-line settlement and deferred checks are considered; do not trigger every invariant reflexively.
- **Out-of-scope dependencies:** Phase 8 BusinessDate/time zone; Phase 9 backup/printing operational reliability; Phase12 replay uniformity; Pass5 Missing status and protected TrackingCode authority remain governed.

## DB-003 — Exact-unit product/source lineage can be contradicted through independent valid FKs

- **Severity / domain / owner:** HIGH / Physical identity / provenance / TRACKING_FROZEN
- **Affected tables:** inventory.units; purchasing.purchase_items; catalog.supplier_products
- **Affected entity/configuration:** InventoryUnitConfiguration; PhysicalUnitCreationAuthority
- **Affected migration:** initial baseline
- **Affected query/handler:** See affected entity/configuration above.
- **Current behavior / exact gap:** ProductId, SupplierProductId, InventoryLotId, SourcePurchaseItemId are separate FKs; they do not prove that all referenced rows describe the same product/supplier/source. Probe showed ProductId can be changed to another valid product while existing TrackingCode/source facts stay unchanged.
- **Expected invariant / business consequence:** Database accepts cross-product physical provenance and tracking snapshot contradiction.
- **Concurrency consequence:** Supported creation authority locks product and supplier-product and writes consistent links. Direct SQL/bulk maintenance bypasses this.
- **Data-repair consequence:** Physical identity remains unique but its commercial origin and product attribution may become unreliable.
- **Classification:** HIGH_RISK_DIRECT_SQL_INTEGRITY_GAP; tracking authority protected.
- **Can schema represent correct state?** Yes for supported source paths; the gap concerns states permitted by direct SQL or noted model mismatch.
- **Migration likely required?** Likely if database enforcement is approved.
- **Recommended direction / tests / PostgreSQL proof:** HIGH Any composite-FK design must preserve frozen tracking authority and be approved separately. Do not redesign TrackingCode or sequences.
- **Out-of-scope dependencies:** Phase 8 BusinessDate/time zone; Phase 9 backup/printing operational reliability; Phase12 replay uniformity; Pass5 Missing status and protected TrackingCode authority remain governed.

## DB-004 — Inventory-unit identity and status columns admit structurally invalid values

- **Severity / domain / owner:** HIGH / Inventory integrity / exact identity / CURRENT_DATABASE_SCHEMA
- **Affected tables:** inventory.units.status; tracking_code; supplier_product_id; item_sequence; supplier_code_snapshot; product_sku_snapshot
- **Affected entity/configuration:** InventoryUnitConfiguration; InventoryUnitAccountingRules
- **Affected migration:** initial baseline
- **Affected query/handler:** See affected entity/configuration above.
- **Current behavior / exact gap:** Schema permits null identity tuple and arbitrary integer status; direct disposable probes cleared identity tuple and stored status=999. Model relies on application policy/status maps and nullable fields used by some origins/history.
- **Expected invariant / business consequence:** Direct SQL can create an untracked unit or unknown accounting status. Supported app paths reject missing identity and unknown status but no catalog CHECK encodes this.
- **Concurrency consequence:** Could bypass exact unit protections and create states no handler knows how to account for.
- **Data-repair consequence:** Inventory ownership, stock and cost cannot be inferred/repaired from status or identity alone.
- **Classification:** SHOULD_HAVE_DATABASE_CONSTRAINT / HIGH_RISK_DIRECT_SQL_INTEGRITY_GAP. Respect the Pass5 Missing-status stop and tracking freeze.
- **Can schema represent correct state?** Yes for supported source paths; the gap concerns states permitted by direct SQL or noted model mismatch.
- **Migration likely required?** Likely if database enforcement is approved.
- **Recommended direction / tests / PostgreSQL proof:** HIGH Migration likely if tightened. Must profile legitimate origin/lifecycle null cases before constraint design; do not add/reuse Missing status without the existing approval.
- **Out-of-scope dependencies:** Phase 8 BusinessDate/time zone; Phase 9 backup/printing operational reliability; Phase12 replay uniformity; Pass5 Missing status and protected TrackingCode authority remain governed.

## DB-005 — Inventory keyset pagination fails when a name cursor is supplied

- **Severity / domain / owner:** MEDIUM / Query correctness / CURRENT_DATABASE_SCHEMA
- **Affected tables:** InventoryOverviewReadService.GetStockPageAsync
- **Affected entity/configuration:** InventoryOverviewReadService.cs
- **Affected migration:** not applicable
- **Affected query/handler:** See affected entity/configuration above.
- **Current behavior / exact gap:** Owned PostgreSQL execution with three temporary product rows reached second page using BeforeName/BeforeProductId; EF Core threw InvalidOperationException translating StringComparison String.Compare. Error capture and rollback transaction verified.
- **Expected invariant / business consequence:** Users requesting next stock page may see failure rather than later products.
- **Concurrency consequence:** Not concurrency-dependent; occurs on normal paged request.
- **Data-repair consequence:** No durable data repair; read/API correction and regression proof needed.
- **Classification:** Query implementation defect; database schema migration not shown necessary.
- **Can schema represent correct state?** Yes for supported source paths; the gap concerns states permitted by direct SQL or noted model mismatch.
- **Migration likely required?** Not proven / query-only / operational evidence required.
- **Recommended direction / tests / PostgreSQL proof:** MEDIUM Owner: Phase7Pass5/live implementation scope or appropriate query owner; audit made no source change.
- **Out-of-scope dependencies:** Phase 8 BusinessDate/time zone; Phase 9 backup/printing operational reliability; Phase12 replay uniformity; Pass5 Missing status and protected TrackingCode authority remain governed.

## DB-006 — Open-stocktake unique index semantics differ between model and physical migration

- **Severity / domain / owner:** LOW / Schema drift / CURRENT_DATABASE_SCHEMA
- **Affected tables:** inventory.stocktakes.status; ux_stocktakes_single_open
- **Affected entity/configuration:** StocktakeConfiguration; InitialProductionBaseline migration
- **Affected migration:** initial baseline
- **Affected query/handler:** See affected entity/configuration above.
- **Current behavior / exact gap:** EF model defines partial unique on status with active-status predicate; physical migration uses constant expression `(1)` and same predicate, permitting only one row across all active states. EF reports no pending changes.
- **Expected invariant / business consequence:** Physical behavior is stricter than model: two simultaneously active rows in different statuses rejected by DB even though model definition implies they can coexist.
- **Concurrency consequence:** No duplicate rows observed; app uses one-open-stocktake lock/behavior.
- **Data-repair consequence:** Unexpected uniqueness conflicts if workflow permits concurrent phase states; model snapshot will not expose this custom SQL.
- **Classification:** SUSPICIOUS_DRIFT; physical restriction may be canonical intent.
- **Can schema represent correct state?** Yes for supported source paths; the gap concerns states permitted by direct SQL or noted model mismatch.
- **Migration likely required?** Not proven / query-only / operational evidence required.
- **Recommended direction / tests / PostgreSQL proof:** LOW Clarify authority then align migration/model representation; append-only migration if production baseline frozen.
- **Out-of-scope dependencies:** Phase 8 BusinessDate/time zone; Phase 9 backup/printing operational reliability; Phase12 replay uniformity; Pass5 Missing status and protected TrackingCode authority remain governed.

## DB-007 — Five FK child-key relationships lack unconditional leading indexes

- **Severity / domain / owner:** LOW / Index health / CURRENT_DATABASE_SCHEMA
- **Affected tables:** inventory.stocktake_unit_checks; sales.pos_draft_items; warranty.claim_item_units
- **Affected entity/configuration:** Inventory/Warranty/Sales configurations
- **Affected migration:** initial baseline
- **Affected query/handler:** See affected entity/configuration above.
- **Current behavior / exact gap:** Catalog FK leading-index audit found five: stocktake_item_id, selected_inventory_unit_id, and original/active-original/replacement unit IDs. Warranty filtered indexes omit null rows.
- **Expected invariant / business consequence:** Parent-row delete/update FK checks and child lookup may scan growing tables. Most business FKs use Restrict; only relevant access/removal paths are affected.
- **Concurrency consequence:** No immediate correctness failure.
- **Data-repair consequence:** Potential growing latency and lock duration during parent modifications.
- **Classification:** Hardening/scale concern, not an orphan defect.
- **Can schema represent correct state?** Yes for supported source paths; the gap concerns states permitted by direct SQL or noted model mismatch.
- **Migration likely required?** Not proven / query-only / operational evidence required.
- **Recommended direction / tests / PostgreSQL proof:** LOW Owner CURRENT_DATABASE_SCHEMA. Validate workload/cardinality before index change.
- **Out-of-scope dependencies:** Phase 8 BusinessDate/time zone; Phase 9 backup/printing operational reliability; Phase12 replay uniformity; Pass5 Missing status and protected TrackingCode authority remain governed.

## DB-008 — Successful operation outcome can omit payload fingerprint

- **Severity / domain / owner:** MEDIUM / Replay / idempotency / PHASE12
- **Affected tables:** system.operation_outcomes
- **Affected entity/configuration:** OperationOutcomeConfiguration; selected mutation handlers
- **Affected migration:** Phase2DurableOperationOutcome
- **Affected query/handler:** See affected entity/configuration above.
- **Current behavior / exact gap:** Fingerprint nullable; fixture had successful committed outcomes with null fingerprint, including supported purchase paths. Unique ClientOperationId is database enforced. Existing code fingerprints some flows, while Phase12 owns uniform policy.
- **Expected invariant / business consequence:** GUID reuse with different command payload may not be distinguishable for flows that omit fingerprint.
- **Concurrency consequence:** Persistence unique constraint prevents duplicate row, but cannot detect changed meaning without fingerprint.
- **Data-repair consequence:** Ambiguous replay requires manual support if result payload cannot be matched.
- **Classification:** Application-only/partial replay invariant; owned by PHASE12.
- **Can schema represent correct state?** Yes for supported source paths; the gap concerns states permitted by direct SQL or noted model mismatch.
- **Migration likely required?** Not proven / query-only / operational evidence required.
- **Recommended direction / tests / PostgreSQL proof:** MEDIUM Do not absorb global replay framework into current schema audit.
- **Out-of-scope dependencies:** Phase 8 BusinessDate/time zone; Phase 9 backup/printing operational reliability; Phase12 replay uniformity; Pass5 Missing status and protected TrackingCode authority remain governed.

## DB-010 — Runtime database principal grants and backup metadata contract remain unverified

- **Severity / domain / owner:** MEDIUM / Security / operations / OPERATIONAL_DEPLOYMENT
- **Affected tables:** configured PostgreSQL login, roles/grants; backup manifest
- **Affected entity/configuration:** Infrastructure configuration; BackupManifest
- **Affected migration:** n/a
- **Affected query/handler:** See affected entity/configuration above.
- **Current behavior / exact gap:** Audit used only isolated cluster bootstrap superuser. Source captures server version, dump version, app/schema version, checksum/size/time in backup manifest; no deployed login/grant catalog or actual secured connection was available.
- **Expected invariant / business consequence:** Cannot determine whether application login can DDL/alter/delete, whether secrets are least privilege, or installed backup is restore-certified.
- **Concurrency consequence:** Privilege risk depends on actual deployment role.
- **Data-repair consequence:** If runtime credentials are overprivileged, DB constraints can be bypassed wholesale.
- **Classification:** Evidence limitation; no defect assertion against unknown deployment.
- **Can schema represent correct state?** Yes for supported source paths; the gap concerns states permitted by direct SQL or noted model mismatch.
- **Migration likely required?** Not proven / query-only / operational evidence required.
- **Recommended direction / tests / PostgreSQL proof:** MEDIUM Owner OPERATIONAL_DEPLOYMENT; inspect deployed roles read-only in approved environment; no operational data accessed here.
- **Out-of-scope dependencies:** Phase 8 BusinessDate/time zone; Phase 9 backup/printing operational reliability; Phase12 replay uniformity; Pass5 Missing status and protected TrackingCode authority remain governed.

## DB-011 — Sequence rollback can reuse a row-backed document number

- **Severity / domain / owner:** INFO / Sequence / document authority / PHASE7_PASS5
- **Affected tables:** system.document_sequences
- **Affected entity/configuration:** PostgresDocumentNumberService.NextAsync
- **Affected migration:** InitialProductionBaseline
- **Affected query/handler:** See affected entity/configuration above.
- **Current behavior / exact gap:** INSERT/ON CONFLICT increment is in current transaction; documented provider behavior means rollback undoes counter change. Existing design says document numbers may have gaps and reuse is forbidden. No persisted completed document gets the rolled-back number, and DB unique document constraints protect actual collisions.
- **Expected invariant / business consequence:** A number generated before transaction rollback can be generated again on retry. Whether external visibility occurs before commit was not demonstrated.
- **Concurrency consequence:** Concurrent allocations serialize on the counter row; calls without ambient transaction commit their own allocation.
- **Data-repair consequence:** Potential leaked/uncommitted number ambiguity only if exposed externally.
- **Classification:** INFO pending targeted authority trace; do not inflate severity.
- **Can schema represent correct state?** Yes for supported source paths; the gap concerns states permitted by direct SQL or noted model mismatch.
- **Migration likely required?** Not proven / query-only / operational evidence required.
- **Recommended direction / tests / PostgreSQL proof:** INFO Verify callers never publish a number before durable commit; no change authorized.
- **Out-of-scope dependencies:** Phase 8 BusinessDate/time zone; Phase 9 backup/printing operational reliability; Phase12 replay uniformity; Pass5 Missing status and protected TrackingCode authority remain governed.

## DB-013 — Purchase-return cash and purchase-void compensation remain Pass5 governance contradictions

- **Severity / domain / owner:** MEDIUM / Cash / supplier settlement / PHASE7_PASS5
- **Affected tables:** purchasing.returns; finance.cash_movements; finance.supplier_refunds; purchasing.purchase_voids
- **Affected entity/configuration:** PurchaseReturnHandler; VoidPurchaseHandler
- **Affected migration:** Pass5 current source / frozen authority
- **Affected query/handler:** See affected entity/configuration above.
- **Current behavior / exact gap:** Frozen Pass5 governance resolution C-P5-ARCH-01/02 authorizes atomic PurchaseReturn plus actual SupplierRefund for cash returned; voiding purchase must not infer actual cash return. Current source/test state is recorded in Pass5 contradiction/remediation evidence.
- **Expected invariant / business consequence:** Cash drawer and supplier credit can misstate real settlement if UX semantics remain unimplemented/aligned.
- **Concurrency consequence:** A transaction can be internally atomic while recording the wrong commercial fact; DB rows cannot infer whether cash physically moved.
- **Data-repair consequence:** May need correction/reversal and owner reconciliation.
- **Classification:** Not solely database defect; authority finding.
- **Can schema represent correct state?** Yes for supported source paths; the gap concerns states permitted by direct SQL or noted model mismatch.
- **Migration likely required?** Not proven / query-only / operational evidence required.
- **Recommended direction / tests / PostgreSQL proof:** MEDIUM Owner PHASE7_PASS5. Existing Missing-status gate remains independent.
- **Out-of-scope dependencies:** Phase 8 BusinessDate/time zone; Phase 9 backup/printing operational reliability; Phase12 replay uniformity; Pass5 Missing status and protected TrackingCode authority remain governed.

## Totals

Critical 0; High 4; Medium 4; Low 2; Info 1.

The shared workspace was dirty at audit start. Fifteen of 866 captured source/document/test hashes changed before final check; authorship is unknown and changes were preserved. The audit issued no source/test/migration writes.

## Independent false-positive review

- Confirmed EF model/physical column, type, nullability, constraint and index comparison; the only extra physical indexes are two intentional PostgreSQL partial defaults. The open-stocktake index semantic difference is retained as low severity because the database is stricter and this matches the one-open-stocktake workflow.
- Confirmed direct SQL mutation/deletion probes ran on disposable fixture rows and each transaction rolled back; not inferred from lack of trigger alone.
- Did not report one stock/movement difference as product corruption: a test explicitly seeded StockBalance without a business movement to test rejection behavior.
- Did not call nullable InventoryUnit identity a defect solely because it is nullable; the direct-SQL probe demonstrated an identity tuple can be cleared, while legitimate warranty/historical status/origin combinations still require source authority review.
- Did not report status 999 as a normal application defect; it is a direct SQL schema-hardening gap. Domain accounting rejects unknown status.
- Duplicate low/info entries were excluded when their scope was already covered by a substantive finding.
- No migration recommendation changes frozen architecture, TrackingCode, supplier identity sequence, or Pass5 missing-unit decision.
- Grades are evidence-limited; no production rows, role permissions, or workload cardinalities were inspected.
