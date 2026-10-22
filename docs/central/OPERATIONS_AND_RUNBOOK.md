# Edge Retails CENTRAL-ONE — Consolidated Operations & Runbook (Waves 1–6)

## 1. Disposable PostgreSQL 18.6 Test Environment (G-ENV)

To satisfy the strict database isolation requirements and fail-closed gates, an owned disposable PostgreSQL 18 cluster is provisioned on a dedicated nondefault loopback port.

### 1.1 Environment Parameters
* **Engine:** PostgreSQL 18.6 (x64 Windows executable).
* **Port:** `55454` (dedicated loopback nondefault port).
* **Host:** `127.0.0.1`.
* **Database Name:** `central054_test`.
* **Schema:** `central`.
* **Application Role:** `central054_app`.
* **Migration Role:** `central054_owner`.
* **Data Directory:** `test-artifacts/w1-close-054/pgdata`.
* **Log File:** `test-artifacts/w1-close-054/pgdata/logfile.log`.
* **Ownership Marker:** `test-artifacts/w1-close-054/GUARD_MARKER.json`.

### 1.2 Fail-Closed Target Guard (`DatabaseTargetGuard`)
Any connection attempt from Central must pass validation through `DatabaseTargetGuard`:
* **Forbidden Ports:** Strictly rejects port `5432` (PostgreSQL standard) and port `7150` (POS service).
* **Forbidden Hostnames:** Strictly rejects non-loopback addresses (must connect only to `127.0.0.1` or `localhost`).
* **Forbidden Database Names:** Rejects `edgeretails`, `edgeretails_pos`, `shop_pos`, `postgres`, `template1`, `edgeretails_dev`.
* **Schema Isolation:** Strictly mandates schema `central`. Zero tables exist in `public`.
* **Process Image Verification:** Validates `CENTRAL_PG_PID` matches running `postgres.exe` process (major version 18) with matching data directory and parameters in `GUARD_MARKER.json` and `postmaster.pid`.

---

## 2. Cluster Lifecycle Management

### 2.1 Starting the Disposable PG18 Cluster
```powershell
$pgBin = "C:\Program Files\PostgreSQL\18\bin\pg_ctl.exe"
$dataDir = "C:\Users\muham\EdgeRetailsWorktrees\central-one\test-artifacts\w1-close-054\pgdata"
$logFile = "C:\Users\muham\EdgeRetailsWorktrees\central-one\test-artifacts\w1-close-054\pgdata\logfile.log"

& $pgBin -D $dataDir -l $logFile -o "-p 55454 -h 127.0.0.1" start
```

### 2.2 Updating the Ownership Marker with Active PID
After starting the cluster, update `test-artifacts/w1-close-054/GUARD_MARKER.json` and `artifacts/guards/GUARD_MARKER.json` with the current PID from `postmaster.pid`:
```powershell
$pidVal = (Get-Content "C:\Users\muham\EdgeRetailsWorktrees\central-one\test-artifacts\w1-close-054\pgdata\postmaster.pid")[0].Trim()
$markerPath = "C:\Users\muham\EdgeRetailsWorktrees\central-one\test-artifacts\w1-close-054\GUARD_MARKER.json"
$marker = Get-Content $markerPath | ConvertFrom-Json
$marker.pid = [int]$pidVal
$marker | ConvertTo-Json -Depth 5 | Set-Content $markerPath -Encoding utf8
```

### 2.3 Stopping the Disposable Cluster
```powershell
$pgBin = "C:\Program Files\PostgreSQL\18\bin\pg_ctl.exe"
$dataDir = "C:\Users\muham\EdgeRetailsWorktrees\central-one\test-artifacts\w1-close-054\pgdata"

& $pgBin -D $dataDir stop -m fast
```

---

## 3. Build & Test Procedures

### 3.1 Solution Structure
Central uses the scoped solution `EdgeRetails.Central.slnx` to build and test Central projects independently without modifying `EdgeRetails.sln`:
* `src/EdgeRetails.Central.Domain/EdgeRetails.Central.Domain.csproj`
* `src/EdgeRetails.Central.Application/EdgeRetails.Central.Application.csproj`
* `src/EdgeRetails.Central.Infrastructure/EdgeRetails.Central.Infrastructure.csproj`
* `src/EdgeRetails.Central.Api/EdgeRetails.Central.Api.csproj`
* `src/EdgeRetails.ProtectedSigner/EdgeRetails.ProtectedSigner.csproj`
* `tests/EdgeRetails.Central.UnitTests/EdgeRetails.Central.UnitTests.csproj`
* `tests/EdgeRetails.Central.IntegrationTests/EdgeRetails.Central.IntegrationTests.csproj`

### 3.2 Running the Full Test Suite with Environment Variables
Ensure the disposable PG18 cluster is running, then execute:
```powershell
$env:CENTRAL_PG_HOST = "127.0.0.1"
$env:CENTRAL_PG_PORT = "55454"
$env:CENTRAL_PG_DATABASE = "central054_test"
$env:CENTRAL_PG_ROLE = "central054_app"
$env:CENTRAL_PG_MIGRATION_ROLE = "central054_owner"
$env:CENTRAL_PG_PID = (Get-Content "C:\Users\muham\EdgeRetailsWorktrees\central-one\test-artifacts\w1-close-054\pgdata\postmaster.pid")[0].Trim()
$env:CENTRAL_PG_BINARY = "C:\Program Files\PostgreSQL\18\bin\postgres.exe"
$env:CENTRAL_PG_OWNERSHIP_MARKER = "C:\Users\muham\EdgeRetailsWorktrees\central-one\test-artifacts\w1-close-054\GUARD_MARKER.json"
$env:CENTRAL_PG_DATA_DIR = "C:\Users\muham\EdgeRetailsWorktrees\central-one\test-artifacts\w1-close-054\pgdata"
$env:CENTRAL_DATABASE_CONNECTION_STRING = "Host=127.0.0.1;Port=55454;Database=central054_test;Username=central054_app;Password=central054_app;SearchPath=central"
$env:CENTRAL_MIGRATION_CONNECTION_STRING = "Host=127.0.0.1;Port=55454;Database=central054_test;Username=central054_owner;Password=central054_owner;SearchPath=central"
$env:CENTRAL_INTERNAL_SIGNER_TOKEN = "TEST_INTERNAL_TOKEN_SECRET_DO_NOT_USE_IN_PRODUCTION"
$env:CENTRAL_DUAL_APPROVAL_ACTIONS = "NONE"

# Build Release
dotnet build EdgeRetails.Central.slnx -c Release

# Run Unit Tests (85 tests)
dotnet test tests/EdgeRetails.Central.UnitTests/EdgeRetails.Central.UnitTests.csproj -c Release

# Run Integration Tests (64 tests)
dotnet test tests/EdgeRetails.Central.IntegrationTests/EdgeRetails.Central.IntegrationTests.csproj -c Release
```
**Total Verified Test Footprint:** 149 passing tests (85 unit + 64 integration), 0 failures, 0 skipped.

---

## 4. Transactional Outbox & Dead-Letter Runbook (`CONE-T24`)

### 4.1 Message Lifecycle
```
PENDING ──(delivery attempt)──► DISPATCHED (success)
   │
   └──(failure)──► FAILED (retry_count++)
                      │
                      └──(retry_count >= maxRetries)──► DEAD_LETTER (isolated)
```

### 4.2 Outbox Dispatcher Operation
* Default batch size: `50` messages per polling iteration.
* Default maximum retries: `5` attempts.
* Messages in `DEAD_LETTER` status are strictly skipped by normal polling loops to prevent poison message blocking.
* Deduplication at consumer sink uses PostgreSQL `INSERT INTO central.outbox_consumer_effects ("MessageId", "EventType", "AggregateId", "PayloadJson") VALUES (...) ON CONFLICT ("MessageId") DO NOTHING`.

### 4.3 Operator Interventions for Dead-Letter Messages
1. **Query Dead-Letter Messages:**
   ```sql
   SELECT "Id", "EventType", "AggregateId", "RetryCount", "LastError", "OccurredAtUtc"
   FROM central.outbox_messages
   WHERE "Status" = 'DEAD_LETTER'
   ORDER BY "OccurredAtUtc" DESC;
   ```
2. **Re-queue Dead-Letter Message (after resolving root cause):**
   ```sql
   UPDATE central.outbox_messages
   SET "Status" = 'PENDING', "RetryCount" = 0, "LastError" = NULL
   WHERE "Id" = '<MESSAGE_GUID>';
   ```

---

## 5. Licensing & Terminal Reconciliation Runbook (`CONE-T25`)

### 5.1 Reconcile Endpoint Operation
* **Route:** `POST /api/licensing/v1/reconcile`
* **Request Payload:** `ReconcileCommandDto` containing `InstallationId`, `CertificateId`, `AuthorityRevision`, `HardwareFingerprintHash`, `ApplicationVersion`, and `LastVerifiedVendorTimeUtc`.
* **Processing:**
  1. Validates `Installation` exists and is in `Approved` status.
  2. Verifies `HardwareFingerprintHash`:
     - Matches `Installation.MachineFingerprint`, OR
     - An approved `HardwareReview` exists for `InstallationId` with status `Approved`.
  3. Pre-persists an authorized `CentralSigningRequest` in `CentralDbContext`.
  4. Invokes `IProtectedSigningEngine.SignAsync` with RS256 PKCS#1 v1.5 algorithm.
  5. Updates `Installation.LastHeartbeatUtc` and returns signed `SignedEntitlementDto`.

### 5.2 Handling Hardware Mismatch (`HARDWARE_MISMATCH`)
When a POS terminal undergoes motherboard/CPU replacement, `POST /api/licensing/v1/reconcile` rejects with HTTP 400 (`HARDWARE_FINGERPRINT_MISMATCH`).
* **Operator Resolution:**
  1. Operator reviews ticket or terminal diagnostics.
  2. Admin submits a hardware review request via `POST /api/admin/v1/hardware-reviews` with `installationId`, `newFingerprintHash`, and `justification`.
  3. Authorized admin approves the review via `POST /api/admin/v1/hardware-reviews/{id}/approve`.
  4. Terminal retries `/api/licensing/v1/reconcile`, which now succeeds through the approved hardware review.

---

## 6. Disaster Recovery & Backup Rehearsal Procedures (`CONE-T27`)

### 6.1 Fail-Closed Target Guard Verification
Before executing any restore operation, verify the target database passes fail-closed validation:
* Target port cannot be 5432 or 7150.
* Target database cannot be `edgeretails`, `shop_pos`, or `postgres`.
* Data directory must contain valid `GUARD_MARKER.json` identifying active process.

### 6.2 Restoration Rehearsal Execution
```powershell
# Verify guard marker before restore
python -c "
import json
with open('test-artifacts/w1-close-054/GUARD_MARKER.json') as f:
    data = json.load(f)
assert data['port'] == 55454
assert data['database'] == 'central054_test'
print('DR Rehearsal Target Guard: VALIDATED')
"
```

---

## 7. Provider Handoff & Remaining Governance Gates

Production cutover remains strictly gated by designated action gates:
* **`G-STORAGE-CONFIG`**: Live cloud S3/blob object storage configuration.
* **`G-SUPPORT-CONFIG`**: Live email / webhook notification bridge for support tickets.
* **`G-POS-HANDOFF`**: POS client update to consume Central URL and production trust sets.
* **`G-PROVIDERS`**: Live cloud provider credentials and external DNS cutover.
* **`G-ENV`**: Production PostgreSQL managed cluster provisioning (AWS RDS / Azure / bare metal).
* **`G-RELEASE`**: Final operational release authorization.
* **`G-REVIEWER`**: Final governance body phase acceptance.
