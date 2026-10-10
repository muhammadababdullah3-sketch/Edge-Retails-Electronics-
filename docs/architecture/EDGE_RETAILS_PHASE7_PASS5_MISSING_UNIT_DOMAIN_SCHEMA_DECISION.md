# Pass5 governance follow-up — missing exact inventory representation

## Required verdict

**PASS5_BLOCKED_MIGRATION_DECISION**

Governance resolution01 resolved the prior purchase-return and purchase-void contradictions. Those authorized reconciliation changes remain unimplemented. Resolution C-P5-ARCH-03 expressly requires STOP BEFORE CODE OR MIGRATION if a new persisted status/value is necessary. Independent bounded inspection of the live domain confirms that condition. No production/test/harness/migration code has been changed; no new migration generated; no EF/build/test/PostgreSQL command executed for Pass5.

This report is the concrete decision package required by the resolution, not an environment blocker or permission to use a misleading existing state. The prior frozen authority, Pass4 manifest/evidence, specialistA/B/C audits and original contradiction/stop evidence remain preserved. Governance resolution is captured verbatim in `EDGE_RETAILS_PHASE7_PASS5_GOVERNANCE_RESOLUTION_01.md` (SHA256 `0CEA822BF390A4FDA0FDC3330FB4A9C1FD584A34FFCD51C9405E3E7AAFFB02A9`).

## Proven missing fact

A known exact physical identity can be absent after posted stocktake or explicit Lost adjustment. It needs a terminal, non-sellable state contributing zero owned stock/OnHand/Sellable/Scrap and zero current carrying value, while preserving identity history and recording removal/loss once.

`InventoryModels.cs:14–26` defines only11 InventoryUnitStatus values. Terminal values are SupplierReturned7, Scrapped8 and ReceiptVoided9. Accounting policy at605–633 makes Scrapped contribute Shop/Scrap, whereas SupplierReturned/ReceiptVoided describe different events. Reusing them would falsify provenance. The separate stocktake-check result Missing is only an observation and cannot supply the unit's terminal accounting rule. Existing customer/Thaka states likewise do not represent loss.

StocktakeHandlers.cs:987–995 and StockAdjustmentHandlers.cs:760–770 currently use Scrapped after source-only derecognition with no Scrap destination. Governance forbids fixing that by creating physical Scrap. Retained identified Scrap behavior must remain unchanged.

## Minimum proposed delta — requires explicit approval

| Item | Proposal | Boundary |
|---|---|---|
|Domain/persisted status|One new explicit value, proposed **InventoryUnitStatus.Missing = 12**|Preserve values1–11; no repurposing or renumbering|
|Accounting rule|No StockBalance or ProductCostState contribution; null bucket; not sellable/returnable; terminal; proposed existing ExternalOrHistorical classification for derecognized identity|Governance must confirm that classification; do not invent a supplier/customer transfer|
|New writes|Stocktake exact shortage and explicit Lost exact adjustment terminalize existing identity to Missing|Preserve code/ID/sequence/claims/origin/acquisition; one removal/loss; no Scrap quantity|
|Current physical schema|Units.status and movement_units from/to_status are PostgreSQL integer; source snapshot/config/migration inspection found no status-list CHECK|No new column or Npgsql enum DDL is demonstrated necessary; this is schema-source evidence, not an inspected operational catalog|
|EF migration|Determine only after approved data/check strategy|A new persisted value still requires approval even if EF model produces no column change; do not generate an empty migration merely to bypass the gate|
|Optional constraint expansion|Only if specifically approved, narrow admissible-status constraints after legacy profiling|Not part of minimum default proposal; no unrelated constraints|
|Reader/writer compatibility|Update enum policy and narrowly affected projections with shortage writers together|Older binary policy rejects12; cannot roll back binary blindly|

Do not generalize Missing to all negative Other/Damaged corrections. Their physical disposition can be ambiguous. No new Tracking creation path, Supplier, Purchase, DealerCode, identity claim, scanner order or sequence primitive is needed.

## Historical data decision

**No blanket status8 conversion.** Retained physical Scrap and missing identities currently share it. Cost0 or a zero lot balance alone does not distinguish them.

Candidate proof for a stocktake reclassification requires a posted negative stocktake, exact matching Missing check, product/unit movement links, negative source effect, matching cost/lot consumption, no positive Scrap destination and no later contradictory transition. Explicit Lost adjustment can be a conditional candidate with equivalent durable proof. Free-cost units may have legitimate loss0. Generic Other/Damaged, incomplete joins, shared-lot ambiguity or colliding history must be reported for review rather than guessed.

An approved backfill must preserve original commercial/movement history, create durable before/after reclassification evidence and change only conclusively identified current state. It must not replay stock removal, add a second loss, rewrite Tracking identities or create balancing Scrap. Historical A-01 unrecognized-loss correction is a separate monetary issue and must not silently piggyback on a status backfill.

No operational database was queried, so historical eligible/ambiguous row counts are unknown. A production backfill or deployment is not requested or authorized by this Pass5 decision package. The first implementation/rehearsal remains isolated PostgreSQL18/Npgsql.

## Upgrade and rollback

After value12 exists, old application policies/readers cannot be assumed compatible. Upgrade the complete status readers/writers under existing single-install authority. A rollback mapping12 to Scrapped recreates the original wrong-stock meaning; mapping to SupplierReturned/ReceiptVoided falsifies provenance. A safe plan must refuse incompatible downgrade or retain compatible readers and roll forward. Reclassification rehearsals may use an exact captured before-state mapping only where no later business events exist; never delete new history to restore old compatibility.

## Required proofs following authorization

- Unit policy: every status covered; values1–11 unchanged; Missing terminal/zero contributions; retained Scrapped still Shop/Scrap and zero carrying value; unknown enum fails closed.
- Real PostgreSQL18/Npgsql shortage/Lost tests for Serialized, IndividualPiece and intact Container: exact BaseQuantity/cost/loss once; original identity/source/sequence unchanged; stock/lot/OnHand/Sellable/Scrap0; no sale/refund/supplier fiction.
- Response-loss replay and concurrent posting: one transition, one derecognition/loss and stable pair high-water; atomic rollback before/after SQL flush.
- Side-by-side independently persisted owner reconciliation for Missing versus retained Scrap, supplier-held shop stock and customer-owned replacement units.
- Mixed-history isolated upgrade/reclassification rehearsal: conclusive candidates only; ambiguous cases fail safely; idempotent evidence mapping; no monetary/identity/sequence rewrite; actual model/EF alignment and approved downgrade-refusal/recovery proof.
- Only directly superseded shortage destination assertions may change under AUTHORIZED_ASSERTION_ALIGNMENT. Preserve all identity, quantity, value, loss and replay guarantees plus actual retained-Scrap tests, P4-H1/H2 and unrelated locked Pass1/2.

Detailed independent source/schema/history references, safe predicates, ambiguity cases and required proof paths: `artifacts/phase7-pass5/audit-A-governance-03-addendum.md`.

## Decision required and exact resume point

Governance must authorize the new persisted Missing value/accounting classification and a bounded historical-data/compatibility strategy (including whether any EF data migration or constraints are approved). Until then **no Pass5 code/test/migration implementation or edit whitelist is authorized**. Completed remaining read-only audit evidence may be integrated and retained.

After approval: retain all32 bounded audit rows, finalize finding ownership and exact whitelist, implement only confirmed gaps and explicitly approved reconciliations, then focused/golden/owner/full-regression/freeze/fresh-independent gates. No acceptance, formal lock or next phase is claimed here.
