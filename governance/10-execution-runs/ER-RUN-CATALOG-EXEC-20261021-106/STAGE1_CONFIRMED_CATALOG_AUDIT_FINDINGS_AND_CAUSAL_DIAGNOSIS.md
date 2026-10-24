# Stage 1: Confirmed Catalog Audit Findings and Causal Diagnosis

- **Run ID**: `ER-RUN-CATALOG-EXEC-20261021-106`
- **Authority**: `OWNER-UPDATE-20261018-019 / OWNER-UPDATE-20261019-007 / ER-RUN-CATALOG-PROMPT-20261021-104`
- **Baseline Audit**: `ER-RUN-CATALOG-AUDIT-20261021-103`

## Findings Overview & Root Causes
1. **CAT-AUDIT-01 (Canonical Registry Preload Gap)**:
   - *Symptom*: Operational units and categories were not populated on fresh setup; `TaxonomyV3Store` remained disconnected from runtime database tables.
   - *Cause*: `FirstSetupBootstrapHandler` omitted transactional preload of canonical units and categories.
2. **CAT-AUDIT-02 (Physical Dimension Compatibility & Gate D Omission)**:
   - *Symptom*: Users could configure cross-dimensional unit conversions (e.g. Length to Mass) or arbitrary ratios for fixed conversion units.
   - *Cause*: Handlers only checked `FactorToBaseUnit > 0` and integer factors for containers, lacking physical dimension checks and Gate D policy enforcement.
3. **CAT-AUDIT-03 (Missing Authoritative ClassRef Linkage)**:
   - *Symptom*: Products in `catalog.products` had `category_id` but lacked persistent link to `catalog_v3.product_classes(class_ref)`.
   - *Cause*: `Product` domain model and EF configurations lacked `ClassRef`.
4. **CAT-AUDIT-04 (Taxonomy Filter Widening)**:
   - *Symptom*: Selecting a leaf class widened the filter to match all products across the entire department.
   - *Cause*: `ProductManagementViewModel` merged leaf selection into free-text search category tokens.
5. **CAT-AUDIT-05 (Dry-Run Mutation Invariant Violation)**:
   - *Symptom*: `ExecuteDryRunAsync` executed `EnsureSchemaAsync` before measuring WAL/LSN, creating tables during preview.
   - *Cause*: Missing separation between read-only schema existence check and DDL execution.
6. **CAT-AUDIT-06 (Target-State Diff Calculation & Incomplete Hash)**:
   - *Symptom*: Importer hardcoded diffs and omitted semantic attributes from manifest SHA256 hash.
   - *Cause*: Lack of database row comparison queries and incomplete hashing formula in `ComputeStoreHash`.
7. **CAT-AUDIT-07 (Urdu Search Alias Misrouting)**:
   - *Symptom*: Urdu term `pankha` did not resolve to Ceiling fan (`B05-01`).
   - *Cause*: Aliases defaulted to broad department mappings without leaf class precedence.
8. **CAT-AUDIT-08 (Test-Setup Circular DDL Hack)**:
   - *Symptom*: Tests executed manual `ALTER TABLE` DDL, masking the absence of production EF migrations.
   - *Cause*: Missing EF Core migration for `class_ref` and unit dimension attributes.
