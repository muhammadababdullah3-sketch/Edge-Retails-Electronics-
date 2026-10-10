# Backup protection lifecycle implementation authority

Canonical source: architecture §56. This is independent of license, update, Owner PIN Recovery signing, maintenance and journal integrity keys. No operational secret is generated or provisioned by writing this design.

## Required implementation

1. Explicit provisioning generates a random 256-bit BMK and independent 256-bit Owner RK once. Existing legacy backup encryption authority must be imported intact when present; never replace it silently. No automatic per-backup generation.
2. Server/Worker read a common protected local ring beneath approved ProgramData state. Windows DPAPI LocalMachine plus restrictive SYSTEM/Administrators ACL permits approved service loading. Corrupt or inaccessible existing authority fails closed; no fallback to a new key.
3. Owner stores RK separately. Local ring contains only authenticated RK-wrapped BMK, key/version metadata and OS-protected BMK. The RK itself must not be in the ring, database, archive, manifest, logs, chat, screenshots, or source-controlled settings. Export via a deliberately selected custody destination; an on-machine second copy does not prove offline custody.
4. Every new archive's authenticated manifest carries encryption/key version and authenticated wrapped BMK metadata. AES-GCM data encryption and derived manifest HMAC stay separate purposes. Select the same version throughout creation; retained versions support old decryptability. Preserve legacy manifest serialization/authentication when optional new metadata is absent.
5. Replacement machine recovery requires Owner RK and a selected authenticated recovery envelope; unwrap/authenticate before writing new protected local authority. Wrong key/tampered metadata fails closed. Do not silently overwrite an established ring. Historical versions remain available after explicit rotation.
6. Existing Environment provider is legacy import/restore compatibility only. Installing source alone is not a live fix. Provisioning and actual fresh backup/PG18 restore need an approved installed release and human custody/elevation handoffs.

## Evidence chain

NEW_COVERAGE RED for missing canonical lifecycle type, then crypto/provision/restart/rotation/legacy/replacement recovery/tamper/ACL/failure tests. Actual installed failure is captured independently before changing backup error categories. Root cause must be linked to authenticated UI action, runtime PID/timestamp/static exception allowlist and controller path. Bounded probe never persists raw trace data or process memory.
