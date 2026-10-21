# Candidate Drift and Provenance Audit: Run 077 vs Run 076

- **Run ID**: `ER-RUN-LOCALFLOW-20261019-077`
- **Predecessor Baseline**: `ER-RUN-LOCALFLOW-20261019-076`
- **Base Commit**: `3cd3b4b8f903adef6f6d7cf3c922c2ce184c2fc2`
- **Candidate Files**: `330` (`161` tracked, `169` untracked)
- **Identical Files**: `327`
- **Modified Files**: `2`
- **New Files**: `1`

## Intentional Modifications Summary

1. `src/EdgeRetails.Application/Features/Sales/CompleteSaleHandler.cs`:
   - **LF02 Causal Source Fix**: Replaced unconditional duplicate line guard on `{ProductId, ProductUnitId}` with physical unit uniqueness validation across all lines (`allInventoryUnits.Count == allInventoryUnits.Distinct().Count()`), rejecting duplicates with canonical `sales.serial_selected_twice`. Restricted `sales.duplicate_line` rejection to commodity/untracked lines (`InventoryUnitIds.Count == 0`). Canonicalized sorting in `MatchesExistingSalePayloadAsync`.

2. `src/EdgeRetails.Desktop/Services/BackendTransactionService.cs`:
   - **LF02 Desktop Transaction Adapter Fix**: Group cart lines by `(ProductId, ProductUnitId, UnitPrice, OverridePrice, OverrideReason)`. Items with identical pricing merge; items with different price overrides remain separate lines with their respective unit IDs, override prices, and reasons.

3. `tests/EdgeRetails.UnitTests/Phase1DPosResolutionAndExactUnitSaleTests.cs`:
   - **LF02 Behavioral Tests**: Added 4 named tests (`LF02_DistinctSerializedUnits_SameSku_MultipleLines_Succeeds`, `LF02_DistinctSerializedUnits_SameSku_WithDifferentPriceOverrides_Succeeds`, `LF02_SameSerializedUnit_RepeatedAcrossLines_RejectedWithSerialSelectedTwice`, `LF02_CommodityProduct_DuplicateLines_RejectedWithDuplicateLine`), verifying that distinct serialized units pass, overrides pass, duplicated units reject with `sales.serial_selected_twice`, and duplicate commodity lines reject with `sales.duplicate_line`.

## Preserved Source Modules
All other components from Run 076 are 100% preserved bit-for-bit without change.
