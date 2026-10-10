# Edge Retails — Phase 3 authenticated continuation

## Scope and disposition

Installed Release 1.0.10 candidate 21 remains preserved. Phase 3 is OPEN. Phase 1/2 and Phase 3A/3B closures are retained. No Phase 4. This report records installed observations and a surgical correction; it is not final independent certification.

User entered the recovered PIN directly in installed Desktop. Authentication succeeded: Amir / Owner, authenticated shell. No PIN, credential, hash, session token or Recovery envelope was accessed or recorded. The session bootstrapped against the actual loopback Shop Server. Desktop PID13064 was observed connected to127.0.0.1:7150; its observed TCP list contained no PostgreSQL connection. Prior API-only composition and response-contract tests are retained as supporting source/test evidence.

## Phase 3C installed matrix

| Required row | Actual result | Evidence / limitation |
|---|---|---|
| Installed 1.0.10 candidate 21 | PASS identity | Prior approved cutover terminal exit0, versions/hashes and fresh backup/restore retained |
| Owner sign-in / new PIN | PASS | User-entered PIN accepted; Amir/Owner shell observed |
| Authenticated shell | PASS | Account/role persist across safe Server-backed pages |
| Canonical navigation | BLOCKED for complete matrix | All 13 sidebar routes plus existing Thaka Workspace and unsaved New Purchase visited; ProductDetail lacks product, SaleDetail lacks selected current-period sale; Supplier Khata lacks supplier |
| POS bootstrap/responsiveness | PASS for bounded empty-catalog smoke | POS loads; input responds; no observed hang during safe navigation/search; cart remains empty |
| Catalog load | PASS: empty dataset | Product Management and Inventory each show0 products; no positive-stock claim |
| Search | BLOCKED for positive cases | Synthetic phase3-no-match-20261001 accepted; empty result and authoritative no-match feedback observed; name/SKU/TrackingCode/Serial/IMEI/exact precedence positive cases require existing stock |
| Scanner | SIMULATED SUPPORTING EVIDENCE | Keyboard Enter reaches authoritative scan no-match; no real hardware PASS. Physical scanner is not an explicit mandatory frozen3C gate |
| Exact-unit lookup | BLOCKED | Physical Units action safely requires selected backend product; catalog has none; no stock created |
| Normal close | FAIL | Close removes window but installed PID13064 stays alive with MainWindowHandle0 for minutes |
| Restart | BLOCKED / unexecuted | Clean-exit prerequisite fails; no forced-exit result substituted |
| Recovery discrepancy | BLOCKED / unresolved | Original Success2/Consumed2/MatchedPair2/Denied0 exactly-one assertion remains FAIL, exit1; new factual diagnostic not executed because UAC canceled |
| Old PIN rejection | BLOCKED / unproven | Actual production rejection evidence missing; isolated PostgreSQL tests do not prove this production observation |
| Desktop API-only boundary | PASS for observed runtime plus retained authority | Actual7150 connection; no direct-DB fallback used; login response/session contract test retained |

Of the 13 required rows above excluding supplemental Scanner:6 PASS, 1 FAIL, 6 BLOCKED; Restart is included in the blocked count and is also unexecuted. Counts are explicitly at this table's row granularity, not an invented global certification count. Critical confirmed defects0; High confirmed installed defects1 (normal-exit deadlock). Unknown Recovery cause is unresolved, not a proven replay defect.

## Installed navigation facts

Dashboard: database Connected; current financial values0; existing active Thaka project asdf. LowStock metric reports unavailable because minimum-stock authority is absent in the backend read model; no unrelated redesign performed.

Sales History: Today has0 transactions; no all-history emptiness claim. Thaka Workspace for existing asdf opens/returns. Purchases shows0 purchases; New Purchase form opens/returns without edits/save. Product Management/Inventory0 products. Expenses current period empty. Customers contains existing asdf row (the accessibility document includes hidden empty-state text; screenshot shows the actual row). Suppliers empty. Warranty has0 cases. Reports shows current-month0 financial metrics and completes rendering. Settings Receipt page displays existing settings; no settings saved.

Observed action-to-capture samples include automation overhead and are not render benchmarks or acceptance thresholds: Dashboard2098ms, POS2053ms, ProductManagement4403ms before completed-load observation; typing20character synthetic query8205ms. One initial Enter input failed activation with unknown outcome, so the window was reobserved before one successful retry. No hangs were inferred from transient loading/stale accessibility snapshots.

## Concrete shutdown defect and correction

Independent bounded source review confirms App.OnExit calls MainViewModel.Dispose; its synchronous SignOutAsync wait blocks the WPF dispatcher while nested HTTP awaits capture that dispatcher. No tray/minimize-to-tray behavior found. Normal process exit failed in the installed release, not merely a source hypothesis.

The new internal DesktopSessionShutdown boundary starts only logout on Task.Run without the UI synchronization context. It passes a dedicated cancellation token through logout and bounds the caller with WaitAsync. MainViewModel uses a5-second deadline; other UI disposal stays on the WPF thread. Deadline cancellation allows process shutdown while Server session expiry remains authoritative if logout cannot finish.

Test classification: NEW_COVERAGE. No existing assertion/concurrency/skip changes. Three background STA tests deliberately do not pump their context and cover delayed success, cooperative cancellation and ignored cancellation. Synthetic credentials/IDs only. Failed-test cleanup releases pending callbacks on pool threads and uses finite joins.

| Command | Exit code | Passed | Failed | Skipped | Database provider | Completion |
|---|---:|---:|---:|---:|---|---|
| dotnet test tests/EdgeRetails.Desktop.PerformanceTests/EdgeRetails.Desktop.PerformanceTests.csproj -c Release --filter FullyQualifiedName~Phase3AuthenticatedShutdownTests --logger console;verbosity=normal -warnaserror |1|0|3|0|No DB; synthetic HTTP|FAIL preserved; terminal05:14:52Z |
| dotnet test same project -c Release --no-restore --filter FullyQualifiedName~Phase3AuthenticatedShutdownTests\|FullyQualifiedName~Phase3InstalledLoginResponseTests --logger console;verbosity=normal -warnaserror |0|4|0|0|No DB; synthetic HTTP|PASS;05:15:38Z–05:16:51Z |

Logs: artifacts/phase3-final-closure-20260930-01/desktop-authenticated-shutdown-red-110.log and desktop-authenticated-shutdown-green-110.log. The red command start timestamp was not retained in its stream; process launch observation is preserved in tool history. No invented timestamp. Green log contains start/end/exact filter.

Corrective Release 1.0.11 candidate 22 has terminal publisher PASS (exit0,05:17:19Z–05:24:57Z): Release/MSI/Burn zero warnings/errors, Unit746/746 zero skipped, PostgreSQL18.6 client and package authority scans. The affected Desktop boundary regression also has terminal22/22 PASS, zero skipped, exit0 (05:26:13Z–05:26:18Z), preserved in desktop-authenticated-shutdown-boundary-111.log. These are supporting gates, not the final15-gate sequence. The package was produced in a new output directory. Candidate21 binaries/manifests remain immutable. Exact Server-version enforcement in Recovery is advanced to1.0.11 for coherent new packaging; no new Recovery ceremony, trust/config/schema/credential change occurs. Production fix is not claimed until approved cutover and actual installed clean-exit/restart smoke succeed.

## Recovery factual reconciliation

Original production audit: PostgreSQL 18.6 / Npgsql, edge_retails_prod, Success2, Consumed2, MatchedPair2, FailedOrDenied0. Original cutoff2026-09-30T16:25:24Z and original FAIL are unchanged.

Safe new read-only diagnostic is prepared and reviewed. It validates protected authority, PG18 target, exact20 migration identities and two valid unique recovery indexes, then obtains safe timestamps and hashed event/nonce/target identifiers in READ ONLY transaction followed by ROLLBACK. It does not inspect credentials/envelopes. SHA256 F66F5873F90975916B3B670CDDD5599784ACCF51064AE08B5B0E1C4AADD68E1E. UAC was canceled, no elevated diagnostic process/log was created; no result is assumed.

Distinct nonces: UNPROVEN. Same Owner target: UNPROVEN. Event timestamps/pairing: UNPROVEN beyond original counts. Same-envelope/same-nonce replay: NOT PROVEN; zero denied events is not rejection proof. Human ceremony intent: no answer yet. Governance disposition: PENDING. No audit record removed, count/cutoff/assertion changed or additional PIN reset performed.

## Phase 3D exact final sequence

Authority: latest governance master c73a8563-9ab0-456a-a3a9-10f110d2d575, section18, preserved by current continuation. All 15 final gates below remain gated by Phase 3C; none credited from supporting runs or historical releases.

| Number | Mandatory gate | Current final-sequence result |
|---:|---|---|
|1|Recovery security tests|UNEXECUTED — gated by 3C |
|2|Release upgrade-path tests|UNEXECUTED — gated by 3C |
|3|Phase 3 focused Unit|UNEXECUTED — gated by 3C |
|4|Phase 3 Desktop|UNEXECUTED — gated by 3C |
|5|Phase 3 API|UNEXECUTED — gated by 3C |
|6|Phase 3 PostgreSQL|UNEXECUTED — gated by 3C |
|7|Phase1 protected PostgreSQL|UNEXECUTED — gated by 3C |
|8|Phase2 protected PostgreSQL|UNEXECUTED — gated by 3C |
|9|Full UnitTests|UNEXECUTED — gated by 3C |
|10|Full IntegrationTests|UNEXECUTED — gated by 3C |
|11|Desktop PerformanceTests|UNEXECUTED — gated by 3C |
|12|Debug build|UNEXECUTED — gated by 3C |
|13|Release build|UNEXECUTED — gated by 3C |
|14|EF source vs production migration history|UNEXECUTED — gated by 3C |
|15|EF has-pending-model-changes|UNEXECUTED — gated by 3C |

Final sequence counts: passed0, failed0, skipped0, unexecuted15 (all prerequisites gated). Golden Trace and Hostile Trace remain unexecuted. No workspace freeze. No fresh final certifier launched. Full final regression, protected final regressions and final model/history gates remain open; no release-pipeline supporting result is substituted for them.

## Production runtime retained

Latest read-only readiness observation05:11:34Z: HTTP200; Ready, canConnect=true, hasPendingMigrations=false, maintenanceState=Normal. Installed Server5152, Worker5192 and PostgreSQL3100 Running/Auto were retained. Provider=PostgreSQL 18 / Npgsql; operational server 18.6. Exact20 approved migrations were verified at candidate 21 cutover, latest20260930065058_Phase 3COwnerPinAuthorizationConsumption. No new production schema operation in this continuation.

Desktop PID13064 is an orphan after normal close, not a clean exit. Original production backup/restore and all prior logs/packages are preserved. Git state was not committed/pushed/reset/cleaned/stashed. No inventory/business/configuration/credential mutation.

## Exact next checkpoint

Finish corrective package evidence and independent bounded package/cutover review; when normal elevation is available, run the read-only recovery reconciliation and approved fresh-backup/binary cutover. Verify new installed authenticated close/restart with human PIN entry. Resolve positive-data/navigation/old-PIN/recovery disposition prerequisites honestly before locking 3C. Then execute the 15ordered gates, current Golden/Hostile traces, formal freeze and fresh independent read-only final certification. Phase 3 remains OPEN; do not start Phase 4.

## Latest runtime census — 2026-10-01 05:28:42Z

Evidence: artifacts/phase3-final-closure-20260930-01/phase3-authenticated-continuation-runtime-20261001_052840_071Z.json. All four installed binaries remain1.0.10 and match preserved candidate21 hashes. Server5152, Worker5192, PostgreSQL3100 Running/Auto; only127.0.0.1:7150 owned byServer5152; readiness HTTP200 Ready/connected/no pending/Normal. Source fix is NOT installed. Desktop orphan PID13064, MainWindowHandle0 remains; no force cleanup yet. This census is read-only and not a final-certification gate.

## Independent package and deployment review

Both fresh bounded reviewers returned GO, with no scoped package/wrapper blocker. Package review verified1,398/1,398payloads, six artifacts/checksums, all binary/MSI/setup versions and zero private-marker hits. Wrapper review inspected7scripts with zero parser errors, twelve top-level artifacts across prior/new candidates and eight EXE versions. Detailed read-only review record: artifacts/phase3-final-closure-20260930-01/release111-independent-bounded-reviews-20261001.md. These are not final certifiers.

New pinned cutover wrapper: scripts/Invoke-Phase3Release111AuthenticatedShutdownCutover.ps1, SHA25623D889BCBC6810DABFBD08655F84B6D9A852D0A9FC41B33EBBD7E62274AB0256. Not executed. All publication/test tasks have terminal results. Normal UAC availability remains pending after cancellation; no automatic retry. When available: reviewed read-only recovery diagnostic, then approved corrective cutover with fresh backup/restore and optional exact-known-orphan cleanup; human reauthentication and real installed close/restart remain required. Original failures and prior releases remain preserved.

## Superseding checkpoint — installed Release 1.0.11, 2026-10-01

The earlier sections describe historical checkpoints. The approved elevation retry completed installation; do not repeat publication or cutover. Phase3C remains open pending actual installed authenticated close/restart and the remaining recovery/data evidence.

| Command | Exit Code | Passed | Failed | Skipped | Database Provider | Completion Status |
|---|---:|---|---|---|---|---|
| powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File scripts/Invoke-Phase3Release111AuthenticatedShutdownCutover.ps1 -AllowKnownOrphanCleanup |0|All wrapper gates; approved installer0; both service-verifier runs0|0|0|PostgreSQL 18 / Npgsql, actual18.6|PASS;06:20:58Z–06:24:04Z |
| Invoke-Phase3Release106BackupUpgradeRehearsal.ps1, invoked by wrapper |0|Fresh archive and actual disposable PostgreSQL restore; history20; cleanup|0|0|PostgreSQL 18 / Npgsql|PASS before installation |
| Test-Phase3Release106ProductionSchema.ps1, invoked by wrapper |0|History20; latest20260930065058_Phase3COwnerPinAuthorizationConsumption; two recovery indexes; hold tables0|0|0|PostgreSQL 18 / Npgsql|PASS |
| Invoke-Phase3RecoveryReconciliationReadOnly.ps1 |0|Read-only snapshot, paired events and unique indexes, two distinct nonces|0 diagnostic errors|0|PostgreSQL18 / psql; application Npgsql|PASS factual diagnostic only;06:14:02Z–06:14:04Z |

Evidence directory: artifacts/phase3-final-closure-20260930-01. Cutover log: phase3-release111-authenticated-shutdown-cutover-20261001_062058_698Z.log. Installer record: release-1.0.11-install-20261001_062058_698Z.json. Backup evidence: C:/Users/muham/AppData/Local/EdgeRetails/Production/backups/phase3_release106_pre_upgrade_20261001_062108_160Z_983ceacf6e734c04bf9033c483d7c2a0.evidence.json. Verified archive SHA256: 6c79f3b47611ad6b0b70ca846c5055924756050a1adcd2ee83dcba51818445f2. All four installed binaries1.0.11 match candidate22; protected configuration, license and trust preserved. No new operational migration or PIN recovery.

Server14272/Worker11496 Running/Automatic; Worker actual PostgreSQL loopback connection and three60-second recovery actions verified. Only127.0.0.1:7150 listens; readiness HTTP200 with status Ready, canConnect=true, hasPendingMigrations=false, maintenanceState=Normal. Live process census also confirms PostgreSQL service Running. Installed Desktop15572 opened06:25:16Z and shows the login surface. Authentication, actual normal-close process termination and restart for this corrected release remain pending. Guarded cleanup of the old1.0.10 orphan is not a clean-exit PASS; the original FAIL remains immutable.

Recovery reconciliation log: phase3-recovery-reconciliation-readonly-20261001_061402_620-19a14c5a02f447aeb2093f7926c2416f.log. Two successes and two authorization consumptions are paired1:1, each nonce occurs once in retained history; no successful same-nonce replay observed. Local event times: September30,21:31:22 and21:31:43. One target digest; a separate read-only diagnostic to map it to current Owner metadata is awaiting normal elevation. Human intent is still pending. Envelope bytes were not examined; an actual same-envelope rejection and old-PIN production rejection are not proven. The original exactly-one audit FAIL/exit1 is retained, not replaced by diagnostic exit0.

Independent bounded fact review confirmed the source and retained1.0.9 recovery method identities relevant to those historical events. Canonical signed action: RESET_ACCOUNT_PIN. An older OWNER_PIN_RESET wording was a report error. This review is not final certification. No final15-gate run, Golden/Hostile trace, freeze or independent final certification has started. Phase1/2 authority remains locked; no Phase4 work.

## Installed verification continuation — authority93b8adc2, 2026-10-01

The latest user-supplied authority confirms normal UI close/X followed by the separate machine ProcessCount0 result. Installed1.0.11 normal-close smoke: PASS based on user-observed normal UI close followed by independent ProcessCount=0 verification. No orphan Desktop process observed. The historical1.0.10 normal-close FAIL remains immutable. This closes the close observation only; authenticated restart verification is still pending.

The approved installed1.0.11 Desktop was restarted, not reinstalled. PID16880, executable C:/Program Files/Edge Retails/EdgeRetails.Desktop.exe, process creation2026-10-01T08:19:21.105737Z (13:19:21 Pakistan time), file version1.0.11 and approved candidate22 hash1195E92C6239369E9B869A1E9E22C8C6FE908ABBBBDDE31BD1CFC5CACBCF1FA2. Actual login surface Amir/Owner loaded without startup error or Sign in unavailable; PIN contents were not inspected. User entry/result is pending. Server14272/Worker11496/PostgreSQL3100 remain Running/Automatic; Server listener127.0.0.1:7150 only. Readiness at08:23:12Z is HTTP200 Ready/connected/no pending/Normal. No Desktop TCP connection was returned in the bounded census; absence at one instant is not positive proof of a7150 request and no continuous monitoring claim is made. Authenticated API-only result remains pending this restart.

Recovery target mapping is now PASS. Diagnostic command: powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File artifacts/phase3-final-closure-20260930-01/Invoke-Phase3RecoveryTargetMetadataReadOnly.ps1. Start06:57:12.3697213Z/end06:57:13.2144145Z; exit0; all scoped diagnostic requirements passed, failed0/skipped0. Provider=PostgreSQL18 / psql read-only diagnostic, application=Npgsql, actual180006. Evidence: phase3-recovery-target-metadata-readonly-20261001_065712_156-c02d60f0bf4b48de82fb53621d73d9fd.log. Current target Amir/Owner, user and role active; four frozen event matches, one actor, two nonce groups, no empty actor/non-USER event/unexpected summary. Only current identity metadata is proved, not historical role reconstruction. No credentials/session/envelope/private material selected; no mutation.

Human intent around the two distinct Sep30 recoveries is still unresolved. No legitimate final recovery disposition claimed yet. Production old-PIN and actual same-envelope rejection remain unproven; their blocking status is being checked against the actual frozen contract. A fresh bounded read-only acceptance reviewer is determining mandatory versus conditional data-dependent rows. This is not final certification. No new build/release/recovery/test/database task launched; the completed mapping diagnostic is not rerun. Phase3C OPEN; Phase3D NOT STARTED, all15 final ordered gates unexecuted; no freeze or final certifier.

## Bounded acceptance review completed; human authentication handoff

Fresh read-only reviewer /root/phase3c_frozen_contract_review completed the scope independently. Authorities: latest93b8adc2; continuationdbe9d9b8; recovery governancec73a8563 sections14–16; frozen docs/Phase3_Final_Acceptance_Contract_2026-09-29.md, ordered3C row and prohibition on adding empty-data/optional stronger-proof gates. No test, production or UI work was delegated. An initial model-capacity failure was retried in the same bounded scope and is not a certification result.

Positive name/SKU/TrackingCode/Serial/IMEI matches, positive exact-unit selection, ProductDetail and SaleDetail are NOT REQUIRED under the current absent-data preconditions; no positive-result PASS is claimed. Search and scanner/exact-unit field responsiveness remain mandatory; prior installed negative lookup and selection-guard observations are retained supporting installed evidence. Real scanner hardware is NOT REQUIRED here. Supplier Khata surface is explicitly mandatory (c73a section15); Suppliers list is not a substitute and absent supplier leaves Khata BLOCKED. Production old-PIN rejection and actual same-authorization replay rejection are explicit mandatory requirements (c73a section14), both BLOCKED/unproven. Two unique nonce pairs do not demonstrate rejected replay.

Recovery discrepancy can be dispositioned only with the permitted explanation of two distinct authorized ceremonies and retained original exactly-one FAIL. Current safe facts establish two distinct signed nonces,1:1consumption/success and current active Amir/Owner mapping; human intent answer is missing, so discrepancy remains unresolved. No token is inspected/replayed and no new PIN ceremony is authorized for convenience.

Latest read-only restart census evidence: artifacts/phase3-final-closure-20260930-01/phase3-release111-restart-preauth-20261001_082454_372.json. Command: Get-Process for Desktop plus Get-Service for the three installed services and Invoke-WebRequest -UseBasicParsing /api/system/ready, with redacted selected fields written to a new evidence file. Start08:24:54Z; readiness response13:25:29 Pakistan time; shell terminal exit0. Passed: PID16880/path/version/Responding, servicesRunning, HTTP200 Ready/connected/no pending/Normal; failed0/skipped0. Provider PostgreSQL18/Npgsql retained runtime. This command performs no fresh database certification. All inspection commands and the bounded reviewer have terminal results. No required build/test/database command is still running.

The terminal Phase3C matrix is not issued before required human authentication. Current installed1.0.11 restart has a visible login surface, but Owner sign-in, authenticated shell/session, authenticated API runtime and the full restart pair remain BLOCKED pending direct user entry/result. Existing1.0.10 authentication and navigation evidence remain retained, not misreported as new1.0.11 authenticated observation. Critical confirmed defects0; shutdown/restart HIGH defect closure remains pending authenticated restart, despite normal-close PASS.

Exact bounded blocker groups: (1) authenticated1.0.11 restart and API/session result; (2) historical Recovery explanation/disposition plus actual rejected same-authorization proof; (3) actual old-PIN production rejection; (4) installed Supplier Khata surface. Four blocked gate groups, three wholly unexecuted required proof groups (authenticated restart proof, old-PIN rejection, Khata); Recovery facts are executed but its rejected-replay subproof is unexecuted. Counts are grouped as stated, not a global final-certification count. No source defect was newly proved in this continuation. No release1.0.12, inventory seeding, extra recovery, installer or final sequence run.

Next exact action: user signs in directly in the open installed Desktop and reports only the result; do not send the PIN. Separate two-click historical intent question is pending. Phase3C OPEN;3D NOT STARTED; all15 final gates still gated, no freeze or final certifier.

## Subsequent login confirmation and controlled pilot preflight

Latest user request024a6977 explicitly confirms successful login after restart. Parent observed actual installed1.0.11 authenticated Amir/Owner shell, PID17888, Product Management0products, Settings and a completed authoritative backup-history refresh. No PIN/session credentials inspected. Installed startup/authentication/shell observed; prior normal-close user-X plus ProcessCount0 remainsPASS. Specific orphan-shutdown/restart symptom not observed in this completed pair; historical1.0.10FAIL preserved. No completePhase3C lock: Recovery intent/oldPIN/replay and Khata proofs remain unresolved.

A separately authorized controlled demo pilot passed governance preflight; it is not formal3D certification or a waiver. Step0 installedBackupNow failed once around14:21Pakistan time before any business creation. History refreshed fromServer but remains empty. All3servicesRunning; readyHTTP200 at14:28:38. Backup-key persistent-source presence checks false; exact caughtServer exception not independently captured. Pilot held at its fresh-backup prerequisite. Details: Controlled_Demo_Business_Pilot_2026-10-01.md and Controlled_Demo_Pilot_Backup_Remediation_2026-10-01.md. No key/config/registry/source/test/service mutation, newrelease or recovery ceremony. Phase3OPEN; no3D/freeze/finalcertifier.
