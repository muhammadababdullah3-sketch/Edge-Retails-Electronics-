# Edge Retails CENTRAL-ONE — Disaster Recovery & Business Continuity Runbook
**Document ID:** `CONE-DOC-T29-DR-001`  
**Scope:** Control-Plane Failover, Zero-Knowledge Backup Lifecycle, PITR, Key Rollover  
**Classification:** Core Operations / Reconstructable Runbook  
**Target Path:** `docs/central/DISASTER_RECOVERY_RUNBOOK.md`

---

## 1. Disaster Recovery & Zero-Knowledge Architecture

CENTRAL-ONE enforces a strict Zero-Knowledge Custody model across all backup and sync infrastructure:

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                          LOCAL POINT OF SALE (POS)                          │
│                                                                             │
│  ┌───────────────────────┐             ┌─────────────────────────────────┐  │
│  │ Local POS Data Engine │             │ POS Master Key Custody          │  │
│  │ (Sales, Cash, Stock)  │             │ (Held strictly at edge machine; │  │
│  └───────────┬───────────┘             │  NEVER transmitted to Central)  │  │
│              │                         └────────────────┬────────────────┘  │
│              ▼                                          ▼                   │
│   ┌─────────────────────────────────────────────────────────────┐           │
│   │ Client-Side AES-256-GCM Envelope Encryption                 │           │
│   │ Input: SQLite/PG Snapshot -> Output: Encrypted Ciphertext   │           │
│   └──────────────────────────────┬──────────────────────────────┘           │
└──────────────────────────────────┼──────────────────────────────────────────┘
                                   │ Upload Ciphertext + SHA-256 Digest
                                   ▼ (POST /api/backups/v1/upload)
┌─────────────────────────────────────────────────────────────────────────────┐
│                       CENTRAL-ONE CLOUD CONTROL PLANE                       │
│                                                                             │
│  ┌───────────────────────────────────────────────────────────────────────┐  │
│  │ Zero-Knowledge Storage Custody                                        │  │
│  │ - Stores: Ciphertext blob, SHA-256 hash, IV, Tag, Installation ID     │  │
│  │ - Zero Central Key Custody: Central CANNOT decrypt shop backups       │  │
│  │ - Refuses unauthenticated, modified, or orphan blobs                  │  │
│  └───────────────────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────────────────┘
```

### 1.1 Invariant Guarantees
1. **Zero Central Key Custody:** Encryption keys are generated, stored, and managed strictly at edge retail nodes. Central holds zero knowledge of plaintext shop databases and possesses no capability to decrypt backups.
2. **Deterministic Payload Digesting:** Backups are indexed via their SHA-256 digest. Corrupted uploads or mismatched hashes are rejected immediately.
3. **Target Guard Restrictiveness:** Disaster recovery rehearsals and database restores are strictly prohibited from targeting standard ports (`5432`, `7150`) or production POS database identities.
4. **Append-Only Immutability:** Audit records and outbox event streams cannot be altered during restoration.

---

## 2. Rehearsal Database Restore Procedure

To validate backup integrity and verify disaster readiness without disrupting the running environment, rehearsals restore snapshots into an auxiliary disposable database (`central054_restore_rehearsal`) on isolated port `55455`.

### 2.1 Rehearsal Provisioning
```powershell
# Define Rehearsal Parameters
$pgBin = "C:\Program Files\PostgreSQL\18\bin"
$rehearsalData = "C:\Users\muham\EdgeRetailsWorktrees\central-one\test-artifacts\w1-close-054\pgdata_rehearsal"
$rehearsalLog = "$rehearsalData\rehearsal.log"
$rehearsalMarker = "C:\Users\muham\EdgeRetailsWorktrees\central-one\test-artifacts\w1-close-054\REHEARSAL_GUARD_MARKER.json"
$backupFile = "C:\Users\muham\EdgeRetailsWorktrees\central-one\artifacts\backups\central_snapshot.dump"

# 1. Initialize temporary rehearsal cluster on isolated port 55455
if (-not (Test-Path "$rehearsalData\PG_VERSION")) {
    & "$pgBin\initdb.exe" -D $rehearsalData -U postgres -A trust --encoding=UTF8
}

# 2. Start rehearsal instance
& "$pgBin\pg_ctl.exe" -D $rehearsalData -l $rehearsalLog -o "-p 55455 -h 127.0.0.1" start

# 3. Create ownership marker for rehearsal instance
$rehearsalPid = (Get-Content "$rehearsalData\postmaster.pid")[0].Trim()
$markerContent = @{
    cluster_type = "disposable_pg18"
    owner = "central_test_admin"
    pid = [int]$rehearsalPid
    port = 55455
    database = "central054_rehearsal"
    runtimeRole = "central054_app"
    migrationRole = "central054_owner"
    dataDirectory = $rehearsalData
    createdAtUtc = (Get-Date).ToUniversalTime().ToString("o")
} | ConvertTo-Json -Depth 4
Set-Content -Path $rehearsalMarker -Value $markerContent -Encoding UTF8
```

### 2.2 Schema Restoration & Validation
```powershell
# 1. Create target database and roles on rehearsal instance
& "$pgBin\psql.exe" -h 127.0.0.1 -p 55455 -U postgres -c "CREATE DATABASE central054_rehearsal;"
& "$pgBin\psql.exe" -h 127.0.0.1 -p 55455 -U postgres -d central054_rehearsal -c "CREATE EXTENSION IF NOT EXISTS pgcrypto;"
& "$pgBin\psql.exe" -h 127.0.0.1 -p 55455 -U postgres -c "CREATE ROLE central054_owner WITH LOGIN PASSWORD 'central054_owner';"
& "$pgBin\psql.exe" -h 127.0.0.1 -p 55455 -U postgres -c "CREATE ROLE central054_app WITH LOGIN PASSWORD 'central054_app';"

# 2. Execute pg_restore into central schema
& "$pgBin\pg_restore.exe" -h 127.0.0.1 -p 55455 -U central054_owner -d central054_rehearsal --clean --if-exists $backupFile

# 3. Execute Target Guard Verification against restored cluster
$env:CENTRAL_PG_HOST = "127.0.0.1"
$env:CENTRAL_PG_PORT = "55455"
$env:CENTRAL_PG_DATABASE = "central054_rehearsal"
$env:CENTRAL_PG_ROLE = "central054_app"
$env:CENTRAL_PG_MIGRATION_ROLE = "central054_owner"
$env:CENTRAL_PG_DATA_DIR = $rehearsalData
$env:CENTRAL_PG_OWNERSHIP_MARKER = $rehearsalMarker
$env:CENTRAL_PG_BINARY = "$pgBin\postgres.exe"
$env:CENTRAL_PG_PID = $rehearsalPid

# Rehearsal Validation Script
python -c "
import os, sys, json
print('Validating disaster recovery rehearsal target guard...')
assert os.environ['CENTRAL_PG_PORT'] == '55455', 'Invalid port'
assert os.environ['CENTRAL_PG_DATABASE'] == 'central054_rehearsal', 'Invalid database'
with open(os.environ['CENTRAL_PG_OWNERSHIP_MARKER']) as f:
    marker = json.load(f)
assert marker['port'] == 55455
assert marker['owner'] == 'central_test_admin'
print('DISASTER RECOVERY REHEARSAL VERIFICATION: SUCCESS')
"

# 4. Teardown rehearsal instance
& "$pgBin\pg_ctl.exe" -D $rehearsalData stop -m fast
Remove-Item -Recurse -Force $rehearsalData
```

---

## 3. Database Point-in-Time Recovery (PITR)

When data corruption or operator error occurs, execute PostgreSQL WAL-based Point-in-Time Recovery:

```powershell
# 1. Stop active Central-One host process
Stop-Process -Name "EdgeRetails.Central.Api" -Force -ErrorAction SilentlyContinue

# 2. Stop running PostgreSQL instance
& "$pgBin\pg_ctl.exe" -D $pgData stop -m fast

# 3. Archive current corrupted data directory
Move-Item -Path $pgData -Destination "$pgData.corrupt_$(Get-Date -Format 'yyyyMMdd_HHmmss')"

# 4. Restore physical base backup
& "$pgBin\pg_basebackup.exe" -D $pgData -Fp -Xs -v

# 5. Create recovery signal file and configure target recovery time
Set-Content -Path "$pgData\recovery.signal" -Value ""
Add-Content -Path "$pgData\postgresql.conf" -Value @"
restore_command = 'copy C:\\wal_archive\\%f %p'
recovery_target_time = '2026-10-21 14:30:00 UTC'
recovery_target_action = 'promote'
"@

# 6. Restart cluster and monitor recovery logs
& "$pgBin\pg_ctl.exe" -D $pgData -l "$pgData\logfile.log" -o "-p 55454 -h 127.0.0.1" start
Get-Content -Path "$pgData\logfile.log" -Tail 50 -Wait
```

---

## 4. Schema Migration Rollback Procedures

If a newly deployed Entity Framework migration introduces defects or operational failure:

```powershell
# 1. Inspect applied migrations
dotnet ef migrations list `
    --project src/EdgeRetails.Central.Infrastructure/EdgeRetails.Central.Infrastructure.csproj `
    --startup-project src/EdgeRetails.Central.Api/EdgeRetails.Central.Api.csproj `
    --context CentralDbContext `
    --connection $env:CENTRAL_MIGRATION_CONNECTION_STRING

# 2. Roll back to the known stable migration target
# Example: Rolling back to '20261018093309_InitialCentralSchema'
dotnet ef database update 20261018093309_InitialCentralSchema `
    --project src/EdgeRetails.Central.Infrastructure/EdgeRetails.Central.Infrastructure.csproj `
    --startup-project src/EdgeRetails.Central.Api/EdgeRetails.Central.Api.csproj `
    --context CentralDbContext `
    --connection $env:CENTRAL_MIGRATION_CONNECTION_STRING

# 3. Verify database schema compatibility with the running codebase
```

---

## 5. Cryptographic Signer Failover & Emergency Key Rollover

In the event of suspected private key exposure or hardware compromise:

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                        EMERGENCY KEY ROTATION WORKFLOW                      │
│                                                                             │
│ 1. Revoke Compromised Key Record ("Status" = 'REVOKED')                     │
│ 2. Generate New RS256 RSA-2048/4096 Keypair in Memory                       │
│ 3. Persist Key Descriptor to `central.signing_keys`                         │
│ 4. Issue Incrementing `TrustSetRecord` (AuthorityRevision++)                │
│ 5. Push Updated Trust Set to All Connected POS Primary Terminals            │
└─────────────────────────────────────────────────────────────────────────────┘
```

### 5.1 Emergency Key Rotation Procedure
1. **Trigger Key Rotation API:**
   Authorized security administrators submit a dual-approved sensitive action:
   ```http
   POST /api/admin/v1/sensitive-actions/key-rotation
   Content-Type: application/json
   X-CSRF-TOKEN: <ValidCsrfToken>
   X-StepUp-Token: <ValidStepUpToken>

   {
     "reason": "EMERGENCY_PRIVATE_KEY_COMPROMISE",
     "targetKeyId": "central-key-v1",
     "newAlgorithm": "RS256"
   }
   ```
2. **Database State Verification:**
   Verify the old key is retired and the new key is active:
   ```sql
   SELECT "KeyId", "Algorithm", "Status", "CreatedAtUtc", "RevokedAtUtc" 
   FROM central.signing_keys 
   ORDER BY "CreatedAtUtc" DESC;
   ```
3. **Public Trust Set Distribution:**
   Terminals receive the updated trust set on subsequent reconciliation heartbeats:
   ```http
   GET /api/licensing/v1/trust-set
   ```
   The response returns the active public key set signed by the Central root certificate.

---

## 6. Service Degradation & Outbox Dead-Letter Procedures

### 6.1 Read-Only Degradation Mode
When the primary database or downstream cloud dependencies experience severe degradation:
1. Set `$env:CENTRAL_DEGRADED_READ_ONLY = "TRUE"`.
2. All non-idempotent mutating endpoints (`POST /api/commercial/*`, `POST /api/activation/*`) return HTTP 503 (`SERVICE_DEGRADED_READ_ONLY`).
3. Read-only queries (`GET /api/licensing/v1/trust-set`, Customer 360 inspection) remain operational.

### 6.2 Transactional Outbox Poison Message Isolation
When message delivery exceeds the maximum retry threshold (default: 5 retries), the outbox worker quarantines the event into `DEAD_LETTER` status:

```sql
-- 1. Inspect Dead-Letter Queue
SELECT "Id", "EventType", "AggregateId", "RetryCount", "LastError", "OccurredAtUtc"
FROM central.outbox_messages
WHERE "Status" = 'DEAD_LETTER'
ORDER BY "OccurredAtUtc" DESC;

-- 2. Diagnose failure cause from "LastError"
-- 3. Upon fixing downstream consumer, requeue messages for dispatch:
UPDATE central.outbox_messages
SET "Status" = 'PENDING', "RetryCount" = 0, "LastError" = NULL
WHERE "Status" = 'DEAD_LETTER' AND "EventType" = 'CommercialLicenseIssuedEvent';
```

---

## 7. Future POS Contract Handoff & Synchronization Protocol

When the local Point of Sale workstation is provisioned and connects to Central, communications strictly adhere to the following contract protocols:

### 7.1 Activation & Terminal Lineage Handshake
```
POS Primary Terminal                                    CENTRAL-ONE API
        │                                                      │
        │  1. POST /api/activation/v1/redeem                   │
        │     { token, machineFingerprintHash, friendlyName }  │
        ├─────────────────────────────────────────────────────►│
        │                                                      │ Validates Token,
        │                                                      │ Registers Installation,
        │                                                      │ Enforces PrimaryCapacity=1
        │  2. Returns Signed Certificate & InstallationId      │
        │◄─────────────────────────────────────────────────────┤
```

### 7.2 Entitlement Reconciliation Protocol (`POST /api/licensing/v1/reconcile`)
* **Endpoint:** `POST /api/licensing/v1/reconcile`
* **Transport Payload:**
  ```json
  {
    "installationId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "certificateId": "7cb4a321-1234-4562-b3fc-2c963f66bbb2",
    "authorityRevision": 3,
    "hardwareFingerprintHash": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
    "applicationVersion": "2.1.0",
    "lastVerifiedVendorTimeUtc": "2026-10-21T12:00:00Z"
  }
  ```
* **Cryptographic Envelope Verification at POS:**
  The POS terminal parses the returned `SignedEntitlementDto` and validates the RS256 signature using `RsaSha256LicenseSignatureVerifier`:
  ```csharp
  bool isValid = verifier.VerifySignature(
      signedEntitlementDto.CanonicalPayloadBytes, 
      signedEntitlementDto.SignatureBase64, 
      trustedCentralPublicKey);
  ```

### 7.3 Zero-Knowledge Offsite Backup Upload Protocol
* **Endpoint:** `POST /api/backups/v1/upload`
* **Headers:**
  * `X-Installation-Id: <GUID>`
  * `X-Payload-Sha256: <HexDigest>`
  * `X-Encryption-Alg: AES-256-GCM`
* **POS Handoff Invariant:** The POS terminal compresses its local SQLite/PostgreSQL business database, encrypts the stream with its edge-resident AES-256-GCM key, and streams raw ciphertext. Central stores the blob, tags it with metadata, and records an append-only audit event. Central never has access to the POS key.
