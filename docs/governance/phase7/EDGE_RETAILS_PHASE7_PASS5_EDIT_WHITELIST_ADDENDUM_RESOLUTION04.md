# Resolution04 narrow edit ownership

Recorded before schema edits. Frozen original whitelist and Resolution01–03 preserved. Root exclusively owns model/configuration/schema/migration generation in this first wave:

- `src/EdgeRetails.Domain/Warranty/WarrantyProvenanceModels.cs` new minimal allocations
- `src/EdgeRetails.Infrastructure/Persistence/Configurations/WarrantyProvenanceConfigurations.cs` new mappings
- `src/EdgeRetails.Infrastructure/Persistence/EdgeRetailsDbContext.cs` allocation DbSets only
- Resolution05 expansion recorded before model agent edits: same DbContext also enforces append-only mutation refusal for the four new facts only; existing audit/ledger/dealer guards preserved.
- `src/EdgeRetails.Infrastructure/Persistence/Migrations/*_Phase7Pass5WarrantySourceAuthority.cs` new forward migration only
- matching new migration Designer and current `EdgeRetailsDbContextModelSnapshot.cs`
- `tests/EdgeRetails.IntegrationTests/Phase7Pass5WarrantyMigrationPostgresTests.cs` new coverage
- `scripts/Invoke-Pass5WarrantyMigrationRehearsal.ps1` new owned migration/backup-restore evidence runner, if necessary

No historical migration edits. Initial specialists have bounded read-only D13 and shop compatibility/economics plans; no overlapping writes. Application/API/allocator/repository corrections begin only after terminal migration rehearsal and exact further ownership handoff. No edits while any build/test/EF/database task runs. All new test work NEW_COVERAGE; harness correction separately recorded; no assertions weakened/skips/concurrency changes.
