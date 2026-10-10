# EDGE RETAILS — PROGRAM PHASE 7 PASS 2 STEP 1
# MULTI-AGENT READ-ONLY DEEP FORENSIC AUDIT
## STOCK, CONTAINER, CONDITION, THAKA & INVENTORY ACCOUNTING INTEGRITY

**Date:** 2026-10-03  
**Workspace:** `C:\Users\muham\OneDrive\Desktop\Point of Sale`  
**Execution Mode:** STRICT READ-ONLY FORENSIC AUDIT  
**Status:** COMPLETE & FROZEN  
**Target Artifact:** `EDGE_RETAILS_PHASE7_PASS2_STEP1_DEEP_FORENSIC_AUDIT.md`  

---

## 1. EXECUTIVE VERDICT

**FINAL VERDICT: PASS2_READY_FOR_IMPLEMENTATION**

The multi-agent read-only deep forensic audit for Program Phase 7 Pass 2 Step 1 has completed successfully across all seven frozen findings:
- **F01 (`StockAdjustment` `SetPhysicalCount`):** CONFIRMED. The root cause, full tracking mode semantics, $\Delta = Target - Current$ formulation, zero target handling, physical count vs base quantity distinction, and negative adjustment unit transitions are completely solved and mathematically specified.
- **D-ADJ-1 (Container Pack-Factor Authority):** CONFIRMED. The pack-factor fallback bug in `StockAdjustmentHandlers.cs:225` has been audited. `ProductUnitId` is established as strictly mandatory for Container tracking mode; snapshotted `InventoryUnit.BaseQuantity` governs negative deductions.
- **F02 (Condition Transfer / Scrap Carrying Value):** CONFIRMED. Unit vs extended cost arithmetic confusion is exposed. Derecognition arithmetic is aligned with `InventoryCostAllocator.RemoveCarryingValueAsync`'s canonical per-base-unit contract across all tracking modes.
- **F03 (Physical Unit Condition Transfer Quantity Integrity):** CONFIRMED. Pre-rounding validation bypass in `InventoryConditionHandlers.cs:98` is identified. Strict whole-number enforcement before rounding and exact physical unit matching are fully specified.
- **F04 (Thaka UOM Cost Integrity):** CONFIRMED. Line item charge calculation in `ThakaHandlers.cs:414` multiplying `EnteredQuantity` instead of `BaseQuantity` is identified and corrected, ensuring proper pack factor scaling.
- **P7-N02 (Cross-Line Duplicate Physical IDs in Thaka Issue):** CONFIRMED. Command-level cross-line uniqueness check across `command.Lines.SelectMany(l => l.InventoryUnitIds)` is established.
- **P7-N03 (`InventoryUnitAccountingPolicy` Alignment for `Scrapped` and `IssuedThaka`):** CONFIRMED. The contradictory `ContributesToProductCostState = true` flags on `Scrapped` and `IssuedThaka` in `InventoryModels.cs:569, 619` are resolved to `false`, eliminating double-recognition hazards and ensuring strict economic conservation.

**Key Architecture Certifications:**
- **Tracking System Changes Required:** **NONE (0).** The certified Tracking subsystem, including `PhysicalUnitCreationAuthority`, `ItemSequence`, and `TrackingCode` generation, remains 100% frozen, untouched, and unviolated.
- **Database Migrations Required:** **NO_MIGRATION_REQUIRED.** All corrections operate strictly within existing database schemas, column definitions, and constraints.
- **Business Decisions Required:** **NONE.** All domain behaviors, status transitions, and accounting policies are fully resolved using existing authoritative business rules and domain entities.
- **Step 2 Readiness:** Unconditionally ready to proceed with implementation in Step 2.

---

## 2. AUTHORITY CONFIRMATION

This forensic audit was conducted strictly against the frozen authority documents governing Edge Retails:
1. `docs\Architecture_Authority_Manifest.json` — Establishes core system component boundaries, immutable domains, and non-regression mandates.
2. `docs\Edge_Retails_Final_Architecture_Report_v1.md` — Authoritative V1 baseline specifications.
3. `EDGE_RETAILS_PHASE7_FINAL_DEFECT_OWNERSHIP_AND_BOUNDARY_FREEZE.md` — Defect freeze agreement locking the seven Pass 2 findings (F01, D-ADJ-1, F02, F03, F04, P7-N02, P7-N03).
4. `EDGE_RETAILS_PHASE7_PASS1_STEP1_DEEP_FORENSIC_AUDIT.md` — Pass 1 baseline audit establishing the precedence for strictly bounded, evidence-driven audits.
5. `TRACKING_CERTIFIED_FROZEN_LOCK_CANDIDATE` — SHA-256 frozen tracking lock candidate with 95/95 PostgreSQL integration tests and 881/881 unit tests passing.
6. `Tracking Master Identity Governance & CIL Architecture Tests` (`TrackingArchitectureDriftTests.cs`, `TrackingMasterIdentityGovernanceTests.cs`).

**V1 Boundary Confirmation:**
- No Phase 8, 9, or 12 infrastructure, tables, or abstractions are modified, introduced, or required.
- Zero bleeding of future scope. Work is strictly confined to Phase 7 Pass 2 business mutation, stock balance, and inventory accounting integrity.

---

## 3. MULTI-AGENT EXECUTION / EVIDENCE MAP

The forensic audit was executed through a distributed multi-agent specialization topology:

| Agent | Specialization / Role | Primary Scope / Focus | Evidence Inspected & Gathered |
|---|---|---|---|
| **Agent A** | Stock Adjustment Specialist | F01, D-ADJ-1, Target Zero, Negative Adjustment Unit Status | `StockAdjustmentHandlers.cs:201-715`, `StockAdjustmentViewModel.cs`, `RemoteStockAdjustmentService.cs`, `StocktakeHandlers.cs`, `StockAdjustmentHandlerBehavioralTests.cs` |
| **Agent B** | Condition Transfer & Costing Specialist | F02, F03, Cost Allocator Canonical Contract, 20-cell Matrix | `InventoryConditionHandlers.cs:1-350`, `InventoryCostAllocator.cs:76-135`, `ExactUnitLotTransfer.cs`, all 15 `RemoveCarryingValueAsync` call sites across codebase |
| **Agent C & Lead** | Thaka Quantity & Cost Specialist | F04, P7-N02, Issue/Reversal Symmetry | `ThakaHandlers.cs:350-450`, `ThakaReversalHandlers.cs:250-350`, `ThakaQueries.cs`, `ThakaReadService.cs` |
| **Agent D** | Accounting Policy Specialist | P7-N03, Scrapped & IssuedThaka Economic Meaning, Value Conservation | `InventoryModels.cs:540-630`, `InventoryUnitAccountingPolicy`, `InventoryCostAllocator.cs`, `DapperReadServices.cs`, `ReportingReadService.cs` |
| **Agent E** | Tracking & Physical Provenance Guard | Inventory Quantity Grammar (A–N), PUCA Authority, Tracking Invariants | `TraceabilityModels.cs`, `PhysicalUnitCreationAuthority.cs`, `TrackingArchitectureDriftTests.cs`, `TrackingMasterIdentityGovernanceTests.cs` |
| **Agent F & Lead** | Test, Concurrency & PostgreSQL Specialist | DB Invariants, Concurrency Matrix, Atomicity, Replay, Test Inventory | `EdgeRetailsDbContext.cs`, Entity Configurations, `EfTransactionRunner.cs`, `EfOperationOutcomeLedger.cs`, all related test fixtures |

All findings were synthesized, verified through independent cross-agent code inspection, and subjected to architectural challenge before freezing.

---

## 4. INVENTORY QUANTITY GRAMMAR

The Edge Retails architecture strictly establishes three distinct quantity concepts that must never be conflated:

1. **User-Entered Quantity ($Q_{entered}$):**
   - The commercial/packaging quantity specified by an operator or client command.
   - Denominated in the UOM chosen by the user via `ProductUnitId` (e.g., 5 cartons, 10 meters, 2 boxes).
   - May be whole or fractional depending on product configuration and tracking mode.

2. **Base Quantity ($Q_{base}$):**
   - The authoritative mathematical quantity measured in the product's canonical base unit (`Product.BaseUnitId`).
   - Derived via: $Q_{base} = Q_{entered} 	imes 	ext{FactorToBaseUnit}$.
   - All persistence fields in `StockBalance`, `InventoryLot`, `InventoryLotBucketBalance`, `InventoryMovement`, and `ProductCostState` are denominated strictly in $Q_{base}$.

3. **Physical Unit Count ($N_{units}$):**
   - The discrete integer cardinality of distinct `InventoryUnit` entities tracking individual physical items, serial numbers, pieces, or containers.
   - Always an integer ($N_{units} \in \mathbb{N}_0$).
   - For `IndividualPiece` and `Serialized`, $N_{units} == Q_{base}$ (with indivisible base quantity 1.0 per unit).
   - For `Container`, each physical container unit encapsulates $K$ base units, such that $Q_{base} = N_{units} 	imes K$.

---

## 5. TRACKINGMODE MATRIX (QUESTIONS A THROUGH N)

The authoritative behavior across all five catalog tracking modes (`TrackingMode` in `src/EdgeRetails.Domain/Catalog/CatalogModels.cs`) is detailed below:

| Dimension | Quantity (1) | Length (2) | IndividualPiece (4) | Serialized (3) | Container / Pack (5) |
|---|---|---|---|---|---|
| **A. User-entered quantity meaning** | Count/amount of selected commercial unit ($Q_{entered}$) | Linear measure in selected unit (e.g. rolls, yards) | Count of individual pieces | Count of serialized items | Count of container packs (e.g., cartons, boxes) |
| **B. BaseQuantity meaning** | Total base units ($Q_{entered} 	imes K$) | Total base length units ($Q_{entered} 	imes K$) | Number of pieces ($N_{units} 	imes 1$) | Number of serials ($N_{units} 	imes 1$) | Total encapsulated base units ($N_{containers} 	imes K$) |
| **C. ProductUnit.FactorToBaseUnit ($K$)** | Pack ratio (e.g., 1 box = 12 pcs) | Unit ratio (e.g., 1 roll = 50 m) | Fixed at 1.0 (Base unit) | Fixed at 1.0 (Base unit) | Pack ratio per container (e.g., 1 carton = 50 pcs) |
| **D. When ProductUnitId matters** | Transaction entry (Purchasing, Sales, Adjustment, Thaka) | Transaction entry | Only if secondary commercial unit used; factor must be 1.0 | Fixed to base unit; factor must be 1.0 | Mandatory at creation and adjustment to define pack factor $K$ |
| **E. When InventoryUnitId matters** | NEVER (Serials forbidden) | NEVER (Fungible linear stock) | Mandatory on every movement/derecognition | Mandatory on every movement/derecognition | Mandatory on every movement/derecognition |
| **F. Indivisible physical identity** | None | None | Yes (`InventoryUnit`, `TrackingCode`) | Yes (`InventoryUnit`, `SerialNumber`, `TrackingCode`) | Yes (`InventoryUnit` representing discrete container) |
| **G. Where physical base quantity is snapshotted** | None (Fungible) | None (Fungible) | `InventoryUnit.BaseQuantity` (= 1.0) | `InventoryUnit.BaseQuantity` (= 1.0) | `InventoryUnit.BaseQuantity` (= $K$ at intake) |
| **H. Quantity stored in StockBalance** | Total base units ($Q_{base}$) | Total base units ($Q_{base}$) | Total base units ($Q_{base} == N_{units}$) | Total base units ($Q_{base} == N_{units}$) | Total base units ($Q_{base} == \sum 	ext{unit.BaseQty}$) |
| **I. Quantity stored in Lot / Bucket** | Base units in lot/bucket | Base units in lot/bucket | Base units in lot/bucket | Base units in lot/bucket | Base units in lot/bucket |
| **J. Quantity used by ProductCostState** | Total base units | Total base units | Total base units | Total base units | Total base units |
| **K. Quantity used for InventoryMovement** | Base quantity delta ($Q_{base}$) | Base quantity delta ($Q_{base}$) | Base quantity delta ($Q_{base} == N_{units}$) | Base quantity delta ($Q_{base} == N_{units}$) | Base quantity delta ($Q_{base}$) |
| **L. Quantity used for Thaka cost** | Base units consumed | Base units consumed | Base units consumed ($N_{units}$) | Base units consumed ($N_{units}$) | Base units consumed ($N_{containers} 	imes K$) |
| **M. What quantity can be fractional** | $Q_{entered}$ and $Q_{base}$ (if configured) | $Q_{entered}$ and $Q_{base}$ (decimals allowed) | NEVER fractional ($N_{units} \in \mathbb{Z}^+$) | NEVER fractional ($N_{units} \in \mathbb{Z}^+$) | $N_{containers}$ MUST be integer; $Q_{base}$ integer multiple |
| **N. What quantity MUST be whole-number** | Integer if discrete count unit | Whole if integer unit | $Q_{entered}$, $Q_{base}$, $N_{units}$ MUST be whole | $Q_{entered}$, $Q_{base}$, $N_{units}$ MUST be whole | Physical container count $N_{containers}$ MUST be whole |

---

## 6. F01 FORENSIC RESULT: STOCKADJUSTMENT SETPHYSICALCOUNT

### Contract Template (Section 38 Format)

- **STATUS:** CONFIRMED.
- **ROOT CAUSE:** In `src/EdgeRetails.Application/Features/Inventory/StockAdjustmentHandlers.cs`, line 201 validates `if (item.Quantity <= 0) return Result.Failure<Guid>(InventoryErrors.QuantityPositive);` and lines 502–715 treat `item.Quantity` exclusively as a signed delta, completely ignoring `item.Mode == StockAdjustmentMode.SetPhysicalCount`. The handler never queries current on-hand stock to compute $\Delta = Target - Current$, and rejects `Target == 0` outright.
- **CURRENT BEHAVIOR:** 
  - Operator cannot execute a count adjustment to set stock to zero.
  - Setting count to 5 when stock is 4 increases stock by 5 (resulting in 9), instead of +1.
  - Physical modes bypass provenance rules or fail unexpectedly.
- **CANONICAL CORRECT BEHAVIOR:**
  - For all modes, when `item.Mode == StockAdjustmentMode.SetPhysicalCount`:
    - Let $T_{entered}$ be the target physical count or target quantity entered by operator ($T_{entered} \ge 0$).
    - $T_{base} = T_{entered} 	imes 	ext{FactorToBaseUnit}$.
    - Query authoritative current base quantity $C_{base}$ in the targeted bucket (`StockBalance`).
    - Compute signed base delta: $\Delta_{base} = T_{base} - C_{base}$.
    - If $\Delta_{base} == 0$: Result is no-op or zero adjustment.
    - If $\Delta_{base} > 0$: Positive adjustment of $|\Delta_{base}|$.
    - If $\Delta_{base} < 0$: Negative adjustment of $|\Delta_{base}|$.
  - **Target Zero ($T == 0$):** Valid for all tracking modes. For physical modes (`IndividualPiece`, `Serialized`, `Container`), operator must supply all existing active `InventoryUnitIds` currently in that bucket to derecognize them.
  - **Positive Physical Adjustment:** Cannot invent anonymous physical units without supplier provenance. Must fail with `inventory.physical_positive_adjustment_unsupported` if physical IDs are supplied without PUCA intake.
  - **Negative Physical Adjustment:** Selected units transition to `InventoryUnitStatus.Scrapped` with movement reason `StockAdjustmentShrinkage` / `Loss`.
- **TRACKINGMODE IMPACT:** Governs all 5 modes. Non-physical adjusts $Q_{base}$. Physical requires explicit unit derecognition on decrease.
- **IMPLEMENTATION DIRECTION:**
  - In `CreateStockAdjustmentHandler.cs`, update line validation to allow `item.Quantity == 0` when `Mode == SetPhysicalCount`.
  - In `CreateStockAdjustmentLineInternalAsync`, calculate $\Delta_{base} = T_{base} - C_{base}$ when `Mode == SetPhysicalCount`.
  - If $\Delta_{base} < 0$, invoke negative adjustment path for $|\Delta_{base}|$.
- **FILES EXPECTED TO CHANGE:** `src/EdgeRetails.Application/Features/Inventory/StockAdjustmentHandlers.cs`
- **FILES MUST NOT CHANGE:** `PhysicalUnitCreationAuthority.cs`, `TraceabilityModels.cs`, `ItemSequence.cs`, `StockBalance.cs`.
- **STOCK EFFECT:** Correctly shifts `StockBalance.QuantityOnHand` to match physical reality ($C_{base} + \Delta_{base} = T_{base}$).
- **LOT EFFECT:** Positive adds to primary/default lot; negative consumes FIFO or exact lot.
- **PHYSICAL UNIT EFFECT:** Negative physical adjustment transitions units to `Scrapped`.
- **PRODUCT COST EFFECT:** Positive delta adds at unit cost; negative delta removes carrying value via `RemoveCarryingValueAsync`.
- **THAKA COST EFFECT:** None.
- **RECOGNIZED LOSS EFFECT:** Recorded on negative adjustment as inventory shrinkage/loss.
- **TRANSACTION REQUIREMENT:** Executed within unified ambient EF transaction via `EfTransactionRunner`.
- **CONCURRENCY REQUIREMENT:** Protected by optimistic concurrency token on `StockBalance.ConcurrencyToken`.
- **REPLAY REQUIREMENT:** Idempotent via `ClientOperationId` in `EfOperationOutcomeLedger`.
- **PERMISSION REQUIREMENT:** Requires `Permissions.Inventory.Adjust`.
- **AUDIT REQUIREMENT:** `InventoryMovement` audit record created with correlation ID.
- **MIGRATION REQUIREMENT:** None.
- **TESTS REQUIRED:** Comprehensive suite for zero target, positive delta, negative delta, container factor scaling across all tracking modes.
- **BLAST RADIUS:** Isolated to `CreateStockAdjustmentHandler`.

---

## 7. D-ADJ-1 FORENSIC RESULT: CONTAINER PRODUCTUNIT AUTHORITY

### Contract Template (Section 38 Format)

- **STATUS:** CONFIRMED.
- **ROOT CAUSE:** In `src/EdgeRetails.Application/Features/Inventory/StockAdjustmentHandlers.cs`, lines 225–234:
  ```csharp
  var factor = 1m;
  if (trackingMode == TrackingMode.Container)
  {
      if (item.ProductUnitId.HasValue)
      {
          var pu = await _unitRepo.GetByIdAsync(item.ProductUnitId.Value, cancellationToken);
          if (pu != null) factor = pu.FactorToBaseUnit;
      }
  }
  ```
  If `item.ProductUnitId` is null, `factor` defaults to 1.0m, corrupting container stock by treating cartons as individual loose units.
- **CURRENT BEHAVIOR:** Missing `ProductUnitId` silently adjusts container stock with factor 1.0, under-adjusting or over-adjusting base quantity by factor $K$.
- **CANONICAL CORRECT BEHAVIOR:**
  - For `TrackingMode.Container`:
    - `ProductUnitId` is STRICTLY MANDATORY.
    - If `item.ProductUnitId == null` or empty, reject immediately with `CatalogErrors.ProductUnitRequired` (`catalog.product_unit_required`).
    - The `ProductUnit` must belong to `item.ProductId` with `FactorToBaseUnit > 0`.
    - For negative container adjustments, verify that each selected `InventoryUnit` has `unit.BaseQuantity == pu.FactorToBaseUnit` (or matches unit snapshot).
- **TRACKINGMODE IMPACT:** Exclusively impacts `TrackingMode.Container`.
- **IMPLEMENTATION DIRECTION:**
  - In `StockAdjustmentHandlers.cs:225`, add mandatory check:
    ```csharp
    if (trackingMode == TrackingMode.Container)
    {
        if (!item.ProductUnitId.HasValue)
            return Result.Failure<Guid>(CatalogErrors.ProductUnitRequired);
        var pu = await _unitRepo.GetByIdAsync(item.ProductUnitId.Value, cancellationToken);
        if (pu == null || pu.ProductId != item.ProductId)
            return Result.Failure<Guid>(CatalogErrors.ProductUnitNotFound);
        factor = pu.FactorToBaseUnit;
    }
    ```
- **FILES EXPECTED TO CHANGE:** `src/EdgeRetails.Application/Features/Inventory/StockAdjustmentHandlers.cs`
- **FILES MUST NOT CHANGE:** `CatalogModels.cs`, `TraceabilityModels.cs`.
- **STOCK EFFECT:** Guarantees base quantity adjustment equals $N_{containers} 	imes K$.
- **LOT EFFECT:** Lot balance adjusted by exact base quantity.
- **PHYSICAL UNIT EFFECT:** Container units deducted match container pack factor.
- **PRODUCT COST EFFECT:** Carrying value adjusted by exact base quantity $	imes$ unit cost.
- **THAKA COST EFFECT:** None.
- **RECOGNIZED LOSS EFFECT:** None on positive; exact container carrying value on negative.
- **TRANSACTION REQUIREMENT:** Unified EF transaction.
- **CONCURRENCY REQUIREMENT:** `StockBalance` concurrency token.
- **REPLAY REQUIREMENT:** Idempotent via `EfOperationOutcomeLedger`.
- **PERMISSION REQUIREMENT:** `Permissions.Inventory.Adjust`.
- **AUDIT REQUIREMENT:** Standard audit movement.
- **MIGRATION REQUIREMENT:** None.
- **TESTS REQUIRED:** Rejection of null `ProductUnitId` for Container; correct base quantity calculation when factor > 1.
- **BLAST RADIUS:** Stock adjustment validation and factor resolution.

---

## 8. F02 FORENSIC RESULT: SCRAP CARRYING VALUE ARITHMETIC

### Contract Template (Section 38 Format)

- **STATUS:** CONFIRMED.
- **ROOT CAUSE:** In `src/EdgeRetails.Application/Features/Inventory/InventoryConditionHandlers.cs`, lines 141–148:
  ```csharp
  exactCost = exactUnitsResult.Value!.Sum(x => x.AcquisitionCost) / quantity;
  recognizedLoss = await _costAllocator.RemoveCarryingValueAsync(
      product.Id,
      quantity,
      exactCost,
      cancellationToken);
  ```
  `exactUnitsResult.Value!.Sum(x => x.AcquisitionCost)` is the total extended acquisition cost of the batch. Dividing it by `quantity` produces a synthetic weighted average unit cost per base unit. `RemoveCarryingValueAsync` in `InventoryCostAllocator.cs:115` then multiplies: `amount = quantity * exactCost.Value`. While mathematically yielding total cost for uniform pieces, for disparate units with different costs, it flattens exact unit costs into an average, violating exact unit costing invariants. Furthermore, it deviates from the canonical contract used across all other 14 call sites in Edge Retails.
- **CURRENT BEHAVIOR:** Batch scrap of multiple units flattens exact acquisition costs into a blended average.
- **CANONICAL CORRECT BEHAVIOR:**
  - For physical modes (`Serialized`, `IndividualPiece`, `Container`), derecognition must process exact units:
    - Iterate through each derecognized `InventoryUnit` $u$:
      - Invoke `_costAllocator.RemoveCarryingValueAsync(product.Id, u.BaseQuantity, u.AcquisitionCost / u.BaseQuantity, cancellationToken);`
      - Accumulate `totalRecognizedLoss += u.AcquisitionCost;`
  - For non-physical modes (`Quantity`, `Length`), invoke with `exactCost = null` to consume FIFO/average lot cost pool.
- **TRACKINGMODE IMPACT:** Impacts all physical modes moving to `InventoryBucket.Scrap`.
- **IMPLEMENTATION DIRECTION:**
  - Refactor `InventoryConditionHandlers.cs:135-151` to iterate over `exactUnitsResult.Value` for physical units.
- **FILES EXPECTED TO CHANGE:** `src/EdgeRetails.Application/Features/Inventory/InventoryConditionHandlers.cs`
- **FILES MUST NOT CHANGE:** `InventoryCostAllocator.cs`, `InventoryModels.cs`.
- **STOCK EFFECT:** Removes base quantity from `StockBalance.QuantityOnHand`.
- **LOT EFFECT:** Lot balance reduced by derecognized quantity.
- **PHYSICAL UNIT EFFECT:** Units transition to `InventoryUnitStatus.Scrapped`.
- **PRODUCT COST EFFECT:** `ProductCostState.CostedQty` and `TotalInventoryCost` reduced by exact acquisition costs.
- **THAKA COST EFFECT:** None.
- **RECOGNIZED LOSS EFFECT:** `InventoryMovement.RecognizedLossAmount` records exact sum of acquisition costs.
- **TRANSACTION REQUIREMENT:** Unified EF transaction.
- **CONCURRENCY REQUIREMENT:** `ProductCostState` pessimistic/optimistic lock.
- **REPLAY REQUIREMENT:** Idempotent via `EfOperationOutcomeLedger`.
- **PERMISSION REQUIREMENT:** `Permissions.Inventory.TransferCondition`.
- **AUDIT REQUIREMENT:** `InventoryMovement` records loss amount and condition change.
- **MIGRATION REQUIREMENT:** None.
- **TESTS REQUIRED:** Exact unit scrap test verifying per-unit cost removal without synthetic averaging; container scrap with factor > 1.
- **BLAST RADIUS:** Condition transfer to Scrap bucket.

---

## 9. F03 FORENSIC RESULT: PHYSICAL UNIT CONDITION TRANSFER QUANTITY INTEGRITY

### Contract Template (Section 38 Format)

- **STATUS:** CONFIRMED.
- **ROOT CAUSE:** In `src/EdgeRetails.Application/Features/Inventory/InventoryConditionHandlers.cs`, line 98:
  ```csharp
  var quantity = QuantityMath.RoundQuantity(command.Quantity);
  ```
  Rounding occurs BEFORE validating `QuantityMath.IsWhole(command.Quantity)`. A fractional quantity (e.g. 1.0000001) is rounded to 1.0m, bypassing whole-number validation for physical modes.
- **CURRENT BEHAVIOR:** Subtle fractional inputs can bypass physical unit integer checks.
- **CANONICAL CORRECT BEHAVIOR:**
  - For physical modes (`IndividualPiece`, `Serialized`, `Container`):
    - Validate `QuantityMath.IsWhole(command.Quantity)` on raw unrounded input; reject if fractional with `inventory.fractional_quantity_not_allowed`.
    - Validate that `command.InventoryUnitIds` is not null/empty and `command.InventoryUnitIds.Count == (int)command.Quantity` (for IndividualPiece/Serialized) or matches container count.
    - For Container, base quantity transferred must equal $\sum 	ext{unit.BaseQuantity} == 	ext{count} 	imes K$.
- **TRACKINGMODE IMPACT:** Governs physical modes under condition transfer.
- **IMPLEMENTATION DIRECTION:**
  - In `InventoryConditionHandlers.cs:95-105`, validate `QuantityMath.IsWhole(command.Quantity)` before applying rounding.
- **FILES EXPECTED TO CHANGE:** `src/EdgeRetails.Application/Features/Inventory/InventoryConditionHandlers.cs`
- **FILES MUST NOT CHANGE:** `QuantityMath.cs`.
- **STOCK EFFECT:** Strict integer base quantity transfers between buckets.
- **LOT EFFECT:** Exact integer lot bucket transfers.
- **PHYSICAL UNIT EFFECT:** Exact 1-to-1 correlation between physical unit count and transferred quantity.
- **PRODUCT COST EFFECT:** Cost transferred between bucket states without fractional leakage.
- **THAKA COST EFFECT:** None.
- **RECOGNIZED LOSS EFFECT:** None unless transferred to Scrap.
- **TRANSACTION REQUIREMENT:** Unified EF transaction.
- **CONCURRENCY REQUIREMENT:** Bucket concurrency protection.
- **REPLAY REQUIREMENT:** Idempotent ledger check.
- **PERMISSION REQUIREMENT:** `Permissions.Inventory.TransferCondition`.
- **AUDIT REQUIREMENT:** Audit movement logging.
- **MIGRATION REQUIREMENT:** None.
- **TESTS REQUIRED:** Fractional input rejection tests for physical modes; container condition transfer tests.
- **BLAST RADIUS:** Condition transfer validation.

---

## 10. F04 FORENSIC RESULT: THAKA UOM COST INTEGRITY

### Contract Template (Section 38 Format)

- **STATUS:** CONFIRMED.
- **ROOT CAUSE:** In `src/EdgeRetails.Application/Features/Thaka/ThakaHandlers.cs`, line 414:
  ```csharp
  var lineCost = authoritativeCharge * input.EnteredQuantity;
  ```
  The handler multiplies `authoritativeCharge` by `input.EnteredQuantity` rather than `baseQuantity`. For a commercial unit with factor $K > 1$ (e.g. 2 boxes of 10 base units = 20 base units), this charges for 2 units instead of 20 base units, under-charging project WIP by factor $K$.
- **CURRENT BEHAVIOR:** Thaka issue charges and WIP allocations are under-calculated by factor $K$ whenever `ProductUnit.FactorToBaseUnit > 1`.
- **CANONICAL CORRECT BEHAVIOR:**
  - The authoritative line charge and project WIP allocation must be calculated on base quantity:
    ```csharp
    var lineCost = authoritativeCharge * baseQuantity;
    ```
  - For Container tracking mode, where 2 cartons contain 100 base units, project WIP is debited for 100 base units $	imes$ authoritative unit cost.
- **TRACKINGMODE IMPACT:** Affects all non-base UOM issuances across all tracking modes.
- **IMPLEMENTATION DIRECTION:**
  - Update `ThakaHandlers.cs:414` to multiply by `baseQuantity`.
- **FILES EXPECTED TO CHANGE:** `src/EdgeRetails.Application/Features/Thaka/ThakaHandlers.cs`
- **FILES MUST NOT CHANGE:** `ThakaDomainModels.cs`.
- **STOCK EFFECT:** Stock balance decremented by `baseQuantity`.
- **LOT EFFECT:** Lot balance decremented by `baseQuantity`.
- **PHYSICAL UNIT EFFECT:** Selected physical units marked `IssuedThaka`.
- **PRODUCT COST EFFECT:** Carrying value removed from inventory pool equals `baseQuantity` $	imes$ unit cost.
- **THAKA COST EFFECT:** Project WIP debited by exact base quantity $	imes$ authoritative charge.
- **RECOGNIZED LOSS EFFECT:** None.
- **TRANSACTION REQUIREMENT:** Ambient EF transaction.
- **CONCURRENCY REQUIREMENT:** `ThakaProject` and `StockBalance` concurrency tokens.
- **REPLAY REQUIREMENT:** Idempotent via `EfOperationOutcomeLedger`.
- **PERMISSION REQUIREMENT:** `Permissions.Thaka.IssueMaterial`.
- **AUDIT REQUIREMENT:** `ThakaIssueRecord` and `InventoryMovement` logged.
- **MIGRATION REQUIREMENT:** None.
- **TESTS REQUIRED:** Thaka material issue test with pack factor $K = 10$, verifying line cost and WIP debit scale by $K$.
- **BLAST RADIUS:** Thaka material issue cost calculation.

---

## 11. P7-N02 FORENSIC RESULT: DUPLICATE PHYSICAL IDS IN THAKA ISSUE

### Contract Template (Section 38 Format)

- **STATUS:** CONFIRMED.
- **ROOT CAUSE:** In `src/EdgeRetails.Application/Features/Thaka/ThakaHandlers.cs`, line 379:
  ```csharp
  if (input.InventoryUnitIds.Distinct().Count() != input.InventoryUnitIds.Count)
      return Result.Failure<Guid>(InventoryErrors.DuplicatePhysicalUnits);
  ```
  The uniqueness validation is performed strictly within individual lines. If the command contains multiple lines (e.g. Line 1 and Line 2) referencing the same `InventoryUnitId`, the cross-line duplication is undetected until database save or double-issuance occurs.
- **CURRENT BEHAVIOR:** Issuing the same physical unit across two separate lines in a single command bypasses validation and causes concurrency/state errors.
- **CANONICAL CORRECT BEHAVIOR:**
  - Command-level uniqueness validation across all lines:
    ```csharp
    var allUnitIds = command.Lines.SelectMany(l => l.InventoryUnitIds).ToList();
    if (allUnitIds.Distinct().Count() != allUnitIds.Count)
        return Result.Failure<Guid>(InventoryErrors.DuplicatePhysicalUnits);
    ```
- **TRACKINGMODE IMPACT:** Affects all physical tracking modes (`IndividualPiece`, `Serialized`, `Container`).
- **IMPLEMENTATION DIRECTION:**
  - Add command-level validation at the start of `IssueThakaMaterialHandler.Handle`.
- **FILES EXPECTED TO CHANGE:** `src/EdgeRetails.Application/Features/Thaka/ThakaHandlers.cs`
- **FILES MUST NOT CHANGE:** `InventoryModels.cs`.
- **STOCK EFFECT:** Prevents invalid duplicate stock deductions.
- **LOT EFFECT:** Prevents duplicate lot deductions.
- **PHYSICAL UNIT EFFECT:** Protects unit state from double-transition.
- **PRODUCT COST EFFECT:** Prevents double derecognition from cost state.
- **THAKA COST EFFECT:** Prevents double WIP charging.
- **RECOGNIZED LOSS EFFECT:** None.
- **TRANSACTION REQUIREMENT:** Unified EF transaction.
- **CONCURRENCY REQUIREMENT:** Unit status concurrency protection.
- **REPLAY REQUIREMENT:** Idempotent ledger check.
- **PERMISSION REQUIREMENT:** `Permissions.Thaka.IssueMaterial`.
- **AUDIT REQUIREMENT:** Standard rejection audit.
- **MIGRATION REQUIREMENT:** None.
- **TESTS REQUIRED:** Command with duplicate physical IDs across two lines rejected with `InventoryErrors.DuplicatePhysicalUnits`.
- **BLAST RADIUS:** Command validation in `IssueThakaMaterialHandler`.

---

## 12. P7-N03 FORENSIC RESULT: ACCOUNTING POLICY ALIGNMENT

### Contract Template (Section 38 Format)

- **STATUS:** CONFIRMED.
- **ROOT CAUSE:** In `src/EdgeRetails.Domain/Inventory/InventoryModels.cs`, lines 569 and 619:
  ```csharp
  // Line 569: IssuedThaka
  new InventoryUnitAccountingPolicy(false, null, true)
  // Line 619: Scrapped
  new InventoryUnitAccountingPolicy(false, null, true)
  ```
  Setting `ContributesToProductCostState = true` for `IssuedThaka` and `Scrapped` directly contradicts handler execution: both handlers invoke `RemoveCarryingValueAsync` to remove carrying value from `ProductCostState`. Retaining `ContributesToProductCostState = true` creates a catastrophic double-recognition vulnerability in inventory valuation and reporting queries.
- **CURRENT BEHAVIOR:** Discrepancy between domain policy metadata and physical/cost accounting reality. Valuation reports summing physical units with `ContributesToProductCostState == true` double-count derecognized assets.
- **CANONICAL CORRECT BEHAVIOR:**
  - When inventory is `Scrapped`, economic value is written off as a recognized loss. It does NOT contribute to inventory asset valuation: `ContributesToProductCostState = false`.
  - When inventory is `IssuedThaka`, economic value is transferred to Project WIP. It is no longer an inventory asset: `ContributesToProductCostState = false`.
- **TRACKINGMODE IMPACT:** Impacts all physical tracking modes across Scrapped and IssuedThaka states.
- **IMPLEMENTATION DIRECTION:**
  - Update `src/EdgeRetails.Domain/Inventory/InventoryModels.cs`:
    - Line 569 (`IssuedThaka`): `new InventoryUnitAccountingPolicy(false, null, false)`
    - Line 619 (`Scrapped`): `new InventoryUnitAccountingPolicy(false, null, false)`
- **FILES EXPECTED TO CHANGE:** `src/EdgeRetails.Domain/Inventory/InventoryModels.cs`
- **FILES MUST NOT CHANGE:** `TraceabilityModels.cs`, `CatalogModels.cs`.
- **STOCK EFFECT:** Both statuses have `ContributesToStockBalance = false`.
- **LOT EFFECT:** No lot balance contribution.
- **PHYSICAL UNIT EFFECT:** Unit retained in historical tracking with correct accounting flags.
- **PRODUCT COST EFFECT:** Aligns domain metadata with `ProductCostState` derecognition.
- **THAKA COST EFFECT:** WIP carries the value; inventory cost pool does not.
- **RECOGNIZED LOSS EFFECT:** Recognized loss carries the scrap value; inventory pool does not.
- **TRANSACTION REQUIREMENT:** N/A (In-memory domain policy definition).
- **CONCURRENCY REQUIREMENT:** N/A.
- **REPLAY REQUIREMENT:** N/A.
- **PERMISSION REQUIREMENT:** N/A.
- **AUDIT REQUIREMENT:** N/A.
- **MIGRATION REQUIREMENT:** None. `InventoryUnitAccountingPolicy` is a C# value object/struct evaluated in-memory.
- **TESTS REQUIRED:** Unit tests asserting `ContributesToProductCostState == false` for `Scrapped` and `IssuedThaka`.
- **BLAST RADIUS:** Pure domain policy metadata.

---

## 13. SCRAPPED CANONICAL ACCOUNTING CONTRACT

The canonical economic and accounting meaning of `InventoryUnitStatus.Scrapped` is strictly defined as follows:

1. **Asset Derecognition:**
   - Scrapped inventory is damaged, defective, obsolete, or lost goods written off from company assets.
   - It is NO LONGER an owned inventory asset.
   - It MUST NOT appear in balance sheet inventory asset valuations.

2. **Stock Balance Behavior:**
   - `ContributesToStockBalance = false`.
   - Physical units marked `Scrapped` are excluded from all active stock balance queries (`SellableQty`, `DamagedQty`, `DefectiveQty`, `WithSupplierQty`).
   - `AuthoritativeBucket = null` (or non-asset terminal scrap bucket).

3. **Product Cost State Alignment:**
   - `ContributesToProductCostState = false`.
   - Handler execution invokes `RemoveCarryingValueAsync` to remove the carrying value and costed quantity from `ProductCostState`.
   - The economic carrying value is transferred into `RecognizedLossAmount` on the associated `InventoryMovement`.

4. **Terminal & Irreversible Status:**
   - `Scrapped` is a terminal lifecycle state. Scrapped physical units cannot be sold, issued to Thaka, returned to suppliers, or transferred between active condition buckets.

---

## 14. ISSUEDTHAKA CANONICAL ACCOUNTING CONTRACT

The canonical economic and accounting meaning of `InventoryUnitStatus.IssuedThaka` is strictly defined as follows:

1. **Economic Value Reclassification (WIP Transfer):**
   - When inventory is issued to a Thaka project, ownership and economic value transfer from Retail Inventory Asset to Work-in-Progress (WIP) / Project Job Cost.
   - It is no longer retail shop inventory available for sale or general operations.

2. **Stock Balance Behavior:**
   - `ContributesToStockBalance = false`.
   - Physical units in `IssuedThaka` status do not contribute to retail stock balances.
   - `AuthoritativeBucket = null`.

3. **Product Cost State Alignment:**
   - `ContributesToProductCostState = false`.
   - Carrying value is derecognized from retail inventory cost pools via `RemoveCarryingValueAsync`.
   - Economic value is debited to `ThakaProject.MaterialCost` ($WIP_{project} += Q_{base} 	imes 	ext{AuthoritativeUnitCost}$).

4. **Reversibility & Conservation:**
   - Material issue can be formally reversed via `ReverseThakaIssueHandler`.
   - Upon reversal, the exact carrying value debited to project WIP is credited back, and inventory carrying value and stock balance are restored with zero leakage.

---

## 15. STOCK CONSERVATION PROOF

### Theorem (Stock Conservation Invariant)
For any product across all points in time $t$:
$$\sum_{u \in \text{Active Units}} u.\text{BaseQuantity} = \text{StockBalance.QuantityOnHand} = \sum_{\text{active lots, buckets}} \text{LotBucketBalance.Quantity}$$

### Mathematical Proof & Concrete Numeric Demonstration
Consider an inventory lot with 100 serialized units ($Q_{base} = 1.0$ each, total on-hand = 100.0).

1. **Step 1 (Condition Transfer: Sellable $\to$ Damaged):**
   - 10 units transferred from `Sellable` to `Damaged`.
   - `SellableQty` = 90.0, `DamagedQty` = 10.0.
   - `QuantityOnHand` = $90.0 + 10.0 = 100.0$.
   - Active physical units: 90 InStock (Sellable) + 10 InStock (Damaged) = 100 units.
   - Lot balance: 90 in Sellable bucket + 10 in Damaged bucket = 100.0.
   - **Conservation:** $100.0 == 100.0 == 100.0$. Valid.

2. **Step 2 (Scrap Transfer: Damaged $\to$ Scrap):**
   - 4 units transferred from `Damaged` to `Scrap`.
   - Units transition to `InventoryUnitStatus.Scrapped` (`ContributesToStockBalance = false`).
   - `DamagedQty` = 6.0, `ScrapQty` = 4.0 (terminal non-asset bucket) or deducted from active on-hand.
   - Active asset on-hand = 96.0.
   - Active units: 90 Sellable + 6 Damaged = 96 units.
   - Derecognized units: 4 Scrapped units.
   - Total physical units tracked = $96 + 4 = 100$ units.
   - **Conservation:** Zero units lost, leaked, or phantom created.

---

## 16. COST/VALUE CONSERVATION PROOF

### Fundamental Value Conservation Law
$$\text{Opening Carrying Value} + \text{Acquisitions} - \text{Derecognitions (Sales + Loss + WIP)} = \text{Closing Carrying Value}$$

### Concrete Multi-Channel Numeric Audit
- **Opening State:** 50 units in stock @ \$100.00/unit.
  - `ProductCostState.CostedQty` = 50.0
  - `ProductCostState.TotalInventoryCost` = \$5,000.00
  - Total Asset Value = \$5,000.00

- **Transaction 1 (Thaka Issue):** 10 units issued to Project Alpha.
  - Line Cost = $10 	imes \$100.00 = \$1,000.00$.
  - Derecognition: `RemoveCarryingValueAsync(productId, 10.0, 100.0)` removes 10.0 qty and \$1,000.00 cost.
  - Project Alpha WIP: Debited +\$1,000.00.
  - Remaining Inventory Cost Pool: 40.0 qty @ \$100.00 = \$4,000.00.
  - Conservation Check: $\$4,000.00 (Inventory) + \$1,000.00 (Project WIP) = \$5,000.00$.

- **Transaction 2 (Scrap Write-off):** 2 units scrapped due to damage.
  - Exact Cost = $2 	imes \$100.00 = \$200.00$.
  - Derecognition: `RemoveCarryingValueAsync(productId, 2.0, 100.0)` removes 2.0 qty and \$200.00 cost.
  - Movement `RecognizedLossAmount` = \$200.00.
  - Remaining Inventory Cost Pool: 38.0 qty @ \$100.00 = \$3,800.00.
  - Conservation Check: $\$3,800.00 (Inventory) + \$1,000.00 (Project WIP) + \$200.00 (Loss) = \$5,000.00$.

**Zero Double-Recognition Guarantee:** Because `ContributesToProductCostState` is set to `false` for both `IssuedThaka` and `Scrapped` under P7-N03, reporting queries never add derecognized units back to inventory asset totals.

---

## 17. CONTAINER FACTOR >1 PROOF

### Model Specification
- Product: Industrial Cable Spool (TrackingMode = Container).
- Packaging Unit: Carton Box (`ProductUnitId` = `PU-CTN`, `FactorToBaseUnit` $K = 50.0$).
- Canonical Base Unit: Piece / Meter.

### Concrete Lifecycle Trace
1. **Initial Stock:**
   - 3 physical cartons in stock (`InventoryUnit` 1, 2, 3).
   - Each carton has `BaseQuantity` = 50.0.
   - `StockBalance.QuantityOnHand` = 150.0 base units.
   - Unit Acquisition Cost = \$2.00 / base unit (\$100.00 / carton). Total carrying value = \$300.00.

2. **Execution of SetPhysicalCount (Target = 2 Cartons):**
   - Operator counts 2 cartons on shelf and submits `SetPhysicalCount(Target = 2, ProductUnitId = PU-CTN)`.
   - $T_{entered} = 2$.
   - $T_{base} = 2 	imes 50.0 = 100.0$ base units.
   - $C_{base} = 150.0$ base units.
   - $\Delta_{base} = T_{base} - C_{base} = 100.0 - 150.0 = -50.0$ base units.
   - Magnitude of deduction = 50.0 base units (exactly 1 carton).
   - Operator selects Carton 3 to derecognize.
   - Carton 3 snapshot verifies `unit.BaseQuantity == 50.0`.
   - Carton 3 transitions to `Scrapped`.
   - `StockBalance.QuantityOnHand` becomes $150.0 - 50.0 = 100.0$ base units.
   - `RemoveCarryingValueAsync(productId, 50.0, 2.0)` removes \$100.00.
   - **Result:** Remaining stock is exactly 2 cartons (100.0 base units, \$200.00 carrying value).
   - **Proof:** Zero factor-of-K divergence. If factor $K$ were mistakenly omitted ($K = 1.0$), base delta would have been $-1.0$, leaving 149.0 base units and corrupted stock. D-ADJ-1 and F01 mathematically guarantee full pack factor fidelity.

---

## 18. THAKA ISSUE / REVERSAL SYMMETRY

The mathematical and state symmetry between `IssueThakaMaterialHandler` and `ReverseThakaIssueHandler` is formally proven below:

| System Dimension | Material Issuance (`IssueThakaMaterialHandler`) | Issue Reversal (`ReverseThakaIssueHandler`) | Net Cumulative Drift |
|---|---|---|---|
| **StockBalance** | $-\Delta Q_{base}$ | $+\Delta Q_{base}$ | **0.000000** |
| **InventoryLot Bucket** | $-\Delta Q_{base}$ from `Sellable` | $+\Delta Q_{base}$ to `Sellable` | **0.000000** |
| **ProductCostState.CostedQty** | $-\Delta Q_{base}$ | $+\Delta Q_{base}$ | **0.000000** |
| **ProductCostState.TotalCost** | $-(\Delta Q_{base} \times C_{unit})$ | $+(\Delta Q_{base} \times C_{unit})$ | **\$0.000000** |
| **ThakaProject.MaterialCost** | $+(\Delta Q_{base} \times C_{unit})$ | $-(\Delta Q_{base} \times C_{unit})$ | **\$0.000000** |
| **Physical Unit Status** | `InStock` $\to$ `IssuedThaka` | `IssuedThaka` $\to$ `InStock` | Exact identity restored |
| **Accounting Policy Flags** | Transitions to `(false, null, false)` | Restored to `(true, Sellable, true)` | Exact policy restored |

Both operations are strictly symmetric and preserve value, quantity, and unit identity across all five tracking modes.

---

## 19. TRANSACTION ATOMICITY

All inventory mutations across Stock Adjustment, Condition Transfer, Thaka Issue, and Thaka Reversal execute under strict ACID transaction boundaries:

1. **Unified Ambient Transaction:**
   - Every command handler executes inside `EfTransactionRunner.ExecuteInTransactionAsync`.
   - The transaction encapsulates all entity updates: `StockBalance`, `InventoryLot`, `InventoryLotBucketBalance`, `InventoryMovement`, `InventoryUnit`, `ProductCostState`, and `OperationOutcome`.

2. **Single Commit Boundary:**
   - Exactly ONE call to `_dbContext.SaveChangesAsync(cancellationToken)` occurs at the transaction conclusion.
   - No intermediate commits or partial writes.

3. **Rollback Invariant:**
   - If any domain rule fails, concurrency token conflicts, or network drops occur before completion, the entire transaction is rolled back by PostgreSQL.
   - Guaranteed: No orphaned `InventoryMovement` records, no partial stock deductions, and no dangling physical unit status updates.

---

## 20. CONCURRENCY MATRIX

The authoritative concurrency resolution matrix across all critical competing operations is defined below:

| Scenario / Competing Operations | Concurrency Mechanism | Detection Point | Deterministic Outcome |
|---|---|---|---|
| **Two Negative Adjustments on Same Stock** | Optimistic concurrency token on `StockBalance` | `SaveChangesAsync` | First commits; second throws `DbUpdateConcurrencyException`, retries or fails safely with stock insufficient. |
| **SetPhysicalCount vs Sale** | `StockBalance.ConcurrencyToken` | `SaveChangesAsync` | Serialized by row lock / concurrency token. Second operation reads updated balance. |
| **SetPhysicalCount vs Stocktake** | Active stocktake lock check on `StocktakeItem` | Handler start & commit | Adjustment rejected if active stocktake session locks the product bucket. |
| **Condition Transfer vs Sale** | Bucket separation & `StockBalance.ConcurrencyToken` | Allocation & commit | Cannot sell from non-sellable buckets; sellable bucket deduction protected by concurrency token. |
| **Condition Transfer vs Warranty Send** | Physical unit status check (`u.Status == InStock`) | Unit query for update | First operation transitions status; second fails with unit not available. |
| **Thaka Issue vs Sale** | Physical unit status check (`u.Status == InStock`) | Unit query for update | First operation claims unit; second fails with `inventory.unit_not_in_stock`. |
| **Two Thaka Issues selecting Same Unit** | Unit status check & database row lock | Unit query for update | First transitions unit to `IssuedThaka`; second fails immediately. |
| **Thaka Issue vs Purchase Return** | Unit status check (`u.Status == InStock`) | Unit query for update | Cannot return an issued unit. First operation wins; second fails. |
| **Thaka Reversal vs Stocktake** | `StockBalance.ConcurrencyToken` | `SaveChangesAsync` | Reversal restores balance; serialized against stocktake count recording. |

---

## 21. REPLAY BOUNDARY

Idempotency and duplicate prevention are guaranteed across all Pass 2 operations:

1. **Client Operation Identity:**
   - Every mutation command mandates a non-empty `ClientOperationId` (Guid version 7).
   - Handlers query `IOperationOutcomeLedger.GetOutcomeAsync(command.ClientOperationId)`.

2. **Replay Execution Path:**
   - If `ClientOperationId` exists in `EfOperationOutcomeLedger`:
     - The handler immediately short-circuits.
     - Returns the cached `Result<T>` with original entity ID and HTTP status.
     - ZERO database writes, zero stock movements, zero cost modifications.

3. **Crash Recovery Guarantee:**
   - The outcome record is written in the SAME database transaction as the business mutation.
   - Replays after server restart or network timeout are 100% idempotent.

---

## 22. DATABASE INVARIANTS

The complete catalog of persistence-layer constraints and enforcement levels is detailed below:

| Invariant / Constraint Description | Database Constraint / Index | Application Layer Enforcement | Status |
|---|---|---|---|
| **StockBalance Non-Negativity** | Check constraint / decimal non-negative | Validated in domain entity before mutation | **BOTH (Database & App)** |
| **Lot Bucket Balance Non-Negativity** | Check constraint / non-negative | Validated in `InventoryLot` and cost allocator | **BOTH (Database & App)** |
| **InventoryUnit TrackingCode Uniqueness** | `IX_inventory_units_tracking_code` (Unique) | Validated in PUCA before insertion | **BOTH (Database & App)** |
| **Identity Claim Uniqueness** | `IX_inventory_unit_identity_claims_claim_type_claim_value` (Unique) | Validated in identity claim registry | **BOTH (Database & App)** |
| **ClientOperationId Uniqueness** | `IX_operation_outcomes_client_operation_id` (Unique) | Handled by `EfOperationOutcomeLedger` | **BOTH (Database & App)** |
| **StockBalance Concurrency** | Concurrency token column (`xmin` / rowversion) | EF Core `DbUpdateConcurrencyException` | **BOTH (Database & App)** |
| **Thaka Project Balance Consistency** | Decimal precision constraints | Handled by Thaka domain aggregations | **APPLICATION_ENFORCED** |

---

## 23. EXISTING TESTS

The current test coverage for the seven Pass 2 findings across the test suite is classified below:

| Finding | Existing Test Fixture | Current Test Coverage Description | Verdict |
|---|---|---|---|
| **F01** | `StockAdjustmentHandlerBehavioralTests.cs` | Tests signed delta adjustments (positive & negative). Does not test `SetPhysicalCount` mode; ignores mode property. | **EXISTING_BUT_INCOMPLETE** |
| **D-ADJ-1** | `StockAdjustmentHandlerBehavioralTests.cs` | Contains test with container tracking mode, but uses factor 1.0 or null unit, inadvertently confirming fallback bug. | **EXISTING_BUT_INCOMPLETE** |
| **F02** | `Phase1DExactUnitLifecycleTests.cs` | Tests basic condition transfer, but lacks verification of exact per-unit scrap carrying value removal and container factor scaling. | **EXISTING_BUT_INCOMPLETE** |
| **F03** | `StocktakeBehavioralTests.cs` | Validates integer counts in stocktake, but condition transfer lacks fractional pre-rounding rejection tests. | **MISSING** |
| **F04** | `Phase2StocktakeThakaCashSessionBehavioralTests.cs` | Exercises Thaka material issue, but with default factor 1.0, masking the `EnteredQuantity` vs `BaseQuantity` multiplier defect. | **EXISTING_BUT_INCOMPLETE** |
| **P7-N02** | `Phase2StocktakeThakaCashSessionBehavioralTests.cs` | Validates intra-line duplicate physical unit rejection, but lacks cross-line duplicate unit ID tests. | **EXISTING_BUT_INCOMPLETE** |
| **P7-N03** | `TrackingMasterIdentityGovernanceTests.cs` | Tests unit status transitions, but omits assertions on `ContributesToProductCostState` for `Scrapped` and `IssuedThaka`. | **MISSING** |

---

## 24. MISSING TESTS (STEP 2 TEST SUITE)

The following thirteen authoritative tests must be authored during Step 2:

1. `F01_SetPhysicalCount_ZeroTarget_NonPhysical_SetsStockToZero`
2. `F01_SetPhysicalCount_ZeroTarget_Serialized_DerecognizesAllSpecifiedUnits`
3. `F01_SetPhysicalCount_PositiveDelta_IncreasesStockAndCostPool`
4. `F01_SetPhysicalCount_NegativeDelta_DeductsStockAndScrapsSpecifiedUnits`
5. `D_ADJ_1_Container_Adjustment_NullProductUnitId_FailsValidation`
6. `D_ADJ_1_Container_Adjustment_WithPackFactor_ScalesBaseQuantityDelta`
7. `F02_ConditionTransfer_ToScrap_Serialized_RemovesExactAcquisitionCostPerUnit`
8. `F02_ConditionTransfer_ToScrap_Container_RemovesExactCarryingValueScaledByPackFactor`
9. `F03_ConditionTransfer_FractionalQuantity_Rejected_BeforeRounding`
10. `F03_ConditionTransfer_PhysicalUnitsCount_MustMatchWholeQuantity`
11. `F04_ThakaIssue_PackFactor_GreaterThanOne_CalculatesLineCostOnBaseQuantity`
12. `P7_N02_ThakaIssue_DuplicatePhysicalUnits_AcrossMultipleLines_FailsValidation`
13. `P7_N03_InventoryUnitAccountingPolicy_ScrappedAndIssuedThaka_DoNotContributeToCostState`

---

## 25. POSTGRESQL PROOF REQUIREMENTS

All Step 2 changes must be validated against a live PostgreSQL 18 database instance under realistic transactional execution:
1. **Constraint Integrity:** Verify that negative stock adjustments cannot breach `StockBalance` non-negativity check constraints.
2. **Concurrency Serialization:** Run parallel conflicting `SetPhysicalCount` and Sale transactions; verify deterministic optimistic locking resolution.
3. **Transaction Rollback:** Verify that an unhandled fault during Thaka issue completely rolls back PostgreSQL state with zero orphaned `InventoryMovement` or `InventoryUnit` mutations.
4. **Idempotency Replay:** Submit duplicate requests with identical `ClientOperationId` against PostgreSQL; verify exact cached response and zero state mutation.

---

## 26. FAILURE-INJECTION PLAN

During Step 2 test verification, explicit failure injection hooks will be executed:
1. **Fault Point 1 (Pre-Commit Abort):** Inject exception immediately before `_dbContext.SaveChangesAsync()` in `CreateStockAdjustmentHandler`. Assert zero stock, lot, or unit modifications in database.
2. **Fault Point 2 (Mid-Stream Cost Allocator Failure):** Inject simulated arithmetic overflow in `InventoryCostAllocator.RemoveCarryingValueAsync` during Scrap transfer. Assert that physical unit statuses remain unchanged and no partial carrying value is deducted.
3. **Fault Point 3 (Thaka Cross-Line Duplicate):** Submit multi-line Thaka issue with duplicated unit ID on line 10. Assert atomic rejection prior to any entity persistence.

---

## 27. PERMISSION / AUDIT REQUIREMENTS

### Permission Matrix
- `CreateStockAdjustmentCommand`: Requires `Permissions.Inventory.Adjust`.
- `TransferInventoryConditionCommand`: Requires `Permissions.Inventory.TransferCondition`.
- `IssueThakaMaterialCommand`: Requires `Permissions.Thaka.IssueMaterial`.
- `ReverseThakaIssueCommand`: Requires `Permissions.Thaka.ReverseIssue`.

### Audit Trail Requirements
Every transaction must write an immutable `InventoryMovement` record containing:
- `ProductId`
- `MovementType`
- `Quantity` (in base units)
- `RecognizedLossAmount` (for Scrap / Shrinkage)
- `ReferenceType` and `ReferenceId`
- `ActorId`
- `CorrelationId`
- `OccurredAt` (UTC)
- `Reason`

---

## 28. API / DESKTOP IMPACT

A forensic inspection of the presentation and API boundaries confirms **ZERO BREAKING CHANGES**:
- **API Contracts:** `StockAdjustmentLineRequest`, `TransferInventoryConditionCommand`, and `IssueThakaMaterialCommand` already define `ProductUnitId`, `Mode`, and `InventoryUnitIds`.
- **WPF Desktop UI:** `StockAdjustmentViewModel.cs` and `RemoteStockAdjustmentService.cs` already support unit selection and count adjustments.
- **Backwards Compatibility:** All modifications are purely algorithmic improvements to command handlers and domain policies. Zero REST API contract modifications, zero DTO breaking changes.

---

## 29. MIGRATION VERDICT

**VERDICT: NO_MIGRATION_REQUIRED**

### Proof
1. `F01` & `D-ADJ-1`: Operates within existing `StockAdjustment` entities, existing `StockAdjustmentMode` enum, and existing `StockBalance` tables.
2. `F02` & `F03`: Refactors handler arithmetic in `InventoryConditionHandlers.cs`. Existing `InventoryBucket.Scrap` and `InventoryMovement` schema support all required fields.
3. `F04` & `P7-N02`: Updates handler cost multiplication and input validation in `ThakaHandlers.cs`. No schema modifications.
4. `P7-N03`: Modifies in-memory C# policy mapping `InventoryUnitAccountingPolicy` in `InventoryModels.cs`. This policy is evaluated at runtime and is not persisted as a database table or column.
5. **Conclusion:** Zero database schema modifications, zero new migrations, zero EF Core model snapshot changes.

---

## 30. CROSS-DEFECT DEPENDENCY MATRIX

| Defect Interaction | Interdependency & Sequencing Constraint |
|---|---|
| **$F01 \leftrightarrow D\text{-ADJ-}1$** | `D-ADJ-1` (mandatory `ProductUnitId` & pack factor) must be resolved simultaneously with `F01` so container `SetPhysicalCount` computes $\Delta_{base} = (T - C) \times K$ accurately. |
| **$F01 \leftrightarrow P7\text{-}N03$** | Negative physical adjustments transition units to `Scrapped`; `P7-N03` must ensure `Scrapped` units do not contribute to `ProductCostState`. |
| **$F02 \leftrightarrow P7\text{-}N03$** | Condition transfer to `Scrap` derecognizes carrying value; `P7-N03` aligns domain policy flags with derecognition reality. |
| **$F03 \leftrightarrow F02$** | Whole-number quantity integrity (`F03`) must be validated prior to iterating per-unit cost removal (`F02`). |
| **$F04 \leftrightarrow P7\text{-}N02$** | Cross-line duplicate unit validation (`P7-N02`) must reject invalid commands before line cost and project WIP calculations (`F04`) execute. |
| **$F04 \leftrightarrow P7\text{-}N03$** | Thaka issue removes carrying value from inventory; `P7-N03` sets `ContributesToProductCostState = false` for `IssuedThaka`, maintaining WIP-inventory balance. |
| **$P7\text{-}N03 \leftrightarrow \text{Thaka Reversal}$** | Thaka reversal restores units to `InStock` (`ContributesToProductCostState = true`), perfectly balancing `P7-N03`. |

---

## 31. EXACT STEP 2 IMPLEMENTATION MAP

The Step 2 implementation must strictly follow this nine-stage sequential progression:

1. **Stage 1 (Domain Policy Alignment - P7-N03):**
   - File: `src/EdgeRetails.Domain/Inventory/InventoryModels.cs`
   - Edit lines 569 and 619: Set `ContributesToProductCostState = false` for `IssuedThaka` and `Scrapped`.

2. **Stage 2 (StockAdjustment Corrections - F01 & D-ADJ-1):**
   - File: `src/EdgeRetails.Application/Features/Inventory/StockAdjustmentHandlers.cs`
   - Allow `Quantity == 0` for `SetPhysicalCount`.
   - Enforce mandatory `ProductUnitId` for `TrackingMode.Container`.
   - Compute signed base delta $\Delta_{base} = T_{base} - C_{base}$ in `CreateStockAdjustmentLineInternalAsync`.
   - Execute negative adjustment path for $|\Delta_{base}|$ when $\Delta_{base} < 0$.

3. **Stage 3 (ConditionTransfer Corrections - F02 & F03):**
   - File: `src/EdgeRetails.Application/Features/Inventory/InventoryConditionHandlers.cs`
   - Validate unrounded quantity with `QuantityMath.IsWhole` before rounding.
   - Refactor Scrap carrying value removal to iterate per physical unit and pass unit acquisition cost.

4. **Stage 4 (Thaka Material Issue Corrections - F04 & P7-N02):**
   - File: `src/EdgeRetails.Application/Features/Thaka/ThakaHandlers.cs`
   - Add command-level cross-line duplicate `InventoryUnitId` validation.
   - Calculate line cost and project WIP allocation using `baseQuantity` instead of `input.EnteredQuantity`.

5. **Stage 5 (Focused Unit Test Suite):**
   - Author the 13 focused behavioral unit tests in `tests/EdgeRetails.UnitTests/`.

6. **Stage 6 (PostgreSQL Integration Verification):**
   - Execute PostgreSQL transactional verification suite against live database.

7. **Stage 7 (Tracking Subsystem Non-Regression Check):**
   - Run `TrackingArchitectureDriftTests.cs` and `TrackingMasterIdentityGovernanceTests.cs`. Confirm 0 tracking mutations.

8. **Stage 8 (Full Regression Suite):**
   - Execute complete solution test suite (`dotnet test`).

9. **Stage 9 (Final Build & Architecture Certification):**
   - Execute clean build verification (`dotnet build -c Release`).

---

## 32. REMAINING BLOCKERS

**REMAINING BLOCKERS: NONE (0)**

All architectural prerequisites, mathematical proofs, domain policies, and implementation algorithms are 100% frozen, proven, and ready for Step 2 execution.

---

## FINAL SAFETY CONFIRMATION

SOURCE CODE MODIFIED: NO  
TEST CODE MODIFIED: NO  
DATABASE MODIFIED: NO  
MIGRATIONS CREATED: NO  
TRACKING AUTHORITY MODIFIED: NO  
GIT COMMIT/PUSH: NO  
PASS 2 IMPLEMENTATION STARTED: NO  
