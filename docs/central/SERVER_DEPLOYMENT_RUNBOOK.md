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
- Configure the disk object-store roots and the outbox, storage-reconciliation, activation-delivery-cleanup, and sensitive-approval lifetime options using their source `FromEnvironment` implementations. Set `CENTRAL_SENSITIVE_APPROVAL_TTL_SECONDS` only from the approved security-policy source; missing or invalid approval lifetime keeps approval requests unavailable, and the caller cannot choose a TTL. Missing or invalid operational settings must remain unavailable; do not substitute sample secrets or guessed bounds.
- Keep credentials in the authorized local secret store. Never put passwords, connection strings, private keys, activation tokens, or customer data in this document, command history, or evidence exports.

## Build and local start

From the Central candidate root, after the owned development configuration and database target have been independently attested:

```powershell
dotnet build EdgeRetails.Central.slnx -c Release
dotnet run --project src/EdgeRetails.Central.Api/EdgeRetails.Central.Api.csproj -c Release
```

Apply migrations only against the same attested disposable Central database using the repository's approved migration workflow and migration identity. The application target guard is not permission to connect to any other database. Preserve the actual build output, migration result, configuration-source identifiers, and candidate hashes in the current execution evidence folder. Do not include secret values.

The migration chain applies the source-defined runtime-role privileges through `CentralRuntimeRolePrivileges`. It requires separate explicit `CENTRAL_PG_ROLE` and `CENTRAL_PG_MIGRATION_ROLE` identities, runs as the migration role, and grants the runtime role schema usage, table SELECT/INSERT/UPDATE, DELETE only for nonce and expiring download-grant cleanup, and sequence USAGE/SELECT. It denies audit UPDATE/DELETE/TRUNCATE, table ownership, schema creation, and inherited migration authority. Missing or invalid role configuration fails the migration. Verify the resulting runtime privileges on the owned disposable target before starting the host.
## Verification and limits

Use controller attributes and `CENTRAL_SERVER_AUTH_ROUTE_MATRIX_*.json` from the current execution run to resolve actual routes and authorization; this document intentionally does not advertise stale or unverified endpoints. Verify portal assets from the built candidate and exercise authenticated API behavior against the owned database. Health observations must come from runtime evidence, not a hard-coded green result.

The development signer is DPAPI CurrentUser protected storage, not an HSM. A local disk object store is not a commissioned object-storage provider. A successful build or local test run does not establish production readiness, operational policy approval, external delivery, or live cutover. Current finalization evidence and open gates are recorded in `CURRENT_CHECKPOINT_029.json` in the execution run folder.
