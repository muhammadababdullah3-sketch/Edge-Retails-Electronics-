# Edge Retails — Sprint 7 Phase 1 Implementation Report

**Date:** 2026-09-20
**Phase:** PostgreSQL Foundation + Setup + Identity
**Status:** CODE COMPLETE / AUTOMATED FORENSIC COMPLETE
**Real PostgreSQL runtime gate:** BLOCKED BY LOCAL TEST-DB AUTHENTICATION CONFIGURATION
**Git:** untouched

## Objective

Phase 1 establishes the production persistence foundation required before the WPF frontend is cut over from Sprint 6 demo authority.

No primary UI screen was added or redesigned.

## Delivered

- Identity domain model for Users, Roles, Permissions, Role Permissions, User Permission Overrides and User Sessions.
- InstallationState lifecycle with NotStarted, InProgress and Complete states.
- First Setup application handler with one transaction boundary.
- protected Walk-in Customer bootstrap.
- Shop Profile and receipt defaults bootstrap.
- Owner, Manager and Cashier system roles.
- eleven canonical frontend-facing permission keys with seeded role mappings.
- PBKDF2-SHA256 PIN credential service using random salt, 210,000 iterations and fixed-time verification.
- no plaintext PIN persistence field.
- setup and identity repository contracts.
- effective role + user-override permission read model.
- database readiness service with connection and pending-migration status.
- Infrastructure dependency-injection registrations.## Physical PostgreSQL schema

Migration created:

20260920111318_Sprint7Phase1SetupIdentity

It adds:
- system.installation_state
- identity.users
- identity.roles
- identity.permissions
- identity.role_permissions
- identity.user_permission_overrides
- identity.user_sessions

Important database protections:
- singleton installation-state key.
- unique role names.
- unique permission keys.
- unique role/permission pair.
- unique user override per user/permission pair.
- unique client session identifier.
- positive PIN-iteration check.
- restrictive User -> Role deletion.
- required identity foreign keys.

EF model snapshot is synchronized with the migration.

An idempotent SQL artifact was generated at:

database/Sprint7_Phase1_SetupIdentity.sql## Setup atomicity contract

First Setup runs through ITransactionRunner and performs a single SaveChanges boundary after creating:

1. InstallationState IN_PROGRESS.
2. primary Shop Profile.
3. protected Walk-in Customer.
4. primary receipt defaults.
5. Owner / Manager / Cashier roles.
6. permission master data.
7. role-permission mappings.
8. hashed Owner user credential.
9. InstallationState COMPLETE.

If setup is already COMPLETE it fails closed.

If identity data already exists while setup is incomplete, bootstrap refuses to silently overwrite it.

Invalid PIN input is rejected before entering the transaction.

## Authorization seed matrix

Owner:
- all eleven seeded permissions.

Manager:
- dashboard.view
- sales.create
- sales.view
- purchasing.manage
- inventory.manage
- customers.manage
- suppliers.manage

Cashier:
- dashboard.view
- sales.create
- sales.view

Application-level authoritative permission enforcement remains Phase 3 scope. Phase 1 establishes its persistent authority model.## Automated verification

Current final gates:
- targeted formatter verify-no-changes: PASS.
- EF pending model changes: NONE.
- Debug full solution build: PASS, 0 warnings, 0 errors.
- Release full solution build: PASS, 0 warnings, 0 errors.
- Debug unit/forensic tests: 134/134 PASS.
- Release unit/forensic tests: 134/134 PASS.
- migration SQL generation: PASS.
- WPF direct Npgsql/DbContext/ConnectionString forensic contract: PASS.

The full repository formatter still reports pre-existing line-ending/encoding debt in older unrelated files. Phase 1 touched files were formatted and verified independently.

## Real PostgreSQL runtime gate

PostgreSQL 18 is installed locally and the Windows service postgresql-x64-18 is RUNNING.

The existing integration suite requires EDGE_RETAILS_TEST_DB to point to an isolated authenticated PostgreSQL database.

That environment variable is not configured. A passwordless local postgres probe was rejected with no password supplied.

Therefore real migration application and PostgreSQL integration tests are not claimed as passed in this report.

No database authentication settings were weakened and no PostgreSQL password or secret was read or modified.

## Phase status

Phase 1 is CODE COMPLETE and AUTOMATED/FORENSIC COMPLETE.

It is not marked fully runtime-verified until an authenticated isolated PostgreSQL test database is available and the migration/integration suite passes against it.

Phase 2 must not begin by bypassing this database gate; once the test DB is available, Phase 1 runtime verification should be the first gate before transactional frontend attachment.
