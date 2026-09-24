# Edge Retails — Production Installer & Deployment Guide

**Document Identifier:** `ER-OPS-INS-01`  
**Phase:** Phase 6 — Final Certification & Long-Term Maintenance  
**Canonical Architecture SHA-256:** `12344760C60124DDC2D1C0E54ABFB7BC0E82B9B23A46E6463303EBFA530AB673`  
**Deployment Model:** Standalone POS Station / Local LAN Shop Server & Satellite Terminals  
**Installer Technologies:** WiX Toolset v4 MSI (`EdgeRetails.Setup`) & WiX Burn Bootstrapper (`EdgeRetails.Bootstrapper`)

---

## 1. System Requirements & Prerequisites

### 1.1 Operating System & Hardware Matrix
- **Operating Systems Supported:**
  - Windows 11 Pro / Enterprise (x64 / ARM64, Build 22H2 or newer)
  - Windows 10 Pro / Enterprise (x64, Build 21H2 or newer)
  - Windows Server 2022 / Windows Server 2025 (x64)
- **Minimum Hardware Specifications:**
  - CPU: Quad-Core Intel / AMD x64 or ARM64 processor (2.0 GHz or higher)
  - RAM: 8 GB physical memory (16 GB recommended for LAN Shop Server)
  - Storage: Minimum 10 GiB available storage on the system drive for operational databases, backups, and diagnostic logging
  - Display: 1920x1080 resolution recommended (1366x768 minimum)
  - Network: Gigabit Ethernet or 5GHz Wi-Fi (for multi-terminal LAN server topology)

### 1.2 PostgreSQL 18.x Database Engine
- Edge Retails requires **PostgreSQL 18.x** running as a local or LAN service.
- The PostgreSQL client tools must be installed and accessible:
  - `pg_dump`
  - `pg_restore`
  - `psql`
  - `createdb`
- **Default Port:** `5432` (TCP)
- **Authentication Scheme:** `scram-sha-256`

---

## 2. Directory Separation & Data Preservation Principle

To ensure total data protection across upgrades, reinstallations, and uninstalls, Edge Retails strictly isolates binaries from data:

```
+---------------------------------------------------------------------------------------------------+
| DIRECTORY PARTITIONING ARCHITECTURE                                                               |
+---------------------------------------------------------------------------------------------------+
|  1. APPLICATION BINARIES (MSI-OWNED)                                                              |
|     C:\Program Files\Edge Retails\                                                                |
|     - Overwritten during version upgrades.                                                        |
|     - Removed completely on uninstall.                                                            |
|     - Contains ZERO database files, user data, licenses, or logs.                                 |
+---------------------------------------------------------------------------------------------------+
|  2. RUNTIME STATE & DIAGNOSTICS (OS/USER OWNED - PRESERVED)                                       |
|     C:\Users\<User>\AppData\Local\EdgeRetails\Production\                                         |
|     - backups\ (encrypted *.erbak and *.manifest.json)                                            |
|     - logs\ (rolling application event logs)                                                      |
|     - production-maintenance.state.json (HMAC barrier state)                                      |
|     - NEVER touched or removed by installer or uninstaller.                                       |
+---------------------------------------------------------------------------------------------------+
|  3. POSTGRESQL CLUSTERS & DATA DIRECTORIES (SERVICE OWNED - PRESERVED)                            |
|     C:\Program Files\PostgreSQL\18\data\                                                          |
|     - Authoritative ACID financial ledger and catalog data.                                       |
|     - Subject to strict static installer verification (Verify-InstallerDataPreservation.ps1).     |
|     - MSI package contains ZERO DROP DATABASE or data removal actions.                            |
+---------------------------------------------------------------------------------------------------+
```

---

## 3. PostgreSQL 18 Server Setup & Provisioning

Before launching the Edge Retails installer, provision the PostgreSQL 18 database instance:

### Step 1: Initialize Database Cluster (if not existing)
```powershell
# Open elevated PowerShell prompt
& "C:\Program Files\PostgreSQL\18\bin\initdb.exe" `
    -D "C:\Program Files\PostgreSQL\18\data" `
    -E UTF8 `
    --auth-local=scram-sha-256 `
    --auth-host=scram-sha-256
```

### Step 2: Create Edge Retails Service User & Database
Connect via `psql` as the PostgreSQL administrator:
```powershell
& "C:\Program Files\PostgreSQL\18\bin\psql.exe" -U postgres -p 5432
```

Execute SQL provisioning commands:
```sql
-- Create dedicated operational role (replace <GENERATE_STRONG_RANDOM_PASSWORD> with an authoritatively generated random password; never use default passwords)
CREATE USER edgeretails_user WITH PASSWORD '<GENERATE_STRONG_RANDOM_PASSWORD>';

-- Create production database owned by edgeretails_user
CREATE DATABASE edgeretails_prod OWNER edgeretails_user;

-- Grant required permissions
GRANT ALL PRIVILEGES ON DATABASE edgeretails_prod TO edgeretails_user;

-- Connect to production database and configure schema permissions
\c edgeretails_prod
GRANT ALL ON SCHEMA public TO edgeretails_user;
```

---

## 4. Installer Deployment Procedures

Edge Retails provides two deployment artifacts in `artifacts/release/`:
1. `EdgeRetailsSetup.exe` — WiX Burn Bootstrapper (recommended for all interactive and automated deployments).
2. `EdgeRetailsSetup.msi` — Direct Windows Installer package (for enterprise Group Policy / SCCM / Intune deployments).

### 4.1 Interactive GUI Installation
1. Double-click `EdgeRetailsSetup.exe`.
2. Review the license agreement and verify target folder (`C:\Program Files\Edge Retails`).
3. Click **Install**.
4. User Account Control (UAC) will prompt for elevation.
5. Setup extracts binaries, registers file associations, and creates desktop/start-menu shortcuts.

### 4.2 Silent / Unattended Installation (Enterprise Automation)

#### Via WiX Bootstrapper (`EdgeRetailsSetup.exe`):
```powershell
# Silent installation without reboot
Start-Process -FilePath ".\artifacts\release\setup\EdgeRetailsSetup.exe" `
    -ArgumentList "/quiet /norestart /log `"$env:TEMP\EdgeRetailsSetup.log`"" `
    -Wait -NoNewWindow
```

#### Via Windows Installer (`msiexec.exe`):
```powershell
# Silent MSI installation with verbose logging
$msiPath = ".\artifacts\release\msi\EdgeRetailsSetup.msi"
$logPath = "$env:TEMP\EdgeRetailsMsiInstall.log"

Start-Process -FilePath "msiexec.exe" `
    -ArgumentList "/i `"$msiPath`" /qn /norestart /l*v `"$logPath`" INSTALLFOLDER=`"C:\Program Files\Edge Retails`"" `
    -Wait -NoNewWindow
```

#### Verification of Installation Success:
```powershell
# Verify exit code and installed executable presence
if (Test-Path "C:\Program Files\Edge Retails\EdgeRetails.Desktop.exe") {
    Write-Host "Edge Retails installed successfully." -ForegroundColor Green
} else {
    throw "Installation failed. Check log at $logPath."
}
```

---

## 5. Background Service Registration

### 5.1 Register Background Worker Service (`EdgeRetails.Worker`)
The background worker manages outbox execution, scheduled database backups, and printer queue dispatching.

```powershell
# Register Windows Service via sc.exe (Elevated Prompt)
sc.exe create EdgeRetailsWorker `
    binPath= "C:\Program Files\Edge Retails\EdgeRetails.Worker.exe" `
    start= auto `
    DisplayName= "Edge Retails Background Worker"

# Configure failure recovery (restart service on crash)
sc.exe failure EdgeRetailsWorker reset= 86400 actions= restart/60000/restart/60000/restart/60000

# Start the service
net start EdgeRetailsWorker
```

### 5.2 Register LAN Shop Server Service (`EdgeRetails.Server`)
For multi-terminal deployments where the host machine acts as the shop server:

```powershell
# Register LAN Server Service
sc.exe create EdgeRetailsServer `
    binPath= "C:\Program Files\Edge Retails\EdgeRetails.Server.exe" `
    start= auto `
    DisplayName= "Edge Retails LAN Shop Server"

# Configure firewall rule to allow terminal connections on port 7150
New-NetFirewallRule -DisplayName "Edge Retails LAN Server (HTTPS)" `
    -Direction Inbound -LocalPort 7150 -Protocol TCP -Action Allow

# Start the server service
net start EdgeRetailsServer
```

---

## 6. First-Run Initialization & Shop Onboarding

1. Launch `EdgeRetails.Desktop.exe` from the desktop shortcut.
2. **Database Auto-Readiness:** The application connects to PostgreSQL 18 and detects an empty database.
3. **Onboarding Wizard:**
   - **Shop Profile:** Input business legal name, NTN / Tax ID, address, phone number, and default currency symbol.
   - **Receipt Template:** Configure default header/footer text, print layout, and thermal receipt width (80mm / 58mm).
   - **Administrator Account:** Create initial master administrative user and secure PIN.
   - **License Activation:** Import the digitally signed `.erlic` license envelope (`RS256` signed by vendor).
4. **Diagnostic Verification:** The application captures the initial diagnostic snapshot. Verify all indicators report `HEALTHY`.
5. Edge Retails is now fully operational in certified production mode.
