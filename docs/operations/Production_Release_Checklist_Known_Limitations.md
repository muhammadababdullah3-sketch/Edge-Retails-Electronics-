# Edge Retails — Production Release Checklist & Known Operational Limitations

**Document Identifier:** `ER-OPS-REL-01`  
**Phase:** Phase 6 — Final Certification & Long-Term Maintenance  
**Canonical Architecture SHA-256:** `12344760C60124DDC2D1C0E54ABFB7BC0E82B9B23A46E6463303EBFA530AB673`  
**Certification Standard:** 9 Automated Fail-Closed Gates (`Invoke-Phase6FinalCertification.ps1`)

---

## 1. Automated Pre-Flight Release Checklist

Before stamping any release candidate for production deployment, the entire automated certification suite must be executed and achieve a **100% PASS** verdict across all 9 gates.

```powershell
# Execute the Master Phase 6 Certification Suite
& ".\scripts\Invoke-Phase6FinalCertification.ps1" -Solution "EdgeRetails.sln" -Configuration "Release"
```

### The 9 Mandatory Pre-Flight Release Gates:

| Gate | Verification Target | Enforced Standard / Threshold | Diagnostic Evidence | Status |
| :---: | :--- | :--- | :--- | :---: |
| **01** | **Architecture Authority** | Matches Canonical SHA-256: `12344760C60124DDC2D1C0E54ABFB7BC0E82B9B23A46E6463303EBFA530AB673` | Output of `Verify-ArchitectureInternationalAuditRemediation.ps1` | **PASS** |
| **02** | **Release Compilation** | Clean release build with `-warnaserror` across all 10 projects | 0 compilation warnings, 0 compilation errors | **PASS** |
| **03** | **Full Unit Test Suite** | 100% passing across domain, application, and infrastructure | 456 / 456 tests passed, 0 failures | **PASS** |
| **04** | **Desktop Performance Tests**| WPF STA UI benchmark suite | 3 / 3 STA UI tests passed | **PASS** |
| **05** | **EF Core Model Drift** | Entity model exactly matches migration snapshot | `dotnet ef migrations has-pending-model-changes` -> 0 drift | **PASS** |
| **06** | **Installer Preservation Audit**| Static audit of WiX `.wxs` and `.wixproj` sources | Zero `DROP DATABASE`, zero data deletion actions | **PASS** |
| **07** | **Database Compatibility Matrix**| Compatibility matrix documented and verified | `docs/Phase6_Database_Compatibility_Matrix.md` verified | **PASS** |
| **08** | **Operations Runbook Package** | Operations runbook index verified | `docs/Phase6_Operations_Package_Index.md` verified | **PASS** |
| **09** | **Agent Handoff Verification** | All Phase 6 agent handoffs verified | `docs/Phase6_Agent_Handoffs/Agent*_Handoff.md` present | **PASS** |

---

## 2. Verified Operational Thresholds & Production SLAs

These thresholds represent the verified operational boundaries certified in Phase 5 and Phase 6 performance testing:

| Metric / Health Dimension | Verified Production Baseline | Configured Warning Threshold | Configured Critical / Failure Threshold |
| :--- | :---: | :---: | :---: |
| **PostgreSQL Roundtrip Latency** | **2.2 ms** | `> 500.0 ms` (`db.latency.degraded`) | `> 5000.0 ms` (`db.unavailable`) |
| **Local Disk Headroom** | **65.4 GiB free** | `< 10.0 GiB` (`disk.degraded`) | `< 1.0 GiB` (`CriticalDiskFloorException`) |
| **Worker Process Heartbeat** | **Every 30 seconds** | `> 2.0 minutes` (`worker.heartbeat.action_required`)| Service Stopped (`UNAVAILABLE`) |
| **Local Backup Maximum Age** | **Nightly (24 hrs)** | `> 24.0 hours` (`backup.age.action_required`) | Missing manifest (`backup.unavailable`) |
| **Pending Print Outbox Depth** | **0 pending** | `> 50 jobs` (`print.backlog.action_required`) | Unbounded queue |
| **OUTCOME_UNKNOWN Print Jobs** | **0 jobs** | `> 1 job` (`outcome_unknown.action_required`) | Cashier triage required |
| **Dead-Letter Action Required Jobs**| **0 jobs** | `> 1 job` (`action_required_backlog.action_required`) | Supervisor triage required |
| **Failed Background Jobs** | **0 jobs** | `> 1 job` (`failed_jobs.action_required`) | Supervisor triage required |

---

## 3. Supported Hardware & Operating System Matrix

### 3.1 Certified Operating Systems
- **Windows 11 Pro / Enterprise:** Versions 22H2, 23H2, 24H2 (x64 and ARM64).
- **Windows 10 Pro / Enterprise:** Version 21H2, 22H2 (x64).
- **Windows Server:** Windows Server 2022, Windows Server 2025.
- *Unsupported:* Windows 7, Windows 8.1, Windows 10 Home (due to IIS/service policy restrictions).

### 3.2 Database Engine Support
- **PostgreSQL 18.x:** Authoritative production engine. Port 5432, UTF-8, SCRAM-SHA-256.
- *Minimum Client Tools Version:* PostgreSQL 18.0 client binaries (`pg_dump`, `pg_restore`, `psql`, `createdb`).

### 3.3 Certified POS Peripherals
- **Thermal Receipt Printers:**
  - Standard 80mm ESC/POS thermal printers (Epson TM-T88VI, TM-T20III, Star Micronics TSP100, Bixolon SRP-350, generic ESC/POS USB/LAN).
  - Standard 58mm ESC/POS compact printers.
  - Connection methods: USB virtual COM, Direct TCP/IP (port 9100 Raw), Windows Spooler.
- **Barcode & QR Code Scanners:**
  - 1D/2D handheld and omnidirectional presentation scanners (Honeywell, Zebra, Datalogic).
  - Mode: USB HID POS or Keyboard Wedge with carriage return suffix (`\r` / `\n`).
- **Cash Drawers:**
  - Standard 12V/24V cash drawers connected via RJ11 / RJ12 connector to the thermal receipt printer kick port.
  - Triggered transactionally via outbox command before or during receipt printing.

---

## 4. Known Operational Limitations & Boundaries

1. **Fail-Closed Offline Mutation Boundary:**
   - Standalone POS stations possess their own local PostgreSQL database and function fully offline.
   - Satellite LAN Terminals communicate exclusively over HTTPS with `EdgeRetails.Server`. When the local network connection drops, satellite terminals **immediately fail closed** (`network.offline_mutation_forbidden`). They do **not** write uncommitted shadow sales to local disk, preventing inventory divergence and ledger collisions upon reconnection.
2. **Single-Primary PostgreSQL Database:**
   - Edge Retails operates against a single read-write PostgreSQL primary. Replicas and standby nodes operating in recovery mode reject mutations (`db.write_safety.unavailable`).
3. **Remote Cloud Backup Verification:**
   - Out-of-the-box local deployments default to `backup.remote.unverified` (Classification: `UNAVAILABLE`) until an authorized cloud storage adapter is configured. This fail-closed design prevents false-green reporting.
4. **Prohibited Binary Downgrades:**
   - Downgrading an existing installation to an older version via the MSI installer is blocked by design (`Schedule="afterInstallInitialize"`). Downgrading requires full database restoration from an earlier backup snapshot.
5. **Physical Paper Reconciliation on `OUTCOME_UNKNOWN`:**
   - If a thermal printer suffers an ambiguous print failure (e.g. communication drop while cutting paper), operator visual inspection is required. The system will not automatically re-print to protect against duplicate physical receipts.
