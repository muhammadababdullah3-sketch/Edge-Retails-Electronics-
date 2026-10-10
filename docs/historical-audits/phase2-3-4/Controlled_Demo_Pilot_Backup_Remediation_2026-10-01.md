# Controlled demo pilot — first blocker and concrete remediation plan

## Preserved installed failure

Installed release1.0.11 candidate22, authenticated Amir/Owner, PID17888, C:/Program Files/Edge Retails/EdgeRetails.Desktop.exe. The user entered credentials directly; no PIN/session/secret access. Actual Product Management and Settings rendered; existing catalog0products retained as historical empty-data evidence. Settings → Backup → Backup Now invoked once at approximately14:21 Pakistan time on2026-10-01. It reached Creating encrypted database backup, then returned The record changed since it was loaded. Refresh it before continuing. No success was assumed. A subsequent history Refresh completed with Verified backup history refreshed from the Server and an empty verified list/No verified backups. No backup retry, prepared restore, cutover or discard was invoked.

At14:28:38 Pakistan time, readinessHTTP200 Ready/canConnect=true/hasPendingMigrations=false/Normal; Server/Worker/PostgreSQL servicesRunning. Desktop17888 RespondingTrue. One instantaneous Desktop netstat census returned no sockets; this is not continuous no-DB monitoring or positive TCP proof. Actual authenticated Server-backed backup history refresh and retained API-only production composition support the runtime path.

Read-only presence checks: EDGE_RETAILS_BACKUP_KEY absent from Machine/User environment, Server/Worker service Environment entries and LocalSystem user environment. Only presence booleans inspected/reported; no secret values output. No Environment entry exists on either service. Backup provider source reads only Server process environment; protected database configuration does not supply its backup key. This proves no configured key in those documented persistent sources. The existing Server process could theoretically contain an inherited/stale value; its secret-bearing environment was not inspected. Therefore missing-key setup is a source-supported deployment finding, not a claim of an independently captured exact Server exception.

BackupsController.Create catches every InvalidOperationException and returns409 backup.operation_conflict. DesktopErrorPresentation maps conflict suffix to the observed stale-record message. Missing/malformed protection key, busy backup lock and PostgreSQL command failure can share that response. Do not report this as proven record-concurrency failure. No raw request headers/session tokens, config credentials or complete log dumps were inspected.

## Canonical authority

- docs/Phase3_Backup_Restore_Server_Operations.md: Server-owned64hex AES256GCM key, separate safekeeping; recovery maintenance identity separate from runtime DB authority.
- docs/Edge_Retails_Final_Architecture_Report_v1.md §56: high-entropy BMK, Windows-protected local copy, separately exported/stored Owner Recovery Key, wrapped-BMK/version metadata and replacement-machine recovery.
- §55: backup encryption is a separate trust domain from license/update/Recovery Authorization signing. Do not reuse the provisioned Owner PIN Recovery signing key.
- §9127: retained-backup decryptability/key history and explicit key-loss recovery.
- docs/operations/Security_Key_Rotation_Guide.md documents environment configuration/old-key custody but is labelledPhase6 and includes a source-run example; it is not permission to run Worker from source or to claim the complete canonical BMK/RK lifecycle exists.

Bounded read-only source reviewer found no dedicated complete BMK/RK provisioning tool/wrapping lifecycle in the searched implementation. Restore-journal and production-maintenance integrity keys are separate, not substitutes. No new random key was generated; no registry/environment/config/service/key changes made.

## Concrete next remediation

1. Identify whether governance/Owner already holds an approved backup key and its independent recovery custody. Pending user question asks only for provisioning status/procedure, never key bytes in chat.
2. If an existing key exists, use the approved secure Server deployment mechanism to make that same key available, retaining all historical decryptability and separate Owner custody. Validate only effective provider success, never print secret material.
3. If no established key exists, implement the minimum canonical provisioning lifecycle before live writes: protected local BMK, independent Owner Recovery Key/custody, authenticated wrapped-key/version metadata, retention of previous keys and effective Server loading. Do not invent a plaintext Machine-environment-only bootstrap or declare an on-machine second copy to be an offline recovery vault. The human-dependent custody step must actually complete.
4. Diagnose the exact backup failure using safe typed stage/exception evidence. Correct the misleading operation-conflict presentation surgically when the cause is established; preserve this installed failure, add focused RED/GREEN/relevant regression as required. Do not produce1.0.12 without a demonstrated installed behavior change that requires it.
5. Retry canonical installed Backup Now only after setup/cause is resolved. Verify fresh exact archive, authenticated manifest/checksum/TOC and actual disposablePostgreSQL18 restore, exact20 migrations/schema invariants, and owned-stage/cluster cleanup. The old cutover dump/restore PASS is historical and cannot substitute for this failed fresh pre-pilot gate.
6. Capture the required before snapshot read-only. Emit CONTROLLED DEMO BUSINESS DATA PILOT START with actual timestamp/release only immediately before the first authorized business creation. Then resume Step2 masters; do not bypass cashier/permissions or financial workflow confirmations.

## Current decision

Pilot halted atStep0. Fresh pre-pilot backup/restore FAIL as a prerequisite: installed create attempt failed; archive/hash/TOC/disposable restore/cleanup for this new attempt unproven. No business/demo records created, no full inventory population, no production SQL mutation. Governance preflight itselfPASS; formalPhase3 remainsOPEN, no3D/freeze/final certification credited. Existing1.0.10 failure,1.0.11 release/cutover and all earlier backups/evidence preserved. No Git operation or source/test alteration.
