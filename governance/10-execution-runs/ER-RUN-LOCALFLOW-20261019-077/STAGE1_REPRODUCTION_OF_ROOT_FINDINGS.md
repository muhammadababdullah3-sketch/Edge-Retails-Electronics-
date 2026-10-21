# STAGE 1: Reproduction of Root Findings and LF02 Causal Remediation

**Run ID:** `ER-RUN-LOCALFLOW-20261019-077`
**Authority:** `OWNER-UPDATE-20261018-019` / `OWNER-UPDATE-20261019-001` / `OWNER-UPDATE-20261019-007`
**Predecessor Review:** `governance/10-execution-runs/ER-RUN-LOCALFLOW-20261019-076/STAGE1_REPRODUCTION_OF_ROOT_FINDINGS.md`
**Timestamp UTC:** `2026-10-19T12:00:00Z`

---

## 1. Executive Summary & LF02 Remediation

In Run 076, canonical finding **LF02** was marked `CLOSED_IN_CANDIDATE` but lacked dedicated, named unit test proof demonstrating distinct serialized units of the same SKU being sold on separate cart lines and with different price overrides.

In Run 077, the native POS Integrator and Test Coordinator executed full causal remediation and direct behavioral verification:

### LF02 Root Causal Defect
- **Location**: `src/EdgeRetails.Application/Features/Sales/CompleteSaleHandler.cs:233`
- **Defect**: The server guard checked:
  ```csharp
  if (command.Lines.GroupBy(x => new { x.ProductId, x.ProductUnitId }).Any(g => g.Count() > 1))
      return Result<CompleteSaleResult>.Failure("sales.duplicate_line", ...);
  ```
  This unconditionally rejected any sale where multiple lines shared `{ ProductId, ProductUnitId }`, even when each line carried distinct physical serialized unit IDs or different legitimate price overrides.
- **Desktop Adapter Defect**: In `src/EdgeRetails.Desktop/Services/BackendTransactionService.cs:110`, grouping cart items only by product and unit failed to account for price overrides, causing lines with different prices to merge or throw pricing errors.

### Causal Source Repair
1. **Server Guard Replacement**: Replaced unconditional duplicate line check with:
   - Physical unit uniqueness validation across all lines (`allInventoryUnits.Count == allInventoryUnits.Distinct().Count()`), rejecting duplicates with canonical `sales.serial_selected_twice`.
   - Commodity-only line deduplication (`g.Any(x => x.InventoryUnitIds.Count == 0)`), rejecting duplicates with `sales.duplicate_line`. Distinct serialized units on separate lines are permitted.
   - Canonical sorting in `MatchesExistingSalePayloadAsync` for robust replay matching.
2. **Desktop Transaction Adapter**: Group cart items by `(ProductId, ProductUnitId, UnitPrice, OverridePrice, OverrideReason)`. Items with identical pricing merge; items with different price overrides remain separate lines.

### Named Behavior Test Proof
Added 4 dedicated named unit tests in `tests/EdgeRetails.UnitTests/Phase1DPosResolutionAndExactUnitSaleTests.cs`:
1. `LF02_DistinctSerializedUnits_SameSku_MultipleLines_Succeeds` [PASSED]
2. `LF02_DistinctSerializedUnits_SameSku_WithDifferentPriceOverrides_Succeeds` [PASSED]
3. `LF02_SameSerializedUnit_RepeatedAcrossLines_RejectedWithSerialSelectedTwice` [PASSED]
4. `LF02_CommodityProduct_DuplicateLines_RejectedWithDuplicateLine` [PASSED]
