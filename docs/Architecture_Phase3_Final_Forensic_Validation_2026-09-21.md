# Edge Retails - Phase 3 Independent Final Forensic Validation

**Date:** 2026-09-21  
**Status:** IN PROGRESS  
**Purpose:** Independent repository/build/test validation against the frozen canonical architecture.  
**Canonical architecture:** `docs/Edge_Retails_Final_Architecture_Report_v1.md`  
**Phase 3 entry SHA-256:** `18A8197C85FEAC91F01D2B67163E82B0659A1D73E18456383C88B39439F7FCDC`

---

# 1. Phase 3 Rules

Phase 3 is a proof/freeze pass, not a redesign pass.

Evidence states:

```text
PASS
= physically executed or directly verified repository evidence satisfies the requirement.

FAIL
= physically verified repository/code/schema state contradicts the canonical requirement.

NOT PROVEN
= required runtime/integration evidence could not be executed or completed safely.

POTENTIAL DRIFT
= physical state appears inconsistent with the canonical policy but requires one remaining contextual fact before final FAIL classification.
```

No runtime PASS is inferred from architecture prose.

---

# 2. Entry Baseline

Canonical SHA-256 at Phase 3 start:

`18A8197C85FEAC91F01D2B67163E82B0659A1D73E18456383C88B39439F7FCDC`

Root pointer SHA-256:

`7FE68B0028D5FF836C5B6626AE04DCD76313EF9B1E9C14AA6211BB2DE7FF7498`

Root pointer remains pointer-only.

Working tree contains extensive existing frontend/backend implementation work and architecture-history moves. Phase 3 does not reset, clean, commit, or discard that work.

---

# 3. Executed Physical Evidence

## P3-BUILD-001 - Release Build

Command:

`dotnet build EdgeRetails.sln -c Release --no-restore`

Result:

```text
Build succeeded.
0 Warning(s)
0 Error(s)
```

**Status:** PASS

## P3-UNIT-001 - Unit Tests

Command:

`dotnet test tests/EdgeRetails.UnitTests/EdgeRetails.UnitTests.csproj -c Release --no-build`

Result:

```text
Passed: 247
Failed: 0
Skipped: 0
Total: 247
```

**Status:** PASS

## P3-STATIC-001 - Existing Static Architecture Audit

`scripts/Verify-Sprint8Architecture.ps1`

Result:

`Sprint 8 static architecture audit PASS`

**Status:** PASS, but scope is limited and does not prove current Sections 209-225 implementation alignment.

## P3-EF-001 - EF Model / Snapshot Drift

`scripts/Verify-EfModelSync.ps1`

Result:

`No changes have been made to the model since the last migration.`

**Status:** PASS

## P3-PG-001 - Local PostgreSQL Tooling

Physical evidence:

```text
PostgreSQL service: postgresql-x64-18 RUNNING
pg_dump:    18.6
pg_restore: 18.6
psql:       18.6
createdb:   18.6
```

**Status:** PASS

## P3-INTEG-001 - PostgreSQL Integration Suite

Executed integration assembly:

```text
Total:   12
Passed:   1
Failed:  11
Skipped:  0
```

The 11 failures occurred before business assertions because the authorized isolated PostgreSQL test environment was not configured:

- `EDGE_RETAILS_TEST_DB` missing;
- backup/restore database environment variables missing;
- destructive cutover safety gate intentionally not armed.

**Status:** NOT PROVEN

These failures are not classified as business-rule failures at this stage.

---

# 4. Phase 3 Repository Drift Findings

## P3-MIG-001 - Fresh Baseline Policy vs Physical Migration Chain

Canonical Sections 167, 190, and 227 keep the fresh `InitialProductionBaseline` strategy authoritative while no production database requires preservation of an older migration chain.

Physical migration chain:

```text
20260920094824_InitialProductionBaseline
20260920111318_Sprint7Phase1SetupIdentity
20260920164958_Sprint7ProductionCutover
20260921101001_Sprint8CanonicalReportingSchema
```

EF model/snapshot drift is zero, but the physical chain contains three migrations after the baseline.

**Status:** POTENTIAL DRIFT

Final classification requires verifying whether any production database authority now requires preservation of this migration chain. If no production database requires it, this is a canonical fresh-baseline policy violation.

---

## P3-IMP-TRACK-001 - Dealer / SupplierProduct / TrackingCode System Missing

Direct source scan under `src/` returned zero matches for:

```text
DealerCode
SupplierProduct
ItemSequence
TrackingCode
supplier_products
dealer_code
item_sequence
tracking_code
```

The EF model snapshot also returned zero matches for the corresponding canonical schema concepts.

Current `InventoryUnit` contains Serial/IMEI and source references but no SupplierProductId, ItemSequence, or TrackingCode.

**Canonical conflict:** Sections 170-191 and related Section 232 tests.

**Severity:** P0 implementation-alignment blocker  
**Status:** FAIL

---

## P3-IMP-KHATA-001 - Supplier Khata Module Missing

Direct source/schema scan returned zero matches for:

```text
SupplierAccountEntry
SupplierPayment
SupplierRefund
SupplierAccount
supplier_account_entries
supplier_payments
supplier_refunds
```

No canonical Supplier Khata domain/account structures were found in the current source tree or EF model snapshot.

**Canonical conflict:** Sections 215-220, 225, 227 and Section 232 Supplier Khata/drift coverage.

**Severity:** P0 implementation-alignment blocker  
**Status:** FAIL

---

## P3-IMP-POSDRAFT-001 - POS Draft Aggregate Missing

Direct source/schema scan returned zero matches for:

```text
PosDraft
pos_drafts
```

**Canonical conflict:** Sections 211 and 227.

**Severity:** P1 implementation-alignment blocker  
**Status:** FAIL

---

## P3-WAR-001 - Prohibited Service-Center Custody Still Implemented

Physical domain enum contains:

`WarrantyCustody.WithServiceCenter`

Canonical hardening explicitly limits V1 custody to:

```text
WITH_CUSTOMER
WITH_SHOP
WITH_SUPPLIER
```

and Hardening 8 regression requires active `WITH_SERVICE_CENTER` residue to remain zero.

**Severity:** P0 canonical-state mismatch  
**Status:** FAIL

---

## P3-WAR-002 - Warranty Financial Recovery Still Deferred in Code

Current Warranty handler rejects `Credited` / `Refunded` supplier recovery with:

`Supplier credit/refund recovery requires the deferred supplier accounting module.`

Canonical architecture makes Supplier Khata final and defines Warranty monetary credit through `WARRANTY_CREDIT / DECREASE_PAYABLE`.

**Severity:** P0 implementation-alignment blocker  
**Status:** FAIL

---

## P3-WAR-003 - Warranty Replacement Does Not Use Canonical TrackingCode Authority

Current serialized replacement path creates a replacement `InventoryUnit` with Serial/IMEI/cost/provenance fields, but the repository has no SupplierProduct sequence, ItemSequence, or TrackingCode implementation.

Therefore the canonical Warranty replacement identity path cannot currently be satisfied.

**Severity:** P0 implementation-alignment blocker  
**Status:** FAIL

---

## P3-NAV-001 - Navigation Contract Not Aligned

Physical navigation evidence:

- `NavigationTarget.NewSale` is active;
- sidebar displays `New Sale`;
- user-facing `POS` title has zero matches in the navigation scan;
- `NavigationTarget.Warranty` absent;
- dedicated Warranty view/target absent;
- Product Management navigation target absent.

Current `NavigationTarget` enum contains the older compact target set and the sidebar contains 11 primary release navigation items.

Canonical Section 209 requires the revised 19-full-screen contract and replaces New Sale naming with POS.

**Severity:** P1 implementation-alignment blocker  
**Status:** FAIL

---

# 5. Current Phase 3 Gate State

```text
Release build                         PASS
Unit tests 247/247                    PASS
Existing static architecture audit    PASS (limited scope)
EF model/snapshot drift               PASS
PostgreSQL service/client readiness   PASS

PostgreSQL integration suite          NOT PROVEN
Fresh-baseline chain policy           POTENTIAL DRIFT

TrackingCode/SupplierProduct system   FAIL
Supplier Khata                        FAIL
POS Draft aggregate                   FAIL
Warranty custody contract             FAIL
Warranty monetary credit              FAIL
Warranty replacement TrackingCode     FAIL
Navigation 19-screen/POS contract     FAIL
```

Phase 3 cannot be frozen/closed while these P0/P1 implementation-alignment failures remain.

Canonical architecture itself remains frozen at the Phase 3 entry hash unless independent evidence discovers a true architecture contradiction.

---

# 6. Next Phase 3 Validation Work

Next evidence pass must:

1. verify production-database migration-history authority and finalize P3-MIG-001;
2. inspect canonical TrackingCode/SupplierProduct implementation requirements against repositories/configurations/migrations;
3. inspect Supplier Khata permission, schema, concurrency, cash, and audit requirements;
4. inspect Warranty eligibility, active-unit uniqueness, transition matrix, supplier provenance, replacement, and monetary-credit implementation;
5. inspect frontend/navigation against Section 209 in detail;
6. configure an authorized disposable PostgreSQL test database and execute the real integration/migration rehearsal;
7. run architecture/code drift checks against the corrected implementation;
8. rerun release build, unit tests, integration tests, EF drift, and final architecture documentary regression;
9. freeze only when open P0/P1 implementation-alignment findings are zero.

**Phase 3 status:** STARTED - NOT CLOSED

---

# 7. Second-Pass Forensic Verification

This section independently re-verifies the Phase 3 kickoff findings using multiple repository layers rather than relying on a single text search.

Verification layers used:

```text
Domain models
DbContext DbSets
EF configurations
EF model snapshot
EF migration source
Application handlers
Infrastructure services
Desktop navigation
Release/closure scripts
Unit-test inventory
PostgreSQL integration-test inventory
Executed build/tests
Executed behavior probes
```

Verdict vocabulary:

```text
CONFIRMED FAIL
= at least two independent physical evidence layers contradict the canonical contract.

PARTIAL PASS
= meaningful implementation exists, but one or more canonical acceptance requirements remain unproven/incomplete.

NOT PROVEN
= safe execution evidence is unavailable.

RETRACTED
= prior finding was a false positive.
```

No kickoff FAIL was retracted.

## 7.1 Re-verified Kickoff Findings

| Finding | Second-pass evidence | Verdict |
|---|---|---|
| P3-IMP-TRACK-001 TrackingCode/SupplierProduct | zero domain/source matches; no DbSets; no EF config; zero model-snapshot fields/tables; Purchase handler creates InventoryUnit using Serial/IMEI only | CONFIRMED FAIL |
| P3-IMP-KHATA-001 Supplier Khata | zero SupplierAccountEntry/Payment/Refund entities; no DbSets; Finance models only CashSession/CashMovement; no finance supplier-account configuration; snapshot has no supplier-account tables | CONFIRMED FAIL |
| P3-IMP-POSDRAFT-001 POS Draft | zero source/model-snapshot PosDraft/pos_drafts; no DbSet/handler | CONFIRMED FAIL |
| P3-WAR-001 service-center custody | `WarrantyCustody.WithServiceCenter = 4` physically present; canonical permits only customer/shop/supplier | CONFIRMED FAIL |
| P3-WAR-002 monetary credit | Warranty handler explicitly rejects Credited/Refunded and says deferred supplier-accounting module is required; Supplier Khata module absent | CONFIRMED FAIL |
| P3-WAR-003 replacement TrackingCode | replacement creates InventoryUnit with Serial/IMEI but repository contains no SupplierProduct/ItemSequence/TrackingCode authority | CONFIRMED FAIL |
| P3-NAV-001 navigation | NewSale target/title active; POS target/title absent; Warranty target/view absent; Product Management target absent; current sidebar remains old compact set | CONFIRMED FAIL |
| P3-MIG-001 fresh-baseline chain | EF has 4 migrations while canonical retains fresh-baseline rule; model drift is zero; no active `EDGE_RETAILS_DB` runtime config exists to prove production-chain preservation requirement | POTENTIAL DRIFT / NOT PROVEN |

## 7.2 P3-H1-001 - PostgreSQL Durability Startup Gate Is Not Implemented End-to-End

Canonical Hardening 1 requires startup write-safety evidence for recovery/read-only/durability state.

Repository scan found zero canonical durability checks for:

```text
pg_is_in_recovery
transaction_read_only
fsync
full_page_writes
synchronous_commit policy
data_checksums
PrimaryWritable
schema/durability write-safety contract
```

A production-facing `NpgsqlDatabaseReadinessProbe` exists, but it checks only:

```text
connection opens
current_database()
server_version
expected database identity
latency
```

The actual Desktop backend startup path uses `EfDatabaseReadinessService`, which checks:

```text
Database.CanConnectAsync()
GetPendingMigrationsAsync()
```

The stronger `ProductionStartupCoordinator` exists in source, but a repository-wide source search found no construction/use site outside its own definition.

**Severity:** P0  
**Verdict:** CONFIRMED FAIL

## 7.3 P3-H3-001 - Durable Print Atomicity Contract Does Not Match Canonical H3

Printing has meaningful production hardening:

```text
durable-ish job abstraction
retry mode
reprint mode
OutcomeUnknown state
succeeded-job idempotent retry protection
physical printer capability checks
```

However, the persistence implementation is:

`JsonPrintJobStore`

not PostgreSQL `system.print_requests`.

A new ORIGINAL print job is created inside `PrintDocumentHandler.CreateJobAsync()` when printing is requested, not atomically inside the originating Sale/Purchase/Warranty database transaction.

Therefore the canonical order:

```text
business mutation
+ ORIGINAL print request
same PostgreSQL transaction
COMMIT
then dispatch
```

is not physically implemented.

There is a crash window where the business transaction can commit before the JSON print job exists.

The repository also has zero `print_requests`, `LogicalArtifactKey`, or `OriginalPrintRequestId` schema/source matches.

**Severity:** P0 reliability/alignment blocker  
**Verdict:** CONFIRMED FAIL

## 7.4 P3-H4-001 - Exact-Unit Pre-Rounding Validation Loophole

Current `ProductUnit.ToBaseQuantity()` performs:

```text
baseQuantity = RoundQuantity(enteredQuantity * FactorToBaseUnit)
then
IsWhole(baseQuantity)
```

Canonical H4 requires whole-unit validation on the exact converted decimal before quantity-scale rounding.

Executed proof:

```text
EXACT=4.0000004
ROUNDED=4.000000
CURRENT_IS_WHOLE=True
EXACT_IS_WHOLE=False
```

Thus a tiny fractional exact-unit result can be legalized by 6-decimal rounding.

Existing unit-test inventory contains no tests named/matched for:
- `1.10`;
- `1.25`;
- `ToBaseQuantity`;
- `serialized_whole_quantity`.

The obvious canonical examples are therefore not currently covered by named regression tests, and the edge rounding loophole is physically demonstrable.

**Severity:** P0 exact-identity correctness blocker  
**Verdict:** CONFIRMED FAIL

## 7.5 P3-WAR-004 - Customer Warranty Eligibility Validation Is Incomplete

`CreateWarrantyClaimHandler` physically validates:

```text
CustomerId is non-empty
items exist
Product exists
quantity positive
fault description present
serialized identity count matches rounded quantity
```

But it has no Sales repository dependency and repository scan shows:

```text
GetSale: 0 in WarrantyHandlers.cs
AuthorizeAsync: 0
PermissionKeys: 0
```

`OriginalSaleId` and `WarrantyValidUntil` are accepted and persisted but not validated against authoritative Sale/SaleItem truth during claim creation.

The handler also accepts `OriginalInventoryUnitId` references without validating original customer/sale/provenance/eligible current state at claim creation.

Canonical claim eligibility requires those cross-checks.

**Severity:** P0  
**Verdict:** CONFIRMED FAIL

## 7.6 P3-WAR-005 - One Active Exact-Unit Claim Barrier Is Missing

EF configuration for `WarrantyClaimItemUnit.OriginalInventoryUnitId` creates a filtered index, but it is not unique.

Migration evidence shows:

`ix_claim_item_units_original_inventory_unit_id`

with filter:

`original_inventory_unit_id IS NOT NULL`

but no unique constraint.

`CreateWarrantyClaimHandler` does not query for an existing active claim for the same InventoryUnit.

Therefore both application and schema layers lack the canonical one-active-exact-unit Warranty barrier.

**Severity:** P0  
**Verdict:** CONFIRMED FAIL

## 7.7 P3-WAR-006 - Warranty Transition Matrix Is Too Permissive

Physical domain methods:

- `SendToSupplier()` rejects only Closed/Cancelled;
- `MarkReadyForCustomer()` rejects only Closed/Cancelled;
- `Handover()` correctly requires ReadyForCustomer.

This means, for example, SendToSupplier can be called from states beyond the canonical allowed source transition, and MarkReadyForCustomer can skip required intermediate states.

Canonical state machine requires explicit transition legality.

**Severity:** P0/P1 state-integrity blocker  
**Verdict:** CONFIRMED FAIL

## 7.8 P3-H5-001 - Large-Dataset Hardening Is Partially Implemented

Positive evidence:

- bounded `PageSize` is present and clamped in multiple query handlers;
- Sales history uses `CompletedAt DESC, Id DESC`;
- Purchase history uses `PurchaseDate DESC, Id DESC`;
- Purchase Return history uses `CreatedAt DESC, Id DESC`;
- queries use `LIMIT @PageSize`;
- keyset-style before-date + Id predicates are implemented;
- cancellation tokens are propagated into Dapper commands;
- DataGrid global style enables row + column virtualization;
- virtualization mode is Recycling.

Therefore core Sales/Purchase/Inventory read paths substantially match Hardening 5.

Remaining evidence gaps:

- no representative Section 76.4 benchmark execution was found/run;
- no p50/p95/p99 evidence was produced;
- Supplier Khata/Warranty/Audit canonical large-data surfaces cannot be fully verified because some are absent/incomplete;
- no full WPF memory/working-set regression was executed.

**Verdict:** PARTIAL PASS; RUNTIME BENCHMARK EVIDENCE NOT PROVEN

## 7.9 P3-H6-001 - LAN Connectivity State Contract Is Not Implemented

Source scan found:

```text
Reconnecting: 0
ConnectivityState: 0
UnknownOutcome: 0
RESOLUTION_REQUIRED: 0
offline write: 0
shadow stock: 0
```

The only `ConnectionState` matches are ADO.NET database connection-state checks.

`ClientOperationId` support is widespread and is a positive idempotency foundation.

Desktop exposes a simple `IsOnline` indicator, but no canonical:

```text
CONNECTED
DEGRADED
RECONNECTING
DISCONNECTED
```

business-command state machine or ambiguous-response resolution workflow was found.

**Severity:** P1 multi-terminal/LAN alignment blocker  
**Verdict:** CONFIRMED FAIL with partial idempotency foundation present

## 7.10 P3-H7-001 - Architecture Drift Gate Is Only Partially Enforced

Positive evidence:

`Invoke-Sprint8Closure.ps1` runs:

```text
restore
format verification
Debug build/test
Release build/test
EF pending-model-change check
Sprint verification scripts
installer preservation
static architecture audit
```

Therefore release-gate infrastructure is real.

However:

- no `.github` CI workflow exists in the repository;
- current static architecture audit does not verify DealerCode/SupplierProduct/TrackingCode, Supplier Khata, Warranty custody/transition, 19-screen navigation, or H1 durability state;
- unit-test list contains no Warranty, Tracking, Khata, Reconnect, or Pagination named tests;
- Phase-4 closure script hard-codes `ProductionScreenCount = 17`, which is stale against canonical 19-screen truth;
- current static architecture audit still checks only that Sprint 8 does not create a `Screen18`, not the canonical 19-screen navigation contract.

Therefore current release tooling can report green while major canonical drift remains.

**Severity:** P0/P1 governance blocker  
**Verdict:** CONFIRMED FAIL for current canonical drift enforcement; PARTIAL PASS for generic build/release gating

## 7.11 H8 Stale-Claim Quarantine Physical Check

Canonical document correctly quarantines stale claims.

Physical implementation/release evidence still contains stale implementation-era truth, notably:

`ProductionScreenCount = 17`

inside `Invoke-Sprint8Phase4Closure.ps1`.

This does not override canonical architecture, but proves why the H8 evidence-separation rule is necessary.

**Verdict:** canonical quarantine PASS; physical implementation drift CONFIRMED

# 8. Forensic Verification Matrix After Pass 2

```text
CANONICAL ARCHITECTURE HASH      PASS / unchanged
Release build                    PASS, 0 warnings / 0 errors
Unit tests                       PASS, 247 / 247
EF model snapshot drift          PASS
PostgreSQL service/tooling       PASS
PostgreSQL integration behavior  NOT PROVEN (authorized test DB env absent)
Fresh-baseline migration policy  POTENTIAL DRIFT / NOT PROVEN

H1 durability/write-safety gate  CONFIRMED FAIL
H2 SupplierProduct sequence      CONFIRMED FAIL because canonical subsystem absent
H3 print atomicity/outbox        CONFIRMED FAIL
H4 exact pre-round unit check    CONFIRMED FAIL
H5 bounded reads/virtualization  PARTIAL PASS; benchmark NOT PROVEN
H6 LAN connectivity contract     CONFIRMED FAIL
H7 canonical drift gate          CONFIRMED FAIL / generic gate PARTIAL PASS

TrackingCode/SupplierProduct     CONFIRMED FAIL
Supplier Khata                   CONFIRMED FAIL
POS Draft                        CONFIRMED FAIL
Warranty service-center custody  CONFIRMED FAIL
Warranty eligibility             CONFIRMED FAIL
Warranty active-unit barrier     CONFIRMED FAIL
Warranty transition matrix       CONFIRMED FAIL
Warranty monetary credit         CONFIRMED FAIL
Warranty replacement identity    CONFIRMED FAIL
Navigation 19-screen/POS         CONFIRMED FAIL
```

# 9. Second-Pass Conclusion

The initial Phase 3 report is validated and was conservative.

No kickoff implementation blocker was disproved.

The second pass found additional P0/P1 implementation-alignment defects that were not fully enumerated in the kickoff report, especially:
- missing PostgreSQL durability/write-safety startup gate;
- non-atomic JSON print-job persistence;
- exact-unit rounding loophole;
- incomplete Warranty eligibility;
- missing one-active-unit Warranty uniqueness;
- over-permissive Warranty transitions;
- missing LAN connectivity-state workflow;
- insufficient canonical drift enforcement in current release scripts.

The canonical architecture remains internally frozen and unchanged.

**Phase 3 forensic verification status:** CONFIRMED - IMPLEMENTATION REMEDIATION REQUIRED BEFORE FREEZE
