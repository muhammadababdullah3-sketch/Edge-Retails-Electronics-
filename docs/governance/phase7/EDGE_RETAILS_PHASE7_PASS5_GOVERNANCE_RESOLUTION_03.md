# Pass5 governance resolution03 — immutable addendum

Status: GOVERNANCE_AUTHORITY_ONLY. Source checkpoint retained; no implementation/certification claim.
Source attachment: C:\Users\muham\.codex\attachments\e6ae9146-68a0-4e02-9e4b-9ba442063eec\Pasted text.txt
Source SHA256: 053138B9E5D84DD6430A85C565407805F56DB838A4A7ADA0DD38301DF953941F

## Exact user governance resolution

# =====================================================================
# EDGE RETAILS
# PHASE 7 PASS 5
# GOVERNANCE RESOLUTION 03
#
# FOUND / RECOVERY BUSINESS ACCOUNTING AUTHORITY
# =====================================================================

The controlled verdict:

PASS5_BLOCKED_BUSINESS_DECISION

was CORRECT.

Do not restart any completed audit.

Preserve:

- Pass4 893/893 baseline
- frozen Pass5 authority
- Governance Resolution 01
- Governance Resolution 02
- completed 32/32 bounded audit rows
- A/B/C/D/root audit evidence
- Missing = 12 decision
- all existing findings and checkpoint evidence


=============================================================
1. BUSINESS POLICY DECISION
=============================================================

Approve:

OPTION A

Canonical classification name:

InventoryLossRecoveryGain


A Found recovery is NOT:

SalesRevenue

WarrantyRecoveryGain

SupplierRecovery

Cash income

Supplier settlement

negative RecognizedLoss

editing/removing the original loss

generic positive-stock profit


InventoryLossRecoveryGain is a distinct approved operating recovery.


=============================================================
2. ORIGINAL LOSS REMAINS HISTORICAL TRUTH
=============================================================

When an exact unit was legitimately posted Missing:

the original inventory derecognition remains

the original RecognizedInventoryLoss remains

the original Missing movement remains

the original audit/history remains


Finding the item later does NOT make the prior Missing event fictitious.


NEVER:

set the old loss to zero

delete the old loss

change its business date

write a negative RecognizedLossAmount

rewrite its original movement

rewrite historical stocktake evidence


Recovery is a NEW business event at the recovery time.


=============================================================
3. SAME PHYSICAL IDENTITY
=============================================================

Found recovery MUST restore the SAME physical identity.


Preserve:

InventoryUnitId

TrackingCode

Serial

IMEI

SupplierProductId

ItemSequence

SupplierCodeSnapshot

ProductSkuSnapshot

OriginType

source/acquisition provenance

historical Missing/loss links


DO NOT call PhysicalUnitCreationAuthority.

DO NOT allocate a new ItemSequence.

DO NOT create a new TrackingCode.

DO NOT create a duplicate InventoryUnit.

DO NOT fake a Purchase.

DO NOT fake a Supplier receipt.


=============================================================
4. FOUND COMMAND
=============================================================

A canonical explicit Found / Recovery mutation is required.


It must operate only on an InventoryUnit currently in:

InventoryUnitStatus.Missing


Ordinary commercial workflows remain prohibited while Missing.


Found must be:

explicit

authorized

audited

reasoned

linked to the exact prior Missing/loss authority

atomic

once-only

replay-safe using current Pass5-local mechanisms


Do NOT use generic positive StockAdjustment semantics if that would allocate a
new identity or lose source linkage.


=============================================================
5. QUANTITY / CUSTODY EFFECT
=============================================================

Approved Found recovery restores:

shop-owned inventory quantity

OnHand quantity

appropriate inventory bucket

Sellable eligibility only if all ordinary sellability rules are satisfied


Found does NOT automatically guarantee Sellable.


If the recovered physical unit is:

Damaged

Defective

otherwise non-sellable

then restore it into the correct existing non-sellable condition authority.


Missing → Found does not erase condition reality.


=============================================================
6. INVENTORY VALUE RESTORATION
=============================================================

Canonical:

RestoredInventoryValue
=
the exact authoritative carrying value actually derecognized by the linked
Missing event for this recovered physical identity


DO NOT derive restoration from:

current Product cost

current moving average

current purchase price

default cost

replacement cost

arbitrary operator amount

AcquisitionCost alone when it does not prove the value actually removed


The recovery cannot restore more inventory value than remains unrecovered from
the linked Missing event.


For a legitimate zero-value source:

RestoredInventoryValue = 0


No invented inventory value.


=============================================================
7. PROFIT / RECOVERY CLASSIFICATION
=============================================================

Canonical:

InventoryLossRecoveryGain
=
the attributable previously recognized inventory loss associated with the
same linked Missing event / recovered identity


The gain is recognized in the CURRENT recovery period.


It does NOT rewrite the original loss period.


Maximum cumulative recovery gain for a source loss event:

<= original recognized loss not already recovered


If the attributable prior recognized loss is zero:

InventoryLossRecoveryGain = 0


Never invent a gain solely because quantity was restored.


=============================================================
8. VALUE VS LOSS PRECISION
=============================================================

Inventory carrying value and RecognizedLoss may have different persisted
precision.


Do NOT force equality by rewriting historical rows.


Restore:

the exact persisted/authoritative derecognized inventory value


Recognize:

the deterministically attributable original recognized loss


Use the existing canonical money precision and residual-allocation rules.


For multi-unit Missing events:

allocate attributable recovery by durable source evidence

use deterministic stable ordering for any canonical residual

ensure:

SUM(recovered gain)
<= original recognized loss

SUM(restored inventory value)
<= original derecognized carrying value


No per-unit guess.


=============================================================
9. PARTIAL RECOVERY
=============================================================

For a multi-unit Missing/loss event:

one or more units may later be found independently.


Each Found operation must link:

InventoryUnitId

source Missing event/movement

source loss authority

allocated derecognized carrying value

allocated recognized loss

actor

reason

operation/correlation identity

recovery classification


Recover only the selected physical identity's attributable amount.


Do not treat the whole original loss as recovered because one unit was found.


=============================================================
10. REPEATED MISSING → FOUND LIFECYCLE
=============================================================

An exact unit may theoretically be:

Missing

→ Found

→ later Missing again

→ Found again


Every Found must link to the latest unrecovered approved Missing/loss episode.


Never reuse an older already recovered loss authority.


Each cycle preserves complete history.


=============================================================
11. AMBIGUOUS LEGACY HISTORY
=============================================================

If the system cannot conclusively prove:

the exact Missing event

the exact physical identity

the derecognized carrying value

the attributable recognized loss

and that the loss has not already been recovered


then automatic Found economic posting is FORBIDDEN.


Return a controlled business error / review requirement.


Do not guess.


Do not use current cost as fallback.


Do not use AcquisitionCost as universal fallback.


Operational legacy repair remains separately controlled.


=============================================================
12. REPORTING
=============================================================

Reporting must expose InventoryLossRecoveryGain separately from:

SalesRevenue

COGS

RecognizedInventoryLoss

WarrantyRecoveryGain

Supplier credits/refunds

Cash movements


Canonical profit intent:

NetProfit
=
GrossProfit
- OperatingExpenses
- RecognizedInventoryLosses
+ approved explicit operating recoveries


InventoryLossRecoveryGain is one such approved explicit operating recovery.


It must never appear in Net Sales.


=============================================================
13. CASH / SUPPLIER / WARRANTY EFFECT
=============================================================

Found recovery by itself creates:

Cash effect = 0

SupplierAccount effect = 0

Sales Revenue effect = 0

Customer financial effect = 0

Warranty supplier credit effect = 0


Do not invent a financial counterparty.


Its economics are:

inventory value restoration
+
InventoryLossRecoveryGain


subject to the source authority defined above.


=============================================================
14. SCHEMA REPRESENTATION GATE
=============================================================

Business policy is now RESOLVED.


Before implementing the recovery path:

perform one bounded representation inspection.


Determine whether existing:

InventoryMovement

InventoryMovementEffect

movement-unit linkage

InventoryUnit status/history

StockAdjustment facts

audit facts

operation outcome facts

reporting projections


can durably represent:

same-ID Found event

source Missing linkage

restored inventory value

InventoryLossRecoveryGain

once-only recovery

partial multi-unit recovery

repeated Missing/Found cycles


Do NOT edit yet during this representation inspection.


If existing persisted structures can safely represent all required facts:

record:

RECOVERY_SCHEMA_EXISTING_MODEL_SUFFICIENT

and continue to exact whitelist.


If they cannot:

STOP BEFORE CODE OR MIGRATION.


Return:

PASS5_BLOCKED_MIGRATION_DECISION


Document:

exact missing persisted fact

minimum schema delta

why existing structure is insufficient

backfill implications

compatibility implications

tests required


Do NOT generate migration without explicit authorization.


=============================================================
15. NO AUTOMATIC HISTORICAL REWRITE
=============================================================

Do not backfill Found recoveries.

Do not convert old positive adjustments into recovery gains.

Do not infer old Found events from present stock.


Historical Missing/Scrap classification strategy from Resolution02 remains
unchanged.


Operational legacy correction is NOT authorized here.


=============================================================
16. REQUIRED TESTS AFTER REPRESENTATION APPROVAL
=============================================================

Unit and PostgreSQL proofs must cover:

nonzero-value Missing → Found

zero-cost Missing → Found

fractional/high-precision carrying value

multi-unit Missing → one unit Found

multi-unit Missing → all units Found across separate operations

ambiguous history refusal

already-recovered loss refusal

concurrent Found

response-loss replay

failure before commit

failure after provisional SQL work

Missing → Found → Missing → Found again

recovered unit still Damaged/Defective

recovered unit becoming Sellable only when eligible


For every successful Found:

same InventoryUnit

same TrackingCode

same ItemSequence

no identity allocation

one stock restoration

one value restoration

one recovery gain where applicable

original loss unchanged

no Cash

no Supplier ledger

no fake Sales revenue


=============================================================
17. OWNER RECONCILIATION
=============================================================

The complete lifecycle must reconcile:


Before loss:

Inventory value = V


After Missing:

Inventory value decreases by V

RecognizedInventoryLoss increases by approved L


After Found:

Inventory value increases by same approved/restorable V

InventoryLossRecoveryGain increases by attributable approved L


Original loss remains visible.


At canonical reporting precision the complete commercial history must explain
the current owner position with no hidden balancing entry.


=============================================================
18. GOVERNANCE STATUS UPDATE
=============================================================

Update:

R02-RECOVERY-POLICY

from:

BLOCKED_BUSINESS_DECISION

to:

GOVERNANCE_RESOLVED


Approved authority:

InventoryLossRecoveryGain


R02-RECOVERY-01:

CONFIRMED_PASS5_GAP


Its implementation becomes eligible for the Pass5 edit whitelist only AFTER
the bounded schema-representation inspection succeeds.


=============================================================
19. RESUME POINT
=============================================================

DO NOT restart completed audit work.


Next:

1. Save this as:

EDGE_RETAILS_PHASE7_PASS5_GOVERNANCE_RESOLUTION_03.md


2. Hash it.


3. Perform bounded recovery representation inspection.


4. If existing schema is sufficient:

finish finding ownership

freeze exact edit whitelist

resume implementation of the existing retained Pass5 gaps

including C-P5-ARCH-01

C-P5-ARCH-02

Missing12

Found recovery


5. If representation is insufficient:

STOP at PASS5_BLOCKED_MIGRATION_DECISION.


6. After implementation:

focused tests

isolated PostgreSQL proofs

Golden traces

Owner reconciliation

protected regressions

candidate freeze

Pass5 manifest

ONE final verification wave

NEW independent Sol/High challenge

formal Pass5 lock only if independently authorized


7. STOP.


=============================================================
20. STILL PROHIBITED
=============================================================

DO NOT START:

Phase7 final closure

Phase12

Phase8

Phase9

POS pilot


DO NOT:

commit

push

reset

clean

deploy

touch operational data


=============================================================
FINAL GOVERNANCE DECISION
=============================================================

Found accounting classification:

InventoryLossRecoveryGain


Original Missing loss:

PRESERVED


Recovery recognition period:

CURRENT RECOVERY PERIOD


Inventory restoration basis:

EXACT AUTHORITATIVE VALUE DERECOGNIZED BY THE LINKED MISSING EVENT


Recovery gain basis:

ATTRIBUTABLE PREVIOUSLY RECOGNIZED LOSS


Maximum recovery:

NEVER EXCEEDS UNRECOVERED SOURCE AUTHORITY


Generic positive stock adjustment:

NOT AUTOMATIC PROFIT


Sales Revenue:

NO


Cash effect:

NO


Supplier effect:

NO


Same physical identity:

MANDATORY


Business decision gate:

RESOLVED


Schema requirement:

TO BE DETERMINED BY BOUNDED REPRESENTATION INSPECTION


PASS5 MAY RESUME FROM THE EXISTING CHECKPOINT AFTER THAT INSPECTION.