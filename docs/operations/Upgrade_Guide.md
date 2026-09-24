# Edge Retails — Production Upgrade & Version Migration Guide

**Document Identifier:** `ER-OPS-UPG-01`  
**Phase:** Phase 6 — Final Certification & Long-Term Maintenance  
**Canonical Architecture SHA-256:** `12344760C60124DDC2D1C0E54ABFB7BC0E82B9B23A46E6463303EBFA530AB673`  
**Upgrade Mechanism:** WiX v4 MajorUpgrade (`Schedule="afterInstallInitialize"`)  
**Scope:** Application Binaries, EF Core Schema Migrations, Worker & Server Services

---

## 1. Upgrade Architecture & MajorUpgrade Principles

Edge Retails implements deterministic, transactional version upgrades:
1. **Binary Independence:** Application executables and libraries reside in `C:\Program Files\Edge Retails`. During an upgrade, the Windows Installer replaces binaries atomically.
2. **Data Preservation Guarantee:** Application data, license files, offline caches, local settings, and PostgreSQL databases are never deleted, moved, or altered by the installer or uninstaller.
3. **Downgrade Block:** Downgrades to older versions are blocked by default (`DowngradeErrorMessage="A newer version of Edge Retails is already installed."`) to protect against schema incompatibility.
4. **Fail-Closed Database Compatibility:** The new binary will not permit user login if the database schema is behind or incompatible.

```
+---------------------------------------------------------------------------------------------------+
| PRODUCTION UPGRADE SEQUENCE                                                                       |
|                                                                                                   |
|  [1. PRE-UPGRADE]                                                                                 |
|     - Check System Health: Verify 11 Diagnostic Probes report HEALTHY                             |
|     - Drain Outbox Backlog: Ensure 0 pending print/side-effect jobs                               |
|     - Stop Background Services: EdgeRetailsWorker, EdgeRetailsServer                              |
|     - Capture Mandatory Backup Snapshot: Generate .erbak and authenticated manifest               |
|                                                                                                   |
|  [2. BINARY INSTALLATION]                                                                         |
|     - Execute EdgeRetailsSetup.exe /quiet (Replaces files in Program Files)                       |
|     - Validate File Hashes against SHA256SUMS.txt                                                 |
|                                                                                                   |
|  [3. DATABASE SCHEMA UPDATE]                                                                      |
|     - Apply Pending EF Core Migrations (Forward Only)                                             |
|     - Verify EfMigrationCompatibilityProbe reports Compatible                                     |
|                                                                                                   |
|  [4. RESTART & VERIFICATION]                                                                      |
|     - Start EdgeRetailsWorker and EdgeRetailsServer                                               |
|     - Execute SmokeTest-ReleaseExe.ps1                                                            |
|     - Re-open Terminals & Release Maintenance Barrier                                             |
+---------------------------------------------------------------------------------------------------+
```

---

## 2. Pre-Upgrade Preparation Checklist

Before initiating any production upgrade, execute the following pre-flight checks:

### Step 1: Health & Queue Drainage Inspection
Query the PostgreSQL database to ensure all outbox events are processed:
```sql
SELECT 
    count(*) FILTER (WHERE status IN (1, 2)) AS pending_jobs,
    count(*) FILTER (WHERE status = 5) AS action_required_jobs
FROM system.outbox_messages;
```
*Requirement:* `pending_jobs` must be `0`. If greater than 0, allow `EdgeRetails.Worker` 60 seconds to finish printing and dispatching.

### Step 2: Stop Operational Background Services
Stop the worker and server processes so files are not locked during binary replacement:
```powershell
net stop EdgeRetailsWorker
net stop EdgeRetailsServer
```

### Step 3: Mandatory Pre-Upgrade Database Snapshot
Execute an immediate on-demand backup. This snapshot is your primary recovery mechanism in case of power loss or unrecoverable environmental failure during migration:
```powershell
$backupDir = "$env:LOCALAPPDATA\EdgeRetails\Production\backups"
& "C:\Program Files\PostgreSQL\18\bin\pg_dump.exe" `
    --format=custom --no-password `
    --host localhost --port 5432 --username postgres `
    --file "$backupDir\pre_upgrade_snapshot_$((Get-Date).ToString('yyyyMMdd_HHmmss')).dump" `
    edgeretails_prod
```

---

## 3. Step-by-Step Upgrade Execution

### 3.1 Binary Upgrade Execution
Run the new version of `EdgeRetailsSetup.exe` in silent mode or through the graphical installer:

```powershell
$installerPath = ".\artifacts\release\setup\EdgeRetailsSetup.exe"

# Execute upgrade
$process = Start-Process -FilePath $installerPath -ArgumentList "/quiet /norestart" -PassThru -Wait

if ($process.ExitCode -ne 0) {
    throw "Installer failed with exit code $($process.ExitCode)."
}
Write-Host "Binaries updated successfully." -ForegroundColor Green
```

### 3.2 Apply Database Schema Migrations
Apply the compiled EF Core migrations against the PostgreSQL 18 production database:

```powershell
$env:EDGE_RETAILS_DB = "Host=localhost;Port=5432;Database=edgeretails_prod;Username=edgeretails_user;Password=$SecureProductionPassword;"

# Apply forward migrations
dotnet ef database update `
    --project "C:\Program Files\Edge Retails\EdgeRetails.Infrastructure.dll" `
    --startup-project "C:\Program Files\Edge Retails\EdgeRetails.Infrastructure.dll"
```
*(Alternatively, starting `EdgeRetails.Desktop.exe` or `EdgeRetails.Server.exe` will automatically invoke `ProductionStartupCoordinator` to apply approved forward migrations under transactional lock).*

### 3.3 Verify Database Compatibility
Verify that the database state is `Compatible`:
```powershell
# Verify migration alignment via EF Core CLI
dotnet ef migrations has-pending-model-changes `
    --project "C:\Program Files\Edge Retails\EdgeRetails.Infrastructure.dll" `
    --startup-project "C:\Program Files\Edge Retails\EdgeRetails.Infrastructure.dll" `
    --no-build
```

---

## 4. Post-Upgrade Verification & Service Resumption

### Step 1: Restart Background Services
```powershell
net start EdgeRetailsWorker
net start EdgeRetailsServer
```

### Step 2: Automated Smoke Test
Execute the automated release executable smoke test to confirm window initialization, WPF UI readiness, and clean shutdown:
```powershell
& ".\scripts\SmokeTest-ReleaseExe.ps1" -ExePath "C:\Program Files\Edge Retails\EdgeRetails.Desktop.exe"
```

### Step 3: Diagnostic Snapshot Audit
Launch Edge Retails and review the System Diagnostics screen. Ensure all 11 diagnostic probes report:
- `db.latency.healthy`
- `db.write_safety.healthy`
- `schema.compatible`
- `disk.healthy`
- `worker.heartbeat.healthy`
- `backup.healthy`
- `print.backlog.healthy`

---

## 5. Rollback & Disaster Fallback Procedure

If the upgrade encounters critical failures (e.g., severe hardware incompatibility, third-party driver regression, or business operational block):

1. **Stop Services:**
   ```powershell
   net stop EdgeRetailsWorker
   net stop EdgeRetailsServer
   ```
2. **Uninstall New Version:**
   ```powershell
   msiexec /x ".\artifacts\release\msi\EdgeRetailsSetup.msi" /qn
   ```
3. **Reinstall Previous Stable Version:**
   ```powershell
   msiexec /i ".\previous_release\EdgeRetailsSetup.msi" /qn
   ```
4. **Restore Pre-Upgrade Database Snapshot:**
   ```powershell
   # Restore PostgreSQL database from the pre-upgrade snapshot
   & "C:\Program Files\PostgreSQL\18\bin\pg_restore.exe" `
       --clean --if-exists --no-password `
       --host localhost --port 5432 --username postgres `
       --dbname edgeretails_prod `
       "$backupDir\pre_upgrade_snapshot_<timestamp>.dump"
   ```
5. **Restart Services:**
   ```powershell
   net start EdgeRetailsWorker
   net start EdgeRetailsServer
   ```
6. **Verify System Integrity:** Confirm login succeeds and cashiers can resume transactions.
