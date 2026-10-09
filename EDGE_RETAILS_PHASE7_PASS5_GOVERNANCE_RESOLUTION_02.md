# Pass5 governance resolution02 — immutable addendum

Status: GOVERNANCE_AUTHORITY_ONLY. No implementation/certification claimed.
Source attachment: C:\Users\muham\.codex\attachments\285e84fc-a339-41b3-b9ee-2ab899433620\Pasted text.txt
Source SHA256: C6CBB91C31393194427ADCC859CC8FA715ADDE7527FF872B9221AA1EDF054C61
Original frozen Pass5 authority and Resolution01 remain unchanged.

## Exact user governance resolution

# =====================================================================
# EDGE RETAILS — PHASE 7 PASS 5
# GOVERNANCE RESOLUTION 02
#
# C-P5-ARCH-03
# MISSING EXACT INVENTORY REPRESENTATION
# =====================================================================

The controlled verdict:

PASS5_BLOCKED_MIGRATION_DECISION

was correct.

Do NOT restart completed audit work.

Preserve:

- Pass4 893/893 baseline
- Pass4 final manifest
- frozen Pass5 authority
- Governance Resolution 01
- A/B/C/D/root specialist audits
- completed 32/32 bounded authority rows
- findings register
- contradiction evidence
- stop-state evidence


=============================================================
1. PERSISTED DOMAIN VALUE — APPROVED
=============================================================

Authorize exactly one new InventoryUnitStatus persisted value:

InventoryUnitStatus.Missing = 12


MANDATORY:

existing persisted values 1–11 remain numerically unchanged.

Do NOT renumber.

Do NOT repurpose Scrapped.

Do NOT repurpose SupplierReturned.

Do NOT repurpose ReceiptVoided.


=============================================================
2. CANONICAL MISSING SEMANTICS
=============================================================

InventoryUnitStatus.Missing means:

A known canonical physical identity existed historically,
but following an explicitly approved shortage/loss business event,
the physical unit is not accounted for in current shop inventory.


For Missing:

StockBalance contribution = 0

OnHand contribution = 0

Sellable contribution = 0

Scrap bucket contribution = 0

ProductCostState/current carrying-value contribution = 0

POS eligibility = NO

ordinary Return eligibility = NO

ordinary PurchaseReturn eligibility = NO

Thaka issue eligibility = NO

ordinary warranty intake eligibility = NO

physical identity history = PRESERVED

TrackingCode = PRESERVED

Serial/IMEI = PRESERVED

SupplierProduct = PRESERVED

ItemSequence = PRESERVED

origin/provenance history = PRESERVED

acquisition history = PRESERVED


The derecognition/loss event must occur exactly once.


Do NOT create:

Supplier transfer fiction

Customer ownership fiction

Scrap inventory

new InventoryUnit

new TrackingCode

new ItemSequence


=============================================================
3. MISSING IS NOT ABSOLUTELY IRREVERSIBLE
=============================================================

Missing is terminal for ORDINARY COMMERCIAL OPERATIONS.

It is NOT absolutely terminal against a controlled physical recovery.


If the SAME physical item is later found:

it may leave Missing only through an explicit authorized
Recovery / Found correction.

The recovery must:

reuse the SAME InventoryUnit

reuse the SAME TrackingCode

reuse the SAME Serial/IMEI

preserve full Missing/loss history

restore inventory only through canonical movement/value authority

create explicit audit evidence

apply the correct existing economic recovery/correction classification


Never create a replacement identity merely because a previously Missing item
was later found.


If the live accounting model cannot represent recovery economics without a
new policy decision:

STOP with:

PASS5_BLOCKED_BUSINESS_DECISION


=============================================================
4. STOCKTAKE OBSERVATION VS POSTED STATUS
=============================================================

StocktakeCheckResult.Missing and InventoryUnitStatus.Missing are separate.


Stocktake Missing check:

observation only


InventoryUnitStatus.Missing:

posted/approved economic state


Required lifecycle:

COUNTING

→ exact unit observed Missing

→ REVIEW

→ approved POST

→ one inventory derecognition

→ one carrying-value removal

→ one RecognizedInventoryLoss where applicable

→ InventoryUnitStatus.Missing


Before POST:

InventoryUnit status remains unchanged.


Cancelled/unposted Stocktake:

must not terminalize the unit.


=============================================================
5. EXPLICIT LOST ADJUSTMENT
=============================================================

An explicit exact-unit Lost/Missing adjustment may transition an existing
InventoryUnit to Missing.


Do NOT generalize Missing to:

Damaged

Defective

Other

Scrap

SupplierReturned

Warranty

ordinary negative adjustment without explicit loss semantics


Each path must preserve its real physical meaning.


=============================================================
6. SCRAPPED REMAINS DISTINCT
=============================================================

Scrapped means retained/identified physical Scrap according to current
canonical accounting policy.


Preserve existing valid Scrapped behavior.


Actual Scrap may contribute to the Scrap bucket according to live policy.


Missing NEVER contributes to Scrap.


Do not "fix" stocktake shortage by creating Scrap quantity.


=============================================================
7. DATABASE / EF DECISION
=============================================================

Current inspected source/configuration indicates:

units.status = integer

movement_units.from_status/to_status = integer

no source-proven admissible-status CHECK requiring DDL


Therefore this governance decision authorizes:

domain enum addition = YES

persisting integer value12 = YES

new column = NO

new table = NO

Npgsql enum DDL = NO

new EF migration solely for this enum value = NO

empty migration = PROHIBITED


After implementation prove on isolated PostgreSQL18/Npgsql:

12 persists successfully

12 reads successfully

EF mapping handles it

model snapshot remains aligned

has-pending-model-changes reports none


If deeper live inspection discovers an actual database constraint requiring
DDL:

STOP BEFORE MIGRATION.


Return:

PASS5_BLOCKED_MIGRATION_DECISION


Document the exact minimum constraint expansion.


Do NOT generate a migration without new explicit authorization.


=============================================================
8. HISTORICAL DATA — NO BLANKET CONVERSION
=============================================================

FORBIDDEN:

UPDATE all Scrapped rows to Missing

or any equivalent blanket status8 → status12 rewrite.


Existing Scrapped rows may represent:

real retained Scrap

historical shortage

other legacy history

ambiguous history


Reclassify only mathematically and historically conclusive candidates.


Potential conclusive stocktake candidate requires durable evidence including:

posted negative Stocktake

exact matching Missing unit check

matching InventoryUnit

matching product/unit movement lineage

negative source stock effect

matching value/lot derecognition

no positive retained Scrap destination

no later contradictory unit transition


Potential Lost-adjustment candidate requires equivalently strong durable
evidence.


Cost == 0 does NOT by itself disqualify a legitimate loss.


Generic Other/Damaged or ambiguous lineage:

DO NOT GUESS.


Classify:

CONCLUSIVE

AMBIGUOUS

NOT_MISSING


=============================================================
9. HISTORICAL REPAIR STRATEGY
=============================================================

Do NOT create an automatic EF data-backfill migration in this pass by default.


Implement/certify a bounded historical classifier/rehearsal capable of:

identifying conclusive candidates

identifying ambiguous candidates

proving idempotent classification

preserving all original commercial history

preserving physical identity

preserving sequences/high-water

avoiding second inventory derecognition

avoiding second recognized loss

avoiding balancing Scrap


Operational production reclassification is NOT authorized by this resolution.


Pass5 certification must prove the strategy against isolated mixed-history
PostgreSQL fixtures.


=============================================================
10. COMPATIBILITY / DOWNGRADE
=============================================================

Once status12 exists in business data:

older binaries that do not understand status12 are NOT assumed safe.


FORBIDDEN downgrade mappings:

Missing → Scrapped

Missing → SupplierReturned

Missing → ReceiptVoided


Do not falsify provenance for compatibility.


Current V1 single-install upgrade policy:

roll forward unless an explicitly verified compatible reader exists.


Record this in Pass5 compatibility evidence.


Do not redesign installer/deployment architecture here.


=============================================================
11. REQUIRED UNIT PROOFS
=============================================================

Prove:

all InventoryUnitStatus values have explicit accounting policy

values1–11 unchanged

Missing numeric value = 12

Missing stock contribution = 0

Missing OnHand = 0

Missing Sellable = 0

Missing Scrap contribution = 0

Missing current carrying-value contribution = 0

Missing ordinary commercial eligibility = false

Missing preserves identity/provenance

Scrapped still retains its existing correct Shop/Scrap semantics

SupplierReturned semantics unchanged

ReceiptVoided semantics unchanged

unknown enum value fails closed


=============================================================
12. REQUIRED REAL POSTGRESQL PROOFS
=============================================================

Use isolated PostgreSQL18/Npgsql.


Cover exact-unit shortage/Lost for:

Serialized

IndividualPiece

intact Container


For every path prove:

one exact identity transitioned

BaseQuantity removed exactly once

carrying value removed exactly once

RecognizedLoss recorded exactly once where applicable

StockBalance contribution after = 0

OnHand after = 0

Sellable after = 0

Scrap bucket after = 0

identity unchanged

TrackingCode unchanged

Serial/IMEI unchanged

SupplierProduct unchanged

ItemSequence unchanged

origin unchanged

acquisition history unchanged

no Supplier fiction

no Customer fiction

no fake Return

no fake Refund


=============================================================
13. REPLAY / CONCURRENCY PROOFS
=============================================================

Within CURRENT Pass5-local operation behavior only,
without redesigning global Phase12 architecture, prove:

same approved Stocktake posting cannot create duplicate loss

same explicit Lost adjustment cannot create duplicate derecognition

concurrent posting cannot transition the unit twice

failure before commit leaves original state

failure after provisional SQL work rolls back atomically

high-water/identity claims remain unchanged


Global replay-before-validation and fingerprint uniformity remain Phase12.


=============================================================
14. MISSING VS OTHER STATES RECONCILIATION
=============================================================

Persist side-by-side controlled scenarios for:

Missing exact unit

retained physical Scrapped unit

shop-owned WithSupplier warranty unit

customer-owned warranty replacement unit


Independently prove each has the correct:

ownership/economic contribution

custody

OnHand

Sellable

StockBalance

bucket contribution

carrying value

profit/loss effect

identity history


No two states may achieve reconciliation through contradictory semantics.


=============================================================
15. RECOVERY PROOF
=============================================================

Add a bounded controlled proof for a previously Missing identity later found.


Required:

same InventoryUnit reused

same TrackingCode reused

same ItemSequence reused

no duplicate identity claim

explicit recovery movement

appropriate inventory value restoration

full Missing/loss history retained

no silent deletion of prior loss history


If current code has no canonical safe Recovery/Found command:

do NOT improvise.


Record:

CONFIRMED_PASS5_GAP

and route through the normal finding/whitelist process.


If business accounting policy for recovery is not already frozen:

STOP:

PASS5_BLOCKED_BUSINESS_DECISION


=============================================================
16. PROTECTED TEST ALIGNMENT
=============================================================

Only shortage-destination assertions directly superseded by this governance
decision may change.


Classification:

AUTHORIZED_ASSERTION_ALIGNMENT


Preserve:

identity assertions

quantity assertions

cost assertions

loss-once assertions

replay assertions

rollback assertions

retained-Scrap tests

Pass1/Pass2 unrelated protections

Pass4 G01-G14/G18/G19

P4-H1

P4-H2

Tracking authority


Do not weaken tests.


=============================================================
17. RESUME THE EXISTING PASS5 CHECKPOINT
=============================================================

DO NOT restart:

Pass4 verification

32-area audit

A audit

B audit

C audit

D audit

root audit


They are retained.


Current 32/32 bounded audit inventory remains the Step1 source.


Now:

1. Record this as:

EDGE_RETAILS_PHASE7_PASS5_GOVERNANCE_RESOLUTION_02.md


2. Record its SHA256.


3. Update C-P5-ARCH-03 from:

BLOCKED

to:

GOVERNANCE_RESOLVED_IMPLEMENTATION_AUTHORIZED


4. Preserve C-P5-ARCH-01 and C-P5-ARCH-02 Resolution01 authority.


5. Resolve only bounded follow-up unknowns required for implementation.


6. Freeze the exact Pass5 edit whitelist.


7. Implement the 17 confirmed production/workflow/read-model gaps plus
   explicitly approved reconciliation work.


8. Complete documentation deliverables:

Business Invariants Registry

Business Event Effect Matrix


9. Run focused tests.


10. Run isolated PostgreSQL proofs.


11. Run Golden A-D traces.


12. Run independent Owner reconciliation.


13. Stabilize only proven defects.


14. Freeze Pass5 source candidate.


15. Generate Pass5 source manifest.


16. Run ONE coordinated final verification wave.


17. Dispatch ONE NEW independent Sol/High final validator.


18. Formal lock only if independent verdict is:

PASS5_SOL_CHALLENGE_PASS_FOR_LOCK


Then:

PASS5_CERTIFIED_CLOSED_LOCKED


19. STOP.


=============================================================
18. STILL OUT OF SCOPE
=============================================================

Do NOT start:

Phase7 final closure

Phase12 implementation

Phase8

Phase9

POS pilot


Do NOT:

commit

push

reset

clean

deploy

touch operational business data


=============================================================
FINAL GOVERNANCE DECISION
=============================================================

InventoryUnitStatus.Missing = 12

APPROVED.


Economic/current inventory contribution:

ZERO.


Physical identity/history:

PRESERVED.


Retained Scrap:

DISTINCT.


Ordinary commercial use:

PROHIBITED.


Controlled same-identity recovery:

ALLOWED ONLY THROUGH EXPLICIT AUTHORIZED RECOVERY.


Schema migration solely for value12:

NOT REQUIRED BASED ON CURRENT EVIDENCE.


Automatic historical backfill:

NOT AUTHORIZED.


Operational historical repair:

NOT AUTHORIZED IN THIS PASS WITHOUT SEPARATE REVIEW.


PASS5 IMPLEMENTATION MAY NOW RESUME FROM THE EXISTING CHECKPOINT.