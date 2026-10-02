# Phase3C installed Desktop smoke

Status: **USER ACTION REQUIRED — existing user's PIN must be entered directly in the installed Desktop.** Phase3A and3B are certified/locked. Phase3C is not certified. No Phase3D/E work started.

Installed executable: `C:\Program Files\Edge Retails\EdgeRetails.Desktop.exe`, FileVersion1.0.5, verified release SHA256 `141CCAE2FFEA6F981E390389BF3EE2AB3BB6238D0731904842E783822F3E60A8`.

| Gate | Scope | Evidence Required | Command/Test | Status |
|---|---|---|---|---|
| Installed executable lifecycle | Startup/window/normal close | Terminal existing smoke script result | Windows PowerShell `scripts/SmokeTest-ReleaseExe.ps1 -ExePath <installed EXE>` | PASS exit0; log in artifacts/phase3b-cutover-20260929-201201/desktop-start-close-smoke.txt; startup dialog alone is not full app readiness |
| Fresh environment startup | Registered terminal context | Existing Windows User terminal secret supplied to child environment without output | Launch installed EXE through ProcessStartInfo with existing User EDGE_RETAILS_TERMINAL_SECRET | PASS to responsive account/PIN screen PID13756 |
| Earlier login-message finding | Reobserve on final runtime | Account view with ordinary PIN prompt | Windows UI Automation read of installed WPF window | Previous "Sign in is unavailable" absent; "Select your account to continue" and "Enter your PIN to continue" present |
| API-only path | Live installed Desktop→Server | Loopback7150 connection and composition inspection | Get-NetTCPConnection owning PID13756; App.xaml.cs/CreateFromEnvironment remote composition | PASS observed connection127.0.0.1:7150; no5432 Desktop connection; production path calls CreateRemoteDesktopServiceCollection/RemoteApplicationGateway |
| Authentication/navigation/catalog/POS | Actual authenticated installed app | Authorized existing identity; responsive navigation/catalog/POS | User enters own PIN directly, then bounded UI smoke | WAITING FOR USER SIGN-IN |
| Final close/restart | Authenticated app lifecycle | Clean close and restart after full smoke | Pending completed authenticated smoke | OPEN |

Initial unrefreshed launch PID2320 showed a missing terminal-authentication configuration dialog. Read-only environment inspection established terminal secret exists in Windows User scope but was absent from the long-running agent process environment. That dialog closed normally; relaunch inherited the existing authorized User secret explicitly, reaching account/PIN screen. No secret was generated, guessed, printed, changed, or sent to chat. No application source change required. This does not bypass authentication.

No available authorized PIN/test identity was documented; no PIN was entered or invented. User was asked to sign in directly in the open app and report completion. Exact resume: inspect installed PID13756 after sign-in, verify canonical navigation, catalog/search and POS bootstrap; then normal close/restart and certification. Keep3D/E gated until3C passes.

## Follow-up — POS responsiveness request and forgotten PIN

At 2026-09-29 21:50 +05:00 the user reported that POS was not responding and that the account PIN was forgotten. The installed Desktop process PID13756 was still present/responding; Server and Worker were Running/Automatic and `/api/system/ready` remained Ready, connected, with no pending migrations. This does not establish whether the current Desktop screen is authenticated or whether the POS defect is reproducible.

The request was checked against the current v1 API and recovery contract. `AuthController` exposes account listing, login, logout, and current-session routes; it has no PIN reset route. The API parity matrix classifies PIN reset as `NOT_PART_OF_CURRENT_V1`, deferred to Phase 6 operator tooling. The older architecture report describes one-time signed recovery authorization, but no implementing tool/flow was found in the current source. No live credential or database row was changed, and no production data was seeded or modified.

User-requested isolated/mock-data diagnostic tests were run as supporting evidence only. No test files or assertions were changed.

| Command | Exit | Passed | Failed | Skipped | Provider / data | Completion |
|---|---:|---:|---:|---:|---|---|
| `dotnet test tests/EdgeRetails.UnitTests/EdgeRetails.UnitTests.csproj --configuration Release --no-restore --filter FullyQualifiedName~Phase3PosDraftResponseLossTests` | 0 | 2 | 0 | 0 | Unit-test fakes; no database | PASS |
| `dotnet test tests/EdgeRetails.IntegrationTests/EdgeRetails.IntegrationTests.csproj --configuration Release --no-restore --filter FullyQualifiedName~Phase3PosCatalogApiContractTests` | 0 | 1 | 0 | 0 | `WebApplicationFactory`, in-memory repositories/catalog stub; no PostgreSQL | PASS |
| `dotnet test tests/EdgeRetails.Desktop.PerformanceTests/EdgeRetails.Desktop.PerformanceTests.csproj --configuration Release --no-restore --filter FullyQualifiedName~Phase3DesktopCutoverTests` | 0 | 12 | 0 | 0 | HTTP fakes and test doubles; no database | PASS |

These tests exercise response-loss replay, authenticated bounded catalog reads, Desktop API/session forwarding, permission visibility, and safe error presentation. They are not evidence that the installed authenticated POS UI is responsive and are not credited as Phase 3D regression gates. The user's POS report therefore remains unverified until Phase 3C authentication succeeds. Current installed release does not provide an approved PIN reset. Continue only through an approved recovery process or the direct user-authentication step; do not bypass login or modify the operational identity/database.
