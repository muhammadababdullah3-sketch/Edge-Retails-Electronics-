# Master remediation / tracking / labels execution ledger

Authority: attachment `1fb5bfbf-871f-4147-9947-d1a210347891`, read completely. Resume installed 1.0.11 candidate22. Preserve all historical Phase 1/2/3 work and failures. No Phase 4, Git mutation, full population, or operational database test fixture. No applicable AGENTS.md found in workspace or checked ancestor directories.

## Preserved checkpoint

The controlled pilot stopped at Step 0: installed Settings → Backup Now failed with misleading stale-record text; history refresh returned no verified backups. No new demo suppliers/customers/products/purchases/receipts/units/sales/warranty/projects have been created. Historical pre-cutover PostgreSQL restore evidence does not satisfy the fresh pilot backup gate. The exact historical caught Server exception was not recorded by the existing controller; absent persistent backup-key settings alone do not prove its exact cause.

| Gate | Scope | Evidence Required | Command/Test | Status |
|---|---|---|---|---|
| W0 | Current authority mapping | Source/docs/tests mapping before source edits; independent bounded read-only audits | Parent backup review; tracking, labels, shared UI agents | IN PROGRESS |
| W1 | Exact installed backup failure / key lifecycle | Bounded UI reproduction, safe actual caught exception/category, endpoint/handler/Worker trace; canonical separate BMK/RK provisioning | Existing evidence/logs first; minimal diagnostics if exact cause unavailable | IN PROGRESS; historical failure preserved |
| W2 | Fresh backup certification | Archive/history/authenticated metadata/checksum/tools; actual disposable PostgreSQL 18 restore/history/schema/cleanup | Canonical installed backup and owned disposable restore | GATED; no business writes |
| W3–5 | Multi-dealer / sequences / intake | One Product, unique supplier pair, independent durable sequences/provenance; concurrency/retry/rollback/restore tests | Focused RED/GREEN; real isolated PostgreSQL 18 / Npgsql | READ-ONLY AUDIT |
| W6–7 | Committed labels / PDF / failures | Exact payload equality, authoritative product labels, post-commit output, reprint unchanged identity, failure safety | Focused RED/GREEN; PDF render and barcode decode | READ-ONLY AUDIT |
| W8–9 | Shared UI / safe backup errors | Baselines/tokens/focus/context; categorized safe errors tied to proved cause | Shared resource tests and actual installed verification | READ-ONLY AUDIT |
| W10–18 | Five-product pilot | Backup and tracking gates PASS before masters; actual UI/provenance/labels/POS/accounting/custody reconciliation | Installed normal workflows; action-time financial confirmation as required | GATED |
| W19 | Regression / deployment | Terminal results for relevant Unit/Desktop/API/PG/performance/Debug/Release/EF; reviewed approved cutover only if necessary | Record each final command and counts/provider/completion | NOT STARTED |
| Freeze / independent review | Fresh independent certification | All tasks terminal, fixed source/evidence snapshot, read-only certifier | Fresh bounded certifier after regression | NOT STARTED |
| Full inventory population | Separate next authorization | Pilot complete then human approval | None in this scope | NOT AUTHORIZED |

## Execution recording rules

Every final command: Command, Exit Code, Passed, Failed, Skipped, Database Provider, Completion Status. DB-sensitive gates explicitly identify `Provider = PostgreSQL 18 / Npgsql`. Test changes must be classified; assertion/concurrency/skip changes require explicit justification and independent review. Supporting tests never substitute for real required gates. Final certification is separate from implementation review.

## Current wave ownership

Parent: backup actual failure and canonical key lifecycle, orchestration/integration. Independent agents: tracking/intake; labels/PDF; shared UI. Wave 0 agents are read-only and write no overlapping files. Source modifications start only after current mapping is reported.

## Wave 0 current implementation mapping — before source edits

- Supplier business code is `Supplier.DealerCode`; allocator is in PartyHandlers. ProductCode is `Product.Sku`, built from company code + category identity symbol + '-' + ModelCode. Product is supplier independent. SupplierProduct owns the unique supplier/product pair and NextItemSequence. Receiving derives supplier from Purchase and records purchase item, lot and SupplierProduct provenance; serial/IMEI remain separate.
- Physical intake is purchase-item scoped. Its existing UI displays invoice/supplier name/product SKU, but not supplier code/tracking mode/remaining authority. It incorrectly treats any existing exact units as completed and disables further partial intake. This requires a focused Phase 3 UI regression proof, not a Phase 1 domain rewrite.
- MachineSequenceHighWaterService persists outside the DB, but source inspection finds silent corrupt-signature/write-error handling, read-once per-instance state, and an unused reconciliation method. These are risks requiring isolated proof; no claim that live identities were reused.
- PhysicalItemStickerDocument.BarcodePayload equals InventoryUnit.TrackingCode; EF loads existing committed units through the authenticated API. WPF prints locally after authenticated document retrieval. The fixed narrow WPF visual omits ProductCode and does not size typical long barcodes safely. Authoritative PDF export and quantity product-label contracts were absent in targeted searches. No pilot label identities exist yet.
- Input.TextBox/PasswordBox set VerticalContentAlignment, but custom PART_ContentHost ignores it. Search placeholder is independently centered. Table.Cell suppresses default focus without a keyboard focus trigger. Buttons already have keyboard focus triggers; do not misreport their absence. Release Settings user writes explicitly lack a production adapter.
- Backup Now uses authenticated POST /api/backups → CreateBackupHandler → PostgresBackupEngine. Worker schedules independently only when backup directory is configured. EnvironmentBackupEncryptionKeyProvider requires one Server process hex key; no complete canonical protected BMK / independently held RK provisioning lifecycle was found. Owner PIN Recovery signing and journal/maintenance integrity keys are separate trust domains.
- Existing controller discards exception detail; failure audit records only type. Therefore the stale-record UI message and absent persistent key sources are insufficient to assert exact caught production cause. A bounded in-memory exception diagnostic with an explicit safe-message allowlist is the next evidence path; no raw trace, memory dump, secret-bearing environment or exception text will be persisted.

## 2026-10-02 continuation census

Previous audit agents are no longer present; no build/test task is running in the process census. Installed Server PID3748 and Worker PID3764 are running; PostgreSQL service running; readiness Ready/connect=true/pending=false. Desktop is currently absent, so prior authenticated session is historical. Sign-in handoff requested without collecting PIN. Source implementation ownership: shared input resources and dedicated tests; physical intake VM/dialog and dedicated tests; printing-specific sources/contracts/engines/API/services and dedicated tests. Parent owns backup and integration. No new release or service mutation performed.

## 2026-10-02 source implementation progress — supporting gates only

| Command/Test | Exit Code | Passed | Failed | Skipped | Database Provider | Completion Status |
|---|---:|---:|---:|---:|---|---|
| BackupFailureProbe Release build (corrected nullable path / canary lifetime) | 0 | 1 build | 0 | 0 | None | PASS; initial failures preserved |
| BackupFailureProbe --self-test | 0 | 3 safe categories | 0 | 0 | None | PASS; unknown private sentinel suppressed; installed capture pending |
| MasterBackupKeyLifecycleTests initial RED | 1 | 0 | 1 | 0 | None | Expected behavioral FAIL; lifecycle absent |
| MasterPhysicalIntakeTests RED | 1 | 1 | 3 | 0 | None | Expected behavioral FAIL |
| MasterPhysicalIntakeTests + unchanged durable intake tests GREEN | 0 | 12 | 0 | 0 | None | PASS; new API projection needs real PG proof |
| MasterSharedInputTemplateTests corrected RED | 1 | 9 | 22 | 0 | None | Expected behavioral FAIL; earlier harness errors retained |
| MasterSharedInputTemplateTests final GREEN | 0 | 31 | 0 | 0 | None | PASS; intermediate 1px compact-search failure retained |
| Phase3StickerLayoutTests RED | 1 | 0 | 1 | 0 | None | Expected behavioral FAIL; ProductCode missing |
| Label layout / remote printer / existing safety GREEN | 0 | 15 | 0 | 0 | None | PASS; PDF/product printing/real PG/decode gates still pending |

Detailed command logs and TRX files are in artifacts/master-remediation-20261002/{backup-probe,backup-key,intake,ui,labels}. NEW_COVERAGE added throughout. HARNESS_CORRECTION records preserve initial missing imports/braces, search initial layout, canary lifetime and HTTP mock adaptation. No assertion/concurrency/skip weakening. Source changes have not been installed and do not establish production PASS.

Backup source foundation: explicit Windows-protected BMK ring, independent supplied Owner RK wrapping, public per-key recovery metadata, retained versions/legacy import, and version-pinned encryption/manifest authentication. Crypto/recovery/legacy focused GREEN and independent surgical review are running/queued. Provisioning/custody UI/tool, exact installed failure capture, approved release/cutover, fresh installed backup and disposable PG18 restore remain required. No operational key or demo record has been provisioned.

New required PostgreSQL runner must isolate high-water/keyring/runtime state in addition to DB and backup directory. Old rehearsal's default high-water path is unsuitable for this new scope without isolation. No operational high-water authority is to be used by test fixtures.

## Latest scope correction — server backup deferred

The active user instruction excludes server-side backup implementation from this phase and assigns it to later phases. Backup lifecycle/provisioning/error remediation implementation is stopped. Earlier uninstalled source and evidence are preserved; no operational key, configuration, service or database changes were made. The independent implementation review identified remaining ACL defects (inherit-only ACE evaluation, generic-rights masks, bootstrap parent validation); these remain unresolved and must not be represented as certified or shipped as an approved backup feature. The 16-case review RED result was 10 passed / 6 failed / 0 skipped, exit 1; later corrections have no completed GREEN evidence.

Both tracking and label agents terminated on account usage limits before their queued commands started. Parent process census confirmed no dotnet/testhost/MSBuild task running. Parent resumes the unfinished test commands sequentially from preserved source; completed proofs are not restarted. Label final Desktop command first failed compilation (CS8600/CS8604 in previously edited key-ring owner lookup), with no tests executed. A minimal nullable fail-closed compile correction is classified HARNESS_CORRECTION/build unblock, not continued backup feature implementation. No ACL feature correction is included. Phase 3 remains open; deferred backup scope and any pilot prerequisite dependency will be explicitly reported.

## Parent sequential continuation — 2026-10-02

Desktop label final GREEN: exit 0, 19 passed / 0 failed / 0 skipped, no DB. MasterLabelPdfTests: 5 passed / 0 failed / 0 skipped. MasterMachineHighWaterTests RED: 1 passed / 4 failed / 0 skipped; combined Unit command exit 1, 6 passed / 4 failed / 0 skipped. Command/TRX references and counters are in `artifacts/master-remediation-20261002/parent-resume-command-results.json`.

Owned PostgreSQL runner attempts are preserved separately. HARNESS_CORRECTION: Windows PowerShell native-pipeline capture retained postgres child handles after pg_ctl exited; exact owned data directory/PID/listener verified before stopping that disposable instance only. The first runner then completed FAIL with cleanup PASS. Subsequent startup process exit-code null handling was corrected with an acquired process handle and Refresh; a null code is now rejected, never coerced to PASS. Native xUnit stderr was previously treated as a terminating PowerShell ErrorRecord before TRX completion; capture now continues until dotnet exits and then checks TRX/exit/counts. No assertions, skip policies or concurrency barriers were reduced. Earlier harness failures are not behavioral RED evidence.

Actual behavioral PostgreSQL RED: `postgres-behavior-red/terminal-result.json`, exit 1, 7 passed / 6 failed / 0 skipped; **Provider = PostgreSQL 18 / Npgsql** verified by server version and tests; zero-to-latest migrations PASS; has-pending-model-changes PASS; owned cluster terminal shutdown/cleanup PASS; operational DB untouched. Failures prove: purchase header DateTimeOffset Dapper constructor mismatch (two cases), corrupt external sequence authority silently accepted after actual pg_dump/pg_restore (one), initial concurrent stock/cost row races (two), and an incorrectly early rollback injection (one).

Minimum production corrections are authorized by these concrete Phase 3 regressions: purchase header maps native UTC DateTime then converts at DTO boundary; ReceiveProductIntake takes the existing canonical product resource lock before stock/cost reads; external high-water reads reject malformed/unsigned/corrupt state and propagate storage failures; separate instances reload/merge under a FileShare.None cross-process lease and flush atomic unique temporary writes. Existing manifest format and HMAC domain are preserved; this is not a claim of completed security hardening. HIGHWATER missing-authority/tamper resistance and all protected regressions remain mandatory. No migration or released migration edits.

Rollback fixture HARNESS_CORRECTION: its IUnitOfWork previously threw on an early cost/lot save before sequence reservation. It now delegates early saves to the same transactional DbContext and injects only when added InventoryUnits prove reservation occurred. The original no-reuse assertion is unchanged; an added exact injected-exception assertion strengthens causal proof (NEW_COVERAGE; independently review before certification).

### Subsequent terminal supporting results and remaining scope

The rollback fixture also has to recognize tracked Unchanged units after the canonical operation-outcome ledger flushes them inside the same uncommitted transaction. This correction preserves the rollback/no-reuse assertion and now produces the exact injected exception. Purchase-return summary projection had the same native DateTime/DateTimeOffset Dapper mismatch even with zero rows; a native row type and explicit UTC conversion correct it. Intermediate PG runs: 19/22 and 21/22 passed, exit 1, zero skipped, cleanup PASS; retained as failures.

Label PG fixture HARNESS_CORRECTION: only its display Unit.Name is set to realistic "Piece"; the generic fixture previously appended a GUID, which exceeded the renderer's fixed label width. Unit ID, unique symbol, tracking/provenance/stock/movement/sequence assertions and production overflow rejection are unchanged. Oversized labels still fail closed. No released/shared fixture changed.

`postgres-focused-green-final`: exit 0; 22 passed / 0 failed / 0 skipped, provider PostgreSQL 18 / Npgsql, migration/model alignment and cleanup PASS. This comprises 13 actual database cases and 9 supporting controller authority cases; mocks do not substitute for DB cases. It is a focused gate, not final certification.

Desktop normal label workflow RED: 0 passed / 2 failed / 0 skipped, exit 1 (commands absent). GREEN after wiring authenticated remote service: 37 passed / 0 failed / 0 skipped, exit 0. Includes 8 workflow tests (exact selected IDs, explicit reprint, product-only quantity labels, cancellation, filesystem failure context, audit warning/no automatic resubmission), prior label/printer tests and 10 intake tests. Initial new-test read-only TrackingMode assignment compile failure retained as HARNESS_CORRECTION; use the immutable constructor policy instead. WPF buttons now expose intake PDF all/selected/reprint and Product Detail print/reprint/product PDF actions. No direct DB/identity allocator introduced. Actual installed visual/output checks remain pending.

Missing-authority NEW_COVERAGE RED: 6 passed / 2 failed / 0 skipped, exit 1. Correction: known loaded authority or an established persistent write-lease marker makes a missing manifest fail closed, including restart; only the creator of a fresh lease may initialize a genuinely absent fixture. Concurrent reads share delete access so atomic replacement can complete. `tracking-missing-green` terminal: 107 passed / 0 failed / 0 skipped, exit 0; includes all 8 high-water cases, 5 PDF cases, protected Phase1 receiving/exact lifecycle and relevant DI/forensic unit tests. Supporting evidence only; protected real PG regression is running sequentially.

Deferred backup work is removed from active production DI composition: the established EnvironmentBackupEncryptionKeyProvider is retained. New uninstalled ring source/evidence remains preserved for later-phase review; no provisioning endpoint/tool or live key was added. This scope containment does not declare those retained files certified.

Read-only live account census: installed /api/auth/accounts exposes only Amir/Owner; no Ali/Cashier account. Source Settings user administration has no production write adapter and UsersController is read-only. Pilot user provisioning is therefore unfinished; a canonical authorized implementation/operational workflow remains required, with no SQL insertion or credential collection. Read-only high-water ACL metadata shows default existing file owner ALI/muham, inherited SYSTEM/Admin FullControl and Users ReadExecute. No file content/ACL changed. Trusted authority permissions/custody must be reviewed before deployment; do not claim malicious-tamper resistance from the public machine-name HMAC alone. Desktop remains closed; sign-in handoff unanswered. All source changes are uninstalled.

## Independent implementation review continuation — 2026-10-02

Protected PostgreSQL regression has a terminal PASS: 107 passed / 0 failed / 0 skipped, exit 0; Provider = PostgreSQL 18 / Npgsql; zero-to-latest migrations, EF model alignment and owned-cluster cleanup PASS. Evidence: artifacts/master-remediation-20261002/postgres-protected-regression/terminal-result.json. This is supporting/protected regression, not final certification.

| Gate | Scope | Evidence Required | Command/Test | Status |
|---|---|---|---|---|
| HIGH authority loss | Tracking | Production cannot bootstrap after both manifest and lease disappear | MasterMachineHighWaterTests new loss/bootstrap tests | NEW_COVERAGE RED running |
| HIGH valid replay | Tracking | Known maxima and restart checkpoint reject older valid snapshots | MasterMachineHighWaterTests snapshot replay tests | NEW_COVERAGE RED running |
| HIGH custody | Tracking | Trusted owner/effective ACL and complete ancestor/reparse validation | Owned custody fixtures + fresh read-only design review | Review/RED pending |
| MEDIUM publication | Labels | Atomic complete PDF pack; no partial final output | Owned staged-output failure cases | Independent agent plan approved |
| MEDIUM audit | Labels | Generation distinct from workstation publication; audit failures visible | Server/Desktop contract tests | Agent RED preparation |
| MEDIUM print retry | Labels | Stable pre-submission attempt, no automatic unknown-job retry, failed-only selection | Desktop printer/intake tests | Agent RED preparation |
| Cashier creation | Pilot user adapter | Active Owner authority, atomic redacted durable operation, normal human PIN entry | Unit/Desktop RED then isolated PostgreSQL | RED terminal (0/1 each); implementation in progress |

The fresh label/tracking review is an implementation review, not final certification. It identified 3 High tracking findings and Medium label gaps; they remain open until regression and independent review. No source change has been installed. Backup feature work remains deferred. Build/test leases are serialized across agents; all prior parent tasks are terminal before this RED command. No operational business records, credentials, services or ACLs changed.

Tracking independent-review NEW_COVERAGE RED is now terminal: 13 total, 8 passed / 5 failed / 0 skipped, exit 1. Five failures prove empty/unprovisioned authority acceptance, loss of both files, valid snapshot rollback in a running reader and after restart, and ordinary-user-owned authority being accepted as production custody. Exact commands/counts are in tracking-authority-review-red/command-results.json. First command failed compilation in the concurrent new Cashier test (IDE0011); no behavioral evidence claimed for that command. Agent corrected missing braces only (HARNESS_CORRECTION) before the repeat.

Cashier source focused GREEN: Unit/controller/status 20 passed / 0 failed / 0 skipped, exit 0; Desktop/remote 7 passed / 0 failed / 0 skipped, exit 0. Supporting evidence only (Unit EF InMemory); real PostgreSQL transaction/concurrency/rollback/replay gate is now being added independently. Unit attempt1 had 19 passed / 1 failed / 0 skipped because test expected UnauthorizedObjectResult while canonical OperationsController returns ObjectResult with status401; HARNESS_CORRECTION asserts explicit 401 without changing security expectations. This correction remains subject to independent review. No installed account created.

High-water correction design: checkpoint only preserves greatest next-unused maxima, never allocates; strict service requires established trusted artifacts and rejects regression/removal before changing memory. Constructor/read/write share a lease. Checkpoint-first then manifest flush/publication, crash divergence fails closed. Production custody includes full ancestor/owner/effective ACL and reparse validation; ordinary-user fixture injection remains internal and attested to a disposable PG root. Existing installation is NOT silently promoted from its user-owned legacy file. Approved operational initialization/migration with independently trusted history is a separate unresolved deployment gate. Coordinated rollback by an administrator controlling all custody is outside the companion checkpoint's claimed detection. No operational ACL changes authorized/executed by source tests.
