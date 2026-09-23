# Edge Retails - Final Backend Architecture Report
## Final Architecture Baseline Through Section 233

**Product:** Edge Retails  
**Vertical:** Electronics / Electrical Retail  
**Platform:** Windows Desktop POS  
**Status:** FINAL ARCHITECTURE BASELINE
**Architecture Coverage:** Sections 0 through 233
**Supersedes:** Any earlier unresolved or contradictory architecture notes  
**Primary Previous Baseline:** `docs/Archive/Architecture_History/Edge_Retails_Backend_Architecture_Legacy_Phase8.md`

---

# 0. Current Architecture Authority Index

This index is the reader entry point for the current canonical V1 architecture.

- Core runtime and foundational rules: Sections 1-90
- Historical forensic/addendum context: Sections 91-169
- Tracking and physical identity: Sections 170-191
- Product / Inventory: Sections 192-208
- POS / Navigation: Sections 209-214
- Supplier Khata: Sections 215-220
- Warranty operational surface: Sections 221-225
- Reporting / Migration: Sections 226-227
- Current V1 architecture declaration: Section 228
- Implementation order: Section 229
- Performance: Section 230
- Retention: Section 231
- Mandatory tests: Section 232
- Architecture-to-Implementation gate: Section 233
- International forensic remediation authority: Section 233.1

Authority precedence:
1. A later explicit correction of the same subject supersedes an older conflicting rule.
2. Historical milestone declarations in Sections 90, 102, 139, and 169 are context only, not current declarations.
3. Section 228 is the current V1 architecture declaration.
4. Section 233 is the current architecture-to-implementation gate; Section 233.1 is the incorporated international forensic remediation authority for the subjects it covers.
5. Historical implementation-status snapshots do not describe current workspace state.
6. External forensic/audit reports are evidence inputs, not architecture authority. A claim from an audit becomes current only when it matches this canonical document or is explicitly incorporated through an approved architecture correction.
7. Repository/build/test completion claims require physical workspace evidence and must not be inferred from architecture wording such as FINAL, required, mandatory, or planned.

Audit/forensic claim handling:

```text
claim matches current canonical rule
-> VALID CURRENT RULE

claim describes an older milestone and is clearly historical
-> HISTORICAL CONTEXT ONLY

claim conflicts with a later canonical correction
-> STALE / REJECTED AS CURRENT AUTHORITY

claim asserts implementation/test completion
-> VERIFY FROM REPOSITORY / BUILD / TEST EVIDENCE
```

A stale external report must never override later Warranty, Supplier Khata, TrackingCode, lock-order, navigation, schema-name, migration, or performance decisions.

---

# 1. Executive Status

The architecture has completed frontend-to-backend mapping, domain design, PostgreSQL design, application structure, reliability, security, diagnostics, performance, testing, and future multi-terminal planning.

Phase 16 identified several implementation-critical gaps. All critical architecture gaps are resolved in this report.

The system is now approved to move from architecture into implementation planning.

No known critical architecture blocker remains.

---

# 2. Final Runtime Architecture

Standalone V1:

```text
EdgeRetails.exe
|-- WPF Presentation
|-- Application Layer
|-- Domain Layer
`-- Infrastructure Layer
        v
Local PostgreSQL
        ^
EdgeRetails.Worker.exe
```

Business flow:

```text
View
v
ViewModel
v
IApplicationGateway
v
LocalApplicationGateway
v
Command / Query Dispatcher
v
Application Handler
v
Domain
v
Infrastructure
v
PostgreSQL
```

WPF never contains SQL, costing rules, stock mutation, permission authority, invoice numbering, or settlement math.

---

# 3. Final Solution Projects

V1 keeps the existing five-project structure:

```text
EdgeRetails.Domain
EdgeRetails.Application
EdgeRetails.Infrastructure
EdgeRetails.Desktop
EdgeRetails.Worker
```

Electronics is a logical module boundary in V1.

No runtime DLL plugin system is used.

General Retail / Mobile modules are not separate projects until those products actually exist.

---

# 4. Final Technology Stack

```text
Windows Desktop
WPF
XAML
C#
.NET 10 LTS
MVVM
Modular Monolith
PostgreSQL
Npgsql
EF Core
Dapper / Raw SQL
.NET Worker Service
Serilog
xUnit
WiX Toolset + Burn
Encrypted Cloud Backup
Local Signed Licensing
```

EF Core owns transactional writes, normal CRUD, relationships, migrations, and aggregate persistence.

Dapper owns dashboard, reports, large history grids, and complex read models.

---

# 5. Final PostgreSQL Schemas

```text
system
identity
parties
catalog
inventory
sales
purchasing
thaka
finance
audit
reporting
```

No schema-per-customer design.

One shop installation uses one authoritative `edge_retails` database.

---

# 6. Identity Strategy

Internal business IDs use application-generated UUIDv7.

Human document numbers remain separate.

Examples:

```text
INV-000001
SR-000001
P-000001
PR-000001
THK-000001
TMI-000001
TP-000001
TS-000001
SA-000001
EXP-000001
```

Document numbers are concurrency-safe and may contain gaps after rollback.

Never generate document numbers using `MAX()+1`.

---

# 7. Monetary and Quantity Precision

```text
Final monetary totals      numeric(18,2)
Unit / moving costs        numeric(18,6)
Quantities                 numeric(18,3) or approved higher precision
Rates / percentages        numeric(9,4)
Business timestamps        timestamptz
Business dates             date
Concurrency version        bigint
```

No floating-point type is used for financial truth.

All monetary rounding uses one central MoneyRoundingPolicy.

---

# 8. Catalog Product Model - Final

A Product defines what an item is. It does not own current stock.

Core fields:

```text
Id
Sku
NormalizedSku
Name
CategoryId
BrandId
UnitId

TrackingMode
SerialTrackingEnabled
ImeiTrackingEnabled

DefaultSalePrice
ReferencePurchaseCost
MinimumStock

DefaultWarrantyMonths
AttributesJson

IsActive
CreatedAt
UpdatedAt
Version
```

Tracking modes:

```text
QUANTITY
LENGTH
SERIALIZED
```

Rules:

```text
QUANTITY / LENGTH
-> SerialTrackingEnabled = false
-> ImeiTrackingEnabled = false

SERIALIZED / INDIVIDUALLY TRACKED
-> Individual physical unit tracking enabled
-> Each physical base unit receives a permanent Edge Retails TrackingCode (DealerCode-ProductSKU-ItemSequence)
-> Manufacturer SerialTrackingEnabled and ImeiTrackingEnabled are optional overlays
```

SKU and barcode are separate concepts.

A product has a permanent business SKU (`ProductSKU`) identifying what product/model this is across all suppliers.

A product may have multiple barcodes through `catalog.product_barcodes`.

---

# 9. Serial / IMEI Rules & Physical TrackingCode Distinction - Resolved

Physical inventory tracking distinguishes between internal business tracking identity and manufacturer hardware identities:

```text
Physical Unit Identity:
TrackingCode (Edge Retails authority: DealerCode-ProductSKU-ItemSequence)

Manufacturer Identities (optional overlay):
SerialNumber (manufacturer serial)
IMEI1 / IMEI2 (mobile/cellular hardware identifiers)
```

If Serial Tracking is enabled:

```text
SerialNumber required per serialized unit (if manufacturer serial exists)
```

If IMEI Tracking is enabled:

```text
IMEI1 required
IMEI2 optional
```

TrackingCode is mandatory for every individually tracked physical unit, regardless of whether a manufacturer Serial Number exists.

Identifiers are normalized before uniqueness checks.

Partial unique indexes protect SerialNumber, IMEI1, IMEI2, and TrackingCode.

Serial/IMEI/TrackingCode data is never stored only in JSONB.

---

# 10. Inventory Golden Rule

```text
Stock is a result.
Movement is truth.
```

No feature may directly edit Current Stock.

All stock changes occur through a business transaction or Stock Adjustment.

---

# 11. Inventory Buckets

```text
SELLABLE
DAMAGED
DEFECTIVE
WITH_SUPPLIER
SCRAP
```

`SELLABLE` is the only stock available for normal POS sale and Thaka issue. `WITH_SUPPLIER` represents physical stock transferred to or held with suppliers/vendors for warranty replacement or return verification.

---

# 12. Inventory Movement Model - Corrected

The earlier single-delta model is superseded.

Final structure:

```text
inventory.movements
-------------------
id
product_id
movement_type
reference_type
reference_id
reference_line_id
unit_cost_snapshot
recognized_loss_amount
reason_code
note
performed_by
occurred_at
correlation_id
```

Each movement has one or more bucket effects:

```text
inventory.movement_effects
--------------------------
movement_id
stock_bucket
quantity_delta
quantity_before
quantity_after
```

Example damage transfer:

```text
SELLABLE -2
DAMAGED  +2
```

Example sale:

```text
SELLABLE -2
```

Example defective customer return:

```text
DEFECTIVE +1
```

This model is the final inventory audit truth.

---

# 13. Stock Balance

`inventory.stock_balances` remains the fast current-state projection:

```text
ProductId
SellableQty
DamagedQty
DefectiveQty
ScrapQty
LastMovementId
UpdatedAt
Version
```

Movement effects and StockBalance update in one PostgreSQL transaction.

All buckets must remain non-negative.

---

# 14. Opening Stock - Resolved

Product creation has no Initial Stock field.

Opening stock is entered through a controlled Stock Adjustment:

```text
AdjustmentMode = SET_PHYSICAL_COUNT
Reason = OPENING_STOCK
```

Quantity/length opening stock requires a valid cost basis.

If no current authoritative cost exists, ReferencePurchaseCost is proposed; otherwise Unit Cost must be entered.

Serialized opening stock requires exact physical-unit identities and actual acquisition cost per unit. Manufacturer Serial/IMEI values are additionally required only where the Product tracking policy enables those overlays.

Every positive serialized opening-stock/stock-adjustment unit also requires explicit physical-origin provenance. It uses the authorized STOCK_ADJUSTMENT InventoryUnit origin defined in Sections 176 and 236, an explicit source Supplier/SupplierProduct for TrackingCode sequence authority, and a SourceStockAdjustmentItemId. A designated opening/legacy source Supplier may be used when the historical commercial Supplier is genuinely unknown, but that provenance relationship creates no Purchase and no Supplier Khata payable effect.

No generic `Stock +5` operation exists for serialized products.

---

# 15. Stock Adjustment - Final Semantics

Adjustment modes:

```text
DELTA
SET_PHYSICAL_COUNT
```

Reasons include:

```text
OPENING_STOCK
DAMAGED
LOST
PHYSICAL_COUNT_CORRECTION
OTHER
```

Behavior:

```text
DAMAGED
SELLABLE -> DAMAGED

LOST
SELLABLE -> removed
Recognized Inventory Loss created

PHYSICAL_COUNT_CORRECTION
target physical count is authoritative
difference creates movement effects

OTHER
direction, reason, note, and cost treatment are explicit
```

Serialized adjustments require exact unit identities.

Positive serialized adjustment requires identity plus acquisition cost.

---

# 16. Inventory Cost Provenance - Corrected

Inventory lots retain acquisition-origin cost.

Final supporting structure:

```text
inventory.lots
inventory.lot_bucket_balances
inventory.consumption_allocations
inventory.cost_states
```

`lot_bucket_balances` preserves quantity and cost origin across:

```text
SELLABLE
DAMAGED
DEFECTIVE
SCRAP
```

This supports supplier-return eligibility, condition transfers, and correct restoration of non-sellable stock.

---

# 17. Costing Policy

Quantity and length products:

```text
Moving Weighted Average
```

Serialized products:

```text
Actual Individual Unit Cost
```

`ReferencePurchaseCost` is an editable purchasing reference only.

It is never authoritative COGS when transaction-derived cost exists.

Product Detail Current Cost means:

```text
QUANTITY/LENGTH:
Current Moving Weighted Average

SERIALIZED:
Average acquisition cost of current sellable/in-stock serialized units
```

Historical sale profit uses immutable cost snapshots.



# 18. Serialized Unit Lifecycle & Provenance - Corrected

Final states:

```text
IN_STOCK
SOLD
ISSUED_THAKA
DAMAGED
DEFECTIVE
WITH_SUPPLIER
SUPPLIER_RETURNED
SCRAPPED
RECEIPT_VOIDED
```

There is no generic customer `RETURNED` state.

A customer return immediately resolves the unit into:

```text
IN_STOCK
DAMAGED
DEFECTIVE
SCRAPPED
```

according to the return disposition.

A unit cannot be sold while SOLD, ISSUED_THAKA, WITH_SUPPLIER, SUPPLIER_RETURNED, SCRAPPED, or RECEIPT_VOIDED.

Each physical unit is assigned a permanent `TrackingCode` (`DealerCode-ProductSKU-ItemSequence`) that stays immutable across all state transitions.

---

# 19. Serialized Sale History - Corrected

A serialized unit may be sold, returned, and legitimately sold again.

Therefore one permanent `InventoryUnit.SaleItemId` relationship is forbidden.

Final historical association:

```text
sales.sale_item_units
---------------------
id
sale_item_id
inventory_unit_id
unit_cost_snapshot
warranty_months_snapshot
warranty_start
warranty_end
returned_at nullable
is_active
```

A partial unique index allows only one active sale assignment per inventory unit.

Historical inactive sale assignments remain permanently traceable via `TrackingCode` and `InventoryUnit.Id`.

---

# 20. Warranty Model - Corrected

Product stores:

```text
DefaultWarrantyMonths
```

Warranty for a serialized item is snapshotted per sale assignment.

This means a returned/resold phone can have separate warranty history for each sale cycle.

Warranty history is not overwritten on InventoryUnit.

---

# 21. Sales Aggregate

`sales.sales` stores:

```text
Id
InvoiceNumber
CustomerId
CashierUserId
SessionId
CompletedAt
Subtotal
InvoiceDiscount
GrandTotal
SaleStatus
PaymentStatus
ClientOperationId
ReceiptTemplateSnapshot
CreatedAt
```

Completed sale financial values are immutable.

---

# 22. SaleItem - Corrected Discount Model

Final SaleItem financial fields:

```text
Quantity
UnitPrice
GrossLineTotal
AllocatedInvoiceDiscount
NetLineTotal

UnitCostSnapshot
TotalCostSnapshot
GrossProfitSnapshot
```

Invoice discount is allocated proportionally across items.

The last deterministic line absorbs any monetary rounding residual.

No per-item discount exists in V1.

---

# 23. Complete Sale Price Authority - Corrected

The Complete Sale command does not accept an authoritative requested sale price.

It may include `ExpectedUnitPrice` only to detect stale cart state.

Final price authority:

```text
catalog.products.DefaultSalePrice
```

at transaction completion.

If a price changed after the cart was built, the command returns a conflict/recalculation result.

There is no hidden cashier price-override mechanism in V1.

---

# 24. Complete Sale Transaction

Final flow:

```text
System State Guard
v
License Guard
v
Authenticated Session
v
sales.create Authorization
v
Input Validation
v
ClientOperationId Check
v
BEGIN
v
Reload Product/Price
v
Lock Stock in deterministic ProductId order
v
Validate Sellable Stock
v
Validate Serialized Units
v
Recalculate Subtotal
v
Allocate Invoice Discount
v
Recalculate Grand Total
v
Validate Payment
v
Generate Invoice Number
v
Create Sale + Items + Payment
v
Create Inventory Movements/Effects
v
Create Lot Consumption
v
Update StockBalance / CostState
v
Update Serialized Unit states
v
Business Audit
v
COMMIT
v
Receipt Generation / Printing
```

Printer failure never rolls back the committed sale.

Receipt delivery/reprint follows the durable post-commit rules in Sections 51.1-51.3.

---

# 25. Payment Rules - Final

Counter Sale V1 is fully paid at completion.

Methods:

```text
CASH
BANK
OTHER
```

Cash:

```text
AmountTendered >= GrandTotal
AppliedAmount = GrandTotal
ChangeGiven = AmountTendered - GrandTotal
```

Bank / Other:

```text
AppliedAmount = GrandTotal
ChangeGiven = 0
Reference optional
```

No partially-paid POS invoice exists in V1.

PaymentMethod is classification, not a bank/cash ledger.

---

# 26. Sale Idempotency

All Complete Sale requests carry a unique `ClientOperationId`.

Retry with the same operation ID returns the already-created sale.

This protects:

```text
double-click
double F10
UI retry
crash after commit
ambiguous future LAN response
```

The database unique constraint is the final duplicate barrier.

---

# 27. Sale Return - Final

Sale Return is a separate immutable aggregate.

Original Sale remains unchanged.

Eligibility:

```text
Eligible Qty
=
Original Sold Qty
-
Completed Prior Return Qty
```

Disposition:

```text
RESTOCK_SELLABLE
DAMAGED
DEFECTIVE
SCRAP
```

Inventory destination is determined by disposition.

Serialized return requires exact original InventoryUnit.

---

# 28. Partial Refund Rounding - Resolved

Partial refund is based on the original SaleItem `NetLineTotal`, not the gross undiscounted line.

Each completed return stores its actual refund amount.

For any SaleItem:

```text
RemainingRefundable
=
OriginalNetLineTotal
-
CompletedPriorRefunds
```

The final eligible return receives the exact remaining refundable amount.

Invariant:

```text
sum(all successful return refunds for a fully returned line)
=
original NetLineTotal
```

This removes cumulative rounding drift.

---

# 29. Return Cost Treatment - Final

Every return records:

```text
OriginalCostAmount
CostReversalAmount
Disposition
```

RESTOCK_SELLABLE restores cost-bearing stock.

DAMAGED / DEFECTIVE preserves cost provenance in non-sellable bucket.

SCRAP recognizes appropriate inventory loss when the returned asset is written off.

The architecture does not pretend non-sellable stock is sellable stock.

---

# 30. Recognized Inventory Losses - Added

Inventory loss is a first-class financial reporting concept.

Examples:

```text
LOST
SCRAPPED / written off
irrecoverable inventory correction
```

Movement records can carry:

```text
RecognizedLossAmount
```

Final Local Net Profit:

```text
Net Local Profit
=
Net Local Sales
-
Net Local COGS
-
Operating Expenses
-
Recognized Inventory Losses
```

Damaged/Defective stock is not automatically expensed while it remains an owned recoverable asset.

---

# 31. Purchasing Aggregate

Purchase stores:

```text
PurchaseNumber
SupplierId
SupplierInvoiceNumber
PurchaseDate
Note
Subtotal
OtherCharges
GrandTotal
Status
CreatedBy
ClientOperationId
```

PurchaseItem stores:

```text
Quantity
UnitBaseCost
BaseLineTotal
AllocatedOtherCost
EffectiveUnitCost
EffectiveLineCost
SalePriceAtPurchase
Product snapshots
```

Completed Purchase is immutable except controlled status/correction metadata.

---

# 32. Landed Cost

Other Charges are acquisition cost.

Allocation:

```text
LineShare = BaseLineTotal / PurchaseSubtotal

AllocatedOtherCost =
OtherCharges x LineShare
```

The deterministic final line absorbs the rounding residual.

Invariant:

```text
sum(AllocatedOtherCost)
=
Purchase.OtherCharges
```

---

# 33. Purchase Return

Purchase Return represents an actual return to supplier.

Eligibility is backend-derived from lot origin and consumption.

```text
Eligible Qty
=
Received Purchase-Origin Qty
-
Consumed Qty
-
Completed Prior Purchase Returns
```

Serialized Purchase Return requires exact original InventoryUnit.

Original Purchase remains intact.

---

# 34. Purchase Void - Added

A mistaken purchase data entry is not always a supplier return.

`VoidPurchaseCommand` is allowed only when:

```text
Purchase is completed
No supplier return exists
No purchase-origin quantity has been consumed
All relevant stock remains available
Serialized units remain eligible
```

It atomically reverses:

```text
Purchase inventory
Lots
Cost State
Serialized receiving state
Relevant price-history effect where safe
Audit
```

Purchase status becomes VOIDED.

If a newer Product sale-price change occurred after the purchase, voiding the Purchase does not overwrite that newer price.

---

# 35. Purchase Print - Added

Purchase Detail Print uses:

```text
GetPurchaseDocumentQuery
v
PurchaseDocumentDto
v
IDocumentPrinter
```

Printing never changes Purchase financial state.

A printer failure does not roll back or void a Purchase.

Purchase-document delivery/reprint follows the durable post-commit rules in Sections 51.1-51.3.

---

# 36. Thaka Core Model

Thaka remains separate from local POS Sales.

```text
Customer
v
ThakaProject
|-- Material Issues
|-- Payments
|-- Settlements
|-- Reopenings
|-- Material Reversals
`-- Payment Reversals
```

Thaka is never stored in the Sales table.

---

# 37. Thaka Revenue Recognition

Revenue is recognized when material is issued.

```text
Material issue
-> stock leaves
-> customer/project charge increases
-> Thaka material revenue increases
```

Payment is collection of receivable, not new revenue.

Thaka remains separate from Today Local Sales and Local Sale Profit.

---

# 38. Thaka Equations - Final

Per project:

```text
GrossMaterialCharges
=
valid material issues
-
material reversals

SettlementAdjustments
=
valid settlement discounts
-
explicit reversed settlement adjustments

PaymentsCollected
=
valid payments
-
payment reversals

CurrentBalance
=
GrossMaterialCharges
-
SettlementAdjustments
-
PaymentsCollected
```

Settlement succeeds only when:

```text
FinalBalance = 0
```

---

# 39. Thaka Settlement Cycles - Resolved

A settled project may be explicitly reopened.

Reopening does not erase the prior settlement.

Previous payments and valid final discount remain historical financial effects.

New material after reopening creates new balance.

A future settlement creates another settlement cycle.

A wrongly entered settlement discount is corrected through a linked immutable reversal/adjustment, never by editing the old settlement row.

---

# 40. Thaka Material Reversal - Promoted to V1

V1 includes a contextual `Reverse Thaka Material` workflow.

Rules:

```text
Reference original issue/item
Reverse Qty <= unreversed Qty
Project must be in a valid correction state
Serialized product requires exact InventoryUnit
Physical stock return accompanies financial reversal
Disposition controls returned bucket
```

A settled project must be reopened before reversal.

No full extra screen is added.

---

# 41. Thaka Payment Reversal - Promoted to V1

V1 includes a contextual `Reverse Thaka Payment` workflow.

Payment is never deleted.

A reversal:

```text
references original payment
creates immutable reversal record
increases project balance
creates audit
```

A settled project must first be reopened.

---

# 42. Serialized Thaka State - Added

A serialized unit issued to Thaka enters:

```text
ISSUED_THAKA
```

It is unavailable for POS sale, supplier return, or another active Thaka issue.

A valid material reversal returns it to the selected resulting condition.

---

# 43. Customer Master

Customer stores master/contact information only.

One protected Walk-in Customer exists for local POS.

Customer aggregate values are read models, not editable columns.

Final definitions:

```text
LocalSales
=
lifetime Net Local Sales for that customer
excluding Thaka

LastSale
=
latest completed local sale date

ActiveThakaCount
=
count active projects

CurrentThakaBalance
=
sum active/current project balances
```

---

# 44. Supplier Master & DealerCode Authority

The backend authoritative entity for upstream business parties is `Supplier` (`Dealer = Supplier` for inventory provenance purposes; no separate Dealer table).

Supplier core fields:

```text
Id
DealerCode
Name
Phone
City
Address
IsActive
CreatedAt
UpdatedAt
Version
```

DealerCode rules:

```text
Format: 2-letter prefix from Supplier Name + incrementing integer (e.g. AB1, AB2, AL1, NA10)
Scope: Globally unique across all suppliers
Immutability: Permanent after issuance; renaming Supplier does not alter DealerCode
Deletion: Hard delete forbidden if referenced; deactivated via IsActive = false
```

Final aggregate definitions:

```text
TotalPurchases
=
lifetime Net Purchase Spend
after Purchase Returns and Purchase Voids

LastPurchase
=
latest non-void completed purchase
```

Supplier Khata / Accounts Payable is now an approved V1 subledger under Sections 215-220. Full General Ledger accounting remains out of scope.

---

# 45. Expenses

Expense states:

```text
POSTED
VOIDED
```

Remove Expense means VOID.

Staff Salary remains an Expense category, not payroll.

Inventory Purchases are never Operating Expenses.

Controlled edits are audited.

---

# 46. Dashboard KPI Definitions - Final

```text
Today Sales
=
today's Net Local Sales

Today Profit
=
today's Local Net Profit
=
Net Local Sales
- Net Local COGS
- Operating Expenses
- Recognized Inventory Losses

Expenses
=
today's POSTED Operating Expenses

Low Stock
=
active products where SellableQty <= MinimumStock

Active Thakas
=
count ACTIVE projects

Thaka Value
=
sum current outstanding balances of ACTIVE projects

Today Thaka Material
=
today's issued material charges
- today's material reversals
```

Thaka payments do not reduce Today Thaka Material.

Dashboard remains chart-free.

---

# 47. Reports - Final Financial Definitions

```text
Gross Local Sales
=
completed POS sale GrandTotals

Sale Returns
=
successful refund value in selected period

Net Local Sales
=
Gross Local Sales - Sale Returns

Net Local COGS
=
Sale COGS - valid Return Cost Reversals

Gross Profit
=
Net Local Sales - Net Local COGS

Net Profit
=
Gross Profit
- Operating Expenses
- Recognized Inventory Losses

Net Purchase Spend
=
Completed Purchase value
- Purchase Returns
- Purchase Voids / reversal effect

Thaka Material
=
issued charges
- material reversals

Thaka Outstanding
=
current project receivable
```

Correction events affect the date on which the correction occurs.

Historical original transactions are not rewritten into another period.

---

# 48. Reports Visual Scope - Final Precedence

Dashboard:

```text
No charts
```

Reports:

```text
Daily
Monthly
Yearly
```

The later approved enhanced Reports requirement supersedes the old minimal-yearly/no-extra-chart restriction only for the Reports screen.

Reports include useful trend graphs and averages.

Purchases remain a separate KPI/series and are not mixed into Expense or Net Profit.

Thaka remains visually and financially separate from Local Sales.

---

# 49. Average Reporting Rules

```text
Average Invoice Value
=
Net Local Sales / Completed Invoice Count

Average Gross Profit per Invoice
=
Gross Profit / Completed Invoice Count

Average Daily Sales
=
Net Local Sales / Calendar Days in selected period

Average Daily Expenses
=
Operating Expenses / Calendar Days in selected period
```

Zero-sales calendar days remain in the denominator.

Current-year monthly averages use elapsed/selected months rather than always dividing by 12.

All grouping uses Shop Time Zone.

---

# 50. Receipt Configuration - Corrected

Global receipt template settings and local printer settings are now separate.

```text
system.receipt_template_settings
--------------------------------
Header
Footer
ShowCustomer
ShowCashier
LogoBehavior
TemplateVersion
AutoPrintDefault
```

Local workstation/terminal configuration owns:

```text
PrinterName
PaperSize
DeviceSpecificOptions
```

This prevents future multi-terminal installations from forcing every counter to share one USB printer configuration.

---

# 51. Historical Receipt Snapshot - Added

At Sale completion store an immutable receipt-template/shop snapshot containing relevant values such as:

```text
Shop Name
Phone
Address
Header
Footer
ShowCustomer
ShowCashier
TemplateVersion
```

Historical reprint therefore reflects the receipt configuration at original completion time.

A later Shop/Receipt settings edit does not rewrite the old receipt.

## 51.1 Durable Post-Commit Print Delivery

Printing is a delivery concern, never business-state authority.

The canonical order is:

```text
Authoritative business transaction
-> persist required ORIGINAL auto-print request in the same PostgreSQL transaction
-> COMMIT
-> durable print request becomes eligible
-> printer dispatch
-> print outcome recorded
```

For an artifact that must automatically exist because a business transaction committed, the durable ORIGINAL print request is persisted atomically with the source business state. This closes the crash gap between "business committed" and "print request recorded".

No printer/device I/O occurs inside that transaction. The durable request is not dispatched until the enclosing transaction commits successfully. If the source transaction rolls back, its uncommitted ORIGINAL print request rolls back with it.

A later operator-requested REPRINT may create a new durable request after the original transaction because the underlying business identity already exists.

A receipt, Purchase document, TrackingCode label, or QR label must never be required to physically print successfully in order for an already committed Sale, Purchase, Inventory receipt, Warranty replacement, Supplier Khata effect, or Cash effect to remain valid.

A durable print request conceptually records at least:

```text
PrintRequestId
ClientOperationId / request idempotency identity
LogicalArtifactKey
ArtifactType
SourceType
SourceId
ArtifactPartIndex nullable
TerminalId / PrinterTarget where applicable
PayloadSnapshot or immutable SnapshotReference
RequestedBy
RequestReason ORIGINAL | REPRINT | RECOVERY_RETRY
Status
AttemptCount
CreatedAt
LastAttemptAt nullable
CompletedAt nullable
LastErrorCode nullable
LastErrorSummary nullable
OriginalPrintRequestId nullable
Version
```

Supported status semantics:

```text
PENDING
DISPATCHING
PRINTED
FAILED_RETRYABLE
OUTCOME_UNKNOWN
ACTION_REQUIRED
CANCELLED
```

Rules:

- `PENDING` means durable business data already exists and the artifact has not yet been dispatched.
- `DISPATCHING` is a delivery state only; it owns no stock/financial truth.
- `PRINTED` means the application received sufficient printer/driver success evidence for that attempt.
- `FAILED_RETRYABLE` is used only when the system knows the print did not complete and retry is safe.
- `OUTCOME_UNKNOWN` is used when bytes may have reached the printer/device but success cannot be proven, such as connection loss after dispatch.
- `ACTION_REQUIRED` requires explicit operator resolution/reprint decision.
- `CANCELLED` cancels delivery only; it never cancels or reverses the source Sale/Purchase/Inventory/Warranty transaction.

ORIGINAL auto-print request creation is idempotent. A stable logical artifact identity prevents the same source artifact/part from creating duplicate ORIGINAL requests during command replay.

A REPRINT is a new delivery request linked to the same committed artifact. Its request identity is independently idempotent so double-click/retry cannot create multiple reprint requests accidentally.

For multi-label receiving, `ArtifactPartIndex` or equivalent stable unit identity distinguishes each required committed label while preserving one business TrackingCode per InventoryUnit.

The system must not claim exactly-once physical printing where the printer protocol cannot prove it.

Automatic retry is allowed only for failures classified as safely retryable. An `OUTCOME_UNKNOWN` TrackingCode label or receipt must not be blindly auto-reprinted because the first physical output may already exist.

## 51.2 Reprint and Identity Safety

Reprint uses the original committed artifact identity and immutable business snapshot/reference.

Receipt reprint:
- uses the historical receipt/shop/template snapshot from the original completed Sale;
- does not recalculate historical prices, discounts, payment, or tax-like values from mutable current settings;
- does not create a new Sale or Payment.

Purchase-document reprint:
- reads the committed Purchase document identity/state;
- does not alter Purchase payable, inventory, cost, cash, or Supplier Khata.

TrackingCode/QR label reprint:
- uses the existing committed `InventoryUnitId`, `ItemSequence`, and `TrackingCode`;
- must never reserve a new sequence;
- must never create a second InventoryUnit;
- must never mutate stock or carrying cost;
- should require explicit operator action when the prior print outcome is unknown.

A reprint request is separately auditable and may link to the original print request. Multiple physical prints may exist, but there remains exactly one underlying committed business identity.

For tracked physical items, operators must not attach duplicate copies of one TrackingCode label to different physical units. The UI/operational workflow must clearly distinguish "reprint same identity" from "create/receive replacement identity".

## 51.3 Print Payload Integrity and Recovery

The durable payload must be reproducible without reopening the source business transaction.

For historical financial documents, prefer an immutable rendered-data snapshot or immutable source snapshot sufficient to reproduce the original document.

For TrackingCode labels, the payload/reference must resolve the committed identity fields needed for printing, including the original TrackingCode and relevant Product/Supplier display snapshots.

A print worker/app restart scans durable requests in unfinished states and resumes only according to status safety:

```text
PENDING -> dispatch allowed
FAILED_RETRYABLE -> bounded retry allowed
DISPATCHING after crash -> OUTCOME_UNKNOWN unless device protocol proves non-delivery
OUTCOME_UNKNOWN -> operator/action-required resolution
ACTION_REQUIRED -> no automatic duplicate dispatch
PRINTED/CANCELLED -> terminal delivery states
```

Print retry/backoff occurs outside authoritative business transactions and outside SupplierProduct/Inventory/Cash locks.

Print diagnostics may retain safe technical error codes and attempt metadata but must not expose credentials or unnecessary customer-sensitive content.

---

# 52. Identity and Permissions

Roles remain:

```text
OWNER
MANAGER
CASHIER
```

Backend permissions remain granular.

Profit/cost data is excluded from unauthorized read DTOs.

No Manager PIN override exists in V1.

At least one active Owner must always remain.

Permission changes invalidate stale session authority through SecurityVersion.

---

# 53. Final Permission Defaults for Newly Resolved Actions

```text
Sale Return
Owner: Yes
Manager: Yes
Cashier: Optional, default No

Purchase Return
Owner: Yes
Manager: Yes
Cashier: No

Purchase Void
Owner: Yes
Manager: Optional
Cashier: No

Thaka Settlement
Owner: Yes
Manager: Optional
Cashier: No

Reopen Thaka
Owner: Yes
Manager: No
Cashier: No

Reverse Thaka Material
Owner: Yes
Manager: Optional
Cashier: No

Reverse Thaka Payment
Owner: Yes
Manager: Optional
Cashier: No

Expense Void
Owner: Yes
Manager: Optional
Cashier: No

License Import
Owner: Yes
Manager: No
Cashier: No

Backup Restore
Owner: Yes
Manager: No
Cashier: No
```

Permissions never bypass business invariants.

---

# 54. Owner PIN Recovery - Resolved Without Backdoor

There is no hidden master PIN.

Recovery uses a one-time signed Recovery Authorization.

```text
Support Recovery Tool
v
Signed Recovery Authorization
v
Bound to LicenseId + DeviceId + Action + Expiry + Nonce
v
Edge Retails verifies embedded Recovery public key
v
Owner PIN reset allowed
v
Authorization consumed
v
Audit
```

Recovery signing uses a separate key from normal license signing.

---

# 55. Security Key Separation

Separate trust domains:

```text
License Signing Key
Update Signing Key
Recovery Authorization Signing Key
Backup Encryption Keys
```

No single private key controls every security function.

Private signing keys never ship with the customer application.



# 56. Backup Encryption and Cross-Machine Recovery - Resolved

Local Windows protection alone is not sufficient for disaster recovery onto a replacement PC.

Final model:

```text
BackupMasterKey (BMK)
-> random high-entropy secret

Local BMK copy
-> protected using Windows OS-backed protection

Backup Recovery Key (RK)
-> separately exported/stored by Owner
```

Each backup contains metadata such as:

```text
EncryptionVersion
KeyVersion
WrappedBackupMasterKey
Checksum
Manifest
```

The Recovery Key can unwrap/recover the Backup Master Key on a replacement machine.

The Recovery Key is never stored next to the cloud backup by default.

---

# 57. Backup Pipeline - Final

```text
Schedule / Backup Now
v
Acquire Backup Job Lock
v
Database Health Check
v
PostgreSQL Logical Backup
v
Manifest
v
Integrity Check
v
Compression
v
Authenticated Encryption
v
Protected Local Backup
v
Cloud Upload
v
Remote Verification
v
Retention
```

A file merely existing does not mean backup succeeded.

Cloud failure does not block POS.

Pending encrypted local backup is retried later.

---

# 58. Restore Architecture - Final

Restore remains Owner-only.

```text
Select Backup
v
Validate Manifest / Version
v
Validate Checksum / Authentication
v
Decrypt
v
Compatibility Check
v
Pre-Restore Safety Backup
v
Maintenance Mode
v
Block Business Writes
v
Restore into Staging Database
v
Integrity Checks
v
Controlled Database Swap
v
Migrations if supported/required
v
Post-Restore Reconciliation
v
Normal Mode
```

If post-restore verification fails:

```text
SystemState = RECOVERY_REQUIRED
```

Normal POS writes remain blocked.

---

# 59. Restore Operation Journal - Resolved

Restore progress must not depend solely on the database being replaced.

A protected local `RestoreOperationJournal` exists outside the target database.

Example states:

```text
VALIDATING
SAFETY_BACKUP
STAGING
SWAPPING
VERIFYING
COMPLETED
RECOVERY_REQUIRED
```

After power loss/crash, startup can detect an interrupted restore and continue/recover deterministically.

---

# 60. License Model

License authority is the signed license file.

Safe cached DB metadata is not cryptographic authority.

License states:

```text
NOT_INSTALLED
VALID
EXPIRED
INVALID_SIGNATURE
DEVICE_MISMATCH
MODULE_NOT_LICENSED
MALFORMED
```

Only VALID permits normal business writes.

Invalid-license mode may still allow safe:

```text
License Import
Database Diagnostics
Backup Visibility
Support Bundle
```

---

# 61. System State Guard - Added

System states:

```text
NORMAL
MAINTENANCE
RECOVERY_REQUIRED
```

Business commands require:

```text
SystemState = NORMAL
LicenseStatus = VALID
```

Recovery/diagnostic commands have explicit controlled exemptions.

A caller cannot bypass maintenance/recovery mode merely by invoking a handler directly.

## 61.1 Production PostgreSQL Durability Contract

Authoritative Edge Retails business commits require crash-safe PostgreSQL durability.

Production requirements:

```text
fsync = on
full_page_writes = on
authoritative business transactions must not use synchronous_commit = off
```

These requirements apply to Sales, Purchases, Inventory, Costing, Thaka, Supplier Khata, Warranty, Cash, Audit, Identity, Licensing state, and other authoritative business records.

Authoritative business tables must not use UNLOGGED semantics.

`wal_sync_method` is not hard-coded to one value by the architecture. PostgreSQL may use the safe platform-supported/default method validated for the deployed operating system and storage stack. The installer/application must not choose a method that bypasses required durable WAL flush behavior.

New production clusters must enable PostgreSQL data checksums when supported by the selected production PostgreSQL version. An existing cluster that does not have checksums enabled requires a controlled maintenance procedure appropriate to that PostgreSQL version; the running business application must not silently toggle cluster checksum state.

The installer, updater, support tooling, or performance tuning must never silently weaken the approved production durability settings.

A UPS is strongly recommended for shop hardware, but correctness must not depend on a UPS being present.

## 61.2 Startup Database Write-Safety Gate

Before the application permits `SystemState = NORMAL` business writes, startup/health checks must establish at least:

```text
PostgreSQL reachable
configured primary is writable
database is not still in recovery/read-only mode
schema version is application-compatible
no interrupted RestoreOperationJournal requires recovery
required durability settings are not weakened
production checksum policy is satisfied
critical disk-free-space floor is not breached
no known unresolved database-integrity failure exists
```

If integrity/recovery truth is uncertain, normal business writes remain blocked and the system enters or remains in the appropriate controlled maintenance/recovery path.

A stale backup, cloud outage, or printer outage may produce DEGRADED/ACTION_REQUIRED health but does not by itself imply database corruption or roll back committed business transactions.

Do not run a full offline `pg_checksums` scan on every normal application startup. Deep checksum/integrity verification belongs to explicit diagnostics/maintenance workflows. Startup uses lightweight readiness/configuration checks and previously recorded failure/recovery evidence.

---

# 62. Final Command Pipeline - Corrected

```text
IApplicationGateway
v
Operation Context / Correlation
v
System State Guard
v
License Guard
v
Session / Authentication
v
Authorization
v
Input Validation
v
Idempotency
v
Transaction Boundary
v
Command Handler
v
Domain Rules
v
Business Audit
v
SaveChanges
v
COMMIT
v
Post-Commit Action
v
Result<T>
```

Printing, cloud operations, and other noncritical external work never occur before the business commit.

---

# 63. Application Gateway - Final

ViewModels depend on:

```text
IApplicationGateway
```

Standalone V1:

```text
ViewModel
v
LocalApplicationGateway
v
Command / Query Dispatchers
```

Future LAN:

```text
ViewModel
v
RemoteApplicationGateway
v
ASP.NET Core
```

This resolves the earlier dispatcher-vs-gateway design difference.

ViewModels do not need LAN-specific branches.

---

# 64. DbContext and Transaction Lifetime

V1 uses one `EdgeRetailsDbContext` for the modular monolith.

A fresh DbContext scope is created per business operation/job.

Never keep one DbContext alive for the entire WPF process.

Cross-module atomic commands can therefore share one PostgreSQL transaction.

---

# 65. Write and Read Separation

Write side:

```text
Application Commands
Aggregate-specific Repositories
EF Core
PostgreSQL transaction
```

Read side:

```text
Queries
Dapper
Screen-shaped DTOs
```

No generic repository is the primary business abstraction.

No EF entities are bound directly to WPF grids.

---

# 66. Idempotent Commands

The following command families require `ClientOperationId`:

```text
Complete Sale
Sale Return
Create Purchase
Purchase Return
Purchase Void
Add Thaka Material
Reverse Thaka Material
Record Thaka Payment
Reverse Thaka Payment
Final Settlement
Stock Adjustment
```

A retry must resolve to the original outcome rather than duplicate financial/stock state.

---

# 67. Concurrency

Use optimistic concurrency through explicit `Version bigint` for mutable master/settings records.

Use PostgreSQL row locking for consistency-critical transactions.

Examples:

```text
Complete Sale
-> lock relevant StockBalance rows

Serialized Sale
-> lock InventoryUnit

Thaka Payment / Settlement / Reversal
-> lock ThakaProject

Purchase Return / Void
-> lock purchase-origin stock state
```

When multiple products are locked, ProductIds are sorted deterministically before lock acquisition.

Negative stock and double-sale states are never allowed.

---

# 68. Settings Storage - Corrected

Avoid a giant untyped settings blob.

Typed settings include:

```text
system.shop_profile
system.receipt_template_settings
system.backup_settings
system.license_state
identity.user_preferences
system.operational_events
```

Local machine/terminal configuration owns:

```text
PrinterName
PaperSize
Device-specific UI/hardware settings
```

Appearance is a user/UI preference:

```text
LIGHT
DARK
SYSTEM
```

Database screen remains diagnostics-only.

Raw database credentials are never editable from normal Settings.

---

# 69. Business Audit vs Technical Logs

Business Audit answers:

```text
Who did what to business state?
```

Serilog answers:

```text
What technically happened?
```

Operational Events answer:

```text
What significant system event occurred?
```

These remain separate stores/concepts.

Audit is append-only in normal application behavior.

Secrets never enter audit or logs.

---

# 70. System Health and Diagnostics

Unified health model includes:

```text
AppVersion
SchemaVersion
LicenseStatus
DatabaseStatus
DatabaseLatency
DatabaseSize
WorkerStatus
WorkerVersion
WorkerHeartbeat
BackupStatus
CloudBackupStatus
DiskFreeSpace
PrinterStatus optional
PrintDeliveryStatus
PendingPrintRequestCount
ActionRequiredPrintRequestCount
SystemState
```

Health levels:

```text
HEALTHY
DEGRADED
UNAVAILABLE
ACTION_REQUIRED
```

Cloud/printer failure is normally non-blocking.

A print request in OUTCOME_UNKNOWN or ACTION_REQUIRED may degrade operational health and require operator attention, but it does not reverse or invalidate its committed source business transaction.

Database/schema/recovery-integrity failure is blocking.

## 70.1 Database Durability and Recovery Health Evidence

The health model must be able to expose or derive the following PostgreSQL safety evidence:

```text
PrimaryWritable
PgIsInRecovery
TransactionReadOnly
FsyncEnabled
FullPageWritesEnabled
SynchronousCommitPolicy
DataChecksumsEnabled
SchemaCompatibility
CriticalDiskFloorStatus
InterruptedRestoreStatus
KnownIntegrityFailureStatus
```

The health check verifies the approved production configuration without modifying it.

If `fsync` or `full_page_writes` is disabled, or authoritative business transactions can report success with `synchronous_commit = off`, production business writes are not considered healthy.

Checksum status detects whether page-checksum protection is enabled; it does not prove that all database content is semantically correct. A detected checksum/integrity failure is blocking and requires diagnostics/recovery rather than silent repair.

Deep database verification may include explicit PostgreSQL-supported checksum/integrity tools during a controlled diagnostics or maintenance workflow. It must not be implemented as an expensive full-cluster scan on every routine startup.

Disk-free-space policy has at least two operational thresholds:
- warning threshold -> DEGRADED/ACTION_REQUIRED;
- critical write-safety threshold -> block new authoritative writes until remediated.

Threshold values are deployment configuration, not hard-coded business constants, but they must be validated during installation/release testing.

---

# 71. Reconciliation - Final

Read-only deep integrity checks include:

```text
Movement-derived stock
vs
StockBalance

Serialized current-state counts
vs
serialized stock

CostState
vs
inventory quantity/cost

Sale returns
vs
original sale quantities/values

Thaka financial balance
vs
issues/reversals/payments/discount adjustments
```

Reconciliation detects and reports.

It does not silently rewrite financial history.

---

# 72. Support Bundle

Owner can generate a redacted diagnostic support bundle containing:

```text
App / Worker / Schema versions
Database health
License status summary
Backup status
Disk health
Recent technical logs
Recent job failures
Reconciliation summary
Non-secret configuration metadata
```

It excludes:

```text
PIN
PIN hashes
DB password
connection secrets
backup encryption keys
private signing keys
full database dump by default
```

Support diagnostics work offline.

---

# 73. Security Boundary - Final

Daily POS runs without Windows Administrator privileges.

PostgreSQL V1 is loopback-only.

EdgeRetails.exe never uses the PostgreSQL superuser.

DB credentials are installer-generated and stored using Windows OS-backed protection.

SQL is parameterized.

Dynamic sort/column identifiers use a whitelist.

License, update, recovery authorization, and backup cryptography use separate keys/trust domains.

A Windows Administrator/full physical compromise is outside any promise of absolute local secrecy.

BitLocker/full-disk encryption is strongly recommended for production PCs.

---

# 74. Update Security and Lifecycle

Update package must pass:

```text
Signature / authenticity validation
Hash validation
Version policy
Compatibility validation
```

Then:

```text
Notify Owner
v
Safe maintenance point
v
Pre-update backup
v
Install
v
Migrate
v
Verify
v
Restart
```

Silent downgrade is rejected by default.

---

# 75. PostgreSQL Engine Major Upgrade - Added

Application schema migration and PostgreSQL major-version upgrade are separate operations.

A PostgreSQL major upgrade requires:

```text
Verified Backup
Maintenance Mode
Tested pg_upgrade or dump/restore route
Post-upgrade health check
Schema compatibility check
Reconciliation
Application startup verification
```

The installer must never silently replace a PostgreSQL major version.

---

# 76. Performance Baseline

Hot paths:

```text
Barcode lookup
POS product search
Complete Sale
Sales/Purchase history
Inventory movement/history
Physical Item history
Supplier Khata/history
Warranty queues/history
Audit history
Dashboard
Daily/Monthly/Yearly Reports
```

Use targeted PostgreSQL indexes.

History uses server-side filtering, sorting, projection, and pagination.

Deep history uses keyset/seek pagination where appropriate.

Reports aggregate inside PostgreSQL and return small DTO/series sets.

No mandatory partitioning, materialized views, or reporting summary tables exist in V1.

They may be introduced only after measured need.

## 76.1 Bounded Read-Query Contract

Potentially large collection queries must never require loading the full table/ledger into application memory before filtering or paging.

Applies at minimum to:

```text
Products/catalog search
Sales History
Purchase History
Inventory Movement History
InventoryUnit / physical-item collections
Supplier Khata / payments / refunds
Warranty claims/cases/queues/history
Audit history
Expenses/history
Thaka histories
POS Draft history
```

Collection-query requests define, as applicable:

```text
filters
deterministic sort
PageSize
continuation/keyset cursor OR bounded shallow page/offset
optional total-count request
CancellationToken / cancellation propagation
```

Rules:
- `PageSize` is positive and capped by an application/deployment maximum; clients cannot request an unbounded page.
- database filtering/sorting occurs before materialization;
- read models project only fields required by the screen/report rather than hydrating large write aggregates;
- list/detail separation prevents every list row from loading complete child collections/event history;
- N+1 per-row database query patterns are not acceptable on hot list/history screens;
- expensive exact total counts are optional where the UX can operate with continuation/has-more semantics;
- cancelled/stale searches must stop propagatable database/read work where supported rather than updating the UI after a newer search has won.

## 76.2 Pagination Semantics

Shallow bounded paging may use OFFSET/LIMIT where it remains measured and appropriate.

Deep or continuously growing history must prefer keyset/seek pagination when stable ordering keys exist.

Keyset order must be deterministic and include a unique tie-breaker, for example:

```text
OccurredAt DESC, Id DESC
CompletedAt DESC, Id DESC
PaidAt DESC, Id DESC
```

The continuation cursor represents the last returned sort key(s) plus unique tie-breaker. It must not rely on UI row number.

Page traversal must not:
- duplicate rows because several records share the same timestamp;
- skip rows because a non-unique sort key was used alone;
- silently change sort semantics between pages.

A broad rule of "all history must use OFFSET/LIMIT" is explicitly rejected. Pagination strategy is chosen by dataset shape and measured query plan while preserving deterministic results.

## 76.3 WPF Large-Collection Rendering Contract

WPF screens that can display large collections must use UI virtualization/recycling or equivalent bounded rendering behavior.

For DataGrid/ListBox/ItemsControl-style surfaces:
- row/item virtualization remains enabled unless a measured, documented reason requires otherwise;
- container recycling should be used where compatible;
- paging/incremental loading feeds a bounded visible collection/window;
- do not materialize an entire large database history into one ever-growing `ObservableCollection`;
- sorting/filtering of authoritative large datasets stays server-side rather than re-sorting millions of rows in the UI process;
- search-as-you-type should use a short debounce/throttle appropriate to the UX and cancel superseded reads;
- query execution and result materialization must not block the WPF UI thread;
- page transitions/search refreshes must avoid duplicate concurrent loads for the same logical request.

UI virtualization is an implementation/performance requirement. It does not change database authority or permit client-side filtering to replace authoritative backend filters.

## 76.4 Performance Evidence and Representative Dataset

Performance acceptance uses repeatable evidence on representative shop hardware/database state, not an architecture-wide arbitrary millisecond promise.

A baseline performance dataset should include at least the order of magnitude below, or a larger measured equivalent:

```text
10,000 Products
100,000 InventoryUnits
250,000 Sales
500,000 SaleItems
500,000 InventoryMovements
250,000 SupplierAccountEntries
representative Purchase/Warranty/Audit/Thaka history
```

These are benchmark fixtures, not hard V1 capacity ceilings.

For critical read paths capture, where practical:

```text
p50 / p95 / p99 application latency
database execution time
rows returned
representative rows scanned / query-plan evidence
memory allocated / working-set behavior for large WPF lists
cancellation/superseded-search behavior
```

Representative PostgreSQL query plans should be inspected with appropriate EXPLAIN tooling in test/staging/diagnostics, not by adding expensive plan instrumentation to every production request.

A performance regression is not solved by adding indexes blindly. Confirm query shape, selectivity, ordering, projection, pagination, and measured plan before adding/retaining an index.

---

# 77. Critical Index Families

Examples:

```text
Product SKU unique
Barcode unique
Sale InvoiceNumber unique
Sale CompletedAt
Sale Customer + CompletedAt
Purchase Supplier + Date
Inventory Movement Product + OccurredAt
Inventory Movement ReferenceType + ReferenceId
Inventory Unit Serial / IMEI partial unique
Inventory Unit Product + Status
Inventory Lot Product + ReceivedAt where remaining > 0
Thaka Project Status + StartDate
Thaka Customer + Status
Expense Date / Category / Status
Audit Entity + OccurredAt
```

Do not index every column.

---

# 78. Testing and Release Gates

Testing layers:

```text
Unit Tests
PostgreSQL Integration Tests
Architecture Tests
Migration Tests
Concurrency Tests
Backup/Restore Tests
Critical Workflow Tests
Manual Desktop QA
```

Integration tests use real PostgreSQL semantics rather than SQLite as the authoritative substitute.

Critical release gates:

```text
Release Build
Unit Tests
Integration Tests
Architecture Dependency Tests
Fresh Migration Tests
Upgrade Migration Tests
Atomicity / Rollback Tests
Idempotency Tests
Concurrency Tests
Golden Financial Report Tests
Backup Restore Drill
Installer Smoke Test
Manual POS Workflow QA
```

A backup is not considered proven until restoration succeeds.

## 78.1 Mandatory Durability / Crash-Recovery Validation

Release validation must include PostgreSQL durability and restart evidence, not only happy-path transaction tests.

Required coverage:

```text
1. authoritative Sale commit survives application restart
2. authoritative Purchase commit survives application restart
3. transaction killed before COMMIT leaves no partial business effect
4. transaction killed immediately after COMMIT is recovered as committed
5. PostgreSQL service restart after committed business transaction preserves committed truth
6. interrupted RestoreOperationJournal keeps normal writes blocked
7. schema mismatch keeps normal writes blocked
8. fsync disabled is detected as unsafe production configuration
9. full_page_writes disabled is detected as unsafe production configuration
10. synchronous_commit = off is rejected for authoritative business commit policy
11. checksum-policy mismatch is surfaced
12. known checksum/integrity failure blocks normal business writes
13. low-disk warning degrades health without corrupting business state
14. critical disk floor blocks new authoritative writes cleanly
15. backup/restore reconciliation succeeds after crash/restart rehearsal
```

Crash tests must use disposable/test PostgreSQL data and must verify final business invariants after restart/recovery.

Process-kill and PostgreSQL-service-restart tests are mandatory automation/integration targets. True abrupt host/power-loss testing belongs in controlled release/installer lab validation where feasible; it must never be performed against production shop data.

The test suite must distinguish:
- committed transaction durability;
- clean rollback of uncommitted work;
- database integrity;
- application-level reconciliation.

Passing unit tests alone is not sufficient evidence for this contract.

## 78.2 Architecture / Code Drift Gate

Architecture alignment is release-enforced, not documentation-only.

The CI/release pipeline must include machine-checkable gates for at least:

```text
EF Model Drift
Migration Drift
Schema Constraint Contract
Enum / State Contract
Permission Contract
Tracking Identity Contract
Deterministic Lock Ordering
Supplier Khata Direction / Source Uniqueness
Warranty Transition / Custody Contract
Navigation / Screen Contract
```

Minimum pipeline order:

```text
dotnet build
-> unit tests
-> architecture tests
-> EF model-drift check
-> migration rehearsal on disposable PostgreSQL
-> PostgreSQL integration tests
-> release build/package validation
```

The exact command/project paths are implementation details, but the required result includes:

- `dotnet ef migrations has-pending-model-changes` or equivalent reports zero pending model drift;
- the checked-in EF migration snapshot matches the compiled model;
- a disposable PostgreSQL database can be migrated from empty to the current baseline successfully;
- where the project policy requires Down-to-zero rehearsal, Down-to-zero then Up also succeeds;
- schema constraints/indexes required by canonical invariants exist in the physical model/migration;
- architecture tests fail if prohibited duplicate tables/engines/authorities are introduced.

Architecture tests must protect canonical invariants such as:

```text
one TrackingCode sequence authority
permanent DealerCode / ProductSKU / ItemSequence semantics
no mutable Supplier balance authority
Supplier Khata EntryType + Direction rules
unique generated Khata source identity
one active exact-unit Warranty claim barrier
Warranty allowed transition/custody matrix
single global lock-order contract
19-screen navigation contract
no duplicate Sale/Purchase/Warranty write engine
no direct terminal PostgreSQL authority in LAN mode
```

A code/schema change that intentionally alters a canonical business invariant requires, in the same change set:

```text
architecture update
+ corresponding architecture/integration regression test update
+ migration/model update where applicable
```

It is not acceptable to merge a business-invariant implementation change and defer the architecture/test update to a later unrelated change.

Generated/read-model/UI differences that do not alter business authority do not require architecture text churn, but they must still respect existing DTO/navigation/performance contracts.

CI may use static/structural tests, reflection/model inspection, SQL/migration inspection, and PostgreSQL integration tests as appropriate. No single testing technique is expected to prove every invariant.

A green compile alone is not architecture-alignment evidence.

---

# 79. Migration Safety

Every application release understands:

```text
ExpectedSchemaVersion
MinimumSupportedSchemaVersion
```

If DB schema is newer than the running app, normal startup is blocked.

Risky/schema-changing update requires pre-migration backup.

Production migration is forward-oriented.

Migration baseline lifecycle is explicit:

```text
PRE_PRODUCTION_SCHEMA_EPOCH
-> clean InitialProductionBaseline regeneration/squash is allowed only while no production business database has been released.

FIRST_PRODUCTION_BASELINE_FREEZE
-> BaselineId + migration history + model snapshot become immutable release history.

POST_PRODUCTION
-> append-only migrations only; already released migration identities are never rewritten, renumbered, or silently regenerated.
```

Each production release records the SchemaEpoch/BaselineId it expects. Backup/restore compatibility validation must preserve enough schema/migration identity to distinguish an older compatible backup from an unknown/future database.

Rollback after a failed migration uses tested backup plus compatible prior application rather than relying blindly on reverse migrations. See Section 235.

---

# 80. Installer / Uninstall Policy

WiX + Burn remains the installer strategy.

Normal uninstall removes application/service components but preserves business database and backups by default.

Data deletion must be an explicit separate destructive action.

No production installer ships development secrets, private signing keys, or test credentials.

---

# 81. Future Multi-Terminal Architecture

Future LAN topology:

```text
POS Terminals
v HTTPS
EdgeRetails.Server (ASP.NET Core)
v
same Application / Domain
v
PostgreSQL
^
EdgeRetails.Worker
```

Terminals never connect directly to PostgreSQL.

Server reuses existing business handlers rather than duplicating domain logic.

Receipt printing remains local to the terminal after central commit.

---

# 82. Multi-Terminal Offline / Reconnect Contract

First LAN release does not permit authoritative offline writes.

A terminal may preserve non-authoritative local UI state such as an unsaved cart, filter state, or draft input buffer, but it must not create a second local business authority.

Terminal connectivity state is modeled explicitly:

```text
CONNECTED
DEGRADED
RECONNECTING
DISCONNECTED
```

Semantics:

- `CONNECTED`: authoritative server round-trips are available.
- `DEGRADED`: reads/heartbeat may be impaired; authoritative writes are allowed only when the server can still confirm the specific command outcome.
- `RECONNECTING`: terminal is restoring authoritative connectivity; no new authoritative write is started.
- `DISCONNECTED`: authoritative server connection is unavailable; business writes are blocked.

The offline-write prohibition applies to every authoritative business mutation, including at minimum:

```text
Sale completion / Sale Return
Purchase / Purchase Void / Purchase Return
Supplier Payment / Refund / Advance / Account Adjustment
Thaka Material / Thaka Payment
Stock Adjustment / Stocktake posting
Warranty claim state mutation
Warranty physical replacement / handover / write-off
Warranty monetary credit
Cash Session authoritative movement
Product/Supplier master mutation that participates in business identity
any other command that changes stock, cost, payable, cash, warranty, audit-required business state, or permanent identity
```

This deliberately avoids split-brain stock, payable, cash, warranty, and identity synchronization.

A disconnected terminal must not:
- issue an authoritative invoice/payment/receipt number locally as if committed;
- create shadow stock or a local Supplier Khata balance;
- allocate TrackingCode/ItemSequence locally;
- post offline Warranty or Cash mutations for later conflict merge;
- invent a local "successful" state that has not been confirmed or idempotently resolved by the server.

## 82.1 Unknown-Outcome Command Rule

A network failure can occur after the server commits but before the terminal receives the response. Therefore "connection lost" does not by itself mean "command failed".

For every replay-safe authoritative command, the terminal preserves the original business request identity, especially the same `ClientOperationId`/correlation identity, until outcome is resolved.

If response outcome is unknown:

```text
do not invent a compensating local write
do not generate a new ClientOperationId for the same intended operation
mark the local command outcome UNKNOWN / RESOLUTION_REQUIRED
restore connectivity
query/replay through the existing idempotent command contract
resolve to the original committed result if it already committed
otherwise re-run the whole command from fresh authoritative state
```

The terminal must never display a second successful Sale/Purchase/Payment merely because the first response was lost.

Hardening 2 retry/idempotency rules remain authoritative. LAN recovery reuses those rules rather than creating a separate retry engine.

## 82.2 Reconnect Revalidation

When connectivity returns, preserved local UI state is not automatically authoritative.

Before submitting a preserved cart/draft/business intent, reload and revalidate all mutable server truth relevant to that command, including where applicable:

```text
Product active state
current ProductUnit / conversion
current authoritative price
current stock / bucket availability
exact InventoryUnit availability/status
current SupplierProduct relationship/sequence authority
current Supplier payable/credit balance
current CashSession state
current Warranty claim/case state and custody
current permissions/session validity
stocktake exclusion / operational locks
server schema/version compatibility
```

If any authoritative value changed while disconnected, the user must receive the current server truth and the command must be refreshed/reconfirmed according to its normal business rules.

A preserved POS cart may survive disconnect, but Sale completion after reconnect still runs through the normal CompleteSale engine and its fresh validation.

## 82.3 Server and Terminal Ownership

The server is the only LAN business-command authority.

Terminal-local storage may contain:
- unsaved cart/draft UI state;
- cache/read models;
- pending UI intent metadata;
- the stable identity needed to resolve an unknown server response.

Terminal-local storage may not contain an independently authoritative stock, payable, cash, warranty, TrackingCode sequence, or completed Sale/Purchase ledger.

Receipt/label printing remains terminal-local delivery after central commit and follows Sections 51.1-51.3.

---

# 83. Multi-Terminal Identity

User identity and Terminal identity are separate.

Future transactions/audit may capture:

```text
UserId
SessionId
TerminalId
CorrelationId
```

Terminal registration is controlled.

LAN terminals do not receive database credentials or PIN hashes.

Server licensing enforces `MaxTerminals`.

---

# 84. Final V1 Full Screen Count

The original navigation contract has been superseded by Section 209.

Exactly 19 full screens are authoritative:

1 First Setup / License
2 Login / User Switch
3 Dashboard
4 POS
5 Sales History
6 Sale Detail
7 Thaka / Projects
8 Thaka Workspace
9 New Purchase
10 Purchase History
11 Product Management
12 Product Detail
13 Inventory
14 Expenses
15 Customers
16 Suppliers
17 Warranty
18 Reports
19 Settings

Product Management and Inventory are separate full management surfaces.
Warranty is a dedicated full operational screen.
Supplier Khata remains inside Suppliers.

---
# 85. Final V1 Scope Boundaries

Included:

```text
Local Sales
Sale Returns
Thaka
Thaka correction reversals
Purchases
Purchase Returns
Controlled Purchase Void
Inventory / Movement Ledger
Serialized Inventory
Stock Adjustments
Expenses
Customers
Suppliers
Supplier Khata / Accounts Payable
Warranty Operational Screen
Reporting
Users / Permissions
Licensing
Database Diagnostics
Backup / Restore
Printing
Audit / Diagnostics
```

Excluded:

```text
FBR / tax authority integration
Mobile app
Web-first product
CRM campaigns
Payroll / attendance
Complex accounting ledger
Multi-branch UI
Marketplace
Distributed offline LAN sync
High-availability cluster
```



# 86. Phase 16 Resolved Issue Register

| # | Issue | Final Resolution | Status |
|---|---|---|---|
| 1 | Opening Stock existed in inventory truth but no Initial Stock UI | Opening Stock is a controlled SET_PHYSICAL_COUNT Stock Adjustment | RESOLVED |
| 2 | Serial and IMEI policies were too generic | Explicit SerialTrackingEnabled / ImeiTrackingEnabled rules added | RESOLVED |
| 3 | Serialized return then resale could not preserve sale history | Historical sale-item-unit assignments with one active assignment | RESOLVED |
| 4 | Serialized Thaka issue had no dedicated state | ISSUED_THAKA status added | RESOLVED |
| 5 | Warranty on returned/resold unit could be overwritten | Warranty moved to per-sale serialized assignment snapshot | RESOLVED |
| 6 | Inventory movement could not represent bucket transfer cleanly | Movement header + movement_effects model adopted | RESOLVED |
| 7 | Damaged/Defective lot cost provenance could be lost | lot_bucket_balances added | RESOLVED |
| 8 | Lost/Scrap inventory had no profit impact | Recognized Inventory Loss concept added | RESOLVED |
| 9 | Invoice discount allocation absent from final SaleItem schema | AllocatedInvoiceDiscount + NetLineTotal added | RESOLVED |
| 10 | Partial-return refund rounding was ambiguous | Remaining-refundable residual algorithm locked | RESOLVED |
| 11 | Complete Sale could imply client price authority | Current Product price is authoritative; ExpectedPrice only detects staleness | RESOLVED |
| 12 | Product Purchase Cost had two meanings | ReferencePurchaseCost separated from CurrentCost | RESOLVED |
| 13 | Stock Adjustment reasons shared ambiguous behavior | Opening/Damaged/Lost/Physical Count/Other semantics separated | RESOLVED |
| 14 | Positive serialized adjustments lacked identity/cost | Exact unit identity + acquisition cost required | RESOLVED |
| 15 | Thaka Material mistakes lacked correction workflow | Immutable Material Reversal promoted to V1 | RESOLVED |
| 16 | Thaka Payment mistakes lacked correction workflow | Immutable Payment Reversal promoted to V1 | RESOLVED |
| 17 | Reopened settlement discount semantics unclear | Settlement cycles + immutable adjustment/reversal model adopted | RESOLVED |
| 18 | Mistaken Purchase entry could only be represented as supplier return | Controlled Purchase Void added | RESOLVED |
| 19 | Purchase Detail Print had no backend path | PurchaseDocument query + printer pipeline added | RESOLVED |
| 20 | Global printer setting conflicts with future LAN | Receipt template and terminal printer settings split | RESOLVED |
| 21 | Reprint could change after Shop/Receipt settings edit | Receipt template/shop snapshot stored at Sale completion | RESOLVED |
| 22 | Owner PIN recovery had no safe design | One-time signed Recovery Authorization added | RESOLVED |
| 23 | DPAPI-only backup key could not restore onto replacement PC | Portable Owner Recovery Key + wrapped BMK design adopted | RESOLVED |
| 24 | Cryptographic trust domains could be accidentally reused | License/Update/Recovery/Backup keys explicitly separated | RESOLVED |
| 25 | Business command pipeline lacked explicit license/system-state guard | Guards added before authorization/transaction | RESOLVED |
| 26 | Phase 9 dispatcher and Phase 15 gateway designs differed | IApplicationGateway is ViewModel boundary from day one | RESOLVED |
| 27 | Early separate module-project proposal conflicted with actual V1 repo | Five-project solution retained; modules logical in V1 | RESOLVED |
| 28 | Settings risked becoming an untyped settings blob | Typed settings tables/config ownership adopted | RESOLVED |
| 29 | Restore state could disappear with DB replacement | External protected RestoreOperationJournal added | RESOLVED |
| 30 | Dashboard financial KPI meanings were ambiguous | Exact formulas locked | RESOLVED |
| 31 | Customer Local Sales definition ambiguous | Lifetime Net Local Sales excluding Thaka | RESOLVED |
| 32 | Supplier Purchases definition ambiguous | Lifetime Net Purchase Spend after returns/voids | RESOLVED |
| 33 | Report Total Sales/Profit/Purchases semantics ambiguous | Final financial definitions locked | RESOLVED |
| 34 | Old Yearly minimal-report requirement conflicted with later user request | Later enhanced Reports requirement explicitly supersedes it | RESOLVED |
| 35 | POS visual could imply early invoice reservation | Final InvoiceNumber generated only in Complete Sale transaction | RESOLVED |
| 36 | SKU / Barcode frontend wording could become one DB field | SKU and Barcodes remain separate backend identities | RESOLVED |
| 37 | Newly added correction actions lacked permission defaults | Final permission defaults locked | RESOLVED |
| 38 | PostgreSQL engine upgrade was conflated with EF migration | Separate major-version upgrade policy added | RESOLVED |

Result:

```text
Critical unresolved architecture issues: 0
High-priority unresolved architecture issues: 0
```

---

# 87. Authoritative Database Amendments

The physical database plan from Phase 8 is amended as follows.

Add or strengthen:

```text
catalog.products
  serial_tracking_enabled
  imei_tracking_enabled
  default_warranty_months
  reference_purchase_cost

inventory.movement_effects

inventory.lot_bucket_balances

inventory.units
  final status set includes ISSUED_THAKA
  no permanent single SaleItemId authority

sales.sale_items
  gross_line_total
  allocated_invoice_discount
  net_line_total
  total_cost_snapshot

sales.sale_item_units
  historical assignment identity
  warranty snapshots
  returned_at
  is_active

thaka.material_reversals
thaka.material_reversal_items
thaka.payment_reversals
thaka.settlement_adjustments / equivalent immutable adjustment linkage

purchasing.purchases
  void status/metadata supported

system.backup_settings
system.receipt_template_settings
system.print_requests
  durable ORIGINAL / REPRINT / RECOVERY_RETRY delivery requests
  stable logical artifact identity for ORIGINAL idempotency
  source/artifact identity + immutable payload snapshot/reference
  delivery status/attempt/error metadata
  original-request linkage for reprints
identity.user_preferences
system.operational_events
```

Exact naming may be adjusted during migration implementation, but these concepts are mandatory.

---

# 88. Non-Blocking Implementation Choices

The following are intentionally not architecture blockers and may be selected during implementation after package/provider evaluation:

```text
Exact cloud backup provider
Exact WPF chart library
Exact receipt-printing library / driver adapter
Exact code-signing certificate provider
Exact authenticated-encryption library/API implementation
Exact backup schedule/retention defaults
Exact log retention duration
Exact UI text for newly added correction overlays
Exact future LAN HTTPS certificate provisioning strategy
```

These choices must conform to the rules in this architecture report.

They do not redefine business truth or transaction boundaries.

---

# 89. Implementation Entry Point

Architecture design is complete.

Implementation should now proceed in controlled order:

```text
1. Final database migrations/schema foundation
2. Domain primitives and aggregates
3. Application command/query infrastructure
4. Identity / setup / authorization
5. Catalog + Inventory foundation
6. Purchasing + Costing
7. Sales + Returns + Printing
8. Thaka + reversals + settlements
9. Expenses + contacts
10. Dashboard + Reports
11. Backup / License / Diagnostics
12. UI service migration from Demo state to IApplicationGateway
13. Full integration/concurrency/recovery testing
14. Installer/release hardening
```

Each module is implemented against the locked contracts in this document.

No developer should invent new stock, profit, correction, permission, or settlement rules inside implementation code.

Any product change must amend the architecture explicitly first.

---

# 90. Historical Phase 1-16 Architecture Milestone

Historical Phase 1-16 snapshot (not the current declaration):

```text
Frontend Contract        FINAL
Technology Stack         FINAL
Module Boundaries        FINAL
Domain Model             FINAL
Inventory Model          FINAL
Costing                  FINAL
Sales / Returns          FINAL
Purchasing               FINAL
Thaka                    FINAL
Expenses                 FINAL
Reporting Formulas       FINAL
Identity / Permissions   FINAL
PostgreSQL Architecture  FINAL
Reliability / Recovery   FINAL
Backup                   FINAL
Licensing                FINAL
Security                 FINAL
Diagnostics              FINAL
Performance Strategy     FINAL
Testing / Deployment     FINAL
Future LAN Path          FINAL
Gap Audit                PASSED
```

**Historical milestone:** Phase 1-16 was treated as the implementation baseline at that time. Current authority is governed by Section 0, Section 228, and Section 233.

Future work starts with implementation planning and execution, not further architecture invention, unless the product requirements themselves change.



---

# 91. FORENSIC VERIFICATION ADDENDUM - Frontend <-> Backend <-> Database

A second forensic cross-audit was performed after the Phase 16 declaration.

It compared:

```text
Frontend master specification
Current WPF implementation
Final backend architecture
Planned PostgreSQL relationships
```

The following rules supersede any earlier ambiguity.

The detailed verification record is:

```text
docs/Edge_Retails_Forensic_Architecture_Verification_v1.md
```

---

# 92. POS and Thaka Separation - Reconfirmed

The production POS screen is Local Sale only.

Any current demo `PosSaleMode.ThakaMaterialIssue`, Thaka mode switch, or Thaka issue action inside POS is implementation drift and must be removed.

Final paths:

```text
POS
-> Local Sale

Thaka Workspace
-> Add Thaka Material
```

No shared transaction mode is permitted.

---

# 93. Serialized Transaction UI Contract - Added

SERIALIZED products require exact-unit selection/capture at every relevant business boundary.

Mandatory UI paths:

```text
POS Sale
-> Select Serialized Units

New Purchase
-> Capture Received Serialized Units

Sale Return
-> Select exact sold units

Purchase Return
-> Select exact eligible purchase-origin units

Thaka Material Issue
-> Select exact IN_STOCK units

Thaka Material Reversal
-> Select exact issued units

Stock Adjustment
-> Select/capture exact affected units
```

For serialized products, quantity must equal selected/captured unit count.

Free-form quantity alone is not sufficient.

---

# 94. Serialized PostgreSQL Relationship Set - Added

Mandatory exact-unit link tables:

```text
inventory.movement_units

sales.sale_item_units
sales.return_item_units

purchasing.return_item_units

thaka.material_issue_item_units
thaka.material_reversal_item_units

inventory.stock_adjustment_item_units
```

`inventory.movement_units` stores the canonical inventory-event trace:

```text
movement_id
inventory_unit_id
from_status
to_status
```

Business-specific link tables preserve document provenance.

---

# 95. Inventory Lot Sources - Corrected

`inventory.lots` must support every cost-bearing inbound source, not only PurchaseItem.

Required concept:

```text
id
product_id
source_movement_id
purchase_item_id nullable
received_quantity
original_unit_cost
effective_unit_cost
created_at
```

Inbound cost-bearing sources include:

```text
PURCHASE_IN
OPENING_STOCK
POSITIVE_ADJUSTMENT
customer-return restoration to original provenance
```

Where possible, customer return restores original consumption allocation/lot origin.

---

# 96. Moving Average Cost Pool - Clarified

For QUANTITY/LENGTH products:

```text
Moving Weighted Average
=
authoritative accounting COGS policy
```

Inventory lot costs remain provenance and supplier-return trace.

`ProductCostState` covers owned inventory that still carries value.

Condition transfer:

```text
SELLABLE <-> DAMAGED / DEFECTIVE
```

does not itself create COGS or inventory loss.

Sale / Thaka issue removes cost at MWA.

Customer return re-enters the original Sale cost snapshot.

Lost/Scrap write-off removes carrying cost and records RecognizedInventoryLoss.

Purchase Return removes its locked inventory cost amount and recalculates the average.

Lot original cost and MWA must not be treated as the same concept.

---

# 97. Purchase Return Dual Values - Clarified

Purchase Return preserves two distinct values:

```text
SupplierReturnValue
-> commercial supplier-facing return amount

InventoryCostRemoved
-> carrying cost removed from inventory
```

A single monetary field must not represent both concepts.

---

# 98. Sale Return Refund and Disposition Rules - Corrected

V1 has no Customer Store Credit ledger.

Therefore production refund methods are:

```text
CASH
BANK
OTHER
```

The current demo Store Credit option must be removed.

Return Reason and Item Condition/Disposition are independent:

```text
sales.returns
-> reason_code / reason_note

sales.return_items
-> disposition
```

A return reason must never automatically dictate the inventory bucket without explicit disposition.

---

# 99. Setup, Brand and Operational Visibility - Added

## Setup State

Add:

```text
system.installation_state
```

First Setup DB bootstrap is atomic and marks COMPLETE only after Shop, Owner, Walk-in Customer, roles/permissions and required defaults commit successfully.

## Brand Management

`catalog.brands` remains normalized master data.

The existing Settings > Categories & Units section gains a compact Brands subsection with Add/Edit/Deactivate.

No new full screen.

## Thaka History Visibility

Thaka Workspace query exposes financial activity for:

```text
Payments
Payment Reversals
Settlements
Settlement Discounts
Settlement Adjustment Reversals
Reopenings
```

## Serialized Product Visibility

Product Detail exposes a conditional View Serialized Units section/drawer showing unit identity, status, condition, source and relevant warranty/business reference.

---

# 100. License Hardware Replacement - Clarified

A DEVICE_MISMATCH cannot be fixed locally by editing database state.

Replacement hardware requires a newly signed license bound to the replacement device, or a separately signed vendor reactivation artifact if such a support workflow is implemented.

There is no local unsigned rebind path.

---

# 101. Historical Physical Implementation Status - Phase 16 Snapshot

This section records a historical forensic snapshot only. It is not current workspace status and must not be used as evidence of present implementation state.

At that historical snapshot:

```text
database folder
-> empty

Domain / Application / Infrastructure backend folders
-> scaffolding

EF Core / Npgsql physical persistence layer
-> not implemented at that snapshot

PostgreSQL migrations
-> not created at that snapshot
```

The architecture was approved at that milestone, while physical PostgreSQL verification still depended on later implementation and real-database testing.

Current physical implementation status must be verified from the repository, migrations, build, and tests. This historical section has no current-status authority.

---

# 102. Historical Phase-16 Verification Result

This is a historical verification result. Later architecture sections supersede its navigation count and implementation-status assumptions.

```text
Frontend-to-Backend Contract      PASS
Historical navigation coverage    PASS
Overlay/Correction Coverage       PASS
Domain Boundaries                 PASS
Inventory/Costing Model           PASS
Serialized Trace Model            PASS
Sales/Returns                     PASS
Purchasing                        PASS
Thaka                             PASS
Reporting                         PASS
Identity/Permissions              PASS
Planned PostgreSQL Model          PASS
Reliability/Security              PASS
Future LAN Path                   PASS

Physical PostgreSQL Implementation
-> NOT IMPLEMENTED AT THAT SNAPSHOT

Production WPF <-> Backend Integration
-> INCOMPLETE AT THAT SNAPSHOT
```

For current navigation authority use Section 209. For the current V1 declaration use Section 228. For the current implementation gate use Section 233.

---

# 103. SHOP-HOLDER OPERATIONAL ADDENDUM - Mandatory Before Physical Database

A shop-holder operational audit identified workflows that are structurally important for an electrical/electronics retail shop.

The following are now part of the authoritative V1 architecture:

```text
CRITICAL
1. Multi-Unit / Packaging Conversion
2. Warranty Claims
3. Batch Physical Stocktake
4. Damaged / Defective / Supplier-Warranty Lifecycle

STRONGLY REQUIRED FOR V1
5. Cash Session / Daily Closing
6. Quotation / Estimate
```

These additions do not introduce a complex accounting ledger.

Normal customer-credit sales remain outside the current V1 scope. Supplier Khata / Accounts Payable is separately approved and finalized in Sections 215-220.

No physical PostgreSQL migration should be created before Sections 103 onward are incorporated.

---

# 104. Multi-Unit / Packaging Conversion - FINAL

Electrical products may be purchased, stocked, sold, or issued in different units.

Examples:

```text
LED Bulb
Base Unit       = Piece
Purchase Unit   = Box
1 Box           = 12 Pieces
Sale Unit       = Piece or Box

Cable
Base Unit       = Meter
Purchase Unit   = Roll
1 Roll          = 90 Meters
Sale Unit       = Meter

Cable alternative display
1 Foot          = 0.3048 Meter
```

The inventory source of truth always uses the Product Base Unit.

All business documents snapshot the selected unit and conversion factor.

---

# 105. Product Unit Model

`catalog.products.UnitId` is superseded by:

```text
catalog.products.base_unit_id
```

Add:

```text
catalog.product_units
---------------------
id
product_id
unit_id
factor_to_base_unit
can_purchase
can_sell
can_use_in_thaka
is_default_purchase_unit
is_default_sale_unit
is_active
created_at
updated_at
```

Rules:

```text
factor_to_base_unit > 0

Base Unit:
factor_to_base_unit = 1

Only one default Purchase Unit per Product
Only one default Sale Unit per Product
```

Conversion is linear only.

No formula-based/nonlinear unit conversion exists in V1.




# 106. Unit Conversion in Transactions

Every transaction line stores both entered quantity and base quantity.

Purchase Item additions:

```text
entered_unit_id
entered_quantity
factor_to_base_snapshot
base_quantity
entered_unit_cost
effective_base_unit_cost
```

Sale Item additions:

```text
sale_unit_id
sale_quantity
factor_to_base_snapshot
base_quantity
unit_price
gross_line_total
```

Thaka Material Item additions:

```text
issue_unit_id
issue_quantity
factor_to_base_snapshot
base_quantity
rate_per_selected_unit
```

Stock, costing, movement effects, lot consumption, and availability all operate in Base Quantity.

Returns reverse the original line's snapshotted conversion rather than using today's Product conversion.

A later change from:

```text
1 Box = 12 Pieces
```

to another pack size must never rewrite historical purchases or sales.

---

# 107. Conversion Rules for Tracking Modes

QUANTITY products may use packaging conversion according to their configured unit/conversion rules.

Example:

```text
1 Box = 12 Pieces
```

LENGTH products use one physical base measure.

Recommended:

```text
Base Unit = Meter
```

with controlled conversion units such as Foot/Roll.

SERIALIZED / individually tracked products represent discrete physical objects. Fractional physical-object truth is forbidden.

For any tracked transaction line, first calculate the conversion using decimal/PostgreSQL-numeric-equivalent exact arithmetic:

```text
ExactConvertedBaseQuantity
=
EnteredQuantity * FactorToBaseUnit
```

Validation occurs on `ExactConvertedBaseQuantity` before any display rounding or persistence-scale rounding.

Required tracked-unit invariants:

```text
EnteredQuantity > 0
FactorToBaseUnit > 0
ExactConvertedBaseQuantity > 0
ExactConvertedBaseQuantity = whole integer count
Committed BaseQuantity = ExactConvertedBaseQuantity
Exact captured/selected physical identity count = BaseQuantity
one physical identity represents exactly one base physical unit
```

No `floor`, `ceiling`, nearest-integer rounding, truncation, tolerance band, or epsilon comparison may convert a fractional physical result into an accepted tracked quantity.

Valid examples:

```text
1 Carton x 6 Fans = 6
=> PASS
=> 6 InventoryUnits / exact identities

2 Cartons x 6 Fans = 12
=> PASS
=> 12 InventoryUnits / exact identities

1.25 Cartons x 4 Fans = 5
=> PASS if the entered unit/transaction permits that decimal quantity
=> 5 InventoryUnits / exact identities
```

Invalid example:

```text
1.10 Cartons x 4 Fans = 4.40
=> REJECT for SERIALIZED / individually tracked Product
=> do not round to 4
=> do not round to 5
=> create zero InventoryUnits
=> allocate zero TrackingCodes
=> create no partial stock/cost effect
```

For tracked receiving:
- Purchase/Inventory `+` must reject the line before sequence allocation when converted base quantity is fractional;
- identity capture count must equal exact whole BaseQuantity before commit;
- SupplierProduct sequence range size equals that exact identity count.

For tracked Sale/Thaka issue:
- exact physical units are selected;
- selected InventoryUnit count must equal BaseQuantity;
- a fractional exact-unit Sale/issue is rejected.

For tracked positive Stock Adjustment/opening stock:
- exact physical identities are required for the whole BaseQuantity;
- manufacturer Serial/IMEI is additionally required only where Product policy enables those overlays;
- adjustment cannot manufacture a fractional InventoryUnit.

For tracked Warranty replacement:
- replacement quantity must resolve to whole physical units;
- each replacement physical unit follows the normal SupplierProduct sequence/TrackingCode authority;
- no fractional replacement identity exists.

QUANTITY/LENGTH products may legitimately carry fractional BaseQuantity within the canonical precision policy and continue using lot/balance provenance rather than artificial InventoryUnit-per-fraction generation.

Backend/domain validation is authoritative. UI controls should prevent or flag impossible tracked quantities early, but UI masking/step controls are not a substitute for backend validation.

A serialized Sale/Thaka issue always selects exact physical units.

---

# 108. Product-Unit Barcode Support

A packaging unit may optionally have its own barcode.

Add:

```text
catalog.product_unit_barcodes
-----------------------------
product_unit_id
barcode
is_active
```

Example:

```text
Single Bulb barcode
-> Piece

Outer Box barcode
-> Box (12 Pieces)
```

Barcode lookup returns:

```text
ProductId
ProductUnitId
FactorToBase
```

POS therefore knows whether a scan represents one Piece or one Box.

Barcode uniqueness remains global across active product/unit barcodes.

---

# 109. Multi-Unit Costing Rules

Moving Weighted Average remains calculated per Base Unit.

Example:

```text
Purchase:
10 Boxes
1 Box = 12 Pieces
Box Cost = Rs. 1,200

Base Qty = 120 Pieces
Base Unit Cost = Rs. 100
```

The cost pool receives:

```text
120 x Rs.100
```

not:

```text
10 x Rs.1,200 as 10 stock units
```

Sale in Box:

```text
1 Box
-> consumes 12 Base Units
```

Sale in Piece:

```text
1 Piece
-> consumes 1 Base Unit
```

This rule applies identically to Returns, Thaka, Stocktake, and Purchase Return eligibility.

---

# 110. Warranty Module - FINAL V1 Addition

Warranty is now a dedicated logical module.

It handles two fundamentally different ownership flows:

```text
A. CUSTOMER WARRANTY CLAIM
Customer owns the item
Shop temporarily holds/carries it for service
It is NOT shop inventory

B. SHOP STOCK WARRANTY CASE
Shop owns the item
It leaves sellable stock but remains a shop asset
```

These flows must never share stock accounting semantics.




# 111. Customer Warranty Claim Data Model - Corrected

Warranty keeps the two ownership models from Section 110 strictly separate.

Customer-owned warranty goods are never normal shop inventory merely because the shop has custody.

Core tables remain:

```text
warranty.claims
warranty.claim_items
warranty.claim_events
warranty.claim_item_units
```

Customer Warranty Claim header:

```text
Id
ClaimNumber
CustomerId
OriginalSaleId nullable
SupplierId nullable during intake/review
Status
CurrentCustody
ReceivedAt
ResolvedAt nullable
ClosedAt nullable
CreatedBy
CreatedAt
Version
```

Claim item:

```text
Id
ClaimId
OriginalSaleItemId
ProductId
BaseQuantity
FaultDescription
WarrantyValidUntilSnapshot
ResolutionType nullable
ResolutionNote nullable
ReplacementProductId nullable
SourceLotConsumptionId nullable
```

For individually tracked Products, `warranty.claim_item_units` links:

```text
ClaimItemId
OriginalInventoryUnitId
ReplacementInventoryUnitId nullable
```

## 111.1 One-Claim / One-Supplier V1 Invariant

A Supplier-bound Customer Warranty Claim may represent many items only when all items resolve to the same authoritative Supplier.

Supplier resolution is never based on free-form client choice.

For an exact tracked unit:

```text
OriginalInventoryUnit
-> SourcePurchaseItem
-> Purchase
-> SupplierId
```

and the result is cross-checked with:

```text
OriginalInventoryUnit.SupplierProduct
-> SupplierId
```

For QUANTITY/LENGTH items, Supplier provenance is derived from the original SaleItem's immutable lot-consumption provenance.

If a SaleItem consumed multiple source lots/Suppliers, the warranty-eligible claimed quantity is deterministically allocated against the original lot-consumption records. Supplier-bound portions are split into separate Warranty Claims when they resolve to different Suppliers.

At RECEIVED / UNDER_REVIEW, `SupplierId` may remain null only while provenance is unresolved.

Before `SENT_TO_SUPPLIER`:
- SupplierId is mandatory;
- every claim item must resolve to that same Supplier;
- wrong-Supplier dispatch is rejected.

A mixed-Supplier claim is never sent as one supplier claim.

## 111.2 Exact-Unit Active-Claim Invariant

For an exact tracked physical item:

```text
one OriginalInventoryUnit
-> at most one active Customer Warranty Claim
```

Active claim states are:

```text
RECEIVED
UNDER_REVIEW
SENT_TO_SUPPLIER
SUPPLIER_PROCESSING
READY_FOR_CUSTOMER
```

Claim creation/transition must use the existing resource-lock abstraction with an exact-unit warranty resource key so two concurrent requests cannot open overlapping active claims for the same InventoryUnit.

A CLOSED or CANCELLED claim does not by itself permanently prohibit a later valid claim. Eligibility is re-evaluated against warranty period and prior terminal resolutions.

---

# 112. Customer Warranty Eligibility, State Machine, and Custody - Corrected

## 112.1 Warranty Eligibility Gate

`CreateWarrantyClaimCommand` must validate warranty eligibility before persistence.

For an exact tracked unit, all applicable checks must pass:

1. Original Sale exists and was successfully completed.
2. Original SaleItem belongs to the claimed Product.
3. Original SaleItemUnit links the exact InventoryUnit.
4. The physical unit was not invalidated by Purchase Void.
5. The customer still owns/controls the claimed unit; a completed Sale Return that returned the unit to shop stock makes that customer-warranty path ineligible.
6. Claim received time is not after the authoritative warranty-valid-until snapshot, unless a separately approved explicit warranty-extension rule exists.
7. No other active claim exists for the same InventoryUnit.
8. Product/Supplier provenance is resolvable before supplier dispatch.
9. The claimed unit has not already been terminally replaced/refunded in a way that ended eligibility for the original physical object.

For QUANTITY/LENGTH SaleItems:

```text
WarrantyEligibleBaseQty
=
OriginalSoldBaseQty
- CompletedSaleReturnBaseQty
- ActiveWarrantyClaimBaseQty
- TerminallyRemovedWarrantyBaseQty
```

Terminally removed warranty quantity includes physical quantity permanently satisfied by outcomes such as REPLACED or REFUNDED for the original item.

REPAIRED, REJECTED, or CANCELLED claims release their non-returned quantity for a later eligibility check, subject to the original warranty expiry/policy.

Requested claim BaseQuantity must be greater than zero and no greater than `WarrantyEligibleBaseQty`.

The backend, not the UI, is authoritative for all eligibility calculations.

For QUANTITY/LENGTH claim creation, acquire the warranty SaleItem resource lock before recalculating WarrantyEligibleBaseQty and persist the claim within the same transaction. This prevents concurrent quantity claims from oversubscribing the original sold quantity.

## 112.2 Authoritative Claim States

```text
RECEIVED
UNDER_REVIEW
SENT_TO_SUPPLIER
SUPPLIER_PROCESSING
READY_FOR_CUSTOMER
CLOSED
CANCELLED
```

## 112.3 Allowed Transitions

```text
RECEIVED
-> UNDER_REVIEW
-> CANCELLED

UNDER_REVIEW
-> SENT_TO_SUPPLIER
-> READY_FOR_CUSTOMER
-> CANCELLED
SENT_TO_SUPPLIER
-> SUPPLIER_PROCESSING
-> READY_FOR_CUSTOMER

SUPPLIER_PROCESSING
-> READY_FOR_CUSTOMER

READY_FOR_CUSTOMER
-> CLOSED
```

Any transition not listed above is rejected.

CLOSED and CANCELLED are terminal in V1. Reopening a closed claim requires a separately designed future workflow and is not implied.

## 112.4 Custody Model

V1 custody is:

```text
WITH_CUSTOMER
WITH_SHOP
WITH_SUPPLIER
```

The previous service-center custody option is removed from V1 because no ServiceCenter identity/provenance model exists.

Required status/custody compatibility:

```text
RECEIVED             -> WITH_SHOP
UNDER_REVIEW         -> WITH_SHOP
SENT_TO_SUPPLIER     -> WITH_SUPPLIER
SUPPLIER_PROCESSING  -> WITH_SUPPLIER
READY_FOR_CUSTOMER   -> WITH_SHOP
CLOSED               -> WITH_CUSTOMER
CANCELLED            -> WITH_CUSTOMER after physical return/handover
```

A transition that would produce an incompatible status/custody pair is rejected.
Every accepted status, custody, resolution, replacement, and handover transition creates an immutable `warranty.claim_events` record.

Customer-owned warranty goods never increment:

```text
inventory.stock_balances
inventory.lot_bucket_balances
inventory.cost_states
```

because custody is not ownership.

---

# 113. Customer Warranty Replacement - Exact Physical Identity

A customer-specific Supplier replacement is a warranty settlement event, not a normal Purchase.

It must not fabricate a Purchase/PurchaseItem solely to obtain a TrackingCode.

## 113.1 Replacement Identity Transaction

For an individually tracked replacement:

```text
Warranty Claim
-> validate REPLACED resolution
-> determine Replacement Product
-> resolve/validate SupplierProduct
-> acquire normal ClientOperation lock
-> acquire Warranty Claim resource lock
-> acquire SupplierProduct sequence lock
-> reserve NextItemSequence
-> create new InventoryUnit physical identity
-> create new TrackingCode
-> link OriginalInventoryUnit <-> ReplacementInventoryUnit
-> record immutable ClaimEvent
-> commit
-> TrackingCode becomes officially issued
```
The same `SupplierProduct.NextItemSequence`, minimum-six-digit formatting, non-reuse rule, and `IResourceLock / PostgresOperationLock` authority used by Purchase tracking apply here.

A missing SupplierProduct relationship must be explicitly created/activated by an authorized relationship operation. Warranty replacement must never silently create a Product.

## 113.2 Customer-Owned Replacement Is Non-Stock

A replacement specifically owed to the customer:
- creates an exact physical identity when tracking policy requires it;
- does not create normal SELLABLE quantity;
- does not create an InventoryLot;
- does not increase ProductCostState;
- does not create shop carrying cost merely because the shop temporarily holds it.

Two non-stock exact-unit lifecycle states are required conceptually:

```text
WARRANTY_CUSTOMER_HELD
WARRANTY_CUSTOMER_HANDED_OVER
```

They are InventoryUnit identity/lifecycle states, not inventory quantity buckets.

On receipt:

```text
ReplacementInventoryUnit
-> WARRANTY_CUSTOMER_HELD
```

On actual handover:

```text
WARRANTY_CUSTOMER_HELD
-> WARRANTY_CUSTOMER_HANDED_OVER
```

Do not mark a replacement as normal SOLD merely because it arrived at the shop.

## 113.3 Replacement Provenance

A warranty-origin InventoryUnit must have warranty origin provenance rather than a fake Purchase origin.

See Section 176 for the corrected InventoryUnit origin model.

If the replacement instead becomes unrestricted shop-owned stock, use the Shop-Owned Warranty replacement path in Section 114.

---

# 114. Shop-Owned Supplier Warranty - Financially Closed Outcomes

Shop-owned defective goods remain shop inventory assets while recoverable.

The authoritative inventory buckets remain:

```text
SELLABLE
DAMAGED
DEFECTIVE
WITH_SUPPLIER
SCRAP
```

Sending recoverable stock to Supplier Warranty:

```text
DEFECTIVE
-> WITH_SUPPLIER
```

does not create COGS or inventory loss. Carrying cost remains owned by the shop.

## 114.1 Repaired Outcome

```text
WITH_SUPPLIER
-> repaired
-> SELLABLE
```

The same physical InventoryUnit retains the same TrackingCode.

Carrying cost remains unchanged.

No revenue and no Supplier Khata effect are created by repair alone.

## 114.2 Replacement Outcome

A Supplier replacement is a different physical object.

For individually tracked stock:

```text
Original unit WITH_SUPPLIER
-> Supplier retains/replaces original
-> Original unit becomes historical supplier-returned/replaced identity
-> new SupplierProduct sequence allocated
-> new InventoryUnit + new TrackingCode created
-> old <-> new replacement linkage persisted
-> original carrying cost transfers to replacement unit
-> replacement enters the approved shop-owned bucket, normally SELLABLE
```

Replacement does not create a new Purchase and does not double inventory carrying cost.

If the Supplier charges or credits an explicit difference, that difference is a separate financial event. It must not silently rewrite original carrying cost.

## 114.3 Rejected Outcome

Supplier rejection returns the same physical object:

```text
WITH_SUPPLIER
-> DEFECTIVE
```

The same TrackingCode and carrying cost remain.

A later authorized write-off may move it to SCRAP and recognize inventory loss.

## 114.4 Scrap / Write-Off Outcome

```text
WITH_SUPPLIER / DEFECTIVE
-> SCRAP
```

removes the recoverable carrying cost and creates `RecognizedInventoryLoss`.

SCRAP may remain operationally visible with zero carrying cost until physical disposal.

## 114.5 Supplier Monetary Credit Outcome

When Supplier retains the physical item and settles the warranty with monetary/account credit, the entire resolution is one atomic business transaction.

Inputs include:

```text
WarrantyCaseId
SupplierId
ResolvedBaseQuantity / exact InventoryUnitIds
InventoryCarryingCostResolved
SupplierCreditAmount
Reason / SupplierReference
Actor
ClientOperationId
```

Atomic effects:

1. Validate case, Supplier provenance, quantity/units, and WITH_SUPPLIER custody.
2. Remove the resolved physical quantity from recoverable WITH_SUPPLIER inventory.
3. Remove `InventoryCarryingCostResolved` from shop inventory carrying cost.
4. Create `WARRANTY_CREDIT / DECREASE_PAYABLE` in Supplier Khata for `SupplierCreditAmount`.
5. Record warranty/inventory movement and exact unit links.
6. Persist financial recovery snapshots on the Warranty Case resolution.
7. Audit and commit atomically.

Define:

```text
RecoveryDifference
=
SupplierCreditAmount
- InventoryCarryingCostResolved
```
If `RecoveryDifference < 0`:

```text
RecognizedInventoryLoss = -RecoveryDifference
```

If `RecoveryDifference = 0`, there is no gain/loss difference.

If `RecoveryDifference > 0`, the positive difference is reported as `WarrantyRecoveryGain`, not Sales Revenue.

V1 Supplier Khata is not a General Ledger; these snapshots/reporting classifications exist to prevent cost/profit distortion.

No physical item may remain simultaneously in WITH_SUPPLIER carrying inventory after a terminal monetary-credit resolution.

---

# 115. Supplier-Warranty Case Linkage - Corrected

Shop-owned warranty handling uses Warranty Case records linked to authoritative inventory movements.

Required references/snapshots include:

```text
WarrantyCaseId
ProductId
BaseQuantity / exact InventoryUnitIds
SupplierId
SourcePurchaseItemId where known
Fault
SentAt
SupplierReference
Resolution
ResolvedAt nullable
ReceivedAt nullable
OriginalInventoryUnitId nullable
ReplacementInventoryUnitId nullable
InventoryCarryingCostResolved nullable
SupplierCreditAmount nullable
RecoveryDifference nullable
Version
```

Resolution types for shop-owned Warranty include:

```text
REPAIRED
REPLACED
REJECTED
SCRAPPED
CREDITED
```

Inventory movement types include or are extended with:

```text
SEND_TO_SUPPLIER_WARRANTY
RECEIVE_REPAIRED_FROM_SUPPLIER
RECEIVE_REPLACEMENT_FROM_SUPPLIER
WARRANTY_REJECTED_RETURN
WARRANTY_WRITE_OFF
WARRANTY_CREDIT_RESOLUTION
```

Exact unit links remain in `inventory.movement_units`.

For a terminal CREDITED resolution:
- the warranty case cannot remain operationally open as recoverable stock;
- the corresponding WITH_SUPPLIER quantity/cost must be removed exactly once;
- the SupplierAccountEntry source is unique to that approved Warranty financial resolution;
- a later cash refund from Supplier is a separate Supplier Refund settlement, not a second Warranty Credit.

# 116. Batch Physical Stocktake - FINAL V1 Addition

Stock Adjustment handles individual corrections.

Stocktake handles controlled physical counting of many products.

Add:

```text
inventory.stocktakes
inventory.stocktake_items
inventory.stocktake_unit_checks
```

Stocktake statuses:

```text
DRAFT
COUNTING
REVIEW
POSTED
CANCELLED
```

V1 supports:

```text
FULL_SHOP
CATEGORY
```

scope.

A Stocktake never directly overwrites StockBalance.

Posting creates audited Physical Count Correction movement(s).

---

# 117. Stocktake Workflow

```text
Create Stocktake
v
Choose Scope
v
Start Counting
v
Capture Expected Snapshot
v
Physical Count
v
Variance Review
v
Recount where needed
v
Authorized Post
v
Generate Stock Adjustment / Movement Effects
v
Stocktake = POSTED
```

Stocktake item stores:

```text
product_id
expected_sellable_qty
counted_sellable_qty
variance_qty
counted_by
counted_at
review_note
```

For non-sellable buckets, optional bucket counts may also be recorded when the count scope includes them.

---

# 118. Stocktake Concurrency Rule

Once a Stocktake enters COUNTING, stock-affecting transactions for its scope are blocked.

Blocked operations include:

```text
Sale completion
Sale Return
Purchase completion
Purchase Return
Purchase Void
Thaka Material Issue/Reversal
Stock Adjustment
Supplier-Warranty stock movement
```

Non-stock operations such as viewing reports or entering an Expense may continue.

V1 deliberately prefers a reliable after-hours/count-window process over complex live-count reconciliation.

Cancelling or posting the Stocktake releases the scope.

---

# 119. Serialized Stocktake

Serialized products are counted by exact identity, not quantity only.

For each serialized Product:

```text
Expected Unit IDs
vs
Physically Scanned/Selected Unit IDs
```

Possible findings:

```text
Expected and Found
Missing
Unexpected Physical Unit
Wrong Current Status
```

A missing known unit may create an authorized adjustment/write-off.

An unexpected physical serialized unit cannot be silently inserted.

It requires a controlled positive adjustment with:

```text
Identity
Acquisition/Cost Basis
Reason
Audit
```

---

# 120. Non-Sellable Inventory Operations - FINAL

Inventory must expose an operational queue for:

```text
DAMAGED
DEFECTIVE
WITH_SUPPLIER
SCRAP
```

Required commands:

```text
MarkDamaged
MarkDefective
RestoreToSellable
SendToSupplierWarranty
ReceiveRepairedFromSupplier
ReceiveReplacementFromSupplier
WriteOffToScrap
RecordSupplierWarrantyRejection
```

Every action creates InventoryMovement + MovementEffects and Business Audit.

No command directly edits a bucket balance.

---

# 121. Damaged / Defective Financial Rules

```text
SELLABLE -> DAMAGED
SELLABLE -> DEFECTIVE
```

does not automatically create COGS or loss while the item remains a recoverable shop asset.

```text
DAMAGED / DEFECTIVE -> SELLABLE
```

restores availability without creating revenue.

```text
DAMAGED / DEFECTIVE -> WITH_SUPPLIER
```

preserves carrying cost.

```text
DAMAGED / DEFECTIVE / WITH_SUPPLIER -> SCRAP
```

removes carrying cost and creates RecognizedInventoryLoss.

SCRAP quantity may remain operationally visible with zero carrying cost until physically disposed.

---

# 122. Non-Sellable Inventory Queries

Add read models:

```text
GetNonSellableInventoryQuery
GetDamagedInventoryQuery
GetDefectiveInventoryQuery
GetWithSupplierInventoryQuery
GetScrapInventoryQuery
GetWarrantyCaseDetailQuery
```

Product Detail includes condition/history visibility.

Inventory may still expose condition filters/drawers, while the dedicated Warranty full screen defined in Section 221 is the operational Warranty authority.




# 123. Cash Session / Daily Closing - STRONG V1 Addition

Edge Retails is not becoming a general ledger.

A lightweight Cash Session exists only to answer:

```text
How much physical cash should be in the counter?
```

Add:

```text
finance.cash_sessions
finance.cash_movements
```

Cash Session:

```text
id
business_date
opened_by
opened_at
opening_cash
status
expected_closing_cash
counted_closing_cash
difference
closed_by
closed_at
note
```

Statuses:

```text
OPEN
CLOSED
VOIDED_BY_OWNER
```

Only one active cash session exists per register/terminal.

Standalone V1 has one logical register.

---

# 124. Cash Movement Rules - Supplier Khata Compatible

Cash movement is append-only and linked to an authoritative business source where applicable.

Canonical V1 movement semantics include:

```text
SALE_CASH_IN
SALE_REFUND_CASH_OUT
THAKA_PAYMENT_CASH_IN
EXPENSE_CASH_OUT
MANUAL_CASH_IN
MANUAL_CASH_OUT
SUPPLIER_PAYMENT_CASH_OUT
SUPPLIER_PAYMENT_REVERSAL_CASH_IN
SUPPLIER_REFUND_CASH_IN
SUPPLIER_REFUND_REVERSAL_CASH_OUT
```

The earlier `PURCHASE_CASH_OUT` naming is superseded for new Supplier Khata implementation by `SUPPLIER_PAYMENT_CASH_OUT` because a Supplier payment may be immediate, delayed, or an explicit advance and is not itself the Purchase liability record.

Existing physical implementations may migrate/alias old Purchase cash-out rows, but the final architecture must not maintain both names as independent financial truth.

Expected Cash:

```text
Opening Cash
+ Cash In Movements
- Cash Out Movements
= Expected Closing Cash
```

Bank/External payment methods do not affect cash-drawer expected balance.

---

# 125. Cash Session Transaction Integration - Supplier Settlement Corrected

When a business transaction uses CASH_DRAWER and is drawer-affecting, its CashMovement is written in the same PostgreSQL transaction.

Examples:

```text
Cash Sale
-> Sale + Payment + SALE_CASH_IN

Cash Sale Return
-> SaleReturn + SALE_REFUND_CASH_OUT

Cash Thaka Payment
-> ThakaPayment + THAKA_PAYMENT_CASH_IN

Cash Expense
-> Expense + EXPENSE_CASH_OUT

Cash Supplier Payment
-> SupplierPayment + SUPPLIER_PAYMENT_CASH_OUT

Cash Supplier Payment Reversal
-> SupplierPaymentReversal + SUPPLIER_PAYMENT_REVERSAL_CASH_IN

Cash Supplier Refund Received
-> SupplierRefund + SUPPLIER_REFUND_CASH_IN

Cash Supplier Refund Reversal
-> SupplierRefundReversal + SUPPLIER_REFUND_REVERSAL_CASH_OUT
```

Supplier Khata balance truth and Cash Drawer truth are separate but atomically coordinated when a Supplier settlement uses the drawer.

A completed Purchase always creates Supplier payable truth independently of whether money is paid immediately.

An immediate Purchase payment is recorded as a SupplierPayment in the same Purchase transaction. A delayed payment is recorded later through the Supplier Payment workflow.

Purchase Return, Purchase Void, and Warranty Credit change Supplier Khata through their own append-only account entries. They do not create cash movement by themselves.

A later Supplier credit settlement received by the shop is recorded as a separate SupplierRefund transaction. CASH_DRAWER receipt creates `SUPPLIER_REFUND_CASH_IN`; EXTERNAL receipt does not affect the drawer.

Manual Cash In/Out still requires Reason, Amount, Actor, and Note, and is always audited.

# 126. Daily Closing Workflow

```text
Open Cash Session
v
Enter Opening Cash
v
Normal Shop Operations
v
Close Day / Close Cash
v
System Calculates Expected Cash
v
User Counts Physical Cash
v
Difference Shown
v
Confirm Close
v
Cash Session = CLOSED
```

A closed session is immutable.

Correction uses Owner-authorized void/reopen workflow with reason and audit, not direct editing.

Daily Reports may show:

```text
Opening Cash
Cash In
Cash Out
Expected Cash
Counted Cash
Over / Short
```

This remains separate from Profit.

---

# 127. Quotation / Estimate - STRONG V1 Addition

Electrical shops frequently prepare estimates for customers, electricians, and house/project work.

Quotation has no stock or financial posting effect.

Add:

```text
sales.quotations
sales.quotation_items
```

Quotation header:

```text
quotation_number
customer_id nullable
customer_name_snapshot
quotation_date
valid_until nullable
status
subtotal
discount
grand_total
notes
created_by
created_at
version
```

Quotation item snapshots:

```text
product_id
product_name
sku
selected_unit_id
quantity
factor_to_base_snapshot
quoted_unit_price
line_total
```

---

# 128. Quotation State Machine

```text
DRAFT
ISSUED
EXPIRED
CONVERTED
CANCELLED
```

Quotation does not:

```text
Deduct stock
Reserve stock
Create COGS
Create revenue
Affect cash
Affect dashboard sales
```

It may be printed/reprinted.

Expired or cancelled quotation cannot be converted without an explicit reissue/update workflow.

---

# 129. Convert Quotation to Sale

V1 supports:

```text
Quotation
-> Convert to POS Sale
```

Conversion does not trust prices supplied by the client.

Server loads the stored Quotation and treats its quoted price as an authorized pricing source while the quotation is valid.

Then it revalidates:

```text
Product active
Unit conversion still valid for historical quote conversion
Stock available
Serialized unit selection where needed
Customer
Discount
Quotation status
```

No stock is reserved until Complete Sale commits.

After successful Sale:

```text
Quotation.Status = CONVERTED
Quotation.ConvertedSaleId = SaleId
```

in the same business transaction or protected conversion workflow.

---

# 130. Frontend Placement - Historical Rules Superseded in Part

The original no-new-screen navigation assumption from this section is superseded by Sections 84 and 209.

Current V1 navigation authority is exactly 19 full screens. Product Management and Inventory are separate full management surfaces, and Warranty is a dedicated full operational screen.

Placement rules that remain compatible with the current contract:

```text
Inventory
-> Non-Sellable Stock and Stocktake operational surfaces

POS
-> New Quotation action / Quotation drawer
-> Convert Quotation to Cart

Dashboard / Reports
-> Open/Close Cash Session operational surface
-> Daily Closing summary

Customer Detail
-> Warranty Claims history
-> Quotations history
```

Warranty must not be reduced to an Inventory tab. This section must not be used to infer the current full-screen count.

---

# 131. Permissions for New Operational Modules

Add permissions:

```text
catalog.product_units.manage

warranty.claim.create
warranty.claim.update
warranty.claim.send_supplier
warranty.claim.resolve
warranty.claim.handover
warranty.shop_stock.manage

inventory.stocktake.create
inventory.stocktake.count
inventory.stocktake.review
inventory.stocktake.post
inventory.nonsellable.manage

cash.session.open
cash.session.close
cash.manual_movement

sales.quotation.create
sales.quotation.edit
sales.quotation.convert
sales.quotation.cancel
```

Default policy:

```text
OWNER
-> all

MANAGER
-> warranty, stocktake, non-sellable, quotation
-> cash close optional/yes by policy
-> stocktake post optional

CASHIER
-> quotation create optional
-> warranty claim intake optional
-> no stocktake post
-> no non-sellable write-off
-> no manual cash movement by default
```

Write-off/Scrap and Stocktake Post remain high-trust operations.

---

# 132. Reporting Impact of New Modules

Warranty Customer Claims:

```text
No Sales impact
No Inventory impact
No Profit impact by default
Operational claim counts only
```

Shop-Owned Warranty:

```text
WITH_SUPPLIER remains owned inventory carrying cost
Write-off creates Inventory Loss
Replacement restores stock without new revenue
```

Stocktake:

```text
Variance posting may create Inventory Loss
or positive inventory correction
```

Cash Session:

```text
Cash Over/Short is operationally reported
It is not automatically treated as Sales or Profit
unless an explicit adjustment policy is later approved
```

Quotation:

```text
No Sales/Revenue/COGS until converted and Sale completes
```

---

# 133. PostgreSQL Schema Additions Before EF Core InitialProductionBaseline

Mandatory new/changed tables:

```text
catalog.products
  base_unit_id

catalog.product_units
catalog.product_unit_barcodes

warranty.claims
warranty.claim_items
warranty.claim_item_units
warranty.claim_events

inventory.stocktakes
inventory.stocktake_items
inventory.stocktake_unit_checks

inventory.stock_balances
  with_supplier_qty

inventory movement/status checks
  WITH_SUPPLIER

finance.cash_sessions
finance.cash_movements

sales.quotations
sales.quotation_items
```

Transaction-line conversion snapshot fields must be included from the first physical migration.

Do not first create a single-unit schema and migrate immediately afterward.

---

# 134. Mandatory New Application Commands

Catalog:

```text
ConfigureProductUnitsCommand
SetProductUnitBarcodeCommand
```

Warranty:

```text
CreateWarrantyClaimCommand
SendWarrantyClaimToSupplierCommand
RecordWarrantyResolutionCommand
HandoverWarrantyItemCommand
SendShopStockToSupplierWarrantyCommand
ReceiveSupplierWarrantyItemCommand
WriteOffWarrantyItemCommand
```

Stocktake:

```text
CreateStocktakeCommand
StartStocktakeCommand
RecordStocktakeCountCommand
ReviewStocktakeCommand
PostStocktakeCommand
CancelStocktakeCommand
```

Non-Sellable:

```text
TransferInventoryConditionCommand
RestoreToSellableCommand
WriteOffInventoryCommand
```

Cash:

```text
OpenCashSessionCommand
RecordManualCashMovementCommand
CloseCashSessionCommand
```

Quotation:

```text
CreateQuotationCommand
UpdateQuotationCommand
IssueQuotationCommand
ConvertQuotationToSaleCommand
CancelQuotationCommand
```

---

# 135. Mandatory New Queries

```text
GetProductUnitsQuery
ResolveBarcodeQuery

GetWarrantyClaimsQuery
GetWarrantyClaimDetailQuery
GetCustomerWarrantyHistoryQuery

GetStocktakeListQuery
GetStocktakeDetailQuery
GetStocktakeVarianceQuery

GetNonSellableInventoryQuery
GetSupplierWarrantyInventoryQuery

GetCurrentCashSessionQuery
GetCashSessionSummaryQuery
GetDailyClosingQuery

GetQuotationsQuery
GetQuotationDetailQuery
```

All sensitive financial DTO fields still follow existing permission-safe projection rules.

---

# 136. Mandatory Tests Before Release

Multi-Unit:

```text
Box -> Piece conversion
Roll -> Meter conversion
Historical factor snapshot
Return uses original factor
MWA cost per Base Unit
Serialized package identity count
Unit barcode resolution
```

Warranty:

```text
Customer claim never increments inventory
Shop stock warranty transfers bucket
Replacement flow
Rejected flow
Write-off creates inventory loss
Serialized exact-unit trace
```

Stocktake:

```text
Scoped stock writes blocked during COUNTING
Variance posting creates movements
No direct StockBalance overwrite
Serialized missing/unexpected unit behavior
Concurrent post prevented
```

Cash Closing:

```text
Cash Sale increases expected cash
Cash Refund decreases expected cash
Cash Expense decreases expected cash
Bank payment ignored by drawer
Closing difference calculation
One open session rule
```

Quotation:

```text
No stock effect
No revenue effect
Valid conversion
Expired conversion rejected
Server uses stored quote price
Converted quotation cannot convert twice
```

---

# 137. Explicitly Deferred Accounting Scope

The following remain deferred until separately approved:

```text
Normal Customer Credit / Udhaar Ledger
Customer Credit Limit / Statement
General Ledger
Bank Account Reconciliation
Double-entry accounting
```

Do not partially implement these through random balance columns.

Extension points exist, but V1 transactions must not pretend a complete receivable/payable ledger exists.

---

# 138. Revised Implementation Gate

Physical PostgreSQL implementation is now allowed only against the complete baseline through Section 137.

Implementation order:

```text
1. Units + Product Conversion foundation
2. Catalog masters / Product
3. Inventory ledger + buckets + lots + costing
4. Serialized exact-unit trace
5. Warranty schema/lifecycle
6. Stocktake
7. Purchasing
8. Sales / Returns / Quotations
9. Thaka
10. Expenses
11. Cash Sessions
12. Reporting
13. Identity / Permissions
14. Backup / License / Diagnostics
15. WPF demo-service replacement
```

Cross-module transactions must be integration-tested against real PostgreSQL before UI migration is considered complete.

---

# 139. Historical Shop-Holder Audit Milestone

This section records the shop-holder audit milestone at that point in the architecture history. Later explicit corrections supersede conflicts; the current declaration is Section 228.

```text
Core Sales / Returns             FINAL
Purchasing                       FINAL
Inventory Ledger                 FINAL
Multi-Unit Conversion            FINAL
Serialized Trace                 FINAL
Warranty Claims                  FINAL
Shop-Owned Warranty              FINAL
Damaged/Defective Lifecycle      FINAL
Batch Stocktake                  FINAL
Thaka                            FINAL
Expenses                         FINAL
Cash Session / Daily Closing     FINAL
Quotation / Estimate             FINAL
Reporting Rules                  FINAL
Security / Recovery              FINAL
Future LAN Path                  FINAL

Customer Normal Credit Ledger    DEFERRED
Supplier Khata / Accounts Payable FINAL
Full Accounting Ledger           OUT OF V1
```

The critical and strongly recommended shop-holder operational gaps are now incorporated into the authoritative architecture.




# 140. Quantity / Conversion Precision - Corrected

Multi-unit support tightens the quantity precision policy.

Final physical PostgreSQL precision:

```text
Base inventory quantity
numeric(18,6)

Entered/display quantity
numeric(18,6)

Unit conversion factor
numeric(18,9)

Money totals
numeric(18,2)

Unit / moving cost
numeric(18,6)
```

Reason:

```text
1 Foot = 0.3048 Meter
```

must not be rounded to three decimal places inside inventory truth.

UI may display fewer decimals according to Unit configuration, but database calculations retain full approved precision.

For SERIALIZED / individually tracked products, the whole-unit test is performed on the exact decimal conversion result before any display rounding or reduction to the persisted BaseQuantity scale. A fractional exact result is rejected rather than rounded into a physical-unit count.

For QUANTITY/LENGTH products, canonical quantity precision/rounding rules may produce the persisted numeric(18,6) BaseQuantity where necessary; that allowance must never be reused to legalize a fractional serialized physical-unit result.

Unit master may optionally define:

```text
display_decimal_places
```

so Piece/Box can display 0 while Meter/Foot can display controlled decimals.




---

# 141. Authoritative Addendum - Sales, Purchasing, Concurrency, Provenance, and Cloud Control

Sections 141 onward supersede any older contradictory notes about Sales, Purchasing, quotation conversion, idempotency, serialized cost provenance, vendor-cloud control, or remote database control.

These sections are architecture authority. Physical implementation status must be verified separately and must not be inferred from the word FINAL in an architecture rule.

---

# 142. Local Business Authority vs Edge Retails Cloud Control Plane

Edge Retails remains offline-first.

The shop PC is the authority for day-to-day business transactions:

```text
Edge Retails Desktop
        |
        v
Local Application / Domain
        |
        v
Local PostgreSQL
```

The internet must not be required for normal Sale, Purchase, Return, Inventory, Warranty, Thaka, Expense, or Reporting operations.

The online/vendor side is a separate control plane:

```text
Edge Retails Cloud
|
+-- Shop Registry
+-- License Service
+-- Device Registry
+-- Encrypted Backup Metadata / Storage
+-- Software Update Service
+-- Recovery Authorization Service
+-- Support / Diagnostics
+-- Security Audit
```

Direct remote SQL access to a shop database is forbidden.

Remote administrative operations, if introduced later, must follow:

```text
Vendor Cloud
    |
Signed / Authorized Command
    |
Local Edge Retails Worker / Application
    |
Local PostgreSQL
```

The cloud must never become a hidden second transaction authority for the local POS.

---

# 143. Online License Lifecycle

The authoritative vendor-side license lifecycle is:

```text
Issue
Activate
Bind Device
Renew
Suspend
Revoke
Replace Device
Recover
Expire
Audit
```

The shop keeps a locally verifiable signed entitlement so temporary internet loss does not stop the shop.

Recommended entitlement model:

```text
Long-lived commercial license identity
+
Shorter signed online entitlement lease
+
Offline grace policy
```

The policy must define:

- last successful entitlement refresh
- offline grace duration
- behavior after grace expiry
- device replacement authorization
- revocation propagation
- subscription expiry
- emergency vendor-side suspension

A one-day internet outage must not brick the POS.

---

# 144. License Time Trust and Key Rotation

Local wall-clock time alone must not be trusted for expiry enforcement.

The licensing subsystem should maintain trusted observations such as:

```text
LastTrustedOnlineTime
LastSuccessfulEntitlementRefresh
LastObservedLocalTime
SuspiciousBackwardClockJump
```

Backward clock movement must be detected and audited.

Signing keys must support rotation:

```text
KeyId
KeyVersion
ValidFrom
ValidUntil
RevokedKeyIds
Trust Set
Emergency Rollover
```

License, Update, Recovery, and Backup cryptographic keys remain separate trust domains.

Private signing keys must never be distributed to customer machines.

---

# 145. Cloud Data Classification and Privacy Boundary

Cloud transmission must be explicit by data class.

Default cloud operational metadata may include:

```text
ShopId
LicenseId
DeviceId
ApplicationVersion
DatabaseSchemaVersion
LastBackupStatus
BackupObjectMetadata
WorkerHealth
DiagnosticStatus
SecurityEvents
```

Normal business data remains local by default:

```text
Customer names / phones
Supplier commercial data
Invoices
Sale lines
Purchase lines
Thaka details
Profit details
Inventory commercial history
```

Encrypted full backups may contain business data, but the cloud must treat the backup as an encrypted object rather than an operational reporting database.

Retention, deletion, restore access, and support-access policies must be explicit.

---

# 146. Production Sales Aggregate

Production Sales is a real domain aggregate and must not be represented by demo ViewModel state.

Core model:

```text
Sale
SaleItem
SalePayment
SaleItemUnit
SaleReturn
SaleReturnItem
SaleReturnItemUnit
```

Sale is immutable after completion except through explicit return/reversal workflows.

Sale snapshots include:

```text
InvoiceNumber
CustomerId nullable
CashierUserId
CompletedAt
Subtotal
InvoiceDiscount
GrandTotal
PaymentStatus = PAID
ClientOperationId
ReceiptSnapshot
```

Each SaleItem snapshots:

```text
ProductId
ProductNameSnapshot
SkuSnapshot
ProductUnitId
EnteredQuantity
FactorToBaseSnapshot
BaseQuantity
UnitPrice
GrossLineTotal
AllocatedInvoiceDiscount
NetLineTotal
UnitCostSnapshot
TotalCostSnapshot
GrossProfitSnapshot
InventoryMovementId
```

POS V1 remains fully paid only. Normal customer credit is still deferred.

---

# 147. Sale Completion Authority and Pricing

The UI is never authoritative for price or inventory.

Normal Sale price authority:

```text
Product.DefaultSalePrice
x
ProductUnit.FactorToBaseUnit
=
Selected Unit Authoritative Price
```

The client may submit ExpectedUnitPrice only for stale-cart detection.

If current authoritative price differs from ExpectedUnitPrice, the command fails and the cart must be refreshed.

Sale completion pipeline:

```text
Acquire ClientOperation lock
Acquire Product resource locks in deterministic order
Validate Customer
Reload Product + ProductUnit
Validate Unit conversion
Validate Stocktake exclusion
Lock StockBalance / Units / Lots
Validate exact serialized selection
Calculate subtotal
Allocate invoice discount deterministically
Validate payment
Generate invoice number inside transaction
Persist Sale / Items / Payment
Create inventory movements
Consume cost provenance
Create Cash movement when applicable
Mark quotation converted when applicable
Audit
Commit
```

Cash tender may exceed GrandTotal and creates ChangeGiven.

Bank/Other applied amount must equal GrandTotal exactly.

---

# 148. Deterministic Invoice Discount Allocation

Invoice-level discount is allocated proportionally across SaleItems.

Rules:

- money allocation rounds to two decimals
- all but the final line use proportional rounded allocation
- final line receives the exact residual
- sum of item allocations must equal invoice discount exactly

This allocation becomes historical truth for later partial returns.

---

# 149. Sale Return - Corrected Reason, Disposition, Refund, and Cost Model

Return reason and inventory disposition are independent fields.

Reason examples:

```text
CUSTOMER_CHANGED_MIND
WRONG_ITEM
FAULT
OTHER
```

Disposition:

```text
RESTOCK_SELLABLE
DAMAGED
DEFECTIVE
SCRAP
```

Refund methods:

```text
CASH
BANK
OTHER
```

Store Credit is not part of V1.

Discounted Sales must remain returnable.

Refund truth is based on original SaleItem.NetLineTotal, not current product price.

For cumulative partial returns:

```text
TargetCumulativeRefund
=
NetLineTotal
x
CumulativeReturnedBaseQty / OriginalBaseQty
```

For non-final returns, money rounds to two decimals.

For the final remaining quantity:

```text
CurrentRefund
=
NetLineTotal - PriorRefundedAmount
```

This guarantees full return equals original NetLineTotal exactly.

Cost reversal uses the same cumulative residual principle at cost precision.

---

# 150. Sale Return Inventory and Scrap Financial Semantics

A Sale Return creates positive inventory movement into its chosen bucket.

For Sellable, Damaged, or Defective:

- carrying cost is restored using original sold cost provenance
- a new return lot is created directly in the destination bucket
- do not restore to Sellable first and then move generically

For Scrap:

- Sale COGS reversal still uses original sold cost
- returned Scrap quantity may remain visible operationally
- carrying value is zero
- RecognizedInventoryLoss equals the original returned cost amount
- a zero-carrying Scrap lot may be created for physical trace

This avoids double counting while preserving operational scrap visibility.

---

# 151. Serialized Sale and Return Provenance

Serialized Sale requires exact inventory-unit selection.

Each InventoryUnit retains full physical and financial provenance:

```text
Id (UUIDv7)
ProductId
SupplierProductId
SourcePurchaseItemId
InventoryLotId

ItemSequence
TrackingCode (DealerCode-ProductSKU-ItemSequence)

SupplierCodeSnapshot
ProductSkuSnapshot

SerialNumber nullable
Imei1 nullable
Imei2 nullable

AcquisitionCost
Status
CreatedAt
Version
```

InventoryLotId is required for exact serialized cost provenance.
SupplierProductId and ItemSequence are required for exact sequence authority.

On Sale:

```text
IN_STOCK -> SOLD
```

and the exact source lot carrying quantity is consumed.

On Sale Return, the exact originally sold unit (identified by immutable TrackingCode) must be returned.

Examples:

```text
SOLD -> IN_STOCK
SOLD -> DAMAGED
SOLD -> DEFECTIVE
SOLD -> SCRAPPED
```

A returned serialized unit receives a new return-lot link representing its new inventory state while its permanent TrackingCode remains unchanged.

---

# 152. Serialized Identity Normalization

Serial/IMEI identity values are normalized before persistence.

Minimum normalization:

```text
Trim
Uppercase invariant
Empty -> NULL
```

Within one receipt, duplicate Serial/IMEI values are rejected before persistence.

Concurrent receipts of the same identity must be serialized with an identity-scoped resource lock before checking existing inventory history.

Existing database partial unique indexes remain a final enforcement layer.

---

# 153. Production Purchasing Aggregate

Production Purchasing is:

```text
Purchase
PurchaseItem
PurchaseItemUnit
PurchaseReturn
PurchaseReturnItem
PurchaseReturnItemUnit
PurchaseVoid
```

Purchase snapshots:

```text
PurchaseNumber
SupplierId
SupplierInvoiceNumber
NormalizedSupplierInvoiceNumber
PurchaseDate
Subtotal
OtherCharges
GrandTotal
InitialPaymentAmountSnapshot nullable
InitialPaymentMethodSnapshot nullable
CreatedBy
CreatedAt
ClientOperationId
Status
```

Initial payment snapshot fields are informational/historical only. SupplierPayment + SupplierAccountEntry are the authoritative settlement and balance records.

PurchaseItem snapshots:

```text
ProductId
ProductUnitId
EnteredQuantity
FactorToBaseSnapshot
BaseQuantity
EnteredUnitCost
BaseLineTotal
AllocatedOtherCost
EffectiveBaseUnitCost
EffectiveLineCost
SalePriceAtPurchase
```

Purchase receipt does not silently redefine Product.DefaultSalePrice unless an explicit product-pricing command is approved.

SalePriceAtPurchase remains a historical commercial snapshot.

---

# 154. Supplier Invoice Duplicate Protection

Supplier invoice duplicate protection is mandatory.

Normalized key:

```text
SupplierId
+
NormalizedSupplierInvoiceNumber
```

must be unique.

Normalization minimum:

```text
Trim
Uppercase invariant
Collapse repeated spaces
```

A resource lock on the normalized supplier-invoice key must be acquired before duplicate lookup so two concurrent requests cannot both pass the pre-check.

The unique database index remains the final barrier.

---

# 155. Purchase Other-Cost Allocation and Inventory Cost

Other Charges are allocated proportionally by purchase base line value.

Allocation follows deterministic residual rounding.

For each PurchaseItem:

```text
EffectiveLineCost
=
BaseLineTotal
+
AllocatedOtherCost
```

```text
EffectiveBaseUnitCost
=
EffectiveLineCost / BaseQuantity
```

Inventory cost state uses EffectiveBaseUnitCost.

ProductCostState.LastPurchaseCost and LastPurchaseAt are transaction-derived from completed receipt.

---

# 156. Purchase Liability, Supplier Payment, and Cash Session Effects - Corrected

Supplier Khata separates commercial liability from money settlement.

Authoritative model:

```text
Purchase
= liability creation

SupplierPayment
= settlement of Supplier liability or explicit Supplier advance
```

Every completed Purchase creates a `PURCHASE / INCREASE_PAYABLE` SupplierAccountEntry for `Purchase.GrandTotal` in the same transaction.

A Purchase may have:
- no immediate payment;
- partial immediate payment;
- full immediate payment.

The Purchase aggregate is never the authoritative running balance.

Immediate payment request:

```text
InitialPaymentAmount = 0 .. Purchase.GrandTotal
Method = CASH_DRAWER | EXTERNAL when amount > 0
```

If `InitialPaymentAmount > 0`, the Purchase transaction also creates:
- a SupplierPayment;
- a `SUPPLIER_PAYMENT / DECREASE_PAYABLE` SupplierAccountEntry;
- `SUPPLIER_PAYMENT_CASH_OUT` when Method = CASH_DRAWER.

EXTERNAL settlement creates no Cash Drawer movement.

Existing Supplier credit/advance automatically offsets later Purchase liability through the derived Supplier account balance; no mutable balance field or invoice-allocation side table is required for V1.

Any older binary Purchase `SettlementMode` concept is superseded as payable authority. If a legacy field remains during implementation migration, it is compatibility metadata only and must not determine Supplier balance.

Purchase Return creates account credit using `SupplierReturnValue`, not `InventoryCostRemoved`.

Purchase Void reverses the Purchase liability charge.

Neither Purchase Return nor Purchase Void implies that cash was received. If Supplier settles a resulting account credit back to the shop, record a separate SupplierRefund transaction.

Cash movement is operational drawer truth. SupplierAccountEntry is payable truth. ProductCostState / InventoryLot provenance remains inventory-cost truth. None substitutes for another.

# 157. Purchase Return - Supplier Value vs Inventory Cost Removed

Purchase Return keeps two separate monetary truths:

```text
SupplierReturnValue
InventoryCostRemoved
```

They may differ.

Example:

```text
Supplier purchase price      5,000
Allocated freight              200
Inventory carrying cost      5,200
Supplier credit              5,000
```

Then:

```text
SupplierReturnValue = 5,000
InventoryCostRemoved = 5,200
```

Supplier return eligibility must derive from purchase-origin provenance, not from current total product stock.

Serialized Purchase Return requires exact units whose SourcePurchaseItemId matches the returned PurchaseItem.

---

# 158. Strict Purchase Void Eligibility

Purchase Void is for correcting a mistaken completed receipt, not for normal commercial returns.

A Purchase is voidable only if:

- no Purchase Return exists
- every purchase-origin quantity is still available in the original eligible state
- no purchase-origin quantity has ever been consumed
- no serialized unit from the purchase has downstream history
- stocktake does not block affected products
- required cash reversal can be recorded when purchase was drawer-paid

Current stock availability alone is not sufficient.

Returning stock later must not erase historical consumption.

---


# 159. Immutable Lot Consumption Ledger

Inventory requires an immutable lot-consumption provenance ledger.

Logical entity:

```text
InventoryLotConsumption
```

Fields:

```text
Id
LotId
MovementId
Quantity
UnitCostSnapshot
TotalCostSnapshot
OccurredAt
```

Every outbound consumption that removes quantity from a cost-bearing lot must create one or more LotConsumption rows.

Examples:

```text
Sale
Thaka Issue
Purchase Return
future other cost-bearing outbound operations
```

This ledger supports:

- Sale COGS provenance
- exact lot trace
- forensic reporting
- Purchase Void "ever consumed" checks
- deterministic reversals
- serialized acquisition trace

Lot consumption rows are append-only and are never deleted merely because goods later return.

---

# 160. Idempotency and Concurrent Duplicate Protection

Every critical write command uses ClientOperationId.

Examples:

```text
CompleteSale
CreateSaleReturn
CreatePurchase
CreatePurchaseReturn
VoidPurchase
Quotation conversion commit
```

Database unique ClientOperationId constraints remain mandatory.

To close the simultaneous-request race, PostgreSQL transaction-scoped advisory locks are used:

```text
Acquire operation lock(ClientOperationId)
Check existing result
If found -> return existing result
Else -> execute command
```

A retry after commit must return the already-created business result rather than create a duplicate.

Idempotency identity is bound to command meaning, not only to a GUID. The persisted/reconstructable operation record must bind at minimum:

```text
ClientOperationId
CommandType
CanonicalPayloadFingerprint
ActorId / TerminalId where applicable
CommittedResultIdentity / deterministic result payload
CommittedAt
RetentionUntil or an equivalent retention class
```

Reusing the same ClientOperationId with a different CommandType or payload fingerprint is rejected as an operation-identity mismatch; it must never return an unrelated prior result.

The retention horizon for replay-safe authoritative operations must be longer than the maximum supported ambiguous-response / terminal reconnect / operator-recovery horizon. Purging an unresolved or still-replayable operation identity is forbidden. See Section 233.1, subsection L.

---

# 161. Deterministic Product Resource Locking

All stock-affecting multi-product commands acquire product resource locks in sorted ProductId order.

Pattern:

```text
Distinct ProductIds
Sort ascending
Acquire transaction-scoped product advisory locks
Then lock rows / lots / units
```

Applies to:

```text
Complete Sale
Sale Return
Create Purchase
Purchase Return
Purchase Void
Stocktake Start / Count window
other stock-affecting commands
```

This provides a stable cross-module lock order and reduces deadlock risk.

Cash-session locks are acquired after inventory/product locking in commands that need both inventory and cash.

---

# 162. Stocktake Concurrency Barrier

When Stocktake begins for a scope, affected Product resource locks are acquired in deterministic order before the counting snapshot is finalized.

Stock-affecting commands:

1. acquire the same Product resource lock
2. re-check active COUNTING Stocktake after inventory row lock
3. fail if the product is blocked

This closes the race where a Sale/Purchase starts just before Stocktake becomes COUNTING.

---

# 163. Quotation to Sale - Single Sale Engine

Quotation conversion must not use a separate sale engine.

Correct architecture:

```text
Quotation
  |
PrepareQuotationForSale
  |
Validated Sale Draft
  |
CompleteSale
  |
Normal Sale transaction pipeline
```

PrepareQuotationForSale does not mutate stock, cash, COGS, or revenue.

It validates:

- quotation status = ISSUED
- not expired
- product still active
- selected unit still active and sellable
- conversion factor unchanged
- quoted price snapshot retained

Final conversion occurs only inside CompleteSale.

CompleteSale revalidates stock, exact serial selection, customer, unit, discount, and payment.

On successful commit:

```text
Quotation -> CONVERTED
ConvertedSaleId = Sale.Id
```

Double conversion is forbidden.

The old direct IQuotationSaleConverter approach is superseded.

---

# 164. Backend-Authoritative Receipt Snapshot

Historical Sale receipts must not depend on current shop settings.

At Sale commit, backend captures immutable receipt/shop snapshot data such as:

```text
ShopName
Address
Phone
ReceiptHeader
ReceiptFooter
Tax / registration identifiers if configured
TemplateVersion
```

The snapshot must come from trusted application/system settings, not arbitrary caller-provided text.

Later receipt reprint uses the historical Sale snapshot.

---


# 165. Business Audit for Sales and Purchasing

Critical Sale/Purchase commands require append-only business audit events in the same database transaction.

Minimum audit event shape:

```text
AuditEventId
OccurredAt
ActorUserId
Action
EntityType
EntityId
CorrelationId / ClientOperationId
Reason
BeforeJson optional
AfterJson optional
Machine / Session metadata where available
```

Mandatory examples:

```text
SALE_COMPLETED
SALE_RETURN_CREATED
PURCHASE_COMPLETED
PURCHASE_RETURN_CREATED
PURCHASE_VOIDED
QUOTATION_ISSUED
QUOTATION_CANCELLED
QUOTATION_CONVERTED
SERIALIZED_IDENTITY_RECEIVED
```

Audit is not a substitute for immutable domain records. It is an additional forensic trail.

Audit records must not be deleted by normal business workflows.

---

# 166. Sales and Purchasing Read-Side Contract

CQRS remains explicit.

Write handlers mutate domain truth.

Read-side queries use Dapper/raw SQL DTO projections where useful.

Required backend query families:

```text
GetSalesHistory
GetSaleDetail
GetSaleReturnHistory
GetPurchaseHistory
GetPurchaseDetail
GetPurchaseReturnHistory
GetSupplierPurchaseHistory
GetProductPurchaseProvenance
GetProductSaleHistory
GetSerializedUnitHistory
GetLotConsumptionTrace
GetQuotationList
GetQuotationDetail
```

Read DTOs may join reporting-friendly data, but must never become write authority.

---

# 167. EF Core InitialProductionBaseline Strategy After Sales/Purchasing Closure

Because no production Edge Retails PostgreSQL database is established as an older applied migration authority, the first production schema baseline must represent the complete approved V1 architecture.

The authoritative baseline is:

```text
EF Core migration
Name: InitialProductionBaseline
Scope: complete approved V1 schema
```

InitialProductionBaseline must include, at minimum:

```text
Catalog / Product Units
Parties
Inventory ledger
Inventory lots / bucket balances
Lot consumptions
Serialized units
Stocktake
Warranty
Sales / Sale Returns / Quotations
Purchasing / Purchase Returns / Purchase Void
Finance Cash Sessions
Supplier Khata structures
System document sequences
Required audit structures
Required constraints / filtered unique indexes
```

Do not introduce a parallel handwritten numbered SQL baseline.

Before release:

```text
EF model -> migration drift = zero
InitialProductionBaseline Up = pass
Down-to-zero in disposable test DB = pass
Up again = pass
Real PostgreSQL integration tests = pass
```

Never rewrite an already-deployed production migration. The fresh-baseline rule is valid only while no production database depends on an older migration history.

---

# 168. Backend-Only Closure Boundary

Frontend integration is explicitly outside the current backend closure task.

The backend must be considered complete independently of WPF demo services only when:

- domain rules are implemented
- persistence model is complete
- migrations are valid
- idempotency is enforced
- resource-lock order is deterministic
- inventory/cost provenance is complete
- cash side effects are atomic
- audit is append-only
- read-side queries exist
- real PostgreSQL tests cover the critical cross-module paths

Frontend demo behavior must not be used as evidence of backend correctness.

---

# 169. Historical Sales/Purchasing Closure Milestone

This section records a historical closure milestone. Its domain rules remain applicable where not superseded, but it is not the current architecture declaration. See Section 228.

The following architecture rules are now authoritative:

```text
Local PostgreSQL business authority             FINAL
Offline-first shop operation                    FINAL
Cloud vendor control-plane boundary             FINAL
License lifecycle / revocation / rotation path  FINAL DESIGN
Sales aggregate                                 FINAL
Sale completion authority                       FINAL
Sale return residual refund/cost model          FINAL
Purchasing aggregate                            FINAL
Purchase settlement semantics                   FINAL
Purchase return dual-value model                FINAL
Strict Purchase Void provenance rule            FINAL
Multi-unit transaction snapshots                FINAL
Serialized exact-lot provenance                 FINAL
Immutable lot-consumption ledger                FINAL
ClientOperationId idempotency                   FINAL
Product resource-lock order                     FINAL
Stocktake concurrency barrier                   FINAL
Quotation-to-Sale single-engine rule            FINAL
Backend receipt snapshot authority              FINAL
Business audit requirement                      FINAL
Sales/Purchasing read-side contract              FINAL
EF Core InitialProductionBaseline strategy       FINAL WHILE NO PROD DB EXISTS

Normal Customer Credit / Udhaar                 DEFERRED
Supplier Khata / Accounts Payable               FINAL
General Ledger / Double Entry                   OUT OF V1
Frontend production integration                 SEPARATE TASK
```

This addendum supersedes any older contradictory Sales/Purchasing or cloud-control notes.

---

---

# 170. Dealer -> Product -> Physical Item Traceability System - Final Foundation

## 170.1 Business Objective
Edge Retails assigns a permanent, human-readable business identity to every individually tracked physical unit.

Canonical visible format:

```text
DealerCode-ProductSKU-ItemSequence
```

Example:

```text
AB1-PKF-DLX56-000001
```

Meaning:

- `AB1` = WHO supplied the item.
- `PKF-DLX56` = WHAT product/model it is.
- `000001` = WHICH exact physical unit it is within that Supplier + Product relationship.
- `InventoryUnit.Id` remains the internal UUIDv7 relational identity.

## 170.2 Dealer vs Supplier
`Dealer` is business/UI terminology. The backend authority remains the existing `Supplier` aggregate in `parties.suppliers`.

No parallel Dealer aggregate/table is introduced.

`Purchase.SupplierId` remains the authoritative commercial supplier provenance.

## 170.3 Lifetime Provenance
The physical unit must remain traceable through:

```text
Supplier
-> Purchase
-> PurchaseItem
-> InventoryLot
-> InventoryUnit / TrackingCode
-> Inventory movements
-> Sale / SaleItemUnit
-> Sale Return
-> Warranty
-> Purchase Return / Replacement / Scrap
```

TrackingCode supplements this provenance. It never replaces relational foreign keys, InventoryLot, Serial/IMEI, or UUIDv7.

---

# 171. Supplier DealerCode Generation and Permanence

## 171.1 DealerCode Rule
Every Supplier receives a permanent DealerCode:

```text
First two normalized alphabetic characters of Supplier.Name
+
prefix-scoped positive integer
```

Examples:

```text
Abdullah Electronics -> AB1
Abdullah Traders     -> AB2
Abbas Electrical     -> AB3

Ali Electronics      -> AL1
Ali Traders          -> AL2
```

The suffix is variable length:

```text
AB1 ... AB9, AB10, AB11, ...
```

DealerCode is therefore not a fixed three-character code.

## 171.2 Normalization
1. Trim whitespace.
2. Ignore punctuation/non-alphabetic characters while locating the first two usable Latin letters.
3. Convert the two-letter prefix to uppercase invariant.
4. If two usable Latin letters cannot be derived, require an explicit two-letter uppercase prefix. Do not silently generate an unstable fallback.

## 171.3 Sequence Authority and Concurrency
A persistent prefix counter is maintained, conceptually:

```text
system.supplier_code_sequences
prefix       varchar(2) PK
next_value   bigint NOT NULL
```

Generation must use the existing application `IResourceLock` / PostgreSQL transaction-scoped advisory-lock abstraction with resource key:

```text
supplier-code-prefix:<PREFIX>
```

Then lock/read/update the prefix counter in the same transaction.

Unsafe unlocked `MAX(...) + 1` generation is forbidden.

## 171.4 Immutability and Reuse
DealerCode:
- is globally case-insensitively unique;
- never changes after commit;
- does not change when Supplier.Name changes;
- is never recycled after deactivation;
- remains reserved permanently for historical traceability.

Suppliers with business history are deactivated, not deleted.

---

# 172. Product SKU Authority, Issuance, and Historical Uniqueness

## 172.1 SKU Role
Product SKU identifies the Product/model, not a physical piece.

The same Product supplied by AB1, AB2, and other Suppliers keeps one ProductId and one SKU.

## 172.2 Issuance Workflow
1. System may suggest a SKU from Brand/Name/Model.
2. Operator confirms or edits the suggestion.
3. Backend normalizes it to the approved character/format policy.
4. Backend enforces case-insensitive global uniqueness.
5. Once the Product is referenced by `SupplierProduct` or any business transaction, the SKU becomes immutable.

Silent collision rewriting is forbidden.

## 172.3 Historical Non-Reuse
A SKU is never reassigned to a different Product, including after the original Product is inactive or discontinued.

No uniqueness filter may exclude inactive Products from SKU uniqueness.

## 172.4 Product Master vs Inventory
The Product aggregate remains catalog/master data. Product creation creates zero stock, zero lots, zero movements, zero InventoryUnits, and zero TrackingCodes.

---

# 173. SupplierProduct Relationship and Permanent Sequence Authority

## 173.1 Required Relationship
A dedicated catalog relationship is introduced:

```text
SupplierProduct
- Id UUIDv7
- SupplierId UUID FK -> parties.suppliers
- ProductId UUID FK -> catalog.products
- NextItemSequence BIGINT NOT NULL DEFAULT 1
- IsActive BOOLEAN NOT NULL DEFAULT TRUE
- CreatedAt TIMESTAMPTZ
- UpdatedAt TIMESTAMPTZ
- Version BIGINT concurrency token
```

Constraint:

```text
UNIQUE(SupplierId, ProductId)
CHECK(NextItemSequence >= 1)
```

## 173.2 Lifecycle
A SupplierProduct row is never hard-deleted through normal application workflows.

Use:

```text
DeactivateSupplierProduct
ReactivateSupplierProduct
```

Reactivation always reuses the same row and its existing `NextItemSequence`.

The sequence never resets.

## 173.3 Creation Timing
SupplierProduct may be created explicitly from Product/Supplier management before stock exists.

Inventory receipt requires an existing active Supplier and Product. The receiving workflow may establish a missing SupplierProduct mapping only if the architecture-approved application command explicitly performs that link inside the same transaction. It must never create a Product silently.

---

# 174. Physical Item Sequence - BIGINT with Minimum Six-Digit Display

## 174.1 Storage and Display
`ItemSequence` is stored as a positive BIGINT.

Display uses minimum six-digit zero padding:

```text
1        -> 000001
2        -> 000002
42       -> 000042
999999   -> 999999
1000000  -> 1000000
1000001  -> 1000001
```

Six digits are a minimum display width, not a maximum range.

There is no artificial 999,999 rollover/exhaustion rule.

Constraint:

```text
CHECK(ItemSequence >= 1)
```

## 174.2 Scope
Sequence is scoped strictly to one `SupplierProduct` pair.

It does not reset for:
- a new Purchase;
- a new invoice;
- a new day;
- a new month/year;
- deactivation/reactivation.

## 174.3 Permanent Non-Reuse
Committed sequence numbers are never reused, including after Purchase Void or later stock disposition.

---

# 175. TrackingCode Format and Relational Authority

## 175.1 Formula
```text
TrackingCode =
DealerCode + "-" + ProductSKU + "-" + FormatMinimum6(ItemSequence)
```

Examples:

```text
AB1-PKF-DLX56-000001
AB1-PKF-DLX56-999999
AB1-PKF-DLX56-1000000
AB2-PKF-DLX56-000001
```

## 175.2 Properties
TrackingCode is:
- globally unique;
- normalized uppercase;
- immutable after successful receipt commit;
- never reused;
- suitable for barcode/QR/search/display.

## 175.3 Anti-Parsing Rule
Business logic must never parse TrackingCode to recover relational identity.

Relational truth comes from:
- ProductId;
- SupplierProductId;
- SourcePurchaseItemId;
- InventoryLotId;
- ItemSequence.

TrackingCode is a business-visible lookup identity.

---

# 176. InventoryUnit Traceability and Origin Model - Corrected

The existing `InventoryUnit` / PostgreSQL `inventory.units` table remains the exact physical identity registry. No replacement `inventory.inventory_units` table is created.

InventoryUnit identity can outlive current shop ownership, as already occurs after Sale. Therefore a customer-specific Warranty replacement may receive an InventoryUnit identity without becoming shop stock.

Required traceability fields conceptually include:

```text
InventoryUnit
- Id UUIDv7
- ProductId
- SupplierProductId
- OriginType PURCHASE | WARRANTY_REPLACEMENT | STOCK_ADJUSTMENT
- SourcePurchaseItemId nullable
- SourceWarrantyClaimItemId nullable
- SourceWarrantyCaseId nullable
- SourceStockAdjustmentItemId nullable
- InventoryLotId nullable
- ItemSequence BIGINT
- TrackingCode
- SupplierCodeSnapshot
- ProductSkuSnapshot
- SerialNumber nullable
- Imei1 nullable
- Imei2 nullable
- AcquisitionCost / carrying-cost snapshot as applicable
- Status
- CreatedAt
- Version
```

Origin invariants:

```text
OriginType = PURCHASE
=> SourcePurchaseItemId required
=> SourceWarrantyClaimItemId null
=> normal Purchase/Lot provenance applies

OriginType = WARRANTY_REPLACEMENT
=> SourcePurchaseItemId null
=> SourceStockAdjustmentItemId null
=> exactly one Warranty source is required:
   SourceWarrantyClaimItemId for customer-claim replacement
   OR SourceWarrantyCaseId for shop-owned WarrantyCase replacement
=> no fake Purchase/PurchaseItem is created

OriginType = STOCK_ADJUSTMENT
=> SourcePurchaseItemId null
=> Warranty source ids null
=> SourceStockAdjustmentItemId required
=> applies only to an authorized positive exact-unit Stock Adjustment / Opening Stock / forensic recovery intake
=> SupplierProductId remains required for any TrackingCode-bearing unit
=> the source Supplier/SupplierProduct is provenance + sequence authority only; the adjustment does not fabricate a Purchase or Supplier Khata payable
```

`SupplierProductId` remains the Supplier/Product sequence authority for any individually tracked replacement that receives a TrackingCode.

Do not duplicate a direct SupplierId on InventoryUnit unless later measured query requirements justify denormalization and a database invariant guarantees consistency.

For Purchase-origin units, Supplier provenance remains independently cross-checkable through `SourcePurchaseItem -> Purchase -> Supplier` and `SupplierProduct -> Supplier`.

For Warranty-origin replacements, Supplier provenance is cross-checkable through the applicable Warranty source (`SourceWarrantyClaimItem -> WarrantyClaim.SupplierId` or `SourceWarrantyCase -> WarrantyCase.SupplierId`) and independently through `SupplierProduct -> Supplier`.

Customer-owned warranty replacement identities do not participate in StockBalance, LotBucketBalance, or ProductCostState merely because their InventoryUnit row exists.

# 177. Tracking Policy, Serial/IMEI, and Multi-Unit Base Quantity

## 177.1 Existing Tracking Modes Remain
Preserve the existing authoritative Product tracking modes:

```text
QUANTITY
LENGTH
SERIALIZED
```

Do not introduce parallel enums such as BULK, QUANTITY_ONLY, or LENGTH_MEASURED.

## 177.2 Edge Tracking Identity vs Manufacturer Identity
For individually tracked units:
- TrackingCode is the Edge Retails physical business identity.
- SerialNumber/IMEI are optional manufacturer/device identities according to Product policy.
- TrackingCode does not replace Serial/IMEI, and Serial/IMEI do not replace TrackingCode.

## 177.3 Base-Quantity Allocation
Tracking items are allocated by base physical quantity:

```text
BaseQuantity = EnteredQuantity * FactorToBaseUnit
```

Example:

```text
2 cartons * 6 fans/carton = 12 base pieces
=> 12 InventoryUnits
=> 12 TrackingCodes
```

For individually tracked products, the exact pre-rounding conversion result must be a positive whole count. BaseQuantity is that exact whole count; rounding/truncation cannot be used to make a fractional result acceptable.

The number of InventoryUnits/TrackingCodes created, selected, or replaced must equal that exact BaseQuantity.

QUANTITY/LENGTH products continue using lot/balance provenance without unnecessary InventoryUnit-per-subunit generation.

---

# 178. Existing CreatePurchase Engine Owns Inventory Receiving

## 178.1 No Parallel Goods-Receipt Write Engine
The Inventory screen `+` action is a UI entry point into the existing authoritative Purchasing engine.

It must route to the existing `CreatePurchaseCommand` / `CreatePurchaseHandler` semantics rather than introduce a second independent `PostGoodsReceiptCommand` transaction engine.

A one-product Inventory `+` dialog may pre-fill a one-line CreatePurchase request, but all purchasing, costing, inventory, idempotency, cash, and audit rules remain those of the single Purchase engine.

## 178.2 Transaction Flow
Within the existing Purchase transaction:

1. Acquire ClientOperationId lock.
2. Acquire supplier-invoice lock where applicable.
3. Acquire deterministic sorted Product resource locks.
4. Reload and validate Product/ProductUnit/Supplier relationship needed for the request.
5. Convert entered quantity using the authoritative snapshotted `FactorToBaseUnit`.
6. For individually tracked products, validate the exact pre-rounding converted BaseQuantity is a positive whole count; reject before SupplierProduct sequence allocation if not.
7. Acquire deterministic SupplierProduct sequence locks only for valid tracked-unit demand.
8. Reload/validate SupplierProduct sequence authority under lock.
9. For individually tracked products, reserve one contiguous sequence range sized exactly to whole BaseQuantity.
10. Advance `NextItemSequence` once per affected SupplierProduct in the same transaction.
11. Validate captured exact physical identity cardinality where required by the Product policy/workflow.
12. Create Purchase/PurchaseItem.
13. Create existing `inventory.movements`, `inventory.lots`, `inventory.lot_bucket_balances`, and update `inventory.stock_balances` / `inventory.cost_states`.
14. Create existing `inventory.units` and `purchasing.purchase_item_units` where individual tracking applies.
15. Persist any required ORIGINAL label print requests atomically with the committed identities according to Sections 51.1-51.3.
16. Save and commit atomically.

## 178.3 TrackingCode Issue Barrier
TrackingCodes are computed/reserved and InventoryUnit rows are created inside the transaction.

They become officially issued/externally visible only after successful commit.

Labels/QR codes must not be printed before commit.

Automatic label/QR printing must use the durable post-commit print-delivery contract in Sections 51.1-51.3 rather than printing inside the database transaction.

A reprint always reuses the already committed InventoryUnit/TrackingCode identity and never consumes another SupplierProduct sequence value.

## 178.4 SupplierProduct Sequence Critical-Section Discipline

Sequence correctness remains owned by the same `SupplierProduct.NextItemSequence` authority. Performance hardening must not create a second sequence allocator, cached authority, or unlocked pre-allocation path.

Before entering the authoritative locked transaction, the application may perform only non-authoritative preparation such as:

```text
request-shape validation
quantity/unit arithmetic preparation
normalization
deterministic sorting of resource keys
permission/input prechecks that will be revalidated where authoritative state matters
```

After the transaction begins, resource locks must still follow Section 185 canonical ordering.

For each affected SupplierProduct in one command:

```text
acquire SupplierProduct resource/row lock once
reload authoritative NextItemSequence
calculate exact required tracked-unit count
reserve one contiguous sequence range
advance NextItemSequence once
create all corresponding InventoryUnit / TrackingCode rows
persist required Purchase/Inventory state
commit
```

Do not acquire/release the same SupplierProduct sequence lock once per physical unit.

While authoritative transaction locks are held, the command must not perform:

```text
printer or scanner device I/O
cloud/network API calls unrelated to PostgreSQL
filesystem export
interactive user prompts
report rendering
email/SMS/webhook delivery
arbitrary sleep/backoff
post-commit label/receipt work
```

Database work needed for the atomic Purchase, Inventory, Cost, Khata/Cash effect, Audit, and idempotency remains inside the transaction.

The objective is minimum necessary lock duration without moving correctness-critical validation or persistence outside the transaction.

---

# 179. Purchase Void and Permanent Historical Identity

Existing strict Purchase Void rules remain authoritative.

If an eligible Purchase is voided after a successful original commit:
- InventoryUnits move to existing `ReceiptVoided` semantics;
- inventory/cost/cash reversal follows the existing Purchase Void engine;
- TrackingCode and ItemSequence remain historical;
- SupplierProduct.NextItemSequence never moves backwards;
- no previously committed TrackingCode is ever reissued.

---

# 180. Sale Integration Using Existing SaleItemUnit

The existing `sales.sale_item_units` relation remains authoritative. No replacement/duplicate table is created.

For individually tracked Sales:
- select/scan exact InventoryUnit by TrackingCode or internal Id;
- validate Product and current unit status;
- preserve stocktake and deterministic locking rules;
- create existing SaleItemUnit link;
- transition InventoryUnit using existing status semantics;
- preserve exact InventoryLotConsumption provenance.

A TrackingCode snapshot may be added only as a historical/read convenience if justified; `InventoryUnitId` remains relational truth.

---

# 181. Sale Return Preserves Identity and Existing Five-Bucket Semantics

Sale Return never creates a new TrackingCode for the same returned physical object.

The exact InventoryUnit retains its TrackingCode.

Return disposition continues to use the existing authoritative inventory model:

```text
SELLABLE
DAMAGED
DEFECTIVE
SCRAP
```

and `WITH_SUPPLIER` when later custody moves upstream through the appropriate warranty/supplier workflow.

Do not introduce competing states such as `DEFECTIVE_PENDING_RETURN` or `DEFECTIVE_PENDING_CLAIM`.

Existing immutable lot-consumption and return-lot rules remain authoritative.

---

# 182. Purchase Return Derives the Original Supplier from Provenance

For an exact tracked unit, backend derives the upstream Supplier from:

```text
InventoryUnit
-> SourcePurchaseItem
-> Purchase
-> SupplierId
```

and cross-validates the same Supplier through `SupplierProduct.SupplierId`.

The client does not choose an arbitrary target Supplier when authoritative origin exists.

Wrong-supplier return is rejected by design.

Existing SupplierReturnValue vs InventoryCostRemoved semantics remain unchanged.

---

# 183. Warranty Replacement Identity Allocation - Corrected

TrackingCode is a primary operational lookup key for exact-unit Warranty work, while Warranty aggregates and relational foreign keys remain authority.

A replacement physical object always receives a different TrackingCode from the original when the Product policy requires individual tracking.

There are two legitimate TrackingCode allocation entry paths:

```text
A. Normal Purchase receipt
-> CreatePurchase transaction
-> SupplierProduct sequence
-> InventoryUnit + TrackingCode

B. Warranty replacement receipt
-> authorized Warranty replacement transaction
-> same SupplierProduct sequence
-> InventoryUnit + TrackingCode
```

There is no third/manual/random sequence source.

Both allocation paths obey:
- `SupplierProduct.NextItemSequence` as the only sequence authority;
- BIGINT monotonic sequence;
- minimum six-digit display formatting;
- committed non-reuse;
- deterministic `IResourceLock / PostgresOperationLock` locking;
- TrackingCode creation inside the transaction;
- official issuance/label printing only after successful commit.

Existing `warranty.claim_item_units` links OriginalInventoryUnitId and ReplacementInventoryUnitId for customer claims.

Shop-owned Warranty Cases preserve equivalent old/new physical-unit linkage through their warranty-case/inventory movement references.

Customer-specific replacement:
- uses `OriginType = WARRANTY_REPLACEMENT`;
- starts in non-stock `WARRANTY_CUSTOMER_HELD` lifecycle state;
- becomes `WARRANTY_CUSTOMER_HANDED_OVER` only on actual customer handover;
- never creates SELLABLE quantity or shop carrying cost merely due to receipt.

Shop-owned replacement:
- replaces the recoverable old physical object;
- transfers the approved original carrying cost to the new physical unit;
- enters the approved shop-owned inventory bucket;
- does not create a fake Purchase or double carrying cost.

The original and replacement TrackingCodes must differ, and the original committed TrackingCode is never reused.

# 184. Quantity and Length Product Exemption

QUANTITY and LENGTH products normally do not create one InventoryUnit/TrackingCode per tiny sub-unit.

They continue using:
- `inventory.lots`;
- `inventory.lot_bucket_balances`;
- `inventory.stock_balances`;
- `inventory.movements`;
- `inventory.lot_consumptions`.

Individual TrackingCode allocation applies only where Product tracking policy requires exact physical-unit identity.

---

# 185. Single Deterministic Locking System - Warranty and Supplier Account Integrated

No second raw advisory-lock convention is introduced.

All logical resource locks use the existing `IResourceLock` / `PostgresOperationLock` abstraction and participate in one global deterministic order.

Canonical logical order, skipping irrelevant resources for a command:

```text
1. ClientOperationId
2. WarrantyClaimId resource keys, sorted
3. Supplier invoice / Supplier-code-prefix resource where applicable
4. ProductId resource keys, sorted
5. SupplierProductId resource keys, sorted
6. SupplierAccount SupplierId resource keys, sorted
7. Warranty SaleItem / InventoryUnit resource keys, sorted
8. normalized Serial / IMEI / Tracking identity keys, sorted
9. required PostgreSQL row locks
10. CashSession
```

Examples:
- Purchase with immediate payment locks Product/SupplierProduct resources before SupplierAccount, then CashSession.
- Supplier-only payment locks SupplierAccount before CashSession.
- Customer Warranty claim creation locks the claim/unit eligibility resources before persistence.
- Warranty replacement locks WarrantyClaim, Product/SupplierProduct sequence authority, then exact-unit resources.
- Warranty monetary credit locks WarrantyCase/claim context, inventory Product/units, SupplierAccount, and CashSession only if a separate cash settlement is also part of the same explicitly designed operation.

A handler must not acquire the same resource families in a conflicting order.

This ordering is intended to prevent known application-level lock inversions and minimize deadlock risk. It is not a claim that PostgreSQL can never detect a deadlock.

## 185.1 Lock-Wait Timeout and Retry Policy

Commands that acquire authoritative PostgreSQL locks must not wait forever without an application-visible outcome.

Use a bounded, operation-scoped lock-wait policy. Where PostgreSQL `lock_timeout` is used, set it for the operation/transaction scope rather than as an indiscriminate cluster-wide default.

If both `lock_timeout` and a broader statement/command timeout are configured, the lock timeout must be meaningfully lower than the broader timeout so lock contention can be identified distinctly.

`deadlock_timeout` remains a PostgreSQL deadlock-detection setting. It is not an application retry timer and must not be treated as a substitute for deterministic lock ordering.

Timeout values are deployment/performance configuration, not business constants. They require measured local-LAN/local-PostgreSQL validation.

On lock timeout, deadlock detection, connection loss with ambiguous outcome, or another explicitly classified transient concurrency failure:

```text
ROLL BACK current transaction if still active
do not retry a partial SQL fragment independently
retry only the complete idempotent business operation
reuse the same ClientOperationId
re-read authoritative state
re-acquire locks in canonical order
revalidate all affected business invariants
```

Automatic retry must be bounded. It must not loop indefinitely.

A non-idempotent command without a safe operation identity must not be automatically retried after an ambiguous outcome.

If the original operation actually committed but the response was lost, replay with the same `ClientOperationId` must resolve to the original committed result/effect rather than create a duplicate Purchase, payment, inventory movement, sequence allocation, or TrackingCode.

Backoff/scheduling occurs outside the rolled-back/finished database transaction. Do not sleep while holding authoritative locks.

## 185.2 Lock Contention Observability

Production diagnostics must be able to measure, at minimum:

```text
OperationName
CorrelationId / ClientOperationId where safe
waited resource family
lock-wait duration
total transaction duration
timeout/deadlock outcome
retry count
success/failure result
affected SupplierProduct count
tracked-unit sequence range size where applicable
```

Do not log secrets, raw credentials, or unnecessary customer-sensitive data.

Operational thresholds should be based on measured p50/p95/p99 behavior on representative shop hardware and realistic concurrent workloads.

A universal statement such as "SupplierProduct lock must always be held under 50 ms" is not a canonical invariant. The architecture requires bounded waits, minimized critical sections, deterministic locking, and measurable performance evidence.

# 186. PostgreSQL Schema Alignment - Extend Existing Tables Only

Required traceability schema changes must align with the current EF/PostgreSQL names.

Existing tables to reuse include:

```text
catalog.products
catalog.units
catalog.product_units
catalog.product_unit_barcodes

parties.suppliers

inventory.stock_balances
inventory.cost_states
inventory.movements
inventory.movement_effects
inventory.movement_units
inventory.units
inventory.lots
inventory.lot_bucket_balances
inventory.lot_consumptions
inventory.stocktakes
inventory.stocktake_items
inventory.stocktake_unit_checks

purchasing.purchases
purchasing.purchase_items
purchasing.purchase_item_units
purchasing.returns
purchasing.return_items
purchasing.return_item_units
purchasing.purchase_voids

sales.sale_item_units

warranty.claims
warranty.claim_items
warranty.claim_item_units
warranty.claim_events
warranty.shop_stock_cases

audit.business_events
```

New required structures are limited to genuine missing concepts, principally:
- Supplier.DealerCode;
- normalized permanent Product SKU enforcement;
- `catalog.supplier_products`;
- Supplier-code prefix sequence authority;
- traceability columns/indexes on existing `inventory.units`.

Do not create parallel tables such as `inventory.inventory_units`, `inventory.inventory_lots`, `inventory.inventory_stock_summaries`, or `audit.audit_logs`.

---

# 187. Traceability Read-Side Contracts

Required read contracts include:

```text
GetTrackingItemByCode
GetSupplierProducts
GetProductSuppliers
GetSupplierProductItems
GetProductPhysicalItems
GetPhysicalItemHistory
GetSupplierItemHistory
```

Physical item history is a read projection derived from authoritative sources such as:
- Purchase/PurchaseItem;
- inventory movements/movement_units;
- InventoryUnit;
- SaleItemUnit;
- Sale Returns;
- Warranty claims/events;
- Purchase Returns.

Do not create a duplicate physical-event truth table unless a future requirement proves it necessary.

Cost/profit fields remain permission-sensitive.

---

# 188. Audit Responsibilities Without Duplicate Event Truth

Continue using the existing `audit.business_events` model.

BusinessAudit records significant human/business actions such as:
- DealerCode assignment;
- SKU issuance/correction before lock;
- SupplierProduct activation/deactivation;
- Purchase/Return/Void;
- high-risk tracking administration.

Physical inventory lifecycle truth remains in:
- commercial aggregates;
- `inventory.movements`;
- unit-link tables;
- warranty events.

Do not mirror every movement into a separate `audit.audit_logs` lifecycle ledger.

`GetPhysicalItemHistory` composes the timeline from authoritative sources.

---

# 189. Mandatory Traceability Test Suite

The architecture requires tests covering at least:

1. Dealer prefix generation: Abdullah -> AB1, next AB supplier -> AB2.
2. Concurrent same-prefix Supplier creation cannot duplicate DealerCode.
3. DealerCode survives Supplier rename and is never reused.
4. SKU uniqueness is case-insensitive across active and inactive Products.
5. SKU cannot be reused by a different Product.
6. SupplierProduct pair is unique.
7. SupplierProduct deactivation/reactivation preserves the same row and sequence.
8. SupplierProduct is not hard-deleted through normal workflows.
9. Sequence display: 1 -> 000001.
10. Sequence display: 999999 -> 999999.
11. Sequence growth: 1000000 -> 1000000.
12. Concurrent receipts for one SupplierProduct allocate non-overlapping ranges.
13. Purchase rollback creates no committed units/codes and no labels are issued.
14. Purchase Void never reuses committed sequences.
15. Multi-unit receipt allocates exactly BaseQuantity physical identities.
16. Serialized/individual TrackingCode coexists with Serial/IMEI.
17. Quantity/Length receiving does not create unnecessary InventoryUnits.
18. Exact Sale preserves TrackingCode through existing SaleItemUnit.
19. Sale Return preserves the same TrackingCode and existing bucket semantics.
20. Purchase Return derives/cross-validates original Supplier.
21. Warranty replacement receives a different TrackingCode and links old/new units.
22. TrackingCode lookup returns complete provenance.
23. Existing schema tables are extended rather than duplicated.
24. Existing IResourceLock ordering is respected.
25. SERIALIZED conversion 1.25 Cartons x factor 4 yields exact BaseQuantity 5 and requires exactly 5 physical identities.
26. SERIALIZED conversion 1.10 Cartons x factor 4 yields exact BaseQuantity 4.40 and is rejected before sequence allocation.
27. Fractional tracked result cannot be accepted through rounding, truncation, floor, ceiling, or tolerance/epsilon logic.
28. Tracked receiving with identity count different from BaseQuantity is rejected atomically.
29. Tracked Sale/Thaka issue selects exactly BaseQuantity physical identities and rejects fractional exact-unit demand.
30. Tracked positive Stock Adjustment/opening stock cannot create a fractional InventoryUnit.
31. QUANTITY/LENGTH fractional BaseQuantity continues through lot/balance provenance without per-fraction InventoryUnits.

---

# 190. Migration and Implementation Gate

The project remains under the existing fresh-baseline rule while no production database authority requires an older migration chain.

Traceability schema changes must be incorporated into/regenerated within the existing EF Core InitialProductionBaseline strategy, not through any parallel handwritten numbered SQL migration system.

Before implementation is declared complete:
- EF model and migration snapshot must match;
- `dotnet ef migrations has-pending-model-changes` must report zero drift;
- fresh Up, Down-to-zero, Up rehearsal must pass;
- PostgreSQL integration tests for uniqueness/concurrency/provenance must pass.

---

# 191. Traceability Identity Invariants

The final traceability identities are:

```text
DealerCode    = WHO supplied it
ProductSKU    = WHAT product/model it is
ItemSequence  = WHICH exact physical item it is within SupplierProduct
TrackingCode  = DealerCode-ProductSKU-Minimum6(ItemSequence)
UUIDv7        = internal technical relational identity
```

`ItemSequence` is BIGINT, starts at 1, has minimum six-digit display formatting, can grow beyond 999999, never resets, and never reuses a committed value.

---

# 192. Product Management vs Inventory Management Boundary

Edge Retails formally separates Catalog Product Management from Inventory Management.

```text
PRODUCT MANAGEMENT
"What products does the business deal in?"

INVENTORY MANAGEMENT
"What physical stock exists, where did it come from,
what does it cost, which exact units exist,
and what happened to that stock?"
```

Core invariants:
1. Product is master data; Inventory is transactional state.
2. Product may exist with zero stock.
3. Product CRUD never changes stock/cost state.
4. Inventory cannot silently create a Product.
5. UI separation does not create duplicate database truth.

---

# 193. Product Management Authority and Commands

Product Management owns:
- SKU and Product identity;
- Name/Brand/Model;
- Category;
- Base Unit/Product Units;
- Tracking mode/policy;
- default selling price;
- minimum stock level;
- warranty/catalog attributes;
- active/inactive state;
- SupplierProduct links.

Authorized commands conceptually include:

```text
CreateProductCommand
UpdateProductCommand
UpdateSellingPriceCommand
DeactivateProductCommand
ReactivateProductCommand
LinkSupplierProductCommand
DeactivateSupplierProductCommand
ReactivateSupplierProductCommand
```

Do not expose a normal `UnlinkSupplierProductCommand` that deletes historical sequence authority.

Product Management never directly mutates:
- `inventory.stock_balances`;
- `inventory.lots`;
- `inventory.units`;
- `inventory.movements`;
- `inventory.cost_states`.

---

# 194. Cost and Margin Authority Separation

Inventory carrying cost authority remains:
- `inventory.cost_states.MovingAverageCost`;
- `inventory.cost_states.TotalInventoryCost`;
- `inventory.lots` for lot acquisition provenance;
- transaction snapshots for historical Purchase/Sale values.

Product master must not contain a competing mutable CurrentCost/MovingCost/CarryingCost.

`ReferencePurchaseCost`, if retained, is only a purchasing guideline.

Margin is derived, not persisted as mutable truth.

Product Management may display read-only inventory cost/margin projections.

---

# 195. Product Deactivation and SKU Preservation

Normal application behavior uses Product deactivation rather than hard deletion.

After Product creation/issuance:
- SKU remains permanently reserved;
- historical references remain resolvable;
- inactive Product cannot be selected for new ordinary Sales/Purchases;
- reactivation uses the same Product row and same SKU.

V1 does not require a user-facing hard-delete Product workflow.

---

# 196. Inventory Management Authority and Write Boundary

Inventory uses the existing authoritative models:
- `inventory.stock_balances`;
- `inventory.lots`;
- `inventory.lot_bucket_balances`;
- `inventory.units`;
- `inventory.movements`;
- `inventory.movement_effects`;
- `inventory.movement_units`;
- `inventory.lot_consumptions`;
- `inventory.cost_states`;
- Stocktake/Warranty/Purchase/Sale/Return aggregates.

Stock changes only through approved workflows:
- CreatePurchase/receiving;
- CompleteSale;
- Sale Return;
- Purchase Return;
- Purchase Void where eligible;
- Stocktake;
- Warranty;
- condition/bucket movement.

Generic unrestricted `SetStockQuantity` / `AddStock(productId, qty)` commands are prohibited.

---

# 197. Inventory "+" Uses the Existing Purchase Engine

The Inventory screen `+` means:

```text
Receive stock for this existing Product
```

It does not mean:
- Create Product;
- Directly increment stock;
- Bypass Purchase provenance.

The dialog may collect:
- Supplier/Dealer;
- supplier invoice number/date;
- entered quantity;
- ProductUnit;
- entered unit cost;
- other acquisition cost;
- serial/IMEI details where applicable;
- cash-session/payment information where required by existing Purchase rules.

The UI adapter invokes the existing `CreatePurchaseCommand` engine, normally pre-filled with the selected Product.

No parallel PostGoodsReceipt transaction engine is introduced.

---

# 198. Supplier-to-Product Multiplicity

A Product can have many Suppliers and a Supplier can provide many Products.

The M:N relationship is `catalog.supplier_products`.

`catalog.products` does not own a single SupplierId/DealerId.

Creating a SupplierProduct link creates:
- no stock;
- no lot;
- no movement;
- no InventoryUnit;
- no TrackingCode.

---

# 199. Precise TrackingCode Allocation Timing - Corrected

Product creation and SupplierProduct creation never create a physical TrackingCode.

TrackingCode is allocated only when a real individually tracked physical object enters one of the approved identity-receipt workflows.

Approved workflows:

```text
A. Normal shop-owned receipt
CreatePurchase transaction
-> allocate SupplierProduct sequence
-> create InventoryUnit + TrackingCode inside transaction
-> advance NextItemSequence inside transaction
-> commit
-> TrackingCode officially issued

B. Warranty replacement receipt
Authorized Warranty replacement transaction
-> allocate the same SupplierProduct sequence
-> create InventoryUnit + TrackingCode inside transaction
-> advance NextItemSequence inside transaction
-> link old/new physical identities
-> commit
-> TrackingCode officially issued
```

Customer-specific Warranty replacement does not create normal shop stock merely because it receives an InventoryUnit/TrackingCode identity.

No manual/random TrackingCode source exists. No committed ItemSequence is reused.

Labels/QR codes are printable only after successful commit.

# 200. Multi-Supplier Inventory Aggregation and Provenance

Inventory overview is Product-level aggregated state.

Do not create a separate SupplierStockBalance table merely for UI presentation.

For QUANTITY/LENGTH products, current supplier-origin stock is derived from remaining:

```text
inventory.lots
-> PurchaseItem
-> Purchase.SupplierId
+
inventory.lot_bucket_balances
```

For individually tracked products, supplier-origin stock is derived from current InventoryUnits through SupplierProduct and SourcePurchaseItem provenance.

Two different counts are distinguished:
- `LinkedSupplierCount`: active SupplierProduct relationships.
- `CurrentStockSupplierCount`: Suppliers currently contributing non-zero owned stock.

The Inventory list Dealer column should normally use `CurrentStockSupplierCount`, not total historical links.

---

# 201. Product Inventory History vs Physical Item History

`GetProductInventoryHistory(ProductId)`:
- applies to all tracking modes;
- is derived primarily from `inventory.movements` and `inventory.movement_effects`;
- shows aggregate Product stock changes.

`GetPhysicalItemHistory(TrackingCode/UnitId)`:
- applies to individually tracked units;
- composes Purchase, movement_units, SaleItemUnit, Sale Return, Warranty, Purchase Return, and current unit state;
- does not require a duplicate event-truth table.

---

# 202. Inventory Overview Metrics - Unambiguous Definitions

Required overview metrics:

```text
TotalProducts
InStockProducts
LowStockProducts
OutOfStockProducts
TotalInventoryCarryingValue
EstimatedSellableGrossProfit
```

Definitions:

```text
InStock:
SellableQty > 0

LowStock:
SellableQty > 0 AND SellableQty <= MinimumStockLevel

OutOfStock:
SellableQty <= 0

TotalInventoryCarryingValue:
SUM(ProductCostState.TotalInventoryCost)
```

This total carrying value may include costed owned inventory outside SELLABLE according to the existing cost-state policy.

For profit estimation use only sellable inventory:

```text
SellableInventoryCarryingValue
= SellableQty * MovingAverageCost

EstimatedSellableGrossProfit
= (SellableQty * DefaultSellingPrice)
  - SellableInventoryCarryingValue
```

EstimatedSellableGrossProfit is not realized Net Profit and excludes expenses, future discounts, and returns.

Do not subtract TotalInventoryCarryingValue from sellable-only revenue.

---

# 203. CQRS DTO Boundaries

## Product Management
`ProductManagementRowDto` may expose:
- ProductId;
- SKU;
- Name;
- Category;
- SellingPrice;
- TrackingMode;
- IsActive;
- optional read-only SellableStock;
- optional read-only MovingAverageCost.

Stock/cost fields are projections only, not Product-owned state.

## Inventory Overview
`InventoryProductRowDto` should expose:
- ProductId;
- SKU;
- ProductName;
- Category;
- CurrentStockSupplierCount;
- LinkedSupplierCount where needed;
- SellableQuantity;
- DamagedQuantity;
- DefectiveQuantity;
- WithSupplierQuantity;
- ScrapQuantity;
- MinimumStockLevel;
- MovingAverageCost;
- LastPurchaseCost;
- DefaultSellingPrice;
- TotalInventoryCarryingValue;
- SellableInventoryCarryingValue;
- EstimatedSellableGrossProfit;
- StockStatus;
- TrackingMode.

Do not expose speculative `LocationId` or `LocationName` fields unless a separate location/warehouse architecture is formally introduced.

## Physical Items
`PhysicalItemRowDto` should expose:
- UnitId;
- TrackingCode;
- Serial/IMEI;
- DealerCode/SupplierName via projection;
- ItemSequence as Int64;
- existing unit Status;
- ReceivedAt/other derivable lifecycle timestamps when available.

`NextItemSequence` is an administrative sequence authority and must never be user-editable.

---

# 204. Inventory Adjustment and Stocktake

Inventory `+` is not a stock-correction tool.

Corrections use the existing Stocktake and authorized inventory-condition workflows.

All corrections must retain:
- reason;
- actor;
- inventory movement;
- cost basis;
- deterministic locking;
- audit where required.

Existing five inventory buckets remain authoritative:

```text
SELLABLE
DAMAGED
DEFECTIVE
WITH_SUPPLIER
SCRAP
```

---

# 205. Database Ownership Map - Current Canonical Names

The Product/Inventory separation uses existing physical schema names.

```text
CATALOG
catalog.products
catalog.categories
catalog.units
catalog.product_units
catalog.product_unit_barcodes
catalog.supplier_products            [new required traceability relation]

PARTIES
parties.suppliers
parties.customers

INVENTORY
inventory.stock_balances
inventory.cost_states
inventory.movements
inventory.movement_effects
inventory.movement_units
inventory.units
inventory.lots
inventory.lot_bucket_balances
inventory.lot_consumptions
inventory.stocktakes
inventory.stocktake_items
inventory.stocktake_unit_checks

PURCHASING
purchasing.purchases
purchasing.purchase_items
purchasing.purchase_item_units
purchasing.returns
purchasing.return_items
purchasing.return_item_units
purchasing.purchase_voids

SALES
existing sales tables, including sales.sale_item_units

WARRANTY
warranty.claims
warranty.claim_items
warranty.claim_item_units
warranty.claim_events
warranty.shop_stock_cases

FINANCE
finance.cash_sessions
finance.cash_movements
finance.expense_categories
finance.expense_subcategories
finance.expenses

AUDIT
audit.business_events
```

Do not invent `customer_ledgers`, `supplier_ledgers`, `audit_logs`, `purchase_receipts`, `inventory.inventory_units`, or `inventory.inventory_lots` as part of this architecture change.

---

# 206. Mandatory Product-vs-Inventory Separation Tests

1. CreateProduct leaves stock zero and creates no lot/movement/unit/tracking code.
2. LinkSupplierProduct leaves stock zero.
3. Product update does not mutate stock/cost.
4. Product deactivate preserves historical resolution and permanent SKU.
5. Inventory `+` routes through existing CreatePurchase engine.
6. Inventory `+` cannot silently create Product.
7. Individually tracked receipt creates exactly BaseQuantity InventoryUnits/TrackingCodes.
8. Multi-supplier receipt aggregates Product stock correctly.
9. CurrentStockSupplierCount reflects suppliers with current non-zero stock, not merely linked suppliers.
10. Product Inventory History reflects authoritative movement ledger.
11. Physical Item History reconstructs one unit without duplicate event truth.
12. TotalInventoryCarryingValue equals authoritative ProductCostState totals.
13. EstimatedSellableGrossProfit uses sellable carrying value, not total carrying value.
14. QUANTITY/LENGTH receipt does not generate unnecessary InventoryUnits.
15. Direct arbitrary stock edit is unavailable/rejected.
16. Existing five-bucket semantics remain unchanged.
17. No Location fields are required for V1 inventory DTOs.
18. Existing `audit.business_events` is used; no `audit_logs` table is introduced.
19. Existing `sales.sale_item_units` is reused.
20. Existing EF InitialProductionBaseline strategy remains the migration authority.
21. Individually tracked receiving rejects fractional exact BaseQuantity before SupplierProduct sequence reservation.
22. Individually tracked receiving creates exactly one InventoryUnit/TrackingCode per whole BaseQuantity unit.
23. Exact-unit identity count mismatch produces no partial stock/cost/sequence commit.
24. QUANTITY/LENGTH fractional receiving remains valid according to Unit precision policy and does not create artificial InventoryUnits.

---

# 207. Resolved Conflict Matrix

| Conflict | Final Resolution |
|---|---|
| Fixed 999999 physical limit | Removed. ItemSequence is BIGINT with minimum six-digit display. |
| SupplierProduct unlink/delete | Removed from normal workflow. Deactivate/reactivate same row. |
| Product SKU reuse after inactive | Forbidden permanently. |
| Duplicate Dealer aggregate | Not allowed. Dealer is Supplier business terminology. |
| Parallel PostGoodsReceipt engine | Not allowed. Inventory + uses existing CreatePurchase engine. |
| inventory.inventory_units / inventory.inventory_lots names | Removed. Reuse inventory.units / inventory.lots. |
| New inventory condition states | Removed. Preserve five buckets and existing unit statuses. |
| Location fields | Removed from V1 unless a separate location architecture is approved. |
| audit.audit_logs | Removed. Reuse audit.business_events. |
| customer/supplier ledgers introduced by this change | Removed. Not part of this boundary change. |
| TrackingCode "generated after commit" wording | Corrected: created/reserved inside transaction, officially issued after commit. |
| Stock Value vs sellable profit basis | Split into TotalInventoryCarryingValue and SellableInventoryCarryingValue. |
| Single dealer per Product | Forbidden. SupplierProduct is M:N. |
| Supplier stock balance table | Not introduced. Derive supplier breakdown from lot/unit provenance. |
| Hard delete Product | Removed from normal V1 UI workflow; use deactivation. |
| Raw alternative advisory-lock convention | Removed. Use existing IResourceLock/PostgresOperationLock. |
| Parallel migration SQL system | Removed. Use existing EF Core InitialProductionBaseline strategy. |

## 207.1 Stale Audit / Forensic Claim Quarantine

The following previously reported claims are explicitly non-authoritative when presented as current V1 truth:

| Stale / External Claim | Canonical Current Truth |
|---|---|
| ItemSequence/TrackingCode sequence stops at 999999 | ItemSequence is positive BIGINT; six digits are minimum display width; 1000000 and above are valid. |
| Current navigation has 17 full screens | Section 209 defines exactly 19 full screens. |
| Dedicated Warranty is screen 18 | Section 209 places Warranty at full screen #17. |
| Current deterministic lock hierarchy has 8 levels | Section 185 defines 10 logical resource-order levels. |
| Current physical tables are inventory.inventory_units / inventory.inventory_lots | Reuse existing inventory.units / inventory.lots; the parallel names are explicitly rejected. |
| Deep history should universally use OFFSET/LIMIT | Section 76 permits bounded shallow OFFSET/LIMIT but prefers deterministic keyset/seek for deep/growing history when stable keys exist. |
| Architecture document proves architecture tests already exist and pass | Architecture requires the tests; physical existence/execution requires repository/CI evidence under Sections 78.2 and 233. |
| Sprint/code completion statements are architecture authority | Implementation status is a workspace/build/test fact, not business-rule authority. |
| Older Warranty/Khata notes override later corrections | Current Warranty/Supplier Khata authority is the later corrected contract, especially Sections 209-225 and related earlier explicit corrections. |

These quarantine rows are intentionally retained as negative evidence. Their presence must not be interpreted as active alternatives.

A future audit may identify a genuine new defect. Such a finding must be classified and, if accepted, incorporated through an explicit canonical correction rather than silently replacing architecture truth.

---

# 208. Product / Inventory Domain Invariants

Edge Retails permanently separates:

```text
PRODUCT MANAGEMENT
= define and maintain WHAT the business sells.

INVENTORY MANAGEMENT
= maintain WHAT physical stock currently exists,
  WHERE it came from,
  WHAT it cost,
  WHICH exact units exist,
  and WHAT happened to them.
```

Final invariants:

1. Product creation never creates stock.
2. Inventory receipt never silently creates Product master data.
3. Inventory `+` invokes the existing Purchase engine.
4. TrackingCode exists only for real individually tracked physical units.
5. TrackingCode creation/reservation occurs inside the receipt transaction; issuance/printing occurs only after commit.
6. Same Product can have many Suppliers.
7. SupplierProduct sequence is permanent, BIGINT, monotonic, and never reused.
8. Product SKU and DealerCode remain permanent historical business identities.
9. Existing Inventory, Sales, Purchasing, Warranty, Audit, and Costing tables remain authoritative.
10. UI separation must never create duplicate database truth.

---

# 209. Revised V1 Full-Screen Navigation Contract

The previous navigation contract is superseded. Edge Retails now has exactly 19 full screens:

1 First Setup / License
2 Login / User Switch
3 Dashboard
4 POS
5 Sales History
6 Sale Detail
7 Thaka / Projects
8 Thaka Workspace
9 New Purchase
10 Purchase History
11 Product Management
12 Product Detail
13 Inventory
14 Expenses
15 Customers
16 Suppliers
17 Warranty
18 Reports
19 Settings

Rules:
- New Sale is renamed to POS everywhere.
- Product Management and Inventory are separate full management surfaces.
- Warranty is a dedicated full operational screen.
- Supplier Khata stays inside Suppliers and does not add another full screen.
- Drawers/dialogs remain appropriate for Price Check, drafts, supplier payments, and warranty actions.

---

# 210. POS Workspace - Final Role

POS is the cashier's primary selling workspace, not merely a New Sale form.

POS owns product search/scan, exact physical-unit scan, cart construction, customer selection, Price Check, Draft/Hold Cart, quotation loading, permitted discounts/price overrides, payment, and final completion through the existing CompleteSaleCommand engine.

POS does not own Product master edits, permanent selling-price changes, direct stock adjustment, Warranty management, Supplier Khata, or mutation of completed Sales.

Completed transactions remain in Sales History/Sale Detail.

---

# 211. POS Draft / Hold Cart

A POS Draft is a suspended working cart. It is not a completed Sale and not a Quotation.

Suggested schema:

sales.pos_drafts
- Id UUIDv7
- DraftNumber unique
- CustomerId nullable
- CreatedBy
- TerminalId nullable
- Status OPEN / CONVERTED / CANCELLED / EXPIRED
- Note nullable
- CreatedAt
- UpdatedAt
- ExpiresAt nullable
- Version

sales.pos_draft_items
- Id UUIDv7
- DraftId
- ProductId
- ProductUnitId
- EnteredQuantity
- FactorToBaseSnapshot
- BaseQuantity
- DisplayedUnitPriceSnapshot
- SelectedInventoryUnitId nullable
- Note nullable

Saving a Draft creates no Sale, invoice, inventory movement, lot consumption, cash movement, COGS, warranty start, or stock reservation.

Draft prices are convenience snapshots only. On completion, Product state, stock, authoritative price, and exact-unit state are revalidated. CompleteSaleCommand remains the only Sale completion engine.

Draft and Quotation remain separate:
- Draft = mutable suspended cart, no price guarantee, no reservation.
- Quotation = formal estimate with existing validity and quote-price rules.

---

# 212. POS Price Check and Universal Search

GetPosPriceCheckQuery is read-only and may return ProductId, SKU, Product Name, selected/default unit, authoritative selling price, SellableQuantity, stock status, TrackingMode, and exact InventoryUnit status for TrackingCode/Serial/IMEI scans.

Price Check does not add to cart, reserve stock, or mutate Product/Inventory.

POS universal search supports Product Name, SKU, Product Unit Barcode, TrackingCode, Serial Number, IMEI1, IMEI2, and category/name keywords.

Exact identifiers rank above fuzzy/name matches.

Recommended read contract: SearchPosCatalogQuery.

Search results distinguish Product-level, barcode/unit, and exact physical-unit results. TrackingCode is never parsed as relational truth.

---

# 213. POS Pricing, Discount, and Authorized Price Override

Normal price authority remains Product.DefaultSalePrice multiplied by ProductUnit.FactorToBaseUnit. The client may send ExpectedUnitPrice for stale-cart detection.

A one-sale Price Override:
- never changes Product.DefaultSalePrice;
- requires permission sales.price_override;
- requires an explicit reason;
- requires a nonnegative final price;
- is backend validated;
- is snapshotted on SaleItem.

Conceptual SaleItem additions:
- ListUnitPriceSnapshot
- UnitPrice as final applied unit price
- PriceOverrideReason nullable
- PriceOverrideBy nullable

A below-current-cost override requires stronger permission sales.price_override_below_cost unless a later rule defines another floor.

Price Override and Discount are distinct. Permanent Product price changes remain Product Management authority.

---

# 214. POS Operational Features and Read Models

POS V1 supports:
- Price Check
- Universal Search
- Save Draft
- Resume Draft
- Recent Drafts
- Cancel Draft
- Load Quotation
- Customer Quick Search / Select
- Current Sellable Stock Indicator
- Low-Stock Warning
- Exact TrackingCode Scan
- Serial / IMEI Scan
- Existing Multi-Method Sale Payments
- Keyboard-first cashier operation

Keyboard shortcuts are client-side. Low-stock warning is informational; insufficient stock remains a backend validation failure.

Recommended queries:
- GetRecentPosDraftsQuery
- GetPosDraftDetailQuery
- SearchPosCatalogQuery
- GetPosPriceCheckQuery
- GetPosCustomerQuickSearchQuery
- GetPosQuoteLookupQuery

---

# 215. Supplier Khata / Accounts Payable - V1 Scope Promotion

Supplier Accounts Payable is no longer deferred.

Edge Retails includes a lightweight Supplier Khata subledger, not a General Ledger.

Still outside V1:
- Customer normal credit / Udhaar
- Customer credit limits / statements
- Full General Ledger
- Double-entry accounting
- Bank reconciliation

Supplier Khata answers:
- total purchased value from a Supplier;
- total paid;
- return/credit reductions;
- current payable;
- supplier credit / shop advance.

V1 Khata is Supplier-level. Invoice-specific payment allocation is optional future scope.

---

# 216. Supplier Account Entry Model - Exact Direction and Source Rules

`finance.supplier_account_entries` is the append-only Supplier Khata balance truth.

Conceptual model:

```text
SupplierAccountEntry
- Id UUIDv7
- EntryNumber unique
- SupplierId
- EntryType
- Direction INCREASE_PAYABLE | DECREASE_PAYABLE
- Amount numeric(18,2) > 0
- ReferenceType
- ReferenceId nullable only where explicitly allowed
- OccurredAt
- ActorId
- ClientOperationId nullable
- Note nullable
- CreatedAt
```

Signed balance effect:

```text
INCREASE_PAYABLE = +Amount
DECREASE_PAYABLE = -Amount
```

Authoritative EntryType direction matrix:

```text
OPENING_BALANCE              -> INCREASE or DECREASE, explicit at setup/import
PURCHASE                     -> INCREASE_PAYABLE
SUPPLIER_PAYMENT             -> DECREASE_PAYABLE
SUPPLIER_PAYMENT_REVERSAL    -> INCREASE_PAYABLE
PURCHASE_RETURN_CREDIT       -> DECREASE_PAYABLE
PURCHASE_VOID_REVERSAL       -> DECREASE_PAYABLE
SUPPLIER_REFUND_RECEIVED     -> INCREASE_PAYABLE
SUPPLIER_REFUND_REVERSAL     -> DECREASE_PAYABLE
WARRANTY_CREDIT              -> DECREASE_PAYABLE
ADJUSTMENT_INCREASE          -> INCREASE_PAYABLE
ADJUSTMENT_DECREASE          -> DECREASE_PAYABLE
```

The backend validates EntryType/Direction compatibility. The client cannot choose an arbitrary direction for system-generated entry types.

Entries are append-only. Posted entries are never edited or deleted; corrections use explicit reversal/adjustment records.

No mutable `Supplier.Balance` column exists.

## 216.1 Source Uniqueness

System-generated account effects must have immutable source identity.

Where applicable, enforce one logical account effect per:

```text
EntryType + ReferenceType + ReferenceId
```

Examples:

```text
PURCHASE + Purchase + PurchaseId                         -> exactly once
PURCHASE_RETURN_CREDIT + PurchaseReturn + ReturnId      -> exactly once
PURCHASE_VOID_REVERSAL + PurchaseVoid + VoidId          -> exactly once
WARRANTY_CREDIT + WarrantyResolution + ResolutionId     -> exactly once
SUPPLIER_PAYMENT + SupplierPayment + PaymentId           -> exactly once
SUPPLIER_PAYMENT_REVERSAL + PaymentReversal + ReversalId -> exactly once
SUPPLIER_REFUND_RECEIVED + SupplierRefund + RefundId     -> exactly once
```

Manual Opening Balance/Adjustment operations use their own unique operation identity and mandatory audit metadata.

`ClientOperationId` protects retryable command execution; source uniqueness independently protects business-source duplication.

---

# 217. Supplier Payment, Advance, Refund, and Reversal Model - Corrected

Add/confirm:

```text
finance.supplier_payments
finance.supplier_payment_reversals
finance.supplier_refunds
finance.supplier_refund_reversals
```

SupplierPayment:

```text
Id UUIDv7
PaymentNumber unique
SupplierId
Amount numeric(18,2) > 0
Purpose SETTLEMENT | ADVANCE
Method CASH_DRAWER | EXTERNAL
CashSessionId nullable
ExternalReference nullable
PaidAt
ActorId
ClientOperationId unique
Note nullable
Status POSTED | REVERSED
```

SupplierPaymentReversal:

```text
Id UUIDv7
SupplierPaymentId unique
Reason
ReversedBy
ReversedAt
ClientOperationId unique
```

SupplierRefund:

```text
Id UUIDv7
RefundNumber unique
SupplierId
Amount numeric(18,2) > 0
Method CASH_DRAWER | EXTERNAL
CashSessionId nullable
ExternalReference nullable
ReferenceType nullable
ReferenceId nullable
ReceivedAt
ActorId
ClientOperationId unique
Note nullable
Status POSTED | REVERSED
```

SupplierRefundReversal:

```text
Id UUIDv7
SupplierRefundId unique
Reason
ReversedBy
ReversedAt
ClientOperationId unique
```

## 217.1 Settlement vs Advance

Under the SupplierAccount resource lock, backend reloads `CurrentSupplierBalance` before accepting a SupplierPayment.

For `Purpose = SETTLEMENT`:

```text
OutstandingPayable = max(CurrentSupplierBalance, 0)
Payment.Amount <= OutstandingPayable
```

A normal settlement cannot silently create a negative Supplier balance.

For `Purpose = ADVANCE`:
- negative balance is intentional;
- permission `supplier.advance.create` is required;
- explicit reason/note is required;
- BusinessAudit is mandatory.

Both SETTLEMENT and ADVANCE create `SUPPLIER_PAYMENT / DECREASE_PAYABLE`.

CASH_DRAWER payment also creates `SUPPLIER_PAYMENT_CASH_OUT` in the same transaction. EXTERNAL payment creates no drawer movement.

## 217.2 Supplier Refund Received

A SupplierRefund settles existing Supplier credit/advance back toward zero.

Under the SupplierAccount resource lock:

```text
SupplierCreditOrAdvance = max(-CurrentSupplierBalance, 0)
Refund.Amount <= SupplierCreditOrAdvance
```

Posting SupplierRefund creates:

```text
SUPPLIER_REFUND_RECEIVED
Direction = INCREASE_PAYABLE
Amount = Refund.Amount
```

This consumes Supplier credit rather than increasing it.

CASH_DRAWER receipt creates `SUPPLIER_REFUND_CASH_IN`; EXTERNAL receipt creates no drawer movement.

A refund reversal creates `SUPPLIER_REFUND_REVERSAL / DECREASE_PAYABLE` and, for CASH_DRAWER, `SUPPLIER_REFUND_REVERSAL_CASH_OUT`.

Payment/refund reversals are one-time append-only corrections. Original records remain historically visible.

---

# 218. Supplier Balance and Summary Calculations - Corrected

Supplier balance is derived only from append-only SupplierAccountEntries:

```text
CurrentSupplierBalance
=
SUM(+Amount for INCREASE_PAYABLE)
-
SUM(Amount for DECREASE_PAYABLE)
```

Interpretation:

```text
Balance > 0 -> Outstanding Payable
Balance = 0 -> Settled
Balance < 0 -> Supplier Credit / Shop Advance
```

Summary metrics:

```text
GrossPurchasedValue
= SUM(PURCHASE increase entries)

PurchaseReturnCredits
= SUM(PURCHASE_RETURN_CREDIT decrease entries)

PurchaseVoidReversals
= SUM(PURCHASE_VOID_REVERSAL decrease entries)

WarrantyCredits
= SUM(WARRANTY_CREDIT decrease entries)
```

```text
NetPurchasedValue
= GrossPurchasedValue
- PurchaseReturnCredits
- PurchaseVoidReversals

NetPaidToSupplier
= posted Supplier Payments
- Supplier Payment Reversals

NetSupplierRefundsReceived
= posted Supplier Refunds
- Supplier Refund Reversals

OutstandingPayable
= max(CurrentSupplierBalance, 0)

SupplierCreditOrAdvance
= max(-CurrentSupplierBalance, 0)
```

`WarrantyCredits` affect Supplier balance but do not rewrite historical purchase value.

SupplierRefund does not create revenue and does not alter NetPurchasedValue. It settles a previously existing Supplier credit/advance.

Do not floor the underlying account balance to zero.

---

# 219. Purchase, Return, Void, Warranty Credit, and Khata Integration - Corrected

## 219.1 Purchase Liability

Every successfully completed Purchase creates a `PURCHASE / INCREASE_PAYABLE` entry for `Purchase.GrandTotal` in the same transaction.

A Purchase may have no, partial, or full immediate payment.

If an immediate payment exists, it is a real SupplierPayment created in the same Purchase transaction. Purchase itself is not payment truth.

Initial payment amount is limited to 0 through the current Purchase GrandTotal. Prior outstanding can be settled separately through Supplier Payment.

Existing negative Supplier credit automatically nets against newly created Purchase liability in the derived account balance.

## 219.2 Purchase Return Credit

A completed Purchase Return creates:

```text
PURCHASE_RETURN_CREDIT
Direction = DECREASE_PAYABLE
Amount = PurchaseReturn.SupplierReturnValue
```

The Khata amount never uses `InventoryCostRemoved`.

Purchase Return itself creates no cash receipt. If the resulting Supplier credit is later settled back to the shop, use SupplierRefund.

## 219.3 Purchase Void Reversal

An eligible Purchase Void creates:

```text
PURCHASE_VOID_REVERSAL
Direction = DECREASE_PAYABLE
Amount = original Purchase.GrandTotal
```

Money previously paid is not erased. If Void causes a Supplier credit, later settlement back to the shop is a separate SupplierRefund.

## 219.4 Warranty Credit

An approved shop-owned Warranty monetary-credit resolution creates:

```text
WARRANTY_CREDIT
Direction = DECREASE_PAYABLE
Amount = SupplierCreditAmount
Reference = WarrantyResolutionId
```

It must be in the same atomic transaction that removes the corresponding recoverable inventory carrying cost, as defined in Section 114.5.

Warranty status `CREDITED` without an explicit approved monetary amount does not create a Khata entry.

## 219.5 Supplier Refund Settlement

SupplierRefund may settle credit created by Purchase Return, Purchase Void, Warranty Credit, or intentional advance.

Posting creates:

```text
SUPPLIER_REFUND_RECEIVED
Direction = INCREASE_PAYABLE
Amount = Refund.Amount
```

The backend validates `Refund.Amount <= SupplierCreditOrAdvance` under the SupplierAccount lock.

SupplierRefund is a balance settlement, not Sales Revenue and not a reversal of the originating Purchase/Return/Warranty record.

## 219.6 Authority Rule

Supplier account truth is reconstructed from SupplierAccountEntries.

Purchase, PurchaseReturn, PurchaseVoid, WarrantyCase, SupplierPayment, and SupplierRefund are immutable business sources. Their account effects are linked by source identity and created exactly once.

# 220. Supplier Screen - Profile, Khata, Purchases, Payments, Refunds, and Warranty

The Suppliers full screen remains one screen with a detailed workspace/panel.

Supplier detail supports:

```text
Overview
Khata
Purchases
Purchase Returns
Payments / Advances
Refunds Received
Products Supplied
Warranty
```

Financial cards:

```text
Gross Purchased
Net Purchased
Net Paid to Supplier
Outstanding Payable
Supplier Credit / Advance
Net Supplier Refunds Received
Warranty Credits
```

Warranty cards remain operational, not automatically financial:

```text
Warranty Claim Count
Claim Lines / tracked-unit count as applicable
Currently With Supplier
Repaired
Replaced
Rejected
Ready / Returned
```

Warranty metrics do not alter Khata unless an explicit financial event such as WARRANTY_CREDIT is posted.

Recommended reads:

```text
GetSupplierOverviewQuery
GetSupplierAccountSummaryQuery
GetSupplierKhataQuery
GetSupplierPaymentsQuery
GetSupplierRefundsQuery
GetSupplierPurchaseHistoryQuery
GetSupplierPurchaseReturnHistoryQuery
GetSupplierWarrantySummaryQuery
GetSupplierWarrantyHistoryQuery
```

Supplier Khata statement must show a single chronological derived balance across Purchases, Payments, Advances, Returns, Voids, Warranty Credits, Refunds, Reversals, and authorized Adjustments.

# 221. Dedicated Warranty Full Screen

Warranty is a dedicated full operational screen over the existing Warranty domain.

Modes:
- Customer Warranty Claims
- Shop Stock Warranty Cases
- All

Summary cards:
- Open Claims
- With Supplier
- Ready for Customer
- Completed / Closed

Optional period metrics:
- Received this period
- Resolved this period
- Average open age where useful

List fields may include Claim/Case Number, TrackingCode where applicable, Product, Customer or Shop Stock, Supplier, Claim Type, Received/Created Date, Status, Current Custody, Days Open, Resolution, and Action.

---

# 222. Warranty Read Models and Operational Queues

Required reads:
- GetWarrantyDashboardSummaryQuery
- GetWarrantyClaimsQuery
- GetWarrantyClaimDetailQuery
- GetWarrantyWithSupplierQueueQuery
- GetWarrantyReadyForCustomerQuery
- GetShopStockWarrantyCasesQuery
- GetSupplierWarrantySummaryQuery
- GetSupplierWarrantyHistoryQuery
- GetWarrantyPhysicalItemHistoryQuery

Filters include:
- Customer Claim / Shop Stock
- Supplier
- Product
- Status
- Custody
- Date range
- TrackingCode / Serial / IMEI
- ClaimNumber / CaseNumber

Use the existing warranty.claims, warranty.claim_items, warranty.claim_item_units, warranty.claim_events, warranty.shop_stock_cases, inventory.units, and inventory.movements authorities.

No duplicate warranty-history table is created.

---

# 223. Warranty Metrics, Quantity Dimensions, and Tracking Semantics

Expose distinct operational metrics:

```text
WarrantyClaimCount
= number of claim/case headers

WarrantyClaimLineCount
= number of claim/case item lines

TrackedWarrantyUnitCount
= distinct InventoryUnits linked to warranty work
```

Quantity reporting is dimension-safe and must be grouped by compatible Product/BaseUnit dimensions:

```text
WarrantyClaimedQuantityBreakdown
= grouped by ProductId + BaseUnitId (or equivalent immutable base-unit identity)
```

Never publish one global claimed-quantity scalar that adds incompatible dimensions such as pieces, meters, rolls, or other unrelated base units.

TrackingCode resolves original Supplier from purchase provenance, original Sale and warranty snapshot where applicable, and current claim/custody/status.

Replacement receives a new TrackingCode and remains linked to the original through claim-item-unit linkage.

Warranty resolution Credited does not automatically mutate Supplier Khata. Monetary warranty credit requires an explicit amount and explicit WARRANTY_CREDIT SupplierAccountEntry linked to the warranty reference.

---

# 224. POS, Supplier Khata, and Warranty Permissions - Corrected

Add or confirm granular permissions:

```text
sales.pos.use
sales.draft.create
sales.draft.resume
sales.draft.cancel
sales.price_check
sales.price_override
sales.price_override_below_cost

supplier.account.view
supplier.payment.create
supplier.payment.reverse
supplier.advance.create
supplier.refund.create
supplier.refund.reverse
supplier.account.adjust
supplier.warranty.view

warranty.view
warranty.claim.create
warranty.claim.update
warranty.claim.send_supplier
warranty.claim.resolve
warranty.claim.replace
warranty.claim.handover
warranty.shop_stock.manage
warranty.shop_stock.credit
```

Cost/profit visibility remains separately permission-sensitive.

High-risk actions requiring BusinessAudit with actor, reason, reference, and correlation include:
- Price Override;
- below-cost override;
- Supplier Advance;
- Supplier Payment Reversal;
- Supplier Refund Reversal;
- Supplier Account Adjustment;
- Warranty monetary credit;
- Warranty physical replacement;
- Warranty write-off/scrap.

---

# 225. Concurrency, Idempotency, and Duplicate-Effect Barriers - Corrected

## POS Draft
- Version optimistic concurrency is required.
- stale concurrent edits are rejected;
- Draft conversion uses the normal CompleteSale locks and unique ClientOperationId;
- only one conversion can succeed.

## Supplier Khata
Every SupplierPayment, SupplierRefund, account adjustment, and source-generated Khata effect uses ClientOperationId/idempotency where applicable.

For balance-sensitive commands:

```text
BEGIN
-> acquire ClientOperationId lock
-> acquire supplier-account:<SupplierId> through IResourceLock
-> reload CurrentSupplierBalance inside transaction
-> validate settlement / advance / refund amount
-> write business source
-> write exactly one SupplierAccountEntry
-> write CashMovement if CASH_DRAWER
-> BusinessAudit where required
-> COMMIT
```

Normal settlement cannot exceed current OutstandingPayable.
SupplierRefund cannot exceed current SupplierCreditOrAdvance.
Intentional advance requires explicit ADVANCE purpose + permission.

System-generated account effects also obey the unique source barrier from Section 216.1, so command retry protection and business-source duplication protection are independent.

Payment reversal is unique per SupplierPayment.
Refund reversal is unique per SupplierRefund.
No mutable Supplier balance column is incremented directly.

## Warranty
- Version concurrency remains authoritative for claim/case headers.
- invalid/stale state transitions are rejected.
- exact-unit claim creation acquires an exact-unit warranty resource lock before checking active claims.
- a tracked InventoryUnit may have at most one active Customer Warranty Claim.
- Warranty replacement uses WarrantyClaim/Case + SupplierProduct sequence + exact-unit locks in the Section 185 order.
- Warranty monetary credit locks the Warranty case/inventory resources and SupplierAccount before persisting inventory-cost removal + WARRANTY_CREDIT atomically.

## LAN Unknown-Outcome Replay
- a terminal preserves the same ClientOperationId for one intended authoritative operation until its outcome is known;
- connection loss after request dispatch is treated as ambiguous until server truth resolves it;
- replay after reconnect uses the existing command/idempotency barrier and cannot create a second business effect;
- a fresh ClientOperationId represents a genuinely new user intent, not a retry of an unresolved prior intent;
- non-idempotent/unsafe commands must not be blindly auto-replayed;
- all retries/replays re-read current authoritative state and revalidate normal business rules;
- terminal-local success/failure flags never override committed server truth.

# 226. Reporting Impact

Reports may now expose:
- Supplier Outstanding Payables
- Supplier Credits / Advances
- Supplier Purchase vs Payment Summary
- Supplier Khata Statement
- Supplier Warranty Claim Summary
- Warranty Status Aging
- Warranty With-Supplier Aging
- POS Draft Activity
- Price Override Audit

Rules:
- POS Drafts are not Sales or Revenue.
- Supplier Payments are settlements, not inventory cost.
- Purchase Return Khata effect uses SupplierReturnValue.
- Supplier Khata is not the General Ledger.
- Warranty metrics are operational unless an explicit monetary credit/refund exists.

---

# 227. Migration and Implementation Additions - Phase 1 Corrected

The fresh EF Core baseline strategy remains authoritative while no production database requires preservation of an older migration chain.

New/required schema concepts from Sections 209 onward now include:

```text
sales.pos_drafts
sales.pos_draft_items

finance.supplier_account_entries
finance.supplier_payments
finance.supplier_payment_reversals
finance.supplier_refunds
finance.supplier_refund_reversals
```

Existing `inventory.units` is extended for the corrected physical-origin model, including conceptually:
- OriginType;
- nullable SourcePurchaseItemId;
- nullable SourceWarrantyClaimItemId;
- nullable SourceWarrantyCaseId;
- nullable InventoryLotId where warranty-origin non-stock identity applies;
- warranty customer-held / handed-over lifecycle states where implementation uses the existing InventoryUnit status mechanism.

Existing cash movement vocabulary must support the Supplier settlement movement types defined in Sections 124-125. Do not maintain old PurchaseCashOut and new SupplierPaymentCashOut as independent parallel truths.

Extend existing Warranty tables only where required for:
- claim active-unit uniqueness support;
- replacement old/new unit linkage;
- shop Warranty financial-resolution snapshots;
- WarrantyCase Version/concurrency;
- indexes/read performance.

Do not add:
- General Ledger;
- Customer Udhaar;
- duplicate Warranty aggregates;
- duplicate Purchase engine;
- duplicate TrackingCode sequence source;
- mutable Supplier balance column.

Required indexes/constraints include:
- Draft Status + UpdatedAt;
- SupplierAccountEntry SupplierId + OccurredAt;
- unique system account-entry source identity where applicable;
- SupplierPayment SupplierId + PaidAt;
- unique SupplierPayment ClientOperationId;
- unique SupplierPaymentReversal SupplierPaymentId;
- SupplierRefund SupplierId + ReceivedAt;
- unique SupplierRefund ClientOperationId;
- unique SupplierRefundReversal SupplierRefundId;
- exact-unit active Warranty claim barrier/index strategy;
- Warranty SupplierId + Status + ReceivedAt;
- origin/provenance indexes required by TrackingCode history queries.

# 228. Current V1 Architecture Declaration

Authoritative V1 decisions:

POS full workspace = FINAL
New Sale naming = REPLACED BY POS
POS Draft / Hold Cart = FINAL
POS Price Check = FINAL
POS Universal Search = FINAL
Authorized Price Override = FINAL
Quotation-to-POS loading = FINAL

Product Management separate from Inventory = FINAL
Dealer/Product/Physical Tracking = FINAL

Supplier Khata / Accounts Payable = FINAL
Supplier Payment Ledger = FINAL
Supplier Refund / Advance Settlement = FINAL
Supplier Warranty Summary = FINAL

Dedicated Warranty full screen = FINAL
Customer Warranty Claims = FINAL
Shop-Owned Warranty Cases = FINAL
Warranty Replacement TrackingCode Path = FINAL
Warranty Monetary Credit Closure = FINAL

Normal Customer Credit / Udhaar = DEFERRED
Customer Credit Limit / Statement = DEFERRED
General Ledger / Double Entry = OUT OF V1
Bank Reconciliation = OUT OF V1

The architecture remains offline-first and local-PostgreSQL authoritative.

Frontend implementation remains a separate task, but navigation and DTO contracts must conform to Sections 209-228.


---

# 229. Implementation Order for Sections 209-228 - Phase 1 Aligned

Implement the new architecture in this order:

Phase A - POS Read/Workspace Foundation
1. Rename New Sale route/navigation to POS.
2. Add POS universal search.
3. Add Price Check.
4. Add POS Draft aggregate and read/write handlers.
5. Add recent/resume/cancel Draft flows.
6. Add Quote-to-POS loading.
7. Add authorized one-sale Price Override snapshots and permissions.

Phase B - Supplier Khata
1. Add SupplierAccountEntry + exact EntryType/Direction validation.
2. Add SupplierPayment + PaymentReversal.
3. Add SupplierRefund + RefundReversal.
4. Add settlement-vs-advance semantics and permissions.
5. Add supplier-account resource locking and fresh-balance validation.
6. Add source-uniqueness constraints for generated account effects.
7. Integrate Purchase payable creation.
8. Integrate initial partial/full Purchase payment.
9. Integrate Purchase Return credit.
10. Integrate Purchase Void payable reversal.
11. Integrate Warranty monetary credit.
12. Add Supplier Khata/payment/refund/summary queries.
13. Add payment, refund, advance, adjustment permissions/audit.

Phase C - Warranty Deep Closure
1. Add warranty eligibility validator.
2. Enforce one active claim per exact InventoryUnit.
3. Enforce one Supplier provenance per supplier-bound Customer Claim.
4. Implement allowed status/custody transition matrix.
5. Remove unsupported service-center custody from V1.
6. Add Warranty replacement identity allocation through SupplierProduct sequence authority.
7. Add Warranty-origin InventoryUnit provenance.
8. Implement shop-owned repair/replacement/rejection/scrap/credit outcomes.
9. Make Warranty monetary credit remove carrying cost + post Supplier Khata atomically.
10. Add Warranty dashboard/queues/search/read models without duplicating Warranty aggregates.

Phase D - Reporting and Cross-Module Closure
1. Supplier payable/advance/refund reports.
2. Warranty aging and recovery/loss reporting.
3. Price Override audit.
4. POS Draft activity.
5. Full cross-module integration tests.

Do not build a second Sale engine, second Purchase engine, second Warranty aggregate, second TrackingCode sequence source, or mutable Supplier balance shortcut.

# 230. Search and Read-Performance Index Contract

POS interactive search must remain fast on a local PostgreSQL database.

Required or equivalent indexed paths:

Catalog:
- catalog.products normalized SKU
- catalog.products Name / normalized search strategy
- catalog.product_unit_barcodes barcode unique/indexed

Physical identity:
- inventory.units TrackingCode unique
- inventory.units SerialNumber unique where not null
- inventory.units Imei1 unique where not null
- inventory.units Imei2 unique where not null

POS Draft:
- sales.pos_drafts Status + UpdatedAt
- sales.pos_drafts CreatedBy + Status where useful
- sales.pos_draft_items DraftId

Supplier Khata:
- finance.supplier_account_entries SupplierId + OccurredAt
- finance.supplier_account_entries ReferenceType + ReferenceId
- finance.supplier_payments SupplierId + PaidAt
- unique SupplierPayment ClientOperationId
- unique SupplierPaymentReversal SupplierPaymentId
- unique reversal ClientOperationId

Warranty:
- warranty.claims Status + ReceivedAt
- warranty.claims SupplierId + Status + ReceivedAt
- warranty.claims CustomerId + Status + ReceivedAt
- warranty.shop_stock_cases SupplierId + Status + CreatedAt
- existing claim/unit linkage indexes retained

Exact identifier search must use indexed equality before name/fuzzy search.

Do not introduce full-text/trigram extensions unless profiling shows ordinary indexed search is insufficient for the expected local retail dataset.

## 230.1 History Ordering and Seek-Index Alignment

History/read indexes must support the actual filter + deterministic sort shape used by the query. A timestamp-only sort without a unique tie-breaker is not sufficient for keyset pagination.

Representative seek shapes include:

```text
Sales History:
filter(s) + CompletedAt DESC + Id DESC

Purchase History:
filter(s) + PurchaseDate/CreatedAt DESC + Id DESC

Inventory Movement History:
ProductId/other filter + OccurredAt DESC + Id DESC

Supplier Khata:
SupplierId + OccurredAt DESC + Id DESC

Warranty queue/history:
Status/Supplier/Customer filter + ReceivedAt/CreatedAt DESC + Id DESC

Audit:
Entity/User/Type filter + OccurredAt DESC + Id DESC
```

The exact physical index column order is chosen from measured query predicates/selectivity and PostgreSQL plans; this list describes required query support, not a command to create every permutation.

Do not add a large family of overlapping indexes merely to satisfy hypothetical filters. Index write/storage cost remains part of the decision.

## 230.2 Read DTO and Count Contract

Paged list queries return a bounded DTO shape such as:

```text
Items
NextCursor / HasMore
optional TotalCount
query/filter metadata required by the screen
```

The read path must not require `SELECT *` semantics for large list screens.

Exact `TotalCount` is not mandatory on every page refresh. When requested, it may use a separate count query/optimized path and must not force the item query to materialize all matches.

Detail/history expansion is requested separately from the list page.

Search requests that supersede an earlier request carry cancellation/version identity so a slower old response cannot overwrite newer UI state.

---

# 231. POS and Khata Data-Retention Rules

POS Draft:
- converted Draft remains historically identifiable as converted;
- cancelled Draft may be retained for a configurable operational retention period;
- expired/cancelled Draft cleanup must never affect completed Sales;
- Draft cleanup is a Worker/maintenance concern and not a Sale mutation.

Supplier Khata:
- posted account entries are retained permanently with business history;
- posted Supplier Payments and reversals are retained permanently;
- Supplier deactivation does not remove Khata;
- Product deactivation does not alter Supplier financial history;
- Warranty closure does not remove linked financial credit history.

Historical identifiers such as PaymentNumber, EntryNumber, PurchaseNumber, ClaimNumber, TrackingCode, and InvoiceNumber are never recycled.

---

# 232. Mandatory End-to-End Tests for New Architecture

POS:
1. Price Check causes zero writes.
2. Search by SKU resolves Product.
3. Search by barcode resolves ProductUnit.
4. Search by TrackingCode resolves exact InventoryUnit.
5. Search by Serial/IMEI resolves exact InventoryUnit.
6. Save Draft creates no Sale/stock/cash effects.
7. Resume Draft revalidates current price and stock.
8. Two concurrent Draft conversions allow only one completion.
9. Quotation load still completes through the normal Sale engine.
10. Unauthorized Price Override is rejected.
11. Authorized Price Override snapshots list price, final price, actor, and reason.
12. Below-cost override requires stronger permission.

Supplier Khata:
13. Purchase GrandTotal increases payable.
14. Unpaid Purchase leaves full payable.
15. Partial payment leaves exact remainder.
16. Full payment closes exact payable.
17. CashDrawer SupplierPayment creates matching cash-out atomically.
18. External SupplierPayment does not affect cash drawer.
19. Payment retry with same ClientOperationId is idempotent.
20. Payment reversal restores payable and cash consistently.
21. Purchase Return reduces payable by SupplierReturnValue, not InventoryCostRemoved.
22. Purchase Void reverses original payable charge.
23. Paid Purchase then Return may create Supplier credit if not yet refunded.
24. Negative Supplier balance is surfaced as SupplierCreditOrAdvance.
25. Account adjustment requires permission, reason, idempotency, and audit.

Warranty:
26. Warranty dashboard counts statuses correctly.
27. WarrantyClaimCount, WarrantyClaimLineCount, and TrackedWarrantyUnitCount remain distinct; claimed quantity is reported only within compatible Product/BaseUnit groups.
28. With-Supplier queue matches custody/state truth.
29. Ready-for-Customer queue matches status truth.
30. TrackingCode search resolves original Supplier/Sale provenance.
31. Replacement keeps a new TrackingCode and old/new linkage.
32. Warranty Credited resolution alone creates no Khata entry.
33. Explicit WARRANTY_CREDIT amount creates the correct Supplier Khata effect.
34. Supplier warranty summary matches detailed underlying claims/cases.

Navigation/Scope:
35. Full-screen navigation contract contains exactly 19 screens.
36. New Sale naming is absent from user-facing navigation.
37. Supplier Khata remains inside Suppliers.
38. Warranty is accessible as a dedicated full screen.

SupplierProduct Sequence / Concurrency Hardening:
39. Two concurrent receipts for the same SupplierProduct allocate non-overlapping committed sequence ranges.
40. Ten concurrent receipts for the same SupplierProduct produce unique TrackingCodes with no duplicate committed business effect.
41. One multi-unit receipt reserves the required contiguous sequence range without one lock cycle per physical unit.
42. Concurrent receipts for unrelated SupplierProducts are not serialized by an unnecessary global SupplierProduct lock.
43. Forced lock timeout rolls back the current operation with no partial Purchase/Inventory/TrackingCode effect.
44. Bounded automatic retry reuses the same ClientOperationId and cannot duplicate a committed Purchase or sequence allocation.
45. Ambiguous response after a successful commit, followed by replay with the same ClientOperationId, resolves to the original result/effect.
46. Retry backoff occurs only after the failed transaction has released authoritative locks.
47. Lock-wait and transaction-duration diagnostics expose measurable contention evidence without logging secrets.
48. Stress evidence records representative p50/p95/p99 operation/lock-wait behavior before production threshold tuning.

Print Delivery / Reprint Hardening:
49. Sale commit and its required ORIGINAL receipt request persist atomically; rollback leaves neither committed Sale nor orphan print request.
50. Tracked Purchase/Warranty replacement commit and required ORIGINAL label requests persist atomically with their committed InventoryUnit/TrackingCode identities.
51. App crash after business commit but before printer dispatch leaves a recoverable PENDING request.
52. App crash while a request is DISPATCHING becomes OUTCOME_UNKNOWN unless the device protocol proves non-delivery.
53. FAILED_RETRYABLE may retry without any stock, finance, Khata, sequence, or identity mutation.
54. OUTCOME_UNKNOWN is not blindly auto-reprinted.
55. Receipt reprint reproduces the original committed historical snapshot and creates no Sale/Payment mutation.
56. Purchase-document reprint changes no Purchase, payable, inventory, cost, cash, or Supplier Khata state.
57. TrackingCode label reprint preserves the same InventoryUnitId, ItemSequence, and TrackingCode and consumes no new SupplierProduct sequence value.
58. Reprint double-click/retry with the same request operation identity is idempotent.
59. Multi-label receipt creates one stable logical ORIGINAL artifact identity per required committed unit/label part without duplicate ORIGINAL requests on business-command replay.
60. PRINTED, CANCELLED, FAILED_RETRYABLE, OUTCOME_UNKNOWN, and ACTION_REQUIRED delivery-state transitions never become business transaction authority.
61. Print retry/backoff occurs outside authoritative business locks/transactions.
62. Print diagnostics/health expose pending/action-required delivery state without secrets or unnecessary customer-sensitive payload logging.

Fractional Exact-Unit Hardening:
63. SERIALIZED 1.25 Cartons x factor 4 evaluates to exact BaseQuantity 5 and commits only with exactly 5 physical identities.
64. SERIALIZED 1.10 Cartons x factor 4 evaluates to exact BaseQuantity 4.40 and is rejected before SupplierProduct sequence allocation.
65. Fractional exact-unit conversion cannot be accepted by rounding, truncation, floor, ceiling, epsilon, or tolerance logic.
66. Tracked receiving with fewer or more captured identities than whole BaseQuantity is rejected with zero partial stock/cost/TrackingCode effect.
67. Tracked Sale rejects fractional BaseQuantity and requires selected InventoryUnit count exactly equal to BaseQuantity.
68. Tracked Thaka issue rejects fractional BaseQuantity and requires selected InventoryUnit count exactly equal to BaseQuantity.
69. Positive tracked Stock Adjustment/opening stock rejects fractional BaseQuantity and requires exact physical identities; Serial/IMEI is additionally required only by Product policy.
70. Tracked Warranty replacement quantity must be whole and creates exactly one replacement InventoryUnit/TrackingCode per replacement physical unit.
71. QUANTITY/LENGTH fractional BaseQuantity remains valid within canonical precision policy and does not create artificial InventoryUnits.
72. UI decimal masking/step controls may prevent invalid entry early, but direct/backend command submission of an invalid tracked fractional quantity is still rejected authoritatively.

Large Dataset / Read Performance Hardening:
73. Large collection query with missing/unbounded PageSize is rejected or capped to the approved maximum rather than returning the full table.
74. Sales/Purchase/Inventory/Supplier Khata/Warranty/Audit large lists apply database-side filter + deterministic sort + projection before materialization.
75. Keyset pagination with many rows sharing the same timestamp uses the unique Id tie-breaker and returns every row exactly once across page traversal.
76. Deep-history traversal uses the approved seek/keyset path where configured and does not degrade into ever-growing OFFSET scans merely because the UI requests later pages.
77. Optional TotalCount can be omitted; requesting it does not force item materialization of all matches.
78. Representative list/detail reads do not exhibit an N+1 per-row query pattern.
79. Report queries aggregate in PostgreSQL and return bounded DTO/series results rather than transferring the full underlying ledger to WPF.
80. Exact SKU/barcode/TrackingCode/Serial/IMEI lookup uses indexed equality before broader name/fuzzy search.
81. WPF large-list screens keep row/item virtualization/recycling enabled or prove an equivalent bounded rendering strategy.
82. Incremental/page loading keeps the active UI collection/window bounded rather than accumulating the entire history in one ever-growing ObservableCollection.
83. Superseded search request is cancelled/versioned so its late response cannot overwrite a newer search result.
84. Database query/result materialization for large lists does not synchronously block the WPF UI thread.
85. Representative benchmark fixture covers at least the Section 76.4 order-of-magnitude dataset, or a larger documented equivalent.
86. Performance evidence records p50/p95/p99 plus relevant database/query-plan and WPF memory evidence for critical read paths before production threshold tuning.
87. Index changes for a slow query are justified by measured predicate/order/query-plan evidence rather than blind index proliferation.

LAN Disconnect / Reconnect Hardening:
88. DISCONNECTED terminal may preserve an unsaved cart/UI state but cannot commit any authoritative business mutation.
89. RECONNECTING terminal does not start a new authoritative write until authoritative connectivity is restored.
90. Network drop before request reaches server produces no committed business effect and the preserved intent may be resubmitted under normal validation.
91. Network drop after server commit but before response delivery, followed by replay with the same ClientOperationId, resolves to exactly one committed business effect.
92. Unknown-outcome replay with a new ClientOperationId for the same intended operation is rejected by workflow policy and never used as the automatic recovery path.
93. Preserved POS cart after reconnect reloads current Product/ProductUnit price and stock before completion; stale price/stock is surfaced rather than silently committed.
94. Exact tracked InventoryUnit sold/moved by another terminal during disconnect is unavailable after reconnect and the preserved cart cannot complete with stale unit ownership.
95. Supplier Payment/Refund intent preserved across disconnect revalidates the current Supplier payable/credit balance before commit.
96. Warranty intent preserved across disconnect revalidates current claim/case state, custody, exact-unit state, and permissions before mutation.
97. Expired/revoked session or permission change during disconnect blocks the reconnected command until normal authentication/authorization succeeds.
98. Terminal cannot allocate ItemSequence/TrackingCode, create shadow stock, or mutate Supplier Khata while disconnected.
99. Two terminals reconnecting with competing intent for the same exact InventoryUnit produce one authoritative winner according to normal locking/concurrency rules, with no merge-created duplicate truth.
100. Receipt/label delivery after central commit remains terminal-local and a printer/network delivery failure does not create a second Sale/Purchase business transaction.
101. LAN recovery uses the existing idempotency/retry engine and does not introduce a second offline synchronization/merge ledger.
102. Terminal-local cached/read-model state never overrides current server truth after reconnect.

Architecture / Code Drift Hardening:
103. EF model-drift gate reports zero pending model changes against the checked-in migration snapshot.
104. Disposable PostgreSQL migration from empty database to current baseline succeeds.
105. Required Down-to-zero then Up migration rehearsal succeeds where the project baseline policy requires it.
106. Physical schema inspection confirms canonical uniqueness/check/index constraints required for TrackingCode, SupplierProduct, Khata source identity, and active exact-unit Warranty claim barriers.
107. Architecture test fails if a second TrackingCode sequence authority or parallel manual/random sequence generator is introduced.
108. Architecture test fails if a mutable Supplier balance column becomes payable authority.
109. Khata contract tests fail if EntryType/Direction mapping or generated-source uniqueness diverges from the canonical rules.
110. Warranty contract tests fail if an illegal status/custody transition or unsupported service-center custody is introduced.
111. Lock-order architecture/concurrency tests fail if handlers acquire canonical resource families in conflicting order.
112. Permission contract tests confirm protected high-risk commands require the canonical granular permissions/audit obligations.
113. Navigation contract test confirms exactly 19 authoritative full screens and no stale New Sale primary route.
114. LAN architecture test prevents terminal-side direct PostgreSQL business authority/offline write engine from being introduced.
115. CI fails when prohibited duplicate Sale/Purchase/Warranty write engines or duplicate canonical table authorities are introduced.
116. Intentional canonical invariant change is accompanied by architecture text + regression test + model/migration update in the same change set where applicable.
117. A green compile with failing architecture/model/migration gates is not releasable.

Audit / Stale-Claim Quarantine Hardening:
118. ItemSequence 1000000 remains valid and no 999999 exhaustion/rollover rule is reintroduced.
119. Navigation contract remains exactly 19 full screens and Warranty remains screen #17 in the Section 209 ordering.
120. Deterministic lock-order contract remains the 10-level Section 185 hierarchy; stale 8-level descriptions cannot become current authority.
121. Schema-contract tests reject parallel inventory.inventory_units / inventory.inventory_lots authorities and preserve inventory.units / inventory.lots.
122. Deep-history performance contract does not regress into a universal OFFSET/LIMIT rule; deterministic keyset/seek remains available/preferred for deep growing histories where appropriate.
123. Architecture-test existence/pass status is accepted only from repository/CI evidence, not inferred from architecture prose.
124. Sprint/implementation completion status is accepted only from current workspace/build/test evidence, not from historical audit notes.
125. Later corrected Warranty and Supplier Khata rules cannot be overridden by an older audit/forensic statement.
126. Historical/archive architecture material cannot silently supersede the current canonical authority index/Section 228 declaration.
127. A newly accepted forensic defect must enter through an explicit canonical correction plus applicable regression coverage, not by treating the audit report itself as authority.

## 232.1 Supplementary Hardening Regression Matrix

Hardening 9 consolidates the supplementary contracts without converting unexecuted implementation requirements into false PASS claims.

Status vocabulary:

```text
ARCHITECTURE_REGRESSION_PASS
= canonical rule/test/gate is present, internally consistent, and passes documentary/structural validation.

RUNTIME_EVIDENCE_REQUIRED
= implementation/integration/stress/crash/performance/CI execution must still provide physical evidence.

RUNTIME_PASS
= may be used only after the corresponding implementation test was actually executed successfully with evidence.
```

| Regression Area | Canonical Authority | Section 232 Coverage | Hardening 9 Architecture Status | Runtime / CI Evidence |
|---|---|---:|---|---|
| PostgreSQL durability + crash/restart safety | Sections 61.1-61.2, 70.1, 78.1 | durability/crash release contract | ARCHITECTURE_REGRESSION_PASS | RUNTIME_EVIDENCE_REQUIRED |
| SupplierProduct concurrency + sequence uniqueness + retry | Sections 178.4, 185.1-185.2 | 39-48 | ARCHITECTURE_REGRESSION_PASS | RUNTIME_EVIDENCE_REQUIRED |
| Durable post-commit receipt/label delivery | Sections 51.1-51.3, 178.3 | 49-62 | ARCHITECTURE_REGRESSION_PASS | RUNTIME_EVIDENCE_REQUIRED |
| Reprint identity safety / no new sequence | Sections 51.2-51.3 | 55-61 | ARCHITECTURE_REGRESSION_PASS | RUNTIME_EVIDENCE_REQUIRED |
| Fractional exact-unit rejection / identity cardinality | Sections 107, 140, 177.3, 178.2 | 63-72 | ARCHITECTURE_REGRESSION_PASS | RUNTIME_EVIDENCE_REQUIRED |
| Large dataset paging + WPF bounded rendering | Sections 76.1-76.4, 230.1-230.2 | 73-87 | ARCHITECTURE_REGRESSION_PASS | RUNTIME_EVIDENCE_REQUIRED |
| LAN disconnect/reconnect + unknown outcome replay | Sections 82-82.3, 225 | 88-102 | ARCHITECTURE_REGRESSION_PASS | RUNTIME_EVIDENCE_REQUIRED |
| Architecture/code CI drift gate | Section 78.2 | 103-117 | ARCHITECTURE_REGRESSION_PASS | RUNTIME_EVIDENCE_REQUIRED |
| Stale forensic/audit claim quarantine | Section 0, Section 207.1 | 118-127 | ARCHITECTURE_REGRESSION_PASS | RUNTIME_EVIDENCE_REQUIRED where repository/build claims are involved |
| Supplier Khata direction/source/idempotency continuity | Sections 215-220, 225 | existing Supplier Khata tests + 103-117 | ARCHITECTURE_REGRESSION_PASS | RUNTIME_EVIDENCE_REQUIRED |
| Warranty transition/custody/replacement/credit continuity | Sections 221-225 + earlier corrected Warranty rules | existing Warranty tests + 103-117 | ARCHITECTURE_REGRESSION_PASS | RUNTIME_EVIDENCE_REQUIRED |
| Navigation/schema/lock-order authority continuity | Sections 185, 205-209 | 113, 118-126 | ARCHITECTURE_REGRESSION_PASS | RUNTIME_EVIDENCE_REQUIRED |

Hardening 9 documentary regression must verify, at minimum:

```text
database durability contract still present
SupplierProduct critical-section/retry contract still present
print ORIGINAL/REPRINT/OUTCOME_UNKNOWN contract still present
exact tracked fractional-unit rejection still present
large-read bounded paging/keyset/WPF rules still present
LAN no-offline-authority and same-operation replay still present
architecture/code drift gate still present
stale audit claims remain quarantined
Supplier Khata direction/source authority remains corrected
Warranty transition/custody/replacement authority remains corrected
19-screen navigation remains current
10-level lock order remains current
canonical table names remain current
root pointer remains pointer-only
numbered sections remain 0-233 unique
Markdown remains structurally balanced
```

If a documentary regression fails, fix the canonical contradiction and rerun Hardening 9 before closure.

Hardening 9 does not replace Phase 3 physical implementation inspection. It proves the hardened architecture remains internally coherent before Phase 3 independently validates code, migrations, tests, and runtime behavior.

---

# 233. Architecture-to-Implementation Gate

Sections 170-233 are now the authoritative post-forensic architecture tail.

Before declaring these additions physically complete:

- Release build must have zero errors and zero warnings attributable to the new implementation.
- EF Core model drift must be zero.
- Fresh baseline migration Up / Down-to-zero / Up must pass.
- Unit tests must pass.
- PostgreSQL integration tests must pass.
- POS Draft tests must prove no stock/cash side effects.
- Supplier Khata tests must prove exact payable arithmetic and idempotency.
- Warranty read-side tests must prove count/quantity correctness.
- SupplierProduct sequence concurrency, timeout rollback, ambiguous-response replay, and bounded-retry stress tests from Section 232 must pass.
- Durable print-request atomicity, crash recovery, unknown-outcome handling, and reprint identity tests from Section 232 must pass.
- Fractional exact-unit conversion, identity-cardinality, Sale/Thaka/Adjustment/Warranty, and backend-authority tests from Section 232 must pass.
- Large-dataset bounded-query, deterministic-pagination, WPF virtualization/cancellation, and benchmark evidence tests from Section 232 must pass.
- LAN disconnect/reconnect, unknown-outcome replay, stale-state revalidation, exact-unit contention, and no-offline-authority tests from Section 232 must pass.
- Architecture/code-drift, EF model/migration, schema-contract, permission/state, lock-order, Khata, Warranty, navigation, and duplicate-authority tests from Section 232 must pass.
- Stale-audit quarantine tests from Section 232 must prove that superseded sequence, navigation, lock-count, schema-name, pagination, and implementation-status claims cannot become current authority.
- Section 232.1 supplementary regression matrix must remain internally consistent, and no row may be promoted from RUNTIME_EVIDENCE_REQUIRED to RUNTIME_PASS without actual execution evidence.
- Existing Sales, Purchasing, Inventory, Tracking, Warranty, Cash, and Audit regression suites must remain green.
- No duplicate Sale/Purchase/Warranty engine may exist.
- No mutable Supplier balance column may exist.
- No frontend screen may become financial authority.

Only after this gate should the implementation be considered aligned with the updated architecture.

---

## 233.1 International Forensic Audit Remediation Annex - 2026-09-22

This annex incorporates the confirmed gaps from the international-level forensic architecture audit. It is part of Section 233 architecture authority and supersedes any older omission or contradictory note on the subjects below.

### A. Canonical Authority Manifest and Supersession Governance

The canonical architecture Markdown is the only narrative business-architecture authority. A machine-readable companion manifest is maintained at `docs/Architecture_Authority_Manifest.json` and records at minimum:

```text
ArchitectureVersion
CanonicalRelativePath
CanonicalSha256
ArchitectureCoverage
EffectiveAtUtc
Supersedes
SchemaEpoch/Baseline policy reference
NavigationContractVersion
LockHierarchyVersion
```

The SHA is external because embedding the file's own SHA inside itself is self-referential. Historical architecture files and forensic reports are evidence/history only and must be treated as `SUPERSEDED_NOT_AUTHORITY` when they conflict with this canonical file. The root `EDGE_RETAILS_BACKEND_ARCHITECTURE_FINAL.md` remains pointer-only.

### B. Currency, Business Date, and Time-Zone Governance

V1 is single-currency. For the Pakistan deployment profile the authoritative shop currency is `PKR`. Shop currency is configured during controlled setup and becomes immutable after the first posted financial transaction unless a future explicit currency-migration workflow is introduced.

All event timestamps remain UTC `timestamptz`. Posted financial/business documents also persist an authoritative `BusinessDate` derived from the configured Shop Time Zone at posting time. A later Shop Time Zone change never rewrites historical UTC timestamps or historical BusinessDate values and is audited because it changes future day/report cutoffs.

### C. AttributesJson Schema Governance

`AttributesJson` is extensibility data, not an ungoverned second schema. Each payload is interpreted under an explicit `AttributesSchemaVersion` plus validated keys/types/ranges. Unknown incompatible versions fail controlled validation. Frequently queried or business-critical attributes are promoted to typed columns or intentionally indexed JSON paths. Serial, IMEI, TrackingCode, stock, cost, warranty authority, Supplier identity, price, and other relational invariants never move into JSON merely to avoid a migration.

### D. Backup Key Lifecycle and Remote Verification Levels

Backup encryption metadata identifies `EncryptionVersion`, `KeyId`, `KeyVersion`, and creation time. Key rotation must preserve decryptability of every retained backup. Historical backup keys cannot be retired until retention proves no required object depends on them, unless the Owner performs an explicit destructive retirement policy. Key loss is surfaced as a disaster-recovery condition, never disguised as generic corruption.

Cloud backup verification levels are distinct:

```text
UPLOADED
REMOTE_HASH_VERIFIED
REMOTE_MANIFEST_AUTH_VERIFIED
RESTORE_REHEARSAL_VERIFIED
```

`UPLOADED` alone is never presented as proof of recoverability. Diagnostics expose the strongest achieved level and its timestamp.

### E. Irreversible External Side-Effect Finality Contract

Every externally visible action that can succeed without a reliable caller response follows one common certainty model:

```text
INTENT_PERSISTED
-> EXECUTION_STARTED
-> CONFIRMED | OUTCOME_UNKNOWN | ACTION_REQUIRED
```

This applies to physical printing/spool submission, cloud backup upload, restore/cutover orchestration, license persistence outside the business DB transaction, future signed updater execution, and future external regulatory/tax submission.

Once an irreversible effect may have happened, later audit/diagnostic failure must not falsify the effect as failed. Caller cancellation after the irreversible boundary must not interrupt mandatory safety reconciliation. `OUTCOME_UNKNOWN` is resolved through the same operation identity where possible and never by blind duplicate execution. Post-effect audit failure is a separate degraded diagnostic condition.

### F. Audit Retention, Redaction, and Growth Governance

Business Audit stays append-only in normal behavior. Financial, stock, identity, permission, restore/recovery, and other forensically material events follow an explicit retention/archival policy and are never silently deleted by UI cleanup. Secrets, PIN/password material, DB credentials, cryptographic keys, and unnecessary sensitive payloads never enter audit. Audit exports require permission and are themselves auditable.

High-growth authorities such as `audit.business_events`, inventory movements, Supplier Khata, cash movements, print requests, and idempotency records require monitored size/growth and an explicit operational retention class where business/legal rules permit expiry. Archival or partitioning may be introduced only without changing append-only meaning or breaking correlation identity.

### G. Operational Health and Read/Report Resource Governance

Diagnostics use configurable validated thresholds for database latency, critical lock wait, worker heartbeat age, last successful backup age, last remote-verified backup age, disk warning/critical floor, pending print age/count, `ACTION_REQUIRED`/`OUTCOME_UNKNOWN` print backlog, failed background-job age/count, and reconciliation failures.

Long-running reads/reports must propagate cancellation to PostgreSQL, use an operation-scoped command/statement timeout appropriate to the report class, use read-only transaction/session semantics where a transaction is needed, keep DTO/materialization memory bounded, aggregate in PostgreSQL, and release resources promptly when cancelled. General command timeout must remain meaningfully broader than the narrower lock-wait timeout where both apply.

### H. Application / Database Compatibility Matrix

Startup and recovery explicitly classify:

```text
new app + older supported DB -> approved forward migration after backup/maintenance checks
new app + unsupported-too-old DB -> block and require supported upgrade/recovery path
old app + newer DB -> block; never auto-downgrade
app + unknown/future applied migrations -> block as database-ahead
restored older compatible backup + current app -> validate SchemaEpoch/BaselineId then migrate forward
restored unknown/future backup -> reject before production cutover
```

Migration compatibility compares known and applied migrations in both directions and preserves the frozen post-production migration history.

### I. Terminal Registration, Session Freshness, and Protocol Authority

Each LAN terminal has a stable registered `TerminalId` distinct from UserId/display name. Registration can be active, suspended, or revoked server-side. Every authoritative mutation re-evaluates current session, permissions, terminal status, maintenance state, and command authorization. Cached permissions are never authority. Terminal clock is metadata only. Reconnect performs application/protocol/schema compatibility handshake before writes are enabled. `MaxTerminals` is enforced server-side.

### J. Scanner Namespace, Normalization, and Historical Barcode Identity

Exact scan resolution precedence is:

```text
1. TrackingCode
2. enabled IMEI / manufacturer Serial exact identity
3. active ProductUnit barcode
4. active Product-level barcode
5. broader SKU/name search only for search workflows
```

If one raw scan matches more than one authoritative namespace/entity, backend returns an ambiguity result and never silently chooses query order. TrackingCode is never parsed into relational IDs. IMEI/Serial normalization preserves significant leading zeroes. Product/ProductUnit barcodes use one canonical normalization policy.

A normalized barcode that has participated in posted business history is a historical business identity and must not later be silently reassigned to a different Product/ProductUnit in a way that makes historical scan interpretation ambiguous.

### K. Exact-Unit State to Accounting Mapping and Serialized Adjustment Safety

An `InventoryUnit` row existing does not itself imply shop stock. Every exact-unit status must map explicitly to:

```text
ContributesToStockBalance?
AuthoritativeBucket or NONE
ContributesToProductCostState?
CurrentBusinessOwner = SHOP | CUSTOMER | EXTERNAL/HISTORICAL
MayBeSold?
MayBeReturned?
IsTerminal?
```

Canonical categories include shop-owned active inventory, customer-owned `WARRANTY_CUSTOMER_HELD` / `WARRANTY_CUSTOMER_HANDED_OVER` non-stock identities, sold/issued historical identities, terminal supplier-return/receipt-void/write-off identities, and shop-owned `WITH_SUPPLIER` units that retain carrying cost until terminal resolution.

Architecture/schema tests fail if a new InventoryUnit status is introduced without an explicit accounting mapping. Serialized positive adjustments require one exact identity per added base unit plus origin/cost provenance. Serialized negative adjustments/write-offs require exact InventoryUnit selection; quantity-only decrement that leaves unidentified physical units is forbidden.

### L. Idempotency Retention, Print Provenance, and Purge Safety

Operation identity retention is a correctness policy, not merely log retention. Unresolved `UNKNOWN / RESOLUTION_REQUIRED` operations are never purged. Committed replay-safe identities remain queryable beyond the maximum supported reconnect/offline/operator-recovery/support horizon. A retained record preserves command type, canonical payload fingerprint, actor/terminal where applicable, committed result identity, and timestamps sufficient to reject GUID reuse with different meaning.

Each durable print attempt additionally records delivery provenance such as `PrinterProfileId/ProfileVersion`, printer queue/logical target, media profile, renderer/template version where applicable, requester, reason, and reprint lineage. Changing a print profile never changes the immutable Sale/Purchase/TrackingCode business snapshot.

### M. Required Regression Additions for This Annex

Implementation/CI must add or retain executable checks proving:

```text
canonical authority manifest hash matches canonical file
root architecture file remains pointer-only
currency/business-date history is not rewritten by time-zone changes
AttributesJson schema-version validation
backup key rotation retains old-backup decryptability
remote UPLOADED is not treated as restore-certified
external side-effect audit failure cannot create false negative business outcome
report cancellation/timeout releases DB resources
database-ahead/unknown migration blocks startup
revoked terminal/session cannot mutate after reconnect
ambiguous scanner namespace fails closed
new exact-unit status without accounting mapping fails architecture tests
serialized negative adjustment requires exact unit selection
unresolved idempotency identity is not purged
reprint profile provenance changes delivery metadata only, never business truth
```

These additions do not convert unexecuted runtime requirements into PASS evidence. Section 233 runtime/CI gates remain mandatory.
### N. Physical Identity Encoding, TrackingCode Label, Serial, and IMEI Normalization

`TrackingCode` remains the immutable Edge Retails business identity and must fit the canonical database/UI limit and the approved physical label profile. DealerCode/SKU policies must prevent generation of a TrackingCode that exceeds the persisted maximum length.

Before a label profile is approved for production, representative longest TrackingCodes are verified for the selected symbology, printer DPI, label width, quiet zone, human-readable text, and scanner distance. Code128 may be used where it remains reliably scannable; QR or another approved 2D representation may be used where density requires it. The encoded value always represents the same committed TrackingCode and never creates a second identity.

Manufacturer identity normalization is separate from business identity:

```text
TrackingCode -> canonical uppercase/business format
IMEI -> digits only, significant leading zeroes preserved, exact IMEI validation policy
SerialNumber -> raw display snapshot + canonical lookup normalization without destroying significant manufacturer characters
```

When IMEI tracking is enabled, IMEI1 is required and validated under the approved IMEI format/check policy; IMEI2 remains optional but, when supplied, follows the same normalization/uniqueness rules. Normalization is performed before uniqueness lookup. Raw scanned/manufacturer text may be retained as display evidence where useful, but uniqueness uses one canonical normalized value.

### O. Stable Error Contract and Localization Boundary

Application/domain failures expose a stable machine-readable error code independent of the human message.

Rules:
- UI must never branch on English/Urdu error-message text.
- localized operator messages are presentation resources mapped from stable error codes plus safe parameters.
- infrastructure exceptions are translated to controlled application/startup error codes where a user-facing recovery path exists.
- sensitive raw SQL, credentials, key material, filesystem secrets, or internal stack traces never become normal operator messages.
- technical logs may include a correlation identifier and sanitized diagnostics so support can connect the user-visible failure to technical evidence.
- changing message wording/translation does not change business semantics or retry classification.

Regression coverage must include longest supported TrackingCode label encoding, ambiguous/normalized IMEI or Serial lookup, duplicate normalized identity rejection, and UI behavior driven by error codes rather than message-string matching.