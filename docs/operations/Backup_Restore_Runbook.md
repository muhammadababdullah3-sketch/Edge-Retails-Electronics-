# Edge Retails — Backup & Restore Operations Runbook

**Document Identifier:** `ER-OPS-BCK-01`  
**Phase:** Phase 6 — Final Certification & Long-Term Maintenance  
**Canonical Architecture SHA-256:** `12344760C60124DDC2D1C0E54ABFB7BC0E82B9B23A46E6463303EBFA530AB673`  
**Target Subsystems:** `EdgeRetails.Worker`, `EdgeRetails.Infrastructure.Production.Backup`, `EdgeRetails.Desktop`  
**Cryptographic Primitives:** AES-256-GCM (Payload), HMAC-SHA256 (Manifest), SHA-256 (Integrity)

---

## 1. Architecture & Security Overview

The Edge Retails backup and restore subsystem guarantees business continuity, cryptographic confidentiality, tamper evidence, and zero-data-loss cutover.

```
+---------------------------------------------------------------------------------------------------+
| BACKUP CREATION LIFECYCLE                                                                         |
|                                                                                                   |
|  [PostgreSQL 18 DB]                                                                               |
|          |                                                                                        |
|    1. pg_dump (--format=custom)                                                                   |
|          v                                                                                        |
|    [plainTemp.dump] ---> 2. pg_restore --list (Validate syntax)                                   |
|          |                                                                                        |
|    3. AES-256-GCM Encrypt                                                                         |
|          v                                                                                        |
|    [*.erbak] (Encrypted payload, SHA-256 hashed)                                                  |
|          +                                                                                        |
|    [*.erbak.manifest.json] (HMAC-SHA256 signed with "EdgeRetails.BackupManifest.HMAC.V1")        |
|          |                                                                                        |
|    4. Post-Commit Retention Pruning (Failsafe: retains existing backups if pruning warns)          |
+---------------------------------------------------------------------------------------------------+
```

### Key Technical Characteristics:
- **Format Version:** `FormatVersion = 2`.
- **Payload Encryption:** AES-256-GCM via `AesGcmBackupProtector`. Key is 32 cryptographically random bytes retrieved via `IBackupEncryptionKeyProvider`.
- **Manifest Authentication:** `HmacBackupManifestAuthenticator` computes HMAC-SHA256 over canonical JSON using a key derived via HMAC-SHA256 with domain label `EdgeRetails.BackupManifest.HMAC.V1`.
- **Companion File Requirement:** An `.erbak` backup artifact is **never valid** without its companion `.erbak.manifest.json` file.
- **Path Safety:** All filesystem operations use `BackupArtifactPathSafety.ResolveOwnedBackupPath`, which strictly forbids path traversal (`..`), alternate data streams, and reparse points / symlinks.

---

## 2. Backup Operations

### 2.1 Scheduled Backups
Scheduled backups are executed autonomously by `EdgeRetails.Worker` via `ScheduledBackupJob`.
- **Default Schedule:** Daily at 02:00:00 Local Shop Time.
- **Storage Directory:** Controlled via `EDGE_RETAILS_BACKUP_DIR` (Defaults to `%LOCALAPPDATA%\EdgeRetails\Production\backups`).
- **Retention Policy:** Keeps last 14 daily backups and 4 monthly archives.
- **Locking:** Protected by `IBackupJobLock` (`FileBackupJobLock`) to prevent concurrent executions between worker instances or manual triggers.

### 2.2 On-Demand Manual Backup Execution
Administrators can trigger an immediate backup via PowerShell, POS Settings UI, or CLI:

```powershell
# Execute on-demand backup script
$backupDir = "$env:LOCALAPPDATA\EdgeRetails\Production\backups"
$connString = $env:EDGE_RETAILS_POSTGRES_CONNECTION

# Run EdgeRetails backup command
dotnet run --project "src/EdgeRetails.Worker" -- backup `
    --directory "$backupDir" `
    --database "edgeretails_prod"
```

### 2.3 Backup Artifact Inspection & Verification
Every backup generates two coupled files:
1. `edgeretails_prod_20260924_020000_<guid>.erbak`
2. `edgeretails_prod_20260924_020000_<guid>.erbak.manifest.json`

Example `manifest.json`:
```json
{
  "manifest": {
    "backupId": "b6a5b7c2-1234-4567-89ab-cdef01234567",
    "databaseName": "edgeretails_prod",
    "fileName": "edgeretails_prod_20260924_020000_b6a5b7c21234456789abcdef01234567.erbak",
    "sha256": "D3F2A4...<64 hex characters>...",
    "sizeBytes": 14589234,
    "createdAtUtc": "2026-09-24T02:00:05.1234567Z",
    "postgresServerVersion": "18.0",
    "pgDumpVersion": "18.0",
    "applicationVersion": "1.0.0",
    "schemaVersion": "20260923125420_Phase5PurchaseHistoryOrderingIndex",
    "protection": "AES-256-GCM",
    "formatVersion": 2
  },
  "authentication": "8F3B2C...<64 hex HMAC characters>..."
}
```

---

## 3. Restore Operations & Phased Cutover

> [!IMPORTANT]
> Edge Retails implements a **two-phase transactional restore with zero destructive in-place overwrite**. The production database is preserved under an archive name until the restored staging instance is verified healthy.

```
+---------------------------------------------------------------------------------------------------+
| TWO-PHASE RESTORE CUTOVER LIFECYCLE                                                               |
|                                                                                                   |
|  [PHASE 1: PREPARATION]                                                                           |
|   1. Enter Maintenance Barrier: state = RestorePreparing                                          |
|   2. Validate companion manifest HMAC, size, and SHA-256 hash                                     |
|   3. Decrypt .erbak to temporary storage                                                          |
|   4. Restore via pg_restore into staging DB: _staging_<restoreId>                                 |
|   5. IRestoreStagingValidator executes SQL schema, table count, and business ledger checks       |
|   6. Transition Maintenance Barrier: state = RestoreReady                                         |
|                                                                                                   |
|  [PHASE 2: CUTOVER]                                                                               |
|   1. Enter Maintenance Barrier: state = RestoreCutover (blocks all business writes)               |
|   2. Lock connections: ALTER DATABASE edgeretails_prod ALLOW_CONNECTIONS = false                 |
|   3. Terminate sessions: SELECT pg_terminate_backend(pid) FROM pg_stat_activity...                |
|   4. Atomic rename:                                                                               |
|        edgeretails_prod        --> pre_edgeretails_prod_<restoreId>   (PRESERVED ORIGINAL)        |
|        _staging_<restoreId>    --> edgeretails_prod                   (ACTIVE RESTORED)           |
|   5. Re-enable connections: ALLOW_CONNECTIONS = true                                              |
|   6. Verify PostgreSQL OIDs match staging session token                                           |
|   7. Execute SELECT 1 readiness probe                                                             |
|   8. Transition Maintenance Barrier: state = Normal                                               |
+---------------------------------------------------------------------------------------------------+
```

### 3.1 Step-by-Step Operator Restore Procedure

#### Step 1: Pre-Restore Triage & Notification
1. Ensure all terminals are informed of maintenance downtime.
2. Verify that PostgreSQL 18 service (`postgresql-x64-18`) is running.
3. Identify the target `.erbak` and `.manifest.json` files.

#### Step 2: Phase 1 — Preparation & Staging Validation
Execute the preparation command via the Management CLI:
```powershell
$targetErbak = "$backupDir\edgeretails_prod_20260924_020000_<guid>.erbak"

dotnet run --project "src/EdgeRetails.Worker" -- restore prepare `
    --artifact "$targetErbak"
```
*Expected Output:*
- HMAC verification: **PASS**
- SHA-256 payload integrity: **PASS**
- Staging database `_staging_<restoreId>` created and populated.
- Staging ledger reconciliation: **PASS** (63 tables verified).
- Session Token generated: `RESTORE_SESSION_<token>`.

#### Step 3: Phase 2 — Final Cutover
Execute the cutover using the acquired session token:
```powershell
dotnet run --project "src/EdgeRetails.Worker" -- restore cutover `
    --session-token "RESTORE_SESSION_<token>"
```
*Expected Output:*
- Connections to `edgeretails_prod` terminated cleanly.
- Original database preserved as `pre_edgeretails_prod_<restoreId>`.
- Restored database activated as `edgeretails_prod`.
- Post-cutover OID validation: **PASS**.
- Maintenance barrier restored to `Normal`.

---

## 4. Rollback & Failure Recovery

### 4.1 Automatic Rollback
If any exception occurs during `CutoverAsync` (e.g., OID mismatch, post-cutover SQL readiness probe failure), the engine automatically executes `RollBackByDatabaseOidAsync`:
1. Re-terminates active connections.
2. Swaps database names back to their original configuration:
   - `edgeretails_prod` (bad) -> `bad_edgeretails_prod_<restoreId>`
   - `pre_edgeretails_prod_<restoreId>` -> `edgeretails_prod`
3. Restores maintenance state to `Normal`.
4. The system resumes running against the original pre-restore database with zero data loss.

### 4.2 Manual Disaster Recovery (`RecoveryRequired` State)
If both cutover AND automatic rollback fail (e.g., PostgreSQL service crash or disk exhaustion midway through renaming), the maintenance barrier transitions to **`ProductionMaintenanceState.RecoveryRequired`**.

In this state:
- All business writes are strictly blocked across Desktop and LAN Server.
- To resolve:
  1. Inspect PostgreSQL databases directly via `psql`:
     ```sql
     SELECT datname, oid FROM pg_database WHERE datname LIKE '%edgeretails%';
     ```
  2. Identify which database has the complete data (`pre_...` or `_staging_...`).
  3. Rename the authoritative database to `edgeretails_prod`:
     ```sql
     ALTER DATABASE edgeretails_prod RENAME TO corrupt_edgeretails_prod;
     ALTER DATABASE pre_edgeretails_prod_<restoreId> RENAME TO edgeretails_prod;
     ```
  4. Reset the maintenance state barrier by deleting the lock and writing a valid state envelope, or running:
     ```powershell
     dotnet run --project "src/EdgeRetails.Worker" -- maintenance reset-barrier
     ```

---

## 5. Post-Restore Verification Checklist

After any restore operation, operators must execute the following verifications before resuming cashier operations:

- [ ] **Database Readiness:** Execute `SELECT NOT pg_is_in_recovery();` (returns `true`).
- [ ] **Schema Compatibility:** Run `EfMigrationCompatibilityProbe` (reports `Compatible`).
- [ ] **Diagnostic Suite:** Execute full diagnostics snapshot (all 11 probes report `HEALTHY`).
- [ ] **Ledger Integrity:** Verify cash balance of the latest closed cash session matches historical records.
- [ ] **Inventory Snapshot:** Verify sellable quantities of top 5 high-turnover products.
- [ ] **Terminal Reconnection:** Allow cashier terminals to perform authoritative revalidation.
