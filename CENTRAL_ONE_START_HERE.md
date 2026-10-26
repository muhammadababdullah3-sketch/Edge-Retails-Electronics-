# Central (cloud server / control plane) — start here

In product language, **this is the real server**: hosted API + admin portal + license/commercial authority.

Central lives in the **same monorepo** as Shop POS (`Desktop\Point of Sale`). It is a **separate deployable**, not part of the shop MSI installer.

Do **not** confuse with **`EdgeRetails.Server`** — that is the **client shop service** on the retailer PC (loopback 7150), not the cloud. See [docs/CLIENT_AND_CLOUD_TERMINOLOGY.md](docs/CLIENT_AND_CLOUD_TERMINOLOGY.md).

## Read first

- [docs/central/ARCHITECTURE.md](docs/central/ARCHITECTURE.md) — boundaries, waves, security
- [docs/central/SERVER_DEPLOYMENT_RUNBOOK.md](docs/central/SERVER_DEPLOYMENT_RUNBOOK.md) — env vars and host setup
- [docs/central/CODE_FIRST_SERVER_AUDIT.md](docs/central/CODE_FIRST_SERVER_AUDIT.md) — code-traced pipeline, env matrix, shop boundary
- [docs/WORKSPACE.md](docs/WORKSPACE.md) — one folder policy, build commands

## Build and test

```powershell
dotnet build EdgeRetails.Central.slnx -c Release
dotnet test tests/EdgeRetails.Central.UnitTests -c Release
```

Integration tests require owned PostgreSQL per `docs/central/`.

## Local development (integrated)

1. First time only: `scripts/central/Initialize-CentralLocalDisposable.ps1` (creates owned PG18 on port **55448**, writes `local/central-dev.env`, applies migrations). Or copy `local/central-dev.env.example` → `local/central-dev.env` if you already have an attested cluster.
2. `scripts/central/Start-CentralLocalDev.ps1` — validates env via `EdgeRetails.Central.DevTools`, then runs the API (`ASPNETCORE_ENVIRONMENT=Development` loads `local/central-dev.env` automatically).
3. Portal admin (idempotent): `Initialize` runs `sync-portal-admin` automatically. Defaults are in `local/central-dev.env.example` (`CENTRAL_DEV_PORTAL_USERNAME` / `CENTRAL_DEV_PORTAL_PASSWORD`). Record + MFA secret: `local/central-dev.portal.json` (gitignored). Re-sync anytime: `scripts/central/Provision-CentralDevAdmin.ps1`.
4. Portal: `https://localhost:5001` — Development shows the server sign-in panel. On localhost only, **Skip password** is available: username, then current authenticator code (password is not checked). Production and non-local hosts always require the password. HTTPS is required for API routes.

## Solution file

`EdgeRetails.Central.slnx` — Central projects only (optional; API project build is enough for most work).

## Shop POS relationship

- Shops run **offline-capable** POS: Desktop → **shop local host** (`EdgeRetails.Server`) → shop PostgreSQL.
- **Central (cloud server)** holds **admin credentials, signing, activation, payment review, expiry policy**.
- Shop verifies **signed** entitlements locally (public key); it does not hold vendor private keys.
- Desktop **cloud activation** calls Central when `EDGE_RETAILS_CENTRAL_BASE_URL` is set (`ShopCentralActivationService`). Broader shop-server ↔ Central sync remains limited; see [docs/central/CODE_FIRST_SERVER_AUDIT.md](docs/central/CODE_FIRST_SERVER_AUDIT.md).
