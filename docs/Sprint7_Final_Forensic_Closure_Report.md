# Edge Retails — Sprint 7 Final Forensic Closure Report

**Sprint:** 7  
**Status:** CLOSED  
**PostgreSQLGate:** PASS  
**ClosedAt:** 2026-09-21T12:31:13.4943248+05:00  
**Device:** Ali  
**Git:** untouched

## Closure objective

Sprint 7 production backend cutover is formally closed after executing the previously pending real-PostgreSQL runtime gate on an isolated local PostgreSQL 18 cluster.

The live system PostgreSQL service and authentication configuration were not weakened or modified. A disposable localhost-only cluster on port 55432 was created solely for verification.

## Real PostgreSQL migration evidence

Canonical EF Core migrations were applied successfully to the isolated database:

1. 20260920094824_InitialProductionBaseline
2. 20260920111318_Sprint7Phase1SetupIdentity
3. 20260920164958_Sprint7ProductionCutover

The EF migrations history contained all three migrations after update.## Runtime defect found and repaired

The first real PostgreSQL integration run exposed a Dapper materialization defect in InventoryProvenanceReadService.GetProductSaleHistoryAsync.

Npgsql exposed the PostgreSQL timestamp field as System.DateTime while ProductSaleHistoryRowDto requires DateTimeOffset. Direct DTO materialization therefore failed at runtime.

The service was repaired to use a database projection row with DateTime CompletedAt and explicitly map it through the existing UTC conversion boundary into ProductSaleHistoryRowDto.

No business rule, document history, stock logic, or public Application contract was weakened.

## Final verification matrix

- Debug full solution build: PASS — 0 warnings / 0 errors
- Release full solution build: PASS — 0 warnings / 0 errors
- Debug unit/forensic tests: PASS — 159/159
- Release unit/forensic tests: PASS — 159/159
- Debug real PostgreSQL integration tests: PASS — 10/10
- Release real PostgreSQL integration tests: PASS — 10/10
- EF pending model changes: NONE
- Changed-file dotnet format verification: PASS
- Real migration application: PASS
- PostgreSQL schema/constraint tests: PASS
- Transactional Sales/Purchasing tests: PASS
- Serialized provenance tests: PASS- Desktop architecture dependency tests: PASS
- Desktop direct Npgsql/DbContext/SaveChanges authority: NOT PRESENT

## Architecture invariants preserved

1. Desktop does not execute PostgreSQL or EF persistence directly.
2. Application/Infrastructure remain business and persistence authority.
3. Inventory movement/state remains stock authority.
4. Completed documents remain correction-by-return/reversal/void, not destructive mutation.
5. PostgreSQL constraints remain independently authoritative.
6. PIN plaintext is not persisted.
7. EF model snapshot and migrations remain synchronized.
8. Git was not used for closure.

## Final declaration

Sprint 7 is formally CLOSED.

The previously unfinished real-PostgreSQL closure item is complete. Sprint 8 may now proceed to its protected pre-merge snapshot, live-contract reconciliation, additive merge, and production closure gates.

This report is the authoritative Sprint 7 closure evidence for Sprint 8 preflight.