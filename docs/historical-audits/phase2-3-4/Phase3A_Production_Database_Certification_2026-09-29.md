# Phase 3A — Production Database Closure Evidence

**Current status: PHASE 3A CERTIFIED & LOCKED.** Fresh independent `PHASE3A-CERT-2` passed under the user's final controlled closure contract. The earlier HOLD assessment below is preserved as historical evidence and superseded by the closure addendum at the end.

**Workspace:** `C:\Users\muham\OneDrive\Desktop\Point of Sale`  
**Branch / HEAD:** `main` / `1fb5d3b1f66b1cf8db23e0fe10eee030477545c7`  
**Database provider:** `Provider = PostgreSQL 18 / Npgsql` (EF Core 10.0.12, Npgsql EF Core provider 10.0.3). The PostgreSQL command-line client used for direct checks and archive operations was PostgreSQL/libpq 18.6.

## Scope and restart checkpoint

Phase 1 and Phase 2 remain certified and locked. This record is limited to Phase 3A production database closure. At reconstruction, the pre-existing broad Phase 3 working tree had 303 status entries (169 modified tracked paths, 134 untracked paths); these were preserved. This Phase 3A work made no source, migration, test, deployment, or runtime-configuration edits. This evidence document is the only new workspace file from this Phase 3A continuation. No `AGENTS.md` was found at the workspace root or the applicable `docs` paths.

Completed before this record: read-only production identity/history/schema/category/index/FK checks; read-only EF inventory/model drift; current readiness GET; one fresh production custom-format backup; archive SHA/TOC/full-read validation; and an actual restore into a unique disposable PostgreSQL 18.6 cluster followed by fingerprint comparison and verified cleanup. On continuation, no `dotnet` process remained, the disposable root was absent, and no listener remained on port 55900. The operational PostgreSQL processes were present as expected. The backup was re-hashed and its size/timestamp matched the recorded values. No completed database gate was rerun.

## Execution ledger

| Gate | Scope | Evidence Required | Command/Test | Status |
|---|---|---|---|---|
| Checkpoint / scope | Preserve workspace and stop at 3A | Branch/HEAD, pre-existing dirty state, no active build/test/restore | `git status --short`; `git rev-parse`; process and disposable-port inspection | PASS; 303 pre-document entries preserved; no active `dotnet`; temp restore absent; port 55900 clear |
| Production identity | Correct configured target, PG18 primary | Database, host/port, version, recovery state, expected schemas/history | PostgreSQL 18.6 `psql`; all direct production SQL enclosed in `BEGIN READ ONLY` / `COMMIT` | PASS: `edge_retails_prod`, `127.0.0.1:5432`, `180006`, primary, transaction read-only |
| Migration history | Exact source-to-production history | Exactly 18 ordered IDs, unique, latest recovery migration | Read-only history query plus connected EF JSON migration listing | PASS: 18/18 applied, IDs match, latest `20260929100000_Phase3LegacyCategoryUpgradeRecovery` |
| Historical migration immutability | Released Phase 1 semantic migration | Historical migration behavior remains unchanged | Read-only Git/content inspection; reflected comparison of release 1.0.3, 1.0.5 and current local build | PASS for compiled Up/Down/TargetModel semantics; source-byte provenance limitation below |
| Recovery state | Append-only recovery / hold cleanup | Recovery migration present/applied; categories restored; hold table absent | Migration/source inspection and read-only database checks | PASS: migration 18 applied; two expected categories; hold table absent |
| Category/index integrity | IDs, names, flags, symbols, row versions and index | Correct rows; valid unique uppercase symbols; exact unique index | Read-only category/index catalog checks | PASS: two preserved rows, symbols `B000` / `F000`, 2 distinct, 0 invalid; index unique/valid/ready |
| FK and relational safety | Category/Product/SupplierProduct/Inventory relationships | Valid restrictive FKs and no orphan/mismatch records | Read-only PostgreSQL catalog and bounded relational checks | PASS: 39 critical FKs valid and restrictive; all 14 production orphan/mismatch checks returned 0 |
| EF alignment | Source migrations and model snapshot | Source history equals production; no pending model changes | Canonical Release `dotnet ef migrations list`; `has-pending-model-changes` | PASS: connected JSON listing applied all 18; model drift command exit 0 / no changes |
| Server DB readiness | Supporting observation only | Ready, connected, no migrations pending | HTTP GET `http://127.0.0.1:7150/api/system/ready` | PASS: HTTP 200; Ready; connected; no pending migrations; Normal maintenance |
| Fresh production backup | Snapshot current 18-migration DB | New custom archive, pg_dump exit 0, path/time/size/hash recorded | PostgreSQL 18.6 `pg_dump --format=custom`; sanitized authority loaded in memory from ProgramData config | PASS; details below |
| Archive integrity | Backup readable and complete | `pg_restore --list`, expected schema/data TOC, full archive read | PostgreSQL 18.6 `pg_restore --list`; `pg_restore --file NUL` | PASS: both exit 0; 83 table-data TOC entries; full archive read |
| Isolated restore | Real PostgreSQL 18 restore | Fresh cluster, loopback-only separate port, restore exit 0 | PostgreSQL 18.6 initdb/pg_ctl/createdb/pg_restore on disposable port 55900 | PASS: restore exit 0; identity and all listed fingerprint checks matched |
| Restore cleanup | No disposable database residue | Cluster stopped, listener gone, unique temp root removed | `pg_ctl stop/status`; `pg_isready`; listener check; guarded temp-root removal | PASS: stopped; no listener; temp root absent |
| No post-backup DB writes | Stable lock reference | No production mutation after snapshot | Operational record/process and command ledger | PASS: only authorized production operation was fresh backup; all SQL inspections read-only |
| Independent certification | Fresh read-only 40-question review | All mandatory 3A gates terminal, no blockers/unexecuted gates | Fresh `PHASE3A-CERT` subagent | HOLD: terminal review found historical source provenance unresolved and InventoryUnit representative check unexecuted |

## Production identity and migration inventory

Read-only SQL was sent to the configured production authority held in process memory; no credential or connection string is recorded here. `EDGE_RETAILS_DB` was not overriding the approved ProgramData authority. Target: database `edge_retails_prod`, host `127.0.0.1`, port `5432`; server `PostgreSQL 18.6`, `server_version_num=180006`, `pg_is_in_recovery=false`; the session reported `transaction_read_only=on`.

The production and source inventory contains these same 18 ordered migrations (all applied):

1. `20260920094824_InitialProductionBaseline`
2. `20260920111318_Sprint7Phase1SetupIdentity`
3. `20260920164958_Sprint7ProductionCutover`
4. `20260921101001_Sprint8CanonicalReportingSchema`
5. `20260921143542_Sprint8FinalProductionAlignment`
6. `20260921152602_Sprint8WarrantyAlignment`
7. `20260922120000_Phase1CanonicalSchemaAlignment`
8. `20260922135055_Phase3ProductionSafetyOutbox`
9. `20260923071510_Phase4MultiTerminalSchema`
10. `20260923095632_Phase5WarrantyClaimClientOperationId`
11. `20260923110943_Phase5WarrantyLifecycleIdempotency`
12. `20260923111027_Phase5MovementHistoryOrderingIndex`
13. `20260923125420_Phase5PurchaseHistoryOrderingIndex`
14. `20260925142150_Phase1SemanticProductIdentity`
15. `20260927062206_Phase2DurableOperationOutcome`
16. `20260927121724_Phase2OutboxLeaseFencing`
17. `20260928150000_Phase3PosPriceOverride`
18. `20260929100000_Phase3LegacyCategoryUpgradeRecovery`

Ordered migration ID SHA-256: `EA4C4F5B4CA5AACEF8D96BF96C47EEE757DCD4C10BA80767726F64FD276FD5FD`.

## Recovery and relational state

The production category rows, including IDs, names, active flags, symbols, and row versions, were:

| ID | Name | Active | Identity symbol | Row version |
|---|---|---:|---|---:|
| `01a0d3b7-eaeb-77a9-942e-8a542159976a` | Blub | true | `B000` | 0 |
| `01a0d798-26d9-74ef-8fad-daf85b0a4f2a` | Fan | true | `F000` | 0 |

Category-row SHA-256: `5DA69EA5A1C358DE39763F6E19D02B607C2874D8278693790B4AF110D4279E3A`. Identity-symbol-set SHA-256: `A79CF597EA65D152377F22269C1C5EFB654FC04A5E666BBF7315A77A51383236`. Both symbols satisfy the recovery shape, are uppercase, nonblank, non-null and unique. Category index definition: `CREATE UNIQUE INDEX ix_categories_identity_symbol ON catalog.categories USING btree (identity_symbol)`; catalog reported unique, valid, ready, correct table and column. `system.phase3_legacy_category_hold` is absent.

The bounded critical-FK inventory contains 39 valid constraints; category relationships use `ON DELETE RESTRICT`. FK inventory SHA-256: `0A4A8D4CE6E0461DA5AA6494BE1E698C06D2D017905F37DB88BB6CC7296E6E51`. Production schema inventory contains 83 base tables; SHA-256: `63B4B662BCDFCE82415E2277912FCB814981506CC1D64778423D6F8C782E692E`. All 14 production orphan/mismatch checks returned zero.

`catalog.products`, `catalog.supplier_products`, `inventory.units`, and `inventory.stocktakes` each had zero rows. Therefore no representative Product/SupplierProduct/InventoryUnit record existed to inspect. The relational integrity evidence is the valid restrictive FK catalog plus zero orphan/mismatch results; this record does not claim populated representative records were tested.

## EF and historical migration evidence

Recorded read-only commands (all run against the Infrastructure project/startup project, `EdgeRetailsDbContext`, Release):

```powershell
dotnet ef migrations list --project src/EdgeRetails.Infrastructure --startup-project src/EdgeRetails.Infrastructure --context EdgeRetailsDbContext --configuration Release --no-build --no-connect
```

Exit 0; 18 compiled IDs. Since `--no-connect` cannot report applied status, applied status comes from the following connected JSON invocation, not from that command:

```powershell
dotnet ef migrations list --project src/EdgeRetails.Infrastructure --startup-project src/EdgeRetails.Infrastructure --context EdgeRetailsDbContext --configuration Release --no-build --json
```

Exit 0; connected read-only history showed all 18 IDs applied and matching production. EF CLI/Core 10.0.12; Npgsql EF Core provider 10.0.3.

```powershell
dotnet ef migrations has-pending-model-changes --project src/EdgeRetails.Infrastructure --startup-project src/EdgeRetails.Infrastructure --context EdgeRetailsDbContext --configuration Release --no-build
```

Exit 0; “No changes have been made to the model since the last migration.” No `dotnet ef database update` was run.

The recovery migration `20260929100000_Phase3LegacyCategoryUpgradeRecovery` is a new forward migration; inspection found deterministic legacy restoration and shape/uniqueness guards, and a `Down()` that throws `NotSupportedException` rather than reversing this recovery. It is applied in production.

Historical migration equivalence inspection compared reflected `UpOperations`, `DownOperations`, and `Migration.TargetModel` from Release 1.0.3 and 1.0.5 Infrastructure assemblies with the current local Release build. Operation counts were 13 Up and 11 Down in each; canonical Up hash `2147CA02302244107FFD9322AB243513124E2613DF56A499635D4DCF7473EFF7`; Down hash `BAF89DF4C4FE076B971DB831ADC2FDEFB6A76AC494C1F4BC5C17E05568C65D67`; target model hash `1AF5C62E4A78FDCF33FABFB35D8E7B1BA86AB8A2F1EE9D36C8C08C7F86892BFB`. Current source SHA-256 values are migration `.cs` `307BA075A56E683457321C479BD15C646B8AD10727AF0287014F8B01B3247842` and Designer `068279861318B4CF92D2D5520930B79DEAD1B0CA0A3E01C7996E4AFA78D6BD45`; current PDB document checksums match these source hashes.

**Provenance limitation for independent review:** those historical migration files are untracked in this checkout and absent from `HEAD`, available Git history, and tags. The release DLLs do not contain source-linked PDBs/manifests binding their bytes to source files. Thus compiled migration behavior is demonstrably equivalent across the compared release/current assemblies, but an exact source-byte-to-published-1.0.3 provenance claim cannot be made from this workspace. The certifier must explicitly decide whether the semantic equivalence suffices for the required immutability gate; do not silently upgrade this evidence to a byte-for-byte proof.

## Fresh production backup

| Field | Value |
|---|---|
| Path | `C:\Users\muham\AppData\Local\EdgeRetails\Production\backups\post_phase3a_migration_20260929_20260929_184442.dump` |
| Created | `2026-09-29 18:44:43.4807003 +05:00` |
| Source | `edge_retails_prod`, PostgreSQL 18.6, 18 migrations through `20260929100000_Phase3LegacyCategoryUpgradeRecovery` |
| Tool | PostgreSQL 18.6 `pg_dump`, custom format |
| Exit / size | Exit 0 / 262,684 bytes |
| SHA-256 | `FF2682D091C324EE41F241E4315136B30F4DA6C66E0EC39EFF66864C9834A606` |
| TOC | `pg_restore --list` exit 0; archive identifies `edge_retails_prod`, PG 18.6; `system.__ef_migrations_history` and `catalog.categories` table data present; 83 table-data entries |
| Full archive read | `pg_restore --file NUL` exit 0 |

The only production-side operation authorized and performed in Phase 3A was creation of this backup. Direct SQL verification used explicit read-only transactions. Credentials were loaded from the approved ProgramData configuration in process memory and are excluded from this document. The backup’s intentionally descriptive filename repeats the date component; it has not been renamed or changed.

## Disposable PostgreSQL 18 restore and comparison

The archive was restored with PostgreSQL 18.6 tools into a fresh unique temporary cluster bound to loopback on port 55900, with isolated disposable credentials and an independently checked `data_directory`. The disposable target reported PostgreSQL 18.6 (`180006`) and primary state. `createdb` and `pg_restore --exit-on-error --single-transaction --no-owner --no-privileges` completed with exit 0. Restore checks used read-only SQL.

| Fingerprint | Production | Restored | Result |
|---|---|---|---|
| Ordered migration IDs SHA-256 | `EA4C4F5B4CA5AACEF8D96BF96C47EEE757DCD4C10BA80767726F64FD276FD5FD` | same | MATCH |
| Category rows SHA-256 | `5DA69EA5A1C358DE39763F6E19D02B607C2874D8278693790B4AF110D4279E3A` | same | MATCH |
| Identity symbol set SHA-256 | `A79CF597EA65D152377F22269C1C5EFB654FC04A5E666BBF7315A77A51383236` | same | MATCH |
| Critical FK inventory (39) SHA-256 | `0A4A8D4CE6E0461DA5AA6494BE1E698C06D2D017905F37DB88BB6CC7296E6E51` | same | MATCH |
| Canonical schema inventory (83 tables) SHA-256 | `63B4B662BCDFCE82415E2277912FCB814981506CC1D64778423D6F8C782E692E` | same | MATCH |
| Category index | same unique/valid/ready definition | same | MATCH |

Restored database had 18 migrations, latest recovery migration, two expected categories, no hold table, no invalid/duplicate symbols, all checked category FKs valid, and zero across all 12 bounded restore orphan checks. The same four relationship tables listed above were empty in the restore. `pg_ctl` stopped the disposable cluster (exit 0); subsequent status confirmed no running cluster; `pg_isready` reported not accepting; listener count on port 55900 was zero; guarded removal succeeded and the exact temporary root no longer exists. No operational PostgreSQL service or data directory was used for restore or cleanup.

The initial custom TOC-count parser used an incorrect pattern and rejected its own first parse; the archive itself was not changed. The corrected parser confirmed 83 table-data entries, consistent with the readable TOC and 83-table schema inventory. An earlier exploratory history query also used an unquoted case-sensitive column name and failed; it was corrected to the quoted EF column and no write-capable SQL ran. These were inspection-command corrections, not database failures.

## Sanitized command result ledger

| Command | Exit Code | Passed | Failed | Skipped | Database Provider | Completion Status |
|---|---:|---|---|---|---|---|
| PostgreSQL 18.6 read-only identity/history/schema/category/index/FK/orphan query (`BEGIN READ ONLY` … `COMMIT`) | 0 | Target, 18 history, category/index/FK and bounded orphan checks | 0 | 0 | PostgreSQL 18 / Npgsql | PASS |
| `dotnet ef migrations list ... --no-connect` (command shown above) | 0 | 18 compiled source IDs | 0 | Applied-state by design | PostgreSQL 18 / Npgsql context; disconnected inventory | PASS, paired with connected JSON |
| `dotnet ef migrations list ... --json` (command shown above) | 0 | All 18 applied and exact match | 0 | 0 | PostgreSQL 18 / Npgsql | PASS |
| `dotnet ef migrations has-pending-model-changes ...` (command shown above) | 0 | No model drift | 0 | 0 | PostgreSQL 18 / Npgsql | PASS |
| `GET http://127.0.0.1:7150/api/system/ready` | 0 / HTTP 200 | Ready, connected, no pending migrations | 0 | 0 | PostgreSQL 18 / Npgsql (supporting runtime observation) | PASS |
| `pg_dump --format=custom` to the backup path | 0 | Fresh archive created | 0 | 0 | PostgreSQL 18.6 / libpq | PASS |
| `pg_restore --list` | 0 | TOC readable, 83 table-data entries | 0 | 0 | PostgreSQL 18.6 / libpq | PASS |
| `pg_restore --file NUL` | 0 | Full archive read | 0 | 0 | PostgreSQL 18.6 / libpq | PASS |
| Disposable PostgreSQL 18.6 `initdb` / `createdb` / `pg_restore --exit-on-error --single-transaction` | 0 | Actual isolated restore and data/schema comparison | 0 | 0 | PostgreSQL 18.6 / libpq | PASS |
| Disposable `pg_ctl stop`, stopped status, readiness/listener check, guarded temp-root removal | 0 for stop/removal; expected stopped/not-ready statuses | No cluster, listener, or temp root remains | 0 | 0 | PostgreSQL 18.6 / libpq | PASS |
| Fresh `PHASE3A-CERT` | Not run yet | — | — | — | PostgreSQL 18 / Npgsql | PENDING — required before lock |

The initial incorrect parser/probe attempts described above were non-mutating; their corrected checks passed. No test suite, build, migration apply, service operation, Worker action, Desktop action, or Phase 3B+ work was run as part of this Phase 3A continuation. The prior open security finding is preserved as `OPEN SECURITY FINDING PRESERVED`; no credential is reproduced or rotated.

## Independent PHASE3A-CERT result

The fresh read-only certifier independently rechecked the current production target/history/category/index/FK fingerprint in a `BEGIN READ ONLY` transaction, readiness, backup SHA/size/TOC, and scoped Git status. Those commands terminated successfully. It reviewed but did not repeat the already completed EF model-drift check or isolated restore.

An earlier independent-review attempt started an unnecessary custom reflection traversal and consumed excessive memory without returning a result. That read-only PowerShell process was stopped; it had no terminal exit code and no result is credited to it. No repository or database mutation occurred. A separate fresh bounded certifier then completed the independent review recorded below.

| Command | Exit Code | Passed | Failed | Skipped | Database Provider | Completion Status |
|---|---:|---|---:|---:|---|---|
| Bounded current PostgreSQL fingerprint query (`BEGIN READ ONLY` … `COMMIT`) | 0 | Target, PostgreSQL 18.6 primary, 18 exact IDs, categories, index, category FKs | 0 | 0 | `Provider = PostgreSQL 18 / Npgsql` | PASS |
| `GET http://127.0.0.1:7150/api/system/ready` | 0 / HTTP 200 | Ready, connected, no pending migrations | 0 | 0 | `Provider = PostgreSQL 18 / Npgsql` | PASS |
| `Get-FileHash -Algorithm SHA256` for the recorded backup | 0 | 262,684 bytes; hash matches | 0 | 0 | N/A | PASS |
| `pg_restore --list` for the recorded backup | 0 | Readable PG 18.6 TOC; 83 table-data entries | 0 | 0 | PostgreSQL 18.6 / libpq | PASS |
| Scoped `git status --short --branch` and migration `git diff --name-status` | 0 | Current state captured; historical source files untracked/no tracked diff | 0 | 0 | N/A | PASS |

### Certifier answers to the 40 required questions

1. Yes — target is `edge_retails_prod`.
2. Yes — PostgreSQL 18.6 (`180006`).
3. Yes — primary; query/readiness succeeded.
4. Yes — expected IDs match; none missing or unexpected.
5. Yes — 18 migrations.
6. Yes — latest is `20260929100000_Phase3LegacyCategoryUpgradeRecovery`.
7. Yes — source inventory and production history match.
8. **Not fully certifiable** — release/current compiled semantics match, but published historical source provenance is absent.
9. Yes — recovery is migration 18 and forward-only per source inspection.
10. Yes — legacy hold table absent.
11. Yes — Blub (`B000`) and Fan (`F000`) present.
12. Yes — both IDs preserved.
13. Yes — no invalid symbols.
14. Yes — symbols uppercase.
15. Yes — two symbols, two distinct values.
16. Yes — `ix_categories_identity_symbol` present.
17. Yes — index unique.
18. Yes — index valid, ready, and on the expected table/column.
19. Yes — category FKs valid and restrictive.
20. Yes — bounded orphan checks returned zero.
21. Structural pass; no representative product records exist (`catalog.products` empty).
22. Structural pass; no representative supplier-product records exist (`catalog.supplier_products` empty).
23. **Unexecuted** — `inventory.units` empty; no representative InventoryUnit relationship could be checked.
24. Yes — recorded EF model-drift command found no pending changes.
25. Yes — readiness HTTP 200 with no pending migrations.
26. Yes — fresh post-migration backup recorded.
27. Yes — SHA-256 recorded and independently matches.
28. Yes — TOC readable, 83 table-data entries.
29. Yes — actual isolated PG 18.6 restore recorded.
30. Yes — restored and production migration-history fingerprints match.
31. Yes — restored and production category fingerprints match.
32. Yes — restored index and FK fingerprints match.
33. Yes — disposable cluster, listener, and temp root removed.
34. Yes — credentials excluded; prior security finding remains open.
35. Yes — production operation was the authorized backup; SQL checks were read-only.
36. Yes — no Worker, Desktop, or Phase 3B+ actions in this Phase 3A execution.
37. No — no Critical production DB blocker found.
38. No — no High production DB blocker found.
39. Yes — historical migration immutability gate is unresolved due missing source-byte provenance.
40. Yes — representative InventoryUnit validation remains unexecuted because its table has no rows.

## Final Phase 3A matrix

| Gate | Result |
|---|---|
| Production DB identity / PostgreSQL 18 primary | PASS |
| Migration history / 18 IDs / latest recovery migration | PASS |
| Source-to-database migration inventory | PASS |
| Historical migration immutability | **HOLD — compiled semantics match, but released source-byte provenance is unavailable** |
| Recovery migration / hold cleanup / category preservation | PASS |
| Category symbols / unique index / category FKs | PASS |
| Product and SupplierProduct relational structure | PASS for FK/orphan evidence; representative rows absent |
| InventoryUnit referential safety | **HOLD — required representative-row check unexecuted because table is empty** |
| EF model alignment / Server DB readiness | PASS |
| Fresh backup / SHA / TOC / isolated PG18 restore / fingerprint comparison / cleanup | PASS |
| Credentials excluded / production writes limited to authorized backup | PASS |
| Worker, Desktop, Phase 3B+ untouched | PASS |
| Independent `PHASE3A-CERT` | **HOLD / OPEN** |

**Counts:** Critical DB blockers = 0; High DB blockers = 0; Blocked required gates = 1; Unexecuted required gates = 1. **Phase 3A is not locked.** No production defect was found. The open security finding remains `OPEN SECURITY FINDING PRESERVED` and was not changed in this sub-phase. Do not start Phase 3B. Minimum future evidence needed: trustworthy source provenance for the released historical migration, and an approved way to verify representative InventoryUnit relationships without changing production data. No such repair or data seeding was performed here.

## Superseding closure — PHASE3A-CERT-2

The user's new final controlled closure prompt defines the actual preservation requirements and authorizes Phase3B after this lock. See `Phase3_Final_Acceptance_Contract_2026-09-29.md`, `Phase3A_Immutability_Closure_2026-09-29.md`, and `Phase3A_Inventory_Preservation_Closure_2026-09-29.md` with its raw/command evidence.

Fresh independent agent `phase3a_cert_2` returned terminal PASS. It read the complete bounded harness/raw results/commands, canonical policy, migration/staging/source/model evidence, and both closure reports. Fresh source/backup hashes matched; temp root was absent and ports 56941/55900 clear. Historical migration immutability PASS via authorized Path B: unchanged ID, released/current Up/Down/TargetModel semantic equivalence, original failing default/index still present, repair solely via guarded staging and new append-only migration. Historical source archival remains EVIDENCE_LIMITATION, not evidence of rewriting. InventoryUnit preservation PASS: verified pre/current/post count 0/0/0, 18 identical valid restrictive inbound/outbound FKs each with correct columns, 54 orphan checks zero; EMPTY PRODUCTION DATASET / STRUCTURAL PRESERVATION PROVEN. No production data was seeded.

Final Phase3A matrix: database closure PASS; historical immutability PASS; InventoryUnit preservation PASS; PostgreSQL18 PASS; 18 migrations PASS; category/index/FK integrity PASS; EF alignment PASS; backup/restore/cleanup PASS; independent PHASE3A-CERT-2 PASS. Critical DB blockers=0; High DB blockers=0; Blocked required gates=0; Unexecuted required gates=0. **PHASE3A CERTIFIED & LOCKED.** Phase3B may now begin under the new user contract. No later-phase work occurred before this lock.
