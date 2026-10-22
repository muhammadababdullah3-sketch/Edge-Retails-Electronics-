# Isolated Central recovery rehearsal

This procedure exercises `scripts/central/Invoke-CentralDisposableRecoveryRehearsal.ps1` against an owned PostgreSQL 18 development cluster. It restores the Central schema, every Central table's logical contents, sequence state, schema definitions, runtime privileges, ciphertext, payment evidence and persisted upload bytes into a new disposable database and separate filesystem roots. It does not decrypt a customer backup or accept a customer private key.

The coordinator owns the database, file writers, test runner and host processes for the entire rehearsal. The script does not stop other processes, elevate, change Windows security settings, modify PostgreSQL authentication configuration or grant broad database privileges. Production operations require separate authorization.

Quarantined uploads preserve rejected file bytes separately from their accepted
upload offset. A rejected oversized chunk can leave a quarantined file while the
accepted offset remains zero. The bundle records `acceptedBytes` and
`quarantineBytes` separately and hashes the complete quarantined file. It never
marks those bytes accepted or finalizes the upload. Active/finalized upload
length and finalized digest checks remain strict.

## Inputs

Run from `C:/Users/muham/EdgeRetailsWorktrees/central-one` with the actual attested values. Do not copy a historical PID, password or port from an earlier run.

| Environment variable | Meaning |
|---|---|
| `CENTRAL_RECOVERY_OWNED_ROOTS` | Semicolon-separated, existing absolute Central development directories explicitly owned by the coordinator. Use disjoint roots when the disposable PostgreSQL cluster and Central worktree artifacts live in separate locations. The script rejects overlapping roots so a broad ancestor cannot silently widen ownership. `CENTRAL_RECOVERY_OWNED_ROOT` remains supported for a single-root setup. |
| `CENTRAL_PG_PORT`, `CENTRAL_PG_DATABASE` | Reserved loopback source port and Central development database. Ports 5432 and 7150 are rejected. |
| `CENTRAL_PG_ROLE`, `CENTRAL_PG_MIGRATION_ROLE` | Existing runtime and schema owner roles. Application verification rejects superuser or role-administrator identities. |
| `CENTRAL_PG_PID`, `CENTRAL_PG_BINARY`, `CENTRAL_PG_DATA_DIR`, `CENTRAL_PG_OWNERSHIP_MARKER` | Actual PostgreSQL 18 process/image/data-directory marker. The process, postmaster file, live listener and SQL identities must agree. |
| `CENTRAL_DATABASE_CONNECTION_STRING` | Explicit runtime credential for the source database. Host, port, database and role must match the owned identity. |
| `CENTRAL_MIGRATION_CONNECTION_STRING` | Explicit schema-owner credential for that source database. |
| `CENTRAL_PG_DATABASE_ADMIN_ROLE` | Separately authorized role used only to provision the fresh disposable target. Existing cluster administration does not elevate the runtime role. |
| `CENTRAL_PG_DATABASE_ADMIN_CONNECTION_STRING` | That role's own credential for the `postgres` maintenance database on the same owned cluster. Its password is not inferred from the migration credential. |
| `CENTRAL_OBJECT_STORAGE_ROOTS` | Semicolon-separated, explicit existing ciphertext roots under one of the owned roots. Include legacy roots only when their ownership is established. |
| `CENTRAL_PAYMENT_STORAGE_ROOTS` | Explicit existing payment/upload roots under one of the owned roots, if byte-bearing payment or commercial-upload records exist. When absent, the singular configured `CENTRAL_PAYMENT_STORAGE_ROOT` is used. No application-directory fallback or filesystem search is performed. |

All Windows path components are checked for aliases and reparse points. Every source, data, storage and evidence path must be contained by one of the explicitly configured disjoint owned roots. The evidence directory must be absent; use a new name for every attempt. The script preserves prior failures and never drops a database or deletes source records/files.

Credentials are parsed as connection strings, including quoted password values. Child PostgreSQL tools receive the appropriate explicit credential. Original `PG*` environment values are restored even on failure. No credentials appear in the generated command summaries.

## Capture and restore

Before capture, stop only the coordinator's owned application hosts, workers, upload writers and test processes. In the same PowerShell process that will run the rehearsal, call `scripts/central/New-CentralRecoveryQuiescenceProof.ps1` after setting the owned-root variables and PostgreSQL identity. It captures a redacted native `Win32_Process` inventory (PID, parent, image, executable path, command-line digest and owned-root/Central markers), refuses any matching process except its own coordinator and the attested PostgreSQL server or its verified `cmd.exe` launcher, and writes the inventory beside the proof. The recovery rehearsal verifies the proof is no more than 120 seconds old, is bound to the current Windows identity/PID and source database/port/PostgreSQL PID, verifies the referenced inventory's SHA-256 and containment, independently re-enumerates current processes, and checks source-database sessions before and after capture. Leave the owned PostgreSQL server running. Do not terminate the POS agent or any unowned process.

```powershell
& .\scripts\central\New-CentralRecoveryQuiescenceProof.ps1 `
  -OutputPath '<owned absolute path to a new proof.json>' `
  -SourceDatabase $env:CENTRAL_PG_DATABASE `
  -Port ([int]$env:CENTRAL_PG_PORT) `
  -PostgresPid ([int]$env:CENTRAL_PG_PID) `
  -PostgresBinary $env:CENTRAL_PG_BINARY `
  -DataDirectory $env:CENTRAL_PG_DATA_DIR
```

Generate the quiescence proof and adjacent process inventory from the same PowerShell process that will run the recovery command. Its required shape is:

```json
{
  "sourceDatabase": "<actual owned Central source database>",
  "port": 55457,
  "postgresPid": 0,
  "coordinator": "<actual Windows identity>",
  "coordinatorPid": 1234,
  "observedAtUtc": "<actual UTC observation time>",
  "fileWritersQuiesced": true,
  "databaseWritersQuiesced": true,
  "activeOwnedWriterProcessIds": [],
  "observationEvidence": [{
    "kind": "windows-process-inventory",
    "path": "<owned absolute path to the adjacent .processes.json file>",
    "sha256": "<SHA-256 of that exact file>"
  }]
}
```

The values above are format examples, not an executed receipt. Generate the actual proof with the helper; it binds to the current PowerShell identity and PID, so keep the same shell for the recovery command. Inspect and preserve the process inventory. The process check detects Central processes whose command lines or executable paths reference an owned root or known Central assembly; it is not a kernel-wide open-handle detector, so the coordinator must also confirm no unowned process is writing those owned files. Keep exclusive ownership throughout capture. The rehearsal rejects other source database connections at capture boundaries and requires canonical fingerprints/schema/outbox state to remain unchanged across the dump and file capture. `pg_dump` uses its own transactionally consistent PostgreSQL snapshot; cross-file consistency also depends on quiesced writers and persisted length/digest checks.

Execute with a fresh destination:

```powershell
& .\scripts\central\Invoke-CentralDisposableRecoveryRehearsal.ps1 `
  -TargetDatabase 'central_restore_<unique_run>' `
  -EvidenceRoot '<owned absolute path to a new evidence directory>' `
  -QuiescenceProofPath '<owned absolute path to the actual quiescence proof.json>'
```

The script captures `backup-bundle/central.dump` with ACLs, a SHA-256 manifest and all required storage blobs **before** restoring. It compares complete canonical row-content hashes and counts for every Central ordinary/partitioned table, including newly added worker/notification tables and migration history. Schema checks cover columns/defaults, constraints, indexes, triggers, routines and sequence state. Raw table rows are not written to the evidence summaries.

The new database is created from `template0`. `pg_restore --single-transaction --exit-on-error --no-owner` preserves source ACLs while assigning ownership to the configured schema owner. The restored runtime role must have the same effective table/schema privileges as the source, perform a real transactional outbox write that is rolled back, and receive PostgreSQL permission denials for audit UPDATE, DELETE and TRUNCATE. Restored migration history never substitutes for restoring grants.

The script verifies full source/target equality before intentionally rebasing storage paths. It then copies solely from the saved backup bundle into `restored-object-store/` and `restored-payment-store/`, updates exact expected path-bearing rows, reopens their metadata and files, and verifies that all non-path table contents remain unchanged. Finalized blobs must match their persisted length/digest. Partial uploads are captured at the exact persisted byte count with an independently computed digest; that digest does not assert that the upload was finalized or customer HMAC verified. A zero-byte initialized upload can legitimately have no file. Missing byte-bearing storage fails the rehearsal.

## Restore the saved bundle and test corruption

The backup bundle can be consumed without reading source files again. Record the externally returned `backupBundleManifestSha256` in the parent evidence package. Use a new database and evidence folder:

```powershell
& .\scripts\central\Invoke-CentralDisposableRecoveryRehearsal.ps1 `
  -TargetDatabase 'central_restore_<another_unique_run>' `
  -EvidenceRoot '<owned absolute new evidence directory>' `
  -RestoreBundlePath '<owned absolute saved backup-bundle directory>' `
  -ExpectedBundleManifestSha256 '<externally recorded 64-character SHA-256>'
```

The explicit restore-only interface is also the corruption-test entry point. The test coordinator must preserve each failure's command, exit and `failure.json`, and independently inspect the owned cluster to show an invalid bundle did not create its proposed target.

Required negative tests, on separate owned fixtures:

1. Copy a saved bundle to a new owned path, flip a byte in the copied `central.dump`, and call restore-only with the original manifest hash. The archive-entry hash must fail before `createdb`.
2. Repeat with one copied storage blob. No source blob is modified; restore must fail its manifest digest before target creation.
3. Supply the wrong external manifest hash; a forbidden/existing/source target; a default/operational port; a mismatched PID/image/postmaster/marker/listener; a credential for the wrong role/database; and an out-of-root/alias/reparse storage path. Each must fail closed. Real link fixtures require the authorized capable Windows runner; ordinary files do not replace them.
4. To prove that equal row counts cannot conceal changed data, use a separate disposable fixture to produce a modified archive with one same-count row-value change. In a copied bundle update only that archive's file hash and the external manifest hash, retain the original table-content expectations, then restore. The content comparison must reject it. This is a deliberately inconsistent synthetic backup, not a change to the accepted source.
5. Use bad runtime credentials and a fixture with missing/overbroad runtime grants. Privilege verification must fail; no superuser runtime or blanket grant is a valid repair.

These are required assertions, not claims that they have run. The coordinator owns their execution and raw evidence.

## Result and follow-on verification

Inspect `recovery-manifest.json`, `commands.json`, `RESTORED_TARGET_MARKER.json`, storage readbacks and the externally hashed bundle manifest. A failed run writes `failure.json` when an owned new evidence directory was established. `PASS` covers the script's explicit schema/data/ACL/file-restoration assertions only. Outbox counts preserve pending/failed/dead-letter states, compare effect payload/aggregate/event identities and reject missing/orphaned effects; they do **not** prove hosted worker replay or actual downstream business effects. The manifest states `NOT_EXERCISED_BY_THIS_SCRIPT` for worker replay.

To exercise the restored application, the coordinator must bind `CENTRAL_PG_DATABASE` and both application connection strings to the restored database, use `RESTORED_TARGET_MARKER.json`, and configure:

```powershell
$env:CENTRAL_OBJECT_STORAGE_ROOT = '<manifest restoredObjectStorageRoot>'
$env:CENTRAL_PAYMENT_STORAGE_ROOT = '<manifest restoredPaymentStorageRoot>'
```

Then prove application restart, authorized reads/downloads, upload continuation, pending/ACK-loss replay and duplicate-effect absence through the actual hosted worker. Reconcile the new business-effect tables as well as inbox/outbox receipts. Keep original failure/dead-letter state explicit. These are separate integration assertions; this script does not silently run a host or advance business effects.

Protected development signing keys are excluded from the bundle and governance exports. Restored signing/trust DB metadata requires a separately protected key-store recovery/restart assertion using the approved custody boundary; never place plaintext private keys in the dump, ordinary DB columns or review evidence. Schema-upgrade/rollback and clean application reconstruction also require their own candidate-bound rehearsal. Customer-edge/POS restore remains deferred. Production restore, publication and live cutover remain NOT_EXECUTED unless separately authorized.

The database archive contains sensitive local synthetic fixture state (for example session/authentication data). Keep it in the owned development environment with its existing access controls. Deliver hashes, redacted observations and reconstruction instructions to governance; do not publish real credentials or protected signing material. This document adds no provider, cryptographic, retention or commercial policy.

