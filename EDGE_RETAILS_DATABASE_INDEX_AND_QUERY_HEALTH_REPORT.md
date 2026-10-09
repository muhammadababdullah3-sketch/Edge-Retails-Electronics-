# Index and Query Health Report

## Catalog and index inventory

- PostgreSQL 18.6 clean replay: 398 indexes, 265 unique; all catalog indexes were valid at inspection.
- Five FK relationships have no unconditional leading child-key index: inventory stocktake-unit-check -> item; sales POS draft item -> selected inventory unit; three warranty claim-item-unit optional unit references. Their access risk depends on parent deletes/updates and table growth.
- Product name `ILIKE %term%` predicates use leading wildcard and no matching trigram extension/index was installed. For broad search at growth, this can scan products; no plan at representative production cardinality was available.
- Inventory movement, purchase and sales history have descending/time indexes; hot document lists are generally keyset/page bounded to 500.
- Physical identity unique indexes support TrackingCode and normalized claims. Supplier/account history has supplier plus occurrence ordering index.

## Query behavior

A real PostgreSQL call to `InventoryOverviewReadService.GetStockPageAsync` with `BeforeName` and `BeforeProductId` was executed inside a rollback transaction against three temporary product rows. EF Core threw `InvalidOperationException` while translating `string.Compare(x.product.Name, query.BeforeName, StringComparison.Ordinal)`. This is a reproducible medium user-visible failure on the second inventory page; existing read API pagination tests cover product management, not this inventory cursor. Owner: CURRENT_DATABASE_SCHEMA / Phase7Pass5 if in scope; implementation is out of scope here.

No `EXPLAIN ANALYZE` was used. The read-only rehearsal captured `EXPLAIN (FORMAT JSON)` for top-N movements, purchases, sales, TrackingCode lookup, normalized manufacturer identity, supplier ledger, wildcard product search, cash movements, warranty claims and Thaka project issues. Plans use freshly migrated, low-volume synthetic fixture tables; sequential scans there are not evidence of production degradation.

## Full FK index check

- `inventory.stocktake_unit_checks` -> `inventory.stocktake_items` (stocktake_item_id)
- `sales.pos_draft_items` -> `inventory.units` (selected_inventory_unit_id)
- `warranty.claim_item_units` -> `inventory.units` (active_original_inventory_unit_id)
- `warranty.claim_item_units` -> `inventory.units` (original_inventory_unit_id)
- `warranty.claim_item_units` -> `inventory.units` (replacement_inventory_unit_id)

## Resource configuration

- EF/Npgsql command timeout defaults to 180 seconds and accepts a bounded environment override from 1 to 3600 seconds. Connection string leaves Npgsql pooling at provider defaults; no explicit pool size, connection timeout, keepalive, multiplexing, or retry configuration was found in infrastructure registration.
- Dapper paginated methods clamp page size and pass cancellation tokens; a general explicit command timeout was not present in the inspected read service calls. Long-running query resource behavior is not live stress-tested.
- Application lock waits and PostgreSQL statements can therefore outlive ordinary UI response targets unless cancellation/timeout is supplied by callers; report as LOW/INFO until a realistic plan/load proof exists.
