# STAGE 3: Validation of Hashes, Owner Policy, and Disposable PostgreSQL 18 Custody

**Run ID:** `ER-RUN-LOCALFLOW-20261019-076`
**Authority:** `OWNER-UPDATE-20261018-019` & `OWNER-UPDATE-20261019-001`
**Timestamp UTC:** `2026-10-19T12:00:00Z`

---

## 1. Candidate Source and Export Hashes

All candidate artifacts are verified bit-for-bit against workspace files on disk:

- **Candidate Source Bundle SHA-256:** `85cf4e912f70d22e969dbcf804a539410e8b727987ba7d5ac9162f703d154ddf` (1,479,753 bytes)
- **Candidate Tracked Patch SHA-256:** `b32887cfe1633f7366f7f6dd5f70ab0643bb3af9b4f34974df7122a699455a4f` (1,140,406 bytes)
- **Candidate Untracked Zip SHA-256:** `fd6fe3726a0ec8877541c94cb2c0af9215114ef1d663b79c3c6cc3719ee80c7d` (750,312 bytes)
- **Base Commit:** `3cd3b4b8f903adef6f6d7cf3c922c2ce184c2fc2`
- **Total Candidate Files:** 329 (160 tracked, 169 untracked)
- **Candidate Verification:** `Verify-CandidateExport.ps1` executed with 0 errors; all 329 files match SHA-256.

---

## 2. Fail-Closed Verification of Database & Server Ports

A fresh live inspection of local network listeners and process ownership was performed:

```powershell
Get-NetTCPConnection -LocalPort 5432, 55437, 7150, 7155 -ErrorAction SilentlyContinue |
    Select-Object LocalAddress, LocalPort, State, OwningProcess
```

### Live Observation Evidence:
1. **Port 5432 (Operational PostgreSQL):**
   - State: `Listen`
   - Owning PID: `5364`
   - Evaluation: Operational database is ACTIVE. Under governance mandate, connecting to or running tests against port 5432 is **STRICTLY PROHIBITED**.
2. **Port 7150 (Operational Server):**
   - State: `Listen`
   - Owning PID: `3424`
   - Evaluation: Operational service is ACTIVE. Connecting to port 7150 is **STRICTLY PROHIBITED**.
3. **Port 55437 (Dedicated Disposable PostgreSQL 18):**
   - State: `No listener`
   - Evaluation: Disposable database instance is **INACTIVE**.
4. **Port 7155 (Dedicated Disposable Server):**
   - State: `No listener`
   - Evaluation: Disposable server instance is **INACTIVE**.

### Fail-Closed Decision:
- In strict adherence to governance policy:
  - **Zero Database Tests Executed**: No live integration tests were run.
  - **No Operational Port Access**: Neither port 5432 nor port 7150 was touched.
  - **Server-Side Ledger Guarantees Marked OPEN**: Claims of server-side single durable cash effect are qualified as **UNPROVEN / OPEN**.
  - **Historical TRX Reconciled**: Pack (14 cases) and PG read contract (5 cases) TRX from 18 Oct are retained explicitly as historical prior evidence.
