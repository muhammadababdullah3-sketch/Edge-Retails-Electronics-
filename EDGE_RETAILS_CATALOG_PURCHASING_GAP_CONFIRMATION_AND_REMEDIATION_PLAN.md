# EDGE RETAILS — Catalog and Purchasing Gap Confirmation and Remediation Plan

**Audit date:** 2026-10-04  
**Mode:** STRICT READ-ONLY; remediation design, not implementation or runtime certification.  
**Workspace:** C:\Users\muham\OneDrive\Desktop\Point of Sale  
**Branch:** tracking-remediation-20261002  
**HEAD:** 97956831e01d7ed6b4e2f1051b15e8ec272e8e4b  
**Final verdict:** **REMEDIATION_PLAN_READY_WITH_DEFERRED_ITEMS**

## 1. Decision and assurance boundary

The existing relational catalog and purchasing hierarchy is suitable for the bounded V1 workflow. Preserve Category and Unit, optional Company, Product with Model text/ModelCode/SKU/policies, ProductUnits, many-to-many SupplierProduct, Purchase, and physical intake inheriting Purchase.SupplierId. No separate Model master or supplier selection at receipt is justified.

The current implementation is **not gap-free**. Seven bugs, six UI gaps and one attribute-governance design gap are confirmed. G05 is CRITICAL because fractional entered Container quantities can produce a whole stock quantity with too few physical identities. G01–G04 and G11 are HIGH due to partial catalog persistence or credible stock/cost errors. G06 has a safe database uniqueness barrier and is MEDIUM. Two items are deferred, two are documentation corrections and two alleged gaps are false positives.

This is source confirmation, not proof that a particular live database contains affected records. No database was accessed, no tests/builds were run, and no fixes were applied. Exact behavior examples below are reasoned from inspected code. Previous certification documents are inherited evidence, not tests performed by this audit. The verdict authorizes no implementation automatically; it means the bounded contracts can be handed to implementation owners with the stated gates.

Only this report was written for the current request. Existing dirty files and concurrent work were preserved.

## 2. Authority, evidence and Phase 7 boundary

| Authority | Use in this audit |
|---|---|
| [Architecture authority manifest][manifest] | Identifies the canonical V1 authority; avoids treating every older architecture document as an equal contract. |
| [Canonical architecture][canonical] | Quantity snapshots, effective costs, immutable provenance, atomic ORIGINAL print intent and Annex 233.1C attribute governance. |
| [Current source-derived catalog workflow][workflow] | Primary workflow baseline; source takes precedence for whether implementation actually follows the contract. |
| [Tracking certification and freeze][trackingfreeze] | Preserves PUCA issuance, supplier/product sequence, manufacturer identity, intact-pack provenance and printing without new identity allocation. |
| [Phase 7 ownership and boundary freeze][boundary] | Separates Phase 7, Phase 12 receipt recovery and future Phase 9 printing responsibilities. |
| [Phase 7 Pass 3 certification and lock][phase7lock] | Reports PASS3_CERTIFIED_CLOSED_LOCKED and limits intentional production edits to five files. |
| [Phase 7 source manifest][phase7manifest] | Distinguishes preserved baseline files from intentional Pass 3 production edits. |

Canonical SHA-256 checked during this audit:
`12344760C60124DDC2D1C0E54ABFB7BC0E82B9B23A46E6463303EBFA530AB673`.

The five intentional Pass 3 production files are VoidPurchaseHandler.cs, CommercialExchangeHandler.cs, PosDraftHandlers.cs, BusinessOperationsReadServices.cs and EdgeRetailsDbContext.cs. All five currently match their recorded manifest hashes. The minimal changes proposed here do not require editing those five files. Listing another file in the broad source manifest does not prove active Phase 7 ownership.

A read-only app-status observation showed Backend Execuations idle and Frontend active. The inspected Frontend work was focus/accessibility work in shared Inputs resources, Shell, POS and sale dialogs; it explicitly excluded backend work. The catalog/purchasing dialog changes proposed here should avoid those shared resources. This observation is point-in-time, not a reservation of files.

**Phase7 Conflict = NO** in the matrix means no demonstrated current co-edit conflict for the minimal fix, not permission to bypass the freeze. Before implementation, recheck owner status and hashes. If Phase 7 is actively editing any proposed production file, use **WAIT_FOR_PHASE7_SOURCE_FREEZE**. ReceiveProductIntakeHandler and receipt recovery remain coordinated with Phase 12; preserve their operation fingerprints and outcome recovery. Do not add generic DbContext guards merely to broaden this task into an intentional Phase 7 file.

## 3. Preserved architecture and database opinion

The database already represents the essential authorities: stable Product identity, distinct UOM mappings, SupplierProduct pair uniqueness, frozen commercial quantity/cost snapshots, inventory lots, physical identities and durable operation outcomes. Retain PostgreSQL and these relational boundaries. The confirmed problems predominantly concern transaction orchestration, validation and consumption of existing snapshots, rather than absence of tables.

The correct flow is:

```text
Category + Unit
       └── optional Company
                └── Product: Model text, ModelCode, SKU, tracking policy
                         ├── ProductUnits: conversion and permitted uses
                         └── SupplierProduct: many-to-many provenance/sequence
Supplier ───────────────────────┘
       └── Purchase: supplier + frozen commercial lines/costs
                └── Receipt: inherit supplier; consume purchase snapshots
                         └── Stock / lots / physical units / tracking
```

A 56-inch ceiling fan and a 48-inch fan are separate stocked Products if their quantities/prices differ, even when their model family text matches. A cable sold by metre can use Length and a roll UOM of 90 metres. A sealed carton of switches requiring one identity per intact carton can use Container. Buying bulbs in boxes does not alone require Container: Quantity with box-to-piece conversion covers bulk loose-piece stock.

Keep stock, supplier identity, valuation, warranty and tracking invariants relational. Use existing AttributesJson/AttributesSchemaVersion for descriptive electrical specifications with typed validation. No blanket denormalization, Model table, supplier-name uniqueness, or tracking redesign is required.

## 4. Final G01–G20 matrix

File references expand to absolute workspace links in the evidence sections and reference index. Severity for deferred/non-defect items describes the candidate risk, not a newly confirmed corruption event.

| ID | Finding | Status | Severity | Real Risk | Exact Fix | Files | Migration | Owner | Phase7 Conflict |
|---|---|---|---|---|---|---|---|---|---|
| G01 | Product save spans separately committed mutations | CONFIRMED_BUG | HIGH | Active Product/base mapping persists after unit/link failure; partial supplier sync; retry reports ambiguous save | One SaveProductAggregate mutation and PostgreSQL transaction; durable operation ID/fingerprint; committed-ID result independent of refresh | [Product handlers][producthandlers], [unit handlers][unithandlers], [local catalog adapter][localcatalog], [remote catalog adapter][remotecatalog], [Catalog API][catalogapi], [Product editor][productvm] | NO_MIGRATION_REQUIRED | Catalog | NO |
| G02 | Used ProductUnit conversion factor remains editable | CONFIRMED_BUG | HIGH | Outstanding orders can be interpreted against a different conversion; pack provenance becomes inconsistent | Freeze factor after commercial/stock history, including unreceived Purchase; serialize against first use; distinct Unit/mapping for a new pack definition | [Unit handlers][unithandlers], [catalog model][catalogmodel], [history service][historyservice] | NO_MIGRATION_REQUIRED | Catalog | NO |
| G03 | Deferred intake reads current factor instead of order snapshot | CONFIRMED_BUG | HIGH | Fewer rolls can complete an order at the wrong base quantity | Resolve exact PurchaseItem; require ordered ProductUnit; use frozen factor/base authority; controlled failure for invalid snapshot | [CreatePurchase][createpurchase], [intake handler][intake], [intake editor][intakevm], [purchasing adapters][localpurchase] | NO_MIGRATION_REQUIRED | Purchasing | NO |
| G04 | Ordinary receipt overrides effective cost with raw cost | CONFIRMED_BUG | HIGH | Allocated charges omitted from inventory carrying value and cost state | Ordinary receipt uses frozen EffectiveBaseUnitCost; remove editable override and reject unauthorized overrides server-side | [intake handler][intake], [intake editor][intakevm], [CreatePurchase][createpurchase], [remote purchasing][remotepurchase] | NO_MIGRATION_REQUIRED | Purchasing | NO |
| G05 | Fractional Container quantities can truncate physical count | CONFIRMED_BUG | CRITICAL | 1.5 packs at factor 2 yields 3 base pieces but only one physical pack identity | Validate positive whole entered pack count before rounding/casting at shared conversion and both purchase/intake boundaries; bound count; enforce valid per-pack conversion | [catalog model][catalogmodel], [unit handlers][unithandlers], [CreatePurchase][createpurchase], [intake handler][intake] | NO_MIGRATION_REQUIRED | Inventory | NO |
| G06 | Manual first SupplierProduct link lacks canonical pair lock | CONFIRMED_BUG | MEDIUM | Concurrent first links fail at unique barrier instead of controlled success/conflict | Acquire existing supplier-product advisory lock before lookup inside transaction; preserve uniqueness and cursor | [Product handlers][producthandlers], [alignment repository][alignmentrepo], [PUCA][puca], [trace configuration][traceconfig] | NO_MIGRATION_REQUIRED | Catalog | NO |
| G07 | Serialized mode omitted; physical overlays cleared by UI | CONFIRMED_UI_GAP | MEDIUM | Supported mode unavailable; edit payload can discard valid identity policy | Expose all five modes; require identity flag for Serialized; preserve optional IndividualPiece/Container flags | [Product editor][productvm], [Product dialog][productdialog] | NO_MIGRATION_REQUIRED | Desktop UI | NO |
| G08 | Order-only purchase asks for identities captured again at receipt | CONFIRMED_UI_GAP | MEDIUM | Duplicate entry and blocked deferred orders | Expose adapter receipt mode; order-only captures commercial data; immediate receipt retains identity validation | [Purchase editor][purchasevm], [remote purchasing][remotepurchase], [local purchasing][localpurchase] | NO_MIGRATION_REQUIRED | Desktop UI | NO |
| G09 | Lookup results are truncated and searched only in memory | CONFIRMED_UI_GAP | MEDIUM | Products/suppliers beyond caps cannot be found; stock lookup can be misleading | Server search plus existing name/ID cursor; cancellation/debounce; retain ID selections across pages | [remote purchasing][remotepurchase], [remote catalog][remotecatalog], [Purchase editor][purchasevm], [Catalog API][catalogapi], [Suppliers API][suppliersapi] | NO_MIGRATION_REQUIRED | Desktop UI | NO |
| G10 | Supplier selection uses name identity and excludes duplicates | CONFIRMED_UI_GAP | MEDIUM | Legitimate same-name suppliers disappear or are ambiguous | Typed SupplierId selection; Name/City/Phone display with short-ID fallback; optional DealerCode projection | [Purchase editor][purchasevm], [Purchase view][purchaseview], [party directory][partydirectory], [purchasing adapters][localpurchase] | NO_MIGRATION_REQUIRED | Desktop UI | NO |
| G11 | Different UOM selections merge by ProductId | CONFIRMED_BUG | HIGH | Metres become rolls through silent quantity accumulation | Preserve backend one-line-per-Product rule; merge only identical ProductUnit; explicitly reject/replace alternate UOM without changing quantity | [Purchase editor][purchasevm], [CreatePurchase][createpurchase] | NO_MIGRATION_REQUIRED | Desktop UI | NO |
| G12 | Deferred order success claims stock updated | CONFIRMED_UI_GAP | LOW | Operator assumes goods are available before receipt | Result-mode message: purchase order saved; receive stock from Purchase Detail | [Purchase editor][purchasevm], [remote purchasing][remotepurchase] | NO_MIGRATION_REQUIRED | Desktop UI | NO |
| G13 | Explicit DealerPrefix fallback absent from supplier UI | CONFIRMED_UI_GAP | MEDIUM | Urdu/short supplier names cannot complete valid creation | Error-driven/fallback two-letter ASCII prefix; thread through adapters and operation fingerprint; never edit assigned DealerCode | [Supplier dialog][supplierdialog], [Supplier editor][suppliervm], [party handlers][partyhandlers], [Suppliers API][suppliersapi], [remote party adapter][remoteparty] | NO_MIGRATION_REQUIRED | Desktop UI | NO |
| G14 | Attributes validation lacks version/key/type/range governance | CONFIRMED_DESIGN_GAP | MEDIUM | Invalid specifications/unknown schema silently accepted contrary to Annex C | Versioned typed electrical profiles in existing JSON fields, explicit compatibility/extension rules; no Model master | [catalog model][catalogmodel], [Product handlers][producthandlers], [canonical Annex C][attributesauthority] | NO_MIGRATION_REQUIRED | Catalog | NO |
| G15 | Tracked intact-container opening/splitting unsupported | DEFER_FUTURE | LOW | Only a gap if traceable pack-to-loose transformation is required | Keep intact-only V1; use bulk Quantity/Length for loose goods; future explicit conserved transformation if requested | [catalog model][catalogmodel], [intake handler][intake], [Tracking freeze][trackingfreeze] | NO_MIGRATION_REQUIRED | Future V2 | NO |
| G16 | Receipt does not atomically persist ORIGINAL label intent | DEFER_PHASE9 | HIGH | Crash after receipt can lose automatic print delivery intent | Phase9 atomically persists logical ORIGINAL artifact; dispatch after commit to workstation with unknown-outcome reconciliation | [intake handler][intake], [intake editor][intakevm], [print handler][printhandler], [outbox model][outboxmodel], [printing API][printingapi] | NOT_ENOUGH_EVIDENCE | Phase 9 | NO |
| G17 | Alleged reachable mutation of frozen tracking identity | FALSE_POSITIVE | LOW | No violating production mutation path established; universal SQL-level immutability not certified | Preserve current history guards/PUCA snapshots; document limits; do not redesign Tracking or alter DbContext | [Product handlers][producthandlers], [reference handlers][referencehandlers], [history service][historyservice], [PUCA][puca], [DbContext][dbcontext] | NO_MIGRATION_REQUIRED | Tracking | NO |
| G18 | Earlier catalog documentation differs from live fields/modes | DOCUMENTATION_ONLY | LOW | Developers use superseded field/mode descriptions | Reconcile authority appendix with actual fields and five modes; preserve historical sections with precedence note | [canonical][canonical], [catalog model][catalogmodel], [workflow][workflow] | NO_MIGRATION_REQUIRED | Documentation | NO |
| G19 | Supplier UpdatedAt documented but not mapped | DOCUMENTATION_ONLY | LOW | Consumers assume nonexistent timestamp | Correct document to CreatedAt/Version and actual audit contract; do not add column just to match prose | [canonical supplier section][supplierauthority], [party model][partymodel], [party configuration][partyconfig] | NO_MIGRATION_REQUIRED | Documentation | NO |
| G20 | Alleged missing supplier re-selection at receipt | FALSE_POSITIVE | LOW | Re-selection would introduce wrong commercial/physical provenance | Keep Purchase.SupplierId as receipt authority and supplier display read-only | [intake handler][intake], [intake editor][intakevm] | NO_MIGRATION_REQUIRED | Purchasing | NO |

## 5. Integrity contracts: G01–G06 and G11

### G01 — Save the catalog aggregate atomically

**Specialized classification:** CONFIRMED_DATA_INTEGRITY_BUG, represented by CONFIRMED_BUG in the prescribed matrix.

[ProductEditViewModel:584][saveui] calls one service save and discards the returned entity identity before completing its flow. Its failure path at 629–638 leaves the operator able to try again. [BackendProductManagementService:333][localsave] and 379–393 call product create/update, unit configuration, supplier synchronization and read-back separately, without an enclosing aggregate transaction. [RemoteProductManagementService:107][remotesave] and 132–138 make separate HTTP mutations; supplier synchronization at 217–233 can commit earlier links before a later link fails.

Each Application handler owns its own transaction: Product creation at ProductManagementHandlers:263–309 persists an active Product and base mapping; ConfigureProductUnits and SetSupplierProductActive commit separately. A shared DI scope is not a shared transaction. Failed unit configuration can therefore leave an active, incomplete Product. Failed supplier synchronization can leave only part of the requested desired link set. Read-back failure after committed writes can also be reported as save failure.

**Chosen solution A:** a single Application/server SaveProductAggregate command. Local and remote adapters both invoke it once. It carries Product draft, expected Product.Version for update, full intended unit configuration, intended supplier-link set, actor context and stable ClientOperationId. One PostgreSQL transaction applies all changes or none. Internal helpers must propagate failure and must not independently commit beyond the outer transaction. Perform reference/permission validation before mutation where possible.

Existing OperationOutcome fields include operation type, fingerprint, result entity ID and commitment state; its configuration already uniquely indexes ClientOperationId. Reuse that schema with a catalog operation type, durable same-payload replay and payload-mismatch rejection. The existing ledger API is storage support, not sufficient admission protection by itself: callers must compare type/fingerprint before resetting pending state, serialize competing same-ID requests with the existing transactional resource-lock primitive, and recover unique conflicts only after rollback where needed.

Successful replay returns the same ProductId and the committed operation outcome. Read the current entity separately if necessary; do not claim the replay version is an historical version that was not stored. Return committed ProductId/version from the original write, and distinguish “saved; refresh unavailable” from “save failed.” A refresh failure never authorizes a second create.

**Rejected B:** adding only a local orchestration transaction leaves the multi-HTTP remote path unsafe. **Rejected C:** compensating delete/retry cannot safely undo a Product already referenced by another workflow. No distributed transaction is needed.

### G02 — Lock conversion semantics after first authoritative use

[ProductUnitHandlers:136][factorupdate] updates existing mappings, including FactorToBaseUnit at 174, without a history lock. ProductUnit has no concurrency Version. [CatalogConfigurations:170][unitunique] enforces a unique ProductId/UnitId pair, including inactive mappings.

A factor must become immutable no later than the first commercial reference, including an unreceived PurchaseItem, or stock/movement/physical provenance. Never rewrite historical factor/base/cost snapshots. The history boundary should include purchases, sales/returns, Thaka, adjustments/movements, lots/units and stocktake references where they preserve that mapping. Existing Product history checks are conservative, including SupplierProduct; reusing that broader policy is safe if intentionally documented. Toggle purchase/sale/default flags independently, while guarding inactivation when pending documents need the mapping.

The source distinguishes new conversion from historical interpretation: ThakaHandlers:370 creates a quantity snapshot; ThakaReversalHandlers uses stored BaseQuantity. PurchaseReturnHandler:249 and 381 uses the purchase factor snapshot. SaleReturnHandler:397–407 uses the sale snapshot. StocktakeHandlers:551 and 949 retrieves physical base-quantity provenance. Do not replace these historical consumers with current factors. G03 identifies the deferred receipt exception.

Use a shared Product resource lock and appropriate row lock, with lock order consistent with PUCA and first commercial use. Configure-only mutations must advance/validate Product.Version as the aggregate revision; this avoids inventing a ProductUnit Version column. A newly created transaction cannot race past the history check.

For genuinely different definitions, create distinct Units such as “Roll 90 m” and “Roll 100 m,” with distinct mappings to the same Product. Creating a second mapping for the **same** ProductId/UnitId is forbidden by current uniqueness. True revisioned mappings for the same Unit would require a separately approved schema/uniqueness/barcode design; they are not required by the bounded fix. Base-unit factor remains 1.

### G03 — Receipt follows the purchase quantity snapshot

[CreatePurchaseHandler:363][ordersnapshot] freezes factor and base quantity. Intake finds the purchase line by ProductId at [ReceiveProductIntakeHandler:255][intakeline], but reads the current ProductUnit at 442–448 and creates a new snapshot from it at 481–485. Its outstanding limit at 505–511 uses the original order base quantity. This mixes two authorities.

Example: order 10 cable rolls × 90 m = 900 m. Change current mapping to 100 m. Receiving 10 rolls fails the outstanding check at 1,000 m; receiving 9 rolls can pass at 900 m and exhaust the order even though only nine ordered packs arrived.

Normal receipt must identify the exact PurchaseItem and its ProductUnitId; current one-line-per-Product constraints make this resolvable without schema change. Require the ordered mapping, otherwise proposed error `purchasing.intake_unit_mismatch`. Calculate base quantity from the frozen factor and validate the frozen ordered base quantity. Invalid/nonpositive/inconsistent snapshots yield `purchasing.purchase_snapshot_invalid`; do not substitute a live factor or 1.

An inactive mapping must produce a controlled eligibility result rather than a conversion substitution. Prevent deactivating pending-order mappings in normal configuration, or explicitly authorize historical receipt while retaining its frozen semantics. Different receipt UOM is a separate conversion workflow, not an implicit ordinary-intake capability.

### G04 — Receipt follows effective order cost

[Canonical effective-cost rules:5883][costauthority] allocate other charges into effective line and base cost. CreatePurchaseHandler:350–370 persists raw and effective cost separately. The purchasing read model exposes both, but [PhysicalIntakeViewModel:443][costpayload] sends raw item.Cost as a non-null override. [Intake:487][intakecost] selects that override divided by the factor instead of the frozen effective base cost. The resulting cost reaches movement, lot carrying value, physical acquisition cost and latest product cost state at 528–669.

Ten units with raw cost 1,000 each and allocated charges 100 each require carrying value 11,000, effective base cost 1,100. Ordinary UI receipt currently selects 10,000/1,000 through the override. This is a normal-workflow financial defect, not proof of changed supplier liability.

Make ordinary receipt cost read-only and use PurchaseItem.EffectiveBaseUnitCost. The UI sends no override; the backend must reject unauthorized supplied overrides, using proposed `purchasing.receipt_cost_override_not_allowed`, rather than trusting that every client sends null. Removing the field in a versioned ordinary-receipt contract is also valid.

An explicit cost correction belongs to an authorized, audited accounting workflow that defines supplier liability, stock revaluation, sold COGS and rounding. Do not build it as a prerequisite to this fix. Partial receipts must conserve the order's allocated total under existing cost precision and final residual policy. Preserve existing receipt intent fingerprints: do not normalize an unresolved old request into a different payload under the same operation ID.

### G05 — Container means an integer number of physical packs

[ProductUnit.ToBaseQuantity:144][quantitypolicy] requires positive entered quantity and whole resulting base quantity for physical modes, but does not require whole **entered** Container quantity. [CreatePurchase:674][purchasecount] and [Intake:563][intakecount] cast entered quantity to an integer for Container identities. Deferred purchase bypasses immediate identity validation at 678–683.

| Entered packs, factor 2 | Current source behavior | Required behavior |
|---|---|---|
| 1 | 2 base pieces, 1 identity | Accept |
| 2 | 4 base pieces, 2 identities | Accept |
| 1.5 | 3 whole base pieces; integer count becomes 1; can create one pack identity for all 3 | Reject before mutation |
| 0.5 | 1 whole base piece; deferred order can pass; receipt count becomes 0 and later rejects | Reject at order and receipt admission |

Factor 1 masks fractional entered quantities because resulting base quantity is fractional. Factor 3 rejects 1.5 through 4.5 base quantity. Therefore testing only factor 1 falsely suggests the invariant is already protected.

Proposed shared rule: entered Container quantity is positive and exactly whole **before rounding/casting**; failure `catalog.container_quantity_whole`. Physical count must fit supported integer/allocation limits; failure `purchasing.physical_unit_count_out_of_range`. Apply the rule to immediate/deferred purchase and direct receipt, not just desktop validation.

**Factor decision:** current code permits positive decimal Container factors when the *total* base quantity happens to be whole, for example two packs × 0.5. That does not establish that each tracked pack has a valid whole base quantity. The minimal V1 policy for Container under the existing whole-base physical rules is positive whole factor per pack, proposed `catalog.container_conversion_whole`. Enforce at configuration and receipt admission without rewriting existing mappings or history. Fractional wire lengths belong to Length/UOM bulk workflows. Supporting physically traced fractional-base packs would require an explicitly approved extension of the frozen physical quantity contract; it is not assumed here.

Inventory data must not be silently “repaired” by generating extra identities or changing old quantities. A later authorized diagnostic can detect existing invalid configurations/receipts and isolate them for a separately reviewed correction.

### G06 — First supplier/product link uses the existing pair authority

[SetSupplierProductActive:613][linkhandler] queries and then inserts a missing pair without the resource lock. AlignmentRepositories:24–32 uses SELECT FOR UPDATE, which cannot lock a row that does not yet exist. The database unique pair index prevents duplicate committed links, but one caller can receive an uncontrolled persistence error.

CreatePurchaseHandler:239–258 and [PUCA:51][pucalocks] already use the canonical resource:
`supplier-product`, key `SupplierId:D:ProductId:D`.

Acquire that pair lock inside the transaction **before lookup**. Preserve existing row ID, Version, NextItemSequence and active-state semantics. If multiple pairs are involved, sort keys consistently. Where product-level resource/row locks are also acquired, follow a consistent global order with PUCA; do not hold a product row and then request a lock held by PUCA while it waits for that row.

Do not weaken database uniqueness. Do not catch PostgreSQL unique violation and query using the same failed transaction. If recovery remains necessary, rollback and retry/reload in a fresh transaction with bounded attempts.

### G11 — Preserve backend purchase line authority

[NewPurchaseViewModel:296][linemerge] merges selections by ProductId although lookup rows represent separate ProductUnit choices. [CreatePurchaseHandler:135][lineunique] explicitly rejects duplicate ProductId purchase lines.

The bounded V1 rule is **one line per Product**. A second selection with the same ProductUnit may add quantity under explicit cost behavior. A different ProductUnit must not add to the existing numeric quantity. Show a controlled warning, or allow deliberate replacement with quantity and cost re-entry.

Example: 5 rolls plus a selection of 20 metres must never become 25 rolls. Enabling two same-Product lines by merely changing the UI merge key contradicts the backend and complicates receipt line resolution, returns, allocation and replay. Such multi-UOM lines are a separate business extension; the safe rejection rule requires no decision or migration.

## 6. Operator contracts: G07–G13

### G07 — Tracking mode and manufacturer identity UI

[ProductEditViewModel:182][modeoptions] lists Quantity, Length, IndividualPiece and Container but omits Serialized. Its clearing/masking logic at 387–397 and 570–571 only retains flags for Serialized, while the dialog binds policy controls to IsSerialized.

Offer all five supported modes. Serialized must require at least one manufacturer identity flag. IndividualPiece and Container retain their backend-supported optional serial/IMEI overlays; Quantity/Length clear unsupported overlays. Use “Manufacturer serial required” as the meaningful electrical-appliance field. IMEI remains an existing capability, not an assumed requirement for fans, heaters or bulbs. Editing a previously configured Product must preserve flags unless an authorized mode change explicitly invalidates them.

### G08 and G12 — Distinguish order from intake

NewPurchaseViewModel:97–100 and 275–281 require identity counts; 309–332 captures identities. [Remote purchasing:168][remoteorder] submits ReceiveStockImmediately:false. CreatePurchaseHandler:678–683 does not create physical units for that path; PhysicalIntakeViewModel:390–435 captures identities later. The first entry is therefore unnecessary for remote deferred orders.

The local purchasing adapter differs: BackendPurchasingInventoryService:237–271 validates identities and uses the default immediate-receipt command. Do not remove identity capture universally.

Expose explicit order-only/immediate-receipt capability and return mode in the shared purchase service contract. Order-only entry gathers quantity, unit and commercial information; actual intake captures identities from goods in hand. Immediate receipt still requires valid identities atomically.

NewPurchaseViewModel:400–402 says stock updated even for remote order-only success. Use “Purchase order [number] saved. Receive stock from Purchase Detail.” Only a result confirming committed receipt can claim updated stock.

### G09 — Search and continuation without arbitrary ceilings

Current remote purchase lookup caps **suppliers at 200**, **products at 500**, and fetches a separate first-500 stock set. Remote product-management lookup caps products/suppliers at 200. NewPurchase filters already-loaded rows. The candidate “all purchase products capped at 200” is factually corrected, although the gap remains. Local purchase lookup is unpaged and does not share that remote cap.

Use existing endpoints with a page size of 50:

- Products: `GET /api/catalog/products?search=...&isActive=true&pageSize=50&beforeName=...&beforeProductId=...`
- Suppliers: `GET /api/suppliers?search=...&pageSize=50&beforeName=...&beforeSupplierId=...`

Use the endpoint's existing name/ID ordering and cursor direction. Do not invent a supplier isActive argument; current directory behavior supplies active choices. Expose/derive continuation explicitly; count equal to page size only proves there may be another page. Product cursor comes from Product rows, not expanded ProductUnit choices. Expand only active purchase-enabled mappings for selectable UOMs.

Reuse the debounce/cancellation/generation pattern in SuppliersViewModel:686–725, about 250–300 ms. Discard stale results. Preserve draft lines, selected SupplierId/ProductUnitId and supplier-link selections across pages; paging must not inadvertently deactivate off-page links. Resolve selected historical/inactive entries separately for display without making them selectable for new orders. Join stock by selected/product page IDs or a server projection; absence from an unrelated first-500 stock list is not proof of zero stock.

### G10 — SupplierId is identity

NewPurchaseViewModel:180–185 represents selection as string; 382–387 resolves by name; 447–461 excludes ambiguous grouped names. Supplier names are not unique.

Use a typed supplier option with SupplierId and display text. Current DTO fields support Name, Phone and City; format “Ali Electric — Lahore — [phone]” and add a short ID when those collide. DealerCode is a useful optional projection, but the inspected PartyDirectory DTO does not already expose it. Adding it to DTO/projection requires no database column. All writes use SupplierId and never parse display text.

### G13 — Prefix fallback without permanent-code editing

SupplierEditDialog has no prefix input; SuppliersViewModel:89–100 and the remote adapter omit it, although SuppliersController supports it. Traceability rules derive two ASCII letters from the name, otherwise require explicit prefix.

Show a small two-ASCII-letter uppercase DealerPrefix fallback when derivation fails or the backend returns the relevant validation error. Ordinary names such as Ali Electric retain automatic derivation. An Urdu-only name can use an explicit operator-supplied prefix such as AE. Thread the field through both adapters and the creation operation fingerprint. Do not confuse prefix entry with editing an already assigned DealerCode, which remains permanent.

## 7. Attributes, pack scope, printing and identity: G14–G20

### G14 — TYPED_ATTRIBUTE_SCHEMA_REQUIRED

Product Model text and ModelCode are sufficient for V1 identity. A **typed, versioned validation schema for existing attributes** is required by canonical Annex 233.1C; a separate Model master is not proven necessary.

[Attributes policy:89][attributevalidation] currently accepts every positive schema version and arbitrary object keys/types/ranges. Version 999 with wattage -50 or ratedVoltage “banana” can pass this policy. Product command validation calls it, so the gap is in enforcement, not only the editor.

Define supported versions and profiles for applicable properties: fan sweep/blade count/rated wattage, cable conductor cross-section/core count/length, bulb wattage/color temperature/socket type. Specify types, units, sensible ranges, requiredness and explicit unknown-key policy. These examples are descriptive requirements to validate with existing product data; they are not a mandate to require every attribute on every Product.

Reject incompatible versions in a controlled way. Validate duplicate JSON property ambiguity. Preserve a declared legacy profile rather than making existing Products uneditable. Business-critical values remain relational; frequently searched properties can later receive justified typed/indexed representation. That optimization could require a migration, but schema validation using existing JSON/version fields does not.

### G15 — Intact-pack V1; splitting DEFER_FUTURE

Current tracking treats one Container identity as one intact entered pack with snapshotted base quantity/cost. Source searches found no open/split lifecycle; that absence alone is not a V1 defect.

Quantity/Length plus UOM covers receiving cartons of bulk bulbs or cable rolls and selling loose stock. A traceable carton split into individually traced descendants is a different capability. Keep V1 explicitly intact-only for Container and do not imply arbitrary partial sale.

If later required, design an atomic transformation with immutable parent identity, opened state, conserved quantity/cost, destination lot/child provenance, replay and reversal restrictions. Never mutate the parent TrackingCode, conversion factor or acquisition snapshot to simulate opening. Documentation/enforcement of intact-only V1 needs no migration; actual future lineage schema is **NOT_ENOUGH_EVIDENCE** until the destination stock model is chosen. No business decision blocks the current bounded fixes.

### G16 — Label recovery is Phase 9

Canonical sections at 1518–1527, 6804 and 8914 require required ORIGINAL label intent in the business transaction. [Intake:672][intakecommit] records outcome and saves without creating label/outbox requests. Desktop keeps committed units in memory at 478–511 and printing is a later operator action. Remote printing posts audit receipts after device output. That after-print record is not a pre-dispatch, atomically committed ORIGINAL intent.

The risk is lost delivery intent after a crash, not loss of the committed stock or TrackingCode. Ownership is **DEFER_PHASE9**, per the phase boundary; do not absorb this feature into Wave A.

Phase9 must persist one stable logical ORIGINAL artifact per unit/label part in the same PostgreSQL transaction as receipt. Dispatch after commit through an authenticated current-workstation print path. Persist target terminal, source identity, content/template version, attempts, leases and OUTCOME_UNKNOWN. Print Later defers an existing durable request. Ambiguous physical output requires reconciliation; replay must not blindly print another ORIGINAL. Reprints have distinct lineage and never allocate tracking sequences.

DbContext already exposes OutboxMessages. First assess that structure and existing print-job contracts; neither a new table nor zero migration is yet established for delivery uniqueness/terminal/attempt state. JsonPrintJobStore alone cannot provide atomic PostgreSQL receipt intent. Migration decision remains NOT_ENOUGH_EVIDENCE for this deferred item.

### G17 — No reachable post-history identity defect demonstrated

[UpdateProductHandler:350][identityguards] locks the Product and validates expected Version. It forbids normal base-unit change, locks tracking policy after history, and checks the **effective normalized values** of ModelCode, CompanyId, CategoryId and SKU before Apply. This covers derived SKU and Brand-driven Company resolution, not just raw input.

[History service:159][historyguards] includes stock, movement, units/lots, SupplierProduct and purchase/sale/return/warranty references. Company.Code and Category.Symbol handlers reject changes once relevant usage/history exists. [DbContext:129][dealerguard] applies permanent DealerCode guards through all SaveChanges overloads.

[PUCA:51][pucalocks] locks and reloads identity masters, then assigns ItemSequence, TrackingCode, SupplierCodeSnapshot and ProductSkuSnapshot only during physical-unit creation at 230–233. A targeted assignment search found no production edit route overwriting those fields on an existing unit. Public setters and the absence of a universal EF guard are not evidence of a reachable violating workflow.

Classify G17 FALSE_POSITIVE for the alleged reachable post-history mutation. This does **not** certify arbitrary privileged SQL or every hypothetical future code path. Canonical sections 172/195 contain stronger permanent-reservation wording alongside pre-use issuance correction semantics; reconcile that wording in documentation. The audit does not prove a retired-unused-SKU reservation registry, nor invent a migration to build one. Preserve existing governance/race tests and require new write paths to honor freeze contracts.

### G18/G19 — Documentation corrections only

Earlier canonical Product descriptions include historical BrandId/UnitId fields and fewer tracking modes; live Product uses CompanyId/BaseUnitId, Model/ModelCode and five modes. An authority reconciliation appendix should make precedence and current fields explicit without silently rewriting historical design sections.

Canonical Supplier lists UpdatedAt, but inspected Supplier model and EF configuration expose CreatedAt and Version without an UpdatedAt property/shadow mapping. Correct prose; adding a database field merely to make an old paragraph true is unjustified.

Neither authority document was edited in this read-only task.

### G20 — NOT_A_GAP / CURRENT_DESIGN_CORRECT

**Should Supplier be selected again at Physical Intake? NO.**

ReceiveProductIntakeCommand has no SupplierId input. The handler locks the Purchase and derives SupplierId from it at 451 and 516; physical creation uses that authority at 625–627. PhysicalIntakeViewModel displays the supplier and refreshes/checks the purchase context.

Adding another supplier selector would let commercial liability and physical DealerCode provenance diverge. Supplier changes require an explicit purchase correction process, never silent receipt reassignment.

## 8. Acceptance and verification contract

These are proposed tests for a later implementation. **None were executed in this audit.** Use isolated real PostgreSQL tests for persistence/locking; mocks alone cannot certify transactions, absent-row races or aborted-transaction recovery. UI tests should verify behavior at service boundaries rather than duplicate implementation statements.

| ID | Unit/UI acceptance | Real PostgreSQL, concurrency and rollback acceptance |
|---|---|---|
| G01 | Product editor uses one mutation; committed save plus refresh failure shows saved state and retains ID; payload mismatch rejected | Invalid units leave no Product/base mapping/links; supplier-link failure leaves none of requested changes; update failure restores every field/link; lost response replay returns same ID with no duplicate; concurrent same-ID request admitted once; concurrent business use cannot observe half-configured committed aggregate |
| G02 | Used factor change rejected; unchanged factor and permitted flag updates accepted; base factor 1; stale aggregate revision rejected | Order-only Purchase freezes factor; configuration racing first Purchase/use has one consistent winner; histories for sale/return/Thaka/movement/stocktake covered; rollback leaves factor/Version unchanged; inactive same-pair mapping cannot bypass uniqueness |
| G03 | Order 10 × 90 m receives 900 m despite mutable master test setup; wrong mapping/invalid snapshot controlled failures | Partial 4 + 6 rolls consumes exactly 900 m; 9 rolls cannot complete 10-roll order; two concurrent receipts cannot exceed outstanding; failure after provisional lot/unit creation rolls back receipt state; replay uses original snapshot and adds no stock |
| G04 | UI ordinary cost read-only/null; forged override rejected; effective charges visible | Ten units carry 11,000 not 10,000; partial receipt rounding conserves allocated effective total; lot/unit/movement/latest cost agree; concurrent/replayed intake adds cost once; failure leaves prior lot/balance/cost state unchanged |
| G05 | 1 and 2 packs accepted; 1.5 and 0.5 rejected before casting; factors 1/2/3 and decimal-factor boundary; overflow controlled | Immediate and deferred orders plus direct API intake enforce same rule; one identity per accepted pack; base/cost conserved; competing receipts no extra units; invalid request commits no movement/lot/unit/claim/sequence row changes |
| G06 | Existing pair reactivation preserves ID/cursor; stale expected Version returns controlled error | Manual/manual and manual/receipt first-link races produce one pair; no leaked unique exception; no reload in aborted PG transaction; sorted multi-pair operation rolls back cleanly on failure/cancellation; no sequence reset |
| G07 | All five modes selectable; serialized requires identity policy; optional physical overlays retained across edit; bulk flags cleared | Existing policy-lock backend regression unchanged; no new backend schema behavior |
| G08 | Deferred mode never requests manufacturer IDs; actual intake does; immediate local receipt still enforces IDs | Deferred save creates no units/reservations; immediate create still atomically persists stock/identities; failure/replay behavior retained |
| G09 | Search finds product beyond 500 and supplier beyond 200; rapid search discards stale response; selection and off-page links survive paging | Cursor has no duplicate/skipped stable rows; mapping expansion does not corrupt cursor; inactive records filtered for new choices; stock by page/selection IDs is accurate |
| G10 | Same-name suppliers remain selectable with distinct IDs; display collision fallback; persisted ID matches selection | Two legal duplicate-name suppliers remain unchanged; purchase links chosen SupplierId; no new name uniqueness |
| G11 | 5 rolls plus 20 metres warns without quantity/cost mutation; same-UOM merge explicit; replacement re-enters values | Existing duplicate-Product rejection remains; direct forged duplicate lines rejected without accounting/stock changes |
| G12 | Deferred result says order saved; immediate result says stock received only on actual commit | Result mode corresponds to committed backend path |
| G13 | Ordinary ASCII name derives prefix; Urdu/one-letter name prompts fallback; invalid prefix controlled; assigned code read-only | Explicit prefix creation and same-operation replay produce same SupplierId/DealerCode; changed prefix fingerprint rejected; allocated code uniqueness/permanence retained |
| G14 | Version/type/range/unit/unknown-key/malformed/duplicate-key rules; electrical profiles; legacy compatibility | Invalid attributes leave Product/version/units/links unchanged through aggregate save; concurrent stale update rejected; JSON data never replaces relational identity/stock/cost authority |
| G15 | Intact Container limitation clear; bulk loose-stock workflow demonstrated | Current frozen intact-pack sale/return/stocktake regressions preserved; future split tests require parent/child conservation, split/sale race and replay before implementation |
| G16 | Pending ORIGINAL and Print Later visible; unknown physical output does not invite blind retry | Phase9 crash before commit leaves neither units nor ORIGINAL; crash after commit retains pending request; replay creates no duplicate; dispatch only after commit; terminal offline/restart/lease recovery; separate reprint lineage; one label per Container, not per base piece |
| G17 | Existing master-history and effective-normalization governance tests retained | Existing product/master mutation-versus-PUCA issuance races remain valid; snapshots never rewritten; DealerCode guard all Save overloads |
| G18–G19 | Source-to-authority field checklist and precedence reviewed | No migration/model drift introduced by a prose correction |
| G20 | Supplier read-only; command has no selector | Receipt's SupplierProduct/DealerCode lineage matches Purchase.SupplierId; replay cannot switch supplier |

**Sequence rollback qualification:** transactional unit/cursor rows must not commit on failure. If the existing machine high-water allocator reserves a range outside the transaction, gaps may remain; that is safe. Never rewind/reuse reserved or committed ranges merely to satisfy a rollback test.

Preserve Tracking certification cases, Phase7 accounting mutation guards and Phase12 outcome recovery. Scope-based regression results must accompany implementation; this plan is not a replacement for recertification.

## 9. Migration decision and implementation file boundaries

**Bounded V1 fixes:** NO_MIGRATION_REQUIRED. Existing Product.Version, quantity/cost snapshots, JSON schema-version fields, Supplier prefix/code columns, pair uniqueness and operation-outcome storage support the minimal contracts. API/DTO additions and Application helpers are code changes, not database migrations.

The Product aggregate's new command/helper/endpoint may require new code files, but no new persistence entity is required. Reuse the existing outcome ledger with admission/replay protection; do not rewrite its shared implementation merely to add a catalog operation type. Avoid editing DbContext/EF snapshot unless evidence proves a required schema change.

**Optional extensions:** same-Unit mapping revisions, typed searchable columns and traced pack splitting require separate scope and migration review. They are not prerequisites.

**Deferred Phase9:** NOT_ENOUGH_EVIDENCE for schema change until the existing outbox/print-job structures are assessed against durable artifact identity and workstation recovery. No migrations were created.

Implementation file clusters:

- Catalog aggregate: ProductManagementHandlers, ProductUnitHandlers, catalog API, local/remote catalog adapters, Product editor; new Application command/helper if needed.
- Receipt authority: CreatePurchaseHandler, ReceiveProductIntakeHandler, shared catalog conversion rule, purchasing adapters and PhysicalIntakeViewModel.
- Pair race: manual link handler plus shared existing resource-lock usage; repository uniqueness remains unchanged.
- Operator UX: Product/Purchase/Supplier viewmodels and their dialogs/service contracts; optional supplier DTO projection.
- Attributes: CatalogModels validation and Product command/editor; no new Model master.
- Documentation: controlled authority reconciliation after owner review; current audit only describes it.
- Phase9 printing: receipt hook/outbox and workstation dispatcher only within that future phase's approved scope.

Any implementation changing receipt replay/fingerprint semantics must coordinate with the Phase12 receipt owner. No concurrent co-editing is recommended.

## 10. Three implementation waves

### Wave A — Stock/cost authority and physical validity

**Scope:** G02, G03, G04, G05 and G11's purchase-line guard.

Implement common conversion admission first, then frozen factor/effective-cost receipt consumption. Freeze used factors against first-use races. Add the bounded one-Product-line UI guard so operators cannot send unintended UOM quantity.

**Gate:** order/receipt examples, allocated-cost conservation, Container identity count, direct API rejection, real PostgreSQL over-receipt races and rollback all pass. Preserve existing receipt operation identity and frozen Tracking behavior. No printing or pack-split work enters this wave.

### Wave B — Atomic catalog save, pair concurrency and governed attributes

**Scope:** G01, G06 and G14.

Establish consistent product/pair lock order. Build the single aggregate command with durable admission/replay; switch both adapters to it. Integrate versioned attributes validation with explicit legacy compatibility. Coordinate unit-configuration code with Wave A rather than editing the same handler concurrently.

**Gate:** no partial Product/unit/link persistence; replay and refresh failure distinguished; absent-row link races controlled; schema version/key/type/range rejection proven without breaking supported legacy data.

### Wave C — Operator workflow and documentation

**Scope:** G07, G08, G09, G10, G12, G13, G18 and G19.

Complete five-mode policy UI, mode-aware identity capture/messages, server search/cursors, supplier-ID selection and prefix fallback. Reconcile documentation against the implemented contracts. Preserve G17/G20 as correct architecture.

**Gate:** realistic large catalogs, duplicate supplier names, Urdu supplier creation and immediate/deferred purchase scenarios pass; active frontend focus work is preserved.

**Outside the three bounded waves:** G15 remains Future V2; G16 remains Phase9. Do not create extra remediation waves for them. Program ownership order remains Phase7 → Phase12 → Phase8 → Phase9 → POS acceptance as stated by the boundary authority.

## 11. Business decisions and final verdict

**Business decisions required for the minimal V1 remediation: NONE.**

The backend already decides one purchase line per Product, receipt supplier inheritance, physical whole-base semantics and effective-cost authority. This plan follows those boundaries. A future request for multi-UOM purchase lines, fractional-base traced packs, a separate Model master or traceable pack splitting requires a new business decision; none is assumed required now.

Attribute profile values/legacy compatibility need ordinary product-owner validation during implementation, rather than an architectural redesign decision. No database inventory was accessed to preselect actual legacy payloads.

**FINAL VERDICT: REMEDIATION_PLAN_READY_WITH_DEFERRED_ITEMS**

Ready means bounded design and ownership are specified. It does not mean fixes are implemented, a production database has been certified, or no defects remain.

## 12. Preservation record

The audit compared SHA-256 before/after for 28 catalog/purchasing source files listed below; all matched. HEAD and branch remained unchanged. The five Phase7 intentional production hashes also matched their lock manifest. These checks cover the named files, not all files potentially changed by independent active tasks.

No source, test, frontend, database, migration or Git-history mutation was performed by this audit. No test/build process was launched. This root report is the sole current-request output.

### Audited source SHA-256 inventory

| Audited file | SHA-256 |
|---|---|
| [src/EdgeRetails.Application/Features/Catalog/ProductManagementHandlers.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/ProductManagementHandlers.cs>) | `06B6BE48DF556904C6237E4139CA58EAAB517560932F4653DD689518BFF8C43F` |
| [src/EdgeRetails.Application/Features/Catalog/ProductUnitHandlers.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/ProductUnitHandlers.cs>) | `49AA752E193902510D8960B1252471381E484172428B73B351229594DD5BEDFC` |
| [src/EdgeRetails.Application/Features/Catalog/CatalogReferenceHandlers.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/CatalogReferenceHandlers.cs>) | `2533576FC7323619F47A55B975FC2CAC3B109E2B3B1969A4361E073AAE91EF6A` |
| [src/EdgeRetails.Application/Features/Purchasing/CreatePurchaseHandler.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/CreatePurchaseHandler.cs>) | `902C5862BC88F38E4027600B22877A4A408DE2E4AACD7DDA26A28CA05A298653` |
| [src/EdgeRetails.Application/Features/Purchasing/ReceiveProductIntakeHandler.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/ReceiveProductIntakeHandler.cs>) | `E8C6A8234DC26E4E87B2C6DCC66D12A29BD2AABCAFC735F0876585F30CC4C2EE` |
| [src/EdgeRetails.Application/Features/Parties/PartyHandlers.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Parties/PartyHandlers.cs>) | `1BDF07944A2A1C2271178A0D0DEDFB785530A5F573B876AFEE793D427EF41160` |
| [src/EdgeRetails.Desktop/Services/BackendProductManagementService.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Services/BackendProductManagementService.cs>) | `220EF1876F717089700CB4EA72098216B8A48B3D8501951B2BEE361F279BA37A` |
| [src/EdgeRetails.Desktop/Services/RemoteProductManagementService.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Services/RemoteProductManagementService.cs>) | `DBF026FBF7ED52E01061D515DAB11B4200A8673C31A83F8DD7A4FECF7CF3B68B` |
| [src/EdgeRetails.Desktop/Services/BackendPurchasingInventoryService.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Services/BackendPurchasingInventoryService.cs>) | `EBC144AD0A448E8609B745323C1FDAA39D945AE04FE2DBE80E72E9B4F4368265` |
| [src/EdgeRetails.Desktop/Services/RemotePurchasingInventoryService.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Services/RemotePurchasingInventoryService.cs>) | `AAE9BA461524AD9B4FACF7480092A9D72A04D4F0BA6E6B662AEBE7A7B104E736` |
| [src/EdgeRetails.Desktop/ViewModels/ProductEditViewModel.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/ProductEditViewModel.cs>) | `BF4589764440DD360A92B856A4BE5D06E3ACD1D70FFC280E2AFC4EA514376F53` |
| [src/EdgeRetails.Desktop/ViewModels/NewPurchaseViewModel.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/NewPurchaseViewModel.cs>) | `005B7FE58A3E8110CFB945A32BC0F4B072D9E5E5E846E2A1C0D58BE857639271` |
| [src/EdgeRetails.Desktop/ViewModels/PhysicalIntakeViewModel.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/PhysicalIntakeViewModel.cs>) | `57F76278E9F0C887F614D7938A74BAE1324C57BC998139E38C53C8035D1B748C` |
| [src/EdgeRetails.Desktop/ViewModels/SuppliersViewModel.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/SuppliersViewModel.cs>) | `C101A8D8182075550974B7048DC0A8BD58203300A84BF178568EF61A608479E0` |
| [src/EdgeRetails.Desktop/Views/Dialogs/ProductEditDialog.xaml](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Views/Dialogs/ProductEditDialog.xaml>) | `0EC2600B3A1ACA7DBF5EA0B8A7EB7894BC61271A39B185EC505B9EB19747852B` |
| [src/EdgeRetails.Desktop/Views/Dialogs/SupplierEditDialog.xaml](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Views/Dialogs/SupplierEditDialog.xaml>) | `7995F1D8DD047979775A6CAC2B288A3FE5EE86C1A8009015BEF45C69AED8D143` |
| [src/EdgeRetails.Domain/Catalog/CatalogModels.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Catalog/CatalogModels.cs>) | `288C220A6F14596DAF0EE4614701FB445E9FFD7069C2089B59C1795095E968F2` |
| [src/EdgeRetails.Domain/Catalog/TraceabilityModels.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Catalog/TraceabilityModels.cs>) | `9423593CDC02E0E8AED38EDBE88D881957583CEEDE3FDA3AED5BDE028F1E1D41` |
| [src/EdgeRetails.Domain/Parties/PartyModels.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Parties/PartyModels.cs>) | `9C6D30D7B5849B34AE75CE0B31C473DE431642835183E0FD8045A5FE01E913CE` |
| [src/EdgeRetails.Domain/Inventory/InventoryModels.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Inventory/InventoryModels.cs>) | `DAC28BEE9273433FF9401C4750EC858649DF80DCBF8DE0E97BFCDD3665251BDD` |
| [src/EdgeRetails.Infrastructure/Services/ProductManagementReadService.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Services/ProductManagementReadService.cs>) | `89E5980F0131898A076C10A047FC1BA864590B65344D06619CFBFF4BF548569E` |
| [src/EdgeRetails.Infrastructure/Services/PhysicalUnitCreationAuthority.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Services/PhysicalUnitCreationAuthority.cs>) | `AD1CE80D3EDE1537C0C9282778513FCA0D05763D5E9A6DC9EC7C07B343E164B4` |
| [src/EdgeRetails.Infrastructure/Persistence/EdgeRetailsDbContext.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Persistence/EdgeRetailsDbContext.cs>) | `D325385DDC631EE62563502BCB8158549DDA69EED8568E2D39BC0DB247FAD4CA` |
| [src/EdgeRetails.Infrastructure/Persistence/Configurations/CatalogConfigurations.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Persistence/Configurations/CatalogConfigurations.cs>) | `90888D08AE0CE3C43B4198F14606BC2BB37E82F6D571BDB65D13D05F20EE274D` |
| [src/EdgeRetails.Infrastructure/Persistence/Configurations/TraceabilityConfigurations.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Persistence/Configurations/TraceabilityConfigurations.cs>) | `618BDEAF12652668BBACB92EDC5A9353823AE3827DF6EED550D54D8C3BBF33E6` |
| [src/EdgeRetails.Infrastructure/Persistence/Configurations/PartyConfigurations.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Persistence/Configurations/PartyConfigurations.cs>) | `C40121596BEBAFAB88AE42A845B9189013EF5AE67BBC3EA987463F4F1F33ADBF` |
| [src/EdgeRetails.Server/Controllers/CatalogController.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Server/Controllers/CatalogController.cs>) | `F68221799F33E637C0A6B8FDA9EBDA6498904FD17B34C7F28C75D049E0FAF6AB` |
| [src/EdgeRetails.Server/Controllers/SuppliersController.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Server/Controllers/SuppliersController.cs>) | `08339D06D8CDF34FAF7871758EAE49EFE98E9AD70518FADA8B49667B6F7FE543` |

## Reference index

[manifest]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/docs/Architecture_Authority_Manifest.json:1>
[canonical]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/docs/Edge_Retails_Final_Architecture_Report_v1.md:1>
[workflow]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/EDGE_RETAILS_CURRENT_CATALOG_PRODUCT_MODEL_SUPPLIER_WORKFLOW_READONLY.md:1>
[trackingfreeze]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/EDGE_RETAILS_TRACKING_SYSTEM_FINAL_CERTIFICATION_AND_FREEZE_2026-10-03.md:1>
[boundary]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/EDGE_RETAILS_PHASE7_FINAL_DEFECT_OWNERSHIP_AND_BOUNDARY_FREEZE.md:1>
[phase7lock]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/EDGE_RETAILS_PHASE7_PASS3_STEP2_IMPLEMENTATION_CERTIFICATION_AND_LOCK.md:1>
[phase7manifest]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/EDGE_RETAILS_PHASE7_PASS3_FINAL_SOURCE_MANIFEST.sha256:1>
[producthandlers]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/ProductManagementHandlers.cs:28>
[unithandlers]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/ProductUnitHandlers.cs:46>
[localcatalog]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Services/BackendProductManagementService.cs:333>
[remotecatalog]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Services/RemoteProductManagementService.cs:107>
[catalogapi]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Server/Controllers/CatalogController.cs:132>
[productvm]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/ProductEditViewModel.cs:182>
[catalogmodel]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Catalog/CatalogModels.cs:44>
[historyservice]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Services/ProductManagementReadService.cs:159>
[createpurchase]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/CreatePurchaseHandler.cs:135>
[intake]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/ReceiveProductIntakeHandler.cs:242>
[intakevm]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/PhysicalIntakeViewModel.cs:443>
[localpurchase]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Services/BackendPurchasingInventoryService.cs:237>
[remotepurchase]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Services/RemotePurchasingInventoryService.cs:29>
[alignmentrepo]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Repositories/AlignmentRepositories.cs:24>
[puca]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Services/PhysicalUnitCreationAuthority.cs:51>
[traceconfig]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Persistence/Configurations/TraceabilityConfigurations.cs:33>
[productdialog]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Views/Dialogs/ProductEditDialog.xaml:116>
[purchasevm]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/NewPurchaseViewModel.cs:97>
[suppliersapi]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Server/Controllers/SuppliersController.cs:36>
[purchaseview]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Views/NewPurchaseView.xaml:29>
[partydirectory]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Parties/PartyDirectoryQueries.cs:13>
[supplierdialog]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Views/Dialogs/SupplierEditDialog.xaml:1>
[suppliervm]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/SuppliersViewModel.cs:89>
[partyhandlers]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Parties/PartyHandlers.cs:395>
[remoteparty]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Services/RemoteBackendBusinessOperationsService.cs:131>
[attributesauthority]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/docs/Edge_Retails_Final_Architecture_Report_v1.md:9121>
[printhandler]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Production/Printing/PrintPhysicalStickersHandler.cs:84>
[outboxmodel]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/SystemConfiguration/OutboxModels.cs:1>
[printingapi]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Server/Controllers/PrintingController.cs:78>
[referencehandlers]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/CatalogReferenceHandlers.cs:118>
[dbcontext]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Persistence/EdgeRetailsDbContext.cs:129>
[supplierauthority]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/docs/Edge_Retails_Final_Architecture_Report_v1.md:1254>
[partymodel]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Parties/PartyModels.cs:17>
[partyconfig]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Persistence/Configurations/PartyConfigurations.cs:32>
[saveui]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/ProductEditViewModel.cs:584>
[localsave]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Services/BackendProductManagementService.cs:333>
[remotesave]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Services/RemoteProductManagementService.cs:107>
[factorupdate]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/ProductUnitHandlers.cs:136>
[unitunique]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Persistence/Configurations/CatalogConfigurations.cs:170>
[ordersnapshot]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/CreatePurchaseHandler.cs:363>
[intakeline]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/ReceiveProductIntakeHandler.cs:255>
[costauthority]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/docs/Edge_Retails_Final_Architecture_Report_v1.md:5883>
[costpayload]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/PhysicalIntakeViewModel.cs:443>
[intakecost]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/ReceiveProductIntakeHandler.cs:487>
[quantitypolicy]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Catalog/CatalogModels.cs:144>
[purchasecount]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/CreatePurchaseHandler.cs:674>
[intakecount]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/ReceiveProductIntakeHandler.cs:563>
[linkhandler]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/ProductManagementHandlers.cs:613>
[pucalocks]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Services/PhysicalUnitCreationAuthority.cs:51>
[linemerge]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/NewPurchaseViewModel.cs:296>
[lineunique]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/CreatePurchaseHandler.cs:135>
[modeoptions]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/ProductEditViewModel.cs:182>
[remoteorder]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Services/RemotePurchasingInventoryService.cs:168>
[attributevalidation]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Catalog/CatalogModels.cs:89>
[intakecommit]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/ReceiveProductIntakeHandler.cs:672>
[identityguards]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/ProductManagementHandlers.cs:350>
[historyguards]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Services/ProductManagementReadService.cs:159>
[dealerguard]: <C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Persistence/EdgeRetailsDbContext.cs:129>

