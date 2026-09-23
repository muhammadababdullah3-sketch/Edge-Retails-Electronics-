# Edge Retails — Sprint 7 Master Implementation Plan

**Date:** 2026-09-20
**Sprint:** 7
**Scope:** PostgreSQL + actual business logic + permissions + audit
**Frontend baseline:** Sprint 6 closed at automated/forensic level
**Git:** untouched unless explicitly requested

## Sprint objective

Sprint 7 replaces demo-only business authority with production backend contracts while preserving the completed 17-screen WPF frontend.

The desktop UI must not call PostgreSQL directly. WPF talks through application contracts; Infrastructure owns EF Core/Npgsql/Dapper and persistence.

Sprint 7 is divided into exactly three implementation phases.

## Phase 1 — PostgreSQL Foundation + Setup + Identity

Purpose: establish the production persistence boundary before transactional screen cutover.

Deliverables:
- PostgreSQL/EF Core migration authority.
- system.installation_state singleton setup lifecycle.
- persistent users, roles, permissions, overrides and sessions.
- PBKDF2-SHA256 PIN credential persistence with no plaintext PIN.
- atomic First Setup bootstrap contract.
- protected Walk-in Customer creation.
- Owner / Manager / Cashier seed roles and permission matrix.
- primary Shop Profile and Receipt defaults.
- database readiness / pending migration contract.
- DI registrations and repository boundaries.
- deterministic migration SQL artifact.
- automated unit and forensic coverage.

Phase 1 does not replace the Sprint 6 WPF demo state yet.## Phase 2 — Transactional Business Backend Attachment

Purpose: make PostgreSQL/application services authoritative for operational business state.

Primary scope:
- Sales + sale returns.
- Purchases + purchase returns.
- Inventory movements, stock truth, provenance and adjustments.
- Thaka projects, material issues, payments, settlement and reopen flows.
- Expenses.
- Customers and Suppliers.
- Reporting read models.
- idempotency, operation/resource locks and transaction boundaries.
- serialized inventory rules and cost allocation.

Frontend ViewModels are moved behind application-facing gateways/contracts without direct EF/Npgsql coupling.

Phase 2 must preserve existing Figma/UI behavior while replacing demo authority module by module.

## Phase 3 — Authorization + Audit + Production WPF Cutover

Purpose: close Sprint 7 as one production-connected desktop system.

Primary scope:
- persistent login/user switching.
- effective permission evaluation from DB roles + overrides.
- permission enforcement inside application use cases, not only navigation.
- authoritative business audit event coverage.
- setup/login/session lifecycle integration.
- DB readiness and migration-safe startup behavior.
- full WPF operational journey against PostgreSQL.
- Debug/Release regression matrix.
- final static/architecture forensic audit.
- removal or isolation of obsolete demo authority where production gateways exist.

Phase 3 closes only after real PostgreSQL journey verification succeeds.## Architecture invariants

1. Desktop never opens a database connection directly.
2. UI permission visibility is convenience; Application permission enforcement is authority.
3. Inventory ledger/state remains the stock authority.
4. Completed financial/business documents are corrected through return/reversal/void flows, not destructive edits.
5. Setup is fail-closed and atomic.
6. PIN plaintext is transient only and never persisted.
7. PostgreSQL constraints protect invariants independently of UI validation.
8. Backend migration state and model snapshot must remain synchronized.
9. Existing 17-screen frontend hierarchy remains intact unless product scope explicitly changes.
10. Sprint 8 concerns such as final printing, backup and license packaging are not pulled into Sprint 7.

## Final status

- Phase 1: CLOSED — real PostgreSQL migration/runtime verification PASS.
- Phase 2: CLOSED — transactional backend attachment complete and real PostgreSQL integration verification PASS.
- Phase 3: CLOSED — authorization/audit/production cutover closure gates complete.
- Sprint 7: FORMALLY CLOSED on 2026-09-21.
- Authoritative closure evidence: docs/Sprint7_Final_Forensic_Closure_Report.md.
