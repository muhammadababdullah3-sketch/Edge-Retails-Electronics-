# Edge Retails â€” Final Backend Architecture Report
## Final Implementation Baseline After Phase 16 Gap Resolution

**Product:** Edge Retails  
**Vertical:** Electronics / Electrical Retail  
**Platform:** Windows Desktop POS  
**Status:** FINAL IMPLEMENTATION BASELINE  
**Architecture Coverage:** Phase 1 through Phase 16  
**Supersedes:** Any earlier unresolved or contradictory architecture notes  
**Primary Previous Baseline:** `Edge_Retails_Backend_Architecture_Master_v1.md`

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
â”œâ”€â”€ WPF Presentation
â”œâ”€â”€ Application Layer
â”œâ”€â”€ Domain Layer
â””â”€â”€ Infrastructure Layer
        â†“
Local PostgreSQL
        â†‘
EdgeRetails.Worker.exe
```

Business flow:

```text
View
â†“
ViewModel
â†“
IApplicationGateway
â†“
LocalApplicationGateway
â†“
Command / Query Dispatcher
â†“
Application Handler
â†“
Domain
â†“
Infrastructure
â†“
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

# 8. Catalog Product Model â€” Final

A Product defines what an item is. It does not own current stock.

Core fields:

```text
Id
Sku
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
â†’ SerialTrackingEnabled = false
â†’ ImeiTrackingEnabled = false

SERIALIZED
â†’ at least one identity tracking policy is enabled
```

SKU and barcode are separate concepts.

A product may have multiple barcodes through `catalog.product_barcodes`.

---

# 9. Serial / IMEI Rules â€” Resolved

If Serial Tracking is enabled:

```text
SerialNumber required per serialized unit
```

If IMEI Tracking is enabled:

```text
IMEI1 required
IMEI2 optional
```

Identifiers are normalized before uniqueness checks.

Partial unique indexes protect SerialNumber, IMEI1, and IMEI2.

Serial/IMEI data is never stored only in JSONB.

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
SCRAP
```

SELLABLE is the only stock available for normal POS sale and Thaka issue.

---

# 12. Inventory Movement Model â€” Corrected

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

# 14. Opening Stock â€” Resolved

Product creation has no Initial Stock field.

Opening stock is entered through a controlled Stock Adjustment:

```text
AdjustmentMode = SET_PHYSICAL_COUNT
Reason = OPENING_STOCK
```

Quantity/length opening stock requires a valid cost basis.

If no current authoritative cost exists, ReferencePurchaseCost is proposed; otherwise Unit Cost must be entered.

Serialized opening stock requires exact Serial/IMEI identities and actual acquisition cost per unit.

No generic `Stock +5` operation exists for serialized products.

---

# 15. Stock Adjustment â€” Final Semantics

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
SELLABLE â†’ DAMAGED

LOST
SELLABLE â†’ removed
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

# 16. Inventory Cost Provenance â€” Corrected

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



# 18. Serialized Unit Lifecycle â€” Corrected

Final states:

```text
IN_STOCK
SOLD
ISSUED_THAKA
DAMAGED
DEFECTIVE
SUPPLIER_RETURNED
SCRAPPED
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

A unit cannot be sold while SOLD, ISSUED_THAKA, SUPPLIER_RETURNED, or SCRAPPED.

---

# 19. Serialized Sale History â€” Corrected

A serialized unit may be sold, returned, and legitimately sold again.

Therefore one permanent `InventoryUnit.SaleItemId` relationship is forbidden.

Final historical association:

```text
sales.sale_item_units
---------------------
id
sale_item_id
inventory_unit_id
warranty_months_snapshot
warranty_start
warranty_end
returned_at nullable
is_active
```

A partial unique index allows only one active sale assignment per inventory unit.

Historical inactive sale assignments remain permanently traceable.

---

# 20. Warranty Model â€” Corrected

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

# 22. SaleItem â€” Corrected Discount Model

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

# 23. Complete Sale Price Authority â€” Corrected

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
â†“
License Guard
â†“
Authenticated Session
â†“
sales.create Authorization
â†“
Input Validation
â†“
ClientOperationId Check
â†“
BEGIN
â†“
Reload Product/Price
â†“
Lock Stock in deterministic ProductId order
â†“
Validate Sellable Stock
â†“
Validate Serialized Units
â†“
Recalculate Subtotal
â†“
Allocate Invoice Discount
â†“
Recalculate Grand Total
â†“
Validate Payment
â†“
Generate Invoice Number
â†“
Create Sale + Items + Payment
â†“
Create Inventory Movements/Effects
â†“
Create Lot Consumption
â†“
Update StockBalance / CostState
â†“
Update Serialized Unit states
â†“
Business Audit
â†“
COMMIT
â†“
Receipt Generation / Printing
```

Printer failure never rolls back the committed sale.

---

# 25. Payment Rules â€” Final

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

# 27. Sale Return â€” Final

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

# 28. Partial Refund Rounding â€” Resolved

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

# 29. Return Cost Treatment â€” Final

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

# 30. Recognized Inventory Losses â€” Added

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
OtherCharges Ã— LineShare
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

# 34. Purchase Void â€” Added

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

# 35. Purchase Print â€” Added

Purchase Detail Print uses:

```text
GetPurchaseDocumentQuery
â†“
PurchaseDocumentDto
â†“
IDocumentPrinter
```

Printing never changes Purchase financial state.

A printer failure does not roll back or void a Purchase.

---

# 36. Thaka Core Model

Thaka remains separate from local POS Sales.

```text
Customer
â†“
ThakaProject
â”œâ”€â”€ Material Issues
â”œâ”€â”€ Payments
â”œâ”€â”€ Settlements
â”œâ”€â”€ Reopenings
â”œâ”€â”€ Material Reversals
â””â”€â”€ Payment Reversals
```

Thaka is never stored in the Sales table.

---

# 37. Thaka Revenue Recognition

Revenue is recognized when material is issued.

```text
Material issue
â†’ stock leaves
â†’ customer/project charge increases
â†’ Thaka material revenue increases
```

Payment is collection of receivable, not new revenue.

Thaka remains separate from Today Local Sales and Local Sale Profit.

---

# 38. Thaka Equations â€” Final

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

# 39. Thaka Settlement Cycles â€” Resolved

A settled project may be explicitly reopened.

Reopening does not erase the prior settlement.

Previous payments and valid final discount remain historical financial effects.

New material after reopening creates new balance.

A future settlement creates another settlement cycle.

A wrongly entered settlement discount is corrected through a linked immutable reversal/adjustment, never by editing the old settlement row.

---

# 40. Thaka Material Reversal â€” Promoted to V1

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

# 41. Thaka Payment Reversal â€” Promoted to V1

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

# 42. Serialized Thaka State â€” Added

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

# 44. Supplier Master

Supplier owns contact/master data only.

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

No Accounts Payable ledger exists in V1.

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

# 46. Dashboard KPI Definitions â€” Final

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

# 47. Reports â€” Final Financial Definitions

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

# 48. Reports Visual Scope â€” Final Precedence

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

# 50. Receipt Configuration â€” Corrected

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

# 51. Historical Receipt Snapshot â€” Added

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

# 54. Owner PIN Recovery â€” Resolved Without Backdoor

There is no hidden master PIN.

Recovery uses a one-time signed Recovery Authorization.

```text
Support Recovery Tool
â†“
Signed Recovery Authorization
â†“
Bound to LicenseId + DeviceId + Action + Expiry + Nonce
â†“
Edge Retails verifies embedded Recovery public key
â†“
Owner PIN reset allowed
â†“
Authorization consumed
â†“
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



# 56. Backup Encryption and Cross-Machine Recovery â€” Resolved

Local Windows protection alone is not sufficient for disaster recovery onto a replacement PC.

Final model:

```text
BackupMasterKey (BMK)
â†’ random high-entropy secret

Local BMK copy
â†’ protected using Windows OS-backed protection

Backup Recovery Key (RK)
â†’ separately exported/stored by Owner
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

# 57. Backup Pipeline â€” Final

```text
Schedule / Backup Now
â†“
Acquire Backup Job Lock
â†“
Database Health Check
â†“
PostgreSQL Logical Backup
â†“
Manifest
â†“
Integrity Check
â†“
Compression
â†“
Authenticated Encryption
â†“
Protected Local Backup
â†“
Cloud Upload
â†“
Remote Verification
â†“
Retention
```

A file merely existing does not mean backup succeeded.

Cloud failure does not block POS.

Pending encrypted local backup is retried later.

---

# 58. Restore Architecture â€” Final

Restore remains Owner-only.

```text
Select Backup
â†“
Validate Manifest / Version
â†“
Validate Checksum / Authentication
â†“
Decrypt
â†“
Compatibility Check
â†“
Pre-Restore Safety Backup
â†“
Maintenance Mode
â†“
Block Business Writes
â†“
Restore into Staging Database
â†“
Integrity Checks
â†“
Controlled Database Swap
â†“
Migrations if supported/required
â†“
Post-Restore Reconciliation
â†“
Normal Mode
```

If post-restore verification fails:

```text
SystemState = RECOVERY_REQUIRED
```

Normal POS writes remain blocked.

---

# 59. Restore Operation Journal â€” Resolved

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

# 61. System State Guard â€” Added

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

---

# 62. Final Command Pipeline â€” Corrected

```text
IApplicationGateway
â†“
Operation Context / Correlation
â†“
System State Guard
â†“
License Guard
â†“
Session / Authentication
â†“
Authorization
â†“
Input Validation
â†“
Idempotency
â†“
Transaction Boundary
â†“
Command Handler
â†“
Domain Rules
â†“
Business Audit
â†“
SaveChanges
â†“
COMMIT
â†“
Post-Commit Action
â†“
Result<T>
```

Printing, cloud operations, and other noncritical external work never occur before the business commit.

---

# 63. Application Gateway â€” Final

ViewModels depend on:

```text
IApplicationGateway
```

Standalone V1:

```text
ViewModel
â†“
LocalApplicationGateway
â†“
Command / Query Dispatchers
```

Future LAN:

```text
ViewModel
â†“
RemoteApplicationGateway
â†“
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
â†’ lock relevant StockBalance rows

Serialized Sale
â†’ lock InventoryUnit

Thaka Payment / Settlement / Reversal
â†’ lock ThakaProject

Purchase Return / Void
â†’ lock purchase-origin stock state
```

When multiple products are locked, ProductIds are sorted deterministically before lock acquisition.

Negative stock and double-sale states are never allowed.

---

# 68. Settings Storage â€” Corrected

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

Database/schema/recovery-integrity failure is blocking.

---

# 71. Reconciliation â€” Final

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

# 73. Security Boundary â€” Final

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
â†“
Safe maintenance point
â†“
Pre-update backup
â†“
Install
â†“
Migrate
â†“
Verify
â†“
Restart
```

Silent downgrade is rejected by default.

---

# 75. PostgreSQL Engine Major Upgrade â€” Added

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
Inventory movement
Dashboard
Daily/Monthly/Yearly Reports
```

Use targeted PostgreSQL indexes.

History uses server-side filtering and pagination.

Deep history uses keyset/seek pagination where appropriate.

Reports aggregate inside PostgreSQL and return small DTO/series sets.

No mandatory partitioning, materialized views, or reporting summary tables exist in V1.

They may be introduced only after measured need.

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

Rollback after a failed migration uses tested backup plus compatible prior application rather than relying blindly on reverse migrations.

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
â†“ HTTPS
EdgeRetails.Server (ASP.NET Core)
â†“
same Application / Domain
â†“
PostgreSQL
â†‘
EdgeRetails.Worker
```

Terminals never connect directly to PostgreSQL.

Server reuses existing business handlers rather than duplicating domain logic.

Receipt printing remains local to the terminal after central commit.

---

# 82. Multi-Terminal Offline Rule

First LAN release does not permit authoritative offline writes.

A disconnected terminal may preserve an unsaved local cart.

It cannot complete:

```text
Sale
Purchase
Thaka Material
Thaka Payment
Stock Adjustment
```

until server connection returns.

This deliberately avoids split-brain stock and financial synchronization.

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

Exactly 17 full screens remain:

```text
1  First Setup / License
2  Login / User Switch
3  Dashboard
4  POS / New Sale
5  Sales History
6  Sale Detail
7  Thaka / Projects
8  Thaka Workspace
9  New Purchase
10 Purchase History
11 Inventory
12 Product Detail
13 Expenses
14 Customers
15 Suppliers
16 Reports
17 Settings
```

All correction additions remain overlays, drawers, dialogs, or commands.

No architecture fix adds a new full screen.

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
Supplier accounts payable ledger
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

# 90. Final Architecture Declaration

As of this report:

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

**Edge Retails Phase 1â€“16 architecture is now the FINAL IMPLEMENTATION BASELINE.**

Future work starts with implementation planning and execution, not further architecture invention, unless the product requirements themselves change.



---

# 91. FORENSIC VERIFICATION ADDENDUM â€” Frontend â†” Backend â†” Database

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

# 92. POS and Thaka Separation â€” Reconfirmed

The production POS screen is Local Sale only.

Any current demo `PosSaleMode.ThakaMaterialIssue`, Thaka mode switch, or Thaka issue action inside New Sale is implementation drift and must be removed.

Final paths:

```text
POS / New Sale
â†’ Local Sale

Thaka Workspace
â†’ Add Thaka Material
```

No shared transaction mode is permitted.

---

# 93. Serialized Transaction UI Contract â€” Added

SERIALIZED products require exact-unit selection/capture at every relevant business boundary.

Mandatory UI paths:

```text
POS Sale
â†’ Select Serialized Units

New Purchase
â†’ Capture Received Serialized Units

Sale Return
â†’ Select exact sold units

Purchase Return
â†’ Select exact eligible purchase-origin units

Thaka Material Issue
â†’ Select exact IN_STOCK units

Thaka Material Reversal
â†’ Select exact issued units

Stock Adjustment
â†’ Select/capture exact affected units
```

For serialized products, quantity must equal selected/captured unit count.

Free-form quantity alone is not sufficient.

---

# 94. Serialized PostgreSQL Relationship Set â€” Added

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

# 95. Inventory Lot Sources â€” Corrected

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

# 96. Moving Average Cost Pool â€” Clarified

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
SELLABLE â†” DAMAGED / DEFECTIVE
```

does not itself create COGS or inventory loss.

Sale / Thaka issue removes cost at MWA.

Customer return re-enters the original Sale cost snapshot.

Lost/Scrap write-off removes carrying cost and records RecognizedInventoryLoss.

Purchase Return removes its locked inventory cost amount and recalculates the average.

Lot original cost and MWA must not be treated as the same concept.

---

# 97. Purchase Return Dual Values â€” Clarified

Purchase Return preserves two distinct values:

```text
SupplierReturnValue
â†’ commercial supplier-facing return amount

InventoryCostRemoved
â†’ carrying cost removed from inventory
```

A single monetary field must not represent both concepts.

---

# 98. Sale Return Refund and Disposition Rules â€” Corrected

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
â†’ reason_code / reason_note

sales.return_items
â†’ disposition
```

A return reason must never automatically dictate the inventory bucket without explicit disposition.

---

# 99. Setup, Brand and Operational Visibility â€” Added

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

# 100. License Hardware Replacement â€” Clarified

A DEVICE_MISMATCH cannot be fixed locally by editing database state.

Replacement hardware requires a newly signed license bound to the replacement device, or a separately signed vendor reactivation artifact if such a support workflow is implemented.

There is no local unsigned rebind path.

---

# 101. Physical Implementation Status

Architecture verification and physical implementation verification are separate.

Current workspace status at this forensic pass:

```text
database folder
â†’ empty

Domain / Application / Infrastructure backend folders
â†’ scaffolding

EF Core / Npgsql physical persistence layer
â†’ not implemented yet

PostgreSQL migrations
â†’ not created yet
```

Therefore the architecture is approved, but the physical PostgreSQL schema cannot be called verified until migrations are implemented and tested against a real PostgreSQL database.

---

# 102. Revised Final Declaration

After incorporating this forensic addendum:

```text
Frontend-to-Backend Contract      PASS
17-Screen Coverage                PASS
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
â†’ NOT YET BUILT

Production WPF â†” Backend Integration
â†’ NOT YET COMPLETE
```

The architecture is now cleared for implementation planning and database migration design.

No physical DB implementation should begin from an older schema description without applying Sections 91â€“101 of this addendum.



---

# 103. SHOP-HOLDER OPERATIONAL ADDENDUM â€” Mandatory Before Physical Database

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

Normal customer-credit sales and Supplier Accounts Payable remain outside the current V1 scope unless separately approved.

No physical PostgreSQL migration should be created before Sections 103 onward are incorporated.

---

# 104. Multi-Unit / Packaging Conversion â€” FINAL

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

QUANTITY products may use packaging conversion freely.

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

SERIALIZED products may use packaging units for receiving only if:

```text
Converted Base Quantity is a whole number
and
Captured serialized identities = Base Quantity
```

Example:

```text
1 Carton = 6 Fans
Purchase 2 Cartons
Base Qty = 12 Fans
Required serial identities = 12
```

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
â†’ Piece

Outer Box barcode
â†’ Box (12 Pieces)
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
120 Ã— Rs.100
```

not:

```text
10 Ã— Rs.1,200 as 10 stock units
```

Sale in Box:

```text
1 Box
â†’ consumes 12 Base Units
```

Sale in Piece:

```text
1 Piece
â†’ consumes 1 Base Unit
```

This rule applies identically to Returns, Thaka, Stocktake, and Purchase Return eligibility.

---

# 110. Warranty Module â€” FINAL V1 Addition

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




# 111. Customer Warranty Claim Data Model

Add schema:

```text
warranty
```

Core tables:

```text
warranty.claims
warranty.claim_items
warranty.claim_events
warranty.claim_item_units
```

Claim header:

```text
id
claim_number
customer_id
original_sale_id nullable
supplier_id nullable
status
current_custody
received_at
resolved_at nullable
closed_at nullable
created_by
created_at
version
```

Claim item:

```text
claim_id
original_sale_item_id nullable
product_id
quantity
fault_description
warranty_valid_until nullable
resolution_type nullable
resolution_note nullable
replacement_product_id nullable
replacement_reference nullable
```

Serialized products use `claim_item_units` to link the exact sold InventoryUnit/SaleItemUnit.

---

# 112. Customer Warranty Claim State Machine

Recommended claim states:

```text
RECEIVED
UNDER_REVIEW
SENT_TO_SUPPLIER
SUPPLIER_PROCESSING
READY_FOR_CUSTOMER
CLOSED
CANCELLED
```

Resolution types:

```text
REPAIRED
REPLACED
REJECTED
CREDITED
REFUNDED
OTHER
```

Custody is separate from status:

```text
WITH_CUSTOMER
WITH_SHOP
WITH_SUPPLIER
WITH_SERVICE_CENTER
```

Every custody/status transition creates an immutable `warranty.claim_events` record.

Customer-owned warranty goods never increment:

```text
StockBalance
InventoryLot
ProductCostState
```

because they are not owned shop stock.

---

# 113. Customer Warranty Replacement

If Supplier replaces a customer's item specifically against a Claim:

```text
Supplier Replacement Received
â†“
Warranty Claim Replacement
â†“
Customer Handover
```

The replacement does not need to enter normal Sellable Stock.

For a serialized replacement, capture the new Serial/Identity in the Warranty Claim and preserve:

```text
Original Unit
Replacement Unit/Identity
Claim
Customer
Handover Date
```

If the replacement instead becomes unrestricted shop-owned stock, it must enter inventory through an explicit shop-stock warranty resolution event, not through the customer-claim path.

---

# 114. Shop-Owned Supplier Warranty â€” FINAL

Shop-owned defective goods remain inventory assets while recoverable.

Add inventory bucket:

```text
WITH_SUPPLIER
```

Final quantity buckets:

```text
SELLABLE
DAMAGED
DEFECTIVE
WITH_SUPPLIER
SCRAP
```

Serialized status set gains:

```text
WITH_SUPPLIER
```

Typical flow:

```text
SELLABLE
â†“ defect found
DEFECTIVE
â†“ sent for supplier warranty
WITH_SUPPLIER
â†“ repaired/replaced
SELLABLE
```

Other outcomes:

```text
WITH_SUPPLIER â†’ DEFECTIVE
WITH_SUPPLIER â†’ SCRAP
WITH_SUPPLIER â†’ supplier credit/recovery resolution
```

Moving a recoverable item to WITH_SUPPLIER does not create COGS or inventory loss by itself.

---

# 115. Supplier-Warranty Case Linkage

Shop-owned warranty handling uses Warranty Case records linked to inventory movements.

Required references:

```text
WarrantyCaseId
ProductId
Quantity / exact InventoryUnitIds
SupplierId
SourcePurchaseItemId where known
Fault
SentAt
SupplierReference
Resolution
ReceivedAt
```

Inventory movement types include:

```text
SEND_TO_SUPPLIER_WARRANTY
RECEIVE_REPAIRED_FROM_SUPPLIER
RECEIVE_REPLACEMENT_FROM_SUPPLIER
WARRANTY_REJECTED_RETURN
WARRANTY_WRITE_OFF
```

Exact unit links remain in `inventory.movement_units`.




# 116. Batch Physical Stocktake â€” FINAL V1 Addition

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
â†“
Choose Scope
â†“
Start Counting
â†“
Capture Expected Snapshot
â†“
Physical Count
â†“
Variance Review
â†“
Recount where needed
â†“
Authorized Post
â†“
Generate Stock Adjustment / Movement Effects
â†“
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

# 120. Non-Sellable Inventory Operations â€” FINAL

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
SELLABLE â†’ DAMAGED
SELLABLE â†’ DEFECTIVE
```

does not automatically create COGS or loss while the item remains a recoverable shop asset.

```text
DAMAGED / DEFECTIVE â†’ SELLABLE
```

restores availability without creating revenue.

```text
DAMAGED / DEFECTIVE â†’ WITH_SUPPLIER
```

preserves carrying cost.

```text
DAMAGED / DEFECTIVE / WITH_SUPPLIER â†’ SCRAP
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

Inventory screen may expose these through tabs/filters/drawers without adding a new full screen.




# 123. Cash Session / Daily Closing â€” STRONG V1 Addition

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

# 124. Cash Movement Rules

Cash movement is append-only and linked to a business source where applicable.

Types:

```text
SALE_CASH_IN
SALE_REFUND_CASH_OUT
THAKA_PAYMENT_CASH_IN
EXPENSE_CASH_OUT
MANUAL_CASH_IN
MANUAL_CASH_OUT
PURCHASE_CASH_OUT optional explicit drawer payment
```

Expected Cash:

```text
Opening Cash
+ Cash In Movements
- Cash Out Movements
=
Expected Closing Cash
```

Closing:

```text
Counted Closing Cash
-
Expected Closing Cash
=
Over / Short Difference
```

Bank and Other payment methods do not affect cash-drawer expected balance.

---

# 125. Cash Session Transaction Integration

When a business transaction uses CASH and is designated as drawer-affecting, its cash movement is written in the same PostgreSQL transaction.

Examples:

```text
Cash Sale commit
â†’ Sale + Payment + SALE_CASH_IN

Cash Sale Return
â†’ Return + SALE_REFUND_CASH_OUT

Cash Thaka Payment
â†’ ThakaPayment + THAKA_PAYMENT_CASH_IN

Cash Expense
â†’ Expense + EXPENSE_CASH_OUT
```

A Purchase only affects the drawer if the user explicitly marks it as paid from the cash counter.

This does not create Supplier Accounts Payable.

Manual Cash In/Out requires:

```text
Reason
Amount
Actor
Note
```

and is always audited.

---

# 126. Daily Closing Workflow

```text
Open Cash Session
â†“
Enter Opening Cash
â†“
Normal Shop Operations
â†“
Close Day / Close Cash
â†“
System Calculates Expected Cash
â†“
User Counts Physical Cash
â†“
Difference Shown
â†“
Confirm Close
â†“
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

# 127. Quotation / Estimate â€” STRONG V1 Addition

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
â†’ Convert to POS Sale
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

# 130. Frontend Placement Without New Full Screens

The full-screen count remains 17.

Use existing screens:

```text
Inventory
â†’ tabs/filters/drawers for:
   Warranty
   Non-Sellable Stock
   Stocktake

POS / New Sale
â†’ New Quotation action / Quotation drawer
â†’ Convert Quotation to Cart

Dashboard / Reports
â†’ Open/Close Cash Session drawer
â†’ Daily Closing summary

Customer Detail
â†’ Warranty Claims history
â†’ Quotations history
```

No new primary navigation screen is required for V1.




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
â†’ all

MANAGER
â†’ warranty, stocktake, non-sellable, quotation
â†’ cash close optional/yes by policy
â†’ stocktake post optional

CASHIER
â†’ quotation create optional
â†’ warranty claim intake optional
â†’ no stocktake post
â†’ no non-sellable write-off
â†’ no manual cash movement by default
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

# 133. PostgreSQL Schema Additions Before Migration 001

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
Box â†’ Piece conversion
Roll â†’ Meter conversion
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
Supplier Accounts Payable
Supplier Payment Ledger
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

# 139. Revised Final Declaration After Shop-Holder Audit

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
Supplier Accounts Payable        DEFERRED
Full Accounting Ledger           OUT OF V1
```

The critical and strongly recommended shop-holder operational gaps are now incorporated into the authoritative architecture.




# 140. Quantity / Conversion Precision â€” Corrected

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

Each InventoryUnit must retain:

```text
InventoryUnitId
ProductId
SerialNumber / IMEI as configured
AcquisitionCost
SourcePurchaseItemId
InventoryLotId
Status
```

InventoryLotId is required for exact serialized cost provenance.

On Sale:

```text
IN_STOCK -> SOLD
```

and the exact source lot carrying quantity is consumed.

On Sale Return, the exact originally sold unit must be returned.

Examples:

```text
SOLD -> IN_STOCK
SOLD -> DAMAGED
SOLD -> DEFECTIVE
SOLD -> SCRAPPED
```

A returned serialized unit receives a new return-lot link representing its new inventory state.

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
SettlementMode
CreatedBy
CreatedAt
ClientOperationId
Status
```

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

# 156. Purchase Settlement Modes and Cash Session Effects

Normal Supplier Accounts Payable remains deferred.

V1 Purchase settlement is explicitly classified:

```text
EXTERNAL
CASH_DRAWER
```

EXTERNAL means the transaction is commercially recorded but no local cash-drawer movement is created.

CASH_DRAWER requires an open CashSession and creates:

```text
PURCHASE_CASH_OUT
```

Purchase Return settlement is separately explicit:

```text
EXTERNAL
CASH_DRAWER
```

CASH_DRAWER Purchase Return creates:

```text
PURCHASE_RETURN_CASH_IN
```

Purchase Void of a drawer-paid purchase requires an open session and creates:

```text
PURCHASE_VOID_CASH_IN
```

Cash movement is operational drawer truth, not profit calculation.

---

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

# 167. Migration Strategy After Final Sales/Purchasing Closure

Because no production Edge Retails PostgreSQL database has yet been established as an applied migration authority, the first physical production migration should represent the complete approved V1 baseline rather than a deliberately incomplete single-unit or demo-era schema.

Migration 001 must include, at minimum:

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
System document sequences
Required audit structures
Required constraints / filtered unique indexes
```

Before release:

```text
Model -> Migration drift = zero
Migration Up = pass
Migration Down = pass in disposable test DB
Migration Up again = pass
Real PostgreSQL integration tests = pass
```

Never rewrite an already-deployed production migration. This clean-baseline rule is valid only while no production database depends on an older migration history.

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

# 169. Revised Architecture Declaration

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
Fresh complete Migration 001 strategy           FINAL WHILE NO PROD DB EXISTS

Normal Customer Credit / Udhaar                 DEFERRED
Supplier Accounts Payable                       DEFERRED
General Ledger / Double Entry                   OUT OF V1
Frontend production integration                 SEPARATE TASK
```

This addendum supersedes any older contradictory Sales/Purchasing or cloud-control notes.

