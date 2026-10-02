# Phase 3 Recovery Governance Bootstrap

Status: implementation in progress under the Phase 3 final-completion authorization dated 2026-09-30. This document records the approved Recovery authority boundary; it does not certify production recovery or close Phase 3.

## Authority and key handling

- The only permitted action is `RESET_ACCOUNT_PIN`.
- Issuance uses an RS256 signature over the serialized payload. Each authorization binds the issuer, installed license ID, current device ID, target Owner user ID, action, issue/expiry timestamps, and a new nonce.
- The Recovery utility creates a five-minute authorization in memory and posts it only to `http://127.0.0.1:7150/api/recovery/owner-pin`. It does not display or persist the authorization and has no database connection or database project reference.
- The governance private key is created in the current Windows user's Microsoft Software Key Storage Provider as a 3072-bit RSA signing key with export disabled. User-profile metadata stores only the issuer identifier and CNG key name. The elevated provisioner reopens that non-exportable signer and compares its RSA modulus/exponent with the staged public key before creating or changing the machine-wide trust directory; a missing or mismatched signer fails closed.
- The Shop Server reads only `RecoveryAuthorization:IssuerId` and `RecoveryAuthorization:PublicKeyPem` from `C:\ProgramData\EdgeRetails\recovery\trust.json`. The directory is protected for `SYSTEM` and local `Administrators`, and the Server receives only public verification material. The provisioner refuses automatic key replacement and restarts the installed Server after provisioning.
- The Server's trust provider fails closed on missing, malformed, undersized, private, or untrusted configuration. Server configuration gives the protected machine trust file precedence over per-user configuration. Test hosts do not load the production trust file.
- The recovery context endpoint is loopback-only and exposes only the verified license ID, device ID, and configured public issuer identifier. Existing owner enumeration and recovery routes remain loopback-only and use the exact authentication middleware allowlist.
- The Server accepts authorizations for at most ten minutes. The governance utility issues five-minute authorizations. Nonce consumption and account credential mutation remain in the same persistence transaction and use the already-rehearsed forward-only migrations 19 and 20.

## Release containment

`scripts/Publish-Release.ps1` now refuses an existing output path, scans the harvested publish tree before WiX for private-key file extensions/names and PEM private-key markers, scans final MSI/Bundle raw payloads for those PEM markers, and writes SHA-256 inventory entries for every published payload file. The Recovery executable product description now describes signed governance recovery. No Recovery private key is part of source, publish, installer, or runtime configuration.

The approved new output path is `artifacts/phase3c-recovery-release-20260930-05`; it must be confirmed absent before publication. Existing release artifacts `-01` through `-04` are preserved and are not candidates for this signed-governance release.

## Evidence ledger

| Gate | Evidence | Status |
|---|---|---|
| Reconstructed production state | PostgreSQL 18.6, production migration history 18, categories=2, no hold table; Server/Worker running; Server loopback-only and `/api/system/ready` Ready | PASS, read-only census; no production mutation |
| Governance trust implementation | Per-user non-exportable CNG signer; public-only protected Server trust file; fail-closed configured provider; exact action and context binding | Implemented; independent bounded review pending |
| Release containment | No-overwrite output, pre-WiX tree scan, final artifact marker scan, full payload SHA-256 inventory | Implemented; PowerShell parse/functional check and Release publish pending |
| Debug solution build | `dotnet build EdgeRetails.sln --configuration Debug --no-restore --warnaserror` | PASS, exit 0, 0 warnings, 0 errors |
| Recovery validator/trust tests | `dotnet test tests\EdgeRetails.UnitTests\EdgeRetails.UnitTests.csproj --configuration Debug --no-restore --filter FullyQualifiedName~SignedRecoveryAuthorizationValidatorTests` | PASS, 13 passed, 0 failed, 0 skipped, exit 0 |
| Recovery API contract | `dotnet test tests\EdgeRetails.IntegrationTests\EdgeRetails.IntegrationTests.csproj --configuration Debug --no-restore --filter FullyQualifiedName~Phase3SetupApiContractTests` | PASS, 6 passed, 0 failed, 0 skipped, exit 0 |
| Test expectation correction | A newly added context test expected 503 but the established test fixture provides a valid installed license; corrected the new test to assert 200 with nonempty identity binding and no configured issuer. Existing tests/assertions were not weakened. Classification: `ASSERTION_CHANGE`; independent reviewer must verify rationale. | Review pending |
| Production key/trust provisioning | CNG key, issuer, protected ACL verification, installed Server restart and correct-issuer handshake | NOT RUN; do not accept as PASS |
| Fresh production backup / actual archive restore | New backup SHA-256, TOC/full archive read, restore exact archive to disposable PostgreSQL 18, 18→20 clone rehearsal | NOT RUN; script authoring in progress |
| Production migrations 19→20 | Exact history/schema/invariants after verified fresh backup and actual archive restore | NOT RUN; production remains at migration 18 |
| Release 1.0.6 installation and runtime | Installed manifest hash match, Server/Worker readiness, desktop/recovery/POS smoke | NOT RUN |
| Full Phase 3D/3E and independent final certification | Complete mandatory regression, Golden/Hostile traces, freeze, fresh read-only certifier | NOT RUN |
| Phase 4 | Explicitly prohibited by the authorization | NOT STARTED |

No production CNG key has been generated, no trust file provisioned, no new production backup taken, no production database schema or account credential changed, and no Phase 4 work started as of this ledger update.

