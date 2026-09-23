# Edge Retails Backend Deep Audit Report

**Audit date:** 20 September 2026  
**Audit target:** Current local working tree, including uncommitted backend implementation  
**Git HEAD at audit start:** `8d4f273 docs: finalize architecture through section 169`  
**Architecture authority:** `docs/Edge_Retails_Final_Architecture_Report_v1.md`, Sections 1-169  
**Scope:** Domain, Application, Infrastructure, PostgreSQL schema/migrations, Worker, tests, and backend operational readiness  
**Explicitly excluded:** WPF/frontend production integration quality

---

# 1. Executive Verdict

Edge Retails is no longer a demo-only backend.

The current implementation contains a serious local commerce kernel with:

- PostgreSQL-backed persistence
- EF Core transactional writes
- Dapper read projections
- transaction-scoped PostgreSQL advisory locks
- ClientOperationId idempotency
- multi-unit product conversions
- inventory buckets
- moving weighted average cost state
- inventory lots
- immutable lot-consumption provenance
- serialized physical-unit tracking
- Sales and Sale Returns
- Purchasing, Purchase Returns, and Purchase Void
- Quotations
- Cash sessions and cash movements
- Warranty workflows
- Stocktake workflows
- append-only business audit at application level
- backend-authoritative historical receipt snapshots
- a complete current baseline migration
- real PostgreSQL integration testing

However, the complete V1 backend platform is **not production-ready yet**.

The biggest reason is not Sales/Purchasing. The commerce kernel is comparatively strong.

The blockers are:

1. Identity / Authentication / Permissions are not physically implemented.
2. License Guard and System State Guard are not physically implemented.
3. Thaka backend is not implemented.
4. Expense backend is not implemented.
5. Reporting/Dashboard backend is not implemented.
6. Backup/Restore/License/Diagnostics worker is still a template worker.
7. Master-data command authority is incomplete.
8. First Setup/bootstrap backend is missing.
9. Two newly-added Dapper read projections currently fail at runtime.
10. Test coverage is much stronger than before, but several important modules still lack handler-level real PostgreSQL integration coverage.

---

# 2. Marks

## 2.1 Architecture Design Quality

**94 / 100**

The architecture is strong because it has explicit rules for:

- offline-first operation
- local PostgreSQL authority
- cloud control-plane separation
- transaction boundaries
- idempotency
- deterministic locking
- inventory provenance
- serialized units
- multi-unit snapshots
- Sales/Purchase return semantics
- Purchase Void safety
- cash drawer truth
- warranty custody
- stocktake correction movements
- immutable historical snapshots
- future LAN path without contaminating V1

The remaining architecture deductions are mostly operational-detail areas, not core commerce-model weaknesses.

## 2.2 Implemented Core Commerce Kernel

**88 / 100**

This score covers:

- Catalog unit-conversion foundation
- Inventory
- Costing
- Sales
- Returns
- Purchasing
- Purchase Returns
- Purchase Void
- Serialized provenance
- Warranty
- Stocktake
- Cash
- Quotation
- persistence and migrations

This part is technically substantial and is the strongest area of the backend.

## 2.3 Complete V1 Backend Implementation

**66 / 100**

This score drops because entire required V1 backend areas are absent:

- Identity/Permissions
- License/System State enforcement
- Thaka
- Expenses
- Reporting
- Setup/bootstrap
- Backup/Restore/Diagnostics
- production Worker jobs
- complete master-data commands

## 2.4 Production Readiness

**55 / 100**

The commerce engine has good integrity controls, but production deployment would currently expose unacceptable authority gaps because a caller can reach business handlers without proven authentication, authorization, license validity, or normal operational state.

---

# 3. Audit Evidence Snapshot

## 3.1 Backend code footprint

Current non-generated C# footprint observed during audit:

| Project | C# files | Approx. lines |
|---|---:|---:|
| Domain | 13 | 1,239 |
| Application | 18 | 6,017 |
| Infrastructure | 23 | 11,223 |

No `TODO`, `FIXME`, `NotImplementedException`, obvious stub marker, or placeholder marker was found inside Domain/Application/Infrastructure source.

That is positive, but absence of TODO comments is not evidence of functional completeness.

## 3.2 PostgreSQL footprint

The isolated PostgreSQL 18 database currently contains **48 tables including EF migration history**, therefore **47 application/system tables excluding EF history**.

Schema table counts:

| Schema | Tables |
|---|---:|
| audit | 1 |
| catalog | 5 |
| finance | 2 |
| inventory | 12 |
| parties | 2 |
| purchasing | 7 |
| sales | 10 |
| system | 4 including EF history |
| warranty | 5 |

Index counts observed:

| Schema | Indexes |
|---|---:|
| audit | 5 |
| catalog | 18 |
| finance | 6 |
| inventory | 41 |
| parties | 7 |
| purchasing | 27 |
| sales | 38 |
| system | 6 |
| warranty | 18 |

This is a real relational model, not an in-memory/demo facade.

---

# 4. Current Build and Test Truth

## 4.1 Infrastructure build

```text
Release build
0 warnings
0 errors
```

Status: **PASS**

## 4.2 Unit tests

```text
126 passed
0 failed
```

Status: **PASS**

Caution: a significant portion of the unit suite is forensic/static/frontend-oriented. The raw number must not be interpreted as 126 deep domain-behavior tests.

## 4.3 Real PostgreSQL integration tests

Latest complete integration run during this audit:

```text
10 total
8 passed
2 failed
```

The two failures are both newly-added read-side materialization defects, not write-transaction failures.

Failing projections:

1. `ProductPurchaseProvenanceRowDto`
2. `PurchaseReturnHistoryRowDto`

Earlier, before these new read projections were introduced, the backend reached:

```text
9 / 9 integration tests PASS
```

including handler-level Sales/Purchase tests.

## 4.4 Migration lifecycle

Verified on isolated PostgreSQL 18:

```text
Migration Up       PASS
Migration Down 0   PASS
Migration Up again PASS
Model drift        ZERO
```

Status: **STRONG PASS**

---

# 5. Severity Scale

## P0 - Production Blocker

Must be solved before production use.

## P1 - High

Can cause incorrect access, broken required workflows, financial/reporting failures, or severe operational gaps.

## P2 - Medium

Important robustness, maintainability, observability, or incomplete-query issue.

## P3 - Low

Polish, optimization, or future-hardening issue.

---

# 6. P0 Findings

## P0-01 - Identity, Authentication, and Permissions are not implemented

Architecture requires:

```text
Authenticated Session
Authorization
SecurityVersion
Granular Permissions
Owner / Manager / Cashier
```

Physical backend search found no backend User/Role/Permission/SecurityVersion model or enforcement service.

Current commands accept values such as:

```text
CashierUserId
ActorId
CreatedBy
VoidedBy
```

as plain GUIDs.

A direct caller does not currently have to prove that:

- the user exists
- the user is active
- the session is authenticated
- SecurityVersion is current
- the user has `sales.create`
- the user has Purchase Return permission
- the user has Purchase Void permission
- the user has Stocktake Post permission
- the user is an Owner for owner-only actions

### Risk

A caller with backend access could invoke sensitive handlers with an arbitrary GUID.

### Required resolution

Implement the Identity/Permissions phase before production:

```text
User
Role
Permission
RolePermission
UserRole
Session/Authority Context
SecurityVersion
Credential/PIN hashing
Active/inactive state
At least-one-active-owner invariant
Command authorization checks
Permission-safe read projections
```

### Severity

**P0**

---

## P0-02 - License Guard is designed but not enforced

Architecture says normal business writes require:

```text
LicenseStatus = VALID
```

No physical `LicenseStatus`, signed-license verifier, entitlement evaluator, or License Guard was found in Domain/Application/Infrastructure.

### Risk

Current backend business commands can execute regardless of license state.

### Required resolution

Implement:

- signed license verification
- device binding
- entitlement state
- expiry/grace handling
- trusted-time handling
- revocation state
- command-level License Guard
- controlled exemptions for diagnostics/recovery/license import

### Severity

**P0**

---

## P0-03 - System State Guard is not implemented

Architecture defines:

```text
NORMAL
MAINTENANCE
RECOVERY_REQUIRED
```

and states business writes require:

```text
SystemState = NORMAL
```

No physical System State Guard exists.

### Risk

During restore/recovery/maintenance, a direct backend caller could still mutate business state.

### Required resolution

Implement persistent operational state plus a mandatory command guard before business writes.

### Severity

**P0**

---

## P0-04 - Thaka backend is absent

Only the cash movement enum contains `ThakaPaymentCashIn`.

No physical backend models or handlers were found for:

- ThakaProject
- Material Issue
- Material Reversal
- Thaka Payment
- Payment Reversal
- Settlement
- Reopen
- Discount/adjustment
- Thaka inventory/cost integration
- Thaka reporting queries

### Risk

One of the explicitly required V1 business modules is still frontend/demo-only or non-existent at backend level.

### Severity

**P0 for complete V1 release**

---

## P0-05 - Expense backend is absent

Only `ExpenseCashOut` exists in the cash movement enum.

No backend Expense aggregate, category, POSTED/VOIDED workflow, audit, queries, or cash-posting command was found.

### Risk

Operating expenses cannot be represented as authoritative backend transactions.

This also blocks correct Net Profit reporting.

### Severity

**P0 for complete V1 release**

---

# 7. P1 Findings

## P1-01 - Two Dapper read paths currently crash at runtime

Current real PostgreSQL integration failures:

### A. ProductPurchaseProvenanceRowDto

PostgreSQL/Npgsql returns constructor values such as:

```text
Guid
Guid
Guid
Guid
string
string
decimal...
DateTime
```

while the positional record expects nullable GUIDs and `DateTimeOffset`.

Dapper cannot find a matching constructor.

### B. PurchaseReturnHistoryRowDto

Dapper receives:

```text
DateTime
Int32 settlement mode
```

while the record expects:

```text
DateTimeOffset
PurchaseReturnSettlementMode
```

### Risk

The newly-added read-side APIs fail despite successful compilation.

### Recommended fix

Do not depend on direct positional record materialization for provider-sensitive enum/timestamp/nullable shapes.

Use internal database row models, for example:

```text
PurchaseReturnHistoryDbRow
ProductPurchaseProvenanceDbRow
```

with provider-native field types, then explicitly map to application DTOs.

This is safer than trying to make every public DTO constructor match Npgsql runtime types.

### Severity

**P1**

---

## P1-02 - Reporting and Dashboard backend are absent

No backend query/service was found for:

- Dashboard KPIs
- daily report
- monthly report
- yearly report
- net sales
- profit
- purchase spend
- expenses
- inventory loss
- Thaka reporting
- warranty operational reporting

### Risk

Frontend reporting would still depend on demo/local projection logic instead of backend financial truth.

### Severity

**P1**

---

## P1-03 - Worker is still the default template

Current Worker behavior is effectively:

```text
while running:
    log current time
    delay 1 second
```

No physical jobs were found for:

- encrypted PostgreSQL backup
- retention
- backup verification
- cloud upload
- update checks
- diagnostics heartbeat
- maintenance
- recovery orchestration

### Risk

Architecture promises operational reliability that current Worker does not provide.

### Severity

**P1**

---

## P1-04 - Backup/Restore backend is absent

No physical:

- backup manifest
- backup job
- encryption/wrapping
- restore journal
- verification
- retention
- recovery key handling
- cloud backup metadata
- restore-state guard

was found.

### Severity

**P1**

---

## P1-05 - First Setup/bootstrap backend is absent

Architecture requires atomic setup that creates:

- Shop
- Owner
- Walk-in Customer
- roles/permissions
- defaults
- setup COMPLETE marker only after successful commit

Physical search found no backend bootstrap command.

### Risk

Initial system authority remains dependent on frontend/demo implementation or manual data creation.

### Severity

**P1**

---

## P1-06 - Master-data command layer is incomplete

No authoritative Application handlers were found for normal CRUD/lifecycle of:

### Catalog

- Product create/update/deactivate
- Category create/update/deactivate
- Unit create/update/deactivate
- Brand management

Only ProductUnit-specific handlers are currently present.

### Parties

No backend command handlers were found for:

- Customer create/update/deactivate
- Supplier create/update/deactivate

### Risk

The transaction engine can use master data, but master-data lifecycle itself is not yet a complete backend-owned workflow.

### Severity

**P1**

---

## P1-07 - Business audit shape is weaker than the final architecture

Physical `BusinessAuditEvent` contains:

```text
ActorId
Action
EntityType
EntityId
CorrelationId
OccurredAt
Summary
```

Final architecture requested richer forensic fields including:

```text
Reason
BeforeJson optional
AfterJson optional
Machine/session metadata where available
```

### Positive

EF blocks modification/deletion of tracked `BusinessAuditEvent` entities.

### Gap


The forensic payload is currently too shallow for high-risk administrative changes.

### Severity

**P1/P2 depending on action class**

---

## P1-08 - Cost/profit read security cannot be enforced yet

The new provenance read side exposes values such as:

```text
UnitCostSnapshot
TotalCostSnapshot
GrossProfitSnapshot
OriginalUnitCost
EffectiveUnitCost
```

Architecture explicitly states sensitive cost/profit DTO fields must follow permissions.

Because backend Identity/Permissions is absent, these projections have no real authorization barrier.

### Severity

**P1**

---

# 8. Core Commerce Strengths

## 8.1 Transaction runner

`EfTransactionRunner` correctly:

- starts a real database transaction
- supports nested handler reuse
- rolls back failed `IResult`
- clears ChangeTracker after failed transaction
- rolls back exceptions
- commits only successful operation results

This is a strong implementation choice.

## 8.2 PostgreSQL advisory locks

`PostgresOperationLock` implements transaction-scoped advisory locks.

Current scopes include operation/idempotency, product, supplier invoice, serialized identity, and other resource keys.

This is materially better than application-process locks because it works at database transaction scope.

## 8.3 ClientOperationId idempotency

Sales and Purchasing workflows use operation locks plus database uniqueness.

A real PostgreSQL test proves Sale retry does not create a duplicate Sale.

## 8.4 Deterministic resource locking

Stock-affecting multi-product paths sort product IDs before lock acquisition.

This reduces deadlock risk and provides a stable cross-module ordering contract.

---

# 9. Inventory Audit

The inventory bucket model supports Sellable, Damaged, Defective, With Supplier, and Scrap.

Stock mutation is represented through movements/effects rather than directly treating the displayed balance as the ledger.

`StockBalance` is current-state result, not the only source of truth.

Physical inventory movement structure includes movement header, bucket effects, serialized-unit effects, correlation/reference metadata, unit cost snapshot, and recognized loss amount.

**Status: STRONG**

---

# 10. Costing Audit

`ProductCostState` maintains CostedQty, TotalInventoryCost, MovingAverageCost, LastPurchaseCost, LastPurchaseAt, and version/concurrency state.

During real PostgreSQL testing, the audit found and fixed a first-purchase defect: a new ProductCostState existed only in EF tracking while a later FOR UPDATE query looked only in PostgreSQL before SaveChanges.

The repository now checks `DbSet.Local` first and queries PostgreSQL only when needed.

Real transactional tests passed after the fix.

**Status: STRONG**

---

# 11. Lot Provenance Audit

The backend contains `InventoryLot`, `InventoryLotBucketBalance`, and immutable `InventoryLotConsumption`.

Lot consumption records:

```text
LotId
MovementId
Quantity
UnitCostSnapshot
TotalCostSnapshot
OccurredAt
```

This closes the dangerous Purchase Void loophole where quantity could leave and later return while current stock appeared untouched.

Purchase Void now checks completed state, prior returns, historical purchase-item consumption, Stocktake blocks, purchase-origin lot availability, serialized-unit state, and cash reversal requirements.

A real handler test proves Purchase -> Sale -> lot consumption -> Purchase Void rejection.

**Status: VERY STRONG**

---

# 12. Multi-Unit Audit

Implemented:

- base unit
- ProductUnit
- factor-to-base
- purchase/sale/thaka capability flags
- default purchase/sale markers
- transaction quantity snapshots

Precision:

```text
factor_to_base_unit numeric(18,9)
quantities numeric(18,6)
```

Partial unique indexes protect one default Sale/Purchase unit.

Real PostgreSQL test validates the default-unit uniqueness constraint.

**Status: STRONG**

---

# 13. Serialized Inventory Audit

Serialized unit truth includes serial, IMEI1, IMEI2, product, status, acquisition cost, SourcePurchaseItemId, InventoryLotId, and version.

Purchase receiving normalizes identities and locks identity keys.

Exact units are selected for Sale. Sale writes exact `SaleItemUnit`. Serialized Sale consumes exact lot carrying quantity.

Sale Return restores the exact unit and assigns new return-lot provenance.

A real PostgreSQL test validates exact receive -> Sale -> SOLD -> exact return -> IN_STOCK -> new return lot, while original Purchase remains non-voidable after historical consumption.

**Status: VERY STRONG**

---

# 14. Sales Audit

Implemented aggregate:

- Sale
- SaleItem
- SalePayment
- SaleItemUnit
- SaleReturn
- SaleReturnItem
- SaleReturnItemUnit

Strong points:

- invoice generated inside transaction
- backend reloads product/unit
- authoritative price validation
- stale expected price detection
- stocktake re-check
- row/resource locking
- exact serialized selection
- deterministic invoice discount allocation
- cost snapshot and COGS
- same-transaction cash movement
- backend receipt snapshot
- business audit
- ClientOperationId idempotency
- Quotation conversion through the normal Sale engine

Return logic correctly separates reason from disposition and uses original net-line value with cumulative residual rounding.

A real test validates Rs299 net line returned as 99.67 + 99.66 + 99.67 = 299.00 exactly.

**Status: VERY STRONG**

---

# 15. Purchasing Audit

Implemented aggregate:

- Purchase
- PurchaseItem
- PurchaseItemUnit
- PurchaseReturn
- PurchaseReturnItem
- PurchaseReturnItemUnit
- PurchaseVoid

Strong points include supplier validation, normalized supplier invoice, supplier-invoice advisory lock, duplicate protection, landed-cost allocation, effective base cost, cost state update, lot creation, exact serialized receiving, optional drawer settlement, audit, and operation idempotency.

Purchase Return correctly separates `SupplierReturnValue` and `InventoryCostRemoved`.

A dedicated test uses supplier price 100, effective inventory cost 120, quantity returned 2, resulting in supplier value 200 and inventory cost removed 240.

The write path succeeds. Read-history verification currently hits the Dapper mapping defect listed above.

**Status: STRONG write side, read defect open**

---

# 16. Quotation Audit

Physical handlers include CreateQuotation, UpdateQuotation, IssueQuotation, CancelQuotation, and PrepareQuotationForSale.

Quotation preparation does not mutate stock/cash, and CompleteSale owns final conversion.

Remaining needs are real PostgreSQL conversion tests and permission enforcement once Identity exists.

**Status: GOOD**

---

# 17. Cash Session Audit

Physical backend includes open CashSession, cash movement posting, close CashSession, expected cash calculation, counted closing cash, over/short difference, and movement direction/type validation.

Database has a partial unique constraint preventing multiple active sessions.

Real PostgreSQL test proves one-open-session enforcement.

Sales/Purchases/Returns create cash movements in the same business transaction where required.

Remaining needs:

- handler-level Sale Cash In test
- Purchase Cash Out test
- Sale Return Cash Out
- Purchase Return Cash In
- Purchase Void Cash In
- close-session total verification
- permissions
- reporting queries

**Status: STRONG FOUNDATION**

---

# 18. Stocktake Audit

Implemented:

- stocktake aggregate
- scope
- Draft/Counting/Review/Posted/Cancelled
- count items
- serialized expected/found checks
- review
- posting
- positive variance cost basis
- negative variance recognized loss
- exact serialized missing-unit write-off
- movement-based correction
- one-open-stocktake database constraint

Stock-affecting operations re-check Stocktake after acquiring locks.

Remaining needs are stronger handler-level real PostgreSQL concurrency tests, permissions, and read/query surfaces.

**Status: STRONG**

---

# 19. Warranty Audit

Implemented:

- customer warranty claims
- claim items
- unit links
- claim events
- shop-stock warranty cases

Customer-owned warranty correctly avoids inventing shop inventory simply because the shop has custody.

A real PostgreSQL test proves customer warranty claim creation does not create an `inventory.units` record.

Shop-owned warranty remains integrated with inventory/cost state.

Remaining needs:

- complete read-side services
- full lifecycle integration tests
- permissions

**Status: GOOD/STRONG**

---

# 20. Receipt Snapshot Audit

Backend owns historical receipt snapshot generation through `ReceiptSnapshotProvider`.

Snapshot includes ShopName, Phone, Address, Header, Footer, ShowCustomer, ShowCashier, LogoBehavior, and TemplateVersion.

CompleteSale captures this server-side, so caller-provided arbitrary historical receipt content is not authoritative.

**Status: STRONG**

---

# 21. Business Audit Audit

Positive:

- dedicated `audit.business_events`
- critical commerce handlers call `IBusinessAuditWriter`
- EF rejects tracked audit update/delete
- CorrelationId links operations

Weakness:

Current audit payload is thinner than final architecture.

Recommended additions:

```text
Reason
BeforeJson
AfterJson
SessionId
MachineId/TerminalId
permission/security context where useful
```

For sensitive security/configuration actions, before/after state should be captured.

---

# 22. Database Audit

Strengths:

- PostgreSQL-native schemas
- explicit numeric precision
- meaningful FK constraints
- restricted deletes
- filtered unique indexes
- transaction row locks
- migration history under system schema
- snake_case consistency
- model drift zero
- Migration Up/Down/Up verified

Current baseline:

`20260920094824_InitialProductionBaseline`

This clean baseline remains valid while no production database depends on an older migration chain.

**Status: VERY STRONG**

---

# 23. Read-Side / CQRS Audit

Current Dapper read side includes:

- Sales History
- Sale Detail
- Sale Return History
- Quotation List
- Quotation Detail
- Purchase History
- Purchase Detail
- Supplier Purchase History
- Purchase Return History
- Product Purchase Provenance
- Product Sale History
- Lot Consumption Trace
- Serialized Unit History

The direction is correct, but provider-sensitive direct positional record materialization has caused runtime failures.

Recommended rule:

```text
PostgreSQL/Npgsql row
-> private infrastructure DB-row type
-> explicit conversion
-> public application DTO
```

Use this especially for:

- enums
- nullable enums
- timestamptz
- DateOnly
- nullable UUID
- provider-sensitive numeric shapes

---

# 24. Reliability Audit

Strong:

- real transactions
- rollback on failed Result
- deterministic locking
- DB uniqueness
- idempotency
- concurrency Version fields
- migration rehearsal
- real PostgreSQL tests

Missing or weak:

- no PostgreSQL transient retry strategy
- no production Worker maintenance
- no backup verification
- no recovery orchestration
- no health model
- no DB diagnostics service
- no support bundle
- no restore journal

**Status: CORE TRANSACTION RELIABILITY GOOD, PLATFORM RELIABILITY INCOMPLETE**

---

# 25. Security Audit

Architecture security design is strong, including separate signing/encryption trust domains and rejection of hidden master credentials.

Physical implementation is incomplete.

Current blockers:

- no authenticated backend session
- no authorization service
- no role/permission persistence
- no SecurityVersion
- no signed license verifier
- no System State Guard
- no recovery authorization verifier
- no observed database-role hardening
- no permission filtering for cost/profit reads

**Status: DESIGN STRONG, IMPLEMENTATION INCOMPLETE**

---

# 26. Functional Coverage Matrix

| Module | Architecture | Physical write side | Physical read side | Real PostgreSQL verification | Status |
|---|---|---|---|---|---|
| Product Units | Final | Yes | Partial | Yes | Green |
| Inventory | Final | Yes | Provenance added | Partial | Green/Amber |
| Costing | Final | Yes | Provenance added | Yes | Green |
| Serialized Inventory | Final | Yes | Added | Yes | Green |
| Sales | Final | Yes | Yes | Yes | Green |
| Sale Returns | Final | Yes | Added | Yes write | Green/Amber |
| Purchasing | Final | Yes | Yes | Yes | Green |
| Purchase Returns | Final | Yes | Added | Write passes, read mapping fails | Amber |
| Purchase Void | Final | Yes | Via Purchase Detail | Yes | Green |
| Quotation | Final | Yes | Yes | Limited | Amber |
| Cash Session | Final | Yes | Limited | DB constraint yes | Amber |
| Stocktake | Final | Yes | Limited | DB constraint yes | Amber |
| Warranty | Final | Yes | Limited | Basic PG test | Amber |
| Customers | Final | Repository only | No full query layer | Limited | Amber/Red |
| Suppliers | Final | Repository only | Purchase joins | Limited | Amber/Red |
| Catalog Product CRUD | Final | Missing | Missing | No | Red |
| First Setup | Final | Missing | N/A | No | Red |
| Identity/Permissions | Final | Missing | Missing | No | Red |
| License Guard | Final | Missing | Missing | No | Red |
| System State | Final | Missing | Missing | No | Red |
| Thaka | Final | Missing | Missing | No | Red |
| Expenses | Final | Missing | Missing | No | Red |
| Dashboard/Reports | Final | Missing | Missing | No | Red |
| Backup/Restore | Final | Missing | Missing | No | Red |
| Worker jobs | Final | Template only | N/A | No | Red |

---

# 27. Test Coverage Audit

Current real PostgreSQL coverage includes:

- precision/index constraints
- one open CashSession
- one open Stocktake
- one active default Sale unit
- customer warranty does not create inventory
- Purchase -> Sale -> lot consumption
- Sale retry idempotency
- Purchase Void blocked after consumption
- discounted partial Sale Return residuals
- serialized exact-lot Sale/Return provenance

High-value missing integration tests:

### Concurrency

- simultaneous Sale of final quantity
- simultaneous Sale of same serialized unit
- simultaneous same ClientOperationId
- simultaneous same supplier invoice
- Stocktake start racing Sale
- Stocktake start racing Purchase

### Cash

- Cash Sale movement atomicity
- Cash Sale Return
- Cash Purchase
- Cash Purchase Return
- Cash Purchase Void
- close-session expected cash

### Quotation

- conversion success
- expiry
- double conversion
- quoted price snapshot
- stock revalidation failure

### Warranty

- full customer claim lifecycle
- shop-owned send supplier
- supplier replacement
- scrap/loss

### Stocktake

- positive correction
- negative MWA correction
- serialized missing unit
- unresolved unexpected unit

### Rollback

- failure after inventory changes are staged
- cash-session validation failure after staged inventory changes
- no partial rows after rollback

### Security

After security implementation:

- expired license blocks writes
- maintenance mode blocks writes
- unauthorized Cashier blocks privileged actions
- stale SecurityVersion blocks command

---

# 28. Code Quality Audit

Positive:

- clear project boundaries
- no SQL in Domain business models
- EF write repositories centralized
- Dapper reserved for reads
- business exceptions translated into Results
- explicit precision helpers
- deterministic ordering
- meaningful business error codes
- no generic-repository overengineering
- no unnecessary MediatR/message bus

Improvement areas:

- several transaction handlers are large
- repeated safety checks could be factored carefully without hiding business order
- Dapper mapping needs one standard convention
- authorization should become a mandatory reusable boundary
- behavioral unit tests should grow
- master-data commands need the same rigor as commerce commands

---

# 29. Production Blocker List

Before complete V1 production release:

1. Fix both Dapper runtime mapping failures.
2. Implement Identity and Authentication.
3. Implement granular Permissions and SecurityVersion.
4. Implement License Guard.
5. Implement System State Guard.
6. Implement First Setup/bootstrap.
7. Implement Product/Category/Unit/Brand command layer.
8. Implement Customer/Supplier command layer.
9. Implement Thaka backend.
10. Implement Expenses backend.
11. Implement Dashboard/Reports backend.
12. Implement Backup/Restore backend.
13. Replace template Worker with production jobs.
14. Implement operational diagnostics.
15. Apply permission-safe read projections.
16. Add high-value real PostgreSQL concurrency/security tests.

---

# 30. Recommended Execution Order

## Phase 1 - Close current commerce slice

1. Fix Dapper DB-row mapping.
2. Restore full integration suite to green.
3. Add concurrency stress tests.
4. Add cash atomicity tests.
5. Add Quotation conversion tests.
6. Add Warranty/Stocktake handler tests.

## Phase 2 - Identity and operational authority

1. Identity models
2. PIN/password hashing
3. Roles
4. Permissions
5. SecurityVersion
6. authenticated operation context
7. authorization
8. License Guard
9. System State Guard

This converts the backend from "correct when trusted code calls it" into a secured application backend.

## Phase 3 - Missing business modules

1. Catalog master-data commands
2. Customer/Supplier commands
3. First Setup
4. Expenses
5. Thaka

## Phase 4 - Reporting/read platform

1. Dashboard KPIs
2. Sales reports
3. Profit reports
4. Purchase reports
5. Expense reports
6. inventory loss
7. Thaka reports
8. permission-safe projections

## Phase 5 - Reliability platform

1. backup service
2. encryption/key wrapping
3. backup verification
4. retention
5. restore journal
6. Worker scheduling
7. cloud upload/update checks
8. diagnostics/health/support bundle

## Phase 6 - Final forensic gate

- clean Release build
- all unit tests green
- all integration tests green
- model drift zero
- fresh install pass
- rollback/reapply pass
- concurrency tests pass
- security tests pass
- restore rehearsal pass
- backup verification pass
- no demo business authority remains in backend path

---

# 31. Final Audit Conclusion

The backend should not be described simply as "unfinished."

The accurate description is:

```text
Core commerce transaction engine:
    advanced and largely correct

Inventory/cost/provenance:
    strong

Sales/Purchasing:
    strong

PostgreSQL model:
    strong

Migration discipline:
    strong

Security authority:
    not implemented

Remaining V1 business modules:
    materially incomplete

Operational reliability platform:
    mostly not implemented

Overall complete V1 production readiness:
    not yet achieved
```

The most technically difficult commerce problems, including landed cost, multi-unit conversion, serialized identity, immutable cost provenance, discounted partial returns, Purchase Return dual values, Purchase Void history safety, idempotency, deterministic locking, and migration correctness, are implemented or substantially implemented.

The next work should not redesign Sales/Purchasing.

Correct next direction:

```text
Fix current Dapper read defects
-> harden tests/concurrency
-> implement Identity/Permissions + License/System State
-> implement missing V1 modules
-> implement reporting
-> implement Worker/Backup/Diagnostics
-> final production forensic gate
```

---

# 32. Audit Status Declaration

```text
Architecture Design                 94/100
Core Commerce Kernel               88/100
Complete V1 Backend                66/100
Production Readiness               55/100

Infrastructure Build               PASS, 0 warnings / 0 errors
Unit Tests                         PASS, 126/126
Integration Tests                  8/10 current
Migration Up/Down/Up               PASS
EF Model Drift                     ZERO
Real PostgreSQL 18 Verification    PASS except 2 read mappings

Production Release Approval        NOT YET
Commerce Kernel Continuation       APPROVED
Major Redesign Required            NO
Targeted Closure Required          YES
```
