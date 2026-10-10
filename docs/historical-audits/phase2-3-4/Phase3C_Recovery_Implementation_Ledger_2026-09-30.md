# Phase 3C Signed Owner PIN Recovery — Continuation Ledger

Updated 2026-09-30. This ledger supersedes the earlier local-administrator recovery design in this file. It records implementation and isolated verification only; it is not Phase 3C closure or production authorization.

## Reconstructed checkpoint

- Phase 1, Phase 2, Phase 3A, and Phase 3B remain certified and locked. No Phase 1/2 source changes were made for this recovery work.
- The last recorded installed production baseline remains Release 1.0.5 with PostgreSQL 18.6, all 18 production migrations, loopback Server readiness green, and Server/Worker/PostgreSQL services running. No current production state was changed in this continuation.
- A prior 1.0.6 publish and PostgreSQL rehearsal were made for local-administrator recovery. They are preserved but are superseded for certification by the user's explicit decision to require canonical signed Recovery Authorization.
- No build, test, or database operation was still running when this continuation began. The only running .NET processes observed were idle reusable MSBuild nodes.
- The unfinished scope was to replace the local-administrator/direct-database recovery tool with a Shop Server API backed by a fail-closed signed-authorization verifier.

## Compact execution ledger

| Gate | Scope | Evidence required | Command / evidence | Status |
|---|---|---|---|---|
| Phase 1 / 2 | Preserve certified behavior | Prior signed-off reports; no Phase 1/2 behavior edits | Existing governance records and workspace diff | LOCKED / preserved |
| Phase 3A / 3B | Preserve production/database/runtime closure | Existing approved evidence; no re-open absent regression | Existing reports; no production mutation | LOCKED / preserved |
| 3C policy | Canonical recovery authorization | User selected signed authorization; distinct Recovery trust key and issuer | User response; Architecture Report §54 | PASS / governing requirement |
| 3C trust provisioning | Governance-issued Recovery public key and issuer | Approved public key + issuer identifier provisioned via approved mechanism | Source and installed key directory inspection | WAITING FOR GOVERNANCE PROVISIONING; production fail-closed |
| 3C implementation | Server-authoritative Owner PIN recovery | Signed RS256 envelope; license/device/action/expiry/nonce/issuer/target binding; no direct DB utility | Source inspection; independent bounded security review | PASS / bounded implementation review |
| 3C schema | Atomic one-time nonce consumption | Forward-only migration and filtered unique index | Migration `20260930065058_Phase3COwnerPinAuthorizationConsumption`; PostgreSQL 18 zero-to-latest and 19→20 rehearsal | PASS / isolated PostgreSQL 18 / Npgsql |
| 3C focused unit | Signature, trust, binding, expiry, replay, audit, secret handling | Unit tests; no weakened assertions | `dotnet test tests\EdgeRetails.UnitTests\EdgeRetails.UnitTests.csproj --configuration Release --no-restore` | PASS: 711 passed, 0 failed, 0 skipped; exit 0 |
| 3C HTTP contract | Exact loopback route, middleware bypass only for exact route, unprovisioned trust returns 503 | Real ASP.NET Core test host contract tests | `dotnet test tests\EdgeRetails.IntegrationTests\EdgeRetails.IntegrationTests.csproj --configuration Release --no-restore --filter FullyQualifiedName~Phase3SetupApiContractTests` | PASS: 6 passed, 0 failed, 0 skipped; exit 0 |
| 3C PostgreSQL | Real provider, migration history, existing-data preservation, replay and concurrency | PostgreSQL 18 / Npgsql; zero-to-latest; 18→19→20; handler integration tests | `powershell.exe -NoProfile -ExecutionPolicy Bypass -NonInteractive -File .\scripts\Invoke-Phase3COwnerRecoveryPostgresRehearsal.ps1` | PASS: Provider = PostgreSQL 18 / Npgsql, server_version_num=180006, history=20, 2 existing audit fixtures preserved, 3 passed / 0 failed / 0 skipped; exit 0 |
| EF model alignment | Migration snapshot matches current model | No pending model changes | `dotnet ef migrations has-pending-model-changes --project .\src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj --startup-project .\src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj --context EdgeRetailsDbContext --configuration Release` | PASS; exit 0 |
| Source builds | Complete Debug and Release solution builds with warnings as errors | Build succeeds, 0 warnings/errors | `dotnet build EdgeRetails.sln --configuration Debug --no-restore --warnaserror`; Release equivalent | PASS / both exit 0 |
| Production backup / migration / PIN reset | Approved backup, provisioned authorization, secure PIN entry, production audit result | Governance key+issuer; verified operational backup; supervised installed Server route | Not run; key and issuer are not provisioned | NOT AUTHORIZED / not a completed gate |
| Authenticated installed Desktop / POS smoke | User signs in; navigation/catalog/POS/clean restart verified | Operator-supplied PIN in the application and approved recovery token if reset is needed | Not run; no production credential was changed | OPEN |
| Phase 3D | Full regression, Golden Trace, Hostile/Recovery Trace | Phase 3C production closure first | Not started | OPEN / gated on 3C |
| Phase 3E | Evidence checkpoint, workspace freeze, fresh independent final certifier | All Phase 3D gates terminal PASS | Not started | OPEN / gated on 3D |
| Phase 4 | Prohibited | No Phase 4 implementation | Not started | NOT STARTED |

## Implemented recovery boundary

- The WPF Recovery utility runs asInvoker, calls only `http://127.0.0.1:7150`, and has no Infrastructure/Application project reference or database configuration resolver.
- The Server exposes the exact `/api/recovery/owner-pin` route for the active Owner list and signed recovery request. The controller rejects non-loopback callers; both authentication middleware layers allow only that exact route.
- The Server validator checks RS256/SHA-256 signature and binds issuer, LicenseId, DeviceId, `OWNER_PIN_RESET`, issued/expiry times (maximum ten-minute lifetime), nonce, and TargetUserId. It independently verifies the installed signed license and current device using the existing license path. Recovery trust does not fall back to the License key.
- The production trust provider currently returns no Recovery key or issuer and therefore rejects all recovery attempts safely with `recovery.authorization_not_provisioned` / HTTP 503. Governance provisioning is required before it can accept tokens.
- A valid nonce is recorded as `USER_PIN_RECOVERY_AUTHORIZATION_CONSUMED` in the same PostgreSQL SaveChanges transaction as PIN/session/audit changes. A unique filtered index prevents concurrent reuse. Owner `User.Version` remains a concurrency fence; active sessions are revoked on success. PINs and authorization bodies are not written to audit summaries.
- Migration `20260930065058_Phase3COwnerPinAuthorizationConsumption` is forward-only. The older migration was not modified. The rehearsal verifies fresh zero-to-latest history=20, an existing database through migration 19→20 while preserving two audit rows, and real Npgsql handler/replay/concurrency behavior.

## Test corrections and review notes

- An initial focused unit run found that EF merged same-column filtered indexes in runtime metadata. Both indexes now have distinct EF model names; the additive migration and model snapshot agree. This was an implementation defect, not an assertion change.
- One added PostgreSQL test initially failed the xUnit analyzer because it used `Assert.Single` after `Where`; it now uses the predicate overload with identical semantics.
- One test rehearsal initially relied on an Owner role seeded by another test. The fixture now creates that role when absent; no production assertion or skip was changed.
- The first API test-host run had no loopback `RemoteIpAddress`; the test host now supplies loopback explicitly, while the separate non-loopback test supplies a remote address. This is a test harness correction.
- The bounded independent security review rechecked the current migration, snapshot, PostgreSQL error constraint, middleware, UI authority boundary, and nonce transaction. It withdrew an earlier stale missing-index finding and reported no remaining actionable Critical/High/Medium findings. This is not the required Phase 3 final certification.

## Exact current stopping point

Resume at governance provisioning of the approved Recovery public key and issuer identifier. Then add the approved key through the canonical separate trust mechanism, rerun signed verifier/API/PostgreSQL tests and full regression, obtain and verify the required production backup, and only then perform the authorized production recovery and installed Desktop/POS smoke. Do not start Phase 3D until Phase 3C production and authenticated smoke gates are complete. Do not start Phase 4.

No production backup was taken for this continuation because no production schema or credential mutation was performed. No release was installed, no PIN was reset, no production account was seeded with mock data, and no Git commit/push/reset/clean was performed.
