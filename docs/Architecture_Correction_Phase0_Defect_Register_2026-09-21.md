# Edge Retails Architecture Correction - Phase 0 Defect Register

> **NON-AUTHORITATIVE WORKING REGISTER**
>
> This file does not define backend architecture.
> The single architecture authority remains:
>
> `docs/Edge_Retails_Final_Architecture_Report_v1.md`
>
> Phase 0 exists only to freeze the current baseline, enumerate confirmed defects, and define closure evidence before any architecture surgery begins.

## 1. Phase 0 Baseline

Canonical architecture:

```text
docs/Edge_Retails_Final_Architecture_Report_v1.md
```

Baseline captured:

```text
Date:        2026-09-21
Sections:    1-233
Lines:       7,525
Bytes:       170,543
SHA-256:     DF9D773E2A123F417ACFA8E63C09BB7D50B5A4EEF014E194EF97ADC30D607DB2
```

Root pointer:

```text
EDGE_RETAILS_BACKEND_ARCHITECTURE_FINAL.md
Bytes:   610
SHA-256: 08DCD659D556C82AD3013EB92D294FFAFE884EBC19FD3B35B3D77068D7433549
```

Root pointer is intentionally not a duplicate architecture body.

## 2. Safety Rules for the Correction Pass

1. Phase 0 does not edit canonical architecture rules.
2. Backend implementation, migrations, tests, and frontend are not modified by Phase 0.
3. Existing unrelated workspace changes must not be staged, reverted, reformatted, or overwritten.
4. Do not use `git add .`.
5. Historical architecture files under `docs/Archive/Architecture_History/` remain non-authoritative.
6. All corrections in later phases must be traceable to an item in this register or a newly discovered defect added to this register before repair.
7. After every repair phase, canonical SHA-256, section uniqueness, and `git diff --check` must be revalidated.

## 3. Workspace Safety Observation

The repository already contains substantial uncommitted backend/frontend work unrelated to this architecture correction.

Therefore:

```text
Architecture correction must be file-selective.
No broad reset.
No broad clean.
No broad checkout.
No blind staging.
No Git push unless explicitly requested.
```

## 4. Confirmed P0 Defects

### ARC-WAR-001 - Warranty Replacement TrackingCode Allocation Contradiction

**Severity:** P0  
**Primary sections:** 113, 176, 183, 199  
**Status:** RESOLVED IN PHASE 1 - pending Phase 3 validation  
**Target phase:** Phase 1

Conflict:

```text
Section 183:
replacement physical object must receive a new TrackingCode

Section 199:
TrackingCode allocation is defined only inside CreatePurchase receipt
```

A customer-specific warranty replacement is not necessarily a normal Purchase receipt.

**Risk:** implementation may create replacement identities outside the authoritative sequence, fail to create them at all, or incorrectly route customer-owned replacement through shop inventory.

**Required resolution:**
- define an explicit warranty-replacement receipt/identity path;
- allocate from the same `SupplierProduct.NextItemSequence`;
- use existing `IResourceLock / PostgresOperationLock`;
- create a new InventoryUnit/TrackingCode identity;
- do not create normal Sellable stock for a customer-specific replacement unless it explicitly becomes shop-owned stock.

**Closure evidence:** architecture test proving replacement gets a unique new TrackingCode through the same monotonic SupplierProduct sequence without a normal Purchase.

---

### ARC-WAR-002 - Claim-Level Supplier Authority Is Ambiguous

**Severity:** P0  
**Primary sections:** 111, 182, 223  
**Status:** RESOLVED IN PHASE 1 - pending Phase 3 validation  
**Target phase:** Phase 1

Current `WarrantyClaim` has header-level `SupplierId`, while one claim can contain multiple claim items.

The architecture does not currently prevent claim items from resolving to different original Suppliers.

**Risk:** one claim could point to Supplier A while one contained item belongs to Supplier B.

**Required V1 resolution:** one customer Warranty Claim may contain only items resolving to the same authoritative Supplier provenance. Mixed-Supplier intake creates separate claims.

**Closure evidence:** invariant + test rejecting a claim whose items resolve to different Suppliers.

---

### ARC-WAR-003 - Duplicate Active Warranty Claims for the Same Physical Unit Are Not Prevented

**Severity:** P0  
**Primary sections:** 111-112, 183, 225  
**Status:** RESOLVED IN PHASE 1 - pending Phase 3 validation  
**Target phase:** Phase 1

No explicit application/database invariant currently prevents two active claims for one exact InventoryUnit.

**Risk:** one physical object can appear simultaneously in two warranty workflows and custody histories.

**Required resolution:** at most one active Warranty Claim per exact tracked InventoryUnit.

Active states must be explicitly defined.

**Closure evidence:** concurrent/open duplicate claim test.

---

### ARC-WAR-004 - Warranty Eligibility Rules Are Incomplete

**Severity:** P0  
**Primary sections:** 111-113  
**Status:** RESOLVED IN PHASE 1 - pending Phase 3 validation  
**Target phase:** Phase 1

The model stores `WarrantyValidUntil` and original Sale references but does not formally define a complete eligibility gate.

Missing/insufficiently explicit checks include:
- warranty expiry;
- Sale/SaleItem/Product relationship;
- InventoryUnit/SaleItemUnit relationship;
- Purchase-voided provenance;
- active claim collision;
- already returned/replaced/refunded state;
- non-serialized cumulative eligible quantity;
- authoritative Supplier provenance.

**Risk:** expired or logically invalid items can enter the claim lifecycle.

**Required resolution:** formal `ValidateWarrantyEligibility` architecture rules for tracked and quantity-based products.

**Closure evidence:** explicit negative tests for each invalid case.

---

### ARC-WAR-005 - Supplier Warranty Credit Does Not Fully Close Inventory Carrying Cost

**Severity:** P0  
**Primary sections:** 114, 121, 223  
**Status:** RESOLVED IN PHASE 1 - pending Phase 3 validation  
**Target phase:** Phase 1

The architecture recognizes:
- shop-owned item can be WITH_SUPPLIER;
- supplier may resolve by credit/recovery;
- monetary `WARRANTY_CREDIT` can affect Supplier Khata.

But it does not fully define the atomic inventory/cost closure when the physical item is not returned.

**Risk:** Supplier Khata credit can be posted while recoverable inventory carrying cost remains stranded in WITH_SUPPLIER.

**Required resolution:** define one atomic warranty-credit resolution covering:
- inventory disposition;
- carrying-cost removal;
- Supplier Khata credit;
- recovery-vs-carrying-cost difference;
- recognized loss/recovery treatment;
- warranty case closure.

**Closure evidence:** equal-credit, under-credit, and over-recovery scenarios.

---

### ARC-KHA-001 - SUPPLIER_REFUND_RECEIVED Direction Is Not Explicitly Defined

**Severity:** P0  
**Primary sections:** 216, 219  
**Status:** RESOLVED IN PHASE 1 - pending Phase 3 validation  
**Target phase:** Phase 1

The EntryType exists, but the direction is not explicitly locked.

Correct balance example:

```text
Purchase               +100,000
Payment                -100,000
Purchase Return Credit  -20,000
Supplier Refund          +20,000
--------------------------------
Balance                       0
```

Therefore:

```text
SUPPLIER_REFUND_RECEIVED
= INCREASE_PAYABLE
```

when the refund consumes an existing Supplier credit/advance.

**Risk:** wrong sign can double the Supplier credit instead of settling it.

**Closure evidence:** end-to-end paid-purchase -> return -> supplier refund -> zero balance test.

---

## 5. Confirmed P1 Defects

### ARC-WAR-006 - Warranty State Transition Matrix Is Not Fully Locked

**Severity:** P1  
**Primary section:** 112  
**Status:** RESOLVED IN PHASE 1 - pending Phase 3 validation  
**Target phase:** Phase 1

States are listed, but exact allowed/forbidden transitions and status/custody compatibility are incomplete.

**Required resolution:** authoritative transition matrix with valid source/target states and custody effects.

---

### ARC-WAR-007 - WITH_SERVICE_CENTER Has No Service-Center Identity Model

**Severity:** P1  
**Primary section:** 112  
**Status:** RESOLVED IN PHASE 1 - pending Phase 3 validation  
**Target phase:** Phase 1

Custody includes `WITH_SERVICE_CENTER`, but no ServiceCenter entity/reference/command path is defined.

**Required resolution:** remove from V1 or formally model service-center identity and custody provenance. Preferred V1 direction: remove unless separately required.

---

### ARC-WAR-008 - Warranty Replacement Provenance Does Not Fit SourcePurchaseItem-Only Origin Cleanly

**Severity:** P1  
**Primary sections:** 176, 183  
**Status:** RESOLVED IN PHASE 1 - pending Phase 3 validation  
**Target phase:** Phase 1

InventoryUnit traceability is strongly purchase-oriented through `SourcePurchaseItemId`, while a customer-specific replacement may originate from a warranty replacement event.

**Required resolution:** define warranty-origin provenance without weakening normal purchase provenance, e.g. explicit warranty-replacement source reference/receipt authority.

---

### ARC-WAR-009 - Mixed-Unit ClaimedBaseQuantity KPI Is Dimensionally Unsafe

**Severity:** P1  
**Primary sections:** 220, 223  
**Status:** RESOLVED IN PHASE 2 - pending Phase 3 validation  
**Target phase:** Phase 2

A global quantity total can combine incompatible base dimensions, e.g. pieces + meters.

**Required resolution:** dashboard uses ClaimCount, ClaimLineCount, TrackedWarrantyUnitCount, and Product/BaseUnit-specific quantity breakdowns.

---

### ARC-KHA-002 - WARRANTY_CREDIT Direction Is Not Explicitly Locked

**Severity:** P1  
**Primary sections:** 216, 223  
**Status:** RESOLVED IN PHASE 1 - pending Phase 3 validation  
**Target phase:** Phase 1

Supplier grants monetary warranty credit, so payable/advance position must have an explicit direction.

Expected normal direction:

```text
WARRANTY_CREDIT
= DECREASE_PAYABLE
```

subject to the final warranty financial closure semantics.

---

### ARC-KHA-003 - Accidental Overpayment vs Intentional Supplier Advance Is Not Distinguished

**Severity:** P1  
**Primary sections:** 217-219  
**Status:** RESOLVED IN PHASE 1 - pending Phase 3 validation  
**Target phase:** Phase 1

Negative Supplier balances are supported, but the architecture does not distinguish accidental overpayment from a deliberate Supplier advance.

**Required resolution:**
- normal payment cannot silently exceed outstanding payable;
- explicit Supplier Advance intent requires permission, confirmation/reason, and audit.

---

### ARC-KHA-004 - Supplier Account Concurrency Lock Is Not Explicit

**Severity:** P1  
**Primary section:** 225  
**Status:** RESOLVED IN PHASE 1 - pending Phase 3 validation  
**Target phase:** Phase 1

Two terminals can theoretically read the same outstanding balance and both post settlement.

**Required resolution:** use the existing resource-lock abstraction with a logical resource such as `supplier-account:<SupplierId>`, then reload balance under the transaction before validating payment/advance.

---

### ARC-KHA-005 - Automatic Account Entry Source Uniqueness Is Not Fully Specified

**Severity:** P1  
**Primary sections:** 216, 219, 225  
**Status:** RESOLVED IN PHASE 1 - pending Phase 3 validation  
**Target phase:** Phase 1

Idempotency is mentioned, but source-generated entries need a clear database uniqueness barrier.

Examples:
- one PURCHASE entry per Purchase;
- one PURCHASE_RETURN_CREDIT per PurchaseReturn;
- one PURCHASE_VOID_REVERSAL per PurchaseVoid;
- one warranty credit per approved financial warranty resolution.

**Required resolution:** unique source identity using EntryType + ReferenceType + ReferenceId where applicable, plus ClientOperationId where command-level retry semantics apply.

---

### ARC-KHA-006 - Purchase Settlement Authority Still Carries Legacy Semantic Baggage

**Severity:** P1  
**Primary sections:** 156, 219 and physical Purchase model  
**Status:** RESOLVED IN PHASE 1 - pending Phase 3 validation  
**Target phase:** Phase 1

Architecture now supports unpaid, partial, and full Supplier settlement, but older `SettlementMode` concepts originated as binary EXTERNAL/CASH_DRAWER settlement.

**Required resolution:** make the architecture explicit that:
- Purchase creates liability;
- SupplierPayment settles liability;
- payment method is not payable truth;
- no Purchase field may compete with SupplierPayment/Khata authority.

---

### ARC-DOC-001 - Duplicate Sections 146-149

**Severity:** P1  
**Primary lines at Phase 0:** 4574-4694 and 4767-4887  
**Status:** RESOLVED IN PHASE 2 - pending Phase 3 validation  
**Target phase:** Phase 2

Duplicated top-level sections:
- 146 Production Sales Aggregate
- 147 Sale Completion Authority and Pricing
- 148 Deterministic Invoice Discount Allocation
- 149 Sale Return

The earlier duplicate block is malformed/truncated and contains repeated unrelated cloud-data text.

**Risk:** readers may consume the incomplete copy and miss later rules.

**Required resolution:** retain one complete authoritative 146-149 block and remove the malformed duplicate.

---

### ARC-DOC-002 - Active 17-Screen Rule Contradicts Current 19-Screen Contract

**Severity:** P1  
**Primary sections:** 102, 130, 209  
**Status:** RESOLVED IN PHASE 2 - pending Phase 3 validation  
**Target phase:** Phase 2

Current contradictory statements include:
- `17-Screen Coverage PASS`;
- `The full-screen count remains 17`;
- `No new primary navigation screen is required for V1`.

Current authority is 19 screens with dedicated Warranty and separate Product Management/Inventory.

**Required resolution:** historicalize old screen-count statements and leave one current 19-screen authority.

---

### ARC-DOC-003 - Physical Implementation Status Section Is Stale

**Severity:** P1  
**Primary sections:** 101-102  
**Status:** RESOLVED IN PHASE 2 - pending Phase 3 validation  
**Target phase:** Phase 2

The canonical architecture still says the DB/persistence/migrations are not yet built as a "current workspace status", while the workspace now contains physical backend/migration implementation.

**Required resolution:** rename/reframe as a historical Phase-16 audit snapshot, not current status.

---

### ARC-DOC-004 - Multiple Competing "Final" Declarations Obscure Current Authority

**Severity:** P1  
**Primary sections:** 90, 102, 139, 169, 208, 228  
**Status:** RESOLVED IN PHASE 2 - pending Phase 3 validation  
**Target phase:** Phase 2

**Required resolution:**
- add a top-level Current Architecture Authority Index;
- relabel older milestones as historical declarations;
- make Section 228 the current V1 declaration and Section 233 the current implementation gate.

---

### ARC-DOC-005 - Migration Vocabulary Is Historically Mixed

**Severity:** P1  
**Primary sections:** 133, 167, 190, 227  
**Status:** RESOLVED IN PHASE 2 - pending Phase 3 validation  
**Target phase:** Phase 2

Older language uses `Migration 001`; later architecture correctly preserves the EF Core `InitialProductionBaseline` strategy.

**Required resolution:** normalize current authority to EF Core InitialProductionBaseline and prevent interpretation as a parallel handwritten SQL migration system.

---

## 6. Confirmed P2 Documentation Defect

### ARC-DOC-006 - Unicode Diagram Portability

**Severity:** P2  
**Status:** RESOLVED IN PHASE 2 - pending Phase 3 validation  
**Target phase:** Phase 2

Raw UTF-8 is not corrupted, but several terminal renderers display decorative Unicode arrows/dashes poorly.

**Required resolution:** normalize architecture flow diagrams to portable ASCII `->` where doing so does not change meaning.

---

## 7. Phase Ownership

```text
PHASE 0
Baseline + defect lock only

PHASE 1
ARC-WAR-001..008
ARC-KHA-001..006

PHASE 2
ARC-WAR-009
ARC-DOC-001..006
Authority-index/consolidation work

PHASE 3
Full contradiction scan
Regression matrix
Section uniqueness
Hash/diff validation
Final architecture freeze
```

Any newly discovered P0/P1 issue must be added to this register before being repaired.

## 8. Phase 0 Exit Criteria

Phase 0 is complete only when:

- canonical file still has the exact baseline SHA-256 above;
- no canonical architecture rule was changed during Phase 0;
- root pointer remains pointer-only;
- defect register exists and is explicitly non-authoritative;
- all currently confirmed P0/P1 defects are registered;
- unrelated backend/frontend workspace changes remain untouched;
- no Git commit or push occurs unless explicitly requested.



### ARC-DOC-007 - Pre-existing Unbalanced Markdown Fence Structure

**Severity:** P1  
**Status:** RESOLVED IN PHASE 2 - pending Phase 3 validation  
**Discovered during:** Phase 1 candidate validation  
**Target phase:** Phase 2

The frozen Phase 0 canonical baseline contains an odd number of Markdown fence lines:

```text
Baseline fence count: 687
```

Phase 1 staged blocks each have balanced fence counts, and the Phase 1 candidate preserves the baseline odd parity rather than introducing it.

A simple parity scan remains open by the time the document reaches the Section 208 area, around baseline line 6762, but this does not prove that line is the original malformed fence because an earlier missing/open fence can shift all later parity.

**Required resolution:** Phase 2 must perform a section-aware Markdown fence audit, identify the true malformed block, repair it without changing business semantics, and require an even final fence count.

**Closure evidence:** full canonical Markdown fence count is even and a sequential section-aware scan ends in closed state.

# Phase 1 Resolution Checkpoint

**Canonical architecture SHA-256 after Phase 1:** `78023EDAAE90EF0093C5534FD9A889BA8A2BF065B150CE6E3ED64BA62170A26D`

Resolved in architecture during Phase 1: ARC-WAR-001 through ARC-WAR-008 and ARC-KHA-001 through ARC-KHA-006.

Resolved in Phase 2: ARC-WAR-009 and ARC-DOC-001 through ARC-DOC-007 (pending Phase 3 validation).

Phase 3 must independently re-scan and validate all Phase 1 and Phase 2 resolutions before final architecture freeze.

# Phase 2 Resolution Checkpoint

**Canonical architecture SHA-256 after Phase 2:** `BEB18E342FBD07D6D473F0774355EF5FB75536FD1C75EF2FDD5DC98B61B733C4`

Resolved in Phase 2: ARC-WAR-009 and ARC-DOC-001 through ARC-DOC-007.

Validation at Phase 2 exit:
- numbered sections 0-233: 234 headings, all unique;
- Markdown fences: 756, balanced and closed;
- stale 17-screen active wording: zero;
- stale current-workspace / NOT YET status wording: zero;
- ambiguous Migration 001 / 001_Initial_Schema.sql wording: zero;
- mixed-unit ClaimedBaseQuantity KPI: zero;
- unsupported WITH_SERVICE_CENTER token: zero;
- non-ASCII / mojibake glyph residue: zero;
- current declaration authority: Section 228;
- current implementation gate: Section 233.

Phase 3 must independently re-scan all Phase 0-2 closures and run the final regression/freeze evidence.

