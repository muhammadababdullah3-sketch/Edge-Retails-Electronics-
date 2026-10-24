# CENTRAL-ONE development operations runbook

**Status:** Owned local development only. Production operations and live cutover are not commissioned.

## Operating boundary

Run Central from its isolated worktree and scoped solution. Keep the POS checkout, shop databases, POS services, production credentials, customer communications, and live providers outside this procedure. Use labelled synthetic fixtures and an owned disposable PostgreSQL 18 loopback target only.

## Before starting the API

1. Read the current execution checkpoint and verify candidate path, branch, HEAD, source overlay, and active writer ownership.
2. Attest the actual PostgreSQL executable/version, PID, command line, data directory, `postmaster.pid`, ownership marker, loopback listener, database, runtime/migration roles, and privileges. Set the target-guard variables from those live observations; never reuse a historical PID, port, or marker.
3. Provide the explicit database connection and protected signer-store root required by `src/EdgeRetails.Central.Api/Program.cs`. Configure disk-storage and hosted-service options from their source `FromEnvironment` parsers. Set `CENTRAL_SENSITIVE_APPROVAL_TTL_SECONDS` only from the approved security-policy source; the approval-request route returns unavailable when it is absent or invalid, and no request body can choose its lifetime. Secret values stay in the local secret store and out of logs/evidence.
4. Set all four admin authentication lifetimes explicitly: `CENTRAL_ADMIN_AUTH_CHALLENGE_TTL_SECONDS`, `CENTRAL_ADMIN_AUTH_SESSION_IDLE_TIMEOUT_SECONDS`, `CENTRAL_ADMIN_AUTH_SESSION_ABSOLUTE_LIFETIME_SECONDS`, and `CENTRAL_ADMIN_STEP_UP_TTL_SECONDS`. Each must be a positive integer number of seconds. Missing or invalid values disable login/MFA completion and step-up; session validation and grant validation fail closed. Do not copy the synthetic test fixture values into an operational environment without the required policy approval.
5. Fail closed if a required target/configuration value is missing, ambiguous, stale, or mismatched. Do not bypass `DatabaseTargetGuard`, relax role privileges, or point Central at an operational database.
6. Build and start only the scoped Central projects using the commands in `SERVER_DEPLOYMENT_RUNBOOK.md`. Preserve raw outputs and candidate hashes in the active execution run.

## Observations and incident response

- Treat a missing, stale, or failed hosted-service observation as unavailable/unknown or degraded according to its source implementation. Do not infer health from process existence or a successful portal response.
- Stop only a process whose executable, command line, owner, and target have been freshly verified as belonging to the current disposable run. If ownership cannot be proved, stop and escalate; do not use broad process-name termination.
- Preserve logs and evidence before changing an owned fixture. Do not delete directories, clusters, object roots, or databases as generic cleanup. Cleanup requires a separate exact-path and resource-ownership check.
- Do not enable production policy from synthetic test values. `effective040` approvals and configuration gates remain authoritative for operational activation.

## Current acceptance note

This runbook is not a production operator handoff. Use `CURRENT_CHECKPOINT_029.json` and the immutable run artifacts for the current proof and explicit gaps. The Central acceptance still requires, among other items, full browser journeys, route/DTO reconciliation, the Windows link/reparse fixtures, signer/trust recovery plus worker replay, final regression, distinct semantic review, and evidence readback. A passing focused test does not close those gates.
