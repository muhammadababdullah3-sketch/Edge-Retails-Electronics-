# Phase 3A.2 InventoryUnit Preservation Closure — 2026-09-29

**InventoryUnit Relationship Preservation: PASS**

**Classification: EMPTY PRODUCTION DATASET / STRUCTURAL PRESERVATION PROVEN**

Scope: section 3A.2 of the new governing attachment only. No production data was created. Production access consisted of explicit BEGIN READ ONLY / COMMIT SQL. No production backups, mutations, service changes, source changes, migration changes, or later-phase work occurred.

## Results

| Dataset | PostgreSQL | inventory.units exists | Rows | Valid FKs | Orphans |
|---|---|---|---:|---:|---:|
| A — verified pre-migration archive restored into inventory_pre | 18.6 | Yes | 0 | 18/18 | 0 across 18 checks |
| B — current edge_retails_prod, 127.0.0.1:5432 | 18.6 | Yes | 0 | 18/18 | 0 across 18 checks |
| C — verified current post-migration archive restored into inventory_post | 18.6 | Yes | 0 | 18/18 | 0 across 18 checks |

All 18 constraint records matched exactly across A/B/C, including constraint names, definitions, validity, source/referenced tables and ordered columns. All definitions use ON DELETE RESTRICT. The critical stop condition (pre-migration inventory > 0 with current inventory = 0) was evaluated and was false. Because all three counts are zero, no production InventoryUnit relationships existed at the data level in these snapshots. This is completed structural preservation verification, not skipped or assumed representative-data verification.

## Complete FK structure

Each row below was identical in all three databases and valid in each. Every referenced column is id. Nullable source keys are excluded from orphan checks according to FK semantics; every nonnull key was checked against its referenced row.

| Constraint | Source table/column | Referenced table/column |
|---|---|---|
| fk_claim_item_units_units_active_original_inventory_unit_id | warranty.claim_item_units.active_original_inventory_unit_id | inventory.units.id |
| fk_claim_item_units_units_original_inventory_unit_id | warranty.claim_item_units.original_inventory_unit_id | inventory.units.id |
| fk_claim_item_units_units_replacement_inventory_unit_id | warranty.claim_item_units.replacement_inventory_unit_id | inventory.units.id |
| fk_material_issue_units_units_inventory_unit_id | thaka.material_issue_units.inventory_unit_id | inventory.units.id |
| fk_movement_units_units_inventory_unit_id | inventory.movement_units.inventory_unit_id | inventory.units.id |
| fk_pos_draft_items_units_selected_inventory_unit_id | sales.pos_draft_items.selected_inventory_unit_id | inventory.units.id |
| fk_purchase_item_units_units_inventory_unit_id | purchasing.purchase_item_units.inventory_unit_id | inventory.units.id |
| fk_return_item_units_units_inventory_unit_id | purchasing.return_item_units.inventory_unit_id | inventory.units.id |
| fk_return_item_units_units_inventory_unit_id | sales.return_item_units.inventory_unit_id | inventory.units.id |
| fk_sale_item_units_units_inventory_unit_id | sales.sale_item_units.inventory_unit_id | inventory.units.id |
| fk_stocktake_unit_checks_units_inventory_unit_id | inventory.stocktake_unit_checks.inventory_unit_id | inventory.units.id |
| fk_units_claim_items_source_warranty_claim_item_id | inventory.units.source_warranty_claim_item_id | warranty.claim_items.id |
| fk_units_lots_inventory_lot_id | inventory.units.inventory_lot_id | inventory.lots.id |
| fk_units_products_product_id | inventory.units.product_id | catalog.products.id |
| fk_units_purchase_items_source_purchase_item_id | inventory.units.source_purchase_item_id | purchasing.purchase_items.id |
| fk_units_shop_stock_cases_source_warranty_case_id | inventory.units.source_warranty_case_id | warranty.shop_stock_cases.id |
| fk_units_stock_adjustment_items_source_stock_adjustment_item_id | inventory.units.source_stock_adjustment_item_id | inventory.stock_adjustment_items.id |
| fk_units_supplier_products_supplier_product_id | inventory.units.supplier_product_id | catalog.supplier_products.id |

## EF alignment

The current EdgeRetailsDbContextModelSnapshot.cs was read directly, with bounded regex inspection of named relationships (no recursive object reflection). All 18 database FK names have matching DeleteBehavior.Restrict model declarations. InventoryUnit has seven outgoing relationships; eleven incoming relationships include two separate same-named return-item constraints in purchasing and sales. The existing certification records the successful Release EF has-pending-model-changes command (exit 0, no model changes) and connected migration inventory. Those existing EF results are reused, not claimed as freshly rerun. Snapshot FK-name/restrict matching command completed exit 0.

Provider for this new database/restore evidence: **PostgreSQL 18.6 / psql, pg_restore and libpq**. Application/model provider for reused EF evidence: **PostgreSQL 18 / Npgsql**, EF Core 10.0.12 and Npgsql EF provider 10.0.3.

## Archive provenance

- A: pre_migration_20260929_163913.dump, SHA-256 675D0248EF1583C73637AD87A558245F2168B9E3B6C5B9488FC91C65996C907D.
- C: post_phase3a_migration_20260929_20260929_184442.dump, SHA-256 FF2682D091C324EE41F241E4315136B30F4DA6C66E0EC39EFF66864C9834A606.
- Both archives reside under C:\Users\muham\AppData\Local\EdgeRetails\Production\backups. Both hashes matched, pg_restore --list exited 0, and pg_restore --file NUL exited 0.

## Execution and isolation

Exact bounded harness: docs/Phase3A_Inventory_Preservation_Evidence.ps1. Invoked using:

    powershell -NoProfile -ExecutionPolicy Bypass -File docs/Phase3A_Inventory_Preservation_Evidence.ps1

Terminal exit: **0**; result: INVENTORY STRUCTURE PASS: A=0 B=0 C=0; identical valid FKs; zero orphans.

NEW_COVERAGE: the added docs-only harness adds bounded database-preservation evidence. It does not replace, weaken, skip, or change an existing assertion or concurrency guard. It uses the canonical operational restore isolation safeguards: verified archive hashes; a unique owned temporary cluster; fresh random disposable credentials; loopback-only nonproduction port; checked server data_directory and PG18 identity before restore; no-owner/no-privileges single-transaction restore; and guarded finally cleanup. Approved production authority was parsed in memory from ProgramData configuration without printing credentials.

Exact commands and individual exit codes are retained in Phase3A_Inventory_Preservation_Commands_2026-09-29.txt. Raw sanitized query results for all three databases are retained in Phase3A_Inventory_Preservation_Raw_2026-09-29.json. Full SQL is in the harness. Each of two createdb and pg_restore commands exited 0; all three verification psql commands exited 0.

The initial attempt stopped at disposable identity-result parsing because a one-row PowerShell return was unwrapped to a scalar. No archive restore or production access occurred in that attempt. Its own cluster was stopped and removed, recorded in Phase3A_Inventory_Preservation_InitialAttempt_2026-09-29.txt. The array return and compact deterministic JSON aggregate were corrected; the final complete execution passed. This was an evidence harness parsing correction, not a database defect.

## Terminal cleanup

Final owned root: C:\Users\muham\AppData\Local\Temp\EdgeRetailsInventoryClosure_58a4b0f8260e42f9b4a81fbab199ce08. Port: 56941, bound only to 127.0.0.1. Exact owned path and non-reparse-point status were checked before recursive removal.

- pg_ctl -m fast -w stop: exit 0.
- pg_ctl status: exit 3 (stopped).
- Listener on 56941: absent.
- Exact owned temporary root after guarded removal: absent.

No cleanup action targeted operational PostgreSQL. This closes only the 3A.2 inventory preservation evidence gate; overall Phase 3A certification remains the parent review responsibility.
