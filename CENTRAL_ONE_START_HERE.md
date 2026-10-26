# Central (cloud server / control plane) — start here

In product language, **this is the real server**: hosted API + admin portal + license/commercial authority.

Central lives in the **same monorepo** as Shop POS (`Desktop\Point of Sale`). It is a **separate deployable**, not part of the shop MSI installer.

Do **not** confuse with **`EdgeRetails.Server`** — that is the **client shop service** on the retailer PC (loopback 7150), not the cloud. See [docs/CLIENT_AND_CLOUD_TERMINOLOGY.md](docs/CLIENT_AND_CLOUD_TERMINOLOGY.md).

## Read first

- [docs/central/ARCHITECTURE.md](docs/central/ARCHITECTURE.md) — boundaries, waves, security
- [docs/central/SERVER_DEPLOYMENT_RUNBOOK.md](docs/central/SERVER_DEPLOYMENT_RUNBOOK.md) — env vars and host setup
- [docs/WORKSPACE.md](docs/WORKSPACE.md) — one folder policy, build commands

## Build and test

```powershell
dotnet build src/EdgeRetails.Central.Api/EdgeRetails.Central.Api.csproj -c Release
dotnet test tests/EdgeRetails.Central.UnitTests -c Release
```

Integration tests require owned PostgreSQL per `docs/central/`.

## Solution file

`EdgeRetails.Central.slnx` — Central projects only (optional; API project build is enough for most work).

## Shop POS relationship

- Shops run **offline-capable** POS: Desktop → **shop local host** (`EdgeRetails.Server`) → shop PostgreSQL.
- **Central (cloud server)** holds **admin credentials, signing, activation, payment review, expiry policy**.
- Shop verifies **signed** entitlements locally (public key); it does not hold vendor private keys.
- HTTP from shop Desktop/local host to Central is **not wired in shop code yet** — follow-on epic; monorepo merge only colocates sources.
