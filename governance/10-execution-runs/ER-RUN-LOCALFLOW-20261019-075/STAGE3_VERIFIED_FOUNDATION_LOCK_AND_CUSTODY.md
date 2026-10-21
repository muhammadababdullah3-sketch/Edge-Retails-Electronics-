# STAGE 3: Verified Foundation Lock & Custody

**Run ID:** `ER-RUN-LOCALFLOW-20261019-075`
**Authority:** `OWNER-UPDATE-20261018-019`
**Timestamp UTC:** `2026-10-19T12:00:00Z`

---

## 1. Disposable PostgreSQL 18.6 Runtime Custody Lock
All database testing, migration rehearsals, and transactional assertions are conducted exclusively against an owned, disposable PostgreSQL 18.6 instance running under strict loopback isolation:
- **Process ID**: `1404` (`postgres.exe`)
- **Executable**: `C:\Program Files\PostgreSQL\18\bin\postgres.exe`
- **Data Directory**: `C:\Users\muham\EdgeRetailsDisposable\ER-RUN-20261011-005\pgdata`
- **Dedicated Port**: `55437` (strictly non-default; default port 5432 is untouched)
- **Host**: `127.0.0.1` (loopback only)
- **Database**: `edgeretails_test_run20261012_001`
- **Approved Role**: `er_provisioner`
- **Local API Routing**: `http://127.0.0.1:7155` (`EDGE_RETAILS_SERVER_URL`)
- **Isolation Guarantee**: Zero operational or production database footprint.

---

## 2. Functional Capacity Lock (Root Review Aligned)
Per `run074-review/ROOT_REVIEW.md` and `run074-review/DRAFT_PROMPT.md`, functional capacity is strictly bounded to actual native workers:
- **Root Coordinator / Sole Integrator**: Exclusive author of POS code edits, test executor, candidate packager, evidence depositor.
- **Read-Only Finance Reviewer** (`/coordinator/run075_finance_review`): Read-only reviewer for financial contracts and error handling.
- **Read-Only Inventory Reviewer** (`/coordinator/run075_inventory_review`): Read-only reviewer for inventory, warranty, and pagination limits.
- **Total Functional Capacity**: 3 (Integrator + 2 Read-Only Reviewers). Fabricated worker suites are rejected.
