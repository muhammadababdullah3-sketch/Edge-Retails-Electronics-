# Pass5 exact edit whitelist addendum —D13 test proof and shared sold-return authority

Recorded2026-10-06 before new paths edited. Latest master remaining-d13-master-authority-20261006.md §§5–13 explicitly requires D13 canonical sold provenance and SaleReturn overlap proof. Existing frozen whitelist remains immutable.

Additional exact test path: tests/EdgeRetails.IntegrationTests/Phase7Pass5ShopWarrantySourcePostgresTests.cs (new), NEW_COVERAGE D13-2 source/case lot wrong-source/supplier/capacity/partial-resolution proofs only, isolatedPostgreSQL18/Npgsql. No existing test assertions changed.

Additional exact production path: src/EdgeRetails.Application/Features/Sales/SaleReturnHandler.cs, D13 ONLY shared sold capacity/reservation allocation and source-specific overlap prevention after realPG RED. Preserve existing commercial/identity/cost/refund/lock/replay/rollback/receipt behavior, H1/H2 tolerances and all other passes. No general return redesign.

Existing whitelisted test: Phase7Pass5WarrantyReportingPostgresTests.cs, exclusive SpecialistA D13 tests only. New ShopWarrantySource test exclusive SpecialistD. SpecialistB representation read-only. Root owns execution; no edits while build/test/EF/database command running, source changes only after terminal RED and exclusive handoff.

No migration/model/source schema expansion, fake neutral consumption, child receipt lot, operational DB/config/deployment or later phase permission. D13-2 NO sufficient persisted representation means STOP BEFORE CODE/MIGRATION and report PASS5_BLOCKED_MIGRATION_DECISION with exact missing facts/minimum delta/legacy/upgrade/rollback/test implications.
