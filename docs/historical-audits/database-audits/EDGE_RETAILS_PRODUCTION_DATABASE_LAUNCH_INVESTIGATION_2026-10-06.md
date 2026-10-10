# Production database and frontend launch investigation — October 6, 2026

## Proven root cause

The supplied screenshot is explicitly titled **Edge Retails — UI Defect Evidence**. It shows the owned resource-only WPF probe, not normal application startup. The eight LED Bulb/Switch/Breaker/Wire/etc. rows are hardcoded `DemoRetailState` preview products in process memory, not proof of persisted shop inventory. The probe uses preview navigation with no BackendRuntime, and a read-only fixture for scanner lookup. Its data cannot be recovered by pointing a desktop shortcut at a different PostgreSQL database.

The previously delivered October 5 shortcut pointed to a Debug UI candidate in `artifacts/DesktopVisualReview_2026-10-05`, with empty arguments. The installed Server and Worker are Windows services running from `C:/Program Files/Edge Retails/server` and `worker`. Normal Desktop `BackendRuntime.CreateFromEnvironment` defaults to HTTP `127.0.0.1:7150`; it does not open an independent desktop database. No current process EDGE_RETAILS_SERVER_URL/EDGE_RETAILS_DB override was found. The configured production identity in `C:/ProgramData/EdgeRetails/config.json` is host 127.0.0.1, port 5432, database `edge_retails_prod`. Both connection keys in that file resolve to that same identity. No per-user config file was present at the inspected location.

## Read-only evidence

The latest user request authorized investigating the real frontend/database, superseding the earlier operational-database inspection prohibition for this investigation. Database checks used Npgsql, explicit READ ONLY transactions, a five-second statement timeout and rollback. No secret was printed; no row content was exported.

| Database | Products | Lots | Units | Sales | Purchases |
|---|---:|---:|---:|---:|---:|
| edge_retails_prod | 0 | 0 | 0 | 0 | 0 |
| edge_retails_phase6_verify | 0 | 0 | 0 | 0 | 0 |

Production additionally contains 2 customer records and 1 user. Exact SQL counts were used; initial pg_stat estimates were not treated as authoritative. The server read-only readiness endpoint reported Ready, canConnect=true, hasPendingMigrations=false, maintenanceState=Normal. This is readiness of the installed server against its current schema, not certification of the newest backend source against production.

Two database names exist, but only `edge_retails_prod` is evidenced as the configured running shop authority. A certification database's existence is not evidence of simultaneous POS authority or an alternate inventory dataset. Neither database was deleted or merged. Test isolation must remain distinct from production; the required product behavior is one active shop database shared by Server/Worker/terminals.

## Completed corrections and verification

- Fresh **Release** builds of Desktop, Server and Worker succeeded with zero warnings/errors, compiling current dependency source. The previous source compile errors were no longer present; no backend business-logic repair was made by this investigation.
- A freshly built Release unit-test assembly passed the 34 selected geometry/focus/reported-defect/exact-unit checks. These are not full backend/integration certification.
- `Server/Program.cs`: per-user config override is now allowed only in Development, preventing a production service from silently selecting a per-user database.
- `Worker/Program.cs`: the same Development-only rule, and explicit Testing database precedence aligned with Server. Tests must not accidentally select shop configuration.
- The current production Desktop Release output was copied to the stable `artifacts/production/desktop` location. Delivered Desktop.dll hash matches the fresh Release assembly. Release startup excludes the Debug preview bypass flags (`App.xaml.cs`).
- Desktop **Edge Retails - Latest Build.lnk** now targets `artifacts/production/desktop/EdgeRetails.Desktop.exe`, with empty arguments and that directory as its working directory. This uses normal server-backed startup and the configured real shop database; it does not inject demo inventory.

## Outstanding work and delivery boundary

No actual inventory exists in either checked database. The user was asked whether another approved backup/export contains their real data; demo inventory was not imported without an explicit decision about real opening stock. Product/stock imports must use canonical product units, opening costs, tracking mode and inventory posting workflows, not direct table inserts or screenshot quantities.

The running installed Server/Worker binaries were **not replaced or restarted**. The production configuration hardening is built and ready in source, but deploying the entire latest backend requires matching migration rehearsal, isolated integration certification, a recoverable backup and coordinated service update. This pass updates the actual frontend launcher; it does not falsely certify or claim a live backend upgrade. No database creation/deletion, operational data mutation, schema migration, login automation, or normal Desktop startup registration was performed for validation.

Do not use the UI probe as the operational application. Do not copy its memory fixture into production automatically. Keep one active shop authority and keep disposable certification environments outside the user-facing operational launch path.
