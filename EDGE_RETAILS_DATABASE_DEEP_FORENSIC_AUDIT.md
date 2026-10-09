# Edge Retails PostgreSQL Deep Forensic Audit

Audit date: 2026-10-05 (Asia/Karachi)  
Workspace: `C:\Users\muham\OneDrive\Desktop\Point of Sale`  
Scope: source-derived EF model, all checked-in migrations, isolated PostgreSQL 18.6 migration/rehearsal, and read-only SELECT reconciliation against test fixtures. No operational database connection was used.

## Executive verdict

**DATABASE_AUDIT_COMPLETE_WITH_FINDINGS.** The 21-migration chain applied from an empty disposable PostgreSQL 18.6 cluster; `dotnet ef migrations has-pending-model-changes` reported no changes. The physical schema has substantial relational protection: primary/foreign keys, restrictive business-history references, check constraints, unique indexes, fixed-point money/quantity fields, and transaction-scoped locking in the application. The audit reproduced direct-SQL integrity gaps and an inventory keyset pagination failure on PostgreSQL. No production data was queried, so live-row reconciliation, deployed role permissions, backups, restore compatibility, and operational query plans remain unverified.

**Grades (evidence-limited):**

| Area | Grade / 10 | Basis |
|---|---:|---|
| Database architecture | 7 | Clear domain schemas and durable facts; several commercial relationships remain application-enforced. |
| Schema integrity | 6 | Strong FK/check/unique coverage; no database immutability guards, and some identity/provenance combinations are not constrained. |
| Migration safety | 8 | Clean PostgreSQL 18.6 replay passed; one model/physical index semantic mismatch; rollback of several safety migrations is intentionally unsupported. |
| Inventory integrity | 7 | Movement/lot/cost projections and exact identity have strong model support; unit state/status and movement arithmetic can be changed directly. |
| Financial integrity | 5 | Fixed point money, immutable snapshots by convention, ledger facts and transaction boundaries exist; direct SQL can rewrite key totals and ledgers. |
| Physical identity / provenance | 7 | Tracking claims are unique and normalized, sequence authority is locked; provenance links do not prove all cross-entity business equality. |
| Concurrency | 7 | Read committed transactions, advisory/resource locks, row locks and unique indexes; lock discipline remains application-wide. |
| Replay persistence | 5 | Durable outcomes and unique operation IDs exist; payload fingerprint/result fields are nullable and observed successful fixture outcomes omitted fingerprints. Phase 12 owns hardening. |
| Performance / indexing | 6 | Many focused indexes and bounded list queries; inventory cursor failure and five FKs lack a plain leading index. No production cardinality/plans. |
| Security / least privilege | 3 | Runtime role/grant/connection configuration could not be attested; only disposable superuser was used for rehearsal. |
| Operational resilience | 5 | Migration compatibility and backup/restore code paths exist; operational backup, role, pool, restore, and growth behavior were not live-proven. |

## Findings totals

Critical: 0 · High: 4 · Medium: 4 · Low: 2 · Info: 1.

The findings register is authoritative for full evidence, ownership and remediation boundaries. Highest risk: high-value ledger/history and financial rows can be rewritten or deleted using SQL under any principal granted those table privileges. Highest-priority cluster: database-level integrity/immutability defenses and runtime principal/grant verification, owned by CURRENT_DATABASE_SCHEMA / OPERATIONAL_DEPLOYMENT; do not implement Phase 8, 9 or 12 work here.

## Key evidence

- EF Core 10.0.12, Npgsql EF provider 10.0.3; one application DbContext, `EdgeRetailsDbContext`; 83 mapped entities/tables (84 physical user tables including EF migration history).
- PostgreSQL 18.6; 21 migrations, latest `20261002101709_TrackingManufacturerIdentityAuthorityV1`; clean replay succeeded; pending model changes: none.
- Physical catalog: 11 schemas; 84 tables; 0 views/materialized views/sequences/enums/domains/triggers/functions; 398 indexes (265 unique); 84 PKs, 124 FKs, 59 CHECK constraints.
- Schema/model comparison matched all columns, types and nullability and all mapped constraints/indexes except the documented open-stocktake index semantic difference. Two PostgreSQL-only partial indexes enforce one default purchase and sale unit per product.
- Isolated PostgreSQL integration test waves passed: 81 protected Pass4/identity/concurrency tests; 36 purchasing/expense/return/inventory tests; 43 replay/transaction/stocktake/tracking/Thaka tests; and 72 pagination/Pass4/tracking tests. Zero failures/skips. The waves overlap, so these are not unique test counts. They are selected suites, not the whole test suite.
- Disposable fixture SELECTs: sale header/line/payment arithmetic, over-return and required exact-unit provenance returned no anomalies. Movement/lot reconciliation reported one stock-balance-only row seeded by an existing test fixture; it was not treated as a product defect. Successful operation outcomes with null fingerprint were present in the test fixture and are routed to Phase12.
- Direct SQL probes were executed only in the owned disposable cluster; every UPDATE/DELETE and the sequence rollback probe was rolled back. Probes showed the checked records were mutable/deletable and selected invalid enum/provenance states were accepted.
- Main workspace was already dirty before this audit. Baseline status and file hashes are archived under `artifacts/database-audit-20261005`. No source/test write operation was issued by this audit. At final check, 15 of 866 source/document/test hashes differed from the audit-start capture; several had modification times during the audit window. Since the workspace is shared, authorship cannot be determined; those changes were preserved.

## Limits and verdict fields

PostgreSQL server version: 18.6 (owned disposable rehearsal cluster).  
EF Core model: 10.0.12; one DbContext; 83 mapped entities.  
Schemas: `audit`, `catalog`, `finance`, `identity`, `inventory`, `parties`, `purchasing`, `sales`, `system`, `thaka`, `warranty`.  
Tables: 84 physical; 83 mapped plus migration history.  
Migrations: 21; latest listed above.  
Pending EF changes: none (tool confirmed).  
Migration currently proven necessary: not yet proven.  
Operational DB modified: NO. Schema modified: NO. Business data modified: NO. Audit issued no source/test/migration writes. Full-window source/test integrity: NOT ATTESTED (15/866 hashes changed; attribution unknown). Migrations created: NO. Git history modified: NO. Phase7 Pass5 modified: NO.

See the companion inventory, relationship matrix, migration report, index/query report, reconciliation report, and findings register.
