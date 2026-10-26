# Central development binary upload

This describes the isolated development implementation and contract harness. Production cryptographic profile, provider commissioning and policy activation remain gated by effective040 source decisions.

## Upload requests

Initialize with `POST /api/licensing/v1/backups/uploads`. The persisted session binds installation, shop, purpose, object identity, whole-object digest/length, retention metadata and expiry. Its response includes `/api/licensing/v1/uploads/{uploadId}/content`.

Upload chunks using `PUT` to that content route with `application/octet-stream`, a complete `Content-Range: bytes from-to/total`, exact `Content-Length` and hexadecimal `chunk-sha256`. No filesystem path is accepted. Exact byte-range retries return the current accepted offset; changed bytes or a gap conflict. Database offset changes and filesystem tail reconciliation share the upload-ID PostgreSQL advisory transaction lock.

The binary proof envelope uses development profile `CENTRAL_INSTALLATION_BINARY_REQUEST_V1` and the existing canonical JSON serializer/RS256 validator. It contains the established fields `purpose`, `keyId`, `installationId`, `shopId`, `licenseId`, `timestamp`, `nonce`, `httpMethod`, `httpPath` and `bodyDigest`, plus:

```json
{"binaryRange":{"from":0,"to":16,"totalLength":41,"contentLength":17,"chunkSha256":"<lowercase SHA-256>"}}
```

`bodyDigest` is lowercase SHA-256 of the exact raw octets. `chunkSha256` must agree with the received bytes. Range fields and length are canonical JSON numbers. `httpPath` includes the query string. Every request uses a fresh nonce; persisted replay and active installation/key checks remain required. JSON requests keep profile `CENTRAL_INSTALLATION_REQUEST_V1` and canonical JSON body hashing. Binary authentication rejects missing/oversize/inconsistent declared bounds before accepting the signature, hashes incrementally and reads at most the declared length plus one byte.

Finalize via the returned finalize route using object identity, expected length and digest. Completed staging is read and hashed in full. Permanent ciphertext is written to a private temporary, flushed to disk and published by an atomic move without overwrite. A retry may reuse identical existing content; different content conflicts. Database metadata is committed through the operation ledger. Integrity failures roll back that operation, then persist a version-checked quarantined session and audit in a separate upload-locked transaction. A concurrent newer revision is preserved.

Successful backup downloads require installation proof and matching persisted shop and encrypted-backup purpose. Responses provide `Digest`, immutable `X-Object-Id`, content length and byte-range support. Support/payment objects require their own authorized workflow.

## Explicit configuration

There are no operational fallbacks for these values:

| Variable | Validation |
| --- | --- |
| `CENTRAL_UPLOAD_MAX_OBJECT_BYTES` | Positive and within the existing 100 MiB domain structural bound |
| `CENTRAL_UPLOAD_MAX_CHUNK_BYTES` | Positive and no greater than the configured object bound |
| `CENTRAL_STORAGE_RECONCILIATION_ENABLED` | Explicit `true` to schedule reconciliation |
| `CENTRAL_STORAGE_RECONCILIATION_BATCH_SIZE` | 1–1000 |
| `CENTRAL_STORAGE_RECONCILIATION_POLL_MS` | 10–3600000 |
| `CENTRAL_STORAGE_PENDING_GRACE_SECONDS` | 60–86400 |

Existing root ownership, retention policy/term/hold, upload-session TTL and installation-proof replay-window configuration are also required by their respective operations. Test module initializers label 100 MiB object/10 MiB chunk values as isolated compatibility fixtures; these are not approved production policy.

The configured hosted reconciler pages through persisted sessions and tombstones. Under the upload lock it removes uncommitted tails, persists expiry with an audit, retries leftover staging cleanup after committed publication and preserves incomplete bytes under hold or an unexpired retention period. Missing acknowledged bytes remain an inconsistency; their database offset is never reset from filesystem observations. Tombstoned ciphertext is removed only after hold/retention checks. Stale, correctly named private publication temporaries are removed after the explicit grace; exclusive active writer handles defer cleanup.

Initialization, finalization and reconciliation share a global immutable object-ID lock. Initialization rejects published/tombstoned metadata or an identity reserved to another persisted shop; the original operation's idempotent replay still recovers its existing session. Existing published/tombstoned metadata rejects a new publication before permanent bytes are touched. A permanent orphan can be reconciled only when its persisted upload is expired/quarantined, retention has passed, there is no hold, existing metadata or other protected upload owner across any shop, and actual bytes match the expected length/digest. The reconciler commits a tombstone and audit before physical removal, so a removal failure remains retryable and the identity cannot be reused. Finalized staging is removed only after permanent ciphertext length/hash verification; corrupted permanent content preserves staging and reports inconsistency. Pending-temporary cleanup uses an ordered progressing cursor, allowing later files to be processed while the first file remains locked.

## Proof and remaining acceptance

`StorageRecoveryTests` exercises immutable retry/conflict, interrupted publication, new-provider resumption and stale/active temporary handling. `InstallationBinaryProofTests` exercises explicit bounds and rejection without reading an oversized body. `StorageReconciliationTests` uses guarded PostgreSQL for persisted offset recovery, missing-byte denial, expiry/audit/hold/retention, finalized staging, tombstone reconciliation and durable quarantine. The signed actual-host lifecycle test covers altered range after signing, retry conflicts, whole-object completion, purpose denial, digest headers and partial download bytes.

The owned `Invoke-StorageProcessCrash012b.ps1` rehearsal starts an actual HTTPS API process against a separate schema-only disposable PostgreSQL target. It observes the exact upload transaction inside a temporary delay trigger after file flush, kills only that owned API process, verifies the persisted offset remained 3 while the file contained 5 bytes, and starts a new API process with configured reconciliation. The file returns to 3 bytes and a freshly signed chunk retry succeeds at offset 5. The fixture uses a temporary encrypted transport certificate pinned by its client; no OS trust/security setting changes are needed.

`Invoke-StoragePublicationCrash012.ps1` exercises the other publication window in another schema-only target. It kills the owned HTTPS API after permanent-file publication while the object metadata INSERT is delayed and still uncommitted. The permanent file survives without metadata. Only that synthetic upload row's expiry/retention timestamps are then advanced to past values. A new API process durably tombstones/audits and removes the orphan; identity reuse is denied409 and further polls retain one tombstone audit. This controlled timestamp fixture does not approve operational retention policy.

Still required: all required Windows symbolic-link/reparse cases, metadata-issued short-lived download grants, support attachment downloads, the remaining server product workflows and final independent acceptance. Production-provider commissioning remains gated. Power-loss durability is not established by these process rehearsals. Final server acceptance remains open until the complete original scope and independent review are proven.
