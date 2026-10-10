# Pass5 Resolution02 follow-up — Found recovery accounting decision

## Current verdict

**PASS5_BLOCKED_BUSINESS_DECISION**

The Missing representation decision is closed: **C-P5-ARCH-03 = GOVERNANCE_RESOLVED_IMPLEMENTATION_AUTHORIZED**. `InventoryUnitStatus.Missing = 12`, preservation of values1–11 and no enum-only/empty migration are approved by Resolution02. This report does not reopen that decision.

The new stop is the explicit recovery-economics gate in Resolution02 §§3/15: same-identity Found recovery is required, but the builder must STOP if its business accounting policy is not already frozen. Bounded follow-up found no existing policy for recognizing recovery of previously lost inventory. No source/test/harness/migration code has been edited, no new migration generated, no test/build/EF/database run started, and no operational data/services/deployment touched.

Resolution02 is preserved verbatim in `EDGE_RETAILS_PHASE7_PASS5_GOVERNANCE_RESOLUTION_02.md`; SHA256 `44DBD76F67EF49A6C7009CB40DDB05B4662F867DDE768D64AD2DCDD9125145D2`. All original authority, audit, contradiction and stop evidence remains preserved. Completed32-area audit was not repeated.

## R02-RECOVERY-01 — confirmed command gap

No canonical same-InventoryUnit Found/Recovery command was found in inspected live inventory handlers/controller/domain paths. State **CONFIRMED_PASS5_GAP**, High required lifecycle/exit impact, ownerA/lead withD profit/reconciliation consultation.

- `StockAdjustmentHandlers.cs:588–647` creates positive exact units through PhysicalUnitCreationAuthority. It allocates a new identity/sequence and origin; it does not recover the existing Missing identity. Calling it for Found would violate Resolution02. A duplicate serial claim is not a safe recovery protocol.
- `InventoryConditionHandlers.cs:270–308` requires current source bucket status and transfers retained bucket stock. Missing has no source bucket or current carrying value; this path cannot restore derecognized stock/value safely.
- Stocktake ExpectedAndFound is a count observation, not an authorized restoration of a previously posted Missing unit. Unposted count observations must continue leaving status/economics unchanged.
- Current generic `Other`/positive adjustment semantics require explicit cost treatment, but do not define a Found loss-compensation policy. Reason text alone must not invent a profit classification.

Absence of a command by itself is an implementation gap that can enter the whitelist. The separate unresolved economic choice below triggers the required stop before freezing/implementing that whitelist.

## R02-RECOVERY-POLICY — unresolved economic authority

1. Canonical architecture §§15/119 defines Lost removal/loss and controlled positive corrections, but does not classify later recovery of the same previously derecognized identity.
2. Canonical §121 allows restoring a still-owned Damaged/Defective asset to Sellable without revenue; that is not a previously removed Missing asset with a posted inventory loss.
3. Frozen Pass5 reporting policy adds only explicit approved operating recoveries and forbids automatically treating positive stock correction as Profit. Existing approved positive recovery is specifically **WarrantyRecoveryGain**, derived from actual SupplierCreditAmount minus resolved inventory cost (§114.5). It cannot be reused for Found with fictitious supplier credit.
4. `InventoryMovement` (`InventoryModels.cs:145–158`) stores RecognizedLossAmount but no typed inventory-loss-recovery or loss-reversal amount. `InventoryConfigurations.cs:76–85` has `ck_inventory_movement_loss_nonnegative`, **recognized_loss_amount >= 0**, with loss precision18,2. A negative loss is neither authorized nor valid under the present constraint.
5. `BusinessOperationsReadServices.cs:389–396` sums positive movement losses and subtracts them from NetProfit; trend at518–520 does the same. There is no existing Found recovery aggregate/projection. Editing the old loss to0 would erase historical truth; restoring only inventory value would not specify how the earlier loss and recovered economic asset reconcile.

Example for the policy decision: one unit with carrying value100 is approved Missing, value falls100 and loss100 is posted. Later the same unit is found. Identity reuse and quantity/value restoration are required, but authority must specify the **current recovery event's** profit/loss classification and permitted amount. Restoration must not silently delete/re-date the original loss, invent Sales/Cash/Supplier settlement, or count a new identity.

## Concrete governance options — none selected

|Option|Proposed rule|Consequence / additional proof|
|---|---|---|
|**A — current-period inventory loss recovery gain** (proposed)|An explicit authorized same-ID Found correction restores approved original derecognized carrying value and recognizes a separately classified InventoryLossRecoveryGain, never SalesRevenue; original loss remains intact|Requires explicit category/valuation/cap/allocation authority and durable once-only source linkage; reports add only that approved recovery once. Zero-cost or previously uncosted loss gives no invented gain|
|**B — compensating inventory loss correction**|A new explicit current-period compensating fact offsets the attributable prior loss while preserving the original loss row; report shows original loss and compensation separately|Requires a defined compensation authority, source linkage and amount rules. Current negative RecognizedLossAmount path is prohibited. Persisted representation must be inspected after policy choice; any required new fields/table/constraint remain separately migration-gated|

Both options must preserve original loss and identity history and can improve current-period NetProfit by the authorized amount, but they have different classification/reporting semantics. The builder is not authorized to choose between them. Neither permits changing original periods or generalizing profit recognition to ordinary positive/opening stock corrections. Neither authorizes a new supplier/warranty/GL engine.

## Minimum decisions to freeze

- Which classification governs Found: optionA orB, with an explicit approved name/meaning.
- Restoration basis and permitted monetary recovery: actual authoritative value derecognized by the linked Missing/loss event, treatment of zero-cost/uncosted identities, and rounding/caps for partially recovered multi-unit loss events. Historical AcquisitionCost alone cannot prove current value removed, particularly when the missing source was already zero-carrying.
- Attributable previous recognized loss versus restored carrying value when six-place inventory values and two-place loss totals differ; deterministic per-unit allocation/remainder and total cap must follow approved canonical money rules.
- Incomplete legacy provenance: require a controlled refusal/decision, not guessed cost or loss amount. Original loss source and identity/cost lineage must be provable. No actual operational legacy classification is performed in this pass.
- Durable linkage and exactly-once semantics: same InventoryUnit, prior approved missing/loss event, immutable recovery value/classification, actor/reason/correlation, one restoration and one economic recovery/compensation. Repeated Missing→Found cycles must link the correct latest approved loss event, not reuse an earlier already recovered loss.

Once policy is approved, inspect whether existing movement/effect/unit-link/lot/adjustment facts can store the chosen authority without schema change. Do not claim a migration is necessary or unnecessary before that bounded representation analysis. Resolution02 permits no new column/table or enum-only migration for Missing; it does not authorize arbitrary recovery financial schema. Any genuinely required persisted delta returns through the migration decision gate before code/migration.

## Required proofs after decision

- Same ID/TrackingCode/SerialIMEI/SupplierProduct/ItemSequence/claims/origin reused; no PhysicalUnitCreationAuthority allocation or high-water change.
- Explicit authorized movement/audit restores correct quantity/bucket/value only after an approved Found command; ordinary sale/return/PurchaseReturn/Thaka/warranty still reject Missing.
- Original loss remains; selected economic recovery/compensation recorded once and clearly separated from Sales/COGS/Cash/Supplier/Warranty income. Independently persisted owner reconciliation must explain the complete Missing→Found→ordinary-use lifecycle.
- Free0, nonzero/fractional carrying value, multi-unit partially found, previously zero-carrying source and incomplete/ambiguous history; no recovery exceeding approved source authority and no per-unit loss-allocation guess.
- Lost response, same/different-operation replay, concurrent Found, failure before commit and after provisional SQL flush; atomic rollback and one restoration/economic effect.
- Repeated loss/recovery lifecycle, preservation of original and later movement history, and old-binary downgrade refusal once12 exists.
- All unaffected locked guards, retained Scrap, P4-H1/H2 and Tracking source authority remain protected; only directly superseded assertions align under AUTHORIZED_ASSERTION_ALIGNMENT.

## Exact checkpoint

Completed: original32 bounded audit rows, finding ownership, immutable Resolution01/02, Missing12 authorization and this implementation-specific recovery follow-up. No audit restarted and no Pass4 regression rerun. The prior893/893 result is retained evidence, not a new run this turn.

Unfinished: precise edit whitelist; implementation of17 retained source gaps, Missing/historical-classifier/recovery work and approved financial reconciliations; required registry/matrix; all focused/PG/golden/owner/final-wave/fresh-independent gates. No Pass5 candidate or formal lock exists. Stop now under the explicit business-decision rule, preserving all work for continuation. No Phase7 final closure/Phase8/9/12/POS pilot or deployment is authorized.
