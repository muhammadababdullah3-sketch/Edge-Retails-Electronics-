# CENTRAL-ONE development operations runbook

**Status:** Owned local development only. Production operations and live cutover are not commissioned.

## Operating boundary

Run Central from its isolated worktree and scoped solution. Keep the POS checkout, shop databases, POS services, production credentials, customer communications, and live providers outside this procedure. Use labelled synthetic fixtures and an owned disposable PostgreSQL 18 loopback target only.

## Before starting the API

1. Read the current execution checkpoint and verify candidate path, branch, HEAD, source overlay, and active writer ownership.
2. Attest the actual PostgreSQL executable/version, PID, command line, data directory, `postmaster.pid`, ownership marker, loopback listener, database, runtime/migration roles, and privileges. Set the target-guard variables from those live observations; never reuse a historical PID, port, or marker.
3. Provide the explicit database connection and protected signer-store root required by `src/EdgeRetails.Central.Api/Program.cs`. Configure disk-storage and hosted-service options from their source `FromEnvironment` parsers. Set `CENTRAL_SENSITIVE_APPROVAL_TTL_SECONDS` only from the approved security-policy source; the approval-request route returns unavailable when it is absent or invalid, and no request body can choose its lifetime. Secret values stay in the local secret store and out of logs/evidence.
4. Set all four admin authentication lifetimes explicitly: `CENTRAL_ADMIN_AUTH_CHALLENGE_TTL_SECONDS`, `CENTRAL_ADMIN_AUTH_SESSION_IDLE_TIMEOUT_SECONDS`, `CENTRAL_ADMIN_AUTH_SESSION_ABSOLUTE_LIFETIME_SECONDS`, and `CENTRAL_ADMIN_STEP_UP_TTL_SECONDS`. Each must be a positive integer number of seconds. Missing or invalid values disable login/MFA completion and step-up; session validation and grant validation fail closed. Do not copy the synthetic test fixture values into an operational environment without the required policy approval.
5. Fail closed if a required target/configuration value is missing, ambiguous, stale, or mismatched. Do not bypass `DatabaseTargetGuard`, relax role privileges, or point Central at an operational database.
6. Build and start only the scoped Central projects using the commands in `SERVER_DEPLOYMENT_RUNBOOK.md`. Preserve raw outputs and candidate hashes in the active execution run.

### Additional policy and storage gates

Before enabling the corresponding workflow, resolve each value from its approved policy/configuration source. These names are read directly by the current Central source; the runbook intentionally supplies no operational values. Missing or invalid values keep the relevant action unavailable or denied.

The bound source `effective040` run records `G-POLICY`, `G-SECURITY-CONFIG`, `G-COMMERCIAL-CONFIG`, `G-CRYPTO-PROFILE`, and `G-STORAGE-CONFIG` as `EXPLICIT_DEPENDENCY_NOT_FABRICATED_COMPLETE`. Its lifecycle policy leaves lease, offline grace, rollback tolerance, first-redemption TTL, retention duration, and after-extension policy null. Treat the parser limits below as code validation limits only; they are not approved operational settings. This runbook does not activate those gates or authorize guessed, synthetic, or default operational values.

| Workflow | Required configuration | Source behavior when absent or invalid |
|---|---|---|
| Database connection and owned-target guard | `CENTRAL_DATABASE_CONNECTION_STRING`, `CENTRAL_PG_PORT`, `CENTRAL_PG_DATABASE`, `CENTRAL_PG_ROLE`, `CENTRAL_PG_MIGRATION_ROLE`, `CENTRAL_PG_PID`, `CENTRAL_PG_BINARY`, `CENTRAL_PG_OWNERSHIP_MARKER`, `CENTRAL_PG_DATA_DIR` | The Central connection guard requires each live target identity to match the process, marker, PostgreSQL 18 instance, database, and separate runtime/migration roles. Do not copy these values from old evidence; freshly attest the owned target before launch. |
| Development storage roots | `CENTRAL_SIGNER_KEY_STORE_ROOT`, `CENTRAL_OBJECT_STORAGE_ROOT`, `CENTRAL_PAYMENT_STORAGE_ROOT` | Protected signer and disk-storage providers require explicit owned roots. Keep them separate from POS or shared data, and preserve path/reparse protections. |
| Login/MFA/bootstrap rate limiting | `CENTRAL_AUTH_RATE_LIMIT_MAX_ATTEMPTS`, `CENTRAL_AUTH_RATE_LIMIT_WINDOW_SECONDS` | Throttled POST routes return 503 with `RATE_LIMIT_CONFIGURATION_REQUIRED`. Both must be positive integers. |
| Installed-request proof | `CENTRAL_INSTALLATION_PROOF_WINDOW_SECONDS` | Installation request proof is rejected unless the value is an integer from 1 through 600 seconds. |
| First-install bootstrap | `CENTRAL_BOOTSTRAP_CHALLENGE_TTL_SECONDS` | Bootstrap challenge creation is unavailable unless the lifetime is 1 through 3600 seconds. |
| Independent approvals | `CENTRAL_DUAL_APPROVAL_ACTIONS` | Approval checks require an explicit action matrix. `NONE` is allowed only as a labelled isolated policy fixture, never as an implicit operational default. |
| Encrypted object upload | `CENTRAL_STORAGE_RETENTION_POLICY_ID`, `CENTRAL_STORAGE_RETENTION_DAYS`, `CENTRAL_STORAGE_LEGAL_HOLD_ENABLED`, `CENTRAL_UPLOAD_SESSION_TTL_SECONDS`, `CENTRAL_UPLOAD_MAX_OBJECT_BYTES`, `CENTRAL_UPLOAD_MAX_CHUNK_BYTES` | Upload initialization requires an exact configured retention-policy ID, valid retention/hold and session-lifetime values, and explicit object/chunk bounds. The source enforces 1–100 MiB for object size, chunk size no greater than object size, 1–36500 retention days, and 60–86400 session seconds. |
| Backup download grants | `CENTRAL_BACKUP_DOWNLOAD_GRANT_TTL_SECONDS` | Grant creation remains unavailable unless TTL is 1 through 300 seconds. |
| Activation-token issuance | `CENTRAL_ACTIVATION_TOKEN_POLICY_ID`, `CENTRAL_ACTIVATION_TOKEN_POLICY_VERSION`, `CENTRAL_ACTIVATION_DELIVERY_CHANNEL_ID`, `CENTRAL_ACTIVATION_FIRST_REDEMPTION_TTL_SECONDS` | Issuance requires explicit nonempty policy/channel IDs and positive version/lifetime values. |
| Activation-delivery secret protection | `CENTRAL_ACTIVATION_DELIVERY_KEYS_JSON`, `CENTRAL_ACTIVATION_DELIVERY_ACTIVE_KEY_ID` | Keep key material only in the approved secret store; configure an active key only with an approved delivery workflow. Never place values in this runbook or evidence. |
| Hosted outbox | `CENTRAL_OUTBOX_ENABLED`, `CENTRAL_OUTBOX_BATCH_SIZE`, `CENTRAL_OUTBOX_MAX_ATTEMPTS`, `CENTRAL_OUTBOX_POLL_MS`, `CENTRAL_OUTBOX_LEASE_SECONDS`, `CENTRAL_OUTBOX_RETRY_BASE_MS`, `CENTRAL_OUTBOX_RETRY_MAX_MS` | Enabled must parse true; batch is 1–1000, attempts 1–100, poll 10–3600000 ms, lease 1–3600 seconds, retry base 1–3600000 ms, and retry max is at least the base and at most 86400000 ms. Otherwise the worker stays unavailable. |
| Storage reconciliation | `CENTRAL_STORAGE_RECONCILIATION_ENABLED`, `CENTRAL_STORAGE_RECONCILIATION_BATCH_SIZE`, `CENTRAL_STORAGE_RECONCILIATION_POLL_MS`, `CENTRAL_STORAGE_PENDING_GRACE_SECONDS` | Enabled must parse true; batch is 1–1000, poll 10–3600000 ms, and pending grace 60–86400 seconds. Otherwise the service stays unavailable. |
| Activation-delivery cleanup | `CENTRAL_ACTIVATION_DELIVERY_CLEANUP_ENABLED`, `CENTRAL_ACTIVATION_DELIVERY_CLEANUP_BATCH_SIZE`, `CENTRAL_ACTIVATION_DELIVERY_CLEANUP_POLL_MS` | Enabled must parse true; batch is 1–1000 and poll 100–86400000 ms. Otherwise the service stays unavailable. |

`CENTRAL_INTERNAL_SIGNER_TOKEN` is an internal endpoint credential, not an operator setting to invent; keep it in the secret store and configure it only for an explicitly authorized local workflow. `CENTRAL_INSTALLATION_REQUEST_V1`, `CENTRAL_INSTALLATION_BINARY_REQUEST_V1`, and `CENTRAL_INSTALLATION_BOOTSTRAP_V1` are source-defined protocol profile identifiers, not operator-provided secret values. Synthetic values are permitted only in labelled disposable tests. Feature unavailability from missing policy is a stop condition; do not fill gaps with guessed values.

## Observations and incident response

- Treat a missing, stale, or failed hosted-service observation as unavailable/unknown or degraded according to its source implementation. Do not infer health from process existence or a successful portal response.
- Stop only a process whose executable, command line, owner, and target have been freshly verified as belonging to the current disposable run. If ownership cannot be proved, stop and escalate; do not use broad process-name termination.
- Preserve logs and evidence before changing an owned fixture. Do not delete directories, clusters, object roots, or databases as generic cleanup. Cleanup requires a separate exact-path and resource-ownership check.
- Do not enable production policy from synthetic test values. `effective040` approvals and configuration gates remain authoritative for operational activation.

## Owned development lifecycle

These procedures apply only to a disposable Central environment owned by the current execution run. They are not production installation or change authority.

### Install and initialize

1. Use a dedicated Central worktree and an empty, newly initialized PostgreSQL 18 cluster bound to loopback. Record the executable, data directory, postmaster PID, non-default port, ownership marker, and run ID. Do not adopt an existing or shared cluster.
2. Create separate migration-owner and runtime roles, plus a separately controlled local cluster administrator. The runtime role must not own relations, create databases or roles, bypass row-level security, or update/delete/truncate audit records. Keep role passwords in the local secret store.
3. Set the explicit database connection, signer root, storage roots, target guard, authentication lifetimes, hosted-service options, and approved policy-derived bounds. Missing required values are a stop condition; synthetic fixture settings are for labelled tests only.
4. Build the scoped Central solution and apply the checked-in migration chain with the migration identity, following `SERVER_DEPLOYMENT_RUNBOOK.md`. Read back the applied migration IDs, Central table count, role grants, and audit mutation denial before starting the API.
5. Start the API only after those checks pass. Confirm the process command line and loopback binding, then perform an authenticated smoke journey and inspect persisted state and audit records using the runtime identity.

### Update and rollback

1. Before an update, record the exact candidate commit and overlay hashes, current schema migration IDs, database identity, configuration source identifiers, and current health observations. Take a recovery bundle using the owned disposable recovery rehearsal and verify the bundle manifest hash before proceeding.
2. Apply updates to a fresh disposable target first. Run the source-defined migration workflow and candidate-specific regression/browser evidence. Compare schema/data invariants and authorization behavior before promoting that candidate within the disposable environment.
3. Do not improvise reverse SQL or downgrade migrations. If an update fails, stop new writes to the affected owned instance, preserve logs and evidence, and restore the last verified bundle into a new isolated target database/cluster. Re-attest the target and source identities, validate the recovered data and hosted-worker state, and only then resume the disposable API against the recovered target.
4. Keep the failed target intact until its evidence is captured. Never delete or overwrite a source database as part of rollback. A production cutover/rollback procedure requires separately approved infrastructure, backup, retention, and recovery-point objectives that are not established here.

### Restore a development recovery bundle

The supported rehearsal is `scripts/central/Invoke-CentralDisposableRecoveryRehearsal.ps1`. Change233 demonstrates a current-candidate owned source capture and separate-cluster restore with PostgreSQL 18, 38 Central tables, 23 migrations, 59 storage references including a labelled synthetic 100 MiB ciphertext object, protected development signer/trust proof, runtime least privilege, independent readback, and two hosted-worker restarts with idempotent replay. Change234 terminated the actual full wrapper while that object was being copied, leaving only a 17,301,504-byte `.partial` staging file and no published destination or API start. Change235 then restored the exact same verified bundle into a new target and matched all 59 storage references. Restored storage references are written to unique same-directory `.partial` files, flushed and checked for expected hash/length, then moved into place; existing destinations are accepted only when their hash/length match. This is a development rehearsal, not production key recovery. Exact results are under `evidence/change-233/`, `evidence/change-234/`, and `evidence/change-235/`.

1. Obtain a fresh quiescence proof for the exact source, independently attest a distinct empty loopback target cluster, and prepare a new database name matching `central_restore_<lowercase-run-id>` (up to 40 lowercase letters/digits/underscores after the prefix).
2. Verify the bundle manifest SHA-256 independently. Keep the source and previous targets preserved. Supply only fresh, attested environment settings and credentials through the local secret store; do not place secrets in PowerShell history or evidence.
3. Invoke the script with the actual parameter names; replace every placeholder with a verified run-specific value:

   ```powershell
   $recovery = @{
       TargetDatabase = 'central_restore_<run_id>'
       EvidenceRoot = '<absolute_owned_evidence_directory>'
       QuiescenceProofPath = '<fresh_source_quiescence_proof.json>'
       RestoreBundlePath = '<verified_bundle_directory>'
       ExpectedBundleManifestSha256 = '<64_hex_manifest_sha256>'
   }
   & .\scripts\central\Invoke-CentralDisposableRecoveryRehearsal.ps1 @recovery
   ```

4. Treat any identity, ownership, integrity, schema, content, least-privilege, signer, trust, storage, or outbox invariant failure as a failed restore. Preserve its evidence and leave the API stopped. Do not weaken a guard or retry against a different target without fresh attestations. Change156 negative controls reject mismatched manifest hash, altered archive bytes, mismatched ownership marker, and invalid target administrator credentials before target database creation. A separate controlled PostgreSQL restore interruption terminated the migration-role backend and confirmed transactional rollback left no Central schema or relations. Change234 additionally interrupts the actual full wrapper during storage copy and proves that the incomplete staging file is not published; it does not cover every other possible interruption stage.
5. On success, independently read back the restored database and storage invariants, verify the runtime role still cannot mutate audit records, and confirm the hosted worker's idempotent effect state before using the target. Change235 independently matched all 59 storage references, including the 100 MiB fixture, after the Change234 interruption and left no incomplete target accepted as usable. Preserve the source, bundle, interrupted target, and recovered target until the rehearsal record is closed.

### Provider configuration and cutover

No live payment, notification, licensing, object-storage, or key-management provider is commissioned by this repository runbook. Keep external delivery disabled and retain human payment review and human license approval. Before any provider is considered, require an approved provider identity, endpoint, credential source, network policy, timeout/retry/idempotency contract, data-handling rule, health signal, rotation/revocation procedure, and rollback owner. Bind those values through the approved configuration source and fail closed if any are absent. Do not test a live provider or send customer messages as part of disposable development recovery.

## Current acceptance note

This runbook is not a production operator handoff. Changes233–235 are the latest current-candidate positive capture/restore, actual full-wrapper storage-copy interruption, and clean same-bundle/new-target recovery chain. Change080 records current-script recovery rejection and no-effect controls, while Change156's database interruption remains a scoped control. Change236 separately proves only the explicitly selected 21-to-23 schema transition with legacy-data preservation, backfill, denial, least-privilege and idempotent reapply checks; it does not certify every supported-release upgrade or rollback. These controls cover only their stated failure points. Older CHECKPOINT entries are historical snapshots, not current candidate validation. Central acceptance remains open, including joined browser recovery, the eight browser journeys, route/DTO and complete source-atom reconciliation, Windows link/reparse fixtures, Linux same-UID/race proof, complete operator lifecycle rehearsal, final regression, distinct semantic review, and current full-candidate delivery/readback.

Current replay verification (Change232): pending count and all required message/effect/notification states are read from one PostgreSQL statement snapshot within the 60-second gate. Successful observation files and any timeout snapshot are retained under the rehearsal evidence directory. The prior Change231 failure is preserved; independent target readback showed completed dispatch after its per-message observation gate timed out. Change232's fresh full rehearsal passes, and eight focused gate controls require incomplete pending/effect/notification states to wait rather than pass. Changes233–235 add the large-object/full-wrapper interruption/recovery sequence, and Change236 adds the scoped 21-to-23 schema transition. Joined browser recovery and broader operator/source acceptance remain open.
