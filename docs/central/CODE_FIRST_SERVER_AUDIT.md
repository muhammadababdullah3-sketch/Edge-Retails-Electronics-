# Code-first server audit (Central + Shop)

Generated from source under `src/` and `tests/` only. Runbook/docs used as a name checklist, not as truth.

## Central.Api — request pipeline

| Order | Component | Effect on API traffic |
|------|-----------|------------------------|
| 1 | `CentralExceptionHandlingMiddleware` | Maps `CentralDomainException` to HTTP; unhandled → 500 `INTERNAL_SERVER_ERROR` (no message leak) |
| 2 | `W1DenialAuditMiddleware` | Audits 4xx on `/api` and `/internal`; audit DB failures swallowed |
| 3 | `RateLimitingMiddleware` | POST login/MFA/step-up + licensing bootstrap/register/redeem → 503 if rate env missing; 429 if over limit |
| 4 | `CsrfProtectionMiddleware` | `/api` + `/internal` require HTTPS (`HTTPS_REQUIRED` on HTTP) |
| 5 | `AnonymousPortalSessionGateMiddleware` | GET `/api/admin/v1/portal/session` without cookie → **401 `SESSION_REQUIRED` without MVC/DbContext** |
| 6 | Static files + routing | Portal UI under `wwwroot` |
| 7 | `CentralRequestAuthorizationFilter` | Fail-closed capabilities; anonymous exceptions below |

### Anonymous / proof bypass (filter)

| Method | Path | DB on success path |
|--------|------|-------------------|
| POST | `/api/admin/v1/auth/login` | Yes (`StartLoginAsync`) |
| POST | `/api/admin/v1/auth/mfa` | Yes |
| POST | `/api/licensing/v1/bootstrap/challenge` | Yes |
| POST | `/api/activation/v1/challenge` | Yes |
| POST | `/api/licensing/v1/primary/register`, `/api/licensing/v1/activation/redeem` | Yes (bootstrap proof in controller) |
| GET | backup grant content route | Bearer validated in controller |
| * | `/api/licensing/v1/*` installation ops | Installation asymmetric proof (`InstallationRequestProofService`) |

### Portal session (authenticated)

1. Valid `central_session` cookie → filter validates session in DB.
2. GET `/api/admin/v1/portal/session` — **no capability check** (bootstrap context).
3. Other portal routes use `ResolveRequiredPermission` (e.g. `sessions.view`, `dashboard.view`).

### Status codes (symptoms)

| Symptom | Code | Layer |
|---------|------|--------|
| No cookie, session probe | 401 `SESSION_REQUIRED` | Gate or filter |
| Rate limit env missing | 503 `RATE_LIMIT_CONFIGURATION_REQUIRED` | Rate middleware |
| HTTP not TLS | 400 `HTTPS_REQUIRED` | CSRF middleware |
| Bad login | 401 `INVALID_CREDENTIALS` | Domain exception |
| Auth TTL env missing | 400 `AUTH_POLICY_NOT_CONFIGURED` | `AdminAuthenticationPolicy.EnsureConfigured()` (not 503) |
| DB/guard misconfig | 500 `INTERNAL_SERVER_ERROR` or startup throw on DbContext | `DatabaseTargetGuard` |

---

## Central — `CENTRAL_*` environment matrix

| Variable(s) | When evaluated | If missing/invalid |
|-------------|----------------|-------------------|
| `CENTRAL_SIGNER_KEY_STORE_ROOT` | Process start (`Program.cs`) | Startup throw |
| `CENTRAL_DATABASE_CONNECTION_STRING` | First `CentralDbContext` | Throw |
| `CENTRAL_PG_PORT`, `CENTRAL_PG_DATABASE`, `CENTRAL_PG_ROLE`, `CENTRAL_PG_MIGRATION_ROLE`, `CENTRAL_PG_PID`, `CENTRAL_PG_BINARY`, `CENTRAL_PG_OWNERSHIP_MARKER`, `CENTRAL_PG_DATA_DIR` | Connection string validation | `DatabaseTargetGuardException` |
| `CENTRAL_ADMIN_AUTH_*` (4) + `CENTRAL_ADMIN_STEP_UP_TTL_SECONDS` | Login/MFA/step-up | `AUTH_POLICY_NOT_CONFIGURED` |
| `CENTRAL_AUTH_RATE_LIMIT_MAX_ATTEMPTS`, `CENTRAL_AUTH_RATE_LIMIT_WINDOW_SECONDS` | Throttled POSTs | 503 |
| `CENTRAL_INSTALLATION_PROOF_WINDOW_SECONDS` | Installation proof | Proof rejected |
| `CENTRAL_BOOTSTRAP_CHALLENGE_TTL_SECONDS` | Bootstrap challenge | Unavailable |
| `CENTRAL_DUAL_APPROVAL_ACTIONS` | Dual-approval routes | `SECURITY_POLICY_REQUIRED` |
| `CENTRAL_INTERNAL_SIGNER_TOKEN` | `/internal/v1/sign` | Rejected in controller |
| `CENTRAL_SENSITIVE_APPROVAL_TTL_SECONDS` | Sensitive approvals | Workflow gated |
| `CENTRAL_OBJECT_STORAGE_ROOT`, `CENTRAL_PAYMENT_STORAGE_ROOT` | Storage providers | Throw on use |
| `CENTRAL_STORAGE_RETENTION_*`, upload limits, `CENTRAL_UPLOAD_*` | Evidence upload init | Unavailable |
| `CENTRAL_BACKUP_DOWNLOAD_GRANT_TTL_SECONDS` | Backup grants | Unavailable |
| `CENTRAL_ACTIVATION_*`, delivery keys JSON | Token issuance | Domain errors |
| `CENTRAL_OUTBOX_*` | Outbox worker + `IOutboxDispatcher` | Worker warns if disabled; dispatcher uses `ContractHarness` policy when outbox disabled (`AddCentralOutboxDispatch`) |
| `CENTRAL_STORAGE_RECONCILIATION_*` | Storage hosted service | Worker idle + warning |
| `CENTRAL_ACTIVATION_DELIVERY_CLEANUP_*` | Cleanup hosted service | Worker idle + warning |

Synthetic fixtures for integration tests only: `tests/EdgeRetails.Central.IntegrationTests/CentralIntegrationTestEnvironment.cs`.

### Admin bootstrap (code)

- No seed in EF migrations.
- `AdminAuthService` matches `admin_users.username` with **trim + lowercase**.
- MFA required (`MfaEnabled` + secret) before session cookie.
- First admin: direct DB insert (see integration `CreateAdminAsync`) or authenticated `AdminUsersController` commands — **no anonymous admin-create API**.

---

## Central — controller groups (capability sample)

| Group | Route prefix | Sample permission |
|-------|--------------|-------------------|
| Auth | `api/admin/v1/auth` | login/MFA anonymous; step-up needs session |
| Portal / dashboard | `api/admin/v1/portal`, `dashboard` | `dashboard.view`, `sessions.view` |
| Activations | `api/admin/v1/activations`, `activation-tokens` | `activation.review`, `activation.issue` |
| Commercial | `api/commercial/v1`, payments, orders | `payment.review`, `order.create` |
| Licensing (admin) | `api/admin/v1/licenses` | `license.*` |
| Licensing (edge) | `api/licensing/v1` | installation proof / bootstrap anonymous paths |
| Directory | `api/admin/v1` shops/customers | `shop.view`, `customer.create` |
| Storage / evidence | `evidence/uploads`, backups | `evidence.upload.init`, `backup.download` |
| Support | `api/support/v1`, admin support commands | `support.*` |
| Internal signer | `internal/v1` | token + no browser Origin |
| Operations / audit | `api/admin/v1/operations`, audit | `operation.admin.lookup`, `audit.read` |

Authoritative mapping: `CentralRequestAuthorizationFilter.ResolveRequiredPermission`.

---

## Shop (`EdgeRetails.Server`)

### Startup (`ProductionStartupCoordinator`)

Order: **License** → **Database** → **Migrations** → **Disk** (optional) → **Maintenance** → **Setup** → **Session recovery hint**.

- `StartupDisposition.Blocked` → `Program` **throws** (includes `license.invalid_signature`).
- `LicenseRequired` / `SetupRequired` → non-throwing dispositions (server still starts; business routes gated elsewhere).

Config: `%ProgramData%\EdgeRetails\config.json`; dev adds `%LocalAppData%\EdgeRetails\config.json`. DB: `EDGE_RETAILS_DB` / connection strings. Default URL: `http://127.0.0.1:7150`.

### Middleware order

`DomainException` → `ProtocolCompatibility` → `SingleHostDrainGuard` → `MaintenanceModeGuard` → `TerminalAuthentication` → `UserSessionAuthentication`.

Staff auth is **PIN/session on shop** — not Central admin.

### Central coupling

- `ShopCentralActivationService` + `EDGE_RETAILS_CENTRAL_BASE_URL` (HTTPS).
- Desktop `FirstSetupViewModel` uses `TryCreateFromEnvironment`; calls `api/licensing/v1/bootstrap/challenge` and register flow.
- Shop server startup does **not** require Central URL; activation is optional at desktop layer.

---

## Doc drift (fixed or listed)

| Doc | Claim | Code truth |
|-----|-------|------------|
| `docs/SHOP_AND_CENTRAL_RUNTIME.md` | Shop has no Central HTTP client | **Stale** — `ShopCentralActivationService` when env set |
| `CENTRAL_ONE_START_HERE.md` | HTTP to Central not wired | **Partial** — Desktop activation wired; not all shop API paths |

---

## Prioritized fixes (implementation status)

| Priority | Item | Status |
|----------|------|--------|
| P0 | Remove `AgentDebugSession` instrumentation | Done in tree |
| P0 | Unit test anonymous portal session gate | `AnonymousPortalSessionGateMiddlewareTests` |
| P1 | `CentralLocalEnvironmentLoader` + `CentralOperationalEnvironment` fail-fast + DevTools + start/provision scripts | Integrated |
| P2 | Admin bootstrap | Documented above; use DB or integration pattern |
| P3 | Shop dev license | Use valid signed `.erlic` or Testing environment |

---

## Smoke checklist

After attested PG18 + env (see `scripts/central/Start-CentralLocalDev.ps1`):

```powershell
curl.exe -k -s -o NUL -w "%{http_code}" https://localhost:5001/api/admin/v1/portal/session
# Expect 401

curl.exe -k -s -X POST https://localhost:5001/api/admin/v1/auth/login -H "Content-Type: application/json" -d "{\"username\":\"x\",\"passwordOrProviderAssertion\":\"y\"}"
# Expect 401 INVALID_CREDENTIALS or 400 AUTH_POLICY_NOT_CONFIGURED — not 503 if rate limit env set
```

### Observed smoke (local, 2026-10-26)

| Request | Result |
|---------|--------|
| `GET /api/admin/v1/portal/session` | **401** `SESSION_REQUIRED` (gate working) |
| `POST /api/admin/v1/auth/login` | **503** `RATE_LIMIT_CONFIGURATION_REQUIRED` (Central process up; set `CENTRAL_AUTH_RATE_LIMIT_*` via `local/central-dev.env.ps1`) |

Re-run after `scripts/central/Start-CentralLocalDev.ps1` with a complete env file.
