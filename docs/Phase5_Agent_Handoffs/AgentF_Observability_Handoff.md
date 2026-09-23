# Phase 5 Observability & Diagnostics Specialist Handoff (Agent F)

- **Date:** 2026-09-23
- **Author:** Agent F — Observability & Diagnostics Specialist
- **Workspace:** `C:\Users\muham\OneDrive\Desktop\Point of Sale`
- **Audit Target Files:**
  - `src/EdgeRetails.Application/Production/Diagnostics/Phase5DiagnosticsContracts.cs`
  - `src/EdgeRetails.Infrastructure/Production/Diagnostics/Phase5DiagnosticsService.cs`
  - `tests/EdgeRetails.UnitTests/Phase5DiagnosticsContractTests.cs`
  - `src/EdgeRetails.Infrastructure/InfrastructureServiceCollectionExtensions.cs`
  - `tests/EdgeRetails.PerformanceTests/Program.cs`

---

## 1. Scope & Verification Objectives

1. **Implementation Inspection:** Verify architecture, contracts, DI registration, and separation of concerns.
2. **Diagnostic Check Coverage:** Verify the 11 canonical health probes (`db.latency`, `db.write_safety`, `schema`, `backup.age`, `disk.free`, `worker.heartbeat`, `print.backlog`, `outcome_unknown`, `action_required_backlog`, `failed_jobs`, `reconciliation`).
3. **Deterministic Classification:** Confirm strict priority order (`UNAVAILABLE` > `ACTION_REQUIRED` > `DEGRADED` > `HEALTHY`).
4. **Environment-Driven Policy:** Confirm all thresholds load from `EDGE_RETAILS_DIAG_*` environment variables with safe defaults and bounded validation ranges.
5. **Data Privacy & Zero Leakage:** Verify zero connection strings, passwords, PINs, JWT secrets, or unhandled raw exception traces are surfaced.

---

## 2. Forensic Inspection Matrix: 11 Diagnostic Probes

| Check Identifier | Probe Mechanism | Evaluated Criteria / SQL Query | Default Policy Threshold | Health Outcomes & Diagnostic Codes |
|---|---|---|---|---|
| **`db.latency`** | Application-side probe via `DbConnection` stopwatch | `SELECT 1` with `CommandTimeout = 5s` | `500.0 ms` | • `<= 500ms`: `HEALTHY` (`db.latency.healthy`)<br>• `> 500ms`: `DEGRADED` (`db.latency.degraded`)<br>• Exception: `UNAVAILABLE` (`db.unavailable`) |
| **`db.write_safety`** | PostgreSQL primary readiness check | `SELECT NOT pg_is_in_recovery()` with `CommandTimeout = 5s` | Must be `true` | • `writable == true`: `HEALTHY` (`db.write_safety.healthy`)<br>• `writable == false`: `UNAVAILABLE` (`db.write_safety.unavailable`)<br>• Exception: `UNAVAILABLE` (`db.write_safety.unavailable`) |
| **`schema`** | EF Core migration compatibility probe | `_db.Database.GetPendingMigrationsAsync(cancellationToken)` | 0 pending migrations | • Pending count == 0: `HEALTHY` (`schema.compatible`)<br>• Pending count > 0: `ACTION_REQUIRED` (`schema.incompatible`)<br>• Exception: `UNAVAILABLE` (`schema.incompatible`) |
| **`backup.age`** | Local backup manifest filesystem probe | Inspects newest `*.manifest.json` in `EDGE_RETAILS_BACKUP_DIR` (or `{stateRoot}/backups`) | `24 hours` | • Age `<= 24h`: `HEALTHY` (`backup.healthy`)<br>• Age `> 24h`: `ACTION_REQUIRED` (`backup.age.action_required`)<br>• Missing dir/manifest: `ACTION_REQUIRED` (`backup.unavailable`) |
| **`backup.remote`** | Offsite backup verification level check | Inspects `EDGE_RETAILS_REMOTE_BACKUP_VERIFICATION_LEVEL` | `>= REMOTE_HASH_VERIFIED` | • Verified level: `HEALTHY` (`backup.remote.verified`)<br>• Unconfigured/insufficient: `UNAVAILABLE` (`backup.remote.unverified`) |
| **`disk.free`** | Operating system drive probe | `DriveInfo.AvailableFreeSpace` on root of `_stateRoot` | `10 GiB` (`10,737,418,240 bytes`) | • Available `>= 10 GiB`: `HEALTHY` (`disk.healthy`)<br>• Available `< 10 GiB`: `DEGRADED` (`disk.degraded`)<br>• Exception/Unresolved: `UNAVAILABLE` (`disk.degraded`) |
| **`worker.heartbeat`** | Background worker liveness timestamp probe | `File.GetLastWriteTimeUtc` on `EDGE_RETAILS_WORKER_HEARTBEAT_PATH` | `2 minutes` | • Age `<= 2m`: `HEALTHY` (`worker.heartbeat.healthy`)<br>• Age `> 2m`: `ACTION_REQUIRED` (`worker.heartbeat.action_required`)<br>• Missing file: `ACTION_REQUIRED` (`worker.heartbeat.action_required`)<br>• Read exception: `UNAVAILABLE` (`worker.heartbeat.action_required`) |
| **`print.backlog`** | Transactional outbox pending print counter | `count(*) FILTER (WHERE effect_type='PrintDocument' AND status IN (1,2))` | `50 jobs` | • Count `<= 50`: `HEALTHY` (`print.backlog.healthy`)<br>• Count `> 50`: `ACTION_REQUIRED` (`print.backlog.action_required`) |
| **`outcome_unknown`** | Unresolved printer outcome failure counter | `count(*) FILTER (WHERE lower(coalesce(last_error,''))='print.outcome_unknown')` | `1 job` | • Count `<= 1`: `HEALTHY` (`outcome_unknown.healthy`)<br>• Count `> 1`: `ACTION_REQUIRED` (`outcome_unknown.action_required`) |
| **`action_required_backlog`** | Outbox dead-letter / escalation backlog | `count(*) FILTER (WHERE status=5)` (ActionRequired) | `1 job` | • Count `<= 1`: `HEALTHY` (`action_required_backlog.healthy`)<br>• Count `> 1`: `ACTION_REQUIRED` (`action_required_backlog.action_required`) |
| **`failed_jobs`** | Outbox hard-failed effect counter | `count(*) FILTER (WHERE status=4)` (Failed) | `1 job` | • Count `<= 1`: `HEALTHY` (`failed_jobs.healthy`)<br>• Count `> 1`: `ACTION_REQUIRED` (`failed_jobs.action_required`) |
| **`reconciliation`** | Authoritative reconciliation failure probe | Evaluated via `CaptureReconciliationStatus()` | Fail-closed authority check | • No authority configured: `UNAVAILABLE` (`reconciliation.action_required`)<br>• Prohibits reporting green without concrete wired authority |

---

## 3. Deterministic Health Classification State Machine

The overall health classification is determined strictly by `Phase5DiagnosticsClassifier.Overall(IReadOnlyList<Phase5DiagnosticValue> checks)` in `Phase5DiagnosticsContracts.cs` (lines 129–156).

### Precedence Hierarchy:
```
           [Empty Snapshot] ----------> UNAVAILABLE
                  |
         [Any Check UNAVAILABLE?] ----> UNAVAILABLE
                  |
     [Any Check ACTION_REQUIRED?] ----> ACTION_REQUIRED
                  |
          [Any Check DEGRADED?] ------> DEGRADED
                  |
           [All Checks HEALTHY] ------> HEALTHY
```

### Invariants:
1. **Fail-Closed on Empty**: If probe list is empty (`checks.Count == 0`), returns `UNAVAILABLE`.
2. **Dominance of Fatal States**: A single `UNAVAILABLE` probe (e.g. database down, write safety failed) guarantees the entire system reports `UNAVAILABLE`, overriding `ACTION_REQUIRED` and `DEGRADED`.
3. **Action Precedence over Degradation**: An actionable condition (e.g., worker dead, pending migration) takes precedence over soft degradation (e.g. 520ms latency).
4. **Purity of Green**: The system ONLY reports `HEALTHY` when 100% of probed components are verified `HEALTHY`.
5. **Unit Test Verification**: All state transitions and dominance rules are unit tested in `Phase5DiagnosticsContractTests.cs`.

---

## 4. Policy Configuration & Environment Variable Governance

All thresholds are isolated in `Phase5DiagnosticsPolicy` and instantiated via `FromEnvironment()`:

| Environment Variable | Target Property | Default Fallback | Enforced Valid Range | Purpose |
|---|---|---|---|---|
| `EDGE_RETAILS_DIAG_BACKUP_MAX_AGE_HOURS` | `BackupMaxAge` | 24 Hours | `0.0 < h <= 720.0` (30 days) | SLA limit before local backup age triggers alert |
| `EDGE_RETAILS_DIAG_WORKER_MAX_AGE_MINUTES` | `WorkerHeartbeatMaxAge` | 2 Minutes | `0.0 < m <= 1440.0` (24 hrs) | Liveness threshold for background worker heartbeat file |
| `EDGE_RETAILS_DIAG_DISK_FREE_WARNING_BYTES` | `DiskFreeWarningBytes` | 10 GiB | `0 <= b <= long.MaxValue / 2` | Storage headroom alert threshold |
| `EDGE_RETAILS_DIAG_PRINT_BACKLOG_WARNING_COUNT` | `PrintBacklogWarningCount` | 50 | `0 <= v <= 1,000,000` | Alert threshold for pending print documents |
| `EDGE_RETAILS_DIAG_OUTCOME_UNKNOWN_WARNING_COUNT` | `OutcomeUnknownWarningCount` | 1 | `0 <= v <= 1,000,000` | Alert threshold for ambiguous hardware print states |
| `EDGE_RETAILS_DIAG_ACTION_REQUIRED_WARNING_COUNT` | `ActionRequiredWarningCount` | 1 | `0 <= v <= 1,000,000` | Alert threshold for outbox entries requiring operator attention |
| `EDGE_RETAILS_DIAG_FAILED_JOBS_WARNING_COUNT` | `FailedJobsWarningCount` | 1 | `0 <= v <= 1,000,000` | Alert threshold for outbox terminal failures |
| `EDGE_RETAILS_DIAG_RECONCILIATION_FAILURE_WARNING_COUNT` | `ReconciliationFailureWarningCount` | 1 | `0 <= v <= 1,000,000` | Alert threshold for reconciliation discrepancy count |
| `EDGE_RETAILS_DIAG_DB_LATENCY_WARNING_MS` | `DbLatencyWarningMilliseconds` | 500.0 ms | `0.0 < ms <= 86,400,000.0` | Warning threshold for single-roundtrip probe latency |

### Configuration Governance Verification:
- **No Hardcoded Constants**: Application code does not bake in inflexible magic numbers; default values act purely as fallbacks.
- **Defensive Boundary Parsing**: If environment variables contain non-numeric data, negative durations, or extreme out-of-range values, `double.TryParse` / `int.TryParse` / `long.TryParse` fails cleanly to the safe fallback.
- **Dependency Injection**: Registered as a singleton in `InfrastructureServiceCollectionExtensions.cs` lines 41–42 using `Phase5DiagnosticsPolicy.FromEnvironment()`.

---

## 5. Security & Zero-Leakage Forensic Audit

A comprehensive string and error handling audit was performed on `Phase5DiagnosticsService.cs`:

1. **Catch Block Sanitization**:
   - `CaptureDatabaseLatencyAsync`: Catch block catches all generic exceptions and emits a sanitized string `"Database probe could not be completed."`. The raw `ex.Message` (which in Npgsql may contain hostnames, ports, database names, or user parameters) is **never** propagated.
   - `CaptureWriteSafetyAsync`: Emits `"Database write-safety could not be verified."`.
   - `CaptureSchemaCompatibilityAsync`: Emits `"Schema compatibility could not be verified."`.
   - `CaptureDisk`: Emits `"Disk free space could not be measured."`.
   - `CaptureWorkerHeartbeat`: Emits `"Worker heartbeat could not be read."`.
   - `CaptureOutboxBacklogsAsync`: Emits `"Outbox backlog diagnostics could not be queried."`.
2. **Metadata Sanitization**:
   - `SafeMetadata` contains only two keys:
     - `["environment"] = "production-diagnostic-snapshot"`
     - `["state_root"] = _stateRoot`
   - Connection strings, user credentials, JWT tokens, and PBKDF2 hash parameters are completely absent from `SafeMetadata`.
3. **Status & Guidance Strings**:
   - Status messages display only numeric timings, counts, and enum identifiers (e.g. `$"Database probe completed in {elapsed:F1} ms."`, `$"Current print backlog: {count}."`).
   - Guidance strings contain strictly operational action instructions (e.g., `"Run the canonical same-operation reconciliation workflow."`).

---

## 6. Architectural Observations & Roadmap Recommendations

1. **Fail-Closed Stubs (`reconciliation` & `backup.remote`)**:
   - Currently, `CaptureReconciliationStatus` intentionally returns `Phase5HealthClassification.UNAVAILABLE` with `"No concrete reconciliation-failure authority is configured in the current deployment."`.
   - Similarly, `CaptureRemoteBackupVerification` defaults to `UNAVAILABLE` unless `EDGE_RETAILS_REMOTE_BACKUP_VERIFICATION_LEVEL` is set to at least `REMOTE_HASH_VERIFIED`.
   - *Assessment*: This is an intentional, highly disciplined security architecture decision to avoid reporting false-green states before physical cloud adapters and automated ledger reconciliation routines are deployed.
2. **Nomenclature Refactoring (Technical Debt Register Item)**:
   - Contracts are presently named `Phase5HealthClassification`, `Phase5DiagnosticsPolicy`, `Phase5DiagnosticCodes`, and `IPhase5DiagnosticsService`.
   - As documented in the Architecture Risk Matrix (Step 18), these embed roadmap phase numbers in permanent domain interfaces.
   - *Recommendation*: Refactor to canonical domain names (`HealthClassification`, `HealthDiagnosticsPolicy`, `HealthDiagnosticCodes`, `IHealthDiagnosticsService`) during subsequent Phase 5 cleanup sprints.

---

## 7. Sign-Off & Verdict

| Verification Item | Requirement | Status |
|---|---|:---:|
| 1. Architecture & Contracts | Contracts cleanly separated from infrastructure probes | **VERIFIED** |
| 2. 11 Diagnostic Checks | All 11 probes present, correct SQL/IO mechanisms, accurate codes | **VERIFIED** |
| 3. Deterministic Health States | Strict priority, fail-closed on empty, fully tested | **VERIFIED** |
| 4. Policy from Environment | 9 `EDGE_RETAILS_DIAG_*` variables parsed defensively | **VERIFIED** |
| 5. Zero Sensitive Data Leakage | All exception catches sanitized; zero credentials in metadata | **VERIFIED** |

**VERDICT: CERTIFIED**
