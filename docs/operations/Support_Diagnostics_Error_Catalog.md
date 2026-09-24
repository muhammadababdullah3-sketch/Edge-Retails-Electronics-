# Edge Retails — Support Diagnostics & Error Code Catalog

**Document Identifier:** `ER-OPS-DIAG-01`  
**Phase:** Phase 6 — Final Certification & Long-Term Maintenance  
**Canonical Architecture SHA-256:** `12344760C60124DDC2D1C0E54ABFB7BC0E82B9B23A46E6463303EBFA530AB673`  
**Classification:** Operational Support, First-Line & Tier-3 Diagnostic Manual  
**Zero-Leakage Assurance:** Contains zero secrets, passwords, connection strings, private keys, or customer PII.

---

## 1. Diagnostic Architecture & The 11 Core Health Families

Edge Retails implements a centralized, deterministic health evaluation engine (`IPhase5DiagnosticsService` / `ProductionDiagnosticsService`) that polls **11 canonical health families** and classifies overall system state without exposing sensitive cryptographic or infrastructure secrets.

```
+---------------------------------------------------------------------------------------------------+
| DETERMINISTIC HEALTH CLASSIFICATION STATE MACHINE                                                 |
|                                                                                                   |
|            [Empty Snapshot] ----------> UNAVAILABLE                                               |
|                   |                                                                               |
|          [Any Check UNAVAILABLE?] ----> UNAVAILABLE (Fatal block; system offline or dangerous)     |
|                   |                                                                               |
|      [Any Check ACTION_REQUIRED?] ----> ACTION_REQUIRED (Operator must intervene)                 |
|                   |                                                                               |
|           [Any Check DEGRADED?] ------> DEGRADED (System functioning with warnings)                |
|                   |                                                                               |
|            [All Checks HEALTHY] ------> HEALTHY (100% components verified)                         |
+---------------------------------------------------------------------------------------------------+
```

### The 11 Diagnostic Health Families:

| Family ID | Health Dimension | Probe Mechanism | Default Warning Threshold | Emitted Codes & Classifications | Operational Meaning & Guidance |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **01. Latency** | `db.latency` | Stopwatch measurement of `SELECT 1` | `500.0 ms` | • `db.latency.healthy` (HEALTHY)<br>• `db.latency.degraded` (DEGRADED)<br>• `db.unavailable` (UNAVAILABLE) | Evaluates PostgreSQL roundtrip responsiveness. Latency > 500ms indicates heavy analytical locks or storage saturation. |
| **02. Write Safety** | `db.write_safety` | `SELECT NOT pg_is_in_recovery()` | Must be `true` | • `db.write_safety.healthy` (HEALTHY)<br>• `db.write_safety.unavailable` (UNAVAILABLE) | Verifies the node is a read-write primary. Returns `UNAVAILABLE` if connected to a read-only replica or standby node. |
| **03. Schema** | `schema` | `GetPendingMigrationsAsync()` | 0 pending | • `schema.compatible` (HEALTHY)<br>• `schema.incompatible` (ACTION_REQUIRED)<br>• `schema.incompatible` (UNAVAILABLE) | Inspects pending EF migrations. Pending migrations trigger `ACTION_REQUIRED` before login; failed connection yields `UNAVAILABLE`. |
| **04. Local Backup** | `backup.age` | Filesystem probe on `*.manifest.json` | `24.0 hours` | • `backup.healthy` (HEALTHY)<br>• `backup.age.action_required` (ACTION_REQUIRED)<br>• `backup.unavailable` (ACTION_REQUIRED) | Validates that a verified local backup has completed within the last 24 hours. Missing backup directory triggers `ACTION_REQUIRED`. |
| **05. Remote Backup** | `backup.remote` | Configured cloud verification level | `>= REMOTE_HASH_VERIFIED` | • `backup.remote.verified` (HEALTHY)<br>• `backup.remote.unverified` (UNAVAILABLE) | Verifies that offsite cloud backup synchronization is configured and verified. Defaults fail-closed until cloud adapter is active. |
| **06. Disk Storage** | `disk.free` | `DriveInfo.AvailableFreeSpace` | `10.0 GiB` (`10737418240 bytes`) | • `disk.healthy` (HEALTHY)<br>• `disk.degraded` (DEGRADED) | Monitors system storage headroom. Free space < 10 GiB reports `DEGRADED`; < 1 GiB throws `CriticalDiskFloorException` to prevent corruption. |
| **07. Worker Liveness**| `worker.heartbeat` | Last write time on `worker.heartbeat` | `2.0 minutes` | • `worker.heartbeat.healthy` (HEALTHY)<br>• `worker.heartbeat.action_required` (ACTION_REQUIRED) | Verifies `EdgeRetails.Worker` background process is executing. Stale heartbeat indicates service failure or process crash. |
| **08. Print Backlog** | `print.backlog` | Count of pending outbox print jobs | `50 jobs` | • `print.backlog.healthy` (HEALTHY)<br>• `print.backlog.action_required` (ACTION_REQUIRED) | Measures queue depth of receipt documents waiting for physical hardware transmission. |
| **09. Outcome Unknown**| `outcome_unknown` | Count of `print.outcome_unknown` jobs | `1 job` | • `outcome_unknown.healthy` (HEALTHY)<br>• `outcome_unknown.action_required` (ACTION_REQUIRED) | Detects ambiguous receipt print cuts where cashier verification is required to avoid blind duplicates. |
| **10. Dead Letter** | `action_required_backlog`| Outbox messages with `status = 5` | `1 job` | • `action_required_backlog.healthy` (HEALTHY)<br>• `action_required_backlog.action_required` (ACTION_REQUIRED) | Tracks outbox jobs that exhausted retries or encountered logical errors requiring supervisor intervention. |
| **11. Failed Jobs** | `failed_jobs` | Outbox messages with `status = 4` | `1 job` | • `failed_jobs.healthy` (HEALTHY)<br>• `failed_jobs.action_required` (ACTION_REQUIRED) | Measures terminal failed background effects. |

*(Auxiliary probe: `reconciliation` checks authoritative reconciliation sources, reporting `reconciliation.action_required` if discrepancy authority is unconfigured).*

---

## 2. Policy Configuration & Environment Variables

All diagnostic probe thresholds are governed dynamically via environment variables with safe defaults and bounded validation:

```powershell
# Bounded Policy Configuration Example
$env:EDGE_RETAILS_DIAG_BACKUP_MAX_AGE_HOURS = "24"             # Range: 0.0 < h <= 720.0
$env:EDGE_RETAILS_DIAG_WORKER_MAX_AGE_MINUTES = "2"           # Range: 0.0 < m <= 1440.0
$env:EDGE_RETAILS_DIAG_DISK_FREE_WARNING_BYTES = "10737418240" # Range: >= 0 (10 GiB)
$env:EDGE_RETAILS_DIAG_PRINT_BACKLOG_WARNING_COUNT = "50"     # Range: 0 to 1,000,000
$env:EDGE_RETAILS_DIAG_OUTCOME_UNKNOWN_WARNING_COUNT = "1"    # Range: 0 to 1,000,000
$env:EDGE_RETAILS_DIAG_ACTION_REQUIRED_WARNING_COUNT = "1"    # Range: 0 to 1,000,000
$env:EDGE_RETAILS_DIAG_FAILED_JOBS_WARNING_COUNT = "1"        # Range: 0 to 1,000,000
$env:EDGE_RETAILS_DIAG_RECONCILIATION_FAILURE_WARNING_COUNT="1" # Range: 0 to 1,000,000
$env:EDGE_RETAILS_DIAG_DB_LATENCY_WARNING_MS = "500"          # Range: 0.0 < ms <= 86,400,000
```

---

## 3. Comprehensive Error Code Catalog

### 3.1 HTTP & REST API Status Codes (`EdgeRetails.Server`)

| HTTP Code | Canonical Error Code | Subsystem / Endpoint | Cause / Description | Tier-1 Support Action |
| :---: | :--- | :--- | :--- | :--- |
| **400** | `protocol.incompatible` | `ProtocolCompatibilityMiddleware` | The client terminal sent an unsupported `X-Protocol-Version` header. | Upgrade terminal software to match server version. |
| **401** | `auth.terminal_id_missing` | `TerminalAuthenticationMiddleware` | Request lacked mandatory `X-Terminal-Id` header. | Verify terminal configuration settings. |
| **401** | `auth.terminal_unknown` | `TerminalAuthenticationMiddleware` | Terminal ID is not registered in `system.terminals`. | Complete terminal onboarding/registration on server. |
| **401** | `auth.invalid_secret` | `TerminalAuthenticationMiddleware` | Provided `X-Terminal-Secret` did not match salted SHA-256 hash. | Re-enter terminal secret or re-generate via administrator. |
| **403** | `auth.terminal_suspended` | `TerminalAuthenticationMiddleware` | Terminal status is `Suspended` (2) in `system.terminals`. | Administrator must reactivate terminal in Settings UI. |
| **403** | `auth.terminal_revoked` | `TerminalAuthenticationMiddleware` | Terminal status is `Revoked` (3). Terminal permanently decommissioned. | Device must be retired or re-registered under new ID. |
| **409** | `terminals.capacity_exceeded`| `RegisterTerminalHandler` | Attempted to register more terminals than allowed by `MaxTerminals`. | Upgrade license tier to allow additional stations. |
| **409** | `idempotency.payload_mismatch`| Sales & Finance Handlers | Same `ClientOperationId` retransmitted with modified payload. | Cashier must not modify cart items during network retry. |
| **503** | `system.maintenance_mode` | `MaintenanceModeGuardMiddleware` | Mutating request blocked while server is in maintenance or backup cutover. | Wait for scheduled maintenance or cutover to complete. |

### 3.2 Terminal Network & State Machine Error Codes

The client-side `IApplicationGateway` operates a four-state machine: `Connected`, `Degraded`, `Reconnecting`, and `Disconnected`.

| State Code | Error Identifier | Invariant Enforced | Recovery Procedure |
| :--- | :--- | :--- | :--- |
| **Disconnected** | `network.offline_mutation_forbidden` | **Strict Fail-Closed:** Terminal never writes shadow or uncommitted sales while offline. | Check Ethernet/Wi-Fi connection to LAN Server. When connection returns, terminal transitions to `Reconnecting`. |
| **Reconnecting** | `network.revalidation_in_progress` | Mutations blocked until authoritative server sync completes. | Terminal automatically invokes `AuthoritativeRevalidationHandler`. |
| **Degraded** | `network.heartbeat_missed` | Ping latency exceeded threshold. | Mutations permitted, but operator warned of network instability. |

### 3.3 Domain Business Error Codes

| Error Code | Domain / Handler | Cause | Safe Resolution |
| :--- | :--- | :--- | :--- |
| `inventory.insufficient_stock` | Sales / Lot Allocation | Requested sellable quantity exceeds current available inventory lots. | Restock items via Purchase Invoice or perform Stock Adjustment. |
| `inventory.stocktake_blocks_product` | Sales / Counting Lock | Product is locked under an active counting Stocktake session. | Finalize or cancel active counting stocktake session. |
| `supplier.payment_exceeds_payable` | Supplier Khata Ledger | Payment amount exceeds outstanding supplier account balance. | Verify invoice credit balance before recording payment. |
| `warranty.active_claim_exists` | Warranty RMA | An open warranty claim already exists for this serialized unit IMEI/SN. | Search existing claims; progress open claim instead of creating new. |

### 3.4 Licensing Error Codes

| Status / Code | Subsystem | Meaning | Resolution |
| :--- | :--- | :--- | :--- |
| `license.empty` | `SignedLicenseValidator` | License file content is empty or zero bytes. | Reinstall original signed `.erlic` file. |
| `license.corrupt_json` | `SignedLicenseValidator` | Envelope JSON cannot be parsed. | Ensure file was not truncated during transfer. |
| `license.unsupported_algorithm` | `SignedLicenseValidator` | Envelope algorithm is not `RS256`. | Obtain license generated with certified keypair. |
| `license.invalid_signature` | `SignedLicenseValidator` | RSA-SHA256 signature verification failed. | License was altered or signed with wrong private key. |
| `license.device_mismatch` | `SignedLicenseValidator` | Hardware fingerprint (`DeviceId`) does not match. | Contact support to re-bind license to new hardware. |
| `license.expired` | `SignedLicenseValidator` | Current UTC time is past `ExpiryDate`. | Renew subscription and import updated license. |

---

## 4. Redacted Diagnostic Support Bundle

When escalating to Tier-3 engineering, administrators can generate a sanitized diagnostic support bundle:

```powershell
# Generate sanitized support bundle
dotnet run --project "C:\Program Files\Edge Retails\EdgeRetails.Desktop.exe" -- export-diagnostics `
    --output "$env:USERPROFILE\Desktop\EdgeRetails_Support_Bundle.zip"
```

### Sanitization Policy:
- **Included:** System version, schema version, diagnostic probe values, anonymized hardware fingerprint, last 100 outbox error codes, and PostgreSQL query plan stats.
- **Excluded (Redacted):** All database connection strings, database passwords, cashier PINs, license private keys, customer names, telephone numbers, and financial cash balances.
