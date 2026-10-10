# Phase 3 Deployment Recovery Ledger — 2026-09-28

## Checkpoint

- Recovery is **incomplete**. The installed `EdgeRetailsServer` service was proven healthy at the HTTP/version boundary, then stopped for the exclusive migration maintenance window. `/api/system/ready` remains gated by unapplied migrations.
- Phase 4 implementation has not started. The Phase 4 master §2 requires a healthy installed Shop Server first.
- User selected the Phase 4 single-install boundary. No `MaxTerminals` or `TerminalId` authority was restored.
- The certified Phase 3 ledger remains unchanged: `docs/Phase3_Execution_Ledger_2026-09-28.md`.
- Git state is preserved on `main` at `1fb5d3b1f66b1cf8db23e0fe10eee030477545c7`; no commit, push, reset, clean, restore, or stash was used.
- PostgreSQL 18.6 is Running and the installed Server's ProgramData authority connects to the operational database. The non-secret target fingerprint is `D4E8AF3A2CD3DBF22ABF8B5594A9A741C24026E9CEA6D6A98D560E34DD1CBF74`; no credential value is recorded here.

## Execution Ledger

| Gate | Scope | Evidence Required | Command/Test | Status |
|---|---|---|---|---|
| Deployment asset inspection | Approved scripts and guide | Source inspected before use; no unsafe binding/firewall behavior | Read `Publish-Release.ps1`, `Register-EdgeRetailsServices.ps1`, `Test-EdgeRetailsServices.ps1`, `Installer_Deployment_Guide.md` | PASS |
| Release `1.0.1` | Rebuild package | Embedded MSI and bundle versions equal manifest version | `Publish-Release.ps1 -Version 1.0.1 -Output artifacts/release-1.0.1`; Windows Installer metadata query | BUILD PASS; PACKAGE VERSION FAIL (embedded `1.0.0`); package preserved and not installed |
| WiX cache correction | MSI/bundle release versioning | WiX intermediates cleaned; embedded versions asserted | Updated release script; published `1.0.2` and checked MSI/bundle metadata | PASS |
| Release `1.0.2` unit/build | Phase 3 production host and release | Release build clean; full unit suite terminal result | `Publish-Release.ps1 -Version 1.0.2 -Output artifacts/release-1.0.2` | PASS: 0 warnings, 0 errors; 691 passed, 0 failed, 0 skipped |
| Installed `1.0.2` startup | Windows Service runtime | Service reaches Running and loopback readiness succeeds | Approved Burn setup; approved registration script | FAIL: service registration succeeded, but SCM events 7000/7009 report the 30-second service-control timeout |
| Runtime root cause | Service handshake | SCM and source evidence agree | `sc.exe queryex/qc/qfailure`; read Service Control Manager events; source/package reference inspection | PASS: Server lacked `UseWindowsService` lifetime and package reference |
| Server service fix | SCM hosting only | Context-aware Windows Service integration; loopback and Desktop HTTP boundary retained | Add `Microsoft.Extensions.Hosting.WindowsServices` 10.0.12 and `builder.Host.UseWindowsService(ServiceName=EdgeRetailsServer)` | Implemented; runtime smoke pending |
| Deployment guardrails | Loopback-only Server | No inbound rule creation; script checks installed state, automatic start, restart policy, loopback listener, Desktop version endpoint, and DB readiness payload | Updated Register/Test scripts and deployment guide; PowerShell parser + targeted diff checks | PASS (static); live smoke pending |
| Release `1.0.3` | Corrected production package | Release build/unit/static audits; version consistency; required binaries | `Publish-Release.ps1 -Version 1.0.3 -Output artifacts/release-1.0.3` | PASS: 0 warnings, 0 errors; 691 passed, 0 failed, 0 skipped; Phase 1/2/3 and installer-preservation audits PASS; package version assertions PASS |
| Installed `1.0.3` | Approved production deployment | Installed binaries match Release hashes | Approved Burn setup `artifacts/release-1.0.3/setup/EdgeRetailsSetup.exe /quiet /norestart` | PASS: installer exit 0; Desktop, Worker, Server hashes match release |
| Elevated service registration | Service start and recovery | Service Running, automatic startup, 60-second restart policy | Approved registration script via elevated PowerShell | PASS before maintenance: service ran from the installed binary with automatic startup and configured recovery; both application services are now intentionally Stopped/Disabled until schema compatibility is restored |
| Installed health smoke | Real Shop DB through Server/Npgsql | HTTP 200 from `/api/system/ready`, `canConnect=true`, no pending migrations, maintenance Normal; only loopback listener | `scripts/Test-EdgeRetailsServices.ps1` | FAIL (expected fail-closed state): loopback and `/api/system/version` passed; readiness reported `canConnect=true`, `hasPendingMigrations=true` |
| Desktop API-only verification | Desktop connects to local Server | Desktop’s `/api/system/version` and `/api/system/ready` route succeeds; no direct DB fallback | Source inspection plus deployment smoke endpoint checks | Source path is HTTP-only; live smoke pending |
| Phase 4 start gate | Runtime restored before contracts/feature work | Installed Server healthy and deployment regression evidence terminal | Master prompt §2 | NOT SATISFIED; Phase 4 implementation agents remain paused |

## Current Production State

- Approved Release `1.0.3` is installed under `C:\Program Files\Edge Retails`; installed Desktop, Worker, and Server binaries match the Release publish hashes.
- `EdgeRetailsServer` registration and recovery policy were verified. It is currently **Stopped/Disabled** for the exclusive migration maintenance window; automatic startup must be restored only after schema compatibility is green.
- Before the maintenance stop, port 7150 listened only on `127.0.0.1`; `/api/system/version` returned HTTP 200 and readiness failed closed only for pending migrations.
- The registration script no longer creates an inbound firewall rule. The Server test requires all listeners on port 7150 to be loopback addresses.
- `EdgeRetailsWorker` is Stopped/Disabled. The operational outbox census was `pending=0`, `action_required=0` before the migration attempt.
- No source-built Server process or `dotnet run` production path was used.

## Production Migration Recovery

| Gate | Scope | Evidence Required | Command/Test | Exit Code | Passed | Failed | Skipped | Database Provider | Completion Status |
|---|---|---|---|---:|---:|---:|---:|---|---|
| Pending migration census | Same operational database used by installed Server | Direct history plus EF pending list | `dotnet ef migrations list ... --configuration Release --no-build`; direct `system.__ef_migrations_history` query | 0 | 13 applied identified; 4 pending identified | 0 | 0 | PostgreSQL 18.6 / Npgsql | PASS |
| Approved binary identity | Current source, Release 1.0.3 publish and installed infrastructure DLL | Byte-identical SHA-256 | `Get-FileHash` over the three `EdgeRetails.Infrastructure.dll` paths | 0 | 3 hashes matched | 0 | 0 | N/A | PASS: `6B1D63EF3C3B80E118C9E30AE5ECF04ACA1051FF618A67B6964B3FAC0C06A913` |
| Pre-upgrade production snapshot | Verified backup before schema mutation | Custom-format archive, TOC validation, full archive read, checksum | PostgreSQL 18.6 `pg_dump --format=custom`; `pg_restore --list`; `pg_restore --file NUL` | 0 | 81 table-data entries; history present | 0 | 0 | PostgreSQL 18.6 | PASS: `pre_upgrade_snapshot_20260928_213752.dump`, 255746 bytes, SHA-256 `F59486AAFD99EAABB16FB34385572907726D41794AB44896789BCB7D614D5F51` |
| Controlled forward migration | Apply only the four approved pending migrations to the same operational database | Explicit target; terminal result; transactional rollback on failure | `dotnet ef database update 20260928150000_Phase3PosPriceOverride ... --configuration Release --no-build` | 1 | 0 migrations applied | 1 | 0 | PostgreSQL 18.6 / Npgsql | FAIL SAFE: first pending migration assigns the same empty `identity_symbol` to both existing categories, then unique-index creation fails with PostgreSQL `23505` |
| Failed-attempt rollback verification | No partial DDL or history mutation | History remains 13; new column/table absent; no active client transaction | Direct PostgreSQL census after EF failure | 0 | 5 checks | 0 | 0 | PostgreSQL 18.6 | PASS: transaction fully rolled back; both application services remain stopped |
| EF model drift | Current source model versus migration snapshot, separate from database pending state | Zero pending model changes | `dotnet ef migrations has-pending-model-changes ... --configuration Release --no-build` | 0 | N/A | 0 | N/A | Npgsql tooling; no schema mutation | PASS: no model drift |
| Maintenance restart safeguard | Prevent application writes after an unexpected restart while schema is unresolved | Server and Worker Stopped/Disabled | Elevated `Stop-Service` and `Set-Service -StartupType Disabled` | 0 | 2 services | 0 | 0 | N/A | PASS; restore Automatic only after migration/readiness certification |

Exact pending chain at the failure point:

1. `20260925142150_Phase1SemanticProductIdentity`
2. `20260927062206_Phase2DurableOperationOutcome`
3. `20260927121724_Phase2OutboxLeaseFencing`
4. `20260928150000_Phase3PosPriceOverride`

The first migration is not executable against an existing database containing more than one category because it adds non-null `identity_symbol` with default `''` and creates a unique index before any backfill. The production database contains two category rows. No migration file or migration history was changed, no new migration was generated, and no retry is permitted until an approved migration-path correction is available.

## Recovery Hold and Next Gate

Do not restart either installed application service or rerun the production migration command while the failing migration is unchanged. Both services are Stopped/Disabled; PostgreSQL remains available for read-only inspection. `docs/Phase3_Continuation_Takeover_2026-09-29.md` records a new disposable PostgreSQL 18.6 / Npgsql upgrade regression: it starts from the same 13-migration state, seeds two legacy categories, verifies a backup, and exits 1 on the identical `23505` unique-index failure. Classification: `NEW_COVERAGE`; no assertion, concurrency, or skip behavior was weakened.

The next gate requires an explicitly approved correction to the released migration path, followed by a passing disposable PostgreSQL upgrade rehearsal and all required regressions. Only then restore automatic service startup, apply the approved forward migrations against the operational database, verify Server readiness and the service script, start Worker, and perform installed Desktop smoke. Keep Phase 1 and Phase 2 locked and Phase 4 paused until recovery is green.
