# Edge Retails — Phase 6 Operations & Maintenance Package Index

**Document Version:** 1.0.0  
**Phase:** Phase 6 — Final Certification & Long-Term Maintenance  
**Workspace:** `C:\Users\muham\OneDrive\Desktop\Point of Sale`  
**Canonical Architecture SHA-256:** `12344760C60124DDC2D1C0E54ABFB7BC0E82B9B23A46E6463303EBFA530AB673`  
**Database Engine:** PostgreSQL 18.x  
**Operating Systems:** Windows 10/11 Pro (x64/ARM64), Windows Server 2022/2025  

---

## 1. Executive Master Index & Document Registry

This document serves as the authoritative operational root and navigation index for all Phase 6 production runbooks, deployment procedures, recovery protocols, and support catalogs.

```
+---------------------------------------------------------------------------------------------------+
| EDGE RETAILS PHASE 6 OPERATIONS & MAINTENANCE PACKAGE                                            |
+---------------------------------------------------------------------------------------------------+
|                                                                                                   |
|  1. DATABASE MIGRATION MAP & SCHEMA LIFECYCLE GOVERNANCE                                          |
|     Location: docs/operations/Database_Migration_Map.md                                           |
|     ID: ER-OPS-DB-MIG-01                                                                          |
|     Scope: 13 EF Core migrations, 63 physical tables across 8 schemas, compatibility state        |
|            machine, zero-model-drift rule, and production rollback policy.                        |
|                                                                                                   |
|  2. BACKUP & RESTORE OPERATIONS RUNBOOK                                                           |
|     Location: docs/operations/Backup_Restore_Runbook.md                                           |
|     ID: ER-OPS-BCK-01                                                                             |
|     Scope: Scheduled backups, AES-256-GCM encryption, HMAC-SHA256 manifest authentication,       |
|            two-phase cutover restore, OID-based rollback, and ledger reconciliation.              |
|                                                                                                   |
|  3. PRODUCTION INSTALLER & DEPLOYMENT GUIDE                                                       |
|     Location: docs/operations/Installer_Deployment_Guide.md                                        |
|     ID: ER-OPS-INS-01                                                                             |
|     Scope: System prerequisites, PostgreSQL 18 setup, WiX v4 Bootstrapper and MSI flags,          |
|            silent enterprise install, background service registration, and initial onboarding.    |
|                                                                                                   |
|  4. PRODUCTION UPGRADE & VERSION MIGRATION GUIDE                                                  |
|     Location: docs/operations/Upgrade_Guide.md                                                    |
|     ID: ER-OPS-UPG-01                                                                             |
|     Scope: WiX v4 MajorUpgrade semantics, pre-upgrade health check and outbox drainage,          |
|            binary updates, EF Core schema migration execution, smoke testing, and fallback.       |
|                                                                                                   |
|  5. DISASTER RECOVERY & OUTAGE RUNBOOK                                                            |
|     Location: docs/operations/Disaster_Recovery_Runbook.md                                        |
|     ID: ER-OPS-DR-01                                                                              |
|     Scope: Runbooks for hardware loss, PostgreSQL corruption, outbox stalls, physical print jams, |
|            OUTCOME_UNKNOWN paper reconciliation, and maintenance barrier lock clearing.           |
|                                                                                                   |
|  6. SUPPORT DIAGNOSTICS & ERROR CODE CATALOG                                                      |
|     Location: docs/operations/Support_Diagnostics_Error_Catalog.md                                |
|     ID: ER-OPS-DIAG-01                                                                            |
|     Scope: Catalog of 11 diagnostic families, deterministic health state machine, HTTP status     |
|            codes, terminal status machine, and redacted support bundle export.                    |
|                                                                                                   |
|  7. PRODUCTION SECURITY & KEY ROTATION GUIDE                                                      |
|     Location: docs/operations/Security_Key_Rotation_Guide.md                                      |
|     ID: ER-OPS-SEC-01                                                                             |
|     Scope: Separation of 4 cryptographic trust domains (Licensing RS256, Terminal HMAC,           |
|            Maintenance Barrier HMAC, Backup AES-GCM), key rotation runbooks, and Windows ACLs.    |
|                                                                                                   |
|  8. PRODUCTION RELEASE CHECKLIST & KNOWN LIMITATIONS                                              |
|     Location: docs/operations/Production_Release_Checklist_Known_Limitations.md                   |
|     ID: ER-OPS-REL-01                                                                             |
|     Scope: 9 automated pre-flight certification gates, operational SLAs, supported hardware       |
|            matrix (OS, printers, scanners, drawers), and architectural boundary limitations.      |
|                                                                                                   |
+---------------------------------------------------------------------------------------------------+
```

---

## 2. Document Cross-Reference & Operational Role Matrix

| Operational Role | Primary Reference Documents | Key Responsibilities & Actions |
| :--- | :--- | :--- |
| **Store Operations / Cashier Lead** | • [Support Diagnostics & Error Catalog](operations/Support_Diagnostics_Error_Catalog.md)<br>• [Disaster Recovery Runbook](operations/Disaster_Recovery_Runbook.md) | Resolving physical printer jams, handling `OUTCOME_UNKNOWN` paper reconciliation, reporting terminal offline indicators. |
| **System Administrator / IT Specialist** | • [Installer & Deployment Guide](operations/Installer_Deployment_Guide.md)<br>• [Production Upgrade Guide](operations/Upgrade_Guide.md)<br>• [Security & Key Rotation Guide](operations/Security_Key_Rotation_Guide.md) | Unattended MSI installations, PostgreSQL 18 service setup, terminal credential management, semi-annual key rotations, Windows ACL enforcement. |
| **Database Administrator (DBA)** | • [Database Migration Map](operations/Database_Migration_Map.md)<br>• [Backup & Restore Runbook](operations/Backup_Restore_Runbook.md) | Executing forward EF Core migrations, managing daily backup retention, staging and cutover restore drills, PostgreSQL tuning. |
| **Release Engineer & Tier-3 Support** | • [Production Release Checklist](operations/Production_Release_Checklist_Known_Limitations.md)<br>• [Support Diagnostics & Error Catalog](operations/Support_Diagnostics_Error_Catalog.md) | Running 9 automated release gates (`Invoke-Phase6FinalCertification.ps1`), triaging dead-letter outbox jobs (`status = 5`), analyzing redacted support bundles. |

---

## 3. Emergency Support Escalation Path

```
Level 1: Cashier Station / Terminal Local Triage
   │  - Visual inspection of printer paper, power cables, and network link
   │  - Restart EdgeRetails.Desktop.exe (triggers local startup probe)
   │  - Reference: docs/operations/Support_Diagnostics_Error_Catalog.md
   ▼
Level 2: Shop IT Administrator (Local LAN & Server)
   │  - Inspect PostgreSQL 18 service status: Get-Service postgresql-x64-18
   │  - Inspect Worker service status: Get-Service EdgeRetailsWorker
   │  - Check Outbox backlog depth: SELECT count(*) FROM system.outbox_messages WHERE status IN (1,2);
   │  - Reference: docs/operations/Disaster_Recovery_Runbook.md
   ▼
Level 3: Central Operations / Engineering Escalation
   │  - Generate Redacted Support Bundle: dotnet run -- export-diagnostics
   │  - Execute two-phase cutover restore if data corruption is suspected
   │  - Reset maintenance barrier if stuck in RecoveryRequired
   │  - Reference: docs/operations/Backup_Restore_Runbook.md & Security_Key_Rotation_Guide.md
```

---

## 4. Operational Quick-Command Cheat Sheet

### 1. Database Readiness & Status
```powershell
# Test PostgreSQL client toolchain readiness
& ".\scripts\Test-PostgresClientReadiness.ps1" -PgBin "C:\Program Files\PostgreSQL\18\bin"

# Test direct SQL connection and primary read-write status
& "C:\Program Files\PostgreSQL\18\bin\psql.exe" -U postgres -d edgeretails_prod -c "SELECT NOT pg_is_in_recovery() AS is_primary, current_setting('server_version');"
```

### 2. Immediate Backup Snapshot
```powershell
# Create an on-demand encrypted backup with authenticated companion manifest
dotnet run --project "src/EdgeRetails.Worker" -- backup
```

### 3. Verify System Diagnostics
```powershell
# Capture production health snapshot (evaluates all 11 health families)
dotnet run --project "src/EdgeRetails.Desktop" -- capture-diagnostics
```

### 4. Outbox Health Census
```sql
-- Query active background jobs and failures
SELECT status, effect_type, count(*), min(occurred_at_utc) 
FROM system.outbox_messages 
GROUP BY status, effect_type;
```

### 5. Automated Certification Execution
```powershell
# Master Phase 6 Fail-Closed Certification Script
& ".\scripts\Invoke-Phase6FinalCertification.ps1"
```
