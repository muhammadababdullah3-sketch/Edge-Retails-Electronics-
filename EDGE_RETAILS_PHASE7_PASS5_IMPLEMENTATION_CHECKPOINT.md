# Resolution05 — prospective sold-source writer continuation

## Completed and proved

Original deferred COMMIT refusal captured before application edits: PostgreSQL SQLSTATE23514, PASS5_PROVENANCE_REQUIRED ambiguous/missing bulk sold-source allocation; original writer omitted0 because it produced no facts. Complete fresh-scope rollback proved. Original capture retained.

SaleReturn and CommercialExchange now select remaining canonical original sale-consumption capacity, restore actual lots and append immutable return-source facts. Return valuation remains original SaleItem residual; central product-wide MWA unchanged. Shared repository accounts for prior immutable returns and qualifying customer claims, rejects legacy ambiguous reconciliation, and serializes original source rows. CreateWarrantyClaim persists source facts for Quantity/Length, uses original supplier capacity, and aligns Sale/product/item/unit lock ordering. No old migration edits, new migration generation, production mutation, deployment, Git history operation or later phase work.

Bounded agents: Exchange writer completed and handed back its exclusive file. Warranty agent hit usage limit before completing its assigned changes; root inspected its file and completed the bounded claim integration. No concurrent overlapping edit or build/database task.

## Actual terminal evidence

Provider = PostgreSQL 18 / Npgsql (server_version_num180006).

| Gate | Exit | Passed | Failed | Skipped | Completion |
|---|---:|---:|---:|---:|---|
| Original refusal and rollback | 0 | 1 | 0 | 0 | PASS |
| Required first H2 Quantity RestockSellable/Scrap | 0 | 2 | 0 | 0 | PASS |
| Original affected protected filter | 0 | 15 | 0 | 0 | PASS |
| Existing D13 source capacity/return/replay | 0 | 6 | 0 | 0 | PASS |
| New concurrency/fractional attempt01 | 1 | 0 | 0 | 0 | FAIL: style compilation, tests not executed |
| New concurrency/fractional attempt02 | 0 | 5 | 0 | 0 | PASS |
| Combined attempt01 | 1 | 26 | 1 | 0 | FAIL: omission fixture missed earlier canonical flush |
| Combined attempt02 | 0 | 27 | 0 | 0 | PASS |

Every run has terminal zero-to-latest migration, no-pending-model-changes, owned PostgreSQL shutdown and guarded cleanup PASS. Exact commands and each command's counts/provider/completion are in resolution05-return-writer-command-index-20261006.json; retained terminal-result.json/log/TRX are authoritative. These focused results are not full regression or final certification.

## Test integrity

NEW_COVERAGE: four hostile remaining-one claim-vs-return/return-vs-return vectors for Quantity/Length; fractional Length claim/return replay; original deferred COMMIT attack/rollback. Existing H1/H2 assertions/data unchanged.

HARNESS_CORRECTION: old-writer fixture now detaches new return facts when EF tracks them, because canonical collaborators can flush before the injected UnitOfWork. No database gates/assertions weakened. Latest attack explicitly omitted1 and reproduced SQLSTATE23514 with complete rollback. Snapshot coverage adds immutable allocation rows. Missing-braces style correction retained with failed first attempt. These harness edits require later independent integrity review.

No ASSERTION_CHANGE, CONCURRENCY_CHANGE or SKIP_CHANGE. New concurrency vectors are additions, not changes to existing protected races. Expected100 to55 authorized alignment has not yet been applied.

## Exact next scope and limits

D13_MATRIX_COVERAGE_UNFINISHED; source NOT FROZEN. Complete multiple same-product SaleItems, wrong Supplier/source/lot/PurchaseItem, overcapacity return, payload mismatch characterization/Phase12 handoff where applicable, CommercialExchange race/replay/rollback, permission/audit/append-only matrix. Preserve all green focused evidence; rerun only affected proofs or mandatory integrated/final gates.

Then D13-2 provenance, partial/custody/current MWA55 economics and bounded independent Resolution05 challenge, followed by D17 and master remaining closure, registry/event matrix/goldens/owner reconciliation, full/protected regression, freeze,21 gates, new independent certifier, rehash/lock. None is claimed complete. Operational DB/services/Desktop untouched; all owned tasks terminal and cleanup verified.
