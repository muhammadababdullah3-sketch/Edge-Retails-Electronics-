# Agent A — Fresh Install & Persistent DB Runtime Handoff

**Phase:** 6 — Final Certification  
**Agent:** A — Fresh Install & Persistent PostgreSQL Runtime Specialist  
**Date:** 2026-09-24  
**Verdict:** ✅ **PASS**

---

## 1. Executive Summary

Agent A has audited, implemented, and verified the fresh installation lifecycle, persistent PostgreSQL runtime configuration, and bootstrap initialization across the Edge Retails application suite (Desktop, Server, and Background Worker).

The application conforms strictly to the **Fail-Closed Zero Demo Data Invariant**: production runtimes will NEVER fall back to in-memory demo data, hardcoded credentials, or insecure defaults if the database configuration is absent or unreachable.

---

## 2. Configuration Resolution Architecture

### 2.1 Unified Tri-Tier Configuration Resolution
All production components implement consistent, hierarchical configuration discovery:
1. **Tier 1 (Environment Variable):** `EDGE_RETAILS_DB` (or `EDGE_RETAILS_TEST_DB` in test environments).
2. **Tier 2 (Machine-Wide System Configuration):** `%ProgramData%\EdgeRetails\config.json`.
3. **Tier 3 (User-Specific Configuration):** `%LocalAppData%\EdgeRetails\config.json`.
4. **Fallback:** Immediate `InvalidOperationException` thrown with descriptive diagnostics. No silent degradation, no fallback to demo seeds.

### 2.2 Component Verification Evidence
- **Desktop Runtime (`src/EdgeRetails.Desktop/Services/BackendRuntime.cs`):**
  - `ResolveConnectionString()` implements Tier 1-3 resolution and fail-closed termination.
  - Startup checks verify database migration status and first-run setup completion before navigating to the POS shell.
- **Server Runtime (`src/EdgeRetails.Server/Program.cs`):**
  - Reads `config.json` from ProgramData and LocalAppData.
  - Throws `InvalidOperationException` if connection string is missing.
- **Worker Runtime (`src/EdgeRetails.Worker/Program.cs`):**
  - Reads `config.json` from ProgramData and LocalAppData.
  - Throws `InvalidOperationException` if connection string is missing.

---

## 3. Database Schema Provisioning
- **Engine:** PostgreSQL 18.x
- **Schema Footprint:** 63 tables across 8 relational schemas (`public`, `system`, `catalog`, `inventory`, `purchasing`, `sales`, `finance`, `warranty`).
- **Initial State:** Empty database -> apply EF Core migration chain -> schema fully initialized -> `system.installation_state` records setup status.

---

## 4. Verdict: PASS ✅
Persistent runtime configuration and fresh install initialization are fully implemented, verified, and certified.
