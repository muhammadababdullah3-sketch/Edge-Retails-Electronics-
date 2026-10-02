# Installed login failure and latency — defect report

Date: 2026-10-02. Installed Desktop 1.0.11 candidate22. Operational credentials/PINs were not collected or entered by the agent.

## Reported and observed behavior

The user entered their PIN in the normal installed Desktop. Sign-in did not succeed and appeared unusually slow. A subsequent screen inspection confirmed the timeout error. The long error text is horizontally clipped in the sign-in card, making the message difficult to understand. Authentication and normal POS use remain unverified.

| Read-only measurement | Result |
|---|---|
| GET `127.0.0.1:7150/api/system/ready` | HTTP 200, 5911 ms; Ready, canConnect=true, pending migrations=false, Normal maintenance |
| GET `127.0.0.1:7150/api/system/version` | HTTP 200, 413 ms |
| GET `127.0.0.1:7150/api/auth/accounts` | HTTP 200, 511 ms; account contents suppressed in diagnostics |
| Installed Desktop | PID 3224, installed executable path confirmed |
| User sign-in attempt | Failed; UI displayed timeout category; actual POST-login/session phase timing not yet captured |
| Host memory snapshot | Approximately 6 GB available of 16 GB; snapshot does not establish the cause of latency |

These are single-request observations, not a latency benchmark. Readiness and account enumeration being successful do not prove the authenticated POST-login and session lookup paths succeeded.

## What source inspection proves

- `LoginViewModel.AppendDigit` handles the first three digits locally. Entering the fourth digit automatically starts authentication. During authentication `_isSigningIn` rejects further digits and duplicate sign-in.
- The normal API path performs **POST `/api/auth/login`**, then **GET `/api/auth/session`**. Either stage may fail; the displayed generic timeout does not identify which stage did so.
- Production Desktop's HTTP client timeout is **30 seconds** (`BackendRuntime.CreateFromEnvironment`). The busy state can therefore remain visible while a request waits up to that limit. This explains the possible loading duration; it does not prove why the installed request exceeded the limit.
- The installed timeout text directs the user to check an operation status, which is inappropriate for the sign-in surface; it also exceeds the available single-line helper layout.
- `LoginView.xaml` helper text has centered horizontal alignment without wrapping or a width bound. Screen evidence confirms clipping of the timeout message.
- Canonical PIN hashing uses PBKDF2-SHA256, 210,000 iterations for new credentials. There is no evidence yet that credential hashing caused this timeout; credential parameters and server wait diagnostics must be checked without exposing hashes, salts or PINs.

## Status and required correction

| Defect | Status | Required proof |
|---|---|---|
| Installed authenticated login timeout | OPEN; user report and visible timeout confirmed | Safe server/DB wait evidence and bounded stage timings, then actual human sign-in |
| Loading duration/responsiveness | OPEN | Isolate API wait versus UI rendering/host contention; controlled latency/busy-state coverage and installed measurement |
| Clipped/inappropriate timeout message | PROVEN UI defect; source correction pending serialized build window | Wrapped complete operation-specific sign-in message, Desktop layout/error tests and installed visual verification |

Do not declare the Server unavailable solely because sign-in timed out. Do not increase HTTP timeouts, lower PBKDF2 work factors, bypass authentication/readiness, introduce direct DB fallback, or claim this fixed by a cosmetic spinner change.

No installed files, service configuration, database credentials or operational account state were changed during this investigation. Existing source remediation and protected gates continue. This report must be updated with terminal diagnostic results and the eventual installed verification.
