# Central (control plane) — start here

Central lives in the **same monorepo** as Shop POS (`Desktop\Point of Sale`). It is a **separate deployable** (hosted API + portal), not part of the shop MSI installer.

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

- Shops run **offline** on local server + PostgreSQL.
- Central handles **license, activation, payment review, expiry policy** when online.
- HTTP integration from shop to Central is a **follow-on epic** (not required for monorepo merge).
