# Pass5 governance resolution — immutable addendum

Status: GOVERNANCE_AUTHORITY_ONLY; no implementation/certification claim.

Source attachment: C:\Users\muham\.codex\attachments\6eef1889-21a2-497e-9392-e6d251bc7392\Pasted text.txt
Source SHA256: C4A849694BDC8125B993027BC6EEDD26815BF86D2033C97AB15DD27EB158A998

Original frozen authority preserved unchanged: 0D93BF325DC475A34366D1AC1568D3BC65232216AA354C1F1E13C5728605D306.
This addendum supersedes only the conflicts explicitly resolved below. All other protected boundaries remain intact.

## Exact user governance resolution

# EDGE RETAILS — PHASE 7 PASS 5
# GOVERNANCE RESOLUTION FOR CONTROLLED ARCHITECTURE STOP

The prior verdict:

PASS5_BLOCKED_ARCHITECTURE_CONTRADICTION

was a CORRECT controlled stop.

Do not restart the audit from zero.

Preserve:

- Pass4 baseline attestation
- 893/893 source-manifest evidence
- frozen Pass5 authority SHA
- completed specialist audits
- contradiction evidence
- stop-state verification

The following governance decisions are now authoritative.


=============================================================
RESOLUTION C-P5-ARCH-01
PURCHASE RETURN + CASH SETTLEMENT
=============================================================

Canonical business facts remain distinct:

PurchaseReturn
=
goods/inventory return
+
supplier-credit effect

SupplierRefund
=
actual monetary settlement of supplier credit


However:

an immediate cash refund may be executed atomically in the SAME user operation
and SAME PostgreSQL transaction as the PurchaseReturn.

Therefore the approved architecture is:

PurchaseReturn
+
canonical SupplierRefund fact
+
SupplierAccount effects
+
CashMovement where cash method
+
Audit

all atomically when the operator explicitly selects immediate refund.


Do NOT represent this merely as an anonymous SupplierRefundReceived ledger row
if the current canonical SupplierRefund authority requires a durable refund
business fact/aggregate.

Inspect the live canonical refund implementation and reuse it.

Do not create a second refund engine.


Replay must produce exactly:

1 PurchaseReturn
1 canonical SupplierRefund
1 appropriate cash effect
1 balanced canonical supplier-account effect


No duplicate effect after response-loss replay.


Existing Pass1 tests may be changed ONLY where their old assertion encoded the
superseded provenance behavior.

Classification:

AUTHORIZED_ASSERTION_ALIGNMENT

Do not weaken their accounting, cash, replay or persisted-state assertions.


=============================================================
RESOLUTION C-P5-ARCH-02
PURCHASE VOID + INITIAL PAYMENT
=============================================================

Purchase Void and payment correction are separate business authorities.


DEFAULT PURCHASE VOID:

reverses Purchase business/liability effects allowed by canonical void rules

does NOT automatically assume previously paid cash was returned

does NOT automatically create CashIn merely because Purchase was voided


If the payment actually occurred:

the payment remains historical financial fact

the resulting supplier credit is settled later through canonical
SupplierRefund when money is actually returned.


If the original payment posting itself was erroneous and no real payment
occurred:

use explicit canonical SupplierPaymentReversal.


Therefore:

PurchaseVoid
!=
automatic PaymentReversal

PurchaseVoid
!=
automatic SupplierRefund


A composite UX may perform:

PurchaseVoid
+
explicit SupplierPaymentReversal

only when the operator deliberately declares that the payment posting itself
was erroneous and backend eligibility permits that correction.


Never infer this automatically from Purchase Void.


Any old protected Pass1 assertion requiring unconditional payment/cash
reversal solely because the Purchase was voided is superseded by this Pass5
business authority.

Such test changes must be classified:

AUTHORIZED_ASSERTION_ALIGNMENT

All unaffected Pass1 guarantees remain protected.


=============================================================
RESOLUTION C-P5-ARCH-03
STOCKTAKE SHORTAGE / MISSING EXACT UNIT VS SCRAP
=============================================================

SCRAPPED and MISSING/SHORTAGE are not the same physical state.


Actual Scrap:

physical unit is known/identified
and intentionally disposed/scrapped according to canonical policy.


Stocktake shortage / missing unit:

expected physical identity is NOT physically present.


For a missing/shortage exact unit:

StockBalance contribution = 0

OnHand contribution = 0

Sellable contribution = 0

carrying value = removed exactly once

RecognizedInventoryLoss = recorded exactly once

physical identity history = retained

terminal/non-sellable state = required

Scrap bucket contribution = 0


Do NOT increase retained Scrap quantity for a physically missing unit.


Before implementation inspect the LIVE domain for an already-valid
terminal/loss/missing/adjusted-out state.


If such canonical state exists:

REUSE IT.


If current schema/domain cannot represent the required state without a new
persisted enum/status/value:

STOP BEFORE CODE OR MIGRATION.


Return:

PASS5_BLOCKED_MIGRATION_DECISION


Document the minimum schema/domain delta.

Do NOT generate a migration without explicit authorization.


=============================================================
AUTHORITY PRECEDENCE
=============================================================

These three governance resolutions are explicit Pass5 authority.

Where an inherited Pass1 test directly contradicts one of these resolutions:

do NOT treat the old assertion as immutable authority.

Change ONLY the conflicting assertion/workflow under:

AUTHORIZED_ASSERTION_ALIGNMENT

and preserve all unrelated Pass1 guarantees.


Do not reopen Pass1 generally.

Do not restart Pass4.

Do not alter the Pass4 manifest/evidence.


=============================================================
RESUME ORDER
=============================================================

1. Record this governance resolution in the Pass5 audit evidence.

2. Continue the INTERRUPTED READ-ONLY AUDIT from the saved checkpoint.

3. Complete all remaining 32 authority rows.

4. Launch the previously unlaunched bounded specialist D if still required.

5. Determine whether C-P5-ARCH-03 can be represented with existing live
   domain/schema.

6. Finish finding ownership classification.

7. Only then freeze the exact Pass5 edit whitelist.

8. Implement confirmed Pass5 gaps plus these approved reconciliation changes.

9. Run focused tests.

10. Run required isolated PostgreSQL proofs.

11. Run Golden Business traces.

12. Run Owner reconciliation.

13. Freeze Pass5 candidate.

14. Generate Pass5 final source manifest.

15. Run ONE coordinated final verification wave.

16. Dispatch ONE NEW independent Sol/High read-only final challenger.

17. Formal Pass5 lock only if independently authorized.

18. STOP.


DO NOT START:

Phase7 final closure
Phase12 implementation
Phase8
Phase9


DO NOT COMMIT.
DO NOT PUSH.