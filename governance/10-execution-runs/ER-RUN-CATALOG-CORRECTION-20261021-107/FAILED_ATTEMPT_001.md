# Preserved failed attempt 001 — generic EF migration scaffold

- Date: 2026-10-21. Exact native start/end timestamp was not captured; do not treat the approximate timestamps from an earlier scratch note as authoritative.
- Command: `dotnet ef migrations add Phase11CatalogV3RelationalIntegrity --project src/EdgeRetails.Infrastructure/EdgeRetails.Infrastructure.csproj --startup-project src/EdgeRetails.Infrastructure/EdgeRetails.Infrastructure.csproj --context EdgeRetailsDbContext --output-dir Persistence/Migrations --configuration Debug`.
- Observed output: build succeeded; EF scaffold produced an unrelated `thaka.credit_refunds` table/index and `catalog.products.class_ref` index, but no taxonomy tables or required cross-schema ClassRef FK.
- Disposition: the generated migration was not used as-is. The initial output files were deleted and the snapshot was restored, then a later CAT migration was authored manually. A subsequent scaffold attempt later recreated a CAT migration designer containing the unrelated `ThakaCreditRefund` target model (current source artifact at run 107). Preserve that as a subsequent attempt, not as the output from the first attempt.
- No PostgreSQL connection, migration apply, or DB operation occurred during this failed scaffold attempt.
