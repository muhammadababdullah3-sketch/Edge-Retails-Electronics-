# Edge Retails Architecture Supplementary Hardening Register
## Hardening 0 - Baseline and Finding Classification

**Date:** 2026-09-21  
**Status:** NON-AUTHORITATIVE WORKING REGISTER  
**Purpose:** Classify newly reported architecture/production risks before any further canonical architecture change.

---

# 1. Authority and Safety Boundary

Canonical architecture remains:

`docs/Edge_Retails_Final_Architecture_Report_v1.md`

Root pointer remains:

`EDGE_RETAILS_BACKEND_ARCHITECTURE_FINAL.md`

This register is **not** architecture authority.

Hardening 0 does not change:
- backend business rules;
- canonical architecture sections;
- migrations;
- backend/runtime code;
- frontend implementation;
- archive/history files.

Any later architecture correction must be made deliberately in the canonical file and revalidated.

---

# 2. Hardening 0 Baseline

Canonical SHA-256 at Hardening 0 entry:

`BEB18E342FBD07D6D473F0774355EF5FB75536FD1C75EF2FDD5DC98B61B733C4`

Expected canonical coverage:

`Sections 0-233`

Phase state at entry:

```text
Phase 0                         COMPLETE
Phase 1                         COMPLETE
Phase 2                         COMPLETE
Phase 2 Supplementary Hardening IN PROGRESS
Phase 3                         NOT STARTED
```

Hardening 0 rule:

> Classification first. No finding is promoted into canonical architecture merely because it appeared in a forensic/audit note.

---

# 3. Classification Vocabulary

```text
REAL_ARCHITECTURE_GAP
ARCHITECTURE_ALREADY_COVERS
IMPLEMENTATION_REQUIREMENT
PERFORMANCE_HARDENING
STALE_AUDIT_FINDING
PHASE3_VALIDATION_ONLY
```

A finding may carry more than one classification where the architecture rule already exists but needs measurable implementation/validation coverage.

---

# 4. Supplementary Finding Register

## SH-DB-001 - Local PostgreSQL Durability / Crash-Recovery Contract

**Source concern:** Sudden power loss, unsafe shutdown, local-node corruption/recovery risk.

**Classification:** REAL_ARCHITECTURE_GAP

**Current assessment:**  
Backup/restore, integrity, recovery state, and reconciliation are already architected, but the canonical document does not yet explicitly lock a production PostgreSQL durability profile and startup write-safety contract.

**Required hardening:**  
Hardening 1 must define:
- authoritative transaction durability requirements;
- unsafe durability tuning that is forbidden;
- startup database health/recovery gate;
- disk-space and recovery-required behavior;
- crash/restart validation;
- separation of database-integrity failures from non-blocking device/printer failures.

**Status:** ARCHITECTURE HARDENING CLOSED IN H10 - runtime/implementation evidence pending Phase 3

---

## SH-CON-001 - SupplierProduct Sequence Lock Contention

**Source concern:** Concurrent receipts for the same Supplier + Product may queue on the sequence authority.

**Classification:** PERFORMANCE_HARDENING

**Current assessment:**  
Correctness is already protected by one SupplierProduct sequence authority, deterministic locking, range reservation, monotonic BIGINT sequence, and non-overlapping concurrency tests. Contention is therefore primarily a transaction-duration/operational-performance concern, not a TrackingCode correctness defect.

**Required hardening:**  
Hardening 2 must define:
- minimum lock-holding critical section;
- no printer/network/cloud/user I/O while authoritative locks are held;
- bounded timeout behavior;
- idempotent retry rules;
- lock-wait/transaction-duration metrics;
- concurrency stress acceptance tests.

A universal hard-coded target such as "<50 ms" must not become a business invariant without benchmark evidence.

**Status:** ARCHITECTURE HARDENING CLOSED IN H10 - runtime/implementation evidence pending Phase 3

---

## SH-PRN-001 - Receipt / Label Failure After Commit

**Source concern:** Database commit succeeds but receipt or TrackingCode label printing fails.

**Classification:** ARCHITECTURE_ALREADY_COVERS + IMPLEMENTATION_REQUIREMENT

**Current assessment:**  
Canonical architecture already establishes:
- Sale/Purchase commit is not rolled back by printer failure;
- TrackingCodes/InventoryUnits are created inside the transaction;
- labels are not printed before successful commit;
- durable post-commit/outbox-style print delivery is the approved crash-safe pattern.

**Required hardening:**  
Hardening 3 must make delivery/reprint semantics operationally testable, including:
- durable print job identity/state;
- retry vs action-required states;
- reprint from original committed identity/snapshot;
- no new TrackingCode on reprint;
- no inventory/financial mutation from printing.

**Status:** ARCHITECTURE HARDENING CLOSED IN H10 - runtime/implementation evidence pending Phase 3

---

## SH-UNIT-001 - Fractional Base Quantity for Individually Tracked Products

**Source concern:** Fractional packaging conversion may mathematically produce a fractional exact physical unit.

**Classification:** ARCHITECTURE_ALREADY_COVERS + PHASE3_VALIDATION_ONLY

**Current assessment:**  
Canonical architecture already states:

`For individually tracked products, BaseQuantity must be a whole count.`

QUANTITY/LENGTH products continue lot/balance provenance without artificial per-subunit InventoryUnits.

**Required hardening:**  
Hardening 4 must lock explicit negative/positive tests and frontend input behavior without changing the underlying business rule.

**Status:** ARCHITECTURE HARDENING CLOSED IN H10 - runtime/implementation evidence pending Phase 3

---

## SH-PERF-001 - Large Dataset Query / WPF Rendering Pressure

**Source concern:** Large Product, InventoryUnit, history, and ledger datasets may cause memory/UI/query degradation.

**Classification:** ARCHITECTURE_ALREADY_COVERS + PERFORMANCE_HARDENING + IMPLEMENTATION_REQUIREMENT

**Current assessment:**  
Canonical architecture already requires:
- targeted PostgreSQL indexes;
- server-side filtering and pagination;
- keyset/seek pagination for deep history where appropriate;
- database-side aggregation with small report DTOs.

The forensic recommendation to use OFFSET/LIMIT for all deep history is not authoritative; deep pagination may require seek/keyset behavior.

**Required hardening:**  
Hardening 5 must define:
- no unbounded history loading;
- paging/filter/sort contracts;
- deep-history seek/keyset usage;
- WPF virtualization/recycling as implementation requirements;
- representative benchmark datasets and measurable p50/p95/p99/memory evidence.

**Status:** ARCHITECTURE HARDENING CLOSED IN H10 - runtime/implementation evidence pending Phase 3

---

## SH-LAN-001 - Multi-Till Disconnect / Split-Brain Risk

**Source concern:** A LAN client loses connection to the authoritative server/database.

**Classification:** ARCHITECTURE_ALREADY_COVERS + PHASE3_VALIDATION_ONLY

**Current assessment:**  
Canonical architecture intentionally forbids authoritative offline writes for the first LAN release. A disconnected terminal may preserve an unsaved local cart but cannot complete Sale, Purchase, Thaka, Payment, or Stock Adjustment until authoritative connectivity returns.

This is an intentional consistency trade-off, not an unresolved split-brain defect.

**Required hardening:**  
Hardening 6 must specify terminal connectivity states, ambiguous-response retry behavior, revalidation after reconnect, and regression tests proving no shadow/offline stock authority is created.

**Status:** ARCHITECTURE HARDENING CLOSED IN H10 - runtime/implementation evidence pending Phase 3

---

## SH-GOV-001 - Architecture / Code Drift

**Source concern:** A large architecture document may diverge from physical implementation over time.

**Classification:** IMPLEMENTATION_REQUIREMENT + PHASE3_VALIDATION_ONLY

**Current assessment:**  
Canonical architecture already requires Architecture Tests, Migration Tests, PostgreSQL integration tests, model/migration drift checks, and release gates.

However, an architecture document cannot prove that every such test physically exists and is wired into CI.

**Required hardening:**  
Hardening 7 must define a formal architecture-to-code CI/release gate. Phase 3/implementation inspection must verify physical evidence rather than trusting documentation claims.

**Status:** ARCHITECTURE HARDENING CLOSED IN H10 - runtime/implementation evidence pending Phase 3

---

# 5. Stale / Incorrect Audit Claims Locked Out of Canonical Authority

## SA-001 - TrackingCode Sequence Stops at 999999

**Classification:** STALE_AUDIT_FINDING

**Canonical truth:**  
`ItemSequence` is BIGINT, uses minimum six-digit display, grows beyond 999999, and never reuses a committed value.

Canonical regression example:

`1000000 -> 1000000`

**Status:** QUARANTINED + REGRESSION-LOCKED IN HARDENING 8

---

## SA-002 - Current Navigation Has 17 Screens

**Classification:** STALE_AUDIT_FINDING

**Canonical truth:**  
Current V1 navigation contains exactly **19 full screens**.

**Status:** QUARANTINED + REGRESSION-LOCKED IN HARDENING 8

---

## SA-003 - Dedicated Warranty Is Screen 18

**Classification:** STALE_AUDIT_FINDING

**Canonical truth:**  
In the current Section 209 ordering, Warranty is full screen **#17**.

**Status:** QUARANTINED + REGRESSION-LOCKED IN HARDENING 8

---

## SA-004 - Current Lock Hierarchy Has 8 Levels

**Classification:** STALE_AUDIT_FINDING

**Canonical truth:**  
Current Section 185 defines one deterministic logical order with **10 levels**, including WarrantyClaim, SupplierAccount, exact-unit/identity, row-lock, and CashSession families.

**Status:** QUARANTINED + REGRESSION-LOCKED IN HARDENING 8

---

## SA-005 - inventory.inventory_lots Is the Current Canonical Table Name

**Classification:** STALE_AUDIT_FINDING

**Canonical truth:**  
Current physical schema name is:

`inventory.lots`

The architecture explicitly rejects inventing `inventory.inventory_lots` as a parallel/current table.

**Status:** QUARANTINED + REGRESSION-LOCKED IN HARDENING 8

---

## SA-006 - "Architecture Tests Are Already Written" Is Proven by the Architecture Document

**Classification:** PHASE3_VALIDATION_ONLY

**Canonical truth:**  
The architecture requires Architecture Tests, but existence/coverage in the physical repository must be verified separately.

**Status:** EVIDENCE-GATED + REGRESSION-LOCKED IN HARDENING 8

---

## SA-007 - Sprint / Codebase Completion Claims Are Architecture Authority

**Classification:** PHASE3_VALIDATION_ONLY

**Canonical truth:**  
Sprint completion and physical implementation status are repository/workspace facts, not canonical business-rule authority. They must be verified from code/build/tests when relevant.

**Status:** EVIDENCE-GATED + REGRESSION-LOCKED IN HARDENING 8

---

# 6. Hardening Execution Map

```text
Hardening 1 -> PostgreSQL durability + crash-recovery contract
Hardening 2 -> SupplierProduct lock-contention / transaction-duration hardening
Hardening 3 -> durable receipt/label delivery + reprint semantics
Hardening 4 -> exact-unit fractional-quantity validation matrix/tests
Hardening 5 -> large-dataset query/UI performance acceptance contract
Hardening 6 -> LAN disconnect / ambiguous-response / reconnect contract
Hardening 7 -> architecture-to-code CI/drift gate
Hardening 8 -> stale audit finding quarantine/correction
Hardening 9 -> supplementary regression matrix
Hardening 10 -> supplementary closure + new canonical hash
Phase 3      -> independent final forensic validation and freeze
```

---

# 7. Hardening 0 Exit Criteria

Hardening 0 is complete only when:

- current canonical SHA-256 is recorded;
- no canonical business rule was changed;
- every reported weak point is classified;
- stale audit claims are explicitly quarantined;
- each genuine concern has an assigned later hardening step;
- Phase 3 remains not started.

---

# 8. Hardening 0 Result

**Canonical architecture changes:** NONE  
**Backend/runtime changes:** NONE  
**Migration changes:** NONE  
**Frontend changes:** NONE  
**Archive/history changes:** NONE

**Hardening 0 conclusion:**

`BASELINE LOCKED + FINDINGS CLASSIFIED + STALE CLAIMS QUARANTINED`

Next step:

`Supplementary Hardening 1 - PostgreSQL Durability + Crash-Recovery Contract`

# 9. Hardening 1 Resolution Checkpoint

**Canonical architecture SHA-256 after Hardening 1:** `061587DE38AED1AC12232B90F4F29D0AFD2BA5C9712574AE3DA79F66C46FF078`

Resolved finding:

`SH-DB-001 - Local PostgreSQL Durability / Crash-Recovery Contract`

Canonical additions:

```text
Section 61.1 -> Production PostgreSQL Durability Contract
Section 61.2 -> Startup Database Write-Safety Gate
Section 70.1 -> Database Durability and Recovery Health Evidence
Section 78.1 -> Mandatory Durability / Crash-Recovery Validation
```

Locked architecture rules:
- `fsync = on` for production authoritative durability;
- `full_page_writes = on`;
- authoritative business commits must not use `synchronous_commit = off`;
- authoritative business tables must not use UNLOGGED semantics;
- `wal_sync_method` is platform/storage validated rather than hard-coded;
- new production clusters enable data checksums when supported;
- checksum state changes use controlled maintenance, never silent runtime mutation;
- startup blocks authoritative writes when recovery/integrity/configuration truth is unsafe;
- routine startup does not run a full offline checksum scan;
- warning vs critical disk-space behavior is explicit;
- UPS is recommended operationally but is not a correctness dependency;
- crash/restart/recovery validation is mandatory release evidence.

Validation at Hardening 1 exit:
- numbered sections 0-233 remain unique;
- Markdown fences are balanced and closed;
- Phase-2 stale-term checks remain clean;
- no Unicode/mojibake residue introduced;
- `git diff --check` reports no whitespace errors;
- root pointer body remains untouched by Hardening 1.

**Status:** HARDENING 1 COMPLETE - pending Hardening 9 regression and Phase 3 independent validation

Next:

`Supplementary Hardening 2 - SupplierProduct Sequence Lock Contention`

# 10. Hardening 2 Resolution Checkpoint

**Canonical architecture SHA-256 after Hardening 2:** `48265D7C3CC7E7EA9AB16D289217CA5A7EEC8095B7A5E50218CF632EDAF84E2A`

Resolved finding:

`SH-CON-001 - SupplierProduct Sequence Lock Contention`

Canonical additions:

```text
Section 178.4 -> SupplierProduct Sequence Critical-Section Discipline
Section 185.1 -> Lock-Wait Timeout and Retry Policy
Section 185.2 -> Lock Contention Observability
Section 232 tests 39-48 -> SupplierProduct sequence/concurrency hardening
Section 233 -> SupplierProduct concurrency/timeout/retry gate
```

Locked architecture rules:
- `SupplierProduct.NextItemSequence` remains the only sequence authority;
- one command reserves one contiguous required range per affected SupplierProduct under one lock cycle;
- no per-physical-unit sequence lock acquire/release loop;
- no printer, cloud, unrelated network, filesystem export, user prompt, report rendering, messaging, sleep/backoff, or post-commit printing while authoritative locks are held;
- correctness-critical validation/persistence remains inside the authoritative transaction;
- lock waits are bounded and operation-scoped;
- PostgreSQL `lock_timeout`, where used, is scoped to the operation/transaction rather than blindly configured cluster-wide;
- broader statement/command timeout remains above the lock-wait timeout when both are active;
- `deadlock_timeout` is not an application retry timer;
- transient concurrency retry is whole-operation only, bounded, idempotent, and reuses the same ClientOperationId;
- retry never replays a partial SQL fragment independently;
- ambiguous committed outcome replay resolves to the original effect rather than duplicating business state;
- retry backoff occurs only after authoritative locks are released;
- contention telemetry records lock-wait/transaction duration, retry/outcome, and SupplierProduct/range context without secrets;
- p50/p95/p99 evidence drives operational tuning;
- no universal "<50 ms" business invariant is introduced.

Validation at Hardening 2 exit:
- numbered sections 0-233 remain 234 unique headings;
- no duplicate/missing numbered sections;
- Markdown fences are balanced and closed;
- Phase-2 stale-term checks remain clean;
- no Unicode/mojibake residue introduced;
- `git diff --check` reports no whitespace errors;
- Hardening 1 durability rules remain intact.

**Status:** HARDENING 2 COMPLETE - pending Hardening 9 regression and Phase 3 independent validation

Next:

`Supplementary Hardening 3 - Receipt / Label Printing Reliability`

# 11. Hardening 3 Resolution Checkpoint

**Canonical architecture SHA-256 after Hardening 3:** `F3AF6B2A78F6563521F8AB335D68A0FB79CCE63EE463C4CFE70E9438E34B4303`

Resolved finding:

`SH-PRN-001 - Receipt / Label Failure After Commit`

Canonical additions / hardening:

```text
Section 24 -> Sale receipt points to durable post-commit delivery
Section 35 -> Purchase document points to durable post-commit delivery
Sections 51.1-51.3 -> Durable print delivery, reprint identity safety, payload/recovery
Section 87 schema plan -> system.print_requests durable store
Section 70 -> print delivery health evidence
Section 178.3 -> TrackingCode label printing/reprint tied to committed identity
Section 232 tests 49-62 -> print delivery/reprint/crash regression suite
Section 233 -> durable print-request implementation gate
```

Locked architecture rules:
- printing is delivery, never stock/financial/TrackingCode authority;
- required ORIGINAL auto-print request is persisted atomically in the same PostgreSQL transaction as its source business transaction;
- no printer/device I/O occurs before commit or while authoritative business locks are held;
- rollback removes the uncommitted ORIGINAL print request with the source transaction;
- committed PENDING request survives app restart/crash;
- durable statuses are PENDING, DISPATCHING, PRINTED, FAILED_RETRYABLE, OUTCOME_UNKNOWN, ACTION_REQUIRED, CANCELLED;
- exactly-once physical printing is not claimed where printer protocols cannot prove it;
- only known-safe FAILED_RETRYABLE outcomes may be automatically retried;
- ambiguous dispatch outcome becomes OUTCOME_UNKNOWN and is not blindly auto-reprinted;
- reprint uses the original committed business identity/snapshot;
- receipt reprint does not create/recalculate a Sale or Payment;
- Purchase document reprint has zero Purchase/payable/inventory/cost/cash/Khata mutation;
- TrackingCode/QR reprint reuses the same InventoryUnitId, ItemSequence, and TrackingCode;
- label reprint consumes no new SupplierProduct sequence value and creates no second InventoryUnit;
- ORIGINAL request creation is idempotent through stable logical artifact identity;
- REPRINT request creation has its own idempotent request identity;
- multi-label receipts use a stable per-unit/artifact-part identity;
- restart recovery converts unresolved DISPATCHING to OUTCOME_UNKNOWN unless non-delivery is provable;
- print retry/backoff is outside authoritative business transactions/locks;
- print queue/action-required state is visible through diagnostics/health without exposing secrets.

Validation at Hardening 3 exit:
- numbered sections 0-233 remain 234 unique headings;
- no duplicate/missing numbered sections;
- Markdown fences are balanced and closed;
- Phase-2 stale-term checks remain clean;
- no Unicode/mojibake residue introduced;
- `git diff --check` reports no whitespace errors;
- Hardening 1 durability and Hardening 2 lock/retry rules remain intact.

**Status:** HARDENING 3 COMPLETE - pending Hardening 9 regression and Phase 3 independent validation

Next:

`Supplementary Hardening 4 - Fractional Exact-Unit Validation`

# 12. Hardening 4 Resolution Checkpoint

**Canonical architecture SHA-256 after Hardening 4:** `ED415ED180622298CC6D78B00550884A75B61B0B3DEEB5802DEA22AED0E542A0`

Resolved finding:

`SH-UNIT-001 - Fractional Base Quantity for Individually Tracked Products`

Canonical hardening:

```text
Section 14 -> opening stock exact physical identity wording corrected
Section 107 -> exact-unit conversion contract expanded
Section 140 -> pre-rounding exact whole-unit precision rule
Section 177.3 -> positive whole-count + exact identity cardinality
Section 178.2 -> fractional tracked rejection before SupplierProduct sequence allocation
Section 189 tests 25-31 -> traceability fractional-unit coverage
Section 206 tests 21-24 -> Product-vs-Inventory fractional-unit coverage
Section 232 tests 63-72 -> full exact-unit E2E regression
Section 233 -> exact-unit hardening implementation gate
```

Locked architecture rules:
- SERIALIZED / individually tracked products represent discrete physical objects;
- exact conversion is `EnteredQuantity * FactorToBaseUnit` using decimal/numeric-equivalent arithmetic;
- whole-unit validation occurs before display or persistence rounding;
- tracked exact result must be positive and mathematically integral;
- no floor, ceiling, truncation, nearest rounding, epsilon, or tolerance may legalize a fractional physical result;
- decimal entered packaging quantity may be valid when exact conversion yields a whole physical count;
- `1.25 x 4 = 5` is valid when that entered quantity is allowed;
- `1.10 x 4 = 4.40` is rejected for individually tracked Product;
- invalid tracked fractional receiving is rejected before SupplierProduct sequence reservation;
- exact captured/selected identity count must equal BaseQuantity;
- sequence range size equals exact whole tracked-unit count;
- tracked Sale/Thaka issue requires exact InventoryUnit cardinality;
- tracked positive Stock Adjustment/opening stock requires exact physical identities;
- Serial/IMEI is required only according to Product policy overlay;
- tracked Warranty replacement quantity must be whole physical units;
- QUANTITY/LENGTH may remain fractional under canonical precision policy and use lot/balance provenance;
- backend/domain validation remains authoritative even if UI prevents invalid input early.

Validation at Hardening 4 exit:
- numbered sections 0-233 remain 234 unique headings;
- no duplicate/missing numbered sections;
- Markdown fences are balanced and closed;
- Phase-2 stale-term checks remain clean;
- no Unicode/mojibake residue introduced;
- `git diff --check` reports no whitespace errors;
- Hardening 1 durability, Hardening 2 contention/retry, and Hardening 3 durable-print rules remain intact.

**Status:** HARDENING 4 COMPLETE - pending Hardening 9 regression and Phase 3 independent validation

Next:

`Supplementary Hardening 5 - Large Dataset Performance Contract`

# 13. Hardening 5 Resolution Checkpoint

**Canonical architecture SHA-256 after Hardening 5:** `899519D1D406245B804511350D6E5B5CC982C9109A4EF7D767386FA11781D2DF`

Resolved finding:

`SH-PERF-001 - Large Dataset Query / WPF Rendering Pressure`

Canonical hardening:

```text
Sections 76.1-76.4 -> bounded read-query, pagination, WPF rendering, benchmark contract
Sections 230.1-230.2 -> seek/index alignment + bounded read DTO/count contract
Section 232 tests 73-87 -> large-dataset/read-performance regression suite
Section 233 -> performance evidence implementation gate
```

Locked architecture rules:
- potentially large collections never require unbounded table/ledger loading before filtering/paging;
- PageSize is mandatory/bounded where applicable and clients cannot request an unbounded page;
- filtering, deterministic sorting, and projection occur in PostgreSQL before materialization;
- list/detail reads remain separated and hot lists cannot hydrate full write aggregates/event histories per row;
- N+1 per-row query behavior is forbidden on hot list/history screens;
- exact TotalCount is optional and may use a separate optimized path;
- deep/continuously growing histories prefer keyset/seek pagination where stable keys exist;
- keyset order includes a unique tie-breaker such as timestamp + Id;
- shallow OFFSET/LIMIT remains allowed when measured appropriate, but "all deep history uses OFFSET/LIMIT" is rejected;
- cursor state represents stable sort keys, not UI row number;
- WPF large-list screens require virtualization/recycling or equivalent bounded rendering;
- incremental/page loading keeps active UI collections bounded;
- one ever-growing ObservableCollection for full history is forbidden;
- authoritative large-data filtering/sorting remains server-side;
- search-as-you-type uses suitable debounce/throttle and superseded-read cancellation/versioning;
- stale slower search results cannot overwrite newer UI state;
- large query execution/materialization must not synchronously block the WPF UI thread;
- representative baseline fixture targets the Section 76.4 order of magnitude and is not a capacity ceiling;
- performance evidence records p50/p95/p99, database/query-plan evidence, and WPF memory/working-set behavior;
- PostgreSQL plans are inspected in test/staging/diagnostics rather than instrumenting every production request;
- indexes are added/retained from measured predicate/order/plan evidence, not blind proliferation;
- reports aggregate in PostgreSQL and return bounded DTO/series data.

Validation at Hardening 5 exit:
- numbered sections 0-233 remain 234 unique headings;
- no duplicate/missing numbered sections;
- Markdown fences are balanced and closed;
- Phase-2 stale-term checks remain clean;
- no Unicode/mojibake residue introduced;
- `git diff --check` reports no whitespace errors;
- Hardening 1-4 contracts remain intact.

**Status:** HARDENING 5 COMPLETE - pending Hardening 9 regression and Phase 3 independent validation

Next:

`Supplementary Hardening 6 - LAN Disconnect / Reconnect Contract`

# 14. Hardening 6 Resolution Checkpoint

**Canonical architecture baseline before Hardening 6:** `899519D1D406245B804511350D6E5B5CC982C9109A4EF7D767386FA11781D2DF`

**Canonical architecture SHA-256 after Hardening 6:** `3681CA7676D74516C2EE37C61A5382AF954D927F61CA67BF48B53D62C402C3BB`

Resolved finding:

`SH-LAN-001 - Multi-Till Disconnect / Split-Brain Risk`

Canonical hardening:

```text
Section 82 -> Multi-Terminal Offline / Reconnect Contract
Section 82.1 -> Unknown-Outcome Command Rule
Section 82.2 -> Reconnect Revalidation
Section 82.3 -> Server and Terminal Ownership
Section 225 -> LAN Unknown-Outcome Replay
Section 232 tests 88-102 -> LAN disconnect/reconnect regression suite
Section 233 -> LAN hardening implementation gate
```

Locked architecture rules:
- first LAN release still forbids authoritative offline writes;
- terminal connectivity states are CONNECTED, DEGRADED, RECONNECTING, DISCONNECTED;
- disconnected/reconnecting terminals cannot start authoritative business mutations;
- offline-write prohibition applies across Sales, Purchasing, Supplier Khata, Thaka, Stock, Warranty, Cash, and permanent business identity mutations;
- terminal-local state may preserve unsaved UI intent/cache, but never authoritative stock/payable/cash/warranty/TrackingCode truth;
- connection loss after request dispatch is an ambiguous outcome, not automatic proof of failure;
- unresolved replay preserves the same ClientOperationId/correlation identity;
- replay uses the existing Hardening 2 idempotency/retry engine rather than a second LAN retry/sync engine;
- if the original command already committed, replay resolves to the original effect/result;
- a fresh ClientOperationId represents new intent, not recovery of the unresolved original operation;
- no local compensating/shadow mutation is invented while the outcome is unknown;
- reconnect reloads and revalidates current price, ProductUnit conversion, stock/buckets, exact InventoryUnit state, Supplier balance, CashSession, Warranty state/custody, permissions/session, stocktake/locks, and compatibility where applicable;
- a preserved POS cart is never automatically authoritative after reconnect;
- stale exact units sold/moved by another terminal are rejected by the normal authoritative engine;
- no offline invoice/payment numbering, TrackingCode sequence allocation, Supplier Khata mutation, or later conflict-merge ledger is allowed;
- server remains the single LAN business-command authority;
- terminal-local printing remains delivery-only after central commit.

Validation status:
- runtime/implementation LAN tests are requirements only at this phase and are not claimed as executed yet;
- Hardening 9 / Phase 3 must independently validate these requirements.

**Status:** HARDENING 6 COMPLETE - pending Hardening 9 regression and Phase 3 independent validation

Next:

`Supplementary Hardening 7 - Architecture / Code Drift Gate`

# 15. Hardening 7 Resolution Checkpoint

**Canonical architecture SHA-256 after Hardening 7:** `95EF66C3ABAACD8037801AA85D87761E43A84AA15C7A4AC9F4C5CB7C311A6533`

Resolved finding:

`SH-GOV-001 - Architecture / Code Drift`

Canonical hardening:

```text
Section 78.2 -> Architecture / Code Drift Gate
Section 232 tests 103-117 -> architecture/code-drift regression suite
Section 233 -> architecture/code-drift implementation gate
```

Locked architecture rules:
- architecture alignment is release-enforced, not documentation-only;
- CI/release must cover EF model drift, migration drift, schema constraints, enum/state, permissions, Tracking identity, lock ordering, Khata direction/source uniqueness, Warranty transitions/custody, and navigation contract;
- minimum pipeline includes build, unit tests, architecture tests, EF model-drift check, disposable PostgreSQL migration rehearsal, PostgreSQL integration tests, and release/package validation;
- `dotnet ef migrations has-pending-model-changes` or equivalent must report zero pending drift;
- checked-in migration snapshot must match the compiled model;
- disposable PostgreSQL migration from empty database to current baseline must succeed;
- required Down-to-zero then Up rehearsal must succeed under the project baseline policy;
- canonical schema constraints/indexes must physically exist;
- architecture tests must fail on duplicate TrackingCode sequence authority, mutable Supplier balance authority, duplicate Sale/Purchase/Warranty engines, illegal Warranty transitions, conflicting lock order, stale navigation, or terminal-side LAN database authority;
- intentional canonical invariant changes require architecture + regression test + model/migration update together where applicable;
- a green compile alone is not release evidence.

Validation status:
- architecture requirements are now explicit and machine-checkable in design;
- this hardening does not falsely claim that every required CI test already exists physically;
- Hardening 9 / Phase 3 must inspect actual repository/CI evidence and execute available gates.

**Status:** HARDENING 7 COMPLETE - pending Hardening 9 regression and Phase 3 independent validation

Next:

`Supplementary Hardening 8 - Audit Corrections / Stale-Claim Quarantine`

# 16. Hardening 8 Resolution Checkpoint

**Canonical architecture SHA-256 after Hardening 8:** `3D0193108C6DA201C66FDA61E4D4F80F0D3D230C46B33ED5CD54E8799D583C70`

Resolved scope:

`Audit Corrections / Stale-Claim Quarantine`

Canonical hardening:

```text
Section 0 -> external forensic/audit evidence precedence + implementation-evidence rule
Section 207.1 -> stale audit / forensic claim quarantine matrix
Section 232 tests 118-127 -> stale-claim regression suite
Section 233 -> stale-audit quarantine implementation gate
```

Regression-locked stale claims:
- ItemSequence does not stop at 999999; BIGINT + minimum six-digit display remains authoritative;
- current V1 navigation remains exactly 19 full screens;
- Warranty remains full screen #17 in Section 209 ordering;
- current Section 185 lock order remains 10 logical levels;
- current physical exact-unit/lot table authorities remain `inventory.units` / `inventory.lots`;
- parallel `inventory.inventory_units` / `inventory.inventory_lots` names remain rejected;
- deep history is not universally forced through OFFSET/LIMIT; deterministic keyset/seek remains preferred where appropriate;
- architecture prose does not prove tests physically exist/pass;
- Sprint/code completion claims require current repository/build/test evidence;
- older Warranty/Khata notes cannot override later corrected authority;
- archive/history documents remain context/evidence and cannot silently supersede the canonical authority index;
- newly accepted forensic defects require explicit canonical incorporation plus applicable regression coverage.

Hardening 8 evidence:
- suspicious stale terms were scanned in canonical context;
- wrong table names occur only in explicit negations/quarantine/regression contexts;
- OFFSET/LIMIT occurrences preserve the shallow-vs-deep distinction;
- current Section 185 programmatic lock-order count = 10;
- current Section 209 programmatic full-screen count = 19;
- current Section 209 screen #17 = Warranty;
- `git diff --check` reports no whitespace errors;
- numbered architecture headings remain 0-233 unique and complete;
- Markdown fences remain balanced.

**Status:** HARDENING 8 COMPLETE - pending Hardening 9 regression and Phase 3 independent validation

Next:

`Supplementary Hardening 9 - Full Supplementary Regression Matrix`

# 17. Hardening 9 Resolution Checkpoint

**Canonical architecture SHA-256 after Hardening 9:** `18A8197C85FEAC91F01D2B67163E82B0659A1D73E18456383C88B39439F7FCDC`

Resolved scope:

`Full Supplementary Regression Matrix`

Canonical hardening:

```text
Section 232.1 -> Supplementary Hardening Regression Matrix
Section 233 -> evidence-separation gate for RUNTIME_EVIDENCE_REQUIRED vs RUNTIME_PASS
```

Hardening 9 status semantics:
- `ARCHITECTURE_REGRESSION_PASS` means the canonical rule/test/gate is present and documentary/structural validation passed;
- `RUNTIME_EVIDENCE_REQUIRED` means implementation/integration/stress/crash/performance/CI evidence is still required;
- `RUNTIME_PASS` is forbidden unless the corresponding runtime/CI test was actually executed successfully with evidence.

Consolidated regression areas:
- PostgreSQL durability + crash/restart;
- SupplierProduct concurrency + sequence uniqueness + retry;
- durable post-commit print delivery;
- reprint identity safety;
- fractional exact-unit rejection;
- large-dataset paging + WPF bounded rendering;
- LAN disconnect/reconnect + unknown-outcome replay;
- architecture/code CI drift;
- stale forensic/audit claim quarantine;
- Supplier Khata direction/source/idempotency continuity;
- Warranty transition/custody/replacement/credit continuity;
- navigation/schema/lock-order authority continuity.

Documentary regression execution:
```text
TOTAL CHECKS 50
PASS         50
FAIL         0
```

Verified evidence includes:
- Hardening 1-8 canonical authority markers present;
- Section 232 numbered tests are exactly 1 through 127 with no gaps/duplicates;
- numbered top-level sections remain exactly 0 through 233;
- Section 209 navigation count = 19;
- Warranty = screen #17;
- Section 185 lock-order levels = 10;
- Supplier Khata PURCHASE direction remains INCREASE_PAYABLE;
- WARRANTY_CREDIT direction remains DECREASE_PAYABLE;
- ItemSequence remains positive BIGINT and may grow beyond 999999;
- `WITH_SERVICE_CENTER` active residue = 0;
- canonical `inventory.units` / `inventory.lots` authorities remain present;
- root architecture file remains pointer-only and references the canonical document;
- Markdown fences remain balanced;
- canonical document remains ASCII-clean;
- Hardening 1-8 completion checkpoints remain present in the supplementary register;
- matrix keeps runtime evidence distinct from architecture regression status.

Important limitation:
- Hardening 9 did **not** execute or claim the full physical application build, PostgreSQL crash suite, concurrency stress suite, printer hardware tests, WPF performance benchmark, LAN integration suite, or CI pipeline.
- Those remain `RUNTIME_EVIDENCE_REQUIRED` and are Phase 3 / implementation-validation evidence targets.

**Status:** HARDENING 9 COMPLETE - architecture regression 50/50 PASS; runtime evidence pending Phase 3

Next:

`Supplementary Hardening 10 - Final Supplementary Closure`

# 18. Hardening 10 Final Supplementary Closure

## 18.1 Final Hardened Architecture Identity

**Hardening 0 / post-Phase-2 entry SHA-256:**
`BEB18E342FBD07D6D473F0774355EF5FB75536FD1C75EF2FDD5DC98B61B733C4`

**Final supplementary-hardened canonical SHA-256:**
`18A8197C85FEAC91F01D2B67163E82B0659A1D73E18456383C88B39439F7FCDC`

Hardening 10 is a closure/register operation only. It introduces no new canonical architecture rule, so the canonical SHA-256 intentionally remains identical to the Hardening 9 hash.

Hash progression:

```text
Phase 2 / H0 entry  BEB18E342FBD07D6D473F0774355EF5FB75536FD1C75EF2FDD5DC98B61B733C4
H1                   061587DE38AED1AC12232B90F4F29D0AFD2BA5C9712574AE3DA79F66C46FF078
H2                   48265D7C3CC7E7EA9AB16D289217CA5A7EEC8095B7A5E50218CF632EDAF84E2A
H3                   F3AF6B2A78F6563521F8AB335D68A0FB79CCE63EE463C4CFE70E9438E34B4303
H4                   ED415ED180622298CC6D78B00550884A75B61B0B3DEEB5802DEA22AED0E542A0
H5                   899519D1D406245B804511350D6E5B5CC982C9109A4EF7D767386FA11781D2DF
H6                   3681CA7676D74516C2EE37C61A5382AF954D927F61CA67BF48B53D62C402C3BB
H7                   95EF66C3ABAACD8037801AA85D87761E43A84AA15C7A4AC9F4C5CB7C311A6533
H8                   3D0193108C6DA201C66FDA61E4D4F80F0D3D230C46B33ED5CD54E8799D583C70
H9 / H10 final       18A8197C85FEAC91F01D2B67163E82B0659A1D73E18456383C88B39439F7FCDC
```

## 18.2 Final Supplementary Finding Closure Matrix

| Finding | Original Source Concern | Classification | Canonical Correction / Hardening | Regression Evidence | Final Supplementary Status |
|---|---|---|---|---|---|
| SH-DB-001 | sudden power loss / unsafe PostgreSQL durability and recovery | REAL_ARCHITECTURE_GAP | Sections 61.1, 61.2, 70.1, 78.1 | H9 architecture regression + Section 233 runtime gate | ARCHITECTURE CLOSED; runtime evidence Phase 3 |
| SH-CON-001 | SupplierProduct sequence lock contention | PERFORMANCE_HARDENING | Sections 178.4, 185.1, 185.2 | Section 232 tests 39-48 + H9 | ARCHITECTURE CLOSED; stress evidence Phase 3 |
| SH-PRN-001 | business commit succeeds but receipt/label delivery fails | ARCHITECTURE_ALREADY_COVERS + IMPLEMENTATION_REQUIREMENT | Sections 51.1-51.3, 178.3, print request schema/health contracts | Section 232 tests 49-62 + H9 | ARCHITECTURE CLOSED; implementation evidence Phase 3 |
| SH-UNIT-001 | fractional converted quantity for exact tracked units | ARCHITECTURE_ALREADY_COVERS + PHASE3_VALIDATION_ONLY | exact pre-rounding whole-unit and identity-cardinality rules | Section 232 tests 63-72 + H9 | ARCHITECTURE CLOSED; runtime evidence Phase 3 |
| SH-PERF-001 | large datasets degrade DB/WPF responsiveness | ARCHITECTURE_ALREADY_COVERS + PERFORMANCE_HARDENING + IMPLEMENTATION_REQUIREMENT | Sections 76.1-76.4, 230.1-230.2 | Section 232 tests 73-87 + H9 | ARCHITECTURE CLOSED; benchmark evidence Phase 3 |
| SH-LAN-001 | LAN disconnect / split-brain / ambiguous outcome | ARCHITECTURE_ALREADY_COVERS + PHASE3_VALIDATION_ONLY | Sections 82-82.3 and LAN replay/revalidation rules | Section 232 tests 88-102 + H9 | ARCHITECTURE CLOSED; integration evidence Phase 3 |
| SH-GOV-001 | architecture/code drift | IMPLEMENTATION_REQUIREMENT + PHASE3_VALIDATION_ONLY | Section 78.2 CI/release drift gate | Section 232 tests 103-117 + H9 | ARCHITECTURE CLOSED; repository/CI evidence Phase 3 |
| SA-001..SA-005 | stale sequence/navigation/lock/schema claims | STALE_AUDIT_FINDINGS | Section 0 precedence + Section 207.1 quarantine | Section 232 tests 118-127 + H9 | QUARANTINED / REGRESSION-LOCKED |
| SA-006..SA-007 | unproved test/code completion claims | PHASE3_VALIDATION_ONLY | repository/build/test evidence rule | Section 232 tests 123-127 + H9 | EVIDENCE-GATED FOR PHASE 3 |

No supplementary finding remains open as an unresolved architecture-design item.

This does not mean physical implementation evidence is complete. Any row marked runtime/implementation/benchmark/integration/CI evidence pending remains `RUNTIME_EVIDENCE_REQUIRED`.

## 18.3 Hardening 9 Regression Closure Evidence

Hardening 9 architecture regression result:

```text
TOTAL CHECKS 50
PASS         50
FAIL         0
```

Validated architecture invariants include:
- canonical Hardening 1-9 markers present;
- Section 232 tests exactly 1-127;
- top-level numbered sections exactly 0-233;
- exactly 19 full screens;
- Warranty screen #17;
- exactly 10 logical lock-order levels;
- Supplier Khata direction continuity;
- Warranty credit direction continuity;
- positive BIGINT ItemSequence with growth beyond 999999;
- no active WITH_SERVICE_CENTER residue;
- inventory.units / inventory.lots authority preserved;
- pointer-only root architecture file;
- balanced Markdown fences;
- ASCII-clean canonical document;
- runtime evidence cannot be silently promoted to RUNTIME_PASS.

## 18.4 Canonical / Pointer / Archive Authority

Canonical source remains:

`docs/Edge_Retails_Final_Architecture_Report_v1.md`

Root pointer remains:

`EDGE_RETAILS_BACKEND_ARCHITECTURE_FINAL.md`

Root pointer must remain pointer-only and must not receive a duplicate architecture body.

Historical files under:

`docs/Archive/Architecture_History/`

remain non-authoritative evidence/history and are not promoted into current architecture truth.

## 18.5 Phase 3 Handoff Baseline

Phase 3 must start from the exact canonical SHA-256:

`18A8197C85FEAC91F01D2B67163E82B0659A1D73E18456383C88B39439F7FCDC`

Phase 3 is an independent proof/freeze pass, not another redesign round unless evidence exposes a real contradiction or implementation-critical architecture defect.

Phase 3 evidence targets include:

```text
repository/build truth
EF model/migration drift
disposable PostgreSQL migration rehearsal
schema constraints/indexes
PostgreSQL integration tests
durability/crash/restart behavior
SupplierProduct concurrency/stress
idempotent ambiguous-response replay
durable print request + reprint behavior
fractional exact-unit enforcement
large-dataset query/WPF benchmark evidence
LAN disconnect/reconnect integration
architecture tests / CI wiring
Warranty transition/custody/replacement/credit behavior
Supplier Khata arithmetic/idempotency/concurrency
navigation/schema/lock-order drift
final canonical document integrity
```

No runtime/CI PASS is implied by this closure.

## 18.6 Final Supplementary Status

```text
Hardening 0   COMPLETE
Hardening 1   COMPLETE
Hardening 2   COMPLETE
Hardening 3   COMPLETE
Hardening 4   COMPLETE
Hardening 5   COMPLETE
Hardening 6   COMPLETE
Hardening 7   COMPLETE
Hardening 8   COMPLETE
Hardening 9   COMPLETE
Hardening 10  COMPLETE

Supplementary architecture hardening: CLOSED
Open supplementary architecture-design findings: 0
Runtime/implementation evidence: PENDING PHASE 3
Phase 3: NOT STARTED
```

**Status:** HARDENING 10 COMPLETE - SUPPLEMENTARY ARCHITECTURE HARDENING CLOSED

Next:

`Phase 3 - Independent Final Forensic Validation and Freeze`

# 19. Hardening 11 - International Forensic Architecture Remediation

**Date:** 2026-09-22

**Entry source:** international-level full backend architecture forensic audit performed after Hardening 10.

**Canonical SHA-256 after Hardening 11:** `12344760C60124DDC2D1C0E54ABFB7BC0E82B9B23A46E6463303EBFA530AB673`

Hardening 11 does not reopen previously closed H1-H10 architecture concerns. It reconciles the later international audit against the live canonical file, preserves already-resolved rules, and incorporates only confirmed remaining gaps through authoritative Section 233.1.

New/strengthened canonical contracts:
- machine-readable canonical authority manifest + SHA;
- V1 currency / BusinessDate / time-zone governance;
- AttributesJson schema versioning;
- backup key lifecycle and remote verification levels;
- cross-cutting irreversible external side-effect finality;
- audit retention/redaction/growth governance;
- operational-health and report resource governance;
- complete application/database compatibility matrix;
- LAN TerminalId/session/permission freshness;
- scanner namespace ambiguity + historical barcode identity;
- exact-unit state/accounting mapping and serialized negative-adjustment safety;
- idempotency purge safety and print delivery provenance;
- TrackingCode physical label / Serial / IMEI normalization;
- stable error-code/localization boundary.

Additional live canonical defect corrected:

`See Section 237` was an invalid forward reference in a 0-233 architecture and now points to the incorporated Section 233.1 idempotency-retention authority.

New governance artifacts:

```text
docs/Architecture_Authority_Manifest.json
docs/Architecture_International_Forensic_Remediation_2026-09-22.md
scripts/Verify-ArchitectureInternationalAuditRemediation.ps1
```

Verification result:

```text
ARCHITECTURE INTERNATIONAL AUDIT REMEDIATION: PASS
NumberedSections = 234 (0..233 unique)
Markdown fences  = 840 / balanced
Canonical SHA    = 12344760C60124DDC2D1C0E54ABFB7BC0E82B9B23A46E6463303EBFA530AB673
Manifest SHA     = MATCH
Root pointer     = POINTER-ONLY
```

Finding reconciliation:
- 6 Critical findings: all architecture-design concerns resolved or confirmed already resolved;
- 13 High findings: all architecture-design concerns resolved/strengthened; runtime-only evidence remains evidence-gated;
- 16 Medium findings: all architecture-design concerns resolved or confirmed already covered;
- no finding is promoted to runtime PASS solely because architecture text now exists.

**Status:** HARDENING 11 COMPLETE - INTERNATIONAL FORENSIC ARCHITECTURE REMEDIATION CLOSED; implementation/runtime evidence remains governed by Section 233.