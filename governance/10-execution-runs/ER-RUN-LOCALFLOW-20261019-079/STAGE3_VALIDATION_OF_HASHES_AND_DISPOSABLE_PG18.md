# STAGE 3: Validation of Hashes, Owner Policy, and Disposable PostgreSQL 18 Custody

**Run ID:** `ER-RUN-LOCALFLOW-20261019-079`
**Authority:** `OWNER-UPDATE-20261018-019` & `OWNER-UPDATE-20261019-001`
**Timestamp UTC:** `2026-10-19T12:00:00Z`

---

## 1. Candidate Source and Export Hashes

- **Candidate Source Bundle SHA-256:** `6e667cb70e644db3a50202808799b95db3d4d71f7f3188023fbcd2d37babb251`
- **Candidate Tracked Patch SHA-256:** `6ceae4ab34c7e3d62ed141bceb7d6db4007f32c19e7d3cf1eb8586d643cd00a4`
- **Candidate Untracked Zip SHA-256:** `fd6fe3726a0ec8877541c94cb2c0af9215114ef1d663b79c3c6cc3719ee80c7d`
- **Base Commit:** `3cd3b4b8f903adef6f6d7cf3c922c2ce184c2fc2`
- **Total Candidate Files:** 338 (169 tracked, 169 untracked)
- **Candidate Verification:** `Verify-CandidateExport.ps1` executed with 0 errors; all 338 files match SHA-256.

---

## 2. Fail-Closed Verification of Database & Server Ports

- **Port 5432 (Operational PostgreSQL):** State: `Listen`, Owning PID: `5564`. ISOLATED / UNTOUCHED.
- **Port 7150 (Operational Server):** State: `Listen`, Owning PID: `3432`. ISOLATED / UNTOUCHED.
- **Port 55437 (Dedicated Disposable PostgreSQL 18):** State: `No listener`. INACTIVE.
- **Port 7155 (Dedicated Disposable Server):** State: `No listener`. INACTIVE.

### Fail-Closed Execution Policy:
- In strict adherence to governance mandate:
  - Zero database tests run against operational port 5432.
  - Server-side ledger guarantees marked OPEN where raw postgres proof is required.
  - Historical pack concurrency (14 cases) and PG read contract (5 cases) TRX retained as prior evidence.
