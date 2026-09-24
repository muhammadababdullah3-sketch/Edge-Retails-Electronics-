# Edge Retails — Database Migration Map & Schema Lifecycle Governance

**Document Identifier:** `ER-OPS-DB-MIG-01`  
**Phase:** Phase 6 — Final Certification & Long-Term Maintenance  
**Canonical Architecture SHA-256:** `12344760C60124DDC2D1C0E54ABFB7BC0E82B9B23A46E6463303EBFA530AB673`  
**Database Engine:** PostgreSQL 18.x  
**Authoritative Migration Provider:** Entity Framework Core (`EdgeRetailsDbContext`)  
**Target Environment:** Local Standalone POS / Local LAN Shop Server (`EdgeRetails.Server`)

---

## 1. Executive Summary & Schema Principles

The Edge Retails database schema operates on strict transactional integrity, zero model drift, and fail-closed compatibility verification. The database consists of **63 canonical tables** partitioned across **8 dedicated PostgreSQL schemas**:
`catalog`, `finance`, `identity`, `inventory`, `parties`, `sales`, `system`, and `warranty`.

### Core Architectural Invariants:
1. **Append-Only Migration History:** Migrations are linear, immutable, and strictly ordered. Once a migration is committed and certified in production, it is never modified or retroactively renumbered.
2. **Fail-Closed Runtime Probe:** On every application and server startup, `EfMigrationCompatibilityProbe` verifies that the physical database migration state exactly matches the compiled model. Any diverged history, unapplied migration, or unrecognized migration aborts startup immediately.
3. **No Automatic Destructive Down-Migrations:** Production rollbacks are never performed via speculative downward migration scripts (`Down()`). Rollback is achieved strictly via the atomic two-phase database restore cutover mechanism (`PostgresBackupEngine`) or forward-fix migration patches.
4. **Zero Model Drift:** Every build enforces `dotnet ef migrations has-pending-model-changes --no-build` in CI/CD and release certification scripts.

---

## 2. Authoritative Migration Register & History

The production migration history is maintained in `src/EdgeRetails.Infrastructure/Persistence/Migrations/` and recorded at runtime in the PostgreSQL table `system.__ef_migrations_history`.

| Order | Migration ID | Applied Schema Scope | Core Entities & Structural Changes |
| :---: | :--- | :--- | :--- |
| **01** | `20260920094824_InitialProductionBaseline` | `catalog`, `identity`, `sales`, `system` | Foundational table creation: `users`, `roles`, `categories`, `products`, `units`, `sales`, `sale_items`, `pos_drafts`, `quotations`, `document_sequences`, base constraints, foreign keys, and indexes. |
| **02** | `20260920111318_Sprint7Phase1SetupIdentity` | `system`, `identity` | Adds `system.installation_state`, `system.shop_profile`, initial admin user seed identity, and initial system installation tracking flags. |
| **03** | `20260920164958_Sprint7ProductionCutover` | `system`, `sales` | Introduces `system.receipt_template_settings`, transactional receipt sequence persistence, document sequence counters, and shop customization parameters. |
| **04** | `20260921101001_Sprint8CanonicalReportingSchema` | `sales`, `finance` | Aligns reporting timestamps to UTC, adds normalized financial view compatibility, and standardizes index definitions across financial dimensions. |
| **05** | `20260921143542_Sprint8FinalProductionAlignment` | `parties`, `finance`, `inventory` | Introduces `parties.customers`, `parties.suppliers`, `finance.expenses`, `finance.expense_categories`, `finance.expense_subcategories`, `finance.cash_sessions`, `finance.cash_movements`, and `finance.supplier_account_entries` (Khata ledger). |
| **06** | `20260921152602_Sprint8WarrantyAlignment` | `warranty` | Introduces `warranty.claims`, `warranty.claim_items`, `warranty.claim_events`, `warranty.claim_item_units`, and `warranty.shop_stock_cases` for complete serialized electronics RMA tracking. |
| **07** | `20260922120000_Phase1CanonicalSchemaAlignment` | All 8 Schemas | Full canonical architectural schema re-partitioning across all 8 schemas. Enforces foreign key constraints, check constraints, default expressions, and naming conventions. |
| **08** | `20260922135055_Phase3ProductionSafetyOutbox` | `system` | Introduces `system.outbox_messages` for guaranteed at-least-once physical side effect execution (receipt printing, drawer kicks, audit persistence) with statuses: `Pending` (1), `Processing` (2), `Completed` (3), `Failed` (4), `ActionRequired` (5). |
| **09** | `20260923071510_Phase4MultiTerminalSchema` | `system` | Introduces `system.terminals` supporting multi-terminal LAN server topology. Tracks `TerminalId`, `TerminalCode`, `Name`, `Status` (Active=1, Suspended=2, Revoked=3), `HardwareFingerprint`, `ProtocolVersion`, `LastKnownIpAddress`, `RegisteredAt`, `LastSeenAt`, and `AuthSecretHash`. |
| **10** | `20260923095632_Phase5WarrantyClaimClientOperationId` | `warranty` | Adds `client_operation_id` to `warranty.claims` with unique compound indexing to guarantee idempotent warranty registration over unreliable networks. |
| **11** | `20260923110943_Phase5WarrantyLifecycleIdempotency` | `warranty` | Adds idempotency constraints to `warranty.claim_events` and lifecycle state transitions to prevent duplicate RMA status mutations. |
| **12** | `20260923111027_Phase5MovementHistoryOrderingIndex` | `inventory` | Adds compound performance index `ix_movements_product_occurred_id` on `inventory.movements(product_id, occurred_at_utc, id)` to eliminate sorting overhead during ledger pagination and cost valuation. |
| **13** | `20260923125420_Phase5PurchaseHistoryOrderingIndex` | `finance` / `sales` | Adds compound performance index on supplier purchase and account transactions `(supplier_id, created_at_utc, id)` for sub-millisecond bounded Khata statements. |

---

## 3. Canonical Schema Census (63 Tables)

The active production schema contains exactly **63 physical tables** across the 8 namespaces:

```
+---------------------------------------------------------------------------------------+
| EDGE RETAILS POSTGRESQL 18 PRODUCTION SCHEMA TOPOLOGY (63 TABLES)                    |
+---------------------------------------------------------------------------------------+
|  catalog (6)           finance (10)                 identity (6)                      |
|  - categories          - cash_movements             - permissions                     |
|  - product_unit_bar-   - cash_sessions              - role_permissions                |
|    codes               - expense_categories         - roles                           |
|  - product_units       - expense_subcategories      - user_permission_overrides       |
|  - products            - expenses                   - user_sessions                   |
|  - supplier_products   - supplier_account_entries   - users                           |
|  - units               - supplier_payment_reversals                                   |
|                        - supplier_payments          parties (2)                       |
|  inventory (14)        - supplier_refund_reversals  - customers                       |
|  - cost_states         - supplier_refunds           - suppliers                       |
|  - lot_bucket_balan-                                                                  |
|    ces                 sales (12)                   system (8)                        |
|  - lot_consumptions    - pos_draft_items            - __ef_migrations_history         |
|  - lots                - pos_drafts                 - document_sequences              |
|  - movement_effects    - quotation_items            - installation_state              |
|  - movement_units      - quotation_operations       - outbox_messages                 |
|  - movements           - quotations                 - receipt_template_settings       |
|  - stock_adjustment_-  - return_item_units          - shop_profile                    |
|    items               - return_items               - supplier_code_sequences         |
|  - stock_adjustments   - returns                    - terminals                       |
|  - stock_balances      - sale_item_units                                              |
|  - stocktake_items     - sale_items                 warranty (5)                      |
|  - stocktake_unit_che- - sale_payments              - claim_events                    |
|    cks                 - sales                      - claim_item_units                |
|  - stocktakes                                       - claim_items                     |
|  - units                                            - claims                          |
|                                                     - shop_stock_cases                |
+---------------------------------------------------------------------------------------+
```

---

## 4. Runtime Compatibility State Machine

Database migration compatibility is inspected on every boot via `EfMigrationCompatibilityProbe<TContext>` and evaluated using `MigrationHistoryCompatibilityEvaluator`:

```
                           [Database Connected]
                                    |
                     +--------------+--------------+
                     | Read Known & Applied Sets   |
                     +--------------+--------------+
                                    |
      +-----------------------------+-----------------------------+
      | Any applied not in known?                                 |
      +--> YES: MigrationCompatibilityState.DatabaseAhead         |
      |                                                           |
      | Applied != Known prefix?                                  |
      +--> YES: MigrationCompatibilityState.HistoryDiverged       |
      |                                                           |
      | Any known not applied?                                    |
      +--> YES: MigrationCompatibilityState.DatabaseBehind        |
      |                                                           |
      | HasPendingModelChanges() == true?                         |
      +--> YES: MigrationCompatibilityState.ModelDrift            |
      |                                                           |
      | All checks satisfied?                                     |
      +--> YES: MigrationCompatibilityState.Compatible            |
```

### State Definitions & Operational Action Rules:

1. **`Compatible` (Healthy / Ready):**
   - *Condition:* Database migrations perfectly match the compiled model; zero pending, zero unknown, zero divergence, zero model drift.
   - *Action:* System proceeds to normal business operations.
2. **`DatabaseBehind` (Action Required):**
   - *Condition:* Database is missing one or more canonical migrations present in the application binaries.
   - *Action:* System startup is blocked. An administrative database update must be executed (`dotnet ef database update` or service updater).
3. **`DatabaseAhead` (Fail-Closed / Blocked):**
   - *Condition:* The database contains migration IDs not recognized by the running application binary (typically caused by deploying an older executable against a newer schema).
   - *Action:* System startup is strictly prohibited. Prevents silent data corruption. Operators must upgrade application binaries to match or exceed the database version.
4. **`HistoryDiverged` (Fail-Closed / Blocked):**
   - *Condition:* Applied migrations do not match the canonical compilation order.
   - *Action:* System startup is strictly prohibited. Database migration history has been tampered with or corrupted. Requires disaster recovery restore or manual forensic reconciliation by an authorized DBA.
5. **`ModelDrift` (Fail-Closed / Blocked):**
   - *Condition:* The compiled EF Core entity model has modifications not represented in any migration snapshot (`HasPendingModelChanges() == true`).
   - *Action:* Build gate fails. Developers must create a canonical migration before releasing.
6. **`ProbeFailed` (Unavailable / Blocked):**
   - *Condition:* Database query failed, connection timed out, or permissions were insufficient to inspect `system.__ef_migrations_history`.
   - *Action:* System remains in diagnostic holding state until database connectivity and credentials are restored.

---

## 5. Migration Execution & Verification Commands

### Check Migration Status:
```powershell
# Inspect pending migrations without modifying schema
dotnet ef migrations list `
    --project "src/EdgeRetails.Infrastructure" `
    --startup-project "src/EdgeRetails.Infrastructure"
```

### Verify Zero Model Drift:
```powershell
# Fail-closed check for pending model changes
dotnet ef migrations has-pending-model-changes `
    --project "src/EdgeRetails.Infrastructure" `
    --startup-project "src/EdgeRetails.Infrastructure" `
    --no-build
```

### Apply Pending Migrations to Target Database:
```powershell
# Controlled forward migration application
$env:EDGE_RETAILS_DB = "Host=localhost;Port=5432;Database=edgeretails_prod;Username=postgres;Password=$YourPassword;"
dotnet ef database update `
    --project "src/EdgeRetails.Infrastructure" `
    --startup-project "src/EdgeRetails.Infrastructure"
```

### Direct PostgreSQL SQL Census Query:
```sql
-- Query applied migration history directly
SELECT "MigrationId", "ProductVersion"
FROM system.__ef_migrations_history
ORDER BY "MigrationId" ASC;
```

---

## 6. Migration Rollback Policy

> [!CAUTION]
> Automatic downward migrations (`Down()`) are **STRICTLY PROHIBITED** in production environments.

### Rationale:
1. Downward migrations frequently drop columns, truncate tables, or destroy historical financial audit ledgers that cannot be regenerated.
2. In multi-terminal retail environments, transactions may have been committed between migration application and rollback detection.

### Production Rollback Procedure:
If a migration introduces an operational blocker:
1. **Engage Maintenance Mode:** Enter exclusive maintenance barrier (`ProductionMaintenanceState.RecoveryRequired`).
2. **Execute Point-in-Time Restore:** Restore the pre-migration snapshot taken immediately prior to the upgrade using `PostgresBackupEngine.CutoverAsync` (see [Backup & Restore Runbook](Backup_Restore_Runbook.md)).
3. **Deploy Forward-Fix Migration:** If data was committed after migration, create and deploy an approved forward-patch migration (`202609..._Fix...cs`) rather than attempting schema reversal.
