# Phase 3B runtime closure

Current status: **PHASE3B CERTIFIED & LOCKED.** Terminal production cutover PASS and focused live verification completed below. Earlier preparation ledger is retained as history. Phase3A was locked before this work began; Phase3C is now authorized. D/E remain gated.

| Gate | Scope | Evidence Required | Command/Test | Status |
|---|---|---|---|---|
| Initial runtime | Authority and upgrade safety | Approved production identity/18 migrations; queue census; service state/config | Read-only psql transaction and Windows service/config-key/ACL checks | PASS: PG180006 primary edge_retails_prod;18 migrations; all outbox states0; Server Running/Auto,Worker Stopped/Auto,LocalSystem; no DB env overrides; config aliases agree |
| Release1.0.5 | Existing package correctness | Five manifest hashes; versions; WindowsServices dependency/lifetime | Independent narrow release agent, read-only COM ProductVersion and hashes | PASS all5; MSI/bundle/EXEs1.0.5; WindowsServices10.0.12 and AddWindowsService binding present |
| Install wrapper correction | Actual StrictMode failure after installer | Hash-only spec must not throw; checks remain enforced | Replace missing-key property access with ContainsKey; parser and isolated present/absent StrictMode checks | PASS; HARNESS_CORRECTION; no assertion/skip/concurrency weakening; no republish needed |
| Noninteractive health | Observed Invoke-WebRequest confirmation prompt | Existing health gate runs without interactive parsing | Add UseBasicParsing to two HTTP reads; powershell.exe -NonInteractive -ExecutionPolicy Bypass -File scripts/Test-EdgeRetailsServices.ps1 | PASS exit0; all11 current1.0.4 gates; not credited as final1.0.5 runtime |
| Credential rotation | Remediate exposed credential and config ACL | Isolated proof, old28P01/new success, unchanged privileges/config authority, secure staged config, protected ACL | scripts/Invoke-Phase3CredentialRotation.ps1 | IN PREPARATION; no production rotation yet |
| Controlled cutover | Approved maintenance/install/startup order | Admin, verified artifacts/backup, stopped services during install, preserved rotated config, installed hashes, Server before Worker | scripts/Invoke-Phase3BControlledCutover.ps1 | PREPARED; independent review corrected existing-evidence overwrite in error handler; not executed |
| Runtime certification | Coherent1.0.5, Server/Worker health, 7150 owner, queue safety | Terminal cutover evidence plus independent review; all required counts0 | Pending controlled elevated execution | OPEN |

New root operational orchestration uses existing approved installer wrapper; it does not re-register valid services. It holds services stopped/disabled on an execution failure after maintenance begins. Credential helper is separately rehearsed; no secret is included in commands/reports. Windows elevation must use normal UAC; current shell is not elevated.

Canonical security guide specifies current authenticated user, LocalSystem and Administrators only on sensitive runtime files/directories. Current ProgramData root/config inherits broad Users access; credential step will apply that existing protection policy. Server/Worker both load ProgramData JSON; no new configuration mechanism or runtime binary is introduced.

Source/application/migrations unchanged. Existing dirty work preserved. Initial malformed DbConnectionStringBuilder property assignment caused one read-only preflight command to exit1; corrected use of set_ConnectionString completed exit0 and provided the stated identity/outbox evidence. Parser command followed by an empty Get-Process lookup returned1 despite parser PASS; the independent reviewer separately parsed all3 scripts with zero errors and proved corrected StrictMode behavior. These are not production failures or skipped final gates.

## Terminal certification

Credential helper agent was interrupted by usage limit, with no terminal rehearsal credited. Root completed its unfinished verification, correcting connection-builder getters/default port and quoted EF history column before production. Windows PowerShell isolated PostgreSQL18 rehearsal exited0: old/wrong password rejected, new password accepted, role attributes/memberships unchanged, config aliases/unrelated settings preserved, canonical ACL applied; cluster stopped/status3/listener absent/temp root removed. Evidence: `docs/Phase3B_Rotation_Rehearsal_Result.json`. Classification NEW_COVERAGE plus HARNESS_CORRECTION; no assertion/skip/concurrency weakening.

Normal Windows UAC elevation launched the reviewed `Invoke-Phase3BControlledCutover.ps1` with new evidence directory `artifacts/phase3b-cutover-20260929-201201`. Terminal JSON Status=PASS, Stage=Complete, completed `2026-09-29T15:15:43.1157228Z`. Parent launch command exit0; installer child exit0; rotation child exit0 (enforced by orchestrator); server-health child exit0. All commands terminal; no production migration or data seeding.

| Required gate | Evidence | Result |
|---|---|---|
| Credential exposure remediated | Operational rotation JSON; secure random new secret, SCRAM verifier, canonical protected config; no plaintext secret in command/evidence | PASS |
| Old credential invalid/new accepted | Actual preflight old auth success; wrong-password rejection; after rotation old password authentication rejected, new accepted | PASS |
| Privileges/config preserved | Role attributes/memberships identical; aliases agree; installer before/after config SHA256 `89E23613D3EB6AFDC510119FF87F7810C8B843B47E20BEAC4DCD3A03B9186711` | PASS |
| Canonical protected config | Independent live ACL check: inheritance disabled, only SYSTEM/Administrators/current user | PASS |
| Release1.0.5 coherent | Approved installer exit0;6 installed/published hashes equal; all3 installed EXE FileVersions1.0.5; setup SHA verified | PASS |
| Server | Running/Auto, correct installed executable/hash, recovery3x60s; PID18060 | PASS |
| Loopback ownership | Only127.0.0.1:7150, owning PID18060 matches Server | PASS |
| Readiness | HTTP200 Ready, canConnect=true, pending=false, maintenance=Normal | PASS |
| Worker | Running/Auto, installed1.0.5/lifetime DLL verified, PID5244 stable through90-second observation; recovery3x60s | PASS |
| New credential runtime use / outbox initialization | Worker PID matched PostgreSQL client port;1 authenticated pg_stat_activity session with outbox_messages query; fresh heartbeat;0 runtime error events | PASS |
| Outbox startup safety | Before cutover/before Worker/after observation total and all states0; no manual outbox mutation | PASS |

Evidence files: `credential-rotation.json`, `release-1.0.5-install.json`, `server-health.txt`, `phase3b-result.json` in the directory above. Provider = PostgreSQL 18 / Npgsql for installed runtime; rotation uses PostgreSQL18/libpq. Critical blockers=0; High blockers=0; blocked required Phase3B gates=0; unexecuted required Phase3B gates=0. **Phase3B CERTIFIED & LOCKED.** Proceed only to installed Desktop smoke, Phase3C.
