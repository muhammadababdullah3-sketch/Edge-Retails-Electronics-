# CENTRAL-ONE server deployment and reconstruction status

**Document ID:** `CONE-DOC-T29-DEPLOY-001`
**Status:** Development reconstruction instructions only; production deployment is not commissioned.
**Candidate:** This document is maintained with the Central source candidate. Reconfirm its source hashes before use.

## Scope

CENTRAL-ONE is the server-side control plane. The ASP.NET Core API host serves the portal from `wwwroot` and maps controller routes. The host registers PostgreSQL persistence, a protected development signer, disk-backed object storage, and hosted outbox, storage-reconciliation, and activation-secret-cleanup services. See `src/EdgeRetails.Central.Api/Program.cs` and the registered service implementations for the actual behavior.

These instructions do not authorize production infrastructure, real customer data, production keys, live issuance, provider cutover, or a production migration. No fixed port, database name, password, public endpoint, policy value, or production cryptographic profile is supplied here.

## Development prerequisites

- Use a dedicated Central worktree and the scoped `EdgeRetails.Central.slnx` and Central project files. Do not run the mixed POS solution for this runbook.
- Use an owned, disposable PostgreSQL 18 cluster on loopback with a non-default port. Before connecting, attest the live process, executable/version, data directory, postmaster identity, ownership marker, database, roles, loopback listener, and minimum runtime privileges. The application guard and connection interceptor are authoritative; a port or marker copied from an old run is not proof of ownership.
- Provide configuration explicitly. `Program.cs` requires `CENTRAL_DATABASE_CONNECTION_STRING`, `CENTRAL_SIGNER_KEY_STORE_ROOT`, and the target-guard environment consumed by `DatabaseTargetGuard` / `OwnedPostgresConnectionInterceptor`. The signer root is protected development storage bound to the current Windows user; it is not a production key service.
- The target guard requires freshly attested `CENTRAL_PG_PORT`, `CENTRAL_PG_DATABASE`, `CENTRAL_PG_ROLE`, `CENTRAL_PG_MIGRATION_ROLE`, `CENTRAL_PG_PID`, `CENTRAL_PG_BINARY`, `CENTRAL_PG_OWNERSHIP_MARKER`, and `CENTRAL_PG_DATA_DIR`. Match them to the live PostgreSQL 18 process, marker, roles, and connection before launch; never reuse historical identifiers. Configure `CENTRAL_OBJECT_STORAGE_ROOT` and `CENTRAL_PAYMENT_STORAGE_ROOT` as separate owned development roots when those stores are in use.
- Configure the disk object-store roots and the outbox, storage-reconciliation, activation-delivery-cleanup, and sensitive-approval lifetime options using their source `FromEnvironment` implementations. Set `CENTRAL_SENSITIVE_APPROVAL_TTL_SECONDS` only from the approved security-policy source; missing or invalid approval lifetime keeps approval requests unavailable, and the caller cannot choose a TTL. Missing or invalid operational settings must remain unavailable; do not substitute sample secrets or guessed bounds.
- Before enabling authentication, installation, upload/download, independent approval, activation issuance, or hosted maintenance workflows, resolve every policy and bound listed in the **Additional policy and storage gates** table in `OPERATIONS_AND_RUNBOOK.md` from its approved source. That table records exact variable names and source-defined rejection behavior; it does not authorize activation or supply production values. Keep delivery/signing secrets in the authorized secret store.
- `effective040` marks `G-POLICY`, `G-SECURITY-CONFIG`, `G-COMMERCIAL-CONFIG`, `G-CRYPTO-PROFILE`, and `G-STORAGE-CONFIG` as explicit dependencies not fabricated complete; its lifecycle values for lease, offline grace, rollback tolerance, token TTL, retention, and after-extension policy are null. Parser bounds in source are validation limits, not approved values. Keep affected operational workflows unavailable until the applicable authorized configuration exists.
- Keep credentials in the authorized local secret store. Never put passwords, connection strings, private keys, activation tokens, or customer data in this document, command history, or evidence exports.

## Build and local start

From the Central candidate root, after the owned development configuration and database target have been independently attested:

```powershell
dotnet build EdgeRetails.Central.slnx -c Release
dotnet run --project src/EdgeRetails.Central.Api/EdgeRetails.Central.Api.csproj -c Release
```

Apply migrations only against the same attested disposable Central database using the repository's approved migration workflow and migration identity. The application target guard is not permission to connect to any other database. Preserve the actual build output, migration result, configuration-source identifiers, and candidate hashes in the current execution evidence folder. Do not include secret values.

The migration chain applies the source-defined runtime-role privileges through `CentralRuntimeRolePrivileges`. It requires separate explicit `CENTRAL_PG_ROLE` and `CENTRAL_PG_MIGRATION_ROLE` identities, runs as the migration role, and grants the runtime role schema usage, table SELECT/INSERT/UPDATE, DELETE only for nonce and expiring download-grant cleanup, and sequence USAGE/SELECT. It denies audit UPDATE/DELETE/TRUNCATE, table ownership, schema creation, and inherited migration authority. Missing or invalid role configuration fails the migration. Verify the resulting runtime privileges on the owned disposable target before starting the host.

For owned-development installation, update, rollback, and restore sequencing, follow `OPERATIONS_AND_RUNBOOK.md`. Never reverse a migration by hand: validate a candidate on a fresh disposable target, and recover a failed owned instance into a separate target from a verified bundle. The supported restore rehearsal is `scripts/central/Invoke-CentralDisposableRecoveryRehearsal.ps1`; its results do not establish production backup cadence, retention, off-site storage, production key custody, or a production recovery-point objective.
## Verification and limits

Use controller attributes and `CENTRAL_SERVER_AUTH_ROUTE_MATRIX_*.json` from the current execution run to resolve actual routes and authorization; this document intentionally does not advertise stale or unverified endpoints. Verify portal assets from the built candidate and exercise authenticated API behavior against the owned database. Health observations must come from runtime evidence, not a hard-coded green result. Changes233–235 record the latest current-candidate large-object capture/restore, actual full-wrapper storage-copy interruption, and clean same-bundle recovery sequence: separate PostgreSQL 18 targets, 38 Central tables, 23 migrations, 59 storage references including a 100 MiB ciphertext object, protected signer/trust proof, runtime least privilege, and idempotent hosted-worker replay. Change080 records recovery rejection/no-effect controls, and Change156 records a database-restore interruption rollback control. Change236 proves only the selected 21-to-23 schema transition; it is not certification of every production upgrade or rollback. See `OPERATIONS_AND_RUNBOOK.md` for scope limits. These scoped controls do not replace joined browser recovery or positive recovery verification. Consult the append-only execution ISSUE_REGISTER.md for current scope; recovery evidence does not replace the required eight authenticated journeys, full source reconciliation, platform runner proofs, or final semantic review.

The development signer is DPAPI CurrentUser protected storage, not an HSM. A local disk object store is not a commissioned object-storage provider. A successful build or local test run does not establish production readiness, operational policy approval, external delivery, or live cutover. The execution run folder's append-only `ISSUE_REGISTER.md` and Changes233–236 record the latest scoped recovery/interruption/upgrade evidence; consult the current register for open gates. Older `CHECKPOINT.md` entries are historical snapshots and must not be treated as current candidate validation.
