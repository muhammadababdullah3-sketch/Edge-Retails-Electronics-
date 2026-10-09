# Edge Retails — Current catalog, product, model and supplier workflow

**Verdict: CATALOG_WORKFLOW_MAPPED — source-derived read-only study.**

Date: 2026-10-04. Workspace: `C:\Users\muham\OneDrive\Desktop\Point of Sale`.
Branch at inspection: `tracking-remediation-20261002`; HEAD: `97956831e01d7ed6b4e2f1051b15e8ec272e8e4b`.

This report describes current live source and its data-entry paths. It is not a runtime acceptance test, database inspection, new certification, redesign or implementation authorization. Existing workspace changes were preserved. No builds, tests, database connections, UI actions, seeding or service changes were performed. The only authorized new file is this requested report.

## 1. Authority and execution ledger

Authority was established in this order: Architecture Authority Manifest → canonical architecture report → latest catalog/remediation authority → latest Tracking certification → live Domain/Application/Infrastructure/Desktop/API sources. Live implementation differences from historical documentation are explicitly identified below. The canonical manifest records architecture version `EdgeRetails-Backend-V1-2026-09-22-InternationalAuditRemediated`, sections 0–233.1, SHA256 `12344760C60124DDC2D1C0E54ABFB7BC0E82B9B23A46E6463303EBFA530AB673`. [MAN:1](<C:/Users/muham/OneDrive/Desktop/Point of Sale/docs/Architecture_Authority_Manifest.json:1>)

| Gate | Scope | Evidence required | Command / inspection | Status |
|---|---|---|---|---|
| Scope | Read-only authority and preservation | User attachment, instructions, branch/status and file hashes | Targeted reads, Git status/HEAD; 871-file baseline | COMPLETE |
| A | Catalog structure | Entity fields, FK cardinality, required inputs, validation | Read-only catalog specialist | COMPLETE |
| B | Supplier structure | Master/bridge fields, code issuance, links and constraints | Read-only supplier specialist | COMPLETE |
| C | Product Management UI | Real controls, adapters, save calls and omissions | Read-only UI specialist | COMPLETE |
| D | Purchasing | Order vs receipt, prerequisites, stock commit | Read-only purchase specialist | COMPLETE |
| E | Tracking | Five modes, canonical identity generation and provenance | Read-only tracking specialist | COMPLETE |
| Reconcile | Current workflow | Cross-layer agreement and explicit gaps | Lead source reconciliation | COMPLETE |
| Preserve | No unintended changes | Final hashes, branch/HEAD and status comparison | Read-only baseline comparison | See preservation result at end |

Three specialist contexts were reused sequentially for five bounded scopes (A/B/C, then D/E). No nested agents and no agent writes. Repository instructions were searched before the study; no applicable AGENTS.md was found.

## 2. Actual hierarchy

There is **no independent Model/ProductModel master** in the current inspected domain, persistence or catalog commands. `Product.Model` and `Product.ModelCode` are strings on Product. Company and Category attach directly to Product. The UI calls Company “Company / Manufacturer”; there is no separate Manufacturer entity in this workflow. [CM:23](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Catalog/CatalogModels.cs:23>) [CM:39](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Catalog/CatalogModels.cs:39>) [PUI:53](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Views/Dialogs/ProductEditDialog.xaml:53>)

```text
Independent Company (optional) ───1:N───┐
Independent Category ────────────1:N───┤
Independent Unit (BaseUnit) ─────1:N───┤
                                      Product
                                      │ Model / ModelCode: text fields
                                      │
              Independent Unit ──1:N─ ProductUnit ──N:1── Product
                                      (UOM conversion, not variant)

Independent Supplier ──1:N── SupplierProduct ──N:1── Product
                              │ NextItemSequence
                              └──1:N── InventoryUnit
                                        └──N:1── Product
                                        └──purchase/lot/movement provenance

Purchase order → authorized receipt commit → stock / lots / movements
                                          → InventoryUnits only for physical modes
```

Product master creation creates zero stock. A Supplier can exist with no Products; a Product can exist with no Supplier. Both must exist before an explicit SupplierProduct link can be created. [PH:288](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/ProductManagementHandlers.cs:288>) [PRH:298](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Parties/PartyHandlers.cs:298>)

### Entity ownership, prerequisites and reuse

| Entity | Parent / references | Children / dependants | Required before creation | Independent? | Reusable? | Relationship |
|---|---|---|---|---|---|---|
| Company / Manufacturer | None | Products | Name; optional valid Code | YES | YES | Company 1:N Product |
| Category | None | Products | Name; optional valid IdentitySymbol | YES | YES | Category 1:N Product |
| Unit | None | Product base references, ProductUnits | Name, Symbol, decimal precision | YES | YES | Unit 1:N mappings/references |
| Model | No entity; fields on Product | No relational children | Product entry context only | NO separate master | Text may repeat | No Model FK/cardinality |
| Product | Active Category and BaseUnit; optional Company | ProductUnits, SupplierProducts, transaction/stock records | Name and active Category/BaseUnit; valid policy/values | YES as catalog master after reference prerequisites | YES | Direct reference master |
| ProductUnit | Product + Unit | Transaction quantity references | Existing Product and active Unit; factor/flags | NO | Within its Product | Product 1:N; unique Product/Unit pair |
| Supplier | None | SupplierProducts, Purchases | Name; valid DealerCode prefix derivation/override | YES | YES | Supplier 1:N links |
| SupplierProduct | Supplier + Product | Physical InventoryUnits and pair sequence | Both masters; activation rules | NO | YES, same pair persists | Canonical M:N bridge |
| InventoryUnit | Product; optional SupplierProduct FK at schema level; mandatory origin provenance | Movement/unit links, identity claims | Authorized physical creation path | NO operator master | Same identity throughout lifecycle | Product 1:N; bridge 1:N when assigned |

Evidence: [CC:120](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Persistence/Configurations/CatalogConfigurations.cs:120>) [CC:160](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Persistence/Configurations/CatalogConfigurations.cs:160>) [TRC:23](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Persistence/Configurations/TraceabilityConfigurations.cs:23>) [IM:170](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Inventory/InventoryModels.cs:170>) [IC:158](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Persistence/Configurations/InventoryConfigurations.cs:158>). Schema-nullability and new-command prerequisites differ for legacy Products: CategoryId is nullable in the entity, but a new Product command requires an active Category. [PH:78](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/ProductManagementHandlers.cs:78>)

### Relationship table

| From | Relationship | To | Cardinality | Meaning |
|---|---|---|---|---|
| Company | classifies manufacturer | Product | 1:N, optional Product FK | Not a Model hierarchy |
| Category | classifies | Product | 1:N | Required by new Product save |
| Unit | defines base measure | Product | 1:N | Product has one BaseUnit |
| Product | configures | ProductUnit | 1:N | Conversion and buy/sell/default flags |
| Unit | reused by | ProductUnit | 1:N | Unit is independent master |
| Supplier | supplies | Product | M:N via SupplierProduct | Same Product reused across suppliers |
| Supplier | owns one side | SupplierProduct | 1:N | Pair-specific permanent sequence |
| Product | owns other side | SupplierProduct | 1:N | Unique supplier/product pair |
| Product | identifies catalog class of | InventoryUnit | 1:N | Physical item is an instance |
| SupplierProduct | identifies source pair of | InventoryUnit | 1:N where populated | Physical supplier/product provenance |
| Model text | describes | Product | No relational cardinality | Repeated text is not shared master |

## 3. Exact fields and creation requirements

| Entry | User fields / validation | Generated / defaults |
|---|---|---|
| Company | Name normalized, nonblank, ≤150; optional Code exactly two Latin letters | Available Code suggestion if omitted; uppercase; ID, active state and Version |
| Category | Name normalized, nonblank, ≤150; optional IdentitySymbol 1–4 uppercase alphanumeric, first character a letter | Available symbol suggestion if omitted; ID, active state and Version |
| Unit | Name 1–100, Symbol 1–20, DisplayDecimalPlaces 0–6 | ID, active state; Symbol is user input |
| Product | Name; Category; BaseUnit; optional Company, Brand, Model, ModelCode, SKU; TrackingMode and Serial/IMEI flags; ReferencePurchaseCost, DefaultSalePrice, MinimumStockLevel, DefaultWarrantyMonths; AttributesJson and schema version | ID; active=true; Version=1; default mode Quantity; numeric defaults zero except optional reference cost; schema version1; base ProductUnit |
| Supplier | Name ≤200; optional Phone≤50, City≤120, Address≤500, Notes≤1000; backend also accepts optional explicit DealerPrefix | DealerCode issued; ID, active=true, CreatedAt, Version |
| SupplierProduct | ProductId, SupplierId, desired IsActive; optional ExpectedVersion in management command | ID, NextItemSequence=1 at first creation, timestamps/Version |
| ProductUnit | UnitId, positive FactorToBaseUnit; CanPurchase, CanSell, CanUseInThaka; default buy/sale flags | Base row factor1, all three operation flags and both defaults true |

Evidence: [RH:79](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/CatalogReferenceHandlers.cs:79>) [RH:292](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/CatalogReferenceHandlers.cs:292>) [RH:501](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/CatalogReferenceHandlers.cs:501>) [PH:9](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/ProductManagementHandlers.cs:9>) [PH:116](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/ProductManagementHandlers.cs:116>) [PH:296](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/ProductManagementHandlers.cs:296>) [PM:17](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Parties/PartyModels.cs:17>) [PC:32](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Persistence/Configurations/PartyConfigurations.cs:32>) [TM:8](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Catalog/TraceabilityModels.cs:8>) [UH:9](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/ProductUnitHandlers.cs:9>).

Product stores **no typed tax, RAM, storage, color, size, parent-variant or variant-group fields** in this entity/input/dialog. Warranty is DefaultWarrantyMonths. AttributesJson is optional metadata; the current validator requires a JSON object and schema version ≥1, without enforcing a per-key RAM/color/type/range schema. [CM:39](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Catalog/CatalogModels.cs:39>) [CM:89](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Catalog/CatalogModels.cs:89>)

Company and Model are optional for Product creation in both the current backend and UI. A selected Company must be active. Brand can resolve an existing Company by name when CompanyId is absent. There is no separate “Company required for Model” rule because Model is not a separately created object. [PH:100](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/ProductManagementHandlers.cs:100>) [PVM:475](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/ProductEditViewModel.cs:475>)

## 4. Model, SKU and variants

**ProductCode means the persisted Product.Sku**, not a separate ProductCode record. Current issuance:

1. Normalize explicit ModelCode, or suggest one from Model text.
2. If Company, Category and ModelCode resolve: **Company.Code + Category.IdentitySymbol + "-" + ModelCode**.
3. Otherwise normalize a supplied SKU, or generate a legacy fallback suggestion using Brand/Name/Model/category information.

When step2 applies, the backend derives the SKU even if the operator typed a different SKU. New-product UI suggestions are not final backend authority. [PH:166](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/ProductManagementHandlers.cs:166>) [PH:183](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/ProductManagementHandlers.cs:183>) [TM:383](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Catalog/TraceabilityModels.cs:383>) [PVM:298](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/ProductEditViewModel.cs:298>)

ModelCode accepts 1–20 uppercase letters/digits/dash/underscore; suggestions derived from Model use a shorter token. ModelCode is not globally unique by itself. SKU is globally unique when nonnull and duplicate lookup includes inactive Products. [TM:310](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Catalog/TraceabilityModels.cs:310>) [PH:204](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/ProductManagementHandlers.cs:204>) [CC:135](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Persistence/Configurations/CatalogConfigurations.cs:135>)

**Same displayed model, multiple independently stocked variants:** create different Product records with distinguishable names/attributes and distinct effective SKU identity tokens. With the same Company/Category, use distinct ModelCodes. Example codes `A55-8256-BLK` and `A55-8128-BLU` fit current grammar. Both may have Model=`Galaxy A55`. Same Company+Category+ModelCode produces a duplicate SKU even if names/JSON/manual SKU differ. This is repeated model text across Products, not a formal Model→variants hierarchy. ProductUnits represent piece/box/pack/meter/carton conversion, not colors or RAM variants. [PH:184](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/ProductManagementHandlers.cs:184>) [CM:130](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Catalog/CatalogModels.cs:130>)

## 5. Supplier and SupplierProduct

Supplier and Product are independent concepts joined by **SupplierProduct**. Supplier does not live “inside” Model, and Model does not live “inside” Supplier. Product has no direct SupplierId; Supplier has no ProductId. One Product can have 2, 3, 10 or more Suppliers; one Supplier can supply many Products. No maximum link count is defined by the inspected models/configuration. [CM:39](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Catalog/CatalogModels.cs:39>) [PM:17](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Parties/PartyModels.cs:17>) [TRC:23](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Persistence/Configurations/TraceabilityConfigurations.cs:23>)

SupplierProduct exact fields: inherited Id; SupplierId; ProductId; NextItemSequence; IsActive; CreatedAt; UpdatedAt; Version. It has **no supplier-specific catalog code, procurement price, lead time, supplier reference or ProductUnit field**. NextItemSequence is the next physical item number for that supplier/product pair. Product.ReferencePurchaseCost and purchase-line costs are separate authorities. [TM:8](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Catalog/TraceabilityModels.cs:8>) [CM:52](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Catalog/CatalogModels.cs:52>)

Explicit link activation creates a missing pair once. Unticking deactivates the existing row; reactivation preserves its cursor. Receipt can create a missing pair inside its authorized transaction. An existing inactive pair is rejected by physical creation rather than silently recreated/reactivated. No Product duplication is needed when adding another supplier. [PH:604](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/ProductManagementHandlers.cs:604>) [CP:234](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/CreatePurchaseHandler.cs:234>) [PUCA:178](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Services/PhysicalUnitCreationAuthority.cs:178>)

### DealerCode / SupplierCode

DealerCode is automatically assigned on Supplier creation and can be backfilled on update when absent. Prefix is the first two ASCII letters from the name, uppercase; fewer than two requires an explicit two-letter prefix through the backend. Prefix-scoped locked persistent sequence produces e.g. `AB1`. Exact values depend on existing sequence state. Rename preserves an assigned DealerCode. [PRH:298](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Parties/PartyHandlers.cs:298>) [PRH:390](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Parties/PartyHandlers.cs:390>) [TM:45](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Catalog/TraceabilityModels.cs:45>)

An assigned DealerCode cannot be changed or cleared through DbContext saves, even before stock. `SupplierCodeSnapshot` means this DealerCode snapshot, not another manually editable code. [DB:129](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Persistence/EdgeRetailsDbContext.cs:129>) [PUCA:217](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Services/PhysicalUnitCreationAuthority.cs:217>)

## 6. UOM setup

BaseUnit must exist before Product creation. Product creation automatically installs its factor1 ProductUnit. Additional mappings may be configured during the dialog save or later, but the selected purchasing mapping must already be active and CanPurchase before purchase/intake. The base mapping must remain present with factor1; each Product/Unit pair is unique. At most one active default purchase and sale mapping exists; defaults must support their operations. [PH:296](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/ProductManagementHandlers.cs:296>) [UH:60](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/ProductUnitHandlers.cs:60>) [UH:80](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/ProductUnitHandlers.cs:80>) [RI:442](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/ReceiveProductIntakeHandler.cs:442>)

Examples of valid meaning: piece→piece factor1; box→piece factor12; meter→meter factor1. Names alone do not define tracking semantics. A Product with Quantity tracking and a “pack” UOM is still bulk; Container/Pack is a tracking-mode choice. Serialized mappings explicitly require whole factors; physical transaction base counts must remain whole under domain conversion rules. [UH:102](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/ProductUnitHandlers.cs:102>) [CM:144](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Catalog/CatalogModels.cs:144>)

**Current factor edit behavior:** ConfigureProductUnits permits updating factor/flags after history; it has no history-lock guard. Existing transaction quantity snapshots remain recorded separately. Normal Product edit refuses BaseUnit changes even before history. Do not confuse an editable UOM conversion with permission to rewrite old transaction snapshots. [UH:136](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/ProductUnitHandlers.cs:136>) [CM:189](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Catalog/CatalogModels.cs:189>) [PH:393](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/ProductManagementHandlers.cs:393>)

## 7. Correct daily master-entry order

Logical dependencies do not impose a fixed order among independent masters.

1. **Create/reuse active Category and Unit** through Product Management → Categories / Units. New Product requires both; Add Product requires an available active Unit.
2. **Create/reuse Company if appropriate** through Companies. This provides optional manufacturer classification and its structured SKU segment.
3. **Create/reuse Supplier**, now or earlier, through Suppliers / Wholesalers → + Add Supplier. It is independent; do not create it inside Product/Model.
4. **+ Add Product**: enter Name, choose Category and BaseUnit, optional Company/Brand, Model display text and ModelCode; choose supported tracking policy; prices/warranty/attributes.
5. **Configure Product Units**. Base factor1 is initialized; enable any extra conversion and buy/sell/default flags needed for procurement.
6. **Optionally tick existing Supplier Links** and Save Product. Additional suppliers can be attached later via Edit on the same Product.
7. **Create Purchase**, then **receive stock** through the actual intake flow described next.

Evidence: [PMV:22](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Views/ProductManagementView.xaml:22>) [PMVM:453](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/ProductManagementViewModel.cs:453>) [PUI:37](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Views/Dialogs/ProductEditDialog.xaml:37>) [SV:10](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Views/SuppliersView.xaml:10>).

Valid alternatives: Supplier first; Company before or after Category/Unit; Product saved with no supplier then supplier/link added later. There is **no “Create Model” step**. A link needs both masters. A receipt needs a saved purchase and purchaseable ProductUnit; manual pre-linking is not invariably required because supported receiving creates a missing pair.

### What the operator actually sees

Product dialog field order: Product Name → SKU (Master Product Code) → Company / Manufacturer + Category → Model (Display) + Model Code (Identity Token) → Brand (Optional) + Base Unit → Tracking Mode / Serial / IMEI → Reference Purchase Cost / Default Sale Price / Minimum Stock → Default Warranty / Attributes Schema Version → Catalog Attributes JSON → Product Units → Supplier Links → Save Product. [PUI:37](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Views/Dialogs/ProductEditDialog.xaml:37>)

Product save is multiple backend calls: Product create/update, ProductUnit configuration, supplier-link synchronization, then readback. This is not a single atomic wizard transaction; later failure may leave the already-saved Product/base mapping. [RPM:101](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Services/RemoteProductManagementService.cs:101>) [BPM:325](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Services/BackendProductManagementService.cs:325>)

Supplier Links is an existing checkbox editor with multiple/empty selection. Supplier detail's Products Supplied tab is read-only. Supplier creation exposes Name/Phone/City/Address/Notes, with no DealerCode or explicit prefix input. Phone's UI asterisk is stronger than the backend optional requirement. [PUI:199](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Views/Dialogs/ProductEditDialog.xaml:199>) [SD:238](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Views/SupplierDetailView.xaml:238>) [SUI:7](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Views/Dialogs/SupplierEditDialog.xaml:7>)

## 8. One source-valid realistic example

This is an illustration, **not records inserted or codes observed in the operational database**.

1. Create/reuse Company **Samsung**, valid explicit Code **SA**; Category **Mobile Phones**, valid Symbol **MP**; Unit **Piece**, Symbol **pc**, precision0. These values must be available under duplicate rules.
2. Create Supplier **ABC Electronics** and Supplier **XYZ Traders**. Backend issues DealerCodes; assume `AB1`/`XY1` only for this illustration, not promised real allocation.
3. Create Product **Galaxy A55 8/256 Black**, Model display **Galaxy A55**, ModelCode **A55-8256-BLK**, Company Samsung, Category Mobile Phones, BaseUnit Piece. Example AttributesJson: `{"ramGb":8,"storageGb":256,"color":"Black"}`, schema1. These keys are metadata, not a typed variant schema.
4. Choose **IndividualPiece**, serial/IMEI flags off, for an example executable through the current Add Product dropdown. Set nonnegative reference cost/sale price and desired warranty. For a true Serial/IMEI-required Serialized item, see the existing UI gap below; do not represent this as an available new Serialized setup.
5. Backend derives **SAMP-A55-8256-BLK** and creates factor1 base ProductUnit. Tick ABC Electronics in Supplier Links if desired. Product save creates no stock.
6. Later Edit the **same Product**, also tick XYZ Traders. Two SupplierProduct pairs now relate two suppliers to one Product. The SKU stays the same.
7. Save a purchase order from ABC for two pieces and then commit its intake. With illustrative AB1 pair starting at1, physical codes are `AB1-SAMP-A55-8256-BLK-000001` and `...-000002`.
8. Receive the same Product from XYZ through another purchase. Its independent pair could start `XY1-SAMP-A55-8256-BLK-000001`; it does not reset ABC's cursor.
9. A separately stocked 8/128 Blue variant is another Product with Model text Galaxy A55 and distinct ModelCode **A55-8128-BLU**, deriving **SAMP-A55-8128-BLU**. ProductUnit is not the variant.

Evidence for exact grammar/issuance: [TM:94](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Catalog/TraceabilityModels.cs:94>) [TM:383](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Catalog/TraceabilityModels.cs:383>) [PUCA:210](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Services/PhysicalUnitCreationAuthority.cs:210>).

## 9. Purchase order and actual receipt

The normal production composition selects the remote adapter when the API client exists. The default-unit catalog and source workflow are proven here: [NAV:72](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Navigation/PageViewModelFactory.cs:72>) [PURCAT:20](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Services/PurchaseCatalogReadService.cs:20>) [NEWVIEW:26](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Views/NewPurchaseView.xaml:26>).

### Normal API Desktop order

1. Choose the existing active Supplier, supplier invoice number/date, then existing catalog Product. The picker supplies its active default purchase ProductUnit; there is no separate UOM picker. The catalog is not filtered by SupplierProduct. Stored Product mode/policy and UOM are used; the operator does not choose a different tracking mode on the purchase.
2. Add lines and enter positive quantity, entered-unit cost, base sale price and optional charges/note. NewPurchase merges duplicate selections by Product ID, not ProductUnit ID; backend also rejects duplicate ProductId lines.
3. **Save New Purchase** submits `ReceiveStockImmediately:false` in the remote adapter. This saves the commercial order and can create supplier-ledger/payment effects; it does not create stock or TrackingCodes.
4. Open Purchase detail, row **Receive / Stickers** or footer **Receive Physical Intake**.
5. The intake dialog loads authoritative purchase/line details. Enter the actual quantity received, optional cost override and required per-unit manufacturer identifiers.
6. Click **Receive Physical Intake & Allocate Identities**. The command derives Supplier from Purchase, validates Product/unit, outstanding quantity and stocktake restrictions, then applies receipt inside the business transaction.
7. Successful commit adds stock balance delta, movement/effect, carrying value/lot and physical units where appropriate. Only then print/reprint/export existing committed labels.

Evidence: [NP:275](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/NewPurchaseViewModel.cs:275>) [NP:289](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/NewPurchaseViewModel.cs:289>) [RPO:153](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Services/RemotePurchasingInventoryService.cs:153>) [PDV:107](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Views/PurchaseDetailView.xaml:107>) [PD:191](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/PurchaseDetailViewModel.cs:191>) [PID:84](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Views/Dialogs/PhysicalIntakeDialog.xaml:84>) [PI:443](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/PhysicalIntakeViewModel.cs:443>) [RI:481](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/ReceiveProductIntakeHandler.cs:481>) [RI:518](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/ReceiveProductIntakeHandler.cs:518>) [RI:625](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/ReceiveProductIntakeHandler.cs:625>) [RI:684](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/ReceiveProductIntakeHandler.cs:684>) [PLAT:83](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Services/PlatformServices.cs:83>).

### Prerequisites and path distinctions

- An authorized actor/operation ID, saved nonvoid purchase, matching Product line, active Product and active purchase-enabled ProductUnit must exist.
- Intake derives Supplier from purchase. It requires an existing Supplier with nonblank DealerCode **even on the deferred bulk receipt path**. Physical creation also checks Supplier active.
- Physical modes require an authoritative Product SKU. Required identities/counts depend on the Product's stored policy.
- A counting stocktake may block receipt. Received base quantity cannot exceed order base quantity minus recorded receipts. Partial receipts are supported.
- A preexisting SupplierProduct is not always necessary. Physical authority can create a missing active pair; inactive pair fails.
- **Deferred Quantity/Length intake does not create SupplierProduct in this handler**. Immediate CreatePurchase receiving creates missing links for all prepared lines, including bulk. Do not generalize every receipt path to bridge creation.
- Backend CreatePurchase default is immediate receiving; the local direct adapter omits the flag and therefore differs from current remote Desktop order-only composition. This report's normal operator order uses the remote API path, not the local legacy adapter.
- Product master cannot be silently created by a purchase; existing identities are selected.

Evidence: [CP:17](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/CreatePurchaseHandler.cs:17>) [CP:234](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/CreatePurchaseHandler.cs:234>) [RI:442](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/ReceiveProductIntakeHandler.cs:442>) [RI:450](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/ReceiveProductIntakeHandler.cs:450>) [RI:467](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/ReceiveProductIntakeHandler.cs:467>) [RI:504](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/ReceiveProductIntakeHandler.cs:504>) [PUCA:90](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Services/PhysicalUnitCreationAuthority.cs:90>) [BPO:261](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Services/BackendPurchasingInventoryService.cs:261>).

**Snapshot caveat:** Purchase creation snapshots its UOM factor, and pending display uses that order snapshot. Deferred receipt calculates quantity from the **current supplied ProductUnit factor**, allowing a purchaseable unit belonging to the Product without requiring equality with the order's ProductUnitId. It bounds that result against ordered base quantity. Since UOM factors are editable, “receipt always uses frozen order factor” is not a source-supported statement. The normal intake UI fixes the ordered product/unit; backend acceptance is broader. [CP:359](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/CreatePurchaseHandler.cs:359>) [BPO:568](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Services/BackendPurchasingInventoryService.cs:568>) [RI:442](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/ReceiveProductIntakeHandler.cs:442>) [RI:481](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/ReceiveProductIntakeHandler.cs:481>) [PI:373](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/PhysicalIntakeViewModel.cs:373>).


**Purchase catalog and pricing:** The selector returns active Products with an active purchase-enabled default purchasing unit. Initial cost comes from Product.ReferencePurchaseCost and initial sale price from Product.DefaultSalePrice. The sale price entered on the purchase is SalePriceAtPurchase, not an observed mutation of Product.DefaultSalePrice. Order cost snapshots incorporate allocated other charges. [CP:352](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/CreatePurchaseHandler.cs:352>) [CP:371](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/CreatePurchaseHandler.cs:371>) [NP:16](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/NewPurchaseViewModel.cs:16>).

**Deferred-cost caveat:** Intake UI sends the original entered line cost explicitly. Backend divides that override by current UOM factor, instead of its null-override fallback to the order's charge-inclusive EffectiveBaseUnitCost. Thus deferred receipt should not be described as automatically reusing the charge-inclusive order cost. [PI:443](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/PhysicalIntakeViewModel.cs:443>) [RI:487](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/ReceiveProductIntakeHandler.cs:487>).

**Manufacturer pre-capture caveat:** Backend allows deferred physical orders without identities. NewPurchase's current CanSave nonetheless requires identity counts when overlays are enabled; order-only save allocates no InventoryUnits and does not persist those identities into PurchaseItem. Intake has its own fresh identity rows. Required values may need to be entered again at actual receipt. [NP:97](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/NewPurchaseViewModel.cs:97>) [NP:275](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/NewPurchaseViewModel.cs:275>) [CP:356](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/CreatePurchaseHandler.cs:356>) [CP:678](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/CreatePurchaseHandler.cs:678>) [PI:80](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/PhysicalIntakeViewModel.cs:80>).

## 10. Five tracking modes

Pack is an alias of Container, value5, not a sixth mode. Mode and overlay flags belong to Product. [CM:5](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Catalog/CatalogModels.cs:5>)

| Mode | Product setup | Actual intake input | Generated stock/identity | History restriction / current UI |
|---|---|---|---|---|
| Quantity | BaseUnit/UOM, prices; Serial/IMEI disabled | Quantity and cost; no identity rows | Base stock, movement and lot; no InventoryUnit or TrackingCode | Product identity/policy locks with history; available in UI |
| Length | Length base UOM/conversion; Serial/IMEI disabled | Decimal length/quantity and cost; no identity rows | Bulk base stock/lot; no physical item code | Same policy lock; available in UI |
| IndividualPiece | UOM and physical mode; overlays optional in backend | Whole base count; identity values only when enabled | One InventoryUnit/TrackingCode per base piece; blank overlay entries auto-generated when no flags | Available UI mode, but overlay controls enabled only for Serialized |
| Serialized | Physical mode and at least one of Serial/IMEI enabled | One identity row per exact whole base unit; required Serial and/or IMEI1, optional IMEI2 | System TrackingCode and pair ItemSequence plus user manufacturer identifiers | **BACKEND SUPPORTED / UI GAP:** new-product dropdown omits Serialized |
| Container / Pack | Container mode, base quantity conversion; optional overlays in backend | Intact entered-pack count and cost, required overlays when enabled | One InventoryUnit/TrackingCode per entered pack; stock delta = entered quantity × factor; whole pack acquisition cost | Available UI mode; opening/splitting workflow not established |

Evidence: [CM:61](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Catalog/CatalogModels.cs:61>) [CM:142](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Catalog/CatalogModels.cs:142>) [CP:659](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/CreatePurchaseHandler.cs:659>) [RI:491](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/ReceiveProductIntakeHandler.cs:491>) [RI:563](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/ReceiveProductIntakeHandler.cs:563>) [RI:633](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/ReceiveProductIntakeHandler.cs:633>) [PVM:182](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/ProductEditViewModel.cs:182>) [PVM:397](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/ProductEditViewModel.cs:397>).

For Serialized and IndividualPiece, **two boxes × factor12 = 24 physical base identities**. For Container, **two packs × factor12 = 24 base stock, two intact pack identities**, each with whole-pack acquisition cost. This is the present source distinction; unit name “pack” alone is insufficient.

The Container backend count uses `decimal.ToInt32(EnteredQuantity)`; physical base quantity must be whole, but no exhaustive explicit entered-pack integrality guard was found in the examined handlers. Normal UI requires whole physical identity count; do not assume all API fractional-pack cases are rejected. This is a static gap/ambiguity, not a reproduced test failure. [RI:563](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/ReceiveProductIntakeHandler.cs:563>) [CM:142](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Catalog/CatalogModels.cs:142>) [PI:213](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/PhysicalIntakeViewModel.cs:213>).

Container physical quantity is derived from receipt movement effects / distinct linked physical units and checked against lot cost provenance. It is not reconstructed from current mutable stock balance, current UOM or original order quantity. No `BaseQuantityPerUnitSnapshot` property, `PhysicalSequence` field, or OpenPack/OpenContainer command was found in the inspected source. Do not invent these names or an automatic loose-content identity flow. [REPO:391](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Repositories/EfRepositories.cs:391>).

## 11. Identifier generation and physical identity

| Identifier | Authority / timing | Operator enters? |
|---|---|---|
| CompanyCode | Company.Code, optional explicit input or available suggestion at master save | Optional valid code |
| CategorySymbol | Category.IdentitySymbol, optional explicit input or suggestion | Optional valid symbol |
| ModelCode | Product text token, explicit or suggested from Model | Optional token; distinct effective identity for variants |
| ProductCode / SKU | Product.Sku at master save; structured formula or fallback | May type, but backend formula overrides when applicable |
| DealerCode / SupplierCode | Supplier master locked prefix sequence | No ordinary DealerCode editing; backend prefix override exists |
| SupplierProduct.NextItemSequence | Pair cursor created at1 and advanced under lock / high-water reconciliation | NO |
| ItemSequence | Positive long allocated for each physical entry | NO |
| TrackingCode | DealerCode + SKU + allocated sequence inside receipt transaction | NO |
| ProductSkuSnapshot / SupplierCodeSnapshot | SKU and DealerCode copied onto InventoryUnit at creation | NO |
| Serial / IMEI | Manufacturer identity supplied at receipt when policy enabled | YES; not a generated shop TrackingCode |
| Internal IDs | Generated entity IDs / authoritative references | Selected by UI; no manual identity allocation |

Evidence: [TM:45](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Catalog/TraceabilityModels.cs:45>) [TM:94](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Catalog/TraceabilityModels.cs:94>) [TM:310](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Catalog/TraceabilityModels.cs:310>) [TM:383](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Catalog/TraceabilityModels.cs:383>) [PUCA:178](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Services/PhysicalUnitCreationAuthority.cs:178>) [PUCA:217](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Services/PhysicalUnitCreationAuthority.cs:217>).

**Grammar:** `<DealerCode>-<ProductSKU>-<ItemSequence:D6>`.

Illustration: `AB1-SAMP-A55-8256-BLK-000001`.
Dealer segment = Supplier; SKU segment embeds Company `SA`, Category `MP`, ModelCode `A55-8256-BLK`; final segment is pair-scoped physical sequence1. Company/Category/Model do not add independent extra segments outside SKU. A fallback SKU need not expose all those concepts. D6 is minimum six digits, not maximum: sequence1000000 has seven digits. Business code may contain dashes; relational IDs are the authority, not manually splitting the code. [TM:94](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Catalog/TraceabilityModels.cs:94>) [TM:383](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Catalog/TraceabilityModels.cs:383>).

Physical authority locks/validates masters and pair, normalizes manufacturer inputs, checks duplicates, creates/loads pair, reconciles high water, reserves a sequence range, then creates InventoryUnits and snapshots. Successful transaction commits before the result is returned. Reserved gaps can survive a rollback through high-water recording; issued numbers must not be reused. New purchase/date/invoice/deactivation/void does not reset the pair. Different suppliers of the same Product have independent pair sequences. [PUCA:178](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Services/PhysicalUnitCreationAuthority.cs:178>) [PUCA:204](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Services/PhysicalUnitCreationAuthority.cs:204>) [PLAT:83](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Services/PlatformServices.cs:83>) [ARCH:6615](<C:/Users/muham/OneDrive/Desktop/Point of Sale/docs/Edge_Retails_Final_Architecture_Report_v1.md:6615>) [TRACK:206](<C:/Users/muham/OneDrive/Desktop/Point of Sale/EDGE_RETAILS_TRACKING_SYSTEM_FINAL_CERTIFICATION_AND_FREEZE_2026-10-03.md:206>).

Labels use already-committed InventoryUnit IDs and persisted TrackingCode/sequence/snapshots. Print/reprint does not create inventory or allocate another number; printing failure does not undo receipt. The examined receipt path did not establish an atomic durable ORIGINAL label-request outbox, so the broader documented requirement is not presumed implemented here. [PI:478](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/PhysicalIntakeViewModel.cs:478>) [STICK:39](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Production/Printing/EfPhysicalStickerDocumentSource.cs:39>) [PRINT:41](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Production/Printing/PrintPhysicalStickersHandler.cs:41>) [PI:566](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/PhysicalIntakeViewModel.cs:566>).

InventoryUnit fields are Id, ProductId, SupplierProductId?, OriginType, SourceWarrantyClaimItemId?, ItemSequence?, TrackingCode?, SupplierCodeSnapshot?, ProductSkuSnapshot?, SerialNumber?, Imei1?, Imei2?, Status, AcquisitionCost, InventoryLotId?, SourcePurchaseItemId?, SourceWarrantyCaseId?, SourceStockAdjustmentItemId?, CreatedAt, Version. Purchase origin requires PurchaseItem provenance; alternative warranty/adjustment origins have their own exclusive source rules. Physical identity may remain after sale; SupplierProduct denotes original provenance, not current ownership or current sellable stock. [IM:170](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Inventory/InventoryModels.cs:170>) [IM:192](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Inventory/InventoryModels.cs:192>) [IC:140](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Persistence/Configurations/InventoryConfigurations.cs:140>).

## 12. Duplicate rules and immutability

### Proven duplicates

| Duplicate | Current rule |
|---|---|
| Company Name/Code | Handler case-insensitive normalized lookup includes inactive; ordinary unique string indexes |
| Category Name/Symbol | Same handler policy; unique indexes |
| Unit Name/Symbol | Case-insensitive handler rejection; unique indexes |
| Product SKU | Normalized lookup including inactive; unique nonnull SKU |
| ModelCode alone | No global unique constraint; same Company/Category/effective code collides through SKU |
| Supplier Name/Phone | **Not unique**; duplicate names can exist, and purchase UI excludes ambiguous name groups |
| Supplier DealerCode | Unique nonnull and permanently guarded |
| SupplierProduct pair | Unique SupplierId/ProductId; edit/reactivate same pair |
| ProductUnit pair | Unique ProductId/UnitId; default active purchase/sale uniqueness |
| TrackingCode | Unique nonnull |
| Pair ItemSequence | Unique SupplierProductId/ItemSequence when nonnull |
| Serial/IMEI | Normalized claims unique by IdentifierType/NormalizedValue, plus individual column uniqueness; both IMEI slots share one normalized IMEI namespace |
| Manufacturer identifier across sold units | Lookup has no status exemption; sale does not release identity |

Evidence: [RH:87](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/CatalogReferenceHandlers.cs:87>) [RH:300](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/CatalogReferenceHandlers.cs:300>) [RH:512](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/CatalogReferenceHandlers.cs:512>) [CC:135](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Persistence/Configurations/CatalogConfigurations.cs:135>) [PC:32](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Persistence/Configurations/PartyConfigurations.cs:32>) [TRC:15](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Persistence/Configurations/TraceabilityConfigurations.cs:15>) [IC:193](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Persistence/Configurations/InventoryConfigurations.cs:193>) [IC:246](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Persistence/Configurations/InventoryConfigurations.cs:246>) [REPO:481](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Repositories/EfRepositories.cs:481>) [PUCA:142](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Services/PhysicalUnitCreationAuthority.cs:142>) [NP:454](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/NewPurchaseViewModel.cs:454>).

Serial normalization trims, applies Unicode FormKC and uppercase, and rejects control/invisible format characters. Backend rejects duplicate normalized identities within the submitted batch and against persisted identities. This study inspected these rules; it did not execute race tests. [TM:587](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Catalog/TraceabilityModels.cs:587>) [PUCA:142](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Services/PhysicalUnitCreationAuthority.cs:142>).

### What changes later

| Field / relationship | Actual implemented rule |
|---|---|
| DealerCode | Permanent once assigned, explicit DbContext save guard |
| BaseUnit | Ordinary Product edit always rejects change, even before stock |
| SKU, ModelCode, Company, Category, TrackingMode, Serial/IMEI flags | Normal update rejects effective change after HasStockOrHistory |
| “History” | Includes stock, movement/units/lots, purchases/sales/returns/warranty **and any SupplierProduct row**; linking alone can lock identity |
| Model display / Name / Brand / prices / warranty / attributes | Ordinary metadata editable; effective derived identity changes still hit guards |
| Company Code / Category Symbol | Locked when referenced by active Products or affected Product history |
| Supplier link | Can deactivate/reactivate/add other pair; sequence preserved |
| ProductUnit factor and flags | Editable through current configuration handler; no history guard |
| Unit name/symbol/precision | Current save permits edits; deactivation blocked by active usage |
| Physical codes/sequence/snapshots | Canonical immutable identity; examined business flows preserve them, but no universal DbContext/DB field-update guard established |

Evidence: [DB:129](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Persistence/EdgeRetailsDbContext.cs:129>) [PH:393](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/ProductManagementHandlers.cs:393>) [PH:400](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/ProductManagementHandlers.cs:400>) [PH:425](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/ProductManagementHandlers.cs:425>) [READ:159](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Services/ProductManagementReadService.cs:159>) [READ:181](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Services/ProductManagementReadService.cs:181>) [RH:118](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/CatalogReferenceHandlers.cs:118>) [RH:331](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/CatalogReferenceHandlers.cs:331>) [UH:136](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/ProductUnitHandlers.cs:136>) [RH:529](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/CatalogReferenceHandlers.cs:529>).

Do not overstate permanent SKU reservation: canonical text is stronger than current ordinary-handler behavior. Before broad history, backend can change SKU; no abandoned-SKU reservation history was found. UI normal Edit nevertheless makes SKU read-only. Physical fields have public setters; observed preservation is not proof that every possible direct write is universally prohibited. [ARCH:7350](<C:/Users/muham/OneDrive/Desktop/Point of Sale/docs/Edge_Retails_Final_Architecture_Report_v1.md:7350>) [PH:400](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/ProductManagementHandlers.cs:400>) [PVM:205](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/ProductEditViewModel.cs:205>) [IM:170](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Inventory/InventoryModels.cs:170>).

## 13. Scenario matrix

| Scenario | Actual current entry / receipt workflow |
|---|---|
| A. New company + model text + product + supplier | Create independent references and Supplier in either order; enter Model/ModelCode on new Product; configure UOM; optional link; purchase then receive |
| B. Existing company + new model text/product + existing supplier | Reuse Company/Category/Unit and Supplier; new Product with new effective SKU; link existing Supplier |
| C. Existing model text + new variant + existing supplier | New Product for separately stocked variant; repeat display Model; distinct effective ModelCode/SKU; not ProductUnit or new Model master |
| D. Existing product + new supplier | Create Supplier; Edit same Product → tick new Supplier Link; no duplicate Product |
| E. Existing product + same supplier + new purchase | Reuse masters/pair/UOM; fresh invoice/operation identity; receive outstanding stock; pair sequence continues |
| F. Serialized | Backend setup supported with Serial and/or IMEI policy; new Product UI cannot select mode; existing valid catalog can go through purchase/intake identity capture |
| G. Container/Pack | Configure Container and UOM factor; receive intact pack count; base-stock conversion but one physical identity per pack; no proven open/split workflow |
| H. Quantity-only | Configure Quantity without identity policies; purchase and receive quantity/cost into balance/lot; no per-piece TrackingCode |

These are source-derived paths, not operational executions or new features. Evidence is in §§4–11.

## 14. Explicit answers to owner questions

**Q1. Supplier first or Product first?** Either. Category/BaseUnit must precede new Product; both Supplier/Product must precede explicit linking and a purchase. Supplier is independent.

**Q2. Does Product contain Supplier?** No direct SupplierId; optional multiple links via SupplierProduct.

**Q3. Does Supplier contain Product?** No owned Product child master; it has many bridge relationships.

**Q4. Does Model contain Supplier?** No. Model is Product text; suppliers link to Product.

**Q5. Same Product with 2, 3 or 10 Suppliers?** Yes, distinct supplier/product pairs; no model-defined maximum.

**Q6. Same displayed model with variants?** Different Products for separate stocked identities, distinct effective codes, shared descriptive Model text; optional JSON metadata. ProductUnits are conversions.

**Q7. When is TrackingCode generated?** Inside successful physical receipt/creation transaction, not Product save or remote order save. Official code is available after commit.

**Q8. When Serial/IMEI?** At intake identity capture when enabled; NewPurchase also exposes pre-capture. Manufacturer identifiers are user supplied; TrackingCode is backend generated.

**Q9. When is stock created?** At authorized receipt commit. Remote Save New Purchase is order-only; immediate-receive backend paths combine it with receipt.

**Q10. What must not be duplicated?** Same catalog Product merely for another supplier, an existing supplier/product pair, an existing ProductUnit pair, issued TrackingCodes/sequences or manufacturer identities. Different genuine variants need distinct Product identities. Supplier names/phones are not uniqueness authority.

## 15. Data-entry dependency table

| Entry | Must exist before | User enters | Auto generated | Editable later? |
|---|---|---|---|---|
| Company | None | Name; optional Code | Available code suggestion, ID/version | Name yes; Code usage/history restricted |
| Category | None | Name; optional symbol | Symbol suggestion, ID/version | Name yes; symbol usage/history restricted |
| Unit | None | Name/Symbol/precision | ID | Yes; usage restricts deactivation |
| Model | Product context, no standalone master | Model display/optional ModelCode | Suggested ModelCode | Display yes; effective identity history restricted |
| Product | Active Category/BaseUnit; optional Company | Name/classification/policy/prices/metadata | SKU derivation, ID, base ProductUnit | Metadata yes; identity/history/BaseUnit rules |
| ProductUnit | Product + active Unit | Factor/operation/default flags | Mapping ID, base defaults | Current handler allows factor/flags updates |
| Supplier | None | Name/contact; backend optional prefix | DealerCode, ID/timestamps | Metadata yes; assigned DealerCode no |
| SupplierProduct | Existing Product and Supplier | Checked/active desired link | Pair ID/cursor1; timestamps/version | Toggle/reuse; no reset |
| Purchase | Active Supplier/Product/purchaseable ProductUnit, actor | Invoice/date/quantities/costs/charges/note | Internal ID/number and quantity/cost snapshots | Use defined lifecycle/void/intake; no free rewrite implied |
| InventoryUnit | Authorized physical receipt + provenance/policy | Required manufacturer values, receipt count | ID, pair sequence, TrackingCode/snapshots | Lifecycle changes preserve identity; not ordinary master editing |

## 16. Current gaps and ambiguities

| Classification | Source-supported finding | Evidence |
|---|---|---|
| CLEAR | Product/Supplier M:N editor exists through Supplier Links; Model is text, Product master creates no stock | [PUI:199](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Views/Dialogs/ProductEditDialog.xaml:199>) [CM:39](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Catalog/CatalogModels.cs:39>) [PH:296](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/ProductManagementHandlers.cs:296>) |
| UI GAP — BACKEND SUPPORTED | Serialized omitted from new-product dropdown; overlay controls enabled only for Serialized, including for IndividualPiece/Container backend policies | [PVM:182](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/ProductEditViewModel.cs:182>) [PVM:397](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/ProductEditViewModel.cs:397>) |
| UI GAP — BACKEND SUPPORTED | Supplier explicit DealerPrefix for names with <2 ASCII letters is absent from dialog | [SUI:7](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Views/Dialogs/SupplierEditDialog.xaml:7>) [SC:203](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Server/Controllers/SuppliersController.cs:203>) |
| UI GAP | Remote Product/Supplier selection uses one page of200 without exposed continuation in this dialog; actual operational omissions were not measured | [RPM:14](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Services/RemoteProductManagementService.cs:14>) |
| UI GAP | NewPurchase duplicate supplier names cannot be selected; Product-ID merging collapses unit-distinct selections | [NP:296](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/NewPurchaseViewModel.cs:296>) [NP:454](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/NewPurchaseViewModel.cs:454>) |
| UI GAP / misleading text | “Stock updated” toast follows remote order-only save | [NP:401](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/NewPurchaseViewModel.cs:401>) [RPO:179](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Services/RemotePurchasingInventoryService.cs:179>) |
| PARTIALLY IMPLEMENTED | Attributes validates object/version only, not typed per-key schema; no formal variant/model master | [CM:89](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Catalog/CatalogModels.cs:89>) |
| BACKEND GAP / static concern | Manual missing-pair activation has row lookup but no pair resource lock; unique constraint remains final barrier; friendly concurrent first-link result not proven | [PH:571](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/ProductManagementHandlers.cs:571>) [TRC:15](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Persistence/Configurations/TraceabilityConfigurations.cs:15>) |
| BACKEND GAP / ambiguity | Mutable current UOM factor used for deferred receipt vs snapshotted order/pending UI; supplied unit need not equal ordered unit | [RI:442](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/ReceiveProductIntakeHandler.cs:442>) [RI:481](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/ReceiveProductIntakeHandler.cs:481>) [CP:359](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/CreatePurchaseHandler.cs:359>) |
| UI GAP | Deferred purchase save requires overlay identity pre-capture although backend order can omit it; intake starts fresh rows | [NP:97](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/NewPurchaseViewModel.cs:97>) [CP:678](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/CreatePurchaseHandler.cs:678>) [PI:80](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/PhysicalIntakeViewModel.cs:80>) |
| BACKEND GAP / source mismatch | Deferred receipt UI sends original entered cost, overriding charge-inclusive order cost fallback | [PI:443](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/PhysicalIntakeViewModel.cs:443>) [RI:487](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/ReceiveProductIntakeHandler.cs:487>) |
| BACKEND GAP / ambiguity | No exhaustive explicit entered-pack integer guard found; Container count converts decimal to integer | [RI:563](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/ReceiveProductIntakeHandler.cs:563>) |
| PARTIALLY IMPLEMENTED / not established | Intact pack provenance supported; opening/splitting pack flow not found; atomic original-label request not established from receipt trace | [REPO:391](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Repositories/EfRepositories.cs:391>) [RI:684](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/ReceiveProductIntakeHandler.cs:684>) [PI:478](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/PhysicalIntakeViewModel.cs:478>) |
| DOCUMENTATION AMBIGUITY | Historical core has BrandId/UnitId/three modes; live code has text Brand/Model, BaseUnitId and five modes | [ARCH:241](<C:/Users/muham/OneDrive/Desktop/Point of Sale/docs/Edge_Retails_Final_Architecture_Report_v1.md:241>) [CM:5](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Catalog/CatalogModels.cs:5>) |
| DOCUMENTATION AMBIGUITY | Canonical permanent SKU reservation/physical immutability stronger than guards proven by current code | [ARCH:7350](<C:/Users/muham/OneDrive/Desktop/Point of Sale/docs/Edge_Retails_Final_Architecture_Report_v1.md:7350>) [PH:400](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/ProductManagementHandlers.cs:400>) [DB:129](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Persistence/EdgeRetailsDbContext.cs:129>) |
| DOCUMENTATION AMBIGUITY | Canonical Supplier UpdatedAt differs from live entity without this field | [ARCH:1254](<C:/Users/muham/OneDrive/Desktop/Point of Sale/docs/Edge_Retails_Final_Architecture_Report_v1.md:1254>) [PM:17](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Parties/PartyModels.cs:17>) |

No gap above was repaired in this study. Static concerns are not newly certified test failures.

## 17. Wrong workflows to avoid

- Creating another copy of the same Product solely for another Supplier; add a SupplierProduct link.
- Assuming Supplier→Model→Product or Company→Model master hierarchy.
- Treating ProductUnits as RAM/color/size variants, or “pack” unit name as Container mode.
- Typing a replacement SKU and expecting it to override the structured backend formula.
- Saving two variants with the same effective Company/Category/ModelCode and expecting distinct SKUs.
- Assuming Product save or remote NewPurchase save receives stock.
- Manually inventing TrackingCode/ItemSequence or reusing an issued number after void/deactivation.
- Entering Serial/IMEI rows for bulk Quantity/Length.
- Mutating normal Product BaseUnit or identity/policy after guarded history, including supplier linking.
- Treating a deactivated bridge as a new sequence1 pair.
- Creating physical inventory records directly outside authorized receipt/provenance commands.
- Assuming print success is required for receipt commit, or reprint allocates new identities.
- Assuming all immutable-policy statements imply universal ORM/DB guards, or receipt uses the order's frozen UOM factor.

## 18. Inspection inventory

Citation keys below identify the live files inspected. Citations point to exact source lines; absolute local links open the shared workspace.

| Key | File |
|---|---|
| CM | [src/EdgeRetails.Domain/Catalog/CatalogModels.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Catalog/CatalogModels.cs>) |
| TM | [src/EdgeRetails.Domain/Catalog/TraceabilityModels.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Catalog/TraceabilityModels.cs>) |
| PH | [src/EdgeRetails.Application/Features/Catalog/ProductManagementHandlers.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/ProductManagementHandlers.cs>) |
| RH | [src/EdgeRetails.Application/Features/Catalog/CatalogReferenceHandlers.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/CatalogReferenceHandlers.cs>) |
| UH | [src/EdgeRetails.Application/Features/Catalog/ProductUnitHandlers.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/ProductUnitHandlers.cs>) |
| CC | [src/EdgeRetails.Infrastructure/Persistence/Configurations/CatalogConfigurations.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Persistence/Configurations/CatalogConfigurations.cs>) |
| TRC | [src/EdgeRetails.Infrastructure/Persistence/Configurations/TraceabilityConfigurations.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Persistence/Configurations/TraceabilityConfigurations.cs>) |
| PM | [src/EdgeRetails.Domain/Parties/PartyModels.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Parties/PartyModels.cs>) |
| PRH | [src/EdgeRetails.Application/Features/Parties/PartyHandlers.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Parties/PartyHandlers.cs>) |
| PC | [src/EdgeRetails.Infrastructure/Persistence/Configurations/PartyConfigurations.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Persistence/Configurations/PartyConfigurations.cs>) |
| DB | [src/EdgeRetails.Infrastructure/Persistence/EdgeRetailsDbContext.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Persistence/EdgeRetailsDbContext.cs>) |
| READ | [src/EdgeRetails.Infrastructure/Services/ProductManagementReadService.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Services/ProductManagementReadService.cs>) |
| PUI | [src/EdgeRetails.Desktop/Views/Dialogs/ProductEditDialog.xaml](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Views/Dialogs/ProductEditDialog.xaml>) |
| PVM | [src/EdgeRetails.Desktop/ViewModels/ProductEditViewModel.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/ProductEditViewModel.cs>) |
| PMV | [src/EdgeRetails.Desktop/Views/ProductManagementView.xaml](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Views/ProductManagementView.xaml>) |
| PMVM | [src/EdgeRetails.Desktop/ViewModels/ProductManagementViewModel.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/ProductManagementViewModel.cs>) |
| RPM | [src/EdgeRetails.Desktop/Services/RemoteProductManagementService.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Services/RemoteProductManagementService.cs>) |
| BPM | [src/EdgeRetails.Desktop/Services/BackendProductManagementService.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Services/BackendProductManagementService.cs>) |
| SUI | [src/EdgeRetails.Desktop/Views/Dialogs/SupplierEditDialog.xaml](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Views/Dialogs/SupplierEditDialog.xaml>) |
| SV | [src/EdgeRetails.Desktop/Views/SuppliersView.xaml](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Views/SuppliersView.xaml>) |
| SD | [src/EdgeRetails.Desktop/Views/SupplierDetailView.xaml](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Views/SupplierDetailView.xaml>) |
| SC | [src/EdgeRetails.Server/Controllers/SuppliersController.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Server/Controllers/SuppliersController.cs>) |
| RPO | [src/EdgeRetails.Desktop/Services/RemotePurchasingInventoryService.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Services/RemotePurchasingInventoryService.cs>) |
| NP | [src/EdgeRetails.Desktop/ViewModels/NewPurchaseViewModel.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/NewPurchaseViewModel.cs>) |
| CP | [src/EdgeRetails.Application/Features/Purchasing/CreatePurchaseHandler.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/CreatePurchaseHandler.cs>) |
| RI | [src/EdgeRetails.Application/Features/Purchasing/ReceiveProductIntakeHandler.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/ReceiveProductIntakeHandler.cs>) |
| PI | [src/EdgeRetails.Desktop/ViewModels/PhysicalIntakeViewModel.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/PhysicalIntakeViewModel.cs>) |
| PID | [src/EdgeRetails.Desktop/Views/Dialogs/PhysicalIntakeDialog.xaml](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Views/Dialogs/PhysicalIntakeDialog.xaml>) |
| PD | [src/EdgeRetails.Desktop/ViewModels/PurchaseDetailViewModel.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/PurchaseDetailViewModel.cs>) |
| PDV | [src/EdgeRetails.Desktop/Views/PurchaseDetailView.xaml](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Views/PurchaseDetailView.xaml>) |
| PUCA | [src/EdgeRetails.Infrastructure/Services/PhysicalUnitCreationAuthority.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Services/PhysicalUnitCreationAuthority.cs>) |
| IM | [src/EdgeRetails.Domain/Inventory/InventoryModels.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Inventory/InventoryModels.cs>) |
| IC | [src/EdgeRetails.Infrastructure/Persistence/Configurations/InventoryConfigurations.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Persistence/Configurations/InventoryConfigurations.cs>) |
| ARCH | [docs/Edge_Retails_Final_Architecture_Report_v1.md](<C:/Users/muham/OneDrive/Desktop/Point of Sale/docs/Edge_Retails_Final_Architecture_Report_v1.md>) |
| MAN | [docs/Architecture_Authority_Manifest.json](<C:/Users/muham/OneDrive/Desktop/Point of Sale/docs/Architecture_Authority_Manifest.json>) |
| TRACK | [EDGE_RETAILS_TRACKING_SYSTEM_FINAL_CERTIFICATION_AND_FREEZE_2026-10-03.md](<C:/Users/muham/OneDrive/Desktop/Point of Sale/EDGE_RETAILS_TRACKING_SYSTEM_FINAL_CERTIFICATION_AND_FREEZE_2026-10-03.md>) |
| PLAT | [src/EdgeRetails.Infrastructure/Services/PlatformServices.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Services/PlatformServices.cs>) |
| REPO | [src/EdgeRetails.Infrastructure/Repositories/EfRepositories.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Repositories/EfRepositories.cs>) |
| BPO | [src/EdgeRetails.Desktop/Services/BackendPurchasingInventoryService.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Services/BackendPurchasingInventoryService.cs>) |
| STICK | [src/EdgeRetails.Infrastructure/Production/Printing/EfPhysicalStickerDocumentSource.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Production/Printing/EfPhysicalStickerDocumentSource.cs>) |
| PRINT | [src/EdgeRetails.Application/Production/Printing/PrintPhysicalStickersHandler.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Production/Printing/PrintPhysicalStickersHandler.cs>) |
| PURCAT | [src/EdgeRetails.Infrastructure/Services/PurchaseCatalogReadService.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Services/PurchaseCatalogReadService.cs>) |
| NEWVIEW | [src/EdgeRetails.Desktop/Views/NewPurchaseView.xaml](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Views/NewPurchaseView.xaml>) |
| NAV | [src/EdgeRetails.Desktop/Navigation/PageViewModelFactory.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/Navigation/PageViewModelFactory.cs>) |
| PURCTL | [src/EdgeRetails.Server/Controllers/PurchasingController.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Server/Controllers/PurchasingController.cs>) |
| LEDGER | [docs/Master_Remediation_Tracking_Labels_Ledger_2026-10-01.md](<C:/Users/muham/OneDrive/Desktop/Point of Sale/docs/Master_Remediation_Tracking_Labels_Ledger_2026-10-01.md>) |
| GOV | [docs/Phase3_Master_Remediation_Governance_Progress_2026-10-02.md](<C:/Users/muham/OneDrive/Desktop/Point of Sale/docs/Phase3_Master_Remediation_Governance_Progress_2026-10-02.md>) |


Other targeted inspections included current supplier/Product Management adapter calls, catalog repositories and applicable initial/identity migrations. No entire repository reread was used after canonical authority was established.

## 19. Read-only preservation result

Read-only comparison of the opening 871-file src/tests/scripts/docs baseline against the pre-report inventory found 873 files, no deletions, and the same branch/HEAD. Concurrent workspace drift was observed in three existing files: `src/EdgeRetails.Desktop/Resources/Tables.xaml`, `src/EdgeRetails.Desktop/Resources/Buttons.xaml`, and `src/EdgeRetails.Application/Features/Sales/PosDraftHandlers.cs`. Two new files appeared: `tests/EdgeRetails.UnitTests/FrontendPass2FocusAccessibilityTests.cs` and `tests/EdgeRetails.UnitTests/Phase7Pass3IntegrityTests.cs`.

These changes were not made by this read-only study or its agents. They were preserved without rollback. The sales-draft/static styling changes do not change the catalog-master or receipt commands mapped here; installed UI rendering was not assessed. Accordingly, this report does **not** claim that the shared workspace remained globally frozen or matches a prior certification hash. “MODIFIED: NO” below describes this study's actions. The only file written by this study is this newly requested report. No previous report or source document was edited.

No implementation recommendations are made. The current workflow is mapped; known omissions and policy/implementation differences are recorded without runtime pass claims. No build/test/database certification gate was started by this study.

SOURCE MODIFIED: NO  
TESTS MODIFIED: NO  
DATABASE MODIFIED: NO  
MIGRATIONS CREATED: NO  
FRONTEND MODIFIED: NO  
GIT HISTORY MODIFIED: NO  
EXISTING DOCUMENTS MODIFIED: NO  
REQUESTED NEW REPORT CREATED: YES

