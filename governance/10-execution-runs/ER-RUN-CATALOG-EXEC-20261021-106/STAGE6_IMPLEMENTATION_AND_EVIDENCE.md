# Stage 6: Implementation and Evidence

- **Run ID**: `ER-RUN-CATALOG-EXEC-20261021-106`

## Implementation Details
1. `src/EdgeRetails.Domain/Catalog/CatalogModels.cs`:
   - Added `ClassRef` to `Product`.
   - Added `Dimension`, `FactorToReference`, and `ConversionKind` to `Unit`.
   - Defined `PhysicalDimension` enum.
2. `src/EdgeRetails.Application/Features/Catalog/ProductUnitValidator.cs`:
   - Enforced physical dimension matching, Gate D restrictions, fixed ratios, and positive packaging factors.
3. `src/EdgeRetails.Application/Features/Catalog/ProductUnitHandlers.cs` & `ProductManagementHandlers.cs`:
   - Wired `ProductUnitValidator` into unit configuration and product catalog save handlers.
4. `src/EdgeRetails.Application/Features/Setup/FirstSetupBootstrapHandler.cs`:
   - Implemented transactional preload of canonical units and operational reference categories from `TaxonomyV3Store`.
5. `src/EdgeRetails.Infrastructure/Persistence/Migrations/20261021120000_Phase11CatalogCanonicalIntegration.cs`:
   - Created official production EF Core migration adding `class_ref`, `dimension`, `factor_to_reference`, and `conversion_kind`.
6. `src/EdgeRetails.Infrastructure/Services/TaxonomyV3Importer.cs`:
   - Non-mutating dry run with WAL/LSN verification.
   - Live target diff computation.
   - Comprehensive SHA256 manifest hash computation covering all entities and policies.
7. `src/EdgeRetails.Infrastructure/Services/ProductManagementReadService.cs`:
   - Prioritized leaf `CLASS` scope over `DEPARTMENT` scope in Urdu alias resolution (`pankha` -> `B05-01`).
8. `src/EdgeRetails.Desktop/ViewModels/ProductManagementViewModel.cs`:
   - Decoupled hierarchy filtering from text search.

## Automated Verification Evidence
- `test-results/catalog_v3_unit_tests.trx`: 1,407 passed (0 failed).
- `test-results/catalog_v3_desktop_editor_tests.trx`: 8 passed (0 failed).
- `build.log`: 0 warnings, 0 errors across 12 projects.
