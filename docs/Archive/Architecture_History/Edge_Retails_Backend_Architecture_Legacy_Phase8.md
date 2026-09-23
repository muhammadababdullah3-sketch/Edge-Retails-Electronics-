# ARCHIVED / NON-AUTHORITATIVE ARCHITECTURE HISTORY

> Historical Phase 8 baseline only. Do NOT use this file for current implementation decisions.
> Current authority: docs/Edge_Retails_Final_Architecture_Report_v1.md (Sections 1-233).

---

# Edge Retails — Backend Architecture Master
## Architecture Baseline Through Phase 8

**Product:** Edge Retails  
**Scope:** V1 Electronics / Electrical Retail POS  
**Platform:** Windows Desktop  
**Architecture Status:** Designed through Phase 8  
**Next Planned Phase:** Phase 9 — Application / Domain Code Architecture  
**Purpose:** Single source of truth for all backend decisions locked so far against the frontend contract.

---

# 1. Architecture Context

Edge Retails is a local-first Windows desktop POS for electronics and electrical retail shops.

The backend is designed according to the frontend contract. Every screen, button, overlay, KPI, filter, transaction, permission-sensitive action and report must map to an explicit backend use case, validation rule, transaction boundary and authoritative data source.

Canonical V1 full screens:

1. First Setup / License
2. Login / User Switch
3. Dashboard
4. POS / New Sale
5. Sales History
6. Sale Detail
7. Thaka / Projects
8. Thaka Workspace
9. New Purchase
10. Purchase History
11. Inventory
12. Product Detail
13. Expenses
14. Customers
15. Suppliers
16. Reports
17. Settings

Important overlays and dialogs:

- Complete Sale
- Sale Return
- New Thaka
- Add Thaka Material
- Record Thaka Payment
- Final Settlement
- Add/Edit Product
- Stock Adjustment
- Add/Edit Expense
- Add/Edit Customer
- Add/Edit Supplier
- Purchase Return
- Purchase Detail
- Customer Detail
- Supplier Detail
- Confirmation
- Permission Required
- Toast / Validation

Core mapping:

    Frontend
      ↓
    User Action
      ↓
    Command / Query
      ↓
    Application Use Case
      ↓
    Domain Rules
      ↓
    Infrastructure
      ↓
    PostgreSQL

---

# 2. Final Locked Technology Stack

    Windows Desktop
    + WPF
    + C#
    + XAML
    + .NET 10 LTS
    + MVVM
    + Modular Monolith
    + PostgreSQL
    + Npgsql
    + Entity Framework Core
    + Dapper / Raw SQL
    + .NET Worker Service
    + Serilog
    + xUnit
    + WiX Toolset + Burn
    + Encrypted Cloud Backup
    + Local Signed Licensing

Runtime:

    Windows PC
    ├── EdgeRetails.exe
    ├── EdgeRetails.Worker.exe
    └── PostgreSQL Service
          └── edge_retails

No unnecessary localhost HTTP API is required for the initial single-PC V1.

ASP.NET Core is reserved for a future multi-terminal/LAN service boundary.

---

# 3. Architectural Style

Edge Retails uses a Modular Monolith.

Dependency direction:

    WPF View
      ↓
    ViewModel
      ↓
    Application Layer
      ↓
    Domain Layer
      ↓
    Infrastructure
      ↓
    PostgreSQL

Rules:

- Business logic must not live in View or code-behind.
- ViewModels must not directly build SQL.
- Domain must not depend on WPF, EF Core, Dapper, Npgsql, PostgreSQL or Serilog.
- Infrastructure implements persistence, printing, licensing, backup and hardware adapters.
- EF Core is used for transactional business operations, CRUD, relationships and migrations.
- Dapper / SQL is used for dashboard, reporting and performance-sensitive complex reads.
- Command and Query responsibilities are separated pragmatically without introducing distributed CQRS infrastructure.

---

# 4. Major Module Boundaries

    Setup & Licensing
    Identity & Access
    Catalog
    Sales
    Inventory
    Purchasing
    Thaka
    Customers
    Suppliers
    Expenses
    Reporting
    Settings
    Audit
    Printing
    Backup / Diagnostics

Ownership:

    Catalog
    → Product definition.

    Inventory
    → Stock authority, movements, serialized units and costing state.

    Sales
    → Counter invoices, payments and sale returns.

    Purchasing
    → Supplier purchases and purchase returns.

    Thaka
    → Project material, project payments and settlement.

    Customers / Suppliers
    → Master/contact data only.

    Expenses
    → Operating expenses.

    Reporting
    → Read-only analytics.

    Identity
    → Users, roles, permissions and sessions.

    System
    → Shop configuration, licensing, backup, jobs and health.

Cross-module business operations use explicit services and one transaction where consistency requires it.

---

# 5. First Setup / License

Startup setup flow:

    Import Signed License
      ↓
    Verify License
      ↓
    Shop Setup
      ↓
    Create Owner
      ↓
    Initialize Defaults
      ↓
    Verify Database / Schema
      ↓
    Setup Complete

Setup must never expose PostgreSQL host, port, password or connection string in the normal UI.

First setup is complete only after all mandatory initialization succeeds.

---

# 6. Identity and PIN Login

V1 uses fast PIN authentication.

User entity includes:

- User ID
- Display Name
- Role
- PIN Hash
- PIN algorithm metadata
- Status
- Failed attempts
- Lockout time
- Security version
- Audit timestamps

Never store plaintext PIN.

Login flow:

    Select User
      ↓
    Enter PIN
      ↓
    Verify Secure Hash
      ↓
    Validate ACTIVE Status
      ↓
    Resolve Role / Permissions
      ↓
    Create Session

User switching requires authentication of the newly selected user.

Users are DISABLED rather than hard-deleted once referenced.

At least one active Owner must always remain.

---

# 7. Roles and Permissions

V1 roles:

    OWNER
    MANAGER
    CASHIER

Owner has full access.

Manager has configurable operational access.

Cashier is sales-focused and has no profit/cost access by default.

Backend granular permission examples:

    sales.create
    sales.history.view
    sales.return
    profit.view

    thaka.create
    thaka.material.add
    thaka.payment.record
    thaka.settle
    thaka.reopen

    purchases.create
    purchases.return

    inventory.product.manage
    inventory.adjust

    expenses.manage
    reports.view

    users.manage
    settings.manage

    backup.execute
    backup.restore

Frontend can group permissions visually, but backend authorization remains granular.

No Manager PIN override in V1.

Unauthorized actions fail in Application authorization before business mutation.

Sensitive cost/profit fields are omitted from unauthorized DTOs, not merely hidden in WPF.

---

# 8. Sessions and Security Version

Authenticated session records:

- Session ID
- User ID
- Role
- Login time
- Machine ID
- Security version at login
- Logout/status

Permission or security changes invalidate stale authority through security versioning.

A disabled user must not continue write operations through an old active session.

---

# 9. Dashboard Contract

Dashboard remains read-only and chart-free.

Required metrics:

- Today Sales
- Today Profit
- Expenses
- Low Stock
- Active Thakas
- Current Thaka Value
- Today Thaka Material
- Recent Activity
- Low Stock list

Dashboard uses optimized read queries and must remain fast.

Deeper graphical analytics belong in Reports.

---

# 10. Catalog Architecture

Catalog owns what a product is.

Core concepts:

- Product
- Category
- Brand
- Unit
- Product Barcode
- Product Price History
- Descriptive Attributes

Product tracking modes:

    QUANTITY
    LENGTH
    SERIALIZED

Examples:

    Bulb / Switch / Breaker
    → QUANTITY

    Wire / Cable
    → LENGTH

    Mobile / Appliance
    → SERIALIZED

Flexible descriptive attributes may use PostgreSQL JSONB.

Critical identity such as serial number, IMEI, warranty per unit, sale linkage and purchase linkage must remain relational.

Product authoritative stock is never directly edited from Product management.

---

# 11. Units and Quantity Behavior

Units define quantity behavior.

Examples:

    Piece    → WHOLE
    Box      → WHOLE
    Roll     → DECIMAL where needed
    Meter    → DECIMAL
    Foot     → DECIMAL

This drives backend quantity validation.

---

# 12. Inventory Core Principle

Golden rule:

    Stock is a result.
    Movement is the truth.

Inventory structures:

    InventoryMovement
    StockBalance
    InventoryLot
    InventoryUnit
    StockConsumptionAllocation
    ProductCostState
    StockAdjustment

All stock mutation must pass through the Inventory domain.

No arbitrary UPDATE of current stock from Product service.

---

# 13. Stock Buckets

V1 stock buckets:

    SELLABLE
    DAMAGED
    DEFECTIVE
    SCRAP

Potential future buckets:

    RESERVED
    SUPPLIER_RETURN

POS availability is based on SELLABLE stock only.

---

# 14. Inventory Movement

Movement records preserve:

- Product
- Movement type
- Quantity delta
- Source/destination bucket
- Before quantity
- After quantity
- Cost snapshot
- Reference type
- Reference ID
- Reference line ID
- Performed by
- Time
- Reason
- Note
- Correlation ID

Movement types:

    OPENING_STOCK
    PURCHASE_IN
    PURCHASE_RETURN_OUT
    SALE_OUT
    SALE_RETURN_IN
    THAKA_OUT
    ADJUSTMENT_IN
    ADJUSTMENT_OUT
    DAMAGE
    RESTORE_TO_SELLABLE

Product Detail movement history is driven by this ledger.

---

# 15. Stock Balance

StockBalance is only a fast current-state snapshot.

Contains:

- Product ID
- Sellable Qty
- Damaged Qty
- Defective Qty
- Scrap Qty
- Last Movement ID
- Updated time
- Version

Movement and balance updates occur together inside the same PostgreSQL transaction.

---

# 16. Serialized Inventory

InventoryUnit contains:

- Product ID
- Purchase item link
- Serial Number
- IMEI 1
- IMEI 2
- Status
- Condition
- Acquisition Cost
- Warranty snapshot
- Warranty start/end
- Sold timestamp
- Concurrency metadata

Statuses:

    IN_STOCK
    SOLD
    DAMAGED
    DEFECTIVE
    SCRAPPED
    SUPPLIER_RETURNED

Serial and IMEI fields have partial unique constraints when present.

A serialized unit cannot be sold twice.

---

# 17. POS / Complete Sale

Cart can remain transient until completion because V1 has no HOLD.

Complete Sale command input includes:

- Customer
- Invoice Discount
- Payment Method
- Amount Received
- Items
- Serialized Unit IDs when applicable
- Print preference
- ClientOperationId

Frontend totals are previews.

Backend recalculates authoritative subtotal, discount, total, payment and change.

Transaction:

    Validate Session
      ↓
    Check Permission
      ↓
    Check ClientOperationId
      ↓
    Validate Customer
      ↓
    Load Products
      ↓
    Validate Authoritative Prices
      ↓
    Lock / Validate Stock
      ↓
    Validate Serialized Units
      ↓
    Calculate Subtotal / Discount / Total
      ↓
    Validate Payment
      ↓
    Generate Invoice Number
      ↓
    Create Sale
      ↓
    Create Sale Items
      ↓
    Create Payment
      ↓
    Create Inventory Movements
      ↓
    Update Stock
      ↓
    Update Costing
      ↓
    Update Serialized Units
      ↓
    Audit
      ↓
    COMMIT

Printing happens after COMMIT.

---

# 18. Sale Idempotency

High-risk commands use ClientOperationId.

If Complete Sale is triggered twice with the same operation ID:

    First Request
    → Creates Sale #1292

    Second Request
    → Returns existing Sale #1292

It must not create Sale #1293.

This protects against double-click, double F10, retry and crash-after-commit.

---

# 19. Sale Items and Historical Snapshots

SaleItem preserves:

- Product ID
- Product Name Snapshot
- SKU Snapshot
- Unit Snapshot
- Quantity
- Unit Price
- Line Total
- Unit Cost Snapshot
- Total Cost Snapshot
- Gross Profit Snapshot

Future product rename, price change or cost change must not alter historical invoice/profit.

---

# 20. Sale Payments

Payments use a child structure rather than only one payment field on Sale.

Stored concepts:

- Method
- Applied Amount
- Amount Tendered
- Change Given
- Reference
- User
- Time

For cash:

    Total = 8,500
    Tendered = 10,000
    Applied = 8,500
    Change = 1,500

Revenue remains 8,500, not 10,000.

V1 may enforce one payment in Application layer while schema remains future split-payment capable.

---

# 21. Sale Returns

Original completed sale remains unchanged.

Return is a separate document.

Eligibility:

    Eligible Return Qty
    =
    Sold Qty
    -
    Successful Previous Returns

Disposition:

    RESTOCK_SELLABLE
    DAMAGED
    DEFECTIVE
    SCRAP

Effects:

    RESTOCK_SELLABLE
    → Sellable stock increases.

    DAMAGED
    → Damaged bucket increases.

    DEFECTIVE
    → Defective bucket increases.

    SCRAP
    → Scrap bucket increases.

Serialized returns identify exact InventoryUnit.

Refund and inventory effect commit atomically.

---

# 22. Purchasing Architecture

Purchase is an inventory acquisition event.

It is not an operating expense.

Purchase header includes:

- Purchase number
- Supplier
- Supplier invoice number
- Purchase date
- Note
- Subtotal
- Other Charges
- Grand Total
- Status
- Creator
- ClientOperationId

PurchaseItem includes:

- Product snapshots
- Quantity
- Base Unit Cost
- Base Line Total
- Allocated Other Cost
- Effective Unit Cost
- Effective Line Cost
- Sale Price at Purchase

---

# 23. Purchase Transaction

    Validate Permission
      ↓
    Validate Supplier
      ↓
    Duplicate Supplier Invoice Check
      ↓
    Validate Products / Qty / Costs
      ↓
    Validate Serial / IMEI
      ↓
    Recalculate Subtotal
      ↓
    Allocate Other Charges
      ↓
    Calculate Landed Cost
      ↓
    Create Purchase
      ↓
    Create Purchase Items
      ↓
    Create Lots / Serialized Units
      ↓
    PURCHASE_IN Movements
      ↓
    Update Stock
      ↓
    Update Cost State
      ↓
    Optional Audited Sale Price Update
      ↓
    Audit
      ↓
    COMMIT

Supplier invoice duplicate protection is per supplier when supplier invoice number exists.

---

# 24. Landed Cost / Other Charges

Purchase Other Charges become part of acquisition cost.

Allocation is proportional by purchase-line base value.

    Line Share
    =
    Line Base Total / Purchase Subtotal

    Allocated Other Cost
    =
    Other Charges × Line Share

Rounding difference is absorbed deterministically so allocated total exactly equals Other Charges.

---

# 25. Costing Decision

Locked costing:

    Quantity / Length
    → Moving Weighted Average

    Serialized
    → Actual Individual Unit Cost

For quantity/length:

    New Average
    =
    Current Inventory Cost + New Acquisition Cost
    ------------------------------------------------
    Current Quantity + Received Quantity

Cost state stores:

- Costed Quantity
- Total Inventory Cost
- Moving Average Cost
- Last Purchase Cost
- Version

Sale captures cost snapshot at transaction time.

Historical profit never changes because of future purchase costs.

---

# 26. Inventory Lots and Consumption Allocation

Costing and purchase-origin traceability are separate.

InventoryLot tracks:

- Product
- Purchase Item
- Received Qty
- Remaining Qty
- Original Unit Cost
- Effective Unit Cost
- Received Date

StockConsumptionAllocation maps stock-out movements to originating lots.

This allows exact purchase return eligibility while still using Moving Weighted Average for COGS.

---

# 27. Purchase Returns

Original purchase remains unchanged.

Purchase Return is a separate document.

Eligibility must account for:

- Purchased Qty
- Already Sold / Used Qty
- Already Returned Qty
- Current Eligible Return Qty
- Requested Return Qty

Rule:

    Requested Return Qty <= Eligible Return Qty

Serialized return uses exact InventoryUnit.

Purchase return creates inventory OUT and preserves history.

---

# 28. Thaka Domain

Thaka is not a normal Sale.

ThakaProject contains:

- Project number
- Customer
- Project name
- Customer snapshots
- Start date
- Notes
- Status
- Creator
- Version

V1 statuses:

    ACTIVE
    SETTLED

Walk-in Customer cannot create Thaka.

---

# 29. Thaka Material Issue

Material issue stores:

- Project
- Product snapshots
- Quantity
- Customer Rate
- Line Total
- Cost Snapshot
- Note
- Issuer
- Date
- ClientOperationId

Flow:

    Lock Project
      ↓
    Require ACTIVE
      ↓
    Validate Permission
      ↓
    Validate Product
      ↓
    Validate SELLABLE Stock
      ↓
    Calculate Rate / Total
      ↓
    Capture Cost
      ↓
    Create Material Issue
      ↓
    THAKA_OUT Inventory Movement
      ↓
    Consume Inventory Lot
      ↓
    Audit
      ↓
    COMMIT

Serialized Thaka item requires exact InventoryUnit identity.

---

# 30. Thaka Revenue Recognition

Locked rule:

    Thaka material revenue is recognized when material is issued.

Reason:

- Stock leaves shop.
- Customer/project obligation increases.
- Project outstanding increases.

Thaka Payment is collection of receivable, not creation of revenue.

Thaka Material remains separate from Today Local Sales.

---

# 31. Thaka Financial Equations

Before settlement:

    Material Total
    =
    Sum of valid material issue totals

    Paid Total
    =
    Sum of valid payments

    Current Balance
    =
    Material Total - Paid Total

Settlement:

    Final Balance
    =
    Material Total
    - Previous Payments
    - Final Discount
    - Received Now

Project can become SETTLED only when:

    Final Balance = 0

Final discount reduces project revenue and profit.

Payments do not affect profit.

---

# 32. Thaka Settlement and Reopening

Settlement captures a snapshot:

- Material Total
- Previous Payments
- Final Discount
- Remaining Before Settlement
- Received Now
- Final Payment
- Net Project Revenue
- Final Balance
- Settled By
- Settled At

Settled project is financially immutable under normal operations.

Reopening creates a separate ThakaReopening record referencing previous settlement.

Old settlement is never deleted.

Future reserved correction workflows:

- Reverse Thaka Material
- Reverse Thaka Payment

Corrections must be explicit reversals, not destructive edits.

---

# 33. Customers

Customer master stores:

- Name
- Phone
- Normalized Phone
- Alternate Phone
- Address
- City
- Notes
- Walk-in marker
- Active status

One protected Walk-in Customer exists for POS.

Phone is indexed but not globally unique.

Computed by queries, not manually stored:

- Local Sales
- Last Sale
- Active Thaka Count
- Current Thaka Balance

Once referenced, customer is deactivated rather than hard-deleted.

---

# 34. Suppliers

Supplier master stores:

- Name
- Phone
- Normalized Phone
- Alternate Phone
- City
- Address
- Notes
- Active status

Computed via Purchasing queries:

- Total Purchases
- Last Purchase
- Products

No supplier Accounts Payable/payment ledger is introduced in V1 because frontend currently has no due/payment workflow.

---

# 35. Expenses

Expense contains:

- Expense Number
- Category
- Subcategory
- Amount
- Date
- Payment Method
- Optional Staff reference/snapshot
- Note
- Status
- Create/update audit metadata
- Version

Statuses:

    POSTED
    VOIDED

Remove Expense means VOID, not physical DELETE.

Staff Salary remains an Expense concept, not a payroll subsystem.

---

# 36. Financial Reporting Rules

Purchases are not directly subtracted from profit.

Correct flow:

    Purchase
    → Inventory Cost
    → COGS when product leaves through sale/Thaka

Local Sales:

    Gross Local Sales
    =
    Sum of Completed POS Sale GrandTotals
    Net Local Sales
    =
    Gross Local Sales
    -
    Successful Sale Refund Value

Local Gross Profit:

    Net Local Sales
    -
    Net Local COGS

Local Net Profit:

    Local Gross Profit
    -
    Operating Expenses

Invoice discount reduces revenue before profit calculation.

---

# 37. Return Impact on Profit

If returned item is RESTOCK_SELLABLE:

- Revenue is refunded.
- Original corresponding COGS is reversed.
- Sellable inventory asset returns.

If returned item is DAMAGED / DEFECTIVE / SCRAP:

- Revenue is refunded.
- Sellable stock is not restored.
- Cost treatment respects non-sellable disposition.

Return affects the period of the return event and does not rewrite the original sale date.

---

# 38. Reporting Architecture

    PostgreSQL
      ↓
    Dapper / Optimized SQL
      ↓
    Report DTO / Chart Series
      ↓
    WPF ViewModel
      ↓
    Cards + Tables + Graphs

Reports are read-only.

No analytics server or data warehouse is required for V1.

---

# 39. Reporting Graphics — New Locked Requirement

Reports officially include useful graphics.

Dashboard remains chart-free.

Daily Report:

- Net Local Sales
- Gross Profit
- Expenses
- Net Profit
- Purchases
- Thaka Material
- Average Invoice Value
- Average Gross Profit per Invoice
- Sales Count
- Average Items per Invoice

Daily primary graph:

    Hourly Sales
    +
    Hourly Gross Profit

Monthly averages:

- Average Daily Sales
- Average Daily Gross/Net Profit
- Average Daily Expenses
- Average Invoice Value

Monthly primary graph:

    Daily Sales
    Daily Profit
    Daily Expenses

Monthly also includes Expense Category Breakdown and separate Thaka Activity.

Yearly metrics:

- Total Net Sales
- Net Profit
- Expenses
- Thaka Material
- Average Monthly Sales
- Average Monthly Net Profit
- Average Monthly Expenses

Yearly primary graph:

    Jan → Dec
    Sales
    Net Profit
    Expenses

Graphs are analytical, not decorative.

---

# 40. Average Metric Rules

Average Invoice Value:

    Completed Net Sales
    /
    Completed Invoice Count

Average Gross Profit per Invoice:

    Gross Profit
    /
    Completed Invoice Count

Average Daily Sales:

    Net Sales in selected period
    /
    Calendar Days in selected period

Average Daily Expenses:

    Expenses in selected period
    /
    Calendar Days in selected period

Zero-sale days are included.

Missing periods are zero-filled in chart series.

Current-year monthly averages divide by elapsed/selected months, not blindly by 12.

All report grouping uses Shop Time Zone.

---

# 41. Audit Architecture

Business Audit is separate from Serilog.

Audit answers:

- Who completed a sale?
- Who returned it?
- Who changed inventory?
- Who changed product price?
- Who settled/reopened Thaka?
- Who changed permissions?
- Who restored backup?

Audit event stores:

- Occurred At
- User ID
- User Name Snapshot
- Role Snapshot
- Session ID
- Action Code
- Entity Type
- Entity ID
- Entity Reference
- Before Data
- After Data
- Reason
- Machine ID
- Correlation ID

Audit is append-only for normal application behavior.

Important audit is inserted inside the same transaction as the successful business change.

Secrets never enter audit.

---

# 42. Settings Architecture

Settings screen orchestrates:

    Shop
    Receipt
    Users & Access
    Categories & Units
    Backup
    License
    Appearance
    Database Diagnostics

It does not own all data itself.

Shop profile includes:

- Shop Name
- Owner display name
- Phone
- Address
- Logo reference
- Time Zone
- Currency

Receipt configuration includes printer/paper/header/footer defaults.

Categories and Units are deactivated rather than deleted when referenced.

Database settings is diagnostics only.

No editable PostgreSQL credentials in UI.

---

# 43. Worker Service

EdgeRetails.Worker.exe handles:

- Scheduled DB backup
- Cloud backup upload
- Backup verification
- Retention cleanup
- Maintenance jobs
- Update checks
- Future sync jobs

It does NOT handle:

- Sale completion
- Purchase posting
- Thaka settlement
- Inventory adjustment

Desktop and Worker may coordinate through PostgreSQL job/status tables.

Worker heartbeat is stored for health display.

---

# 44. Backup Architecture

    PostgreSQL
      ↓
    Consistent Backup
      ↓
    Backup Manifest
      ↓
    Basic Verification
      ↓
    Compression
      ↓
    Encryption
      ↓
    Protected Local Copy
      ↓
    Cloud Upload
      ↓
    Remote Verification
      ↓
    Retention

Cloud is backup, not primary database and not sync.

Internet/cloud failure must never stop local POS.

Backup success requires verification, not merely existence of a file.

---

# 45. Backup Metadata

Backup history records include:

- Backup ID
- Start/completion time
- App version
- Schema version
- Type
- Status
- Local artifact reference
- Size
- Checksum
- Encryption version
- Cloud status
- Cloud object key
- Verification status
- Failure details

Latest known-good backup must not be deleted before a replacement is verified.

---

# 46. Restore Architecture

Restore is Owner-only and high risk.

    Request Restore
      ↓
    Permission Check
      ↓
    Destructive Confirmation
      ↓
    Validate Backup / Checksum / Encryption
      ↓
    Validate Version Compatibility
      ↓
    Pre-Restore Safety Backup
      ↓
    Maintenance Mode
      ↓
    Block Business Writes
      ↓
    Restore PostgreSQL
      ↓
    Schema / Integrity / Health Checks
      ↓
    Exit Maintenance Mode

pg_restore success alone is not enough.

System health verification is required.

---

# 47. Licensing

V1 uses local signed licensing.

License may contain:

- LicenseId
- CustomerName
- StoreName
- Plan
- IssueDate
- ExpiryDate
- DeviceId
- MaxTerminals
- EnabledModules
- LicenseVersion
- Signature

Security:

    PRIVATE KEY
    → License generation environment only

    PUBLIC KEY
    → Embedded in Edge Retails
    → Local verification

Private signing key is never shipped.

Database license state is cached metadata, not cryptographic authority.

Electronics module is enabled only if signed license allows it.

---

# 48. Printing

Printing is Infrastructure.

Business order:

    Complete Sale
      ↓
    PostgreSQL COMMIT
      ↓
    Generate Receipt
      ↓
    Print

Printer failure does not roll back sale.

UI behavior:

    Sale completed successfully.
    Receipt failed to print.
    Retry Print.

Retry/Reprint loads existing committed sale and never repeats Complete Sale.

Historical receipt uses snapshots so future product changes do not alter old receipt content.

---

# 49. Crash Recovery

PostgreSQL ACID transactions are the primary crash protection.

Crash before COMMIT:

    Transaction rolls back.

Crash after COMMIT but before UI success:

    ClientOperationId detects already-completed operation.

Startup sequence:

    Configuration
      ↓
    License Validation
      ↓
    PostgreSQL Health
      ↓
    Schema Compatibility
      ↓
    Shop Initialization
      ↓
    Worker Health
      ↓
    Login

Dangerous migration and restore use Maintenance Mode.

Pre-migration backup is required before schema-changing update.

---

# 50. Technical Logging

Serilog handles:

- Startup / shutdown
- App errors
- DB errors
- Worker errors
- Printer failures
- Backup failures
- Hardware integration issues

Use rolling files, retention and size caps.

Never log:

- Raw PIN
- PIN Hash
- DB Password
- Connection secrets
- Backup encryption key
- License private key
- Secret tokens

Business Audit remains separate.



---

# 51. PostgreSQL Database Architecture

Database:

    edge_retails

Schemas:

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

This provides explicit module data ownership.

---

# 52. Database Type Strategy

Recommended:

    Internal IDs
    → uuid, UUIDv7 application-generated

    Human Document Numbers
    → varchar

    Final Money
    → numeric(18,2)

    Unit / Average Cost
    → numeric(18,6)

    Quantity
    → numeric(18,3) or appropriate higher precision

    Percent / Rate
    → numeric(9,4)

    Time
    → timestamptz

    Business Date
    → date

    Flexible Product Attributes
    → jsonb

    Concurrency
    → bigint version

Human document number is never the database primary key.

---

# 53. system Schema

Tables:

    system.shops
    system.receipt_settings
    system.schema_info
    system.system_state
    system.license_state
    system.worker_heartbeats
    system.background_jobs
    system.backup_runs
    system.app_settings

Only lightweight generic configuration belongs in app_settings.

---

# 54. identity Schema

Tables:

    identity.users
    identity.roles
    identity.permissions
    identity.role_permissions
    identity.user_permission_overrides
    identity.user_sessions

User includes PIN/security metadata, status, audit metadata and version.

---

# 55. parties Schema

Tables:

    parties.customers
    parties.suppliers

Customer has a protected Walk-in marker.

Customer/Supplier aggregate totals are queries, not manually maintained authoritative columns.

---

# 56. catalog Schema

Tables:

    catalog.categories
    catalog.brands
    catalog.units
    catalog.products
    catalog.product_barcodes
    catalog.product_price_history

Product contains:

- SKU
- Name
- Category
- Brand
- Unit
- Tracking Mode
- Default Sale Price
- Reference Purchase Cost
- Minimum Stock
- JSONB attributes
- Warranty defaults
- Active status
- Version

Barcode is normalized into its own table for exact lookup and future alternate barcodes.

---

# 57. inventory Schema

Tables:

    inventory.stock_balances
    inventory.movements
    inventory.lots
    inventory.units
    inventory.consumption_allocations
    inventory.cost_states
    inventory.stock_adjustments
    inventory.stock_adjustment_items

This schema is the stock authority.

---

# 58. sales Schema

Tables:

    sales.sales
    sales.sale_items
    sales.sale_payments
    sales.sale_item_units
    sales.returns
    sales.return_items
    sales.return_item_units

Completed transactions are immutable.

Corrections use return/reversal patterns.

---

# 59. purchasing Schema

Tables:

    purchasing.purchases
    purchasing.purchase_items
    purchasing.returns
    purchasing.return_items

Duplicate supplier invoice protection is scoped per supplier where invoice number exists.

---

# 60. thaka Schema

Tables:

    thaka.projects
    thaka.material_issues
    thaka.material_issue_items
    thaka.material_issue_units
    thaka.payments
    thaka.settlements
    thaka.reopenings

Reserved future correction tables:

    thaka.material_reversals
    thaka.payment_reversals

---

# 61. finance Schema

Tables:

    finance.expense_categories
    finance.expense_subcategories
    finance.expenses

Expense is POSTED or VOIDED.

No destructive financial deletion.

---

# 62. audit Schema

Table:

    audit.events

Append-only business forensic trail.

---

# 63. reporting Schema

V1 does not duplicate all transactions into analytics tables.

Use:

    Dapper SQL
    +
    selected PostgreSQL views

Possible reusable views:

    reporting.v_sales_daily
    reporting.v_profit_daily
    reporting.v_expenses_daily
    reporting.v_thaka_daily
    reporting.v_inventory_status
    reporting.v_customer_summary
    reporting.v_supplier_summary

Chart points are generated by report queries, not stored as authoritative graph rows.

---

# 64. Document Number Strategy

Examples:

    Sale               INV-001292
    Sale Return        SR-000048
    Purchase           P-000252
    Purchase Return    PR-000027
    Thaka              THK-000142
    Material Issue     TMI-000688
    Thaka Payment      TP-000311
    Settlement         TS-000074
    Stock Adjustment   SA-000055
    Expense            EXP-000231

Human number and UUID PK remain separate.

Concurrency-safe sequence/counter generation is required.

Small gaps after rollback are acceptable.

---

# 65. Immutability Rules

Mutable master data:

    Product
    Customer
    Supplier
    Category
    Brand
    Unit
    User
    Settings

May be edited/deactivated.

Transactional records:

    Completed Sale
    Sale Item
    Sale Return
    Completed Purchase
    Purchase Item
    Purchase Return
    Thaka Material Issue
    Thaka Payment
    Settlement
    Inventory Movement
    Audit Event

must not be destructively rewritten in normal flow.

Corrections use:

    return
    void
    reversal
    adjustment

---

# 66. Concurrency

Mutable consistency-sensitive rows use version bigint.

Optimistic update pattern:

    WHERE id = requested_id
    AND version = expected_version

then increment version.

Critical operations such as stock mutation and Thaka financial commands may additionally use row locks inside transaction.

Architecture is therefore prepared for future multi-terminal concurrency.

---

# 67. Key Unique Constraints

Examples:

    catalog.products.sku
    → UNIQUE

    catalog.product_barcodes.barcode
    → UNIQUE

    sales.sales.invoice_number
    → UNIQUE

    sales.sales.client_operation_id
    → UNIQUE

    purchasing.purchases.purchase_number
    → UNIQUE

    thaka.projects.project_number
    → UNIQUE

    inventory.units.serial_number
    → PARTIAL UNIQUE

    inventory.units.imei1
    → PARTIAL UNIQUE

    inventory.units.imei2
    → PARTIAL UNIQUE

High-risk command tables use unique ClientOperationId.

---

# 68. Database Check Constraints

Examples:

    quantity > 0
    amount >= 0
    unit_price >= 0
    cost >= 0
    discount >= 0
    minimum_stock >= 0
    change_given >= 0

PostgreSQL blocks simple impossible states.

Complex business rules remain in Domain/Application.

---

# 69. Important Index Areas

Catalog / POS:

- SKU
- Barcode
- Product Name
- Category
- Brand

Sales:

- Completed At
- Customer
- Cashier
- Status
- Invoice Number

Inventory:

- Product + Time
- Reference Type + Reference ID
- Serial
- IMEI

Purchasing:

- Purchase Date
- Supplier
- Purchase Number
- Supplier Invoice

Thaka:

- Customer
- Status
- Start Date
- Project Number

Expenses:

- Expense Date
- Category
- Status

Future fuzzy local search may use pg_trgm.

No external search engine is needed for V1.

---

# 70. Atomic Transaction Map — Complete Sale

One transaction touches:

    sales.sales
    sales.sale_items
    sales.sale_payments
    sales.sale_item_units

    inventory.movements
    inventory.stock_balances
    inventory.consumption_allocations
    inventory.cost_states
    inventory.units

    audit.events

Then COMMIT.

Printing follows afterward.

---

# 71. Atomic Transaction Map — Purchase

One transaction touches:

    purchasing.purchases
    purchasing.purchase_items

    inventory.lots
    inventory.units
    inventory.movements
    inventory.stock_balances
    inventory.cost_states

    catalog.product_price_history when required

    audit.events

Then COMMIT.

---

# 72. Atomic Transaction Map — Thaka Material

One transaction touches:

    thaka.material_issues
    thaka.material_issue_items
    thaka.material_issue_units

    inventory.movements
    inventory.stock_balances
    inventory.consumption_allocations
    inventory.cost_states

    audit.events

Then COMMIT.

---

# 73. Atomic Transaction Map — Sale Return

One transaction touches:

    sales.returns
    sales.return_items
    sales.return_item_units

    inventory.movements
    inventory.stock_balances
    inventory.cost_states

    audit.events

Return disposition decides which inventory bucket receives returned stock.

---

# 74. Major Locked Decisions

1. Backend design is driven by the frontend contract.
2. V1 uses Modular Monolith.
3. PostgreSQL is the local primary database.
4. EF Core handles transactional operations.
5. Dapper handles heavy/reporting reads.
6. Product and Inventory are separate concepts.
7. Inventory Movement is stock audit truth.
8. StockBalance is fast current state.
9. Current Stock is not directly editable.
10. Quantity, Length and Serialized tracking are distinct.
11. Serial/IMEI identity remains relational.
12. Complete Sale is atomic.
13. ClientOperationId protects against duplicates.
14. Printing happens after commit.
15. Original sale is never rewritten by a return.
16. Return disposition controls sellable/non-sellable stock.
17. Purchase is inventory acquisition, not operating expense.
18. Purchase Other Charges become landed cost.
19. Quantity/length costing uses Moving Weighted Average.
20. Serialized costing uses actual unit cost.
21. Lots preserve purchase-origin traceability.
22. Purchase return eligibility is backend calculated.
23. Thaka is separate from POS.
24. Thaka revenue is recognized when material is issued.
25. Thaka payment is collection, not revenue.
26. Thaka settles only when final balance equals zero.
27. Final discount reduces Thaka revenue/profit.
28. Reopened Thaka preserves previous settlement history.
29. Customers and Suppliers own master data only.
30. Expenses are voided, not hard deleted.
31. Purchases are not directly subtracted from Net Profit.
32. Reports use exact Sales / COGS / Profit definitions.
33. Reports include useful graphs and averages.
34. Dashboard remains chart-free.
35. Cashier does not receive profit/cost data by default.
36. Authorization is enforced in Application layer.
37. Business Audit and technical logging are separate.
38. Worker owns backup/maintenance, not core business writes.
39. Cloud outage never blocks local POS.
40. Restore uses maintenance mode and safety backup.
41. Licensing is local signed license with public-key validation.
42. Private license key is never shipped.
43. Database UI is diagnostics-only.
44. Transactions are corrected through return/void/reversal.
45. UUIDv7 is recommended for internal IDs.
46. Human document numbers remain separate.
47. PostgreSQL schemas enforce module ownership.
48. Future LAN/multi-terminal expansion must not require rewriting domain logic.

---

# 75. Known Reserved / Follow-Up Work

Reserved for later phases:

- Exact Thaka Material Reversal workflow
- Exact Thaka Payment Reversal workflow
- Final command-level permission matrix
- Final receipt/printer implementation library
- Final cloud backup provider
- Backup retention defaults
- Signed application update distribution
- Optional supplier Accounts Payable if frontend later adds due/payment flow
- Optional Inventory Valuation UI now that costing is decided
- Final WPF chart library
- Future ASP.NET Core multi-terminal boundary

These are known extension points, not forgotten gaps.

---

# 76. Next Planned Phase

## Phase 9 — Application / Domain Code Architecture

Next architecture work must convert the locked business/database model into implementable C# structure:

    Commands
    Queries
    Command Handlers
    Query Handlers
    Application Services
    Domain Aggregates
    Entities
    Value Objects
    Domain Services
    Repository Interfaces
    EF Implementations
    Dapper Query Services
    Authorization Pipeline
    Validation Pipeline
    Transaction / Unit-of-Work Boundary
    Result / Error Model
    Domain Events
    Dependency Injection
    Project / Folder Structure
    Unit / Integration Test Boundaries

Phase 9 must use this document as baseline and must not silently redefine locked decisions.

---

# 77. Architecture Progress

    Phase 1
    Frontend → Backend Requirement Contract
    COMPLETE

    Phase 2
    Sales + Inventory Core
    COMPLETE

    Phase 3
    Purchasing + Costing + Supplier
    COMPLETE

    Phase 4
    Thaka + Customer + Payments + Settlement
    COMPLETE

    Phase 5
    Expenses + Reporting + Dashboard + Graphs
    COMPLETE

    Phase 6
    Identity + Permissions + Audit + Settings
    COMPLETE

    Phase 7
    Worker + Backup + License + Crash Safety
    COMPLETE

    Phase 8
    PostgreSQL Database Architecture
    COMPLETE

    Phase 9
    Application / Domain Code Architecture
    NEXT

---

# 78. Baseline Status

This document is the authoritative backend architecture baseline before Phase 9.

When architecture discussion resumes:

1. Read this document first.
2. Preserve all locked Phase 1–8 decisions.
3. Continue from Phase 9.
4. Record later approved architectural changes explicitly rather than silently replacing earlier rules.



---

# FINAL SUPERSESSION NOTICE — 2026-09-19

Architecture phases continued beyond the original Phase 8 baseline through Phase 16.

The authoritative implementation baseline is now:

```text
docs/Edge_Retails_Final_Architecture_Report_v1.md
```

That final report resolves the Phase 16 gap audit, including serialized inventory lifecycle, invoice-discount allocation, inventory-loss treatment, Thaka reversals, Purchase Void, receipt snapshotting, portable backup recovery, Owner PIN recovery, final KPI/report formulas, system/license guards, and future LAN gateway consistency.

Where this original master file contains an earlier unresolved assumption or a rule that conflicts with the final report, **the Final Architecture Report takes precedence**.

This original file remains preserved as architecture history and phase record.
