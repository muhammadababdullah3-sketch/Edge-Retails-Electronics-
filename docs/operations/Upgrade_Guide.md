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
|     - Capture and verify mandatory backup from the same operational database                      |
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
|     - Start and verify EdgeRetailsServer readiness                                                 |
|     - Start EdgeRetailsWorker, then execute installed Desktop smoke                                |
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
Execute an immediate on-demand backup using the approved Edge Retails backup workflow or a PostgreSQL 18 custom-format snapshot. Resolve the target from the same approved runtime configuration used by the installed Server; do not substitute a hard-coded database, role, or plaintext credential. Record the target identity without recording the password. For a custom-format snapshot, require a nonempty file, `pg_restore --list` success, a full archive read, SHA-256, and migration-history content before any schema mutation. Keep Server and Worker stopped during the upgrade. See `Backup_Restore_Runbook.md` for the authenticated application backup lifecycle.

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
Apply only approved forward EF Core migrations against the PostgreSQL 18 database used by the installed Server. First compare the source infrastructure DLL hash with the installed Server's `EdgeRetails.Infrastructure.dll`, inspect `dotnet ef migrations list` and direct `system.__ef_migrations_history`, and rehearse the same pending chain against a disposable PostgreSQL 18 database containing representative older business data. Keep both application services stopped. The design-time context factory reads the approved ProgramData runtime configuration when `EDGE_RETAILS_DB` is absent; do not replace a working runtime authority with a new connection string.

```powershell
$infraProject = '.\src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj'

# Read-only census, then apply the specifically approved final migration ID.
dotnet ef migrations list --project $infraProject --startup-project $infraProject --context EdgeRetailsDbContext --configuration Release --no-build
dotnet ef database update <ApprovedFinalMigrationId> --project $infraProject --startup-project $infraProject --context EdgeRetailsDbContext --configuration Release --no-build
```

For the 1.0.3 legacy-category upgrade at the exact 13-migration baseline, the released `20260925142150_Phase1SemanticProductIdentity` remains immutable. Its unique index cannot be created while two existing categories have the same empty default. The governed forward recovery uses `scripts/Invoke-Phase3LegacyCategoryPreMigration.ps1` and append-only `20260929100000_Phase3LegacyCategoryUpgradeRecovery`. This special procedure is valid only after the isolated PostgreSQL 18 backup/restore and upgrade rehearsals have passed. A database with category-dependent products or stocktakes fails the guard and needs a separate reviewed plan.

Run the pre-step first without `-Apply` using the new, verified same-database custom backup path and its SHA-256. Review the emitted nonsecret target fingerprint and require the expected 13 history IDs, two categories, and stopped/disabled Server and Worker. Then run it from an elevated shell with `-Apply -ExpectedTargetFingerprint <reviewed fingerprint>`. The pre-step atomically stages the two category rows and leaves both application services disabled. Apply the EF chain explicitly through the approved ProgramData authority, targeting `20260929100000_Phase3LegacyCategoryUpgradeRecovery`, then verify all 18 history IDs, restored category IDs/names/active states, valid unique identity symbols, absence of the staging table, and no pending migrations. If the pre-step or EF update fails or returns an ambiguous result, keep both services disabled and inspect hold/history before any retry. Do not blindly rerun the pre-step or restore over the live database.

`ProductionStartupCoordinator` checks schema compatibility and blocks startup when migrations are pending. Starting Desktop or Server does not apply them. If the EF update fails, stop, verify transactional rollback and migration history, and follow the approved correction path before retrying.

### 3.3 Verify Database Compatibility
Verify database history and source-model alignment as separate checks. `has-pending-model-changes` checks whether the source model differs from its migration snapshot; it does not prove that the production database has applied all migrations.
```powershell
dotnet ef migrations list --project $infraProject --startup-project $infraProject --context EdgeRetailsDbContext --configuration Release --no-build
dotnet ef migrations has-pending-model-changes --project $infraProject --startup-project $infraProject --context EdgeRetailsDbContext --configuration Release --no-build
```

Require no `(Pending)` migrations, matching `system.__ef_migrations_history`, and zero pending model changes before service resumption.

---

## 4. Post-Upgrade Verification & Service Resumption

### Step 1: Restart and Verify Services
```powershell
Set-Service -Name EdgeRetailsServer -StartupType Automatic
Start-Service -Name EdgeRetailsServer
.\scripts\Test-EdgeRetailsServices.ps1
Set-Service -Name EdgeRetailsWorker -StartupType Automatic
Start-Service -Name EdgeRetailsWorker
(Get-Service -Name EdgeRetailsWorker).WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(30))
$worker = Get-CimInstance Win32_Service -Filter "Name='EdgeRetailsWorker'"
if ($worker.State -ne 'Running' -or $worker.StartMode -ne 'Auto') { throw 'Worker did not reach Running/Auto.' }
sc.exe qfailure EdgeRetailsWorker
```

Require HTTP 200 from `http://127.0.0.1:7150/api/system/ready` and a loopback-only listener before starting Worker or opening Desktop. Run the service commands from an elevated shell. Inspect `sc.exe qfailure` and require the approved Worker restart actions before completing service resumption.

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
4. **Restore Pre-Upgrade Database Snapshot:** Follow `Backup_Restore_Runbook.md` for staged restore, validation, and cutover. Do not run an in-place `pg_restore --clean` against the operational database. A transactionally rolled-back migration needs verification, not an automatic restore.
5. **Restart Services:** After the restored schema is confirmed compatible with the reinstalled binary, start Server first, require the deployment verification to pass, then start Worker:
   ```powershell
    Set-Service -Name EdgeRetailsServer -StartupType Automatic
    Start-Service -Name EdgeRetailsServer
    .\scripts\Test-EdgeRetailsServices.ps1
    Set-Service -Name EdgeRetailsWorker -StartupType Automatic
    Start-Service -Name EdgeRetailsWorker
    (Get-Service -Name EdgeRetailsWorker).WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(30))
    sc.exe qfailure EdgeRetailsWorker
   ```
6. **Verify System Integrity:** Confirm login succeeds and cashiers can resume transactions.
