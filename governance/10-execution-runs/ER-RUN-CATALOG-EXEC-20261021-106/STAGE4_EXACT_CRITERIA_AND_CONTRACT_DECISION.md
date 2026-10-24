# Stage 4: Exact Criteria and Contract Decision

- **Run ID**: `ER-RUN-CATALOG-EXEC-20261021-106`

## Architectural Decisions
1. **Product.ClassRef Integration**:
   - `Product.ClassRef` added as an optional string (max length 20), foreign-keyed to `catalog_v3.product_classes(class_ref)` with `ON DELETE RESTRICT`.
   - Legacy records without `class_ref` remain fully valid (nullable column).
2. **Unit Model Expansion**:
   - Added `Dimension` (`PhysicalDimension` enum stored as string).
   - Added `FactorToReference` (`decimal(18,9)`).
   - Added `ConversionKind` (`string(20)`: `FIXED`, `FIXED_TO_M`, `PACKAGING`, etc.).
3. **Shared Unit Validator**:
   - Centralized in `ProductUnitValidator.ValidateUnitInputs` used across command handlers and API endpoints.
4. **Decoupled Filter State**:
   - Separated `ActiveTaxonomyFilterNode` from free-text search string in Desktop viewmodel.
