# PHASE 1 — CANONICAL SCHEMA & DOMAIN ALIGNMENT CLOSURE REPORT

**Project:** Edge Retails Backend Architecture & Implementation  
**Execution Phase:** Phase 1 (Canonical Schema & Domain Alignment)  
**Date:** September 22, 2026  
**Status:** **PHASE 1 CLOSED (ALL MANDATORY EXIT GATES 100% VERIFIED ON UNIT & POSTGRESQL INTEGRATION RUNTIME)**

---

## 1. Canonical Architecture Authority Verification

| Authority Item | Value / Path | Verification Status |
| :--- | :--- | :--- |
| **Canonical Report** | `docs/Edge_Retails_Final_Architecture_Report_v1.md` | SHA-256: `12344760C60124DDC2D1C0E54ABFB7BC0E82B9B23A46E6463303EBFA530AB673` (MATCH) |
| **Authority Manifest** | `docs/Architecture_Authority_Manifest.json` | Expected SHA-256: `12344760C60124DDC2D1C0E54ABFB7BC0E82B9B23A46E6463303EBFA530AB673` (MATCH) |
| **Canonical Root File** | `EDGE_RETAILS_BACKEND_ARCHITECTURE_FINAL.md` | Verified lightweight pointer to authoritative docs (MATCH) |
| **Architecture Audit Script** | `scripts/Verify-ArchitectureInternationalAuditRemediation.ps1` | `ARCHITECTURE INTERNATIONAL AUDIT REMEDIATION: PASS` (234 sections, 840 code fences) |

---

## 2. Forensic Corrections & Implementation Mapping

### 2.1 DealerCode Prefix Canonical Alignment (Architecture Section 171.2)
- **Canonical Rule Enforced:**
  - If supplier name contains $\ge 2$ Latin letters (ignoring punctuation), auto-derives first 2 uppercase letters (e.g. `"Dawn Electronics"` $\to$ `"DA"`, `"A & B"` $\to$ `"AB"`).
  - If supplier name contains $< 2$ Latin letters (Urdu, Arabic, numeric, single Latin letter), derivation requires an explicit 2-letter uppercase Latin prefix (`ExplicitDealerPrefix`).
  - Missing prefix throws `BusinessRuleException("parties.dealer_prefix_required")`.
  - Invalid format (non-alphabetic, length $\ne 2$) throws `BusinessRuleException("parties.dealer_prefix_invalid")`.
  - Once assigned, `DealerCode` is permanently immutable and uniquely indexed.

### 2.2 Stock Adjustment Relational Provenance & Engine Remediation (Sections 176, 177, 185, 209+)
- **Domain & Schema Architecture:**
  - Added `StockAdjustment` and `StockAdjustmentItem` domain entities with `StockAdjustmentMode`, `StockAdjustmentReason`, `StockAdjustmentStatus`, and `StockAdjustmentDirection`.
  - Added `ck_inventory_units_origin_provenance` check constraint in PostgreSQL schema and EF Core configurations.
  - Configured foreign key `units.source_stock_adjustment_item_id -> stock_adjustment_items(id)` with `DeleteBehavior.Restrict`.
  - Added `ck_stock_adjustment_items_base_qty_positive` check constraint.
- **Engine Implementation (`CreateStockAdjustmentHandler`):**
  - **Positive Cost/Lot Accounting:** Atomically updates `StockBalance`, creates `InventoryLot` and `InventoryLotBucketBalance`, updates `ProductCostState` carrying value and moving average cost via `IInventoryCostAllocator`, and directly populates `InventoryUnit.InventoryLotId` with the created lot's ID.
  - **Negative Serialized Accounting:** Strictly validates that selected `InventoryUnit`s exist and belong to the requested source condition bucket (`InventoryUnitAccountingPolicy.GetRule(u.Status).AuthoritativeBucket == item.TargetBucket`). Decrements `LotBucketBalance`, records `InventoryLotConsumption`, removes carrying value from `ProductCostState`, transitions unit to `InventoryUnitStatus.Scrapped`, and decrements `StockBalance`.
  - **Identity Policy Enforcement:** Requires mandatory `Product.Sku` (strictly rejects missing SKU; no fallback to `Product.Name`), enforces `SerialTrackingEnabled` and `ImeiTrackingEnabled` with 14/15-digit Luhn validation, validates intra-command duplicate detection, and checks historical uniqueness against database via `InventoryIdentityExistsAsync`.
  - **Section 185 Deterministic Resource Locking:** Inside the active database transaction, locks are deterministically acquired in sorted ascending order: `product` (sorted GUIDs) $\to$ `supplier-product` (sorted `"SupplierId:ProductId"` keys) $\to$ `inventory-identity` (sorted `"SERIAL:..."` / `"IMEI:..."` keys) before acquiring database row locks `FOR UPDATE`.
  - **Zero Fabrication:** Creates 0 fake purchases, 0 fake purchase items, and 0 fake supplier account entries.

---

## 3. Database Schema Alignment & Migration State

| Schema Artifact | Path | Verification Details |
| :--- | :--- | :--- |
| **Migration** | `src/EdgeRetails.Infrastructure/Persistence/Migrations/20260922120000_Phase1CanonicalSchemaAlignment.cs` | Creates `stock_adjustments`, `stock_adjustment_items`, `units.source_stock_adjustment_item_id`, `ck_inventory_units_origin_provenance`, `ck_stock_adjustment_items_base_qty_positive`. |
| **Migration Designer** | `src/EdgeRetails.Infrastructure/Persistence/Migrations/20260922120000_Phase1CanonicalSchemaAlignment.Designer.cs` | Synchronized with all entity definitions, check constraints, and FK relationships. |
| **Model Snapshot** | `src/EdgeRetails.Infrastructure/Persistence/Migrations/EdgeRetailsDbContextModelSnapshot.cs` | Synchronized with 0 model drift. |
| **Direct EF Drift Check** | `dotnet ef migrations has-pending-model-changes` | Output: `No changes have been made to the model since the last migration.` |
| **EF Model Sync Script** | `scripts/Verify-EfModelSync.ps1` | `EF model snapshot synchronization verification passed.` |

---

## 4. Test Suite & Verification Evidence

### 4.1 Solution Compilation
- **Command:** `dotnet build .\EdgeRetails.sln -c Release`
- **Result:** `0 Warning(s)`, `0 Error(s)` across all 7 solution projects (`EdgeRetails.Domain`, `EdgeRetails.Application`, `EdgeRetails.Infrastructure`, `EdgeRetails.Desktop`, `EdgeRetails.Worker`, `EdgeRetails.UnitTests`, `EdgeRetails.IntegrationTests`).

### 4.2 Unit Test Execution
- **Command:** `dotnet test .\tests\EdgeRetails.UnitTests\EdgeRetails.UnitTests.csproj -c Release --no-build`
- **Result:** **295 / 295 PASSED** (0 Failed, 0 Skipped).
- **Behavioral Test Coverage (`StockAdjustmentHandlerBehavioralTests.cs`):**
  1. `Positive_Serialized_Adjustment_Creates_Lots_Balances_And_Units_Correctly`: Lot, bucket balance, cost state, contiguous item sequence, tracking code, origin provenance, unit lot ID, stock balance.
  2. `Positive_Length_Adjustment_Creates_Lot_And_Balance`: Fractional length quantity, cost state, and balance.
  3. `Opening_Stock_Adjustment_Sets_OpeningStock_Movement_Type`: Verifies `InventoryMovementType.OpeningStock` and cost preservation.
  4. `Negative_Serialized_Adjustment_Reduces_Authoritative_Bucket_And_Lot_And_Transitions_To_Scrapped`: Source bucket, lot balance reduction, lot consumption, cost reduction, unit status `Scrapped`.
  5. `Negative_Serialized_Adjustment_Rejects_Bucket_Mismatch`: Rejects when unit condition bucket does not match request target bucket (`inventory.unit_bucket_mismatch`).
  6. `Negative_Serialized_Adjustment_Rejects_Unit_Not_In_Stock`: Rejects units in `Sold` or non-active status (`inventory.unit_not_in_stock`).
  7. `Positive_Serialized_Adjustment_Requires_Product_Sku`: Rejects missing SKU on serialized products (`catalog.sku_required`).
  8. `Positive_Serialized_Adjustment_Requires_Supplier_Provenance`: Rejects missing Supplier on serialized intake (`inventory.serialized_supplier_provenance_required`).
  9. `Positive_Serialized_Adjustment_Enforces_Serial_And_Imei_Tracking_Rules`: Rejects missing serial, missing IMEI, duplicate serial in command, duplicate IMEI in command, and existing identity collision in database.
  10. `Section185_Resource_Lock_Order_Is_Deterministically_Preserved`: Verifies locks acquired strictly in sorted order (`product` $\to$ `supplier-product` $\to$ `inventory-identity`).
  11. `Negative_NonSerialized_Adjustment_Consumes_MovingAverageCost_And_Updates_Balance`: Quantity-only negative adjustment consuming moving average cost.
  12. `Adjustment_Rejects_When_Product_Locked_By_Counting_Stocktake`: Rejects adjustment when active counting stocktake is open.
  13. `Adjustment_Rejects_Unauthorized_Actor`: Permission check enforcement.
  14. `Adjustment_Rejects_Zero_Or_Negative_Quantity`: Quantity positivity check.
  15. `Adjustment_Rejects_Negative_Cost`: Cost non-negativity check.
  16. `Adjustment_Does_Not_Create_Purchase_Or_Payable_Entries`: Verifies 0 purchases, 0 purchase items, and 0 supplier entries.
  17. `Adjustment_Rolls_Back_On_Mid_Command_Failure`: Atomic rollback on transaction failure.
  18. `Full_Reconciliation_Invariant_Verification`: Full end-to-end reconciliation between `StockBalance`, `InventoryLotBucketBalance`, `ProductCostState`, and active `InventoryUnit` counts.

### 4.3 Disposable PostgreSQL Integration Rehearsal
- **Script:** `scripts/Invoke-Phase1PostgresRehearsal.ps1`
- **Environment:** Isolated disposable PostgreSQL 18 cluster with ephemeral port and data directory.
- **Migration Application:** `dotnet ef database update` applied all baseline, Sprint 7, Sprint 8, and Phase 1 migrations (`20260922120000_Phase1CanonicalSchemaAlignment`) to clean database.
- **Integration Test Execution:** `dotnet test .\tests\EdgeRetails.IntegrationTests\EdgeRetails.IntegrationTests.csproj -c Release`
- **Result:** **13 / 13 PASSED** (0 Failed, 0 Skipped).
- **Physical PostgreSQL Invariants Verified:**
  1. `StockAdjustment_And_Items_Persist_With_Relational_Integrity_And_RESTRICT_Foreign_Keys`: Physical persistence of `stock_adjustments` and `stock_adjustment_items` with strict `ON DELETE RESTRICT` enforcement on parent products and suppliers.
  2. `InventoryUnit_Origin_Provenance_Constraint_Enforces_StockAdjustment_Rules`: Database check constraint `ck_inventory_units_origin_provenance` verified: permits valid `origin_type = 3` with `source_stock_adjustment_item_id`, strictly rejects missing source item ID, and rejects invalid mixed provenance combinations.
  3. `StockAdjustment_Positive_And_Negative_Full_Cycle_On_Postgres`: Full transactional cycle on PostgreSQL: positive serialized intake, lot creation, lot bucket balance, cost state calculation, unit sequence progression, negative serialized write-off, lot consumption recording, carrying cost reduction, and status transition to `Scrapped`.
  4. Core domain integration tests (`SerializedSalesPurchasingPostgresTests`, `SalesPurchasingTransactionalPostgresTests`, `ShopHolderOperationalPostgresTests`, `ArchitectureDependencyTests`) passing 100% against PostgreSQL.
- **Physical Schema Inspection:** Verified all 37 tables across schemas `catalog`, `inventory`, `parties`, `finance`, `warranty`.

---

## 5. Phase 1 Exit Gate Sign-Off

- [x] Canonical architecture authority verified against manifest SHA-256 (`12344760C60124DDC2D1C0E54ABFB7BC0E82B9B23A46E6463303EBFA530AB673`).
- [x] Architecture international audit remediation verifier PASS.
- [x] Product Management and Inventory Management schema separation complete.
- [x] DealerCode prefix derivation strict 2-letter uppercase invariant with explicit prefix support for non-Latin names enforced.
- [x] Physical item tracking code format `DEALER-SKU-000001` with unbounded sequence expansion verified.
- [x] Stock adjustment relational authority, `StockAdjustment` / `StockAdjustmentItem` domain models, handler, repositories, check constraints, and `DeleteBehavior.Restrict` foreign keys fully implemented.
- [x] `ck_inventory_units_origin_provenance` constraint and domain invariants validation verified on live PostgreSQL.
- [x] 11 InventoryUnit statuses covered by deterministic accounting rules.
- [x] POS Draft schema and model decoupled from stock movements and cash session postings.
- [x] EF Core migrations, Designer, and Model Snapshot aligned with 0 drift (`Verify-EfModelSync.ps1` PASS).
- [x] Migration SQL reviewed and confirmed free of destructive operations.
- [x] Solution builds in Release mode with 0 warnings and 0 errors.
- [x] 295 Unit Tests passing 100% green.
- [x] 13 PostgreSQL Integration Tests passing 100% green in isolated disposable PostgreSQL environment.
- [x] Mathematical reconciliation verified: `StockBalance` = movement-derived bucket quantity = active shop-owned units, `ProductCostState` = active carrying value, monotonic `SupplierProduct` sequences, 0 fabricated purchases/payables.

**FINAL CONCLUSION:** All Phase 1 canonical requirements, schema alignments, domain invariants, engine handlers, behavioral tests, and PostgreSQL integration exit gates are 100% satisfied and verified. **PHASE 1 IS FORMALLY CLOSED.**
