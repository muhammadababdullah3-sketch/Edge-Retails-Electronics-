# Canonical workspace (Desktop)

**Single git repo, one primary folder for daily work:**

`C:\Users\muham\OneDrive\Desktop\Point of Sale`

## Product lines in this repo

| Line | What it is | Build entry |
|------|------------|-------------|
| **Shop POS** | Offline desktop + local shop server + worker + recovery | `EdgeRetails.sln` |
| **Central** | Cloud license, activation, payments, admin portal (separate deploy) | `EdgeRetails.Central.slnx` or `src/EdgeRetails.Central.Api/` |

Shop POS git branch (historical name): `feat/phase11-subledger-implementation`.  
Monorepo integration branch: `integrate/central-shop-monorepo`.

| Path | Purpose |
|------|---------|
| `src/EdgeRetails.*` | Shop POS stack |
| `src/EdgeRetails.Central.*` | Central control plane |
| `src/EdgeRetails.ProtectedSigner/` | Central signing support |
| `tests/` | Shop + Central test suites |
| `scripts/central/` | Central recovery / rehearsal |
| `docs/central/` | Central runbooks and architecture |
| `contracts/central/` | Central API route contracts |
| `governance/10-execution-runs/` | Execution evidence |

**Not this repo:** `Edge Retails Goverence Body` on Desktop is a separate git project.

## Worktree policy

- Use **only** `Desktop\Point of Sale` for Cursor and daily commits.
- Retire extra worktrees (`Desktop\Point of Sale Server`, `.codex\worktrees\...`) after monorepo merge — they are the same `.git`, not a second project.
- Do **not** use `origin/codex/central-one` for integration (stale merge attempt).

## Runtime data (outside repo)

| Location | Role |
|----------|------|
| `C:\ProgramData\EdgeRetails` | Machine-wide install state |
| `%LOCALAPPDATA%\EdgeRetails` | Desktop client state / intents |
| `C:\Users\muham\EdgeRetailsDisposable` | Disposable Postgres rehearsals |

## Clean build

```powershell
# Shop POS
dotnet build EdgeRetails.sln -c Release

# Central API
dotnet build src/EdgeRetails.Central.Api/EdgeRetails.Central.Api.csproj -c Release
```

Central deployment env vars: see [docs/central/SERVER_DEPLOYMENT_RUNBOOK.md](central/SERVER_DEPLOYMENT_RUNBOOK.md).
