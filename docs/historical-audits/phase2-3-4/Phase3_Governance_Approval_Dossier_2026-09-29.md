# Edge Retails Phase 3 — Governance Approval Dossier

**Prepared:** 2026-09-29  
**Purpose:** Governance review and approval record for completed Phase 3A/3B work and the current Phase 3C hold.  
**Overall disposition:** **PHASE 3 IN PROGRESS — NOT CLOSED**

## 1. Decision requested

Governance is asked to:

1. Review and acknowledge the evidence-backed closure of **Phase 3A (Production Database)** and **Phase 3B (Release and Runtime Cutover)**.
2. Confirm that Phase 3C may resume when the authorized user can authenticate directly in the installed Desktop. Authentication must remain user-entered; this dossier does not request or record a PIN.
3. Keep Phase 3 overall open until Phase 3C, the required Phase 3D regression gates, and Phase 3E freeze and fresh independent certification have each passed.

No Phase 4 work is included in this request. Phase 1 and Phase 2 remain certified and locked. No production migration or data repair is requested by this dossier.

## 2. Executive status

| Area | Status | Governance meaning |
|---|---|---|
| Phase 1 / Phase 2 | Certified and locked | No reopening or changes in this continuation. |
| Phase 3A — database closure | **PASS — certified and locked** | PostgreSQL 18 evidence, migration history, forward-only migration integrity, inventory relationship preservation, backup/restore, and fresh independent review are documented. |
| Phase 3B — release/runtime cutover | **PASS — certified and locked** | Release 1.0.5 was installed and verified; credential rotation, Server readiness, loopback binding, Worker startup and bounded runtime observation passed. |
| Phase 3C — installed Desktop smoke | **OPEN — user action required** | Desktop reached the account/PIN screen through the Shop Server. Authentication and the authenticated catalog/POS checks were not completed. |
| Phase 3D — full regression | **NOT STARTED** | All required regression and protected Phase 1/2 suites remain outstanding. |
| Phase 3E — freeze and final certification | **NOT STARTED** | Requires Phase 3D PASS, a frozen checkpoint, and a new independent read-only final certifier. |
| Phase 4 | **NOT STARTED** | Explicitly outside current scope. |

There are no claims here that Phase 3 as a whole is certified, that Phase 3C passed, or that a final regression/freeze/certification occurred.

## 3. What was completed

### 3.1 Phase 3A — production database closure

- Confirmed the production target as `edge_retails_prod` on `127.0.0.1:5432`, running PostgreSQL 18.6 as the primary. The certification record identifies the application provider as **PostgreSQL 18 / Npgsql**.
- Verified the production migration history against the current source inventory: 18 ordered migrations, with `20260929100000_Phase3LegacyCategoryUpgradeRecovery` latest and applied.
- Preserved the released historical migration. The category-upgrade repair was implemented as an append-only forward migration; its `Down()` is explicitly unsupported. The allowed Path B semantic comparison found unchanged released/current Up, Down, and target-model behavior for the historical migration. Historical source-byte archival was unavailable and is recorded as an evidence limitation, not represented as byte-level proof.
- Verified InventoryUnit relationship structure without seeding production data: counts were 0 in the pre-migration backup, operational database, and post-migration backup; all 18 named FK relationships retained the expected restrictive behavior; 54 orphan checks were zero. The empty production dataset means this is structural-preservation evidence, not a populated-row survival demonstration.
- Verified the post-migration backup archive and restored it into an isolated PostgreSQL 18 instance. The isolated instance was stopped and its owned temporary workspace cleaned up.
- Completed fresh independent read-only review (`PHASE3A-CERT-2`) with PASS. The Phase 3A record reports zero critical, high, blocked, and unexecuted Phase 3A gates.

### 3.2 Phase 3B — release installation and runtime cutover

- Corrected two deployment/verification harness defects discovered during execution: optional manifest-field access under PowerShell StrictMode, and interactive `Invoke-WebRequest` parsing prompts in the health check. These are recorded as **HARNESS_CORRECTION**; no assertion, concurrency, or skip criteria were weakened.
- Independently reviewed the existing Release 1.0.5 package manifest, binary versions/hashes, and Worker Windows Service dependency/lifetime configuration.
- Rehearsed the approved credential rotation against an isolated PostgreSQL 18 instance. The evidence records rejection of the wrong and old credentials, acceptance of the new credential, preserved role attributes/memberships, consistent configuration aliases, and protected ACL. No secret is included in the evidence.
- Ran the controlled production Release 1.0.5 cutover under normal Windows elevation using the existing approved deployment assets. The installer exited successfully; installed/published hashes matched; the production database configuration hash was unchanged across installation.
- Verified the installed Shop Server was Running and Automatic, used the installed binary, had the configured recovery policy, and listened only on `127.0.0.1:7150`. Readiness returned HTTP 200 with `Ready`, `canConnect=true`, no pending migrations, and maintenance state `Normal`.
- Started the Worker after Server readiness. It remained Running/Automatic through a 90-second observation; its authenticated database activity and fresh heartbeat were observed, with zero runtime error events. Outbox counts were zero before cutover, before Worker startup, and after observation.
- Completed the bounded installed Desktop process/window startup-and-close smoke. This is lifecycle evidence only; it is not the authenticated business-flow smoke required by Phase 3C.

### 3.3 Phase 3C — partial installed Desktop evidence

- Verified the installed Desktop executable is Release 1.0.5 and matches the approved hash in the evidence report.
- An initial launch surfaced that the long-running automation process lacked an existing terminal-authentication value that was present in the Windows User environment. The dialog was closed normally. The Desktop was relaunched with the existing user-scoped value passed to that child process without printing or recording it; no credential was generated, changed, guessed, or sent in chat.
- The installed Desktop reached its account/PIN screen and was observed connecting to `127.0.0.1:7150`; no Desktop connection to PostgreSQL port 5432 was observed. The production composition uses the remote application gateway.
- The user stated they could not sign in at that time. No PIN was entered by the agent. Consequently, authenticated navigation, catalog/search, POS bootstrap, and authenticated close/restart checks remain open. The installed app may still be open; its last recorded login-screen observation is in the Phase 3C evidence report.

## 4. Verification record and evidence locations

### 4.1 PostgreSQL and database evidence

| Evidence | Result | Record |
|---|---|---|
| Production provider / migration history | PostgreSQL 18 / Npgsql; 18/18 applied | [Phase 3A certification](Phase3A_Production_Database_Certification_2026-09-29.md) |
| Historical migration integrity | PASS via approved semantic Path B; repair is append-only | [Immutability closure](Phase3A_Immutability_Closure_2026-09-29.md) |
| InventoryUnit structure preservation | PASS; A/B/C counts 0/0/0; 18 restrictive FKs; zero orphan checks | [Inventory preservation closure](Phase3A_Inventory_Preservation_Closure_2026-09-29.md) |
| Raw query results and command/exit ledger | Retained | [Raw JSON](Phase3A_Inventory_Preservation_Raw_2026-09-29.json), [commands](Phase3A_Inventory_Preservation_Commands_2026-09-29.txt) |
| Post-migration backup | SHA-256 `FF2682D091C324EE41F241E4315136B30F4DA6C66E0EC39EFF66864C9834A606`; archive validation and isolated restore recorded PASS | `C:\Users\muham\AppData\Local\EdgeRetails\Production\backups\post_phase3a_migration_20260929_20260929_184442.dump` |

### 4.2 Release and runtime evidence

| Evidence | Result | Record |
|---|---|---|
| Phase 3B closure and cutover ledger | PASS — certified and locked | [Phase 3B runtime closure](Phase3B_Runtime_Closure_2026-09-29.md) |
| Controlled cutover result | PASS; PostgreSQL 18 / Npgsql; readiness Ready; Worker observation 90 seconds; runtime errors 0 | `artifacts\phase3b-cutover-20260929-201201\phase3b-result.json` |
| Installed service checks | All recorded Server deployment gates PASS, including loopback-only binding and database readiness | `artifacts\phase3b-cutover-20260929-201201\server-health.txt` |
| Release installation and hashes | Installer exit 0; installed hashes and configuration preservation recorded | `artifacts\phase3b-cutover-20260929-201201\release-1.0.5-install.json` |
| Credential rotation | Production result recorded without plaintext secret | `artifacts\phase3b-cutover-20260929-201201\credential-rotation.json` |
| Isolated rotation rehearsal | PASS on PostgreSQL 18 | [Rehearsal JSON](Phase3B_Rotation_Rehearsal_Result.json) |
| Desktop lifecycle-only smoke | PASS; process/window opened and closed normally | `artifacts\phase3b-cutover-20260929-201201\desktop-start-close-smoke.txt` |

### 4.3 Desktop smoke evidence

[Phase 3C installed Desktop smoke report](Phase3C_Installed_Desktop_Smoke_2026-09-29.md) contains the binary hash, both launch outcomes, process-to-loopback observation, API-only composition evidence, and the exact remaining authenticated checks.

## 5. Production and workspace change boundary

- Production changes performed under the Phase 3B controlled cutover: approved Release 1.0.5 installation, approved credential rotation/configuration protection, and Server/Worker service operation. The Phase 3A forward migration had already been applied and independently verified before the current 3B runtime lock.
- No Phase 3B production migration, production data seeding, outbox mutation, LAN listener, or inbound LAN firewall rule was performed.
- No application-source, migration, or test edits were made during Phase 3B/3C. Deployment scripts/wrappers and verification/evidence materials were corrected or added as documented above. The broader Phase 3 implementation changes already present in the workspace have been retained for the not-yet-run final regression; this dossier does not certify those changes.
- No Git commit, push, reset, or clean was performed. At dossier preparation, the workspace remained on branch `main`, HEAD `1fb5d3b1f66b1cf8db23e0fe10eee030477545c7`, with 316 status entries (169 modified tracked paths, 147 untracked paths). The initial controlled-closure record noted 304 entries before the closure/certification evidence additions. This is an intentionally preserved dirty workspace, not a clean or frozen release checkpoint.
- Secret values were not written into reports, command lines, or this dossier.

## 6. Remaining gates and exact continuation point

1. **Phase 3C:** The authorized user enters their existing PIN directly into the installed Desktop. Then complete and record authenticated navigation, catalog/search, POS bootstrap, and clean close/restart. Do not infer these from the login-screen or lifecycle-only evidence.
2. **Phase 3D:** Execute every required final regression gate from the acceptance contract, including full regression and protected Phase 1/2 suites, with PostgreSQL 18 / Npgsql for required PostgreSQL gates. Record each command, exit code, pass/fail/skip counts, provider, and completion status. No skipped or assumed gates may count as PASS.
3. **Phase 3E:** Only after all regressions terminate successfully, freeze the workspace and produce the complete final checkpoint. Then obtain a fresh, independent, read-only final certification in a context that did not implement the work.
4. **Stop boundary:** Do not start Phase 4 before formal Phase 3 closure.

The governing acceptance contract and its gate matrix are in [Phase 3 Final Acceptance Contract](Phase3_Final_Acceptance_Contract_2026-09-29.md). The contract requires 3A–3E PASS/locked, zero critical/high/blocked/unexecuted required gates, and a separate fresh final certification before Phase 3 can be called complete.

## 7. Approval record

| Governance decision | Decision / reference | Date | Reviewer |
|---|---|---|---|
| Acknowledge Phase 3A database closure evidence | Pending |  |  |
| Acknowledge Phase 3B release/runtime closure evidence | Pending |  |  |
| Confirm Phase 3C may resume after direct authorized user authentication | Pending |  |  |
| Confirm Phase 3 remains open pending 3C, 3D, and 3E gates | Pending |  |  |

**Prepared by:** Codex execution record, based on the retained Phase 3 evidence listed above.  
**Approval scope:** Review of completed evidence and authorization to continue at the stated checkpoint. This is not a Phase 3 final certification or Phase 4 authorization.
