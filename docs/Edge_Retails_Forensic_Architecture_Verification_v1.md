# Edge Retails — Forensic Architecture Verification
## Frontend ↔ Backend ↔ PostgreSQL Cross-Audit

**Date:** 2026-09-19  
**Scope:** Frontend master specification, current WPF workspace, final backend architecture, planned PostgreSQL model  
**Verdict:** Architecture is viable, but the forensic pass found additional corrections that must be incorporated before physical database implementation.

---

# 1. Verification Method

Every frontend area was checked through the chain:

```text
Frontend Screen / Overlay
↓
User Action
↓
Application Command / Query
↓
Domain Invariant
↓
PostgreSQL Source of Truth
↓
Read DTO / UI Response
```

The current WPF implementation was also inspected to distinguish:

```text
Architecture problem
vs
Demo / incomplete implementation problem
```

The physical PostgreSQL schema is not implemented yet.

The workspace `database` directory is currently empty, and Infrastructure/Application/Domain persistence folders are still scaffolds.

Therefore this report verifies the **planned database architecture**, not an already-running physical schema.

---

# 2. Full Screen Coverage

| Screen | Backend Owner | Primary DB Sources | Architecture |
|---|---|---|---|
| First Setup / License | Setup, Licensing, Identity | system, identity | COVERED |
| Login / User Switch | Identity | identity.users, roles, sessions | COVERED |
| Dashboard | Reporting | sales, finance, thaka, inventory | COVERED |
| POS / New Sale | Sales + Inventory | sales, inventory, catalog, parties | COVERED |
| Sales History | Sales Read Model | sales | COVERED |
| Sale Detail | Sales / Returns / Printing | sales, inventory | COVERED |
| Thaka / Projects | Thaka | thaka, parties | COVERED |
| Thaka Workspace | Thaka + Inventory | thaka, inventory | COVERED |
| New Purchase | Purchasing + Inventory | purchasing, inventory, catalog | COVERED WITH SERIALIZED UI FIX |
| Purchase History | Purchasing | purchasing | COVERED |
| Inventory | Catalog + Inventory | catalog, inventory | COVERED |
| Product Detail | Catalog + Inventory | catalog, inventory, purchasing, sales | COVERED |
| Expenses | Finance | finance | COVERED |
| Customers | Parties Read Model | parties, sales, thaka | COVERED |
| Suppliers | Parties Read Model | parties, purchasing | COVERED |
| Reports | Reporting | source transaction schemas | COVERED |
| Settings | System / Identity / Catalog | system, identity, catalog | COVERED |

All 17 frontend screens have a backend/data owner.

No full frontend screen requires a new standalone backend subsystem.

---

# 3. Overlay / Action Coverage

Covered transaction actions:

```text
Complete Sale
Sale Return
New Thaka
Add Thaka Material
Record Thaka Payment
Final Settlement
Add/Edit Product
Stock Adjustment
Add/Edit Expense
Add/Edit Customer
Add/Edit Supplier
Purchase Return
Purchase Detail
Customer Detail
Supplier Detail
Confirmation
Permission Required
Toast / Error
```

Final architecture additionally requires correction overlays without increasing the full-screen count:

```text
Reverse Thaka Material
Reverse Thaka Payment
Serialized Unit Selector
Serialized Receiving / Identity Capture
Owner Recovery Authorization
```

---

# 4. Critical Frontend Drift — POS Must Not Contain Thaka Mode

The corrected frontend specification explicitly keeps Thaka out of the POS screen.

Current WPF `NewSaleViewModel` still contains:

```text
PosSaleMode.NormalSale
PosSaleMode.ThakaMaterialIssue
SwitchToThakaCommand
IssueMaterialToThakaCommand
```

This is frontend implementation drift.

Final rule:

```text
POS / New Sale
→ Local Sale only

Thaka Workspace
→ Add Thaka Material only
```

The POS Thaka mode must be removed during backend integration.

This is not a backend architecture redesign.

---

# 5. Critical Serialized Gap — POS Unit Selection

A SERIALIZED product cannot be sold using only ProductId + Quantity.

Final POS behavior:

```text
User selects serialized Product
↓
Open Select Serialized Units overlay
↓
Query eligible IN_STOCK units
↓
Select exact Serial / IMEI units
↓
Cart stores InventoryUnitIds
↓
Quantity = selected unit count
↓
Complete Sale revalidates and locks those units
```

For serialized products:

```text
free-form quantity increment
→ NOT authoritative
```

The exact unit selector is mandatory.

---

# 6. Critical Serialized Gap — Purchase Receiving

Current New Purchase frontend only captures:

```text
Product
Quantity
Cost
Sale Price
```

For a serialized product this is insufficient.

Final behavior:

```text
Serialized Purchase Line
↓
Quantity must be whole
↓
Capture Units overlay
↓
one identity row per received unit
↓
Serial / IMEI validation according to Product policy
↓
Identity count must equal Quantity
```

Purchase cannot commit if serialized identity collection is incomplete.

---

# 7. Serialized Purchase Return Link

A PurchaseReturnItem may represent multiple serialized units.

One nullable InventoryUnitId on a return line is insufficient.

Mandatory table:

```text
purchasing.return_item_units
----------------------------
purchase_return_item_id
inventory_unit_id
```

Each unit must originate from the referenced original purchase and remain supplier-return eligible.

---

# 8. Serialized Thaka Reversal Link

Material reversal of serialized goods needs exact unit trace.

Mandatory table:

```text
thaka.material_reversal_item_units
----------------------------------
material_reversal_item_id
inventory_unit_id
```

The unit must have been issued by the referenced Thaka material issue and not already reversed.

---

# 9. Serialized Stock Adjustment Link

Serialized adjustment cannot be represented only by quantity.

Mandatory table:

```text
inventory.stock_adjustment_item_units
-------------------------------------
stock_adjustment_item_id
inventory_unit_id
```

Removal/condition transfer selects existing units.

Positive adjustment captures new identities and acquisition cost.

---

# 10. General Serialized Movement Trace

A generalized exact-unit movement trail is required.

Mandatory table:

```text
inventory.movement_units
------------------------
movement_id
inventory_unit_id
from_status
to_status
```

This provides one canonical answer to:

```text
Which IMEI/serial actually moved in this inventory event?
```

It complements, rather than replaces, Sale/Thaka/Purchase business link tables.

---

# 11. Inbound Lot Source Model — Corrected

Inventory lots cannot exist only for PurchaseItems.

Opening stock and positive adjustments also create cost-bearing stock.

Final lot identity:

```text
inventory.lots
--------------
id
product_id
source_movement_id
purchase_item_id nullable
received_quantity
original_unit_cost
effective_unit_cost
created_at
```

Cost-bearing inbound sources include:

```text
PURCHASE_IN
OPENING_STOCK
POSITIVE_ADJUSTMENT
SELLABLE_RETURN restoration to original source lot
```

A sale return should restore its original lot allocation where possible rather than inventing unrelated provenance.

---

# 12. Cost State Semantics — Clarified

For QUANTITY/LENGTH products, Moving Weighted Average is the accounting cost pool.

`ProductCostState` represents all owned inventory that still carries value, not only immediately sellable quantity.

Condition transfer:

```text
SELLABLE → DAMAGED / DEFECTIVE
```

does not create COGS and does not remove ownership cost.

Sale / Thaka issue removes cost at current MWA.

Customer return adds the original sale cost snapshot back into the cost pool.

Lost / Scrap write-off removes carrying cost and creates RecognizedInventoryLoss.

Purchase Return removes the defined purchase-origin carrying amount and recalculates the average.

For quantity/length goods:

```text
Lot original cost
→ provenance / supplier-return trace

Moving Average
→ authoritative operational accounting COGS
```

These two concepts must never be conflated.

---

# 13. Customer Return Cost Flow

RESTOCK_SELLABLE:

```text
Refund revenue
Reverse original Sale COGS
Add original cost back to inventory cost pool
SELLABLE bucket increases
```

DAMAGED / DEFECTIVE:

```text
Refund revenue
Reverse original Sale COGS
Recover inventory asset at original cost
Non-sellable bucket increases
No immediate inventory loss unless written off
```

SCRAP / immediate write-off:

```text
Refund revenue
Recognize return cost recovery
Immediately remove carrying cost
RecognizedInventoryLoss = written-off cost
SCRAP operational bucket may remain for quantity trace
```

This keeps sales profit and subsequent inventory loss explainable.

---

# 14. Purchase Return Value vs Inventory Cost

Two values must remain separate:

```text
SupplierReturnValue
→ commercial amount expected/recorded against supplier
→ normally based on original base purchase cost

InventoryCostRemoved
→ inventory carrying cost removed
→ based on locked costing policy
```

Do not use one field for both concepts.

---

# 15. Store Credit Is Not Supported in V1

Current demo SalesReturnViewModel exposes:

```text
Store Credit
```

But V1 deliberately has no Customer Credit ledger.

Therefore Store Credit must be removed from the production frontend.

Allowed refund classifications:

```text
CASH
BANK
OTHER
```

If `OTHER` is used, an explanatory note/reference should be available.

A future Store Credit feature requires its own customer-credit liability/ledger architecture.

---

# 16. Return Reason and Item Condition Must Stay Separate

Frontend contract contains both:

```text
Return Reason
Item Condition
```

Database mapping:

```text
sales.returns.reason_code / reason_note
sales.return_items.disposition
```

A reason such as:

```text
Wrong Item
Customer Changed Mind
Defective
```

is not automatically the inventory disposition.

Example:

```text
Reason = Customer Changed Mind
Disposition = DAMAGED
```

can be valid if physical inspection finds damage.

Current demo UI conflates these concepts and must be changed.

---

# 17. Thaka Settlement Frontend Drift

Frontend contract requires:

```text
Material Total
Previous Payments
Final Discount
Remaining Before Settlement
Received Now
Payment Method
Final Balance
```

Current demo settlement ViewModel only accepts an exact SettlementAmount.

Final backend architecture is correct.

The production ViewModel must be rewritten to the full settlement contract.

---

# 18. Stock Adjustment Frontend Drift

Frontend requires:

```text
Adjustment Type
Quantity
New Stock Preview
Reason
Note
```

with distinct Damaged/Lost/Physical Count semantics.

Current demo implementation is only:

```text
IsIncrease + Quantity
```

Production implementation must use the final StockAdjustment command model and never directly mutate Product.Stock.

---

# 19. Product Editor Frontend Drift

Current demo ProductEdit lacks the required electronics controls:

```text
Barcode
Serial Tracking
IMEI Tracking
Warranty
Color
Variant
Tracking Mode
```

Production Product editing must use the Catalog backend model.

Product creation still does not contain Initial Stock.

---

# 20. Brand Master Ownership Gap — Resolved

Backend has `catalog.brands`, while frontend has a Brand field/filter but no explicit Brand management screen.

Final resolution:

```text
Settings sidebar remains:
Categories & Units
```

Inside that existing settings section add a compact:

```text
Brands
```

subsection with:

```text
Add
Edit
Deactivate
```

No new full screen.

Product editor uses BrandId through a controlled selector.

---

# 21. First Setup Atomicity — Added

First Setup must be resumable and transactional.

Add singleton/system state:

```text
system.installation_state
-------------------------
installation_id
setup_status
selected_module
started_at
completed_at
version
```

After license validation, database bootstrap runs as one transaction:

```text
Shop Profile
Owner User
Protected Walk-in Customer
System Roles
Permissions
Required Defaults
InstallationState = COMPLETE
```

Crash before commit leaves setup incomplete and safe to retry.

---

# 22. License Hardware Replacement Path

DEVICE_MISMATCH has no local bypass.

Replacement hardware requires:

```text
new signed license bound to replacement device
```

or a separately signed vendor reactivation artifact if that workflow is introduced.

Editing DB license status cannot reactivate the product.


# 23. Thaka Settlement/Reopen Visibility

Multiple settlement cycles and reversals must be visible to the user.

Thaka Workspace remains one full screen but its query returns a financial activity timeline containing:

```text
Payments
Payment Reversals
Settlements
Settlement Discounts
Settlement Adjustment Reversals
Reopenings
```

This can be rendered as an additional section/timeline in the existing workspace.

No new full screen.

---

# 24. Serialized Product Visibility

Product Detail should expose a conditional:

```text
View Serialized Units
```

drawer/section for SERIALIZED products.

It shows:

```text
Serial / IMEI
Condition
Current Status
Purchase Source
Current/last business reference
Warranty where relevant
```

This is operational visibility, not a new primary screen.

---

# 25. Invoice Number Preview

Current demo POS maintains a local invoice counter.

Production rule remains:

```text
Cart opening
→ no final InvoiceNumber allocation

CompleteSale transaction
→ DB sequence assigns final InvoiceNumber
```

Any pre-completion value shown by UI is informational only and must not be used as business identity.

---

# 26. Current Physical Database Status

Forensic inspection found:

```text
workspace/database
→ empty

EdgeRetails.Infrastructure/Persistence
→ scaffold only

EdgeRetails.Application/Features
→ scaffold

EdgeRetails.Domain/Entities
→ scaffold
```

Current project files also do not yet contain the EF Core / Npgsql implementation packages required for the final database layer.

Therefore:

```text
Database Architecture
→ designed

Physical PostgreSQL schema / migrations
→ NOT IMPLEMENTED YET
```

No claim of physical database verification should be made until migrations are created and applied to PostgreSQL.

---

# 27. Current Frontend Implementation Status

The WPF project is still a design/demo scaffold.

Confirmed examples:

```text
DemoTransactionService
DemoRetailState
DemoPurchaseInventoryService
DesignPreviewSessionContext
```

Several navigation areas still resolve through PlaceholderPageViewModelFactory, including current Expenses, Customers, Suppliers, Reports, and Settings paths.

This is an implementation status issue, not an architecture failure.

Production backend integration must replace demo business state with `IApplicationGateway`.

---

# 28. Frontend ↔ Backend Boundary Rule

Production ViewModels may keep:

```text
UI state
selection
formatting
loading/error display
command invocation
```

They must lose:

```text
stock authority
invoice generation
return eligibility authority
Thaka balance authority
purchase return eligibility authority
profit calculation
direct business mutation
```

Those move behind Application commands/queries.

---

# 29. Database Relationship Addendum

Mandatory serialized relations after forensic review:

```text
inventory.movement_units

sales.sale_item_units
sales.return_item_units

purchasing.return_item_units

thaka.material_issue_item_units
thaka.material_reversal_item_units

inventory.stock_adjustment_item_units
```

Receiving ownership remains:

```text
inventory.units.purchase_item_id
```

These relations guarantee exact serial/IMEI trace across every business movement.

---

# 30. Final Forensic Verdict

Frontend contract coverage:

```text
17 / 17 screens mapped
All major overlays mapped
```

Core backend architecture:

```text
PASS after this addendum
```

Planned PostgreSQL model:

```text
PASS after serialized relationship,
lot-source, cost-state, and setup-state amendments
```

Current physical PostgreSQL implementation:

```text
NOT YET IMPLEMENTED
```

Current WPF production-backend integration:

```text
NOT YET COMPLETE
```

Most discovered problems are implementation drift from demo code, not failures in the core modular architecture.

The new architecture-level findings in this report must be incorporated into the authoritative final architecture before database migrations begin.



---

# 31. Independent Re-Verification Pass — Final Architecture vs Current Workspace

A second independent verification pass was performed after the Final Architecture Report received Sections 91–101.

This pass checked three separate layers:

```text
A. Frontend Contract ↔ Backend Architecture
B. Backend Architecture ↔ Planned PostgreSQL Model
C. Planned Model ↔ Current Physical Workspace Implementation
```

These layers are intentionally reported separately.

---

# 32. Frontend Contract ↔ Backend Architecture Verdict

**Result: PASS**

All 17 full screens have an explicit backend owner, query/command path, and authoritative data source.

Critical frontend contracts are represented in the final architecture:

```text
First Setup / License
Login / User Switch
Dashboard
POS / New Sale
Sales History
Sale Detail
Thaka Projects
Thaka Workspace
New Purchase
Purchase History
Inventory
Product Detail
Expenses
Customers
Suppliers
Reports
Settings
```

The following frontend-specific requirements are also covered:

```text
Invoice-level discount
Cash / Bank / Other payment classification
Amount received and cash change
Sale Return reason + independent condition/disposition
Purchase Other Charges
Purchase Return eligibility
Thaka Final Discount / Received Now / Final Balance
Quantity / Length / Serialized product tracking
Serial / IMEI conditional capture
No Initial Stock field
Stock Adjustment reason semantics
Role-aware Profit visibility
Dashboard without charts
Enhanced charts only inside Reports
Categories / Units / Brands master management
Backup / License / Database diagnostics
```

No frontend screen requires a new primary backend subsystem.

---

# 33. Planned PostgreSQL Model ↔ Backend Architecture Verdict

**Result: PASS, subject to implementing the complete final schema rather than the earlier Phase 8 draft alone.**

The final database implementation must include the forensic addendum concepts.

Critical required structures include:

```text
system.installation_state

catalog.products
catalog.product_barcodes
catalog.brands
catalog.units
catalog.product_price_history

inventory.stock_balances
inventory.movements
inventory.movement_effects
inventory.movement_units
inventory.lots
inventory.lot_bucket_balances
inventory.consumption_allocations
inventory.cost_states
inventory.units
inventory.stock_adjustments
inventory.stock_adjustment_items
inventory.stock_adjustment_item_units

sales.sales
sales.sale_items
sales.sale_payments
sales.sale_item_units
sales.returns
sales.return_items
sales.return_item_units

purchasing.purchases
purchasing.purchase_items
purchasing.returns
purchasing.return_items
purchasing.return_item_units

thaka.projects
thaka.material_issues
thaka.material_issue_items
thaka.material_issue_item_units
thaka.material_reversals
thaka.material_reversal_items
thaka.material_reversal_item_units
thaka.payments
thaka.payment_reversals
thaka.settlements
thaka.reopenings
settlement adjustment / reversal linkage

finance.expenses
finance.expense_categories
finance.expense_subcategories

identity.users
identity.roles
identity.permissions
identity.role_permissions
identity.user_permission_overrides
identity.user_sessions

system.receipt_template_settings
system.backup_settings
system.worker_heartbeats
system.background_jobs
system.backup_runs
system.operational_events

audit.events
```

The architecture now has relational coverage for all exact serialized-unit paths:

```text
Purchase Receive
Sale
Sale Return
Purchase Return
Thaka Issue
Thaka Reversal
Stock Adjustment
General Inventory Movement
```

No serial/IMEI business movement needs to rely on JSON or quantity-only linkage.

---

# 34. Current Physical Database Implementation Verdict

**Result: NOT IMPLEMENTED YET**

Forensic workspace evidence:

```text
workspace/database
→ empty

src/EdgeRetails.Infrastructure/Persistence
→ scaffold directory, no physical DbContext/migrations

src/EdgeRetails.Infrastructure/EdgeRetails.Infrastructure.csproj
→ no EF Core / Npgsql package references

src/EdgeRetails.Domain/Entities
→ scaffold

src/EdgeRetails.Application/Features
→ scaffold
```

Therefore the following do not yet exist physically:

```text
EdgeRetailsDbContext
EF Core entity configurations
PostgreSQL migrations
PostgreSQL schemas/tables
indexes
check constraints
partial unique constraints
row-locking implementation
Dapper reporting queries
physical reconciliation queries
```

The **database architecture is verified**, but the **database implementation is not yet verifiable**.

A physical DB PASS can only be issued after migrations are created, applied to real PostgreSQL, and tested.

---

# 35. Current WPF ↔ Production Backend Drift — Confirmed

The current WPF frontend is still a demo/design implementation.

Confirmed production integration gaps:

## POS contains Thaka mode

File:

```text
src/EdgeRetails.Desktop/ViewModels/NewSaleViewModel.cs
```

Current code contains:

```text
PosSaleMode.ThakaMaterialIssue
SwitchToThakaCommand
IssueMaterialToThakaCommand
```

Final contract requires:

```text
POS → Local Sale only
Thaka Workspace → Material Issue
```

## POS still allocates local demo invoice preview/counter

Current code contains local `_invoiceCounter`.

Final InvoiceNumber authority belongs to the database transaction.

## Sale Return conflates reason and disposition

File:

```text
SalesReturnViewModel.cs
```

Current `ReturnDisposition` values mix:

```text
Defective
Damaged
CustomerChangedMind
Other
```

Production contract must separate:

```text
Return Reason
from
Inventory Disposition
```

## Store Credit exists in demo but V1 has no credit ledger

Current return ViewModel supports:

```text
Store Credit
```

Production V1 only supports:

```text
Cash
Bank
Other
```

unless a future Customer Credit ledger is designed.

## Discounted sale returns are artificially blocked in demo

Current code blocks Return processing when Sale has invoice-level discount.

Final architecture has already solved proportional discount allocation and residual refund rounding.

Production return workflow must therefore support discounted invoices.

## Final Settlement UI contract is incomplete

Current `ThakaFinalSettlementViewModel` uses only exact `SettlementAmount`.

Production settlement must use:

```text
Material Total
Previous Payments
Final Discount
Remaining Before Settlement
Received Now
Payment Method
Final Balance
```

## Stock Adjustment is oversimplified

Current code uses:

```text
IsIncrease + Quantity
```

Production needs:

```text
Adjustment Mode
Reason
Bucket/state effect
Physical Count semantics
Serialized unit selection
Cost/loss treatment
```

## Product editor lacks electronics tracking controls

Current ProductEdit lacks production controls for:

```text
Barcode
TrackingMode
Serial Tracking
IMEI Tracking
Warranty
Color
Variant
```

## Purchase receiving lacks serialized-unit capture

Current NewPurchase only captures Product / Quantity / Cost / Sale Price.

A SERIALIZED Purchase cannot commit until exact received identities equal quantity.

## Several screens still use placeholder routing

Current PageViewModelFactory only has production-like implementations for a subset of screens.

Other navigation targets continue through PlaceholderPageViewModelFactory.

This is implementation incompleteness, not architecture failure.

---

# 36. Final Three-Layer Verdict

```text
Frontend Specification
        ↕
Final Backend Architecture
        PASS

Final Backend Architecture
        ↕
Final Planned PostgreSQL Model
        PASS

Final Planned PostgreSQL Model
        ↕
Physical PostgreSQL Database
        NOT YET IMPLEMENTED

Current WPF Demo
        ↕
Production Backend Contract
        PARTIAL / REQUIRES INTEGRATION REWRITE
```

The architecture itself is cleared for implementation.

The next engineering step must begin with the final PostgreSQL schema/migration foundation and must use the Final Architecture Report including Sections 91–101.

No implementation should use the older Phase 8 database sketch in isolation.



---

# 37. Shop-Holder Operational Findings — Implemented Into Final Architecture

A subsequent shop-holder audit identified critical day-to-day operational gaps.

These are no longer open findings.

They are incorporated into the authoritative Final Architecture Report Sections 103–140.

Implemented architecture additions:

```text
Multi-Unit / Packaging Conversion
Unit-level barcodes
Base-quantity costing
Customer Warranty Claims
Shop-Owned Supplier Warranty
WITH_SUPPLIER inventory state
Batch Physical Stocktake
Non-Sellable operational queue
Cash Session / Daily Closing
Quotation / Estimate
```

Key correction:

```text
Product stock truth is now always Base Quantity.
```

This is mandatory for real electrical-shop cases such as:

```text
Box → Piece
Roll → Meter
Foot → Meter
```

The physical database must implement the complete model before receiving production data.

---

# 38. Revised Forensic Verdict After Shop-Holder Audit

```text
Frontend Contract Coverage       PASS
Backend Architecture             PASS
Shop Operational Coverage        PASS
Planned PostgreSQL Model         PASS
Physical PostgreSQL              NOT YET BUILT
Production WPF Integration       NOT YET COMPLETE
```

The implementation plan is:

```text
docs/Edge_Retails_ShopHolder_Critical_Implementation_Plan_v1.md
```

Normal Customer Credit/Udhaar and Supplier Accounts Payable remain intentionally deferred rather than half-implemented.

