# Edge Retails Backend Implementation Roadmap
## Antigravity Execution Plan — 6 Macro Phases Only

**Project:** Edge Retails  
**Workstream:** Backend Implementation Alignment  
**Execution Environment:** Antigravity  
**Architecture Authority:** `docs/Edge_Retails_Final_Architecture_Report_v1.md`  
**Authority Manifest:** `docs/Architecture_Authority_Manifest.json`  
**Current Canonical SHA-256:** `12344760C60124DDC2D1C0E54ABFB7BC0E82B9B23A46E6463303EBFA530AB673`  
**Roadmap Model:** Six long-run macro phases  
**Status:** Ready for Antigravity implementation  

---

# 0. Purpose of This Roadmap

This roadmap converts the frozen Edge Retails backend architecture into a production-ready implementation without fragmenting the work into dozens of artificial phases.

The project must be executed through exactly six macro phases:

```text
Phase 1 — Canonical Schema & Domain Alignment
Phase 2 — Core Business Transaction Engine
Phase 3 — Production Safety & External Effects
Phase 4 — Multi-Terminal & Operational Runtime
Phase 5 — Scale, Performance & Observability
Phase 6 — Final Certification & Long-Term Maintenance
```

Subtasks, commits, checklists, test batches, or implementation groups may exist inside a phase, but they must not be promoted into new roadmap phases.

The goal is long-run completion, not repeated planning loops.

---

# 1. Non-Negotiable Execution Rules

## 1.1 Architecture Is the Target

The current canonical backend architecture is the implementation authority.

Default rule:

```text
Architecture vs Code mismatch
→ Architecture wins
→ Code/schema/tests are aligned to architecture
```

Architecture may be changed only if implementation evidence proves a genuine contradiction or impossible requirement.

Required process for an architecture change:

```text
Evidence
→ Architecture defect identified
→ RFC / forensic review
→ Canonical architecture updated
→ Manifest SHA updated
→ Regression rules updated
→ Implementation continues
```

Do not silently change architecture to make existing code easier to keep.

---

## 1.2 No Micro-Phase Explosion

Antigravity must not create:

```text
Phase 1.1
Phase 1.2
Phase 1.3
Phase 1.4
...
Phase 37
```

Use internal task groups instead:

```text
Phase 1
  ├── Domain models
  ├── EF configuration
  ├── repository contracts
  ├── migration
  └── tests
```

All work still belongs to Phase 1 until the Phase 1 exit gate passes.

---

## 1.3 Evidence-Based Completion

A phase is not complete because:

- code exists;
- a file was created;
- a class compiles;
- one test passed;
- documentation says complete.

A phase closes only after its defined exit gate is proven.

Evidence types:

```text
Build evidence
Unit-test evidence
Integration-test evidence
EF/schema evidence
PostgreSQL evidence
Runtime evidence
Concurrency evidence
Performance evidence
Architecture-drift evidence
Release evidence
```

---

## 1.4 Do Not Fake Runtime PASS

Use these meanings consistently:

```text
IMPLEMENTED
= code/schema exists

STRUCTURALLY VERIFIED
= static/build/schema checks passed

INTEGRATION VERIFIED
= real test environment behavior passed

RUNTIME VERIFIED
= actual runtime scenario passed

PRODUCTION CERTIFIED
= final release/certification gates passed
```

Never promote an implementation to `RUNTIME VERIFIED` from architecture text or static code inspection.

---

## 1.5 Preserve Historical Business Identity

Never rewrite or recycle committed business identity.

Examples:

```text
TrackingCode
DealerCode
SaleNumber
PurchaseNumber
SupplierPaymentNumber
WarrantyClaimNumber
InventoryUnit history
ClientOperationId
posted movement identity
```

Voids, reversals, returns, credits, and replacements create new historical records. They do not erase the original event.

---

## 1.6 Database Safety

Never run destructive migration, restore, crash, truncate, or stress tests against a production shop database.

Use:

```text
disposable PostgreSQL database
isolated integration database
staging restore database
test backup
test printer / simulated printer where appropriate
```

Production-like destructive testing must always target an explicitly disposable environment.

---

## 1.7 Git / Change Discipline

Recommended working cycle:

```text
Read architecture
→ inspect current implementation
→ implement coherent business slice
→ compile
→ test
→ inspect diff
→ run architecture verifier
→ checkpoint
```

Do not produce giant unrelated patches.

Do not modify architecture history/archive files unless the task explicitly requires historical metadata.

---

# 2. Global Definition of Done

The backend is not considered complete until Phase 6 proves all of the following:

```text
Release build                           PASS
Compiler warnings                       0
Unit tests                              PASS
Architecture tests                      PASS
EF model drift                          ZERO
Migration rehearsal                     PASS
PostgreSQL integration tests            PASS
Supplier Khata reconciliation           PASS
Tracking identity tests                 PASS
Warranty lifecycle tests                PASS
Printing/outbox tests                    PASS
Backup/restore rehearsal                PASS
LAN/reconnect tests                     PASS
Concurrency/stress tests                PASS
Performance benchmark                   PASS
Installer/package validation            PASS
Clean installation                      PASS
Upgrade path                            PASS
Architecture authority verifier         PASS
Canonical SHA/manifest                  MATCH
```

---

# 3. Phase 1 — Canonical Schema & Domain Alignment

## 3.1 Objective

Make the physical Domain model, EF Core model, PostgreSQL schema, repository contracts, permissions, and migration structure match the frozen architecture.

This phase establishes the database/domain foundation. Business workflows must not be considered reliable until Phase 1 is complete.

---

## 3.2 Primary Scope

### Catalog & Product

Align:

```text
Product
ProductUnit
SKU
Barcode
TrackingMode
Serial/IMEI policy
DefaultWarrantyMonths
AttributesJson + AttributesSchemaVersion
```

Rules to enforce:

- SKU uniqueness and normalization;
- historical SKU/barcode identity safety;
- product deactivation does not destroy historical references;
- unit conversion stays Base Quantity centered;
- exact-unit conversion validates whole quantity before rounding;
- warranty defaults belong to Product policy;
- AttributesJson does not become a hidden relational schema.

---

### Supplier & Traceability

Implement/verify:

```text
Supplier.DealerCode
SupplierCodeSequence
SupplierProduct
SupplierProduct.NextItemSequence
TrackingCode
InventoryUnit.SupplierProductId
InventoryUnit.ItemSequence
InventoryUnit.SupplierCodeSnapshot
InventoryUnit.ProductSkuSnapshot
InventoryUnit.OriginType
```

Required invariants:

```text
Supplier + Product = one SupplierProduct authority
ItemSequence is positive BIGINT
minimum display width = 6 digits
no artificial 999999 ceiling
TrackingCode is unique
sequence is never rolled back after void/replacement
DealerCode is permanent
```

Tracked identity:

```text
DealerCode + SKU + ItemSequence
→ TrackingCode
```

---

### Inventory

Align:

```text
InventoryMovement
InventoryMovementEffect
InventoryUnit
InventoryLot
LotBucketBalance
StockBalance
ProductCostState
InventoryUnit origin/status mapping
```

Every exact-unit status must define:

```text
Stock contribution
Bucket contribution
Cost contribution
Business owner
Sellability
Returnability
Terminal/non-terminal state
```

Opening Stock must support exact-unit provenance without creating a fake Purchase.

---

### Supplier Khata

Implement/verify:

```text
SupplierAccountEntry
SupplierPayment
SupplierPaymentReversal
SupplierRefund
SupplierRefundReversal
OpeningBalance
```

Canonical direction matrix:

```text
PURCHASE                  → INCREASE_PAYABLE
SUPPLIER_PAYMENT          → DECREASE_PAYABLE
PAYMENT_REVERSAL          → INCREASE_PAYABLE
PURCHASE_RETURN_CREDIT    → DECREASE_PAYABLE
PURCHASE_VOID_REVERSAL    → DECREASE_PAYABLE
SUPPLIER_REFUND_RECEIVED  → INCREASE_PAYABLE
SUPPLIER_REFUND_REVERSAL  → DECREASE_PAYABLE
WARRANTY_CREDIT           → DECREASE_PAYABLE
ADJUSTMENT_INCREASE       → INCREASE_PAYABLE
ADJUSTMENT_DECREASE       → DECREASE_PAYABLE
```

Supplier balance must be ledger-derived.

No mutable `Supplier.Balance` authority.

---

### POS Draft

Implement/verify:

```text
PosDraft
PosDraftItem
Draft status
Version/concurrency
TerminalId
Customer reference
Selected InventoryUnit
```

Draft rules:

```text
Draft ≠ Sale
Draft ≠ stock reservation
Draft ≠ cash transaction
Draft ≠ Supplier/Customer financial posting
```

Conversion to Sale must reuse the canonical Sale engine.

---

### Warranty Schema

Align:

```text
WarrantyClaim
WarrantyClaimItem
WarrantyClaimItemUnit
WarrantyClaimEvent
ShopStockWarrantyCase
active exact-unit reservation
replacement InventoryUnit linkage
Supplier provenance
financial resolution snapshots
```

Allowed V1 custody:

```text
WITH_CUSTOMER
WITH_SHOP
WITH_SUPPLIER
```

No `WITH_SERVICE_CENTER`.

---

### Permissions

Add/verify granular permission contracts for:

```text
POS
POS drafts
price checks/override
Supplier Khata
Supplier payments/refunds
Warranty claim lifecycle
shop-owned warranty
reports
settings
```

First-setup role seeding must include the new permission set.

---

## 3.3 Migration Strategy

Do not generate the migration until the model is internally aligned and compiles cleanly.

Then:

```text
Inspect existing migration history
↓
confirm current schema epoch policy
↓
generate canonical alignment migration
↓
inspect every operation manually
↓
run disposable PostgreSQL rehearsal
```

Verify migration includes all required:

```text
supplier_products
supplier_code_sequences
dealer_code
TrackingCode/indexes
Supplier Khata tables
POS Draft tables
Warranty uniqueness/barriers
warranty financial snapshots
default warranty fields
print/outbox entities if already introduced
```

Never rewrite a migration that has already crossed the production baseline freeze.

---

## 3.4 Phase 1 Required Tests

Minimum:

```text
TrackingCode sequence > 999999
DealerCode immutability
SupplierProduct uniqueness
TrackingCode uniqueness
Serial/IMEI normalized uniqueness
exact-unit pre-round validation
Supplier Khata direction matrix
Warranty custody enum
active Warranty exact-unit uniqueness
POS Draft has no stock/cash side effects
EF pending-model-change check
fresh schema creation
```

---

## 3.5 Phase 1 Exit Gate

Phase 1 closes only when:

```text
Release build                 PASS
Warnings                      0
Unit tests                    PASS
EF model drift                ZERO
Migration inspection          PASS
Disposable migration          PASS
Canonical schema verifier     PASS
No stale architecture enums   PASS
```

### Phase 1 Output

A stable database/domain foundation ready for business transactions.

---

# 4. Phase 2 — Core Business Transaction Engine

## 4.1 Objective

Make every stock, financial, warranty, and payment operation an authoritative atomic business transaction.

The system should become functionally correct under normal business usage during this phase.

---

## 4.2 Canonical Command Pipeline

All critical mutation commands must follow:

```text
Validate command
↓
Authorize
↓
Acquire ClientOperationId
↓
Acquire deterministic resource locks
↓
Reload authoritative state
↓
Revalidate
↓
Apply domain changes
↓
Persist stock/finance/audit/outbox intent
↓
COMMIT
↓
post-commit external dispatch
```

---

## 4.3 Purchase Flow

Canonical flow:

```text
CreatePurchase
→ validate Supplier
→ validate Product/Unit
→ exact Base Quantity conversion
→ SupplierProduct lock
→ contiguous ItemSequence allocation
→ TrackingCode creation
→ inventory movement
→ lot/cost update
→ PURCHASE Khata entry
→ optional SupplierPayment
→ Cash Movement if applicable
→ audit
→ durable print request
→ COMMIT
```

Must support:

```text
full payment
partial payment
unpaid purchase
external payment
cash-drawer payment
```

A Purchase always creates liability first.

Payment is a separate financial event, even if created in the same command.

---

## 4.4 Purchase Return

Flow:

```text
Original Purchase
→ calculate eligible remaining return
→ exact unit or quantity validation
→ remove/transfer inventory
→ PURCHASE_RETURN_CREDIT
→ audit
→ COMMIT
```

Actual money received from Supplier is not part of the Return credit itself.

Actual refund:

```text
SupplierRefund
→ SUPPLIER_REFUND_RECEIVED
→ cash/bank movement
```

---

## 4.5 Purchase Void

Allowed only when downstream consumption/business constraints permit.

Flow:

```text
Purchase
→ reverse inventory
→ preserve exact identity history
→ PURCHASE_VOID_REVERSAL
→ do not roll back SupplierProduct sequence
→ audit
```

Any prior SupplierPayment remains its own financial record and must be handled through proper reversal/refund logic.

---

## 4.6 Supplier Payment / Advance / Refund

Implement:

```text
Supplier settlement payment
Supplier advance
Payment reversal
Supplier refund received
Refund reversal
Supplier-account adjustment
Opening balance
```

Rules:

- normal settlement cannot exceed payable;
- negative balance only through explicit advance/credit semantics;
- refund received cannot exceed available Supplier credit;
- cash methods require open CashSession;
- every operation is idempotent;
- every operation is auditable.

---

## 4.7 Sale / POS

Canonical flow:

```text
POS or PosDraft
→ revalidate Product
→ revalidate price
→ revalidate unit conversion
→ lock stock/exact units
→ revalidate current stock
→ Sale
→ SaleItems
→ exact unit allocation / lot consumption
→ COGS
→ inventory movements
→ payment
→ cash
→ warranty expiry snapshot
→ durable print request
→ audit
→ COMMIT
```

No UI-calculated stock or total becomes authority without backend revalidation.

---

## 4.8 Sale Return

Must enforce:

```text
original Sale
original SaleItem
remaining refundable quantity
exact physical unit for serialized goods
original economic snapshots
return disposition
cash/refund result
movement/cost result
```

Return economics come from the original Sale snapshot.

---

## 4.9 Commercial Exchange

Treat as one atomic composite business operation:

```text
Return side
+
Replacement Sale side
+
difference settlement
=
one transaction
```

Do not build a second independent exchange accounting engine.

Reuse Sale + Return logic.

---

## 4.10 Customer Warranty

Eligibility must validate:

```text
Original Sale
Original SaleItem
Customer
Warranty expiry
returned quantity/unit
active existing claim
terminally resolved quantity/unit
Supplier provenance
```

Customer Warranty exact-unit path:

```text
claim
→ UNDER_REVIEW
→ SENT_TO_SUPPLIER
→ SUPPLIER_PROCESSING
→ READY_FOR_CUSTOMER
→ CLOSED
```

Replacement:

```text
new SupplierProduct sequence
→ new TrackingCode
→ WARRANTY_CUSTOMER_HELD
→ customer handover
→ WARRANTY_CUSTOMER_HANDED_OVER
```

These customer-owned states do not add shop StockBalance or ProductCostState.

---

## 4.11 Shop-Owned Supplier Warranty

Flow:

```text
DAMAGED / DEFECTIVE
→ WITH_SUPPLIER
→ carrying cost retained
```

Possible outcomes:

```text
REPAIRED
REPLACED
REJECTED
SCRAPPED
CREDITED
```

Replacement:

```text
old unit historical/terminal path
→ new SupplierProduct sequence
→ new TrackingCode
→ carrying cost continuity
→ SELLABLE/appropriate state
```

Credit:

```text
WITH_SUPPLIER
→ remove recoverable inventory
→ remove carrying cost
→ WARRANTY_CREDIT / DECREASE_PAYABLE
→ RecoveryDifference
```

Where:

```text
RecoveryDifference
= SupplierCreditAmount
- InventoryCarryingCostResolved
```

Then classify gain/loss according to architecture.

---

## 4.12 Stock Adjustment

Quantity/length:

```text
positive/negative quantity
→ explicit reason
→ movement
→ cost treatment
```

Serialized:

```text
positive
→ one exact identity per unit
→ provenance required
→ TrackingCode authority required

negative
→ exact InventoryUnit selection required
→ explicit destination/terminal state
```

No anonymous serialized quantity adjustment.

---

## 4.13 Stocktake

Flow:

```text
create scope
→ lock scope for conflicting writes
→ count
→ exact unit verification
→ review variance
→ approve
→ movement-backed adjustment
```

Unknown serialized item:

```text
FOUND_UNREGISTERED
→ controlled investigation/intake
→ origin/cost/identity verification
→ authorized positive adjustment
```

Never silently create an exact unit during count entry.

---

## 4.14 Thaka / Projects

Verify:

```text
issue
payment
return
settlement
profit impact
customer/project balance
cash
inventory
```

No direct balance mutation outside authoritative transactions.

---

## 4.15 Phase 2 Required Functional Tests

At minimum:

```text
quantity Purchase → Sale
serialized Purchase → TrackingCode → Sale
partial Supplier payment
Supplier advance
Purchase Return
Supplier Refund
Purchase Void
Sale Return
Commercial Exchange
exact unit duplicate-sale race
Customer Warranty replacement
Customer Warranty duplicate active claim
mixed-Supplier Warranty
Shop Warranty repair
Shop Warranty replacement
Shop Warranty credit
serialized adjustment
stocktake variance
Thaka lifecycle
```

---

## 4.16 Phase 2 Exit Gate

```text
Core business unit tests             PASS
PostgreSQL integration tests         PASS
Stock reconciliation                 PASS
Supplier Khata reconciliation        PASS
Cash reconciliation                  PASS
Tracking identity invariants         PASS
Warranty lifecycle tests             PASS
Idempotent normal retries            PASS
Architecture verifier                PASS
```

---

# 5. Phase 3 — Production Safety & External Effects

## 5.1 Objective

Protect business truth from crashes, lost responses, printer ambiguity, restore failures, disk errors, audit failures, and repeated commands.

Phase 2 proves normal business correctness.

Phase 3 proves that bad days do not corrupt the shop.

---

## 5.2 PostgreSQL Startup Write-Safety Gate

Startup must verify at minimum:

```text
database identity
schema/migration compatibility
pg_is_in_recovery()
transaction_read_only
fsync
full_page_writes
synchronous_commit policy
checksum policy/evidence
primary writable state
```

If the business DB is not safe for writes:

```text
normal mutation capability = BLOCKED
```

Fail closed.

---

## 5.3 Durable Print Outbox

Required architecture:

```text
business transaction
+
ORIGINAL print request
same PostgreSQL transaction
↓
COMMIT
↓
physical printer dispatch
```

Do not use local JSON files as authoritative print intent.

Print states include:

```text
PENDING
DISPATCHING
PRINTED
FAILED_RETRYABLE
OUTCOME_UNKNOWN
ACTION_REQUIRED
CANCELLED
```

If physical submission may have happened but confirmation is lost:

```text
OUTCOME_UNKNOWN
```

Never blindly reprint.

Reprints:

```text
same business document identity
new print attempt
reprint marker
reprint lineage
printer/profile provenance
```

---

## 5.4 Universal External-Effect Finality

External effects follow:

```text
INTENT_PERSISTED
→ EXECUTION_STARTED
→ CONFIRMED
   or OUTCOME_UNKNOWN
   or ACTION_REQUIRED
```

Applies to:

```text
printing
cloud backup
restore/cutover
external license persistence
future updater
future tax/regulatory API
```

Audit failure after an irreversible effect must not return a fake business failure that encourages duplicate retry.

---

## 5.5 Backup

Implement/verify:

```text
local backup
encrypted backup
authenticated manifest
retention
remote upload
remote hash verification
manifest authentication
optional restore rehearsal
```

Verification levels:

```text
UPLOADED
REMOTE_HASH_VERIFIED
REMOTE_MANIFEST_AUTH_VERIFIED
RESTORE_REHEARSAL_VERIFIED
```

---

## 5.6 Backup Key Lifecycle

Backup objects record:

```text
EncryptionVersion
KeyId
KeyVersion
CreatedAt
```

Rotation must not make retained old backups unreadable.

Key-loss is an explicit disaster-recovery condition.

---

## 5.7 Restore

Restore must be treated as a recovery state machine, not a utility command.

Required principles:

```text
maintenance/write barrier
separate recovery identity
opaque authenticated restore authority
staging restore
OID/database identity verification
schema/migration compatibility validation
controlled cutover
recovery reconciliation
fail closed
```

A failure after database rename/cutover must leave enough durable state for deterministic recovery.

---

## 5.8 Licensing

Verify:

```text
fail-closed startup
signed license
shop identity binding
expiry
MaxTerminals
feature flags
key rotation
clock rollback/tamper behavior
```

License failure must not corrupt business data.

---

## 5.9 Audit

Verify:

```text
append-only business audit
technical logs separate
redaction
retention
export permissions
correlation IDs
growth monitoring
```

Secrets never enter business audit.

---

## 5.10 Idempotency

Store:

```text
ClientOperationId
CommandType
PayloadFingerprint
ActorId
TerminalId where applicable
Result identity
CommittedAt
Resolution state
Retention class
```

Same operation ID + different payload:

```text
REJECT
```

Unknown operations are not purged.

---

## 5.11 Crash / Failure Testing

Required scenarios:

```text
app crash before COMMIT
app crash after COMMIT before response
printer response lost
backup upload response lost
restore interruption
audit failure after irreversible effect
disk write failure simulation where safe
DB temporarily read-only
worker restart
same command replay
```

---

## 5.12 Phase 3 Exit Gate

```text
Durability startup tests        PASS
Crash/restart tests             PASS
Idempotency replay tests        PASS
Print outbox tests              PASS
OUTCOME_UNKNOWN tests           PASS
Backup integrity tests          PASS
Restore rehearsal              PASS
License fail-closed tests       PASS
Audit/redaction tests           PASS
```

---

# 6. Phase 4 — Multi-Terminal & Operational Runtime

## 6.1 Objective

Make the backend safe for concurrent terminals and unreliable LAN connections without introducing split-brain authority.

---

## 6.2 Server Authority

First LAN release:

```text
Terminal
→ HTTPS/API
→ Application handlers
→ Domain
→ PostgreSQL
```

No direct terminal PostgreSQL authority.

---

## 6.3 Terminal Identity

Each terminal has:

```text
TerminalId
registration status
protocol/app version
license membership
last authenticated session
```

Terminal may be:

```text
ACTIVE
SUSPENDED
REVOKED
```

Server enforces `MaxTerminals`.

---

## 6.4 Connectivity State Machine

Required:

```text
CONNECTED
DEGRADED
RECONNECTING
DISCONNECTED
```

When disconnected:

Allowed:

```text
local unsaved UI state
local draft/cache
read-only stale display with warning
```

Forbidden:

```text
completed Sale
Purchase posting
Supplier payment
Warranty authoritative mutation
stock posting
local Khata ledger
local sequence allocation
offline invoice authority
```

---

## 6.5 Unknown Outcome Recovery

Scenario:

```text
Terminal sends Sale
↓
Server COMMIT
↓
network response lost
```

Client must not assume failure.

Recovery:

```text
same ClientOperationId
→ reconnect
→ query/replay
→ server resolves existing operation
```

Never generate a fresh operation ID for a retry of the same business intent.

---

## 6.6 Reconnect Revalidation

Before authoritative mutation resumes, revalidate:

```text
Terminal registration
user session
permissions
license
protocol/app/schema compatibility
maintenance state
CashSession
Product
price
stock
exact InventoryUnit
Supplier balance
Warranty state
stocktake locks
```

Terminal clock is never authority.

---

## 6.7 Concurrency Tests

Mandatory races:

```text
two terminals sell same InventoryUnit
two terminals sell final quantity
two terminals pay same Supplier balance
two terminals claim same warranty unit
same ClientOperationId from retries
Purchase receiving same SupplierProduct
sequence allocation stress
cash-session close while Sale starts
stocktake vs Sale
restore maintenance vs terminal mutation
```

---

## 6.8 Phase 4 Exit Gate

```text
LAN state machine              PASS
Terminal registration          PASS
Permission freshness           PASS
Unknown-outcome replay         PASS
Split-brain prevention         PASS
Concurrent stock tests         PASS
Concurrent Khata tests         PASS
Concurrent Warranty tests      PASS
Sequence stress tests          PASS
```

---

# 7. Phase 5 — Scale, Performance & Observability

## 7.1 Objective

Prove that Edge Retails remains responsive, bounded, and diagnosable under realistic long-term shop data.

---

## 7.2 Representative Benchmark Dataset

Minimum representative scale:

```text
Products                  10,000
InventoryUnits           100,000
Sales                    250,000
SaleItems                500,000
InventoryMovements       500,000
SupplierAccountEntries   250,000
plus realistic:
Purchases
Warranty
Audit
Thaka
CashMovement
PrintRequests
```

---

## 7.3 Bounded Query Rules

Large screens must:

```text
filter in PostgreSQL
sort in PostgreSQL
project in PostgreSQL
cap PageSize
support cancellation
avoid N+1
avoid full table materialization
```

Deep histories use deterministic keyset/seek pagination.

Avoid growing deep `OFFSET/LIMIT`.

---

## 7.4 WPF Runtime

Verify:

```text
row virtualization
column virtualization
recycling
bounded collections
search debounce
cancel stale query
no UI-thread database blocking
```

---

## 7.5 Report Resource Governance

Reports need:

```text
CancellationToken
statement/command timeout
bounded DTO memory
database aggregation
read-only semantics where applicable
query-plan evidence
```

Cancelled report must release database resources and never overwrite a newer result.

---

## 7.6 Performance Evidence

Collect:

```text
p50
p95
p99
DB execution time
rows scanned
rows returned
query plan
memory usage
WPF working set
GC behavior where relevant
```

Do not create blind indexes.

Indexes require measured need.

---

## 7.7 Observability

Backend health exposes:

```text
DB latency
DB write-safety status
schema compatibility
backup age
remote backup verification
disk free
worker heartbeat
print backlog
OUTCOME_UNKNOWN backlog
ACTION_REQUIRED backlog
failed jobs
reconciliation failures
```

Use stable diagnostic codes.

---

## 7.8 Operational Thresholds

Thresholds are configuration/policy, not random constants.

Classifications:

```text
HEALTHY
DEGRADED
ACTION_REQUIRED
UNAVAILABLE
```

---

## 7.9 Data Growth

Monitor high-growth authorities:

```text
audit.business_events
inventory movements
cash movements
Supplier Khata
print requests
ClientOperation/idempotency data
```

Introduce archival/partitioning only after preserving historical correlation and legal/business retention.

---

## 7.10 Phase 5 Exit Gate

```text
Benchmark dataset loaded            PASS
p50/p95/p99 captured                PASS
critical screens bounded            PASS
query plans reviewed                 PASS
WPF memory bounded                   PASS
report cancellation                 PASS
diagnostics/SLO behavior             PASS
no unexplained performance blocker   PASS
```

---

# 8. Phase 6 — Final Certification & Long-Term Maintenance

## 8.1 Objective

Turn the completed implementation into a certified production release with a safe upgrade/restore/maintenance future.

Phase 6 is primarily proof, packaging, release, and governance.

Do not use Phase 6 to hide unfinished Phase 1–5 business implementation.

---

## 8.2 Fresh Installation Certification

Test:

```text
clean supported Windows machine
→ installer
→ PostgreSQL dependency/config
→ fresh database
→ migrations
→ first setup
→ login
→ complete business lifecycle
→ backup
→ restore rehearsal
```

---

## 8.3 Upgrade Certification

Test:

```text
supported older production DB
→ backup
→ maintenance gate
→ current application
→ forward migration
→ startup compatibility
→ reconciliation
→ normal business continuity
```

---

## 8.4 Database Compatibility Matrix

Prove:

```text
new app + older supported DB
→ migrate forward

new app + unsupported-too-old DB
→ block

old app + newer DB
→ block

app + unknown future migration
→ block database-ahead

old compatible backup + current app
→ validate + restore + migrate forward

unknown/future backup
→ reject before cutover
```

---

## 8.5 Migration Rehearsal

Disposable PostgreSQL:

```text
empty DB
→ baseline/latest migrations
→ expected constraints/indexes
→ integration tests
```

Where architecture policy requires:

```text
Down → zero → Up
```

must also be rehearsed.

Never rewrite released production migrations.

---

## 8.6 Architecture Drift Gate

CI/release gate checks:

```text
EF model drift
migration drift
schema constraints
enum/state contract
permissions
TrackingCode authority
lock hierarchy
Supplier Khata directions
Warranty transitions
Warranty custody
navigation contract
POS Draft contract
print outbox
startup durability
LAN state machine
authority manifest SHA
root pointer-only
```

Green compile alone is insufficient.

---

## 8.7 Release Pipeline

Minimum:

```text
dotnet restore
dotnet format verification
Release build
unit tests
architecture tests
EF drift
migration rehearsal
PostgreSQL integration tests
runtime smoke tests
package/installer build
package validation
clean-install test
upgrade test
```

---

## 8.8 Final Reconciliation

Before production freeze verify:

```text
StockBalance
vs movement-derived stock

ProductCostState
vs cost/movement history

Supplier balance
vs SupplierAccountEntry ledger

Cash expected
vs CashMovement

exact InventoryUnit states
vs stock/accounting mapping

Warranty active units
vs claims

print requests
vs business documents

ClientOperation
vs committed command results
```

---

## 8.9 Documentation / Operations Package

Final package should include:

```text
canonical architecture
authority manifest
database migration map
backup/restore runbook
installer guide
upgrade guide
disaster recovery runbook
diagnostics guide
support error-code catalog
security/key rotation notes
release checklist
known limitations
```

---

## 8.10 Phase 6 Exit Gate

The backend may be declared Production Baseline only when:

```text
Release build                    PASS
Warnings                         0
Unit tests                       PASS
Architecture tests               PASS
PostgreSQL integration           PASS
Migration rehearsal              PASS
Fresh install                    PASS
Upgrade                          PASS
Backup/restore rehearsal         PASS
Concurrency tests                PASS
Performance benchmark            PASS
Installer/package                PASS
Architecture verifier            PASS
Canonical SHA/manifest           MATCH
No unresolved Critical blocker   TRUE
No unresolved High blocker       TRUE
```

---

# 9. Antigravity Working Protocol

Every Antigravity working session should begin with:

```text
1. Read Architecture_Authority_Manifest.json
2. Verify canonical SHA
3. Read the relevant canonical architecture sections
4. Inspect current code/schema/tests
5. Identify exact drift
6. Implement coherent fixes
7. Compile
8. Test
9. Run architecture verifier
10. Update Phase evidence
```

Do not begin implementation from memory when the authoritative section is available.

---

# 10. Antigravity Session Prompt Template

Use a prompt similar to:

```text
We are implementing Edge Retails Backend according to the frozen canonical architecture.

Workspace:
C:\Users\muham\OneDrive\Desktop\Point of Sale

Canonical architecture:
docs\Edge_Retails_Final_Architecture_Report_v1.md

Authority manifest:
docs\Architecture_Authority_Manifest.json

Roadmap:
Edge_Retails_Antigravity_Backend_Implementation_Roadmap.md

Rules:
- Follow exactly six macro phases.
- Do not create new artificial phases.
- Architecture is authority unless a real contradiction is proven.
- Never claim runtime PASS from static inspection.
- Never run destructive tests on a production database.
- Preserve historical business identity.
- Run build/tests/verifiers after coherent implementation blocks.
- Record evidence before closing a macro phase.

Current target:
<PHASE NUMBER + CURRENT OBJECTIVE>

First inspect the relevant architecture sections and current implementation, then continue implementation until the current macro-phase exit gate is satisfied.
```

---

# 11. Phase Status Board

Update this table during the project.

| Phase | Status | Main Gate |
|---|---|---|
| Phase 1 — Canonical Schema & Domain Alignment | NOT CLOSED | EF/schema/model alignment |
| Phase 2 — Core Business Transaction Engine | NOT STARTED | business + reconciliation |
| Phase 3 — Production Safety & External Effects | NOT STARTED | crash/recovery/outbox |
| Phase 4 — Multi-Terminal & Operational Runtime | NOT STARTED | concurrency/LAN |
| Phase 5 — Scale, Performance & Observability | NOT STARTED | benchmark/SLO |
| Phase 6 — Final Certification & Maintenance | NOT STARTED | production certification |

Allowed statuses:

```text
NOT STARTED
IN PROGRESS
BLOCKED
STRUCTURALLY VERIFIED
RUNTIME VERIFICATION
CLOSED
```

Do not mark a phase `CLOSED` until its exit gate is green.

---

# 12. Dependency Order

The six phases are intentionally ordered:

```text
Schema/domain correctness
        ↓
Business transaction correctness
        ↓
Production failure safety
        ↓
Multi-terminal concurrency
        ↓
Scale/performance
        ↓
Release certification
```

Do not jump ahead to performance optimization while the authoritative transaction engine is incomplete.

Do not build LAN conflict logic before single-server transaction correctness is proven.

Do not certify a release while architecture drift remains.

---

# 13. Final Long-Run Target

At the end of this roadmap Edge Retails Backend should have:

```text
one canonical architecture
one canonical schema
one migration history
one inventory authority
one Supplier Khata authority
one Sale engine
one Purchase engine
one Warranty lifecycle
one TrackingCode sequence authority
one print outbox
one idempotency engine
one LAN server authority
one release/certification pipeline
```

The final system should not merely “work”.

It should be:

```text
auditable
replay-safe
crash-safe
financially reconcilable
inventory-reconcilable
traceable
migration-safe
restore-safe
multi-terminal-safe
bounded under load
diagnosable
maintainable for years
```

---

# 14. Roadmap Completion Rule

Do not create a seventh implementation phase.

If new work is discovered:

```text
Schema/domain issue            → Phase 1
Business transaction issue     → Phase 2
Production/failure issue       → Phase 3
LAN/concurrency issue          → Phase 4
Performance/diagnostics issue  → Phase 5
Release/upgrade/support issue  → Phase 6
```

Everything must map into one of the six macro phases.

That keeps the project understandable, measurable, and finishable.

---

# 15. Current Starting Point

The immediate Antigravity starting point is:

```text
PHASE 1
Canonical Schema & Domain Alignment
```

Initial Phase 1 action:

```text
1. verify canonical manifest SHA
2. inventory current Domain/EF/migrations
3. compare schema against canonical Sections 0-233 + 233.1
4. build one Phase 1 defect matrix
5. remediate all Phase 1 drift
6. generate/inspect migration
7. run Phase 1 exit gate
8. formally close Phase 1
9. move to Phase 2
```

No roadmap redesign is required unless the canonical architecture itself changes through the controlled RFC process.
