# Edge Retails — Shop-Holder Critical Implementation Plan
## Multi-Unit, Warranty, Stocktake, Non-Sellable, Cash Closing, Quotations

Status: APPROVED IMPLEMENTATION PLAN
Architecture Source: Edge_Retails_Final_Architecture_Report_v1.md Sections 103–140
Database Status: Not yet physically implemented

---

# 1. Goal

Implement the critical and strongly recommended shop-holder features without breaking the existing Sales, Purchasing, Inventory, Thaka, Reporting, Security, Backup, or future LAN architecture.

Dependency order:

    Units / Conversions
        ↓
    Inventory Foundation
        ↓
    Non-Sellable + Warranty
        ↓
    Stocktake
        ↓
    Purchasing / Sales / Thaka integration
        ↓
    Cash Sessions
        ↓
    Quotations
        ↓
    Reports / WPF integration

---

# 2. Scope Locked

Critical:
- Multi-Unit / Packaging Conversion
- Customer Warranty Claims
- Shop-Owned Supplier Warranty
- Batch Physical Stocktake
- Damaged / Defective operational workflow

Strong V1 additions:
- Cash Session / Daily Closing
- Quotation / Estimate

Explicitly deferred:
- Normal Customer Udhaar ledger
- Supplier Accounts Payable ledger
- Full accounting / general ledger

---

# 3. Stage 0 — Foundation Freeze

Before Migration 001:
1. Replace Product single Unit ownership with BaseUnitId.
2. Lock BaseQuantity precision at numeric(18,6).
3. Lock conversion factor at numeric(18,9).
4. Add WITH_SUPPLIER inventory bucket/status.
5. Lock Warranty schema.
6. Lock Stocktake tables/state machine.
7. Lock Cash Session tables.
8. Lock Quotation tables.
9. Update permission definitions.
10. Update integration-test database fixtures.

Exit gate:
No unresolved schema question remains for Final Architecture Sections 103–140.

---

# 4. Stage 1 — Unit and Conversion Foundation

Domain concepts:
- Unit
- ProductUnit
- UnitConversionFactor
- BaseQuantity
- EnteredQuantity

Product owns BaseUnitId.

ProductUnit owns:
- UnitId
- FactorToBaseUnit
- CanPurchase
- CanSell
- CanUseInThaka
- Default Purchase flag
- Default Sale flag
- Active state

Domain validation:
- Factor > 0
- Base factor = 1
- One default Purchase Unit
- One default Sale Unit
- Serialized converted Base Quantity must be whole

Database:
- catalog.units
- catalog.product_units
- catalog.product_unit_barcodes

Constraints/indexes:
- unique(product_id, unit_id)
- unique active barcode
- factor_to_base_unit > 0
- one active default Purchase Unit per Product
- one active default Sale Unit per Product

Acceptance:
- 10 Boxes × 12 = 120 Pieces
- 1 Foot = 0.3048 Meter
- Historical factor survives conversion edit
- Unit barcode resolves exact ProductUnit

---

# 5. Stage 2 — Conversion-Aware Inventory and Costing

All InventoryMovement and StockBalance quantities use BaseQuantity.

Purchase, Sale, Return, Thaka, Stock Adjustment and Stocktake lines snapshot:
- SelectedUnitId
- EnteredQuantity
- FactorToBaseSnapshot
- BaseQuantity

Costing operates per Base Unit.

Scenario:
Purchase 10 Boxes, 1 Box = 12 Pieces, Box Cost = Rs.1,200.

Expected:
- Base Qty = 120 Pieces
- Base Unit Cost = Rs.100

Then sell 5 Pieces:
- Remaining = 115 Pieces

Then sell 1 Box:
- Remaining = 103 Pieces

No rounding drift allowed.

---

# 6. Stage 3 — Non-Sellable Inventory Lifecycle

StockBalance:
- SellableQty
- DamagedQty
- DefectiveQty
- WithSupplierQty
- ScrapQty

Movement types:
- MARK_DAMAGED
- MARK_DEFECTIVE
- RESTORE_TO_SELLABLE
- SEND_TO_SUPPLIER_WARRANTY
- RECEIVE_REPAIRED
- RECEIVE_REPLACEMENT
- WARRANTY_REJECTED
- WRITE_OFF_TO_SCRAP

Commands:
- TransferInventoryCondition
- RestoreToSellable
- SendToSupplierWarranty
- ReceiveSupplierWarrantyItem
- WriteOffInventory

Every command:
1. Locks current inventory state.
2. Validates source bucket/status.
3. Creates movement/effects.
4. Updates StockBalance.
5. Updates cost state when required.
6. Writes audit.
7. Commits atomically.

Exit gate:
No code path directly mutates a non-sellable bucket.

---

# 7. Stage 4 — Warranty Module

Namespaces:
- Domain/Warranty
- Application/Features/Warranty
- Infrastructure/Persistence/Configurations/Warranty
- Infrastructure/Queries/Warranty

Database:
- warranty.claims
- warranty.claim_items
- warranty.claim_item_units
- warranty.claim_events

Customer claim rule:
Customer owns item, so no StockBalance movement. Only custody/status is tracked.

Shop warranty rule:
Shop owns item, so inventory moves to WITH_SUPPLIER and carrying cost remains.

Commands:
- CreateWarrantyClaim
- SendWarrantyClaimToSupplier
- RecordWarrantyResolution
- HandoverWarrantyItem
- SendShopStockToSupplierWarranty
- ReceiveSupplierWarrantyItem
- WriteOffWarrantyItem

Queries:
- GetWarrantyClaims
- GetWarrantyClaimDetail
- GetCustomerWarrantyHistory
- GetSupplierWarrantyInventory

Acceptance:
- Customer fan received for warranty never increases stock.
- Shop-owned fan sent to supplier enters WithSupplier.
- Replacement customer item is traceable without entering Sellable stock.
- Serialized warranty preserves exact unit identity.

---

# 8. Stage 5 — Batch Stocktake

Database:
- inventory.stocktakes
- inventory.stocktake_items
- inventory.stocktake_unit_checks

State:
DRAFT → COUNTING → REVIEW → POSTED
or CANCELLED before post.

During COUNTING:
Stock-affecting operations in scope are rejected.

Posting:
Never updates StockBalance directly. It creates controlled Physical Count Correction movements.

Serialized count:
Expected Unit IDs versus physically scanned Unit IDs.

Acceptance:
- Variance is reproducible.
- Post is idempotent.
- Double-post is impossible.
- Concurrent Sale in locked scope is rejected.
- Unexpected serialized item cannot silently appear.

---

# 9. Stage 6 — Purchasing Integration

Purchase line adds:
- Product
- Purchase Unit
- Entered Qty
- Base Qty Preview
- Cost per selected unit
- Effective base cost
- Sale Price

Serialized receiving:
- Base Qty must be whole.
- Captured identities count must equal Base Qty.

Purchase Return uses original factor snapshot and purchase-origin Base Quantity.

Shop-stock supplier warranty links to purchase origin where available.

No Supplier Payable ledger is added.

---

# 10. Stage 7 — Sales and Return Integration

POS barcode resolution returns ProductUnit.

Cart can display:
- LED Bulb × 1 Box
- LED Bulb × 3 Pieces
- Cable × 15 Meters

Complete Sale converts every line to BaseQuantity before stock validation.

Sale Return uses original SaleItem factor snapshot.

Discount allocation remains monetary and independent of conversion.

Serialized Sale still selects exact physical units.

---

# 11. Stage 8 — Thaka Integration

Add Material supports allowed ProductUnits.

Examples:
- Cable × 15 Meter
- LED Bulb × 2 Boxes

BaseQuantity is used for stock issue and costing.

Thaka reversal uses original factor snapshot.

Serialized Thaka issue still selects exact units.

---

# 12. Stage 9 — Cash Session / Daily Closing

Database:
- finance.cash_sessions
- finance.cash_movements

Hooks:
- Cash Sale → SALE_CASH_IN
- Cash Sale Return → SALE_REFUND_CASH_OUT
- Cash Thaka Payment → THAKA_PAYMENT_CASH_IN
- Cash Expense → EXPENSE_CASH_OUT

Manual Cash movement requires reason and audit.

Commands:
- OpenCashSession
- RecordManualCashMovement
- CloseCashSession

Close formula:
Opening + Cash In - Cash Out = Expected
Counted - Expected = Difference

Daily report reads session summary but never treats cash count as profit.

---

# 13. Stage 10 — Quotation / Estimate

Database:
- sales.quotations
- sales.quotation_items

Quotation has zero inventory and financial effect.

Commands:
- CreateQuotation
- UpdateQuotation
- IssueQuotation
- ConvertQuotationToSale
- CancelQuotation

Conversion:
1. Load Quotation from DB.
2. Verify valid status/date.
3. Use stored server-side quoted prices.
4. Revalidate product/unit/stock.
5. Require serialized selection where relevant.
6. Complete normal Sale transaction.
7. Mark Quotation converted.

A converted Quotation cannot convert twice.

---

# 14. Stage 11 — Frontend Integration

Keep 17 full screens.

Inventory screen gains:
- Warranty
- Non-Sellable
- Stocktake

POS gains:
- Unit selector when multiple units exist
- Quotation action
- Load/Convert Quotation

Product editor gains:
- Base Unit
- Allowed Purchase Units
- Allowed Sale Units
- Conversion factors
- Unit barcodes

Dashboard/Reports gains:
- Open Cash
- Close Cash
- Daily Closing summary

Customer Detail gains:
- Warranty history
- Quotation history

No new top-level full screen is required.

---

# 15. Stage 12 — Permissions

Seed permissions from Final Architecture Section 131.

High-risk permissions:
- inventory.stocktake.post
- inventory.nonsellable.manage
- warranty.shop_stock.manage
- cash.manual_movement

Application authorization remains authoritative.

UI visibility is not security.

---

# 16. Stage 13 — PostgreSQL Integration Tests

Use real PostgreSQL.

Mandatory:
- Unit conversion constraints
- Factor snapshot history
- MWA across mixed units
- Customer warranty has no stock effect
- Shop warranty bucket movement
- Stocktake transaction blocking
- Stocktake atomic post
- Cash one-open-session rule
- Cash expected balance
- Quotation zero-stock behavior
- Quotation idempotent conversion

Concurrency:
- Sale vs Stocktake
- Warranty send vs Sale
- Stocktake double post
- Quotation double conversion
- Cash session double open

---

# 17. Database Migration Strategy

The production DB does not exist yet, so the final concepts must be present before first deployment.

Development may use staged migrations.

Before commercial release:
- Apply full migration chain to empty PostgreSQL.
- Apply upgrade rehearsal to realistic prior development snapshots.
- Verify all constraints/indexes.
- Run reconciliation.
- Run integration tests.

The first production baseline must not encode the old single-unit assumption.

---

# 18. Definition of Done

This work is complete only when:
- Multi-unit purchase/sale/return/thaka works.
- Base stock never drifts.
- Warranty customer goods never alter inventory.
- Shop warranty preserves ownership/cost.
- Non-sellable queue has complete exit paths.
- Stocktake posts through movements.
- Cash close reconciles the physical drawer.
- Quotation converts safely to Sale.
- Permissions enforce every protected action.
- Reports remain financially consistent.
- Real PostgreSQL integration tests pass.
- DemoRetailState is no longer business authority.

---

# 19. Final Execution Order

A. Product Units / Conversion
B. Inventory + Cost precision
C. Non-Sellable lifecycle
D. Warranty
E. Stocktake
F. Purchasing integration
G. Sales / Returns integration
H. Thaka integration
I. Cash Sessions
J. Quotations
K. Reporting
L. WPF integration
M. PostgreSQL forensic verification

Do not jump directly to UI wiring before A–E are stable.

The inventory foundation is the spine. Everything else hangs from it.

