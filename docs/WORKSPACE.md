# Canonical workspace (Desktop)

**Single working tree:** open and build only this folder:

`C:\Users\muham\OneDrive\Desktop\Point of Sale`

| Path | Purpose |
|------|---------|
| `src/` | Application code (Desktop, Server, Domain, …) |
| `tests/` | Automated tests |
| `scripts/` | Ops, rehearsals, one-off delivery generators (`scripts/governance-delivery/`) |
| `docs/` | Architecture, governance prose, runbooks |
| `governance/10-execution-runs/` | Execution evidence (markdown/json; not duplicate `src/`) |
| `EdgeRetails.sln` | Build entry point |

**Branch:** `feat/phase11-subledger-implementation` (shop POS line).

**Not this repo:** `Edge Retails Goverence Body` on Desktop is a separate git project (boards/evidence archive).

## Worktree policy

- Do **not** create agent worktrees under `.claude/worktrees/` (gitignored).
- `git worktree list` should show **only** this Desktop path for daily work.
- Arch / central-server lines live on **remote branches** (`codex/*`); check out elsewhere only when needed.

## Runtime data (outside repo)

| Location | Role |
|----------|------|
| `C:\ProgramData\EdgeRetails` | Machine-wide install state |
| `%LOCALAPPDATA%\EdgeRetails` | Desktop client state / intents |
| `C:\Users\muham\EdgeRetailsDisposable` | Disposable Postgres rehearsals (safe to prune old `ER-RUN-*` folders) |

## Clean build

```powershell
dotnet build EdgeRetails.sln -c Release
```
