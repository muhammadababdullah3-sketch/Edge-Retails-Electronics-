# Migration Forensic Report

## Summary

- Migration count: 21, ordered by timestamp/name below.
- Latest migration: `20261002101709_TrackingManufacturerIdentityAuthorityV1`.
- Snapshot: `EdgeRetailsDbContextModelSnapshot`; `dotnet ef migrations has-pending-model-changes` returned no changes.
- Clean PostgreSQL 18.6 migration-from-zero completed successfully and created 84 user tables and their indexes/constraints.
- No migrations were generated or changed by this audit.
- All migration classes contain `Down`; three recent Phase3 safety migrations explicitly throw NotSupportedException on downgrade. Other generated Down methods commonly drop created objects and can therefore discard data; rollback is not a safe production recovery strategy.
- No actual operational database version/history was accessed.

## Chronological migration ledger

Counts are source operation counts, not risk weights. Data transformations and SQL are summarized from live migration source.

### `20260920094824_InitialProductionBaseline`

- Purpose: Initial schema; creates core catalog/inventory/sales/purchasing/finance/security/operations structures and PostgreSQL partial unique indexes for default units and one active stocktake.
- Source operation counts: CreateTable=47, CreateIndex=115, Sql=1, DropTable=47
- Raw SQL: 1 block(s); inspect source when replaying against legacy data.
- Downgrade method: implemented; destructive drops may discard later business data.

### `20260920111318_Sprint7Phase1SetupIdentity`

- Purpose: Incremental schema/index/constraint alignment; see migration source for exact operation and Down details.
- Source operation counts: CreateTable=7, CreateIndex=11, DropTable=7
- Raw SQL: 0 block(s); inspect source when replaying against legacy data.
- Downgrade method: implemented; destructive drops may discard later business data.

### `20260920164958_Sprint7ProductionCutover`

- Purpose: Incremental schema/index/constraint alignment; see migration source for exact operation and Down details.
- Source operation counts: CreateTable=12, CreateIndex=38, DropTable=12, DropColumn=2
- Raw SQL: 0 block(s); inspect source when replaying against legacy data.
- Downgrade method: implemented; destructive drops may discard later business data.

### `20260921101001_Sprint8CanonicalReportingSchema`

- Purpose: Incremental schema/index/constraint alignment; see migration source for exact operation and Down details.
- Source operation counts: no MigrationBuilder operations detected
- Raw SQL: 0 block(s); inspect source when replaying against legacy data.
- Downgrade method: implemented; destructive drops may discard later business data.

### `20260921143542_Sprint8FinalProductionAlignment`

- Purpose: Large final production alignment: adds snapshot/provenance/precision structures and tightens checks; reverse path drops added columns/tables.
- Source operation counts: CreateTable=9, CreateIndex=32, AddCheckConstraint=1, AddForeignKey=2, DropForeignKey=2, DropTable=9, DropIndex=5, DropCheckConstraint=1, DropColumn=8
- Raw SQL: 0 block(s); inspect source when replaying against legacy data.
- Downgrade method: implemented; destructive drops may discard later business data.

### `20260921152602_Sprint8WarrantyAlignment`

- Purpose: Incremental schema/index/constraint alignment; see migration source for exact operation and Down details.
- Source operation counts: CreateIndex=2, AddCheckConstraint=1, AddForeignKey=1, DropForeignKey=1, DropIndex=2, DropCheckConstraint=1, DropColumn=7
- Raw SQL: 0 block(s); inspect source when replaying against legacy data.
- Downgrade method: implemented; destructive drops may discard later business data.

### `20260922120000_Phase1CanonicalSchemaAlignment`

- Purpose: Canonical schema alignment and checks/columns; explicit Down drops added structures.
- Source operation counts: AddCheckConstraint=2, CreateTable=2, CreateIndex=8, AddForeignKey=1, DropForeignKey=1, DropIndex=1, DropCheckConstraint=2, DropColumn=3, DropTable=2
- Raw SQL: 0 block(s); inspect source when replaying against legacy data.
- Downgrade method: implemented; destructive drops may discard later business data.

### `20260922135055_Phase3ProductionSafetyOutbox`

- Purpose: Incremental schema/index/constraint alignment; see migration source for exact operation and Down details.
- Source operation counts: DropCheckConstraint=2, CreateTable=1, AddCheckConstraint=2, CreateIndex=2, DropTable=1, DropColumn=3
- Raw SQL: 0 block(s); inspect source when replaying against legacy data.
- Downgrade method: implemented; destructive drops may discard later business data.

### `20260923071510_Phase4MultiTerminalSchema`

- Purpose: Incremental schema/index/constraint alignment; see migration source for exact operation and Down details.
- Source operation counts: CreateTable=1, CreateIndex=2, DropTable=1
- Raw SQL: 0 block(s); inspect source when replaying against legacy data.
- Downgrade method: implemented; destructive drops may discard later business data.

### `20260923095632_Phase5WarrantyClaimClientOperationId`

- Purpose: Warranty claim operation identity; raw SQL adds/aligns the idempotency field/index.
- Source operation counts: CreateIndex=1, Sql=1, DropIndex=1, DropColumn=1
- Raw SQL: 1 block(s); inspect source when replaying against legacy data.
- Downgrade method: implemented; destructive drops may discard later business data.

### `20260923110943_Phase5WarrantyLifecycleIdempotency`

- Purpose: Warranty lifecycle operation/idempotency alignment.
- Source operation counts: CreateTable=1, CreateIndex=2, DropTable=1
- Raw SQL: 0 block(s); inspect source when replaying against legacy data.
- Downgrade method: implemented; destructive drops may discard later business data.

### `20260923111027_Phase5MovementHistoryOrderingIndex`

- Purpose: Incremental schema/index/constraint alignment; see migration source for exact operation and Down details.
- Source operation counts: CreateIndex=1, DropIndex=1
- Raw SQL: 0 block(s); inspect source when replaying against legacy data.
- Downgrade method: implemented; destructive drops may discard later business data.

### `20260923125420_Phase5PurchaseHistoryOrderingIndex`

- Purpose: Incremental schema/index/constraint alignment; see migration source for exact operation and Down details.
- Source operation counts: CreateIndex=1, DropIndex=1
- Raw SQL: 0 block(s); inspect source when replaying against legacy data.
- Downgrade method: implemented; destructive drops may discard later business data.

### `20260925142150_Phase1SemanticProductIdentity`

- Purpose: Incremental schema/index/constraint alignment; see migration source for exact operation and Down details.
- Source operation counts: CreateTable=1, CreateIndex=4, AddCheckConstraint=3, AddForeignKey=1, DropForeignKey=1, DropTable=1, DropIndex=2, DropCheckConstraint=3, DropColumn=4
- Raw SQL: 0 block(s); inspect source when replaying against legacy data.
- Downgrade method: implemented; destructive drops may discard later business data.

### `20260927062206_Phase2DurableOperationOutcome`

- Purpose: Incremental schema/index/constraint alignment; see migration source for exact operation and Down details.
- Source operation counts: CreateTable=1, CreateIndex=3, DropTable=1
- Raw SQL: 0 block(s); inspect source when replaying against legacy data.
- Downgrade method: implemented; destructive drops may discard later business data.

### `20260927121724_Phase2OutboxLeaseFencing`

- Purpose: Incremental schema/index/constraint alignment; see migration source for exact operation and Down details.
- Source operation counts: DropColumn=2
- Raw SQL: 0 block(s); inspect source when replaying against legacy data.
- Downgrade method: implemented; destructive drops may discard later business data.

### `20260928150000_Phase3PosPriceOverride`

- Purpose: Incremental schema/index/constraint alignment; see migration source for exact operation and Down details.
- Source operation counts: Sql=1, AddCheckConstraint=1, DropCheckConstraint=1, DropColumn=3
- Raw SQL: 1 block(s); inspect source when replaying against legacy data.
- Downgrade method: implemented; destructive drops may discard later business data.

### `20260929100000_Phase3LegacyCategoryUpgradeRecovery`

- Purpose: Raw PostgreSQL SQL for legacy category upgrade recovery; Down is explicitly unsupported.
- Source operation counts: Sql=1
- Raw SQL: 1 block(s); inspect source when replaying against legacy data.
- Downgrade method: explicitly unsupported.

### `20260930053030_Phase3COwnerPinRecoveryReplaySafety`

- Purpose: Adds owner recovery replay safety; Down is explicitly unsupported.
- Source operation counts: CreateIndex=1
- Raw SQL: 0 block(s); inspect source when replaying against legacy data.
- Downgrade method: explicitly unsupported.

### `20260930065058_Phase3COwnerPinAuthorizationConsumption`

- Purpose: Adds authorization consumption safety; Down is explicitly unsupported.
- Source operation counts: CreateIndex=1
- Raw SQL: 0 block(s); inspect source when replaying against legacy data.
- Downgrade method: explicitly unsupported.

### `20261002101709_TrackingManufacturerIdentityAuthorityV1`

- Purpose: Adds normalized identity claims; uses temporary PostgreSQL PL/pgSQL normalization/backfill and collision/invalid-value guards before creating unique identity indexes; Down removes the authority table/indexes.
- Source operation counts: CreateTable=1, Sql=2, CreateIndex=3, DropTable=1
- Raw SQL: 2 block(s); inspect source when replaying against legacy data.
- Downgrade method: implemented; destructive drops may discard later business data.

## Rehearsal evidence

- The safe rehearsal script isolated `EDGE_RETAILS_DB` to an owned ephemeral PostgreSQL cluster and verified the cluster data directory before migration/testing.
- All 21 migrations applied in order from empty database; latest migration listed above.
- The migration-history table was read from the isolated cluster. Catalog evidence is in `financial-rehearsal/catalog.json`.
- `has-pending-model-changes` passed after replay and said no changes since last migration.
- Server version: PostgreSQL 18.6, Npgsql 10.0.3, EF Core 10.0.12.
- One failed initial audit catalog export queried an incorrectly cased EF history-column name; the query was corrected and the full rehearsal rerun passed. Failed attempt retained separately.

## Findings / risks

- Model/physical open-stocktake unique-index semantics differ (see constraint matrix and MED-02).
- Migration source evolves rapidly in dated append-only sequence. The audit verified deterministic replay in one clean environment, not upgrades from every historical/production state.
- Raw SQL migrations include Unicode normalization/data collision checks that are PostgreSQL-version-specific; replay on PostgreSQL 18.6 passed. No non-18 compatibility claim is made.
- No destructive data backfill failure reproduced; data-bearing upgrade states are not simulated by the empty-database rehearsal.
- No EF migration is currently proven necessary by model drift.
