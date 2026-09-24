# Edge Retails — Disaster Recovery & Outage Runbook

**Document Identifier:** `ER-OPS-DR-01`  
**Phase:** Phase 6 — Final Certification & Long-Term Maintenance  
**Canonical Architecture SHA-256:** `12344760C60124DDC2D1C0E54ABFB7BC0E82B9B23A46E6463303EBFA530AB673`  
**Target Audience:** Operations Engineers, Database Administrators, Technical Support Leads

---

## 1. Disaster Recovery Classification & Response Matrix

| Scenario Identifier | Outage Description | Severity | Target RTO | Target RPO | Primary Recovery Mechanism |
| :--- | :--- | :---: | :---: | :---: | :--- |
| **DR-SCN-01** | Total Physical Hardware Failure / Terminal Destruction | **CRITICAL** | < 60 min | < 24 hrs | Hardware replacement, PostgreSQL 18 provisioning, restore from verified `.erbak` snapshot, license device re-bind. |
| **DR-SCN-02** | PostgreSQL Database Corruption / Checksum Failure | **CRITICAL** | < 30 min | Point of failure | Isolation via maintenance barrier, atomic database cutover restore via `PostgresBackupEngine`. |
| **DR-SCN-03** | Outbox Message Backlog / Worker Pipeline Stall | **HIGH** | < 15 min | 0 sec | Stale lock release, worker service restart, stuck message re-queuing. |
| **DR-SCN-04** | Physical Printer Jam / `OUTCOME_UNKNOWN` Receipt State | **MEDIUM** | < 5 min | 0 sec | Cashier visual paper verification, idempotent receipt reprint or status reconciliation. |
| **DR-SCN-05** | Production Maintenance Barrier Stuck (`RecoveryRequired`) | **HIGH** | < 10 min | 0 sec | Inspection of `.state.json`, database OID validation, barrier state reset. |

---

## 2. DR-SCN-01: Total Physical Hardware Failure / Node Replacement

When a POS machine or LAN Server suffers catastrophic hardware destruction (motherboard failure, disk crash, electrical surge):

### Phase 1: Rapid Infrastructure Provisioning
1. Deploy a replacement PC satisfying minimum specifications (Windows 11/10 Pro x64, 8GB+ RAM, SSD).
2. Install PostgreSQL 18.x and client tools (`pg_dump`, `pg_restore`, `psql`, `createdb`) in `C:\Program Files\PostgreSQL\18\bin`.
3. Provision the PostgreSQL service on port 5432 using `scram-sha-256` authentication.
4. Install Edge Retails using `EdgeRetailsSetup.exe /quiet`.

### Phase 2: Database Restoration
1. Locate the latest authenticated backup pair (`.erbak` and `.manifest.json`) from external storage, network share, or cloud replication.
2. Execute the restore command via the Worker CLI:
   ```powershell
   dotnet run --project "C:\Program Files\Edge Retails\EdgeRetails.Worker.exe" -- restore full `
       --artifact "D:\Backups\edgeretails_prod_latest.erbak"
   ```
3. Verify that all 63 tables are restored and query plans report healthy.

### Phase 3: Hardware Fingerprint & License Re-Binding
Because the physical hardware changed, `IDeviceIdentityProvider` will produce a new `DeviceId`. The existing license will report `LicenseValidationStatus.DeviceMismatch`:
1. Obtain the new Device ID from the Diagnostics screen or CLI:
   ```powershell
   dotnet run --project "C:\Program Files\Edge Retails\EdgeRetails.Desktop.exe" -- show-device-id
   ```
2. Contact Edge Retails Support / Portal with the shop NTN, license ID, and new Device ID.
3. Receive the re-keyed signed license envelope (`.erlic`) and import it:
   ```powershell
   dotnet run --project "C:\Program Files\Edge Retails\EdgeRetails.Desktop.exe" -- import-license `
       --file "D:\Licensing\edgeretails_reissued.erlic"
   ```

---

## 3. DR-SCN-02: PostgreSQL Database Corruption / Checksum Failure

If PostgreSQL detects storage corruption (e.g., bad disk sectors, bit-rot, ungraceful power interruption during WAL flush):

### Symptoms:
- Diagnostic probe reports `db.write_safety.unavailable` or `db.unavailable`.
- PostgreSQL logs indicate `PANIC: could not locate a valid checkpoint record` or `invalid page in block`.

### Step-by-Step Remediation:
1. **Engage Maintenance Mode:**
   Ensure writes are blocked immediately to prevent cascading corruption:
   ```powershell
   $statePath = "$env:LOCALAPPDATA\EdgeRetails\Production\production-maintenance.state.json"
   # Touch recovery required if engine hasn't already entered it
   ```
2. **Isolate Corrupt Database:**
   Connect via `psql` to the default `postgres` maintenance database:
   ```sql
   ALTER DATABASE edgeretails_prod RENAME TO corrupt_edgeretails_prod;
   ```
3. **Restore Clean Copy:**
   Create an empty `edgeretails_prod` database and restore from the latest verified `.erbak`:
   ```powershell
   & "C:\Program Files\PostgreSQL\18\bin\createdb.exe" -U postgres -O edgeretails_user edgeretails_prod
   & "C:\Program Files\PostgreSQL\18\bin\pg_restore.exe" -U postgres -d edgeretails_prod "D:\Backups\latest.dump"
   ```
4. **Reconcile Offline Transactions:**
   If cashier terminals operated with cached drafts or paper receipts during the outage, enter those transactions manually with supervisor authorization.

---

## 4. DR-SCN-03: Outbox Backlog & Background Worker Stall

Edge Retails employs a transactional outbox (`system.outbox_messages`) to decouple database transactions from external side effects (physical ESC/POS printing, cash drawer opening, and audit streaming).

### Outbox Status Codes:
- `1` = `Pending` (Ready for worker execution)
- `2` = `Processing` (Locked by active worker)
- `3` = `Completed` (Successfully executed)
- `4` = `Failed` (Max retries exhausted; hard failure)
- `5` = `ActionRequired` (Requires manual supervisor intervention)

### Step-by-Step Remediation:

#### Step 1: Diagnose Outbox Health
```sql
SELECT 
    status, 
    effect_type, 
    count(*), 
    min(occurred_at_utc) AS oldest_message,
    max(attempt_count) AS max_attempts
FROM system.outbox_messages
WHERE status IN (1, 2, 4, 5)
GROUP BY status, effect_type;
```

#### Step 2: Release Stale Worker Locks
If `EdgeRetails.Worker` crashed while processing messages, records will remain stuck in `Processing` (`status = 2`):
```sql
-- Reset stale in-flight messages whose locks expired
UPDATE system.outbox_messages
SET status = 1,
    locked_until = NULL,
    locked_by = NULL
WHERE status = 2 
  AND (locked_until IS NULL OR locked_until < NOW() - INTERVAL '5 minutes');
```

#### Step 3: Clear or Retry `Failed` / `ActionRequired` Jobs
Inspect the `last_error` column:
```sql
SELECT id, effect_type, last_error, attempt_count 
FROM system.outbox_messages 
WHERE status IN (4, 5);
```
- If the printer was offline or paper was out: fix the physical printer, then re-queue:
  ```sql
  UPDATE system.outbox_messages
  SET status = 1,
      attempt_count = 0,
      next_attempt_at_utc = NOW()
  WHERE status IN (4, 5) AND effect_type = 'PrintDocument';
  ```

---

## 5. DR-SCN-04: Corrupted Print Queue & `OUTCOME_UNKNOWN` Handling

> [!CAUTION]
> When a thermal receipt printer loses connection or suffers a paper jam midway through transmitting an ESC/POS job, the system records `OUTCOME_UNKNOWN` (`print.outcome_unknown`).
> **NEVER blindly re-execute or cancel the transaction!** Physical paper may have already partially or fully emerged from the receipt cutter.

```
+---------------------------------------------------------------------------------------------------+
| OUTCOME_UNKNOWN RESOLUTION FLOWCHART                                                              |
|                                                                                                   |
|                      [Print Error: OUTCOME_UNKNOWN]                                               |
|                                     |                                                             |
|                    Cashier Visual Paper Inspection                                                |
|                                     |                                                             |
|             +-----------------------+-----------------------+                                     |
|             |                                               |                                     |
|    [Paper Printed Completely]                       [Paper Missing / Jammed]                      |
|             |                                               |                                     |
|   1. Cashier hands receipt to customer            1. Clear physical jam / replace paper roll      |
|   2. Click "Confirm Printed" in POS UI            2. Click "Reprint Receipt" in POS UI            |
|   3. System marks Outbox status = 3               3. System re-transmits SAME receipt #           |
|      (Completed)                                     (Zero duplicate inventory or ledger effects) |
+---------------------------------------------------------------------------------------------------+
```

### Invariant Rules:
1. **Zero Double-Deduction:** The sale transaction is already committed to PostgreSQL. Re-printing uses the identical `SaleId` and `InvoiceNumber`. Inventory is never deducted twice.
2. **Re-print Audit Logging:** Every re-print event appends a `DOCUMENT_REPRINTED` audit record to `identity.audit_logs`.

---

## 6. DR-SCN-05: Maintenance Mode Barrier Stuck (`RecoveryRequired`)

If the cross-process maintenance barrier remains stuck in `RecoveryRequired` after an interrupted backup or restore cutover:

### Symptoms:
- Any cashier mutation returns HTTP 503 / `system.maintenance_mode` or throws `ProductionMaintenanceException`.
- System Diagnostics displays `RecoveryRequired`.

### Remediation:
1. Check if the lock file is orphaned:
   `%LOCALAPPDATA%\EdgeRetails\Production\production-maintenance.lock`
2. Check the contents of `%LOCALAPPDATA%\EdgeRetails\Production\production-maintenance.state.json`.
3. If database cutover completed and PostgreSQL is healthy (`SELECT 1`), reset the barrier:
   ```powershell
   dotnet run --project "C:\Program Files\Edge Retails\EdgeRetails.Worker.exe" -- maintenance reset-barrier
   ```
4. Verify that the barrier state returns to `Normal`.
