# Edge Retails CENTRAL-ONE — Server Deployment Runbook
**Document ID:** `CONE-DOC-T29-DEPLOY-001`  
**Target Architecture:** ASP.NET Core (.NET 10) / Npgsql / PostgreSQL 18.6 Control Plane  
**Classification:** Core Operations / Reconstructable Runbook  
**Target Path:** `docs/central/SERVER_DEPLOYMENT_RUNBOOK.md`

---

## 1. System Architecture Overview

CENTRAL-ONE functions as the sovereign control plane, commercial authority, licensing custodian, and cryptographic trust root for the Edge Retails monorepo ecosystem. 

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                       CENTRAL-ONE HOST PROCESS (Kestrel)                   │
│                                                                             │
│  ┌───────────────────────┐             ┌─────────────────────────────────┐  │
│  │   Static Web Admin    │             │   ASP.NET Core Minimal/REST     │  │
│  │   Portal (wwwroot)    │             │   Controllers & Middleware      │  │
│  │   HTML5 / CSS / JS    │             │   Session / CSRF / Rate-Limit   │  │
│  └───────────┬───────────┘             └───────────────┬─────────────────┘  │
│              │                                         │                    │
│              └────────────────────┬────────────────────┘                    │
│                                   │                                         │
│                      ┌────────────┴─────────────┐                           │
│                      │   EdgeRetails.Central    │                           │
│                      │    Application Core      │                           │
│                      └────────────┬─────────────┘                           │
│                                   │                                         │
│         ┌─────────────────────────┼─────────────────────────┐               │
│         ▼                         ▼                         ▼               │
│  ┌──────────────┐         ┌──────────────┐         ┌─────────────────┐      │
│  │  Protected   │         │ Append-Only  │         │ Database Target │      │
│  │  Signer      │         │ Audit Engine │         │ Guard (Security)│      │
│  │  (In-Memory) │         │ Interceptor  │         │ Validation Hook │      │
│  └──────────────┘         └──────────────┘         └────────┬────────┘      │
└─────────────────────────────────────────────────────────────┼───────────────┘
                                                              ▼
                                             ┌─────────────────────────────────┐
                                             │  PostgreSQL 18.6 Storage Engine │
                                             │  Schema: `central`              │
                                             │  Non-default Loopback Port      │
                                             │  (e.g., 55454 / 55455)          │
                                             └─────────────────────────────────┘
```

### 1.1 Key Architecture Invariants
1. **Single-Primary V1 Commercial Governance:** Central strictly bounds each licensed shop to exactly one operational Primary workstation (`PrimaryCapacity = 1`). Multi-seat, secondary LAN nodes, or cloud retail transaction processing are prohibited in V1.
2. **Unified Host Model:** ASP.NET Core API directly hosts the Web Admin Portal as static single-page assets (`wwwroot/index.html`, `wwwroot/css/portal.css`, `wwwroot/js/portal.js`). No external node proxy or second web server process is required.
3. **Database Guard Isolation:** `DatabaseTargetGuard` executes fail-closed checks on every startup and connection initialization. It strictly prevents operational overlap, cross-contamination, or accidental connection to production POS databases.
4. **Append-Only Auditing:** EF Core interceptor `AppendOnlyAuditInterceptor` rejects SQL updates and deletes against the audit ledger at the infrastructure layer.

---

## 2. Infrastructure Prerequisites & Environment Setup

### 2.1 Software Requirements
* **Runtime/SDK:** .NET 10 SDK (`10.0.100` or higher). Verify with `dotnet --version`.
* **Database Engine:** PostgreSQL 18.6 (x64) with the `pgcrypto` extension installed.
* **Operating System:** Windows 11 / Windows Server 2022+ / Ubuntu Linux 24.04 LTS.
* **Network Binding:** Loopback isolated interface (`127.0.0.1`). Dedicated staging/disposable ports: `55454` (Primary Staging), `55455` (Rehearsal/DR).

### 2.2 PostgreSQL 18.6 Disposable Instance Initialization
To establish the isolated database cluster on non-default port `55454`:

```powershell
# Define paths
$pgBin = "C:\Program Files\PostgreSQL\18\bin"
$pgData = "C:\Users\muham\EdgeRetailsWorktrees\central-one\test-artifacts\w1-close-054\pgdata"
$pgLog = "$pgData\logfile.log"
$guardMarker = "C:\Users\muham\EdgeRetailsWorktrees\central-one\test-artifacts\w1-close-054\GUARD_MARKER.json"

# 1. Initialize DB Cluster if not initialized
if (-not (Test-Path "$pgData\PG_VERSION")) {
    & "$pgBin\initdb.exe" -D $pgData -U postgres -A trust --encoding=UTF8
}

# 2. Start PostgreSQL daemon on isolated port 55454
& "$pgBin\pg_ctl.exe" -D $pgData -l $pgLog -o "-p 55454 -h 127.0.0.1" start

# 3. Read active PID and update ownership marker
$activePid = (Get-Content "$pgData\postmaster.pid")[0].Trim()
$markerContent = @{
    cluster_type = "disposable_pg18"
    owner = "central_test_admin"
    pid = [int]$activePid
    port = 55454
    database = "central054_test"
    runtimeRole = "central054_app"
    migrationRole = "central054_owner"
    dataDirectory = $pgData
    createdAtUtc = (Get-Date).ToUniversalTime().ToString("o")
} | ConvertTo-Json -Depth 4
Set-Content -Path $guardMarker -Value $markerContent -Encoding UTF8
```

### 2.3 PostgreSQL Roles & Schema Provisioning
Execute the following bootstrap SQL script under administrative credentials via `psql`:

```sql
-- Connect via psql -h 127.0.0.1 -p 55454 -U postgres

-- 1. Create target database
CREATE DATABASE central054_test WITH ENCODING = 'UTF8';

\c central054_test

-- 2. Create required extensions
CREATE EXTENSION IF NOT EXISTS pgcrypto;

-- 3. Create Migration Role (DDL Authority)
CREATE ROLE central054_owner WITH LOGIN PASSWORD 'central054_owner';

-- 4. Create Runtime Application Role (Least-Privilege DML Authority)
CREATE ROLE central054_app WITH LOGIN PASSWORD 'central054_app';

-- 5. Establish central schema and grant ownership
CREATE SCHEMA central AUTHORIZATION central054_owner;

-- 6. Grant scoped permissions to runtime application role
GRANT USAGE ON SCHEMA central TO central054_app;
GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA central TO central054_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA central TO central054_app;

ALTER DEFAULT PRIVILEGES IN SCHEMA central 
    GRANT SELECT, INSERT, UPDATE ON TABLES TO central054_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA central 
    GRANT USAGE, SELECT ON SEQUENCES TO central054_app;

-- Runtime role strictly forbidden from DROP, TRUNCATE, and ALTERing audit trail
REVOKE TRUNCATE ON ALL TABLES IN SCHEMA central FROM central054_app;
```

---

## 3. Fail-Closed Target Guard Constraints

CENTRAL-ONE enforces fail-closed connection constraints through `DatabaseTargetGuard.cs`. If any rule is violated, application initialization crashes immediately with `DatabaseTargetGuardException`.

| Guard Criterion | Validated Value | Violation Action | Guard Exception Code |
|---|---|---|---|
| **Forbidden Ports** | Rejects `5432`, `7150` | Immediate abort | `FORBIDDEN_PORT_TARGET` |
| **Port Range** | Must be between `1024` and `65535` | Immediate abort | `INVALID_PORT_RANGE` |
| **Host Interface** | Must be `127.0.0.1`, `localhost`, `::1` | Immediate abort | `NON_LOOPBACK_TARGET` |
| **Forbidden Databases**| Rejects `edgeretails`, `edgeretails_pos`, `shop_pos`, `postgres`, `template1`, `edgeretails_dev` | Immediate abort | `FORBIDDEN_DATABASE_TARGET` |
| **Process Identity** | PID must match running `postgres.exe` (v18.x) | Immediate abort | `PROCESS_MISMATCH` / `ENGINE_MISMATCH` |
| **Marker Consistency**| `GUARD_MARKER.json` must match PID, Port, DB, Roles | Immediate abort | `OWNERSHIP_MARKER_MISMATCH` |
| **Postmaster Validation**| `postmaster.pid` must match active PID and Data Dir | Immediate abort | `POSTMASTER_MISMATCH` |

---

## 4. Environment Variables Configuration

Configure the following process-level environment variables prior to running migrations or starting the web host:

```powershell
# Target Database Configuration
$env:CENTRAL_PG_HOST = "127.0.0.1"
$env:CENTRAL_PG_PORT = "55454"
$env:CENTRAL_PG_DATABASE = "central054_test"
$env:CENTRAL_PG_ROLE = "central054_app"
$env:CENTRAL_PG_MIGRATION_ROLE = "central054_owner"
$env:CENTRAL_PG_DATA_DIR = "C:\Users\muham\EdgeRetailsWorktrees\central-one\test-artifacts\w1-close-054\pgdata"
$env:CENTRAL_PG_OWNERSHIP_MARKER = "C:\Users\muham\EdgeRetailsWorktrees\central-one\test-artifacts\w1-close-054\GUARD_MARKER.json"
$env:CENTRAL_PG_BINARY = "C:\Program Files\PostgreSQL\18\bin\postgres.exe"
$env:CENTRAL_PG_PID = (Get-Content "$env:CENTRAL_PG_DATA_DIR\postmaster.pid")[0].Trim()

# Connection Strings
$env:CENTRAL_DATABASE_CONNECTION_STRING = "Host=127.0.0.1;Port=55454;Database=central054_test;Username=central054_app;Password=central054_app;SearchPath=central"
$env:CENTRAL_MIGRATION_CONNECTION_STRING = "Host=127.0.0.1;Port=55454;Database=central054_test;Username=central054_owner;Password=central054_owner;SearchPath=central"

# Cryptographic and Security Settings
$env:CENTRAL_INTERNAL_SIGNER_TOKEN = "SECURE_CENTRAL_INTERNAL_SIGNER_TOKEN_9A8F7E6D5C4B3A21"
$env:CENTRAL_DUAL_APPROVAL_ACTIONS = "PRIMARY_TRANSFER,COMMERCIAL_REFUND,KEY_ROTATION"
$env:CENTRAL_STORAGE_ROOT = "C:\Users\muham\EdgeRetailsWorktrees\central-one\artifacts\storage"
$env:ASPNETCORE_ENVIRONMENT = "Staging"
$env:ASPNETCORE_URLS = "https://127.0.0.1:5001;http://127.0.0.1:5000"
```

---

## 5. Build, Migration, & Application Startup

### 5.1 Compilation
Build the scoped Central solution in Release configuration:

```powershell
cd C:\Users\muham\EdgeRetailsWorktrees\central-one

# Verify zero compiler warnings or errors
dotnet build EdgeRetails.Central.slnx -c Release
```

### 5.2 Schema Migrations (DDL Migration Role)
Migrations must be executed under `central054_owner` with the migration history table strictly quarantined within the `central` schema:

```powershell
# Apply EF Core Migrations using the migration connection string
dotnet ef database update `
    --project src/EdgeRetails.Central.Infrastructure/EdgeRetails.Central.Infrastructure.csproj `
    --startup-project src/EdgeRetails.Central.Api/EdgeRetails.Central.Api.csproj `
    --context CentralDbContext `
    --connection $env:CENTRAL_MIGRATION_CONNECTION_STRING
```

### 5.3 Runtime Host Startup (Least-Privilege Role)
Launch the ASP.NET Core server under the runtime connection string:

```powershell
cd C:\Users\muham\EdgeRetailsWorktrees\central-one\src\EdgeRetails.Central.Api
dotnet run -c Release --no-build
```

---

## 6. Runtime Security Configurations

### 6.1 CSRF Defense Mechanism (`CsrfProtectionMiddleware`)
* **Scope:** All state-modifying HTTP requests (`POST`, `PUT`, `DELETE`, `PATCH`) targeting `/api/admin/*`.
* **Cookie Formulation:** `XSRF-TOKEN` cookie (HttpOnly: false, SameSite: Strict, Secure: true).
* **Header Expectation:** Client portal sends matching value in `X-CSRF-TOKEN` header.
* **Origin/Referer Validation:** Validates `Origin` matches loopback host authority. Missing or mismatched origins trigger HTTP 403 Forbidden.

### 6.2 Rate Limiting Policies (`RateLimitingMiddleware`)
* **Public & POS Auth Endpoints (`/api/activation/*`, `/api/auth/login`):** 5 requests per 60-second window per client IP. Prevents brute force of activation codes and passwords.
* **Terminal Reconciliation (`/api/licensing/v1/reconcile`):** 60 requests per minute per installation fingerprint.
* **Admin Mutation Endpoints (`/api/admin/*`):** 30 requests per minute per authenticated admin session.

### 6.3 TOTP Multi-Factor Authentication & Step-Up (`ITotpService`)
* **Standard Login:** Requires username, Argon2id-hashed password, and 6-digit TOTP code (RFC 6238, 30s window, HMAC-SHA1).
* **Step-Up Verification:** Sensitive actions (`PrimaryTransfer`, `Revocation`, `PaymentReview`) require a fresh step-up ticket issued within a 5-minute validity TTL.

### 6.4 Internal Signer Token Protection
* The endpoint `/api/protected-signer/v1/sign` is accessible only by internal application orchestration components.
* Requests must supply `X-Internal-Signer-Token: $env:CENTRAL_INTERNAL_SIGNER_TOKEN`. Mismatched or absent tokens trigger HTTP 401 with audit logging.

---

## 7. Verification & Health Probes

Verify operational health across the control-plane endpoints:

```powershell
# 1. Probe static Web Admin Portal delivery
Invoke-WebRequest -Uri "http://127.0.0.1:5000/index.html" -Method GET

# 2. Probe Public Trust Set
Invoke-RestMethod -Uri "http://127.0.0.1:5000/api/licensing/v1/trust-set" -Method GET

# 3. Probe Licensing Reconcile Contract with invalid payload (expects 400 Bad Request, not 500)
try {
    Invoke-RestMethod -Uri "http://127.0.0.1:5000/api/licensing/v1/reconcile" -Method POST -Body "{}" -ContentType "application/json"
} catch {
    $_.Exception.Response.StatusCode # Returns 400
}
```
